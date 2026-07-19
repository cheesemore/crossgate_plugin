using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>露比试炼：客户端直接 Open(空 Proto)，绕过服务端「同步数据」。</summary>
internal static class RubyTrialOpenIlBuilder
{
    public static byte[] BuildOpenBody(MethodDefinition callback, AssemblyDefinition asm, byte[] snapshot)
    {
        var module = callback.Module;
        var body = callback.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        var getUIPanel = module.ImportReference(FindGetUIPanel(asm, "RubyTrialPanel"));
        var protoType = asm.MainModule.Types.First(t => t.Name == "Proto_SC_LoopyTrial");
        var protoCtor = protoType.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        var setKUid = protoType.Methods.First(m => m.Name == "set_KUid");
        var rubyOpen = asm.MainModule.Types.First(t => t.Name == "RubyTrialPanel")
            .Methods.First(m => m.Name == "Open" && m.HasParameters && m.Parameters.Count == 1);
        var mainUid = module.ImportReference(
            asm.MainModule.Types.First(t => t.Name == "PlayerDataHolder")
                .Fields.First(f => f.Name == "MainPlayerUid"));

        il.Append(il.Create(OpCodes.Call, getUIPanel));
        il.Append(il.Create(OpCodes.Newobj, module.ImportReference(protoCtor)));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldsfld, mainUid));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(setKUid)));
        il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(rubyOpen)));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        return IlSerializer.Serialize(body, snapshot);
    }

    public static void PatchSelfTeamAsClaim(AssemblyDefinition asm, byte[] origBytes, byte[] data)
    {
        var panel = asm.MainModule.Types.First(t => t.Name == "RubyTrialPanel");
        var selfTeam = panel.Methods.First(m => m.Name == "OnClickSelfTeam" && m.HasBody);
        if (IsSelfTeamClaimPatched(selfTeam))
        {
            Console.WriteLine("[SKIP] RubyTrialPanel.OnClickSelfTeam 已是领取奖励");
            return;
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, selfTeam.RVA);
        var module = asm.MainModule;
        var body = selfTeam.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        var bountyMgr = module.ImportReference(FindManagerInstanceGetter(asm, "BountyOfferedManager"));
        var sendMsg = module.ImportReference(FindSendRubyTrialMsgFromGetAward(asm));
        var claimType = FindLdstrOperand(asm, "RubyTrialPanel", "OnClickGetAward", "领取奖励");
        var uidField = module.ImportReference(panel.Fields.First(f => f.Name == "m_Uid"));

        il.Append(il.Create(OpCodes.Call, bountyMgr));
        il.Append(il.Create(OpCodes.Ldstr, claimType));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, uidField));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Callvirt, sendMsg));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        var newBody = IlSerializer.Serialize(body, snapshot);
        BinaryPeWriter.ReplaceMethodBody(data, selfTeam.RVA, snapshot, newBody);
        Console.WriteLine("[PATCH] RubyTrialPanel.OnClickSelfTeam -> 领取奖励");
    }

    public static bool IsCustomerOpen(byte[] peBytes, AssemblyDefinition asm)
    {
        if (!IsSelfTeamClaimPatched(peBytes, asm))
        {
            return false;
        }

        var callback = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel")
            .Methods.First(m => m.Name == "OnClickCustom" && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(peBytes, callback.RVA);
        return LooksLikeRubyCustomerBody(snapshot);
    }

    private static bool LooksLikeRubyCustomerBody(byte[] methodBody)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var newobj = 0;
        var callvirt = 0;
        for (var i = codeOffset; i < codeOffset + codeSize; i++)
        {
            if (methodBody[i] == (byte)OpCodes.Newobj.Value)
            {
                newobj++;
            }

            if (methodBody[i] == (byte)OpCodes.Callvirt.Value)
            {
                callvirt++;
            }
        }

        return newobj == 1 && callvirt >= 2;
    }

    public static bool IsSelfTeamClaimPatched(byte[] peBytes, AssemblyDefinition asm)
    {
        var panel = asm.MainModule.Types.First(t => t.Name == "RubyTrialPanel");
        var selfTeam = panel.Methods.First(m => m.Name == "OnClickSelfTeam" && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(peBytes, selfTeam.RVA);
        var codeOffset = GetCodeOffset(snapshot);
        var codeSize = GetCodeSize(snapshot);
        if (codeSize > 64)
        {
            return false;
        }

        for (var i = codeOffset; i <= codeOffset + codeSize - 1; i++)
        {
            if (snapshot[i] != (byte)OpCodes.Ldc_I4_1.Value)
            {
                continue;
            }

            var hasCallvirt = false;
            for (var j = i + 1; j <= Math.Min(i + 4, codeOffset + codeSize - 1); j++)
            {
                if (snapshot[j] == (byte)OpCodes.Callvirt.Value)
                {
                    hasCallvirt = true;
                    break;
                }
            }

            if (!hasCallvirt)
            {
                continue;
            }

            for (var k = i + 1; k <= codeOffset + codeSize - 1; k++)
            {
                if (snapshot[k] == (byte)OpCodes.Ret.Value)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool ContainsRubyOpen(MethodDefinition callback)
    {
        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "Open"
                && mr.DeclaringType.Name == "RubyTrialPanel")
            {
                return true;
            }

            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference send
                && send.Name == "SendRubyTrialMsg")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelfTeamClaimPatched(MethodDefinition selfTeam)
    {
        var ins = selfTeam.Body.Instructions;
        if (ins.Count != 7)
        {
            return false;
        }

        return ins[0].OpCode == OpCodes.Call
            && ins[2].OpCode == OpCodes.Ldarg_0
            && ins[3].OpCode == OpCodes.Ldfld
            && ins[3].Operand is FieldReference uid && uid.Name == "m_Uid"
            && ins[4].OpCode == OpCodes.Ldc_I4_1
            && ins[5].OpCode == OpCodes.Callvirt
            && ins[5].Operand is MethodReference send && send.Name == "SendRubyTrialMsg"
            && ins[6].OpCode == OpCodes.Ret;
    }

    private static GenericInstanceMethod FindGetUIPanel(AssemblyDefinition asm, string panelTypeName)
    {
        foreach (var type in asm.MainModule.Types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Call || ins.Operand is not GenericInstanceMethod gim)
                    {
                        continue;
                    }

                    if (gim.Name == "GetUIPanel"
                        && gim.GenericArguments.Count > 0
                        && gim.GenericArguments[0].Name == panelTypeName)
                    {
                        return gim;
                    }
                }
            }
        }

        throw new InvalidOperationException($"未找到 UIManager.GetUIPanel<{panelTypeName}>");
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

    private static MethodReference FindSendRubyTrialMsgFromGetAward(AssemblyDefinition asm)
    {
        var getAward = asm.MainModule.Types.First(t => t.Name == "RubyTrialPanel")
            .Methods.First(m => m.Name == "OnClickGetAward" && m.HasBody);
        foreach (var ins in getAward.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "SendRubyTrialMsg")
            {
                return mr;
            }
        }

        throw new InvalidOperationException("未找到 OnClickGetAward 中的 SendRubyTrialMsg");
    }

    private static string FindLdstrOperand(AssemblyDefinition asm, string typeName, string methodName, string expected)
    {
        var type = asm.MainModule.Types.First(t => t.Name == typeName);
        var method = type.Methods.First(m => m.Name == methodName && m.HasBody);
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s == expected)
            {
                return s;
            }
        }

        throw new InvalidOperationException($"未找到 {typeName}.{methodName} 的 ldstr \"{expected}\"");
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
