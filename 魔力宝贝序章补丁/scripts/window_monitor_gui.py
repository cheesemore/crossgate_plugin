#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""窗口监视：右下角置顶，刷新 cg37 标题；卡死/卡循环 Bark 推送。
中元券等统计逻辑保留作参考（面板中元循环已卸）。
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
STATS_PATH = Path.home() / ".seqchapter_helper" / "window_monitor_stats.json"
STAGE_STALE_SEC = 180
LOG_NAME = "SeqChapterTestUi.log"

ZY_PHASE = (
    "zy-loop 开始",
    "zy-loop scan done",
    "zy-loop stop",
    "zy-catch start",
    "zy-catch stop",
    "zy-xfer start",
    "zy-xfer stop",
    "zy-xfer resume-captain",
    "zy-xfer remote-pull",
    "zy bank ",
    "zy-all start",
    "zy-all stop",
    "zy-all quota",
    "zy-all return",
    "zy-all skip",
    "skc start",
    "skc stop",
    "skc floor change",
    "skc handoff",
    "skc nav",
    "skc after-wall",
    "wild-ex start",
    "wild-ex stop",
    "wild-ex 存",
    "wild-ex 开超银",
    "wild-ex 个人仓",
    "凑不齐一套",
    "zy seal remain=",
    "zy seal empty",
    "zy battle exit",
    "zy ticket ",
    "zy exchange ok",
    "轮兑换完成",
)
ZY_START = (
    "zy-loop 开始",
    "zy-catch start",
    "zy-xfer start",
    "zy-all start",
    "skc start",
    "wild-ex start",
)
ZY_STOP_OK = ("zy-loop stop 已手动停止",)
ZY_STOP_ALERT = (
    "zy-loop stop 兑换中断",
    "zy-loop stop 仓检不通过",
    "zy-loop stop 抓齐未完成",
    "zy-loop stop 未能开始兑换",
    "zy-loop stop 封印卡已用尽",
    "zy seal empty STOP",
)

UID_TAIL_RE = re.compile(r"uid尾(\d+)")
# 账号银张数（仅展示，不再用于券速率）
TICKET_BANK_RE = re.compile(r"\bbank=(-?\d+)")
SEAL_RE = re.compile(r"zy seal remain=(\d+)")
# 兑换成功：DLL zy exchange ok round=N，或文案「第N轮兑换完成」
EXCHANGE_OK_RE = re.compile(r"(?:zy exchange ok round=(\d+)|第(\d+)轮兑换完成)")

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
        return str(data.get("bark_url") or "").strip()
    except (OSError, ValueError, TypeError):
        return ""


def save_bark_url(url: str) -> None:
    SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
    payload: dict = {"bark_url": (url or "").strip()}
    try:
        old = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        if isinstance(old, dict):
            payload = {**old, **payload}
    except (OSError, ValueError, TypeError):
        pass
    SETTINGS_PATH.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


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
        return None  # 未配置则跳过，不算失败
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


