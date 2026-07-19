// 注入到 hotfix.dll.bytes 内运行。编译时不依赖 Hotfix/Unity 引用，运行时在同程序集内反射调用。
// IPC：%USERPROFILE%\.seqchapter_helper\instances\inst_{pid}\

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

public static class SeqChapterHelperBridge
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

    private const float TickIntervalSec = 0.5f;
    private const double LightHeartbeatSec = 0.5;
    private const double FullHeartbeatLoginSec = 1.0;
    private const double FullHeartbeatInGameSec = 3.0;

    private static readonly Dictionary<string, Type> _typeByName = new Dictionary<string, Type>(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> _indexedAssemblies = new HashSet<Assembly>();
    private static readonly Dictionary<string, object> _managerCache = new Dictionary<string, object>(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> _stateCache = new Dictionary<string, object>(StringComparer.Ordinal);
    private static MethodInfo _getPlayerFromUidMethod;
    private static MethodInfo _getServerTimeMethod;
    private static MethodInfo _teamIsTeamMethod;
    private static MethodInfo _teamGetTeamNumMethod;
    private static MethodInfo _teamGetTeamMulitCountMethod;
    private static MethodInfo _teamIsLeaderMethod;
    private static MethodInfo _netGetZoneIdMethod;
    private static Type _teamMgrTypeForMethods;

    public static void InitFromStart()
    {
        var pid = ResolveCurrentProcessId();

        _instanceId = "inst_" + pid;
        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seqchapter_helper", "instances", _instanceId);
        Directory.CreateDirectory(_baseDir);
        WriteState("boot", "bridge_started");
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

            var tick = typeof(SeqChapterHelperBridge).GetMethod(
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
        AppendPositionFields(st);

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
            AppendResourceFields(st);
            if (phase == "login")
            {
                AppendNetworkFields(st);
            }
        }

        st["workflow_active"] = _workflowActive;
        st["workflow_steps"] = _workflow.Count;
        st["workflow_current"] = _workflow.Count > 0 ? _workflow.Peek() : "";
        st["workflow_error"] = _workflowError ?? "";
        st["workflow_done"] = !_workflowActive && string.IsNullOrEmpty(_workflowError) && _workflowDoneFlag;
        WriteJson("state.json", st);
    }

    private static bool _workflowDoneFlag;

    private static string GuessPhase()
    {
        if (string.IsNullOrEmpty(GetStaticString("PlayerDataHolder", "MainPlayerUid")))
        {
            return "login";
        }

        if (GetStaticBool("BattleDataHolder", "IsInBattle"))
        {
            return "battle";
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
            case "fetch_resource_status":
                return DoFetchResourceStatus(out msg);
            case "switch_char":
                return DoSwitchChar(GetInt(prm, "index", 0), out msg);
            case "one_key_summon":
                return DoOneKeySummon(out msg);
            case "workflow_step1":
                return StartWorkflowStep1(GetStr(prm, "phone"), GetStr(prm, "password"), out msg);
            case "workflow_login_enter":
                return StartWorkflowLoginEnter(GetStr(prm, "phone"), GetStr(prm, "password"), out msg);
            case "nav_general":
                return DoNavGeneral(GetInt(prm, "floor", 0), GetInt(prm, "x", 0), GetInt(prm, "y", 0), out msg);
            case "nav_walk_map":
                return DoNavWalkMapPoint(GetInt(prm, "floor", 0), GetInt(prm, "x", 0), GetInt(prm, "y", 0), out msg);
            case "nav_walk_same":
                return DoNavWalkSameMap(GetInt(prm, "x", 0), GetInt(prm, "y", 0), out msg);
            case "nav_task":
                return DoNavTask(GetInt(prm, "floor", 0), GetInt(prm, "x", 0), GetInt(prm, "y", 0), out msg);
            case "nav_stop":
                return DoNavStop(out msg);
            case "send_proto":
                return DoSendProto(
                    GetInt(prm, "opcode", 0),
                    GetStr(prm, "opcode_name"),
                    GetStr(prm, "proto_type"),
                    GetDict(prm, "fields"),
                    GetStr(prm, "uid"),
                    GetStr(prm, "data_b64"),
                    out msg);
            case "send_gm":
                return DoSendGm(GetStr(prm, "text"), GetStr(prm, "uid"), out msg);
            case "open_panel":
                return DoOpenPanel(GetStr(prm, "panel_id"), GetStr(prm, "uid"), out msg);
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

        var noticeMsg = "";
        TryCloseNoticePanel(out noticeMsg);

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
        msg = string.IsNullOrEmpty(noticeMsg) ? "OnClicklogin invoked" : noticeMsg + "; OnClicklogin invoked";
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

        var players = GetInstanceField(panel, "m_Players") as IList;
        var selectIndex = Convert.ToInt32(GetInstanceField(panel, "m_SelectIndex") ?? -1);
        var uid = "";
        if (players != null && selectIndex >= 0 && selectIndex < players.Count)
        {
            uid = GetProperty(players[selectIndex], "Uid") as string ?? "";
        }

        msg = "multi selected index=" + index + " selectIndex=" + selectIndex + " uid=" + uid;
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

        var players = GetInstanceField(panel, "m_Players") as IList;
        var uid = "";
        if (players != null && selectIndex >= 0 && selectIndex < players.Count)
        {
            uid = GetProperty(players[selectIndex], "Uid") as string ?? "";
        }

        msg = "one key summon via MulitPanel uid=" + uid;
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

    private static bool IsUiPanelOpen(object panel)
        => IsUiPanelVisible(panel);

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
        }
        else
        {
            _workflowError = note ?? "workflow failed";
            WriteState("workflow_error", _workflowError);
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
        else if (step == "close_share_panel")
        {
            // handled above (retry until closed or timeout)
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
        var onlineParts = new List<string>();
        var teamParts = new List<string>();
        var total = 0;
        var online = 0;
        var manualTeamMulti = 0;

        if (players != null)
        {
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null)
                {
                    onlineParts.Add("0");
                    teamParts.Add("0");
                    continue;
                }

                var uid = GetProperty(p, "Uid") as string;
                if (string.IsNullOrEmpty(uid))
                {
                    onlineParts.Add("0");
                    teamParts.Add("0");
                    continue;
                }

                total++;
                var on = Convert.ToInt32(GetProperty(p, "Online") ?? 0);
                var inTeam = on > 0 && IsTeamUid(teamMgr, uid);
                onlineParts.Add(on > 0 ? "1" : "0");
                teamParts.Add(inTeam ? "1" : "0");
                if (on > 0)
                {
                    online++;
                }

                if (inTeam)
                {
                    manualTeamMulti++;
                }
            }
        }

        var teamMulti = InvokeTeamMgrInt("GetTeamMulitCount");
        var teamNum = InvokeTeamMgrInt("GetTeamNum");
        if (teamMulti <= 0)
        {
            teamMulti = manualTeamMulti;
        }

        st["multi_count"] = total;
        st["multi_online"] = online;
        st["multi_ready"] = total > 0 && online >= total;
        var slot0Uid = GetFirstMultiUid(players);
        var leaderUid = GetTeamLeaderUid();
        var leaderOk = teamNum >= 5
            && !string.IsNullOrEmpty(slot0Uid)
            && IsLeaderUid(teamMgr, slot0Uid);

        st["team_num"] = teamNum;
        st["team_multi_count"] = teamMulti;
        st["team_leader_uid"] = leaderUid;
        st["multi_slot0_uid"] = slot0Uid;
        st["team_leader_ok"] = leaderOk;
        st["team_ok"] = teamNum >= 5;
        st["multi_online_slots"] = string.Join(",", onlineParts.ToArray());
        st["multi_team_slots"] = string.Join(",", teamParts.ToArray());
    }

    private static void AppendResourceFields(Dictionary<string, object> st)
    {
        var poolMax = Convert.ToInt32(GetStaticField("PlayerDataHolder", "HpMpPoolMax") ?? 0);
        st["hp_mp_pool_max"] = poolMax;

        var mainUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        AppendTeamPoolFields(st, mainUid);

        var multi = GetManagerProperty("TeamManager", "MultiInfo");
        var players = GetProperty(multi, "Players") as IList;
        var hpParts = new List<string>();
        var mpParts = new List<string>();
        var hpPoolOnParts = new List<string>();
        var mpPoolOnParts = new List<string>();
        var stoneParts = new List<string>();
        var stoneLimitParts = new List<string>();
        var vipParts = new List<string>();
        var nameParts = new List<string>();
        var uidParts = new List<string>();

        if (players != null)
        {
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var uid = p == null ? "" : GetProperty(p, "Uid") as string ?? "";
                if (string.IsNullOrEmpty(uid))
                {
                    hpParts.Add("-");
                    mpParts.Add("-");
                    hpPoolOnParts.Add("-");
                    mpPoolOnParts.Add("-");
                    stoneParts.Add("-");
                    stoneLimitParts.Add("-");
                    vipParts.Add("-");
                    nameParts.Add("-");
                    uidParts.Add("-");
                    continue;
                }

                var pd = GetPlayerFromUid(uid);
                var buff = TryGetPlayerBuff(uid);
                hpParts.Add(FormatIntPair(pd, "hp", "maxHp"));
                mpParts.Add(FormatIntPair(pd, "mp", "maxMp"));
                hpPoolOnParts.Add(FormatBool(GetBuffBool(buff, "HpStatus")));
                mpPoolOnParts.Add(FormatBool(GetBuffBool(buff, "FpStatus")));
                var vip = IsVipActive(pd);
                TryGetStoneDrop(buff, vip, out var stoneCount, out var stoneLimit);
                stoneParts.Add(stoneCount.ToString());
                stoneLimitParts.Add(stoneLimit.ToString());
                vipParts.Add(vip ? "1" : "0");
                var name = GetPlayerString(pd, "name");
                nameParts.Add(string.IsNullOrEmpty(name) ? ("#" + (i + 1)) : name);
                uidParts.Add(uid);
            }
        }

        st["char_hp"] = string.Join("|", hpParts.ToArray());
        st["char_mp"] = string.Join("|", mpParts.ToArray());
        st["char_hp_pool_on"] = string.Join("|", hpPoolOnParts.ToArray());
        st["char_mp_pool_on"] = string.Join("|", mpPoolOnParts.ToArray());
        st["char_stone"] = string.Join("|", stoneParts.ToArray());
        st["char_stone_limit"] = string.Join("|", stoneLimitParts.ToArray());
        st["char_vip"] = string.Join("|", vipParts.ToArray());
        st["char_names"] = string.Join("|", nameParts.ToArray());
        st["char_uids"] = string.Join("|", uidParts.ToArray());
        st["resource_uid"] = mainUid;
    }

    private static void AppendPositionFields(Dictionary<string, object> st)
    {
        var playerData = GetStaticField("PlayerDataHolder", "playerData");
        var mapId = 0;
        var floor = 0;
        if (playerData != null)
        {
            mapId = Convert.ToInt32(GetProperty(playerData, "mapId") ?? 0);
            floor = Convert.ToInt32(GetProperty(playerData, "floor") ?? 0);
        }

        GetLocationCoords(out var x, out var y);
        var currentFloor = GetMapManagerCurrentFloor();
        var navFloor = currentFloor > 0 ? currentFloor : floor;

        st["pos_map_id"] = mapId;
        st["pos_floor"] = floor;
        st["pos_current_floor"] = currentFloor;
        st["pos_nav_floor"] = navFloor;
        st["pos_x"] = x;
        st["pos_y"] = y;
        st["pos_key"] = navFloor + ":" + x + ":" + y;
    }

    private static void AppendNetworkFields(Dictionary<string, object> st)
    {
        st["net_server_url"] = TryGetGameSettingString("ServerUrl");
        st["net_cdn_url"] = TryGetGameSettingString("CDNUrl");
        st["net_server_zone"] = TryGetGameSettingString("ServerZone");
        st["net_zone_id"] = TryGetNetManagerInt("GetZoneId");

        var server = GetStaticField("PlayerDataHolder", "currentServerInfo");
        if (server != null)
        {
            st["net_game_host"] = GetProperty(server, "host") as string ?? "";
            st["net_game_port"] = Convert.ToInt32(GetProperty(server, "port") ?? 0);
            st["net_game_name"] = GetProperty(server, "name") as string ?? "";
            st["net_game_server_id"] = Convert.ToInt32(GetProperty(server, "serverid") ?? 0);
        }
        else
        {
            st["net_game_host"] = "";
            st["net_game_port"] = 0;
            st["net_game_name"] = "";
            st["net_game_server_id"] = 0;
        }
    }

    private static string TryGetGameSettingString(string propertyName)
    {
        var inst = GetSingletonInstance("GameSetting");
        if (inst == null)
        {
            return "";
        }

        return GetProperty(inst, propertyName) as string ?? "";
    }

    private static int TryGetNetManagerInt(string methodName)
    {
        var netMgr = GetManagerInstance("NetManager");
        if (netMgr == null)
        {
            return 0;
        }

        if (methodName == "GetZoneId")
        {
            if (_netGetZoneIdMethod == null)
            {
                _netGetZoneIdMethod = netMgr.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (_netGetZoneIdMethod == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(_netGetZoneIdMethod.Invoke(netMgr, null) ?? 0);
            }
            catch
            {
                return 0;
            }
        }

        return 0;
    }

    private static object GetSingletonInstance(string typeName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        foreach (var propName in new[] { "Instance", "instance" })
        {
            var prop = t.GetProperty(
                propName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (prop == null)
            {
                continue;
            }

            try
            {
                var inst = prop.GetValue(null, null);
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

        var baseType = FindType("Singleton`1");
        if (baseType != null)
        {
            foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (method.Name != "get_Instance" || !method.IsStatic)
                {
                    continue;
                }

                try
                {
                    return method.Invoke(null, null);
                }
                catch
                {
                    // try next
                }
            }
        }

        return null;
    }

    private static bool DoNavGeneral(int floor, int x, int y, out string msg)
    {
        msg = "";
        if (floor <= 0)
        {
            msg = "floor/mapIndex required";
            return false;
        }

        if (!TryMakeMapPoint(floor, x, y, out var mapPoint))
        {
            msg = "MapPoint create failed";
            return false;
        }

        var entity = GetPlayerEntity();
        if (entity == null)
        {
            msg = "playerEntity missing (not in game?)";
            return false;
        }

        if (!InvokeMethodWithMapPoint(entity, "GeneralPointMoveTo", mapPoint))
        {
            msg = "GeneralPointMoveTo missing";
            return false;
        }

        msg = "GeneralPointMoveTo floor=" + floor + " (" + x + "," + y + ")";
        return true;
    }

    private static bool DoNavWalkMapPoint(int floor, int x, int y, out string msg)
    {
        msg = "";
        if (floor <= 0)
        {
            msg = "floor/mapIndex required";
            return false;
        }

        if (!TryMakeMapPoint(floor, x, y, out var mapPoint))
        {
            msg = "MapPoint create failed";
            return false;
        }

        var walkSys = GetWalkSystem();
        if (walkSys == null)
        {
            msg = "walkSystem missing";
            return false;
        }

        var ret = InvokeWalkMoveToMapPoint(walkSys, mapPoint);
        msg = "WalkSystem.MoveTo(MapPoint) floor=" + floor + " (" + x + "," + y + ") ret=" + ret;
        return true;
    }

    private static bool DoNavWalkSameMap(int x, int y, out string msg)
    {
        msg = "";
        var walkSys = GetWalkSystem();
        if (walkSys == null)
        {
            msg = "walkSystem missing";
            return false;
        }

        var vec = MakeVector2Int(x, y);
        if (vec == null)
        {
            msg = "Vector2Int create failed";
            return false;
        }

        var ret = InvokeWalkMoveToVector(walkSys, vec);
        msg = "WalkSystem.MoveTo(same map) (" + x + "," + y + ") ret=" + ret;
        return true;
    }

    private static bool DoNavTask(int floor, int x, int y, out string msg)
    {
        msg = "";
        if (floor <= 0)
        {
            msg = "floor/mapIndex required";
            return false;
        }

        if (!TryMakeMapPoint(floor, x, y, out var mapPoint))
        {
            msg = "MapPoint create failed";
            return false;
        }

        var missionType = FindType("MissionSystem");
        if (missionType == null)
        {
            msg = "MissionSystem missing";
            return false;
        }

        foreach (var method in missionType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "TaskMoveTo")
            {
                continue;
            }

            var ps = method.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType.Name != "MapPoint")
            {
                continue;
            }

            try
            {
                var args = ps.Length == 1
                    ? new[] { mapPoint }
                    : new object[] { mapPoint, null };
                var ret = method.Invoke(null, args);
                msg = "MissionSystem.TaskMoveTo floor=" + floor + " (" + x + "," + y + ") ret=" + ret;
                return true;
            }
            catch (Exception ex)
            {
                msg = "TaskMoveTo: " + ex.Message;
                return false;
            }
        }

        msg = "TaskMoveTo missing";
        return false;
    }

    private static bool DoNavStop(out string msg)
    {
        var stopped = false;
        var walkSys = GetWalkSystem();
        if (walkSys != null)
        {
            InvokeInstanceMethod(walkSys, "StopMove", true);
            stopped = true;
        }

        var taskMgr = GetManagerInstance("TaskManager");
        if (taskMgr != null)
        {
            InvokeInstanceMethod(taskMgr, "CancelTaskPathfinding");
            stopped = true;
        }

        msg = stopped ? "navigation stopped" : "walkSystem/TaskManager missing";
        return stopped;
    }

    private static object GetPlayerEntity()
    {
        var pm = GetManagerInstance("PlayerManager");
        return pm == null ? null : GetProperty(pm, "playerEntity");
    }

    private static object GetWalkSystem()
    {
        var pm = GetManagerInstance("PlayerManager");
        return pm == null ? null : GetProperty(pm, "walkSystem");
    }

    private static int GetMapManagerCurrentFloor()
    {
        var inst = GetMonoSingletonInstance("MapManager");
        if (inst == null)
        {
            return 0;
        }

        var v = GetProperty(inst, "currentFloor");
        return v == null ? 0 : Convert.ToInt32(v);
    }

    private static object GetMonoSingletonInstance(string typeName)
    {
        var hostType = FindType(typeName);
        if (hostType == null)
        {
            return null;
        }

        for (var t = hostType; t != null; t = t.BaseType)
        {
            foreach (var name in new[] { "instance", "Instance" })
            {
                var prop = t.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (prop == null)
                {
                    continue;
                }

                try
                {
                    var inst = prop.GetValue(null, null);
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

    private static void GetLocationCoords(out int x, out int y)
    {
        x = 0;
        y = 0;

        var entity = GetPlayerEntity();
        if (entity != null && TryReadVector2Int(GetProperty(entity, "location"), out x, out y))
        {
            return;
        }

        if (TryReadVector2Int(GetStaticProperty("PlayerDataHolder", "location"), out x, out y))
        {
            return;
        }

        if (TryReadVector2Int(GetStaticField("PlayerDataHolder", "m_location"), out x, out y))
        {
            return;
        }

        TryReadVector2Int(GetStaticProperty("PlayerDataHolder", "serverLocation"), out x, out y);
    }

    private static bool TryReadVector2Int(object vec, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (vec == null)
        {
            return false;
        }

        if (TryReadVector2IntComponent(vec, "x", out x) && TryReadVector2IntComponent(vec, "y", out y))
        {
            return true;
        }

        if (TryReadVector2IntComponent(vec, "m_X", out x) && TryReadVector2IntComponent(vec, "m_Y", out y))
        {
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static bool TryReadVector2IntComponent(object vec, string name, out int value)
    {
        value = 0;
        var raw = GetProperty(vec, name) ?? GetInstanceField(vec, name);
        if (raw == null)
        {
            return false;
        }

        value = Convert.ToInt32(raw);
        return true;
    }

    private static object GetStaticProperty(string typeName, string propertyName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        var prop = t.GetProperty(
            propertyName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (prop == null)
        {
            return null;
        }

        try
        {
            return prop.GetValue(null, null);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryMakeMapPoint(int mapIndex, int x, int y, out object mapPoint)
    {
        mapPoint = null;
        var t = FindType("MapPoint");
        if (t == null)
        {
            return false;
        }

        foreach (var ctor in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var ps = ctor.GetParameters();
            if (ps.Length < 3)
            {
                continue;
            }

            try
            {
                var args = new object[ps.Length];
                args[0] = Convert.ChangeType(mapIndex, ps[0].ParameterType);
                args[1] = Convert.ChangeType(x, ps[1].ParameterType);
                args[2] = Convert.ChangeType(y, ps[2].ParameterType);
                for (var i = 3; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsClass ? null : Activator.CreateInstance(ps[i].ParameterType);
                }

                mapPoint = ctor.Invoke(args);
                return true;
            }
            catch
            {
                // try next ctor
            }
        }

        return false;
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

    private static bool InvokeMethodWithMapPoint(object target, string methodName, object mapPoint)
    {
        if (target == null)
        {
            return false;
        }

        foreach (var m in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != methodName)
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType.Name != "MapPoint")
            {
                continue;
            }

            try
            {
                var args = ps.Length == 1
                    ? new[] { mapPoint }
                    : new object[] { mapPoint, null };
                m.Invoke(target, args);
                return true;
            }
            catch
            {
                // try next overload
            }
        }

        return false;
    }

    private static object InvokeWalkMoveToMapPoint(object walkSys, object mapPoint)
    {
        foreach (var m in walkSys.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "MoveTo")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length < 1 || ps[0].ParameterType.Name != "MapPoint")
            {
                continue;
            }

            try
            {
                var args = ps.Length == 1
                    ? new[] { mapPoint }
                    : ps.Length == 2
                        ? new object[] { mapPoint, null }
                        : new object[] { mapPoint, null, false };
                return m.Invoke(walkSys, args);
            }
            catch
            {
                // try next overload
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

    private static void AppendTeamPoolFields(Dictionary<string, object> st, string uid)
    {
        var buff = TryGetPlayerBuff(uid);
        if (buff == null)
        {
            st["pool_hp"] = 0;
            st["pool_mp"] = 0;
            st["pool_hp_max"] = 0;
            st["pool_mp_max"] = 0;
            st["buff_ready"] = false;
            return;
        }

        st["pool_hp"] = GetBuffInt(buff, "HpfpHp");
        st["pool_mp"] = GetBuffInt(buff, "HpfpFp");
        st["pool_hp_max"] = GetBuffInt(buff, "HpfpMaxhp");
        st["pool_mp_max"] = GetBuffInt(buff, "HpfpMaxfp");
        st["buff_ready"] = string.Equals(
            GetProperty(buff, "Type") as string,
            "玩家BUFF数据",
            StringComparison.Ordinal);
    }

    private static bool DoFetchResourceStatus(out string msg)
    {
        msg = "";
        var mainUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        if (string.IsNullOrEmpty(mainUid))
        {
            msg = "no player uid";
            return false;
        }

        var roleMgr = GetManagerInstance("RoleManager");
        if (roleMgr == null)
        {
            msg = "RoleManager missing";
            return false;
        }

        try
        {
            var requested = 0;
            InvokeRoleSendHpFp(roleMgr, "血魔池状态", mainUid);
            InvokeRoleSendHpFp(roleMgr, "血魔池设置", mainUid);

            var multi = GetManagerProperty("TeamManager", "MultiInfo");
            var players = GetProperty(multi, "Players") as IList;
            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    var uid = p == null ? "" : GetProperty(p, "Uid") as string ?? "";
                    if (string.IsNullOrEmpty(uid))
                    {
                        continue;
                    }

                    RequestPlayerBuffData(uid);
                    requested++;
                }
            }
            else
            {
                RequestPlayerBuffData(mainUid);
                requested = 1;
            }

            msg = "requested pool + buff x" + requested;
            _fullHeartbeatAt = 0;
            return true;
        }
        catch (Exception ex)
        {
            msg = "fetch_resource_status: " + ex.Message;
            return false;
        }
    }

    private static void InvokeRoleSendHpFp(object roleMgr, string type, string uid)
    {
        var method = roleMgr.GetType().GetMethod(
            "SendHpFp",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(string), typeof(int) },
            null);
        if (method != null)
        {
            method.Invoke(roleMgr, new object[] { type, uid, 0 });
        }
    }

    private static bool DoSendProto(
        int opcodeInt,
        string opcodeName,
        string protoTypeName,
        Dictionary<string, object> fields,
        string uid,
        string dataB64,
        out string msg)
    {
        msg = "";
        if (string.IsNullOrWhiteSpace(protoTypeName))
        {
            msg = "proto_type required";
            return false;
        }

        if (!ResolveLssProto(opcodeInt, opcodeName, out var opcode, out msg))
        {
            return false;
        }

        var protoType = FindType(protoTypeName.Trim());
        if (protoType == null)
        {
            msg = "proto type not found: " + protoTypeName;
            return false;
        }

        object proto;
        try
        {
            if (!string.IsNullOrWhiteSpace(dataB64))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(dataB64.Trim());
                }
                catch (Exception ex)
                {
                    msg = "data_b64 invalid: " + ex.Message;
                    return false;
                }

                proto = CreateProtoFromBytes(protoType, bytes);
                if (proto == null)
                {
                    msg = "ReadMsg failed for " + protoTypeName;
                    return false;
                }
            }
            else
            {
                proto = Activator.CreateInstance(protoType);
                if (fields != null)
                {
                    foreach (var kv in fields)
                    {
                        if (!TrySetProtoMember(proto, kv.Key, kv.Value, out var setErr))
                        {
                            msg = setErr;
                            return false;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            msg = "proto create: " + ex.Message;
            return false;
        }

        if (!string.IsNullOrEmpty(uid))
        {
            TrySetProtoMember(proto, "KUid", uid, out _);
        }
        else
        {
            var selectUid = GetStaticString("PlayerDataHolder", "SelectPlayerUid");
            if (!string.IsNullOrEmpty(selectUid))
            {
                TrySetProtoMember(proto, "KUid", selectUid, out _);
            }
        }

        var netMgr = GetManagerInstance("NetManager");
        if (netMgr == null)
        {
            msg = "NetManager missing";
            return false;
        }

        var send = netMgr.GetType().GetMethod(
            "SendMessage",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (send == null)
        {
            msg = "SendMessage missing";
            return false;
        }

        try
        {
            send.Invoke(netMgr, new[] { opcode, proto });
        }
        catch (Exception ex)
        {
            msg = "SendMessage: " + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }

        var opcodeLabel = !string.IsNullOrWhiteSpace(opcodeName)
            ? opcodeName.Trim()
            : opcodeInt.ToString();
        msg = "sent " + opcodeLabel + " " + protoTypeName;
        return true;
    }

    private static bool DoOpenPanel(string panelId, string uid, out string msg)
    {
        msg = "";
        if (string.IsNullOrWhiteSpace(panelId))
        {
            msg = "panel_id empty";
            return false;
        }

        var id = panelId.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(uid))
        {
            uid = GetStaticString("PlayerDataHolder", "SelectPlayerUid");
        }

        switch (id)
        {
            case "gm1":
                return OpenUiPanelSimple("GMToolsPanel", out msg);
            case "gm2":
                return OpenUiPanelSimple("GMStorePanel", out msg);
            case "gm3":
                return OpenUiPanelSimple("GMPetStorePanel", out msg);
            case "gm4":
                return OpenUiPanelSimple("GMPetEffectPanel", out msg);
            case "gm5":
                return OpenUiPanelSimple("GMAnimationSettingPanel", out msg);
            case "crystal":
                return OpenUiPanelSimple("LuckCrystalPanel", out msg);
            case "boss":
                return OpenUiPanelSimple("BOSSChallengePanel", out msg);
            case "ruby":
                return OpenUiPanelSimple("RubyTrialPanel", out msg);
            case "tower":
                return OpenBossChallengeTower(out msg);
            case "autoskill":
                return OpenAutoSkillPanel(uid, out msg);
            case "blindbox":
                return OpenBlindboxPanel(uid, out msg);
            case "lottery":
                return OpenUiPanelSimple("LotteryPanel", out msg);
            case "challengeboss":
                return OpenUiPanelSimple("ChallengeBossPanel", out msg);
            case "bravetrial":
                return OpenUiPanelSimple("BraveTrialPanel", out msg);
            default:
                msg = "unknown panel_id: " + panelId;
                return false;
        }
    }

    private static bool OpenUiPanelSimple(string panelTypeName, out string msg)
    {
        msg = "";
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", panelTypeName);
        if (panel == null)
        {
            msg = panelTypeName + " not found";
            return false;
        }

        try
        {
            InvokeInstanceMethod(panel, "Open");
        }
        catch (Exception ex)
        {
            msg = panelTypeName + ".Open: " + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }

        msg = panelTypeName + " opened";
        return true;
    }

    private static bool OpenAutoSkillPanel(string uid, out string msg)
    {
        msg = "";
        if (string.IsNullOrEmpty(uid))
        {
            msg = "uid empty (SelectPlayerUid missing)";
            return false;
        }

        var mgr = GetManagerInstance("BattleAutoSkillManager");
        if (mgr == null)
        {
            msg = "BattleAutoSkillManager missing";
            return false;
        }

        if (!InvokePrivateMethod(mgr, "OpenAutoSkillSettingPanel", new object[] { uid }, out msg))
        {
            msg = string.IsNullOrEmpty(msg) ? "OpenAutoSkillSettingPanel failed" : msg;
            return false;
        }

        msg = "AutoSkillSettingPanel opened uid=" + uid;
        return true;
    }

    private static bool OpenBlindboxPanel(string uid, out string msg)
    {
        msg = "";
        if (string.IsNullOrEmpty(uid))
        {
            msg = "uid empty (SelectPlayerUid missing)";
            return false;
        }

        var actMgr = GetManagerInstance("ActivityManager");
        if (actMgr == null)
        {
            msg = "ActivityManager missing";
            return false;
        }

        if (!InvokePrivateMethod(
                actMgr,
                "SendBlindboxDraw",
                new object[] { "获取数据", uid, null },
                out msg))
        {
            msg = string.IsNullOrEmpty(msg) ? "SendBlindboxDraw failed" : msg;
            return false;
        }

        msg = "BlindboxDraw 获取数据 sent (面板在回包后打开)";
        return true;
    }

    private static bool OpenBossChallengeTower(out string msg)
    {
        msg = "";
        var panel = InvokeStaticGeneric("UIManager", "GetUIPanel", "BOSSChallengePanel");
        if (panel == null)
        {
            msg = "BOSSChallengePanel not found";
            return false;
        }

        try
        {
            InvokeInstanceMethod(panel, "Open");
        }
        catch (Exception ex)
        {
            msg = "BOSSChallengePanel.Open: " + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }

        if (InvokePrivateMethod(panel, "OnValueChangeTab1", new object[] { true }, out var tabErr))
        {
            msg = "BOSSChallengePanel opened (无尽之塔 Tab)";
            return true;
        }

        var tab2 = GetInstanceField(panel, "m_Tog_Tab_2");
        if (tab2 != null)
        {
            SetProperty(tab2, "IsOn", true);
            msg = "BOSSChallengePanel opened (m_Tog_Tab_2)";
            return true;
        }

        msg = "BOSSChallengePanel opened (Tab switch failed: " + tabErr + ")";
        return true;
    }

    private static bool DoSendGm(string text, string uid, out string msg)
    {
        msg = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            msg = "text empty";
            return false;
        }

        var chatMgr = GetManagerInstance("ChatManager");
        if (chatMgr == null)
        {
            msg = "ChatManager missing";
            return false;
        }

        var channelType = FindType("PROTO_CHANNEL_TYPE");
        var gmField = channelType?.GetField(
            "PROTO_CHANNEL_TYPE_GM",
            BindingFlags.Public | BindingFlags.Static);
        if (gmField == null)
        {
            msg = "PROTO_CHANNEL_TYPE_GM missing";
            return false;
        }

        var gmChannel = gmField.GetValue(null);
        var useUid = string.IsNullOrEmpty(uid)
            ? GetStaticString("PlayerDataHolder", "SelectPlayerUid")
            : uid;
        if (string.IsNullOrEmpty(useUid))
        {
            useUid = GetStaticString("PlayerDataHolder", "MainPlayerUid");
        }

        foreach (var method in chatMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != "SendChatMessage")
            {
                continue;
            }

            var ps = method.GetParameters();
            if (ps.Length != 4 || ps[0].ParameterType != typeof(string) || ps[3].ParameterType != typeof(bool))
            {
                continue;
            }

            try
            {
                method.Invoke(chatMgr, new object[] { text, gmChannel, useUid, false });
                msg = "gm sent uid=" + useUid + " text=" + text;
                return true;
            }
            catch (Exception ex)
            {
                msg = "SendChatMessage: " + (ex.InnerException?.Message ?? ex.Message);
                return false;
            }
        }

        msg = "SendChatMessage(4) missing";
        return false;
    }

    private static bool ResolveLssProto(int opcodeInt, string opcodeName, out object opcode, out string err)
    {
        err = "";
        opcode = null;
        var lssType = FindType("LSSPROTO");
        if (lssType == null)
        {
            err = "LSSPROTO type missing";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(opcodeName))
        {
            var field = lssType.GetField(
                opcodeName.Trim(),
                BindingFlags.Public | BindingFlags.Static);
            if (field != null)
            {
                opcode = field.GetValue(null);
                return true;
            }

            err = "unknown opcode name: " + opcodeName;
            return false;
        }

        if (opcodeInt > 0)
        {
            opcode = Enum.ToObject(lssType, opcodeInt);
            return true;
        }

        err = "opcode or opcode_name required";
        return false;
    }

    private static object CreateProtoFromBytes(Type protoType, byte[] bytes)
    {
        var netMgrType = FindType("NetManager");
        if (netMgrType == null)
        {
            return null;
        }

        foreach (var method in netMgrType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!method.IsGenericMethod || method.Name != "ReadMsg")
            {
                continue;
            }

            var gParams = method.GetGenericArguments();
            if (gParams.Length != 1)
            {
                continue;
            }

            try
            {
                var readMsg = method.MakeGenericMethod(protoType);
                var ps = readMsg.GetParameters();
                var args = ps.Length >= 2
                    ? new object[] { bytes, null }
                    : new object[] { bytes };
                return readMsg.Invoke(null, args);
            }
            catch
            {
                // try next overload
            }
        }

        return null;
    }

    private static bool TrySetProtoMember(object obj, string name, object rawValue, out string err)
    {
        err = "";
        if (obj == null || string.IsNullOrEmpty(name))
        {
            err = "invalid target";
            return false;
        }

        var t = obj.GetType();
        var prop = t.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite)
        {
            try
            {
                prop.SetValue(obj, ConvertProtoValue(rawValue, prop.PropertyType), null);
                return true;
            }
            catch (Exception ex)
            {
                err = name + ": " + ex.Message;
                return false;
            }
        }

        var field = t.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            try
            {
                field.SetValue(obj, ConvertProtoValue(rawValue, field.FieldType));
                return true;
            }
            catch (Exception ex)
            {
                err = name + ": " + ex.Message;
                return false;
            }
        }

        err = "member not found: " + name;
        return false;
    }

    private static object ConvertProtoValue(object rawValue, Type targetType)
    {
        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (rawValue == null)
        {
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }

        if (targetType.IsEnum)
        {
            if (rawValue is string s)
            {
                return Enum.Parse(targetType, s);
            }

            return Enum.ToObject(targetType, Convert.ToInt32(rawValue));
        }

        if (targetType == typeof(string))
        {
            return rawValue.ToString();
        }

        if (targetType == typeof(bool))
        {
            if (rawValue is bool b)
            {
                return b;
            }

            var text = rawValue.ToString();
            return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        if (targetType == typeof(int))
        {
            return Convert.ToInt32(rawValue);
        }

        if (targetType == typeof(long))
        {
            return Convert.ToInt64(rawValue);
        }

        if (targetType == typeof(uint))
        {
            return Convert.ToUInt32(rawValue);
        }

        if (targetType == typeof(float))
        {
            return Convert.ToSingle(rawValue);
        }

        if (targetType == typeof(double))
        {
            return Convert.ToDouble(rawValue);
        }

        if (rawValue is Dictionary<string, object> nested && targetType.IsClass && targetType != typeof(string))
        {
            var nestedObj = Activator.CreateInstance(targetType);
            foreach (var kv in nested)
            {
                TrySetProtoMember(nestedObj, kv.Key, kv.Value, out _);
            }

            return nestedObj;
        }

        if (targetType.IsGenericType && targetType.Name.StartsWith("RepeatedField", StringComparison.Ordinal))
        {
            var elemType = targetType.GetGenericArguments()[0];
            var repeated = Activator.CreateInstance(targetType);
            var add = targetType.GetMethod("Add", new[] { elemType });
            if (add != null && rawValue is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    add.Invoke(repeated, new[] { ConvertProtoValue(item, elemType) });
                }

                return repeated;
            }
        }

        return Convert.ChangeType(rawValue, targetType);
    }

    private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null)
        {
            return new Dictionary<string, object>();
        }

        if (v is Dictionary<string, object> dict)
        {
            return dict;
        }

        return new Dictionary<string, object>();
    }

    private static void RequestPlayerBuffData(string uid = null)
    {
        var protoType = FindType("Proto_CS_PlayerBuff");
        if (protoType == null)
        {
            return;
        }

        var proto = Activator.CreateInstance(protoType);
        SetProperty(proto, "Type", "玩家BUFF数据");
        if (!string.IsNullOrEmpty(uid))
        {
            SetProperty(proto, "KUid", uid);
        }

        var lssType = FindType("LSSPROTO");
        var opcodeField = lssType?.GetField(
            "LSSPROTO_PLAYERBUFF_FUNC",
            BindingFlags.Public | BindingFlags.Static);
        if (opcodeField == null)
        {
            return;
        }

        var netMgr = GetManagerInstance("NetManager");
        var send = netMgr?.GetType().GetMethod(
            "SendMessage",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (send == null)
        {
            return;
        }

        var opcode = opcodeField.GetValue(null);
        send.Invoke(netMgr, new[] { opcode, proto });
    }

    private static bool IsTeamOk()
    {
        return InvokeTeamMgrInt("GetTeamNum") >= 5;
    }

    private static bool TryGetStoneDrop(object buff, bool vip, out int current, out int limit)
    {
        current = 0;
        limit = vip ? 20000 : 5000;
        if (buff == null)
        {
            return false;
        }

        var infoList = GetProperty(buff, "Info") as IList;
        if (infoList == null)
        {
            return false;
        }

        for (var i = 0; i < infoList.Count; i++)
        {
            var info = infoList[i];
            if (info == null || Convert.ToInt32(GetProperty(info, "Id") ?? 0) != 10)
            {
                continue;
            }

            current = Convert.ToInt32(GetProperty(info, "Value") ?? 0);
            var timeVal = Convert.ToInt32(GetProperty(info, "Time") ?? 0);
            if (timeVal > 0)
            {
                limit = timeVal;
            }

            return true;
        }

        return false;
    }

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

    private static object GetPlayerFromUid(string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        if (_getPlayerFromUidMethod == null)
        {
            var t = FindType("PlayerDataHolder");
            _getPlayerFromUidMethod = t?.GetMethod(
                "GetPlayerFromUid",
                BindingFlags.Public | BindingFlags.Static);
        }

        return _getPlayerFromUidMethod?.Invoke(null, new object[] { uid });
    }

    private static object TryGetPlayerBuff(string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        var roleMgr = GetManagerInstance("RoleManager");
        var dict = roleMgr == null ? null : GetInstanceField(roleMgr, "m_buffInfo") as IDictionary;
        if (dict == null || !dict.Contains(uid))
        {
            return null;
        }

        return dict[uid];
    }

    private static int GetPlayerInt(object player, string fieldName)
    {
        if (player == null)
        {
            return 0;
        }

        var field = player.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            return Convert.ToInt32(field.GetValue(player) ?? 0);
        }

        return Convert.ToInt32(GetProperty(player, fieldName) ?? 0);
    }

    private static string GetPlayerString(object player, string fieldName)
    {
        if (player == null)
        {
            return "";
        }

        var field = player.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            return field.GetValue(player)?.ToString() ?? "";
        }

        return GetProperty(player, fieldName)?.ToString() ?? "";
    }

    private static int GetBuffInt(object buff, string propertyName)
        => buff == null ? 0 : Convert.ToInt32(GetProperty(buff, propertyName) ?? 0);

    private static bool GetBuffBool(object buff, string propertyName)
    {
        if (buff == null)
        {
            return false;
        }

        var v = GetProperty(buff, propertyName);
        return v is bool b && b;
    }

    private static string FormatIntPair(object player, string curField, string maxField)
    {
        if (player == null)
        {
            return "-";
        }

        var cur = GetPlayerInt(player, curField);
        var max = GetPlayerInt(player, maxField);
        return cur + "/" + max;
    }

    private static string FormatBool(bool value) => value ? "1" : "0";

    private static int GetServerTime()
    {
        if (_getServerTimeMethod == null)
        {
            var t = FindType("TimeManager");
            _getServerTimeMethod = t?.GetMethod("GetServerTime", BindingFlags.Public | BindingFlags.Static);
        }

        if (_getServerTimeMethod == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(_getServerTimeMethod.Invoke(null, null) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsVipActive(object player)
    {
        if (player == null)
        {
            return false;
        }

        var monthCardTime = GetPlayerInt(player, "MonthCardTime");
        if (monthCardTime > GetServerTime())
        {
            return true;
        }

        var playCardTime = GetPlayerInt(player, "PlayCardTime");
        return playCardTime > GetServerTime();
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

    private static bool IsTeamUid(object teamMgr, string uid)
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

        if (_teamIsTeamMethod == null)
        {
            _teamIsTeamMethod = mgrType.GetMethod("IsTeam");
        }

        if (_teamIsTeamMethod == null)
        {
            return false;
        }

        try
        {
            return Convert.ToBoolean(_teamIsTeamMethod.Invoke(teamMgr, new object[] { uid }) ?? false);
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
        if (_managerCache.TryGetValue(managerName, out var cached))
        {
            return cached;
        }

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

    private static bool GetStaticBool(string typeName, string fieldName)
    {
        var v = GetStaticField(typeName, fieldName);
        return v is bool b && b;
    }

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
