using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 战斗加速·方案2（不改变 BattleTimeScale，只加速动画/移动/特效表现）：
/// 1) 近战跑位 BattleRole/&lt;MoveToDestination&gt;d__199.MoveNext：fast 9f→18f，普通 6f→12f
/// 2) 击飞撞墙 BaseAction/&lt;&gt;c__DisplayClass28_0.&lt;KickOff&gt;b__1：count&lt;3 → count&lt;1（撞墙 1 次即停）
///    b__2 WaitUntil count&gt;=3 → count&gt;=1
/// 3) 箭矢 BowAttack/&lt;Attack&gt;d__11.MoveNext：arrowSpeed 12f → 24f
/// 4) 气功弹 BombAttack/&lt;Attack&gt;d__10.MoveNext：arrowSpeed 6f → 12f
/// 5) 慢放清除：所有 SetAllRoleTimeScale(参数∈(0,1)) → (1f)，覆盖击杀后减速 0.125f
///    与技能结束后的 0.1f 慢放，全部改为 1（不慢放）。0f 暂停/恢复不动。
/// 6) 上报屏蔽（强制，不可关闭）：GmManager.CheckTimeScaleWarning + NetManager.SendTimeScaleWarning → 首指令 ret。
///    与 VipTimeScaleIlPatcher 相同：任何加速改动强制绑定 kill-report，防倍速检测上报。
/// </summary>
internal static class CombatAccelIlPatcher
{
    private const float RunFastOriginal = 9f;
    private const float RunFastNew = 18f;
    private const float RunNormalOriginal = 6f;
    private const float RunNormalNew = 12f;
    private const float ArrowOriginal = 12f;
    private const float ArrowNew = 24f;
    private const float BombOriginal = 6f;
    private const float BombNew = 12f;
    private const float DeathSlowNew = 1f;

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
            Console.WriteLine(
                "用法: HotfixPatcher combat-accel-patch --hotfix <orig> --output <out>");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 战斗加速方案2补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("可能已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
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

        var targets = CollectTargets(asm, origBytes, out var allAlreadyDone, out var killDone);

        if (allAlreadyDone && killDone)
        {
            throw new InvalidOperationException("战斗加速方案2补丁可能已打过（跑位+击飞+箭矢+气功弹+慢放清除+上报掐断）");
        }

        foreach (var target in targets)
        {
            if (target.AlreadyDone)
            {
                continue;
            }

            ApplyTarget(data, origBytes, target);
        }

        // 上报屏蔽（强制 kill-report）：复用 VipTimeScale 的成空方法逻辑
        var wroteKill = false;
        if (!killDone)
        {
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
                wroteKill = true;
                Console.WriteLine($"[PATCH] {label} 打成空方法（首指令 ret），上报出口已掐断");
            }
        }

        if (!targets.Any(t => t.Wrote) && !wroteKill)
        {
            throw new InvalidOperationException("战斗加速方案2补丁可能已打过（跑位+击飞+箭矢+气功弹+慢放清除+上报掐断）");
        }

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private sealed class Target
    {
        public MethodDefinition Method = null!;
        public string Label = "";
        public (float Old, float New)? LdcR4;
        public (byte Old, byte New)? LdcI4;
        public bool AlreadyDone;
        public bool Wrote;
    }

