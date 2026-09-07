# VIP「攻击无效」等六件套：DLL 接管计划（未做）

> 状态：**仅文档，尚未实现。**  
> 背景：用户希望 VIP 套「攻击无效」时**不要按血量**，而是找**还没有该效果**的友方套；DLL 可设优先人/宠；全员已有则不套。

---

## 1. 官方现状（已核实）

技能表：`AutoSkillType=14`，六件套 SkillId：

| SkillId | 名称 | 判定缺 buff（`BC_FLAG`） |
|---|---|---|
| 55 | 攻击反弹 | `ATTACK_REBOUND` `0x200000` |
| 56 | 魔法反弹 | `MAGIC_REBOUND` `0x400000` |
| 57 | 攻击吸收 | `ATTACK_ABSORB` `0x800000` |
| 58 | 魔法吸收 | `MAGIC_ABSORB` `0x20000000` |
| 59 | **攻击无效** | **`ATTACK_INVALID` `0x40000000`** |
| 60 | 魔法无效 | `MAGIC_INVALID` `0x80000000` |

入口：`BattleProcesser.TryUseVipAutoSkill` → `case 14`（反编译约 310933 行）。

**官方逻辑（同时满足才套）：**

1. 友方侧 Index 顺序扫 `num .. num+9`（`battlePlayerIndex<10` → 0–9，否则 10–19；**不是** `placeToIndex`）
2. 未死亡
3. **`HP% ≤ UseCondition1`**（VIP 设置里的「友方血量≤P%」，默认常为 100）
4. **对应 buff 未挂上**（59 即 `!status.HasFlag(ATTACK_INVALID)`）
5. `CheckCanSelect(tech.Target, self, target)` 通过
6. 命中第一个就 `SendPlayerVipAutoSkill` / `SendPetVipAutoCommand` 并 `break`

结论：

- **有**判断「身上有没有攻击无效」（及另外五件套各自的 flag）。
- **仍强制血量门槛**：P% 设低时，满血/高血友方即使没攻无也**不会**被套。
- **无**「优先人 / 优先宠」；扫表是固定 Index 升序（队长位常先于宠）。
- **无**专用 `beUsedSkill` 去重（与恢复的 `Recover` 不同）；同回合是否再套靠「已有 flag」。
- 全部活着且都已有对应 buff → 本技能本回合不放（`result` 保持 false，继续列表下一项）。

---

## 2. 用户期望（接管后）

针对 Type14（至少 SkillId 59 攻击无效；建议六件套同一套规则）：

| 项 | 期望 |
|---|---|
| 血量 | **不再**用 `HP% ≤ P` 作为是否套的条件 |
| 目标 | 我方**活人**里，**没有**对应 `BC_FLAG` 的单位 |
| 优先 | DLL/面板可设：**优先人物** 或 **优先宠物**（同优先档内再定序） |
| 停手 | 全部活着的可选中目标**都已有**该效果 → **不套**（本技能本回合跳过） |
| 官方 VIP 列表 | 仍从玩家已配的 VIP 自动技能列表触发；只改「何时 / 对谁」 |

可选（实现时再定）：

- 是否保留官方 P% 作「可选开关」（默认关=忽略血量）。
- 魔法无效 / 反弹 / 吸收是否同一 DLL 一并接管（推荐：Type14 统一接管，按 SkillId 换 flag）。

---

## 3. 可行性：DLL 接管

**可以。** 不必再打易卡战斗的 `AutoSelect` IL。

推荐路径（与抓宠/日常等一致）：

1. **IL 钩子（小）**：在 `TryUseVipAutoSkill` 的 `case 14` 开头（或整 case 替换）调外部 DLL；DLL 返回 `true`=已处理（含「故意不放」），官方 case 跳过。
2. **DLL**：读 `BattleRoleDic`、自身 `tech.SkillId` / `Target`、我方 Index 范围；按配置排序；`SendBattleCommond` / 复用官方 `SendPlayerVipAutoSkill` 反射调用发包。
3. **配置**：助手面板或 `seqchapter_*.txt` / json：`prefer=player|pet`，以及是否接管 Type14。

备选：不钩 VIP，由 AI 战斗在虚拟/实发层自己排「套攻无」指令——与「改 VIP 行为」目标不同，本计划以 **接管 VIP Type14** 为准。

**不要**再往 `BattleRoleSelector.AutoSelect` 塞选目标逻辑（曾导致未开 AI 也卡 AUTO）；Type14 官方本就带 `targetRole`，不经合击/随机。

---

## 4. 建议选目标算法（实现时）

```
alive = 我方 10 格内未死、CheckCanSelect 通过的单位
need  = alive 中 !HasFlag(对应 BC_FLAG) 的单位
若 need 为空 → 不放，返回已处理
排序 need：
  1) 优先档：prefer==player → IsPlayer 在前；prefer==pet → 宠在前
  2) 同档内：Index 升序（或站位序，实现时二选一写死）
取 need[0] 发包
```

对应 flag 映射与官方 `switch (tech.SkillId)` 一致（见 §1）。

读状态：`RoleData.status` / `Char.Bcflag`，与现网 `GetBattleRoleStatus` / 超级 AI 读 `bc` 相同。

---

## 5. 验收要点（以后做时）

- [ ] VIP 列表含「攻击无效」，友方全无 buff、任意血量 → 会套，且符合「优先人/宠」
- [ ] 已有 `ATTACK_INVALID` 的单位不再被选
- [ ] 全部活人已有攻无 → 本回合不放该技（可观察到列表落到后续技能/普攻）
- [ ] 未开接管 / DLL 失败 → 回落官方 case 14（含血量条件）
- [ ] 关 AI、普通/VIP AUTO 不卡死（不碰 AutoSelect）

---

## 6. 相关代码位置

| 项 | 位置 |
|---|---|
| 官方 Type14 | `tools/hotfix_ilspy/...` → `TryUseVipAutoSkill` case 14 |
| 技能表 | `tools/vip_autoskill_type_map.txt` AutoSkillType=14 |
| VIP 总览 | `tools/VIP自动战斗逻辑整理.md` §4 |
| BC_FLAG | `BC_FLAG.ATTACK_INVALID` 等（同反编译 enum） |
| 现有状态读取参考 | `SeqChapterTestUi` `GetBattleRoleStatus` / `FormatBcStatus` |

---

## 7. 明确不做（本文档阶段）

- 不改 hotfix IL、不建 DLL、不改助手面板。
- 不恢复已拆除的 `ai-target-autoselect` / `focusFire` AutoSelect 注入。
