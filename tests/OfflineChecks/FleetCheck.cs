using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline checks for Void Crew Fleet (no game running). Everything is read with Mono.Cecil, nothing is executed:
//  1. every [GamePatch] in the plugin names a type and method that exist in the game's assemblies, with exactly one
//     method of that name unless the attribute lists the argument types, and every injected parameter of the patch
//     method (__instance, __result, ___field, argument names) fits that target;
//  2. every GameMember.Type/Field/Method/Property call in the plugin passes constant names that exist in the game.
// Run it with tests\build-and-test.ps1, which builds the plugin first.
// Usage: FleetCheck.exe <plugin dll> <game Managed dir>
internal static class Program
{
    private const string AttributeName = "VoidCrewFleet.GamePatchAttribute";
    private const string MemberHelper = "VoidCrewFleet.GameMember";

    private static readonly Dictionary<string, TypeDefinition> Types = new Dictionary<string, TypeDefinition>();
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
                    }
                    CheckPatch(feature, typeName, methodName, kind, arguments, optional, hook);
                }
            }
        }
    }

    private static void CheckPatch(string feature, string typeName, string methodName, int kind, string[] arguments, bool optional, MethodDefinition hook)
    {
        string what = "[" + feature + (optional ? ", optional" : "") + "] " + typeName + "." + methodName +
                      (arguments != null ? "(" + string.Join(", ", arguments) + ")" : "") + " <- " + hook.DeclaringType.Name + "." + hook.Name;
        Check(hook.IsStatic, what + ": patch method is static");
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
}
