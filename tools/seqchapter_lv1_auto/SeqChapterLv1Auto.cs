using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

/// <summary>
/// 遇1级怪自动：封印 / 技能1 / 防御。
/// 部署 hotfixdata/SeqChapterLv1Auto.dll.bytes
/// 场上有存活 LevelOneFlag 敌时：
///   队序0 扔封印卡（无卡则防御）；
///   队序1 放技能栏第1个（Config[0] / Magic 槽0）；失败则防御；
///   其余人物防御 G；宠物防御（SkillId=74 → W|slot|petIndex，否则 W|FF|FF）。
/// 无 LevelOneFlag 存活敌 → return false 走原版自动。
/// 开启时强制关掉 LevelOnePetStop（遇1级不停自动）。
/// Pause 加载；钩 AutoFight_Player/Player2/Pet 与 DoVipPlayer/Pet。
/// 默认 PipelineEnabled=false；百科 Tip 或 SetEnabled 开关。与九动DLL/烧卡/抓宠/桥接互斥。
/// </summary>
public static class SeqChapterLv1Auto
{
    public const string AssetPath = "hotfixdata/SeqChapterLv1Auto.dll.bytes";
    public const string TypeName = "SeqChapterLv1Auto";

    public static volatile bool PipelineEnabled = false;

    private const int SealFlagMask = 0x100;
    private const int PetDefendSkillId = 74;

