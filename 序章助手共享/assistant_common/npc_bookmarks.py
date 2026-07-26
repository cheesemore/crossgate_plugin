#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""保存/读取 NPC 书签（含备注）。Objindex 为会话运行时值，切图/重登可能变化。"""
from __future__ import annotations

import json
import uuid
from pathlib import Path
from typing import Any

from .config import DATA_DIR

BOOKMARKS_PATH = DATA_DIR / "npc_bookmarks.json"


def _load_all() -> dict[str, Any]:
    if not BOOKMARKS_PATH.is_file():
        return {"items": []}
    try:
        data = json.loads(BOOKMARKS_PATH.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return {"items": []}
    if not isinstance(data, dict):
        return {"items": []}
    if not isinstance(data.get("items"), list):
        data["items"] = []
    return data


def _save_all(data: dict[str, Any]) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    BOOKMARKS_PATH.write_text(
        json.dumps(data, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def list_bookmarks() -> list[dict[str, Any]]:
    return list(_load_all().get("items") or [])


def add_bookmark(
    *,
    objindex: int,
    name: str,
    x: int = 0,
    y: int = 0,
    floor: int = 0,
    npc_type: str = "",
    note: str = "",
) -> dict[str, Any]:
    data = _load_all()
    items: list[dict[str, Any]] = list(data.get("items") or [])
    note = (note or "").strip()
    name = (name or "").strip() or "-"
    item = {
        "id": uuid.uuid4().hex[:10],
        "objindex": int(objindex),
        "name": name,
        "x": int(x),
        "y": int(y),
        "floor": int(floor),
        "type": (npc_type or "").strip(),
        "note": note or name,
    }
    items.append(item)
    data["items"] = items
    _save_all(data)
    return item


def update_bookmark(item_id: str, **fields: Any) -> dict[str, Any] | None:
    data = _load_all()
    items: list[dict[str, Any]] = list(data.get("items") or [])
    for it in items:
        if str(it.get("id")) != item_id:
            continue
        for key in ("objindex", "name", "x", "y", "floor", "type", "note"):
            if key in fields and fields[key] is not None:
                it[key] = fields[key]
        if "note" in fields and fields["note"] is not None:
            it["note"] = str(fields["note"]).strip()
        data["items"] = items
        _save_all(data)
        return it
    return None


def delete_bookmark(item_id: str) -> bool:
    data = _load_all()
    items: list[dict[str, Any]] = list(data.get("items") or [])
    new_items = [it for it in items if str(it.get("id")) != item_id]
    if len(new_items) == len(items):
        return False
    data["items"] = new_items
    _save_all(data)
    return True


def get_bookmark(item_id: str) -> dict[str, Any] | None:
    for it in list_bookmarks():
        if str(it.get("id")) == item_id:
            return it
    return None


def bookmark_label(item: dict[str, Any]) -> str:
    note = str(item.get("note") or "").strip()
    name = str(item.get("name") or "-").strip()
    oid = item.get("objindex", "")
    if note and note != name:
        return f"{note} · {name} ({oid})"
    return f"{name} ({oid})"
