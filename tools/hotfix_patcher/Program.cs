using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class Program
{
    private const string BootstrapTypeName = "CrossgateMod.ModBootstrap";
    private const string BootstrapMethodName = "Init";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "ildump")
        {
            return IlDump.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "findfield")
        {
            return FindField.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "ilbytes")
        {
            return IlBytesDump.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "binary-patch")
        {
            return BinaryIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "binary-mod")
        {
            return BinaryModPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "perfect-pet-patch")
        {
            return PerfectPetIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "battle-longpress-patch")
        {
            return BattleLongPressIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "battle-nine-external-patch")
        {
            return BattleNineActionExternalIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "battle-nine-action-patch")
        {
            return BattleNineActionIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "auto-seal-patch")
        {
            return AutoSealIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "area-time-patch")
        {
            return AreaTimeAddIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "vigor-add-area-time-patch")
        {
            return VigorAddAsAreaTimeIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "customer-gm-patch")
        {
            return CustomerBtnGmIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "vip-timescale-patch")
        {
            return VipTimeScaleIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "boss-challenge-tower-tab-patch")
        {
            return BossChallengeTowerTabIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "auto-battle-transmit-show-patch")
        {
            return AutoBattleTransmitShowIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "map-sprint-speed-patch")
        {
            return MapSprintSpeedIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "skill-effect-speed-patch")
        {
            return SkillEffectSpeedIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "auto-battle-transmit-bypass-patch")
        {
            return AutoBattleTransmitBypassIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "battle-nav-show-patch")
        {
            return BattleNavShowIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "mige-boss-patch")
        {
            return MigeBossPanelIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "mige-panel-patch")
        {
            return MigePanelIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "pet-equip-unlock-patch")
        {
            return PetEquipUnlockIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "pet-supplement-tab-patch")
        {
            return PetSupplementTabIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "pet-recycle-show-patch")
        {
            return PetRecycleShowIlPatcher.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "metadata-gaps")
        {
            return MetadataStreamGaps.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "metadata-slack")
        {
            return MetadataSlackProbe.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "import-probe")
        {
            return ImportTokenProbe.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "typeref-pool")
        {
            return TypeRefPoolProbe.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "scan-tokens")
        {
            return TokenScan.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "slack-report")
        {
            return SlackReport.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] == "helper-bridge-patch")
        {
            return HelperBridgeIlPatcher.Run(args.Skip(1).ToArray());
        }

        try
        {
            var options = PatchOptions.Parse(args);
            Run(options);
            Console.WriteLine("[OK] hotfix 已打补丁: " + options.OutputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Run(PatchOptions options)
    {
        if (!File.Exists(options.SourcePath))
        {
            throw new FileNotFoundException("找不到 hotfix 源文件", options.SourcePath);
        }

        if (!File.Exists(options.ModDllPath))
        {
            throw new FileNotFoundException("找不到 CrossgateMod.dll", options.ModDllPath);
        }

        var workDir = Path.Combine(Path.GetTempPath(), "crossgate_mod_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var patchedPath = Path.Combine(workDir, "patched.dll");
            PatchHotfix(options.SourcePath, options.ModDllPath, patchedPath);
            CopyWithRetry(patchedPath, options.OutputPath);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // ignore temp cleanup errors
            }
        }
    }

    private static void CopyWithRetry(string source, string dest)
    {
        const int maxAttempts = 8;
        IOException? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    Console.WriteLine($"[WAIT] 目标文件被占用，2 秒后重试 ({attempt}/{maxAttempts})...");
                    Thread.Sleep(2000);
                }
            }
        }

        var fallback = dest + ".patched";
        File.Copy(source, fallback, overwrite: true);
        Console.WriteLine("[OK] 已写入备用文件: " + fallback);
        throw new IOException(
            "无法写入 " + dest + "（文件被占用）。请关闭 cross.exe 后将 .patched 覆盖为 hotfix.dll.bytes，或重新运行应用Mod.bat。",
            lastError);
    }

    private static void PatchHotfix(string hotfixPath, string modDllPath, string outputPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(hotfixPath)!);
        foreach (var stubDir in ResolveRefStubDirs())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        var readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            InMemory = true,
        };

        using var assembly = AssemblyDefinition.ReadAssembly(hotfixPath, readerParams);
        ModTypeInjector.InjectTypes(assembly, modDllPath);
        InjectBootstrapCall(assembly);
        PatchValidator.Validate(assembly);
        assembly.Write(outputPath);
    }

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

    private static void InjectBootstrapCall(AssemblyDefinition assembly)
    {
        var module = assembly.MainModule;
        var bootstrap = module.Types.FirstOrDefault(t => t.FullName == BootstrapTypeName)
            ?? throw new InvalidOperationException("注入后未找到 " + BootstrapTypeName);

        var initMethod = bootstrap.Methods.FirstOrDefault(m => m.Name == BootstrapMethodName && m.IsStatic && m.HasBody)
            ?? throw new InvalidOperationException("未找到 ModBootstrap.Init");

        var gameManager = module.Types.FirstOrDefault(t => t.Name == "GameManagerHotfix")
            ?? throw new InvalidOperationException("未找到 GameManagerHotfix");

        var startMethod = gameManager.Methods.FirstOrDefault(m => m.Name == "Start" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 GameManagerHotfix.Start");

        if (MethodAlreadyCalls(startMethod, initMethod))
        {
            Console.WriteLine("[SKIP] 已存在 ModBootstrap.Init 调用");
            return;
        }

        var injectPoint = FindInitInjectionPoint(startMethod)
            ?? throw new InvalidOperationException("找不到 Start 内合适的 Init 注入点");

        var il = startMethod.Body.GetILProcessor();
        il.InsertBefore(injectPoint, il.Create(OpCodes.Call, initMethod));
        Console.WriteLine("[INJECT] GameManagerHotfix.Start -> ModBootstrap.Init (转场前)");
    }

    private static Instruction? FindInitInjectionPoint(MethodDefinition startMethod)
    {
        var instructions = startMethod.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            var insn = instructions[i];
            if (insn.OpCode != OpCodes.Call && insn.OpCode != OpCodes.Callvirt)
            {
                continue;
            }

            if (insn.Operand is not MethodReference called)
            {
                continue;
            }

            if (called.Name == "Dispatch" && called.DeclaringType.Name.Contains("MEvent"))
            {
                return insn;
            }
        }

        return instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ret);
    }

    private static bool MethodAlreadyCalls(MethodDefinition method, MethodReference target)
    {
        if (!method.HasBody)
        {
            return false;
        }

        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Call && insn.Operand is MethodReference called
                && called.FullName == target.FullName)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class PatchOptions
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required string ModDllPath { get; init; }

    public static PatchOptions Parse(string[] args)
    {
        string? source = null;
        string? output = null;
        string? mod = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotfix" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--mod" when i + 1 < args.Length:
                    mod = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(mod))
        {
            PrintHelp();
            throw new ArgumentException("必须指定 --hotfix 与 --mod");
        }

        source = Path.GetFullPath(source);
        output = string.IsNullOrWhiteSpace(output) ? source : Path.GetFullPath(output);

        return new PatchOptions
        {
            SourcePath = source,
            OutputPath = output,
            ModDllPath = Path.GetFullPath(mod),
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("用法: HotfixPatcher --hotfix <源hotfix> --mod <CrossgateMod.dll> [--output <目标hotfix>]");
    }
}
