using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

/// <summary>
/// 盗贼辅助 DLL。部署为 hotfixdata/SeqChapterAutoSell.dll.bytes
/// 等价背包「出售魔石 / 远程出售魔石」（月卡权益；亦可清无用卡片）。
/// Pause 加载 Bootstrap 后钩 EventCenter.ExitBattle；百科开启时立刻出售一次，
/// 之后每累计 10 次退战再对各角色发「远程出售魔石」。
/// 侧栏百科 Tip 开关。开启时标题追加「 ★盗贼辅助★N次战斗后出售」。
/// 与烧卡/抓宠/九动DLL/桥接互斥；可与 IL 九动共存。不进傻瓜补丁。
/// </summary>
public static class SeqChapterAutoSell
{
    public const string AssetPath = "hotfixdata/SeqChapterAutoSell.dll.bytes";
    public const string ActivityName = "远程出售魔石";
    public const int BattlesPerSell = 10;

    /// <summary>总开关。默认关闭；点百科 Tip 切换。</summary>
    public static volatile bool PipelineEnabled = false;

    private static bool _bootstrapped;
    private static bool _exitHooked;
    private static Action _onExitBattle;
    private static int _battleEndCount;
    private static int _sellRunning;

    public static bool IsPipelineActive()
    {
        if (PipelineEnabled)
        {
            return true;
        }

        return ReadPipelineEnabledFromAnyCopy();
    }

    /// <summary>距下次出售还剩几场（开启时 1..BattlesPerSell）。</summary>
    private static int BattlesUntilSell()
    {
        var done = Volatile.Read(ref _battleEndCount);
        if (done < 0)
        {
            done = 0;
        }

        if (done >= BattlesPerSell)
        {
            return BattlesPerSell;
        }

        return BattlesPerSell - done;
    }

    public static void Bootstrap()
    {
        // 允许重复调用：Pause 过早时 EventCenter 可能尚未就绪，钩失败后须在百科点击时重试
        _bootstrapped = true;
        TryHookExitBattle();
    }

    /// <summary>MapSidebarPanel.OnClickWiki：切换；返回是否开启（IL Tip）。</summary>
    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetPipelineEnabledAllCopies(enable);
        if (enable)
        {
            Interlocked.Exchange(ref _battleEndCount, 0);
            if (!_exitHooked)
            {
                Tip("盗贼辅助：退战钩未挂上，出售可能不会触发");
            }

            // 开启瞬间先清一次；之后仍按每 BattlesPerSell 场退战再卖
            StartSellOnce("开启立即出售");
        }

