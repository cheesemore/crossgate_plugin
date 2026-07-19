using System.Buffers.Binary;

using Mono.Cecil;

using Mono.Cecil.Cil;



namespace CrossgateMod.Patcher;



internal static class IlSerializer

{

    public static void RecalculateOffsets(MethodBody body)

    {

        var offset = 0;

        foreach (var insn in body.Instructions)

        {

            insn.Offset = offset;

            offset += insn.GetSize();

        }

    }



    public static byte[] Serialize(MethodBody body, UserStringHeap? userStrings = null)

    {

        return Serialize(body, userStrings, null);

    }



    public static byte[] Serialize(MethodBody body, byte[] origMethodBodySnapshot)

    {

        return Serialize(body, null, ExtractLdstrTokens(origMethodBodySnapshot));

    }



    public static byte[] Serialize(
        MethodBody body,
        UserStringHeap? userStrings,
        IReadOnlyList<uint>? ldstrTokensFromSnapshot)

    {

        using var ms = new MemoryStream();

        var bw = new BinaryWriter(ms);

        var ldstrCursor = ldstrTokensFromSnapshot == null ? null : new LdstrTokenCursor(ldstrTokensFromSnapshot);



        byte[] code;

        using (var codeMs = new MemoryStream())

        {

            var codeBw = new BinaryWriter(codeMs);

            foreach (var insn in body.Instructions)

            {

                WriteInstruction(codeBw, insn, body, userStrings, ldstrCursor);

            }



            code = codeMs.ToArray();

        }



        var codeSize = code.Length;

        if (codeSize <= 63 && body.MaxStackSize <= 8 && body.HasExceptionHandlers == false && body.Variables.Count == 0 && !body.InitLocals)

        {

            var flags = (byte)(0x02 | (codeSize << 2));

            bw.Write(flags);

        }

        else

        {

            ushort flags = 0x3003;

            if (body.InitLocals)

            {

                flags |= 0x10;

            }



            if (body.ExceptionHandlers.Count > 0)

            {

                flags |= 0x08;

            }



            bw.Write(flags);

            bw.Write((ushort)body.MaxStackSize);

            bw.Write(codeSize);

            var localVarToken = body.InitLocals && body.Variables.Count > 0
                ? body.LocalVarToken.ToUInt32()
                : 0u;
            bw.Write(localVarToken);

        }



        bw.Write(code);

        foreach (var handler in body.ExceptionHandlers)

        {

            WriteExceptionHandler(bw, handler, body);

        }



        return ms.ToArray();

    }



    private static uint GetStringToken(MethodBody body, string value, UserStringHeap? userStrings)

    {

        if (userStrings != null)

        {

            return userStrings.GetOrReuseToken(value);

        }



        throw new InvalidOperationException($"无法解析 ldstr 令牌: {value}");

    }



    private static List<uint> ExtractLdstrTokens(byte[] methodBody)

    {

        var code = GetMethodCodeSpan(methodBody);

        var tokens = new List<uint>();

        for (var i = 0; i < code.Length; i++)

        {

            if (code[i] != 0x72 || i + 4 >= code.Length)

            {

                continue;

            }



            tokens.Add(BinaryPrimitives.ReadUInt32LittleEndian(code.Slice(i + 1)));

            i += 4;

        }



        return tokens;

    }



    private static ReadOnlySpan<byte> GetMethodCodeSpan(byte[] methodBody)

    {

        var flags = methodBody[0];

        if ((flags & 0x3) == 0x2)

        {

            return methodBody.AsSpan(1);

        }



        if ((flags & 0x3) == 0x3)

        {

            return methodBody.AsSpan(12);

        }



        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");

    }



    private sealed class LdstrTokenCursor(IReadOnlyList<uint> tokens)

    {

        private int _index;

        public uint? Next()

        {

            if (_index >= tokens.Count)

            {

                return null;

            }



            return tokens[_index++];

        }

    }



