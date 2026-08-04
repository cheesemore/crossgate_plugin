using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 战斗钩多类型分发：卖银 → 无宠抓 → 普通抓。各自 PipelineEnabled=false 时返回 false，落到下一候选或原版自动。
/// </summary>
internal static class CatchBattleDispatchIl
{
    public const string Marker = "SeqChapterCatchDispatch.v1";

    public static readonly string[] TypeAssemblyNames =
    {
        "SeqChapterAutoCatchSell, SeqChapterAutoCatchSell",
        "SeqChapterAutoCatchNoPet, SeqChapterAutoCatchNoPet",
        "SeqChapterAutoCatch, SeqChapterAutoCatch",
    };

    public static bool IsDispatchInstalled(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s && s == Marker)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 若已有旧单类型钩或未钩，则在方法入口安装分发（已是分发则跳过）。
    /// 旧钩无法安全拆除时：在其前再插一层分发；旧钩仍会执行，但卖银/抓宠已 Enable 时会先命中。
    /// </summary>
    public static void EnsureDispatchHook(
        MethodDefinition method,
        ModuleDefinition module,
        string entryName,
        string label)
    {
        if (IsDispatchInstalled(method))
        {
            Console.WriteLine($"[CATCH-DISPATCH] {label} 分发已存在，跳过");
            return;
        }

        var body = method.Body;
        if (body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(method.Name + " 无指令");
        }

        var il = body.GetILProcessor();
        var getType = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);
        var continueAt = body.Instructions[0];

        var block = new List<Instruction>
        {
            // 标记串：便于检测；立即 Pop
            il.Create(OpCodes.Ldstr, Marker),
            il.Create(OpCodes.Pop),
        };

        foreach (var typeAsm in TypeAssemblyNames)
        {
            var haveType = il.Create(OpCodes.Nop);
            var haveMethod = il.Create(OpCodes.Nop);
            var unboxLabel = il.Create(OpCodes.Nop);
            var nextType = il.Create(OpCodes.Nop);

            block.Add(il.Create(OpCodes.Ldstr, typeAsm));
            block.Add(il.Create(OpCodes.Call, getType));
            block.Add(il.Create(OpCodes.Dup));
            block.Add(il.Create(OpCodes.Brtrue, haveType));
            block.Add(il.Create(OpCodes.Pop));
            block.Add(il.Create(OpCodes.Br, nextType));
            block.Add(haveType);
            block.Add(il.Create(OpCodes.Ldstr, entryName));
            block.Add(il.Create(OpCodes.Callvirt, getMethod));
            block.Add(il.Create(OpCodes.Dup));
            block.Add(il.Create(OpCodes.Brtrue, haveMethod));
            block.Add(il.Create(OpCodes.Pop));
            block.Add(il.Create(OpCodes.Br, nextType));
            block.Add(haveMethod);
            block.Add(il.Create(OpCodes.Ldnull));
            block.Add(il.Create(OpCodes.Ldnull));
            block.Add(il.Create(OpCodes.Callvirt, invoke));
            block.Add(il.Create(OpCodes.Dup));
            block.Add(il.Create(OpCodes.Brtrue, unboxLabel));
            block.Add(il.Create(OpCodes.Pop));
            block.Add(il.Create(OpCodes.Br, nextType));
            block.Add(unboxLabel);
            block.Add(il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean));
            block.Add(il.Create(OpCodes.Brfalse, nextType));
            block.Add(il.Create(OpCodes.Ret));
            block.Add(nextType);
        }

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine($"[CATCH-DISPATCH] 已注入分发 {entryName}（{label}）");
    }
}
