namespace CrossgateMod.Patcher;

internal static class UsedMemberRefScanner
{
    public static HashSet<uint> CollectUsedTokens(byte[] pe)
    {
        var used = new HashSet<uint>();
        for (var i = 0; i <= pe.Length - 5; i++)
        {
            if (pe[i] != 0x28 && pe[i] != 0x6F)
            {
                continue;
            }

            var token = BitConverter.ToUInt32(pe, i + 1);
            if ((token & 0xFF000000) == 0x0A000000)
            {
                used.Add(token);
            }
        }

        return used;
    }

    public static List<int> FindUnusedRows(byte[] pe, int count)
    {
        var layout = MemberRefTableLayout.Read(pe);
        var used = CollectUsedTokens(pe);
        var free = new List<int>();
        for (var row = layout.RowCount; row >= 1 && free.Count < count; row--)
        {
            var token = 0x0A000000u | (uint)row;
            if (!used.Contains(token))
            {
                free.Add(row - 1);
            }
        }

        if (free.Count < count)
        {
            throw new InvalidOperationException(
                $"MemberRef 空闲行不足：需要 {count}，仅找到 {free.Count}");
        }

        free.Reverse();
        return free;
    }
}

internal sealed class MemberRefTableLayout
{
    public int RowCount { get; private init; }
    public int RowSize { get; private init; }
    public int DataFileOffset { get; private init; }

    public static MemberRefTableLayout Read(byte[] pe)
    {
        var (dataOff, rowSize, rowCount) = MemberRefTokenLookup.GetMemberRefTable(pe);
        return new MemberRefTableLayout
        {
            RowCount = rowCount,
            RowSize = rowSize,
            DataFileOffset = dataOff,
        };
    }

    public void WriteRow(byte[] pe, int rowIndex, int classCoded, int nameIndex, int sigIndex)
    {
        var off = DataFileOffset + rowIndex * RowSize;
        if (RowSize == 6)
        {
            BitConverter.GetBytes((ushort)classCoded).CopyTo(pe, off);
            BitConverter.GetBytes((ushort)nameIndex).CopyTo(pe, off + 2);
            BitConverter.GetBytes((ushort)sigIndex).CopyTo(pe, off + 4);
            return;
        }

        BitConverter.GetBytes(classCoded).CopyTo(pe, off);
        BitConverter.GetBytes(nameIndex).CopyTo(pe, off + 4);
        BitConverter.GetBytes(sigIndex).CopyTo(pe, off + 8);
    }
}
