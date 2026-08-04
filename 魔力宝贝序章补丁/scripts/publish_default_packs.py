#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按 publish_packs.json 发布默认傻瓜包（当前：融合版 + 换装）。"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
TOOLKIT_ROOT = SCRIPTS_DIR.parent
GAME_ROOT = TOOLKIT_ROOT.parent
CROSS_ROOT = GAME_ROOT.parent
RELEASE_DIR = CROSS_ROOT / "发布plugin"
CONFIG_PATH = TOOLKIT_ROOT / "publish_packs.json"
_STAMP_RE = re.compile(r"^\d{8}_\d{6}$")


def load_config() -> dict:
    if not CONFIG_PATH.is_file():
        raise FileNotFoundError(f"找不到发布配置: {CONFIG_PATH}")
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))


def _is_series_zip(stem: str, prefix: str) -> bool:
    if stem == prefix:
        return True
    head = prefix + "_"
    if not stem.startswith(head):
        return False
    return _STAMP_RE.fullmatch(stem[len(head) :]) is not None


def cleanup_disabled_packs(packs: dict) -> None:
    """删除已禁用包在发布目录留下的旧 zip / dist 目录。"""
    if not RELEASE_DIR.is_dir():
        return
    for key, meta in packs.items():
        if not isinstance(meta, dict) or meta.get("enabled", True):
            continue
        label = str(meta.get("label") or "").strip()
        if not label:
            continue
        for zip_path in list(RELEASE_DIR.glob("*.zip")):
            if _is_series_zip(zip_path.stem, label):
                try:
                    zip_path.unlink()
                    print(f"[CLEAN] 已禁用包，删除 {zip_path.name}")
                except OSError as exc:
                    print(f"[WARN] 无法删除 {zip_path.name}: {exc}")
        for dist_name in ("dist_foolproof", "dist_foolproof_skin", "dist"):
            folder = RELEASE_DIR / dist_name / label
            if folder.is_dir():
                shutil.rmtree(folder, ignore_errors=True)
                print(f"[CLEAN] 已禁用包，删除目录 {folder}")


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    force_all = any(a in ("--all-enabled", "/all") for a in argv)
    only = [a[7:] for a in argv if a.startswith("--only=")]

    cfg = load_config()
    packs = cfg.get("packs") or {}
    order = list(only) if only else list(cfg.get("default_publish") or [])
    if force_all:
        order = [k for k, v in packs.items() if isinstance(v, dict) and v.get("enabled")]

    if not order:
        print("[FAIL] publish_packs.json 未配置 default_publish")
        return 1

    print(f"[CFG] {CONFIG_PATH}")
    print(f"[CFG] 将发布: {', '.join(order)}\n")
    cleanup_disabled_packs(packs)

    for key in order:
        meta = packs.get(key)
        if not isinstance(meta, dict):
            print(f"[FAIL] 未知包键: {key}")
            return 1
        if not meta.get("enabled", True):
            reason = meta.get("disabled_reason") or "已在配置中禁用"
            print(f"[SKIP] {meta.get('label') or key}: {reason}")
            continue

        script = TOOLKIT_ROOT / str(meta["script"])
        args = [sys.executable, str(script), *list(meta.get("args") or [])]
        label = meta.get("label") or key
        print(f"=== 发布 {label} ===")
        print("[CMD]", " ".join(args))
        proc = subprocess.run(args, cwd=str(TOOLKIT_ROOT))
        if proc.returncode != 0:
            print(f"[FAIL] {label} 退出码 {proc.returncode}")
            return proc.returncode
        print(f"[OK] {label}\n")

    print("=== 默认包全部完成 ===")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
