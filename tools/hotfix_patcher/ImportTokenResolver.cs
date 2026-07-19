using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ImportTokenResolver
{
    public sealed class ResolvedTokens
    {
        public required uint LoadBytesFromHotfixAssets { get; init; }
        public required uint AssemblyLoad { get; init; }
        public required uint TypeGetTypeStatic { get; init; }
        public required uint AssemblyGetType { get; init; }
        public required uint TypeGetMethod { get; init; }
        public required uint MethodInvoke { get; init; }
    }

    public sealed class LoadFromTokens
    {
        public required uint LoadBytesFromHotfixAssets { get; init; }
        public required uint FileWriteAllBytes { get; init; }
        public required uint StringConcat { get; init; }
        public required uint AssemblyLoadFrom { get; init; }
        public required uint TypeGetTypeStatic { get; init; }
        public required uint AssemblyGetType { get; init; }
        public required uint TypeGetMethod { get; init; }
        public required uint MethodInvoke { get; init; }
        public uint FileUtilTempPathField { get; init; } = 0x0A00032B;
    }

    public static LoadFromTokens ResolveLoadFromTokens(
        AssemblyDefinition asm,
        ModuleDefinition module,
        byte[] pe,
        IAssemblyResolver resolver,
        byte[] probePe)
    {
        var typeRefLayout = SystemMetadataBridge.GetTypeRefRowLayout(pe);
        var typeRefTableOffset = SystemMetadataBridge.GetTypeRefTableFileOffset(pe);
        var memberRefLayout = SystemMetadataBridge.GetMemberRefRowLayout(pe);
        var memberRefTableOffset = SystemMetadataBridge.GetMemberRefTableFileOffset(pe);

        var reflection = ReflectionImportPatcher.ImportIntoPe(
            pe,
            module,
            resolver,
            typeRefLayout,
            typeRefTableOffset,
            memberRefLayout,
            memberRefTableOffset,
            probePe);

        uint loadFromToken;
        var loadFromExisting = TryFindToken(pe, module, "Assembly", "LoadFrom");
        if (loadFromExisting == null)
        {
            loadFromToken = ReflectionImportPatcher.ImportSingleMethodIntoPe(
                pe,
                module,
                resolver,
                typeRefLayout,
                typeRefTableOffset,
                memberRefLayout,
                memberRefTableOffset,
                "Assembly",
                "LoadFrom",
                2,
                probePe);
        }
        else
        {
            loadFromToken = loadFromExisting.Value;
        }

        var writeAllBytes = TryFindToken(pe, module, "File", "WriteAllBytes")
            ?? throw new InvalidOperationException("hotfix 中未找到 File.WriteAllBytes");
        var concat = TryFindToken(pe, module, "String", "Concat")
            ?? throw new InvalidOperationException("hotfix 中未找到 String.Concat");
        return new LoadFromTokens
        {
            LoadBytesFromHotfixAssets = reflection.LoadBytesFromHotfixAssets,
            FileWriteAllBytes = writeAllBytes,
            StringConcat = concat,
            AssemblyLoadFrom = loadFromToken,
            TypeGetTypeStatic = reflection.TypeGetTypeStatic,
            AssemblyGetType = reflection.AssemblyGetType,
            TypeGetMethod = reflection.TypeGetMethod,
            MethodInvoke = reflection.MethodInvoke,
        };
    }

    private static uint? TryFindToken(byte[] pe, ModuleDefinition module, string typeName, string methodName)
    {
        try
        {
            return MemberRefTokenLookup.FindToken(module, typeName, methodName);
        }
        catch
        {
            try
            {
                return MemberRefTokenLookup.FindToken(pe, typeName, methodName);
            }
            catch
            {
                return null;
            }
        }
    }

    public static ResolvedTokens ResolveLoaderTokens(AssemblyDefinition asm, ModuleDefinition module, byte[] pe)
        => ResolveLoaderTokens(asm, module, pe, asm.MainModule.AssemblyResolver, pe);

    public static ResolvedTokens ResolveLoaderTokens(
        AssemblyDefinition asm,
        ModuleDefinition module,
        byte[] pe,
        IAssemblyResolver resolver,
        byte[] probePe)
    {
        var typeRefLayout = SystemMetadataBridge.GetTypeRefRowLayout(pe);
        var typeRefTableOffset = SystemMetadataBridge.GetTypeRefTableFileOffset(pe);
        var memberRefLayout = SystemMetadataBridge.GetMemberRefRowLayout(pe);
        var memberRefTableOffset = SystemMetadataBridge.GetMemberRefTableFileOffset(pe);
        return ReflectionImportPatcher.ImportIntoPe(
            pe,
            module,
            resolver,
            typeRefLayout,
            typeRefTableOffset,
            memberRefLayout,
            memberRefTableOffset,
            probePe);
    }

    /// <summary>
    /// Cecil Write 会丢弃未被 IL 引用的 Import；用探测方法保留 MemberRef 后再从写出 PE 解析 token。
    /// </summary>
    public static void AppendTokenProbeMethodPublic(
        ModuleDefinition module,
        MethodReference loadBytes,
        MethodReference assemblyLoad,
        MethodReference getTypeStatic,
        MethodReference getType,
        MethodReference getMethod,
        MethodReference invoke)
    {
        var probeType = module.Types.FirstOrDefault(t => t.Name == "<SeqChapterTokenProbe>");
        if (probeType == null)
        {
            probeType = new TypeDefinition(
                "",
                "<SeqChapterTokenProbe>",
                TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract,
                module.TypeSystem.Object);
            module.Types.Add(probeType);
        }

        var probeMethod = probeType.Methods.FirstOrDefault(m => m.Name == "Probe");
        if (probeMethod == null)
        {
            probeMethod = new MethodDefinition(
                "Probe",
                MethodAttributes.Private | MethodAttributes.Static,
                module.TypeSystem.Void);
            probeType.Methods.Add(probeMethod);
        }

        probeMethod.Body ??= new MethodBody(probeMethod);
        probeMethod.Body.Instructions.Clear();
        probeMethod.Body.Variables.Clear();
        probeMethod.Body.ExceptionHandlers.Clear();
        probeMethod.Body.InitLocals = false;

        var il = probeMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldstr, "hotfixdata/SeqChapterHelperBridge.dll.bytes"));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        il.Append(il.Create(OpCodes.Ldstr, "SeqChapterHelperBridge"));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Ldstr, "Bootstrap"));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ldstr, "SeqChapterHelperBridge, SeqChapterHelperBridge"));
        il.Append(il.Create(OpCodes.Call, getTypeStatic));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        probeMethod.Body.MaxStackSize = 8;
    }

    public static void AppendLoadFromProbeMethodPublic(ModuleDefinition module, MethodReference loadFrom)
    {
        var probeType = module.Types.FirstOrDefault(t => t.Name == "<SeqChapterLoadFromProbe>");
        if (probeType == null)
        {
            probeType = new TypeDefinition(
                "",
                "<SeqChapterLoadFromProbe>",
                TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract,
                module.TypeSystem.Object);
            module.Types.Add(probeType);
        }

        var probeMethod = probeType.Methods.FirstOrDefault(m => m.Name == "Probe");
        if (probeMethod == null)
        {
            probeMethod = new MethodDefinition(
                "Probe",
                MethodAttributes.Private | MethodAttributes.Static,
                module.TypeSystem.Void);
            probeType.Methods.Add(probeMethod);
        }

        probeMethod.Body ??= new MethodBody(probeMethod);
        probeMethod.Body.Instructions.Clear();
        probeMethod.Body.Variables.Clear();
        probeMethod.Body.ExceptionHandlers.Clear();
        probeMethod.Body.InitLocals = false;

        var il = probeMethod.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldstr, "probe.dll"));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Call, loadFrom));
        il.Append(il.Create(OpCodes.Pop));
        il.Append(il.Create(OpCodes.Ret));
        probeMethod.Body.MaxStackSize = 8;
    }

    private static bool ContainsAscii(byte[] data, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - bytes.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (data[i + j] != bytes[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }
}
