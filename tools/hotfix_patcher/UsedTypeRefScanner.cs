namespace CrossgateMod.Patcher;

/// <summary>找出 #~ 中未被引用的 TypeRef 行，供原地回收（不扩表、不 shift #~）。</summary>
internal static class UsedTypeRefScanner
{
    public static List<int> FindUnusedRows(byte[] pe, int count)
        => SystemMetadataBridge.FindUnusedTypeRefRows(pe, count);

    internal static HashSet<int> CollectUsedRows(byte[] pe, MemberRefTokenLookup.CliTablesReader tables)
        => SystemMetadataBridge.CollectUsedTypeRefRows(pe);
}
