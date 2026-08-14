# Boss属性计算器（增强版独立工具）

独立 GUI 工具：输入 Boss 名称 + 等级 + 血量（蓝量可选）→ 锁定成长倍率 rate、掉档、攻防敏精回估计。
血量留空时，按常见倍率（100/50/20）与全倍率绝对范围展示七维属性范围。

## 运行

```bash
python tools/boss_calc/boss_stat_gui.py
```

依赖：仅标准库（tkinter）。档位数据随目录自带 `pet_rank.bin`（也可放同目录 `pet_rank_slim.csv`）。

命令行自检：

```bash
python tools/boss_calc/boss_stat_estimator.py
```

## 目录内容

| 文件 | 说明 |
|---|---|
| `boss_stat_gui.py` | tkinter GUI 入口 |
| `boss_stat_estimator.py` | 增强版算法（类封装 `BossStatEstimator`） |
| `pet_rank.bin` | 档位表（含 BOSS 超模），PRK1 格式 |

## 与远端 AI 算法的差异（重要：远端 AI 有误，建议按本目录修正）

远端 AI 有两处实现，算法一致但存在相同错误：

- Python：`tools/boss_stat_estimator.py`（函数式 API：`estimate_enemy` / `enum_drops_3125` / `soft_in_range`）
- C#：`tools/seqchapter_test_ui/BossStatEstimator.cs`（`EstimateEnemy` / `EnumDrops3125` / `SoftInRange`）

### 差异 1：掉档枚举时成长系数固定用 0.0425（错）

远端两处 `EnumDrops3125` 均固定 `CoeffMid = 0.0425`（Python 版 `coeff: float = COEFF_MID` 默认参数；C# 版 `Factor(level, rate, CoeffMid)`）。

- 问题：野生/BOSS 成长系数应由**掉档总数**决定：0系=0.045 → 5系=0.040，每掉 4 档降 0.001，即 `coeff = 0.045 − 0.00025 × 掉档总数`。0.0425 只是「掉 10 档」的中点值。
- 后果：满档 BOSS（掉档少）被低估、高掉档 BOSS 被高估，掉档推断系统性偏差。
- 修正：见本目录 `_rank_coeff(drop_total)`。枚举每套掉档时按该掉档总数动态取系数。

### 差异 2：软匹配容差按「上限绝对值」而非「区间宽度」（错）

远端 `SoftInRange` 的 pad 计算为 `pad = max(hi * tol, 8.0)`，与区间**上限绝对值**成正比。

- 问题：高血量 BOSS 的 ±5% 容差会被放大到足以把明显越界的倍率也放进软匹配。
  例：80 级帕鲁凯斯的亡灵 HP=8437，rate=20 上限 8183，越界 254，仍被误判为软匹配并混入结果。
- 修正：`pad = max((hi - lo) * tol, 8.0)`，与区间宽度成正比（本目录 `_soft_in_range`）。

### 差异 3：倍率锁定策略（推荐按本目录调整）

远端 `FindRate` 先试 20/50/100（软容差命中即停），再倍增+扫描；本目录 `enumerate_schemes` 按：

1. 常见倍率 100/50/20 优先；存在**硬匹配**时只返回硬匹配（剔除软匹配倍率）；
2. 无硬匹配才接受软匹配；无软匹配再全扫描 20..640；
3. 全扫描仍无硬匹配时补 5 步长。

这样可避免软匹配把实际越界的倍率混入（帕鲁凯斯例），且倍率推荐顺序符合玩家约定（100 > 50 > 20）。

### 差异 4：同名多档位变体（C# 版缺失）

- C# `Lookup` 用 `Dictionary<string, PetRank>`，同名只保留**一条**；本目录用 `name → list[PetRank]`，支持同名多档位变体（如「亡灵骑士」「丘比特」），并提供变体选择。
- 建议：C# 版 `_byName` 改为 `Dictionary<string, List<PetRank>>`，`EstimateBest` 遍历全部变体取最优。

### 差异 5：多方案输出（增强，非纠错）

远端仅返回单个最佳方案；本目录 `enumerate_schemes` 枚举 top-N 方案并按（硬匹配, 掉档惩罚, 倍率常见性）排序，GUI 可同时展示多个候选档位，供实战中结合更多观测缩小范围。

## 备注

- 算法移植自 `tools/seqchapter_test_ui/BossStatEstimator.cs`，本目录为 Python 独立增强实现。
- 档位表来源：`tools/export_pet_rank_bin.py` 导出的 `tools/pet_rank.bin`。
