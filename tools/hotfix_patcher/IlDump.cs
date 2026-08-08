using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class IlDump
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: HotfixPatcher ildump <hotfix> <Type.Method>");
            return 1;
        }

        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var target = args[1];
        var dot = target.LastIndexOf('.');
        var typeName = target[..dot];
        var methodName = target[(dot + 1)..];

        // 支持 .Method 全局搜索：只给方法名时遍历所有类型（含嵌套）
        MethodDefinition? method = null;
        if (dot <= 0)
        {
            methodName = target;
            foreach (var t in asm.MainModule.Types.SelectMany(CecilHelpers.NestedTypes))
            {
                method = t.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody);
                if (method != null)
                {
                    typeName = t.FullName;
                    break;
                }
            }

            if (method == null)
            {
                throw new InvalidOperationException("method not found: " + methodName);
            }
        }
        else
        {
            var type = FindTypeRecursive(asm.MainModule, typeName)
                ?? throw new InvalidOperationException("type not found: " + typeName);
            method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody)
                ?? type.NestedTypes.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == methodName && m.HasBody)
                ?? throw new InvalidOperationException("method not found: " + methodName);
        }

        Console.WriteLine($"== {typeName}.{methodName} ==");
        foreach (var insn in method.Body.Instructions)
        {
            Console.WriteLine($"{insn.Offset:X4} {insn.OpCode} {FormatOperand(insn)}");
        }

        return 0;
    }

    private static TypeDefinition? FindTypeRecursive(ModuleDefinition module, string typeName)
    {
        // 精确 FullName（含命名空间）优先，其次 NestedType 的 FullName（如 BattleRole/MoveToDestination>d__199）
        foreach (var t in module.Types.SelectMany(CecilHelpers.NestedTypes))
        {
            if (t.FullName == typeName || t.FullName.Replace('/', '.') == typeName || t.Name == typeName)
            {
                return t;
            }
        }

        return null;
    }

    private static string FormatOperand(Instruction insn)
    {
        return insn.Operand switch
        {
            MethodReference m => m.FullName,
            FieldReference f => f.FullName,
            TypeReference t => t.FullName,
            string s => "\"" + s + "\"",
            null => "",
            _ => insn.Operand?.ToString() ?? "",
        };
    }
}
