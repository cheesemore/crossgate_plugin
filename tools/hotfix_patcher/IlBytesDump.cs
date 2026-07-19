using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class IlBytesDump
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: HotfixPatcher ilbytes <hotfix> <Type.Method>");
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
            ?? throw new InvalidOperationException("type not found");
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody)
            ?? throw new InvalidOperationException("method not found");

        var body = method.Body;
        Console.WriteLine(
            $"RVA=0x{method.RVA:X} CodeSize=0x{body.CodeSize:X} MaxStack={body.MaxStackSize} Token=0x{method.MetadataToken.ToUInt32():X8}");

        var bytes = IlSerializer.Serialize(body);
        Console.WriteLine("serialized=" + bytes.Length);
        Console.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
        return 0;
    }
}
