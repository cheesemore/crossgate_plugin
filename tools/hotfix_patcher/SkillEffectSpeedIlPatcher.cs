using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 战斗技能特效帧动画：EffectEntity.Play 在 base.Play() 后将 PlaySpeed 乘以 1.5/2/3/5。
/// 不影响回合读秒、BattleTimeScale、心跳上报。
/// </summary>
internal static class SkillEffectSpeedIlPatcher
{
    private static readonly float[] AllowedScales = { 1.5f, 2f, 3f, 5f };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var scale = 1.5f;

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
            Console.WriteLine(
                "用法: HotfixPatcher skill-effect-speed-patch --hotfix <hotfix> --output <out> [--scale 1.5|2|3|5]");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output, scale);
            Console.WriteLine("[OK] 技能特效加速补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("可能已打过"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath, float effectScale = 1.5f)
    {
        if (!AllowedScales.Contains(effectScale))
        {
            throw new InvalidOperationException($"特效倍速须为 1.5、2、3 或 5，实际: {effectScale}");
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

        var effectEntity = asm.MainModule.Types.First(t => t.Name == "EffectEntity");
        var setSpeed = effectEntity.Methods.First(m => m.Name == "SetSpeed" && m.HasParameters);
        var playMethod = effectEntity.Methods.FirstOrDefault(m =>
            m.Name == "Play" && m.HasBody && m.Parameters.Count == 0 && !m.IsStatic)
            ?? throw new InvalidOperationException("未找到 EffectEntity.Play()");

        var snapshot = ReadMethodBodyFromPe(origBytes, playMethod.RVA);
        string patchMsg;

        if (IsAlreadyPatched(playMethod, effectScale))
        {
            throw new InvalidOperationException("技能特效加速补丁可能已打过");
        }

        if (TryReplaceScaleConstant(playMethod, effectScale))
        {
            patchMsg = $"[PATCH] EffectEntity.Play 特效倍速已改为 × {effectScale}";
        }
        else if (ContainsPatchSequence(playMethod))
        {
            if (FindFixedScaleConstantIndex(playMethod) < 0 && FindLegacyScaleConstantIndex(playMethod) >= 0)
            {
                throw new InvalidOperationException(
                    "检测到旧版技能特效补丁（IL 栈错误），请勾选「从 .orig 重打」或一键还原后再打");
            }

            throw new InvalidOperationException(
                "技能特效加速补丁已存在但无法改倍率，请从 .orig 重打或一键还原");
        }
        else if (!InjectAfterBasePlay(playMethod, setSpeed, effectScale))
        {
            throw new InvalidOperationException("未能在 EffectEntity.Play 注入特效倍速");
        }
        else
        {
            patchMsg = $"[PATCH] EffectEntity.Play 特效 PlaySpeed × {effectScale}";
        }

        IlSerializer.RecalculateOffsets(playMethod.Body);
        var newBody = IlSerializer.Serialize(playMethod.Body, snapshot);
        BinaryPeWriter.ReplaceMethodBody(data, playMethod.RVA, snapshot, newBody);

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine(patchMsg);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static float ParseScale(string raw)
    {
        if (!float.TryParse(raw, out var value) || !AllowedScales.Contains(value))
        {
            throw new InvalidOperationException($"--scale 须为 1.5、2、3 或 5，实际: {raw}");
        }

        return value;
    }

    /// <summary>
    /// 在 base.Play() 之后、加载 PlayFirstAnim 参数之前注入，避免破坏求值栈。
    /// SetSpeed 返回 this（链式），需 pop 掉返回值。
    /// </summary>
    private static bool InjectAfterBasePlay(MethodDefinition playMethod, MethodDefinition setSpeed, float scale)
    {
        var basePlayCall = playMethod.Body.Instructions.FirstOrDefault(IsBasePlayCall);
        if (basePlayCall?.Next == null)
        {
            return false;
        }

        var il = playMethod.Body.GetILProcessor();
        var injectPoint = basePlayCall.Next;
        var getPlaySpeed = playMethod.Module.ImportReference(
            playMethod.Module.Types.First(t => t.Name == "AnimatorBaseSystem")
                .Properties.First(p => p.Name == "PlaySpeed")
                .GetMethod!);
        var setSpeedRef = playMethod.Module.ImportReference(setSpeed);
        var animatorField = GetAnimatorField(playMethod);

        il.InsertBefore(injectPoint, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Ldfld, animatorField));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Callvirt, getPlaySpeed));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Ldc_R4, scale));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Mul));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Call, setSpeedRef));
        il.InsertBefore(injectPoint, il.Create(OpCodes.Pop));

        playMethod.Body.MaxStackSize = Math.Max(playMethod.Body.MaxStackSize, (short)8);
        return true;
    }

    private static bool IsBasePlayCall(Instruction insn)
    {
        return insn.OpCode == OpCodes.Call
            && insn.Operand is MethodReference called
            && called.Name == "Play"
            && called.DeclaringType.Name == "EffectEntityBase";
    }

    private static bool ContainsPatchSequence(MethodDefinition method)
    {
        return FindFixedScaleConstantIndex(method) >= 0 || FindLegacyScaleConstantIndex(method) >= 0;
    }

    internal static bool IsHookInstalled(MethodDefinition method)
        => ContainsPatchSequence(method);

    private static bool IsAlreadyPatched(MethodDefinition method, float scale)
    {
        var idx = FindFixedScaleConstantIndex(method);
        if (idx < 0)
        {
            return false;
        }

        var instructions = method.Body.Instructions;
        return instructions[idx].Operand is float value && Math.Abs(value - scale) < 0.001f;
    }

    private static bool TryReplaceScaleConstant(MethodDefinition method, float newScale)
    {
        var idx = FindFixedScaleConstantIndex(method);
        if (idx < 0)
        {
            return false;
        }

        var ins = method.Body.Instructions[idx];
        if (ins.Operand is float old && Math.Abs(old - newScale) < 0.001f)
        {
            return false;
        }

        ins.Operand = newScale;
        return true;
    }

    private static int FindFixedScaleConstantIndex(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 3; i++)
        {
            if (instructions[i].OpCode != OpCodes.Ldc_R4
                || instructions[i].Operand is not float value
                || !AllowedScales.Contains(value))
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Mul
                || instructions[i + 2].OpCode != OpCodes.Call
                || instructions[i + 2].Operand is not MethodReference called
                || called.Name != "SetSpeed")
            {
                continue;
            }

            if (instructions[i + 3].OpCode != OpCodes.Pop)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int FindLegacyScaleConstantIndex(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].OpCode != OpCodes.Ldc_R4
                || instructions[i].Operand is not float value
                || !AllowedScales.Contains(value))
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Mul
                || instructions[i + 2].OpCode != OpCodes.Call
                || instructions[i + 2].Operand is not MethodReference called
                || called.Name != "SetSpeed")
            {
                continue;
            }

            if (i + 3 < instructions.Count && instructions[i + 3].OpCode == OpCodes.Pop)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static FieldReference GetAnimatorField(MethodDefinition playMethod)
    {
        var field = playMethod.DeclaringType.Fields.FirstOrDefault(f => f.Name == "mAnimatorSys")
            ?? throw new InvalidOperationException("找不到 EffectEntity.mAnimatorSys");
        return playMethod.Module.ImportReference(field);
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
