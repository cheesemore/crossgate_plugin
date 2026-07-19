using System;
using System.Collections;
using System.IO;
using System.Reflection;

/// <summary>
/// 神奇九动 DLL。部署为 hotfixdata/SeqChapterNineAction.dll.bytes
/// Pause 加载 Bootstrap；OnCommandPlayerCallback 末尾同步调用 OnCommandPlayerEnd。
/// </summary>
public static class SeqChapterNineAction
{
    public const string AssetPath = "hotfixdata/SeqChapterNineAction.dll.bytes";

    private static bool _bootstrapped;
    private static string _statusPath;
    private static FieldInfo _acountListField;
    private static FieldInfo _battleModeField;

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
            CacheHotfixFields();
            WriteStatus("mounted", "sync_hook_ok");
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

    /// <summary>协议 69 回调末尾入口（由 hotfix IL 反射 Invoke）。</summary>
    public static void OnCommandPlayerEnd()
    {
        try
        {
            if (!_bootstrapped)
            {
                Bootstrap();
            }

            ExpandAccountList();
        }
        catch (Exception ex)
        {
            try
            {
                WriteStatus("expand_error", ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>把 AcountList 从 P1..PN 扩为 P1..P(N-1) P1..P(N-1) PN。</summary>
    public static void ExpandAccountList()
    {
        if (!ShouldExpandInCurrentBattleMode())
        {
            return;
        }

        var list = GetAcountList();
        if (list == null || list.Count <= 1)
        {
            return;
        }

        var n = list.Count;
        if (LooksAlreadyExpanded(list))
        {
            return;
        }

        // 先拷贝前 N-1，避免在同一 IList 上边读边 Add
        var head = n - 1;
        var prefix = new object[head];
        for (var i = 0; i < head; i++)
        {
            prefix[i] = list[i];
        }

        var last = list[n - 1];
        list.RemoveAt(n - 1);
        for (var i = 0; i < head; i++)
        {
            list.Add(prefix[i]);
        }

        list.Add(last);
        WriteStatus("expanded", "n=" + n + " -> " + list.Count);
    }

    private static bool ShouldExpandInCurrentBattleMode()
    {
        try
        {
            CacheHotfixFields();
            if (_battleModeField == null)
            {
                return true;
            }

            var mode = Convert.ToInt32(_battleModeField.GetValue(null));
            // WATCH=3, PVP_WATCH=8, REPLAY=9（与反编译 beq 常量一致）
            if (mode == 3 || mode == 8 || mode == 9)
            {
                return false;
            }
        }
        catch
        {
            // 读模式失败则仍尝试扩
        }

        return true;
    }

    private static bool LooksAlreadyExpanded(IList list)
    {
        var count = list.Count;
        if (count < 3 || (count % 2) == 0)
        {
            return false;
        }

        var n = (count + 1) / 2;
        if (n < 2 || 2 * n - 1 != count)
        {
            return false;
        }

        for (var i = 0; i < n - 1; i++)
        {
            if (!Equals(list[i], list[n - 1 + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void CacheHotfixFields()
    {
        if (_acountListField != null)
        {
            return;
        }

        var hotfix = FindHotfixAssembly();
        if (hotfix == null)
        {
            return;
        }

        var holder = hotfix.GetType("BattleDataHolder");
        if (holder == null)
        {
            return;
        }

        _acountListField = holder.GetField("AcountList", BindingFlags.Public | BindingFlags.Static);
        _battleModeField = holder.GetField("battleModeFlag", BindingFlags.Public | BindingFlags.Static);
    }

    private static IList GetAcountList()
    {
        CacheHotfixFields();
        if (_acountListField == null)
        {
            return null;
        }

        return _acountListField.GetValue(null) as IList;
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

    private static void EnsureStatusPath()
    {
        if (!string.IsNullOrEmpty(_statusPath))
        {
            return;
        }

        _statusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seqchapter_helper",
            "nine_action.status");
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
