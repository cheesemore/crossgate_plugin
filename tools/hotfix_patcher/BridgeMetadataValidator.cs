using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace CrossgateMod.Patcher;

internal static class BridgeMetadataValidator
{
    public static void VerifyTokens(byte[] pe, IReadOnlyDictionary<string, uint> tokensByKey)
    {
        var reader = SystemMetadataBridge.OpenReader(pe);
        foreach (var (key, token) in tokensByKey)
        {
            var handle = MetadataTokens.MemberReferenceHandle((int)(token & 0xFFFFFF));
            var memberRef = reader.GetMemberReference(handle);
            _ = reader.GetString(memberRef.Name);
            _ = reader.GetBlobReader(memberRef.Signature);
            Console.WriteLine($"[VERIFY] BCL 可读 {key} token=0x{token:X8}");
        }
    }
}
