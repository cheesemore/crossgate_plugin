using System;
using System.Reflection;

/// <summary>
/// 傻瓜换装补丁：侧栏百科点击循环导入 4 套装备（1→2→3→4→1）。
/// 无面板。依赖已部署的 SeqChapterBattleAppear。独立维护。
/// </summary>
public static class SeqChapterWikiSkinCycle
{
    public const string AssetPath = "hotfixdata/SeqChapterWikiSkinCycle.dll.bytes";
    public const string TypeName = "SeqChapterWikiSkinCycle";
    public const string AppearAssetPath = "hotfixdata/SeqChapterBattleAppear.dll.bytes";
    public const string AppearTypeName = "SeqChapterBattleAppear";
    public const int PresetCount = 4;

    private static int _nextPreset = 1;

    /// <summary>
    /// 百科点击：切换并导入下一套装备。始终返回 true（不走原生 tipOn/tipOff）。
    /// </summary>
    public static bool OnWikiClick()
    {
        try
        {
            EnsureAppearLoaded();
            var t = FindTypeAll(AppearTypeName);
            if (t == null)
            {
                Tip("换装钩子未加载（缺 SeqChapterBattleAppear）");
                return true;
            }

            var import = t.GetMethod(
                "ImportPreset",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int) },
                null);
            if (import == null)
            {
                Tip("换装钩子无 ImportPreset");
                return true;
            }

            var index = _nextPreset;
            if (index < 1 || index > PresetCount)
            {
                index = 1;
            }

            var err = import.Invoke(null, new object[] { index }) as string;
            if (!string.IsNullOrEmpty(err))
            {
                Tip(err);
                return true;
            }

            Tip("已切换装备套装" + index);
            _nextPreset = index >= PresetCount ? 1 : index + 1;
            return true;
        }
        catch (Exception ex)
        {
            Tip("切换装备失败: " + RootMessage(ex));
            return true;
        }
    }

    private static void EnsureAppearLoaded()
    {
        if (FindTypeAll(AppearTypeName) != null)
        {
            return;
        }

        TryLoadExternalDll(AppearAssetPath);
    }

    private static void TryLoadExternalDll(string assetPath)
    {
        try
        {
            var fileUtil = FindTypeAll("FileUtil");
            if (fileUtil == null)
            {
                return;
            }

            byte[] bytes = null;
            foreach (var name in new[] { "LoadBytes", "LoadBytesFromHotfixAssets" })
            {
                var load = fileUtil.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                if (load == null)
                {
                    continue;
                }

                bytes = load.Invoke(null, new object[] { assetPath }) as byte[];
                if (bytes != null && bytes.Length > 0)
                {
                    break;
                }
            }

            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            Assembly.Load(bytes);
        }
        catch
        {
            // ignore
        }
    }

    private static void Tip(string msg)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            var tip = notify?.GetType().GetMethod(
                "Tip",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            tip?.Invoke(notify, new object[] { msg, false });
        }
        catch
        {
            // ignore
        }
    }

    private static object GetManagerInstance(string typeName)
    {
        try
        {
            var t = FindTypeAll(typeName);
            if (t == null)
            {
                return null;
            }

            var p = t.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null)
            {
                return p.GetValue(null, null);
            }

            // Manager<T>.get_Instance
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type managerOpen = null;
                try
                {
                    foreach (var cand in asm.GetTypes())
                    {
                        if (cand.Name == "Manager`1" || cand.FullName == "Manager`1")
                        {
                            managerOpen = cand;
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (managerOpen == null)
                {
                    continue;
                }

                try
                {
                    var closed = managerOpen.MakeGenericType(t);
                    var gp = closed.GetProperty(
                        "Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (gp != null)
                    {
                        return gp.GetValue(null, null);
                    }

                    var gm = closed.GetMethod(
                        "get_Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (gm != null)
                    {
                        return gm.Invoke(null, null);
                    }
                }
                catch
                {
                    // next
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static Type FindTypeAll(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        try
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
                    // next
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            return Type.GetType(typeName, false);
        }
        catch
        {
            return null;
        }
    }

    private static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException != null)
        {
            cur = cur.InnerException;
        }

        return cur.Message ?? ex.GetType().Name;
    }
}
