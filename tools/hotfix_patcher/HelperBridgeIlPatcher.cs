using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 序章助手桥接：外部 SeqChapterHelperBridge.dll.bytes + 二进制 hook（hotfix 体积不变）。
/// </summary>
internal static class HelperBridgeIlPatcher
{
    internal const string BridgeAssetFileName = "SeqChapterHelperBridge.dll.bytes";

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var detectOnly = false;
        var variantOnly = false;
        var bootstrapSiteOnly = false;

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
                case "--detect":
                    detectOnly = true;
                    break;
                case "--detect-variant":
                    variantOnly = true;
                    break;
                case "--detect-bootstrap-site":
                    bootstrapSiteOnly = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher helper-bridge-patch --hotfix <hotfix.dll.bytes> [--output <out>]\n" +
                "      HotfixPatcher helper-bridge-patch --hotfix <hotfix> --detect\n" +
                "      HotfixPatcher helper-bridge-patch --hotfix <hotfix> --detect-variant");
            return 1;
        }

        if (variantOnly)
        {
            Console.WriteLine(DetectPatchVariant(source));
            return 0;
        }

        if (bootstrapSiteOnly)
        {
            Console.WriteLine(DetectBootstrapSite(source));
            return 0;
        }

        if (detectOnly)
        {
            Console.WriteLine(IsPatched(source) ? "patched" : "not_patched");
            return 0;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 序章助手桥接已注入: " + output);
        return 0;
    }

    public static bool IsPatched(string hotfixPath)
    {
        return DetectPatchVariant(hotfixPath) is "cecil_light" or "cecil_light_loadfrom" or "cecil_light_loadbytes" or "binary_loadfrom";
    }

    /// <summary>embedded=黑屏风险；cecil_light*=可进游戏；not_patched=未注入。</summary>
    public static string DetectPatchVariant(string hotfixPath)
    {
        if (!File.Exists(hotfixPath))
        {
            return "missing";
        }

        if (!File.Exists(BridgeAssetPath(hotfixPath)))
        {
            return "not_patched";
        }

        var data = File.ReadAllBytes(hotfixPath);
        if (!ContainsUserString(data, BridgeLoaderIlBuilder.BridgeDllAssetPath)
            || !ContainsUserString(data, BridgeLoaderIlBuilder.BridgeBootstrapName))
        {
            return "not_patched";
        }

        try
        {
            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

            if (asm.MainModule.Types.Any(t => t.Name == BridgeLoaderIlBuilder.BridgeTypeName))
            {
                return "embedded";
            }

            var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");
            var bootstrap = ResolveBootstrapLoader(hotfixEntry);
            var entryStart = hotfixEntry.Methods.First(m => m.Name == "Start" && m.HasBody);
            VerifyBridgeHooksCore(asm, bootstrap, entryStart);

            if (LoaderUsesLoadFrom(bootstrap))
            {
                return "cecil_light_loadfrom";
            }

            if (LoaderUsesAssemblyLoadBytes(bootstrap))
            {
                return "cecil_light_loadbytes";
            }

            return "unknown";
        }
        catch
        {
            return "broken";
        }
    }

    /// <summary>quit=Timer 延迟版；pause=旧版 Start 末尾直接 call；none=未注入 loader。</summary>
    public static string DetectBootstrapSite(string hotfixPath)
    {
        if (!File.Exists(hotfixPath))
        {
            return "none";
        }

        try
        {
            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

            var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");
            var pause = hotfixEntry.Methods.FirstOrDefault(m => m.Name == "OnApplicationPause" && m.HasBody);
            if (pause != null
                && pause.Body.Instructions.Count > 5
                && (LoaderUsesLoadFrom(pause) || LoaderUsesAssemblyLoadBytes(pause)))
            {
                return "pause";
            }

            var quit = hotfixEntry.Methods.FirstOrDefault(m => m.Name == "OnApplicationQuit" && m.HasBody);
            if (quit != null
                && quit.Body.Instructions.Count > 2
                && (LoaderUsesLoadFrom(quit) || LoaderUsesAssemblyLoadBytes(quit)))
            {
                return "quit";
            }

            var pauseEmpty = hotfixEntry.Methods.FirstOrDefault(m => m.Name == "OnApplicationPause" && m.HasBody);
            if (pauseEmpty != null
                && pauseEmpty.Body.Instructions.Count > 5
                && (LoaderUsesLoadFrom(pauseEmpty) || LoaderUsesAssemblyLoadBytes(pauseEmpty)))
            {
                return "pause";
            }

            return "none";
        }
        catch
        {
            return "none";
        }
    }

    private static bool LoaderUsesLoadFrom(MethodDefinition loader)
    {
        if (!loader.HasBody)
        {
            return false;
        }

        return loader.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference called
            && called.Name == "LoadFrom"
            && called.DeclaringType?.FullName == "System.Reflection.Assembly");
    }

    private static bool LoaderUsesAssemblyLoadBytes(MethodDefinition loader)
    {
        if (!loader.HasBody)
        {
            return false;
        }

        return loader.Body.Instructions.Any(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference called
            && called.Name == "Load"
            && called.DeclaringType?.FullName == "System.Reflection.Assembly");
    }

    private static bool ContainsBridgeStartHook(byte[] data, uint loaderMethodToken)
    {
        var token = BitConverter.GetBytes(loaderMethodToken);
        for (var i = 0; i <= data.Length - 6; i++)
        {
            if (data[i] != 0x16 || data[i + 1] != 0x28)
            {
                continue;
            }

            if (data[i + 2] == token[0]
                && data[i + 3] == token[1]
                && data[i + 4] == token[2]
                && data[i + 5] == token[3])
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUserString(byte[] data, string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        if (IndexOf(data, ascii) >= 0)
        {
            return true;
        }

        var utf16 = System.Text.Encoding.Unicode.GetBytes(text);
        return IndexOf(data, utf16) >= 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
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

    public static void Apply(string sourcePath, string outputPath)
    {
        if (IsPatched(sourcePath))
        {
            Console.WriteLine("[SKIP] 助手桥接 hook 已存在，跳过");
            if (!string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, outputPath, overwrite: true);
            }

            return;
        }

        var origPath = sourcePath.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
            ? sourcePath
            : sourcePath + ".orig";
        if (!File.Exists(origPath))
        {
            if (origPath == sourcePath)
            {
                throw new FileNotFoundException("找不到原版 hotfix 备份", origPath);
            }

            File.Copy(sourcePath, origPath, overwrite: false);
            Console.WriteLine("[BACKUP] 已创建 " + Path.GetFileName(origPath));
        }

        var hotfixPath = origPath.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
            ? origPath[..^5]
            : sourcePath;

        var origBytes = File.ReadAllBytes(origPath);
        var expectedSize = HotfixSize.Require(origBytes);

        var bridgeDll = BuildBridgeDll(hotfixPath);
        try
        {
            File.Copy(bridgeDll, BridgeAssetPath(hotfixPath), overwrite: true);
            Console.WriteLine("[DEPLOY] " + BridgeAssetPath(hotfixPath));

            var sourceBytes = File.ReadAllBytes(sourcePath);
            var sourceIsClean = sourceBytes.AsSpan().SequenceEqual(origBytes.AsSpan());
            if (sourceIsClean)
            {
                ApplyViaCecilLight(sourcePath, outputPath, origBytes, expectedSize);
            }
            else
            {
                Console.WriteLine("[BRIDGE] 目标 hotfix 已含玩法补丁，使用模板叠加");
                ApplyViaOrigTemplate(sourcePath, outputPath, origPath, origBytes, expectedSize);
            }
        }
        finally
        {
            try { File.Delete(bridgeDll); } catch { }
        }
    }

    /// <summary>Cecil 仅改 IL + 零填充到固定体积；不嵌入类型（嵌入会导致黑屏）。</summary>
    internal static void ApplyViaCecilLight(
        string sourcePath,
        string outputPath,
        byte[] origBytes,
        int expectedSize)
    {
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

        BridgeLoaderIlBuilder.BuildLoaderBodyInPlace(pauseMethod, asm.MainModule, userStrings, skipIfTypeLoaded: true);
        BridgeLoaderIlBuilder.BuildQuitTriggersPauseBody(quitMethod, pauseMethod, asm.MainModule);
        BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);
        BridgeLoaderIlBuilder.ApplyGameManagerStartHook(gmStartMethod.Body, pauseMethod);

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

        using (var verifyMs = new MemoryStream(padded))
        using (var asmVerify = AssemblyDefinition.ReadAssembly(verifyMs, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        }))
        {
            VerifyBridgeHooksCore(asmVerify, pauseMethod, entryStartMethod);
        }

        File.WriteAllBytes(outputPath, padded);
        Console.WriteLine($"[OK] Cecil 轻量桥接注入完成（Load 字节 + ModLoader 挂点），文件大小: {padded.Length} 字节");
    }

    internal static void ApplyViaOrigTemplate(
        string sourcePath,
        string outputPath,
        string origPath,
        byte[] origBytes,
        int expectedSize)
    {
        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var tempBridged = Path.Combine(Path.GetTempPath(), "seqchapter_bridge_tpl_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            ApplyViaCecilLight(origPath, tempBridged, origBytes, expectedSize);
            var targetBytes = File.ReadAllBytes(sourcePath);
            if (targetBytes.Length != expectedSize)
            {
                throw new InvalidOperationException(
                    $"当前 hotfix 体积 {targetBytes.Length} 与 .orig 模板 {expectedSize} 不一致");
            }

            BridgeTemplateOverlay.Apply(origBytes, tempBridged, targetBytes, sourcePath, hotfixDir);
            MetadataValidator.EnsureReadable(targetBytes, hotfixDir);

            var resolver = new HotfixAssemblyResolver(hotfixDir);
            using var verifyMs = new MemoryStream(targetBytes, writable: false);
            using var asmVerify = AssemblyDefinition.ReadAssembly(verifyMs, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var pauseVerify = asmVerify.MainModule.Types.First(t => t.Name == "HotfixEntry")
                .Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
            var startVerify = asmVerify.MainModule.Types.First(t => t.Name == "HotfixEntry")
                .Methods.First(m => m.Name == "Start" && m.HasBody);
            VerifyBridgeHooksCore(asmVerify, pauseVerify, startVerify);

            if (!LoaderUsesAssemblyLoadBytes(pauseVerify))
            {
                throw new InvalidOperationException("模板叠加后 loader 校验失败：未找到 Assembly.Load(byte[])");
            }

            EnsureTextSlackBudget(targetBytes, origBytes, maxAppendBytes: 200);
            File.WriteAllBytes(outputPath, targetBytes);
            Console.WriteLine(
                $"[OK] 桥接模板叠加完成（玩法补丁 + Load 字节桥接），文件大小: {targetBytes.Length} 字节");
        }
        finally
        {
            try { File.Delete(tempBridged); } catch { }
        }
    }

    /// <summary>二进制 IL 补丁：体积与 .orig 完全一致；用于已打玩法补丁、无法 Cecil Write 的 hotfix。</summary>
    internal static void ApplyViaBinaryPe(string sourcePath, string outputPath, byte[] origBytes)
    {
        var expectedSize = origBytes.Length;
        var data = File.ReadAllBytes(sourcePath);
        if (data.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"当前 hotfix 体积 {data.Length} 与 .orig 模板 {expectedSize} 不一致，请先还原或重新初始化");
        }

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var origPath = sourcePath.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
            ? sourcePath
            : sourcePath + ".orig";
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        var origBytesForBridge = File.ReadAllBytes(origPath);
        if (origBytesForBridge.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $".orig 体积 {origBytesForBridge.Length} 与 hotfix {expectedSize} 不一致");
        }

        var tempBridged = Path.Combine(Path.GetTempPath(), "seqchapter_bridge_bin_" + Guid.NewGuid().ToString("N") + ".dll");
        ImportTokenResolver.ResolvedTokens tokens;
        try
        {
            ApplyViaCecilLight(origPath, tempBridged, origBytesForBridge, expectedSize);
            var bridgedBytes = File.ReadAllBytes(tempBridged);
            tokens = BridgeMemberRefTemplateImporter.Import(origBytesForBridge, bridgedBytes, data, hotfixDir);
        }
        finally
        {
            try { File.Delete(tempBridged); } catch { }
        }

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

        var userStrings = UserStringHeap.FromPe(data);

        var pauseSnapshot = ReadMethodBodyFromPe(origBytes, pauseMethod.RVA);
        var pauseBody = BridgeManualIlBuilder.BuildLoaderBody(tokens, userStrings, skipIfTypeLoaded: true);
        BinaryPeWriter.ReplaceMethodBody(data, pauseMethod.RVA, pauseSnapshot, pauseBody);

        var quitSnapshot = ReadMethodBodyFromPe(origBytes, quitMethod.RVA);
        var quitBody = BridgeManualIlBuilder.BuildQuitTriggersPauseBody(
            pauseMethod.MetadataToken.ToUInt32());
        BinaryPeWriter.ReplaceMethodBody(data, quitMethod.RVA, quitSnapshot, quitBody);

        var entryStartSnapshot = ReadMethodBodyFromPe(data, entryStartMethod.RVA);
        BridgeLoaderIlBuilder.ApplyDeferredTimerStartHook(entryStartMethod.Body, quitMethod, asm.MainModule);
        var entryStartBody = IlSerializer.Serialize(entryStartMethod.Body, userStrings);
        BinaryPeWriter.ReplaceMethodBody(data, entryStartMethod.RVA, entryStartSnapshot, entryStartBody);

        var gmStartSnapshot = ReadMethodBodyFromPe(data, gmStartMethod.RVA);
        BridgeLoaderIlBuilder.ApplyGameManagerStartHook(gmStartMethod.Body, pauseMethod);
        var gmStartBody = IlSerializer.Serialize(gmStartMethod.Body, userStrings);
        BinaryPeWriter.ReplaceMethodBody(data, gmStartMethod.RVA, gmStartSnapshot, gmStartBody);

        if (data.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"二进制桥接改变了 hotfix 体积 {data.Length} != {expectedSize}");
        }

        using var verifyMs = new MemoryStream(data, writable: false);
        using var asmVerify = AssemblyDefinition.ReadAssembly(verifyMs, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });
        var pauseVerify = asmVerify.MainModule.Types.First(t => t.Name == "HotfixEntry")
            .Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
        var startVerify = asmVerify.MainModule.Types.First(t => t.Name == "HotfixEntry")
            .Methods.First(m => m.Name == "Start" && m.HasBody);
        VerifyBridgeHooksCore(asmVerify, pauseVerify, startVerify);

        if (!LoaderUsesAssemblyLoadBytes(pauseVerify))
        {
            throw new InvalidOperationException("二进制 loader 校验失败：未找到 Assembly.Load(byte[])");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 二进制桥接注入完成，文件大小: {data.Length} 字节（Load 字节 + ModLoader 挂点）");
    }

    internal static void ApplyViaCecilWriteDisabled(
        string sourcePath,
        string outputPath,
        byte[] origBytes,
        int expectedSize,
        string bridgeDll)
    {
        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");
        var loaderMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
        var entryUpdate = hotfixEntry.Methods.First(m => m.Name == "Update" && m.HasBody);
        var userStrings = UserStringHeap.FromPe(origBytes);

        BridgeLoaderIlBuilder.BuildLoaderBody(loaderMethod, asm.MainModule, userStrings, skipIfTypeLoaded: true);

        var firstUpdate = entryUpdate.Body.Instructions[0];
        var updateIl = entryUpdate.Body.GetILProcessor();
        updateIl.InsertBefore(firstUpdate, updateIl.Create(OpCodes.Ldc_I4_0));
        updateIl.InsertBefore(firstUpdate, updateIl.Create(OpCodes.Call, asm.MainModule.ImportReference(loaderMethod)));
        IlSerializer.RecalculateOffsets(entryUpdate.Body);
        entryUpdate.Body.MaxStackSize = Math.Max(entryUpdate.Body.MaxStackSize, (short)8);

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}，无法填充。");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);

        VerifyBridgeHooks(padded, loaderMethod, entryUpdate);
        File.WriteAllBytes(outputPath, padded);
        Console.WriteLine($"[OK] Cecil 桥接注入完成，文件大小: {padded.Length} 字节");
    }

    public static string BridgeAssetPath(string hotfixPath)
        => Path.Combine(Path.GetDirectoryName(hotfixPath)!, BridgeAssetFileName);

    private static string BuildBridgeDll(string hotfixPath)
    {
        var bridgeDir = ResolveBridgeSourceDir(hotfixPath);
        var csPath = Path.Combine(bridgeDir, "SeqChapterHelperBridge.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterHelperBridge.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_bridge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterHelperBridge.dll");

        var refs = ResolveGameCorlibReferences(hotfixDataDir);
        if (refs.Count == 0)
        {
            throw new InvalidOperationException(
                "未找到 hotfixdata 内 mscorlib/system 引用，无法编译 HybridCLR 兼容桥接 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterHelperBridge",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterHelperBridge 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[BRIDGE] 已用游戏 corlib 编译桥接 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    /// <summary>使用 hotfixdata 内 mscorlib/system（HybridCLR 同源），避免 net8 System.Private.CoreLib。</summary>
    private static List<MetadataReference> ResolveGameCorlibReferences(string hotfixDataDir)
    {
        var refs = new List<MetadataReference>();
        foreach (var name in new[]
                 {
                     "mscorlib.dll.bytes",
                     "system.dll.bytes",
                     "system.core.dll.bytes",
                 })
        {
            var path = Path.Combine(hotfixDataDir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }

    private static string ResolveBridgeSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_helper_bridge")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_helper_bridge")),
        };

        for (var dir = hotfixDir; ; dir = Path.GetDirectoryName(dir)!)
        {
            if (string.IsNullOrEmpty(dir))
            {
                break;
            }

            var probe = Path.Combine(dir, "tools", "seqchapter_helper_bridge");
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
            if (File.Exists(Path.Combine(dir, "SeqChapterHelperBridge.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException("找不到 tools/seqchapter_helper_bridge 目录");
    }

    private static byte[] ReadMethodBodyFromPe(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            var codeSize = flags >> 2;
            var len = 1 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(pe, off + 4);
            var len = 12 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }

    private static void VerifyBridgeHooks(byte[] data, MethodDefinition loaderMethod, MethodDefinition updateMethod)
    {
        VerifyBridgeHooksBinaryByRva(
            data,
            loaderMethod.MetadataToken.ToUInt32(),
            updateMethod.MetadataToken.ToUInt32(),
            CliMetadata.ReadMethodDefRva(data, loaderMethod.MetadataToken.ToUInt32()),
            CliMetadata.ReadMethodDefRva(data, updateMethod.MetadataToken.ToUInt32()));
    }

    private static void VerifyBridgeHooksBinary(
        byte[] data,
        uint loaderToken,
        uint startToken,
        int loaderRvaFileOffset,
        int startRvaFileOffset)
    {
        var loaderRva = BitConverter.ToInt32(data, loaderRvaFileOffset);
        var startRva = BitConverter.ToInt32(data, startRvaFileOffset);
        VerifyBridgeStartHookBinaryByRva(data, loaderToken, startRva, loaderRva);
    }

    private static void VerifyBridgeStartHookBinaryByRva(
        byte[] data,
        uint loaderToken,
        int startRva,
        int loaderRva)
    {
        var loaderBody = ReadMethodBodyFromPe(data, loaderRva);
        var loaderCode = ExtractMethodCode(loaderBody);
        if (loaderCode.Length < 10 || !ContainsCallToken(loaderCode, 0x0A000000))
        {
            throw new InvalidOperationException("桥接 loader (OnApplicationPause) IL 二进制校验失败");
        }

        var startBody = ReadMethodBodyFromPe(data, startRva);
        var startCode = ExtractMethodCode(startBody);
        if (!ContainsBridgeStartHookCode(startCode, loaderToken))
        {
            throw new InvalidOperationException(
                $"桥接 hook 校验失败：Start 中应含 ldc.i4.0 + call loader(0x{loaderToken:X8})");
        }

        Console.WriteLine(
            $"[VERIFY] loader RVA=0x{loaderRva:X}, Start 含 ldc.i4.0 + call loader @ RVA=0x{startRva:X}");
    }

    private static bool ContainsBridgeStartHookCode(byte[] code, uint loaderMethodToken)
    {
        var token = BitConverter.GetBytes(loaderMethodToken);
        for (var i = 0; i <= code.Length - 6; i++)
        {
            if (code[i] != 0x16 || code[i + 1] != 0x28)
            {
                continue;
            }

            if (code[i + 2] == token[0]
                && code[i + 3] == token[1]
                && code[i + 4] == token[2]
                && code[i + 5] == token[3])
            {
                return true;
            }
        }

        return false;
    }

    private static void VerifyBridgeHooksBinaryByRva(
        byte[] data,
        uint loaderToken,
        uint updateToken,
        int loaderRva,
        int updateRva)
    {
        var updateBody = ReadMethodBodyFromPe(data, updateRva);
        var updateCode = ExtractMethodCode(updateBody);
        if (updateCode.Length < 6
            || updateCode[0] != 0x16
            || updateCode[1] != 0x28
            || BitConverter.ToUInt32(updateCode, 2) != loaderToken)
        {
            throw new InvalidOperationException(
                $"桥接 hook 校验失败：Update 首行应为 ldc.i4.0 + call loader(0x{loaderToken:X8})");
        }

        var loaderBody = ReadMethodBodyFromPe(data, loaderRva);
        var loaderCode = ExtractMethodCode(loaderBody);
        if (loaderCode.Length < 10 || !ContainsCallToken(loaderCode, 0x0A000000))
        {
            throw new InvalidOperationException("桥接 loader (OnApplicationPause) IL 二进制校验失败");
        }

        Console.WriteLine(
            $"[VERIFY] loader RVA=0x{loaderRva:X}, Update 首行 ldc.i4.0 + call loader @ RVA=0x{updateRva:X}");
    }

    private static byte[] ExtractMethodCode(byte[] methodBody)
    {
        var flags = methodBody[0];
        if ((flags & 0x3) == 0x2)
        {
            var codeSize = flags >> 2;
            return methodBody[1..(1 + codeSize)];
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(methodBody, 4);
            return methodBody[12..(12 + codeSize)];
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");
    }

    private static bool ContainsCallToken(byte[] code, uint tokenHigh)
    {
        for (var i = 0; i <= code.Length - 5; i++)
        {
            if (code[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToUInt32(code, i + 1);
            if ((token & 0xFF000000) == tokenHigh)
            {
                return true;
            }
        }

        return false;
    }

    private static void VerifyBridgeHooks(string hotfixPath, MethodDefinition loaderMethod, MethodDefinition updateMethod)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        VerifyBridgeHooksCore(asm, loaderMethod, updateMethod);
    }

    private static MethodDefinition ResolveBootstrapLoader(TypeDefinition hotfixEntry)
    {
        var pause = hotfixEntry.Methods.FirstOrDefault(m => m.Name == "OnApplicationPause" && m.HasBody);
        if (pause != null
            && pause.Body.Instructions.Count > 5
            && (LoaderUsesLoadFrom(pause) || LoaderUsesAssemblyLoadBytes(pause)))
        {
            return pause;
        }

        var quit = hotfixEntry.Methods.FirstOrDefault(m => m.Name == "OnApplicationQuit" && m.HasBody);
        if (quit != null
            && quit.Body.Instructions.Count > 2
            && (LoaderUsesLoadFrom(quit) || LoaderUsesAssemblyLoadBytes(quit)))
        {
            return quit;
        }

        return pause ?? quit ?? hotfixEntry.Methods.First(m => m.Name == "OnApplicationPause" && m.HasBody);
    }

    private static void VerifyBridgeHooksCore(
        AssemblyDefinition asm,
        MethodDefinition bootstrapMethod,
        MethodDefinition entryStartMethod)
    {
        var hotfixEntry = asm.MainModule.Types.First(t => t.Name == "HotfixEntry");
        var bootstrap = bootstrapMethod.HasBody && bootstrapMethod.Body.Instructions.Count > 2
            ? bootstrapMethod
            : ResolveBootstrapLoader(hotfixEntry);
        var entryStart = hotfixEntry.Methods.First(m => m.Name == "Start" && m.HasBody);

        if (!bootstrap.HasBody || bootstrap.Body.Instructions.Count < 5)
        {
            throw new InvalidOperationException($"桥接 loader ({bootstrap.Name}) IL 读回校验失败");
        }

        var quitMethod = hotfixEntry.Methods.First(m => m.Name == "OnApplicationQuit" && m.HasBody);
        if (TryFindAddTimeInvokeBootstrapHook(entryStart.Body, quitMethod, out _))
        {
            Console.WriteLine(
                $"[VERIFY] loader RVA=0x{bootstrap.RVA:X} ({bootstrap.Name}), HotfixEntry.Start AddTimeInvoke→OnApplicationQuit @ RVA=0x{entryStart.RVA:X}");
            return;
        }

        if (TryFindTimerCreateBootstrapHook(entryStart.Body, quitMethod, out _))
        {
            throw new InvalidOperationException(
                "桥接 hook 使用 Timer.Create 但未 Start()，请从 .orig 重新注入（AddTimeInvoke 版）");
        }

        if (TryFindLoaderCallHook(entryStart.Body, bootstrap, out var hookIndex))
        {
            var retIndex = entryStart.Body.Instructions.Count - 1;
            if (entryStart.Body.Instructions[retIndex].OpCode != OpCodes.Ret
                || hookIndex != retIndex - 2)
            {
                throw new InvalidOperationException(
                    "桥接 hook 校验失败：HotfixEntry.Start hook 应在 return 前");
            }

            Console.WriteLine(
                $"[VERIFY] loader RVA=0x{bootstrap.RVA:X}, HotfixEntry.Start 末尾 ldc.i4.0 + call {bootstrap.Name} @ RVA=0x{entryStart.RVA:X}");
            return;
        }

        throw new InvalidOperationException(
            "桥接 hook 校验失败：HotfixEntry.Start 应含 AddTimeInvoke 延迟 bootstrap 或 ldc.i4.0 + call loader");
    }

    private static bool TryFindAddTimeInvokeBootstrapHook(
        MethodBody body,
        MethodDefinition bootstrap,
        out int invokeCallIndex)
    {
        invokeCallIndex = -1;
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var insn = body.Instructions[i];
            if (insn.OpCode != OpCodes.Call
                || insn.Operand is not MethodReference called
                || called.Name != "AddTimeInvoke"
                || called.DeclaringType?.Name != "Timer")
            {
                continue;
            }

            for (var j = i - 1; j >= Math.Max(0, i - 6); j--)
            {
                if (body.Instructions[j].OpCode != OpCodes.Ldftn)
                {
                    continue;
                }

                if (body.Instructions[j].Operand is MethodReference ftfn
                    && ftfn.Name == bootstrap.Name
                    && ftfn.DeclaringType?.Name == bootstrap.DeclaringType.Name)
                {
                    invokeCallIndex = i;
                    return true;
                }

                if (body.Instructions[j].Operand is IMetadataTokenProvider tokenProvider
                    && tokenProvider.MetadataToken.ToUInt32() == bootstrap.MetadataToken.ToUInt32())
                {
                    invokeCallIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindTimerCreateBootstrapHook(
        MethodBody body,
        MethodDefinition bootstrap,
        out int timerCallIndex)
    {
        timerCallIndex = -1;
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var insn = body.Instructions[i];
            if (insn.OpCode != OpCodes.Call
                || insn.Operand is not MethodReference called
                || called.Name != "Create"
                || called.DeclaringType?.Name != "Timer")
            {
                continue;
            }

            for (var j = i - 1; j >= Math.Max(0, i - 10); j--)
            {
                if (body.Instructions[j].OpCode != OpCodes.Ldftn)
                {
                    continue;
                }

                if (body.Instructions[j].Operand is MethodReference ftfn
                    && ftfn.Name == bootstrap.Name
                    && ftfn.DeclaringType?.Name == bootstrap.DeclaringType.Name)
                {
                    timerCallIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindTimerBootstrapHook(
        MethodBody body,
        MethodDefinition bootstrap,
        out int timerCallIndex)
        => TryFindAddTimeInvokeBootstrapHook(body, bootstrap, out timerCallIndex)
            || TryFindTimerCreateBootstrapHook(body, bootstrap, out timerCallIndex);

    private static bool TryFindLoaderCallHook(
        MethodBody body,
        MethodDefinition loader,
        out int ldcIndex)
    {
        ldcIndex = -1;
        for (var i = 0; i <= body.Instructions.Count - 2; i++)
        {
            var first = body.Instructions[i];
            var second = body.Instructions[i + 1];
            if (first.OpCode != OpCodes.Ldc_I4_0 || second.OpCode != OpCodes.Call)
            {
                continue;
            }

            if (second.Operand is MethodReference called
                && called.Name == loader.Name
                && called.DeclaringType?.Name == loader.DeclaringType.Name)
            {
                ldcIndex = i;
                return true;
            }

            if (second.Operand is IMetadataTokenProvider tokenProvider
                && tokenProvider.MetadataToken.ToUInt32() == loader.MetadataToken.ToUInt32())
            {
                ldcIndex = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 追加 IL 只能使用 .text 节区现有 slack；超出会压缩 .rsrc 导致游戏加载 hotfix 崩溃。
    /// </summary>
    private static void EnsureTextSlackBudget(byte[] data, byte[] origBytes, int maxAppendBytes)
    {
        var origText = PeLayout.GetSection(origBytes, ".text");
        var newText = PeLayout.GetSection(data, ".text");
        var origSlack = (int)(origText.SizeOfRawData - origText.VirtualSize);
        var growth = (int)(newText.VirtualSize - origText.VirtualSize);
        if (growth > origSlack)
        {
            throw new InvalidOperationException(
                $"桥接 hook 追加 IL {growth} 字节，超过 .text slack {origSlack} 字节。"
                + "继续写入会压缩 .rsrc 导致游戏闪退，已中止。");
        }

        if (growth > maxAppendBytes)
        {
            Console.WriteLine(
                $"[WARN] .text 追加 {growth} 字节（slack {origSlack}，建议上限 {maxAppendBytes}）");
        }
        else
        {
            Console.WriteLine($"[OK] .text 追加 {growth} 字节（slack {origSlack}，未压缩 .rsrc）");
        }
    }
}
