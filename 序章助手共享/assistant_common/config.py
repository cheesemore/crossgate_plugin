#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import json
from pathlib import Path

APP_NAME = "SeqChapterHelper"
DATA_DIR = Path.home() / ".seqchapter_helper"
ACCOUNTS_PATH = DATA_DIR / "accounts.json"
SETTINGS_PATH = DATA_DIR / "settings.json"
DEFAULT_GAME_ROOT = Path(__file__).resolve().parents[2]  # 魔力宝贝：序章


def load_settings() -> dict:
    if SETTINGS_PATH.is_file():
        return json.loads(SETTINGS_PATH.read_text(encoding="utf-8-sig"))
    return {}


def save_settings(data: dict) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    SETTINGS_PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def get_game_root() -> Path:
    raw = load_settings().get("game_root", "").strip()
    if raw:
        path = Path(raw)
        if (path / "cg37_Data").is_dir():
            return path
    if (DEFAULT_GAME_ROOT / "cg37_Data").is_dir():
        return DEFAULT_GAME_ROOT
    return DEFAULT_GAME_ROOT


def set_game_root(path: Path) -> None:
    cfg = load_settings()
    cfg["game_root"] = str(path.resolve())
    save_settings(cfg)


def game_exe(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    return root / "cg37.exe"
