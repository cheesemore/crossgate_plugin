using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>侧栏客服 → BattleAutoSkillManager.OpenAutoSkillSettingPanel(SelectPlayerUid)。</summary>
internal static class AutoSkillCustomerIlBuilder
{
    public static byte[] BuildOpenBody(AssemblyDefinition asm)
    {
        var mgr = FindManagerInstanceGetter(asm, "BattleAutoSkillManager");
        var selectUid = FindSelectPlayerUidGetter(asm);
        var openPanel = asm.MainModule.Types.First(t => t.Name == "BattleAutoSkillManager")
            .Methods.First(m => m.Name == "OpenAutoSkillSettingPanel" && m.Parameters.Count == 1);

        var code = new byte[16];
        code[0] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(mgr.MetadataToken.ToUInt32()).CopyTo(code, 1);
        code[5] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(selectUid.MetadataToken.ToUInt32()).CopyTo(code, 6);
        code[10] = (byte)OpCodes.Callvirt.Value;
        BitConverter.GetBytes(openPanel.MetadataToken.ToUInt32()).CopyTo(code, 11);
        code[15] = (byte)OpCodes.Ret.Value;
        return CompactIlBody.BuildTiny(code);
    }

    public static bool IsCustomerOpen(byte[] peBytes, AssemblyDefinition asm, MethodDefinition callback)
    {
        var openPanel = asm.MainModule.Types.First(t => t.Name == "BattleAutoSkillManager")
            .Methods.First(m => m.Name == "OpenAutoSkillSettingPanel" && m.Parameters.Count == 1);
        var snapshot = ReadMethodBodyFromPe(peBytes, callback.RVA);
        return ContainsCallvirtToken(snapshot, openPanel.MetadataToken.ToUInt32());
    }

    private static MethodReference FindSelectPlayerUidGetter(AssemblyDefinition asm)
    {
        var holder = asm.MainModule.Types.First(t => t.Name == "PlayerDataHolder");
        var getter = holder.Properties.FirstOrDefault(p => p.Name == "SelectPlayerUid")?.GetMethod
            ?? holder.Methods.FirstOrDefault(m => m.Name == "get_SelectPlayerUid");
        if (getter == null)
        {
            throw new InvalidOperationException("未找到 PlayerDataHolder.SelectPlayerUid");
        }

        return getter;
    }

    private static MethodReference FindManagerInstanceGetter(AssemblyDefinition asm, string managerName)
    {
        foreach (var type in asm.MainModule.Types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Call || ins.Operand is not MethodReference mr)
                    {
                        continue;
                    }

                    if (mr.Name == "get_Instance"
                        && mr.DeclaringType.Name.StartsWith("Manager`1", StringComparison.Ordinal)
                        && mr.DeclaringType.FullName.Contains(managerName, StringComparison.Ordinal))
                    {
                        return mr;
                    }
                }
            }
        }

        throw new InvalidOperationException($"未找到 Manager<{managerName}>.get_Instance");
    }

    private static bool ContainsCallvirtToken(byte[] methodBody, uint methodToken)
    {
        var token = BitConverter.GetBytes(methodToken);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Callvirt.Value)
            {
                continue;
            }

            if (methodBody[i + 1] == token[0]
                && methodBody[i + 2] == token[1]
                && methodBody[i + 3] == token[2]
                && methodBody[i + 4] == token[3])
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

        if ((flags & 0x3) == 0x3)
        {
            return BitConverter.ToInt32(methodBody, 4);
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");
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
