#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""加载「魔力宝贝序章补丁/scripts」模块 — 助手/多开器只读此入口，勿重复实现注入。"""
from __future__ import annotations

import importlib
import sys
from functools import lru_cache
from pathlib import Path


@lru_cache(maxsize=1)
def patch_scripts_dir() -> Path:
    shared = Path(__file__).resolve().parent.parent
    repo = shared.parent
    scripts = repo / "魔力宝贝序章补丁" / "scripts"
    if not scripts.is_dir():
        raise FileNotFoundError(f"找不到序章补丁脚本目录: {scripts}")
    return scripts


def _ensure_scripts_on_path() -> None:
    scripts = str(patch_scripts_dir())
    if scripts not in sys.path:
        sys.path.insert(0, scripts)


def import_patch_module(name: str):
    _ensure_scripts_on_path()
    return importlib.import_module(name)
