using System;
using System.Collections;
using System.Reflection;
using System.Threading;

/// <summary>
/// 自动烧卡 DLL（原「自动封印」）。部署为 hotfixdata/SeqChapterAutoSeal.dll.bytes
/// Pause 延迟加载后 Bootstrap；AutoFight_PlayerAction 入口调 TryPlayerAutoSeal。
/// 侧栏百科 = 手动开关：默认 PipelineEnabled=false；点百科 Tip 切换开/关。
/// 开启后：仅队长（本机 MainPlayerUid 且队序 0）回合，从其背包扔封印卡；队员不烧卡。
/// 退战（仅队长）：Tip 队长背包封印卡剩余数量；若为 0 则发「停止挂机」。
/// 开启时窗口标题追加「 ★自动烧卡中★」。关闭后不走烧卡逻辑。
/// </summary>
public static class SeqChapterAutoSeal
{
    public const string AssetPath = "hotfixdata/SeqChapterAutoSeal.dll.bytes";

    /// <summary>烧卡总开关。默认关闭；点侧栏百科切换。</summary>
    public static volatile bool PipelineEnabled = false;

    private const int SealFlagMask = 0x100;

    private static bool _bootstrapped;
    private static bool _exitHooked;
    private static Action _onExitBattle;
    private static int _exitPipelineRunning;

    public static bool IsPipelineActive()
    {
        if (PipelineEnabled)
        {
            return true;
        }

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

    /// <summary>MapSidebarPanel.OnClickWiki：切换烧卡；返回是否开启（由 hotfix IL 用原版 Tip 提示）。</summary>
    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetPipelineEnabledAllCopies(enable);
        RefreshWindowTitle();
        return enable;
    }

