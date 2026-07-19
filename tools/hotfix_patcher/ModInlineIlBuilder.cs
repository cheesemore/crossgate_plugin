using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ModInlineIlBuilder
{
    private const float TickIntervalSeconds = 10f;

    internal const string InitLogMessage = "[CrossgateMod] Init OK";
    internal const string TickLogMessage = "[CrossgateMod] tick";

    public static byte[] BuildTickBody(MethodDefinition method, ModuleDefinition module, UserStringHeap userStrings)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = false;

        var il = method.Body.GetILProcessor();
        var debugLog = ImportDebugLog(module);

        il.Append(il.Create(OpCodes.Ldstr, TickLogMessage));
        il.Append(il.Create(OpCodes.Call, debugLog));
        il.Append(il.Create(OpCodes.Ret));

        IlSerializer.RecalculateOffsets(method.Body);
        method.Body.MaxStackSize = 8;
        return IlSerializer.Serialize(method.Body, userStrings);
    }

    public static byte[] BuildStartLogBody(
        MethodBody originalStart,
        FieldReference isMsgLog,
        UserStringHeap userStrings,
        ModuleDefinition module,
        bool enableTick,
        MethodReference? tickMethod)
    {
        var injectBefore = originalStart.Instructions.First(i =>
            i.OpCode == OpCodes.Callvirt
            && i.Operand is MethodReference called
            && called.Name == "Dispatch")
            ?? throw new InvalidOperationException("未找到 ScreenTransiton.Dispatch");

        var il = originalStart.GetILProcessor();
        var debugLog = ImportDebugLog(module);

        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Stsfld, isMsgLog));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldstr, InitLogMessage));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Call, debugLog));

        if (enableTick)
        {
            if (tickMethod == null)
            {
                throw new InvalidOperationException("enableTick 需要 tickMethod");
            }

            var timerCreate = ImportTimerCreate(module);
            var actionCtor = ImportActionCtor(module);

            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldnull));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldftn, tickMethod));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Newobj, actionCtor));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_R4, TickIntervalSeconds));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4, -1));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_R4, 1f));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Call, timerCreate));
            il.InsertBefore(injectBefore, il.Create(OpCodes.Pop));
        }

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
        return IlSerializer.Serialize(originalStart, userStrings);
    }

    private static MethodReference ImportDebugLog(ModuleDefinition module)
        => ImportCall(module, "Debug", "Log", 1)
            ?? throw new InvalidOperationException("未找到 UnityEngine.Debug.Log 引用");

    private static MethodReference ImportTimerCreate(ModuleDefinition module)
        => ImportCall(module, "Timer", "Create", 5)
            ?? throw new InvalidOperationException("未找到 Timer.Create");

    private static MethodReference ImportActionCtor(ModuleDefinition module)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode != OpCodes.Newobj || insn.Operand is not MethodReference ctor)
                {
                    continue;
                }

                if (ctor.DeclaringType.Name == "Action"
                    && ctor.Name == ".ctor"
                    && ctor.Parameters.Count == 2)
                {
                    return module.ImportReference(ctor);
                }
            }
        }

        throw new InvalidOperationException("未找到 System.Action::.ctor");
    }

    private static MethodReference? ImportCall(
        ModuleDefinition module,
        string typeName,
        string methodName,
        int paramCount,
        string? firstParamType = null,
        bool instance = false)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
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

        return null;
    }
}
