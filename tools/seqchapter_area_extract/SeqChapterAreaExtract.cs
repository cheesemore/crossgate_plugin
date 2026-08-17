using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// 采集自动提取 DLL。部署为 hotfixdata/SeqChapterAreaExtract.dll.bytes
/// 由助手面板「战斗」页开关（SetEnabled），与战斗模式共存（不互斥）；
/// 「脚本」页有「立刻提取」按钮（ExtractNowFromUi，绕过冷却强制提取一轮）。
/// 不需要自动采集：对账号所有在线角色逐个请求采集数据，已采物品共 5 格，
/// 单格达到 999 时对该格发 SendArea("取出物品到账号仓库", uid, index+1, pile) 提取到账号银行。
/// 节奏：状态机 + 主线程 Timer；5 个号 × 每号 5 格分开遍历，格间/号间加短延时，
/// 每 tick 最多发 1 条协议，避免一瞬间扫完导致卡顿。
/// 角色覆盖：MultiInfo 在线角色（五开）→ 当前队伍 → 保底主角色。
/// 触发：开启/立刻提取强制一轮；采集推送仅在空闲且冷却后排队；每 10 分钟兜底。
/// </summary>
public static class SeqChapterAreaExtract
{
    public const string AssetPath = "hotfixdata/SeqChapterAreaExtract.dll.bytes";
    public const string TypeName = "SeqChapterAreaExtract";

    /// <summary>单格触发提取的堆叠数（满格）。</summary>
    public const int FullPile = 999;

    /// <summary>每角色采集仓库格数。</summary>
    private const int SlotCount = 5;

    /// <summary>节奏 tick 间隔（秒）。</summary>
    private const float StepSec = 0.5f;

    /// <summary>发包后至少等待这么多 tick 再读回包（避免旧缓存误判成功）。</summary>
    private const int MinWaitAfterSendTicks = 2;

    /// <summary>等待服务端回推的最大 tick 数（约 6.5s）。</summary>
    private const int WaitTicksMax = 13;

    /// <summary>格与格之间空转 tick（约 1s）。</summary>
    private const int PauseBetweenSlotsTicks = 2;

    /// <summary>角色与角色之间空转 tick（约 1.5s）。</summary>
    private const int PauseBetweenUidsTicks = 3;

    /// <summary>每个等待操作超时后再重发次数，之后跳过。</summary>
    private const int MaxOpRetries = 2;

    /// <summary>每格提取最多尝试次数（1 次 + 重试）。</summary>
    private const int MaxAttemptsPerSlot = 2;

    /// <summary>后台兜底扫描周期（毫秒）：10 分钟。</summary>
    private const long ScanIntervalMs = 10 * 60 * 1000;

    /// <summary>提取冷却（毫秒）：距上次实际发送提取不足此值则不开始新扫描，防重复发。</summary>
    private const long ExtractCooldownMs = 60_000;

    /// <summary>总开关。默认关闭；面板战斗页切换。</summary>
    public static volatile bool PipelineEnabled = false;

    private static bool _bootstrapped;
    private static bool _pipelineRunning;
    private static object _timer;
    private static int _state;
    private static int _waitTicks;
    private static int _pauseLeft;
    private static int _opRetryCount;
    private static List<string> _uids;
    private static int _uidIndex;
    private static int _slotIndex;
    private static int _attempt;
    private static int _extractedCount;
    private static long _lastSendMs;
    private static long _lastScanMs;
    private static bool _pendingEvent;
    /// <summary>面板开启/立刻提取：绕过冷却强制开一轮。</summary>
    private static bool _forceRun;
    private static int _fullSlotCount;
    private static int _resumeAfterPause;

    private static bool _areaHooked;
    private static Action<object> _onAreaEvent;

