using System;
using System.Reflection;

/// <summary>
/// 百科限帧：侧栏百科点击切换。开启→10FPS+关VSync；再点→还原。
/// 部署为 hotfixdata/SeqChapterWikiFps.dll.bytes（普通版百科闲置时使用；与抓宠/烧卡百科互斥）。
/// </summary>
public static class SeqChapterWikiFps
{
    public const string AssetPath = "hotfixdata/SeqChapterWikiFps.dll.bytes";
    public const string TypeName = "SeqChapterWikiFps";

    public const int LowFrameRate = 10;

    private static bool _limited;
    private static int _savedFrameRate = 60;
    private static int _savedVSync = 1;

    /// <summary>
    /// 百科点击：切换限帧。true=已限帧（Tip 开启）；false=已恢复（Tip 关闭）。
    /// </summary>
    public static bool OnWikiClick()
    {
        if (_limited)
        {
            ApplyFrameRate(_savedFrameRate);
            ApplyVSync(_savedVSync);
            _limited = false;
            return false;
        }

        _savedFrameRate = ReadTargetFrameRate();
        _savedVSync = ReadVSyncCount();
        if (_savedFrameRate < 1)
        {
            _savedFrameRate = 60;
        }

        ApplyFrameRate(LowFrameRate);
        ApplyVSync(0);
        _limited = true;
        return true;
    }

    private static int ReadTargetFrameRate()
    {
        try
        {
            var app = FindTypeAll("UnityEngine.Application");
            var p = app?.GetProperty(
                "targetFrameRate",
                BindingFlags.Static | BindingFlags.Public);
            if (p != null)
            {
                return Convert.ToInt32(p.GetValue(null, null));
            }
        }
        catch
        {
            // fall through
        }

        return 60;
    }

    private static int ReadVSyncCount()
    {
        try
        {
            var qs = FindTypeAll("UnityEngine.QualitySettings");
            var p = qs?.GetProperty(
                "vSyncCount",
                BindingFlags.Static | BindingFlags.Public);
            if (p != null)
            {
                return Convert.ToInt32(p.GetValue(null, null));
            }
        }
        catch
        {
            // fall through
        }

        return 1;
    }

    private static void ApplyFrameRate(int fps)
    {
        try
        {
            var appMgr = FindTypeAll("AppManager");
            if (appMgr != null)
            {
                foreach (var m in appMgr.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "SetFrameRate")
                    {
                        continue;
                    }

                    var ps = m.GetParameters();
                    if (ps.Length == 1)
                    {
                        m.Invoke(null, new object[] { fps });
                        return;
                    }
                }
            }
        }
        catch
        {
            // try UnityEngine
        }

        try
        {
            var app = FindTypeAll("UnityEngine.Application");
            var p = app?.GetProperty(
                "targetFrameRate",
                BindingFlags.Static | BindingFlags.Public);
            p?.SetValue(null, fps, null);
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyVSync(int count)
    {
        try
        {
            var qs = FindTypeAll("UnityEngine.QualitySettings");
            var p = qs?.GetProperty(
                "vSyncCount",
                BindingFlags.Static | BindingFlags.Public);
            p?.SetValue(null, count, null);
        }
        catch
        {
            // ignore
        }
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
}
