using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 在干净 .orig 上生成桥接模板，再叠加到已打玩法补丁的 hotfix（避免对已补丁 PE 做 Cecil Write / metadata 导入）。
/// </summary>
internal static class BridgeTemplateOverlay
{
    private sealed record MethodRva(string TypeName, string MethodName, int Rva);

    public static void Apply(
        byte[] orig,
        string bridgedPath,
        byte[] target,
        string targetPath,
        string hotfixDir)
    {
        var methodRvas = ReadMethodRvas(targetPath, hotfixDir);
        Console.WriteLine($"[OVERLAY] 已定位 {methodRvas.Count} 个 hook 方法 RVA");
        CopyStreamDelta(orig, File.ReadAllBytes(bridgedPath), target, "#Strings");
        CopyStreamDelta(orig, File.ReadAllBytes(bridgedPath), target, "#US");
        CopyStreamDelta(orig, File.ReadAllBytes(bridgedPath), target, "#Blob");
        CopyStreamDelta(orig, File.ReadAllBytes(bridgedPath), target, "#GUID");
        var bridgedBytes = File.ReadAllBytes(bridgedPath);
        CopyTableRows(orig, bridgedBytes, target, MemberRefTableLayout.Read(orig));
        CopyTypeRefRows(orig, bridgedBytes, target);
        OverlayBridgeMethodBodies(bridgedPath, target, hotfixDir, methodRvas);
    }

    private static List<MethodRva> ReadMethodRvas(string targetPath, string hotfixDir)
    {
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var targetAsm = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            targetPath,
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadWrite = true,
            });

        return
        [
            new("HotfixEntry", "OnApplicationPause", FindMethod(targetAsm, "HotfixEntry", "OnApplicationPause").RVA),
            new("HotfixEntry", "OnApplicationQuit", FindMethod(targetAsm, "HotfixEntry", "OnApplicationQuit").RVA),
            new("HotfixEntry", "Start", FindMethod(targetAsm, "HotfixEntry", "Start").RVA),
            new("GameManagerHotfix", "Start", FindMethod(targetAsm, "GameManagerHotfix", "Start").RVA),
        ];
    }

    private static void CopyStreamDelta(byte[] orig, byte[] bridged, byte[] target, string streamName)
    {
        var streams = MetadataStreamGaps.ListStreams(orig);
        var stream = streams.FirstOrDefault(s => s.Name == streamName)
            ?? throw new InvalidOperationException($"缺少 metadata 流 {streamName}");
        var end = stream.FileOffset + stream.Size;
        var copied = 0;
        for (var i = stream.FileOffset; i < end; i++)
        {
            if (orig[i] != bridged[i] && target[i] == orig[i])
            {
                target[i] = bridged[i];
                copied++;
            }
        }

        if (copied > 0)
        {
            Console.WriteLine($"[OVERLAY] {streamName} 复制 {copied} 字节增量");
        }
    }

    private static void CopyTableRows(
        byte[] orig,
        byte[] bridged,
        byte[] target,
        MemberRefTableLayout layout)
    {
        var copied = 0;
        for (var row = 0; row < layout.RowCount; row++)
        {
            var off = layout.DataFileOffset + row * layout.RowSize;
            if (!BytesEqual(orig, bridged, off, layout.RowSize)
                && BytesEqual(orig, target, off, layout.RowSize))
            {
                Array.Copy(bridged, off, target, off, layout.RowSize);
                copied++;
            }
        }

        if (copied > 0)
        {
            Console.WriteLine($"[OVERLAY] MemberRef 复制 {copied} 行");
        }
    }

    private static void CopyTypeRefRows(byte[] orig, byte[] bridged, byte[] target)
    {
        var layout = SystemMetadataBridge.GetTypeRefRowLayout(orig);
        var tableOffset = SystemMetadataBridge.GetTypeRefTableFileOffset(orig);
        var rowCount = SystemMetadataBridge.OpenReader(orig).GetTableRowCount(TableIndex.TypeRef);
        var copied = 0;
        for (var row = 0; row < rowCount; row++)
        {
            var off = tableOffset + row * layout.RowSize;
            if (!BytesEqual(orig, bridged, off, layout.RowSize)
                && BytesEqual(orig, target, off, layout.RowSize))
            {
                Array.Copy(bridged, off, target, off, layout.RowSize);
                copied++;
            }
        }

        if (copied > 0)
        {
            Console.WriteLine($"[OVERLAY] TypeRef 复制 {copied} 行");
        }
    }

    private static void OverlayBridgeMethodBodies(
        string bridgedPath,
        byte[] target,
        string hotfixDir,
        List<MethodRva> methodRvas)
    {
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        var bridgedBytes = File.ReadAllBytes(bridgedPath);
        using var bridgedAsm = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            bridgedPath,
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadWrite = true,
            });

        var userStrings = UserStringHeap.FromPe(bridgedBytes);
        foreach (var entry in methodRvas)
        {
            var bridgedMethod = FindMethod(bridgedAsm, entry.TypeName, entry.MethodName);
            var newBody = IlSerializer.Serialize(bridgedMethod.Body, userStrings);
            var snapshot = ReadMethodBodySnapshot(target, entry.Rva);
            BinaryPeWriter.ReplaceMethodBody(target, entry.Rva, snapshot, newBody);
            Console.WriteLine(
                $"[OVERLAY] {entry.TypeName}.{entry.MethodName} RVA=0x{entry.Rva:X} len={newBody.Length}");
        }
    }

    private static Mono.Cecil.MethodDefinition FindMethod(Mono.Cecil.AssemblyDefinition asm, string typeName, string methodName)
        => asm.MainModule.Types.First(t => t.Name == typeName)
            .Methods.First(m => m.Name == methodName && m.HasBody);

    private static byte[] ReadMethodBodySnapshot(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            var codeSize = flags >> 2;
            var len = 1 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(pe, off + 4);
            var totalSize = BitConverter.ToInt32(pe, off + 8);
            var len = Math.Max(12 + codeSize, totalSize);
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }

    private static bool BytesEqual(byte[] left, byte[] right, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (left[offset + i] != right[offset + i])
            {
                return false;
            }
        }

        return true;
    }
}