    // states
    private const int StIdle = 0;
    private const int StCollect = 1;
    private const int StRequestData = 2;
    private const int StWaitData = 3;
    private const int StCheckSlot = 4;
    private const int StExtract = 5;
    private const int StWaitExtract = 6;
    private const int StPause = 7;
    private const int StNextUid = 8;
    private const int StDone = 9;
    private const int StAdvanceSlot = 10;

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
        TryHookAreaEvent();
        EnsureTimer();
    }

    /// <summary>面板战斗页：显式开/关。开启时立即开始一轮。</summary>
    public static void SetEnabled(bool enable)
    {
        Bootstrap();
        SetPipelineEnabledAllCopies(enable);
        if (enable)
        {
            TryHookAreaEvent();
            EnsureTimer();
            _forceRun = true;
            _pendingEvent = true;
        }
        else
        {
            StopTimer();
            _pipelineRunning = false;
            _state = StIdle;
            _uids = null;
            _forceRun = false;
            _pendingEvent = false;
        }
    }

    /// <summary>面板战斗页：切换；返回是否开启。</summary>
    public static bool ToggleFromUi()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetEnabled(enable);
        return enable;
    }

    /// <summary>侧栏百科切换（兼容旧入口）。</summary>
    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetEnabled(enable);
        return enable;
    }

    /// <summary>面板标题协调用：后缀，未开启返回空。</summary>
    public static string BuildTitleSuffix()
    {
        if (!IsPipelineActive())
        {
            return "";
        }

        return "★自动提取★" + _fullSlotCount + "格已满";
    }

    /// <summary>标题/计数用：当前满格总数。</summary>
    public static int GetFullSlotCount()
    {
        return _fullSlotCount;
    }

    /// <summary>立刻提取（脚本页按钮）：绕过冷却，强制开始一轮；返回当前已知满格数。</summary>
    public static int ExtractNowFromUi()
    {
        Bootstrap();
        if (!IsPipelineActive())
        {
            // 手动点按钮即使总开关未开也应执行一轮，但不改变开关状态
        }

        EnsureTimer();
        _forceRun = true;
        _pendingEvent = true;
        _lastSendMs = 0; // 立刻提取：清冷却
        return _fullSlotCount;
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

    private static void StopTimer()
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

    private static void Tick()
    {
        if (!IsPipelineActive())
        {
            if (_pipelineRunning)
            {
                _pipelineRunning = false;
                _state = StIdle;
                _uids = null;
            }

            return;
        }

        try
        {
            StepExtract();
        }
        catch
        {
            // ignore
        }
    }

    // ---------------- 状态机：5 号 × 5 格分开遍历 + 短延时 ----------------

    private static void StepExtract()
    {
        switch (_state)
        {
            case StIdle:
            {
                var now = NowMs();
                var due = _pendingEvent
                          || _forceRun
                          || (_uids == null && now - _lastScanMs >= ScanIntervalMs);
                if (!due)
                {
                    return;
                }

                // 采集推送只排队；仅面板强制 / 立刻提取可绕过冷却
                if (!_forceRun && now - _lastSendMs < ExtractCooldownMs)
                {
                    return;
                }

                _forceRun = false;
                _pendingEvent = false;
                _pipelineRunning = true;
                _state = StCollect;
                return;
            }
            case StCollect:
            {
                _uids = CollectUids();
                _uidIndex = 0;
                _slotIndex = 0;
                _extractedCount = 0;
                _fullSlotCount = 0;
                if (_uids == null || _uids.Count == 0)
                {
                    _lastScanMs = NowMs();
                    _pipelineRunning = false;
                    _state = StIdle;
                    return;
                }

                _state = StRequestData;
                return;
            }
            case StRequestData:
            {
                if (_uidIndex >= _uids.Count)
                {
                    _state = StDone;
                    return;
                }

                var uid = _uids[_uidIndex];
                InvalidateAreaData(uid);
                SendAreaData(uid);
                _waitTicks = 0;
                _opRetryCount = 0;
                _slotIndex = 0;
                _state = StWaitData;
                return;
            }
            case StWaitData:
            {
                _waitTicks++;
                var uid = _uids[_uidIndex];
                // 至少等 MinWaitAfterSendTicks，避免立刻读到未清干净的旧缓存
                if (_waitTicks >= MinWaitAfterSendTicks && ReadAreaData(uid) != null)
                {
                    _state = StCheckSlot;
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    if (RetryOrSkip(StNextUid))
                    {
                        return;
                    }

                    _state = StRequestData; // 重发获取数据
                }

                return;
            }
            case StCheckSlot:
            {
                if (_slotIndex >= SlotCount)
                {
                    BeginPause(PauseBetweenUidsTicks, StNextUid);
                    return;
                }

                var uid = _uids[_uidIndex];
                var pile = ReadSlotPile(uid, _slotIndex);
                if (pile >= FullPile)
                {
                    _fullSlotCount++;
                    _state = StExtract;
                    return;
                }

                // 未满：也按格空转一下，避免连扫反射卡主线程
                BeginPause(PauseBetweenSlotsTicks, StAdvanceSlot);
                return;
            }
            case StExtract:
            {
                var uid = _uids[_uidIndex];
                var pile = ReadSlotPile(uid, _slotIndex);
                if (pile < FullPile)
                {
                    BeginPause(PauseBetweenSlotsTicks, StAdvanceSlot);
                    return;
                }

                if (SendTakeOutToAccountBank(uid, _slotIndex, pile))
                {
                    _lastSendMs = NowMs();
                }

                _attempt = 0;
                _waitTicks = 0;
                _state = StWaitExtract;
                return;
            }
            case StWaitExtract:
            {
                _waitTicks++;
                if (_waitTicks < MinWaitAfterSendTicks)
                {
                    return;
                }

                var uid = _uids[_uidIndex];
                var cur = ReadSlotPile(uid, _slotIndex);
                // cur < 0：读失败，继续等，勿当成功
                if (cur >= 0 && cur < FullPile)
                {
                    _extractedCount++;
                    BeginPause(PauseBetweenSlotsTicks, StAdvanceSlot);
                    return;
                }

                if (_waitTicks >= WaitTicksMax)
                {
                    _attempt++;
                    if (_attempt < MaxAttemptsPerSlot)
                    {
                        _state = StExtract; // 重发一次
                    }
                    else
                    {
                        BeginPause(PauseBetweenSlotsTicks, StAdvanceSlot);
                    }
                }

                return;
            }
            case StPause:
            {
                if (_pauseLeft > 0)
                {
                    _pauseLeft--;
                    return;
                }

                _state = _resumeAfterPause;
                return;
            }
            case StAdvanceSlot:
            {
                _slotIndex++;
                if (_slotIndex >= SlotCount)
                {
                    BeginPause(PauseBetweenUidsTicks, StNextUid);
                }
                else
                {
                    _state = StCheckSlot;
                }

                return;
            }
            case StNextUid:
            {
                _uidIndex++;
                if (_uidIndex >= _uids.Count)
                {
                    _state = StDone;
                }
                else
                {
                    _state = StRequestData;
                }

                return;
            }
            case StDone:
            {
                _lastScanMs = NowMs();
                // 本轮提取回包产生的 OnEvent 不连环开下一轮
                _pendingEvent = false;
                _pipelineRunning = false;
                _uids = null;
                if (_extractedCount > 0)
                {
                    Tip("自动提取：本轮提取 " + _extractedCount + " 格到账号银行");
                }

                _state = StIdle;
                return;
            }
        }
    }

    /// <summary>空转若干 tick 后再进 nextState（格间/号间延时）。</summary>
    private static void BeginPause(int ticks, int nextState)
    {
        if (ticks <= 0)
        {
            _state = nextState;
            return;
        }

        _pauseLeft = ticks;
        _resumeAfterPause = nextState;
        _state = StPause;
    }

    /// <summary>等待超时处理：未到重试上限返回 false（调用方回发送状态重发）；已到上限跳到 skipState。</summary>
    private static bool RetryOrSkip(int skipState)
    {
        if (_opRetryCount < MaxOpRetries)
        {
            _opRetryCount++;
            return false;
        }

        _opRetryCount = 0;
        _state = skipState;
        return true;
    }

    // ---------------- 数据读取 / 发送 ----------------

    /// <summary>清掉该 uid 的采集缓存，强制等本次「获取数据」回包。</summary>
    private static void InvalidateAreaData(string uid)
    {
        try
        {
            if (string.IsNullOrEmpty(uid))
            {
                return;
            }

            var cm = GetManagerInstance("CollectionManager");
            var areaInfos = GetMember(cm, "AreaInfos") as IDictionary;
            if (areaInfos == null)
            {
                return;
            }

            if (areaInfos.Contains(uid))
            {
                areaInfos.Remove(uid);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static object ReadAreaData(string uid)
    {
        try
        {
            var cm = GetManagerInstance("CollectionManager");
            if (cm == null)
            {
                return null;
            }

            var areaInfos = GetMember(cm, "AreaInfos") as IDictionary;
            if (areaInfos == null)
            {
                return null;
            }

            object area = null;
            try
            {
                area = areaInfos[uid];
            }
            catch
            {
                return null;
            }

            return area;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读某角色某格最新 Pile；读取失败返回 -1。</summary>
    private static int ReadSlotPile(string uid, int slotIndex)
    {
        try
        {
            var area = ReadAreaData(uid);
            if (area == null)
            {
                return -1;
            }

            var have = GetMember(area, "Itemhave") as IEnumerable;
            if (have == null)
            {
                return -1;
            }

            var idx = 0;
            foreach (var item in have)
            {
                if (item != null && idx == slotIndex)
                {
                    return Convert.ToInt32(GetMember(item, "Pile") ?? 0);
                }

                idx++;
            }

            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>对齐面板：SendArea("获取数据", uid) 请求该角色采集数据，让 AreaInfos 覆盖所有角色。</summary>
    private static void SendAreaData(string uid)
    {
        var cm = GetManagerInstance("CollectionManager");
        if (cm == null)
        {
            return;
        }

        var send = FindMethod(cm.GetType(), "SendArea",
            new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int) });
        if (send == null)
        {
            send = FindMethodByParams(cm.GetType(), "SendArea", 6);
        }

        if (send == null)
        {
            return;
        }

        send.Invoke(cm, new object[] { "获取数据", uid, 0, 0, -1, 0 });
    }

    /// <summary>对齐面板：SendArea("取出物品到账号仓库", uid, index+1, num) 提取到账号银行。</summary>
    private static bool SendTakeOutToAccountBank(string uid, int index, int pile)
    {
        var cm = GetManagerInstance("CollectionManager");
        if (cm == null)
        {
            return false;
        }

        var send = FindMethod(cm.GetType(), "SendArea",
            new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int) });
        if (send == null)
        {
            send = FindMethodByParams(cm.GetType(), "SendArea", 6);
        }

        if (send == null)
        {
            return false;
        }

        // 与 CollectionPanel.OnTakeItemCallback 一致：SendArea(type, uid, index+1, num, -1, 0)
        send.Invoke(cm, new object[] { "取出物品到账号仓库", uid, index + 1, pile, -1, 0 });
        return true;
    }

    // ---------------- 角色收集（同日常 CollectUids） ----------------

    private static List<string> CollectUids()
    {
        var result = new List<string>();

        // 1) 五开 MultiInfo：Online>=1 的本账号角色
        try
        {
            var teamMgr = GetManagerInstance("TeamManager");
            var multi = GetMember(teamMgr, "MultiInfo");
            var players = multi != null ? GetMember(multi, "Players") as IList : null;
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
                    // 五开命中
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
                        // 队伍命中
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

    // ---------------- 事件钩子 ----------------

    private static void TryHookAreaEvent()
    {
        if (_areaHooked)
        {
            return;
        }

        try
        {
            var cm = GetManagerInstance("CollectionManager");
            if (cm == null)
            {
                return;
            }

            var evt = GetMember(cm, "OnEvent");
            if (evt == null)
            {
                return;
            }

            var add = FindMethod(evt.GetType(), "Add", new[] { typeof(Action<object>) });
            if (add == null)
            {
                return;
            }

            _onAreaEvent = OnAreaEvent;
            add.Invoke(evt, new object[] { _onAreaEvent });
            _areaHooked = true;
        }
        catch
        {
            // 钩失败：后台 10 分钟兜底仍会检查
        }
    }

    private static void OnAreaEvent(object obj)
    {
        if (!IsPipelineActive())
        {
            return;
        }

        // 本轮跑着时忽略推送，避免提取回包连环开下一轮导致卡顿
        if (_pipelineRunning || _state != StIdle)
        {
            return;
        }

        // 仅排队；仍受 ExtractCooldownMs 约束（面板强制用 _forceRun）
        _pendingEvent = true;
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

                if (t == null || t == typeof(SeqChapterAreaExtract))
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

                if (t == null || t == typeof(SeqChapterAreaExtract))
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

    private static Type FindLoadedType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(name, false, false);
                if (t != null)
                {
                    return t;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
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

        var f = t.GetField(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        return f?.GetValue(null);
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
