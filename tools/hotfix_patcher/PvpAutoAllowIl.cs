using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// PVP 自动战斗放行：在 NotifyManager.Tip(string,bool) 入口拦截
/// 「PVP下不允许自动战斗哟」，若 SeqChapterTestUi.AllowPvpAutoBattle 开则改走
/// TryHandlePvpAutoBlockedTip（切换 IsAutoBattle）并吞掉原提示。
/// 回合强制关自动由 TestUi TickPvpAutoHold 补回，不必再钩 state machine。
/// </summary>
internal static class PvpAutoAllowIl
{
    public const string Marker = "SeqChapterPvpAutoAllow.v1";
    public const string TypeAssemblyName = "SeqChapterTestUi, SeqChapterTestUi";
    public const string EntryName = "TryHandlePvpAutoBlockedTip";
    public const string BlockedTip = "PVP下不允许自动战斗哟";

    public static bool IsInstalled(MethodDefinition method)
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

    public static void EnsureTipHook(AssemblyDefinition asm)
    {
        var notify = asm.MainModule.Types.FirstOrDefault(t => t.Name == "NotifyManager");
        if (notify == null)
        {
            Console.WriteLine("[PVP-AUTO] 警告：未找到 NotifyManager");
            return;
        }

        var tip = notify.Methods.FirstOrDefault(m =>
            m.Name == "Tip"
            && m.HasBody
            && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.FullName == "System.String"
            && m.Parameters[1].ParameterType.FullName == "System.Boolean");
        if (tip == null)
        {
            Console.WriteLine("[PVP-AUTO] 警告：未找到 Tip(string,bool)");
            return;
        }

        if (IsInstalled(tip))
        {
            Console.WriteLine("[PVP-AUTO] Tip 钩已存在，跳过");
            return;
        }

        InjectTipHook(tip, asm.MainModule);
    }

    private static void InjectTipHook(MethodDefinition tip, ModuleDefinition module)
    {
        var body = tip.Body;
        if (body.Instructions.Count == 0)
        {
            throw new InvalidOperationException("Tip 无指令");
        }

        var il = body.GetILProcessor();
        var getType = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);
        var stringEq = FindStringOpEquality(module);
        var continueAt = body.Instructions[0];

        var notBlocked = il.Create(OpCodes.Nop);
        var haveType = il.Create(OpCodes.Nop);
        var haveMethod = il.Create(OpCodes.Nop);
        var unboxLabel = il.Create(OpCodes.Nop);
        var fallthrough = il.Create(OpCodes.Nop);

        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, Marker),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Ldarg_1),
            il.Create(OpCodes.Ldstr, BlockedTip),
            il.Create(OpCodes.Call, stringEq),
            il.Create(OpCodes.Brfalse, notBlocked),
            il.Create(OpCodes.Ldstr, TypeAssemblyName),
            il.Create(OpCodes.Call, getType),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveType),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            haveType,
            il.Create(OpCodes.Ldstr, EntryName),
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
            notBlocked,
            fallthrough,
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine("[PVP-AUTO] 已注入 NotifyManager.Tip → TryHandlePvpAutoBlockedTip");
    }

    private static MethodReference FindStringOpEquality(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            foreach (var m in type.Methods.Where(x => x.HasBody))
            {
                foreach (var insn in m.Body.Instructions)
                {
                    if (insn.Operand is MethodReference mr
                        && mr.Name == "op_Equality"
                        && mr.DeclaringType != null
                        && mr.DeclaringType.Name == "String"
                        && mr.Parameters.Count == 2)
                    {
                        return module.ImportReference(mr);
                    }
                }
            }
        }

        var stringType = new TypeReference("System", "String", module, module.TypeSystem.CoreLibrary);
        var op = new MethodReference("op_Equality", module.TypeSystem.Boolean, stringType)
        {
            HasThis = false,
        };
        op.Parameters.Add(new ParameterDefinition(stringType));
        op.Parameters.Add(new ParameterDefinition(stringType));
        return module.ImportReference(op);
    }
}
