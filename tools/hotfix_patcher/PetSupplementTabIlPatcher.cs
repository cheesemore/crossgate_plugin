using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 洗练 Tab 强制为补档布局：
/// 右侧先显示选宠空位（m_Btn_AddPet），选宠后为 Com_RefinementAfterBg 补档属性勾选；
/// 左侧隐藏洗练次数图标；底部为合成补档（m_Obj_Polishing）。
/// </summary>
internal static class PetSupplementTabIlPatcher
{
    private const int ExpectedSize = 6_355_968;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;

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
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("用法: HotfixPatcher pet-supplement-tab-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 宠物补档界面补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        if (origBytes.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"源文件体积应为 {ExpectedSize}，实际 {origBytes.Length}");
        }

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

        var refinement = asm.MainModule.Types.First(t => t.Name == "Com_PetRefinement");
        var refreshPage = refinement.Methods.First(m => m.Name == "RefreshPage" && m.HasBody);
        var refreshUi = refinement.Methods.First(m => m.Name == "RefreshRefinementUI" && m.HasBody);
        var refreshMoney = refinement.Methods.First(m => m.Name == "RefreshMoney" && m.HasBody);
        var onPolishing = refinement.Methods.First(m => m.Name == "OnClickPolishing" && m.HasBody);

        var beforeBg = asm.MainModule.Types.First(t => t.Name == "Com_RefinementBeforeBg");
        var beforeSetData = beforeBg.Methods.First(m => m.Name == "SetData" && m.HasBody);

        var targets = new[]
        {
            refreshPage,
            refreshUi,
            refreshMoney,
            onPolishing,
            beforeSetData,
        };

        if (IsAlreadyPatched(refreshPage, refreshUi, beforeSetData))
        {
            throw new InvalidOperationException("宠物补档界面补丁已包含");
        }

        var snapshots = targets.ToDictionary(
            m => m,
            m => ReadMethodBodyFromPe(origBytes, m.RVA));

        // RefreshPage：跳过 GroomCostCount 洗练右栏，再强制走 SupplementCost 补档右栏（AddPet）
        ForceSkipGroomCostRefinementBranch(refreshPage);
        ForceEnterSupplementBlock(refreshPage, "RefreshPage");

        // RefreshRefinementUI：隐藏洗练区/连续洗练，显示合成补档区
        ForceEnterSupplementBlock(refreshUi, "RefreshRefinementUI");

        // 底部费用显示走补档货币
        ForceEnterSupplementBlock(refreshMoney, "RefreshMoney");

        // 允许点击合成补档（仍依赖服务端 SupplementCost 数值）
        ForceEnterSupplementBlock(onPolishing, "OnClickPolishing");

        // 左侧不显示洗练次数角标（与原生补档宠一致）
        ForceTakeLowCostBranch(beforeSetData, "get_GroomCostCount", "Com_RefinementBeforeBg.SetData");

        foreach (var method in targets)
        {
            IlSerializer.RecalculateOffsets(method.Body);
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (ushort)8);
            var newBody = IlSerializer.Serialize(method.Body, snapshots[method]);
            BinaryPeWriter.ReplaceMethodBody(data, method.RVA, snapshots[method], newBody);
        }

        if (data.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] Com_PetRefinement -> 洗练 Tab 强制补档 UI（右栏选宠+属性勾选+合成补档）");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    /// <summary>
    /// GroomCostCount &gt; 0 时原逻辑会显示洗练预览右栏；改为始终进入 SupplementCost 判断。
    /// </summary>
    private static void ForceSkipGroomCostRefinementBranch(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != "get_GroomCostCount")
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Ldc_I4_0)
            {
                continue;
            }

            var branch = instructions[i + 2];
            if (branch.OpCode != OpCodes.Ble_S && branch.OpCode != OpCodes.Ble)
            {
                continue;
            }

            branch.OpCode = branch.OpCode == OpCodes.Ble_S ? OpCodes.Br_S : OpCodes.Br;
            return;
        }

        throw new InvalidOperationException("未找到 RefreshPage 的 GroomCostCount 分支");
    }

    /// <summary>
    /// SupplementCost &lt;= 0 时跳过补档块；改为始终进入补档块（ble 目标之后的第一条指令）。
    /// </summary>
    private static void ForceEnterSupplementBlock(MethodDefinition method, string label)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != "get_SupplementCost")
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Ldc_I4_0)
            {
                continue;
            }

            var branch = instructions[i + 2];
            if (branch.OpCode != OpCodes.Ble_S && branch.OpCode != OpCodes.Ble)
            {
                continue;
            }

            if (i + 3 >= instructions.Count)
            {
                throw new InvalidOperationException($"{label} 的 SupplementCost 分支后无补档块");
            }

            var supplementEntry = instructions[i + 3];
            branch.OpCode = branch.OpCode == OpCodes.Ble_S ? OpCodes.Br_S : OpCodes.Br;
            branch.Operand = supplementEntry;
            return;
        }

        throw new InvalidOperationException($"未找到 {label} 的 SupplementCost 分支");
    }

    /// <summary>
    /// 对 x &lt;= 0 的分支，强制走「低值」侧（如隐藏洗练次数图标）。
    /// </summary>
    private static void ForceTakeLowCostBranch(
        MethodDefinition method,
        string getterName,
        string label)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != getterName)
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Ldc_I4_0)
            {
                continue;
            }

            var branch = instructions[i + 2];
            if (branch.OpCode != OpCodes.Ble_S && branch.OpCode != OpCodes.Ble)
            {
                continue;
            }

            branch.OpCode = branch.OpCode == OpCodes.Ble_S ? OpCodes.Br_S : OpCodes.Br;
            return;
        }

        throw new InvalidOperationException($"未找到 {label} 的 {getterName} 分支");
    }

    private static bool IsAlreadyPatched(
        MethodDefinition refreshPage,
        MethodDefinition refreshUi,
        MethodDefinition beforeSetData)
    {
        return HasForcedGroomSkip(refreshPage)
            && HasForcedSupplementEntry(refreshPage)
            && HasForcedSupplementEntry(refreshUi)
            && HasForcedLowBranch(beforeSetData, "get_GroomCostCount");
    }

    private static bool HasForcedGroomSkip(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != "get_GroomCostCount")
            {
                continue;
            }

            var branch = instructions[i + 2];
            return branch.OpCode == OpCodes.Br_S || branch.OpCode == OpCodes.Br;
        }

        return false;
    }

    private static bool HasForcedSupplementEntry(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != "get_SupplementCost")
            {
                continue;
            }

            var branch = instructions[i + 2];
            if (branch.OpCode != OpCodes.Br_S && branch.OpCode != OpCodes.Br)
            {
                continue;
            }

            return ReferenceEquals(branch.Operand, instructions[i + 3]);
        }

        return false;
    }

    private static bool HasForcedLowBranch(MethodDefinition method, string getterName)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt
                || instructions[i].Operand is not MethodReference called
                || called.Name != getterName)
            {
                continue;
            }

            var branch = instructions[i + 2];
            return branch.OpCode == OpCodes.Br_S || branch.OpCode == OpCodes.Br;
        }

        return false;
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
