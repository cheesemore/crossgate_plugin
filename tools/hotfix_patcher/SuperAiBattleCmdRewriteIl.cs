using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// BattleManager.SendBattleCommond(string) 入口把指令交给助手改写目标。
/// 助手关着或认不出技能时原样返回，不代发。
/// </summary>
internal static class SuperAiBattleCmdRewriteIl
{
    public const string Marker = "SeqChapterSuperAiRetarget.v1";
    public const string EntryName = "RewriteSuperAiBattleCommand";
    public const string TypeAssemblyName = "SeqChapterTestUi, SeqChapterTestUi";

    public static void Ensure(AssemblyDefinition asm)
    {
        var mgr = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleManager");
        if (mgr == null)
        {
            Console.WriteLine("[SUPERAI-RETARGET] 警告：未找到 BattleManager");
            return;
        }

        var hooked = 0;
        foreach (var method in mgr.Methods)
        {
            if (!method.HasBody || method.Name != "SendBattleCommond" || method.Parameters.Count != 1)
            {
                continue;
            }

            if (method.Parameters[0].ParameterType.FullName != "System.String")
            {
                continue;
            }

            if (IsInstalled(method))
            {
                Console.WriteLine("[SUPERAI-RETARGET] 已存在，跳过");
                hooked++;
                continue;
            }

            Inject(method, asm.MainModule);
            hooked++;
        }

        if (hooked == 0)
        {
            Console.WriteLine("[SUPERAI-RETARGET] 警告：未找到 SendBattleCommond(string)");
        }
    }

    public static void EnsureOnFile(string hotfixPath)
    {
        var origBytes = File.ReadAllBytes(hotfixPath);
        var expectedSize = HotfixSize.Require(origBytes);
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        Ensure(asm);

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);
        MetadataValidator.EnsureReadable(padded, hotfixDir);
        File.WriteAllBytes(hotfixPath, padded);
        HotfixSize.EnsureUnchanged(File.ReadAllBytes(hotfixPath), expectedSize);
    }

    private static bool IsInstalled(MethodDefinition method)
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

    private static void Inject(MethodDefinition method, ModuleDefinition module)
    {
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
        var arg = method.Parameters[0];

        var haveType = il.Create(OpCodes.Nop);
        var haveMethod = il.Create(OpCodes.Nop);
        var haveResult = il.Create(OpCodes.Nop);
        var store = il.Create(OpCodes.Nop);
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
            il.Create(OpCodes.Ldstr, EntryName),
            il.Create(OpCodes.Callvirt, getMethod),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveMethod),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            haveMethod,
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Newarr, module.TypeSystem.Object),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Ldarg, arg),
            il.Create(OpCodes.Stelem_Ref),
            il.Create(OpCodes.Callvirt, invoke),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, haveResult),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            haveResult,
            il.Create(OpCodes.Isinst, module.TypeSystem.String),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Brtrue, store),
            il.Create(OpCodes.Pop),
            il.Create(OpCodes.Br, fallthrough),
            store,
            il.Create(OpCodes.Starg, arg),
            fallthrough,
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine("[SUPERAI-RETARGET] 已注入 SendBattleCommond");
    }
}
