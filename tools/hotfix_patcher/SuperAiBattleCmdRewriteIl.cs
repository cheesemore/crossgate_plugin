using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// BattleManager.SendBattleCommond(string) 入口把指令交给助手改写目标。
/// 助手关着或认不出技能时原样返回，不代发。
/// </summary>
internal static class SuperAiBattleCmdRewriteIl
{
    public const string Marker = "SeqChapterSuperAiRetarget.v2";
    public const string LegacyMarker = "SeqChapterSuperAiRetarget.v1";
    public const string HookTypeName = "SeqChapterSuperAiRetargetHook";
    public const string HookMethodName = "Apply";
    /// <summary>v1 注入块固定插在方法最前，共 37 条（含末尾 fallthrough nop）。</summary>
    private const int LegacyPrefixCount = 37;

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

            var apply = EnsureHookMethod(asm.MainModule);
            if (IsInstalled(method))
            {
                Console.WriteLine("[SUPERAI-RETARGET] v2 已存在，跳过");
                hooked++;
                continue;
            }

            StripLegacy(method);
            Inject(method, asm.MainModule, apply);
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

    private static void StripLegacy(MethodDefinition method)
    {
        var ins = method.Body.Instructions;
        if (ins.Count < LegacyPrefixCount)
        {
            return;
        }

        if (ins[0].OpCode != OpCodes.Ldstr || ins[0].Operand as string != LegacyMarker)
        {
            return;
        }

        if (ins[LegacyPrefixCount - 2].OpCode != OpCodes.Starg
            || ins[LegacyPrefixCount - 1].OpCode != OpCodes.Nop)
        {
            throw new InvalidOperationException("旧集火改写块形状不符，拒绝拆除");
        }

        var il = method.Body.GetILProcessor();
        for (var i = 0; i < LegacyPrefixCount; i++)
        {
            il.Remove(method.Body.Instructions[0]);
        }

        Console.WriteLine("[SUPERAI-RETARGET] 已拆除 v1 Type.GetType 挂钩");
    }

