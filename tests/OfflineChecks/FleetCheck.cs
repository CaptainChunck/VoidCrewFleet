using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline checks for Void Crew Fleet (no game running). Everything is read with Mono.Cecil, nothing is executed:
//  1. every [GamePatch] in the plugin names a type and method that exist in the game's assemblies, with exactly one
//     method of that name unless the attribute lists the argument types, and every injected parameter of the patch
//     method (__instance, __result, ___field, argument names) fits that target; a patch on a coroutine finds the
//     coroutine's body from the method ([IteratorStateMachine]), a patch on a lambda exactly one lambda with that number;
//     every patch method is static and not generic (the plugin compiles each one at start-up, Patcher.Prepare);
//  2. every GameMember.Type/Field/Method/Property call in the plugin passes constant names that exist in the game;
//  3. every game member a patch method's code names, and every one its class's static fields and static set-up name,
//     exists in the game: what the start-up compile would refuse. A build against the game already ensures it; this
//     shows it for a built plugin against another game version (FleetCheck.exe <old dll> <new game's Managed>).
// Run it with tests\build-and-test.ps1, which builds the plugin first.
// Usage: FleetCheck.exe <plugin dll> <game Managed dir>
internal static class Program
{
    private const string AttributeName = "VoidCrewFleet.GamePatchAttribute";
    private const string MemberHelper = "VoidCrewFleet.GameMember";

    private static readonly Dictionary<string, TypeDefinition> Types = new Dictionary<string, TypeDefinition>();

    /// <summary>The names of the assemblies in the game's Managed folder (the game's, Unity's, the class libraries).</summary>
    private static readonly HashSet<string> GameAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every patch method, once, for check 3.</summary>
    private static readonly List<MethodDefinition> Hooks = new List<MethodDefinition>();

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
            Console.WriteLine("Elements of the game's HUD that Fleet's PvP displays are put in or read (the game's own code names them):");
            CheckHudNames();
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
                    }
                    if (!Hooks.Contains(hook)) Hooks.Add(hook);
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
            return;
        }

        foreach (ParameterDefinition parameter in hook.Parameters)
        {
            string name = parameter.Name;
            if (name == "__instance") Check(!target.IsStatic, what + ": __instance on an instance method");
            else if (name == "__result") Check(target.ReturnType.FullName != "System.Void", what + ": __result on a method that returns a value");
            else if (name == "__exception") Check(kind == 2 || kind == 1, what + ": __exception in a postfix or finalizer");
            else if (name == "__state" || name == "__runOriginal" || name == "__originalMethod" || name == "__args") continue;
            else if (name.StartsWith("___", StringComparison.Ordinal))
            {
                string field = name.Substring(3);
                bool found = Hierarchy(target.DeclaringType).Any(t => t.Fields.Any(f => f.Name == field));
                Check(found, what + ": field " + field + " exists for ___" + field);
            }
            else if (name.StartsWith("__", StringComparison.Ordinal))
            {
                int index;
                Check(int.TryParse(name.Substring(2), out index) && index < target.Parameters.Count, what + ": argument " + name + " exists");
            }
            else
            {
                Check(target.Parameters.Any(p => p.Name == name), what + ": target has a parameter named " + name);
            }
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
                    int count = call.Name == "Type" ? 1 : (call.Name == "Field" || call.Name == "Method" || call.Name == "Property" ? 2 : 0);
                    if (count == 0 || call.Parameters.Count != count) continue;
                    string where = " (in " + type.Name + "." + method.Name + ")";

                    var names = new List<string>();
                    if (count == 2 && call.Parameters[0].ParameterType.FullName == "System.Type")
                    {
                        // GameMember.X(typeof(T), "name"): ldtoken T; call Type.GetTypeFromHandle; ldstr "name"
                        Instruction name = i - 1 >= 0 ? code[i - 1] : null;
                        Instruction token = i - 3 >= 0 ? code[i - 3] : null;
                        var typeRef = token != null && token.OpCode == OpCodes.Ldtoken ? token.Operand as TypeReference : null;
                        if (typeRef == null || name == null || name.OpCode != OpCodes.Ldstr)
                        {
                            Check(false, "GameMember." + call.Name + " is called with typeof() and a constant name" + where);
                            continue;
                        }
                        names.Add(Normalize((typeRef is GenericInstanceType ? ((GenericInstanceType)typeRef).ElementType : typeRef).FullName));
                        names.Add((string)name.Operand);
                        CheckLookup(call.Name, names, where);
                        continue;
                    }

                    for (int back = count; back >= 1; back--)
                    {
                        Instruction load = i - back >= 0 ? code[i - back] : null;
                        names.Add(load != null && load.OpCode == OpCodes.Ldstr ? (string)load.Operand : null);
                    }
                    if (names.Any(n => n == null))
                    {
                        Check(false, "GameMember." + call.Name + " is called with constant names" + where);
                        continue;
                    }
                    CheckLookup(call.Name, names, where);
                }
            }
        }
    }

    private static void CheckLookup(string kind, List<string> names, string where)
    {
        TypeDefinition type = FindType(names[0]);
        if (kind == "Type" || type == null)
        {
            Check(type != null, "type " + names[0] + " exists" + where);
            return;
        }
        string member = names[1];
        string what = kind.ToLowerInvariant() + " " + names[0] + "." + member;
        if (kind == "Field")
        {
            Check(Hierarchy(type).Any(t => t.Fields.Any(f => f.Name == member)), what + " exists" + where);
        }
        else if (kind == "Property")
        {
            Check(Hierarchy(type).Any(t => t.Properties.Any(p => p.Name == member)), what + " exists" + where);
        }
        else
        {
            TypeDefinition level = Hierarchy(type).FirstOrDefault(t => t.Methods.Any(m => m.Name == member));
            int count = level == null ? 0 : level.Methods.Count(m => m.Name == member);
            Check(count == 1, what + " exists exactly once (found " + count + ")" + where);
        }
    }

    // ------------------------------------------------------------------ 4. names in the game's HUD

    /// <summary>
    /// The elements of the game's HUD document (its UXML, which no assembly holds) that Fleet's PvP displays look up by name:
    /// each with a game type whose own code looks the same name up, so a game update that renames one shows here, in the
    /// game's code, rather than as a display that silently stops showing. (PvpHack's countdowns go into the HUD's mission
    /// list and use its mission and objective templates as MissionDisplay does; ActionHint's prompt goes into the HUD's row
    /// of key prompts, and writes the game's key prompt's action label.)
    /// </summary>
    private static readonly string[][] HudNames =
    {
        new[] { "MissionList", "MissionListUIController" },
        new[] { "MissionName", "MissionDisplay" },
        new[] { "MissionBody", "MissionDisplay" },
        new[] { "ObjectiveName", "MissionDisplay" },
        new[] { "BodyContainer", "MissionDisplay" },
        new[] { "ObjectiveTimer", "MissionDisplay" },
        new[] { "InteractionInfoContainer", "UI.MainHUD.HUDInteractionInfo" },
        new[] { "Action", "UI.Core.KeyBindVE" }
    };

    private static void CheckHudNames()
    {
        foreach (string[] pair in HudNames)
        {
            TypeDefinition type = FindType(pair[1]);
            Check(type != null && Names(type, pair[0]), "HUD element " + pair[0] + " is named by the game's own " + pair[1]);
        }
    }

    /// <summary>Whether a type's code (its nested types' too: a coroutine's body is one) loads this text.</summary>
    private static bool Names(TypeDefinition type, string text)
    {
        if (type.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == text)) return true;
        return type.NestedTypes.Any(nested => Names(nested, text));
    }
}
