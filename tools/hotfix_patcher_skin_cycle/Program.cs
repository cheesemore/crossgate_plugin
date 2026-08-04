namespace CrossgateMod.Patcher;

/// <summary>
/// 皮肤版专用补丁引擎入口（独立于主 HotfixPatcher，不改其 Program.cs）。
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // ignore
        }

        if (args.Length > 0 && args[0] == "wiki-skin-cycle-patch")
        {
            return WikiSkinCycleExternalIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "battle-appear-external-patch")
        {
            // 复用已链接的主工程实现，便于本 exe 单独完成皮肤版两步打补丁
            return BattleAppearExternalIlPatcher.Run(args.Skip(1).ToArray());
        }

        Console.WriteLine(
            "HotfixPatcherSkinCycle — 傻瓜皮肤补丁专用\n" +
            "  wiki-skin-cycle-patch --hotfix <in> --output <out>\n" +
            "  battle-appear-external-patch --hotfix <in> --output <out>");
        return 1;
    }

    /// <summary>供链接进来的主引擎源码调用（与主 Program 同名 API）。</summary>
    internal static IEnumerable<string> ResolveRefStubDirsPublic() => ResolveRefStubDirs();

    private static IEnumerable<string> ResolveRefStubDirs()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ref_stubs"),
            Path.Combine(AppContext.BaseDirectory, "ref_stubs", "bin"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ref_stubs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ref_stubs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "hotfix_patcher", "ref_stubs", "bin")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "hotfix_patcher", "ref_stubs", "Release")),
        };

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 10 && !string.IsNullOrEmpty(dir); depth++)
        {
            var stubBin = Path.Combine(dir, "tools", "hotfix_patcher", "ref_stubs", "bin");
            if (Directory.Exists(stubBin))
            {
                yield return stubBin;
            }

            dir = Path.GetDirectoryName(dir);
        }
    }
}
