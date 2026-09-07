# 护航循环备份（已卸下 / 永久删除）

| 功能 | 状态 | 说明 |
|------|------|------|
| **龙族循环 A**（110–113） | 卸下面板 | `dragon_loop_ui=False`；实现仍在 `SeqChapterTestUi.cs` |
| **七夕阿凯/哥拉尔**（#119） | 卸下面板 | `TempMidAutumnEscort119=false`；实现仍在主文件 |
| **中元循环**（仓检→抓三种→兑换） | **永久删除**（2026-09-04） | 主文件已删；快照见 `ZhongyuanLoop.extracted.cs`。战斗页「抓野生宠」、脚本「兑换野生宠」独立保留 |

| 文件 | 说明 |
|------|------|
| `SeqChapterTestUi.pre-uninstall.cs` | 卸龙族/七夕按钮前的整份助手面板源码 |
| `ZhongyuanLoop.extracted.cs` | 永久删除前抽出的中元循环实现 |

日常不要改本目录。
