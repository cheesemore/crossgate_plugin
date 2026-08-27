# 护航循环备份（已卸下面板入口）

2026-08-26 从助手护航页卸下：

- **龙族循环 A**（110–113，flag 控制显示）
- **七夕阿凯版**、**七夕哥拉尔版**（#119 月宫救兔循环）

实现仍留在 `tools/seqchapter_test_ui/SeqChapterTestUi.cs`（`TempMidAutumnEscort119=false`，护航页不再画那三个按钮）。本目录是卸前完整快照，以后要恢复循环时对照这里。

| 文件 | 说明 |
|------|------|
| `SeqChapterTestUi.pre-uninstall.cs` | 卸按钮前的整份助手面板源码 |

## 恢复入口（对照快照）

1. `TempMidAutumnEscort119` 改回 `true`
2. `BuildEscortBody` 里恢复龙族按钮 + 七夕阿凯/哥拉尔两个按钮
3. 默认组合若要再显示龙族按钮：`dragon_loop_ui=True`（写 `seqchapter_dragon_loop.flag`）

日常不要改本目录。
