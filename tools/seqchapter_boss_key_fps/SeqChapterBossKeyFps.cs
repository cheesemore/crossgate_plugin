using System;
using System.Reflection;

/// <summary>
/// 切后台 / 老板键限帧：窗口失焦或 BossKey 隐藏时 targetFrameRate=30 且关 VSync；恢复时还原。
/// 部署为 hotfixdata/SeqChapterBossKeyFps.dll.bytes；由 HotfixEntry.Update 每帧 Invoke Tick。
/// 30FPS（2026-08 起由 10 调升）：战斗计时为真实时间驱动（Observable.Interval + WaitForSeconds），
/// 低帧率只带来 WaitForSeconds 半帧舍入误差；30FPS 将演出累计误差降到 10FPS 的约 1/3，
/// 画面卡顿明显改善，仍比 60FPS 省一半资源。
/// </summary>
public static class SeqChapterBossKeyFps
{
    public const string AssetPath = "hotfixdata/SeqChapterBossKeyFps.dll.bytes";
    public const string TypeName = "SeqChapterBossKeyFps";

    /// <summary>切后台 / 老板键隐藏时目标帧率（30FPS：演出误差小，画面较流畅；10FPS 会轻微拉长战斗演出）。</summary>
    public const int HiddenFrameRate = 30;

    private static bool _applied;
    private static int _savedFrameRate = 60;
    private static int _savedVSync = 1;
    private static FieldInfo _isHideField;
    private static bool _resolvedHideField;
    private static PropertyInfo _isFocusedProp;
    private static bool _resolvedFocusedProp;

    /// <summary>HotfixEntry.Update 每帧调用（加载后）。</summary>
    public static void Tick()
    {
        try
        {
            var throttle = IsBossKeyHidden() || !IsApplicationFocused();
            if (throttle)
            {
                if (!_applied)
                {
                    _savedFrameRate = ReadTargetFrameRate();
                    _savedVSync = ReadVSyncCount();
                    if (_savedFrameRate < 1)
                    {
                        _savedFrameRate = 60;
                    }

                    ApplyFrameRate(HiddenFrameRate);
                    ApplyVSync(0);
                    _applied = true;
                }
            }
            else if (_applied)
            {
                ApplyFrameRate(_savedFrameRate);
                ApplyVSync(_savedVSync);
                _applied = false;
            }
        }
        catch
        {
            // 忽略单帧异常，避免拖垮 Update
        }
    }

    private static bool IsApplicationFocused()
    {
        if (!_resolvedFocusedProp)
        {
            _resolvedFocusedProp = true;
            var app = FindTypeAll("UnityEngine.Application");
            _isFocusedProp = app?.GetProperty(
                "isFocused",
                BindingFlags.Static | BindingFlags.Public);
        }

        if (_isFocusedProp == null)
        {
            // 读不到焦点时不当作后台，避免误限帧
            return true;
        }

        try
        {
            return Convert.ToBoolean(_isFocusedProp.GetValue(null, null));
        }
        catch
        {
            return true;
        }
    }

    private static bool IsBossKeyHidden()
    {
        if (!_resolvedHideField)
        {
            _resolvedHideField = true;
            var boss = FindTypeAll("BossKey");
            _isHideField = boss?.GetField(
                "isHide",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }

        if (_isHideField == null)
        {
            return false;
        }

        try
        {
            return Convert.ToBoolean(_isHideField.GetValue(null));
        }
        catch
        {
            return false;
        }
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
            // try UnityEngine path
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
                    // next assembly
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
