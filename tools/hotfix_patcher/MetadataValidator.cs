using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal static class MetadataValidator
{
    /// <summary>注入后必须能完整读取 MemberRef，否则 HybridCLR 加载 hotfix 会黑屏。</summary>
    public static void EnsureReadable(byte[] pe, string hotfixDir)
    {
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(
            new MemoryStream(pe, writable: false),
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

        var count = 0;
        foreach (var memberRef in asm.MainModule.GetMemberReferences())
        {
            if (memberRef is MethodReference method)
            {
                _ = method.ReturnType;
                _ = method.DeclaringType?.FullName;
            }

            count++;
        }

        Console.WriteLine($"[VERIFY] Cecil 可读 MemberRef {count} 条");
    }
}
