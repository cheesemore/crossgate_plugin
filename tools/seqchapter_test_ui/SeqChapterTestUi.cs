using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// 助手面板（百科入口）：概况 / 战斗模式 / AI / 脚本 / 任务护航 / 界面 / 导航 / 形象。
/// 抓宠·烧卡·九动等在「战斗模式」页互斥切换。Update+UGUI（HybridCLR 无 OnGUI）。
/// 部署 hotfixdata/SeqChapterTestUi.dll.bytes；日志 SeqChapterTestUi.log。
/// </summary>
public static class SeqChapterTestUi
{
    public const string AssetPath = "hotfixdata/SeqChapterTestUi.dll.bytes";
    public const string LogFileName = "SeqChapterTestUi.log";

    private const int TabOverview = 0;
    private const int TabBattle = 1;
    private const int TabSuperAi = 2;
    private const int TabScript = 3;
    private const int TabEscort = 4;
    private const int TabOpenUi = 5;
    private const int TabNav = 6;
    private const int TabAppear = 7;
    private const int NavWaypointPageSize = 4;

    private const string ModeNormal = "normal";
    private const string ModeNine = "nine";
    /// <summary>无宠二动：不扩九动队列；依赖 Magics PE（一动技能后二动仍可开技能）。与九动/抓宠/烧卡互斥。</summary>
    private const string ModeNopet2Act = "nopet_2act";
    private const string ModeCatch = "catch";
    private const string ModeCatchSell = "catch_sell";
    private const string ModeSeal = "seal";
    private const string ModeCatchNopet = "catch_nopet";
    private const string ModeLv1 = "lv1";
    private const string ModeCountFarm = "count_farm";

    private static readonly object LogLock = new object();
    private static readonly Dictionary<string, long> MoshiBuffReqMs =
        new Dictionary<string, long>(StringComparer.Ordinal);
    private static string _logPath;
    private static bool _bootLogged;
    private static bool _visible;
    /// <summary>面板收缩到左上角小按钮。</summary>
    private static bool _minimized;
    private static int _wikiCalls;
    private static int _updateCalls;
    private static long _lastToggleMs;
    private static long _lastOverviewRefreshMs;
    private static long _lastDialogueClickMs;
    private static int _lastDialogueSeqno = int.MinValue;
    private static int _dialogueAutoClicks;
    private const long DebounceMs = 400;
    private const long OverviewRefreshMs = 500;
    /// <summary>两次对话面板弹出间隔实测约 0.5s；0.8s 足够，不必等 SendWindows 的 2s 冷却。</summary>
    private const long DialogueClickIntervalMs = 800;
    private const long StuckIdleMs = 5000;
    /// <summary>卡图判定后，阶段1：先等这么久再只点任务。</summary>
    private const long StuckNavOnlyWaitMs = 2000;
    /// <summary>挪格后短观察，再点任务 / 再走回原格。</summary>
    private const long StuckResumeDelayMs = 1500;
    /// <summary>阶段2：挪动+点任务 最多次数；满后进入阶段3（挪动+走回原格+点任务）。</summary>
    private const int StuckShuffleClickMax = 5;
    /// <summary>刷灵堂仍用旧「连挪 N 次再续航」阈值。</summary>
    private const int StuckShuffleBeforeNavRetry = 5;
    private const int EscortStuckPhaseNavOnly = 0;
    private const int EscortStuckPhaseShuffleClick = 1;
    private const int EscortStuckPhaseClassic = 2;
    private const int StuckKindNone = 0;
    private const int StuckKindNavOnlyWait = 1;
    private const int StuckKindShuffleClick = 2;
    private const int StuckKindClassicReturn = 3;
    private const int StuckKindClassicClick = 4;
    /// <summary>单一步骤内卡楼梯恢复累计达此次数 → 自动暂停（换步骤才重置）。</summary>
    private const int EscortMaxRecoverFails = 20;
    /// <summary>遇敌 1 级提示铃（BattleProcesser LevelOneFlag → AudioUtil.PlaySE）。</summary>
    private const int LevelOneAlertSeId = 476;
    /// <summary>自动暂停时循环播铃间隔（未知 SE 时长时按 2s 重播）。</summary>
    private const long AlertRingIntervalMs = 2000;

    private static GameObject _hostGo;
    private static Component _hostComp;
    private static object _canvasGo;
    private static object _shellGo;
    private static object _miniFabGo;
    private static object _bodyRoot;
    private static object _overviewText;
    private static object _escortStatusText;
    private static object _navPosText;
    private static object _navStatusText;
    private static object _navFloorInput;
    private static object _navXInput;
    private static object _navYInput;
    private static object _navNameInput;
    private static string _navFloorStr = "";
    private static string _navXStr = "";
    private static string _navYStr = "";
    private static string _navNameStr = "";
    private static string _navStatusLine = "";
    /// <summary>抓宠卖银币：回收掉档阈值 Y（与 SeqChapterAutoCatchSell 读同一 json）。</summary>
    private static object _catchSellYInput;
    private static string _catchSellYStr = "6";
    private const int CatchSellDefaultY = 6;
    private static int _navWpPage;
    private static readonly List<NavWaypoint> _navWaypoints = new List<NavWaypoint>();
    private static readonly List<object> _tabButtons = new List<object>();
    private static readonly List<object> _modeButtons = new List<object>();
    private static readonly List<string> _modeIds = new List<string>();
    private static int _tab = TabOverview;
    private static string _battleMode = ModeNormal;
    private static string _statusLine = "";

    // ----- 窗口标题统一协调（各功能 DLL 后缀合并） -----
    private static long _lastTitleRefreshMs;
    private const long TitleRefreshIntervalMs = 2000;
    private static string _lastTitle = "";

    // ----- 进战宠物形象（轻量配置；预览在游戏外 Python） -----
    private static object _appearStatusText;
    private static object _appearEnableBtn;
    private static readonly object[] _appearAnimInputs = new object[5];
    private static readonly object[] _appearPerfectBtns = new object[5];
    private static readonly int[] _appearPerfect = { -1, -1, -1, -1, -1 };
    private static bool _appearEnabled;

    // ----- 任务护航（队列） -----
    /// <summary>正在编辑/追加队列（自建列表）。</summary>
    private static bool _escortPicking;
    /// <summary>队列护航进行中（含暂停）。</summary>
    private static bool _escortActive;
    /// <summary>暂停自动护航：保留队列，停止自动点对话/卡楼梯/进队；可手动接管。</summary>
    private static bool _escortPaused;
    /// <summary>最近一次暂停原因（条件诊断等），状态栏展示。</summary>
    private static string _escortPauseReason = "";
    /// <summary>最近一次点任务准备失败的诊断文案。</summary>
    private static string _escortLastDiag = "";
    /// <summary>自动暂停后循环播铃（手动点暂停不启铃）。</summary>
    private static bool _escortAlertRinging;
    private static long _escortLastAlertRingMs;
    private static int _escortMissionId = -1;
    private static string _escortMissionTitle = "";
    private static readonly List<EscortCandidate> _escortQueue = new List<EscortCandidate>();
    /// <summary>当前护航在队列中的下标；-1=未开始。</summary>
    private static int _escortQueueIndex = -1;
    /// <summary>上一任务收尾完成后，等待再开下一任务的起始时间；0=未在等待。</summary>
    private static long _escortBetweenTasksWaitMs;
    /// <summary>当前任务暂不可接时，重试开始的时间戳；0=不在重试。</summary>
    private static long _escortAwaitingReadyMs;
    /// <summary>当前步骤卡楼梯恢复累计次数；换步骤才清零（有位移不清零）。</summary>
    private static int _escortRecoverAttempts;
    /// <summary>卡图恢复阶段：0=先等2s点任务；1=挪动+点任务；2=挪动+走回+点任务。</summary>
    private static int _escortStuckPhase;
    /// <summary>阶段1内「挪动+点任务」已执行次数。</summary>
    private static int _escortStuckShuffles;
    /// <summary>记录上次观测到的任务子步骤 missionStepNum；-1=未知。</summary>
    private static int _escortLastStepNum = -1;
    /// <summary>当前步骤是「遇敌/打怪获取道具」，护航原地挂机等待掉落。</summary>
    private static bool _escortWaitItem;
    /// <summary>进入等待道具时的 missionStepNum。</summary>
    private static int _escortWaitAtStepNum = -1;
    /// <summary>从步骤文案「获得XXX」解析出的目标道具名；可空。</summary>
    private static string _escortWaitItemName = "";
    /// <summary>本次等待是否由护航发出「开始挂机」。</summary>
    private static bool _escortStartedEncounter;
    /// <summary>等待道具期间上一拍是否在战斗（退战边沿）。</summary>
    private static bool _escortWasInBattle;
    /// <summary>遇敌步骤：必须先到达本步导航点附近才开遇敌（格）。</summary>
    private const int EscortEncounterArriveNear = 4;
    /// <summary>
    /// 中秋 #119 月宫救兔护航特例（步骤 5 挂机传送布朗山；步骤 2 回登入点+赤凤之翼；步骤 7 挂机传送奇怪的洞窟怪）。
    /// 两个傻瓜包都带（助手面板 wiki_test_ui）。临时活动：等用户明确下令后再永久删除本开关及全部 119 特例。
    /// </summary>
    private const bool TempMidAutumnEscort119 = true;
    /// <summary>中秋 #119：队长回登入点后等待切图（登入点在阿凯鲁法）。</summary>
    private static bool _escortLoginGatePending;
    private static long _escortLoginGateAtMs;
    private const long EscortLoginGateWaitMs = 4000;
    /// <summary>中秋 #119 步骤 2：回登入点后使用赤凤之翼，再等弹窗点完再点任务。</summary>
    private static bool _escortUseItemPending;
    private static long _escortUseItemAtMs;
    private static bool _escortWingWizardSeen;
    private static long _escortWingWizardClosedAtMs;
    private static int _escortWingNextClicks;
    private static bool _escortWingPickedDest;
    /// <summary>赤凤之翼弹窗出现等待。服务端 LSSPROTO_WINDOWS 分页，可能晚半拍才开。</summary>
    private const long EscortWingWizardAppearMs = 4000;
    private const long EscortWingWizardSettleMs = 1500;
    private const long EscortWingWizardTimeoutMs = 20000;
    private const int EscortWingMaxNextClicks = 12;
    private const int WindowButtonNextValue = 0x20;
    private const string MoonRabbitWingDestKeyword = "哥拉尔";
    /// <summary>脚本页「测试赤凤之翼」：独立点分页窗，不推进护航。</summary>
    private static bool _scriptWingTestPending;
    private static long _scriptWingTestAtMs;
    private const int MoonRabbitMissionId = 119;
    private const int MoonRabbitLoginGateStep2 = 2;
    /// <summary>挑战暗影巡卫（执行序 StepID=5）：挂机传送布朗山后再导航。</summary>
    private const int MoonRabbitBrownMountainStep = 5;
    /// <summary>battle_tbautobattlenavigationconfig Id=6「布朗山」。</summary>
    private const int MoonRabbitBrownMountainTeleportId = 6;
    /// <summary>布朗山 传送落点 floor（配置 Map.floor=52709，与步骤 5 首个 PathPoint 同图）。</summary>
    private const int MoonRabbitBrownMountainFloor = 52709;
    /// <summary>调查星月落痕·礁石（执行序 StepID=7）。</summary>
    private const int MoonRabbitReefStep = 7;
    /// <summary>battle_tbautobattlenavigationconfig Id=2「奇怪的洞窟怪」。TaskManager.SendMisc Type=挂机传送。</summary>
    private const int MoonRabbitHangupTeleportId = 2;
    /// <summary>奇怪的洞窟怪 传送落点 floor（配置 Map.floor=52140）。</summary>
    private const int MoonRabbitHangupTeleportFloor = 52140;
    private const int MoonRabbitReefMapFloor = 100;
    private const int MoonRabbitReefX = 611;
    private const int MoonRabbitReefY = 26;
    private const int MoonRabbitReefNearDist = 25;
    private const string MoonRabbitWingKeyword = "赤凤之翼";
    /// <summary>
    /// 中秋 #119：仅对 15000 (22,33) 做特例。
    /// 遇敌步会把 AutoWarpIndex 设到表尾 100；切图后官方还握着「回芙蕾雅」。
    /// 先取消这条回程，再导航 15001（正向下一张），不要 RunTask（失败会回退到 100）。
    /// </summary>
    private const int MoonRabbitWarpStuckFloor = 15000;
    private const int MoonRabbitWarpStuckX = 22;
    private const int MoonRabbitWarpStuckY = 33;
    private const int MoonRabbitWarpNextFloor = 15001;
    private const int MoonRabbitWarpNextX = 31;
    private const int MoonRabbitWarpNextY = 22;
    private const int MoonRabbitWarpGoalFloor = 400;
    private const int MoonRabbitWarpGoalX = 247;
    private const int MoonRabbitWarpGoalY = 581;
    private const long MoonRabbitWarpUnstickSettleMs = 1500;
    /// <summary>第一次导航后固定隔 2 秒再试一次。</summary>
    private const long MoonRabbitWarpUnstickRetryMs = 2000;
    private const int MoonRabbitWarpUnstickMaxClicks = 2;
    /// <summary>0=无 1=已点过，等第二次。</summary>
    private static int _escort119WarpUnstickPhase;
    private static long _escort119WarpUnstickAtMs;
    private static int _escort119WarpUnstickClicks;
    private static bool _escort119WarpUnstickDone;
    /// <summary>最后一步（与月宫使者交谈，StepID=1）不交任务，改把兑换券存账号银行，任务会回到第一步。</summary>
    private const int MoonRabbitLastStep = 1;
    private const string MoonRabbitTicketKeyword = "七夕礼盒兑换券";
    private const string MoonRabbitAccountBankActivity = "远程账号道具仓库";
    private static bool _escort119TicketBankDone;
    private static bool _escort119TicketBankPending;
    private static long _escort119TicketBankAtMs;
    private static long _escort119LastStepSinceMs;
    private static readonly List<string> _escort119TicketBankUids = new List<string>();
    private static int _escort119TicketBankUidIndex;
    private static int _escort119TicketBankFailStreak;
    private static bool _escort119TicketBankAwaitConfirm;
    private static bool _escort119TicketBankAnyStored;
    private const long EscortTicketBankWaitMs = 2500;
    private const long EscortTicketMissingWaitMs = 8000;
    /// <summary>账号之间、以及存券后复查背包的间隔。</summary>
    private const long EscortTicketBankAccountGapMs = 2000;
    private const int EscortTicketBankMaxFails = 5;
    private static bool _escort119GateDone2;
    private static bool _escort119TeleportDone5;
    private static bool _escort119TeleportDone7;
    private static bool _escortHangupTeleportPending;
    private static long _escortHangupTeleportAtMs;
    private static int _escortHangupTeleportExpectFloor;
    private const long EscortHangupTeleportWaitMs = 4000;
    private static int _escort119PendingAfterGateStep = -1;
    private static int _prevRunTaskId = -999;
    private static int _lastPosX = int.MinValue;
    private static int _lastPosY = int.MinValue;
    private static long _lastActivityMs;
    private static long _stuckMoveAtMs;
    private static bool _stuckResumePending;
    private static int _stuckResumeKind;
    /// <summary>阶段3：随机挪格前的坐标，用于走回原格。</summary>
    private static int _stuckReturnX;
    private static int _stuckReturnY;
    /// <summary>护航观测到的上一地图 floor；切图只刷新静止计时，续航交给官方。</summary>
    private static int _escortLastFloor = int.MinValue;
    private static long _escortMapChangeAtMs;
    /// <summary>切图后约 1.5s 内不判普通卡图，避免打断官方续航。</summary>
    private const long EscortMapChangeSettleMs = 2000;
    /// <summary>任务已完成后，等待弹窗出现/点完的起始时间；0=未进入收尾。</summary>
    private static long _escortFinishWaitMs;
    private const long EscortFinishGraceMs = 2500;
    private const long EscortBetweenTasksMs = 5000;
    private const long EscortReadyRetryMs = 3000;
    private static readonly Random _rng = new Random();
    private static readonly List<EscortCandidate> _escortCandidates = new List<EscortCandidate>();
    private static int _escortPage;
    private const int EscortPageSize = 4;
    /// <summary>任务护航列表搜索关键字（标题 / ID / 状态）。</summary>
    private static string _escortSearch = "";
    private static object _escortSearchInput;

    // ----- 龙族纷争循环 -----
    /// <summary>龙族循环是否激活。</summary>
    private static bool _dragonLoopActive;
    /// <summary>已完成循环次数。</summary>
    private static int _dragonLoopCount;
    /// <summary>龙族循环阶段：0=未运行 1=重置龙4 2=判断可接 3=执行中 4=存包腾位。</summary>
    private static int _dragonPhase;
    /// <summary>阶段开始时间戳。</summary>
    private static long _dragonPhaseAtMs;
    /// <summary>龙3/4 使用记忆后等待服务器处理，再点任务的标志。</summary>
    private static bool _dragonUseMemoryPending;
    private static long _dragonUseMemoryAtMs;
    private const long DragonResetDelayMs = 2500;
    private const long DragonUseMemoryDelayMs = 1500;
    /// <summary>A 线：龙族纷争 1-4 全量。</summary>
    private static readonly int[] DragonMissionIds = { 110, 111, 112, 113 };
    /// <summary>当前循环实际执行的任务集。</summary>
    private static int[] _dragonMissionIds;
    private const string DragonTitleKeyword = "龙族纷争";
    /// <summary>存包腾位阶段已重试发包次数。</summary>
    private static int _dragonStoreRetries;
    private const int DragonStoreMaxRetries = 3;
    /// <summary>存包后等待空位的复检次数（兼容银行回包滞后）。</summary>
    private static int _dragonStoreRechecks;
    private const int DragonStoreMaxRechecks = 10;
    private const long DragonStoreWaitMs = 2500;
    /// <summary>phase2 判断可接时，因重置回包可能滞后，允许重试等待的次数与间隔。</summary>
    private static int _dragonCheckRetries;
    private const int DragonCheckMaxRetries = 5;
    private const long DragonCheckRetryMs = 1500;
    private const int StorePetLevel = 1;
    private const int PetStatusRest = 0;

    /// <summary>中秋 #119 循环（临时；等用户下令再删）。</summary>
    private static bool _midAutumnLoopActive;
    private static int _midAutumnLoopCount;
    /// <summary>true=哥拉尔版（登入点哥拉尔、不用赤凤之翼）；false=阿凯版（回登入点+赤凤之翼）。</summary>
    private static bool _midAutumnGoralEdition;

    // ----- 刷灵堂脚本 -----
    private static bool _lingTangActive;
    /// <summary>1..6 步骤；0=未运行。</summary>
    private static int _lingTangPhase;
    private static int _lingTangCycles;
    private static int _lingTangStuckFails;
    private static int _lingTangStuckShuffles;
    private static bool _lingTangStuckNavTriedFirst;
    private static long _lingTangLastNavMs;
    private static long _lingTangLastActivityMs;
    private static long _lingTangStuckMoveAtMs;
    private static bool _lingTangStuckPending;
    private static long _lingTangLastNpcMs;
    private static int _lingTangLastX = int.MinValue;
    private static int _lingTangLastY = int.MinValue;
    private static object _lingTangStatusText;
    private const int LingTangMaxStuckFails = 10;
    private const long LingTangNavRetryMs = 3000;
    private const long LingTangNpcRetryMs = 2500;
    private const int LingTangPhaseTo1515 = 1;
    private const int LingTangPhaseTo52026 = 2;
    private const int LingTangPhaseTo52028a = 3;
    private const int LingTangPhaseTo52028b = 4;
    private const int LingTangPhaseTo52027 = 5;
    private const int LingTangPhaseTalkNpc = 6;

    // ----- 一键命名（1级宠按捉宠逻辑改名，最多5角色 + 延迟） -----
    /// <summary>一键命名运行中。</summary>
    private static bool _petNamerActive;
    /// <summary>待处理角色 uid 列表（队伍/多控，最多5）。</summary>
    private static List<string> _petNamerUids;
    private static int _petNamerRoleIdx;
    /// <summary>当前角色内已扫描到的宠物下标（下次从这里继续）。</summary>
    private static int _petNamerPetIdx;
    private static long _petNamerNextAtMs;
    private static int _petNamerRenamed;
    private static int _petNamerSkipped;
    private static string _petNamerNote = "";
    private static object _petNamerStatusText;
    private const int PetNamerMaxUids = 5;
    private const int PetNamerMinRandomSuffix = 6;
    /// <summary>每只宠物改名之间的发包间隔。</summary>
    private const long PetNamerStepMs = 400;
    /// <summary>角色之间切换的额外间隔。</summary>
    private const long PetNamerRoleMs = 1000;

    // ----- 超级AI（模拟阶段：只采信息+模拟决策，不改出手） -----
    private static bool _superAiActive;
    private static bool _superAiVipBackupValid;
    private static int _superAiVipPlayerSwitch;
    private static int _superAiVipPetSwitch;
    private static string _superAiLastDumpKey = "";
    private static long _superAiLastDumpMs;
    private static string _superAiLastSimLine = "";
    private static object _superAiStatusText;
    private static object _superAiBattleRoot;
    private const long SuperAiDumpMinIntervalMs = 900;
    private static int _superAiUiPage; // 0=战场一览 1=单位详情
    private static int _superAiDetailIndex = -1;
    private static string _superAiUnitsKey = "";
    private static readonly List<SuperAiUnitSnap> _superAiUnits = new List<SuperAiUnitSnap>();

    private struct SuperAiUnitSnap
    {
        public int Idx;
        public bool Mine;
        public bool IsPlayer;
        public string Name;
        public int Level;
        public int Hp;
        public int MaxHp;
        public int Mp;
        public int MaxMp;
        public bool DetailOk;
        public int Rate;
        public int Atk;
        public int Def;
        public int Agi;
        public int Spirit;
        public int Rec;
        public string Extra; // drops / job
    }
    /// <summary>VIP AutoSkillType：与 BattleProcesser.TryUseVipAutoSkill 一致，便于后续决策。</summary>
    private const string SuperAiVipTypeHint =
        "VIP条件:2/3敌数 4敌蓝% 5自身血% 6/7队均血% 8加血 9恢复(无RCV_UP) 10守卫 "
        + "11场上无属性祈祷(地水火风) 12友方异常 13友方倒地 14反弹/吸收类";

    private struct EscortCandidate
    {
        public int Id;
        public string Title;
        public string Status;
    }

    private struct NavWaypoint
    {
        public string Id;
        public string Name;
        public int Floor;
        public int MapId;
        public int X;
        public int Y;
    }

    static SeqChapterTestUi()
    {
        try
        {
            EnsureLogBoot("static-ctor");
        }
        catch
        {
            // ignore
        }
    }

    public static string GetLogPath()
    {
        EnsureLogPath();
        return _logPath ?? LogFileName;
    }

    public static void WriteLog(string message)
    {
        try
        {
            EnsureLogPath();
            var line = DateTime.Now.ToString("HH:mm:ss.fff")
                       + " [pid=" + Process.GetCurrentProcess().Id + "] "
                       + (message ?? "")
                       + Environment.NewLine;
            lock (LogLock)
            {
                // 多开共享日志：允许读写共享，避免第二个客户端 Append 失败/卡死
                using (var fs = new FileStream(
                           _logPath,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    sw.Write(line);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public static bool OnWikiClick()
    {
        EnsureLogBoot("OnWikiClick");
        _wikiCalls++;
        var now = NowMs();
        WriteLog("OnWikiClick #" + _wikiCalls + " visibleBefore=" + _visible);

        try
        {
            if (_lastToggleMs > 0 && now - _lastToggleMs < DebounceMs)
            {
                WriteLog("DEBOUNCE keep visible=" + _visible);
                return _visible;
            }

            EnsureHost();
            _visible = !_visible;
            _lastToggleMs = now;

            if (_visible)
            {
                EnsurePanel();
                // 同步战斗模式（关掉默认九动等），保证面板选项与 DLL 开关一致
                ApplyBattleMode(_battleMode);
                SetPanelActive(true);
                SetMinimized(false);
                ShowTab(_tab);
                RefreshOverview(true);
            }
            else
            {
                _minimized = false;
                SetPanelActive(false);
            }

            WriteLog("toggle -> visible=" + _visible + " tab=" + _tab + " mode=" + _battleMode);
            return _visible;
        }
        catch (Exception ex)
        {
            WriteLog("OnWikiClick EX: " + RootMessage(ex));
            Tip("面板异常: " + RootMessage(ex));
            return false;
        }
    }

    public static void Tick()
    {
        _updateCalls++;

        // 窗口标题统一协调：各功能后缀（计数挂机/自动提取等）合并刷新。
        // 放 _visible 判断之前：面板隐藏时挂机也保持标题提示。
        if (NowMs() - _lastTitleRefreshMs >= TitleRefreshIntervalMs)
        {
            _lastTitleRefreshMs = NowMs();
            try
            {
                RefreshTitleFromFeature();
            }
            catch
            {
                // ignore
            }
        }

        // 护航/刷灵堂/超级AI在关面板时也要跑；自动暂停铃声同理
        try
        {
            TickEscort();
            TickEscortAlertRing();
            TickLingTang();
            TickSuperAi();
            TickPetNamer();
            TickScriptWingTest();
        }
        catch (Exception ex)
        {
            WriteLog("TickEscort/LingTang/SuperAi/WingTest EX: " + RootMessage(ex));
        }

        if (!_visible)
        {
            return;
        }

        if (_canvasGo == null || IsUnityNull(_canvasGo))
        {
            try
            {
                EnsurePanel();
                SetPanelActive(true);
                ShowTab(_tab);
            }
            catch (Exception ex)
            {
                WriteLog("Tick rebuild EX: " + RootMessage(ex));
            }

            return;
        }

        if (_tab == TabOverview && NowMs() - _lastOverviewRefreshMs >= OverviewRefreshMs)
        {
            RefreshOverview(false);
        }

        if (_tab == TabNav && NowMs() - _lastOverviewRefreshMs >= OverviewRefreshMs)
        {
            RefreshNavPos(false);
        }

        if (_tab == TabEscort && _escortStatusText != null && !IsUnityNull(_escortStatusText))
        {
            SetText(_escortStatusText, FormatEscortStatus(), 13);
        }

        if (_tab == TabScript && _lingTangStatusText != null && !IsUnityNull(_lingTangStatusText))
        {
            SetText(_lingTangStatusText, FormatLingTangStatus(), 12);
        }

        if (_tab == TabScript && _petNamerStatusText != null && !IsUnityNull(_petNamerStatusText))
        {
            SetText(_petNamerStatusText, FormatPetNamerStatus(), 12);
        }

        if (_tab == TabSuperAi && _superAiActive)
        {
            RefreshSuperAiBattlefieldUi(false);
        }
        else if (_tab == TabSuperAi && _superAiStatusText != null && !IsUnityNull(_superAiStatusText))
        {
            SetText(_superAiStatusText, FormatSuperAiStatus(), 11);
        }
    }

    public static void DrawGui()
    {
        // HybridCLR 通常不进 OnGUI
    }

    // ---------- 窗口标题统一协调 ----------

    /// <summary>
    /// 汇总各功能 DLL 后缀（计数挂机/自动提取等）刷新窗口标题。
    /// 供 DLL 在事件触发时调用（RefreshTitleFromFeature），面板 Tick 每 2s 兜底。
    /// 标题格式：{产品名} {服务器} {角色} Lv.{等级} + 空格 + 各后缀（空格分隔）。
    /// </summary>
    public static void RefreshTitleFromFeature()
    {
        try
        {
            var baseTitle = BuildGameTitle();
            if (string.IsNullOrEmpty(baseTitle))
            {
                return;
            }

            var suffix = CollectTitleSuffix();
            var full = string.IsNullOrEmpty(suffix) ? baseTitle : baseTitle + " " + suffix;
            if (full == _lastTitle)
            {
                return;
            }

            _lastTitle = full;
            SetGameWindowTitle(full);
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildGameTitle()
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

    private static string CollectTitleSuffix()
    {
        var parts = new System.Collections.Generic.List<string>();
        // 只保留计数挂机标题（挂机）；采集/抓宠等不再影响窗口标题
        AppendFeatureSuffix("SeqChapterCountFarm", parts);
        AppendFeatureSuffix("SeqChapterBearSlayer", parts);
        return string.Join(" ", parts);
    }

    private static void AppendFeatureSuffix(string typeName, System.Collections.Generic.List<string> parts)
    {
        try
        {
            var t = FindLoadedType(typeName);
            if (t == null)
            {
                return;
            }

            var m = t.GetMethod(
                "BuildTitleSuffix",
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (m == null)
            {
                return;
            }

            var s = Convert.ToString(m.Invoke(null, null) ?? "") ?? "";
            if (!string.IsNullOrEmpty(s))
            {
                parts.Add(s);
            }
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

    private static void SetGameWindowTitle(string title)
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

    // ---------- tabs / modes ----------

    private static void ShowTab(int tab)
    {
        _tab = tab;
        WriteLog("ShowTab " + tab);
        ClearBody();
        // 非 AI 页恢复默认面板尺寸
        if (tab != TabSuperAi)
        {
            SetShellSize(620f, 620f);
        }

        try
        {
            if (tab == TabOverview)
            {
                BuildOverviewBody();
                RefreshOverview(true);
            }
            else if (tab == TabBattle)
            {
                BuildBattleBody();
            }
            else if (tab == TabSuperAi)
            {
                BuildSuperAiBody();
            }
            else if (tab == TabScript)
            {
                BuildScriptBody();
            }
            else if (tab == TabEscort)
            {
                BuildEscortBody();
            }
            else if (tab == TabOpenUi)
            {
                BuildOpenUiBody();
            }
            else if (tab == TabNav)
            {
                BuildNavBody();
                RefreshNavPos(true);
            }
            else if (tab == TabAppear)
            {
                BuildAppearBody();
            }
        }
        catch (Exception ex)
        {
            WriteLog("ShowTab build EX tab=" + tab + ": " + RootMessage(ex));
            try
            {
                var rtType = RequireType("UnityEngine.RectTransform");
                var err = CreateUiChild(_bodyRoot, "BuildErr", rtType);
                StretchFull(RequireRect(err, "be"));
                SetText(AddText(err), "页面构建失败，见日志\n" + RootMessage(ex), 13);
            }
            catch
            {
                // ignore
            }
        }

        RefreshTabButtonLabels();
    }

    private static void SelectBattleMode(string mode)
    {
        try
        {
            WriteLog("SelectBattleMode " + mode);
            ApplyBattleMode(mode);
            _battleMode = mode;
            if (!IsSuperAiModeAllowed(mode) && _superAiActive)
            {
                StopSuperAi("战斗模式非常规，已关闭超级AI");
            }

            _statusLine = "战斗模式: " + ModeLabel(mode);
            Tip(_statusLine);
            if (_tab == TabBattle)
            {
                ClearBody();
                BuildBattleBody();
                RefreshTabButtonLabels();
            }
        }
        catch (Exception ex)
        {
            WriteLog("SelectBattleMode EX: " + RootMessage(ex));
            Tip("切换模式失败: " + RootMessage(ex));
        }
    }

    private static bool IsSuperAiModeAllowed(string mode)
    {
        return mode == ModeNormal || mode == ModeNopet2Act;
    }

    private static void ApplyBattleMode(string mode)
    {
        // 全关再开选中项（互斥）
        TrySetFeatureEnabled("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterAutoCatchSell", "hotfixdata/SeqChapterAutoCatchSell.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterAutoCatchNoPet", "hotfixdata/SeqChapterAutoCatchNoPet.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterAutoSeal", "hotfixdata/SeqChapterAutoSeal.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterLv1Auto", "hotfixdata/SeqChapterLv1Auto.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterNineAction", "hotfixdata/SeqChapterNineAction.dll.bytes", false);
        TrySetFeatureEnabled("SeqChapterCountFarm", "hotfixdata/SeqChapterCountFarm.dll.bytes", false);

        if (mode == ModeCatch)
        {
            TrySetFeatureEnabled("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes", true);
        }
        else if (mode == ModeCatchSell)
        {
            TrySetFeatureEnabled("SeqChapterAutoCatchSell", "hotfixdata/SeqChapterAutoCatchSell.dll.bytes", true);
        }
        else if (mode == ModeCatchNopet)
        {
            TrySetFeatureEnabled("SeqChapterAutoCatchNoPet", "hotfixdata/SeqChapterAutoCatchNoPet.dll.bytes", true);
        }
        else if (mode == ModeSeal)
        {
            TrySetFeatureEnabled("SeqChapterAutoSeal", "hotfixdata/SeqChapterAutoSeal.dll.bytes", true);
        }
        else if (mode == ModeLv1)
        {
            TrySetFeatureEnabled("SeqChapterLv1Auto", "hotfixdata/SeqChapterLv1Auto.dll.bytes", true);
        }
        else if (mode == ModeCountFarm)
        {
            TrySetFeatureEnabled("SeqChapterCountFarm", "hotfixdata/SeqChapterCountFarm.dll.bytes", true);
        }
        // normal / nopet_2act: 全部 DLL 关；无宠二动靠 Magics PE
    }

    private static bool FeatureAvailable(string typeName, string assetPath)
    {
        if (FindLoadedType(typeName) != null)
        {
            return true;
        }

        return CanLoadBytes(assetPath);
    }

    private static void TrySetFeatureEnabled(string typeName, string assetPath, bool enable)
    {
        var t = EnsureFeatureType(typeName, assetPath);
        if (t == null)
        {
            if (enable)
            {
                WriteLog("feature missing " + typeName);
            }

            return;
        }

        try
        {
            var set = t.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
            if (set != null)
            {
                set.Invoke(null, new object[] { enable });
                WriteLog("SetEnabled " + typeName + "=" + enable);
                return;
            }

            // 兼容：仅有开关字段
            var f = t.GetField("PipelineEnabled", BindingFlags.Public | BindingFlags.Static)
                    ?? t.GetField("ModeEnabled", BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.FieldType == typeof(bool))
            {
                f.SetValue(null, enable);
                WriteLog("field " + typeName + "=" + enable);
            }
        }
        catch (Exception ex)
        {
            WriteLog("TrySetFeatureEnabled EX " + typeName + ": " + RootMessage(ex));
        }
    }

    private static Type EnsureFeatureType(string typeName, string assetPath)
    {
        var t = FindLoadedType(typeName);
        if (t != null)
        {
            return t;
        }

        try
        {
            var bytes = LoadBytes(assetPath);
            if (bytes == null || bytes.Length == 0)
            {
                WriteLog("EnsureFeatureType no-bytes " + typeName + " path=" + assetPath);
                return null;
            }

            WriteLog("EnsureFeatureType load " + typeName + " bytes=" + bytes.Length);
            var asm = Assembly.Load(bytes);
            t = asm != null ? FindTypeInAsm(asm, typeName) : null;
            if (t == null && asm != null)
            {
                // HybridCLR 偶发 GetType 失败时扫一遍
                try
                {
                    foreach (var x in asm.GetTypes())
                    {
                        if (x != null && x.Name == typeName)
                        {
                            t = x;
                            break;
                        }
                    }
                }
                catch (Exception scanEx)
                {
                    WriteLog("EnsureFeatureType GetTypes EX: " + RootMessage(scanEx));
                }
            }

            if (t != null)
            {
                var boot = t.GetMethod("Bootstrap", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                boot?.Invoke(null, null);
                WriteLog("EnsureFeatureType ok " + typeName);
            }
            else
            {
                WriteLog("EnsureFeatureType type-missing " + typeName + " asm=" + (asm != null ? asm.FullName : "null"));
            }

            return t;
        }
        catch (Exception ex)
        {
            WriteLog("EnsureFeatureType EX " + typeName + ": " + RootMessage(ex));
            return null;
        }
    }

    private static void RunDailyClaim()
    {
        InvokeDailyClaimToggle("ToggleDailyFromUi", "日常");
    }

    private static void RunGiftClaim()
    {
        InvokeDailyClaimToggle("ToggleGiftFromUi", "礼包码");
    }

    private static void RunAreaExtractNow()
    {
        try
        {
            WriteLog("RunAreaExtractNow");
            var t = EnsureFeatureType("SeqChapterAreaExtract", "hotfixdata/SeqChapterAreaExtract.dll.bytes");
            if (t == null)
            {
                Tip("采集自动提取 DLL 加载失败（见日志）");
                return;
            }

            var m = t.GetMethod("ExtractNowFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null)
            {
                Tip("立刻提取入口缺失（请更新 AreaExtract DLL）");
                return;
            }

            m.Invoke(null, null);
        }
        catch (Exception ex)
        {
            WriteLog("RunAreaExtractNow EX: " + RootMessage(ex));
            Tip("立刻提取失败: " + RootMessage(ex));
        }
    }

    private static void RunAutoPoint()
    {
        try
        {
            WriteLog("RunAutoPoint");
            var t = EnsureFeatureType("SeqChapterAutoPoint", "hotfixdata/SeqChapterAutoPoint.dll.bytes");
            if (t == null)
            {
                Tip("一键加点 DLL 加载失败（见日志）");
                return;
            }

            var m = t.GetMethod("RunAllFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null)
            {
                Tip("一键加点入口缺失（请更新 AutoPoint DLL）");
                return;
            }

            m.Invoke(null, null);
        }
        catch (Exception ex)
        {
            WriteLog("RunAutoPoint EX: " + RootMessage(ex));
            Tip("一键加点失败: " + RootMessage(ex));
        }
    }

    private static void RunAutoStall()
    {
        try
        {
            WriteLog("RunAutoStall");
            var t = EnsureFeatureType("SeqChapterAutoStall", "hotfixdata/SeqChapterAutoStall.dll.bytes");
            if (t == null)
            {
                Tip("自动上架 DLL 加载失败（见日志）");
                return;
            }

            if (Convert.ToBoolean(t.GetMethod("IsRunning", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null) ?? false))
            {
                var stop = t.GetMethod("StopFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                stop?.Invoke(null, null);
                Tip("自动上架已停止");
                return;
            }

            var run = t.GetMethod("RunAutoStallFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (run == null)
            {
                Tip("自动上架入口缺失（请更新 AutoStall DLL）");
                return;
            }

            var r = Convert.ToBoolean(run.Invoke(null, null) ?? false);
            Tip(r ? "自动上架已启动" : "自动上架未能启动（见飘字）");
        }
        catch (Exception ex)
        {
            WriteLog("RunAutoStall EX: " + RootMessage(ex));
            Tip("自动上架失败: " + RootMessage(ex));
        }
    }

    private static void RunBearSlayer()
    {
        try
        {
            WriteLog("RunBearSlayer");
            var t = EnsureFeatureType("SeqChapterBearSlayer", "hotfixdata/SeqChapterBearSlayer.dll.bytes");
            if (t == null)
            {
                Tip("刷熊男 DLL 加载失败（见日志）");
                return;
            }

            if (Convert.ToBoolean(t.GetMethod("IsRunning", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null) ?? false))
            {
                var stop = t.GetMethod("StopFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                stop?.Invoke(null, null);
                Tip("刷熊男已停止");
                return;
            }

            var run = t.GetMethod("RunBearSlayerFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (run == null)
            {
                Tip("刷熊男入口缺失（请更新 BearSlayer DLL）");
                return;
            }

            var r = Convert.ToBoolean(run.Invoke(null, null) ?? false);
            Tip(r ? "刷熊男已启动" : "刷熊男未能启动（见飘字）");
        }
        catch (Exception ex)
        {
            WriteLog("RunBearSlayer EX: " + RootMessage(ex));
            Tip("刷熊男失败: " + RootMessage(ex));
        }
    }

    private static void RunPetNamer()
    {
        try
        {
            WriteLog("RunPetNamer");
            if (_petNamerActive)
            {
                StopPetNamer("已手动停止");
                return;
            }

            var uids = CollectTeamOrMultiUids();
            if (uids.Count == 0)
            {
                var cap = GetCaptainUid();
                if (!string.IsNullOrEmpty(cap))
                {
                    uids.Add(cap);
                }
            }

            if (uids.Count > PetNamerMaxUids)
            {
                uids = uids.GetRange(0, PetNamerMaxUids);
            }

            if (uids.Count == 0)
            {
                Tip("一键命名：未找到角色");
                return;
            }

            _petNamerActive = true;
            _petNamerUids = uids;
            _petNamerRoleIdx = 0;
            _petNamerPetIdx = 0;
            _petNamerRenamed = 0;
            _petNamerSkipped = 0;
            _petNamerNote = "准备中…";
            _petNamerNextAtMs = NowMs();
            Tip(string.Format("一键命名：开始 {0} 个角色", uids.Count));
            WriteLog("PetNamer start uids=" + string.Join(",", uids.ToArray()));
        }
        catch (Exception ex)
        {
            WriteLog("RunPetNamer EX: " + RootMessage(ex));
            Tip("一键命名失败: " + RootMessage(ex));
        }
    }

    private static void StopPetNamer(string reason)
    {
        _petNamerActive = false;
        _petNamerUids = null;
        _petNamerNote = reason;
        Tip("一键命名：" + reason);
    }

    private static void TickPetNamer()
    {
        if (!_petNamerActive)
        {
            return;
        }

        var now = NowMs();
        if (now < _petNamerNextAtMs)
        {
            return;
        }

        try
        {
            if (_petNamerUids == null || _petNamerRoleIdx >= _petNamerUids.Count)
            {
                StopPetNamer(string.Format("完成：改名 {0}，跳过 {1}", _petNamerRenamed, _petNamerSkipped));
                WriteLog("PetNamer done renamed=" + _petNamerRenamed + " skipped=" + _petNamerSkipped);
                return;
            }

            if (TryRenameNextLevelOnePet())
            {
                _petNamerNextAtMs = now + PetNamerStepMs;
            }
            else
            {
                // 当前角色扫完 → 切换下一角色
                _petNamerRoleIdx++;
                _petNamerPetIdx = 0;
                _petNamerNextAtMs = now + PetNamerRoleMs;
                if (_petNamerRoleIdx < _petNamerUids.Count)
                {
                    _petNamerNote = string.Format("角色 {0}/{1} 处理完成，切换下一角色",
                        _petNamerRoleIdx, _petNamerUids.Count);
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("TickPetNamer EX: " + RootMessage(ex));
            _petNamerNextAtMs = now + PetNamerStepMs;
        }
    }

    /// <summary>
    /// 处理当前角色「下一只」1 级背包宠：改名/跳过均只处理一只，返回 true 表示需延迟；
    /// 当前角色已扫完返回 false（由调用方切换角色）。
    /// </summary>
    private static bool TryRenameNextLevelOnePet()
    {
        if (_petNamerUids == null || _petNamerRoleIdx >= _petNamerUids.Count)
        {
            return false;
        }

        var uid = _petNamerUids[_petNamerRoleIdx];
        var pets = GetPetListByUid(uid);
        if (pets == null || pets.Count == 0)
        {
            return false;
        }

        var petMgr = GetManagerInstance("PetManager");
        if (petMgr == null)
        {
            return false;
        }

        var sendChange = FindSendChangePetNameNamer(petMgr);
        if (sendChange == null)
        {
            return false;
        }

        var getFileValue = FindGetPetFileValueNamer(petMgr);

        while (_petNamerPetIdx < pets.Count)
        {
            var i = _petNamerPetIdx;
            _petNamerPetIdx = i + 1;

            var pet = pets[i];
            if (pet == null)
            {
                continue;
            }

            if (Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
            {
                continue;
            }

            var data = GetMember(pet, "data");
            if (data == null)
            {
                continue;
            }

            if (ReadIntMemberNamer(data, "Level") != StorePetLevel)
            {
                continue;
            }

            // 命中一只 1 级背包宠：算档改名或跳过，返回 true 触发延迟
            if (!TryGetMaxResetBaseRandomNamer(data, out var maxRand))
            {
                // ResetBaseInfo 未到：跳过（下次再点）
                _petNamerSkipped++;
                return true;
            }

            var perfect = IsPerfectPetNamer(pet, data);
            var grade = 0;
            if (!perfect)
            {
                grade = GetPetGradeValueNamer(petMgr, getFileValue, data);
                if (grade < 0)
                {
                    grade = 0;
                }
            }

            var newName = FormatPetMarkNameNamer(perfect, grade, maxRand);
            var display = GetDisplayPetNameNamer(data);
            if (!NeedsPetRenameMarkNamer(display, newName, maxRand))
            {
                _petNamerSkipped++;
                return true;
            }

            var index = Convert.ToInt32(GetMember(data, "Index") ?? i);
            sendChange.Invoke(petMgr, new object[] { uid, index, newName });
            _petNamerRenamed++;
            _petNamerNote = string.Format("角色 {0}/{1}：改名 #{2} → {3}",
                _petNamerRoleIdx + 1, _petNamerUids.Count, index, newName);
            WriteLog("PetNamer rename uid=" + uid + " index=" + index + " name=" + newName);
            return true;
        }

        return false;
    }

    private static string FormatPetNamerStatus()
    {
        if (!_petNamerActive)
        {
            return "一键命名: 未启动\n点按钮为 5 个角色背包里的 1 级宠物按捉宠规则改名（#档/#满/@随机）。";
        }

        var total = _petNamerUids != null ? _petNamerUids.Count : 0;
        var cur = _petNamerRoleIdx + 1;
        if (cur > total)
        {
            cur = total;
        }

        return "一键命名: 运行中\n角色 " + cur + "/" + total
               + " · 已改名 " + _petNamerRenamed
               + " · 跳过 " + _petNamerSkipped
               + "\n" + _petNamerNote;
    }

    private static bool IsPerfectPetNamer(object pet, object data)
    {
        try
        {
            var flag = GetMember(pet, "isPrefectPet");
            if (flag is bool b)
            {
                return b;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            return Convert.ToInt32(GetMember(data, "Nowvitalbase") ?? 0)
                   >= Convert.ToInt32(GetMember(data, "Maxvitalbase") ?? 0)
                   && Convert.ToInt32(GetMember(data, "Nowstrbase") ?? 0)
                   >= Convert.ToInt32(GetMember(data, "Maxstrbase") ?? 0)
                   && Convert.ToInt32(GetMember(data, "Nowtghbase") ?? 0)
                   >= Convert.ToInt32(GetMember(data, "Maxtghbase") ?? 0)
                   && Convert.ToInt32(GetMember(data, "Nowquickbase") ?? 0)
                   >= Convert.ToInt32(GetMember(data, "Maxquickbase") ?? 0)
                   && Convert.ToInt32(GetMember(data, "Nowmagicbase") ?? 0)
                   >= Convert.ToInt32(GetMember(data, "Maxmagicbase") ?? 0);
        }
        catch
        {
            return false;
        }
    }

    private static int GetPetGradeValueNamer(object petMgr, MethodInfo getFileValue, object data)
    {
        if (petMgr != null && getFileValue != null)
        {
            try
            {
                return Convert.ToInt32(getFileValue.Invoke(petMgr, new object[] { data }) ?? 0);
            }
            catch
            {
                // fall through
            }
        }

        try
        {
            var maxSum = Convert.ToInt32(GetMember(data, "Maxvitalbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Maxstrbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Maxtghbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Maxquickbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Maxmagicbase") ?? 0);
            var nowSum = Convert.ToInt32(GetMember(data, "Nowvitalbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Nowstrbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Nowtghbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Nowquickbase") ?? 0)
                         + Convert.ToInt32(GetMember(data, "Nowmagicbase") ?? 0);
            return maxSum - nowSum;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryGetMaxResetBaseRandomNamer(object data, out int maxRand)
    {
        maxRand = 0;
        var reset = GetMember(data, "ResetBaseInfo");
        if (reset == null)
        {
            return false;
        }

        foreach (var name in new[]
                 {
                     "Vitalbase", "Strbase", "Tghbase", "Quickbase", "Magicbase",
                     "vitalbase_", "strbase_", "tghbase_", "quickbase_", "magicbase_"
                 })
        {
            var v = ReadIntMemberNamer(reset, name);
            if (v > maxRand)
            {
                maxRand = v;
            }
        }

        return true;
    }

    private static string FormatPetMarkNameNamer(bool perfect, int grade, int maxRand)
    {
        var head = perfect ? "#满" : "#" + grade;
        if (maxRand >= PetNamerMinRandomSuffix)
        {
            return head + "@" + maxRand;
        }

        return head;
    }

    private static bool NeedsPetRenameMarkNamer(string display, string newName, int maxRand)
    {
        if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(newName))
        {
            return false;
        }

        if (string.Equals(display, newName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!display.StartsWith("#", StringComparison.Ordinal))
        {
            return true;
        }

        var at = display.LastIndexOf('@');
        if (at > 0 && at < display.Length - 1)
        {
            if (int.TryParse(display.Substring(at + 1), out var tagged)
                && tagged < PetNamerMinRandomSuffix)
            {
                return true;
            }
        }
        else if (maxRand >= PetNamerMinRandomSuffix)
        {
            return true;
        }

        return false;
    }

    private static string GetDisplayPetNameNamer(object data)
    {
        var free = Convert.ToString(GetMember(data, "FreeName") ?? "") ?? "";
        if (!string.IsNullOrEmpty(free))
        {
            return free;
        }

        return Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
    }

    private static MethodInfo FindSendChangePetNameNamer(object petMgr)
    {
        if (petMgr == null)
        {
            return null;
        }

        foreach (var m in petMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendChangePetName")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 3
                && ps[0].ParameterType == typeof(string)
                && (ps[1].ParameterType == typeof(int) || ps[1].ParameterType == typeof(short))
                && ps[2].ParameterType == typeof(string))
            {
                return m;
            }
        }

        return null;
    }

    private static MethodInfo FindGetPetFileValueNamer(object petMgr)
    {
        if (petMgr == null)
        {
            return null;
        }

        foreach (var m in petMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "GetPetFileValue")
            {
                continue;
            }

            if (m.GetParameters().Length == 1)
            {
                return m;
            }
        }

        return null;
    }

    private static int ReadIntMemberNamer(object obj, string name)
    {
        try
        {
            var v = GetMember(obj, name);
            if (v == null || v is bool)
            {
                return 0;
            }

            return Convert.ToInt32(v);
        }
        catch
        {
            return 0;
        }
    }

    private static void InvokeDailyClaimToggle(string methodName, string label)
    {
        try
        {
            WriteLog(methodName);
            var t = EnsureFeatureType("SeqChapterDailyClaim", "hotfixdata/SeqChapterDailyClaim.dll.bytes");
            if (t == null)
            {
                Tip(label + " DLL 加载失败（见日志）");
                return;
            }

            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null)
            {
                Tip(label + "入口缺失（请更新 DailyClaim DLL）");
                return;
            }

            var r = m.Invoke(null, null);
            Tip(r is bool b && b ? label + "已开始" : label + "已停止/未开始");
        }
        catch (Exception ex)
        {
            WriteLog(methodName + " EX: " + RootMessage(ex));
            Tip(label + "失败: " + RootMessage(ex));
        }
    }

    // ---------- overview data ----------

    private static void RefreshOverview(bool force)
    {
        _lastOverviewRefreshMs = NowMs();
        if (_overviewText == null || IsUnityNull(_overviewText))
        {
            if (!force)
            {
                return;
            }
        }

        var text = BuildOverviewText();
        if (_overviewText != null && !IsUnityNull(_overviewText))
        {
            SetText(_overviewText, text, 15);
        }
    }

    private static string BuildOverviewText()
    {
        var sb = new StringBuilder();
        try
        {
            var captainUid = GetCaptainUid();
            var player = GetPlayer(captainUid);
            // 小地图/任务用的是 MapManager.currentFloor，不是 PlayerData.mapId（协议字段常滞后或含义不同）
            int floor;
            string floorName;
            int mapResId;
            TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
            var loc = GetStaticMember("PlayerDataHolder", "location");
            var x = Convert.ToInt32(GetMember(loc, "x") ?? GetMember(loc, "X") ?? 0);
            var y = Convert.ToInt32(GetMember(loc, "y") ?? GetMember(loc, "Y") ?? 0);
            var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
            var name = Convert.ToString(GetMember(player, "Name") ?? GetMember(player, "name") ?? "") ?? "";

            sb.AppendLine("【队长】");
            sb.Append("名称: ").AppendLine(string.IsNullOrEmpty(name) ? "(无)" : name);
            sb.Append("地图号: ").Append(floor);
            if (!string.IsNullOrEmpty(floorName))
            {
                sb.Append("（").Append(floorName).Append('）');
            }

            sb.Append("  坐标: ").Append(x).Append(',').Append(y).AppendLine();
            sb.Append("战斗中: ").AppendLine(inBattle ? "是" : "否");
            sb.AppendLine();
            sb.AppendLine("【队伍血魔池】");
            AppendTeamPools(sb);
            sb.AppendLine();
            sb.AppendLine("【队员每日魔石】");
            AppendTeamMoshi(sb);
            if (!string.IsNullOrEmpty(_statusLine))
            {
                sb.AppendLine();
                sb.Append("状态: ").Append(_statusLine);
            }
        }
        catch (Exception ex)
        {
            sb.Append("概况读取失败: ").Append(RootMessage(ex));
            WriteLog("BuildOverview EX: " + RootMessage(ex));
        }

        return sb.ToString();
    }

    /// <summary>读取当前场景地图：floor=地图号（与小地图一致），mapResId=资源 mapid。</summary>
    private static bool TryGetCurrentMapInfo(out int floor, out string floorName, out int mapResId)
    {
        floor = 0;
        floorName = "";
        mapResId = 0;
        try
        {
            var mm = GetMapManagerInstance();
            if (mm == null)
            {
                return false;
            }

            floor = Convert.ToInt32(GetProp(mm, "currentFloor") ?? GetMember(mm, "currentFloor") ?? 0);
            floorName = Convert.ToString(GetProp(mm, "currentFloorName") ?? GetMember(mm, "currentFloorName") ?? "") ?? "";
            mapResId = Convert.ToInt32(GetProp(mm, "currentMapID") ?? GetMember(mm, "currentMapID") ?? 0);
            return floor != 0 || !string.IsNullOrEmpty(floorName);
        }
        catch (Exception ex)
        {
            WriteLog("TryGetCurrentMapInfo EX: " + RootMessage(ex));
            return false;
        }
    }

    private static object GetMapManagerInstance()
    {
        try
        {
            var mmType = FindType("MapManager");
            if (mmType == null)
            {
                return null;
            }

            for (var cur = mmType; cur != null; cur = cur.BaseType)
            {
                var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
                            | BindingFlags.FlattenHierarchy;
                foreach (var propName in new[] { "instance", "Instance" })
                {
                    try
                    {
                        var p = cur.GetProperty(propName, flags);
                        var inst = p?.GetValue(null, null);
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

                foreach (var fieldName in new[] { "instance", "Instance" })
                {
                    try
                    {
                        var f = cur.GetField(fieldName, flags);
                        var inst = f?.GetValue(null);
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

            return GetStaticMember("MapManager", "instance") ?? GetStaticMember("MapManager", "Instance");
        }
        catch
        {
            return null;
        }
    }

    // ---------- 导航 ----------

    private static void RebuildNavTab()
    {
        if (_tab != TabNav || _bodyRoot == null || IsUnityNull(_bodyRoot))
        {
            return;
        }

        ClearBody();
        BuildNavBody();
        RefreshNavPos(true);
        RefreshTabButtonLabels();
    }

    private static void RefreshNavPos(bool force)
    {
        _lastOverviewRefreshMs = NowMs();
        if (_navPosText == null || IsUnityNull(_navPosText))
        {
            if (!force)
            {
                return;
            }
        }

        if (_navPosText != null && !IsUnityNull(_navPosText))
        {
            SetText(_navPosText, FormatNavPosLine(), 13);
        }
    }

    private static string FormatNavPosLine()
    {
        try
        {
            int floor;
            string floorName;
            int mapResId;
            TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
            TryGetPlayerXY(out var x, out var y);
            var sb = new StringBuilder();
            sb.Append("当前位置  地图号: ").Append(floor);
            if (!string.IsNullOrEmpty(floorName))
            {
                sb.Append("（").Append(floorName).Append('）');
            }

            sb.Append("  坐标: ").Append(x).Append(',').Append(y);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "位置读取失败: " + RootMessage(ex);
        }
    }

    private static void CaptureNavInputsFromUi()
    {
        _navFloorStr = ReadNavField(_navFloorInput, _navFloorStr);
        _navXStr = ReadNavField(_navXInput, _navXStr);
        _navYStr = ReadNavField(_navYInput, _navYStr);
        _navNameStr = ReadNavField(_navNameInput, _navNameStr);
    }

    private static string ReadNavField(object input, string fallback)
    {
        if (input == null || IsUnityNull(input))
        {
            return fallback ?? "";
        }

        try
        {
            var t = GetProp(input, "text") ?? GetMember(input, "text");
            return Convert.ToString(t ?? "") ?? fallback ?? "";
        }
        catch
        {
            return fallback ?? "";
        }
    }

    private static void SetNavStatus(string msg)
    {
        _navStatusLine = msg ?? "";
        if (_navStatusText != null && !IsUnityNull(_navStatusText))
        {
            SetText(_navStatusText, _navStatusLine, 12);
        }
    }

    private static void NavFillCurrent()
    {
        try
        {
            int floor;
            string floorName;
            int mapResId;
            TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
            TryGetPlayerXY(out var x, out var y);
            _navFloorStr = floor.ToString();
            _navXStr = x.ToString();
            _navYStr = y.ToString();
            if (string.IsNullOrEmpty(_navNameStr))
            {
                _navNameStr = string.IsNullOrEmpty(floorName) ? ("点" + floor) : floorName;
            }

            RebuildNavTab();
            SetNavStatus("已填入 地图" + floor + " (" + x + "," + y + ")");
            Tip("导航：已填入当前位置");
        }
        catch (Exception ex)
        {
            WriteLog("NavFillCurrent EX: " + RootMessage(ex));
            Tip("填入失败");
        }
    }

    private static void NavGoFromInputs()
    {
        CaptureNavInputsFromUi();
        int floor, x, y;
        if (!TryParseInt(_navFloorStr, out floor) || floor <= 0)
        {
            Tip("请输入有效地图号");
            return;
        }

        if (!TryParseInt(_navXStr, out x) || !TryParseInt(_navYStr, out y))
        {
            Tip("请输入有效坐标 X/Y");
            return;
        }

        NavGoTo(floor, x, y, null);
    }

    private static void NavGoTo(int floor, int x, int y, string name)
    {
        string how;
        if (!TryNavigateTo(floor, x, y, out how))
        {
            SetNavStatus("导航失败: " + how);
            Tip("导航失败: " + how);
            WriteLog("NavGoTo fail floor=" + floor + " xy=" + x + "," + y + " " + how);
            return;
        }

        var label = string.IsNullOrEmpty(name) ? "" : (name + " ");
        SetNavStatus("导航中 " + label + floor + " (" + x + "," + y + ") via " + how);
        Tip("导航 → " + label + floor + " (" + x + "," + y + ")");
        WriteLog("NavGoTo ok floor=" + floor + " xy=" + x + "," + y + " how=" + how);
    }

    private static void NavStop()
    {
        try
        {
            StopTaskNavigation();
            var tm = GetManagerInstance("TaskManager");
            if (tm != null)
            {
                var cancel = tm.GetType().GetMethod(
                    "CancelTaskPathfinding",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                cancel?.Invoke(tm, null);
            }

            SetNavStatus("已停止导航");
            Tip("导航：已停止");
            WriteLog("NavStop ok");
        }
        catch (Exception ex)
        {
            WriteLog("NavStop EX: " + RootMessage(ex));
            Tip("停止失败");
        }
    }

    private static void NavSaveCurrentWaypoint()
    {
        try
        {
            CaptureNavInputsFromUi();
            int floor;
            string floorName;
            int mapResId;
            TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
            TryGetPlayerXY(out var x, out var y);
            if (floor <= 0)
            {
                Tip("无法获取当前地图号");
                return;
            }

            var name = (_navNameStr ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(floorName)
                    ? ("点(" + floor + "," + x + "," + y + ")")
                    : floorName;
            }

            var wp = new NavWaypoint
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Name = name,
                Floor = floor,
                MapId = 0,
                X = x,
                Y = y
            };
            _navWaypoints.Add(wp);
            SaveNavWaypointsToDisk();
            _navFloorStr = floor.ToString();
            _navXStr = x.ToString();
            _navYStr = y.ToString();
            _navWpPage = Math.Max(0, (_navWaypoints.Count - 1) / NavWaypointPageSize);
            RebuildNavTab();
            SetNavStatus("已记录 " + name + " " + floor + " (" + x + "," + y + ")");
            Tip("已记录点位: " + name);
            WriteLog("NavSave wp=" + name + " floor=" + floor + " xy=" + x + "," + y);
        }
        catch (Exception ex)
        {
            WriteLog("NavSaveCurrentWaypoint EX: " + RootMessage(ex));
            Tip("记录失败");
        }
    }

    /// <summary>跨图导航：优先 GeneralPointMoveTo（与序章助手「导航」相同）。</summary>
    private static bool TryNavigateTo(int floor, int x, int y, out string how)
    {
        how = "";
        if (floor <= 0)
        {
            how = "地图号无效";
            return false;
        }

        object mapPoint;
        if (!TryMakeNavMapPoint(floor, x, y, out mapPoint))
        {
            how = "MapPoint创建失败";
            return false;
        }

        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var entity = GetProp(pm, "playerEntity") ?? GetMember(pm, "playerEntity");
            if (entity != null && InvokeNavWithMapPoint(entity, "GeneralPointMoveTo", mapPoint))
            {
                how = "GeneralPointMoveTo";
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryNavigateTo General EX: " + RootMessage(ex));
        }

        try
        {
            var msType = FindType("MissionSystem");
            if (msType != null)
            {
                foreach (var m in msType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (m.Name != "TaskMoveTo")
                    {
                        continue;
                    }

                    var ps = m.GetParameters();
                    if (ps.Length < 1 || ps[0].ParameterType.Name != "MapPoint")
                    {
                        continue;
                    }

                    var args = ps.Length == 1 ? new[] { mapPoint } : new object[] { mapPoint, null };
                    m.Invoke(null, args);
                    how = "TaskMoveTo";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryNavigateTo TaskMoveTo EX: " + RootMessage(ex));
        }

        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var walk = GetProp(pm, "walkSystem") ?? GetMember(pm, "walkSystem");
            if (walk != null && InvokeNavWithMapPoint(walk, "MoveTo", mapPoint))
            {
                how = "WalkSystem.MoveTo";
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryNavigateTo Walk EX: " + RootMessage(ex));
        }

        how = "无可用导航接口";
        return false;
    }

    private static bool TryMakeNavMapPoint(int mapIndex, int x, int y, out object mapPoint)
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
                    args[i] = ps[i].ParameterType.IsClass
                        ? null
                        : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                }

                mapPoint = ctor.Invoke(args);
                return mapPoint != null;
            }
            catch
            {
                // next ctor
            }
        }

        return false;
    }

    private static bool InvokeNavWithMapPoint(object target, string methodName, object mapPoint)
    {
        if (target == null || mapPoint == null)
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
                var args = new object[ps.Length];
                args[0] = mapPoint;
                for (var i = 1; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(bool))
                    {
                        args[i] = false;
                    }
                    else if (ps[i].ParameterType.IsValueType && !ps[i].ParameterType.IsEnum)
                    {
                        args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                m.Invoke(target, args);
                return true;
            }
            catch
            {
                // next
            }
        }

        return false;
    }

    private static bool TryParseInt(string s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        s = s.Trim();
        return int.TryParse(s, out value);
    }

    private static string GetNavWaypointsPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".seqchapter_helper", "waypoints.json");
        }
        catch
        {
            return Path.Combine(Environment.CurrentDirectory, "waypoints.json");
        }
    }

    private static void LoadNavWaypointsFromDisk()
    {
        _navWaypoints.Clear();
        try
        {
            var path = GetNavWaypointsPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            ParseNavWaypointsJson(json);
            WriteLog("NavWaypoints loaded n=" + _navWaypoints.Count + " path=" + path);
        }
        catch (Exception ex)
        {
            WriteLog("LoadNavWaypointsFromDisk EX: " + RootMessage(ex));
        }
    }

    private static void SaveNavWaypointsToDisk()
    {
        try
        {
            var path = GetNavWaypointsPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.Append("{\n  \"items\": [\n");
            for (var i = 0; i < _navWaypoints.Count; i++)
            {
                var w = _navWaypoints[i];
                if (i > 0)
                {
                    sb.Append(",\n");
                }

                sb.Append("    {\n");
                sb.Append("      \"id\": \"").Append(JsonEscape(w.Id)).Append("\",\n");
                sb.Append("      \"name\": \"").Append(JsonEscape(w.Name)).Append("\",\n");
                sb.Append("      \"floor\": ").Append(w.Floor).Append(",\n");
                sb.Append("      \"map_id\": ").Append(w.MapId).Append(",\n");
                sb.Append("      \"x\": ").Append(w.X).Append(",\n");
                sb.Append("      \"y\": ").Append(w.Y).Append("\n");
                sb.Append("    }");
            }

            sb.Append("\n  ]\n}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            WriteLog("NavWaypoints saved n=" + _navWaypoints.Count + " path=" + path);
        }
        catch (Exception ex)
        {
            WriteLog("SaveNavWaypointsToDisk EX: " + RootMessage(ex));
            Tip("保存点位失败");
        }
    }

    private static bool DeleteNavWaypoint(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        for (var i = 0; i < _navWaypoints.Count; i++)
        {
            if (_navWaypoints[i].Id == id)
            {
                _navWaypoints.RemoveAt(i);
                SaveNavWaypointsToDisk();
                return true;
            }
        }

        return false;
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

    /// <summary>极简解析 waypoints.json（与序章助手格式兼容）。</summary>
    private static void ParseNavWaypointsJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        var searchFrom = 0;
        var itemsAt = json.IndexOf("\"items\"", StringComparison.Ordinal);
        if (itemsAt >= 0)
        {
            searchFrom = itemsAt;
        }

        while (true)
        {
            var floorAt = json.IndexOf("\"floor\"", searchFrom, StringComparison.Ordinal);
            if (floorAt < 0)
            {
                break;
            }

            var objStart = json.LastIndexOf('{', floorAt);
            var objEnd = json.IndexOf('}', floorAt);
            if (objStart < 0 || objEnd < 0 || objEnd <= objStart)
            {
                searchFrom = floorAt + 7;
                continue;
            }

            var chunk = json.Substring(objStart, objEnd - objStart + 1);
            searchFrom = objEnd + 1;

            var id = JsonExtractString(chunk, "id");
            var name = JsonExtractString(chunk, "name");
            var floor = JsonExtractInt(chunk, "floor");
            var mapId = JsonExtractInt(chunk, "map_id");
            var x = JsonExtractInt(chunk, "x");
            var y = JsonExtractInt(chunk, "y");
            if (floor <= 0)
            {
                continue;
            }

            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 10);
            }

            _navWaypoints.Add(new NavWaypoint
            {
                Id = id,
                Name = name ?? "",
                Floor = floor,
                MapId = mapId,
                X = x,
                Y = y
            });
        }
    }

    private static string JsonExtractString(string obj, string key)
    {
        var k = "\"" + key + "\"";
        var at = obj.IndexOf(k, StringComparison.Ordinal);
        if (at < 0)
        {
            return "";
        }

        var colon = obj.IndexOf(':', at + k.Length);
        if (colon < 0)
        {
            return "";
        }

        var q1 = obj.IndexOf('"', colon + 1);
        if (q1 < 0)
        {
            return "";
        }

        var q2 = obj.IndexOf('"', q1 + 1);
        if (q2 < 0)
        {
            return "";
        }

        return obj.Substring(q1 + 1, q2 - q1 - 1)
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }

    private static int JsonExtractInt(string obj, string key)
    {
        var k = "\"" + key + "\"";
        var at = obj.IndexOf(k, StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        var colon = obj.IndexOf(':', at + k.Length);
        if (colon < 0)
        {
            return 0;
        }

        var i = colon + 1;
        while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t'))
        {
            i++;
        }

        var start = i;
        if (i < obj.Length && obj[i] == '-')
        {
            i++;
        }

        while (i < obj.Length && char.IsDigit(obj[i]))
        {
            i++;
        }

        if (i <= start)
        {
            return 0;
        }

        int v;
        return int.TryParse(obj.Substring(start, i - start), out v) ? v : 0;
    }

    private static void AppendTeamPools(StringBuilder sb)
    {
        // 队伍共享一份血魔池，只显示一条
        var uid = GetCaptainUid();
        if (string.IsNullOrEmpty(uid))
        {
            var uids = CollectTeamOrMultiUids();
            uid = uids.Count > 0 ? uids[0] : "";
        }

        if (string.IsNullOrEmpty(uid))
        {
            sb.AppendLine("(无)");
            return;
        }

        var p = GetPlayer(uid);
        if (p == null)
        {
            sb.AppendLine("(角色数据未就绪)");
            return;
        }

        var max = Convert.ToInt32(GetStaticMember("PlayerDataHolder", "HpMpPoolMax") ?? 0);
        var hp = Convert.ToInt32(GetMember(p, "hpPond") ?? 0);
        var mp = Convert.ToInt32(GetMember(p, "mpPond") ?? 0);
        if (max > 0)
        {
            sb.Append("血池 ").Append(hp).Append('/').Append(max)
                .Append("  魔池 ").Append(mp).Append('/').Append(max).AppendLine();
        }
        else
        {
            sb.Append("血池 ").Append(hp).Append("  魔池 ").Append(mp).AppendLine();
        }
    }

    private static void AppendTeamMoshi(StringBuilder sb)
    {
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            sb.AppendLine("(无)");
            return;
        }

        foreach (var uid in uids)
        {
            var p = GetPlayer(uid);
            var n = Convert.ToString(GetMember(p, "Name") ?? uid) ?? uid;
            var line = ReadMoshiBuffLine(uid);
            if (string.IsNullOrEmpty(line))
            {
                // 缺缓存时主动拉一次 BUFF（限流），避免一直显示“无Buff缓存”
                if (TryRequestPlayerBuffData(uid))
                {
                    line = "(魔石统计请求中…)";
                }
                else
                {
                    line = "(无Buff缓存，进游戏后看Buff面板或等推送)";
                }
            }

            sb.Append(n).Append("  ").AppendLine(line);
        }
    }

    /// <summary>向服务器请求玩家 BUFF（含每日魔石）。同 uid 8 秒内只发一次。</summary>
    private static bool TryRequestPlayerBuffData(string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        try
        {
            var now = NowMs();
            long last;
            if (MoshiBuffReqMs.TryGetValue(uid, out last) && now - last < 8000)
            {
                return true; // 已在请求窗口内
            }

            var protoType = FindType("Proto_CS_PlayerBuff");
            if (protoType == null)
            {
                return false;
            }

            var proto = Activator.CreateInstance(protoType);
            SetMember(proto, "Type", "玩家BUFF数据");
            SetMember(proto, "KUid", uid);

            var lss = FindType("LSSPROTO");
            var opcodeField = lss?.GetField(
                "LSSPROTO_PLAYERBUFF_FUNC",
                BindingFlags.Public | BindingFlags.Static);
            if (opcodeField == null)
            {
                return false;
            }

            var net = GetManagerInstance("NetManager");
            var send = net?.GetType().GetMethod(
                "SendMessage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (net == null || send == null)
            {
                return false;
            }

            send.Invoke(net, new object[] { opcodeField.GetValue(null), proto });
            MoshiBuffReqMs[uid] = now;
            WriteLog("RequestPlayerBuff uid=" + uid);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("RequestPlayerBuff EX: " + RootMessage(ex));
            return false;
        }
    }

    private static string ReadMoshiBuffLine(string uid)
    {
        try
        {
            var roleMgr = FindType("RoleManager");
            var field = roleMgr?.GetField("m_buffInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            object dictObj = null;
            if (field != null && field.IsStatic)
            {
                dictObj = field.GetValue(null);
            }
            else
            {
                var inst = GetManagerInstance("RoleManager");
                if (field != null && inst != null)
                {
                    dictObj = field.GetValue(inst);
                }
                else if (inst != null)
                {
                    dictObj = GetMember(inst, "m_buffInfo");
                }
            }

            var dict = dictObj as IDictionary;
            if (dict == null || !dict.Contains(uid))
            {
                return "";
            }

            var buff = dict[uid];
            var infos = GetMember(buff, "Info") as IEnumerable;
            if (infos == null)
            {
                return "";
            }

            foreach (var info in infos)
            {
                if (info == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(info, "Id") ?? 0) != 10)
                {
                    continue;
                }

                var str2 = Convert.ToString(GetMember(info, "Str2") ?? "");
                var str = Convert.ToString(GetMember(info, "Str") ?? "");
                var val = Convert.ToInt32(GetMember(info, "Value") ?? 0);
                var time = Convert.ToInt32(GetMember(info, "Time") ?? 0);
                if (!string.IsNullOrEmpty(str2))
                {
                    return str2;
                }

                if (!string.IsNullOrEmpty(str))
                {
                    return str + " " + val + "/" + time;
                }

                return val + "/" + time;
            }
        }
        catch (Exception ex)
        {
            WriteLog("ReadMoshi EX: " + RootMessage(ex));
        }

        return "";
    }

    private static string GetCaptainUid()
    {
        try
        {
            var teamData = GetStaticMember("PlayerDataHolder", "teamData") as Array;
            if (teamData != null && teamData.Length > 0)
            {
                var slot0 = teamData.GetValue(0);
                if (slot0 != null && Convert.ToInt32(GetMember(slot0, "UseFlag") ?? 0) == 1)
                {
                    var player = GetMember(slot0, "Player");
                    var uid = Convert.ToString(GetMember(player, "Uid") ?? "");
                    if (!string.IsNullOrEmpty(uid))
                    {
                        return uid;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
    }

    private static List<string> CollectTeamOrMultiUids()
    {
        var result = new List<string>();
        try
        {
            var teamMgr = GetManagerInstance("TeamManager");
            var multi = GetMember(teamMgr, "MultiInfo");
            var players = GetMember(multi, "Players") as IList;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    var uid = Convert.ToString(GetMember(p, "Uid") ?? "");
                    var online = Convert.ToInt32(GetMember(p, "Online") ?? 0);
                    if (!string.IsNullOrEmpty(uid) && online >= 1 && !result.Contains(uid))
                    {
                        result.Add(uid);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (result.Count > 0)
        {
            return result;
        }

        try
        {
            var teamData = GetStaticMember("PlayerDataHolder", "teamData") as Array;
            if (teamData != null)
            {
                foreach (var slot in teamData)
                {
                    if (slot == null || Convert.ToInt32(GetMember(slot, "UseFlag") ?? 0) != 1)
                    {
                        continue;
                    }

                    var uid = Convert.ToString(GetMember(GetMember(slot, "Player"), "Uid") ?? "");
                    if (!string.IsNullOrEmpty(uid) && !result.Contains(uid))
                    {
                        result.Add(uid);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (result.Count == 0)
        {
            var main = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "");
            if (!string.IsNullOrEmpty(main))
            {
                result.Add(main);
            }
        }

        return result;
    }

    private static object GetPlayer(string uid)
    {
        try
        {
            var holder = FindType("PlayerDataHolder");
            var m = holder?.GetMethod("GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static);
            if (m != null && !string.IsNullOrEmpty(uid))
            {
                return m.Invoke(null, new object[] { uid });
            }
        }
        catch
        {
            // ignore
        }

        return GetStaticMember("PlayerDataHolder", "playerData");
    }

    // ---------- UI build ----------

    private static void EnsureHost()
    {
        if (_hostGo != null && _hostComp != null)
        {
            return;
        }

        _hostGo = new GameObject("SeqChapterTestUiHost");
        UnityEngine.Object.DontDestroyOnLoad(_hostGo);
        try
        {
            _hostComp = _hostGo.AddComponent<SeqChapterTestUiHost>();
        }
        catch
        {
            var add = typeof(GameObject).GetMethod("AddComponent", new[] { typeof(Type) });
            _hostComp = add != null
                ? (Component)add.Invoke(_hostGo, new object[] { typeof(SeqChapterTestUiHost) })
                : null;
        }

        WriteLog("EnsureHost comp=" + (_hostComp != null));
    }

    private static void EnsurePanel()
    {
        if (_canvasGo != null && !IsUnityNull(_canvasGo))
        {
            return;
        }

        WriteLog("EnsurePanel");
        var rtType = RequireType("UnityEngine.RectTransform");
        var canvasType = RequireType("UnityEngine.Canvas");
        _canvasGo = CreateGoWithComponents(
            "SeqChapterModPanel",
            rtType,
            canvasType,
            FindType("UnityEngine.UI.CanvasScaler"),
            FindType("UnityEngine.UI.GraphicRaycaster"));
        CallStatic(RequireType("UnityEngine.Object"), "DontDestroyOnLoad",
            new[] { RequireType("UnityEngine.Object") }, new[] { _canvasGo });

        var canvas = GetComp(_canvasGo, canvasType);
        SetProp(canvas, "renderMode", EnumValue("UnityEngine.RenderMode", "ScreenSpaceOverlay", 0));
        SetProp(canvas, "overrideSorting", true);
        SetProp(canvas, "sortingOrder", 32767);
        StretchFull(RequireRect(_canvasGo, "canvas"));

        _shellGo = CreateUiChild(_canvasGo, "Shell", rtType);
        SetAnchoredCenter(RequireRect(_shellGo, "shell"), 620f, 620f);
        SetColor(AddComp(_shellGo, "UnityEngine.UI.Image"), 0.07f, 0.09f, 0.12f, 0.96f);

        var title = CreateUiChild(_shellGo, "Title", rtType);
        SetAnchoredTop(RequireRect(title, "title"), -50f, -10f, 300f, 26f);
        SetText(AddText(title), "序章面板", 18);

        // 右上角最小/关闭
        var minBtn = CreateUiChild(_shellGo, "Minimize", rtType);
        SetAnchoredTop(RequireRect(minBtn, "min"), 220f, -8f, 56f, 26f);
        var minImg = AddComp(minBtn, "UnityEngine.UI.Image");
        SetColor(minImg, 0.22f, 0.35f, 0.45f, 1f);
        var minLab = CreateUiChild(minBtn, "L", rtType);
        StretchFull(RequireRect(minLab, "ml"));
        SetText(AddText(minLab), "最小", 13);
        BindButton(minBtn, minImg, () => SetMinimized(true));

        var close = CreateUiChild(_shellGo, "Close", rtType);
        SetAnchoredTop(RequireRect(close, "close"), 280f, -8f, 56f, 26f);
        var closeImg = AddComp(close, "UnityEngine.UI.Image");
        SetColor(closeImg, 0.4f, 0.18f, 0.18f, 1f);
        var closeLab = CreateUiChild(close, "L", rtType);
        StretchFull(RequireRect(closeLab, "cl"));
        SetText(AddText(closeLab), "关闭", 13);
        BindButton(close, closeImg, () =>
        {
            _visible = false;
            _minimized = false;
            SetPanelActive(false);
        });

        // tabs：概况 / 战斗 / AI / 脚本 / 护航 / 界面 / 导航 / 形象
        _tabButtons.Clear();
        BuildTabButton(_shellGo, rtType, -278f, "概况", TabOverview, 60f);
        BuildTabButton(_shellGo, rtType, -214f, "战斗", TabBattle, 60f);
        BuildTabButton(_shellGo, rtType, -150f, "AI", TabSuperAi, 48f);
        BuildTabButton(_shellGo, rtType, -98f, "脚本", TabScript, 60f);
        BuildTabButton(_shellGo, rtType, -34f, "护航", TabEscort, 60f);
        BuildTabButton(_shellGo, rtType, 30f, "界面", TabOpenUi, 60f);
        BuildTabButton(_shellGo, rtType, 94f, "导航", TabNav, 60f);
        BuildTabButton(_shellGo, rtType, 158f, "形象", TabAppear, 60f);

        _bodyRoot = CreateUiChild(_shellGo, "Body", rtType);
        SetAnchoredTop(RequireRect(_bodyRoot, "body"), 0f, -88f, 580f, 500f);
        SetColor(AddComp(_bodyRoot, "UnityEngine.UI.Image"), 0.1f, 0.12f, 0.16f, 0.5f);

        // 左上角收缩按钮（默认隐藏）
        _miniFabGo = CreateUiChild(_canvasGo, "MiniFab", rtType);
        SetAnchoredTopLeft(RequireRect(_miniFabGo, "fab"), 10f, -10f, 110f, 36f);
        var fabImg = AddComp(_miniFabGo, "UnityEngine.UI.Image");
        SetColor(fabImg, 0.12f, 0.28f, 0.4f, 0.94f);
        var fabLab = CreateUiChild(_miniFabGo, "L", rtType);
        StretchFull(RequireRect(fabLab, "fl"));
        SetText(AddText(fabLab), "序章面板", 14);
        BindButton(_miniFabGo, fabImg, () => SetMinimized(false));
        SetGoActive(_miniFabGo, false);

        _minimized = false;
        ShowTab(_tab);
        WriteLog("EnsurePanel done");
    }

    private static void BuildTabButton(object shell, Type rtType, float x, string label, int tab, float width = 150f)
    {
        var go = CreateUiChild(shell, "Tab" + tab, rtType);
        SetAnchoredTop(RequireRect(go, "tab"), x, -48f, width, 32f);
        var img = AddComp(go, "UnityEngine.UI.Image");
        SetColor(img, 0.18f, 0.22f, 0.28f, 1f);
        var labGo = CreateUiChild(go, "L", rtType);
        StretchFull(RequireRect(labGo, "tl"));
        var text = AddText(labGo);
        SetText(text, label, 13);
        var captured = tab;
        BindButton(go, img, () => ShowTab(captured));
        _tabButtons.Add(text);
    }

    private static void RefreshTabButtonLabels()
    {
        var names = new[] { "概况", "战斗", "AI", "脚本", "护航", "界面", "导航", "形象" };
        for (var i = 0; i < _tabButtons.Count && i < names.Length; i++)
        {
            var mark = i == _tab ? "●" : "○";
            SetText(_tabButtons[i], mark + names[i], 12);
        }
    }

    private static void ClearBody()
    {
        _overviewText = null;
        _escortStatusText = null;
        _escortSearchInput = null;
        _navPosText = null;
        _navStatusText = null;
        _navFloorInput = null;
        _navXInput = null;
        _navYInput = null;
        _navNameInput = null;
        _catchSellYInput = null;
        _lingTangStatusText = null;
        _petNamerStatusText = null;
        _superAiStatusText = null;
        _superAiBattleRoot = null;
        _appearStatusText = null;
        if (_appearAnimInputs != null)
        {
            for (var i = 0; i < _appearAnimInputs.Length; i++)
            {
                _appearAnimInputs[i] = null;
            }
        }

        if (_appearPerfectBtns != null)
        {
            for (var i = 0; i < _appearPerfectBtns.Length; i++)
            {
                _appearPerfectBtns[i] = null;
            }
        }

        _modeButtons.Clear();
        _modeIds.Clear();
        if (_bodyRoot == null || IsUnityNull(_bodyRoot))
        {
            return;
        }

        try
        {
            var tr = GetProp(_bodyRoot, "transform");
            var countProp = tr.GetType().GetProperty("childCount");
            var getChild = tr.GetType().GetMethod("GetChild", new[] { typeof(int) });
            var childCount = countProp != null ? Convert.ToInt32(countProp.GetValue(tr, null)) : 0;
            for (var i = childCount - 1; i >= 0; i--)
            {
                var child = getChild.Invoke(tr, new object[] { i });
                var go = GetProp(child, "gameObject");
                CallStatic(RequireType("UnityEngine.Object"), "Destroy",
                    new[] { RequireType("UnityEngine.Object") }, new[] { go });
            }
        }
        catch (Exception ex)
        {
            WriteLog("ClearBody EX: " + RootMessage(ex));
        }
    }

    private static void BuildOverviewBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        var box = CreateUiChild(_bodyRoot, "Ov", rtType);
        StretchFull(RequireRect(box, "ov"));
        _overviewText = AddText(box);
        try
        {
            SetProp(_overviewText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(_overviewText, "加载中…", 15);
    }

    private static void BuildBattleBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        WriteLog("BuildBattleBody begin mode=" + _battleMode);

        float y = -8f;
        var hint = CreateUiChild(_bodyRoot, "Hint", rtType);
        SetAnchoredTop(RequireRect(hint, "h"), 0f, y, 540f, 36f);
        SetText(AddText(hint), "战斗模式互斥。超级AI请切到「AI」页。当前=" + ModeLabel(_battleMode), 13);
        y -= 44f;

        _modeButtons.Clear();
        _modeIds.Clear();
        AddModeRow(rtType, ModeNormal, "常规（什么都不开）", ref y, true);

        // 九动已停用：旧存档 ModeNine 状态重置为常规
        if (_battleMode == ModeNine)
        {
            _battleMode = ModeNormal;
        }

        // 抓宠（无宠二动）：不带宠时第二动防御（SeqChapterAutoCatchNoPet.dll）
        if (FeatureAvailable("SeqChapterAutoCatchNoPet", "hotfixdata/SeqChapterAutoCatchNoPet.dll.bytes"))
        {
            AddModeRow(rtType, ModeCatchNopet, "抓宠（无宠二动）", ref y, true);
        }

        if (FeatureAvailable("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes"))
        {
            AddModeRow(rtType, ModeCatch, "抓宠", ref y, true);
        }

        if (FeatureAvailable("SeqChapterAutoCatchSell", "hotfixdata/SeqChapterAutoCatchSell.dll.bytes"))
        {
            AddModeRow(rtType, ModeCatchSell, "抓宠卖银币", ref y, true);
        }

        if (FeatureAvailable("SeqChapterCountFarm", "hotfixdata/SeqChapterCountFarm.dll.bytes"))
        {
            AddModeRow(rtType, ModeCountFarm, "计数挂机（标题 ★挂机中★ 魔石进度）", ref y, true);
        }

        if (FeatureAvailable("SeqChapterAutoSeal", "hotfixdata/SeqChapterAutoSeal.dll.bytes"))
        {
            AddModeRow(rtType, ModeSeal, "烧卡", ref y, true);
        }

        if (FeatureAvailable("SeqChapterLv1Auto", "hotfixdata/SeqChapterLv1Auto.dll.bytes"))
        {
            AddModeRow(rtType, ModeLv1, "遇1级（封印/技能1/防御）", ref y, true);
        }

        if (FeatureAvailable("SeqChapterAutoCatchSell", "hotfixdata/SeqChapterAutoCatchSell.dll.bytes"))
        {
            AddCatchSellYRow(rtType, ref y);
        }

        // 采集自动提取：不属于战斗，独立开关，与战斗模式共存（不互斥）
        if (FeatureAvailable("SeqChapterAreaExtract", "hotfixdata/SeqChapterAreaExtract.dll.bytes"))
        {
            AddAreaExtractToggleRow(rtType, ref y);
        }

        WriteLog("BuildBattleBody done");
    }

    /// <summary>战斗模式页：采集自动提取独立开关（单格满 999 自动提取到背包）。</summary>
    private static void AddAreaExtractToggleRow(Type rtType, ref float y)
    {
        y -= 10f;
        var on = IsAreaExtractActive();

        var row = CreateUiChild(_bodyRoot, "AreaExtractRow", rtType);
        SetAnchoredTop(RequireRect(row, "aer"), 0f, y, 500f, 32f);
        var img = AddComp(row, "UnityEngine.UI.Image");
        SetColor(img, 0.16f, 0.24f, 0.26f, 1f);
        var lab = CreateUiChild(row, "L", rtType);
        StretchFull(RequireRect(lab, "ael"));
        var text = AddText(lab);
        SetText(text, (on ? "● " : "○ ") + "采集自动提取（单格满999提取，与战斗模式共存）", 13);
        BindButton(row, img, ToggleAreaExtractFromUi);

        y -= 34f;
    }

    private static bool IsAreaExtractActive()
    {
        try
        {
            var t = FindLoadedType("SeqChapterAreaExtract");
            if (t == null)
            {
                return false;
            }

            var m = t.GetMethod("IsPipelineActive", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return m != null && m.Invoke(null, null) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static void ToggleAreaExtractFromUi()
    {
        try
        {
            WriteLog("ToggleAreaExtractFromUi");
            var t = EnsureFeatureType("SeqChapterAreaExtract", "hotfixdata/SeqChapterAreaExtract.dll.bytes");
            if (t == null)
            {
                Tip("采集自动提取 DLL 加载失败（见日志）");
                return;
            }

            var toggle = t.GetMethod("ToggleFromUi", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var r = toggle != null ? toggle.Invoke(null, null) : null;
            Tip(r is bool b && b ? "采集自动提取已开启" : "采集自动提取已关闭");

            if (_tab == TabBattle)
            {
                ClearBody();
                BuildBattleBody();
                RefreshTabButtonLabels();
            }
        }
        catch (Exception ex)
        {
            WriteLog("ToggleAreaExtractFromUi EX: " + RootMessage(ex));
            Tip("采集自动提取失败: " + RootMessage(ex));
        }
    }

    /// <summary>战斗模式页：抓宠卖银币的回收阈值 Y（默认 6）。</summary>
    private static void AddCatchSellYRow(Type rtType, ref float y)
    {
        y -= 8f;
        _catchSellYStr = LoadCatchSellRecycleMinGrade().ToString();

        var tip = CreateUiChild(_bodyRoot, "CatchSellTip", rtType);
        SetAnchoredTop(RequireRect(tip, "cst"), 0f, y, 540f, 40f);
        SetText(
            AddText(tip),
            "抓宠卖银币：名字已#跳过；掉档≥Y且无@→回收；其余改名后满仓存仓。",
            12);
        y -= 42f;

        var lab = CreateUiChild(_bodyRoot, "CatchSellYLab", rtType);
        SetAnchoredTop(RequireRect(lab, "csyl"), -170f, y, 160f, 30f);
        SetText(AddText(lab), "回收掉档阈值 Y", 13);

        _catchSellYInput = CreateInputField(
            _bodyRoot, rtType, "CatchSellY", 20f, y, 80f, 30f, _catchSellYStr, "Y");

        var save = CreateUiChild(_bodyRoot, "CatchSellYSave", rtType);
        SetAnchoredTop(RequireRect(save, "csys"), 140f, y, 100f, 30f);
        var saveImg = AddComp(save, "UnityEngine.UI.Image");
        SetColor(saveImg, 0.18f, 0.42f, 0.28f, 1f);
        var saveLab = CreateUiChild(save, "L", rtType);
        StretchFull(RequireRect(saveLab, "csysl"));
        SetText(AddText(saveLab), "保存 Y", 13);
        BindButton(save, saveImg, SaveCatchSellYFromUi);

        y -= 36f;
    }

    private static string CatchSellConfigPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seqchapter_helper",
                "catch_sell.json");
        }
        catch
        {
            return Path.Combine(Environment.CurrentDirectory, "catch_sell.json");
        }
    }

    private static int LoadCatchSellRecycleMinGrade()
    {
        try
        {
            var path = CatchSellConfigPath();
            if (!File.Exists(path))
            {
                return CatchSellDefaultY;
            }

            var json = File.ReadAllText(path);
            var key = "recycle_min_grade";
            var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return CatchSellDefaultY;
            }

            idx = json.IndexOf(':', idx + key.Length);
            if (idx < 0)
            {
                return CatchSellDefaultY;
            }

            idx++;
            while (idx < json.Length && char.IsWhiteSpace(json[idx]))
            {
                idx++;
            }

            var end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
            {
                end++;
            }

            if (end > idx
                && int.TryParse(json.Substring(idx, end - idx), out var y)
                && y >= 0)
            {
                return y;
            }
        }
        catch
        {
            // ignore
        }

        return CatchSellDefaultY;
    }

    private static void SaveCatchSellYFromUi()
    {
        try
        {
            var raw = ReadCatchSellYField();
            if (!int.TryParse(raw, out var y) || y < 0)
            {
                Tip("Y 请填非负整数（默认 " + CatchSellDefaultY + "）");
                return;
            }

            _catchSellYStr = y.ToString();
            var path = CatchSellConfigPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                path,
                "{\n  \"recycle_min_grade\": " + y + "\n}\n");
            Tip("已保存卖银回收阈值 Y=" + y);
            WriteLog("catch_sell Y=" + y + " -> " + path);
        }
        catch (Exception ex)
        {
            Tip("保存 Y 失败: " + RootMessage(ex));
            WriteLog("SaveCatchSellY EX: " + RootMessage(ex));
        }
    }

    private static string ReadCatchSellYField()
    {
        try
        {
            if (_catchSellYInput != null && !IsUnityNull(_catchSellYInput))
            {
                var t = GetProp(_catchSellYInput, "text") ?? GetMember(_catchSellYInput, "text");
                var s = Convert.ToString(t ?? "");
                if (!string.IsNullOrEmpty(s))
                {
                    return s.Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return (_catchSellYStr ?? CatchSellDefaultY.ToString()).Trim();
    }

    private static void BuildAppearBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        LoadAppearConfigIntoUiState();
        try
        {
            ReloadBattleAppearDll();
            FindType("SeqChapterBattleAppear")
                ?.GetMethod("LoadUidProfilesOnReady", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
        }
        catch
        {
            // ignore
        }

        float y = -6f;
        var hint = CreateUiChild(_bodyRoot, "AppearHint", rtType);
        SetAnchoredTop(RequireRect(hint, "ah"), 0f, y, 560f, 72f);
        SetText(
            AddText(hint),
            "粘贴后按玩家Uid写入 AppData（删客户端不丢）。进战/过图都按本地已存 Uid 套形象。\n"
            + "同机其它号只要本地有档，过图后也能互相看到皮肤。\n"
            + "五开槽1~5=在线顺序；「清空当前账号」只删当前登录角色。",
            12);
        y -= 78f;

        var paste = CreateUiChild(_bodyRoot, "AppearPaste", rtType);
        SetAnchoredTop(RequireRect(paste, "ap"), -150f, y, 170f, 36f);
        var pasteImg = AddComp(paste, "UnityEngine.UI.Image");
        SetColor(pasteImg, 0.16f, 0.42f, 0.55f, 1f);
        var pasteLab = CreateUiChild(paste, "L", rtType);
        StretchFull(RequireRect(pasteLab, "apl"));
        SetText(AddText(pasteLab), "粘贴导入", 14);
        BindButton(paste, pasteImg, ImportAppearFromClipboard);

        _appearEnableBtn = CreateUiChild(_bodyRoot, "AppearEn", rtType);
        SetAnchoredTop(RequireRect(_appearEnableBtn, "aen"), 40f, y, 140f, 36f);
        var enImg = AddComp(_appearEnableBtn, "UnityEngine.UI.Image");
        SetColor(enImg, _appearEnabled ? 0.15f : 0.25f, _appearEnabled ? 0.45f : 0.22f, 0.28f, 1f);
        var enLab = CreateUiChild(_appearEnableBtn, "L", rtType);
        StretchFull(RequireRect(enLab, "enl"));
        SetText(AddText(enLab), _appearEnabled ? "● 钩子已开" : "○ 钩子关闭", 13);
        BindButton(_appearEnableBtn, enImg, () =>
        {
            _appearEnabled = !_appearEnabled;
            RefreshAppearEnableBtn();
            try
            {
                ToggleAppearEnabledOnly();
                Tip(_appearEnabled ? "钩子已开（已保存）" : "钩子已关（已保存）");
            }
            catch (Exception ex)
            {
                WriteLog("ToggleAppear EX: " + RootMessage(ex));
                Tip("钩子开关保存失败: " + RootMessage(ex));
            }
        });

        var clearBtn = CreateUiChild(_bodyRoot, "AppearClear", rtType);
        SetAnchoredTop(RequireRect(clearBtn, "aclr"), 200f, y, 150f, 36f);
        var clearImg = AddComp(clearBtn, "UnityEngine.UI.Image");
        SetColor(clearImg, 0.45f, 0.2f, 0.18f, 1f);
        var clearLab = CreateUiChild(clearBtn, "L", rtType);
        StretchFull(RequireRect(clearLab, "clrl"));
        SetText(AddText(clearLab), "清空当前账号", 13);
        BindButton(clearBtn, clearImg, ClearCurrentAppearUid);
        y -= 44f;

        // 推荐方案 1/2/3/4（两行）
        for (var i = 1; i <= 4; i++)
        {
            var presetIdx = i;
            var row = (i - 1) / 2;
            var col = (i - 1) % 2;
            var bx = -150f + col * 155f;
            var by = y - row * 38f;
            var pbtn = CreateUiChild(_bodyRoot, "AppearPreset" + i, rtType);
            SetAnchoredTop(RequireRect(pbtn, "ap" + i), bx, by, 145f, 34f);
            var pimg = AddComp(pbtn, "UnityEngine.UI.Image");
            SetColor(pimg, 0.2f, 0.38f, 0.28f, 1f);
            var plab = CreateUiChild(pbtn, "L", rtType);
            StretchFull(RequireRect(plab, "apl" + i));
            SetText(AddText(plab), "推荐方案" + i, 12);
            BindButton(pbtn, pimg, () => ImportAppearPreset(presetIdx));
        }

        y -= 80f;

        var tip2 = CreateUiChild(_bodyRoot, "AppearTip2", rtType);
        SetAnchoredTop(RequireRect(tip2, "at2"), 0f, y, 560f, 36f);
        SetText(AddText(tip2), "存档: AppData\\LocalLow\\魔力永恒\\魔力宝贝：序章\\battle_appear_uid.json", 11);
        y -= 40f;

        _appearStatusText = CreateUiChild(_bodyRoot, "AppearSt", rtType);
        SetAnchoredTop(RequireRect(_appearStatusText, "ast"), 0f, y, 560f, 200f);
        SetText(AddText(_appearStatusText), AppearStatusLine(), 12);
        WriteLog("BuildAppearBody done");
    }

    private static void ClearCurrentAppearUid()
    {
        try
        {
            ReloadBattleAppearDll();
            var t = FindType("SeqChapterBattleAppear");
            var m = t?.GetMethod("ClearCurrentUidProfile", BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                Tip("形象钩子无 ClearCurrentUidProfile");
                return;
            }

            var err = m.Invoke(null, null) as string;
            if (!string.IsNullOrEmpty(err))
            {
                Tip(err);
            }
            else
            {
                Tip("已清空当前角色 Uid 形象档");
            }

            if (_appearStatusText != null && !IsUnityNull(_appearStatusText))
            {
                SetText(AddText(_appearStatusText), AppearStatusLine(), 12);
            }
        }
        catch (Exception ex)
        {
            Tip("清空失败: " + RootMessage(ex));
            WriteLog("ClearAppear EX: " + RootMessage(ex));
        }
    }

    private static void ImportAppearPreset(int index)
    {
        try
        {
            ReloadBattleAppearDll();
            var t = FindType("SeqChapterBattleAppear");
            var m = t?.GetMethod("ImportPreset", BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                Tip("形象钩子无 ImportPreset");
                return;
            }

            var err = m.Invoke(null, new object[] { index }) as string;
            if (!string.IsNullOrEmpty(err))
            {
                Tip(err);
                return;
            }

            LoadAppearConfigIntoUiState();
            _appearEnabled = true;
            RefreshAppearEnableBtn();
            if (_appearStatusText != null && !IsUnityNull(_appearStatusText))
            {
                SetText(AddText(_appearStatusText), AppearStatusLine(), 12);
            }

            Tip("已导入推荐方案" + index + "（已按Uid保存）");
        }
        catch (Exception ex)
        {
            Tip("导入方案失败: " + RootMessage(ex));
            WriteLog("ImportPreset EX: " + RootMessage(ex));
        }
    }

    private static string ReplaceJsonBool(string text, string key, bool value)
    {
        var needle = "\"" + key + "\"";
        var idx = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return text;
        }

        var colon = text.IndexOf(':', idx + needle.Length);
        if (colon < 0)
        {
            return text;
        }

        var p = colon + 1;
        while (p < text.Length && char.IsWhiteSpace(text[p]))
        {
            p++;
        }

        var end = p;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        if (end <= p)
        {
            return text;
        }

        return text.Substring(0, p) + (value ? "true" : "false") + text.Substring(end);
    }

    private static void ImportAppearFromClipboard()
    {
        try
        {
            var clip = ReadClipboardText();
            if (string.IsNullOrEmpty(clip))
            {
                Tip("剪贴板为空");
                return;
            }

            ReloadBattleAppearDll();
            var t = FindType("SeqChapterBattleAppear");
            var m = t?.GetMethod("ImportFromCode", BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                Tip("形象钩子 DLL 未加载/无 ImportFromCode");
                return;
            }

            var err = m.Invoke(null, new object[] { clip }) as string;
            if (!string.IsNullOrEmpty(err))
            {
                Tip("导入失败: " + err);
                WriteLog("ImportAppear fail: " + err);
                return;
            }

            LoadAppearConfigIntoUiState();
            _appearEnabled = true;
            RefreshAppearEnableBtn();
            if (_appearStatusText != null && !IsUnityNull(_appearStatusText))
            {
                SetText(AddText(_appearStatusText), AppearStatusLine(), 12);
            }

            Tip("形象代码已导入并按Uid保存（钩子已开）");
        }
        catch (Exception ex)
        {
            Tip("导入异常: " + RootMessage(ex));
            WriteLog("ImportAppear EX: " + RootMessage(ex));
        }
    }

    private static string ReadClipboardText()
    {
        try
        {
            var gui = FindType("UnityEngine.GUIUtility");
            var p = gui?.GetProperty("systemCopyBuffer", BindingFlags.Public | BindingFlags.Static);
            return p?.GetValue(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void ToggleAppearEnabledOnly()
    {
        ReloadBattleAppearDll();
        var t = FindType("SeqChapterBattleAppear");
        var set = t?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
        if (set != null)
        {
            set.Invoke(null, new object[] { _appearEnabled });
            return;
        }

        // 兼容旧 DLL：直接改 json
        var path = ResolveAppearConfigPath(createDir: true);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SaveAppearConfigFromUi();
            return;
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        text = ReplaceJsonBool(text, "enabled", _appearEnabled);
        File.WriteAllText(path, text, Encoding.UTF8);
        try
        {
            var dataPath = ReadUnityDataPathSafe();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var hf = Path.Combine(dataPath, "assets", "hotfixdata", "battle_appear.json");
                File.WriteAllText(hf, text, Encoding.UTF8);
            }
        }
        catch
        {
            // ignore
        }

        ReloadBattleAppearDll();
    }

    private static string GetAppearAnimStr(int slot1To5)
    {
        try
        {
            var path = ResolveAppearConfigPath(createDir: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "";
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            // 粗解析对应 slot 的 pet_anim
            var marker = "\"slot\": " + slot1To5;
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                marker = "\"slot\":" + slot1To5;
                idx = text.IndexOf(marker, StringComparison.Ordinal);
            }

            if (idx < 0)
            {
                return "";
            }

            var end = text.IndexOf('}', idx);
            if (end < 0)
            {
                return "";
            }

            var chunk = text.Substring(idx, end - idx);
            var key = "\"pet_anim\"";
            var k = chunk.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
            {
                return "";
            }

            var colon = chunk.IndexOf(':', k);
            if (colon < 0)
            {
                return "";
            }

            var p = colon + 1;
            while (p < chunk.Length && char.IsWhiteSpace(chunk[p]))
            {
                p++;
            }

            var e = p;
            if (e < chunk.Length && (chunk[e] == '-' || chunk[e] == '+'))
            {
                e++;
            }

            while (e < chunk.Length && char.IsDigit(chunk[e]))
            {
                e++;
            }

            if (e <= p)
            {
                return "";
            }

            var n = int.Parse(chunk.Substring(p, e - p), CultureInfo.InvariantCulture);
            return n < 0 ? "" : n.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string PerfectLabel(int v)
    {
        if (v < 0)
        {
            return "满档:不改";
        }

        return v != 0 ? "满档:开" : "满档:关";
    }

    private static void CycleAppearPerfect(int index0)
    {
        if (index0 < 0 || index0 >= 5)
        {
            return;
        }

        // -1 → 1 → 0 → -1
        var cur = _appearPerfect[index0];
        if (cur < 0)
        {
            _appearPerfect[index0] = 1;
        }
        else if (cur != 0)
        {
            _appearPerfect[index0] = 0;
        }
        else
        {
            _appearPerfect[index0] = -1;
        }

        try
        {
            var labGo = GetChild(_appearPerfectBtns[index0], "L");
            var text = labGo != null ? GetComp(labGo, "UnityEngine.UI.Text") : null;
            if (text != null)
            {
                SetText(text, PerfectLabel(_appearPerfect[index0]), 12);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void RefreshAppearEnableBtn()
    {
        if (_appearEnableBtn == null || IsUnityNull(_appearEnableBtn))
        {
            return;
        }

        try
        {
            var img = GetComp(_appearEnableBtn, "UnityEngine.UI.Image");
            if (img != null)
            {
                SetColor(img, _appearEnabled ? 0.15f : 0.25f, _appearEnabled ? 0.45f : 0.22f, 0.28f, 1f);
            }

            var lab = GetChild(_appearEnableBtn, "L");
            if (lab != null)
            {
                SetText(AddText(lab), _appearEnabled ? "● 钩子已开" : "○ 钩子关闭", 13);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void LoadAppearConfigIntoUiState()
    {
        _appearEnabled = false;
        for (var i = 0; i < 5; i++)
        {
            _appearPerfect[i] = -1;
        }

        try
        {
            var path = ResolveAppearConfigPath(createDir: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            _appearEnabled = text.IndexOf("\"enabled\": true", StringComparison.OrdinalIgnoreCase) >= 0
                             || text.IndexOf("\"enabled\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            for (var slot = 1; slot <= 5; slot++)
            {
                var marker = "\"slot\": " + slot;
                var idx = text.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                {
                    marker = "\"slot\":" + slot;
                    idx = text.IndexOf(marker, StringComparison.Ordinal);
                }

                if (idx < 0)
                {
                    continue;
                }

                var end = text.IndexOf('}', idx);
                if (end < 0)
                {
                    continue;
                }

                var chunk = text.Substring(idx, end - idx);
                var pk = chunk.IndexOf("\"perfect\"", StringComparison.OrdinalIgnoreCase);
                if (pk >= 0)
                {
                    var colon = chunk.IndexOf(':', pk);
                    if (colon >= 0)
                    {
                        var p = colon + 1;
                        while (p < chunk.Length && char.IsWhiteSpace(chunk[p]))
                        {
                            p++;
                        }

                        var e = p;
                        if (e < chunk.Length && (chunk[e] == '-' || chunk[e] == '+'))
                        {
                            e++;
                        }

                        while (e < chunk.Length && char.IsDigit(chunk[e]))
                        {
                            e++;
                        }

                        if (e > p)
                        {
                            var n = int.Parse(chunk.Substring(p, e - p), CultureInfo.InvariantCulture);
                            _appearPerfect[slot - 1] = n < 0 ? -1 : (n != 0 ? 1 : 0);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("LoadAppearConfig EX: " + RootMessage(ex));
        }
    }

    private static void SaveAppearConfigFromUi()
    {
        try
        {
            var path = ResolveAppearConfigPath(createDir: true);
            if (string.IsNullOrEmpty(path))
            {
                Tip("找不到配置路径");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.Append("  \"enabled\": ").Append(_appearEnabled ? "true" : "false").AppendLine(",");
            sb.AppendLine("  \"comment\": \"请用游戏外工具生成 CGAP1 代码后粘贴导入。\",");
            sb.AppendLine("  \"slots\": [");
            for (var i = 0; i < 5; i++)
            {
                sb.Append("    { \"slot\": ").Append(i + 1);
                sb.Append(", \"pet_anim\": 0, \"role_halo\": 0, \"perfect\": 0, \"max_crest\": 0");
                sb.Append(", \"char_anim\": 0, \"ride_skin\": 0 }");
                if (i < 4)
                {
                    sb.Append(',');
                }

                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            // 同步一份到 tools/
            try
            {
                var toolsPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? "", "..", "tools", "battle_appear.json");
                // path 一般是 .../cg37_Data/assets/hotfixdata/battle_appear.json
                var dataPath = ReadUnityDataPathSafe();
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                    var t = Path.Combine(gameRoot, "tools", "battle_appear.json");
                    File.WriteAllText(t, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // ignore
            }

            ReloadBattleAppearDll();
            Tip("形象配置已保存");
            if (_appearStatusText != null && !IsUnityNull(_appearStatusText))
            {
                SetText(AddText(_appearStatusText), AppearStatusLine(), 12);
            }
        }
        catch (Exception ex)
        {
            Tip("保存失败: " + RootMessage(ex));
            WriteLog("SaveAppear EX: " + RootMessage(ex));
        }
    }

    private static string AppearStatusLine()
    {
        try
        {
            var t = FindType("SeqChapterBattleAppear");
            var m = t?.GetMethod("Status", BindingFlags.Public | BindingFlags.Static);
            var s = m?.Invoke(null, null) as string;
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }
        catch
        {
            // ignore
        }

        return "钩子DLL未加载时，进战后首次收包会自动加载。enabled=" + _appearEnabled;
    }

    private static void ReloadBattleAppearDll()
    {
        try
        {
            var t = FindType("SeqChapterBattleAppear");
            if (t == null)
            {
                // 尝试从 hotfixdata 加载
                TryLoadExternalDll("hotfixdata/SeqChapterBattleAppear.dll.bytes", "SeqChapterBattleAppear");
                t = FindType("SeqChapterBattleAppear");
            }

            t?.GetMethod("ReloadConfig", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            WriteLog("ReloadBattleAppear EX: " + RootMessage(ex));
        }
    }

    private static string ResolveAppearConfigPath(bool createDir)
    {
        try
        {
            var dataPath = ReadUnityDataPathSafe();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                var tools = Path.Combine(gameRoot, "tools", "battle_appear.json");
                var hf = Path.Combine(dataPath, "assets", "hotfixdata", "battle_appear.json");
                if (File.Exists(tools))
                {
                    return tools;
                }

                if (File.Exists(hf))
                {
                    return hf;
                }

                if (createDir)
                {
                    var dir = Path.GetDirectoryName(tools);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    return tools;
                }
            }
        }
        catch
        {
            // ignore
        }

        return @"E:\cross\魔力宝贝：序章\tools\battle_appear.json";
    }

    private static string ReadUnityDataPathSafe()
    {
        try
        {
            var app = FindType("UnityEngine.Application");
            var p = app?.GetProperty("dataPath", BindingFlags.Public | BindingFlags.Static);
            return p?.GetValue(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void TryLoadExternalDll(string assetPath, string typeName)
    {
        try
        {
            var fileUtil = FindType("FileUtil");
            var load = fileUtil?.GetMethod(
                "LoadBytesFromHotfixAssets",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            var bytes = load?.Invoke(null, new object[] { assetPath }) as byte[];
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            Assembly.Load(bytes);
            WriteLog("Loaded " + typeName + " from " + assetPath);
        }
        catch (Exception ex)
        {
            WriteLog("TryLoadExternalDll EX: " + RootMessage(ex));
        }
    }

    /// <summary>与 BattleRole.placeOfIndex 一致；localPlace&gt;=5 为前排（靠场中）。</summary>
    private static readonly int[] SuperAiPlaceOfIndex =
    {
        2, 3, 1, 4, 0, 7, 8, 6, 9, 5,
        12, 13, 11, 14, 10, 17, 18, 16, 19, 15
    };

    private static void SetShellSize(float w, float h)
    {
        try
        {
            if (_shellGo != null && !IsUnityNull(_shellGo))
            {
                SetAnchoredCenter(RequireRect(_shellGo, "shell"), w, h);
            }

            if (_bodyRoot != null && !IsUnityNull(_bodyRoot))
            {
                SetAnchoredTop(RequireRect(_bodyRoot, "body"), 0f, -88f, w - 40f, h - 120f);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsSuperAiFrontRow(int battleIdx)
    {
        if (battleIdx < 0 || battleIdx >= SuperAiPlaceOfIndex.Length)
        {
            return (battleIdx % 10) >= 5;
        }

        return (SuperAiPlaceOfIndex[battleIdx] % 10) >= 5;
    }

    private static void BuildSuperAiBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        WriteLog("BuildSuperAiBody begin sai=" + _superAiActive + " mode=" + _battleMode);
        // AI 页放大面板，四列阵型够用
        SetShellSize(760f, 720f);

        float y = -4f;
        var saiAllowed = IsSuperAiModeAllowed(_battleMode);
        var sai = CreateUiChild(_bodyRoot, "SuperAi", rtType);
        SetAnchoredTop(RequireRect(sai, "sai"), 0f, y, 700f, 36f);
        var saiImg = AddComp(sai, "UnityEngine.UI.Image");
        if (!saiAllowed)
        {
            SetColor(saiImg, 0.25f, 0.18f, 0.14f, 1f);
            var saiLab = CreateUiChild(sai, "L", rtType);
            StretchFull(RequireRect(saiLab, "sail"));
            SetText(AddText(saiLab), "○ 超级AI（请先到「战斗」页选常规/九动）", 14);
        }
        else
        {
            SetColor(saiImg, _superAiActive ? 0.45f : 0.15f, _superAiActive ? 0.32f : 0.38f,
                _superAiActive ? 0.12f : 0.55f, 1f);
            var saiLab = CreateUiChild(sai, "L", rtType);
            StretchFull(RequireRect(saiLab, "sail"));
            SetText(AddText(saiLab),
                (_superAiActive ? "● 超级AI（点此停止）" : "○ 超级AI（点此启动）") + " · " + ModeLabel(_battleMode), 14);
            BindButton(sai, saiImg, ToggleSuperAi);
        }

        y -= 42f;
        _superAiBattleRoot = CreateUiChild(_bodyRoot, "SuperAiBattle", rtType);
        SetAnchoredTop(RequireRect(_superAiBattleRoot, "saib"), 0f, y, 720f, 560f);
        SetColor(AddComp(_superAiBattleRoot, "UnityEngine.UI.Image"), 0.08f, 0.1f, 0.14f, 0.75f);
        _superAiStatusText = null;
        BuildSuperAiBattlefieldContent();
        WriteLog("BuildSuperAiBody done units=" + _superAiUnits.Count);
    }

    private static void AddModeRow(Type rtType, string modeId, string label, ref float y, bool available)
    {
        if (!available)
        {
            return;
        }

        var row = CreateUiChild(_bodyRoot, "M_" + modeId, rtType);
        SetAnchoredTop(RequireRect(row, "mr"), 0f, y, 500f, 32f);
        var img = AddComp(row, "UnityEngine.UI.Image");
        SetColor(img, 0.16f, 0.2f, 0.26f, 1f);
        var lab = CreateUiChild(row, "L", rtType);
        StretchFull(RequireRect(lab, "ml"));
        var text = AddText(lab);
        var mark = _battleMode == modeId ? "● " : "○ ";
        SetText(text, mark + label, 14);
        var id = modeId;
        BindButton(row, img, () => SelectBattleMode(id));
        _modeButtons.Add(text);
        _modeIds.Add(modeId);
        y -= 34f;
    }


    private static void RefreshSuperAiBattlefieldUi(bool forceRebuild)
    {
        try
        {
            if (!_superAiActive || _tab != TabSuperAi || _bodyRoot == null || IsUnityNull(_bodyRoot))
            {
                return;
            }

            if (_superAiBattleRoot == null || IsUnityNull(_superAiBattleRoot) || forceRebuild)
            {
                if (_superAiBattleRoot != null && !IsUnityNull(_superAiBattleRoot))
                {
                    BuildSuperAiBattlefieldContent();
                }
            }
        }
        catch
        {
            // ignore UI refresh
        }
    }

    private static void BuildSuperAiBattlefieldContent()
    {
        if (_superAiBattleRoot == null || IsUnityNull(_superAiBattleRoot))
        {
            return;
        }

        try
        {
            // 清子节点
            var tr = GetProp(_superAiBattleRoot, "transform");
            var countProp = tr.GetType().GetProperty("childCount");
            var getChild = tr.GetType().GetMethod("GetChild", new[] { typeof(int) });
            var childCount = countProp != null ? Convert.ToInt32(countProp.GetValue(tr, null)) : 0;
            for (var i = childCount - 1; i >= 0; i--)
            {
                var child = getChild.Invoke(tr, new object[] { i });
                var go = GetProp(child, "gameObject");
                if (go != null)
                {
                    CallStatic(RequireType("UnityEngine.Object"), "Destroy",
                        new[] { RequireType("UnityEngine.Object") }, new[] { go });
                }
            }
        }
        catch
        {
            // ignore clear
        }

        var rtType = RequireType("UnityEngine.RectTransform");
        if (!_superAiActive)
        {
            var tip = CreateUiChild(_superAiBattleRoot, "Tip", rtType);
            StretchFull(RequireRect(tip, "tip"));
            var tx = AddText(tip);
            try { SetProp(tx, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0)); } catch { }
            SetText(tx, FormatSuperAiStatus(), 11);
            return;
        }

        if (_superAiUiPage == 1 && _superAiDetailIndex >= 0 && _superAiDetailIndex < _superAiUnits.Count)
        {
            BuildSuperAiDetailPage(rtType);
            return;
        }

        BuildSuperAiListPage(rtType);
    }

    private static void BuildSuperAiListPage(Type rtType)
    {
        var head = CreateUiChild(_superAiBattleRoot, "Head", rtType);
        SetAnchoredTop(RequireRect(head, "hd"), 0f, -2f, 700f, 18f);
        SetText(AddText(head),
            "敌后 | 敌前 | 我前 | 我后　共" + _superAiUnits.Count
            + (_superAiLastSimLine.Length > 0 ? " · 有快照" : " · 等待战斗") + "（点单位详情）", 11);

        // 四列：敌后、敌前、我前、我后
        var cols = new List<int>[4];
        for (var c = 0; c < 4; c++)
        {
            cols[c] = new List<int>();
        }

        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            var u = _superAiUnits[i];
            var front = IsSuperAiFrontRow(u.Idx);
            int col;
            if (!u.Mine)
            {
                col = front ? 1 : 0; // 敌前 / 敌后
            }
            else
            {
                col = front ? 2 : 3; // 我前 / 我后
            }

            cols[col].Add(i);
        }

        for (var c = 0; c < 4; c++)
        {
            cols[c].Sort((ia, ib) => _superAiUnits[ia].Idx.CompareTo(_superAiUnits[ib].Idx));
        }

        var titles = new[] { "敌后", "敌前", "我前", "我后" };
        const float colW = 168f;
        const float cardW = 158f;
        const float cardH = 72f;
        const float barW = 50f;
        var startX = -3f * colW / 2f; // 四列居中
        for (var c = 0; c < 4; c++)
        {
            var colX = startX + c * colW;
            var title = CreateUiChild(_superAiBattleRoot, "ColT" + c, rtType);
            SetAnchoredTop(RequireRect(title, "ct" + c), colX, -22f, colW - 8f, 16f);
            SetText(AddText(title), titles[c] + "(" + cols[c].Count + ")", 11);

            var y = -42f;
            var list = cols[c];
            for (var r = 0; r < list.Count && r < 6; r++)
            {
                var ui = list[r];
                var u = _superAiUnits[ui];
                BuildSuperAiUnitCard(rtType, ui, u, colX, y, cardW, cardH, barW);
                y -= cardH + 4f;
            }
        }

        if (_superAiUnits.Count == 0)
        {
            var empty = CreateUiChild(_superAiBattleRoot, "Empty", rtType);
            SetAnchoredTop(RequireRect(empty, "em"), 0f, -80f, 480f, 40f);
            SetText(AddText(empty), "等待进入战斗…", 13);
        }
    }

    private static void BuildSuperAiUnitCard(Type rtType, int listIndex, SuperAiUnitSnap u,
        float colX, float y, float cardW, float cardH, float barW)
    {
        var row = CreateUiChild(_superAiBattleRoot, "U" + listIndex, rtType);
        SetAnchoredTop(RequireRect(row, "ur" + listIndex), colX, y, cardW, cardH);
        var rowImg = AddComp(row, "UnityEngine.UI.Image");
        SetColor(rowImg, u.Mine ? 0.12f : 0.2f, u.Mine ? 0.2f : 0.12f, u.Mine ? 0.16f : 0.12f, 0.92f);

        var nameLab = CreateUiChild(row, "N", rtType);
        SetAnchoredTopLeft(RequireRect(nameLab, "nl"), 4f, -2f, cardW - 8f, 28f);
        var nm = u.Name ?? "";
        if (nm.Length > 6)
        {
            nm = nm.Substring(0, 6);
        }

        var tag = u.IsPlayer ? "P" : "宠";
        SetText(AddText(nameLab), tag + nm + "\nLv" + u.Level, 10);

        // 血蓝叠放，宽约 50
        AddResourceBar(row, rtType, "Hp", 4f, -34f, barW, 8f, u.Hp, u.MaxHp, 0.8f, 0.2f, 0.2f, false);
        AddResourceBar(row, rtType, "Mp", 4f, -44f, barW, 8f, u.Mp, u.MaxMp, 0.25f, 0.4f, 0.85f, false);
        var num = CreateUiChild(row, "Num", rtType);
        SetAnchoredTopLeft(RequireRect(num, "nu"), 58f, -34f, cardW - 64f, 28f);
        SetText(AddText(num), u.Hp + "/" + u.MaxHp + "\n" + u.Mp + "/" + u.MaxMp, 9);

        var idx = listIndex;
        BindButton(row, rowImg, () =>
        {
            _superAiUiPage = 1;
            _superAiDetailIndex = idx;
            BuildSuperAiBattlefieldContent();
        });
    }

    private static void AddResourceBar(object parent, Type rtType, string name, float x, float y, float w, float h,
        int cur, int max, float r, float g, float b, bool withLabel = true)
    {
        var bg = CreateUiChild(parent, name + "Bg", rtType);
        SetAnchoredTopLeft(RequireRect(bg, name + "b"), x, y, w, h);
        SetColor(AddComp(bg, "UnityEngine.UI.Image"), 0.15f, 0.15f, 0.18f, 1f);

        var ratio = max > 0 ? Math.Max(0f, Math.Min(1f, cur / (float)max)) : 0f;
        var fillW = Math.Max(2f, w * ratio);
        var fill = CreateUiChild(bg, "Fill", rtType);
        SetAnchoredTopLeft(RequireRect(fill, name + "f"), 0f, 0f, fillW, h);
        SetColor(AddComp(fill, "UnityEngine.UI.Image"), r, g, b, 1f);

        if (withLabel)
        {
            var lab = CreateUiChild(parent, name + "T", rtType);
            SetAnchoredTopLeft(RequireRect(lab, name + "t"), x, y - 12f, w + 40f, 12f);
            SetText(AddText(lab), name + " " + cur + "/" + max, 10);
        }
    }

    private static void BuildSuperAiDetailPage(Type rtType)
    {
        var u = _superAiUnits[_superAiDetailIndex];
        var back = CreateUiChild(_superAiBattleRoot, "Back", rtType);
        SetAnchoredTopLeft(RequireRect(back, "bk"), 8f, -6f, 80f, 28f);
        var backImg = AddComp(back, "UnityEngine.UI.Image");
        SetColor(backImg, 0.25f, 0.3f, 0.4f, 1f);
        var bl = CreateUiChild(back, "L", rtType);
        StretchFull(RequireRect(bl, "bll"));
        SetText(AddText(bl), "← 返回", 13);
        BindButton(back, backImg, () =>
        {
            _superAiUiPage = 0;
            _superAiDetailIndex = -1;
            BuildSuperAiBattlefieldContent();
        });

        var title = CreateUiChild(_superAiBattleRoot, "Title", rtType);
        SetAnchoredTop(RequireRect(title, "tt"), 40f, -6f, 400f, 28f);
        SetText(AddText(title), (u.Mine ? "[我]" : "[敌]") + u.Name + " Lv" + u.Level, 15);

        AddResourceBar(_superAiBattleRoot, rtType, "Hp", 20f, -42f, 460f, 14f, u.Hp, u.MaxHp, 0.8f, 0.22f, 0.22f);
        AddResourceBar(_superAiBattleRoot, rtType, "Mp", 20f, -72f, 460f, 14f, u.Mp, u.MaxMp, 0.22f, 0.4f, 0.8f);

        var box = CreateUiChild(_superAiBattleRoot, "Detail", rtType);
        SetAnchoredTop(RequireRect(box, "dt"), 0f, -110f, 500f, 220f);
        var tx = AddText(box);
        try { SetProp(tx, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0)); } catch { }
        var sb = new StringBuilder();
        if (u.DetailOk)
        {
            if (!u.Mine)
            {
                sb.AppendLine("倍率 x" + u.Rate);
            }
            else
            {
                sb.AppendLine("来源: 系统面板属性（血蓝取战斗）");
            }

            sb.AppendLine("攻击 " + u.Atk);
            sb.AppendLine("防御 " + u.Def);
            sb.AppendLine("敏捷 " + u.Agi);
            sb.AppendLine("精神 " + u.Spirit);
            sb.AppendLine("回复 " + u.Rec);
            if (!string.IsNullOrEmpty(u.Extra))
            {
                sb.AppendLine(u.Extra);
            }
        }
        else
        {
            sb.AppendLine("无详细属性（表中无名 / 非系统可读单位）");
            sb.AppendLine("一级页仅保证血蓝条。");
        }

        SetText(tx, sb.ToString(), 14);
    }

    private static string FormatSuperAiStatus()
    {
        if (!_superAiActive)
        {
            return "超级AI: 关闭\n仅常规/九动可开。开启后强制走普通 Auto（关闭 VIP 自动技开关，退出时还原）。\n"
                   + "模拟阶段：进战斗采信息并写日志，不真正改出手。\n"
                   + SuperAiVipTypeHint;
        }

        return "超级AI: 运行中（模拟）\n模式: " + ModeLabel(_battleMode)
               + "\nVIP自动技: 已强制关（退出还原）\n"
               + (_superAiLastSimLine.Length > 0 ? _superAiLastSimLine : "等待进入战斗…")
               + "\n" + SuperAiVipTypeHint;
    }

    private static void ToggleSuperAi()
    {
        if (_superAiActive)
        {
            StopSuperAi("已手动停止");
        }
        else
        {
            StartSuperAi();
        }

        if (_tab == TabSuperAi)
        {
            ClearBody();
            BuildSuperAiBody();
            RefreshTabButtonLabels();
        }
    }

    private static void StartSuperAi()
    {
        if (!IsSuperAiModeAllowed(_battleMode))
        {
            Tip("超级AI：请先到「战斗」页选常规或九动");
            return;
        }

        if (!ForceVipAutoSkillOff(true))
        {
            Tip("超级AI：关闭 VIP 自动技失败，见日志");
            return;
        }

        _superAiActive = true;
        _superAiLastDumpKey = "";
        _superAiLastSimLine = "已启动，等待战斗回合…";
        try
        {
            BossStatEstimator.EnsureLoaded();
            WriteLog("BossStatEstimator rows=" + BossStatEstimator.TableCount
                     + " from=" + (BossStatEstimator.LoadedFrom ?? "?")
                     + " err=" + (BossStatEstimator.LoadError ?? ""));
        }
        catch (Exception ex)
        {
            WriteLog("BossStatEstimator load EX: " + RootMessage(ex));
        }

        Tip("超级AI：已启动（模拟，不改出手）");
        WriteLog("SuperAI start mode=" + _battleMode);
    }

    private static void StopSuperAi(string reason)
    {
        if (!_superAiActive && !_superAiVipBackupValid)
        {
            return;
        }

        _superAiActive = false;
        ForceVipAutoSkillOff(false);
        _superAiLastSimLine = reason ?? "";
        _superAiUiPage = 0;
        _superAiDetailIndex = -1;
        _superAiUnits.Clear();
        WriteLog("SuperAI stop: " + reason);
        Tip("超级AI：" + reason);
    }

    /// <summary>
    /// VIP 月卡路径：MonthCardOpen && GetAutoSkillSwitch==1 → DoVip*；否则 AutoFight_PlayerAction。
    /// 直接改本地 m_AutoState（不置 needSendData，避免改服务器 VIP 配置）。
    /// </summary>
    private static bool ForceVipAutoSkillOff(bool turnOff)
    {
        try
        {
            var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            if (string.IsNullOrEmpty(uid))
            {
                uid = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount") ?? "") ?? "";
            }

            if (string.IsNullOrEmpty(uid))
            {
                WriteLog("SuperAI VIP switch: empty uid");
                return !turnOff;
            }

            int[] state;
            if (!TryGetVipAutoStateArray(uid, true, out state))
            {
                WriteLog("SuperAI VIP switch: no m_AutoState");
                return false;
            }

            if (turnOff)
            {
                _superAiVipPlayerSwitch = state.Length > 0 ? state[0] : 0;
                _superAiVipPetSwitch = state.Length > 1 ? state[1] : 0;
                _superAiVipBackupValid = true;
                if (state.Length > 0)
                {
                    state[0] = 0;
                }

                if (state.Length > 1)
                {
                    state[1] = 0;
                }

                WriteLog("SuperAI VIP off(local) backup p=" + _superAiVipPlayerSwitch + " pet=" + _superAiVipPetSwitch);
            }
            else if (_superAiVipBackupValid)
            {
                if (state.Length > 0)
                {
                    state[0] = _superAiVipPlayerSwitch;
                }

                if (state.Length > 1)
                {
                    state[1] = _superAiVipPetSwitch;
                }

                WriteLog("SuperAI VIP restore(local) p=" + _superAiVipPlayerSwitch + " pet=" + _superAiVipPetSwitch);
                _superAiVipBackupValid = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteLog("ForceVipAutoSkillOff EX: " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryGetVipAutoStateArray(string uid, bool createIfMissing, out int[] state)
    {
        state = null;
        var mgr = GetManagerInstance("BattleAutoSkillManager");
        if (mgr == null)
        {
            return false;
        }

        var f = mgr.GetType().GetField("m_AutoState",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var dict = f?.GetValue(mgr) as IDictionary;
        if (dict == null)
        {
            return false;
        }

        if (!dict.Contains(uid))
        {
            if (!createIfMissing)
            {
                return false;
            }

            dict.Add(uid, new int[2]);
        }

        state = dict[uid] as int[];
        return state != null && state.Length >= 2;
    }

    private static void TickSuperAi()
    {
        if (!_superAiActive)
        {
            return;
        }

        if (!IsSuperAiModeAllowed(_battleMode))
        {
            StopSuperAi("战斗模式非常规/九动，已关闭超级AI");
            return;
        }

        // 防止战斗中 VIP 开关被服务端刷新回来
        try
        {
            var uid = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount")
                                      ?? GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            int[] state;
            if (!string.IsNullOrEmpty(uid) && TryGetVipAutoStateArray(uid, true, out state))
            {
                if (state[0] != 0)
                {
                    state[0] = 0;
                }

                if (state[1] != 0)
                {
                    state[1] = 0;
                }
            }
        }
        catch
        {
            // ignore keep-alive
        }

        var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        if (!inBattle)
        {
            _superAiLastDumpKey = "";
            return;
        }

        var now = NowMs();
        if (_superAiLastDumpMs > 0 && now - _superAiLastDumpMs < SuperAiDumpMinIntervalMs)
        {
            return;
        }

        string dump;
        string key;
        if (!TryBuildSuperAiBattlefieldDump(out dump, out key))
        {
            return;
        }

        if (key == _superAiLastDumpKey)
        {
            return;
        }

        _superAiLastDumpKey = key;
        _superAiLastDumpMs = now;
        var sim = SimulateSuperAiDecision(dump);
        _superAiLastSimLine = sim;
        WriteLog("===== SuperAI SNAPSHOT key=" + key + " =====");
        WriteLog(dump);
        WriteLog("===== SuperAI SIM: " + sim + " =====");
        if (_tab == TabSuperAi && _visible && !_minimized)
        {
            RefreshSuperAiBattlefieldUi(true);
        }
    }

    /// <summary>模拟决策占位：不发包、不改 Auto 配置。</summary>
    private static string SimulateSuperAiDecision(string dump)
    {
        // 后续按你给的规则填；现阶段只声明「已看到快照」
        var hasPray = dump.IndexOf("fieldPray=", StringComparison.Ordinal) >= 0
                      && dump.IndexOf("fieldPray=none", StringComparison.Ordinal) < 0
                      && dump.IndexOf("fieldPray=?", StringComparison.Ordinal) < 0;
        var hasRcv = dump.IndexOf("RCV_UP", StringComparison.Ordinal) >= 0;
        return "SIM(不执行) 见快照; 场上属性祈祷=" + (hasPray ? "有" : "无")
               + "; 我方RCV_UP=" + (hasRcv ? "有" : "无")
               + "; 策略=待定";
    }

    private static bool TryBuildSuperAiBattlefieldDump(out string dump, out string key)
    {
        dump = "";
        key = "";
        try
        {
            var sb = new StringBuilder(4096);
            var uid = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount")
                                      ?? GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var battleIndex = Convert.ToInt32(GetStaticMember("BattleDataHolder", "BattleIndex") ?? -1);
            var playerMp = Convert.ToInt32(GetStaticMember("BattleDataHolder", "PlayerMp") ?? 0);
            var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
            var acountN = 0;
            try
            {
                var list = GetStaticMember("BattleDataHolder", "AcountList") as ICollection;
                acountN = list != null ? list.Count : 0;
            }
            catch
            {
                // ignore
            }

            var fieldPray = ReadBattleFieldPrayFlags();
            var melee = CheckEquippedMelee(uid);
            string selfName;
            string selfJob;
            ReadSuperAiSelfIdentity(uid, out selfName, out selfJob);
            sb.AppendLine("uid=" + uid + " name=" + selfName + " job=" + selfJob
                          + " battleIndex=" + battleIndex + " playerIdx=" + playerIdx
                          + " PlayerMp=" + playerMp);
            sb.AppendLine("AcountList=" + acountN
                          + " weaponMelee=" + melee.Melee + " weapon=" + melee.WeaponDesc);
            sb.AppendLine("fieldPray=" + fieldPray + "  // VIP type11: 无地水火风场才可放属性祈祷类");
            AppendSuperAiBattleMeta(sb, uid);
            AppendSuperAiBagItems(sb, uid);
            AppendSuperAiOwnStats(sb, uid);
            AppendSuperAiSkills(sb, uid);
            AppendSuperAiPetSkills(sb, uid);
            var unitsKey = AppendSuperAiBattleUnits(sb, playerIdx);

            dump = sb.ToString();
            key = battleIndex + "|" + playerMp + "|" + fieldPray + "|" + unitsKey;
            return dump.Length > 0;
        }
        catch (Exception ex)
        {
            WriteLog("TryBuildSuperAiBattlefieldDump EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>普通 Auto 配置 / BP 武器旗 / VIP 集火等（探索结论补全）。</summary>
    private static void AppendSuperAiBattleMeta(StringBuilder sb, string uid)
    {
        try
        {
            var bm = GetManagerInstance("BattleManager");
            var isAuto = bm != null && Convert.ToBoolean(GetMember(bm, "IsAutoBattle") ?? false);
            var bp = Convert.ToInt32(GetStaticMember("BattleDataHolder", "BPFlag") ?? 0);
            // WEAPON_DIRECT=0x80 BOW=0x100 BOOMERANG=0x200 KNIFE=0x400
            var wparts = new List<string>();
            if ((bp & 0x80) != 0) wparts.Add("DIRECT");
            if ((bp & 0x100) != 0) wparts.Add("BOW");
            if ((bp & 0x200) != 0) wparts.Add("BOOMERANG");
            if ((bp & 0x400) != 0) wparts.Add("KNIFE");
            sb.AppendLine("IsAutoBattle=" + isAuto + " BPFlag=0x" + bp.ToString("X")
                          + " bpWeapon=" + (wparts.Count > 0 ? string.Join("+", wparts.ToArray()) : "none"));

            // 普通 Auto：Config[0]=人物1动 Config[1]=人物2动；Type 0/1攻 2守 3技
            if (bm != null)
            {
                var configs = GetMember(bm, "PlayerAutoConfigs") as IDictionary;
                if (configs != null && configs.Contains(uid))
                {
                    var auto = configs[uid];
                    var cfgList = GetMember(auto, "Config") as IList;
                    if (cfgList != null)
                    {
                        for (var i = 0; i < cfgList.Count && i < 2; i++)
                        {
                            var c = cfgList[i];
                            if (c == null)
                            {
                                continue;
                            }

                            var typ = Convert.ToInt32(GetMember(c, "Type") ?? -1);
                            var typName = typ == 2 ? "守" : (typ == 3 ? "技" : "攻");
                            sb.AppendLine("normalAuto Config[" + i + "] type=" + typ + "(" + typName + ")"
                                          + " skill=" + GetMember(c, "Skillindex")
                                          + " tech=" + GetMember(c, "Techindex"));
                        }
                    }
                }
                else
                {
                    sb.AppendLine("normalAuto Config=(missing)");
                }
            }

            var asm = GetManagerInstance("BattleAutoSkillManager");
            if (asm != null)
            {
                var focus = Convert.ToInt32(GetMember(asm, "focusFireIndex") ?? -1);
                var guardN = 0;
                try
                {
                    var gd = GetMember(asm, "needGuardDict") as IDictionary;
                    guardN = gd != null ? gd.Count : 0;
                }
                catch
                {
                    // ignore
                }

                sb.AppendLine("vipMeta focusFireIndex=" + focus + " needGuardDict=" + guardN
                              + " (超级AI已强制 VIP 开关=0，仅作参考)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("battleMeta err=" + RootMessage(ex));
        }
    }

    private static void AppendSuperAiPetSkills(StringBuilder sb, string uid)
    {
        sb.AppendLine("petSkills:");
        try
        {
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            var battlePetId = player != null ? Convert.ToInt32(GetMember(player, "battlePetID") ?? -1) : -1;
            if (battlePetId < 0)
            {
                sb.AppendLine("  (no battle pet)");
                return;
            }

            var getPets = FindType("PlayerDataHolder")?.GetMethod(
                "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var pets = getPets?.Invoke(null, new object[] { uid }) as IList;
            if (pets == null || battlePetId >= pets.Count || pets[battlePetId] == null)
            {
                sb.AppendLine("  (pet missing)");
                return;
            }

            var pd = GetMember(pets[battlePetId], "data");
            var skills = GetMember(pd, "PetSkills") as IEnumerable;
            if (skills == null)
            {
                sb.AppendLine("  (no PetSkills)");
                return;
            }

            var n = 0;
            foreach (var tech in skills)
            {
                if (tech == null)
                {
                    continue;
                }

                var use = Convert.ToBoolean(GetMember(tech, "Use") ?? GetMember(tech, "use") ?? false);
                if (!use)
                {
                    continue;
                }

                var skillId = Convert.ToInt32(GetMember(tech, "SkillId") ?? GetMember(tech, "skillId") ?? 0);
                var techId = Convert.ToInt32(GetMember(tech, "TechId") ?? GetMember(tech, "techId") ?? 0);
                var lv = Convert.ToInt32(GetMember(tech, "Level") ?? GetMember(tech, "level") ?? 0);
                var fp = Convert.ToInt32(GetMember(tech, "Fp") ?? GetMember(tech, "fp") ?? 0);
                var tname = Convert.ToString(GetMember(tech, "Name") ?? GetMember(tech, "name") ?? "") ?? "";
                var memo = Convert.ToString(GetMember(tech, "Memo") ?? GetMember(tech, "memo") ?? "") ?? "";
                var autoType = ReadSkillAutoType(skillId);
                sb.Append("  ").Append(tname).Append(" skill=").Append(skillId)
                    .Append(" tech=").Append(techId).Append(" L").Append(lv)
                    .Append(" fp=").Append(fp).Append(" autoType=").Append(autoType);
                if (memo.Length > 0)
                {
                    sb.Append(" effect=").Append(TrimDiag(memo, 40));
                }

                sb.AppendLine();
                n++;
                if (n >= 30)
                {
                    sb.AppendLine("  ...(cap 30)");
                    break;
                }
            }

            if (n == 0)
            {
                sb.AppendLine("  (none usable)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  petSkills err=" + RootMessage(ex));
        }
    }

    private static string ReadBattleFieldPrayFlags()
    {
        try
        {
            // BattleProcesser.m_PropertyIndex ← Proto_SC_BattleChar.BCFIELDFLAG（属性祈祷场）
            object proc = null;
            try
            {
                var gmType = FindType("GameManagerHotfix");
                var mono = FindType("MonoSingleton`1");
                if (gmType != null && mono != null)
                {
                    var closed = mono.MakeGenericType(gmType);
                    var inst = closed.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                               ?.GetValue(null, null)
                               ?? closed.GetField("instance", BindingFlags.Public | BindingFlags.Static)
                                   ?.GetValue(null);
                    proc = GetMember(inst, "battleProcesser");
                }
            }
            catch
            {
                // ignore
            }

            if (proc == null)
            {
                return "?";
            }

            var f = proc.GetType().GetField("m_PropertyIndex",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null)
            {
                return "?";
            }

            var v = Convert.ToInt32(f.GetValue(proc) ?? 0);
            // BC_FIELD_FLAG: EARTH=1 WATER=2 FIRE=4 WIND=8 SILENCE=16 END=32
            if (v == 0)
            {
                return "none";
            }

            if (v == 32)
            {
                return "END";
            }

            var parts = new List<string>();
            if ((v & 1) != 0) parts.Add("地");
            if ((v & 2) != 0) parts.Add("水");
            if ((v & 4) != 0) parts.Add("火");
            if ((v & 8) != 0) parts.Add("风");
            if ((v & 16) != 0) parts.Add("沉默");
            return parts.Count > 0 ? string.Join("+", parts.ToArray()) + "(raw=" + v + ")" : ("raw=" + v);
        }
        catch
        {
            return "?";
        }
    }

    private struct SuperAiWeaponInfo
    {
        public bool Melee;
        public string WeaponDesc;
    }

    /// <summary>对齐 BattleRoleSelector.CheckMelee：槽 2/3，Type 4/5/6 为远程。</summary>
    private static SuperAiWeaponInfo CheckEquippedMelee(string uid)
    {
        var info = new SuperAiWeaponInfo { Melee = true, WeaponDesc = "无" };
        try
        {
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var itemList = getItems?.Invoke(null, new object[] { uid }) as IList;
            if (itemList == null || itemList.Count <= 3)
            {
                return info;
            }

            var parts = new List<string>();
            for (var slot = 2; slot <= 3; slot++)
            {
                var it = itemList[slot];
                if (it == null)
                {
                    continue;
                }

                var useFlag = Convert.ToInt32(GetMember(it, "useFlag") ?? 0);
                if (useFlag != 1)
                {
                    continue;
                }

                var data = GetMember(it, "data");
                if (data == null)
                {
                    continue;
                }

                var type = Convert.ToInt32(GetMember(data, "Type") ?? 0);
                var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                parts.Add("slot" + slot + ":" + name + "(t=" + type + ")");
                if (type == 4 || type == 5 || type == 6)
                {
                    info.Melee = false;
                }
            }

            if (parts.Count > 0)
            {
                info.WeaponDesc = string.Join(";", parts.ToArray());
            }
        }
        catch (Exception ex)
        {
            info.WeaponDesc = "err:" + RootMessage(ex);
        }

        return info;
    }

    private static void AppendSuperAiBagItems(StringBuilder sb, string uid)
    {
        sb.Append("bagHpPotions:");
        try
        {
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var itemList = getItems?.Invoke(null, new object[] { uid }) as IEnumerable;
            var n = 0;
            if (itemList != null)
            {
                foreach (var it in itemList)
                {
                    if (it == null || Convert.ToInt32(GetMember(it, "useFlag") ?? 0) != 1)
                    {
                        continue;
                    }

                    var data = GetMember(it, "data");
                    if (data == null)
                    {
                        continue;
                    }

                    var type = Convert.ToInt32(GetMember(data, "Type") ?? 0);
                    var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                    // 43 常见血瓶类；名称兜底
                    var isHp = type == 43 || type == 23
                               || name.IndexOf("血", StringComparison.Ordinal) >= 0
                               || name.IndexOf("生命", StringComparison.Ordinal) >= 0;
                    if (!isHp)
                    {
                        continue;
                    }

                    var pile = Convert.ToInt32(GetMember(data, "Pile") ?? GetMember(it, "pile") ?? 1);
                    sb.Append(" [").Append(name).Append(" t=").Append(type).Append(" x").Append(pile).Append("]");
                    n++;
                    if (n >= 12)
                    {
                        break;
                    }
                }
            }

            if (n == 0)
            {
                sb.Append(" (无)");
            }
        }
        catch (Exception ex)
        {
            sb.Append(" err=").Append(RootMessage(ex));
        }

        sb.AppendLine();
    }

    private static void ReadSuperAiSelfIdentity(string uid, out string name, out string job)
    {
        name = "?";
        job = "?";
        try
        {
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            if (player == null)
            {
                return;
            }

            name = Convert.ToString(GetMember(player, "name") ?? "") ?? "?";
            var jobName = Convert.ToString(GetMember(player, "JobName") ?? "") ?? "";
            var ancestry = Convert.ToString(GetMember(player, "JobAncestryName") ?? "") ?? "";
            var jobId = Convert.ToInt32(GetMember(player, "Job") ?? -1);
            var ancestryId = Convert.ToInt32(GetMember(player, "JobAncestry") ?? -1);
            if (jobName.Length == 0 && ancestry.Length == 0)
            {
                job = "id=" + jobId + "/ancestry=" + ancestryId;
            }
            else if (ancestry.Length > 0 && ancestry != jobName)
            {
                job = ancestry + "/" + jobName + "(job=" + jobId + ",anc=" + ancestryId + ")";
            }
            else
            {
                job = (jobName.Length > 0 ? jobName : ancestry) + "(job=" + jobId + ",anc=" + ancestryId + ")";
            }
        }
        catch
        {
            // keep ?
        }
    }

    private static void AppendSuperAiOwnStats(StringBuilder sb, string uid)
    {
        try
        {
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            if (player != null)
            {
                var name = Convert.ToString(GetMember(player, "name") ?? "") ?? "";
                var jobName = Convert.ToString(GetMember(player, "JobName") ?? "") ?? "";
                var ancestry = Convert.ToString(GetMember(player, "JobAncestryName") ?? "") ?? "";
                sb.AppendLine("selfOutBattleStat name=" + name
                              + " job=" + jobName
                              + " jobAncestry=" + ancestry
                              + " jobId=" + GetMember(player, "Job")
                              + " ancestryId=" + GetMember(player, "JobAncestry")
                              + " hp=" + GetMember(player, "hp")
                              + "/" + GetMember(player, "maxHp")
                              + " mp=" + GetMember(player, "mp") + "/" + GetMember(player, "maxMp")
                              + " atk=" + GetMember(player, "AttackPower")
                              + " def=" + GetMember(player, "DefencePower")
                              + " agi=" + GetMember(player, "Agility")
                              + " rcv=" + GetMember(player, "Recovery"));
            }

            var battlePetId = player != null ? Convert.ToInt32(GetMember(player, "battlePetID") ?? -1) : -1;
            if (battlePetId >= 0)
            {
                var getPets = FindType("PlayerDataHolder")?.GetMethod(
                    "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                var pets = getPets?.Invoke(null, new object[] { uid }) as IList;
                if (pets != null && battlePetId < pets.Count && pets[battlePetId] != null)
                {
                    var pet = pets[battlePetId];
                    var pd = GetMember(pet, "data") ?? pet;
                    sb.AppendLine("petOutBattleStat name=" + GetMember(pd, "Name")
                                  + " hp=" + GetMember(pd, "Hp") + "/" + GetMember(pd, "MaxHp")
                                  + " mp=" + GetMember(pd, "Mp") + "/" + GetMember(pd, "MaxMp")
                                  + " atk=" + GetMember(pd, "AttackPower")
                                  + " def=" + GetMember(pd, "DefencePower")
                                  + " agi=" + GetMember(pd, "Agility")
                                  + " rcv=" + GetMember(pd, "Recovery"));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ownStats err=" + RootMessage(ex));
        }
    }

    private static void AppendSuperAiSkills(StringBuilder sb, string uid)
    {
        sb.AppendLine("skills(usable, !forget):");
        try
        {
            var getMag = FindType("PlayerDataHolder")?.GetMethod(
                "GetMagicDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var magics = getMag?.Invoke(null, new object[] { uid }) as IEnumerable;
            if (magics == null)
            {
                sb.AppendLine("  (none)");
                return;
            }

            var n = 0;
            foreach (var magic in magics)
            {
                if (magic == null || Convert.ToInt32(GetMember(magic, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var forget = Convert.ToBoolean(GetMember(magic, "forgetInBatlle") ?? false);
                var cd = Convert.ToBoolean(GetMember(magic, "isCD") ?? false);
                var skillId = Convert.ToInt32(GetMember(magic, "skillId") ?? 0);
                var name = Convert.ToString(GetMember(magic, "name") ?? GetMember(magic, "Name") ?? "") ?? "";
                var autoType = ReadSkillAutoType(skillId);
                sb.Append("  id=").Append(skillId).Append(" ").Append(name)
                    .Append(" forget=").Append(forget).Append(" cd=").Append(cd)
                    .Append(" autoType=").Append(autoType);
                var techs = GetMember(magic, "techs") as IList;
                if (techs != null)
                {
                    sb.Append(" techs=[");
                    for (var i = 0; i < techs.Count; i++)
                    {
                        var tech = techs[i];
                        if (tech == null)
                        {
                            continue;
                        }

                        var use = Convert.ToBoolean(GetMember(tech, "Use") ?? GetMember(tech, "use") ?? false);
                        var flg = Convert.ToBoolean(GetMember(tech, "Flg") ?? GetMember(tech, "flg") ?? false);
                        var lv = Convert.ToInt32(GetMember(tech, "Level") ?? GetMember(tech, "level") ?? (i + 1));
                        var fp = Convert.ToInt32(GetMember(tech, "Fp") ?? GetMember(tech, "fp") ?? 0);
                        var memo = Convert.ToString(GetMember(tech, "Memo") ?? GetMember(tech, "memo") ?? "") ?? "";
                        var tname = Convert.ToString(GetMember(tech, "Name") ?? GetMember(tech, "name") ?? "") ?? "";
                        if (i > 0)
                        {
                            sb.Append("; ");
                        }

                        sb.Append("L").Append(lv).Append(":").Append(tname)
                            .Append(" fp=").Append(fp).Append(" use=").Append(use).Append(" flg=").Append(flg);
                        if (memo.Length > 0)
                        {
                            sb.Append(" effect=").Append(TrimDiag(memo, 40));
                        }
                    }

                    sb.Append("]");
                }

                sb.AppendLine();
                n++;
                if (n >= 40)
                {
                    sb.AppendLine("  ...(cap 40)");
                    break;
                }
            }

            if (n == 0)
            {
                sb.AppendLine("  (none)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  skills err=" + RootMessage(ex));
        }
    }

    private static int ReadSkillAutoType(int skillId)
    {
        try
        {
            var cfgMgr = GetManagerInstance("ConfigManager");
            if (cfgMgr == null)
            {
                return -1;
            }

            var getTb = cfgMgr.GetType().GetMethod("GetTbSkillConfig",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var tb = getTb?.Invoke(cfgMgr, null);
            if (tb == null)
            {
                return -1;
            }

            var get = tb.GetType().GetMethod("Get", new[] { typeof(int) })
                      ?? tb.GetType().GetMethod("get_Item", new[] { typeof(int) });
            var row = get?.Invoke(tb, new object[] { skillId });
            if (row == null)
            {
                return -1;
            }

            return Convert.ToInt32(GetMember(row, "AutoSkillType") ?? -1);
        }
        catch
        {
            return -1;
        }
    }

    private static string AppendSuperAiBattleUnits(StringBuilder sb, int playerIdx)
    {
        var keyParts = new StringBuilder();
        sb.AppendLine("units:");
        _superAiUnits.Clear();
        try
        {
            var container = FindType("BattleRoleContainer");
            var dic = container?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                      ?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                sb.AppendLine("  (no BattleRoleDic)");
                _superAiUnitsKey = "0";
                return "0";
            }

            var allySide = playerIdx < 10;
            var selfUid = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount")
                                          ?? GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            string selfName;
            string selfJob;
            ReadSuperAiSelfIdentity(selfUid, out selfName, out selfJob);
            var enemyEstCount = 0;
            const int enemyEstMax = 10;
            foreach (DictionaryEntry kv in dic)
            {
                var role = kv.Value;
                if (role == null)
                {
                    continue;
                }

                var idx = Convert.ToInt32(GetMember(role, "Index") ?? kv.Key ?? -1);
                var roleData = GetMember(role, "RoleData");
                var ch = roleData != null ? GetMember(roleData, "Char") : null;
                if (ch == null)
                {
                    continue;
                }

                var name = Convert.ToString(GetMember(ch, "Name") ?? "") ?? "";
                var hp = Convert.ToInt32(GetMember(ch, "Hp") ?? 0);
                var maxHp = Convert.ToInt32(GetMember(ch, "MaxHp") ?? 0);
                var mp = Convert.ToInt32(GetMember(ch, "Mp") ?? 0);
                var maxMp = Convert.ToInt32(GetMember(ch, "MaxMp") ?? 0);
                var level = Convert.ToInt32(GetMember(ch, "Level") ?? 0);
                var animId = Convert.ToInt32(GetMember(ch, "AnimationId") ?? 0);
                var bc = Convert.ToInt64(GetMember(ch, "Bcflag") ?? 0);
                var status = FormatBcStatus(bc);
                var isPlayer = (bc & 4L) != 0; // BC_FLAG.PLAYER
                var side = idx < 10 ? "L" : "R";
                var mine = (allySide && idx < 10) || (!allySide && idx >= 10);
                var beUsed = Convert.ToString(GetMember(role, "beUsedSkill") ?? "") ?? "";
                var act2 = (bc & 0x400L) != 0 ? "2ACT" : "1ACT";
                var isSelf = idx == playerIdx && isPlayer;

                var snap = new SuperAiUnitSnap();
                snap.Idx = idx;
                snap.Mine = mine;
                snap.IsPlayer = isPlayer;
                snap.Name = name;
                snap.Level = level;
                snap.Hp = hp;
                snap.MaxHp = maxHp;
                snap.Mp = mp;
                snap.MaxMp = maxMp;
                snap.DetailOk = false;
                snap.Extra = "";

                sb.Append("  [").Append(side).Append(idx).Append(mine ? "*" : "")
                    .Append("] name=").Append(name);
                if (isSelf)
                {
                    sb.Append(" job=").Append(selfJob);
                    snap.Extra = "job=" + selfJob;
                }
                else if (isPlayer)
                {
                    sb.Append(" job=?");
                }

                sb.Append(isPlayer ? " (P)" : " (pet/mon)")
                    .Append(" lv=").Append(level)
                    .Append(" anim=").Append(animId)
                    .Append(" hp=").Append(hp).Append("/").Append(maxHp)
                    .Append(" mp=").Append(mp).Append("/").Append(maxMp)
                    .Append(" ").Append(status)
                    .Append(" ").Append(act2)
                    .Append(" beUsed=").Append(beUsed);
                if ((bc & 0x100000L) != 0)
                {
                    sb.Append(" RCV_UP");
                }

                sb.AppendLine();

                // 我方：系统面板属性（血蓝已用战斗值）；失败则仅一览血蓝
                if (mine)
                {
                    try
                    {
                        TryFillAllySystemDetail(ref snap, isSelf, isPlayer, selfUid, idx);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // 敌方非玩家：最多估10；查不到表静默
                if (!mine && !isPlayer && enemyEstCount < enemyEstMax
                    && maxHp > 0 && level > 0 && !string.IsNullOrEmpty(name))
                {
                    enemyEstCount++;
                    try
                    {
                        var est = BossStatEstimator.EstimateBest(name, animId, 0, level, maxHp, maxMp);
                        if (est.Ok)
                        {
                            snap.DetailOk = true;
                            snap.Rate = est.Rate;
                            snap.Atk = est.Atk;
                            snap.Def = est.Def;
                            snap.Agi = est.Agi;
                            snap.Spirit = est.Spirit;
                            snap.Rec = est.Rec;
                            snap.Extra = "drops=" + est.DropVit + "/" + est.DropStr + "/" + est.DropTgh
                                         + "/" + est.DropQuick + "/" + est.DropMagic + " pen=" + est.MatchPen;
                            var line = BossStatEstimator.FormatOneLine(est);
                            if (!string.IsNullOrEmpty(line))
                            {
                                sb.Append("    ").AppendLine(line);
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                _superAiUnits.Add(snap);
                keyParts.Append(idx).Append(':').Append(hp).Append('/').Append(mp).Append('/').Append(bc)
                    .Append(';');
            }

            // 敌方优先，再按站位 index
            _superAiUnits.Sort((a, b) =>
            {
                if (a.Mine != b.Mine)
                {
                    return a.Mine ? 1 : -1;
                }

                return a.Idx.CompareTo(b.Idx);
            });
        }
        catch (Exception ex)
        {
            sb.AppendLine("  units err=" + RootMessage(ex));
        }

        _superAiUnitsKey = keyParts.ToString();
        return _superAiUnitsKey;
    }

    /// <summary>5开我方：血蓝用战斗，攻防敏回复从 PlayerData/宠物面板读；读不到就算了。</summary>
    private static void TryFillAllySystemDetail(ref SuperAiUnitSnap snap, bool isSelf, bool isPlayer, string selfUid, int idx)
    {
        if (isPlayer)
        {
            string uid = null;
            if (isSelf)
            {
                uid = selfUid;
            }
            else
            {
                uid = FindUidByBattleIndex(idx);
            }

            if (string.IsNullOrEmpty(uid))
            {
                return;
            }

            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            if (player == null)
            {
                return;
            }

            snap.Atk = Convert.ToInt32(GetMember(player, "AttackPower") ?? 0);
            snap.Def = Convert.ToInt32(GetMember(player, "DefencePower") ?? 0);
            snap.Agi = Convert.ToInt32(GetMember(player, "Agility") ?? 0);
            snap.Spirit = Convert.ToInt32(GetMember(player, "Spirit") ?? GetMember(player, "Mental") ?? 0);
            snap.Rec = Convert.ToInt32(GetMember(player, "Recovery") ?? 0);
            snap.DetailOk = snap.Atk > 0 || snap.Def > 0 || snap.Agi > 0;
            return;
        }

        // 己方宠：仅对照当前账号出战宠名字（避免扫全队过重）
        try
        {
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { selfUid });
            if (player == null)
            {
                return;
            }

            var battlePetId = Convert.ToInt32(GetMember(player, "battlePetID") ?? -1);
            if (battlePetId < 0)
            {
                return;
            }

            var getPets = FindType("PlayerDataHolder")?.GetMethod(
                "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var pets = getPets?.Invoke(null, new object[] { selfUid }) as IList;
            if (pets == null || battlePetId >= pets.Count || pets[battlePetId] == null)
            {
                return;
            }

            // 仅当名字对得上当前出战宠，避免错挂到别人宠
            var pd = GetMember(pets[battlePetId], "data") ?? pets[battlePetId];
            var pname = Convert.ToString(GetMember(pd, "Name") ?? "") ?? "";
            if (!string.IsNullOrEmpty(pname) && pname != snap.Name)
            {
                return;
            }

            snap.Atk = Convert.ToInt32(GetMember(pd, "AttackPower") ?? 0);
            snap.Def = Convert.ToInt32(GetMember(pd, "DefencePower") ?? 0);
            snap.Agi = Convert.ToInt32(GetMember(pd, "Agility") ?? 0);
            snap.Spirit = Convert.ToInt32(GetMember(pd, "Spirit") ?? GetMember(pd, "Mental") ?? 0);
            snap.Rec = Convert.ToInt32(GetMember(pd, "Recovery") ?? 0);
            snap.DetailOk = snap.Atk > 0 || snap.Def > 0;
            snap.Extra = "出战宠面板";
        }
        catch
        {
            // ignore
        }
    }

    private static string FindUidByBattleIndex(int idx)
    {
        try
        {
            var dic = FindType("BattleRoleContainer")
                ?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                return null;
            }

            foreach (DictionaryEntry e in dic)
            {
                if (Convert.ToInt32(e.Value) == idx)
                {
                    return Convert.ToString(e.Key);
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string FormatBcStatus(long bc)
    {
        // ABNORMAL_POISON=0x10 SLEEP=0x20 STONE=0x40 INEBRIETY=0x80 CONFUSION=0x100 FORGET=0x200 DEATH=2
        if ((bc & 2L) != 0)
        {
            return "死亡";
        }

        var parts = new List<string>();
        if ((bc & 0x10L) != 0) parts.Add("中毒");
        if ((bc & 0x20L) != 0) parts.Add("睡眠");
        if ((bc & 0x40L) != 0) parts.Add("石化");
        if ((bc & 0x80L) != 0) parts.Add("酒醉");
        if ((bc & 0x100L) != 0) parts.Add("混乱");
        if ((bc & 0x200L) != 0) parts.Add("遗忘");
        return parts.Count == 0 ? "正常" : string.Join("+", parts.ToArray());
    }

    private static void BuildScriptBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        var hint = CreateUiChild(_bodyRoot, "Hint", rtType);
        SetAnchoredTop(RequireRect(hint, "hs"), 0f, -4f, 500f, 56f);
        SetText(
            AddText(hint),
            "简单脚本：点按钮运行。\n礼包码读 hotfixdata/seqchapter_gift_codes.txt（最多5角色）。\n采集物满格（999）才提，默认提入账号银行。\n一键加点：人物按推荐第一方案，宠物先加力量到极限。\n一键上架：只上架默认定价表单内装备（读 seqchapter_auto_sell_prices.txt）。\n刷熊男：等杀熊者→丢欧兹那克→走17,15→穿身触发战斗→循环。\n一键命名：5角色背包1级宠按捉宠规则改名（#档/#满/@随机）。",
            12);
        var btn = CreateUiChild(_bodyRoot, "Daily", rtType);
        SetAnchoredTop(RequireRect(btn, "db"), 0f, -68f, 200f, 40f);
        var img = AddComp(btn, "UnityEngine.UI.Image");
        SetColor(img, 0.2f, 0.4f, 0.55f, 1f);
        var lab = CreateUiChild(btn, "L", rtType);
        StretchFull(RequireRect(lab, "dl"));
        SetText(AddText(lab), "做日常（开/停）", 15);
        BindButton(btn, img, RunDailyClaim);

        var gift = CreateUiChild(_bodyRoot, "Gift", rtType);
        SetAnchoredTop(RequireRect(gift, "gb"), 0f, -118f, 220f, 40f);
        var gImg = AddComp(gift, "UnityEngine.UI.Image");
        SetColor(gImg, 0.45f, 0.32f, 0.18f, 1f);
        var gLab = CreateUiChild(gift, "L", rtType);
        StretchFull(RequireRect(gLab, "gl"));
        SetText(AddText(gLab), "礼包码（开/停）", 15);
        BindButton(gift, gImg, RunGiftClaim);

        var extractNow = CreateUiChild(_bodyRoot, "AreaExtractNow", rtType);
        SetAnchoredTop(RequireRect(extractNow, "aen"), 0f, -168f, 240f, 40f);
        var enImg = AddComp(extractNow, "UnityEngine.UI.Image");
        SetColor(enImg, 0.18f, 0.42f, 0.38f, 1f);
        var enLab = CreateUiChild(extractNow, "L", rtType);
        StretchFull(RequireRect(enLab, "enl"));
        SetText(AddText(enLab), "立刻提取采集物", 15);
        BindButton(extractNow, enImg, RunAreaExtractNow);

        var autoPoint = CreateUiChild(_bodyRoot, "AutoPoint", rtType);
        SetAnchoredTop(RequireRect(autoPoint, "apb"), 0f, -218f, 240f, 40f);
        var apImg = AddComp(autoPoint, "UnityEngine.UI.Image");
        SetColor(apImg, 0.55f, 0.35f, 0.65f, 1f);
        var apLab = CreateUiChild(autoPoint, "L", rtType);
        StretchFull(RequireRect(apLab, "apl"));
        SetText(AddText(apLab), "一键加点（人物方案+宠物力量）", 13);
        BindButton(autoPoint, apImg, RunAutoPoint);

        var autoStall = CreateUiChild(_bodyRoot, "AutoStall", rtType);
        SetAnchoredTop(RequireRect(autoStall, "asb"), 0f, -268f, 240f, 40f);
        var asImg = AddComp(autoStall, "UnityEngine.UI.Image");
        SetColor(asImg, 0.35f, 0.55f, 0.35f, 1f);
        var asLab = CreateUiChild(autoStall, "L", rtType);
        StretchFull(RequireRect(asLab, "asl"));
        SetText(AddText(asLab), "一键上架（开/停）", 15);
        BindButton(autoStall, asImg, RunAutoStall);

        var bearSlayer = CreateUiChild(_bodyRoot, "BearSlayer", rtType);
        SetAnchoredTop(RequireRect(bearSlayer, "bsb"), 0f, -318f, 240f, 40f);
        var bsImg = AddComp(bearSlayer, "UnityEngine.UI.Image");
        SetColor(bsImg, 0.55f, 0.35f, 0.25f, 1f);
        var bsLab = CreateUiChild(bearSlayer, "L", rtType);
        StretchFull(RequireRect(bsLab, "bsl"));
        SetText(AddText(bsLab), "刷熊男（开/停）", 15);
        BindButton(bearSlayer, bsImg, RunBearSlayer);

        var petNamer = CreateUiChild(_bodyRoot, "PetNamer", rtType);
        SetAnchoredTop(RequireRect(petNamer, "pnb"), -130f, -368f, 220f, 40f);
        var pnImg = AddComp(petNamer, "UnityEngine.UI.Image");
        SetColor(pnImg, 0.20f, 0.45f, 0.60f, 1f);
        var pnLab = CreateUiChild(petNamer, "L", rtType);
        StretchFull(RequireRect(pnLab, "pnl"));
        SetText(AddText(pnLab), "一键命名（开/停）", 15);
        BindButton(petNamer, pnImg, RunPetNamer);

        var wingTest = CreateUiChild(_bodyRoot, "WingTest", rtType);
        SetAnchoredTop(RequireRect(wingTest, "wtb"), 130f, -368f, 220f, 40f);
        var wtImg = AddComp(wingTest, "UnityEngine.UI.Image");
        SetColor(wtImg, 0.62f, 0.28f, 0.18f, 1f);
        var wtLab = CreateUiChild(wingTest, "L", rtType);
        StretchFull(RequireRect(wtLab, "wtl"));
        SetText(AddText(wtLab), _scriptWingTestPending ? "赤凤之翼（点窗中）" : "测试赤凤之翼", 14);
        BindButton(wingTest, wtImg, RunScriptWingTest);

        var pnStatus = CreateUiChild(_bodyRoot, "PetNamerStatus", rtType);
        SetAnchoredTop(RequireRect(pnStatus, "pns"), 0f, -418f, 500f, 56f);
        _petNamerStatusText = AddText(pnStatus);
        SetText(_petNamerStatusText, FormatPetNamerStatus(), 12);

        // 「测试铃声」「刷灵堂」入口隐藏（逻辑保留，不在此页展示）
        _lingTangStatusText = null;
    }

    private static string FormatLingTangStatus()
    {
        if (!_lingTangActive)
        {
            return "刷灵堂: 未启动\n需在地图 1538 启动。战斗后继续导航；卡楼梯挪格，连续"
                   + LingTangMaxStuckFails + "次失败则停止。\n（丢弃未鉴定装备/古钱：本版不做）";
        }

        return "刷灵堂: 运行中\n步骤: " + LingTangPhaseName(_lingTangPhase)
               + "\n完成轮次: " + _lingTangCycles
               + "\n卡楼梯恢复: " + _lingTangStuckFails + "/" + LingTangMaxStuckFails
               + "\n" + FormatNavPosLine();
    }

    private static string LingTangPhaseName(int phase)
    {
        switch (phase)
        {
            case LingTangPhaseTo1515: return "1) 1538 (15,15)";
            case LingTangPhaseTo52026: return "2) 52026 (43,26)→52028";
            case LingTangPhaseTo52028a: return "3) 52028 (10,15)→(12,7)";
            case LingTangPhaseTo52028b: return "4) 52028 (10,4)→52027";
            case LingTangPhaseTo52027: return "5) 52027 (4,5)";
            case LingTangPhaseTalkNpc: return "6) 点NPC(5,5)→回1538";
            default: return "准备中";
        }
    }

    private static void ToggleLingTang()
    {
        if (_lingTangActive)
        {
            StopLingTang("已手动停止");
            if (_tab == TabScript)
            {
                ClearBody();
                BuildScriptBody();
                RefreshTabButtonLabels();
            }

            return;
        }

        StartLingTang();
        if (_tab == TabScript)
        {
            ClearBody();
            BuildScriptBody();
            RefreshTabButtonLabels();
        }
    }

    private static void StartLingTang()
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        if (floor != 1538)
        {
            Tip("刷灵堂：请先到地图 1538（当前 " + floor + "）");
            WriteLog("LingTang start reject floor=" + floor);
            return;
        }

        _lingTangActive = true;
        _lingTangPhase = LingTangPhaseTo1515;
        _lingTangStuckFails = 0;
        _lingTangStuckShuffles = 0;
        _lingTangStuckNavTriedFirst = false;
        _lingTangStuckPending = false;
        _lingTangLastNavMs = 0;
        _lingTangLastNpcMs = 0;
        _lingTangLastActivityMs = NowMs();
        TryGetPlayerXY(out _lingTangLastX, out _lingTangLastY);
        Tip("刷灵堂：已启动");
        WriteLog("LingTang start cycles=" + _lingTangCycles);
        LingTangIssueNav(1538, 15, 15, true);
    }

    private static void StopLingTang(string reason)
    {
        if (!_lingTangActive && _lingTangPhase == 0)
        {
            return;
        }

        _lingTangActive = false;
        _lingTangPhase = 0;
        _lingTangStuckPending = false;
        try
        {
            StopTaskNavigation();
        }
        catch
        {
            // ignore
        }

        WriteLog("LingTang stop: " + reason);
        Tip("刷灵堂：" + reason);
    }

    private static void TickLingTang()
    {
        if (!_lingTangActive)
        {
            return;
        }

        var now = NowMs();
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        TryGetPlayerXY(out var x, out var y);
        var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        var dialogueOpen = IsDialoguePanelOpen();

        if (dialogueOpen)
        {
            TryAutoPickDialogue();
            _lingTangLastActivityMs = now;
        }

        if (inBattle)
        {
            _lingTangLastActivityMs = now;
            _lingTangStuckPending = false;
            return;
        }

        if (x != _lingTangLastX || y != _lingTangLastY)
        {
            _lingTangLastX = x;
            _lingTangLastY = y;
            _lingTangLastActivityMs = now;
            if (!_lingTangStuckPending)
            {
                _lingTangStuckNavTriedFirst = false;
                _lingTangStuckShuffles = 0;
            }

            if (_lingTangStuckFails > 0 && !_lingTangStuckPending)
            {
                _lingTangStuckFails = 0;
            }
        }

        // 步骤完成判定 / 推进
        switch (_lingTangPhase)
        {
            case LingTangPhaseTo1515:
                if (floor == 1538 && x == 15 && y == 15)
                {
                    _lingTangPhase = LingTangPhaseTo52026;
                    _lingTangStuckFails = 0;
                    WriteLog("LingTang phase -> 52026");
                    LingTangIssueNav(52026, 43, 26, true);
                    return;
                }

                LingTangEnsureNav(1538, 15, 15, now);
                break;

            case LingTangPhaseTo52026:
                // 导航到 52026(43,26) 后传送进 52028
                if (floor == 52028)
                {
                    _lingTangPhase = LingTangPhaseTo52028a;
                    _lingTangStuckFails = 0;
                    WriteLog("LingTang phase -> 52028a (teleported)");
                    LingTangIssueNav(52028, 10, 15, true);
                    return;
                }

                LingTangEnsureNav(52026, 43, 26, now);
                break;

            case LingTangPhaseTo52028a:
                // 导航 52028(10,15) 后传送到 (12,7)
                if (floor == 52028 && x == 12 && y == 7)
                {
                    _lingTangPhase = LingTangPhaseTo52028b;
                    _lingTangStuckFails = 0;
                    WriteLog("LingTang phase -> 52028b");
                    LingTangIssueNav(52028, 10, 4, true);
                    return;
                }

                LingTangEnsureNav(52028, 10, 15, now);
                break;

            case LingTangPhaseTo52028b:
                if (floor == 52027)
                {
                    _lingTangPhase = LingTangPhaseTo52027;
                    _lingTangStuckFails = 0;
                    WriteLog("LingTang phase -> 52027");
                    LingTangIssueNav(52027, 4, 5, true);
                    return;
                }

                LingTangEnsureNav(52028, 10, 4, now);
                break;

            case LingTangPhaseTo52027:
                if (floor == 52027 && x == 4 && y == 5)
                {
                    _lingTangPhase = LingTangPhaseTalkNpc;
                    _lingTangStuckFails = 0;
                    _lingTangLastNpcMs = 0;
                    WriteLog("LingTang phase -> talk NPC");
                    return;
                }

                LingTangEnsureNav(52027, 4, 5, now);
                break;

            case LingTangPhaseTalkNpc:
                if (floor == 1538)
                {
                    _lingTangCycles++;
                    WriteLog("LingTang cycle done n=" + _lingTangCycles);
                    Tip("刷灵堂：完成第 " + _lingTangCycles + " 轮（丢弃本版跳过）");
                    _lingTangPhase = LingTangPhaseTo1515;
                    _lingTangStuckFails = 0;
                    LingTangIssueNav(1538, 15, 15, true);
                    return;
                }

                if (floor != 52027)
                {
                    // 异常地图，尝试回到流程
                    WriteLog("LingTang talk unexpected floor=" + floor);
                }

                // 靠近 (5,5) 并点 NPC；对话自动点
                if (!(x == 5 && y == 5) && Math.Abs(x - 5) + Math.Abs(y - 5) > 1)
                {
                    LingTangEnsureNav(52027, 5, 5, now);
                }
                else if (now - _lingTangLastNpcMs >= LingTangNpcRetryMs)
                {
                    _lingTangLastNpcMs = now;
                    if (TryLookNpcAt(5, 5))
                    {
                        _lingTangLastActivityMs = now;
                        WriteLog("LingTang LookNpc at 5,5");
                    }
                    else
                    {
                        // 找不到 NPC 时挪到旁边再试
                        LingTangEnsureNav(52027, 4, 5, now);
                    }
                }

                break;
        }

        if (dialogueOpen || inBattle)
        {
            return;
        }

        // 卡楼梯：非战斗静止 → 挪格再续航
        if (_lingTangStuckPending)
        {
            if (now - _lingTangStuckMoveAtMs >= StuckResumeDelayMs)
            {
                _lingTangStuckPending = false;
                _lingTangLastActivityMs = now;
                LingTangReissueCurrentNav(true);
            }

            return;
        }

        if (now - _lingTangLastActivityMs >= StuckIdleMs)
        {
            _lingTangStuckFails++;
            WriteLog("LingTang stuck fail=" + _lingTangStuckFails + "/" + LingTangMaxStuckFails
                     + " phase=" + _lingTangPhase + " floor=" + floor + " xy=" + x + "," + y);
            if (_lingTangStuckFails >= LingTangMaxStuckFails)
            {
                StopLingTang("卡楼梯恢复失败 " + LingTangMaxStuckFails + " 次，已停止");
                if (_visible && _tab == TabScript)
                {
                    try
                    {
                        ClearBody();
                        BuildScriptBody();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }

            if (!_lingTangStuckNavTriedFirst)
            {
                _lingTangStuckNavTriedFirst = true;
                _lingTangLastActivityMs = now;
                Tip("刷灵堂：卡图，先继续导航（观察5秒）");
                WriteLog("LingTang stuck nav-first phase=" + _lingTangPhase);
                LingTangReissueCurrentNav(true);
                return;
            }

            if (_lingTangStuckShuffles >= StuckShuffleBeforeNavRetry)
            {
                _lingTangStuckShuffles = 0;
                _lingTangLastActivityMs = now;
                Tip("刷灵堂：已挪 " + StuckShuffleBeforeNavRetry + " 次，改为继续导航（观察5秒）");
                WriteLog("LingTang stuck nav-observe phase=" + _lingTangPhase);
                LingTangReissueCurrentNav(true);
                return;
            }

            if (TryRandomStepOne())
            {
                _lingTangStuckShuffles++;
                _lingTangStuckMoveAtMs = now;
                _lingTangStuckPending = true;
                _lingTangLastActivityMs = now;
                Tip("刷灵堂：卡楼梯，挪格后续航（" + _lingTangStuckFails + "/" + LingTangMaxStuckFails
                    + " 挪" + _lingTangStuckShuffles + "/" + StuckShuffleBeforeNavRetry + "）");
            }
            else
            {
                _lingTangLastActivityMs = now;
                LingTangReissueCurrentNav(true);
            }
        }
    }

    private static void LingTangEnsureNav(int floor, int x, int y, long now)
    {
        if (_lingTangLastNavMs > 0 && now - _lingTangLastNavMs < LingTangNavRetryMs)
        {
            return;
        }

        LingTangIssueNav(floor, x, y, false);
    }

    private static void LingTangIssueNav(int floor, int x, int y, bool force)
    {
        var now = NowMs();
        if (!force && _lingTangLastNavMs > 0 && now - _lingTangLastNavMs < LingTangNavRetryMs)
        {
            return;
        }

        string how;
        if (TryNavigateTo(floor, x, y, out how))
        {
            _lingTangLastNavMs = now;
            WriteLog("LingTang nav " + floor + " (" + x + "," + y + ") " + how);
        }
        else
        {
            _lingTangLastNavMs = now;
            WriteLog("LingTang nav fail " + floor + " (" + x + "," + y + ") " + how);
        }
    }

    private static void LingTangReissueCurrentNav(bool force)
    {
        switch (_lingTangPhase)
        {
            case LingTangPhaseTo1515:
                LingTangIssueNav(1538, 15, 15, force);
                break;
            case LingTangPhaseTo52026:
                LingTangIssueNav(52026, 43, 26, force);
                break;
            case LingTangPhaseTo52028a:
                LingTangIssueNav(52028, 10, 15, force);
                break;
            case LingTangPhaseTo52028b:
                LingTangIssueNav(52028, 10, 4, force);
                break;
            case LingTangPhaseTo52027:
                LingTangIssueNav(52027, 4, 5, force);
                break;
            case LingTangPhaseTalkNpc:
                LingTangIssueNav(52027, 5, 5, force);
                break;
        }
    }

    /// <summary>查找 (x,y) 上的 NPC 并 SendLookNpc 触发对话。</summary>
    private static bool TryLookNpcAt(int nx, int ny)
    {
        try
        {
            var objindex = FindNpcObjIndexAt(nx, ny);
            if (objindex < 0)
            {
                WriteLog("TryLookNpcAt miss at " + nx + "," + ny);
                return false;
            }

            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return false;
            }

            var dir = 0;
            try
            {
                var pm = GetManagerInstance("PlayerManager");
                var entity = GetProp(pm, "playerEntity") ?? GetMember(pm, "playerEntity");
                dir = Convert.ToInt32(GetProp(entity, "direction") ?? GetMember(entity, "direction") ?? 0);
            }
            catch
            {
                // ignore
            }

            var look = npcMgr.GetType().GetMethod(
                "SendLookNpc",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (look == null)
            {
                WriteLog("SendLookNpc missing");
                return false;
            }

            look.Invoke(npcMgr, new object[] { dir, objindex });
            WriteLog("SendLookNpc obj=" + objindex + " dir=" + dir + " at " + nx + "," + ny);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryLookNpcAt EX: " + RootMessage(ex));
            return false;
        }
    }

    private static int FindNpcObjIndexAt(int nx, int ny)
    {
        try
        {
            var holder = FindType("EntityDataHolder");
            object dictObj = null;
            var prop = holder?.GetProperty(
                "characterDatas",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            dictObj = prop?.GetValue(null, null);
            if (dictObj == null)
            {
                dictObj = GetStaticMember("EntityDataHolder", "characterDatas");
            }

            var dict = dictObj as System.Collections.IDictionary;
            if (dict == null)
            {
                return -1;
            }

            var best = -1;
            var bestDist = int.MaxValue;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var cd = e.Value;
                if (cd == null || !IsLingTangNpc(cd))
                {
                    continue;
                }

                var ox = Convert.ToInt32(GetMember(cd, "x") ?? GetProp(cd, "x") ?? -999);
                var oy = Convert.ToInt32(GetMember(cd, "y") ?? GetProp(cd, "y") ?? -999);
                var dist = Math.Abs(ox - nx) + Math.Abs(oy - ny);
                if (dist > 1)
                {
                    continue;
                }

                var objindex = Convert.ToInt32(GetMember(cd, "objindex") ?? GetProp(cd, "objindex") ?? -1);
                if (objindex < 0)
                {
                    continue;
                }

                if (dist < bestDist || (dist == bestDist && (ox == nx && oy == ny)))
                {
                    bestDist = dist;
                    best = objindex;
                    if (dist == 0)
                    {
                        return objindex;
                    }
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            WriteLog("FindNpcObjIndexAt EX: " + RootMessage(ex));
            return -1;
        }
    }

    private static bool IsLingTangNpc(object cd)
    {
        try
        {
            var typeVal = Convert.ToInt32(GetMember(cd, "charEntityType") ?? GetProp(cd, "charEntityType") ?? 0);
            if (typeVal == 0)
            {
                return false;
            }

            // 排除玩家/敌人/宠物/摊位
            if (typeVal == 1 || typeVal == 2 || typeVal == 3
                || typeVal == 997 || typeVal == 998 || typeVal == 999)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void BuildNavBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        LoadNavWaypointsFromDisk();

        var posGo = CreateUiChild(_bodyRoot, "NavPos", rtType);
        SetAnchoredTop(RequireRect(posGo, "np"), 0f, -4f, 500f, 40f);
        _navPosText = AddText(posGo);
        try
        {
            SetProp(_navPosText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(_navPosText, FormatNavPosLine(), 13);

        var y = -48f;
        _navFloorInput = CreateInputField(_bodyRoot, rtType, "NavFloor", -155f, y, 100f, 30f, _navFloorStr, "地图号");
        _navXInput = CreateInputField(_bodyRoot, rtType, "NavX", -20f, y, 80f, 30f, _navXStr, "X");
        _navYInput = CreateInputField(_bodyRoot, rtType, "NavY", 100f, y, 80f, 30f, _navYStr, "Y");

        y -= 40f;
        var fillBtn = CreateUiChild(_bodyRoot, "NavFill", rtType);
        SetAnchoredTop(RequireRect(fillBtn, "nf"), -150f, y, 120f, 34f);
        var fillImg = AddComp(fillBtn, "UnityEngine.UI.Image");
        SetColor(fillImg, 0.22f, 0.38f, 0.5f, 1f);
        var fillLab = CreateUiChild(fillBtn, "L", rtType);
        StretchFull(RequireRect(fillLab, "nfl"));
        SetText(AddText(fillLab), "填入当前位置", 13);
        BindButton(fillBtn, fillImg, NavFillCurrent);

        var goBtn = CreateUiChild(_bodyRoot, "NavGo", rtType);
        SetAnchoredTop(RequireRect(goBtn, "ng"), 0f, y, 120f, 34f);
        var goImg = AddComp(goBtn, "UnityEngine.UI.Image");
        SetColor(goImg, 0.2f, 0.48f, 0.28f, 1f);
        var goLab = CreateUiChild(goBtn, "L", rtType);
        StretchFull(RequireRect(goLab, "ngl"));
        SetText(AddText(goLab), "导航", 14);
        BindButton(goBtn, goImg, NavGoFromInputs);

        var stopBtn = CreateUiChild(_bodyRoot, "NavStop", rtType);
        SetAnchoredTop(RequireRect(stopBtn, "ns"), 150f, y, 120f, 34f);
        var stopImg = AddComp(stopBtn, "UnityEngine.UI.Image");
        SetColor(stopImg, 0.45f, 0.22f, 0.22f, 1f);
        var stopLab = CreateUiChild(stopBtn, "L", rtType);
        StretchFull(RequireRect(stopLab, "nsl"));
        SetText(AddText(stopLab), "停止", 14);
        BindButton(stopBtn, stopImg, NavStop);

        y -= 42f;
        _navNameInput = CreateInputField(_bodyRoot, rtType, "NavName", -70f, y, 220f, 30f, _navNameStr, "点位名称(可选)");
        var saveBtn = CreateUiChild(_bodyRoot, "NavSave", rtType);
        SetAnchoredTop(RequireRect(saveBtn, "nsv"), 140f, y, 140f, 30f);
        var saveImg = AddComp(saveBtn, "UnityEngine.UI.Image");
        SetColor(saveImg, 0.4f, 0.32f, 0.18f, 1f);
        var saveLab = CreateUiChild(saveBtn, "L", rtType);
        StretchFull(RequireRect(saveLab, "nsvl"));
        SetText(AddText(saveLab), "记录当前点位", 13);
        BindButton(saveBtn, saveImg, NavSaveCurrentWaypoint);

        y -= 38f;
        var listHint = CreateUiChild(_bodyRoot, "WpHint", rtType);
        SetAnchoredTop(RequireRect(listHint, "wh"), 0f, y, 500f, 22f);
        SetText(AddText(listHint), "已存点位（与序章助手共用 waypoints.json）共 " + _navWaypoints.Count + " 个", 12);

        y -= 26f;
        var total = _navWaypoints.Count;
        var pages = total <= 0 ? 1 : (total + NavWaypointPageSize - 1) / NavWaypointPageSize;
        if (_navWpPage >= pages)
        {
            _navWpPage = Math.Max(0, pages - 1);
        }

        var start = _navWpPage * NavWaypointPageSize;
        for (var i = 0; i < NavWaypointPageSize; i++)
        {
            var idx = start + i;
            if (idx >= total)
            {
                break;
            }

            var wp = _navWaypoints[idx];
            var row = CreateUiChild(_bodyRoot, "Wp" + idx, rtType);
            SetAnchoredTop(RequireRect(row, "wr"), -55f, y, 320f, 28f);
            var rowImg = AddComp(row, "UnityEngine.UI.Image");
            SetColor(rowImg, 0.14f, 0.18f, 0.22f, 1f);
            var rowLab = CreateUiChild(row, "L", rtType);
            StretchFull(RequireRect(rowLab, "wrl"));
            var title = wp.Name ?? "";
            if (title.Length > 10)
            {
                title = title.Substring(0, 10) + "…";
            }

            SetText(AddText(rowLab), title + " " + wp.Floor + " (" + wp.X + "," + wp.Y + ")", 11);

            var navOne = CreateUiChild(_bodyRoot, "WpGo" + idx, rtType);
            SetAnchoredTop(RequireRect(navOne, "wg"), 145f, y, 70f, 28f);
            var nImg = AddComp(navOne, "UnityEngine.UI.Image");
            SetColor(nImg, 0.2f, 0.45f, 0.28f, 1f);
            var nLab = CreateUiChild(navOne, "L", rtType);
            StretchFull(RequireRect(nLab, "wnl"));
            SetText(AddText(nLab), "导航", 12);
            var cap = wp;
            BindButton(navOne, nImg, () => NavGoTo(cap.Floor, cap.X, cap.Y, cap.Name));

            var delOne = CreateUiChild(_bodyRoot, "WpDel" + idx, rtType);
            SetAnchoredTop(RequireRect(delOne, "wd"), 220f, y, 60f, 28f);
            var dImg = AddComp(delOne, "UnityEngine.UI.Image");
            SetColor(dImg, 0.4f, 0.2f, 0.2f, 1f);
            var dLab = CreateUiChild(delOne, "L", rtType);
            StretchFull(RequireRect(dLab, "wdl"));
            SetText(AddText(dLab), "删", 12);
            var delId = wp.Id;
            BindButton(delOne, dImg, () =>
            {
                if (DeleteNavWaypoint(delId))
                {
                    Tip("已删除点位");
                    RebuildNavTab();
                }
            });

            y -= 32f;
        }

        var barY = y - 4f;
        var prev = CreateUiChild(_bodyRoot, "WpPrev", rtType);
        SetAnchoredTop(RequireRect(prev, "wp"), -120f, barY, 90f, 28f);
        var pImg = AddComp(prev, "UnityEngine.UI.Image");
        SetColor(pImg, 0.25f, 0.28f, 0.34f, 1f);
        var prevLab = CreateUiChild(prev, "L", rtType);
        StretchFull(RequireRect(prevLab, "pll"));
        SetText(AddText(prevLab), "上一页", 12);
        BindButton(prev, pImg, () =>
        {
            CaptureNavInputsFromUi();
            if (_navWpPage > 0)
            {
                _navWpPage--;
                RebuildNavTab();
            }
        });

        var pageGo = CreateUiChild(_bodyRoot, "WpPage", rtType);
        SetAnchoredTop(RequireRect(pageGo, "wpg"), 0f, barY, 100f, 28f);
        SetText(AddText(pageGo), (_navWpPage + 1) + "/" + pages, 12);

        var next = CreateUiChild(_bodyRoot, "WpNext", rtType);
        SetAnchoredTop(RequireRect(next, "wnx"), 120f, barY, 90f, 28f);
        var nxImg = AddComp(next, "UnityEngine.UI.Image");
        SetColor(nxImg, 0.25f, 0.28f, 0.34f, 1f);
        var nxLab = CreateUiChild(next, "L", rtType);
        StretchFull(RequireRect(nxLab, "nxl"));
        SetText(AddText(nxLab), "下一页", 12);
        BindButton(next, nxImg, () =>
        {
            CaptureNavInputsFromUi();
            if (_navWpPage + 1 < pages)
            {
                _navWpPage++;
                RebuildNavTab();
            }
        });

        barY -= 34f;
        var st = CreateUiChild(_bodyRoot, "NavSt", rtType);
        SetAnchoredTop(RequireRect(st, "nst"), 0f, barY, 500f, 48f);
        _navStatusText = AddText(st);
        try
        {
            SetProp(_navStatusText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(_navStatusText, string.IsNullOrEmpty(_navStatusLine) ? "状态: 就绪（地图号=currentFloor）" : _navStatusLine, 12);
    }

    /// <summary>界面页：一键打开原客服入口可切的各功能面板。</summary>
    private static void BuildOpenUiBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        var hint = CreateUiChild(_bodyRoot, "OpenUiHint", rtType);
        SetAnchoredTop(RequireRect(hint, "ouh"), 0f, -4f, 540f, 40f);
        var hintTxt = AddText(hint);
        try
        {
            SetProp(hintTxt, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(hintTxt, "打开界面：点按钮打开对应面板（原侧栏客服可改的那些入口）。", 12);

        // 两列按钮
        var entries = new[]
        {
            new[] { "autoskill", "高级自动战斗" },
            new[] { "blindbox", "盲盒(3028)" },
            new[] { "lottery", "幸运秘宝" },
            new[] { "crystal", "水晶阁(3043)" },
            new[] { "honour", "荣耀士兵(3044)" },
            new[] { "challengeboss", "讨伐令(3045)" },
            new[] { "diglett", "地鼠抽奖(3046)" },
            new[] { "bravetrial", "英雄试炼(3047)" },
            new[] { "boss", "讨伐Boss" },
            new[] { "bossland", "Boss大陆(3050)" },
            new[] { "ruby", "露比试炼" },
            new[] { "petreform", "宠物改造" },
            new[] { "familyhall", "公会领地传送" },
            new[] { "gm1", "GM命令工具" },
            new[] { "gm2", "GM道具商店" },
            new[] { "gm3", "GM宠物商店" },
            new[] { "gm4", "GM宠物特效" },
            new[] { "gm5", "GM动画设置" },
        };

        var y = -52f;
        for (var i = 0; i < entries.Length; i++)
        {
            var col = i % 2;
            var row = i / 2;
            var x = col == 0 ? -130f : 130f;
            var yy = y - row * 44f;
            var id = entries[i][0];
            var label = entries[i][1];
            var btn = CreateUiChild(_bodyRoot, "OpenUi_" + id, rtType);
            SetAnchoredTop(RequireRect(btn, "oub"), x, yy, 240f, 38f);
            var img = AddComp(btn, "UnityEngine.UI.Image");
            SetColor(img, 0.18f, 0.32f, 0.42f, 1f);
            var lab = CreateUiChild(btn, "L", rtType);
            StretchFull(RequireRect(lab, "oul"));
            SetText(AddText(lab), label, 14);
            var captured = id;
            BindButton(btn, img, () => OpenFeaturePanel(captured));
        }

        WriteLog("BuildOpenUiBody done");
    }

    /// <summary>打开功能面板（与客服入口模式对应）。</summary>
    private static void OpenFeaturePanel(string mode)
    {
        try
        {
            WriteLog("OpenFeaturePanel " + mode);
            bool ok;
            string tip;
            switch (mode)
            {
                case "autoskill":
                    ok = TryOpenAutoSkillPanel();
                    tip = ok ? "已打开高级自动战斗" : "打开高级自动战斗失败";
                    break;
                case "blindbox":
                    ok = TryOpenBlindbox();
                    tip = ok ? "已请求盲盒数据" : "打开盲盒失败";
                    break;
                case "lottery":
                    ok = TryOpenUiPanelBare("LotteryPanel");
                    tip = ok ? "已打开幸运秘宝" : "打开幸运秘宝失败";
                    break;
                case "crystal":
                    ok = TryOpenUiPanelBare("LuckCrystalPanel");
                    tip = ok ? "已打开水晶阁" : "打开水晶阁失败";
                    break;
                case "honour":
                    ok = TryOpenUiPanelBare("HonourPanel");
                    tip = ok ? "已打开荣耀士兵" : "打开荣耀士兵失败";
                    break;
                case "challengeboss":
                    ok = TryOpenUiPanelBare("ChallengeBossPanel");
                    tip = ok ? "已打开讨伐令" : "打开讨伐令失败";
                    break;
                case "diglett":
                    ok = TryOpenDiglettLottery();
                    tip = ok ? "已请求地鼠抽奖" : "打开地鼠抽奖失败";
                    break;
                case "bravetrial":
                    ok = TryOpenUiPanelBare("BraveTrialPanel");
                    tip = ok ? "已打开英雄试炼" : "打开英雄试炼失败";
                    break;
                case "boss":
                    ok = TryOpenUiPanelBare("BOSSChallengePanel");
                    tip = ok ? "已打开讨伐Boss" : "打开讨伐Boss失败";
                    break;
                case "bossland":
                    ok = TryOpenBossLand();
                    tip = ok ? "已请求Boss大陆" : "打开Boss大陆失败";
                    break;
                case "ruby":
                    ok = TryOpenRubyTrial();
                    tip = ok ? "已打开露比试炼" : "打开露比试炼失败";
                    break;
                case "petreform":
                    ok = TryOpenPetReform();
                    tip = ok ? "已打开宠物改造" : "打开宠物改造失败";
                    break;
                case "familyhall":
                    ok = TryOpenFamilyHallTeleport();
                    tip = ok ? "已发送公会领地传送" : "公会传送失败";
                    break;
                case "gm1":
                    ok = TryOpenUiPanelBare("GMToolsPanel");
                    tip = ok ? "已打开GM命令工具" : "打开GM命令失败";
                    break;
                case "gm2":
                    ok = TryOpenUiPanelBare("GMStorePanel");
                    tip = ok ? "已打开GM道具商店" : "打开GM道具店失败";
                    break;
                case "gm3":
                    ok = TryOpenUiPanelBare("GMPetStorePanel");
                    tip = ok ? "已打开GM宠物商店" : "打开GM宠店失败";
                    break;
                case "gm4":
                    ok = TryOpenUiPanelBare("GMPetEffectPanel");
                    tip = ok ? "已打开GM宠物特效" : "打开GM特效失败";
                    break;
                case "gm5":
                    ok = TryOpenUiPanelBare("GMAnimationSettingPanel");
                    tip = ok ? "已打开GM动画设置" : "打开GM动画失败";
                    break;
                default:
                    ok = false;
                    tip = "未知面板: " + mode;
                    break;
            }

            Tip(tip);
            WriteLog("OpenFeaturePanel " + mode + " ok=" + ok);
        }
        catch (Exception ex)
        {
            WriteLog("OpenFeaturePanel EX: " + RootMessage(ex));
            Tip("打开失败: " + RootMessage(ex));
        }
    }

    private static string GetSelectOrMainUid()
    {
        var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "SelectPlayerUid") ?? "") ?? "";
        if (string.IsNullOrEmpty(uid))
        {
            uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
        }

        return uid;
    }

    /// <summary>UIManager.GetUIPanel&lt;T&gt;() 后调 UIPanel.Open()（无参）。</summary>
    private static bool TryOpenUiPanelBare(string panelTypeName)
    {
        var panel = GetUiPanel(panelTypeName);
        if (panel == null)
        {
            WriteLog("TryOpenUiPanelBare miss type=" + panelTypeName);
            return false;
        }

        MethodInfo open = null;
        for (var t = panel.GetType(); t != null; t = t.BaseType)
        {
            open = t.GetMethod(
                "Open",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                Type.EmptyTypes,
                null);
            if (open != null)
            {
                break;
            }
        }

        if (open == null)
        {
            open = panel.GetType().GetMethod(
                "Open",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
        }

        if (open == null)
        {
            WriteLog("TryOpenUiPanelBare no Open() on " + panelTypeName);
            return false;
        }

        open.Invoke(panel, null);
        return true;
    }

    private static bool TryOpenAutoSkillPanel()
    {
        var mgr = GetManagerInstance("BattleAutoSkillManager");
        if (mgr == null)
        {
            return false;
        }

        var uid = GetSelectOrMainUid();
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        var open = mgr.GetType().GetMethod(
            "OpenAutoSkillSettingPanel",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        if (open == null)
        {
            return false;
        }

        open.Invoke(mgr, new object[] { uid });
        return true;
    }

    private static bool TryOpenBlindbox()
    {
        var mgr = GetManagerInstance("ActivityManager");
        if (mgr == null)
        {
            return false;
        }

        var uid = GetSelectOrMainUid();
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        MethodInfo send = null;
        foreach (var m in mgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendBlindboxDraw")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length >= 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
            {
                send = m;
                break;
            }
        }

        if (send == null)
        {
            return false;
        }

        var psAll = send.GetParameters();
        var args = new object[psAll.Length];
        args[0] = "获取数据";
        args[1] = uid;
        args[2] = null;
        for (var i = 3; i < args.Length; i++)
        {
            args[i] = psAll[i].HasDefaultValue ? psAll[i].DefaultValue : null;
        }

        send.Invoke(mgr, args);
        return true;
    }

    /// <summary>侧栏同款：ActivityManager.SendDiglettLotteryMsg("请求数据", uid) → SC「同步数据」开 DiglettLotteryPanel。</summary>
    private static bool TryOpenDiglettLottery()
    {
        var mgr = GetManagerInstance("ActivityManager");
        if (mgr == null)
        {
            return false;
        }

        var uid = GetSelectOrMainUid();
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        var send = mgr.GetType().GetMethod(
            "SendDiglettLotteryMsg",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(string) },
            null);
        if (send == null)
        {
            return false;
        }

        send.Invoke(mgr, new object[] { "请求数据", uid });
        return true;
    }

    /// <summary>
    /// Boss 大陆/水晶副本挂在 BOSSChallengePanel 子页：先开父面板，再 SendCrystalAndSwMsg("获取数据", 101, 0, uid)。
    /// </summary>
    private static bool TryOpenBossLand()
    {
        var uid = GetSelectOrMainUid();
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        // 父面板需在场才能收 FramAndCrystal 并 Open CrystalAndSwPanel
        TryOpenUiPanelBare("BOSSChallengePanel");

        var mgr = GetManagerInstance("BountyOfferedManager");
        if (mgr == null)
        {
            return false;
        }

        var send = mgr.GetType().GetMethod(
            "SendCrystalAndSwMsg",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(int), typeof(int), typeof(string) },
            null);
        if (send == null)
        {
            return false;
        }

        // dungeonId 101 = 侧栏 Tab2（水晶副本其一）；102 为 Tab3
        send.Invoke(mgr, new object[] { "获取数据", 101, 0, uid });
        return true;
    }

    private static bool TryOpenPetReform()
    {
        var mgr = GetManagerInstance("PetManager");
        if (mgr == null)
        {
            return false;
        }

        var uid = GetSelectOrMainUid();
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        MethodInfo open = null;
        foreach (var m in mgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "OpenPetMain")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 4
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(int)
                && ps[2].ParameterType == typeof(int)
                && ps[3].ParameterType == typeof(int))
            {
                open = m;
                break;
            }
        }

        if (open == null)
        {
            return false;
        }

        // openPage=3 → PET_TYPE.RESET（洗档/改造/重构）
        open.Invoke(mgr, new object[] { uid, -1, 3, -1 });
        return true;
    }

    private static bool TryOpenRubyTrial()
    {
        var panel = GetUiPanel("RubyTrialPanel");
        if (panel == null)
        {
            return false;
        }

        var protoType = FindType("Proto_SC_LoopyTrial");
        if (protoType == null)
        {
            return TryOpenUiPanelBare("RubyTrialPanel");
        }

        var proto = Activator.CreateInstance(protoType);
        var mainUid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
        SetMember(proto, "KUid", mainUid);

        MethodInfo open1 = null;
        foreach (var m in panel.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "Open")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(protoType))
            {
                open1 = m;
                break;
            }
        }

        if (open1 == null)
        {
            return TryOpenUiPanelBare("RubyTrialPanel");
        }

        open1.Invoke(panel, new object[] { proto });
        return true;
    }

    private static bool TryOpenFamilyHallTeleport()
    {
        var mgr = GetManagerInstance("FamilyManager");
        if (mgr == null)
        {
            return false;
        }

        MethodInfo send = null;
        foreach (var m in mgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendFamily")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length >= 3
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(int)
                && ps[2].ParameterType == typeof(string))
            {
                send = m;
                break;
            }
        }

        if (send == null)
        {
            return false;
        }

        var psAll = send.GetParameters();
        var args = new object[psAll.Length];
        args[0] = "NPC传送";
        args[1] = 0;
        args[2] = "1";
        for (var i = 3; i < args.Length; i++)
        {
            if (psAll[i].ParameterType == typeof(string))
            {
                args[i] = "";
            }
            else if (psAll[i].ParameterType == typeof(int))
            {
                args[i] = 0;
            }
            else if (psAll[i].ParameterType.IsValueType)
            {
                args[i] = Activator.CreateInstance(psAll[i].ParameterType);
            }
            else
            {
                args[i] = null;
            }
        }

        send.Invoke(mgr, args);
        return true;
    }

    private static void BuildEscortBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        if (_escortPicking)
        {
            BuildEscortPickerBody(rtType);
            return;
        }

        var dragonLoopUi = DragonLoopUiEnabled();

        var hint2 = CreateUiChild(_bodyRoot, "Hint2", rtType);
        SetAnchoredTop(RequireRect(hint2, "ha2"), 0f, -8f, 500f, 88f);
        var hintText = AddText(hint2);
        try
        {
            SetProp(hintText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(
            hintText,
            "队列护航：可塞未接；完成一项后等 5 秒再下一项。\n"
            + "手动暂停不清铃；自动暂停约每2秒响铃，点「我知道了」或停止才停。静止5秒尝试恢复，连挪5次后改为直接续任务再观察5秒；本步骤连续20次失败自动暂停。\n"
            + (TempMidAutumnEscort119
                ? "中秋循环：阿凯版=回登入点+赤凤之翼；哥拉尔版=登入点在哥拉尔、不用赤凤之翼。最后一步分账号存兑换券后下一轮。\n"
                : "")
            + (dragonLoopUi
                ? "龙族循环A：自动重置龙4→按序执行龙族纷争1-4→宠物位满停止。"
                : ""),
            11);

        var y = -96f;
        var running = _escortActive;

        // 第一行：编辑/追加队列（主入口，始终可见）
        var editBtn = CreateUiChild(_bodyRoot, "EditQueue", rtType);
        SetAnchoredTop(RequireRect(editBtn, "eq"), 0f, y, 420f, 42f);
        var editImg = AddComp(editBtn, "UnityEngine.UI.Image");
        SetColor(editImg, 0.18f, 0.42f, 0.55f, 1f);
        var editLab = CreateUiChild(editBtn, "L", rtType);
        StretchFull(RequireRect(editLab, "eql"));
        SetText(AddText(editLab), running ? "追加任务到队列（点任务入队）" : "选择任务加入队列", 15);
        BindButton(editBtn, editImg, () =>
        {
            OpenEscortPicker(_escortActive);
            RebuildEscortTab();
        });

        y -= 50f;
        // 第二行：开始/停止 + 暂停
        var btn = CreateUiChild(_bodyRoot, "EscortBtn", rtType);
        SetAnchoredTop(RequireRect(btn, "ebb"), running ? -110f : 0f, y, running ? 200f : 420f, 40f);
        var img = AddComp(btn, "UnityEngine.UI.Image");
        SetColor(img, running ? 0.45f : 0.2f, running ? 0.22f : 0.48f, 0.28f, 1f);
        var lab = CreateUiChild(btn, "L", rtType);
        StretchFull(RequireRect(lab, "ebl"));
        SetText(AddText(lab), running ? "停止(清队列)" : "开始队列护航", 14);
        BindButton(btn, img, () =>
        {
            if (_escortActive)
            {
                CancelEscort(true, "已停止，队列已清空");
            }
            else
            {
                StartEscortQueue();
            }
        });

        if (running)
        {
            var pauseBtn = CreateUiChild(_bodyRoot, "PauseBtn", rtType);
            SetAnchoredTop(RequireRect(pauseBtn, "pb"), 110f, y, 200f, 40f);
            var pauseImg = AddComp(pauseBtn, "UnityEngine.UI.Image");
            SetColor(pauseImg, _escortPaused ? 0.2f : 0.42f, _escortPaused ? 0.45f : 0.35f, _escortPaused ? 0.28f : 0.2f, 1f);
            var pauseLab = CreateUiChild(pauseBtn, "L", rtType);
            StretchFull(RequireRect(pauseLab, "pbl"));
            SetText(AddText(pauseLab), _escortPaused ? "继续护航" : "暂停护航", 14);
            BindButton(pauseBtn, pauseImg, () =>
            {
                if (_escortPaused)
                {
                    ResumeEscort();
                }
                else
                {
                    PauseEscort("已暂停，可手动接管", false);
                }
            });
        }

        y -= 50f;
        // 龙族循环 A（仅带龙族.flag）；中秋循环两版都显示（临时活动）
        if (dragonLoopUi)
        {
            var dragonBtn = CreateUiChild(_bodyRoot, "DragonLoopBtn", rtType);
            SetAnchoredTop(RequireRect(dragonBtn, "dlb"), 0f, y, 420f, 40f);
            var dragonImg = AddComp(dragonBtn, "UnityEngine.UI.Image");
            SetColor(dragonImg, _dragonLoopActive ? 0.55f : 0.3f, _dragonLoopActive ? 0.24f : 0.3f, _dragonLoopActive ? 0.22f : 0.42f, 1f);
            var dragonLab = CreateUiChild(dragonBtn, "L", rtType);
            StretchFull(RequireRect(dragonLab, "dll"));
            SetText(AddText(dragonLab), _dragonLoopActive
                ? ("停止龙族循环(第" + (_dragonLoopCount + 1) + "轮)")
                : "龙族循环A(110-113)", 14);
            BindButton(dragonBtn, dragonImg, () =>
            {
                if (_dragonLoopActive)
                {
                    StopDragonLoop();
                }
                else
                {
                    StartDragonLoop();
                }
            });
            y -= 48f;
        }

        if (TempMidAutumnEscort119)
        {
            if (_midAutumnLoopActive)
            {
                var midBtn = CreateUiChild(_bodyRoot, "MidAutumnLoopBtn", rtType);
                SetAnchoredTop(RequireRect(midBtn, "mab"), 0f, y, 420f, 40f);
                var midImg = AddComp(midBtn, "UnityEngine.UI.Image");
                SetColor(midImg, 0.62f, 0.32f, 0.12f, 1f);
                var midLab = CreateUiChild(midBtn, "L", rtType);
                StretchFull(RequireRect(midLab, "mal"));
                var ed = _midAutumnGoralEdition ? "哥拉尔" : "阿凯";
                SetText(AddText(midLab),
                    "停止中秋" + ed + "版(第" + (_midAutumnLoopCount + 1) + "轮)", 14);
                BindButton(midBtn, midImg, StopMidAutumnLoop);
                y -= 48f;
            }
            else
            {
                var akBtn = CreateUiChild(_bodyRoot, "MidAutumnAkBtn", rtType);
                SetAnchoredTop(RequireRect(akBtn, "maak"), -110f, y, 200f, 40f);
                var akImg = AddComp(akBtn, "UnityEngine.UI.Image");
                SetColor(akImg, 0.42f, 0.28f, 0.18f, 1f);
                var akLab = CreateUiChild(akBtn, "L", rtType);
                StretchFull(RequireRect(akLab, "maakl"));
                SetText(AddText(akLab), "中秋阿凯版", 14);
                BindButton(akBtn, akImg, () => StartMidAutumnLoop(false));

                var goBtn = CreateUiChild(_bodyRoot, "MidAutumnGoBtn", rtType);
                SetAnchoredTop(RequireRect(goBtn, "mago"), 110f, y, 200f, 40f);
                var goImg = AddComp(goBtn, "UnityEngine.UI.Image");
                SetColor(goImg, 0.28f, 0.36f, 0.48f, 1f);
                var goLab = CreateUiChild(goBtn, "L", rtType);
                StretchFull(RequireRect(goLab, "magol"));
                SetText(AddText(goLab), "中秋哥拉尔版", 14);
                BindButton(goBtn, goImg, () => StartMidAutumnLoop(true));
                y -= 48f;
            }
        }

        if (_escortAlertRinging)
        {
            var ack = CreateUiChild(_bodyRoot, "AckBell", rtType);
            SetAnchoredTop(RequireRect(ack, "ack"), 0f, y, 420f, 40f);
            var ackImg = AddComp(ack, "UnityEngine.UI.Image");
            SetColor(ackImg, 0.55f, 0.28f, 0.12f, 1f);
            var ackLab = CreateUiChild(ack, "L", rtType);
            StretchFull(RequireRect(ackLab, "ackl"));
            SetText(AddText(ackLab), "我知道了（停止铃声）", 15);
            BindButton(ack, ackImg, AcknowledgeEscortAlert);
            y -= 48f;
        }

        var qSummary = CreateUiChild(_bodyRoot, "QSum", rtType);
        SetAnchoredTop(RequireRect(qSummary, "qs"), 0f, y, 500f, 72f);
        var qText = AddText(qSummary);
        try
        {
            SetProp(qText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(qText, FormatEscortQueueSummary(), 12);

        y -= 80f;
        var st = CreateUiChild(_bodyRoot, "St", rtType);
        SetAnchoredTop(RequireRect(st, "st"), 0f, y, 500f, 160f);
        _escortStatusText = AddText(st);
        try
        {
            SetProp(_escortStatusText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        SetText(_escortStatusText, FormatEscortStatus(), 13);
    }

    private static void BuildEscortPickerBody(Type rtType)
    {
        RefreshEscortCandidates();
        var filtered = GetFilteredEscortCandidates();

        var hint = CreateUiChild(_bodyRoot, "PickHint", rtType);
        SetAnchoredTop(RequireRect(hint, "ph"), 0f, -2f, 500f, 36f);
        var hintText = AddText(hint);
        try
        {
            SetProp(hintText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0));
        }
        catch
        {
            // ignore
        }

        var totalAll = _escortCandidates.Count;
        var total = filtered.Count;
        var pages = total <= 0 ? 1 : (total + EscortPageSize - 1) / EscortPageSize;
        if (_escortPage >= pages)
        {
            _escortPage = Math.Max(0, pages - 1);
        }

        var filterNote = string.IsNullOrEmpty(_escortSearch)
            ? ""
            : (" 搜「" + _escortSearch + "」");
        var modeNote = _escortActive ? "追加到队列" : "点任务加入队列";
        SetText(
            hintText,
            modeNote + "（含未接）共" + totalAll + " / 显" + total + filterNote
            + "｜队列" + _escortQueue.Count + "项",
            12);

        var searchY = -40f;
        _escortSearchInput = CreateInputField(_bodyRoot, rtType, "EscortSearch", 0f, searchY, 300f, 28f, _escortSearch, "任务名或ID");
        var searchBtn = CreateUiChild(_bodyRoot, "SearchBtn", rtType);
        SetAnchoredTop(RequireRect(searchBtn, "sb"), 175f, searchY, 70f, 28f);
        var searchImg = AddComp(searchBtn, "UnityEngine.UI.Image");
        SetColor(searchImg, 0.2f, 0.4f, 0.55f, 1f);
        var searchLab = CreateUiChild(searchBtn, "L", rtType);
        StretchFull(RequireRect(searchLab, "sbl"));
        SetText(AddText(searchLab), "搜索", 12);
        BindButton(searchBtn, searchImg, () =>
        {
            _escortSearch = ReadInputFieldText(_escortSearchInput);
            _escortPage = 0;
            RebuildEscortTab();
        });

        var clearBtn = CreateUiChild(_bodyRoot, "ClearSearch", rtType);
        SetAnchoredTop(RequireRect(clearBtn, "cs"), 235f, searchY, 50f, 28f);
        var clearImg = AddComp(clearBtn, "UnityEngine.UI.Image");
        SetColor(clearImg, 0.35f, 0.3f, 0.3f, 1f);
        var clearLab = CreateUiChild(clearBtn, "L", rtType);
        StretchFull(RequireRect(clearLab, "csl"));
        SetText(AddText(clearLab), "清空", 11);
        BindButton(clearBtn, clearImg, () =>
        {
            _escortSearch = "";
            _escortPage = 0;
            RebuildEscortTab();
        });

        var start = _escortPage * EscortPageSize;
        var y = -74f;
        for (var i = 0; i < EscortPageSize; i++)
        {
            var idx = start + i;
            if (idx >= total)
            {
                break;
            }

            var c = filtered[idx];
            var row = CreateUiChild(_bodyRoot, "Pick" + c.Id + "_" + idx, rtType);
            SetAnchoredTop(RequireRect(row, "pr"), 0f, y, 500f, 28f);
            var img = AddComp(row, "UnityEngine.UI.Image");
            var inQueue = EscortQueueContains(c.Id);
            SetColor(img, inQueue ? 0.22f : 0.16f, inQueue ? 0.32f : 0.22f, inQueue ? 0.28f : 0.3f, 1f);
            var lab = CreateUiChild(row, "L", rtType);
            StretchFull(RequireRect(lab, "pl"));
            var label = AddText(lab);
            try
            {
                SetProp(label, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleLeft", 3));
            }
            catch
            {
                // ignore
            }

            var title = c.Title ?? "";
            if (title.Length > 18)
            {
                title = title.Substring(0, 18) + "…";
            }

            SetText(label, (inQueue ? "✓" : "+") + "[" + c.Status + "] #" + c.Id + " " + title, 12);
            var missionId = c.Id;
            var missionTitle = c.Title ?? ("#" + c.Id);
            var missionStatus = c.Status ?? "";
            BindButton(row, img, () => EnqueueEscortMission(missionId, missionTitle, missionStatus));
            y -= 32f;
        }

        var barY = -74f - EscortPageSize * 32f - 4f;
        var prev = CreateUiChild(_bodyRoot, "Prev", rtType);
        SetAnchoredTop(RequireRect(prev, "prev"), -160f, barY, 90f, 28f);
        var prevImg = AddComp(prev, "UnityEngine.UI.Image");
        SetColor(prevImg, 0.25f, 0.28f, 0.34f, 1f);
        var prevLab = CreateUiChild(prev, "L", rtType);
        StretchFull(RequireRect(prevLab, "pvl"));
        SetText(AddText(prevLab), "上一页", 12);
        BindButton(prev, prevImg, () =>
        {
            CaptureEscortSearchFromUi();
            if (_escortPage > 0)
            {
                _escortPage--;
                RebuildEscortTab();
            }
        });

        var pageLabGo = CreateUiChild(_bodyRoot, "Page", rtType);
        SetAnchoredTop(RequireRect(pageLabGo, "pg"), 0f, barY, 100f, 28f);
        SetText(AddText(pageLabGo), (_escortPage + 1) + "/" + pages, 12);

        var next = CreateUiChild(_bodyRoot, "Next", rtType);
        SetAnchoredTop(RequireRect(next, "next"), 160f, barY, 90f, 28f);
        var nextImg = AddComp(next, "UnityEngine.UI.Image");
        SetColor(nextImg, 0.25f, 0.28f, 0.34f, 1f);
        var nextLab = CreateUiChild(next, "L", rtType);
        StretchFull(RequireRect(nextLab, "nxl"));
        SetText(AddText(nextLab), "下一页", 12);
        BindButton(next, nextImg, () =>
        {
            CaptureEscortSearchFromUi();
            if (_escortPage + 1 < pages)
            {
                _escortPage++;
                RebuildEscortTab();
            }
        });

        // 队列预览（可点移除未来项）
        barY -= 34f;
        var qHint = CreateUiChild(_bodyRoot, "QHint", rtType);
        SetAnchoredTop(RequireRect(qHint, "qh"), 0f, barY, 500f, 22f);
        SetText(AddText(qHint), "队列（点项可移除未执行的）:", 11);
        barY -= 24f;
        var showN = Math.Min(3, _escortQueue.Count);
        var qStart = Math.Max(0, _escortQueue.Count - showN);
        if (_escortActive && _escortQueueIndex >= 0)
        {
            qStart = Math.Max(0, Math.Min(_escortQueueIndex, _escortQueue.Count - showN));
        }

        for (var qi = 0; qi < showN; qi++)
        {
            var qIdx = qStart + qi;
            if (qIdx >= _escortQueue.Count)
            {
                break;
            }

            var qc = _escortQueue[qIdx];
            var mark = _escortActive && qIdx == _escortQueueIndex
                ? "▶"
                : (_escortActive && qIdx < _escortQueueIndex ? "✓" : (qIdx + 1) + ".");
            var row = CreateUiChild(_bodyRoot, "Q" + qIdx, rtType);
            SetAnchoredTop(RequireRect(row, "qr"), 0f, barY, 500f, 24f);
            var qImg = AddComp(row, "UnityEngine.UI.Image");
            SetColor(qImg, 0.14f, 0.18f, 0.22f, 1f);
            var qLab = CreateUiChild(row, "L", rtType);
            StretchFull(RequireRect(qLab, "ql"));
            var t = qc.Title ?? "";
            if (t.Length > 16)
            {
                t = t.Substring(0, 16) + "…";
            }

            SetText(AddText(qLab), mark + " #" + qc.Id + " " + t, 11);
            var removeIdx = qIdx;
            BindButton(row, qImg, () => RemoveEscortQueueAt(removeIdx));
            barY -= 26f;
        }

        barY -= 4f;
        if (!_escortActive)
        {
            var startBtn = CreateUiChild(_bodyRoot, "StartQ", rtType);
            SetAnchoredTop(RequireRect(startBtn, "sq"), -110f, barY, 180f, 32f);
            var startImg = AddComp(startBtn, "UnityEngine.UI.Image");
            SetColor(startImg, 0.2f, 0.48f, 0.28f, 1f);
            var startLab = CreateUiChild(startBtn, "L", rtType);
            StretchFull(RequireRect(startLab, "sql"));
            SetText(AddText(startLab), "开始队列护航", 13);
            BindButton(startBtn, startImg, StartEscortQueue);

            var clearQ = CreateUiChild(_bodyRoot, "ClearQ", rtType);
            SetAnchoredTop(RequireRect(clearQ, "cq"), 110f, barY, 140f, 32f);
            var clearQImg = AddComp(clearQ, "UnityEngine.UI.Image");
            SetColor(clearQImg, 0.4f, 0.28f, 0.2f, 1f);
            var clearQLab = CreateUiChild(clearQ, "L", rtType);
            StretchFull(RequireRect(clearQLab, "cql"));
            SetText(AddText(clearQLab), "清空队列", 13);
            BindButton(clearQ, clearQImg, () =>
            {
                _escortQueue.Clear();
                Tip("任务护航：队列已清空");
                RebuildEscortTab();
            });
            barY -= 36f;
        }

        var back = CreateUiChild(_bodyRoot, "BackPick", rtType);
        SetAnchoredTop(RequireRect(back, "bp"), 0f, barY, 200f, 30f);
        var backImg = AddComp(back, "UnityEngine.UI.Image");
        SetColor(backImg, 0.35f, 0.25f, 0.25f, 1f);
        var backLab = CreateUiChild(back, "L", rtType);
        StretchFull(RequireRect(backLab, "bpl"));
        SetText(AddText(backLab), _escortActive ? "返回护航" : "返回", 13);
        BindButton(back, backImg, () =>
        {
            _escortPicking = false;
            RebuildEscortTab();
        });
    }

    private static List<EscortCandidate> GetFilteredEscortCandidates()
    {
        var q = (_escortSearch ?? "").Trim();
        if (q.Length == 0)
        {
            return _escortCandidates;
        }

        var list = new List<EscortCandidate>();
        for (var i = 0; i < _escortCandidates.Count; i++)
        {
            var c = _escortCandidates[i];
            var idStr = c.Id.ToString();
            var title = c.Title ?? "";
            var status = c.Status ?? "";
            if (idStr.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                list.Add(c);
            }
        }

        return list;
    }

    private static void CaptureEscortSearchFromUi()
    {
        var t = ReadInputFieldText(_escortSearchInput);
        if (t != null)
        {
            _escortSearch = t;
        }
    }

    private static string ReadInputFieldText(object input)
    {
        if (input == null || IsUnityNull(input))
        {
            return _escortSearch ?? "";
        }

        try
        {
            var t = GetProp(input, "text") ?? GetMember(input, "text");
            return Convert.ToString(t ?? "") ?? "";
        }
        catch
        {
            return _escortSearch ?? "";
        }
    }

    /// <summary>创建简易 UGUI InputField（反射），用于任务搜索。</summary>
    private static object CreateInputField(
        object parent, Type rtType, string name, float x, float y, float w, float h, string value, string placeholder)
    {
        var go = CreateUiChild(parent, name, rtType);
        SetAnchoredTop(RequireRect(go, name + "rt"), x, y, w, h);
        var img = AddComp(go, "UnityEngine.UI.Image");
        SetColor(img, 0.12f, 0.14f, 0.18f, 1f);

        var inputType = FindType("UnityEngine.UI.InputField") ?? FindType("TMPro.TMP_InputField");
        if (inputType == null)
        {
            // 退化：只显示当前关键字
            var fallback = AddText(go);
            try
            {
                SetProp(fallback, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleLeft", 3));
            }
            catch
            {
                // ignore
            }

            SetText(fallback, string.IsNullOrEmpty(value) ? (" " + placeholder) : (" " + value), 13);
            return null;
        }

        var input = AddComp(go, inputType);
        SetProp(input, "targetGraphic", img);

        var textGo = CreateUiChild(go, "Text", rtType);
        StretchFull(RequireRect(textGo, name + "txt"));
        try
        {
            var rt = RequireRect(textGo, name + "txt2");
            SetProp(rt, "offsetMin", Vec2(6f, 2f));
            SetProp(rt, "offsetMax", Vec2(-6f, -2f));
        }
        catch
        {
            // ignore
        }

        var text = AddText(textGo);
        try
        {
            SetProp(text, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleLeft", 3));
            SetProp(text, "supportRichText", false);
        }
        catch
        {
            // ignore
        }

        SetText(text, value ?? "", 13);
        SetProp(text, "color", MakeColor(0.95f, 0.95f, 0.95f, 1f));

        var phGo = CreateUiChild(go, "Placeholder", rtType);
        StretchFull(RequireRect(phGo, name + "ph"));
        try
        {
            var rt = RequireRect(phGo, name + "ph2");
            SetProp(rt, "offsetMin", Vec2(6f, 2f));
            SetProp(rt, "offsetMax", Vec2(-6f, -2f));
        }
        catch
        {
            // ignore
        }

        var ph = AddText(phGo);
        try
        {
            SetProp(ph, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleLeft", 3));
        }
        catch
        {
            // ignore
        }

        SetText(ph, placeholder ?? "", 13);
        SetProp(ph, "color", MakeColor(0.55f, 0.58f, 0.62f, 1f));

        // InputField / TMP_InputField 绑定
        try
        {
            SetProp(input, "textComponent", text);
        }
        catch
        {
            try
            {
                SetMember(input, "m_TextComponent", text);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            SetProp(input, "placeholder", ph);
        }
        catch
        {
            // ignore
        }

        try
        {
            SetProp(input, "text", value ?? "");
        }
        catch
        {
            // ignore
        }

        // 有字时藏 placeholder
        try
        {
            var phGoObj = GetProp(ph, "gameObject") ?? phGo;
            var has = !string.IsNullOrEmpty(value);
            phGoObj.GetType().GetMethod("SetActive", new[] { typeof(bool) })
                ?.Invoke(phGoObj, new object[] { !has });
        }
        catch
        {
            // ignore
        }

        return input;
    }

    private static void TryRebuildEscortTab()
    {
        if (_visible && _tab == TabEscort && _canvasGo != null && !IsUnityNull(_canvasGo))
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void RebuildEscortTab()
    {
        if (_tab != TabEscort || _bodyRoot == null || IsUnityNull(_bodyRoot))
        {
            return;
        }

        ClearBody();
        BuildEscortBody();
        RefreshTabButtonLabels();
    }

    private static string FormatEscortQueueSummary()
    {
        if (_escortQueue.Count == 0)
        {
            return "队列为空。点「编辑队列」加入任务（可含未接取）。";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("队列 ").Append(_escortQueue.Count).Append(" 项");
        if (_escortActive && _escortQueueIndex >= 0)
        {
            sb.Append("｜进度 ").Append(_escortQueueIndex + 1).Append('/').Append(_escortQueue.Count);
        }

        sb.Append('\n');
        var from = 0;
        var to = Math.Min(_escortQueue.Count, 4);
        if (_escortActive && _escortQueueIndex >= 0)
        {
            from = Math.Max(0, _escortQueueIndex);
            to = Math.Min(_escortQueue.Count, from + 4);
        }

        for (var i = from; i < to; i++)
        {
            var c = _escortQueue[i];
            var mark = _escortActive && i == _escortQueueIndex
                ? "▶"
                : (_escortActive && i < _escortQueueIndex ? "✓" : "·");
            var t = c.Title ?? "";
            if (t.Length > 14)
            {
                t = t.Substring(0, 14) + "…";
            }

            sb.Append(mark).Append('#').Append(c.Id).Append(' ').Append(t);
            if (i + 1 < to)
            {
                sb.Append("  ");
            }
        }

        if (to < _escortQueue.Count)
        {
            sb.Append(" …+").Append(_escortQueue.Count - to);
        }

        return sb.ToString();
    }

    /// <summary>读当前护航任务 MissionData.missionStepNum；-1=读不到。</summary>
    private static int GetEscortMissionStepNum()
    {
        try
        {
            if (_escortMissionId <= 0)
            {
                return -1;
            }

            var mission = GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                return -1;
            }

            var v = GetMember(mission, "missionStepNum");
            return Convert.ToInt32(v ?? -1);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>取当前护航任务标题（带子步骤进度，如「任务名 2/5」）；失败回退纯标题。</summary>
    private static string GetEscortMissionTitleWithStep()
    {
        var fallback = _escortMissionTitle ?? "";
        try
        {
            if (_escortMissionId <= 0)
            {
                return fallback;
            }

            var mission = GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                return fallback;
            }

            var m = mission.GetType().GetMethod(
                "GetTitleWithStepProgress",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return fallback;
            }

            var s = Convert.ToString(m.Invoke(mission, null) ?? "") ?? "";
            return string.IsNullOrEmpty(s) ? fallback : s;
        }
        catch
        {
            return fallback;
        }
    }

    // ---------------- 龙族纷争循环 ----------------

    /// <summary>判断任务 ID 是否属于龙族纷争（硬编码 + 标题关键字双校验）。</summary>
    private static bool IsDragonMission(int missionId)
    {
        foreach (var id in DragonMissionIds)
        {
            if (id == missionId)
            {
                return true;
            }
        }

        try
        {
            var mission = GetMissionDataById(missionId);
            var title = Convert.ToString(GetMember(mission, "title") ?? "") ?? "";
            if (!string.IsNullOrEmpty(title) && title.IndexOf(DragonTitleKeyword, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>读取任务当前状态字符串（Started/NotStart/Ended）。</summary>
    private static string GetMissionStatusStr(object mission)
    {
        try
        {
            return Convert.ToString(GetMember(mission, "taskstatus") ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 判断当前循环任务集（A/B 线）是否均非「已完成」状态。
    /// 已接(Started) 或 可接(NotStart) 都算就绪；入队后由护航自身的 ClickEscortTaskNav
    /// 负责把「可接」任务实际接取（含等级绕过与 20 次恢复重试），此处不做预接取。
    /// </summary>
    private static bool CheckDragonMissionsReady(out string failReason)
    {
        failReason = "";
        var ids = _dragonMissionIds ?? DragonMissionIds;
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var mission = GetMissionDataById(id);
            if (mission == null)
            {
                failReason = "找不到龙族纷争" + (i + 1) + "任务数据(#" + id + ")";
                return false;
            }

            var st = GetMissionStatusStr(mission);
            if (st.EndsWith("Ended", StringComparison.Ordinal) || st == "2")
            {
                failReason = "龙族纷争" + (i + 1) + "(#" + id + ")已完成（未重置）";
                return false;
            }
        }

        return true;
    }

    /// <summary>对所有队员发送重置龙族纷争4（resetId 有效才发）。</summary>
    private static void ResetDragon4ForAll()
    {
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            var cap = GetCaptainUid();
            if (!string.IsNullOrEmpty(cap))
            {
                uids.Add(cap);
            }
        }

        var resetType = FindType("Proto_CS_ResetTask");
        var lss = FindType("LSSPROTO");
        var opcodeField = lss?.GetField("LSSPROTO_RESET_TASK_FUNC", BindingFlags.Public | BindingFlags.Static);
        var net = GetManagerInstance("NetManager");
        var send = net?.GetType().GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (resetType == null || opcodeField == null || net == null || send == null)
        {
            WriteLog("dragon reset: 反射缺失 resetType=" + (resetType != null)
                     + " opcode=" + (opcodeField != null) + " net=" + (net != null) + " send=" + (send != null));
            return;
        }

        foreach (var uid in uids)
        {
            var mission = GetMissionDataById(113); // 龙族纷争4
            var resetId = mission != null ? Convert.ToInt32(GetMember(mission, "resetId") ?? -1) : -1;
            if (resetId <= 0)
            {
                WriteLog("dragon reset: uid=" + uid + " resetId 无效，跳过");
                continue;
            }

            try
            {
                var msg = Activator.CreateInstance(resetType);
                SetMember(msg, "Type", "重置任务");
                SetMember(msg, "Id", resetId.ToString());
                SetMember(msg, "KUid", uid);
                send.Invoke(net, new object[] { opcodeField.GetValue(null), msg });
                WriteLog("dragon reset: uid=" + uid + " resetId=" + resetId);
            }
            catch (Exception ex)
            {
                WriteLog("dragon reset EX uid=" + uid + " " + RootMessage(ex));
            }
        }
    }

    /// <summary>丢弃某个队员背包中含指定关键字的所有道具。返回丢弃件数。</summary>
    private static int DropItemsByKeyword(string uid, string keyword)
    {
        var dropped = 0;
        try
        {
            var items = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (items == null)
            {
                return 0;
            }

            var itemMgr = GetManagerInstance("ItemManager");
            if (itemMgr == null)
            {
                return 0;
            }

            // 找 SendBackPackMessage(string, int, int, string)
            MethodInfo send = null;
            foreach (var m in itemMgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendBackPackMessage")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 4
                    && ps[0].ParameterType.FullName == "System.String"
                    && ps[1].ParameterType.FullName == "System.Int32"
                    && ps[2].ParameterType.FullName == "System.Int32"
                    && ps[3].ParameterType.FullName == "System.String")
                {
                    send = m;
                    break;
                }
            }

            if (send == null)
            {
                return 0;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                var useFlag = Convert.ToInt32(GetMember(item, "useFlag") ?? 0);
                if (useFlag != 1)
                {
                    continue;
                }

                var data = GetMember(item, "data");
                var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                if (name.IndexOf(keyword, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                send.Invoke(itemMgr, new object[] { "丢弃道具", i, 1, uid });
                dropped++;
                Tip("已丢弃[" + name + "]");
            }
        }
        catch (Exception ex)
        {
            WriteLog("DropItemsByKeyword EX uid=" + uid + " kw=" + keyword + " " + RootMessage(ex));
        }

        return dropped;
    }

    /// <summary>龙1/2 特例：丢弃所有队员背包中的黑之记忆/白之记忆。</summary>
    private static void DropTeamMemoryItems()
    {
        StopTaskNavigation(false);
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            var cap = GetCaptainUid();
            if (!string.IsNullOrEmpty(cap))
            {
                uids.Add(cap);
            }
        }

        var total = 0;
        foreach (var uid in uids)
        {
            total += DropItemsByKeyword(uid, "黑之记忆");
            total += DropItemsByKeyword(uid, "白之记忆");
        }

        WriteLog("dragon drop memory total=" + total + " uids=" + uids.Count);
        if (total > 0)
        {
            Tip("已丢弃全员黑/白之记忆 " + total + " 件");
        }
    }

    /// <summary>龙3/4 特例：使用队长的记忆/意志道具（按关键字顺序优先），只用 1 件。返回是否已发包。</summary>
    private static bool UseCaptainMemoryItem(string[] keywords)
    {
        var cap = GetCaptainUid();
        if (string.IsNullOrEmpty(cap))
        {
            return false;
        }

        StopTaskNavigation(false);
        foreach (var keyword in keywords)
        {
            if (TryUseMemoryItem(cap, keyword))
            {
                Tip("已使用队长的" + keyword);
                return true;
            }
        }

        return false;
    }

    private static bool TryUseMemoryItem(string uid, string keyword, bool requireUseFlag = true)
    {
        try
        {
            var items = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (items == null)
            {
                return false;
            }

            var itemMgr = GetManagerInstance("ItemManager");
            if (itemMgr == null)
            {
                return false;
            }

            // SendUseItem(int x, int y, int haveitemindex, string uid, int toindex, int selectIndex, int useNum)
            MethodInfo use = null;
            foreach (var m in itemMgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendUseItem")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 4 && ps[0].ParameterType == typeof(int) && ps[3].ParameterType == typeof(string))
                {
                    use = m;
                    break;
                }
            }

            if (use == null)
            {
                return false;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null)
                {
                    continue;
                }

                if (requireUseFlag)
                {
                    var useFlag = Convert.ToInt32(GetMember(item, "useFlag") ?? 0);
                    if (useFlag != 1)
                    {
                        continue;
                    }
                }

                var data = GetMember(item, "data");
                var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                var secret = Convert.ToString(GetMember(data, "Secretname") ?? "") ?? "";
                if (name.IndexOf(keyword, StringComparison.Ordinal) < 0
                    && secret.IndexOf(keyword, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                // 取当前坐标
                var x = 0;
                var y = 0;
                TryGetPlayerXY(out x, out y);

                // 兼容不同参数个数（4/5/6/7 参）
                var ps = use.GetParameters();
                if (ps.Length >= 7)
                {
                    use.Invoke(itemMgr, new object[] { x, y, i, uid, 0, -1, 1 });
                }
                else if (ps.Length >= 4)
                {
                    use.Invoke(itemMgr, new object[] { x, y, i, uid });
                }

                WriteLog("use bag item uid=" + uid + " kw=" + keyword + " idx=" + i
                         + " name=" + name + " secret=" + secret);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            WriteLog("TryUseMemoryItem EX uid=" + uid + " kw=" + keyword + " " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>检查每个队员是否都至少有 1 个宠物空位（宠物栏固定 5 槽）。</summary>
    private static bool CheckAllPetSlotFree(out string failName)
    {
        failName = "";
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            var cap = GetCaptainUid();
            if (!string.IsNullOrEmpty(cap))
            {
                uids.Add(cap);
            }
        }

        var getPets = FindType("PlayerDataHolder")?.GetMethod(
            "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (getPets == null)
        {
            failName = "(反射缺失)";
            return false;
        }

        foreach (var uid in uids)
        {
            var pets = getPets.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (pets == null)
            {
                failName = uid;
                return false;
            }

            var used = 0;
            for (var i = 0; i < pets.Count; i++)
            {
                var p = pets[i];
                if (p == null)
                {
                    continue;
                }

                var useFlag = Convert.ToInt32(GetMember(p, "useFlag") ?? 0);
                if (useFlag == 1)
                {
                    used++;
                }
            }

            if (used >= pets.Count)
            {
                failName = uid;
                WriteLog("dragon pet full uid=" + uid + " used=" + used + "/" + pets.Count);
                return false;
            }
        }

        return true;
    }

    /// <summary>收集指定账号的宠物列表。</summary>
    private static System.Collections.IList GetPetListByUid(string uid)
    {
        try
        {
            var getPets = FindType("PlayerDataHolder")?.GetMethod(
                "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return getPets?.Invoke(null, new object[] { uid }) as System.Collections.IList;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析个人宠物仓库枚举值（BANK_TYPE.PERSONAL_BANK）。</summary>
    private static object ResolvePersonalBankType()
    {
        try
        {
            var t = FindType("BANK_TYPE");
            if (t == null || !t.IsEnum)
            {
                return null;
            }

            try
            {
                return Enum.Parse(t, "PERSONAL_BANK", ignoreCase: true);
            }
            catch
            {
                // fall through
            }

            foreach (var name in Enum.GetNames(t))
            {
                if (name.IndexOf("PERSONAL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Enum.Parse(t, name);
                }
            }

            var values = Enum.GetValues(t);
            return values.Length > 0 ? values.GetValue(0) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开远程个人宠物仓库（存仓前置，打不开不阻断后续存宠尝试）。</summary>
    private static void TryOpenRemotePersonalPetBank(string uid)
    {
        try
        {
            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr != null)
            {
                SetMember(roleMgr, "OpenBankFromPet", true);
            }

            var actMgr = GetManagerInstance("ActivityManager");
            if (actMgr == null)
            {
                return;
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
                if (ps.Length == 4
                    && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(string))
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
                return;
            }

            var ps2 = send.GetParameters();
            if (ps2.Length >= 4)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid, 0, 19 });
            }
            else if (ps2.Length == 3)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid, 0 });
            }
            else if (ps2.Length == 2)
            {
                send.Invoke(actMgr, new object[] { "远程个人宠物仓库", uid });
            }
        }
        catch
        {
            // 打不开远程仓也不阻断后续「存宠物」尝试
        }
    }

    /// <summary>
    /// 对所有宠物栏满的队员，把 1 级休息宠存到银行。返回是否有成功发包（存在可存的 1 级宠）。
    /// </summary>
    private static bool StoreLevelOnePetsForFull()
    {
        // 存宠前停官方导航，避免寻路与银行操作抢控制
        StopTaskNavigation(false);
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            var cap = GetCaptainUid();
            if (!string.IsNullOrEmpty(cap))
            {
                uids.Add(cap);
            }
        }

        var getPets = FindType("PlayerDataHolder")?.GetMethod(
            "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (getPets == null)
        {
            return false;
        }

        var roleMgr = GetManagerInstance("RoleManager");
        if (roleMgr == null)
        {
            return false;
        }

        var bankType = ResolvePersonalBankType();
        if (bankType == null)
        {
            return false;
        }

        MethodInfo sendBank = null;
        foreach (var m in roleMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendBankMessage")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length >= 4 && ps.Length <= 6)
            {
                sendBank = m;
                break;
            }
        }

        if (sendBank == null)
        {
            return false;
        }

        var anyStored = false;
        foreach (var uid in uids)
        {
            var pets = getPets.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (pets == null)
            {
                continue;
            }

            // 只处理宠物栏满的账号
            var used = 0;
            for (var i = 0; i < pets.Count; i++)
            {
                var p = pets[i];
                if (p == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(p, "useFlag") ?? 0) == 1)
                {
                    used++;
                }
            }

            if (used < pets.Count)
            {
                continue; // 没满，跳过
            }

            // 收集 1 级休息宠（记录宠物对象，存后本地置 useFlag=0，贴合抓宠存包逻辑）
            var storePets = new List<object>();
            for (var i = 0; i < pets.Count && i < 5; i++)
            {
                var pet = pets[i];
                if (pet == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var data = GetMember(pet, "data");
                if (data == null)
                {
                    continue;
                }

                var status = Convert.ToInt32(GetMember(data, "DepartureBattleStatus") ?? -1);
                if (status != PetStatusRest)
                {
                    continue;
                }

                var level = Convert.ToInt32(GetMember(data, "Level") ?? 0);
                if (level != StorePetLevel)
                {
                    continue;
                }

                storePets.Add(pet);
            }

            if (storePets.Count == 0)
            {
                continue; // 该账号无可存 1 级宠
            }

            TryOpenRemotePersonalPetBank(uid);

            foreach (var pet in storePets)
            {
                try
                {
                    var data = GetMember(pet, "data");
                    var index = Convert.ToInt32(GetMember(data, "Index") ?? 0);
                    var ps = sendBank.GetParameters();
                    object[] args;
                    if (ps.Length >= 6)
                    {
                        args = new object[] { bankType, uid, "存宠物", index, 0, null };
                    }
                    else if (ps.Length == 5)
                    {
                        args = new object[] { bankType, uid, "存宠物", index, 0 };
                    }
                    else
                    {
                        args = new object[] { bankType, uid, "存宠物", index };
                    }

                    sendBank.Invoke(roleMgr, args);
                    SetMember(pet, "useFlag", 0);
                    anyStored = true;
                    WriteLog("dragon store pet uid=" + uid + " idx=" + index);
                }
                catch (Exception ex)
                {
                    WriteLog("dragon store pet EX uid=" + uid + " " + RootMessage(ex));
                }
            }
        }

        return anyStored;
    }

    private static string FormatEscortStatus()
    {
        string state;
        if (_dragonLoopActive && _dragonPhase == 1)
        {
            state = "龙族循环：重置龙族纷争4中…（等待服务器）";
        }
        else if (_dragonLoopActive && _dragonPhase == 2)
        {
            state = "龙族循环：检查龙族纷争1-4是否可接…";
        }
        else if (_dragonLoopActive && _dragonPhase == 4)
        {
            state = "龙族循环：宠物位满，存1级宠物到银行中…";
        }
        else if (_escortActive && _escortPaused)
        {
            state = "已暂停（手动接管）#" + _escortMissionId + " " + GetEscortMissionTitleWithStep();
            if (_escortQueue.Count > 0 && _escortQueueIndex >= 0)
            {
                state += "｜" + (_escortQueueIndex + 1) + "/" + _escortQueue.Count;
            }

            state += "｜队列保留";
            if (!string.IsNullOrEmpty(_escortPauseReason))
            {
                state += "\n原因: " + _escortPauseReason;
            }

            if (_escortAlertRinging)
            {
                state += "\n⚠ 铃声提醒中 — 点「我知道了」或停止队列";
            }
        }
        else if (_escortActive)
        {
            if (_escortBetweenTasksWaitMs > 0)
            {
                var left = EscortBetweenTasksMs - (NowMs() - _escortBetweenTasksWaitMs);
                if (left < 0)
                {
                    left = 0;
                }

                state = "间隔等待 " + ((left + 999) / 1000) + "s → 下一项 #"
                        + (_escortQueueIndex >= 0 && _escortQueueIndex < _escortQueue.Count
                            ? _escortQueue[_escortQueueIndex].Id.ToString()
                            : "?");
            }
            else if (_escortAwaitingReadyMs > 0)
            {
                state = "等待可接 #" + _escortMissionId + " " + GetEscortMissionTitleWithStep() + "（重试中）";
            }
            else
            {
                state = "护航中 #" + _escortMissionId + " " + GetEscortMissionTitleWithStep();
                if (_escortWaitItem)
                {
                    state += string.IsNullOrEmpty(_escortWaitItemName)
                        ? "（遇敌中，等待任务道具…）"
                        : ("（遇敌中，等待获得" + _escortWaitItemName + "…）");
                }
                else if (_escort119TicketBankPending)
                {
                    var n = _escort119TicketBankUids.Count;
                    var i = _escort119TicketBankUidIndex + 1;
                    if (i < 1)
                    {
                        i = 1;
                    }

                    if (n > 0 && i > n)
                    {
                        i = n;
                    }

                    state += "（存兑换券 " + i + "/" + (n > 0 ? n : 1)
                             + " 连续失败 " + _escort119TicketBankFailStreak
                             + "/" + EscortTicketBankMaxFails + "）";
                }
                else if (IsCurrentEscortEncounterFarm())
                {
                    state += "（前往遇敌点…）";
                }
                else if (_escortFinishWaitMs > 0)
                {
                    state += IsDialoguePanelOpen() ? "（收尾点弹窗…）" : "（收尾确认中…）";
                }
            }

            if (_escortQueue.Count > 0 && _escortQueueIndex >= 0)
            {
                state += "｜" + (_escortQueueIndex + 1) + "/" + _escortQueue.Count;
            }
        }
        else if (_escortPicking)
        {
            state = "编辑队列中…";
        }
        else
        {
            state = "未启动";
        }

        var idleSec = 0;
        if (_escortActive && !_escortPaused && _lastActivityMs > 0 && _escortBetweenTasksWaitMs <= 0)
        {
            idleSec = (int)((NowMs() - _lastActivityMs) / 1000);
        }

        return "状态: " + state
               + "\nRunTaskId: " + GetRunTaskId()
               + "\n对话自动点: " + _dialogueAutoClicks + " 次"
               + "\n静止计时: " + idleSec + "s / 5s"
               + "\n本步骤恢复: " + _escortRecoverAttempts + " / " + EscortMaxRecoverFails
               + "（换步骤重置；卡楼梯：挪格后点任务）"
               + "\n" + GetEscortSpecialNote()
               + (_dragonLoopActive ? "\n龙族循环: 已循环 " + _dragonLoopCount + " 轮" : "")
               + (_midAutumnLoopActive ? "\n中秋循环: 已循环 " + _midAutumnLoopCount + " 轮" : "")
               + (_stuckResumePending ? "\n卡楼梯：恢复动作进行中…" : "");
    }

    /// <summary>
    /// 生成「特殊处理」标注：同时暴露识别结果与分流结果，便于排查。
    /// 丢/用道具只由任务本身决定（IsDragonMission），不再依赖龙族循环模式；分流按硬编码 ID。
    /// </summary>
    private static string GetEscortSpecialNote()
    {
        var id = _escortMissionId;
        if (id <= 0)
        {
            return "特殊处理: 无（无当前任务）";
        }

        if (!IsDragonMission(id))
        {
            if (id == MoonRabbitMissionId)
            {
                return "特殊处理: 中秋#119 "
                       + (_midAutumnGoralEdition ? "哥拉尔版(回登入点、不用赤凤之翼)" : "阿凯版(回登入点+赤凤之翼)")
                       + "；步骤7洞窟传送；步骤5布朗山；仅15000先取消回程再走15001；最后一步存兑换券";
            }

            return "特殊处理: 无 #" + id;
        }

        if (id == 110 || id == 111)
        {
            return "特殊处理: 开始丢道具（全员黑/白之记忆） #" + id;
        }

        if (id == 112)
        {
            return "特殊处理: 开始用道具（队长白/黑之记忆） #" + id;
        }

        if (id == 113)
        {
            return "特殊处理: 开始用道具（队长白之/黑之意志） #" + id;
        }

        return "特殊处理: 已识别龙族但ID不匹配（未丢未用） #" + id;
    }

    private static bool EscortQueueContains(int missionId)
    {
        for (var i = 0; i < _escortQueue.Count; i++)
        {
            if (_escortQueue[i].Id == missionId)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="appendMode">true=护航中追加，不中断当前护航。</param>
    private static void OpenEscortPicker(bool appendMode)
    {
        _escortPicking = true;
        _escortPage = 0;
        if (!appendMode)
        {
            _escortSearch = "";
        }

        WriteLog("escort picker open append=" + appendMode + " queue=" + _escortQueue.Count);
        Tip(appendMode ? "任务护航：点任务追加到队列末尾" : "任务护航：点任务加入队列（可含未接）");
    }

    private static void EnqueueEscortMission(int missionId, string title, string status)
    {
        try
        {
            if (missionId <= 0)
            {
                return;
            }

            if (EscortQueueContains(missionId))
            {
                Tip("任务护航：#" + missionId + " 已在队列中");
                return;
            }

            var mission = GetMissionDataById(missionId);
            if (mission == null)
            {
                Tip("任务护航：找不到任务 " + missionId);
                return;
            }

            var st = Convert.ToString(GetMember(mission, "taskstatus") ?? "") ?? "";
            if (st.EndsWith("Ended", StringComparison.Ordinal) || st == "2")
            {
                Tip("任务护航：任务已结束，无法入队");
                return;
            }

            _escortQueue.Add(new EscortCandidate
            {
                Id = missionId,
                Title = title ?? ("#" + missionId),
                Status = string.IsNullOrEmpty(status) ? "排队" : status
            });
            WriteLog("escort enqueue id=" + missionId + " title=" + title + " queue=" + _escortQueue.Count);
            Tip("任务护航：已入队 #" + missionId + "（队列 " + _escortQueue.Count + "）");
            RebuildEscortTab();
        }
        catch (Exception ex)
        {
            WriteLog("EnqueueEscortMission EX: " + RootMessage(ex));
            Tip("任务护航：入队失败");
        }
    }

    private static void RemoveEscortQueueAt(int index)
    {
        if (index < 0 || index >= _escortQueue.Count)
        {
            return;
        }

        if (_escortActive)
        {
            if (index < _escortQueueIndex)
            {
                Tip("任务护航：已完成项不可移除");
                return;
            }

            if (index == _escortQueueIndex)
            {
                Tip("任务护航：当前进行中不可移除，请先停止");
                return;
            }
        }

        var id = _escortQueue[index].Id;
        _escortQueue.RemoveAt(index);
        if (_escortActive && index < _escortQueueIndex)
        {
            _escortQueueIndex--;
        }

        WriteLog("escort dequeue id=" + id + " idx=" + index + " left=" + _escortQueue.Count);
        Tip("任务护航：已移出 #" + id);
        RebuildEscortTab();
    }

    private static void StartEscortQueue()
    {
        try
        {
            if (_escortActive)
            {
                if (_escortPaused)
                {
                    ResumeEscort();
                    return;
                }

                Tip("任务护航：已在护航中");
                return;
            }

            if (_escortQueue.Count == 0)
            {
                Tip("任务护航：队列为空，请先编辑队列");
                OpenEscortPicker(false);
                RebuildEscortTab();
                return;
            }

            // 从队列当前位置续开；全新则从 0
            var startIdx = _escortQueueIndex >= 0 && _escortQueueIndex < _escortQueue.Count
                ? _escortQueueIndex
                : 0;
            _escortPicking = false;
            _escortActive = true;
            _escortPaused = false;
            _escortPauseReason = "";
            _escortLastDiag = "";
            StopEscortAlertRing();
            _escortQueueIndex = startIdx;
            _escortBetweenTasksWaitMs = 0;
            _escortAwaitingReadyMs = 0;
            _escortFinishWaitMs = 0;
            _escortRecoverAttempts = 0;
            ResetEscortStuckState();
            _escortLastFloor = int.MinValue;
            _escortMapChangeAtMs = 0;
            _dialogueAutoClicks = 0;
            _prevRunTaskId = GetRunTaskId();
            WriteLog("escort queue start count=" + _escortQueue.Count + " idx=" + startIdx);
            Tip("任务护航：开始队列（" + _escortQueue.Count + " 项）");
            BeginEscortAtIndex(startIdx, "queue-start");
            RebuildEscortTab();
        }
        catch (Exception ex)
        {
            WriteLog("StartEscortQueue EX: " + RootMessage(ex));
            Tip("任务护航：启动失败");
            CancelEscort(false, "启动失败");
        }
    }

    /// <summary>暂停自动护航：不清队列，停导航与自动逻辑，便于手动接管。</summary>
    /// <param name="autoAlert">true=自动暂停并循环响铃；false=手动暂停不响铃。</param>
    private static void PauseEscort(string tipMsg, bool autoAlert = true)
    {
        if (!_escortActive || _escortPaused)
        {
            return;
        }

        _escortPaused = true;
        ClearEscortStuckPending();
        _escortAwaitingReadyMs = 0;
        if (_escortBetweenTasksWaitMs > 0)
        {
            _escortBetweenTasksWaitMs = 0;
        }

        if (!string.IsNullOrEmpty(tipMsg) && tipMsg.IndexOf("条件", StringComparison.Ordinal) >= 0)
        {
            _escortPauseReason = tipMsg;
        }
        else if (string.IsNullOrEmpty(_escortPauseReason))
        {
            _escortPauseReason = tipMsg ?? "已暂停";
        }

        StopEscortEncounterWait("pause", false);
        StopTaskNavigation();
        WriteLog("escort pause id=" + _escortMissionId + " idx=" + _escortQueueIndex
                 + " recoverFails=" + _escortRecoverAttempts + " tip=" + tipMsg
                 + " reason=" + _escortPauseReason + " autoAlert=" + autoAlert);
        Tip(string.IsNullOrEmpty(tipMsg) ? "任务护航：已暂停" : ("任务护航：" + tipMsg));
        if (autoAlert)
        {
            StartEscortAlertRing();
        }

        if (_visible && _tab == TabEscort)
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void StartEscortAlertRing()
    {
        _escortAlertRinging = true;
        _escortLastAlertRingMs = 0;
        PlayLevelOneAlertSe();
        _escortLastAlertRingMs = NowMs();
        WriteLog("escort alert ring start");
    }

    private static void StopEscortAlertRing()
    {
        if (!_escortAlertRinging)
        {
            return;
        }

        _escortAlertRinging = false;
        _escortLastAlertRingMs = 0;
        WriteLog("escort alert ring stop");
    }

    /// <summary>任务面板「我知道了」：只停铃，保持暂停与队列。</summary>
    private static void AcknowledgeEscortAlert()
    {
        StopEscortAlertRing();
        Tip("任务护航：已停止铃声（仍暂停中）");
        if (_visible && _tab == TabEscort)
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TickEscortAlertRing()
    {
        if (!_escortAlertRinging)
        {
            return;
        }

        var now = NowMs();
        if (_escortLastAlertRingMs > 0 && now - _escortLastAlertRingMs < AlertRingIntervalMs)
        {
            return;
        }

        PlayLevelOneAlertSe();
        _escortLastAlertRingMs = now;
    }

    /// <summary>播放遇敌 1 级提示铃 SE 476。</summary>
    private static bool PlayLevelOneAlertSe()
    {
        try
        {
            var audio = GetSingletonInstance("AudioUtil");
            if (audio == null)
            {
                WriteLog("PlayLevelOneAlertSe AudioUtil null");
                return false;
            }

            var play = audio.GetType().GetMethod(
                "PlaySE",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null);
            if (play == null)
            {
                WriteLog("PlayLevelOneAlertSe PlaySE missing");
                return false;
            }

            play.Invoke(audio, new object[] { LevelOneAlertSeId });
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("PlayLevelOneAlertSe EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>Singleton&lt;T&gt;.Instance（AudioUtil 等）。</summary>
    private static object GetSingletonInstance(string typeName)
    {
        try
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
                try
                {
                    var instProp = cur.GetProperty("Instance", flags);
                    var inst = instProp?.GetValue(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    var instField = cur.GetField("Instance", flags);
                    var inst = instField?.GetValue(null);
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

    /// <summary>从暂停继续：重新点当前任务导航。</summary>
    private static void ResumeEscort()
    {
        if (!_escortActive || !_escortPaused)
        {
            return;
        }

        _escortPaused = false;
        _escortPauseReason = "";
        _escortLastDiag = "";
        StopEscortAlertRing();
        // 用户点「继续」：清零本步骤恢复计数，重新开始卡楼梯检测
        _escortRecoverAttempts = 0;
        ResetEscortStuckState();
        _escortFinishWaitMs = 0;
        _escortAwaitingReadyMs = 0;
        _lastActivityMs = NowMs();
        if (TryGetPlayerXY(out var x, out var y))
        {
            _lastPosX = x;
            _lastPosY = y;
        }

        WriteLog("escort resume id=" + _escortMissionId + " idx=" + _escortQueueIndex
                 + " recover=" + _escortRecoverAttempts);
        Tip("任务护航：已继续");
        if (_escortMissionId > 0)
        {
            if (!ClickEscortTaskNav("resume"))
            {
                PauseEscortOnConditionFail("resume");
            }
        }
        else if (_escortQueueIndex >= 0 && _escortQueueIndex < _escortQueue.Count)
        {
            BeginEscortAtIndex(_escortQueueIndex, "resume");
        }

        if (_visible && _tab == TabEscort)
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>开始队列中指定下标的任务；失败则进入可接条件重试。</summary>
    private static bool BeginEscortAtIndex(int index, string reason)
    {
        if (index < 0 || index >= _escortQueue.Count)
        {
            FinishEscortQueue("队列护航完毕");
            return false;
        }

        _escortQueueIndex = index;
        var item = _escortQueue[index];
        _escortMissionId = item.Id;
        _escortMissionTitle = item.Title ?? ("#" + item.Id);
        _stuckResumePending = false;
        _escortFinishWaitMs = 0;
        _escortBetweenTasksWaitMs = 0;
        _escortRecoverAttempts = 0;
        _escortLastStepNum = -1;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("begin", false);
        _lastActivityMs = NowMs();
        if (TryGetPlayerXY(out var x, out var y))
        {
            _lastPosX = x;
            _lastPosY = y;
        }

        // 龙族任务特例：龙1/2 丢弃全员记忆；龙3 使用队长记忆；龙4 使用队长意志
        if (IsDragonMission(_escortMissionId))
        {
            if (_escortMissionId == 110 || _escortMissionId == 111)
            {
                DropTeamMemoryItems();
            }
            else if (_escortMissionId == 112 || _escortMissionId == 113)
            {
                var useKeywords = _escortMissionId == 112
                    ? new[] { "白之记忆", "黑之记忆" }
                    : new[] { "白之意志", "黑之意志" };
                if (UseCaptainMemoryItem(useKeywords))
                {
                    _dragonUseMemoryPending = true;
                    _dragonUseMemoryAtMs = NowMs();
                    WriteLog("dragon use memory pending id=" + _escortMissionId);
                    return true; // 已受理，TickEscort 等待后继续
                }

                WriteLog("dragon use memory none id=" + _escortMissionId + "（无可使用道具，继续点任务）");
            }
        }

        if (TempMidAutumnEscort119 && _escortMissionId == MoonRabbitMissionId)
        {
            try
            {
                var sn = GetEscortMissionStepNum();
                _escortLastStepNum = sn;
                if (TryStartMoonRabbitStepSpecial(sn, "begin")
                    || TryStartMoonRabbitBrownTeleport(sn, "begin")
                    || TryStartMoonRabbitReefTeleport(sn, "begin")
                    || TryStartMoonRabbitLastStepBank(sn, "begin"))
                {
                    _escortAwaitingReadyMs = 0;
                    _escortLastDiag = "";
                    WriteLog("escort begin idx=" + index + " id=" + _escortMissionId
                             + " title=" + _escortMissionTitle + " 119-special step=" + sn);
                    Tip("任务护航：(" + (index + 1) + "/" + _escortQueue.Count + ") #" + _escortMissionId);
                    return true;
                }
            }
            catch
            {
                // ignore，走普通点任务
            }
        }

        if (!ClickEscortTaskNav(reason))
        {
            PauseEscortOnConditionFail(reason);
            return false;
        }

        _escortAwaitingReadyMs = 0;
        _escortLastDiag = "";
        WriteLog("escort begin idx=" + index + " id=" + _escortMissionId + " title=" + _escortMissionTitle);
        Tip("任务护航：(" + (index + 1) + "/" + _escortQueue.Count + ") #" + _escortMissionId);
        return true;
    }

    /// <summary>点任务失败：诊断步骤条件并暂停（保留队列）。</summary>
    private static void PauseEscortOnConditionFail(string reason)
    {
        if (!_escortActive)
        {
            return;
        }

        var diag = _escortLastDiag;
        if (string.IsNullOrEmpty(diag))
        {
            try
            {
                var mission = GetMissionDataById(_escortMissionId);
                diag = DiagnoseEscortStepFail(mission);
            }
            catch (Exception ex)
            {
                diag = "诊断异常:" + RootMessage(ex);
            }
        }

        if (string.IsNullOrEmpty(diag))
        {
            diag = "条件不满足";
        }

        _escortAwaitingReadyMs = 0;
        _escortPauseReason = diag;
        WriteLog("escort condition-fail pause id=" + _escortMissionId
                 + " reason=" + reason + " diag=" + diag);
        // 可能已在暂停态（重复失败）；强制刷新原因并确保响铃
        if (_escortPaused)
        {
            StartEscortAlertRing();
            Tip("任务护航：条件仍未满足 — " + diag);
            if (_visible && _tab == TabEscort)
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        PauseEscort("条件未满足已暂停：" + diag, true);
    }

    private static void OnEscortMissionCompleted()
    {
        var doneId = _escortMissionId;
        WriteLog("escort done missionId=" + doneId + " idx=" + _escortQueueIndex);
        StopEscortEncounterWait("mission-done", false);
        StopTaskNavigation();
        _stuckResumePending = false;
        _escortFinishWaitMs = 0;
        _escortAwaitingReadyMs = 0;
        _escortRecoverAttempts = 0;
        _escortMissionId = -1;
        _escortMissionTitle = "";

        var next = _escortQueueIndex + 1;
        if (next >= _escortQueue.Count)
        {
            if (TempMidAutumnEscort119 && _midAutumnLoopActive)
            {
                if (!EnsureCaptainHasWingOrStopLoop("mission-complete"))
                {
                    return;
                }

                _midAutumnLoopCount++;
                ResetMoonRabbitEscortFlags();
                EnqueueMidAutumnMission();
                _escortQueueIndex = 0;
                _escortBetweenTasksWaitMs = NowMs();
                Tip("中秋循环：第 " + _midAutumnLoopCount + " 轮完成，5 秒后继续");
                WriteLog("mid-autumn loop next round count=" + _midAutumnLoopCount);
                if (_visible && _tab == TabEscort)
                {
                    try
                    {
                        RebuildEscortTab();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }

            FinishEscortQueue("队列护航完毕");
            return;
        }

        _escortQueueIndex = next;
        _escortBetweenTasksWaitMs = NowMs();
        Tip("任务护航：#" + doneId + " 完成，5 秒后下一任务 ("
            + (next + 1) + "/" + _escortQueue.Count + ")");
        WriteLog("escort between wait nextIdx=" + next + " nextId=" + _escortQueue[next].Id);
        if (_visible && _tab == TabEscort)
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>清理护航运行状态（清空队列、重置状态、可选停导航），不改变龙族循环标志。</summary>
    private static void CleanupEscortRuntime(bool stopNav)
    {
        var wasActive = _escortActive;
        _escortPicking = false;
        _escortActive = false;
        _escortPaused = false;
        _escortPauseReason = "";
        _escortLastDiag = "";
        StopEscortAlertRing();
        _escortMissionId = -1;
        _escortMissionTitle = "";
        _escortQueueIndex = -1;
        _escortBetweenTasksWaitMs = 0;
        _escortAwaitingReadyMs = 0;
        _escortRecoverAttempts = 0;
        _stuckResumePending = false;
        _escortFinishWaitMs = 0;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("cleanup", false);
        _escortQueue.Clear();
        _prevRunTaskId = GetRunTaskId();
        if (stopNav && wasActive)
        {
            StopTaskNavigation();
        }
    }

    private static void FinishEscortQueue(string tipMsg)
    {
        // 龙族循环：队列完成后不停止，检查宠物空位；满则存 1 级宠到银行腾位，仍满才停
        if (_dragonLoopActive)
        {
            _dragonLoopCount++;
            CleanupEscortRuntime(true);

            if (CheckAllPetSlotFree(out var petFailUid))
            {
                WriteLog("dragon loop next round: count=" + _dragonLoopCount);
                BeginDragonNextRound("龙族循环第 " + _dragonLoopCount + " 轮完成，准备下一轮…");
            }
            else
            {
                // 宠物位满：尝试存 1 级宠到银行腾位，等空位后再继续循环
                _dragonStoreRetries = 0;
                _dragonStoreRechecks = 0;
                var stored = StoreLevelOnePetsForFull();
                if (stored)
                {
                    WriteLog("dragon loop store pets: count=" + _dragonLoopCount);
                    Tip("龙族循环：宠物位满，正在存1级宠物到银行…");
                    _dragonPhase = 4;
                    _dragonPhaseAtMs = NowMs();
                }
                else
                {
                    WriteLog("dragon loop stop: 宠物位满且无可存1级宠 uid=" + petFailUid + " count=" + _dragonLoopCount);
                    Tip("龙族循环停止：队员宠物位满且无1级宠可存包，共循环 " + _dragonLoopCount + " 轮");
                    _dragonLoopActive = false;
                    _dragonPhase = 0;
                }
            }

            if (_visible && _tab == TabEscort && _canvasGo != null && !IsUnityNull(_canvasGo))
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        var was = _escortActive || _escortPicking;
        var wasActive = _escortActive;
        _escortPicking = false;
        _escortActive = false;
        _escortPaused = false;
        _escortPauseReason = "";
        _escortLastDiag = "";
        StopEscortAlertRing();
        _escortMissionId = -1;
        _escortMissionTitle = "";
        _escortQueueIndex = -1;
        _escortBetweenTasksWaitMs = 0;
        _escortAwaitingReadyMs = 0;
        _escortRecoverAttempts = 0;
        _stuckResumePending = false;
        _escortFinishWaitMs = 0;
        _escortQueue.Clear();
        _prevRunTaskId = GetRunTaskId();
        if (wasActive)
        {
            StopTaskNavigation();
        }

        if (was)
        {
            WriteLog("escort queue finish tip=" + tipMsg);
            Tip(string.IsNullOrEmpty(tipMsg) ? "任务护航：队列结束" : ("任务护航：" + tipMsg));
            if (_visible && _tab == TabEscort && _canvasGo != null && !IsUnityNull(_canvasGo))
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>停止护航并清空队列（停止按钮 / ESC）。</summary>
    private static void CancelEscort(bool stopNav, string tipMsg = null)
    {
        // 若龙族循环激活，一并停止循环标志
        if (_dragonLoopActive)
        {
            _dragonLoopActive = false;
            _dragonPhase = 0;
            WriteLog("dragon loop stop via cancel count=" + _dragonLoopCount);
        }

        if (_midAutumnLoopActive)
        {
            WriteLog("mid-autumn loop stop via cancel count=" + _midAutumnLoopCount);
            _midAutumnLoopActive = false;
        }

        // 用记忆等待标志无条件清理（普通护航龙3/4 也可能置位）
        _dragonUseMemoryPending = false;

        var was = _escortActive || _escortPicking || _escortQueue.Count > 0;
        var id = _escortMissionId;
        var wasActive = _escortActive;
        var qCount = _escortQueue.Count;
        _escortPicking = false;
        _escortActive = false;
        _escortPaused = false;
        _escortPauseReason = "";
        _escortLastDiag = "";
        StopEscortAlertRing();
        _escortMissionId = -1;
        _escortMissionTitle = "";
        _escortQueueIndex = -1;
        _escortBetweenTasksWaitMs = 0;
        _escortAwaitingReadyMs = 0;
        _escortRecoverAttempts = 0;
        _stuckResumePending = false;
        _escortFinishWaitMs = 0;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("cancel", false);
        _escortQueue.Clear();
        _prevRunTaskId = GetRunTaskId();
        if (stopNav && wasActive)
        {
            StopTaskNavigation();
        }

        if (was)
        {
            WriteLog("escort cancel id=" + id + " stopNav=" + stopNav + " clearedQueue=" + qCount);
            Tip(string.IsNullOrEmpty(tipMsg) ? "任务护航已取消，队列已清空" : ("任务护航：" + tipMsg));
            if (_visible && _tab == TabEscort && _canvasGo != null && !IsUnityNull(_canvasGo))
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    // ---------------- 龙族纷争循环状态机 ----------------

    /// <summary>启动龙族循环 A 线（110-113）。</summary>
    private static void StartDragonLoop()
    {
        StartDragonLoopCore(DragonMissionIds, "A");
    }

    /// <summary>启动中秋 #119 循环。goralEdition=true 哥拉尔版（不用赤凤之翼）。</summary>
    private static void StartMidAutumnLoop(bool goralEdition)
    {
        try
        {
            if (!TempMidAutumnEscort119)
            {
                Tip("中秋循环未启用");
                return;
            }

            if (_midAutumnLoopActive)
            {
                var ed = _midAutumnGoralEdition ? "哥拉尔" : "阿凯";
                Tip("中秋循环：已在运行中（" + ed + "版 第 " + (_midAutumnLoopCount + 1) + " 轮）");
                return;
            }

            if (_dragonLoopActive)
            {
                StopDragonLoop();
            }
            else if (_escortActive || _escortPicking || _escortQueue.Count > 0)
            {
                CancelEscort(true, "已切换到中秋循环");
            }

            if (!goralEdition && !CaptainHasMoonRabbitWing())
            {
                Tip("中秋阿凯版：队长背包没有赤凤之翼");
                WriteLog("mid-autumn loop abort no 赤凤之翼");
                return;
            }

            _midAutumnGoralEdition = goralEdition;
            _midAutumnLoopActive = true;
            _midAutumnLoopCount = 0;
            EnqueueMidAutumnMission();
            WriteLog("mid-autumn loop start id=" + MoonRabbitMissionId
                     + " edition=" + (goralEdition ? "goral" : "akai"));
            Tip(goralEdition
                ? "中秋哥拉尔版：开始月宫救兔（不用赤凤之翼）"
                : "中秋阿凯版：开始月宫救兔");
            StartEscortQueue();
            if (_visible && _tab == TabEscort)
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("StartMidAutumnLoop EX: " + RootMessage(ex));
            Tip("中秋循环：启动失败");
            _midAutumnLoopActive = false;
        }
    }

    private static void StopMidAutumnLoop()
    {
        if (!_midAutumnLoopActive)
        {
            return;
        }

        var n = _midAutumnLoopCount;
        var ed = _midAutumnGoralEdition ? "哥拉尔" : "阿凯";
        _midAutumnLoopActive = false;
        WriteLog("mid-autumn loop manual stop count=" + n + " edition=" + ed);
        CancelEscort(true, "中秋" + ed + "版已停止（共 " + n + " 轮）");
    }

    private static void EnqueueMidAutumnMission()
    {
        var title = "月宫救兔";
        try
        {
            var mission = GetMissionDataById(MoonRabbitMissionId);
            if (mission != null)
            {
                var t = Convert.ToString(GetMember(mission, "title") ?? "") ?? "";
                if (!string.IsNullOrEmpty(t))
                {
                    title = t;
                }
            }
        }
        catch
        {
            // ignore
        }

        _escortQueue.Clear();
        _escortQueue.Add(new EscortCandidate
        {
            Id = MoonRabbitMissionId,
            Title = title,
            Status = "循环"
        });
        _escortQueueIndex = 0;
    }

    /// <summary>启动龙族循环（重置龙4 → 判断可接 → 顺序执行任务集，循环直到宠物位满/手动停）。</summary>
    private static void StartDragonLoopCore(int[] missionIds, string line)
    {
        try
        {
            if (_dragonLoopActive)
            {
                Tip("龙族循环：已在运行中（第 " + (_dragonLoopCount + 1) + " 轮）");
                return;
            }

            // 若普通护航在跑，先停
            if (_escortActive || _midAutumnLoopActive)
            {
                CancelEscort(true, "已切换到龙族循环");
            }

            _dragonLoopActive = true;
            _dragonMissionIds = missionIds;
            _dragonLoopCount = 0;
            _dragonPhase = 1;
            _dragonPhaseAtMs = NowMs();
            _dragonUseMemoryPending = false;
            _dragonCheckRetries = 0;
            _dragonStoreRetries = 0;
            _dragonStoreRechecks = 0;
            WriteLog("dragon loop start line=" + line);
            Tip("龙族循环" + line + "线：开始，先重置龙族纷争4…");
            ResetDragon4ForAll();
            if (_visible && _tab == TabEscort)
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("StartDragonLoop EX: " + RootMessage(ex));
            Tip("龙族循环：启动失败");
            _dragonLoopActive = false;
            _dragonPhase = 0;
        }
    }

    /// <summary>停止龙族循环。</summary>
    private static void StopDragonLoop()
    {
        if (!_dragonLoopActive)
        {
            return;
        }

        _dragonLoopActive = false;
        _dragonPhase = 0;
        _dragonUseMemoryPending = false;
        WriteLog("dragon loop manual stop count=" + _dragonLoopCount);
        Tip("龙族循环已停止（共 " + _dragonLoopCount + " 轮）");
        if (_escortActive)
        {
            CancelEscort(true, "龙族循环已停止");
        }
    }

    /// <summary>龙族循环进入下一轮：重置龙4 → phase1 等待 → 检查可接 → 再跑队列。</summary>
    private static void BeginDragonNextRound(string tip)
    {
        if (!string.IsNullOrEmpty(tip))
        {
            Tip(tip);
        }

        _dragonPhase = 1;
        _dragonPhaseAtMs = NowMs();
        _dragonCheckRetries = 0;
        _dragonStoreRetries = 0;
        _dragonStoreRechecks = 0;
        _dragonUseMemoryPending = false;
        ResetDragon4ForAll();
        TryRebuildEscortTab();
    }

    /// <summary>龙族循环 phase 1/2/4：重置等待 → 判断可接 → 入队执行；phase4=存宠后等空位再继续。</summary>
    private static void TickDragonLoopPrepare()
    {
        var now = NowMs();
        if (_dragonPhase == 1)
        {
            if (now - _dragonPhaseAtMs < DragonResetDelayMs)
            {
                return;
            }

            _dragonPhase = 2;
            _dragonPhaseAtMs = now;
            return;
        }

        if (_dragonPhase == 2)
        {
            if (!CheckDragonMissionsReady(out var failReason))
            {
                // 重置回包可能滞后，重试等待若干次再终止（避免「已完成」误判）
                if (_dragonCheckRetries < DragonCheckMaxRetries)
                {
                    if (now - _dragonPhaseAtMs < DragonCheckRetryMs)
                    {
                        return;
                    }

                    _dragonCheckRetries++;
                    _dragonPhaseAtMs = now;
                    WriteLog("dragon loop check retry=" + _dragonCheckRetries + " fail=" + failReason);
                    return;
                }

                WriteLog("dragon loop check fail: " + failReason);
                Tip("龙族循环终止：" + failReason);
                _dragonLoopActive = false;
                _dragonPhase = 0;
                _dragonCheckRetries = 0;
                if (_visible && _tab == TabEscort)
                {
                    try
                    {
                        RebuildEscortTab();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return;
            }

            _dragonCheckRetries = 0;

            // 构建队列（当前 A/B 线任务集）
            _escortQueue.Clear();
            var ids = _dragonMissionIds ?? DragonMissionIds;
            foreach (var id in ids)
            {
                var mission = GetMissionDataById(id);
                var title = mission != null
                    ? (Convert.ToString(GetMember(mission, "title") ?? "") ?? "")
                    : "";
                _escortQueue.Add(new EscortCandidate
                {
                    Id = id,
                    Title = string.IsNullOrEmpty(title) ? ("#" + id) : title,
                    Status = "排队"
                });
            }

            _dragonPhase = 3;
            _escortPicking = false;
            _escortActive = true;
            _escortPaused = false;
            _escortPauseReason = "";
            _escortLastDiag = "";
            StopEscortAlertRing();
            _escortQueueIndex = 0;
            _escortBetweenTasksWaitMs = 0;
            _escortAwaitingReadyMs = 0;
            _escortRecoverAttempts = 0;
            ResetEscortStuckState();
            _escortFinishWaitMs = 0;
            _escortLastStepNum = -1;
            _lastActivityMs = now;
            _prevRunTaskId = GetRunTaskId();
            WriteLog("dragon loop run round=" + (_dragonLoopCount + 1) + " queue=" + _escortQueue.Count);
            Tip("龙族循环：开始第 " + (_dragonLoopCount + 1) + " 轮（" + _escortQueue.Count + " 项）");
            BeginEscortAtIndex(0, "dragon-start");
            if (_visible && _tab == TabEscort)
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        if (_dragonPhase == 4)
        {
            // 存包腾位：等银行回包 → 全员有空位则继续循环；否则再存/再等，不因一次复检失败就停
            if (now - _dragonPhaseAtMs < DragonStoreWaitMs)
            {
                return;
            }

            if (CheckAllPetSlotFree(out var failUid2))
            {
                WriteLog("dragon loop store ok, next round count=" + _dragonLoopCount + " recheck=" + _dragonStoreRechecks);
                BeginDragonNextRound("龙族循环：存包完成，全员有空位，继续循环…");
                return;
            }

            _dragonStoreRechecks++;

            // 仍满：优先再发一次存宠；发不出也不立刻停，继续等空位（回包滞后）
            if (_dragonStoreRetries < DragonStoreMaxRetries)
            {
                var stored = StoreLevelOnePetsForFull();
                if (stored)
                {
                    _dragonStoreRetries++;
                    WriteLog("dragon loop store retry=" + _dragonStoreRetries + " recheck=" + _dragonStoreRechecks);
                    Tip("龙族循环：宠物仍满，继续存1级宠到银行…");
                    _dragonPhaseAtMs = now;
                    return;
                }
            }

            if (_dragonStoreRechecks < DragonStoreMaxRechecks)
            {
                WriteLog("dragon loop store wait free uid=" + failUid2
                    + " recheck=" + _dragonStoreRechecks + "/" + DragonStoreMaxRechecks);
                _dragonPhaseAtMs = now;
                return;
            }

            WriteLog("dragon loop stop: 存包后仍满 uid=" + failUid2
                + " retries=" + _dragonStoreRetries + " rechecks=" + _dragonStoreRechecks
                + " count=" + _dragonLoopCount);
            Tip("龙族循环停止：宠物位满且存包后仍无空位，共循环 " + _dragonLoopCount + " 轮");
            _dragonLoopActive = false;
            _dragonPhase = 0;
            TryRebuildEscortTab();
        }
    }

    private static void TickEscort()
    {
        if (IsEscapeDown() && (_escortPicking || _escortActive || _escortPaused
            || _dragonLoopActive || _midAutumnLoopActive))
        {
            if (_dragonLoopActive)
            {
                StopDragonLoop();
                return;
            }

            if (_midAutumnLoopActive)
            {
                StopMidAutumnLoop();
                return;
            }

            // 护航中（含暂停）编辑队列时：ESC 只关编辑，不清队列
            if (_escortPicking && _escortActive)
            {
                _escortPicking = false;
                RebuildEscortTab();
                Tip("任务护航：已关闭队列编辑");
                return;
            }

            // 未护航仅编辑：ESC 关闭并清空队列
            if (_escortPicking && !_escortActive)
            {
                CancelEscort(false, "已取消，队列已清空");
                return;
            }

            CancelEscort(true, "已按 ESC 取消，队列已清空");
            return;
        }

        if (_dragonLoopActive && (_dragonPhase == 1 || _dragonPhase == 2 || _dragonPhase == 4))
        {
            TickDragonLoopPrepare();
            return;
        }

        if (!_escortActive)
        {
            return;
        }

        // 暂停：不自动点对话 / 不推进队列 / 不卡楼梯；允许手动接管（点其它任务不终止）
        if (_escortPaused)
        {
            return;
        }

        var now = NowMs();
        var skipMoonRabbitLastDialogue = TempMidAutumnEscort119 && _midAutumnLoopActive
            && _escortMissionId == MoonRabbitMissionId
            && (_escort119TicketBankPending || GetEscortMissionStepNum() == MoonRabbitLastStep);
        if (!skipMoonRabbitLastDialogue)
        {
            TryAutoPickDialogue();
        }

        // 龙3/4 使用记忆后等待服务器处理，再点任务
        if (_dragonUseMemoryPending)
        {
            if (now - _dragonUseMemoryAtMs < DragonUseMemoryDelayMs)
            {
                return;
            }

            _dragonUseMemoryPending = false;
            if (!ClickEscortTaskNav("dragon-use-memory"))
            {
                PauseEscortOnConditionFail("dragon-use-memory");
            }

            return;
        }

        if (TempMidAutumnEscort119 && _escortLoginGatePending)
        {
            // 回登入点后立刻掐掉任务导航；切到阿凯鲁法后若仍在寻路，会继续点回城去哥拉尔。
            StopTaskNavigation(false);
            if (now - _escortLoginGateAtMs < EscortLoginGateWaitMs)
            {
                return;
            }

            _escortLoginGatePending = false;
            if (_escort119PendingAfterGateStep == MoonRabbitLoginGateStep2)
            {
                // 哥拉尔版：登入点已在哥拉尔，回登入点后直接点任务，不用赤凤之翼
                if (_midAutumnGoralEdition)
                {
                    if (!ClickEscortTaskNav("119-after-logingate-goral"))
                    {
                        PauseEscortOnConditionFail("119-after-logingate-goral");
                    }

                    return;
                }

                StopTaskNavigation();
                if (UseCaptainBagItem(MoonRabbitWingKeyword, false))
                {
                    _escortUseItemPending = true;
                    _escortUseItemAtMs = now;
                    ResetWingWizardState();
                    WriteLog("119 use 赤凤之翼 after login-gate");
                    return;
                }

                Tip("任务护航：队长背包没有赤凤之翼");
                _escortLastDiag = "队长背包没有赤凤之翼";
                PauseEscortOnConditionFail("119-red-phoenix-wing");
                return;
            }

            if (!ClickEscortTaskNav("119-after-logingate"))
            {
                PauseEscortOnConditionFail("119-after-logingate");
            }

            return;
        }

        if (TempMidAutumnEscort119 && _escortUseItemPending)
        {
            var wingWait = TickWingWizardProgress(_escortUseItemAtMs);
            if (wingWait == 0)
            {
                _escortLastDiag = "赤凤之翼弹窗（下一步/确定）";
                return;
            }

            _escortUseItemPending = false;
            _escortLastDiag = "";
            if (wingWait < 0)
            {
                Tip("任务护航：赤凤之翼弹窗未点完");
                PauseEscortOnConditionFail("119-wing-wizard-timeout");
                return;
            }

            if (wingWait == 2)
            {
                WriteLog("119 wing wizard never opened, continue task");
            }

            if (!ClickEscortTaskNav("119-after-use-item"))
            {
                PauseEscortOnConditionFail("119-after-use-item");
            }

            return;
        }

        if (TempMidAutumnEscort119 && _escortHangupTeleportPending)
        {
            var arrived = false;
            try
            {
                int floor;
                string floorName;
                int mapResId;
                if (TryGetCurrentMapInfo(out floor, out floorName, out mapResId)
                    && _escortHangupTeleportExpectFloor != 0)
                {
                    arrived = floor == _escortHangupTeleportExpectFloor;
                }
            }
            catch
            {
                // ignore
            }

            if (!arrived && now - _escortHangupTeleportAtMs < EscortHangupTeleportWaitMs)
            {
                return;
            }

            _escortHangupTeleportPending = false;
            _escortHangupTeleportExpectFloor = 0;
            if (!ClickEscortTaskNav("119-after-hangup-teleport"))
            {
                PauseEscortOnConditionFail("119-after-hangup-teleport");
            }

            return;
        }

        if (TempMidAutumnEscort119 && _escort119TicketBankPending)
        {
            TickMoonRabbitTicketBank(now);
            return;
        }

        if (TempMidAutumnEscort119 && _escortMissionId == MoonRabbitMissionId
            && !_escortHangupTeleportPending && !_escortLoginGatePending && !_escortUseItemPending
            && !_escort119TicketBankPending)
        {
            try
            {
                var sn = GetEscortMissionStepNum();
                if (TryStartMoonRabbitBrownTeleport(sn, "tick")
                    || TryStartMoonRabbitReefTeleport(sn, "tick")
                    || TryStartMoonRabbitLastStepBank(sn, "tick"))
                {
                    return;
                }
            }
            catch
            {
                // ignore
            }
        }

        // 任务间 5 秒间隔
        if (_escortBetweenTasksWaitMs > 0)
        {
            if (now - _escortBetweenTasksWaitMs < EscortBetweenTasksMs)
            {
                return;
            }

            _escortBetweenTasksWaitMs = 0;
            BeginEscortAtIndex(_escortQueueIndex, "queue-next");
            if (_visible && _tab == TabEscort)
            {
                try
                {
                    RebuildEscortTab();
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        if (_escortMissionId <= 0)
        {
            if (_escortQueueIndex >= 0 && _escortQueueIndex < _escortQueue.Count)
            {
                BeginEscortAtIndex(_escortQueueIndex, "queue-recover");
            }

            return;
        }

        // 兼容旧状态：若仍在 await-ready，失败则诊断暂停（不再无限重试）
        if (_escortAwaitingReadyMs > 0)
        {
            if (IsMissionEnded(_escortMissionId))
            {
                WriteLog("escort skip already ended id=" + _escortMissionId);
                OnEscortMissionCompleted();
                return;
            }

            _escortAwaitingReadyMs = 0;
            if (!ClickEscortTaskNav("await-ready"))
            {
                PauseEscortOnConditionFail("await-ready");
            }

            return;
        }

        var runId = GetRunTaskId();
        // 中途点了其它任务 → 终止整队并清队列（自己续航同一 ID 不终止）
        if (runId > 0 && runId != _escortMissionId)
        {
            WriteLog("escort abort foreign RunTaskId=" + runId + " escort=" + _escortMissionId);
            CancelEscort(true, "点了其它任务，已终止并清空队列");
            return;
        }

        _prevRunTaskId = runId;

        var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        var dialogueOpen = IsDialoguePanelOpen();

        // 任务已完成：有弹窗则继续点到消失；无弹窗再等一小段，然后进队间隔
        if (IsMissionEnded(_escortMissionId))
        {
            StopEscortEncounterWait("mission-ended", false);
            ClearEscortStuckPending();
            _escortRecoverAttempts = 0;
            if (dialogueOpen)
            {
                if (_escortFinishWaitMs == 0)
                {
                    WriteLog("escort finish wait dialog missionId=" + _escortMissionId);
                }

                _escortFinishWaitMs = now;
                _lastActivityMs = now;
                return;
            }

            if (_escortFinishWaitMs == 0)
            {
                _escortFinishWaitMs = now;
                WriteLog("escort finish grace missionId=" + _escortMissionId);
                return;
            }

            if (now - _escortFinishWaitMs < EscortFinishGraceMs)
            {
                return;
            }

            OnEscortMissionCompleted();
            return;
        }

        _escortFinishWaitMs = 0;

        if (dialogueOpen || inBattle)
        {
            _lastActivityMs = now;
        }

        if (TryGetPlayerXY(out var x, out var y))
        {
            if (x != _lastPosX || y != _lastPosY)
            {
                _lastPosX = x;
                _lastPosY = y;
                _lastActivityMs = now;
                // 有位移只刷新静止计时；不重置卡图阶段（任务导航走动也会触发位移）。
                // 换步骤 / 手动继续 才 ResetEscortStuckState。
            }
        }

        // 切图：只刷新静止计时。续航由官方完成；119 在 15000(22,33) 续不上走特例。
        if (TryGetCurrentMapInfo(out var curFloor, out _, out _))
        {
            if (_escortLastFloor == int.MinValue)
            {
                _escortLastFloor = curFloor;
            }
            else if (curFloor != _escortLastFloor)
            {
                WriteLog("escort floor change " + _escortLastFloor + "->" + curFloor
                         + " id=" + _escortMissionId);
                _escortLastFloor = curFloor;
                _escortMapChangeAtMs = now;
                _lastActivityMs = now;
                ClearEscortStuckPending();
                // 15000 挪格特例：整步只做一次，离图不重置 Done
                if (_escort119WarpUnstickPhase != 0 && curFloor != MoonRabbitWarpStuckFloor)
                {
                    _escort119WarpUnstickPhase = 0;
                }
            }
        }

        // 中秋 #119 仅此一处导航特例：15000(22,33)
        if (TryTickMoonRabbitWarpUnstick(now))
        {
            return;
        }

        // 切图后短 settle：等官方自己续上（切图已刷过静止计时，这里不再刷）
        if (_escortMapChangeAtMs > 0 && now - _escortMapChangeAtMs < EscortMapChangeSettleMs)
        {
            return;
        }

        // 子任务步骤变化（missionStepNum 改变）→ 本步骤恢复计数清零，重新开始卡楼梯检测
        var stepNum = GetEscortMissionStepNum();
        if (stepNum >= 0 && stepNum != _escortLastStepNum)
        {
            var prevStep = _escortLastStepNum;
            if (prevStep >= 0 && _escortRecoverAttempts > 0)
            {
                WriteLog("escort step changed id=" + _escortMissionId
                         + " step=" + stepNum + " (was " + prevStep + ")"
                         + " recover reset");
            }

            _escortLastStepNum = stepNum;
            _escortRecoverAttempts = 0;
            ResetEscortStuckState();
            _escort119WarpUnstickDone = false;
            _escort119WarpUnstickPhase = 0;
            _escort119WarpUnstickClicks = 0;
            _lastActivityMs = now;

            if (TempMidAutumnEscort119 && _escortMissionId == MoonRabbitMissionId && prevStep >= 0)
            {
                if (TryStartMoonRabbitStepSpecial(stepNum, "step-change")
                    || TryStartMoonRabbitBrownTeleport(stepNum, "step-change")
                    || TryStartMoonRabbitReefTeleport(stepNum, "step-change")
                    || TryStartMoonRabbitLastStepBank(stepNum, "step-change"))
                {
                    WriteLog("119 special on step-change " + prevStep + "->" + stepNum);
                    return;
                }
            }

            if (_escortWaitItem && prevStep >= 0 && stepNum != _escortWaitAtStepNum)
            {
                StopEscortEncounterWait("step-changed", true);
                if (!ClickEscortTaskNav("encounter-step-done"))
                {
                    PauseEscortOnConditionFail("encounter-step-done");
                }

                return;
            }
        }

        if (inBattle)
        {
            if (_escortWaitItem)
            {
                _escortWasInBattle = true;
            }

            ClearEscortStuckPending();
            return;
        }

        if (_escortWaitItem)
        {
            if (_escortWasInBattle)
            {
                _escortWasInBattle = false;
                if (EscortEncounterHasTargetItem() || CanEscortLeaveEncounterWait())
                {
                    StopEscortEncounterWait("battle-got-item", true);
                    if (!ClickEscortTaskNav("encounter-done"))
                    {
                        PauseEscortOnConditionFail("encounter-done");
                    }

                    return;
                }
            }

            EnsureEscortEncounterOn();
            _lastActivityMs = now;
            return;
        }

        if (IsCurrentEscortEncounterFarm())
        {
            if (IsAtEscortEncounterDest())
            {
                EnterEscortEncounterWait();
                return;
            }
        }

        if (_stuckResumePending)
        {
            TickEscortStuckPending(now);
            return;
        }

        if (now - _lastActivityMs >= StuckIdleMs)
        {
            // 15000 上卡楼梯会 RunTask(400) 绕回芙蕾雅；中秋该图只走特例/手点
            if (TempMidAutumnEscort119 && _escortMissionId == MoonRabbitMissionId
                && TryGetCurrentMapInfo(out var stuckFloor, out _, out _)
                && stuckFloor == MoonRabbitWarpStuckFloor)
            {
                return;
            }

            BeginEscortStuckRecovery(now);
        }
    }

    private static void ResetEscortStuckState()
    {
        _stuckResumePending = false;
    }

    private static void ClearEscortStuckPending()
    {
        _stuckResumePending = false;
    }

    /// <summary>卡楼梯：随机挪 1 格后点任务（与改 15000 特例之前一致）。</summary>
    private static void BeginEscortStuckRecovery(long now)
    {
        _escortRecoverAttempts++;
        WriteLog("escort stuck idle missionId=" + _escortMissionId
                 + " stepRecover=" + _escortRecoverAttempts + "/" + EscortMaxRecoverFails);
        if (_escortRecoverAttempts >= EscortMaxRecoverFails)
        {
            _escortPauseReason = "本步骤累计" + EscortMaxRecoverFails + "次尝试恢复失败";
            PauseEscort(_escortPauseReason + "，已暂停，请手动处理", true);
            return;
        }

        if (TryRandomStepOne())
        {
            _stuckMoveAtMs = now;
            _stuckResumePending = true;
            _lastActivityMs = now;
            Tip("任务护航：卡楼梯，挪格后重点任务（本步骤 "
                + _escortRecoverAttempts + "/" + EscortMaxRecoverFails + "）");
        }
        else
        {
            _lastActivityMs = now;
            if (!ClickEscortTaskNav("stuck-resume-fallback"))
            {
                PauseEscortOnConditionFail("stuck-resume-fallback");
            }
        }
    }

    private static void TickEscortStuckPending(long now)
    {
        if (now - _stuckMoveAtMs < StuckResumeDelayMs)
        {
            return;
        }

        _stuckResumePending = false;
        _lastActivityMs = now;
        if (!ClickEscortTaskNav("stuck-resume"))
        {
            PauseEscortOnConditionFail("stuck-resume");
        }
    }

    private static void RefreshEscortCandidates()
    {
        _escortCandidates.Clear();
        try
        {
            var dataList = GetStaticMember("MissionDataHolder", "DataList") as System.Collections.IList;
            if (dataList == null)
            {
                return;
            }

            var uid = Convert.ToString(
                GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var holder = FindType("MissionDataHolder");
            var get = holder?.GetMethod(
                "GetMissionDataFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var dictObj = get?.Invoke(null, new object[] { uid });
            var idict = dictObj as System.Collections.IDictionary;
            if (idict == null)
            {
                return;
            }

            var jobName = Convert.ToString(
                GetMember(GetStaticMember("PlayerDataHolder", "playerData"), "JobAncestryName") ?? "") ?? "";

            var started = new List<EscortCandidate>();
            var ready = new List<EscortCandidate>();
            var queued = new List<EscortCandidate>();

            foreach (var keyObj in dataList)
            {
                var id = Convert.ToInt32(keyObj);
                if (!idict.Contains(id))
                {
                    continue;
                }

                var mission = idict[id];
                if (mission == null)
                {
                    continue;
                }

                if (!PassJobAncestryFilter(mission, jobName))
                {
                    continue;
                }

                var status = Convert.ToString(GetMember(mission, "taskstatus") ?? "") ?? "";
                if (status.EndsWith("Ended", StringComparison.Ordinal) || status == "2")
                {
                    continue;
                }

                var title = Convert.ToString(GetMember(mission, "title") ?? "") ?? ("任务" + id);
                var isStarted = status.EndsWith("Started", StringComparison.Ordinal) || status == "1";

                if (isStarted)
                {
                    started.Add(new EscortCandidate { Id = id, Title = title, Status = "进行中" });
                    continue;
                }

                // 未开始：可接 / 未接（可排队，等前置完成后执行）
                if (TryPrepareEscortMission(mission, out _))
                {
                    ready.Add(new EscortCandidate { Id = id, Title = title, Status = "可接" });
                }
                else
                {
                    queued.Add(new EscortCandidate { Id = id, Title = title, Status = "未接" });
                }
            }

            _escortCandidates.AddRange(started);
            _escortCandidates.AddRange(ready);
            _escortCandidates.AddRange(queued);
            WriteLog("escort candidates started=" + started.Count
                     + " ready=" + ready.Count + " notReady=" + queued.Count);
        }
        catch (Exception ex)
        {
            WriteLog("RefreshEscortCandidates EX: " + RootMessage(ex));
        }
    }

    private static bool PassJobAncestryFilter(object mission, string jobName)
    {
        try
        {
            var names = GetMember(mission, "jobancestryNames") as System.Collections.IList;
            if (names == null || names.Count == 0)
            {
                return true;
            }

            foreach (var n in names)
            {
                if (string.Equals(Convert.ToString(n) ?? "", jobName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>准备任务步骤；临时放宽等级后再 CommonSetMissionStep，其它条件仍生效。</summary>
    private static bool TryPrepareEscortMission(object mission, out string failReason)
    {
        failReason = "条件不满足";
        if (mission == null)
        {
            failReason = "空任务";
            return false;
        }

        object player = null;
        object oldLevel = null;
        try
        {
            var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            player = getPlayer?.Invoke(null, new object[] { uid })
                     ?? GetStaticMember("PlayerDataHolder", "playerData");
            if (player != null)
            {
                oldLevel = GetMember(player, "level");
            }

            var common = mission.GetType().GetMethod(
                "CommonSetMissionStep",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (common == null)
            {
                failReason = "CommonSetMissionStep缺失";
                return false;
            }

            var tryLevels = CollectLevelBypassCandidates(mission, oldLevel);
            foreach (var lv in tryLevels)
            {
                if (player != null && lv >= 0)
                {
                    SetMember(player, "level", lv);
                }
                else if (player != null && oldLevel != null)
                {
                    SetMember(player, "level", oldLevel);
                }

                common.Invoke(mission, null);
                var flag = Convert.ToBoolean(GetMember(mission, "missionStepFlag") ?? false);
                if (flag && (MissionHasMovePoints(mission) || IsEncounterFarmStep(GetMissionStepConfig(mission))))
                {
                    return true;
                }
            }

            failReason = MissionHasMovePoints(mission) ? "条件不满足" : "无寻路点";
            if (failReason == "条件不满足")
            {
                var diag = DiagnoseEscortStepFail(mission);
                if (!string.IsNullOrEmpty(diag))
                {
                    failReason = diag;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            failReason = RootMessage(ex);
            return false;
        }
        finally
        {
            if (player != null && oldLevel != null)
            {
                try
                {
                    SetMember(player, "level", oldLevel);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>
    /// 对照 MissionData.CheckMissionCondition 拆条件，找出「最近」步骤变体差什么（忽略等级，与护航绕过一致）。
    /// </summary>
    private static string DiagnoseEscortStepFail(object mission)
    {
        try
        {
            if (mission == null)
            {
                return "找不到任务数据";
            }

            var missStep = GetMember(mission, "MissStepData") as System.Collections.IDictionary;
            if (missStep == null || missStep.Count == 0)
            {
                return "无步骤配置";
            }

            List<object> items;
            object player;
            int curMap;
            string curMapName;
            int curX, curY;
            int teamNum;
            long gold;
            int curTimer;
            TryGetEscortDiagContext(out player, out items, out curMap, out curMapName,
                out curX, out curY, out teamNum, out gold, out curTimer);

            List<string> bestFails = null;
            string bestDesc = "";
            var variantCount = 0;

            foreach (System.Collections.DictionaryEntry e in missStep)
            {
                var steps = e.Value as System.Collections.IList;
                if (steps == null)
                {
                    continue;
                }

                foreach (var step in steps)
                {
                    if (step == null)
                    {
                        continue;
                    }

                    variantCount++;
                    var fails = EvaluateStepConfigFails(
                        step, player, items, curMap, curMapName, curX, curY, teamNum, gold, curTimer);
                    if (fails.Count == 0)
                    {
                        return "步骤条件已满足但未选中（可点继续重试）";
                    }

                    if (bestFails == null || fails.Count < bestFails.Count)
                    {
                        bestFails = fails;
                        bestDesc = Convert.ToString(GetMember(step, "Describe") ?? "") ?? "";
                    }
                }
            }

            if (bestFails == null || bestFails.Count == 0)
            {
                return "条件不满足（变体" + variantCount + "）";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("当前").Append(TimeSectionName(curTimer));
            if (!string.IsNullOrEmpty(bestDesc))
            {
                var d = bestDesc;
                if (d.Length > 18)
                {
                    d = d.Substring(0, 18) + "…";
                }

                sb.Append("｜").Append(d);
            }

            sb.Append("｜缺:");
            for (var i = 0; i < bestFails.Count && i < 4; i++)
            {
                if (i > 0)
                {
                    sb.Append("；");
                }

                sb.Append(bestFails[i]);
            }

            if (bestFails.Count > 4)
            {
                sb.Append("…");
            }

            if (variantCount > 1)
            {
                sb.Append("（").Append(variantCount).Append("变体）");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            WriteLog("DiagnoseEscortStepFail EX: " + RootMessage(ex));
            return "条件诊断失败";
        }
    }

    private static void TryGetEscortDiagContext(
        out object player,
        out List<object> items,
        out int curMap,
        out string curMapName,
        out int curX,
        out int curY,
        out int teamNum,
        out long gold,
        out int curTimer)
    {
        player = null;
        items = new List<object>();
        curMap = 0;
        curMapName = "";
        curX = 0;
        curY = 0;
        teamNum = 0;
        gold = 0;
        curTimer = 0;
        try
        {
            var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            player = getPlayer?.Invoke(null, new object[] { uid })
                     ?? GetStaticMember("PlayerDataHolder", "playerData");
            if (player != null)
            {
                try
                {
                    gold = Convert.ToInt64(GetMember(player, "gold") ?? 0)
                           + Convert.ToInt64(GetMember(player, "unBindGold") ?? 0);
                }
                catch
                {
                    // ignore
                }
            }

            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var itemList = getItems?.Invoke(null, new object[] { uid }) as System.Collections.IEnumerable;
            if (itemList != null)
            {
                foreach (var it in itemList)
                {
                    if (it != null)
                    {
                        items.Add(it);
                    }
                }
            }

            int mapResId;
            TryGetCurrentMapInfo(out curMap, out curMapName, out mapResId);

            TryGetPlayerXY(out curX, out curY);

            var tm = GetManagerInstance("TeamManager");
            if (tm != null)
            {
                var m = tm.GetType().GetMethod("GetTeamNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    teamNum = Convert.ToInt32(m.Invoke(tm, null) ?? 0);
                }
            }

            var git = FindType("GameInnerTime");
            var gs = git?.GetMethod("GetGameTimeSection", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (gs != null)
            {
                curTimer = Convert.ToInt32(gs.Invoke(null, null) ?? 0);
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryGetEscortDiagContext EX: " + RootMessage(ex));
        }
    }

    private static List<string> EvaluateStepConfigFails(
        object step,
        object player,
        List<object> items,
        int curMap,
        string curMapName,
        int curX,
        int curY,
        int teamNum,
        long gold,
        int curTimer)
    {
        var fails = new List<string>();
        try
        {
            // 表达式 Condition（TaskStepCondition）
            try
            {
                var stepId = Convert.ToInt32(GetMember(step, "ID") ?? GetProp(step, "ID") ?? 0);
                if (stepId > 0 && !EvaluateTaskStepExpression(stepId))
                {
                    var cond = Convert.ToString(GetMember(step, "Condition") ?? "") ?? "";
                    fails.Add(string.IsNullOrEmpty(cond) ? "表达式条件" : ("表达式:" + TrimDiag(cond, 24)));
                }
            }
            catch
            {
                // ignore
            }

            // Level：护航会绕过，诊断里不作为主因（仍可提示）
            // Timer
            var timers = ToIntList(GetMember(step, "Timer"));
            if (timers.Count > 0 && !timers.Contains(curTimer))
            {
                var need = new System.Text.StringBuilder("时段需");
                for (var i = 0; i < timers.Count; i++)
                {
                    if (i > 0)
                    {
                        need.Append('/');
                    }

                    need.Append(TimeSectionName(timers[i]));
                }

                fails.Add(need.ToString());
            }

            // ItemList range
            var itemList = ToIntList(GetMember(step, "ItemList"));
            var itemMin = ToIntList(GetMember(step, "ItemMin"));
            var itemMax = ToIntList(GetMember(step, "ItemMax"));
            for (var j = 0; j < itemList.Count; j++)
            {
                var n = CountItemPile(items, itemList[j]);
                var min = j < itemMin.Count ? itemMin[j] : 0;
                var max = j < itemMax.Count ? itemMax[j] : int.MaxValue;
                if (n < min || n > max)
                {
                    fails.Add("物品#" + itemList[j] + "x" + n + "(需" + min + "-" + max + ")");
                }
            }

            // ItemHaveAll
            foreach (var id in ToIntList(GetMember(step, "ItemHaveAll")))
            {
                if (CountItemPile(items, id) <= 0)
                {
                    fails.Add("缺物品#" + id);
                }
            }

            // AnyItemHave
            var anyItems = ToIntList(GetMember(step, "AnyItemHave"));
            if (anyItems.Count > 0)
            {
                var any = false;
                foreach (var id in anyItems)
                {
                    if (CountItemPile(items, id) > 0)
                    {
                        any = true;
                        break;
                    }
                }

                if (!any)
                {
                    fails.Add("需任一物品#" + string.Join("/", anyItems.ConvertAll(x => x.ToString()).ToArray()));
                }
            }

            // ItemNotAll：全部持有则失败
            var notAll = ToIntList(GetMember(step, "ItemNotAll"));
            if (notAll.Count > 0)
            {
                var allHave = true;
                foreach (var id in notAll)
                {
                    if (CountItemPile(items, id) <= 0)
                    {
                        allHave = false;
                        break;
                    }
                }

                if (allHave)
                {
                    fails.Add("不可同时持有物品组");
                }
            }

            // Events
            AppendEventFails(fails, GetMember(step, "AnyNowEvent"), true, true);
            AppendEventFails(fails, GetMember(step, "AllNowEvent"), true, false);
            AppendEventFails(fails, GetMember(step, "AnyEndEvent"), false, true);
            AppendEventFails(fails, GetMember(step, "AllEndEvent"), false, false);

            // Map
            var mapIds = ToIntList(GetMember(step, "MapID"));
            if (mapIds.Count > 0 && !mapIds.Contains(curMap))
            {
                fails.Add("需地图" + string.Join("/", mapIds.ConvertAll(x => x.ToString()).ToArray())
                          + "(现" + curMap + ")");
            }

            var unMap = ToIntList(GetMember(step, "UnMapID"));
            if (unMap.Contains(curMap))
            {
                fails.Add("不可在地图" + curMap);
            }

            var mapNames = ToStringList(GetMember(step, "MapName"));
            if (mapNames.Count > 0 && !mapNames.Contains(curMapName))
            {
                fails.Add("需地图名「" + TrimDiag(string.Join("/", mapNames.ToArray()), 20) + "」");
            }

            var unMapNames = ToStringList(GetMember(step, "UnMapName"));
            if (unMapNames.Contains(curMapName))
            {
                fails.Add("不可在「" + TrimDiag(curMapName, 12) + "」");
            }

            // MapIDXY
            var xyList = GetMember(step, "MapIDXY") as System.Collections.IList;
            if (xyList != null && xyList.Count > 0)
            {
                var ok = false;
                foreach (var pt in xyList)
                {
                    if (pt == null)
                    {
                        continue;
                    }

                    var id = Convert.ToInt32(GetMember(pt, "Id") ?? GetProp(pt, "Id") ?? -1);
                    var px = Convert.ToInt32(GetMember(pt, "X") ?? GetProp(pt, "X") ?? -999);
                    var py = Convert.ToInt32(GetMember(pt, "Y") ?? GetProp(pt, "Y") ?? -999);
                    if (id == curMap && px == curX && py == curY)
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    fails.Add("需站指定坐标");
                }
            }

            // Team
            var teams = ToIntList(GetMember(step, "TeamNum"));
            if (teams.Count > 0 && !teams.Contains(teamNum))
            {
                fails.Add("需队伍人数" + string.Join("/", teams.ConvertAll(x => x.ToString()).ToArray())
                          + "(现" + teamNum + ")");
            }

            // Gold
            try
            {
                var needGold = Convert.ToInt64(GetMember(step, "Gold") ?? 0);
                if (needGold > 0 && gold < needGold)
                {
                    fails.Add("金币不足(需" + needGold + ")");
                }
            }
            catch
            {
                // ignore
            }

            // Job ancestry whitelist (ignore if empty)
            var jobs = ToIntList(GetMember(step, "JobAncestry"));
            if (jobs.Count > 0 && player != null)
            {
                var ja = Convert.ToInt32(GetMember(player, "JobAncestry") ?? -1);
                if (!jobs.Contains(ja))
                {
                    fails.Add("职业系不符");
                }
            }
        }
        catch (Exception ex)
        {
            fails.Add("解析异常:" + RootMessage(ex));
        }

        return fails;
    }

    private static void AppendEventFails(List<string> fails, object dictObj, bool nowEvent, bool anyMode)
    {
        var dict = dictObj as System.Collections.IDictionary;
        if (dict == null || dict.Count == 0)
        {
            return;
        }

        var label = nowEvent ? "进行中事件" : "完成事件";
        if (anyMode)
        {
            var anyOk = false;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var id = Convert.ToInt32(e.Key);
                var expect = Convert.ToBoolean(e.Value);
                if (HasMissionEvent(id, nowEvent) == expect)
                {
                    anyOk = true;
                    break;
                }
            }

            if (!anyOk)
            {
                fails.Add("缺" + label);
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var id = Convert.ToInt32(e.Key);
                var expect = Convert.ToBoolean(e.Value);
                if (HasMissionEvent(id, nowEvent) != expect)
                {
                    fails.Add(label + "#" + id + (expect ? "未达成" : "不应有"));
                    break;
                }
            }
        }
    }

    private static bool HasMissionEvent(int eventId, bool nowEvent)
    {
        try
        {
            var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var holder = FindType("MissionDataHolder");
            var field = holder?.GetField(
                nowEvent ? "nowEventsDic" : "endEventsDic",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var dic = field?.GetValue(null) as System.Collections.IDictionary;
            if (dic == null || !dic.Contains(uid))
            {
                // EventsUid
                var evUid = Convert.ToString(GetStaticMember("MissionDataHolder", "EventsUid") ?? uid) ?? uid;
                if (dic != null && dic.Contains(evUid))
                {
                    uid = evUid;
                }
                else
                {
                    return false;
                }
            }

            var set = dic[uid];
            if (set is System.Collections.IList list)
            {
                return list.Contains(eventId);
            }

            // HashSet etc.
            var contains = set?.GetType().GetMethod("Contains");
            if (contains != null)
            {
                return Convert.ToBoolean(contains.Invoke(set, new object[] { eventId }) ?? false);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool EvaluateTaskStepExpression(int stepConfigId)
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm == null)
            {
                return true;
            }

            var condDic = GetMember(tm, "TaskStepCondition") as System.Collections.IDictionary
                          ?? GetProp(tm, "TaskStepCondition") as System.Collections.IDictionary;
            if (condDic == null || !condDic.Contains(stepConfigId))
            {
                return true;
            }

            var cond = condDic[stepConfigId];
            if (cond == null)
            {
                return true;
            }

            var has = Convert.ToBoolean(GetMember(cond, "HasCondition") ?? GetProp(cond, "HasCondition") ?? false);
            if (!has)
            {
                return true;
            }

            var eval = cond.GetType().GetMethod(
                "EvaluateCondition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (eval == null)
            {
                return true;
            }

            return Convert.ToBoolean(eval.Invoke(cond, null) ?? true);
        }
        catch
        {
            return true;
        }
    }

    private static int CountItemPile(List<object> items, int itemId)
    {
        var n = 0;
        if (items == null)
        {
            return 0;
        }

        foreach (var it in items)
        {
            try
            {
                var useFlag = Convert.ToInt32(GetMember(it, "useFlag") ?? 0);
                if (useFlag != 1)
                {
                    continue;
                }

                var data = GetMember(it, "data") ?? GetProp(it, "data");
                var id = Convert.ToInt32(GetMember(data, "Id") ?? GetProp(data, "Id") ?? -1);
                if (id != itemId)
                {
                    continue;
                }

                n += Convert.ToInt32(GetMember(data, "Pile") ?? GetProp(data, "Pile") ?? 1);
            }
            catch
            {
                // ignore
            }
        }

        return n;
    }

    private static List<int> ToIntList(object listObj)
    {
        var list = new List<int>();
        var il = listObj as System.Collections.IList;
        if (il == null)
        {
            return list;
        }

        foreach (var o in il)
        {
            try
            {
                list.Add(Convert.ToInt32(o));
            }
            catch
            {
                // ignore
            }
        }

        return list;
    }

    private static List<string> ToStringList(object listObj)
    {
        var list = new List<string>();
        var il = listObj as System.Collections.IList;
        if (il == null)
        {
            return list;
        }

        foreach (var o in il)
        {
            list.Add(Convert.ToString(o ?? "") ?? "");
        }

        return list;
    }

    private static string TimeSectionName(int section)
    {
        switch (section)
        {
            case 0: return "白天";
            case 1: return "傍晚";
            case 2: return "夜晚";
            case 3: return "早晨";
            default: return "时段" + section;
        }
    }

    private static string TrimDiag(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s ?? "";
        }

        return s.Substring(0, max) + "…";
    }

    /// <summary>收集用于绕过等级检查的候选等级（含当前等级与各步骤区间中点）。</summary>
    private static List<int> CollectLevelBypassCandidates(object mission, object currentLevel)
    {
        var list = new List<int>();
        try
        {
            if (currentLevel != null)
            {
                list.Add(Convert.ToInt32(currentLevel));
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var missStep = GetMember(mission, "MissStepData") as System.Collections.IDictionary;
            if (missStep != null)
            {
                foreach (System.Collections.DictionaryEntry e in missStep)
                {
                    var steps = e.Value as System.Collections.IList;
                    if (steps == null)
                    {
                        continue;
                    }

                    foreach (var step in steps)
                    {
                        if (step == null)
                        {
                            continue;
                        }

                        var level = GetMember(step, "Level") as System.Collections.IList;
                        if (level == null || level.Count < 2)
                        {
                            continue;
                        }

                        var lo = Convert.ToInt32(level[0]);
                        var hi = Convert.ToInt32(level[1]);
                        if (hi < lo)
                        {
                            var t = lo;
                            lo = hi;
                            hi = t;
                        }

                        var mid = lo + (hi - lo) / 2;
                        if (!list.Contains(mid))
                        {
                            list.Add(mid);
                        }

                        if (!list.Contains(lo))
                        {
                            list.Add(lo);
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (list.Count == 0)
        {
            list.Add(-1);
        }

        return list;
    }

    private static bool MissionHasMovePoints(object mission)
    {
        try
        {
            var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
            var move = GetMember(script, "movePoint") as System.Collections.ICollection;
            return move != null && move.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMissionEnded(int missionId)
    {
        try
        {
            var mission = GetMissionDataById(missionId);
            if (mission == null)
            {
                return false;
            }

            var status = Convert.ToString(GetMember(mission, "taskstatus") ?? "") ?? "";
            if (status.EndsWith("Ended", StringComparison.Ordinal) || status == "2")
            {
                return true;
            }

            var tm = GetManagerInstance("TaskManager");
            var check = tm?.GetType().GetMethod(
                "CheckTaskFlag",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (check != null)
            {
                var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
                var ps = check.GetParameters();
                if (ps.Length >= 2)
                {
                    return Convert.ToBoolean(check.Invoke(tm, new object[] { missionId, uid }));
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static int GetRunTaskId()
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm == null)
            {
                return -1;
            }

            var v = GetProp(tm, "RunTaskId") ?? GetMember(tm, "RunTaskId");
            return Convert.ToInt32(v ?? -1);
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryGetPlayerXY(out int x, out int y)
    {
        x = 0;
        y = 0;
        try
        {
            var loc = GetStaticMember("PlayerDataHolder", "location");
            if (loc == null)
            {
                return false;
            }

            x = Convert.ToInt32(GetMember(loc, "x") ?? GetMember(loc, "X") ?? 0);
            y = Convert.ToInt32(GetMember(loc, "y") ?? GetMember(loc, "Y") ?? 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEscapeDown()
    {
        try
        {
            var input = FindType("UnityEngine.Input");
            var keyCodeType = FindType("UnityEngine.KeyCode");
            if (input == null || keyCodeType == null)
            {
                return false;
            }

            var escape = Enum.Parse(keyCodeType, "Escape");
            var m = input.GetMethod("GetKeyDown", new[] { keyCodeType });
            if (m == null)
            {
                return false;
            }

            return Convert.ToBoolean(m.Invoke(null, new[] { escape }));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 停官方任务导航（CancelTaskPathfinding + StopMove）。
    /// 用道具 / 存包 / 回登入点 / 挂机传送 / 暂停护航 等必须走这里。
    /// 卡图「只点任务续航」不要用本方法（会抹掉切图后续航），改用 <see cref="StopWalkOnly"/>。
    /// </summary>
    private static void StopTaskNavigation(bool writeLog = true)
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm != null)
            {
                try
                {
                    var cancel = tm.GetType().GetMethod(
                        "CancelTaskPathfinding",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    cancel?.Invoke(tm, null);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    SetProp(tm, "RunTaskId", -1);
                }
                catch
                {
                    try
                    {
                        SetMember(tm, "RunTaskId", -1);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            var pm = GetManagerInstance("PlayerManager");
            var walk = GetProp(pm, "walkSystem") ?? GetMember(pm, "walkSystem");
            if (walk == null)
            {
                return;
            }

            MethodInfo stop = null;
            foreach (var m in walk.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "StopMove")
                {
                    continue;
                }

                stop = m;
                if (m.GetParameters().Length >= 1)
                {
                    break;
                }
            }

            if (stop == null)
            {
                return;
            }

            var ps = stop.GetParameters();
            var args = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(bool))
                {
                    args[i] = true;
                }
                else if (ps[i].ParameterType.IsEnum || ps[i].ParameterType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(ps[i].ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            stop.Invoke(walk, args);
            if (writeLog)
            {
                WriteLog("escort StopMove ok");
            }
        }
        catch (Exception ex)
        {
            WriteLog("StopTaskNavigation EX: " + RootMessage(ex));
        }
    }

    /// <summary>走到指定格（卡楼梯解卡 / 走回原格）。</summary>
    private static bool TryWalkTo(int tx, int ty)
    {
        try
        {
            if (!TryGetPlayerXY(out var x, out var y))
            {
                return false;
            }

            if (x == tx && y == ty)
            {
                return true;
            }

            var pm = GetManagerInstance("PlayerManager");
            var walk = GetProp(pm, "walkSystem") ?? GetMember(pm, "walkSystem");
            if (walk == null)
            {
                return false;
            }

            var v2Type = FindType("UnityEngine.Vector2Int");
            if (v2Type == null)
            {
                return false;
            }

            object target;
            try
            {
                target = Activator.CreateInstance(v2Type, tx, ty);
            }
            catch
            {
                target = Activator.CreateInstance(v2Type);
                SetMember(target, "x", tx);
                SetMember(target, "y", ty);
                try
                {
                    SetProp(target, "x", tx);
                    SetProp(target, "y", ty);
                }
                catch
                {
                    // ignore
                }
            }

            MethodInfo moveTo = null;
            foreach (var m in walk.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "MoveTo")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType.Name == "Vector2Int")
                {
                    moveTo = m;
                    break;
                }
            }

            if (moveTo == null)
            {
                return false;
            }

            var psAll = moveTo.GetParameters();
            var args = new object[psAll.Length];
            args[0] = target;
            for (var i = 1; i < psAll.Length; i++)
            {
                if (psAll[i].ParameterType == typeof(bool))
                {
                    args[i] = false;
                }
                else if (psAll[i].ParameterType.IsValueType && !psAll[i].ParameterType.IsEnum)
                {
                    args[i] = Activator.CreateInstance(psAll[i].ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            moveTo.Invoke(walk, args);
            WriteLog("escort walk (" + x + "," + y + ")->(" + tx + "," + ty + ")");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryWalkTo EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>随机 X±1 或 Y±1 走一格（卡楼梯解卡）。</summary>
    private static bool TryRandomStepOne()
    {
        try
        {
            if (!TryGetPlayerXY(out var x, out var y))
            {
                return false;
            }

            var axisX = _rng.Next(2) == 0;
            var delta = _rng.Next(2) == 0 ? -1 : 1;
            var tx = axisX ? x + delta : x;
            var ty = axisX ? y : y + delta;
            return TryWalkTo(tx, ty);
        }
        catch (Exception ex)
        {
            WriteLog("TryRandomStepOne EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>
    /// 遇敌/打怪获取道具步骤：到达目的地后开原地遇敌，战斗结束检查道具后再关遇敌继续。
    /// </summary>
    private static string StripRichText(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '<')
            {
                var j = s.IndexOf('>', i);
                if (j >= 0)
                {
                    i = j;
                    continue;
                }
            }

            if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == 'n' || s[i + 1] == 'N'))
            {
                sb.Append(' ');
                i++;
                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }

    private static string MainStepDescribe(object step)
    {
        var desc = StripRichText(Convert.ToString(GetMember(step, "Describe") ?? GetProp(step, "Describe") ?? "") ?? "");
        var tipAt = desc.IndexOf("小贴士", StringComparison.Ordinal);
        if (tipAt > 0)
        {
            desc = desc.Substring(0, tipAt);
        }

        return desc.Trim();
    }

    private static object GetMissionStepConfig(object mission)
    {
        if (mission == null)
        {
            return null;
        }

        try
        {
            var m = mission.GetType().GetMethod(
                "GetStepConfig",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cfg = m?.Invoke(mission, null);
            if (cfg != null)
            {
                return cfg;
            }
        }
        catch
        {
            // fallback below
        }

        try
        {
            var miss = GetMember(mission, "MissStepData") as System.Collections.IDictionary;
            var stepNum = Convert.ToInt32(GetMember(mission, "missionStepNum") ?? -1);
            if (miss == null || !miss.Contains(stepNum))
            {
                return null;
            }

            var list = miss[stepNum] as System.Collections.IList;
            if (list == null || list.Count == 0)
            {
                return null;
            }

            // 同一步多变体时取最后一个（与任务步骤队列先取末项一致）
            return list[list.Count - 1];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是否「遇敌/击败获取道具」类步骤（原地挂机，不是点 NPC 挑战）。</summary>
    private static bool IsEncounterFarmStep(object step)
    {
        if (step == null)
        {
            return false;
        }

        var desc = MainStepDescribe(step);
        if (desc.IndexOf("遇敌", StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        try
        {
            var hints = GetMember(step, "Hints") as System.Collections.IList;
            if (hints != null)
            {
                foreach (var h in hints)
                {
                    var s = Convert.ToString(h) ?? "";
                    if (s.IndexOf("开启原地遇敌", StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (desc.IndexOf("获得", StringComparison.Ordinal) >= 0
            && desc.IndexOf("交谈", StringComparison.Ordinal) < 0
            && (desc.IndexOf("击败", StringComparison.Ordinal) >= 0
                || desc.IndexOf("击杀", StringComparison.Ordinal) >= 0
                || desc.IndexOf("打倒", StringComparison.Ordinal) >= 0))
        {
            return true;
        }

        return false;
    }

    private static bool IsCurrentEscortEncounterFarm()
    {
        try
        {
            var mission = GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                return false;
            }

            var common = mission.GetType().GetMethod(
                "CommonSetMissionStep",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            common?.Invoke(mission, null);
            return IsEncounterFarmStep(GetMissionStepConfig(mission));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>遇敌步骤导航点：scriptData.movePoint 最后一项（与 ClickEscortTaskNav 一致）。MapPoint 为 x=地图 y=X z=Y。</summary>
    private static bool TryGetEscortEncounterDest(out int mapFloor, out int x, out int y)
    {
        mapFloor = 0;
        x = 0;
        y = 0;
        try
        {
            var mission = GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                return false;
            }

            var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
            var move = GetMember(script, "movePoint") as IList;
            if (move == null || move.Count == 0)
            {
                return false;
            }

            var v3 = move[move.Count - 1];
            mapFloor = Convert.ToInt32(GetMember(v3, "x") ?? GetProp(v3, "x") ?? 0);
            x = Convert.ToInt32(GetMember(v3, "y") ?? GetProp(v3, "y") ?? 0);
            y = Convert.ToInt32(GetMember(v3, "z") ?? GetProp(v3, "z") ?? 0);
            if (mapFloor == -999)
            {
                int floor;
                string floorName;
                int mapResId;
                TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
                mapFloor = floor;
            }

            return mapFloor != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAtEscortEncounterDest()
    {
        int destFloor;
        int destX;
        int destY;
        if (!TryGetEscortEncounterDest(out destFloor, out destX, out destY))
        {
            return true;
        }

        int floor;
        string floorName;
        int mapResId;
        if (!TryGetCurrentMapInfo(out floor, out floorName, out mapResId) || floor != destFloor)
        {
            return false;
        }

        int px;
        int py;
        if (!TryGetPlayerXY(out px, out py))
        {
            return false;
        }

        var dx = px - destX;
        var dy = py - destY;
        if (dx < 0)
        {
            dx = -dx;
        }

        if (dy < 0)
        {
            dy = -dy;
        }

        return dx <= EscortEncounterArriveNear && dy <= EscortEncounterArriveNear;
    }

    private static string ParseObtainItemName(object step)
    {
        var desc = MainStepDescribe(step);
        var i = desc.LastIndexOf("获得", StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }

        var rest = desc.Substring(i + 2).Trim();
        var cutChars = new[] { '，', '。', '；', '、', ' ', '\t', ',', '.', ';', '通', '可', '后' };
        var cut = rest.Length;
        for (var k = 0; k < rest.Length; k++)
        {
            for (var c = 0; c < cutChars.Length; c++)
            {
                if (rest[k] == cutChars[c])
                {
                    cut = k;
                    k = rest.Length;
                    break;
                }
            }
        }

        if (cut < rest.Length)
        {
            rest = rest.Substring(0, cut);
        }

        return rest.Trim();
    }

    private static int CountBagItemByName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        try
        {
            var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var itemList = getItems?.Invoke(null, new object[] { uid }) as System.Collections.IEnumerable;
            if (itemList == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var it in itemList)
            {
                try
                {
                    if (Convert.ToInt32(GetMember(it, "useFlag") ?? 0) != 1)
                    {
                        continue;
                    }

                    var data = GetMember(it, "data") ?? GetProp(it, "data");
                    var itemName = Convert.ToString(GetMember(data, "Name") ?? GetProp(data, "Name") ?? "") ?? "";
                    if (itemName.IndexOf(name, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    n += Convert.ToInt32(GetMember(data, "Pile") ?? GetProp(data, "Pile") ?? 1);
                }
                catch
                {
                    // ignore
                }
            }

            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static bool EscortEncounterHasTargetItem()
    {
        return !string.IsNullOrEmpty(_escortWaitItemName) && CountBagItemByName(_escortWaitItemName) > 0;
    }

    private static bool CanEscortLeaveEncounterWait()
    {
        if (IsMissionEnded(_escortMissionId))
        {
            return true;
        }

        var stepNum = GetEscortMissionStepNum();
        if (stepNum >= 0 && _escortWaitAtStepNum >= 0 && stepNum != _escortWaitAtStepNum)
        {
            return true;
        }

        if (EscortEncounterHasTargetItem())
        {
            return true;
        }

        return !IsCurrentEscortEncounterFarm();
    }

    private static int GetEncounterStatus()
    {
        try
        {
            var pd = GetStaticMember("PlayerDataHolder", "playerData");
            return Convert.ToInt32(GetMember(pd, "encounterStatus") ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static void ResetMoonRabbitEscortFlags()
    {
        _escortLoginGatePending = false;
        _escortLoginGateAtMs = 0;
        _escortUseItemPending = false;
        _escortUseItemAtMs = 0;
        ResetWingWizardState();
        _escort119GateDone2 = false;
        _escort119TeleportDone5 = false;
        _escort119TeleportDone7 = false;
        _escortHangupTeleportPending = false;
        _escortHangupTeleportAtMs = 0;
        _escortHangupTeleportExpectFloor = 0;
        _escort119TicketBankDone = false;
        _escort119TicketBankPending = false;
        _escort119TicketBankAtMs = 0;
        _escort119LastStepSinceMs = 0;
        _escort119TicketBankUids.Clear();
        _escort119TicketBankUidIndex = 0;
        _escort119TicketBankFailStreak = 0;
        _escort119TicketBankAwaitConfirm = false;
        _escort119TicketBankAnyStored = false;
        _escort119PendingAfterGateStep = -1;
        _escort119WarpUnstickPhase = 0;
        _escort119WarpUnstickAtMs = 0;
        _escort119WarpUnstickClicks = 0;
        _escort119WarpUnstickDone = false;
    }

    /// <summary>
    /// 中秋 #119 唯一导航特例：15000(22,33)。
    /// 右侧点任务不是「取消再启动」，而是 AutoWarpIndex=0（走向 400）。
    /// 护航遇敌步却从表尾 100 开，切图后还握着回芙蕾雅，直接导航迈不开步。
    /// </summary>
    private static bool TryTickMoonRabbitWarpUnstick(long now)
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        int floor;
        string floorName;
        int mapResId;
        if (!TryGetCurrentMapInfo(out floor, out floorName, out mapResId))
        {
            return false;
        }

        if (floor != MoonRabbitWarpStuckFloor)
        {
            if (!_escort119WarpUnstickDone
                && (_escort119WarpUnstickPhase != 0 || _escort119WarpUnstickClicks > 0))
            {
                WriteLog("119 warp-unstick done left floor=" + floor
                         + " clicks=" + _escort119WarpUnstickClicks);
                _escort119WarpUnstickPhase = 0;
                _escort119WarpUnstickClicks = 0;
                _escort119WarpUnstickDone = true;
            }

            return false;
        }

        if (_escort119WarpUnstickDone)
        {
            return false;
        }

        int px;
        int py;
        if (!TryGetPlayerXY(out px, out py))
        {
            return false;
        }

        var atStuck = px == MoonRabbitWarpStuckX && py == MoonRabbitWarpStuckY;
        if (_escort119WarpUnstickClicks > 0 && !atStuck)
        {
            WriteLog("119 warp-unstick done left tile (" + px + "," + py + ")"
                     + " clicks=" + _escort119WarpUnstickClicks);
            _escort119WarpUnstickPhase = 0;
            _escort119WarpUnstickClicks = 0;
            _escort119WarpUnstickDone = true;
            return false;
        }

        if (_escort119WarpUnstickPhase == 1)
        {
            if (!atStuck)
            {
                _escort119WarpUnstickPhase = 0;
                _escort119WarpUnstickClicks = 0;
                _escort119WarpUnstickDone = true;
                return false;
            }

            if (now - _escort119WarpUnstickAtMs < MoonRabbitWarpUnstickRetryMs)
            {
                return true;
            }

            if (_escort119WarpUnstickClicks >= MoonRabbitWarpUnstickMaxClicks)
            {
                _escort119WarpUnstickPhase = 0;
                _escort119WarpUnstickDone = true;
                return false;
            }

            return ClickMoonRabbitWarpRestart(now, "second-after-2s");
        }

        if (_escortMapChangeAtMs > 0
            && now - _escortMapChangeAtMs < MoonRabbitWarpUnstickSettleMs)
        {
            return false;
        }

        if (IsMapLoading())
        {
            return true;
        }

        if (!atStuck)
        {
            return false;
        }

        return ClickMoonRabbitWarpRestart(now, "first");
    }

    /// <summary>15000：取消回芙蕾雅的任务导航，再走正向下一张。</summary>
    private static bool ClickMoonRabbitWarpRestart(long now, string tag)
    {
        _escort119WarpUnstickClicks++;
        var to15001 = _escort119WarpUnstickClicks <= 1;
        var floor = to15001 ? MoonRabbitWarpNextFloor : MoonRabbitWarpGoalFloor;
        var x = to15001 ? MoonRabbitWarpNextX : MoonRabbitWarpGoalX;
        var y = to15001 ? MoonRabbitWarpNextY : MoonRabbitWarpGoalY;
        WriteLog("119 warp-unstick cancel+nav #" + _escort119WarpUnstickClicks + " tag=" + tag
                 + " dest=(" + floor + "," + x + "," + y + ")");
        Tip(to15001
            ? "任务护航：过图卡住，取消回程改走下层"
            : "任务护航：再导航去星月噬灵者");
        ClearEscortStuckPending();
        StopTaskNavigation(false);
        ClearTaskPathSameIndexStopGuard();
        ForceEscortAutoWarpIndexZero();
        _escort119WarpUnstickPhase = 1;
        _escort119WarpUnstickAtMs = now;
        _lastActivityMs = now;
        if (!TryNavigateTo(floor, x, y, out var how))
        {
            WriteLog("119 warp-unstick nav fail how=" + how);
            PauseEscortOnConditionFail("119-warp-restart");
        }
        else
        {
            WriteLog("119 warp-unstick nav ok how=" + how);
        }

        return true;
    }

    /// <summary>侧栏手点是 AutoWarpIndex=0（去 400）。写回字段，避免官方续航再走表尾 100。</summary>
    private static void ForceEscortAutoWarpIndexZero()
    {
        try
        {
            var mission = GetSidebarTaskMissionData(_escortMissionId) ?? GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                WriteLog("119 ForceAutoWarpIndex miss");
                return;
            }

            var before = GetMember(mission, "AutoWarpIndex") ?? GetProp(mission, "AutoWarpIndex");
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = mission.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(flags))
                {
                    if (p.Name != "AutoWarpIndex" || !p.CanWrite)
                    {
                        continue;
                    }

                    try
                    {
                        p.SetValue(mission, Convert.ChangeType(0, p.PropertyType), null);
                    }
                    catch
                    {
                        // next
                    }
                }

                foreach (var f in t.GetFields(flags))
                {
                    if (f.Name != "AutoWarpIndex" && f.Name.IndexOf("AutoWarp", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        f.SetValue(mission, Convert.ChangeType(0, f.FieldType));
                    }
                    catch
                    {
                        // next
                    }
                }
            }

            var after = GetMember(mission, "AutoWarpIndex") ?? GetProp(mission, "AutoWarpIndex");
            WriteLog("119 ForceAutoWarpIndex before=" + before + " after=" + after);
        }
        catch (Exception ex)
        {
            WriteLog("119 ForceAutoWarpIndex EX " + RootMessage(ex));
        }
    }

    /// <summary>侧栏手点：优先用 Com_TaskItem.m_Info，只 AutoWarpIndex=0 + RunTask。</summary>
    private static bool ClickEscortTaskLikeMouse()
    {
        try
        {
            var mapLoading = IsMapLoading();
            var mission = GetSidebarTaskMissionData(_escortMissionId) ?? GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                WriteLog("ClickEscortTaskLikeMouse miss id=" + _escortMissionId);
                return false;
            }

            var src = GetSidebarTaskMissionData(_escortMissionId) != null ? "sidebar" : "holder";
            WriteLog("ClickEscortTaskLikeMouse src=" + src
                     + " mapLoading=" + mapLoading
                     + " " + FormatMissionNavDebug(mission));

            if (mapLoading)
            {
                WriteLog("ClickEscortTaskLikeMouse skip, MapLoading");
                return true;
            }

            try
            {
                SetProp(mission, "AutoWarpIndex", 0);
            }
            catch
            {
                SetMember(mission, "AutoWarpIndex", 0);
            }

            if (!InvokeTaskManagerRunTask(mission, out var how))
            {
                WriteLog("ClickEscortTaskLikeMouse invoke fail id=" + _escortMissionId);
                return false;
            }

            _prevRunTaskId = GetRunTaskId();
            WriteLog("ClickEscortTaskLikeMouse ok id=" + _escortMissionId
                     + " how=" + how
                     + " runId=" + _prevRunTaskId
                     + " " + FormatMissionNavDebug(mission));
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("ClickEscortTaskLikeMouse EX: " + RootMessage(ex));
            return false;
        }
    }

    private static object GetSidebarTaskMissionData(int missionId)
    {
        try
        {
            var panel = GetUiPanel("MissionSidebarPanel");
            if (panel == null)
            {
                return null;
            }

            var comTask = GetMember(panel, "m_Com_Task") ?? GetProp(panel, "m_Com_Task");
            if (comTask == null)
            {
                return null;
            }

            var pool = GetMember(comTask, "m_BtnPool") ?? GetProp(comTask, "m_BtnPool");
            if (pool is System.Collections.IDictionary dict)
            {
                foreach (var item in dict.Values)
                {
                    var info = GetMember(item, "m_Info") ?? GetProp(item, "m_Info");
                    if (info == null)
                    {
                        continue;
                    }

                    var id = Convert.ToInt32(GetMember(info, "id") ?? GetProp(info, "id") ?? 0);
                    if (id == missionId)
                    {
                        return info;
                    }
                }
            }

            var infos = GetMember(comTask, "m_Infos") ?? GetProp(comTask, "m_Infos");
            if (infos is System.Collections.IList list)
            {
                foreach (var info in list)
                {
                    if (info == null)
                    {
                        continue;
                    }

                    var id = Convert.ToInt32(GetMember(info, "id") ?? GetProp(info, "id") ?? 0);
                    if (id == missionId)
                    {
                        return info;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("GetSidebarTaskMissionData EX " + RootMessage(ex));
        }

        return null;
    }

    private static string FormatMissionNavDebug(object mission)
    {
        try
        {
            var aw = GetMember(mission, "AutoWarpIndex") ?? GetProp(mission, "AutoWarpIndex") ?? "?";
            var step = GetMember(mission, "missionStepNum") ?? GetProp(mission, "missionStepNum") ?? "?";
            var tp = GetProp(mission, "TargetPoint") ?? GetMember(mission, "TargetPoint");
            var tpDump = DumpObjectMembers(tp);
            var mp0 = "";
            try
            {
                var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
                var move = GetMember(script, "movePoint") as System.Collections.IList;
                if (move != null && move.Count > 0)
                {
                    var v3 = move[0];
                    mp0 = " move0=("
                          + (GetMember(v3, "x") ?? GetProp(v3, "x")) + ","
                          + (GetMember(v3, "y") ?? GetProp(v3, "y")) + ","
                          + (GetMember(v3, "z") ?? GetProp(v3, "z")) + ")";
                }
            }
            catch
            {
                mp0 = "";
            }

            return "step=" + step + " warp=" + aw + " TargetPoint{" + tpDump + "}" + mp0;
        }
        catch (Exception ex)
        {
            return "debugEX=" + RootMessage(ex);
        }
    }

    private static string DumpObjectMembers(object obj)
    {
        if (obj == null)
        {
            return "null";
        }

        var sb = new System.Text.StringBuilder();
        try
        {
            var t = obj.GetType();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                try
                {
                    sb.Append(f.Name).Append('=').Append(f.GetValue(obj));
                }
                catch
                {
                    sb.Append(f.Name).Append("=?");
                }
            }

            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                try
                {
                    sb.Append(p.Name).Append('=').Append(p.GetValue(obj, null));
                }
                catch
                {
                    sb.Append(p.Name).Append("=?");
                }
            }
        }
        catch
        {
            sb.Append(obj);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 清掉 TaskManager 的 sameIndex 停止守卫。
    /// 切图续航失败后首次 RunTask/手点常只清守卫不寻路；第二次才真正走——我们主动清掉避免「没反应」。
    /// </summary>
    private static void ClearTaskPathSameIndexStopGuard()
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm == null)
            {
                return;
            }

            var m = tm.GetType().GetMethod(
                "ClearTaskPathStopIfSameIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                m.Invoke(tm, null);
                WriteLog("ClearTaskPathStopIfSameIndex ok");
                return;
            }

            // 反射方法名失败时直接写字段
            try
            {
                SetMember(tm, "m_TaskPathStopIfSameTaskId", -1);
                SetMember(tm, "m_TaskPathStopIfSameStep", -1);
                SetMember(tm, "m_TaskPathStopIfSameIndex", -1);
                WriteLog("ClearTaskPathStopIfSameIndex via fields");
            }
            catch (Exception ex2)
            {
                WriteLog("ClearTaskPathStopIfSameIndex fields EX " + RootMessage(ex2));
            }
        }
        catch (Exception ex)
        {
            WriteLog("ClearTaskPathSameIndexStopGuard EX " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 官方任务导航是否仍在进行：寻路中 / 等切图续航 / 地图加载中。
    /// 为 true 时护航不得判卡图、不得再 RunTask（会清 m_TaskPathResumePending）。
    /// </summary>
    private static bool IsOfficialTaskPathBusy()
    {
        try
        {
            if (IsMapLoading())
            {
                return true;
            }

            var tm = GetManagerInstance("TaskManager");
            if (tm == null)
            {
                return false;
            }

            var pathing = Convert.ToBoolean(
                GetMember(tm, "m_IsTaskPathfindingActive")
                ?? GetProp(tm, "m_IsTaskPathfindingActive")
                ?? false);
            var resume = Convert.ToBoolean(
                GetMember(tm, "m_TaskPathResumePending")
                ?? GetProp(tm, "m_TaskPathResumePending")
                ?? false);
            var waitNpc = Convert.ToBoolean(
                GetMember(tm, "m_TaskPathWaitingNpcMapChange")
                ?? GetProp(tm, "m_TaskPathWaitingNpcMapChange")
                ?? false);
            return pathing || resume || waitNpc;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMapLoading()
    {
        try
        {
            var t = FindType("MapManager");
            if (t == null)
            {
                return false;
            }

            var p = t.GetProperty(
                "MapLoading", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (p != null)
            {
                return Convert.ToBoolean(p.GetValue(null, null) ?? false);
            }

            var f = t.GetField(
                "MapLoading", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return f != null && Convert.ToBoolean(f.GetValue(null) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalCaptain()
    {
        try
        {
            var main = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            var cap = GetCaptainUid();
            return !string.IsNullOrEmpty(main) && !string.IsNullOrEmpty(cap)
                   && string.Equals(main, cap, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 中秋 #119：步骤 2 开始时队长回登入点。
    /// 阿凯版：切图后再用赤凤之翼；哥拉尔版：登入点已在哥拉尔，回点后直接点任务。
    /// </summary>
    private static bool TryStartMoonRabbitStepSpecial(int stepNum, string reason)
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (stepNum != MoonRabbitLoginGateStep2 || _escort119GateDone2)
        {
            return false;
        }

        if (!TrySendEscortLoginGate())
        {
            return false;
        }

        _escort119GateDone2 = true;
        _escort119PendingAfterGateStep = stepNum;
        _escortLoginGatePending = true;
        _escortLoginGateAtMs = NowMs();
        WriteLog("119 login-gate " + reason + " step=" + stepNum
                 + " edition=" + (_midAutumnGoralEdition ? "goral" : "akai"));
        return true;
    }

    /// <summary>
    /// 中秋 #119 步骤 5 挑战暗影巡卫：开始前挂机传送「布朗山」（SendMisc Id=6），到图后再点任务导航。
    /// 已在布朗山则跳过。战斗中不标记完成，出战后重试。
    /// </summary>
    private static bool TryStartMoonRabbitBrownTeleport(int stepNum, string reason)
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (stepNum != MoonRabbitBrownMountainStep || _escort119TeleportDone5 || _escortHangupTeleportPending)
        {
            return false;
        }

        if (!IsLocalCaptain())
        {
            return false;
        }

        if (IsOnMoonRabbitBrownMountain())
        {
            _escort119TeleportDone5 = true;
            WriteLog("119 hangup-teleport brown skip already-there reason=" + reason);
            return false;
        }

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                WriteLog("119 hangup-teleport brown wait in-battle");
                return false;
            }
        }
        catch
        {
            // ignore
        }

        if (!TrySendHangupTeleport(MoonRabbitBrownMountainTeleportId))
        {
            return false;
        }

        _escort119TeleportDone5 = true;
        _escortHangupTeleportPending = true;
        _escortHangupTeleportAtMs = NowMs();
        _escortHangupTeleportExpectFloor = MoonRabbitBrownMountainFloor;
        Tip("任务护航：已传送布朗山");
        WriteLog("119 hangup-teleport brown " + reason + " id=" + MoonRabbitBrownMountainTeleportId);
        return true;
    }

    private static bool IsOnMoonRabbitBrownMountain()
    {
        try
        {
            int floor;
            string floorName;
            int mapResId;
            return TryGetCurrentMapInfo(out floor, out floorName, out mapResId)
                   && floor == MoonRabbitBrownMountainFloor;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 中秋 #119 步骤 7 调查星月落痕·礁石：开始前挂机传送「奇怪的洞窟怪」（SendMisc Id=2）。
    /// 已在洞窟内或已靠近礁石则跳过。战斗中不标记完成，出战后重试。
    /// </summary>
    private static bool TryStartMoonRabbitReefTeleport(int stepNum, string reason)
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (stepNum != MoonRabbitReefStep || _escort119TeleportDone7 || _escortHangupTeleportPending)
        {
            return false;
        }

        if (!IsLocalCaptain())
        {
            return false;
        }

        if (IsNearMoonRabbitReefOrCave())
        {
            _escort119TeleportDone7 = true;
            WriteLog("119 hangup-teleport skip already-near reason=" + reason);
            return false;
        }

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                WriteLog("119 hangup-teleport wait in-battle");
                return false;
            }
        }
        catch
        {
            // ignore
        }

        if (!TrySendHangupTeleport(MoonRabbitHangupTeleportId))
        {
            return false;
        }

        _escort119TeleportDone7 = true;
        _escortHangupTeleportPending = true;
        _escortHangupTeleportAtMs = NowMs();
        _escortHangupTeleportExpectFloor = MoonRabbitHangupTeleportFloor;
        Tip("任务护航：已传送奇怪的洞窟怪");
        WriteLog("119 hangup-teleport " + reason + " id=" + MoonRabbitHangupTeleportId);
        return true;
    }

    private static bool IsNearMoonRabbitReefOrCave()
    {
        try
        {
            int floor;
            string floorName;
            int mapResId;
            if (TryGetCurrentMapInfo(out floor, out floorName, out mapResId)
                && floor == MoonRabbitHangupTeleportFloor)
            {
                return true;
            }

            if (floor == MoonRabbitReefMapFloor && TryGetPlayerXY(out var x, out var y))
            {
                var dx = x - MoonRabbitReefX;
                var dy = y - MoonRabbitReefY;
                return dx * dx + dy * dy <= MoonRabbitReefNearDist * MoonRabbitReefNearDist;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>挂机导航「传送前往」：TaskManager.SendMisc(id)，Type=挂机传送。</summary>
    private static bool TrySendHangupTeleport(int navId)
    {
        if (!IsLocalCaptain() || navId <= 0)
        {
            return false;
        }

        try
        {
            if (GetEncounterStatus() != 0)
            {
                TrySendEscortAutoBattle("停止挂机");
            }

            StopTaskNavigation();
            var tm = GetManagerInstance("TaskManager");
            var cancel = tm?.GetType().GetMethod(
                "CancelTaskPathfinding",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            cancel?.Invoke(tm, null);

            var send = tm?.GetType().GetMethod(
                "SendMisc",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null);
            if (send == null)
            {
                send = tm?.GetType().GetMethod(
                    "SendMisc",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (send == null)
            {
                WriteLog("119 SendMisc method miss");
                return false;
            }

            send.Invoke(tm, new object[] { navId });
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("119 SendMisc EX " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>
    /// 中秋循环最后一步：停导航，全员把七夕礼盒兑换券存账号银行。任务会回到第一步，直接开下一轮。
    /// </summary>
    private static bool TryStartMoonRabbitLastStepBank(int stepNum, string reason)
    {
        if (!TempMidAutumnEscort119 || !_midAutumnLoopActive || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (stepNum != MoonRabbitLastStep || _escort119TicketBankDone || _escort119TicketBankPending)
        {
            return false;
        }

        if (!IsLocalCaptain())
        {
            return false;
        }

        StopTaskNavigation();
        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                WriteLog("119 ticket-bank wait in-battle");
                return true;
            }
        }
        catch
        {
            // ignore
        }

        FillMoonRabbitBankUids();
        _escort119TicketBankUidIndex = 0;
        _escort119TicketBankFailStreak = 0;
        _escort119TicketBankAwaitConfirm = false;
        _escort119TicketBankAnyStored = false;
        _escort119TicketBankDone = true;
        _escort119TicketBankPending = true;
        _escort119TicketBankAtMs = 0;
        _escortLastDiag = "分账号存兑换券";
        WriteLog("119 ticket-bank start " + reason + " n=" + _escort119TicketBankUids.Count);
        return true;
    }

    private static void FillMoonRabbitBankUids()
    {
        _escort119TicketBankUids.Clear();
        var uids = CollectTeamOrMultiUids();
        if (uids.Count == 0)
        {
            var cap = GetCaptainUid();
            if (!string.IsNullOrEmpty(cap))
            {
                uids.Add(cap);
            }
        }

        for (var i = 0; i < uids.Count; i++)
        {
            var uid = uids[i];
            if (!string.IsNullOrEmpty(uid) && !_escort119TicketBankUids.Contains(uid))
            {
                _escort119TicketBankUids.Add(uid);
            }
        }
    }

    private static bool AnyMoonRabbitUidHasTickets()
    {
        for (var i = 0; i < _escort119TicketBankUids.Count; i++)
        {
            if (CountBagItemByKeyword(_escort119TicketBankUids[i], MoonRabbitTicketKeyword) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void AbortMoonRabbitTicketBank(string tip, string reason, bool stopLoop)
    {
        _escort119TicketBankPending = false;
        _escortLastDiag = tip;
        WriteLog("119 ticket-bank abort " + reason + " fail=" + _escort119TicketBankFailStreak
                 + " stopLoop=" + stopLoop);
        if (stopLoop)
        {
            _midAutumnLoopActive = false;
            CancelEscort(true, tip);
            return;
        }

        PauseEscortOnConditionFail(reason);
    }

    private static void FinishMoonRabbitTicketBankNextRound(long now)
    {
        _escort119TicketBankPending = false;
        if (!EnsureCaptainHasWingOrStopLoop("after-ticket-bank"))
        {
            return;
        }

        _midAutumnLoopCount++;
        ResetMoonRabbitEscortFlags();
        _escortLastStepNum = GetEscortMissionStepNum();
        _lastActivityMs = now;
        Tip("中秋循环：已存兑换券，进入第 " + (_midAutumnLoopCount + 1) + " 轮");
        WriteLog("119 ticket-bank next-round count=" + _midAutumnLoopCount + " step=" + _escortLastStepNum);
        if (!ClickEscortTaskNav("119-after-ticket-bank"))
        {
            PauseEscortOnConditionFail("119-after-ticket-bank");
        }

        if (_visible && _tab == TabEscort)
        {
            try
            {
                RebuildEscortTab();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TickMoonRabbitTicketBank(long now)
    {
        StopTaskNavigation(false);
        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                return;
            }
        }
        catch
        {
            // ignore
        }

        if (_escort119TicketBankUids.Count == 0)
        {
            FillMoonRabbitBankUids();
        }

        if (_escort119TicketBankUids.Count == 0)
        {
            if (_escort119LastStepSinceMs == 0)
            {
                _escort119LastStepSinceMs = now;
            }

            if (now - _escort119LastStepSinceMs >= EscortTicketMissingWaitMs)
            {
                AbortMoonRabbitTicketBank("未找到可存券账号", "119-ticket-no-uid", false);
            }

            return;
        }

        if (_escort119TicketBankUidIndex < _escort119TicketBankUids.Count)
        {
            var uid = _escort119TicketBankUids[_escort119TicketBankUidIndex];
            var left = CountBagItemByKeyword(uid, MoonRabbitTicketKeyword);
            _escortLastDiag = "存兑换券 " + (_escort119TicketBankUidIndex + 1)
                              + "/" + _escort119TicketBankUids.Count
                              + " 余" + left
                              + " 失败" + _escort119TicketBankFailStreak + "/" + EscortTicketBankMaxFails;

            if (_escort119TicketBankAwaitConfirm)
            {
                if (now - _escort119TicketBankAtMs < EscortTicketBankAccountGapMs)
                {
                    return;
                }

                if (left <= 0)
                {
                    _escort119TicketBankFailStreak = 0;
                    _escort119TicketBankAwaitConfirm = false;
                    _escort119TicketBankUidIndex++;
                    _escort119TicketBankAtMs = now;
                    WriteLog("119 ticket-bank empty uid=" + uid
                             + " idx=" + _escort119TicketBankUidIndex
                             + "/" + _escort119TicketBankUids.Count);
                    if (_escort119TicketBankUidIndex >= _escort119TicketBankUids.Count
                        && _escort119TicketBankAnyStored)
                    {
                        Tip("中秋循环：全员兑换券已存入账号银行");
                    }

                    return;
                }

                _escort119TicketBankFailStreak++;
                WriteLog("119 ticket-bank still-have uid=" + uid + " left=" + left
                         + " fail=" + _escort119TicketBankFailStreak);
                if (_escort119TicketBankFailStreak >= EscortTicketBankMaxFails)
                {
                    AbortMoonRabbitTicketBank("存兑换券连续失败5次，已停止", "119-ticket-fail5", true);
                    return;
                }

                _escort119TicketBankAwaitConfirm = false;
            }

            if (_escort119TicketBankAtMs > 0
                && now - _escort119TicketBankAtMs < EscortTicketBankAccountGapMs)
            {
                return;
            }

            if (left <= 0)
            {
                _escort119TicketBankFailStreak = 0;
                _escort119TicketBankUidIndex++;
                _escort119TicketBankAtMs = now;
                if (_escort119TicketBankUidIndex >= _escort119TicketBankUids.Count
                    && _escort119TicketBankAnyStored)
                {
                    Tip("中秋循环：全员兑换券已存入账号银行");
                }

                return;
            }

            var sent = StoreBagItemsToAccountBank(uid, MoonRabbitTicketKeyword);
            _escort119TicketBankAtMs = now;
            if (!sent)
            {
                _escort119TicketBankAwaitConfirm = false;
                _escort119TicketBankFailStreak++;
                WriteLog("119 ticket-bank send-fail uid=" + uid
                         + " fail=" + _escort119TicketBankFailStreak);
                if (_escort119TicketBankFailStreak >= EscortTicketBankMaxFails)
                {
                    AbortMoonRabbitTicketBank("存兑换券连续失败5次，已停止", "119-ticket-send-fail5", true);
                }

                return;
            }

            _escort119TicketBankAnyStored = true;
            _escort119TicketBankAwaitConfirm = true;
            WriteLog("119 ticket-bank sent uid=" + uid + " left=" + left);
            return;
        }

        if (AnyMoonRabbitUidHasTickets())
        {
            for (var i = 0; i < _escort119TicketBankUids.Count; i++)
            {
                if (CountBagItemByKeyword(_escort119TicketBankUids[i], MoonRabbitTicketKeyword) > 0)
                {
                    _escort119TicketBankUidIndex = i;
                    _escort119TicketBankAwaitConfirm = false;
                    WriteLog("119 ticket-bank rescan still uid=" + _escort119TicketBankUids[i]);
                    break;
                }
            }

            return;
        }

        if (!_escort119TicketBankAnyStored)
        {
            if (_escort119LastStepSinceMs == 0)
            {
                _escort119LastStepSinceMs = now;
            }

            if (now - _escort119LastStepSinceMs >= EscortTicketMissingWaitMs)
            {
                _escort119TicketBankDone = false;
                AbortMoonRabbitTicketBank("未找到七夕礼盒兑换券", "119-ticket-missing", false);
                return;
            }

            _escort119TicketBankUidIndex = 0;
            _escort119TicketBankAwaitConfirm = false;
            if (_escort119TicketBankAtMs == 0)
            {
                _escort119TicketBankAtMs = now;
            }

            return;
        }

        var stepNow = GetEscortMissionStepNum();
        var rolledBack = stepNow >= 0 && stepNow != MoonRabbitLastStep;
        if (!rolledBack)
        {
            if (_escort119TicketBankAtMs == 0)
            {
                _escort119TicketBankAtMs = now;
                Tip("中秋循环：全员兑换券已存入账号银行");
            }

            if (now - _escort119TicketBankAtMs < EscortTicketBankWaitMs * 2)
            {
                return;
            }

            AbortMoonRabbitTicketBank("存券后任务未回到第一步", "119-ticket-bank-no-rollback", false);
            return;
        }

        FinishMoonRabbitTicketBankNextRound(now);
    }

    private static bool EnsureCaptainHasWingOrStopLoop(string reason)
    {
        if (_midAutumnGoralEdition)
        {
            return true;
        }

        if (CaptainHasMoonRabbitWing())
        {
            return true;
        }

        Tip("中秋阿凯版：队长背包没有赤凤之翼");
        WriteLog("119 wing missing " + reason);
        StopMidAutumnLoop();
        return false;
    }

    private static bool CaptainHasMoonRabbitWing()
    {
        var uid = GetCaptainUid();
        if (string.IsNullOrEmpty(uid))
        {
            uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
        }

        return CountBagItemByKeyword(uid, MoonRabbitWingKeyword) > 0;
    }

    private static int CountBagItemByKeyword(string uid, string keyword)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(keyword))
        {
            return 0;
        }

        try
        {
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var items = getItems?.Invoke(null, new object[] { uid }) as IList;
            if (items == null)
            {
                return 0;
            }

            var n = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || Convert.ToInt32(GetMember(item, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                if (!ItemDataMatchesKeyword(GetMember(item, "data"), keyword))
                {
                    continue;
                }

                var data = GetMember(item, "data");
                n += Convert.ToInt32(GetMember(data, "Pile") ?? GetProp(data, "Pile") ?? 1);
            }

            return n;
        }
        catch
        {
            return 0;
        }
    }

    private static bool ItemDataMatchesKeyword(object data, string keyword)
    {
        if (data == null || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
        var secret = Convert.ToString(GetMember(data, "Secretname") ?? "") ?? "";
        return name.IndexOf(keyword, StringComparison.Ordinal) >= 0
               || secret.IndexOf(keyword, StringComparison.Ordinal) >= 0;
    }

    private static bool StoreBagItemsToAccountBank(string uid, string keyword)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        // 存包与任务导航互斥，必须 Cancel 官方寻路
        StopTaskNavigation(false);
        try
        {
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var items = getItems?.Invoke(null, new object[] { uid }) as IList;
            if (items == null)
            {
                return false;
            }

            var indexes = new List<int>();
            for (var i = 8; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || Convert.ToInt32(GetMember(item, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var data = GetMember(item, "data");
                if (!ItemDataMatchesKeyword(data, keyword))
                {
                    continue;
                }

                var idx = Convert.ToInt32(GetMember(data, "Index") ?? i);
                if (!indexes.Contains(idx))
                {
                    indexes.Add(idx);
                }
            }

            if (indexes.Count == 0)
            {
                return false;
            }

            TryOpenRemoteAccountItemBank(uid);
            if (!TrySendAccountBankPutItems(uid, indexes))
            {
                return false;
            }

            WriteLog("119 store tickets uid=" + uid + " n=" + indexes.Count);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("119 store tickets EX uid=" + uid + " " + RootMessage(ex));
            return false;
        }
    }

    private static object ResolveAccountBankType()
    {
        try
        {
            var t = FindType("BANK_TYPE");
            if (t == null || !t.IsEnum)
            {
                return null;
            }

            try
            {
                return Enum.Parse(t, "ACCOUNT_BANK", ignoreCase: true);
            }
            catch
            {
                // fall through
            }

            foreach (var name in Enum.GetNames(t))
            {
                if (name.IndexOf("ACCOUNT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Enum.Parse(t, name);
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void TryOpenRemoteAccountItemBank(string uid)
    {
        try
        {
            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr != null)
            {
                SetMember(roleMgr, "OpenBankFromBag", true);
            }

            var actMgr = GetManagerInstance("ActivityManager");
            if (actMgr == null)
            {
                return;
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
                if (ps.Length == 4
                    && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(string))
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
                return;
            }

            var ps2 = send.GetParameters();
            if (ps2.Length >= 4)
            {
                send.Invoke(actMgr, new object[] { MoonRabbitAccountBankActivity, uid, 0, 19 });
            }
            else if (ps2.Length == 3)
            {
                send.Invoke(actMgr, new object[] { MoonRabbitAccountBankActivity, uid, 0 });
            }
            else if (ps2.Length == 2)
            {
                send.Invoke(actMgr, new object[] { MoonRabbitAccountBankActivity, uid });
            }
        }
        catch
        {
            // 打不开远程仓也不阻断后续「存道具」尝试
        }
    }

    private static bool TrySendAccountBankPutItems(string uid, List<int> indexList)
    {
        var roleMgr = GetManagerInstance("RoleManager");
        var bankType = ResolveAccountBankType();
        if (roleMgr == null || bankType == null || indexList == null || indexList.Count == 0)
        {
            return false;
        }

        MethodInfo sendBank = null;
        foreach (var m in roleMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "SendBankMessage")
            {
                continue;
            }

            var ps = m.GetParameters();
            if (ps.Length >= 4 && ps.Length <= 6)
            {
                sendBank = m;
                break;
            }
        }

        if (sendBank == null)
        {
            WriteLog("119 SendBankMessage method miss");
            return false;
        }

        var ps2 = sendBank.GetParameters();
        object[] args;
        if (ps2.Length >= 6)
        {
            args = new object[] { bankType, uid, "存道具", 0, 0, indexList };
        }
        else if (ps2.Length == 5)
        {
            args = new object[] { bankType, uid, "存道具", 0, 0 };
        }
        else
        {
            args = new object[] { bankType, uid, "存道具" };
        }

        sendBank.Invoke(roleMgr, args);
        return true;
    }

    private static bool UseCaptainBagItem(string keyword, bool requireUseFlag)
    {
        if (string.IsNullOrEmpty(keyword) || !IsLocalCaptain())
        {
            return false;
        }

        StopTaskNavigation(false);

        var cap = GetCaptainUid();
        if (string.IsNullOrEmpty(cap))
        {
            return false;
        }

        if (TryUseMemoryItem(cap, keyword, requireUseFlag))
        {
            Tip("任务护航：已使用队长的" + keyword);
            return true;
        }

        return false;
    }

    private static bool IsWingWizardPending()
    {
        return _escortUseItemPending || _scriptWingTestPending;
    }

    private static void ResetWingWizardState()
    {
        _escortWingWizardSeen = false;
        _escortWingWizardClosedAtMs = 0;
        _escortWingNextClicks = 0;
        _escortWingPickedDest = false;
    }

    /// <returns>0=仍在等, 1=弹窗已关, 2=一直没弹窗, -1=点窗超时</returns>
    private static int TickWingWizardProgress(long startedAtMs)
    {
        StopTaskNavigation(false);
        var now = NowMs();
        if (IsDialoguePanelOpen())
        {
            _escortWingWizardSeen = true;
            _escortWingWizardClosedAtMs = 0;
            return now - startedAtMs >= EscortWingWizardTimeoutMs ? -1 : 0;
        }

        if (!_escortWingWizardSeen)
        {
            return now - startedAtMs < EscortWingWizardAppearMs ? 0 : 2;
        }

        if (_escortWingWizardClosedAtMs == 0)
        {
            _escortWingWizardClosedAtMs = now;
            WriteLog("wing wizard closed, settle");
        }

        return now - _escortWingWizardClosedAtMs < EscortWingWizardSettleMs ? 0 : 1;
    }

    private static void RefreshScriptTabIfVisible()
    {
        if (!_visible || _tab != TabScript)
        {
            return;
        }

        try
        {
            ClearBody();
            BuildScriptBody();
            RefreshTabButtonLabels();
        }
        catch
        {
            // ignore
        }
    }

    private static void RunScriptWingTest()
    {
        if (_scriptWingTestPending)
        {
            _scriptWingTestPending = false;
            Tip("已取消赤凤之翼测试");
            WriteLog("script wing test cancel");
            RefreshScriptTabIfVisible();
            return;
        }

        if (_escortUseItemPending)
        {
            Tip("护航正在用赤凤之翼，请稍后再测");
            return;
        }

        if (_escortActive && !_escortPaused)
        {
            Tip("请先暂停任务护航再测赤凤之翼");
            return;
        }

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                Tip("战斗中不能使用赤凤之翼");
                return;
            }
        }
        catch
        {
            // ignore
        }

        var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
        if (string.IsNullOrEmpty(uid))
        {
            Tip("当前角色无效，无法使用赤凤之翼");
            return;
        }

        StopTaskNavigation(false);
        if (!TryUseMemoryItem(uid, MoonRabbitWingKeyword, false))
        {
            Tip("背包没有赤凤之翼");
            WriteLog("script wing test no item uid=" + uid);
            return;
        }

        ResetWingWizardState();
        _scriptWingTestPending = true;
        _scriptWingTestAtMs = NowMs();
        WriteLog("script wing test start uid=" + uid);
        RefreshScriptTabIfVisible();
    }

    private static void TickScriptWingTest()
    {
        if (!_scriptWingTestPending)
        {
            return;
        }

        TryAutoPickDialogue();
        var wait = TickWingWizardProgress(_scriptWingTestAtMs);
        if (wait == 0)
        {
            return;
        }

        _scriptWingTestPending = false;
        if (wait == 1)
        {
            Tip("赤凤之翼使用成功");
            WriteLog("script wing test ok");
        }
        else if (wait == 2)
        {
            Tip("赤凤之翼未弹出窗口");
            WriteLog("script wing test no window");
        }
        else
        {
            Tip("赤凤之翼弹窗未点完");
            WriteLog("script wing test timeout");
        }

        RefreshScriptTabIfVisible();
    }

    private static bool TrySendEscortLoginGate()
    {
        if (!IsLocalCaptain())
        {
            return false;
        }

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                WriteLog("119 SendLoginGate skip in-battle");
                return false;
            }

            var login = GetManagerInstance("LoginManager");
            var send = login?.GetType().GetMethod(
                "SendLoginGate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (send == null)
            {
                send = login?.GetType().GetMethod(
                    "SendLoginGate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (send == null)
            {
                WriteLog("119 SendLoginGate method miss");
                return false;
            }

            StopTaskNavigation(false);
            send.Invoke(login, null);
            StopTaskNavigation();
            Tip("任务护航：队长已回登入点");
            WriteLog("119 SendLoginGate ok step=" + _escortLastStepNum);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("119 SendLoginGate EX " + RootMessage(ex));
            return false;
        }
    }

    private static void TrySendEscortAutoBattle(string action)
    {
        try
        {
            var uid = GetCaptainUid();
            if (string.IsNullOrEmpty(uid))
            {
                return;
            }

            if (action == "停止挂机" && GetEncounterStatus() == 0)
            {
                return;
            }

            var roleMgr = GetManagerInstance("RoleManager");
            var send = roleMgr?.GetType().GetMethod(
                "SendAutoBattle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            send?.Invoke(roleMgr, new object[] { action, uid });
        }
        catch
        {
            // ignore
        }
    }

    private static void EnsureEscortEncounterOn()
    {
        if (GetEncounterStatus() != 0)
        {
            _escortStartedEncounter = true;
            return;
        }

        TrySendEscortAutoBattle("开始挂机");
        _escortStartedEncounter = true;
    }

    private static void EnterEscortEncounterWait()
    {
        if (_escortWaitItem)
        {
            EnsureEscortEncounterOn();
            return;
        }

        _escortWaitItem = true;
        _escortWaitAtStepNum = GetEscortMissionStepNum();
        _escortWasInBattle = false;
        try
        {
            var mission = GetMissionDataById(_escortMissionId);
            _escortWaitItemName = ParseObtainItemName(GetMissionStepConfig(mission));
        }
        catch
        {
            _escortWaitItemName = "";
        }

        EnsureEscortEncounterOn();
        WriteLog("escort encounter wait id=" + _escortMissionId
                 + " step=" + _escortWaitAtStepNum
                 + " item=" + _escortWaitItemName);
        Tip(string.IsNullOrEmpty(_escortWaitItemName)
            ? "任务护航：已开启遇敌，等待任务道具"
            : ("任务护航：已开启遇敌，等待获得" + _escortWaitItemName));
    }

    private static void StopEscortEncounterWait(string reason, bool tipContinue)
    {
        var wasWait = _escortWaitItem || _escortStartedEncounter;
        if (_escortStartedEncounter || (_escortWaitItem && GetEncounterStatus() != 0))
        {
            TrySendEscortAutoBattle("停止挂机");
        }

        _escortWaitItem = false;
        _escortStartedEncounter = false;
        _escortWasInBattle = false;
        _escortWaitAtStepNum = -1;
        if (!wasWait)
        {
            _escortWaitItemName = "";
            return;
        }

        WriteLog("escort encounter stop reason=" + reason + " item=" + _escortWaitItemName);
        if (tipContinue)
        {
            Tip(string.IsNullOrEmpty(_escortWaitItemName)
                ? "任务护航：遇敌已关闭，继续任务"
                : ("任务护航：已获得" + _escortWaitItemName + "，继续任务"));
        }

        _escortWaitItemName = "";
    }

    /// <summary>
    /// 模拟右侧任务点击：AutoWarpIndex=0 + TaskManager.RunTask（与改 15000 特例之前一致）。
    /// 卡楼梯：先 StopTaskNavigation 再点任务。遇敌无寻路点则进挂机等待。
    /// </summary>
    private static bool ClickEscortTaskNav(string reason)
    {
        try
        {
            if (_escortMissionId <= 0)
            {
                return false;
            }

            var mission = GetMissionDataById(_escortMissionId);
            if (mission == null)
            {
                WriteLog("ClickEscortTaskNav miss id=" + _escortMissionId + " reason=" + reason);
                return false;
            }

            // 卡楼梯恢复：先停掉随机挪格，再点任务，避免 MoveTo 互相取消
            if (reason != null && reason.IndexOf("stuck", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                StopTaskNavigation();
            }

            string prepFail;
            var prepOk = TryPrepareEscortMission(mission, out prepFail);
            var stepFlag = false;
            try
            {
                stepFlag = Convert.ToBoolean(GetMember(mission, "missionStepFlag") ?? false);
            }
            catch
            {
                // ignore
            }

            var hasMove = MissionHasMovePoints(mission);
            var encounterFarm = IsEncounterFarmStep(GetMissionStepConfig(mission));
            if (encounterFarm && !hasMove)
            {
                _escortLastDiag = "";
                WriteLog("ClickEscortTaskNav encounter-wait no move id=" + _escortMissionId
                         + " reason=" + reason);
                EnterEscortEncounterWait();
                return true;
            }

            if (!prepOk || !stepFlag || !hasMove)
            {
                var diag = DiagnoseEscortStepFail(mission);
                if (string.IsNullOrEmpty(diag))
                {
                    diag = string.IsNullOrEmpty(prepFail) ? "条件不满足" : prepFail;
                }

                if (!hasMove && (prepOk || stepFlag))
                {
                    diag = string.IsNullOrEmpty(diag) || diag == "条件不满足"
                        ? "无寻路点"
                        : (diag + "；无寻路点");
                }

                _escortLastDiag = diag;
                WriteLog("ClickEscortTaskNav prep fail id=" + _escortMissionId
                         + " prepOk=" + prepOk + " stepFlag=" + stepFlag + " move=" + hasMove
                         + " " + diag + " reason=" + reason);
                return false;
            }

            _escortLastDiag = "";

            try
            {
                var common = mission.GetType().GetMethod(
                    "CommonSetMissionStep",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                common?.Invoke(mission, null);
            }
            catch
            {
                // ignore
            }

            try
            {
                var st = Convert.ToString(GetMember(mission, "taskstatus") ?? "") ?? "";
                if (st.EndsWith("NotStart", StringComparison.Ordinal) || st == "0")
                {
                    var ended = FindType("MissionStatus");
                    if (ended != null && ended.IsEnum)
                    {
                        SetMember(mission, "taskstatus", Enum.Parse(ended, "Started"));
                    }
                }
            }
            catch
            {
                // ignore
            }

            var warpIndex = 0;
            if (encounterFarm)
            {
                try
                {
                    var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
                    var move = GetMember(script, "movePoint") as System.Collections.IList;
                    if (move != null && move.Count > 0)
                    {
                        warpIndex = move.Count - 1;
                    }
                }
                catch
                {
                    warpIndex = 0;
                }
            }

            try
            {
                SetProp(mission, "AutoWarpIndex", warpIndex);
            }
            catch
            {
                SetMember(mission, "AutoWarpIndex", warpIndex);
            }

            var moveCount = 0;
            var warpFloor = 0;
            var warpX = 0;
            var warpY = 0;
            try
            {
                var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
                var move = GetMember(script, "movePoint") as System.Collections.IList;
                moveCount = move != null ? move.Count : 0;
                if (move != null && warpIndex >= 0 && warpIndex < move.Count)
                {
                    var v3 = move[warpIndex];
                    warpFloor = Convert.ToInt32(GetMember(v3, "x") ?? GetProp(v3, "x") ?? 0);
                    warpX = Convert.ToInt32(GetMember(v3, "y") ?? GetProp(v3, "y") ?? 0);
                    warpY = Convert.ToInt32(GetMember(v3, "z") ?? GetProp(v3, "z") ?? 0);
                }
            }
            catch
            {
                // ignore
            }

            if (!InvokeTaskManagerRunTask(mission, out var invokeHow))
            {
                _escortLastDiag = "触发导航失败";
                WriteLog("ClickEscortTaskNav invoke fail id=" + _escortMissionId
                         + " reason=" + reason + " movePoints=" + moveCount);
                return false;
            }

            _prevRunTaskId = GetRunTaskId();
            _lastActivityMs = NowMs();

            try
            {
                var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
                var hint = Convert.ToString(GetMember(script, "stepStartHint") ?? "") ?? "";
                if (!string.IsNullOrEmpty(hint))
                {
                    Tip(hint);
                }
            }
            catch
            {
                // ignore
            }

            WriteLog("ClickEscortTaskNav ok id=" + _escortMissionId
                     + " reason=" + reason
                     + " how=" + invokeHow
                     + " runId=" + _prevRunTaskId
                     + " movePoints=" + moveCount
                     + " warpIndex=" + warpIndex
                     + " dest=(" + warpFloor + "," + warpX + "," + warpY + ")");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("ClickEscortTaskNav EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>清掉 MissionData.TargetPoint，避免 RunTask 仍走向切图前缓存点。</summary>
    private static void ClearMissionTargetPoint(object mission)
    {
        if (mission == null)
        {
            return;
        }

        try
        {
            var mpType = FindType("MapPoint");
            if (mpType == null)
            {
                return;
            }

            var empty = Activator.CreateInstance(mpType);
            try
            {
                SetProp(mission, "TargetPoint", empty);
            }
            catch
            {
                SetMember(mission, "TargetPoint", empty);
            }
        }
        catch (Exception ex)
        {
            WriteLog("ClearMissionTargetPoint EX " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 只停走路，不 CancelTaskPathfinding。
    /// 仅用于卡图恢复里「挪格前/点任务前」避免随机 MoveTo 与任务导航抢控制；
    /// 用道具、存包、传送等一律用 <see cref="StopTaskNavigation"/>。
    /// </summary>
    private static void StopWalkOnly()
    {
        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var walk = GetProp(pm, "walkSystem") ?? GetMember(pm, "walkSystem");
            if (walk == null)
            {
                return;
            }

            MethodInfo stop = null;
            foreach (var m in walk.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "StopMove")
                {
                    continue;
                }

                stop = m;
                break;
            }

            if (stop == null)
            {
                return;
            }

            var ps = stop.GetParameters();
            var args = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(bool))
                {
                    args[i] = true;
                }
                else if (ps[i].ParameterType.IsEnum || ps[i].ParameterType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(ps[i].ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            stop.Invoke(walk, args);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>官方切图后续航：TaskManager.TryResumeTaskPathAfterMapLoad。</summary>
    private static bool TryResumeEscortTaskPathAfterMap(string reason)
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm == null)
            {
                return false;
            }

            var pending = false;
            try
            {
                pending = Convert.ToBoolean(
                    GetMember(tm, "m_TaskPathResumePending")
                    ?? GetProp(tm, "m_TaskPathResumePending")
                    ?? false);
            }
            catch
            {
                pending = false;
            }

            if (!pending)
            {
                return false;
            }

            var m = tm.GetType().GetMethod(
                "TryResumeTaskPathAfterMapLoad",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return false;
            }

            var ok = Convert.ToBoolean(m.Invoke(tm, null) ?? false);
            if (ok)
            {
                _lastActivityMs = NowMs();
                WriteLog("escort TryResumeTaskPathAfterMapLoad ok reason=" + reason);
            }

            return ok;
        }
        catch (Exception ex)
        {
            WriteLog("TryResumeEscortTaskPathAfterMap EX " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>
    /// 调用 TaskManager.RunTask；HybridCLR 下优先 GetMethod 按名查找，再扫方法，最后手搓 TaskMoveTo。
    /// </summary>
    private static bool InvokeTaskManagerRunTask(object mission, out string how)
    {
        how = "";
        var tm = GetManagerInstance("TaskManager");
        if (tm == null)
        {
            WriteLog("InvokeRunTask TaskManager null");
            return false;
        }

        WriteLog("InvokeRunTask tmType=" + tm.GetType().FullName);
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        var missionType = FindType("MissionData") ?? mission.GetType();
        var types = new List<Type>();
        var tmType = FindType("TaskManager");
        if (tmType != null)
        {
            types.Add(tmType);
        }

        if (!types.Contains(tm.GetType()))
        {
            types.Add(tm.GetType());
        }

        // 1) 按名 GetMethod("RunTask", [MissionData])
        foreach (var type in types)
        {
            MethodInfo run = null;
            try
            {
                run = type.GetMethod("RunTask", flags, null, new[] { missionType }, null)
                      ?? type.GetMethod("RunTask", flags, null, new[] { mission.GetType() }, null)
                      ?? type.GetMethod("RunTask", flags);
            }
            catch (AmbiguousMatchException)
            {
                try
                {
                    foreach (var m in type.GetMethods(flags))
                    {
                        if (m.Name == "RunTask" && m.GetParameters().Length >= 1)
                        {
                            run = m;
                            if (m.GetParameters().Length == 1)
                            {
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
            catch (Exception ex)
            {
                WriteLog("InvokeRunTask GetMethod EX: " + RootMessage(ex));
            }

            if (run == null)
            {
                continue;
            }

            try
            {
                var ps = run.GetParameters();
                var args = new object[ps.Length];
                args[0] = mission;
                for (var i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[i].ParameterType)
                        : null;
                }

                run.Invoke(tm, args);
                how = "RunTask/" + type.Name;
                return true;
            }
            catch (Exception ex)
            {
                WriteLog("InvokeRunTask invoke EX: " + RootMessage(ex));
            }
        }

        // 2) StartWayThMissionStepByID
        foreach (var type in types)
        {
            MethodInfo start = null;
            try
            {
                start = type.GetMethod("StartWayThMissionStepByID", flags);
            }
            catch
            {
                // ignore
            }

            if (start == null)
            {
                continue;
            }

            try
            {
                var uid = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
                var id = Convert.ToInt32(GetMember(mission, "id") ?? GetProp(mission, "id") ?? _escortMissionId);
                var ps = start.GetParameters();
                var args = new object[ps.Length];
                args[0] = uid;
                args[1] = id;
                for (var i = 2; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[i].ParameterType)
                        : null;
                }

                start.Invoke(tm, args);
                how = "StartWayTh/" + type.Name;
                return true;
            }
            catch (Exception ex)
            {
                WriteLog("InvokeRunTask StartWay EX: " + RootMessage(ex));
            }
        }

        // 3) 手搓：currentExecuting + RunTaskId + MissionSystem.TaskMoveTo（带 OnTaskCallback）
        if (TryManualTaskMoveTo(tm, mission))
        {
            how = "ManualTaskMoveTo";
            return true;
        }

        WriteLog("InvokeRunTask all paths failed");
        return false;
    }

    /// <summary>复刻 TaskManager.RunTask 的寻路段，回调挂回 OnTaskCallback。</summary>
    private static bool TryManualTaskMoveTo(object tm, object mission)
    {
        try
        {
            var encounter = Convert.ToInt32(
                GetMember(GetStaticMember("PlayerDataHolder", "playerData"), "encounterStatus") ?? 0);
            if (encounter != 0)
            {
                Tip("自动挂机中无法行动！");
                return false;
            }

            var id = Convert.ToInt32(GetMember(mission, "id") ?? GetProp(mission, "id") ?? _escortMissionId);

            // MissionData.currentExecuting = mission
            var mdType = FindType("MissionData");
            if (mdType != null)
            {
                var curProp = mdType.GetProperty(
                    "currentExecuting", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (curProp != null && curProp.CanWrite)
                {
                    curProp.SetValue(null, mission, null);
                }
                else
                {
                    var curField = mdType.GetField(
                        "currentExecuting", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    curField?.SetValue(null, mission);
                }
            }

            try
            {
                SetProp(tm, "RunTaskId", id);
            }
            catch
            {
                SetMember(tm, "RunTaskId", id);
            }

            var script = GetProp(mission, "scriptData") ?? GetMember(mission, "scriptData");
            var movePoint = GetMember(script, "movePoint") as System.Collections.IList;
            if (movePoint == null || movePoint.Count == 0)
            {
                WriteLog("ManualTaskMoveTo no movePoint");
                return false;
            }

            var autoWarp = Convert.ToInt32(GetMember(mission, "AutoWarpIndex") ?? GetProp(mission, "AutoWarpIndex") ?? 0);
            if (autoWarp < 0 || autoWarp >= movePoint.Count)
            {
                autoWarp = 0;
            }

            var v3 = movePoint[autoWarp];
            var mapId = Convert.ToInt32(GetMember(v3, "x") ?? GetProp(v3, "x") ?? 0);
            var mx = Convert.ToInt32(GetMember(v3, "y") ?? GetProp(v3, "y") ?? 0);
            var my = Convert.ToInt32(GetMember(v3, "z") ?? GetProp(v3, "z") ?? 0);
            if (mapId == -999)
            {
                try
                {
                    var mmType = FindType("MapManager");
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type mono = null;
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (t.IsGenericTypeDefinition && t.Name == "MonoSingleton`1" && mmType != null)
                                {
                                    mono = t.MakeGenericType(mmType);
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        var inst = mono?.GetProperty(
                                "instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                            ?.GetValue(null, null);
                        if (inst != null)
                        {
                            mapId = Convert.ToInt32(GetMember(inst, "currentFloor") ?? mapId);
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            var mapPointType = FindType("MapPoint");
            if (mapPointType == null)
            {
                WriteLog("ManualTaskMoveTo MapPoint type missing");
                return false;
            }

            object mapPoint;
            try
            {
                // MapPoint(ushort map, ushort x, ushort y, Action)
                mapPoint = Activator.CreateInstance(
                    mapPointType,
                    (ushort)mapId,
                    (ushort)mx,
                    (ushort)my,
                    null);
            }
            catch
            {
                mapPoint = Activator.CreateInstance(mapPointType);
                try
                {
                    var ctor = mapPointType.GetConstructor(new[]
                    {
                        typeof(ushort), typeof(ushort), typeof(ushort), FindType("System.Action") ?? typeof(Action)
                    });
                    if (ctor != null)
                    {
                        mapPoint = ctor.Invoke(new object[] { (ushort)mapId, (ushort)mx, (ushort)my, null });
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("ManualTaskMoveTo MapPoint ctor EX: " + RootMessage(ex));
                    return false;
                }
            }

            // callback -> TaskManager.OnTaskCallback
            object callback = null;
            try
            {
                var cbMethod = tm.GetType().GetMethod(
                    "OnTaskCallback",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var errType = FindType("EWalkError");
                if (cbMethod != null && errType != null)
                {
                    var actionType = typeof(Action<,>).MakeGenericType(typeof(bool), errType);
                    callback = Delegate.CreateDelegate(actionType, tm, cbMethod);
                }
            }
            catch (Exception ex)
            {
                WriteLog("ManualTaskMoveTo callback EX: " + RootMessage(ex));
            }

            var msType = FindType("MissionSystem");
            MethodInfo taskMoveTo = null;
            if (msType != null)
            {
                foreach (var m in msType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (m.Name == "TaskMoveTo" && m.GetParameters().Length >= 2)
                    {
                        taskMoveTo = m;
                        break;
                    }
                }
            }

            if (taskMoveTo == null)
            {
                WriteLog("ManualTaskMoveTo TaskMoveTo missing");
                return false;
            }

            taskMoveTo.Invoke(null, new[] { mapPoint, callback });
            WriteLog("ManualTaskMoveTo ok id=" + id + " map=" + mapId + " xy=" + mx + "," + my);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("ManualTaskMoveTo EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>兼容旧名：等同 ClickEscortTaskNav。</summary>
    private static void ResumeEscortMission(string reason)
    {
        ClickEscortTaskNav(reason);
    }

    private static object GetMissionDataById(int missionId)
    {
        try
        {
            var uid = Convert.ToString(
                GetStaticMember("PlayerDataHolder", "MainPlayerUid")
                ?? GetStaticMember("PlayerDataHolder", "SelectPlayerUid")
                ?? "") ?? "";
            if (string.IsNullOrEmpty(uid))
            {
                return null;
            }

            var holder = FindType("MissionDataHolder");
            var get = holder?.GetMethod(
                "GetMissionDataFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (get == null)
            {
                return null;
            }

            var dictObj = get.Invoke(null, new object[] { uid });
            if (dictObj == null)
            {
                return null;
            }

            if (dictObj is System.Collections.IDictionary idict)
            {
                return idict.Contains(missionId) ? idict[missionId] : null;
            }

            var contains = dictObj.GetType().GetMethod("ContainsKey");
            if (contains != null && !Convert.ToBoolean(contains.Invoke(dictObj, new object[] { missionId })))
            {
                return null;
            }

            var item = dictObj.GetType().GetProperty("Item");
            return item?.GetValue(dictObj, new object[] { missionId });
        }
        catch (Exception ex)
        {
            WriteLog("GetMissionDataById EX: " + RootMessage(ex));
            return null;
        }
    }

    /// <summary>
    /// 有 UI_WindowsMessage / wmdb 选项时自动点选。
    /// 优先「发送/确定」类按钮（NPC 输入框：自动任务已填好内容后点发送）；
    /// 跳过取消/删除。LINEINPUT 窗口附带输入框文本作为 data。
    /// </summary>
    private static void TryAutoPickDialogue()
    {
        var now = NowMs();
        if (now - _lastDialogueClickMs < DialogueClickIntervalMs)
        {
            return;
        }

        try
        {
            if (!IsWingWizardPending() && TryClickNpcChatPanelSend())
            {
                _lastDialogueClickMs = now;
                _dialogueAutoClicks++;
                WriteLog("autoDialogue NPCChatPanel send");
                return;
            }

            // 输入框+取消/发送（ChangeName / WindowsMessage LINEINPUT）
            // 赤凤之翼分页窗也带确定+取消，不能走这条「直接点确定」捷径。
            if (!IsWingWizardPending() && TryClickNpcInputDialogSend())
            {
                _lastDialogueClickMs = now;
                _dialogueAutoClicks++;
                WriteLog("autoDialogue input-dialog send");
                return;
            }

            if (!IsDialoguePanelOpen())
            {
                return;
            }

            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return;
            }

            var wmdb = GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return;
            }

            var seqno = Convert.ToInt32(GetMember(wmdb, "seqno") ?? 0);
            // 同一窗未刷新前不连点
            if (seqno == _lastDialogueSeqno && now - _lastDialogueClickMs < DialogueClickIntervalMs * 2)
            {
                return;
            }

            var windowTypeObj = GetMember(wmdb, "windowType");
            var windowType = Convert.ToInt32(windowTypeObj ?? 0);
            var isLineInput = !IsWingWizardPending()
                              && (IsLineInputWindowType(windowTypeObj, windowType)
                                  || HasWindowsMessageInputField()
                                  || WmdbHasSendAndCancel(wmdb));

            // 输入框对话：优先点 UI「发送」
            if (isLineInput && TryClickWindowsMessageSendButton())
            {
                _lastDialogueClickMs = now;
                _lastDialogueSeqno = seqno;
                _dialogueAutoClicks++;
                WriteLog("autoDialogue LINEINPUT UI send seq=" + seqno);
                return;
            }

            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null || buttonData.Length == 0)
            {
                return;
            }

            int pickValue;
            string pickName;
            PickDialogueButton(buttonData, out pickValue, out pickName);

            if (pickValue < 0)
            {
                return;
            }

            int select;
            string data;
            if (pickValue > 64)
            {
                select = 0;
                data = (pickValue - 64).ToString();
            }
            else
            {
                select = pickValue;
                data = "";
            }

            // LINEINPUT / 发送+取消：把输入框（自动任务已填）内容带上
            if (isLineInput)
            {
                var inputText = ReadWindowsMessageInputText();
                if (!string.IsNullOrEmpty(inputText))
                {
                    data = inputText;
                }
            }

            var loc = GetStaticMember("PlayerDataHolder", "location");
            var x = Convert.ToInt32(GetMember(loc, "x") ?? GetMember(loc, "X") ?? 0);
            var y = Convert.ToInt32(GetMember(loc, "y") ?? GetMember(loc, "Y") ?? 0);
            var objindex = Convert.ToInt32(GetMember(wmdb, "objindex") ?? 0);
            var uid = Convert.ToString(GetMember(wmdb, "m_Uid") ?? "") ?? "";

            MethodInfo send8 = null;
            foreach (var m in npcMgr.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendWindows")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 8)
                {
                    send8 = m;
                    break;
                }
            }

            if (send8 == null)
            {
                WriteLog("SendWindows missing");
                return;
            }

            var psAll = send8.GetParameters();
            var args = new object[psAll.Length];
            args[0] = x;
            args[1] = y;
            args[2] = seqno;
            args[3] = objindex;
            args[4] = select;
            args[5] = data ?? "";
            args[6] = windowType;
            args[7] = uid;
            for (var i = 8; i < psAll.Length; i++)
            {
                if (psAll[i].ParameterType.IsEnum || psAll[i].ParameterType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(psAll[i].ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            send8.Invoke(npcMgr, args);
            _lastDialogueClickMs = now;
            _lastDialogueSeqno = seqno;
            _dialogueAutoClicks++;
            WriteLog("autoDialogue seq=" + seqno + " opt=" + pickName + " v=" + pickValue
                     + " wt=" + windowType + " lineInput=" + isLineInput
                     + " dataLen=" + (data == null ? 0 : data.Length));
        }
        catch (Exception ex)
        {
            WriteLog("TryAutoPickDialogue EX: " + RootMessage(ex));
        }
    }

    private static string NormalizeDialogueBtnName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == ' ' || ch == '\t' || ch == '\u3000')
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsDialogueCancelName(string name)
    {
        var n = NormalizeDialogueBtnName(name);
        return n == "取消" || n == "删除" || n == "关闭";
    }

    private static bool IsDialogueSendName(string name)
    {
        var n = NormalizeDialogueBtnName(name);
        return n == "发送" || n == "确定" || n == "确认" || n == "提交";
    }

    private static bool IsDialogueNextName(string name)
    {
        var n = NormalizeDialogueBtnName(name);
        return n == "下一步" || n == "下一页";
    }

    /// <summary>
    /// 赤凤之翼一类分页窗：buttonType 是 value|name|value|name（服务端下发）。
    /// 确定若排在下一步前面，先点确定会提前关窗。有哥拉尔选项则先点它。
    /// </summary>
    private static void PickDialogueButton(Array buttonData, out int pickValue, out string pickName)
    {
        pickValue = -1;
        pickName = null;
        var preferWing = IsWingWizardPending();
        int nextValue = -1;
        string nextName = null;
        int sendValue = -1;
        string sendName = null;
        int destValue = -1;
        string destName = null;
        int fallbackValue = -1;
        string fallbackName = null;
        string dump = preferWing ? "" : null;

        for (var i = 0; i < buttonData.Length && i < 9; i++)
        {
            var btn = buttonData.GetValue(i);
            if (btn == null)
            {
                continue;
            }

            var name = (Convert.ToString(GetMember(btn, "name") ?? "") ?? "").Trim();
            var value = Convert.ToInt32(GetMember(btn, "value") ?? -1);
            if (string.IsNullOrEmpty(name) || value < 0)
            {
                continue;
            }

            if (dump != null)
            {
                if (dump.Length > 0)
                {
                    dump += ",";
                }

                dump += name + "=" + value;
            }

            if (IsDialogueCancelName(name))
            {
                continue;
            }

            if (preferWing
                && name.IndexOf(MoonRabbitWingDestKeyword, StringComparison.Ordinal) >= 0)
            {
                destValue = value;
                destName = name;
                continue;
            }

            if (IsDialogueNextName(name) || value == WindowButtonNextValue)
            {
                nextValue = value;
                nextName = name;
                continue;
            }

            if (IsDialogueSendName(name) || value == 1 || value == 4)
            {
                sendValue = value;
                sendName = name;
                continue;
            }

            if (fallbackValue < 0)
            {
                fallbackValue = value;
                fallbackName = name;
            }
        }

        if (preferWing && dump != null)
        {
            WriteLog("wing wizard buttons " + dump
                     + " nextClicks=" + _escortWingNextClicks
                     + " pickedDest=" + _escortWingPickedDest);
        }

        if (preferWing && destValue >= 0 && !_escortWingPickedDest)
        {
            pickValue = destValue;
            pickName = destName;
            _escortWingPickedDest = true;
            return;
        }

        if (preferWing && nextValue >= 0 && !_escortWingPickedDest
            && _escortWingNextClicks < EscortWingMaxNextClicks)
        {
            pickValue = nextValue;
            pickName = nextName;
            _escortWingNextClicks++;
            return;
        }

        if (preferWing && sendValue >= 0)
        {
            pickValue = sendValue;
            pickName = sendName;
            return;
        }

        if (!preferWing && sendValue >= 0)
        {
            pickValue = sendValue;
            pickName = sendName;
            return;
        }

        if (!preferWing && nextValue >= 0)
        {
            pickValue = nextValue;
            pickName = nextName;
            return;
        }

        pickValue = fallbackValue;
        pickName = fallbackName;
        if (pickValue < 0 && sendValue >= 0)
        {
            pickValue = sendValue;
            pickName = sendName;
        }

        if (pickValue < 0 && nextValue >= 0)
        {
            pickValue = nextValue;
            pickName = nextName;
        }
    }

    private static bool IsLineInputWindowType(object windowTypeObj, int windowType)
    {
        // WINDOW_MESSAGETYPE_MESSAGEANDLINEINPUT=1, WIDEMESSAGEANDLINEINPUT=11
        if (windowType == 1 || windowType == 11)
        {
            return true;
        }

        try
        {
            var s = Convert.ToString(windowTypeObj ?? "") ?? "";
            return s.IndexOf("LINEINPUT", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>点 UI_WindowsMessage 上「发送」；没有发送文案则点第一个非取消可见按钮。</summary>
    private static bool TryClickWindowsMessageSendButton()
    {
        try
        {
            var panel = GetUiPanel("UI_WindowsMessage");
            if (panel == null || !IsUnityObjectActive(panel))
            {
                return false;
            }

            string[] names =
            {
                "m_Btn_Commond1", "m_Btn_Commond2", "m_Btn_Commond3", "m_Btn_Commond4",
                "m_Btn_Commond5", "m_Btn_Commond6", "m_Btn_Commond7", "m_Btn_Commond8",
                "m_Btn_Commond9"
            };
            object fallback = null;
            foreach (var fieldName in names)
            {
                var btn = GetMember(panel, fieldName);
                if (btn == null || !IsUnityObjectActive(btn))
                {
                    continue;
                }

                var title = GetCustomButtonTitle(btn);
                if (IsDialogueCancelName(title))
                {
                    continue;
                }

                if (IsDialogueSendName(title))
                {
                    if (InvokeButtonClick(btn))
                    {
                        return true;
                    }
                }

                if (fallback == null && !string.IsNullOrEmpty(NormalizeDialogueBtnName(title)))
                {
                    fallback = btn;
                }
            }

            return fallback != null && InvokeButtonClick(fallback);
        }
        catch (Exception ex)
        {
            WriteLog("TryClickWindowsMessageSendButton EX: " + RootMessage(ex));
        }

        return false;
    }

    /// <summary>
    /// 任务自动寻路到 NPC 且 scriptData.codePhrase 非空时打开：
    /// NPCChatPanel（输入框已填口令 + 发送/取消）。点发送 → ChatManager.SendTalk。
    /// </summary>
    private static bool TryClickNpcChatPanelSend()
    {
        try
        {
            var panel = GetUiPanel("NPCChatPanel");
            if (panel == null || !IsUnityObjectActive(panel))
            {
                return false;
            }

            var send = GetMember(panel, "m_Btn_Send");
            var cancel = GetMember(panel, "m_Btn_Cancel");
            if (send == null || !IsUnityObjectActive(send))
            {
                return false;
            }

            // 取消按钮存在即视为「输入框对话」形态（与护航任务口令一致）
            if (cancel != null && !IsUnityObjectActive(cancel))
            {
                // 仍尝试点发送：部分皮肤可能藏取消
            }

            // 优先调面板 SendMessage（与按钮 onClick 一致，带上输入框文本）
            try
            {
                var mi = panel.GetType().GetMethod(
                    "SendMessage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi != null)
                {
                    mi.Invoke(panel, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteLog("NPCChatPanel.SendMessage EX: " + RootMessage(ex));
            }

            return InvokeButtonClick(send);
        }
        catch (Exception ex)
        {
            WriteLog("TryClickNpcChatPanelSend EX: " + RootMessage(ex));
        }

        return false;
    }

    /// <summary>
    /// 碰到「输入框 + 取消/发送」就点发送（不看 MessageBox 文案）。
    /// </summary>
    private static bool TryClickNpcInputDialogSend()
    {
        if (TryClickNpcChatPanelSend())
        {
            return true;
        }

        // ChangeNamePanel：输入 + 取消 + 提交 → 有内容就点提交
        try
        {
            var panel = GetUiPanel("ChangeNamePanel");
            if (panel != null && IsUnityObjectActive(panel))
            {
                var input = GetMember(panel, "m_ITxt_Name");
                var text = "";
                if (input != null)
                {
                    text = Convert.ToString(GetProp(input, "text") ?? GetMember(input, "text") ?? "") ?? "";
                }

                var submit = GetMember(panel, "m_Btn_Submit");
                var cancel = GetMember(panel, "m_Btn_Cancel");
                if (!string.IsNullOrWhiteSpace(text)
                    && submit != null && IsUnityObjectActive(submit)
                    && cancel != null && IsUnityObjectActive(cancel)
                    && InvokeButtonClick(submit))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryClickNpcInputDialogSend ChangeName EX: " + RootMessage(ex));
        }

        // UI_WindowsMessage：LINEINPUT / 有输入框 / 发送+取消
        try
        {
            if (!IsWindowsMessagePanelOpen())
            {
                return false;
            }

            var npcMgr = GetManagerInstance("NpcManager");
            var wmdb = GetMember(npcMgr, "wmdb");
            var windowTypeObj = GetMember(wmdb, "windowType");
            var windowType = Convert.ToInt32(windowTypeObj ?? 0);
            var isInput = IsLineInputWindowType(windowTypeObj, windowType)
                          || HasWindowsMessageInputField()
                          || WmdbHasSendAndCancel(wmdb);
            if (!isInput)
            {
                return false;
            }

            return TryClickWindowsMessageSendButton();
        }
        catch (Exception ex)
        {
            WriteLog("TryClickNpcInputDialogSend WindowsMessage EX: " + RootMessage(ex));
        }

        return false;
    }

    private static bool WmdbHasSendAndCancel(object wmdb)
    {
        try
        {
            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null)
            {
                return false;
            }

            var hasSend = false;
            var hasCancel = false;
            for (var i = 0; i < buttonData.Length && i < 9; i++)
            {
                var btn = buttonData.GetValue(i);
                if (btn == null)
                {
                    continue;
                }

                var name = Convert.ToString(GetMember(btn, "name") ?? "") ?? "";
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (IsDialogueSendName(name))
                {
                    hasSend = true;
                }

                if (IsDialogueCancelName(name))
                {
                    hasCancel = true;
                }
            }

            return hasSend && hasCancel;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasWindowsMessageInputField()
    {
        return FindWindowsMessageInputField() != null;
    }

    private static object FindWindowsMessageInputField()
    {
        try
        {
            var panel = GetUiPanel("UI_WindowsMessage");
            if (panel == null)
            {
                return null;
            }

            var go = GetProp(panel, "gameObject") ?? GetMember(panel, "gameObject");
            if (go == null)
            {
                return null;
            }

            foreach (var typeName in new[] { "TMPro.TMP_InputField", "UnityEngine.UI.InputField" })
            {
                var t = FindType(typeName);
                if (t == null)
                {
                    continue;
                }

                MethodInfo getComps = null;
                foreach (var m in go.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != "GetComponentsInChildren" || !m.IsGenericMethodDefinition)
                    {
                        continue;
                    }

                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                    {
                        getComps = m.MakeGenericMethod(t);
                        break;
                    }
                }

                if (getComps == null)
                {
                    continue;
                }

                var arr = getComps.Invoke(go, new object[] { true }) as Array;
                if (arr != null && arr.Length > 0)
                {
                    return arr.GetValue(0);
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string GetCustomButtonTitle(object btn)
    {
        try
        {
            var title = GetMember(btn, "Title") ?? GetProp(btn, "Title");
            if (title == null)
            {
                return "";
            }

            var text = GetProp(title, "text") ?? GetMember(title, "text");
            return Convert.ToString(text ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool InvokeButtonClick(object btn)
    {
        try
        {
            var onClick = GetProp(btn, "onClick") ?? GetMember(btn, "onClick");
            if (onClick == null)
            {
                return false;
            }

            var invoke = onClick.GetType().GetMethod("Invoke", Type.EmptyTypes);
            if (invoke == null)
            {
                invoke = onClick.GetType().GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            }

            if (invoke == null)
            {
                return false;
            }

            invoke.Invoke(onClick, null);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("InvokeButtonClick EX: " + RootMessage(ex));
            return false;
        }
    }

    private static bool IsUnityObjectActive(object obj)
    {
        try
        {
            if (obj == null || IsUnityNull(obj))
            {
                return false;
            }

            var go = GetProp(obj, "gameObject") ?? GetMember(obj, "gameObject") ?? obj;
            var active = GetProp(go, "activeInHierarchy");
            if (active is bool b)
            {
                return b;
            }

            var activeSelf = GetProp(go, "activeSelf");
            return activeSelf is bool b2 && b2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读 UI_WindowsMessage 上 TMP/UGUI 输入框文本（自动任务常已填好）。</summary>
    private static string ReadWindowsMessageInputText()
    {
        try
        {
            var field = FindWindowsMessageInputField();
            if (field == null)
            {
                return "";
            }

            var text = Convert.ToString(GetProp(field, "text") ?? GetMember(field, "text") ?? "") ?? "";
            return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        }
        catch (Exception ex)
        {
            WriteLog("ReadWindowsMessageInputText EX: " + RootMessage(ex));
        }

        return "";
    }

    private static bool IsDialoguePanelOpen()
    {
        try
        {
            var chat = GetUiPanel("NPCChatPanel");
            if (chat != null && IsUnityObjectActive(chat))
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return IsWindowsMessagePanelOpen();
    }

    private static bool IsWindowsMessagePanelOpen()
    {
        try
        {
            var panel = GetUiPanel("UI_WindowsMessage");
            if (panel == null)
            {
                return false;
            }

            var go = GetProp(panel, "gameObject") ?? GetMember(panel, "gameObject");
            if (go != null)
            {
                var active = GetProp(go, "activeInHierarchy");
                if (active is bool b)
                {
                    return b;
                }

                var activeSelf = GetProp(go, "activeSelf");
                if (activeSelf is bool b2)
                {
                    return b2;
                }
            }

            // 退化：有按钮名即视为开着
            var npcMgr = GetManagerInstance("NpcManager");
            var wmdb = GetMember(npcMgr, "wmdb");
            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null)
            {
                return false;
            }

            for (var i = 0; i < buttonData.Length && i < 9; i++)
            {
                var name = Convert.ToString(GetMember(buttonData.GetValue(i), "name") ?? "");
                if (!string.IsNullOrEmpty(name))
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

    private static object GetUiPanel(string typeName)
    {
        var ui = FindType("UIManager");
        var panelType = FindType(typeName);
        if (ui == null || panelType == null)
        {
            return null;
        }

        foreach (var m in ui.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
        {
            if (m.Name != "GetUIPanel" || !m.IsGenericMethodDefinition)
            {
                continue;
            }

            try
            {
                return m.MakeGenericMethod(panelType).Invoke(null, null);
            }
            catch
            {
                // next
            }
        }

        return null;
    }

    private static string ModeLabel(string mode)
    {
        if (mode == ModeNine) return "九动";
        if (mode == ModeNopet2Act) return "无宠二动";
        if (mode == ModeCatch) return "抓宠";
        if (mode == ModeCatchSell) return "抓宠卖银币";
        if (mode == ModeSeal) return "烧卡";
        if (mode == ModeCatchNopet) return "抓宠（无宠二动）";
        if (mode == ModeLv1) return "遇1级自动";
        if (mode == ModeCountFarm) return "计数挂机";
        return "常规";
    }

    private static void SetPanelActive(bool active)
    {
        if (_canvasGo == null || IsUnityNull(_canvasGo))
        {
            return;
        }

        try
        {
            _canvasGo.GetType().GetMethod("SetActive", new[] { typeof(bool) })
                ?.Invoke(_canvasGo, new object[] { active });
            if (active)
            {
                ApplyMinimizedVisual();
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        if (!_visible)
        {
            return;
        }

        EnsurePanel();
        SetPanelActive(true);
        ApplyMinimizedVisual();
        if (!minimized)
        {
            ShowTab(_tab);
        }

        WriteLog("minimized=" + _minimized);
        Tip(_minimized ? "面板已最小化（左上角）" : "面板已展开");
    }

    private static void ApplyMinimizedVisual()
    {
        if (_shellGo != null && !IsUnityNull(_shellGo))
        {
            SetGoActive(_shellGo, !_minimized);
        }

        if (_miniFabGo != null && !IsUnityNull(_miniFabGo))
        {
            SetGoActive(_miniFabGo, _minimized);
        }
    }

    private static void SetGoActive(object go, bool active)
    {
        if (go == null || IsUnityNull(go))
        {
            return;
        }

        try
        {
            go.GetType().GetMethod("SetActive", new[] { typeof(bool) })
                ?.Invoke(go, new object[] { active });
        }
        catch
        {
            // ignore
        }
    }

    // ---------- UGUI helpers (same proven path) ----------

    private static object CreateGoWithComponents(string name, params Type[] components)
    {
        var goType = RequireType("UnityEngine.GameObject");
        var list = new List<Type>();
        foreach (var t in components)
        {
            if (t != null)
            {
                list.Add(t);
            }
        }

        var arr = list.ToArray();
        var ctor = goType.GetConstructor(new[] { typeof(string), typeof(Type[]) });
        if (ctor != null)
        {
            return ctor.Invoke(new object[] { name, arr });
        }

        var go = Activator.CreateInstance(goType, new object[] { name });
        foreach (var t in arr)
        {
            AddComp(go, t);
        }

        return go;
    }

    private static object CreateUiChild(object parent, string name, Type rtType)
    {
        var child = CreateGoWithComponents(name, rtType);
        var transform = GetProp(child, "transform");
        var parentTransform = GetProp(parent, "transform");
        transform.GetType().GetMethod("SetParent", new[] { RequireType("UnityEngine.Transform"), typeof(bool) })
            .Invoke(transform, new object[] { parentTransform, false });
        var v3 = FindType("UnityEngine.Vector3");
        var one = v3?.GetField("one", BindingFlags.Public | BindingFlags.Static);
        if (one != null)
        {
            SetProp(transform, "localScale", one.GetValue(null));
        }

        return child;
    }

    private static object RequireRect(object go, string tag)
    {
        var rt = GetComp(go, "UnityEngine.RectTransform");
        if (rt != null)
        {
            return rt;
        }

        var tr = GetProp(go, "transform");
        if (tr != null && tr.GetType().Name.IndexOf("RectTransform", StringComparison.Ordinal) >= 0)
        {
            return tr;
        }

        throw new InvalidOperationException("没有 RectTransform:" + tag);
    }

    private static object AddText(object go)
    {
        var t = FindType("UnityEngine.UI.Text") ?? FindType("TMPro.TextMeshProUGUI");
        if (t == null)
        {
            throw new InvalidOperationException("找不到 UI.Text");
        }

        var text = AddComp(go, t);
        var font = ResolveFont();
        if (font != null)
        {
            SetProp(text, "font", font);
        }

        SetProp(text, "color", MakeColor(0.95f, 0.95f, 0.95f, 1f));
        try
        {
            SetProp(text, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleCenter", 4));
        }
        catch
        {
            // ignore
        }

        return text;
    }

    private static void SetText(object text, string content, int fontSize)
    {
        if (text == null)
        {
            return;
        }

        SetProp(text, "text", content ?? "");
        SetProp(text, "fontSize", fontSize);
    }

    private static object ResolveFont()
    {
        try
        {
            var resources = FindType("UnityEngine.Resources");
            var fontType = FindType("UnityEngine.Font");
            var getBuiltin = resources?.GetMethod(
                "GetBuiltinResource", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Type), typeof(string) }, null);
            var f = getBuiltin?.Invoke(null, new object[] { fontType, "Arial.ttf" });
            if (f != null)
            {
                return f;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var objectType = FindType("UnityEngine.Object");
            var textType = FindType("UnityEngine.UI.Text");
            var find = objectType?.GetMethod(
                "FindObjectsOfType", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Type) }, null);
            var arr = find?.Invoke(null, new object[] { textType }) as Array;
            if (arr != null && arr.Length > 0)
            {
                return GetProp(arr.GetValue(0), "font");
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void BindButton(object go, object targetGraphic, Action action)
    {
        var btn = AddComp(go, "UnityEngine.UI.Button");
        if (targetGraphic != null)
        {
            SetProp(btn, "targetGraphic", targetGraphic);
        }

        var onClick = GetProp(btn, "onClick");
        var actionType = RequireType("UnityEngine.Events.UnityAction");
        var holder = new ClickHolder(action);
        var del = Delegate.CreateDelegate(actionType, holder, "Invoke");
        onClick.GetType().GetMethod("AddListener", new[] { actionType }).Invoke(onClick, new object[] { del });
    }

    private sealed class ClickHolder
    {
        private readonly Action _action;

        public ClickHolder(Action action)
        {
            _action = action;
        }

        public void Invoke()
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                WriteLog("button EX: " + RootMessage(ex));
            }
        }
    }

    private static void StretchFull(object rt)
    {
        SetProp(rt, "anchorMin", Vec2(0f, 0f));
        SetProp(rt, "anchorMax", Vec2(1f, 1f));
        SetProp(rt, "offsetMin", Vec2(0f, 0f));
        SetProp(rt, "offsetMax", Vec2(0f, 0f));
        SetProp(rt, "pivot", Vec2(0.5f, 0.5f));
    }

    private static void SetAnchoredCenter(object rt, float w, float h)
    {
        SetProp(rt, "anchorMin", Vec2(0.5f, 0.5f));
        SetProp(rt, "anchorMax", Vec2(0.5f, 0.5f));
        SetProp(rt, "pivot", Vec2(0.5f, 0.5f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(0f, 0f));
    }

    private static void SetAnchoredTop(object rt, float x, float y, float w, float h)
    {
        SetProp(rt, "anchorMin", Vec2(0.5f, 1f));
        SetProp(rt, "anchorMax", Vec2(0.5f, 1f));
        SetProp(rt, "pivot", Vec2(0.5f, 1f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static void SetAnchoredTopLeft(object rt, float x, float y, float w, float h)
    {
        SetProp(rt, "anchorMin", Vec2(0f, 1f));
        SetProp(rt, "anchorMax", Vec2(0f, 1f));
        SetProp(rt, "pivot", Vec2(0f, 1f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static object Vec2(float x, float y)
    {
        return Activator.CreateInstance(RequireType("UnityEngine.Vector2"), new object[] { x, y });
    }

    private static void SetColor(object graphic, float r, float g, float b, float a)
    {
        SetProp(graphic, "color", MakeColor(r, g, b, a));
    }

    private static object MakeColor(float r, float g, float b, float a)
    {
        return Activator.CreateInstance(RequireType("UnityEngine.Color"), new object[] { r, g, b, a });
    }

    private static object AddComp(object go, string typeName)
    {
        return AddComp(go, RequireType(typeName));
    }

    private static object AddComp(object go, Type t)
    {
        var existing = GetComp(go, t);
        if (existing != null)
        {
            return existing;
        }

        return go.GetType().GetMethod("AddComponent", new[] { typeof(Type) }).Invoke(go, new object[] { t });
    }

    private static object GetChild(object go, string childName)
    {
        if (go == null || string.IsNullOrEmpty(childName))
        {
            return null;
        }

        try
        {
            var tr = GetProp(go, "transform") ?? go;
            var find = tr.GetType().GetMethod("Find", new[] { typeof(string) });
            var found = find?.Invoke(tr, new object[] { childName });
            if (found != null)
            {
                var g = GetProp(found, "gameObject");
                return g ?? found;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static object GetComp(object go, string typeName)
    {
        return GetComp(go, FindType(typeName));
    }

    private static object GetComp(object go, Type t)
    {
        if (go == null || t == null)
        {
            return null;
        }

        return go.GetType().GetMethod("GetComponent", new[] { typeof(Type) }).Invoke(go, new object[] { t });
    }

    private static object GetProp(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        return p != null ? p.GetValue(obj, null) : null;
    }

    private static void SetProp(object obj, string name, object value)
    {
        if (obj == null)
        {
            return;
        }

        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, value, null);
        }
    }

    private static object GetMember(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null)
        {
            return p.GetValue(obj, null);
        }

        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return f != null ? f.GetValue(obj) : null;
    }

    private static void SetMember(object obj, string name, object value)
    {
        if (obj == null || string.IsNullOrEmpty(name))
        {
            return;
        }

        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, value, null);
            return;
        }

        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        f?.SetValue(obj, value);
    }

    private static object GetStaticMember(string typeName, string name)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null)
        {
            return p.GetValue(null, null);
        }

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return f != null ? f.GetValue(null) : null;
    }

    private static object EnumValue(string enumTypeName, string name, int fallback)
    {
        var t = FindType(enumTypeName);
        if (t != null && t.IsEnum)
        {
            try
            {
                return Enum.Parse(t, name);
            }
            catch
            {
                return Enum.ToObject(t, fallback);
            }
        }

        return fallback;
    }

    private static void CallStatic(Type type, string name, Type[] argTypes, object[] args)
    {
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, argTypes, null)?.Invoke(null, args);
    }

    private static bool IsUnityNull(object obj)
    {
        if (obj == null)
        {
            return true;
        }

        try
        {
            var objectType = FindType("UnityEngine.Object");
            var op = objectType?.GetMethod(
                "op_Equality", BindingFlags.Public | BindingFlags.Static, null,
                new[] { objectType, objectType }, null);
            if (op != null)
            {
                return (bool)op.Invoke(null, new object[] { obj, null });
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static byte[] LoadBytes(string assetPath)
    {
        // 游戏内正确入口是 FileUtil.LoadBytesFromHotfixAssets（桥接补丁同款）
        try
        {
            var fu = FindType("FileUtil");
            if (fu != null)
            {
                foreach (var name in new[] { "LoadBytesFromHotfixAssets", "LoadBytes" })
                {
                    var m = fu.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic, null,
                        new[] { typeof(string) }, null);
                    if (m == null)
                    {
                        continue;
                    }

                    var bytes = m.Invoke(null, new object[] { assetPath }) as byte[];
                    if (bytes != null && bytes.Length > 0)
                    {
                        return bytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("LoadBytes FileUtil EX: " + RootMessage(ex));
        }

        // 磁盘回退：cg37_Data/assets/hotfixdata/...
        try
        {
            var fileName = assetPath;
            var slash = assetPath.LastIndexOf('/');
            if (slash < 0)
            {
                slash = assetPath.LastIndexOf('\\');
            }

            if (slash >= 0)
            {
                fileName = assetPath.Substring(slash + 1);
            }

            foreach (var path in EnumerateHotfixAssetPaths(fileName, assetPath))
            {
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes != null && bytes.Length > 0)
                    {
                        WriteLog("LoadBytes disk " + path + " len=" + bytes.Length);
                        return bytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("LoadBytes disk EX: " + RootMessage(ex));
        }

        return null;
    }

    private static IEnumerable<string> EnumerateHotfixAssetPaths(string fileName, string assetPath)
    {
        var list = new List<string>();
        try
        {
            var dataPath = Convert.ToString(GetStaticMember("UnityEngine.Application", "dataPath") ?? "") ?? "";
            if (!string.IsNullOrEmpty(dataPath))
            {
                list.Add(Path.Combine(dataPath, "assets", "hotfixdata", fileName));
                list.Add(Path.Combine(dataPath, "StreamingAssets", "hotfixdata", fileName));
                list.Add(Path.Combine(dataPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            }

            var baseDir = GuessGameDir();
            if (!string.IsNullOrEmpty(baseDir))
            {
                list.Add(Path.Combine(baseDir, "cg37_Data", "assets", "hotfixdata", fileName));
                list.Add(Path.Combine(baseDir, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    /// <summary>护航面板是否显示「龙族循环 A/B」按钮：hotfixdata 存在 seqchapter_dragon_loop.flag 标记即显示。
    /// 傻瓜补丁分「带龙族」/「原版」两版，唯一差别就是这个标记。</summary>
    private static bool DragonLoopUiEnabled()
    {
        try
        {
            foreach (var path in EnumerateHotfixAssetPaths("seqchapter_dragon_loop.flag", "hotfixdata/seqchapter_dragon_loop.flag"))
            {
                if (File.Exists(path))
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

    private static bool CanLoadBytes(string assetPath)
    {
        var b = LoadBytes(assetPath);
        return b != null && b.Length > 0;
    }

    private static Type FindLoadedType(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = FindTypeInAsm(asm, typeName);
            if (t != null)
            {
                return t;
            }
        }

        return Type.GetType(typeName + ", " + typeName) ?? Type.GetType(typeName);
    }

    private static Type RequireType(string name)
    {
        return FindType(name) ?? throw new TypeLoadException(name);
    }

    private static Type FindType(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var t = Type.GetType(name);
        if (t != null)
        {
            return t;
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = FindTypeInAsm(asm, name);
            if (t != null)
            {
                return t;
            }
        }

        return null;
    }

    private static Type FindTypeInAsm(Assembly asm, string name)
    {
        try
        {
            return asm.GetType(name);
        }
        catch
        {
            return null;
        }
    }

    private static object GetManagerInstance(string typeName)
    {
        try
        {
            var mgrType = FindType(typeName);
            if (mgrType == null)
            {
                return null;
            }

            // 与日常 DLL 相同：沿继承链找 Instance（Manager<T>.Instance）
            for (var cur = mgrType; cur != null; cur = cur.BaseType)
            {
                var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
                            | BindingFlags.FlattenHierarchy;
                try
                {
                    var instProp = cur.GetProperty("Instance", flags);
                    var inst = instProp?.GetValue(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // next
                }

                try
                {
                    var getter = cur.GetMethod("get_Instance", flags, null, Type.EmptyTypes, null);
                    var inst = getter?.Invoke(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
                catch
                {
                    // next
                }
            }

            // 兜底：拼 Manager<T>
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type closed = null;
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.IsGenericTypeDefinition && t.Name == "Manager`1")
                        {
                            closed = t.MakeGenericType(mgrType);
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                var prop = closed?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    var inst = prop.GetValue(null, null);
                    if (inst != null)
                    {
                        return inst;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void Tip(string msg)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            var tip = notify?.GetType().GetMethod(
                "Tip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(bool) }, null);
            tip?.Invoke(notify, new object[] { msg, false });
        }
        catch
        {
            // ignore
        }
    }

    private static void EnsureLogBoot(string reason)
    {
        if (_bootLogged)
        {
            return;
        }

        _bootLogged = true;
        EnsureLogPath();
        WriteLog("======== SeqChapterTestUi/ModPanel boot (" + reason + ") ========");
        WriteLog("pid=" + Process.GetCurrentProcess().Id);
        WriteLog("logPath=" + GetLogPath());
    }

    private static void EnsureLogPath()
    {
        if (!string.IsNullOrEmpty(_logPath))
        {
            return;
        }

        var dir = GuessGameDir() ?? Environment.CurrentDirectory ?? Path.GetTempPath();
        try
        {
            dir = Path.GetFullPath(dir);
        }
        catch
        {
            dir = Path.GetTempPath();
        }

        _logPath = Path.Combine(dir, LogFileName);
    }

    private static string GuessGameDir()
    {
        try
        {
            Type app = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                app = FindTypeInAsm(asm, "UnityEngine.Application");
                if (app != null)
                {
                    break;
                }
            }

            var dataPath = app?.GetProperty("dataPath", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null) as string;
            if (!string.IsNullOrEmpty(dataPath))
            {
                var parent = Directory.GetParent(dataPath);
                if (parent != null)
                {
                    return parent.FullName;
                }
            }
        }
        catch
        {
            // ignore
        }

        return Environment.CurrentDirectory;
    }

    private static long NowMs()
    {
        return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static string RootMessage(Exception ex)
    {
        while (ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        return ex.GetType().Name + ": " + ex.Message;
    }
}

public sealed class SeqChapterTestUiHost : MonoBehaviour
{
    private void Awake()
    {
        SeqChapterTestUi.WriteLog("Host.Awake");
    }

    private void Update()
    {
        SeqChapterTestUi.Tick();
    }
}
