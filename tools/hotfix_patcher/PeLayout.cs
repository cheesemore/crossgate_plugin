namespace CrossgateMod.Patcher;

internal static class PeLayout
{
    public static PeSection GetSection(byte[] pe, string name)
    {
        return ParseSections(pe).First(s => s.Name == name);
    }

    public static int RvaToOffset(byte[] pe, int rva)
    {
        foreach (var sec in ParseSections(pe))
        {
            if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + sec.VirtualSize)
            {
                return (int)(rva - sec.VirtualAddress + sec.PointerToRawData);
            }
        }

        throw new InvalidOperationException($"RVA 0x{rva:X} 无法映射");
    }

    public static int OffsetToRva(byte[] pe, int offset)
    {
        foreach (var sec in ParseSections(pe))
        {
            if (offset >= sec.PointerToRawData && offset < sec.PointerToRawData + sec.SizeOfRawData)
            {
                return (int)(offset - sec.PointerToRawData + sec.VirtualAddress);
            }
        }

        throw new InvalidOperationException($"offset 0x{offset:X} 无法映射");
    }

    public static void SetSectionRawSize(byte[] pe, string name, uint rawSize)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            if (secName != name)
            {
                continue;
            }

            BitConverter.GetBytes(rawSize).CopyTo(pe, off + 16);
            return;
        }

        throw new InvalidOperationException($"未找到节区 {name}");
    }

    public static void SetSectionPointerToRawData(byte[] pe, string name, uint pointer)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            if (secName != name)
            {
                continue;
            }

            BitConverter.GetBytes(pointer).CopyTo(pe, off + 20);
            return;
        }

        throw new InvalidOperationException($"未找到节区 {name}");
    }

    public static void SetSectionVirtualSize(byte[] pe, string name, uint virtualSize)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            if (secName != name)
            {
                continue;
            }

            var rawSize = BitConverter.ToUInt32(pe, off + 16);
            if (virtualSize > rawSize)
            {
                throw new InvalidOperationException(
                    $"节区 {name} VirtualSize 0x{virtualSize:X} 超过 SizeOfRawData 0x{rawSize:X}");
            }

            BitConverter.GetBytes(virtualSize).CopyTo(pe, off + 8);
            return;
        }

        throw new InvalidOperationException($"未找到节区 {name}");
    }

    public static void SetSectionVirtualAddress(byte[] pe, string name, uint virtualAddress)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            if (secName != name)
            {
                continue;
            }

            BitConverter.GetBytes(virtualAddress).CopyTo(pe, off + 12);
            return;
        }

        throw new InvalidOperationException($"未找到节区 {name}");
    }

    public static uint GetSectionAlignment(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        // OptionalHeader.SectionAlignment @ +32 for both PE32 and PE32+
        return BitConverter.ToUInt32(pe, peOff + 24 + 32);
    }

    public static uint GetSizeOfImage(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        return BitConverter.ToUInt32(pe, peOff + 24 + 56);
    }

    public static void SetSizeOfImage(byte[] pe, uint sizeOfImage)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        BitConverter.GetBytes(sizeOfImage).CopyTo(pe, peOff + 24 + 56);
    }

    public static uint AlignUp(uint value, uint align)
    {
        if (align == 0)
        {
            return value;
        }

        return (value + align - 1) / align * align;
    }

    /// <summary>
    /// .text VirtualSize 到下一节 VirtualAddress 之间的可用虚拟间隙（HybridCLR 可执行追加上限）。
    /// </summary>
    public static int GetTextVaGapBytes(byte[] pe)
    {
        var sections = ParseSections(pe).OrderBy(s => s.VirtualAddress).ToList();
        var text = sections.FirstOrDefault(s => s.Name == ".text")
            ?? throw new InvalidOperationException("缺少 .text");
        var textEnd = text.VirtualAddress + text.VirtualSize;
        var next = sections.FirstOrDefault(s => s.VirtualAddress > text.VirtualAddress);
        if (next == null)
        {
            return (int)Math.Max(0, text.SizeOfRawData - text.VirtualSize);
        }

        return (int)Math.Max(0, next.VirtualAddress - textEnd);
    }

    /// <summary>
    /// .text VirtualSize 扩大后不得越过后续节 VirtualAddress。
    /// 禁止后移节区 VA：OptionalHeader DataDirectory（资源/.reloc）不会同步，HybridCLR 会启动未响应。
    /// </summary>
    public static void EnsureNoVirtualOverlapAfterText(byte[] pe)
    {
        var sections = ParseSections(pe).OrderBy(s => s.VirtualAddress).ToList();
        var text = sections.FirstOrDefault(s => s.Name == ".text")
            ?? throw new InvalidOperationException("缺少 .text");
        var textEnd = text.VirtualAddress + text.VirtualSize;
        var following = sections.Where(s => s.VirtualAddress > text.VirtualAddress).ToList();
        if (following.Count == 0)
        {
            RecalcSizeOfImage(pe);
            return;
        }

        var firstFollowing = following[0];
        if (textEnd <= firstFollowing.VirtualAddress)
        {
            RecalcSizeOfImage(pe);
            return;
        }

        throw new InvalidOperationException(
            $".text 虚拟尾 0x{textEnd:X} 侵入 {firstFollowing.Name} VA 0x{firstFollowing.VirtualAddress:X}；" +
            "禁止后移节区 VirtualAddress（DataDirectory 不同步会导致启动未响应）。" +
            "请原地扩写，或把紧随的方法体迁入 VA 间隙后再扩写。");
    }

    public static void RecalcSizeOfImage(byte[] pe)
    {
        var align = GetSectionAlignment(pe);
        var maxEnd = 0u;
        foreach (var sec in ParseSections(pe))
        {
            var end = sec.VirtualAddress + Math.Max(sec.VirtualSize, sec.SizeOfRawData);
            if (end > maxEnd)
            {
                maxEnd = end;
            }
        }

        var sizeOfImage = AlignUp(maxEnd, align);
        var old = GetSizeOfImage(pe);
        if (sizeOfImage != old)
        {
            SetSizeOfImage(pe, sizeOfImage);
            Console.WriteLine($"[PE] SizeOfImage 0x{old:X} -> 0x{sizeOfImage:X}");
        }
    }

    public static List<PeSection> ParseSections(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        var list = new List<PeSection>();
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            list.Add(new PeSection
            {
                Name = secName,
                VirtualSize = BitConverter.ToUInt32(pe, off + 8),
                VirtualAddress = BitConverter.ToUInt32(pe, off + 12),
                SizeOfRawData = BitConverter.ToUInt32(pe, off + 16),
                PointerToRawData = BitConverter.ToUInt32(pe, off + 20),
            });
        }

        return list;
    }
}

internal sealed class PeSection
{
    public required string Name { get; init; }
    public uint VirtualSize { get; init; }
    public uint VirtualAddress { get; init; }
    public uint SizeOfRawData { get; init; }
    public uint PointerToRawData { get; init; }
}
