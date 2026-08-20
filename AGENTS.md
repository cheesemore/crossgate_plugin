# 序章 Agent 入口（少思考）

仓库：`E:\cross\魔力宝贝：序章`。干净底稿只读：`E:\crosscopy\魔力宝贝：序章`。

**更新 / 打补丁 / 发傻瓜包完整规范：** [`游戏更新与补丁规范.md`](游戏更新与补丁规范.md)

## 一句话命令

| 场景 | 命令 |
|---|---|
| 看状态 | `python tools/workflow.py status` |
| crosscopy 已更新 → 一条龙 | `python tools/workflow.py update`（先可 `--dry-run`；反外挂误报加 `--confirm-anticheat`） |
| 只重打 cross 默认补丁 | `python tools/workflow.py repatch`（需关游戏） |
| 打傻瓜补丁 | `python tools/workflow.py publish-foolproof` |
| 按配置默认发布 | `python tools/workflow.py publish-all` |

## 铁律（详见规范文档与 `.cursor/rules/`）

1. **不写游戏客户端文件**（`hotfix.dll.bytes` 等）除非用户明确要求代打；默认 `repatch`/`auto-update` 是用户允许的固化流程。
2. **永不污染 crosscopy**。
3. **不杀 cg37** 除非用户明确同意。
4. 默认组合：拦截倍速上报、日常、客服→autoskill、精简桥接；**龙族护航已卸载**；**加速类默认关**（技能特效归属战斗倍速）；九动封存不提。
5. 新功能先查 `tools/常用反射方法速查.md`，复用已有协议片段。
6. 废弃模块见 `tools/DEPRECATED.md`，默认不打开。

## 傻瓜补丁

`publish_foolproof.py` **一次产出**（至游戏目录上一级 `发布plugin/`，相对 `../发布plugin`）：

- `傻瓜补丁_融合版_*.zip`（龙族护航已卸载；打补丁会删 `seqchapter_dragon_loop.flag`）

包内含多开器、窗口监视。说明文件**不提**九动。
七夕 #119 护航循环在助手面板（阿凯版/哥拉尔版；**存兑换券后才计一轮**，标题 `★七夕N轮★`）；临时活动，**等用户下令再永久移除**。

## 协议复用

权威速查：`tools/常用反射方法速查.md`  
反编译只读：`tools/hotfix_ilspy/`（大文件，按需 Grep，勿整文件灌进上下文）  
卡图 / 传送落地后官方往回城走：彻底清路径 → 等 2 秒 → `AutoWarpIndex=0` + `RunTask`（见 `.cursor/rules/seqchapter-official-nav-reset.mdc`）。不要自己发明中间跳。
