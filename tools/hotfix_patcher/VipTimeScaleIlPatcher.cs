using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// VIP 加速：改 get_BattleTimeScale 中 ldc.r4 1.5 → 3/5/10；可选 --non-vip 将默认 1.0 改为同倍速。
/// 心跳 Echo.Speed：仅 VIP 加速时固定报 1.5；启用非 VIP 加速时固定报 1.0（防检测）。
/// 可选 --echo 1.0|1.5 显式指定心跳上报倍率（覆盖上述默认规则）；仅指定 --echo 时不改倍速（仅心跳回传补丁）。
/// 同步：KickOff 打飞速度 MoveSpeed×4 → ×(4×倍速)，如 5x → 20。
/// 防检测（2026-08 起）：官方 GmManager::StartTimeScaleCheck 定时调 CheckTimeScaleWarning，
/// 战斗中读 BattleManager.BattleTimeScale&gt;1.0（阈值约 1.00003）即触发
/// NetManager::SendTimeScaleWarning Web 上报（HTTP + MD5 签名 + 时间戳）。
/// 本补丁强制把 CheckTimeScaleWarning 与 SendTimeScaleWarning 两个方法打成空方法（首指令 ret），
/// 彻底掐断倍速检测上报出口；加速功能强制绑定 kill-report，不可关闭（--no-kill-report 已废弃）。
/// 假设备指纹（--fake-mac）：即使倍速上报被掐断，deviceId/device（MAC+UUID）仍会随
/// m_WebFrom 模板与登录协议 Proto_CS_Login 上报。SpoofMacIlPatcher 可把
/// NetManager.OnInit 与 LoginManager.SendLogin 里对 AppManager::GetMacAddress/GetMacInfo
/// 的 4 处 call 原位替换为 ldstr 假值。
/// 注意：默认关闭（临时放弃，测试改用虚拟机），需要时用 --fake-mac AA-BB-CC-DD-EE-FF
/// 显式开启并指定假 MAC（省略则随机）。
/// </summary>
internal static class VipTimeScaleIlPatcher
{
    private const float OriginalVipScale = 1.5f;
    private const float EchoReportScaleVip = 1.5f;
    private const float EchoReportScaleNonVip = 1.0f;
    private const float OriginalKickOffMul = 4f;
    private static readonly float[] AllowedScales = { 3f, 5f, 10f };
    private static readonly float[] AllowedEchoScales = { EchoReportScaleVip, EchoReportScaleNonVip };
    private static readonly float[] KnownKickOffMuls = { 4f, 12f, 20f, 40f };
    private static readonly float[] KnownEchoReportScales = { EchoReportScaleVip, EchoReportScaleNonVip };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var scale = 3f;
        var patchVipBranch = true;
        var patchDefaultBranch = false;
        float? echoOverride = null;
        var echoOnly = false;
        var killReport = true;  // 强制开启：加速必带上报掐断，不可关闭
        var spoofMac = false;
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
                case "--scale" when i + 1 < args.Length:
                    scale = ParseScale(args[++i]);
                    break;
                case "--echo" when i + 1 < args.Length:
                    echoOverride = ParseEcho(args[++i]);
                    break;
                case "--echo-only":
                    echoOnly = true;
                    break;
                case "--non-vip":
                    patchDefaultBranch = true;
                    break;
                case "--non-vip-only":
                    patchVipBranch = false;
                    patchDefaultBranch = true;
                    break;
                case "--no-kill-report":
                    // 已废弃：加速必带 kill-report（掐断倍速检测上报），忽略该开关
                    Console.WriteLine("[WARN] --no-kill-report 已忽略：加速补丁强制携带 kill-report");
                    break;
                case "--no-fake-mac":
                    spoofMac = false;
                    break;
                case "--fake-mac" when i + 1 < args.Length:
                    spoofMac = true;
                    fakeMac = args[++i];
                    break;
            }
        }

        if (echoOnly)
        {
            patchVipBranch = false;
            patchDefaultBranch = false;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher vip-timescale-patch --hotfix <orig> --output <out> [--scale 3|5|10] [--non-vip] [--non-vip-only] [--echo 1.0|1.5] [--echo-only] [--no-fake-mac] [--fake-mac AA-BB-CC-DD-EE-FF]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, scale, patchVipBranch, patchDefaultBranch, echoOverride, killReport, spoofMac, fakeMac);
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
        bool patchDefaultBranch = false,
        float? echoReportOverride = null,
        bool killReport = true,
        bool spoofMac = false,
        string? fakeMac = null)
    {
        if (!patchVipBranch && !patchDefaultBranch && echoReportOverride == null)
        {
            throw new InvalidOperationException("须至少指定 VIP 分支、--non-vip/--non-vip-only 或 --echo");
        }

        if (!AllowedScales.Contains(battleScale))
        {
            throw new InvalidOperationException($"战斗倍速须为 3、5 或 10，实际: {battleScale}");
        }

        var wantScale = patchVipBranch || patchDefaultBranch;
        var kickOffTarget = OriginalKickOffMul * battleScale;
        // 心跳回传：显式 --echo 优先；否则启用非 VIP 加速时回传 1.0，仅 VIP 加速时回传官方 1.5
        var patchEcho = wantScale || echoReportOverride != null;
        var echoReportScale = echoReportOverride ?? (patchDefaultBranch ? EchoReportScaleNonVip : EchoReportScaleVip);

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
        var gmMgr = asm.MainModule.Types.FirstOrDefault(t => t.Name == "GmManager");
        var checkTimeScaleWarning = gmMgr?.Methods.FirstOrDefault(m => m.Name == "CheckTimeScaleWarning" && m.HasBody);
        var sendTimeScaleWarning = netMgr.Methods.FirstOrDefault(m => m.Name == "SendTimeScaleWarning" && m.HasBody);
        var getBattleTimeScale = getScale;

        // 仅心跳模式（--echo 且未启用倍速分支）：不查 KickOff、不改 BattleTimeScale
        MethodDefinition? kickOffMethod = null;
        if (wantScale)
        {
            kickOffMethod = FindKickOffSpeedMethod(asm.MainModule)
                ?? throw new InvalidOperationException("未找到 KickOff 打飞速度方法 BaseAction/<KickOff>b__*");
        }

        var getScaleBody = ReadMethodBodyFromPe(origBytes, getScale.RVA);
        var updateBody = ReadMethodBodyFromPe(origBytes, update.RVA);

        var vipDone = !patchVipBranch || !ContainsLdcR4(getScaleBody, OriginalVipScale);
        var echoDone = !patchEcho || IsEchoPatched(updateBody, echoReportScale);
        var defaultDone = !patchDefaultBranch || !ContainsLdcR4(getScaleBody, OriginalDefaultScale);
        var kickOffDone = !wantScale;
        if (wantScale)
        {
            var kickOffBody = ReadMethodBodyFromPe(origBytes, kickOffMethod!.RVA);
            kickOffDone = ContainsLdcR4(kickOffBody, kickOffTarget)
                && !ContainsLdcR4(kickOffBody, OriginalKickOffMul);
        }

        var killDone = true;
        if (killReport)
        {
            killDone = checkTimeScaleWarning != null
                && sendTimeScaleWarning != null
                && IsEarlyReturn(ReadMethodBodyFromPe(origBytes, checkTimeScaleWarning.RVA))
                && IsEarlyReturn(ReadMethodBodyFromPe(origBytes, sendTimeScaleWarning.RVA));
        }

        var spoofDone = true;
        if (spoofMac)
        {
            spoofDone = SpoofMacIlPatcher.IsPatched(origBytes, asm);
        }

        if (vipDone && echoDone && defaultDone && kickOffDone && killDone && spoofDone)
        {
            throw new InvalidOperationException("VIP 倍速补丁可能已打过（BattleTimeScale + Echo.Speed + KickOff + 上报掐断 + 假设备指纹）");
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

        var wroteEcho = false;
        if (patchEcho && !echoDone)
        {
            if (!PatchEchoSpeedInPlace(updateBody, getBattleTimeScale, echoReportScale))
            {
                throw new InvalidOperationException("未找到 Echo.Speed 的 BattleTimeScale 读取/旧上报常量（可能已打过补丁）");
            }

            wroteEcho = true;
        }

        var wroteKickOff = false;
        byte[]? patchedKickOffBody = null;
        if (wantScale && !kickOffDone)
        {
            patchedKickOffBody = ReadMethodBodyFromPe(origBytes, kickOffMethod!.RVA);
            if (!PatchKickOffMulInPlace(patchedKickOffBody, kickOffTarget))
            {
                throw new InvalidOperationException(
                    $"未找到 KickOff 的 ldc.r4 打飞倍率（期望原版 {OriginalKickOffMul} 或已知档位）");
            }

            wroteKickOff = true;
        }

        var wroteScale = false;

        if (wantScale)
        {
            BinaryPeWriter.ReplaceMethodBody(data, getScale.RVA, getScaleBody, getScaleBody);
            wroteScale = true;
        }

        if (wroteEcho)
        {
            BinaryPeWriter.ReplaceMethodBody(data, update.RVA, updateBody, updateBody);
        }

        if (wroteKickOff)
        {
            BinaryPeWriter.ReplaceMethodBody(data, kickOffMethod!.RVA, patchedKickOffBody!, patchedKickOffBody!);
        }

        var wroteKill = false;
        if (killReport && !killDone)
        {
            foreach (var (method, label) in new[]
                     {
                         (Method: checkTimeScaleWarning, Label: "GmManager.CheckTimeScaleWarning"),
                         (Method: sendTimeScaleWarning, Label: "NetManager.SendTimeScaleWarning"),
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

                var body = ReadMethodBodyFromPe(origBytes, method.RVA);
                if (IsEarlyReturn(body))
                {
                    Console.WriteLine($"[PATCH] {label} 已是空方法（跳过）");
                    continue;
                }

                PatchEarlyReturnInPlace(body);
                BinaryPeWriter.ReplaceMethodBody(data, method.RVA, body, body);
                wroteKill = true;
                Console.WriteLine($"[PATCH] {label} 打成空方法（首指令 ret），上报出口已掐断");
            }
        }

        var wroteSpoof = false;
        string? spoofNote = null;
        if (spoofMac && !spoofDone)
        {
            if (SpoofMacIlPatcher.ApplyToData(data, asm, origBytes, fakeMac, out var note))
            {
                spoofNote = note;
                wroteSpoof = true;
            }
        }

        if (!wroteScale && !wroteEcho && !wroteKickOff && !wroteKill && !wroteSpoof)
        {
            throw new InvalidOperationException("VIP 倍速补丁可能已打过（BattleTimeScale + Echo.Speed + KickOff + 上报掐断 + 假设备指纹）");
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        if (patchVipBranch && !vipDone)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale VIP 1.5 -> {battleScale}");
        }
        else if (patchVipBranch)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale VIP 已是 {battleScale}（跳过）");
        }

        if (patchDefaultBranch && !defaultDone)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale 默认 1.0 -> {battleScale}");
        }
        else if (patchDefaultBranch)
        {
            Console.WriteLine($"[PATCH] BattleTimeScale 默认 已是 {battleScale}（跳过）");
        }

        if (patchEcho)
        {
            if (wroteEcho)
            {
                var echoNote = echoReportOverride != null
                    ? (echoReportOverride > 1.0f ? "（加速开启·1.5官方倍）" : "（加速关闭·1.0非VIP倍）")
                    : (patchDefaultBranch ? "（非VIP防检测）" : "（VIP官方倍）");
                Console.WriteLine(
                    $"[PATCH] Echo.Speed 固定上报: {echoReportScale} x 100 = {(int)(echoReportScale * 100)}"
                    + echoNote);
            }
            else
            {
                Console.WriteLine(
                    $"[PATCH] Echo.Speed 已是上报 {echoReportScale}x（跳过）");
            }
        }

        if (wantScale)
        {
            if (wroteKickOff)
            {
                Console.WriteLine($"[PATCH] KickOff 打飞速度 ×{OriginalKickOffMul} -> ×{kickOffTarget}（随战斗 {battleScale}x）");
            }
            else
            {
                Console.WriteLine($"[PATCH] KickOff 打飞速度 已是 ×{kickOffTarget}（跳过）");
            }
        }

        if (spoofMac)
        {
            if (wroteSpoof && spoofNote != null)
            {
                Console.WriteLine("[PATCH] " + spoofNote);
            }
            else
            {
                Console.WriteLine("[PATCH] 假设备指纹：已是补丁状态（跳过）");
            }
        }

        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static MethodDefinition? FindKickOffSpeedMethod(ModuleDefinition module)
    {
        var baseAction = module.Types.FirstOrDefault(t => t.Name == "BaseAction");
        if (baseAction == null)
        {
            return null;
        }

        foreach (var nested in baseAction.NestedTypes)
        {
            // 编译器生成：<>c__DisplayClass28_0.<KickOff>b__1
            if (!nested.Name.Contains("DisplayClass", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in nested.Methods.Where(m => m.HasBody && m.Name.Contains("KickOff", StringComparison.Ordinal)))
            {
                var insns = method.Body.Instructions;
                for (var i = 0; i < insns.Count; i++)
                {
                    if (insns[i].OpCode != OpCodes.Callvirt
                        || insns[i].Operand is not MethodReference mr
                        || mr.Name != "get_MoveSpeed")
                    {
                        continue;
                    }

                    // get_MoveSpeed → Vector3.op_Multiply → ldc.r4 N
                    for (var j = i + 1; j < Math.Min(insns.Count, i + 4); j++)
                    {
                        if (insns[j].OpCode == OpCodes.Ldc_R4
                            && insns[j].Operand is float f
                            && KnownKickOffMuls.Any(m => Math.Abs(m - f) < 0.001f))
                        {
                            return method;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>把 KickOff 中 MoveSpeed 后的倍率改为 target（可从原版 4 或已知 12/20/40 改写）。</summary>
    private static bool PatchKickOffMulInPlace(byte[] methodBody, float target)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var targetBytes = BitConverter.GetBytes(target);

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            var value = BitConverter.ToSingle(methodBody, i + 1);
            if (!KnownKickOffMuls.Any(m => Math.Abs(m - value) < 0.001f))
            {
                continue;
            }

            targetBytes.CopyTo(methodBody, i + 1);
            return true;
        }

        return false;
    }

    private static float ParseScale(string raw)
    {
        if (!float.TryParse(raw, out var value) || !AllowedScales.Contains(value))
        {
            throw new InvalidOperationException($"--scale 须为 3、5 或 10，实际: {raw}");
        }

        return value;
    }

    private static float ParseEcho(string raw)
    {
        if (!float.TryParse(raw, out var value) || !AllowedEchoScales.Contains(value))
        {
            throw new InvalidOperationException($"--echo 须为 1.0 或 1.5，实际: {raw}");
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

    private static bool PatchEchoSpeedInPlace(
        byte[] methodBody,
        MethodReference getBattleTimeScale,
        float echoReportScale)
    {
        var echoScale = BitConverter.GetBytes(echoReportScale);
        var hundred = BitConverter.GetBytes(100f);
        var getScaleToken = BitConverter.GetBytes(getBattleTimeScale.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        // 1) 原版：ldloc.0 + call + callvirt get_BattleTimeScale + ldc.r4 100
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

        // 2) 已打过旧上报常量（如 1.5）：改成目标常量（如 1.0）
        foreach (var oldScale in KnownEchoReportScales)
        {
            if (Math.Abs(oldScale - echoReportScale) < 0.001f)
            {
                continue;
            }

            var oldBytes = BitConverter.GetBytes(oldScale);
            for (var i = codeOffset; i <= codeOffset + codeSize - 16; i++)
            {
                if (methodBody[i] != (byte)OpCodes.Ldloc_0.Value
                    || methodBody[i + 1] != (byte)OpCodes.Ldc_R4.Value
                    || methodBody[i + 2] != oldBytes[0]
                    || methodBody[i + 3] != oldBytes[1]
                    || methodBody[i + 4] != oldBytes[2]
                    || methodBody[i + 5] != oldBytes[3]
                    || methodBody[i + 6] != (byte)OpCodes.Nop.Value
                    || methodBody[i + 11] != (byte)OpCodes.Ldc_R4.Value
                    || methodBody[i + 12] != hundred[0]
                    || methodBody[i + 13] != hundred[1]
                    || methodBody[i + 14] != hundred[2]
                    || methodBody[i + 15] != hundred[3])
                {
                    continue;
                }

                echoScale.CopyTo(methodBody, i + 2);
                return true;
            }
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

    private static bool IsEchoPatched(byte[] methodBody, float echoReportScale)
    {
        var echoScale = BitConverter.GetBytes(echoReportScale);
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
