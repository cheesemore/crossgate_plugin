#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""命令行傻瓜补丁（同 GUI 逻辑）。"""
from __future__ import annotations

import sys
import traceback

from foolproof_apply import FoolproofError, run_foolproof_patch


def main() -> int:
    print("魔力宝贝：序章 — 傻瓜补丁（命令行）\n")
    try:
        for line in run_foolproof_patch():
            print(line)
        try:
            input("\n按 Enter 退出…")
        except EOFError:
            pass
        return 0
    except FoolproofError as exc:
        print(f"\n[无法打补丁]\n{exc}", file=sys.stderr)
        try:
            input("\n按 Enter 退出…")
        except EOFError:
            pass
        return 1
    except Exception as exc:
        print(f"\n[FAIL] {exc}", file=sys.stderr)
        traceback.print_exc()
        try:
            input("\n按 Enter 退出…")
        except EOFError:
            pass
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
