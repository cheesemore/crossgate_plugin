using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// DoAutoFight：跳过 MonthCardOpen 门闩。
/// 无月卡时只要玩家打开了 VIP 自动开关（GetAutoSkillSwitch==1），仍走 DoVip*；
/// 开关关闭时仍走 AutoFight_PlayerAction / PetAction，与原先非 VIP 分支一致。
/// </summary>
internal static class VipAutoMonthCardBypassIlPatcher
{
    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var sniffApplied = false;

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
                case "--sniff-applied":
                    sniffApplied = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher vip-auto-monthcard-bypass-patch --hotfix <hotfix> --output <out>\n" +
                "      HotfixPatcher vip-auto-monthcard-bypass-patch --hotfix <hotfix> --sniff-applied");
            return 1;
        }

        if (sniffApplied)
        {
            return RunSniffApplied(source) ? 0 : 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] VIP自动月卡门闩 bypass 已写入: " + output);
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath)
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

        var method = asm.MainModule.Types.First(t => t.Name == "BattleProcesser")
            .Methods.First(m => m.Name == "DoAutoFight" && m.HasBody);

        var snapshot = ReadMethodBodyFromPe(origBytes, method.RVA);
        if (IsBypassApplied(snapshot, method.Body.Instructions))
        {
            Console.WriteLine("[SKIP] DoAutoFight 已是 VIP 月卡 bypass 状态");
            return;
        }

        var newBody = (byte[])snapshot.Clone();
        if (!PatchMonthCardOpenToTrue(newBody, method.Body.Instructions))
        {
            throw new InvalidOperationException("未找到 DoAutoFight 中的 get_MonthCardOpen + brfalse");
        }

        BinaryPeWriter.ReplaceMethodBody(data, method.RVA, snapshot, newBody);
        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] DoAutoFight：MonthCardOpen 恒真（VIP开关仍由玩家控制）");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static bool SniffApplied(string hotfixPath)
    {
        var origBytes = File.ReadAllBytes(hotfixPath);
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

        var method = asm.MainModule.Types.First(t => t.Name == "BattleProcesser")
            .Methods.First(m => m.Name == "DoAutoFight" && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(origBytes, method.RVA);
        return IsBypassApplied(snapshot, method.Body.Instructions);
    }

    private static bool RunSniffApplied(string hotfixPath)
    {
        if (SniffApplied(hotfixPath))
        {
            Console.WriteLine("[SNIFF] VIP自动月卡 bypass 已生效");
            return true;
        }

        Console.WriteLine("[SNIFF] 未打 VIP自动月卡 bypass");
        return false;
    }

    /// <summary>
    /// callvirt get_MonthCardOpen (5B) → pop; ldc.i4.1; nop; nop; nop。
    /// 随后 brfalse 因栈顶为 1 永不跳转，进入 VIP 分支；开关仍决定是否 DoVip*。
    /// </summary>
    private static bool PatchMonthCardOpenToTrue(byte[] body, IList<Instruction> instructions)
    {
        var codeBase = GetCodeOffset(body);
        for (var i = 0; i < instructions.Count - 1; i++)
        {
            var call = instructions[i];
            if (call.OpCode != OpCodes.Callvirt
                || call.Operand is not MethodReference called
                || called.Name != "get_MonthCardOpen"
                || called.DeclaringType.Name != "PlayerData")
            {
                continue;
            }

            var branch = instructions[i + 1];
            if (branch.OpCode != OpCodes.Brfalse && branch.OpCode != OpCodes.Brfalse_S)
            {
                continue;
            }

            var abs = codeBase + call.Offset;
            if (body[abs] != (byte)OpCodes.Callvirt.Value)
            {
                return false;
            }

            body[abs] = (byte)OpCodes.Pop.Value;
            body[abs + 1] = (byte)OpCodes.Ldc_I4_1.Value;
            body[abs + 2] = (byte)OpCodes.Nop.Value;
            body[abs + 3] = (byte)OpCodes.Nop.Value;
            body[abs + 4] = (byte)OpCodes.Nop.Value;
            return true;
        }

        return false;
    }

    private static bool IsBypassApplied(byte[] body, IList<Instruction> instructions)
    {
        var codeBase = GetCodeOffset(body);
        // 补丁特征：pop; ldc.i4.1; nop; nop; nop（原 callvirt get_MonthCardOpen 五字节位）
        for (var i = codeBase; i + 4 < body.Length; i++)
        {
            if (body[i] == (byte)OpCodes.Pop.Value
                && body[i + 1] == (byte)OpCodes.Ldc_I4_1.Value
                && body[i + 2] == (byte)OpCodes.Nop.Value
                && body[i + 3] == (byte)OpCodes.Nop.Value
                && body[i + 4] == (byte)OpCodes.Nop.Value)
            {
                return true;
            }
        }

        _ = instructions;
        return false;
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
