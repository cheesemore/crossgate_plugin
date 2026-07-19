using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class FriendlyScopeIl
{
    internal sealed class Refs
    {
        public required MethodReference BattleSelfRoleIndex { get; init; }
        public required MethodReference BattleCharIndex { get; init; }
        public required FieldReference BattleRoleDataChar { get; init; }
        public required MethodReference BattleRoleGetSide { get; init; }
        public required MethodReference BattlePlayerUnitSide { get; init; }
        public required MethodReference PlayerDataHolderPlayerData { get; init; }
        public required FieldReference PlayerDataObjIndex { get; init; }
        public required FieldReference CharacterDataObjIndex { get; init; }
        public required FieldReference CharacterDataPerfectPet { get; init; }
        public required FieldReference CharacterEntityData { get; init; }
        public required MethodReference CheckGroupMember { get; init; }
        public required MethodReference BattleRoleDataEffectId { get; init; }
    }

    public static Refs Resolve(ModuleDefinition module)
    {
        var battleDataHolder = RequireType(module, "BattleDataHolder");
        var battleRoleData = RequireType(module, "BattleRoleData");
        var battleRole = RequireType(module, "BattleRole");
        var protoBattleChar = RequireType(module, "Proto_BattleChar");
        var playerDataHolder = RequireType(module, "PlayerDataHolder");
        var playerData = RequireType(module, "PlayerData");
        var characterData = RequireType(module, "CharacterData");
        var characterEntity = RequireType(module, "CharacterEntity");
        var groupManager = RequireType(module, "GroupManager");

        return new Refs
        {
            BattleSelfRoleIndex = RequireGetter(battleDataHolder, "selfRoleIndex"),
            BattleCharIndex = RequireGetter(protoBattleChar, "Index"),
            BattleRoleDataChar = RequireField(battleRoleData, "Char"),
            BattleRoleGetSide = RequireGetter(battleRole, "Side"),
            BattlePlayerUnitSide = RequireGetter(battleDataHolder, "battlePlayerUnitSide"),
            PlayerDataHolderPlayerData = RequireGetter(playerDataHolder, "playerData"),
            PlayerDataObjIndex = RequireField(playerData, "objindex"),
            CharacterDataObjIndex = RequireField(characterData, "objindex"),
            CharacterDataPerfectPet = RequireField(characterData, "PerfectPet"),
            CharacterEntityData = RequireField(characterEntity, "m_Data"),
            CheckGroupMember = RequireStaticMethod(groupManager, "CheckGroupMember", 1),
            BattleRoleDataEffectId = RequireGetter(battleRoleData, "EffectID"),
        };
    }

    public static bool PatchBattleRefreshDataSkin(MethodDefinition method, Refs refs)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt || instructions[i].Operand is not MethodReference called
                || called.Name != "GetPerfectPetSkin")
            {
                continue;
            }

            Instruction? beqInsn = null;
            for (var j = i - 1; j >= Math.Max(0, i - 15); j--)
            {
                if (instructions[j].OpCode != OpCodes.Beq_S && instructions[j].OpCode != OpCodes.Beq)
                {
                    continue;
                }

                var target = (Instruction)instructions[j].Operand!;
                if (instructions.IndexOf(target) < i)
                {
                    beqInsn = instructions[j];
                    break;
                }
            }

            if (beqInsn == null)
            {
                return false;
            }

            var beqIdx = instructions.IndexOf(beqInsn);
            if (beqIdx < 5)
            {
                return false;
            }

            var skinTarget = (Instruction)beqInsn.Operand!;
            var il = method.Body.GetILProcessor();
            var insertBefore = instructions[beqIdx + 1];
            il.InsertBefore(insertBefore, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(insertBefore, il.Create(OpCodes.Callvirt, refs.BattleRoleGetSide));
            il.InsertBefore(insertBefore, il.Create(OpCodes.Call, refs.BattlePlayerUnitSide));
            il.InsertBefore(insertBefore, il.Create(OpCodes.Beq_S, skinTarget));

            var toRemove = new Instruction[6];
            for (var k = 0; k < 6; k++)
            {
                toRemove[k] = instructions[beqIdx - 5 + k];
            }

            foreach (var insn in toRemove)
            {
                il.Remove(insn);
            }

            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)8);
            return true;
        }

        return false;
    }

    public static bool PatchBattleDebuffAura(ModuleDefinition module, Refs refs)
    {
        var method = RequireType(module, "BattleProcesser").Methods
            .FirstOrDefault(m => m.Name == "RefreshDebuff" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 BattleProcesser.RefreshDebuff");

        var instructions = method.Body.Instructions;
        for (var i = 2; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode != OpCodes.Callvirt || instructions[i].Operand is not MethodReference called
                || called.Name != "get_Halo")
            {
                continue;
            }

            if (instructions[i - 1].OpCode != OpCodes.Ldfld || instructions[i - 2].OpCode != OpCodes.Ldloc_0)
            {
                continue;
            }

            var il = method.Body.GetILProcessor();
            il.Replace(instructions[i - 1], il.Create(OpCodes.Callvirt, refs.BattleRoleDataEffectId));
            il.Remove(instructions[i]);
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)4);
            return true;
        }

        return false;
    }

    public static bool PatchMapSkinByGroup(MethodDefinition method, Refs refs, int characterDataArg)
    {
        return ReplaceSkinBranch(
            method,
            (il, normal, skin) => InsertMapGroupBranch(il, normal, skin, refs, characterDataArg));
    }

    public static bool PatchMapSkinByEntityData(MethodDefinition method, Refs refs)
    {
        return ReplaceSkinBranch(method, (il, normal, skin) => InsertMapGroupBranchFromEntity(il, normal, skin, refs));
    }

    public static bool PatchMapPerfectPetBeforeMat(MethodDefinition method, Refs refs, int characterDataArg)
    {
        var arg = ArgOp(characterDataArg);
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode != OpCodes.Call || insn.Operand is not MethodReference called
                || called.Name != "GetAniMatConfig"
                || !called.Parameters[0].ParameterType.Name.Contains("CharacterData"))
            {
                continue;
            }

            var il = method.Body.GetILProcessor();
            var skipSet = il.Create(OpCodes.Nop);
            il.InsertBefore(insn, skipSet);
            il.InsertBefore(insn, il.Create(OpCodes.Stfld, refs.CharacterDataPerfectPet));
            il.InsertBefore(insn, il.Create(OpCodes.Ldc_I4_1));
            var setStart = il.Create(arg);
            il.InsertBefore(insn, setStart);
            InsertMapGroupBranch(il, skipSet, setStart, refs, characterDataArg);
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)8);
            return true;
        }

        return false;
    }

    public static bool InsertBattleEffectGuard(MethodDefinition method, Refs refs)
    {
        var point = FindCrestLookupPoint(method);
        if (point == null)
        {
            return false;
        }

        var il = method.Body.GetILProcessor();
        InsertBattleIndexSameSideBranch(il, FindReturnZero(method), point, refs);
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)8);
        return true;
    }

    public static bool InsertMapEffectGuard(MethodDefinition method, Refs refs)
    {
        var point = FindCrestLookupPoint(method);
        if (point == null)
        {
            return false;
        }

        var il = method.Body.GetILProcessor();
        InsertMapGroupBranch(il, FindReturnZero(method), point, refs, 0);
        method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)8);
        return true;
    }

    private static bool ReplaceSkinBranch(
        MethodDefinition method,
        Action<ILProcessor, Instruction, Instruction> insertBranch)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (!IsSkinBranch(instructions, i, out var skinTarget, out var normalTarget))
            {
                continue;
            }

            var il = method.Body.GetILProcessor();
            if (instructions[i].OpCode == OpCodes.Ldflda)
            {
                il.Remove(instructions[i + 3]);
                il.Remove(instructions[i + 2]);
                il.Remove(instructions[i + 1]);
                il.Remove(instructions[i]);
            }
            else if (instructions[i].OpCode == OpCodes.Callvirt)
            {
                il.Remove(instructions[i + 1]);
                il.Remove(instructions[i]);
            }
            else
            {
                il.Remove(instructions[i + 2]);
                il.Remove(instructions[i + 1]);
                il.Remove(instructions[i]);
            }

            insertBranch(il, normalTarget, skinTarget);
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)8);
            return true;
        }

        return false;
    }

    private static bool IsSkinBranch(
        IList<Instruction> instructions,
        int i,
        out Instruction skinTarget,
        out Instruction normalTarget)
    {
        skinTarget = null!;
        normalTarget = null!;

        if (i + 3 < instructions.Count
            && instructions[i].OpCode == OpCodes.Ldflda
            && IsPerfectPetFieldLoad(instructions[i + 1])
            && instructions[i + 2].OpCode == OpCodes.Ldc_I4_1
            && instructions[i + 3].OpCode == OpCodes.Beq_S)
        {
            skinTarget = (Instruction)instructions[i + 3].Operand!;
            normalTarget = instructions[i + 4];
            return true;
        }

        if (IsPerfectPetCheck(instructions[i])
            && instructions[i + 1].OpCode == OpCodes.Ldc_I4_1
            && instructions[i + 2].OpCode == OpCodes.Beq_S)
        {
            skinTarget = (Instruction)instructions[i + 2].Operand!;
            normalTarget = instructions[i + 3];
            return true;
        }

        if (instructions[i].OpCode == OpCodes.Callvirt
            && instructions[i].Operand is MethodReference called
            && called.Name == "get_isPrefectPet"
            && instructions[i + 1].OpCode == OpCodes.Brtrue_S)
        {
            skinTarget = (Instruction)instructions[i + 1].Operand!;
            normalTarget = instructions[i + 2];
            return true;
        }

        return false;
    }

    private static void InsertBattleSideBranch(
        ILProcessor il,
        Instruction normalTarget,
        Instruction skinTarget,
        Refs refs)
    {
        il.InsertBefore(normalTarget, il.Create(OpCodes.Beq_S, skinTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Call, refs.BattlePlayerUnitSide));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Callvirt, refs.BattleRoleGetSide));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldarg_0));
    }

    private static void InsertBattleIndexSameSideBranch(
        ILProcessor il,
        Instruction enemyTarget,
        Instruction insertBefore,
        Refs refs)
    {
        var sameSide = il.Create(OpCodes.Nop);
        il.InsertBefore(insertBefore, sameSide);
        il.InsertBefore(insertBefore, il.Create(OpCodes.Br_S, enemyTarget));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Beq_S, sameSide));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Call, refs.BattleSelfRoleIndex));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Div));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Div));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Callvirt, refs.BattleCharIndex));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldfld, refs.BattleRoleDataChar));
        il.InsertBefore(insertBefore, il.Create(OpCodes.Ldarg_0));
    }

    private static void InsertMapGroupBranch(
        ILProcessor il,
        Instruction normalTarget,
        Instruction skinTarget,
        Refs refs,
        int characterDataArg)
    {
        var arg = ArgOp(characterDataArg);
        il.InsertBefore(normalTarget, il.Create(OpCodes.Br_S, skinTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Beq_S, skinTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.PlayerDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Call, refs.PlayerDataHolderPlayerData));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(arg));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Brfalse_S, normalTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Call, refs.CheckGroupMember));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(arg));
    }

    private static void InsertMapGroupBranchFromEntity(
        ILProcessor il,
        Instruction normalTarget,
        Instruction skinTarget,
        Refs refs)
    {
        il.InsertBefore(normalTarget, il.Create(OpCodes.Br_S, skinTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Beq_S, skinTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.PlayerDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Call, refs.PlayerDataHolderPlayerData));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterEntityData));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Brfalse_S, normalTarget));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Call, refs.CheckGroupMember));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterDataObjIndex));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldfld, refs.CharacterEntityData));
        il.InsertBefore(normalTarget, il.Create(OpCodes.Ldarg_0));
    }

    private static Instruction? FindCrestLookupPoint(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            var isCrestKey = insn.OpCode == OpCodes.Callvirt && insn.Operand is MethodReference called
                && called.Name == "get_MaxCrestUseId";
            if (!isCrestKey && !(insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference field
                && field.Name == "maxCrestUseId"))
            {
                continue;
            }

            var idx = method.Body.Instructions.IndexOf(insn);
            for (var i = idx - 1; i >= Math.Max(0, idx - 4); i--)
            {
                if (method.Body.Instructions[i].OpCode == OpCodes.Ldarg_0)
                {
                    return method.Body.Instructions[i];
                }
            }
        }

        return null;
    }

    private static Instruction FindReturnZero(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 1; i++)
        {
            if (instructions[i].OpCode == OpCodes.Ldloc_0 && instructions[i + 1].OpCode == OpCodes.Ret)
            {
                return instructions[i];
            }
        }

        throw new InvalidOperationException("未找到 EffectID 返回 0 的路径: " + method.FullName);
    }

    private static bool IsPerfectPetCheck(Instruction insn)
    {
        if (insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference field)
        {
            return field.Name is "PerfectPet" or "Perfectpet";
        }

        return insn.OpCode == OpCodes.Callvirt && insn.Operand is MethodReference called
            && called.Name is "get_Perfectpet" or "get_isPrefectPet";
    }

    private static bool IsPerfectPetFieldLoad(Instruction insn)
    {
        return insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference field
            && field.Name is "PerfectPet" or "Perfectpet";
    }

    private static OpCode ArgOp(int index) => index switch
    {
        0 => OpCodes.Ldarg_0,
        1 => OpCodes.Ldarg_1,
        _ => OpCodes.Ldarg_2,
    };

    private static TypeDefinition RequireType(ModuleDefinition module, string name)
    {
        return module.Types.FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException("未找到类型: " + name);
    }

    private static MethodReference RequireGetter(TypeDefinition type, string propertyName)
    {
        var prop = type.Properties.FirstOrDefault(p => p.Name == propertyName)
            ?? throw new InvalidOperationException($"未找到属性: {type.Name}.{propertyName}");
        return prop.GetMethod ?? throw new InvalidOperationException("属性无 getter");
    }

    private static FieldReference RequireField(TypeDefinition type, string fieldName)
    {
        return type.Fields.FirstOrDefault(f => f.Name == fieldName)
            ?? throw new InvalidOperationException($"未找到字段: {type.Name}.{fieldName}");
    }

    private static MethodReference RequireMethod(TypeDefinition type, string name, int paramCount)
    {
        return type.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == paramCount)
            ?? throw new InvalidOperationException($"未找到方法: {type.Name}.{name}");
    }

    private static MethodReference RequireStaticMethod(TypeDefinition type, string name, int paramCount)
    {
        return type.Methods.FirstOrDefault(m => m.IsStatic && m.Name == name && m.Parameters.Count == paramCount)
            ?? throw new InvalidOperationException($"未找到方法: {type.Name}.{name}");
    }

}
