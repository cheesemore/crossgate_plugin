using Mono.Cecil;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace CrossgateMod.Patcher;

/// <summary>
/// 从 .orig 上 Cecil 轻量桥接模板，把变更的 MemberRef 行（同行号）与 #Blob 增量复制到已打玩法补丁的 hotfix。
/// </summary>
internal static class BridgeMemberRefTemplateImporter
{
    public static ImportTokenResolver.ResolvedTokens Import(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        string hotfixDir)
    {
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var bridgedAsm = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            new MemoryStream(bridged, writable: false),
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });

        var bridgedRefs = CollectBridgeMemberRefs(bridgedAsm.MainModule);
        if (bridgedRefs.Count != 6)
        {
            throw new InvalidOperationException(
                $"桥接模板 MemberRef 不完整：期望 6 条，找到 {bridgedRefs.Count}");
        }

        var layout = SystemMetadataBridge.GetMemberRefRowLayout(target);
        var tableOffset = SystemMetadataBridge.GetMemberRefTableFileOffset(target);
        var mapped = new Dictionary<string, uint>(StringComparer.Ordinal);

        CopyChangedTypeRefRowsFromBridged(orig, bridged, target);

        foreach (var spec in bridgedRefs)
        {
            var bridgedToken = spec.Method.MetadataToken.ToUInt32();
            var targetToken = CopyMemberRefRowAtSameIndex(
                orig, bridged, target, bridgedToken, layout, tableOffset);
            mapped[spec.Key] = targetToken;
            Console.WriteLine(
                $"[META] 模板复制 {spec.TypeName}.{spec.MethodName} token=0x{targetToken:X8}");
        }

        return new ImportTokenResolver.ResolvedTokens
        {
            LoadBytesFromHotfixAssets = mapped["LoadBytesFromHotfixAssets"],
            AssemblyLoad = mapped["AssemblyLoad"],
            TypeGetTypeStatic = mapped["TypeGetTypeStatic"],
            AssemblyGetType = mapped["AssemblyGetType"],
            TypeGetMethod = mapped["TypeGetMethod"],
            MethodInvoke = mapped["MethodInvoke"],
        };
    }

    private sealed record BridgeMemberRefSpec(
        string Key,
        string TypeName,
        string MethodName,
        MethodReference Method);

    private static List<BridgeMemberRefSpec> CollectBridgeMemberRefs(Mono.Cecil.ModuleDefinition module)
    {
        var found = new Dictionary<string, BridgeMemberRefSpec>(StringComparer.Ordinal);
        foreach (var memberRef in module.GetMemberReferences())
        {
            if (memberRef is not MethodReference method)
            {
                continue;
            }

            var typeName = method.DeclaringType?.Name ?? "";
            var methodName = method.Name;
            string? key = (typeName, methodName, method.HasThis) switch
            {
                ("FileUtil", "LoadBytesFromHotfixAssets", _) => "LoadBytesFromHotfixAssets",
                ("Assembly", "Load", false) => "AssemblyLoad",
                ("Type", "GetType", false) => "TypeGetTypeStatic",
                ("Assembly", "GetType", true) => "AssemblyGetType",
                ("Type", "GetMethod", true) => "TypeGetMethod",
                ("MethodBase", "Invoke", true) => "MethodInvoke",
                _ => null,
            };

            if (key == null || found.ContainsKey(key))
            {
                continue;
            }

            found[key] = new BridgeMemberRefSpec(key, typeName, methodName, method);
        }

        var order = new[]
        {
            "LoadBytesFromHotfixAssets",
            "AssemblyLoad",
            "TypeGetTypeStatic",
            "AssemblyGetType",
            "TypeGetMethod",
            "MethodInvoke",
        };
        return order.Where(found.ContainsKey).Select(k => found[k]).ToList();
    }

    private static void CopyChangedTypeRefRowsFromBridged(byte[] orig, byte[] bridged, byte[] target)
    {
        var layout = SystemMetadataBridge.GetTypeRefRowLayout(target);
        var tableOffset = SystemMetadataBridge.GetTypeRefTableFileOffset(target);
        var rowCount = SystemMetadataBridge.OpenReader(orig).GetTableRowCount(TableIndex.TypeRef);
        for (var row = 1; row <= rowCount; row++)
        {
            var rowOffset = tableOffset + (row - 1) * layout.RowSize;
            if (BytesEqual(orig, bridged, rowOffset, layout.RowSize))
            {
                continue;
            }

            if (!BytesEqual(orig, target, rowOffset, layout.RowSize))
            {
                continue;
            }

            EnsureTypeRefRowFromBridged(orig, bridged, target, row);
        }
    }

    private static uint CopyMemberRefRowAtSameIndex(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        uint bridgedToken,
        SystemMetadataBridge.MemberRefRowLayout layout,
        int tableOffset)
    {
        var row = (int)(bridgedToken & 0xFFFFFF) - 1;
        var rowOffset = tableOffset + row * layout.RowSize;
        if (BytesEqual(orig, bridged, rowOffset, layout.RowSize))
        {
            return bridgedToken;
        }

        if (!BytesEqual(orig, target, rowOffset, layout.RowSize))
        {
            throw new InvalidOperationException(
                $"MemberRef 行 {row + 1} 在玩法补丁后已变化，无法安全复制桥接模板");
        }

        var bridgedReader = SystemMetadataBridge.OpenReader(bridged);
        var handle = MetadataTokens.MemberReferenceHandle((int)(bridgedToken & 0xFFFFFF));
        var memberRef = bridgedReader.GetMemberReference(handle);
        var classRow = ResolveTypeRefRowIndex(bridged, rowOffset, layout);
        if (classRow > 0)
        {
            EnsureTypeRefRowFromBridged(orig, bridged, target, classRow);
        }

        var sigBlob = SystemMetadataBridge.ReadBlobHeapEntry(bridged, memberRef.Signature);
        var targetSigIndex = ResolveSignatureBlobIndex(
            orig, bridged, target, memberRef.Signature, sigBlob);
        var methodName = bridgedReader.GetString(memberRef.Name);
        var nameIndex = ResolveStringIndex(target, methodName);

        var classCoded = layout.ReadClassCodedIndex(bridged, rowOffset);
        layout.WriteRow(target, rowOffset, classCoded, nameIndex, targetSigIndex);
        return bridgedToken;
    }

    private static int ResolveTypeRefRowIndex(
        byte[] bridged,
        int memberRefRowOffset,
        SystemMetadataBridge.MemberRefRowLayout layout)
    {
        var classCoded = layout.ReadClassCodedIndex(bridged, memberRefRowOffset);
        if ((classCoded & 0x03) != 0x01)
        {
            return 0;
        }

        return (classCoded >> 2) + 1;
    }

    private static void EnsureTypeRefRowFromBridged(byte[] orig, byte[] bridged, byte[] target, int typeRefRow)
    {
        var layout = SystemMetadataBridge.GetTypeRefRowLayout(target);
        var tableOffset = SystemMetadataBridge.GetTypeRefTableFileOffset(target);
        var rowOffset = tableOffset + (typeRefRow - 1) * layout.RowSize;
        if (BytesEqual(orig, bridged, rowOffset, layout.RowSize))
        {
            return;
        }

        if (!BytesEqual(orig, target, rowOffset, layout.RowSize))
        {
            throw new InvalidOperationException(
                $"TypeRef 行 {typeRefRow} 在玩法补丁后已变化，无法安全复制桥接模板");
        }

        var bridgedReader = SystemMetadataBridge.OpenReader(bridged);
        var typeHandle = MetadataTokens.TypeReferenceHandle(typeRefRow);
        var typeRef = bridgedReader.GetTypeReference(typeHandle);
        var typeName = bridgedReader.GetString(typeRef.Name);
        var ns = typeRef.Namespace.IsNil ? string.Empty : bridgedReader.GetString(typeRef.Namespace);
        var nameIndex = ResolveTypeRefStringIndex(orig, bridged, target, typeRef.Name, typeName);
        var nsIndex = string.IsNullOrEmpty(ns)
            ? 0
            : ResolveTypeRefStringIndex(orig, bridged, target, typeRef.Namespace, ns);
        var scopeCoded = layout.ReadResolutionScopeCoded(bridged, rowOffset);
        layout.WriteRow(target, rowOffset, scopeCoded, nameIndex, nsIndex);
    }

    private static int ResolveStringIndex(byte[] target, string text)
    {
        var existing = MetadataStreamGaps.FindStringIndex(target, text);
        return existing >= 0 ? existing : MetadataStreamGaps.EnsureString(target, text);
    }

    private static int ResolveTypeRefStringIndex(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        StringHandle handle,
        string text)
    {
        return ResolveStringIndex(target, text);
    }

    private static void EnsureStringAtIndexFromBridged(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        StringHandle handle)
    {
        if (handle.IsNil)
        {
            return;
        }

        var index = MetadataTokens.GetHeapOffset(handle);
        if (index <= 0)
        {
            return;
        }

        var origText = ReadNullTerminatedUtf8(orig, index);
        var targetText = ReadNullTerminatedUtf8(target, index);
        if (!string.Equals(origText, targetText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"#Strings 索引 0x{index:X} 在玩法补丁后已变化（{origText} → {targetText}）");
        }

        var bridgedText = ReadNullTerminatedUtf8(bridged, index);
        if (string.Equals(targetText, bridgedText, StringComparison.Ordinal))
        {
            return;
        }

        var strings = MetadataStreamGaps.ListStreams(target).First(s => s.Name == "#Strings");
        var payload = System.Text.Encoding.UTF8.GetBytes(bridgedText + "\0");
        if (index + payload.Length > strings.Size)
        {
            throw new InvalidOperationException(
                $"#Strings 索引 0x{index:X} 无法覆盖桥接字符串（需 {payload.Length} 字节）");
        }

        payload.CopyTo(target.AsSpan(strings.FileOffset + index));
    }

    private static string ReadNullTerminatedUtf8(byte[] pe, int index)
    {
        var strings = MetadataStreamGaps.ListStreams(pe).First(s => s.Name == "#Strings");
        if (index <= 0 || index >= strings.Size)
        {
            return string.Empty;
        }

        var start = strings.FileOffset + index;
        var end = strings.FileOffset + strings.Size;
        var len = 0;
        while (start + len < end && pe[start + len] != 0)
        {
            len++;
        }

        return System.Text.Encoding.UTF8.GetString(pe, start, len);
    }

    private static int ResolveSignatureBlobIndex(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        BlobHandle bridgedSigHandle,
        ReadOnlySpan<byte> sigBlob)
    {
        var existing = FindBlobIndex(target, sigBlob);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        var bridgedSigIndex = MetadataTokens.GetHeapOffset(bridgedSigHandle);
        if (TryOverlayBlobAtIndex(orig, target, bridgedSigIndex, sigBlob))
        {
            return bridgedSigIndex;
        }

        return MetadataStreamGaps.EnsureBlob(target, sigBlob);
    }

    private static bool TryOverlayBlobAtIndex(
        byte[] orig,
        byte[] target,
        int index,
        ReadOnlySpan<byte> sigBlob)
    {
        if (index <= 0)
        {
            return false;
        }

        var stream = MetadataStreamGaps.ListStreams(target).First(s => s.Name == "#Blob");
        if (index >= stream.Size || index + sigBlob.Length > stream.Size)
        {
            return false;
        }

        var origEntry = ReadBlobEntryBytes(orig, index);
        var targetEntry = ReadBlobEntryBytes(target, index);
        if (origEntry == null || targetEntry == null)
        {
            return false;
        }

        if (!origEntry.AsSpan().SequenceEqual(targetEntry))
        {
            return false;
        }

        sigBlob.CopyTo(target.AsSpan(stream.FileOffset + index));
        return true;
    }

    private static byte[]? ReadBlobEntryBytes(byte[] pe, int index)
    {
        try
        {
            var handle = MetadataTokens.BlobHandle(index);
            return SystemMetadataBridge.ReadBlobHeapEntry(pe, handle);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSigIndex(
        byte[] pe,
        int rowOffset,
        SystemMetadataBridge.MemberRefRowLayout layout,
        int sigIndex)
    {
        var off = rowOffset + layout.ParentIndexSize + layout.StringIndexSize;
        if (layout.BlobIndexSize == 4)
        {
            BitConverter.GetBytes(sigIndex).CopyTo(pe, off);
            return;
        }

        BitConverter.GetBytes((ushort)sigIndex).CopyTo(pe, off);
    }

    private static int? FindBlobIndex(byte[] pe, ReadOnlySpan<byte> blobData)
    {
        var stream = MetadataStreamGaps.ListStreams(pe).First(s => s.Name == "#Blob");
        var end = stream.FileOffset + stream.Size;
        for (var index = 1; index < stream.Size; index++)
        {
            var abs = stream.FileOffset + index;
            if (abs + blobData.Length > end)
            {
                break;
            }

            var ok = true;
            for (var i = 0; i < blobData.Length; i++)
            {
                if (pe[abs + i] != blobData[i])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return index;
            }
        }

        return null;
    }

    private static bool BytesEqual(byte[] left, byte[] right, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (left[offset + i] != right[offset + i])
            {
                return false;
            }
        }

        return true;
    }
}
