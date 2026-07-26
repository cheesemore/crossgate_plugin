using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 侧栏百科按钮：OnClickWiki 把按钮下 TMP/UI 文字改为「百科1」，并 Tip 提示。
/// （原 Title 依赖子节点名 "Text"，百科按钮常对不上导致无效果。）
/// </summary>
internal static class WikiLabelIlPatcher
{
    public const string NewLabel = "百科1";

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var detect = false;

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
                case "--detect":
                    detect = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher wiki-label-patch --hotfix <hotfix> [--output <out>]\n" +
                "      HotfixPatcher wiki-label-patch --hotfix <hotfix> --detect");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "wiki-label" : "original");
            return patched ? 0 : 1;
        }

        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 百科按钮文字→百科1 补丁已写入: " + output);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已包含"))
        {
            Console.WriteLine("[SKIP] " + ex.Message);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            return IsWikiLabelPatched(asm);
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        if (IsWikiLabelPatched(asm))
        {
            throw new InvalidOperationException("百科按钮文字→百科1 补丁已包含");
        }

        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel");
        var onClickWiki = mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickWiki");
        var btnWiki = mapSidebar.Fields.FirstOrDefault(f => f.Name == "m_Btn_Wiki")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.m_Btn_Wiki");

        var getInChildrenTmp = FindGenericGetComponentInChildren(asm.MainModule, "TextMeshProUGUI")
            ?? throw new InvalidOperationException("未找到 GetComponentInChildren<TextMeshProUGUI>");
        var setText = FindMemberRef(asm.MainModule, "set_text", "TMP_Text")
            ?? throw new InvalidOperationException("未找到 TMP_Text.set_text");
        var tip = FindNotifyTip(asm.MainModule)
            ?? throw new InvalidOperationException("未找到 NotifyManager.Tip");
        var getNotify = FindManagerInstanceGetter(asm.MainModule, "NotifyManager")
            ?? throw new InvalidOperationException("未找到 Manager<NotifyManager>.get_Instance");

        BuildSetLabelBody(onClickWiki, btnWiki, getInChildrenTmp, setText, getNotify, tip);
        Console.WriteLine("[PATCH] OnClickWiki -> GetComponentInChildren<TMP>.text=\"百科1\" + Tip");

        using var ms = new MemoryStream();
        asm.Write(ms);
        var written = ms.ToArray();
        if (written.Length > expectedSize)
        {
            throw new InvalidOperationException(
                $"Cecil 写出 {written.Length} 字节，超过 hotfix 固定体积 {expectedSize}");
        }

        var padded = PeExactSizePad.Pad(written, origBytes, expectedSize);
        MetadataValidator.EnsureReadable(padded, hotfixDir);
        File.WriteAllBytes(outputPath, padded);
        HotfixSize.EnsureUnchanged(File.ReadAllBytes(outputPath), expectedSize);
    }

    private static void BuildSetLabelBody(
        MethodDefinition method,
        FieldDefinition btnWiki,
        MethodReference getInChildrenTmp,
        MethodReference setText,
        MethodReference getNotify,
        MethodReference tip)
    {
        method.Body.Instructions.Clear();
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.InitLocals = false;

        var il = method.Body.GetILProcessor();
        var afterText = il.Create(OpCodes.Nop);
        var ret = il.Create(OpCodes.Ret);

        // CustomButton btn = this.m_Btn_Wiki;
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, btnWiki));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, afterText));

        // TextMeshProUGUI tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
        il.Append(il.Create(OpCodes.Callvirt, getInChildrenTmp));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, afterText));

        // tmp.text = "百科1";
        il.Append(il.Create(OpCodes.Ldstr, NewLabel));
        il.Append(il.Create(OpCodes.Callvirt, setText));
        il.Append(il.Create(OpCodes.Br, afterText));

        // afterText: 若上面 brfalse 带了多余引用则 pop
        // 两条 brfalse 路径：一条栈上是 btn(已 dup 后 call 前失败? wait)

        // Fix control flow carefully:
        // Actually on brfalse from btn null: stack has nothing extra if we don't dup wrong.
        // Redesign:

        method.Body.Instructions.Clear();
        il = method.Body.GetILProcessor();
        var tipBlock = il.Create(OpCodes.Nop);
        ret = il.Create(OpCodes.Ret);

        // btn = this.m_Btn_Wiki
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, btnWiki));
        il.Append(il.Create(OpCodes.Dup));
        var popAndTip = il.Create(OpCodes.Pop);
        il.Append(il.Create(OpCodes.Brfalse, popAndTip));

        // tmp = btn.GetComponentInChildren<TMPUGUI>()
        il.Append(il.Create(OpCodes.Callvirt, getInChildrenTmp));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Brfalse, popAndTip));

        // tmp.text = label
        il.Append(il.Create(OpCodes.Ldstr, NewLabel));
        il.Append(il.Create(OpCodes.Callvirt, setText));
        il.Append(il.Create(OpCodes.Br, tipBlock));

        // popAndTip: pop leftover null/ref
        il.Append(popAndTip);
        il.Append(tipBlock);

        // NotifyManager.Instance.Tip("百科1", false)
        il.Append(il.Create(OpCodes.Call, getNotify));
        il.Append(il.Create(OpCodes.Ldstr, NewLabel));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Callvirt, tip));
        il.Append(ret);

        IlSerializer.RecalculateOffsets(method.Body);
        method.Body.MaxStackSize = 8;
    }

    private static bool IsWikiLabelPatched(AssemblyDefinition asm)
    {
        var wiki = asm.MainModule.Types
            .FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?.Methods.FirstOrDefault(m => m.Name == "OnClickWiki" && m.HasBody);
        if (wiki == null)
        {
            return false;
        }

        var sawLabel = false;
        var sawGetInChildren = false;
        foreach (var ins in wiki.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s == NewLabel)
            {
                sawLabel = true;
            }

            if (ins.Operand is MethodReference mr
                && mr.Name.StartsWith("GetComponentInChildren", StringComparison.Ordinal)
                && mr.ToString().Contains("TextMeshProUGUI", StringComparison.Ordinal))
            {
                sawGetInChildren = true;
            }
        }

        return sawLabel && sawGetInChildren;
    }

    private static MethodReference? FindGenericGetComponentInChildren(ModuleDefinition module, string typeArgName)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.Operand is not GenericInstanceMethod gim)
                {
                    continue;
                }

                if (gim.ElementMethod.Name != "GetComponentInChildren")
                {
                    continue;
                }

                if (gim.GenericArguments.Count == 1
                    && gim.GenericArguments[0].Name == typeArgName)
                {
                    return module.ImportReference(gim);
                }
            }
        }

        return null;
    }

    private static MethodReference? FindNotifyTip(ModuleDefinition module)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.Operand is not MethodReference mr)
                {
                    continue;
                }

                if (mr.DeclaringType?.Name == "NotifyManager"
                    && mr.Name == "Tip"
                    && mr.Parameters.Count == 2)
                {
                    return module.ImportReference(mr);
                }
            }
        }

        return null;
    }

    private static MethodReference? FindManagerInstanceGetter(ModuleDefinition module, string managerName)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode != OpCodes.Call || insn.Operand is not MethodReference mr)
                {
                    continue;
                }

                if (mr.Name == "get_Instance"
                    && mr.DeclaringType.Name.StartsWith("Manager`1", StringComparison.Ordinal)
                    && mr.DeclaringType.FullName.Contains(managerName, StringComparison.Ordinal))
                {
                    return module.ImportReference(mr);
                }
            }
        }

        return null;
    }

    private static MethodReference? FindMemberRef(ModuleDefinition module, string methodName, string typeName)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var insn in method.Body.Instructions)
            {
                if (insn.OpCode != OpCodes.Call && insn.OpCode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (insn.Operand is not MethodReference mr)
                {
                    continue;
                }

                if (mr.Name == methodName && mr.DeclaringType?.Name == typeName)
                {
                    return module.ImportReference(mr);
                }
            }
        }

        foreach (var mr in module.GetMemberReferences().OfType<MethodReference>())
        {
            if (mr.Name == methodName && mr.DeclaringType?.Name == typeName)
            {
                return module.ImportReference(mr);
            }
        }

        return null;
    }
}
