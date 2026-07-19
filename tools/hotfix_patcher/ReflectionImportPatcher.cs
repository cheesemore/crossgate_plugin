using Mono.Cecil;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using CecilAssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace CrossgateMod.Patcher;

internal static class ReflectionImportPatcher
{
    private static readonly Dictionary<string, int> TypeRefRowCache = new(StringComparer.Ordinal);

    private sealed record ImportSpec(string TypeName, string MethodName, string? Scope);

    private static readonly ImportSpec[] Required =
    {
        new("FileUtil", "LoadBytesFromHotfixAssets", "Moli"),
        new("Assembly", "Load", "mscorlib"),
        new("Type", "GetType", "mscorlib"),
        new("Assembly", "GetType", "mscorlib"),
        new("Type", "GetMethod", "mscorlib"),
        new("MethodBase", "Invoke", "mscorlib"),
    };

    public static ImportTokenResolver.ResolvedTokens ImportIntoPe(
        byte[] pe,
        ModuleDefinition targetModule,
        IAssemblyResolver resolver,
        SystemMetadataBridge.TypeRefRowLayout typeRefLayout,
        int typeRefTableOffset,
        SystemMetadataBridge.MemberRefRowLayout memberRefLayout,
        int memberRefTableOffset,
        byte[] probePe)
    {
        TypeRefRowCache.Clear();
        MetadataTableRecycler.ResetTypeRefPool(typeRefTableOffset, typeRefLayout);
        var tempPath = Path.Combine(Path.GetTempPath(), "seqchapter_imp_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            // Cecil Write 在「已打玩法补丁」的 hotfix 上会失败；用干净 .orig 做 probe，再写入目标 PE。
            using var probeAsm = CecilAssemblyDefinition.ReadAssembly(
                new MemoryStream(probePe, writable: false),
                new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadWrite = true,
                });
            var probeModule = probeAsm.MainModule;
            var loadBytes = BridgeLoaderIlBuilder.ImportFileUtilLoadBytesPublic(probeModule);
            var assemblyLoad = BridgeLoaderIlBuilder.ImportAssemblyLoadPublic(probeModule);
            var getTypeStatic = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(probeModule);
            var getType = BridgeLoaderIlBuilder.ImportAssemblyGetTypePublic(probeModule);
            var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(probeModule);
            var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(probeModule);
            ImportTokenResolver.AppendTokenProbeMethodPublic(
                probeModule, loadBytes, assemblyLoad, getTypeStatic, getType, getMethod, invoke);
            probeAsm.Write(tempPath);
            var tempPe = File.ReadAllBytes(tempPath);

            using var tempAsm = CecilAssemblyDefinition.ReadAssembly(tempPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

            var typeRefRows = new int[Required.Length];
            for (var i = 0; i < Required.Length; i++)
            {
                var spec = Required[i];
                var tempToken = MemberRefTokenLookup.FindToken(tempAsm.MainModule, spec.TypeName, spec.MethodName);
                var tempMethod = (MethodReference)tempAsm.MainModule.LookupToken((int)tempToken);
                typeRefRows[i] = EnsureTypeRefRow(pe, targetModule, tempMethod.DeclaringType);
            }

            var freeRows = UsedMemberRefScanner.FindUnusedRows(pe, Required.Length);
            var tokens = new uint[Required.Length];

            for (var i = 0; i < Required.Length; i++)
            {
                var spec = Required[i];
                var tempToken = MemberRefTokenLookup.FindToken(tempAsm.MainModule, spec.TypeName, spec.MethodName);
                var tempReader = SystemMetadataBridge.OpenReader(tempPe);
                var tempHandle = MetadataTokens.MemberReferenceHandle((int)(tempToken & 0xFFFFFF));
                var tempMemberRef = tempReader.GetMemberReference(tempHandle);

                var nameIndex = MetadataStreamGaps.EnsureString(pe, spec.MethodName);
                var sig = SystemMetadataBridge.ReadBlobHeapEntry(tempPe, tempMemberRef.Signature);
                var sigIndex = MetadataStreamGaps.EnsureBlob(pe, sig);
                var classCoded = (typeRefRows[i] << 2) | 0x01;

                var rowOffset = memberRefTableOffset + freeRows[i] * memberRefLayout.RowSize;
                memberRefLayout.WriteRow(pe, rowOffset, classCoded, nameIndex, sigIndex);
                tokens[i] = 0x0A000000u | (uint)(freeRows[i] + 1);
                Console.WriteLine(
                    $"[META] MemberRef row {freeRows[i] + 1} ← {spec.TypeName}.{spec.MethodName} token=0x{tokens[i]:X8}");
            }

            return new ImportTokenResolver.ResolvedTokens
            {
                LoadBytesFromHotfixAssets = tokens[0],
                AssemblyLoad = tokens[1],
                TypeGetTypeStatic = tokens[2],
                AssemblyGetType = tokens[3],
                TypeGetMethod = tokens[4],
                MethodInvoke = tokens[5],
            };
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static int EnsureTypeRefRow(byte[] origPe, ModuleDefinition origModule, TypeReference declaringType)
    {
        var key = declaringType.FullName;
        if (TypeRefRowCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (TryFindTypeRefRow(origModule, declaringType, out var existing))
        {
            TypeRefRowCache[key] = existing;
            return existing;
        }

        if (TryFindTypeRefRowInPe(origPe, declaringType, out existing))
        {
            TypeRefRowCache[key] = existing;
            return existing;
        }

        var row = ImportTypeRefRow(origPe, origModule, declaringType);
        TypeRefRowCache[key] = row;
        return row;
    }

    private static bool TryFindTypeRefRowInPe(byte[] pe, TypeReference target, out int row)
    {
        row = -1;
        var reader = SystemMetadataBridge.OpenReader(pe);
        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeRef.Namespace);
            var name = reader.GetString(typeRef.Name);
            var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            if (string.Equals(fullName, target.FullName, StringComparison.Ordinal)
                || (string.Equals(name, target.Name, StringComparison.Ordinal)
                    && string.Equals(ns, target.Namespace, StringComparison.Ordinal)))
            {
                row = reader.GetRowNumber(handle) - 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindTypeRefRow(ModuleDefinition module, TypeReference target, out int row)
    {
        row = -1;
        foreach (var typeRef in module.GetTypeReferences())
        {
            if (typeRef.MetadataToken.TokenType != TokenType.TypeRef)
            {
                continue;
            }

            if (string.Equals(typeRef.FullName, target.FullName, StringComparison.Ordinal)
                || (string.Equals(typeRef.Name, target.Name, StringComparison.Ordinal)
                    && string.Equals(typeRef.Namespace, target.Namespace, StringComparison.Ordinal)))
            {
                row = (int)typeRef.MetadataToken.RID - 1;
                return true;
            }
        }

        return false;
    }

    private static int ImportTypeRefRow(
        byte[] origPe,
        ModuleDefinition origModule,
        TypeReference declaringType)
    {
        if (declaringType.MetadataToken.TokenType != TokenType.TypeRef)
        {
            throw new InvalidOperationException(
                $"无法导入非 TypeRef 声明类型: {declaringType.FullName}");
        }

        var name = declaringType.Name;
        var ns = declaringType.Namespace ?? "";
        var origScopeCoded = MapResolutionScope(origModule, declaringType.Scope);
        var origNameIndex = MetadataStreamGaps.EnsureString(origPe, name);
        var origNsIndex = MetadataStreamGaps.EnsureString(origPe, ns);
        return MetadataTableRecycler.RecycleTypeRefRow(origPe, origScopeCoded, origNameIndex, origNsIndex);
    }

    private static int MapResolutionScope(ModuleDefinition origModule, IMetadataScope tempScope)
    {
        return tempScope switch
        {
            AssemblyNameReference asmRef => MapAssemblyRefRow(origModule, asmRef),
            Mono.Cecil.ModuleReference => 0,
            TypeReference typeRef when typeRef.MetadataToken.TokenType == TokenType.TypeRef =>
                (((int)typeRef.MetadataToken.RID - 1) << 2) | 0x02,
            _ => throw new InvalidOperationException(
                $"未支持的 TypeRef scope: {tempScope?.GetType().Name ?? "null"}"),
        };
    }

    private static int MapAssemblyRefRow(ModuleDefinition origModule, AssemblyNameReference tempAsm)
    {
        var name = tempAsm.Name ?? throw new InvalidOperationException("AssemblyRef 无名称");
        foreach (var asmRef in origModule.AssemblyReferences)
        {
            if (string.Equals(asmRef.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return (((int)asmRef.MetadataToken.RID - 1) << 2) | 0x01;
            }
        }

        throw new InvalidOperationException($"orig 中未找到 AssemblyRef {name}");
    }

    /// <summary>向 PE 追加单条 MemberRef（用于 LoadFrom 等 orig 中不存在的引用）。</summary>
    public static uint ImportSingleMethodIntoPe(
        byte[] pe,
        ModuleDefinition targetModule,
        IAssemblyResolver resolver,
        SystemMetadataBridge.TypeRefRowLayout typeRefLayout,
        int typeRefTableOffset,
        SystemMetadataBridge.MemberRefRowLayout memberRefLayout,
        int memberRefTableOffset,
        string typeName,
        string methodName,
        int paramCount,
        byte[] probePe)
    {
        TypeRefRowCache.Clear();
        MetadataTableRecycler.ResetTypeRefPool(typeRefTableOffset, typeRefLayout);
        var tempPath = Path.Combine(Path.GetTempPath(), "seqchapter_imp_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using var probeAsm = CecilAssemblyDefinition.ReadAssembly(
                new MemoryStream(probePe, writable: false),
                new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadWrite = true,
                });
            var probeModule = probeAsm.MainModule;
            var loadFrom = BridgeLoaderIlBuilder.ImportAssemblyLoadFromEvidencePublic(probeModule);
            if (loadFrom.Parameters.Count != paramCount)
            {
                throw new InvalidOperationException(
                    $"LoadFrom 参数个数 {loadFrom.Parameters.Count} != 期望 {paramCount}");
            }

            ImportTokenResolver.AppendLoadFromProbeMethodPublic(probeModule, loadFrom);
            probeAsm.Write(tempPath);
            var tempPe = File.ReadAllBytes(tempPath);

            using var tempAsm = CecilAssemblyDefinition.ReadAssembly(tempPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

            var tempToken = MemberRefTokenLookup.FindToken(tempAsm.MainModule, typeName, methodName);
            var tempMethod = (MethodReference)tempAsm.MainModule.LookupToken((int)tempToken);
            var typeRefRow = EnsureTypeRefRow(pe, targetModule, tempMethod.DeclaringType);
            var freeRows = UsedMemberRefScanner.FindUnusedRows(pe, 1);
            var tempReader = SystemMetadataBridge.OpenReader(tempPe);
            var tempHandle = MetadataTokens.MemberReferenceHandle((int)(tempToken & 0xFFFFFF));
            var tempMemberRef = tempReader.GetMemberReference(tempHandle);
            var nameIndex = MetadataStreamGaps.EnsureString(pe, methodName);
            var sig = SystemMetadataBridge.ReadBlobHeapEntry(tempPe, tempMemberRef.Signature);
            var sigIndex = MetadataStreamGaps.EnsureBlob(pe, sig);
            var classCoded = (typeRefRow << 2) | 0x01;
            var rowOffset = memberRefTableOffset + freeRows[0] * memberRefLayout.RowSize;
            memberRefLayout.WriteRow(pe, rowOffset, classCoded, nameIndex, sigIndex);
            var token = 0x0A000000u | (uint)(freeRows[0] + 1);
            Console.WriteLine($"[META] MemberRef row {freeRows[0] + 1} ← {typeName}.{methodName} token=0x{token:X8}");
            return token;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
