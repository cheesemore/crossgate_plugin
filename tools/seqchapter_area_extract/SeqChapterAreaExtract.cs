using System;
using System.Collections;
using System.Reflection;
using System.Threading;

/// <summary>
/// 采集自动提取 DLL。部署为 hotfixdata/SeqChapterAreaExtract.dll.bytes
/// 由助手面板「战斗」页开关（SetEnabled），与战斗模式共存（不互斥）；
/// 「脚本」页有「立刻提取」按钮（ExtractNowFromUi，绕过冷却强制提取一次）。
/// 不需要自动采集：只监控已采集物品，已采集物共 5 格，单格达到 999 时
/// 对该格发 SendArea("取出物品到账号仓库", uid, index+1, pile) 提取到账号银行。
/// 每次提取最多尝试 2 次（1 次 + 等待服务端回推后仍满则重试 1 次）。
/// 触发：启动即检 + 每次 CollectionManager.OnEvent（服务端采集数据推送）即检
///       + 每 10 分钟后台兜底。采集很慢（一格最快10分钟+），无需高频扫描。
/// 标题由助手面板统一协调：BuildTitleSuffix 返回「 ★自动提取★X格已满」。
/// </summary>
public static class SeqChapterAreaExtract
{
    public const string AssetPath = "hotfixdata/SeqChapterAreaExtract.dll.bytes";
    public const string TypeName = "SeqChapterAreaExtract";

    /// <summary>单格触发提取的堆叠数（满格）。</summary>
    public const int FullPile = 999;

    /// <summary>后台兜底扫描周期（毫秒）：10 分钟。</summary>
    public const int ScanIntervalMs = 10 * 60 * 1000;

    /// <summary>提取冷却（毫秒）：距上次提取不足此值则不再次提取，防重复发。</summary>
    public const int ExtractCooldownMs = 60_000;

    /// <summary>每次提取最多尝试次数（1 次 + 1 次重试）。</summary>
    public const int MaxAttemptsPerSlot = 2;

    /// <summary>提取后等待服务端回推数据再校验的毫秒数。</summary>
    public const int RetryCheckDelayMs = 3000;

    /// <summary>总开关。默认关闭；面板战斗页切换。</summary>
    public static volatile bool PipelineEnabled = false;

    private static bool _bootstrapped;
    private static int _threadStarted;
    private static Thread _worker;
    private static volatile int _workerStop;

    private static long _lastExtractMs;
    private static int _fullSlotCount;
    private static int _extractRunning;

