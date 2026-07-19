# 魔力宝贝序章助手 · 序章多开器

两套 Python GUI，通过**文件 IPC** 驱动游戏内 `SeqChapterHelperBridge`，直接调用 `LoginGetToken`、`SendMulti("一键召唤")` 等方法，不做 OCR/模拟点击。

```
魔力宝贝：序章/
├── 魔力宝贝序章助手/              # 单实例「助手」GUI
├── 序章多开器/                    # 多账号库 + 多开 + 打开助手
├── 序章助手共享/assistant_common/
└── tools/seqchapter_helper_bridge/
    └── SeqChapterHelperBridge.cs    # 注入 hotfix 的桥接源码
```

## 启动

| 程序 | 入口 |
|------|------|
| 多开器 | `序章多开器\启动多开器.bat` |
| 助手 | `魔力宝贝序章助手\启动序章助手.bat` |

## 典型流程

1. **序章多开器**：添加账号 →「启动并绑定助手」
2. 助手窗口确认 **桥接：已连接**
3. 执行「一键完成第一步」或分步操作

## IPC

- 目录：`%USERPROFILE%\.seqchapter_helper\instances\inst_{进程PID}\`
- GUI 写 `cmd.json`，游戏 Bridge 写 `state.json` / `ack.json`
- 实例 ID 默认 `inst_{cg37.exe 的 PID}`，与多开器启动时一致

## 游戏内桥接（必做一步）

GUI 只发命令；**必须**注入桥接补丁：

- `Bootstrap()`：在 `HotfixEntry.Start` 末尾加载 `SeqChapterHelperBridge.dll.bytes` 并初始化
- `Tick()`：由 `Timer.Create` 每 0.25 秒轮询 IPC

合入方式（任选）：

1. **序章多开器**（推荐）：勾选「启动游戏前自动注入助手桥接」，或点「立即注入桥接」
2. 命令行：`HotfixPatcher helper-bridge-patch --hotfix cg37_Data/assets/hotfixdata/hotfix.dll.bytes`
3. **序章补丁 GUI「一键还原」** / 多开器 **「取消桥接」**：从 `hotfix.dll.bytes.orig` 恢复 hotfix，并删除 `SeqChapterHelperBridge.dll.bytes`

注入方式：**不修改 hotfix 体积**。桥接逻辑编译为 `cg37_Data/assets/hotfixdata/SeqChapterHelperBridge.dll.bytes`，hotfix 仅二进制 hook `HotfixEntry.Start` 加载并调用 `Bootstrap()`。

编译 HotfixPatcher：`dotnet build -c Release`（`tools/hotfix_patcher`）。首次注入会备份 `hotfix.dll.bytes.orig`。

未注入 Bridge 时，GUI 仍可发命令，但游戏不会响应。

## 与序章补丁的关系

- **序章补丁**：改 hotfix 功能（倍速、客服等）
- **序章助手**：运行时自动化（登录、五控）
- 建议：先打补丁（含 helper-bridge），再用助手

## 多开说明

- 每个 `cg37.exe` 独立 PID → 独立 `inst_{pid}` IPC 目录
- 游戏自带 `MultiClientManager` 可能限制多开；若启动失败需用官方多开通道或已有「序章多开」环境
- 账号密码保存在 `%USERPROFILE%\.seqchapter_helper\accounts.json`（明文，仅本地）
