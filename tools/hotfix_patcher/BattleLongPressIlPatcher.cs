using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 战斗内长按单位：去掉 BattleRole.OnLongPress 的 P_vs_E 模式守卫。
/// 仅原地改 beq.s → br.s 一字节，不 Cecil 重序列化。
/// </summary>
internal static class BattleLongPressIlPatcher
{
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
                "用法: HotfixPatcher battle-longpress-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher battle-longpress-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (restore)
        {
            Restore(source, output);
            Console.WriteLine("[OK] 战斗长按面板补丁已回退: " + output);
            return 0;
        }

        Apply(source, output);
        Console.WriteLine("[OK] 战斗长按面板补丁完成: " + output);
        return 0;
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

        var method = RequireOnLongPress(asm);
        var guard = FindGuardBranch(method);
        if (guard == null)
        {
            throw new InvalidOperationException("未找到 OnLongPress 的 battleModeFlag 守卫");
        }

        var (codeFileOff, _) = GetMethodCodeRange(data, method.RVA);
        var branchFileOff = codeFileOff + guard.Value.ilOffset;

        if (data[branchFileOff] == 0x2B)
        {
            Console.WriteLine("[SKIP] BattleRole.OnLongPress 已去除 P_vs_E 守卫");
        }
        else if (data[branchFileOff] == 0x2E)
        {
            data[branchFileOff] = 0x2B;
            Console.WriteLine("[PATCH] BattleRole.OnLongPress: beq.s -> br.s（跳过 P_vs_E 守卫，原地 1 字节）");
        }
        else
        {
            throw new InvalidOperationException(
                $"OnLongPress 守卫分支 opcode 异常: 0x{data[branchFileOff]:X2} @ file 0x{branchFileOff:X}");
        }

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static void Restore(string sourcePath, string outputPath)
    {
        File.Copy(sourcePath, outputPath, overwrite: true);
        Console.WriteLine($"[RESTORE] 已从原版复制: {sourcePath}");
    }

    private static (int ilOffset, OpCode op)? FindGuardBranch(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 3; i++)
        {
            if (instructions[i].OpCode != OpCodes.Ldsfld
                || instructions[i].Operand is not FieldReference field
                || field.Name != "battleModeFlag")
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Ldc_I4_1)
            {
                continue;
            }

            var branch = instructions[i + 2];
            if (branch.OpCode != OpCodes.Beq_S && branch.OpCode != OpCodes.Br_S)
            {
                continue;
            }

            if (instructions[i + 3].OpCode != OpCodes.Ret)
            {
                continue;
            }

            return (branch.Offset, branch.OpCode);
        }

        return null;
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

    private static MethodDefinition RequireOnLongPress(AssemblyDefinition asm)
    {
        var battleRole = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleRole")
            ?? throw new InvalidOperationException("未找到 BattleRole");
        return battleRole.Methods.FirstOrDefault(m => m.Name == "OnLongPress" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 BattleRole.OnLongPress");
    }
}
