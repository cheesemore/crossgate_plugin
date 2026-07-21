using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 加速过场：CrossBlocks.duration 0.8f → 0.4/0.2/0.1（进出战斗十字格过场）。
/// 原地改 ldc.r4，不占 VA 间隙。
/// </summary>
internal static class TransitionSpeedIlPatcher
{
    private const float OriginalDuration = 0.8f;
    private static readonly float[] AllowedDurations = { 0.4f, 0.2f, 0.1f };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var duration = 0.2f;

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
                case "--scale" when i + 1 < args.Length:
                    duration = ParseDuration(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher transition-speed-patch --hotfix <hotfix> --output <out> [--scale 0.4|0.2|0.1]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, duration);
            Console.WriteLine("[OK] 加速过场补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("可能已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath, float duration = 0.2f)
    {
        if (!AllowedDurations.Contains(duration))
        {
            throw new InvalidOperationException($"过场时长须为 0.4、0.2 或 0.1，实际: {duration}");
        }

        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);
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

        var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == "CrossBlocks")
            ?? throw new InvalidOperationException("未找到类型 CrossBlocks");
        var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.HasBody && !m.HasParameters)
            ?? throw new InvalidOperationException("未找到 CrossBlocks..ctor");
        var body = ReadMethodBodyFromPe(origBytes, ctor.RVA);

        if (IsDurationPatched(body, duration))
        {
            throw new InvalidOperationException($"加速过场补丁可能已打过（CrossBlocks.duration 已是 {duration}）");
        }

        if (!PatchDurationInPlace(body, duration))
        {
            throw new InvalidOperationException(
                $"未找到 CrossBlocks..ctor 的 ldc.r4 {OriginalDuration}（或其它档位）（可能 IL 已变化）");
        }

        BinaryPeWriter.ReplaceMethodBody(data, ctor.RVA, body, body);
        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[PATCH] CrossBlocks.duration -> {duration}");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static float ParseDuration(string raw)
    {
        if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || !AllowedDurations.Any(d => Math.Abs(d - value) < 0.001f))
        {
            throw new InvalidOperationException($"--scale 须为 0.4、0.2 或 0.1，实际: {raw}");
        }

        return AllowedDurations.First(d => Math.Abs(d - value) < 0.001f);
    }

    private static bool PatchDurationInPlace(byte[] methodBody, float duration)
    {
        if (ContainsLdcR4(methodBody, OriginalDuration))
        {
            return ReplaceFirstLdcR4(methodBody, OriginalDuration, duration);
        }

        foreach (var scale in AllowedDurations)
        {
            if (Math.Abs(scale - duration) < 0.001f)
            {
                continue;
            }

            if (ContainsLdcR4(methodBody, scale) && ReplaceFirstLdcR4(methodBody, scale, duration))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDurationPatched(byte[] methodBody, float duration)
    {
        if (ContainsLdcR4(methodBody, OriginalDuration))
        {
            return false;
        }

        return ContainsLdcR4(methodBody, duration);
    }

    private static bool ContainsLdcR4(byte[] methodBody, float value)
    {
        var pattern = BitConverter.GetBytes(value);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (methodBody[i + 1] == pattern[0]
                && methodBody[i + 2] == pattern[1]
                && methodBody[i + 3] == pattern[2]
                && methodBody[i + 4] == pattern[3])
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReplaceFirstLdcR4(byte[] methodBody, float from, float to)
    {
        var fromBytes = BitConverter.GetBytes(from);
        var toBytes = BitConverter.GetBytes(to);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != fromBytes[0]
                || methodBody[i + 2] != fromBytes[1]
                || methodBody[i + 3] != fromBytes[2]
                || methodBody[i + 4] != fromBytes[3])
            {
                continue;
            }

            toBytes.CopyTo(methodBody, i + 1);
            return true;
        }

        return false;
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

        return BitConverter.ToInt32(methodBody, 4);
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
}
