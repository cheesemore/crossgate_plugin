# 神奇九动 — 双路径说明

## IL 原版（`battle-nine-action-patch`）

- 整法扩写 `OnCommandPlayerCallback` + Magics 原地
- 需要 `.text` VA 间隙足够（当前约需 **581B**，间隙约 **438B** → 打不进）
- 客户端余量增大后可继续用
- GUI：「神奇九动·IL原版」

## 外挂 / DLL 版（`battle-nine-external-patch`）

- Magics 原地 + `SeqChapterNineAction.dll.bytes`
- Pause 加载器只做 Bootstrap（**无 Timer**）
- **`OnCommandPlayerCallback` 每个 `ret` 前同步**反射调用 `ExpandAccountList`
- 挂载状态：`%USERPROFILE%\.seqchapter_helper\nine_action.status` → `mounted=sync_hook_ok`
- GUI：「外挂九动」

## 互斥

`IL九动` ⊥ `外挂九动` ⊥ `助手桥接`

## 探针

```bat
python tools\hotfix_patcher\_probe_nine_external.py
```

期望：`LAUNCH [ok]` 且 `STATUS` 含 `mounted=sync_hook_ok`
