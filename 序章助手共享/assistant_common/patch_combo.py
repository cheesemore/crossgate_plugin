#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""热补丁组合（兼容层）— 实现已迁至「魔力宝贝序章补丁/scripts/launch_inject.py」。"""
from __future__ import annotations

from pathlib import Path

from .patch_kit import import_patch_module


def apply_launch_hotfix_patches(
    game_root: Path,
    *,
    vip: bool | None = None,
    vip_scale: int | None = None,
    vip_non_vip: bool | None = None,
    map_sprint: bool | None = None,
    map_sprint_scale: int | None = None,
) -> tuple[bool, str]:
    """还原 → 启动用补丁组合 → 桥接。选项默认见 patch_defaults.LAUNCH_INJECT_PRESET。"""
    launch = import_patch_module("launch_inject")
    overrides: dict = {}
    if vip is not None:
        overrides["vip"] = vip
    if vip_scale is not None:
        overrides["vip_scale"] = vip_scale
    if vip_non_vip is not None:
        overrides["vip_non_vip"] = vip_non_vip
    if map_sprint is not None:
        overrides["map_sprint"] = map_sprint
    if map_sprint_scale is not None:
        overrides["map_sprint_scale"] = map_sprint_scale
    return launch.apply_launch_inject(game_root, **overrides)
