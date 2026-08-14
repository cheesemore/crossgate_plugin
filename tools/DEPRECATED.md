# 废弃 / 封存模块（日常勿打开、勿扩展）

> Agent / 人工：默认**不要**读这些目录做新功能，除非用户点名要求。
> 代码仍保留在仓库以便历史对照；发布包默认不启用。

| 目录 / 能力 | 状态 | 说明 |
|---|---|---|
| `tools/seqchapter_nine_action/` | **永久封存** | 神奇九动。傻瓜补丁不发、默认组合 `battle_nine_*=False`，对外文案不再提「无九动」。 |
| `tools/hotfix_patcher/*Nine*` / `battle-nine-*` | 封存 | 九动 IL/DLL patcher 子命令仍在引擎里，但默认不跑。 |
| `tools/seqchapter_wiki_chat_test/` | 废弃实验 | 百科聊天测试，勿当正式功能。 |
| `tools/seqchapter_wiki_skin_cycle/` | 旁路 | 仅傻瓜「换装」包使用；日常组合默认不打。 |
| `tools/seqchapter_plugin_host/` | 闲置 | 插件 Host，与桥接/抓宠互斥，默认关。 |
| `tools/seqchapter_lv1_auto/` | 闲置可选 | 遇1级自动；面板模式默认不部署（`lv1_auto_external=False`）。 |
| `tools/seqchapter_auto_sell/` | 闲置 | 盗贼辅助卖魔石，非傻瓜默认。 |
| `tools/hotfix_patcher_skin_cycle/` | 旁路工程 | 换装专用引擎旁路，勿当主引擎。 |
| `tools/_unused_backup/` | 忽略 | 一次性临时备份，gitignore。 |
| `tools/_tmp_*.py` / `tools/_tmp_*/` | 忽略 | 嗅探一次性脚本，gitignore，勿提交、勿当 API。 |
| `魔力宝贝序章补丁/发布傻瓜补丁_九动版.bat` 等旧 bat | 重定向 | 已改为提示改用新入口，勿复活旧打包参数。 |

## 日常默认要思考的模块

- 补丁引擎：`tools/hotfix_patcher/`
- 助手面板：`tools/seqchapter_test_ui/`
- 玩法 DLL：`auto_seal` / `auto_catch*` / `daily_claim` / `count_farm` / `area_extract` / `auto_point` / `auto_stall` / `bear_slayer` / `battle_appear` / `boss_key_fps`
- 桥接：`seqchapter_mini_bridge`（默认） / `seqchapter_helper_bridge`（完整助手，非傻瓜默认）
- 更新：`tools/cross_update.py`、`tools/workflow.py`
- 发布：`魔力宝贝序章补丁/scripts/publish_foolproof.py`
