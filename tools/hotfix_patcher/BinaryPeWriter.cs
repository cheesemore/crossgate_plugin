using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal static class BinaryPeWriter
{
    public static int AppendToTextSlack(byte[] pe, byte[] payload)
    {
        var text = PeLayout.GetSection(pe, ".text");
        var start = (int)(text.PointerToRawData + text.VirtualSize);
        var end = (int)(text.PointerToRawData + text.SizeOfRawData);
        if (end - start < payload.Length)
        {
            GrowTextIntoTrailingSections(pe, payload.Length - (end - start));
            text = PeLayout.GetSection(pe, ".text");
            start = (int)(text.PointerToRawData + text.VirtualSize);
            end = (int)(text.PointerToRawData + text.SizeOfRawData);
            if (end - start < payload.Length)
            {
                throw new InvalidOperationException(
                    $".text 余量仍不足：需要 {payload.Length} 字节，仅剩 {end - start} 字节");
            }
        }

        var offset = FindWritableOffset(pe, start, end, payload.Length);
        Array.Copy(payload, 0, pe, offset, payload.Length);
        var newRva = PeLayout.OffsetToRva(pe, offset);
        EnsureTextVirtualSize(pe, newRva + payload.Length);
        return newRva;
    }

    private static void EnsureTextVirtualSize(byte[] pe, int requiredRvaEnd)
    {
        var text = PeLayout.GetSection(pe, ".text");
        var requiredSize = (uint)(requiredRvaEnd - text.VirtualAddress);
        if (requiredSize <= text.VirtualSize)
        {
            return;
        }

        if (requiredSize > text.SizeOfRawData)
        {
            throw new InvalidOperationException(
                $"追加 IL 需要 VirtualSize 0x{requiredSize:X}，超过 .text RawSize 0x{text.SizeOfRawData:X}");
        }

        PeLayout.SetSectionVirtualSize(pe, ".text", requiredSize);
        Console.WriteLine($"[PE] .text VirtualSize 0x{text.VirtualSize:X} -> 0x{requiredSize:X}");
        PeLayout.EnsureNoVirtualOverlapAfterText(pe);
    }

    public static void EnsureTextSlack(byte[] pe, int requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        var text = PeLayout.GetSection(pe, ".text");
        var available = (int)(text.SizeOfRawData - text.VirtualSize);
        if (available >= requiredBytes)
        {
            return;
        }

        GrowTextIntoTrailingSections(pe, requiredBytes - available);
    }

    /// <summary>
    /// 追加方法体时每次写入完整 newBody，按当前 .text slack 累计还需扩容的字节数。
    /// </summary>
    public static int ComputeAppendSlackRequired(byte[] pe, IEnumerable<int> appendPayloadLengths)
    {
        var text = PeLayout.GetSection(pe, ".text");
        var available = (int)(text.SizeOfRawData - text.VirtualSize);
        var extra = 0;
        foreach (var length in appendPayloadLengths)
        {
            var need = length - available;
            if (need > 0)
            {
                extra += need;
                available = 0;
            }
            else
            {
                available -= length;
            }
        }

        return extra;
    }

    public static void EnsureTextSlackForAppends(byte[] pe, IEnumerable<int> appendPayloadLengths)
    {
        var payloads = appendPayloadLengths.ToList();
        if (payloads.Count == 0)
        {
            return;
        }

        var text = PeLayout.GetSection(pe, ".text");
        var available = (int)(text.SizeOfRawData - text.VirtualSize);
        var extra = ComputeAppendSlackRequired(pe, payloads);
        EnsureTextSlack(pe, available + extra);
    }

    public static void ReplaceMethodBody(byte[] pe, MethodDefinition method, byte[] oldBodySnapshot, byte[] newBody)
    {
        ReplaceMethodBody(pe, method.RVA, oldBodySnapshot, newBody, null);
    }

    public static void ReplaceMethodBody(byte[] pe, int oldRva, byte[] oldBodySnapshot, byte[] newBody)
    {
        ReplaceMethodBody(pe, oldRva, oldBodySnapshot, newBody, null);
    }

    public static void ReplaceMethodBody(
        byte[] pe,
        int oldRva,
        byte[] oldBodySnapshot,
        byte[] newBody,
        uint? methodDefToken,
        int? methodDefRvaFileOffset = null)
    {
        if (newBody.Length <= oldBodySnapshot.Length)
        {
            var fileOff = PeLayout.RvaToOffset(pe, oldRva);
            Array.Copy(newBody, 0, pe, fileOff, newBody.Length);
            if (newBody.Length < oldBodySnapshot.Length)
            {
                Array.Clear(pe, fileOff + newBody.Length, oldBodySnapshot.Length - newBody.Length);
            }

            Console.WriteLine($"[PATCH] 原地替换 RVA=0x{oldRva:X} len {oldBodySnapshot.Length}->{newBody.Length}");
            return;
        }

        var vaGap = PeLayout.GetTextVaGapBytes(pe);
        if (newBody.Length <= vaGap)
        {
            var newRva = AppendToTextSlack(pe, newBody);
            PatchMethodRva(pe, oldRva, newRva, methodDefToken, methodDefRvaFileOffset);
            Console.WriteLine($"[PATCH] 追加方法体(VA间隙) file RVA=0x{newRva:X} len={newBody.Length}");
            return;
        }

        // 禁止后迁邻居方法：实测 OnCommandCharCallback 等带 EH 的方法迁入间隙会启动未响应。
        throw new InvalidOperationException(
            $"方法体扩大 {oldBodySnapshot.Length}->{newBody.Length} 超 VA 间隙 {vaGap} 字节。" +
            "禁止后迁邻居方法（会导致启动未响应）。可改用神奇九动·DLL版，或压缩 IL 至可原地/可进间隙。");
    }

    /// <summary>已禁用：后迁邻居进 VA 间隙会未响应。保留方法供对照实验，恒返回 false。</summary>
    private static bool TryExpandContiguousByMovingFollowers(
        byte[] pe,
        int oldRva,
        int oldBodyLength,
        byte[] newBody)
    {
        _ = (pe, oldRva, oldBodyLength, newBody);
        return false;
    }

    public static void PatchMethodRva(byte[] pe, int oldRva, int newRva)
    {
        PatchMethodRva(pe, oldRva, newRva, null);
    }

    public static void PatchMethodRva(byte[] pe, int oldRva, int newRva, uint? methodDefToken, int? methodDefRvaFileOffset = null)
    {
        if (oldRva <= 0)
        {
            throw new InvalidOperationException($"无效 MethodDef old RVA: 0x{oldRva:X}");
        }

        if (methodDefRvaFileOffset != null)
        {
            var offset = methodDefRvaFileOffset.Value;
            var current = BitConverter.ToInt32(pe, offset);
            if (current != oldRva)
            {
                throw new InvalidOperationException(
                    $"MethodDef RVA file 0x{offset:X} 当前 0x{current:X} 与期望 0x{oldRva:X} 不符");
            }

            BitConverter.GetBytes(newRva).CopyTo(pe, offset);
            Console.WriteLine(
                $"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} (file 0x{offset:X}, pinned)");
            return;
        }

        if (methodDefToken != null)
        {
            var current = CliMetadata.ReadMethodDefRva(pe, methodDefToken.Value);
            if (current != oldRva)
            {
                throw new InvalidOperationException(
                    $"MethodDef token 0x{methodDefToken.Value:X8} 当前 RVA 0x{current:X} 与期望 0x{oldRva:X} 不符");
            }

            CliMetadata.PatchMethodDefRva(pe, methodDefToken.Value, newRva);
            return;
        }

        if (CliMetadata.TryPatchMethodDefRvaByOldRva(pe, oldRva, newRva, out _))
        {
            return;
        }

        // MethodDef 表解析偶发 miss 时，仅当全文件 RVA 唯一命中才兜底（避免误改其它字段）。
        if (TryFindUniqueRvaFileOffset(pe, oldRva, out var scanOff))
        {
            BitConverter.GetBytes(newRva).CopyTo(pe, scanOff);
            Console.WriteLine($"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} (file 0x{scanOff:X}, unique scan)");
            return;
        }

        throw new InvalidOperationException(
            $"MethodDef RVA 0x{oldRva:X} 无法在 MethodDef 表中定位（请从 .orig 重打）");
    }

    internal static bool TryFindUniqueRvaFileOffset(byte[] pe, int rva, out int fileOffset)
        => TryScanUniqueRvaOffset(pe, rva, out fileOffset);

    private static bool TryScanUniqueRvaOffset(byte[] pe, int oldRva, out int fileOffset)
    {
        fileOffset = -1;
        var pattern = BitConverter.GetBytes(oldRva);
        var hits = 0;
        for (var i = 0; i <= pe.Length - 4; i++)
        {
            if (pe[i] != pattern[0] || pe[i + 1] != pattern[1] || pe[i + 2] != pattern[2] || pe[i + 3] != pattern[3])
            {
                continue;
            }

            hits++;
            fileOffset = i;
        }

        return hits == 1;
    }

    private static int FindWritableOffset(byte[] pe, int start, int end, int length)
    {
        for (var offset = start; offset <= end - length; offset++)
        {
            var ok = true;
            for (var i = 0; i < length; i++)
            {
                if (pe[offset + i] != 0)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("在 .text slack 中找不到连续可写区域");
    }

    /// <summary>
    /// 在保持 PE 文件总大小不变的前提下，扩大 .text 并后移 .rsrc/.reloc。
    /// 优先压缩 .reloc 尾部；.rsrc 仅可压缩 VirtualSize 之后的零填充，绝不截断资源内容。
    /// </summary>
    private static void GrowTextIntoTrailingSections(byte[] pe, int extraTextBytes)
    {
        if (extraTextBytes <= 0)
        {
            return;
        }

        var text = PeLayout.GetSection(pe, ".text");
        var rsrc = PeLayout.GetSection(pe, ".rsrc");
        var reloc = PeLayout.GetSection(pe, ".reloc");

        const int relocKeepRaw = 12;
        var relocAvail = Math.Max(0, (int)reloc.SizeOfRawData - relocKeepRaw);
        var rsrcPadAvail = Math.Max(0, (int)rsrc.SizeOfRawData - (int)rsrc.VirtualSize);
        if (extraTextBytes > relocAvail + rsrcPadAvail)
        {
            throw new InvalidOperationException(
                $"无法为 .text 腾出 {extraTextBytes} 字节（reloc 可缩 {relocAvail}，rsrc 零尾可缩 {rsrcPadAvail}）；" +
                "禁止压缩 .rsrc 有效 VirtualSize 以免启动黑屏/未响应");
        }

        // 优先吃 .reloc，再吃 .rsrc 零填充。
        var takeReloc = Math.Min(extraTextBytes, relocAvail);
        var takeRsrc = extraTextBytes - takeReloc;

        var newTextRaw = text.SizeOfRawData + (uint)extraTextBytes;
        var newRelocRaw = reloc.SizeOfRawData - (uint)takeReloc;
        var newRsrcRaw = rsrc.SizeOfRawData - (uint)takeRsrc;
        if (newRsrcRaw < rsrc.VirtualSize)
        {
            throw new InvalidOperationException(
                $"内部错误：.rsrc RawSize {newRsrcRaw} < VirtualSize {rsrc.VirtualSize}");
        }

        var oldRsrcOff = (int)rsrc.PointerToRawData;
        var oldRsrcSize = (int)rsrc.SizeOfRawData;
        var oldRelocOff = (int)reloc.PointerToRawData;
        var oldRelocSize = (int)reloc.SizeOfRawData;

        var rsrcBytes = new byte[oldRsrcSize];
        Array.Copy(pe, oldRsrcOff, rsrcBytes, 0, oldRsrcSize);
        if (takeRsrc > 0 && !IsZeroTail(rsrcBytes, (int)newRsrcRaw, takeRsrc))
        {
            throw new InvalidOperationException(
                $"rsrc 零尾不足：需缩 {takeRsrc} 字节，但偏移 {newRsrcRaw} 起并非全零");
        }

        var relocBytes = new byte[oldRelocSize];
        Array.Copy(pe, oldRelocOff, relocBytes, 0, oldRelocSize);

        var newRsrcOff = text.PointerToRawData + newTextRaw;
        var newRelocOff = newRsrcOff + newRsrcRaw;
        if (newRelocOff + newRelocRaw != pe.Length)
        {
            throw new InvalidOperationException(
                $"节区重排后文件尾不匹配：{newRelocOff + newRelocRaw} != {pe.Length}");
        }

        Array.Copy(rsrcBytes, 0, pe, (int)newRsrcOff, (int)newRsrcRaw);
        Array.Copy(relocBytes, 0, pe, (int)newRelocOff, (int)newRelocRaw);
        if (newRelocRaw < reloc.SizeOfRawData)
        {
            // 保留原 reloc 内容头部，尾部已由 RawSize 截断
        }

        PeLayout.SetSectionRawSize(pe, ".text", newTextRaw);
        PeLayout.SetSectionPointerToRawData(pe, ".rsrc", newRsrcOff);
        PeLayout.SetSectionRawSize(pe, ".rsrc", newRsrcRaw);
        // 不再下调 .rsrc VirtualSize
        PeLayout.SetSectionPointerToRawData(pe, ".reloc", newRelocOff);
        PeLayout.SetSectionRawSize(pe, ".reloc", newRelocRaw);

        var zeroStart = (int)(text.PointerToRawData + text.VirtualSize);
        var zeroEnd = (int)(text.PointerToRawData + newTextRaw);
        if (zeroEnd > zeroStart)
        {
            Array.Clear(pe, zeroStart, zeroEnd - zeroStart);
        }

        Console.WriteLine(
            $"[PE] .text RawSize +{extraTextBytes} (rsrc -{takeRsrc}, reloc -{takeReloc})，文件体积保持 {pe.Length}");
    }

    private static bool IsZeroTail(byte[] data, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}
