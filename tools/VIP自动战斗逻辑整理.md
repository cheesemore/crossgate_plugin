# VIP 自动战斗完整逻辑（移植用）

> 依据：`hotfix.dll.bytes.decompiled.cs` 的 `BattleProcesser.DoAutoFight` / `DoVipPlayerAutoFight` / `DoVipPetAutoFight` / `TryUseVipAutoSkill`  
> 技能名/类型：`skill_tbskillconfig.bytes` → `tools/vip_autoskill_type_map.txt`  
> 整理日期：2026-09-04

---

## 1. 何时走 VIP Auto

前提：`BattleManager.IsAutoBattle == true`，到号后 `DoAutoFight()`。

```
月卡开（或 MonthCard bypass 补丁）?
  ├─ 否 → 永远普通 AutoFight_PlayerAction / _PetAction / _PlayerAction2
  └─ 是
        人物：GetAutoSkillSwitch(uid,0)==1 且 GetSortedSkillDataByUid(uid,uid) 非空
              → DoVipPlayerAutoFight
              否则 → AutoFight_PlayerAction
        宠：GetAutoSkillSwitch(uid,1)==1 且该宠 VIP 列表非空
              → DoVipPetAutoFight
              否则 → AutoFight_PetAction
        无宠二动（PET_MENU_NON）：人物 VIP 开时优先 focusFire 普攻 → 列表里第一个 73/74 → 否则普攻
```

开关与列表来源：`BattleAutoSkillManager`（协议 `LSSPROTO_JOBSWITCH_FUNC`「请求自动技能设置」）。

列表字段 `Proto_AutoSkillInfo`：

| 字段 | 含义 |
|---|---|
| `SkillId` | 技能本体 ID（宠侧匹配的是 `TechId`） |
| `SkillIndex` | 人物=Magic 槽下标；宠=`PetSkills` 下标 |
| `UseLevel` | 要用的技能等级（人物取 `techs[UseLevel-1]`） |
| `Priority` | 越小越先（列表已按 Priority 升序） |
| `UseCondition1` | 条件阈值（敌人数 / 血蓝%） |

人物技可用性 `TryGetUseTechData`：`UseLevel>=1`、Magic `useFlag==1`、`skillId` 对上、未遗忘、非 CD、`tech.Use`、当前蓝 `PlayerMp >= tech.Fp`、`tech.Flg`。

---

## 2. VIP 人物出手总序（`DoVipPlayerAutoFight`）

1. **集火** `focusFireIndex != -1` → `PlayerFocusFireAttack`  
   - 只试 `SingleAttack==1` 的列表项；73=普攻打标记目标；其它单体技打标记目标（过 `CheckCanSelect` / 近战前排挡）  
   - **现网**：点标记不写 `focusFireIndex`，此步几乎永不触发  
2. **护卫标记** `needGuardDict` 非空 → `PlayerGuard`（列表里必须有 SkillId=7）  
   - 同样现网字典常空  
3. **按 VIP 列表 Priority 升序** 逐条 `TryUseVipAutoSkill`（条件不满足则下一条）  
4. 全部失败 → **兜底普攻** `defaultPlayerAttack()`（目标走合击/随机 `AutoSelect`）

发包：

- 普攻：`H|{target:X}`
- 技能：`S|{skillIndex:X}|{tech.Index:X}|{target:X}`
- 防御：`G`（type5 且 SkillId=74）

---

## 3. VIP 宠出手总序（`DoVipPetAutoFight`）

1. 有 `focusFireIndex` 且目标存活：列表中第一个 `SingleAttack==1`、`Use`、TechId 对上、蓝够 → `W|{tech.Index}|{target}`  
2. `needGuardDict`：找 `SkillId/100==7`（宠护卫类）打字典里第一个可护卫队友  
3. 按列表 `TryUseVipAutoSkill`（与人物同一套 type 分支）  
4. 全失败：若人物尚未结束则 `SendPlayerIdleCommand`，否则 `W|FF|FF`

宠技蓝耗看 `petUnit.RoleData.Char.Mp >= tech.Fp`。

---

## 4. `TryUseVipAutoSkill`：每种 AutoSkillType

读表：`TbSkillConfig.Get(skillId).AutoSkillType`。  
UI 文案见 `Com_AutoSkillCondition.SetCondition`。  
条件默认：敌人数类默认 8；百分比类默认 100（滑条 5% 步进）。

