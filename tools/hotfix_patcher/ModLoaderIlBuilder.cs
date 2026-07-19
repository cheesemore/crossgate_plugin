using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ModLoaderIlBuilder
{
    private const string ModDllAssetPath = "hotfixdata/CrossgateMod.dll.bytes";
    private const string ModTypeName = "CrossgateMod.ModBootstrap";
    private const string ModInitName = "Init";

    public static byte[] BuildLoaderBody(MethodDefinition method, ModuleDefinition module, UserStringHeap userStrings)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = true;

        method.Body.Variables.Add(new VariableDefinition(new ArrayType(module.TypeSystem.Byte)));
        method.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object));
        method.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object));
        method.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object));

        var body = method.Body;
        var il = body.GetILProcessor();
        var loadBytes = ImportFileUtilLoadBytes(module);
        var assemblyLoad = ImportAssemblyLoad(module);
        var getType = ImportAssemblyGetType(module);
        var getMethod = ImportTypeGetMethod(module);
        var invoke = ImportMethodInvoke(module);

        il.Append(il.Create(OpCodes.Ldstr, ModDllAssetPath));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(il.Create(OpCodes.Ldloc_0));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(il.Create(OpCodes.Ldloc_1));
        il.Append(il.Create(OpCodes.Ldstr, ModTypeName));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Stloc_2));
        il.Append(il.Create(OpCodes.Ldloc_2));
        il.Append(il.Create(OpCodes.Ldstr, ModInitName));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Stloc_3));
        il.Append(il.Create(OpCodes.Ldloc_3));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = 8;
        return IlSerializer.Serialize(body, userStrings);
    }

    public static byte[] BuildStartHookBody(MethodBody originalStart, MethodReference hookMethod, UserStringHeap userStrings)
    {
        var injectBefore = originalStart.Instructions.First(i =>
            i.OpCode == OpCodes.Callvirt
            && i.Operand is MethodReference called
            && called.Name == "Dispatch")
            ?? throw new InvalidOperationException("未找到 ScreenTransiton.Dispatch");

        var il = originalStart.GetILProcessor();
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_0));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Call, hookMethod));

        IlSerializer.RecalculateOffsets(originalStart);
        originalStart.MaxStackSize = Math.Max(originalStart.MaxStackSize, (short)8);
        return IlSerializer.Serialize(originalStart, userStrings);
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
            ?? asmType.Methods.FirstOrDefault(m =>
                m.Name == "Load"
                && m.IsStatic
                && m.HasParameters
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "System.Byte[]")
            ?? throw new InvalidOperationException("mscorlib 中未找到 Assembly.Load(byte[])");
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
        var method = methodBaseType.Methods.FirstOrDefault(m =>
            m.Name == "Invoke"
            && !m.IsStatic
            && m.HasParameters
            && m.Parameters.Count == 2)
            ?? throw new InvalidOperationException("未找到 MethodBase.Invoke");
        return module.ImportReference(method);
    }

    private static AssemblyDefinition ResolveCorlib(ModuleDefinition module)
    {
        var name = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib")
            ?? module.AssemblyReferences.FirstOrDefault(r => r.Name == "System.Runtime")
            ?? throw new InvalidOperationException("未找到 mscorlib 引用");
        return module.AssemblyResolver.Resolve(name);
    }
}
