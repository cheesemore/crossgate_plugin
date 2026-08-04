using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 进战形象钩子：OnCommandCharCallback 末尾加载 SeqChapterBattleAppear.dll.bytes 并 Invoke OnBattleCharsReceived。
/// 地图形象：CreateCharacterEntity / CreatePetEntity / GetPlayerCharacterData /
/// EntityManager.OnUpdateObjCallback（UpdateObj 组装 CharacterData 后，覆盖队长本机与回程）。
/// 不占用 Pause / 百科 / 分享；可与九动/抓宠/老板键等并存。
/// </summary>
internal static class BattleAppearExternalIlPatcher
{
    public const string AssetFileName = "SeqChapterBattleAppear.dll.bytes";
    public const string TypeName = "SeqChapterBattleAppear";
    public const string EntryName = "OnBattleCharsReceived";
    public const string WorldEntryName = "TryApplyWorldAppear";
    public const string WorldHookMethodName = "SeqChapter_WorldAppearHook";
    public const string DllAssetPath = "hotfixdata/" + AssetFileName;

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        var restore = false;
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
                case "--restore":
                    restore = true;
                    break;
                case "--detect":
                    detect = true;
                    break;
                case "--dll-only":
                    // 只重编译并部署 SeqChapterBattleAppear.dll.bytes，不改 hotfix
                    break;
            }
        }

        var dllOnly = args.Any(a => a == "--dll-only");

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher battle-appear-external-patch --hotfix <orig> --output <out>\n" +
                "      HotfixPatcher battle-appear-external-patch --hotfix <file> --detect\n" +
                "      HotfixPatcher battle-appear-external-patch --hotfix <file> --dll-only\n" +
                "      HotfixPatcher battle-appear-external-patch --hotfix <orig> --output <out> --restore");
            return 1;
        }

        output ??= source;

        if (detect)
        {
            var patched = IsPatched(source);
            Console.WriteLine(patched ? "battle-appear" : "original");
            return patched ? 0 : 1;
        }

        if (restore)
        {
            File.Copy(source, output, overwrite: true);
            Console.WriteLine("[RESTORE] 已从原版复制: " + output);
            return 0;
        }

        if (dllOnly)
        {
            try
            {
                var dllPath = BuildDll(source);
                var assetOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, AssetFileName);
                File.Copy(dllPath, assetOut, overwrite: true);
                Console.WriteLine("[OK] 已重编译并部署 " + assetOut);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex.Message);
                return 1;
            }
        }

        try
        {
            Apply(source, output);
            Console.WriteLine("[OK] 进战形象钩子补丁完成: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    public static void Apply(string sourcePath, string outputPath)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var dllPath = BuildDll(sourcePath);
        var assetOut = Path.Combine(Path.GetDirectoryName(outputPath)!, AssetFileName);
        File.Copy(dllPath, assetOut, overwrite: true);
        Console.WriteLine("[APPEAR] 已部署 " + assetOut);

        // 同步默认配置到 hotfixdata（不覆盖已有）
        try
        {
            var cfgSrc = Path.Combine(ResolveSourceDir(sourcePath), "..", "battle_appear.json");
            cfgSrc = Path.GetFullPath(cfgSrc);
            var cfgDst = Path.Combine(Path.GetDirectoryName(outputPath)!, "battle_appear.json");
            if (File.Exists(cfgSrc) && !File.Exists(cfgDst))
            {
                File.Copy(cfgSrc, cfgDst, overwrite: false);
                Console.WriteLine("[APPEAR] 已复制 battle_appear.json → hotfixdata");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[APPEAR] 配置复制跳过: " + ex.Message);
        }

        var hotfixDir = Path.GetDirectoryName(sourcePath)!;
        var resolver = new HotfixAssemblyResolver(hotfixDir);
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadWrite = true,
        });

        var battleProcesser = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser")
            ?? throw new InvalidOperationException("未找到 BattleProcesser");
        var onChar = battleProcesser.Methods.FirstOrDefault(m => m.Name == "OnCommandCharCallback" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 OnCommandCharCallback");

        if (IsHookInstalled(onChar))
        {
            Console.WriteLine("[APPEAR] OnCommandCharCallback 钩已存在，仅刷新 DLL");
        }
        else
        {
            InjectHook(onChar, asm.MainModule);
            Console.WriteLine("[APPEAR] OnCommandCharCallback → OnBattleCharsReceived");
        }

        EnsureWorldAppearHooks(asm.MainModule);

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

        var outBytes = File.ReadAllBytes(outputPath);
        var growth = (long)PeLayout.GetSection(outBytes, ".text").VirtualSize
                     - (long)PeLayout.GetSection(origBytes, ".text").VirtualSize;
        Console.WriteLine($"[APPEAR] .text VirtualSize {(growth >= 0 ? "+" : "")}{growth}");
        HotfixSize.EnsureUnchanged(outBytes, expectedSize);
    }

    public static bool IsPatched(string hotfixPath)
    {
        try
        {
            var pe = File.ReadAllBytes(hotfixPath);
            if (!ContainsUtf16Le(pe, TypeName)
                && !ContainsUtf16Le(pe, AssetFileName)
                && !ContainsAscii(pe, TypeName)
                && !ContainsAscii(pe, AssetFileName))
            {
                return false;
            }

            var resolver = new HotfixAssemblyResolver(Path.GetDirectoryName(hotfixPath)!);
            using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
            });
            var onChar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleProcesser")
                ?.Methods.FirstOrDefault(m => m.Name == "OnCommandCharCallback" && m.HasBody);
            var battleHook = onChar != null && IsHookInstalled(onChar);
            var worldHook = asm.MainModule.Types.FirstOrDefault(t => t.Name == "EntityFactory")
                ?.Methods.Any(m => m.Name == WorldHookMethodName) == true;
            return battleHook || worldHook;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 创建人物/宠物 + GetPlayerCharacterData + OnUpdateObjCallback（组装 CharacterData 后）按本地 Uid 档改写。
    /// UpdateObj 覆盖队长本机刷新与回程原地 SetData；钩内 MethodInfo 已缓存，避免旧 set_data 每帧反射崩。
    /// </summary>
    private static void EnsureWorldAppearHooks(ModuleDefinition module)
    {
        var factory = module.Types.FirstOrDefault(t => t.Name == "EntityFactory")
            ?? throw new InvalidOperationException("未找到 EntityFactory");
        var charData = module.Types.FirstOrDefault(t => t.Name == "CharacterData")
            ?? throw new InvalidOperationException("未找到 CharacterData");

        var hook = factory.Methods.FirstOrDefault(m => m.Name == WorldHookMethodName);
        if (hook == null)
        {
            hook = new MethodDefinition(
                WorldHookMethodName,
                MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            hook.Parameters.Add(new ParameterDefinition(
                "data",
                ParameterAttributes.None,
                new ByReferenceType(charData)));
            hook.Parameters.Add(new ParameterDefinition("kind", ParameterAttributes.None, module.TypeSystem.Int32));
            factory.Methods.Add(hook);
            Console.WriteLine("[APPEAR] 已添加 " + WorldHookMethodName);
        }
        else
        {
            Console.WriteLine("[APPEAR] 重建 " + WorldHookMethodName + " 方法体（空id快退+缓存）");
        }

        EnsureWorldAppearCacheFields(module, factory);
        RebuildWorldAppearHookBody(module, factory, charData, hook);

        InjectWorldCall(factory, "CreateCharacterEntity", hook, kind: 0);
        InjectWorldCall(factory, "CreatePetEntity", hook, kind: 1);
        InjectGetPlayerCharacterDataCall(module, hook, charData);
        InjectUpdateObjWorldAppearCall(module, hook, charData);

        // 卸掉旧版 set_data 钩（DisplaySys 仍会按未改的 CharacterData 覆盖；改走 UpdateObj）
        var entity = module.Types.FirstOrDefault(t => t.Name == "CharacterEntity");
        var setData = entity?.Methods.FirstOrDefault(m => m.Name == "set_data" && m.HasBody);
        if (setData != null && IsWorldCallInstalled(setData))
        {
            StripWorldCallPrefix(setData);
            Console.WriteLine("[APPEAR] 已移除 set_data 世界形象钩（改走 UpdateObj）");
        }
    }

    private const string WorldMethodCacheField = "s_SeqWorldAppearMethod";
    private const string WorldResolvedField = "s_SeqWorldAppearResolved";

    private static void EnsureWorldAppearCacheFields(ModuleDefinition module, TypeDefinition factory)
    {
        var corlibRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib")
            ?? module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        var methodInfoType = module.ImportReference(
            module.AssemblyResolver
                .Resolve(corlibRef)
                .MainModule.Types.First(t => t.FullName == "System.Reflection.MethodInfo"));

        if (factory.Fields.All(f => f.Name != WorldMethodCacheField))
        {
            factory.Fields.Add(new FieldDefinition(
                WorldMethodCacheField,
                FieldAttributes.Static | FieldAttributes.Private,
                methodInfoType));
        }

        if (factory.Fields.All(f => f.Name != WorldResolvedField))
        {
            factory.Fields.Add(new FieldDefinition(
                WorldResolvedField,
                FieldAttributes.Static | FieldAttributes.Private,
                module.TypeSystem.Boolean));
        }
    }

    /// <summary>删除方法开头到 WorldAppearHook call（含）的注入前缀。</summary>
    private static void StripWorldCallPrefix(MethodDefinition method)
    {
        var body = method.Body;
        Instruction? callInsn = null;
        foreach (var i in body.Instructions)
        {
            if (i.Operand is MethodReference mr && mr.Name == WorldHookMethodName)
            {
                callInsn = i;
                break;
            }
        }

        if (callInsn == null)
        {
            return;
        }

        var il = body.GetILProcessor();
        var remove = new List<Instruction>();
        foreach (var i in body.Instructions)
        {
            remove.Add(i);
            if (ReferenceEquals(i, callInsn))
            {
                break;
            }
        }

        foreach (var i in remove)
        {
            il.Remove(i);
        }

        IlSerializer.RecalculateOffsets(body);
    }

    /// <summary>
    /// EntityManager.OnUpdateObjCallback(Proto)：V_11 填完拷到 V_9 后立刻套皮，
    /// 再进 SetData / DisplaySys.SetRide|Halo（否则队长本机与回程会被服务端原皮盖掉）。
    /// kind = (charEntityType == Pet=3) ? 1 : 0。
    /// </summary>
    private static void InjectUpdateObjWorldAppearCall(
        ModuleDefinition module,
        MethodDefinition hook,
        TypeDefinition charData)
    {
        var em = module.Types.FirstOrDefault(t => t.Name == "EntityManager")
            ?? throw new InvalidOperationException("未找到 EntityManager");
        var method = em.Methods.FirstOrDefault(m =>
                m.Name == "OnUpdateObjCallback"
                && m.HasBody
                && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.Name == "Proto_SC_UpdateObj")
            ?? throw new InvalidOperationException("未找到 OnUpdateObjCallback(Proto_SC_UpdateObj)");

        if (IsWorldCallInstalled(method))
        {
            Console.WriteLine("[APPEAR] OnUpdateObjCallback 世界形象钩已存在");
            return;
        }

        var fType = charData.Fields.FirstOrDefault(f => f.Name == "charEntityType")
            ?? throw new InvalidOperationException("CharacterData 无 charEntityType");

        var body = method.Body;
        var il = body.GetILProcessor();
        Instruction? insertAfter = null;
        VariableDefinition? targetLocal = null;

        for (var i = 0; i < body.Instructions.Count - 1; i++)
        {
            var cur = body.Instructions[i];
            var next = body.Instructions[i + 1];
            if ((cur.OpCode != OpCodes.Ldloc_S && cur.OpCode != OpCodes.Ldloc)
                || cur.Operand is not VariableDefinition src
                || src.VariableType.Name != "CharacterData")
            {
                continue;
            }

            if ((next.OpCode != OpCodes.Stloc_S && next.OpCode != OpCodes.Stloc)
                || next.Operand is not VariableDefinition dst
                || dst.VariableType.Name != "CharacterData"
                || ReferenceEquals(src, dst))
            {
                continue;
            }

            insertAfter = next;
            targetLocal = dst;
            break;
        }

        if (insertAfter == null || targetLocal == null)
        {
            throw new InvalidOperationException("OnUpdateObjCallback 未找到 CharacterData 赋值点 (ldloc/stloc)");
        }

        var nextInsn = insertAfter.Next
            ?? throw new InvalidOperationException("OnUpdateObjCallback CharacterData 赋值后无后续指令");

        // ldloca data; ldloca data; ldfld charEntityType; ldc.i4.3; ceq; call hook
        il.InsertBefore(nextInsn, il.Create(OpCodes.Ldloca, targetLocal));
        il.InsertBefore(nextInsn, il.Create(OpCodes.Ldloca, targetLocal));
        il.InsertBefore(nextInsn, il.Create(OpCodes.Ldfld, module.ImportReference(fType)));
        il.InsertBefore(nextInsn, il.Create(OpCodes.Ldc_I4_3));
        il.InsertBefore(nextInsn, il.Create(OpCodes.Ceq));
        il.InsertBefore(nextInsn, il.Create(OpCodes.Call, hook));

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)4);
        Console.WriteLine("[APPEAR] OnUpdateObjCallback → " + WorldHookMethodName + "(kind=Pet?1:0)");
    }

    private static void InjectGetPlayerCharacterDataCall(
        ModuleDefinition module,
        MethodDefinition hook,
        TypeDefinition charData)
    {
        var holder = module.Types.FirstOrDefault(t => t.Name == "PlayerDataHolder")
            ?? throw new InvalidOperationException("未找到 PlayerDataHolder");
        var method = holder.Methods.FirstOrDefault(m => m.Name == "GetPlayerCharacterData" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 GetPlayerCharacterData");
        if (IsWorldCallInstalled(method))
        {
            Console.WriteLine("[APPEAR] GetPlayerCharacterData 世界形象钩已存在");
            return;
        }

        // 返回前：对栈上的 CharacterData 本地变量套形象
        var body = method.Body;
        var il = body.GetILProcessor();
        VariableDefinition? cdLocal = null;
        foreach (var v in body.Variables)
        {
            if (v.VariableType.FullName == charData.FullName || v.VariableType.Name == "CharacterData")
            {
                cdLocal = v;
                break;
            }
        }

        if (cdLocal == null)
        {
            throw new InvalidOperationException("GetPlayerCharacterData 无 CharacterData 局部变量");
        }

        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in rets)
        {
            // 栈上已有返回值时：pop → 改 local → ldloc 再 ret
            // 本方法是 ldloc.0; ret，在 ret 前插入即可（改 local 后重载）
            il.InsertBefore(ret, il.Create(OpCodes.Pop));
            il.InsertBefore(ret, il.Create(OpCodes.Ldloca, cdLocal));
            il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(ret, il.Create(OpCodes.Call, hook));
            il.InsertBefore(ret, il.Create(OpCodes.Ldloc, cdLocal));
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)4);
        Console.WriteLine("[APPEAR] GetPlayerCharacterData → " + WorldHookMethodName);
    }

    private static void InjectWorldCall(
        TypeDefinition factory,
        string methodName,
        MethodDefinition hook,
        int kind)
    {
        var target = factory.Methods.FirstOrDefault(m => m.Name == methodName && m.HasBody)
            ?? throw new InvalidOperationException("未找到 " + methodName);
        if (IsWorldCallInstalled(target))
        {
            Console.WriteLine("[APPEAR] " + methodName + " 世界形象钩已存在");
            return;
        }

        var il = target.Body.GetILProcessor();
        var first = target.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarga_S, target.Parameters[0]));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, kind));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        IlSerializer.RecalculateOffsets(target.Body);
        target.Body.MaxStackSize = Math.Max(target.Body.MaxStackSize, (short)4);
        Console.WriteLine("[APPEAR] " + methodName + " → " + WorldHookMethodName + "(kind=" + kind + ")");
    }

    private static bool IsWorldCallInstalled(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.Operand is MethodReference mr && mr.Name == WorldHookMethodName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// void SeqChapter_WorldAppearHook(ref CharacterData data, int kind)
    /// 空 id 立刻返回；MethodInfo 静态缓存；再 Invoke TryApplyWorldAppear。
    /// </summary>
    private static void RebuildWorldAppearHookBody(
        ModuleDefinition module,
        TypeDefinition factory,
        TypeDefinition charData,
        MethodDefinition method)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = true;
        var il = body.GetILProcessor();

        var getTypeStatic = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var loadBytes = BridgeLoaderIlBuilder.ImportFileUtilLoadBytesPublic(module);
        var assemblyLoad = BridgeLoaderIlBuilder.ImportAssemblyLoadPublic(module);
        var getType = BridgeLoaderIlBuilder.ImportAssemblyGetTypePublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);
        var exceptionType = new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary);

        var corlibName = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib")
            ?? module.AssemblyReferences.First(r => r.Name == "System.Runtime");
        var corlib = module.AssemblyResolver.Resolve(corlibName);
        var convertType = corlib.MainModule.Types.First(t => t.FullName == "System.Convert");
        var toInt32Obj = module.ImportReference(convertType.Methods.First(m =>
            m.Name == "ToInt32"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.Object"));
        var toUInt32Obj = module.ImportReference(convertType.Methods.First(m =>
            m.Name == "ToUInt32"
            && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.Object"));
        var strLen = module.ImportReference(
            corlib.MainModule.Types.First(t => t.FullName == "System.String")
                .Properties.First(p => p.Name == "Length").GetMethod);

        var fId = charData.Fields.First(f => f.Name == "id");
        var fAnim = charData.Fields.First(f => f.Name == "animationDataID");
        var fRide = charData.Fields.First(f => f.Name == "rideSkinId");
        var fHole = charData.Fields.First(f => f.Name == "roleHole");
        var fPerfect = charData.Fields.First(f => f.Name == "PerfectPet");
        var fCrest = charData.Fields.First(f => f.Name == "maxCrestUseId");
        var fCachedMethod = factory.Fields.First(f => f.Name == WorldMethodCacheField);
        var fResolved = factory.Fields.First(f => f.Name == WorldResolvedField);

        var typeVar = new VariableDefinition(getTypeStatic.ReturnType);
        var bytesVar = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        var asmVar = new VariableDefinition(assemblyLoad.ReturnType);
        var ioVar = new VariableDefinition(new ArrayType(module.TypeSystem.Object));
        var retVar = new VariableDefinition(module.TypeSystem.Object);
        body.Variables.Add(typeVar);
        body.Variables.Add(bytesVar);
        body.Variables.Add(asmVar);
        body.Variables.Add(ioVar);
        body.Variables.Add(retVar);

        var leaveTarget = il.Create(OpCodes.Nop);
        var afterResolve = il.Create(OpCodes.Nop);
        var doInvoke = il.Create(OpCodes.Nop);
        var needResolve = il.Create(OpCodes.Nop);
        var writeBack = il.Create(OpCodes.Nop);

        // try 内退出只能用 leave，不能 br/brfalse 跳出保护块（否则 HybridCLR 原生崩）
        void LeaveIfFalse()
        {
            var cont = il.Create(OpCodes.Nop);
            il.Append(il.Create(OpCodes.Brtrue, cont));
            il.Append(il.Create(OpCodes.Leave, leaveTarget));
            il.Append(cont);
        }

        void LeaveIfTrue()
        {
            var cont = il.Create(OpCodes.Nop);
            il.Append(il.Create(OpCodes.Brfalse, cont));
            il.Append(il.Create(OpCodes.Leave, leaveTarget));
            il.Append(cont);
        }

        // if (data.id == null || data.id.Length == 0) return;  （try 外，可用 br）
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fId));
        il.Append(il.Create(OpCodes.Brfalse, leaveTarget));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fId));
        il.Append(il.Create(OpCodes.Call, strLen));
        il.Append(il.Create(OpCodes.Brfalse, leaveTarget));

        // try {
        var tryStart = il.Create(OpCodes.Ldsfld, fCachedMethod);
        il.Append(tryStart);
        il.Append(il.Create(OpCodes.Brtrue, doInvoke));
        il.Append(il.Create(OpCodes.Ldsfld, fResolved));
        LeaveIfTrue(); // 已解析失败
        il.Append(il.Create(OpCodes.Br, needResolve));

        il.Append(needResolve);
        il.Append(il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName));
        il.Append(il.Create(OpCodes.Call, getTypeStatic));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Brtrue, afterResolve));

        il.Append(il.Create(OpCodes.Ldstr, DllAssetPath));
        il.Append(il.Create(OpCodes.Call, loadBytes));
        il.Append(il.Create(OpCodes.Stloc, bytesVar));
        il.Append(il.Create(OpCodes.Ldloc, bytesVar));
        LeaveIfFalse();
        il.Append(il.Create(OpCodes.Ldloc, bytesVar));
        il.Append(il.Create(OpCodes.Call, assemblyLoad));
        il.Append(il.Create(OpCodes.Stloc, asmVar));
        il.Append(il.Create(OpCodes.Ldloc, asmVar));
        LeaveIfFalse();
        il.Append(il.Create(OpCodes.Ldloc, asmVar));
        il.Append(il.Create(OpCodes.Ldstr, TypeName));
        il.Append(il.Create(OpCodes.Callvirt, getType));
        il.Append(il.Create(OpCodes.Stloc, typeVar));
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        LeaveIfFalse();

        il.Append(afterResolve);
        il.Append(il.Create(OpCodes.Ldloc, typeVar));
        il.Append(il.Create(OpCodes.Ldstr, WorldEntryName));
        il.Append(il.Create(OpCodes.Callvirt, getMethod));
        il.Append(il.Create(OpCodes.Stsfld, fCachedMethod));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stsfld, fResolved));
        il.Append(il.Create(OpCodes.Ldsfld, fCachedMethod));
        LeaveIfFalse();

        il.Append(doInvoke);
        il.Append(il.Create(OpCodes.Ldc_I4_7));
        il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Stloc, ioVar));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fId));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fAnim));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.UInt32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fRide));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fHole));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fPerfect));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_6));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, fCrest));
        il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
        il.Append(il.Create(OpCodes.Stelem_Ref));

        il.Append(il.Create(OpCodes.Ldsfld, fCachedMethod));
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Dup));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(il.Create(OpCodes.Stloc, retVar));

        il.Append(il.Create(OpCodes.Ldloc, retVar));
        LeaveIfFalse();
        il.Append(il.Create(OpCodes.Ldloc, retVar));
        il.Append(il.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean));
        LeaveIfFalse();

        il.Append(writeBack);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Call, toUInt32Obj));
        il.Append(il.Create(OpCodes.Stfld, fAnim));

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Call, toInt32Obj));
        il.Append(il.Create(OpCodes.Stfld, fRide));

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Call, toInt32Obj));
        il.Append(il.Create(OpCodes.Stfld, fHole));

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_5));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Call, toInt32Obj));
        il.Append(il.Create(OpCodes.Stfld, fPerfect));

        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, ioVar));
        il.Append(il.Create(OpCodes.Ldc_I4_6));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Call, toInt32Obj));
        il.Append(il.Create(OpCodes.Stfld, fCrest));

        il.Append(il.Create(OpCodes.Leave, leaveTarget));

        var catchPop = il.Create(OpCodes.Pop);
        il.Append(catchPop);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stsfld, fResolved));
        il.Append(il.Create(OpCodes.Leave, leaveTarget));
        il.Append(leaveTarget);
        il.Append(il.Create(OpCodes.Ret));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = leaveTarget,
        });

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = 8;
    }

    /// <summary>
    /// 每个 ret 前：GetType→必要时 LoadBytes/Assembly.Load→Invoke OnBattleCharsReceived；try/catch。
    /// </summary>
    private static void InjectHook(MethodDefinition method, ModuleDefinition module)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        var getTypeStatic = BridgeLoaderIlBuilder.ImportTypeGetTypeStaticPublic(module);
        var loadBytes = BridgeLoaderIlBuilder.ImportFileUtilLoadBytesPublic(module);
        var assemblyLoad = BridgeLoaderIlBuilder.ImportAssemblyLoadPublic(module);
        var getType = BridgeLoaderIlBuilder.ImportAssemblyGetTypePublic(module);
        var getMethod = BridgeLoaderIlBuilder.ImportTypeGetMethodPublic(module);
        var invoke = BridgeLoaderIlBuilder.ImportMethodInvokePublic(module);
        var exceptionType = new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary);

        body.InitLocals = true;
        var typeVar = new VariableDefinition(getTypeStatic.ReturnType);
        var bytesVar = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
        body.Variables.Add(typeVar);
        body.Variables.Add(bytesVar);

        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count == 0)
        {
            throw new InvalidOperationException("OnCommandCharCallback 无 ret");
        }

        foreach (var ret in rets)
        {
            var leaveTarget = il.Create(OpCodes.Nop);
            var afterLoad = il.Create(OpCodes.Nop);
            var doInvoke = il.Create(OpCodes.Nop);

            var tryStart = il.Create(OpCodes.Ldstr, TypeName + ", " + TypeName);
            var callGetType = il.Create(OpCodes.Call, getTypeStatic);
            var stType = il.Create(OpCodes.Stloc, typeVar);
            var ldType1 = il.Create(OpCodes.Ldloc, typeVar);
            var brHave = il.Create(OpCodes.Brtrue, afterLoad);

            var ldAsset = il.Create(OpCodes.Ldstr, DllAssetPath);
            var callLoadBytes = il.Create(OpCodes.Call, loadBytes);
            var stBytes = il.Create(OpCodes.Stloc, bytesVar);
            var ldBytes1 = il.Create(OpCodes.Ldloc, bytesVar);
            var brFalseBytes = il.Create(OpCodes.Brfalse, leaveTarget);
            var ldBytes2 = il.Create(OpCodes.Ldloc, bytesVar);
            var callAsmLoad = il.Create(OpCodes.Call, assemblyLoad);
            var dupAsm = il.Create(OpCodes.Dup);
            var brFalseAsm = il.Create(OpCodes.Brfalse, leaveTarget);
            var ldTypeName = il.Create(OpCodes.Ldstr, TypeName);
            var callGetType2 = il.Create(OpCodes.Callvirt, getType);
            var stType2 = il.Create(OpCodes.Stloc, typeVar);
            var ldType2 = il.Create(OpCodes.Ldloc, typeVar);
            var brFalseType = il.Create(OpCodes.Brfalse, leaveTarget);
            var brInvoke = il.Create(OpCodes.Br, doInvoke);

            var ldType3 = il.Create(OpCodes.Ldloc, typeVar);
            var ldEntry = il.Create(OpCodes.Ldstr, EntryName);
            var callGetMethod = il.Create(OpCodes.Callvirt, getMethod);
            var dupMethod = il.Create(OpCodes.Dup);
            var brFalseMethod = il.Create(OpCodes.Brfalse, leaveTarget);
            var ldnull1 = il.Create(OpCodes.Ldnull);
            var ldnull2 = il.Create(OpCodes.Ldnull);
            var callInvoke = il.Create(OpCodes.Callvirt, invoke);
            var popResult = il.Create(OpCodes.Pop);
            var tryLeave = il.Create(OpCodes.Leave, leaveTarget);
            var catchPop = il.Create(OpCodes.Pop);
            var catchLeave = il.Create(OpCodes.Leave, leaveTarget);

            var block = new[]
            {
                tryStart, callGetType, stType, ldType1, brHave,
                ldAsset, callLoadBytes, stBytes, ldBytes1, brFalseBytes,
                ldBytes2, callAsmLoad, dupAsm, brFalseAsm,
                ldTypeName, callGetType2, stType2, ldType2, brFalseType, brInvoke,
                afterLoad, doInvoke,
                ldType3, ldEntry, callGetMethod, dupMethod, brFalseMethod,
                ldnull1, ldnull2, callInvoke, popResult, tryLeave,
                catchPop, catchLeave, leaveTarget,
            };

            foreach (var insn in block)
            {
                il.InsertBefore(ret, insn);
            }

            // afterLoad 落点应跳过 load；修正：have type 时跳到 doInvoke
            // 上面 afterLoad 与 doInvoke 相邻，Brtrue→afterLoad 即可
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = exceptionType,
                TryStart = tryStart,
                TryEnd = catchPop,
                HandlerStart = catchPop,
                HandlerEnd = leaveTarget,
            });
        }

        IlSerializer.RecalculateOffsets(body);
        body.MaxStackSize = Math.Max(body.MaxStackSize, (short)8);
        Console.WriteLine("[APPEAR] 已注入 OnBattleCharsReceived + catch");
    }

    private static bool IsHookInstalled(MethodDefinition method)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Ldstr && insn.Operand is string s
                && (s == TypeName || s == TypeName + ", " + TypeName || s == EntryName
                    || s == DllAssetPath || s.IndexOf("BattleAppear", StringComparison.Ordinal) >= 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUtf16Le(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return IndexOfBytes(pe, System.Text.Encoding.Unicode.GetBytes(text)) >= 0;
    }

    private static bool ContainsAscii(byte[] pe, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return IndexOfBytes(pe, System.Text.Encoding.ASCII.GetBytes(text)) >= 0;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static string BuildDll(string hotfixPath)
    {
        var srcDir = ResolveSourceDir(hotfixPath);
        var csPath = Path.Combine(srcDir, "SeqChapterBattleAppear.cs");
        if (!File.Exists(csPath))
        {
            throw new FileNotFoundException("找不到 SeqChapterBattleAppear.cs", csPath);
        }

        var hotfixDataDir = Path.GetDirectoryName(hotfixPath)!;
        var outDir = Path.Combine(Path.GetTempPath(), "seqchapter_appear_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var dllPath = Path.Combine(outDir, "SeqChapterBattleAppear.dll");

        var refs = new List<MetadataReference>();
        foreach (var name in new[] { "mscorlib.dll.bytes", "system.dll.bytes", "system.core.dll.bytes" })
        {
            var path = Path.Combine(hotfixDataDir, name);
            if (File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        if (refs.Count == 0)
        {
            throw new InvalidOperationException("未找到 hotfixdata 内 mscorlib/system，无法编译形象钩子 DLL");
        }

        var syntax = CSharpSyntaxTree.ParseText(
            File.ReadAllText(csPath),
            path: csPath,
            encoding: System.Text.Encoding.UTF8);
        var compile = CSharpCompilation.Create(
            "SeqChapterBattleAppear",
            new[] { syntax },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compile.Emit(ms);
        if (!result.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
            throw new InvalidOperationException("Roslyn 编译 SeqChapterBattleAppear 失败:\n" + errors);
        }

        File.WriteAllBytes(dllPath, ms.ToArray());
        Console.WriteLine($"[APPEAR] 已编译形象钩子 DLL（{refs.Count} 个引用）");
        return dllPath;
    }

    private static string ResolveSourceDir(string hotfixPath)
    {
        var hotfixDir = Path.GetDirectoryName(hotfixPath)!;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
            ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(exeDir, "seqchapter_battle_appear")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "seqchapter_battle_appear")),
            Path.GetFullPath(Path.Combine(exeDir, "..", "tools", "seqchapter_battle_appear")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "seqchapter_battle_appear")),
            Path.GetFullPath(Path.Combine(hotfixDir, "..", "..", "..", "tools", "seqchapter_battle_appear")),
        };

        for (var dir = hotfixDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)!)
        {
            var probe = Path.Combine(dir, "tools", "seqchapter_battle_appear");
            if (!candidates.Contains(probe, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(probe);
            }
        }

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SeqChapterBattleAppear.cs")))
            {
                return dir;
            }
        }

        throw new DirectoryNotFoundException("找不到 tools/seqchapter_battle_appear 目录");
    }
}
