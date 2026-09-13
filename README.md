# 魔力宝贝：序章（工具 + 工作客户端）

本仓库根 = **打补丁工作区**（本机常见 `E:\cross\魔力宝贝：序章`）。  
干净官方对照 = **`E:\crosscopy\魔力宝贝：序章`（只读，永不打补丁）**。

Agent / 新会话入口：**[`AGENTS.md`](AGENTS.md)** · 改什么找哪：**[`tools/模块地图.md`](tools/模块地图.md)** · 更新规范：**[`游戏更新与补丁规范.md`](游戏更新与补丁规范.md)**

## 目录

| 路径 | 说明 |
|------|------|
| `cg37.exe` / `cg37_Data/` | 工作客户端（补丁产物落在此；**不是** crosscopy） |
| `魔力宝贝序章补丁/` | 热补丁 GUI / 组合打补丁 / 傻瓜包 |
| `新序章多开器/` | 多账号启动与批量一键（权威多开器） |
| `序章多开器/` / `序章多开助手/` | 旧同步副本（改后以新序章多开器为准） |
| `魔力宝贝序章助手/` | 单实例助手 GUI |
| `序章助手共享/` | 助手公共逻辑 |
| `tools/workflow.py` | `status` / `update` / `repatch` / `publish-*` 统一入口 |
| `tools/hotfix_patcher/` | C# 补丁引擎源码 |
| `tools/seqchapter_test_ui/` | 百科助手面板 / 护航 / 半山 / AI（dll-only 部署） |
| `tools/seqchapter_*` | 日常、抓宠、烧卡等外挂 DLL |
| `tools/seqchapter_helper_bridge/` | 助手桥接 DLL |
| `tools/DEPRECATED.md` | 废弃模块（九动等封存，默认不打开） |

## 快速开始

1. 确认目录：日常在 **cross**；更新底稿只读对照 **crosscopy**
2. 关闭游戏 `cg37.exe`
3. `魔力宝贝序章补丁/启动补丁GUI.bat` → 初始化 → 勾选 → **应用补丁**  
   或：`python tools/workflow.py repatch`（默认组合）
4. 只改助手面板：`wiki-test-ui-patch --dll-only`（见 TestUi README）
5. 多开：`新序章多开器` 启动脚本

首次或改引擎后：`魔力宝贝序章补丁/重建补丁引擎.bat`（需 .NET SDK）。

维护说明：`魔力宝贝序章补丁/补丁维护.md`。过时备忘 `当前目录备忘录以及准备做的事情.MD` **已废弃**，勿当真。
