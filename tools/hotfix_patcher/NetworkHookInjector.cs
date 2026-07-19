using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class NetworkHookInjector
{
    private const string SnifferTypeName = "CrossgateMod.ModNetworkSniffer";

    public static void Inject(AssemblyDefinition assembly)
    {
        var module = assembly.MainModule;
        var sniffer = module.Types.FirstOrDefault(t => t.FullName == SnifferTypeName)
            ?? throw new InvalidOperationException("未找到 " + SnifferTypeName);

        var recordReceive = sniffer.Methods.FirstOrDefault(m => m.Name == "RecordReceive" && m.IsStatic && m.HasBody)
            ?? throw new InvalidOperationException("未找到 RecordReceive");
        var recordSend = sniffer.Methods.FirstOrDefault(m => m.Name == "RecordSend" && m.IsStatic && m.HasBody)
            ?? throw new InvalidOperationException("未找到 RecordSend");

        var netManager = module.Types.FirstOrDefault(t => t.Name == "NetManager")
            ?? throw new InvalidOperationException("未找到 NetManager");

        var receiveMsg = netManager.Methods.FirstOrDefault(m => m.Name == "receiveMsg" && m.IsPrivate && m.HasBody)
            ?? throw new InvalidOperationException("未找到 NetManager.receiveMsg");
        var sendMessage = netManager.Methods.FirstOrDefault(m => m.Name == "SendMessage" && m.HasBody && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException("未找到 NetManager.SendMessage");

        InjectReceiveHook(receiveMsg, recordReceive);
        InjectSendHook(sendMessage, recordSend);
    }

    private static void InjectReceiveHook(MethodDefinition method, MethodReference hook)
    {
        if (MethodCalls(method, hook))
        {
            Console.WriteLine("[SKIP] receiveMsg 已挂钩");
            return;
        }

        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Conv_I4));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        Console.WriteLine("[HOOK] NetManager.receiveMsg -> ModNetworkSniffer.RecordReceive");
    }

    private static void InjectSendHook(MethodDefinition method, MethodReference hook)
    {
        if (MethodCalls(method, hook))
        {
            Console.WriteLine("[SKIP] SendMessage 已挂钩");
            return;
        }

        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        Console.WriteLine("[HOOK] NetManager.SendMessage -> ModNetworkSniffer.RecordSend");
    }

    private static bool MethodCalls(MethodDefinition method, MethodReference target)
    {
        if (!method.HasBody)
        {
            return false;
        }

        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Call && insn.Operand is MethodReference called
                && called.FullName == target.FullName)
            {
                return true;
            }
        }

        return false;
    }
}
