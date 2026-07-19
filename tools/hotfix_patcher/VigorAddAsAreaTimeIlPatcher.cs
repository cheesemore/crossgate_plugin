using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>卡时加号（m_Btn_VigorAdd）改为发送工时兑换请求，与 OnClickAreaTimeAdd 相同。</summary>
internal static class VigorAddAsAreaTimeIlPatcher
{
    private const int ExpectedSize = 6_355_968;
    private const int AreaTimeCurrency = 21; // PROTO_CURRENCY_AREA_TIME

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
            Console.WriteLine("用法: HotfixPatcher vigor-add-area-time-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 卡时加号改工时请求补丁已写入: " + output);
        return 0;
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

        var currencyType = asm.MainModule.Types.First(t => t.Name == "Com_Currency");
        var vigorHandler = FindVigorAddHandler(currencyType);
        if (IsAlreadyPatched(vigorHandler))
        {
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, outputPath, overwrite: true);
            }

            Console.WriteLine("[SKIP] 卡时加号已是工时兑换请求");
            return;
        }

        var areaTimeAdd = currencyType.Methods.First(m => m.Name == "OnClickAreaTimeAdd" && m.HasBody);
        var ldstrSnapshot = ReadMethodBodyFromPe(origBytes, areaTimeAdd.RVA);
        var handlerSnapshot = ReadMethodBodyFromPe(origBytes, vigorHandler.RVA);

        var module = asm.MainModule;
        var uidField = currencyType.Fields.First(f => f.Name == "m_Uid");
        ExtractCallsFromAreaTimeAdd(areaTimeAdd, out var getRoleMgrRef, out var sendExchangeRef);
        var getRoleMgr = module.ImportReference(getRoleMgrRef);
        var sendExchange = module.ImportReference(sendExchangeRef);

        var body = vigorHandler.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;

        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Call, getRoleMgr));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, uidField));
        il.Append(il.Create(OpCodes.Ldstr, "获取信息"));
        il.Append(il.Create(OpCodes.Ldc_I4_S, (sbyte)AreaTimeCurrency));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Callvirt, sendExchange));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        var newBody = IlSerializer.Serialize(body, ldstrSnapshot);
        BinaryPeWriter.ReplaceMethodBody(data, vigorHandler.RVA, handlerSnapshot, newBody);

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[PATCH] {vigorHandler.Name} -> SendCurrencyExchange(工时获取信息)");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static MethodDefinition FindVigorAddHandler(TypeDefinition currencyType)
    {
        foreach (var method in currencyType.Methods.Where(m => m.HasBody && m.IsPrivate))
        {
            foreach (var ins in method.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Callvirt
                    && ins.Operand is MethodReference called
                    && called.Name == "SendOpenVigor")
                {
                    return method;
                }
            }
        }

        throw new InvalidOperationException("未找到 Com_Currency 内调用 SendOpenVigor 的卡时加号回调");
    }

    private static bool IsAlreadyPatched(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == "SendCurrencyExchange")
            {
                return true;
            }
        }

        return false;
    }

    private static void ExtractCallsFromAreaTimeAdd(
        MethodDefinition areaTimeAdd,
        out MethodReference getRoleManagerInstance,
        out MethodReference sendCurrencyExchange)
    {
        MethodReference? getInstance = null;
        MethodReference? sendExchange = null;
        foreach (var ins in areaTimeAdd.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call
                && ins.Operand is MethodReference call
                && call.Name == "get_Instance")
            {
                getInstance = call;
            }

            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference virt
                && virt.Name == "SendCurrencyExchange")
            {
                sendExchange = virt;
            }
        }

        if (getInstance == null || sendExchange == null)
        {
            throw new InvalidOperationException("OnClickAreaTimeAdd 中未找到 RoleManager.Instance / SendCurrencyExchange");
        }

        getRoleManagerInstance = getInstance;
        sendCurrencyExchange = sendExchange;
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