    private static bool _areaHooked;
    private static Action<object> _onAreaEvent;

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
        EnsureWorker();
    }

    /// <summary>面板战斗页：显式开/关。开启时立即检查一次。</summary>
    public static void SetEnabled(bool enable)
    {
        Bootstrap();
        SetPipelineEnabledAllCopies(enable);
        if (enable)
        {
            TryHookAreaEvent();
            EnsureWorker();
            CheckAndExtractAll();
        }

        NotifyTitleRefresh();
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

    /// <summary>进战斗标题协调用：留给面板/其他 DLL 合并（计数挂机用）。</summary>
    public static int GetFullSlotCount()
    {
        return _fullSlotCount;
    }

    private static void EnsureWorker()
    {
        if (_threadStarted != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _threadStarted, 1) == 1)
        {
            return;
        }

        _worker = new Thread(WorkerLoop);
        _worker.IsBackground = true;
        _worker.Name = "SeqChapterAreaExtract.Worker";
        _worker.Start();
    }

    private static void WorkerLoop()
    {
        while (Volatile.Read(ref _workerStop) == 0)
        {
            try
            {
                if (IsPipelineActive())
                {
                    CheckAndExtractAll();
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                Thread.Sleep(ScanIntervalMs);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// 钩 CollectionManager.OnEvent（MEvent&lt;object&gt;）：服务端采集数据推送时
    /// 派发 Proto_SC_AREA，这里收到即检查一次。
    /// </summary>
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

        CheckAndExtractAll();
    }

    /// <summary>读所有角色 AreaInfos，对单格 Pile>=999 的格子逐个提取；返回满格总数。</summary>
    private static int CheckAndExtractAll()
    {
        return CheckAndExtractAllInternal(false);
    }

    /// <summary>立刻提取（脚本页按钮）：绕过冷却，强制扫描提取一次；返回满格总数。</summary>
    public static int ExtractNowFromUi()
    {
        Bootstrap();
        try
        {
            var cm = GetManagerInstance("CollectionManager");
            if (cm == null)
            {
                return 0;
            }
        }
        catch
        {
            // ignore
        }

        var full = CheckAndExtractAllInternal(true);
        Tip(full > 0 ? "立即提取：发现 " + full + " 个满格，已提取到账号银行" : "立即提取：当前没有满格采集物");
        return full;
    }

    private static int CheckAndExtractAllInternal(bool force)
    {
        var cm = GetManagerInstance("CollectionManager");
        if (cm == null)
        {
            return 0;
        }

        var areaInfos = GetMember(cm, "AreaInfos") as IDictionary;
        if (areaInfos == null)
        {
            return 0;
        }

        // 收集所有满格 (uid, index, pile)
        var fullSlots = new System.Collections.Generic.List<object[]>();
        var full = 0;
        foreach (var key in areaInfos.Keys)
        {
            var uid = Convert.ToString(key) ?? "";
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            object area = null;
            try
            {
                area = areaInfos[key];
            }
            catch
            {
                continue;
            }

            if (area == null)
            {
                continue;
            }

            var have = GetMember(area, "Itemhave") as IEnumerable;
            if (have == null)
            {
                continue;
            }

            var idx = 0;
            foreach (var item in have)
            {
                var pile = Convert.ToInt32(GetMember(item, "Pile") ?? 0);
                if (pile >= FullPile)
                {
                    fullSlots.Add(new object[] { uid, idx, pile });
                    full++;
                }

                idx++;
            }
        }

        _fullSlotCount = full;
        if (fullSlots.Count > 0)
        {
            ExtractFullSlots(fullSlots, force);
        }

        NotifyTitleRefresh();
        return full;
    }

    private static void ExtractFullSlots(System.Collections.Generic.List<object[]> fullSlots, bool force)
    {
        if (Interlocked.CompareExchange(ref _extractRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = NowMs();
            if (!force && now - _lastExtractMs < ExtractCooldownMs)
            {
                return;
            }

            var sent = 0;
            foreach (var slot in fullSlots)
            {
                var uid = Convert.ToString(slot[0]) ?? "";
                var index = Convert.ToInt32(slot[1]);
                var pile = Convert.ToInt32(slot[2]);
                if (SendTakeOutToAccountBank(uid, index, pile))
                {
                    sent++;
                }

                try
                {
                    Thread.Sleep(150);
                }
                catch
                {
                    // ignore
                }
            }

            if (sent > 0)
            {
                Interlocked.Exchange(ref _lastExtractMs, NowMs());
                // 等待服务端回推采集数据，对仍满的格子重试一次
                TrySleep(RetryCheckDelayMs);
                var retried = RetryStillFullSlots(fullSlots);
                if (retried > 0)
                {
                    Tip("自动提取：已提取 " + sent + " 格到账号银行（重试 " + retried + " 格）");
                }
                else
                {
                    Tip("自动提取：已提取 " + sent + " 格采集物到账号银行");
                }
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            Interlocked.Exchange(ref _extractRunning, 0);
        }
    }

    /// <summary>提取后服务端已回推，仍满（≥999）的格子重试一次。</summary>
    private static int RetryStillFullSlots(System.Collections.Generic.List<object[]> attemptedSlots)
    {
        var retried = 0;
        foreach (var slot in attemptedSlots)
        {
            var uid = Convert.ToString(slot[0]) ?? "";
            var index = Convert.ToInt32(slot[1]);
            var pile = Convert.ToInt32(slot[2]);

            // 读取最新 Pile，仍满则再发一次
            var cur = ReadSlotPile(uid, index);
            if (cur < FullPile)
            {
                continue;
            }

            if (SendTakeOutToAccountBank(uid, index, cur))
            {
                retried++;
            }

            try
            {
                Thread.Sleep(150);
            }
            catch
            {
                // ignore
            }
        }

        return retried;
    }

    /// <summary>读某角色某格最新 Pile；读取失败返回 -1。</summary>
    private static int ReadSlotPile(string uid, int slotIndex)
    {
        try
        {
            var cm = GetManagerInstance("CollectionManager");
            if (cm == null)
            {
                return -1;
            }

            var areaInfos = GetMember(cm, "AreaInfos") as IDictionary;
            if (areaInfos == null)
            {
                return -1;
            }

            object area = null;
            try
            {
                area = areaInfos[uid];
            }
            catch
            {
                return -1;
            }

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
                if (idx == slotIndex)
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

    private static void TrySleep(int ms)
    {
        try
        {
            Thread.Sleep(ms);
        }
        catch
        {
            // ignore
        }
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

        send.Invoke(cm, new object[] { "取出物品到账号仓库", uid, index + 1, pile, -1, 0 });
        return true;
    }

    /// <summary>窗口标题只保留计数挂机；自动提取不再刷新标题。</summary>
    private static void NotifyTitleRefresh()
    {
        // 标题不再受自动提取影响（需求变更：只保留自动挂机标题）
    }

    /// <summary>无面板时的自刷新（仅本 DLL 后缀）。</summary>
    private static void RefreshWindowTitleSelf()
    {
        try
        {
            var baseTitle = BuildBaseTitle();
            if (string.IsNullOrEmpty(baseTitle))
            {
                return;
            }

            var suffix = BuildTitleSuffix();
            SetTitle(baseTitle + (string.IsNullOrEmpty(suffix) ? "" : " " + suffix));
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildBaseTitle()
    {
        var product = GetUnityProductName();
        if (string.IsNullOrEmpty(product))
        {
            return "";
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

        return string.IsNullOrEmpty(roleName)
            ? product
            : string.Format("{0} {1} {2} Lv.{3}", product, server, roleName, level);
    }

    private static void SetTitle(string title)
    {
        try
        {
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
            foreach (var m in notify.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Tip")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                {
                    tip = m;
                    if (ps.Length == 2)
                    {
                        break;
                    }
                }
            }

            if (tip == null)
            {
                return;
            }

            var ps2 = tip.GetParameters();
            if (ps2.Length >= 2)
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
