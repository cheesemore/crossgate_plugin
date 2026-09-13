using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// AI 战斗强制出手：在 AutoFight / DoVip 入口最前反射调用 SeqChapterTestUi 钩子。
/// true=已代发并 Ret；false=落到抓宠分发或原版自动。独立于 CatchBattleDispatchIl。
/// </summary>
internal static class SuperAiBattleDispatchIl
{
    public const string Marker = "SeqChapterSuperAiDispatch.v1";
    public const string TypeAssemblyName = "SeqChapterTestUi, SeqChapterTestUi";

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

    public static void EnsureDispatchHook(
        MethodDefinition method,
        ModuleDefinition module,
        string entryName,
        string label)
    {
        if (IsDispatchInstalled(method))
        {
            Console.WriteLine($"[SUPERAI-DISPATCH] {label} 已存在，跳过");
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

        var haveType = il.Create(OpCodes.Nop);
        var haveMethod = il.Create(OpCodes.Nop);
        var unboxLabel = il.Create(OpCodes.Nop);
        var fallthrough = il.Create(OpCodes.Nop);

        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, Marker),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Ldstr, TypeAssemblyName),
            il.Create(OpCodes.Call, getType),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveType),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            haveType,
            il.Create(OpCodes.Ldstr, entryName),
            il.Create(OpCodes.Callvirt, getMethod),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveMethod),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            haveMethod,
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Callvirt, invoke),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, unboxLabel),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            unboxLabel,
            il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean),
            il.Create(OpCodes.Brfalse, fallthrough),
            il.Create(OpCodes.Ret),
            fallthrough,
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine($"[SUPERAI-DISPATCH] 已注入 {entryName}（{label}）");
    }
}
