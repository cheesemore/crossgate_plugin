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

        var tables = MemberRefTokenLookup.GetTables(pe);
        var hits = 0;
        for (var row = 0; row < tables.MethodDefRowCount; row++)
        {
            var offset = tables.GetMethodDefRvaOffset(row);
            if (BitConverter.ToInt32(pe, offset) != oldRva)
            {
                continue;
            }

            BitConverter.GetBytes(newRva).CopyTo(pe, offset);
            hits++;
            fileOffset = offset;
        }

        if (hits == 0)
        {
            return false;
        }

        Console.WriteLine(
            $"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} ({hits} row(s), file 0x{fileOffset:X}, MethodDef table)");
        return true;
    }
}
