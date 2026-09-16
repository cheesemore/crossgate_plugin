#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""窗口监视：右下角置顶，刷新 cg37 标题；卡死 / 护航战斗异常 Bark 推送。

护航战斗（金色）：
- 日志出现「escort-battle 停战」→ Bark
- 护航战斗运行中 180 秒未出现「escort-battle 战斗结束」→ 视为卡住 Bark
"""
from __future__ import annotations

import ctypes
import json
import re
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
WIN_W, WIN_H = 1180, 480
WIN_MIN_W, WIN_MIN_H = 780, 320

BARK_TITLE = "序章监控"
BARK_MIN_INTERVAL_SEC = 600
SETTINGS_PATH = Path.home() / ".seqchapter_helper" / "window_monitor.json"
STAGE_STALE_SEC = 180
LOG_NAME = "SeqChapterTestUi.log"

# 护航战斗日志（SeqChapterTestUi）
EB_BATTLE_END = "escort-battle 战斗结束"
EB_STOP_MARK = "escort-battle 停战"
EB_MODE_LINE = "SelectBattleMode "
EB_MODE_ON = "SelectBattleMode escort_battle"
EB_ABORT_OFF = "escort-battle abort 模式已关"
EB_COLOR = "#c9a227"  # 金色

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


def load_settings() -> dict:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError, TypeError):
        return {}


def save_settings(patch: dict) -> None:
    SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload = {**load_settings(), **patch}
    SETTINGS_PATH.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def load_bark_url() -> str:
    return str(load_settings().get("bark_url") or "").strip()


def save_bark_url(url: str) -> None:
    save_settings({"bark_url": (url or "").strip()})


def _title_key(title: str) -> str:
    """稳定身份：产品 + 服务器 + 角色名。

    窗口标题含 Lv / ★战斗护航★ / 魔池数字会一直变；旧逻辑把魔池写进 key，
    魔池一变就匹配失败，表现为「莫名其妙退出监控」。
    """
    t = (title or "").strip()
    if not t:
        return ""
    if t.startswith("pid:"):
        return t
    m = re.match(r"^(.+?)\s+Lv\.\d+\b", t)
    if m:
        t = m.group(1)
    else:
        for junk in (
            "★自动中★",
            "自动中",
            "★自动烧卡中★",
            "★战斗护航★",
            "护航战斗",
        ):
            t = t.replace(junk, "")
        t = re.sub(r"魔池\s*-?\d*", "", t)
        t = re.sub(r"采集\d+\s*/\s*\d+", "", t)
    t = re.sub(r"\s+", " ", t).strip()
    return t[:80] if t else ""


def _title_looks_escort_battle(title: str) -> bool:
    s = title or ""
    return "战斗护航" in s or "护航战斗" in s


def _watch_key(pid: int, title: str) -> str:
    tk = _title_key(title)
    return tk if tk else f"pid:{pid}"


def load_eb_watch_keys() -> list[str]:
    raw = load_settings().get("eb_watch_keys")
    if not isinstance(raw, list):
        return []
    out: list[str] = []
    for item in raw:
        s = _title_key(str(item or ""))
        if s and s not in out:
            out.append(s)
    return out


def save_eb_watch_keys(keys: list[str]) -> None:
    cleaned: list[str] = []
    for k in keys:
        s = _title_key(str(k or ""))
        if s and s not in cleaned:
            cleaned.append(s)
    save_settings({"eb_watch_keys": cleaned})


def bark_request_url(base: str, title: str, body: str) -> str:
    raw = (base or "").strip()
    if not raw:
        raise ValueError("未填写 Bark 地址")
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
    raw = (base if base is not None else load_bark_url()) or ""
    if not raw.strip():
        return None
    try:
        url = bark_request_url(raw, title, body)
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
        "last_battle_end",
        "last_line",
        "eb_active",
        "watched",
        "watch_key",
        "problem",
        "title",
        "alerted_kind",
        "stop_reason",
        "battle_ends",
    )

    def __init__(self) -> None:
        now = time.time()
        self.last_seen = now
        self.last_battle_end = now
        self.last_line = ""
        self.eb_active = False
        self.watched = False
        self.watch_key = ""
        self.problem = ""
        self.title = ""
        self.alerted_kind = ""
        self.stop_reason = ""
        self.battle_ends = 0


class EscortBattleWatch:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._states: dict[int, PidState] = {}
        self._tails: dict[str, int] = {}
        self._stop = threading.Event()
        self._paused = False
        self._pending: dict[int, tuple[str, str]] = {}
        # 持久：稳定角色 key；会话：pid，避免标题短暂空/枚举漏窗时掉监控
        self._watch_keys: set[str] = set(load_eb_watch_keys())
        self._session_pids: set[int] = set()
        self._last_push = 0.0
        self._last_push_ok = ""
        self._last_push_err = ""
        self._thread = threading.Thread(target=self._loop, name="eb-watch", daemon=True)
        self._started = time.time()
        self.bark_url = load_bark_url()
        # 立刻把旧「含魔池」key 归一化写回，避免堆积重复
        try:
            save_eb_watch_keys(sorted(self._watch_keys))
        except OSError:
            pass

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._persist_watch_keys()

    def set_paused(self, paused: bool) -> None:
        with self._lock:
            self._paused = bool(paused)
            if paused:
                self._pending.clear()

    def is_paused(self) -> bool:
        with self._lock:
            return self._paused

    def snapshot(self) -> dict[int, PidState]:
        with self._lock:
            out: dict[int, PidState] = {}
            for pid, st in self._states.items():
                copy = PidState()
                for k in PidState.__slots__:
                    setattr(copy, k, getattr(st, k))
                out[pid] = copy
            return out

    def push_status(self) -> tuple[str, str]:
        with self._lock:
            return self._last_push_ok, self._last_push_err

    def watched_count(self) -> int:
        with self._lock:
            return sum(1 for st in self._states.values() if st.watched)

    def set_watched(self, pid: int, title: str, enabled: bool) -> None:
        with self._lock:
            st = self._state(pid)
            st.title = title or st.title
            key = _watch_key(pid, st.title)
            st.watch_key = key
            st.watched = bool(enabled)
            if enabled:
                self._watch_keys.add(key)
                self._session_pids.add(pid)
                st.last_battle_end = time.time()
                st.problem = ""
                st.alerted_kind = ""
                st.stop_reason = ""
            else:
                self._watch_keys.discard(key)
                self._session_pids.discard(pid)
                st.problem = ""
                st.alerted_kind = ""
                st.stop_reason = ""
                self._pending.pop(pid, None)
            self._persist_watch_keys_unlocked()

    def toggle_watched(self, pid: int, title: str) -> bool:
        with self._lock:
            st = self._states.get(pid)
            cur = bool(st.watched) if st is not None else False
        self.set_watched(pid, title, not cur)
        return not cur

    def is_watched(self, pid: int) -> bool:
        with self._lock:
            st = self._states.get(pid)
            return bool(st and st.watched)

    def _persist_watch_keys(self) -> None:
        with self._lock:
            self._persist_watch_keys_unlocked()

    def _persist_watch_keys_unlocked(self) -> None:
        try:
            save_eb_watch_keys(sorted(self._watch_keys))
        except OSError:
            pass

    def update_titles(self, rows: list[tuple[int, str]]) -> None:
        with self._lock:
            live_pids = {pid for pid, _ in rows}
            for pid, title in rows:
                st = self._state(pid)
                st.title = title
                key = _watch_key(pid, title)
                st.watch_key = key
                hit = (
                    key in self._watch_keys
                    or pid in self._session_pids
                )
                st.watched = hit
                if hit and key and not key.startswith("pid:"):
                    # 会话命中但 key 未入库（或旧魔池 key 已归一化）→ 补写稳定 key
                    if key not in self._watch_keys:
                        self._watch_keys.add(key)
                        self._persist_watch_keys_unlocked()
                    self._session_pids.add(pid)
                if _title_looks_escort_battle(title) and st.watched and not st.eb_active:
                    st.eb_active = True
                    st.last_battle_end = time.time()
            for pid in list(self._session_pids):
                if pid not in live_pids:
                    self._session_pids.discard(pid)
            for pid, st in self._states.items():
                if pid not in live_pids:
                    st.watched = False

    def _state(self, pid: int) -> PidState:
        st = self._states.get(pid)
        if st is None:
            st = PidState()
            self._states[pid] = st
        return st

    def _is_watched_unlocked(self, pid: int) -> bool:
        st = self._states.get(pid)
        return bool(st and st.watched)

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self._poll_logs()
                if not self._paused:
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
            self._ingest(path, pos, size, now, alerting=not seed_only and not self._paused)
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

            if EB_MODE_LINE in line:
                if EB_MODE_ON in line:
                    st.eb_active = True
                    st.last_battle_end = now
                    if st.watched:
                        st.problem = ""
                        st.alerted_kind = ""
                        st.stop_reason = ""
                else:
                    st.eb_active = False
                    if st.watched and st.alerted_kind != "stop":
                        st.problem = ""
                        st.alerted_kind = ""
                        st.stop_reason = ""

            if EB_ABORT_OFF in line:
                st.eb_active = False
                if st.watched and st.alerted_kind != "stop":
                    st.problem = ""
                    st.alerted_kind = ""
                    st.stop_reason = ""

            if "escort-battle " in line and EB_ABORT_OFF not in line:
                if not st.eb_active:
                    st.eb_active = True
                    st.last_battle_end = now

            if EB_BATTLE_END in line:
                st.eb_active = True
                st.last_battle_end = now
                st.battle_ends += 1
                if st.watched and st.alerted_kind in ("stale", "hung", "stop"):
                    st.alerted_kind = ""
                    st.problem = ""
                    st.stop_reason = ""

            if EB_STOP_MARK in line:
                st.eb_active = True
                reason = line
                idx = line.find(EB_STOP_MARK)
                if idx >= 0:
                    reason = line[idx:].strip()
                m = re.search(r"reason=(.+?)(?:\s+wasHang=|$)", reason)
                detail = m.group(1).strip() if m else reason
                st.stop_reason = detail
                st.last_battle_end = now
                if alerting and st.watched:
                    self._queue_locked(pid, f"护航战斗停战：{detail}", "stop")

    def _queue_locked(self, pid: int, detail: str, kind: str) -> None:
        st = self._state(pid)
        if not st.watched:
            return
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
            for pid in list(self._states):
                if pid not in live:
                    st = self._states[pid]
                    st.eb_active = False
                    st.watched = False
                    if st.alerted_kind != "stop":
                        st.problem = ""
            for pid, st in self._states.items():
                if not st.watched or pid not in live:
                    continue
                # 已勾选监控：即使尚未判为护航战斗模式，卡死也推
                if pid in hung:
                    self._queue_locked(pid, "卡死（窗口无响应）", "hung")
                    continue
                if not st.eb_active:
                    continue
                if st.alerted_kind == "stop":
                    continue
                stale = now - st.last_battle_end
                if stale >= STAGE_STALE_SEC:
                    self._queue_locked(
                        pid,
                        f"护航战斗卡住（{int(stale)}秒未战斗结束）",
                        "stale",
                    )
                else:
                    if st.alerted_kind in ("hung", "stale"):
                        st.alerted_kind = ""
                        st.problem = ""

    def _flush_push(self) -> None:
        now = time.time()
        with self._lock:
            if self._paused or not self._pending:
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


WATCH = EscortBattleWatch()


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
        self.pause_btn = ttk.Button(head, text="暂停报警", command=self._toggle_pause)
        self.pause_btn.pack(side=tk.RIGHT, padx=(6, 0))
        ttk.Button(head, text="立即刷新", command=self.refresh).pack(side=tk.RIGHT)

        bark_row = ttk.Frame(body)
        bark_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Label(bark_row, text="Bark").pack(side=tk.LEFT)
        self.bark_var = tk.StringVar(value=WATCH.bark_url or "")
        bark_entry = ttk.Entry(bark_row, textvariable=self.bark_var)
        bark_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(6, 6))
        bark_entry.bind("<FocusOut>", lambda _e: self._save_bark())
        bark_entry.bind("<Return>", lambda _e: self._save_bark())
        ttk.Button(bark_row, text="测试", command=self._test_bark).pack(side=tk.RIGHT)

        split = ttk.Panedwindow(body, orient=tk.HORIZONTAL)
        split.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        left = ttk.Frame(split, padding=(0, 0, 4, 0))
        right = ttk.Frame(split, padding=(4, 0, 0, 0))
        split.add(left, weight=1)
        split.add(right, weight=1)

        ttk.Label(left, text="全部窗口（选中后开关监控）").pack(anchor=tk.W)
        ttk.Label(
            right,
            text="已开启监控（金色 · 停战/180秒无战斗结束 → Bark）",
        ).pack(anchor=tk.W)

        left_btns = ttk.Frame(left)
        left_btns.pack(fill=tk.X, pady=(4, 0))
        ttk.Button(left_btns, text="开启监控", command=self._watch_on).pack(
            side=tk.LEFT
        )
        ttk.Button(left_btns, text="关闭监控", command=self._watch_off).pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Label(left_btns, text="也可双击切换", foreground="#888").pack(
            side=tk.LEFT, padx=(10, 0)
        )

        left_list_frm = ttk.Frame(left)
        left_list_frm.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.list = tk.Listbox(
            left_list_frm,
            activestyle="none",
            font=("Consolas", 10),
            selectmode=tk.EXTENDED,
        )
        yscroll = ttk.Scrollbar(left_list_frm, orient=tk.VERTICAL, command=self.list.yview)
        self.list.configure(yscrollcommand=yscroll.set)
        self.list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        yscroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.list.bind("<Double-Button-1>", self._toggle_selected)
        self._row_pids: list[int] = []

        right_list_frm = ttk.Frame(right)
        right_list_frm.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.eb_list = tk.Listbox(
            right_list_frm,
            activestyle="none",
            font=("Consolas", 10),
            selectmode=tk.EXTENDED,
        )
        eb_scroll = ttk.Scrollbar(
            right_list_frm, orient=tk.VERTICAL, command=self.eb_list.yview
        )
        self.eb_list.configure(yscrollcommand=eb_scroll.set)
        self.eb_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        eb_scroll.pack(side=tk.RIGHT, fill=tk.Y)

        tip = ttk.Label(
            self,
            text="仅「已开启监控」的窗口会 Bark：停战立即推送；"
            f"{STAGE_STALE_SEC} 秒无战斗结束视为卡住；卡死也会推送。勾选按「服+角色」记住（魔池变化不会掉监控）。",
            foreground="#666666",
            padding=(8, 0, 8, 6),
        )
        tip.pack(fill=tk.X)

        WATCH.start()
        self._place_bottom_right()
        self.refresh()
        self.after(REFRESH_MS, self._tick)

    def _selected_pids(self) -> list[tuple[int, str]]:
        rows = getattr(self, "_last_rows", [])
        out: list[tuple[int, str]] = []
        for idx in self.list.curselection():
            if 0 <= idx < len(self._row_pids):
                pid = self._row_pids[idx]
                title = ""
                for p, t in rows:
                    if p == pid:
                        title = t
                        break
                out.append((pid, title))
        return out

    def _watch_on(self) -> None:
        for pid, title in self._selected_pids():
            WATCH.set_watched(pid, title, True)
        self.refresh()

    def _watch_off(self) -> None:
        for pid, title in self._selected_pids():
            WATCH.set_watched(pid, title, False)
        self.refresh()

    def _toggle_selected(self, _event=None) -> None:
        sel = self._selected_pids()
        if not sel:
            return
        pid, title = sel[0]
        WATCH.toggle_watched(pid, title)
        self.refresh()

    def _on_close(self) -> None:
        self._save_bark()
        WATCH.stop()
        self.destroy()

    def _toggle_pause(self) -> None:
        paused = not WATCH.is_paused()
        WATCH.set_paused(paused)
        self.pause_btn.configure(text="继续报警" if paused else "暂停报警")

    def _save_bark(self) -> None:
        url = (self.bark_var.get() or "").strip()
        self.bark_var.set(url)
        WATCH.bark_url = url
        try:
            save_bark_url(url)
        except OSError:
            pass

    def _test_bark(self) -> None:
        self._save_bark()
        if not (WATCH.bark_url or "").strip():
            self.status_var.set("请先填写 Bark 地址")
            return
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
        self._last_rows = rows
        WATCH.update_titles(rows)
        snap = WATCH.snapshot()
        now_ts = time.time()
        paused = WATCH.is_paused()
        self.list.delete(0, tk.END)
        self._row_pids = []
        watch_n = 0
        if not rows:
            self.list.insert(tk.END, "（当前没有 cg37 进程）")
        else:
            for i, (pid, title) in enumerate(rows, 1):
                st = snap.get(pid)
                watched = bool(st and st.watched)
                if watched:
                    watch_n += 1
                mark = "[监]" if watched else "    "
                color = None
                if watched:
                    color = EB_COLOR
                elif "★自动中★" in title or "自动中" in title:
                    color = "#0a7a32"
                self.list.insert(tk.END, f"{mark} {i}. pid={pid}  {title}")
                self._row_pids.append(pid)
                if color:
                    self.list.itemconfig(tk.END, foreground=color)

        self.eb_list.delete(0, tk.END)
        watched_rows = [
            (pid, title, snap.get(pid))
            for pid, title in rows
            if snap.get(pid) is not None and snap[pid].watched
        ]
        if paused:
            self.eb_list.insert(tk.END, "【已暂停报警】")
            self.eb_list.itemconfig(tk.END, foreground="#b36b00")
        if not watched_rows:
            self.eb_list.insert(tk.END, "（尚未开启任何窗口的护航战斗监控）")
            self.eb_list.itemconfig(tk.END, foreground="#888888")
            self.eb_list.insert(tk.END, "左侧选中窗口 →「开启监控」或双击切换")
            self.eb_list.itemconfig(tk.END, foreground="#888888")
        else:
            for pid, title, st in watched_rows:
                assert st is not None
                mode_s = "护航战斗中" if st.eb_active else "已监控（等待护航战斗）"
                left = int(STAGE_STALE_SEC - (now_ts - st.last_battle_end))
                if left < 0:
                    left = 0
                if st.problem:
                    count = st.problem
                    count_color = "#8b0000"
                elif st.alerted_kind == "stop":
                    count = "已停战报警，等下一场战斗结束继续监视"
                    count_color = "#8b0000"
                elif not st.eb_active:
                    count = "未检测到护航战斗模式（不判卡住）"
                    count_color = "#888888"
                else:
                    count = f"剩余 {left} 秒无战斗结束发警报"
                    count_color = "#8b4513" if left <= 30 else "#666666"
                self.eb_list.insert(tk.END, f"{title or f'pid={pid}'}  · {mode_s}")
                self.eb_list.itemconfig(tk.END, foreground=EB_COLOR)
                self.eb_list.insert(
                    tk.END,
                    f"  战斗结束累计 {st.battle_ends}"
                    + (f"  · {st.stop_reason}" if st.stop_reason else ""),
                )
                self.eb_list.itemconfig(tk.END, foreground="#555555")
                self.eb_list.insert(tk.END, "  " + count)
                self.eb_list.itemconfig(tk.END, foreground=count_color)

        now = datetime.now().strftime("%H:%M:%S")
        ok, err = WATCH.push_status()
        push = f" · 上次推送 {ok}" if ok else ""
        if err:
            push += f" · 推送失败 {err[:40]}"
        pause_s = " · 已暂停报警" if paused else ""
        self.status_var.set(
            f"{now}  ·  {len(rows)} 个 cross  ·  监控中 {watch_n}{pause_s}{push}"
        )

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
