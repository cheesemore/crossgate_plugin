using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class CompactIlBody
{
    public static byte[] BuildTiny(byte[] code)
    {
        if (code.Length > 63)
        {
            throw new InvalidOperationException($"tiny method 代码过长: {code.Length}");
        }

        var body = new byte[1 + code.Length];
        body[0] = (byte)(0x02 | (code.Length << 2));
        Array.Copy(code, 0, body, 1, code.Length);
        return body;
    }

    public static byte[] BuildLdargCallRet(MethodReference method)
    {
        var code = new byte[7];
        code[0] = (byte)OpCodes.Ldarg_0.Value;
        code[1] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(method.MetadataToken.ToUInt32()).CopyTo(code, 2);
        code[6] = (byte)OpCodes.Ret.Value;
        return BuildTiny(code);
    }

    public static byte[] BuildCallCallvirtRet(MethodReference call, MethodReference callvirt)
    {
        var code = new byte[11];
        code[0] = (byte)OpCodes.Call.Value;
        BitConverter.GetBytes(call.MetadataToken.ToUInt32()).CopyTo(code, 1);
        code[5] = (byte)OpCodes.Callvirt.Value;
        BitConverter.GetBytes(callvirt.MetadataToken.ToUInt32()).CopyTo(code, 6);
        code[10] = (byte)OpCodes.Ret.Value;
        return BuildTiny(code);
    }

    public static byte[] BuildTinyFromExistingMethodBody(byte[] methodBody)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var code = new byte[codeSize];
        Array.Copy(methodBody, codeOffset, code, 0, codeSize);
        EnsureNoInstanceThisUsage(code);
        return BuildTiny(code);
    }

    private static void EnsureNoInstanceThisUsage(byte[] code)
    {
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == (byte)OpCodes.Ldarg_0.Value)
            {
                throw new InvalidOperationException("内联 IL 使用了 ldarg.0，不能用于 static OnClickCustom");
            }

            if (code[i] != 0xFE || i + 1 >= code.Length)
            {
                continue;
            }

            var op2 = code[i + 1];
            if ((op2 == (byte)OpCodes.Ldarg.Value || op2 == (byte)OpCodes.Ldarg_S.Value)
                && i + 2 < code.Length
                && code[i + 2] == 0)
            {
                throw new InvalidOperationException("内联 IL 使用了 ldarg 0，不能用于 static OnClickCustom");
            }
        }
    }

    public static void SwapLdftnMethodToken(byte[] methodBody, MethodReference from, MethodReference to)
    {
        SwapTokenAfterOpcode(methodBody, 0xFE, (byte)OpCodes.Ldftn.Value, from.MetadataToken.ToUInt32(), to.MetadataToken.ToUInt32());
    }

    public static void SwapLdfldTokensBeforeGetTitle(
        byte[] methodBody,
        FieldReference from,
        FieldReference to,
        MethodReference getTitle)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var fromToken = BitConverter.GetBytes(from.MetadataToken.ToUInt32());
        var toToken = BitConverter.GetBytes(to.MetadataToken.ToUInt32());
        var titleToken = BitConverter.GetBytes(getTitle.MetadataToken.ToUInt32());

        for (var i = codeOffset; i <= codeOffset + codeSize - 10; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldfld.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != fromToken[0]
                || methodBody[i + 2] != fromToken[1]
                || methodBody[i + 3] != fromToken[2]
                || methodBody[i + 4] != fromToken[3])
            {
                continue;
            }

            if (methodBody[i + 5] != (byte)OpCodes.Callvirt.Value)
            {
                continue;
            }

            if (methodBody[i + 6] != titleToken[0]
                || methodBody[i + 7] != titleToken[1]
                || methodBody[i + 8] != titleToken[2]
                || methodBody[i + 9] != titleToken[3])
            {
                continue;
            }

            toToken.CopyTo(methodBody, i + 1);
        }
    }

    public static void ReplaceBossDefaultTab(byte[] methodBody, FieldReference tabField, MethodReference towerHandler)
    {
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var tabToken = BitConverter.GetBytes(tabField.MetadataToken.ToUInt32());
        var callToken = BitConverter.GetBytes(towerHandler.MetadataToken.ToUInt32());

        for (var i = codeOffset; i <= codeOffset + codeSize - 12; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Ldarg_0.Value
                || methodBody[i + 1] != (byte)OpCodes.Ldfld.Value
                || methodBody[i + 2] != tabToken[0]
                || methodBody[i + 3] != tabToken[1]
                || methodBody[i + 4] != tabToken[2]
                || methodBody[i + 5] != tabToken[3]
                || methodBody[i + 6] != (byte)OpCodes.Ldc_I4_1.Value
                || methodBody[i + 7] != (byte)OpCodes.Callvirt.Value)
            {
                continue;
            }

            methodBody[i + 1] = (byte)OpCodes.Ldc_I4_1.Value;
            methodBody[i + 2] = (byte)OpCodes.Call.Value;
            callToken.CopyTo(methodBody, i + 3);
            methodBody[i + 7] = (byte)OpCodes.Nop.Value;
            methodBody[i + 8] = (byte)OpCodes.Nop.Value;
            methodBody[i + 9] = (byte)OpCodes.Nop.Value;
            methodBody[i + 10] = (byte)OpCodes.Nop.Value;
            methodBody[i + 11] = (byte)OpCodes.Nop.Value;
            return;
        }

        throw new InvalidOperationException("未找到 BOSSChallengePanel 默认 Tab 样例");
    }

    public static bool ContainsLdftnMethod(byte[] methodBody, MethodReference method)
    {
        var token = BitConverter.GetBytes(method.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        for (var i = codeOffset; i <= codeOffset + codeSize - 6; i++)
        {
            if (methodBody[i] == 0xFE
                && methodBody[i + 1] == (byte)OpCodes.Ldftn.Value
                && methodBody[i + 2] == token[0]
                && methodBody[i + 3] == token[1]
                && methodBody[i + 4] == token[2]
                && methodBody[i + 5] == token[3])
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsCallMethod(byte[] methodBody, MethodReference method)
    {
        var token = BitConverter.GetBytes(method.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Call.Value)
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

    public static void SwapCallMethodToken(
        byte[] methodBody,
        MethodReference from,
        MethodReference to)
    {
        var fromToken = BitConverter.GetBytes(from.MetadataToken.ToUInt32());
        var toToken = BitConverter.GetBytes(to.MetadataToken.ToUInt32());
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);
        var swapped = false;

        for (var i = codeOffset; i <= codeOffset + codeSize - 5; i++)
        {
            if (methodBody[i] != (byte)OpCodes.Call.Value)
            {
                continue;
            }

            if (methodBody[i + 1] != fromToken[0]
                || methodBody[i + 2] != fromToken[1]
                || methodBody[i + 3] != fromToken[2]
                || methodBody[i + 4] != fromToken[3])
            {
                continue;
            }

            toToken.CopyTo(methodBody, i + 1);
            swapped = true;
        }

        if (!swapped)
        {
            throw new InvalidOperationException($"未找到 call {from.Name} 以替换为 {to.Name}");
        }
    }

    private static void SwapTokenAfterOpcode(
        byte[] methodBody,
        byte prefix,
        byte opcode,
        uint fromToken,
        uint toToken)
    {
        var from = BitConverter.GetBytes(fromToken);
        var to = BitConverter.GetBytes(toToken);
        var codeOffset = GetCodeOffset(methodBody);
        var codeSize = GetCodeSize(methodBody);

        for (var i = codeOffset; i <= codeOffset + codeSize - 6; i++)
        {
            if (methodBody[i] != prefix || methodBody[i + 1] != opcode)
            {
                continue;
            }

            if (methodBody[i + 2] != from[0]
                || methodBody[i + 3] != from[1]
                || methodBody[i + 4] != from[2]
                || methodBody[i + 5] != from[3])
            {
                continue;
            }

            to.CopyTo(methodBody, i + 2);
            return;
        }

        throw new InvalidOperationException($"未找到 ldftn token 0x{fromToken:X8}");
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
