using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 神奇九动（DLL 版）：Magics 原地 + 加载 SeqChapterNineAction.dll.bytes。
/// OnCommandPlayerCallback 末尾同步 Invoke ExpandAccountList（不依赖 Timer）。
/// 与 IL 整法九动、助手桥接互斥（共用 OnApplicationPause / .text 余量）。
/// </summary>
internal static class BattleNineActionExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterNineAction.dll.bytes";
    public const string TypeName = "SeqChapterNineAction";
    public const string BootstrapName = "Bootstrap";
    public const string ExpandName = "ExpandAccountList";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;

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
                "用法: HotfixPatcher battle-nine-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher battle-nine-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher battle-nine-external-patch --hotfix <orig> --output <out> --restore");
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
            Console.WriteLine("[OK] 神奇九动·DLL版补丁完成: " + output);
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

        var dllPath = BuildNineDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[NINE-EXT] 已部署 " + assetOut);

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
        var gameManager = asm.MainModule.Types.First(t => t.Name == "GameManagerHotfix");
        var gmStartMethod = gameManager.Methods.First(m => m.Name == "Start" && m.HasBody);
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
        BridgeLoaderIlBuilder.ApplyGameManagerStartHook(gmStartMethod.Body, pauseMethod);

        var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
        var onCommandPlayer = battleProcesser.Methods.First(m => m.Name == "OnCommandPlayerCallback" && m.HasBody);
        InjectExpandHook(onCommandPlayer, asm.MainModule);
        Console.WriteLine("[NINE-EXT] OnCommandPlayerCallback 末尾同步 ExpandAccountList");

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

        var tmpMagics = Path.Combine(Path.GetTempPath(), "nine_ext_" + Guid.NewGuid().ToString("N") + ".bytes");
        try
        {
            File.WriteAllBytes(tmpMagics, padded);
            BattleNineActionIlPatcher.Apply(tmpMagics, outputPath, patchQueue: false, patchMagics: true);
        }
        finally
        {
            try { File.Delete(tmpMagics); } catch { /* ignore */ }
        }

        var outBytes = File.ReadAllBytes(outputPath);
        var growth = (long)PeLayout.GetSection(outBytes, ".text").VirtualSize
                     - (long)PeLayout.GetSection(origBytes, ".text").VirtualSize;
        Console.WriteLine($"[NINE-EXT] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}（钩子+Magics）");
        HotfixSize.EnsureUnchanged(outBytes, expectedSize);
    }

    /// <summary>
    /// 在每个 ret 前反射调用 OnCommandPlayerEnd；try/catch + leave，避免 HybridCLR 因异常闪退。
    /// </summary>
    private static void InjectExpandHook(MethodDefinition method, ModuleDefinition module)
    {
        if (IsExpandHookInstalled(method))
        {
            Console.WriteLine("[NINE-EXT] Expand 钩已存在，跳过");
            return;
        }

        var body = method.Body;
        var il = body.GetILProcessor();
        var getType = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);
        var exceptionType = new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary);

        const string entryName = "OnCommandPlayerEnd";

        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count == 0)
        {
            throw new InvalidOperationException("OnCommandPlayerCallback 无 ret");
        }

        foreach (var ret in rets)
        {
            var leaveTarget = il.Create(OpCodes.Nop);
            var haveType = il.Create(OpCodes.Nop);
            var haveMethod = il.Create(OpCodes.Nop);

            var tryStart = il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName);
            var callGetType = il.Create(OpCodes.Call, getType);
            var dupType = il.Create(OpCodes.Dup);
            var brTrueType = il.Create(OpCodes.Brtrue, haveType);
            var popTypeNull = il.Create(OpCodes.Pop);
            var leaveIfNoType = il.Create(OpCodes.Leave, leaveTarget);
            var ldstrEntry = il.Create(OpCodes.Ldstr, entryName);
            var callGetMethod = il.Create(OpCodes.Callvirt, getMethod);
            var dupMethod = il.Create(OpCodes.Dup);
            var brTrueMethod = il.Create(OpCodes.Brtrue, haveMethod);
            var popMethodNull = il.Create(OpCodes.Pop);
            var leaveIfNoMethod = il.Create(OpCodes.Leave, leaveTarget);
            var ldnull1 = il.Create(OpCodes.Ldnull);
            var ldnull2 = il.Create(OpCodes.Ldnull);
            var callInvoke = il.Create(OpCodes.Callvirt, invoke);
            var popResult = il.Create(OpCodes.Pop);
            var tryLeave = il.Create(OpCodes.Leave, leaveTarget);
            var catchPop = il.Create(OpCodes.Pop);
            var catchLeave = il.Create(OpCodes.Leave, leaveTarget);

            var block = new[]
            {
                tryStart,
                callGetType,
                dupType,
                brTrueType,
                popTypeNull,
                leaveIfNoType,
                haveType,
                ldstrEntry,
                callGetMethod,
                dupMethod,
                brTrueMethod,
                popMethodNull,
                leaveIfNoMethod,
                haveMethod,
                ldnull1,
                ldnull2,
                callInvoke,
                popResult,
                tryLeave,
                catchPop,
                catchLeave,
                leaveTarget,
            };

            foreach (var insn in block)
            {
                il.InsertBefore(ret, insn);
            }

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = exceptionType,
                TryStart = tryStart,
                TryEnd = catchPop,
                HandlerStart = catchPop,
                HandlerEnd = leaveTarget,
            });
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine("[NINE-EXT] 已注入 OnCommandPlayerEnd + catch");
    }

    private static bool IsExpandHookInstalled(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == ExpandName || s == "OnCommandPlayerEnd"))
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
            var pe = File.ReadAllBytes(hotfixPath);
            var ascii = System.Text.Encoding.ASCII.GetString(pe);
            if (!ascii.Contains("SeqChapterNineAction") && !ascii.Contains(AssetFileName))
            {
                var uni = System.Text.Encoding.Unicode.GetString(pe);
                if (!uni.Contains("SeqChapterNineAction"))
                {
                    return false;
                }
            }

            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var pause = asm.MainModule.Types.First(t => t.Name == "HotfixEntry")
                .Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
            if (pause.Body.Instructions.Count <= 3)
            {
                return false;
            }

            var onPlayer = asm.MainModule.Types.First(t => t.Name == "BattleProcesser")
                .Methods.First(m => m.Name == "OnCommandPlayerCallback" && m.HasBody);
            return IsExpandHookInstalled(onPlayer);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildNineDll(string hotfixPath)
    {
        var srcDir = ResolveNineSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterNineAction.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterNineAction.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_nine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterNineAction.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译神奇九动 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterNineAction",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterNineAction 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[NINE-EXT] 已编译九动 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveNineSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_nine_action")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_nine_action")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_nine_action");
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
            if (File.Exists(Path.Combine(dir, "SeqChapterNineAction.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException("找不到 tools/seqchapter_nine_action 目录");
    }
}
