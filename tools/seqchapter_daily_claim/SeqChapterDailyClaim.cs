using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 日常 DLL。部署为 hotfixdata/SeqChapterDailyClaim.dll.bytes
/// 侧栏分享 OnShareClick：日常流水线（主线程 Timer，不挂钩 NetManager，避免闪退）。
/// 不占用 OnApplicationPause / 百科，可与九动/抓宠/烧卡/加速并存。
/// </summary>
public static class SeqChapterDailyClaim
{
    public const string AssetPath = "hotfixdata/SeqChapterDailyClaim.dll.bytes";

    private const float StepSec = 0.4f;
    private const int WaitTicksMax = 12; // 约 4.8s
    private const int MaxUsePerSlot = 99;

    private static bool _bootstrapped;
    private static string _statusPath;
    private static bool _pipelineRunning;
    private static object _timer;
    private static int _state;
    private static int _waitTicks;
    private static List<string> _uids;
    private static int _uidIndex;
    private static int _monthTitleId;
    private static int _onlineTitleId;
    private static List<object> _onlineClaimable;
    private static int _onlineClaimIndex;
    private static int _useUidIndex;
    private static int _useSlot;
    private static int _useAttempt;
    private static int _monthClaims;
    private static int _onlineClaims;
    private static int _itemUses;
    private static string _expectInfoType;
    private static string _expectUid;

    private static readonly HashSet<int> UseItemIds = new HashSet<int>
    {
        661313, 661314, 880338, 880339, 920063,
        661100, 661140, 920019, 920047,
        661091, 661141, 661142, 661143, 661144, 661145, 661146, 661147, 661148, 661149, 661150,
        661366, 661367, 661368, 661552, 880188, 880253, 880254, 880255, 883140, 883141,
        661105, 661542, 880407, 883260,
        661106, 661543, 880408, 883261,
        920050, 1005736,
        661318, 661319, 661320, 661321, 661322, 661323,
        661330, 661331, 661332, 661333, 661334, 661335, 661538,
    };

    // states
    private const int StSendList = 1;
    private const int StWaitList = 2;
    private const int StSendMonthInfo = 3;
    private const int StWaitMonthInfo = 4;
    private const int StClaimMonth = 5;
    private const int StSendOnlineInfo = 6;
    private const int StWaitOnlineInfo = 7;
    private const int StClaimOnline = 8;
    private const int StNextUid = 9;
    private const int StUsePrep = 10;
    private const int StUseTick = 11;
    private const int StDone = 12;

    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        try
        {
            EnsureStatusPath();
            WriteStatus("mounted", "daily_share_timer");
        }
        catch (Exception ex)
        {
            try
            {
                WriteStatus("boot_error", ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // ignore
            }
        }
    }


    /// <summary>分享：主线程启动日常。true=已开始；false=进行中。</summary>
    public static bool OnShareClick()
    {
        Bootstrap();
        if (_pipelineRunning)
        {
            return false;
        }

        _uids = CollectUids();
        if (_uids == null || _uids.Count == 0)
        {
            Tip("日常：未找到角色");
            return true;
        }

        _pipelineRunning = true;
        _monthClaims = 0;
        _onlineClaims = 0;
        _itemUses = 0;
        _uidIndex = 0;
        _state = StSendList;
        _waitTicks = 0;
        _onlineClaimable = null;
        _onlineClaimIndex = 0;
        Tip("日常：开始领取/使用…");
        StartDailyTimer();
        return true;
    }


