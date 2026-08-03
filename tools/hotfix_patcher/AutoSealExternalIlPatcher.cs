using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 自动烧卡·DLL版：部署 SeqChapterAutoSeal.dll.bytes，
/// 钩 AutoFight_PlayerAction 与 DoVipPlayerAutoFight。
/// 默认另占 Pause/百科；<c>--panel</c> 时只打 DLL+战斗钩，给助手面板切换。
/// </summary>
internal static class AutoSealExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterAutoSeal.dll.bytes";
    public const string TypeName = "SeqChapterAutoSeal";
    public const string BootstrapName = "Bootstrap";
    public const string EntryName = "TryPlayerAutoSeal";
    public const string WikiEntryName = "OnWikiClick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_auto_seal.dll";

    private static bool _panelMode;

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
                "用法: HotfixPatcher auto-seal-external-patch --hotfix <orig> --output <out> [--panel]\n" +
                "      HotfixPatcher auto-seal-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher auto-seal-external-patch --hotfix <orig> --output <out> --restore");
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
            Console.WriteLine("[OK] 自动烧卡·DLL版补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath) =>
        Apply(sourcePath, outputPath, panelMode: false);

    public static void Apply(string sourcePath, string outputPath, bool panelMode)
    {
        _panelMode = panelMode;
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var dllPath = BuildAutoSealDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        var deployedNew = false;
        try
        {
            File.Copy(dllPath, assetOut, overwrite: true);
            deployedNew = true;
            Console.WriteLine("[AUTO-SEAL] 已部署 " + assetOut);

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
                    dllAssetPath: DllAssetPath,
                    typeName: TypeName,
                    bootstrapName: BootstrapName);
                BridgeLoaderIlBuilder.BuildQuitTriggersPauseBody(quitMethod, pauseMethod, asm.MainModule);
                BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);
            }
            else
            {
                Console.WriteLine("[AUTO-SEAL] 面板模式：跳过 Pause/百科（由助手面板加载）");
            }

            var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
            var playerAction = battleProcesser.Methods.First(
                m => m.Name == "AutoFight_PlayerAction" && m.HasBody && m.Parameters.Count == 0);
            InjectPlayerActionHook(playerAction, asm.MainModule);

            // VIP 路径：月卡 bypass 后开关开会走 DoVip*，不钩则烧卡永不触发
            var vipPlayer = battleProcesser.Methods.FirstOrDefault(
                m => m.Name == "DoVipPlayerAutoFight" && m.HasBody);
            if (vipPlayer != null)
            {
                InjectPlayerActionHook(vipPlayer, asm.MainModule, label: "VipPlayer");
            }
            else
            {
                Console.WriteLine("[AUTO-SEAL] 警告：未找到 DoVipPlayerAutoFight");
            }

            if (!_panelMode)
            {
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
                    TempDllSuffix,
                    tipOn: "自动烧卡已开启",
                    tipOff: "自动烧卡已关闭",
                    tipFail: "自动烧卡加载失败");
                Console.WriteLine("[AUTO-SEAL] OnClickWiki -> OnWikiClick + 原版 Tip");
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

            var outBytes = File.ReadAllBytes(outputPath);
            var growth = (long)PeLayout.GetSection(outBytes, ".text").VirtualSize
                         - (long)PeLayout.GetSection(origBytes, ".text").VirtualSize;
            Console.WriteLine($"[AUTO-SEAL] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
            HotfixSize.EnsureUnchanged(outBytes, expectedSize);
        }
        catch
        {
            // 校验/写出失败：删掉刚部署的 DLL，避免「有 DLL 无钩子」的假成功
            if (deployedNew)
            {
                try
                {
                    if (!File.Exists(outputPath) || !IsPatched(outputPath))
                    {
                        File.Delete(assetOut);
                        Console.WriteLine("[AUTO-SEAL] 失败回滚，已删除 " + assetOut);
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

    /// <summary>
    /// 方法入口：TryPlayerAutoSeal()==true 则 ret，否则走原逻辑。无 EH（HybridCLR 更稳）。
    /// </summary>
    private static void InjectPlayerActionHook(
        MethodDefinition method,
        ModuleDefinition module,
        string label = "PlayerAction")
    {
        if (IsPlayerActionHookInstalled(method))
        {
            Console.WriteLine($"[AUTO-SEAL] {label} 钩已存在，跳过");
            return;
        }

        var body = method.Body;
        if (body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(method.Name + " 无指令");
        }

        var il = body.GetILProcessor();
        var getType = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);

        var continueAt = body.Instructions[0];
        var haveType = il.Create(OpCodes.Nop);
        var haveMethod = il.Create(OpCodes.Nop);
        var unboxLabel = il.Create(OpCodes.Nop);

        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName),
            il.Create(OpCodes.Call, getType),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveType),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, continueAt),
            haveType,
            il.Create(OpCodes.Ldstr, EntryName),
            il.Create(OpCodes.Callvirt, getMethod),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveMethod),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, continueAt),
            haveMethod,
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Callvirt, invoke),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, unboxLabel),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, continueAt),
            unboxLabel,
            il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean),
            il.Create(OpCodes.Brfalse, continueAt),
            il.Create(OpCodes.Ret),
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine($"[AUTO-SEAL] 已注入 TryPlayerAutoSeal（{label}，无 EH）");
    }

    private static bool IsPlayerActionHookInstalled(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == EntryName || s == TypeName + ", " + TypeName))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var asset = Path.Combine(Path.GetDirectoryName(hotfixPath)!, AssetFileName);
            if (!File.Exists(asset))
            {
                return false;
            }

            var pe = File.ReadAllBytes(hotfixPath);
            var ascii = System.Text.Encoding.ASCII.GetString(pe);
            var uni = System.Text.Encoding.Unicode.GetString(pe);
            // 用户字符串堆常为 UTF-16，且可能奇数对齐；不能只靠 ASCII/Unicode.GetString
            if (!ascii.Contains(TypeName) && !uni.Contains(TypeName)
                && !ascii.Contains(AssetFileName)
                && !ContainsUtf16(pe, TypeName)
                && !ContainsUtf16(pe, AssetFileName)
                && !ContainsUtf16(pe, EntryName))
            {
                return false;
            }

            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

            var battleProcesser = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser");
            var playerAction = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PlayerAction" && m.HasBody && m.Parameters.Count == 0);
            var vipPlayer = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "DoVipPlayerAutoFight" && m.HasBody);
            return playerAction != null && IsPlayerActionHookInstalled(playerAction)
                   && (vipPlayer == null || IsPlayerActionHookInstalled(vipPlayer));
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAutoSealDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterAutoSeal.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterAutoSeal.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_autoseal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterAutoSeal.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译自动烧卡 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterAutoSeal",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterAutoSeal 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[AUTO-SEAL] 已编译自动烧卡 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        // 单文件发布时 BaseDirectory 在临时解压目录；ProcessPath 才是 exe 真实目录
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            // 傻瓜包 / 本机 patcher 旁随包源码
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_auto_seal")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_auto_seal")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_auto_seal")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_auto_seal")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_auto_seal")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_auto_seal")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_auto_seal");
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
            if (File.Exists(Path.Combine(dir, "SeqChapterAutoSeal.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_auto_seal 目录（请把 seqchapter_auto_seal 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
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
}
