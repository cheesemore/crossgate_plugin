using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CrossgateMod.Patcher;

/// <summary>
/// 一键加点·DLL版：编译并部署 SeqChapterAutoPoint.dll.bytes。
/// 由助手面板「脚本」页「一键加点」按钮加载（EnsureFeatureType → RunAllFromUi）。
/// 只部署 DLL，不改 hotfix IL 体积。
/// </summary>
internal static class AutoPointExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterAutoPoint.dll.bytes";
    public const string TypeName = "SeqChapterAutoPoint";

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
                "用法: HotfixPatcher auto-point-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher auto-point-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher auto-point-external-patch --hotfix <orig> --output <out> --restore");
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
            Console.WriteLine("[OK] 一键加点·DLL版补丁完成: " + output);
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
        var dllPath = BuildAutoPointDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[AUTO-POINT] 已部署 " + assetOut);
        Console.WriteLine("[AUTO-POINT] 助手面板脚本页「一键加点」按钮加载（不改 hotfix IL）");
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

    private static string BuildAutoPointDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterAutoPoint.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterAutoPoint.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_auto_point_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterAutoPoint.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译一键加点 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterAutoPoint",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterAutoPoint 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[AUTO-POINT] 已编译一键加点 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_auto_point")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_auto_point")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_auto_point")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_auto_point")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_auto_point")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_auto_point")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_auto_point");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }

            if (Directory.Exists(Path.Combine(dir, "cg37_Data")))
            {
                break;
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterAutoPoint.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_auto_point 目录（请把 seqchapter_auto_point 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
    }
}