    private static void StartDailyTimer()
    {
        StopDailyTimer();
        try
        {
            var timerType = FindType("Timer");
            MethodInfo create = null;
            foreach (var m in timerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Create")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 3
                    && ps[0].ParameterType.Name == "Action"
                    && ps[1].ParameterType == typeof(float)
                    && ps[2].ParameterType == typeof(int))
                {
                    create = m;
                    break;
                }
            }

            if (create == null)
            {
                FinishDaily("日常：无 Timer，已中止");
                return;
            }

            var tick = (Action)DailyTick;
            var psAll = create.GetParameters();
            object[] args;
            if (psAll.Length >= 4)
            {
                args = new object[] { tick, StepSec, -1, true };
                if (psAll.Length > 4)
                {
                    var more = new object[psAll.Length];
                    Array.Copy(args, more, 4);
                    for (var i = 4; i < psAll.Length; i++)
                    {
                        more[i] = psAll[i].HasDefaultValue ? psAll[i].DefaultValue : null;
                    }

                    args = more;
                }
            }
            else
            {
                args = new object[] { tick, StepSec, -1 };
            }

            _timer = create.Invoke(null, args);
            var start = _timer?.GetType().GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            start?.Invoke(_timer, null);
        }
        catch (Exception ex)
        {
            FinishDaily("日常：Timer 失败 " + ex.GetType().Name);
        }
    }

    private static void StopDailyTimer()
    {
        if (_timer == null)
        {
            return;
        }

        try
        {
            var stop = _timer.GetType().GetMethod(
                "Stop",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            stop?.Invoke(_timer, null);
        }
        catch
        {
            // ignore
        }

        _timer = null;
    }

    private static void DailyTick()
    {
        try
        {
            StepDaily();
        }
        catch (Exception ex)
        {
            WriteStatus("daily_tick_err", ex.GetType().Name + ": " + ex.Message);
            FinishDaily("日常异常：" + ex.GetType().Name);
        }
    }

    private static void StepDaily()
    {
        switch (_state)
        {
            case StSendList:
            {
                if (_uidIndex >= _uids.Count)
                {
                    _state = StUsePrep;
                    return;
                }

                var uid = _uids[_uidIndex];
                ClearActiveListInfo();
                SendActivity("活动列表", uid, 0, 0);
                _waitTicks = 0;
                _state = StWaitList;
                return;
            }
            case StWaitList:
            {
                _waitTicks++;
                var list = GetActiveListInfo();
                var uid = _uids[_uidIndex];
                if (list != null && TitlesOf(list).Count > 0)
                {
                    var titles = TitlesOf(list);
                    _monthTitleId = FindTitleId(titles, "特权月卡", "超值月卡");
                    _onlineTitleId = FindTitleId(titles, "累计在线奖励", "在线礼包", "在线奖励");
                    _state = StSendMonthInfo;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _state = StNextUid;
                }

                return;
            }
            case StSendMonthInfo:
            {
                if (_monthTitleId < 0)
                {
                    _state = StSendOnlineInfo;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "特权月卡";
                SendActivity("活动信息", uid, _monthTitleId, 0);
                _waitTicks = 0;
                _state = StWaitMonthInfo;
                return;
            }
            case StWaitMonthInfo:
            {
                _waitTicks++;
                var info = FindActivityInfo("特权月卡", _uids[_uidIndex]);
                if (info != null)
                {
                    _state = StClaimMonth;
                    // stash on field via unused: reuse _onlineClaimable as single-item? use static temp
                    _pendingInfo = info;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _state = StSendOnlineInfo;
                }

                return;
            }
            case StClaimMonth:
            {
                var info = _pendingInfo;
                _pendingInfo = null;
                if (info != null)
                {
                    var tqkTime = Convert.ToInt32(GetMember(info, "TqkTime") ?? 0);
                    var tqkbDay = Convert.ToBoolean(GetMember(info, "TqkbDay") ?? true);
                    var activityId = Convert.ToInt32(GetMember(info, "ActivityId") ?? 0);
                    var now = GetServerTime();
                    if (tqkTime > now && !tqkbDay && activityId > 0)
                    {
                        SendActivity("领取每日奖励", _uids[_uidIndex], 0, activityId);
                        _monthClaims++;
                    }
                }

                _state = StSendOnlineInfo;
                return;
            }
            case StSendOnlineInfo:
            {
                if (_onlineTitleId < 0)
                {
                    _state = StNextUid;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "累计在线奖励";
                SendActivity("活动信息", uid, _onlineTitleId, 0);
                _waitTicks = 0;
                _state = StWaitOnlineInfo;
                return;
            }
            case StWaitOnlineInfo:
            {
                _waitTicks++;
                var info = FindActivityInfo("累计在线奖励", _uids[_uidIndex]);
                if (info != null)
                {
                    _pendingInfo = info;
                    _onlineClaimable = CollectClaimableTiers(info);
                    _onlineClaimIndex = 0;
                    _state = StClaimOnline;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _state = StNextUid;
                }

                return;
            }
            case StClaimOnline:
            {
                if (_onlineClaimable == null || _onlineClaimIndex >= _onlineClaimable.Count)
                {
                    _pendingInfo = null;
                    _state = StNextUid;
                    return;
                }

                var activityId = Convert.ToInt32(GetMember(_pendingInfo, "ActivityId") ?? 0);
                var tier = _onlineClaimable[_onlineClaimIndex++];
                var tierId = Convert.ToInt32(GetMember(tier, "Id") ?? 0);
                if (activityId > 0 && tierId > 0)
                {
                    SendActivity("累计在线奖励领取", _uids[_uidIndex], tierId, activityId);
                    _onlineClaims++;
                }

                // stay in StClaimOnline for next tier next tick
                return;
            }
            case StNextUid:
            {
                _uidIndex++;
                _state = StSendList;
                return;
            }
            case StUsePrep:
            {
                _useUidIndex = 0;
                _useSlot = 8;
                _useAttempt = 0;
                _state = StUseTick;
                return;
            }
            case StUseTick:
            {
                if (_useUidIndex >= _uids.Count)
                {
                    _state = StDone;
                    return;
                }

                if (TryUseOne())
                {
                    _itemUses++;
                }

                return;
            }
            case StDone:
            {
                FinishDaily(string.Format(
                    "日常完成：月卡{0} · 在线{1}档 · 用道具{2}次",
                    _monthClaims,
                    _onlineClaims,
                    _itemUses));
                return;
            }
        }
    }

    private static object _pendingInfo;

    private static bool TryUseOne()
    {
        while (_useUidIndex < _uids.Count)
        {
            var uid = _uids[_useUidIndex];
            var holder = FindType("PlayerDataHolder");
            var getItems = holder?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var getPlayer = holder?.GetMethod(
                "GetPlayerFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            var gridNum = player != null ? Convert.ToInt32(GetMember(player, "itemGridNum") ?? 0) : 0;
            if (gridNum <= 8)
            {
                _useUidIndex++;
                _useSlot = 8;
                _useAttempt = 0;
                continue;
            }

            while (_useSlot < gridNum)
            {
                var items = getItems?.Invoke(null, new object[] { uid }) as IList;
                if (items == null || _useSlot >= items.Count)
                {
                    _useSlot++;
                    _useAttempt = 0;
                    continue;
                }

                var slot = items[_useSlot];
                if (slot == null || Convert.ToInt32(GetMember(slot, "useFlag") ?? 0) != 1)
                {
                    _useSlot++;
                    _useAttempt = 0;
                    continue;
                }

                var data = GetMember(slot, "data");
                var itemId = data != null ? Convert.ToInt32(GetMember(data, "Id") ?? 0) : 0;
                if (!UseItemIds.Contains(itemId))
                {
                    _useSlot++;
                    _useAttempt = 0;
                    continue;
                }

                if (_useAttempt >= MaxUsePerSlot)
                {
                    _useSlot++;
                    _useAttempt = 0;
                    continue;
                }

                var slotIndex = Convert.ToInt32(GetMember(data, "Index") ?? _useSlot);
                if (!SendUseItem(slotIndex, uid))
                {
                    _useSlot++;
                    _useAttempt = 0;
                    continue;
                }

                _useAttempt++;
                return true;
            }

            _useUidIndex++;
            _useSlot = 8;
            _useAttempt = 0;
        }

        _state = StDone;
        return false;
    }

    private static void FinishDaily(string tip)
    {
        StopDailyTimer();
        _pipelineRunning = false;
        _state = 0;
        Tip(tip);
        WriteStatus("daily_done", tip);
    }

    private static List<object> CollectClaimableTiers(object info)
    {
        var result = new List<object>();
        var listInfo = GetMember(info, "ListInfo") as IEnumerable;
        if (listInfo == null)
        {
            return result;
        }

        foreach (var tier in listInfo)
        {
            if (tier == null)
            {
                continue;
            }

            if (Convert.ToInt32(GetMember(tier, "Status") ?? 0) == 1)
            {
                result.Add(tier);
            }
        }

        return result;
    }

    private static object FindActivityInfo(string type, string uid)
    {
        // 1) ActivityPanel.m_Info
        try
        {
            var panel = GetUiPanel("ActivityPanel");
            var info = GetMember(panel, "m_Info");
            if (InfoMatches(info, type, uid))
            {
                return info;
            }
        }
        catch
        {
            // ignore
        }

        // 2) MonthCardChildPanel.m_ActivityInfo
        if (type.IndexOf("特权", StringComparison.Ordinal) >= 0 || type.IndexOf("月卡", StringComparison.Ordinal) >= 0)
        {
            try
            {
                var child = GetUiChildPanel("MonthCardChildPanel");
                var info = GetMember(child, "m_ActivityInfo");
                if (InfoMatches(info, type, uid))
                {
                    return info;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static bool InfoMatches(object info, string type, string uid)
    {
        if (info == null)
        {
            return false;
        }

        var t = Convert.ToString(GetMember(info, "Type") ?? "") ?? "";
        if (!string.Equals(t, type, StringComparison.Ordinal))
        {
            return false;
        }

        var k = Convert.ToString(GetMember(info, "KUid") ?? "") ?? "";
        return string.IsNullOrEmpty(uid) || string.Equals(k, uid, StringComparison.Ordinal);
    }

    private static object GetUiPanel(string typeName)
    {
        var ui = FindType("UIManager");
        var panelType = FindType(typeName);
        if (ui == null || panelType == null)
        {
            return null;
        }

        foreach (var m in ui.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
        {
            if (m.Name != "GetUIPanel" || !m.IsGenericMethodDefinition)
            {
                continue;
            }

            try
            {
                return m.MakeGenericMethod(panelType).Invoke(null, null);
            }
            catch
            {
                // next
            }
        }

        return null;
    }

    private static object GetUiChildPanel(string typeName)
    {
        var ui = FindType("UIManager");
        var panelType = FindType(typeName);
        if (ui == null || panelType == null)
        {
            return null;
        }

        foreach (var m in ui.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
        {
            if (m.Name != "GetChildPanel" || !m.IsGenericMethodDefinition)
            {
                continue;
            }

            try
            {
                return m.MakeGenericMethod(panelType).Invoke(null, null);
            }
            catch
            {
                // next
            }
        }

        return null;
    }

    private static object GetActiveListInfo()
    {
        var mgr = GetManagerInstance("ActivityManager");
        return GetMember(mgr, "activeListInfo");
    }

    private static void ClearActiveListInfo()
    {
        try
        {
            var mgr = GetManagerInstance("ActivityManager");
            SetMember(mgr, "activeListInfo", null);
        }
        catch
        {
            // ignore
        }
    }

    private static List<object> TitlesOf(object listProto)
    {
        var result = new List<object>();
        var titles = GetMember(listProto, "Titles") as IEnumerable;
        if (titles == null)
        {
            return result;
        }

        foreach (var t in titles)
        {
            if (t != null)
            {
                result.Add(t);
            }
        }

        return result;
    }

    private static int FindTitleId(List<object> titles, params string[] keys)
    {
        foreach (var t in titles)
        {
            var name = Convert.ToString(GetMember(t, "Name") ?? "") ?? "";
            foreach (var key in keys)
            {
                if (name.IndexOf(key, StringComparison.Ordinal) >= 0)
                {
                    return Convert.ToInt32(GetMember(t, "Id") ?? -1);
                }
            }
        }

        return -1;
    }

    private static void SendActivity(string type, string uid, int id, int activityId)
    {
        var mgr = GetManagerInstance("ActivityManager");
        if (mgr == null)
        {
            return;
        }

        MethodInfo send = null;
        foreach (var m in mgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendActivity")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length >= 2
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(string))
            {
                send = m;
                break;
            }
        }

        if (send == null)
        {
            return;
        }

        var psAll = send.GetParameters();
        var args = new object[psAll.Length];
        args[0] = type;
        args[1] = uid;
        for (var i = 2; i < psAll.Length; i++)
        {
            if (i == 2)
            {
                args[i] = id;
            }
            else if (i == 3)
            {
                args[i] = activityId;
            }
            else if (psAll[i].HasDefaultValue)
            {
                args[i] = psAll[i].DefaultValue;
            }
            else if (psAll[i].ParameterType == typeof(int))
            {
                args[i] = 0;
            }
            else if (psAll[i].ParameterType == typeof(string))
            {
                args[i] = "";
            }
            else
            {
                args[i] = null;
            }
        }

        send.Invoke(mgr, args);
    }

    private static bool SendUseItem(int haveItemIndex, string uid)
    {
        try
        {
            var itemMgr = GetManagerInstance("ItemManager");
            if (itemMgr == null)
            {
                return false;
            }

            var loc = GetStaticMember("PlayerDataHolder", "location");
            var x = loc != null ? Convert.ToInt32(GetMember(loc, "x") ?? 0) : 0;
            var y = loc != null ? Convert.ToInt32(GetMember(loc, "y") ?? 0) : 0;

            MethodInfo send = null;
            foreach (var m in itemMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendUseItem")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 4
                    && ps[0].ParameterType == typeof(int)
                    && ps[1].ParameterType == typeof(int)
                    && ps[2].ParameterType == typeof(int)
                    && ps[3].ParameterType == typeof(string))
                {
                    send = m;
                    break;
                }
            }

            if (send == null)
            {
                return false;
            }

            var psAll = send.GetParameters();
            var args = new object[psAll.Length];
            args[0] = x;
            args[1] = y;
            args[2] = haveItemIndex;
            args[3] = uid;
            for (var i = 4; i < psAll.Length; i++)
            {
                if (psAll[i].HasDefaultValue)
                {
                    args[i] = psAll[i].DefaultValue;
                }
                else if (psAll[i].ParameterType == typeof(int))
                {
                    args[i] = i == 5 ? -1 : (i == 6 ? 1 : 0);
                }
                else
                {
                    args[i] = null;
                }
            }

            send.Invoke(itemMgr, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> CollectUids()
    {
        var result = new List<string>();
        try
        {
            var holder = FindType("PlayerDataHolder");
            var getAll = holder?.GetMethod(
                "GetAllPlayers",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var dict = getAll?.Invoke(null, null) as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    var uid = Convert.ToString(kv.Key);
                    if (!string.IsNullOrEmpty(uid))
                    {
                        result.Add(uid);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (result.Count == 0)
        {
            var main = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "");
            if (string.IsNullOrEmpty(main))
            {
                var pd = GetStaticMember("PlayerDataHolder", "playerData");
                main = Convert.ToString(GetMember(pd, "Uid") ?? GetMember(pd, "uid") ?? "");
            }

            if (!string.IsNullOrEmpty(main))
            {
                result.Add(main);
            }
        }

        return result;
    }

    private static int GetServerTime()
    {
        try
        {
            var tm = FindType("TimeManager");
            var m = tm?.GetMethod(
                "GetServerTime",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (m != null)
            {
                return Convert.ToInt32(m.Invoke(null, null));
            }
        }
        catch
        {
            // ignore
        }

        return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private static void Tip(string msg)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            MethodInfo tip = null;
            MethodInfo oneArg = null;
            foreach (var m in notify.GetType().GetMethods(
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
                    tip = m;
                    break;
                }

                if (ps.Length == 1 && ps[0].ParameterType.FullName == "System.String")
                {
                    oneArg = m;
                }
            }

            tip ??= oneArg;
            if (tip == null)
            {
                return;
            }

            if (tip.GetParameters().Length == 2)
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
                // next
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
                // next
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
                   ?? Type.GetType(typeName + ", hotfix", false);
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

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.GetType("BattleDataHolder") != null)
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
            return p.GetValue(obj, null);
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

        var f = t.GetField(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        return f?.GetValue(null);
    }

    private static void EnsureStatusPath()
    {
        if (!string.IsNullOrEmpty(_statusPath))
        {
            return;
        }

        _statusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seqchapter_helper",
            "daily_claim.status");
        Directory.CreateDirectory(Path.GetDirectoryName(_statusPath)!);
    }

    private static void WriteStatus(string key, string value)
    {
        try
        {
            EnsureStatusPath();
            File.WriteAllText(_statusPath, key + "=" + value + "\n" + DateTime.Now.ToString("o"));
        }
        catch
        {
            // ignore
        }
    }
}
