namespace CrossgateMod.Patcher;

/// <summary>
/// 将 Cecil 写出的紧凑 PE 零填充到与原版 hotfix 相同的文件体积。
/// 只扩展文件尾部，不套用原版节区 RawSize / SizeOfImage（否则 compact PE 头与节区表不一致会闪退）。
/// </summary>
internal static class PeExactSizePad
{
    public static byte[] Pad(byte[] compactPe, byte[] origTemplate, int expectedSize)
    {
        if (compactPe.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"紧凑 PE {compactPe.Length} 字节已超过目标 {expectedSize} 字节");
        }

        if (origTemplate.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"模板体积 {origTemplate.Length} 与目标 {expectedSize} 不一致");
        }

        if (compactPe.Length == expectedSize)
        {
            return compactPe;
        }

        var padded = new byte[expectedSize];
        Array.Copy(compactPe, padded, compactPe.Length);
        Console.WriteLine(
            $"[PAD] 紧凑 PE {compactPe.Length} 字节 → {expectedSize} 字节（尾部零填充 {expectedSize - compactPe.Length}，不改节区头）");
        return padded;
    }
}
