using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// 刷熊男（欧兹那克）自动脚本。部署 hotfixdata/SeqChapterBearSlayer.dll.bytes。
/// 由助手面板「脚本」页「刷熊男」按钮加载运行（RunBearSlayerFromUi / StopFromUi / IsRunning）。
///
/// 流程（状态机 + 主线程 Timer，StepSec=0.4s）：
///   开始 → 丢弃队长背包中含「欧兹那克」的道具 →
///   等待「杀熊者」NPC 刷新（非战斗时每 10s 扫 EntityDataHolder.characterDatas，
///     找名字以「杀熊者」开头的 NPC，记录其坐标）→
///   走到 A(17,15) → 停 2 秒 → 走向 NPC 坐标 B（穿过熊男）→ 检查进战斗；
///   没进战斗 → 回 A 再走一次（循环）→
///   进战斗 → 等战斗结束 → 停 10 秒 → 回「等待杀熊者」循环。
///
/// 约定：
///   - 坐标判断只发寻路不校验到达（按用户要求：A→2s→B→看进战斗，穿身而过）。
///   - 每步 Tip；脚本界面/窗口标题实时显示已刷次数（「刷熊男N次」）。
/// </summary>
public static class SeqChapterBearSlayer
{
    public const string AssetPath = "hotfixdata/SeqChapterBearSlayer.dll.bytes";
    public const string TypeName = "SeqChapterBearSlayer";

    /// <summary>节奏 tick 间隔（秒）。</summary>
    private const float StepSec = 0.4f;

    /// <summary>等待杀熊者 NPC 的扫缓存间隔（秒）：非战斗时 10s 扫一次。</summary>
    private const float ScanIntervalSec = 10f;

    /// <summary>A 点：先走到 (17,15)。</summary>
    private const int PointAX = 17;
    private const int PointAY = 15;

    /// <summary>A→B 之间的停 2 秒（tick 数 = 2.0 / 0.4 = 5）。</summary>
    private const int DelayABTicks = 5;

    /// <summary>走向 B（NPC）后等待进战斗的最大 tick 数（约 5.2s）。</summary>
    private const int WaitBattleTicks = 13;

    /// <summary>战斗结束后的冷却 tick 数（10s）。</summary>
    private const int CooldownTicks = 25;

    /// <summary>每次进战斗判定后标题/界面刷新节流（tick）。</summary>
    private const int TitleRefreshTicks = 5;

    private static bool _bootstrapped;
    private static object _timer;

    private static bool _started;
    private static int _state;
    private static int _tick;
    private static int _lastScanTick;
    private static int _lastTitleTick;
    private static string _uid = "";
    private static string _phase = "";
    private static int _cycleCount;
    private static int _npcObjIndex = -1;
    private static int _npcX;
    private static int _npcY;
    private static bool _wasInBattle;

    // states
    private const int StIdle = 0;
    private const int StBegin = 1;
    private const int StDropItem = 2;
    private const int StWaitNpc = 3;
    private const int StWalkA = 4;
    private const int StDelayAB = 5;
    private const int StWalkB = 6;
    private const int StWaitBattle = 7;
    private const int StBattleDone = 8;
    private const int StCooldown = 9;
    private const int StFail = 10;

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

    /// <summary>助手面板「脚本」页：刷熊男开关。</summary>
    public static bool RunBearSlayerFromUi()
    {
        Bootstrap();
        if (_started)
        {
            return false; // 已运行（UI 走停止分支）
        }

        var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "");
        if (string.IsNullOrEmpty(uid))
        {
            var pd = GetStaticMember("PlayerDataHolder", "playerData");
            uid = Convert.ToString(GetMember(pd, "Uid") ?? "");
        }

        if (string.IsNullOrEmpty(uid))
        {
            Tip("未登录角色，无法刷熊男");
            return false;
        }