        RefreshWindowTitle();
        return enable;
    }

    /// <summary>
    /// 与游戏一致：{产品名} {服务器} {角色} Lv.{等级}；
    /// 开启时追加「 ★盗贼辅助★{N}次战斗后出售」。
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
                title = title + " ★盗贼辅助★" + BattlesUntilSell() + "次战斗后出售";
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

    private static object GetStaticMember(string typeName, string name)
    {
        try
        {
            var t = FindType(typeName);
            if (t == null)
            {
                return null;
            }

            var f = t.GetField(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (f != null)
            {
                return f.GetValue(null);
            }

            var p = t.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return p?.GetValue(null, null);
        }
        catch
        {
            return null;
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
            // ignore
        }
    }

    private static void OnBattleExited()
    {
        if (!IsPipelineActive())
        {
            return;
        }

        var n = Interlocked.Increment(ref _battleEndCount);
        RefreshWindowTitle();
        if (n < BattlesPerSell)
        {
            return;
        }

        Interlocked.Exchange(ref _battleEndCount, 0);
        RefreshWindowTitle();
        StartSellOnce(null);
    }

    private static void StartSellOnce(string reasonTip)
    {
        if (Interlocked.CompareExchange(ref _sellRunning, 1, 0) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                var ok = SellForAllPlayers();
                if (ok > 0)
                {
                    Tip(string.IsNullOrEmpty(reasonTip)
                        ? ("盗贼辅助：已远程出售魔石×" + ok)
                        : ("盗贼辅助：" + reasonTip + "×" + ok));
                }
                else
                {
                    Tip("盗贼辅助：出售未发出（无角色或发包失败）");
                }
            }
            catch
            {
                Tip("盗贼辅助：出售异常");
            }
            finally
            {
                Interlocked.Exchange(ref _sellRunning, 0);
            }
        });
        thread.IsBackground = true;
        thread.Name = "SeqChapterAutoSell.Sell";
        thread.Start();
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

    /// <returns>成功发出的角色数。</returns>
    private static int SellForAllPlayers()
    {
        var uids = CollectAllPlayerUids();
        if (uids.Count == 0)
        {
            return 0;
        }

        var ok = 0;
        foreach (var uid in uids)
        {
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            try
            {
                if (SendRemoteSellMoshi(uid))
                {
                    ok++;
                }
            }
            catch
            {
                // ignore per-uid
            }

            try
            {
                Thread.Sleep(80);
            }
            catch
            {
                // ignore
            }
        }

        return ok;
    }

    private static List<string> CollectAllPlayerUids()
    {
        var list = new List<string>();
        try
        {
            var holder = FindType("PlayerDataHolder");
            var getAll = holder?.GetMethod(
                "GetAllPlayers",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var dict = getAll?.Invoke(null, null) as IDictionary;
            if (dict != null)
            {
                foreach (var key in dict.Keys)
                {
                    var uid = Convert.ToString(key) ?? "";
                    if (!string.IsNullOrEmpty(uid) && !list.Contains(uid))
                    {
                        list.Add(uid);
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        if (list.Count == 0)
        {
            var main = GetStaticString("PlayerDataHolder", "MainPlayerUid");
            if (!string.IsNullOrEmpty(main))
            {
                list.Add(main);
            }
        }

        return list;
    }

    /// <summary>
    /// 对齐 ChildBackPackPanel：SendActivity("远程出售魔石", uid, 0, 19)。
    /// 注意：方法签名实为 6 参（后两参有默认值），反射 Invoke 必须补齐全部参数，
    /// 否则 TargetParameterCountException 被吞掉，表现为「Tip 有了但没卖掉」。
    /// </summary>
    private static bool SendRemoteSellMoshi(string uid)
    {
        var actMgr = GetManagerInstance("ActivityManager");
        if (actMgr == null)
        {
            return false;
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
            if (ps.Length >= 4
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(string)
                && ps[2].ParameterType == typeof(int)
                && ps[3].ParameterType == typeof(int))
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
            return false;
        }

        // SendActivity(string type, string KUid, int id=0, int activityId=0, string code="", int index=0)
        var args = BuildMethodArgs(send, new object[] { ActivityName, uid, 0, 19, "", 0 });
        send.Invoke(actMgr, args);
        return true;
    }

    private static object[] BuildMethodArgs(MethodInfo method, object[] preferred)
    {
        var ps = method.GetParameters();
        var args = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (i < preferred.Length && preferred[i] != null)
            {
                var target = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
                args[i] = target.IsInstanceOfType(preferred[i])
                    ? preferred[i]
                    : Convert.ChangeType(preferred[i], target);
                continue;
            }

            if (ps[i].HasDefaultValue)
            {
                args[i] = ps[i].DefaultValue;
            }
            else if (ps[i].ParameterType == typeof(string))
            {
                args[i] = "";
            }
            else if (ps[i].ParameterType.IsValueType)
            {
                args[i] = Activator.CreateInstance(ps[i].ParameterType);
            }
            else
            {
                args[i] = null;
            }
        }

        return args;
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
                    t = asm.GetType("SeqChapterAutoSell", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterAutoSell))
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
                    t = asm.GetType("SeqChapterAutoSell", false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterAutoSell))
                {
                    continue;
                }

                var f = t.GetField(
                    "PipelineEnabled",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null))
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

    private static object GetManagerInstance(string managerName)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type managerType = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.StartsWith("Manager`") && t.IsGenericTypeDefinition)
                        {
                            managerType = t;
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (managerType == null)
                {
                    continue;
                }

                Type inner = null;
                try
                {
                    inner = FindType(managerName);
                }
                catch
                {
                    continue;
                }

                if (inner == null)
                {
                    continue;
                }

                try
                {
                    var closed = managerType.MakeGenericType(inner);
                    var prop = closed.GetProperty(
                        "Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    var inst = prop?.GetValue(null, null);
                    if (inst != null)
                    {
                        return inst;
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

        return null;
    }

    private static Type FindType(string name)
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

                foreach (var x in asm.GetTypes())
                {
                    if (x.Name == name)
                    {
                        return x;
                    }
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
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var f = t.GetField(name, flags);
        if (f != null)
        {
            return f.GetValue(obj);
        }

        var p = t.GetProperty(name, flags);
        return p?.GetValue(obj, null);
    }

    private static string GetStaticString(string typeName, string member)
    {
        try
        {
            var t = FindType(typeName);
            if (t == null)
            {
                return "";
            }

            var f = t.GetField(
                member,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (f != null)
            {
                return Convert.ToString(f.GetValue(null) ?? "") ?? "";
            }

            var p = t.GetProperty(
                member,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return Convert.ToString(p?.GetValue(null, null) ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }
}