    private static List<Target> CollectTargets(
        AssemblyDefinition asm,
        byte[] origBytes,
        out bool allAlreadyDone,
        out bool killDone)
    {
        var list = new List<Target>();

        // 1) 近战跑位（MoveToDestination d__199.MoveNext）
        var battleRole = asm.MainModule.Types.First(t => t.Name == "BattleRole");
        var moveD199 = battleRole.NestedTypes.FirstOrDefault(t => t.Name.Contains("MoveToDestination") && t.Name.Contains("d__"))
            ?? throw new InvalidOperationException("未找到 BattleRole/<MoveToDestination>d__* 状态机");
        var moveNext = moveD199.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MoveToDestination.MoveNext");
        list.Add(BuildR4Target(origBytes, moveNext, "近战跑位 fast 9->18", RunFastOriginal, RunFastNew));
        list.Add(BuildR4Target(origBytes, moveNext, "近战跑位 普通 6->12", RunNormalOriginal, RunNormalNew));

        // 2) 击飞撞墙（KickOff b__1 count<3、b__2 WaitUntil count>=3）
        var baseAction = asm.MainModule.Types.First(t => t.Name == "BaseAction");
        var kickB1 = FindKickOffLambda(baseAction, "<KickOff>b__1")
            ?? throw new InvalidOperationException("未找到 BaseAction/<KickOff>b__1");
        list.Add(BuildI4Target(origBytes, kickB1, "击飞撞墙 count<3->count<1", 3, 1));
        var kickB2 = FindKickOffLambda(baseAction, "<KickOff>b__2")
            ?? throw new InvalidOperationException("未找到 BaseAction/<KickOff>b__2");
        list.Add(BuildI4Target(origBytes, kickB2, "击飞 WaitUntil count>=3->count>=1", 3, 1));

        // 3) 箭矢（BowAttack/<Attack>d__11.MoveNext arrowSpeed 12->24）
        var bow = asm.MainModule.Types.First(t => t.Name == "BowAttack");
        var bowAttack = bow.NestedTypes.FirstOrDefault(t => t.Name.Contains("<Attack>") && t.Name.Contains("d__"))
            ?? throw new InvalidOperationException("未找到 BowAttack/<Attack>d__* 状态机");
        var bowNext = bowAttack.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 BowAttack/<Attack>d__*.MoveNext");
        list.Add(BuildR4Target(origBytes, bowNext, "箭矢速度 12->24", ArrowOriginal, ArrowNew));

        // 4) 气功弹（BombAttack/<Attack>d__10.MoveNext arrowSpeed 6->12）
        var bomb = asm.MainModule.Types.First(t => t.Name == "BombAttack");
        var bombAttack = bomb.NestedTypes.FirstOrDefault(t => t.Name.Contains("<Attack>") && t.Name.Contains("d__"))
            ?? throw new InvalidOperationException("未找到 BombAttack/<Attack>d__* 状态机");
        var bombNext = bombAttack.Methods.FirstOrDefault(m => m.Name == "MoveNext" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 BombAttack/<Attack>d__*.MoveNext");
        list.Add(BuildR4Target(origBytes, bombNext, "气功弹速度 6->12", BombOriginal, BombNew));

        // 5) 慢放清除：全局找 SetAllRoleTimeScale(参数∈(0,1))，改成 (1f)
        var deathTargets = FindDeathSlowdowns(origBytes, asm);
        list.AddRange(deathTargets);

        allAlreadyDone = list.All(t => t.AlreadyDone);

        // 6) 上报屏蔽
        var checkWarn = FindMethod(asm.MainModule, "GmManager", "CheckTimeScaleWarning");
        var sendWarn = FindMethod(asm.MainModule, "NetManager", "SendTimeScaleWarning");
        killDone = checkWarn != null && sendWarn != null
            && IsEarlyReturn(ReadMethodBodyFromPe(origBytes, checkWarn.RVA))
            && IsEarlyReturn(ReadMethodBodyFromPe(origBytes, sendWarn.RVA));

        return list;
    }

    private static Target BuildR4Target(byte[] origBytes, MethodDefinition method, string label, float oldVal, float newVal)
    {
        var body = ReadMethodBodyFromPe(origBytes, method.RVA);
        var count = CountLdcR4(body, oldVal);
        return new Target
        {
            Method = method,
            Label = label,
            LdcR4 = (oldVal, newVal),
            AlreadyDone = count == 0,
        };
    }

    private static Target BuildI4Target(byte[] origBytes, MethodDefinition method, string label, int oldVal, int newVal)
    {
        var body = ReadMethodBodyFromPe(origBytes, method.RVA);
        var count = CountLdcI4(body, oldVal);
        return new Target
        {
            Method = method,
            Label = label,
            LdcI4 = ((byte)OpCodeForLdcI4(oldVal), (byte)OpCodeForLdcI4(newVal)),
            AlreadyDone = count == 0,
        };
    }

    private static void ApplyTarget(byte[] data, byte[] origBytes, Target target)
    {
        var body = ReadMethodBodyFromPe(data, target.Method.RVA);
        var wrote = false;
        if (target.LdcR4 is { } r4)
        {
            wrote = PatchAllLdcR4InPlace(body, r4.Old, r4.New);
        }

        if (target.LdcI4 is { } i4)
        {
            wrote = PatchAllLdcI4InPlace(body, i4.Old, i4.New) || wrote;
        }

        if (!wrote)
        {
            Console.WriteLine($"[WARN] {target.Label}: 未找到常量，跳过（可能已打过）");
            return;
        }

        BinaryPeWriter.ReplaceMethodBody(data, target.Method.RVA, body, body);
        target.Wrote = true;
        Console.WriteLine($"[PATCH] {target.Label}: {Describe(target)}");
    }

    private static string Describe(Target target)
    {
        if (target.LdcR4 is { } r4)
        {
            return $"ldc.r4 {r4.Old} -> {r4.New}";
        }

        if (target.LdcI4 is { } i4)
        {
            return $"ldc.i4.{OpCodeValueForLdcI4(i4.Old)} -> ldc.i4.{OpCodeValueForLdcI4(i4.New)}";
        }

        return "?";
    }

    private static List<Target> FindDeathSlowdowns(byte[] origBytes, AssemblyDefinition asm)
    {
        var result = new List<Target>();
        var seen = new HashSet<(MethodDefinition, float)>();

        foreach (var type in AllTypes(asm.MainModule))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var insns = method.Body.Instructions;
                for (var i = 1; i < insns.Count; i++)
                {
                    if (insns[i].OpCode != OpCodes.Call
                        || insns[i].Operand is not MethodReference mr
                        || mr.Name != "SetAllRoleTimeScale")
                    {
                        continue;
                    }

                    var prev = insns[i - 1];
                    if (prev.OpCode != OpCodes.Ldc_R4
                        || prev.Operand is not float f
                        || f <= 0f
                        || f >= 1f)
                    {
                        continue;
                    }

                    if (!seen.Add((method, f)))
                    {
                        continue;
                    }

                    var body = ReadMethodBodyFromPe(origBytes, method.RVA);
                    result.Add(new Target
                    {
                        Method = method,
                        Label = $"慢放 {method.DeclaringType.Name}.{method.Name} {f:0.###}->1",
                        LdcR4 = (f, DeathSlowNew),
                        AlreadyDone = CountLdcR4(body, f) == 0,
                    });
                }
            }
        }