    private static void Inject(MethodDefinition method, ModuleDefinition module, MethodReference apply)
    {
        var body = method.Body;
        if (body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(method.Name + " 无指令");
        }

        var il = body.GetILProcessor();
        var continueAt = body.Instructions[0];
        var arg = method.Parameters[0];
        var exceptionType = module.ImportReference(
            ResolveCorlib(module).MainModule.Types.First(t => t.FullName == "System.Exception"));
        var tryStart = il.Create(OpCodes.Ldarg, arg);
        var catchPop = il.Create(OpCodes.Pop);
        var after = il.Create(OpCodes.Nop);

        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, Marker),
            il.Create(OpCodes.Pop),
            tryStart,
            il.Create(OpCodes.Call, apply),
            il.Create(OpCodes.Starg, arg),
            il.Create(OpCodes.Leave, after),
            catchPop,
            il.Create(OpCodes.Leave, after),
            after,
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(continueAt, block[i]);
        }

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = after,
        });

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine("[SUPERAI-RETARGET] 已注入 SendBattleCommond（扫描已加载助手）");
    }

    /// <summary>
    /// 在 hotfix 里放一个直接调用的查找器。
    /// <c>Type.GetType</c> 找不到 <c>Assembly.Load(byte[])</c> 进来的助手，而且可能打到另一份静态。
    /// </summary>
    private static MethodReference EnsureHookMethod(ModuleDefinition module)
    {
        var existing = module.Types.FirstOrDefault(t => t.Name == HookTypeName);
        MethodDefinition apply;
        if (existing == null)
        {
            existing = new TypeDefinition(
                "",
                HookTypeName,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
                | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(existing);
            apply = new MethodDefinition(
                HookMethodName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.String);
            apply.Parameters.Add(new ParameterDefinition("cmd", ParameterAttributes.None, module.TypeSystem.String));
            existing.Methods.Add(apply);
        }
        else
        {
            apply = existing.Methods.First(m => m.Name == HookMethodName && m.Parameters.Count == 1);
            apply.Body.Instructions.Clear();
            apply.Body.Variables.Clear();
            apply.Body.ExceptionHandlers.Clear();
        }

        EmitApply(apply, module);
        return apply;
    }

    private static void EmitApply(MethodDefinition apply, ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var appDomain = corlib.MainModule.Types.First(t => t.FullName == "System.AppDomain");
        var getCurrent = appDomain.Methods.First(m => m.Name == "get_CurrentDomain" && m.Parameters.Count == 0);
        var getAssemblies = appDomain.Methods.First(m => m.Name == "GetAssemblies" && m.Parameters.Count == 0);
        var asmType = corlib.MainModule.Types.First(t => t.FullName == "System.Reflection.Assembly");
        var getName = asmType.Methods.First(m => m.Name == "GetName" && m.Parameters.Count == 0);
        var asmGetType = asmType.Methods.First(m =>
            m.Name == "GetType" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        var asmNameType = corlib.MainModule.Types.First(t => t.FullName == "System.Reflection.AssemblyName");
        var getSimpleName = asmNameType.Methods.First(m => m.Name == "get_Name" && m.Parameters.Count == 0);
        var typeType = corlib.MainModule.Types.First(t => t.FullName == "System.Type");
        var typeGetMethod = typeType.Methods.First(m =>
            m.Name == "GetMethod" && !m.IsStatic && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        var methodBase = corlib.MainModule.Types.First(t => t.FullName == "System.Reflection.MethodBase");
        var invoke = methodBase.Methods.First(m =>
            m.Name == "Invoke" && m.Parameters.Count == 2);
        var stringType = corlib.MainModule.Types.First(t => t.FullName == "System.String");
        var equals = stringType.Methods.First(m =>
            m.Name == "op_Equality" && m.IsStatic && m.Parameters.Count == 2);
        var exceptionType = corlib.MainModule.Types.First(t => t.FullName == "System.Exception");

        var body = apply.Body;
        body.InitLocals = true;
        var asmsVar = new VariableDefinition(module.ImportReference(getAssemblies.ReturnType));
        var iVar = new VariableDefinition(module.TypeSystem.Int32);
        var asmVar = new VariableDefinition(module.ImportReference(asmType));
        var nameVar = new VariableDefinition(module.TypeSystem.String);
        var typeVar = new VariableDefinition(module.ImportReference(typeType));
        var liveVar = new VariableDefinition(module.ImportReference(typeGetMethod.ReturnType));
        var flagVar = new VariableDefinition(module.TypeSystem.Object);
        var rewriteVar = new VariableDefinition(module.ImportReference(typeGetMethod.ReturnType));
        var chosenVar = new VariableDefinition(module.TypeSystem.String);
        body.Variables.Add(asmsVar);
        body.Variables.Add(iVar);
        body.Variables.Add(asmVar);
        body.Variables.Add(nameVar);
        body.Variables.Add(typeVar);
        body.Variables.Add(liveVar);
        body.Variables.Add(flagVar);
        body.Variables.Add(rewriteVar);
        body.Variables.Add(chosenVar);

        var il = body.GetILProcessor();
        var loop = il.Create(OpCodes.Nop);
        var check = il.Create(OpCodes.Ldloc, iVar);
        var catchPop = il.Create(OpCodes.Pop);
        var end = il.Create(OpCodes.Ldloc, chosenVar);
        var retChosen = il.Create(OpCodes.Ldloc, chosenVar);

        var tryStart = il.Create(OpCodes.Ldnull);
        il.Append(tryStart);
        il.Append(il.Create(OpCodes.Stloc, chosenVar));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(getCurrent)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getAssemblies)));
        il.Append(il.Create(OpCodes.Stloc, asmsVar));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Stloc, iVar));
        il.Append(il.Create(OpCodes.Br, check));

        il.Append(loop);
        il.Append(il.Create(OpCodes.Ldloc, asmsVar));
        il.Append(il.Create(OpCodes.Ldloc, iVar));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Stloc, asmVar));
        il.Append(il.Create(OpCodes.Ldloc, iVar));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Add));
        il.Append(il.Create(OpCodes.Stloc, iVar));
        il.Append(il.Create(OpCodes.Ldloc, asmVar));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, asmVar));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getName)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getSimpleName)));
        il.Append(il.Create(OpCodes.Stloc, nameVar));
        il.Append(il.Create(OpCodes.Ldloc, nameVar));
        il.Append(il.Create(OpCodes.Ldstr, "SeqChapterTestUi"));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(equals)));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, asmVar));
        il.Append(il.Create(OpCodes.Ldstr, "SeqChapterTestUi"));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(asmGetType)));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Ldstr, "SuperAiCommandHookLive"));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(typeGetMethod)));
        il.Append(il.Create(OpCodes.Stloc, liveVar));
        il.Append(il.Create(OpCodes.Ldloc, liveVar));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, liveVar));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(invoke)));
        il.Append(il.Create(OpCodes.Stloc, flagVar));
        il.Append(il.Create(OpCodes.Ldloc, flagVar));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, flagVar));
        il.Append(il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Ldstr, "RewriteSuperAiBattleCommand"));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(typeGetMethod)));
        il.Append(il.Create(OpCodes.Stloc, rewriteVar));
        il.Append(il.Create(OpCodes.Ldloc, rewriteVar));
        il.Append(il.Create(OpCodes.Brfalse, check));
        il.Append(il.Create(OpCodes.Ldloc, rewriteVar));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(invoke)));
        il.Append(il.Create(OpCodes.Isinst, module.TypeSystem.String));
        il.Append(il.Create(OpCodes.Stloc, chosenVar));
        il.Append(il.Create(OpCodes.Leave, end));

        il.Append(check);
        il.Append(il.Create(OpCodes.Ldloc, asmsVar));
        il.Append(il.Create(OpCodes.Ldlen));
        il.Append(il.Create(OpCodes.Conv_I4));
        il.Append(il.Create(OpCodes.Blt, loop));
        il.Append(il.Create(OpCodes.Leave, end));

        il.Append(catchPop);
        il.Append(il.Create(OpCodes.Leave, end));

        il.Append(end);
        il.Append(il.Create(OpCodes.Brtrue, retChosen));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(retChosen);
        il.Append(il.Create(OpCodes.Ret));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(exceptionType),
            TryStart = tryStart,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = end,
        });

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = 8;
    }

    private static AssemblyDefinition ResolveCorlib(ModuleDefinition module)
    {
        var name = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib")
            ?? module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        return module.AssemblyResolver.Resolve(name);
    }
}