    private static bool _bootstrapped;
    private static int _titleRefresh;

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
        if (IsPipelineActive())
        {
            SuppressLevelOneStop();
            RefreshWindowTitle();
        }
    }

    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetEnabled(enable);
        return enable;
    }

    public static void SetEnabled(bool enable)
    {
        Bootstrap();
        SetPipelineEnabledAllCopies(enable);
        if (enable)
        {
            SuppressLevelOneStop();
        }

        RefreshWindowTitle();
    }

    /// <summary>钩 AutoFight_PlayerAction / DoVipPlayerAutoFight。</summary>
    public static bool TryPlayerLv1Auto()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            SuppressLevelOneStop();
            if (!TryFindLevelOneEnemy(out var targetIndex))
            {
                return false;
            }

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

            // 无宠时人物 2动：一律防御
            if (IsPlayerSecondAction(battleMgr))
            {
                return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
            }

            var partySlot = GetPartySlot(uid);
            if (partySlot == 0)
            {
                if (TryFindSealCard(uid, out var itemIndex, out _))
                {
                    return SendBattleCmd(
                        battleMgr,
                        uid,
                        "I|" + itemIndex.ToString("X") + "|" + targetIndex.ToString("X"),
                        setMagic: true);
                }

                return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
            }

            if (partySlot == 1)
            {
                if (TryResolveSkillOne(uid, battleMgr, out var skillIndex, out var techIndex))
                {
                    return SendBattleCmd(
                        battleMgr,
                        uid,
                        "S|" + skillIndex.ToString("X") + "|" + techIndex.ToString("X") + "|"
                        + targetIndex.ToString("X"),
                        setMagic: true);
                }

                return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
            }

            return SendBattleCmd(battleMgr, uid, "G", setMagic: false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>钩 AutoFight_PlayerAction2（无宠 2动）。</summary>
    public static bool TryPlayerLv1Auto2()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            SuppressLevelOneStop();
            if (!TryFindLevelOneEnemy(out _))
            {
                return false;
            }

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

    /// <summary>钩 AutoFight_PetAction / DoVipPetAutoFight。</summary>
    public static bool TryPetLv1Auto()
    {
        if (!IsPipelineActive())
        {
            return false;
        }

        try
        {
            SuppressLevelOneStop();
            if (!TryFindLevelOneEnemy(out _))
            {
                return false;
            }

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
                cmd = "W|FF|FF";
            }

            return SendBattleCmd(battleMgr, uid, cmd, setMagic: false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启时忽略「遇1级停自动」。</summary>
    private static void SuppressLevelOneStop()
    {
        try
        {
            var bm = GetManagerInstance("BattleManager");
            if (bm != null)
            {
                SetMember(bm, "LevelOnePetStop", false);
            }

            var prefs = FindType("UnityEngine.PlayerPrefs");
            var setInt = prefs?.GetMethod(
                "SetInt",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(int) },
                null);
            setInt?.Invoke(null, new object[] { "StopLevelOne", 0 });
        }
        catch
        {
            // ignore
        }
    }

    private static void RefreshWindowTitle()
    {
        if (Interlocked.Exchange(ref _titleRefresh, 1) == 1)
        {
            // allow reentry after done
        }

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
                title += " ★遇1级自动★";
            }

            var app = FindType("UnityEngine.Application");
            var setTitle = app?.GetProperty(
                "title",
                BindingFlags.Public | BindingFlags.Static);
            if (setTitle != null && setTitle.CanWrite)
            {
                setTitle.SetValue(null, title, null);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _titleRefresh, 0);
        }
    }

    private static string GetUnityProductName()
    {
        try
        {
            var app = FindType("UnityEngine.Application");
            var p = app?.GetProperty("productName", BindingFlags.Public | BindingFlags.Static);
            return Convert.ToString(p?.GetValue(null, null) ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsPlayerSecondAction(object battleMgr)
    {
        try
        {
            var flag = GetMember(battleMgr, "FightProcessFlag");
            if (flag == null)
            {
                return false;
            }

            return (Convert.ToInt32(flag) & 1) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 队序：优先 PlayerDataHolder.teamData 中 UseFlag==1 的顺序（与进战 AcountList 一致）；
    /// 再尝试 AcountList.IndexOf(uid)。
    /// </summary>
    private static int GetPartySlot(string uid)
    {
        try
        {
            var teamData = GetStaticField("PlayerDataHolder", "teamData") as Array;
            if (teamData != null && teamData.Length > 0)
            {
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

                if (any)
                {
                    return 99;
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var list = GetStaticMember("BattleDataHolder", "AcountList") as IList;
            if (list != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(Convert.ToString(list[i]), uid, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return 0;
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
            if (roleData == null)
            {
                continue;
            }

            if (!Convert.ToBoolean(GetMember(roleData, "LevelOneFlag") ?? false))
            {
                continue;
            }

            targetIndex = idx;
            return true;
        }

        return false;
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

            var defendSlot = -1;
            for (var i = 0; i < skills.Count; i++)
            {
                var sk = skills[i];
                if (sk == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(sk, "SkillId") ?? 0) == PetDefendSkillId)
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
            return false;
        });
        itemIndex = foundIndex;
        itemName = foundName;
        return found;
    }

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

            var pile = 1;
            try
            {
                pile = Convert.ToInt32(GetMember(data, "Pile") ?? 1);
            }
            catch
            {
                pile = 1;
            }

            foundAny = true;
            if (!onFound(rawIndex, string.IsNullOrEmpty(name) ? ("#" + rawIndex) : name, pile))
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
            try
            {
                var pam = GetMember(battleMgr, "PlayerActionMagics") as IDictionary;
                if (pam != null)
                {
                    pam[uid] = true;
                }
            }
            catch
            {
                // ignore
            }
        }

        send.Invoke(battleMgr, new object[] { cmd });
        return true;
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

    private static void SetPipelineEnabledAllCopies(bool enable)
    {
        PipelineEnabled = enable;
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
                    // ignore
                }

                if (t == null)
                {
                    continue;
                }

                var f = t.GetField(
                    "PipelineEnabled",
                    BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.FieldType == typeof(bool))
                {
                    f.SetValue(null, enable);
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

                if (t == null)
                {
                    continue;
                }

                var f = t.GetField(
                    "PipelineEnabled",
                    BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.FieldType == typeof(bool) && Convert.ToBoolean(f.GetValue(null)))
                {
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
            try
            {
                var instProp = cur.GetProperty("Instance", flags);
                var inst = instProp?.GetValue(null, null);
                if (inst != null)
                {
                    return inst;
                }
            }
            catch
            {
                // try next
            }

            try
            {
                var getter = cur.GetMethod("get_Instance", flags, null, Type.EmptyTypes, null);
                var inst = getter?.Invoke(null, null);
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

        return null;
    }

    private static Type FindType(string typeName)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(asm.GetName().Name, "hotfix", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = asm.GetType(typeName, false, false)
                                ?? asm.GetType("Hotfix." + typeName, false, false);
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
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            return Type.GetType(typeName, false)
                   ?? Type.GetType(typeName + ", hotfix", false);
        }
        catch
        {
            return null;
        }
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

            t.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(obj, value);
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
        return t?.GetField(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            ?.GetValue(null);
    }

    private static string GetStaticString(string typeName, string name)
    {
        return Convert.ToString(GetStaticMember(typeName, name) ?? "") ?? "";
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