    /// <summary>
    /// 与游戏一致：{产品名} {服务器} {角色} Lv.{等级}；
    /// 烧卡开启时追加「 ★自动烧卡中★」。
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
                title = title + " ★自动烧卡中★";
            }

            var appMgr = FindType("AppManager");
            MethodInfo setTitle = null;
            if (appMgr != null)
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
                    t = asm.GetType("SeqChapterAutoSeal", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterAutoSeal))
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
                    t = asm.GetType("SeqChapterAutoSeal", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterAutoSeal))
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

    /// <summary>
    /// 非 VIP Config 自动战斗人物行动入口。成功下发封印则返回 true（跳过原逻辑）。
    /// 仅队长本机主号回合烧队长背包；队员/非主号回合直接 false 走原逻辑。
    /// </summary>
    public static bool TryPlayerAutoSeal()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            var currentUid = GetStaticString("BattleDataHolder", "CurrentAccount");
            if (string.IsNullOrEmpty(currentUid))
            {
                return false;
            }

            // 只烧本机主号（队长客户端上的自己）背包；队员回合不碰
            var captainUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
            if (string.IsNullOrEmpty(captainUid)
                || !string.Equals(currentUid, captainUid, StringComparison.Ordinal))
            {
                return false;
            }

            // 组队时仅队长（队序 0）烧卡；队序解析失败(>=90)时不误拦本机主号
            var slot = GetPartySlot(captainUid);
            if (slot != 0 && slot < 90)
            {
                return false;
            }

            var battleMgr = GetManagerInstance("BattleManager");
            if (battleMgr == null)
            {
                return false;
            }

            var playerIndex = Convert.ToInt32(GetMember(battleMgr, "PlayerIndex") ?? -1);
            var roleDic = GetStaticField("BattleRoleContainer", "BattleRoleDic") as IDictionary;
            if (roleDic == null || !roleDic.Contains(playerIndex))
            {
                return false;
            }

            var playerRole = roleDic[playerIndex];
            if (playerRole == null || Convert.ToBoolean(GetMember(playerRole, "IsDead") ?? false))
            {
                return false;
            }

            // 明确从队长背包找卡
            if (!TryFindSealCard(captainUid, out var itemIndex, out _))
            {
                return false;
            }

            if (!TryFindAliveEnemy(out var targetIndex))
            {
                return false;
            }

            // 封印卡 Type≥23：I|绝对格|目标（勿对 Type<23 装备发 I|，会空耗行动）。
            var cmd = "I|" + itemIndex.ToString("X") + "|" + targetIndex.ToString("X");

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
            TrySetPlayerActionMagic(battleMgr, captainUid, true);
            send.Invoke(battleMgr, new object[] { cmd });
            return true;
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
            // 退战钩失败时战斗内烧卡仍可用
        }
    }

    private static void OnBattleExited()
    {
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
            var slot = GetPartySlot(mainUid);
            if (slot != 0 && slot < 90)
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
                    RunExitSealCheck(uid);
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
            thread.Name = "SeqChapterAutoSeal.ExitPipeline";
            thread.Start();
        }
        catch
        {
            Interlocked.Exchange(ref _exitPipelineRunning, 0);
        }
    }

    /// <summary>
    /// 退战：Tip 队长背包封印卡剩余张数；为 0 且仍在自动遇敌则「停止挂机」。
    /// </summary>
    private static void RunExitSealCheck(string uid)
    {
        if (!IsPipelineActive() || string.IsNullOrEmpty(uid))
        {
            return;
        }

        var remain = CountSealCardsInBag(uid);
        Tip("封印卡剩余 " + remain + " 张");

        if (remain > 0)
        {
            return;
        }

        if (!IsEncounterActive(uid))
        {
            return;
        }

        try
        {
            TrySendAutoBattle("停止挂机", uid);
            Tip("封印卡已用尽，已停止自动遇敌");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>背包封印卡总张数（各格 Pile 之和；判定与战斗用卡一致）。</summary>
    private static int CountSealCardsInBag(string uid)
    {
        var total = 0;
        try
        {
            var holder = FindType("PlayerDataHolder");
            var getItems = holder?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var list = getItems?.Invoke(null, new object[] { uid }) as IList;
            if (list == null)
            {
                return 0;
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

            for (var i = 8; i < list.Count; i++)
            {
                var item = list[i];
                if (item == null || Convert.ToInt32(GetMember(item, "useFlag") ?? 0) != 1)
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
                              || (!string.IsNullOrEmpty(typeName)
                                  && typeName.IndexOf("封印", StringComparison.Ordinal) >= 0);
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

                var pile = Convert.ToInt32(GetMember(data, "Pile") ?? 0);
                total += pile > 0 ? pile : 1;
            }
        }
        catch
        {
            return total;
        }

        return total;
    }

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

    private static bool TryFindSealCard(string uid, out int itemIndex, out string itemName)
    {
        itemIndex = -1;
        itemName = "";

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

        // 对齐游戏 OnUsingItem：仅 Type≥23 走 I| 扔道具。
        // 封印卡：PROTO_ITEM_FLAG_IS_SEAL(0x100)，或名称含「封印」。
        // 切勿把 Type4–6 装备当封印卡（旧逻辑会误对武器发 I| →「什么都没发生」）。
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

        for (var i = 8; i < list.Count; i++)
        {
            var item = list[i];
            if (item == null)
            {
                continue;
            }

            var useFlag = Convert.ToInt32(GetMember(item, "useFlag") ?? 0);
            if (useFlag != 1)
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
            var isSeal = ((flg & SealFlagMask) != 0) || nameHit;
            if (!isSeal)
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
                    // CanUse 反射失败则仍尝试，由服务器校验
                }
            }

            // 游戏发令用 data.Index（背包绝对格）。
            var rawIndex = Convert.ToInt32(GetMember(data, "Index") ?? i);
            if (rawIndex < 8)
            {
                rawIndex = i;
            }

            itemIndex = rawIndex;
            itemName = string.IsNullOrEmpty(name) ? ("#" + rawIndex) : name;
            return true;
        }

        return false;
    }

    private static bool TryFindAliveEnemy(out int targetIndex)
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

            targetIndex = idx;
            return true;
        }

        return false;
    }

    private static void TrySetPlayerActionMagic(object battleMgr, string uid, bool value)
    {
        try
        {
            var pam = GetMember(battleMgr, "PlayerActionMagics") as IDictionary;
            if (pam == null)
            {
                return;
            }

            pam[uid] = value;
        }
        catch
        {
            // ignore
        }
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
