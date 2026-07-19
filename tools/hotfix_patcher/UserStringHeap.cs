using System.Text;

namespace CrossgateMod.Patcher;

internal sealed class UserStringHeap
{
    private readonly byte[] _pe;
    private readonly int _heapOffset;
    private readonly int _heapSize;
    private readonly Dictionary<string, uint> _stringToToken = new(StringComparer.Ordinal);
    private readonly List<HeapEntry> _entries = new();
    private int _appendCursor;

    private UserStringHeap(byte[] pe, int heapOffset, int heapSize)
    {
        _pe = pe;
        _heapOffset = heapOffset;
        _heapSize = heapSize;
        IndexHeap();
    }

    public static UserStringHeap FromPe(byte[] pe)
    {
        var meta = FindMetadataOffset(pe);
        var (offset, size) = FindStream(pe, meta, "#US");
        return new UserStringHeap(pe, offset, size);
    }

    public bool HasString(string value) => _stringToToken.ContainsKey(value);

    public uint GetOrReuseToken(string value) => AppendToken(value);

    public uint AppendToken(string value)
    {
        if (_stringToToken.TryGetValue(value, out var token))
        {
            return token;
        }

        var utf16 = Encoding.Unicode.GetBytes(value);
        if (utf16.Length % 2 != 0)
        {
            throw new InvalidOperationException($"用户字符串 UTF-16 字节长度必须为偶数: {value}");
        }

        var entrySize = GetEntrySize(utf16.Length);
        var offset = _appendCursor;
        if (offset + entrySize > _heapSize)
        {
            throw new InvalidOperationException(
                $"#US 堆余量不足：需要 {entrySize} 字节，剩余 {_heapSize - offset} 字节");
        }

        WriteEntryAt(offset, utf16);
        _appendCursor = offset + entrySize;
        token = 0x70000000u | (uint)offset;
        _stringToToken[value] = token;
        Console.WriteLine($"[US] 追加 token=0x{token:X8} offset=0x{offset:X} \"{value}\"");
        return token;
    }

    private void WriteEntryAt(int offset, byte[] utf16)
    {
        var abs = _heapOffset + offset;
        WriteCompressed(ref abs, utf16.Length);
        Array.Copy(utf16, 0, _pe, abs, utf16.Length);
        abs += utf16.Length;
        if (utf16.Length % 2 != 0)
        {
            _pe[abs] = 0;
        }
    }

    private void IndexHeap()
    {
        var offset = 0;
        while (offset < _heapSize)
        {
            if (!TryReadEntry(offset, out var utf16Len, out var nextOffset, out var utf16))
            {
                break;
            }

            if (utf16Len > 0)
            {
                var value = Encoding.Unicode.GetString(utf16);
                var token = 0x70000000u | (uint)offset;
                _stringToToken.TryAdd(value, token);
                _entries.Add(new HeapEntry(offset, utf16Len, nextOffset - offset, token));
            }

            offset = nextOffset;
        }

        _appendCursor = offset;
        Console.WriteLine($"[US] 已索引 {_stringToToken.Count} 条用户字符串，追加起点=0x{_appendCursor:X}");
    }

    public void SealHeapTail()
    {
        if (_appendCursor >= _heapSize)
        {
            return;
        }

        Array.Clear(_pe, _heapOffset + _appendCursor, _heapSize - _appendCursor);
        Console.WriteLine($"[US] 封存堆尾 0x{_appendCursor:X}..0x{_heapSize:X}");
    }

    private static int GetEntrySize(int utf16ByteLen)
    {
        return GetCompressedSize(utf16ByteLen) + utf16ByteLen + (utf16ByteLen % 2);
    }

    private static int GetCompressedSize(int value)
    {
        if (value < 0x80)
        {
            return 1;
        }

        if (value < 0x4000)
        {
            return 2;
        }

        return 4;
    }

