using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 遇敌一级停止：取消对哥布林(101800)/迷你蝙蝠(101242) 的 LevelOneFlag 排除。
/// 将比较常量改为 999999（无效 AnimationId），原地改 ldc.i4 立即数，体积不变。
/// </summary>
internal static class LevelOneIncludeAllIlPatcher
{
    private const int ExcludedGoblin = 101800;
    private const int ExcludedBat = 101242;
    private const int DummyAnimId = 999999;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var restore = false;

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
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher level-one-include-all-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher level-one-include-all-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (restore)
        {
            File.Copy(source, output, overwrite: true);
            Console.WriteLine("[RESTORE] 已从原版复制: " + output);
            return 0;
        }

        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 遇敌一级含哥布林/迷你蝙蝠补丁完成: " + output);
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

        var method = RequireRefreshData(asm);
        var (codeFileOff, _) = GetMethodCodeRange(data, method.RVA);

        var patched = 0;
        var already = 0;
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode != OpCodes.Ldc_I4 || insn.Operand is not int value)
            {
                continue;
            }

            int? target = null;
            if (value == ExcludedGoblin || value == ExcludedBat)
            {
                target = DummyAnimId;
            }
            else if (value == DummyAnimId)
            {
                already++;
                continue;
            }
            else
            {
                continue;
            }

            // ldc.i4 = 0x20 + int32 LE；立即数在 opcode 后 1 字节
            var immOff = codeFileOff + insn.Offset + 1;
            if (immOff < 0 || immOff + 4 > data.Length)
            {
                throw new InvalidOperationException($"立即数越界 @ IL+0x{insn.Offset:X}");
            }

            var current = BitConverter.ToInt32(data, immOff);
            if (current != value)
            {
                throw new InvalidOperationException(
                    $"IL/文件立即数不一致: IL={value} file={current} @ 0x{immOff:X}");
            }

            BitConverter.GetBytes(target.Value).CopyTo(data, immOff);
            patched++;
            Console.WriteLine($"[PATCH] RefreshData: ldc.i4 {value} -> {target.Value}");
        }

        // 官方新版已删除 RefreshData 中的 101800/101242 排除逻辑：
        // 没有任何排除常量可改，一级天然包含所有怪，视为已达成，保持文件不变。
        if (patched == 0 && already == 0)
        {
            Console.WriteLine("[SKIP] 官方新版 RefreshData 已无 LevelOne 排除逻辑，无需改写");
            File.WriteAllBytes(outputPath, data);
            return;
        }

        if (patched == 0 && already >= 2)
        {
            Console.WriteLine("[SKIP] LevelOne 排除 ID 已改为无效值");
        }
        else if (patched != 2 && already == 0)
        {
            throw new InvalidOperationException(
                $"期望改写 2 处排除常量(101800/101242)，实际 patched={patched} already={already}");
        }
        else if (patched + already < 2)
        {
            throw new InvalidOperationException(
                $"排除常量不完整: patched={patched} already={already}");
        }

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
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
            var method = RequireRefreshData(asm);
            var dummy = 0;
            var old = 0;
            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode != OpCodes.Ldc_I4 || insn.Operand is not int v)
                {
                    continue;
                }

                if (v == DummyAnimId)
                {
                    dummy++;
                }
                else if (v == ExcludedGoblin || v == ExcludedBat)
                {
                    old++;
                }
            }

            // 官方新版已无排除逻辑 → 视为已达成（一级天然含全部）
            if (dummy == 0 && old == 0)
            {
                return true;
            }
            return dummy >= 2 && old == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int codeFileOff, int codeSize) GetMethodCodeRange(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            return (off + 1, flags >> 2);
        }

        if ((flags & 0x3) == 0x3)
        {
            return (off + 12, BitConverter.ToInt32(pe, off + 4));
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }

    private static MethodDefinition RequireRefreshData(AssemblyDefinition asm)
    {
        var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser")
            ?? throw new InvalidOperationException("未找到 BattleProcesser");
        // RefreshData 可能重载；优先取带 body 且含 101800/101242/999999 常量的那个，
        // 官方新版已无排除常量时，退回任意带 body 的 RefreshData（视为无需改写）。
        MethodDefinition? hit = null;
        foreach (var m in type.Methods.Where(m => m.Name == "RefreshData" && m.HasBody))
        {
            foreach (var insn in m.Body.Instructions)
            {
                if (insn.OpCode == OpCodes.Ldc_I4 && insn.Operand is int v
                    && (v == ExcludedGoblin || v == ExcludedBat || v == DummyAnimId))
                {
                    hit = m;
                    break;
                }
            }

            if (hit != null)
            {
                break;
            }
        }

        if (hit != null)
        {
            return hit;
        }

        return type.Methods.FirstOrDefault(m => m.Name == "RefreshData" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 BattleProcesser.RefreshData");
    }
}
