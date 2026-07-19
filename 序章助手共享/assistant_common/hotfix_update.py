#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""客户端 hotfix 更新检测（多开器 / 助手共用，实现位于序章补丁 patch_common）。"""
from __future__ import annotations

import sys
from pathlib import Path


def _patch_scripts_dir() -> Path:
    return Path(__file__).resolve().parents[2] / "魔力宝贝序章补丁" / "scripts"


def _ensure_patch_scripts() -> None:
    path = str(_patch_scripts_dir())
    if path not in sys.path:
        sys.path.insert(0, path)


def get_update_status(game_root: Path) -> dict:
    _ensure_patch_scripts()
    from patch_common import get_update_status as _fn

    return _fn(game_root)


def format_client_update_hint(status: dict) -> str:
    _ensure_patch_scripts()
    from patch_common import format_client_update_hint as _fn

    return _fn(status)


def sync_client_baseline(game_root: Path, *, force: bool = False) -> list[str]:
    _ensure_patch_scripts()
    from patch_common import sync_client_baseline as _fn

    return _fn(game_root, force=force)


def effective_expected_size(game_root: Path) -> int:
    _ensure_patch_scripts()
    from patch_common import effective_expected_size as _fn

    return _fn(game_root)
