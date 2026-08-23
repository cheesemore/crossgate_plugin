namespace CrossgateMod.Patcher;

/// <summary>解析 CLI #~ 元数据表，精确更新 MethodDef.RVA。</summary>
internal static class CliMetadata
{
    public static void PatchMethodDefRva(byte[] pe, uint methodDefToken, int newRva)
    {
        var row = (int)(methodDefToken & 0xFFFFFF) - 1;
        if (row < 0)
        {
            throw new InvalidOperationException($"无效 MethodDef token: 0x{methodDefToken:X8}");
        }

        var tables = MemberRefTokenLookup.GetTables(pe);
        var offset = tables.GetMethodDefRvaOffset(row);
        var oldRva = BitConverter.ToInt32(pe, offset);
        BitConverter.GetBytes(newRva).CopyTo(pe, offset);
        Console.WriteLine(
            $"[META] MethodDef 0x{methodDefToken:X8} RVA 0x{oldRva:X} -> 0x{newRva:X} (file 0x{offset:X})");
    }

    public static int GetMethodDefRvaFileOffset(byte[] pe, uint methodDefToken)
    {
        var row = (int)(methodDefToken & 0xFFFFFF) - 1;
        if (row < 0)
        {
            throw new InvalidOperationException($"无效 MethodDef token: 0x{methodDefToken:X8}");
        }

        var tables = MemberRefTokenLookup.GetTables(pe);
        return tables.GetMethodDefRvaOffset(row);
    }

    public static int ReadMethodDefRva(byte[] pe, uint methodDefToken)
    {
        var row = (int)(methodDefToken & 0xFFFFFF) - 1;
        if (row < 0)
        {
            throw new InvalidOperationException($"无效 MethodDef token: 0x{methodDefToken:X8}");
        }

        var tables = MemberRefTokenLookup.GetTables(pe);
        var offset = tables.GetMethodDefRvaOffset(row);
        return BitConverter.ToInt32(pe, offset);
    }

    public static bool TryPatchMethodDefRvaByOldRva(byte[] pe, int oldRva, int newRva, out int fileOffset)
    {
        fileOffset = -1;
        if (oldRva <= 0)
        {
            return false;
        }

        if (TryLocateAccurateMethodDefTable(pe, out var start, out var rowSize, out var rowCount))
        {
            var hits = 0;
            for (var row = 0; row < rowCount; row++)
            {
                var offset = start + row * rowSize;
                if (BitConverter.ToInt32(pe, offset) != oldRva)
                {
                    continue;
                }

                BitConverter.GetBytes(newRva).CopyTo(pe, offset);
                hits++;
                fileOffset = offset;
            }

            if (hits == 1)
            {
                Console.WriteLine(
                    $"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} (file 0x{fileOffset:X}, accurate MethodDef)");
                return true;
            }

            if (hits > 1)
            {
                return false;
            }
        }

        var tables = MemberRefTokenLookup.GetTables(pe);
        var legacyHits = 0;
        for (var row = 0; row < tables.MethodDefRowCount; row++)
        {
            var offset = tables.GetMethodDefRvaOffset(row);
            if (BitConverter.ToInt32(pe, offset) != oldRva)
            {
                continue;
            }

            BitConverter.GetBytes(newRva).CopyTo(pe, offset);
            legacyHits++;
            fileOffset = offset;
        }

        if (legacyHits == 0)
        {
            return false;
        }

        Console.WriteLine(
            $"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} ({legacyHits} row(s), file 0x{fileOffset:X}, MethodDef table)");
        return true;
    }

    /// <summary>
    /// 按 ECMA-335 计算 MethodDef 表起点。MemberRefTokenLookup.GetRowSize 对
    /// Module/TypeDef 等前表宽度不准确，会导致 MethodDef 行对不齐，追加 IL 时找不到 RVA。
    /// </summary>
    public static bool TryGetAccurateMethodDefRvaFileOffset(byte[] pe, uint methodDefToken, out int fileOffset)
    {
        fileOffset = -1;
        var row = (int)(methodDefToken & 0xFFFFFF) - 1;
        if (row < 0 || !TryLocateAccurateMethodDefTable(pe, out var start, out var rowSize, out var rowCount))
        {
            return false;
        }

        if (row >= rowCount)
        {
            return false;
        }

        fileOffset = start + row * rowSize;
        return true;
    }

    internal static bool TryLocateAccurateMethodDefTable(
        byte[] pe,
        out int dataOffset,
        out int rowSize,
        out int rowCount)
    {
        dataOffset = 0;
        rowSize = 0;
        rowCount = 0;
        try
        {
            var metaOff = MetadataStreamGaps.FindMetadataRoot(pe);
            var versionLen = BitConverter.ToInt32(pe, metaOff + 12);
            var streamCount = BitConverter.ToInt16(pe, metaOff + 18 + versionLen);
            var pos = metaOff + 20 + versionLen;
            var tablesOff = 0;
            for (var i = 0; i < streamCount; i++)
            {
                var streamOffset = BitConverter.ToInt32(pe, pos);
                var nameEnd = pos + 8;
                while (pe[nameEnd] != 0)
                {
                    nameEnd++;
                }

                var streamName = System.Text.Encoding.ASCII.GetString(pe, pos + 8, nameEnd - (pos + 8));
                if (streamName is "#~" or "#-")
                {
                    tablesOff = metaOff + streamOffset;
                    break;
                }

                var nameByteLen = streamName.Length + 1;
                pos += 8 + ((nameByteLen + 3) / 4) * 4;
            }

            if (tablesOff == 0)
            {
                return false;
            }

            var heapSizes = pe[tablesOff + 6];
            var stringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
            var guidIndexSize = (heapSizes & 0x02) != 0 ? 4 : 2;
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

            int Row(int table) => rowCounts[table];
            int Simple(int table) => Row(table) >= 0x10000 ? 4 : 2;
            int Coded(int tagBits, params int[] tables)
            {
                var max = 0;
                foreach (var t in tables)
                {
                    if (Row(t) > max)
                    {
                        max = Row(t);
                    }
                }

                return max >= (1 << (16 - tagBits)) ? 4 : 2;
            }

            var typeDefOrRef = Coded(2, 0x02, 0x01, 0x1B);
            var resolutionScope = Coded(2, 0x00, 0x1A, 0x23, 0x01);
            var tableDataOffset = countsOff;
            foreach (var table in present)
            {
                var size = table switch
                {
                    0x00 => 2 + stringIndexSize + (3 * guidIndexSize),
                    0x01 => resolutionScope + stringIndexSize + stringIndexSize,
                    0x02 => 4 + stringIndexSize + stringIndexSize + typeDefOrRef + Simple(0x04) + Simple(0x06),
                    0x03 => Simple(0x04),
                    0x04 => 2 + stringIndexSize + blobIndexSize,
                    0x05 => Simple(0x06),
                    0x06 => 4 + 2 + 2 + stringIndexSize + blobIndexSize + Simple(0x08),
                    _ => 0,
                };

                if (table == 0x06)
                {
                    if (size <= 0)
                    {
                        return false;
                    }

                    dataOffset = tableDataOffset;
                    rowSize = size;
                    rowCount = Row(0x06);
                    return rowCount > 0;
                }

                if (size <= 0)
                {
                    return false;
                }

                tableDataOffset += Row(table) * size;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
