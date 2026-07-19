using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 侧栏客服 → PetManager.OpenPetMain(SelectPlayerUid, -1, 3, -1)
/// openPage=3 对应 PET_TYPE.RESET（PetSetChildPanel：洗档 / 改造 / 重构）。
/// </summary>
internal static class PetReformCustomerIlBuilder
{
    // PET_TYPE.RESET
    private const int OpenPageReset = 3;

    public static byte[] BuildOpenBody(AssemblyDefinition asm)
    {
        var mgr = FindManagerInstanceGetter(asm, "PetManager");
        var selectUid = FindSelectPlayerUidGetter(asm);
        var openPetMain = asm.MainModule.Types.First(t => t.Name == "PetManager")
            .Methods.First(m =>
                m.Name == "OpenPetMain"
                && m.Parameters.Count == 4
                && m.Parameters[0].ParameterType.FullName == "System.String"
                && m.Parameters[1].ParameterType.FullName == "System.Int32"
                && m.Parameters[2].ParameterType.FullName == "System.Int32"
                && m.Parameters[3].ParameterType.FullName == "System.Int32");

        // call get_Instance; call get_SelectPlayerUid; ldc.i4.m1; ldc.i4.3; ldc.i4.m1; callvirt OpenPetMain; ret
        var code = new byte[19];
        var i = 0;
        code[i++] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(mgr.MetadataToken.ToUInt32()).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(selectUid.MetadataToken.ToUInt32()).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldc_I4_M1.Value;
        code[i++] = (byte)OpCodes.Ldc_I4_3.Value;
        code[i++] = (byte)OpCodes.Ldc_I4_M1.Value;
        code[i++] = (byte)OpCodes.Callvirt.Value;
        BitConverter.GetBytes(openPetMain.MetadataToken.ToUInt32()).CopyTo(code, i);
        i += 4;
        code[i] = (byte)OpCodes.Ret.Value;
        return CompactIlBody.BuildTiny(code);
    }

    public static bool IsCustomerOpen(byte[] peBytes, AssemblyDefinition asm, MethodDefinition callback)
    {
        var openPetMain = asm.MainModule.Types.FirstOrDefault(t => t.Name == "PetManager")
            ?.Methods.FirstOrDefault(m => m.Name == "OpenPetMain" && m.Parameters.Count == 4);
        if (openPetMain == null)
        {
            return false;
        }

        var snapshot = ReadMethodBodyFromPe(peBytes, callback.RVA);
        if (!ContainsCallvirtToken(snapshot, openPetMain.MetadataToken.ToUInt32()))
        {
            return false;
        }

        // 确认 openPage = 3（ldc.i4.3）
        var codeOffset = GetCodeOffset(snapshot);
        var codeSize = GetCodeSize(snapshot);
        for (var i = codeOffset; i < codeOffset + codeSize; i++)
        {
            if (snapshot[i] == (byte)OpCodes.Ldc_I4_3.Value)
            {
                return true;
            }
        }

        return false;
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
