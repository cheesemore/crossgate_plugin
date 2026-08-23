using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 宠物回收：去掉「捕捉的金卡宠物无法回收」客户端拦截。
/// OnClickPetRecycleCallback 里 get_ETRARE + ldc.i4.3 + bne.un.s 改成 pop + br.s，
/// 体积不变，点回收直接进确认框。服务端仍可能拒绝。
/// </summary>
internal static class PetRecycleCaptureAllowIlPatcher
{
    private const string TipText = "捕捉的金卡宠物无法回收";

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
                "用法: HotfixPatcher pet-recycle-capture-allow-patch --hotfix <hotfix> --output <out>\n" +
                "      HotfixPatcher pet-recycle-capture-allow-patch --hotfix <file> --detect");
            return 1;
        }

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "pet-recycle-capture-allow" : "original");
            return patched ? 0 : 1;
        }

        output ??= source;
        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 宠物回收允许捕捉金卡补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含") || ex.Message.Contains("已去掉"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
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

        var method = FindClickCallback(asm);
        if (IsAlreadyPatched(method))
        {
            throw new InvalidOperationException("捕捉金卡回收拦截已去掉");
        }

        var (ldc, bne) = FindCaptureCheck(method);
        var (codeFileOff, _) = GetMethodCodeRange(data, method.RVA);

        var ldcOff = codeFileOff + ldc.Offset;
        var bneOff = codeFileOff + bne.Offset;
        if (data[ldcOff] != OpCodes.Ldc_I4_3.Op2 || data[bneOff] != OpCodes.Bne_Un_S.Op2)
        {
            throw new InvalidOperationException(
                $"IL/文件操作码不一致: ldc=0x{data[ldcOff]:X2} bne=0x{data[bneOff]:X2}");
        }

        data[ldcOff] = OpCodes.Pop.Op2;
        data[bneOff] = OpCodes.Br_S.Op2;

        HotfixSize.EnsureUnchanged(data, origBytes.Length);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] PetMainPanel.OnClickPetRecycleCallback: 跳过捕捉金卡拦截");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (var stubDir in Program.ResolveRefStubDirsPublic())
            {
                resolver.AddSearchDirectory(stubDir);
            }

            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            return IsAlreadyPatched(FindClickCallback(asm));
        }
        catch
        {
            return false;
        }
    }

    private static MethodDefinition FindClickCallback(AssemblyDefinition asm)
    {
        var panel = asm.MainModule.Types.FirstOrDefault(t => t.Name == "PetMainPanel")
            ?? throw new InvalidOperationException("未找到 PetMainPanel");
        return panel.Methods.FirstOrDefault(m => m.Name == "OnClickPetRecycleCallback" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 OnClickPetRecycleCallback");
    }

    private static bool IsAlreadyPatched(MethodDefinition method)
    {
        var ins = method.Body.Instructions;
        for (var i = 0; i < ins.Count - 2; i++)
        {
            if (!IsCallNamed(ins[i], "get_ETRARE"))
            {
                continue;
            }

            if (ins[i + 1].OpCode == OpCodes.Pop && ins[i + 2].OpCode == OpCodes.Br_S)
            {
                return HasTipString(method);
            }

            return false;
        }

        return false;
    }

    private static (Instruction ldc, Instruction bne) FindCaptureCheck(MethodDefinition method)
    {
        if (!HasTipString(method))
        {
            throw new InvalidOperationException("OnClickPetRecycleCallback 没有捕捉金卡提示，无法定位拦截");
        }

        var ins = method.Body.Instructions;
        for (var i = 0; i < ins.Count - 4; i++)
        {
            if (!IsCallNamed(ins[i], "get_ETRARE"))
            {
                continue;
            }

            if (ins[i + 1].OpCode != OpCodes.Ldc_I4_3 || ins[i + 2].OpCode != OpCodes.Bne_Un_S)
            {
                continue;
            }

            var hasCapture = false;
            for (var j = i + 3; j < Math.Min(ins.Count, i + 12); j++)
            {
                if (IsCallNamed(ins[j], "get_IsCapturePet"))
                {
                    hasCapture = true;
                    break;
                }
            }

            if (!hasCapture)
            {
                continue;
            }

            return (ins[i + 1], ins[i + 2]);
        }

        throw new InvalidOperationException("未找到 ETRARE==3 && IsCapturePet 拦截");
    }

    private static bool HasTipString(MethodDefinition method)
    {
        return method.Body.Instructions.Any(insn => insn.Operand is string s && s == TipText);
    }

    private static bool IsCallNamed(Instruction insn, string name)
    {
        return insn.Operand is MethodReference mr && mr.Name == name;
    }

    private static (int codeFileOff, int codeSize) GetMethodCodeRange(byte[] pe, int rva)
    {
        var off = PeLayout.RvaToOffset(pe, rva);
        var flags = pe[off];
        if ((flags & 0x3) == 0x2)
        {
            return (off + 1, flags >> 2);
        }

        if ((flags & 0x3) == 0x3)
        {
            return (off + 12, BitConverter.ToInt32(pe, off + 4));
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2} @ RVA 0x{rva:X}");
    }
}
