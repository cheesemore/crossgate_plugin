#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""窗口监视：右下角置顶，每 10 秒刷新所有 cg37（cross）窗口标题。"""
from __future__ import annotations

import ctypes
import sys
import tkinter as tk
from ctypes import wintypes
from datetime import datetime
from tkinter import ttk

PROCESS_NAME = "cg37.exe"
REFRESH_MS = 10_000
WIN_W, WIN_H = 980, 320
WIN_MIN_W, WIN_MIN_H = 640, 200

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.EnumWindows.argtypes = [EnumWindowsProc, wintypes.LPARAM]
user32.EnumWindows.restype = wintypes.BOOL
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int

kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL
kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE,
    wintypes.DWORD,
    wintypes.LPWSTR,
    ctypes.POINTER(wintypes.DWORD),
]
kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

TH32CS_SNAPPROCESS = 0x00000002


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.Process32FirstW.restype = wintypes.BOOL
kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.Process32NextW.restype = wintypes.BOOL


def list_cg37_pids() -> list[int]:
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == wintypes.HANDLE(-1).value or snap is None:
        return []
    pids: list[int] = []
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        ok = kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            if (entry.szExeFile or "").lower() == PROCESS_NAME.lower():
                pids.append(int(entry.th32ProcessID))
            ok = kernel32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snap)
    return sorted(pids)


def _window_title(hwnd: int) -> str:
    n = user32.GetWindowTextLengthW(hwnd)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return (buf.value or "").strip()


def _window_class(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return (buf.value or "").strip()


def _windows_for_pids(pids: set[int]) -> dict[int, list[tuple[int, str, str, bool]]]:
    """pid -> [(hwnd, title, classname, visible), ...]"""
    by_pid: dict[int, list[tuple[int, str, str, bool]]] = {p: [] for p in pids}

    @EnumWindowsProc
    def _cb(hwnd, _lparam):
        pid = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in by_pid:
            return True
        title = _window_title(hwnd)
        cls = _window_class(hwnd)
        vis = bool(user32.IsWindowVisible(hwnd))
        by_pid[pid.value].append((int(hwnd), title, cls, vis))
        return True

    user32.EnumWindows(_cb, 0)
    return by_pid


def _pick_title(windows: list[tuple[int, str, str, bool]]) -> str:
    if not windows:
        return "(无窗口)"
    # 优先：可见 + 有标题；再任意有标题；Unity 主窗类名优先
    scored: list[tuple[int, str]] = []
    for _hwnd, title, cls, vis in windows:
        if not title:
            continue
        score = 0
        if vis:
            score += 100
        if "Unity" in cls:
            score += 20
        score += min(len(title), 80)
        scored.append((score, title))
    if scored:
        scored.sort(key=lambda x: x[0], reverse=True)
        return scored[0][1]
    # 只有空标题窗口
    for _hwnd, _title, cls, vis in windows:
        if vis:
            return f"(可见无标题 · {cls or '?'})"
    return f"(后台无标题 · {len(windows)} 窗)"


def list_cg37_windows() -> list[tuple[int, str]]:
    """返回 [(pid, title), ...]。"""
    pids = list_cg37_pids()
    if not pids:
        return []
    by_pid = _windows_for_pids(set(pids))
    return [(pid, _pick_title(by_pid.get(pid, []))) for pid in pids]


class WindowMonitorApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("窗口监视")
        self.geometry(f"{WIN_W}x{WIN_H}")
        self.minsize(WIN_MIN_W, WIN_MIN_H)
        self.attributes("-topmost", True)
        self.resizable(True, True)

        body = ttk.Frame(self, padding=8)
        body.pack(fill=tk.BOTH, expand=True)

        head = ttk.Frame(body)
        head.pack(fill=tk.X)
        self.status_var = tk.StringVar(value="准备中…")
        ttk.Label(head, textvariable=self.status_var).pack(side=tk.LEFT)
        ttk.Button(head, text="立即刷新", command=self.refresh).pack(side=tk.RIGHT)

        list_frame = ttk.Frame(body)
        list_frame.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        self.list = tk.Listbox(
            list_frame,
            activestyle="none",
            font=("Consolas", 10),
            selectmode=tk.EXTENDED,
            width=120,
        )
        yscroll = ttk.Scrollbar(list_frame, orient=tk.VERTICAL, command=self.list.yview)
        xscroll = ttk.Scrollbar(list_frame, orient=tk.HORIZONTAL, command=self.list.xview)
        self.list.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.list.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        list_frame.rowconfigure(0, weight=1)
        list_frame.columnconfigure(0, weight=1)

        tip = ttk.Label(
            self,
            text="监视 cg37.exe · 每 10 秒刷新 · 置顶右下角",
            foreground="#666666",
            padding=(8, 0, 8, 6),
        )
        tip.pack(fill=tk.X)

        self._place_bottom_right()
        self.refresh()
        self.after(REFRESH_MS, self._tick)

    def _place_bottom_right(self) -> None:
        self.update_idletasks()
        sw = self.winfo_screenwidth()
        sh = self.winfo_screenheight()
        x = max(0, sw - WIN_W - 16)
        y = max(0, sh - WIN_H - 56)
        self.geometry(f"{WIN_W}x{WIN_H}+{x}+{y}")

    def refresh(self) -> None:
        rows = list_cg37_windows()
        self.list.delete(0, tk.END)
        if not rows:
            self.list.insert(tk.END, "（当前没有 cg37 进程）")
        else:
            for i, (pid, title) in enumerate(rows, 1):
                self.list.insert(tk.END, f"{i}. pid={pid}  {title}")
                if "★自动中★" in title or "自动中" in title:
                    self.list.itemconfig(tk.END, foreground="#0a7a32")
        now = datetime.now().strftime("%H:%M:%S")
        self.status_var.set(f"{now}  ·  {len(rows)} 个 cross")

    def _tick(self) -> None:
        try:
            self.refresh()
        finally:
            self.after(REFRESH_MS, self._tick)


def main() -> int:
    app = WindowMonitorApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
