using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal static class BridgeManualIlBuilder
{
    public static byte[] BuildUpdateHook(byte[] updateSnapshot, uint loaderMethodToken)
    {
        var code = ExtractCode(updateSnapshot);
        var hooked = new byte[6 + code.Length];
        hooked[0] = 0x16; // ldc.i4.0 -> OnApplicationPause(false)
        hooked[1] = 0x28;
        BitConverter.GetBytes(loaderMethodToken).CopyTo(hooked, 2);
        code.CopyTo(hooked, 6);
        return WrapMethodBody(hooked, maxStack: 8);
    }

    public static byte[] BuildLoaderBody(
        ImportTokenResolver.ResolvedTokens tokens,
        UserStringHeap userStrings,
        bool skipIfTypeLoaded)
    {
        var loadBytesToken = tokens.LoadBytesFromHotfixAssets;
        var assemblyLoadToken = tokens.AssemblyLoad;
        var getTypeStaticToken = tokens.TypeGetTypeStatic;
        var getTypeToken = tokens.AssemblyGetType;
        var getMethodToken = tokens.TypeGetMethod;
        var invokeToken = tokens.MethodInvoke;

        var code = new List<byte>();
        var branchPos = -1;
        if (skipIfTypeLoaded)
        {
            WriteLdstr(code, userStrings.GetOrReuseToken(
                BridgeLoaderIlBuilder.BridgeTypeName + ", " + BridgeLoaderIlBuilder.BridgeTypeName));
            WriteCall(code, getTypeStaticToken);
            branchPos = code.Count;
            code.Add(0x2D);
            code.Add(0x00);
        }

        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeDllAssetPath));
        WriteCall(code, loadBytesToken);
        code.Add(0x25);
        WriteCall(code, assemblyLoadToken);
        code.Add(0x25);
        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeTypeName));
        WriteCallvirt(code, getTypeToken);
        code.Add(0x25);
        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeBootstrapName));
        WriteCallvirt(code, getMethodToken);
        code.Add(0x14);
        code.Add(0x14);
        WriteCallvirt(code, invokeToken);
        code.Add(0x26);
        code.Add(0x2A);

        if (skipIfTypeLoaded && branchPos >= 0)
        {
            var retOffset = code.Count;
            code.Add(0x2A);
            code[branchPos + 1] = (byte)(retOffset - (branchPos + 2));
        }

        return WrapMethodBody(code.ToArray(), maxStack: 8);
    }

    public static byte[] BuildQuitTriggersPauseBody(uint pauseMethodDefToken)
    {
        var code = new List<byte> { 0x16 };
        WriteCall(code, pauseMethodDefToken);
        code.Add(0x2A);
        return WrapMethodBody(code.ToArray(), maxStack: 8);
    }

    public static byte[] BuildLoaderBodyLoadFrom(
        ImportTokenResolver.LoadFromTokens tokens,
        UserStringHeap userStrings,
        bool skipIfTypeLoaded)
    {
        var code = new List<byte>();
        var branchPos = -1;
        if (skipIfTypeLoaded)
        {
            WriteLdstr(code, userStrings.GetOrReuseToken(
                BridgeLoaderIlBuilder.BridgeTypeName + ", " + BridgeLoaderIlBuilder.BridgeTypeName));
            WriteCall(code, tokens.TypeGetTypeStatic);
            branchPos = code.Count;
            code.Add(0x2D);
            code.Add(0x00);
        }

        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeDllAssetPath));
        WriteCall(code, tokens.LoadBytesFromHotfixAssets);
        code.Add(0x0A); // stloc.0
        WriteLdsfld(code, tokens.FileUtilTempPathField);
        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeTempDllSuffix));
        WriteCall(code, tokens.StringConcat);
        code.Add(0x0B); // stloc.1
        code.Add(0x07); // ldloc.1
        code.Add(0x06); // ldloc.0
        WriteCall(code, tokens.FileWriteAllBytes);
        code.Add(0x07); // ldloc.1
        code.Add(0x14); // ldnull (Evidence)
        WriteCall(code, tokens.AssemblyLoadFrom);
        code.Add(0x25); // dup
        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeTypeName));
        WriteCallvirt(code, tokens.AssemblyGetType);
        code.Add(0x25);
        WriteLdstr(code, userStrings.GetOrReuseToken(BridgeLoaderIlBuilder.BridgeBootstrapName));
        WriteCallvirt(code, tokens.TypeGetMethod);
        code.Add(0x14);
        code.Add(0x14);
        WriteCallvirt(code, tokens.MethodInvoke);
        code.Add(0x26);
        code.Add(0x2A);

        if (skipIfTypeLoaded && branchPos >= 0)
        {
            var retOffset = code.Count;
            code.Add(0x2A);
            code[branchPos + 1] = (byte)(retOffset - (branchPos + 2));
        }

        return WrapMethodBody(code.ToArray(), maxStack: 16, initLocals: true);
    }

    private static void WriteLdsfld(List<byte> code, uint token)
    {
        code.Add(0x7E);
        code.AddRange(BitConverter.GetBytes(token));
    }

    private static void WriteLdstr(List<byte> code, uint token)
    {
        code.Add(0x72);
        code.AddRange(BitConverter.GetBytes(token));
    }

    private static void WriteCall(List<byte> code, uint token)
    {
        code.Add(0x28);
        code.AddRange(BitConverter.GetBytes(token));
    }

    private static void WriteCallvirt(List<byte> code, uint token)
    {
        code.Add(0x6F);
        code.AddRange(BitConverter.GetBytes(token));
    }

    private static byte[] ExtractCode(byte[] methodBody)
    {
        var flags = methodBody[0];
        if ((flags & 0x3) == 0x2)
        {
            var codeSize = flags >> 2;
            return methodBody[1..(1 + codeSize)];
        }

        if ((flags & 0x3) == 0x3)
        {
            var codeSize = BitConverter.ToInt32(methodBody, 4);
            return methodBody[12..(12 + codeSize)];
        }

        throw new InvalidOperationException($"未知 method header 0x{flags:X2}");
    }

    private static byte[] WrapMethodBody(byte[] code, int maxStack, bool initLocals = false)
    {
        if (code.Length <= 63 && maxStack <= 8 && !initLocals)
        {
            var tiny = new byte[1 + code.Length];
            tiny[0] = (byte)(0x02 | (code.Length << 2));
            code.CopyTo(tiny, 1);
            return tiny;
        }

        var fat = new byte[12 + code.Length];
        var flags = (ushort)(0x3003 | (initLocals ? 0x0010 : 0));
        BitConverter.GetBytes(flags).CopyTo(fat, 0);
        BitConverter.GetBytes((ushort)maxStack).CopyTo(fat, 2);
        BitConverter.GetBytes(code.Length).CopyTo(fat, 4);
        code.CopyTo(fat, 12);
        return fat;
    }
}
