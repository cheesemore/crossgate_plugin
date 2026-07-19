using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class TokenScan
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: HotfixPatcher scan-tokens <hotfix.dll.bytes>");
            return 1;
        }

        var hotfixPath = Path.GetFullPath(args[0]);
        var data = File.ReadAllBytes(hotfixPath);
        var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        Console.WriteLine($"MemberRef rows (Cecil): {asm.MainModule.GetMemberReferences().Count()}");
        var seen = new HashSet<uint>();
        foreach (var type in asm.MainModule.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (insn.OpCode != OpCodes.Call && insn.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    var tn = called.DeclaringType?.Name ?? "";
                    if (tn is not ("Assembly" or "Type" or "MethodBase" or "File" or "FileUtil" or "String"))
                    {
                        continue;
                    }

                    var token = called.MetadataToken.ToUInt32();
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"[IL {insn.OpCode.Name}] {tn}.{called.Name} token=0x{token:X8} sig={called.FullName}");
                }
            }
        }

        foreach (var type in asm.MainModule.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Ldsfld || insn.Operand is not FieldReference field)
                    {
                        continue;
                    }

                    if (!(field.DeclaringType?.Name?.Contains("FileUtil") ?? false))
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"[IL ldsfld] {field.DeclaringType?.Name}.{field.Name} token=0x{field.MetadataToken.ToUInt32():X8}");
                }
            }
        }

        foreach (var memberRef in asm.MainModule.GetMemberReferences())
        {
            if (memberRef is not MethodReference method)
            {
                continue;
            }

            if (method.Name is "Load" or "GetType" or "GetMethod" or "Invoke" or "LoadBytesFromHotfixAssets")
            {
                Console.WriteLine(
                    $"[Cecil MR] {method.DeclaringType?.FullName}.{method.Name} token=0x{method.MetadataToken.ToUInt32():X8}");
            }
        }

        foreach (var typeName in new[] { "FileUtil", "Assembly", "Type", "MethodBase", "File" })
        {
            foreach (var methodName in new[] { "LoadBytesFromHotfixAssets", "Load", "GetType", "GetMethod", "Invoke", "ReadAllBytes" })
            {
                TryPrintCall(asm.MainModule, typeName, methodName, OpCodes.Call);
                TryPrintCallvirt(asm.MainModule, typeName, methodName);
            }
        }

        foreach (var name in new[]
                 {
                     "LoadBytesFromHotfixAssets",
                     "Load",
                     "GetType",
                     "GetMethod",
                     "Invoke",
                     "ReadAllBytes",
                 })
        {
            try
            {
                var token = MemberRefTokenLookup.FindToken(asm.MainModule, GuessType(name), name);
                Console.WriteLine($"[Cecil MemberRef] {GuessType(name)}.{name} = 0x{token:X8}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cecil MemberRef] {GuessType(name)}.{name} = {ex.Message}");
            }
        }

        foreach (var name in new[]
                 {
                     "LoadBytesFromHotfixAssets",
                     "Load",
                     "GetType",
                     "GetMethod",
                     "Invoke",
                     "ReadAllBytes",
                 })
        {
            try
            {
                var token = MemberRefTokenLookup.FindToken(data, GuessType(name), name);
                Console.WriteLine($"[PE MemberRef] {GuessType(name)}.{name} = 0x{token:X8}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PE MemberRef] {GuessType(name)}.{name} = {ex.Message}");
            }
        }

        foreach (var typeRef in asm.MainModule.GetTypeReferences().OrderBy(t => t.MetadataToken.ToUInt32()))
        {
            if (typeRef.Namespace?.Contains("Reflection") == true
                || typeRef.Name is "FileUtil" or "Assembly" or "MethodBase"
                || typeRef.FullName?.StartsWith("System.Type") == true)
            {
                Console.WriteLine(
                    $"[TypeRef] {typeRef.FullName} token=0x{typeRef.MetadataToken.ToUInt32():X8} scope={typeRef.Scope}");
            }
        }

        return 0;
    }

    private static string GuessType(string method) => method switch
    {
        "LoadBytesFromHotfixAssets" => "FileUtil",
        "ReadAllBytes" => "File",
        "Load" => "Assembly",
        "GetType" => "Type",
        "GetMethod" => "Type",
        "Invoke" => "MethodBase",
        _ => "?",
    };

    private static void TryPrintCall(ModuleDefinition module, string typeName, string methodName, OpCode op)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != op || insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (!Matches(called, typeName, methodName))
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"[IL {op.Name}] {type.Name}.{method.Name} -> {called.DeclaringType?.Name}.{called.Name} token=0x{called.MetadataToken.ToUInt32():X8}");
                }
            }
        }
    }

    private static void TryPrintCallvirt(ModuleDefinition module, string typeName, string methodName)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Callvirt || insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (!Matches(called, typeName, methodName))
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"[IL callvirt] {type.Name}.{method.Name} -> {called.DeclaringType?.Name}.{called.Name} token=0x{called.MetadataToken.ToUInt32():X8}");
                }
            }
        }
    }

    private static bool Matches(MethodReference called, string declaringTypeName, string methodName)
    {
        if (!string.Equals(called.Name, methodName, StringComparison.Ordinal))
        {
            return false;
        }

        var typeName = called.DeclaringType?.Name ?? "";
        return string.Equals(typeName, declaringTypeName, StringComparison.Ordinal)
            || called.DeclaringType?.FullName?.EndsWith("." + declaringTypeName, StringComparison.Ordinal) == true;
    }
}
