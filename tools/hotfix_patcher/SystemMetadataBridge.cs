using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CrossgateMod.Patcher;

/// <summary>用 BCL 元数据读取器做 TypeRef 回收与注入校验（避免手写 #~ 偏移错误）。</summary>
internal static class SystemMetadataBridge
{
    public static MetadataReader OpenReader(byte[] pe)
    {
        var stream = new MemoryStream(pe, writable: false);
        var peReader = new PEReader(stream);
        return peReader.GetMetadataReader();
    }

    public static HashSet<int> CollectUsedTypeRefRows(byte[] pe)
    {
        var reader = OpenReader(pe);
        var used = new HashSet<int>();

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MarkTypeRefParent(reader, reader.GetMemberReference(handle).Parent, used);
        }

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            MarkTypeRefParent(reader, typeDef.BaseType, used);
            foreach (var impl in typeDef.GetInterfaceImplementations())
            {
                MarkTypeRefParent(reader, reader.GetInterfaceImplementation(impl).Interface, used);
            }
        }

        MarkTypeRefsInBlobHeap(pe, reader.GetTableRowCount(TableIndex.TypeRef), used);
        return used;
    }

    private static void MarkTypeRefsInBlobHeap(byte[] pe, int typeRefRows, HashSet<int> used)
    {
        var blob = MetadataStreamGaps.ListStreams(pe).First(s => s.Name == "#Blob");
        var end = blob.FileOffset + blob.Size;
        for (var row = 1; row <= typeRefRows; row++)
        {
            var coded = (row << 2) | 0x01;
            if (ContainsCompressedUInt(pe, blob.FileOffset, end, coded))
            {
                used.Add(row);
            }
        }
    }

    private static bool ContainsCompressedUInt(byte[] pe, int start, int end, int value)
    {
        foreach (var pattern in BuildCompressedPatterns(value))
        {
            for (var i = start; i <= end - pattern.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (pe[i + j] != pattern[j])
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
        }

        return false;
    }

    private static IEnumerable<byte[]> BuildCompressedPatterns(int value)
    {
        if (value <= 0x7F)
        {
            yield return new[] { (byte)value };
            yield break;
        }

        if (value <= 0x3FFF)
        {
            yield return new[] { (byte)(0x80 | (value >> 8)), (byte)value };
            yield break;
        }

        yield return new[]
        {
            (byte)(0xC0 | (value >> 24)),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        };
    }

    public static List<int> FindUnusedTypeRefRows(byte[] pe, int count)
    {
        var reader = OpenReader(pe);
        var used = CollectUsedTypeRefRows(pe);
        var free = new List<int>();
        var total = reader.GetTableRowCount(TableIndex.TypeRef);
        for (var row = total; row >= 1 && free.Count < count; row--)
        {
            if (!used.Contains(row))
            {
                free.Add(row - 1);
            }
        }

        if (free.Count < count)
        {
            throw new InvalidOperationException(
                $"TypeRef 可回收行不足：需要 {count}，仅 {free.Count}（共 {total} 行，已引用 {used.Count} 行）");
        }

        free.Reverse();
        return free;
    }

    public static void EnsureAllMemberRefsReadable(byte[] pe)
    {
        var reader = OpenReader(pe);
        var count = reader.GetTableRowCount(TableIndex.MemberRef);
        for (var row = 1; row <= count; row++)
        {
            var handle = MetadataTokens.MemberReferenceHandle(row);
            try
            {
                var memberRef = reader.GetMemberReference(handle);
                var blob = reader.GetBlobReader(memberRef.Signature);
                _ = blob.ReadSignatureHeader();
                MarkTypeRefParent(reader, memberRef.Parent, used: null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"MemberRef row {row} (token=0x{MetadataTokens.GetToken(handle):X8}) 损坏: {ex.Message}",
                    ex);
            }
        }

        Console.WriteLine($"[VERIFY] System.Reflection.Metadata 可读 MemberRef {count} 条");
    }

    private static void MarkTypeRefParent(MetadataReader reader, EntityHandle parent, HashSet<int>? used)
    {
        if (used == null || parent.IsNil || parent.Kind != HandleKind.TypeReference)
        {
            return;
        }

        used.Add(reader.GetRowNumber((TypeReferenceHandle)parent));
    }

    public static byte[] ReadBlobHeapEntry(byte[] pe, BlobHandle handle)
    {
        var stream = MetadataStreamGaps.ListStreams(pe).First(s => s.Name == "#Blob");
        var heapIndex = MetadataTokens.GetHeapOffset(handle);
        if (heapIndex <= 0 || heapIndex >= stream.Size)
        {
            throw new InvalidOperationException($"无效 Blob 索引 0x{heapIndex:X}");
        }

        var pos = stream.FileOffset + heapIndex;
        var start = pos;
        var payloadLen = ReadCompressedUInt(pe, ref pos);
        var totalLen = pos - start + payloadLen;
        if (totalLen <= 0 || start + totalLen > stream.FileOffset + stream.Size)
        {
            throw new InvalidOperationException($"Blob 条目越界 index=0x{heapIndex:X} len=0x{totalLen:X}");
        }

        return pe.AsSpan(start, totalLen).ToArray();
    }

    public static int ReadStringHeapIndex(MetadataReader reader, StringHandle handle)
        => MetadataTokens.GetHeapOffset(handle);

    private static int GetTableDataFileOffset(byte[] pe)
    {
        var tablesStream = MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
        var tablesOff = tablesStream.FileOffset;
        var valid = BitConverter.ToUInt64(pe, tablesOff + 8);
        var presentCount = 0;
        for (var table = 0; table < 64; table++)
        {
            if (((valid >> table) & 1) != 0)
            {
                presentCount++;
            }
        }

        return tablesOff + 24 + presentCount * 4;
    }

    public static int GetTypeRefTableFileOffset(byte[] pe)
    {
        var reader = OpenReader(pe);
        var layout = GetTypeRefRowLayout(pe);
        const int anchorRow = 0x33;
        var anchorHandle = MetadataTokens.TypeReferenceHandle(anchorRow);
        var anchorNameIndex = MetadataTokens.GetHeapOffset(reader.GetTypeReference(anchorHandle).Name);
        var tableDataStart = GetTableDataFileOffset(pe);
        var tables = MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
        var end = tables.FileOffset + tables.Size - layout.RowSize;
        for (var off = tableDataStart; off <= end; off++)
        {
            var nameIndex = ReadIndex(pe, off + layout.ScopeIndexSize, layout.StringIndexSize);
            if (nameIndex != anchorNameIndex)
            {
                continue;
            }

            var tableStart = off - (anchorRow - 1) * layout.RowSize;
            if (tableStart < tableDataStart)
            {
                continue;
            }

            return tableStart;
        }

        throw new InvalidOperationException("无法在 #~ 中定位 TypeRef 表");
    }

    public static TypeRefRowLayout GetTypeRefRowLayout(byte[] pe)
    {
        var reader = OpenReader(pe);
        var tablesStream = MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
        var heapSizes = pe[tablesStream.FileOffset + 6];
        var stringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        var scopeRows = new[]
        {
            reader.GetTableRowCount(TableIndex.Module),
            reader.GetTableRowCount(TableIndex.ModuleRef),
            reader.GetTableRowCount(TableIndex.AssemblyRef),
            reader.GetTableRowCount(TableIndex.TypeRef),
        };
        var scopeIndexSize = scopeRows.Max() >= (1 << 14) ? 4 : 2;
        return new TypeRefRowLayout(scopeIndexSize, stringIndexSize);
    }

    public readonly struct TypeRefRowLayout(int scopeIndexSize, int stringIndexSize)
    {
        public int ScopeIndexSize { get; } = scopeIndexSize;
        public int StringIndexSize { get; } = stringIndexSize;
        public int RowSize => ScopeIndexSize + StringIndexSize + StringIndexSize;

        public int ReadResolutionScopeCoded(byte[] pe, int fileOffset)
            => ReadIndex(pe, fileOffset, ScopeIndexSize);

        public void WriteRow(byte[] pe, int fileOffset, int scopeCoded, int nameIndex, int namespaceIndex)
        {
            WriteIndex(pe, fileOffset, ScopeIndexSize, scopeCoded);
            WriteIndex(pe, fileOffset + ScopeIndexSize, StringIndexSize, nameIndex);
            WriteIndex(pe, fileOffset + ScopeIndexSize + StringIndexSize, StringIndexSize, namespaceIndex);
        }

        private static void WriteIndex(byte[] pe, int offset, int size, int value)
        {
            if (size == 4)
            {
                BitConverter.GetBytes(value).CopyTo(pe, offset);
                return;
            }

            BitConverter.GetBytes((ushort)value).CopyTo(pe, offset);
        }

        private static int ReadIndex(byte[] pe, int offset, int size)
            => size == 4
                ? (int)BitConverter.ToUInt32(pe, offset)
                : BitConverter.ToUInt16(pe, offset);
    }

    public static void WriteRecycledTypeRefRow(
        byte[] pe,
        int tableOffset,
        TypeRefRowLayout layout,
        int rowIndex0Based,
        int scopeCoded,
        int nameIndex,
        int namespaceIndex)
    {
        layout.WriteRow(pe, tableOffset + rowIndex0Based * layout.RowSize, scopeCoded, nameIndex, namespaceIndex);
        Console.WriteLine(
            $"[META] TypeRef 回收 row {rowIndex0Based + 1} (scope=0x{scopeCoded:X}, name=0x{nameIndex:X}, ns=0x{namespaceIndex:X})");
    }

    public static MemberRefRowLayout GetMemberRefRowLayout(byte[] pe)
    {
        var reader = OpenReader(pe);
        var tablesStream = MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
        var heapSizes = pe[tablesStream.FileOffset + 6];
        var stringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        var blobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;
        var parentRows = new[]
        {
            reader.GetTableRowCount(TableIndex.TypeDef),
            reader.GetTableRowCount(TableIndex.TypeRef),
            reader.GetTableRowCount(TableIndex.ModuleRef),
            reader.GetTableRowCount(TableIndex.MethodDef),
            reader.GetTableRowCount(TableIndex.TypeSpec),
        };
        var parentIndexSize = parentRows.Max() >= (1 << 13) ? 4 : 2;
        return new MemberRefRowLayout(parentIndexSize, stringIndexSize, blobIndexSize);
    }

    public static int GetMemberRefTableFileOffset(byte[] pe)
    {
        var reader = OpenReader(pe);
        var layout = GetMemberRefRowLayout(pe);
        const int anchorRow = 0x48;
        var anchorHandle = MetadataTokens.MemberReferenceHandle(anchorRow);
        var anchorNameIndex = MetadataTokens.GetHeapOffset(reader.GetMemberReference(anchorHandle).Name);
        var tableDataStart = GetTableDataFileOffset(pe);
        var tables = MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
        var end = tables.FileOffset + tables.Size - layout.RowSize;
        for (var off = tableDataStart; off <= end; off++)
        {
            var nameIndex = ReadIndex(pe, off + layout.ParentIndexSize, layout.StringIndexSize);
            if (nameIndex != anchorNameIndex)
            {
                continue;
            }

            var tableStart = off - (anchorRow - 1) * layout.RowSize;
            if (tableStart < tableDataStart)
            {
                continue;
            }

            return tableStart;
        }

        throw new InvalidOperationException("无法在 #~ 中定位 MemberRef 表");
    }

    public readonly struct MemberRefRowLayout(int parentIndexSize, int stringIndexSize, int blobIndexSize)
    {
        public int ParentIndexSize { get; } = parentIndexSize;
        public int StringIndexSize { get; } = stringIndexSize;
        public int BlobIndexSize { get; } = blobIndexSize;
        public int RowSize => ParentIndexSize + StringIndexSize + BlobIndexSize;

        public int ReadClassCodedIndex(byte[] pe, int fileOffset)
            => ReadIndex(pe, fileOffset, ParentIndexSize);

        public void WriteRow(byte[] pe, int fileOffset, int classCoded, int nameIndex, int sigIndex)
        {
            WriteIndex(pe, fileOffset, ParentIndexSize, classCoded);
            WriteIndex(pe, fileOffset + ParentIndexSize, StringIndexSize, nameIndex);
            WriteIndex(pe, fileOffset + ParentIndexSize + StringIndexSize, BlobIndexSize, sigIndex);
        }

        private static void WriteIndex(byte[] pe, int offset, int size, int value)
        {
            if (size == 4)
            {
                BitConverter.GetBytes(value).CopyTo(pe, offset);
                return;
            }

            BitConverter.GetBytes((ushort)value).CopyTo(pe, offset);
        }

        private static int ReadIndex(byte[] pe, int offset, int size)
            => size == 4
                ? (int)BitConverter.ToUInt32(pe, offset)
                : BitConverter.ToUInt16(pe, offset);
    }

    private static int ReadIndex(byte[] pe, int offset, int size)
        => size == 4
            ? (int)BitConverter.ToUInt32(pe, offset)
            : BitConverter.ToUInt16(pe, offset);

    private static int ReadCompressedUInt(byte[] pe, ref int pos)
    {
        var b0 = pe[pos++];
        if ((b0 & 0x80) == 0)
        {
            return b0;
        }

        if ((b0 & 0xC0) == 0x80)
        {
            return ((b0 & 0x3F) << 8) | pe[pos++];
        }

        return ((b0 & 0x1F) << 24) | (pe[pos++] << 16) | (pe[pos++] << 8) | pe[pos++];
    }
}
