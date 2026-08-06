using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 插件 Host 第一期：Pause 加载 SeqChapterPluginHost.dll.bytes，
/// MapSidebarPanel.OnClickWiki → OnWikiClick（打开自绘面板，无 Tip 开关）。
/// 与现有扩展 DLL（九动/烧卡/抓宠/盗贼/桥接）及百科其它改写互斥（共用 Pause / 百科）。
/// </summary>
internal static class PluginHostIlPatcher
{
    public const string AssetFileName = "SeqChapterPluginHost.dll.bytes";
    public const string TypeName = "SeqChapterPluginHost";
    public const string BootstrapName = "Bootstrap";
    public const string WikiEntryName = "OnWikiClick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_plugin_host.dll";

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
                "用法: HotfixPatcher plugin-host-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher plugin-host-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher plugin-host-patch --hotfix <orig> --output <out> --restore");
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
            Console.WriteLine("[OK] 插件 Host 补丁完成: " + output);
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

        var dllPath = BuildHostDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        var deployedNew = false;
        try
        {
            File.Copy(dllPath, assetOut, overwrite: true);
            deployedNew = true;
            Console.WriteLine("[PLUGIN-HOST] 已部署 " + assetOut);

            var hotfixDir = Path.GetDirectoryName(sourcePath)!;
            var resolver = new HotfixAssemblyResolver(hotfixDir);
            using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadWrite = true,
            });

            var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");
            var pauseMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
            var quitMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationQuit" && m.HasBody);
            var entryStartMethod = hotfixEntry.Methods.First(m => m.Name == "Start" && m.HasBody);
            var userStrings = UserStringHeap.FromPe(origBytes);

            BridgeLoaderIlBuilder.BuildLoaderBodyInPlace(
                pauseMethod,
                asm.MainModule,
                userStrings,
                skipIfTypeLoaded: true,
                dllAssetPath: DllAssetPath,
                typeName: TypeName,
                bootstrapName: BootstrapName);
            BridgeLoaderIlBuilder.BuildQuitTriggersPauseBody(quitMethod, pauseMethod, asm.MainModule);
            BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);
            Console.WriteLine("[PLUGIN-HOST] Pause -> SeqChapterPluginHost.Bootstrap");

            var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
            var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");
            BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
                onClickWiki,
                asm.MainModule,
                DllAssetPath,
                TypeName,
                WikiEntryName,
                TempDllSuffix);
            Console.WriteLine("[PLUGIN-HOST] OnClickWiki -> OnWikiClick（自绘面板）");

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
            Console.WriteLine($"[PLUGIN-HOST] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
            HotfixSize.EnsureUnchanged(outBytes, expectedSize);
        }
        catch
        {
            if (deployedNew)
            {
                try
                {
                    if (!File.Exists(outputPath) || !IsPatched(outputPath))
                    {
                        File.Delete(assetOut);
                        Console.WriteLine("[PLUGIN-HOST] 失败回滚，已删除 " + assetOut);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            throw;
        }
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
            if (!ContainsUtf16(pe, TypeName)
                && !ContainsUtf16(pe, AssetFileName)
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
            var pause = asm.MainModule.Types.First(t => t.Name == "HotfixEntry")
                .Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
            if (pause.Body.Instructions.Count <= 8)
            {
                return false;
            }

            var wiki = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody);
            return wiki != null && wiki.Body.Instructions.Count > 8;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildHostDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterPluginHost.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterPluginHost.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_plugin_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterPluginHost.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译插件 Host DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterPluginHost",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterPluginHost 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[PLUGIN-HOST] 已编译 Host DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;

        // 优先：从 hotfixPath 所在目录向上探测游戏根目录（含 cg37_Data 的目录）下的 tools/seqchapter_plugin_host。
        // 这是唯一权威源；exeDir 同级的副本（发布残留）不能优先，否则会编译到旧版源码。
        var probes = new List<string>();
        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            probes.Add(Path.Combine(dir, "tools", "seqchapter_plugin_host"));

            if (Directory.Exists(Path.Combine(dir, "cg37_Data")))
            {
                break;
            }
        }

        // 兜底：exeDir 相关路径（发布工具链场景，exe 与 tools 目录结构固定）。
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "seqchapter_plugin_host")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_plugin_host")));
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_plugin_host")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_plugin_host")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_plugin_host")));

        foreach (var dir in probes)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterPluginHost.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_plugin_host 目录（请把 seqchapter_plugin_host 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
    }


    private static bool ContainsUtf16(byte[] pe, string text)
    {
        var needle = System.Text.Encoding.Unicode.GetBytes(text);
        if (needle.Length == 0 || pe.Length < needle.Length)
        {
            return false;
        }

        var end = pe.Length - needle.Length;
        for (var i = 0; i <= end; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (pe[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAscii(byte[] pe, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        if (needle.Length == 0 || pe.Length < needle.Length)
        {
            return false;
        }

        var end = pe.Length - needle.Length;
        for (var i = 0; i <= end; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (pe[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }
}
