using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 宠物面板：所有宠物始终显示「满档回收」按钮（原版需配置表 + isPrefectPet）。
/// </summary>
internal static class PetRecycleShowIlPatcher
{
    private const int ExpectedSize = 6_355_968;

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
            Console.WriteLine("用法: HotfixPatcher pet-recycle-show-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 宠物满档回收按钮补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        if (origBytes.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"源文件体积应为 {ExpectedSize}，实际 {origBytes.Length}");
        }

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

        var panel = asm.MainModule.Types.First(t => t.Name == "PetMainPanel");
        var refreshMiddle = panel.Methods.First(m => m.Name == "RefreshMiddle" && m.HasBody);

        if (IsAlreadyPatched(refreshMiddle))
        {
            throw new InvalidOperationException("宠物满档回收按钮补丁已包含");
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, refreshMiddle.RVA);
        PatchAlwaysShowRecycle(refreshMiddle, asm.MainModule);

        IlSerializer.RecalculateOffsets(refreshMiddle.Body);
        refreshMiddle.Body.MaxStackSize = Math.Max(refreshMiddle.Body.MaxStackSize, (ushort)8);
        var newBody = IlSerializer.Serialize(refreshMiddle.Body, snapshot);
        BinaryPeWriter.ReplaceMethodBody(data, refreshMiddle.RVA, snapshot, newBody);

        if (data.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] PetMainPanel.RefreshMiddle -> m_Btn_Recycle 始终显示");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static void PatchAlwaysShowRecycle(MethodDefinition method, ModuleDefinition module)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 8; i++)
        {
            if (!IsRecycleButtonGameObjectLoad(instructions, i))
            {
                continue;
            }

            var gameObjectInsn = instructions[i + 2];
            var setActiveIndex = FindRecycleSetActive(instructions, i);
            if (setActiveIndex < 0)
            {
                continue;
            }

            var il = method.Body.GetILProcessor();
            var setActiveInsn = instructions[setActiveIndex];
            for (var j = setActiveIndex - 1; j >= i + 3; j--)
            {
                il.Remove(instructions[j]);
            }

            il.InsertBefore(setActiveInsn, il.Create(OpCodes.Ldc_I4_1));
            return;
        }

        throw new InvalidOperationException("未找到 RefreshMiddle 中 m_Btn_Recycle 显示逻辑");
    }

    private static bool IsRecycleButtonGameObjectLoad(IList<Instruction> instructions, int i)
    {
        return instructions[i].OpCode == OpCodes.Ldarg_0
            && instructions[i + 1].OpCode == OpCodes.Ldfld
            && instructions[i + 1].Operand is FieldReference field
            && field.Name == "m_Btn_Recycle"
            && instructions[i + 2].OpCode == OpCodes.Callvirt
            && instructions[i + 2].Operand is MethodReference getGo
            && getGo.Name == "get_gameObject";
    }

    private static int FindRecycleSetActive(IList<Instruction> instructions, int recycleLoadIndex)
    {
        for (var j = recycleLoadIndex + 3; j < instructions.Count - 1; j++)
        {
            if (instructions[j].OpCode != OpCodes.Callvirt
                || instructions[j].Operand is not MethodReference called
                || called.Name != "SetActive")
            {
                continue;
            }

            var prev = instructions[j - 1];
            if (prev.OpCode == OpCodes.Ldc_I4_0 || prev.OpCode == OpCodes.Ldc_I4_1)
            {
                return j;
            }

            if (prev.OpCode == OpCodes.Callvirt
                && prev.Operand is MethodReference methodRef
                && methodRef.Name == "get_isPrefectPet")
            {
                return j;
            }
        }

        return -1;
    }

    private static bool IsAlreadyPatched(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count - 3; i++)
        {
            if (!IsRecycleButtonGameObjectLoad(instructions, i))
            {
                continue;
            }

            return instructions[i + 2].OpCode == OpCodes.Callvirt
                && instructions[i + 2].Operand is MethodReference getGo
                && getGo.Name == "get_gameObject"
                && instructions[i + 3].OpCode == OpCodes.Ldc_I4_1
                && instructions[i + 4].OpCode == OpCodes.Callvirt
                && instructions[i + 4].Operand is MethodReference setActive
                && setActive.Name == "SetActive";
        }

        return false;
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
