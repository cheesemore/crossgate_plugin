# 序章 Agent 入口（少思考）

## 目录铁律

| 角色 | 路径（本机示例） | 含义 |
|---|---|---|
| **干净版** | `E:\crosscopy\魔力宝贝：序章` | 官方更新后的纯净客户端；**只读、永不打补丁、永不写入** |
| **打补丁工作区** | `E:\cross\魔力宝贝：序章` | **本 git 根**：日常游戏、`tools`、补丁 GUI、DLL/hotfix 部署 |

命令一律在 **cross** 仓库根执行。更新流程：crosscopy 手动更新 → `workflow.py` 只读探测 → 底稿同步到 cross → 在 cross 上 `repatch`。

**更新 / 打补丁 / 发傻瓜包完整规范：** [`游戏更新与补丁规范.md`](游戏更新与补丁规范.md)  
**改什么找哪：** [`tools/模块地图.md`](tools/模块地图.md)

## 一句话命令

| 场景 | 命令 |
|---|---|
| 看状态 | `python tools/workflow.py status` |
| crosscopy 已更新 → 一条龙 | `python tools/workflow.py update`（先可 `--dry-run`；反外挂误报加 `--confirm-anticheat`） |
| 只重打 cross 默认补丁 | `python tools/workflow.py repatch`（需关游戏） |
| 打傻瓜补丁融合版 | `python tools/workflow.py publish-foolproof` |
| 按配置默认发布 | `python tools/workflow.py publish-all` |
| 只重编助手面板 DLL | `HotfixPatcher wiki-test-ui-patch --hotfix <cross>/cg37_Data/assets/hotfixdata/hotfix.dll.bytes --dll-only` |

## 开发入口

| 做什么 | 去哪 |
|---|---|
| 助手面板 / 护航 / 半山 / AI 战斗 | `tools/seqchapter_test_ui/` → dll-only 部署到 **cross** |
| 补丁引擎 / 组合默认 | `tools/hotfix_patcher/`、`魔力宝贝序章补丁/scripts/` |
| 协议复用 | `tools/常用反射方法速查.md` |
| 废弃勿开 | `tools/DEPRECATED.md` |
| 过时备忘（勿当真） | `当前目录备忘录以及准备做的事情.MD`（已废弃） |

Rules（清缓存后仍生效）：`.cursor/rules/`，尤其 work-dir、do-not-pollute-crosscopy、ai-battle、banshan-prep、escort-defeat-guard。

## 铁律（详见规范文档与 `.cursor/rules/`）

1. **不写游戏客户端文件**（`hotfix.dll.bytes` 等）除非用户明确要求代打；默认 `repatch`/`auto-update` 是用户允许的固化流程。产物只落 **cross**，禁止指向 crosscopy。
2. **永不污染 crosscopy**。
3. **不杀 cg37** 除非用户明确同意。
4. 默认组合：拦截倍速上报、日常、客服→autoskill、精简桥接、**地图 Sprint 8 速**；战斗倍速 / 技能特效仍默认关；九动封存不提。龙族/七夕护航循环已卸；**中元循环已永久删除**；战斗页「抓野生宠」独立保留。傻瓜包仍默认不打加速。
5. 新功能先查 `tools/常用反射方法速查.md`，复用已有协议片段。
6. 废弃模块见 `tools/DEPRECATED.md`，默认不打开。

## 傻瓜补丁

`publish_foolproof.py` **只产融合版**（至游戏目录上一级 `发布plugin/`，相对 `../发布plugin`）：

- `傻瓜补丁_融合版_*.zip`

包内含多开器、窗口监视。说明文件**不提**龙族/中元循环。

## 协议复用

权威速查：`tools/常用反射方法速查.md`  
反编译只读：`tools/hotfix_ilspy/`（大文件，按需 Grep，勿整文件灌进上下文）  
卡图 / 传送落地后官方往回城走：彻底清路径 → 等 2 秒 → `AutoWarpIndex=0` + `RunTask`（见 `.cursor/rules/seqchapter-official-nav-reset.mdc`）。

## 清缓存后自检

1. 能说出：**crosscopy=干净版，cross=打补丁工作区**
2. 在 cross 根执行：`python tools/workflow.py status`
3. 不问历史对话也能答：半山重置 13689 / 护航 1368；攻击无效重写独立于 AI；声望集火序；护航战败暂停
