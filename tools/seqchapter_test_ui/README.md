# SeqChapterTestUi（百科总面板）

百科开/关面板。日志：游戏根目录 `SeqChapterTestUi.log`。  
总览：[`tools/模块地图.md`](../模块地图.md) · 入口 [`AGENTS.md`](../../AGENTS.md)

## 部署（只写 cross，勿碰 crosscopy）

在 **`E:\cross\魔力宝贝：序章`**（本仓库根）关闭游戏后：

```bat
魔力宝贝序章补丁\patcher\HotfixPatcher.exe wiki-test-ui-patch --hotfix cg37_Data\assets\hotfixdata\hotfix.dll.bytes --dll-only
```

产物：`cg37_Data/assets/hotfixdata/SeqChapterTestUi.dll.bytes`  
改完 `SeqChapterTestUi.cs` 后通常只需 `--dll-only`；首次接入或钩子变更才需完整 `wiki-test-ui-patch`。

## 切页

| 切页 | 内容 |
|------|------|
| **概况** | 队长地图号(`currentFloor`)/坐标/是否战斗；队伍血魔池；队员每日魔石 |
| **战斗** | 圆点单选：常规 / 抓宠 / 烧卡 / 抓宠不带宠；（九动隐藏，仅有 DLL 时显示）；**AI 战斗**（常规下）：血瓶/菲尔尼等；页内开关见下 |
| **脚本** | 「做日常」；**循环等待传送**；部分测试入口可能隐藏（半山测试/开书等逻辑保留） |
| **护航** | 队列式；**洗礼预备**；自动暂停响铃；静止恢复 / 连续失败暂停；战败 `forceQuitBattle` 暂停 |
| **导航** | 地图号/坐标导航；记录点位（`%USERPROFILE%\.seqchapter_helper\waypoints.json`） |
| **形象** | 进战宠物形象钩子 |
| **截获** | 开超银（账号道具仓），列出道具号和数量 |

### AI 页开关（摘要）

| 开关 | 作用 |
|------|------|
| **重写攻防序** | 开：写 `SeqChapterAiBattleTarget` 锚点；关：官方 AutoSelect |
| **攻击无效重写** | **已取消**。VIP 自动战斗不再改攻击无效目标 |
| **特殊 Boss** | 菲尔尼 / 金银角 / 声望挑战等开场关键字自动切模式 |

约定：[`seqchapter-ai-battle-virtual.mdc`](../../.cursor/rules/seqchapter-ai-battle-virtual.mdc) · [`VIP攻击无效接管计划.md`](../VIP攻击无效接管计划.md)

### 护航约定

- 战败保护（洗礼等独立脚本）：[`seqchapter-escort-defeat-guard.mdc`](../../.cursor/rules/seqchapter-escort-defeat-guard.mdc)
- 卡图清路径：[`seqchapter-official-nav-reset.mdc`](../../.cursor/rules/seqchapter-official-nav-reset.mdc)

## 实现要点

- HybridCLR **无 OnGUI** → `Update` + UGUI。
- 战斗模式通过各功能 DLL 的 `SetEnabled(bool)`（九动为 `ModeEnabled`）。
- 概况只反射读客户端数据；AI / 攻无钩在 AutoFight / DoVip 分发（见引擎 SuperAi* / TestUiExternal）。
