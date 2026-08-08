# -*- coding: utf-8 -*-
"""序章目录约定（勿与 crossgate_cursor / 青春魔力混淆）。

| 角色 | 路径 | 说明 |
|---|---|---|
| 干净只读源 | E:\\crosscopy\\魔力宝贝：序章 | 提取配置/对照用，**禁止写入** |
| 工作目录 | E:\\cross\\魔力宝贝：序章 | 插件 / hotfix / 工具产物 |
| 抓宠目录 | E:\\cross抓宠\\魔力宝贝：序章 | 抓宠专用实例 |

「启动 cross」= 启动工作目录的 cg37.exe，不是 crossgate_cursor\\cross.exe。
"""
from __future__ import annotations

from pathlib import Path

DRIVE = Path("E:/")


def _find_game_under(parent: Path) -> Path:
    """在 parent 下找含 cg37.exe 或 cg37_Data 的游戏根。"""
    if not parent.is_dir():
        raise FileNotFoundError(f"目录不存在: {parent}")
    # 直接就是游戏根
    if (parent / "cg37.exe").is_file() or (parent / "cg37_Data").is_dir():
        return parent.resolve()
    for child in sorted(parent.iterdir()):
        if not child.is_dir():
            continue
        if (child / "cg37.exe").is_file() or (child / "cg37_Data").is_dir():
            return child.resolve()
    raise FileNotFoundError(f"在 {parent} 下找不到序章游戏目录")


# 干净备份：只读提取源
CLEAN_ROOT = _find_game_under(DRIVE / "crosscopy")
# 工作目录：插件与工具写入这里
WORK_ROOT = _find_game_under(DRIVE / "cross")
# 抓宠实例（可选）
try:
    CATCH_ROOT = _find_game_under(DRIVE / "cross抓宠")
except FileNotFoundError:
    CATCH_ROOT = None

CLEAN_ASSETS = CLEAN_ROOT / "cg37_Data" / "assets"
WORK_ASSETS = WORK_ROOT / "cg37_Data" / "assets"
WORK_HOTFIXDATA = WORK_ASSETS / "hotfixdata"
WORK_TOOLS = WORK_ROOT / "tools"
# 从 crosscopy 提取出的配置落盘位置（在工作目录 tools 下，不碰 crosscopy）
CONFIG_EXTRACT = WORK_TOOLS / "_config_extract"
CONFIG_EXCELGENERAL = CONFIG_EXTRACT / "excelgeneral"
CONFIG_EXCELGENERAL_L = CONFIG_EXTRACT / "excelgeneral_l"

EXE_NAME = "cg37.exe"
DATA_DIR_NAME = "cg37_Data"

# 与 Crossgate 同名的 Luban 配置包（序章 cg37 亦使用）
BUNDLE_EXCELGENERAL = "4bd60e623f3f8796cb234b3f01f0c91a"
BUNDLE_EXCELGENERAL_L = "87e1c5e854407d55d8f140115ef9e820"


def assert_not_clean(path: Path) -> None:
    """防止误写 crosscopy。"""
    try:
        resolved = path.resolve()
        clean = CLEAN_ROOT.resolve()
    except OSError:
        return
    if resolved == clean or clean in resolved.parents:
        raise RuntimeError(f"禁止写入干净目录 crosscopy: {path}")
