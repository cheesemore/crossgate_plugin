#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

from .config import DATA_DIR, game_exe, get_game_root
from .subprocess_win import no_window_flags


@dataclass
class GameWindow:
    pid: int
    title: str
    instance_id: str


@dataclass
class GameInstance:
    instance_id: str
    pid: int
    label: str = ""


def launch_game(game_root: Path | None = None, instance_id: str | None = None) -> GameInstance:
    root = game_root or get_game_root()
    exe = game_exe(root)
    if not exe.is_file():
        raise FileNotFoundError(f"找不到游戏: {exe}")
    proc = subprocess.Popen(
        [str(exe)],
        cwd=str(root),
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if hasattr(subprocess, "CREATE_NEW_PROCESS_GROUP") else 0,
    )
    iid = instance_id or f"inst_{proc.pid}"
    # 写入实例绑定，供游戏内 Bridge 按 PID 定位 IPC 目录
    bind = DATA_DIR / "instances" / iid / "pid.txt"
    bind.parent.mkdir(parents=True, exist_ok=True)
    bind.write_text(str(proc.pid), encoding="utf-8")
    return GameInstance(instance_id=iid, pid=proc.pid)


def find_game_processes() -> list[tuple[int, str]]:
    """返回 (pid, exe_path) 列表。Windows 下用 tasklist。"""
    try:
        out = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq cg37.exe", "/FO", "CSV", "/NH"],
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=no_window_flags(),
        )
    except (subprocess.CalledProcessError, FileNotFoundError):
        return []
    rows: list[tuple[int, str]] = []
    for line in out.splitlines():
        line = line.strip().strip('"')
        if not line or "cg37.exe" not in line.lower():
            continue
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2:
            try:
                rows.append((int(parts[1]), parts[0]))
            except ValueError:
                pass
    return rows


def wait_process(pid: int, timeout: float = 60.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        procs = {p for p, _ in find_game_processes()}
        if pid in procs:
            return True
        time.sleep(0.5)
    return False


def instance_id_from_pid(pid: int) -> str:
    return f"inst_{pid}"


def list_cg37_windows() -> list[GameWindow]:
    """枚举可见的 cg37.exe 窗口（标题 + PID → inst_{pid}）。"""
    if sys.platform != "win32":
        return []
    cg37_pids = {pid for pid, _ in find_game_processes()}
    if not cg37_pids:
        return []

    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    rows: list[GameWindow] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        title = (buf.value or "").strip()
        if not title:
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in cg37_pids:
            return True
        rows.append(
            GameWindow(
                pid=int(pid.value),
                title=title,
                instance_id=instance_id_from_pid(int(pid.value)),
            )
        )
        return True

    user32.EnumWindows(callback, 0)
    rows.sort(key=lambda w: (w.title.lower(), w.pid))
    return rows


def get_foreground_cg37_window() -> GameWindow | None:
    """当前前台窗口若为 cg37.exe，返回其信息。"""
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    length = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    title = (buf.value or "").strip()
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    cg37_pids = {pid for pid, _ in find_game_processes()}
    if pid.value not in cg37_pids:
        return None
    return GameWindow(
        pid=int(pid.value),
        title=title or f"cg37.exe ({pid.value})",
        instance_id=instance_id_from_pid(int(pid.value)),
    )
