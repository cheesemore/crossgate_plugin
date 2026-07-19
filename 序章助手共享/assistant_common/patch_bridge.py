#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章助手桥接（兼容层）— 注入实现已迁至「魔力宝贝序章补丁/scripts/bridge_inject.py」。"""
from __future__ import annotations

from pathlib import Path

from .patch_kit import import_patch_module

HOTFIX_REL = Path("cg37_Data") / "assets" / "hotfixdata" / "hotfix.dll.bytes"
BRIDGE_DLL_REL = Path("cg37_Data") / "assets" / "hotfixdata" / "SeqChapterHelperBridge.dll.bytes"


def _bridge():
    return import_patch_module("bridge_inject")


def _common():
    return import_patch_module("patch_common")


def hotfix_path(game_root: Path) -> Path:
    return game_root / HOTFIX_REL


def bridge_dll_path(game_root: Path) -> Path:
    return game_root / BRIDGE_DLL_REL


def hotfix_orig_path(game_root: Path) -> Path:
    hotfix = hotfix_path(game_root)
    return hotfix.with_name(hotfix.name + ".orig")


def bridge_inject_log_path() -> Path:
    return _bridge().bridge_inject_log_path()


def report_bridge_inject_failure(game_root: Path, stage: str, detail: str) -> str:
    return _bridge().report_bridge_inject_failure(game_root, stage, detail)


def is_bridge_patched(game_root: Path) -> bool:
    return _common().is_bridge_patched(game_root)


def detect_patch_variant(game_root: Path) -> str:
    return _common().detect_bridge_variant(game_root)


def patch_variant_label(variant: str) -> str:
    return _common().bridge_variant_label(variant)


def detect_bootstrap_site(game_root: Path) -> str:
    return _bridge().detect_bootstrap_site(game_root)


def ensure_orig_backup(game_root: Path) -> Path:
    return _bridge().ensure_orig_backup(game_root)


def remove_bridge_patch(game_root: Path) -> tuple[bool, str]:
    return _bridge().remove_bridge_patch(game_root)


def apply_bridge_patch(game_root: Path, *, force_from_orig: bool = False) -> tuple[bool, str]:
    return _bridge().apply_bridge_patch(game_root, force_from_orig=force_from_orig)


BRIDGE_INJECT_DISABLED_MSG = (
    "助手桥接注入已暂停。\n"
    "请用序章多开器「恢复原版 hotfix」或序章补丁 GUI 还原后再启动游戏。"
)
