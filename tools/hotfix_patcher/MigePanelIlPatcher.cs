using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>秘阁按钮（m_Btn_OfflineTrade）目标面板：原版秘阁 / 讨伐 Boss / 露比试炼。</summary>
internal static class MigePanelIlPatcher
{
    public enum MigePanelMode
    {
        Offline,
        Boss,
        Ruby,
        Blindbox,
        Lottery,
        Crystal,
        Adventurer,
        PetExchange,
        Gm1,
        Gm2,
        Gm3,
        Gm4,
        Gm5,
    }

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        string? orig = null;
        var mode = (MigePanelMode?)null;
        var detectOnly = false;

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
                case "--orig" when i + 1 < args.Length:
                    orig = Path.GetFullPath(args[++i]);
                    break;
                case "--mode" when i + 1 < args.Length:
                    mode = ParseMode(args[++i]);
                    break;
                case "--detect":
                    detectOnly = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher mige-panel-patch --hotfix <hotfix> --output <out> --mode offline|boss|ruby|blindbox|lottery|crystal|adventurer|pet_exchange|gm1|gm2|gm3|gm4|gm5 [--orig <orig>]\n" +
                "      HotfixPatcher mige-panel-patch --hotfix <hotfix> --detect");
            return 1;
        }

        if (detectOnly)
        {
            Console.WriteLine(DetectMode(source));
            return 0;
        }

        if (mode == null)
        {
            Console.WriteLine("[FAIL] 缺少 --mode offline|boss|ruby|blindbox|lottery|crystal|adventurer|pet_exchange|gm1|gm2|gm3|gm4|gm5");
            return 1;
        }

        output ??= source;
        Apply(source, output, mode.Value, orig);
        Console.WriteLine($"[OK] 秘阁按钮补丁 ({ModeLabel(mode.Value)}) 已写入: {output}");
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath, MigePanelMode mode, string? origPath = null)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        HotfixSize.Require(origBytes);
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

        var callback = RequireOfflineTradeCallback(asm);
        var snapshot = ReadMethodBodyFromPe(origBytes, callback.RVA);
        byte[] newBody;

        switch (mode)
        {
            case MigePanelMode.Offline:
                if (string.IsNullOrWhiteSpace(origPath))
                {
                    throw new InvalidOperationException("还原原版秘阁需要 --orig 指向 hotfix.dll.bytes.orig");
                }

                RestoreOfflineBody(data, asm, origPath, callback);
                newBody = Array.Empty<byte>();
                break;
            case MigePanelMode.Boss:
                newBody = BuildBossBody(callback, asm, snapshot);
                break;
            case MigePanelMode.Ruby:
                newBody = RubyTrialOpenIlBuilder.BuildOpenBody(callback, asm, snapshot);
                RubyTrialOpenIlBuilder.PatchSelfTeamAsClaim(asm, origBytes, data);
                break;
            case MigePanelMode.Blindbox:
                newBody = BuildDelegateSidebarBody(callback, asm, snapshot, "OnClickBlindboxDrawCallback");
                break;
            case MigePanelMode.Lottery:
                if (!asm.MainModule.Types.Any(t => t.Name == "LotteryPanel"))
                {
                    throw new InvalidOperationException("未找到 LotteryPanel");
                }

                newBody = BuildOpenPanelBody(callback, asm, snapshot, "LotteryPanel");
                break;
            case MigePanelMode.Crystal:
                newBody = BuildCrystalBody(callback, asm, snapshot);
                break;
            case MigePanelMode.Adventurer:
                newBody = BuildDelegateSidebarBody(callback, asm, snapshot, "OnClickAventurerCallback");
                break;
            case MigePanelMode.PetExchange:
                newBody = BuildDelegateSidebarBody(callback, asm, snapshot, "OnClickExchangeNewCallback");
                break;
            case MigePanelMode.Gm1:
                newBody = BuildOpenPanelBody(callback, asm, snapshot, "GMToolsPanel");
                break;
            case MigePanelMode.Gm2:
                newBody = BuildOpenPanelBody(callback, asm, snapshot, "GMStorePanel");
                break;
            case MigePanelMode.Gm3:
                newBody = BuildOpenPanelBody(callback, asm, snapshot, "GMPetStorePanel");
                break;
            case MigePanelMode.Gm4:
                newBody = BuildOpenPanelBody(callback, asm, snapshot, "GMPetEffectPanel");
                break;
            case MigePanelMode.Gm5:
                newBody = BuildOpenPanelBody(callback, asm, snapshot, "GMAnimationSettingPanel");
                break;
            default:
                throw new InvalidOperationException($"未知模式: {mode}");
        }

        if (mode != MigePanelMode.Offline)
        {
            BinaryPeWriter.ReplaceMethodBody(data, callback.RVA, snapshot, newBody);
        }

        if (data.Length != origBytes.Length)
        {
            throw new InvalidOperationException(
                $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"[PATCH] MapSidebarPanel.OnClickOfflineTradeCallback -> {ModeLabel(mode)}");
        Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
    }

    public static string DetectMode(string hotfixPath)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var callback = RequireOfflineTradeCallback(asm);
        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call
                && ins.Operand is MethodReference mr
                && mr.Name == "OnClickBlindboxDrawCallback")
            {
                return "blindbox";
            }
        }

        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "SendBlindboxDraw")
            {
                return "blindbox";
            }
        }

        if (RubyTrialOpenIlBuilder.ContainsRubyOpen(callback))
        {
            return "ruby";
        }

        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || ins.Operand is not GenericInstanceMethod gim)
            {
                continue;
            }

            if (gim.Name != "GetUIPanel" || gim.GenericArguments.Count == 0)
            {
                continue;
            }

            return gim.GenericArguments[0].Name switch
            {
                "OfflineTradeMainPanel" => "offline",
                "ChallengeBossPanel" => "boss",
                "RubyTrialPanel" => "ruby",
                "LotteryPanel" => "lottery",
                "LuckCrystalPanel" => "crystal",
                "PetExchangePanel" => "pet_exchange",
                "PetNewExchangePanel" => "pet_exchange",
                "GMToolsPanel" => "gm1",
                "GMStorePanel" => "gm2",
                "GMPetStorePanel" => "gm3",
                "GMPetEffectPanel" => "gm4",
                "GMAnimationSettingPanel" => "gm5",
                _ => "unknown",
            };
        }

        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call
                && ins.Operand is MethodReference mr
                && mr.Name == "OnClickExchangeNewCallback")
            {
                return "pet_exchange";
            }
        }

        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call
                && ins.Operand is MethodReference mr
                && mr.Name == "OnClickLuckCrystalCallback")
            {
                return "crystal";
            }
        }

        return "unknown";
    }

    private static void RestoreOfflineBody(
        byte[] data,
        AssemblyDefinition currentAsm,
        string origPath,
        MethodDefinition currentCallback)
    {
        if (!File.Exists(origPath))
        {
            throw new InvalidOperationException($"还原原版需要 --orig 指向 .orig 备份: {origPath}");
        }

        var origBytes = File.ReadAllBytes(origPath);
        HotfixSize.Require(origBytes, "原版");

        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var origAsm = AssemblyDefinition.ReadAssembly(origPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var origCallback = RequireOfflineTradeCallback(origAsm);
        var origRva = origCallback.RVA;
        var origSnapshot = ReadMethodBodyFromPe(origBytes, origRva);
        var currentRva = currentCallback.RVA;

        if (currentRva != origRva)
        {
            BinaryPeWriter.PatchMethodRva(data, currentRva, origRva);
            Console.WriteLine($"[META] 秘阁回调 RVA 0x{currentRva:X} -> 0x{origRva:X}");
        }

        var currentSnapshot = ReadMethodBodyFromPe(data, origRva);
        BinaryPeWriter.ReplaceMethodBody(data, origRva, currentSnapshot, origSnapshot);
        Console.WriteLine("[PATCH] MapSidebarPanel.OnClickOfflineTradeCallback 已从 .orig 还原");
    }

    private static byte[] BuildBossBody(MethodDefinition callback, AssemblyDefinition asm, byte[] snapshot)
    {
        var module = callback.Module;
        var body = callback.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        var getUIPanel = module.ImportReference(FindGetUIPanel(asm, "ChallengeBossPanel"));
        var open = module.ImportReference(FindUIPanelOpen(asm));

        il.Append(il.Create(OpCodes.Call, getUIPanel));
        il.Append(il.Create(OpCodes.Callvirt, open));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        return IlSerializer.Serialize(body, snapshot);
    }

    private static byte[] BuildOpenPanelBody(
        MethodDefinition callback,
        AssemblyDefinition asm,
        byte[] snapshot,
        string panelTypeName)
    {
        var module = callback.Module;
        var body = callback.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        var getUIPanel = module.ImportReference(FindGetUIPanel(asm, panelTypeName));
        var open = module.ImportReference(FindUIPanelOpen(asm));

        il.Append(il.Create(OpCodes.Call, getUIPanel));
        il.Append(il.Create(OpCodes.Callvirt, open));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        return IlSerializer.Serialize(body, snapshot);
    }

    private static byte[] BuildCrystalBody(MethodDefinition callback, AssemblyDefinition asm, byte[] snapshot)
    {
        return BuildOpenPanelBody(callback, asm, snapshot, "LuckCrystalPanel");
    }

    private static byte[] BuildPetExchangeBody(MethodDefinition callback, AssemblyDefinition asm, byte[] snapshot)
    {
        var module = callback.Module;
        var body = callback.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        var getUIPanel = module.ImportReference(FindGetUIPanel(asm, "PetExchangePanel"));
        var open = module.ImportReference(FindUIPanelOpen(asm));

        il.Append(il.Create(OpCodes.Call, getUIPanel));
        il.Append(il.Create(OpCodes.Callvirt, open));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        return IlSerializer.Serialize(body, snapshot);
    }

    private static byte[] BuildDelegateSidebarBody(
        MethodDefinition callback,
        AssemblyDefinition asm,
        byte[] snapshot,
        string targetMethodName)
    {
        var mapSidebar = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel");
        var target = mapSidebar.Methods.First(m => m.Name == targetMethodName && m.HasBody);
        var module = callback.Module;
        var body = callback.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();

        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(target)));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 8;

        IlSerializer.RecalculateOffsets(body);
        return IlSerializer.Serialize(body, snapshot);
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

    private static MethodReference FindUIPanelOpen(AssemblyDefinition asm)
    {
        var callback = RequireOfflineTradeCallback(asm);
        foreach (var ins in callback.Module.Types
                     .SelectMany(t => t.Methods)
                     .Where(m => m.Name == "OnClickOfflineTradeCallback" && m.HasBody)
                     .SelectMany(m => m.Body.Instructions))
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "Open"
                && mr.DeclaringType.Name == "UIPanel")
            {
                return mr;
            }
        }

        foreach (var ins in asm.MainModule.Types
                     .First(t => t.Name == "BossManager")
                     .Methods.First(m => m.Name == "InitSuppressData")
                     .Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "Open")
            {
                return mr;
            }
        }

        throw new InvalidOperationException("未找到 UIPanel.Open");
    }

    private static MigePanelMode ParseMode(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "offline" or "orig" or "restore" or "秘阁" or "原版" => MigePanelMode.Offline,
            "boss" or "challenge" or "讨伐" or "讨伐boss" => MigePanelMode.Boss,
            "ruby" or "loopy" or "露比" or "露比试炼" => MigePanelMode.Ruby,
            "blindbox" or "blind" or "盲盒" => MigePanelMode.Blindbox,
            "lottery" or "lotterry" or "幸运秘宝" or "3049" => MigePanelMode.Lottery,
            "crystal" or "luckcrystal" or "水晶格" or "水晶" => MigePanelMode.Crystal,
            "adventurer" or "adventure" or "冒险家" or "战令" => MigePanelMode.Adventurer,
            "pet_exchange" or "petexchange" or "宠物兑换" => MigePanelMode.PetExchange,
            "gm1" or "gm" or "gm_tools" or "gm命令" => MigePanelMode.Gm1,
            "gm2" or "gm_store" or "gm商店" => MigePanelMode.Gm2,
            "gm3" or "gm_pet_store" or "gm宠物商店" => MigePanelMode.Gm3,
            "gm4" or "gm_pet_effect" or "gm宠物特效" => MigePanelMode.Gm4,
            "gm5" or "gm_animation" or "gm动画" => MigePanelMode.Gm5,
            _ => throw new InvalidOperationException($"未知 --mode: {raw}（可用 offline|boss|ruby|blindbox|lottery|crystal|adventurer|pet_exchange|gm1|gm2|gm3|gm4|gm5）"),
        };
    }

    private static string ModeLabel(MigePanelMode mode) => mode switch
    {
        MigePanelMode.Offline => "原版秘阁(OfflineTradeMainPanel)",
        MigePanelMode.Boss => "讨伐Boss(ChallengeBossPanel)",
        MigePanelMode.Ruby => "露比试炼(无奖励)",
        MigePanelMode.Blindbox => "盲盒(BlindboxDrawPanel)",
        MigePanelMode.Lottery => "幸运秘宝(LotteryPanel/3049)",
        MigePanelMode.Crystal => "水晶格(LuckCrystalPanel)",
        MigePanelMode.PetExchange => "宠物兑换(PetNewExchangePanel)",
        MigePanelMode.Gm1 => "GM1命令工具(GMToolsPanel)",
        MigePanelMode.Gm2 => "GM2道具商店(GMStorePanel)",
        MigePanelMode.Gm3 => "GM3宠物商店(GMPetStorePanel)",
        MigePanelMode.Gm4 => "GM4宠物特效(GMPetEffectPanel)",
        MigePanelMode.Gm5 => "GM5动画设置(GMAnimationSettingPanel)",
        _ => mode.ToString(),
    };

    private static MethodDefinition RequireOfflineTradeCallback(AssemblyDefinition asm)
    {
        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel");

        return mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickOfflineTradeCallback" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickOfflineTradeCallback");
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
