using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace CrossgateMod.Patcher;

internal static class TypeRefPoolProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: HotfixPatcher typeref-pool <hotfix>");
            return 1;
        }

        var pe = File.ReadAllBytes(Path.GetFullPath(args[0]));
        var reader = SystemMetadataBridge.OpenReader(pe);
        var formatHandle = MetadataTokens.MemberReferenceHandle(0x48);
        var formatRef = reader.GetMemberReference(formatHandle);
        var formatNameIndex = MetadataTokens.GetHeapOffset(formatRef.Name);
        var formatSigIndex = MetadataTokens.GetHeapOffset(formatRef.Signature);
        Console.WriteLine(
            $"Format row=0x48 parent={formatRef.Parent} name=0x{formatNameIndex:X} sig=0x{formatSigIndex:X}");

        var used = SystemMetadataBridge.CollectUsedTypeRefRows(pe);
        Console.WriteLine(
            $"TypeRef rows={reader.GetTableRowCount(TableIndex.TypeRef)}, used={used.Count}, " +
            $"MemberRef rows={reader.GetTableRowCount(TableIndex.MemberRef)}");

        try
        {
            var free = UsedTypeRefScanner.FindUnusedRows(pe, 2);
            Console.WriteLine($"Next recyclable rows: {string.Join(", ", free.Select(r => r + 1))}");
            Console.WriteLine(
                $"TypeRef table file=0x{SystemMetadataBridge.GetTypeRefTableFileOffset(pe):X} " +
                $"rowSize={SystemMetadataBridge.GetTypeRefRowLayout(pe).RowSize}");
            Console.WriteLine(
                $"MemberRef table file=0x{SystemMetadataBridge.GetMemberRefTableFileOffset(pe):X} " +
                $"rowSize={SystemMetadataBridge.GetMemberRefRowLayout(pe).RowSize}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Probe: " + ex.Message);
        }

        return 0;
    }
}
