# 多功能统一入口计划（Plugin Host + 百科面板）

状态：**已搁置 / 判定不可行**（2026-07-29）

实机验证结论：第一期「百科打开运行时自绘 UGUI 面板」不稳定（`RectTransform` / `UnityEngine.UI` 反射创建易 `NullReferenceException`，Tip「插件面板打开失败」），收益不足以继续投入。  
后续百科轮换多模式方案亦否决。  
**现行方案保持不变**：各功能独立 DLL + 侧栏百科 Tip 开关 + 工具层 Pause/百科互斥；傻瓜补丁走「九动版 / 融合版」两包四选一。

以下原文仅作历史设计存档，勿按此实现主线。

---

前身：Pause 五选一 + 百科 Tip 开关互抢。  
原产品目标（已放弃）：

1. DLL 入口只保留一个（Pause → Plugin Host）。
2. 游戏内点侧栏百科 → 打开自己绘制的功能面板。
3. 在面板里勾选/切换功能；有逻辑冲突的不能同时勾选。

相关资产：神奇九动·DLL、自动烧卡、自动抓宠、盗贼辅助、注入桥接；IL 九动仍可不占 Pause。

---

## 1. 背景与问题

### 1.1 今日架构

```text
OnApplicationPause ──单 DLL──► 某一功能.Bootstrap()
MapSidebarPanel.OnClickWiki ──► 同一功能.OnWikiClick() + Tip 开关
```

- 谁后打补丁谁覆盖 Pause → 工具层强制五选一。
- 烧卡 / 抓宠 / 盗贼都改写百科 → 即使能多加载，按钮仍互抢。
- 烧卡与抓宠还都钩 `AutoFight_PlayerAction`（抓宠另钩 Pet / VIP）→ **IL 层也不能简单叠两个补丁**。

### 1.2 已拍板方向（相对旧 §5 候选）

| 旧候选 | 结论 |
|--------|------|
| Host 统一百科分发 Tip | 否：改为打开面板，不再用百科当「当前功能开关」 |
| 短按/长按分工 | 否 |
| 单一挂机模式枚举（百科循环） | 部分采纳：挂机类互斥，但入口改为面板勾选 |
| 弃用百科、改其它按钮 | 否：仍用百科，只改成「打开面板」 |

---

## 2. 目标架构

```text
Pause ──► SeqChapterPluginHost.Bootstrap()
            ├─ 读启用清单，依次 Load + 各功能.Bootstrap()（只做钩子/初始化，默认不自动开挂机）
            └─ 安装战斗分发器（见 §4）

百科 OnClickWiki ──► Host.OnWikiClick()
                      └─ 打开/关闭 SeqChapterPluginPanel（自绘 UI）

面板勾选 ──► FeatureRegistry.SetEnabled(id, on)
              ├─ 冲突组校验（§5）不通过 → 拒绝勾选 + Tip 说明
              └─ 通过 → 调对应功能 Enable/Disable
```

### 2.1 资产分工

| 资产 | 职责 |
|------|------|
| `SeqChapterPluginHost.dll.bytes` | **唯一** Pause 加载目标；百科入口；面板；功能注册表；冲突校验；战斗分发（可选同 DLL 内模块） |
| `SeqChapterNineAction.dll.bytes` 等 | 业务 DLL；保留 `Bootstrap`；**去掉各自百科 Tip 开关**（改由 Host 调 `SetEnabled`） |
| 清单 | 嵌入 Host，或 `hotfixdata/seqchapter_plugins.json`（优先嵌入，少文件依赖） |

Hotfix 内只保留：

- Pause → Host（`BridgeLoaderIlBuilder` 模板）
- 百科 → `Host.OnWikiClick`（无 Tip 开关字符串，或仅 Tip「面板打开失败」）
- 战斗分发钩（见 §4）：`AutoFight_PlayerAction` / Pet / VIP 等**只钩一次**进 Host

各 `*ExternalIlPatcher` 不再各自覆盖 Pause / 百科；改为：确保 Host 已装、登记本插件、部署本 DLL、（挂机类）**不再**各自改 `AutoFight_*`。

---

## 3. 自绘面板（层级最高、稳定）

### 3.1 为何不用 IMGUI / 为何不复用原版 Panel 资源

| 方案 | 评价 |
|------|------|
| `OnGUI` 浮层 | 实现快，但与 UGUI 混排时层级/遮挡不稳定，易被战斗 UI 盖住 |
| `UIManager.GetUIPanel<某原版面板>` 改皮 | 依赖具体面板生命周期，更新易碎 |
| **运行时拼 UGUI Canvas**（推荐） | 不依赖 prefab；`sortingOrder` 可控；可 `DontDestroyOnLoad` |

