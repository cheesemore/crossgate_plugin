using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 切后台 / 老板键限帧：HotfixEntry.Update 前缀加载 SeqChapterBossKeyFps.dll.bytes 并 Invoke Tick。
/// 不占用 OnApplicationPause / 百科 / 分享，可与九动/抓宠/烧卡/日常/桥接并存。
/// </summary>
internal static class BossKeyFpsExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterBossKeyFps.dll.bytes";
    public const string TypeName = "SeqChapterBossKeyFps";
    public const string EntryName = "Tick";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;
    public const string TempDllSuffix = "/seqchapter_boss_key_fps.dll";

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
                "用法: HotfixPatcher boss-key-fps-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher boss-key-fps-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher boss-key-fps-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "boss-key-fps" : "original");
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
            Console.WriteLine("[OK] 老板键限帧补丁完成: " + output);
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

        var dllPath = BuildBossKeyFpsDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[BOSS-FPS] 已部署 " + assetOut);

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        var hotfixEntry = asm.MainModule.Types.FirstOrDefault(t => t.Name == "HotfixEntry")
            ?? throw new InvalidOperationException("未找到 HotfixEntry");
        var update = hotfixEntry.Methods.FirstOrDefault(m =>
                m.Name == "Update" && m.HasBody && m.Parameters.Count == 3)
            ?? throw new InvalidOperationException("未找到 HotfixEntry.Update(Single,Single,Int32)");

        if (IsUpdatePatched(update))
        {
            // 仍刷新 DLL 资源；Update 钩已在则跳过重写（避免叠加前缀）
            Console.WriteLine("[BOSS-FPS] Update 钩已存在，仅刷新 DLL");
        }
        else
        {
            PrependLoadAndInvokeTick(update, asm.MainModule);
            Console.WriteLine("[BOSS-FPS] HotfixEntry.Update -> Tick（老板键隐藏限 30FPS）");
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
        Console.WriteLine($"[BOSS-FPS] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
        HotfixSize.EnsureUnchanged(outBytes, expectedSize);
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
            if (!ContainsUtf16Le(pe, TypeName)
                && !ContainsUtf16Le(pe, AssetFileName)
                && !ContainsUtf16Le(pe, EntryName)
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
            var update = asm.MainModule.Types.FirstOrDefault(t => t.Name == "HotfixEntry")
                ?.Methods.FirstOrDefault(m => m.Name == "Update" && m.HasBody && m.Parameters.Count == 3);
            return update != null && IsUpdatePatched(update);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在保留原 Update 体（Timer + updateBeat）前提下，前缀加载 DLL 并 Invoke Tick。
    /// </summary>
    private static void PrependLoadAndInvokeTick(MethodDefinition method, ModuleDefinition module)
    {
        var original = method.Body.Instructions.ToList();
        var originalVars = method.Body.Variables.ToList();
        var originalHandlers = method.Body.ExceptionHandlers.ToList();

        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = true;

        var body = method.Body;
        var getTypeStatic = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var typeVar = new VariableDefinition(getTypeStatic.ReturnType);
        body.Variables.Add(typeVar);
        var bytesVar = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body.Variables.Add(bytesVar);

        // 复用原 VariableDefinition 实例，避免 ldloc 操作数失效
        foreach (var v in originalVars)
        {
            body.Variables.Add(v);
        }

        var il = body.GetILProcessor();
        var loadBytes = BridgeLoaderIlBuilder.ImportFileUtilLoadBytesPublic(module);
        var assemblyLoad = BridgeLoaderIlBuilder.ImportAssemblyLoadPublic(module);
        var getType = BridgeLoaderIlBuilder.ImportAssemblyGetTypePublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);

        var afterLoad = il.Create(OpCodes.Nop);
        var runOriginal = il.Create(OpCodes.Nop);

        // type = Type.GetType("SeqChapterBossKeyFps, SeqChapterBossKeyFps")
        il.Append(il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName));
        il.Append(il.Create(OpCodes.Call, getTypeStatic));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Brtrue, afterLoad));

        // load once
        il.Append(il.Create(OpCodes.Ldstr, DllAssetPath));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Stloc, bytesVar));
        il.Append(il.Create(OpCodes.Ldloc, bytesVar));
        il.Append(il.Create(OpCodes.Brfalse, runOriginal));
        il.Append(il.Create(OpCodes.Ldloc, bytesVar));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, runOriginal));
        il.Append(il.Create(OpCodes.Ldstr, TypeName));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Brfalse, runOriginal));

        il.Append(afterLoad);
        // Invoke Tick
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Ldstr, EntryName));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, runOriginal));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Pop));

        il.Append(runOriginal);

        // map old instruction refs for exception handlers (none expected)
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var insn in original)
        {
            var copy = CloneInstruction(il, insn);
            map[insn] = copy;
            il.Append(copy);
        }

        foreach (var eh in originalHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
            {
                CatchType = eh.CatchType,
                TryStart = map[eh.TryStart],
                TryEnd = map[eh.TryEnd],
                HandlerStart = map[eh.HandlerStart],
                HandlerEnd = map[eh.HandlerEnd],
                FilterStart = eh.FilterStart == null ? null : map[eh.FilterStart],
            });
        }

        // Retarget branches within cloned original
        foreach (var insn in body.Instructions)
        {
            if (insn.Operand is Instruction target && map.TryGetValue(target, out var nt))
            {
                insn.Operand = nt;
            }
            else if (insn.Operand is Instruction[] targets)
            {
                insn.Operand = targets.Select(t => map.TryGetValue(t, out var n) ? n : t).ToArray();
            }
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, 16);
    }

    private static Instruction CloneInstruction(ILProcessor il, Instruction insn)
    {
        // 操作数为旧 Instruction 时先占位，随后用 map 重定向
        return insn.Operand switch
        {
            null => il.Create(insn.OpCode),
            Instruction target => il.Create(insn.OpCode, target),
            Instruction[] targets => il.Create(insn.OpCode, targets),
            VariableDefinition v => il.Create(insn.OpCode, v),
            ParameterDefinition p => il.Create(insn.OpCode, p),
            MethodReference m => il.Create(insn.OpCode, m),
            FieldReference f => il.Create(insn.OpCode, f),
            TypeReference t => il.Create(insn.OpCode, t),
            CallSite cs => il.Create(insn.OpCode, cs),
            string s => il.Create(insn.OpCode, s),
            int i => il.Create(insn.OpCode, i),
            long l => il.Create(insn.OpCode, l),
            float f32 => il.Create(insn.OpCode, f32),
            double f64 => il.Create(insn.OpCode, f64),
            byte u8 => il.Create(insn.OpCode, u8),
            sbyte i8 => il.Create(insn.OpCode, i8),
            _ => throw new InvalidOperationException(
                $"无法克隆操作码 {insn.OpCode} 操作数类型 {insn.Operand?.GetType().Name}"),
        };
    }

    private static bool IsUpdatePatched(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == TypeName || s == TypeName + ", " + TypeName || s == EntryName
                    || s == DllAssetPath || s.IndexOf("BossKeyFps", StringComparison.Ordinal) >= 0))
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

    private static string BuildBossKeyFpsDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterBossKeyFps.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterBossKeyFps.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_bossfps_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterBossKeyFps.dll");

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
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译老板键限帧 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterBossKeyFps",
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
            throw new InvalidOperationException("Roslyn 编译 SeqChapterBossKeyFps 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[BOSS-FPS] 已编译老板键限帧 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_boss_key_fps")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_boss_key_fps")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_boss_key_fps")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_boss_key_fps")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seqchapter_boss_key_fps")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_boss_key_fps")),
        };

        for (var dir = hotfixDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)!)
        {
            var probe = Path.Combine(dir, "tools", "seqchapter_boss_key_fps");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterBossKeyFps.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/seqchapter_boss_key_fps 目录（请把源码放在 HotfixPatcher.exe 同级或游戏根目录 tools/ 下）");
    }
}