    private static void WriteInstruction(
        BinaryWriter bw,
        Instruction insn,
        MethodBody body,
        UserStringHeap? userStrings,
        LdstrTokenCursor? ldstrCursor)
    {
        WriteOpCode(bw, insn.OpCode);

        switch (insn.OpCode.OperandType)

        {

            case OperandType.InlineNone:

                break;

            case OperandType.ShortInlineBrTarget:

                bw.Write((sbyte)GetBranchOffset(insn, (Instruction)insn.Operand!, 1));

                break;

            case OperandType.InlineBrTarget:

                bw.Write(GetBranchOffset(insn, (Instruction)insn.Operand!, 4));

                break;

            case OperandType.ShortInlineI:

                bw.Write((sbyte)(sbyte)insn.Operand!);

                break;

            case OperandType.InlineI:

                bw.Write((int)insn.Operand!);

                break;

            case OperandType.InlineI8:

                bw.Write((long)insn.Operand!);

                break;

            case OperandType.ShortInlineR:

                bw.Write((float)insn.Operand!);

                break;

            case OperandType.InlineR:

                bw.Write((double)insn.Operand!);

                break;

            case OperandType.InlineString:
                {
                    var token = ldstrCursor?.Next() ?? GetStringToken(body, (string)insn.Operand!, userStrings);
                    bw.Write(BitConverter.GetBytes(token));
                    break;
                }

            case OperandType.InlineSig:
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineTok:
                {
                    var token = ((IMetadataTokenProvider)insn.Operand!).MetadataToken.ToUInt32();
                    bw.Write(BitConverter.GetBytes(token));
                    break;
                }

            case OperandType.InlineVar:

                bw.Write((short)((VariableDefinition)insn.Operand!).Index);

                break;

            case OperandType.ShortInlineVar:

                bw.Write((byte)((VariableDefinition)insn.Operand!).Index);

                break;

            case OperandType.InlineSwitch:

                var targets = (Instruction[])insn.Operand!;

                bw.Write(targets.Length);

                var baseOff = insn.Offset + 1 + 4 + targets.Length * 4;

                foreach (var t in targets)

                {

                    bw.Write(t.Offset - baseOff);

                }



                break;

            case OperandType.ShortInlineArg:

                bw.Write((byte)((ParameterDefinition)insn.Operand!).Index);

                break;

            case OperandType.InlineArg:

                bw.Write((short)((ParameterDefinition)insn.Operand!).Index);

                break;

            default:

                throw new NotSupportedException(insn.OpCode.OperandType.ToString());

        }

    }



    private static void WriteOpCode(BinaryWriter bw, OpCode opCode)
    {
        bw.Write((byte)(opCode.Value & 0xFF));
        if (opCode.Size == 2)
        {
            bw.Write((byte)(opCode.Value >> 8));
        }
    }

    private static int GetBranchOffset(Instruction from, Instruction target, int operandSize)
    {
        return target.Offset - (from.Offset + from.OpCode.Size + operandSize);
    }



    private static void WriteExceptionHandler(BinaryWriter bw, ExceptionHandler handler, MethodBody body)

    {

        var isFat = body.Instructions.Count > ushort.MaxValue;

        if (isFat)

        {

            bw.Write(0x41);

            bw.Write((int)handler.HandlerType);

            bw.Write(handler.TryStart.Offset);

            bw.Write(handler.TryEnd.Offset - handler.TryStart.Offset);

            bw.Write(handler.HandlerStart.Offset);

            bw.Write(handler.HandlerEnd.Offset - handler.HandlerStart.Offset);

            if (handler.CatchType != null)

            {

                bw.Write(handler.CatchType.MetadataToken.ToUInt32());

            }

            else

            {

                bw.Write(0);

            }

        }

        else

        {

            bw.Write(0x01);

            bw.Write((ushort)handler.TryStart.Offset);

            bw.Write((ushort)(handler.TryEnd.Offset - handler.TryStart.Offset));

            bw.Write((ushort)handler.HandlerStart.Offset);

            bw.Write((ushort)(handler.HandlerEnd.Offset - handler.HandlerStart.Offset));

            bw.Write((ushort)(int)handler.HandlerType);

            if (handler.CatchType != null)

            {

                bw.Write(handler.CatchType.MetadataToken.ToUInt32());

            }

            else

            {

                bw.Write(0);

            }

        }

    }

}


