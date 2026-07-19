using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>侧栏客服按钮（MapSidebarPanel.OnClickCustom）改开 GM / 盲盒 / 水晶阁 / 讨伐 Boss / 无尽之塔 / 露比试炼等。</summary>
internal static class CustomerBtnGmIlPatcher
{
    public enum CustomerGmMode
    {
        Original,
        Gm1,
        Gm2,
        Gm3,
        Gm4,
        Gm5,
        Blindbox,
        Lottery,
        Crystal,
        Boss,
        Tower,
        Ruby,
        AutoSkill,
        ChallengeBoss,
        BraveTrial,
        FamilyHall,
        PetReform,
    }

    private static readonly (CustomerGmMode Mode, string PanelName, string Label)[] GmPanelCandidates =
    {
        (CustomerGmMode.Gm1, "GMToolsPanel", "GM 命令工具"),
        (CustomerGmMode.Gm2, "GMStorePanel", "GM 道具商店"),
        (CustomerGmMode.Gm3, "GMPetStorePanel", "GM 宠物商店"),
        (CustomerGmMode.Gm4, "GMPetEffectPanel", "GM 宠物特效"),
        (CustomerGmMode.Gm5, "GMAnimationSettingPanel", "GM 动画设置"),
    };

    public static int Run(string[] args)
    {
        string? source = null;
        string? output = null;
        string? orig = null;
        var mode = (CustomerGmMode?)null;
        var sniffOnly = false;
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
                case "--sniff":
                    sniffOnly = true;
                    break;
                case "--sniff-dojo":
                    Console.WriteLine("[WARN] --sniff-dojo 已移除（百人道场依赖服务端，不再提供客服入口）");
                    sniffOnly = true;
                    break;
                case "--detect":
                    detectOnly = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine(
                "用法: HotfixPatcher customer-gm-patch --hotfix <hotfix> [--output <out>] [--mode original|gm1|...|ruby] [--orig <orig>]\n" +
                "      HotfixPatcher customer-gm-patch --hotfix <hotfix> --sniff\n" +
                "      HotfixPatcher customer-gm-patch --hotfix <hotfix> --detect");
            return 1;
        }

        if (sniffOnly)
        {
            return RunSniff(source);
        }

        if (detectOnly)
        {
            Console.WriteLine(DetectMode(source));
            return 0;
        }

        if (mode == null)
        {
            Console.WriteLine("[FAIL] 缺少 --mode original|gm1|...|blindbox|lottery|crystal|boss|tower|ruby|autoskill");
            return 1;
        }

