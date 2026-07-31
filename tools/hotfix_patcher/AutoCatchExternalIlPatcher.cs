using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 自动抓宠·DLL版：Pause 加载 SeqChapterAutoCatch.dll.bytes（或无宠人防 SeqChapterAutoCatchNoPet.dll.bytes），
/// 钩 AutoFight_PlayerAction / AutoFight_PetAction，以及月卡 VIP 路径
/// DoVipPlayerAutoFight / DoVipPetAutoFight（否则 VIP 自动技会绕过抓宠防御）。
/// 并将 MapSidebarPanel.OnClickWiki 改为 OnWikiClick（点百科 Tip 切换流水线开/关）。
/// 与烧封印/助手桥接/神奇九动·DLL版互斥（共用 OnApplicationPause）；可与 IL 九动共存。
/// </summary>
internal static class AutoCatchExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterAutoCatch.dll.bytes";
    public const string TypeName = "SeqChapterAutoCatch";
    public const string NoPetAssetFileName = "SeqChapterAutoCatchNoPet.dll.bytes";
    public const string NoPetTypeName = "SeqChapterAutoCatchNoPet";
    public const string BootstrapName = "Bootstrap";
    public const string EntryName = "TryPlayerAutoCatch";
    public const string Player2EntryName = "TryPlayerAutoCatch2";
    public const string PetEntryName = "TryPetAutoCatch";
    public const string WikiEntryName = "OnWikiClick";
    public const string TempDllSuffix = "/seqchapter_auto_catch.dll";

    private static bool _noPetHumanDefend;
    private static string ActiveAssetFileName => _noPetHumanDefend ? NoPetAssetFileName : AssetFileName;
    private static string ActiveTypeName => _noPetHumanDefend ? NoPetTypeName : TypeName;
    private static string ActiveDllAssetPath => "hotfixdata/" + ActiveAssetFileName;
    private static string LogTag => _noPetHumanDefend ? "[AUTO-CATCH-NOPET]" : "[AUTO-CATCH]";

    public static int RunNopet(string[] args) => RunCore(args, noPetHumanDefend: true);

    public static int Run(string[] args) => RunCore(args, noPetHumanDefend: false);

    private static int RunCore(string[] args, bool noPetHumanDefend)
    {
        _noPetHumanDefend = noPetHumanDefend;
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
                case "--nopet":
                    _noPetHumanDefend = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            var cmd = _noPetHumanDefend ? "auto-catch-nopet-external-patch" : "auto-catch-external-patch";
            Console.WriteLine(
                $"用法: HotfixPatcher {cmd} --hotfix <orig> --output <out>\n" +
                $"      HotfixPatcher {cmd} --hotfix <file> --detect\n" +
                $"      HotfixPatcher {cmd} --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source, _noPetHumanDefend);
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
            Apply(source, output, _noPetHumanDefend);
            Console.WriteLine(_noPetHumanDefend
                ? "[OK] 自动抓宠·无宠人防御 补丁完成: " + output
                : "[OK] 自动抓宠·DLL版补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath) =>
        Apply(sourcePath, outputPath, noPetHumanDefend: false);

    public static void Apply(string sourcePath, string outputPath, bool noPetHumanDefend)
    {
        _noPetHumanDefend = noPetHumanDefend;
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var dllPath = BuildAutoCatchDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, ActiveAssetFileName);
        var deployedNew = false;
        try
        {
            File.Copy(dllPath, assetOut, overwrite: true);
            deployedNew = true;
            Console.WriteLine(LogTag + " 已部署 " + assetOut);

            var other = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                _noPetHumanDefend ? AssetFileName : NoPetAssetFileName);
            if (File.Exists(other))
            {
                File.Delete(other);
                Console.WriteLine(LogTag + " 已删除互斥 " + Path.GetFileName(other));
            }

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

            // Pause / 百科统一 Assembly.Load(bytes)（与助手桥接相同），避免 LoadFrom 落盘失败静默无反应
            BridgeLoaderIlBuilder.BuildLoaderBodyInPlace(
                pauseMethod,
                asm.MainModule,
                userStrings,
                skipIfTypeLoaded: true,
                dllAssetPath: ActiveDllAssetPath,
                typeName: ActiveTypeName,
                bootstrapName: BootstrapName);
            BridgeLoaderIlBuilder.BuildQuitTriggersPauseBody(quitMethod, pauseMethod, asm.MainModule);
            BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);

            var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
            var playerAction = battleProcesser.Methods.First(
                m => m.Name == "AutoFight_PlayerAction" && m.HasBody && m.Parameters.Count == 0);
            InjectBoolHook(playerAction, asm.MainModule, EntryName, "PlayerAction");
            // 无宠时 2动走 AutoFight_PlayerAction2（原先未钩 → 原版自动攻击）
            var playerAction2 = battleProcesser.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PlayerAction2" && m.HasBody && m.Parameters.Count == 0);
            if (playerAction2 != null)
            {
                InjectBoolHook(playerAction2, asm.MainModule, Player2EntryName, "PlayerAction2");
            }
            else
            {
                Console.WriteLine(LogTag + " 警告：未找到 AutoFight_PlayerAction2");
            }

            var petAction = battleProcesser.Methods.First(
                m => m.Name == "AutoFight_PetAction" && m.HasBody && m.Parameters.Count == 0);
            InjectBoolHook(petAction, asm.MainModule, PetEntryName, "PetAction");

            var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
            var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
                ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");
            var tipOn = _noPetHumanDefend ? "自动抓宠(无宠人防)已开启" : "自动抓宠已开启";
            var tipOff = _noPetHumanDefend ? "自动抓宠(无宠人防)已关闭" : "自动抓宠已关闭";
            var tipFail = _noPetHumanDefend ? "自动抓宠(无宠人防)加载失败" : "自动抓宠加载失败";
            BridgeLoaderIlBuilder.BuildLoadAndAlwaysInvokeBody(
                onClickWiki,
                asm.MainModule,
                ActiveDllAssetPath,
                ActiveTypeName,
                WikiEntryName,
                TempDllSuffix,
                tipOn: tipOn,
                tipOff: tipOff,
                tipFail: tipFail);
            Console.WriteLine(LogTag + " OnClickWiki -> OnWikiClick + 原版 Tip");

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
            Console.WriteLine($"{LogTag} .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
            HotfixSize.EnsureUnchanged(outBytes, expectedSize);

            // 原版不给哥布林打 LevelOneFlag；抓宠依赖「一级含哥布林」或 Level==1 兜底
            if (!LevelOneIncludeAllIlPatcher.IsPatched(outputPath))
            {
                LevelOneIncludeAllIlPatcher.Apply(outputPath, outputPath);
                Console.WriteLine(LogTag + " 已附带：遇敌一级含哥布林/迷你蝙蝠");
            }
        }
        catch
        {
            if (deployedNew)
            {
                try
                {
                    if (!File.Exists(outputPath) || !IsPatched(outputPath, _noPetHumanDefend))
                    {
                        File.Delete(assetOut);
                        Console.WriteLine(LogTag + " 失败回滚，已删除 " + assetOut);
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
    /// 方法入口：entry()==true 则 ret，否则走原逻辑。无 EH（HybridCLR 更稳）。
    /// </summary>
    private static void InjectBoolHook(
        MethodDefinition method,
        ModuleDefinition module,
        string entryName,
        string label)
    {
        if (IsHookInstalled(method, entryName))
        {
            Console.WriteLine($"{LogTag} {label} 钩已存在，跳过");
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
            il.Create(OpCodes.Ldstr, ActiveTypeName + ", " + ActiveTypeName),
            il.Create(OpCodes.Call, getType),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveType),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, continueAt),
            haveType,
            il.Create(OpCodes.Ldstr, entryName),
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
        Console.WriteLine($"{LogTag} 已注入 {entryName}（{label}，无 EH）");
    }

    private static bool IsHookInstalled(MethodDefinition method, string entryName)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == entryName || s == ActiveTypeName + ", " + ActiveTypeName))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPatched(string hotfixPath) => IsPatched(hotfixPath, noPetHumanDefend: false);

    public static bool IsPatched(string hotfixPath, bool noPetHumanDefend)
    {
        _noPetHumanDefend = noPetHumanDefend;
        try
        {
            // DLL 丢失时百科/Pause 会静默失败，必须视为未打齐
            var asset = Path.Combine(Path.GetDirectoryName(hotfixPath)!, ActiveAssetFileName);
            if (!File.Exists(asset))
            {
                return false;
            }

            var pe = File.ReadAllBytes(hotfixPath);
            var ascii = System.Text.Encoding.ASCII.GetString(pe);
            var uni = System.Text.Encoding.Unicode.GetString(pe);
            var typeName = ActiveTypeName;
            if (!ascii.Contains(typeName) && !uni.Contains(typeName)
                && !ascii.Contains(ActiveAssetFileName)
                && !ContainsUtf16(pe, typeName))
            {
                return false;
            }

            // 整文件 Unicode.GetString 可能错位漏检；用字节级 UTF-16 搜用户字符串
            if (!ContainsUtf16(pe, "OnWikiClick") && !ascii.Contains("OnWikiClick") && !uni.Contains("OnWikiClick"))
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

            var battleProcesser = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser");
            var playerAction = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PlayerAction" && m.HasBody && m.Parameters.Count == 0);
            var playerAction2 = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PlayerAction2" && m.HasBody && m.Parameters.Count == 0);
            var petAction = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PetAction" && m.HasBody && m.Parameters.Count == 0);
            return playerAction != null && IsHookInstalled(playerAction, EntryName)
                   && petAction != null && IsHookInstalled(petAction, PetEntryName)
                   && (playerAction2 == null || IsHookInstalled(playerAction2, Player2EntryName));
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAutoCatchDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterAutoCatch.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterAutoCatch.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_autocatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var asmName = ActiveTypeName;
        var dllPath = Path.Combine(outDir, asmName + ".dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译自动抓宠 DLL");
        }

        var parseOpts = CSharpParseOptions.Default;
        if (_noPetHumanDefend)
        {
            parseOpts = parseOpts.WithPreprocessorSymbols("AUTO_CATCH_NOPET");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            parseOpts,
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            asmName,
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
            throw new InvalidOperationException("Roslyn 编译 " + asmName + " 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"{LogTag} 已编译 {asmName}（{refs.Count} 个引用）");
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
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_auto_catch")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_auto_catch")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_auto_catch")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_auto_catch")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_auto_catch")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_auto_catch")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_auto_catch");
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
            if (File.Exists(Path.Combine(dir, "SeqChapterAutoCatch.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_auto_catch 目录（请把 seqchapter_auto_catch 放在 HotfixPatcher.exe 同级，或游戏根目录 tools/ 下）");
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
