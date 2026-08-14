# 神奇九动 — **永久封存**

> 日常开发请忽略本目录。见 `tools/DEPRECATED.md` / `AGENTS.md`。
> 傻瓜补丁与默认组合均不启用九动；对外说明不要写「无九动」。

以下为历史说明（仅对照用）。

## IL 原版（`battle-nine-action-patch`）

- 整法扩写 `OnCommandPlayerCallback` + Magics 原地
- 需要 `.text` VA 间隙足够
- GUI：「神奇九动·IL原版」（已默认关）

## 外挂 / DLL 版（`battle-nine-external-patch`）

- Magics 原地 + `SeqChapterNineAction.dll.bytes`
- 与助手桥接互斥

## 互斥（历史）

`IL九动` ⊥ `外挂九动` ⊥ `助手桥接`
