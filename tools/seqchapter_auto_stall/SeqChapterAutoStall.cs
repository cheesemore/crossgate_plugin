using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

/// <summary>
/// 单角色一键自动上架 DLL。部署为 hotfixdata/SeqChapterAutoStall.dll.bytes。
/// 由助手面板「脚本」页「一键上架」按钮加载运行（也可由批量模块经 IPC 调用）。
///
/// 流程（状态机 + 主线程 Timer，StepSec=0.4s，节奏与日常/采集提取一致）：
///   安全码检查 → 停止挂机 → 退队 → 回法兰城(mapId==0,floor==1000) →
///   查摊位状态（1出摊/2到期→寻找摊位→收摊→0.1s→重摆重置时间；3未摆摊→直接摆摊）→
///   寻找摊位落位 → 读剩余格位(20−在售数) → 扫描背包[8..67] →
///   只上架「可上架装备表单」内有定价的装备（读 hotfixdata/seqchapter_auto_sell_prices.txt），
///   按运行品质取价（D~S 五档）→ 分批(≤5)发送「上架摊位」→ 复查格数直至满/无货。
///
/// 约束：
///   - 全程不点「增加时间」续费（要钱）；重置时间一律用 收摊→重摆（免费）。
///   - 每步都 Tip；网络类等待超时最多重试 2 次；不可执行步骤（无摊位/卡位置/安全码等）立即终止并 Tip。
///   - 定位失败（回城超时/寻找摊位后状态异常）→ 终止转人工，不硬顶。
///   - 只上架表单内装备（有定价），宠物/料理/表单外道具一律跳过。
///
/// IPC（供批量自动上架模块，目录与精简桥接一致）：
///   ~/.seqchapter_helper/instances/inst_{pid}/
///     auto_stall_cmd.json   {"cmd":"start","uid":"..."} / {"cmd":"stop"}
///     auto_stall_ack.json   {"cmd":"start","ok":true/false,"msg":"..."}
///     auto_stall_state.json {"running":bool,"phase":"...","items_listed":N,"skipped":N,"reason":"..."}
/// </summary>
public static class SeqChapterAutoStall
{
    public const string AssetPath = "hotfixdata/SeqChapterAutoStall.dll.bytes";
    public const string TypeName = "SeqChapterAutoStall";
    public const string PriceFileName = "seqchapter_auto_sell_prices.txt";

    /// <summary>节奏 tick 间隔（秒）：同日常。</summary>
    private const float StepSec = 0.4f;

    /// <summary>等待服务端回推的最大 tick 数（约 5.2s）。</summary>
    private const int WaitTicksMax = 13;

    /// <summary>网络问题最多重试次数（1 次发送 + 2 次重发），之后终止。</summary>
    private const int MaxOpRetries = 2;

    /// <summary>摊位商品上限（客户端固定 20）。</summary>
    private const int StallCap = 20;

    /// <summary>背包可上架范围 [BagStart .. BagEnd)。</summary>
    private const int BagStart = 8;
    private const int BagEnd = 68;

    /// <summary>单条「上架摊位」最多携带商品数。</summary>
    private const int ShelfBatch = 5;

    /// <summary>寻找摊位后等待落位固定 tick 数（约 2.4s）。</summary>
    private const int WaitFindTicks = 6;

    private static bool _bootstrapped;
    private static object _timer;

    private static bool _started;
    private static int _state;
    private static int _waitTicks;
    private static int _opRetry;
    private static string _phase = "";
    private static string _uid = "";
    private static string _stallName = "";
    private static int _activeCount;
    private static int _listedCount;
    private static int _skippedNoPrice;
    private static int _skippedUnlistable;
    private static int _skippedSellable;
    private static List<object[]> _candidates;
    private static int _candidateIdx;
    private static List<object[]> _pendingBatch;
    private static HashSet<int> _listedBagIndexes;
    private static Dictionary<int, int[]> _priceTable;
    private static bool _needResetTime;
    private static string _failReason = "";
    private static string _lastTip = "";
    private static long _startedMs;

    // IPC
    private static string _baseDir;
    private static string _instanceId;

    // states
    private const int StIdle = 0;
    private const int StBegin = 1;
    private const int StSecurity = 2;
    private const int StStopHook = 3;
    private const int StWaitStopHook = 4;
    private const int StUnparty = 5;
    private const int StWaitUnparty = 6;
    private const int StBackCity = 7;
    private const int StWaitCity = 8;
    private const int StQueryStall = 9;
    private const int StWaitStatus = 10;
    private const int StDecide = 11;
    private const int StSetupStall = 12;
    private const int StWaitSetup = 13;
    private const int StFindStall = 14;
    private const int StWaitFind = 15;
    private const int StCollapseStall = 16;
    private const int StWaitCollapse = 17;
    private const int StDelayReset = 18;
    private const int StReSetupStall = 19;
    private const int StWaitReSetup = 20;
    private const int StQuerySlots = 21;
    private const int StWaitSlots = 22;
    private const int StScanBackpack = 23;
    private const int StSendShelf = 24;
    private const int StWaitShelf = 25;
    private const int StNextBatch = 26;
    private const int StDone = 27;
    private const int StFail = 28;

