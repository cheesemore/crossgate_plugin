using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class FindField
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: HotfixPatcher findfield <hotfix> <fieldName>");
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

        var fieldName = args[1];
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
                    if (insn.Operand is not FieldReference field || field.Name != fieldName)
                    {
                        continue;
                    }

                    Console.WriteLine($"{type.Name}.{method.Name}@{insn.Offset:X4} {insn.OpCode} {field.FullName} token=0x{field.MetadataToken.ToUInt32():X8}");
                }
            }
        }

        return 0;
    }
}
