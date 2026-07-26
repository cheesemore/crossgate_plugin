using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilAssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using CecilMethodDefinition = Mono.Cecil.MethodDefinition;
using CecilTypeDefinition = Mono.Cecil.TypeDefinition;

namespace CrossgateMod.Patcher;

/// <summary>
/// 侧栏百科按钮（MapSidebarPanel.OnClickWiki）改为打开资源下载面板 DownloadResPanel.Open(uid)。
/// 同时将 DownloadResPanel 内对 PlayerData.downloadResAward 的读取强制为 0，
/// 否则已领取时按钮灰掉且不会 SendResetBp("资源下载")。
/// 需追加 MethodSpec：UIManager.GetUIPanel&lt;DownloadResPanel&gt;。
/// </summary>
internal static class WikiOpenDownloadResIlPatcher
{
    private static readonly string[] AwardReadMethods =
    [
        "OnClickStartBtn",
        "OnPlayerDataChange",
        "OnUpdataResource",
    ];

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var detectOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotfix" when i + 1 < args.Length:
                    source = Path.GetFullPath(args[++i]);
                    break;
                case "--output" when i + 1 < args.Length:
                    output = Path.GetFullPath(args[++i]);
                    break;
                case "--detect":
                    detectOnly = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher wiki-download-res-patch --hotfix <hotfix> [--output <out>]\n" +
                "      HotfixPatcher wiki-download-res-patch --hotfix <hotfix> --detect");
            return 1;
        }

        if (detectOnly)
        {
            Console.WriteLine(IsPatched(source) ? "wiki-download-res" : "original");
            return 0;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 百科→资源下载面板补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static bool IsPatched(string hotfixPath)
    {
        var resolver = CreateResolver();
        using var asm = CecilAssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });
        return IsWikiOpenPatched(asm) && IsAwardForced(asm);
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);
        var data = (byte[])origBytes.Clone();

        var resolver = CreateResolver();
        using var asm = CecilAssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var wikiDone = IsWikiOpenPatched(asm);
        var awardDone = IsAwardForced(asm);
        if (wikiDone && awardDone)
        {
            throw new InvalidOperationException("百科→资源下载面板补丁已包含");
        }

        var downloadPanel = asm.MainModule.Types.FirstOrDefault(t => t.Name == "DownloadResPanel")
            ?? throw new InvalidOperationException("未找到 DownloadResPanel");

        if (!wikiDone)
        {
            PatchWikiOpen(data, origBytes, asm, downloadPanel);
        }
        else
        {
            Console.WriteLine("[SKIP] MapSidebarPanel.OnClickWiki 已是打开 DownloadResPanel");
        }

        if (!awardDone)
        {
            ForceDownloadResAwardZero(data, asm, downloadPanel);
        }
        else
        {
            Console.WriteLine("[SKIP] downloadResAward 读取已强制为 0");
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static void PatchWikiOpen(
        byte[] data,
        byte[] origBytes,
        CecilAssemblyDefinition asm,
        CecilTypeDefinition downloadPanel)
    {
        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
        var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");
        var open = downloadPanel.Methods.FirstOrDefault(m =>
                m.Name == "Open"
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == "System.String")
            ?? throw new InvalidOperationException("未找到 DownloadResPanel.Open(string)");
        var selectUid = asm.MainModule.Types
                .FirstOrDefault(t => t.Name == "PlayerDataHolder")
                ?.Methods.FirstOrDefault(m => m.Name == "get_SelectPlayerUid" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("未找到 PlayerDataHolder.get_SelectPlayerUid");
        var getUIPanelDef = asm.MainModule.Types
                .FirstOrDefault(t => t.Name == "UIManager")
                ?.Methods.FirstOrDefault(m => m.Name == "GetUIPanel" && m.HasGenericParameters)
            ?? throw new InvalidOperationException("未找到 UIManager.GetUIPanel<T>");

        var methodSpecToken = EnsureGetUIPanelMethodSpec(data, getUIPanelDef, downloadPanel);
        var snapshot = MethodBodyBlob.Read(origBytes, onClickWiki.RVA);
        var newBody = BuildOpenBody(methodSpecToken, selectUid.MetadataToken.ToUInt32(), open.MetadataToken.ToUInt32());
        BinaryPeWriter.ReplaceMethodBody(data, onClickWiki.RVA, snapshot, newBody);
        Console.WriteLine("[PATCH] MapSidebarPanel.OnClickWiki -> DownloadResPanel.Open(SelectPlayerUid)");
    }

    /// <summary>
    /// 将 <c>ldfld PlayerData::downloadResAward</c> 替换为 <c>pop; ldc.i4.0; nop×3</c>（等长 5 字节），
    /// 使领取按钮可点并走 SendResetBp。
    /// </summary>
    private static void ForceDownloadResAwardZero(
        byte[] data,
        CecilAssemblyDefinition asm,
        CecilTypeDefinition downloadPanel)
    {
        var awardField = asm.MainModule.Types
                .FirstOrDefault(t => t.Name == "PlayerData")
                ?.Fields.FirstOrDefault(f => f.Name == "downloadResAward")
            ?? throw new InvalidOperationException("未找到 PlayerData.downloadResAward");
        var fieldToken = BitConverter.GetBytes(awardField.MetadataToken.ToUInt32());
        var patchedMethods = 0;

        foreach (var methodName in AwardReadMethods)
        {
            var method = downloadPanel.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody)
                ?? throw new InvalidOperationException($"未找到 DownloadResPanel.{methodName}");
            var snapshot = MethodBodyBlob.Read(data, method.RVA);
            var body = (byte[])snapshot.Clone();
            var hits = ReplaceLdfldWithZero(body, fieldToken);
            if (hits == 0)
            {
                throw new InvalidOperationException(
                    $"DownloadResPanel.{methodName} 中未找到 ldfld downloadResAward");
            }

            BinaryPeWriter.ReplaceMethodBody(data, method.RVA, snapshot, body);
            Console.WriteLine($"[PATCH] DownloadResPanel.{methodName}: downloadResAward 读取→0（{hits}处）");
            patchedMethods++;
        }

        if (patchedMethods == 0)
        {
            throw new InvalidOperationException("未能强制 downloadResAward=0");
        }
    }

    private static int ReplaceLdfldWithZero(byte[] methodBody, byte[] fieldToken)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var hits = 0;
        // ldfld = 0x7B + token(4) → pop + ldc.i4.0 + nop×3
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldfld.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != fieldToken[0]
                || methodBody[i + 2] != fieldToken[1]
                || methodBody[i + 3] != fieldToken[2]
                || methodBody[i + 4] != fieldToken[3])
            {
                continue;
            }

            methodBody[i] = (byte)OpCodes.Pop.Value;
            methodBody[i + 1] = (byte)OpCodes.Ldc_I4_0.Value;
            methodBody[i + 2] = (byte)OpCodes.Nop.Value;
            methodBody[i + 3] = (byte)OpCodes.Nop.Value;
            methodBody[i + 4] = (byte)OpCodes.Nop.Value;
            hits++;
        }

        return hits;
    }

    private static int GetCodeOffset(byte[] methodBody)
    {
        var flags = methodBody[0];
        return (flags & 0x3) switch
        {
            0x2 => 1,
            0x3 => 12,
            _ => throw new InvalidOperationException($"未知 method header 0x{flags:X2}"),
        };
    }

    private static int GetCodeSize(byte[] methodBody)
    {
        var flags = methodBody[0];
        if ((flags & 0x3) == 0x2)
        {
            return flags >> 2;
        }

        if ((flags & 0x3) == 0x3)
        {
            return BitConverter.ToInt32(methodBody, 4);
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");
    }

    private static bool IsWikiOpenPatched(CecilAssemblyDefinition asm)
    {
        var wiki = asm.MainModule.Types
            .FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody);
        if (wiki == null)
        {
            return false;
        }

        foreach (var ins in wiki.Body.Instructions)
        {
            if (ins.Operand is MethodReference mr
                && mr.DeclaringType?.Name == "DownloadResPanel"
                && mr.Name == "Open")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAwardForced(CecilAssemblyDefinition asm)
    {
        var panel = asm.MainModule.Types.FirstOrDefault(t => t.Name == "DownloadResPanel");
        var click = panel?.Methods.FirstOrDefault(m => m.Name == "OnClickStartBtn" && m.HasBody);
        if (click == null)
        {
            return false;
        }

        foreach (var ins in click.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldfld
                && ins.Operand is FieldReference fr
                && fr.Name == "downloadResAward")
            {
                return false;
            }
        }

        // 已打补丁时应不再出现该 ldfld，且仍有 GetPlayerFromUid（pop 掉 player）
        return click.Body.Instructions.Any(ins =>
            ins.Operand is MethodReference mr && mr.Name == "GetPlayerFromUid");
    }

    private static uint EnsureGetUIPanelMethodSpec(
        byte[] pe,
        CecilMethodDefinition getUIPanelDef,
        CecilTypeDefinition panelType)
    {
        var existing = FindExistingGetUIPanelMethodSpec(pe, getUIPanelDef.MetadataToken.RID, panelType.MetadataToken.RID);
        if (existing != 0)
        {
            Console.WriteLine($"[META] 复用 MethodSpec GetUIPanel<{panelType.Name}> token=0x{existing:X8}");
            return existing;
        }

        var methodCoded = (int)((getUIPanelDef.MetadataToken.RID << 1) | 0);
        var typeCoded = (int)((panelType.MetadataToken.RID << 2) | 0);
        var blobEntry = BuildGenericInstClassBlobEntry(typeCoded);
        var blobIndex = MetadataStreamGaps.EnsureBlob(pe, blobEntry);
        var newRow = MetadataTableAppender.AppendMethodSpecRow(pe, methodCoded, blobIndex);
        var token = 0x2B000000u | (uint)(newRow + 1);
        Console.WriteLine($"[META] 新建 MethodSpec GetUIPanel<{panelType.Name}> token=0x{token:X8}");
        return token;
    }

    private static uint FindExistingGetUIPanelMethodSpec(byte[] pe, uint getUIPanelRid, uint panelTypeRid)
    {
        using var ms = new MemoryStream(pe, writable: false);
        using var peReader = new PEReader(ms);
        var reader = peReader.GetMetadataReader();
        var wantMethodToken = 0x06000000 | (int)getUIPanelRid;
        var wantTypeCoded = (int)((panelTypeRid << 2) | 0);

        var count = reader.GetTableRowCount(TableIndex.MethodSpec);
        for (var i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.MethodSpecificationHandle(i);
            var spec = reader.GetMethodSpecification(handle);
            if (MetadataTokens.GetToken(spec.Method) != wantMethodToken)
            {
                continue;
            }

            var blob = reader.GetBlobReader(spec.Signature);
            if (!TryReadGenericInstClassCoded(ref blob, out var coded) || coded != wantTypeCoded)
            {
                continue;
            }

            return (uint)MetadataTokens.GetToken(handle);
        }

        return 0;
    }

    private static bool TryReadGenericInstClassCoded(ref BlobReader blob, out int typeDefOrRefCoded)
    {
        typeDefOrRefCoded = 0;
        if (blob.RemainingBytes < 3)
        {
            return false;
        }

        if (blob.ReadByte() != 0x0A)
        {
            return false;
        }

        var genArgCount = blob.ReadCompressedInteger();
        if (genArgCount != 1 || blob.RemainingBytes < 2)
        {
            return false;
        }

        if (blob.ReadByte() != 0x12)
        {
            return false;
        }

        typeDefOrRefCoded = blob.ReadCompressedInteger();
        return true;
    }

    private static byte[] BuildGenericInstClassBlobEntry(int typeDefOrRefCoded)
    {
        var payload = new List<byte> { 0x0A, 0x01, 0x12 };
        payload.AddRange(CompressUnsigned(typeDefOrRefCoded));
        var entry = new List<byte>();
        entry.AddRange(CompressUnsigned(payload.Count));
        entry.AddRange(payload);
        return entry.ToArray();
    }

    private static byte[] CompressUnsigned(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value <= 0x7F)
        {
            return [(byte)value];
        }

        if (value <= 0x3FFF)
        {
            return [(byte)(0x80 | (value >> 8)), (byte)(value & 0xFF)];
        }

        return
        [
            (byte)(0xC0 | (value >> 24)),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
        ];
    }

    private static byte[] BuildOpenBody(uint getUIPanelMethodSpec, uint selectUidToken, uint openToken)
    {
        var code = new byte[16];
        code[0] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(getUIPanelMethodSpec).CopyTo(code, 1);
        code[5] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(selectUidToken).CopyTo(code, 6);
        code[10] = (byte)OpCodes.Callvirt.Value;
        BitConverter.GetBytes(openToken).CopyTo(code, 11);
        code[15] = (byte)OpCodes.Ret.Value;
        return CompactIlBody.BuildTiny(code);
    }

    private static DefaultAssemblyResolver CreateResolver()
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        return resolver;
    }
}
