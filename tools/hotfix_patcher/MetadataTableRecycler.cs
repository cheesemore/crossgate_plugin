namespace CrossgateMod.Patcher;

/// <summary>原地改写元数据表行（不增加行数、不 shift #~ 流）。</summary>
internal static class MetadataTableRecycler
{
    private static List<int>? _freeTypeRefRows;
    private static int _typeRefTableOffset;
    private static SystemMetadataBridge.TypeRefRowLayout _typeRefLayout;

    internal static void ResetTypeRefPool(
        int typeRefTableOffset,
        SystemMetadataBridge.TypeRefRowLayout typeRefLayout)
    {
        _freeTypeRefRows = null;
        _typeRefTableOffset = typeRefTableOffset;
        _typeRefLayout = typeRefLayout;
    }

    public static int RecycleTypeRefRow(byte[] pe, int scopeCoded, int nameIndex, int namespaceIndex)
    {
        _freeTypeRefRows ??= UsedTypeRefScanner.FindUnusedRows(pe, 2);
        if (_freeTypeRefRows.Count == 0)
        {
            throw new InvalidOperationException("TypeRef 回收池已耗尽");
        }

        var rowIndex = _freeTypeRefRows[0];
        _freeTypeRefRows.RemoveAt(0);
        SystemMetadataBridge.WriteRecycledTypeRefRow(
            pe, _typeRefTableOffset, _typeRefLayout, rowIndex, scopeCoded, nameIndex, namespaceIndex);
        return rowIndex;
    }
}
