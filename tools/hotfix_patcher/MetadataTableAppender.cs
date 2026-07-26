namespace CrossgateMod.Patcher;

internal static class MetadataTableAppender
{
    internal static int TildeInsertShift { get; private set; }

    internal static void ResetTildeInsertShift() => TildeInsertShift = 0;

    public static int AppendTypeRefRow(byte[] pe, int scopeCoded, int nameIndex, int namespaceIndex)
    {
        var tables = MemberRefTokenLookup.GetTables(pe);
        var rowSize = tables.TypeRefRowSize;
        var insertAt = tables.TypeRefDataOffset + tables.TypeRefRowCount * rowSize;
        var tilde = FindTildeStream(pe);
        var tildeEnd = tilde.FileOffset + tilde.Size;
        EnsureGapBeforeStream(pe, tildeEnd, rowSize);
        ShiftBytes(pe, insertAt, rowSize, tildeEnd);
        TildeInsertShift += rowSize;
        GrowStreamSize(pe, tilde.HeaderPos, rowSize);
        WriteTypeRefRow(pe, tables.TypeRefRowCount, scopeCoded, nameIndex, namespaceIndex, tables);
        IncrementRowCount(pe, 0x01);
        var newRow = tables.TypeRefRowCount;
        Console.WriteLine($"[META] TypeRef 追加 row {newRow + 1} (scope=0x{scopeCoded:X}, name=0x{nameIndex:X}, ns=0x{namespaceIndex:X})");
        return newRow;
    }

    /// <summary>
    /// 追加 MethodSpec 行。Method 列为 MethodDefOrRef coded index；Instantiation 为 #Blob 索引。
    /// 返回 0-based 新行号（token = 0x2B000000 | (row+1)）。
    /// </summary>
    public static int AppendMethodSpecRow(byte[] pe, int methodCoded, int instantiationBlobIndex)
    {
        var layout = LocateMethodSpecTable(pe);
        var rowSize = layout.RowSize;
        var insertAt = layout.DataOffset + layout.RowCount * rowSize;
        var tilde = FindTildeStream(pe);
        var tildeEnd = tilde.FileOffset + tilde.Size;
        EnsureGapBeforeStream(pe, tildeEnd, rowSize);
        layout = LocateMethodSpecTable(pe);
        insertAt = layout.DataOffset + layout.RowCount * rowSize;
        tilde = FindTildeStream(pe);
        tildeEnd = tilde.FileOffset + tilde.Size;
        ShiftBytes(pe, insertAt, rowSize, tildeEnd);
        TildeInsertShift += rowSize;
        GrowStreamSize(pe, tilde.HeaderPos, rowSize);
        WriteMethodSpecRow(pe, insertAt, methodCoded, instantiationBlobIndex, layout);
        IncrementRowCount(pe, 0x2B);
        var newRow = layout.RowCount;
        Console.WriteLine(
            $"[META] MethodSpec 追加 row {newRow + 1} (method=0x{methodCoded:X}, instBlob=0x{instantiationBlobIndex:X})");
        return newRow;
    }

    private static void WriteMethodSpecRow(
        byte[] pe,
        int offset,
        int methodCoded,
        int instantiationBlobIndex,
        MethodSpecTableLayout layout)
    {
        if (layout.MethodIndexSize == 4)
        {
            BitConverter.GetBytes(methodCoded).CopyTo(pe, offset);
        }
        else
        {
            BitConverter.GetBytes((ushort)methodCoded).CopyTo(pe, offset);
        }

        var blobOff = offset + layout.MethodIndexSize;
        if (layout.BlobIndexSize == 4)
        {
            BitConverter.GetBytes(instantiationBlobIndex).CopyTo(pe, blobOff);
        }
        else
        {
            BitConverter.GetBytes((ushort)instantiationBlobIndex).CopyTo(pe, blobOff);
        }
    }

    private readonly struct MethodSpecTableLayout(
        int dataOffset,
        int rowCount,
        int rowSize,
        int methodIndexSize,
        int blobIndexSize)
    {
        public int DataOffset { get; } = dataOffset;
        public int RowCount { get; } = rowCount;
        public int RowSize { get; } = rowSize;
        public int MethodIndexSize { get; } = methodIndexSize;
        public int BlobIndexSize { get; } = blobIndexSize;
    }

