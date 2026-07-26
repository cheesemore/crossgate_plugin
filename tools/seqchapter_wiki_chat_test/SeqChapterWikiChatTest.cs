using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

/// <summary>
/// 百科按钮测试：点击侧栏百科后，仅向 TEAM（队伍）频道发一条探测消息。
/// 不碰世界/附近/星球/综合/家族/好友/系统等其它频道。
/// 部署为 hotfixdata/SeqChapterWikiChatTest.dll.bytes。
/// </summary>
public static class SeqChapterWikiChatTest
{
    public const string AssetPath = "hotfixdata/SeqChapterWikiChatTest.dll.bytes";

    private const string TeamChannelField = "PROTO_CHANNEL_TYPE_TEAM";

    public static void Bootstrap()
    {
        // 点击时再跑；保留空 Bootstrap 以便加载器复用。
    }

    /// <summary>MapSidebarPanel.OnClickWiki 入口。</summary>
    public static void OnWikiClick()
    {
        try
        {
            var report = new StringBuilder();
            report.AppendLine("[百科测] 仅 TEAM 频道");

            var uid = GetUid();
            report.AppendLine("uid=" + (string.IsNullOrEmpty(uid) ? "(empty)" : uid));

            if (TrySendTeamChat(uid, report))
            {
                report.AppendLine("结果: 已发包");
            }
            else
            {
                report.AppendLine("结果: 失败");
            }

            ShowLocal(report.ToString());
        }
        catch (Exception ex)
        {
            ShowLocal("[百科测] 异常: " + (ex.InnerException ?? ex).Message);
        }
    }

    private static bool TrySendTeamChat(string uid, StringBuilder report)
    {
        try
        {
            var chatMgr = GetManagerInstance("ChatManager");
            if (chatMgr == null)
            {
                report.AppendLine("ChatManager 缺失");
                return false;
            }

            MethodInfo send = null;
            foreach (var m in chatMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendChatMessage")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 4
                    && ps[0].ParameterType == typeof(string)
                    && ps[3].ParameterType == typeof(bool))
                {
                    send = m;
                    break;
                }
            }

            if (send == null)
            {
                report.AppendLine("SendChatMessage 无 4 参重载");
                return false;
            }

            var channelType = FindType("PROTO_CHANNEL_TYPE");
            var teamField = channelType?.GetField(TeamChannelField, BindingFlags.Public | BindingFlags.Static);
            if (teamField == null)
            {
                report.AppendLine("PROTO_CHANNEL_TYPE_TEAM 缺失");
                return false;
            }

            var channel = teamField.GetValue(null);
            send.Invoke(chatMgr, new object[] { "[百科测]TEAM", channel, uid, false });
            report.AppendLine("SendChatMessage TEAM: ok");
            return true;
        }
        catch (Exception ex)
        {
            report.AppendLine("SendChatMessage TEAM: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    private static void ShowLocal(string text)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            foreach (var name in new[] { "ShowMessageBox", "ShowMessage", "ShowTip", "ShowTips", "ShowMsg" })
            {
                foreach (var m in notify.GetType().GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != name)
                    {
                        continue;
                    }

                    var ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        {
                            m.Invoke(notify, new object[] { text });
                            return;
                        }

                        if (ps.Length == 2
                            && ps[0].ParameterType == typeof(string)
                            && ps[1].ParameterType == typeof(string))
                        {
                            m.Invoke(notify, new object[] { text, "百科测" });
                            return;
                        }

                        if (ps.Length >= 3
                            && ps[0].ParameterType == typeof(string)
                            && ps[1].ParameterType == typeof(string))
                        {
                            var args = new object[ps.Length];
                            args[0] = text;
                            args[1] = "百科测";
                            for (var i = 2; i < ps.Length; i++)
                            {
                                if (ps[i].ParameterType == typeof(string))
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

                            m.Invoke(notify, args);
                            return;
                        }
                    }
                    catch
                    {
                        // try next
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string GetUid()
    {
        var uid = GetStaticString("PlayerDataHolder", "SelectPlayerUid");
        if (string.IsNullOrEmpty(uid))
        {
            uid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        }

        return uid ?? "";
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
            var instProp = cur.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
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

            var instField = cur.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
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

    private static string GetStaticString(string typeName, string name)
    {
        return Convert.ToString(GetStaticMember(typeName, name) ?? "");
    }
}