        output ??= source;
        Apply(source, output, mode.Value, orig);
        Console.WriteLine($"[OK] 客服按钮补丁 ({ModeLabel(mode.Value)}) 已写入: {output}");
        return 0;
    }

    public static int RunSniff(string hotfixPath)
    {
        var peBytes = File.ReadAllBytes(hotfixPath);
        var resolver = CreateResolver();
        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var foundGm = SniffGmPanels(asm);
        Console.WriteLine("[SNIFF] GM 面板类型:");
        foreach (var panel in foundGm)
        {
            Console.WriteLine($"  [FOUND] {panel}");
        }

        foreach (var name in new[]
                 {
                     "BlindboxDrawPanel", "LotteryPanel", "LuckCrystalPanel", "BOSSChallengePanel",
                     "ChallengeBossPanel", "BraveTrialPanel",
                     "RubyTrialPanel", "AutoSkillSettingPanel",
                     "PetMainPanel", "PetSetChildPanel", "Com_PetReform",
                 })
        {
            if (asm.MainModule.Types.Any(t => t.Name == name))
            {
                Console.WriteLine($"  [FOUND] {name}");
            }
        }

        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel");
        if (mapSidebar == null)
        {
            Console.WriteLine("[FAIL] 未找到 MapSidebarPanel");
            return 1;
        }

        var callback = FindCustomerCallback(asm);
        var hasQq = HasTryOpenQqSupport(peBytes, asm, callback);
        Console.WriteLine($"[SNIFF] 客服入口: MapSidebarPanel.OnClickCustom (TryOpenQQUrl={hasQq})");
        Console.WriteLine($"[SNIFF] 侧栏回调: OnClickBlindboxDrawCallback={HasSidebarMethod(mapSidebar, "OnClickBlindboxDrawCallback")}");
        Console.WriteLine($"[SNIFF] 侧栏回调: OnClcikLotterryCallback={HasSidebarMethod(mapSidebar, "OnClcikLotterryCallback")}");
        Console.WriteLine($"[SNIFF] 侧栏回调: OnClickLuckCrystalCallback={HasSidebarMethod(mapSidebar, "OnClickLuckCrystalCallback")}");
        Console.WriteLine($"[SNIFF] 侧栏回调: OnClickBossCallback={HasSidebarMethod(mapSidebar, "OnClickBossCallback")}");
        Console.WriteLine($"[SNIFF] 侧栏回调: OnClickTrialCallback={HasSidebarMethod(mapSidebar, "OnClickTrialCallback")}");
        Console.WriteLine($"[SNIFF] 无尽之塔 Tab 补丁目标: {BossChallengeTowerTabIlPatcher.SniffTargets(hotfixPath)}");
        var hasFamilyHall = asm.MainModule.Types.Any(t => t.Name == "FamilyHallChildPanel")
            && asm.MainModule.Types.Any(t => t.Name == "FamilyManager");
        Console.WriteLine($"[SNIFF] 公会领地传送: FamilyHallChildPanel.OnClickGoFamilyCallback={hasFamilyHall}");
        var hasPetReform = asm.MainModule.Types.Any(t => t.Name == "PetManager")
            && asm.MainModule.Types.Any(t => t.Name == "Com_PetReform")
            && asm.MainModule.Types.Any(t => t.Name == "PetSetChildPanel");
        Console.WriteLine($"[SNIFF] 宠物改造: OpenPetMain(RESET)+Com_PetReform={hasPetReform}");
        Console.WriteLine(
            "[OK] 可打补丁，推荐 --mode gm1 / blindbox / lottery / challengeboss / bravetrial / tower / familyhall / petreform");
        return 0;
    }

    public static void Apply(string sourcePath, string outputPath, CustomerGmMode mode, string? origPath = null)
    {
        var origBytes = File.ReadAllBytes(sourcePath);
        var expectedSize = HotfixSize.Require(origBytes);

        var data = (byte[])origBytes.Clone();
        var resolver = CreateResolver();
        using var asm = AssemblyDefinition.ReadAssembly(sourcePath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var callback = FindCustomerCallback(asm);
        var snapshot = ReadMethodBodyFromPe(origBytes, callback.RVA);

        switch (mode)
        {
            case CustomerGmMode.Original:
                if (string.IsNullOrWhiteSpace(origPath))
                {
                    throw new InvalidOperationException("还原原版客服需要 --orig 指向 hotfix.dll.bytes.orig");
                }

                RestoreOriginalBody(data, asm, origPath, callback);
                break;
            case CustomerGmMode.Blindbox:
                EnsureSidebarDelegateTarget(asm, "OnClickBlindboxDrawCallback");
                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildInlinedSidebarBody(origBytes, asm, "OnClickBlindboxDrawCallback"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> SendBlindboxDraw(获取数据) [3028]");
                break;
            case CustomerGmMode.Lottery:
                if (!asm.MainModule.Types.Any(t => t.Name == "LotteryPanel"))
                {
                    throw new InvalidOperationException("未找到 LotteryPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildOpenPanelBody(callback, asm, snapshot, "LotteryPanel"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> LotteryPanel.Open() [3049 幸运秘宝]");
                break;
            case CustomerGmMode.Crystal:
                if (!asm.MainModule.Types.Any(t => t.Name == "LuckCrystalPanel"))
                {
                    throw new InvalidOperationException("未找到 LuckCrystalPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildOpenPanelBody(callback, asm, snapshot, "LuckCrystalPanel"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> LuckCrystalPanel.Open()");
                break;
            case CustomerGmMode.Boss:
                if (!asm.MainModule.Types.Any(t => t.Name == "BOSSChallengePanel"))
                {
                    throw new InvalidOperationException("未找到 BOSSChallengePanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildOpenPanelBody(callback, asm, snapshot, "BOSSChallengePanel"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> BOSSChallengePanel.Open()（讨伐 Boss，无 Tab 补丁）");
                break;
            case CustomerGmMode.Tower:
                if (!asm.MainModule.Types.Any(t => t.Name == "BOSSChallengePanel"))
                {
                    throw new InvalidOperationException("未找到 BOSSChallengePanel");
                }

                ReplaceCallbackBody(data, callback, snapshot, BuildOpenPanelBody(callback, asm, snapshot, "BOSSChallengePanel"));
                if (data.Length != origBytes.Length)
                {
                    throw new InvalidOperationException(
                        $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
                }

                File.WriteAllBytes(outputPath, data);
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> BOSSChallengePanel.Open()");
                BossChallengeTowerTabIlPatcher.Apply(outputPath, outputPath);
                break;
            case CustomerGmMode.Ruby:
                if (!asm.MainModule.Types.Any(t => t.Name == "RubyTrialPanel"))
                {
                    throw new InvalidOperationException("未找到 RubyTrialPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    RubyTrialOpenIlBuilder.BuildOpenBody(callback, asm, snapshot));
                RubyTrialOpenIlBuilder.PatchSelfTeamAsClaim(asm, origBytes, data);
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> RubyTrialPanel.Open(空同步)");
                break;
            case CustomerGmMode.AutoSkill:
                if (!asm.MainModule.Types.Any(t => t.Name == "AutoSkillSettingPanel"))
                {
                    throw new InvalidOperationException("未找到 AutoSkillSettingPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    AutoSkillCustomerIlBuilder.BuildOpenBody(asm));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> OpenAutoSkillSettingPanel(SelectPlayerUid)");
                break;
            case CustomerGmMode.ChallengeBoss:
                if (!asm.MainModule.Types.Any(t => t.Name == "ChallengeBossPanel"))
                {
                    throw new InvalidOperationException("未找到 ChallengeBossPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildOpenPanelBody(callback, asm, snapshot, "ChallengeBossPanel"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> ChallengeBossPanel.Open() [3045 讨伐令]");
                break;
            case CustomerGmMode.BraveTrial:
                if (!asm.MainModule.Types.Any(t => t.Name == "BraveTrialPanel"))
                {
                    throw new InvalidOperationException("未找到 BraveTrialPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    BuildOpenPanelBody(callback, asm, snapshot, "BraveTrialPanel"));
                Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom -> BraveTrialPanel.Open() [3047 英雄试炼]");
                break;
            case CustomerGmMode.FamilyHall:
                if (!asm.MainModule.Types.Any(t => t.Name == "FamilyManager")
                    || !asm.MainModule.Types.Any(t => t.Name == "FamilyHallChildPanel"))
                {
                    throw new InvalidOperationException("未找到 FamilyManager / FamilyHallChildPanel");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    FamilyHallTeleportCustomerIlBuilder.BuildOpenBody(data, asm));
                Console.WriteLine(
                    "[PATCH] MapSidebarPanel.OnClickCustom -> SendFamily(\"NPC传送\", 0, \"1\") [公会领地]");
                break;
            case CustomerGmMode.PetReform:
                if (!asm.MainModule.Types.Any(t => t.Name == "PetManager")
                    || !asm.MainModule.Types.Any(t => t.Name == "Com_PetReform")
                    || !asm.MainModule.Types.Any(t => t.Name == "PetSetChildPanel"))
                {
                    throw new InvalidOperationException("未找到 PetManager / PetSetChildPanel / Com_PetReform");
                }

                ReplaceCallbackBody(
                    data,
                    callback,
                    snapshot,
                    PetReformCustomerIlBuilder.BuildOpenBody(asm));
                Console.WriteLine(
                    "[PATCH] MapSidebarPanel.OnClickCustom -> OpenPetMain(SelectPlayerUid, -1, 3, -1) [宠物改造页]");
                break;
            default:
                var panelName = PanelNameForMode(mode);
                if (!SniffGmPanels(asm).Contains(panelName))
                {
                    throw new InvalidOperationException($"未找到 GM 面板类型: {panelName}");
                }

                ReplaceCallbackBody(data, callback, snapshot, BuildOpenPanelBody(callback, asm, snapshot, panelName));
                Console.WriteLine($"[PATCH] MapSidebarPanel.OnClickCustom -> {panelName}");
                break;
        }

        if (mode != CustomerGmMode.Tower)
        {
            if (data.Length != origBytes.Length)
            {
                throw new InvalidOperationException(
                    $"二进制补丁改变了文件大小 ({origBytes.Length} -> {data.Length})，已中止");
            }

            File.WriteAllBytes(outputPath, data);
            Console.WriteLine($"[OK] 文件大小不变: {data.Length} 字节");
        }
        else
        {
            Console.WriteLine($"[OK] 文件大小不变: {expectedSize} 字节");
        }
    }

    public static string DetectMode(string hotfixPath)
    {
        var peBytes = File.ReadAllBytes(hotfixPath);
        var resolver = CreateResolver();
        using var asm = AssemblyDefinition.ReadAssembly(hotfixPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var callback = FindCustomerCallback(asm);
        if (HasTryOpenQqSupport(peBytes, asm, callback))
        {
            return "original";
        }

        if (RubyTrialOpenIlBuilder.IsCustomerOpen(peBytes, asm))
        {
            return "ruby";
        }

        if (AutoSkillCustomerIlBuilder.IsCustomerOpen(peBytes, asm, callback))
        {
            return "autoskill";
        }

        if (FamilyHallTeleportCustomerIlBuilder.IsCustomerOpen(peBytes, asm, callback))
        {
            return "familyhall";
        }

        if (PetReformCustomerIlBuilder.IsCustomerOpen(peBytes, asm, callback))
        {
            return "petreform";
        }

        foreach (var ins in callback.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference virt
                && virt.Name == "SendBlindboxDraw")
            {
                return "blindbox";
            }

            if (ins.OpCode != OpCodes.Call)
            {
                continue;
            }

            if (ins.Operand is MethodReference mr)
            {
                switch (mr.Name)
                {
                    case "OnClickBlindboxDrawCallback":
                        return "blindbox";
                    case "OnClcikLotterryCallback":
                        return "lottery";
                    case "OnClickLuckCrystalCallback":
                        return "crystal";
                    case "OnClickBossCallback":
                        return "boss";
                    case "OnClickTrialCallback":
                        return "bravetrial";
                }
            }

            if (ins.Operand is GenericInstanceMethod gim)
            {
                var detected = DetectModeFromCall(hotfixPath, gim);
                if (detected != "unknown")
                {
                    return detected;
                }
            }
        }

        return "unknown";
    }

    private static string DetectModeFromCall(string hotfixPath, GenericInstanceMethod gim)
    {
        if (gim.Name != "GetUIPanel" || gim.GenericArguments.Count == 0)
        {
            return "unknown";
        }

        return gim.GenericArguments[0].Name switch
        {
            "GMToolsPanel" => "gm1",
            "GMStorePanel" => "gm2",
            "GMPetStorePanel" => "gm3",
            "GMPetEffectPanel" => "gm4",
            "GMAnimationSettingPanel" => "gm5",
            "BOSSChallengePanel" => BossChallengeTowerTabIlPatcher.IsTowerTabPatched(hotfixPath) ? "tower" : "boss",
            "LuckCrystalPanel" => "crystal",
            "LotteryPanel" => "lottery",
            "ChallengeBossPanel" => "challengeboss",
            "BraveTrialPanel" => "bravetrial",
            _ => "unknown",
        };
    }

    private static List<string> SniffGmPanels(AssemblyDefinition asm)
    {
        var typeNames = asm.MainModule.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return GmPanelCandidates
            .Where(c => typeNames.Contains(c.PanelName))
            .Select(c => c.PanelName)
            .ToList();
    }

    private static bool HasSidebarMethod(TypeDefinition mapSidebar, string methodName)
        => mapSidebar.Methods.Any(m => m.Name == methodName && m.HasBody);

    private static void EnsureSidebarDelegateTarget(AssemblyDefinition asm, string methodName)
    {
        var mapSidebar = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel");
        if (!HasSidebarMethod(mapSidebar, methodName))
        {
            throw new InvalidOperationException($"未找到 MapSidebarPanel.{methodName}");
        }
    }

    private static MethodDefinition FindCustomerCallback(AssemblyDefinition asm)
    {
        var mapSidebar = asm.MainModule.Types.FirstOrDefault(t => t.Name == "MapSidebarPanel")
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel");

        return mapSidebar.Methods.FirstOrDefault(m => m.Name == "OnClickCustom" && m.HasBody)
            ?? throw new InvalidOperationException("未找到 MapSidebarPanel.OnClickCustom");
    }

    private static bool HasTryOpenQqSupport(byte[] peBytes, AssemblyDefinition asm, MethodDefinition callback)
    {
        var tryOpen = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel")
            .Methods.First(m => m.Name == "TryOpenQQUrl" && m.IsStatic);
        var snapshot = ReadMethodBodyFromPe(peBytes, callback.RVA);
        return ContainsCallToken(snapshot, tryOpen.MetadataToken.ToUInt32());
    }

    private static bool ContainsCallToken(byte[] methodBody, uint methodToken)
    {
        var token = BitConverter.GetBytes(methodToken);
        var codeOffset = GetMethodCodeOffset(methodBody);
        var codeSize = GetMethodCodeSize(methodBody);
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

    private static int GetMethodCodeOffset(byte[] methodBody)
    {
        var flags = methodBody[0];
        return (flags & 0x3) switch
        {
            0x2 => 1,
            0x3 => 12,
            _ => throw new InvalidOperationException($"未知 method header 0x{flags:X2}"),
        };
    }

    private static int GetMethodCodeSize(byte[] methodBody)
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

    private static void ReplaceCallbackBody(
        byte[] data,
        MethodDefinition callback,
        byte[] snapshot,
        byte[] newBody)
    {
        BinaryPeWriter.ReplaceMethodBody(data, callback.RVA, snapshot, newBody);
    }

    private static byte[] BuildOpenPanelBody(
        MethodDefinition callback,
        AssemblyDefinition asm,
        byte[] snapshot,
        string panelTypeName)
    {
        var getUIPanel = FindGetUIPanel(asm, panelTypeName);
        var open = FindUIPanelOpen(asm);
        return CompactIlBody.BuildCallCallvirtRet(getUIPanel, open);
    }

    private static byte[] BuildInlinedSidebarBody(byte[] origBytes, AssemblyDefinition asm, string targetMethodName)
    {
        var mapSidebar = asm.MainModule.Types.First(t => t.Name == "MapSidebarPanel");
        var target = mapSidebar.Methods.First(m => m.Name == targetMethodName && m.HasBody);
        var snapshot = ReadMethodBodyFromPe(origBytes, target.RVA);
        return CompactIlBody.BuildTinyFromExistingMethodBody(snapshot);
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
        var chatMini = asm.MainModule.Types.FirstOrDefault(t => t.Name == "Com_ChatMini");
        if (chatMini != null)
        {
            var onClickGm = chatMini.Methods.FirstOrDefault(m => m.Name == "OnClickGm" && m.HasBody);
            if (onClickGm != null)
            {
                foreach (var ins in onClickGm.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Callvirt
                        && ins.Operand is MethodReference mr
                        && mr.Name == "Open"
                        && mr.DeclaringType.Name == "UIPanel")
                    {
                        return mr;
                    }
                }
            }
        }

        var offline = asm.MainModule.Types
            .First(t => t.Name == "MapSidebarPanel")
            .Methods.First(m => m.Name == "OnClickOfflineTradeCallback" && m.HasBody);
        foreach (var ins in offline.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt
                && ins.Operand is MethodReference mr
                && mr.Name == "Open"
                && mr.DeclaringType.Name == "UIPanel")
            {
                return mr;
            }
        }

        throw new InvalidOperationException("未找到 UIPanel.Open");
    }

    private static void RestoreOriginalBody(
        byte[] data,
        AssemblyDefinition currentAsm,
        string origPath,
        MethodDefinition currentCallback)
    {
        if (!File.Exists(origPath))
        {
            throw new InvalidOperationException($"还原原版需要 --orig: {origPath}");
        }

        var origBytes = File.ReadAllBytes(origPath);
        HotfixSize.Require(origBytes, "原版");
        var resolver = CreateResolver();
        using var origAsm = AssemblyDefinition.ReadAssembly(origPath, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        var origCallback = FindCustomerCallback(origAsm);
        var origSnapshot = ReadMethodBodyFromPe(origBytes, origCallback.RVA);
        var currentRva = currentCallback.RVA;

        if (currentRva != origCallback.RVA)
        {
            BinaryPeWriter.PatchMethodRva(data, currentRva, origCallback.RVA);
        }

        BinaryPeWriter.ReplaceMethodBody(data, origCallback.RVA, origSnapshot, origSnapshot);
        Console.WriteLine("[PATCH] MapSidebarPanel.OnClickCustom 已还原为原版（QQ 客服）");
    }

    private static DefaultAssemblyResolver CreateResolver()
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        return resolver;
    }

    private static CustomerGmMode ParseMode(string raw) => raw.ToLowerInvariant() switch
    {
        "original" or "offline" => CustomerGmMode.Original,
        "gm1" => CustomerGmMode.Gm1,
        "gm2" => CustomerGmMode.Gm2,
        "gm3" => CustomerGmMode.Gm3,
        "gm4" => CustomerGmMode.Gm4,
        "gm5" => CustomerGmMode.Gm5,
        "blindbox" or "blind" or "盲盒" => CustomerGmMode.Blindbox,
        "lottery" or "lotterry" or "幸运秘宝" or "3049" => CustomerGmMode.Lottery,
        "crystal" or "luckcrystal" or "水晶" or "水晶阁" => CustomerGmMode.Crystal,
        "boss" or "challenge" or "讨伐" or "讨伐boss" => CustomerGmMode.Boss,
        "tower" or "endless" or "无尽之塔" or "无尽" => CustomerGmMode.Tower,
        "ruby" or "loopy" or "露比" or "露比试炼" => CustomerGmMode.Ruby,
        "autoskill" or "auto-skill" or "自动技能" or "自动技能设置" => CustomerGmMode.AutoSkill,
        "challengeboss" or "suppress" or "讨伐令" or "3045" => CustomerGmMode.ChallengeBoss,
        "bravetrial" or "hero_trials" or "英雄试炼" or "3047" => CustomerGmMode.BraveTrial,
        "familyhall" or "family" or "guild" or "公会" or "公会领地" or "传送公会" => CustomerGmMode.FamilyHall,
        "petreform" or "reform" or "宠物改造" or "改造" or "pet-reform" => CustomerGmMode.PetReform,
        "diglett" or "earthmouse" or "地鼠" or "地鼠抽奖" or "3046" =>
            throw new InvalidOperationException("地鼠抽奖客服入口已移除"),
        "bossland" or "crystal_sw" or "boss大陆" or "水晶副本" or "3050" =>
            throw new InvalidOperationException("BOSS大陆客服入口已移除，请改用 tower（无尽之塔）"),
        "collection" or "collect" or "采集" or "采集面板" =>
            throw new InvalidOperationException("采集客服入口已移除，请改用 autoskill / blindbox / tower 等"),
        "dojo" or "hundred" or "百人" or "百人道场" =>
            throw new InvalidOperationException("百人道场依赖服务端数据，已从客服入口移除，请改用 boss / tower / ruby 等"),
        _ => throw new InvalidOperationException($"无效 --mode: {raw}"),
    };

    private static string PanelNameForMode(CustomerGmMode mode)
    {
        var match = GmPanelCandidates.FirstOrDefault(c => c.Mode == mode);
        if (match.PanelName == null)
        {
            throw new InvalidOperationException($"未知模式: {mode}");
        }

        return match.PanelName;
    }

    private static string ModeLabel(CustomerGmMode mode) => mode switch
    {
        CustomerGmMode.Original => "原版 QQ 客服",
        CustomerGmMode.Gm1 => "GM 命令工具",
        CustomerGmMode.Gm2 => "GM 道具商店",
        CustomerGmMode.Gm3 => "GM 宠物商店",
        CustomerGmMode.Gm4 => "GM 宠物特效",
        CustomerGmMode.Gm5 => "GM 动画设置",
        CustomerGmMode.Blindbox => "盲盒(BlindboxDraw/3028)",
        CustomerGmMode.Lottery => "幸运秘宝(LotteryPanel/3049)",
        CustomerGmMode.Crystal => "水晶阁(LuckCrystal)",
        CustomerGmMode.Boss => "讨伐Boss(BOSSChallengePanel)",
        CustomerGmMode.Tower => "无尽之塔(BOSS挑战 Tab1)",
        CustomerGmMode.Ruby => "露比试炼(RubyTrialPanel)",
        CustomerGmMode.AutoSkill => "自动技能(AutoSkillSettingPanel)",
        CustomerGmMode.ChallengeBoss => "讨伐令(ChallengeBossPanel/3045)",
        CustomerGmMode.BraveTrial => "英雄试炼(BraveTrialPanel/3047)",
        CustomerGmMode.FamilyHall => "传送公会领地(SendFamily NPC传送 1)",
        CustomerGmMode.PetReform => "宠物改造(OpenPetMain RESET/Com_PetReform)",
        _ => mode.ToString(),
    };

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
