#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章中控 · 监视切页：读 MiniBridge state.json，不扫 TestUi 日志。

首次进入切页才建重控件；扫盘/EnumWindows 放后台，避免切页卡死。
"""
from __future__ import annotations

import sys
import threading
import time
import tkinter as tk
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from tkinter import messagebox, ttk

from assistant_common import ipc
from assistant_common.config import load_settings, save_settings

STAGE_STALE_SEC = 180
BARK_MIN_INTERVAL_SEC = 600
REFRESH_MS = 1000
BARK_TITLE = "序章中控"


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


def send_bark(title: str, body: str, base: str) -> str | None:
    raw = (base or "").strip()
    if not raw:
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


def hung_pids(pids: set[int]) -> set[int]:
    if not pids or sys.platform != "win32":
        return set()
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    out: set[int] = set()
    target = set(pids)

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _lparam):
        proc_id = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc_id))
        pid = int(proc_id.value)
        if pid not in target:
            return True
        if not user32.IsWindowVisible(hwnd):
            return True
        if user32.IsHungAppWindow(hwnd):
            out.add(pid)
        return True

    user32.EnumWindows(callback, 0)
    return out


def _captain_name(overview: str) -> str:
    if not overview:
        return ""
    lines = overview.splitlines()
    for i, line in enumerate(lines):
        if line.strip() == "【队长】" and i + 1 < len(lines):
            nxt = lines[i + 1]
            if nxt.startswith("名称:"):
                name = nxt.split(":", 1)[-1].strip()
                return "" if name in ("(无)",) else name
    return ""


def _collect_rows(
    watch: set[str],
    watch_since: dict[str, float],
    last_stop: dict[str, str],
) -> tuple[list[dict], list[tuple[str, str, str]], dict[str, str], dict[str, float]]:
    """后台线程：读 state + 查无响应窗口。"""
    snaps = ipc.list_instance_snapshots()
    live = [r for r in snaps if r.get("alive")]
    pids = {int(r.get("pid_txt") or 0) for r in live if r.get("pid_txt")}
    hung = hung_pids(pids)
    now = time.time()
    rows: list[dict] = []
    alerts: list[tuple[str, str, str]] = []
    new_since = dict(watch_since)
    new_stop = dict(last_stop)

    for r in live:
        st = r.get("state") or {}
        iid = r["instance_id"]
        pid = int(r.get("pid_txt") or 0)
        if iid in watch:
            new_since.setdefault(iid, now)
        overview = str(st.get("overview") or "")
        name = _captain_name(overview)
        helper = "开" if st.get("helper_open") else ("已加载" if st.get("helper_loaded") else "关")
        mode = str(st.get("battle_mode_label") or st.get("battle_mode") or "")
        escort = bool(st.get("escort_battle"))
        last_end = int(st.get("escort_last_end_unix") or 0)
        stop = str(st.get("escort_stop") or "")
        flag = str(st.get("phase") or "")
        if hung and pid in hung:
            flag = "窗口无响应"
        elif stop:
            flag = "停战 " + stop
        elif escort:
            since = last_end if last_end > 0 else int(new_since.get(iid, now))
            age = now - since if since else 0
            flag = f"护航战斗 距上次结束 {int(age)}s"
            if iid in watch and age >= STAGE_STALE_SEC:
                flag = f"卡住 {int(age)}s 无战斗结束"
                alerts.append((iid, name or iid, flag))
        script = str(st.get("script_run") or "")
        if script and flag in ("in_game", ""):
            flag = script
        rows.append(
            {
                "instance_id": iid,
                "pid": pid,
                "name": name,
                "helper": helper,
                "mode": mode,
                "flag": flag,
                "overview": overview,
                "script_run": script,
                "ctrl_msg": str(st.get("ctrl_msg") or ""),
                "ctrl_ok": bool(st.get("ctrl_ok")),
                "escort_note": str(st.get("escort_note") or ""),
                "alert": iid in watch
                and ((hung and pid in hung) or "卡住" in flag or bool(stop)),
            }
        )
        if iid in watch and hung and pid in hung:
            alerts.append((iid, name or iid, "窗口无响应"))
        prev = new_stop.get(iid, "")
        if iid in watch and stop and stop != prev:
            alerts.append((iid, name or iid, stop))
        new_stop[iid] = stop

    return rows, alerts, new_stop, new_since


class MonitorTab:
    def __init__(self, parent: tk.Misc, root: tk.Tk) -> None:
        self.parent = parent
        self.root = root
        self._lock = threading.Lock()
        self._watch: set[str] = set(load_settings().get("central_watch_ids") or [])
        self._last_stop: dict[str, str] = {}
        self._watch_since: dict[str, float] = {}
        self._pending: dict[str, tuple[str, str]] = {}
        self._last_push = 0.0
        self._paused = False
        self._rows: list[dict] = []
        self._selected = ""
        self._after_id: str | None = None
        self._built = False
        self._active = False
        self._scan_busy = False
        self._first_paint = True

        self.status_var = tk.StringVar(value="切到本页后再加载监视…")
        self.bark_var = tk.StringVar(value=str(load_settings().get("central_bark_url") or ""))

        self._shell = ttk.Frame(parent)
        self._shell.pack(fill=tk.BOTH, expand=True)
        self._placeholder = ttk.Label(
            self._shell,
            text="首次打开监视会稍等一下…\n正在准备列表（不会卡住窗口）",
            foreground="#888",
            justify=tk.CENTER,
            font=("Microsoft YaHei UI", 11),
        )
        self._placeholder.pack(expand=True, pady=40)

        # 空闲时在后台预热 EnumWindows / 读盘，减轻首次真正刷新的尖峰
        self.root.after(1800, self._warm_idle)

    def set_active(self, active: bool) -> None:
        """由 Notebook 切页回调：只在监视页可见时轮询。"""
        was = self._active
        self._active = bool(active)
        if self._active and not was:
            self.status_var.set("加载监视…")
            if not self._built:
                # 先让切页动画走完，再搭控件，体感更柔和
                self.root.after(16, self._ensure_built)
            else:
                self.refresh(force=True)
        elif not self._active:
            self._cancel_after()

    def _warm_idle(self) -> None:
        if self._built:
            return

        def work() -> None:
            try:
                ipc.list_instance_snapshots()
                hung_pids(set())
            except Exception:
                pass

        threading.Thread(target=work, name="monitor-warm", daemon=True).start()

    def _cancel_after(self) -> None:
        if self._after_id:
            try:
                self.root.after_cancel(self._after_id)
            except tk.TclError:
                pass
            self._after_id = None

    def _ensure_built(self) -> None:
        if self._built:
            if self._active:
                self.refresh(force=True)
            return
        try:
            self._placeholder.destroy()
        except tk.TclError:
            pass
        self._build_ui()
        self._built = True
        if self._active:
            self.refresh(force=True)

    def _build_ui(self) -> None:
        head = ttk.Frame(self._shell)
        head.pack(fill=tk.X)
        ttk.Label(head, textvariable=self.status_var).pack(side=tk.LEFT)
        self.pause_btn = ttk.Button(head, text="暂停报警", command=self._toggle_pause)
        self.pause_btn.pack(side=tk.RIGHT, padx=(6, 0))
        ttk.Button(head, text="立即刷新", command=lambda: self.refresh(force=True)).pack(
            side=tk.RIGHT
        )

        bark_row = ttk.Frame(self._shell)
        bark_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Label(bark_row, text="Bark").pack(side=tk.LEFT)
        bark_entry = ttk.Entry(bark_row, textvariable=self.bark_var)
        bark_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(6, 6))
        bark_entry.bind("<FocusOut>", lambda _e: self._save_bark())
        bark_entry.bind("<Return>", lambda _e: self._save_bark())
        ttk.Button(bark_row, text="测试", command=self._test_bark).pack(side=tk.RIGHT)

        split = ttk.Panedwindow(self._shell, orient=tk.HORIZONTAL)
        split.pack(fill=tk.BOTH, expand=True, pady=(8, 0))
        left = ttk.Frame(split, padding=(0, 0, 4, 0))
        right = ttk.Frame(split, padding=(4, 0, 0, 0))
        split.add(left, weight=3)
        split.add(right, weight=2)

        ttk.Label(left, text="桥接窗口（选中后开关监控）").pack(anchor=tk.W)
        cols = ("pid", "name", "helper", "mode", "flag")
        self.tree = ttk.Treeview(left, columns=cols, show="headings", selectmode="extended")
        self.tree.heading("pid", text="PID")
        self.tree.heading("name", text="角色")
        self.tree.heading("helper", text="助手")
        self.tree.heading("mode", text="模式")
        self.tree.heading("flag", text="状态")
        self.tree.column("pid", width=70, anchor=tk.W)
        self.tree.column("name", width=120, anchor=tk.W)
        self.tree.column("helper", width=70, anchor=tk.W)
        self.tree.column("mode", width=90, anchor=tk.W)
        self.tree.column("flag", width=220, anchor=tk.W)
        self.tree.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.tree.bind("<<TreeviewSelect>>", lambda _e: self._on_select())
        self.tree.tag_configure("watch", background="#ff4d4d")
        self.tree.tag_configure("alert", background="#ff4d4d")

        btns = ttk.Frame(left)
        btns.pack(fill=tk.X, pady=(6, 0))
        ttk.Button(btns, text="开启监控", command=self._watch_on).pack(side=tk.LEFT)
        ttk.Button(btns, text="关闭监控", command=self._watch_off).pack(side=tk.LEFT, padx=(6, 0))
        ttk.Button(btns, text="老板键", command=self._boss_key_hide).pack(side=tk.LEFT, padx=(12, 0))
        ttk.Button(btns, text="恢复", command=self._boss_key_restore).pack(side=tk.LEFT, padx=(6, 0))

        ttk.Label(right, text="概况（助手 BuildOverview）").pack(anchor=tk.W)
        self.overview = tk.Text(right, wrap=tk.WORD, height=18, font=("Microsoft YaHei UI", 10))
        self.overview.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.overview.configure(state=tk.DISABLED)
        if self._first_paint:
            self.overview.configure(state=tk.NORMAL)
            self.overview.insert("1.0", "正在读取各窗口概况…")
            self.overview.configure(state=tk.DISABLED)
            self._first_paint = False

    def _save_bark(self) -> None:
        cfg = load_settings()
        cfg["central_bark_url"] = self.bark_var.get().strip()
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _persist_watch(self) -> None:
        cfg = load_settings()
        cfg["central_watch_ids"] = sorted(self._watch)
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _toggle_pause(self) -> None:
        self._paused = not self._paused
        if self._built:
            self.pause_btn.configure(text="继续报警" if self._paused else "暂停报警")
        with self._lock:
            if self._paused:
                self._pending.clear()

    def _test_bark(self) -> None:
        self._save_bark()
        err = send_bark(BARK_TITLE, "测试推送", self.bark_var.get())
        if err:
            messagebox.showerror("Bark", err, parent=self.root)
        else:
            messagebox.showinfo("Bark", "已发送测试", parent=self.root)

    def _selected_ids(self) -> list[str]:
        if not self._built:
            return []
        return [iid for iid in self.tree.selection() if iid]

    def selected_ids(self) -> list[str]:
        """供指令页读取监视树当前多选。"""
        return self._selected_ids()

    def _watch_on(self) -> None:
        for iid in self._selected_ids():
            self._watch.add(iid)
            self._watch_since.setdefault(iid, time.time())
        self._persist_watch()
        self.refresh(force=True)

    def _watch_off(self) -> None:
        for iid in self._selected_ids():
            self._watch.discard(iid)
            self._watch_since.pop(iid, None)
        self._persist_watch()
        self.refresh(force=True)

    def _boss_key_hide(self) -> None:
        self._run_boss_key("boss_key", "老板键", all_windows=False)

    def _boss_key_restore(self) -> None:
        # 批量：监视列表里所有窗口，不用选中
        self._run_boss_key("boss_key_restore", "恢复", all_windows=True)

    def _all_monitor_ids(self) -> list[str]:
        """监视树当前列出的全部窗口（去重保序）。"""
        seen: set[str] = set()
        out: list[str] = []
        for r in self._rows or []:
            iid = str(r.get("instance_id") or "").strip()
            if not iid or iid in seen:
                continue
            seen.add(iid)
            out.append(iid)
        return out

    def _run_boss_key(self, cmd: str, title: str, *, all_windows: bool = False) -> None:
        if all_windows:
            ids = self._all_monitor_ids()
            if not ids:
                messagebox.showinfo(title, "当前没有可操作的窗口", parent=self.root)
                return
        else:
            ids = self._selected_ids()
            if not ids:
                messagebox.showinfo(title, "请先选中要操作的窗口", parent=self.root)
                return
        name_by_id = {r["instance_id"]: r.get("name") or r["instance_id"] for r in self._rows}
        targets = [(iid, str(name_by_id.get(iid) or iid)) for iid in ids]

        def work() -> None:
            lines = []
            for iid, name in targets:
                if not ipc.bridge_alive(iid):
                    lines.append("失败 " + name + "：窗口离线")
                    continue
                req = ipc.send_command(iid, cmd)
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                if not msg:
                    msg = "无应答" if not ok else "完成"
                good = ok and msg.startswith("已")
                lines.append(("成功 " if good else "失败 ") + name + "：" + msg)
            text = "\n".join(lines) if lines else "无目标"
            self.root.after(0, lambda: messagebox.showinfo(title, text, parent=self.root))

        threading.Thread(target=work, daemon=True).start()

    def watch_once(self, instance_id: str) -> None:
        """启动完成时纳入红色监控。已在监控里则不动，关掉后不会自动加回。"""
        iid = str(instance_id or "").strip()
        if not iid or iid in self._watch:
            return
        self._watch.add(iid)
        self._watch_since.setdefault(iid, time.time())
        self._persist_watch()
        self.refresh(force=True)

    def unwatch(self, instance_ids: list[str]) -> None:
        changed = False
        for iid in instance_ids:
            if iid in self._watch:
                self._watch.discard(iid)
                self._watch_since.pop(iid, None)
                changed = True
        if changed:
            self._persist_watch()
            self.refresh(force=True)

    def _on_select(self) -> None:
        ids = self._selected_ids()
        self._selected = ids[0] if ids else ""
        self._fill_overview()

    def _fill_overview(self) -> None:
        if not self._built:
            return
        text = ""
        for row in self._rows:
            if row["instance_id"] == self._selected:
                extra = row.get("script_run") or ""
                ov = row.get("overview") or ""
                note = row.get("escort_note") or ""
                ctrl = row.get("ctrl_msg") or ""
                parts = []
                if extra:
                    parts.append("【脚本运行状态】\n" + extra)
                if ctrl:
                    if str(ctrl).startswith("进行中"):
                        mark = "执行中"
                    else:
                        mark = "成功" if row.get("ctrl_ok") else "失败"
                    parts.append("【上次指令】\n" + mark + " " + ctrl)
                if note:
                    parts.append("【护航】\n" + note)
                if ov:
                    parts.append(ov)
                text = "\n\n".join(parts)
                break
        self.overview.configure(state=tk.NORMAL)
        self.overview.delete("1.0", tk.END)
        self.overview.insert("1.0", text or "（无概况：未开助手或尚未心跳）")
        self.overview.configure(state=tk.DISABLED)

    def refresh(self, force: bool = False) -> None:
        if not self._built:
            return
        if not self._active and not force:
            return
        if self._scan_busy:
            if force:
                self.status_var.set("刷新中…")
            self._schedule_next()
            return

        self._scan_busy = True
        if force or not self._rows:
            self.status_var.set("刷新中…")

        watch = set(self._watch)
        watch_since = dict(self._watch_since)
        last_stop = dict(self._last_stop)

        def work() -> None:
            err = ""
            rows: list[dict] = []
            alerts: list[tuple[str, str, str]] = []
            new_stop = last_stop
            new_since = watch_since
            try:
                rows, alerts, new_stop, new_since = _collect_rows(watch, watch_since, last_stop)
            except Exception as exc:
                err = f"{type(exc).__name__}: {exc}"
            self.root.after(
                0,
                lambda: self._apply_scan(rows, alerts, new_stop, new_since, err),
            )

        threading.Thread(target=work, name="monitor-scan", daemon=True).start()

    def _apply_scan(
        self,
        rows: list[dict],
        alerts: list[tuple[str, str, str]],
        new_stop: dict[str, str],
        new_since: dict[str, float],
        err: str,
    ) -> None:
        self._scan_busy = False
        if not self._built:
            return
        if err:
            self.status_var.set(f"刷新失败：{err}")
            self._schedule_next()
            return

        self._last_stop = new_stop
        self._watch_since = new_since
        self._rows = rows
        try:
            prev_sel = set(self.tree.selection())
        except tk.TclError:
            prev_sel = set()

        self.tree.delete(*self.tree.get_children())
        for row in rows:
            iid = row["instance_id"]
            tags = []
            if iid in self._watch:
                tags.append("watch")
            if row["alert"]:
                tags.append("alert")
            self.tree.insert(
                "",
                tk.END,
                iid=iid,
                values=(
                    row["pid"] or "",
                    row["name"],
                    row["helper"],
                    row["mode"],
                    row["flag"],
                ),
                tags=tuple(tags),
            )
        still = [i for i in prev_sel if self.tree.exists(i)]
        if still:
            self.tree.selection_set(still)
        elif not self._selected and rows:
            pass
        self._fill_overview()
        n_watch = len(self._watch & {r["instance_id"] for r in rows})
        self.status_var.set(
            f"在线 {len(rows)} · 监控 {n_watch} · 心跳 state.json"
            + (" · 已暂停报警" if self._paused else "")
        )
        if not self._paused:
            for iid, title, detail in alerts:
                with self._lock:
                    self._pending[iid] = (title, detail)
            self._flush_bark()
        self._schedule_next()

    def _schedule_next(self) -> None:
        self._cancel_after()
        if not self._active:
            return
        self._after_id = self.root.after(REFRESH_MS, self.refresh)

    def _flush_bark(self) -> None:
        now = time.time()
        with self._lock:
            if not self._pending:
                return
            if now - self._last_push < BARK_MIN_INTERVAL_SEC:
                return
            items = list(self._pending.items())
            self._pending.clear()
            self._last_push = now
        titles = [t for _iid, (t, _d) in items]
        details = [f"{t} {d}" for _iid, (t, d) in items]
        name = "、".join(titles[:4])
        if len(titles) > 4:
            name += f" 等{len(titles)}个"
        body = f"{name} 异常：{'；'.join(details)}"
        err = send_bark(BARK_TITLE, body, self.bark_var.get())
        if err:
            with self._lock:
                for iid, pair in items:
                    self._pending.setdefault(iid, pair)
                self._last_push = 0.0
            self.status_var.set(f"Bark 失败：{err}")
        else:
            self.status_var.set(f"Bark 已推 {datetime.now().strftime('%H:%M:%S')}")
