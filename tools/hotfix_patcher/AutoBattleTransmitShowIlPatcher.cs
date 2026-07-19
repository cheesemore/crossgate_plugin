using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 挂机导航面板：不显示 m_Btn_Transmit，将 m_Btn_Go 点击改为 OnClickTransmitCallback。
/// </summary>
internal static class AutoBattleTransmitShowIlPatcher
{
    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var sniffTargets = false;
        var sniffApplied = false;

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
                case "--sniff-targets":
                    sniffTargets = true;
                    break;
                case "--sniff-applied":
                    sniffApplied = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher auto-battle-transmit-show-patch --hotfix <hotfix> --output <out>\n" +
                "      HotfixPatcher auto-battle-transmit-show-patch --hotfix <hotfix> --sniff-targets\n" +
                "      HotfixPatcher auto-battle-transmit-show-patch --hotfix <hotfix> --sniff-applied");
            return 1;
        }

        if (sniffApplied)
        {
            return RunSniffApplied(source) ? 0 : 1;
        }

        if (sniffTargets)
        {
            return RunSniffTargets(source) ? 0 : 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 挂机面板「前往→传送」补丁已写入: " + output);
        return 0;
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

        var panel = asm.MainModule.Types.First(t => t.Name == "AutoBattleNavigationPanel");
        var onShow = panel.Methods.First(m => m.Name == "OnShow" && m.HasBody);
        var goCallback = panel.Methods.First(m => m.Name == "OnClickGoCallback" && m.HasBody);
        var transmitCallback = panel.Methods.First(m => m.Name == "OnClickTransmitCallback" && m.HasBody);
        var goField = panel.Fields.First(f => f.Name == "m_Btn_Go");
        var getTitle = FindCustomButtonGetTitle(asm);

        if (HasLegacyShowPatch(onShow))
        {
            throw new InvalidOperationException(
                "检测到旧版「显示传送按钮」补丁（OnShow 内 m_Btn_Transmit.SetActive(true)）。"
                + "请先用 GUI「一键还原」或从 hotfix.dll.bytes.orig 恢复后再打本补丁。");
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, onShow.RVA);

        if (HasGoTransmitPatch(snapshot, goCallback, transmitCallback, goField, getTitle))
        {
            Console.WriteLine("[SKIP] AutoBattleNavigationPanel 已是「前往→传送」补丁状态");
            return;
        }

        var newBody = (byte[])snapshot.Clone();
        CompactIlBody.SwapLdftnMethodToken(newBody, goCallback, transmitCallback);
        BinaryPeWriter.ReplaceMethodBody(data, onShow.RVA, snapshot, newBody);
        Console.WriteLine("[PATCH] OnShow: m_Btn_Go -> OnClickTransmitCallback（按钮仍显示「前往」）");

        HotfixSize.EnsureUnchanged(data, expectedSize);

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static bool RunSniffTargets(string hotfixPath)
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

        if (!asm.MainModule.Types.Any(t => t.Name == "AutoBattleNavigationPanel"))
        {
            Console.WriteLine("[FAIL] 未找到 AutoBattleNavigationPanel");
            return false;
        }

        var panel = asm.MainModule.Types.First(t => t.Name == "AutoBattleNavigationPanel");
        if (!panel.Fields.Any(f => f.Name == "m_Btn_Go")
            || !panel.Fields.Any(f => f.Name == "m_Btn_Transmit")
            || !panel.Methods.Any(m => m.Name == "OnClickGoCallback" && m.HasBody)
            || !panel.Methods.Any(m => m.Name == "OnClickTransmitCallback" && m.HasBody))
        {
            Console.WriteLine("[FAIL] 缺少 m_Btn_Go / m_Btn_Transmit 或传送回调");
            return false;
        }

        Console.WriteLine("[SNIFF] 挂机面板传送目标齐全（AutoBattleNavigationPanel + Go/Transmit 回调）");
        return true;
    }

    public static bool SniffApplied(string hotfixPath)
    {
        var origBytes = File.ReadAllBytes(hotfixPath);
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

        var panel = asm.MainModule.Types.First(t => t.Name == "AutoBattleNavigationPanel");
        var onShow = panel.Methods.First(m => m.Name == "OnShow" && m.HasBody);
        var goCallback = panel.Methods.First(m => m.Name == "OnClickGoCallback" && m.HasBody);
        var transmitCallback = panel.Methods.First(m => m.Name == "OnClickTransmitCallback" && m.HasBody);
        var goField = panel.Fields.First(f => f.Name == "m_Btn_Go");
        var getTitle = FindCustomButtonGetTitle(asm);
        var snapshot = ReadMethodBodyFromPe(origBytes, onShow.RVA);
        return HasGoTransmitPatch(snapshot, goCallback, transmitCallback, goField, getTitle);
    }

    public static bool RunSniffApplied(string hotfixPath)
    {
        if (SniffApplied(hotfixPath))
        {
            Console.WriteLine("[SNIFF] 「前往→传送」补丁已生效（OnShow: m_Btn_Go → OnClickTransmitCallback）");
            return true;
        }

        Console.WriteLine("[SNIFF] 未打补丁：OnShow 仍为原版「前往」逻辑");
        return false;
    }

    private static MethodReference FindCustomButtonGetTitle(AssemblyDefinition asm)
    {
        foreach (var type in asm.MainModule.Types.Where(t => t.Name == "AutoBattleNavigationPanel"))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Callvirt
                        && ins.Operand is MethodReference called
                        && called.Name == "get_Title"
                        && called.DeclaringType.Name == "CustomButton")
                    {
                        return called;
                    }
                }
            }
        }

        throw new InvalidOperationException("未找到 CustomButton.get_Title");
    }

    private static bool HasGoTransmitPatch(
        byte[] methodBody,
        MethodReference goCallback,
        MethodReference transmitCallback,
        FieldReference goField,
        MethodReference getTitle)
    {
        if (CompactIlBody.ContainsLdftnMethod(methodBody, transmitCallback)
            && !CompactIlBody.ContainsLdftnMethod(methodBody, goCallback))
        {
            return true;
        }

        if (CompactIlBody.ContainsLdftnMethod(methodBody, goCallback))
        {
            return false;
        }

        var goToken = BitConverter.GetBytes(goField.MetadataToken.ToUInt32());
        var titleToken = BitConverter.GetBytes(getTitle.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 10; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldfld.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != goToken[0]
                || methodBody[i + 2] != goToken[1]
                || methodBody[i + 3] != goToken[2]
                || methodBody[i + 4] != goToken[3])
            {
                continue;
            }

            if (methodBody[i + 5] != (byte)OpCodes.Callvirt.Value)
            {
                continue;
            }

            if (methodBody[i + 6] == titleToken[0]
                && methodBody[i + 7] == titleToken[1]
                && methodBody[i + 8] == titleToken[2]
                && methodBody[i + 9] == titleToken[3])
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLegacyShowPatch(MethodDefinition onShow)
    {
        var sawTransmitField = false;
        foreach (var ins in onShow.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldfld
                && ins.Operand is FieldReference field
                && field.Name == "m_Btn_Transmit")
            {
                sawTransmitField = true;
            }

            if (sawTransmitField
                && ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == "SetActive"
                && called.DeclaringType.Name == "GameObject")
            {
                return true;
            }
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
