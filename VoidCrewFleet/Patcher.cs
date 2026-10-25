using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace VoidCrewFleet
{
    internal enum PatchKind
    {
        Prefix,
        Postfix,
        Finalizer,
        Transpiler
    }

    /// <summary>
    /// Marks a static method of this plugin as a Harmony patch on a game method, looked up by name. The offline checker
    /// (tests\OfflineChecks) reads these attributes from the built DLL and verifies every target, overload and injected
    /// parameter against the game's assemblies, so the list of patches can't drift from the code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class GamePatchAttribute : Attribute
    {
        public GamePatchAttribute(string feature, string type, string method, PatchKind kind)
        {
            Feature = feature;
            Type = type;
            Method = method;
            Kind = kind;
        }

        public string Feature { get; private set; }
        public string Type { get; private set; }
        public string Method { get; private set; }
        public PatchKind Kind { get; private set; }

        /// <summary>Full type names of the target's parameters, when the method has overloads.</summary>
        public string[] Arguments { get; set; }

        /// <summary>
        /// The patch goes on the body of the coroutine the named method starts: the MoveNext of the class the compiler makes
        /// for it. That class is named after the method's place in its class (Start becomes &lt;Start&gt;d__20), so a game update
        /// that adds a method renames it; it is found from the method instead, which names it ([IteratorStateMachine]).
        /// </summary>
        public bool Coroutine { get; set; }

        /// <summary>
        /// The patch goes on a lambda written in the named method: its number among that method's lambdas, 0 for the first.
        /// The compiler's name for it also holds the method's place in its class (&lt;Start&gt;b__12_0, or &lt;DoPrintProcess&gt;b__1 in
        /// a closure class &lt;&gt;c__DisplayClass47_0), which a game update that adds a method shifts; only the method's name and
        /// the lambda's own number are used to find it. -1: not a lambda.
        /// </summary>
        public int Lambda { get; set; } = -1;

        /// <summary>
        /// An optional patch only logs a warning when it is missing; the feature stays on (unless a required patch of it is
        /// missing too), and its start-up report line names the parts it runs without.
        /// </summary>
        public bool Optional { get; set; }

        /// <summary>
        /// Patched once the game has loaded (Patcher.ApplyLater) instead of at start-up: for a class whose first use starts
        /// one of the game's systems, which must not start before the game starts it itself (Photon's PhotonNetwork). Such a
        /// patch should also be Optional: it goes in after the start-up report, so a required one that fails switches its
        /// feature off only then (with a report line of its own).
        /// </summary>
        public bool AfterGameLoad { get; set; }
    }

    /// <summary>
    /// Applies every [GamePatch] in this assembly. Each target is patched on its own, so a game update that renames
    /// something only switches off the feature that needs it, never the game. A feature that misses a required patch is
    /// switched off whole: the patches it did get are taken out again, so it never runs half-patched. A feature that only
    /// misses optional patches, or whose transpiler no longer finds what it changes in a game method, runs without those
    /// parts, and its start-up report line names them. Every patch method is compiled before it goes in (Prepare), so one
    /// that names a game member a game update changed counts as missing too. PatchProcessor is used because it behaves the
    /// same in every Harmony 2 version BepInEx installs ship.
    /// </summary>
    internal static class Patcher
    {
        private static readonly Dictionary<string, List<string>> Missing = new Dictionary<string, List<string>>();
        private static readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

        /// <summary>
        /// Per feature that is on: the parts it runs without (optional patches that are not in, game methods its transpilers no
        /// longer find anything to change in), for its report line.
        /// </summary>
        private static readonly Dictionary<string, List<string>> Degraded = new Dictionary<string, List<string>>();

        /// <summary>Each patch method compiled so far: null when it compiled, else why it could not be.</summary>
        private static readonly Dictionary<MethodInfo, Exception> Prepared = new Dictionary<MethodInfo, Exception>();

        private static long _prepareTicks;

        /// <summary>Every patch that went in, per feature, so a feature that is switched off can be taken out again.</summary>
        private static readonly Dictionary<string, List<AppliedPatch>> Patched = new Dictionary<string, List<AppliedPatch>>();

        /// <summary>Per switched-off feature: how many of the patches it did get were taken out again.</summary>
        private static readonly Dictionary<string, int> Withdrawn = new Dictionary<string, int>();

        /// <summary>One patch that went in: the game method, the plugin's hook on it, and the attribute's name for it.</summary>
        private struct AppliedPatch
        {
            public MethodBase Target;
            public MethodInfo Hook;
            public string Label;
            public PatchKind Kind;
        }

        /// <summary>True when every required patch of the feature is in (and the feature has at least one).</summary>
        public static bool IsReady(string feature)
        {
            int count;
            return Applied.TryGetValue(feature, out count) && count > 0 && !Missing.ContainsKey(feature);
        }

        private static Harmony _harmony;

        /// <summary>The plugin's Harmony instance (null before ApplyAll).</summary>
        public static Harmony Instance
        {
            get { return _harmony; }
        }

        /// <summary>Every patch method that went in, prefixes, postfixes and finalizers, once each (for the developer profiler).</summary>
        public static List<MethodInfo> AppliedHooks()
        {
            return Patched.Values.SelectMany(list => list).Where(p => p.Kind != PatchKind.Transpiler).Select(p => p.Hook).Distinct().ToList();
        }
        private static readonly List<KeyValuePair<GamePatchAttribute, MethodInfo>> Later = new List<KeyValuePair<GamePatchAttribute, MethodInfo>>();

        public static void ApplyAll(Harmony harmony)
        {
            _harmony = harmony;
            Type[] types;
            try
            {
                types = typeof(Patcher).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                Log.Warn("Some of the plugin's types couldn't load (" + (e.LoaderExceptions.Length > 0 ? e.LoaderExceptions[0].Message : "unknown") +
                         "); the features in them are off.");
                types = Array.FindAll(e.Types, t => t != null);
            }
            foreach (Type host in types)
            {
                foreach (MethodInfo hook in host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object[] attributes;
                    try
                    {
                        attributes = hook.GetCustomAttributes(typeof(GamePatchAttribute), false);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Could not read the patch list of " + host.Name + "." + hook.Name + ": " + e.Message);
                        continue;
                    }
                    foreach (GamePatchAttribute patch in attributes.Cast<GamePatchAttribute>())
                    {
                        if (patch.AfterGameLoad) Later.Add(new KeyValuePair<GamePatchAttribute, MethodInfo>(patch, hook));
                        else Apply(harmony, patch, hook);
                    }
                }
            }
            foreach (string feature in Missing.Keys.ToList()) Withdraw(harmony, feature);
            Log.Info("Set up " + Prepared.Keys.Select(hook => hook.DeclaringType).Distinct().Count() + " patch classes and compiled their " + Prepared.Count +
                     " patch methods before patching, in " + (_prepareTicks * 1000.0 / Stopwatch.Frequency).ToString("0") + " ms.");
        }

        /// <summary>
        /// Once the game has loaded (FleetRuntime): the patches marked AfterGameLoad, except those of a feature that is off. A
        /// required one that fails switches its feature off as at start-up: the patches it got are taken out again, and the
        /// log says so in a report line of its own (the start-up report has been written by then).
        /// </summary>
        public static void ApplyLater()
        {
            if (_harmony == null || Later.Count == 0) return;
            var offBefore = new HashSet<string>(Missing.Keys);
            foreach (KeyValuePair<GamePatchAttribute, MethodInfo> pair in Later)
            {
                GamePatchAttribute patch = pair.Key;
                // Off at start-up, or one of its required late patches just failed: it gets no more patches.
                if (Missing.ContainsKey(patch.Feature))
                {
                    Log.Verbose("Did not patch " + patch.Type + "." + patch.Method + " (" + patch.Feature + "): the feature is off.");
                    continue;
                }
                Apply(_harmony, patch, pair.Value);
            }
            Later.Clear();
            List<string> nowOff = Missing.Keys.Where(feature => !offBefore.Contains(feature)).ToList();
            foreach (string feature in nowOff) Withdraw(_harmony, feature);
            Report(nowOff);
        }

        private static void Apply(Harmony harmony, GamePatchAttribute patch, MethodInfo hook)
        {
            string label = patch.Type + "." + patch.Method + (patch.Coroutine ? " (coroutine)" : patch.Lambda >= 0 ? " (lambda " + patch.Lambda + ")" : "");
            try
            {
                Type type = AccessTools.TypeByName(patch.Type);
                if (type == null) throw new MissingMemberException("type " + patch.Type + " not found");
                MethodBase target;
                if (patch.Arguments != null)
                {
                    Type[] arguments = patch.Arguments.Select(ResolveArgument).ToArray();
                    target = patch.Method == ".ctor" ? (MethodBase)AccessTools.Constructor(type, arguments) : AccessTools.Method(type, patch.Method, arguments);
                }
                else
                {
                    target = patch.Method == ".ctor" ? OnlyConstructor(type) : AccessTools.Method(type, patch.Method);
                }
                if (target == null) throw new MissingMethodException(patch.Type, patch.Method);
                if (patch.Coroutine) target = CoroutineBody(target);
                else if (patch.Lambda >= 0) target = LambdaIn(target.DeclaringType, patch.Method, patch.Lambda);
                Prepare(hook);

                PatchProcessor processor = harmony.CreateProcessor(target);
                var method = new HarmonyMethod(hook);
                switch (patch.Kind)
                {
                    case PatchKind.Prefix:
                        processor.AddPrefix(method);
                        break;
                    case PatchKind.Postfix:
                        processor.AddPostfix(method);
                        break;
                    case PatchKind.Transpiler:
                        processor.AddTranspiler(method);
                        break;
                    default:
                        processor.AddFinalizer(method);
                        break;
                }
                processor.Patch();
                int count;
                Applied.TryGetValue(patch.Feature, out count);
                Applied[patch.Feature] = count + 1;
                List<AppliedPatch> patched;
                if (!Patched.TryGetValue(patch.Feature, out patched)) Patched[patch.Feature] = patched = new List<AppliedPatch>();
                patched.Add(new AppliedPatch { Target = target, Hook = hook, Label = label, Kind = patch.Kind });
                Log.Verbose("Patched " + label + " (" + patch.Feature + ", " + hook.DeclaringType.Name + "." + hook.Name + ").");
            }
            catch (Exception e)
            {
                if (patch.Optional)
                {
                    Log.Warn("Optional patch " + label + " (" + patch.Feature + ") is not in; the game may have changed: " + e.Message);
                    AddDegraded(patch.Feature, label + " (not patched)");
                    return;
                }
                List<string> missing;
                if (!Missing.TryGetValue(patch.Feature, out missing)) Missing[patch.Feature] = missing = new List<string>();
                missing.Add(label);
                Log.Error("Could not patch " + label + " for " + patch.Feature + " (the game may have changed): " + e.Message);
            }
        }

        /// <summary>
        /// Sets up the patch method's class and compiles the method, inside Apply's try. A patch method (or a static field of
        /// its class) that names a game member a game update renamed or retyped only fails when it is first compiled. Left to
        /// the game, that happens inside the game's patched method, before the patch method's own try/catch, so the game method
        /// would throw on every call (a hook on the hub's Update would stop every mission from starting). Here it fails at
        /// start-up and counts as a missing patch, so its feature is switched off cleanly. The game's Mono compiles a method
        /// when its code address is asked for; RuntimeHelpers.PrepareMethod does nothing there (it compiles on .NET). A generic
        /// method is never compiled this way, as Mono aborts on one (the plugin has none; the offline checker makes sure).
        /// Compiling a method also runs the static set-up of the classes whose static fields it reads; for a patch method those
        /// are the plugin's own (its lookups and lists), never the game's. Each method is compiled once, however many patches
        /// use it.
        /// </summary>
        private static void Prepare(MethodInfo hook)
        {
            Exception failure;
            if (!Prepared.TryGetValue(hook, out failure))
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    // ContainsGenericParameters: a generic method, or one of a generic class.
                    if (!hook.ContainsGenericParameters)
                    {
                        RuntimeHelpers.RunClassConstructor(hook.DeclaringType.TypeHandle);
                        RuntimeHelpers.PrepareMethod(hook.MethodHandle);
                        hook.MethodHandle.GetFunctionPointer();
                    }
                }
                catch (Exception e)
                {
                    failure = new InvalidOperationException("its patch method " + hook.DeclaringType.Name + "." + hook.Name + " can't be compiled (" + Innermost(e) + ")", e);
                }
                _prepareTicks += Stopwatch.GetTimestamp() - start;
                Prepared[hook] = failure;
            }
            if (failure != null) throw failure;
        }

        /// <summary>"MissingFieldException: Field not found: ...": the exception at the bottom of a class set-up or call that failed.</summary>
        private static string Innermost(Exception e)
        {
            while ((e is TypeInitializationException || e is TargetInvocationException) && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>
        /// The body of the coroutine a game method starts: the MoveNext of the class the compiler made for it, which the method
        /// names in its [IteratorStateMachine] (the compiler puts it on every coroutine). That class is nested in the method's.
        /// </summary>
        private static MethodBase CoroutineBody(MethodBase method)
        {
            object[] marks = method.GetCustomAttributes(typeof(IteratorStateMachineAttribute), false);
            Type machine = marks.Length == 1 ? ((IteratorStateMachineAttribute)marks[0]).StateMachineType : null;
            MethodInfo body = machine != null && machine.DeclaringType == method.DeclaringType ? AccessTools.Method(machine, "MoveNext") : null;
            if (body == null) throw new MissingMethodException(method.DeclaringType.FullName + "." + method.Name + " is no longer a coroutine");
            return body;
        }

        /// <summary>
        /// The lambda with that number (0 for the first) written in a game method, on the method's class or in one of the
        /// classes the compiler nests in it for lambdas (&lt;&gt;c, closure classes): the compiler names it &lt;Method&gt;b__N, or
        /// &lt;Method&gt;b__P_N where P is the method's place in its class. Exactly one must match.
        /// </summary>
        private static MethodBase LambdaIn(Type type, string method, int number)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string prefix = "<" + method + ">b__";
            var found = new List<MethodInfo>();
            var types = new List<Type> { type };
            types.AddRange(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (Type candidate in types)
            {
                foreach (MethodInfo lambda in candidate.GetMethods(all))
                {
                    if (lambda.Name.StartsWith(prefix, StringComparison.Ordinal) && LambdaNumber(lambda.Name.Substring(prefix.Length)) == number) found.Add(lambda);
                }
            }
            if (found.Count == 1) return found[0];
            if (found.Count == 0) throw new MissingMethodException(type.FullName + "." + method + " has no lambda " + number);
            throw new AmbiguousMatchException(type.FullName + "." + method + " has " + found.Count + " lambdas numbered " + number);
        }

        /// <summary>The lambda's own number from the end of its compiler name ("1" or "12_1"), or -1.</summary>
        private static int LambdaNumber(string suffix)
        {
            int cut = suffix.LastIndexOf('_');
            int number;
            return int.TryParse(cut >= 0 ? suffix.Substring(cut + 1) : suffix, out number) ? number : -1;
        }

        /// <summary>
        /// A transpiler found nothing to change in a game method (a game update changed the method): its feature runs without
        /// that part, and its start-up report line says so. Logged once per method (Harmony runs a method's transpilers again
        /// whenever anything else patches it).
        /// </summary>
        public static void NothingReplaced(string feature, MethodBase original, string what)
        {
            string method = original.DeclaringType != null ? original.DeclaringType.FullName + "." + original.Name : original.Name;
            if (AddDegraded(feature, method + " (no longer " + what + ")"))
                Log.Warn(feature + ": " + method + " no longer " + what + "; that part is left as the game has it. The game may have changed.");
        }

        /// <summary>Notes a part a feature runs without; false when it was noted already.</summary>
        private static bool AddDegraded(string feature, string part)
        {
            List<string> parts;
            if (!Degraded.TryGetValue(feature, out parts)) Degraded[feature] = parts = new List<string>();
            if (parts.Contains(part)) return false;
            parts.Add(part);
            return true;
        }

        /// <summary>
        /// A feature missed a required patch, so it is off: take out the patches it did get (optional ones included), or its
        /// other hooks would still act without the one that is missing. A patch that can't be taken out stays counted as in.
        /// </summary>
        private static void Withdraw(Harmony harmony, string feature)
        {
            List<AppliedPatch> patched;
            if (!Patched.TryGetValue(feature, out patched) || patched.Count == 0) return;
            var left = new List<AppliedPatch>();
            foreach (AppliedPatch patch in patched)
            {
                try
                {
                    harmony.CreateProcessor(patch.Target).Unpatch(patch.Hook);
                    Log.Verbose("Took out " + patch.Label + " (" + feature + ", " + patch.Hook.DeclaringType.Name + "." + patch.Hook.Name + "): the feature is off.");
                }
                catch (Exception e)
                {
                    left.Add(patch);
                    Log.Error("Could not take out " + patch.Label + " for " + feature + ", which is off, so that part of it may still act: " + e.Message);
                }
            }
            Withdrawn[feature] = patched.Count - left.Count;
            Applied[feature] = left.Count;
            Patched[feature] = left;
        }

        /// <summary>
        /// The type's own instance constructor when it has exactly one. (AccessTools.Constructor without parameters looks
        /// for a parameterless one and goes up to the base types, so it can return object's constructor.)
        /// </summary>
        private static MethodBase OnlyConstructor(Type type)
        {
            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (constructors.Length != 1)
                throw new AmbiguousMatchException(type.FullName + " has " + constructors.Length + " constructors; name the arguments of the one to patch");
            return constructors[0];
        }

        private static Type ResolveArgument(string name)
        {
            bool byRef = name.EndsWith("&", StringComparison.Ordinal);
            string plain = byRef ? name.Substring(0, name.Length - 1) : name;
            Type type = AccessTools.TypeByName(plain);
            if (type == null) throw new MissingMemberException("parameter type " + plain + " not found");
            return byRef ? type.MakeByRefType() : type;
        }

        /// <summary>
        /// One line per feature: on (with the parts it runs without, if any), or off with the game members that could not be
        /// patched (and what became of the patches it did get).
        /// </summary>
        public static void Report(IEnumerable<string> features)
        {
            foreach (string feature in features)
            {
                List<string> missing;
                if (Missing.TryGetValue(feature, out missing))
                {
                    int withdrawn, left;
                    Withdrawn.TryGetValue(feature, out withdrawn);
                    Applied.TryGetValue(feature, out left);
                    string others = withdrawn > 0 ? " Its other " + withdrawn + (withdrawn == 1 ? " patch was" : " patches were") + " taken out again." : "";
                    if (left > 0) others += " " + left + (left == 1 ? " patch" : " patches") + " could not be taken out, so parts of it may still act.";
                    Log.Error(feature + " is turned off; these game members could not be patched: " + string.Join("; ", missing.ToArray()) + "." + others);
                }
                else if (!Applied.ContainsKey(feature))
                {
                    Log.Error(feature + " is turned off; none of its patches were found.");
                }
                else
                {
                    List<string> parts;
                    if (Degraded.TryGetValue(feature, out parts) && parts.Count > 0)
                        Log.Warn(feature + ": ready (" + Applied[feature] + " patches), but without " + parts.Count + (parts.Count == 1 ? " part" : " parts") +
                                 " the game may have changed (left as the game has " + (parts.Count == 1 ? "it" : "them") + "): " + string.Join("; ", parts.ToArray()) + ".");
                    else
                        Log.Info(feature + ": ready (" + Applied[feature] + " patches).");
                }
            }
        }
    }
}
