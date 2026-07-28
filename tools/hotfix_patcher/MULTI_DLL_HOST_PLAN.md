# 多 DLL 共存整改计划（Plugin Host）

状态：**暂缓实现**（先落文档，有空再做）  
相关互斥：神奇九动·DLL / 自动烧卡·DLL / 自动抓宠·DLL / 盗贼辅助·DLL / 注入桥接·DLL  
例外：IL 九动不占 Pause 加载槽，可与烧卡/抓宠/盗贼等同打（与九动·DLL 互斥）。

---

## 1. 背景与问题

当前各「·DLL 版」功能通过改写 `HotfixEntry.OnApplicationPause` 注入**单 DLL 加载器**：

`FileUtil.LoadBytes("hotfixdata/<One>.dll.bytes")` → `Assembly.Load` → `Type.Bootstrap()`

谁后打补丁谁覆盖 Pause 方法体，因此 GUI / `apply_combo_patch.py` 强制五选一。

另外，烧卡 / 抓宠 / 盗贼都改写侧栏 **百科 `OnClickWiki`** 做 Tip 开关，即使 Pause 能加载多个 DLL，**百科按钮仍会互抢**，需要单独规划，不能只靠 Host。

2026-07-28 客户端更新后 `.text` 裸余量更紧（干净底稿约数百字节级），IL 九动偏吃余量；多功能并存更倾向「Pause 只挂一个小加载器 + 功能 DLL 外置」。

---

## 2. 目标

1. **允许同时加载多个功能 DLL**（九动 DLL、烧卡、抓宠、盗贼、桥接中的任意组合，以清单为准）。
2. Hotfix 内 Pause 槽位仍只保留**一段**加载 IL（余量友好）。
3. 各功能 DLL 尽量保持现有 `Bootstrap()` 契约，少改业务逻辑。
4. **百科按钮**：本阶段只定义问题与候选方案，**不在 Host 第一期强行合并 UI**。

非目标（第一期不做）：

- 不重写 IL 九动为 DLL（可继续与 Host 共存）。
- 不把全部功能揉成单一「胖 DLL」。
- 不在 Pause 方法体内联多段 `LoadBytes`（VA/可维护性差）。

---

## 3. 推荐方案：Plugin Host 调度 DLL

### 3.1 结构

| 资产 | 职责 |
|------|------|
| `SeqChapterPluginHost.dll.bytes` | Pause 唯一加载目标；读清单；依次 `Assembly.Load` + 调各 `Bootstrap` |
| `SeqChapterNineAction.dll.bytes` 等 | 现有功能 DLL，尽量不改入口名 |
| 清单（建议） | 嵌入 Host，或旁路 `hotfixdata/seqchapter_plugins.json`（需评估 FileUtil 读文本能力） |

Pause 伪代码：

```text
Host.Bootstrap()
  for each plugin in enabled_list:
      load hotfixdata/<plugin>.dll.bytes
      invoke <TypeName>.Bootstrap()
```

补丁器侧：现有 `BridgeLoaderIlBuilder` 已是「单路径加载器」模板，改为只指向 Host 即可；各 `*ExternalIlPatcher` 不再各自覆盖 Pause，改为：

- 确保 Host 加载器已安装（幂等）；
- 把本功能登记进 Host 清单 / 构建参数；
- 仍负责本功能在 hotfix 内的**专用钩子**（Magics、AutoFight 布尔钩、百科等——百科见第 5 节）。

### 3.2 与现有代码的衔接

| 现有 | 用法 |
|------|------|
| `BridgeLoaderIlBuilder` | 继续生成 Pause→Host 的 IL |
| 各功能 `Bootstrap()` | Host 统一调用 |
| `WikiChatTestExternalIlPatcher` | 已证明非 Pause 加载点可与 Pause 功能并存，可作旁路参考 |
| GUI / `apply_combo_patch` 互斥表 | Host 落地后改为「可多选」，改为校验百科/余量等真实约束 |

### 3.3 风险

- HybridCLR / `Assembly.Load(byte[])`：与现网单 DLL 路径相同，风险低。
- Bootstrap 顺序：九动 vs 战斗钩、百科 vs Tip，需固定顺序并写进清单。
- 重复打补丁：Host 加载器安装必须幂等；清单合并不能丢已启用插件。
- 助手桥接：若桥接也改走 Host，需确认与外部助手进程约定仍成立。

