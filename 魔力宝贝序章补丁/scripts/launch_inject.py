#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""多开器启动前补丁 + 桥接 — 调用序章补丁组合，再注入助手桥接。"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from apply_combo_patch import apply_combo
from bridge_inject import (
    remove_bridge_patch,
    report_bridge_inject_failure,
)
from patch_common import detect_bridge_variant, get_game_root, is_bridge_patched
from patch_defaults import LAUNCH_INJECT_PRESET


def apply_launch_inject(
    game_root: Path | None = None,
    *,
    inject_bridge: bool = True,
    **overrides,
) -> tuple[bool, str]:
    """从 .orig 重打「启动用」补丁组合，并注入桥接。配置见 patch_defaults.LAUNCH_INJECT_PRESET。"""
    root = Path(game_root) if game_root else get_game_root()
    if root is None:
        return False, "未设置游戏目录"

    preset = dict(LAUNCH_INJECT_PRESET)
    preset.update(overrides)
    preset["inject_bridge"] = inject_bridge

    try:
        msgs = apply_combo(game_root=root, **preset)
    except Exception as exc:
        return False, report_bridge_inject_failure(root, "combo", str(exc))

    return True, "\n".join(msgs)


def restore_launch_hotfix(game_root: Path | None = None) -> tuple[bool, str]:
    """恢复 .orig 并删除桥接（等同多开器「恢复原版 / 取消桥接」）。"""
    root = Path(game_root) if game_root else get_game_root()
    if root is None:
        return False, "未设置游戏目录"
    return remove_bridge_patch(root)


def launch_status(game_root: Path | None = None) -> dict:
    root = Path(game_root) if game_root else get_game_root()
    if root is None:
        return {"error": "no game_root"}
    variant = detect_bridge_variant(root)
    return {
        "game_root": str(root),
        "bridge_patched": is_bridge_patched(root),
        "bridge_variant": variant,
        "launch_preset": LAUNCH_INJECT_PRESET,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="序章补丁 — 多开器启动用注入")
    parser.add_argument("--game-root", type=str, default="", help="游戏根目录（含 cg37_Data）")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("apply", help="还原 .orig → 打启动补丁 → 注入桥接")
    sub.add_parser("restore", help="恢复 .orig 并取消桥接")
    sub.add_parser("status", help="桥接 / 预设状态 JSON")

    args = parser.parse_args()
    root = Path(args.game_root).resolve() if args.game_root.strip() else get_game_root()
    if root is None or not (root / "cg37_Data").is_dir():
        print("[FAIL] 无效游戏目录", file=sys.stderr)
        return 1

    try:
        if args.cmd == "apply":
            ok, msg = apply_launch_inject(root)
            print(msg)
            return 0 if ok else 1
        if args.cmd == "restore":
            ok, msg = restore_launch_hotfix(root)
            print(msg)
            return 0 if ok else 1
        if args.cmd == "status":
            print(json.dumps(launch_status(root), ensure_ascii=False, indent=2))
            return 0
    except Exception as exc:
        print(f"[FAIL] {exc}", file=sys.stderr)
        return 1
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
