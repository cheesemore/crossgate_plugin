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
