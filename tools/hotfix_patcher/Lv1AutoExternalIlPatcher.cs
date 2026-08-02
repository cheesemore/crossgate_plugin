using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 遇1级自动·DLL版：Pause 加载 SeqChapterLv1Auto.dll.bytes，
/// 钩 AutoFight_PlayerAction / PlayerAction2 / PetAction，
/// 以及 DoVipPlayerAutoFight / DoVipPetAutoFight（防 VIP 路径绕过）。
/// MapSidebarPanel.OnClickWiki → OnWikiClick。
/// 附带 level-one-include-all（哥布林/蝙蝠也打 LevelOneFlag）。
/// 与烧卡/抓宠/九动DLL/桥接互斥（共用 OnApplicationPause）。
/// </summary>
internal static class Lv1AutoExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterLv1Auto.dll.bytes";
    public const string TypeName = "SeqChapterLv1Auto";
    public const string BootstrapName = "Bootstrap";
    public const string EntryName = "TryPlayerLv1Auto";
    public const string Player2EntryName = "TryPlayerLv1Auto2";
    public const string PetEntryName = "TryPetLv1Auto";
    public const string WikiEntryName = "OnWikiClick";
    public const string TempDllSuffix = "/seqchapter_lv1_auto.dll";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    private const string LogTag = "[LV1-AUTO]";

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
                "用法: HotfixPatcher lv1-auto-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher lv1-auto-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher lv1-auto-external-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            Console.WriteLine(IsPatched(source) ? "patched" : "not_patched");
            return IsPatched(source) ? 0 : 1;
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
            Console.WriteLine("[OK] 遇1级自动·DLL版补丁完成: " + output);
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

        var dllPath = BuildDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        var deployedNew = false;
        try
        {
            File.Copy(dllPath, assetOut, overwrite: true);
            deployedNew = true;
            Console.WriteLine(LogTag + " 已部署 " + assetOut);

            // 与抓宠/烧卡 DLL 互斥：同目录删掉对方资产，避免误开
            foreach (var other in new[]
                     {
                         "SeqChapterAutoCatch.dll.bytes",
                         "SeqChapterAutoCatchNoPet.dll.bytes",
                         "SeqChapterAutoSeal.dll.bytes",
                     })
            {
                var p = Path.Combine(Path.GetDirectoryName(outputPath)!, other);
                if (File.Exists(p))
                {
                    File.Delete(p);
                    Console.WriteLine(LogTag + " 已删除互斥 " + other);
                }
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

            var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
            InjectBoolHook(
                battleProcesser.Methods.First(
                    m => m.Name == "AutoFight_PlayerAction" && m.HasBody && m.Parameters.Count == 0),
                asm.MainModule,
                EntryName,
                "PlayerAction");

            var playerAction2 = battleProcesser.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PlayerAction2" && m.HasBody && m.Parameters.Count == 0);
            if (playerAction2 != null)
            {
                InjectBoolHook(playerAction2, asm.MainModule, Player2EntryName, "PlayerAction2");
            }

            InjectBoolHook(
                battleProcesser.Methods.First(
                    m => m.Name == "AutoFight_PetAction" && m.HasBody && m.Parameters.Count == 0),
                asm.MainModule,
                PetEntryName,
                "PetAction");

            // VIP 路径：入口同样 bool 钩（忽略原方法参数）
            var vipPlayer = battleProcesser.Methods.FirstOrDefault(
                m => m.Name == "DoVipPlayerAutoFight" && m.HasBody);
            if (vipPlayer != null)
            {
                InjectBoolHook(vipPlayer, asm.MainModule, EntryName, "VipPlayer");
            }
            else
            {
                Console.WriteLine(LogTag + " 警告：未找到 DoVipPlayerAutoFight");
            }

            var vipPet = battleProcesser.Methods.FirstOrDefault(
                m => m.Name == "DoVipPetAutoFight" && m.HasBody);
            if (vipPet != null)
            {
                InjectBoolHook(vipPet, asm.MainModule, PetEntryName, "VipPet");
            }
            else
            {
                Console.WriteLine(LogTag + " 警告：未找到 DoVipPetAutoFight");
            }

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
                tipOn: "遇1级自动已开启",
                tipOff: "遇1级自动已关闭",
                tipFail: "遇1级自动加载失败");
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
                    if (!File.Exists(outputPath) || !IsPatched(outputPath))
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
            il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName),
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
                && (s == entryName || s == TypeName + ", " + TypeName))
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
            if (!ascii.Contains(TypeName) && !ContainsUtf16(pe, TypeName)
                && !ascii.Contains(AssetFileName))
            {
                return false;
            }

            if (!ContainsUtf16(pe, "OnWikiClick") && !ascii.Contains("OnWikiClick"))
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
            var petAction = battleProcesser?.Methods.FirstOrDefault(
                m => m.Name == "AutoFight_PetAction" && m.HasBody && m.Parameters.Count == 0);
            return playerAction != null && IsHookInstalled(playerAction, EntryName)
                   && petAction != null && IsHookInstalled(petAction, PetEntryName);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterLv1Auto.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterLv1Auto.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_lv1_" + Guid.NewGuid().ToString("N"));
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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译遇1级自动 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
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
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_lv1_auto")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_lv1_auto")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_lv1_auto")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_lv1_auto")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_lv1_auto")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_lv1_auto");
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
            if (File.Exists(Path.Combine(dir, "SeqChapterLv1Auto.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_lv1_auto 目录（请把源码放在 HotfixPatcher.exe 同级或游戏根 tools/ 下）");
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
