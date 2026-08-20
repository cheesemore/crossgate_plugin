# -*- coding: utf-8 -*-
"""序章目录约定（勿与 crossgate_cursor / 青春魔力混淆）。

建议布局（相对盘符根，名称固定；盘符可不同）：

| 角色 | 建议路径 | 说明 |
|---|---|---|
| 干净只读源 | ``<盘符>/crosscopy/魔力宝贝：序章`` | 提取配置/对照，**禁止写入** |
| 工作目录 | 本仓库根（含 ``cg37.exe`` / ``cg37_Data``） | 插件 / hotfix / 工具产物 |
| 抓宠目录 | ``<盘符>/cross抓宠/魔力宝贝：序章`` | 抓宠专用实例（可选） |

也可用环境变量覆盖：``SEQCHAPTER_WORK`` / ``SEQCHAPTER_CLEAN`` / ``SEQCHAPTER_CATCH``。

「启动 cross」= 启动工作目录的 cg37.exe，不是 crossgate_cursor\\cross.exe。
"""
from __future__ import annotations

import os
from pathlib import Path

# 本文件在 tools/ 下 → 仓库/游戏根
_REPO = Path(__file__).resolve().parent.parent
_CROSS_DIR = _REPO.parent
_BASE = _CROSS_DIR.parent


def _find_game_under(parent: Path) -> Path:
    """在 parent 下找含 cg37.exe 或 cg37_Data 的游戏根。"""
    if not parent.is_dir():
        raise FileNotFoundError(f"目录不存在: {parent}")
    if (parent / "cg37.exe").is_file() or (parent / "cg37_Data").is_dir():
        return parent.resolve()
    for child in sorted(parent.iterdir()):
        if not child.is_dir():
            continue
        if (child / "cg37.exe").is_file() or (child / "cg37_Data").is_dir():
            return child.resolve()
    raise FileNotFoundError(f"在 {parent} 下找不到序章游戏目录")


def _try_find_game_under(parent: Path) -> Path | None:
    try:
        return _find_game_under(parent)
    except FileNotFoundError:
        return None


def _resolve_named_root(env_key: str, *candidates: Path) -> Path:
    env = (os.environ.get(env_key) or "").strip()
    if env:
        p = Path(env)
        if (p / "cg37.exe").is_file() or (p / "cg37_Data").is_dir():
            return p.resolve()
        found = _try_find_game_under(p)
        if found is not None:
            return found
        raise FileNotFoundError(f"{env_key}={env} 不是有效序章游戏目录")
    for c in candidates:
        found = _try_find_game_under(c)
        if found is not None:
            return found
    raise FileNotFoundError(
        f"找不到序章目录（{env_key}）。已尝试: "
        + ", ".join(str(c) for c in candidates)
    )


# 工作目录：优先本仓库（相对路径），再尝试同级名「cross」
WORK_ROOT = _resolve_named_root(
    "SEQCHAPTER_WORK",
    _REPO,
    _BASE / "cross",
    Path("E:/cross"),
)

# 干净备份：只读提取源（著名目录名 crosscopy）
CLEAN_ROOT = _resolve_named_root(
    "SEQCHAPTER_CLEAN",
    _BASE / "crosscopy",
    _CROSS_DIR.parent / "crosscopy",
    Path("E:/crosscopy"),
)

# 抓宠实例（可选）
try:
    CATCH_ROOT = _resolve_named_root(
        "SEQCHAPTER_CATCH",
        _BASE / "cross抓宠",
        Path("E:/cross抓宠"),
    )
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
