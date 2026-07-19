using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>在人物自动行动方法入口内联封印逻辑（二进制补丁，不新增类型/局部变量）。</summary>
internal static class AutoSealIlBuilder
{
    private const int SealFlagMask = 0x100;

    public static void PrependSealHook(MethodDefinition method, ModuleDefinition module)
    {
        var body = method.Body;
        if (body.Variables.Count < 6)
        {
            throw new InvalidOperationException(
                $"DoVipPlayerAutoFight 需要至少 6 个局部变量，实际 {body.Variables.Count}");
        }

        // 复用原版局部槽（不追加 VariableDefinition，保持 LocalVarToken 有效）：
        // V0 bool flag → 临时 int（rangeStart）；失败分支后原版会 ldc.i4.0 stloc.0
        // V1 int i     → itemIndex / enemyIndex
        // V2 object    → List<ItemData> / ItemData
        // V3 object    → Proto_ItemData
        // V4 BattleRole& → int placeToIndex key
        // V5 Proto_TechData → string 指令
        VariableDefinition V(int index) => body.Variables[index];

        var original = body.Instructions.ToList();
        body.Instructions.Clear();

        var il = body.GetILProcessor();
        var debugLog = ImportDebugLog(module);

        var battleData = Req(module, "BattleDataHolder");
        var battleMgr = Req(module, "BattleManager");
        var playerHolder = Req(module, "PlayerDataHolder");
        var itemMgrType = Req(module, "ItemManager");
        var itemDataType = Req(module, "ItemData");
        var protoItemType = Req(module, "Proto_ItemData");
        var battleRoleType = Req(module, "BattleRole");
        var roleContainer = Req(module, "BattleRoleContainer");

        var getBattlePlayerIndex = Prop(battleData, "battlePlayerIndex");
        var getIsInBattle = Prop(battleData, "IsInBattle").GetMethod
            ?? throw new InvalidOperationException("IsInBattle getter");
        var getCurrentAccount = Field(battleData, "CurrentAccount");
        var getMainPlayerUid = Field(playerHolder, "MainPlayerUid");
        var stringEquals = FindStringEquals(module);
        var stringIsNullOrEmpty = FindStringIsNullOrEmpty(module);
        var setSkillUsed = Prop(battleData, "skillUsed").SetMethod
            ?? throw new InvalidOperationException("skillUsed setter");
        var getIsAutoBattle = Field(battleMgr, "IsAutoBattle");
        var getPlayerActionMagics = Field(battleMgr, "PlayerActionMagics");
        var sendCommand = Meth(battleMgr, "SendBattleCommond", 1);
        var getItems = Meth(playerHolder, "GetItemDatasFromUid", 1);
        var canUse = Meth(itemMgrType, "CanUseInBattle", 2);
        var getItemDataField = Field(itemDataType, "data");
        var getUseFlagMethod = Prop(itemDataType, "useFlag").GetMethod
            ?? throw new InvalidOperationException("ItemData.useFlag getter");
        var getProtoTypeGet = Prop(protoItemType, "Type").GetMethod
            ?? throw new InvalidOperationException("Proto_ItemData.Type getter");
        var getProtoIndexGet = Prop(protoItemType, "Index").GetMethod
            ?? throw new InvalidOperationException("Proto_ItemData.Index getter");
        var getProtoFlgGet = Prop(protoItemType, "Flg").GetMethod
            ?? throw new InvalidOperationException("Proto_ItemData.Flg getter");
        var placeToIndex = Field(battleRoleType, "placeToIndex");
        var roleDic = Field(roleContainer, "BattleRoleDic");
        var getIsDeadMethod = Prop(battleRoleType, "IsDead").GetMethod
            ?? throw new InvalidOperationException("BattleRole.IsDead getter");

        var bmInst = FindManagerInstance(module, "BattleManager");
        var itemInst = FindManagerInstance(module, "ItemManager");

        var listType = getItems.ReturnType.Resolve()!;
        var getListItem = listType.Methods.First(m => m.Name == "get_Item");
        var getListCount = listType.Methods.First(m => m.Name == "get_Count");

        var dictType = roleDic.FieldType.Resolve()!;
        var containsKey = dictType.Methods.First(m => m.Name == "ContainsKey");
        var getDictItem = dictType.Methods.First(m => m.Name == "get_Item");
        var pamDictType = getPlayerActionMagics.FieldType.Resolve()!;
        var setPamItem = pamDictType.Methods.First(m => m.Name == "set_Item");

        var intToHex = module.ImportReference(
            typeof(int).GetMethod(nameof(int.ToString), new[] { typeof(string) })!);
        var concat = module.ImportReference(
            typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!);

        var continueAt = il.Create(OpCodes.Nop);
        var failHandlers = new List<(Instruction Label, string? Message)>();

        void EmitLog(string message)
        {
            il.Append(il.Create(OpCodes.Ldstr, message));
            il.Append(il.Create(OpCodes.Call, debugLog));
        }

        Instruction ReserveFail(string? message)
        {
            var label = il.Create(OpCodes.Nop);
            failHandlers.Add((label, message));
            return label;
        }

        var failNotInBattle = ReserveFail("[AutoSeal] 跳过：不在战斗中");
        var failEmptyAccount = ReserveFail("[AutoSeal] 跳过：CurrentAccount 为空");
        var failNotMain = ReserveFail("[AutoSeal] 跳过：非账号1(MainPlayerUid)");
        var failNotAuto = ReserveFail("[AutoSeal] 跳过：未开启自动战斗");
        var failNoBag = ReserveFail("[AutoSeal] 无法封印：背包数据为空(CurrentAccount)");
        var noSealCard = ReserveFail("[AutoSeal] 无法封印：背包无可用封印卡（Type4-6/IS_SEAL 且 CanUseInBattle）");

        il.Append(il.Create(OpCodes.Call, module.ImportReference(getIsInBattle)));
        il.Append(il.Create(OpCodes.Brfalse, failNotInBattle));

        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(getCurrentAccount)));
        il.Append(il.Create(OpCodes.Call, stringIsNullOrEmpty));
        il.Append(il.Create(OpCodes.Brtrue, failEmptyAccount));

        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(getCurrentAccount)));
        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(getMainPlayerUid)));
        il.Append(il.Create(OpCodes.Call, stringEquals));
        il.Append(il.Create(OpCodes.Brfalse, failNotMain));

        il.Append(il.Create(OpCodes.Call, bmInst));
        il.Append(il.Create(OpCodes.Ldfld, module.ImportReference(getIsAutoBattle)));
        il.Append(il.Create(OpCodes.Brfalse, failNotAuto));

        EmitLog("[AutoSeal] 检测通过，扫描背包封印卡…");

        il.Append(il.Create(OpCodes.Call, module.ImportReference(getBattlePlayerIndex.GetMethod)));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        var onEnemySide = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Bge, onEnemySide));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        il.Append(il.Create(OpCodes.Stloc, V(0)));
        var rangeDone = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Br, rangeDone));
        il.Append(onEnemySide);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, V(0)));
        il.Append(rangeDone);

        il.Append(il.Create(OpCodes.Ldarg, method.Parameters[0]));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(getItems)));
        il.Append(il.Create(OpCodes.Dup));
        var storeList = il.Create(OpCodes.Stloc, V(2));
        il.Append(il.Create(OpCodes.Brtrue, storeList));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Br, failNoBag));
        il.Append(storeList);
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, V(1)));

        var itemLoop = il.Create(OpCodes.Nop);
        il.Append(itemLoop);
        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Ldloc, V(2)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getListCount)));
        il.Append(il.Create(OpCodes.Bge, noSealCard));

        il.Append(il.Create(OpCodes.Ldloc, V(2)));
        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getListItem)));
        il.Append(il.Create(OpCodes.Stloc, V(2)));
        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, V(1)));

        il.Append(il.Create(OpCodes.Ldloc, V(2)));
        il.Append(il.Create(OpCodes.Brfalse, itemLoop));
        il.Append(il.Create(OpCodes.Ldloc, V(2)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getUseFlagMethod)));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Bne_Un, itemLoop));

        il.Append(il.Create(OpCodes.Ldloc, V(2)));
        il.Append(il.Create(OpCodes.Ldfld, module.ImportReference(getItemDataField)));
        il.Append(il.Create(OpCodes.Stloc, V(3)));
        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Brfalse, itemLoop));

        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getProtoTypeGet)));
        var checkFlg = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Blt, checkFlg));
        il.Append(il.Create(OpCodes.Ldc_I4_6));
        il.Append(il.Create(OpCodes.Bgt, checkFlg));
        var sealTypeOk = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Br, sealTypeOk));
        il.Append(checkFlg);
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getProtoFlgGet)));
        il.Append(il.Create(OpCodes.Ldc_I4, SealFlagMask));
        il.Append(il.Create(OpCodes.And));
        il.Append(il.Create(OpCodes.Brfalse, itemLoop));
        il.Append(sealTypeOk);
        il.Append(il.Create(OpCodes.Pop));

        il.Append(il.Create(OpCodes.Call, itemInst));
        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Ldarg, method.Parameters[0]));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(canUse)));
        il.Append(il.Create(OpCodes.Brfalse, itemLoop));

        il.Append(il.Create(OpCodes.Ldloc, V(0)));
        il.Append(il.Create(OpCodes.Stloc, V(1)));

        var enemyLoop = il.Create(OpCodes.Nop);
        il.Append(enemyLoop);

        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(getBattlePlayerIndex.GetMethod)));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        var enemyRangeHigh = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Bge, enemyRangeHigh));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)10));
        var enemyRangeDone = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Br, enemyRangeDone));
        il.Append(enemyRangeHigh);
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)20));
        il.Append(enemyRangeDone);
        il.Append(il.Create(OpCodes.Bge, itemLoop));

        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(placeToIndex)));
        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Ldelem_I4));
        il.Append(il.Create(OpCodes.Stloc, V(4)));

        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(roleDic)));
        il.Append(il.Create(OpCodes.Ldloc, V(4)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(containsKey)));
        var nextEnemy = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Brfalse, nextEnemy));

        il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(roleDic)));
        il.Append(il.Create(OpCodes.Ldloc, V(4)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getDictItem)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getIsDeadMethod)));
        il.Append(il.Create(OpCodes.Brtrue, nextEnemy));

        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getProtoTypeGet)));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)23));
        var qPath = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Blt, qPath));

        il.Append(il.Create(OpCodes.Ldstr, "I|"));
        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getProtoIndexGet)));
        il.Append(il.Create(OpCodes.Ldstr, "X"));
        il.Append(il.Create(OpCodes.Call, intToHex));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Stloc, V(5)));
        il.Append(il.Create(OpCodes.Ldstr, "|"));
        il.Append(il.Create(OpCodes.Ldloc, V(4)));
        il.Append(il.Create(OpCodes.Ldstr, "X"));
        il.Append(il.Create(OpCodes.Call, intToHex));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Ldloc, V(5)));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Stloc, V(5)));
        var cmdReady = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Br, cmdReady));

        il.Append(qPath);
        il.Append(il.Create(OpCodes.Ldloc, V(3)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getProtoIndexGet)));
        var useQff = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_8));
        il.Append(il.Create(OpCodes.Blt, useQff));
        il.Append(il.Create(OpCodes.Ldstr, "X"));
        il.Append(il.Create(OpCodes.Call, intToHex));
        il.Append(il.Create(OpCodes.Ldstr, "Q|"));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Stloc, V(5)));
        il.Append(il.Create(OpCodes.Br, cmdReady));
        il.Append(useQff);
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldstr, "Q|FF"));
        il.Append(il.Create(OpCodes.Stloc, V(5)));
        il.Append(cmdReady);

        il.Append(il.Create(OpCodes.Ldstr, "[AutoSeal] 已发送 "));
        il.Append(il.Create(OpCodes.Ldloc, V(5)));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Call, debugLog));

        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(setSkillUsed)));

        il.Append(il.Create(OpCodes.Call, bmInst));
        il.Append(il.Create(OpCodes.Ldfld, module.ImportReference(getPlayerActionMagics)));
        il.Append(il.Create(OpCodes.Ldarg, method.Parameters[0]));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(setPamItem)));

        il.Append(il.Create(OpCodes.Call, bmInst));
        il.Append(il.Create(OpCodes.Ldloc, V(5)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(sendCommand)));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(nextEnemy);
        il.Append(il.Create(OpCodes.Ldloc, V(1)));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, V(1)));
        il.Append(il.Create(OpCodes.Br, enemyLoop));

        foreach (var (label, message) in failHandlers)
        {
            il.Append(label);
            if (message != null)
            {
                EmitLog(message);
            }

            il.Append(il.Create(OpCodes.Br, continueAt));
        }

        il.Append(continueAt);
        foreach (var insn in original)
        {
            il.Append(insn);
        }
    }

    private static MethodReference FindStringEquals(ModuleDefinition module)
        => ImportCall(module, "String", "op_Equality", 2)
            ?? throw new InvalidOperationException("未找到 String.op_Equality 引用");

    private static MethodReference FindStringIsNullOrEmpty(ModuleDefinition module)
        => ImportCall(module, "String", "IsNullOrEmpty", 1)
            ?? throw new InvalidOperationException("未找到 String.IsNullOrEmpty 引用");

    private static MethodReference ImportDebugLog(ModuleDefinition module)
        => ImportCall(module, "Debug", "Log", 1)
            ?? throw new InvalidOperationException("未找到 UnityEngine.Debug.Log 引用");

    private static MethodReference? ImportCall(
        ModuleDefinition module,
        string typeName,
        string methodName,
        int paramCount,
        string? firstParamType = null,
        bool instance = false)
    {
        foreach (var method in module.Types.SelectMany(CecilHelpers.NestedTypes).Concat(module.Types))
        {
            foreach (var m in method.Methods.Where(x => x.HasBody))
            {
                foreach (var insn in m.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Call && insn.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    if (insn.Operand is not MethodReference called
                        || called.Name != methodName
                        || called.Parameters.Count != paramCount
                        || called.DeclaringType.Name != typeName
                        || called.HasThis != instance)
                    {
                        continue;
                    }

                    if (firstParamType != null
                        && called.Parameters[0].ParameterType.FullName != firstParamType)
                    {
                        continue;
                    }

                    return module.ImportReference(called);
                }
            }
        }

        return null;
    }

    private static MethodReference FindManagerInstance(ModuleDefinition module, string managerName)
    {
        foreach (var type in module.Types.SelectMany(CecilHelpers.NestedTypes))
        {
            foreach (var m in type.Methods.Where(x => x.HasBody))
            {
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (called.Name != "get_Instance")
                    {
                        continue;
                    }

                    if (called.DeclaringType is not GenericInstanceType git
                        || git.GenericArguments.Count != 1)
                    {
                        continue;
                    }

                    var arg = git.GenericArguments[0];
                    var argName = arg.Resolve()?.Name ?? arg.Name;
                    if (argName == managerName)
                    {
                        return called;
                    }
                }
            }
        }

        throw new InvalidOperationException("未找到 Manager<" + managerName + ">.Instance 调用样例");
    }

    private static TypeDefinition Req(ModuleDefinition module, string name)
    {
        return module.Types.FirstOrDefault(t => t.Name == name)
            ?? module.Types.SelectMany(CecilHelpers.NestedTypes).FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException("未找到类型: " + name);
    }

    private static MethodDefinition Meth(TypeDefinition type, string name, int n)
    {
        return type.Methods.First(m => m.Name == name && m.Parameters.Count == n);
    }

    private static PropertyDefinition Prop(TypeDefinition type, string name)
    {
        return type.Properties.First(p => p.Name == name);
    }

    private static FieldDefinition Field(TypeDefinition type, string name)
    {
        return type.Fields.First(f => f.Name == name);
    }
}
