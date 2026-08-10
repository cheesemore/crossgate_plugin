using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CrossgateMod.Patcher;

/// <summary>
/// 单角色一键自动上架·DLL版：编译并部署 SeqChapterAutoStall.dll.bytes。
/// 由助手面板「脚本」页「一键上架」按钮加载运行（EnsureFeatureType → RunAutoStallFromUi），
/// 也可由批量自动上架模块经 IPC（auto_stall_cmd.json）调用。
/// 只部署 DLL，不改 hotfix IL 体积。
/// </summary>
internal static class AutoStallExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterAutoStall.dll.bytes";
    public const string TypeName = "SeqChapterAutoStall";

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var restore = false;
        var detect = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotfix" when i + 1 < args.Length:
                    source = Path.GetFullPath(args[++i]);
                    break;
                case "--output" when i + 1 < args.Length:
                    output = Path.GetFullPath(args[++i]);
                    break;
                case "--restore":
                    restore = true;
                    break;
                case "--detect":
                    detect = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher auto-stall-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher auto-stall-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher auto-stall-external-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "patched" : "not_patched");
            return patched ? 0 : 1;
        }

        if (restore)
        {
            File.Copy(source, output, overwrite: true);
            Console.WriteLine("[RESTORE] 已从原版复制: " + output);
            return 0;
        }

        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 自动上架·DLL版补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var dllPath = BuildAutoStallDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[AUTO-STALL] 已部署 " + assetOut);
        Console.WriteLine("[AUTO-STALL] 助手面板脚本页「一键上架」加载运行（不改 hotfix IL）");
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var asset = Path.Combine(Path.GetDirectoryName(hotfixPath)!, AssetFileName);
            return File.Exists(asset);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAutoStallDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterAutoStall.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterAutoStall.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_auto_stall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterAutoStall.dll");

        var refs = new List<MetadataReference>();
        foreach (var name in new[] { "mscorlib.dll.bytes", "system.dll.bytes", "system.core.dll.bytes" })
        {
            var path = Path.Combine(hotfixDataDir, name);
            if (File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        if (refs.Count == 0)
        {
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译自动上架 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterAutoStall",
            new[] { syntax },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compile.Emit(ms);
        if (!result.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
            throw new InvalidOperationException("Roslyn 编译 SeqChapterAutoStall 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[AUTO-STALL] 已编译自动上架 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;

        // 优先：从 hotfixPath 所在目录向上探测游戏根目录（含 cg37_Data 的目录）下的 tools/seqchapter_auto_stall。
        // 这是唯一权威源；exeDir 同级的副本（发布残留）不能优先，否则会编译到旧版源码。
        var probes = new List<string>();
        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            probes.Add(Path.Combine(dir, "tools", "seqchapter_auto_stall"));

            if (Directory.Exists(Path.Combine(dir, "cg37_Data")))
            {
                break;
            }
        }

        // 兜底：exeDir 相关路径（发布工具链场景，exe 与 tools 目录结构固定）。
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "seqchapter_auto_stall")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_auto_stall")));
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_auto_stall")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_auto_stall")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_auto_stall")));

        foreach (var dir in probes)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterAutoStall.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_auto_stall 目录（请把 seqchapter_auto_stall 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
    }
}