    private void WriteCompressed(ref int abs, int value)
    {
        if (value < 0x80)
        {
            _pe[abs++] = (byte)value;
            return;
        }

        if (value < 0x4000)
        {
            _pe[abs] = (byte)(0x80 | (value >> 8));
            _pe[abs + 1] = (byte)value;
            abs += 2;
            return;
        }

        _pe[abs] = (byte)(0xC0 | (value >> 24));
        _pe[abs + 1] = (byte)(value >> 16);
        _pe[abs + 2] = (byte)(value >> 8);
        _pe[abs + 3] = (byte)value;
        abs += 4;
    }

    private bool TryReadEntry(int offset, out int utf16ByteLen, out int nextOffset, out byte[] utf16)
    {
        utf16ByteLen = 0;
        nextOffset = offset + 1;
        utf16 = Array.Empty<byte>();

        if (offset >= _heapSize)
        {
            return false;
        }

        var pos = _heapOffset + offset;
        if (!TryReadCompressed(pos, out utf16ByteLen, out var dataPos))
        {
            return false;
        }

        if (utf16ByteLen == 0)
        {
            return true;
        }

        if (dataPos + utf16ByteLen > _heapOffset + _heapSize)
        {
            return false;
        }

        utf16 = new byte[utf16ByteLen];
        Array.Copy(_pe, dataPos, utf16, 0, utf16ByteLen);
        nextOffset = dataPos + utf16ByteLen - _heapOffset;
        if (utf16ByteLen % 2 != 0)
        {
            nextOffset++;
        }

        return true;
    }

    private bool TryReadCompressed(int abs, out int value, out int dataPos)
    {
        value = 0;
        dataPos = abs + 1;
        if (abs >= _pe.Length)
        {
            return false;
        }

        var b0 = _pe[abs];
        if (b0 < 0x80)
        {
            value = b0;
            return true;
        }

        if ((b0 & 0xC0) == 0x80)
        {
            if (abs + 1 >= _pe.Length)
            {
                return false;
            }

            value = ((b0 & 0x3F) << 8) | _pe[abs + 1];
            dataPos = abs + 2;
            return true;
        }

        if ((b0 & 0xE0) == 0xC0)
        {
            if (abs + 3 >= _pe.Length)
            {
                return false;
            }

            value = ((b0 & 0x1F) << 24)
                | (_pe[abs + 1] << 16)
                | (_pe[abs + 2] << 8)
                | _pe[abs + 3];
            dataPos = abs + 4;
            return true;
        }

        return false;
    }

    private static int FindMetadataOffset(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var magic = BitConverter.ToUInt16(pe, peOff + 24);
        var dataDirBase = magic == 0x10B ? peOff + 24 + 96 : peOff + 24 + 112;
        var cliRva = BitConverter.ToInt32(pe, dataDirBase + 14 * 8);
        var cliOffset = PeLayout.RvaToOffset(pe, cliRva);
        var metaRva = BitConverter.ToInt32(pe, cliOffset + 8);
        return PeLayout.RvaToOffset(pe, metaRva);
    }

    private static (int offset, int size) FindStream(byte[] pe, int metaOffset, string name)
    {
        var versionLen = BitConverter.ToInt32(pe, metaOffset + 12);
        var aligned = 16 + versionLen + (4 - versionLen % 4) % 4;
        var streamCount = BitConverter.ToUInt16(pe, metaOffset + aligned + 2);
        var cursor = metaOffset + aligned + 4;

        for (var i = 0; i < streamCount; i++)
        {
            var streamOffset = BitConverter.ToInt32(pe, cursor);
            var streamSize = BitConverter.ToInt32(pe, cursor + 4);
            var nameStart = cursor + 8;
            var nameEnd = nameStart;
            while (pe[nameEnd] != 0)
            {
                nameEnd++;
            }

            var streamName = Encoding.ASCII.GetString(pe, nameStart, nameEnd - nameStart);
            if (streamName == name)
            {
                return (metaOffset + streamOffset, streamSize);
            }

            cursor = (nameEnd + 1 + 3) & ~3;
        }

        throw new InvalidOperationException($"未找到元数据流 {name}");
    }

    private sealed record HeapEntry(int Offset, int Utf16Bytes, int SlotBytes, uint Token);
}