游戏内已有大量 `UIManager.GetUIPanel<T>` 用法（桥接 / GM / 百科下资源），证明 UGUI 栈可用；Host 面板走**独立 Canvas**，不挤进某业务 Panel。

### 3.2 面板规格

- 入口：侧栏 **百科** 点击 → 若面板未显示则创建并置顶；若已显示则关闭（或再点关闭按钮）。
- 层级：
  - 根节点挂独立 `Canvas`：`renderMode = ScreenSpaceOverlay`
  - `overrideSorting = true`，`sortingOrder = 32767`（或同档极高值）
  - 根上挂 `CanvasGroup`；需要时挡下层点击（`blocksRaycasts = true`）
- 稳定：
  - `DontDestroyOnLoad(root)`；场景切换后若被销毁则下次百科点击重建
  - Bootstrap 时只注册，不强制常驻显示
  - 打开失败 → `NotifyManager.Tip` 明确报错，不静默
- 布局（第一期从简，一屏完成）：
  - 标题：`序章插件` + 关闭
  - 分组勾选（Toggle）+ 当前状态一行字
  - 底部短说明：互斥项灰色/禁止勾选时 Tip

第一期控件用代码创建 `Image`/`Text`/`Toggle`/`Button`（UnityEngine.UI）；不引入新字体资源则用游戏默认 Font（反射取现有 UI 上的 Font）。

### 3.3 与百科其它补丁的关系

启用 Host 面板后：

- **禁止**再打 `wiki-download-res` / `wiki-label` / 旧版烧卡·抓宠·盗贼百科 Tip 补丁（工具层报错）。
- 百科原始打开百科页的行为被替换为开面板（与现网「百科改 Tip 开关」一致：本来就不进原百科）。

---

## 4. 战斗钩必须统一分发（关键）

烧卡、抓宠**不能**各自再改写一遍 `AutoFight_PlayerAction`，否则后打覆盖前打，面板互斥也救不了「钩子只剩一个」。

### 4.1 推荐

Hotfix 只打**一组**入口钩 → `Host.TryPlayerAutoFight()` / `TryPetAutoFight()`：

```text
TryPlayerAutoFight():
  if Seal.Enabled:  return Seal.TryPlayerAutoSeal()
  if Catch.Enabled: return Catch.TryPlayerAutoCatch()  // 含现有防御逻辑
  return false  // 走原版自动战斗

TryPetAutoFight():
  if Catch.Enabled: return Catch.TryPet...
  return false
```

VIP 路径（`DoVipPlayerAutoFight` / `DoVipPetAutoFight`）同样只钩到 Host 一次。

### 4.2 盗贼辅助

盗贼在 DLL 内反射订 `ExitBattle`，与 PlayerAction 钩不冲突；仍归入**挂机互斥组**（产品：三种挂机模式三选一），避免一边烧卡一边每 10 场去卖石等预期外组合。

### 4.3 九动 / 桥接

- DLL 九动：仍走 Magics + `ExpandAccountList` 等现有钩；由 Host 加载，**不占百科**。
- 助手桥接：若迁入 Host，确认外部助手协议不变；与 DLL 九动是否互斥见 §5（保持现状：互斥）。

---

## 5. 冲突组（面板 + 工具双重约束）

### 5.1 运行时（面板勾选）

| 冲突组 ID | 成员 | 规则 |
|-----------|------|------|
| `hangup` | 自动烧卡、自动抓宠、盗贼辅助 | **至多开一个**；勾新的自动关旧的，或拒绝并 Tip（推荐：**自动关旧的**并 Tip「已切换为 xxx」） |
| `nine` | 神奇九动·DLL、（若暴露）助手桥接影响九动的项 | DLL 九动 ⊥ 桥接（与现网一致）；IL 九动不在面板勾选（打补丁时已定） |
| 独立项 | 地图加速、侧栏改键等 inplace | 不进 Host 面板或只读展示「已由补丁启用」 |

慢速烧卡 vs 普通烧卡：同属烧卡功能内选项（子 Toggle），不进 `hangup` 多成员冲突。

### 5.2 打补丁时（工具）

Host 落地后：

- **取消** Pause 五选一。
- **保留**：IL 九动 ⊥ DLL 九动；Host 面板 ⊥ wiki-download / wiki-label；余量不足时的真实报错。
- 烧卡 + 抓宠 + 盗贼：**允许同打**（DLL 都部署 + 统一战斗钩），由面板保证不同时 Enable。
- 傻瓜包仍可只打子集（体积/场景需要时）。

### 5.3 API 草图（功能 DLL）

```csharp
// 各功能尽量收敛到：
public static void Bootstrap();           // 初始化、订事件；默认 Enabled=false
public static bool IsEnabled();
public static void SetEnabled(bool on);   // 取代 OnWikiClick 开关语义
// 烧卡/抓宠另保留供 Host 分发调用的 Try* 方法
```