| Type | UI 条件 | 代码逻辑 | 典型技能 |
|---|---|---|---|
| **0** | （不可进 VIP 列表） | 生活/被动等，`AutoSkillType==0` 不会出现在设置可选列表 | 抗性、生产、钓鱼… |
| **1** | 无条件 | **default 分支**：立刻出手。73→普攻；其它→按技 Target `AutoSelect` | 连击、乾坤、单体属性魔、攻击… |
| **2** | 敌人数量 ≥ N | 敌侧按 `placeToIndex` 数活人，累计到 ≥`UseCondition1` 就放（**不指定目标**，Send 时再 AutoSelect） | 气功弹、强力/超强属性魔、乱射、追月 |
| **3** | 敌人数量 ≥ N | 敌人数 ≥N 后，按 `placeToIndex` 找第一个：**非异常** + 可选中（近战还要过前排挡） | 异常魔法/异常攻击（毒睡石醉混忘） |
| **4** | 敌人剩余魔力 ≥ P% | 敌侧 `placeToIndex`：MP%≥P 且绝对 MP 最高者 | **战栗袭心(6)** |
| **5** | 自身血量 ≤ P% | 自己 HP% ≤P：74→`G`；其它对自己放 | 圣盾、明镜、吸血魔法、防御 |
| **6** | 队友平均血量 ≤ P% | 我方 `placeToIndex` 均血 ≤P → 放，目标 `LowHP` | **超强补血(63)** |
| **7** | 队友平均血量 ≤ P% | 均血 ≤P → 我方按当前 HP 升序，找无 `RCV_UP`、本回合未 Recover 标记者 | **超强恢复(66)** |
| **8** | 友方血量 ≤ P% | 我方 **Index 顺序** 0–9/10–19（非 placeToIndex），找 HP%≤P 且最低、未 AddHp 标记 | 补血、强力补血 |
| **9** | 友方血量 ≤ P% | 同上顺序，无 RCV_UP、未 Recover、HP%≤P | 恢复、强力恢复 |
| **10** | 友方血量 ≤ P% | 同上，未 Guard 标记、HP%≤P | **护卫(7)** |
| **11** | 不存在当前技能效果 | 场上无地水火风祈祷旗 → 放 | 大地/海洋/火焰/云群的祈祷 |
| **12** | 友方存在异常 | 我方有 `IsAbnormal`（毒睡石醉混忘）且未 Clean 标记 | **洁净(67)** |
| **13** | 友方有倒地 | 我方 `IsDead` 且未 Relive 标记 | **气绝回复(68)** |
| **14** | 友方血量 ≤ P% | **先** `HP%≤P`，**再**查对应 buff 未挂：55 攻反弹 /56 魔反弹 /57 攻吸收 /58 魔吸收 /59 攻无效(`ATTACK_INVALID`) /60 魔无效；我方 Index 升序第一个命中就套。**会判身上有没有无效类 buff，但仍绑血量门槛**；无优先人/宠。以后若要「不看血量、优先人/宠、全员已有则不套」→ 见 `tools/VIP攻击无效接管计划.md`（未做） | 反弹/吸收/无效六件套 |


异常判定 `BattleRoleData.IsAbnormal`：毒 / 睡 / 石 / 醉 / 混 / 忘（`BC_FLAG`）。

本回合去重标记 `beUsedSkill`：`Recover` / `AddHp` / `Guard` / `Clean` / `Relive`（同回合不重复点同一人）。

目标选不到（`AutoSelect==-1`）时人物技能会 **fallback 普攻**。

---

## 5. 合击 / 随机如何嵌进 VIP

`SendPlayerVipAutoSkill` / `SendPetVipAutoCommand` 在 **未指定 targetRole** 时调 `AutoSelect`：

- 默认 mode=`Default` → 被战斗设置覆盖成 **合击** 或 **随机**（`PROTO_CHAR_FS_BATTLERANDOM`）
- type6/7 显式传 `LowHP`（不受合击/随机覆盖）
- 近战 `CHECK_WEAPON`：同列前排活人挡后排（见站位文档）

集火若真生效，打的是标记 Index，**不走**合击表。

