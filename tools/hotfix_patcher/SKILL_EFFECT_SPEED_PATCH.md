# 战斗技能特效帧动画加速 — 序章版

本版 hotfix 基准：**7,042,560 字节**（2026-07）。实现见 `SkillEffectSpeedIlPatcher.cs`。

## 命令

```bat
HotfixPatcher.exe skill-effect-speed-patch ^
  --hotfix hotfix.dll.bytes.orig ^
  --output hotfix.dll.bytes ^
  --scale 1.5
```

`--scale` 仅允许 **1.5 | 2 | 3**。

## 原理

在 **`EffectEntity.Play()`** 里、**`base.Play()` 之后**、`PlayFirstAnim` 参数加载之前插入：

```csharp
SetSpeed(mAnimatorSys.PlaySpeed * EFFECT_SCALE); // 链式返回 this，IL 需 pop
```

**勿**在 `PlayFirstAnim` 的 `callvirt` 之前注入（会破坏求值栈 → 进战斗 InvalidProgram）。

- 不影响回合读秒（`BattleProcesser` + `Observable.Interval(1.0)`）
- 不影响 VIP `BattleTimeScale` 与 Echo.Speed 心跳上报
- 与 VIP 战斗倍速可叠加

## 旧版错误补丁

若曾在 `PlayFirstAnim` 前注入（无 `pop`），打补丁时会提示：

> 检测到旧版技能特效补丁（IL 栈错误），请勾选「从 .orig 重打」或一键还原后再打

## 验证

1. `HotfixPatcher ildump hotfix.dll.bytes EffectEntity.Play` — `base.Play` 后应有 `ldc.r4` + `mul` + `call SetSpeed` + `pop`
2. 文件体积仍为 7,042,560
3. 进战斗施法，观察特效帧动画变快；回合倒计时仍为 1 秒减 1
