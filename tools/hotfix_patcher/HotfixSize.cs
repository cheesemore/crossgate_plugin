namespace CrossgateMod.Patcher;

internal static class HotfixSize
{
    public const int Expected = 7_075_328;

    public static int Require(byte[] data, string label = "源文件")
    {
        if (data.Length == Expected)
        {
            return Expected;
        }

        throw new InvalidOperationException(
            $"{label}体积应为 {Expected}，实际 {data.Length}。请用更新后的 hotfix 重新创建 .orig");
    }

    public static void EnsureUnchanged(byte[] data, int expectedSize)
    {
        if (data.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({expectedSize} -> {data.Length})，已中止");
        }
    }
}
