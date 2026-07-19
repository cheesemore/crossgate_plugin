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

        var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == typeName)
            ?? throw new InvalidOperationException("type not found: " + typeName);
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody)
            ?? throw new InvalidOperationException("method not found: " + methodName);

        Console.WriteLine($"== {typeName}.{methodName} ==");
        foreach (var insn in method.Body.Instructions)
        {
            Console.WriteLine($"{insn.Offset:X4} {insn.OpCode} {FormatOperand(insn)}");
        }

        return 0;
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
