#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Windows 下隐藏 subprocess 弹出的 cmd 窗口。"""
from __future__ import annotations

import subprocess
import sys


def no_window_flags() -> int:
    if sys.platform == "win32" and hasattr(subprocess, "CREATE_NO_WINDOW"):
        return subprocess.CREATE_NO_WINDOW
    return 0
