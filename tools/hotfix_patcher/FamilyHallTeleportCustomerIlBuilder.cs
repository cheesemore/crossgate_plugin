using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 侧栏客服 → FamilyManager.SendFamily("NPC传送", 0, "1")（同 FamilyHallChildPanel.OnClickGoFamilyCallback）。
/// </summary>
internal static class FamilyHallTeleportCustomerIlBuilder
{
    public static byte[] BuildOpenBody(byte[] pe, AssemblyDefinition asm)
    {
        var getInstance = FindManagerInstanceGetter(asm, "FamilyManager");
        var sendFamily = asm.MainModule.Types.First(t => t.Name == "FamilyManager")
            .Methods.First(m =>
                m.Name == "SendFamily"
                && m.Parameters.Count >= 3
                && m.Parameters[0].ParameterType.FullName == "System.String"
                && m.Parameters[1].ParameterType.FullName == "System.Int32"
                && m.Parameters[2].ParameterType.FullName == "System.String");

        var us = UserStringHeap.FromPe(pe);
        var npcTeleport = us.GetOrReuseToken("NPC传送");
        var one = us.GetOrReuseToken("1");
        var empty = us.GetOrReuseToken("");

        // call get_Instance; ldstr "NPC传送"; ldc.i4.0; ldstr "1"; ldstr ""; ldstr ""; ldc.i4.0; callvirt SendFamily; ret
        var code = new byte[33];
        var i = 0;
        code[i++] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(getInstance.MetadataToken.ToUInt32()).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldstr.Value;
        BitConverter.GetBytes(npcTeleport).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldc_I4_0.Value;
        code[i++] = (byte)OpCodes.Ldstr.Value;
        BitConverter.GetBytes(one).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldstr.Value;
        BitConverter.GetBytes(empty).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldstr.Value;
        BitConverter.GetBytes(empty).CopyTo(code, i);
        i += 4;
        code[i++] = (byte)OpCodes.Ldc_I4_0.Value;
        code[i++] = (byte)OpCodes.Callvirt.Value;
        BitConverter.GetBytes(sendFamily.MetadataToken.ToUInt32()).CopyTo(code, i);
        i += 4;
        code[i] = (byte)OpCodes.Ret.Value;
        return CompactIlBody.BuildTiny(code);
    }

    public static bool IsCustomerOpen(byte[] peBytes, AssemblyDefinition asm, MethodDefinition callback)
    {
        var sendFamily = asm.MainModule.Types.FirstOrDefault(t => t.Name == "FamilyManager")
            ?.Methods.FirstOrDefault(m =>
                m.Name == "SendFamily"
                && m.Parameters.Count >= 3);
        if (sendFamily == null)
        {
            return false;
        }

        var snapshot = ReadMethodBodyFromPe(peBytes, callback.RVA);
        if (!ContainsCallvirtToken(snapshot, sendFamily.MetadataToken.ToUInt32()))
        {
            return false;
        }

        var us = UserStringHeap.FromPe(peBytes);
        if (!us.HasString("NPC传送"))
        {
            return false;
        }

        var token = us.GetOrReuseToken("NPC传送");
        return ContainsLdstrToken(snapshot, token);
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

    private static bool ContainsLdstrToken(byte[] methodBody, uint stringToken)
    {
        var token = BitConverter.GetBytes(stringToken);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldstr.Value)
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
