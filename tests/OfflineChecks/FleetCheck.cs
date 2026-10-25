using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline checks for Void Crew Fleet (no game running). Everything is read with Mono.Cecil, nothing is executed:
//  1. every [GamePatch] in the plugin names a type and method that exist in the game's assemblies, with exactly one
//     method of that name unless the attribute lists the argument types, and every injected parameter of the patch
//     method (__instance, __result, ___field, argument names) fits that target, its type and ref-ness included (Harmony
//     doesn't check most of them: a wrong one makes bad code at run time); a patch on a coroutine finds the coroutine's
//     body from the method ([IteratorStateMachine]), a patch on a lambda exactly one lambda with that number; every patch
//     method is static and not generic (the plugin compiles each one at start-up, Patcher.Prepare); a patch that waits for
//     the game to load is optional; a transpiler's target still has what it replaces (a call of, or read of, one of the game
//     members its code looks up by name);
//  2. every GameMember.Type/Field/Method/Property call in the plugin passes constant names that exist in the game;
//  3. every game member a patch method's code names, and every one its class's static fields and static set-up name,
//     exists in the game: what the start-up compile would refuse. A build against the game already ensures it; this
//     shows it for a built plugin against another game version (FleetCheck.exe <old dll> <new game's Managed>);
//  4. every lookup by name of a game type's member outside GameMember (AccessTools, Type.GetMethod and the like) names
//     one that exists (exactly once for a method or property without argument types), and none is in a static set-up,
//     where a lookup that throws (an overload a game update adds) would break every hook of its class. Lookups on a type
//     only known at run time are listed as notes;
//  5. the features: every feature with a patch applied at start-up is in the start-up report (Features.All), every
//     feature there has one, and every Feature constant names a feature that has a patch.
// Run it with tests\build-and-test.ps1, which builds the plugin first.
// Usage: FleetCheck.exe <plugin dll> <game Managed dir>
internal static class Program
{
    private const string AttributeName = "VoidCrewFleet.GamePatchAttribute";
    private const string MemberHelper = "VoidCrewFleet.GameMember";
    private const string PatcherClass = "VoidCrewFleet.Patcher";
    private const string FeaturesClass = "VoidCrewFleet.Features";

    private static readonly Dictionary<string, TypeDefinition> Types = new Dictionary<string, TypeDefinition>();

    /// <summary>The names of the assemblies in the game's Managed folder (the game's, Unity's, the class libraries).</summary>
    private static readonly HashSet<string> GameAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every patch method, once, for check 3.</summary>
    private static readonly List<MethodDefinition> Hooks = new List<MethodDefinition>();

    /// <summary>Per feature: how many of its patches go in at start-up (not AfterGameLoad), for check 5.</summary>
    private static readonly Dictionary<string, int> StartupPatches = new Dictionary<string, int>();

    /// <summary>The features with a patch of any kind, for check 5.</summary>
    private static readonly HashSet<string> PatchedFeatures = new HashSet<string>();

