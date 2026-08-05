using System;
using System.Reflection;

/// <summary>
/// 计数挂机 DLL。部署为 hotfixdata/SeqChapterCountFarm.dll.bytes
/// 由助手面板战斗模式页「计数挂机」互斥切换（SetEnabled）。
/// 开启后：监听 EventCenter.EnterBattle，每次进战斗 +1；
/// 窗口标题追加「 ★挂机中★ 已战斗N次」。关闭时清零并恢复原标题。
/// 仅计数与标题，不拦截任何战斗动作，可与抓宠/烧卡等同开。
/// </summary>
public static class SeqChapterCountFarm
{
    public const string AssetPath = "hotfixdata/SeqChapterCountFarm.dll.bytes";
    public const string TypeName = "SeqChapterCountFarm";

    /// <summary>计数挂机总开关。默认关闭；面板战斗模式页切换。</summary>
    public static volatile bool PipelineEnabled = false;

    private static bool _bootstrapped;
    private static bool _enterHooked;
    private static Action _onEnterBattle;
    private static int _battleCount;

    public static bool IsPipelineActive()
    {
        if (PipelineEnabled)
        {
            return true;
        }

        return ReadPipelineEnabledFromAnyCopy();
    }

    /// <summary>面板标题协调用：后缀，未开启返回空。</summary>
    public static string BuildTitleSuffix()
    {
        if (!IsPipelineActive())
        {
            return "";
        }

        return "★挂机中★ 已战斗" + _battleCount + "次";
    }

    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        TryHookEnterBattle();
    }

    /// <summary>面板/外部：显式开/关（互斥模式用）。关闭时清零计数并恢复标题。</summary>
    public static void SetEnabled(bool enable)
    {
        Bootstrap();
        SetPipelineEnabledAllCopies(enable);
        if (!enable)
        {
            _battleCount = 0;
        }

        RefreshWindowTitle();
    }

    /// <summary>侧栏百科切换（兼容旧入口）。</summary>
    public static bool OnWikiClick()
    {
        Bootstrap();
        var enable = !IsPipelineActive();
        SetEnabled(enable);
        return enable;
    }

    /// <summary>进战斗：计数 +1 并刷新标题。</summary>
    private static void OnBattleEntered()
    {
        if (!IsPipelineActive())
        {
            return;
        }

        _battleCount++;
        RefreshWindowTitle();
    }

    /// <summary>
    /// 刷新标题：优先通知助手面板统一协调（合并计数挂机+自动提取等后缀），
    /// 面板不在时自己写（与游戏一致：{产品名} {服务器} {角色} Lv.{等级}）。
    /// </summary>
    private static void RefreshWindowTitle()
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

            if (IsPipelineActive())
            {
                title = title + " ★挂机中★ 已战斗" + _battleCount + "次";
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

    private static void TryHookEnterBattle()
    {
        if (_enterHooked)
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

            var enterEv = GetMember(instance, "EnterBattle");
            if (enterEv == null)
            {
                return;
            }

            _onEnterBattle = OnBattleEntered;
            var add = enterEv.GetType().GetMethod(
                "Add",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Action) },
                null);
            if (add == null)
            {
                return;
            }

            add.Invoke(enterEv, new object[] { _onEnterBattle });
            _enterHooked = true;
        }
        catch
        {
            // 进战钩失败：标题仍可手动刷新
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
                    t = asm.GetType(TypeName, false, false);
                }
                catch
                {
                    continue;
                }

                if (t == null || t == typeof(SeqChapterCountFarm))
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

                if (t == null || t == typeof(SeqChapterCountFarm))
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

    private static Type FindLoadedType(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(typeName, false, false);
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
}
