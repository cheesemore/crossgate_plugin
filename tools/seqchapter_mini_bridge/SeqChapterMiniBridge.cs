// 精简桥接：从新序章多开器直接驱动，仅实现 登录 + 进游戏 + 拉多控 + 一键召唤。
// IPC 与 ipc.py 完全兼容（cmd.json / ack.json / state.json，inst_{pid}）。
// 流程反馈用游戏内 NotifyManager.Tip；召唤完成（team_ok）后软释放（停 Timer 心跳）。
// 编译时不依赖 Hotfix/Unity 引用，运行时在同程序集内反射调用。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

public static class SeqChapterMiniBridge
{
    private static string _instanceId;
    private static string _baseDir;
    private static string _lastCmdId;
    private static double _heartbeatAt;
    private static double _fullHeartbeatAt;
    private static double _pollAt;
    private static readonly Queue<string> _workflow = new Queue<string>();
    private static double _workflowWaitUntil;
    private static double? _workflowUntilStarted;
    private static bool _workflowActive;
    private static string _workflowError = "";
    private static bool _workflowDoneFlag;

    private const float TickIntervalSec = 0.5f;
    private const double LightHeartbeatSec = 0.5;
    private const double FullHeartbeatLoginSec = 1.0;
    private const double FullHeartbeatInGameSec = 3.0;