    /// <summary>
    /// MethodSpec.Method 是 MethodDefOrRef（1 bit tag），不是 TypeDefOrRef。
    /// MemberRefTokenLookup.GetRowSize(0x2B) 用了 TypeDefOrRef 宽度，这里单独按正确宽度定位。
    /// </summary>
    private static MethodSpecTableLayout LocateMethodSpecTable(byte[] pe)
    {
        var tilde = FindTildeStream(pe);
        var tablesOff = tilde.FileOffset;
        var heapSizes = pe[tablesOff + 6];
        var stringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
        var blobIndexSize = (heapSizes & 0x04) != 0 ? 4 : 2;
        var valid = BitConverter.ToUInt64(pe, tablesOff + 8);
        var countsOff = tablesOff + 24;
        var rowCounts = new int[64];
        var present = new List<int>();
        for (var table = 0; table < 64; table++)
        {
            if (((valid >> table) & 1) == 0)
            {
                continue;
            }

            present.Add(table);
            rowCounts[table] = BitConverter.ToInt32(pe, countsOff);
            countsOff += 4;
        }

        int Row(int t) => rowCounts[t];
        var typeDefOrRefSize = CodedIndexSize(2, Row(0x02), Row(0x01), Row(0x1B));
        var memberRefParentSize = CodedIndexSize(3, Row(0x02), Row(0x01), Row(0x1A), Row(0x06), Row(0x1B));
        var methodDefOrRefSize = CodedIndexSize(1, Row(0x06), Row(0x0A));
        var dataOffset = countsOff;
        foreach (var table in present)
        {
            var rowSize = table == 0x2B
                ? methodDefOrRefSize + blobIndexSize
                : MemberRefTokenLookupRowSize(table, stringIndexSize, blobIndexSize, typeDefOrRefSize, memberRefParentSize);
            if (table == 0x2B)
            {
                return new MethodSpecTableLayout(
                    dataOffset,
                    Row(0x2B),
                    rowSize,
                    methodDefOrRefSize,
                    blobIndexSize);
            }

            dataOffset += Row(table) * rowSize;
        }

        throw new InvalidOperationException("未找到 MethodSpec 表 (0x2B)");
    }

    private static int CodedIndexSize(int tagBits, params int[] rowCounts)
    {
        var max = rowCounts.DefaultIfEmpty(0).Max();
        return max >= (1 << (16 - tagBits)) ? 4 : 2;
    }

    private static int MemberRefTokenLookupRowSize(
        int table,
        int stringIndexSize,
        int blobIndexSize,
        int typeDefOrRef,
        int memberRefParent)
    {
        // 与 MemberRefTokenLookup.CliTablesReader.GetRowSize 保持一致（MethodSpec 除外，由调用方处理）
        var coded2 = typeDefOrRef;
        var coded3 = memberRefParent;
        return table switch
        {
            0x00 => 2 + stringIndexSize + 16 + blobIndexSize,
            0x01 => coded2 + stringIndexSize + stringIndexSize,
            0x02 => 4 + coded2 + coded2 + stringIndexSize + coded2,
            0x04 => 2 + stringIndexSize + blobIndexSize,
            0x06 => 4 + 2 + 2 + stringIndexSize + blobIndexSize + coded2,
            0x08 => 2 + coded2 + stringIndexSize + blobIndexSize,
            0x09 => coded3 + 2,
            0x0A => coded3 + stringIndexSize + blobIndexSize,
            0x0B => 2 + coded2 + stringIndexSize,
            0x0C => 2 + coded2 + stringIndexSize,
            0x0D => 2 + coded2,
            0x0E => coded2 + coded2,
            0x0F => 2 + coded2 + coded2,
            0x10 => coded2,
            0x11 => 2 + stringIndexSize + blobIndexSize,
            0x12 => coded2 + coded2,
            0x14 => 2 + stringIndexSize + coded2,
            0x15 => coded2 + coded2,
            0x17 => 2 + stringIndexSize + blobIndexSize + coded2,
            0x18 => coded2 + coded2,
            0x19 => coded2 + coded2 + coded2,
            0x1A => coded2,
            0x1B => coded2 + stringIndexSize + blobIndexSize,
            0x1C => 4 + 2 + coded2 + coded2,
            0x1D => coded2 + blobIndexSize,
            0x20 => 4 + 2 + 2 + 4 + 4 + 4,
            0x21 => 4 + 2 + 2 + blobIndexSize,
            0x22 => 4 + 2 + 2 + blobIndexSize,
            0x23 => 4 + 2 + 2 + blobIndexSize,
            0x24 => 4 + 2 + 2 + blobIndexSize,
            0x25 => 4 + 2 + 2 + blobIndexSize,
            0x26 => 2 + blobIndexSize,
            0x27 => coded2 + coded2 + stringIndexSize + blobIndexSize,
            0x28 => 4 + 2 + stringIndexSize,
            0x29 => 4 + coded2 + coded2,
            0x2A => coded2 + coded2,
            0x2B => coded2 + blobIndexSize,
            0x2C => coded2 + 2,
            _ => throw new InvalidOperationException($"未支持的元数据表 0x{table:X2}"),
        };
    }

