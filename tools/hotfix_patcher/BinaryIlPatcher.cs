using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class BinaryIlPatcher
{
    private const uint IsMsgLogFieldToken = 0x0A000437;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var enableMsgLog = false;

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
                case "--enable-msglog":
                    enableMsgLog = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source) || !enableMsgLog)
        {
            Console.WriteLine("用法: HotfixPatcher binary-patch --hotfix <orig> --output <out> --enable-msglog");
            return 1;
        }

        output ??= source;
        PatchEnableMsgLog(source, output);
        Console.WriteLine("[OK] 二进制 IL 补丁完成: " + output);
        return 0;
    }

    private static void PatchEnableMsgLog(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
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

        var gameManager = asm.MainModule.Types.FirstOrDefault(t => t.Name == "GameManagerHotfix")
            ?? throw new InvalidOperationException("未找到 GameManagerHotfix");
        var start = gameManager.Methods.FirstOrDefault(m => m.Name == "Start" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 GameManagerHotfix.Start");

        var isMsgLog = (FieldReference)asm.MainModule.LookupToken((int)IsMsgLogFieldToken);

        var injectBefore = start.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Callvirt
            && i.Operand is MethodReference called
            && called.Name == "Dispatch")
            ?? throw new InvalidOperationException("未找到 ScreenTransiton.Dispatch 调用点");

        var oldBodySnapshot = IlSerializer.Serialize(start.Body);

        var il = start.Body.GetILProcessor();
        il.InsertBefore(injectBefore, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(injectBefore, il.Create(OpCodes.Stsfld, isMsgLog));

        IlSerializer.RecalculateOffsets(start.Body);
        start.Body.MaxStackSize = Math.Max(start.Body.MaxStackSize, (short)8);
        var newBody = IlSerializer.Serialize(start.Body);
        BinaryPeWriter.ReplaceMethodBody(data, start.RVA, oldBodySnapshot, newBody);

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException("二进制补丁改变了文件大小，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static int GetMethodBodyLength(byte[] pe, int rva)
    {
        var off = RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            return 1 + (flags >> 2);
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(pe, off + 4);
            return 12 + codeSize;
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }

    private static int FindTextAppendOffset(byte[] pe)
    {
        var text = GetSection(pe, ".text");
        var used = (int)text.VirtualSize;
        var append = (int)(text.PointerToRawData + used);
        var slack = (int)(text.SizeOfRawData - used);
        if (slack < 128)
        {
            throw new InvalidOperationException($".text 节区余量不足: {slack} 字节");
        }

        return append;
    }

    private static void PatchMethodRva(byte[] pe, int oldRva, int newRva)
    {
        var pattern = BitConverter.GetBytes(oldRva);
        var hits = 0;
        int hitOff = -1;
        for (var i = 0; i <= pe.Length - 4; i++)
        {
            if (pe[i] != pattern[0] || pe[i + 1] != pattern[1] || pe[i + 2] != pattern[2] || pe[i + 3] != pattern[3])
            {
                continue;
            }

            hits++;
            hitOff = i;
        }

        if (hits != 1)
        {
            throw new InvalidOperationException($"MethodDef RVA 0x{oldRva:X} 在文件中出现 {hits} 次，无法安全修补");
        }

        BitConverter.GetBytes(newRva).CopyTo(pe, hitOff);
        Console.WriteLine($"[META] MethodDef RVA 0x{oldRva:X} -> 0x{newRva:X} (file 0x{hitOff:X})");
    }

    private static PeSection GetSection(byte[] pe, string name)
    {
        return ParseSections(pe).First(s => s.Name == name);
    }

    private static List<PeSection> ParseSections(byte[] pe)
    {
        var peOff = BitConverter.ToInt32(pe, 0x3C);
        var num = BitConverter.ToUInt16(pe, peOff + 6);
        var optSize = BitConverter.ToUInt16(pe, peOff + 20);
        var secOff = peOff + 24 + optSize;
        var list = new List<PeSection>();
        for (var i = 0; i < num; i++)
        {
            var off = secOff + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(pe, off, 8).TrimEnd('\0');
            list.Add(new PeSection
            {
                Name = secName,
                VirtualSize = BitConverter.ToUInt32(pe, off + 8),
                VirtualAddress = BitConverter.ToUInt32(pe, off + 12),
                SizeOfRawData = BitConverter.ToUInt32(pe, off + 16),
                PointerToRawData = BitConverter.ToUInt32(pe, off + 20),
            });
        }

        return list;
    }

    private static int RvaToOffset(byte[] pe, int rva)
    {
        foreach (var sec in ParseSections(pe))
        {
            if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + sec.VirtualSize)
            {
                return (int)(rva - sec.VirtualAddress + sec.PointerToRawData);
            }
        }

        throw new InvalidOperationException($"RVA 0x{rva:X} 无法映射");
    }

    private static int OffsetToRva(byte[] pe, int offset)
    {
        foreach (var sec in ParseSections(pe))
        {
            if (offset >= sec.PointerToRawData && offset < sec.PointerToRawData + sec.SizeOfRawData)
            {
                return (int)(offset - sec.PointerToRawData + sec.VirtualAddress);
            }
        }

        throw new InvalidOperationException($"offset 0x{offset:X} 无法映射");
    }

    private sealed class PeSection
    {
        public required string Name { get; init; }
        public uint VirtualSize { get; init; }
        public uint VirtualAddress { get; init; }
        public uint SizeOfRawData { get; init; }
        public uint PointerToRawData { get; init; }
    }
}
