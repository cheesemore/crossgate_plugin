using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 地图 Sprint 基础跑速：CharacterEntity.set_Running 中 RUN 6f → 8/10/12（WALK 4f 不变）。
/// 仍叠加 RideSkinGoRun 与月卡 TbAdvVipCardConfig.Speed。
/// </summary>
internal static class MapSprintSpeedIlPatcher
{
    private const float OriginalRunScale = 6f;
    private static readonly float[] AllowedScales = { 8f, 10f, 12f };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var scale = 8f;

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
                    scale = ParseScale(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("用法: HotfixPatcher map-sprint-speed-patch --hotfix <hotfix> --output <out> [--scale 8|10|12]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, scale);
            Console.WriteLine("[OK] 地图 Sprint 跑速补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("可能已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath, float runScale = 8f)
    {
        if (!AllowedScales.Contains(runScale))
        {
            throw new InvalidOperationException($"Sprint 跑速须为 8、10 或 12，实际: {runScale}");
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

        var entity = asm.MainModule.Types.First(t => t.Name == "CharacterEntity");
        var setRunning = entity.Methods.First(m => m.Name == "set_Running" && m.HasBody);
        var body = ReadMethodBodyFromPe(origBytes, setRunning.RVA);

        if (IsRunScalePatched(body, runScale))
        {
            throw new InvalidOperationException("地图 Sprint 跑速补丁可能已打过");
        }

        if (!PatchRunScaleInPlace(body, runScale))
        {
            throw new InvalidOperationException(
                $"未找到 set_Running 的 ldc.r4 {OriginalRunScale}（可能已打过补丁或 IL 已变化）");
        }

        BinaryPeWriter.ReplaceMethodBody(data, setRunning.RVA, body, body);
        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[PATCH] Sprint 基础跑速 {OriginalRunScale} -> {runScale}（WALK 4 不变，仍叠加坐骑/月卡）");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static float ParseScale(string raw)
    {
        if (!float.TryParse(raw, out var value) || !AllowedScales.Contains(value))
        {
            throw new InvalidOperationException($"--scale 须为 8、10 或 12，实际: {raw}");
        }

        return value;
    }

    private static bool PatchRunScaleInPlace(byte[] methodBody, float runScale)
    {
        if (ContainsLdcR4(methodBody, OriginalRunScale))
        {
            return ReplaceFirstLdcR4(methodBody, OriginalRunScale, runScale);
        }

        foreach (var scale in AllowedScales)
        {
            if (Math.Abs(scale - runScale) < 0.001f)
            {
                continue;
            }

            if (ContainsLdcR4(methodBody, scale) && ReplaceFirstLdcR4(methodBody, scale, runScale))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunScalePatched(byte[] methodBody, float runScale)
    {
        if (ContainsLdcR4(methodBody, OriginalRunScale))
        {
            return false;
        }

        return ContainsLdcR4(methodBody, runScale);
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
