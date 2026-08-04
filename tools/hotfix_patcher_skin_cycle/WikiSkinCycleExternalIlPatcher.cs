using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 傻瓜皮肤补丁专用（独立工程维护，不改主 HotfixPatcher）。
/// MapSidebarPanel.OnClickWiki → 加载 SeqChapterWikiSkinCycle.dll.bytes 并调用 OnWikiClick。
/// </summary>
internal static class WikiSkinCycleExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterWikiSkinCycle.dll.bytes";
    public const string TypeName = "SeqChapterWikiSkinCycle";
    public const string EntryName = "OnWikiClick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_wiki_skin_cycle.dll";

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
                "用法: HotfixPatcherSkinCycle wiki-skin-cycle-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcherSkinCycle wiki-skin-cycle-patch --hotfix <file> --detect\n" +
                "      HotfixPatcherSkinCycle wiki-skin-cycle-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "wiki-skin-cycle" : "original");
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
            Console.WriteLine("[OK] 百科皮肤循环补丁完成: " + output);
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

        var dllPath = BuildSkinCycleDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[SKIN-CYCLE] 已部署 " + assetOut);

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

        // 不传 tipOn/tipOff：由 DLL 内 Tip「已切换皮肤方案N」
        BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
            onClickWiki,
            asm.MainModule,
            DllAssetPath,
            TypeName,
            EntryName,
            TempDllSuffix);
        Console.WriteLine("[SKIN-CYCLE] OnClickWiki -> OnWikiClick（百科循环皮肤方案）");

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
        Console.WriteLine($"[SKIN-CYCLE] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
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
                    || s == DllAssetPath
                    || s.IndexOf("WikiSkinCycle", StringComparison.Ordinal) >= 0
                    || s.IndexOf("皮肤方案", StringComparison.Ordinal) >= 0))
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

    private static string BuildSkinCycleDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterWikiSkinCycle.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterWikiSkinCycle.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_wikiskin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterWikiSkinCycle.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译百科皮肤循环 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterWikiSkinCycle",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterWikiSkinCycle 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[SKIN-CYCLE] 已编译百科皮肤循环 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_wiki_skin_cycle")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_wiki_skin_cycle")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_wiki_skin_cycle")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_wiki_skin_cycle")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_wiki_skin_cycle")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_wiki_skin_cycle")),
        };

        for (var dir = hotfixDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)!)
        {
            var probe = Path.Combine(dir, "tools", "seqchapter_wiki_skin_cycle");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterWikiSkinCycle.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_wiki_skin_cycle 目录");
    }
}
