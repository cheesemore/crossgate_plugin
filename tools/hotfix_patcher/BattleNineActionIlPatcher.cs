using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>神奇九动：69 队列 P1 P2 P3 P4 P1 P2 P3 P4 P5（5 号位只动一次）。</summary>
internal static class BattleNineActionIlPatcher
{
    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var restore = false;
        var queueOnly = false;
        var magicsOnly = false;

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
                case "--queue-only":
                    queueOnly = true;
                    break;
                case "--magics-only":
                    magicsOnly = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher battle-nine-action-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher battle-nine-action-patch --hotfix <orig> --output <out> --queue-only\n" +
                "      HotfixPatcher battle-nine-action-patch --hotfix <orig> --output <out> --magics-only\n" +
                "      HotfixPatcher battle-nine-action-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (restore)
        {
            File.Copy(source, output, overwrite: true);
            Console.WriteLine("[RESTORE] 已从原版复制: " + source);
            Console.WriteLine("[OK] 9动补丁已回退: " + output);
            return 0;
        }

        try
        {
            Apply(source, output, patchQueue: !magicsOnly, patchMagics: !queueOnly);
            Console.WriteLine("[OK] 9动补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath, bool patchQueue = true, bool patchMagics = true)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);
        var data = (byte[])origBytes.Clone();
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
        var onCommandPlayer = battleProcesser.Methods.First(m => m.Name == "OnCommandPlayerCallback" && m.HasBody);

        var listDone = !patchQueue || IsNineActionListHookInstalled(onCommandPlayer);
        var (magicsDone, magicsMethods) = patchMagics
            ? ProbeMagicsPatchState(asm.MainModule)
            : (true, new List<MethodDefinition>());

        if (listDone && (magicsDone || magicsMethods.Count == 0))
        {
            Console.WriteLine("[SKIP] 9动补丁已存在");
            File.WriteAllBytes(outputPath, data);
            return;
        }

        var magicsPatched = false;

        if (patchMagics && !magicsDone)
        {
            foreach (var method in magicsMethods)
            {
                magicsPatched |= PatchPlayerActionMagicsInPlace(data, method);
            }

            if (magicsMethods.Count > 0 && !magicsPatched && !magicsDone)
            {
                throw new InvalidOperationException("未能在 PE 中补丁 PlayerActionMagics 赋值点");
            }
        }

        if (patchQueue && !listDone)
        {
            var snapshot = ReadMethodBodyFromPe(data, onCommandPlayer.RVA);
            BattleNineActionIlBuilder.ApplyOnCommandPlayerPatches(onCommandPlayer, asm.MainModule);
            IlSerializer.RecalculateOffsets(onCommandPlayer.Body);
            onCommandPlayer.Body.MaxStackSize = Math.Max(onCommandPlayer.Body.MaxStackSize, (short)8);

            var newBody = IlSerializer.Serialize(onCommandPlayer.Body, snapshot);
            var vaGap = PeLayout.GetTextVaGapBytes(data);
            if (newBody.Length > snapshot.Length && newBody.Length > vaGap)
            {
                throw new InvalidOperationException(
                    $"神奇九动队列 IL 需 {newBody.Length} 字节，.text VA 间隙仅 {vaGap} 字节，无法安全写入。" +
                    "后迁邻居方法会导致启动未响应。请改用 DLL 版（SeqChapterNineAction），" +
                    "或仅打 Magics：battle-nine-action-patch --magics-only");
            }

            BinaryPeWriter.ReplaceMethodBody(data, onCommandPlayer, snapshot, newBody);
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);

        if (patchQueue && !listDone)
        {
            Console.WriteLine("[PATCH] 神奇九动(69): P1 P2 P3 P4 P1 P2 P3 P4 P5");
        }

        if (magicsPatched)
        {
            Console.WriteLine("[PATCH] PlayerActionMagics: 一动用魔法/道具仍保持 false（9动二轮可开技能栏）");
        }

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static bool IsNineActionListHookInstalled(MethodDefinition method)
    {
        try
        {
            return BattleNineActionIlBuilder.IsHookInstalled(method);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WARN] IsHookInstalled Cecil 读取失败，视为已打补丁: " + ex.Message);
            return true;
        }
    }

    private static (bool Done, List<MethodDefinition> Methods) ProbeMagicsPatchState(ModuleDefinition module)
    {
        try
        {
            var done = BattlePlayerActionMagicsIlBuilder.IsHookInstalled(module);
            var methods = BattlePlayerActionMagicsIlBuilder.FindMethodsNeedingPatch(module).ToList();
            return (done, methods);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[WARN] PlayerActionMagics 探测失败，视为已打补丁: " + ex.Message);
            return (true, new List<MethodDefinition>());
        }
    }

    /// <summary>原地改 ldc.i4.1 → ldc.i4.0，避免 Cecil 重序列化撑大方法体。</summary>
    private static bool PatchPlayerActionMagicsInPlace(byte[] pe, MethodDefinition method)
    {
        var snapshot = ReadMethodBodyFromPe(pe, method.RVA);
        var sites = BattlePlayerActionMagicsIlBuilder.FindAssignmentSites(method.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldc_I4_1)
            .ToList();
        if (sites.Count == 0)
        {
            return false;
        }

        var patched = (byte[])snapshot.Clone();
        var codeOff = GetCodeOffset(patched);
        foreach (var site in sites)
        {
            patched[codeOff + site.Offset] = (byte)OpCodes.Ldc_I4_0.Value;
        }

        BinaryPeWriter.ReplaceMethodBody(pe, method.RVA, snapshot, patched);
        return true;
    }

    private static int GetCodeOffset(byte[] methodBody)
    {
        var flags = methodBody[0];
        return (flags & 0x3) switch
        {
            0x2 => 1,
            0x3 => 12,
            _ => throw new InvalidOperationException($"未知 method header 0x{flags:X2}"),
        };
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
}
