using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class BridgeLoaderIlBuilder
{
    internal const string BridgeDllAssetPath = "hotfixdata/SeqChapterHelperBridge.dll.bytes";
    internal const string BridgeTypeName = "SeqChapterHelperBridge";
    internal const string BridgeBootstrapName = "Bootstrap";
    internal const string BridgeTempDllSuffix = "/seqchapter_bridge.dll";
    internal const uint FileUtilTempPathFieldToken = 0x0A00032B;
    internal const float BootstrapDelaySeconds = 3f;

    /// <summary>桥接类型已嵌入 hotfix 时，直接 call Bootstrap（无 Assembly.Load）。</summary>
    public static void BuildDirectBootstrapBody(MethodDefinition method, ModuleDefinition module)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = false;

        var bridgeType = module.Types.FirstOrDefault(t => t.Name == BridgeTypeName)
            ?? throw new InvalidOperationException($"hotfix 中未找到嵌入类型 {BridgeTypeName}");
        var bootstrap = bridgeType.Methods.FirstOrDefault(m =>
            m.Name == BridgeBootstrapName && m.IsStatic && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException($"{BridgeTypeName}.{BridgeBootstrapName}() 未找到");

        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Call, bootstrap));
        il.Append(il.Create(OpCodes.Ret));

        IlSerializer.RecalculateOffsets(method.Body);
        method.Body.MaxStackSize = 8;
    }

    public static void BuildLoaderBodyInPlace(
        MethodDefinition method,
        ModuleDefinition module,
        UserStringHeap userStrings,
        bool skipIfTypeLoaded = false,
        string? dllAssetPath = null,
        string? typeName = null,
        string? bootstrapName = null)
    {
        BuildLoaderBody(
            method,
            module,
            userStrings,
            skipIfTypeLoaded,
            dllAssetPath,
            typeName,
            bootstrapName);
    }

    /// <summary>OnApplicationQuit 仅 ldc.i4.0 + call Pause，供 AddTimeInvoke 无参回调。</summary>
    public static void BuildQuitTriggersPauseBody(
        MethodDefinition quitMethod,
        MethodDefinition pauseMethod,
        ModuleDefinition module)
    {
        quitMethod.Body.Instructions.Clear();
        quitMethod.Body.Variables.Clear();
        quitMethod.Body.ExceptionHandlers.Clear();
        quitMethod.Body.InitLocals = false;

        var pauseRef = module.ImportReference(pauseMethod);
        var il = quitMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Call, pauseRef));
        il.Append(il.Create(OpCodes.Ret));

        IlSerializer.RecalculateOffsets(quitMethod.Body);
        quitMethod.Body.MaxStackSize = 8;
    }

    public static byte[] BuildLoaderBody(
        MethodDefinition method,
        ModuleDefinition module,
        UserStringHeap userStrings,
        bool skipIfTypeLoaded = false,
        string? dllAssetPath = null,
        string? typeName = null,
        string? bootstrapName = null)
    {
        dllAssetPath ??= BridgeDllAssetPath;
        typeName ??= BridgeTypeName;
        bootstrapName ??= BridgeBootstrapName;

        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = false;

        var body = method.Body;
        var il = body.GetILProcessor();
        var loadBytes = ImportFileUtilLoadBytes(module);
        var assemblyLoad = ImportAssemblyLoad(module);
        var getType = ImportAssemblyGetType(module);
        var getMethod = ImportTypeGetMethod(module);
        var invoke = ImportMethodInvoke(module);

        Instruction? skipRet = null;
        if (skipIfTypeLoaded)
        {
            var getTypeStatic = ImportTypeGetTypeStatic(module);
            skipRet = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldstr, typeName + ", " + typeName));
            il.Append(il.Create(OpCodes.Call, getTypeStatic));
            il.Append(il.Create(OpCodes.Brtrue_S, skipRet));
        }

        il.Append(il.Create(OpCodes.Ldstr, dllAssetPath));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        var failRet = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse_S, failRet));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldstr, typeName));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse_S, failRet));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldstr, bootstrapName));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(failRet);
        if (skipRet != null)
        {
            il.Append(skipRet);
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = 16;
        return IlSerializer.Serialize(body, userStrings);
    }

    /// <summary>LoadBytes → TempPath 落盘 → LoadFrom → Bootstrap（HybridCLR 下禁止 Assembly.Load 字节，会 TypeLoadException）。</summary>
    public static void BuildLoaderBodyLoadFrom(
        MethodDefinition method,
        ModuleDefinition module,
        UserStringHeap userStrings,
        bool skipIfTypeLoaded = false)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = true;

        var body = method.Body;
        var bytesVar = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        var pathVar = new VariableDefinition(module.TypeSystem.String);
        body.Variables.Add(bytesVar);
        body.Variables.Add(pathVar);

        var il = body.GetILProcessor();
        var loadBytes = ImportFileUtilLoadBytes(module);
        var tempPath = ImportFieldFromHotfix(module, "FileUtil", "TempPath");
        var stringConcat = ImportCallFromHotfix(module, "String", "Concat", 2);
        var writeAllBytes = ImportCallFromHotfix(module, "File", "WriteAllBytes", 2);
        var assemblyLoadFrom = ImportAssemblyLoadFromEvidencePublic(module);
        var getTypeStatic = ImportTypeGetTypeStatic(module);
        var getType = ImportAssemblyGetType(module);
        var getMethod = ImportTypeGetMethod(module);
        var invoke = ImportMethodInvoke(module);

        Instruction? skipRet = null;
        if (skipIfTypeLoaded)
        {
            skipRet = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldstr, BridgeTypeName + ", " + BridgeTypeName));
            il.Append(il.Create(OpCodes.Call, getTypeStatic));
            il.Append(il.Create(OpCodes.Brtrue_S, skipRet));
        }

        il.Append(il.Create(OpCodes.Ldstr, BridgeDllAssetPath));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Stloc, bytesVar));
        il.Append(il.Create(OpCodes.Ldsfld, tempPath));
        il.Append(il.Create(OpCodes.Ldstr, BridgeTempDllSuffix));
        il.Append(il.Create(OpCodes.Call, stringConcat));
        il.Append(il.Create(OpCodes.Stloc, pathVar));
        il.Append(il.Create(OpCodes.Ldloc, pathVar));
        il.Append(il.Create(OpCodes.Ldloc, bytesVar));
        il.Append(il.Create(OpCodes.Call, writeAllBytes));
        il.Append(il.Create(OpCodes.Ldloc, pathVar));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Call, assemblyLoadFrom));
        var failRet = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse_S, failRet));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldstr, BridgeTypeName));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse_S, failRet));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldstr, BridgeBootstrapName));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(failRet);
        if (skipRet != null)
        {
            il.Append(skipRet);
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = 16;
    }

    private static MethodReference ImportCallFromHotfix(
        ModuleDefinition module,
        string typeName,
        string methodName,
        int paramCount)
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
                    || called.DeclaringType?.Name != typeName)
                {
                    continue;
                }

                return module.ImportReference(called);
            }
        }

        throw new InvalidOperationException($"hotfix IL 中未找到 {typeName}.{methodName}");
    }

    private static FieldReference ImportFieldFromHotfix(
        ModuleDefinition module,
        string typeName,
        string fieldName)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode != OpCodes.Ldsfld || insn.Operand is not FieldReference field)
                {
                    continue;
                }

                if (field.DeclaringType?.Name == typeName && field.Name == fieldName)
                {
                    return module.ImportReference(field);
                }
            }
        }

        throw new InvalidOperationException($"hotfix IL 中未找到 {typeName}.{fieldName}");
    }

    public static void ApplyUpdateHook(MethodBody updateBody, MethodReference loaderMethod)
    {
        var first = updateBody.Instructions[0];
        var il = updateBody.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Call, loaderMethod));
        IlSerializer.RecalculateOffsets(updateBody);
        updateBody.MaxStackSize = Math.Max(updateBody.MaxStackSize, (short)8);
    }

    public static byte[] BuildUpdateHookBody(
        MethodBody updateBody,
        MethodDefinition loaderMethod,
        UserStringHeap userStrings)
    {
        ApplyUpdateHook(updateBody, loaderMethod);
        return IlSerializer.Serialize(updateBody, userStrings);
    }

    public static void BuildEmptyRetBody(MethodDefinition method)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = false;

        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ret));

        IlSerializer.RecalculateOffsets(method.Body);
        method.Body.MaxStackSize = 1;
    }

    /// <summary>HotfixEntry.Start 末尾 AddTimeInvoke 延迟 bootstrap。</summary>
    public static void ApplyDeferredTimerStartHook(
        MethodBody originalStart,
        MethodDefinition bootstrapMethod,
        ModuleDefinition module)
    {
        var injectBefore = originalStart.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        var il = originalStart.GetILProcessor();
        var addTimeInvoke = ImportTimerAddTimeInvoke(module);
        var actionCtor = ImportActionCtor(module);

        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_R4, BootstrapDelaySeconds));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldnull));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldftn, bootstrapMethod));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Newobj, actionCtor));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Call, addTimeInvoke));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Pop));

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
    }

    public static void ApplyEarlyStartHook(MethodBody originalStart, MethodReference hookMethod)
    {
        var injectBefore = originalStart.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        var il = originalStart.GetILProcessor();
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Call, hookMethod));

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
    }

    public static byte[] BuildEarlyStartHookBody(
        MethodBody originalStart,
        MethodReference hookMethod,
        byte[] startSnapshot)
    {
        ApplyEarlyStartHook(originalStart, hookMethod);
        return IlSerializer.Serialize(originalStart, startSnapshot);
    }

    public static void ApplyGameManagerStartHook(MethodBody originalStart, MethodReference hookMethod)
    {
        var dispatch = FindGameManagerDispatchInstruction(originalStart);
        var insertPoint = dispatch.Previous;
        if (insertPoint is null || insertPoint.OpCode != OpCodes.Ldc_I4_0)
        {
            insertPoint = dispatch;
        }

        var il = originalStart.GetILProcessor();
        il.InsertBefore(insertPoint, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(insertPoint, il.Create(OpCodes.Call, hookMethod));

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
    }

    public static void ApplyGameManagerStartHookDirect(MethodBody originalStart, MethodReference hookMethod)
    {
        var dispatch = FindGameManagerDispatchInstruction(originalStart);
        var il = originalStart.GetILProcessor();
        il.InsertBefore(dispatch, il.Create(OpCodes.Call, hookMethod));

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
    }

    private static Instruction FindGameManagerDispatchInstruction(MethodBody originalStart)
        => originalStart.Instructions.First(i =>
            i.OpCode == OpCodes.Callvirt
            && i.Operand is MethodReference called
            && called.Name == "Dispatch")
            ?? throw new InvalidOperationException("未找到 ScreenTransiton.Dispatch");

    public static byte[] BuildStartHookBody(MethodBody originalStart, MethodReference hookMethod, UserStringHeap userStrings)
    {
        ApplyGameManagerStartHook(originalStart, hookMethod);
        return IlSerializer.Serialize(originalStart, userStrings);
    }

    private static MethodReference ImportTimerAddTimeInvoke(ModuleDefinition module)
        => ImportCallFromHotfix(module, "Timer", "AddTimeInvoke", 2);

    private static MethodReference ImportTimerCreate(ModuleDefinition module)
        => ImportCallFromHotfix(module, "Timer", "Create", 5);

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

    private static MethodReference ImportFileUtilLoadBytes(ModuleDefinition module)
    {
        var moli = module.AssemblyResolver.Resolve(
            module.AssemblyReferences.First(r => r.Name == "Moli"));
        var fileUtil = moli.MainModule.Types.First(t => t.Name == "FileUtil");
        var method = fileUtil.Methods.First(m =>
            m.Name == "LoadBytesFromHotfixAssets"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Name == "String");
        return module.ImportReference(method);
    }

    private static MethodReference ImportAssemblyLoad(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var asmType = corlib.MainModule.Types.First(t =>
            t.FullName == "System.Reflection.Assembly");
        var method = asmType.Methods.FirstOrDefault(m =>
            m.Name == "Load"
            && m.IsStatic
            && m.HasParameters
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.IsArray)
            ?? asmType.Methods.First(m =>
                m.Name == "Load"
                && m.IsStatic
                && m.HasParameters
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "System.Byte[]");
        return module.ImportReference(method);
    }

    private static MethodReference ImportTypeGetTypeStatic(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var typeType = corlib.MainModule.Types.First(t => t.FullName == "System.Type");
        var method = typeType.Methods.First(m =>
            m.Name == "GetType"
            && m.IsStatic
            && m.HasParameters
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        return module.ImportReference(method);
    }

    private static MethodReference ImportAssemblyGetType(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var asmType = corlib.MainModule.Types.First(t =>
            t.FullName == "System.Reflection.Assembly");
        var method = asmType.Methods.First(m =>
            m.Name == "GetType"
            && !m.IsStatic
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        return module.ImportReference(method);
    }

    private static MethodReference ImportTypeGetMethod(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var typeType = corlib.MainModule.Types.First(t => t.FullName == "System.Type");
        var method = typeType.Methods.First(m =>
            m.Name == "GetMethod"
            && !m.IsStatic
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.String");
        return module.ImportReference(method);
    }

    private static MethodReference ImportMethodInvoke(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var methodBaseType = corlib.MainModule.Types.First(t => t.FullName == "System.Reflection.MethodBase");
        var method = methodBaseType.Methods.First(m =>
            m.Name == "Invoke"
            && !m.IsStatic
            && m.HasParameters
            && m.Parameters.Count == 2);
        return module.ImportReference(method);
    }

    private static AssemblyDefinition ResolveCorlib(ModuleDefinition module)
    {
        var name = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib")
            ?? module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        return module.AssemblyResolver.Resolve(name);
    }

    internal static MethodReference ImportFileUtilLoadBytesPublic(ModuleDefinition module) => ImportFileUtilLoadBytes(module);
    internal static MethodReference ImportAssemblyLoadPublic(ModuleDefinition module) => ImportAssemblyLoad(module);
    internal static MethodReference ImportTypeGetTypeStaticPublic(ModuleDefinition module) => ImportTypeGetTypeStatic(module);
    internal static MethodReference ImportAssemblyGetTypePublic(ModuleDefinition module) => ImportAssemblyGetType(module);
    internal static MethodReference ImportTypeGetMethodPublic(ModuleDefinition module) => ImportTypeGetMethod(module);
    internal static MethodReference ImportMethodInvokePublic(ModuleDefinition module) => ImportMethodInvoke(module);

    internal static MethodReference ImportAssemblyLoadFromEvidencePublic(ModuleDefinition module)
    {
        var corlib = ResolveCorlib(module);
        var asmType = corlib.MainModule.Types.First(t =>
            t.FullName == "System.Reflection.Assembly");
        var method = asmType.Methods.FirstOrDefault(m =>
            m.Name == "LoadFrom"
            && m.IsStatic
            && m.HasParameters
            && m.Parameters.Count == 2
            && m.Parameters[0].ParameterType.Name == "String")
            ?? asmType.Methods.First(m =>
                m.Name == "LoadFrom"
                && m.IsStatic
                && m.HasParameters
                && m.Parameters.Count == 2);
        return module.ImportReference(method);
    }
}
