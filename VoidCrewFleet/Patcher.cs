using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        /// <summary>An optional patch only logs a warning when it is missing; the feature stays on.</summary>
        public bool Optional { get; set; }
    }

    /// <summary>
    /// Applies every [GamePatch] in this assembly. Each target is patched on its own, so a game update that renames
    /// something only switches off the feature that needs it, never the game. PatchProcessor is used because it behaves
    /// the same in every Harmony 2 version BepInEx installs ship.
    /// </summary>
    internal static class Patcher
    {
        private static readonly Dictionary<string, List<string>> Missing = new Dictionary<string, List<string>>();
        private static readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

        /// <summary>True when every required patch of the feature is in (and the feature has at least one).</summary>
        public static bool IsReady(string feature)
        {
            int count;
            return Applied.TryGetValue(feature, out count) && count > 0 && !Missing.ContainsKey(feature);
        }

        public static void ApplyAll(Harmony harmony)
        {
            foreach (Type host in typeof(Patcher).Assembly.GetTypes())
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
                    foreach (GamePatchAttribute patch in attributes.Cast<GamePatchAttribute>()) Apply(harmony, patch, hook);
                }
            }
        }

        private static void Apply(Harmony harmony, GamePatchAttribute patch, MethodInfo hook)
        {
            string label = patch.Type + "." + patch.Method;
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
                Log.Verbose("Patched " + label + " (" + patch.Feature + ", " + hook.DeclaringType.Name + "." + hook.Name + ").");
            }
            catch (Exception e)
            {
                if (patch.Optional)
                {
                    Log.Warn("Optional patch " + label + " (" + patch.Feature + ") is not in; the game may have changed: " + e.Message);
                    return;
                }
                List<string> missing;
                if (!Missing.TryGetValue(patch.Feature, out missing)) Missing[patch.Feature] = missing = new List<string>();
                missing.Add(label);
                Log.Error("Could not patch " + label + " for " + patch.Feature + " (the game may have changed): " + e.Message);
            }
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

        /// <summary>One line per feature: on, or off with the game members that could not be patched.</summary>
        public static void Report(IEnumerable<string> features)
        {
            foreach (string feature in features)
            {
                List<string> missing;
                if (Missing.TryGetValue(feature, out missing))
                {
                    Log.Error(feature + " is turned off; these game members could not be patched: " + string.Join("; ", missing.ToArray()) + ".");
                }
                else if (!Applied.ContainsKey(feature))
                {
                    Log.Error(feature + " is turned off; none of its patches were found.");
                }
                else
                {
                    Log.Info(feature + ": ready (" + Applied[feature] + " patches).");
                }
            }
        }
    }
}
