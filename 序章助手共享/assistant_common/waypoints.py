#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""保存/读取导航坐标点。"""
from __future__ import annotations

import json
import uuid
from pathlib import Path
from typing import Any

from .config import DATA_DIR

WAYPOINTS_PATH = DATA_DIR / "waypoints.json"


def _load_all() -> dict[str, Any]:
    if not WAYPOINTS_PATH.is_file():
        return {"items": []}
    try:
        data = json.loads(WAYPOINTS_PATH.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return {"items": []}
    if not isinstance(data, dict):
        return {"items": []}
    items = data.get("items")
    if not isinstance(items, list):
        data["items"] = []
    return data


def _save_all(data: dict[str, Any]) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    WAYPOINTS_PATH.write_text(
        json.dumps(data, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def list_waypoints() -> list[dict[str, Any]]:
    return list(_load_all().get("items") or [])


def add_waypoint(
    name: str,
    *,
    floor: int,
    x: int,
    y: int,
    map_id: int = 0,
) -> dict[str, Any]:
    data = _load_all()
    items: list[dict[str, Any]] = list(data.get("items") or [])
    item = {
        "id": uuid.uuid4().hex[:10],
        "name": name.strip() or f"点({floor},{x},{y})",
        "floor": int(floor),
        "map_id": int(map_id),
        "x": int(x),
        "y": int(y),
    }
    items.append(item)
    data["items"] = items
    _save_all(data)
    return item


def delete_waypoint(item_id: str) -> bool:
    data = _load_all()
    items: list[dict[str, Any]] = list(data.get("items") or [])
    new_items = [it for it in items if str(it.get("id")) != item_id]
    if len(new_items) == len(items):
        return False
    data["items"] = new_items
    _save_all(data)
    return True
