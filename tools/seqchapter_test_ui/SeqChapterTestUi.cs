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
    /// <summary>官方单回合 180s 超时（即将断线）单独记一笔，不跟主日志混在一起。</summary>
    public const string RoundTimeoutLogFileName = "SeqChapterTestUi.round_timeout.log";

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
    private const string ModeCatchWild = "catch_wild";
    private const string ModeSeal = "seal";
    private const string ModeCatchNopet = "catch_nopet";
    private const string ModeLv1 = "lv1";
    private const string ModeCountFarm = "count_farm";

    private static readonly object LogLock = new object();
    private static readonly object RoundTimeoutLogLock = new object();
    private static readonly Dictionary<string, long> MoshiBuffReqMs =
        new Dictionary<string, long>(StringComparer.Ordinal);
    private static string _logPath;
    private static string _roundTimeoutLogPath;
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
    /// <summary>本步骤第 1 次卡位：清路径后等这么久再官方点任务。</summary>
    private const long EscortStuckFirstAbortWaitMs = 500;
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
    /// <summary>中元抓宠 A/B/C；抓野生宠默认三种都抓。</summary>
    private const string ZhongyuanPetA = "幽灵";
    private const string ZhongyuanPetB = "僵尸";
    private const string ZhongyuanPetC = "骷髅战士";
    /// <summary>指定号：A→2号 B→3号 C→4号（1号抓，账号宠行中转）。</summary>
    private const int ZhongyuanSlotPetA = 1;
    private const int ZhongyuanSlotPetB = 2;
    private const int ZhongyuanSlotPetC = 3;
    private static readonly string[] WildPetPresets = { ZhongyuanPetA, ZhongyuanPetB, ZhongyuanPetC };
    /// <summary>A/B/C 抓宠路径稍后规划，仅预留空表。C 测试按钮走骷髅战士导航。</summary>
    private static readonly NavWaypoint[] ZhongyuanHuntPathA = new NavWaypoint[0];
    private static readonly NavWaypoint[] ZhongyuanHuntPathB = new NavWaypoint[0];
    private static readonly NavWaypoint[] ZhongyuanHuntPathC =
    {
        new NavWaypoint { Name = "402切4400", Floor = 402, X = 118, Y = 100 },
        new NavWaypoint { Name = "4400切4403", Floor = 4400, X = 106, Y = 54 },
        new NavWaypoint { Name = "4403站位", Floor = 4403, X = 26, Y = 38 },
        new NavWaypoint { Name = "快要崩裂的墙壁", Floor = 4403, X = 28, Y = 38 },
        new NavWaypoint { Name = "过墙后(41,39)", Floor = 4403, X = 41, Y = 39 },
        new NavWaypoint { Name = "C落点", Floor = 4404, X = 70, Y = 8 }
    };
    private static string _wildPetName = "";
    private static object _wildPetNameInput;
    /// <summary>
    /// 兑换野生宠（仅1号点一次，不切号）：回城点2 → 1500(47,75) → 找中元使者
    /// → 仓检234个人仓 → 远程从234个人仓各取1只存账号仓 → 1号取3只
    /// → NPC第2项 → 循环到凑不齐一套 → 1号把中元礼盒兑换券存账号道具仓。
    /// 开仓后必关仓。
    /// </summary>
    private const int WildExIdle = 0;
    private const int WildExReturn = 1;
    private const int WildExNav = 2;
    private const int WildExFindNpc = 3;
    private const int WildExScanDestOpen = 10;
    private const int WildExScanDestWait = 11;
    private const int WildExMemOpenPersonal = 20;
    private const int WildExMemWaitPersonal = 21;
    private const int WildExMemTake = 22;
    private const int WildExMemOpenAccount = 23;
    private const int WildExMemWaitAccount = 24;
    private const int WildExMemStore = 25;
    private const int WildExCapOpenAccount = 30;
    private const int WildExCapWaitAccount = 31;
    private const int WildExCapTake = 32;
    private const int WildExLookNpc = 40;
    private const int WildExPickOption = 41;
    private const int WildExConfirm = 42;
    private const int WildExWaitDone = 43;
    private const int WildExStoreTicket = 50;
    private const long WildExWaitListTimeoutMs = 8000;
    /// <summary>账号仓刚开时常先刷出空列表，空仓至少等这么久才当真。</summary>
    private const long WildExAccountEmptySettleMs = 1500;
    private const long WildExProtocolGapMs = 1000;
    private const long WildExPollMs = 1500;
    private const long WildExReturnWaitMs = 8000;
    private const long WildExNavWaitMs = 60000;
    private const long WildExNpcLookRetryMs = 2500;
    private const long WildExNpcWaitMs = 15000;
    private const int WildExMaxTries = 3;
    private const int WildExNavMaxTries = 6;
    private const int WildExAccountOpenMaxTries = 3;
    private const int WildExNpcFloor = 1500;
    private const int WildExNpcStandX = 47;
    private const int WildExNpcStandY = 75;
    private const string WildExNpcName = "中元使者";
    private const int WildExOptionIndex = 2;
    private const string WildExTicketKeyword = "中元礼盒兑换券";
    private const string AccountPetBankActivity = "远程账号宠物仓库";
    private static bool _wildExActive;
    private static int _wildExPhase;
    private static long _wildExDelayUntilMs;
    private static long _wildExWaitListStartMs;
    private static long _wildExLastTipMs;
    private static long _wildExActionAtMs;
    private static long _wildExLastLookMs;
    private static int _wildExStepTries;
    private static int _wildExRound;
    private static int _wildExNpcObj;
    private static int _wildExNpcFoundX;
    private static int _wildExNpcFoundY;
    private static int _wildExPetsBeforeTalk;
    private static bool _wildExInLoop;
    private static string _wildExNote = "";
    private static string _wildExTargetName = "";
    private static string _wildExWorkUid = "";
    private static int _wildExScanIndex;
    private static readonly int[] _wildExDestHave = new int[3];
    private static readonly int[] _wildExDestTotal = new int[3];
    private static readonly List<int> _wildExBankIndexes = new List<int>();
    private static int _wildExTakePos;
    private static int _wildExAccountOpenTries;
    private static string _lastAppliedWildCatchName = "";
    private static object _wildExInfoBefore;
    private static object _wildExStoreBefore;
    /// <summary>中元抓宠：按 234 个人仓还缺几只来抓（兑换前各满 15）；可从半路接着跑。</summary>
    private const int ZhongyuanQuota = 15;
    private const int ZhongyuanAccountSlots = 5;
    private const int ZyCatchIdle = 0;
    private const int ZyCatchCount = 1;
    private const int ZyCatchHunt = 2;
    private const int ZyCatchOpenStore = 3;
    private const int ZyCatchWaitStore = 4;
    private const int ZyCatchStore = 5;
    private const int ZyXferIdle = 0;
    private const int ZyXferPushOpenPersonal = 1;
    private const int ZyXferPushWaitPersonal = 2;
    private const int ZyXferPushTake = 3;
    private const int ZyXferPushOpenAccount = 4;
    private const int ZyXferPushWaitAccount = 5;
    private const int ZyXferPushStore = 6;
    private const int ZyXferPushWaitEmpty = 7;
    /// <summary>倒腾前先丢掉 1 号身上多余的幽灵/僵尸/骷髅战士，避免栏满卡死。</summary>
    private const int ZyXferPushTrimBody = 8;
    private const int ZyXferPullOpenAccount = 20;
    private const int ZyXferPullWaitAccount = 21;
    private const int ZyXferPullTake = 22;
    private const int ZyXferPullOpenPersonal = 23;
    private const int ZyXferPullWaitPersonal = 24;
    private const int ZyXferPullStore = 25;
    private const int ZyXferDrop = 30;
    private const long ZhongyuanWaitListTimeoutMs = 8000;
    private const long ZhongyuanProtocolGapMs = 1000;
    private const long ZhongyuanPollMs = 1500;
    private static bool _zyCatchActive;
    private static int _zyCatchPhase;
    private static bool _zyXferActive;
    private static int _zyXferPhase;
    private static bool _zyXferPush;
    private static string _zyXferWorkUid = "";
    private static string _zyName = "";
    private static int _zyDestSlot = -1;
    private static string _zyNote = "";
    private static long _zyDelayUntilMs;
    private static long _zyWaitListStartMs;
    private static int _zyPersonalMatch;
    private static int _zyPersonalStart;
    private static int _zyAccountMatch;
    private static int _zyAccountTotal;
    /// <summary>1 号身上+个人仓要凑到的只数（= 234 还缺 − 账号仓已有）。</summary>
    private static int _zyCatchFill;
    /// <summary>234 还缺几只（15 − 仓检已有）。</summary>
    private static int _zyXferNeed;
    /// <summary>本轮 1 号已推进账号仓的只数。</summary>
    private static int _zyXferSent;
    /// <summary>交够后把个人仓多余目标种取到身上再丢。</summary>
    private static bool _zyXferTakeForDrop;
    private static int _zyDestHave;
    private static int _zyScanStep;
    private static int _zyScanDestHave;
    private static int _zyScanAccountHave;
    private static readonly List<int> _zyWorkIndexes = new List<int>();
    private static int _zyWorkPos;
    private static int _zyCatchStuckStoreIndex = -1;
    private static int _zyCatchStuckStoreCount;
    private static object _zyInfoBefore;
    private static object _zyStoreBefore;
    private static object _zyStatusText;
    /// <summary>中元抓齐：仓检 234 后按缺额抓；已满 15 的种跳过。指定号只接收对应名字。</summary>
    private static bool _zyAllActive;
    private static int _zyAllIndex;
    private static int _zyAllPhase;
    private static string _zyAllNote = "";
    private static long _zyAllDelayUntilMs;
    private static long _zyAllWaitStartMs;
    private static int _zyAllExpectFloor;
    private static int _zyAllReturnTries;
    private static bool _zyLastCatchOk;
    private static bool _zyLastXferOk;
    private static bool _zyAllCatchStarted;
    private static bool _zyAllXferStarted;
    private static bool _skCNavForAll;
    /// <summary>中元幽灵：前往 WayId=1003 后临时占用护航队列，到 52018 再还。</summary>
    private static bool _zyLingTangOwnsEscort;
    private static readonly List<EscortCandidate> _zyLingTangSavedQueue = new List<EscortCandidate>();
    private static int _zyLingTangSavedIndex = -1;
    private static long _zyAllWaitBattleSinceMs;
    /// <summary>中元循环：仓检 → 抓三种 → 兑换 → 再仓检。</summary>
    private static bool _zyLoopActive;
    private static int _zyLoopPhase;
    private static int _zyLoopRound;
    private static int _zyLoopScanIndex;
    private static readonly int[] _zyLoopDestHave = new int[3];
    private static readonly int[] _zyLoopDestBankMatch = new int[3];
    private static readonly int[] _zyLoopDestBankTotal = new int[3];
    private static readonly int[] _zyLoopDestBankCap = new int[3];
    private static readonly int[] _zyLoopDestBankFree = new int[3];
    private static readonly bool[] _zyLoopDestTimedOut = new bool[3];
    private static readonly string[] _zyLoopDestOthers = new string[3];
    private static readonly int[] _zyLoopAccountHave = new int[3];
    private static readonly int[] _zyLoopLocalHave = new int[3];
    private static int _zyLoopAccountTotal;
    private static bool _zyLoopAccountTimedOut;
    private static int _zyLoopAccountOpenTries;
    private static int _zyLoopLocalBankTotal;
    private static int _zyLoopLocalBankCap;
    private static int _zyLoopLocalBankFree;
    private static string _zyLoopReport = "";
    private static string _zyLoopNote = "";
    private static long _zyLoopLastTipMs;
    private static bool _zyAllCompletedOk;
    private static string _zyPendingBankUid = "";
    private static string _zyBankFpBefore = "";
    private static readonly List<string> _zyRecentBankUids = new List<string>();
    private static readonly List<string> _zyRecentBankFps = new List<string>();
    private static bool _zyDestFullVerifyPending;
    private const int ZyLoopIdle = 0;
    private const int ZyLoopScanDest = 1;
    private const int ZyLoopScanDestWait = 2;
    private const int ZyLoopScanAccount = 3;
    private const int ZyLoopScanAccountWait = 4;
    private const int ZyLoopScanLocal = 5;
    private const int ZyLoopScanLocalWait = 6;
    private const int ZyLoopCatch = 7;
    private const int ZyLoopExchange = 8;
    private const int ZyAllIdle = 0;
    private const int ZyAllReturn = 1;
    private const int ZyAllWaitReturn = 2;
    private const int ZyAllTeleport = 3;
    private const int ZyAllWaitTeleport = 4;
    private const int ZyAllNavC = 5;
    private const int ZyAllCatchWait = 6;
    private const int ZyAllXferWait = 7;
    private const int ZyAllScan = 8;
    private const int ZyScanDestOpen = 0;
    private const int ZyScanDestWait = 1;
    private const int ZyScanAccountOpen = 2;
    private const int ZyScanAccountWait = 3;
    private const int ZyScanLocalOpen = 4;
    private const int ZyScanLocalWait = 5;
    private const int ZhongyuanHangupAId = 3;
    private const int ZhongyuanHangupAWayId = 1003;
    private const int ZhongyuanHangupAFloor = 52018;
    private const long ZhongyuanHangupGoTimeoutMs = 600000;
    private const long ZhongyuanHangupTeleportTimeoutMs = 25000;
    private const int ZhongyuanHangupBId = 2;
    private const int ZhongyuanHangupBFloor = 52140;
    /// <summary>骷髅战士导航：护航 #1008 到 402 打断，再走路切 4400→4403 点墙；(41,39) 再去 4404。15000/15001 按普通护航过图，不做掐路径特例。</summary>
    private const int SkCMissionId = 1008;
    private const int SkCStopEscortFloor = 402;
    private const int SkCHangupFloor = 52418;
    private const int SkCWarp402X = 118;
    private const int SkCWarp402Y = 100;
    private const int SkCWarp4400X = 106;
    private const int SkCWarp4400Y = 54;
    private const int SkCPhaseIdle = 0;
    private const int SkCPhaseEscort = 1;
    private const int SkCPhaseAbortWait = 2;
    private const int SkCPhaseNav402 = 3;
    private const int SkCPhaseNav4400 = 4;
    private const int SkCPhaseNav4403 = 5;
    private const int SkCPhaseTalkWall = 6;
    /// <summary>点墙传送后本图导航 (41,39)，再跨图去 4404。不是任务导航。</summary>
    private const int SkCPhaseNavVia = 7;
    private const int SkCPhaseNav4404 = 8;
    private const long SkCAbortWaitMs = 2000;
    private const string SkCWallNpcName = "快要崩裂的墙壁";
    /// <summary>4403 站 (26,38)，再跟 (28,38) 的「快要崩裂的墙壁」对话。</summary>
    private const int SkCWallStandX = 26;
    private const int SkCWallStandY = 38;
    private const int SkCWallNpcX = 28;
    private const int SkCWallNpcY = 38;
    /// <summary>点墙传送后落在墙东，本图导航去 (41,39)，再跨图 4404(70,8)。不是任务导航。</summary>
    private const int SkCViaX = 41;
    private const int SkCViaY = 39;
    private static bool _skCNavActive;
    private static int _skCNavPhase;
    private static string _skCNavNote = "";
    private static bool _skCNavOwnsEscort;
    private static readonly List<EscortCandidate> _skCNavSavedQueue = new List<EscortCandidate>();
    private static int _skCNavSavedIndex = -1;
    private static long _skCNavAbortUntilMs;
    private static long _skCNavLastNavMs;
    private static long _skCNavLastNpcMs;
    private static long _skCNavLastActivityMs;
    private static long _skCNavStuckMoveAtMs;
    private static int _skCNavStuckFails;
    private static int _skCNavStuckShuffles;
    private static bool _skCNavStuckNavTriedFirst;
    private static bool _skCNavStuckPending;
    private static bool _skCNavSawDialogue;
    private static bool _skCNavLastOk;
    private static int _skCNavLastX;
    private static int _skCNavLastY;
    private static int _skCNavLastFloor = int.MinValue;
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
    /// <summary>上一 tick 是否在战斗；用于退战边沿重置静止基线。</summary>
    private static bool _escortPrevInBattle;
    /// <summary>护航要求队伍至少人数（战后不足则暂停等手动继续）。</summary>
    private const int EscortTeamMinMembers = 5;
    /// <summary>战斗页：PVE 清表现队列让 RunProcess 自然 OnCompleted，再等 CHAR。默认关。</summary>
    private static bool _skipBattleAnim = false;
    private static bool _skipBattleAnimFlushLogged;
    private static long _skipBattleAnimLastDiagMs;
    private static long _skipBattleAnimCmdSinceMs;
    private static bool _skipBattleAnimManualDone;
    private static long _skipBattleAnimLastFlushMs;
    private static long _skipBattleAnimAllEndStuckSinceMs;
    private static long _skipBattleAnimAllEndTipMs;
    /// <summary>flush 后短时间内不把 AllEnd 空队列当卡死（毫秒）。</summary>
    private const long SkipAnimFlushCooldownMs = 2500;
    /// <summary>上一拍 select-done 队长是否无法行动（用来抓石化边沿：刚中 / 刚结束）。</summary>
    private static bool _skipBattleAnimPrevPlayerUnable;
    private static bool _skipBattleAnimHasPrevPlayerUnable;
    private static long _skipBattleAnimPrevSelectAtMs;
    /// <summary>AllEnd+空队列空等 ACTION 多久后 Tip。只提示，不在这里强退。</summary>
    private const long SkipAnimAllEndStuckTipMs = 8000;
    /// <summary>官方 m_SingleRoundMonitorTime 到此秒数写独立超时日志（180 会断线）。</summary>
    private const int OfficialSingleRoundTimeoutSec = 180;
    private const int RoundTimeoutLogAtSec = 170;
    private static int _roundTimeoutLoggedTurn = int.MinValue;
    /// <summary>表现队列已 Stop 仍不 OnCompleted 才补一次（过短会双开 NextRound）。</summary>
    private const long SkipAnimForceFinishMs = 3000;
    /// <summary>
    /// flush+Stop 会立刻 OnCompleted→NextRound。若 CHAR 已在队里，石化号 RefreshOtherBattleUI 会马上发 N，
    /// 服务端还没收齐上一拍就丢指令，之后不再下 ACTION。先把 CHAR 拿开，等 PLAYER 入队；
    /// 首号 PLAYER_MENU_NON 再多等 SkipAnimMenuNonHoldMs（对齐官方首号 AutoFight 3 秒）。
    /// </summary>
    private static readonly List<object> _skipAnimHeldChars = new List<object>();
    private static bool _skipAnimHoldActive;
    private static long _skipAnimHoldSinceMs;
    private const long SkipAnimMenuNonHoldMs = 3000;
    private const long SkipAnimHoldMaxMs = 8000;
    /// <summary>无法行动容错：按账号+回合去重。</summary>
    private static string _battleUnableActFixKey = "";
    private static bool _battleUnableActInjected;
    /// <summary>本账号本回合已强制补发人物 idle「N」（防死亡静默跳过不发包）。</summary>
    private static string _battleUnableActPlayerIdleKey = "";
    /// <summary>本账号本回合已强制补发宠物 idle。</summary>
    private static string _battleUnableActPetIdleKey = "";
    /// <summary>AllEnd+空队列卡死时补发去重。</summary>
    private static string _battleUnableActStuckRescueKey = "";
    private static long _battleUnableActStuckSinceMs;
    /// <summary>卡死计时签名（turn|acct|fight|acctQ）；变化则重置，避免正常选指令累计超时。</summary>
    private static string _battleUnableActStuckSig = "";
    private const int BpFlagPlayerMenuNon = 4;
    private const int BpFlagPetMenuNon = 8;
    private const int BpFlagPet = 0x20;
    private const long BcUnableActMask = 0x2L | 0x20L | 0x40L; // 死亡 | 睡眠 | 石化
    private const int FightProcessPlayerEnd = 1;
    private const int FightProcessPetEnd = 2;
    private const int FightProcessAllEnd = 3;
    /// <summary>无法行动账号选指令卡住才强制收尾（毫秒）。</summary>
    private const long BattleUnableActSelectStuckMs = 2500;
    /// <summary>fight≠AllEnd 且 acctQ=0 幽灵态才强制 AllEnd（毫秒）。</summary>
    private const long BattleGhostStuckMs = 4000;
    /// <summary>无法行动且 AllEnd+空队列才补发 idle（毫秒）。</summary>
    private const long BattleEmptyQueueStuckMs = 5000;
    // BATTLE_TYPE：P_vs_P=2 WATCH=3 PVP_WATCH=9 REPLAY_BATTLE=10（跳过动画不处理）
    private const int BattleTypePvp = 2;
    private const int BattleTypeWatch = 3;
    private const int BattleTypePvpWatch = 9;
    private const int BattleTypeReplay = 10;
    /// <summary>遇敌步骤：必须先到达本步导航点附近才开遇敌（格）。</summary>
    private const int EscortEncounterArriveNear = 4;
    /// <summary>
    /// 中秋 #119 月宫救兔护航特例（已卸：护航页不再显示七夕阿凯/哥拉尔按钮）。
    /// 实现仍留在本文件；卸前快照见 tools/seqchapter_escort_loops_backup/。
    /// </summary>
    private const bool TempMidAutumnEscort119 = false;
    /// <summary>中秋 #119：队长回登入点后等待切图（登入点在阿凯鲁法）。</summary>
    private static bool _escortLoginGatePending;
    private static long _escortLoginGateAtMs;
    private const long EscortLoginGateWaitMs = 4000;
    /// <summary>中秋 #119 步骤 2：回登入点后使用赤凤之翼，再等弹窗点完再点任务。</summary>
    private static bool _escortUseItemPending;
    private static long _escortUseItemAtMs;
    /// <summary>掐掉缓存路径后，等 2 秒再官方点任务（或阿凯版先用羽毛）。</summary>
    private static bool _escort119OfficialResumePending;
    private static long _escort119OfficialResumeAtMs;
    /// <summary>阿凯版第一次取消后：等 2 秒再用赤凤之翼，而不是点任务。</summary>
    private static bool _escort119ResumeUseWing;
    private static bool _escortWingWizardSeen;
    private static long _escortWingWizardClosedAtMs;
    private static int _escortWingNextClicks;
    private static bool _escortWingPickedDest;
    /// <summary>赤凤之翼弹窗出现等待。服务端 LSSPROTO_WINDOWS 分页，可能晚半拍才开。</summary>
    private const long EscortWingWizardAppearMs = 4000;
    private const long EscortWingWizardSettleMs = 2000;
    private const long EscortWingWizardTimeoutMs = 20000;
    private const int EscortWingMaxNextClicks = 12;
    private const int WindowButtonNextValue = 0x20;
    private const string MoonRabbitWingDestKeyword = "哥拉尔";
    /// <summary>脚本页「测试赤凤之翼」：独立点分页窗，不推进护航。</summary>
    private static bool _scriptWingTestPending;
    private static long _scriptWingTestAtMs;
    /// <summary>通用法兰治疗：回城点2 → 1000(82,83)切图1111 → (7,33) → 点迪拉 → 全队回复。脚本页与七夕循环共用。</summary>
    private const int FloraHealRecordIndex = 2;
    private const int FloraHealReturnFloor = 1000;
    private const int FloraHealReturnX = 63;
    private const int FloraHealReturnY = 79;
    private const int FloraHealDoorFloor = 1000;
    private const int FloraHealDoorX = 82;
    private const int FloraHealDoorY = 83;
    private const int FloraHealHospitalFloor = 1111;
    private const int FloraHealStandX = 7;
    private const int FloraHealStandY = 33;
    private const int FloraHealNpcX = 7;
    private const int FloraHealNpcY = 32;
    private const string FloraHealNpcName = "资深护士迪拉";
    private const string FloraHealNpcShortName = "迪拉";
    private const string FloraHealOptionName = "全队回复";
    private const int FloraHealOptionIndex = 2;
    private const int FloraHealMaxTries = 3;
    private const long FloraHealStepDelayMs = 1000;
    private const long FloraHealReturnWaitMs = 8000;
    private const long FloraHealDoorWaitMs = 25000;
    private const long FloraHealStandWaitMs = 20000;
    private const long FloraHealLookRetryMs = 2500;
    private const int FloraHealPhaseIdle = 0;
    private const int FloraHealPhaseReturn = 1;
    private const int FloraHealPhaseDelayAfterReturn = 2;
    private const int FloraHealPhaseToDoor = 3;
    private const int FloraHealPhaseDelayAfterDoor = 4;
    private const int FloraHealPhaseToStand = 5;
    private const int FloraHealPhaseDelayAfterStand = 6;
    private const int FloraHealPhaseLookNpc = 7;
    private const int FloraHealPhaseDelayAfterLook = 8;
    private const int FloraHealPhasePick = 9;
    /// <summary>治疗前回队长背包丢名字为「绿头盔」「红头盔」的道具（七夕每轮；一件一丢）。</summary>
    private const int FloraHealPhaseDropHelmets = 10;
    private const int FloraHealBagStart = 8;
    private const int FloraHealBagEnd = 68;
    private const long FloraHealDropDelayMs = 1000;
    private static bool _floraHealActive;
    private static bool _floraHealResumeEscort;
    private static bool _floraHealNeedRetry;
    private static int _floraHealPhase;
    private static int _floraHealStepTries;
    private static long _floraHealDelayUntilMs;
    private static long _floraHealActionAtMs;
    private static long _floraHealLastLookMs;
    private static string _floraHealNote = "";
    private static bool _floraHealLastOk;
    private static object _floraHealStatusText;
    /// <summary>脚本页「自动全套脚本」：计数挂机→日常→法兰治疗→回登入点→33200 门关→33500 切 33000→遇敌。</summary>
    private const int FullScriptNav1Floor = 33200;
    private const int FullScriptNav1X = 167;
    private const int FullScriptNav1Y = 108;
    private const int FullScriptNav2Floor = 33500;
    private const int FullScriptNav2X = 32;
    private const int FullScriptNav2Y = 14;
    private const int FullScriptNav2WarpFloor = 33000;
    private const string FullScriptNpcName = "门关管理人";
    private const int FullScriptNpcX = 167;
    private const int FullScriptNpcY = 107;
    private const int FullScriptPhaseIdle = 0;
    private const int FullScriptPhaseCountFarm = 1;
    private const int FullScriptPhaseDaily = 2;
    private const int FullScriptPhaseWaitDaily = 3;
    private const int FullScriptPhaseFloraHeal = 4;
    private const int FullScriptPhaseWaitFlora = 5;
    private const int FullScriptPhaseLoginGate = 6;
    private const int FullScriptPhaseWaitLogin = 7;
    private const int FullScriptPhaseNav1 = 8;
    private const int FullScriptPhaseTalkNpc = 9;
    private const int FullScriptPhaseNav2 = 10;
    private const int FullScriptPhaseWaitWarp = 11;
    private const int FullScriptPhaseEncounter = 12;
    private const long FullScriptDailyWaitMs = 180000;
    private const long FullScriptFloraWaitMs = 120000;
    private const long FullScriptLoginWaitMs = 15000;
    private const long FullScriptLoginSettleMs = 2000;
    private const long FullScriptNavWaitMs = 90000;
    private const long FullScriptNavRetryMs = 4000;
    private const long FullScriptTalkWaitMs = 40000;
    private const long FullScriptTalkRetryMs = 2500;
    private const long FullScriptTalkSettleMs = 1000;
    private const long FullScriptWarpWaitMs = 45000;
    private const long FullScriptWarpSettleMs = 1500;
    private static bool _fullScriptActive;
    private static int _fullScriptPhase;
    private static string _fullScriptNote = "";
    private static object _fullScriptStatusText;
    private static long _fullScriptPhaseAtMs;
    private static long _fullScriptLastActionMs;
    private static int _fullScriptLoginFromFloor;
    private static bool _fullScriptTalkPicked;
    private static bool _fullScriptAtWarpTile;
    private const int MoonRabbitMissionId = 119;
    private const int MoonRabbitLoginGateStep2 = 2;
    /// <summary>调查星月落痕·石碑（执行序 StepID=6）：挂机传送哈巴鲁洞穴后再导航。</summary>
    private const int MoonRabbitSteleStep = 6;
    /// <summary>battle_tbautobattlenavigationconfig Id=1「哈巴鲁洞穴」。</summary>
    private const int MoonRabbitHabaruTeleportId = 1;
    /// <summary>哈巴鲁洞穴 传送落点 floor（配置 Map.floor=11003）。</summary>
    private const int MoonRabbitHabaruTeleportFloor = 11003;
    private const int MoonRabbitSteleMapFloor = 100;
    private const int MoonRabbitSteleX = 695;
    private const int MoonRabbitSteleY = 333;
    private const int MoonRabbitSteleNearDist = 25;
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
    /// 彻底掐掉缓存路径，等 2 秒，再官方点任务（与哥拉尔羽毛落地相同）。
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
    /// <summary>步骤 2 怨念囚灵魔：倒序表头，真正目的地（表尾 2000 是法兰）。</summary>
    private const int MoonRabbitStep2GoalFloor = 47005;
    private const int MoonRabbitStep2GoalX = 52;
    private const int MoonRabbitStep2GoalY = 56;
    /// <summary>赤凤之翼落到哥拉尔城。</summary>
    private const int MoonRabbitGoralLandFloor = 43100;
    /// <summary>从 43100 出发 GeneralPointMoveTo(47005) 的第一跳；在此停住就要再推一把。</summary>
    private const int MoonRabbitGoralHopFloor = 43000;
    /// <summary>翅膀落地后还在 43100/43000，需继续推向 47005。</summary>
    private static bool _escort119GoralForwardPending;
    /// <summary>赤凤之翼关窗时所在地图；用来等飞到哥拉尔再导航。</summary>
    private static int _escortWingFromFloor;
    private const long MoonRabbitWarpUnstickSettleMs = 1500;
    /// <summary>第一次导航后固定隔 2 秒再试一次。</summary>
    private const long MoonRabbitWarpUnstickRetryMs = 2000;
    private const int MoonRabbitWarpUnstickMaxClicks = 2;
    /// <summary>0=无 1=已点过，等第二次。</summary>
    private static int _escort119WarpUnstickHandledStep = -1;
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
    private static bool _escort119TeleportDone6;
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
    /// <summary>本步骤第 1 次卡位：清路径后等 2 秒再官方点任务（与 15000/传送相同）。</summary>
    private static bool _escortStuckAbortResumePending;
    private static long _escortStuckAbortResumeAtMs;
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
    /// <summary>龙3/4 用完记忆/意志后再点任务；在原 1.5s 上再加 1s。</summary>
    private const long DragonUseMemoryDelayMs = 2500;
    /// <summary>A 线：龙族纷争 1-4 全量。</summary>
    private static readonly int[] DragonMissionIds = { 110, 111, 112, 113 };
    /// <summary>当前循环实际执行的任务集。</summary>
    private static int[] _dragonMissionIds;
    private const string DragonTitleKeyword = "龙族纷争";
    /// <summary>存包腾位阶段已重试发包次数。</summary>
    private static int _dragonStoreRetries;
    private const int DragonStoreMaxRetries = 5;
    /// <summary>存包后等待空位的复检次数（兼容银行回包滞后）。</summary>
    private static int _dragonStoreRechecks;
    private const int DragonStoreMaxRechecks = 18;
    private const long DragonStoreWaitMs = 4500;
    private const long DragonStoreRetryWaitMs = 3500;
    /// <summary>本阶段已发出存宠包数（用于回包滞后时容错继续）。</summary>
    private static int _dragonStoreSentCount;
    /// <summary>存包前全队已占宠物格总数（用于检测是否已腾出位）。</summary>
    private static int _dragonStoreBaselineUsed;
    private const int DragonStoreForceContinueRechecks = 6;
    /// <summary>phase2 判断可接时，因重置回包可能滞后，允许重试等待的次数与间隔。</summary>
    private static int _dragonCheckRetries;
    private const int DragonCheckMaxRetries = 5;
    private const long DragonCheckRetryMs = 1500;
    private const int StorePetLevel = 1;
    private const int PetStatusRest = 0;

    /// <summary>七夕 #119 循环（已卸面板入口；实现保留备用）。</summary>
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

    // ----- 超级AI（纯提示：不改出手、不关 VIP、不发包） -----
    private static bool _superAiActive;
    private static string _superAiLastHintKey = "";
    private static string _superAiLastSimLine = "";
    private static object _superAiStatusText;
    private static object _superAiBattleRoot;
    private static object _superAiHintCanvas;
    private static object _superAiHintText;
    private static int _superAiHintTurn;
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
        public long Bc;
        public string Status;
        public bool Unable;
        public string Suggest;
        public string Uid;
        public string JobName;
        public string JobAncestry;
    }

    private struct SuperAiPotion
    {
        public string Name;
        public int Power;
        public int Count;
        public int BagIndex;
        public int Type;
    }

    private struct SuperAiPlannedCmd
    {
        public string Actor;
        public string Str;
        public string Label;
    }

    private static readonly List<SuperAiPlannedCmd> _superAiPlannedCmds = new List<SuperAiPlannedCmd>();
    private const int SuperAiPetAttackSkillId = 73;
    private const int SuperAiBattleItemMinType = 23;

    private const float SuperAiPlayerPotionHpRatio = 0.5f;
    private const float SuperAiPetPotionHpRatio = 0.4f;
    private const int SuperAiPriestSkipPotionMp = 200;
    private const string SuperAiPotionNamePrefix = "生命力回复药";
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
            if (SkipBattleAnimDefaultEnabled())
            {
                _skipBattleAnim = true;
            }
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
            // 最小化时再点百科：展开，不要当成关闭（不受开关防抖影响）
            if (_visible && _minimized)
            {
                _lastToggleMs = now;
                EnsureHost();
                EnsurePanel();
                SetMinimized(false);
                WriteLog("wiki restore from minimized");
                return true;
            }

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
            TickSkCNav();
            TickSuperAi();
            TickPetNamer();
            TickScriptWingTest();
            TickFloraHeal();
            TickFullAutoScript();
            TickWildExchange();
            TickZhongyuanLoop();
            TickZhongyuanAll();
            TickZhongyuanCatch();
            TickZhongyuanTransfer();
            // 跳过动画：只清表现队列；选指令交给官方 AutoFight，禁止回合间隙乱踢 DoAutoFight。
            TickSkipBattleAnim();
            // 跳过动画开着时仍跑无法行动兜底（石化/死亡漏 N）；与清队列不冲突。
            TickBattleUnableActFix();
            TickBattleRoundTimeoutLog();
        }
        catch (Exception ex)
        {
            WriteLog("TickEscort/LingTang/SuperAi/WingTest/SkipAnim/UnableAct EX: " + RootMessage(ex));
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

        if (_tab == TabScript && _zyStatusText != null && !IsUnityNull(_zyStatusText))
        {
            SetText(_zyStatusText, FormatWildExchangeStatus(), 12);
        }

        if (_tab == TabScript && _floraHealStatusText != null && !IsUnityNull(_floraHealStatusText))
        {
            SetText(_floraHealStatusText, FormatFloraHealStatus(), 12);
        }

        if (_tab == TabScript && _fullScriptStatusText != null && !IsUnityNull(_fullScriptStatusText))
        {
            SetText(_fullScriptStatusText, FormatFullAutoScriptStatus(), 12);
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
        // 七夕循环：标题显示已完成轮次（存兑换券后才 +1）
        if (_midAutumnLoopActive)
        {
            parts.Add("★七夕" + _midAutumnLoopCount + "轮★");
        }

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
        CaptureWildPetNameFromUi();
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
            CaptureWildPetNameFromUi();
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
            TrySetAutoCatchWild(false, "");
        }
        else if (mode == ModeCatchWild)
        {
            TrySetFeatureEnabled("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes", true);
            TrySetAutoCatchWild(true, "");
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

    /// <summary>供多开器后续实装：打开面板加载 DLL 后直接开/停自动全套脚本。</summary>
    public static bool ToggleFullAutoScriptFromUi()
    {
        ToggleFullAutoScript();
        return _fullScriptActive;
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

    /// <summary>本窗口角色在队伍里的下标：0=1号 … 3=4号。找不到当 1 号。</summary>
    private static int GetLocalTeamSlot()
    {
        try
        {
            var main = Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            if (string.IsNullOrEmpty(main))
            {
                return -1;
            }

            var teamData = GetStaticMember("PlayerDataHolder", "teamData") as Array;
            if (teamData == null)
            {
                return 0;
            }

            for (var i = 0; i < teamData.Length; i++)
            {
                var slot = teamData.GetValue(i);
                if (slot == null || Convert.ToInt32(GetMember(slot, "UseFlag") ?? 0) != 1)
                {
                    continue;
                }

                var player = GetMember(slot, "Player");
                var uid = Convert.ToString(GetMember(player, "Uid") ?? "");
                if (string.Equals(uid, main, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static string FormatTeamSlotLabel(int slot)
    {
        return (slot + 1) + "号";
    }

    /// <summary>队伍下标 0=1号 … 的 UID；找不到返回空。</summary>
    private static string GetTeamUidBySlot(int slot)
    {
        if (slot < 0)
        {
            return "";
        }

        try
        {
            var teamData = GetStaticMember("PlayerDataHolder", "teamData") as Array;
            if (teamData == null || slot >= teamData.Length)
            {
                return "";
            }

            var data = teamData.GetValue(slot);
            if (data == null || Convert.ToInt32(GetMember(data, "UseFlag") ?? 0) != 1)
            {
                return "";
            }

            var uid = Convert.ToString(GetMember(GetMember(data, "Player"), "Uid") ?? "") ?? "";
            return uid.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static int GetZyCatchFill()
    {
        return _zyCatchFill > 0 ? _zyCatchFill : ZhongyuanQuota;
    }

    private static int GetZyXferNeed()
    {
        return _zyXferNeed >= 0 ? _zyXferNeed : ZhongyuanQuota;
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

        // 右上角收缩按钮（默认隐藏）
        _miniFabGo = CreateUiChild(_canvasGo, "MiniFab", rtType);
        SetAnchoredTopRight(RequireRect(_miniFabGo, "fab"), -10f, -10f, 110f, 36f);
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
        _wildPetNameInput = null;
        _lingTangStatusText = null;
        _petNamerStatusText = null;
        _floraHealStatusText = null;
        _zyStatusText = null;
        _fullScriptStatusText = null;
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
            AddModeRow(rtType, ModeCatchWild, "抓野生宠", ref y, true);
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

        AddSkipBattleAnimToggleRow(rtType, ref y);

        WriteLog("BuildBattleBody done");
    }

    /// <summary>战斗页：PVE 清空表现队列（默认关；PVP/观战/录像不处理）。</summary>
    private static void AddSkipBattleAnimToggleRow(Type rtType, ref float y)
    {
        y -= 10f;

        var row = CreateUiChild(_bodyRoot, "SkipBattleAnimRow", rtType);
        SetAnchoredTop(RequireRect(row, "sbar"), 0f, y, 500f, 32f);
        var img = AddComp(row, "UnityEngine.UI.Image");
        SetColor(img, 0.16f, 0.24f, 0.26f, 1f);
        var lab = CreateUiChild(row, "L", rtType);
        StretchFull(RequireRect(lab, "sbal"));
        var text = AddText(lab);
        SetText(text, (_skipBattleAnim ? "● " : "○ ") + "跳过动画（清播放；石化等官方首号节奏再选指令）", 13);
        BindButton(row, img, ToggleSkipBattleAnimFromUi);

        y -= 34f;
    }

    private static void ToggleSkipBattleAnimFromUi()
    {
        _skipBattleAnim = !_skipBattleAnim;
        _skipBattleAnimFlushLogged = false;
        _skipBattleAnimCmdSinceMs = 0;
        _skipBattleAnimManualDone = false;
        if (!_skipBattleAnim)
        {
            ReleaseHeldBattleChars("toggle-off");
        }
        Tip(_skipBattleAnim ? "跳过动画已开启" : "跳过动画已关闭");
        WriteLog("ToggleSkipBattleAnim=" + _skipBattleAnim);
        if (_tab == TabBattle)
        {
            ClearBody();
            BuildBattleBody();
            RefreshTabButtonLabels();
        }
    }

    /// <summary>
    /// 跳过动画：见到播放队列立刻清掉并 Stop。CHAR 先暂存，等 PLAYER 入队；
    /// 首号 MENU_NON（石化等）再等 3 秒才还给 NextRound，避免立刻发 N 把回合打丢。
    /// </summary>
    private static void TickSkipBattleAnim()
    {
        if (!_skipBattleAnim)
        {
            _skipBattleAnimFlushLogged = false;
            _skipBattleAnimAllEndStuckSinceMs = 0;
            ReleaseHeldBattleChars("skip-off");
            return;
        }

        try
        {
            if (!Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                _skipBattleAnimFlushLogged = false;
                _skipBattleAnimCmdSinceMs = 0;
                _skipBattleAnimManualDone = false;
                _skipBattleAnimLastFlushMs = 0;
                _skipBattleAnimAllEndStuckSinceMs = 0;
                _skipBattleAnimHasPrevPlayerUnable = false;
                _skipBattleAnimPrevSelectAtMs = 0;
                _roundTimeoutLoggedTurn = int.MinValue;
                ReleaseHeldBattleChars("out-of-battle");
                return;
            }

            var mode = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battleModeFlag") ?? 0);
            if (mode == BattleTypePvp || mode == BattleTypeWatch || mode == BattleTypePvpWatch || mode == BattleTypeReplay)
            {
                return;
            }

            var bm = GetManagerInstance("BattleManager");
            if (bm == null)
            {
                return;
            }

            DrainBattleStatusQueueIntoHold();
            TryReleaseHeldBattleChars();

            var cmdRunning = Convert.ToBoolean(GetMember(bm, "CmdRunningFlag") ?? false);
            if (!cmdRunning)
            {
                _skipBattleAnimFlushLogged = false;
                _skipBattleAnimCmdSinceMs = 0;
                _skipBattleAnimManualDone = false;
                TryRescueSkipAnimAllEndStuck(bm);
                MaybeLogSkipBattleDiag(bm);
                return;
            }

            _skipBattleAnimAllEndStuckSinceMs = 0;
            ForceBattleGlobalTimeScale(1f);

            var cmdQ = GetBattleCommandQueueCount();
            var cur = GetBattleCurCmdCount();
            var luan = GetBattleLuanCount();
            var hasPresentation = cmdQ > 0 || cur > 0 || luan > 0;
            var now = NowMs();

            if (!_skipBattleAnimFlushLogged && hasPresentation)
            {
                SnapSkipAnimLeadUnable(bm);
                BeginHoldBattleChars();
                ForceSkipAnimRolesReady();
                FlushBattlePresentationQueue();
                _skipBattleAnimLastFlushMs = now;
                _skipBattleAnimCmdSinceMs = now;
                _skipBattleAnimFlushLogged = true;
                WriteLog("SkipAnim: flush after ACTION cmdQ " + cmdQ + "->" + GetBattleCommandQueueCount()
                         + " cur=" + cur + " luan=" + luan
                         + " statusQ=" + GetBattleStatusQueueCount()
                         + " held=" + _skipAnimHeldChars.Count
                         + " pendingMenuNon=" + FirstPendingPlayerMenuNon());
                TryReleaseHeldBattleChars();
            }

            if (_skipBattleAnimFlushLogged
                && !_skipBattleAnimManualDone
                && !_skipAnimHoldActive
                && _skipBattleAnimCmdSinceMs > 0
                && now - _skipBattleAnimCmdSinceMs > SkipAnimForceFinishMs)
            {
                WriteLog("SkipAnim: RunProcess 超时，补一次 OnCompleted");
                ForceFinishPresentationRun(bm);
                _skipBattleAnimManualDone = true;
            }

            MaybeLogSkipBattleDiag(bm);
        }
        catch (Exception ex)
        {
            WriteLog("TickSkipBattleAnim EX: " + RootMessage(ex));
        }
    }

    private static void MaybeLogSkipBattleDiag(object bm)
    {
        var now = NowMs();
        if (now - _skipBattleAnimLastDiagMs < 2000)
        {
            return;
        }

        _skipBattleAnimLastDiagMs = now;
        try
        {
            var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
            var acct = 0;
            var acctList = GetStaticMember("BattleDataHolder", "AcountList") as ICollection;
            if (acctList != null)
            {
                acct = acctList.Count;
            }

            WriteLog("SkipAnim diag: cmdRun=" + GetMember(bm, "CmdRunningFlag")
                     + " gScale=" + GetBattleGlobalTimeScale()
                     + " fight=" + fight + "(AllEnd=" + FightProcessAllEnd + ")"
                     + " acctQ=" + acct
                     + " actQ=" + GetBattleActionQueueCount()
                     + " cmdQ=" + GetBattleCommandQueueCount()
                     + " cur=" + GetBattleCurCmdCount()
                     + " luan=" + GetBattleLuanCount()
                     + " statusQ=" + GetBattleStatusQueueCount()
                     + " hold=" + _skipAnimHoldActive
                     + " held=" + _skipAnimHeldChars.Count
                     + " pendingMenuNon=" + FirstPendingPlayerMenuNon());
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 队长还能动（或宠还能动）→ 100ms；队长和宠（没有宠也算槽位不能动）都不能动 → 等第一个单位开播。
    /// </summary>
    private static void SnapSkipAnimLeadUnable(object bm)
    {
        object playerRole = null;
        object petRole = null;
        try
        {
            var uid = GetCaptainUid();
            var brc = FindType("BattleRoleContainer");
            var roleDic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            var acctDic = brc?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            var idx = -1;
            if (acctDic != null && !string.IsNullOrEmpty(uid))
            {
                if (acctDic.Contains(uid))
                {
                    idx = Convert.ToInt32(acctDic[uid] ?? -1);
                }
                else
                {
                    foreach (DictionaryEntry kv in acctDic)
                    {
                        var key = Convert.ToString(kv.Key ?? "") ?? "";
                        if (string.Equals(key, uid, StringComparison.Ordinal))
                        {
                            idx = Convert.ToInt32(kv.Value ?? -1);
                            break;
                        }
                    }
                }
            }

            if (idx >= 0 && roleDic != null)
            {
                playerRole = roleDic.Contains(idx) ? roleDic[idx] : null;
                if (roleDic.Contains(idx + 5))
                {
                    petRole = roleDic[idx + 5];
                }
                else if (roleDic.Contains(idx - 5))
                {
                    petRole = roleDic[idx - 5];
                }
            }
        }
        catch
        {
            // ignore
        }

        if (playerRole == null)
        {
            playerRole = GetCurrentBattlePlayerRole();
            if (petRole == null)
            {
                petRole = GetCurrentBattlePetRole(bm);
            }
        }

        var playerUnable = IsBattleRoleTrulyUnableToAct(playerRole);
        var hasPet = petRole != null;
        var petUnable = !hasPet || IsBattleRoleTrulyUnableToAct(petRole);
        var bc = GetBattleRoleStatus(playerRole);
        var bp = GetBattleBpFlag();
        var menuNon = HasBpFlag(bp, BpFlagPlayerMenuNon);
        var petMenuNon = HasBpFlag(bp, BpFlagPetMenuNon);
        var turn = 0;
        try
        {
            turn = Convert.ToInt32(GetMember(TryGetBattleProcesser(), "m_BattleSvTurnIndex") ?? 0);
        }
        catch
        {
            // ignore
        }

        var edge = "";
        var dtPrev = 0L;
        if (_skipBattleAnimHasPrevPlayerUnable
            && _skipBattleAnimPrevPlayerUnable != playerUnable
            && _skipBattleAnimPrevSelectAtMs > 0)
        {
            dtPrev = NowMs() - _skipBattleAnimPrevSelectAtMs;
            edge = playerUnable ? "stone-on" : "stone-off";
        }

        WriteLog("SkipAnim: lead snap playerUnable=" + playerUnable
                 + " hasPet=" + hasPet
                 + " petUnable=" + petUnable
                 + " turn=" + turn
                 + " bc=0x" + bc.ToString("X") + "(" + FormatBcStatus(bc) + ")"
                 + " bp=0x" + bp.ToString("X")
                 + " playerMenuNon=" + menuNon
                 + " petMenuNon=" + petMenuNon
                 + (edge.Length > 0 ? " edge=" + edge + " dtPrev=" + dtPrev + "ms" : "")
                 + (playerUnable != menuNon ? " MISMATCH-stone-vs-MENU_NON" : ""));
        _skipBattleAnimPrevPlayerUnable = playerUnable;
        _skipBattleAnimHasPrevPlayerUnable = true;
        _skipBattleAnimPrevSelectAtMs = NowMs();
    }

    /// <summary>先清队列再 Stop，让 RunProcess 自然 OnCompleted。</summary>
    private static void FlushBattlePresentationQueue()
    {
        var runner = GetBattleCommandRunner();
        if (runner == null)
        {
            return;
        }

        ClearReflectCollection(GetMember(runner, "m_CmdQueue"));
        ClearReflectCollection(GetMember(runner, "m_LuanList"));
        ClearReflectDictionary(GetMember(runner, "m_CurCmds"));
        runner.GetType().GetMethod("Stop",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?.Invoke(runner, null);
    }

    /// <summary>把已到的 CHAR 从 BattleStatusQueue 挪到暂存，阻止 NextRound 立刻 RefreshData。</summary>
    private static void BeginHoldBattleChars()
    {
        _skipAnimHoldActive = true;
        _skipAnimHoldSinceMs = NowMs();
        DrainBattleStatusQueueIntoHold();
        WriteLog("SkipAnim: hold CHAR start n=" + _skipAnimHeldChars.Count
                 + " acctQ=" + GetBattleAccountQueueCount()
                 + " pendingMenuNon=" + FirstPendingPlayerMenuNon());
    }

    private static void DrainBattleStatusQueueIntoHold()
    {
        if (!_skipAnimHoldActive)
        {
            return;
        }

        var q = GetBattleStatusQueue();
        if (q == null)
        {
            return;
        }

        var n = 0;
        try
        {
            var dequeue = q.GetType().GetMethod("Dequeue", Type.EmptyTypes);
            if (dequeue == null)
            {
                return;
            }

            while ((q as ICollection)?.Count > 0)
            {
                var item = dequeue.Invoke(q, null);
                if (item != null)
                {
                    _skipAnimHeldChars.Add(item);
                    n++;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("SkipAnim: drain CHAR EX: " + RootMessage(ex));
        }

        if (n > 0)
        {
            WriteLog("SkipAnim: drain CHAR +" + n + " held=" + _skipAnimHeldChars.Count);
        }
    }

    private static void TryReleaseHeldBattleChars()
    {
        if (!_skipAnimHoldActive)
        {
            return;
        }

        var now = NowMs();
        var elapsed = _skipAnimHoldSinceMs > 0 ? now - _skipAnimHoldSinceMs : 0;
        var acctQ = GetBattleAccountQueueCount();
        var menuNon = FirstPendingPlayerMenuNon();
        var ready = acctQ > 0 && (!menuNon || elapsed >= SkipAnimMenuNonHoldMs);
        if (!ready && elapsed < SkipAnimHoldMaxMs)
        {
            return;
        }

        var reason = ready
            ? (menuNon ? "menu-non-waited" : "player-ready")
            : "hold-max";
        ReleaseHeldBattleChars(reason + " elapsed=" + elapsed + "ms acctQ=" + acctQ + " menuNon=" + menuNon);
    }

    private static void ReleaseHeldBattleChars(string reason)
    {
        if (!_skipAnimHoldActive && _skipAnimHeldChars.Count == 0)
        {
            return;
        }

        DrainBattleStatusQueueIntoHold();
        var n = _skipAnimHeldChars.Count;
        var drop = reason != null && reason.IndexOf("out-of-battle", StringComparison.Ordinal) >= 0;
        try
        {
            if (!drop)
            {
                var q = GetBattleStatusQueue();
                var enqueue = q?.GetType().GetMethod("Enqueue");
                if (enqueue != null)
                {
                    for (var i = 0; i < _skipAnimHeldChars.Count; i++)
                    {
                        enqueue.Invoke(q, new[] { _skipAnimHeldChars[i] });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("SkipAnim: release CHAR EX: " + RootMessage(ex));
        }

        _skipAnimHeldChars.Clear();
        _skipAnimHoldActive = false;
        _skipAnimHoldSinceMs = 0;
        if (n > 0 || !string.IsNullOrEmpty(reason))
        {
            WriteLog("SkipAnim: release CHAR n=" + n
                     + " statusQ=" + GetBattleStatusQueueCount()
                     + " drop=" + drop
                     + " reason=" + reason);
        }
    }

    /// <summary>下一拍将选的首号是否 PLAYER_MENU_NON（来自已入队的 PLAYER，不看当前 BPFlag）。</summary>
    private static bool FirstPendingPlayerMenuNon()
    {
        try
        {
            var list = GetStaticMember("BattleDataHolder", "AcountList") as IList;
            if (list == null || list.Count == 0)
            {
                return false;
            }

            var uid = Convert.ToString(list[0] ?? "") ?? "";
            if (uid.Length == 0)
            {
                return false;
            }

            var brc = FindType("BattleRoleContainer");
            var acctDic = brc?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (acctDic == null || !acctDic.Contains(uid))
            {
                return false;
            }

            var idx = Convert.ToInt32(acctDic[uid] ?? -1);
            var arrObj = GetStaticMember("BattleDataHolder", "BPFlagArray");
            if (!(arrObj is Array arr) || idx < 0 || idx >= arr.Length)
            {
                return false;
            }

            return HasBpFlag(Convert.ToInt32(arr.GetValue(idx) ?? 0), BpFlagPlayerMenuNon);
        }
        catch
        {
            return false;
        }
    }

    private static object GetBattleStatusQueue()
    {
        try
        {
            return GetMember(TryGetBattleProcesser(), "BattleStatusQueue");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// AllEnd + 账号/ACTION/CHAR 全空：客户端在等服务端 ACTION，通常指令已对不齐。
    /// 只 Tip，不在这里强退。
    /// </summary>
    private static void TryRescueSkipAnimAllEndStuck(object bm)
    {
        try
        {
            var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
            var acctQ = GetBattleAccountQueueCount();
            var actQ = GetBattleActionQueueCount();
            var statusQ = GetBattleStatusQueueCount();
            var cmdQ = GetBattleCommandQueueCount();
            var now = NowMs();

            if (fight != FightProcessAllEnd || acctQ != 0 || actQ != 0 || statusQ != 0 || cmdQ != 0)
            {
                _skipBattleAnimAllEndStuckSinceMs = 0;
                return;
            }

            if (_skipBattleAnimLastFlushMs > 0 && now - _skipBattleAnimLastFlushMs < SkipAnimFlushCooldownMs)
            {
                return;
            }

            if (_skipBattleAnimAllEndStuckSinceMs <= 0)
            {
                _skipBattleAnimAllEndStuckSinceMs = now;
                return;
            }

            var elapsed = now - _skipBattleAnimAllEndStuckSinceMs;
            if (elapsed >= SkipAnimAllEndStuckTipMs && now - _skipBattleAnimAllEndTipMs > 15000)
            {
                _skipBattleAnimAllEndTipMs = now;
                Tip("战斗卡住，等待官方超时脱离…");
                WriteLog("SkipAnim: AllEnd-empty stuck tip elapsed=" + elapsed
                         + "ms fight=3 acctQ=0 actQ=0 statusQ=0");
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryRescueSkipAnimAllEndStuck EX: " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 官方单回合监视器 m_SingleRoundMonitorTime ≥ 180 会断线。到 170s 写独立日志，方便和 AllEnd-empty 对照。
    /// </summary>
    private static void TickBattleRoundTimeoutLog()
    {
        try
        {
            if (!Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                _roundTimeoutLoggedTurn = int.MinValue;
                return;
            }

            var proc = TryGetBattleProcesser();
            if (proc == null)
            {
                return;
            }

            var monitorSec = Convert.ToInt32(GetMember(proc, "m_SingleRoundMonitorTime") ?? 0);
            var turn = Convert.ToInt32(GetMember(proc, "m_BattleSvTurnIndex") ?? 0);
            if (monitorSec < RoundTimeoutLogAtSec)
            {
                if (monitorSec <= 1 && _roundTimeoutLoggedTurn == turn)
                {
                    _roundTimeoutLoggedTurn = int.MinValue;
                }

                return;
            }

            if (_roundTimeoutLoggedTurn == turn)
            {
                return;
            }

            _roundTimeoutLoggedTurn = turn;
            var bm = GetManagerInstance("BattleManager");
            var fight = bm != null ? Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0) : -1;
            var bc = GetBattleRoleStatus(GetCurrentBattlePlayerRole());
            var bp = GetBattleBpFlag();
            var stuckMs = _skipBattleAnimAllEndStuckSinceMs > 0
                ? (NowMs() - _skipBattleAnimAllEndStuckSinceMs)
                : 0;
            var line = "round-timeout monitor=" + monitorSec + "s/" + OfficialSingleRoundTimeoutSec
                       + " turn=" + turn
                       + " uid=" + (GetCaptainUid() ?? "")
                       + " pid=" + Process.GetCurrentProcess().Id
                       + " fight=" + fight
                       + " acctQ=" + GetBattleAccountQueueCount()
                       + " actQ=" + GetBattleActionQueueCount()
                       + " cmdQ=" + GetBattleCommandQueueCount()
                       + " statusQ=" + GetBattleStatusQueueCount()
                       + " cmdRun=" + (bm != null ? GetMember(bm, "CmdRunningFlag") : "")
                       + " skipAnim=" + _skipBattleAnim
                       + " playerUnable=" + IsBattleRoleTrulyUnableToAct(GetCurrentBattlePlayerRole())
                       + " bc=0x" + bc.ToString("X") + "(" + FormatBcStatus(bc) + ")"
                       + " bp=0x" + bp.ToString("X")
                       + " playerMenuNon=" + HasBpFlag(bp, BpFlagPlayerMenuNon)
                       + " petMenuNon=" + HasBpFlag(bp, BpFlagPetMenuNon)
                       + " allEndEmptyMs=" + stuckMs
                       + " mission=" + _escortMissionId;
            WriteLog("SkipAnim: " + line);
            WriteRoundTimeoutLog(line);
        }
        catch (Exception ex)
        {
            WriteLog("TickBattleRoundTimeoutLog EX: " + RootMessage(ex));
        }
    }

    private static void WriteRoundTimeoutLog(string message)
    {
        try
        {
            EnsureLogPath();
            if (string.IsNullOrEmpty(_roundTimeoutLogPath))
            {
                var dir = Path.GetDirectoryName(_logPath ?? "") ?? "";
                _roundTimeoutLogPath = string.IsNullOrEmpty(dir)
                    ? RoundTimeoutLogFileName
                    : Path.Combine(dir, RoundTimeoutLogFileName);
            }

            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                       + " [pid=" + Process.GetCurrentProcess().Id + "] "
                       + (message ?? "")
                       + Environment.NewLine;
            lock (RoundTimeoutLogLock)
            {
                using (var fs = new FileStream(
                           _roundTimeoutLogPath,
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

    /// <summary>跳过表现后清 NeedWaitDeadAction。</summary>
    private static void ForceSkipAnimRolesReady()
    {
        try
        {
            var brc = FindType("BattleRoleContainer");
            brc?.GetMethod("AllRoleReturn", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                ?.Invoke(null, null);
            var dic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                return;
            }

            foreach (DictionaryEntry kv in dic)
            {
                var role = kv.Value;
                if (role == null)
                {
                    continue;
                }

                SetMember(role, "NeedWaitDeadAction", false);
                SetMember(role, "IsInPosition", true);
                SetMember(role, "returnCompleted", true);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>仅超时兜底：补官方 OnCompleted。</summary>
    private static void ForceFinishPresentationRun(object bm)
    {
        ForceBattleGlobalTimeScale(1f);
        if (!Convert.ToBoolean(GetMember(bm, "CmdRunningFlag") ?? false))
        {
            return;
        }

        var proc = TryGetBattleProcesser();
        proc?.GetType().GetMethod("CommandRunner_OnCompleted",
                BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?.Invoke(proc, null);
    }

    /// <summary>
    /// 五开自动战斗：死亡/石化/睡眠时官方常漏发「N」或卡在选目标；跳过动画时更明显。
    /// 无法行动立刻补 idle；选指令卡住 / AllEnd 空队列 / fight 与 acctQ 错位再强制收尾。
    /// </summary>
    private static void TickBattleUnableActFix()
    {
        try
        {
            if (!Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                _battleUnableActFixKey = "";
                _battleUnableActPlayerIdleKey = "";
                _battleUnableActPetIdleKey = "";
                _battleUnableActStuckRescueKey = "";
                _battleUnableActStuckSinceMs = 0;
                _battleUnableActStuckSig = "";
                return;
            }

            var mode = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battleModeFlag") ?? 0);
            if (mode == BattleTypePvp || mode == BattleTypeWatch || mode == BattleTypePvpWatch || mode == BattleTypeReplay)
            {
                return;
            }

            var bm = GetManagerInstance("BattleManager");
            if (bm == null)
            {
                return;
            }

            // 跳过动画：选指令全交给官方 AutoFight。预填/注入 MENU_NON 或补发 N
            // 会和 RefreshOtherBattleUI / DoAutoFight 打成双包，服务端不再下发 ACTION。
            if (_skipBattleAnim)
            {
                return;
            }

            // 表现中也预填 BPFlagArray（仅死亡/睡眠/石化，不读 MENU_NON 防自循环）
            PrefillUnableActMenuNonForAllies();

            if (Convert.ToBoolean(GetMember(bm, "CmdRunningFlag") ?? false))
            {
                _battleUnableActStuckSinceMs = 0;
                _battleUnableActStuckSig = "";
                return;
            }

            if (GetBattleStatusQueueCount() > 0 || GetBattleActionQueueCount() > 0)
            {
                return;
            }

            var proc = TryGetBattleProcesser();
            var turn = Convert.ToInt32(GetMember(proc, "m_BattleSvTurnIndex") ?? 0);
            var acct = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount") ?? "") ?? "";
            var fixKey = acct + "|" + turn;
            if (!string.Equals(fixKey, _battleUnableActFixKey, StringComparison.Ordinal))
            {
                _battleUnableActFixKey = fixKey;
                _battleUnableActInjected = false;
            }

            var auto = Convert.ToBoolean(GetMember(bm, "IsAutoBattle") ?? false);
            var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
            var injected = EnsureBattleUnableActMenuNon(bm);
            if (injected && !_battleUnableActInjected)
            {
                _battleUnableActInjected = true;
                var playerRole = GetCurrentBattlePlayerRole();
                var bc = GetBattleRoleStatus(playerRole);
                WriteLog("UnableAct: inject MENU_NON uid=" + acct + " turn=" + turn
                         + " bc=0x" + bc.ToString("X") + "(" + FormatBcStatus(bc) + ")"
                         + " fight=" + fight + " auto=" + auto);
            }

            // 无法行动：立刻发 idle（不等待官方 DoAutoFight / 不要求已标 PlayerActionEnd）
            EnsureUnableActIdleCommandsSent(bm, proc, acct, turn);

            fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
            // 跳过动画开着时不踢 DoAutoFight（与官方 3s 倒计时抢跑）；idle 已由上面补发
            if (auto && fight != FightProcessAllEnd && injected && !_skipBattleAnim)
            {
                proc?.GetType().GetMethod("DoAutoFight",
                        BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                    ?.Invoke(proc, null);
            }

            // 仅无法行动 / 幽灵态 / 真空队列才救；正常选指令绝不再「强制跳过」
            TryRescueBattleCommandStuck(bm, proc, acct, turn, auto);
        }
        catch (Exception ex)
        {
            WriteLog("TickBattleUnableActFix EX: " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 人物/宠无法行动时补 idle。SendBattleCommond 的 Count 看 PlayerActionEnd：
    /// 未结束=人物指令(1)，已结束=宠物指令(2)。人物 N 绝不能在 fight 已带 PlayerActionEnd 时再发，
    /// 否则会当成宠物包，和官方 RefreshOtherBattleUI / DoAutoFight 撞车，服务器不再下发 ACTION。
    /// </summary>
    private static void EnsureUnableActIdleCommandsSent(object bm, object proc, string acct, int turn)
    {
        if (proc == null || string.IsNullOrEmpty(acct))
        {
            return;
        }

        var playerRole = GetCurrentBattlePlayerRole();
        var playerUnable = IsBattleRoleTrulyUnableToAct(playerRole);
        var petUnable = IsCurrentBattlePetTrulyUnableToAct(bm);
        if (!playerUnable && !petUnable)
        {
            return;
        }

        EnsureBattleUnableActMenuNon(bm);
        var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
        var playerKey = acct + "|" + turn + "|P";
        var petKey = acct + "|" + turn + "|E";
        var playerEnd = (fight & FightProcessPlayerEnd) != 0;
        var petEnd = (fight & FightProcessPetEnd) != 0;

        // 跳过动画：官方 NextRound 对 2 号起立刻 DoAutoFight。我们再发 N 会与官方各一包，
        // 两条 Count=1 的人物待机让服务端再也等不齐 ACTION（AllEnd 空队列）。
        // 只预写 MENU_NON，N / 宠 idle 交给官方 AutoFight。
        if (_skipBattleAnim)
        {
            if (playerUnable
                && !string.Equals(playerKey, _battleUnableActPlayerIdleKey, StringComparison.Ordinal))
            {
                _battleUnableActPlayerIdleKey = playerKey;
                WriteLog("UnableAct: skip extra N (skip-anim, official DoAutoFight) uid=" + acct
                         + " turn=" + turn + " fight=" + fight
                         + " playerEnd=" + playerEnd + " petUnable=" + petUnable);
            }

            return;
        }

        // 官方 MENU_NON 路径已经发过人物 N（fight 已有 PlayerActionEnd）→ 不再发，否则 Count=2 变成宠物包
        if (playerUnable
            && !playerEnd
            && !string.Equals(playerKey, _battleUnableActPlayerIdleKey, StringComparison.Ordinal))
        {
            InvokeBattleProcesserMethod(proc, "EndSelect");
            InvokeBattleProcesserMethod(proc, "SendPlayerIdleCommand");
            _battleUnableActPlayerIdleKey = playerKey;
            SetBattleFightProcessFlag(bm, fight | FightProcessPlayerEnd);
            fight |= FightProcessPlayerEnd;
            playerEnd = true;
            WriteLog("UnableAct: force player idle N uid=" + acct + " turn=" + turn
                     + " fight=" + fight + " dead="
                     + Convert.ToBoolean(GetMember(playerRole, "IsDead") ?? false)
                     + " bc=0x" + GetBattleRoleStatus(playerRole).ToString("X"));
        }
        else if (playerUnable && playerEnd
                 && !string.Equals(playerKey, _battleUnableActPlayerIdleKey, StringComparison.Ordinal))
        {
            _battleUnableActPlayerIdleKey = playerKey;
            WriteLog("UnableAct: skip player N (already PlayerActionEnd) uid=" + acct
                     + " turn=" + turn + " fight=" + fight);
        }

        if (petUnable
            && playerEnd
            && !petEnd
            && !string.Equals(petKey, _battleUnableActPetIdleKey, StringComparison.Ordinal))
        {
            InvokeBattleProcesserMethod(proc, "EndSelect");
            ForceSendPetIdle(bm, proc);
            _battleUnableActPetIdleKey = petKey;
            SetBattleFightProcessFlag(bm, fight | FightProcessPetEnd);
            WriteLog("UnableAct: force pet idle uid=" + acct + " turn=" + turn);
        }
    }

    private static void ForceSendPetIdle(object bm, object proc)
    {
        var bp = GetBattleBpFlag();
        if (HasBpFlag(bp, BpFlagPet))
        {
            InvokeBattleProcesserMethod(proc, "CheckAndSendDefaultPetCommand");
            return;
        }

        try
        {
            bm.GetType().GetMethod("SendBattleCommond",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string) }, null)
                ?.Invoke(bm, new object[] { "W|FF|FF" });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 卡死兜底（收紧，避免正常选指令误 Tip「强制跳过」）：
    /// 1) 当前账号人物/宠真正无法行动且久不 AllEnd → 强制 idle+AllEnd；
    /// 2) fight≠AllEnd 且 acctQ=0 幽灵态过久 → 静默标 AllEnd（不 Tip）；
    /// 3) 无法行动且 AllEnd+空队列过久 → 再补发 idle（不 Tip）。
    /// </summary>
    private static void TryRescueBattleCommandStuck(object bm, object proc, string acct, int turn, bool auto)
    {
        var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
        var acctQ = GetBattleAccountQueueCount();
        var actQ = GetBattleActionQueueCount();
        var statusQ = GetBattleStatusQueueCount();
        var now = NowMs();

        var playerUnable = IsBattleRoleTrulyUnableToAct(GetCurrentBattlePlayerRole());
        var petUnable = IsCurrentBattlePetTrulyUnableToAct(bm);
        var unableNow = playerUnable || petUnable;

        // 正常选指令：fight 会长时间 ≠ AllEnd，绝不能当卡死。
        // 最后一名账号被弹出后 acctQ 本来就是 0，不能当幽灵态标 AllEnd。
        var selectStuck = unableNow && fight != FightProcessAllEnd;
        var ghostStuck = !_skipBattleAnim && unableNow && fight != FightProcessAllEnd && acctQ == 0;
        var emptyQueueStuck = unableNow && fight == FightProcessAllEnd
                              && acctQ == 0 && actQ == 0 && statusQ == 0;
        if (!selectStuck && !ghostStuck && !emptyQueueStuck)
        {
            _battleUnableActStuckSinceMs = 0;
            _battleUnableActStuckSig = "";
            return;
        }

        var sig = turn + "|" + acct + "|" + fight + "|" + acctQ
                  + "|" + (selectStuck ? "S" : "") + (ghostStuck ? "G" : "") + (emptyQueueStuck ? "E" : "");
        if (!string.Equals(sig, _battleUnableActStuckSig, StringComparison.Ordinal))
        {
            _battleUnableActStuckSig = sig;
            _battleUnableActStuckSinceMs = now;
            return;
        }

        if (_battleUnableActStuckSinceMs <= 0)
        {
            _battleUnableActStuckSinceMs = now;
            return;
        }

        var waitMs = selectStuck
            ? BattleUnableActSelectStuckMs
            : (ghostStuck ? BattleGhostStuckMs : BattleEmptyQueueStuckMs);
        if (now - _battleUnableActStuckSinceMs < waitMs)
        {
            return;
        }

        if (selectStuck)
        {
            if (_skipBattleAnim)
            {
                _battleUnableActStuckSinceMs = now;
                return;
            }

            var selKey = acct + "|" + turn + "|sel";
            if (!string.Equals(selKey, _battleUnableActStuckRescueKey, StringComparison.Ordinal))
            {
                _battleUnableActStuckRescueKey = selKey;
                ForceCompleteCurrentBattleAccount(bm, proc, acct, turn, "unable-select-stuck");
                if (auto && !_skipBattleAnim)
                {
                    try
                    {
                        SetMember(bm, "IsAutoBattle", true);
                    }
                    catch
                    {
                        // ignore
                    }

                    proc?.GetType().GetMethod("DoAutoFight",
                            BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                        ?.Invoke(proc, null);
                }
            }

            _battleUnableActStuckSinceMs = now;
            return;
        }

        if (ghostStuck)
        {
            var ghostKey = turn + "|ghost";
            if (!string.Equals(ghostKey, _battleUnableActStuckRescueKey, StringComparison.Ordinal))
            {
                _battleUnableActStuckRescueKey = ghostKey;
                SetBattleFightProcessFlag(bm, FightProcessAllEnd);
                WriteLog("UnableAct: rescue ghost AllEnd turn=" + turn + " uid=" + acct
                         + " fightWas=" + fight + " auto=" + auto + " unable=" + unableNow);
            }

            _battleUnableActStuckSinceMs = now;
            return;
        }

        // emptyQueueStuck（仅 unableNow）
        var rescueKey = turn + "|empty";
        if (string.Equals(rescueKey, _battleUnableActStuckRescueKey, StringComparison.Ordinal))
        {
            return;
        }

        _battleUnableActStuckRescueKey = rescueKey;
        EnsureBattleUnableActMenuNon(bm);
        WriteLog("UnableAct: empty-queue wait (no extra N, already AllEnd) turn=" + turn
                 + " uid=" + acct + " auto=" + auto);
        _battleUnableActStuckSinceMs = now;
    }

    /// <summary>对本账号强制发人物+宠物 idle 并标 AllEnd（仿 RoundTimeUp）。仅无法行动路径调用。</summary>
    private static void ForceCompleteCurrentBattleAccount(object bm, object proc, string acct, int turn, string reason)
    {
        InvokeBattleProcesserMethod(proc, "EndSelect");
        EnsureBattleUnableActMenuNon(bm);
        var fight = Convert.ToInt32(GetMember(bm, "FightProcessFlag") ?? 0);
        if ((fight & FightProcessPlayerEnd) == 0)
        {
            InvokeBattleProcesserMethod(proc, "SendPlayerIdleCommand");
            _battleUnableActPlayerIdleKey = acct + "|" + turn + "|P";
        }

        if ((fight & FightProcessPetEnd) == 0)
        {
            ForceSendPetIdle(bm, proc);
            _battleUnableActPetIdleKey = acct + "|" + turn + "|E";
        }

        SetBattleFightProcessFlag(bm, FightProcessAllEnd);
        WriteLog("UnableAct: force-complete account uid=" + acct + " turn=" + turn
                 + " reason=" + reason + " fightWas=" + fight);
        Tip("无法行动已自动跳过指令");
    }

    private static bool AnyAllyUnableToAct()
    {
        try
        {
            var brc = FindType("BattleRoleContainer");
            var roleDic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            var acctDic = brc?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (roleDic == null || acctDic == null)
            {
                return false;
            }

            var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
            foreach (DictionaryEntry kv in acctDic)
            {
                var idx = Convert.ToInt32(kv.Value ?? -1);
                if (idx < 0)
                {
                    continue;
                }

                var mine = playerIdx < 0
                    || (playerIdx < 10 && idx < 10)
                    || (playerIdx >= 10 && idx >= 10);
                if (!mine)
                {
                    continue;
                }

                var role = roleDic.Contains(idx) ? roleDic[idx] : null;
                if (role != null && IsBattleRoleTrulyUnableToAct(role))
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

    private static void InvokeBattleProcesserMethod(object proc, string name)
    {
        try
        {
            proc?.GetType().GetMethod(name,
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?.Invoke(proc, null);
        }
        catch
        {
            // ignore
        }
    }

    private static void SetBattleFightProcessFlag(object bm, int value)
    {
        try
        {
            var p = bm.GetType().GetProperty("FightProcessFlag",
                BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite)
            {
                object boxed = p.PropertyType.IsEnum
                    ? Enum.ToObject(p.PropertyType, value)
                    : value;
                p.SetValue(bm, boxed, null);
                return;
            }

            SetMember(bm, "FightProcessFlag", value);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>给全队无法行动的我方角色预写 BPFlagArray，抢在 ChangeNextBattleRole / DoAutoFight 之前。</summary>
    private static void PrefillUnableActMenuNonForAllies()
    {
        try
        {
            var arrObj = GetStaticMember("BattleDataHolder", "BPFlagArray");
            if (!(arrObj is Array arr) || arr.Length == 0)
            {
                return;
            }

            var brc = FindType("BattleRoleContainer");
            var roleDic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            var acctDic = brc?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (roleDic == null || acctDic == null)
            {
                return;
            }

            var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
            foreach (DictionaryEntry kv in acctDic)
            {
                var idx = Convert.ToInt32(kv.Value ?? -1);
                if (idx < 0 || idx >= arr.Length)
                {
                    continue;
                }

                var role = roleDic.Contains(idx) ? roleDic[idx] : null;
                if (role == null)
                {
                    continue;
                }

                // 只处理我方（与当前操作角色同侧）
                var mine = playerIdx < 0
                    || (playerIdx < 10 && idx < 10)
                    || (playerIdx >= 10 && idx >= 10);
                if (!mine)
                {
                    continue;
                }

                var unable = Convert.ToBoolean(GetMember(role, "IsDead") ?? false)
                             || (GetBattleRoleStatus(role) & BcUnableActMask) != 0;
                if (!unable)
                {
                    continue;
                }

                var cur = Convert.ToInt32(arr.GetValue(idx) ?? 0);
                var next = cur | BpFlagPlayerMenuNon;
                object petRole = null;
                if (roleDic.Contains(idx + 5))
                {
                    petRole = roleDic[idx + 5];
                }
                else if (roleDic.Contains(idx - 5))
                {
                    petRole = roleDic[idx - 5];
                }
                if (petRole != null
                    && (Convert.ToBoolean(GetMember(petRole, "IsDead") ?? false)
                        || (GetBattleRoleStatus(petRole) & BcUnableActMask) != 0))
                {
                    next |= BpFlagPetMenuNon;
                }

                if (next != cur)
                {
                    var elemType = arr.GetType().GetElementType();
                    object boxed = elemType != null && elemType.IsEnum
                        ? Enum.ToObject(elemType, next)
                        : next;
                    arr.SetValue(boxed, idx);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>无法行动时注入 MENU_NON，返回是否对本账号注入了人物/宠物菜单禁止。</summary>
    private static bool EnsureBattleUnableActMenuNon(object bm)
    {
        var playerRole = GetCurrentBattlePlayerRole();
        var playerUnable = IsBattleRoleTrulyUnableToAct(playerRole);
        var petUnable = IsCurrentBattlePetTrulyUnableToAct(bm);
        if (!playerUnable && !petUnable)
        {
            return false;
        }

        var bp = GetBattleBpFlag();
        var next = bp;
        if (playerUnable)
        {
            next |= BpFlagPlayerMenuNon;
        }

        if (petUnable)
        {
            next |= BpFlagPetMenuNon;
        }

        if (next != bp)
        {
            SetBattleBpFlag(next);
        }

        return true;
    }

    private static int GetBattleBpFlag()
    {
        return Convert.ToInt32(GetStaticMember("BattleDataHolder", "BPFlag") ?? 0);
    }

    private static void SetBattleBpFlag(int bp)
    {
        try
        {
            var t = FindType("BattleDataHolder");
            var p = t?.GetProperty("BPFlag", BindingFlags.Public | BindingFlags.Static);
            if (p != null && p.CanWrite)
            {
                var enumType = p.PropertyType;
                object boxed = Enum.ToObject(enumType, bp);
                p.SetValue(null, boxed, null);
                return;
            }

            var f = t?.GetField("BPFlag", BindingFlags.Public | BindingFlags.Static);
            if (f != null)
            {
                object boxed = f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, bp) : bp;
                f.SetValue(null, boxed);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool HasBpFlag(int bp, int flag)
    {
        return (bp & flag) != 0;
    }

    /// <summary>优先 Char.Bcflag（与超级AI一致），其次 RoleData.status。</summary>
    private static long GetBattleRoleStatus(object role)
    {
        try
        {
            var roleData = GetMember(role, "RoleData");
            if (roleData == null)
            {
                return 0;
            }

            var ch = GetMember(roleData, "Char");
            if (ch != null)
            {
                var bc = Convert.ToInt64(GetMember(ch, "Bcflag") ?? 0L);
                if (bc != 0)
                {
                    return bc;
                }
            }

            return Convert.ToInt64(GetMember(roleData, "status") ?? 0L);
        }
        catch
        {
            return 0;
        }
    }

    private static object GetCurrentBattlePlayerRole()
    {
        var idx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
        if (idx < 0)
        {
            var bm = GetManagerInstance("BattleManager");
            idx = Convert.ToInt32(GetMember(bm, "PlayerIndex") ?? -1);
        }

        if (idx < 0)
        {
            return null;
        }

        var brc = FindType("BattleRoleContainer");
        var dic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as IDictionary;
        return dic?[idx];
    }

    private static object GetCurrentBattlePetRole(object bm)
    {
        if (bm == null)
        {
            return null;
        }

        try
        {
            var petIdx = Convert.ToInt32(bm.GetType().GetMethod("GetBattlePetIndex", Type.EmptyTypes)
                ?.Invoke(bm, null) ?? -1);
            if (petIdx < 0)
            {
                return null;
            }

            var brc = FindType("BattleRoleContainer");
            var dic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            return dic?[petIdx];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>死亡 / 睡眠 / 石化（不读 MENU_NON，避免注入后自循环把正常人当无法行动）。</summary>
    private static bool IsBattleRoleTrulyUnableToAct(object role)
    {
        if (role == null)
        {
            return false;
        }

        if (Convert.ToBoolean(GetMember(role, "IsDead") ?? false))
        {
            return true;
        }

        return (GetBattleRoleStatus(role) & BcUnableActMask) != 0;
    }

    /// <summary>真正无法行动，或本拍已写过 MENU_NON（仅用于写 BP / 发 idle 去重，不作卡死判定入口）。</summary>
    private static bool IsBattleRoleUnableToAct(object role, bool isPlayer)
    {
        if (IsBattleRoleTrulyUnableToAct(role))
        {
            return true;
        }

        if (role == null)
        {
            return false;
        }

        var bp = GetBattleBpFlag();
        if (isPlayer && HasBpFlag(bp, BpFlagPlayerMenuNon))
        {
            return true;
        }

        return !isPlayer && HasBpFlag(bp, BpFlagPetMenuNon);
    }

    private static bool IsCurrentBattlePetTrulyUnableToAct(object bm)
    {
        var petRole = GetCurrentBattlePetRole(bm);
        return IsBattleRoleTrulyUnableToAct(petRole);
    }

    private static bool IsCurrentBattlePetUnableToAct(object bm)
    {
        var bp = GetBattleBpFlag();
        if (HasBpFlag(bp, BpFlagPetMenuNon))
        {
            return true;
        }

        var petRole = GetCurrentBattlePetRole(bm);
        if (petRole == null)
        {
            return false;
        }

        return IsBattleRoleUnableToAct(petRole, false);
    }

    private static object GetBattleCommandRunner()
    {
        return GetMember(TryGetBattleProcesser(), "m_CommandRunner");
    }

    private static void ForceBattleRolesReady()
    {
        try
        {
            var brc = FindType("BattleRoleContainer");
            brc?.GetMethod("AllRoleReturn", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                ?.Invoke(null, null);

            var dic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (dic != null)
            {
                foreach (DictionaryEntry kv in dic)
                {
                    var role = kv.Value;
                    if (role == null)
                    {
                        continue;
                    }

                    SetMember(role, "IsInPosition", true);
                    SetMember(role, "returnCompleted", true);
                    SetMember(role, "NeedWaitDeadAction", false);
                    role.GetType().GetMethod("SetTimeScale",
                            BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null)
                        ?.Invoke(role, new object[] { 1f });
                }
            }

            var mapType = FindType("MapManager");
            var mono = FindType("MonoSingleton`1");
            if (mapType != null && mono != null)
            {
                var closed = mono.MakeGenericType(mapType);
                var map = closed.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null, null)
                          ?? closed.GetField("instance", BindingFlags.Public | BindingFlags.Static)
                              ?.GetValue(null);
                SetMember(map, "backShining", false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool AnyBattleRoleNotReady()
    {
        return CountBattleRolesNotReady() > 0;
    }

    private static int CountBattleRolesNotReady()
    {
        var bad = 0;
        try
        {
            var brc = FindType("BattleRoleContainer");
            var dic = brc?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                return 0;
            }

            foreach (DictionaryEntry kv in dic)
            {
                var role = kv.Value;
                if (role == null)
                {
                    continue;
                }

                var dead = Convert.ToBoolean(GetMember(role, "IsDead") ?? false);
                if (dead)
                {
                    continue;
                }

                if (!Convert.ToBoolean(GetMember(role, "IsInPosition") ?? true))
                {
                    bad++;
                    continue;
                }

                if (!Convert.ToBoolean(GetMember(role, "returnCompleted") ?? true))
                {
                    bad++;
                }
            }
        }
        catch
        {
            // ignore
        }

        return bad;
    }

    private static int GetBattleActionQueueCount()
    {
        try
        {
            var proc = TryGetBattleProcesser();
            var q = GetMember(proc, "BattleCmdQueue") as ICollection;
            return q?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetBattleCommandQueueCount()
    {
        try
        {
            var proc = TryGetBattleProcesser();
            var runner = GetMember(proc, "m_CommandRunner");
            var q = GetMember(runner, "m_CmdQueue") as ICollection;
            return q?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>RunProcess 已把第一批指令放进 m_CurCmds / m_LuanList，第一个单位即将 Execute。</summary>
    private static bool IsFirstBattleUnitAnimStarting()
    {
        return GetBattleCurCmdCount() > 0 || GetBattleLuanCount() > 0;
    }

    private static int GetBattleCurCmdCount()
    {
        try
        {
            var d = GetMember(GetBattleCommandRunner(), "m_CurCmds") as ICollection;
            return d?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetBattleLuanCount()
    {
        try
        {
            var q = GetMember(GetBattleCommandRunner(), "m_LuanList") as ICollection;
            return q?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetBattleStatusQueueCount()
    {
        try
        {
            var proc = TryGetBattleProcesser();
            var q = GetMember(proc, "BattleStatusQueue") as ICollection;
            return q?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetBattleAccountQueueCount()
    {
        try
        {
            var list = GetStaticMember("BattleDataHolder", "AcountList") as ICollection;
            return list?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void ClearReflectCollection(object collection)
    {
        try
        {
            collection?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(collection, null);
        }
        catch
        {
            // ignore
        }
    }

    private static void ClearReflectDictionary(object dictionary)
    {
        try
        {
            (dictionary as IDictionary)?.Clear();
        }
        catch
        {
            // ignore
        }
    }

    private static float GetBattleGlobalTimeScale()
    {
        try
        {
            var brc = FindType("BattleRoleContainer");
            var f = brc?.GetField("GlobalTimeScale", BindingFlags.Public | BindingFlags.Static);
            return Convert.ToSingle(f?.GetValue(null) ?? 1f);
        }
        catch
        {
            return 1f;
        }
    }

    private static void ForceBattleGlobalTimeScale(float scale)
    {
        try
        {
            var brc = FindType("BattleRoleContainer");
            var m = brc?.GetMethod("SetAllRoleTimeScale",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float) }, null);
            m?.Invoke(null, new object[] { scale });
            // SetAllRoleTimeScale 会乘 BattleTimeScale；为 0 时 GlobalTimeScale 仍为 0，ProcessCommand 会卡死
            var f = brc?.GetField("GlobalTimeScale", BindingFlags.Public | BindingFlags.Static);
            if (f != null && Convert.ToSingle(f.GetValue(null) ?? 0f) <= 0f)
            {
                f.SetValue(null, scale);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void InvokeStopBattleCommand()
    {
        var proc = TryGetBattleProcesser();
        if (proc == null)
        {
            return;
        }

        proc.GetType().GetMethod("StopBattleCommand",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?.Invoke(proc, null);
    }

    private static object TryGetBattleProcesser()
    {
        try
        {
            var gmType = FindType("GameManagerHotfix");
            var mono = FindType("MonoSingleton`1");
            if (gmType == null || mono == null)
            {
                return null;
            }

            var closed = mono.MakeGenericType(gmType);
            var inst = closed.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                       ?.GetValue(null, null)
                       ?? closed.GetField("instance", BindingFlags.Public | BindingFlags.Static)
                           ?.GetValue(null);
            return GetMember(inst, "battleProcesser");
        }
        catch
        {
            return null;
        }
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

    /// <summary>脚本页：兑换野生宠名字输入 + A/B/C 列表。</summary>
    private static void AddWildPetNameRow(Type rtType, ref float y)
    {
        AddWildPetNameRowAt(rtType, 0f, y);
        y -= 72f;
    }

    private static void AddWildPetNameRowAt(Type rtType, float x, float y)
    {
        CaptureWildPetNameFromUi();
        var tip = CreateUiChild(_bodyRoot, "WildPetTip", rtType);
        SetAnchoredTop(RequireRect(tip, "wpt"), x, y, 540f, 22f);
        SetText(AddText(tip), "兑换目标（A幽灵 / B僵尸 / C骷髅战士）", 12);
        y -= 26f;

        _wildPetNameInput = CreateInputField(
            _bodyRoot, rtType, "WildPetName", -90f, y, 200f, 30f, _wildPetName ?? "", "例如 幽灵");

        var px = 130f;
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            var name = WildPetPresets[i];
            var btn = CreateUiChild(_bodyRoot, "WildPetP" + i, rtType);
            SetAnchoredTop(RequireRect(btn, "wpp" + i), px, y, 90f, 30f);
            var img = AddComp(btn, "UnityEngine.UI.Image");
            SetColor(img, 0.22f, 0.34f, 0.28f, 1f);
            var lab = CreateUiChild(btn, "L", rtType);
            StretchFull(RequireRect(lab, "wppl" + i));
            SetText(AddText(lab), name, 13);
            var captured = name;
            BindButton(btn, img, () => SelectWildPetPreset(captured));
            px += 100f;
        }
    }

    private static void SelectWildPetPreset(string name)
    {
        _wildPetName = name ?? "";
        try
        {
            if (_wildPetNameInput != null && !IsUnityNull(_wildPetNameInput))
            {
                SetProp(_wildPetNameInput, "text", _wildPetName);
            }
        }
        catch
        {
            // ignore
        }

        Tip("已选择 " + _wildPetName);
    }

    private static bool IsZhongyuanPetName(string name)
    {
        var n = (name ?? "").Trim();
        return string.Equals(n, ZhongyuanPetA, StringComparison.Ordinal)
               || string.Equals(n, ZhongyuanPetB, StringComparison.Ordinal)
               || string.Equals(n, ZhongyuanPetC, StringComparison.Ordinal);
    }

    /// <summary>脚本开启抓野生宠：战斗里三种都抓；name 只用于本轮导航/倒腾日志。</summary>
    private static bool ApplyCatchWildFromScript(string petName)
    {
        var name = (petName ?? "").Trim();
        if (!string.IsNullOrEmpty(name) && !IsZhongyuanPetName(name))
        {
            Tip("抓宠只支持幽灵/僵尸/骷髅战士");
            WriteLog("catch-wild reject name=" + name);
            return false;
        }

        ApplyBattleMode(ModeCatchWild);
        _battleMode = ModeCatchWild;
        _statusLine = string.IsNullOrEmpty(name) ? "抓野生宠" : ("抓野生宠: " + name);
        WriteLog("catch-wild script name=" + name
                 + " destSlot=" + GetZhongyuanDestSlot(name)
                 + " pathNodes=" + GetZhongyuanHuntPath(name).Length);
        return true;
    }

    private static int GetZhongyuanDestSlot(string petName)
    {
        var n = (petName ?? "").Trim();
        if (string.Equals(n, ZhongyuanPetA, StringComparison.Ordinal))
        {
            return ZhongyuanSlotPetA;
        }

        if (string.Equals(n, ZhongyuanPetB, StringComparison.Ordinal))
        {
            return ZhongyuanSlotPetB;
        }

        if (string.Equals(n, ZhongyuanPetC, StringComparison.Ordinal))
        {
            return ZhongyuanSlotPetC;
        }

        return -1;
    }

    /// <summary>A/B/C 抓宠路径（空表=尚未规划）。</summary>
    private static NavWaypoint[] GetZhongyuanHuntPath(string petName)
    {
        var n = (petName ?? "").Trim();
        if (string.Equals(n, ZhongyuanPetA, StringComparison.Ordinal))
        {
            return ZhongyuanHuntPathA;
        }

        if (string.Equals(n, ZhongyuanPetB, StringComparison.Ordinal))
        {
            return ZhongyuanHuntPathB;
        }

        if (string.Equals(n, ZhongyuanPetC, StringComparison.Ordinal))
        {
            return ZhongyuanHuntPathC;
        }

        return new NavWaypoint[0];
    }

    private static void CaptureWildPetNameFromUi()
    {
        if (_wildPetNameInput == null || IsUnityNull(_wildPetNameInput))
        {
            return;
        }

        try
        {
            var t = GetProp(_wildPetNameInput, "text") ?? GetMember(_wildPetNameInput, "text");
            var s = Convert.ToString(t ?? "") ?? "";
            _wildPetName = s.Trim();
        }
        catch
        {
            // keep previous
        }
    }

    private static string GetWildPetName()
    {
        CaptureWildPetNameFromUi();
        return (_wildPetName ?? "").Trim();
    }

    private static void TrySetAutoCatchWild(bool enable, string name)
    {
        _lastAppliedWildCatchName = enable ? (name ?? "") : "";
        var t = EnsureFeatureType("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes");
        if (t == null)
        {
            return;
        }

        try
        {
            var set = t.GetMethod(
                "SetWildCatch",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(bool), typeof(string) },
                null);
            if (set != null)
            {
                set.Invoke(null, new object[] { enable, name ?? "" });
                WriteLog("SetWildCatch enable=" + enable + " name=" + (name ?? ""));
                return;
            }

            var fMode = t.GetField("WildMode", BindingFlags.Public | BindingFlags.Static);
            if (fMode != null && fMode.FieldType == typeof(bool))
            {
                fMode.SetValue(null, enable);
            }

            var fName = t.GetField("WildTargetName", BindingFlags.Public | BindingFlags.Static);
            if (fName != null && fName.FieldType == typeof(string))
            {
                fName.SetValue(null, enable ? (name ?? "") : "");
            }
        }
        catch (Exception ex)
        {
            WriteLog("TrySetAutoCatchWild EX: " + RootMessage(ex));
        }
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
        SetShellSize(620f, 620f);

        float y = -4f;
        var saiAllowed = IsSuperAiModeAllowed(_battleMode);
        var sai = CreateUiChild(_bodyRoot, "SuperAi", rtType);
        SetAnchoredTop(RequireRect(sai, "sai"), 0f, y, 540f, 36f);
        var saiImg = AddComp(sai, "UnityEngine.UI.Image");
        if (!saiAllowed)
        {
            SetColor(saiImg, 0.25f, 0.18f, 0.14f, 1f);
            var saiLab = CreateUiChild(sai, "L", rtType);
            StretchFull(RequireRect(saiLab, "sail"));
            SetText(AddText(saiLab), "○ AI战斗（请先到「战斗」页选常规）", 14);
        }
        else
        {
            SetColor(saiImg, _superAiActive ? 0.18f : 0.15f, _superAiActive ? 0.42f : 0.38f,
                _superAiActive ? 0.28f : 0.55f, 1f);
            var saiLab = CreateUiChild(sai, "L", rtType);
            StretchFull(RequireRect(saiLab, "sail"));
            SetText(AddText(saiLab),
                (_superAiActive ? "● AI战斗（点此停止）" : "○ AI战斗（点此启动）") + " · " + ModeLabel(_battleMode), 14);
            BindButton(sai, saiImg, ToggleSuperAi);
        }

        y -= 48f;
        _superAiBattleRoot = CreateUiChild(_bodyRoot, "SuperAiHintHelp", rtType);
        SetAnchoredTop(RequireRect(_superAiBattleRoot, "saib"), 0f, y, 540f, 280f);
        SetColor(AddComp(_superAiBattleRoot, "UnityEngine.UI.Image"), 0.08f, 0.1f, 0.14f, 0.75f);
        var tip = CreateUiChild(_superAiBattleRoot, "Tip", rtType);
        StretchFull(RequireRect(tip, "tip"));
        var tx = AddText(tip);
        try { SetProp(tx, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0)); } catch { }
        _superAiStatusText = tx;
        SetText(tx, FormatSuperAiStatus(), 13);
        WriteLog("BuildSuperAiBody done");
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
            return "AI战斗（虚拟）：关闭\n"
                   + "开启后屏幕左侧列出本回合将发的指令。\n"
                   + "只写本客户端能实际发出的包（H/I/G/N/W），发不出的不写。\n"
                   + "血瓶仅 Type≥23 才写 I|格|目标；人血<50%、宠血<40%。";
        }

        return "AI战斗（虚拟）：运行中（只显示，不发包）\n模式: " + ModeLabel(_battleMode)
               + "\n左侧为本回合将发指令。\n"
               + (_superAiLastSimLine.Length > 0 ? _superAiLastSimLine : "等待进入战斗…");
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
            Tip("AI战斗：请先到「战斗」页选常规");
            return;
        }

        _superAiActive = true;
        _superAiLastHintKey = "";
        _superAiLastSimLine = "已启动，等待战斗回合…";
        _superAiHintTurn = -1;
        EnsureSuperAiHintOverlay();
        RefreshSuperAiHintOverlay("等待进入战斗…");
        Tip("AI战斗已开启");
        WriteLog("SuperAI start(hint-only) mode=" + _battleMode);
    }

    private static void StopSuperAi(string reason)
    {
        if (!_superAiActive)
        {
            return;
        }

        _superAiActive = false;
        _superAiLastSimLine = reason ?? "";
        _superAiUiPage = 0;
        _superAiDetailIndex = -1;
        _superAiUnits.Clear();
        _superAiPlannedCmds.Clear();
        _superAiLastHintKey = "";
        HideSuperAiHintOverlay();
        WriteLog("SuperAI stop: " + reason);
        Tip("AI战斗已关闭");
    }

    private static void TickSuperAi()
    {
        if (!_superAiActive)
        {
            return;
        }

        if (!IsSuperAiModeAllowed(_battleMode))
        {
            StopSuperAi("战斗模式非常规，已关闭AI战斗");
            return;
        }

        EnsureSuperAiHintOverlay();

        var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        if (!inBattle)
        {
            _superAiLastHintKey = "";
            _superAiHintTurn = -1;
            RefreshSuperAiHintOverlay("等待进入战斗…");
            return;
        }

        var battleIndex = Convert.ToInt32(GetStaticMember("BattleDataHolder", "BattleIndex") ?? -1);
        var turn = GetSuperAiBattleTurn();
        var cmdRunning = false;
        try
        {
            var bm = GetManagerInstance("BattleManager");
            cmdRunning = bm != null && Convert.ToBoolean(GetMember(bm, "CmdRunningFlag") ?? false);
        }
        catch
        {
            // ignore
        }

        // 回合开始：CHAR 刷新后选指令阶段（不在播动画）
        var key = battleIndex + "|" + turn;
        if (cmdRunning)
        {
            return;
        }

        if (key == _superAiLastHintKey)
        {
            return;
        }

        CollectSuperAiRoundUnits();
        FillSuperAiRoundSuggestions();
        _superAiLastHintKey = key;
        _superAiHintTurn = turn;
        var board = FormatSuperAiHintBoard(turn);
        _superAiLastSimLine = "第" + (turn + 1) + "回合 将发 " + _superAiPlannedCmds.Count + " 条";
        RefreshSuperAiHintOverlay(board);
        WriteLog("===== SuperAI HINT turn=" + turn + " key=" + key + " =====");
        WriteLog(board);
        if (_tab == TabSuperAi && _visible && _superAiStatusText != null && !IsUnityNull(_superAiStatusText))
        {
            SetText(_superAiStatusText, FormatSuperAiStatus(), 13);
        }
    }

    private static int GetSuperAiBattleTurn()
    {
        try
        {
            var proc = TryGetBattleProcesser();
            return Convert.ToInt32(GetMember(proc, "m_BattleSvTurnIndex") ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static void EnsureSuperAiHintOverlay()
    {
        try
        {
            if (_superAiHintCanvas != null && !IsUnityNull(_superAiHintCanvas))
            {
                SetGoActive(_superAiHintCanvas, true);
                LayoutSuperAiHintOverlay();
                return;
            }

            var rtType = RequireType("UnityEngine.RectTransform");
            var canvasType = RequireType("UnityEngine.Canvas");
            _superAiHintCanvas = CreateGoWithComponents(
                "SeqChapterAiHintOverlay",
                rtType,
                canvasType,
                FindType("UnityEngine.UI.CanvasScaler"));
            CallStatic(RequireType("UnityEngine.Object"), "DontDestroyOnLoad",
                new[] { RequireType("UnityEngine.Object") }, new[] { _superAiHintCanvas });

            var canvas = GetComp(_superAiHintCanvas, canvasType);
            SetProp(canvas, "renderMode", EnumValue("UnityEngine.RenderMode", "ScreenSpaceOverlay", 0));
            SetProp(canvas, "overrideSorting", true);
            SetProp(canvas, "sortingOrder", 32000);
            StretchFull(RequireRect(_superAiHintCanvas, "aiov"));

            var panel = CreateUiChild(_superAiHintCanvas, "Panel", rtType);
            var img = AddComp(panel, "UnityEngine.UI.Image");
            SetColor(img, 0.05f, 0.08f, 0.12f, 0.82f);
            try { SetProp(img, "raycastTarget", false); } catch { }

            var title = CreateUiChild(panel, "Title", rtType);
            var titleTx = AddText(title);
            try { SetProp(titleTx, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleLeft", 3)); } catch { }
            try { SetProp(titleTx, "raycastTarget", false); } catch { }

            var body = CreateUiChild(panel, "Body", rtType);
            _superAiHintText = AddText(body);
            try { SetProp(_superAiHintText, "alignment", EnumValue("UnityEngine.TextAnchor", "UpperLeft", 0)); } catch { }
            try { SetProp(_superAiHintText, "horizontalOverflow", EnumValue("UnityEngine.HorizontalWrapMode", "Wrap", 0)); } catch { }
            try { SetProp(_superAiHintText, "verticalOverflow", EnumValue("UnityEngine.VerticalWrapMode", "Overflow", 0)); } catch { }
            try { SetProp(_superAiHintText, "raycastTarget", false); } catch { }
            LayoutSuperAiHintOverlay();
            SetText(_superAiHintText, "等待进入战斗…", 12);
        }
        catch (Exception ex)
        {
            WriteLog("EnsureSuperAiHintOverlay EX: " + RootMessage(ex));
        }
    }

    private static void LayoutSuperAiHintOverlay()
    {
        if (_superAiHintCanvas == null || IsUnityNull(_superAiHintCanvas))
        {
            return;
        }

        var panel = GetChild(_superAiHintCanvas, "Panel");
        if (panel == null)
        {
            return;
        }

        SetAnchoredTopLeft(RequireRect(panel, "aip"), 8f, -72f, 360f, 210f);
        var title = GetChild(panel, "Title");
        if (title != null)
        {
            SetAnchoredTopLeft(RequireRect(title, "ait"), 8f, -6f, 344f, 22f);
            var tx = GetComp(title, FindType("UnityEngine.UI.Text"));
            if (tx != null)
            {
                SetText(tx, "AI战斗（虚拟）本回合将发", 13);
            }
        }

        var body = GetChild(panel, "Body");
        if (body != null)
        {
            SetAnchoredTopLeft(RequireRect(body, "aib"), 8f, -30f, 344f, 172f);
        }
    }

    private static void HideSuperAiHintOverlay()
    {
        try
        {
            if (_superAiHintCanvas != null && !IsUnityNull(_superAiHintCanvas))
            {
                SetGoActive(_superAiHintCanvas, false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void RefreshSuperAiHintOverlay(string body)
    {
        EnsureSuperAiHintOverlay();
        if (_superAiHintText != null && !IsUnityNull(_superAiHintText))
        {
            SetText(_superAiHintText, body ?? "", 12);
        }
    }

    private static string FormatSuperAiHintBoard(int turn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("第" + (turn + 1) + "回合 · 虚拟不发包");
        if (_superAiPlannedCmds.Count == 0)
        {
            sb.Append("（本客户端本回合无可发包）");
            return sb.ToString();
        }

        for (var i = 0; i < _superAiPlannedCmds.Count; i++)
        {
            var c = _superAiPlannedCmds[i];
            sb.AppendLine(c.Actor + "  " + c.Str + "  " + c.Label);
        }

        return sb.ToString().TrimEnd();
    }

    private static void CollectSuperAiRoundUnits()
    {
        _superAiUnits.Clear();
        try
        {
            var container = FindType("BattleRoleContainer");
            var dic = container?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                      ?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                return;
            }

            var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
            var allySide = playerIdx < 10;
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

                var bc = Convert.ToInt64(GetMember(ch, "Bcflag") ?? 0);
                var hp = Convert.ToInt32(GetMember(ch, "Hp") ?? 0);
                var maxHp = Convert.ToInt32(GetMember(ch, "MaxHp") ?? 0);
                var snap = new SuperAiUnitSnap();
                snap.Idx = idx;
                snap.Mine = (allySide && idx < 10) || (!allySide && idx >= 10);
                snap.IsPlayer = (bc & 4L) != 0;
                snap.Name = Convert.ToString(GetMember(ch, "Name") ?? "") ?? "";
                snap.Level = Convert.ToInt32(GetMember(ch, "Level") ?? 0);
                snap.Hp = hp;
                snap.MaxHp = maxHp;
                snap.Mp = Convert.ToInt32(GetMember(ch, "Mp") ?? 0);
                snap.MaxMp = Convert.ToInt32(GetMember(ch, "MaxMp") ?? 0);
                snap.Bc = bc;
                snap.Status = FormatBcStatus(bc);
                snap.Unable = IsSuperAiUnable(bc, hp);
                snap.Suggest = "";
                snap.Uid = "";
                snap.JobName = "";
                snap.JobAncestry = "";
                snap.Rec = 0;

                if (snap.Mine)
                {
                    FillSuperAiAllyIdentity(ref snap);
                }

                _superAiUnits.Add(snap);
            }

            _superAiUnits.Sort((a, b) =>
            {
                if (a.Mine != b.Mine)
                {
                    return a.Mine ? -1 : 1;
                }

                if (a.IsPlayer != b.IsPlayer)
                {
                    return a.IsPlayer ? -1 : 1;
                }

                return a.Idx.CompareTo(b.Idx);
            });
        }
        catch (Exception ex)
        {
            WriteLog("CollectSuperAiRoundUnits EX: " + RootMessage(ex));
        }
    }

    private static bool IsSuperAiUnable(long bc, int hp)
    {
        if (hp <= 0)
        {
            return true;
        }

        return (bc & 2L) != 0 || (bc & 0x20L) != 0 || (bc & 0x40L) != 0;
    }

    private static void FillSuperAiAllyIdentity(ref SuperAiUnitSnap snap)
    {
        try
        {
            var ownerIdx = (snap.Idx % 10) >= 5 ? snap.Idx - 5 : snap.Idx;
            var uid = FindUidByBattleIndex(snap.IsPlayer ? snap.Idx : ownerIdx) ?? "";
            snap.Uid = uid;
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

            snap.JobName = Convert.ToString(GetMember(player, "JobName") ?? "") ?? "";
            snap.JobAncestry = Convert.ToString(GetMember(player, "JobAncestryName") ?? "") ?? "";
            if (snap.IsPlayer)
            {
                snap.Rec = Convert.ToInt32(GetMember(player, "Recovery") ?? 0);
            }
            else
            {
                TryFillAllySystemDetail(ref snap, false, false, uid, snap.Idx);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsSuperAiPriestJob(SuperAiUnitSnap u)
    {
        var a = u.JobAncestry ?? "";
        var j = u.JobName ?? "";
        return a.IndexOf("传教", StringComparison.Ordinal) >= 0
               || j.IndexOf("传教", StringComparison.Ordinal) >= 0;
    }

    private static void FillSuperAiRoundSuggestions()
    {
        FillSuperAiSendableCommands();
    }

    private static void AddSuperAiPlannedCmd(string actor, string str, string label)
    {
        if (string.IsNullOrEmpty(str))
        {
            return;
        }

        var cmd = new SuperAiPlannedCmd();
        cmd.Actor = actor ?? "";
        cmd.Str = str;
        cmd.Label = label ?? "";
        _superAiPlannedCmds.Add(cmd);
    }

    private static string SuperAiHex(int n)
    {
        return n.ToString("X");
    }

    /// <summary>
    /// 虚拟版：只为本客户端（CurrentAccount 人物+宠）列出能实际 SendBattleCommond 的包。
    /// 敌方/队友/发不出的动作一律不写。
    /// </summary>
    private static void FillSuperAiSendableCommands()
    {
        _superAiPlannedCmds.Clear();
        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            var u = _superAiUnits[i];
            u.Suggest = "";
            _superAiUnits[i] = u;
        }

        SuperAiUnitSnap player;
        SuperAiUnitSnap pet;
        bool hasPlayer;
        bool hasPet;
        if (!TryGetSuperAiSelfUnits(out player, out hasPlayer, out pet, out hasPet) || !hasPlayer)
        {
            return;
        }

        int enemyIdx;
        string enemyName;
        var hasEnemy = TryFindSuperAiLiveEnemy(out enemyIdx, out enemyName);
        var playerActor = "人 " + (player.Name ?? "?");

        if (player.Unable)
        {
            AddSuperAiPlannedCmd(playerActor, "N", "待机（" + (player.Status ?? "异常") + "）");
            player.Suggest = "N";
        }
        else
        {
            string potStr;
            string potLabel;
            if (TryBuildSuperAiPotionCmd(player, hasPet, pet, out potStr, out potLabel))
            {
                AddSuperAiPlannedCmd(playerActor, potStr, potLabel);
                player.Suggest = potStr;
            }
            else if (hasEnemy)
            {
                var str = "H|" + SuperAiHex(enemyIdx);
                AddSuperAiPlannedCmd(playerActor, str, "攻击 " + enemyName + "#" + enemyIdx);
                player.Suggest = str;
            }
            else
            {
                AddSuperAiPlannedCmd(playerActor, "G", "防御");
                player.Suggest = "G";
            }
        }

        WriteBackSuperAiUnit(player);

        if (!hasPet)
        {
            return;
        }

        var petActor = "宠 " + (pet.Name ?? "?");
        if (pet.Unable)
        {
            AddSuperAiPlannedCmd(petActor, "W|FF|FF", "待机（" + (pet.Status ?? "异常") + "）");
            pet.Suggest = "W|FF|FF";
        }
        else
        {
            int slot;
            if (hasEnemy && TryGetSuperAiPetAttackSlot(player.Uid, out slot) && slot >= 0)
            {
                var str = "W|" + SuperAiHex(slot) + "|" + SuperAiHex(enemyIdx);
                AddSuperAiPlannedCmd(petActor, str, "攻击 " + enemyName + "#" + enemyIdx);
                pet.Suggest = str;
            }
            else
            {
                AddSuperAiPlannedCmd(petActor, "W|FF|FF", "待机");
                pet.Suggest = "W|FF|FF";
            }
        }

        WriteBackSuperAiUnit(pet);
    }

    private static void WriteBackSuperAiUnit(SuperAiUnitSnap snap)
    {
        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            if (_superAiUnits[i].Idx == snap.Idx)
            {
                _superAiUnits[i] = snap;
                return;
            }
        }
    }

    private static bool TryGetSuperAiSelfUnits(
        out SuperAiUnitSnap player, out bool hasPlayer,
        out SuperAiUnitSnap pet, out bool hasPet)
    {
        player = default(SuperAiUnitSnap);
        pet = default(SuperAiUnitSnap);
        hasPlayer = false;
        hasPet = false;
        var uid = Convert.ToString(GetStaticMember("BattleDataHolder", "CurrentAccount") ?? "") ?? "";
        var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            var u = _superAiUnits[i];
            if (u.Idx == playerIdx && u.IsPlayer)
            {
                player = u;
                hasPlayer = true;
            }
            else if (!u.IsPlayer && u.Mine && playerIdx >= 0
                     && (u.Idx / 10) == (playerIdx / 10) && Math.Abs(u.Idx - playerIdx) == 5)
            {
                pet = u;
                hasPet = true;
            }
        }

        if (!hasPlayer && uid.Length > 0)
        {
            for (var i = 0; i < _superAiUnits.Count; i++)
            {
                var u = _superAiUnits[i];
                if (u.IsPlayer && string.Equals(u.Uid, uid, StringComparison.Ordinal))
                {
                    player = u;
                    hasPlayer = true;
                    playerIdx = u.Idx;
                    break;
                }
            }

            if (hasPlayer && !hasPet)
            {
                for (var i = 0; i < _superAiUnits.Count; i++)
                {
                    var u = _superAiUnits[i];
                    if (!u.IsPlayer && u.Mine && (u.Idx / 10) == (playerIdx / 10)
                        && Math.Abs(u.Idx - playerIdx) == 5)
                    {
                        pet = u;
                        hasPet = true;
                        break;
                    }
                }
            }
        }

        return hasPlayer;
    }

    private static bool TryFindSuperAiLiveEnemy(out int idx, out string name)
    {
        idx = -1;
        name = "";
        var best = int.MaxValue;
        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            var u = _superAiUnits[i];
            if (u.Mine || u.Unable || u.Hp <= 0)
            {
                continue;
            }

            if (u.Idx < best)
            {
                best = u.Idx;
                idx = u.Idx;
                name = u.Name ?? "?";
            }
        }

        return idx >= 0;
    }

    private static bool TryBuildSuperAiPotionCmd(
        SuperAiUnitSnap player, bool hasPet, SuperAiUnitSnap pet,
        out string str, out string label)
    {
        str = "";
        label = "";
        if (player.Unable)
        {
            return false;
        }

        if (IsSuperAiPriestJob(player) && player.Mp > SuperAiPriestSkipPotionMp)
        {
            return false;
        }

        var pots = ScanSuperAiHpPotions(player.Uid);
        if (pots.Count == 0)
        {
            return false;
        }

        SuperAiUnitSnap target;
        var hasTarget = false;
        if (SuperAiNeedsPotion(player))
        {
            target = player;
            hasTarget = true;
        }
        else if (hasPet && SuperAiNeedsPotion(pet))
        {
            target = pet;
            hasTarget = true;
        }
        else
        {
            target = default(SuperAiUnitSnap);
            for (var i = 0; i < _superAiUnits.Count; i++)
            {
                var u = _superAiUnits[i];
                if (!u.Mine || !SuperAiNeedsPotion(u))
                {
                    continue;
                }

                if (!hasTarget || (u.IsPlayer && !target.IsPlayer))
                {
                    target = u;
                    hasTarget = true;
                }
            }
        }

        if (!hasTarget)
        {
            return false;
        }

        var rec = Math.Max(1, target.Rec);
        var missing = Math.Max(1, target.MaxHp - target.Hp);
        var best = 0;
        var bestScore = int.MaxValue;
        for (var i = 0; i < pots.Count; i++)
        {
            if (pots[i].Type < SuperAiBattleItemMinType)
            {
                continue;
            }

            var heal = pots[i].Power * rec;
            var over = Math.Abs(heal - missing);
            if (over < bestScore)
            {
                bestScore = over;
                best = i;
            }
        }

        if (best < 0 || best >= pots.Count || pots[best].Type < SuperAiBattleItemMinType)
        {
            return false;
        }

        var pot = pots[best];
        str = "I|" + SuperAiHex(pot.BagIndex) + "|" + SuperAiHex(target.Idx);
        label = (target.Idx == player.Idx ? "对自己用 " : "投给" + (target.Name ?? "?") + " ")
                + (pot.Name ?? "血瓶");
        return true;
    }

    private static bool TryGetSuperAiPetAttackSlot(string uid, out int slot)
    {
        slot = -1;
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        try
        {
            var getPlayer = FindType("PlayerDataHolder")?.GetMethod(
                "GetPlayerFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var player = getPlayer?.Invoke(null, new object[] { uid });
            var battlePetId = player != null ? Convert.ToInt32(GetMember(player, "battlePetID") ?? -1) : -1;
            if (battlePetId < 0)
            {
                return false;
            }

            var getPets = FindType("PlayerDataHolder")?.GetMethod(
                "GetPetDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var pets = getPets?.Invoke(null, new object[] { uid }) as IList;
            if (pets == null || battlePetId >= pets.Count || pets[battlePetId] == null)
            {
                return false;
            }

            var pd = GetMember(pets[battlePetId], "data");
            var skills = GetMember(pd, "PetSkills") as IList;
            if (skills == null)
            {
                return false;
            }

            for (var i = 0; i < skills.Count; i++)
            {
                var tech = skills[i];
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
                if (skillId == SuperAiPetAttackSkillId)
                {
                    slot = i;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryGetSuperAiPetAttackSlot EX: " + RootMessage(ex));
        }

        return false;
    }

    /// <summary>
    /// 血瓶：生命力回复药N，回血=N×目标回复力。
    /// 人&lt;50%、宠&lt;40%才建议；优先人；优先自己给自己；宠物不丢（等位）；
    /// 传教 MP&gt;200 不用药；不能行动者不丢，改由其他人丢。
    /// </summary>
    private static void AssignSuperAiPotionSuggestions()
    {
        var need = new List<int>();
        var throwers = new List<int>();
        var potions = new Dictionary<int, List<SuperAiPotion>>();

        for (var i = 0; i < _superAiUnits.Count; i++)
        {
            var u = _superAiUnits[i];
            if (!u.Mine)
            {
                continue;
            }

            if (SuperAiNeedsPotion(u))
            {
                need.Add(i);
            }

            if (!u.IsPlayer || u.Unable)
            {
                continue;
            }

            if (IsSuperAiPriestJob(u) && u.Mp > SuperAiPriestSkipPotionMp)
            {
                continue;
            }

            var pots = ScanSuperAiHpPotions(u.Uid);
            if (pots.Count == 0)
            {
                continue;
            }

            throwers.Add(i);
            potions[i] = pots;
        }

        need.Sort((ia, ib) =>
        {
            var a = _superAiUnits[ia];
            var b = _superAiUnits[ib];
            if (a.IsPlayer != b.IsPlayer)
            {
                return a.IsPlayer ? -1 : 1;
            }

            var ra = a.MaxHp > 0 ? a.Hp / (float)a.MaxHp : 1f;
            var rb = b.MaxHp > 0 ? b.Hp / (float)b.MaxHp : 1f;
            return ra.CompareTo(rb);
        });

        var usedThrower = new bool[_superAiUnits.Count];

        // 1) 能行动的人优先自己吃药
        for (var n = 0; n < need.Count; n++)
        {
            var ti = need[n];
            var t = _superAiUnits[ti];
            if (!t.IsPlayer || t.Unable || usedThrower[ti])
            {
                continue;
            }

            if (!potions.ContainsKey(ti) || potions[ti].Count == 0)
            {
                continue;
            }

            ApplyPotionSuggest(ti, ti, potions[ti]);
            usedThrower[ti] = true;
        }

        // 2) 其余需求（含不能动的人、等位宠物）由还能丢药的人补
        for (var n = 0; n < need.Count; n++)
        {
            var ti = need[n];
            var t = _superAiUnits[ti];
            if (!string.IsNullOrEmpty(t.Suggest) && t.Suggest.IndexOf("血瓶", StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            var thrower = -1;
            for (var k = 0; k < throwers.Count; k++)
            {
                var fi = throwers[k];
                if (usedThrower[fi])
                {
                    continue;
                }

                List<SuperAiPotion> pots;
                if (!potions.TryGetValue(fi, out pots) || pots == null || pots.Count == 0)
                {
                    continue;
                }

                thrower = fi;
                break;
            }

            if (thrower < 0)
            {
                if (!t.Unable)
                {
                    t.Suggest = t.IsPlayer
                        ? "需血瓶（无人可丢）"
                        : "等位血瓶（无人可丢）";
                    _superAiUnits[ti] = t;
                }

                continue;
            }

            List<SuperAiPotion> bag;
            potions.TryGetValue(thrower, out bag);
            ApplyPotionSuggest(thrower, ti, bag);
            usedThrower[thrower] = true;
        }
    }

    private static bool SuperAiNeedsPotion(SuperAiUnitSnap u)
    {
        if (!u.Mine || u.MaxHp <= 0)
        {
            return false;
        }

        if ((u.Bc & 2L) != 0 || u.Hp <= 0)
        {
            return false;
        }

        var ratio = u.Hp / (float)u.MaxHp;
        if (u.IsPlayer)
        {
            return ratio < SuperAiPlayerPotionHpRatio;
        }

        return ratio < SuperAiPetPotionHpRatio;
    }

    private static void ApplyPotionSuggest(int throwerIdx, int targetIdx, List<SuperAiPotion> bag)
    {
        if (bag == null || bag.Count == 0)
        {
            return;
        }

        var target = _superAiUnits[targetIdx];
        var missing = Math.Max(1, target.MaxHp - target.Hp);
        var rec = Math.Max(1, target.Rec);
        var best = 0;
        var bestScore = int.MaxValue;
        for (var i = 0; i < bag.Count; i++)
        {
            var heal = bag[i].Power * rec;
            var over = Math.Abs(heal - missing);
            if (over < bestScore)
            {
                bestScore = over;
                best = i;
            }
        }

        var pot = bag[best];
        var est = pot.Power * rec;
        var thrower = _superAiUnits[throwerIdx];
        var self = throwerIdx == targetIdx;
        var line = (self ? "吃血瓶 " : "丢血瓶给" + (target.Name ?? "?") + " ")
                   + pot.Name
                   + " 估回" + est
                   + "(×回复" + rec + ")";
        thrower.Suggest = line;
        _superAiUnits[throwerIdx] = thrower;
        if (!self)
        {
            if (!target.Unable)
            {
                target.Suggest = "等待" + (thrower.Name ?? "?") + "丢血瓶";
                _superAiUnits[targetIdx] = target;
            }
        }

        pot.Count--;
        if (pot.Count <= 0)
        {
            bag.RemoveAt(best);
        }
        else
        {
            bag[best] = pot;
        }
    }

    private static List<SuperAiPotion> ScanSuperAiHpPotions(string uid)
    {
        var list = new List<SuperAiPotion>();
        if (string.IsNullOrEmpty(uid))
        {
            return list;
        }

        try
        {
            var getItems = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var items = getItems?.Invoke(null, new object[] { uid }) as IList;
            if (items == null)
            {
                return list;
            }

            for (var i = 8; i < items.Count && i <= 67; i++)
            {
                var it = items[i];
                if (it == null || Convert.ToInt32(GetMember(it, "useFlag") ?? 0) != 1)
                {
                    continue;
                }

                var data = GetMember(it, "data");
                SuperAiPotion pot;
                if (!TryParseSuperAiHpPotion(data, i, out pot))
                {
                    continue;
                }

                if (pot.Type < SuperAiBattleItemMinType)
                {
                    continue;
                }

                list.Add(pot);
            }
        }
        catch (Exception ex)
        {
            WriteLog("ScanSuperAiHpPotions EX: " + RootMessage(ex));
        }

        return list;
    }

    private static bool TryParseSuperAiHpPotion(object data, int bagIndex, out SuperAiPotion pot)
    {
        pot = new SuperAiPotion();
        if (data == null)
        {
            return false;
        }

        var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
        var secret = Convert.ToString(GetMember(data, "Secretname") ?? "") ?? "";
        var label = Convert.ToString(GetMember(data, "Label") ?? "") ?? "";
        var raw = secret.Length > 0 ? secret : name;
        if (raw.IndexOf(SuperAiPotionNamePrefix, StringComparison.Ordinal) < 0
            && name.IndexOf(SuperAiPotionNamePrefix, StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var power = ParseTrailingNumber(raw);
        if (power <= 0)
        {
            power = ParseTrailingNumber(name);
        }

        if (power <= 0)
        {
            power = ParseTrailingNumber(label);
        }

        if (power <= 0)
        {
            return false;
        }

        var shown = name.IndexOf(SuperAiPotionNamePrefix, StringComparison.Ordinal) >= 0
            ? name
            : SuperAiPotionNamePrefix + power;
        pot.Name = shown;
        pot.Power = power;
        pot.Count = Math.Max(1, Convert.ToInt32(GetMember(data, "Pile") ?? 1));
        pot.BagIndex = bagIndex;
        pot.Type = Convert.ToInt32(GetMember(data, "Type") ?? 0);
        return true;
    }

    private static int ParseTrailingNumber(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }

        var i = s.Length - 1;
        while (i >= 0 && s[i] >= '0' && s[i] <= '9')
        {
            i--;
        }

        if (i == s.Length - 1)
        {
            return 0;
        }

        var num = 0;
        for (var k = i + 1; k < s.Length; k++)
        {
            num = num * 10 + (s[k] - '0');
        }

        return num;
    }

    /// <summary>模拟决策占位：不发包、不改 Auto 配置。</summary>
    private static string SimulateSuperAiDecision(string dump)
    {
        return dump ?? "";
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

    private static void AddScriptColButton(
        Type rtType, string name, string id, float x, float y,
        float r, float g, float b, string label, Action onClick)
    {
        var go = CreateUiChild(_bodyRoot, name, rtType);
        SetAnchoredTop(RequireRect(go, id), x, y, 250f, 38f);
        var img = AddComp(go, "UnityEngine.UI.Image");
        SetColor(img, r, g, b, 1f);
        var lab = CreateUiChild(go, "L", rtType);
        StretchFull(RequireRect(lab, id + "l"));
        SetText(AddText(lab), label, 14);
        BindButton(go, img, onClick);
    }

    private static void BuildScriptBody()
    {
        var rtType = RequireType("UnityEngine.RectTransform");
        const float leftX = -130f;
        const float rightX = 130f;
        const float row0 = -48f;
        const float rowStep = 46f;

        var hint = CreateUiChild(_bodyRoot, "Hint", rtType);
        SetAnchoredTop(RequireRect(hint, "hs"), 0f, -4f, 540f, 40f);
        SetText(
            AddText(hint),
            "简单脚本：左右两排点按钮。",
            12);

        AddScriptColButton(rtType, "Daily", "db", leftX, row0,
            0.2f, 0.4f, 0.55f, "做日常（开/停）", RunDailyClaim);
        AddScriptColButton(rtType, "Gift", "gb", rightX, row0,
            0.45f, 0.32f, 0.18f, "礼包码（开/停）", RunGiftClaim);

        AddScriptColButton(rtType, "AreaExtractNow", "aen", leftX, row0 - rowStep,
            0.18f, 0.42f, 0.38f, "立刻提取采集物", RunAreaExtractNow);
        AddScriptColButton(rtType, "AutoPoint", "apb", rightX, row0 - rowStep,
            0.55f, 0.35f, 0.65f, "一键加点", RunAutoPoint);

        AddScriptColButton(rtType, "PetNamer", "pnb", leftX, row0 - rowStep * 2,
            0.20f, 0.45f, 0.60f, "一键命名（开/停）", RunPetNamer);
        AddScriptColButton(
            rtType, "FloraHealTest", "fht", rightX, row0 - rowStep * 2,
            0.22f, 0.48f, 0.40f,
            _floraHealActive ? "法兰治疗（停止）" : "法兰治疗测试",
            ToggleFloraHeal);

        AddScriptColButton(
            rtType, "WildExchange", "wex", leftX, row0 - rowStep * 3,
            0.42f, 0.28f, 0.18f,
            _wildExActive && !_wildExInLoop ? "兑换野生宠（停止）" : "兑换野生宠",
            ToggleWildExchange);

        var wildY = row0 - rowStep * 4;
        var zyStatus = CreateUiChild(_bodyRoot, "ZhongyuanStatus", rtType);
        SetAnchoredTop(RequireRect(zyStatus, "zys"), 0f, wildY, 540f, 88f);
        _zyStatusText = AddText(zyStatus);
        SetText(_zyStatusText, FormatWildExchangeStatus(), 12);
        wildY -= 92f;

        var fhStatus = CreateUiChild(_bodyRoot, "FloraHealStatus", rtType);
        SetAnchoredTop(RequireRect(fhStatus, "fhs"), 0f, wildY, 540f, 48f);
        _floraHealStatusText = AddText(fhStatus);
        SetText(_floraHealStatusText, FormatFloraHealStatus(), 12);

        // 自动全套脚本 / 一键上架 / 刷熊男 / 一键命名说明 / 测试铃声 / 刷灵堂：入口隐藏（逻辑保留）
        _fullScriptStatusText = null;
        _petNamerStatusText = null;
        _lingTangStatusText = null;
    }

    private static string FormatSkCNavStatus()
    {
        if (!_skCNavActive)
        {
            return "骷髅战士导航: 未启动  #1008护航→402停→(118,100)切4400→(106,54)切4403点墙→(41,39)→4404(70,8)";
        }

        return "骷髅战士导航: " + SkCNavPhaseName(_skCNavPhase) + "  " + (_skCNavNote ?? "");
    }

    private static string SkCNavPhaseName(int phase)
    {
        switch (phase)
        {
            case SkCPhaseEscort: return "护航#1008";
            case SkCPhaseAbortWait: return "402停护航清路径";
            case SkCPhaseNav402: return "402(118,100)切4400";
            case SkCPhaseNav4400: return "4400(106,54)切4403";
            case SkCPhaseNav4403: return "去4403(26,38)";
            case SkCPhaseTalkWall: return "点(28,38)快要崩裂的墙壁";
            case SkCPhaseNavVia: return "过墙后(41,39)";
            case SkCPhaseNav4404: return "4404(70,8)";
            default: return "准备";
        }
    }

    private static void ToggleSkCNav()
    {
        if (_skCNavActive)
        {
            StopSkCNav("已手动停止");
            return;
        }

        StartSkCNav();
    }

    private static void StartSkCNav()
    {
        StartSkCNavCore();
    }

    private static bool StartSkCNavCore()
    {
        if (_escortActive && !_skCNavOwnsEscort)
        {
            Tip("请先停任务护航，再开骷髅战士导航");
            return false;
        }

        if (_dragonLoopActive || _midAutumnLoopActive)
        {
            Tip("龙族/七夕循环进行中，不能开骷髅战士导航");
            return false;
        }

        if (!_zyAllActive && (_floraHealActive || _lingTangActive || _zyCatchActive || _zyXferActive))
        {
            Tip("请先停其它脚本再开骷髅战士导航");
            return false;
        }

        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        _skCNavLastOk = false;
        _skCNavActive = true;
        _skCNavSawDialogue = false;
        _skCNavStuckFails = 0;
        _skCNavStuckShuffles = 0;
        _skCNavStuckNavTriedFirst = false;
        _skCNavStuckPending = false;
        _skCNavLastNavMs = 0;
        _skCNavLastNpcMs = 0;
        _skCNavLastActivityMs = NowMs();
        _skCNavLastFloor = floor;
        TryGetPlayerXY(out _skCNavLastX, out _skCNavLastY);
        ResetSkCStuck();

        if (floor == 4404)
        {
            _skCNavPhase = SkCPhaseNav4404;
            _skCNavNote = "已在4404，去(70,8)";
        }
        else if (floor == 4403)
        {
            if (SkCPassedWall(_skCNavLastX, _skCNavLastY))
            {
                SkCEnterAfterWall("start-east");
            }
            else if (SkCAtWallStand(_skCNavLastX, _skCNavLastY) || SkCAtWallNpc(_skCNavLastX, _skCNavLastY))
            {
                _skCNavPhase = SkCPhaseTalkWall;
                _skCNavNote = "已在墙边，点(28,38)墙壁";
            }
            else
            {
                _skCNavPhase = SkCPhaseNav4403;
                _skCNavNote = "已在4403，去(26,38)";
                TryWalkTo(SkCWallStandX, SkCWallStandY);
                _skCNavLastNavMs = NowMs();
            }
        }
        else if (floor == 4400)
        {
            _skCNavPhase = SkCPhaseNav4400;
            _skCNavNote = "已在4400，去(106,54)";
            SkCIssueNav(4400, SkCWarp4400X, SkCWarp4400Y, true);
        }
        else if (floor == SkCStopEscortFloor)
        {
            _skCNavPhase = SkCPhaseNav402;
            _skCNavNote = "已在402，去(118,100)切4400";
            SkCIssueNav(402, SkCWarp402X, SkCWarp402Y, true);
        }
        else if (!StartSkCOwnedEscort())
        {
            _skCNavActive = false;
            return false;
        }

        Tip("骷髅战士导航已开启");
        WriteLog("skc start floor=" + floor + " phase=" + _skCNavPhase);
        RefreshScriptTabIfVisible();
        return true;
    }

    private static bool StartSkCOwnedEscort()
    {
        _skCNavSavedQueue.Clear();
        for (var i = 0; i < _escortQueue.Count; i++)
        {
            _skCNavSavedQueue.Add(_escortQueue[i]);
        }

        _skCNavSavedIndex = _escortQueueIndex;
        _escortQueue.Clear();
        var title = "#1008";
        try
        {
            var mission = GetMissionDataById(SkCMissionId);
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

        _escortQueue.Add(new EscortCandidate
        {
            Id = SkCMissionId,
            Title = title,
            Status = "脚本"
        });
        _skCNavOwnsEscort = true;
        _skCNavPhase = SkCPhaseEscort;
        _skCNavNote = "护航#1008，进402后停再走路切4400";
        try
        {
            StartEscortQueue();
        }
        catch (Exception ex)
        {
            WriteLog("skc start escort EX " + RootMessage(ex));
            ReleaseSkCOwnedEscort();
            Tip("骷髅战士导航：启动#1008失败");
            return false;
        }

        if (!_escortActive)
        {
            ReleaseSkCOwnedEscort();
            Tip("骷髅战士导航：#1008护航未启动");
            return false;
        }

        return true;
    }

    private static void StopSkCNav(string reason)
    {
        if (!_skCNavActive && _skCNavPhase == SkCPhaseIdle)
        {
            if (_skCNavOwnsEscort)
            {
                ReleaseSkCOwnedEscort();
            }

            return;
        }

        ReleaseSkCOwnedEscort();
        _skCNavActive = false;
        _skCNavPhase = SkCPhaseIdle;
        _skCNavNote = reason ?? "";
        _skCNavLastOk = (_skCNavNote ?? "").IndexOf("已到达", StringComparison.Ordinal) >= 0;
        _skCNavStuckPending = false;
        _skCNavLastFloor = int.MinValue;
        try
        {
            StopTaskNavigation();
        }
        catch
        {
            // ignore
        }

        WriteLog("skc stop " + reason);
        Tip("骷髅战士导航：" + reason);
        RefreshScriptTabIfVisible();
    }

    private static void HandOffSkCNavFromEscort(string reason)
    {
        WriteLog("skc handoff " + reason);
        ReleaseSkCOwnedEscort();
        _skCNavPhase = SkCPhaseAbortWait;
        _skCNavAbortUntilMs = NowMs() + SkCAbortWaitMs;
        _skCNavNote = "已停护航，清路径2秒";
        Tip("骷髅战士导航：已进402，停止#1008");
    }

    private static void ReleaseSkCOwnedEscort()
    {
        if (!_skCNavOwnsEscort)
        {
            return;
        }

        _skCNavOwnsEscort = false;
        try
        {
            AbortEscortTaskPathFully("skc-release");
        }
        catch
        {
            // ignore
        }

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
        ClearEscortStuckPending();
        _escortFinishWaitMs = 0;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("skc-release", false);
        _escortPrevInBattle = false;
        _escortQueue.Clear();
        for (var i = 0; i < _skCNavSavedQueue.Count; i++)
        {
            _escortQueue.Add(_skCNavSavedQueue[i]);
        }

        _escortQueueIndex = _skCNavSavedIndex;
        _skCNavSavedQueue.Clear();
        _skCNavSavedIndex = -1;
        _prevRunTaskId = GetRunTaskId();
        if (wasActive)
        {
            try
            {
                StopTaskNavigation(false);
            }
            catch
            {
                // ignore
            }
        }

        TryRebuildEscortTab();
        WriteLog("skc restored escort queue n=" + _escortQueue.Count);
    }

    private static void TickSkCNav()
    {
        if (!_skCNavActive)
        {
            return;
        }

        var now = NowMs();
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        int x;
        int y;
        TryGetPlayerXY(out x, out y);
        var inBattle = false;
        try
        {
            inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        }
        catch
        {
            // ignore
        }

        var dialogueOpen = IsDialoguePanelOpen();
        if (dialogueOpen)
        {
            TryAutoPickDialogue();
            _skCNavSawDialogue = true;
            _skCNavLastActivityMs = now;
        }

        if (_skCNavPhase == SkCPhaseEscort)
        {
            _skCNavNote = _escortPaused
                ? "护航已暂停"
                : ("护航#1008 当前图" + floor);
            return;
        }

        if (_skCNavPhase == SkCPhaseAbortWait)
        {
            try
            {
                AbortEscortTaskPathFully("skc-abort-wait");
            }
            catch
            {
                // ignore
            }

            if (now < _skCNavAbortUntilMs)
            {
                return;
            }

            if (floor == 4404)
            {
                _skCNavPhase = SkCPhaseNav4404;
                _skCNavNote = "已在4404，去(70,8)";
                ResetSkCStuck();
                _skCNavLastFloor = floor;
                return;
            }

            if (floor == 4403)
            {
                if (SkCPassedWall(x, y))
                {
                    SkCEnterAfterWall("handoff-east");
                    _skCNavLastFloor = floor;
                    return;
                }

                _skCNavPhase = SkCPhaseNav4403;
                _skCNavNote = "已在4403，走到(26,38)";
                TryWalkTo(SkCWallStandX, SkCWallStandY);
                _skCNavLastNavMs = now;
                _skCNavLastActivityMs = now;
                _skCNavLastFloor = floor;
                return;
            }

            if (floor == 4400)
            {
                _skCNavPhase = SkCPhaseNav4400;
                _skCNavNote = "已切4400，去(106,54)";
                SkCIssueNav(4400, SkCWarp4400X, SkCWarp4400Y, true);
                _skCNavLastActivityMs = now;
                _skCNavLastFloor = floor;
                return;
            }

            _skCNavPhase = SkCPhaseNav402;
            _skCNavNote = "去402(118,100)切4400";
            SkCIssueNav(402, SkCWarp402X, SkCWarp402Y, true);
            _skCNavLastActivityMs = now;
            _skCNavLastFloor = floor;
            return;
        }

        if (IsMapLoading())
        {
            _skCNavLastActivityMs = now;
            _skCNavLastFloor = floor;
            return;
        }

        if (_skCNavLastFloor == int.MinValue)
        {
            _skCNavLastFloor = floor;
        }
        else if (floor != _skCNavLastFloor)
        {
            WriteLog("skc floor change " + _skCNavLastFloor + "->" + floor);
            _skCNavLastFloor = floor;
            _skCNavLastActivityMs = now;
        }

        if (inBattle)
        {
            _skCNavLastActivityMs = now;
            _skCNavStuckPending = false;
            return;
        }

        if (x != _skCNavLastX || y != _skCNavLastY)
        {
            _skCNavLastX = x;
            _skCNavLastY = y;
            _skCNavLastActivityMs = now;
            if (!_skCNavStuckPending)
            {
                _skCNavStuckNavTriedFirst = false;
                _skCNavStuckShuffles = 0;
            }

            if (_skCNavStuckFails > 0 && !_skCNavStuckPending)
            {
                _skCNavStuckFails = 0;
            }
        }

        switch (_skCNavPhase)
        {
            case SkCPhaseNav402:
                if (floor == 4400)
                {
                    _skCNavPhase = SkCPhaseNav4400;
                    ResetSkCStuck();
                    _skCNavNote = "已切4400，去(106,54)";
                    SkCIssueNav(4400, SkCWarp4400X, SkCWarp4400Y, true);
                    return;
                }

                if (floor == 4403)
                {
                    _skCNavPhase = SkCPhaseNav4403;
                    ResetSkCStuck();
                    _skCNavNote = "已切4403，走到(26,38)";
                    TryWalkTo(SkCWallStandX, SkCWallStandY);
                    _skCNavLastNavMs = now;
                    return;
                }

                SkCEnsureNav(402, SkCWarp402X, SkCWarp402Y, now);
                _skCNavNote = "402(118,100) 图" + floor + " 现" + x + "," + y;
                break;

            case SkCPhaseNav4400:
                if (floor == 4403)
                {
                    _skCNavPhase = SkCPhaseNav4403;
                    ResetSkCStuck();
                    _skCNavNote = "已切4403，走到(26,38)";
                    TryWalkTo(SkCWallStandX, SkCWallStandY);
                    _skCNavLastNavMs = now;
                    return;
                }

                if (floor == 4404)
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "已进4404，去(70,8)";
                    return;
                }

                SkCEnsureNav(4400, SkCWarp4400X, SkCWarp4400Y, now);
                _skCNavNote = "4400(106,54) 图" + floor + " 现" + x + "," + y;
                break;

            case SkCPhaseNav4403:
                if (floor == 4404)
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "已进4404，去(70,8)";
                    return;
                }

                if (floor == 4403 && SkCPassedWall(x, y))
                {
                    SkCEnterAfterWall("4403-already-east");
                    return;
                }

                if (floor == 4403 && SkCAtWallStand(x, y))
                {
                    _skCNavPhase = SkCPhaseTalkWall;
                    _skCNavSawDialogue = false;
                    _skCNavNote = "已到(26,38)，点(28,38)墙壁";
                    return;
                }

                if (floor == 4403)
                {
                    if (!IsWalkSystemPathBusy()
                        && (_skCNavLastNavMs <= 0 || now - _skCNavLastNavMs >= LingTangNavRetryMs))
                    {
                        TryWalkTo(SkCWallStandX, SkCWallStandY);
                        _skCNavLastNavMs = now;
                    }

                    _skCNavNote = "4403走到(26,38) 现" + x + "," + y;
                    break;
                }

                SkCEnsureNav(4403, SkCWallStandX, SkCWallStandY, now);
                _skCNavNote = "去4403(26,38) 图" + floor + " 现" + x + "," + y;
                break;

            case SkCPhaseTalkWall:
                if (floor == 4404)
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "已进4404，去(70,8)";
                    return;
                }

                if (floor != 4403)
                {
                    _skCNavPhase = SkCPhaseNav4403;
                    break;
                }

                if (SkCPassedWall(x, y))
                {
                    SkCEnterAfterWall("talk-passed-wall");
                    return;
                }

                if (_skCNavSawDialogue && !dialogueOpen)
                {
                    _skCNavNote = "对话结束，等过墙 现" + x + "," + y;
                    break;
                }

                if (!SkCAtWallStand(x, y) && !SkCAtWallNpc(x, y))
                {
                    if (!IsWalkSystemPathBusy()
                        && (_skCNavLastNavMs <= 0 || now - _skCNavLastNavMs >= LingTangNavRetryMs))
                    {
                        TryWalkTo(SkCWallStandX, SkCWallStandY);
                        _skCNavLastNavMs = now;
                    }

                    _skCNavNote = "回到(26,38)再对话 现" + x + "," + y;
                    break;
                }

                if (!dialogueOpen && now - _skCNavLastNpcMs >= LingTangNpcRetryMs)
                {
                    _skCNavLastNpcMs = now;
                    if (TryLookNpcAt(SkCWallNpcX, SkCWallNpcY))
                    {
                        _skCNavLastActivityMs = now;
                        _skCNavNote = "已点墙壁(28,38)";
                    }
                    else
                    {
                        var obj = FindNpcObjIndexByName(SkCWallNpcName, "快要崩裂");
                        if (obj >= 0 && TryLookNpcByObj(obj))
                        {
                            _skCNavLastActivityMs = now;
                            _skCNavNote = "格子未中，按名点墙壁 obj=" + obj;
                        }
                        else
                        {
                            _skCNavNote = "未找到(28,38)快要崩裂的墙壁";
                        }
                    }
                }

                break;

            case SkCPhaseNavVia:
                if (floor == 4404)
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "已进4404，去(70,8)";
                    return;
                }

                if (floor != 4403)
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "过墙后离图，改导航4404(70,8)";
                    SkCIssueNav(4404, 70, 8, true);
                    return;
                }

                if (SkCNear(x, y, SkCViaX, SkCViaY))
                {
                    _skCNavPhase = SkCPhaseNav4404;
                    ResetSkCStuck();
                    _skCNavNote = "(41,39)到了，导航去4404(70,8)";
                    SkCIssueNav(4404, 70, 8, true);
                    return;
                }

                SkCKeepAfterWallNav(now);
                _skCNavNote = "过墙后导航(41,39) 现" + x + "," + y;
                break;

            case SkCPhaseNav4404:
                if (floor == 4404 && SkCNear(x, y, 70, 8))
                {
                    StopSkCNav("已到达 图" + floor);
                    return;
                }

                SkCEnsureNav(4404, 70, 8, now);
                _skCNavNote = "4404(70,8) 图" + floor;
                break;
        }

        if (dialogueOpen || inBattle)
        {
            return;
        }

        TickSkCNavStuck(now, floor, x, y);
    }

    private static bool SkCNear(int x, int y, int tx, int ty)
    {
        return Math.Abs(x - tx) + Math.Abs(y - ty) <= 1;
    }

    private static bool SkCAtWallStand(int x, int y)
    {
        return x == SkCWallStandX && y == SkCWallStandY;
    }

    private static bool SkCAtWallNpc(int x, int y)
    {
        return x == SkCWallNpcX && y == SkCWallNpcY;
    }

    /// <summary>墙在 x=28。点墙传送后落在墙东（约 31,38），禁止再往西走回 (26,38)。</summary>
    private static bool SkCPassedWall(int x, int y)
    {
        return x >= 30;
    }

    private static bool IsSkCResumeFloor(int floor)
    {
        return floor == SkCStopEscortFloor || floor == 4400 || floor == 4403 || floor == 4404;
    }

    private static bool IsSkCArriveFloor(int floor)
    {
        return floor == SkCStopEscortFloor || floor == 4403 || floor == 4404;
    }

    private static void ResetSkCStuck()
    {
        _skCNavStuckFails = 0;
        _skCNavStuckShuffles = 0;
        _skCNavStuckNavTriedFirst = false;
        _skCNavStuckPending = false;
        _skCNavLastActivityMs = NowMs();
    }

    private static void SkCEnsureNav(int floor, int x, int y, long now)
    {
        if (_skCNavOwnsEscort && KeepOfficialPathAfterMap("skc-ensure"))
        {
            _skCNavLastActivityMs = now;
            return;
        }

        // 官方 WalkSystem 还在走 / 等过图：不要再发 GeneralPointMoveTo（会停路再开）。
        if (IsWalkSystemPathBusy())
        {
            return;
        }

        if (_skCNavLastNavMs > 0 && now - _skCNavLastNavMs < LingTangNavRetryMs)
        {
            return;
        }

        SkCIssueNav(floor, x, y, false);
    }

    /// <summary>
    /// 点墙传送后：本图 GeneralPointMoveTo(4403, 41, 39)。不是任务导航，不要 Abort / RunTask。
    /// </summary>
    private static void SkCEnterAfterWall(string reason)
    {
        _skCNavPhase = SkCPhaseNavVia;
        ResetSkCStuck();
        _skCNavNote = "过墙后导航(41,39)";
        SkCIssueAfterWallNav(reason, true);
    }

    private static void SkCIssueAfterWallNav(string reason, bool force)
    {
        var now = NowMs();
        if (!force && _skCNavLastNavMs > 0 && now - _skCNavLastNavMs < LingTangNavRetryMs)
        {
            return;
        }

        string how;
        var ok = TryNavigateTo(4403, SkCViaX, SkCViaY, out how);
        _skCNavLastNavMs = now;
        _skCNavLastActivityMs = now;
        WriteLog("skc after-wall (41,39) ok=" + ok + " how=" + how + " reason=" + reason);
    }

    private static void SkCKeepAfterWallNav(long now)
    {
        if (IsMapLoading())
        {
            _skCNavLastActivityMs = now;
            return;
        }

        // 点墙是同图传送，WalkSystem 常停在等过图；不能因此不发 (41,39) 导航。
        if (IsWalkSystemPathBusy() && !IsWalkWaitingMap())
        {
            return;
        }

        SkCIssueAfterWallNav("keep", false);
    }

    private static void SkCIssueNav(int floor, int x, int y, bool force)
    {
        var now = NowMs();
        if (!force && _skCNavLastNavMs > 0 && now - _skCNavLastNavMs < LingTangNavRetryMs)
        {
            return;
        }

        string how;
        if (TryNavigateTo(floor, x, y, out how))
        {
            _skCNavLastNavMs = now;
            WriteLog("skc nav " + floor + " (" + x + "," + y + ") " + how);
        }
        else
        {
            _skCNavLastNavMs = now;
            WriteLog("skc nav fail " + floor + " (" + x + "," + y + ") " + how);
        }
    }

    private static void SkCReissueCurrentNav(bool force)
    {
        int reFloor;
        int reX;
        int reY;
        TryGetCurrentMapInfo(out reFloor, out _, out _);
        TryGetPlayerXY(out reX, out reY);
        switch (_skCNavPhase)
        {
            case SkCPhaseNav402:
                SkCIssueNav(402, SkCWarp402X, SkCWarp402Y, force);
                break;
            case SkCPhaseNav4400:
                SkCIssueNav(4400, SkCWarp4400X, SkCWarp4400Y, force);
                break;
            case SkCPhaseNav4403:
                if (reFloor == 4403)
                {
                    if (SkCPassedWall(reX, reY))
                    {
                        SkCEnterAfterWall("stuck-east");
                    }
                    else
                    {
                        TryWalkTo(SkCWallStandX, SkCWallStandY);
                        _skCNavLastNavMs = NowMs();
                    }
                }
                else
                {
                    SkCIssueNav(4403, SkCWallStandX, SkCWallStandY, force);
                }
                break;
            case SkCPhaseTalkWall:
                if (reFloor == 4403 && SkCPassedWall(reX, reY))
                {
                    SkCEnterAfterWall("stuck-talk-east");
                }
                else if (reFloor == 4403)
                {
                    TryWalkTo(SkCWallStandX, SkCWallStandY);
                    _skCNavLastNavMs = NowMs();
                }
                else
                {
                    SkCIssueNav(4403, SkCWallStandX, SkCWallStandY, force);
                }
                break;
            case SkCPhaseNavVia:
                SkCIssueAfterWallNav("stuck-reissue", true);
                break;
            case SkCPhaseNav4404:
                SkCIssueNav(4404, 70, 8, force);
                break;
        }
    }

    private static void TickSkCNavStuck(long now, int floor, int x, int y)
    {
        if (_skCNavOwnsEscort && KeepOfficialPathAfterMap("skc-stuck"))
        {
            _skCNavLastActivityMs = now;
            return;
        }

        if (_skCNavStuckPending)
        {
            if (now - _skCNavStuckMoveAtMs >= StuckResumeDelayMs)
            {
                _skCNavStuckPending = false;
                _skCNavLastActivityMs = now;
                SkCReissueCurrentNav(true);
            }

            return;
        }

        if (now - _skCNavLastActivityMs < StuckIdleMs)
        {
            return;
        }

        _skCNavStuckFails++;
        WriteLog("skc stuck fail=" + _skCNavStuckFails + "/" + LingTangMaxStuckFails
                 + " phase=" + _skCNavPhase + " floor=" + floor + " xy=" + x + "," + y);
        if (_skCNavStuckFails >= LingTangMaxStuckFails)
        {
            StopSkCNav("卡图恢复失败，已停止");
            return;
        }

        if (!_skCNavStuckNavTriedFirst)
        {
            _skCNavStuckNavTriedFirst = true;
            _skCNavLastActivityMs = now;
            Tip("骷髅战士导航：卡图，先继续导航");
            SkCReissueCurrentNav(true);
            return;
        }

        if (_skCNavStuckShuffles >= StuckShuffleBeforeNavRetry)
        {
            _skCNavStuckShuffles = 0;
            _skCNavLastActivityMs = now;
            Tip("骷髅战士导航：已挪格，继续导航");
            SkCReissueCurrentNav(true);
            return;
        }

        if (TryRandomStepOne())
        {
            _skCNavStuckShuffles++;
            _skCNavStuckMoveAtMs = now;
            _skCNavStuckPending = true;
            _skCNavLastActivityMs = now;
            Tip("骷髅战士导航：卡图，挪格后继续导航");
        }
        else
        {
            _skCNavLastActivityMs = now;
            SkCReissueCurrentNav(true);
        }
    }

    private static bool TryLookNpcByObj(int objindex)
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null || objindex < 0)
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
                return false;
            }

            look.Invoke(npcMgr, new object[] { dir, objindex });
            WriteLog("skc SendLookNpc obj=" + objindex);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("skc LookNpc EX " + RootMessage(ex));
            return false;
        }
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

        if (IsMapLoading())
        {
            _lingTangLastActivityMs = now;
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
            if (KeepOfficialPathAfterMap("lingtang-stuck"))
            {
                _lingTangLastActivityMs = now;
                return;
            }

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
        if (KeepOfficialPathAfterMap("lingtang-ensure"))
        {
            _lingTangLastActivityMs = now;
            return;
        }

        if (IsWalkSystemPathBusy())
        {
            return;
        }

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

        var hint2 = CreateUiChild(_bodyRoot, "Hint2", rtType);
        SetAnchoredTop(RequireRect(hint2, "ha2"), 0f, -8f, 500f, 72f);
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
            + "战后若队伍解散或不足5人：自动暂停，组好后点「继续护航」。",
            11);

        var y = -84f;
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
        var zyLoopBtn = CreateUiChild(_bodyRoot, "ZhongyuanLoopBtn", rtType);
        SetAnchoredTop(RequireRect(zyLoopBtn, "zylb"), 0f, y, 420f, 40f);
        var zyLoopImg = AddComp(zyLoopBtn, "UnityEngine.UI.Image");
        SetColor(zyLoopImg, _zyLoopActive ? 0.42f : 0.28f, _zyLoopActive ? 0.28f : 0.34f,
            _zyLoopActive ? 0.22f : 0.48f, 1f);
        var zyLoopLab = CreateUiChild(zyLoopBtn, "L", rtType);
        StretchFull(RequireRect(zyLoopLab, "zyll"));
        SetText(AddText(zyLoopLab), _zyLoopActive ? "停止中元循环" : "中元循环", 14);
        BindButton(zyLoopBtn, zyLoopImg, ToggleZhongyuanLoop);
        y -= 48f;

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
            var send = FindSendBackPackMessage(itemMgr);
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

    private static MethodInfo FindSendBackPackMessage(object itemMgr)
    {
        if (itemMgr == null)
        {
            return null;
        }

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
                return m;
            }
        }

        return null;
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

    /// <summary>检查每个队员是否都至少有 1 个宠物空位（宠物栏最多 5 槽）。</summary>
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
            if (!IsPetBarHasFreeSlot(getPets, uid, out var used, out var capSlots))
            {
                failName = uid;
                WriteLog("dragon pet full uid=" + uid + " used=" + used + "/" + capSlots);
                return false;
            }
        }

        return true;
    }

    private static int CountTeamUsedPetSlots()
    {
        var total = 0;
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
            return 0;
        }

        foreach (var uid in uids)
        {
            CountUsedPetSlots(getPets, uid, out var used, out _);
            total += used;
        }

        return total;
    }

    private static void CountUsedPetSlots(MethodInfo getPets, string uid, out int used, out int capSlots)
    {
        used = 0;
        capSlots = 5;
        try
        {
            var pets = getPets.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (pets == null)
            {
                return;
            }

            capSlots = Math.Max(1, Math.Min(5, pets.Count));
            for (var i = 0; i < pets.Count && i < 5; i++)
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
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsPetBarHasFreeSlot(MethodInfo getPetsMethod, string uid, out int used, out int capSlots)
    {
        used = 0;
        capSlots = 5;
        try
        {
            var pets = getPetsMethod.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (pets == null)
            {
                return false;
            }

            capSlots = Math.Max(1, Math.Min(5, pets.Count));
            for (var i = 0; i < pets.Count && i < 5; i++)
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

            return used < capSlots;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPetBarFull(System.Collections.IList pets)
    {
        if (pets == null || pets.Count == 0)
        {
            return false;
        }

        var used = 0;
        var capSlots = Math.Max(1, Math.Min(5, pets.Count));
        for (var i = 0; i < pets.Count && i < 5; i++)
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

        return used >= capSlots;
    }

    /// <summary>关掉银行/宠物仓库面板。GetUIPanel 有重载，不能用 GetMethod 单名查找。</summary>
    private static void TryDismissBankUiAfterStore()
    {
        try
        {
            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr != null)
            {
                SetMember(roleMgr, "OpenBankFromPet", false);
            }

            foreach (var panelName in new[] { "BankPanel", "PetBankPanel", "RemoteBankPanel", "PetStoragePanel" })
            {
                TryCloseUiPanel(panelName);
            }

            ClearOpenBankPetLists();
        }
        catch (Exception ex)
        {
            WriteLog("dismiss bank ui EX " + RootMessage(ex));
        }
    }

    private static bool TryCloseUiPanel(string panelTypeName)
    {
        try
        {
            var panel = GetUiPanel(panelTypeName);
            if (panel == null)
            {
                return false;
            }

            MethodInfo close = null;
            for (var t = panel.GetType(); t != null; t = t.BaseType)
            {
                close = t.GetMethod(
                    "Close",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (close != null)
                {
                    break;
                }
            }

            if (close == null)
            {
                close = panel.GetType().GetMethod(
                    "Close",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
            }

            if (close != null)
            {
                close.Invoke(panel, null);
            }

            TryHideUiPanelFallback(panel);
            WriteLog("close panel " + panelTypeName + (close == null ? " no-Close" : ""));
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("close panel EX " + panelTypeName + " " + RootMessage(ex));
            return false;
        }
    }

    private static void TryHideUiPanelFallback(object panel)
    {
        if (panel == null)
        {
            return;
        }

        try
        {
            MethodInfo hide = null;
            for (var t = panel.GetType(); t != null; t = t.BaseType)
            {
                hide = t.GetMethod(
                    "Hide",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (hide != null)
                {
                    break;
                }
            }

            hide?.Invoke(panel, null);
        }
        catch
        {
            // ignore
        }

        try
        {
            var go = GetProp(panel, "gameObject") ?? GetMember(panel, "gameObject");
            if (go == null)
            {
                return;
            }

            var setActive = go.GetType().GetMethod("SetActive", new[] { typeof(bool) });
            setActive?.Invoke(go, new object[] { false });
        }
        catch
        {
            // ignore
        }
    }

    private static string GetWildExPetNameForSlot(int slot)
    {
        if (slot == ZhongyuanSlotPetA)
        {
            return ZhongyuanPetA;
        }

        if (slot == ZhongyuanSlotPetB)
        {
            return ZhongyuanPetB;
        }

        if (slot == ZhongyuanSlotPetC)
        {
            return ZhongyuanPetC;
        }

        return "";
    }

    private static string FormatWildExchangeStatus()
    {
        if (_zyLoopActive)
        {
            var round = _zyLoopRound > 0 ? "第" + _zyLoopRound + "轮 " : "";
            var extra = _wildExActive ? ("\n兑换 " + WildExPhaseName(_wildExPhase) + " " + (_wildExNote ?? "")) : "";
            return "中元循环: " + round + ZhongyuanLoopPhaseName()
                   + "\n" + (_zyLoopNote ?? "") + extra;
        }

        if (!_wildExActive)
        {
            return "中元循环: 未启动（仓检→抓三种→兑换）\n单独「兑换野生宠」：只兑换，不抓宠";
        }

        var exRound = _wildExRound > 0 ? "第" + _wildExRound + "轮后 " : "";
        return "兑换野生宠: " + exRound + WildExPhaseName(_wildExPhase)
               + "\n" + (_wildExNote ?? "");
    }

    private static string WildExPhaseName(int phase)
    {
        switch (phase)
        {
            case WildExReturn:
                return "回城点2";
            case WildExNav:
                return "导航1500(47,75)";
            case WildExFindNpc:
                return "找中元使者";
            case WildExScanDestOpen:
            case WildExScanDestWait:
                return "仓检2/3/4个人仓";
            case WildExMemOpenPersonal:
            case WildExMemWaitPersonal:
            case WildExMemTake:
                return "234个人仓取1只";
            case WildExMemOpenAccount:
            case WildExMemWaitAccount:
            case WildExMemStore:
                return "存入账号仓";
            case WildExCapOpenAccount:
            case WildExCapWaitAccount:
            case WildExCapTake:
                return "1号从账号仓取3只";
            case WildExLookNpc:
                return "点中元使者";
            case WildExPickOption:
                return "选第2项";
            case WildExConfirm:
                return "点确定交宠";
            case WildExWaitDone:
                return "等交宠完成";
            case WildExStoreTicket:
                return "存中元礼盒兑换券";
            default:
                return "准备";
        }
    }

    private static void WildExSay(string msg, bool tip)
    {
        _wildExNote = msg ?? "";
        WriteLog("wild-ex " + (msg ?? ""));
        if (tip)
        {
            Tip("兑换野生宠：" + msg);
            _wildExLastTipMs = NowMs();
        }
    }

    private static void WildExSayWait(string msg, long now)
    {
        _wildExNote = msg ?? "";
        if (_wildExLastTipMs <= 0 || now - _wildExLastTipMs >= 2500)
        {
            WildExSay(msg, true);
        }
    }

    private static void WildExOpenBank(string uid, bool account)
    {
        TryDismissBankUiAfterStore();
        MarkPendingBankOpen(uid, account);
        _wildExInfoBefore = TryGetPetStorageInfo();
        _wildExStoreBefore = TryGetBankStorePetInfo();
        var openUid = uid;
        if (account)
        {
            var captainUid = GetMainPlayerUidSafe();
            if (!string.IsNullOrEmpty(captainUid))
            {
                openUid = captainUid;
            }

            TryOpenRemoteAccountPetBank(openUid);
        }
        else
        {
            TryOpenRemotePersonalPetBank(uid);
        }

        _wildExWaitListStartMs = NowMs();
        WriteLog("wild-ex OpenBank account=" + account
                 + " uid尾" + TailUid(uid)
                 + " openUid尾" + TailUid(openUid));
    }

    private static void WildExRetryAccountOpen(long now, int reopenPhase)
    {
        TryDismissBankUiAfterStore();
        _wildExAccountOpenTries++;
        if (_wildExAccountOpenTries >= WildExAccountOpenMaxTries)
        {
            StopWildExchange("account-timeout");
            Tip("打开账号宠物仓库失败");
            return;
        }

        WildExSay("账号仓未开，重开 " + _wildExAccountOpenTries + "/" + WildExAccountOpenMaxTries, true);
        WriteLog("wild-ex retry account open try=" + _wildExAccountOpenTries);
        _wildExPhase = reopenPhase;
        _wildExDelayUntilMs = now + 800;
    }

    private static bool TryCollectOpenBankZhongyuan(
        out int[] counts, out List<int>[] indexes, out int total)
    {
        counts = new int[3];
        indexes = new List<int>[3];
        total = 0;
        var any = false;
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            List<int> matching;
            int t;
            if (!TryCollectOpenBankPets(
                    WildPetPresets[i], _wildExInfoBefore, _wildExStoreBefore, out matching, out t,
                    true))
            {
                continue;
            }

            any = true;
            indexes[i] = matching ?? new List<int>();
            counts[i] = indexes[i].Count;
            total = t;
        }

        return any;
    }

    private static void ToggleWildExchange()
    {
        if (_zyLoopActive)
        {
            Tip("请先停中元循环");
            return;
        }

        if (_wildExActive)
        {
            StopWildExchange("已手动停止");
            Tip("兑换野生宠已关闭");
            return;
        }

        StartWildExchange();
    }

    private static void StartWildExchange()
    {
        if (_zyCatchActive || _zyXferActive || _zyAllActive || _skCNavActive)
        {
            Tip("请先停中元抓齐/倒腾");
            return;
        }

        if (_zyLoopActive && !_wildExInLoop)
        {
            Tip("请先停中元循环");
            return;
        }

        if (IsInBattleNow())
        {
            Tip("战斗中不能兑换");
            return;
        }

        var uid = GetMainPlayerUidSafe();
        if (string.IsNullOrEmpty(uid))
        {
            Tip("未找到角色");
            return;
        }

        var local = GetLocalTeamSlot();
        if (local < 0)
        {
            Tip("未找到角色");
            return;
        }

        if (local > 0)
        {
            Tip("兑换野生宠请用1号点，不要切号");
            return;
        }

        for (var i = 0; i < _wildExDestHave.Length; i++)
        {
            _wildExDestHave[i] = 0;
            _wildExDestTotal[i] = 0;
        }

        TrySendLocalAutoBattle("停止挂机");
        StopTaskNavigation(false);
        _wildExActive = true;
        _wildExDelayUntilMs = 0;
        _wildExWaitListStartMs = 0;
        _wildExLastTipMs = 0;
        _wildExActionAtMs = 0;
        _wildExLastLookMs = 0;
        _wildExStepTries = 0;
        _wildExRound = 0;
        _wildExNpcObj = -1;
        _wildExNpcFoundX = 0;
        _wildExNpcFoundY = 0;
        _wildExPetsBeforeTalk = 0;
        _wildExBankIndexes.Clear();
        _wildExTakePos = 0;
        _wildExAccountOpenTries = 0;
        _wildExScanIndex = 0;
        _wildExInfoBefore = null;
        _wildExStoreBefore = null;
        _wildExTargetName = "";
        _wildExWorkUid = "";
        _wildExPhase = WildExReturn;
        WildExSay("回城点2", true);
        WriteLog("wild-ex start captain");
        RefreshScriptTabIfVisible();
    }

    private static void StopWildExchange(string reason)
    {
        if (!_wildExActive && _wildExPhase == WildExIdle)
        {
            return;
        }

        _wildExActive = false;
        _wildExPhase = WildExIdle;
        _wildExDelayUntilMs = 0;
        _wildExWaitListStartMs = 0;
        _wildExBankIndexes.Clear();
        _wildExTakePos = 0;
        _wildExNote = reason ?? "";
        TryDismissBankUiAfterStore();
        WriteLog("wild-ex stop " + reason);
        var looping = _wildExInLoop;
        _wildExInLoop = false;
        RefreshScriptTabIfVisible();
        if (looping && _zyLoopActive && reason == "done")
        {
            RestartZhongyuanLoopScan();
            return;
        }

        if (looping && _zyLoopActive && reason != "loop-stop")
        {
            StopZhongyuanLoop("兑换中断：" + reason);
        }
    }

    private static void TickWildExchange()
    {
        if (!_wildExActive)
        {
            return;
        }

        var now = NowMs();
        if (_wildExDelayUntilMs > 0 && now < _wildExDelayUntilMs)
        {
            return;
        }

        _wildExDelayUntilMs = 0;
        if (IsInBattleNow())
        {
            WildExSayWait("兑换暂停：战斗中", now);
            return;
        }

        if (IsMapLoading())
        {
            WildExSayWait("兑换暂停：过图中", now);
            return;
        }

        var uid = GetMainPlayerUidSafe();
        if (string.IsNullOrEmpty(uid))
        {
            StopWildExchange("no-uid");
            Tip("未找到角色");
            return;
        }

        switch (_wildExPhase)
        {
            case WildExReturn:
                TickWildExReturn(now);
                break;
            case WildExNav:
                TickWildExNav(now);
                break;
            case WildExFindNpc:
                TickWildExFindNpc(now);
                break;
            case WildExScanDestOpen:
                TickWildExScanDestOpen(now);
                break;
            case WildExScanDestWait:
                TickWildExScanDestWait(now);
                break;
            case WildExMemOpenPersonal:
                TickWildExMemOpenPersonal(now);
                break;
            case WildExMemWaitPersonal:
                TickWildExMemWaitPersonal(now);
                break;
            case WildExMemTake:
                TickWildExMemTake(now);
                break;
            case WildExMemOpenAccount:
                TickWildExMemOpenAccount(now);
                break;
            case WildExMemWaitAccount:
                TickWildExMemWaitAccount(now);
                break;
            case WildExMemStore:
                TickWildExMemStore(now);
                break;
            case WildExCapOpenAccount:
                StopTaskNavigation(false);
                WildExOpenBank(uid, true);
                _wildExPhase = WildExCapWaitAccount;
                WildExSay("开账号仓取3只", false);
                _wildExDelayUntilMs = now + 400;
                break;
            case WildExCapWaitAccount:
                TickWildExCapWaitAccount(uid, now);
                break;
            case WildExCapTake:
                TickWildExCapTake(uid, now);
                break;
            case WildExLookNpc:
                TickWildExLookNpc(now);
                break;
            case WildExPickOption:
                TickWildExPickOption(now);
                break;
            case WildExConfirm:
                TickWildExConfirm(uid, now);
                break;
            case WildExWaitDone:
                TickWildExWaitDone(uid, now);
                break;
            case WildExStoreTicket:
                TickWildExStoreTicket(uid, now);
                break;
        }
    }

    private static void TickWildExReturn(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        int x;
        int y;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        TryGetPlayerXY(out x, out y);
        if (floor == FloraHealReturnFloor)
        {
            _wildExStepTries = 0;
            _wildExActionAtMs = 0;
            _wildExPhase = WildExNav;
            WildExSay("已回城，导航 1500 (47,75)", true);
            _wildExDelayUntilMs = now + 1000;
            return;
        }

        if (_wildExActionAtMs > 0 && now - _wildExActionAtMs < WildExReturnWaitMs)
        {
            WildExSayWait("等待回城点2 " + _wildExStepTries + "/" + WildExMaxTries, now);
            return;
        }

        if (_wildExStepTries >= WildExMaxTries)
        {
            StopWildExchange("return-fail");
            Tip("回城点2未到位");
            return;
        }

        _wildExStepTries++;
        _wildExActionAtMs = now;
        if (!FloraHealSendReturnCity())
        {
            WildExSay("回城发包失败 " + _wildExStepTries + "/" + WildExMaxTries, true);
            return;
        }

        WildExSay("已发回城点2 " + _wildExStepTries + "/" + WildExMaxTries, true);
    }

    private static bool IsAtWildExStand(int floor, int x, int y)
    {
        return floor == WildExNpcFloor && x == WildExNpcStandX && y == WildExNpcStandY;
    }

    private static void TickWildExNav(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        int x;
        int y;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        TryGetPlayerXY(out x, out y);
        if (IsAtWildExStand(floor, x, y))
        {
            _wildExStepTries = 0;
            _wildExActionAtMs = 0;
            _wildExPhase = WildExFindNpc;
            WildExSay("已到 1500 (47,75)，找中元使者", true);
            return;
        }

        if (_wildExActionAtMs > 0 && now - _wildExActionAtMs < WildExNavWaitMs)
        {
            WildExSayWait("导航 1500 (47,75) 当前 " + floor + " (" + x + "," + y + ") "
                          + _wildExStepTries + "/" + WildExNavMaxTries, now);
            return;
        }

        if (_wildExStepTries >= WildExNavMaxTries)
        {
            StopWildExchange("nav-fail");
            Tip("导航 1500 (47,75) 未到位，当前 " + floor + " (" + x + "," + y + ")");
            return;
        }

        _wildExStepTries++;
        _wildExActionAtMs = now;
        if (floor != WildExNpcFloor)
        {
            StopTaskNavigation(false);
        }

        string how;
        if (!TryNavigateTo(WildExNpcFloor, WildExNpcStandX, WildExNpcStandY, out how))
        {
            WildExSay("导航失败 " + how + " " + _wildExStepTries + "/" + WildExNavMaxTries, true);
            return;
        }

        WildExSay("已发导航 1500 (47,75) " + how + " 当前 " + floor + " (" + x + "," + y + ") "
                  + _wildExStepTries + "/" + WildExNavMaxTries, true);
    }

    private static void TickWildExFindNpc(long now)
    {
        int obj;
        int nx;
        int ny;
        if (TryFindWildExNpc(out obj, out nx, out ny))
        {
            _wildExNpcObj = obj;
            _wildExNpcFoundX = nx;
            _wildExNpcFoundY = ny;
            _wildExStepTries = 0;
            _wildExScanIndex = 0;
            _wildExPhase = WildExScanDestOpen;
            WildExSay("找到中元使者 (" + nx + "," + ny + ")，开始仓检", true);
            return;
        }

        if (_wildExLastLookMs > 0 && now - _wildExLastLookMs < WildExNpcLookRetryMs)
        {
            WildExSayWait("查找中元使者", now);
            return;
        }

        _wildExLastLookMs = now;
        _wildExStepTries++;
        if (_wildExStepTries > WildExMaxTries)
        {
            StopWildExchange("npc-miss");
            Tip("找不到中元使者");
            return;
        }

        WildExSay("没找到中元使者，重试 " + _wildExStepTries + "/" + WildExMaxTries, true);
    }

    private static void TickWildExScanDestOpen(long now)
    {
        if (_wildExScanIndex < 0 || _wildExScanIndex >= WildPetPresets.Length)
        {
            TickWildExFinishScan();
            return;
        }

        var name = WildPetPresets[_wildExScanIndex];
        var destSlot = GetZhongyuanDestSlot(name);
        var destUid = GetTeamUidBySlot(destSlot);
        if (string.IsNullOrEmpty(destUid))
        {
            StopWildExchange("no-teammate");
            Tip("兑换仓检不通过：队伍里没有" + FormatTeamSlotLabel(destSlot));
            return;
        }

        StopTaskNavigation(false);
        WildExOpenBank(destUid, false);
        _wildExPhase = WildExScanDestWait;
        WildExSay("仓检" + FormatTeamSlotLabel(destSlot) + name + "，已发开个人仓 uid尾"
                  + TailUid(destUid), true);
        _wildExDelayUntilMs = now + 400;
    }

    private static void TickWildExScanDestWait(long now)
    {
        var name = WildPetPresets[_wildExScanIndex];
        var destSlot = GetZhongyuanDestSlot(name);
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(name, _wildExInfoBefore, _wildExStoreBefore, out matching, out total))
        {
            var elapsed = now - _wildExWaitListStartMs;
            if (elapsed >= WildExWaitListTimeoutMs)
            {
                _wildExDestHave[_wildExScanIndex] = 0;
                _wildExDestTotal[_wildExScanIndex] = 0;
                WildExSay("仓检" + FormatTeamSlotLabel(destSlot) + name + " 个人仓超时，按0只计", true);
                TryDismissBankUiAfterStore();
                _wildExScanIndex++;
                _wildExPhase = WildExScanDestOpen;
                return;
            }

            WildExSayWait("仓检" + FormatTeamSlotLabel(destSlot) + name
                          + " 等仓库列表 " + (elapsed / 1000) + "秒", now);
            return;
        }

        _wildExDestHave[_wildExScanIndex] = matching == null ? 0 : matching.Count;
        _wildExDestTotal[_wildExScanIndex] = total;
        WildExSay("仓检" + FormatTeamSlotLabel(destSlot) + name
                  + " 个人仓" + _wildExDestHave[_wildExScanIndex] + "/" + total + "只", true);
        TryDismissBankUiAfterStore();
        _wildExScanIndex++;
        _wildExPhase = WildExScanDestOpen;
    }

    private static void TickWildExFinishScan()
    {
        TryDismissBankUiAfterStore();
        var parts = new List<string>();
        var missing = new List<string>();
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            var name = WildPetPresets[i];
            var dest = GetZhongyuanDestSlot(name);
            var have = _wildExDestHave[i];
            parts.Add(FormatTeamSlotLabel(dest) + name + have + "只");
            if (have <= 0)
            {
                missing.Add(FormatTeamSlotLabel(dest) + "个人仓没有" + name);
            }
        }

        var summary = string.Join(" ", parts.ToArray());
        if (missing.Count > 0)
        {
            WildExSay(summary + "。凑不齐一套，去存券", true);
            _wildExStepTries = 0;
            _wildExPhase = WildExStoreTicket;
            _wildExDelayUntilMs = NowMs() + WildExProtocolGapMs;
            return;
        }

        _wildExScanIndex = 0;
        _wildExPhase = WildExMemOpenPersonal;
        WildExSay(summary + "。从2号个人仓取1只幽灵", true);
    }

    private static string WildExCurrentDestUid()
    {
        if (_wildExScanIndex < 0 || _wildExScanIndex >= WildPetPresets.Length)
        {
            return "";
        }

        return GetTeamUidBySlot(GetZhongyuanDestSlot(WildPetPresets[_wildExScanIndex]));
    }

    private static void TickWildExMemOpenPersonal(long now)
    {
        if (_wildExScanIndex < 0 || _wildExScanIndex >= WildPetPresets.Length)
        {
            _wildExAccountOpenTries = 0;
            _wildExPhase = WildExCapOpenAccount;
            return;
        }

        var name = WildPetPresets[_wildExScanIndex];
        var destUid = WildExCurrentDestUid();
        if (string.IsNullOrEmpty(destUid))
        {
            StopWildExchange("no-teammate");
            Tip("队伍里没有" + FormatTeamSlotLabel(GetZhongyuanDestSlot(name)));
            return;
        }

        _wildExTargetName = name;
        _wildExWorkUid = destUid;
        StopTaskNavigation(false);
        WildExOpenBank(destUid, false);
        _wildExPhase = WildExMemWaitPersonal;
        WildExSay(FormatTeamSlotLabel(GetZhongyuanDestSlot(name)) + "开个人仓取1只" + name, true);
        _wildExDelayUntilMs = now + 400;
    }

    private static void TickWildExMemWaitPersonal(long now)
    {
        var destUid = _wildExWorkUid;
        var name = _wildExTargetName;
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(name, _wildExInfoBefore, _wildExStoreBefore, out matching, out total))
        {
            if (now - _wildExWaitListStartMs >= WildExWaitListTimeoutMs)
            {
                TryDismissBankUiAfterStore();
                StopWildExchange("personal-timeout");
                Tip("打开" + FormatTeamSlotLabel(GetZhongyuanDestSlot(name)) + "个人仓失败");
            }

            return;
        }

        if (matching == null || matching.Count <= 0)
        {
            TryDismissBankUiAfterStore();
            WildExSay(FormatTeamSlotLabel(GetZhongyuanDestSlot(name)) + "个人仓没有" + name + "，去存券", true);
            _wildExStepTries = 0;
            _wildExPhase = WildExStoreTicket;
            return;
        }

        var free = CountLocalPetFreeSlots(destUid);
        if (free <= 0)
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("no-slot");
            Tip(FormatTeamSlotLabel(GetZhongyuanDestSlot(name)) + "身上没有空位，无法取宠");
            return;
        }

        _wildExBankIndexes.Clear();
        _wildExBankIndexes.Add(matching[0]);
        _wildExTakePos = 0;
        _wildExPhase = WildExMemTake;
        WildExSay("个人仓取出1只" + name, false);
    }

    private static void TickWildExMemTake(long now)
    {
        if (_wildExTakePos >= _wildExBankIndexes.Count)
        {
            TryDismissBankUiAfterStore();
            _wildExAccountOpenTries = 0;
            _wildExPhase = WildExMemOpenAccount;
            _wildExDelayUntilMs = now + WildExProtocolGapMs;
            return;
        }

        var bankIndex = _wildExBankIndexes[_wildExTakePos];
        if (!TrySendBankPet(false, _wildExWorkUid, "取宠物", bankIndex))
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("take-fail");
            Tip("从个人仓取出失败");
            return;
        }

        _wildExTakePos++;
        _wildExDelayUntilMs = now + WildExProtocolGapMs;
    }

    private static void TickWildExMemOpenAccount(long now)
    {
        var destUid = _wildExWorkUid;
        if (string.IsNullOrEmpty(destUid))
        {
            destUid = GetMainPlayerUidSafe();
        }

        StopTaskNavigation(false);
        WildExOpenBank(destUid, true);
        _wildExPhase = WildExMemWaitAccount;
        WildExSay("开账号仓存1只" + _wildExTargetName, false);
        _wildExDelayUntilMs = now + 400;
    }

    private static void TickWildExMemWaitAccount(long now)
    {
        int[] counts;
        List<int>[] indexes;
        int total;
        if (!TryCollectOpenBankZhongyuan(out counts, out indexes, out total))
        {
            if (now - _wildExWaitListStartMs >= WildExWaitListTimeoutMs)
            {
                WildExRetryAccountOpen(now, WildExMemOpenAccount);
            }

            return;
        }

        _wildExAccountOpenTries = 0;
        if (total >= ZhongyuanAccountSlots)
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("account-full");
            Tip("账号仓已满，无法再存");
            return;
        }

        var name = _wildExTargetName;
        var destUid = _wildExWorkUid;
        var captainUid = GetMainPlayerUidSafe();
        var rest = CollectMatchingRestPetIndexes(destUid, name);
        var storeUid = destUid;
        if (rest.Count <= 0 && !string.IsNullOrEmpty(captainUid))
        {
            rest = CollectMatchingRestPetIndexes(captainUid, name);
            storeUid = captainUid;
        }

        if (rest.Count <= 0)
        {
            if (now - _wildExWaitListStartMs >= WildExWaitListTimeoutMs)
            {
                TryDismissBankUiAfterStore();
                StopWildExchange("bag-empty");
                Tip("身上没有刚取出的" + name);
            }

            return;
        }

        _wildExWorkUid = storeUid;
        _wildExBankIndexes.Clear();
        _wildExBankIndexes.Add(rest[0]);
        _wildExTakePos = 0;
        _wildExPhase = WildExMemStore;
        WildExSay("存入账号仓1只" + name, false);
    }

    private static void TickWildExMemStore(long now)
    {
        if (_wildExTakePos >= _wildExBankIndexes.Count)
        {
            TryDismissBankUiAfterStore();
            _wildExScanIndex++;
            if (_wildExScanIndex >= WildPetPresets.Length)
            {
                _wildExAccountOpenTries = 0;
                _wildExPhase = WildExCapOpenAccount;
                WildExSay("234已各存1只，1号取账号仓", true);
                _wildExDelayUntilMs = now + WildExProtocolGapMs;
                return;
            }

            _wildExPhase = WildExMemOpenPersonal;
            _wildExDelayUntilMs = now + WildExProtocolGapMs;
            return;
        }

        var index = _wildExBankIndexes[_wildExTakePos];
        if (!TrySendBankPet(true, _wildExWorkUid, "存宠物", index))
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("store-fail");
            Tip("存入账号仓失败");
            return;
        }

        MarkPetUnusedByIndex(_wildExWorkUid, index);
        _wildExTakePos++;
        _wildExDelayUntilMs = now + WildExProtocolGapMs;
    }

    private static void TickWildExCapWaitAccount(string uid, long now)
    {
        int[] counts;
        List<int>[] indexes;
        int total;
        if (!TryCollectOpenBankZhongyuan(out counts, out indexes, out total))
        {
            if (now - _wildExWaitListStartMs >= WildExWaitListTimeoutMs)
            {
                WildExRetryAccountOpen(now, WildExCapOpenAccount);
            }
            else
            {
                WildExSayWait("等账号仓列表 " + ((now - _wildExWaitListStartMs) / 1000) + "秒", now);
            }

            return;
        }

        if (counts[0] <= 0 || counts[1] <= 0 || counts[2] <= 0)
        {
            if (now - _wildExWaitListStartMs < WildExWaitListTimeoutMs)
            {
                WildExSayWait("等账号仓三种 幽灵" + counts[0] + " 僵尸" + counts[1]
                              + " 骷髅" + counts[2], now);
                return;
            }

            WildExRetryAccountOpen(now, WildExCapOpenAccount);
            return;
        }

        _wildExAccountOpenTries = 0;
        var free = CountLocalPetFreeSlots(uid);
        if (free < 3)
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("no-slot");
            Tip("1号身上空位不足3格，无法取出兑换宠");
            return;
        }

        _wildExBankIndexes.Clear();
        for (var i = 0; i < 3; i++)
        {
            _wildExBankIndexes.Add(indexes[i][0]);
        }

        _wildExTakePos = 0;
        _wildExPhase = WildExCapTake;
        WildExSay("账号仓已齐3只，开始取出", true);
    }

    private static void TickWildExCapTake(string uid, long now)
    {
        if (_wildExTakePos >= _wildExBankIndexes.Count)
        {
            TryDismissBankUiAfterStore();
            _wildExPetsBeforeTalk = CountWildExRestSet(uid);
            _wildExLastLookMs = 0;
            _wildExStepTries = 0;
            _wildExPhase = WildExLookNpc;
            _wildExDelayUntilMs = now + WildExProtocolGapMs;
            WildExSay("已取3只，点中元使者", true);
            return;
        }

        var bankIndex = _wildExBankIndexes[_wildExTakePos];
        if (!TrySendBankPet(true, uid, "取宠物", bankIndex))
        {
            TryDismissBankUiAfterStore();
            StopWildExchange("take-fail");
            Tip("从账号仓取出失败");
            return;
        }

        _wildExTakePos++;
        _wildExDelayUntilMs = now + WildExProtocolGapMs;
    }

    private static int CountWildExRestSet(string uid)
    {
        return CountMatchingRestPets(uid, ZhongyuanPetA)
               + CountMatchingRestPets(uid, ZhongyuanPetB)
               + CountMatchingRestPets(uid, ZhongyuanPetC);
    }

    private static bool TryFindWildExNpc(out int objindex, out int nx, out int ny)
    {
        objindex = -1;
        nx = 0;
        ny = 0;
        try
        {
            var holder = FindType("EntityDataHolder");
            object dictObj = holder?.GetProperty(
                    "characterDatas",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null, null);
            if (dictObj == null)
            {
                dictObj = GetStaticMember("EntityDataHolder", "characterDatas");
            }

            var dict = dictObj as System.Collections.IDictionary;
            if (dict == null)
            {
                return false;
            }

            var best = -1;
            var bestDist = int.MaxValue;
            var bestX = 0;
            var bestY = 0;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var cd = e.Value;
                if (cd == null)
                {
                    continue;
                }

                var npcindex = Convert.ToInt32(GetMember(cd, "npcindex") ?? GetProp(cd, "npcindex") ?? -1);
                if (npcindex == -1)
                {
                    continue;
                }

                var obj = Convert.ToInt32(GetMember(cd, "objindex") ?? GetProp(cd, "objindex") ?? -1);
                if (obj < 0)
                {
                    continue;
                }

                var name = (Convert.ToString(GetMember(cd, "name") ?? GetProp(cd, "name") ?? "") ?? "").Trim();
                if (name.IndexOf(WildExNpcName, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var ox = Convert.ToInt32(GetMember(cd, "x") ?? GetProp(cd, "x") ?? 0);
                var oy = Convert.ToInt32(GetMember(cd, "y") ?? GetProp(cd, "y") ?? 0);
                var dist = Math.Abs(ox - WildExNpcStandX) + Math.Abs(oy - WildExNpcStandY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = obj;
                    bestX = ox;
                    bestY = oy;
                }
            }

            if (best < 0)
            {
                return false;
            }

            objindex = best;
            nx = bestX;
            ny = bestY;
            WriteLog("wild-ex npc obj=" + best + " at " + bestX + "," + bestY);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryFindWildExNpc EX " + RootMessage(ex));
            return false;
        }
    }

    private static void TickWildExLookNpc(long now)
    {
        if (IsDialoguePanelOpen())
        {
            _wildExPhase = WildExPickOption;
            _wildExActionAtMs = now;
            WildExSay("对话已开，选第2项", false);
            _wildExDelayUntilMs = now + 800;
            return;
        }

        if (_wildExLastLookMs > 0 && now - _wildExLastLookMs < WildExNpcLookRetryMs)
        {
            return;
        }

        int obj;
        int nx;
        int ny;
        if (TryFindWildExNpc(out obj, out nx, out ny))
        {
            _wildExNpcObj = obj;
            _wildExNpcFoundX = nx;
            _wildExNpcFoundY = ny;
        }

        _wildExLastLookMs = now;
        _wildExStepTries++;
        if (_wildExStepTries > 6)
        {
            StopWildExchange("look-fail");
            Tip("点中元使者失败");
            return;
        }

        if (_wildExNpcObj >= 0 && TryLookNpcByObj(_wildExNpcObj))
        {
            WildExSay("已点中元使者，等对话", true);
            return;
        }

        WildExSay("没点到中元使者，重试", true);
    }

    private static void TickWildExPickOption(long now)
    {
        if (!IsDialoguePanelOpen())
        {
            _wildExLastLookMs = 0;
            _wildExPhase = WildExLookNpc;
            WildExSay("对话关了，再点中元使者", false);
            return;
        }

        if (TryPickWildExNpcOption())
        {
            _wildExActionAtMs = now;
            _wildExPhase = WildExConfirm;
            WildExSay("已选第2项，等确定框", true);
            _wildExDelayUntilMs = now + 800;
            return;
        }

        if (_wildExActionAtMs > 0 && now - _wildExActionAtMs > WildExNpcWaitMs)
        {
            StopWildExchange("pick-fail");
            Tip("中元使者选第2项失败");
        }
    }

    private static void TickWildExConfirm(string uid, long now)
    {
        DumpWildExOpenUi("confirm");
        var left = CountWildExRestSet(uid);
        if (left <= _wildExPetsBeforeTalk - 3)
        {
            _wildExRound++;
            _wildExScanIndex = 0;
            _wildExStepTries = 0;
            _wildExActionAtMs = 0;
            _wildExPhase = WildExScanDestOpen;
            WildExSay("第" + _wildExRound + "轮兑换完成，再仓检", true);
            _wildExDelayUntilMs = now + WildExProtocolGapMs;
            return;
        }

        if (TryPickWildExConfirm())
        {
            _wildExActionAtMs = now;
            _wildExPhase = WildExWaitDone;
            WildExSay("已点确定，等交宠", true);
            _wildExDelayUntilMs = now + 800;
            return;
        }

        if (_wildExActionAtMs > 0 && now - _wildExActionAtMs > WildExNpcWaitMs)
        {
            StopWildExchange("confirm-fail");
            Tip("兑换确定框未点到");
            return;
        }

        WildExSayWait("等确定框", now);
    }

    private static bool TryPickWildExConfirm()
    {
        if (TryConfirmMessageBoxPanel())
        {
            WriteLog("wild-ex confirm MessageBoxPanel.OnSubmit");
            Tip("兑换野生宠：已点MessageBox确定");
            return true;
        }

        if (TryPickWildExWindowsOk())
        {
            return true;
        }

        if (TryClickWindowsMessageOkOnly())
        {
            WriteLog("wild-ex confirm UI_WindowsMessage btn");
            Tip("兑换野生宠：已点对话确定");
            return true;
        }

        return false;
    }

    private static void DumpWildExOpenUi(string tag)
    {
        var now = NowMs();
        if (_wildExLastLookMs > 0 && now - _wildExLastLookMs < 1500)
        {
            return;
        }

        _wildExLastLookMs = now;
        var parts = new List<string>();
        var names = new[]
        {
            "UI_WindowsMessage", "MessageBoxPanel", "NPCChatPanel",
            "BankPanel", "PetStoragePanel", "PetBankPanel"
        };
        for (var i = 0; i < names.Length; i++)
        {
            try
            {
                var panel = GetUiPanel(names[i]);
                if (panel == null || !IsUnityObjectActive(panel))
                {
                    continue;
                }

                var extra = "";
                if (names[i] == "MessageBoxPanel")
                {
                    extra = " type=" + Convert.ToString(GetMember(panel, "m_type") ?? "")
                            + " sever=" + Convert.ToString(GetMember(panel, "m_SeverInfo") ?? "")
                            + " client=" + Convert.ToString(GetMember(panel, "m_ClientInfo") ?? "");
                }

                if (names[i] == "UI_WindowsMessage")
                {
                    extra = " " + DumpWildExWmdbButtons();
                }

                parts.Add(names[i] + extra);
            }
            catch
            {
                // ignore
            }
        }

        WriteLog("wild-ex ui " + tag + " open=" + (parts.Count == 0 ? "(none)" : string.Join("|", parts.ToArray())));
    }

    private static string DumpWildExWmdbButtons()
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            var wmdb = npcMgr == null ? null : GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return "wmdb=null";
            }

            var seqno = Convert.ToInt32(GetMember(wmdb, "seqno") ?? 0);
            var windowType = Convert.ToInt32(GetMember(wmdb, "windowType") ?? 0);
            var buttonData = GetMember(wmdb, "buttonData") as Array;
            var dump = "";
            if (buttonData != null)
            {
                for (var i = 0; i < buttonData.Length && i < 9; i++)
                {
                    var btn = buttonData.GetValue(i);
                    if (btn == null)
                    {
                        continue;
                    }

                    var name = (Convert.ToString(GetMember(btn, "name") ?? "") ?? "").Trim();
                    var value = Convert.ToInt32(GetMember(btn, "value") ?? -1);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (dump.Length > 0)
                    {
                        dump += ",";
                    }

                    dump += name + "=" + value;
                }
            }

            return "wt=" + windowType + " seq=" + seqno + " btns=" + dump;
        }
        catch (Exception ex)
        {
            return "wmdbEX=" + RootMessage(ex);
        }
    }

    private static bool TryConfirmMessageBoxPanel()
    {
        try
        {
            var panel = GetUiPanel("MessageBoxPanel");
            if (panel == null || !IsUnityObjectActive(panel))
            {
                return false;
            }

            var sever = GetMember(panel, "m_SeverInfo");
            var client = GetMember(panel, "m_ClientInfo");
            var type = Convert.ToString(GetMember(panel, "m_type") ?? "");
            if (sever == null && client == null && string.IsNullOrEmpty(type))
            {
                return false;
            }

            var onSubmit = panel.GetType().GetMethod(
                "OnSubmit",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (onSubmit == null)
            {
                return false;
            }

            onSubmit.Invoke(panel, null);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryConfirmMessageBoxPanel EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryPickWildExWindowsOk()
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return false;
            }

            var wmdb = GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return false;
            }

            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null || buttonData.Length == 0)
            {
                return false;
            }

            int pickValue = -1;
            string pickName = null;
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

                if (IsDialogueSendName(name))
                {
                    pickValue = value;
                    pickName = name;
                    break;
                }
            }

            if (pickValue < 0)
            {
                return false;
            }

            if (!TrySendWildExWindows(npcMgr, wmdb, pickValue))
            {
                return false;
            }

            WriteLog("wild-ex confirm SendWindows " + pickName + " v=" + pickValue);
            Tip("兑换野生宠：已点" + pickName);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryPickWildExWindowsOk EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryClickWindowsMessageOkOnly()
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
            foreach (var fieldName in names)
            {
                var btn = GetMember(panel, fieldName);
                if (btn == null || !IsUnityObjectActive(btn))
                {
                    continue;
                }

                var title = GetCustomButtonTitle(btn);
                if (!IsDialogueSendName(title))
                {
                    continue;
                }

                return InvokeButtonClick(btn);
            }
        }
        catch (Exception ex)
        {
            WriteLog("TryClickWindowsMessageOkOnly EX " + RootMessage(ex));
        }

        return false;
    }

    private static bool TryPickWildExNpcOption()
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return false;
            }

            var wmdb = GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return false;
            }

            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null || buttonData.Length == 0)
            {
                return false;
            }

            int pickValue;
            string pickName;
            if (!TryChooseWildExButton(buttonData, out pickValue, out pickName))
            {
                return false;
            }

            if (!TrySendWildExWindows(npcMgr, wmdb, pickValue))
            {
                return false;
            }

            WriteLog("wild-ex pick " + pickName + " v=" + pickValue);
            Tip("兑换野生宠：已选" + pickName);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryPickWildExNpcOption EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TrySendWildExWindows(object npcMgr, object wmdb, int pickValue)
    {
        if (npcMgr == null || wmdb == null)
        {
            return false;
        }

        var seqno = Convert.ToInt32(GetMember(wmdb, "seqno") ?? 0);
        var windowTypeObj = GetMember(wmdb, "windowType");
        var windowType = Convert.ToInt32(windowTypeObj ?? 0);
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

        var loc = GetStaticMember("PlayerDataHolder", "location");
        var x = Convert.ToInt32(GetMember(loc, "x") ?? GetMember(loc, "X") ?? 0);
        var y = Convert.ToInt32(GetMember(loc, "y") ?? GetMember(loc, "Y") ?? 0);
        var objindex = Convert.ToInt32(GetMember(wmdb, "objindex") ?? 0);
        var uid = Convert.ToString(GetMember(wmdb, "m_Uid") ?? "") ?? "";

        MethodInfo send8 = null;
        foreach (var m in npcMgr.GetType().GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
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
            WriteLog("wild-ex SendWindows missing");
            return false;
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
        WriteLog("wild-ex SendWindows v=" + pickValue + " seq=" + seqno);
        return true;
    }

    private static bool TryChooseWildExButton(Array buttonData, out int pickValue, out string pickName)
    {
        pickValue = -1;
        pickName = null;
        var options = new List<KeyValuePair<int, string>>();
        var dump = "";
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

            if (dump.Length > 0)
            {
                dump += ",";
            }

            dump += name + "=" + value;
            if (IsDialogueCancelName(name))
            {
                continue;
            }

            options.Add(new KeyValuePair<int, string>(value, name));
        }

        WriteLog("wild-ex buttons " + dump);
        var idx = WildExOptionIndex - 1;
        if (idx >= 0 && idx < options.Count)
        {
            pickValue = options[idx].Key;
            pickName = options[idx].Value;
            return true;
        }

        return false;
    }

    private static void TickWildExWaitDone(string uid, long now)
    {
        if (IsDialoguePanelOpen() || GetUiPanel("MessageBoxPanel") != null)
        {
            TryPickWildExConfirm();
        }

        var left = CountWildExRestSet(uid);
        if (left <= _wildExPetsBeforeTalk - 3)
        {
            _wildExRound++;
            _wildExScanIndex = 0;
            _wildExStepTries = 0;
            _wildExActionAtMs = 0;
            _wildExPhase = WildExScanDestOpen;
            WildExSay("第" + _wildExRound + "轮兑换完成，再仓检", true);
            _wildExDelayUntilMs = now + WildExProtocolGapMs;
            return;
        }

        if (_wildExActionAtMs > 0 && now - _wildExActionAtMs > WildExNpcWaitMs)
        {
            StopWildExchange("exchange-timeout");
            Tip("交宠超时，身上还剩" + left + "只");
            return;
        }

        WildExSayWait("等交宠完成 身" + left + "只", now);
    }

    private static void TickWildExStoreTicket(string uid, long now)
    {
        TryDismissBankUiAfterStore();
        StopTaskNavigation(false);
        var n = CountBagItemByKeyword(uid, WildExTicketKeyword);
        if (n <= 0)
        {
            StopWildExchange("done");
            Tip("兑换结束");
            return;
        }

        _wildExStepTries++;
        if (_wildExStepTries > 8)
        {
            StopWildExchange("ticket-fail");
            Tip("中元礼盒兑换券未能存完，背包还剩" + n);
            return;
        }

        if (!StoreBagItemsToAccountBank(uid, WildExTicketKeyword))
        {
            StopWildExchange("ticket-fail");
            Tip("存中元礼盒兑换券失败");
            TryDismissBankUiAfterStore();
            return;
        }

        TryDismissBankUiAfterStore();
        WildExSay("已存兑换券，背包原有" + n + "，复查", true);
        _wildExDelayUntilMs = now + 1500;
    }
    private static string FormatZhongyuanStatus()
    {
        if (_zyAllActive)
        {
            var n = (_zyAllIndex >= 0 && _zyAllIndex < WildPetPresets.Length)
                ? WildPetPresets[_zyAllIndex]
                : "";
            return "中元抓齐: " + (_zyAllIndex + 1) + "/3 " + n
                   + " " + ZyAllPhaseName(_zyAllPhase)
                   + " 缺" + _zyXferNeed
                   + "\n" + (_zyAllNote ?? "");
        }

        if (_zyCatchActive)
        {
            return "中元抓宠: " + (_zyName ?? "") + " 仓" + _zyPersonalMatch
                   + "+身休" + CountMatchingRestPets(GetMainPlayerUidSafe(), _zyName)
                   + "/" + GetZyCatchFill() + "（234已有" + _zyDestHave + "）\n" + (_zyNote ?? "");
        }

        if (_zyXferActive)
        {
            var role = _zyXferPush ? "交出" : "接收";
            return "中元倒腾: " + role + " " + (_zyName ?? "")
                   + " →" + FormatTeamSlotLabel(_zyDestSlot)
                   + " 仓" + _zyPersonalMatch + " 号仓" + _zyAccountMatch
                   + " 需" + GetZyXferNeed() + "已交" + _zyXferSent
                   + "\n" + (_zyNote ?? "");
        }

        return "中元: 未启动\n1号点「中元抓齐」：先仓检 2/3/4，已满15的种跳过，只补缺的。半路接着跑即可。";
    }

    private static string ZyAllPhaseName(int phase)
    {
        switch (phase)
        {
            case ZyAllReturn:
            case ZyAllWaitReturn: return "回城点2";
            case ZyAllTeleport:
            case ZyAllWaitTeleport: return "去刷点";
            case ZyAllNavC: return "骷髅战士路";
            case ZyAllCatchWait: return "按缺额抓";
            case ZyAllXferWait: return "倒腾";
            case ZyAllScan: return "仓检";
            default: return "准备";
        }
    }

    private static string GetMainPlayerUidSafe()
    {
        try
        {
            return Convert.ToString(GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static void ToggleZhongyuanCatch()
    {
        if (_zyCatchActive)
        {
            StopZhongyuanCatch("已手动停止", false);
            return;
        }

        StartZhongyuanCatch();
    }

    private static void ToggleZhongyuanAll()
    {
        if (_zyAllActive)
        {
            StopZhongyuanAll("已手动停止");
            return;
        }

        StartZhongyuanAll();
    }

    private static void StartZhongyuanAll()
    {
        if (_zyLoopActive && _zyLoopPhase != ZyLoopCatch)
        {
            Tip("中元循环仓检中");
            return;
        }

        if (_wildExActive || _zyCatchActive || _zyXferActive || _skCNavActive)
        {
            Tip("请先停兑换/抓宠/倒腾/骷髅战士导航");
            return;
        }

        if (_escortActive || _dragonLoopActive || _midAutumnLoopActive || _floraHealActive)
        {
            Tip("请先停护航或法兰治疗");
            return;
        }

        var local = GetLocalTeamSlot();
        if (local > 0)
        {
            Tip("请用1号抓齐（不切号）");
            return;
        }

        _zyAllActive = true;
        _zyAllIndex = 0;
        _zyAllPhase = ZyAllScan;
        _zyScanStep = ZyScanDestOpen;
        _zyAllNote = "仓检";
        _zyAllDelayUntilMs = 0;
        _zyAllExpectFloor = 0;
        _zyAllReturnTries = 0;
        _zyLastCatchOk = false;
        _zyLastXferOk = false;
        _zyAllCompletedOk = false;
        _zyAllCatchStarted = false;
        _zyAllXferStarted = false;
        _skCNavForAll = false;
        _zyAllWaitBattleSinceMs = 0;
        _zyCatchFill = 0;
        _zyXferNeed = -1;
        _zyXferSent = 0;
        _zyDestHave = 0;
        Tip("中元抓齐已开启：先仓检再按缺额抓");
        WriteLog("zy-all start");
        RefreshScriptTabIfVisible();
    }

    private static void StopZhongyuanAll(string reason)
    {
        if (!_zyAllActive)
        {
            return;
        }

        _zyAllActive = false;
        _zyAllPhase = ZyAllIdle;
        _zyAllNote = reason ?? "";
        _skCNavForAll = false;
        ReleaseLingTangOwnedEscort();
        if (_zyCatchActive)
        {
            StopZhongyuanCatch("抓齐停止", false);
        }

        if (_zyXferActive)
        {
            StopZhongyuanTransfer("抓齐停止", false);
        }

        if (_skCNavActive)
        {
            StopSkCNav("抓齐停止");
        }

        TrySendLocalAutoBattle("停止挂机");
        TryDismissBankUiAfterStore();
        WriteLog("zy-all stop " + reason);
        Tip("中元抓齐：" + reason);
        RefreshScriptTabIfVisible();
    }

    private static void FinishZhongyuanAllSuccess()
    {
        _zyAllCompletedOk = true;
        StopZhongyuanAll("已抓齐三种宠物");
    }

    private static void TickZhongyuanAll()
    {
        if (!_zyAllActive)
        {
            return;
        }

        var now = NowMs();
        if (_zyAllDelayUntilMs > 0 && now < _zyAllDelayUntilMs)
        {
            return;
        }

        _zyAllDelayUntilMs = 0;
        if (IsInBattleNow() || IsMapLoading())
        {
            if (IsInBattleNow()
                && _zyAllWaitStartMs > 0
                && (_zyAllPhase == ZyAllWaitTeleport || _zyAllPhase == ZyAllNavC)
                && _zyAllWaitBattleSinceMs <= 0)
            {
                _zyAllWaitBattleSinceMs = now;
            }

            return;
        }

        if (_zyAllWaitBattleSinceMs > 0 && _zyAllWaitStartMs > 0)
        {
            _zyAllWaitStartMs += now - _zyAllWaitBattleSinceMs;
            _zyAllWaitBattleSinceMs = 0;
        }

        if (_zyAllIndex < 0 || _zyAllIndex >= WildPetPresets.Length)
        {
            FinishZhongyuanAllSuccess();
            return;
        }

        var name = WildPetPresets[_zyAllIndex];
        switch (_zyAllPhase)
        {
            case ZyAllScan:
                TickZhongyuanAllScan(name, now);
                break;
            case ZyAllReturn:
                TickZhongyuanAllReturn();
                break;
            case ZyAllWaitReturn:
                TickZhongyuanAllWaitReturn(now);
                break;
            case ZyAllTeleport:
                TickZhongyuanAllTeleport(name);
                break;
            case ZyAllWaitTeleport:
            case ZyAllNavC:
                TickZhongyuanAllWaitArrive(name, now);
                break;
            case ZyAllCatchWait:
                TickZhongyuanAllCatchWait(name);
                break;
            case ZyAllXferWait:
                TickZhongyuanAllXferWait(name);
                break;
        }
    }

    private static void ToggleZhongyuanLoop()
    {
        if (_zyLoopActive)
        {
            StopZhongyuanLoop("已手动停止");
            return;
        }

        StartZhongyuanLoop();
    }

    private static void StartZhongyuanLoop()
    {
        if (_escortActive || _escortPicking)
        {
            Tip("请先停任务护航再开中元循环");
            return;
        }

        if (_zyAllActive || _zyCatchActive || _zyXferActive || _skCNavActive || _wildExActive)
        {
            Tip("请先停脚本页中元抓齐/倒腾");
            return;
        }

        if (_dragonLoopActive || _midAutumnLoopActive || _floraHealActive)
        {
            Tip("请先停其它循环或法兰治疗");
            return;
        }

        if (GetLocalTeamSlot() > 0)
        {
            Tip("请用1号开中元循环（不切号，234不必点接收）");
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            _zyLoopDestHave[i] = 0;
            _zyLoopDestBankMatch[i] = 0;
            _zyLoopDestBankTotal[i] = 0;
            _zyLoopDestBankCap[i] = 0;
            _zyLoopDestBankFree[i] = 0;
            _zyLoopDestTimedOut[i] = false;
            _zyLoopDestOthers[i] = "";
            _zyLoopAccountHave[i] = 0;
            _zyLoopLocalHave[i] = 0;
        }

        _zyLoopAccountTotal = 0;
        _zyLoopAccountTimedOut = false;
        _zyLoopAccountOpenTries = 0;
        _zyLoopLocalBankTotal = 0;
        _zyLoopLocalBankCap = 0;
        _zyLoopLocalBankFree = 0;

        _zyLoopRound = 0;
        _zyLoopActive = true;
        _zyLoopPhase = ZyLoopScanDest;
        _zyLoopScanIndex = 0;
        _zyLoopReport = "";
        _zyLoopLastTipMs = 0;
        _zyLoopNote = "仓检2号幽灵";
        ZyLoopSay("开始仓检：仓检 → 抓三种 → 兑换 → 再仓检", true);
        TryRebuildEscortTab();
    }

    private static void StopZhongyuanLoop(string reason)
    {
        if (!_zyLoopActive)
        {
            return;
        }

        _zyLoopActive = false;
        _zyLoopPhase = ZyLoopIdle;
        _zyLoopNote = reason ?? "";
        _wildExInLoop = false;
        if (_wildExActive)
        {
            StopWildExchange("loop-stop");
        }
        if (_zyAllActive)
        {
            StopZhongyuanAll(reason);
        }

        TryDismissBankUiAfterStore();
        WriteLog("zy-loop stop " + reason);
        Tip("中元循环：" + reason);
        TryRebuildEscortTab();
        RefreshScriptTabIfVisible();
    }

    private static void BeginZhongyuanLoopExchange()
    {
        TryDismissBankUiAfterStore();
        _zyLoopPhase = ZyLoopExchange;
        ZyLoopSay("抓齐完成，开始兑换", true);
        _wildExInLoop = true;
        StartWildExchange();
        if (!_wildExActive)
        {
            _wildExInLoop = false;
            StopZhongyuanLoop("未能开始兑换");
        }
    }

    private static void RestartZhongyuanLoopScan()
    {
        TryDismissBankUiAfterStore();
        for (var i = 0; i < 3; i++)
        {
            _zyLoopDestHave[i] = 0;
            _zyLoopDestBankMatch[i] = 0;
            _zyLoopDestBankTotal[i] = 0;
            _zyLoopDestBankCap[i] = 0;
            _zyLoopDestBankFree[i] = 0;
            _zyLoopDestTimedOut[i] = false;
            _zyLoopDestOthers[i] = "";
            _zyLoopAccountHave[i] = 0;
            _zyLoopLocalHave[i] = 0;
        }

        _zyLoopAccountTotal = 0;
        _zyLoopAccountTimedOut = false;
        _zyLoopAccountOpenTries = 0;
        _zyLoopLocalBankTotal = 0;
        _zyLoopLocalBankCap = 0;
        _zyLoopLocalBankFree = 0;
        _zyLoopRound++;
        _zyLoopScanIndex = 0;
        _zyLoopReport = "";
        _zyLoopPhase = ZyLoopScanDest;
        ZyLoopSay("第" + _zyLoopRound + "轮结束，重新仓检", true);
        TryRebuildEscortTab();
        RefreshScriptTabIfVisible();
    }

    private static string FormatZhongyuanLoopStatus()
    {
        if (!_zyLoopActive && string.IsNullOrEmpty(_zyLoopReport))
        {
            return "";
        }

        var head = _zyLoopActive
            ? ("中元循环: " + ZhongyuanLoopPhaseName() + " " + (_zyLoopNote ?? ""))
            : "中元循环: 未启动";
        if (!string.IsNullOrEmpty(_zyLoopReport))
        {
            return head + "\n" + _zyLoopReport;
        }

        return head;
    }

    private static string ZhongyuanLoopPhaseName()
    {
        switch (_zyLoopPhase)
        {
            case ZyLoopScanDest:
            case ZyLoopScanDestWait:
            case ZyLoopScanAccount:
            case ZyLoopScanAccountWait:
            case ZyLoopScanLocal:
            case ZyLoopScanLocalWait:
                return "仓检";
            case ZyLoopCatch:
                return "抓齐";
            case ZyLoopExchange:
                return "兑换";
            default:
                return "准备";
        }
    }

    private static void ZyLoopSay(string msg, bool tip)
    {
        _zyLoopNote = msg ?? "";
        WriteLog("zy-loop " + (msg ?? ""));
        if (tip)
        {
            Tip("中元循环：" + msg);
            _zyLoopLastTipMs = NowMs();
        }
    }

    private static void ZyLoopSayWait(string msg, long now)
    {
        _zyLoopNote = msg ?? "";
        if (_zyLoopLastTipMs <= 0 || now - _zyLoopLastTipMs >= 2500)
        {
            ZyLoopSay(msg, true);
        }
    }

    private static string TailUid(string uid)
    {
        var s = uid ?? "";
        if (s.Length <= 4)
        {
            return s;
        }

        return s.Substring(s.Length - 4);
    }

    private static string BankWaitHint()
    {
        try
        {
            var info = TryGetPetStorageInfo();
            var store = TryGetBankStorePetInfo();
            return " info=" + (info != null ? "有" : "空")
                   + (info != null && ReferenceEquals(info, _zyInfoBefore) ? "未刷新" : "")
                   + " store=" + (store != null ? "有" : "空")
                   + (store != null && ReferenceEquals(store, _zyStoreBefore) ? "未刷新" : "")
                   + " accN=" + CountRawOrMinus(GetOpenBankRawList(true))
                   + " petN=" + CountRawOrMinus(GetOpenBankRawList(false));
        }
        catch
        {
            return "";
        }
    }

    private static int CountRawOrMinus(IList raw)
    {
        return raw == null ? -1 : raw.Count;
    }

    private static void TickZhongyuanLoop()
    {
        if (!_zyLoopActive)
        {
            return;
        }

        if (_zyLoopPhase == ZyLoopCatch)
        {
            if (!_zyAllActive)
            {
                if (_zyAllCompletedOk)
                {
                    BeginZhongyuanLoopExchange();
                }
                else
                {
                    StopZhongyuanLoop("抓齐未完成：" + (_zyAllNote ?? ""));
                }
            }

            return;
        }

        if (_zyLoopPhase == ZyLoopExchange)
        {
            if (!_wildExActive)
            {
                RestartZhongyuanLoopScan();
            }

            return;
        }

        var now = NowMs();
        if (_zyAllDelayUntilMs > 0 && now < _zyAllDelayUntilMs)
        {
            return;
        }

        _zyAllDelayUntilMs = 0;
        if (IsInBattleNow())
        {
            ZyLoopSayWait("仓检暂停：战斗中", now);
            return;
        }

        if (IsMapLoading())
        {
            ZyLoopSayWait("仓检暂停：过图中", now);
            return;
        }

        switch (_zyLoopPhase)
        {
            case ZyLoopScanDest:
                TickZhongyuanLoopScanDestOpen(now);
                break;
            case ZyLoopScanDestWait:
                TickZhongyuanLoopScanDestWait(now);
                break;
            case ZyLoopScanAccount:
                TickZhongyuanLoopScanAccountOpen(now);
                break;
            case ZyLoopScanAccountWait:
                TickZhongyuanLoopScanAccountWait(now);
                break;
            case ZyLoopScanLocal:
                TickZhongyuanLoopScanLocalOpen(now);
                break;
            case ZyLoopScanLocalWait:
                TickZhongyuanLoopScanLocalWait(now);
                break;
        }
    }

    private static void TickZhongyuanLoopScanDestOpen(long now)
    {
        if (_zyLoopScanIndex < 0 || _zyLoopScanIndex >= WildPetPresets.Length)
        {
            ZyLoopSay("2/3/4个人仓查完，开始仓检账号仓", true);
            _zyLoopPhase = ZyLoopScanAccount;
            return;
        }

        var name = WildPetPresets[_zyLoopScanIndex];
        var destSlot = GetZhongyuanDestSlot(name);
        var destUid = GetTeamUidBySlot(destSlot);
        if (string.IsNullOrEmpty(destUid))
        {
            StopZhongyuanLoop("仓检不通过：队伍里没有" + FormatTeamSlotLabel(destSlot));
            return;
        }

        ZhongyuanOpenBank(destUid, false);
        _zyLoopPhase = ZyLoopScanDestWait;
        ZyLoopSay("仓检" + FormatTeamSlotLabel(destSlot) + name + "，已发开个人仓 uid尾"
                  + TailUid(destUid), true);
        _zyAllDelayUntilMs = now + 400;
    }

    private static void TickZhongyuanLoopScanDestWait(long now)
    {
        var name = WildPetPresets[_zyLoopScanIndex];
        var destSlot = GetZhongyuanDestSlot(name);
        var destUid = GetTeamUidBySlot(destSlot);
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(name, _zyInfoBefore, _zyStoreBefore, out matching, out total))
        {
            var elapsed = now - _zyWaitListStartMs;
            if (elapsed >= ZhongyuanWaitListTimeoutMs)
            {
                _zyLoopDestTimedOut[_zyLoopScanIndex] = true;
                _zyLoopDestHave[_zyLoopScanIndex] = CountMatchingRestPets(destUid, name);
                ZyLoopSay("仓检" + FormatTeamSlotLabel(destSlot) + name
                          + " 个人仓超时，改数身上"
                          + _zyLoopDestHave[_zyLoopScanIndex] + "只", true);
                TryDismissBankUiAfterStore();
                _zyDestFullVerifyPending = false;
                _zyLoopScanIndex++;
                _zyLoopPhase = ZyLoopScanDest;
                return;
            }

            ZyLoopSayWait("仓检" + FormatTeamSlotLabel(destSlot) + name
                          + " 等仓库列表 " + (elapsed / 1000) + "秒"
                          + BankWaitHint(), now);
            return;
        }

        var matchN = matching == null ? 0 : matching.Count;
        if (matchN >= ZhongyuanQuota && !_zyDestFullVerifyPending)
        {
            _zyDestFullVerifyPending = true;
            TryDismissBankUiAfterStore();
            _zyLoopPhase = ZyLoopScanDest;
            ZyLoopSay(FormatTeamSlotLabel(destSlot) + name + " 显示满" + matchN + "，关仓复核", true);
            _zyAllDelayUntilMs = now + 400;
            return;
        }

        _zyDestFullVerifyPending = false;
        _zyLoopDestBankMatch[_zyLoopScanIndex] = matchN;
        _zyLoopDestBankTotal[_zyLoopScanIndex] = total;
        int destCap;
        int destFree;
        int destOcc;
        if (TryGetOpenBankSpace(out destOcc, out destCap, out destFree))
        {
            _zyLoopDestBankCap[_zyLoopScanIndex] = destCap;
            _zyLoopDestBankFree[_zyLoopScanIndex] = destFree;
            if (destOcc > _zyLoopDestBankTotal[_zyLoopScanIndex])
            {
                _zyLoopDestBankTotal[_zyLoopScanIndex] = destOcc;
            }
        }

        _zyLoopDestOthers[_zyLoopScanIndex] = ListOpenBankOtherPetNames(name);
        _zyLoopDestHave[_zyLoopScanIndex] = _zyLoopDestBankMatch[_zyLoopScanIndex]
                                            + CountMatchingRestPets(destUid, name);
        ZyLoopSay("仓检" + FormatTeamSlotLabel(destSlot) + name
                  + " 已有" + _zyLoopDestHave[_zyLoopScanIndex] + "/" + ZhongyuanQuota
                  + "（仓" + _zyLoopDestBankMatch[_zyLoopScanIndex] + "/" + total
                  + " 空位" + _zyLoopDestBankFree[_zyLoopScanIndex] + "/"
                  + _zyLoopDestBankCap[_zyLoopScanIndex] + "）", true);
        TryDismissBankUiAfterStore();
        _zyDestFullVerifyPending = false;
        _zyLoopScanIndex++;
        _zyLoopPhase = ZyLoopScanDest;
    }

    private static void TickZhongyuanLoopScanAccountOpen(long now)
    {
        var uid = GetMainPlayerUidSafe();
        ZhongyuanOpenBank(uid, true);
        _zyLoopPhase = ZyLoopScanAccountWait;
        ZyLoopSay("仓检账号仓，已发开仓", true);
        _zyAllDelayUntilMs = now + 400;
    }

    private static void TickZhongyuanLoopScanAccountWait(long now)
    {
        List<int> matching;
        int total;
        var any = false;
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            if (TryCollectOpenBankPets(
                    WildPetPresets[i], _zyInfoBefore, _zyStoreBefore, out matching, out total, true))
            {
                _zyLoopAccountHave[i] = matching == null ? 0 : matching.Count;
                _zyLoopAccountTotal = total;
                any = true;
            }
        }

        if (!any)
        {
            var elapsed = now - _zyWaitListStartMs;
            if (elapsed >= ZhongyuanWaitListTimeoutMs)
            {
                TryDismissBankUiAfterStore();
                _zyLoopAccountOpenTries++;
                if (_zyLoopAccountOpenTries < WildExAccountOpenMaxTries)
                {
                    ZyLoopSay("账号仓未开，重开 " + _zyLoopAccountOpenTries
                              + "/" + WildExAccountOpenMaxTries, true);
                    _zyLoopPhase = ZyLoopScanAccount;
                    _zyAllDelayUntilMs = now + 800;
                    return;
                }

                _zyLoopAccountTimedOut = true;
                ZyLoopSay("账号仓超时未打开，按空仓继续仓检1号个人仓", true);
                _zyLoopPhase = ZyLoopScanLocal;
                return;
            }

            ZyLoopSayWait("仓检账号仓 等仓库列表 " + (elapsed / 1000) + "秒" + BankWaitHint(), now);
            return;
        }

        _zyLoopAccountTimedOut = false;
        _zyLoopAccountOpenTries = 0;
        ZyLoopSay("账号仓 " + _zyLoopAccountTotal + "只（幽灵"
                  + _zyLoopAccountHave[0] + " 僵尸" + _zyLoopAccountHave[1]
                  + " 骷髅战士" + _zyLoopAccountHave[2] + "）", true);
        TryDismissBankUiAfterStore();
        _zyLoopPhase = ZyLoopScanLocal;
    }

    private static void TickZhongyuanLoopScanLocalOpen(long now)
    {
        var uid = GetMainPlayerUidSafe();
        ZhongyuanOpenBank(uid, false);
        _zyLoopPhase = ZyLoopScanLocalWait;
        ZyLoopSay("仓检1号个人仓，已发开仓", true);
        _zyAllDelayUntilMs = now + 400;
    }

    private static void TickZhongyuanLoopScanLocalWait(long now)
    {
        var uid = GetMainPlayerUidSafe();
        List<int> matching;
        int total;
        var any = false;
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            if (TryCollectOpenBankPets(WildPetPresets[i], _zyInfoBefore, _zyStoreBefore, out matching, out total))
            {
                _zyLoopLocalHave[i] = (matching == null ? 0 : matching.Count)
                                      + CountMatchingRestPets(uid, WildPetPresets[i]);
                _zyLoopLocalBankTotal = total;
                any = true;
            }
        }

        if (!any)
        {
            var elapsed = now - _zyWaitListStartMs;
            if (elapsed >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanLoop("仓检不通过：1号个人仓超时未打开");
                return;
            }

            ZyLoopSayWait("仓检1号个人仓 等仓库列表 " + (elapsed / 1000) + "秒" + BankWaitHint(), now);
            return;
        }

        int localOcc;
        int localCap;
        int localFree;
        if (TryGetOpenBankSpace(out localOcc, out localCap, out localFree))
        {
            _zyLoopLocalBankCap = localCap;
            _zyLoopLocalBankFree = localFree;
            if (localOcc > _zyLoopLocalBankTotal)
            {
                _zyLoopLocalBankTotal = localOcc;
            }
        }

        ZyLoopSay("1号个人仓 " + _zyLoopLocalBankTotal + "只，空位"
                  + _zyLoopLocalBankFree + "/" + _zyLoopLocalBankCap + "，开始汇总", true);
        FinishZhongyuanLoopScan();
    }

    private static void FinishZhongyuanLoopScan()
    {
        TryDismissBankUiAfterStore();
        var lines = new List<string>();
        var tipParts = new List<string>();
        var fails = CollectZhongyuanLoopScanFails();
        var needAny = false;
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            var name = WildPetPresets[i];
            var dest = GetZhongyuanDestSlot(name);
            var have = _zyLoopDestHave[i];
            var remain = ZhongyuanQuota - have;
            if (remain < 0)
            {
                remain = 0;
            }

            var line = FormatTeamSlotLabel(dest) + name + " " + have + "/" + ZhongyuanQuota;
            if (_zyLoopDestTimedOut[i])
            {
                line += " 仓超时";
            }
            else if (remain <= 0)
            {
                line += " 跳过";
                tipParts.Add(name + "满");
            }
            else
            {
                line += " 缺" + remain;
                tipParts.Add(name + "缺" + remain);
                needAny = true;
            }

            if (_zyLoopAccountHave[i] > 0 || _zyLoopLocalHave[i] > 0)
            {
                line += "（账号仓" + _zyLoopAccountHave[i] + " 1号" + _zyLoopLocalHave[i] + "）";
            }

            if (!string.IsNullOrEmpty(_zyLoopDestOthers[i]))
            {
                line += " 混有" + _zyLoopDestOthers[i];
            }

            line += " 空位" + _zyLoopDestBankFree[i] + "/" + _zyLoopDestBankCap[i];
            lines.Add(line);
        }

        if (_zyLoopAccountTimedOut)
        {
            lines.Add("账号仓未打开，按空仓计");
        }
        else if (_zyLoopAccountTotal > 0)
        {
            lines.Add("账号仓有" + _zyLoopAccountTotal + "只");
        }

        if (_zyLoopLocalBankTotal > 0 || _zyLoopLocalBankFree < ZhongyuanQuota)
        {
            lines.Add("1号个人仓有" + _zyLoopLocalBankTotal + "只，空位"
                      + _zyLoopLocalBankFree + "/" + _zyLoopLocalBankCap);
        }

        _zyLoopReport = string.Join("\n", lines.ToArray());
        WriteLog("zy-loop scan done " + _zyLoopReport.Replace("\n", " | "));
        if (fails.Count > 0)
        {
            var why = string.Join("；", fails.ToArray());
            _zyLoopNote = "仓检不通过";
            StopZhongyuanLoop("仓检不通过：" + why);
            return;
        }

        Tip("仓检通过：" + string.Join(" ", tipParts.ToArray()));
        _zyLoopNote = "仓检通过";
        if (!needAny)
        {
            BeginZhongyuanLoopExchange();
            return;
        }

        _zyLoopPhase = ZyLoopCatch;
        StartZhongyuanAll();
        if (!_zyAllActive)
        {
            StopZhongyuanLoop("仓检通过但未能开始抓齐");
        }
    }

    private static List<string> CollectZhongyuanLoopScanFails()
    {
        var fails = new List<string>();
        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            var name = WildPetPresets[i];
            var slot = FormatTeamSlotLabel(GetZhongyuanDestSlot(name));
            if (_zyLoopDestTimedOut[i])
            {
                fails.Add(slot + "个人仓超时未打开");
                continue;
            }

            var mixed = _zyLoopDestBankTotal[i] - _zyLoopDestBankMatch[i];
            if (mixed > 0)
            {
                var others = string.IsNullOrEmpty(_zyLoopDestOthers[i])
                    ? (mixed + "只杂宠")
                    : _zyLoopDestOthers[i];
                fails.Add(slot + "个人仓只能放" + name + "，现混有" + others);
            }

            // 已满 15 只目标宠 = 运营态，空位 0 也通过；未满才按缺额要空位。
            var remain = ZhongyuanQuota - _zyLoopDestHave[i];
            if (remain < 0)
            {
                remain = 0;
            }

            if (remain > 0 && _zyLoopDestBankFree[i] < remain)
            {
                fails.Add(slot + "个人仓空位" + _zyLoopDestBankFree[i]
                          + "，还缺" + remain + "只" + name);
            }
        }

        if (!_zyLoopAccountTimedOut && _zyLoopAccountTotal > 0)
        {
            fails.Add("账号仓有" + _zyLoopAccountTotal + "只，须清空");
        }

        if (_zyLoopLocalBankFree < ZhongyuanQuota)
        {
            fails.Add("1号个人仓空位" + _zyLoopLocalBankFree
                      + "，须空出" + ZhongyuanQuota + "格");
        }
        else if (_zyLoopLocalBankTotal > 0)
        {
            fails.Add("1号个人仓有" + _zyLoopLocalBankTotal + "只，须清空");
        }

        return fails;
    }

    private static void AdvanceZhongyuanAllAfterType()
    {
        _zyAllIndex++;
        if (_zyAllIndex >= WildPetPresets.Length)
        {
            FinishZhongyuanAllSuccess();
            return;
        }

        _zyAllReturnTries = 0;
        _zyScanStep = ZyScanDestOpen;
        _zyAllPhase = ZyAllScan;
        _zyAllNote = "下一种仓检 " + WildPetPresets[_zyAllIndex];
        Tip("中元抓齐：开始仓检" + WildPetPresets[_zyAllIndex]);
    }

    private static void TickZhongyuanAllScan(string name, long now)
    {
        var localUid = GetMainPlayerUidSafe();
        var destSlot = GetZhongyuanDestSlot(name);
        var destUid = GetTeamUidBySlot(destSlot);
        if (string.IsNullOrEmpty(localUid))
        {
            StopZhongyuanAll("仓检不通过：未找到1号角色");
            return;
        }

        if (string.IsNullOrEmpty(destUid))
        {
            StopZhongyuanAll("仓检不通过：队伍里没有" + FormatTeamSlotLabel(destSlot));
            return;
        }

        switch (_zyScanStep)
        {
            case ZyScanDestOpen:
                _zyScanDestHave = 0;
                _zyScanAccountHave = 0;
                ZhongyuanOpenBank(destUid, false);
                _zyScanStep = ZyScanDestWait;
                _zyAllNote = "仓检" + FormatTeamSlotLabel(destSlot) + "个人仓";
                _zyAllDelayUntilMs = now + 400;
                return;
            case ZyScanDestWait:
            {
                List<int> matching;
                int total;
                if (!TryCollectOpenBankPets(name, _zyInfoBefore, _zyStoreBefore, out matching, out total))
                {
                    if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
                    {
                        _zyScanDestHave = CountMatchingRestPets(destUid, name);
                        WriteLog("zy-all scan dest timeout body=" + _zyScanDestHave);
                        Tip("仓检不通过：" + FormatTeamSlotLabel(destSlot) + "个人仓超时未打开，改数身上");
                        TryDismissBankUiAfterStore();
                        _zyScanStep = ZyScanAccountOpen;
                    }

                    return;
                }

                _zyScanDestHave = (matching == null ? 0 : matching.Count)
                                  + CountMatchingRestPets(destUid, name);
                int destOcc;
                int destCap;
                int destFree;
                var destRemain = ZhongyuanQuota - _zyScanDestHave;
                if (destRemain < 0)
                {
                    destRemain = 0;
                }

                if (destRemain > 0
                    && TryGetOpenBankSpace(out destOcc, out destCap, out destFree)
                    && destFree < destRemain)
                {
                    StopZhongyuanAll("仓检不通过：" + FormatTeamSlotLabel(destSlot)
                                     + "个人仓空位" + destFree + "，还缺" + destRemain + "只" + name);
                    return;
                }

                WriteLog("zy-all scan dest " + FormatTeamSlotLabel(destSlot)
                         + " have=" + _zyScanDestHave);
                TryDismissBankUiAfterStore();
                _zyScanStep = ZyScanAccountOpen;
                return;
            }
            case ZyScanAccountOpen:
                ZhongyuanOpenBank(localUid, true);
                _zyScanStep = ZyScanAccountWait;
                _zyAllNote = "仓检账号仓";
                _zyAllDelayUntilMs = now + 400;
                return;
            case ZyScanAccountWait:
            {
                List<int> matching;
                int total;
                if (!TryCollectOpenBankPets(
                        name, _zyInfoBefore, _zyStoreBefore, out matching, out total, true))
                {
                    if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
                    {
                        _zyScanAccountHave = 0;
                        WriteLog("zy-all scan account timeout");
                        Tip("仓检不通过：账号仓超时未打开，按空仓计");
                        TryDismissBankUiAfterStore();
                        _zyScanStep = ZyScanLocalOpen;
                    }

                    return;
                }

                _zyScanAccountHave = matching == null ? 0 : matching.Count;
                TryDismissBankUiAfterStore();
                _zyScanStep = ZyScanLocalOpen;
                return;
            }
            case ZyScanLocalOpen:
                ZhongyuanOpenBank(localUid, false);
                _zyScanStep = ZyScanLocalWait;
                _zyAllNote = "仓检1号个人仓";
                _zyAllDelayUntilMs = now + 400;
                return;
            case ZyScanLocalWait:
            {
                List<int> matching;
                int total;
                if (!TryCollectOpenBankPets(name, _zyInfoBefore, _zyStoreBefore, out matching, out total))
                {
                    if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
                    {
                        StopZhongyuanAll("仓检不通过：1号个人仓超时未打开");
                    }

                    return;
                }

                var localHave = (matching == null ? 0 : matching.Count)
                                + CountMatchingRestPets(localUid, name);
                ApplyZhongyuanQuotaFromScan(name, destSlot, localHave);
                return;
            }
        }
    }

    private static void ApplyZhongyuanQuotaFromScan(string name, int destSlot, int localHave)
    {
        var remain = ZhongyuanQuota - _zyScanDestHave;
        if (remain < 0)
        {
            remain = 0;
        }

        _zyDestHave = _zyScanDestHave;
        _zyXferNeed = remain;
        _zyXferSent = 0;
        var catchFill = remain - _zyScanAccountHave;
        if (catchFill < 0)
        {
            catchFill = 0;
        }

        _zyCatchFill = catchFill;
        WriteLog("zy-all quota " + name
                 + " destHave=" + _zyDestHave
                 + " remain=" + remain
                 + " account=" + _zyScanAccountHave
                 + " local=" + localHave
                 + " catchFill=" + _zyCatchFill);
        TryDismissBankUiAfterStore();

        if (remain <= 0)
        {
            _zyAllNote = FormatTeamSlotLabel(destSlot) + "已有" + _zyDestHave + "只" + name
                         + "，丢掉1号多余";
            Tip(FormatTeamSlotLabel(destSlot) + "已有" + _zyDestHave + "只" + name
                + "，先丢掉1号多余的" + name);
            _zyLastCatchOk = true;
            _zyLastXferOk = false;
            _zyAllCatchStarted = true;
            _zyAllXferStarted = false;
            _zyAllPhase = ZyAllXferWait;
            return;
        }

        if (localHave >= _zyCatchFill)
        {
            _zyAllNote = FormatTeamSlotLabel(destSlot) + "缺" + remain + "，1号已够，直接倒腾";
            Tip("中元抓齐：" + name + "还缺" + remain + "，跳过抓宠直接倒腾");
            _zyLastCatchOk = true;
            _zyLastXferOk = false;
            _zyAllCatchStarted = true;
            _zyAllXferStarted = false;
            _zyAllPhase = ZyAllXferWait;
            return;
        }

        _zyAllNote = FormatTeamSlotLabel(destSlot) + "已有" + _zyDestHave + "，再抓" + _zyCatchFill;
        Tip("中元抓齐：" + name + " " + FormatTeamSlotLabel(destSlot) + "已有"
            + _zyDestHave + "，再抓" + _zyCatchFill);
        _zyAllReturnTries = 0;
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        if (string.Equals(name, ZhongyuanPetC, StringComparison.Ordinal) && IsSkCResumeFloor(floor))
        {
            _zyAllNote = "已在图" + floor + "，跳过回城";
            TickZhongyuanAllTeleport(name);
            return;
        }

        if (string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal) && floor == ZhongyuanHangupAFloor)
        {
            _zyAllNote = "已在灵堂，跳过回城";
            TickZhongyuanAllTeleport(name);
            return;
        }

        _zyAllPhase = ZyAllReturn;
    }

    private static void TickZhongyuanAllReturn()
    {
        TrySendLocalAutoBattle("停止挂机");
        var name = (_zyAllIndex >= 0 && _zyAllIndex < WildPetPresets.Length)
            ? WildPetPresets[_zyAllIndex]
            : "";
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        if (string.Equals(name, ZhongyuanPetC, StringComparison.Ordinal) && IsSkCResumeFloor(floor))
        {
            _zyAllNote = "已在图" + floor + "，跳过回城走骷髅战士路";
            TickZhongyuanAllTeleport(name);
            return;
        }

        if (string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal) && floor == ZhongyuanHangupAFloor)
        {
            _zyAllNote = "已在灵堂，跳过回城";
            TickZhongyuanAllTeleport(name);
            return;
        }
        if (!FloraHealSendReturnCity())
        {
            _zyAllReturnTries++;
            if (_zyAllReturnTries >= FloraHealMaxTries)
            {
                StopZhongyuanAll("回城点2失败");
                return;
            }

            _zyAllNote = "回城发包失败，重试";
            _zyAllDelayUntilMs = NowMs() + FloraHealStepDelayMs;
            return;
        }

        _zyAllPhase = ZyAllWaitReturn;
        _zyAllDelayUntilMs = NowMs() + 1200;
        _zyAllNote = "等待回城点2";
        WriteLog("zy-all return send try=" + _zyAllReturnTries);
    }

    private static void TickZhongyuanAllWaitReturn(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        int x;
        int y;
        TryGetPlayerXY(out x, out y);
        if (floor == FloraHealReturnFloor)
        {
            _zyAllReturnTries = 0;
            _zyAllPhase = ZyAllTeleport;
            _zyAllNote = "已回城，出发";
            _zyAllDelayUntilMs = now + SkCAbortWaitMs;
            return;
        }

        if (_zyAllReturnTries >= FloraHealMaxTries)
        {
            StopZhongyuanAll("回城点2未到位");
            return;
        }

        _zyAllReturnTries++;
        _zyAllPhase = ZyAllReturn;
        _zyAllNote = "回城未到位，再发";
        _zyAllDelayUntilMs = now + FloraHealStepDelayMs;
    }

    private static void TickZhongyuanAllTeleport(string name)
    {
        TrySendLocalAutoBattle("停止挂机");
        if (string.Equals(name, ZhongyuanPetC, StringComparison.Ordinal))
        {
            _skCNavForAll = true;
            _skCNavLastOk = false;
            if (!StartSkCNavCore())
            {
                _skCNavForAll = false;
                StopZhongyuanAll("骷髅战士导航未能启动");
                return;
            }

            _zyAllPhase = ZyAllNavC;
            _zyAllNote = "走骷髅战士路";
            return;
        }

        if (string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal))
        {
            int floorNowA;
            string floorNameNowA;
            int mapResIdNowA;
            TryGetCurrentMapInfo(out floorNowA, out floorNameNowA, out mapResIdNowA);
            if (floorNowA == ZhongyuanHangupAFloor)
            {
                _zyAllExpectFloor = ZhongyuanHangupAFloor;
                _zyAllPhase = ZyAllWaitTeleport;
                _zyAllWaitStartMs = NowMs();
                _zyAllWaitBattleSinceMs = 0;
                _zyAllNote = "已在灵堂 图" + ZhongyuanHangupAFloor;
                WriteLog("zy-all lingtang skip already-there floor=" + floorNowA);
                return;
            }

            if (!StartLingTangGoThenEscort())
            {
                StopZhongyuanAll("前往灵堂失败");
                return;
            }

            _zyAllExpectFloor = ZhongyuanHangupAFloor;
            _zyAllPhase = ZyAllWaitTeleport;
            _zyAllWaitStartMs = NowMs();
            _zyAllWaitBattleSinceMs = 0;
            _zyAllNote = "前往灵堂";
            WriteLog("zy-all hangup-go wayId=" + ZhongyuanHangupAWayId + " floor=" + ZhongyuanHangupAFloor);
            Tip("中元抓齐：已前往灵堂");
            return;
        }

        if (string.Equals(name, ZhongyuanPetB, StringComparison.Ordinal))
        {
            int floorNow;
            string floorNameNow;
            int mapResIdNow;
            TryGetCurrentMapInfo(out floorNow, out floorNameNow, out mapResIdNow);
            if (floorNow == ZhongyuanHangupBFloor)
            {
                _zyAllExpectFloor = ZhongyuanHangupBFloor;
                _zyAllPhase = ZyAllWaitTeleport;
                _zyAllWaitStartMs = NowMs();
                _zyAllWaitBattleSinceMs = 0;
                _zyAllNote = "已在奇怪的洞窟怪 图" + ZhongyuanHangupBFloor;
                WriteLog("zy-all hangup skip already-there id=" + ZhongyuanHangupBId + " floor=" + floorNow);
                return;
            }

            if (!TrySendHangupTeleport(ZhongyuanHangupBId))
            {
                StopZhongyuanAll("挂机传送奇怪的洞窟怪失败");
                return;
            }

            _zyAllExpectFloor = ZhongyuanHangupBFloor;
            _zyAllPhase = ZyAllWaitTeleport;
            _zyAllWaitStartMs = NowMs();
            _zyAllWaitBattleSinceMs = 0;
            _zyAllNote = "传送奇怪的洞窟怪";
            WriteLog("zy-all hangup id=" + ZhongyuanHangupBId + " floor=" + ZhongyuanHangupBFloor);
            Tip("中元抓齐：已传送奇怪的洞窟怪");
            return;
        }

        StopZhongyuanAll("未知刷点 " + name);
    }

    /// <summary>挂机「前往」WayId=1003，再护航这条路，到 52018 停。</summary>
    private static bool StartLingTangGoThenEscort()
    {
        if (_escortActive && !_zyLingTangOwnsEscort)
        {
            WriteLog("zy-lingtang escort busy");
            return false;
        }

        if (_skCNavOwnsEscort || _skCNavActive)
        {
            WriteLog("zy-lingtang skc busy");
            return false;
        }

        try
        {
            AbortEscortTaskPathFully("zy-lingtang-go");
        }
        catch
        {
            // ignore
        }

        if (!TrySendHangupGo(ZhongyuanHangupAWayId))
        {
            WriteLog("zy-lingtang StartWayTh fail wayId=" + ZhongyuanHangupAWayId);
            return false;
        }

        return StartLingTangOwnedEscort();
    }

    private static bool StartLingTangOwnedEscort()
    {
        _zyLingTangSavedQueue.Clear();
        for (var i = 0; i < _escortQueue.Count; i++)
        {
            _zyLingTangSavedQueue.Add(_escortQueue[i]);
        }

        _zyLingTangSavedIndex = _escortQueueIndex;
        _escortQueue.Clear();
        var title = "#1003";
        try
        {
            var mission = GetMissionDataById(ZhongyuanHangupAWayId);
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

        _escortQueue.Add(new EscortCandidate
        {
            Id = ZhongyuanHangupAWayId,
            Title = title,
            Status = "脚本"
        });
        _zyLingTangOwnsEscort = true;
        try
        {
            StartEscortQueue();
        }
        catch (Exception ex)
        {
            WriteLog("zy-lingtang start escort EX " + RootMessage(ex));
            ReleaseLingTangOwnedEscort();
            return false;
        }

        if (!_escortActive)
        {
            ReleaseLingTangOwnedEscort();
            WriteLog("zy-lingtang escort not active");
            return false;
        }

        WriteLog("zy-lingtang escort start id=" + ZhongyuanHangupAWayId);
        return true;
    }

    private static void HandOffLingTangFromEscort(string reason)
    {
        WriteLog("zy-lingtang handoff " + reason);
        ReleaseLingTangOwnedEscort();
    }

    private static void ReleaseLingTangOwnedEscort()
    {
        if (!_zyLingTangOwnsEscort)
        {
            return;
        }

        _zyLingTangOwnsEscort = false;
        try
        {
            AbortEscortTaskPathFully("zy-lingtang-release");
        }
        catch
        {
            // ignore
        }

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
        ClearEscortStuckPending();
        _escortFinishWaitMs = 0;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("zy-lingtang-release", false);
        _escortPrevInBattle = false;
        _escortQueue.Clear();
        for (var i = 0; i < _zyLingTangSavedQueue.Count; i++)
        {
            _escortQueue.Add(_zyLingTangSavedQueue[i]);
        }

        _escortQueueIndex = _zyLingTangSavedIndex;
        _zyLingTangSavedQueue.Clear();
        _zyLingTangSavedIndex = -1;
        _prevRunTaskId = GetRunTaskId();
        if (wasActive)
        {
            try
            {
                StopTaskNavigation(false);
            }
            catch
            {
                // ignore
            }
        }

        TryRebuildEscortTab();
        WriteLog("zy-lingtang restored escort queue n=" + _escortQueue.Count);
    }

    private static void TickZhongyuanAllWaitArrive(string name, long now)
    {
        if (string.Equals(name, ZhongyuanPetC, StringComparison.Ordinal))
        {
            if (_skCNavActive)
            {
                _zyAllNote = "骷髅战士路 " + (_skCNavNote ?? "");
                return;
            }

            if (!_skCNavLastOk)
            {
                StopZhongyuanAll("骷髅战士导航未到达");
                return;
            }

            _skCNavForAll = false;
            _zyLastCatchOk = false;
            _zyAllCatchStarted = false;
            _zyAllPhase = ZyAllCatchWait;
            _zyAllDelayUntilMs = now + SkCAbortWaitMs;
            _zyAllNote = "已到刷点，准备抓" + name;
            return;
        }

        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        var arrived = _zyAllExpectFloor > 0
            ? floor == _zyAllExpectFloor
            : floor > 0 && floor != FloraHealReturnFloor;
        if (arrived)
        {
            if (string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal))
            {
                HandOffLingTangFromEscort("arrived-floor-" + floor);
            }

            _zyAllNote = "已到刷点 图" + floor;
            _zyLastCatchOk = false;
            _zyAllCatchStarted = false;
            _zyAllPhase = ZyAllCatchWait;
            _zyAllDelayUntilMs = now + SkCAbortWaitMs;
            return;
        }

        _zyAllNote = "等待落地 图" + floor;
        var timeout = string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal)
            ? ZhongyuanHangupGoTimeoutMs
            : ZhongyuanHangupTeleportTimeoutMs;
        if (_zyAllWaitStartMs > 0 && now - _zyAllWaitStartMs >= timeout)
        {
            StopZhongyuanAll((string.Equals(name, ZhongyuanPetA, StringComparison.Ordinal)
                ? "前往超时 图"
                : "传送超时 图") + floor);
        }
    }

    private static void BeginZhongyuanAllCatch(string name)
    {
        _wildPetName = name;
        _zyLastCatchOk = false;
        StartZhongyuanCatch();
        if (!_zyCatchActive)
        {
            StopZhongyuanAll("未能开始抓" + name);
        }
    }

    private static void TickZhongyuanAllCatchWait(string name)
    {
        if (!_zyCatchActive && !_zyLastCatchOk)
        {
            if (_zyAllCatchStarted)
            {
                StopZhongyuanAll("抓" + name + "失败");
                return;
            }

            _zyAllCatchStarted = true;
            _zyAllNote = "开始抓" + name;
            BeginZhongyuanAllCatch(name);
            return;
        }

        if (_zyCatchActive)
        {
            _zyAllNote = "抓" + name + " " + (_zyNote ?? "");
            return;
        }

        if (!_zyLastCatchOk)
        {
            StopZhongyuanAll("抓" + name + "失败");
            return;
        }

        _zyLastXferOk = false;
        _zyAllXferStarted = false;
        _wildPetName = name;
        _zyAllPhase = ZyAllXferWait;
        _zyAllNote = "倒腾" + name + "给" + FormatTeamSlotLabel(GetZhongyuanDestSlot(name));
        Tip("中元抓齐：倒腾" + name + "给" + FormatTeamSlotLabel(GetZhongyuanDestSlot(name)));
    }

    private static void TickZhongyuanAllXferWait(string name)
    {
        if (!_zyXferActive && !_zyLastXferOk)
        {
            if (_zyAllXferStarted)
            {
                StopZhongyuanAll("倒腾" + name + "失败");
                return;
            }

            _zyAllXferStarted = true;
            StartZhongyuanTransfer();
            if (!_zyXferActive)
            {
                StopZhongyuanAll("未能开始倒腾" + name);
            }

            return;
        }

        if (_zyXferActive)
        {
            _zyAllNote = "倒腾" + name + " " + (_zyNote ?? "");
            return;
        }

        if (!_zyLastXferOk)
        {
            StopZhongyuanAll("倒腾" + name + "失败");
            return;
        }

        AdvanceZhongyuanAllAfterType();
    }

    private static int FindHangupNavIdByKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword))
        {
            return 0;
        }

        try
        {
            var cfgMgr = GetManagerInstance("ConfigManager");
            var getTb = cfgMgr?.GetType().GetMethod(
                "GetTbAutoBattleNavigationConfig",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var tb = getTb?.Invoke(cfgMgr, null);
            if (tb == null)
            {
                return 0;
            }

            var list = GetProp(tb, "DataList") as IList ?? GetMember(tb, "DataList") as IList;
            if (list == null)
            {
                return 0;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var row = list[i];
                if (row == null)
                {
                    continue;
                }

                var n = Convert.ToString(GetProp(row, "Name") ?? GetMember(row, "Name") ?? "") ?? "";
                if (n.IndexOf(keyword, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var id = Convert.ToInt32(GetProp(row, "Id") ?? GetMember(row, "Id") ?? 0);
                if (id > 0)
                {
                    WriteLog("zy-all nav match name=" + n + " id=" + id);
                    return id;
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("zy-all FindHangupNav EX " + RootMessage(ex));
        }

        return 0;
    }

    private static void ToggleZhongyuanTransfer()
    {
        if (_zyXferActive)
        {
            StopZhongyuanTransfer("已手动停止", false);
            return;
        }

        StartZhongyuanTransfer();
    }

    private static void StartZhongyuanCatch()
    {
        string name;
        if (_zyAllActive)
        {
            name = WildPetPresets[_zyAllIndex];
            _wildPetName = name;
        }
        else
        {
            CaptureWildPetNameFromUi();
            name = GetWildPetName();
        }

        if (!IsZhongyuanPetName(name))
        {
            Tip("抓宠只支持幽灵/僵尸/骷髅战士");
            return;
        }

        if (!_zyAllActive && (_wildExActive || _zyXferActive))
        {
            Tip("请先停兑换或倒腾");
            return;
        }

        var local = GetLocalTeamSlot();
        if (local > 0)
        {
            Tip("请用1号抓宠");
            return;
        }

        if (!ApplyCatchWildFromScript(name))
        {
            return;
        }

        _zyCatchActive = true;
        _zyCatchPhase = ZyCatchOpenStore;
        _zyName = name;
        _zyDestSlot = GetZhongyuanDestSlot(name);
        _zyNote = "先数个人仓";
        _zyDelayUntilMs = 0;
        _zyWaitListStartMs = 0;
        _zyPersonalMatch = 0;
        _zyWorkIndexes.Clear();
        _zyWorkPos = 0;
        _zyCatchStuckStoreIndex = -1;
        _zyCatchStuckStoreCount = 0;
        if (!_zyAllActive)
        {
            _zyCatchFill = ZhongyuanQuota;
            _zyDestHave = 0;
        }

        TrySendLocalAutoBattle("停止挂机");
        Tip("中元抓宠已开启：" + name + " 凑" + GetZyCatchFill() + "只");
        WriteLog("zy-catch start name=" + name + " dest=" + FormatTeamSlotLabel(_zyDestSlot));
        RefreshScriptTabIfVisible();
    }

    private static void StopZhongyuanCatch(string reason, bool success)
    {
        if (!_zyCatchActive && _zyCatchPhase == ZyCatchIdle)
        {
            return;
        }

        TrySendLocalAutoBattle("停止挂机");
        TrySetAutoCatchWild(false, "");
        TrySetFeatureEnabled("SeqChapterAutoCatch", "hotfixdata/SeqChapterAutoCatch.dll.bytes", false);
        _battleMode = ModeNormal;
        _zyCatchActive = false;
        _zyCatchPhase = ZyCatchIdle;
        _zyNote = reason ?? "";
        _zyLastCatchOk = success;
        _zyWorkIndexes.Clear();
        TryDismissBankUiAfterStore();
        WriteLog("zy-catch stop " + reason + " ok=" + success);
        RefreshScriptTabIfVisible();
    }

    private static void StartZhongyuanTransfer()
    {
        var local = GetLocalTeamSlot();
        string name;
        if (local == ZhongyuanSlotPetA)
        {
            name = ZhongyuanPetA;
        }
        else if (local == ZhongyuanSlotPetB)
        {
            name = ZhongyuanPetB;
        }
        else if (local == ZhongyuanSlotPetC)
        {
            name = ZhongyuanPetC;
        }
        else if (_zyAllActive)
        {
            name = WildPetPresets[_zyAllIndex];
        }
        else
        {
            CaptureWildPetNameFromUi();
            name = GetWildPetName();
        }

        _wildPetName = name;

        if (!IsZhongyuanPetName(name))
        {
            Tip("倒腾只支持幽灵/僵尸/骷髅战士");
            return;
        }

        if (!_zyAllActive && (_wildExActive || _zyCatchActive))
        {
            Tip("请先停兑换或抓宠");
            return;
        }

        var dest = GetZhongyuanDestSlot(name);
        if (local < 0)
        {
            Tip("未找到角色");
            return;
        }

        var push = local == 0;
        var pull = local == dest;
        if (!push && !pull)
        {
            Tip(name + "请用1号交出或" + FormatTeamSlotLabel(dest) + "接收");
            return;
        }

        if (IsInBattleNow())
        {
            Tip("战斗中不能倒腾");
            return;
        }

        TrySendLocalAutoBattle("停止挂机");
        _zyXferActive = true;
        _zyXferPush = push;
        _zyXferWorkUid = "";
        _zyName = name;
        _zyDestSlot = dest;
        _zyPersonalMatch = 0;
        _zyPersonalStart = -1;
        _zyAccountMatch = 0;
        _zyAccountTotal = 0;
        _zyXferSent = 0;
        _zyXferTakeForDrop = false;
        if (!_zyAllActive)
        {
            _zyXferNeed = ZhongyuanQuota;
        }

        _zyWorkIndexes.Clear();
        _zyWorkPos = 0;
        _zyDelayUntilMs = 0;
        if (push)
        {
            _zyXferPhase = ZyXferPushTrimBody;
            _zyNote = "丢掉1号多余再交出" + name;
            Tip("中元倒腾：先丢掉1号多余，再把" + name + "交给" + FormatTeamSlotLabel(dest) + "（远程代收）");
        }
        else
        {
            _zyXferPhase = ZyXferPullOpenPersonal;
            _zyNote = FormatTeamSlotLabel(local) + "接收" + name;
            Tip("中元倒腾：" + FormatTeamSlotLabel(local) + "从账号仓收" + name);
        }

        WriteLog("zy-xfer start name=" + name + " push=" + push + " local=" + local + " dest=" + dest);
        RefreshScriptTabIfVisible();
    }

    private static void StopZhongyuanTransfer(string reason, bool success)
    {
        if (!_zyXferActive && _zyXferPhase == ZyXferIdle)
        {
            return;
        }

        _zyXferActive = false;
        _zyXferPhase = ZyXferIdle;
        _zyNote = reason ?? "";
        _zyLastXferOk = success;
        _zyWorkIndexes.Clear();
        _zyXferWorkUid = "";
        TryDismissBankUiAfterStore();
        WriteLog("zy-xfer stop " + reason + " ok=" + success);
        RefreshScriptTabIfVisible();
    }

    private static void TickZhongyuanCatch()
    {
        if (!_zyCatchActive)
        {
            return;
        }

        var now = NowMs();
        if (_zyDelayUntilMs > 0 && now < _zyDelayUntilMs)
        {
            return;
        }

        _zyDelayUntilMs = 0;
        if (IsInBattleNow())
        {
            return;
        }

        var uid = GetMainPlayerUidSafe();
        if (string.IsNullOrEmpty(uid))
        {
            StopZhongyuanCatch("no-uid", false);
            Tip("未找到角色");
            return;
        }

        switch (_zyCatchPhase)
        {
            case ZyCatchHunt:
                TickZhongyuanCatchHunt(uid);
                break;
            case ZyCatchOpenStore:
                ZhongyuanOpenBank(uid, false);
                _zyCatchPhase = ZyCatchWaitStore;
                _zyDelayUntilMs = now + 400;
                break;
            case ZyCatchWaitStore:
                TickZhongyuanCatchWaitStore(uid, now);
                break;
            case ZyCatchStore:
                TickZhongyuanCatchStore(uid, now);
                break;
        }
    }

    private static void TickZhongyuanCatchHunt(string uid)
    {
        var rest = CountMatchingRestPets(uid, _zyName);
        var bagFull = CountLocalPetFreeSlots(uid) <= 0;
        var keep = GetZyCatchFill() - _zyPersonalMatch;
        if (keep < 0)
        {
            keep = 0;
        }

        string dropped;
        var drop = DropZhongyuanExtraOnce(uid, _zyName, keep, out dropped);
        if (drop < 0)
        {
            StopZhongyuanCatch("drop-fail", false);
            Tip("丢弃多余宠物失败");
            return;
        }

        if (drop > 0)
        {
            _zyNote = "丢弃多余" + dropped;
            _zyDelayUntilMs = NowMs() + ZhongyuanProtocolGapMs;
            return;
        }

        if (bagFull || _zyPersonalMatch + rest >= GetZyCatchFill())
        {
            TrySendLocalAutoBattle("停止挂机");
            _zyNote = bagFull ? "栏满，存个人仓" : "已够" + GetZyCatchFill() + "，存身上剩余";
            _zyCatchPhase = ZyCatchOpenStore;
            return;
        }

        if (GetEncounterStatus() == 0)
        {
            TrySendLocalAutoBattle("开始挂机");
        }

        _zyNote = "挂机抓" + _zyName + " 仓" + _zyPersonalMatch + "+身" + rest
                  + "/" + GetZyCatchFill();
    }

    private static void TickZhongyuanCatchWaitStore(string uid, long now)
    {
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(_zyName, _zyInfoBefore, _zyStoreBefore, out matching, out total))
        {
            if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanCatch("personal-timeout", false);
                Tip("打开个人宠物仓库失败");
            }

            return;
        }

        _zyPersonalMatch = matching == null ? 0 : matching.Count;
        var rest = CollectMatchingRestPetIndexes(uid, _zyName);
        var bagFull = CountLocalPetFreeSlots(uid) <= 0;
        var fill = GetZyCatchFill();
        var need = fill - _zyPersonalMatch;
        if (need < 0)
        {
            need = 0;
        }

        var sum = _zyPersonalMatch + rest.Count;
        int occ;
        int cap;
        int bankFree;
        var spaceKnown = TryGetOpenBankSpace(out occ, out cap, out bankFree);
        _zyNote = "仓" + _zyPersonalMatch + " 身休" + rest.Count + "/" + fill;
        if (need <= 0)
        {
            string dropped;
            var drop = DropZhongyuanExtraOnce(uid, _zyName, 0, out dropped);
            if (drop < 0)
            {
                FinishZhongyuanCatch();
                return;
            }

            if (drop > 0)
            {
                _zyNote = "已够" + fill + "，丢弃多余" + dropped;
                _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
                return;
            }

            FinishZhongyuanCatch();
            return;
        }

        if (spaceKnown && bankFree <= 0)
        {
            if (rest.Count > 0)
            {
                if (!TrySendDropPet(uid, rest[0]))
                {
                    StopZhongyuanCatch("personal-full", false);
                    Tip("个人仓没有空位，无法继续存" + _zyName);
                    return;
                }

                MarkPetUnusedByIndex(uid, rest[0]);
                _zyNote = "个人仓已满，丢弃多余";
                _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
                return;
            }

            StopZhongyuanCatch("personal-full", false);
            Tip("个人仓没有空位，无法继续存" + _zyName);
            return;
        }

        if (rest.Count > 0 && (bagFull || sum >= fill))
        {
            if (rest[0] == _zyCatchStuckStoreIndex)
            {
                _zyCatchStuckStoreCount++;
            }
            else
            {
                _zyCatchStuckStoreIndex = rest[0];
                _zyCatchStuckStoreCount = 1;
            }

            if (_zyCatchStuckStoreCount >= 3)
            {
                if (!TrySendDropPet(uid, rest[0]))
                {
                    StopZhongyuanCatch("store-stuck", false);
                    Tip("存仓卡住，丢弃多余失败");
                    return;
                }

                MarkPetUnusedByIndex(uid, rest[0]);
                _zyCatchStuckStoreIndex = -1;
                _zyCatchStuckStoreCount = 0;
                _zyNote = "同一只存不进，丢弃多余";
                _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
                return;
            }

            var storeN = rest.Count;
            if (storeN > need)
            {
                storeN = need;
            }

            if (spaceKnown && storeN > bankFree)
            {
                storeN = bankFree;
            }

            if (storeN <= 0)
            {
                if (!TrySendDropPet(uid, rest[0]))
                {
                    FinishZhongyuanCatch();
                    return;
                }

                _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
                return;
            }

            _zyWorkIndexes.Clear();
            for (var i = 0; i < storeN; i++)
            {
                _zyWorkIndexes.Add(rest[i]);
            }

            _zyWorkPos = 0;
            _zyCatchPhase = ZyCatchStore;
            return;
        }

        if (sum >= fill)
        {
            FinishZhongyuanCatch();
            return;
        }

        EnterZhongyuanCatchHunt();
    }

    private static void EnterZhongyuanCatchHunt()
    {
        TryDismissBankUiAfterStore();
        _zyCatchPhase = ZyCatchHunt;
    }

    private static void TickZhongyuanCatchStore(string uid, long now)
    {
        if (_zyPersonalMatch >= GetZyCatchFill() || _zyWorkPos >= _zyWorkIndexes.Count)
        {
            _zyCatchPhase = ZyCatchOpenStore;
            return;
        }

        var index = _zyWorkIndexes[_zyWorkPos];
        if (!TrySendBankPet(false, uid, "存宠物", index))
        {
            StopZhongyuanCatch("store-fail", false);
            Tip("存个人仓失败");
            return;
        }

        MarkPetUnusedByIndex(uid, index);
        _zyPersonalMatch++;
        _zyWorkPos++;
        _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
    }

    private static void FinishZhongyuanCatch()
    {
        var name = _zyName;
        var dest = FormatTeamSlotLabel(_zyDestSlot);
        StopZhongyuanCatch("ok", true);
        if (!_zyAllActive)
        {
            Tip("已抓够" + GetZyCatchFill() + "只" + name + "，请1号和" + dest + "点中元倒腾");
        }
        else
        {
            Tip("已抓够" + GetZyCatchFill() + "只" + name);
        }
    }

    private static void TickZhongyuanTransfer()
    {
        if (!_zyXferActive)
        {
            return;
        }

        var now = NowMs();
        if (_zyDelayUntilMs > 0 && now < _zyDelayUntilMs)
        {
            return;
        }

        _zyDelayUntilMs = 0;
        if (IsInBattleNow())
        {
            return;
        }

        var uid = GetMainPlayerUidSafe();
        if (!_zyXferPush && !string.IsNullOrEmpty(_zyXferWorkUid))
        {
            uid = _zyXferWorkUid;
        }

        if (string.IsNullOrEmpty(uid))
        {
            StopZhongyuanTransfer("no-uid", false);
            Tip("未找到角色");
            return;
        }

        if (_zyXferPush)
        {
            TickZhongyuanPush(uid, now);
        }
        else
        {
            TickZhongyuanPull(uid, now);
        }
    }

    private static void TickZhongyuanPush(string uid, long now)
    {
        switch (_zyXferPhase)
        {
            case ZyXferPushTrimBody:
                TickZhongyuanPushTrimBody(uid, now);
                break;
            case ZyXferPushOpenPersonal:
                ZhongyuanOpenBank(uid, false);
                _zyXferPhase = ZyXferPushWaitPersonal;
                _zyDelayUntilMs = now + 400;
                break;
            case ZyXferPushWaitPersonal:
                TickZhongyuanPushWaitPersonal(uid, now);
                break;
            case ZyXferPushTake:
                TickZhongyuanPushTake(uid, now);
                break;
            case ZyXferPushOpenAccount:
                ZhongyuanOpenBank(uid, true);
                _zyXferPhase = ZyXferPushWaitAccount;
                _zyDelayUntilMs = now + 400;
                break;
            case ZyXferPushWaitAccount:
                TickZhongyuanPushWaitAccount(uid, now);
                break;
            case ZyXferPushStore:
                TickZhongyuanPushStore(uid, now);
                break;
            case ZyXferPushWaitEmpty:
                TickZhongyuanPushWaitEmpty(uid, now);
                break;
            case ZyXferDrop:
                TickZhongyuanDrop(uid, now, true);
                break;
        }
    }

    private static void TickZhongyuanPushTrimBody(string uid, long now)
    {
        var keep = GetZyXferNeed() - _zyXferSent;
        if (keep < 0)
        {
            keep = 0;
        }

        string dropped;
        var drop = DropZhongyuanExtraOnce(uid, _zyName, keep, out dropped);
        if (drop < 0)
        {
            StopZhongyuanTransfer("drop-fail", false);
            Tip("丢弃多余宠物失败");
            return;
        }

        if (drop > 0)
        {
            _zyNote = "丢掉1号多余" + dropped;
            _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
            return;
        }

        _zyXferPhase = ZyXferPushOpenPersonal;
        _zyNote = "1号交出" + _zyName;
    }

    private static void TickZhongyuanPushWaitPersonal(string uid, long now)
    {
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(_zyName, _zyInfoBefore, _zyStoreBefore, out matching, out total))
        {
            if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanTransfer("personal-timeout", false);
                Tip("打开个人宠物仓库失败");
            }

            return;
        }

        _zyPersonalMatch = matching == null ? 0 : matching.Count;
        var rest = CollectMatchingRestPetIndexes(uid, _zyName);
        var still = GetZyXferNeed() - _zyXferSent;
        if (still < 0)
        {
            still = 0;
        }

        if (still <= 0)
        {
            BeginZhongyuanDropExtras(uid, matching, rest, "已交够" + GetZyXferNeed() + "，丢弃多余");
            return;
        }

        if (_zyPersonalMatch <= 0 && rest.Count <= 0)
        {
            _zyXferPhase = ZyXferDrop;
            _zyNote = "已交完，丢弃多余";
            return;
        }

        var free = CountLocalPetFreeSlots(uid);
        var already = rest.Count;
        var takeN = Math.Min(free, Math.Min(_zyPersonalMatch, Math.Min(ZhongyuanAccountSlots - already, still - already)));
        if (takeN < 0)
        {
            takeN = 0;
        }

        if (takeN <= 0 && already > 0)
        {
            _zyXferPhase = ZyXferPushOpenAccount;
            _zyNote = "身上已有" + already + "只，改存账号仓";
            return;
        }

        if (takeN <= 0)
        {
            var keep = GetZyXferNeed() - _zyXferSent;
            if (keep < 0)
            {
                keep = 0;
            }

            string dropped;
            var drop = DropZhongyuanExtraOnce(uid, _zyName, keep, out dropped, true);
            if (drop < 0)
            {
                StopZhongyuanTransfer("drop-fail", false);
                Tip("丢弃多余宠物失败");
                return;
            }

            if (drop > 0)
            {
                _zyNote = "栏满，丢弃多余" + dropped;
                _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
                return;
            }

            StopZhongyuanTransfer("no-slot", false);
            Tip("身上没有空位，无法倒腾");
            return;
        }

        _zyWorkIndexes.Clear();
        for (var i = 0; i < takeN && i < matching.Count; i++)
        {
            _zyWorkIndexes.Add(matching[i]);
        }

        _zyWorkPos = 0;
        _zyXferPhase = ZyXferPushTake;
        _zyNote = "个人仓取出" + takeN + "只";
    }

    private static void TickZhongyuanPushTake(string uid, long now)
    {
        if (_zyWorkPos >= _zyWorkIndexes.Count)
        {
            _zyXferPhase = _zyXferTakeForDrop ? ZyXferDrop : ZyXferPushOpenAccount;
            _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
            return;
        }

        var bankIndex = _zyWorkIndexes[_zyWorkPos];
        if (!TrySendBankPet(false, uid, "取宠物", bankIndex))
        {
            StopZhongyuanTransfer("take-fail", false);
            Tip("从个人仓取出失败");
            return;
        }

        _zyWorkPos++;
        _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
    }

    private static void TickZhongyuanPushWaitAccount(string uid, long now)
    {
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(
                _zyName, _zyInfoBefore, _zyStoreBefore, out matching, out total, true))
        {
            if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanTransfer("account-timeout", false);
                Tip("打开账号宠物仓库失败");
            }

            return;
        }

        _zyAccountMatch = matching == null ? 0 : matching.Count;
        _zyAccountTotal = total;
        var rest = CollectMatchingRestPetIndexes(uid, _zyName);
        var still = GetZyXferNeed() - _zyXferSent;
        if (still < 0)
        {
            still = 0;
        }

        if (still <= 0)
        {
            _zyXferPhase = ZyXferPushOpenPersonal;
            _zyNote = "已交够，处理多余";
            return;
        }

        var room = ZhongyuanAccountSlots - _zyAccountTotal;
        if (room < 0)
        {
            room = 0;
        }

        if (rest.Count <= 0)
        {
            if (_zyXferSent <= 0)
            {
                if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
                {
                    StopZhongyuanTransfer("dest-bank-full", false);
                    Tip("倒腾中断：身上没有" + _zyName + "，请检查"
                        + FormatTeamSlotLabel(_zyDestSlot) + "个人仓是否有"
                        + ZhongyuanQuota + "格空位");
                    return;
                }

                _zyNote = "等待身上" + _zyName;
                _zyDelayUntilMs = now + ZhongyuanPollMs;
                return;
            }

            if (_zyAccountMatch > 0)
            {
                BeginZhongyuanRemotePull();
                return;
            }

            _zyXferPhase = ZyXferPushOpenPersonal;
            return;
        }

        if (room <= 0)
        {
            BeginZhongyuanRemotePull();
            return;
        }

        var storeN = Math.Min(rest.Count, Math.Min(room, still));
        if (storeN <= 0)
        {
            _zyXferPhase = ZyXferDrop;
            _zyNote = "已交够，丢弃多余";
            return;
        }

        _zyWorkIndexes.Clear();
        for (var i = 0; i < storeN; i++)
        {
            _zyWorkIndexes.Add(rest[i]);
        }

        _zyWorkPos = 0;
        _zyXferPhase = ZyXferPushStore;
        _zyNote = "存入账号仓" + storeN + "只";
    }

    private static void TickZhongyuanPushStore(string uid, long now)
    {
        if (_zyWorkPos >= _zyWorkIndexes.Count)
        {
            _zyXferPhase = ZyXferPushWaitEmpty;
            _zyNote = "等待" + FormatTeamSlotLabel(_zyDestSlot) + "从账号仓取出";
            _zyDelayUntilMs = now + ZhongyuanPollMs;
            return;
        }

        var index = _zyWorkIndexes[_zyWorkPos];
        if (!TrySendBankPet(true, uid, "存宠物", index))
        {
            StopZhongyuanTransfer("account-store-fail", false);
            Tip("存入账号仓失败");
            return;
        }

        MarkPetUnusedByIndex(uid, index);
        _zyXferSent++;
        _zyWorkPos++;
        _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
    }

    private static void TickZhongyuanPushWaitEmpty(string uid, long now)
    {
        if (_zyXferSent <= 0)
        {
            var rest = CollectMatchingRestPetIndexes(uid, _zyName);
            if (rest.Count > 0)
            {
                _zyXferPhase = ZyXferPushOpenAccount;
                _zyNote = "身上还有" + rest.Count + "只，再存账号仓";
                return;
            }

            StopZhongyuanTransfer("dest-bank-full", false);
            Tip("倒腾中断：账号仓未存入，请检查" + FormatTeamSlotLabel(_zyDestSlot)
                + "个人仓是否有" + ZhongyuanQuota + "格空位");
            return;
        }

        BeginZhongyuanRemotePull();
    }

    private static void BeginZhongyuanRemotePull()
    {
        TryDismissBankUiAfterStore();
        var destUid = GetTeamUidBySlot(_zyDestSlot);
        if (string.IsNullOrEmpty(destUid))
        {
            StopZhongyuanTransfer("no-dest", false);
            Tip("倒腾中断：队伍里没有" + FormatTeamSlotLabel(_zyDestSlot));
            return;
        }

        _zyXferPush = false;
        _zyXferWorkUid = destUid;
        _zyXferPhase = ZyXferPullOpenAccount;
        _zyNote = "代" + FormatTeamSlotLabel(_zyDestSlot) + "从账号仓收" + _zyName;
        WriteLog("zy-xfer remote-pull dest=" + FormatTeamSlotLabel(_zyDestSlot)
                 + " uid尾" + TailUid(destUid));
    }

    private static void TickZhongyuanPull(string uid, long now)
    {
        switch (_zyXferPhase)
        {
            case ZyXferPullOpenPersonal:
                ZhongyuanOpenBank(uid, false);
                _zyXferPhase = ZyXferPullWaitPersonal;
                _zyDelayUntilMs = now + 400;
                break;
            case ZyXferPullWaitPersonal:
                TickZhongyuanPullWaitPersonal(uid, now);
                break;
            case ZyXferPullOpenAccount:
                ZhongyuanOpenBank(uid, true);
                _zyXferPhase = ZyXferPullWaitAccount;
                _zyDelayUntilMs = now + 400;
                break;
            case ZyXferPullWaitAccount:
                TickZhongyuanPullWaitAccount(uid, now);
                break;
            case ZyXferPullTake:
                TickZhongyuanPullTake(uid, now);
                break;
            case ZyXferPullStore:
                TickZhongyuanPullStore(uid, now);
                break;
            case ZyXferDrop:
                TickZhongyuanDrop(uid, now, false);
                break;
        }
    }

    private static void TickZhongyuanPullWaitPersonal(string uid, long now)
    {
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(_zyName, _zyInfoBefore, _zyStoreBefore, out matching, out total))
        {
            if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanTransfer("personal-timeout", false);
                Tip("打开个人宠物仓库失败");
            }

            return;
        }

        _zyPersonalMatch = matching == null ? 0 : matching.Count;
        if (_zyPersonalStart < 0)
        {
            _zyPersonalStart = _zyPersonalMatch;
        }

        var rest = CollectMatchingRestPetIndexes(uid, _zyName);
        var need = ZhongyuanQuota - _zyPersonalMatch;
        if (need < 0)
        {
            need = 0;
        }

        if (need <= 0)
        {
            _zyXferPhase = ZyXferDrop;
            _zyNote = "仓内已有" + _zyPersonalMatch + "只，丢弃多余";
            return;
        }

        if (rest.Count > 0)
        {
            int occ;
            int cap;
            int bankFree;
            if (!TryGetOpenBankSpace(out occ, out cap, out bankFree) || bankFree <= 0)
            {
                StopZhongyuanTransfer("personal-full", false);
                Tip("个人仓没有空位，无法接收（须空出" + ZhongyuanQuota + "格）");
                return;
            }

            var storeN = Math.Min(rest.Count, Math.Min(need, bankFree));
            _zyWorkIndexes.Clear();
            for (var i = 0; i < storeN; i++)
            {
                _zyWorkIndexes.Add(rest[i]);
            }

            _zyWorkPos = 0;
            _zyXferPhase = ZyXferPullStore;
            _zyNote = "存入个人仓" + storeN + "只";
            return;
        }

        _zyXferPhase = ZyXferPullOpenAccount;
        _zyNote = "等待账号仓" + _zyName;
    }

    private static void TickZhongyuanPullWaitAccount(string uid, long now)
    {
        List<int> matching;
        int total;
        if (!TryCollectOpenBankPets(
                _zyName, _zyInfoBefore, _zyStoreBefore, out matching, out total, true))
        {
            if (now - _zyWaitListStartMs >= ZhongyuanWaitListTimeoutMs)
            {
                StopZhongyuanTransfer("account-timeout", false);
                Tip("打开账号宠物仓库失败");
            }

            return;
        }

        _zyAccountMatch = matching == null ? 0 : matching.Count;
        _zyAccountTotal = total;
        var need = ZhongyuanQuota - _zyPersonalMatch;
        if (need < 0)
        {
            need = 0;
        }

        if (need <= 0)
        {
            _zyXferPhase = ZyXferDrop;
            return;
        }

        if (_zyAccountMatch <= 0)
        {
            TryDismissBankUiAfterStore();
            var still = GetZyXferNeed() - _zyXferSent;
            if (!string.IsNullOrEmpty(_zyXferWorkUid) && still > 0)
            {
                _zyXferPush = true;
                _zyXferWorkUid = "";
                _zyXferPhase = ZyXferPushOpenPersonal;
                _zyNote = "账号仓已空，1号继续交出" + _zyName;
                return;
            }

            if (!string.IsNullOrEmpty(_zyXferWorkUid))
            {
                _zyXferPhase = ZyXferPullOpenPersonal;
                _zyNote = "账号仓已空，核对该号个人仓";
                return;
            }

            _zyNote = "等待1号把" + _zyName + "存进账号仓";
            _zyDelayUntilMs = now + ZhongyuanPollMs;
            _zyXferPhase = ZyXferPullOpenAccount;
            return;
        }

        var free = CountLocalPetFreeSlots(uid);
        var takeN = Math.Min(free, Math.Min(_zyAccountMatch, need));
        if (takeN <= 0)
        {
            StopZhongyuanTransfer("no-slot", false);
            Tip("身上没有空位，无法接收");
            return;
        }

        _zyWorkIndexes.Clear();
        for (var i = 0; i < takeN && matching != null && i < matching.Count; i++)
        {
            _zyWorkIndexes.Add(matching[i]);
        }

        _zyWorkPos = 0;
        _zyXferPhase = ZyXferPullTake;
        _zyNote = "账号仓取出" + takeN + "只";
    }

    private static void TickZhongyuanPullTake(string uid, long now)
    {
        if (_zyWorkPos >= _zyWorkIndexes.Count)
        {
            _zyXferPhase = ZyXferPullOpenPersonal;
            _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
            return;
        }

        var bankIndex = _zyWorkIndexes[_zyWorkPos];
        if (!TrySendBankPet(true, uid, "取宠物", bankIndex))
        {
            StopZhongyuanTransfer("take-fail", false);
            Tip("从账号仓取出失败");
            return;
        }

        _zyWorkPos++;
        _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
    }

    private static void TickZhongyuanPullStore(string uid, long now)
    {
        if (_zyWorkPos >= _zyWorkIndexes.Count)
        {
            _zyXferPhase = ZyXferPullOpenPersonal;
            return;
        }

        var index = _zyWorkIndexes[_zyWorkPos];
        if (!TrySendBankPet(false, uid, "存宠物", index))
        {
            StopZhongyuanTransfer("store-fail", false);
            Tip("存入个人仓失败");
            return;
        }

        MarkPetUnusedByIndex(uid, index);
        _zyPersonalMatch++;
        _zyWorkPos++;
        _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
    }

    private static void BeginZhongyuanDropExtras(
        string uid, List<int> matching, List<int> rest, string note)
    {
        var bodyN = rest == null ? 0 : rest.Count;
        var bankN = matching == null ? 0 : matching.Count;
        if (bodyN <= 0 && bankN <= 0)
        {
            _zyXferTakeForDrop = false;
            StopZhongyuanTransfer("ok", true);
            Tip("倒腾完成：已把" + _zyName + "交给" + FormatTeamSlotLabel(_zyDestSlot));
            return;
        }

        _zyNote = note ?? "丢弃多余";
        if (bodyN > 0)
        {
            _zyXferTakeForDrop = false;
            _zyXferPhase = ZyXferDrop;
            return;
        }

        var free = CountLocalPetFreeSlots(uid);
        var takeN = Math.Min(free, bankN);
        if (takeN <= 0)
        {
            StopZhongyuanTransfer("no-slot-drop", false);
            Tip("身上没空位，个人仓还有多余" + _zyName);
            return;
        }

        _zyXferTakeForDrop = true;
        _zyWorkIndexes.Clear();
        for (var i = 0; i < takeN; i++)
        {
            _zyWorkIndexes.Add(matching[i]);
        }

        _zyWorkPos = 0;
        _zyXferPhase = ZyXferPushTake;
        _zyNote = (note ?? "丢弃多余") + "，先取出" + takeN;
    }

    private static void TickZhongyuanDrop(string uid, long now, bool fromPush)
    {
        string dropped;
        var drop = DropZhongyuanExtraOnce(uid, _zyName, 0, out dropped);
        if (drop < 0)
        {
            StopZhongyuanTransfer("drop-fail", false);
            Tip("丢弃多余宠物失败");
            return;
        }

        if (drop > 0)
        {
            _zyNote = "丢弃多余" + dropped;
            _zyDelayUntilMs = now + ZhongyuanProtocolGapMs;
            return;
        }

        if (fromPush)
        {
            ZhongyuanOpenBank(uid, false);
            _zyXferPhase = ZyXferPushWaitPersonal;
            _zyDelayUntilMs = now + 400;
            _zyNote = "再扫个人仓丢多余";
            return;
        }

        var name = _zyName;
        StopZhongyuanTransfer("ok", true);
        Tip("倒腾完成：" + name + "个人仓" + _zyPersonalMatch + "只");
    }

    private static void ZhongyuanOpenBank(string uid, bool account)
    {
        TryDismissBankUiAfterStore();
        MarkPendingBankOpen(uid, account);
        _zyInfoBefore = TryGetPetStorageInfo();
        _zyStoreBefore = TryGetBankStorePetInfo();
        var openUid = uid;
        if (account)
        {
            var captainUid = GetMainPlayerUidSafe();
            if (!string.IsNullOrEmpty(captainUid))
            {
                openUid = captainUid;
            }

            TryOpenRemoteAccountPetBank(openUid);
        }
        else
        {
            TryOpenRemotePersonalPetBank(uid);
        }

        _zyWaitListStartMs = NowMs();
        WriteLog("zy-loop OpenBank account=" + account
                 + " uid尾" + TailUid(uid)
                 + " openUid尾" + TailUid(openUid)
                 + " waitFrom=" + _zyWaitListStartMs);
    }

    private static void TrySendLocalAutoBattle(string action)
    {
        try
        {
            var uid = GetMainPlayerUidSafe();
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

    private static bool PetNameMatches(object petInfo, string target)
    {
        if (petInfo == null || string.IsNullOrEmpty(target))
        {
            return false;
        }

        var name = Convert.ToString(GetMember(petInfo, "Name") ?? "") ?? "";
        var free = Convert.ToString(GetMember(petInfo, "FreeName") ?? "") ?? "";
        return string.Equals(name, target, StringComparison.Ordinal)
               || string.Equals(free, target, StringComparison.Ordinal);
    }

    private static List<int> CollectMatchingRestPetIndexes(string uid, string targetName)
    {
        var list = new List<int>();
        try
        {
            var pets = GetPetListByUid(uid);
            if (pets == null || string.IsNullOrEmpty(targetName))
            {
                return list;
            }

            for (var i = 0; i < pets.Count && i < 5; i++)
            {
                var pet = pets[i];
                if (pet == null || Convert.ToInt32(GetMember(pet, "useFlag") ?? 0) != 1)
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

                if (!PetNameMatches(data, targetName))
                {
                    continue;
                }

                list.Add(Convert.ToInt32(GetMember(data, "Index") ?? i));
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static int CountMatchingRestPets(string uid, string targetName)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(targetName))
        {
            return 0;
        }

        return CollectMatchingRestPetIndexes(uid, targetName).Count;
    }

    /// <summary>
    /// 丢掉身上多余的中元休息宠。当前种只保留 keepCount 只；dropOthers 时其它两种也丢（栏满解卡）。
    /// 返回 1=已丢一只，0=没有多余，-1=发包失败。
    /// </summary>
    private static int DropZhongyuanExtraOnce(
        string uid, string keepName, int keepCount, out string droppedName, bool dropOthers = false)
    {
        droppedName = "";
        int index;
        if (!TryPickZhongyuanExtraToDrop(uid, keepName, keepCount, out index, out droppedName)
            && !(dropOthers && TryPickOtherZhongyuanRest(uid, keepName, out index, out droppedName)))
        {
            return 0;
        }

        if (!TrySendDropPet(uid, index))
        {
            return -1;
        }

        MarkPetUnusedByIndex(uid, index);
        WriteLog("zy drop extra " + droppedName + " idx=" + index
                 + " keep=" + keepName + " keepN=" + keepCount + " others=" + dropOthers);
        return 1;
    }

    private static bool TryPickZhongyuanExtraToDrop(
        string uid, string keepName, int keepCount, out int index, out string name)
    {
        index = -1;
        name = "";
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(keepName))
        {
            return false;
        }

        if (keepCount < 0)
        {
            keepCount = 0;
        }

        var keep = CollectMatchingRestPetIndexes(uid, keepName);
        if (keep.Count <= keepCount)
        {
            return false;
        }

        index = keep[keepCount];
        name = keepName;
        return true;
    }

    private static bool TryPickOtherZhongyuanRest(
        string uid, string keepName, out int index, out string name)
    {
        index = -1;
        name = "";
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        for (var i = 0; i < WildPetPresets.Length; i++)
        {
            var n = WildPetPresets[i];
            if (!string.IsNullOrEmpty(keepName)
                && string.Equals(n, keepName, StringComparison.Ordinal))
            {
                continue;
            }

            var other = CollectMatchingRestPetIndexes(uid, n);
            if (other.Count <= 0)
            {
                continue;
            }

            index = other[0];
            name = n;
            return true;
        }

        return false;
    }

    private static void MarkPetUnusedByIndex(string uid, int index)
    {
        try
        {
            var pets = GetPetListByUid(uid);
            if (pets == null)
            {
                return;
            }

            for (var i = 0; i < pets.Count && i < 5; i++)
            {
                var pet = pets[i];
                if (pet == null)
                {
                    continue;
                }

                var data = GetMember(pet, "data");
                if (data == null)
                {
                    continue;
                }

                if (Convert.ToInt32(GetMember(data, "Index") ?? -1) != index)
                {
                    continue;
                }

                SetMember(pet, "useFlag", 0);
                return;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool TrySendBankPet(bool account, string uid, string action, int index)
    {
        var roleMgr = GetManagerInstance("RoleManager");
        var bankType = account ? ResolveAccountBankType() : ResolvePersonalBankType();
        if (roleMgr == null || bankType == null || string.IsNullOrEmpty(uid))
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

        try
        {
            var ps = sendBank.GetParameters();
            object[] args;
            if (ps.Length >= 6)
            {
                args = new object[] { bankType, uid, action, index, 0, null };
            }
            else if (ps.Length == 5)
            {
                args = new object[] { bankType, uid, action, index, 0 };
            }
            else
            {
                args = new object[] { bankType, uid, action, index };
            }

            sendBank.Invoke(roleMgr, args);
            WriteLog("zy bank " + (account ? "account" : "personal") + " " + action + " idx=" + index);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("zy bank EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TrySendDropPet(string uid, int petIndex)
    {
        try
        {
            var petMgr = GetManagerInstance("PetManager");
            if (petMgr == null)
            {
                return false;
            }

            MethodInfo sendDrop = null;
            foreach (var m in petMgr.GetType().GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "SendDropPet")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 2)
                {
                    sendDrop = m;
                    break;
                }
            }

            if (sendDrop == null)
            {
                return false;
            }

            sendDrop.Invoke(petMgr, new object[] { uid, petIndex });
            WriteLog("zy drop pet idx=" + petIndex + " name=" + _zyName);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("zy drop EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryCollectOpenBankPets(
        string targetName, object infoBefore, object storeBefore, out List<int> matching, out int total,
        bool account = false)
    {
        matching = new List<int>();
        total = 0;
        var info = TryGetPetStorageInfo();
        var store = TryGetBankStorePetInfo();
        var fresh = (info != null && !ReferenceEquals(info, infoBefore))
                    || (store != null && !ReferenceEquals(store, storeBefore));
        if (!fresh)
        {
            return false;
        }

        var raw = GetOpenBankRawList(account);
        if (raw == null)
        {
            return false;
        }

        var fp = FingerprintOpenBankRaw(raw);
        if (!account)
        {
            if (!string.IsNullOrEmpty(_zyBankFpBefore) && fp == _zyBankFpBefore)
            {
                return false;
            }

            if (IsStalePersonalBankFp(_zyPendingBankUid, fp))
            {
                WriteLog("stale bank list uid尾" + TailUid(_zyPendingBankUid) + " fp=" + fp);
                return false;
            }
        }

        total = raw.Count;
        if (account && total > ZhongyuanAccountSlots)
        {
            // 个人仓残留列表（常 15 格）被当成账号仓，会误判仓满死循环。
            return false;
        }

        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry == null)
            {
                total--;
                continue;
            }

            var pet = DecodeBankUpdatePet(entry);
            if (pet == null)
            {
                continue;
            }

            if (!PetNameMatches(pet, targetName))
            {
                continue;
            }

            var idx = Convert.ToInt32(GetMember(entry, "Index") ?? -1);
            if (idx < 0)
            {
                continue;
            }

            matching.Add(idx);
        }

        if (account && matching.Count == 0 && total == 0)
        {
            var opened = _wildExWaitListStartMs;
            if (_zyWaitListStartMs > opened)
            {
                opened = _zyWaitListStartMs;
            }

            if (opened > 0 && NowMs() - opened < WildExAccountEmptySettleMs)
            {
                return false;
            }
        }

        if (!account)
        {
            RememberPersonalBankFp(_zyPendingBankUid, fp);
        }

        return true;
    }

    private static void MarkPendingBankOpen(string uid, bool account)
    {
        _zyPendingBankUid = uid ?? "";
        _zyBankFpBefore = account ? "" : FingerprintOpenBankRaw(GetOpenBankRawList(false));
    }

    private static string FingerprintOpenBankRaw(IList raw)
    {
        if (raw == null)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry == null)
            {
                continue;
            }

            var pet = DecodeBankUpdatePet(entry);
            var name = "";
            var level = 0;
            if (pet != null)
            {
                name = (Convert.ToString(GetMember(pet, "FreeName") ?? GetMember(pet, "Name") ?? "") ?? "").Trim();
                level = Convert.ToInt32(GetMember(pet, "Level") ?? 0);
            }

            var idx = Convert.ToInt32(GetMember(entry, "Index") ?? -1);
            sb.Append('|').Append(idx).Append(':').Append(name).Append(':').Append(level);
        }

        return sb.ToString();
    }

    private static bool IsStalePersonalBankFp(string uid, string fp)
    {
        if (string.IsNullOrEmpty(fp) || fp == "0")
        {
            return false;
        }

        for (var i = 0; i < _zyRecentBankFps.Count; i++)
        {
            if (_zyRecentBankFps[i] != fp)
            {
                continue;
            }

            var oldUid = i < _zyRecentBankUids.Count ? _zyRecentBankUids[i] : "";
            if (!string.IsNullOrEmpty(oldUid)
                && !string.Equals(oldUid, uid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RememberPersonalBankFp(string uid, string fp)
    {
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(fp) || fp == "0")
        {
            return;
        }

        for (var i = 0; i < _zyRecentBankUids.Count; i++)
        {
            if (_zyRecentBankUids[i] == uid)
            {
                _zyRecentBankFps[i] = fp;
                return;
            }
        }

        _zyRecentBankUids.Add(uid);
        _zyRecentBankFps.Add(fp);
        while (_zyRecentBankUids.Count > 8)
        {
            _zyRecentBankUids.RemoveAt(0);
            _zyRecentBankFps.RemoveAt(0);
        }
    }

    private static void ClearOpenBankPetLists()
    {
        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            var info = storage != null ? GetMember(storage, "m_Info") : null;
            if (info != null)
            {
                SetMember(info, "UpdatePet", null);
            }

            var bank = GetUiPanel("BankPanel");
            if (bank != null)
            {
                SetMember(bank, "storePetInfo", null);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static IList GetOpenBankRawList(bool account = false)
    {
        IList storeList = null;
        try
        {
            var bank = GetUiPanel("BankPanel");
            storeList = bank != null ? GetMember(bank, "storePetInfo") as IList : null;
        }
        catch
        {
            // ignore
        }

        IList update = null;
        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            var storageInfo = storage != null ? GetMember(storage, "m_Info") : null;
            update = storageInfo != null ? GetMember(storageInfo, "UpdatePet") as IList : null;
        }
        catch
        {
            // ignore
        }

        if (account)
        {
            // 超银最多 5 格。个人仓常 15 格，空的个人仓 Max 也是 15。
            // 不能把 BankPanel 残留的普通银行列表当成超银。
            if (IsLikelyPersonalBankUi(storeList, update))
            {
                return null;
            }

            if (storeList != null && storeList.Count <= ZhongyuanAccountSlots)
            {
                return storeList;
            }

            if (update != null && update.Count <= ZhongyuanAccountSlots)
            {
                return update;
            }

            return null;
        }

        if (update != null)
        {
            return update;
        }

        return storeList;
    }

    private static int TryReadOpenBankMax()
    {
        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            var info = storage != null ? GetMember(storage, "m_Info") : null;
            var bank = GetUiPanel("BankPanel");
            var names = new[]
            {
                "MaxCount", "maxCount", "PetMax", "petMax", "MaxPet", "maxPet",
                "SlotCount", "slotCount", "MaxNum", "maxNum", "Capacity", "capacity",
                "BankMax", "bankMax", "Max", "Size", "size"
            };
            var objs = new[] { info, storage, bank };
            for (var i = 0; i < objs.Length; i++)
            {
                if (objs[i] == null)
                {
                    continue;
                }

                for (var n = 0; n < names.Length; n++)
                {
                    var v = Convert.ToInt32(GetMember(objs[i], names[n]) ?? GetProp(objs[i], names[n]) ?? 0);
                    if (v >= ZhongyuanQuota)
                    {
                        return v;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    /// <summary>当前已打开的宠物仓：占用、容量、空位。列表若是紧凑表且读不到上限，按 15 格计。</summary>
    private static bool TryGetOpenBankSpace(out int occupied, out int capacity, out int free)
    {
        occupied = 0;
        capacity = 0;
        free = 0;
        var raw = GetOpenBankRawList();
        if (raw == null)
        {
            return false;
        }

        var occ = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry == null)
            {
                continue;
            }

            if (DecodeBankUpdatePet(entry) != null)
            {
                occ++;
            }
        }

        occupied = occ;
        capacity = raw.Count;
        var max = TryReadOpenBankMax();
        if (max > capacity)
        {
            capacity = max;
        }

        if (capacity <= occupied)
        {
            capacity = Math.Max(ZhongyuanQuota, occupied);
        }

        if (capacity <= 0)
        {
            capacity = ZhongyuanQuota;
        }

        free = capacity - occupied;
        if (free < 0)
        {
            free = 0;
        }

        return true;
    }

    private static string ListOpenBankOtherPetNames(string allowedName)
    {
        var names = new List<string>();
        IList raw = null;
        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            var storageInfo = storage != null ? GetMember(storage, "m_Info") : null;
            raw = storageInfo != null ? GetMember(storageInfo, "UpdatePet") as IList : null;
        }
        catch
        {
            raw = null;
        }

        if (raw == null)
        {
            try
            {
                var bank = GetUiPanel("BankPanel");
                raw = bank != null ? GetMember(bank, "storePetInfo") as IList : null;
            }
            catch
            {
                raw = null;
            }
        }

        if (raw == null)
        {
            return "";
        }

        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry == null)
            {
                continue;
            }

            var pet = DecodeBankUpdatePet(entry);
            if (pet == null || PetNameMatches(pet, allowedName))
            {
                continue;
            }

            var n = (Convert.ToString(GetMember(pet, "FreeName") ?? "") ?? "").Trim();
            if (n.Length == 0)
            {
                n = (Convert.ToString(GetMember(pet, "Name") ?? "") ?? "").Trim();
            }

            if (n.Length == 0 || names.Contains(n))
            {
                continue;
            }

            names.Add(n);
        }

        return string.Join("、", names.ToArray());
    }

    private static bool IsInBattleNow()
    {
        try
        {
            return Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static int CountLocalPetFreeSlots(string uid)
    {
        var used = 0;
        try
        {
            var pets = GetPetListByUid(uid);
            if (pets != null)
            {
                for (var i = 0; i < pets.Count && i < 5; i++)
                {
                    var p = pets[i];
                    if (p != null && Convert.ToInt32(GetMember(p, "useFlag") ?? 0) == 1)
                    {
                        used++;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return Math.Max(0, 5 - used);
    }

    /// <summary>
    /// 官方签名 SendActivity(type, KUid, id=0, activityId=0, code="", index=0) 共 6 参。
    /// 反射 Invoke 必须把可选参数也填上，否则 TargetParameterCountException。
    /// </summary>
    private static bool TrySendActivity(string type, string uid, int id, int activityId)
    {
        try
        {
            var actMgr = GetManagerInstance("ActivityManager");
            if (actMgr == null)
            {
                WriteLog("SendActivity miss ActivityManager type=" + type);
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
                if (ps.Length < 2
                    || ps[0].ParameterType != typeof(string)
                    || ps[1].ParameterType != typeof(string))
                {
                    continue;
                }

                if (ps.Length >= 6)
                {
                    send = m;
                    break;
                }

                if (send == null)
                {
                    send = m;
                }
            }

            if (send == null)
            {
                WriteLog("SendActivity method miss type=" + type);
                return false;
            }

            var ps2 = send.GetParameters();
            var args = new object[ps2.Length];
            args[0] = type;
            args[1] = uid;
            if (ps2.Length > 2)
            {
                args[2] = id;
            }

            if (ps2.Length > 3)
            {
                args[3] = activityId;
            }

            for (var i = 4; i < ps2.Length; i++)
            {
                if (ps2[i].HasDefaultValue)
                {
                    args[i] = ps2[i].DefaultValue;
                }
                else if (ps2[i].ParameterType == typeof(string))
                {
                    args[i] = "";
                }
                else if (ps2[i].ParameterType == typeof(int))
                {
                    args[i] = 0;
                }
                else
                {
                    args[i] = Type.Missing;
                }
            }

            send.Invoke(actMgr, args);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("SendActivity EX " + type + " " + RootMessage(ex));
            return false;
        }
    }

    private static void TryOpenRemoteAccountPetBank(string uid)
    {
        try
        {
            var roleMgr = GetManagerInstance("RoleManager");
            if (roleMgr != null)
            {
                SetMember(roleMgr, "OpenBankFromPet", true);
            }

            if (!TrySendActivity(AccountPetBankActivity, uid, 0, 19))
            {
                WriteLog("open account pet bank send fail uid尾" + TailUid(uid));
            }
        }
        catch (Exception ex)
        {
            WriteLog("open account pet bank EX " + RootMessage(ex));
        }
    }

    private static object TryGetPetStorageInfo()
    {
        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            return storage != null ? GetMember(storage, "m_Info") : null;
        }
        catch
        {
            return null;
        }
    }

    private static object TryGetBankStorePetInfo()
    {
        try
        {
            var bank = GetUiPanel("BankPanel");
            return bank != null ? GetMember(bank, "storePetInfo") : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAccountBankListFresh()
    {
        var info = TryGetPetStorageInfo();
        if (info != null && !ReferenceEquals(info, _wildExInfoBefore))
        {
            return true;
        }

        var store = TryGetBankStorePetInfo();
        if (store != null && !ReferenceEquals(store, _wildExStoreBefore))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 从 BankPanel / PetStoragePanel 收集账号仓里名字匹配的宠物 Index。
    /// ready=false 表示仓列表还没到。
    /// </summary>
    private static bool TryCollectAccountBankMatchingPets(string targetName, out List<int> indexes)
    {
        indexes = new List<int>();
        if (!IsAccountBankListFresh())
        {
            return false;
        }

        IList raw = null;
        var loaded = false;

        try
        {
            var storage = GetUiPanel("PetStoragePanel");
            var info = storage != null ? GetMember(storage, "m_Info") : null;
            var update = info != null ? GetMember(info, "UpdatePet") as IList : null;
            if (update != null)
            {
                raw = update;
                loaded = true;
            }
        }
        catch
        {
            // ignore
        }

        if (!loaded)
        {
            try
            {
                var bank = GetUiPanel("BankPanel");
                var store = bank != null ? GetMember(bank, "storePetInfo") as IList : null;
                if (store != null)
                {
                    raw = store;
                    loaded = true;
                }
            }
            catch
            {
                // ignore
            }
        }

        if (!loaded)
        {
            return false;
        }

        if (raw == null)
        {
            return true;
        }

        for (var i = 0; i < raw.Count; i++)
        {
            var entry = raw[i];
            if (entry == null)
            {
                continue;
            }

            var pet = DecodeBankUpdatePet(entry);
            if (pet == null)
            {
                continue;
            }

            var name = Convert.ToString(GetMember(pet, "Name") ?? "") ?? "";
            var free = Convert.ToString(GetMember(pet, "FreeName") ?? "") ?? "";
            if (!string.Equals(name, targetName, StringComparison.Ordinal)
                && !string.Equals(free, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            var idx = Convert.ToInt32(GetMember(entry, "Index") ?? -1);
            if (idx < 0)
            {
                continue;
            }

            indexes.Add(idx);
        }

        return true;
    }

    private static object DecodeBankUpdatePet(object entry)
    {
        if (entry == null)
        {
            return null;
        }

        try
        {
            var pet = GetMember(entry, "Pet");
            if (pet != null)
            {
                return pet;
            }

            var bytesObj = GetMember(entry, "PetBytes");
            var bytes = TryToByteArray(bytesObj);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var netType = FindType("NetManager");
            var petType = FindType("Proto_PetInfo");
            if (netType == null || petType == null)
            {
                return null;
            }

            MethodInfo read = null;
            foreach (var m in netType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ReadMsg" || !m.IsGenericMethodDefinition)
                {
                    continue;
                }

                read = m;
                break;
            }

            if (read == null)
            {
                return null;
            }

            var generic = read.MakeGenericMethod(petType);
            var ps = generic.GetParameters();
            if (ps.Length >= 2)
            {
                return generic.Invoke(null, new object[] { bytes, null });
            }

            return generic.Invoke(null, new object[] { bytes });
        }
        catch (Exception ex)
        {
            WriteLog("DecodeBankUpdatePet EX " + RootMessage(ex));
            return null;
        }
    }

    private static byte[] TryToByteArray(object bytesObj)
    {
        if (bytesObj == null)
        {
            return null;
        }

        if (bytesObj is byte[] raw)
        {
            return raw;
        }

        try
        {
            var m = bytesObj.GetType().GetMethod("ToByteArray", Type.EmptyTypes);
            return m?.Invoke(bytesObj, null) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySendAccountBankTakePet(string uid, int bankIndex)
    {
        var roleMgr = GetManagerInstance("RoleManager");
        var bankType = ResolveAccountBankType();
        if (roleMgr == null || bankType == null)
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

        try
        {
            var ps = sendBank.GetParameters();
            object[] args;
            if (ps.Length >= 6)
            {
                args = new object[] { bankType, uid, "取宠物", bankIndex, 0, null };
            }
            else if (ps.Length == 5)
            {
                args = new object[] { bankType, uid, "取宠物", bankIndex, 0 };
            }
            else
            {
                args = new object[] { bankType, uid, "取宠物", bankIndex };
            }

            sendBank.Invoke(roleMgr, args);
            WriteLog("wild-ex take pet index=" + bankIndex);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("wild-ex take EX " + RootMessage(ex));
            return false;
        }
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

            if (!TrySendActivity("远程个人宠物仓库", uid, 0, 19))
            {
                WriteLog("open personal pet bank send fail uid尾" + TailUid(uid));
            }
        }
        catch (Exception ex)
        {
            WriteLog("open personal pet bank EX " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 对所有宠物栏满的队员，把 1 级休息宠存到银行。返回成功发包的宠物数量。
    /// </summary>
    private static int StoreLevelOnePetsForFull()
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
            return 0;
        }

        var roleMgr = GetManagerInstance("RoleManager");
        if (roleMgr == null)
        {
            return 0;
        }

        var bankType = ResolvePersonalBankType();
        if (bankType == null)
        {
            return 0;
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
            return 0;
        }

        var storedCount = 0;
        foreach (var uid in uids)
        {
            var pets = getPets.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (pets == null)
            {
                continue;
            }

            // 只处理宠物栏满的账号
            if (!IsPetBarFull(pets))
            {
                continue;
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
                    storedCount++;
                    WriteLog("dragon store pet uid=" + uid + " idx=" + index);
                }
                catch (Exception ex)
                {
                    WriteLog("dragon store pet EX uid=" + uid + " " + RootMessage(ex));
                }
            }
        }

        if (storedCount > 0)
        {
            TryDismissBankUiAfterStore();
        }

        return storedCount;
    }

    private static string FormatEscortStatus()
    {
        var zyHead = FormatZhongyuanLoopStatus();
        if (!string.IsNullOrEmpty(zyHead))
        {
            zyHead += "\n";
        }

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
                else if (_floraHealActive && _floraHealResumeEscort)
                {
                    state += "（法兰治疗：" + FloraHealPhaseName(_floraHealPhase) + "）";
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
        var idleLine = "";
        if (_escortActive && !_escortPaused && _escortBetweenTasksWaitMs <= 0)
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                idleLine = "\n静止计时: 战斗中";
            }
            else if (_lastActivityMs > 0)
            {
                idleSec = (int)((NowMs() - _lastActivityMs) / 1000);
                idleLine = "\n静止计时: " + idleSec + "s / 5s";
            }
        }

        return zyHead
               + "状态: " + state
               + "\nRunTaskId: " + GetRunTaskId()
               + "\n对话自动点: " + _dialogueAutoClicks + " 次"
               + idleLine
               + "\n本步骤恢复: " + _escortRecoverAttempts + " / " + EscortMaxRecoverFails
               + "（换步骤重置；第1次清路径点任务，其后挪格）"
               + "\n" + GetEscortSpecialNote()
               + (_dragonLoopActive ? "\n龙族循环: 已循环 " + _dragonLoopCount + " 轮" : "")
               + (_midAutumnLoopActive ? "\n七夕循环: 已完成 " + _midAutumnLoopCount + " 轮（存券后计）" : "")
               + (_escortStuckAbortResumePending ? "\n卡位：清路径后点任务…" : "")
               + (_stuckResumePending ? "\n卡楼梯：挪格后点任务…" : "");
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
                return "特殊处理: 七夕#119 "
                       + (_midAutumnGoralEdition ? "哥拉尔版(回登入点、不用赤凤之翼)" : "阿凯版(回登入点+赤凤之翼)")
                       + "；步骤6哈巴鲁洞穴；步骤7洞窟传送；步骤5布朗山；仅15000先取消回程再走15001；存兑换券计一轮后法兰治疗再下一轮";
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

        var teamNow = GetEscortTeamNum();
        WriteLog("escort MARK resume-team-check teamNum=" + teamNow
                 + " need>=" + EscortTeamMinMembers + " id=" + _escortMissionId
                 + " idx=" + _escortQueueIndex);
        if (teamNow < EscortTeamMinMembers)
        {
            WriteLog("escort MARK resume-blocked-team-low teamNum=" + teamNow
                     + " id=" + _escortMissionId + " idx=" + _escortQueueIndex);
            Tip("队伍仍不足" + EscortTeamMinMembers + "人（当前" + teamNow + "），请组好再继续");
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
                 + " recover=" + _escortRecoverAttempts + " teamNum=" + teamNow);
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
        ClearEscortStuckPending();
        _escortFinishWaitMs = 0;
        _escortBetweenTasksWaitMs = 0;
        _escortRecoverAttempts = 0;
        _escortLastStepNum = -1;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("begin", false);
        _escortPrevInBattle = false;
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
                    || TryStartMoonRabbitSteleTeleport(sn, "begin")
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
        if (_skCNavOwnsEscort)
        {
            WriteLog("skc #1008 mission-done, hand off");
            HandOffSkCNavFromEscort("mission-done");
            return;
        }

        if (_zyLingTangOwnsEscort)
        {
            int lingFloor;
            string lingName;
            int lingMap;
            TryGetCurrentMapInfo(out lingFloor, out lingName, out lingMap);
            if (lingFloor == ZhongyuanHangupAFloor)
            {
                WriteLog("zy-lingtang #1003 mission-done at dest");
                HandOffLingTangFromEscort("mission-done");
                return;
            }

            WriteLog("zy-lingtang #1003 mission-done not dest floor=" + lingFloor);
            BeginEscortAtIndex(0, "lingtang-reclick");
            return;
        }

        var doneId = _escortMissionId;
        WriteLog("escort done missionId=" + doneId + " idx=" + _escortQueueIndex);
        StopEscortEncounterWait("mission-done", false);
        StopTaskNavigation();
        ClearEscortStuckPending();
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

                // 一轮以「存兑换券」为准；任务结束本身不计轮，重新接取继续
                ResetMoonRabbitEscortFlags();
                EnqueueMidAutumnMission();
                _escortQueueIndex = 0;
                _escortBetweenTasksWaitMs = NowMs();
                Tip("七夕循环：任务结束，重新接取（存兑换券后才计一轮，当前已完成 "
                    + _midAutumnLoopCount + " 轮）");
                WriteLog("qixi loop mission-complete no-count count=" + _midAutumnLoopCount);
                try
                {
                    RefreshTitleFromFeature();
                }
                catch
                {
                    // ignore
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
        ClearEscortStuckPending();
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
                _dragonStoreBaselineUsed = CountTeamUsedPetSlots();
                _dragonStoreSentCount = StoreLevelOnePetsForFull();
                if (_dragonStoreSentCount > 0)
                {
                    WriteLog("dragon loop store pets: count=" + _dragonLoopCount
                             + " sent=" + _dragonStoreSentCount
                             + " baselineUsed=" + _dragonStoreBaselineUsed);
                    Tip("龙族循环：宠物位满，已存 " + _dragonStoreSentCount + " 只1级宠，等待银行确认…");
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
        ClearEscortStuckPending();
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
        if (_skCNavOwnsEscort)
        {
            StopSkCNav(string.IsNullOrEmpty(tipMsg) ? "护航已取消" : tipMsg);
            return;
        }

        if (_zyLingTangOwnsEscort)
        {
            StopZhongyuanAll(string.IsNullOrEmpty(tipMsg) ? "已取消护航" : tipMsg);
            return;
        }
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
        ClearEscortStuckPending();
        _escortFinishWaitMs = 0;
        ResetMoonRabbitEscortFlags();
        StopEscortEncounterWait("cancel", false);
        _escortPrevInBattle = false;
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

    /// <summary>启动七夕 #119 循环。goralEdition=true 哥拉尔版（不用赤凤之翼）。</summary>
    private static void StartMidAutumnLoop(bool goralEdition)
    {
        try
        {
            if (!TempMidAutumnEscort119)
            {
                Tip("七夕循环未启用");
                return;
            }

            if (_midAutumnLoopActive)
            {
                var ed = _midAutumnGoralEdition ? "哥拉尔" : "阿凯";
                Tip("七夕循环：已在运行中（" + ed + "版，已完成 " + _midAutumnLoopCount + " 轮）");
                return;
            }

            if (_dragonLoopActive)
            {
                StopDragonLoop();
            }
            else if (_escortActive || _escortPicking || _escortQueue.Count > 0)
            {
                CancelEscort(true, "已切换到七夕循环");
            }

            if (!goralEdition && !CaptainHasMoonRabbitWing())
            {
                Tip("七夕阿凯版：队长背包没有赤凤之翼");
                WriteLog("mid-autumn loop abort no 赤凤之翼");
                return;
            }

            _midAutumnGoralEdition = goralEdition;
            _midAutumnLoopActive = true;
            _midAutumnLoopCount = 0;
            EnqueueMidAutumnMission();
            try { RefreshTitleFromFeature(); } catch { }
            WriteLog("qixi loop start id=" + MoonRabbitMissionId
                     + " edition=" + (goralEdition ? "goral" : "akai"));
            Tip(goralEdition
                ? "七夕哥拉尔版：开始月宫救兔（不用赤凤之翼）"
                : "七夕阿凯版：开始月宫救兔");
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
            Tip("七夕循环：启动失败");
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
        WriteLog("qixi loop manual stop count=" + n + " edition=" + ed);
        CancelEscort(true, "七夕" + ed + "版已停止（共完成 " + n + " 轮）");
        try { RefreshTitleFromFeature(); } catch { }
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
            _dragonStoreSentCount = 0;
            _dragonStoreBaselineUsed = 0;
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
        _dragonStoreSentCount = 0;
        _dragonStoreBaselineUsed = 0;
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
            var waitMs = _dragonStoreRetries > 0 ? DragonStoreRetryWaitMs : DragonStoreWaitMs;
            if (now - _dragonPhaseAtMs < waitMs)
            {
                return;
            }

            var usedNow = CountTeamUsedPetSlots();
            if (CheckAllPetSlotFree(out var failUid2))
            {
                WriteLog("dragon loop store ok, next round count=" + _dragonLoopCount
                         + " recheck=" + _dragonStoreRechecks + " used=" + usedNow);
                BeginDragonNextRound("龙族循环：存包完成，全员有空位，继续循环…");
                return;
            }

            if (_dragonStoreSentCount > 0 && usedNow < _dragonStoreBaselineUsed)
            {
                WriteLog("dragon loop store improved used " + _dragonStoreBaselineUsed + "->" + usedNow
                         + " sent=" + _dragonStoreSentCount);
                BeginDragonNextRound("龙族循环：存包已生效，继续循环…");
                return;
            }

            _dragonStoreRechecks++;

            if (_dragonStoreRetries < DragonStoreMaxRetries)
            {
                var sent = StoreLevelOnePetsForFull();
                if (sent > 0)
                {
                    _dragonStoreSentCount += sent;
                    _dragonStoreRetries++;
                    WriteLog("dragon loop store retry=" + _dragonStoreRetries
                             + " recheck=" + _dragonStoreRechecks + " sent+=" + sent);
                    Tip("龙族循环：宠物仍满，继续存1级宠到银行…");
                    _dragonPhaseAtMs = now;
                    return;
                }
            }

            if (_dragonStoreSentCount > 0 && _dragonStoreRechecks >= DragonStoreForceContinueRechecks)
            {
                WriteLog("dragon loop store force-continue sent=" + _dragonStoreSentCount
                         + " rechecks=" + _dragonStoreRechecks + " failUid=" + failUid2
                         + " used=" + usedNow + " baseline=" + _dragonStoreBaselineUsed);
                Tip("龙族循环：存包已发送，继续循环（若仍满请手动存宠）");
                BeginDragonNextRound(null);
                return;
            }

            if (_dragonStoreRechecks < DragonStoreMaxRechecks)
            {
                WriteLog("dragon loop store wait free uid=" + failUid2
                         + " recheck=" + _dragonStoreRechecks + "/" + DragonStoreMaxRechecks
                         + " used=" + usedNow + " sent=" + _dragonStoreSentCount);
                _dragonPhaseAtMs = now;
                return;
            }

            WriteLog("dragon loop stop: 存包后仍满 uid=" + failUid2
                     + " retries=" + _dragonStoreRetries + " rechecks=" + _dragonStoreRechecks
                     + " sent=" + _dragonStoreSentCount + " count=" + _dragonLoopCount);
            Tip("龙族循环停止：宠物位满且存包后仍无空位，共循环 " + _dragonLoopCount + " 轮");
            _dragonLoopActive = false;
            _dragonPhase = 0;
            TryRebuildEscortTab();
        }
    }

    private static void TickEscort()
    {
        if (IsEscapeDown() && _skCNavActive)
        {
            StopSkCNav("已按 ESC 停止");
            return;
        }

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

        if (_skCNavActive && _skCNavOwnsEscort)
        {
            int skcFloor;
            string skcFloorName;
            int skcMapRes;
            if (TryGetCurrentMapInfo(out skcFloor, out skcFloorName, out skcMapRes)
                && IsSkCArriveFloor(skcFloor))
            {
                HandOffSkCNavFromEscort("floor-" + skcFloor);
                return;
            }
        }

        if (_zyLingTangOwnsEscort)
        {
            int lingFloor;
            string lingFloorName;
            int lingMapRes;
            if (TryGetCurrentMapInfo(out lingFloor, out lingFloorName, out lingMapRes)
                && lingFloor == ZhongyuanHangupAFloor)
            {
                HandOffLingTangFromEscort("floor-52018");
                return;
            }
        }

        if (!_escortActive)
        {
            return;
        }

        // 护航进行中（含七夕/龙城）：每拍跟踪战斗边沿；战后少人一律暂停。
        // 必须放在各类 pending early-return 之前，否则会漏检。
        TickEscortBattleExitTeamGuard();

        // 暂停：不自动点对话 / 不推进队列 / 不卡楼梯；允许手动接管（点其它任务不终止）
        if (_escortPaused)
        {
            return;
        }

        if (_floraHealActive)
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
            // 回登入点途中一直掐路径，避免切图后继续沿倒序表回城。
            AbortEscortTaskPathFully("119-logingate-wait");
            if (now - _escortLoginGateAtMs < EscortLoginGateWaitMs)
            {
                return;
            }

            _escortLoginGatePending = false;
            if (_escort119PendingAfterGateStep == MoonRabbitLoginGateStep2)
            {
                AbortEscortTaskPathFully(_midAutumnGoralEdition
                    ? "119-goral-before-resume"
                    : "119-akai-before-wing");
                _escort119OfficialResumeAtMs = now;
                _escort119OfficialResumePending = true;
                _escort119ResumeUseWing = !_midAutumnGoralEdition;
                WriteLog(_midAutumnGoralEdition
                    ? "119 goral abort then wait 2s official nav"
                    : "119 akai abort #1, wait 2s then 赤凤之翼");
                Tip("任务护航：已取消导航");
                return;
            }

            if (!ClickEscortTaskNav("119-after-logingate"))
            {
                PauseEscortOnConditionFail("119-after-logingate");
            }

            return;
        }

        if (TempMidAutumnEscort119 && _escort119OfficialResumePending)
        {
            AbortEscortTaskPathFully("119-official-resume-wait");
            if (now - _escort119OfficialResumeAtMs < EscortWingWizardSettleMs)
            {
                _escortLastDiag = _escort119ResumeUseWing ? "取消后用羽毛" : "等2秒后点任务";
                return;
            }

            _escort119OfficialResumePending = false;
            _escortLastDiag = "";
            if (_escort119ResumeUseWing)
            {
                _escort119ResumeUseWing = false;
                StartMoonRabbitWingAfterAbort("after-akai-abort");
                return;
            }

            Tip("任务护航：重新点任务");
            if (!ClickEscortTaskLikeMouse())
            {
                PauseEscortOnConditionFail("119-official-resume");
            }

            return;
        }

        if (_escortStuckAbortResumePending)
        {
            TickEscortStuckAbortResume(now);
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

            if (wingWait < 0)
            {
                _escortUseItemPending = false;
                _escortLastDiag = "";
                Tip("任务护航：赤凤之翼弹窗未点完");
                PauseEscortOnConditionFail("119-wing-wizard-timeout");
                return;
            }

            if (wingWait == 2)
            {
                WriteLog("119 wing wizard never opened, continue task");
            }
            else if (IsMapLoading())
            {
                _escortLastDiag = "赤凤之翼切图中";
                return;
            }

            _escortUseItemPending = false;
            _escortLastDiag = "";
            AbortEscortTaskPathFully("119-after-wing");
            _escort119OfficialResumeAtMs = now;
            _escort119OfficialResumePending = true;
            _escort119ResumeUseWing = false;
            WriteLog("119 akai abort #2 after wing, wait 2s then official nav");
            Tip("任务护航：已取消导航");
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
                if (TryStartMoonRabbitStepSpecial(sn, "tick")
                    || TryStartMoonRabbitSteleTeleport(sn, "tick")
                    || TryStartMoonRabbitBrownTeleport(sn, "tick")
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

        // 战斗中不刷新静止基线（战后 idle 重置已在 TickEscortBattleExitTeamGuard）
        if (dialogueOpen)
        {
            _lastActivityMs = now;
        }

        if (!inBattle && TryGetPlayerXY(out var x, out var y))
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
            }
        }

        // 中秋 #119 仅此一处导航特例：15000(22,33)
        if (TryTickMoonRabbitWarpUnstick(now))
        {
            return;
        }

        // 切图后短 settle：等官方 EndLoadMap 自己续上，不要代调 TryResumeTaskPathAfterMapLoad
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
            _lastActivityMs = now;

            if (TempMidAutumnEscort119 && _escortMissionId == MoonRabbitMissionId && prevStep >= 0)
            {
                if (TryStartMoonRabbitStepSpecial(stepNum, "step-change")
                    || TryStartMoonRabbitSteleTeleport(stepNum, "step-change")
                    || TryStartMoonRabbitBrownTeleport(stepNum, "step-change")
                    || TryStartMoonRabbitReefTeleport(stepNum, "step-change")
                    || TryStartMoonRabbitLastStepBank(stepNum, "step-change"))
                {
                    WriteLog("119 special on step-change " + prevStep + "->" + stepNum);
                    return;
                }

                // 打完 boss 时常在战斗中就切到步骤 2，回登入点发不出去。
                // 先掐路径，出战后由 tick 补发；不要点任务/走卡死。
                if (MoonRabbitLoginGateNeeded())
                {
                    StopEscortEncounterWait("119-step2-defer", false);
                    AbortEscortTaskPathFully("119-step2-wait-battle");
                    ClearEscortStuckPending();
                    WriteLog("119 login-gate defer in-battle step=" + prevStep + "->" + stepNum);
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

        if (MoonRabbitLoginGateNeeded())
        {
            AbortEscortTaskPathFully("119-step2-wait");
            ClearEscortStuckPending();
            _lastActivityMs = now;
            _escortLastDiag = "出战后回登入点";
            return;
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

        if (KeepOfficialPathAfterMap("escort-keep"))
        {
            _lastActivityMs = now;
            _escortLastDiag = "官方恢复路径";
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
        ClearEscortStuckPending();
    }

    private static void ClearEscortStuckPending()
    {
        _stuckResumePending = false;
        _escortStuckAbortResumePending = false;
        _escortStuckAbortResumeAtMs = 0;
    }

    /// <summary>
    /// 本步骤第 1 次卡位：清路径 → 等 0.5 秒（期间继续 abort）→ 官方点任务（与 15000/传送相同，等待更短）。
    /// 第 2 次起：随机挪 1 格后再点任务。
    /// </summary>
    private static void BeginEscortStuckRecovery(long now)
    {
        if (KeepOfficialPathAfterMap("escort-stuck"))
        {
            _lastActivityMs = now;
            _escortLastDiag = "官方恢复路径";
            return;
        }

        _escortRecoverAttempts++;
        WriteLog("escort stuck idle missionId=" + _escortMissionId
                 + " stepRecover=" + _escortRecoverAttempts + "/" + EscortMaxRecoverFails);
        if (_escortRecoverAttempts >= EscortMaxRecoverFails)
        {
            _escortPauseReason = "本步骤累计" + EscortMaxRecoverFails + "次尝试恢复失败";
            PauseEscort(_escortPauseReason + "，已暂停，请手动处理", true);
            return;
        }

        if (_escortRecoverAttempts == 1)
        {
            AbortEscortTaskPathFully("stuck-first-abort");
            _escortStuckAbortResumePending = true;
            _escortStuckAbortResumeAtMs = now;
            _stuckResumePending = false;
            _lastActivityMs = now;
            WriteLog("escort stuck first: abort then wait "
                     + EscortStuckFirstAbortWaitMs + "ms official nav");
            Tip("任务护航：卡位，清路径后点任务（本步骤 1/"
                + EscortMaxRecoverFails + "）");
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

    /// <summary>第 1 次卡位：等待期间每拍继续 abort，满 0.5 秒再官方点任务。</summary>
    private static void TickEscortStuckAbortResume(long now)
    {
        AbortEscortTaskPathFully("stuck-first-abort-wait");
        if (now - _escortStuckAbortResumeAtMs < EscortStuckFirstAbortWaitMs)
        {
            _escortLastDiag = "清路径后点任务";
            return;
        }

        _escortStuckAbortResumePending = false;
        _escortStuckAbortResumeAtMs = 0;
        _escortLastDiag = "";
        _lastActivityMs = now;
        Tip("任务护航：重新点任务");
        if (!ClickEscortTaskLikeMouse())
        {
            PauseEscortOnConditionFail("stuck-first-abort-resume");
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

    /// <summary>
    /// 护航战斗边沿：出战刷新静止计时；队伍解散或不足 5 人则暂停（普通护航 / 七夕 / 龙城共用）。
    /// </summary>
    private static void TickEscortBattleExitTeamGuard()
    {
        var inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        if (_escortPrevInBattle && !inBattle)
        {
            _lastActivityMs = NowMs();
            WriteLog("escort battle exit idle reset id=" + _escortMissionId
                     + " dragon=" + _dragonLoopActive + " qixi=" + _midAutumnLoopActive);
            if (!_escortPaused)
            {
                TryPauseEscortIfTeamBrokenAfterBattle();
            }
        }

        _escortPrevInBattle = inBattle;
    }

    /// <summary>战后：队伍解散或不足 EscortTeamMinMembers 则暂停护航，等手动继续。</summary>
    private static void TryPauseEscortIfTeamBrokenAfterBattle()
    {
        if (!_escortActive || _escortPaused)
        {
            return;
        }

        var teamNum = GetEscortTeamNum();
        var mode = _dragonLoopActive ? "dragon" : (_midAutumnLoopActive ? "qixi" : "escort");
        WriteLog("escort MARK battle-exit-team-check mode=" + mode
                 + " teamNum=" + teamNum
                 + " need>=" + EscortTeamMinMembers
                 + " id=" + _escortMissionId + " idx=" + _escortQueueIndex
                 + " title=" + _escortMissionTitle);
        if (teamNum >= EscortTeamMinMembers)
        {
            WriteLog("escort MARK battle-exit-team-ok mode=" + mode + " teamNum=" + teamNum
                     + " id=" + _escortMissionId);
            return;
        }

        WriteLog("escort MARK battle-exit-team-break pause mode=" + mode
                 + " teamNum=" + teamNum
                 + " id=" + _escortMissionId + " idx=" + _escortQueueIndex
                 + " title=" + _escortMissionTitle);
        PauseEscort("战后队伍不足" + EscortTeamMinMembers + "人（当前" + teamNum
                    + "），请组队后点继续", true);
    }

    /// <summary>当前队伍人数：TeamManager.GetTeamNum（与条件诊断/桥接一致）。</summary>
    private static int GetEscortTeamNum()
    {
        try
        {
            var tm = GetManagerInstance("TeamManager");
            if (tm == null)
            {
                return 0;
            }

            var m = tm.GetType().GetMethod("GetTeamNum",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return 0;
            }

            return Convert.ToInt32(m.Invoke(tm, null) ?? 0);
        }
        catch
        {
            return 0;
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

            // 第二处：WalkSystem.curRequest + waitRequest。只 Cancel 不清走路，官方过图会 resumeMove 旧路。
            InvokeWalkStopMove(true);
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

    /// <summary>
    /// 彻底终止官方任务导航。官方要清两处：TaskManager（Cancel + sameIndex + AutoWarpIndex）
    /// 和 WalkSystem（StopMove 的 curRequest/waitRequest）以及 MissionData.TargetPoint。
    /// 只清一处时过图会续上旧路（七夕 15000 已踩过）。
    /// CancelTaskPathfinding 本身不清 AutoWarpIndex；m_IsStartingTaskPath 为 true 时 Cancel 会直接 return。
    /// </summary>
    private static void AbortEscortTaskPathFully(string reason)
    {
        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm != null)
            {
                try
                {
                    SetMember(tm, "m_IsStartingTaskPath", false);
                }
                catch
                {
                    // ignore
                }
            }

            StopTaskNavigation(false);
            ClearTaskPathSameIndexStopGuard();
            if (tm != null)
            {
                try
                {
                    SetMember(tm, "m_IsTaskPathfindingActive", false);
                    SetMember(tm, "m_TaskPathResumePending", false);
                    SetMember(tm, "m_TaskPathWaitingNpcMapChange", false);
                    SetMember(tm, "m_TaskPathCompletedIndex", -1);
                    SetMember(tm, "m_TaskPathCompletedStep", -1);
                    SetMember(tm, "m_TaskPathContextTaskId", -1);
                    SetMember(tm, "m_TaskPathContextStep", -1);
                    SetMember(tm, "m_TaskPathContextIndex", -1);
                    SetMember(tm, "m_TaskPathSuppressAutoWarpFallback", false);
                    SetMember(tm, "m_TaskPathResetIndexAfterMapLoad", false);
                    SetMember(tm, "m_TaskPathMissionDataDirty", false);
                    SetMember(tm, "RunTaskId", -1);
                }
                catch (Exception ex)
                {
                    WriteLog("AbortEscortTaskPathFully fields EX " + RootMessage(ex));
                }

                try
                {
                    var clearCtx = tm.GetType().GetMethod(
                        "ClearTaskPathContext",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    clearCtx?.Invoke(tm, null);
                    var clearRetry = tm.GetType().GetMethod(
                        "ClearTaskPathLastRetry",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    clearRetry?.Invoke(tm, null);
                }
                catch
                {
                    // ignore
                }
            }

            var mission = GetSidebarTaskMissionData(_escortMissionId) ?? GetMissionDataById(_escortMissionId);
            ForceEscortAutoWarpIndexZero();
            ClearMissionTargetPoint(mission);
            try
            {
                var mdType = FindType("MissionData");
                if (mdType != null)
                {
                    var cur = mdType.GetProperty(
                        "currentExecuting", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    if (cur != null && cur.CanWrite)
                    {
                        cur.SetValue(null, null, null);
                    }
                    else
                    {
                        mdType.GetField(
                            "currentExecuting",
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                            ?.SetValue(null, null);
                    }
                }
            }
            catch
            {
                // ignore
            }

            WriteLog("119 abort-path " + reason);
        }
        catch (Exception ex)
        {
            WriteLog("AbortEscortTaskPathFully EX " + RootMessage(ex));
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

    /// <summary>
    /// 遇敌步骤指定坐标：movePoint[0]（倒序表头=真正目的地，与侧栏 AutoWarpIndex=0 一致）。
    /// 切勿用表尾——那是出发城镇，会在出发点就开遇敌。
    /// </summary>
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

            var v3 = move[0];
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
            // 无寻路点：只有「开启原地遇敌」类才允许原地开；否则视为未到点
            return false;
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
        _escort119OfficialResumePending = false;
        _escort119OfficialResumeAtMs = 0;
        _escort119ResumeUseWing = false;
        ResetWingWizardState();
        _escort119GateDone2 = false;
        _escort119TeleportDone6 = false;
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
        _escort119WarpUnstickHandledStep = -1;
    }

    /// <summary>
    /// 中秋 #119：15000(22,33) 与哥拉尔羽毛落地同一套：
    /// 彻底掐掉缓存路径 → 等 2 秒 → 官方 AutoWarpIndex=0 + RunTask。
    /// 同一 missionStepNum 只处理一次，避免过图反复落点重复清路径。
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
            return false;
        }

        if (_escort119OfficialResumePending)
        {
            return true;
        }

        var stepNum = GetEscortMissionStepNum();
        if (_escort119WarpUnstickHandledStep == stepNum && stepNum >= 0)
        {
            return false;
        }

        if (IsMapLoading())
        {
            AbortEscortTaskPathFully("119-15000-loading");
            return true;
        }

        int px;
        int py;
        if (!TryGetPlayerXY(out px, out py))
        {
            return false;
        }

        if (px != MoonRabbitWarpStuckX || py != MoonRabbitWarpStuckY)
        {
            return false;
        }

        AbortEscortTaskPathFully("119-15000-before-resume");
        _escort119OfficialResumeAtMs = now;
        _escort119OfficialResumePending = true;
        _escort119WarpUnstickHandledStep = stepNum;
        _lastActivityMs = now;
        ClearEscortStuckPending();
        WriteLog("119 15000 abort once step=" + stepNum + ", wait 2s then official nav");
        Tip("任务护航：过图卡住，清路径后点任务");
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

    /// <summary>
    /// 取消官方任务导航后，直接 GeneralPointMoveTo 正向目的地。
    /// Cancel 不会清 MissionData.AutoWarpIndex；再 RunTask 会按倒序表 +1 退回出发点。
    /// </summary>
    private static bool RestartMoonRabbitForwardNav(string reason, int floor, int x, int y, string tip)
    {
        WriteLog("119 forward-nav " + reason + " dest=(" + floor + "," + x + "," + y + ")");
        if (!string.IsNullOrEmpty(tip))
        {
            Tip(tip);
        }

        ClearEscortStuckPending();
        StopTaskNavigation(false);
        ClearTaskPathSameIndexStopGuard();
        ForceEscortAutoWarpIndexZero();
        _lastActivityMs = NowMs();
        string how;
        if (!TryNavigateTo(floor, x, y, out how))
        {
            WriteLog("119 forward-nav fail " + reason + " how=" + how);
            return false;
        }

        WriteLog("119 forward-nav ok " + reason + " how=" + how);
        return true;
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

            // 七夕 15000：官方要清两处。只清 sameIndex 时第一次 RunTask 不走路；
            // 过期 TargetPoint 会寻路“忙”却不走。两处都清掉，这一下才真正走。
            ClearTaskPathSameIndexStopGuard();
            ClearMissionTargetPoint(mission);

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

    /// <summary>步骤 2 还没发出回登入点（含战斗中切步、出战后待补发）。</summary>
    private static bool MoonRabbitLoginGateNeeded()
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (_escort119GateDone2 || _escortLoginGatePending)
        {
            return false;
        }

        return GetEscortMissionStepNum() == MoonRabbitLoginGateStep2;
    }

    /// <summary>
    /// 中秋 #119：步骤 2 开始时队长回登入点。
    /// 战斗中发不出，出战后由 tick 重试。阿凯版切图后再用赤凤之翼；哥拉尔版回点后点任务。
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

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                if (reason != "tick")
                {
                    WriteLog("119 login-gate wait in-battle reason=" + reason);
                }

                return false;
            }
        }
        catch
        {
            // ignore，交给 SendLoginGate 再判一次
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

    /// <summary>阿凯版第一次取消并等 2 秒后，使用队长背包赤凤之翼。</summary>
    private static void StartMoonRabbitWingAfterAbort(string reason)
    {
        if (UseCaptainBagItem(MoonRabbitWingKeyword, false))
        {
            _escortUseItemPending = true;
            _escortUseItemAtMs = NowMs();
            ResetWingWizardState();
            WriteLog("119 use 赤凤之翼 " + reason);
            Tip("任务护航：使用赤凤之翼");
            return;
        }

        Tip("任务护航：队长背包没有赤凤之翼");
        _escortLastDiag = "队长背包没有赤凤之翼";
        PauseEscortOnConditionFail("119-red-phoenix-wing");
    }

    /// <summary>
    /// 中秋 #119 步骤 6 调查星月落痕·石碑：开始前挂机传送「哈巴鲁洞穴」（SendMisc Id=1），到图后再点任务导航。
    /// 已在洞穴或已靠近石碑则跳过。战斗中不标记完成，出战后重试。
    /// </summary>
    private static bool TryStartMoonRabbitSteleTeleport(int stepNum, string reason)
    {
        if (!TempMidAutumnEscort119 || _escortMissionId != MoonRabbitMissionId)
        {
            return false;
        }

        if (stepNum != MoonRabbitSteleStep || _escort119TeleportDone6 || _escortHangupTeleportPending)
        {
            return false;
        }

        if (!IsLocalCaptain())
        {
            return false;
        }

        if (IsNearMoonRabbitSteleOrHabaru())
        {
            _escort119TeleportDone6 = true;
            WriteLog("119 hangup-teleport habaru skip already-near reason=" + reason);
            return false;
        }

        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                WriteLog("119 hangup-teleport habaru wait in-battle");
                return false;
            }
        }
        catch
        {
            // ignore
        }

        if (!TrySendHangupTeleport(MoonRabbitHabaruTeleportId))
        {
            return false;
        }

        _escort119TeleportDone6 = true;
        _escortHangupTeleportPending = true;
        _escortHangupTeleportAtMs = NowMs();
        _escortHangupTeleportExpectFloor = MoonRabbitHabaruTeleportFloor;
        Tip("任务护航：已传送哈巴鲁洞穴");
        WriteLog("119 hangup-teleport habaru " + reason + " id=" + MoonRabbitHabaruTeleportId);
        return true;
    }

    private static bool IsNearMoonRabbitSteleOrHabaru()
    {
        try
        {
            int floor;
            string floorName;
            int mapResId;
            if (TryGetCurrentMapInfo(out floor, out floorName, out mapResId)
                && floor == MoonRabbitHabaruTeleportFloor)
            {
                return true;
            }

            if (floor == MoonRabbitSteleMapFloor && TryGetPlayerXY(out var x, out var y))
            {
                var dx = x - MoonRabbitSteleX;
                var dy = y - MoonRabbitSteleY;
                return dx * dx + dy * dy <= MoonRabbitSteleNearDist * MoonRabbitSteleNearDist;
            }
        }
        catch
        {
            // ignore
        }

        return false;
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

    /// <summary>
    /// 挂机导航真正的「前往」：TaskManager.StartWayThMissionStepByID(uid, WayId)。
    /// 不要点挂机 UI「前往」按钮（补丁已把它绑成传送）。
    /// </summary>
    private static bool TrySendHangupGo(int wayId)
    {
        if (!IsLocalCaptain() || wayId <= 0)
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

            var uid = Convert.ToString(
                GetStaticMember("PlayerDataHolder", "MainPlayerUid") ?? "") ?? "";
            if (string.IsNullOrEmpty(uid) || tm == null)
            {
                WriteLog("hangup-go miss uid/tm wayId=" + wayId);
                return false;
            }

            var mission = GetMissionDataById(wayId);
            if (mission == null)
            {
                WriteLog("hangup-go miss mission wayId=" + wayId);
                return false;
            }

            var start = tm.GetType().GetMethod(
                "StartWayThMissionStepByID",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (start == null)
            {
                WriteLog("hangup-go StartWayTh miss");
                return false;
            }

            var ps = start.GetParameters();
            var args = new object[ps.Length];
            args[0] = uid;
            args[1] = wayId;
            for (var i = 2; i < ps.Length; i++)
            {
                args[i] = ps[i].ParameterType.IsValueType
                    ? Activator.CreateInstance(ps[i].ParameterType)
                    : null;
            }

            start.Invoke(tm, args);
            WriteLog("hangup-go StartWayTh wayId=" + wayId);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("hangup-go EX " + RootMessage(ex));
            return false;
        }
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
    /// 七夕循环最后一步：停导航，全员把七夕礼盒兑换券存账号银行。任务会回到第一步，直接开下一轮。
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
        Tip("七夕循环：已存兑换券，完成第 " + _midAutumnLoopCount + " 轮，先丢绿/红头盔再治疗…");
        WriteLog("qixi ticket-bank next-round count=" + _midAutumnLoopCount + " step=" + _escortLastStepNum);
        try
        {
            RefreshTitleFromFeature();
        }
        catch
        {
            // ignore
        }

        StartFloraHeal(true);

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
                        Tip("七夕循环：全员兑换券已存入账号银行");
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
                    Tip("七夕循环：全员兑换券已存入账号银行");
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
                Tip("七夕循环：全员兑换券已存入账号银行");
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

        Tip("七夕阿凯版：队长背包没有赤凤之翼");
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

            if (!TrySendActivity(MoonRabbitAccountBankActivity, uid, 0, 19))
            {
                WriteLog("open account item bank send fail uid尾" + TailUid(uid));
            }
        }
        catch (Exception ex)
        {
            WriteLog("open account item bank EX " + RootMessage(ex));
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
        _escortWingFromFloor = 0;
    }

    /// <returns>0=仍在等, 1=弹窗已关, 2=一直没弹窗, -1=点窗超时</returns>
    private static int TickWingWizardProgress(long startedAtMs)
    {
        if (_escortUseItemPending)
        {
            AbortEscortTaskPathFully("119-wing-wait");
        }
        else
        {
            StopTaskNavigation(false);
        }
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
            _escortWingFromFloor = 0;
            try
            {
                int floor;
                string floorName;
                int mapResId;
                if (TryGetCurrentMapInfo(out floor, out floorName, out mapResId))
                {
                    _escortWingFromFloor = floor;
                }
            }
            catch
            {
                // ignore
            }

            WriteLog("wing wizard closed, settle fromFloor=" + _escortWingFromFloor);
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

    /// <summary>脚本页法兰治疗：回城点2 → 1000(82,83)切图 → (7,33) → 迪拉全队回复。</summary>
    private static string FormatFloraHealStatus()
    {
        if (!_floraHealActive)
        {
            return "法兰治疗: 未启动\n回城点2 → 1000(82,83)切图1111 → (7,33) → 迪拉全队回复；回城/导航不到位单步最多3次";
        }

        return "法兰治疗: " + FloraHealPhaseName(_floraHealPhase)
               + " 尝试" + _floraHealStepTries + "/" + FloraHealMaxTries
               + "\n" + (_floraHealNote ?? "")
               + "\n" + FormatNavPosLine();
    }

    private static string FloraHealPhaseName(int phase)
    {
        switch (phase)
        {
            case FloraHealPhaseReturn: return "回城记录点2";
            case FloraHealPhaseDelayAfterReturn: return "回城后等待 1 秒";
            case FloraHealPhaseToDoor: return "导航 1000 (82,83) 等切图";
            case FloraHealPhaseDelayAfterDoor: return "切图后等待 1 秒";
            case FloraHealPhaseToStand: return "导航 1111 (7,33)";
            case FloraHealPhaseDelayAfterStand: return "到位后等待 1 秒";
            case FloraHealPhaseLookNpc: return "点资深护士迪拉";
            case FloraHealPhaseDelayAfterLook: return "对话后等待 1 秒";
            case FloraHealPhasePick: return "选全队回复";
            case FloraHealPhaseDropHelmets: return "丢弃绿/红头盔";
            default: return "准备中";
        }
    }

    private static void ToggleFloraHeal()
    {
        if (_floraHealActive)
        {
            StopFloraHeal("已手动停止", false);
            RefreshScriptTabIfVisible();
            return;
        }

        StartFloraHeal(false);
        RefreshScriptTabIfVisible();
    }

    /// <summary>
    /// 通用法兰治疗。脚本页按钮与七夕每轮存券后都走这里（resumeEscort=true 时治完再点任务）。
    /// 七夕：先丢队长背包里名字为「绿头盔」「红头盔」的道具（一件一丢，间隔 1 秒）→ 回城点2 → 1000(82,83)切图1111 → (7,33) → 点迪拉 → 全队回复。
    /// </summary>
    private static void StartFloraHeal(bool resumeEscort)
    {
        if (_floraHealActive)
        {
            return;
        }

        if (GetEncounterStatus() != 0)
        {
            TrySendEscortAutoBattle("停止挂机");
        }

        if (resumeEscort)
        {
            AbortEscortTaskPathFully("119-before-flora-heal");
        }

        _floraHealResumeEscort = resumeEscort;
        _floraHealActive = true;
        _floraHealPhase = resumeEscort ? FloraHealPhaseDropHelmets : FloraHealPhaseReturn;
        _floraHealStepTries = 0;
        _floraHealActionAtMs = 0;
        _floraHealNeedRetry = false;
        _floraHealLastLookMs = 0;
        _floraHealDelayUntilMs = 0;
        _floraHealNote = resumeEscort ? "丢弃绿/红头盔" : "回城记录点2";
        Tip(resumeEscort ? "七夕循环：先丢绿/红头盔再治疗" : "法兰治疗：已启动");
        WriteLog("flora-heal start resumeEscort=" + resumeEscort + " phase=" + _floraHealPhase);
        try
        {
            if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
            {
                _floraHealNote = resumeEscort ? "战斗中，等出战再丢头盔" : "战斗中，等出战再回城";
                return;
            }
        }
        catch
        {
            // ignore
        }

        if (resumeEscort)
        {
            return;
        }

        FloraHealDoReturn();
    }

    private static void StopFloraHeal(string reason, bool success)
    {
        if (!_floraHealActive && _floraHealPhase == FloraHealPhaseIdle)
        {
            return;
        }

        _floraHealLastOk = success;
        var resume = _floraHealResumeEscort;
        _floraHealActive = false;
        _floraHealPhase = FloraHealPhaseIdle;
        _floraHealResumeEscort = false;
        _floraHealNeedRetry = false;
        _floraHealNote = reason ?? "";
        try
        {
            StopTaskNavigation(false);
        }
        catch
        {
            // ignore
        }

        WriteLog("flora-heal stop ok=" + success + " resume=" + resume + " " + reason);
        Tip("法兰治疗：" + reason);

        if (!resume || !_midAutumnLoopActive || !_escortActive || _escortPaused)
        {
            RefreshScriptTabIfVisible();
            return;
        }

        if (success)
        {
            AbortEscortTaskPathFully("119-after-flora-heal");
            Tip("七夕循环：治疗完成，开始下一轮");
            if (!ClickEscortTaskNav("119-after-flora-heal"))
            {
                PauseEscortOnConditionFail("119-after-flora-heal");
            }
        }
        else
        {
            PauseEscortOnConditionFail("119-flora-heal");
        }

        RefreshScriptTabIfVisible();
    }

    private static string FormatFullAutoScriptStatus()
    {
        if (!_fullScriptActive)
        {
            return "自动全套: 未启动（测试）\n计数挂机→日常→法兰治疗→回登入点→33200(167,108)→门关管理人选第一项→33500(32,14)切33000→遇敌";
        }

        return "自动全套: " + FullScriptPhaseName(_fullScriptPhase)
               + "\n" + (_fullScriptNote ?? "")
               + "\n" + FormatNavPosLine();
    }

    private static string FullScriptPhaseName(int phase)
    {
        switch (phase)
        {
            case FullScriptPhaseCountFarm: return "1) 切到计数挂机";
            case FullScriptPhaseDaily: return "2) 做日常";
            case FullScriptPhaseWaitDaily: return "2) 等日常完成";
            case FullScriptPhaseFloraHeal: return "3) 法兰治疗";
            case FullScriptPhaseWaitFlora: return "3) 等法兰治疗";
            case FullScriptPhaseLoginGate: return "4) 回登入点";
            case FullScriptPhaseWaitLogin: return "4) 等回登入点";
            case FullScriptPhaseNav1: return "5) 寻路 33200 (167,108)";
            case FullScriptPhaseTalkNpc: return "6) 门关管理人选第一项";
            case FullScriptPhaseNav2: return "7) 寻路 33500 (32,14)";
            case FullScriptPhaseWaitWarp: return "7) 等切图 33000";
            case FullScriptPhaseEncounter: return "8) 开始遇敌";
            default: return "准备中";
        }
    }

    private static void ToggleFullAutoScript()
    {
        if (_fullScriptActive)
        {
            StopFullAutoScript("已手动停止");
            RefreshScriptTabIfVisible();
            return;
        }

        StartFullAutoScript();
        RefreshScriptTabIfVisible();
    }

    private static void StartFullAutoScript()
    {
        if (_fullScriptActive)
        {
            return;
        }

        if (_escortActive)
        {
            PauseEscort("自动全套：已暂停护航");
        }

        if (_lingTangActive)
        {
            StopLingTang("自动全套：已停刷灵堂");
        }

        if (_floraHealActive)
        {
            StopFloraHeal("自动全套接管，已停治疗", false);
        }

        _fullScriptActive = true;
        _fullScriptPhase = FullScriptPhaseCountFarm;
        _fullScriptNote = "切到计数挂机";
        _fullScriptPhaseAtMs = NowMs();
        _fullScriptLastActionMs = 0;
        _fullScriptLoginFromFloor = 0;
        Tip("自动全套脚本：已启动（测试）");
        WriteLog("full-script start");
    }

    private static void StopFullAutoScript(string reason)
    {
        if (!_fullScriptActive && _fullScriptPhase == FullScriptPhaseIdle)
        {
            return;
        }

        if (_floraHealActive && !_floraHealResumeEscort)
        {
            StopFloraHeal("自动全套已停", false);
        }

        _fullScriptActive = false;
        _fullScriptPhase = FullScriptPhaseIdle;
        _fullScriptNote = reason ?? "";
        WriteLog("full-script stop " + reason);
        Tip("自动全套脚本：" + reason);
        RefreshScriptTabIfVisible();
    }

    private static void FullScriptEnter(int phase, string note)
    {
        _fullScriptPhase = phase;
        _fullScriptNote = note ?? "";
        _fullScriptPhaseAtMs = NowMs();
        _fullScriptLastActionMs = 0;
        if (phase != FullScriptPhaseTalkNpc)
        {
            _fullScriptTalkPicked = false;
        }

        if (phase != FullScriptPhaseWaitWarp)
        {
            _fullScriptAtWarpTile = false;
        }

        WriteLog("full-script phase -> " + FullScriptPhaseName(phase) + " " + note);
    }

    private static void TickFullAutoScript()
    {
        if (!_fullScriptActive)
        {
            return;
        }

        var now = NowMs();
        var inBattle = false;
        try
        {
            inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        }
        catch
        {
            // ignore
        }

        switch (_fullScriptPhase)
        {
            case FullScriptPhaseCountFarm:
                SelectBattleMode(ModeCountFarm);
                FullScriptEnter(FullScriptPhaseDaily, "做日常");
                break;

            case FullScriptPhaseDaily:
                if (IsDailyClaimPipelineRunning())
                {
                    FullScriptEnter(FullScriptPhaseWaitDaily, "日常已在跑，等待完成");
                    break;
                }

                InvokeDailyClaimToggle("ToggleDailyFromUi", "日常");
                if (IsDailyClaimPipelineRunning())
                {
                    FullScriptEnter(FullScriptPhaseWaitDaily, "日常已开始");
                }
                else
                {
                    FullScriptEnter(FullScriptPhaseFloraHeal, "日常未开始，继续法兰治疗");
                }

                break;

            case FullScriptPhaseWaitDaily:
                if (!IsDailyClaimPipelineRunning())
                {
                    FullScriptEnter(FullScriptPhaseFloraHeal, "日常完成");
                    break;
                }

                if (now - _fullScriptPhaseAtMs >= FullScriptDailyWaitMs)
                {
                    FullScriptEnter(FullScriptPhaseFloraHeal, "日常超时，继续法兰治疗");
                }
                else
                {
                    _fullScriptNote = "等日常 " + ((now - _fullScriptPhaseAtMs) / 1000) + "s";
                }

                break;

            case FullScriptPhaseFloraHeal:
                _floraHealLastOk = false;
                StartFloraHeal(false);
                if (_floraHealActive)
                {
                    FullScriptEnter(FullScriptPhaseWaitFlora, "法兰治疗已启动");
                }
                else
                {
                    StopFullAutoScript("法兰治疗未能启动");
                }

                break;

            case FullScriptPhaseWaitFlora:
                if (_floraHealActive)
                {
                    if (now - _fullScriptPhaseAtMs >= FullScriptFloraWaitMs)
                    {
                        StopFloraHeal("自动全套等待超时", false);
                        StopFullAutoScript("法兰治疗超时");
                    }
                    else
                    {
                        _fullScriptNote = "等治疗 " + FloraHealPhaseName(_floraHealPhase);
                    }

                    break;
                }

                if (!_floraHealLastOk)
                {
                    StopFullAutoScript("法兰治疗失败");
                    break;
                }

                FullScriptEnter(FullScriptPhaseLoginGate, "治疗完成，回登入点");
                break;

            case FullScriptPhaseLoginGate:
            case FullScriptPhaseWaitLogin:
            case FullScriptPhaseNav1:
            case FullScriptPhaseTalkNpc:
            case FullScriptPhaseNav2:
            case FullScriptPhaseWaitWarp:
            case FullScriptPhaseEncounter:
                if (inBattle)
                {
                    _fullScriptNote = "战斗中，等出战";
                    return;
                }

                break;
        }

        if (!_fullScriptActive)
        {
            return;
        }

        switch (_fullScriptPhase)
        {
            case FullScriptPhaseLoginGate:
                FullScriptDoLoginGate();
                break;

            case FullScriptPhaseWaitLogin:
                FullScriptTickWaitLogin(now);
                break;

            case FullScriptPhaseNav1:
                FullScriptTickNav(now, FullScriptNav1Floor, FullScriptNav1X, FullScriptNav1Y,
                    FullScriptPhaseTalkNpc, "寻路1", "寻路1未配置，跳过");
                break;

            case FullScriptPhaseTalkNpc:
                FullScriptTickTalkNpc(now);
                break;

            case FullScriptPhaseNav2:
                FullScriptTickNav2(now);
                break;

            case FullScriptPhaseWaitWarp:
                FullScriptTickWaitWarp(now);
                break;

            case FullScriptPhaseEncounter:
                FullScriptDoEncounter();
                break;
        }
    }

    private static bool IsDailyClaimPipelineRunning()
    {
        try
        {
            var t = EnsureFeatureType("SeqChapterDailyClaim", "hotfixdata/SeqChapterDailyClaim.dll.bytes");
            if (t == null)
            {
                return false;
            }

            var f = t.GetField("_pipelineRunning", BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null && Convert.ToBoolean(f.GetValue(null)))
            {
                return true;
            }

            var m = t.GetMethod(
                "IsAnyCopyPipelineRunning",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (m != null && Convert.ToBoolean(m.Invoke(null, null)))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("IsDailyClaimPipelineRunning EX: " + RootMessage(ex));
        }

        return false;
    }

    private static void FullScriptDoLoginGate()
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        _fullScriptLoginFromFloor = floor;
        if (!FullScriptSendLoginGate())
        {
            _fullScriptNote = "回登入点未发出（非队长或战斗中）";
            return;
        }

        FullScriptEnter(FullScriptPhaseWaitLogin, "已发回登入点，等待切图");
    }

    private static bool FullScriptSendLoginGate()
    {
        try
        {
            if (!IsLocalCaptain())
            {
                return false;
            }

            try
            {
                if (Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false))
                {
                    return false;
                }
            }
            catch
            {
                // ignore
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
                WriteLog("full-script SendLoginGate missing");
                return false;
            }

            StopTaskNavigation(false);
            send.Invoke(login, null);
            StopTaskNavigation();
            Tip("自动全套：已回登入点");
            WriteLog("full-script SendLoginGate ok fromFloor=" + _fullScriptLoginFromFloor);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("full-script SendLoginGate EX " + RootMessage(ex));
            return false;
        }
    }

    private static void FullScriptTickWaitLogin(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        var left = now - _fullScriptPhaseAtMs;
        if (floor > 0 && floor != _fullScriptLoginFromFloor && left >= FullScriptLoginSettleMs)
        {
            FullScriptEnter(FullScriptPhaseNav1, "已回登入点");
            return;
        }

        if (left >= FullScriptLoginWaitMs)
        {
            FullScriptEnter(FullScriptPhaseNav1, "回登入点等待结束，继续");
            return;
        }

        _fullScriptNote = "等回登入点 floor=" + floor + " " + (left / 1000) + "s";
    }

    private static void FullScriptTickNav(
        long now, int floor, int x, int y, int nextPhase, string label, string skipNote)
    {
        if (floor <= 0)
        {
            FullScriptEnter(nextPhase, skipNote);
            return;
        }

        int curFloor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out curFloor, out floorName, out mapResId);
        int px;
        int py;
        TryGetPlayerXY(out px, out py);
        if (curFloor == floor && Math.Abs(px - x) + Math.Abs(py - y) <= 1)
        {
            FullScriptEnter(nextPhase, label + " 已到位");
            return;
        }

        if (now - _fullScriptPhaseAtMs >= FullScriptNavWaitMs)
        {
            StopFullAutoScript(label + " 超时未到位");
            return;
        }

        if (_fullScriptLastActionMs == 0 || now - _fullScriptLastActionMs >= FullScriptNavRetryMs)
        {
            string how;
            if (TryNavigateTo(floor, x, y, out how))
            {
                _fullScriptNote = label + " " + how + " → " + floor + "(" + x + "," + y + ")";
                WriteLog("full-script nav " + label + " " + how);
            }
            else
            {
                _fullScriptNote = label + " 导航失败: " + how;
                WriteLog("full-script nav fail " + label + " " + how);
            }

            _fullScriptLastActionMs = now;
        }
    }

    private static void FullScriptTickTalkNpc(long now)
    {
        if (string.IsNullOrEmpty(FullScriptNpcName))
        {
            FullScriptEnter(FullScriptPhaseNav2, "NPC 名未配置，跳过对话");
            return;
        }

        if (now - _fullScriptPhaseAtMs >= FullScriptTalkWaitMs)
        {
            StopFullAutoScript("NPC 对话超时: " + FullScriptNpcName);
            return;
        }

        if (_fullScriptTalkPicked)
        {
            if (IsDialoguePanelOpen())
            {
                _fullScriptNote = "已选第一项，等对话关掉";
                return;
            }

            if (now - _fullScriptLastActionMs < FullScriptTalkSettleMs)
            {
                _fullScriptNote = "对话已关，稍等再寻路";
                return;
            }

            FullScriptEnter(FullScriptPhaseNav2, "门关对话完成");
            return;
        }

        if (IsDialoguePanelOpen())
        {
            if (TryPickFirstDialogueOption())
            {
                _fullScriptTalkPicked = true;
                _fullScriptLastActionMs = now;
                _fullScriptNote = "已选第一项，等关窗";
                WriteLog("full-script talk picked first");
            }
            else
            {
                _fullScriptNote = "对话已开，等待第一项";
            }

            return;
        }

        if (_fullScriptLastActionMs == 0 || now - _fullScriptLastActionMs >= FullScriptTalkRetryMs)
        {
            var objindex = FindNpcObjIndexByNameOrPos(
                FullScriptNpcName, FullScriptNpcName, FullScriptNpcX, FullScriptNpcY);
            if (objindex >= 0 && FullScriptSendLookNpc(objindex))
            {
                _fullScriptNote = "已点 " + FullScriptNpcName;
                WriteLog("full-script LookNpc ok obj=" + objindex);
            }
            else
            {
                _fullScriptNote = "未找到 NPC " + FullScriptNpcName + " @ "
                                  + FullScriptNpcX + "," + FullScriptNpcY;
            }

            _fullScriptLastActionMs = now;
        }
    }

    private static void FullScriptTickNav2(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        int px;
        int py;
        TryGetPlayerXY(out px, out py);
        if (floor == FullScriptNav2WarpFloor)
        {
            FullScriptEnter(FullScriptPhaseEncounter, "已在 33000");
            return;
        }

        if (floor == FullScriptNav2Floor
            && Math.Abs(px - FullScriptNav2X) + Math.Abs(py - FullScriptNav2Y) <= 1)
        {
            try
            {
                StopTaskNavigation(false);
            }
            catch
            {
                // ignore
            }

            FullScriptEnter(FullScriptPhaseWaitWarp, "已到 33500 传送格，等切 33000");
            return;
        }

        FullScriptTickNav(
            now,
            FullScriptNav2Floor,
            FullScriptNav2X,
            FullScriptNav2Y,
            FullScriptPhaseWaitWarp,
            "寻路2",
            "寻路2未配置，跳过");
    }

    private static void FullScriptTickWaitWarp(long now)
    {
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        int px;
        int py;
        TryGetPlayerXY(out px, out py);
        if (floor == FullScriptNav2WarpFloor)
        {
            if (now - _fullScriptPhaseAtMs < FullScriptWarpSettleMs)
            {
                _fullScriptNote = "已切 33000，稍等";
                return;
            }

            FullScriptEnter(FullScriptPhaseEncounter, "已切到 33000");
            return;
        }

        var onTile = floor == FullScriptNav2Floor
                     && Math.Abs(px - FullScriptNav2X) + Math.Abs(py - FullScriptNav2Y) <= 1;
        if (onTile)
        {
            if (!_fullScriptAtWarpTile)
            {
                _fullScriptAtWarpTile = true;
                try
                {
                    StopTaskNavigation(false);
                }
                catch
                {
                    // ignore
                }

                WriteLog("full-script wait warp at 33500 (32,14)");
            }

            if (now - _fullScriptPhaseAtMs >= FullScriptWarpWaitMs)
            {
                StopFullAutoScript("等切图 33000 超时");
                return;
            }

            _fullScriptNote = "停在传送格，等切 33000 " + ((now - _fullScriptPhaseAtMs) / 1000) + "s";
            return;
        }

        if (now - _fullScriptPhaseAtMs >= FullScriptWarpWaitMs)
        {
            StopFullAutoScript("未停在 33500 传送格");
            return;
        }

        if (_fullScriptLastActionMs == 0 || now - _fullScriptLastActionMs >= FullScriptNavRetryMs)
        {
            string how;
            if (TryNavigateTo(FullScriptNav2Floor, FullScriptNav2X, FullScriptNav2Y, out how))
            {
                _fullScriptNote = "不在传送格，再走 " + how;
            }
            else
            {
                _fullScriptNote = "再走失败: " + how;
            }

            _fullScriptLastActionMs = now;
        }
    }

    private static bool FullScriptSendLookNpc(int objindex)
    {
        try
        {
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
                return false;
            }

            look.Invoke(npcMgr, new object[] { dir, objindex });
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("full-script SendLookNpc EX " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryPickFirstDialogueOption()
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return false;
            }

            var wmdb = GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return false;
            }

            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null || buttonData.Length == 0)
            {
                return false;
            }

            int pickValue = -1;
            string pickName = null;
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

                if (IsDialogueCancelName(name))
                {
                    continue;
                }

                pickValue = value;
                pickName = name;
                break;
            }

            if (pickValue < 0)
            {
                return false;
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

            var loc = GetStaticMember("PlayerDataHolder", "location");
            var x = Convert.ToInt32(GetMember(loc, "x") ?? GetMember(loc, "X") ?? 0);
            var y = Convert.ToInt32(GetMember(loc, "y") ?? GetMember(loc, "Y") ?? 0);
            var seqno = Convert.ToInt32(GetMember(wmdb, "seqno") ?? 0);
            var objindex = Convert.ToInt32(GetMember(wmdb, "objindex") ?? 0);
            var uid = Convert.ToString(GetMember(wmdb, "m_Uid") ?? "") ?? "";
            var windowTypeObj = GetMember(wmdb, "windowType");
            var windowType = Convert.ToInt32(windowTypeObj ?? 0);

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
                return false;
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
            WriteLog("full-script pick first " + pickName + " v=" + pickValue + " seq=" + seqno);
            Tip("自动全套：已选" + pickName);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryPickFirstDialogueOption EX: " + RootMessage(ex));
            return false;
        }
    }

    private static void FullScriptDoEncounter()
    {
        if (GetEncounterStatus() != 0)
        {
            StopFullAutoScript("已开始遇敌");
            return;
        }

        if (_fullScriptLastActionMs == 0)
        {
            TrySendEscortAutoBattle("开始挂机");
            _fullScriptLastActionMs = NowMs();
            _fullScriptNote = "已发开始挂机";
            Tip("自动全套：开始遇敌");
            WriteLog("full-script encounter sent");
            return;
        }

        if (NowMs() - _fullScriptLastActionMs >= 3000)
        {
            StopFullAutoScript(GetEncounterStatus() != 0 ? "已开始遇敌" : "已发开始挂机");
        }
    }

    private static void TickFloraHeal()
    {
        if (!_floraHealActive)
        {
            return;
        }

        var now = NowMs();
        int floor;
        string floorName;
        int mapResId;
        TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
        TryGetPlayerXY(out var x, out var y);
        var inBattle = false;
        try
        {
            inBattle = Convert.ToBoolean(GetStaticMember("BattleDataHolder", "IsInBattle") ?? false);
        }
        catch
        {
            // ignore
        }

        if (inBattle)
        {
            _floraHealNote = "战斗中，等出战";
            return;
        }

        if (_floraHealDelayUntilMs > 0 && now < _floraHealDelayUntilMs)
        {
            return;
        }

        _floraHealDelayUntilMs = 0;
        if (_floraHealNeedRetry)
        {
            _floraHealNeedRetry = false;
            FloraHealRetryCurrentStep();
            return;
        }

        switch (_floraHealPhase)
        {
            case FloraHealPhaseDropHelmets:
                FloraHealTickDropHelmets(now);
                break;

            case FloraHealPhaseReturn:
                if (IsAtFloraHealReturn(floor, x, y))
                {
                    _floraHealStepTries = 0;
                    FloraHealBeginDelay(FloraHealPhaseDelayAfterReturn, "已回城点2，等 1 秒");
                    return;
                }

                FloraHealWaitOrRetry(now, FloraHealReturnWaitMs, "回城点2");
                break;

            case FloraHealPhaseDelayAfterReturn:
                FloraHealEnterStep(FloraHealPhaseToDoor, "导航 1000 (82,83)");
                FloraHealDoDoorNav();
                break;

            case FloraHealPhaseToDoor:
                if (floor == FloraHealHospitalFloor)
                {
                    _floraHealStepTries = 0;
                    FloraHealBeginDelay(FloraHealPhaseDelayAfterDoor, "已到 1111，等 1 秒");
                    return;
                }

                FloraHealWaitOrRetry(now, FloraHealDoorWaitMs, "切图 1111");
                break;

            case FloraHealPhaseDelayAfterDoor:
                FloraHealEnterStep(FloraHealPhaseToStand, "导航 (7,33)");
                FloraHealDoStandNav();
                break;

            case FloraHealPhaseToStand:
                if (IsAtFloraHealStand(floor, x, y))
                {
                    _floraHealStepTries = 0;
                    FloraHealBeginDelay(FloraHealPhaseDelayAfterStand, "已到护士旁，等 1 秒");
                    return;
                }

                FloraHealWaitOrRetry(now, FloraHealStandWaitMs, "1111 (7,33)");
                break;

            case FloraHealPhaseDelayAfterStand:
                _floraHealPhase = FloraHealPhaseLookNpc;
                _floraHealLastLookMs = 0;
                _floraHealNote = "点迪拉";
                WriteLog("flora-heal phase -> look npc");
                break;

            case FloraHealPhaseLookNpc:
                if (IsDialoguePanelOpen())
                {
                    FloraHealBeginDelay(FloraHealPhaseDelayAfterLook, "对话已开，等 1 秒");
                    return;
                }

                if (_floraHealLastLookMs > 0 && now - _floraHealLastLookMs < FloraHealLookRetryMs)
                {
                    return;
                }

                _floraHealLastLookMs = now;
                if (TryLookFloraHealNpc())
                {
                    _floraHealNote = "已点迪拉，等对话";
                    WriteLog("flora-heal LookNpc ok");
                }
                else
                {
                    _floraHealNote = "没找到迪拉，重试";
                    WriteLog("flora-heal LookNpc miss");
                }

                break;

            case FloraHealPhaseDelayAfterLook:
                _floraHealPhase = FloraHealPhasePick;
                _floraHealNote = "选全队回复";
                WriteLog("flora-heal phase -> pick");
                break;

            case FloraHealPhasePick:
                if (!IsDialoguePanelOpen())
                {
                    _floraHealPhase = FloraHealPhaseLookNpc;
                    _floraHealLastLookMs = 0;
                    _floraHealNote = "对话关了，再点迪拉";
                    return;
                }

                if (TryPickFloraHealOption())
                {
                    StopFloraHeal("已选全队回复", true);
                    return;
                }

                _floraHealNote = "选项未出现，等待";
                break;
        }
    }

    private static void FloraHealTickDropHelmets(long now)
    {
        if (TryDropOneCaptainGreenOrRedHelmet())
        {
            _floraHealDelayUntilMs = now + FloraHealDropDelayMs;
            return;
        }

        FloraHealEnterStep(FloraHealPhaseReturn, "回城记录点2");
        FloraHealDoReturn();
    }

    /// <summary>
    /// 队长背包 [8..67] 丢一件名字固定为「绿头盔」或「红头盔」的道具。
    /// </summary>
    private static bool TryDropOneCaptainGreenOrRedHelmet()
    {
        try
        {
            var uid = GetCaptainUid();
            if (string.IsNullOrEmpty(uid))
            {
                WriteLog("flora-heal drop-helm skip: no captain uid");
                return false;
            }

            var items = FindType("PlayerDataHolder")?.GetMethod(
                "GetItemDatasFromUid", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, new object[] { uid }) as System.Collections.IList;
            if (items == null)
            {
                return false;
            }

            var itemMgr = GetManagerInstance("ItemManager");
            var send = FindSendBackPackMessage(itemMgr);
            if (send == null)
            {
                WriteLog("flora-heal drop-helm skip: SendBackPackMessage missing");
                return false;
            }

            var end = Math.Min(FloraHealBagEnd, items.Count);
            for (var i = FloraHealBagStart; i < end; i++)
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
                if (data == null || !IsGreenOrRedHelmet(data))
                {
                    continue;
                }

                var locked = Convert.ToInt32(GetMember(data, "Locked") ?? 0);
                if (locked != 0)
                {
                    continue;
                }

                var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
                send.Invoke(itemMgr, new object[] { "丢弃道具", i, 1, uid });
                _floraHealStepTries++;
                _floraHealNote = "已丢[" + name + "] 第 " + _floraHealStepTries + " 件，等 1 秒";
                Tip("已丢弃[" + name + "]");
                WriteLog("flora-heal drop-helm idx=" + i + " name=" + name
                         + " n=" + _floraHealStepTries);
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("flora-heal drop-helm EX " + RootMessage(ex));
        }

        return false;
    }

    private static bool IsGreenOrRedHelmet(object data)
    {
        var name = Convert.ToString(GetMember(data, "Name") ?? "") ?? "";
        return name == "绿头盔" || name == "红头盔";
    }

    private static bool IsAtFloraHealReturn(int floor, int x, int y)
    {
        return floor == FloraHealReturnFloor
               && Math.Abs(x - FloraHealReturnX) + Math.Abs(y - FloraHealReturnY) <= 1;
    }

    private static bool IsAtFloraHealStand(int floor, int x, int y)
    {
        return floor == FloraHealHospitalFloor
               && Math.Abs(x - FloraHealStandX) + Math.Abs(y - FloraHealStandY) <= 1;
    }

    private static void FloraHealEnterStep(int phase, string note)
    {
        _floraHealPhase = phase;
        _floraHealStepTries = 0;
        _floraHealActionAtMs = 0;
        _floraHealNeedRetry = false;
        _floraHealNote = note;
        WriteLog("flora-heal phase -> " + FloraHealPhaseName(phase));
    }

    private static void FloraHealWaitOrRetry(long now, long waitMs, string label)
    {
        if (_floraHealActionAtMs == 0)
        {
            FloraHealRetryCurrentStep();
            return;
        }

        if (now - _floraHealActionAtMs < waitMs)
        {
            _floraHealNote = label + " 等待到位 " + _floraHealStepTries + "/" + FloraHealMaxTries;
            return;
        }

        if (_floraHealStepTries >= FloraHealMaxTries)
        {
            StopFloraHeal(label + " 三次未到位", false);
            return;
        }

        _floraHealNeedRetry = true;
        _floraHealDelayUntilMs = now + FloraHealStepDelayMs;
        _floraHealNote = label + " 未到位，1秒后第 " + (_floraHealStepTries + 1) + " 次";
        WriteLog("flora-heal retry wait " + label + " tries=" + _floraHealStepTries);
    }

    private static void FloraHealRetryCurrentStep()
    {
        switch (_floraHealPhase)
        {
            case FloraHealPhaseReturn:
                FloraHealDoReturn();
                break;
            case FloraHealPhaseToDoor:
                FloraHealDoDoorNav();
                break;
            case FloraHealPhaseToStand:
                FloraHealDoStandNav();
                break;
        }
    }

    private static void FloraHealBeginDelay(int nextPhase, string note)
    {
        _floraHealPhase = nextPhase;
        _floraHealDelayUntilMs = NowMs() + FloraHealStepDelayMs;
        _floraHealNote = note;
        WriteLog("flora-heal delay 1s -> " + FloraHealPhaseName(nextPhase));
        try
        {
            StopTaskNavigation(false);
        }
        catch
        {
            // ignore
        }
    }

    private static void FloraHealDoReturn()
    {
        if (_floraHealStepTries >= FloraHealMaxTries)
        {
            StopFloraHeal("回城点2 三次未到位", false);
            return;
        }

        _floraHealStepTries++;
        _floraHealActionAtMs = NowMs();
        if (!FloraHealSendReturnCity())
        {
            _floraHealNote = "回城发包失败 " + _floraHealStepTries + "/" + FloraHealMaxTries;
            WriteLog("flora-heal return send fail try=" + _floraHealStepTries);
            return;
        }

        _floraHealNote = "已发回城点2 " + _floraHealStepTries + "/" + FloraHealMaxTries;
        WriteLog("flora-heal return SendMenu(3,2) try=" + _floraHealStepTries);
    }

    private static bool FloraHealSendReturnCity()
    {
        var role = GetManagerInstance("RoleManager");
        if (role == null)
        {
            return false;
        }

        try
        {
            var data = FloraHealRecordIndex.ToString();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var send = role.GetType().GetMethod(
                "SendMenu", flags, null, new[] { typeof(int), typeof(string) }, null);
            if (send != null)
            {
                send.Invoke(role, new object[] { 3, data });
                return true;
            }

            send = role.GetType().GetMethod(
                "SendMenu",
                flags,
                null,
                new[] { typeof(int), typeof(string), typeof(string), typeof(string) },
                null);
            if (send == null)
            {
                return false;
            }

            send.Invoke(role, new object[] { 3, data, "", "" });
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("flora-heal SendMenu EX: " + RootMessage(ex));
            return false;
        }
    }

    private static void FloraHealDoDoorNav()
    {
        FloraHealDoNav(FloraHealDoorFloor, FloraHealDoorX, FloraHealDoorY, "门口");
    }

    private static void FloraHealDoStandNav()
    {
        FloraHealDoNav(FloraHealHospitalFloor, FloraHealStandX, FloraHealStandY, "护士旁");
    }

    private static void FloraHealDoNav(int floor, int x, int y, string label)
    {
        if (_floraHealStepTries >= FloraHealMaxTries)
        {
            StopFloraHeal(label + " 三次未到位", false);
            return;
        }

        _floraHealStepTries++;
        _floraHealActionAtMs = NowMs();
        string how;
        if (TryNavigateTo(floor, x, y, out how))
        {
            _floraHealNote = "导航" + label + " " + _floraHealStepTries + "/" + FloraHealMaxTries;
            WriteLog("flora-heal nav " + label + " " + floor + " (" + x + "," + y + ") "
                     + how + " try=" + _floraHealStepTries);
        }
        else
        {
            _floraHealNote = "导航失败: " + how;
            WriteLog("flora-heal nav fail " + label + " " + how + " try=" + _floraHealStepTries);
        }
    }

    private static bool TryLookFloraHealNpc()
    {
        var objindex = FindNpcObjIndexByNameOrPos(FloraHealNpcName, FloraHealNpcShortName, FloraHealNpcX, FloraHealNpcY);
        if (objindex < 0)
        {
            return false;
        }

        try
        {
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
                return false;
            }

            look.Invoke(npcMgr, new object[] { dir, objindex });
            WriteLog("flora-heal SendLookNpc obj=" + objindex + " dir=" + dir);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryLookFloraHealNpc EX: " + RootMessage(ex));
            return false;
        }
    }

    /// <summary>只按名字找 NPC（不依赖格子）。</summary>
    private static int FindNpcObjIndexByName(string fullName, string shortName)
    {
        return FindNpcObjIndexByNameOrPos(fullName, shortName, -999, -999);
    }

    /// <summary>优先按名字找迪拉，找不到再用 (7,32) 附近的 NPC。</summary>
    private static int FindNpcObjIndexByNameOrPos(string fullName, string shortName, int nx, int ny)
    {
        try
        {
            var holder = FindType("EntityDataHolder");
            object dictObj = holder?.GetProperty(
                "characterDatas",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null, null);
            if (dictObj == null)
            {
                dictObj = GetStaticMember("EntityDataHolder", "characterDatas");
            }

            var dict = dictObj as System.Collections.IDictionary;
            if (dict == null)
            {
                return -1;
            }

            var bestName = -1;
            var bestNameDist = int.MaxValue;
            var bestPos = -1;
            var bestPosDist = int.MaxValue;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var cd = e.Value;
                if (cd == null)
                {
                    continue;
                }

                var npcindex = Convert.ToInt32(GetMember(cd, "npcindex") ?? GetProp(cd, "npcindex") ?? -1);
                if (npcindex == -1)
                {
                    continue;
                }

                var objindex = Convert.ToInt32(GetMember(cd, "objindex") ?? GetProp(cd, "objindex") ?? -1);
                if (objindex < 0)
                {
                    continue;
                }

                var name = (Convert.ToString(GetMember(cd, "name") ?? GetProp(cd, "name") ?? "") ?? "").Trim();
                var ox = Convert.ToInt32(GetMember(cd, "x") ?? GetProp(cd, "x") ?? -999);
                var oy = Convert.ToInt32(GetMember(cd, "y") ?? GetProp(cd, "y") ?? -999);
                var dist = Math.Abs(ox - nx) + Math.Abs(oy - ny);
                var nameHit = (!string.IsNullOrEmpty(fullName) && name.IndexOf(fullName, StringComparison.Ordinal) >= 0)
                              || (!string.IsNullOrEmpty(shortName) && name.IndexOf(shortName, StringComparison.Ordinal) >= 0);
                if (nameHit && dist < bestNameDist)
                {
                    bestNameDist = dist;
                    bestName = objindex;
                }

                if (dist <= 1 && dist < bestPosDist)
                {
                    bestPosDist = dist;
                    bestPos = objindex;
                }
            }

            if (bestName >= 0)
            {
                WriteLog("flora-heal npc by name obj=" + bestName + " dist=" + bestNameDist);
                return bestName;
            }

            if (bestPos >= 0)
            {
                WriteLog("flora-heal npc by pos obj=" + bestPos);
                return bestPos;
            }
        }
        catch (Exception ex)
        {
            WriteLog("FindNpcObjIndexByNameOrPos EX: " + RootMessage(ex));
        }

        return -1;
    }

    private static bool TryPickFloraHealOption()
    {
        try
        {
            var npcMgr = GetManagerInstance("NpcManager");
            if (npcMgr == null)
            {
                return false;
            }

            var wmdb = GetMember(npcMgr, "wmdb");
            if (wmdb == null)
            {
                return false;
            }

            var buttonData = GetMember(wmdb, "buttonData") as Array;
            if (buttonData == null || buttonData.Length == 0)
            {
                return false;
            }

            int pickValue;
            string pickName;
            if (!TryChooseFloraHealButton(buttonData, out pickValue, out pickName))
            {
                return false;
            }

            var seqno = Convert.ToInt32(GetMember(wmdb, "seqno") ?? 0);
            var windowTypeObj = GetMember(wmdb, "windowType");
            var windowType = Convert.ToInt32(windowTypeObj ?? 0);
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
                WriteLog("flora-heal SendWindows missing");
                return false;
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
            WriteLog("flora-heal pick " + pickName + " v=" + pickValue + " seq=" + seqno);
            Tip("法兰治疗：已选" + pickName);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("TryPickFloraHealOption EX: " + RootMessage(ex));
            return false;
        }
    }

    private static bool TryChooseFloraHealButton(Array buttonData, out int pickValue, out string pickName)
    {
        pickValue = -1;
        pickName = null;
        var options = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>>();
        var dump = "";
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

            if (dump.Length > 0)
            {
                dump += ",";
            }

            dump += name + "=" + value;
            if (IsDialogueCancelName(name))
            {
                continue;
            }

            options.Add(new System.Collections.Generic.KeyValuePair<int, string>(value, name));
        }

        WriteLog("flora-heal buttons " + dump);
        for (var i = 0; i < options.Count; i++)
        {
            var n = NormalizeDialogueBtnName(options[i].Value);
            if (n.IndexOf(FloraHealOptionName, StringComparison.Ordinal) >= 0)
            {
                pickValue = options[i].Key;
                pickName = options[i].Value;
                return true;
            }
        }

        var idx = FloraHealOptionIndex - 1;
        if (idx >= 0 && idx < options.Count)
        {
            pickValue = options[idx].Key;
            pickName = options[idx].Value;
            return true;
        }

        return false;
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
        // 有指定坐标时必须到位才开遇敌，否则在路上/出发点开挂机会出问题
        if (TryGetEscortEncounterDest(out var df, out var dx, out var dy) && !IsAtEscortEncounterDest())
        {
            WriteLog("escort encounter refuse not-at-dest id=" + _escortMissionId
                     + " need=(" + df + "," + dx + "," + dy + ")");
            return;
        }

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
                 + " item=" + _escortWaitItemName
                 + (df != 0 ? (" at=(" + df + "," + dx + "," + dy + ")") : " (原地无寻路点)"));
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
    /// 模拟右侧任务点击：AutoWarpIndex=0 + TaskManager.RunTask。
    /// 卡楼梯：先 StopTaskNavigation 再点任务。
    /// 遇敌有坐标：只导航，到位后由 Tick 开挂机；无寻路点才立刻原地开。
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
            // 无寻路点的「原地遇敌」才可立刻开挂机；有坐标必须先导航到位，由 Tick 再开
            if (encounterFarm && !hasMove)
            {
                _escortLastDiag = "";
                WriteLog("ClickEscortTaskNav encounter-wait no move id=" + _escortMissionId
                         + " reason=" + reason);
                EnterEscortEncounterWait();
                return true;
            }

            if (encounterFarm && hasMove && IsAtEscortEncounterDest())
            {
                _escortLastDiag = "";
                WriteLog("ClickEscortTaskNav encounter already-at-dest id=" + _escortMissionId
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

            // 遇敌步骤也走 AutoWarpIndex=0（真正指定坐标）；到位后 Tick 再开挂机
            var warpIndex = 0;

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
            InvokeWalkStopMove(true);
        }
        catch
        {
            // ignore
        }
    }

    private static object GetWalkSystem()
    {
        try
        {
            var pm = GetManagerInstance("PlayerManager");
            var walk = GetProp(pm, "walkSystem") ?? GetMember(pm, "walkSystem");
            if (walk != null)
            {
                return walk;
            }

            var entity = GetProp(pm, "playerEntity") ?? GetMember(pm, "playerEntity");
            return GetProp(entity, "walkSys") ?? GetMember(entity, "walkSys");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// WalkSystem.StopMove(immediate, CustomCancel)。
    /// 必须带 CustomCancel：默认枚举是 None，OnTaskCallback 不会当取消处理。
    /// 一次清 curRequest + waitRequest（官方 onMapBeginLoad 也是这两处）。
    /// </summary>
    private static void InvokeWalkStopMove(bool immediate)
    {
        var walk = GetWalkSystem();
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
            if (m.GetParameters().Length >= 2)
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
                args[i] = immediate;
            }
            else if (ps[i].ParameterType.IsEnum)
            {
                try
                {
                    args[i] = Enum.Parse(ps[i].ParameterType, "CustomCancel");
                }
                catch
                {
                    args[i] = Activator.CreateInstance(ps[i].ParameterType);
                }
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

        stop.Invoke(walk, args);
    }

    private static bool WalkRequestIsActive(object req)
    {
        if (req == null)
        {
            return false;
        }

        try
        {
            var state = GetMember(req, "state") ?? GetProp(req, "state");
            if (state == null)
            {
                return true;
            }

            var name = Convert.ToString(state) ?? "";
            return name.IndexOf("Exit", StringComparison.Ordinal) < 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool WalkRequestIsWaitTransport(object req)
    {
        if (req == null)
        {
            return false;
        }

        try
        {
            var state = GetMember(req, "state") ?? GetProp(req, "state");
            var name = Convert.ToString(state) ?? "";
            return name.IndexOf("WaitTransport", StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// WalkSystem 过图中：pauseMove / WaitTransport / curMapLoadedAction。
    /// 官方恢复路径的第二处（第一处是 TaskManager.TryResume）。
    /// </summary>
    private static bool IsWalkWaitingMap()
    {
        try
        {
            var walk = GetWalkSystem();
            if (walk == null)
            {
                return false;
            }

            if (Convert.ToBoolean(GetMember(walk, "isPaused") ?? false))
            {
                return true;
            }

            if (GetMember(walk, "curMapLoadedAction") != null)
            {
                return true;
            }

            var cur = GetMember(walk, "curRequest");
            var wait = GetMember(walk, "waitRequest");
            return WalkRequestIsWaitTransport(cur) || WalkRequestIsWaitTransport(wait);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>WalkSystem 仍有活路径：正在走 / 暂停 / 等过图 / curRequest 或 waitRequest 未 Exit。</summary>
    private static bool IsWalkSystemPathBusy()
    {
        try
        {
            var walk = GetWalkSystem();
            if (walk == null)
            {
                return false;
            }

            if (Convert.ToBoolean(GetProp(walk, "IsMoving") ?? GetMember(walk, "mIsMoveing") ?? false))
            {
                return true;
            }

            if (IsWalkWaitingMap())
            {
                return true;
            }

            return WalkRequestIsActive(GetMember(walk, "curRequest"))
                   || WalkRequestIsActive(GetMember(walk, "waitRequest"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>官方过图第二处：WalkSystem.onMapEndLoad → resumeMove。</summary>
    private static void TryResumeWalkAfterMap(string reason)
    {
        try
        {
            var walk = GetWalkSystem();
            if (walk == null)
            {
                return;
            }

            var paused = false;
            try
            {
                paused = Convert.ToBoolean(GetMember(walk, "isPaused") ?? false);
            }
            catch
            {
                paused = false;
            }

            if (!paused)
            {
                return;
            }

            var m = walk.GetType().GetMethod(
                "resumeMove",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return;
            }

            m.Invoke(walk, null);
            WriteLog("escort WalkSystem.resumeMove ok reason=" + reason);
        }
        catch (Exception ex)
        {
            WriteLog("TryResumeWalkAfterMap EX " + RootMessage(ex));
        }
    }

    /// <summary>
    /// 过图后官方 EndLoadMap 会自己 TryResumeTaskPathAfterMapLoad / resumeMove。
    /// 这里只判断官方是否还在恢复，不要代调，避免过图抢跑。
    /// </summary>
    private static bool KeepOfficialPathAfterMap(string reason)
    {
        if (IsMapLoading() || IsWalkWaitingMap())
        {
            return true;
        }

        try
        {
            var tm = GetManagerInstance("TaskManager");
            if (tm != null)
            {
                var resume = Convert.ToBoolean(
                    GetMember(tm, "m_TaskPathResumePending")
                    ?? GetProp(tm, "m_TaskPathResumePending")
                    ?? false);
                var waitNpc = Convert.ToBoolean(
                    GetMember(tm, "m_TaskPathWaitingNpcMapChange")
                    ?? GetProp(tm, "m_TaskPathWaitingNpcMapChange")
                    ?? false);
                if (resume || waitNpc)
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
        if (mode == ModeCatchWild) return "抓野生宠";
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
        Tip(_minimized ? "面板已最小化（右上角）" : "面板已展开");
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

    private static void SetAnchoredTopRight(object rt, float x, float y, float w, float h)
    {
        SetProp(rt, "anchorMin", Vec2(1f, 1f));
        SetProp(rt, "anchorMax", Vec2(1f, 1f));
        SetProp(rt, "pivot", Vec2(1f, 1f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static void SetAnchoredBottomLeft(object rt, float x, float y, float w, float h)
    {
        SetProp(rt, "anchorMin", Vec2(0f, 0f));
        SetProp(rt, "anchorMax", Vec2(0f, 0f));
        SetProp(rt, "pivot", Vec2(0f, 0f));
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

    /// <summary>助手战斗页「跳过动画」是否默认开：hotfixdata 存在 seqchapter_skip_battle_anim.flag 即开。</summary>
    private static bool SkipBattleAnimDefaultEnabled()
    {
        try
        {
            foreach (var path in EnumerateHotfixAssetPaths(
                         "seqchapter_skip_battle_anim.flag",
                         "hotfixdata/seqchapter_skip_battle_anim.flag"))
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

    /// <summary>龙族循环按钮已卸（2026-08-26）。flag 逻辑保留备用，护航页不再调用。</summary>
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