    private static void WriteTypeRefRow(
        byte[] pe,
        int rowIndex,
        int scopeCoded,
        int nameIndex,
        int namespaceIndex,
        MemberRefTokenLookup.CliTablesReader tables)
    {
        var off = tables.TypeRefDataOffset + rowIndex * tables.TypeRefRowSize;
        var scopeSize = tables.TypeDefOrRefIndexSize;
        var strSize = tables.StringIndexSize;
        if (scopeSize == 4)
        {
            BitConverter.GetBytes(scopeCoded).CopyTo(pe, off);
        }
        else
        {
            BitConverter.GetBytes((ushort)scopeCoded).CopyTo(pe, off);
        }

        if (strSize == 4)
        {
            BitConverter.GetBytes(nameIndex).CopyTo(pe, off + scopeSize);
            BitConverter.GetBytes(namespaceIndex).CopyTo(pe, off + scopeSize + 4);
        }
        else
        {
            BitConverter.GetBytes((ushort)nameIndex).CopyTo(pe, off + scopeSize);
            BitConverter.GetBytes((ushort)namespaceIndex).CopyTo(pe, off + scopeSize + 2);
        }
    }

    private static MetadataStreamGaps.StreamInfo FindTildeStream(byte[] pe)
    {
        return MetadataStreamGaps.ListStreams(pe).First(s => s.Name is "#~" or "#-");
    }

    private static void EnsureGapBeforeStream(byte[] pe, int streamEnd, int need)
    {
        var streams = MetadataStreamGaps.ListStreams(pe);
        streams.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
        var idx = streams.FindIndex(s => s.FileOffset + s.Size == streamEnd);
        if (idx < 0)
        {
            for (var i = 0; i < streams.Count - 1; i++)
            {
                if (streams[i].FileOffset + streams[i].Size <= streamEnd
                    && streams[i + 1].FileOffset >= streamEnd)
                {
                    idx = i;
                    break;
                }
            }
        }

        var nextStart = idx >= 0 && idx + 1 < streams.Count
            ? streams[idx + 1].FileOffset
            : MetadataStreamGaps.TextTailSlack(pe) + streamEnd;
        var gap = nextStart - streamEnd;
        if (gap >= need)
        {
            return;
        }

        var shift = need - gap;
        var metaEnd = streams.Max(s => s.FileOffset + s.Size);
        MetadataStreamGaps.ShiftRight(pe, nextStart, shift, metaEnd);
    }

    private static void ShiftBytes(byte[] pe, int start, int bytes, int end)
    {
        for (var i = end - 1; i >= start; i--)
        {
            pe[i + bytes] = pe[i];
        }
    }

    private static void GrowStreamSize(byte[] pe, int headerPos, int bytes)
    {
        var size = BitConverter.ToInt32(pe, headerPos + 4);
        BitConverter.GetBytes(size + bytes).CopyTo(pe, headerPos + 4);
    }

    private static void IncrementRowCount(byte[] pe, int tableIndex)
    {
        var metaRoot = MetadataStreamGaps.FindMetadataRoot(pe);
        var versionLen = BitConverter.ToInt32(pe, metaRoot + 12);
        var streamCount = BitConverter.ToInt16(pe, metaRoot + 18 + versionLen);
        var pos = metaRoot + 20 + versionLen;
        var tablesOff = 0;
        for (var i = 0; i < streamCount; i++)
        {
            var streamOffset = BitConverter.ToInt32(pe, pos);
            var streamName = ReadStreamName(pe, pos + 8);
            if (streamName is "#~" or "#-")
            {
                tablesOff = metaRoot + streamOffset;
                break;
            }

            var nameByteLen = System.Text.Encoding.ASCII.GetByteCount(streamName) + 1;
            pos += 8 + ((nameByteLen + 3) / 4) * 4;
        }

        var valid = BitConverter.ToUInt64(pe, tablesOff + 8);
        var offset = tablesOff + 24;
        for (var table = 0; table < 64; table++)
        {
            if (((valid >> table) & 1) == 0)
            {
                continue;
            }

            if (table == tableIndex)
            {
                var count = BitConverter.ToInt32(pe, offset);
                BitConverter.GetBytes(count + 1).CopyTo(pe, offset);
                return;
            }

            offset += 4;
        }

        throw new InvalidOperationException($"未找到元数据表 0x{tableIndex:X2} 行计数");
    }

    private static string ReadStreamName(byte[] pe, int offset)
    {
        var end = offset;
        while (pe[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(pe, offset, end - offset);
    }
}
