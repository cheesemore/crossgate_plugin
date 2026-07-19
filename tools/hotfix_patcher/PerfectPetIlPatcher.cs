using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal sealed class PerfectPetPatchOptions
{
    public bool Material { get; init; }
    public bool Skin { get; init; }
    public bool Aura { get; init; }
    public int CrestId { get; init; } = 1;
}

internal static class PerfectPetIlPatcher
{
    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var material = false;
        var skin = false;
        var aura = false;
        var crestId = 1;

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
                case "--material":
                    material = true;
                    break;
                case "--skin":
                    skin = true;
                    break;
                case "--aura":
                    aura = true;
                    break;
                case "--crest-id" when i + 1 < args.Length:
                    crestId = int.Parse(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source) || (!material && !skin && !aura))
        {
            Console.WriteLine(
                "用法: HotfixPatcher perfect-pet-patch --hotfix <orig> --output <out> " +
                "[--material] [--skin] [--aura] [--crest-id N]");
            return 1;
        }

        output ??= source;
        var opts = new PerfectPetPatchOptions
        {
            Material = material,
            Skin = skin,
            Aura = aura,
            CrestId = crestId,
        };

        Apply(source, output, opts);
        Console.WriteLine("[OK] 满档特效补丁完成: " + output);
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath, PerfectPetPatchOptions opts)
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

        var snapshots = new Dictionary<MethodDefinition, byte[]>();
        var dirty = new HashSet<MethodDefinition>();

        void Snapshot(MethodDefinition method)
        {
            if (!method.HasBody)
            {
                throw new InvalidOperationException("方法无 IL: " + method.FullName);
            }

            if (!snapshots.ContainsKey(method))
            {
                snapshots[method] = ReadMethodBodyFromPe(origBytes, method.RVA);
            }
        }

        void Touch(MethodDefinition method, Action patch)
        {
            Snapshot(method);
            patch();
            dirty.Add(method);
        }

        void CommitAll()
        {
            foreach (var method in dirty.OrderBy(m => m.MetadataToken.ToInt32()))
            {
                IlSerializer.RecalculateOffsets(method.Body);
                var newBody = IlSerializer.Serialize(method.Body, snapshots[method]);
                BinaryPeWriter.ReplaceMethodBody(data, method.RVA, snapshots[method], newBody);
            }
        }

        if (opts.Material)
        {
            ApplyMaterial(asm, Touch);
        }

        if (opts.Skin)
        {
            ApplySkin(asm, Touch);
        }

        if (opts.Aura)
        {
            ApplyAura(asm, opts.CrestId, Touch);
        }

        CommitAll();

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static void ApplyMaterial(AssemblyDefinition asm, Action<MethodDefinition, Action> touch)
    {
        var petData = RequireType(asm, "PetData");
        var uiChar = RequireType(asm, "UICharacterEntity");
        var charEntity = RequireType(asm, "CharacterEntity");

        var isPerfect = RequireMethod(petData, "get_isPrefectPet");
        touch(isPerfect, () => ReplaceBoolGetter(isPerfect, value: true));
        Console.WriteLine("[MAT] PetData.isPrefectPet -> true (宠物 UI)");

        var setPetData = RequireMethod(uiChar, "SetPetData", "PetData");
        touch(setPetData, () => ForceSkinOutlineFromMat(setPetData));
        Console.WriteLine("[MAT] UICharacterEntity.SetPetData 描边修正");

        var setData = RequireMethod(charEntity, "set_data");
        touch(setData, () => ForceSkinOutlineFromMat(setData));
        Console.WriteLine("[MAT] CharacterEntity.set_data 描边修正");
    }

    private static void ApplySkin(AssemblyDefinition asm, Action<MethodDefinition, Action> touch)
    {
        var refs = FriendlyScopeIl.Resolve(asm.MainModule);
        var uiChar = RequireType(asm, "UICharacterEntity");
        var battleRole = RequireType(asm, "BattleRole");

        var setPetData = RequireMethod(uiChar, "SetPetData", "PetData");
        touch(setPetData, () =>
        {
            if (!ForcePerfectSkinBranch(setPetData))
            {
                throw new InvalidOperationException("未找到 UI 换皮分支");
            }
        });
        Console.WriteLine("[SKIN] UICharacterEntity.SetPetData (宠物 UI)");

        var refreshData = RequireMethod(battleRole, "RefreshData", "BattleRoleData");
        touch(refreshData, () =>
        {
            if (!FriendlyScopeIl.PatchBattleRefreshDataSkin(refreshData, refs))
            {
                throw new InvalidOperationException("未找到 BattleRole.RefreshData 换皮分支");
            }
        });
        Console.WriteLine("[SKIN] BattleRole.RefreshData 仅己方/友军");
    }

    private static void ApplyAura(AssemblyDefinition asm, int crestId, Action<MethodDefinition, Action> touch)
    {
        if (crestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crestId), "光环配置 Id 必须 > 0");
        }

        var refs = FriendlyScopeIl.Resolve(asm.MainModule);

        var petGetter = RequireMethod(RequireType(asm, "PetData"), "get_EffectID");
        touch(petGetter, () =>
        {
            if (!ReplaceMaxCrestUseId(petGetter, crestId))
            {
                throw new InvalidOperationException("未找到 PetData MaxCrestUseId");
            }
        });
        Console.WriteLine("[AURA] PetData.EffectID (宠物 UI) crestId=" + crestId);

        var battleGetter = RequireMethod(RequireType(asm, "BattleRoleData"), "get_EffectID");
        touch(battleGetter, () =>
        {
            if (!FriendlyScopeIl.InsertBattleEffectGuard(battleGetter, refs))
            {
                throw new InvalidOperationException("未找到 BattleRoleData 友军光环守卫");
            }

            if (!ReplaceMaxCrestUseId(battleGetter, crestId))
            {
                throw new InvalidOperationException("未找到 BattleRoleData MaxCrestUseId");
            }
        });
        Console.WriteLine("[AURA] BattleRoleData.EffectID 仅己方/友军 crestId=" + crestId);

        var debuffMethod = RequireType(asm, "BattleProcesser").Methods
            .First(m => m.Name == "RefreshDebuff" && m.HasBody);
        touch(debuffMethod, () =>
        {
            if (!FriendlyScopeIl.PatchBattleDebuffAura(asm.MainModule, refs))
            {
                throw new InvalidOperationException("未找到 BattleProcesser.RefreshDebuff 光环注入点");
            }
        });
        Console.WriteLine("[AURA] BattleProcesser.RefreshDebuff 战斗光环");
    }

    private static void ReplaceBoolGetter(MethodDefinition method, bool value)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        body.MaxStackSize = 1;

        var il = body.GetILProcessor();
        il.Append(il.Create(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static bool ForcePerfectPetFlag(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var patched = false;

        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode != OpCodes.Ldfld || instructions[i].Operand is not FieldReference field)
            {
                continue;
            }

            if (field.Name is "PerfectPet" or "Perfectpet")
            {
                instructions[i].OpCode = OpCodes.Ldc_I4_1;
                instructions[i].Operand = null;
                patched = true;
                continue;
            }

            if (field.Name == "Char" && i + 1 < instructions.Count
                && instructions[i + 1].OpCode == OpCodes.Callvirt
                && instructions[i + 1].Operand is MethodReference called
                && called.Name == "get_Perfectpet")
            {
                NopLdargBefore(instructions, i);
                instructions[i].OpCode = OpCodes.Nop;
                instructions[i].Operand = null;
                instructions[i + 1].OpCode = OpCodes.Ldc_I4_1;
                instructions[i + 1].Operand = null;
                patched = true;
            }
        }

        return patched;
    }

    private static bool ForcePerfectSkinBranch(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var patched = false;

        for (var i = 0; i < instructions.Count - 2; i++)
        {
            var a = instructions[i];
            var b = instructions[i + 1];
            var c = instructions[i + 2];

            if (IsPerfectPetCheck(a) && b.OpCode == OpCodes.Ldc_I4_1
                && c.OpCode == OpCodes.Beq_S)
            {
                c.OpCode = OpCodes.Br_S;
                patched = true;
                continue;
            }

            if (i + 3 < instructions.Count
                && a.OpCode == OpCodes.Ldflda
                && IsPerfectPetFieldLoad(b)
                && instructions[i + 2].OpCode == OpCodes.Ldc_I4_1
                && instructions[i + 3].OpCode == OpCodes.Beq_S)
            {
                instructions[i + 3].OpCode = OpCodes.Br_S;
                patched = true;
                continue;
            }

            if (a.OpCode == OpCodes.Callvirt && a.Operand is MethodReference called
                && called.Name == "get_isPrefectPet" && b.OpCode == OpCodes.Brtrue_S)
            {
                b.OpCode = OpCodes.Br_S;
                patched = true;
            }
        }

        return patched;
    }

    private static bool IsPerfectPetCheck(Instruction insn)
    {
        if (IsPerfectPetFieldLoad(insn))
        {
            return true;
        }

        if (insn.OpCode == OpCodes.Callvirt && insn.Operand is MethodReference called)
        {
            return called.Name is "get_Perfectpet" or "get_isPrefectPet";
        }

        return false;
    }

    private static bool IsPerfectPetFieldLoad(Instruction insn)
    {
        return insn.OpCode == OpCodes.Ldfld && insn.Operand is FieldReference field
            && field.Name is "PerfectPet" or "Perfectpet";
    }

    private static void ForceSkinOutlineFromMat(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i + 2].OpCode != OpCodes.Beq_S)
            {
                continue;
            }

            var third = instructions[i + 2];
            var falseBranch = (Instruction)third.Operand!;
            if (!LeadsToOutlineFalse(instructions, falseBranch))
            {
                continue;
            }

            third.OpCode = OpCodes.Br_S;
            third.Operand = FindOutlineMatBranch(instructions, i + 3, falseBranch);
        }
    }

    private static bool LeadsToOutlineFalse(IList<Instruction> instructions, Instruction start)
    {
        var startIdx = instructions.IndexOf(start);
        if (startIdx < 0)
        {
            return false;
        }

        var endIdx = Math.Min(instructions.Count, startIdx + 8);
        for (var idx = startIdx; idx < endIdx; idx++)
        {
            var insn = instructions[idx];
            if (insn.OpCode == OpCodes.Callvirt && insn.Operand is MethodReference called
                && called.Name == "SetOutline")
            {
                for (var back = idx - 1; back >= Math.Max(startIdx, idx - 4); back--)
                {
                    if (instructions[back].OpCode == OpCodes.Ldc_I4_0)
                    {
                        return true;
                    }
                }
            }

            if (insn.OpCode == OpCodes.Ret || insn.OpCode == OpCodes.Br_S || insn.OpCode == OpCodes.Br)
            {
                break;
            }
        }

        return false;
    }

    private static Instruction FindOutlineMatBranch(
        IList<Instruction> instructions,
        int searchFrom,
        Instruction falseBranch)
    {
        var falseIdx = instructions.IndexOf(falseBranch);
        for (var idx = Math.Max(searchFrom, falseIdx + 1); idx < instructions.Count; idx++)
        {
            var insn = instructions[idx];
            if (insn.OpCode != OpCodes.Callvirt || insn.Operand is not MethodReference called
                || called.Name != "SetOutline")
            {
                continue;
            }

            var usesMatLocal = false;
            for (var back = idx - 1; back >= Math.Max(0, idx - 6); back--)
            {
                if (instructions[back].OpCode == OpCodes.Ldc_I4_0)
                {
                    break;
                }

                if (IsMatLocalLoad(instructions[back]))
                {
                    usesMatLocal = true;
                    break;
                }
            }

            if (!usesMatLocal)
            {
                continue;
            }

            for (var back = idx; back >= Math.Max(0, falseIdx); back--)
            {
                if (instructions[back].OpCode == OpCodes.Ldarg_0)
                {
                    return instructions[back];
                }
            }
        }

        return falseBranch;
    }

    private static bool ReplaceMaxCrestUseId(MethodDefinition method, int crestId)
    {
        var instructions = method.Body.Instructions;
        var patched = 0;
        var il = method.Body.GetILProcessor();

        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Ldfld && instructions[i].Operand is FieldReference field
                && field.Name == "maxCrestUseId")
            {
                NopLdargBefore(instructions, i);
                instructions[i].OpCode = OpCodes.Ldc_I4;
                instructions[i].Operand = crestId;
                patched++;
                continue;
            }

            if (instructions[i].OpCode == OpCodes.Ldfld && instructions[i].Operand is FieldReference dataField
                && dataField.Name == "data" && i + 1 < instructions.Count
                && instructions[i + 1].OpCode == OpCodes.Callvirt
                && instructions[i + 1].Operand is MethodReference crestGetter
                && crestGetter.Name == "get_MaxCrestUseId")
            {
                NopLdargBefore(instructions, i);
                il.Replace(instructions[i], il.Create(OpCodes.Ldc_I4, crestId));
                il.Replace(instructions[i + 1], il.Create(OpCodes.Nop));
                patched++;
                continue;
            }

            if (instructions[i].OpCode == OpCodes.Ldfld && instructions[i].Operand is FieldReference charField
                && charField.Name == "Char" && i + 1 < instructions.Count
                && instructions[i + 1].OpCode == OpCodes.Callvirt
                && instructions[i + 1].Operand is MethodReference battleCrest
                && battleCrest.Name == "get_MaxCrestUseId")
            {
                NopLdargBefore(instructions, i);
                il.Replace(instructions[i], il.Create(OpCodes.Ldc_I4, crestId));
                il.Replace(instructions[i + 1], il.Create(OpCodes.Nop));
                patched++;
            }
        }

        return patched > 0;
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

    private static bool IsMatLocalLoad(Instruction insn)
    {
        return insn.OpCode == OpCodes.Ldloc_S || insn.OpCode == OpCodes.Ldloc
            || insn.OpCode == OpCodes.Ldloc_0 || insn.OpCode == OpCodes.Ldloc_1
            || insn.OpCode == OpCodes.Ldloc_2 || insn.OpCode == OpCodes.Ldloc_3;
    }

    private static void NopLdargBefore(IList<Instruction> instructions, int index)
    {
        if (index <= 0)
        {
            return;
        }

        var prev = instructions[index - 1];
        if (prev.OpCode == OpCodes.Ldarg_0 || prev.OpCode == OpCodes.Ldarg_1 || prev.OpCode == OpCodes.Ldarg_2)
        {
            prev.OpCode = OpCodes.Nop;
            prev.Operand = null;
        }
    }

    private static TypeDefinition RequireType(AssemblyDefinition asm, string name)
    {
        return asm.MainModule.Types.FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException("未找到类型: " + name);
    }

    private static MethodDefinition RequireMethod(TypeDefinition type, string name, string? firstParamType = null)
    {
        var methods = type.Methods.Where(m => m.Name == name && m.HasBody).ToList();
        if (firstParamType != null)
        {
            methods = methods.Where(m =>
                m.Parameters.Count > 0 && m.Parameters[0].ParameterType.Name == firstParamType).ToList();
        }

        return methods.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"未找到方法: {type.Name}.{name}" + (firstParamType != null ? $"({firstParamType})" : ""));
    }
}
