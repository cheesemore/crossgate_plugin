using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 倍速检测上报拦截（kill-report，独立补丁）：
/// 把 GmManager.CheckTimeScaleWarning 与 NetManager.SendTimeScaleWarning 打成空方法（首指令 ret）。
///
/// 背景：客户端内置倍速检测（StartTimeScaleCheck 定时调 CheckTimeScaleWarning，
/// 战斗中读 BattleTimeScale&gt;1.0 阈值约 1.00003 即触发 SendTimeScaleWarning
/// Web 上报：HTTP + MD5 签名 + 时间戳）。默认组合（即使加速关）也拦截上报出口，
/// 避免任何倍速/变速相关改动触发服务端检测上报。
///
/// 幂等：已为空方法时跳过。与 VipTimeScaleIlPatcher / CombatAccelIlPatcher 自带
/// kill-report 逻辑并存（三者互相跳过）。
/// </summary>
internal static class KillTimeScaleReportIlPatcher
{
    private const string CommandName = "kill-timescale-report-patch";

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var detect = false;

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
                    detect = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                $"用法: HotfixPatcher {CommandName} --hotfix <orig> --output <out>\n" +
                $"      HotfixPatcher {CommandName} --hotfix <file> --detect");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "kill-timescale-report" : "original");
            return patched ? 0 : 1;
        }

        try
        {
            Apply(source, output);
            Console.WriteLine($"[OK] 倍速上报拦截补丁完成: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static bool IsPatched(string hotfixPath)
    {
        var data = File.ReadAllBytes(hotfixPath);
        using var asm = AssemblyDefinition.ReadAssembly(
            hotfixPath,
            new ReaderParameters { InMemory = true });
        var check = FindMethod(asm.MainModule, "GmManager", "CheckTimeScaleWarning");
        var send = FindMethod(asm.MainModule, "NetManager", "SendTimeScaleWarning");
        return check != null && send != null
            && IsEarlyReturn(ReadMethodBodyFromPe(data, check.RVA))
            && IsEarlyReturn(ReadMethodBodyFromPe(data, send.RVA));
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var data = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(data);

        using var asm = AssemblyDefinition.ReadAssembly(
            sourcePath,
            new ReaderParameters { InMemory = true });

        var wrote = false;
        foreach (var (method, label) in new[]
                 {
                     (Method: FindMethod(asm.MainModule, "GmManager", "CheckTimeScaleWarning"),
                         Label: "GmManager.CheckTimeScaleWarning"),
                     (Method: FindMethod(asm.MainModule, "NetManager", "SendTimeScaleWarning"),
                         Label: "NetManager.SendTimeScaleWarning"),
                 })
        {
            if (method == null)
            {
                Console.WriteLine($"[WARN] 未找到 {label}，跳过");
                continue;
            }

            if (method.ReturnType.FullName != "System.Void")
            {
                throw new InvalidOperationException(
                    $"{label} 返回类型 {method.ReturnType.FullName} 非 void，拒绝打成空方法");
            }

            var body = ReadMethodBodyFromPe(data, method.RVA);
            if (IsEarlyReturn(body))
            {
                Console.WriteLine($"[PATCH] {label} 已是空方法（跳过）");
                continue;
            }

            PatchEarlyReturnInPlace(body);
            BinaryPeWriter.ReplaceMethodBody(data, method.RVA, body, body);
            wrote = true;
            Console.WriteLine($"[PATCH] {label} 打成空方法（首指令 ret），上报出口已掐断");
        }

        if (!wrote)
        {
            throw new InvalidOperationException("倍速上报拦截补丁可能已打过（两个方法均已为空方法）");
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static MethodDefinition? FindMethod(ModuleDefinition module, string typeName, string methodName)
    {
        foreach (var type in module.Types)
        {
            if (type.Name != typeName)
            {
                continue;
            }

            return type.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody);
        }

        return null;
    }

    private static bool IsEarlyReturn(byte[] methodBody)
    {
        var codeOffset = GetCodeOffset(methodBody);
        return codeOffset < methodBody.Length && methodBody[codeOffset] == (byte)OpCodes.Ret.Value;
    }

    private static void PatchEarlyReturnInPlace(byte[] methodBody)
    {
        var codeOffset = GetCodeOffset(methodBody);
        if (codeOffset >= methodBody.Length)
        {
            throw new InvalidOperationException("方法体过短，无法写入 ret");
        }

        methodBody[codeOffset] = (byte)OpCodes.Ret.Value;
        for (var i = codeOffset + 1; i < methodBody.Length; i++)
        {
            methodBody[i] = (byte)OpCodes.Nop.Value;
        }
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

        throw new InvalidOperationException($"无法解析方法体头部 flags=0x{flags:X2} (rva=0x{rva:X})");
    }

    private static int GetCodeOffset(byte[] methodBody)
    {
        var flags = methodBody[0];
        if ((flags & 0x3) == 0x2)
        {
            return 1;
        }

        if ((flags & 0x3) == 0x3)
        {
            return 12;
        }

        throw new InvalidOperationException($"无法解析方法体头 flags=0x{flags:X2}");
    }
}