旧 `OnWikiClick`：Host 过渡期可内部转调 `SetEnabled(!IsEnabled())`，正式面板上线后删除百科 Tip IL。

---

## 6. 分期

### 第一期 — Host + 空面板 + 百科入口（验收骨架）

1. ~~新建 `tools/seqchapter_plugin_host/`~~（已有：`Bootstrap` / `OnWikiClick` / 运行时 Canvas 面板 + hangup 占位互斥）
2. ~~`PluginHostIlPatcher`~~（`plugin-host-patch`；Pause → Host；百科 → Host.OnWikiClick）
3. ~~组合 GUI / `apply_combo_patch`~~：勾选「插件 Host·实验」（暂与其它扩展 DLL 互斥）
4. [ ] 进游戏点百科验证：面板最上层、可开关、切场景可再开
5. 文档 / GUI：标明「百科 = 插件面板」

### 第二期 — 挂机三件套进面板

1. 烧卡 / 抓宠 / 盗贼改 `SetEnabled`；去掉各自 Pause/百科补丁路径。
2. Hotfix 战斗钩改为 Host 分发（§4）。
3. 面板 `hangup` 组互斥；Tip 提示切换。
4. 组合补丁允许三 DLL 同打；傻瓜包可出「Host 全家桶」或仍出单功能包。

### 第三期 — 九动 DLL / 桥接进 Host

1. Pause 五选一彻底删除。
2. 面板增加九动 DLL 开关（若需运行时关）；桥接按风险决定是否面板化。
3. 冲突组 `nine` 落地；验收多开组合。

### 非目标（明确不做）

- 不把全部业务揉成单一胖 DLL（Host 只调度 + UI + 分发）。
- 不在 Pause 方法体内联多段 `LoadBytes`。
- 第一期不做精美 UI / 动画；先稳定层级与互斥。

---

## 7. 验收标准

### 第一期

- [ ] 干净底稿可打：加速类 + Host；体积/余量可接受。
- [ ] 进游戏点百科 → 自绘面板出现在最上层；再点百科或关闭可关掉。
- [ ] 进战斗 / 切图 / 开关其它原版界面后，再开面板仍正常（不永久丢失引用）。
- [ ] 未勾其它占百科补丁；误勾 wiki-download 时工具明确报错。

### 第二期

- [ ] 烧卡、抓宠、盗贼 DLL 均由 Host 加载；百科不再出现三套 Tip 开关文案覆盖。
- [ ] 面板勾选烧卡后行为与现网百科 Tip 开烧卡一致；抓宠 / 盗贼同理。
- [ ] 勾选抓宠时若烧卡已开 → 烧卡关闭且抓宠开启（或等价互斥），战斗钩只走抓宠。
- [ ] 三角色同打补丁后，未在面板开启前不自动挂机。

### 第三期

- [ ] DLL 九动 + 盗贼（面板关着）可同加载；需要时面板只开盗贼。
- [ ] DLL 九动 + 桥接仍被拒绝或面板互斥，行为与文档一致。
- [ ] `from_orig` 重打幂等：无双 Host、无双战斗钩。

---

## 8. 参考文件

| 区域 | 路径 |
|------|------|
| Loader IL | `tools/hotfix_patcher/BridgeLoaderIlBuilder.cs` |
| 现百科 Tip 三件套 | `AutoSealExternalIlPatcher.cs` / `AutoCatch…` / `AutoSell…` |
| 九动 / 桥接 | `BattleNineActionExternalIlPatcher.cs` / `HelperBridgeIlPatcher.cs` |
| 打开原版面板参考 | `WikiOpenDownloadResIlPatcher.cs`、`CustomerBtnGmIlPatcher.BuildOpenPanelBody` |
| 功能 DLL | `tools/seqchapter_auto_seal|catch|sell|nine_action|helper_bridge/` |
| 互斥校验 | `魔力宝贝序章补丁/scripts/apply_combo_patch.py` |
| GUI | `seqchapter_combo_gui.py` / `foolproof_gui.py` |

---

## 9. 备忘

- 实现前确认客户端有**干净** `hotfix.dll.bytes`；打补丁后勿写回 `.orig` / `neworig`。
- 当前体积目标以维护文档为准（约 **7,089,152**）；Host + 面板会增加外置 DLL，不必然增大 hotfix 本体（Pause/百科/分发钩仍要吃 `.text` 余量，分发钩设计需紧凑）。
- 本计划落地前，现有「单 DLL + 百科 Tip」组合包仍可继续发布到 `E:\cross序章\发布plugin`。
- 工作区唯一插件仓库：`E:\cross序章\crossgate_plugin`。
