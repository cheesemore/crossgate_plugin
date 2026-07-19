using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal static class MemberRefTokenLookup
{
    public static uint FindToken(byte[] pe, string typeName, string methodName)
        => FindTokenFromPe(pe, typeName, methodName);

    internal static (int dataOffset, int rowSize, int rowCount) GetMemberRefTable(byte[] pe)
    {
        var tables = CliTablesReader.Read(pe);
        return (tables.MemberRefDataOffset, tables.MemberRefRowSize, tables.MemberRefRowCount);
    }

    internal static CliTablesReader GetTables(byte[] pe) => CliTablesReader.Read(pe);

    public static uint FindToken(ModuleDefinition module, string typeName, string methodName)
    {
        foreach (var memberRef in module.GetMemberReferences())
        {
            if (memberRef is not MethodReference method)
            {
                continue;
            }

            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var declaringName = method.DeclaringType?.Name ?? "";
            if (!string.Equals(declaringName, typeName, StringComparison.Ordinal)
                && method.DeclaringType?.FullName?.EndsWith("." + typeName, StringComparison.Ordinal) != true)
            {
                continue;
            }

            var token = method.MetadataToken.ToUInt32();
            if (token != 0 && (token & 0xFFFFFF) != 0)
            {
                return token;
            }
        }

        throw new InvalidOperationException($"MemberRef 表中未找到 {typeName}.{methodName}");
    }

    private static uint FindTokenFromPe(byte[] pe, string typeName, string methodName)
    {
        var methodString = ReadStringsHeap(pe, methodName)
            ?? throw new InvalidOperationException($"#Strings 中未找到方法名 {methodName}");
        var typeString = ReadStringsHeap(pe, typeName)
            ?? throw new InvalidOperationException($"#Strings 中未找到类型名 {typeName}");

        var tables = CliTablesReader.Read(pe);
        for (var row = 0; row < tables.MemberRefRowCount; row++)
        {
            var offset = tables.GetMemberRefOffset(row);
            var nameIndex = tables.ReadStringIndex(pe, offset + tables.MemberRefNameOffset);
            if (nameIndex != methodString.index)
            {
                continue;
            }

            if (!TypeNameMatches(pe, offset, typeString.index, tables))
            {
                continue;
            }

            return 0x0A000000u | (uint)(row + 1);
        }

        throw new InvalidOperationException($"MemberRef 表中未找到 {typeName}.{methodName}");
    }

    private sealed class StringHit
    {
        public required int index { get; init; }
    }

    private static StringHit? ReadStringsHeap(byte[] pe, string value)
    {
        var (offset, size) = FindStream(pe, "#Strings");
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        for (var i = 1; i < size - bytes.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (pe[offset + i + j] != bytes[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return new StringHit { index = i };
            }
        }

        return null;
    }

    private static bool TypeNameMatches(byte[] pe, int memberRefOffset, int typeNameIndex, CliTablesReader tables)
    {
        const int memberRefParentTagBits = 3;
        var coded = tables.ReadCodedIndex(pe, memberRefOffset + tables.MemberRefClassOffset, memberRefParentTagBits);
        var tag = coded & ((1 << memberRefParentTagBits) - 1);
        var index = coded >> memberRefParentTagBits;
        if (tag != 0x01)
        {
            return false;
        }

        var typeOffset = tables.GetTypeRefOffset(index - 1);
        var nameIndex = tables.ReadStringIndex(pe, typeOffset + tables.TypeRefNameOffset);
        return nameIndex == typeNameIndex;
    }

    private static (int offset, int size) FindStream(byte[] pe, string name)
    {
        var metaOff = FindMetadataOffset(pe);
        var versionLen = BitConverter.ToInt32(pe, metaOff + 12);
        var streamCount = BitConverter.ToInt16(pe, metaOff + 18 + versionLen);
        var pos = metaOff + 20 + versionLen;
        for (var i = 0; i < streamCount; i++)
        {
            var streamOffset = BitConverter.ToInt32(pe, pos);
            var streamName = ReadStreamName(pe, pos + 8);
            if (streamName == name)
            {
                return (metaOff + streamOffset, BitConverter.ToInt32(pe, pos + 4));
            }

            var nameByteLen = System.Text.Encoding.ASCII.GetByteCount(streamName) + 1;
            pos += 8 + ((nameByteLen + 3) / 4) * 4;
        }

        throw new InvalidOperationException("未找到 " + name);
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

    internal sealed class CliTablesReader
    {
        public int MemberRefDataOffset { get; }
        public int MemberRefRowSize { get; }
        public int MemberRefRowCount { get; }
        public int MemberRefNameOffset { get; }
        public int MemberRefClassOffset { get; }
        public int MemberRefParentIndexSize { get; }
        public int TypeRefDataOffset { get; }
        public int TypeRefRowSize { get; }
        public int TypeRefRowCount { get; }
        public int TypeRefNameOffset { get; }
        public int AssemblyRefDataOffset { get; }
        public int AssemblyRefRowSize { get; }
        public int AssemblyRefRowCount { get; }
        public int MethodDefDataOffset { get; }
        public int MethodDefRowSize { get; }
        public int MethodDefRowCount { get; }
        public int TypeDefDataOffset { get; }
        public int TypeDefRowSize { get; }
        public int TypeDefRowCount { get; }
        public int TypeDefExtendsOffset { get; }
        public int StringIndexSize { get; }
        public int TypeDefOrRefIndexSize { get; }
        public bool StringIndex4 { get; }
        public bool BlobIndex4 { get; }
        private readonly int _blobIndexSize;

        private CliTablesReader(
            int memberRefDataOffset,
            int memberRefRowSize,
            int memberRefRowCount,
            int memberRefNameOffset,
            int memberRefClassOffset,
            int memberRefParentIndexSize,
            int typeRefDataOffset,
            int typeRefRowSize,
            int typeRefRowCount,
            int typeRefNameOffset,
            int assemblyRefDataOffset,
            int assemblyRefRowSize,
            int assemblyRefRowCount,
            int methodDefDataOffset,
            int methodDefRowSize,
            int methodDefRowCount,
            int typeDefDataOffset,
            int typeDefRowSize,
            int typeDefRowCount,
            int typeDefExtendsOffset,
            int stringIndexSize,
            int typeDefOrRefIndexSize,
            bool stringIndex4,
            bool blobIndex4,
            int blobIndexSize)
        {
            MemberRefDataOffset = memberRefDataOffset;
            MemberRefRowSize = memberRefRowSize;
            MemberRefRowCount = memberRefRowCount;
            MemberRefNameOffset = memberRefNameOffset;
            MemberRefClassOffset = memberRefClassOffset;
            MemberRefParentIndexSize = memberRefParentIndexSize;
            TypeRefDataOffset = typeRefDataOffset;
            TypeRefRowSize = typeRefRowSize;
            TypeRefRowCount = typeRefRowCount;
            TypeRefNameOffset = typeRefNameOffset;
            AssemblyRefDataOffset = assemblyRefDataOffset;
            AssemblyRefRowSize = assemblyRefRowSize;
            AssemblyRefRowCount = assemblyRefRowCount;
            MethodDefDataOffset = methodDefDataOffset;
            MethodDefRowSize = methodDefRowSize;
            MethodDefRowCount = methodDefRowCount;
            TypeDefDataOffset = typeDefDataOffset;
            TypeDefRowSize = typeDefRowSize;
            TypeDefRowCount = typeDefRowCount;
            TypeDefExtendsOffset = typeDefExtendsOffset;
            StringIndexSize = stringIndexSize;
            TypeDefOrRefIndexSize = typeDefOrRefIndexSize;
            StringIndex4 = stringIndex4;
            BlobIndex4 = blobIndex4;
            _blobIndexSize = blobIndexSize;
        }

        public int ReadStringIndex(byte[] pe, int offset)
        {
            return StringIndexSize == 4
                ? (int)BitConverter.ToUInt32(pe, offset)
                : BitConverter.ToUInt16(pe, offset);
        }

        public int ReadCodedIndex(byte[] pe, int offset, int tagBits)
        {
            var size = tagBits == 3 ? MemberRefParentIndexSize : TypeDefOrRefIndexSize;
            return size == 4
                ? (int)BitConverter.ToUInt32(pe, offset)
                : BitConverter.ToUInt16(pe, offset);
        }

        public int ReadMemberRefParentIndex(byte[] pe, int offset)
        {
            return MemberRefParentIndexSize == 4
                ? (int)BitConverter.ToUInt32(pe, offset)
                : BitConverter.ToUInt16(pe, offset);
        }

        public static CliTablesReader Read(byte[] pe)
        {
            var metaOff = FindMetadataOffset(pe);
            var versionLen = BitConverter.ToInt32(pe, metaOff + 12);
            var streamCount = BitConverter.ToInt16(pe, metaOff + 18 + versionLen);
            var pos = metaOff + 20 + versionLen;
            var tablesOff = 0;
            for (var i = 0; i < streamCount; i++)
            {
                var streamOffset = BitConverter.ToInt32(pe, pos);
                var streamName = ReadStreamName(pe, pos + 8);
                if (streamName is "#~" or "#-")
                {
                    tablesOff = metaOff + streamOffset;
                    break;
                }

                var nameByteLen = System.Text.Encoding.ASCII.GetByteCount(streamName) + 1;
                pos += 8 + ((nameByteLen + 3) / 4) * 4;
            }

            var heapSizes = pe[tablesOff + 6];
            var stringIndex4 = (heapSizes & 0x01) != 0;
            var blobIndex4 = (heapSizes & 0x04) != 0;
            var stringIndexSize = stringIndex4 ? 4 : 2;
            var blobIndexSize = blobIndex4 ? 4 : 2;
            var valid = BitConverter.ToUInt64(pe, tablesOff + 8);
            var dataOffset = tablesOff + 24;
            var rowCounts = new Dictionary<int, int>();
            var present = new List<int>();
            for (var table = 0; table < 64; table++)
            {
                if (((valid >> table) & 1) == 0)
                {
                    continue;
                }

                present.Add(table);
                rowCounts[table] = BitConverter.ToInt32(pe, dataOffset);
                dataOffset += 4;
            }

            var tableDataOffset = dataOffset;
            int Row(int table) => rowCounts.GetValueOrDefault(table, 0);
            var typeDefOrRefIndexSize = CodedIndexSize(2, Row(0x02), Row(0x01));
            var memberRefParentIndexSize = CodedIndexSize(3, Row(0x02), Row(0x01), Row(0x1A), Row(0x06), Row(0x1B));
            var codedSizes = new TableCodedSizes(typeDefOrRefIndexSize, memberRefParentIndexSize);

            int memberRefDataOffset = 0, memberRefRowSize = 0, memberRefRowCount = 0;
            int typeRefDataOffset = 0, typeRefRowSize = 0, typeRefRowCount = 0;
            int assemblyRefDataOffset = 0, assemblyRefRowSize = 0, assemblyRefRowCount = 0;
            int methodDefDataOffset = 0, methodDefRowSize = 0, methodDefRowCount = 0;
            int typeDefDataOffset = 0, typeDefRowSize = 0, typeDefRowCount = 0;
            var typeDefExtendsOffset = 4 + stringIndexSize + stringIndexSize;
            foreach (var table in present)
            {
                var rowSize = GetRowSize(table, stringIndexSize, blobIndexSize, codedSizes);
                if (rowSize <= 0)
                {
                    throw new InvalidOperationException($"未支持的元数据表 0x{table:X2}");
                }
                if (table == 0x0A)
                {
                    memberRefDataOffset = tableDataOffset;
                    memberRefRowSize = rowSize;
                    memberRefRowCount = rowCounts[table];
                }
                else if (table == 0x01)
                {
                    typeRefDataOffset = tableDataOffset;
                    typeRefRowSize = rowSize;
                    typeRefRowCount = rowCounts[table];
                }
                else if (table == 0x23)
                {
                    assemblyRefDataOffset = tableDataOffset;
                    assemblyRefRowSize = rowSize;
                    assemblyRefRowCount = rowCounts[table];
                }
                else if (table == 0x06)
                {
                    methodDefDataOffset = tableDataOffset;
                    methodDefRowSize = rowSize;
                    methodDefRowCount = rowCounts[table];
                }
                else if (table == 0x02)
                {
                    typeDefDataOffset = tableDataOffset;
                    typeDefRowSize = rowSize;
                    typeDefRowCount = rowCounts[table];
                }

                tableDataOffset += rowCounts[table] * rowSize;
            }

            return new CliTablesReader(
                memberRefDataOffset,
                memberRefRowSize,
                memberRefRowCount,
                memberRefParentIndexSize,
                0,
                memberRefParentIndexSize,
                typeRefDataOffset,
                typeRefRowSize,
                typeRefRowCount,
                typeDefOrRefIndexSize,
                assemblyRefDataOffset,
                assemblyRefRowSize,
                assemblyRefRowCount,
                methodDefDataOffset,
                methodDefRowSize,
                methodDefRowCount,
                typeDefDataOffset,
                typeDefRowSize,
                typeDefRowCount,
                typeDefExtendsOffset,
                stringIndexSize,
                typeDefOrRefIndexSize,
                stringIndex4,
                blobIndex4,
                blobIndexSize);
        }

        public int GetMemberRefOffset(int row) => MemberRefDataOffset + row * MemberRefRowSize;

        public int GetTypeRefOffset(int row) => TypeRefDataOffset + row * TypeRefRowSize;

        public int GetMethodDefRvaOffset(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= MethodDefRowCount)
            {
                throw new InvalidOperationException(
                    $"MethodDef 行号越界: {rowIndex} (共 {MethodDefRowCount})");
            }

            return MethodDefDataOffset + rowIndex * MethodDefRowSize;
        }

        private static int CodedIndexSize(int tagBits, params int[] rowCounts)
        {
            var max = rowCounts.DefaultIfEmpty(0).Max();
            return max >= (1 << (16 - tagBits)) ? 4 : 2;
        }

        private readonly struct TableCodedSizes(int typeDefOrRef, int memberRefParent)
        {
            public int TypeDefOrRef { get; } = typeDefOrRef;
            public int MemberRefParent { get; } = memberRefParent;
        }

        private static int GetRowSize(int table, int stringIndexSize, int blobIndexSize, TableCodedSizes coded)
        {
            var coded2 = coded.TypeDefOrRef;
            var coded3 = coded.MemberRefParent;
            return table switch
            {
                0x00 => 2 + stringIndexSize + 16 + blobIndexSize,
                0x01 => coded2 + stringIndexSize + stringIndexSize,
                0x02 => 4 + coded2 + coded2 + stringIndexSize + coded2,
                0x04 => 2 + stringIndexSize + blobIndexSize,
                0x06 => 4 + 2 + 2 + stringIndexSize + blobIndexSize + coded2,
                0x08 => 2 + coded2 + stringIndexSize + blobIndexSize,
                0x09 => coded3 + 2,
                0x0A => coded3 + stringIndexSize + blobIndexSize,
                0x0B => 2 + coded2 + stringIndexSize,
                0x0C => 2 + coded2 + stringIndexSize,
                0x0D => 2 + coded2,
                0x0E => coded2 + coded2,
                0x0F => 2 + coded2 + coded2,
                0x10 => coded2,
                0x11 => 2 + stringIndexSize + blobIndexSize,
                0x12 => coded2 + coded2,
                0x14 => 2 + stringIndexSize + coded2,
                0x15 => coded2 + coded2,
                0x17 => 2 + stringIndexSize + blobIndexSize + coded2,
                0x18 => coded2 + coded2,
                0x19 => coded2 + coded2 + coded2,
                0x1A => coded2,
                0x1B => coded2 + stringIndexSize + blobIndexSize,
                0x1C => 4 + 2 + coded2 + coded2,
                0x1D => coded2 + blobIndexSize,
                0x20 => 4 + 2 + 2 + 4 + 4 + 4,
                0x21 => 4 + 2 + 2 + blobIndexSize,
                0x22 => 4 + 2 + 2 + blobIndexSize,
                0x23 => 4 + 2 + 2 + blobIndexSize,
                0x24 => 4 + 2 + 2 + blobIndexSize,
                0x25 => 4 + 2 + 2 + blobIndexSize,
                0x26 => 2 + blobIndexSize,
                0x27 => coded2 + coded2 + stringIndexSize + blobIndexSize,
                0x28 => 4 + 2 + stringIndexSize,
                0x29 => 4 + coded2 + coded2,
                0x2A => coded2 + coded2,
                0x2B => coded2 + blobIndexSize,
                0x2C => coded2 + 2,
                _ => 0,
            };
        }
    }
}
