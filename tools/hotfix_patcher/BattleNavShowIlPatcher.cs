using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>显示地图侧栏「挂机/练级导航」按钮（m_Btn_BattleNavigation）及顶栏按钮行。</summary>
internal static class BattleNavShowIlPatcher
{
    private const int ExpectedSize = 6_355_968;

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
            Console.WriteLine("用法: HotfixPatcher battle-nav-show-patch --hotfix <hotfix> --output <out>");
            return 1;
        }

        output ??= source;
        Apply(source, output);
        Console.WriteLine("[OK] 挂机导航按钮显示补丁已写入: " + output);
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

        var mapSidebar = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel");
        var setAdvanced = mapSidebar.Methods.First(m => m.Name == "SetAdvancedVersion" && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(origBytes, setAdvanced.RVA);

        if (HasShowPatch(setAdvanced))
        {
            Console.WriteLine("[SKIP] MapSidebarPanel.SetAdvancedVersion 已包含显示补丁");
        }
        else
        {
            if (setAdvanced.Body.Instructions.Count != 1
                || setAdvanced.Body.Instructions[0].OpCode != OpCodes.Ret)
            {
                throw new InvalidOperationException("SetAdvancedVersion 应为空方法（仅 ret）");
            }

            var battleNavField = mapSidebar.Fields.First(f => f.Name == "m_Btn_BattleNavigation");
            var topRowField = mapSidebar.Fields.First(f => f.Name == "m_Tran_Top");
            var topObjField = mapSidebar.Fields.First(f => f.Name == "m_Obj_Top");
            var refMethod = mapSidebar.Methods.First(m => m.Name == "RefreshFirstChargeTime" && m.HasBody);
            var getGameObject = FindCallvirt(refMethod, "get_gameObject");
            var setActive = FindGameObjectSetActive(refMethod);

            var il = setAdvanced.Body.GetILProcessor();
            var module = asm.MainModule;
            var ret = setAdvanced.Body.Instructions[0];
            il.Remove(ret);

            void EmitSetActiveTrue(FieldDefinition field)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, field));
                il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(getGameObject)));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(setActive)));
            }

            void EmitSetActiveGameObject(FieldDefinition field)
            {
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, field));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(setActive)));
            }

            EmitSetActiveTrue(battleNavField);
            EmitSetActiveTrue(topRowField);
            EmitSetActiveGameObject(topObjField);
            il.Append(il.Create(OpCodes.Ret));

            IlSerializer.RecalculateOffsets(setAdvanced.Body);
            setAdvanced.Body.MaxStackSize = 8;
            Console.WriteLine("[PATCH] MapSidebarPanel.SetAdvancedVersion -> BattleNavigation + TopRow SetActive(true)");
        }

        var newBody = IlSerializer.Serialize(setAdvanced.Body, snapshot);
        BinaryPeWriter.ReplaceMethodBody(data, setAdvanced.RVA, snapshot, newBody);

        if (data.Length != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    private static bool HasShowPatch(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldfld
                && ins.Operand is FieldReference field
                && field.Name == "m_Btn_BattleNavigation")
            {
                return true;
            }
        }

        return false;
    }

    private static MethodReference FindCallvirt(MethodDefinition method, string name)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == name)
            {
                return called;
            }
        }

        throw new InvalidOperationException($"未找到 callvirt {name}");
    }

    private static MethodReference FindGameObjectSetActive(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference called
                && called.Name == "SetActive"
                && called.Parameters.Count == 1
                && called.DeclaringType.Name == "GameObject")
            {
                return called;
            }
        }

        throw new InvalidOperationException("未找到 GameObject.SetActive 样例");
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
