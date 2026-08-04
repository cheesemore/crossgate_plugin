using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 日常 / 新手礼包码 DLL。部署为 hotfixdata/SeqChapterDailyClaim.dll.bytes
/// 侧栏分享 OnShareClick：切页（日常 | 新手礼包码）+ 再点开始（主线程 Timer）。
/// 礼包码协议：ActivityManager.SendActivity("CDKey兑换", uid, id=0, activityId=4, code)
///   （码含 "N_" 时改发 giftCode，与 Com_Cdkey 一致）。
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
    /// <summary>礼包码最多尝试角色数（与五开一致）。</summary>
    private const int MaxGiftUids = 5;
    /// <summary>切页后需在此毫秒内再点分享才开始，否则下次点击切换切页。</summary>
    private const int ShareArmMs = 2000;

    /// <summary>内置默认礼包码（无外部文件时使用）。</summary>
    private static readonly string[] DefaultNewbieGiftCodes =
    {
        "VIP666", "VIP777", "VIP888", "VIP999",
        "MLBB666", "MLBB777", "mlbb521", "mlbb24", "mlbb0803",
    };

    /// <summary>运行时列表：优先读 hotfixdata/seqchapter_gift_codes.txt（一行一个，# 注释）。</summary>
    private static string[] _giftCodes;

    private static bool _bootstrapped;
    private static string _statusPath;
    private static bool _pipelineRunning;
    private static object _timer;
    private static int _state;
    private static int _waitTicks;
    private static List<string> _uids;
    private static string _uidSource = "";
    private static int _uidIndex;
    /// <summary>0=日常领取 1=新手礼包码。</summary>
    private static int _sharePage;
    private static int _shareArmStartTick;
    private static bool _shareArmed;
    private static int _giftUidIndex;
    private static int _giftCodeIndex;
    private static int _giftSent;
    /// <summary>当前流水线是否礼包码页（决定 Trace 前缀）。</summary>
    private static bool _runningGift;
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

    /// <summary>调试 Tip：状态变化必提示；同态每 5 tick（约 2 秒）心跳一次。</summary>
    private static int _tracePrevState = -1;
    private static int _traceSameTicks;
    private const int TraceHeartbeatEvery = 5;


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
    private const int StGiftSend = 20;
    private const int StGiftDone = 21;

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
            WriteStatus("mounted", "daily_share_timer_gift");
            LoadShareOpts();
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

    /// <summary>opts：hotfixdata/seqchapter_share_opts.txt 内 daily=0/1 gift=0/1；缺省都开。</summary>
    private static bool _optDaily = true;
    private static bool _optGift = true;

    private static string[] ActiveGiftCodes
    {
        get
        {
            if (_giftCodes != null && _giftCodes.Length > 0)
            {
                return _giftCodes;
            }

            return DefaultNewbieGiftCodes;
        }
    }

    private static void LoadShareOpts()
    {
        _optDaily = true;
        _optGift = true;
        try
        {
            foreach (var path in HotfixdataFileCandidates("seqchapter_share_opts.txt"))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                if (text.IndexOf("daily=0", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _optDaily = false;
                }

                if (text.IndexOf("gift=0", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _optGift = false;
                }

                WriteStatus("share_opts", path + " daily=" + (_optDaily ? 1 : 0) + " gift=" + (_optGift ? 1 : 0));
                break;
            }
        }
        catch
        {
            // ignore
        }

        if (!_optDaily && !_optGift)
        {
            _optDaily = true;
        }

        if (!_optDaily)
        {
            _sharePage = 1;
        }
        else if (!_optGift)
        {
            _sharePage = 0;
        }

        LoadGiftCodes();
    }

    /// <summary>从 seqchapter_gift_codes.txt 读可编辑礼包码；无文件/空则用内置默认。</summary>
    private static void LoadGiftCodes()
    {
        _giftCodes = null;
        try
        {
            foreach (var path in HotfixdataFileCandidates("seqchapter_gift_codes.txt"))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var lines = File.ReadAllLines(path);
                var list = new List<string>();
                for (var i = 0; i < lines.Length; i++)
                {
                    var s = (lines[i] ?? "").Trim();
                    if (s.Length == 0 || s[0] == '#')
                    {
                        continue;
                    }

                    list.Add(s);
                }

                if (list.Count > 0)
                {
                    _giftCodes = list.ToArray();
                    WriteStatus("gift_codes", path + " n=" + list.Count);
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        WriteStatus("gift_codes", "default n=" + DefaultNewbieGiftCodes.Length);
    }

    private static List<string> HotfixdataFileCandidates(string fileName)
    {
        var list = new List<string>();
        try
        {
            var dataPath = Convert.ToString(
                FindType("UnityEngine.Application")
                    ?.GetProperty("dataPath", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null, null) ?? "") ?? "";
            if (!string.IsNullOrEmpty(dataPath))
            {
                var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                list.Add(Path.Combine(gameRoot, "cg37_Data", "assets", "hotfixdata", fileName));
                list.Add(Path.Combine(gameRoot, "hotfixdata", fileName));
            }
        }
        catch
        {
            // ignore
        }

        list.Add(Path.Combine("cg37_Data", "assets", "hotfixdata", fileName));
        return list;
    }

    /// <summary>测试 UI / 脚本：开/停日常流水线（不经分享切页）。</summary>
    public static bool ToggleDailyFromUi()
    {
        Bootstrap();
        if (_pipelineRunning || IsAnyCopyPipelineRunning())
        {
            AbortDailyAllCopies();
            Tip("日常：已停止");
            return false;
        }

        return StartDailyPipeline();
    }

    /// <summary>测试 UI / 脚本：开/停新手礼包码流水线（读 seqchapter_gift_codes.txt）。</summary>
    public static bool ToggleGiftFromUi()
    {
        Bootstrap();
        if (_pipelineRunning || IsAnyCopyPipelineRunning())
        {
            AbortDailyAllCopies();
            Tip("礼包码：已停止");
            return false;
        }

        return StartGiftPipeline();
    }

    /// <summary>
    /// 分享：切页（日常 / 新手礼包码）→ 限时内再点开始；进行中再点则停止。
    /// 返回值仅兼容加载器；提示一律走 Tip()。
    /// </summary>
    public static bool OnShareClick()
    {
        Bootstrap();
        if (_pipelineRunning || IsAnyCopyPipelineRunning())
        {
            AbortDailyAllCopies();
            Tip("分享：已停止");
            return false;
        }

        var now = Environment.TickCount;
        if (_shareArmed)
        {
            var elapsed = now - _shareArmStartTick;
            if (elapsed >= 0 && elapsed < ShareArmMs)
            {
                var started = StartSharePage(_sharePage);
                _shareArmed = false;
                return started;
            }

            // 超时后再点：切页并重新武装
            ToggleSharePage();
        }
        else if (!_optDaily || !_optGift)
        {
            _sharePage = _optGift && !_optDaily ? 1 : 0;
        }

        _shareArmed = true;
        _shareArmStartTick = now;
        Tip(_sharePage == 0
            ? "切页·日常领取 — 2秒内再点分享开始"
            : "切页·新手礼包码 — 2秒内再点分享开始（最多5角色）");
        return false;
    }

    private static void ToggleSharePage()
    {
        if (_optDaily && _optGift)
        {
            _sharePage = _sharePage == 0 ? 1 : 0;
        }
        else if (_optGift && !_optDaily)
        {
            _sharePage = 1;
        }
        else
        {
            _sharePage = 0;
        }
    }

    private static bool StartSharePage(int page)
    {
        if (page == 1 && _optGift)
        {
            return StartGiftPipeline();
        }

        if (_optDaily)
        {
            return StartDailyPipeline();
        }

        if (_optGift)
        {
            return StartGiftPipeline();
        }

        Tip("分享：日常/礼包码均未启用");
        return false;
    }

    private static bool StartDailyPipeline()
    {
        _uids = CollectUids();
        if (_uids == null || _uids.Count == 0)
        {
            Tip("日常：未找到角色");
            return false;
        }

        _runningGift = false;
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
        _tracePrevState = -1;
        _traceSameTicks = 0;
        Tip(string.Format("日常：开始 角色{0}个（{1}）", _uids.Count, _uidSource));
        StartDailyTimer();
        return true;
    }

    private static bool StartGiftPipeline()
    {
        LoadGiftCodes();
        if (ActiveGiftCodes.Length == 0)
        {
            Tip("礼包码：列表为空（请在补丁 GUI 填写或检查 seqchapter_gift_codes.txt）");
            return false;
        }

        _uids = CollectUids();
        if (_uids == null || _uids.Count == 0)
        {
            Tip("礼包码：未找到角色");
            return false;
        }

        if (_uids.Count > MaxGiftUids)
        {
            _uids = _uids.GetRange(0, MaxGiftUids);
        }

        _runningGift = true;
        _pipelineRunning = true;
        SyncPipelineRunningAllCopies(true);
        _giftUidIndex = 0;
        _giftCodeIndex = 0;
        _giftSent = 0;
        _uidIndex = 0;
        _state = StGiftSend;
        _waitTicks = 0;
        _pipelineTicks = 0;
        _tracePrevState = -1;
        _traceSameTicks = 0;
        Tip(string.Format(
            "新手礼包码：开始 角色{0}个×{1}码（{2}）",
            _uids.Count,
            ActiveGiftCodes.Length,
            _uidSource));
        StartDailyTimer();
        return true;
    }


    private static string StateName(int state)
    {
        switch (state)
        {
            case StSendList: return "发列表";
            case StWaitList: return "等列表";
            case StSendSignInfo: return "发签到信息";
            case StWaitSignInfo: return "等签到信息";
            case StClaimSign: return "领签到";
            case StSendMonthInfo: return "发月卡信息";
            case StWaitMonthInfo: return "等月卡信息";
            case StClaimMonth: return "领月卡";
            case StSendOnlineInfo: return "发在线信息";
            case StWaitOnlineInfo: return "等在线信息";
            case StClaimOnline: return "领在线";
            case StNextUid: return "下一角色";
            case StUsePrep: return "用道具准备";
            case StUseTick: return "用道具";
            case StDone: return "完成";
            case StGiftSend: return "发礼包码";
            case StGiftDone: return "礼包码完成";
            default: return "s" + state;
        }
    }

    /// <summary>短 Tip：日常/礼包[态] t序号 角i/n · 详情</summary>
    private static void TraceTip(string detail)
    {
        var uidN = _uids != null ? _uids.Count : 0;
        var head = _runningGift ? "礼包" : "日常";
        var ang = _runningGift ? _giftUidIndex : _uidIndex;
        var msg = string.Format(
            "{0}[{1}] t{2} 角{3}/{4} · {5}",
            head,
            StateName(_state),
            _pipelineTicks,
            ang,
            uidN,
            detail ?? "");
        Tip(msg);
        try
        {
            WriteStatus("daily_trace", msg);
        }
        catch
        {
            // ignore
        }
    }

    private static void TraceStatePulse()
    {
        if (_state != _tracePrevState)
        {
            _tracePrevState = _state;
            _traceSameTicks = 0;
            TraceTip("进入");
            return;
        }

        _traceSameTicks++;
        if (_traceSameTicks % TraceHeartbeatEvery != 0)
        {
            return;
        }

        if (_state == StUseTick)
        {
            TraceTip(string.Format(
                "心跳 用角{0} 格{1} 次{2} 等确认{3}/{4} stale{5}",
                _useUidIndex,
                _useSlot,
                _useAttempt,
                _awaitingUseConfirm ? 1 : 0,
                _confirmWaitTicks,
                _staleUseCount));
            return;
        }

        TraceTip(string.Format("心跳 等待{0}/{1}", _waitTicks, WaitTicksMax));
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
            TraceTip("整条流水线超时，强制结束");
            FinishDaily(string.Format(
                "日常超时结束：签到{0} · 月卡{1} · 在线{2}档 · 用道具{3}次",
                _signClaims,
                _monthClaims,
                _onlineClaims,
                _itemUses));
            return;
        }

        TraceStatePulse();

        switch (_state)
        {
            case StSendList:
            {
                if (_uidIndex >= _uids.Count)
                {
                    TraceTip("列表角色已走完→用道具");
                    _state = StUsePrep;
                    return;
                }

                var uid = _uids[_uidIndex];
                ClearActiveListInfo();
                SendActivity("活动列表", uid, 0, 0);
                TraceTip("已发活动列表");
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
                    TraceTip(string.Format(
                        "列表OK 签到{0} 月卡{1} 在线{2}",
                        _signTitleId,
                        _monthTitleId,
                        _onlineTitleId));
                    _state = StSendSignInfo;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    TraceTip("等列表超时→下一角色");
                    _state = StNextUid;
                }

                return;
            }
            case StSendSignInfo:
            {
                if (_signTitleId < 0)
                {
                    TraceTip("无签到活动→月卡");
                    _state = StSendMonthInfo;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "周期14日签到";
                SendActivity("活动信息", uid, _signTitleId, 0);
                TraceTip("已发签到信息 id=" + _signTitleId);
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
                    TraceTip("签到信息到→领取");
                    _state = StClaimSign;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    TraceTip("等签到信息超时→月卡");
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
                    TraceTip("签到已领 +" + _signClaims);
                }
                else
                {
                    TraceTip("签到跳过(已领/不可领)");
                }

                _state = StSendMonthInfo;
                return;
            }
            case StSendMonthInfo:
            {
                if (_monthTitleId < 0)
                {
                    TraceTip("无月卡活动→在线");
                    _state = StSendOnlineInfo;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "特权月卡";
                SendActivity("活动信息", uid, _monthTitleId, 0);
                TraceTip("已发月卡信息 id=" + _monthTitleId);
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
                    _pendingInfo = info;
                    TraceTip("月卡信息到→领取");
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    TraceTip("等月卡信息超时→在线");
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
                        TraceTip("月卡每日已领 +" + _monthClaims);
                    }
                    else
                    {
                        TraceTip(string.Format(
                            "月卡跳过 tqk={0} day={1} aid={2}",
                            tqkTime,
                            tqkbDay ? 1 : 0,
                            activityId));
                    }
                }
                else
                {
                    TraceTip("月卡无数据");
                }

                _state = StSendOnlineInfo;
                return;
            }
            case StSendOnlineInfo:
            {
                if (_onlineTitleId < 0)
                {
                    TraceTip("无在线活动→下一角色");
                    _state = StNextUid;
                    return;
                }

                var uid = _uids[_uidIndex];
                _expectUid = uid;
                _expectInfoType = "累计在线奖励";
                SendActivity("活动信息", uid, _onlineTitleId, 0);
                TraceTip("已发在线信息 id=" + _onlineTitleId);
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
                    TraceTip("在线信息到 可领档=" + (_onlineClaimable != null ? _onlineClaimable.Count : 0));
                    _state = StClaimOnline;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    TraceTip("等在线信息超时→下一角色");
                    _state = StNextUid;
                }

                return;
            }
            case StClaimOnline:
            {
                if (_onlineClaimable == null || _onlineClaimIndex >= _onlineClaimable.Count)
                {
                    _pendingInfo = null;
                    TraceTip("在线档领完→下一角色");
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
                    TraceTip(string.Format("领在线档 tier={0} 累计{1}", tierId, _onlineClaims));
                }
                else
                {
                    TraceTip(string.Format("在线档无效 aid={0} tier={1}", activityId, tierId));
                }

                return;
            }
            case StNextUid:
            {
                _uidIndex++;
                TraceTip("切换下一角色");
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
                TraceTip("开始扫背包用道具");
                _state = StUseTick;
                return;
            }
            case StUseTick:
            {
                if (_uids == null || _useUidIndex >= _uids.Count)
                {
                    TraceTip("用道具角色扫完→结束");
                    _state = StDone;
                    return;
                }

                // 工时小闹钟等：仅在「刚用完等确认」时点 MessageBox；
                // 禁止每 tick 无条件点弹窗（残留面板会导致死循环点确定）。
                if (_awaitingUseConfirm)
                {
                    if (TryConfirmMessageBox())
                    {
                        TraceTip("MessageBox已点确定");
                        _awaitingUseConfirm = false;
                        _confirmWaitTicks = 0;
                        _skipConfirmTicks = 2;
                        return;
                    }

                    _confirmWaitTicks++;
                    if (_confirmWaitTicks >= WaitTicksMax)
                    {
                        TraceTip("等确认超时→跳下一格");
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
            case StGiftSend:
            {
                if (_uids == null || _giftUidIndex >= _uids.Count)
                {
                    _state = StGiftDone;
                    return;
                }

                var codes = ActiveGiftCodes;
                if (_giftCodeIndex >= codes.Length)
                {
                    _giftCodeIndex = 0;
                    _giftUidIndex++;
                    if (_giftUidIndex >= _uids.Count)
                    {
                        _state = StGiftDone;
                        return;
                    }
                }

                var uid = _uids[_giftUidIndex];
                var code = codes[_giftCodeIndex];
                SendCdKeyExchange(uid, code);
                _giftSent++;
                TraceTip(string.Format("已发 {0} → 角{1}", code, _giftUidIndex + 1));
                _giftCodeIndex++;
                return;
            }
            case StGiftDone:
            {
                FinishDaily(string.Format(
                    "新手礼包码完成：角色{0} · 已尝试发送{1}次（已领过会由服务端忽略）",
                    _uids != null ? _uids.Count : 0,
                    _giftSent));
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
                    TraceTip(string.Format("格{0}用满{1}次跳过 id={2}", _useSlot, MaxUsePerSlot, itemId));
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
                        TraceTip(string.Format(
                            "格{0}堆积不变跳过 id={1} pile={2}",
                            _useSlot,
                            itemId,
                            pile));
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
                    TraceTip(string.Format("用道具发包失败 格{0} id={1}", _useSlot, itemId));
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
                    TraceTip(string.Format(
                        "已用待确认 格{0} id={1} pile={2} 次{3}",
                        _useSlot,
                        itemId,
                        pile,
                        _useAttempt));
                }
                else
                {
                    TraceTip(string.Format(
                        "已用 格{0} id={1} pile={2} 次{3}",
                        _useSlot,
                        itemId,
                        pile,
                        _useAttempt));
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
        _runningGift = false;
        _shareArmed = false;
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
        _runningGift = false;
        _shareArmed = false;
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
        SendActivityFull(type, uid, id, activityId, "");
    }

    /// <summary>与 Com_Cdkey 一致：无 N_ → CDKey兑换；含 N_ → giftCode。activityId=4。</summary>
    private static void SendCdKeyExchange(string uid, string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        var type = code.IndexOf("N_", StringComparison.Ordinal) >= 0 ? "giftCode" : "CDKey兑换";
        SendActivityFull(type, uid, 0, 4, code);
    }

    private static void SendActivityFull(string type, string uid, int id, int activityId, string code)
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
            else if (i == 4 && psAll[i].ParameterType == typeof(string))
            {
                args[i] = code ?? "";
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

    /// <summary>
    /// 日常只扫「本账号多开在线」或「当前队伍」角色。
    /// 禁止 GetAllPlayers：切队长/点头像后字典会堆历史角色，出现角8/22 这类假人数，需重进才清。
    /// </summary>
    private static List<string> CollectUids()
    {
        var result = new List<string>();
        _uidSource = "none";

        // 1) 五开 MultiInfo：Online>=1 的本账号角色
        try
        {
            var teamMgr = GetManagerInstance("TeamManager");
            var multi = GetMember(teamMgr, "MultiInfo");
            var players = GetMember(multi, "Players") as IList;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    var uid = Convert.ToString(GetMember(p, "Uid") ?? "");
                    var online = Convert.ToInt32(GetMember(p, "Online") ?? 0);
                    if (!string.IsNullOrEmpty(uid) && online >= 1)
                    {
                        AddUidUnique(result, uid);
                    }
                }

                if (result.Count > 0)
                {
                    _uidSource = "multi";
                }
            }
        }
        catch
        {
            // ignore
        }

        // 2) 当前队伍 teamData（UseFlag==1）
        if (result.Count == 0)
        {
            try
            {
                var teamData = GetStaticMember("PlayerDataHolder", "teamData") as Array;
                if (teamData != null)
                {
                    foreach (var slot in teamData)
                    {
                        if (slot == null)
                        {
                            continue;
                        }

                        if (Convert.ToInt32(GetMember(slot, "UseFlag") ?? 0) != 1)
                        {
                            continue;
                        }

                        var player = GetMember(slot, "Player");
                        var uid = Convert.ToString(GetMember(player, "Uid") ?? "");
                        AddUidUnique(result, uid);
                    }

                    if (result.Count > 0)
                    {
                        _uidSource = "team";
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // 3) 保底：主角色 / 当前选中
        if (result.Count == 0)
        {
            var main = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "");
            if (string.IsNullOrEmpty(main))
            {
                var pd = GetStaticMember("PlayerDataHolder", "playerData");
                main = Convert.ToString(GetMember(pd, "Uid") ?? GetMember(pd, "uid") ?? "");
            }

            AddUidUnique(result, main);
            var select = Convert.ToString(GetStaticMember("PlayerDataHolder", "SelectPlayerUid") ?? "");
            AddUidUnique(result, select);
            if (result.Count > 0)
            {
                _uidSource = "main";
            }
        }

        return result;
    }

    private static void AddUidUnique(List<string> list, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == uid)
            {
                return;
            }
        }

        list.Add(uid);
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
