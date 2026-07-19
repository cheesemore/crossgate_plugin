using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 挂机传送：OnClickTransmitCallback 跳过月卡/等级/任务/队友月卡，仅保留 TransmitCount 倒计时。
/// </summary>
internal static class AutoBattleTransmitBypassIlPatcher
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
                "用法: HotfixPatcher auto-battle-transmit-bypass-patch --hotfix <hotfix> --output <out>\n" +
                "      HotfixPatcher auto-battle-transmit-bypass-patch --hotfix <hotfix> --sniff-applied");
            return 1;
        }

        if (sniffApplied)
        {
            return RunSniffApplied(source) ? 0 : 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 挂机传送条件 bypass 补丁已写入: " + output);
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

        var panel = asm.MainModule.Types.First(t => t.Name == "AutoBattleNavigationPanel");
        var method = panel.Methods.First(m => m.Name == "OnClickTransmitCallback" && m.HasBody);
        var levelField = panel.Fields.First(f => f.Name == "m_level");
        var taskField = panel.Fields.First(f => f.Name == "m_task");

        var snapshot = ReadMethodBodyFromPe(origBytes, method.RVA);
        if (IsBypassApplied(snapshot, levelField))
        {
            Console.WriteLine("[SKIP] OnClickTransmitCallback 已是传送 bypass 补丁状态");
            return;
        }

        var newBody = (byte[])snapshot.Clone();
        var codeBase = GetCodeOffset(newBody);
        var patched = 0;

        patched += PatchMonthCardGate(newBody, codeBase, method.Body.Instructions);
        patched += PatchFieldCheck(newBody, codeBase, method.Body.Instructions, levelField, "m_level");
        patched += PatchFieldCheck(newBody, codeBase, method.Body.Instructions, taskField, "m_task");
        patched += PatchTeammateMonthCardLoop(newBody, codeBase, method.Body.Instructions);

        if (patched != 4)
        {
            throw new InvalidOperationException(
                $"OnClickTransmitCallback bypass 补丁不完整（成功 {patched}/4），IL 可能已变化");
        }

        BinaryPeWriter.ReplaceMethodBody(data, method.RVA, snapshot, newBody);
        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] 传送：跳过月卡/等级/任务/队友月卡，保留倒计时");
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

        var panel = asm.MainModule.Types.First(t => t.Name == "AutoBattleNavigationPanel");
        var method = panel.Methods.First(m => m.Name == "OnClickTransmitCallback" && m.HasBody);
        var levelField = panel.Fields.First(f => f.Name == "m_level");
        var snapshot = ReadMethodBodyFromPe(origBytes, method.RVA);
        return IsBypassApplied(snapshot, levelField);
    }

    public static bool RunSniffApplied(string hotfixPath)
    {
        if (SniffApplied(hotfixPath))
        {
            Console.WriteLine("[SNIFF] 传送 bypass 已生效（仅保留倒计时）");
            return true;
        }

        Console.WriteLine("[SNIFF] 未打传送 bypass 补丁");
        return false;
    }

    private static int PatchMonthCardGate(byte[] body, int codeBase, IList<Instruction> instructions)
    {
        for (var i = 0; i < instructions.Count - 1; i++)
        {
            var call = instructions[i];
            if (call.OpCode != OpCodes.Callvirt
                || call.Operand is not MethodReference called
                || called.Name != "get_MonthCardOpen")
            {
                continue;
            }

            var branch = instructions[i + 1];
            if (branch.OpCode != OpCodes.Brfalse)
            {
                continue;
            }

            var target = instructions[i + 2];
            WriteLongBranch(body, codeBase, branch.Offset, OpCodes.Br, target.Offset);
            return 1;
        }

        return 0;
    }

    private static int PatchFieldCheck(
        byte[] body,
        int codeBase,
        IList<Instruction> instructions,
        FieldReference field,
        string fieldName)
    {
        var fieldToken = BitConverter.GetBytes(field.MetadataToken.ToUInt32());

        for (var i = 0; i < instructions.Count - 1; i++)
        {
            var load = instructions[i];
            if (load.OpCode != OpCodes.Ldfld)
            {
                continue;
            }

            var abs = codeBase + load.Offset;
            if (body[abs] != (byte)OpCodes.Ldfld.Value
                || body[abs + 1] != fieldToken[0]
                || body[abs + 2] != fieldToken[1]
                || body[abs + 3] != fieldToken[2]
                || body[abs + 4] != fieldToken[3])
            {
                continue;
            }

            var branch = instructions[i + 1];
            if (branch.OpCode != OpCodes.Brtrue_S || branch.Operand is not Instruction target)
            {
                continue;
            }

            WriteShortBranch(body, codeBase, branch.Offset, OpCodes.Br_S, target.Offset);
            return 1;
        }

        throw new InvalidOperationException($"未找到 {fieldName} 检查分支");
    }

    private static int PatchTeammateMonthCardLoop(byte[] body, int codeBase, IList<Instruction> instructions)
    {
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
            if (branch.OpCode != OpCodes.Brtrue_S || branch.Operand is not Instruction target)
            {
                continue;
            }

            // 跳过方法入口处的 PlayerDataHolder.playerData.MonthCardOpen（已在 PatchMonthCardGate 处理）
            if (i < 8)
            {
                continue;
            }

            WriteShortBranch(body, codeBase, branch.Offset, OpCodes.Br_S, target.Offset);
            return 1;
        }

        throw new InvalidOperationException("未找到队友月卡检查分支");
    }

    private static bool IsBypassApplied(byte[] body, FieldReference levelField)
    {
        var fieldToken = BitConverter.GetBytes(levelField.MetadataToken.ToUInt32());
        var codeBase = GetCodeOffset(body);
        var codeSize = GetCodeSize(body);

        for (var i = codeBase; i <= codeBase + codeSize - 6; i++)
        {
            if (body[i] != (byte)OpCodes.Ldfld.Value
                || body[i + 1] != fieldToken[0]
                || body[i + 2] != fieldToken[1]
                || body[i + 3] != fieldToken[2]
                || body[i + 4] != fieldToken[3])
            {
                continue;
            }

            return body[i + 5] == (byte)OpCodes.Br_S.Value;
        }

        return false;
    }

    private static void WriteLongBranch(byte[] body, int codeBase, int branchOffset, OpCode opcode, int targetOffset)
    {
        var abs = codeBase + branchOffset;
        body[abs] = (byte)opcode.Value;
        var rel = targetOffset - (branchOffset + 5);
        BitConverter.GetBytes(rel).CopyTo(body, abs + 1);
    }

    private static void WriteShortBranch(byte[] body, int codeBase, int branchOffset, OpCode opcode, int targetOffset)
    {
        var abs = codeBase + branchOffset;
        body[abs] = (byte)opcode.Value;
        var rel = targetOffset - (branchOffset + 2);
        body[abs + 1] = (byte)rel;
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

    private static int GetCodeSize(byte[] methodBody)
    {
        var flags = methodBody[0];
        if ((flags & 0x3) == 0x2)
        {
            return flags >> 2;
        }

        return BitConverter.ToInt32(methodBody, 4);
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
