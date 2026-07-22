using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 协议 69（OnCommandPlayerCallback）：AcountList 由 P1..PN 扩为
/// P1 P2 … P(N-1) P1 P2 … P(N-1) PN。
/// 实现：主循环跳过最后一人 Add；主循环结束后次轮 Add 前 N-1 人，再 Add 最后一人。
/// </summary>
internal static class BattleNineActionIlBuilder
{
    public static void ApplyOnCommandPlayerPatches(MethodDefinition method, ModuleDefinition module)
    {
        if (IsHookInstalled(method))
        {
            return;
        }

        var body = method.Body;
        var insns = body.Instructions;
        var addCall = FindAcountListAdd(insns)
            ?? throw new InvalidOperationException("OnCommandPlayerCallback 未找到 AcountList.Add");

        var acountListLoad = addCall.Previous!.Previous
            ?? throw new InvalidOperationException("AcountList.Add 前缺少 ldsfld");

        var loopBlt = FindMainLoopBlt(insns)
            ?? throw new InvalidOperationException("未找到主循环末尾 blt");

        var getCount = FindPlayerCountCall(insns)
            ?? throw new InvalidOperationException("未找到 Player.get_Count");
        var getPlayer = getCount.Previous
            ?? throw new InvalidOperationException("get_Count 前缺少 get_Player");
        if (getPlayer.OpCode != OpCodes.Callvirt
            || getPlayer.Operand is not MethodReference getPlayerRef
            || getPlayerRef.Name != "get_Player")
        {
            throw new InvalidOperationException("get_Count 前一条不是 get_Player");
        }

        var getItem = FindLoopPlayerGetItem(insns)
            ?? throw new InvalidOperationException("未找到主循环 Player.get_Item");
        var getUid = FindPlayerGetUid(insns)
            ?? throw new InvalidOperationException("未找到 Proto_BattlePlayer.get_Uid");

        var battleData = Defn(module, "BattleDataHolder");
        var acountListField = module.ImportReference(battleData.Fields.First(f => f.Name == "AcountList"));
        var addMethod = module.ImportReference((MethodReference)addCall.Operand!);
        var getPlayerMethod = module.ImportReference(getPlayerRef);
        var getCountMethod = module.ImportReference((MethodReference)getCount.Operand!);
        var getItemMethod = module.ImportReference((MethodReference)getItem.Operand!);
        var getUidMethod = module.ImportReference((MethodReference)getUid.Operand!);
        var uidLoadInsn = addCall.Previous
            ?? throw new InvalidOperationException("AcountList.Add 前缺少 uid 加载");
        var uidStlocInsn = uidLoadInsn.Previous!.Previous
            ?? throw new InvalidOperationException("AcountList.Add 前缺少 uid 暂存");

        var il = body.GetILProcessor();
        var skipMainAdd = il.Create(OpCodes.Nop);

        // 主循环：i >= Count-1 时跳过 Add（首轮只收集 P1..P(N-1)）
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Ldloc_1));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Ldloc_0));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Callvirt, getPlayerMethod));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Callvirt, getCountMethod));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Sub));
        il.InsertBefore(acountListLoad, il.Create(OpCodes.Bge_S, skipMainAdd));
        il.InsertAfter(addCall, skipMainAdd);

        // 次轮：for j=0..Count-2 Add；最后 Add Player[Count-1]（复用 ldloc.1 作 j）
        var loopHead = il.Create(OpCodes.Ldloc_1);
        var afterLoop = il.Create(OpCodes.Nop);
        var anchor = loopBlt;

        void Append(Instruction insn)
        {
            il.InsertAfter(anchor, insn);
            anchor = insn;
        }

        void AppendAddUidChain()
        {
            Append(CloneStore(uidStlocInsn, il));
            Append(il.Create(OpCodes.Ldsfld, acountListField));
            Append(CloneLoad(uidLoadInsn, il));
            Append(il.Create(OpCodes.Callvirt, addMethod));
        }

        Append(il.Create(OpCodes.Ldc_I4_0));
        Append(il.Create(OpCodes.Stloc_1));
        Append(loopHead);
        Append(il.Create(OpCodes.Ldloc_0));
        Append(il.Create(OpCodes.Callvirt, getPlayerMethod));
        Append(il.Create(OpCodes.Callvirt, getCountMethod));
        Append(il.Create(OpCodes.Ldc_I4_1));
        Append(il.Create(OpCodes.Sub));
        Append(il.Create(OpCodes.Bge_S, afterLoop));
        Append(il.Create(OpCodes.Ldloc_0));
        Append(il.Create(OpCodes.Callvirt, getPlayerMethod));
        Append(il.Create(OpCodes.Ldloc_1));
        Append(il.Create(OpCodes.Callvirt, getItemMethod));
        Append(il.Create(OpCodes.Callvirt, getUidMethod));
        AppendAddUidChain();
        Append(il.Create(OpCodes.Ldloc_1));
        Append(il.Create(OpCodes.Ldc_I4_1));
        Append(il.Create(OpCodes.Add));
        Append(il.Create(OpCodes.Stloc_1));
        Append(il.Create(OpCodes.Br, loopHead));
        Append(afterLoop);
        Append(il.Create(OpCodes.Ldloc_0));
        Append(il.Create(OpCodes.Callvirt, getPlayerMethod));
        Append(il.Create(OpCodes.Callvirt, getCountMethod));
        Append(il.Create(OpCodes.Ldc_I4_1));
        Append(il.Create(OpCodes.Sub));
        Append(il.Create(OpCodes.Stloc_1));
        Append(il.Create(OpCodes.Ldloc_0));
        Append(il.Create(OpCodes.Callvirt, getPlayerMethod));
        Append(il.Create(OpCodes.Ldloc_1));
        Append(il.Create(OpCodes.Callvirt, getItemMethod));
        Append(il.Create(OpCodes.Callvirt, getUidMethod));
        AppendAddUidChain();

        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
    }

    internal static bool IsHookInstalled(MethodDefinition method)
    {
        var insns = method.Body.Instructions;
        if (CountAcountListAdds(insns) < 3)
        {
            return false;
        }

        var addCall = FindAcountListAdd(insns);
        if (addCall?.Previous?.Previous?.OpCode != OpCodes.Ldsfld)
        {
            return false;
        }

        var load = addCall.Previous.Previous;
        for (var p = load.Previous; p != null; p = p.Previous)
        {
            if (p.OpCode == OpCodes.Ldloc_1)
            {
                return HasPostLoopAfterMainBlt(insns);
            }

            if (p.OpCode == OpCodes.Callvirt
                && p.Operand is MethodReference call
                && call.Name == "get_Uid")
            {
                return false;
            }
        }

        return false;
    }

    private static bool HasPostLoopAfterMainBlt(IList<Instruction> insns)
    {
        var blt = FindMainLoopBlt(insns);
        if (blt?.Next == null)
        {
            return false;
        }

        // blt → ldc.i4.0 → stloc.1（次轮扩队）
        return blt.Next.OpCode == OpCodes.Ldc_I4_0
            && blt.Next.Next?.OpCode == OpCodes.Stloc_1;
    }

    private static Instruction? FindMainLoopBlt(IList<Instruction> insns)
    {
        for (var i = 4; i < insns.Count; i++)
        {
            if (insns[i].OpCode != OpCodes.Blt)
            {
                continue;
            }

            if (insns[i - 1].OpCode == OpCodes.Callvirt
                && insns[i - 1].Operand is MethodReference countCall
                && countCall.Name == "get_Count"
                && insns[i - 2].OpCode == OpCodes.Callvirt
                && insns[i - 3].OpCode == OpCodes.Ldloc_0
                && insns[i - 4].OpCode == OpCodes.Ldloc_1)
            {
                return insns[i];
            }
        }

        return null;
    }

    private static int CountAcountListAdds(IList<Instruction> insns)
    {
        var count = 0;
        for (var i = 1; i < insns.Count; i++)
        {
            if (FindAcountListAddAt(insns, i) != null)
            {
                count++;
            }
        }

        return count;
    }

    private static Instruction? FindAcountListAdd(IList<Instruction> insns)
    {
        for (var i = 1; i < insns.Count; i++)
        {
            var hit = FindAcountListAddAt(insns, i);
            if (hit != null)
            {
                return hit;
            }
        }

        return null;
    }

    private static Instruction? FindAcountListAddAt(IList<Instruction> insns, int i)
    {
        var insn = insns[i];
        if (insn.OpCode != OpCodes.Callvirt
            || insn.Operand is not MethodReference call
            || call.Name != "Add")
        {
            return null;
        }

        if (call.DeclaringType.Name.StartsWith("List`1", StringComparison.Ordinal)
            && i >= 2
            && insns[i - 2].OpCode == OpCodes.Ldsfld
            && insns[i - 2].Operand is FieldReference field
            && field.Name == "AcountList"
            && IsUidLoad(insns[i - 1]))
        {
            return insn;
        }

        return null;
    }

    private static Instruction? FindLoopPlayerGetItem(IList<Instruction> insns)
    {
        for (var i = 2; i < insns.Count; i++)
        {
            var insn = insns[i];
            if (insn.OpCode != OpCodes.Callvirt
                || insn.Operand is not MethodReference call
                || call.Name != "get_Item"
                || !call.DeclaringType.Name.StartsWith("RepeatedField`1", StringComparison.Ordinal))
            {
                continue;
            }

            var indexLoad = insns[i - 1];
            var playerLoad = insns[i - 2];
            if (indexLoad.OpCode != OpCodes.Ldloc_1)
            {
                continue;
            }

            if (playerLoad.OpCode == OpCodes.Callvirt
                && playerLoad.Operand is MethodReference gp
                && gp.Name == "get_Player")
            {
                return insn;
            }
        }

        return null;
    }

    private static Instruction? FindPlayerGetUid(IList<Instruction> insns)
    {
        foreach (var insn in insns)
        {
            if (insn.OpCode == OpCodes.Callvirt
                && insn.Operand is MethodReference call
                && call.Name == "get_Uid"
                && call.DeclaringType.Name == "Proto_BattlePlayer")
            {
                return insn;
            }
        }

        return null;
    }

    private static Instruction? FindPlayerCountCall(IList<Instruction> insns)
    {
        Instruction? last = null;
        foreach (var insn in insns)
        {
            if (insn.OpCode == OpCodes.Callvirt
                && insn.Operand is MethodReference call
                && call.Name == "get_Count"
                && call.DeclaringType.Name.StartsWith("RepeatedField`1", StringComparison.Ordinal))
            {
                last = insn;
            }
        }

        return last;
    }

    private static bool IsUidLoad(Instruction insn)
    {
        return insn.OpCode == OpCodes.Ldloc
            || insn.OpCode == OpCodes.Ldloc_S
            || insn.OpCode == OpCodes.Ldloc_0
            || insn.OpCode == OpCodes.Ldloc_1
            || insn.OpCode == OpCodes.Ldloc_2
            || insn.OpCode == OpCodes.Ldloc_3;
    }

    private static Instruction CloneStore(Instruction src, ILProcessor il)
    {
        if (src.OpCode == OpCodes.Stloc_S)
        {
            return il.Create(OpCodes.Stloc_S, (VariableDefinition)src.Operand!);
        }

        if (src.OpCode == OpCodes.Stloc)
        {
            return il.Create(OpCodes.Stloc, (VariableDefinition)src.Operand!);
        }

        throw new InvalidOperationException($"不支持的 uid 暂存指令: {src.OpCode}");
    }

    private static Instruction CloneLoad(Instruction src, ILProcessor il)
    {
        if (src.OpCode == OpCodes.Ldloc_0)
        {
            return il.Create(OpCodes.Ldloc_0);
        }

        if (src.OpCode == OpCodes.Ldloc_1)
        {
            return il.Create(OpCodes.Ldloc_1);
        }

        if (src.OpCode == OpCodes.Ldloc_2)
        {
            return il.Create(OpCodes.Ldloc_2);
        }

        if (src.OpCode == OpCodes.Ldloc_3)
        {
            return il.Create(OpCodes.Ldloc_3);
        }

        if (src.OpCode == OpCodes.Ldloc_S)
        {
            return il.Create(OpCodes.Ldloc_S, (VariableDefinition)src.Operand!);
        }

        if (src.OpCode == OpCodes.Ldloc)
        {
            return il.Create(OpCodes.Ldloc, (VariableDefinition)src.Operand!);
        }

        throw new InvalidOperationException($"不支持的 uid 加载指令: {src.OpCode}");
    }

    private static TypeDefinition Defn(ModuleDefinition module, string name)
    {
        return module.Types.FirstOrDefault(t => t.Name == name)
            ?? module.Types.SelectMany(CecilHelpers.NestedTypes).FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException("未找到类型: " + name);
    }
}
