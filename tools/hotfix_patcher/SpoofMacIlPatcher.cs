using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;

namespace CrossgateMod.Patcher;

/// <summary>
/// 假 MAC / 设备指纹伪装（2026-08 起，配合 VIP 倍速补丁使用）。
/// 背景：即使掐断倍速上报（CheckTimeScaleWarning / SendTimeScaleWarning），
/// 设备指纹仍会随其它消息上报：
///   - NetManager.OnInit 把 deviceId / device 写入 m_WebFrom 模板，所有 Web 请求复用；
///   - LoginManager.SendLogin 把 DeviceId / Device 写入 Proto_CS_Login 登录协议。
/// 其中 deviceId = MD5(device)，device = 网卡MAC + SystemInfo.deviceUniqueIdentifier。
/// 本补丁把这两处对 AppManager::GetMacAddress/GetMacInfo 的 4 个 call
/// 原位替换为 ldstr 假值（call 与 ldstr 同为 5 字节，方法体长度不变），
/// 使客户端全部设备指纹上报都使用假 MAC/假 UUID，避免真实指纹被反追踪。
/// </summary>
internal static class SpoofMacIlPatcher
{
    private const uint GetMacAddressToken = 0x0A000742;
    private const uint GetMacInfoToken = 0x0A000743;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        string? fakeMac = null;

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
                case "--fake-mac" when i + 1 < args.Length:
                    fakeMac = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher spoof-mac-patch --hotfix <orig> --output <out> [--fake-mac AA-BB-CC-DD-EE-FF]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, fakeMac);
            Console.WriteLine("[OK] 假设备指纹补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath, string? fakeMac = null)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        HotfixSize.Require(origBytes);
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

        if (!ApplyToData(data, asm, origBytes, fakeMac, out var note))
        {
            throw new InvalidOperationException("假设备指纹补丁可能已打过");
        }

        HotfixSize.EnsureUnchanged(data, origBytes.Length);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] " + note);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    /// <summary>
    /// 在已克隆的数据字节上执行替换。返回 false 表示已打过（无需再改）。
    /// </summary>
    public static bool ApplyToData(
        byte[] data,
        AssemblyDefinition asm,
        byte[] origBytes,
        string? fakeMac,
        out string note)
    {
        var sites = LocateCallSites(asm, origBytes);
        if (sites.Count != 4)
        {
            throw new InvalidOperationException(
                $"预期 4 处 MAC 调用点（OnInit×2 + SendLogin×2），实际 {sites.Count} 处，可能客户端已更新");
        }

        if (IsAlreadyPatched(sites))
        {
            note = "假设备指纹：已是补丁状态（跳过）";
            return false;
        }

        var (device, deviceId) = GenerateFakeDevice(fakeMac);
        var userStrings = UserStringHeap.FromPe(data);
        var deviceToken = userStrings.AppendToken(device);
        var deviceIdToken = userStrings.AppendToken(deviceId);
        userStrings.SealHeapTail();

        foreach (var site in sites)
        {
            // call (0x28) -> ldstr (0x72)，token 换成 #US 假值 token
            data[site.FileOffset] = (byte)OpCodes.Ldstr.Value;
            var tokenBytes = BitConverter.GetBytes(
                site.CallName == "GetMacAddress" ? deviceIdToken : deviceToken);
            tokenBytes.CopyTo(data, site.FileOffset + 1);
        }

        note = $"假设备指纹已注入：MAC={device[..17]} UUID={device[17..]}（deviceId={deviceId}）";
        return true;
    }

    public static bool IsPatched(byte[] pe, AssemblyDefinition asm)
    {
        var sites = LocateCallSites(asm, pe);
        return sites.Count == 4 && IsAlreadyPatched(sites);
    }

    private static bool IsAlreadyPatched(List<CallSite> sites)
    {
        return sites.All(s => s.Pe[s.FileOffset] == (byte)OpCodes.Ldstr.Value);
    }

    private static List<CallSite> LocateCallSites(AssemblyDefinition asm, byte[] pe)
    {
        var list = new List<CallSite>();
        var netMgr = asm.MainModule.Types.First(t => t.Name == "NetManager");
        var onInit = netMgr.Methods.First(m => m.Name == "OnInit" && m.HasBody);
        var loginMgr = asm.MainModule.Types.First(t => t.Name == "LoginManager");
        var sendLogin = loginMgr.Methods.First(m => m.Name == "SendLogin" && m.HasBody);

        // OnInit：ldstr "deviceId"/"device" 的下一条指令即为 MAC 调用/替换点
        CollectOnInitSites(list, onInit, pe);
        // SendLogin：set_DeviceId/set_Device 的上一条指令即为 MAC 调用/替换点
        CollectSendLoginSites(list, sendLogin, pe);

        return list;
    }

    private static void CollectOnInitSites(List<CallSite> list, MethodDefinition onInit, byte[] pe)
    {
        var insns = onInit.Body.Instructions;
        for (var i = 0; i < insns.Count - 1; i++)
        {
            if (insns[i].OpCode != OpCodes.Ldstr || insns[i].Operand is not string key)
            {
                continue;
            }

            var target = key switch
            {
                "deviceId" => "GetMacAddress",
                "device" => "GetMacInfo",
                _ => null,
            };
            if (target == null)
            {
                continue;
            }

            var macInsn = insns[i + 1];
            VerifyMacInsn(macInsn, target, "NetManager.OnInit");
            list.Add(ToSite(pe, onInit, macInsn, target, "NetManager.OnInit"));
        }
    }

    private static void CollectSendLoginSites(List<CallSite> list, MethodDefinition sendLogin, byte[] pe)
    {
        var insns = sendLogin.Body.Instructions;
        for (var i = 1; i < insns.Count; i++)
        {
            if (insns[i].OpCode != OpCodes.Callvirt
                || insns[i].Operand is not MethodReference setter)
            {
                continue;
            }

            var target = setter.Name switch
            {
                "set_DeviceId" => "GetMacAddress",
                "set_Device" => "GetMacInfo",
                _ => null,
            };
            if (target == null)
            {
                continue;
            }

            var macInsn = insns[i - 1];
            VerifyMacInsn(macInsn, target, "LoginManager.SendLogin");
            list.Add(ToSite(pe, sendLogin, macInsn, target, "LoginManager.SendLogin"));
        }
    }

    private static void VerifyMacInsn(Instruction macInsn, string callName, string owner)
    {
        var isCall = macInsn.OpCode == OpCodes.Call
            && macInsn.Operand is MethodReference mr
            && mr.Name == callName;
        var isLdstr = macInsn.OpCode == OpCodes.Ldstr;
        if (!isCall && !isLdstr)
        {
            throw new InvalidOperationException(
                $"{owner} 中 {callName} 调用点异常（预期 call 或 ldstr，实际 {macInsn.OpCode}），客户端可能已更新");
        }
    }

    private static CallSite ToSite(
        byte[] pe,
        MethodDefinition method,
        Instruction macInsn,
        string callName,
        string owner)
    {
        var fileOffset = PeLayout.RvaToOffset(pe, (int)method.RVA)
            + GetCodeOffset(pe, (int)method.RVA)
            + macInsn.Offset;
        return new CallSite
        {
            Pe = pe,
            FileOffset = fileOffset,
            CallName = callName,
            Owner = owner,
            IlOffset = macInsn.Offset,
        };
    }

    private static int GetCodeOffset(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        return (flags & 0x3) switch
        {
            0x2 => 1,
            0x3 => 12,
            _ => throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}"),
        };
    }

    /// <summary>
    /// 生成与官方格式一致的假设备指纹：
    /// device = "AA-BB-CC-DD-EE-FF" + 32位hex UUID，deviceId = MD5(device) 小写hex。
    /// 未指定 fakeMac 时随机生成（每次补丁不同）。
    /// </summary>
    public static (string Device, string DeviceId) GenerateFakeDevice(string? fakeMac = null)
    {
        byte[] mac = ParseMac(fakeMac);
        var macStr = string.Join("-", mac.Select(b => b.ToString("X2")));

        Span<byte> uuidBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(uuidBytes);
        var uuid = Convert.ToHexString(uuidBytes).ToLowerInvariant();

        var device = macStr + uuid;
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(device));
        var deviceId = Convert.ToHexString(md5).ToLowerInvariant();
        return (device, deviceId);
    }

    private static byte[] ParseMac(string? fakeMac)
    {
        if (string.IsNullOrWhiteSpace(fakeMac))
        {
            var buf = new byte[6];
            RandomNumberGenerator.Fill(buf);
            return buf;
        }

        var parts = fakeMac.Split(['-', ':', '.']);
        if (parts.Length != 6)
        {
            throw new InvalidOperationException($"--fake-mac 须为 AA-BB-CC-DD-EE-FF 格式，实际: {fakeMac}");
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                throw new InvalidOperationException($"--fake-mac 含非法字节: {fakeMac}");
            }
        }

        return bytes;
    }

    private sealed class CallSite
    {
        public required byte[] Pe { get; init; }
        public required int FileOffset { get; init; }
        public required string CallName { get; init; }
        public required string Owner { get; init; }
        public required int IlOffset { get; init; }
    }
}
