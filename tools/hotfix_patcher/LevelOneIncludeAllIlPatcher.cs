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

        var (method, builtin) = FindRefreshData(asm);
        if (builtin)
        {
            Console.WriteLine(
                "[SKIP] 官方已内建：RefreshData 不再排除哥布林/迷你蝙蝠，LevelOneFlag 覆盖全部一级");
            return;
        }

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
            var (method, builtin) = FindRefreshData(asm);
            if (builtin)
            {
                return true;
            }

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

    /// <summary>
    /// 定位 BattleProcesser.RefreshData。
    /// 返回 (method, builtin)：builtin=true 表示新版官方已删除哥布林/蝙蝠排除（LevelOneFlag 覆盖全部一级），无需补丁。
    /// </summary>
    private static (MethodDefinition, bool) FindRefreshData(AssemblyDefinition asm)
    {
        var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser")
            ?? throw new InvalidOperationException("未找到 BattleProcesser");

        MethodDefinition? withExclusion = null;
        MethodDefinition? withFlag = null;
        foreach (var m in type.Methods.Where(m => m.Name == "RefreshData" && m.HasBody))
        {
            var hasExclusion = false;
            var hasFlagStore = false;
            foreach (var insn in m.Body.Instructions)
            {
                if (insn.OpCode == OpCodes.Ldc_I4 && insn.Operand is int v)
                {
                    if (v == ExcludedGoblin || v == ExcludedBat || v == DummyAnimId)
                    {
                        hasExclusion = true;
                    }
                }
                else if (insn.OpCode == OpCodes.Stfld
                         && insn.Operand is FieldReference f
                         && f.Name == "LevelOneFlag")
                {
                    hasFlagStore = true;
                }
            }

            if (hasExclusion)
            {
                withExclusion = m;
                break;
            }

            if (hasFlagStore && withFlag == null)
            {
                withFlag = m;
            }
        }

        if (withExclusion != null)
        {
            return (withExclusion, false);
        }

        if (withFlag != null)
        {
            // 新版：仍有 LevelOneFlag 逻辑但没有 101800/101242 排除 → 官方已内建
            return (withFlag, true);
        }

        throw new InvalidOperationException(
            "未找到含 LevelOne 排除常量或 LevelOneFlag 写入的 BattleProcesser.RefreshData");
    }
}
