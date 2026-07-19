using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 工时加号 OnClickAreaTimeAdd：GoToSource(商店) → SendCurrencyExchange(获取信息)。
/// 二进制替换方法体，保持 hotfix 体积不变，可叠在桥接/VIP 等补丁之后。
/// </summary>
internal static class AreaTimeAddIlPatcher
{
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
            Console.WriteLine("用法: HotfixPatcher area-time-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 工时加号补丁已写入: " + output);
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

        var currencyType = asm.MainModule.Types.First(t => t.Name == "Com_Currency");
        var onClick = currencyType.Methods.First(m => m.Name == "OnClickAreaTimeAdd" && m.HasBody);
        if (IsExchangeHandlerPatched(onClick))
        {
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, outputPath, overwrite: true);
            }

            Console.WriteLine("[SKIP] 工时加号已是兑换协议");
            return;
        }

        var snapshot = ReadMethodBodyFromPe(origBytes, onClick.RVA);
        var refMethod = asm.MainModule.Types.First(t => t.Name == "CollectionPanel")
            .Methods.First(m => m.Name == "OnClickAddAreaTime" && m.HasBody);
        var refSnapshot = ReadMethodBodyFromPe(origBytes, refMethod.RVA);
        var uidField = currencyType.Fields.First(f => f.Name == "m_Uid");
        ExtractExchangeCalls(asm, out var getRoleMgrRef, out var sendExchangeRef);

        var module = asm.MainModule;
        var getRoleMgr = module.ImportReference(getRoleMgrRef);
        var sendExchange = module.ImportReference(sendExchangeRef);

        var body = onClick.Body;
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
        var newBody = IlSerializer.Serialize(body, refSnapshot);
        BinaryPeWriter.ReplaceMethodBody(data, onClick.RVA, snapshot, newBody);

        if (data.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({expectedSize} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine("[PATCH] OnClickAreaTimeAdd -> SendCurrencyExchange(获取信息)");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static bool IsExchangeHandlerPatched(MethodDefinition method)
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

    private static void ExtractExchangeCalls(
        AssemblyDefinition asm,
        out MethodReference getRoleManagerInstance,
        out MethodReference sendCurrencyExchange)
    {
        MethodReference? getInstance = null;
        MethodReference? sendExchange = null;

        foreach (var type in asm.MainModule.Types)
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Call
                        && ins.Operand is MethodReference call
                        && call.Name == "get_Instance"
                        && call.DeclaringType is GenericInstanceType git
                        && git.GenericArguments.Count == 1
                        && git.GenericArguments[0].Name == "RoleManager")
                    {
                        getInstance = call;
                    }

                    if (ins.OpCode == OpCodes.Callvirt
                        && ins.Operand is MethodReference virt
                        && virt.Name == "SendCurrencyExchange"
                        && virt.Parameters.Count == 4
                        && virt.DeclaringType.Name == "RoleManager")
                    {
                        sendExchange = virt;
                    }
                }
            }
        }

        if (getInstance == null || sendExchange == null)
        {
            throw new InvalidOperationException("未找到 RoleManager.Instance / SendCurrencyExchange 引用");
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
