#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓宠卖银币：掉档阈值 Y 配置（与 DLL 共用路径）。"""
from __future__ import annotations

import json
from pathlib import Path

DEFAULT_RECYCLE_MIN_GRADE = 6
CONFIG_NAME = "catch_sell.json"


def config_path() -> Path:
    return Path.home() / ".seqchapter_helper" / CONFIG_NAME


def load_recycle_min_grade() -> int:
    path = config_path()
    if not path.is_file():
        return DEFAULT_RECYCLE_MIN_GRADE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        y = int(data.get("recycle_min_grade", DEFAULT_RECYCLE_MIN_GRADE))
        return max(0, y)
    except Exception:
        return DEFAULT_RECYCLE_MIN_GRADE


def save_recycle_min_grade(y: int) -> Path:
    y = max(0, int(y))
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps({"recycle_min_grade": y}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return path
