namespace CrossgateMod.Patcher;

internal static class MetadataStreamGaps
{
    public sealed class StreamInfo
    {
        public required string Name { get; init; }
        public required int HeaderPos { get; init; }
        public int MetaOffset { get; set; }
        public int FileOffset { get; set; }
        public int Size { get; set; }
    }

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: HotfixPatcher metadata-gaps <hotfix>");
            return 1;
        }

        var pe = File.ReadAllBytes(Path.GetFullPath(args[0]));
        var streams = ListStreams(pe);
        streams.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
        for (var i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var end = s.FileOffset + s.Size;
            var gap = i + 1 < streams.Count ? streams[i + 1].FileOffset - end : TextTailSlack(pe);
            Console.WriteLine(
                $"{s.Name,-10} file=0x{s.FileOffset:X5}..0x{end:X5} size=0x{s.Size:X5} tailGap=0x{gap:X5}");
        }

        return 0;
    }

    public static int FindMetadataRoot(byte[] pe) => FindMetadataOffset(pe);

    public static List<StreamInfo> ListStreams(byte[] pe)
    {
        var metaRoot = FindMetadataOffset(pe);
        var versionLen = BitConverter.ToInt32(pe, metaRoot + 12);
        var streamCount = BitConverter.ToInt16(pe, metaRoot + 18 + versionLen);
        var pos = metaRoot + 20 + versionLen;
        var streams = new List<StreamInfo>();
        for (var i = 0; i < streamCount; i++)
        {
            var streamOffset = BitConverter.ToInt32(pe, pos);
            var streamSize = BitConverter.ToInt32(pe, pos + 4);
            var name = ReadStreamName(pe, pos + 8);
            streams.Add(new StreamInfo
            {
                Name = name,
                HeaderPos = pos,
                MetaOffset = streamOffset,
                FileOffset = metaRoot + streamOffset,
                Size = streamSize,
            });
            var nameByteLen = System.Text.Encoding.ASCII.GetByteCount(name) + 1;
            pos += 8 + ((nameByteLen + 3) / 4) * 4;
        }

        return streams;
    }

    public static int TextTailSlack(byte[] pe)
    {
        var blob = ListStreams(pe).First(s => s.Name == "#Blob");
        var blobEnd = blob.FileOffset + blob.Size;
        var text = PeLayout.GetSection(pe, ".text");
        var textUsedEnd = text.PointerToRawData + text.VirtualSize;
        return (int)(textUsedEnd - blobEnd);
    }

    /// <summary>在 #Strings 末尾追加；必要时右移 #US/#GUID/#Blob。</summary>
    public static int EnsureString(byte[] pe, string value)
    {
        var existing = FindStringIndex(pe, value);
        if (existing >= 0)
        {
            return existing;
        }

        var payload = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        var strings = ListStreams(pe).First(s => s.Name == "#Strings");
        var us = ListStreams(pe).First(s => s.Name == "#US");
        var gap = us.FileOffset - (strings.FileOffset + strings.Size);
        if (gap < payload.Length)
        {
            var need = payload.Length - gap;
            ShiftRight(pe, us.FileOffset, need, strings.FileOffset + strings.Size);
            us = ListStreams(pe).First(s => s.Name == "#US");
        }

        strings = ListStreams(pe).First(s => s.Name == "#Strings");
        var index = strings.Size;
        payload.CopyTo(pe.AsSpan(strings.FileOffset + strings.Size));
        strings.Size += payload.Length;
        BitConverter.GetBytes(strings.Size).CopyTo(pe, strings.HeaderPos + 4);
        return index;
    }

    public static int EnsureBlob(byte[] pe, ReadOnlySpan<byte> blobData)
    {
        var blob = ListStreams(pe).First(s => s.Name == "#Blob");
        var slack = TextTailSlack(pe);
        if (slack < blobData.Length)
        {
            throw new InvalidOperationException(
                $"#Blob 尾部 slack 0x{slack:X} 不足追加 0x{blobData.Length:X} 字节");
        }

        var index = blob.Size;
        blobData.CopyTo(pe.AsSpan(blob.FileOffset + blob.Size));
        blob.Size += blobData.Length;
        BitConverter.GetBytes(blob.Size).CopyTo(pe, blob.HeaderPos + 4);
        return index;
    }

    public static int FindStringIndex(byte[] pe, string value)
    {
        var strings = ListStreams(pe).First(s => s.Name == "#Strings");
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        for (var j = 1; j < strings.Size - bytes.Length; j++)
        {
            var ok = true;
            for (var k = 0; k < bytes.Length; k++)
            {
                if (pe[strings.FileOffset + j + k] != bytes[k])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return j;
            }
        }

        return -1;
    }

    internal static void ShiftRight(byte[] pe, int start, int bytes, int end)
    {
        if (bytes <= 0)
        {
            return;
        }

        for (var i = end - 1; i >= start; i--)
        {
            pe[i + bytes] = pe[i];
        }

        Array.Clear(pe, start, bytes);
        var streams = ListStreams(pe);
        foreach (var s in streams)
        {
            if (s.FileOffset >= start)
            {
                s.MetaOffset += bytes;
                BitConverter.GetBytes(s.MetaOffset).CopyTo(pe, s.HeaderPos);
                s.FileOffset += bytes;
            }
        }
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
