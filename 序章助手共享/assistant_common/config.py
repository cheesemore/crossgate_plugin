#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import sys
from pathlib import Path

APP_NAME = "SeqChapterHelper"
DATA_DIR = Path.home() / ".seqchapter_helper"
ACCOUNTS_PATH = DATA_DIR / "accounts.json"
SETTINGS_PATH = DATA_DIR / "settings.json"
GAME_DATA_DIR = "cg37_Data"
DEFAULT_GAME_ROOT = Path(__file__).resolve().parents[2]  # 源码/开发机：魔力宝贝：序章


def load_settings() -> dict:
    if SETTINGS_PATH.is_file():
        return json.loads(SETTINGS_PATH.read_text(encoding="utf-8-sig"))
    return {}


def save_settings(data: dict) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    SETTINGS_PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def _walk_up(start: Path):
    cur = start.resolve()
    if cur.is_file():
        cur = cur.parent
    while True:
        yield cur
        parent = cur.parent
        if parent == cur:
            return
        cur = parent


def _frozen_exe_dir() -> Path | None:
    if getattr(sys, "frozen", False):
        try:
            return Path(sys.executable).resolve().parent
        except OSError:
            return None
    return None


def find_game_root_walking_up(start: Path | None = None) -> Path | None:
    """从 start（默认 exe/脚本目录）向上找含 cg37.exe + cg37_Data 的游戏根目录。"""
    if start is None:
        start = _frozen_exe_dir() or Path(__file__).resolve().parent
    for cur in _walk_up(start):
        if (cur / "cg37.exe").is_file() and (cur / GAME_DATA_DIR).is_dir():
            return cur
    return None


def find_patcher_config_game_root(start: Path | None = None) -> Path | None:
    """从 exe 目录向上找傻瓜补丁包的 patch_config.json，读取其 game_root。

    多开器常与傻瓜补丁解压在同一包内（多开器/ 子目录），补丁 GUI 把
    游戏目录写在包根 patch_config.json。共享该值即可让两工具默认目录一致。
    """
    if start is None:
        start = _frozen_exe_dir()
        if start is None:
            return None
    for cur in _walk_up(start):
        cfg = cur / "patch_config.json"
        if not cfg.is_file():
            continue
        try:
            raw = json.loads(cfg.read_text(encoding="utf-8")).get("game_root", "").strip()
        except (OSError, json.JSONDecodeError):
            continue
        if raw:
            path = Path(raw)
            if path.is_dir() and (path / GAME_DATA_DIR).is_dir():
                return path
    return None


def get_game_root() -> Path:
    raw = load_settings().get("game_root", "").strip()
    if raw:
        path = Path(raw)
        if (path / GAME_DATA_DIR).is_dir():
            return path
    for candidate in (
        find_patcher_config_game_root(),
        find_game_root_walking_up(),
    ):
        if candidate is not None:
            return candidate
    if (DEFAULT_GAME_ROOT / GAME_DATA_DIR).is_dir():
        return DEFAULT_GAME_ROOT
    return DEFAULT_GAME_ROOT


def set_game_root(path: Path) -> None:
    cfg = load_settings()
    cfg["game_root"] = str(path.resolve())
    save_settings(cfg)


def game_exe(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    return root / "cg37.exe"