---

## 4. 建议分期

### 第一期（最小可用）

1. 新增 `tools/seqchapter_plugin_host/`（或同级命名）实现 Host + `Bootstrap`。
2. 新增 / 改造 patcher：`PluginHostIlPatcher`（Pause → Host）。
3. 先接入 **DLL 九动 + 盗贼辅助** 两条，验证双 Bootstrap。
4. 组合补丁 / GUI：这两项允许同打；其它 DLL 仍可暂互斥。
5. 文档与傻瓜包说明同步。

### 第二期

- 烧卡、抓宠、桥接迁入 Host 清单。
- 全面去掉 Pause 层五选一（改为真实冲突检测）。

### 第三期（与 Host 可并行，但依赖产品决策）

- **百科按钮重新规划**（见下节）。
- 可选：更多逻辑迁出 hotfix，进一步省余量。

---

## 5. 百科按钮：必须另做规划（Host 解决不了）

### 5.1 现状

| 功能 | 百科用法 |
|------|----------|
| 自动烧卡 | `OnClickWiki` → Tip 开关烧卡 |
| 自动抓宠 | 同上，抓宠 Tip |
| 盗贼辅助 | 同上，盗贼 Tip |
| 百科→资源下载 / 百科文字 | 也会占百科，与上列冲突 |

同一点击只能进一套逻辑；Host 只解决「多个 DLL 能加载」，**不自动解决「一个按钮给谁用」**。

### 5.2 候选方向（待定，需产品拍板）

1. **Host 统一百科分发**  
   hotfix 只钩一次 `OnClickWiki` → 调 `Host.OnWikiClick`；由 Host 按「当前主模式」或菜单分发给插件。  
2. **点击 / 长按分工**  
   短按开关 A，长按开关 B（或弹出简易选择 Tip）。  
3. **单一「挂机模式」枚举**  
   烧卡 / 抓宠 / 盗贼互为模式，百科只切换模式 + 显示当前状态（与现「只能开一种挂机」产品一致时最简单）。  
4. **弃用百科，改标题栏/其它冷门按钮**  
   成本高，需找稳定 UI 入口。

### 5.3 第一期建议

- Host 允许多 DLL **加载**。
- 若启用的插件中 **超过一个需要百科**，组合补丁仍报错或提示「请只选一个占百科的功能」，直到第 5.2 落地。
- 文档与 GUI 文案写清：**加载共存 ≠ 百科共存**。

---

## 6. 验收标准（第一期）

- [ ] 干净底稿上可同时打上：正常加速类 inplace 补丁 + Host + DLL 九动 + 盗贼 DLL。
- [ ] 进游戏后两个功能的 `Bootstrap` 均执行（日志或标题/Tip 可观察）。
- [ ] 仅开盗贼时百科 Tip 行为与现网一致；仅开九动 DLL 时无百科冲突。
- [ ] 同时勾选烧卡+盗贼（两者都要百科）时，工具给出明确错误/指引，而不是静默覆盖。
- [ ] `from_orig` 重打幂等，不产生双 Host 或坏 Pause。

---

## 7. 参考文件

- `tools/hotfix_patcher/BridgeLoaderIlBuilder.cs`（及同类 Loader 构建）
- `tools/hotfix_patcher/BattleNineActionExternalIlPatcher.cs`
- `tools/hotfix_patcher/AutoSealExternalIlPatcher.cs`
- `tools/hotfix_patcher/AutoCatchExternalIlPatcher.cs`
- `tools/hotfix_patcher/AutoSellExternalIlPatcher.cs`
- `tools/hotfix_patcher/HelperBridgeIlPatcher.cs`
- `魔力宝贝序章补丁/scripts/apply_combo_patch.py`（DLL 互斥校验）
- `魔力宝贝序章补丁/scripts/seqchapter_combo_gui.py`（互斥 UI）

---

## 8. 备忘

- 实现前先确认客户端有**干净** `hotfix.dll.bytes` 底稿；打补丁后勿把已打补丁文件同步回 `.orig` / `neworig`。
- 本计划不阻塞当前「IL 九动 + 单 DLL」或「加速 + DLL 九动」等现有组合的使用。