        return StartRun(uid);
    }

    public static void StopFromUi()
    {
        AbortRun("已手动停止");
    }

    public static bool IsRunning()
    {
        return _started;
    }

    public static string GetPhase()
    {
        return _phase;
    }

    public static int GetCycleCount()
    {
        return _cycleCount;
    }

    /// <summary>窗口标题后缀（面板统一协调用）。未开启返回空。</summary>
    public static string BuildTitleSuffix()
    {
        return _started ? "刷熊男" + _cycleCount + "次" : "";
    }

    private static bool StartRun(string uid)
    {
        EnsureTimer();
        _uid = uid;
        _cycleCount = 0;
        _npcObjIndex = -1;
        _npcX = 0;
        _npcY = 0;
        _wasInBattle = false;
        _state = StBegin;
        _started = true;
        RefreshTitle();
        return true;
    }

    private static void AbortRun(string reason)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _state = StIdle;
        _phase = "";
        RefreshTitle();
        Tip(reason);
    }

    // ---------------- Timer 驱动 ----------------

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
        if (!_started)
        {
            return;
        }

        _tick++;
        try
        {
            StepMachine();
        }
        catch
        {
            // ignore
        }

        if (_started && _tick - _lastTitleTick >= TitleRefreshTicks)
        {
            _lastTitleTick = _tick;
            RefreshTitle();
        }
    }

    private static void StepMachine()
    {
        switch (_state)
        {
            case StIdle:
                _state = StBegin;
                return;

            case StBegin:
                Tip("刷熊男开始");
                _phase = "丢弃欧兹那克道具";
                _state = StDropItem;
                return;

            case StDropItem:
            {
                var dropped = TryDropBearItems();
                if (!dropped)
                {
                    Tip("未找到含「欧兹那克」的道具（可跳过）");
                }

                _phase = "等待杀熊者";
                _state = StWaitNpc;
                _lastScanTick = _tick;
                return;
            }

            case StWaitNpc:
            {
                // 非战斗时 10s 扫一次缓存
                if (_tick - _lastScanTick >= (int)(ScanIntervalSec / StepSec))
                {
                    _lastScanTick = _tick;
                    if (!IsInBattle() && TryFindBearSlayerNpc(out _npcObjIndex, out _npcX, out _npcY))
                    {
                        Tip("发现杀熊者 (" + _npcX + "," + _npcY + ")，前往触发战斗");
                        _phase = "走向A点";
                        _state = StWalkA;
                        return;
                    }
                }

                return;
            }

            case StWalkA:
            {
                WalkSameMap(PointAX, PointAY);
                _phase = "A点停2秒";
                _state = StDelayAB;
                return;
            }

            case StDelayAB:
            {
                if (IsInBattle())
                {
                    EnterBattleDetected();
                    return;
                }

                _delayCounter++;
                if (_delayCounter >= DelayABTicks)
                {
                    _delayCounter = 0;
                    if (!TryGetNpcTarget(out _npcX, out _npcY))
                    {
                        Tip("杀熊者不见了，重新等待");
                        _phase = "等待杀熊者";
                        _state = StWaitNpc;
                        _lastScanTick = _tick;
                        return;
                    }

                    _phase = "走向杀熊者";
                    _state = StWalkB;
                }

                return;
            }

            case StWalkB:
            {
                WalkSameMap(_npcX, _npcY);
                _phase = "等待进战斗";
                _state = StWaitBattle;
                _battleWaitTicks = 0;
                return;
            }

            case StWaitBattle:
            {
                if (IsInBattle())
                {
                    EnterBattleDetected();
                    return;
                }

                _battleWaitTicks++;
                if (_battleWaitTicks >= WaitBattleTicks)
                {
                    // 没进战斗：回 A 再走一次
                    Tip("未触发战斗，回 A 再走");
                    _phase = "走向A点";
                    _state = StWalkA;
                }

                return;
            }

            case StBattleDone:
            {
                if (IsInBattle())
                {
                    return; // 还在战斗结算
                }

                _cycleCount++;
                Tip("战斗结束，已刷 " + _cycleCount + " 次，冷却10秒");
                _phase = "冷却10秒";
                _cooldownTicks = 0;
                _state = StCooldown;
                RefreshTitle();
                return;
            }

            case StCooldown:
            {
                _cooldownTicks++;
                if (_cooldownTicks >= CooldownTicks)
                {
                    _npcObjIndex = -1;
                    _phase = "等待杀熊者";
                    _state = StWaitNpc;
                    _lastScanTick = _tick;
                }

                return;
            }

            case StFail:
            {
                // 人工处理
                return;
            }
        }
    }

    private static int _delayCounter;
    private static int _battleWaitTicks;
    private static int _cooldownTicks;

    private static void EnterBattleDetected()
    {
        _phase = "战斗中";
        _wasInBattle = true;
        _state = StBattleDone;
    }

    private static bool IsInBattle()
    {
        try
        {
            return Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 丢弃欧兹那克道具 ----------------

    /// <summary>丢弃队长背包中含「欧兹那克」的道具。返回是否丢弃了至少一件。</summary>
    private static bool TryDropBearItems()
    {
        try
        {
            var items = GetItemDatas(_uid);
            if (items == null)
            {
                return false;
            }

            var itemMgr = GetManagerInstance("ItemManager");
            if (itemMgr == null)
            {
                return false;
            }

            var send = FindDropMethod(itemMgr.GetType());
            if (send == null)
            {
                return false;
            }

            var dropped = false;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
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

                var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                if (name.IndexOf("欧兹那克", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                send.Invoke(itemMgr, new object[] { "丢弃物品", i, 1, _uid });
                dropped = true;
                Tip("已丢弃: " + name);
            }

            return dropped;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo FindDropMethod(Type itemMgrType)
    {
        foreach (var m in itemMgrType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendBackPackMessage")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 4
                && ps[0].ParameterType.FullName == "System.String"
                && ps[1].ParameterType.FullName == "System.Int32"
                && ps[2].ParameterType.FullName == "System.Int32"
                && ps[3].ParameterType.FullName == "System.String")
            {
                return m;
            }
        }

        return null;
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

    // ---------------- 扫描杀熊者 NPC ----------------

    /// <summary>扫描 EntityDataHolder.characterDatas，找名字以「杀熊者」开头的 NPC。</summary>
    private static bool TryFindBearSlayerNpc(out int objIndex, out int x, out int y)
    {
        objIndex = -1;
        x = 0;
        y = 0;

        try
        {
            var dict = GetStaticMember("EntityDataHolder", "characterDatas") as IDictionary;
            if (dict == null)
            {
                return false;
            }

            foreach (DictionaryEntry entry in dict)
            {
                var cd = entry.Value;
                if (cd == null)
                {
                    continue;
                }

                if (!IsNpc(cd))
                {
                    continue;
                }

                var name = Convert.ToString(GetMember(cd, "name") ?? "") ?? "";
                if (!name.StartsWith("杀熊者", StringComparison.Ordinal))
                {
                    continue;
                }

                objIndex = Convert.ToInt32(GetMember(cd, "objindex") ?? 0);
                x = Convert.ToInt32(GetMember(cd, "x") ?? 0);
                y = Convert.ToInt32(GetMember(cd, "y") ?? 0);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool IsNpc(object cd)
    {
        try
        {
            var typeVal = Convert.ToInt32(GetMember(cd, "charEntityType") ?? 0);
            // 1=Player 2=Enemy 3=Pet 997=PlayerNpc 998=PlayerPetNpc 999=Vender
            if (typeVal == 1 || typeVal == 2 || typeVal == 3
                || typeVal == 997 || typeVal == 998 || typeVal == 999)
            {
                return false;
            }

            return typeVal != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNpcTarget(out int x, out int y)
    {
        x = _npcX;
        y = _npcY;
        return _npcObjIndex >= 0;
    }

    // ---------------- 寻路 ----------------

    /// <summary>同图寻路到 (x,y)：WalkSystem.MoveTo(Vector2Int)。</summary>
    private static void WalkSameMap(int x, int y)
    {
        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var walk = pm == null ? null : (GetMember(pm, "walkSystem") ?? GetMember(pm, "m_WalkSystem"));
            if (walk == null)
            {
                return;
            }

            var vec = MakeVector2Int(x, y);
            if (vec == null)
            {
                return;
            }

            InvokeWalkMoveToVector(walk, vec);
        }
        catch
        {
            // ignore
        }
    }

    private static object MakeVector2Int(int x, int y)
    {
        var t = FindType("Vector2Int");
        if (t == null)
        {
            return null;
        }

        foreach (var ctor in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var ps = ctor.GetParameters();
            if (ps.Length != 2)
            {
                continue;
            }

            try
            {
                return ctor.Invoke(new object[] { x, y });
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static object InvokeWalkMoveToVector(object walkSys, object vec)
    {
        foreach (var m in walkSys.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "MoveTo")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType.Name != "Vector2Int")
            {
                continue;
            }

            try
            {
                var args = ps.Length == 1
                    ? new[] { vec }
                    : ps.Length == 2
                        ? new object[] { vec, null }
                        : new object[] { vec, null, false };
                return m.Invoke(walkSys, args);
            }
            catch
            {
                // try next overload
            }
        }

        return null;
    }

    // ---------------- 标题刷新 ----------------

    private static void RefreshTitle()
    {
        try
        {
            var panelType = FindLoadedType("SeqChapterTestUi");
            if (panelType != null)
            {
                var m = panelType.GetMethod(
                    "RefreshTitleFromFeature",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                m?.Invoke(null, null);
                return;
            }
        }
        catch
        {
            // fall through
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

            var suffix = BuildTitleSuffix();
            if (!string.IsNullOrEmpty(suffix))
            {
                title = title + " " + suffix;
            }

            var appMgr = FindType("AppManager");
            var setTitle = appMgr?.GetMethod(
                "SetWindowTitle",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
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
            var appType = FindType("UnityEngine.Application");
            var prop = appType?.GetProperty(
                "productName", BindingFlags.Public | BindingFlags.Static);
            return Convert.ToString(prop?.GetValue(null, null) ?? "") ?? "";
        }
        catch
        {
            return "";
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

    // ---------------- 通用反射辅助 ----------------

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
}
