#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""桌面工具单实例锁（Windows 命名 Mutex + 锁文件兜底）。"""
from __future__ import annotations

import atexit
import os
import sys
from pathlib import Path

from .config import DATA_DIR

_mutex_handle: int | None = None
_lock_path: Path | None = None

MULTI_LAUNCHER_LOCK_KEY = "seqchapter_multi_launcher"


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        exit_code = ctypes.c_ulong()
        ok = kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code))
        kernel32.CloseHandle(handle)
        return bool(ok) and exit_code.value == 259
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def _release_windows_mutex() -> None:
    global _mutex_handle
    if _mutex_handle and sys.platform == "win32":
        import ctypes

        ctypes.windll.kernel32.CloseHandle(_mutex_handle)
        _mutex_handle = None


def _release_lock_file() -> None:
    global _lock_path
    if _lock_path and _lock_path.is_file():
        try:
            _lock_path.unlink()
        except OSError:
            pass
    _lock_path = None


def _try_windows_mutex(key: str) -> bool:
    global _mutex_handle
    import ctypes

    kernel32 = ctypes.windll.kernel32
    name = f"Local\\{key}"
    handle = kernel32.CreateMutexW(None, False, name)
    if not handle:
        return False
    if kernel32.GetLastError() == 183:
        kernel32.CloseHandle(handle)
        return False
    _mutex_handle = handle
    atexit.register(_release_windows_mutex)
    return True


def _try_lock_file(key: str) -> bool:
    global _lock_path
    path = DATA_DIR / "locks" / f"{key}.lock"
    path.parent.mkdir(parents=True, exist_ok=True)

    if path.is_file():
        try:
            old_pid = int(path.read_text(encoding="utf-8").strip())
        except (ValueError, OSError):
            old_pid = 0
        if old_pid and _pid_alive(old_pid):
            return False
        try:
            path.unlink()
        except OSError:
            return False

    try:
        path.write_text(str(os.getpid()), encoding="utf-8")
    except OSError:
        return False

    _lock_path = path
    atexit.register(_release_lock_file)
    return True


def try_acquire_single_instance(key: str) -> bool:
    """获取单实例锁；成功返回 True，已有实例在运行返回 False。"""
    if sys.platform == "win32":
        return _try_windows_mutex(key)
    return _try_lock_file(key)


def show_already_running_message(app_title: str, message: str | None = None) -> None:
    text = message or (
        f"{app_title} 已在运行，不能重复打开。\n\n"
        "请在已打开的窗口中管理多开与账号；如需重启请先关闭当前窗口。"
    )
    if sys.platform == "win32":
        import ctypes

        ctypes.windll.user32.MessageBoxW(0, text, app_title, 0x30)
        return
    print(text, file=sys.stderr)


def ensure_single_instance(
    app_title: str,
    *,
    lock_key: str = MULTI_LAUNCHER_LOCK_KEY,
    message: str | None = None,
) -> bool:
    if try_acquire_single_instance(lock_key):
        return True
    show_already_running_message(app_title, message)
    return False
