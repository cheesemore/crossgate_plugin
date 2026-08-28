#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""窗口监视：右下角置顶，刷新 cg37 标题；中元循环标红；卡死/卡循环 Bark 推送。"""
from __future__ import annotations

import ctypes
import json
import sys
import threading
import time
import tkinter as tk
import urllib.error
import urllib.parse
import urllib.request
from ctypes import wintypes
from datetime import datetime
from pathlib import Path
from tkinter import ttk

PROCESS_NAME = "cg37.exe"
REFRESH_MS = 1_000
WATCH_MS = 800
WIN_W, WIN_H = 1100, 420
WIN_MIN_W, WIN_MIN_H = 780, 280

BARK_DEFAULT_URL = "https://api.day.app/EC2tiUVGg9d2bdv85X5p5Z/"
BARK_TITLE = "序章监控"
BARK_MIN_INTERVAL_SEC = 600
SETTINGS_PATH = Path.home() / ".seqchapter_helper" / "window_monitor.json"
SILENCE_SEC = 120
STUCK_OPENBANK = 24
LOG_NAME = "SeqChapterTestUi.log"

ZY_PROGRESS = (
    "zy-loop ",
    "zy-catch ",
    "zy-xfer ",
    "zy-all ",
    "skc ",
    "wild-ex ",
    "仓检",
    "resume-captain",
)
ZY_START = (
    "zy-loop 开始",
    "zy-catch start",
    "zy-xfer start",
    "zy-all start",
    "skc start",
    "wild-ex ",
)
ZY_STOP_OK = ("zy-loop stop 已手动停止",)
ZY_STOP_ALERT = (
    "zy-loop stop 兑换中断",
    "zy-loop stop 仓检不通过",
    "zy-loop stop 抓齐未完成",
    "zy-loop stop 未能开始兑换",
)

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
user32.IsHungAppWindow.argtypes = [wintypes.HWND]
user32.IsHungAppWindow.restype = wintypes.BOOL

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


def process_image_path(pid: int) -> str | None:
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        n = wintypes.DWORD(32768)
        buf = ctypes.create_unicode_buffer(n.value)
        if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(n)):
            return buf.value or None
    finally:
        kernel32.CloseHandle(handle)
    return None


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
    for _hwnd, _title, cls, vis in windows:
        if vis:
            return f"(可见无标题 · {cls or '?'})"
    return f"(后台无标题 · {len(windows)} 窗)"


def list_cg37_windows() -> list[tuple[int, str]]:
    pids = list_cg37_pids()
    if not pids:
        return []
    by_pid = _windows_for_pids(set(pids))
    return [(pid, _pick_title(by_pid.get(pid, []))) for pid in pids]


def hung_pids(pids: set[int]) -> set[int]:
    if not pids:
        return set()
    by_pid = _windows_for_pids(pids)
    out: set[int] = set()
    for pid, wins in by_pid.items():
        for hwnd, title, cls, vis in wins:
            if not vis:
                continue
            if title or "Unity" in cls:
                if user32.IsHungAppWindow(hwnd):
                    out.add(pid)
                    break
    return out


def _default_log_paths() -> list[Path]:
    here = Path(__file__).resolve()
    cands = [
        here.parent.parent.parent / LOG_NAME,
        Path.cwd() / LOG_NAME,
    ]
    out: list[Path] = []
    seen: set[str] = set()
    for p in cands:
        if not p.is_file():
            continue
        key = str(p.resolve())
        if key in seen:
            continue
        seen.add(key)
        out.append(p)
    return out


def load_bark_url() -> str:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        url = str(data.get("bark_url") or "").strip()
        if url:
            return url
    except (OSError, ValueError, TypeError):
        pass
    return BARK_DEFAULT_URL


