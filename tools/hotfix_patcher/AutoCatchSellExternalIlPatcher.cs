using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 抓宠卖银币·DLL版：部署 SeqChapterAutoCatchSell.dll.bytes，
/// 并确保战斗钩为「卖银→无宠抓→普通抓」分发（与稳定抓宠共存，面板互斥切换）。
/// </summary>
internal static class AutoCatchSellExternalIlPatcher
{
    private static bool _panelMode;
    public const string AssetFileName = "SeqChapterAutoCatchSell.dll.bytes";
    public const string TypeName = "SeqChapterAutoCatchSell";
    public const string BootstrapName = "Bootstrap";
    public const string EntryName = "TryPlayerAutoCatch";
    public const string Player2EntryName = "TryPlayerAutoCatch2";
    public const string PetEntryName = "TryPetAutoCatch";
    public const string WikiEntryName = "OnWikiClick";
    public const string TempDllSuffix = "/seqchapter_auto_catch_sell.dll";
    private const string LogTag = "[AUTO-CATCH-SELL]";

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var restore = false;
        var detect = false;
        _panelMode = false;

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
                case "--panel":
                case "--hooks-only":
                    _panelMode = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher auto-catch-sell-external-patch --hotfix <orig> --output <out> [--panel]\n" +
                "      HotfixPatcher auto-catch-sell-external-patch --hotfix <file> --detect\n" +
                "      --panel：只部署 DLL + 战斗分发钩，不占百科/Pause");
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
            Apply(source, output, _panelMode);
            Console.WriteLine("[OK] 抓宠卖银币·DLL版补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath, bool panelMode = false)
    {
        _panelMode = panelMode;
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var dllPath = BuildDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine(LogTag + " 已部署 " + assetOut);

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        if (!_panelMode)
        {
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
                dllAssetPath: "hotfixdata/" + AssetFileName,
                typeName: TypeName,
                bootstrapName: BootstrapName);
            BridgeLoaderIlBuilder.BuildQuitTriggersPauseBody(quitMethod, pauseMethod, asm.MainModule);
            BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);
        }
        else
        {
            Console.WriteLine(LogTag + " 面板模式：跳过 Pause/百科（由助手面板加载）");
        }

        var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
        InjectDispatchExact(battleProcesser, asm.MainModule, "AutoFight_PlayerAction", EntryName, "PlayerAction", requireNoParams: true);
        InjectDispatchExact(battleProcesser, asm.MainModule, "AutoFight_PlayerAction2", Player2EntryName, "PlayerAction2", requireNoParams: true);
        InjectDispatchExact(battleProcesser, asm.MainModule, "AutoFight_PetAction", PetEntryName, "PetAction", requireNoParams: true);
        InjectDispatchExact(battleProcesser, asm.MainModule, "DoVipPlayerAutoFight", EntryName, "VipPlayer", requireNoParams: false);
        InjectDispatchExact(battleProcesser, asm.MainModule, "DoVipPetAutoFight", PetEntryName, "VipPet", requireNoParams: false);

        if (!_panelMode)
        {
            var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
            var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");
            BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
                onClickWiki,
                asm.MainModule,
                "hotfixdata/" + AssetFileName,
                TypeName,
                WikiEntryName,
                TempDllSuffix,
                tipOn: "抓宠卖银币已开启",
                tipOff: "抓宠卖银币已关闭",
                tipFail: "抓宠卖银币加载失败");
            Console.WriteLine(LogTag + " OnClickWiki -> OnWikiClick + 原版 Tip");
        }

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
        HotfixSize.EnsureUnchanged(File.ReadAllBytes(outputPath), expectedSize);

        if (!LevelOneIncludeAllIlPatcher.IsPatched(outputPath))
        {
            LevelOneIncludeAllIlPatcher.Apply(outputPath, outputPath);
            Console.WriteLine(LogTag + " 已附带：遇敌一级含哥布林/迷你蝙蝠");
        }
    }

    private static void InjectDispatchExact(
        TypeDefinition battleProcesser,
        ModuleDefinition module,
        string methodName,
        string entryName,
        string label,
        bool requireNoParams)
    {
        var method = battleProcesser.Methods.FirstOrDefault(
            m => m.Name == methodName && m.HasBody
                 && (!requireNoParams || m.Parameters.Count == 0));
        if (method == null)
        {
            Console.WriteLine(LogTag + " 警告：未找到 " + methodName);
            return;
        }

        CatchBattleDispatchIl.EnsureDispatchHook(method, module, entryName, label);
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

    private static string BuildDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterAutoCatchSell.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterAutoCatchSell.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_autocatch_sell_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, TypeName + ".dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译抓宠卖银币 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            CSharpParseOptions.Default,
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            TypeName,
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
            throw new InvalidOperationException("Roslyn 编译 " + TypeName + " 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"{LogTag} 已编译 {TypeName}（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;

        // 优先：从 hotfixPath 所在目录向上探测游戏根目录（含 cg37_Data 的目录）下的 tools/seqchapter_auto_catch_sell。
        // 这是唯一权威源；exeDir 同级的副本（发布残留）不能优先，否则会编译到旧版源码。
        var probes = new List<string>();
        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            probes.Add(Path.Combine(dir, "tools", "seqchapter_auto_catch_sell"));

            if (Directory.Exists(Path.Combine(dir, "cg37_Data")))
            {
                break;
            }
        }

        // 兜底：exeDir 相关路径（发布工具链场景，exe 与 tools 目录结构固定）。
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "seqchapter_auto_catch_sell")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_auto_catch_sell")));
        probes.Add(Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_auto_catch_sell")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_auto_catch_sell")));
        probes.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_auto_catch_sell")));

        foreach (var dir in probes)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterAutoCatchSell.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_auto_catch_sell 目录（请把 seqchapter_auto_catch_sell 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
    }

}
