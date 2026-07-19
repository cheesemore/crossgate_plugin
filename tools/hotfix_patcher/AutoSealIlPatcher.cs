using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 自动封印：二进制 IL 补丁，保持 hotfix 体积 6355968 不变（禁止 Cecil Write）。
/// </summary>
internal static class AutoSealIlPatcher
{
    private const int ExpectedSize = 6_355_968;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;

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
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("用法: HotfixPatcher auto-seal-patch --hotfix <orig> --output <out.autoseal>");
            return 1;
        }

        output ??= source + ".autoseal";
        Apply(source, output);
        Console.WriteLine("[OK] 自动封印补丁已写入: " + output);
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        if (origBytes.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"源文件体积应为 {ExpectedSize}，实际 {origBytes.Length}。请使用 hotfix.dll.bytes.orig");
        }

        var data = (byte[])origBytes.Clone();
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var battleProcesser = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
        // VIP 自动战斗走 DoVipPlayerAutoFight；只补丁一处以控制 .text 扩容。
        var vipPlayerAction = battleProcesser.Methods.First(m => m.Name == "DoVipPlayerAutoFight" && m.HasBody);

        var userStrings = UserStringHeap.FromPe(data);
        var snapshots = new Dictionary<MethodDefinition, byte[]>();
        var dirty = new List<MethodDefinition>();

        void Patch(MethodDefinition method)
        {
            snapshots[method] = ReadMethodBodyFromPe(origBytes, method.RVA);
            AutoSealIlBuilder.PrependSealHook(method, asm.MainModule);
            IlSerializer.RecalculateOffsets(method.Body);
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, (short)32);
            DumpBranchDebug(method);
            dirty.Add(method);
        }

        Patch(vipPlayerAction);

        foreach (var method in dirty.OrderBy(m => m.MetadataToken.ToInt32()))
        {
            var oldRva = method.RVA;
            var newBody = IlSerializer.Serialize(method.Body, userStrings);
            BinaryPeWriter.ReplaceMethodBody(data, oldRva, snapshots[method], newBody);
            var newRva = ReadMethodRvaFromPe(data, method.MetadataToken.ToInt32());
            if (Environment.GetEnvironmentVariable("AUTOSEAL_SKIP_VERIFY") != "1")
            {
                VerifyPatchedMethod(data, method, userStrings, newBody, newRva);
            }
        }

        if (data.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] DoVipPlayerAutoFight 内联封印逻辑（VIP 人物自动段）");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static byte[] ReadMethodBodyFromPe(byte[] pe, int rva)
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
            var len = 12 + codeSize;
            var buf = new byte[len];
            Array.Copy(pe, off, buf, 0, len);
            return buf;
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }

    private static void DumpBranchDebug(MethodDefinition method)
    {
        if (Environment.GetEnvironmentVariable("AUTOSEAL_DEBUG") != "1")
        {
            return;
        }

        foreach (var insn in method.Body.Instructions)
        {
            if (insn.Offset is >= 0x250 and <= 0x2E0)
            {
                Console.WriteLine($"[IL] {insn.Offset:X4} {insn.OpCode.Name} {insn.Operand}");
            }
        }
    }

    private static int ReadMethodRvaFromPe(byte[] pe, int methodToken)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var tmp = new MemoryStream(pe, writable: false);
        using var asm = AssemblyDefinition.ReadAssembly(tmp, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        return asm.MainModule.Types
            .SelectMany(t => t.Methods)
            .First(m => m.MetadataToken.ToInt32() == methodToken)
            .RVA;
    }

    private static void VerifyPatchedMethod(
        byte[] pe,
        MethodDefinition method,
        UserStringHeap userStrings,
        byte[] serializedBody,
        int rva)
    {
        var fileOff = PeLayout.RvaToOffset(pe, rva);
        if (!serializedBody.AsSpan().SequenceEqual(pe.AsSpan(fileOff, serializedBody.Length)))
        {
            throw new InvalidOperationException(
                $"补丁校验失败：RVA 0x{rva:X} 处文件内容与序列化结果不一致");
        }

        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var tmp = new MemoryStream(pe, writable: false);
        using var asm = AssemblyDefinition.ReadAssembly(tmp, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var type = asm.MainModule.Types.First(t => t.FullName == method.DeclaringType.FullName);
        var readBack = type.Methods.First(m => m.MetadataToken.ToInt32() == method.MetadataToken.ToInt32());
        foreach (var insn in readBack.Body.Instructions)
        {
            if (insn.OpCode.FlowControl is FlowControl.Cond_Branch or FlowControl.Branch)
            {
                if (insn.Operand is not Instruction)
                {
                    throw new InvalidOperationException(
                        $"补丁校验失败：{readBack.Name} @ 0x{insn.Offset:X} 分支目标无效 ({insn.OpCode})");
                }
            }
        }

        IlSerializer.Serialize(readBack.Body, userStrings);
        Console.WriteLine($"[VERIFY] {readBack.Name} IL 读回校验通过 (RVA=0x{rva:X})");
    }
}