        if (result.Count == 0)
        {
            Console.WriteLine("[WARN] 未找到 SetAllRoleTimeScale 的 (0,1) 慢放调用点（视为已打过慢放清除，跳过）");
        }

        return result;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var nested in CecilHelpers.NestedTypes(t))
            {
                yield return nested;
            }
        }
    }

    private static MethodDefinition? FindKickOffLambda(TypeDefinition baseAction, string methodName)
    {
        foreach (var nested in CecilHelpers.NestedTypes(baseAction))
        {
            if (!nested.Name.Contains("DisplayClass", StringComparison.Ordinal))
            {
                continue;
            }

            var method = nested.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody);
            if (method != null)
            {
                return method;
            }
        }

        return null;
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

    private static int CountLdcR4(byte[] body, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        var codeOffset = GetCodeOffset(body);
        var codeSize = GetCodeSize(body);
        var count = 0;
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (body[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (body[i + 1] == bytes[0] && body[i + 2] == bytes[1]
                && body[i + 3] == bytes[2] && body[i + 4] == bytes[3])
            {
                count++;
            }
        }

        return count;
    }

    private static bool PatchAllLdcR4InPlace(byte[] body, float oldVal, float newVal)
    {
        var oldBytes = BitConverter.GetBytes(oldVal);
        var newBytes = BitConverter.GetBytes(newVal);
        var codeOffset = GetCodeOffset(body);
        var codeSize = GetCodeSize(body);
        var count = 0;
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (body[i] != (byte)OpCodes.Ldc_R4.Value)
            {
                continue;
            }

            if (body[i + 1] != oldBytes[0] || body[i + 2] != oldBytes[1]
                || body[i + 3] != oldBytes[2] || body[i + 4] != oldBytes[3])
            {
                continue;
            }

            newBytes.CopyTo(body, i + 1);
            count++;
        }

        return count > 0;
    }

    private static int CountLdcI4(byte[] body, int value)
    {
        var op = (byte)OpCodeForLdcI4(value);
        var codeOffset = GetCodeOffset(body);
        var codeSize = GetCodeSize(body);
        var count = 0;
        for (var i = codeOffset; i < codeOffset + codeSize; i++)
        {
            if (body[i] == op)
            {
                count++;
            }
        }

        return count;
    }

    private static bool PatchAllLdcI4InPlace(byte[] body, byte oldOp, byte newOp)
    {
        var codeOffset = GetCodeOffset(body);
        var codeSize = GetCodeSize(body);
        var count = 0;
        for (var i = codeOffset; i < codeOffset + codeSize; i++)
        {
            if (body[i] == oldOp)
            {
                body[i] = newOp;
                count++;
            }
        }

        return count > 0;
    }

    private static byte OpCodeForLdcI4(int value)
    {
        return value switch
        {
            0 => (byte)OpCodes.Ldc_I4_0.Value,
            1 => (byte)OpCodes.Ldc_I4_1.Value,
            2 => (byte)OpCodes.Ldc_I4_2.Value,
            3 => (byte)OpCodes.Ldc_I4_3.Value,
            4 => (byte)OpCodes.Ldc_I4_4.Value,
            5 => (byte)OpCodes.Ldc_I4_5.Value,
            6 => (byte)OpCodes.Ldc_I4_6.Value,
            7 => (byte)OpCodes.Ldc_I4_7.Value,
            8 => (byte)OpCodes.Ldc_I4_8.Value,
            _ => throw new InvalidOperationException($"仅支持 ldc.i4 0..8，实际: {value}"),
        };
    }

    private static string OpCodeValueForLdcI4(byte op)
    {
        return op switch
        {
            0x16 => "0",
            0x17 => "1",
            0x18 => "2",
            0x19 => "3",
            0x1A => "4",
            0x1B => "5",
            0x1C => "6",
            0x1D => "7",
            0x1E => "8",
            _ => "?",
        };
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

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }
}
