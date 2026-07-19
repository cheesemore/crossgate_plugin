using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// VIP 加速：改 get_BattleTimeScale 中 ldc.r4 1.5 → 3/5/10；可选 --non-vip 将默认 1.0 改为同倍速。
/// 心跳 Echo.Speed 固定按 1.5 倍上报。
/// </summary>
internal static class VipTimeScaleIlPatcher
{
    private const float OriginalVipScale = 1.5f;
    private const float EchoReportScale = 1.5f;
    private static readonly float[] AllowedScales = { 3f, 5f, 10f };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var scale = 3f;
        var patchVipBranch = true;
        var patchDefaultBranch = false;

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
                case "--non-vip":
                    patchDefaultBranch = true;
                    break;
                case "--non-vip-only":
                    patchVipBranch = false;
                    patchDefaultBranch = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher vip-timescale-patch --hotfix <orig> --output <out> [--scale 3|5|10] [--non-vip] [--non-vip-only]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, scale, patchVipBranch, patchDefaultBranch);
            Console.WriteLine("[OK] VIP 倍速补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("可能已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(
        string sourcePath,
        string outputPath,
        float battleScale = 3f,
        bool patchVipBranch = true,
        bool patchDefaultBranch = false)
    {
        if (!patchVipBranch && !patchDefaultBranch)
        {
            throw new InvalidOperationException("须至少指定 VIP 分支或 --non-vip/--non-vip-only");
        }

        if (!AllowedScales.Contains(battleScale))
        {
            throw new InvalidOperationException($"战斗倍速须为 3、5 或 10，实际: {battleScale}");
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

        var battleMgr = asm.MainModule.Types.First(t => t.Name == "BattleManager");
        var getScale = battleMgr.Methods.First(m => m.Name == "get_BattleTimeScale" && m.HasBody);
        var netMgr = asm.MainModule.Types.First(t => t.Name == "NetManager");
        var update = netMgr.Methods.First(m => m.Name == "update" && m.HasBody);
        var getBattleTimeScale = getScale;

        var getScaleBody = ReadMethodBodyFromPe(origBytes, getScale.RVA);
        var updateBody = ReadMethodBodyFromPe(origBytes, update.RVA);

        var vipDone = !patchVipBranch || !ContainsLdcR4(getScaleBody, OriginalVipScale);
        var echoDone = !patchVipBranch || IsEchoPatched(updateBody);
        var defaultDone = !patchDefaultBranch || !ContainsLdcR4(getScaleBody, OriginalDefaultScale);

        if (vipDone && echoDone && defaultDone)
        {
            throw new InvalidOperationException("VIP 倍速补丁可能已打过（BattleTimeScale + Echo.Speed）");
        }

        if (patchVipBranch && !PatchBattleTimeScaleInPlace(getScaleBody, battleScale))
        {
            if (!vipDone)
            {
                throw new InvalidOperationException(
                    "未找到 get_BattleTimeScale 的 ldc.r4 1.5（可能已打过补丁）");
            }
        }

        if (patchDefaultBranch && !PatchDefaultBattleTimeScaleInPlace(getScaleBody, battleScale))
        {
            if (!defaultDone)
            {
                throw new InvalidOperationException(
                    "未找到 get_BattleTimeScale 的 ldc.r4 1.0（可能已打过补丁）");
            }
        }

        if (patchVipBranch && !PatchEchoSpeedInPlace(updateBody, getBattleTimeScale))
        {
            if (!echoDone)
            {
                throw new InvalidOperationException("未找到 Echo.Speed 的 BattleTimeScale 读取（可能已打过补丁）");
            }
        }

        var wroteScale = false;
        var wroteEcho = false;

        if (patchVipBranch || patchDefaultBranch)
        {
            BinaryPeWriter.ReplaceMethodBody(data, getScale.RVA, getScaleBody, getScaleBody);
            wroteScale = true;
        }

        if (patchVipBranch)
        {
            BinaryPeWriter.ReplaceMethodBody(data, update.RVA, updateBody, updateBody);
            wroteEcho = true;
        }

        if (!wroteScale && !wroteEcho)
        {
            throw new InvalidOperationException("VIP 倍速补丁可能已打过（BattleTimeScale + Echo.Speed）");
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        if (patchVipBranch)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale VIP 1.5 -> {battleScale}");
        }

        if (patchDefaultBranch)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale 默认 1.0 -> {battleScale}");
        }

        if (patchVipBranch)
        {
            Console.WriteLine($"[PATCH] Echo.Speed 固定上报: {EchoReportScale} x 100 = {(int)(EchoReportScale * 100)}");
        }

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static float ParseScale(string raw)
    {
        if (!float.TryParse(raw, out var value) || !AllowedScales.Contains(value))
        {
            throw new InvalidOperationException($"--scale 须为 3、5 或 10，实际: {raw}");
        }

        return value;
    }

    private static bool PatchBattleTimeScaleInPlace(byte[] methodBody, float battleScale)
    {
        var original = BitConverter.GetBytes(OriginalVipScale);
        var patched = BitConverter.GetBytes(battleScale);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != original[0]
                || methodBody[i + 2] != original[1]
                || methodBody[i + 3] != original[2]
                || methodBody[i + 4] != original[3])
            {
                continue;
            }

            patched.CopyTo(methodBody, i + 1);
            return true;
        }

        return false;
    }

    private static bool PatchEchoSpeedInPlace(byte[] methodBody, MethodReference getBattleTimeScale)
    {
        var echoScale = BitConverter.GetBytes(EchoReportScale);
        var hundred = BitConverter.GetBytes(100f);
        var getScaleToken = BitConverter.GetBytes(getBattleTimeScale.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 16; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldloc_0.Value
                || methodBody[i + 1] != (byte)OpCodes.Call.Value
                || methodBody[i + 6] != (byte)OpCodes.Callvirt.Value
                || methodBody[i + 7] != getScaleToken[0]
                || methodBody[i + 8] != getScaleToken[1]
                || methodBody[i + 9] != getScaleToken[2]
                || methodBody[i + 10] != getScaleToken[3]
                || methodBody[i + 11] != (byte)OpCodes.Ldc_R4.Value
                || methodBody[i + 12] != hundred[0]
                || methodBody[i + 13] != hundred[1]
                || methodBody[i + 14] != hundred[2]
                || methodBody[i + 15] != hundred[3])
            {
                continue;
            }

            methodBody[i + 1] = (byte)OpCodes.Ldc_R4.Value;
            echoScale.CopyTo(methodBody, i + 2);
            methodBody[i + 6] = (byte)OpCodes.Nop.Value;
            methodBody[i + 7] = (byte)OpCodes.Nop.Value;
            methodBody[i + 8] = (byte)OpCodes.Nop.Value;
            methodBody[i + 9] = (byte)OpCodes.Nop.Value;
            methodBody[i + 10] = (byte)OpCodes.Nop.Value;
            return true;
        }

        return false;
    }

    private const float OriginalDefaultScale = 1f;

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

    private static bool PatchDefaultBattleTimeScaleInPlace(byte[] methodBody, float battleScale)
    {
        var original = BitConverter.GetBytes(OriginalDefaultScale);
        var patched = BitConverter.GetBytes(battleScale);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != original[0]
                || methodBody[i + 2] != original[1]
                || methodBody[i + 3] != original[2]
                || methodBody[i + 4] != original[3])
            {
                continue;
            }

            patched.CopyTo(methodBody, i + 1);
            return true;
        }

        return false;
    }

    private static bool IsEchoPatched(byte[] methodBody)
    {
        var echoScale = BitConverter.GetBytes(EchoReportScale);
        var hundred = BitConverter.GetBytes(100f);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 16; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldloc_0.Value
                || methodBody[i + 1] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (methodBody[i + 2] != echoScale[0]
                || methodBody[i + 3] != echoScale[1]
                || methodBody[i + 4] != echoScale[2]
                || methodBody[i + 5] != echoScale[3])
            {
                continue;
            }

            if (methodBody[i + 6] != (byte)OpCodes.Nop.Value
                || methodBody[i + 11] != (byte)OpCodes.Ldc_R4.Value
                || methodBody[i + 12] != hundred[0]
                || methodBody[i + 13] != hundred[1]
                || methodBody[i + 14] != hundred[2]
                || methodBody[i + 15] != hundred[3])
            {
                continue;
            }

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