    private static readonly Dictionary<string, Type> _typeByName = new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> _indexedAssemblies = new HashSet<Assembly>();
    private static readonly Dictionary<string, object> _managerCache = new Dictionary<string, object>(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> _stateCache = new Dictionary<string, object>(StringComparer.Ordinal);
    private static Type _teamMgrTypeForMethods;
    private static MethodInfo _teamGetTeamNumMethod;
    private static MethodInfo _teamGetTeamMulitCountMethod;
    private static MethodInfo _teamIsTeamMethod;
    private static MethodInfo _teamIsLeaderMethod;

    // 保留 Timer 引用，防止 Unity 定时器被 GC 回收（不释放 DLL，心跳常驻）。
    private static object _timerInstance;

    public static void InitFromStart()
    {
        var pid = ResolveCurrentProcessId();
        _instanceId = "inst_" + pid;
        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seqchapter_helper", "instances", _instanceId);
        Directory.CreateDirectory(_baseDir);
        WriteState("boot", "mini_bridge_started");
    }

    private static bool _bootstrapped;

    /// <summary>外部 DLL 加载入口：Init + 注册 Timer。</summary>
    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        try
        {
            InitFromStart();
            RegisterTickTimer();
        }
        catch (Exception ex)
        {
            try
            {
                WriteState("boot_error", ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // ignore secondary failures
            }
        }
    }

    private static void RegisterTickTimer()
    {
        try
        {
            var hotfix = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "hotfix", StringComparison.OrdinalIgnoreCase));
            if (hotfix == null)
            {
                WriteState("boot_error", "hotfix assembly not found");
                return;
            }

            var timerType = hotfix.GetType("Timer");
            if (timerType == null)
            {
                WriteState("boot_error", "Timer type not found");
                return;
            }

            var create = timerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Create")
                    {
                        return false;
                    }

                    var p = m.GetParameters();
                    return p.Length == 5 && p[0].ParameterType.Name == "Action";
                });
            if (create == null)
            {
                WriteState("boot_error", "Timer.Create not found");
                return;
            }

            var tick = typeof(SeqChapterMiniBridge).GetMethod(
                "Tick",
                BindingFlags.Public | BindingFlags.Static);
            if (tick == null)
            {
                WriteState("boot_error", "Tick not found");
                return;
            }

            var actionType = create.GetParameters()[0].ParameterType;
            var del = Delegate.CreateDelegate(actionType, tick);
            var timer = create.Invoke(null, new object[] { del, TickIntervalSec, -1, true, 1f });
            _timerInstance = timer;
            var start = timer.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
            if (start == null)
            {
                WriteState("boot_error", "Timer.Start not found");
                return;
            }

            start.Invoke(timer, null);
        }
        catch (Exception ex)
        {
            WriteState("boot_error", "RegisterTickTimer: " + ex.Message);
        }
    }

    public static void Tick()
    {
        if (_baseDir == null)
        {
            InitFromStart();
        }

        var now = Now();

        if (now - _pollAt >= LightHeartbeatSec)
        {
            _pollAt = now;
            TryProcessCommand();
            TryAdvanceWorkflow();
        }

        if (now - _heartbeatAt < LightHeartbeatSec)
        {
            return;
        }

        _heartbeatAt = now;
        var phase = GuessPhase();
        var fullInterval = phase == "login" || _workflowActive
            ? FullHeartbeatLoginSec
            : FullHeartbeatInGameSec;
        var full = _fullHeartbeatAt <= 0 || now - _fullHeartbeatAt >= fullInterval;
        if (full)
        {
            _fullHeartbeatAt = now;
        }

        WriteHeartbeat(phase, full);
    }

    private static double Now() => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

    private static void WriteHeartbeat(string phase, bool full)
    {
        var st = _stateCache;
        st["heartbeat_ts"] = (long)Now();
        st["instance_id"] = _instanceId;
        st["phase"] = phase;
        st["select_uid"] = GetStaticString("PlayerDataHolder", "SelectPlayerUid");
        st["main_uid"] = GetStaticString("PlayerDataHolder", "MainPlayerUid");

        if (full)
        {
            if (phase == "login")
            {
                st["net_ready"] = IsNetManagerReady();
                st["login_ui_ready"] = IsLoginUiReady();
                st["notice_panel_open"] = IsNoticePanelOpen();
                st["route_panel_open"] = IsRoutePanelOpen();
                st["route_char_ready"] = IsRouteCharReady();
                st["account_count"] = GetAccountCount();
            }
            else
            {
                st["net_ready"] = true;
                st["login_ui_ready"] = false;
                st["notice_panel_open"] = false;
                st["route_panel_open"] = false;
                st["route_char_ready"] = false;
                st["account_count"] = 0;
            }

            AppendMultiFields(st);
        }

        st["workflow_active"] = _workflowActive;
        st["workflow_steps"] = _workflow.Count;
        st["workflow_current"] = _workflow.Count > 0 ? _workflow.Peek() : "";
        st["workflow_error"] = _workflowError ?? "";
        st["workflow_done"] = !_workflowActive && string.IsNullOrEmpty(_workflowError) && _workflowDoneFlag;
        WriteJson("state.json", st);
    }

    private static string GuessPhase()
    {
        if (string.IsNullOrEmpty(GetStaticString("PlayerDataHolder", "MainPlayerUid")))
        {
            return "login";
        }

        return "in_game";
    }

    private static void TryProcessCommand()
    {
        var cmdPath = Path.Combine(_baseDir, "cmd.json");
        if (!File.Exists(cmdPath))
        {
            return;
        }

        Dictionary<string, object> cmd;
        try
        {
            cmd = MiniJson.Deserialize(File.ReadAllText(cmdPath)) as Dictionary<string, object>;
        }
        catch
        {
            return;
        }

        if (cmd == null)
        {
            return;
        }

        var id = cmd.TryGetValue("id", out var idObj) ? idObj?.ToString() : "";
        if (id == _lastCmdId)
        {
            return;
        }

        _lastCmdId = id;
        File.Delete(cmdPath);

        var name = cmd.TryGetValue("cmd", out var cObj) ? cObj?.ToString() : "";
        var prm = cmd.TryGetValue("params", out var pObj) && pObj is Dictionary<string, object> d
            ? d
            : new Dictionary<string, object>();

        var ok = false;
        var msg = "";
        try
        {
            ok = Dispatch(name, prm, out msg);
        }
        catch (Exception ex)
        {
            ok = false;
            msg = ex.Message;
        }

        WriteJson("ack.json", new Dictionary<string, object>
        {
            ["id"] = id,
            ["ok"] = ok,
            ["msg"] = msg ?? "",
            ["ts"] = (long)Now(),
        });
    }

    private static bool Dispatch(string cmd, Dictionary<string, object> prm, out string msg)
    {
        msg = "";
        switch (cmd)
        {
            case "login":
                return DoLogin(GetStr(prm, "phone"), GetStr(prm, "password"), out msg);
            case "enter_game":
                return DoEnterGame(out msg);
            case "multi_login_offline_all":
                return DoMultiLoginOfflineAll(out msg);
            case "multi_login_char":
                return DoMultiLoginChar(GetInt(prm, "index", 0), out msg);
            case "fetch_multi_info":
                return DoFetchMultiInfo(out msg);
            case "create_team":
                return DoCreateTeam(out msg);
            case "team_gather":
                return DoTeamGather(out msg);
            case "click_multi_head":
                return DoClickMultiPanelHead(GetInt(prm, "index", 0), out msg);
            case "select_multi_char":
                return DoSelectMultiPanelChar(GetInt(prm, "index", 0), out msg);
            case "close_share_panel":
                return DoCloseSharePanel(out msg);
            case "switch_char":
                return DoSwitchChar(GetInt(prm, "index", 0), out msg);
            case "one_key_summon":
                return DoOneKeySummon(out msg);
            case "workflow_step1":
                return StartWorkflowStep1(GetStr(prm, "phone"), GetStr(prm, "password"), out msg);
            case "workflow_login_enter":
                return StartWorkflowLoginEnter(GetStr(prm, "phone"), GetStr(prm, "password"), out msg);
            default:
                msg = "unknown cmd: " + cmd;
                return false;
        }
    }

    private static bool DoLogin(string phone, string password, out string msg)
    {
        msg = "";
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(password))
        {
            msg = "phone/password empty";
            return false;
        }

        SetStaticField("PlayerDataHolder", "account", phone);
        SetStaticField("PlayerDataHolder", "password", password);

        TryCloseNoticePanel(out _);

        var loginPanel = InvokeStaticGeneric("UIManager", "GetUIPanel", "LoginPanel");
        if (loginPanel == null)
        {
            msg = "LoginPanel not open";
            return false;
        }

        var comLogin = GetInstanceField(loginPanel, "m_Com_Login");
        if (comLogin == null)
        {
            InvokeInstanceMethod(loginPanel, "Open");
            comLogin = GetInstanceField(loginPanel, "m_Com_Login");
        }

        if (comLogin == null)
        {
            msg = "Com_Login missing";
            return false;
        }

        SetInputFieldText(comLogin, "m_ITxt_PhoneAccount", phone);
        SetInputFieldText(comLogin, "m_ITxt_PhonePasswd", password);
        SetInputFieldText(comLogin, "m_ITxt_MessageAccount", phone);
        SetInstanceField(comLogin, "fullPhoneNumber", phone);
        SetInstanceField(comLogin, "m_phoneNumCanUse", true);

        var agree = GetProperty(comLogin, "m_UTog_ReadAgree");
        if (agree != null)
        {
            SetProperty(agree, "IsOn", true);
        }

        var onClick = comLogin.GetType().GetMethod(
            "OnClicklogin",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (onClick == null)
        {
            msg = "OnClicklogin missing";
            return false;
        }

        onClick.Invoke(comLogin, null);
        Tip("正在自动登录");
        msg = "OnClicklogin invoked";
        return true;
    }

    private static bool IsNoticePanelOpen()
        => IsPanelVisible("NoticePanel");

    private static bool IsPanelVisible(string panelTypeName)
    {
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", panelTypeName);
        if (panel == null)
        {
            return false;
        }

        var go = GetProperty(panel, "gameObject");
        if (go == null)
        {
            return false;
        }

        var active = GetProperty(go, "activeInHierarchy");
        return active is bool b && b;
    }

    private static bool TryCloseNoticePanel(out string msg)
    {
        msg = "";
        if (!IsNoticePanelOpen())
        {
            return true;
        }

        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "NoticePanel");
        if (panel == null)
        {
            msg = "notice close skipped (panel null)";
            return false;
        }

        InvokeInstanceMethod(panel, "Close");
        if (IsNoticePanelOpen())
        {
            msg = "notice close failed";
            return false;
        }

        msg = "notice closed";
        return true;
    }

    private static void SetInputFieldText(object holder, string fieldName, string text)
    {
        if (holder == null)
        {
            return;
        }

        var field = holder.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var input = field?.GetValue(holder);
        if (input == null)
        {
            return;
        }

        var prop = input.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
        prop?.SetValue(input, text ?? "", null);
    }

    private static bool IsLoginUiReady()
    {
        var loginPanel = InvokeStaticGeneric("UIManager", "GetUIPanel", "LoginPanel");
        if (loginPanel == null)
        {
            return false;
        }

        var go = GetProperty(loginPanel, "gameObject");
        if (go == null)
        {
            return false;
        }

        var active = GetProperty(go, "activeInHierarchy");
        if (!(active is bool b && b))
        {
            return false;
        }

        var comLogin = GetInstanceField(loginPanel, "m_Com_Login");
        if (comLogin == null)
        {
            return false;
        }

        return GetInstanceField(comLogin, "m_ITxt_PhoneAccount") != null && IsNetManagerReady();
    }

    private static bool DoEnterGame(out string msg)
    {
        msg = "";
        TryCloseNoticePanel(out _);

        if (!IsRoutePanelOpen())
        {
            msg = "RouteSelectPanel not open";
            return false;
        }

        TryPrepareRoutePanel(out var prepMsg);

        var roleIndex = FindFirstPlayableRoleIndex();
        if (roleIndex < 0)
        {
            var accounts = GetDataCenterAccounts();
            if (accounts != null && accounts.Count > 0)
            {
                roleIndex = 0;
            }
        }

        if (roleIndex < 0)
        {
            msg = "no playable role";
            return false;
        }

        if (!DispatchRouteEnter(roleIndex, out msg))
        {
            return false;
        }

        msg = (string.IsNullOrEmpty(prepMsg) ? "" : prepMsg + "; ")
            + "ROUTE_ENTER role=" + roleIndex;
        return true;
    }

    private static bool IsRouteCharReady()
        => IsRoutePanelOpen() && FindFirstPlayableRoleIndex() >= 0;

    private static void TryPrepareRoutePanel(out string msg)
    {
        msg = "";
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "RouteSelectPanel");
        if (panel == null)
        {
            msg = "RouteSelectPanel null";
            return;
        }

        var loginMgr = GetManagerInstance("LoginManager");
        if (loginMgr != null)
        {
            var groupIdx = Convert.ToInt32(GetProperty(loginMgr, "GroupIndex") ?? -1);
            var serverIdx = Convert.ToInt32(GetProperty(loginMgr, "ServerIndex") ?? -1);
            if (groupIdx >= 0 && serverIdx >= 0)
            {
                SetInstanceField(panel, "m_GroupIndex", groupIdx);
                SetInstanceField(panel, "m_ServerIndex", serverIdx);
            }
        }

        InvokeInstanceMethod(panel, "RefreshServer");
        InvokeInstanceMethod(panel, "SetPlayerHead");
        msg = "route panel prepared";
    }

    private static bool DoMultiLoginOfflineAll(out string msg)
    {
        msg = "";
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        if (multi == null)
        {
            msg = "MultiInfo not ready";
            return false;
        }

        var players = GetProperty(multi, "Players") as IEnumerable;
        if (players == null)
        {
            msg = "Players missing";
            return false;
        }

        var count = 0;
        foreach (var p in players)
        {
            if (p == null)
            {
                continue;
            }

            var uid = GetProperty(p, "Uid") as string;
            var online = Convert.ToInt32(GetProperty(p, "Online") ?? 0);
            if (online <= 0 && !string.IsNullOrEmpty(uid))
            {
                SendMultiForUid("登陆角色", uid);
                count++;
            }
        }

        Tip("正在拉起离线多控");
        msg = "multi login sent: " + count;
        return true;
    }

    private static bool DoMultiLoginChar(int index, out string msg)
    {
        msg = "";
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IList;
        if (players == null || index < 0 || index >= players.Count)
        {
            msg = "invalid multi index " + index;
            return false;
        }

        var player = players[index];
        if (player == null)
        {
            msg = "null player at index " + index;
            return false;
        }

        var uid = GetProperty(player, "Uid") as string;
        if (string.IsNullOrEmpty(uid))
        {
            msg = "empty uid at index " + index;
            return false;
        }

        var online = Convert.ToInt32(GetProperty(player, "Online") ?? 0);
        if (online > 0)
        {
            msg = "already online index=" + index;
            return true;
        }

        SendMultiForUid("登陆角色", uid);
        msg = "multi login sent index=" + index + " uid=" + uid;
        return true;
    }

    private static bool DoFetchMultiInfo(out string msg)
    {
        msg = "";
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IList;
        if (players != null && players.Count > 0)
        {
            msg = "MultiInfo ready count=" + players.Count;
            return true;
        }

        var teamMgr = GetManagerInstance("TeamManager");
        if (teamMgr == null)
        {
            msg = "TeamManager missing";
            return false;
        }

        var playerData = GetStaticField("PlayerDataHolder", "playerData");
        if (playerData == null)
        {
            msg = "playerData missing (not in game?)";
            return false;
        }

        var mapId = Convert.ToInt32(GetProperty(playerData, "mapId") ?? 0);
        var floor = Convert.ToInt32(GetProperty(playerData, "floor") ?? 0);
        var location = GetStaticField("PlayerDataHolder", "location");
        var sendMulti = teamMgr.GetType().GetMethod("SendMulti");
        if (sendMulti == null)
        {
            msg = "SendMulti missing";
            return false;
        }

        sendMulti.Invoke(teamMgr, new object[] { "获取多控", mapId, floor, location, "" });
        msg = "fetch multi sent";
        return true;
    }

    private static bool DoSwitchChar(int index, out string msg)
    {
        msg = "";
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IList;
        if (players == null || index < 0 || index >= players.Count)
        {
            msg = "invalid multi index";
            return false;
        }

        var player = players[index];
        var uid = GetProperty(player, "Uid") as string;
        if (string.IsNullOrEmpty(uid))
        {
            msg = "empty uid";
            return false;
        }

        var mainUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        if (uid == mainUid)
        {
            SetStaticField("PlayerDataHolder", "SelectPlayerUid", uid);
            msg = "already main";
            return true;
        }

        SendMultiForUid("头像切换角色", uid);
        var teamMgr = GetManagerInstance("TeamManager");
        SetInstanceField(teamMgr, "IsRefreshPos", true);
        msg = "switch sent for index " + index;
        return true;
    }

    private static object GetMulitPanel(out string msg, bool refreshUi)
    {
        msg = "";
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "MulitPanel");
        if (panel == null)
        {
            msg = "MulitPanel missing";
            return null;
        }

        InvokeInstanceMethod(panel, "Open", 1);
        if (refreshUi)
        {
            InvokeInstanceMethod(panel, "RefreshUi");
        }

        return panel;
    }

    private static bool TryGetMulitBtn(object panel, int index, out object comBtn, out string msg)
    {
        msg = "";
        comBtn = null;
        var btnMulits = GetInstanceField(panel, "m_Btn_Mulits") as Array;
        if (btnMulits == null || index < 0 || index >= btnMulits.Length)
        {
            msg = "invalid mulit btn index " + index;
            return false;
        }

        comBtn = btnMulits.GetValue(index);
        if (comBtn == null)
        {
            msg = "Com_BtnMulit null at " + index;
            return false;
        }

        return true;
    }

    private static bool DoClickMultiPanelHead(int index, out string msg)
    {
        msg = "";
        var panel = GetMulitPanel(out msg, true);
        if (panel == null)
        {
            return false;
        }

        if (!TryGetMulitBtn(panel, index, out var comBtn, out msg))
        {
            return false;
        }

        var btnHead = GetInstanceField(comBtn, "Btn_Head");
        if (btnHead == null)
        {
            msg = "Btn_Head missing";
            return false;
        }

        if (!InvokePrivateMethod(panel, "OnClickHead", new object[] { btnHead }, out msg))
        {
            return false;
        }

        msg = "multi head clicked index=" + index;
        return true;
    }

    private static bool DoSelectMultiPanelChar(int index, out string msg)
    {
        msg = "";
        var panel = GetMulitPanel(out msg, true);
        if (panel == null)
        {
            return false;
        }

        if (!TryGetMulitBtn(panel, index, out var comBtn, out msg))
        {
            return false;
        }

        if (!InvokePrivateMethod(panel, "OnClickMulit", new object[] { index, comBtn }, out msg))
        {
            return false;
        }

        msg = "multi selected index=" + index;
        return true;
    }

    private static bool DoOneKeySummon(out string msg)
    {
        msg = "";
        var panel = GetMulitPanel(out msg, true);
        if (panel == null)
        {
            return false;
        }

        var selectIndex = Convert.ToInt32(GetInstanceField(panel, "m_SelectIndex") ?? -1);
        if (selectIndex < 0)
        {
            if (!TryGetMulitBtn(panel, 0, out var comBtn, out msg))
            {
                return false;
            }

            if (!InvokePrivateMethod(panel, "OnClickMulit", new object[] { 0, comBtn }, out msg))
            {
                return false;
            }

            selectIndex = Convert.ToInt32(GetInstanceField(panel, "m_SelectIndex") ?? 0);
        }

        if (!InvokePrivateMethod(panel, "OnClickOneKey", null, out msg))
        {
            return false;
        }

        Tip("一键召唤中");
        msg = "one key summon via MulitPanel";
        return true;
    }

    private static bool DoCloseSharePanel(out string msg)
    {
        msg = "";
        var closed = new List<string>();
        var failed = new List<string>();
        TryCloseUiPanel("ShareNoticePanel", closed, failed);
        TryCloseUiPanel("ActivityPanel", closed, failed);

        if (closed.Count > 0)
        {
            msg = string.Join(", ", closed.ToArray()) + " closed";
            return true;
        }

        if (failed.Count > 0)
        {
            msg = string.Join(", ", failed.ToArray()) + " close failed";
            return false;
        }

        msg = "promotion panels not open";
        return true;
    }

    private static void TryCloseUiPanel(string panelTypeName, List<string> closed, List<string> failed)
    {
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", panelTypeName);
        if (panel == null || !IsUiPanelVisible(panel))
        {
            return;
        }

        InvokeInstanceMethod(panel, "Close");
        if (IsUiPanelVisible(panel))
        {
            failed.Add(panelTypeName);
        }
        else
        {
            closed.Add(panelTypeName);
        }
    }

    private static bool IsUiPanelVisible(object panel)
    {
        if (panel == null)
        {
            return false;
        }

        var state = Convert.ToInt32(GetProperty(panel, "eUIState") ?? 0);
        if (state != 3)
        {
            return false;
        }

        var hided = GetProperty(panel, "isHided");
        return !(hided is bool hidden && hidden);
    }

    private static bool InvokePrivateMethod(object target, string name, object[] args, out string err)
    {
        err = "";
        args = args ?? new object[0];
        foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != name)
            {
                continue;
            }

            var ps = method.GetParameters();
            if (ps.Length != args.Length)
            {
                continue;
            }

            try
            {
                method.Invoke(target, args);
                return true;
            }
            catch (Exception ex)
            {
                err = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        err = name + " invoke failed";
        return false;
    }

    private static bool DoCreateTeam(out string msg)
    {
        msg = "";
        var teamMgr = GetManagerInstance("TeamManager");
        if (teamMgr == null)
        {
            msg = "TeamManager missing";
            return false;
        }

        var sendOp = teamMgr.GetType().GetMethod("SendOperation");
        if (sendOp == null)
        {
            msg = "SendOperation missing";
            return false;
        }

        sendOp.Invoke(teamMgr, new object[] { "创建队伍", "" });
        msg = "create team sent";
        return true;
    }

    private static bool DoTeamGather(out string msg)
    {
        msg = "";
        var teamMgr = GetManagerInstance("TeamManager");
        if (teamMgr == null)
        {
            msg = "TeamManager missing";
            return false;
        }

        var uid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        var sendOp = teamMgr.GetType().GetMethod("SendOperation");
        if (sendOp == null)
        {
            msg = "SendOperation missing";
            return false;
        }

        sendOp.Invoke(teamMgr, new object[] { "队伍召集", uid });
        msg = "team gather sent uid=" + uid;
        return true;
    }

    private static bool StartWorkflowStep1(string phone, string password, out string msg)
    {
        _workflow.Clear();
        _workflowActive = true;
        _workflowDoneFlag = false;
        _workflowError = "";
        _workflowUntilStarted = null;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            _workflow.Enqueue("until:net_manager:180");
            _workflow.Enqueue("login:" + phone + ":" + password);
        }

        EnqueueLoginEnterSteps();
        _workflow.Enqueue("multi_login_offline_all");
        _workflow.Enqueue("until:multi_ready:90");
        _workflow.Enqueue("click_multi_head:0");
        _workflow.Enqueue("wait:2");
        _workflow.Enqueue("select_multi_char:0");
        _workflow.Enqueue("wait:1");
        _workflow.Enqueue("one_key_summon");
        _workflow.Enqueue("wait:5");
        _workflow.Enqueue("until:team_ok:60");
        _workflow.Enqueue("close_share_panel");
        msg = "workflow queued";
        return true;
    }

    private static bool StartWorkflowLoginEnter(string phone, string password, out string msg)
    {
        _workflow.Clear();
        _workflowActive = true;
        _workflowDoneFlag = false;
        _workflowError = "";
        _workflowUntilStarted = null;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            _workflow.Enqueue("until:net_manager:180");
            _workflow.Enqueue("login:" + phone + ":" + password);
        }

        EnqueueLoginEnterSteps();
        msg = "login_enter workflow queued";
        return true;
    }

    private static void EnqueueLoginEnterSteps()
    {
        _workflow.Enqueue("until:route_panel:120");
        _workflow.Enqueue("ensure_server");
        _workflow.Enqueue("until:route_ready:90");
        _workflow.Enqueue("enter_game");
        _workflow.Enqueue("until:in_game:180");
    }

    private static void FinishWorkflow(bool ok, string note)
    {
        _workflow.Clear();
        _workflowActive = false;
        _workflowUntilStarted = null;
        if (ok)
        {
            _workflowDoneFlag = true;
            WriteState("workflow_done", note ?? "step1 complete");
            if (IsTeamOk())
            {
                Tip("一键召唤完成，多控就绪");
            }
            else
            {
                Tip("自动流程完成");
            }
        }
        else
        {
            _workflowError = note ?? "workflow failed";
            WriteState("workflow_error", _workflowError);
            Tip("自动流程失败：" + _workflowError);
        }
    }

    private static void TryAdvanceWorkflow()
    {
        if (_workflow.Count == 0)
        {
            if (_workflowActive)
            {
                FinishWorkflow(true, "step1 complete");
            }

            return;
        }

        if (Now() < _workflowWaitUntil)
        {
            return;
        }

        var step = _workflow.Peek();
        if (step.StartsWith("wait:"))
        {
            _workflow.Dequeue();
            var sec = double.TryParse(step.Substring(5), out var s) ? s : 1.0;
            _workflowWaitUntil = Now() + sec;
            _workflowUntilStarted = null;
            return;
        }

        if (step.StartsWith("until:"))
        {
            var parts = step.Split(':');
            var cond = parts.Length > 1 ? parts[1] : "";
            var maxSec = 60.0;
            if (parts.Length > 2)
            {
                double.TryParse(parts[2], out maxSec);
            }

            if (_workflowUntilStarted == null)
            {
                _workflowUntilStarted = Now();
            }

            if (Now() - _workflowUntilStarted.Value > maxSec)
            {
                _workflow.Dequeue();
                FinishWorkflow(false, "timeout waiting for " + cond);
                return;
            }

            if (!CheckUntilCondition(cond))
            {
                _workflowWaitUntil = Now() + 0.5;
                return;
            }

            _workflow.Dequeue();
            _workflowUntilStarted = null;
            _workflowWaitUntil = Now() + 0.3;
            return;
        }

        if (step.StartsWith("login:"))
        {
            var parts = step.Split(':');
            if (parts.Length >= 3)
            {
                if (!DoLogin(parts[1], string.Join(":", parts.Skip(2)), out var loginMsg))
                {
                    if (_workflowUntilStarted == null)
                    {
                        _workflowUntilStarted = Now();
                    }

                    if (Now() - _workflowUntilStarted.Value > 180.0)
                    {
                        _workflow.Dequeue();
                        FinishWorkflow(false, "login failed: " + loginMsg);
                        return;
                    }

                    _workflowWaitUntil = Now() + 2.0;
                    return;
                }
            }

            _workflow.Dequeue();
            _workflowWaitUntil = Now() + 1.0;
            _workflowUntilStarted = null;
            return;
        }

        if (step == "enter_game")
        {
            if (!DoEnterGame(out var enterMsg))
            {
                if (_workflowUntilStarted == null)
                {
                    _workflowUntilStarted = Now();
                }

                if (Now() - _workflowUntilStarted.Value > 60.0)
                {
                    _workflow.Dequeue();
                    FinishWorkflow(false, "enter_game failed: " + enterMsg);
                    return;
                }

                _workflowWaitUntil = Now() + 2.0;
                return;
            }

            _workflow.Dequeue();
            _workflowWaitUntil = Now() + 1.0;
            _workflowUntilStarted = null;
            return;
        }

        if (step == "close_share_panel")
        {
            if (_workflowUntilStarted == null)
            {
                _workflowUntilStarted = Now();
            }

            DoCloseSharePanel(out var closeMsg);
            if (closeMsg.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _workflow.Dequeue();
                _workflowUntilStarted = null;
                _workflowWaitUntil = Now() + 0.3;
                return;
            }

            if (closeMsg.IndexOf("not open", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (Now() - _workflowUntilStarted.Value > 20.0)
                {
                    _workflow.Dequeue();
                    _workflowUntilStarted = null;
                    _workflowWaitUntil = Now() + 0.3;
                    return;
                }

                _workflowWaitUntil = Now() + 0.5;
                return;
            }

            if (Now() - _workflowUntilStarted.Value > 20.0)
            {
                _workflow.Dequeue();
                _workflowUntilStarted = null;
                _workflowWaitUntil = Now() + 0.3;
                return;
            }

            _workflowWaitUntil = Now() + 0.5;
            return;
        }

        _workflow.Dequeue();
        _workflowUntilStarted = null;
        if (step.StartsWith("click_multi_head:"))
        {
            var idx = int.TryParse(step.Substring("click_multi_head:".Length), out var hi) ? hi : 0;
            DoClickMultiPanelHead(idx, out _);
        }
        else if (step.StartsWith("select_multi_char:"))
        {
            var idx = int.TryParse(step.Substring("select_multi_char:".Length), out var si) ? si : 0;
            DoSelectMultiPanelChar(idx, out _);
        }
        else if (step == "switch_char:0")
        {
            DoSwitchChar(0, out _);
        }
        else if (step == "ensure_server")
        {
            EnsureDefaultServer(out _);
        }
        else
        {
            Dispatch(step, new Dictionary<string, object>(), out _);
        }

        _workflowWaitUntil = Now() + 1.0;
    }

    private static bool CheckUntilCondition(string cond)
    {
        switch (cond)
        {
            case "route_panel":
                return IsRoutePanelOpen();
            case "in_game":
                return IsInGame();
            case "multi_ready":
                return IsMultiReady();
            case "net_manager":
                return IsNetManagerReady();
            case "route_ready":
                return IsRouteReady();
            case "team_ok":
                return IsTeamOk();
            default:
                return false;
        }
    }

    private static bool IsRouteReady()
    {
        if (!IsRoutePanelOpen())
        {
            return false;
        }

        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "RouteSelectPanel");
        if (panel == null)
        {
            return false;
        }

        var serverIdx = Convert.ToInt32(GetInstanceField(panel, "m_ServerIndex") ?? -1);
        if (serverIdx < 0)
        {
            return false;
        }

        return FindFirstPlayableRoleIndex() >= 0;
    }

    private static bool IsNetManagerReady()
    {
        var netMgr = GetManagerInstance("NetManager");
        if (netMgr == null)
        {
            return false;
        }

        return netMgr.GetType().GetMethod("LoginGetToken") != null;
    }

    private static bool IsRoutePanelOpen()
    {
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "RouteSelectPanel");
        if (panel == null)
        {
            return false;
        }

        var go = GetProperty(panel, "gameObject");
        if (go == null)
        {
            return false;
        }

        var active = GetProperty(go, "activeInHierarchy");
        return active is bool b && b;
    }

    private static bool IsInGame()
        => !string.IsNullOrEmpty(GetStaticString("PlayerDataHolder", "MainPlayerUid"));

    private static bool IsMultiReady()
    {
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IEnumerable;
        if (players == null)
        {
            return false;
        }

        var any = false;
        foreach (var p in players)
        {
            if (p == null)
            {
                continue;
            }

            any = true;
            if (Convert.ToInt32(GetProperty(p, "Online") ?? 0) <= 0)
            {
                return false;
            }
        }

        return any;
    }

    private static void AppendMultiFields(Dictionary<string, object> st)
    {
        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IList;
        var teamMgr = GetManagerInstance("TeamManager");
        var total = 0;
        var online = 0;
        var onlineParts = new List<string>();

        if (players != null)
        {
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null)
                {
                    onlineParts.Add("0");
                    continue;
                }

                var uid = GetProperty(p, "Uid") as string;
                if (string.IsNullOrEmpty(uid))
                {
                    onlineParts.Add("0");
                    continue;
                }

                total++;
                var on = Convert.ToInt32(GetProperty(p, "Online") ?? 0);
                onlineParts.Add(on > 0 ? "1" : "0");
                if (on > 0)
                {
                    online++;
                }
            }
        }

        var teamNum = InvokeTeamMgrInt("GetTeamNum");
        st["multi_count"] = total;
        st["multi_online"] = online;
        st["multi_ready"] = total > 0 && online >= total;
        st["team_num"] = teamNum;
        st["team_ok"] = teamNum >= 5;
        st["multi_slot0_uid"] = GetFirstMultiUid(players);
        st["team_leader_uid"] = GetTeamLeaderUid();
        st["multi_online_slots"] = string.Join(",", onlineParts.ToArray());
    }

    private static bool IsTeamOk()
        => InvokeTeamMgrInt("GetTeamNum") >= 5;

    private static string GetFirstMultiUid(IList players)
    {
        if (players == null || players.Count == 0)
        {
            return "";
        }

        var p = players[0];
        return p == null ? "" : GetProperty(p, "Uid") as string ?? "";
    }

    private static string GetTeamLeaderUid()
    {
        var teamData = GetStaticField("PlayerDataHolder", "teamData") as Array;
        if (teamData == null || teamData.Length == 0)
        {
            return "";
        }

        var first = teamData.GetValue(0);
        if (first == null || Convert.ToInt32(GetProperty(first, "UseFlag") ?? 0) != 1)
        {
            return "";
        }

        var player = GetProperty(first, "Player");
        return player == null ? "" : GetProperty(player, "Uid") as string ?? "";
    }

    private static int InvokeTeamMgrInt(string methodName)
    {
        var teamMgr = GetManagerInstance("TeamManager");
        if (teamMgr == null)
        {
            return 0;
        }

        var mgrType = teamMgr.GetType();
        if (_teamMgrTypeForMethods != mgrType)
        {
            _teamMgrTypeForMethods = mgrType;
            _teamGetTeamNumMethod = null;
            _teamGetTeamMulitCountMethod = null;
            _teamIsTeamMethod = null;
            _teamIsLeaderMethod = null;
        }

        MethodInfo method = null;
        if (methodName == "GetTeamNum")
        {
            method = _teamGetTeamNumMethod ?? (_teamGetTeamNumMethod = mgrType.GetMethod(methodName));
        }
        else if (methodName == "GetTeamMulitCount")
        {
            method = _teamGetTeamMulitCountMethod
                ?? (_teamGetTeamMulitCountMethod = mgrType.GetMethod(methodName));
        }

        if (method == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(method.Invoke(teamMgr, null) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsLeaderUid(object teamMgr, string uid)
    {
        if (teamMgr == null || string.IsNullOrEmpty(uid))
        {
            return false;
        }

        var mgrType = teamMgr.GetType();
        if (_teamMgrTypeForMethods != mgrType)
        {
            _teamMgrTypeForMethods = mgrType;
            _teamGetTeamNumMethod = null;
            _teamGetTeamMulitCountMethod = null;
            _teamIsTeamMethod = null;
            _teamIsLeaderMethod = null;
        }

        if (_teamIsLeaderMethod == null)
        {
            _teamIsLeaderMethod = mgrType.GetMethod("IsLeader");
        }

        if (_teamIsLeaderMethod == null)
        {
            return false;
        }

        try
        {
            return Convert.ToBoolean(_teamIsLeaderMethod.Invoke(teamMgr, new object[] { uid }) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureDefaultServer(out string msg)
    {
        msg = "";
        var loginMgr = GetManagerInstance("LoginManager");
        if (loginMgr == null)
        {
            msg = "LoginManager missing";
            return false;
        }

        var groupIdx = Convert.ToInt32(GetProperty(loginMgr, "GroupIndex") ?? -1);
        var serverIdx = Convert.ToInt32(GetProperty(loginMgr, "ServerIndex") ?? -1);
        if (groupIdx >= 0 && serverIdx >= 0)
        {
            SyncRoutePanel(out _);
            msg = "server already selected";
            return true;
        }

        var dcType = FindType("DataCenterHotfix");
        var serverListField = dcType?.GetField("serverList", BindingFlags.Public | BindingFlags.Static);
        var serverList = serverListField?.GetValue(null);
        var listServer = GetProperty(serverList, "ListServer") as IList;
        if (listServer == null || listServer.Count == 0)
        {
            msg = "server list empty";
            return false;
        }

        SetProperty(loginMgr, "GroupIndex", 0);
        var group = listServer[0];
        var servers = GetProperty(group, "Servers") as IList;
        if (servers == null || servers.Count == 0)
        {
            msg = "no server in group 0";
            return false;
        }

        SetProperty(loginMgr, "ServerIndex", 0);
        SyncRoutePanel(out _);
        msg = "selected default server 0/0";
        return true;
    }

    private static bool SyncRoutePanel(out string msg)
    {
        msg = "";
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "RouteSelectPanel");
        if (panel == null)
        {
            msg = "RouteSelectPanel not open";
            return false;
        }

        InvokeInstanceMethod(panel, "RefreshServer");
        InvokeInstanceMethod(panel, "SetPlayerHead");
        var serverIdx = Convert.ToInt32(GetInstanceField(panel, "m_ServerIndex") ?? -1);
        if (serverIdx < 0)
        {
            msg = "panel server not selected";
            return false;
        }

        msg = "route panel synced";
        return true;
    }

    private static int FindFirstPlayableRoleIndex()
    {
        var accounts = GetDataCenterAccounts();
        if (accounts == null)
        {
            return -1;
        }

        for (var i = 0; i < accounts.Count; i++)
        {
            var role = accounts[i];
            if (role == null)
            {
                continue;
            }

            var face = Convert.ToInt32(GetProperty(role, "face") ?? 0);
            var name = GetProperty(role, "name")?.ToString();
            if (face != 0 && !string.IsNullOrEmpty(name))
            {
                return i;
            }
        }

        return -1;
    }

    private static int GetAccountCount()
    {
        var accounts = GetDataCenterAccounts();
        return accounts?.Count ?? 0;
    }

    private static IList GetDataCenterAccounts()
    {
        var dc = GetDataCenterInstance();
        if (dc == null)
        {
            return null;
        }

        var list = GetInstanceField(dc, "Account") as IList;
        if (list != null)
        {
            return list;
        }

        return GetProperty(dc, "Account") as IList;
    }

    private static object GetDataCenterInstance()
    {
        var dcType = FindType("DataCenterHotfix");
        if (dcType == null)
        {
            return null;
        }

        for (var t = dcType; t != null; t = t.BaseType)
        {
            var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
            {
                continue;
            }

            try
            {
                return prop.GetValue(null, null);
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static bool DispatchRouteEnter(int roleIndex, out string msg)
    {
        msg = "";
        var loginMgr = GetManagerInstance("LoginManager");
        if (loginMgr == null)
        {
            msg = "LoginManager missing";
            return false;
        }

        var onEvent = GetInstanceField(loginMgr, "OnEvent") ?? GetProperty(loginMgr, "OnEvent");
        if (onEvent == null)
        {
            msg = "OnEvent missing";
            return false;
        }

        var enumType = FindType("LOGIN_TYPE_EVENT");
        if (enumType == null)
        {
            msg = "LOGIN_TYPE_EVENT missing";
            return false;
        }

        object routeEnter;
        try
        {
            routeEnter = Enum.Parse(enumType, "ROUTE_ENTER");
        }
        catch
        {
            msg = "ROUTE_ENTER enum missing";
            return false;
        }

        var dispatch = onEvent.GetType().GetMethod("Dispatch");
        if (dispatch == null)
        {
            msg = "Dispatch missing";
            return false;
        }

        dispatch.Invoke(onEvent, new object[] { routeEnter, roleIndex });
        Tip("正在进入游戏");
        msg = "ROUTE_ENTER ok";
        return true;
    }

    private static void InvokeInstanceMethod(object obj, string methodName, params object[] args)
    {
        if (obj == null)
        {
            return;
        }

        args = args ?? new object[0];
        foreach (var m in obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != methodName)
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length != args.Length)
            {
                continue;
            }

            try
            {
                m.Invoke(obj, args);
                return;
            }
            catch
            {
                // try next overload
            }
        }
    }

    private static object GetInstanceField(object obj, string fieldName)
    {
        if (obj == null)
        {
            return null;
        }

        return obj.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
    }

    private static void SendMultiForUid(string type, string uid)
    {
        var teamMgr = GetManagerInstance("TeamManager");
        var playerData = GetStaticField("PlayerDataHolder", "playerData");
        var mapId = Convert.ToInt32(GetProperty(playerData, "mapId") ?? 0);
        var floor = Convert.ToInt32(GetProperty(playerData, "floor") ?? 0);
        var location = GetStaticField("PlayerDataHolder", "location");

        var sendMulti = teamMgr.GetType().GetMethod("SendMulti");
        sendMulti?.Invoke(teamMgr, new[] { type, mapId, floor, location, uid });
    }

    private static object GetManagerInstance(string managerName)
    {
        if (_managerCache.TryGetValue(managerName, out var cached) && cached != null)
        {
            if (!string.Equals(managerName, "NetManager", StringComparison.Ordinal))
            {
                return cached;
            }
        }

        return RefreshManagerInstance(managerName);
    }

    private static object RefreshManagerInstance(string managerName)
    {
        _managerCache.Remove(managerName);

        var mgrType = FindType(managerName);
        if (mgrType != null)
        {
            for (var t = mgrType; t != null; t = t.BaseType)
            {
                var prop = t.GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (prop == null)
                {
                    continue;
                }

                try
                {
                    var inst = prop.GetValue(null, null);
                    if (inst != null)
                    {
                        _managerCache[managerName] = inst;
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

    private static object GetManagerProperty(string managerName, string propertyName)
    {
        var inst = GetManagerInstance(managerName);
        if (inst == null)
        {
            return null;
        }

        var val = GetProperty(inst, propertyName);
        return val ?? GetInstanceField(inst, propertyName);
    }

    private static object InvokeStaticGeneric(string typeName, string methodName, string genericTypeName)
    {
        var host = FindType(typeName);
        if (host == null)
        {
            return null;
        }

        var argType = FindType(genericTypeName);
        if (argType == null)
        {
            return null;
        }

        foreach (var method in host.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != methodName || !method.IsGenericMethodDefinition)
            {
                continue;
            }

            try
            {
                return method.MakeGenericMethod(argType).Invoke(null, null);
            }
            catch
            {
                // continue
            }
        }

        return null;
    }

    private static void IndexNewAssemblies()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!_indexedAssemblies.Add(asm))
            {
                continue;
            }

            foreach (var t in SafeGetTypes(asm))
            {
                if (!_typeByName.ContainsKey(t.Name))
                {
                    _typeByName[t.Name] = t;
                }
            }
        }
    }

    private static Type FindType(string simpleName)
    {
        IndexNewAssemblies();
        _typeByName.TryGetValue(simpleName, out var t);
        return t;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static object GetProperty(object obj, string name)
        => obj?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);

    private static void SetProperty(object obj, string name, object value)
    {
        if (obj == null)
        {
            return;
        }

        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop == null || !prop.CanWrite)
        {
            return;
        }

        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var converted = value == null ? null : Convert.ChangeType(value, targetType);
        prop.SetValue(obj, converted, null);
    }

    private static object GetStaticField(string typeName, string fieldName)
    {
        var t = FindType(typeName);
        return t?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    }

    private static string GetStaticString(string typeName, string fieldName)
        => GetStaticField(typeName, fieldName)?.ToString() ?? "";

    private static void SetStaticField(string typeName, string fieldName, object value)
    {
        var t = FindType(typeName);
        t?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, value);
    }

    private static void SetInstanceField(object obj, string fieldName, object value)
    {
        obj?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(obj, value);
    }

    private static string GetStr(Dictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static int GetInt(Dictionary<string, object> d, string key, int def)
    {
        if (!d.TryGetValue(key, out var v))
        {
            return def;
        }

        return int.TryParse(v?.ToString(), out var n) ? n : def;
    }

    private static int ResolveCurrentProcessId()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var procType = asm.GetType("System.Diagnostics.Process");
                if (procType == null)
                {
                    continue;
                }

                var getCurrent = procType.GetMethod(
                    "GetCurrentProcess",
                    BindingFlags.Public | BindingFlags.Static);
                var proc = getCurrent?.Invoke(null, null);
                var idProp = procType.GetProperty("Id");
                if (idProp != null && proc != null)
                {
                    return Convert.ToInt32(idProp.GetValue(proc));
                }
            }
            catch
            {
                // try next assembly
            }
        }

        return Environment.TickCount;
    }

    private static void WriteState(string phase, string note)
    {
        WriteJson("state.json", new Dictionary<string, object>
        {
            ["phase"] = phase,
            ["note"] = note,
            ["instance_id"] = _instanceId,
            ["heartbeat_ts"] = (long)Now(),
        });
    }

    private static Dictionary<string, object> ReadJson(string name)
    {
        var path = Path.Combine(_baseDir, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJson(string name, Dictionary<string, object> data)
    {
        var path = Path.Combine(_baseDir, name);
        File.WriteAllText(path, MiniJson.Serialize(data));
    }

    /// <summary>游戏内飘字反馈（NotifyManager.Tip，isMessage=false）。</summary>
    private static void Tip(string text)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            var tip = notify.GetType().GetMethod("Tip", new[] { typeof(string), typeof(bool) });
            tip?.Invoke(notify, new object[] { text, false });
        }
        catch
        {
            // ignore
        }
    }
}

internal static class MiniJson
{
    public static string Serialize(Dictionary<string, object> d)
    {
        var parts = new List<string>();
        foreach (var kv in d)
        {
            parts.Add("\"" + kv.Key + "\":" + Val(kv.Value));
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static string Val(object v)
    {
        if (v == null)
        {
            return "null";
        }

        if (v is bool b)
        {
            return b ? "true" : "false";
        }

        if (v is string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        if (v is Array arr)
        {
            var items = new string[arr.Length];
            for (var i = 0; i < arr.Length; i++)
            {
                items[i] = Val(arr.GetValue(i));
            }

            return "[" + string.Join(",", items) + "]";
        }

        return v.ToString();
    }

    public static object Deserialize(string json)
    {
        return SimpleParse(json.Trim());
    }

    private static object SimpleParse(string json)
    {
        if (json.StartsWith("{"))
        {
            var d = new Dictionary<string, object>();
            json = json.Substring(1, json.Length - 2);
            foreach (var pair in SplitTop(json))
            {
                var idx = pair.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var k = pair.Substring(0, idx).Trim().Trim('"');
                var v = pair.Substring(idx + 1).Trim();
                d[k] = ParseVal(v);
            }

            return d;
        }

        return null;
    }

    private static object ParseVal(string v)
    {
        if (v == "true")
        {
            return true;
        }

        if (v == "false")
        {
            return false;
        }

        if (v.StartsWith("\""))
        {
            return v.Trim('"');
        }

        if (v.StartsWith("{"))
        {
            return SimpleParse(v);
        }

        if (v.StartsWith("["))
        {
            var list = new List<object>();
            var inner = v.Substring(1, v.Length - 2).Trim();
            if (inner.Length == 0)
            {
                return list;
            }

            foreach (var part in SplitTop(inner))
            {
                list.Add(ParseVal(part.Trim()));
            }

            return list;
        }

        if (long.TryParse(v, out var num))
        {
            return num;
        }

        return v;
    }

    private static IEnumerable<string> SplitTop(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '{' || c == '[')
            {
                depth++;
            }
            else if (c == '}' || c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            yield return s.Substring(start);
        }
    }
}