def save_bark_url(url: str) -> None:
    SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
    SETTINGS_PATH.write_text(
        json.dumps({"bark_url": (url or "").strip() or BARK_DEFAULT_URL}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def bark_request_url(base: str, title: str, body: str) -> str:
    raw = (base or "").strip() or BARK_DEFAULT_URL
    if "://" not in raw:
        raw = "https://api.day.app/" + raw.lstrip("/")
    parsed = urllib.parse.urlparse(raw)
    parts = [p for p in parsed.path.split("/") if p]
    key = parts[0] if parts else ""
    if not key:
        raise ValueError("Bark 链接里没有 key")
    host = parsed.netloc or "api.day.app"
    scheme = parsed.scheme or "https"
    t = urllib.parse.quote(title, safe="")
    b = urllib.parse.quote(body, safe="")
    return f"{scheme}://{host}/{key}/{t}/{b}"


def send_bark(title: str, body: str, base: str | None = None) -> str | None:
    try:
        url = bark_request_url(base if base is not None else load_bark_url(), title, body)
    except ValueError as exc:
        return str(exc)
    req = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            resp.read()
        return None
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        return str(exc)


class PidState:
    __slots__ = (
        "last_seen",
        "last_line",
        "zy_active",
        "openbank",
        "problem",
        "title",
        "alerted_kind",
    )

    def __init__(self) -> None:
        now = time.time()
        self.last_seen = now
        self.last_line = ""
        self.zy_active = False
        self.openbank = 0
        self.problem = ""
        self.title = ""
        self.alerted_kind = ""


class ZhongyuanWatch:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._states: dict[int, PidState] = {}
        self._tails: dict[str, int] = {}
        self._stop = threading.Event()
        self._pending: dict[int, tuple[str, str]] = {}
        self._last_push = 0.0
        self._last_push_ok = ""
        self._last_push_err = ""
        self._thread = threading.Thread(target=self._loop, name="zy-watch", daemon=True)
        self._started = time.time()
        self.bark_url = load_bark_url()

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def snapshot(self) -> dict[int, PidState]:
        with self._lock:
            out: dict[int, PidState] = {}
            for pid, st in self._states.items():
                copy = PidState()
                copy.last_seen = st.last_seen
                copy.last_line = st.last_line
                copy.zy_active = st.zy_active
                copy.openbank = st.openbank
                copy.problem = st.problem
                copy.title = st.title
                copy.alerted_kind = st.alerted_kind
                out[pid] = copy
            return out

    def push_status(self) -> tuple[str, str]:
        with self._lock:
            return self._last_push_ok, self._last_push_err

    def update_titles(self, rows: list[tuple[int, str]]) -> None:
        with self._lock:
            for pid, title in rows:
                st = self._states.get(pid)
                if st is not None:
                    st.title = title

    def _state(self, pid: int) -> PidState:
        st = self._states.get(pid)
        if st is None:
            st = PidState()
            self._states[pid] = st
        return st

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self._poll_logs()
                self._evaluate()
                self._flush_push()
            except Exception:
                pass
            self._stop.wait(WATCH_MS / 1000.0)

    def _log_files(self) -> list[Path]:
        found: dict[str, Path] = {}
        for path in _default_log_paths():
            found[str(path.resolve())] = path
        for pid in list_cg37_pids():
            image = process_image_path(pid)
            if not image:
                continue
            log = Path(image).parent / LOG_NAME
            if log.is_file():
                found[str(log.resolve())] = log
        return list(found.values())

    def _poll_logs(self) -> None:
        now = time.time()
        seed_only = now - self._started < 1.5
        for path in self._log_files():
            key = str(path.resolve())
            try:
                size = path.stat().st_size
            except OSError:
                continue
            pos = self._tails.get(key)
            if pos is None:
                self._ingest(path, max(0, size - 2_000_000), size, now, alerting=False)
                self._tails[key] = size
                continue
            if size < pos:
                pos = 0
            if size == pos:
                continue
            self._ingest(path, pos, size, now, alerting=not seed_only)
            self._tails[key] = size

    def _ingest(self, path: Path, start: int, end: int, now: float, *, alerting: bool) -> None:
        try:
            with path.open("rb") as fh:
                fh.seek(start)
                raw = fh.read(end - start)
        except OSError:
            return
        text = raw.decode("utf-8", "replace")
        for line in text.splitlines():
            self._on_line(line, now, alerting=alerting)

    def _on_line(self, line: str, now: float, *, alerting: bool) -> None:
        marker = "[pid="
        i = line.find(marker)
        if i < 0:
            return
        j = line.find("]", i)
        if j < 0:
            return
        try:
            pid = int(line[i + len(marker) : j])
        except ValueError:
            return
        with self._lock:
            st = self._state(pid)
            st.last_seen = now
            st.last_line = line.strip()[-160:]
            if "OpenBank" in line:
                st.openbank += 1
            if any(k in line for k in ZY_PROGRESS):
                st.openbank = 0
            if any(k in line for k in ZY_START) or (
                any(k in line for k in ZY_PROGRESS) and "zy-loop stop" not in line
            ):
                if "zy-loop stop" not in line:
                    st.zy_active = True
            if any(k in line for k in ZY_STOP_OK):
                st.zy_active = False
                st.problem = ""
                st.alerted_kind = ""
            alert_hit = next((k for k in ZY_STOP_ALERT if k in line), "")
            if alert_hit:
                st.zy_active = False
                reason = line.split("zy-loop stop", 1)[-1].strip() or alert_hit
                if alerting:
                    self._queue_locked(pid, f"脚本中断（{reason}）", "script")

    def _queue_locked(self, pid: int, detail: str, kind: str) -> None:
        st = self._state(pid)
        if st.alerted_kind == kind:
            return
        st.alerted_kind = kind
        st.problem = detail
        title = st.title or f"pid={pid}"
        self._pending[pid] = (title, detail)

    def _evaluate(self) -> None:
        now = time.time()
        if now - self._started < 8:
            return
        live = set(list_cg37_pids())
        hung = hung_pids(live)
        with self._lock:
            gone = [pid for pid in self._states if pid not in live]
            for pid in gone:
                st = self._states[pid]
                st.zy_active = False
                st.problem = ""
            for pid, st in self._states.items():
                if not st.zy_active or pid not in live:
                    if pid in live and not st.zy_active:
                        st.problem = ""
                    continue
                silent = now - st.last_seen
                if pid in hung:
                    self._queue_locked(pid, "卡死（窗口无响应）", "hung")
                elif silent >= SILENCE_SEC:
                    self._queue_locked(
                        pid, f"卡死（已无日志 {int(silent)} 秒）", "silent"
                    )
                elif st.openbank >= STUCK_OPENBANK:
                    self._queue_locked(pid, "卡循环（反复开仓）", "openbank")
                else:
                    if st.alerted_kind in ("hung", "silent", "openbank"):
                        st.alerted_kind = ""
                        st.problem = ""

    def _flush_push(self) -> None:
        now = time.time()
        with self._lock:
            if not self._pending:
                return
            if now - self._last_push < BARK_MIN_INTERVAL_SEC:
                return
            items = list(self._pending.items())
            self._pending.clear()
            self._last_push = now
        titles = [t for _pid, (t, _d) in items]
        details = [f"{t} {d}" for _pid, (t, d) in items]
        name = "、".join(titles[:4])
        if len(titles) > 4:
            name += f" 等{len(titles)}个"
        body = f"标题为{name}的窗口异常，异常情况{'；'.join(details)}"
        err = send_bark(BARK_TITLE, body, self.bark_url)
        with self._lock:
            if err:
                self._last_push_err = err
                for pid, pair in items:
                    self._pending.setdefault(pid, pair)
                self._last_push = 0.0
            else:
                self._last_push_ok = datetime.now().strftime("%H:%M:%S")
                self._last_push_err = ""


WATCH = ZhongyuanWatch()


class WindowMonitorApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("窗口监视")
        self.geometry(f"{WIN_W}x{WIN_H}")
        self.minsize(WIN_MIN_W, WIN_MIN_H)
        self.attributes("-topmost", True)
        self.resizable(True, True)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        body = ttk.Frame(self, padding=8)
        body.pack(fill=tk.BOTH, expand=True)

        head = ttk.Frame(body)
        head.pack(fill=tk.X)
        self.status_var = tk.StringVar(value="准备中…")
        ttk.Label(head, textvariable=self.status_var).pack(side=tk.LEFT)
        ttk.Button(head, text="立即刷新", command=self.refresh).pack(side=tk.RIGHT)

        bark_row = ttk.Frame(body)
        bark_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Label(bark_row, text="Bark").pack(side=tk.LEFT)
        self.bark_var = tk.StringVar(value=WATCH.bark_url or BARK_DEFAULT_URL)
        bark_entry = ttk.Entry(bark_row, textvariable=self.bark_var)
        bark_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(6, 6))
        bark_entry.bind("<FocusOut>", lambda _e: self._save_bark())
        ttk.Button(bark_row, text="测试", command=self._test_bark).pack(side=tk.RIGHT)

        split = ttk.Panedwindow(body, orient=tk.HORIZONTAL)
        split.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        left = ttk.Frame(split, padding=(0, 0, 4, 0))
        right = ttk.Frame(split, padding=(4, 0, 0, 0))
        split.add(left, weight=1)
        split.add(right, weight=1)

        ttk.Label(left, text="全部窗口").pack(anchor=tk.W)
        ttk.Label(right, text="中元监控（剩余秒数未响应发警报）").pack(anchor=tk.W)

        self.list = tk.Listbox(
            left,
            activestyle="none",
            font=("Consolas", 10),
            selectmode=tk.EXTENDED,
        )
        yscroll = ttk.Scrollbar(left, orient=tk.VERTICAL, command=self.list.yview)
        self.list.configure(yscrollcommand=yscroll.set)
        self.list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, pady=(4, 0))
        yscroll.pack(side=tk.RIGHT, fill=tk.Y, pady=(4, 0))

        self.zy_list = tk.Listbox(
            right,
            activestyle="none",
            font=("Consolas", 10),
            selectmode=tk.EXTENDED,
        )
        zy_scroll = ttk.Scrollbar(right, orient=tk.VERTICAL, command=self.zy_list.yview)
        self.zy_list.configure(yscrollcommand=zy_scroll.set)
        self.zy_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, pady=(4, 0))
        zy_scroll.pack(side=tk.RIGHT, fill=tk.Y, pady=(4, 0))

        tip = ttk.Label(
            self,
            text="左列全部窗口 · 右列正在跑中元的倒计时 · 卡死/卡循环推送（10 分钟最多 1 次）",
            foreground="#666666",
            padding=(8, 0, 8, 6),
        )
        tip.pack(fill=tk.X)

        WATCH.start()
        self._place_bottom_right()
        self.refresh()
        self.after(REFRESH_MS, self._tick)

    def _on_close(self) -> None:
        self._save_bark()
        WATCH.stop()
        self.destroy()

    def _save_bark(self) -> None:
        url = (self.bark_var.get() or "").strip() or BARK_DEFAULT_URL
        self.bark_var.set(url)
        WATCH.bark_url = url
        try:
            save_bark_url(url)
        except OSError:
            pass

    def _test_bark(self) -> None:
        self._save_bark()
        err = send_bark(BARK_TITLE, "测试推送，窗口监视工作正常", WATCH.bark_url)
        if err:
            self.status_var.set("Bark 测试失败：" + err[:80])
        else:
            self.status_var.set(datetime.now().strftime("%H:%M:%S") + "  ·  Bark 测试已发送")

    def _place_bottom_right(self) -> None:
        self.update_idletasks()
        sw = self.winfo_screenwidth()
        sh = self.winfo_screenheight()
        x = max(0, sw - WIN_W - 16)
        y = max(0, sh - WIN_H - 56)
        self.geometry(f"{WIN_W}x{WIN_H}+{x}+{y}")

    def refresh(self) -> None:
        rows = list_cg37_windows()
        WATCH.update_titles(rows)
        snap = WATCH.snapshot()
        now_ts = time.time()
        self.list.delete(0, tk.END)
        zy_n = 0
        if not rows:
            self.list.insert(tk.END, "（当前没有 cg37 进程）")
        else:
            for i, (pid, title) in enumerate(rows, 1):
                st = snap.get(pid)
                color = None
                if st is not None and st.zy_active:
                    zy_n += 1
                    color = "#cc1f1f"
                elif "★自动中★" in title or "自动中" in title:
                    color = "#0a7a32"
                self.list.insert(tk.END, f"{i}. pid={pid}  {title}")
                if color:
                    self.list.itemconfig(tk.END, foreground=color)

        self.zy_list.delete(0, tk.END)
        watched = [
            (pid, title, snap.get(pid))
            for pid, title in rows
            if snap.get(pid) is not None and snap[pid].zy_active
        ]
        if not watched:
            self.zy_list.insert(tk.END, "（没有在跑中元的窗口）")
            self.zy_list.itemconfig(tk.END, foreground="#888888")
        else:
            for pid, title, st in watched:
                left = int(SILENCE_SEC - (now_ts - st.last_seen))
                if left < 0:
                    left = 0
                if st.problem:
                    count = st.problem
                    count_color = "#8b0000"
                elif left <= 0:
                    count = "剩余 0 秒未响应发警报"
                    count_color = "#8b0000"
                else:
                    count = f"剩余 {left} 秒未响应发警报"
                    count_color = "#cc1f1f" if left <= 30 else "#333333"
                self.zy_list.insert(tk.END, title or f"pid={pid}")
                self.zy_list.itemconfig(tk.END, foreground="#cc1f1f")
                self.zy_list.insert(tk.END, "  " + count)
                self.zy_list.itemconfig(tk.END, foreground=count_color)

        now = datetime.now().strftime("%H:%M:%S")
        ok, err = WATCH.push_status()
        push = f" · 上次推送 {ok}" if ok else ""
        if err:
            push += f" · 推送失败 {err[:40]}"
        self.status_var.set(f"{now}  ·  {len(rows)} 个 cross  ·  中元 {zy_n}{push}")

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
