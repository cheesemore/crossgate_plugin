using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>宠物装备：显示 4 个装备孔。</summary>
internal static class PetEquipUnlockIlPatcher
{
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
            Console.WriteLine("用法: HotfixPatcher pet-equip-unlock-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 宠物装备补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
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

        var petEquipPanel = asm.MainModule.Types.First(t => t.Name == "PetEquipChildPanel");
        var setData = petEquipPanel.Methods.First(m => m.Name == "SetData" && m.HasBody);

        if (HasSlotUnlockPatch(origBytes, setData))
        {
            throw new InvalidOperationException("宠物装备四孔补丁已包含");
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, setData.RVA);
        PatchPetEquipSlots(setData, asm.MainModule);

        IlSerializer.RecalculateOffsets(setData.Body);
        setData.Body.MaxStackSize = Math.Max(setData.Body.MaxStackSize, (ushort)8);
        var newBody = IlSerializer.Serialize(setData.Body, snapshot);
        BinaryPeWriter.ReplaceMethodBody(data, setData.RVA, snapshot, newBody);

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] PetEquipChildPanel -> 显示 4 个装备孔");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static void PatchPetEquipSlots(MethodDefinition method, ModuleDefinition module)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var ret = body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        il.Remove(ret);

        EmitShowAllSlots(il, module, () => il.Append(il.Create(OpCodes.Ldarg_0)));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static bool HasSlotUnlockPatch(byte[] peBytes, MethodDefinition setData)
    {
        if (LooksLikePatchedSetDataBody(peBytes, setData.RVA))
        {
            return true;
        }

        return ScanTextForPatchedSetDataBody(peBytes);
    }

    private static bool LooksLikePatchedSetDataBody(byte[] peBytes, int rva)
    {
        byte[] snapshot;
        try
        {
            snapshot = ReadMethodBodyFromPe(peBytes, rva);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return IsPatchedSetDataSnapshot(snapshot);
    }

    private static bool ScanTextForPatchedSetDataBody(byte[] peBytes)
    {
        var text = PeLayout.GetSection(peBytes, ".text");
        var start = (int)text.PointerToRawData;
        var end = start + (int)text.SizeOfRawData;
        for (var off = start; off < end - 13; off++)
        {
            if ((peBytes[off] & 0x3) != 0x3)
            {
                continue;
            }

            var codeSize = BitConverter.ToInt32(peBytes, off + 4);
            // 补丁后 SetData 约 132 字节 IL；RefreshEquipShow 等方法更长
            if (codeSize is < 100 or > 160)
            {
                continue;
            }

            var len = 12 + codeSize;
            if (off + len > end)
            {
                continue;
            }

            var snapshot = new byte[len];
            Array.Copy(peBytes, off, snapshot, 0, len);
            if (IsPatchedSetDataSnapshot(snapshot))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPatchedSetDataSnapshot(byte[] snapshot)
    {
        var codeSize = GetCodeSize(snapshot);
        if (codeSize is < 100 or > 160)
        {
            return false;
        }

        if (!HasSetDataPrologue(snapshot))
        {
            return false;
        }

        var codeOffset = GetCodeOffset(snapshot);
        var setActiveCount = CountSetActiveTrueCalls(snapshot, codeOffset, codeSize);
        return setActiveCount == 4;
    }

    private static int CountSetActiveTrueCalls(byte[] snapshot, int codeOffset, int codeSize)
    {
        var setActiveCount = 0;
        for (var i = codeOffset; i < codeOffset + codeSize - 2; i++)
        {
            if (snapshot[i] != (byte)OpCodes.Ldc_I4_1.Value)
            {
                continue;
            }

            if (snapshot[i + 1] == (byte)OpCodes.Callvirt.Value
                || snapshot[i + 2] == (byte)OpCodes.Callvirt.Value)
            {
                setActiveCount++;
            }
        }

        return setActiveCount;
    }

    private static bool HasSetDataPrologue(byte[] snapshot)
    {
        var codeOffset = GetCodeOffset(snapshot);
        var codeSize = GetCodeSize(snapshot);
        var end = Math.Min(codeOffset + 24, codeOffset + codeSize - 2);
        for (var i = codeOffset; i < end; i++)
        {
            if (snapshot[i] == (byte)OpCodes.Ldarg_0.Value
                && snapshot[i + 1] == (byte)OpCodes.Ldarg_1.Value
                && snapshot[i + 2] == (byte)OpCodes.Stfld.Value)
            {
                return true;
            }

            if (i + 4 < codeOffset + codeSize
                && snapshot[i] == (byte)OpCodes.Ldarg_0.Value
                && snapshot[i + 2] == (byte)OpCodes.Ldarg_1.Value
                && snapshot[i + 4] == (byte)OpCodes.Stfld.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitShowAllSlots(ILProcessor il, ModuleDefinition module, Action loadPanel)
    {
        var getGameObject = ImportComponentGetGameObject(module);
        var setActive = ImportGameObjectSetActive(module);

        foreach (var fieldName in new[]
                 {
                     "m_PetEquipItem1",
                     "m_PetEquipItem2",
                     "m_PetEquipItem3",
                     "m_PetEquipItem4",
                 })
        {
            loadPanel();
            il.Append(il.Create(OpCodes.Ldfld, FindField(module, "PetEquipChildPanel", fieldName)));
            il.Append(il.Create(OpCodes.Ldfld, FindField(module, "PetEquipItem", "collector")));
            il.Append(il.Create(OpCodes.Callvirt, getGameObject));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Callvirt, setActive));
        }
    }

    private static MethodReference ImportComponentGetGameObject(ModuleDefinition module)
    {
        var panel = module.Types.First(t => t.Name == "PetEquipChildPanel");
        var refresh = panel.Methods.First(m => m.Name == "RefreshEquipShow" && m.HasBody);
        foreach (var ins in refresh.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == "get_gameObject"
                && called.DeclaringType.Name == "Component")
            {
                return module.ImportReference(called);
            }
        }

        throw new InvalidOperationException("未找到 Component.get_gameObject 样例");
    }

    private static MethodReference ImportGameObjectSetActive(ModuleDefinition module)
    {
        var panel = module.Types.First(t => t.Name == "PetEquipChildPanel");
        var refresh = panel.Methods.First(m => m.Name == "RefreshEquipShow" && m.HasBody);
        foreach (var ins in refresh.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == "SetActive"
                && called.DeclaringType.Name == "GameObject")
            {
                return module.ImportReference(called);
            }
        }

        throw new InvalidOperationException("未找到 GameObject.SetActive 样例");
    }

    private static FieldReference FindField(ModuleDefinition module, string typeName, string fieldName)
    {
        var type = module.Types.First(t => t.Name == typeName);
        var field = type.Fields.First(f => f.Name == fieldName);
        return module.ImportReference(field);
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

        if ((flags & 0x3) == 0x3)
        {
            return BitConverter.ToInt32(methodBody, 4);
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");
    }
}