def _fmt_dur(sec: float) -> str:
    if sec < 0:
        sec = 0
    m = int(sec // 60)
    s = int(sec % 60)
    if m >= 60:
        h = m // 60
        m = m % 60
        return f"{h}时{m}分"
    return f"{m}分{s}秒"


def _title_key(title: str) -> str:
    t = (title or "").strip()
    for junk in ("★自动中★", "自动中", "★自动烧卡中★"):
        t = t.replace(junk, "")
    t = re.sub(r"\s+", " ", t).strip()
    return t[:48] if t else ""


class AccountStats:
    """按账号：本监视会话兑换成功次数=券数；速率÷锚点起时长。封印卡仍跟日志。"""

    __slots__ = (
        "key",
        "label",
        "seal_remain",
        "ticket_est",
        "rate_start_ts",
        "rate_start_src",
        "first_seal",
        "last_exchange_ts",
        "last_seal_at_ticket",
        "round_count",
        "sum_round_sec",
        "last_round_sec",
        "tickets_gained",
        "seal_spent",
        "bank_tickets",
        "cached_seal_per_hour",
        "cached_ticket_per_hour",
    )

    def __init__(self, key: str) -> None:
        self.key = key
        self.label = key
        self.seal_remain = -1
        self.ticket_est = 0  # 本会话兑换成功次数
        self.rate_start_ts = 0.0
        self.rate_start_src = ""  # script | monitor | exchange
        self.first_seal = -1
        self.last_exchange_ts = 0.0
        self.last_seal_at_ticket = -1
        self.round_count = 0
        self.sum_round_sec = 0.0
        self.last_round_sec = 0.0
        self.tickets_gained = 0
        self.seal_spent = 0
        self.bank_tickets = -1
        self.cached_seal_per_hour = 0.0
        self.cached_ticket_per_hour = 0.0

    def to_dict(self) -> dict:
        return {k: getattr(self, k) for k in self.__slots__}

    @classmethod
    def from_dict(cls, data: dict) -> "AccountStats":
        st = cls(str(data.get("key") or "unknown"))
        for k in cls.__slots__:
            if k in data and data[k] is not None:
                setattr(st, k, data[k])
        # 兼容旧字段
        if st.rate_start_ts <= 0 and data.get("first_ticket_ts"):
            try:
                st.rate_start_ts = float(data["first_ticket_ts"])
            except (TypeError, ValueError):
                pass
        return st

    def avg_round_sec(self) -> float:
        if self.round_count <= 0:
            return 0.0
        return self.sum_round_sec / self.round_count

    def hours_since_start(self, now: float) -> float:
        if self.rate_start_ts <= 0:
            return 0.0
        return max(0.0, (now - self.rate_start_ts) / 3600.0)

    def recompute_rates(self, now: float) -> None:
        h = self.hours_since_start(now)
        if h <= 0.05:
            self.cached_seal_per_hour = 0.0
            self.cached_ticket_per_hour = 0.0
            return
        self.cached_seal_per_hour = self.seal_spent / h
        self.cached_ticket_per_hour = self.tickets_gained / h

    def stats_line(self, now: float) -> str:
        seal = "?" if self.seal_remain < 0 else str(self.seal_remain)
        if self.rate_start_ts <= 0 and self.tickets_gained <= 0:
            return f"封印卡{seal} 中元券0（等待脚本/兑换）"
        last_r = _fmt_dur(self.last_round_sec) if self.round_count else "-"
        avg_r = _fmt_dur(self.avg_round_sec()) if self.round_count else "-"
        src = {"script": "自脚本", "monitor": "自监视", "exchange": "自首兑"}.get(
            self.rate_start_src, ""
        )
        src_s = f" {src}" if src else ""
        return (
            f"封印卡{seal} "
            f"中元券{self.tickets_gained}（兑成功）{src_s} "
            f"本轮{last_r} 均轮{avg_r} "
            f"卡{self.cached_seal_per_hour:.0f}/时 "
            f"券{self.cached_ticket_per_hour:.1f}/时"
        )


class PidState:
    __slots__ = (
        "last_seen",
        "last_progress",
        "last_line",
        "zy_active",
        "problem",
        "title",
        "alerted_kind",
        "seal_remain",
        "ticket_bank_est",
        "phase_hint",
        "uid_tail",
        "acct_key",
    )

    def __init__(self) -> None:
        now = time.time()
        self.last_seen = now
        self.last_progress = now
        self.last_line = ""
        self.zy_active = False
        self.problem = ""
        self.title = ""
        self.alerted_kind = ""
        self.seal_remain = -1
        self.ticket_bank_est = -1
        self.phase_hint = ""
        self.uid_tail = ""
        self.acct_key = ""


class ZhongyuanWatch:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._states: dict[int, PidState] = {}
        self._accounts: dict[str, AccountStats] = {}
        self._tails: dict[str, int] = {}
        self._stop = threading.Event()
        self._paused = False
        self._pending: dict[int, tuple[str, str]] = {}
        self._last_push = 0.0
        self._last_push_ok = ""
        self._last_push_err = ""
        self._last_hourly_key = ""
        self._last_save_ts = 0.0
        self._thread = threading.Thread(target=self._loop, name="zy-watch", daemon=True)
        self._started = time.time()
        self.bark_url = load_bark_url()
        self._load_stats()
        # 本监视窗口：券按会话重计（封印卡余量可保留）
        self._reset_ticket_session()

    def _reset_ticket_session(self) -> None:
        for ac in self._accounts.values():
            ac.tickets_gained = 0
            ac.ticket_est = 0
            ac.rate_start_ts = 0.0
            ac.rate_start_src = ""
            ac.last_exchange_ts = 0.0
            ac.round_count = 0
            ac.sum_round_sec = 0.0
            ac.last_round_sec = 0.0
            ac.seal_spent = 0
            ac.cached_ticket_per_hour = 0.0
            ac.cached_seal_per_hour = 0.0

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._save_stats()

    def set_paused(self, paused: bool) -> None:
        with self._lock:
            self._paused = bool(paused)
            if paused:
                self._pending.clear()
            self._save_stats_unlocked()

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

    def account_snapshot(self) -> dict[str, AccountStats]:
        with self._lock:
            out: dict[str, AccountStats] = {}
            for key, ac in self._accounts.items():
                out[key] = AccountStats.from_dict(ac.to_dict())
            return out

    def total_tickets(self) -> int:
        with self._lock:
            return sum(max(0, ac.tickets_gained) for ac in self._accounts.values())

    def push_status(self) -> tuple[str, str]:
        with self._lock:
            return self._last_push_ok, self._last_push_err

    def update_titles(self, rows: list[tuple[int, str]]) -> None:
        with self._lock:
            for pid, title in rows:
                st = self._states.get(pid)
                if st is None:
                    continue
                st.title = title
                if not st.uid_tail:
                    tk = _title_key(title)
                    if tk:
                        st.acct_key = "t:" + tk
                        ac = self._acct(st.acct_key)
                        if not ac.label or ac.label.startswith("t:") or ac.label.startswith("u:"):
                            ac.label = tk

    def _state(self, pid: int) -> PidState:
        st = self._states.get(pid)
        if st is None:
            st = PidState()
            self._states[pid] = st
        return st

    def _acct(self, key: str) -> AccountStats:
        ac = self._accounts.get(key)
        if ac is None:
            ac = AccountStats(key)
            self._accounts[key] = ac
        return ac

    def _resolve_acct(self, st: PidState) -> AccountStats | None:
        if st.uid_tail:
            key = "u:" + st.uid_tail
            st.acct_key = key
            ac = self._acct(key)
            if st.title:
                ac.label = _title_key(st.title) or ("尾" + st.uid_tail)
            elif not ac.label or ac.label == key:
                ac.label = "尾" + st.uid_tail
            return ac
        if st.acct_key:
            return self._acct(st.acct_key)
        tk = _title_key(st.title)
        if tk:
            st.acct_key = "t:" + tk
            ac = self._acct(st.acct_key)
            ac.label = tk
            return ac
        return None

    def _load_stats(self) -> None:
        try:
            data = json.loads(STATS_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError, TypeError):
            return
        accounts = data.get("accounts") if isinstance(data, dict) else None
        if not isinstance(accounts, dict):
            return
        for key, raw in accounts.items():
            if isinstance(raw, dict):
                self._accounts[str(key)] = AccountStats.from_dict({**raw, "key": key})
        self._last_hourly_key = str(data.get("last_hourly_key") or "")

    def _save_stats(self) -> None:
        with self._lock:
            self._save_stats_unlocked()

    def _save_stats_unlocked(self) -> None:
        try:
            STATS_PATH.parent.mkdir(parents=True, exist_ok=True)
            payload = {
                "saved_at": datetime.now().isoformat(timespec="seconds"),
                "last_hourly_key": self._last_hourly_key,
                "accounts": {k: v.to_dict() for k, v in self._accounts.items()},
            }
            STATS_PATH.write_text(
                json.dumps(payload, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            self._last_save_ts = time.time()
        except OSError:
            pass

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self._poll_logs()
                if not self._paused:
                    self._evaluate()
                    self._flush_push()
                if time.time() - self._last_save_ts >= 60:
                    self._save_stats()
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

            m_uid = UID_TAIL_RE.search(line)
            if m_uid:
                st.uid_tail = m_uid.group(1)

            m_seal = SEAL_RE.search(line)
            if m_seal:
                try:
                    st.seal_remain = int(m_seal.group(1))
                    ac = self._resolve_acct(st)
                    if ac is not None:
                        ac.seal_remain = st.seal_remain
                except ValueError:
                    pass

            if "zy ticket store done" in line and "bank=" in line:
                self._on_ticket_bank_line(st, line, now)

            if EXCHANGE_OK_RE.search(line):
                self._on_exchange_ok(st, line, now)

            phase_hit = next((k for k in ZY_PHASE if k in line), "")
            if phase_hit:
                st.last_progress = now
                st.phase_hint = phase_hit

            if any(k in line for k in ZY_START) or (
                phase_hit and "zy-loop stop" not in line and "zy seal empty" not in line
            ):
                if "zy-loop stop" not in line and "zy seal empty" not in line:
                    st.zy_active = True
                    if any(k in line for k in ZY_START):
                        self._on_script_start(st, now)
            if any(k in line for k in ZY_STOP_OK):
                st.zy_active = False
                st.problem = ""
                st.alerted_kind = ""
            alert_hit = next((k for k in ZY_STOP_ALERT if k in line), "")
            if alert_hit:
                st.zy_active = False
                if "zy seal empty" in line or "封印卡已用尽" in line:
                    reason = "封印卡已用尽"
                else:
                    reason = line.split("zy-loop stop", 1)[-1].strip() or alert_hit
                if alerting:
                    self._queue_locked(pid, f"脚本中断（{reason}）", "script")
            elif "zy-loop stop" in line:
                st.zy_active = False
                st.problem = ""
                st.alerted_kind = ""

    def _ensure_rate_start(self, ac: AccountStats, now: float, src: str) -> None:
        """锚点：脚本启动优先；否则用监视窗口开启；再否则首兑时刻。"""
        if ac.rate_start_ts > 0 and ac.rate_start_src == "script":
            return
        if src == "script":
            ac.rate_start_ts = now
            ac.rate_start_src = "script"
            return
        if ac.rate_start_ts > 0:
            return
        # 无脚本启动记录时，用监视窗口开启时刻
        ac.rate_start_ts = self._started if self._started > 0 else now
        ac.rate_start_src = "monitor"

    def _on_script_start(self, st: PidState, now: float) -> None:
        """脚本新开：锚点=脚本启动，本会话兑换计数清零。"""
        ac = self._resolve_acct(st)
        if ac is None:
            return
        ac.rate_start_ts = now
        ac.rate_start_src = "script"
        ac.tickets_gained = 0
        ac.ticket_est = 0
        ac.last_exchange_ts = 0.0
        ac.round_count = 0
        ac.sum_round_sec = 0.0
        ac.last_round_sec = 0.0
        ac.seal_spent = 0
        ac.first_seal = ac.seal_remain if ac.seal_remain >= 0 else -1
        ac.recompute_rates(now)

    def _on_exchange_ok(self, st: PidState, line: str, now: float) -> None:
        """兑换成功一次 = +1 张券。"""
        ac = self._resolve_acct(st)
        if ac is None:
            return
        self._ensure_rate_start(ac, now, "exchange")
        seal = st.seal_remain if st.seal_remain >= 0 else ac.seal_remain
        if ac.last_exchange_ts > 0:
            round_sec = max(0.0, now - ac.last_exchange_ts)
            if round_sec >= 30:
                ac.round_count += 1
                ac.sum_round_sec += round_sec
                ac.last_round_sec = round_sec
            if seal >= 0 and ac.last_seal_at_ticket >= 0 and ac.last_seal_at_ticket >= seal:
                ac.seal_spent += ac.last_seal_at_ticket - seal
        elif seal >= 0 and ac.first_seal < 0:
            ac.first_seal = seal

        ac.tickets_gained += 1
        ac.ticket_est = ac.tickets_gained
        ac.last_exchange_ts = now
        if seal >= 0:
            ac.seal_remain = seal
            ac.last_seal_at_ticket = seal
        ac.recompute_rates(now)

    def _on_ticket_bank_line(self, st: PidState, line: str, now: float) -> None:
        """账号银张数仅旁路展示，不计入券速率。"""
        m_bank = TICKET_BANK_RE.search(line)
        if not m_bank:
            return
        bank = int(m_bank.group(1))
        if bank < 0:
            return
        st.ticket_bank_est = bank
        ac = self._resolve_acct(st)
        if ac is None:
            return
        ac.bank_tickets = bank
        seal = st.seal_remain if st.seal_remain >= 0 else ac.seal_remain
        if seal >= 0:
            ac.seal_remain = seal

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
                # 不删账号统计、不清封印卡/中元券
            for pid, st in self._states.items():
                if not st.zy_active or pid not in live:
                    if pid in live and not st.zy_active:
                        st.problem = ""
                    continue
                stale = now - st.last_progress
                if pid in hung:
                    self._queue_locked(pid, "卡死（窗口无响应）", "hung")
                elif stale >= STAGE_STALE_SEC:
                    self._queue_locked(
                        pid,
                        f"阶段卡住（{int(stale)}秒无换阶段"
                        + (f"：{st.phase_hint}" if st.phase_hint else "")
                        + "）",
                        "stale",
                    )
                else:
                    if st.alerted_kind in ("hung", "stale", "silent", "openbank"):
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

    # 整点报时已去掉（原先 _hourly_ticket_push）


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

        ttk.Label(left, text="全部窗口").pack(anchor=tk.W)
        ttk.Label(right, text="中元监控（兑成功次数=券 · 暂停不丢封印卡）").pack(anchor=tk.W)

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
            text="券=本监视会话内兑换成功次数 · 时长锚点优先脚本启动否则监视开启 · "
            "卡/时、券/时均÷该时长 · 暂停报警不清除封印卡 · 统计写入 "
            + str(STATS_PATH),
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
        WATCH.update_titles(rows)
        snap = WATCH.snapshot()
        accts = WATCH.account_snapshot()
        now_ts = time.time()
        paused = WATCH.is_paused()
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
        if paused:
            self.zy_list.insert(tk.END, "【已暂停报警】统计仍更新，封印卡/中元券不清除")
            self.zy_list.itemconfig(tk.END, foreground="#b36b00")
        if not watched:
            self.zy_list.insert(tk.END, "（没有在跑中元的窗口）")
            self.zy_list.itemconfig(tk.END, foreground="#888888")
            # 仍展示已有账号统计（暂停/停脚本后也能看）
            shown = 0
            for ac in sorted(accts.values(), key=lambda a: a.label):
                if ac.tickets_gained <= 0 and ac.seal_remain < 0 and ac.rate_start_ts <= 0:
                    continue
                self.zy_list.insert(tk.END, ac.label)
                self.zy_list.itemconfig(tk.END, foreground="#333333")
                self.zy_list.insert(tk.END, "  " + ac.stats_line(now_ts))
                self.zy_list.itemconfig(tk.END, foreground="#555555")
                shown += 1
                if shown >= 12:
                    break
        else:
            for pid, title, st in watched:
                left = int(STAGE_STALE_SEC - (now_ts - st.last_progress))
                if left < 0:
                    left = 0
                if st.problem:
                    count = st.problem
                    count_color = "#8b0000"
                else:
                    count = f"剩余 {left} 秒无换阶段发警报"
                    count_color = "#cc1f1f" if left <= 30 else "#666666"
                ac = None
                if st.acct_key and st.acct_key in accts:
                    ac = accts[st.acct_key]
                elif st.uid_tail:
                    ac = accts.get("u:" + st.uid_tail)
                self.zy_list.insert(tk.END, title or f"pid={pid}")
                self.zy_list.itemconfig(tk.END, foreground="#cc1f1f")
                if ac is not None:
                    self.zy_list.insert(tk.END, "  " + ac.stats_line(now_ts))
                else:
                    seal = "封印卡?" if st.seal_remain < 0 else f"封印卡{st.seal_remain}"
                    ticket = (
                        "中元券?"
                        if st.ticket_bank_est < 0
                        else f"中元券银{st.ticket_bank_est}"
                    )
                    self.zy_list.insert(
                        tk.END, f"  {seal}  {ticket}  阶段:{st.phase_hint or '-'}"
                    )
                self.zy_list.itemconfig(tk.END, foreground="#333333")
                self.zy_list.insert(tk.END, "  " + count)
                self.zy_list.itemconfig(tk.END, foreground=count_color)

        now = datetime.now().strftime("%H:%M:%S")
        ok, err = WATCH.push_status()
        push = f" · 上次推送 {ok}" if ok else ""
        if err:
            push += f" · 推送失败 {err[:40]}"
        pause_s = " · 已暂停报警" if paused else ""
        tickets = WATCH.total_tickets()
        self.status_var.set(
            f"{now}  ·  {len(rows)} 个 cross  ·  中元 {zy_n}  ·  券合计{tickets}{pause_s}{push}"
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
