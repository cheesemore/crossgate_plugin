#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""简单补丁：无 GUI，一键完成 初始化 → 还原 → 默认组合打补丁。"""
from __future__ import annotations

import sys
import traceback

from apply_combo_patch import apply_combo, restore_hotfix
from patch_common import (
    DATA_DIR,
    detect_game_root_from_launcher,
    initialize_hotfix_workspace,
    set_game_root,
)
from patch_defaults import DEFAULT_COMBO_KWARGS


def run_one_click(game_root=None) -> list[str]:
    root = game_root or detect_game_root_from_launcher()
    if root is None:
        raise FileNotFoundError(
            f"未找到游戏目录。请将「简单补丁.exe」放在含 {DATA_DIR} 的游戏根目录，"
            f"或在该目录下运行。"
        )
    set_game_root(root)
    messages: list[str] = [f"游戏目录: {root}"]

    _, _, init_msgs = initialize_hotfix_workspace(root)
    messages.extend(init_msgs)

    restore_hotfix(root)
    messages.append("已从 .orig 还原 hotfix（等同一键还原）")

    messages.extend(apply_combo(**DEFAULT_COMBO_KWARGS, game_root=root))
    messages.append("默认组合补丁已全部应用")
    return messages


def _pause_on_error() -> None:
    try:
        input("\n按 Enter 退出…")
    except EOFError:
        pass


def main() -> int:
    print("魔力宝贝：序章 — 简单补丁")
    print("流程：初始化 → 一键还原 → 应用默认补丁\n")
    try:
        for line in run_one_click():
            print(f"[OK] {line}")
        print("\n完成。请启动游戏验证。")
        try:
            input("\n按 Enter 退出…")
        except EOFError:
            pass
        return 0
    except Exception as exc:
        print(f"\n[FAIL] {exc}", file=sys.stderr)
        traceback.print_exc()
        _pause_on_error()
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
