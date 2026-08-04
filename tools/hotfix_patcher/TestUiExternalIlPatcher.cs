using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 百科→助手面板：OnClickWiki → 加载 SeqChapterTestUi.dll.bytes 并调用 OnWikiClick。
/// 抓宠/烧卡/九动等由面板内切换（DLL 面板模式部署）；勿再独占百科 Tip。
/// </summary>
internal static class TestUiExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterTestUi.dll.bytes";
    public const string TypeName = "SeqChapterTestUi";
    public const string EntryName = "OnWikiClick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_test_ui.dll";

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
                case "--dll-only":
                    break;
            }
        }

        var dllOnly = args.Any(a => a == "--dll-only");

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher wiki-test-ui-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher wiki-test-ui-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher wiki-test-ui-patch --hotfix <file> --dll-only\n" +
                "      HotfixPatcher wiki-test-ui-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "wiki-test-ui" : "original");
            return patched ? 0 : 1;
        }

        if (restore)
        {
            File.Copy(source, output, overwrite: true);
            Console.WriteLine("[RESTORE] 已从原版复制: " + output);
            return 0;
        }

        if (dllOnly)
        {
            try
            {
                var dllPath = BuildTestUiDll(source);
                var assetOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, AssetFileName);
                File.Copy(dllPath, assetOut, overwrite: true);
                Console.WriteLine("[OK] 已重编译并部署 " + assetOut);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex.Message);
                return 1;
            }
        }

        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 百科→助手面板 补丁完成: " + output);
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

        var dllPath = BuildTestUiDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[TEST-UI] 已部署 " + assetOut);

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
        var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");

        BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
            onClickWiki,
            asm.MainModule,
            DllAssetPath,
            TypeName,
            EntryName,
            TempDllSuffix,
            tipOn: "助手面板已打开",
            tipOff: "助手面板已关闭",
            tipFail: "助手面板加载失败");
        Console.WriteLine("[HELPER] OnClickWiki -> 助手面板");

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
        Console.WriteLine($"[TEST-UI] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
        HotfixSize.EnsureUnchanged(outBytes, expectedSize);
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
            if (!ContainsUtf16Le(pe, TypeName)
                && !ContainsUtf16Le(pe, AssetFileName)
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
            var wiki = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody);
            return wiki != null && IsWikiClickPatched(wiki);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWikiClickPatched(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == TypeName || s == TypeName + ", " + TypeName || s == EntryName
                    || s == DllAssetPath || s.IndexOf("TestUi", StringComparison.Ordinal) >= 0
                    || s.IndexOf("测试UI", StringComparison.Ordinal) >= 0
                    || s.IndexOf("助手面板", StringComparison.Ordinal) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUtf16Le(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return IndexOfBytes(pe, System.Text.Encoding.Unicode.GetBytes(text)) >= 0;
    }

    private static bool ContainsAscii(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return IndexOfBytes(pe, System.Text.Encoding.ASCII.GetBytes(text)) >= 0;
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

    private static string BuildTestUiDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterTestUi.cs");
        var stubPath = Path.Combine(srcDir, "UnityEngine.Stub.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterTestUi.cs", csPath);
        }

        if (!File.Exists(stubPath))
        {
            throw new FileNotFoundException("找不到 UnityEngine.Stub.cs", stubPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_testui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var stubDll = Path.Combine(outDir, "UnityEngine.dll");
        var dllPath = Path.Combine(outDir, "SeqChapterTestUi.dll");

        var runtimeRefs = new List<MetadataReference>();
        foreach (var name in new[] { "mscorlib.dll.bytes", "system.dll.bytes", "system.core.dll.bytes" })
        {
            var path = Path.Combine(hotfixDataDir, name);
            if (File.Exists(path))
            {
                runtimeRefs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        if (runtimeRefs.Count == 0)
        {
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译助手面板 DLL");
        }

        // 1) 单独编译 UnityEngine 桩程序集（运行时由游戏真 Unity 解析同名引用）
        var stubTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(stubPath),
            path: stubPath,
            encoding: System.Text.Encoding.UTF8);
        var stubCompile = CSharpCompilation.Create(
            "UnityEngine",
            new[] { stubTree },
            runtimeRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default));
        using (var stubMs = new MemoryStream())
        {
            var stubResult = stubCompile.Emit(stubMs);
            if (!stubResult.Success)
            {
                var errors = string.Join(
                    Environment.NewLine,
                    stubResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                throw new InvalidOperationException("Roslyn 编译 UnityEngine 桩失败:\n" + errors);
            }

            File.WriteAllBytes(stubDll, stubMs.ToArray());
        }

        // 2) 编译测试 UI（引用桩 UnityEngine.dll，勿把桩源码编进同一程序集）
        // 同目录下除 UnityEngine.Stub.cs 外的全部 .cs（含 BossStatEstimator 等）
        var refs = new List<MetadataReference>(runtimeRefs)
        {
            MetadataReference.CreateFromFile(stubDll),
        };
        var csFiles = Directory.GetFiles(srcDir, "*.cs")
            .Where(p => !string.Equals(Path.GetFileName(p), "UnityEngine.Stub.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (csFiles.Length == 0)
        {
            throw new FileNotFoundException("seqchapter_test_ui 下无 .cs", srcDir);
        }

        var trees = csFiles.Select(p => CSharpSyntaxTree.ParseText(
            File.ReadAllText(p),
            path: p,
            encoding: System.Text.Encoding.UTF8)).ToArray();
        var compile = CSharpCompilation.Create(
            "SeqChapterTestUi",
            trees,
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterTestUi 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[HELPER] 已编译助手面板 DLL（{refs.Count} 个引用，含 UnityEngine 桩）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_test_ui")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_test_ui")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_test_ui")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_test_ui")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_test_ui")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_test_ui")),
        };

        for (var dir = hotfixDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)!)
        {
            var probe = Path.Combine(dir, "tools", "seqchapter_test_ui");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterTestUi.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException("找不到 tools/seqchapter_test_ui 目录");
    }
}