    private static int _failures;
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: FleetCheck.exe <plugin dll> <game Managed dir>");
            return 2;
        }
        try
        {
            IndexGame(args[1]);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(args[1]);
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            ModuleDefinition plugin = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });

            Console.WriteLine("Harmony patches ([GamePatch]):");
            CheckPatches(plugin);
            Console.WriteLine("Game members looked up by name (GameMember):");
            CheckMemberLookups(plugin);
            Console.WriteLine("Game members named in the patch methods' code and their classes' static set-up:");
            CheckHookCode();
            Console.WriteLine("State a prefix hands its postfix or finalizer (__state):");
            CheckStatePairs(plugin);
            Console.WriteLine("Game members looked up by name outside GameMember:");
            CheckOtherLookups(plugin);
            Console.WriteLine("Features:");
            CheckFeatures(plugin);
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e);
            return 2;
        }
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED (" + _checks + ")" : _failures + " CHECK(S) FAILED of " + _checks);
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }

    /// <summary>A line for what the checker can't verify (not counted).</summary>
    private static void Note(string what)
    {
        Console.WriteLine("  note " + what);
    }

    // ------------------------------------------------------------------ the game's types

    private static void IndexGame(string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        foreach (string path in Directory.GetFiles(managed, "*.dll"))
        {
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
            }
            catch
            {
                continue; // native or unreadable
            }
            GameAssemblies.Add(module.Assembly.Name.Name);
            foreach (TypeDefinition type in module.GetTypes())
            {
                string name = Normalize(type.FullName);
                if (!Types.ContainsKey(name)) Types[name] = type;
            }
        }
    }

    /// <summary>Cecil writes nested types as Outer/Inner; reflection and AccessTools use Outer+Inner.</summary>
    private static string Normalize(string name)
    {
        return name.Replace('/', '+');
    }

    private static TypeDefinition FindType(string name)
    {
        TypeDefinition type;
        return Types.TryGetValue(name, out type) ? type : null;
    }

    private static IEnumerable<TypeDefinition> Hierarchy(TypeDefinition type)
    {
        while (type != null)
        {
            yield return type;
            TypeReference baseType = type.BaseType;
            if (baseType == null) yield break;
            TypeDefinition next;
            try
            {
                next = baseType.Resolve();
            }
            catch
            {
                next = FindType(Normalize(baseType.FullName.Split('<')[0]));
            }
            type = next;
        }
    }

    // ------------------------------------------------------------------ 1. [GamePatch]

    private static void CheckPatches(ModuleDefinition plugin)
    {
        foreach (TypeDefinition host in plugin.GetTypes())
        {
            foreach (MethodDefinition hook in host.Methods)
            {
                foreach (CustomAttribute attribute in hook.CustomAttributes.Where(a => a.AttributeType.FullName == AttributeName))
                {
                    string feature = (string)attribute.ConstructorArguments[0].Value;
                    string typeName = (string)attribute.ConstructorArguments[1].Value;
                    string methodName = (string)attribute.ConstructorArguments[2].Value;
                    int kind = Convert.ToInt32(attribute.ConstructorArguments[3].Value);
                    string[] arguments = null;
                    bool optional = false;
                    bool coroutine = false;
                    bool afterGameLoad = false;
                    int lambda = -1;
                    foreach (Mono.Cecil.CustomAttributeNamedArgument property in attribute.Properties)
                    {
                        if (property.Name == "Arguments")
                        {
                            var values = property.Argument.Value as CustomAttributeArgument[];
                            if (values != null) arguments = values.Select(v => (string)v.Value).ToArray();
                        }
                        else if (property.Name == "Optional")
                        {
                            optional = (bool)property.Argument.Value;
                        }
                        else if (property.Name == "Coroutine")
                        {
                            coroutine = (bool)property.Argument.Value;
                        }
                        else if (property.Name == "Lambda")
                        {
                            lambda = Convert.ToInt32(property.Argument.Value);
                        }
                        else if (property.Name == "AfterGameLoad")
                        {
                            afterGameLoad = (bool)property.Argument.Value;
                        }
                    }
                    if (!Hooks.Contains(hook)) Hooks.Add(hook);
                    PatchedFeatures.Add(feature);
                    int count;
                    StartupPatches.TryGetValue(feature, out count);
                    StartupPatches[feature] = count + (afterGameLoad ? 0 : 1);
                    // Features.Listen registers a feature's work only while it is on at start-up: a required late patch could
                    // still switch it off after that.
                    if (afterGameLoad)
                        Check(optional, "[" + feature + "] " + typeName + "." + methodName + " <- " + hook.DeclaringType.Name + "." + hook.Name +
                                        ": a patch that waits for the game to load is optional");
                    CheckPatch(feature, typeName, methodName, kind, arguments, optional, coroutine, lambda, hook);
                }
            }
        }
    }

    private static void CheckPatch(string feature, string typeName, string methodName, int kind, string[] arguments, bool optional, bool coroutine, int lambda,
        MethodDefinition hook)
    {
        string what = "[" + feature + (optional ? ", optional" : "") + "] " + typeName + "." + methodName +
                      (arguments != null ? "(" + string.Join(", ", arguments) + ")" : "") + (coroutine ? " (coroutine)" : lambda >= 0 ? " (lambda " + lambda + ")" : "") +
                      " <- " + hook.DeclaringType.Name + "." + hook.Name;
        Check(hook.IsStatic && !hook.HasGenericParameters && !hook.DeclaringType.HasGenericParameters, what + ": patch method is static and not generic");
        TypeDefinition type = FindType(typeName);
        if (type == null)
        {
            Check(false, what + ": type exists");
            return;
        }

        MethodDefinition target = null;
        foreach (TypeDefinition level in Hierarchy(type))
        {
            List<MethodDefinition> candidates = level.Methods.Where(m => m.Name == methodName).ToList();
            if (candidates.Count == 0) continue;
            if (arguments != null)
            {
                target = candidates.FirstOrDefault(m => m.Parameters.Count == arguments.Length &&
                                                        m.Parameters.Select(p => Normalize(p.ParameterType.FullName)).SequenceEqual(arguments.Select(Normalize)));
                if (target == null)
                {
                    Check(false, what + ": an overload with those arguments exists (found: " +
                                 string.Join(" | ", candidates.Select(m => "(" + string.Join(", ", m.Parameters.Select(p => Normalize(p.ParameterType.FullName))) + ")")) + ")");
                    return;
                }
            }
            else
            {
                if (candidates.Count != 1)
                {
                    Check(false, what + ": exactly one method of that name (found " + candidates.Count + "; add Arguments to the attribute)");
                    return;
                }
                target = candidates[0];
            }
            break;
        }
        if (target == null)
        {
            Check(false, what + ": method exists");
            return;
        }
        Check(target.HasBody || target.IsAbstract == false, what + ": method found in " + target.DeclaringType.FullName);
        if (coroutine)
        {
            target = CoroutineBody(target, what);
            if (target == null) return;
        }
        else if (lambda >= 0)
        {
            target = LambdaIn(target.DeclaringType, methodName, lambda, what);
            if (target == null) return;
        }
        if (kind == 3)
        {
            // Transpilers get their arguments by type (instructions, ILGenerator, original), not by name.
            Check(target.HasBody, what + ": target has a body to transpile");
            if (target.HasBody) CheckTranspilerTarget(hook, target, what);
            return;
        }

        foreach (ParameterDefinition parameter in hook.Parameters)
        {
            string name = parameter.Name;
            if (name == "__instance")
            {
                Check(!target.IsStatic, what + ": __instance on an instance method");
                if (!target.IsStatic) CheckInjected(what, name, parameter.ParameterType, target.DeclaringType, InstanceRule(target.DeclaringType));
            }
            else if (name == "__result")
            {
                bool returns = target.ReturnType.FullName != "System.Void";
                Check(returns, what + ": __result on a method that returns a value");
                if (returns) CheckInjected(what, name, parameter.ParameterType, target.ReturnType, Rule.Result);
            }
            else if (name == "__exception") Check(kind == 2 || kind == 1, what + ": __exception in a postfix or finalizer");
            else if (name == "__state" || name == "__runOriginal" || name == "__originalMethod" || name == "__args") continue;
            else if (name.StartsWith("___", StringComparison.Ordinal))
            {
                string field = name.Substring(3);
                FieldDefinition found = Hierarchy(target.DeclaringType).SelectMany(t => t.Fields).FirstOrDefault(f => f.Name == field);
                Check(found != null, what + ": field " + field + " exists for ___" + field);
                if (found != null) CheckInjected(what, name, parameter.ParameterType, found.FieldType, Rule.Field);
            }
            else if (name.StartsWith("__", StringComparison.Ordinal))
            {
                int index;
                bool exists = int.TryParse(name.Substring(2), out index) && index >= 0 && index < target.Parameters.Count;
                Check(exists, what + ": argument " + name + " exists");
                if (exists) CheckInjected(what, name, parameter.ParameterType, target.Parameters[index].ParameterType, Rule.Argument);
            }
            else
            {
                ParameterDefinition argument = target.Parameters.FirstOrDefault(p => p.Name == name);
                Check(argument != null, what + ": target has a parameter named " + name);
                if (argument != null) CheckInjected(what, name, parameter.ParameterType, argument.ParameterType, Rule.Argument);
            }
        }
    }

    // ------------------------------------------------------------------ 1. injected parameter types

    /// <summary>How Harmony (BepInEx's HarmonyX, HarmonyManipulator.EmitCallParameter) passes a value to a patch method's parameter.</summary>
    private enum Rule
    {
        /// <summary>An argument of the game method: boxed for a parameter of a reference type when it is a value; by reference the same type.</summary>
        Argument,

        /// <summary>A field (ldfld, ldflda): never boxed; by reference the same type.</summary>
        Field,

        /// <summary>The return value: boxed only into object; by reference the same type (or object for a value).</summary>
        Result,

        /// <summary>The instance of a class: a base type or interface of it will do; by reference the same type.</summary>
        ClassInstance,

        /// <summary>The instance of a struct: the same type, by value or by reference.</summary>
        StructInstance
    }

    private static Rule InstanceRule(TypeDefinition declaringType)
    {
        return declaringType.IsValueType ? Rule.StructInstance : Rule.ClassInstance;
    }

    /// <summary>Checks that Harmony passes a value of type 'given' to a patch method's parameter of type 'wanted' as it should.</summary>
    private static void CheckInjected(string what, string name, TypeReference wanted, TypeReference given, Rule rule)
    {
        bool byRef = wanted is ByReferenceType;
        TypeReference hook = byRef ? ((ByReferenceType)wanted).ElementType : wanted;
        TypeReference game = given is ByReferenceType ? ((ByReferenceType)given).ElementType : given;
        bool fits;
        bool value = IsValueType(game);
        switch (rule)
        {
            case Rule.StructInstance:
                fits = Same(hook, game);
                break;
            case Rule.Result:
                fits = byRef ? Same(hook, game) || (value && Name(hook) == "System.Object") : value ? Same(hook, game) || Name(hook) == "System.Object" : AssignableTo(game, hook);
                break;
            case Rule.Field:
                fits = byRef || value ? Same(hook, game) : AssignableTo(game, hook);
                break;
            case Rule.Argument:
                // A value passed to a parameter of a reference type is boxed (by reference too, through a temporary).
                fits = value && !IsValueType(hook) ? AssignableTo(game, hook) : byRef ? Same(hook, game) : AssignableTo(game, hook);
                break;
            default:
                fits = byRef ? Same(hook, game) : AssignableTo(game, hook);
                break;
        }
        Check(fits, what + ": " + name + " (" + (byRef ? "ref " : "") + Name(hook) + ") can take the " + Name(game) + " Harmony passes");
    }

    /// <summary>A type's name as reflection writes it (nested types with '+'), generic arguments included.</summary>
    private static string Name(TypeReference type)
    {
        return Normalize(type.FullName);
    }

    private static bool Same(TypeReference a, TypeReference b)
    {
        return Name(a) == Name(b);
    }

    private static bool IsValueType(TypeReference type)
    {
        if (type is GenericParameter || type is ArrayType) return false;
        try
        {
            TypeDefinition definition = type.Resolve();
            return definition != null ? definition.IsValueType : type.IsValueType;
        }
        catch
        {
            return type.IsValueType;
        }
    }

    /// <summary>Whether a value of type 'from' is one of type 'to' without a conversion: the same type, one of its base types or interfaces, or object.</summary>
    private static bool AssignableTo(TypeReference from, TypeReference to)
    {
        string target = Name(to);
        if (target == "System.Object" || Name(from) == target) return true;
        var seen = new HashSet<string>();
        var pending = new Queue<TypeReference>();
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            TypeReference current = pending.Dequeue();
            if (!seen.Add(Name(current))) continue;
            if (Name(current) == target) return true;
            TypeDefinition definition;
            try
            {
                definition = current.Resolve();
            }
            catch
            {
                definition = null;
            }
            if (definition == null) continue;
            if (definition.BaseType != null) pending.Enqueue(definition.BaseType);
            foreach (InterfaceImplementation implemented in definition.Interfaces) pending.Enqueue(implemented.InterfaceType);
        }
        return false;
    }

    // ------------------------------------------------------------------ 1. transpiler targets

    /// <summary>A game member looked up by name: Type, Field, Method or Property, its type's name and its own.</summary>
    private sealed class Lookup
    {
        public string Kind;
        public string TypeName;
        public string Member;

        public override string ToString()
        {
            return TypeName + "." + Member;
        }
    }

    /// <summary>
    /// A transpiler changes the calls (or reads) of game members its code looks up by name: the GameMember lookups whose
    /// results it reads from its class's static fields, in its own code and in the code of its class it calls (an iterator's
    /// body included). Its target must still have one of them, or the transpiler changes nothing (Patcher.NothingReplaced).
    /// </summary>
    private static void CheckTranspilerTarget(MethodDefinition hook, MethodDefinition target, string what)
    {
        List<Lookup> lookups = TranspilerLookups(hook);
        if (lookups.Count == 0)
        {
            Check(false, what + ": the transpiler looks up what it replaces with GameMember, into a static field (the checker can't tell what it replaces)");
            return;
        }
        Lookup found = lookups.FirstOrDefault(lookup => Uses(target, lookup));
        Check(found != null, what + ": target still uses what the transpiler replaces (" + (found != null ? found.ToString() : "none of: " + string.Join(", ", lookups.Select(l => l.ToString()).Distinct())) + ")");
    }

    private static List<Lookup> TranspilerLookups(MethodDefinition hook)
    {
        TypeDefinition home = hook.DeclaringType;
        var fields = new List<FieldDefinition>();
        var seen = new HashSet<MethodDefinition>();
        var pending = new Queue<MethodDefinition>();
        pending.Enqueue(hook);
        while (pending.Count > 0)
        {
            MethodDefinition method = pending.Dequeue();
            if (!seen.Add(method) || !method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var field = instruction.Operand as FieldReference;
                if (field != null && instruction.OpCode == OpCodes.Ldsfld)
                {
                    FieldDefinition definition = ResolveField(field);
                    if (definition != null && definition.Module == home.Module && !fields.Contains(definition)) fields.Add(definition);
                    continue;
                }
                var called = instruction.Operand as MethodReference;
                if (called == null) continue;
                MethodDefinition callee = ResolveMethod(called);
                if (callee == null || !Within(callee.DeclaringType, home)) continue;
                pending.Enqueue(callee);
                // An iterator: its body is in the class the compiler makes for it, created here.
                if (callee.IsConstructor && callee.DeclaringType != home)
                {
                    foreach (MethodDefinition part in callee.DeclaringType.Methods) pending.Enqueue(part);
                }
            }
        }
        var lookups = new List<Lookup>();
        var followed = new HashSet<FieldDefinition>();
        foreach (FieldDefinition field in fields) AddFieldLookups(field, lookups, followed);
        return lookups;
    }

    /// <summary>Whether a type is the class or one nested in it (an iterator's, a lambda's).</summary>
    private static bool Within(TypeDefinition type, TypeDefinition home)
    {
        for (TypeDefinition current = type; current != null; current = current.DeclaringType)
        {
            if (current == home) return true;
        }
        return false;
    }

    /// <summary>The GameMember lookups a static field of the plugin is set up from, in its class's static set-up (through other static fields too).</summary>
    private static void AddFieldLookups(FieldDefinition field, List<Lookup> lookups, HashSet<FieldDefinition> followed)
    {
        if (!followed.Add(field)) return;
        MethodDefinition setUp = field.DeclaringType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
        if (setUp == null) return;
        IList<Instruction> code = setUp.Body.Instructions;
        int start = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].OpCode != OpCodes.Stsfld) continue;
            FieldDefinition stored = ResolveField(code[i].Operand as FieldReference);
            if (stored == field)
            {
                for (int j = start; j < i; j++)
                {
                    var call = code[j].Operand as MethodReference;
                    if (call != null && (code[j].OpCode == OpCodes.Call || code[j].OpCode == OpCodes.Callvirt) && call.DeclaringType.FullName == MemberHelper)
                    {
                        Lookup lookup = ReadLookup(code, j, call);
                        if (lookup != null && lookup.Kind != "Type") lookups.Add(lookup);
                    }
                    var other = code[j].OpCode == OpCodes.Ldsfld ? ResolveField(code[j].Operand as FieldReference) : null;
                    if (other != null && other.Module == field.Module) AddFieldLookups(other, lookups, followed);
                }
            }
            start = i + 1;
        }
    }

    /// <summary>Whether a game method's code calls (or reads, for a field) the member looked up, on its type or one of its base types.</summary>
    private static bool Uses(MethodDefinition target, Lookup lookup)
    {
        TypeDefinition type = FindType(lookup.TypeName);
        var owners = new HashSet<string>(type != null ? Hierarchy(type).Select(t => Normalize(t.FullName)) : new[] { lookup.TypeName });
        foreach (Instruction instruction in target.Body.Instructions)
        {
            var member = instruction.Operand as MemberReference;
            if (member == null || member.DeclaringType == null) continue;
            TypeReference owner = member.DeclaringType is GenericInstanceType ? ((GenericInstanceType)member.DeclaringType).ElementType : member.DeclaringType;
            if (!owners.Contains(Normalize(owner.FullName))) continue;
            if (lookup.Kind == "Field" ? member is FieldReference && member.Name == lookup.Member :
                lookup.Kind == "Property" ? member is MethodReference && (member.Name == "get_" + lookup.Member || member.Name == "set_" + lookup.Member) :
                member is MethodReference && member.Name == lookup.Member)
                return true;
        }
        return false;
    }

    private static FieldDefinition ResolveField(FieldReference field)
    {
        if (field == null) return null;
        try
        {
            return field.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static MethodDefinition ResolveMethod(MethodReference method)
    {
        try
        {
            return method.Resolve();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The coroutine body Patcher.CoroutineBody finds: the MoveNext of the class the method's [IteratorStateMachine] names,
    /// which must be nested in the method's class.
    /// </summary>
    private static MethodDefinition CoroutineBody(MethodDefinition method, string what)
    {
        CustomAttribute mark = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IteratorStateMachineAttribute");
        var machineRef = mark != null && mark.ConstructorArguments.Count == 1 ? mark.ConstructorArguments[0].Value as TypeReference : null;
        TypeDefinition machine = machineRef != null ? FindType(Normalize(machineRef.FullName)) : null;
        bool nested = machine != null && machine.DeclaringType != null && Normalize(machine.DeclaringType.FullName) == Normalize(method.DeclaringType.FullName);
        MethodDefinition body = nested ? machine.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.HasBody) : null;
        Check(body != null, what + ": a coroutine, its body " + (machine != null ? Normalize(machine.FullName) + ".MoveNext" : "not named by the method"));
        return body;
    }

    /// <summary>The lambda Patcher.LambdaIn finds: exactly one method named &lt;Method&gt;b__N or &lt;Method&gt;b__P_N on the class or a class nested in it.</summary>
    private static MethodDefinition LambdaIn(TypeDefinition type, string method, int number, string what)
    {
        string prefix = "<" + method + ">b__";
        var found = new List<MethodDefinition>();
        foreach (TypeDefinition candidate in new[] { type }.Concat(type.NestedTypes))
        {
            foreach (MethodDefinition lambda in candidate.Methods)
            {
                if (lambda.Name.StartsWith(prefix, StringComparison.Ordinal) && LambdaNumber(lambda.Name.Substring(prefix.Length)) == number) found.Add(lambda);
            }
        }
        Check(found.Count == 1 && found[0].HasBody, what + ": exactly one lambda numbered " + number + " (found " +
                                                    (found.Count == 0 ? "none" : string.Join(", ", found.Select(f => Normalize(f.DeclaringType.FullName) + "." + f.Name))) + ")");
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>As Patcher.LambdaNumber: the lambda's own number from the end of its compiler name ("1" or "12_1"), or -1.</summary>
    private static int LambdaNumber(string suffix)
    {
        int cut = suffix.LastIndexOf('_');
        int number;
        return int.TryParse(cut >= 0 ? suffix.Substring(cut + 1) : suffix, out number) ? number : -1;
    }

    // ------------------------------------------------------------------ 3. game members named in the patch methods' code

    private static void CheckHookCode()
    {
        foreach (MethodDefinition hook in Hooks)
        {
            List<string> missing = MissingGameMembers(hook);
            Check(missing.Count == 0, hook.DeclaringType.Name + "." + hook.Name + ": its code names only game members that exist" +
                                      (missing.Count > 0 ? " (missing: " + string.Join("; ", missing) + ")" : ""));
        }
        foreach (TypeDefinition host in Hooks.Select(h => h.DeclaringType).Distinct())
        {
            var missing = new List<string>();
            foreach (FieldDefinition field in host.Fields)
            {
                if (!Resolves(field.FieldType)) missing.Add(field.FieldType.FullName + " (field " + field.Name + ")");
            }
            MethodDefinition setUp = host.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            if (setUp != null) missing.AddRange(MissingGameMembers(setUp));
            Check(missing.Count == 0, host.Name + ": its static fields and static set-up name only game members that exist" +
                                      (missing.Count > 0 ? " (missing: " + string.Join("; ", missing.Distinct()) + ")" : ""));
        }
    }

    /// <summary>The game types, fields and methods a method's code, locals, parameters and return type name that the game doesn't have.</summary>
    private static List<string> MissingGameMembers(MethodDefinition method)
    {
        var missing = new List<string>();
        var types = new List<TypeReference> { method.ReturnType };
        types.AddRange(method.Parameters.Select(p => p.ParameterType));
        if (method.HasBody)
        {
            types.AddRange(method.Body.Variables.Select(v => v.VariableType));
            types.AddRange(method.Body.ExceptionHandlers.Where(h => h.CatchType != null).Select(h => h.CatchType));
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var type = instruction.Operand as TypeReference;
                if (type != null)
                {
                    types.Add(type);
                    continue;
                }
                var genericMethod = instruction.Operand as GenericInstanceMethod;
                if (genericMethod != null) types.AddRange(genericMethod.GenericArguments);
                var member = instruction.Operand as MemberReference;
                if (member == null || member.DeclaringType == null) continue;
                // A member of another library's generic type (a BepInEx setting, a Harmony field reader): its game type arguments.
                types.Add(member.DeclaringType);
                if (!FromGame(member.DeclaringType) || member.DeclaringType is ArrayType) continue;
                bool found;
                try
                {
                    var asMethod = member as MethodReference;
                    var asField = member as FieldReference;
                    found = asMethod != null ? asMethod.Resolve() != null : asField == null || asField.Resolve() != null;
                }
                catch
                {
                    found = false;
                }
                if (!found) missing.Add(member.FullName);
            }
        }
        foreach (TypeReference type in types)
        {
            if (!Resolves(type)) missing.Add(type.FullName);
        }
        return missing.Distinct().ToList();
    }

    /// <summary>
    /// Whether a type is defined in one of the game's assemblies (for an array, pointer, by-ref or generic type: whether
    /// its element type is; generic arguments are checked on their own).
    /// </summary>
    private static bool FromGame(TypeReference type)
    {
        if (type == null || type is GenericParameter) return false;
        var specification = type as TypeSpecification;
        if (specification != null) return FromGame(specification.ElementType);
        IMetadataScope scope = type.Scope;
        var assembly = scope as AssemblyNameReference;
        var module = scope as ModuleDefinition;
        string name = assembly != null ? assembly.Name : module != null ? module.Assembly.Name.Name : null;
        return name != null && GameAssemblies.Contains(name);
    }

    /// <summary>Whether every part of a type that comes from the game (the type itself, its element type, its generic arguments) exists there.</summary>
    private static bool Resolves(TypeReference type)
    {
        if (type == null || type is GenericParameter) return true;
        var generic = type as GenericInstanceType;
        if (generic != null) return Resolves(generic.ElementType) && generic.GenericArguments.All(Resolves);
        var specification = type as TypeSpecification;
        if (specification != null) return Resolves(specification.ElementType);
        if (!FromGame(type)) return true;
        try
        {
            return type.Resolve() != null;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 2. GameMember lookups

    private static void CheckMemberLookups(ModuleDefinition plugin)
    {
        foreach (TypeDefinition type in plugin.GetTypes())
        {
            if (type.FullName == MemberHelper) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                IList<Instruction> code = method.Body.Instructions;
                for (int i = 0; i < code.Count; i++)
                {
                    var call = code[i].Operand as MethodReference;
                    if (call == null || (code[i].OpCode != OpCodes.Call && code[i].OpCode != OpCodes.Callvirt)) continue;
                    if (call.DeclaringType.FullName != MemberHelper) continue;
                    int count = LookupArguments(call);
                    if (count == 0) continue;
                    string where = " (in " + type.Name + "." + method.Name + ")";
                    Lookup lookup = ReadLookup(code, i, call);
                    if (lookup == null)
                    {
                        Check(false, "GameMember." + call.Name + " is called with " + (count == 2 && call.Parameters[0].ParameterType.FullName == "System.Type"
                            ? "typeof() and a constant name"
                            : "constant names") + where);
                        continue;
                    }
                    CheckLookup(lookup, true, where);
                }
            }
        }
    }

    /// <summary>How many arguments a GameMember lookup takes (1 for Type, 2 for a member); 0 for its other methods.</summary>
    private static int LookupArguments(MethodReference call)
    {
        int count = call.Name == "Type" ? 1 : (call.Name == "Field" || call.Name == "Method" || call.Name == "Property" ? 2 : 0);
        return count != 0 && call.Parameters.Count == count ? count : 0;
    }

    /// <summary>
    /// The member a GameMember lookup at code[i] names, read from its constant arguments: GameMember.X(typeof(T), "name")
    /// (ldtoken T; call Type.GetTypeFromHandle; ldstr "name") or GameMember.X("T", "name"); null when they aren't constant.
    /// </summary>
    private static Lookup ReadLookup(IList<Instruction> code, int i, MethodReference call)
    {
        int count = LookupArguments(call);
        if (count == 0) return null;
        if (count == 2 && call.Parameters[0].ParameterType.FullName == "System.Type")
        {
            Instruction name = i - 1 >= 0 ? code[i - 1] : null;
            Instruction token = i - 3 >= 0 ? code[i - 3] : null;
            var typeRef = token != null && token.OpCode == OpCodes.Ldtoken ? token.Operand as TypeReference : null;
            if (typeRef == null || name == null || name.OpCode != OpCodes.Ldstr) return null;
            return new Lookup { Kind = call.Name, TypeName = ElementName(typeRef), Member = (string)name.Operand };
        }
        var names = new List<string>();
        for (int back = count; back >= 1; back--)
        {
            Instruction load = i - back >= 0 ? code[i - back] : null;
            names.Add(load != null && load.OpCode == OpCodes.Ldstr ? (string)load.Operand : null);
        }
        if (names.Any(n => n == null)) return null;
        return new Lookup { Kind = call.Name, TypeName = names[0], Member = count == 2 ? names[1] : null };
    }

    /// <summary>A type's name without its generic arguments (List`1 for List&lt;T&gt;), as GameMember's checks look it up.</summary>
    private static string ElementName(TypeReference type)
    {
        return Normalize((type is GenericInstanceType ? ((GenericInstanceType)type).ElementType : type).FullName);
    }

    /// <summary>Checks that a looked-up member exists; a method (or property) looked up without argument types exactly once.</summary>
    private static void CheckLookup(Lookup lookup, bool once, string where)
    {
        TypeDefinition type = FindType(lookup.TypeName);
        if (lookup.Kind == "Type" || type == null)
        {
            Check(type != null, "type " + lookup.TypeName + " exists" + where);
            return;
        }
        string member = lookup.Member;
        string what = lookup.Kind.ToLowerInvariant() + " " + lookup.TypeName + "." + member;
        if (lookup.Kind == "Field")
        {
            Check(Hierarchy(type).Any(t => t.Fields.Any(f => f.Name == member)), what + " exists" + where);
        }
        else if (lookup.Kind == "Property")
        {
            TypeDefinition level = Hierarchy(type).FirstOrDefault(t => t.Properties.Any(p => p.Name == member));
            int count = level == null ? 0 : level.Properties.Count(p => p.Name == member);
            if (once && count > 1) Check(false, what + " exists exactly once (found " + count + ")" + where);
            else Check(count > 0, what + " exists" + where);
        }
        else
        {
            TypeDefinition level = Hierarchy(type).FirstOrDefault(t => t.Methods.Any(m => m.Name == member));
            int count = level == null ? 0 : level.Methods.Count(m => m.Name == member);
            if (once) Check(count == 1, what + " exists exactly once (found " + count + ")" + where);
            else Check(count > 0, what + " exists" + where);
        }
    }

    // ------------------------------------------------------------------ 4. lookups by name outside GameMember

    /// <summary>The reflection calls that find a member by name, and the kind of member each finds.</summary>
    private static readonly Dictionary<string, string> NamedLookups = new Dictionary<string, string>
    {
        { "HarmonyLib.AccessTools::Method", "Method" }, { "HarmonyLib.AccessTools::DeclaredMethod", "Method" },
        { "HarmonyLib.AccessTools::Field", "Field" }, { "HarmonyLib.AccessTools::DeclaredField", "Field" },
        { "HarmonyLib.AccessTools::Property", "Property" }, { "HarmonyLib.AccessTools::DeclaredProperty", "Property" },
        { "HarmonyLib.AccessTools::PropertyGetter", "Property" }, { "HarmonyLib.AccessTools::PropertySetter", "Property" },
        { "HarmonyLib.AccessTools::DeclaredPropertyGetter", "Property" }, { "HarmonyLib.AccessTools::DeclaredPropertySetter", "Property" },
        { "HarmonyLib.AccessTools::FieldRefAccess", "Field" }, { "HarmonyLib.AccessTools::StaticFieldRefAccess", "Field" },
        { "System.Type::GetMethod", "Method" }, { "System.Type::GetField", "Field" }, { "System.Type::GetProperty", "Property" }
    };

    /// <summary>
    /// Every lookup by name (AccessTools, Type.GetMethod and the like) outside GameMember: on a game type named with typeof(),
    /// the member must exist (a method or property looked up without argument types exactly once, or AccessTools and
    /// Type.GetMethod throw), and the lookup must not be in a static set-up, where a throw breaks every hook of the class
    /// (GameMember never throws). The plugin's own types aren't checked; a type only known at run time gets a note. GameMember
    /// and the patch system (Patcher) are left out: their lookups by name are those checks 1 and 2 verify.
    /// </summary>
    private static void CheckOtherLookups(ModuleDefinition plugin)
    {
        TypeDefinition helper = plugin.GetType(MemberHelper);
        TypeDefinition patcher = plugin.GetType(PatcherClass);
        foreach (TypeDefinition type in plugin.GetTypes())
        {
            if (Within(type, helper) || Within(type, patcher)) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                IList<Instruction> code = method.Body.Instructions;
                for (int i = 0; i < code.Count; i++)
                {
                    var call = code[i].Operand as MethodReference;
                    if (call == null || (code[i].OpCode != OpCodes.Call && code[i].OpCode != OpCodes.Callvirt)) continue;
                    string kind;
                    if (!NamedLookups.TryGetValue(call.DeclaringType.FullName + "::" + call.Name, out kind)) continue;
                    if (!call.Parameters.Any(p => p.ParameterType.FullName == "System.String")) continue;
                    string where = " (in " + type.Name + "." + method.Name + ")";
                    // The name: the constant loaded last before the call (its other arguments are flags or argument types);
                    // the type: typeof() loaded before that.
                    int nameAt = -1;
                    for (int back = i - 1; back >= 0 && back >= i - 24; back--)
                    {
                        if (code[back].OpCode == OpCodes.Ldstr)
                        {
                            nameAt = back;
                            break;
                        }
                    }
                    TypeReference owner = nameAt >= 2 && code[nameAt - 1].OpCode == OpCodes.Call && ((MethodReference)code[nameAt - 1].Operand).Name == "GetTypeFromHandle" &&
                                          code[nameAt - 2].OpCode == OpCodes.Ldtoken
                        ? code[nameAt - 2].Operand as TypeReference
                        : null;
                    string shown = call.DeclaringType.Name + "." + call.Name + (nameAt >= 0 ? "(\"" + code[nameAt].Operand + "\")" : "");
                    if (owner == null)
                    {
                        Note("a lookup by name the checker can't verify (its type is only known at run time): " + shown + where);
                        continue;
                    }
                    if (!FromGame(owner)) continue;
                    bool withTypes = call.Parameters.Any(p => p.ParameterType.FullName == "System.Type[]") && !ArgumentIsNull(code, nameAt, i);
                    bool canThrow = (kind == "Method" || kind == "Property") && !withTypes;
                    var lookup = new Lookup { Kind = kind, TypeName = ElementName(owner), Member = (string)code[nameAt].Operand };
                    CheckLookup(lookup, canThrow, " (" + shown + where + ")");
                    if (method.IsConstructor && method.IsStatic && canThrow)
                        Check(false, shown + where + " is in a static set-up and throws if a game update adds an overload, which would break every hook of " +
                                     type.Name + ": look it up with GameMember, which never throws");
                }
            }
        }
    }

    /// <summary>Whether a lookup's argument types were left out: between the name and the call only nulls and flags are loaded.</summary>
    private static bool ArgumentIsNull(IList<Instruction> code, int nameAt, int callAt)
    {
        for (int i = nameAt + 1; i < callAt; i++)
        {
            OpCode op = code[i].OpCode;
            if (op == OpCodes.Newarr || op == OpCodes.Ldsfld || op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Ldloc || op == OpCodes.Ldloc_0 ||
                op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc_S || op == OpCodes.Ldarg || op == OpCodes.Ldarg_0 ||
                op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3 || op == OpCodes.Ldarg_S)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ 5. features

    /// <summary>
    /// The start-up report (Features.All) lists every feature with a patch applied at start-up, each feature it lists has one,
    /// and every Feature constant names a feature with a patch.
    /// </summary>
    private static void CheckFeatures(ModuleDefinition plugin)
    {
        TypeDefinition features = plugin.GetType(FeaturesClass);
        MethodDefinition setUp = features != null ? features.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody) : null;
        if (setUp == null)
        {
            Check(false, FeaturesClass + " has its list of features (All)");
            return;
        }
        // Its static set-up only fills All: every constant in it is a feature's name.
        var listed = new HashSet<string>(setUp.Body.Instructions.Where(x => x.OpCode == OpCodes.Ldstr).Select(x => (string)x.Operand));
        foreach (KeyValuePair<string, int> feature in StartupPatches.OrderBy(f => f.Key))
        {
            if (feature.Value == 0) continue;
            Check(listed.Contains(feature.Key), "\"" + feature.Key + "\" (" + feature.Value + " patches at start-up) is in the start-up report (Features.All)");
        }
        foreach (string feature in listed.OrderBy(f => f))
        {
            int count;
            StartupPatches.TryGetValue(feature, out count);
            Check(count > 0, "\"" + feature + "\" in the start-up report has a patch applied at start-up");
        }
        foreach (TypeDefinition type in plugin.GetTypes())
        {
            FieldDefinition constant = type.Fields.FirstOrDefault(f => f.Name == "Feature" && f.HasConstant && f.Constant is string);
            if (constant == null) continue;
            Check(PatchedFeatures.Contains((string)constant.Constant), type.Name + ".Feature (\"" + constant.Constant + "\") names a feature that has a patch");
        }
    }

    // ------------------------------------------------------------------ 4. __state between a prefix and its finalizer

    /// <summary>
    /// Harmony keeps one __state per patch class and game method, typed by the first of that class's patch methods there
    /// that names it. So:
    /// - a postfix or finalizer that reads __state needs a prefix of its class on the same method that sets it (out or ref)
    ///   with the same type, or it always reads the type's default;
    /// - an optional finalizer (or postfix) that ends a "player ship" (ShipRedirect.EndOverride) or a ship's event
    ///   (ShipEvents.End) must take __state, so it ends only what its own prefix began. Patches go in one by one, so an
    ///   optional prefix can be missing (a game update renamed an argument it reads) while its finalizer is in; a missing
    ///   required patch switches its whole feature off instead.
    /// </summary>
    private static void CheckStatePairs(ModuleDefinition plugin)
    {
        var groups = new Dictionary<string, List<KeyValuePair<MethodDefinition, int>>>();
        var labels = new Dictionary<string, string>();
        foreach (TypeDefinition host in plugin.GetTypes())
        {
            foreach (MethodDefinition hook in host.Methods)
            {
                foreach (CustomAttribute attribute in hook.CustomAttributes.Where(a => a.AttributeType.FullName == AttributeName))
                {
                    string feature = (string)attribute.ConstructorArguments[0].Value;
                    int kind = Convert.ToInt32(attribute.ConstructorArguments[3].Value);
                    bool optional = false;
                    string arguments = "", body = "";
                    foreach (Mono.Cecil.CustomAttributeNamedArgument property in attribute.Properties)
                    {
                        var values = property.Argument.Value as CustomAttributeArgument[];
                        if (property.Name == "Optional") optional = (bool)property.Argument.Value;
                        else if (property.Name == "Coroutine" && (bool)property.Argument.Value) body = " (coroutine)";
                        else if (property.Name == "Lambda") body = " (lambda " + property.Argument.Value + ")";
                        else if (property.Name == "Arguments" && values != null) arguments = "(" + string.Join(", ", values.Select(v => (string)v.Value)) + ")";
                    }
                    if (kind == 3) continue;
                    // The game method and the patch class (Harmony's __state is per both), written the same way whatever the
                    // order of the attribute's settings.
                    string key = (string)attribute.ConstructorArguments[1].Value + "." + (string)attribute.ConstructorArguments[2].Value + arguments + body +
                                 " <- " + host.Name;
                    if (!labels.ContainsKey(key)) labels[key] = "[" + feature + "] " + key;
                    List<KeyValuePair<MethodDefinition, int>> group;
                    if (!groups.TryGetValue(key, out group)) groups[key] = group = new List<KeyValuePair<MethodDefinition, int>>();
                    group.Add(new KeyValuePair<MethodDefinition, int>(hook, kind));
                    if (optional && kind != 0 && EndsWhatAPrefixBegan(hook))
                        Check(StateOf(hook) != null,
                            "[" + feature + "] " + key + "." + hook.Name + ": an optional finalizer that ends a \"player ship\" or a ship's event takes __state");
                }
            }
        }
        foreach (KeyValuePair<string, List<KeyValuePair<MethodDefinition, int>>> group in groups)
        {
            List<KeyValuePair<MethodDefinition, int>> withState = group.Value.Where(p => StateOf(p.Key) != null).ToList();
            if (withState.Count == 0) continue;
            List<string> types = withState.Select(p => Unref(StateOf(p.Key).ParameterType).FullName).Distinct().ToList();
            bool prefixSets = withState.Any(p => p.Value == 0 && StateOf(p.Key).ParameterType is ByReferenceType);
            bool prefixReadsOnly = withState.Any(p => p.Value == 0 && !(StateOf(p.Key).ParameterType is ByReferenceType));
            List<string> readers = withState.Where(p => p.Value != 0).Select(p => p.Key.Name).ToList();
            Check(types.Count == 1 && prefixSets && !prefixReadsOnly, labels[group.Key] + ": __state (" + string.Join(" / ", types) + ") set by a prefix, read by " +
                                                                    (readers.Count > 0 ? string.Join(", ", readers) : "none"));
        }
    }

    private static ParameterDefinition StateOf(MethodDefinition hook)
    {
        return hook.Parameters.FirstOrDefault(p => p.Name == "__state");
    }

    private static TypeReference Unref(TypeReference type)
    {
        var byRef = type as ByReferenceType;
        return byRef != null ? byRef.ElementType : type;
    }

    /// <summary>
    /// Whether a patch method's own code ends a "player ship" (ShipRedirect.EndOverride) or a ship's event (ShipEvents.End)
    /// that it doesn't begin itself (a try/finally pair in one method is no prefix's).
    /// </summary>
    private static bool EndsWhatAPrefixBegan(MethodDefinition hook)
    {
        if (!hook.HasBody) return false;
        int overrides = 0, events = 0;
        foreach (Instruction instruction in hook.Body.Instructions)
        {
            var call = instruction.Operand as MethodReference;
            if (call == null) continue;
            string owner = call.DeclaringType.FullName;
            if (owner == "VoidCrewFleet.ShipRedirect" && call.Name == "BeginOverride") overrides++;
            else if (owner == "VoidCrewFleet.ShipRedirect" && call.Name == "EndOverride") overrides--;
            else if (owner == "VoidCrewFleet.ShipEvents" && (call.Name == "Begin" || call.Name == "BeginFor")) events++;
            else if (owner == "VoidCrewFleet.ShipEvents" && call.Name == "End") events--;
        }
        return overrides < 0 || events < 0;
    }
}