    // ---------------- 入口 ----------------

    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        EnsureTimer();
    }

    /// <summary>助手面板「脚本」页：一键上架当前主角色。</summary>
    public static bool RunAutoStallFromUi()
    {
        Bootstrap();
        if (_started)
        {
            Tip("自动上架已在运行，请先停止");
            return false;
        }

        var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "");
        if (string.IsNullOrEmpty(uid))
        {
            var pd = GetStaticMember("PlayerDataHolder", "playerData");
            uid = Convert.ToString(GetMember(pd, "Uid") ?? "");
        }

        if (string.IsNullOrEmpty(uid))
        {
            Tip("未登录角色，无法自动上架");
            return false;
        }

        return StartRun(uid);
    }

    /// <summary>停止当前自动上架（UI / IPC）。</summary>
    public static void StopFromUi()
    {
        AbortRun("已手动停止");
    }

    /// <summary>是否正在运行（供 UI 状态展示）。</summary>
    public static bool IsRunning()
    {
        return _started;
    }

    /// <summary>当前阶段名（供 UI 状态展示）。</summary>
    public static string GetPhase()
    {
        return _phase;
    }

    /// <summary>本次已上架件数（供 UI 状态展示）。</summary>
    public static int GetListedCount()
    {
        return _listedCount;
    }

    /// <summary>最近一条 Tip（供 UI 状态展示）。</summary>
    public static string GetLastTip()
    {
        return _lastTip;
    }

    /// <summary>IPC：指定 uid 启动（批量模块用）。</summary>
    private static bool StartRun(string uid)
    {
        EnsureIpcDir();
        EnsureTimer();
        LoadPriceTable();

        _uid = uid;
        _state = StBegin;
        _started = true;
        _listedCount = 0;
        _skippedNoPrice = 0;
        _skippedUnlistable = 0;
        _skippedSellable = 0;
        _candidates = null;
        _candidateIdx = 0;
        _pendingBatch = null;
        _listedBagIndexes = new HashSet<int>();
        _stallName = "";
        _activeCount = 0;
        _failReason = "";
        _startedMs = NowMs();
        _phase = "启动";
        return true;
    }

    // ---------------- Timer 驱动（同日常） ----------------

    private static void EnsureTimer()
    {
        if (_timer != null)
        {
            return;
        }

        try
        {
            var timerType = FindType("Timer");
            if (timerType == null)
            {
                return;
            }

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
                return;
            }

            var tick = (Action)Tick;
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
        catch
        {
            // ignore
        }
    }

    private static void Tick()
    {
        try
        {
            if (_started)
            {
                StepAutoStall();
            }
            else
            {
                PollIpc();
            }
        }
        catch
        {
            // ignore
        }
    }

    // ---------------- 状态机 ----------------

    private static void StepAutoStall()
    {
        switch (_state)
        {
            case StIdle:
                _state = StBegin;
                return;

            case StBegin:
            {
                if (_priceTable == null || _priceTable.Count == 0)
                {
                    Fail("未找到定价配置（seqchapter_auto_sell_prices.txt），请重新打补丁");
                    return;
                }

                Tip("自动上架开始");
                _phase = "检查安全码";
                _state = StSecurity;
                return;
            }

            case StSecurity:
            {
                var role = GetManagerInstance("RoleManager");
                var flag = Convert.ToInt32(GetMember(role, "CurrSecurityCodeFlag") ?? 0);
                if (flag != 0)
                {
                    Fail("检测到二级密码，请先验证后再上架");
                    return;
                }

                _phase = "停止挂机";
                _state = StStopHook;
                return;
            }

            case StStopHook:
            {
                var encounter = ReadEncounterStatus();
                if (encounter == 0)
                {
                    _phase = "退出队伍";
                    _state = StUnparty;
                    return;
                }

                SendStopAutoBattle();
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitStopHook;
                return;
            }

            case StWaitStopHook:
            {
                _waitTicks++;
                if (ReadEncounterStatus() == 0)
                {
                    _phase = "退出队伍";
                    _state = StUnparty;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("停止挂机"))
                    {
                        SendStopAutoBattle();
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StUnparty:
            {
                if (!IsParty())
                {
                    _phase = "回法兰城";
                    _state = StBackCity;
                    return;
                }

                var isLeader = IsLeader();
                Tip(isLeader ? "正在解散队伍…" : "正在离开队伍…");
                SendTeamLeave(isLeader);
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitUnparty;
                return;
            }

            case StWaitUnparty:
            {
                _waitTicks++;
                if (!IsParty())
                {
                    _phase = "回法兰城";
                    _state = StBackCity;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("退出队伍"))
                    {
                        SendTeamLeave(IsLeader());
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StBackCity:
            {
                SendMenuReturnCity();
                Tip("正在回法兰城…");
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitCity;
                return;
            }

            case StWaitCity:
            {
                _waitTicks++;
                if (IsInFalanCity())
                {
                    _phase = "查询摊位";
                    Tip("已回到法兰城");
                    _state = StQueryStall;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("回城"))
                    {
                        SendMenuReturnCity();
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StQueryStall:
            {
                SendOpenStall();
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitStatus;
                return;
            }

            case StWaitStatus:
            {
                _waitTicks++;
                var stall = ReadStallData();
                if (stall != null)
                {
                    _state = StDecide;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("查询摊位状态"))
                    {
                        SendOpenStall();
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StDecide:
            {
                var status = ReadStallStatus();
                _stallName = ReadStallName();
                if (status == 1)
                {
                    _needResetTime = true;
                    Tip("检测到已出摊，寻找摊位后收摊重摆重置时间");
                    _phase = "寻找摊位";
                    _state = StFindStall;
                    return;
                }

                if (status == 2)
                {
                    _needResetTime = true;
                    Tip("摊位已到期，收摊重摆重置时间");
                    _phase = "寻找摊位";
                    _state = StFindStall;
                    return;
                }

                if (status == 3)
                {
                    _needResetTime = false;
                    Tip("未摆摊，先在法兰城摆摊");
                    _phase = "摆摊";
                    _state = StSetupStall;
                    return;
                }

                Fail("摊位状态异常(" + status + ")，请人工确认");
                return;
            }

            case StSetupStall:
            {
                SendStallMsg("我要摆摊");
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitSetup;
                return;
            }

            case StWaitSetup:
            {
                _waitTicks++;
                if (ReadStallStatus() == 1)
                {
                    Tip("摆摊成功");
                    _phase = "寻找摊位";
                    _state = StFindStall;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("摆摊"))
                    {
                        SendStallMsg("我要摆摊");
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StFindStall:
            {
                SendStallMsg("寻找摊位");
                Tip("正在传送到摊位…");
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitFind;
                return;
            }

            case StWaitFind:
            {
                _waitTicks++;
                if (_waitTicks >= WaitFindTicks)
                {
                    if (_needResetTime)
                    {
                        _phase = "收起摊位";
                        _state = StCollapseStall;
                    }
                    else
                    {
                        _phase = "读取格位";
                        _state = StQuerySlots;
                    }
                }

                return;
            }

            case StCollapseStall:
            {
                SendStallMsg("收起摊位");
                Tip("正在收起摊位…");
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitCollapse;
                return;
            }

            case StWaitCollapse:
            {
                _waitTicks++;
                if (ReadStallStatus() == 3)
                {
                    Tip("已收摊");
                    _state = StDelayReset;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("收起摊位"))
                    {
                        SendStallMsg("收起摊位");
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StDelayReset:
            {
                // 收摊后稍作停顿再重摆（原流程 0.1s，此处 1 tick 0.4s 更稳）
                _phase = "重新摆摊";
                _state = StReSetupStall;
                return;
            }

            case StReSetupStall:
            {
                SendStallMsg("我要摆摊");
                Tip("重新摆摊（摊位时间已免费重置）");
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitReSetup;
                return;
            }

            case StWaitReSetup:
            {
                _waitTicks++;
                if (ReadStallStatus() == 1)
                {
                    Tip("重新摆摊成功");
                    _phase = "读取格位";
                    _state = StQuerySlots;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("重新摆摊"))
                    {
                        SendStallMsg("我要摆摊");
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StQuerySlots:
            {
                SendOpenStall();
                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitSlots;
                return;
            }

            case StWaitSlots:
            {
                _waitTicks++;
                var stall = ReadStallData();
                if (stall != null && ReadActiveCount() >= 0)
                {
                    _activeCount = ReadActiveCount();
                    var available = StallCap - _activeCount;
                    if (available <= 0)
                    {
                        Tip("摊位已满（20/20），无可上架格位");
                        _state = StDone;
                        return;
                    }

                    Tip("剩余可上架格位 " + available);
                    _phase = "扫描背包";
                    _state = StScanBackpack;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (!RetryOrFail("读取摊位格数"))
                    {
                        SendOpenStall();
                        _waitTicks = 0;
                    }
                }

                return;
            }

            case StScanBackpack:
            {
                var available = StallCap - _activeCount;
                _candidates = CollectCandidates();
                _candidateIdx = 0;
                _phase = "上架";

                if (_candidates.Count == 0)
                {
                    var reason = "背包无可上架装备";
                    if (_skippedNoPrice > 0)
                    {
                        reason += "（跳过无定价 " + _skippedNoPrice + " 件）";
                    }

                    Tip(reason);
                    _state = StDone;
                    return;
                }

                if (_candidates.Count > available)
                {
                    _candidates = _candidates.GetRange(0, available);
                }

                Tip("发现可上架装备 " + _candidates.Count + " 件，开始上架");
                _state = StSendShelf;
                return;
            }

            case StSendShelf:
            {
                if (_candidateIdx >= _candidates.Count)
                {
                    _state = StNextBatch;
                    return;
                }

                var batch = new List<object[]>();
                while (batch.Count < ShelfBatch && _candidateIdx < _candidates.Count)
                {
                    batch.Add(_candidates[_candidateIdx]);
                    _candidateIdx++;
                }

                // 无论发送是否立刻成功，先把本批标记为已处理，避免后续重扫时重复上架
                _pendingBatch = batch;
                foreach (var cand in batch)
                {
                    _listedBagIndexes.Add(Convert.ToInt32(cand[0]));
                }

                if (!SendShelfBatch(batch))
                {
                    if (!RetryOrFail("上架协议构造"))
                    {
                        _state = StSendShelf;
                    }

                    return;
                }

                _waitTicks = 0;
                _opRetry = 0;
                _state = StWaitShelf;
                return;
            }

            case StWaitShelf:
            {
                _waitTicks++;
                var after = ReadActiveCount();
                if (after > _activeCount)
                {
                    var success = after - _activeCount;
                    _listedCount += success;
                    _activeCount = after;
                    Tip("已上架 " + success + " 件（累计 " + _listedCount + " 件）");
                    _state = StNextBatch;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (RetryOrFail("上架"))
                    {
                        return;
                    }

                    // 重发本批（不消耗候选指针）
                    SendShelfBatch(_pendingBatch);
                    _waitTicks = 0;
                }

                return;
            }

            case StNextBatch:
            {
                if (_candidateIdx >= _candidates.Count)
                {
                    _state = StDone;
                    return;
                }

                _phase = "读取格位";
                _state = StQuerySlots;
                return;
            }

            case StDone:
            {
                var extra = "";
                if (_skippedNoPrice > 0 || _skippedUnlistable > 0)
                {
                    extra = "（跳过无定价 " + _skippedNoPrice
                            + "、不可售 " + _skippedUnlistable + " 件）";
                }

                Tip("自动上架完成：共上架 " + _listedCount + " 件" + extra);
                _phase = "完成";
                _started = false;
                WriteIpcState();
                return;
            }

            case StFail:
            {
                FinishFail();
                return;
            }
        }
    }

    private static void FinishFail()
    {
        Tip("自动上架终止：" + _failReason);
        _phase = "终止";
        _started = false;
        WriteIpcState();
    }

    private static void Fail(string reason)
    {
        _failReason = reason;
        _state = StFail;
    }

    private static void AbortRun(string reason)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _state = StIdle;
        _failReason = reason;
        Tip("自动上架终止：" + reason);
        _phase = "终止";
        WriteIpcState();
    }

    /// <summary>等待超时：未到重试上限返回 false（调用方重发）；已到上限 → 终止。</summary>
    private static bool RetryOrFail(string opName)
    {
        if (_opRetry < MaxOpRetries)
        {
            _opRetry++;
            return false;
        }

        Fail(opName + "超时（已重试 " + MaxOpRetries + " 次），请人工处理");
        return true;
    }

    // ---------------- 摊位 / 角色 / 背包数据 ----------------

    private static int ReadEncounterStatus()
    {
        try
        {
            var pd = GetStaticMember("PlayerDataHolder", "playerData");
            return Convert.ToInt32(GetMember(pd, "encounterStatus") ?? 0);
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsParty()
    {
        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var entity = GetMember(pm, "playerEntity");
            return entity != null && Convert.ToBoolean(CallMethod(entity, "IsParty") ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLeader()
    {
        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var entity = GetMember(pm, "playerEntity");
            return entity != null && Convert.ToBoolean(CallMethod(entity, "IsLeader") ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInFalanCity()
    {
        try
        {
            var player = GetPlayer(_uid);
            if (player == null)
            {
                return false;
            }

            return Convert.ToInt32(GetMember(player, "mapId") ?? -1) == 0
                   && Convert.ToInt32(GetMember(player, "floor") ?? -1) == 1000;
        }
        catch
        {
            return false;
        }
    }

    private static object GetPlayer(string uid)
    {
        try
        {
            var method = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return method?.Invoke(null, new object[] { uid });
        }
        catch
        {
            return null;
        }
    }

    private static IList GetItemDatas(string uid)
    {
        try
        {
            var method = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return method?.Invoke(null, new object[] { uid }) as IList;
        }
        catch
        {
            return null;
        }
    }

    private static object ReadStallData()
    {
        try
        {
            var sm = GetManagerInstance("StallManager");
            var dic = GetMember(sm, "stallData") as IDictionary;
            if (dic == null || string.IsNullOrEmpty(_uid))
            {
                return null;
            }

            try
            {
                return dic[_uid];
            }
            catch
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static int ReadStallStatus()
    {
        var stall = ReadStallData();
        return stall == null ? -1 : Convert.ToInt32(GetMember(stall, "Status") ?? -1);
    }

    private static string ReadStallName()
    {
        var stall = ReadStallData();
        if (stall == null)
        {
            return "";
        }

        var name = Convert.ToString(GetMember(stall, "Name") ?? "");
        if (string.IsNullOrEmpty(name))
        {
            var info = GetMember(stall, "Info");
            name = Convert.ToString(GetMember(info, "Name") ?? "");
        }

        return name ?? "";
    }

    /// <summary>在售商品数 = Info.List 中 Status != 4 的条数；读不到返回 -1。</summary>
    private static int ReadActiveCount()
    {
        try
        {
            var stall = ReadStallData();
            if (stall == null)
            {
                return -1;
            }

            var info = GetMember(stall, "Info");
            var list = GetMember(info, "List") as IEnumerable;
            if (list == null)
            {
                return -1;
            }

            var count = 0;
            foreach (var c in list)
            {
                if (c == null)
                {
                    continue;
                }

                var status = Convert.ToInt32(GetMember(c, "Status") ?? 0);
                if (status != 4)
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>已上架商品对应的背包格下标集合（从 Info.List 里 Status==2 的条目 Id）。</summary>
    private static HashSet<int> ReadListedBagIndexes()
    {
        var result = new HashSet<int>();
        try
        {
            var stall = ReadStallData();
            if (stall == null)
            {
                return result;
            }

            var info = GetMember(stall, "Info");
            var list = GetMember(info, "List") as IEnumerable;
            if (list == null)
            {
                return result;
            }

            foreach (var c in list)
            {
                if (c == null)
                {
                    continue;
                }

                var status = Convert.ToInt32(GetMember(c, "Status") ?? 0);
                var id = Convert.ToString(GetMember(c, "Id") ?? "");
                int idx;
                if (status == 2 && int.TryParse(id, out idx))
                {
                    result.Add(idx);
                }
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    /// <summary>扫描背包 [8..67]，返回可上架候选 [bagIndex, itemData, price, grade]。</summary>
    private static List<object[]> CollectCandidates()
    {
        var result = new List<object[]>();
        _skippedNoPrice = 0;
        _skippedUnlistable = 0;
        _skippedSellable = 0;

        try
        {
            var items = GetItemDatas(_uid);
            if (items == null || items.Count <= BagStart)
            {
                return result;
            }

            var serverListed = ReadListedBagIndexes();
            for (var i = BagStart; i < BagEnd && i < items.Count; i++)
            {
                var item = items[i];
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

                if (_listedBagIndexes.Contains(i) || serverListed.Contains(i))
                {
                    continue;
                }

                var itemId = Convert.ToInt32(GetMember(data, "Id") ?? 0);
                int[] prices;
                if (_priceTable == null || !_priceTable.TryGetValue(itemId, out prices))
                {
                    _skippedNoPrice++;
                    continue;
                }

                if (!IsListable(data))
                {
                    _skippedUnlistable++;
                    continue;
                }

                var grade = ComputeEquipGrade(data);
                if (grade < 1 || grade > 5 || prices.Length < grade)
                {
                    _skippedNoPrice++;
                    continue;
                }

                var price = prices[grade - 1];
                if (price < 10)
                {
                    _skippedSellable++;
                    continue;
                }

                result.Add(new object[] { i, data, price, grade });
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    /// <summary>是否可上架（与 StallPanel 判定一致：非「未鉴定且无交易保护且未锁定」灰显）。</summary>
    private static bool IsListable(object data)
    {
        try
        {
            var flg = Convert.ToInt32(GetMember(data, "Flg") ?? 0);
            var locked = Convert.ToInt32(GetMember(data, "Locked") ?? 0);
            var appraisal = (flg & 4) != 0;       // PROTO_ITEM_FLAG_APPRAISAL
            var dicePass = (flg & 0x10) != 0;     // PROTO_ITEM_FLAG_DICE_PASS
            var logoutPass = (flg & 0x20) != 0;   // PROTO_ITEM_FLAG_LOGOUT_PASS
            var grayed = appraisal && !dicePass && !logoutPass && locked == 0;
            return !grayed;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 运行品质（1=D 2=C 3=B 4=A 5=S）。与游戏 ItemInfoPanel 一致：
    /// 对每个非零属性按 (value-min)/(max-min) 归一化加权，再由
    /// ItemManager.GetEquipScoreLevel 按 EquipScoreLevelConfig 阈值映射。
    /// 无宝石路径（已鉴定装备一般无宝石；有宝石品级可能偏差一档，价格差 10%，可接受）。
    /// </summary>
    private static int ComputeEquipGrade(object data)
    {
        try
        {
            var im = GetManagerInstance("ItemManager");
            if (im == null)
            {
                return 0;
            }

            var weights = GetMember(im, "m_EquipScoreWeight") as IList;
            if (weights == null || weights.Count < 20)
            {
                return 0;
            }

            var itemId = Convert.ToInt32(GetMember(data, "Id") ?? 0);
            var cfg = GetItemConfig(itemId);
            if (cfg == null)
            {
                return 0;
            }

            var equip = GetMember(data, "Equip");
            if (equip == null)
            {
                return 0;
            }

            var names = new string[]
            {
                "Attack", "Defence", "Agility", "Magic", "Recovery",
                "Poison", "Sleep", "Stone", "Drunk", "Confusion",
                "Amnesia", "Critical", "Counter", "Hitrate", "Avoid",
                "Hp", "Fp", "Charisma", "Adm", "Rss",
            };
            var equipNames = new string[]
            {
                "AttackPower", "DefencePower", "Agility", "MagicPower", "Recovery",
                "Poison", "Sleep", "Stone", "Drunk", "Confusion",
                "Amnesia", "ModCritical", "ModCounter", "ModHitrate", "ModAvoid",
                "Hp", "Forcepoint", "FixCharm", "Adm", "Rss",
            };

            var totleWeight = 0;
            var scoreRecord = new List<float>();
            var weightRecord = new List<int>();
            for (var k = 0; k < names.Length; k++)
            {
                var value = Convert.ToInt32(GetMember(equip, equipNames[k]) ?? 0);
                if (value == 0)
                {
                    continue; // 与 SetRealEquip 一致：0 属性不参与评分
                }

                var maxV = Convert.ToInt32(GetMember(cfg, names[k] + "Max") ?? 0);
                var minV = Convert.ToInt32(GetMember(cfg, names[k] + "Min") ?? 0);
                if (maxV == minV)
                {
                    continue;
                }

                var weight = Convert.ToInt32(weights[k]);
                totleWeight += weight;
                weightRecord.Add(weight);
                var fraction = (float)(value - minV) / (float)(maxV - minV);
                scoreRecord.Add(fraction);
            }

            if (scoreRecord.Count == 0)
            {
                return 0;
            }

            var method = FindMethod(im.GetType(), "GetEquipScoreLevel",
                new[] { typeof(int), typeof(List<float>), typeof(List<int>) });
            if (method == null)
            {
                return 0;
            }

            return Convert.ToInt32(method.Invoke(im, new object[] { totleWeight, scoreRecord, weightRecord }) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static object GetItemConfig(int itemId)
    {
        try
        {
            var cm = GetManagerInstance("ConfigManager");
            if (cm == null)
            {
                return null;
            }

            var tb = CallMethod(cm, "GetTbItemConfig");
            if (tb == null)
            {
                return null;
            }

            var get = FindMethodByParams(tb.GetType(), "GetOrDefault", 1);
            return get?.Invoke(tb, new object[] { itemId });
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 发送 ----------------

    private static void SendOpenStall()
    {
        var sm = GetManagerInstance("StallManager");
        if (sm == null)
        {
            return;
        }

        var m = FindMethodByParams(sm.GetType(), "OpenStall", 1);
        m?.Invoke(sm, new object[] { _uid });
    }

    private static void SendStallMsg(string type)
    {
        var sm = GetManagerInstance("StallManager");
        if (sm == null)
        {
            return;
        }

        var m = FindMethodByParams(sm.GetType(), "SendStallMessage", 2);
        if (m != null)
        {
            m.Invoke(sm, new object[] { _uid, type });
            return;
        }

        m = FindMethodByParams(sm.GetType(), "SendStallMessage", 4);
        m?.Invoke(sm, new object[] { _uid, type, "", 1 });
    }

    private static void SendStopAutoBattle()
    {
        var role = GetManagerInstance("RoleManager");
        if (role == null)
        {
            return;
        }

        var m = FindMethodByParams(role.GetType(), "SendAutoBattle", 2);
        m?.Invoke(role, new object[] { "停止挂机", _uid });
    }

    private static void SendMenuReturnCity()
    {
        var role = GetManagerInstance("RoleManager");
        if (role == null)
        {
            return;
        }

        var m = FindMethodByParams(role.GetType(), "SendMenu", 2);
        if (m != null)
        {
            m.Invoke(role, new object[] { 3, "-1" });
            return;
        }

        // 原型 SendMenu(int func, string data, string Kuid = "", string callfunc = "")
        m = FindMethodByParams(role.GetType(), "SendMenu", 4);
        m?.Invoke(role, new object[] { 3, "-1", "", "" });
    }

    private static void SendTeamLeave(bool isLeader)
    {
        var tm = GetManagerInstance("TeamManager");
        if (tm == null)
        {
            return;
        }

        var m = FindMethodByParams(tm.GetType(), "SendOperation", 2);
        m?.Invoke(tm, new object[] { isLeader ? "解散队伍" : "离开队伍", _uid });
    }

    /// <summary>构造并发送一批「上架摊位」。返回是否成功构造发送。</summary>
    private static bool SendShelfBatch(List<object[]> batch)
    {
        try
        {
            var streetType = FindType("Proto_CS_Street");
            var commodityType = FindType("Proto_CommodityInfo");
            var arrType = FindType("Proto_commodityInfoArr");
            if (streetType == null || commodityType == null || arrType == null)
            {
                return false;
            }

            var msg = Activator.CreateInstance(streetType);
            var arr = Activator.CreateInstance(arrType);
            SetMember(msg, "Type", "上架摊位");
            SetMember(msg, "KUid", _uid);
            if (!string.IsNullOrEmpty(_stallName))
            {
                SetMember(msg, "Id", _stallName);
            }

            SetMember(msg, "List", arr);
            var arrList = GetMember(arr, "List");
            var add = FindMethodByParams(arrList.GetType(), "Add", 1);

            foreach (var cand in batch)
            {
                var index = Convert.ToInt32(cand[0]);
                var price = Convert.ToInt32(cand[2]);
                var item = Activator.CreateInstance(commodityType);
                SetMember(item, "Id", index.ToString());
                SetMember(item, "Type", 1);
                // 与客户端 OnClickStartSell 一致：服务端按背包格下标(Id)识别道具，Item 字段置空
                SetMember(item, "Item", null);
                SetMember(item, "Pric", price);
                SetMember(item, "Status", 2);
                SetMember(item, "PricType", 0);
                add?.Invoke(arrList, new object[] { item });
            }

            var net = GetManagerInstance("NetManager");
            var send = FindMethodByParams(net.GetType(), "SendMessage", 2);
            if (send == null)
            {
                return false;
            }

            var func = GetLssprotoStreetFunc();
            send.Invoke(net, new object[] { func, msg });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetLssprotoStreetFunc()
    {
        try
        {
            var ls = FindType("LSSPROTO");
            var f = ls?.GetField("LSSPROTO_STREET_FUNC", BindingFlags.Public | BindingFlags.Static);
            return f != null ? Convert.ToInt32(f.GetValue(null)) : 1013;
        }
        catch
        {
            return 1013;
        }
    }

    // ---------------- 定价配置 ----------------

    private static void LoadPriceTable()
    {
        if (_priceTable != null)
        {
            return;
        }

        _priceTable = new Dictionary<int, int[]>();
        try
        {
            var lines = ReadHotfixdataLines(PriceFileName);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int itemId;
                if (parts.Length < 6 || !int.TryParse(parts[0], out itemId))
                {
                    continue;
                }

                var prices = new int[5];
                var ok = true;
                for (var i = 0; i < 5; i++)
                {
                    if (!int.TryParse(parts[i + 1], out prices[i]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    _priceTable[itemId] = prices;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static List<string> ReadHotfixdataLines(string fileName)
    {
        var result = new List<string>();
        foreach (var path in HotfixdataFileCandidates(fileName))
        {
            try
            {
                if (File.Exists(path))
                {
                    result.AddRange(File.ReadAllLines(path));
                    return result;
                }
            }
            catch
            {
                // try next
            }
        }

        return result;
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

    // ---------------- IPC（批量自动上架模块） ----------------

    private static void EnsureIpcDir()
    {
        if (_baseDir != null)
        {
            return;
        }

        try
        {
            var pid = 0;
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                pid = proc.Id;
            }
            catch
            {
                pid = 0;
            }

            _instanceId = "inst_" + pid;
            _baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seqchapter_helper", "instances", _instanceId);
            Directory.CreateDirectory(_baseDir);
        }
        catch
        {
            _baseDir = "";
        }
    }

    private static void PollIpc()
    {
        if (string.IsNullOrEmpty(_baseDir))
        {
            return;
        }

        try
        {
            var cmdPath = Path.Combine(_baseDir, "auto_stall_cmd.json");
            if (!File.Exists(cmdPath))
            {
                return;
            }

            var json = File.ReadAllText(cmdPath);
            var data = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (data == null)
            {
                return;
            }

            File.Delete(cmdPath);
            var cmd = Convert.ToString(data["cmd"] ?? "") ?? "";
            if (cmd == "start")
            {
                var uid = Convert.ToString(data["uid"] ?? "") ?? "";
                var ok = false;
                var msg = "";
                if (string.IsNullOrEmpty(uid))
                {
                    msg = "缺少 uid";
                }
                else if (_started)
                {
                    msg = "已有上架任务运行";
                }
                else
                {
                    ok = StartRun(uid);
                    msg = ok ? "started" : "启动失败";
                }

                WriteAck(cmd, ok, msg);
            }
            else if (cmd == "stop")
            {
                AbortRun("收到停止指令");
                WriteAck(cmd, true, "stopped");
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteAck(string cmd, bool ok, string msg)
    {
        try
        {
            var path = Path.Combine(_baseDir, "auto_stall_ack.json");
            File.WriteAllText(path, MiniJson.Serialize(new Dictionary<string, object>
            {
                ["cmd"] = cmd,
                ["ok"] = ok,
                ["msg"] = msg,
                ["ts"] = (long)(NowMs() / 1000),
            }));
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteIpcState()
    {
        if (string.IsNullOrEmpty(_baseDir))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_baseDir, "auto_stall_state.json");
            File.WriteAllText(path, MiniJson.Serialize(new Dictionary<string, object>
            {
                ["running"] = _started,
                ["phase"] = _phase,
                ["uid"] = _uid,
                ["items_listed"] = _listedCount,
                ["skipped_no_price"] = _skippedNoPrice,
                ["skipped_unlistable"] = _skippedUnlistable,
                ["reason"] = _failReason,
                ["started_ms"] = _startedMs,
                ["ts"] = (long)(NowMs() / 1000),
            }));
        }
        catch
        {
            // ignore
        }
    }

    // ---------------- 反射辅助 ----------------

    private static long NowMs()
    {
        try
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
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

            _lastTip = msg;
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

    private static object GetManagerInstance(string managerName)
    {
        try
        {
            var inner = FindType(managerName);
            if (inner == null)
            {
                return null;
            }

            var managerType = FindType("Manager`1");
            if (managerType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "Manager`1" && t.IsGenericTypeDefinition)
                            {
                                managerType = t;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    if (managerType != null)
                    {
                        break;
                    }
                }
            }

            if (managerType == null)
            {
                return null;
            }

            var closed = managerType.MakeGenericType(inner);
            var prop = closed.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var inst = prop?.GetValue(null, null);
            if (inst != null)
            {
                return inst;
            }

            var field = closed.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static Type FindType(string name)
    {
        try
        {
            var hotfixAsm = FindHotfixAssembly();
            if (hotfixAsm != null)
            {
                var t = hotfixAsm.GetType(name, false, false)
                        ?? hotfixAsm.GetType("Hotfix." + name, false, false);
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
            return Type.GetType(name, false)
                   ?? Type.GetType(name + ", hotfix", false)
                   ?? Type.GetType("Hotfix." + name + ", hotfix", false);
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
            try
            {
                return p.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        var f = t.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
        {
            try
            {
                return f.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        return null;
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
        if (p != null)
        {
            p.SetValue(obj, value);
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
            try
            {
                return p.GetValue(null, null);
            }
            catch
            {
                return null;
            }
        }

        var f = t.GetField(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (f != null)
        {
            try
            {
                return f.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static object CallMethod(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var m = FindMethodByParams(obj.GetType(), name, 0);
        return m?.Invoke(obj, null);
    }

    private static MethodInfo FindMethod(Type type, string name, Type[] parameters)
    {
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null);
    }

    private static MethodInfo FindMethodByParams(Type type, string name, int paramCount)
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.Name == name && m.GetParameters().Length == paramCount)
            {
                return m;
            }
        }

        return null;
    }
}

/// <summary>极简 JSON（与精简桥接 MiniJson 一致）。</summary>
internal static class MiniJson
{
    public static string Serialize(Dictionary<string, object> d)
    {
        var parts = new List<string>();
        foreach (var kv in d)
        {
            parts.Add("\"" + kv.Key + "\":" + Val(kv.Value));
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static string Val(object v)
    {
        if (v == null)
        {
            return "null";
        }

        if (v is bool b)
        {
            return b ? "true" : "false";
        }

        if (v is string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        if (v is Array arr)
        {
            var items = new string[arr.Length];
            for (var i = 0; i < arr.Length; i++)
            {
                items[i] = Val(arr.GetValue(i));
            }

            return "[" + string.Join(",", items) + "]";
        }

        return v.ToString();
    }

    public static object Deserialize(string json)
    {
        return SimpleParse(json.Trim());
    }

    private static object SimpleParse(string json)
    {
        if (json.StartsWith("{"))
        {
            var d = new Dictionary<string, object>();
            json = json.Substring(1, json.Length - 2);
            foreach (var pair in SplitTop(json))
            {
                var idx = pair.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var k = pair.Substring(0, idx).Trim().Trim('"');
                var v = pair.Substring(idx + 1).Trim();
                d[k] = ParseVal(v);
            }

            return d;
        }

        return null;
    }

    private static object ParseVal(string v)
    {
        if (v == "true")
        {
            return true;
        }

        if (v == "false")
        {
            return false;
        }

        if (v.StartsWith("\""))
        {
            return v.Trim('"');
        }

        if (v.StartsWith("{"))
        {
            return SimpleParse(v);
        }

        if (long.TryParse(v, out var num))
        {
            return num;
        }

        return v;
    }

    private static IEnumerable<string> SplitTop(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '{' || c == '[')
            {
                depth++;
            }
            else if (c == '}' || c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            yield return s.Substring(start);
        }
    }
}
