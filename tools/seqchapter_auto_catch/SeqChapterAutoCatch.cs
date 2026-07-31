using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

/// <summary>
/// 自动抓宠 DLL。
#if AUTO_CATCH_NOPET
/// 无宠人防御分支：部署为 hotfixdata/SeqChapterAutoCatchNoPet.dll.bytes
/// 一级时 1动：P1 扔卡、P2 一号技能、其余人物 G（与普通抓宠相同）；
/// 无宠时人物有 2动：2动一律人物防御 G（替代原宠防御）。有宠仍走宠防御。
#else
/// 部署为 hotfixdata/SeqChapterAutoCatch.dll.bytes
/// 场上有可抓一级敌宠时（LevelOneFlag 或 Level==1；排除迷你蝙蝠 101242；含哥布林 101800）：
///   P1(队长/队序0) 扔封印卡（无卡则走原自动）；
///   P2(队序1) 放 1 号技能（Config[0] 的 Skillindex/Techindex，即位置编号非 SkillId）；
///   其余人物防御 G；所有宠物防御（PetSkills 中 SkillId=74 的栏位，W|slot|petIndex）。
#endif
/// Pause 延迟加载后 Bootstrap；钩 AutoFight_PlayerAction / AutoFight_PlayerAction2 / AutoFight_PetAction。
/// 侧栏百科 = 手动开关：默认 PipelineEnabled=false；点百科切换开/关，并用 NotifyManager.Tip 提示。
/// 无「可抓一级」则走原自动。
/// 退战后（仅队长，且 PipelineEnabled）：
///   1) 需停挂机时立刻发「停止挂机」；
///   2) 扫背包：仅 1 级未正确标记 → #档位，单项随机≥6 才加 @N（满档 #满 / #满@N）；
///   3) 满 5 宠 → 存仓→终检；未满无卡已停挂机；已停挂机则不做存仓。
/// 其余发包间隔 1 秒。与烧封印 / 桥接 / 九动 DLL 互斥。
/// </summary>
#if AUTO_CATCH_NOPET
public static class SeqChapterAutoCatchNoPet
#else
public static class SeqChapterAutoCatch
#endif
{
#if AUTO_CATCH_NOPET
    public const string AssetPath = "hotfixdata/SeqChapterAutoCatchNoPet.dll.bytes";
    public const string TypeName = "SeqChapterAutoCatchNoPet";
#else
    public const string AssetPath = "hotfixdata/SeqChapterAutoCatch.dll.bytes";
    public const string TypeName = "SeqChapterAutoCatch";
#endif

    /// <summary>
    /// 流水线总开关。false 时：战斗内抓宠、退战改名/存仓/挂机、以及后续任何新环节均不触发。
    /// 默认关闭；点侧栏百科切换开/关。
    /// </summary>
    public static volatile bool PipelineEnabled = false;

    private const int SealFlagMask = 0x100;
    /// <summary>
    /// 宠物防御：在 PetSkills 里找 SkillId=74 的栏位发 W|{slot}|{petIndex}。
    /// 注意：不可固定 slot=0——0 号常是攻击，会导致「防御」实为出手。
    /// </summary>
    private const int PetDefendSkillId = 74;
    /// <summary>迷你蝙蝠形象 ID：一级也不抓。</summary>
    private const int MiniBatAnimationId = 101242;
    /// <summary>CHAR_PET_BATTLE.REST — 休息。</summary>
    private const int PetStatusRest = 0;
    /// <summary>存仓目标等级（只存 1 级）。</summary>
    private const int StorePetLevel = 1;
    /// <summary>退战流水线相邻发包间隔。</summary>
    private const int ProtocolGapMs = 1000;
    /// <summary>随机档单项 ≥ 此值才在名字里加 @N；否则仅 #档位 / #满。</summary>
    private const int MinRandomSuffix = 6;

    private static bool _bootstrapped;
    private static bool _exitHooked;
    private static Action _onExitBattle;
    private static int _exitPipelineRunning;
    private static bool _exitNeedProtocolGap;

    /// <summary>开启抓宠后标题「★自动中★遇到1级N只」中的遇一级计数；关闭时清零。</summary>
    private static int _levelOneMeetCount;
    /// <summary>本场战斗是否已计过一次一级（避免每回合重复 +1）。</summary>
    private static bool _countedLevelOneThisBattle;

    /// <summary>总开关门闩：后续新环节也应先调此方法。</summary>
    public static bool IsPipelineActive()
    {
        if (PipelineEnabled)
        {
            return true;
        }

        // 若曾被 LoadFrom / Load 成多份程序集，同步其它副本上的开关
        return ReadPipelineEnabledFromAnyCopy();
    }

    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        TryHookExitBattle();
    }

    /// <summary>MapSidebarPanel.OnClickWiki：切换流水线；返回是否开启（由 hotfix IL 用原版 Tip 提示）。</summary>
    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetPipelineEnabledAllCopies(enable);
        _levelOneMeetCount = 0;
        _countedLevelOneThisBattle = false;
        RefreshWindowTitle();
        return enable;
    }

    /// <summary>本场首次发现可抓一级时 +1，并刷新标题 ★自动中★遇到1级N只。</summary>
    private static void NoteLevelOneEncounterOnce()
    {
        if (!IsPipelineActive() || _countedLevelOneThisBattle)
        {
            return;
        }

        _countedLevelOneThisBattle = true;
        _levelOneMeetCount++;
        RefreshWindowTitle();
    }

    /// <summary>
    /// 与游戏一致：{产品名} {服务器} {角色} Lv.{等级}；
    /// 抓宠开启时追加「 ★自动中★遇到1级{次数}只」。
    /// </summary>
    private static void RefreshWindowTitle()
    {
        try
        {
            var product = GetUnityProductName();
            if (string.IsNullOrEmpty(product))
            {
                return;
            }

            var server = "";
            var serverInfo = GetStaticMember("PlayerDataHolder", "currentServerInfo");
            if (serverInfo != null)
            {
                server = Convert.ToString(GetMember(serverInfo, "name") ?? "") ?? "";
            }

            var player = GetStaticMember("PlayerDataHolder", "playerData");
            var roleName = "";
            var level = 0;
            if (player != null)
            {
                roleName = Convert.ToString(GetMember(player, "name") ?? "") ?? "";
                level = Convert.ToInt32(GetMember(player, "level") ?? 0);
            }

            var title = string.IsNullOrEmpty(roleName)
                ? product
                : string.Format("{0} {1} {2} Lv.{3}", product, server, roleName, level);

            if (IsPipelineActive())
            {
                title = title + " ★自动中★遇到1级" + _levelOneMeetCount + "只";
            }

            var appMgr = FindType("AppManager");
            var setTitle = appMgr?.GetMethod(
                "SetWindowTitle",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            // HybridCLR：参数 Type 可能对不上，按名字兜底
            if (setTitle == null && appMgr != null)
            {
                foreach (var m in appMgr.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (m.Name != "SetWindowTitle")
                    {
                        continue;
                    }

                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.FullName == "System.String")
                    {
                        setTitle = m;
                        break;
                    }
                }
            }

            setTitle?.Invoke(null, new object[] { title });
        }
        catch
        {
            // ignore
        }
    }

    private static string GetUnityProductName()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType("UnityEngine.Application", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null)
                {
                    continue;
                }

                var p = t.GetProperty(
                    "productName",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (p != null)
                {
                    return Convert.ToString(p.GetValue(null, null) ?? "") ?? "";
                }
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }

    private static void SetPipelineEnabledAllCopies(bool enabled)
    {
        PipelineEnabled = enabled;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType(TypeName, false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(
#if AUTO_CATCH_NOPET
                    SeqChapterAutoCatchNoPet
#else
                    SeqChapterAutoCatch
#endif
                    ))
                {
                    continue;
                }

                var f = t.GetField(
                    "PipelineEnabled",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool))
                {
                    f.SetValue(null, enabled);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool ReadPipelineEnabledFromAnyCopy()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType(TypeName, false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(
#if AUTO_CATCH_NOPET
                    SeqChapterAutoCatchNoPet
#else
                    SeqChapterAutoCatch
#endif
                    ))
                {
                    continue;
                }

                var f = t.GetField(
                    "PipelineEnabled",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool) && Convert.ToBoolean(f.GetValue(null)))
                {
                    PipelineEnabled = true;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>人物自动行动：有一级则按 P1/P2/其他分工；否则 false 走原逻辑。</summary>
    public static bool TryPlayerAutoCatch()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            if (!TryFindLevelOneEnemy(out var targetIndex))
            {
                return false;
            }

            NoteLevelOneEncounterOnce();

            var uid = GetStaticString("BattleDataHolder", "CurrentAccount");
            if (string.IsNullOrEmpty(uid))
            {
                return false;
            }

            var battleMgr = GetManagerInstance("BattleManager");
            if (battleMgr == null || !IsPlayerAlive(battleMgr))
            {
                return false;
            }

#if AUTO_CATCH_NOPET
            // 无宠时人物 2动：一律防御（FightProcessFlag.PlayerActionEnd 表示 1动已结束）
            if (IsPlayerSecondAction(battleMgr))
            {
                return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
            }
#endif

            var partySlot = GetPartySlot(uid);
            if (partySlot == 0)
            {
                if (!TryFindSealCard(uid, out var itemIndex, out _))
                {
                    return false;
                }

                return SendBattleCmd(
                    battleMgr,
                    uid,
                    "I|" + itemIndex.ToString("X") + "|" + targetIndex.ToString("X"),
                    setMagic: true);
            }

            if (partySlot == 1)
            {
                if (!TryResolveSkillOne(uid, battleMgr, out var skillIndex, out var techIndex))
                {
                    return false;
                }

                return SendBattleCmd(
                    battleMgr,
                    uid,
                    "S|" + skillIndex.ToString("X") + "|" + techIndex.ToString("X") + "|"
                    + targetIndex.ToString("X"),
                    setMagic: true);
            }

            return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 人物 2动入口（钩 AutoFight_PlayerAction2）。
    /// 有一级时一律防御，避免无宠时原版自动出手。
    /// </summary>
    public static bool TryPlayerAutoCatch2()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            if (!TryFindLevelOneEnemy(out _))
            {
                return false;
            }

            NoteLevelOneEncounterOnce();

            var uid = GetStaticString("BattleDataHolder", "CurrentAccount");
            if (string.IsNullOrEmpty(uid))
            {
                return false;
            }

            var battleMgr = GetManagerInstance("BattleManager");
            if (battleMgr == null || !IsPlayerAlive(battleMgr))
            {
                return false;
            }

            return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>宠物自动行动：有一级则防御；否则 false 走原逻辑。</summary>
    public static bool TryPetAutoCatch()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            if (!TryFindLevelOneEnemy(out _))
            {
                return false;
            }

            NoteLevelOneEncounterOnce();

            var uid = GetStaticString("BattleDataHolder", "CurrentAccount");
            if (string.IsNullOrEmpty(uid))
            {
                return false;
            }

            var battleMgr = GetManagerInstance("BattleManager");
            if (battleMgr == null)
            {
                return false;
            }

            if (!TryBuildPetDefendCommand(uid, battleMgr, out var cmd))
            {
                // 与原版缺指令时一致；勿 return false 以免再走自动出手
                cmd = "W|FF|FF";
            }

            return SendBattleCmd(battleMgr, uid, cmd, setMagic: false);
        }
        catch
        {
            return false;
        }
    }

    private static void TryHookExitBattle()
    {
        if (_exitHooked)
        {
            return;
        }

        try
        {
            var ecType = FindType("EventCenter");
            if (ecType == null)
            {
                return;
            }

            object instance = null;
            for (var cur = ecType; cur != null; cur = cur.BaseType)
            {
                var instProp = cur.GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (instProp != null)
                {
                    instance = instProp.GetValue(null, null);
                    if (instance != null)
                    {
                        break;
                    }
                }
            }

            if (instance == null)
            {
                return;
            }

            var exitEv = GetMember(instance, "ExitBattle");
            if (exitEv == null)
            {
                return;
            }

            _onExitBattle = OnBattleExited;
            var add = exitEv.GetType().GetMethod(
                "Add",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Action) },
                null);
            if (add == null)
            {
                return;
            }

            add.Invoke(exitEv, new object[] { _onExitBattle });
            _exitHooked = true;
        }
        catch
        {
            // 退战钩失败时仍可用战斗内逻辑；满宠停挂机不可用
        }
    }

    private static void OnBattleExited()
    {
        _countedLevelOneThisBattle = false;

        if (!IsPipelineActive())
        {
            return;
        }

        try
        {
            var mainUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
            if (string.IsNullOrEmpty(mainUid))
            {
                return;
            }

            // 仅队长客户端
            if (GetPartySlot(mainUid) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _exitPipelineRunning, 1, 0) != 0)
            {
                return;
            }

            var uid = mainUid;
            var thread = new Thread(() =>
            {
                try
                {
                    RunExitPipeline(uid);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    Interlocked.Exchange(ref _exitPipelineRunning, 0);
                }
            });
            thread.IsBackground = true;
            thread.Name = TypeName + ".ExitPipeline";
            thread.Start();
        }
        catch
        {
            Interlocked.Exchange(ref _exitPipelineRunning, 0);
        }
    }

    /// <summary>
    /// 退战入口（后台线程）：
    /// · 需停挂机时立刻发「停止挂机」（不等 1 秒间隔、也不等改名轮询）；
    /// · 再标记未改名 1 级宠（#档 / #档@随机≥6 / #满 / #满@随机≥6）；
    /// · 仍在挂机且满 5 宠 → 存仓休息+1级 → 终检；
    /// · 未满、无封印卡 → 仅停挂机；未满有卡 → 不停挂机。
    /// </summary>
    private static void RunExitPipeline(string uid)
    {
        if (!IsPipelineActive())
        {
            return;
        }

        _exitNeedProtocolGap = false;

        var encounterActive = IsEncounterActive(uid);
        var petFull = encounterActive && HavePetCount(uid) >= 5;

        if (encounterActive)
        {
            // 满宠 或 无卡：立刻停挂机（不走 RunProtocolStep，避免被间隔/改名拖住）
            if (petFull || !TryFindSealCard(uid, out _, out _))
            {
                try
                {
                    TrySendAutoBattle("停止挂机", uid);
                }
                catch
                {
                    // ignore
                }

                _exitNeedProtocolGap = true;
            }
        }

        // 抓宠开启时：每次队长退战都扫未标记宠并改名（停挂机之后、存仓之前）
        TryRenameUnmarkedBagPets(uid);

        if (!encounterActive || !petFull)
        {
            return;
        }

        StoreCaptainRestLevelOnePets(uid);
        FinalizeEncounterState(uid);
    }

    /// <summary>
    /// 仅 1 级、未正确标记的宠：#档位；单项随机档 ≥6 时追加 @N（满档 #满 / #满@N）。
    /// 随机档 = ResetBaseInfo 的 Vital/Str/Tgh/Quick/Magic base 最大值。
    /// </summary>
    private static void TryRenameUnmarkedBagPets(string uid)
    {
        if (!IsPipelineActive() || string.IsNullOrEmpty(uid))
        {
            return;
        }

        try
        {
            // RESETBASE_INFO 可能略晚于进包
            WaitForLevelOnePetResetBaseSync(uid);

            if (!IsPipelineActive())
            {
                return;
            }

            var pets = GetPetList(uid);
            if (pets == null || pets.Count == 0)
            {
                return;
            }

            var petMgr = GetManagerInstance("PetManager");
            if (petMgr == null)
            {
                return;
            }

            var sendChange = FindSendChangePetName(petMgr);
            if (sendChange == null)
            {
                return;
            }

            MethodInfo getFileValue = null;
            foreach (var m in petMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "GetPetFileValue")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 1)
                {
                    getFileValue = m;
                    break;
                }
            }

            for (var i = 0; i < pets.Count && i < 5; i++)
            {
                if (!IsPipelineActive())
                {
                    return;
                }

                var pet = pets[i];
                if (pet == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var data = GetMember(pet, "data");
                if (data == null)
                {
                    continue;
                }

                var level = ReadIntMember(data, "Level");
                if (level != StorePetLevel)
                {
                    continue;
                }

                if (!TryGetMaxResetBaseRandom(data, out var maxRand))
                {
                    // ResetBaseInfo 未到：不改名
                    continue;
                }

                var grade = 0;
                var perfect = IsPerfectPet(pet, data);
                if (!perfect)
                {
                    grade = GetPetGradeValue(petMgr, getFileValue, data);
                    if (grade < 0)
                    {
                        grade = 0;
                    }
                }

                var newName = FormatPetMarkName(perfect, grade, maxRand);
                var display = GetDisplayPetName(data);
                if (!NeedsPetRenameMark(display, newName, maxRand))
                {
                    continue;
                }

                var index = Convert.ToInt32(GetMember(data, "Index") ?? i);
                var nameLocal = newName;
                var indexLocal = index;
                var mgrLocal = petMgr;
                var sendLocal = sendChange;
                RunProtocolStep(() =>
                {
                    sendLocal.Invoke(mgrLocal, new object[] { uid, indexLocal, nameLocal });
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>#档位 或 #满；仅当 maxRand ≥ MinRandomSuffix 时追加 @N。</summary>
    private static string FormatPetMarkName(bool perfect, int grade, int maxRand)
    {
        var head = perfect ? "#满" : "#" + grade;
        if (maxRand >= MinRandomSuffix)
        {
            return head + "@" + maxRand;
        }

        return head;
    }

    /// <summary>
    /// 无 # → 标记；已 # 仅修正：误标 @0/@&lt;6，或漏掉的 ≥6 @后缀。
    /// </summary>
    private static bool NeedsPetRenameMark(string display, string newName, int maxRand)
    {
        if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(newName))
        {
            return false;
        }

        if (string.Equals(display, newName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!display.StartsWith("#", StringComparison.Ordinal))
        {
            return true;
        }

        var at = display.LastIndexOf('@');
        if (at > 0 && at < display.Length - 1)
        {
            if (int.TryParse(display.Substring(at + 1), out var tagged)
                && tagged < MinRandomSuffix)
            {
                // #13@5 / #13@0 → 去掉低随机或改成正确名
                return true;
            }
        }
        else if (maxRand >= MinRandomSuffix)
        {
            // #13 但实际有 ≥6 随机 → 补 @N
            return true;
        }

        return false;
    }

    /// <summary>轮询至多 ~2.5s：等「可能需要改名」的 1 级宠 ResetBaseInfo 到位。</summary>
    private static void WaitForLevelOnePetResetBaseSync(string uid)
    {
        const int attempts = 13;
        const int gapMs = 200;
        for (var n = 0; n < attempts; n++)
        {
            if (!IsPipelineActive())
            {
                return;
            }

            try
            {
                var pets = GetPetList(uid);
                if (pets != null)
                {
                    var pending = 0;
                    var ready = 0;
                    for (var i = 0; i < pets.Count && i < 5; i++)
                    {
                        var pet = pets[i];
                        if (pet == null || Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
                        {
                            continue;
                        }

                        var data = GetMember(pet, "data");
                        if (data == null || ReadIntMember(data, "Level") != StorePetLevel)
                        {
                            continue;
                        }

                        var display = GetDisplayPetName(data);
                        if (!MightNeedPetRename(display))
                        {
                            continue;
                        }

                        pending++;
                        if (TryGetMaxResetBaseRandom(data, out _))
                        {
                            ready++;
                        }
                    }

                    if (pending == 0 || ready >= pending)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // keep waiting
            }

            try
            {
                Thread.Sleep(gapMs);
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>无 #，或已有 # 但带 @&lt;6（含 @0）——需要等数据再决定最终名。</summary>
    private static bool MightNeedPetRename(string display)
    {
        if (string.IsNullOrEmpty(display))
        {
            return false;
        }

        if (!display.StartsWith("#", StringComparison.Ordinal))
        {
            return true;
        }

        var at = display.LastIndexOf('@');
        if (at > 0 && at < display.Length - 1
            && int.TryParse(display.Substring(at + 1), out var tagged)
            && tagged < MinRandomSuffix)
        {
            return true;
        }

        return false;
    }

    private static MethodInfo FindSendChangePetName(object petMgr)
    {
        if (petMgr == null)
        {
            return null;
        }

        foreach (var m in petMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendChangePetName")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 3
                && ps[0].ParameterType == typeof(string)
                && (ps[1].ParameterType == typeof(int) || ps[1].ParameterType == typeof(short))
                && ps[2].ParameterType == typeof(string))
            {
                return m;
            }
        }

        return null;
    }

    private static string GetDisplayPetName(object petInfo)
    {
        var free = Convert.ToString(GetMember(petInfo, "FreeName") ?? "") ?? "";
        if (!string.IsNullOrEmpty(free))
        {
            return free;
        }

        return Convert.ToString(GetMember(petInfo, "Name") ?? "") ?? "";
    }

    private static bool IsPerfectPet(object petData, object petInfo)
    {
        try
        {
            var flag = GetMember(petData, "isPrefectPet");
            if (flag is bool b)
            {
                return b;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            return Convert.ToInt32(GetMember(petInfo, "Nowvitalbase") ?? 0)
                   >= Convert.ToInt32(GetMember(petInfo, "Maxvitalbase") ?? 0)
                   && Convert.ToInt32(GetMember(petInfo, "Nowstrbase") ?? 0)
                   >= Convert.ToInt32(GetMember(petInfo, "Maxstrbase") ?? 0)
                   && Convert.ToInt32(GetMember(petInfo, "Nowtghbase") ?? 0)
                   >= Convert.ToInt32(GetMember(petInfo, "Maxtghbase") ?? 0)
                   && Convert.ToInt32(GetMember(petInfo, "Nowquickbase") ?? 0)
                   >= Convert.ToInt32(GetMember(petInfo, "Maxquickbase") ?? 0)
                   && Convert.ToInt32(GetMember(petInfo, "Nowmagicbase") ?? 0)
                   >= Convert.ToInt32(GetMember(petInfo, "Maxmagicbase") ?? 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>差档（与 PetManager.GetPetFileValue 一致）：各维 Max*base 之和 − Now*base 之和。</summary>
    private static int GetPetGradeValue(object petMgr, MethodInfo getFileValue, object petInfo)
    {
        if (petMgr != null && getFileValue != null)
        {
            try
            {
                return Convert.ToInt32(getFileValue.Invoke(petMgr, new object[] { petInfo }) ?? 0);
            }
            catch
            {
                // fall through
            }
        }

        try
        {
            var maxSum = Convert.ToInt32(GetMember(petInfo, "Maxvitalbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Maxstrbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Maxtghbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Maxquickbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Maxmagicbase") ?? 0);
            var nowSum = Convert.ToInt32(GetMember(petInfo, "Nowvitalbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Nowstrbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Nowtghbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Nowquickbase") ?? 0)
                         + Convert.ToInt32(GetMember(petInfo, "Nowmagicbase") ?? 0);
            return maxSum - nowSum;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 洗随机页当前五项：ResetBaseInfo.Vital/Str/Tgh/Quick/Magic + base。
    /// 与 Com_RefRandomItem（selfData）一致。无 ResetBaseInfo 返回 false。
    /// </summary>
    private static bool TryGetMaxResetBaseRandom(object petInfo, out int maxRand)
    {
        maxRand = 0;
        var reset = GetMember(petInfo, "ResetBaseInfo");
        if (reset == null)
        {
            return false;
        }

        foreach (var name in new[]
                 {
                     "Vitalbase", "Strbase", "Tghbase", "Quickbase", "Magicbase",
                     "vitalbase_", "strbase_", "tghbase_", "quickbase_", "magicbase_"
                 })
        {
            var v = ReadIntMember(reset, name);
            if (v > maxRand)
            {
                maxRand = v;
            }
        }

        return true;
    }

    private static int ReadIntMember(object obj, string name)
    {
        try
        {
            var v = GetMember(obj, name);
            if (v == null || v is bool)
            {
                return 0;
            }

            return Convert.ToInt32(v);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>当前是否仍在自动遇敌/挂机中（false = 已停，含手动/受伤等）。</summary>
    private static bool IsEncounterActive(string uid)
    {
        try
        {
            var player = GetPlayerFromUid(uid);
            if (player != null)
            {
                var status = Convert.ToInt32(GetMember(player, "encounterStatus") ?? 0);
                if (status != 0)
                {
                    return true;
                }
            }

            var pdata = GetStaticField("PlayerDataHolder", "playerData");
            return Convert.ToInt32(GetMember(pdata, "encounterStatus") ?? 0) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void FinalizeEncounterState(string uid)
    {
        if (!IsPipelineActive())
        {
            return;
        }

        // 满宠流水线终检：满宠或无卡 → 停；有空位且有卡 → 开挂机
        if (HavePetCount(uid) >= 5 || !TryFindSealCard(uid, out _, out _))
        {
            RunProtocolStep(() => TrySendAutoBattle("停止挂机", uid));
            return;
        }

        RunProtocolStep(() => TrySendAutoBattle("开始挂机", uid));
    }

    private static void RunProtocolStep(Action send)
    {
        if (!IsPipelineActive() || send == null)
        {
            return;
        }

        if (_exitNeedProtocolGap)
        {
            try
            {
                Thread.Sleep(ProtocolGapMs);
            }
            catch
            {
                // ignore
            }
        }

        if (!IsPipelineActive())
        {
            return;
        }

        try
        {
            send();
        }
        catch
        {
            // ignore single step
        }

        _exitNeedProtocolGap = true;
    }

    /// <summary>
    /// 退战存仓：队长休息且 1 级。打开远程仓 + 每只存宠各算一次发包（间隔 1 秒）。
    /// </summary>
    private static void StoreCaptainRestLevelOnePets(string uid)
    {
        if (!IsPipelineActive())
        {
            return;
        }

        try
        {
            var pets = GetPetList(uid);
            if (pets == null)
            {
                return;
            }

            var storePets = new List<object>();
            var storeIndexes = new List<int>();
            for (var i = 0; i < pets.Count && i < 5; i++)
            {
                var pet = pets[i];
                if (pet == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var data = GetMember(pet, "data");
                if (data == null)
                {
                    continue;
                }

                var status = Convert.ToInt32(GetMember(data, "DepartureBattleStatus") ?? -1);
                if (status != PetStatusRest)
                {
                    continue;
                }

                var level = Convert.ToInt32(GetMember(data, "Level") ?? 0);
                if (level != StorePetLevel)
                {
                    continue;
                }

                var index = Convert.ToInt32(GetMember(data, "Index") ?? i);
                storePets.Add(pet);
                storeIndexes.Add(index);
            }

            if (storeIndexes.Count == 0)
            {
                return;
            }

            RunProtocolStep(() => TryOpenRemotePersonalPetBank(uid));

            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr == null)
            {
                return;
            }

            var bankType = ResolvePersonalBankType();
            if (bankType == null)
            {
                return;
            }

            MethodInfo sendBank = null;
            foreach (var m in roleMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendBankMessage")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 4 && ps.Length <= 6)
                {
                    sendBank = m;
                    break;
                }
            }

            if (sendBank == null)
            {
                return;
            }

            for (var i = 0; i < storeIndexes.Count; i++)
            {
                if (!IsPipelineActive())
                {
                    return;
                }

                var pet = storePets[i];
                var index = storeIndexes[i];
                var sendBankLocal = sendBank;
                RunProtocolStep(() =>
                {
                    var ps = sendBankLocal.GetParameters();
                    object[] args;
                    if (ps.Length >= 6)
                    {
                        args = new object[] { bankType, uid, "存宠物", index, 0, null };
                    }
                    else if (ps.Length == 5)
                    {
                        args = new object[] { bankType, uid, "存宠物", index, 0 };
                    }
                    else
                    {
                        args = new object[] { bankType, uid, "存宠物", index };
                    }

                    sendBankLocal.Invoke(roleMgr, args);
                    SetMember(pet, "useFlag", 0);
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static IList GetPetList(string uid)
    {
        var holder = FindType("PlayerDataHolder");
        var getPets = holder?.GetMethod(
            "GetPetDatasFromUid",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        return getPets?.Invoke(null, new object[] { uid }) as IList;
    }


    private static void TryOpenRemotePersonalPetBank(string uid)
    {
        try
        {
            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr != null)
            {
                SetMember(roleMgr, "OpenBankFromPet", true);
            }

            var actMgr = GetManagerInstance("ActivityManager");
            if (actMgr == null)
            {
                return;
            }

            MethodInfo send = null;
            foreach (var m in actMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendActivity")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 4
                    && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(string))
                {
                    send = m;
                    break;
                }

                if (send == null && ps.Length >= 2 && ps[0].ParameterType == typeof(string))
                {
                    send = m;
                }
            }

            if (send == null)
            {
                return;
            }

            var ps2 = send.GetParameters();
            // SendActivity("远程个人宠物仓库", uid, 0, 19)
            if (ps2.Length >= 4)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid, 0, 19 });
            }
            else if (ps2.Length == 3)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid, 0 });
            }
            else if (ps2.Length == 2)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid });
            }
        }
        catch
        {
            // 打不开远程仓也不阻断后续「存宠物」尝试
        }
    }

    private static object ResolvePersonalBankType()
    {
        try
        {
            var t = FindType("BANK_TYPE");
            if (t == null || !t.IsEnum)
            {
                return null;
            }

            try
            {
                return Enum.Parse(t, "PERSONAL_BANK", ignoreCase: true);
            }
            catch
            {
                // fall through
            }

            foreach (var name in Enum.GetNames(t))
            {
                if (name.IndexOf("PERSONAL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Enum.Parse(t, name);
                }
            }

            // 常见：0=个人银行
            var values = Enum.GetValues(t);
            return values.Length > 0 ? values.GetValue(0) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySendAutoBattle(string action, string mainUid)
    {
        try
        {
            if (action == "停止挂机")
            {
                var player = GetPlayerFromUid(mainUid);
                if (player != null)
                {
                    var status = Convert.ToInt32(GetMember(player, "encounterStatus") ?? 0);
                    if (status == 0)
                    {
                        var pdata = GetStaticField("PlayerDataHolder", "playerData");
                        status = Convert.ToInt32(GetMember(pdata, "encounterStatus") ?? 0);
                    }

                    if (status == 0)
                    {
                        return;
                    }
                }
            }

            var roleMgr = GetManagerInstance("RoleManager");
            var send = roleMgr?.GetType().GetMethod(
                "SendAutoBattle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            send?.Invoke(roleMgr, new object[] { action, mainUid });
        }
        catch
        {
            // ignore
        }
    }

    private static int HavePetCount(string uid)
    {
        try
        {
            var holder = FindType("PlayerDataHolder");
            var m = holder?.GetMethod(
                "HavePetCount",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (m != null)
            {
                return Convert.ToInt32(m.Invoke(null, new object[] { uid }) ?? 0);
            }

            var getPets = holder?.GetMethod(
                "GetPetDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var list = getPets?.Invoke(null, new object[] { uid }) as IList;
            if (list == null)
            {
                return 0;
            }

            var n = 0;
            for (var i = 0; i < list.Count && i < 5; i++)
            {
                var pet = list[i];
                if (pet != null && Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) > 0)
                {
                    n++;
                }
            }

            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static object GetPlayerFromUid(string uid)
    {
        try
        {
            var holder = FindType("PlayerDataHolder");
            var m = holder?.GetMethod(
                "GetPlayerFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return m?.Invoke(null, new object[] { uid });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 人物是否已进入 2动：BattleManager.FightProcessFlag 含 PlayerActionEnd(1)。
    /// 无宠时 AutoFight_PlayerAction 会再调一次，此时应防御。
    /// </summary>
    private static bool IsPlayerSecondAction(object battleMgr)
    {
        try
        {
            var flag = GetMember(battleMgr, "FightProcessFlag");
            if (flag == null)
            {
                return false;
            }

            // FightProcessFlag.PlayerActionEnd = 1
            return (Convert.ToInt32(flag) & 1) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>队序：teamData 中 UseFlag==1 的槽位序号；队长为 0。单人无队则视为 0。</summary>
    private static int GetPartySlot(string uid)
    {
        try
        {
            var teamData = GetStaticField("PlayerDataHolder", "teamData") as Array;
            if (teamData == null || teamData.Length == 0)
            {
                return 0;
            }

            var slot = 0;
            var any = false;
            for (var i = 0; i < teamData.Length; i++)
            {
                var entry = teamData.GetValue(i);
                if (entry == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(entry, "UseFlag") ?? 0) != 1)
                {
                    continue;
                }

                any = true;
                var player = GetMember(entry, "Player");
                var entryUid = Convert.ToString(GetMember(player, "Uid") ?? "");
                if (string.Equals(entryUid, uid, StringComparison.Ordinal))
                {
                    return slot;
                }

                slot++;
            }

            return any ? 99 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsPlayerAlive(object battleMgr)
    {
        var playerIndex = Convert.ToInt32(GetMember(battleMgr, "PlayerIndex") ?? -1);
        var roleDic = GetStaticField("BattleRoleContainer", "BattleRoleDic") as IDictionary;
        if (roleDic == null || !roleDic.Contains(playerIndex))
        {
            return false;
        }

        var playerRole = roleDic[playerIndex];
        return playerRole != null && !Convert.ToBoolean(GetMember(playerRole, "IsDead") ?? false);
    }

    /// <summary>
    /// 找可抓的一级敌宠：非迷你蝙蝠，且 LevelOneFlag 或等级为 1。
    /// 原版不给哥布林(101800) 打 LevelOneFlag，故必须用 Level==1 兜底。
    /// </summary>
    private static bool TryFindLevelOneEnemy(out int targetIndex)
    {
        targetIndex = -1;
        var roleDic = GetStaticField("BattleRoleContainer", "BattleRoleDic") as IDictionary;
        if (roleDic == null)
        {
            return false;
        }

        var playerIndex = Convert.ToInt32(
            GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? 0);
        var enemyLo = playerIndex < 10 ? 10 : 0;
        var enemyHi = playerIndex < 10 ? 20 : 10;

        foreach (DictionaryEntry entry in roleDic)
        {
            var idx = Convert.ToInt32(entry.Key);
            if (idx < enemyLo || idx >= enemyHi)
            {
                continue;
            }

            var role = entry.Value;
            if (role == null || Convert.ToBoolean(GetMember(role, "IsDead") ?? false))
            {
                continue;
            }

            var roleData = GetMember(role, "RoleData");
            if (!IsCatchableLevelOne(roleData))
            {
                continue;
            }

            targetIndex = idx;
            return true;
        }

        return false;
    }

    /// <summary>可抓一级：非迷你蝙蝠，且（LevelOneFlag 或 Level==1）。</summary>
    private static bool IsCatchableLevelOne(object roleData)
    {
        if (roleData == null)
        {
            return false;
        }

        var ch = GetMember(roleData, "Char");
        var animId = Convert.ToInt32(GetMember(ch, "AnimationId") ?? 0);
        if (animId == MiniBatAnimationId)
        {
            return false;
        }

        // 含哥布林：原版不打 LevelOneFlag，但「遇敌一级含哥布林」补丁后会打上；同时用 Level==1 兜底
        if (Convert.ToBoolean(GetMember(roleData, "LevelOneFlag") ?? false))
        {
            return true;
        }

        return TryReadEnemyLevel(roleData, ch) == 1;
    }

    private static int TryReadEnemyLevel(object roleData, object ch)
    {
        foreach (var host in new[] { roleData, ch })
        {
            if (host == null)
            {
                continue;
            }

            foreach (var name in new[] { "Level", "level", "Lv", "EnemyLevel" })
            {
                var v = GetMember(host, name);
                if (v == null || v is bool)
                {
                    continue;
                }

                try
                {
                    var n = Convert.ToInt32(v);
                    if (n > 0 && n < 500)
                    {
                        return n;
                    }
                }
                catch
                {
                    // try next
                }
            }
        }

        return 0;
    }

    private static bool TryResolveSkillOne(
        string uid,
        object battleMgr,
        out int skillIndex,
        out int techIndex)
    {
        skillIndex = 0;
        techIndex = 0;
        try
        {
            var configs = GetMember(battleMgr, "PlayerAutoConfigs") as IDictionary;
            if (configs != null && configs.Contains(uid))
            {
                var pac = configs[uid];
                var arr = GetMember(pac, "Config") as IList;
                if (arr != null && arr.Count > 0 && arr[0] != null)
                {
                    var c0 = arr[0];
                    var type = Convert.ToInt32(GetMember(c0, "Type") ?? 0);
                    if (type == 3)
                    {
                        skillIndex = Convert.ToInt32(GetMember(c0, "Skillindex") ?? 0);
                        techIndex = Convert.ToInt32(GetMember(c0, "Techindex") ?? 0);
                        return true;
                    }
                }
            }

            var holder = FindType("PlayerDataHolder");
            var getMagic = holder?.GetMethod(
                "GetMagicDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var dict = getMagic?.Invoke(null, new object[] { uid }) as IDictionary;
            if (dict == null || dict.Count == 0)
            {
                return false;
            }

            var keys = new List<int>();
            foreach (DictionaryEntry e in dict)
            {
                keys.Add(Convert.ToInt32(e.Key));
            }

            keys.Sort();
            skillIndex = keys[0];
            techIndex = 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildPetDefendCommand(string uid, object battleMgr, out string cmd)
    {
        cmd = "";
        try
        {
            var player = GetPlayerFromUid(uid);
            if (player == null)
            {
                return false;
            }

            var battlePetId = Convert.ToInt32(GetMember(player, "battlePetID") ?? -1);
            if (battlePetId < 0)
            {
                return false;
            }

            var holder = FindType("PlayerDataHolder");
            var getPets = holder?.GetMethod(
                "GetPetDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var pets = getPets?.Invoke(null, new object[] { uid }) as IList;
            if (pets == null || battlePetId >= pets.Count)
            {
                return false;
            }

            var pet = pets[battlePetId];
            if (pet == null || Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
            {
                return false;
            }

            var data = GetMember(pet, "data");
            var skills = GetMember(data, "PetSkills") as IList;
            if (skills == null)
            {
                return false;
            }

            // SendPetCommand 用 Techindex 下标取 PetSkills[i]；优先匹配 SkillId=74 的下标。
            // 若技能的 Index 字段与下标不一致，仍用下标（与 SendPetCommand 一致）。
            var defendSlot = -1;
            for (var i = 0; i < skills.Count; i++)
            {
                var sk = skills[i];
                if (sk == null)
                {
                    continue;
                }

                var sid = Convert.ToInt32(GetMember(sk, "SkillId") ?? 0);
                if (sid == PetDefendSkillId)
                {
                    defendSlot = i;
                    break;
                }
            }

            if (defendSlot < 0)
            {
                return false;
            }

            var petIndex = ResolveBattlePetIndex(battleMgr);
            if (petIndex < 0)
            {
                return false;
            }

            cmd = "W|" + defendSlot.ToString("X") + "|" + petIndex.ToString("X");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>对齐 BattleManager.GetBattlePetIndex(false)。</summary>
    private static int ResolveBattlePetIndex(object battleMgr)
    {
        try
        {
            var getPetIdx = battleMgr.GetType().GetMethod(
                "GetBattlePetIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(bool) },
                null);
            if (getPetIdx != null)
            {
                return Convert.ToInt32(getPetIdx.Invoke(battleMgr, new object[] { false }) ?? -1);
            }
        }
        catch
        {
            // fall through
        }

        var playerIndex = Convert.ToInt32(GetMember(battleMgr, "PlayerIndex") ?? 0);
        return (playerIndex % 10) < 5 ? playerIndex + 5 : playerIndex - 5;
    }

    private static bool TryFindSealCard(string uid, out int itemIndex, out string itemName)
    {
        var foundIndex = -1;
        var foundName = "";
        var found = EnumerateSealCards(uid, (index, name, _) =>
        {
            foundIndex = index;
            foundName = name;
            return false; // 只要第一张
        });
        itemIndex = foundIndex;
        itemName = foundName;
        return found;
    }

    /// <summary>背包封印卡总张数（各格 Pile 之和；与战斗用卡判定一致）。</summary>
    private static int CountSealCardsInBag(string uid)
    {
        var total = 0;
        EnumerateSealCards(uid, (_, __, pile) =>
        {
            total += pile > 0 ? pile : 1;
            return true; // 继续扫
        });
        return total;
    }

    /// <summary>
    /// 遍历背包封印卡。onFound 返回 true 继续，false 停止。
    /// 有找到过至少一张则整体返回 true。
    /// </summary>
    private static bool EnumerateSealCards(string uid, Func<int, string, int, bool> onFound)
    {
        if (string.IsNullOrEmpty(uid) || onFound == null)
        {
            return false;
        }

        var holder = FindType("PlayerDataHolder");
        var getItems = holder?.GetMethod(
            "GetItemDatasFromUid",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (getItems == null)
        {
            return false;
        }

        var list = getItems.Invoke(null, new object[] { uid }) as IList;
        if (list == null)
        {
            return false;
        }

        MethodInfo canUse = null;
        var itemMgr = GetManagerInstance("ItemManager");
        if (itemMgr != null)
        {
            foreach (var m in itemMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "CanUseInBattle" && m.GetParameters().Length == 2)
                {
                    canUse = m;
                    break;
                }
            }
        }

        var foundAny = false;
        for (var i = 8; i < list.Count; i++)
        {
            var item = list[i];
            if (item == null)
            {
                continue;
            }

            if (Convert.ToInt32(GetMember(item, "useFlag") ?? 0) != 1)
            {
                continue;
            }

            var data = GetMember(item, "data");
            if (data == null)
            {
                continue;
            }

            var typeVal = Convert.ToInt32(GetMember(data, "Type") ?? 0);
            if (typeVal < 23)
            {
                continue;
            }

            var flg = Convert.ToInt32(GetMember(data, "Flg") ?? 0);
            var name = Convert.ToString(GetMember(data, "Name") ?? GetMember(data, "name") ?? "");
            var typeName = Convert.ToString(GetMember(data, "TypeName") ?? "");
            var nameHit = (!string.IsNullOrEmpty(name) && name.IndexOf("封印", StringComparison.Ordinal) >= 0)
                          || (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("封印", StringComparison.Ordinal) >= 0);
            if (((flg & SealFlagMask) == 0) && !nameHit)
            {
                continue;
            }

            if (canUse != null)
            {
                try
                {
                    if (!Convert.ToBoolean(canUse.Invoke(itemMgr, new object[] { data, uid })))
                    {
                        continue;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            var rawIndex = Convert.ToInt32(GetMember(data, "Index") ?? i);
            if (rawIndex < 8)
            {
                rawIndex = i;
            }

            var pile = ReadIntMember(data, "Pile");
            var itemName = string.IsNullOrEmpty(name) ? ("#" + rawIndex) : name;
            foundAny = true;
            if (!onFound(rawIndex, itemName, pile))
            {
                return true;
            }
        }

        return foundAny;
    }

    private static bool SendBattleCmd(object battleMgr, string uid, string cmd, bool setMagic)
    {
        var send = battleMgr.GetType().GetMethod(
            "SendBattleCommond",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        if (send == null)
        {
            return false;
        }

        SetStaticMember("BattleDataHolder", "skillUsed", true);
        if (setMagic)
        {
            TrySetPlayerActionMagic(battleMgr, uid, true);
        }

        send.Invoke(battleMgr, new object[] { cmd });
        return true;
    }

    private static void TrySetPlayerActionMagic(object battleMgr, string uid, bool value)
    {
        try
        {
            var pam = GetMember(battleMgr, "PlayerActionMagics") as IDictionary;
            if (pam != null)
            {
                pam[uid] = value;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void Tip(string msg)
    {
        try
        {
            if (string.IsNullOrEmpty(msg))
            {
                return;
            }

            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            // HybridCLR：不能用 typeof(string)/typeof(bool) 做 GetMethod 签名匹配（跨程序集 Type 对不上会找不到 Tip）
            var tip = FindTipMethod(notify.GetType());
            if (tip == null)
            {
                return;
            }

            var ps = tip.GetParameters();
            if (ps.Length >= 2)
            {
                tip.Invoke(notify, new object[] { msg, false });
            }
            else
            {
                tip.Invoke(notify, new object[] { msg });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static MethodInfo FindTipMethod(Type notifyType)
    {
        MethodInfo oneArg = null;
        foreach (var m in notifyType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "Tip")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 2
                && ps[0].ParameterType.FullName == "System.String"
                && ps[1].ParameterType.FullName == "System.Boolean")
            {
                return m;
            }

            if (ps.Length == 1 && ps[0].ParameterType.FullName == "System.String")
            {
                oneArg = m;
            }
        }

        return oneArg;
    }

    private static object GetManagerInstance(string typeName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
                        | BindingFlags.FlattenHierarchy;

            var instProp = cur.GetProperty("Instance", flags);
            if (instProp != null)
            {
                try
                {
                    var inst = instProp.GetValue(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // try next
                }
            }

            // HybridCLR 有时属性反射不到，但 get_Instance 方法在
            var getter = cur.GetMethod("get_Instance", flags, null, Type.EmptyTypes, null);
            if (getter != null)
            {
                try
                {
                    var inst = getter.Invoke(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // try next
                }
            }

            var instField = cur.GetField("Instance", flags);
            if (instField != null)
            {
                try
                {
                    var inst = instField.GetValue(null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // try next
                }
            }
        }

        return null;
    }

    private static Type FindType(string typeName)
    {
        try
        {
            var hotfixAsm = FindHotfixAssembly();
            if (hotfixAsm != null)
            {
                var t = hotfixAsm.GetType(typeName, false, false)
                        ?? hotfixAsm.GetType("Hotfix." + typeName, false, false);
                if (t != null)
                {
                    return t;
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            return Type.GetType(typeName, false)
                   ?? Type.GetType(typeName + ", hotfix", false)
                   ?? Type.GetType("Hotfix." + typeName + ", hotfix", false);
        }
        catch
        {
            return null;
        }
    }

    private static Assembly FindHotfixAssembly()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(asm.GetName().Name, "hotfix", StringComparison.OrdinalIgnoreCase))
                {
                    return asm;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static object GetMember(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var t = obj.GetType();
        var p = t.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null)
        {
            return p.GetValue(obj);
        }

        var f = t.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return f?.GetValue(obj);
    }

    private static void SetMember(object obj, string name, object value)
    {
        if (obj == null)
        {
            return;
        }

        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite)
            {
                p.SetValue(obj, value, null);
                return;
            }

            var f = t.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            f?.SetValue(obj, value);
        }
        catch
        {
            // ignore
        }
    }

    private static object GetStaticMember(string typeName, string name)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        var p = t.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null)
        {
            return p.GetValue(null, null);
        }

        return GetStaticField(typeName, name);
    }

    private static object GetStaticField(string typeName, string name)
    {
        var t = FindType(typeName);
        var f = t?.GetField(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        return f?.GetValue(null);
    }

    private static string GetStaticString(string typeName, string name)
    {
        return Convert.ToString(GetStaticMember(typeName, name) ?? "");
    }

    private static void SetStaticMember(string typeName, string name, object value)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return;
        }

        var p = t.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null && p.CanWrite)
        {
            p.SetValue(null, value, null);
            return;
        }

        t.GetField(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            ?.SetValue(null, value);
    }
}
