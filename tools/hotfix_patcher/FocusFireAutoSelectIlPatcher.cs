using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// BattleRoleSelector.AutoSelect：合击/随机（及非 LowHP/Death）时，
/// 若 AI 独占锚点 <c>SeqChapterAiBattleTarget.Index</c> 对应单位已 CanSelect，
/// 则优先选它（群攻也以此为锚点）。
/// <para>
/// 不读、不写官方 VIP <c>focusFireIndex</c> / 集火标签。
/// Index==-1（默认、未开 AI）时直接落到原合击/随机，不得卡死。
/// </para>
/// </summary>
internal static class FocusFireAutoSelectIlPatcher
{
    public const string Marker = "SeqChapterAiSelect.v1";
    public const string LegacyMarker = "SeqChapterFocusAutoSelect.v1";
    public const string AiTargetTypeName = "SeqChapterAiBattleTarget";
    public const string AiTargetFieldName = "Index";
    private const string CommandName = "ai-target-autoselect-patch";

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
                $"用法: HotfixPatcher {CommandName} --hotfix <file> [--output <out>]\n" +
                $"      HotfixPatcher focus-fire-autoselect-patch …（同义别名）\n" +
                $"      HotfixPatcher {CommandName} --hotfix <file> --detect");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var state = DetectState(source);
            Console.WriteLine(state);
            return state == "ai-target-autoselect" ? 0 : 1;
        }

        try
        {
            // 默认：只拆除不注入（当前注入会卡战斗）
            var stripOnly = true;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--inject")
                {
                    stripOnly = false;
                }
            }

            if (stripOnly)
            {
                StripOnly(source, output);
                Console.WriteLine("[OK] 已拆除 AutoSelect AI 注入（未重新注入）: " + output);
            }
            else
            {
                Apply(source, output);
                Console.WriteLine("[OK] AutoSelect AI 目标优先补丁完成: " + output);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    private static string DetectState(string hotfixPath)
    {
        try
        {
            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var method = FindAutoSelect(asm);
            if (method == null)
            {
                return "missing";
            }

            if (HasMarker(method, Marker) || HasMarker(method, LegacyMarker) || HasFocusFireIndexRead(method))
            {
                return HasFocusFireIndexRead(method) ? "legacy-focus-fire" : "ai-target-autoselect";
            }

            return "original";
        }
        catch
        {
            return "error";
        }
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var method = FindAutoSelect(asm);
            if (method == null)
            {
                return false;
            }

            // 新版 marker，且不得再读官方 focusFireIndex
            if (!HasMarker(method, Marker))
            {
                return false;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference fr
                    && fr.Name == "focusFireIndex")
                {
                    return false;
                }
            }

            return asm.MainModule.Types.Any(t => t.Name == AiTargetTypeName);
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);
        var hotfixDir = Path.GetDirectoryName(sourcePath)!;

        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        InjectIfNeeded(asm, reinject: true);

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);
        MetadataValidator.EnsureReadable(padded, hotfixDir);
        File.WriteAllBytes(outputPath, padded);
        HotfixSize.EnsureUnchanged(File.ReadAllBytes(outputPath), expectedSize);
        Console.WriteLine("[PATCH] BattleRoleSelector.AutoSelect：合击/随机优先 AI 独占锚点");
    }

    /// <param name="reinject">false=只拆除旧块，不注入（紧急恢复普通 Auto）。</param>
    public static void InjectIfNeeded(AssemblyDefinition asm, bool reinject)
    {
        var method = FindAutoSelect(asm);
        if (method == null)
        {
            Console.WriteLine("[AI-AUTOSELECT] 警告：未找到 AutoSelect");
            return;
        }

        var hadLegacy = HasMarker(method, LegacyMarker)
                        || HasFocusFireIndexRead(method)
                        || HasMarker(method, Marker);

        if (hadLegacy)
        {
            StripInjectedPreferBlock(method);
            Console.WriteLine("[AI-AUTOSELECT] 已拆除 AutoSelect 注入块");
        }

        if (!reinject)
        {
            Console.WriteLine("[AI-AUTOSELECT] 仅拆除，不注入（普通 Auto 应恢复）");
            // 类型可留着；助手仍可写 Index，AutoSelect 不读则零干预
            EnsureAiTargetType(asm.MainModule);
            return;
        }

        // Index==-1 时首条 Beq 直接回落官方合击/随机；有有效 Index 且 CanSelect 才改目标
        // 注意：现网默认不走此路径（易卡自动）；仅 --inject / 显式 reinject:true
        var indexField = EnsureAiTargetType(asm.MainModule);
        if (!HasMarker(method, Marker))
        {
            InjectPreferAiTarget(method, asm.MainModule, indexField);
        }
        else
        {
            Console.WriteLine("[AI-AUTOSELECT] 已有注入 marker，跳过");
        }
    }

    /// <summary>只拆除、不注入。用于紧急恢复。</summary>
    public static void StripOnly(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);
        var hotfixDir = Path.GetDirectoryName(sourcePath)!;

        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        InjectIfNeeded(asm, reinject: false);

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);
        MetadataValidator.EnsureReadable(padded, hotfixDir);
        File.WriteAllBytes(outputPath, padded);
        HotfixSize.EnsureUnchanged(File.ReadAllBytes(outputPath), expectedSize);
    }

    private static MethodDefinition? FindAutoSelect(AssemblyDefinition asm)
    {
        return asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleRoleSelector")
            ?.Methods.FirstOrDefault(m =>
                m.Name == "AutoSelect" && m.HasBody && m.Parameters.Count >= 2);
    }

    private static bool HasMarker(MethodDefinition method, string marker)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s && s == marker)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFocusFireIndexRead(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference fr
                && fr.Name == "focusFireIndex")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>hotfix 内注入 AI 独占静态锚点（默认 -1）。</summary>
    private static FieldDefinition EnsureAiTargetType(ModuleDefinition module)
    {
        var existing = module.Types.FirstOrDefault(t => t.Name == AiTargetTypeName);
        if (existing != null)
        {
            var f = existing.Fields.FirstOrDefault(x => x.Name == AiTargetFieldName)
                ?? throw new InvalidOperationException($"{AiTargetTypeName} 缺少字段 {AiTargetFieldName}");
            return f;
        }

        var type = new TypeDefinition(
            "",
            AiTargetTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        var field = new FieldDefinition(
            AiTargetFieldName,
            FieldAttributes.Public | FieldAttributes.Static,
            module.TypeSystem.Int32);
        type.Fields.Add(field);

        // .cctor: Index = -1
        var cctor = new MethodDefinition(
            ".cctor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
            | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        var cil = cctor.Body.GetILProcessor();
        cil.Emit(OpCodes.Ldc_I4_M1);
        cil.Emit(OpCodes.Stsfld, field);
        cil.Emit(OpCodes.Ret);
        type.Methods.Add(cctor);

        module.Types.Add(type);
        Console.WriteLine($"[AI-AUTOSELECT] 已注入 {AiTargetTypeName}.{AiTargetFieldName}");
        return field;
    }

    /// <summary>
    /// 拆除 marker～switch 前 ldarg.3 之间的注入块（含旧 focusFire / 新 AI 半残）。
    /// </summary>
    private static void StripInjectedPreferBlock(MethodDefinition method)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        Instruction? marker = null;
        foreach (var insn in body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == Marker || s == LegacyMarker))
            {
                marker = insn;
                break;
            }
        }

        if (marker == null)
        {
            // 无 marker 但仍有 focusFireIndex：无法安全拆，交给上层报错
            if (HasFocusFireIndexRead(method))
            {
                throw new InvalidOperationException(
                    "AutoSelect 含 focusFireIndex 读取但无注入 marker，请从 .orig 重打");
            }

            return;
        }

        var switchLdarg3 = FindSwitchModeLdarg3(body)
            ?? throw new InvalidOperationException("拆块时未定位 switch(mode) 入口");

        // 收集 marker .. switchLdarg3 之前（不含）的指令
        var toRemove = new List<Instruction>();
        var seen = false;
        foreach (var insn in body.Instructions)
        {
            if (insn == marker)
            {
                seen = true;
            }

            if (!seen)
            {
                continue;
            }

            if (insn == switchLdarg3)
            {
                break;
            }

            toRemove.Add(insn);
        }

        // 外部若有分支指向块内，改指 switchLdarg3（不应发生；防御）
        foreach (var insn in body.Instructions.ToArray())
        {
            if (insn.Operand is Instruction target && toRemove.Contains(target))
            {
                insn.Operand = switchLdarg3;
            }
            else if (insn.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    if (toRemove.Contains(targets[i]))
                    {
                        targets[i] = switchLdarg3;
                    }
                }
            }
        }

        foreach (var insn in toRemove)
        {
            il.Remove(insn);
        }

        // 注入时加的局部变量留着无害；不删以免打乱已有索引
        IlSerializer.RecalculateOffsets(body);
    }

    private static void InjectPreferAiTarget(
        MethodDefinition method, ModuleDefinition module, FieldDefinition indexField)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var switchLdarg3 = FindSwitchModeLdarg3(body)
            ?? throw new InvalidOperationException("未定位 AutoSelect 的 switch(mode) 入口");
        // 与官方 switch 分支 leave/br 同一收尾点，避免 Br 错目标死循环
        var cleanupStart = FindOfficialCleanupStart(body, switchLdarg3)
            ?? throw new InvalidOperationException("未定位 AutoSelect 收尾清 CanSelect");

        var calcSelected = method.DeclaringType.Methods.First(m =>
            m.Name == "CaculateSelectedIndex" && m.Parameters.Count == 2);
        var battleRoleDic = module.Types.First(t => t.Name == "BattleRoleContainer")
            .Fields.First(f => f.Name == "BattleRoleDic");
        var dicFieldType = battleRoleDic.FieldType;
        var battleRoleType = module.Types.First(t => t.Name == "BattleRole");
        var containsKey = ImportDictMethod(module, dicFieldType, "ContainsKey", module.TypeSystem.Boolean)
            ?? throw new InvalidOperationException("未导入 Dictionary.ContainsKey");
        var getItem = ImportDictMethod(module, dicFieldType, "get_Item", battleRoleType)
            ?? throw new InvalidOperationException("未导入 Dictionary.get_Item");
        var getIsDead = battleRoleType.Methods.First(m => m.Name == "get_IsDead" && m.Parameters.Count == 0);
        var getCanSelect = battleRoleType.Methods.First(m => m.Name == "get_CanSelect" && m.Parameters.Count == 0);

        var focusLocal = new VariableDefinition(module.TypeSystem.Int32);
        var roleLocal = new VariableDefinition(battleRoleType);
        body.Variables.Add(focusLocal);
        body.Variables.Add(roleLocal);

        var skip = il.Create(OpCodes.Nop);
        var block = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, Marker),
            il.Create(OpCodes.Pop),
            // 先读 AI 锚点：-1 立刻回落官方合击/随机（无 AI 时零干预）
            il.Create(OpCodes.Ldsfld, indexField),
            il.Create(OpCodes.Stloc, focusLocal),
            il.Create(OpCodes.Ldloc, focusLocal),
            il.Create(OpCodes.Ldc_I4_M1),
            il.Create(OpCodes.Beq, skip),
            // LowHP=1 / Death=2 → 不抢（补血/复活）
            il.Create(OpCodes.Ldarg_3),
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Beq, skip),
            il.Create(OpCodes.Ldarg_3),
            il.Create(OpCodes.Ldc_I4_2),
            il.Create(OpCodes.Beq, skip),
            il.Create(OpCodes.Ldsfld, battleRoleDic),
            il.Create(OpCodes.Ldloc, focusLocal),
            il.Create(OpCodes.Callvirt, containsKey),
            il.Create(OpCodes.Brfalse, skip),
            il.Create(OpCodes.Ldsfld, battleRoleDic),
            il.Create(OpCodes.Ldloc, focusLocal),
            il.Create(OpCodes.Callvirt, getItem),
            il.Create(OpCodes.Stloc, roleLocal),
            il.Create(OpCodes.Ldloc, roleLocal),
            il.Create(OpCodes.Brfalse, skip),
            il.Create(OpCodes.Ldloc, roleLocal),
            il.Create(OpCodes.Callvirt, getIsDead),
            il.Create(OpCodes.Brtrue, skip),
            il.Create(OpCodes.Ldloc, roleLocal),
            il.Create(OpCodes.Callvirt, getCanSelect),
            il.Create(OpCodes.Brfalse, skip),
            // result = CaculateSelectedIndex(type, role); type 在 loc.2
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldloc_2),
            il.Create(OpCodes.Ldloc, roleLocal),
            il.Create(OpCodes.Call, calcSelected),
            il.Create(OpCodes.Stloc_0),
            il.Create(OpCodes.Br, cleanupStart),
            skip,
        };

        for (var i = 0; i < block.Count; i++)
        {
            il.InsertBefore(switchLdarg3, block[i]);
        }

        body.InitLocals = true;
        IlSerializer.RecalculateOffsets(body);
        if (body.MaxStackSize < 8)
        {
            body.MaxStackSize = 8;
        }

        Console.WriteLine("[AI-AUTOSELECT] 已注入合击/随机优先 SeqChapterAiBattleTarget.Index（Index=-1 不干预）");
    }

    private static MethodReference? ImportDictMethod(
        ModuleDefinition module, TypeReference dictType, string name, TypeReference returnType)
    {
        if (dictType is not GenericInstanceType git)
        {
            return null;
        }

        var resolved = git.Resolve();
        if (resolved == null)
        {
            return null;
        }

        foreach (var m in resolved.Methods)
        {
            if (m.Name != name || m.Parameters.Count != 1)
            {
                continue;
            }

            var mr = new MethodReference(m.Name, returnType, git)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = m.CallingConvention,
            };
            foreach (var _ in m.Parameters)
            {
                mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            }

            return module.ImportReference(mr);
        }

        return null;
    }

    private static Instruction? FindSwitchModeLdarg3(MethodBody body)
    {
        for (var i = 1; i < body.Instructions.Count; i++)
        {
            if (body.Instructions[i].OpCode == OpCodes.Switch
                && body.Instructions[i - 1].OpCode == OpCodes.Ldarg_3)
            {
                return body.Instructions[i - 1];
            }
        }

        return null;
    }

    /// <summary>
    /// 取官方 switch 分支 leave/br 的共同收尾（清 CanSelect 的 BattleRoleDic 循环）。
    /// 比「最后一个带 set_CanSelect 的 Dic」更稳，避免 Br 错目标死循环。
    /// </summary>
    private static Instruction? FindOfficialCleanupStart(MethodBody body, Instruction switchLdarg3)
    {
        var switchIdx = body.Instructions.IndexOf(switchLdarg3);
        if (switchIdx < 0 || switchIdx + 1 >= body.Instructions.Count)
        {
            return null;
        }

        var switchInsn = body.Instructions[switchIdx + 1];
        if (switchInsn.OpCode != OpCodes.Switch || switchInsn.Operand is not Instruction[] cases)
        {
            return null;
        }

        // 扫描 switch 之后的 leave/br，统计跳到「后方 ldsfld BattleRoleDic」的目标
        var votes = new Dictionary<Instruction, int>();
        for (var i = switchIdx + 2; i < body.Instructions.Count; i++)
        {
            var insn = body.Instructions[i];
            Instruction? target = null;
            if ((insn.OpCode == OpCodes.Leave || insn.OpCode == OpCodes.Leave_S
                 || insn.OpCode == OpCodes.Br || insn.OpCode == OpCodes.Br_S)
                && insn.Operand is Instruction t)
            {
                target = t;
            }

            if (target == null)
            {
                continue;
            }

            if (target.OpCode == OpCodes.Ldsfld && target.Operand is FieldReference fr
                && fr.Name == "BattleRoleDic" && fr.DeclaringType.Name == "BattleRoleContainer"
                && body.Instructions.IndexOf(target) > switchIdx)
            {
                votes[target] = votes.TryGetValue(target, out var c) ? c + 1 : 1;
            }
        }

        if (votes.Count > 0)
        {
            return votes.OrderByDescending(kv => kv.Value).First().Key;
        }

        // 回退：switch 之后最后一个「Dic + set_CanSelect」
        Instruction? lastDic = null;
        for (var i = switchIdx + 1; i < body.Instructions.Count; i++)
        {
            var insn = body.Instructions[i];
            if (insn.OpCode != OpCodes.Ldsfld || insn.Operand is not FieldReference fr
                || fr.Name != "BattleRoleDic" || fr.DeclaringType.Name != "BattleRoleContainer")
            {
                continue;
            }

            for (var j = i + 1; j < Math.Min(i + 60, body.Instructions.Count); j++)
            {
                if (body.Instructions[j].OpCode == OpCodes.Callvirt
                    && body.Instructions[j].Operand is MethodReference mr
                    && mr.Name == "set_CanSelect")
                {
                    lastDic = insn;
                    break;
                }
            }
        }

        return lastDic;
    }
}
