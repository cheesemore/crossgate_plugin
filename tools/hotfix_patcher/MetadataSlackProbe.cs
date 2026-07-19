namespace CrossgateMod.Patcher;

internal static class MetadataSlackProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: HotfixPatcher metadata-slack <hotfix>");
            return 1;
        }

        var pe = File.ReadAllBytes(Path.GetFullPath(args[0]));
        var metaOff = FindMetadataOffset(pe);
        var versionLen = BitConverter.ToInt32(pe, metaOff + 12);
        var streamCount = BitConverter.ToInt16(pe, metaOff + 18 + versionLen);
        var pos = metaOff + 20 + versionLen;
        for (var i = 0; i < streamCount; i++)
        {
            var streamOffset = BitConverter.ToInt32(pe, pos);
            var streamSize = BitConverter.ToInt32(pe, pos + 4);
            var name = ReadStreamName(pe, pos + 8);
            var used = MeasureStreamUsed(pe, metaOff + streamOffset, streamSize, name);
            Console.WriteLine(
                $"{name,-12} size=0x{streamSize:X5} used~0x{used:X5} slack~0x{streamSize - used:X5}");
            var nameByteLen = System.Text.Encoding.ASCII.GetByteCount(name) + 1;
            pos += 8 + ((nameByteLen + 3) / 4) * 4;
        }

        return 0;
    }

    private static int MeasureStreamUsed(byte[] pe, int offset, int size, string name)
    {
        if (name == "#Strings")
        {
            var end = offset + size - 1;
            while (end > offset && pe[end] == 0)
            {
                end--;
            }

            return end - offset + 1;
        }

        if (name is "#~" or "#-")
        {
            return size;
        }

        return size;
    }

    private static int FindMetadataOffset(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var magic = BitConverter.ToUInt16(pe, peOff + 24);
        var dataDirOff = peOff + 24 + (magic == 0x20B ? 112 : 96);
        var cliRva = BitConverter.ToInt32(pe, dataDirOff + 14 * 8);
        var cliOff = PeLayout.RvaToOffset(pe, cliRva);
        var metaRva = BitConverter.ToInt32(pe, cliOff + 8);
        return PeLayout.RvaToOffset(pe, metaRva);
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
