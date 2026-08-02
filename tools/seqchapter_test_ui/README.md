# SeqChapterTestUi（百科总面板）

状态：**可用骨架**（Update + UGUI）

百科开/关面板。日志：`游戏目录/SeqChapterTestUi.log`。

## 切页

| 切页 | 内容 |
|------|------|
| **概况** | 队长地图号(`currentFloor`)/坐标/是否战斗；队伍血魔池；队员每日魔石 |
| **战斗** | 圆点单选互斥：常规 / 抓宠 / 烧卡 / 抓宠不带宠；（九动为隐藏项，仅带 DLL 时显示）；**超级AI**（仅常规/九动）：关 VIP 自动技走普通 Auto，模拟阶段只采战场信息写日志 |
| **脚本** | 「做日常」（「测试铃声」「刷灵堂」入口已隐藏） |
| **护航** | 队列式；自动暂停响铃；静止5秒恢复，连续10次失败自动暂停 |
| **导航** | 显示当前地图号/坐标；输入地图号+坐标导航；记录点位（共用 `%USERPROFILE%\.seqchapter_helper\waypoints.json`） |

## 实现要点

- HybridCLR **无 OnGUI** → 必须用 `Update` + UGUI。
- 战斗模式通过各功能 DLL 的 `SetEnabled(bool)`（九动为 `ModeEnabled`）。
- 概况只反射读客户端数据，不新打 IL 钩。

补丁：`HotfixPatcher wiki-test-ui-patch`
