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
    private const int MaxUsePerSlot = 20;
    /// <summary>整条流水线最长 tick，超时强制结束（约 3 分钟）。</summary>
    private const int MaxPipelineTicks = 450;
    /// <summary>同格堆积数不变时最多连用次数，防止用不掉死循环。</summary>
    private const int MaxStaleUsesPerSlot = 3;

    private static bool _bootstrapped;
    private static string _statusPath;
    private static bool _pipelineRunning;
    private static object _timer;
    private static int _state;
    private static int _waitTicks;
    private static List<string> _uids;
    private static int _uidIndex;
    private static int _signTitleId;
    private static int _monthTitleId;
    private static int _onlineTitleId;
    private static List<object> _onlineClaimable;
    private static int _onlineClaimIndex;
    private static int _useUidIndex;
    private static int _useSlot;
    private static int _useAttempt;
    private static int _signClaims;
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
        // 工时小闹钟（含 3小时 / 6小时）— 使用后常弹 MessageBox 二次确认
        661355, 661536, 920043,
    };

    /// <summary>使用后需点 MessageBox 确定才会真正消耗的道具。</summary>
    private static readonly HashSet<int> ConfirmUseItemIds = new HashSet<int>
    {
        661355, 661536, 920043,
    };

    private static bool _awaitingUseConfirm;
    private static int _confirmWaitTicks;
    private static int _pipelineTicks;
    private static int _skipConfirmTicks;
    private static int _staleUseCount;
    private static int _stalePile;
    private static int _staleItemId;
    private static int _staleSlot;

    // states
    private const int StSendList = 1;
    private const int StWaitList = 2;
    private const int StSendSignInfo = 13;
    private const int StWaitSignInfo = 14;
    private const int StClaimSign = 15;
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


    /// <summary>
    /// 分享：切换日常流水线。true=已开始（Tip 开启）；false=已停止（Tip 关闭）。
    /// 进行中再点一次会强制中止（含 Timer / 用道具确认等待），避免关不掉。
    /// </summary>
    public static bool OnShareClick()
    {
        Bootstrap();
        if (_pipelineRunning || IsAnyCopyPipelineRunning())
        {
            AbortDailyAllCopies();
            return false;
        }

        _uids = CollectUids();
        if (_uids == null || _uids.Count == 0)
        {
            Tip("日常：未找到角色");
            // 返回 true 会 Tip「已开始」，这里改为 false 并自行提示
            return false;
        }

        _pipelineRunning = true;
        SyncPipelineRunningAllCopies(true);
        _signClaims = 0;
        _monthClaims = 0;
        _onlineClaims = 0;
        _itemUses = 0;
        _uidIndex = 0;
        _state = StSendList;
        _waitTicks = 0;
        _onlineClaimable = null;
        _onlineClaimIndex = 0;
        _awaitingUseConfirm = false;
        _confirmWaitTicks = 0;
        _pipelineTicks = 0;
        _skipConfirmTicks = 0;
        _staleUseCount = 0;
        _stalePile = -1;
        _staleItemId = 0;
        _staleSlot = -1;
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
        if (!_pipelineRunning)
        {
            StopDailyTimer();
            return;
        }

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
        _pipelineTicks++;
        if (_pipelineTicks > MaxPipelineTicks)
        {
            FinishDaily(string.Format(
                "日常超时结束：签到{0} · 月卡{1} · 在线{2}档 · 用道具{3}次",
                _signClaims,
                _monthClaims,
                _onlineClaims,
                _itemUses));
            return;
        }

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
                    _signTitleId = FindTitleId(titles, "周期14日签到", "每日签到", "签到");
                    _monthTitleId = FindTitleId(titles, "特权月卡", "超值月卡");
                    _onlineTitleId = FindTitleId(titles, "累计在线奖励", "在线礼包", "在线奖励");
                    _state = StSendSignInfo;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _state = StNextUid;
                }

                return;
            }
            case StSendSignInfo:
            {
                if (_signTitleId < 0)
                {
                    _state = StSendMonthInfo;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "周期14日签到";
                SendActivity("活动信息", uid, _signTitleId, 0);
                _waitTicks = 0;
                _state = StWaitSignInfo;
                return;
            }
            case StWaitSignInfo:
            {
                _waitTicks++;
                var info = FindActivityInfo("周期14日签到", _uids[_uidIndex]);
                if (info != null)
                {
                    _pendingInfo = info;
                    _state = StClaimSign;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _state = StSendMonthInfo;
                }

                return;
            }
            case StClaimSign:
            {
                var info = _pendingInfo;
                _pendingInfo = null;
                if (info != null && TryClaimSignIn(info, _uids[_uidIndex]))
                {
                    _signClaims++;
                }

                _state = StSendMonthInfo;
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
                _awaitingUseConfirm = false;
                _confirmWaitTicks = 0;
                _skipConfirmTicks = 0;
                _staleUseCount = 0;
                _stalePile = -1;
                _staleItemId = 0;
                _staleSlot = -1;
                _state = StUseTick;
                return;
            }
            case StUseTick:
            {
                if (_uids == null || _useUidIndex >= _uids.Count)
                {
                    _state = StDone;
                    return;
                }

                // 工时小闹钟等：仅在「刚用完等确认」时点 MessageBox；
                // 禁止每 tick 无条件点弹窗（残留面板会导致死循环点确定）。
                if (_awaitingUseConfirm)
                {
                    if (TryConfirmMessageBox())
                    {
                        _awaitingUseConfirm = false;
                        _confirmWaitTicks = 0;
                        _skipConfirmTicks = 2;
                        return;
                    }

                    _confirmWaitTicks++;
                    if (_confirmWaitTicks >= WaitTicksMax)
                    {
                        _awaitingUseConfirm = false;
                        _confirmWaitTicks = 0;
                        _useSlot++;
                        _useAttempt = 0;
                        _staleUseCount = 0;
                    }

                    return;
                }

                if (_skipConfirmTicks > 0)
                {
                    _skipConfirmTicks--;
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
                    "日常完成：签到{0} · 月卡{1} · 在线{2}档 · 用道具{3}次",
                    _signClaims,
                    _monthClaims,
                    _onlineClaims,
                    _itemUses));
                return;
            }
        }
    }

    private static object _pendingInfo;

    /// <summary>
    /// 周期14日签到：Status=可领天数(1-based)，Month[day-1]=是否已领。
    /// 可领时 SendActivity("每日签到", uid, Status, ActivityId)。
    /// </summary>
    private static bool TryClaimSignIn(object info, string uid)
    {
        try
        {
            var status = Convert.ToInt32(GetMember(info, "Status") ?? 0);
            var activityId = Convert.ToInt32(GetMember(info, "ActivityId") ?? 0);
            if (status <= 0 || activityId <= 0)
            {
                return false;
            }

            var month = GetMember(info, "Month") as IList;
            if (month != null && status - 1 < month.Count)
            {
                if (Convert.ToBoolean(month[status - 1]))
                {
                    return false; // 今日已签
                }
            }

            SendActivity("每日签到", uid, status, activityId);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
                    _staleUseCount = 0;
                    continue;
                }

                if (_useAttempt >= MaxUsePerSlot)
                {
                    _useSlot++;
                    _useAttempt = 0;
                    _staleUseCount = 0;
                    continue;
                }

                var pile = 0;
                try
                {
                    pile = Convert.ToInt32(GetMember(data, "Pile") ?? GetMember(slot, "Pile") ?? 0);
                }
                catch
                {
                    pile = 0;
                }

                // 同一格同一道具堆积数一直不变 → 服务端没用掉，跳过避免死循环
                if (_staleSlot == _useSlot && _staleItemId == itemId && _stalePile == pile && _useAttempt > 0)
                {
                    _staleUseCount++;
                    if (_staleUseCount >= MaxStaleUsesPerSlot)
                    {
                        _useSlot++;
                        _useAttempt = 0;
                        _staleUseCount = 0;
                        _stalePile = -1;
                        continue;
                    }
                }
                else
                {
                    _staleSlot = _useSlot;
                    _staleItemId = itemId;
                    _stalePile = pile;
                    _staleUseCount = 0;
                }

                var slotIndex = Convert.ToInt32(GetMember(data, "Index") ?? _useSlot);
                if (!SendUseItem(slotIndex, uid))
                {
                    _useSlot++;
                    _useAttempt = 0;
                    _staleUseCount = 0;
                    continue;
                }

                _useAttempt++;
                if (ConfirmUseItemIds.Contains(itemId))
                {
                    _awaitingUseConfirm = true;
                    _confirmWaitTicks = 0;
                }

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
        SyncPipelineRunningAllCopies(false);
        _state = 0;
        _awaitingUseConfirm = false;
        _confirmWaitTicks = 0;
        _pipelineTicks = 0;
        _skipConfirmTicks = 0;
        Tip(tip);
        WriteStatus("daily_done", tip);
    }

    /// <summary>强制中止：停 Timer、清状态；并尽量同步其它已 Load 的 DLL 副本。</summary>
    private static void AbortDailyAllCopies()
    {
        try
        {
            StopDailyTimer();
        }
        catch
        {
            // ignore
        }

        _pipelineRunning = false;
        _state = 0;
        _awaitingUseConfirm = false;
        _confirmWaitTicks = 0;
        _pipelineTicks = 0;
        _skipConfirmTicks = 0;
        _uids = null;
        _onlineClaimable = null;
        _pendingInfo = null;
        WriteStatus("daily_aborted", "user_stop");

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType("SeqChapterDailyClaim", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterDailyClaim))
                {
                    continue;
                }

                try
                {
                    var abort = t.GetMethod(
                        "AbortDailyLocal",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (abort != null)
                    {
                        abort.Invoke(null, null);
                        continue;
                    }
                }
                catch
                {
                    // fall through to field clear
                }

                try
                {
                    var f = t.GetField(
                        "_pipelineRunning",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        f.SetValue(null, false);
                    }

                    var stop = t.GetMethod(
                        "StopDailyTimer",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    stop?.Invoke(null, null);
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
    }

    /// <summary>供其它程序集副本反射调用：只清本副本。</summary>
    public static void AbortDailyLocal()
    {
        try
        {
            StopDailyTimer();
        }
        catch
        {
            // ignore
        }

        _pipelineRunning = false;
        _state = 0;
        _awaitingUseConfirm = false;
        _confirmWaitTicks = 0;
        _pipelineTicks = 0;
        _skipConfirmTicks = 0;
        _uids = null;
        _onlineClaimable = null;
        _pendingInfo = null;
    }

    private static void SyncPipelineRunningAllCopies(bool running)
    {
        _pipelineRunning = running;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType("SeqChapterDailyClaim", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterDailyClaim))
                {
                    continue;
                }

                try
                {
                    var f = t.GetField(
                        "_pipelineRunning",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        f.SetValue(null, running);
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
    }

    private static bool IsAnyCopyPipelineRunning()
    {
        if (_pipelineRunning)
        {
            return true;
        }

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType("SeqChapterDailyClaim", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null)
                {
                    continue;
                }

                try
                {
                    var f = t.GetField(
                        "_pipelineRunning",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(bool) && Convert.ToBoolean(f.GetValue(null)))
                    {
                        return true;
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

        return false;
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

        // 3) SingInChildPanel.m_info（周期签到）
        if (type.IndexOf("签到", StringComparison.Ordinal) >= 0)
        {
            try
            {
                var child = GetUiChildPanel("SingInChildPanel");
                var info = GetMember(child, "m_info");
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

    /// <summary>
    /// 点掉 MessageBoxPanel 二次确认（服务端 1049 / 客户端 ShowMessageBox）。
    /// 工时小闹钟用后常见此弹窗；OnSubmit 会 SendMessageBox(btntype=0) 或执行客户端回调。
    /// </summary>
    private static bool TryConfirmMessageBox()
    {
        try
        {
            var panel = GetUiPanel("MessageBoxPanel");
            if (panel == null || !IsUiPanelLikelyOpen(panel))
            {
                return false;
            }

            // 有服务端/客户端内容才点，避免空壳面板误点
            var sever = GetMember(panel, "m_SeverInfo");
            var client = GetMember(panel, "m_ClientInfo");
            var type = Convert.ToString(GetMember(panel, "m_type") ?? "");
            if (sever == null && client == null && string.IsNullOrEmpty(type))
            {
                return false;
            }

            var onSubmit = panel.GetType().GetMethod(
                "OnSubmit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (onSubmit == null)
            {
                return false;
            }

            onSubmit.Invoke(panel, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUiPanelLikelyOpen(object panel)
    {
        try
        {
            // UIPanel 常见：IsShow / isShow / m_IsShow
            foreach (var name in new[] { "IsShow", "isShow", "m_IsShow", "IsOpen", "isOpen" })
            {
                var v = GetMember(panel, name);
                if (v is bool b)
                {
                    return b;
                }
            }

            var goProp = panel.GetType().GetProperty(
                "gameObject",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var go = goProp != null ? goProp.GetValue(panel, null) : GetMember(panel, "gameObject");
            if (go != null)
            {
                var active = go.GetType().GetProperty("activeInHierarchy")
                    ?? go.GetType().GetProperty("activeSelf");
                if (active != null)
                {
                    return Convert.ToBoolean(active.GetValue(go, null));
                }
            }
        }
        catch
        {
            // fall through：看内容字段决定
        }

        return true;
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
