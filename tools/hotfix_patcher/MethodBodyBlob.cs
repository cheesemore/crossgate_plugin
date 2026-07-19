namespace CrossgateMod.Patcher;

/// <summary>读取 CLI 方法体字节长度（含 Fat EH Section）。</summary>
internal static class MethodBodyBlob
{
    public static int GetLength(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        return GetLengthAtFileOffset(pe, off);
    }

    public static int GetLengthAtFileOffset(byte[] pe, int off)
    {
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            return 1 + (flags >> 2);
        }

        if ((flags & 0x3) != 0x3)
        {
            throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ file 0x{off:X}");
        }

        var headerFlags = BitConverter.ToUInt16(pe, off);
        var headerSize = (headerFlags >> 12) * 4;
        if (headerSize < 12)
        {
            headerSize = 12;
        }

        var codeSize = BitConverter.ToInt32(pe, off + 4);
        var size = headerSize + codeSize;
        if ((headerFlags & 0x8) == 0)
        {
            return size;
        }

        while (size % 4 != 0)
        {
            size++;
        }

        var sectOff = off + size;
        while (true)
        {
            if (sectOff + 4 > pe.Length)
            {
                throw new InvalidOperationException($"方法 EH Section 越界 @ file 0x{sectOff:X}");
            }

            var kind = pe[sectOff];
            int dataSize;
            if ((kind & 0x40) != 0)
            {
                dataSize = pe[sectOff + 1] | (pe[sectOff + 2] << 8) | (pe[sectOff + 3] << 16);
            }
            else
            {
                dataSize = pe[sectOff + 1];
            }

            if (dataSize < 4)
            {
                dataSize = 4;
            }

            size = sectOff + dataSize - off;
            if ((kind & 0x80) == 0)
            {
                break;
            }

            sectOff += dataSize;
        }

        return size;
    }

    public static byte[] Read(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var len = GetLengthAtFileOffset(pe, off);
        var buf = new byte[len];
        Array.Copy(pe, off, buf, 0, len);
        return buf;
    }
}