序章补丁 `ai-target-autoselect-patch`：在 `AutoSelect` 合击/随机入口优先 **AI 独占** `SeqChapterAiBattleTarget.Index`（单位须已 `CanSelect`；LowHP/Death 不动；**不读**官方 `focusFireIndex`）。气功弹/乱射等 Type2 未带 `targetRole`、最终走 AutoSelect 的技能，群攻也以该 Index 为锚点。

---

## 6. 完整技能↔Type 表

见 `tools/vip_autoskill_type_map.txt`（169 条 SkillConfig）。移植时可：

```csharp
int type = ConfigManager.GetTbSkillConfig().Get(skillId).AutoSkillType;
int single = ... .SingleAttack; // 1=集火路径可用的单体
```

---

## 7. 战场可读性确认（移植前提）

### 7.1 人还是宠？

| 来源 | 字段 | 结论 |
|---|---|---|
| 战斗角色 | `RoleData.IsPlayer` ← `Bcflag` 含 `BC_FLAG.PLAYER`（现码 `(bc & 4)!=0`） | ✅ 已用 |
| UI | `BattleRole.IsPlayer` / `IsPlayerPet` | ✅ 有 |

人 → 发包 `S|…` / `H|` / `G` / `N`；宠 → `W|slot|target` / `W|FF|FF`。

### 7.2 人的技能 / 等级 / 蓝耗？

对本客户端账号（`GetAllPlayers()` 里的 uid，含多开同端）：

```
PlayerDataHolder.GetMagicDatasFromUid(uid)
  → MagicData: skillId, name, useFlag, forgetInBatlle, isCD, index
  → magic.techs[i]: Level, Name, Fp(蓝耗), Use, Flg, Index, Target, SkillId, Memo
```

当前蓝：战场 `Char.Mp` / `MaxMp`，或面板 `PlayerMp`。  
VIP 用等级：`UseLevel` → `techs[UseLevel-1]`。

**敌方人物**：一般没有 `GetMagicDatasFromUid`（不在本端账号表）→ ❌ 读不到完整技能栏。

**队友（同端多开）**：✅ 与自己一样可读。

现有采集：`AppendSuperAiSkills` 已 dump 可用技 + 每级 Fp/Level。

### 7.3 宠的技能？

```
Player.battlePetID → GetPetDatasFromUid(uid)[id].data.PetSkills
  → Proto_TechData: SkillId, TechId, Level, Fp, Use, Name, Index, Memo
```

现有：`AppendSuperAiPetSkills`。敌方宠同样 ❌ 无完整技栏。

### 7.4 人的职业？

```
PlayerData.Job          // 两位数，个位=进阶；系 = Job - Job%10
PlayerData.JobName
PlayerData.JobAncestry / JobAncestryName
```

战斗单位 → `AccountIndexDic` / `FindUidByBattleIndex` → `GetPlayerFromUid`。  
敌方人物通常 ❌ 无 Job；己方/同端 ✅。

### 7.5 汇总

| 数据 | 己方人 | 己方宠 | 同端队友 | 敌方 |
|---|---|---|---|---|
| 人/宠 | ✅ | ✅ | ✅ | ✅（Bcflag） |
| 技能列表 | ✅ Magic | ✅ PetSkills | ✅ | ❌ |
| 技能等级 | ✅ techs.Level | ✅ Level | ✅ | ❌ |
| 当前蓝耗/蓝 | ✅ Fp + Char.Mp | ✅ | ✅ | 仅可见 Mp（敌方 Char） |
| 职业 Job | ✅ | — | ✅ | ❌ |

---

## 8. 移植到虚拟 AI 的建议

1. **不接管**：不要自己发 `H|x`；保持 `IsAutoBattle`，让官方 `DoAutoFight` 跑（VIP 开则 VIP，否则普通）。  
2. **接管**：只对本端 CurrentAccount 的人+宠写可发包指令；决策可复用上表 type 分支。  
3. **目标**：要指定目标必须自己选 Index（合击表 / 列阻挡 / LowHP）；不要指望集火字段。  
4. **配置**：可直接读 `GetSortedSkillDataByUid` + `GetAutoSkillSwitch`，与玩家 VIP 面板一致。  
5. **职业**：用 `Job` 两位数做分支（传教/剑士等），不要只靠中文名。

参考实现入口：`SeqChapterTestUi` 的 `AppendSuperAiSkills` / `ReadSkillAutoType` / `FillSuperAiSendableCommands`。
