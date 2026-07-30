using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 日常·分享入口：仅改 MapSidebarPanel.OnClickShareCallback → 加载 SeqChapterDailyClaim.dll.bytes 并调用 OnShareClick。
/// 不占用 OnApplicationPause / 百科，可与九动/抓宠/烧卡/加速/桥接并存。
/// </summary>
internal static class DailyClaimExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterDailyClaim.dll.bytes";
    public const string TypeName = "SeqChapterDailyClaim";
    public const string EntryName = "OnShareClick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_daily_claim.dll";

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
                "用法: HotfixPatcher daily-claim-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher daily-claim-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher daily-claim-external-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "daily-claim" : "original");
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
            Console.WriteLine("[OK] 日常·分享入口补丁完成: " + output);
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
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var dllPath = BuildDailyClaimDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[DAILY] 已部署 " + assetOut);

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
        var onClickShare = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickShareCallback" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickShareCallback");

        // 始终重写分享入口（体积固定填充；便于更新 Tip 文案）
        BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
            onClickShare,
            asm.MainModule,
            DllAssetPath,
            TypeName,
            EntryName,
            TempDllSuffix,
            tipOn: "日常领取已开始",
            tipOff: "日常领取进行中",
            tipFail: "日常加载失败");
        Console.WriteLine("[DAILY] OnClickShareCallback -> OnShareClick（日常流水线）");

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);
        MetadataValidator.EnsureReadable(padded, hotfixDir);
        File.WriteAllBytes(outputPath, padded);

        var outBytes = File.ReadAllBytes(outputPath);
        var growth = (long)PeLayout.GetSection(outBytes, ".text").VirtualSize
                     - (long)PeLayout.GetSection(origBytes, ".text").VirtualSize;
        Console.WriteLine($"[DAILY] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
        HotfixSize.EnsureUnchanged(outBytes, expectedSize);
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
            if (!ContainsUtf16Le(pe, TypeName)
                && !ContainsUtf16Le(pe, AssetFileName)
                && !ContainsUtf16Le(pe, EntryName)
                && !ContainsAscii(pe, TypeName)
                && !ContainsAscii(pe, AssetFileName))
            {
                return false;
            }

            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var share = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?.Methods.FirstOrDefault(m => m.Name == "OnClickShareCallback" && m.HasBody);
            return share != null && IsShareClickPatched(share);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsUtf16Le(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var needle = System.Text.Encoding.Unicode.GetBytes(text);
        return IndexOfBytes(pe, needle) >= 0;
    }

    private static bool ContainsAscii(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        return IndexOfBytes(pe, needle) >= 0;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsShareClickPatched(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == TypeName || s == TypeName + ", " + TypeName || s == EntryName
                    || s == DllAssetPath || s.IndexOf("DailyClaim", StringComparison.Ordinal) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDailyClaimDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterDailyClaim.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterDailyClaim.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_daily_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterDailyClaim.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译日常 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterDailyClaim",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterDailyClaim 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[DAILY] 已编译日常 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_daily_claim")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_daily_claim")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_daily_claim")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_daily_claim")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_daily_claim")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_daily_claim")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_daily_claim");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterDailyClaim.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_daily_claim 目录（请把 seqchapter_daily_claim 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
    }
}
