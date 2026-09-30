#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章中控 — 多开 + 监视（state.json）+ 指令 + 交易/分发切页。

指令：打钱 / 日常 / 一键丢弃道具 / 百人 / 噩梦 / 挑战 / 治疗回城 / 一键加点 / 大乱斗报名 / 道具传递 / 开始遇敌 / 停止遇敌 / 一键打卡 / 存钱 / 跳过动画。
交易：多给出方排队→单接收方；回城点2→1000(65,73)；可选超银取/存；全部交易。
分发：单给出方→多接收方（反向）；超银堆叠 + 单次交易数量（格）；可选超银取/存。

傻瓜补丁的「启动中控」打的就是本程序。
"""
from __future__ import annotations

import argparse
import random
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk

SHARED = Path(__file__).resolve().parents[2] / "序章助手共享"
sys.path.insert(0, str(SHARED))

from assistant_common import ipc  # noqa: E402
from assistant_common.accounts import (  # noqa: E402
    AccountProfile,
    delete_account,
    find_account_by_phone,
    load_accounts,
    normalize_secondary_code,
    upsert_account,
)
from assistant_common.config import get_game_root, load_settings, save_settings, set_game_root  # noqa: E402
from assistant_common.game import GameInstance, find_game_processes, launch_game  # noqa: E402
from assistant_common.patch_bridge import is_mini_bridge_ready  # noqa: E402
from assistant_common.single_instance import (  # noqa: E402
    CENTRAL_CONTROL_LOCK_KEY,
    ensure_single_instance,
)

from monitor_tab import MonitorTab  # noqa: E402

APP_TITLE = "序章中控"

# 自动分窗：每列最多 10 个，阶梯 80px；第 11 个起右移 1200px。一次最多 20 个。
TILE_STEP_PX = 80
TILE_COL_OFFSET_PX = 1200
TILE_PER_COL = 10
TILE_MAX_WINDOWS = 20
TILE_WAIT_HWND_SEC = 45


def _tile_xy(index: int) -> tuple[int, int]:
    """index 从 0 起：第 1 个 (0,0)，第 2 个 (80,80)…；第 11 个 (1200,0)。"""
    col = index // TILE_PER_COL
    row = index % TILE_PER_COL
    return col * TILE_COL_OFFSET_PX + row * TILE_STEP_PX, row * TILE_STEP_PX


def _find_hwnd_for_pid(pid: int) -> int | None:
    if sys.platform != "win32" or pid <= 0:
        return None
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    best_hwnd = 0
    best_score = -1

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _lparam):
        nonlocal best_hwnd, best_score
        proc_id = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc_id))
        if int(proc_id.value) != pid:
            return True
        visible = bool(user32.IsWindowVisible(hwnd))
        length = user32.GetWindowTextLengthW(hwnd)
        title = ""
        if length > 0:
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            title = (buf.value or "").strip()
        cls_buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls_buf, 256)
        cls = (cls_buf.value or "").strip()
        score = 0
        if visible:
            score += 100
        if title:
            score += min(len(title), 80)
        if "Unity" in cls:
            score += 20
        if score > best_score:
            best_score = score
            best_hwnd = int(hwnd)
        return True

    user32.EnumWindows(callback, 0)
    return best_hwnd or None


def _wait_hwnd_for_pid(pid: int, timeout: float = TILE_WAIT_HWND_SEC) -> int | None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        hwnd = _find_hwnd_for_pid(pid)
        if hwnd:
            return hwnd
        time.sleep(0.4)
    return None


def _move_window(hwnd: int, x: int, y: int) -> bool:
    if sys.platform != "win32" or not hwnd:
        return False
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.ShowWindow.restype = wintypes.BOOL
    user32.SetWindowPos.argtypes = [
        wintypes.HWND,
        wintypes.HWND,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        wintypes.UINT,
    ]
    user32.SetWindowPos.restype = wintypes.BOOL
    sw_restore = 9
    swp_nosize = 0x0001
    swp_nozorder = 0x0004
    user32.ShowWindow(hwnd, sw_restore)
    return bool(user32.SetWindowPos(hwnd, 0, x, y, 0, 0, swp_nosize | swp_nozorder))


def _snapshot_caption(row: dict) -> str:
    st = row.get("state") or {}
    overview = str(st.get("overview") or "")
    lines = overview.splitlines()
    for i, line in enumerate(lines):
        if line.strip() == "【队长】" and i + 1 < len(lines) and lines[i + 1].startswith("名称:"):
            name = lines[i + 1].split(":", 1)[-1].strip()
            if name:
                return name
    return str(row.get("instance_id") or "")


def _kill_game_pid(pid: int) -> tuple[bool, str]:
    """关掉指定游戏窗口。进程已经不在也算关掉了。"""
    if pid <= 0:
        return False, "没有进程号"
    try:
        proc = subprocess.run(
            ["taskkill", "/PID", str(pid), "/F"],
            capture_output=True,
            text=True,
            encoding="mbcs",
            errors="replace",
        )
    except OSError as exc:
        return False, "关闭失败 " + str(exc)
    text = ((proc.stdout or "") + (proc.stderr or "")).strip()
    if proc.returncode == 0:
        return True, "已关闭"
    if "没有找到" in text or "not found" in text.lower():
        return True, "窗口已不在"
    return False, "关闭失败 " + (text or str(proc.returncode))


class CentralControlApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        self.root.title(APP_TITLE)
        self.root.geometry("980x860")
        self.root.minsize(860, 720)

        self.instances: list[GameInstance] = []
        initial_root = get_game_root()
        # 若 settings 尚未持久化有效目录，把探测到的目录存下来，
        # 与傻瓜补丁 GUI 的默认目录保持一致，避免检测错目录。
        raw = load_settings().get("game_root", "").strip()
        if not raw or not (Path(raw) / "cg37_Data").is_dir():
            try:
                set_game_root(initial_root)
            except OSError:
                pass
        self.game_root_var = tk.StringVar(value=str(initial_root))

        outer = ttk.Frame(self.root, padding=14)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text=APP_TITLE, font=("Microsoft YaHei UI", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(
            outer,
            text="独立中控。多开与原多开器相同；监视读桥接心跳。指令页可给在线窗口下发脚本。",
            foreground="#555",
            wraplength=920,
        ).pack(anchor=tk.W, pady=(0, 10))

        notebook = ttk.Notebook(outer)
        notebook.pack(fill=tk.BOTH, expand=True)
        tab_multi = ttk.Frame(notebook, padding=8)
        tab_watch = ttk.Frame(notebook, padding=8)
        tab_cmd = ttk.Frame(notebook, padding=8)
        tab_trade = ttk.Frame(notebook, padding=8)
        tab_dist = ttk.Frame(notebook, padding=8)
        notebook.add(tab_multi, text="多开")
        notebook.add(tab_watch, text="监视")
        notebook.add(tab_cmd, text="指令")
        notebook.add(tab_trade, text="交易")
        notebook.add(tab_dist, text="分发")
        self._notebook = notebook

        ttk.Label(
            tab_cmd,
            text="左侧选一条指令后点启动。只发给监视页已开启监控、且已进游戏的窗口。日志为执行中、成功或失败。",
            foreground="#666",
            wraplength=860,
        ).pack(anchor=tk.W, pady=(4, 8))

        _cfg0 = load_settings()
        self.cmd_delay_base_var = tk.StringVar(value=str(_cfg0.get("central_cmd_delay_base", 3)))
        self.cmd_delay_float_var = tk.StringVar(value=str(_cfg0.get("central_cmd_delay_float", 5)))
        delay_frm = ttk.LabelFrame(tab_cmd, text="下发间隔（秒）", padding=6)
        delay_frm.pack(fill=tk.X, pady=(0, 8))
        delay_row = ttk.Frame(delay_frm)
        delay_row.pack(fill=tk.X)
        ttk.Label(delay_row, text="基础").pack(side=tk.LEFT)
        ttk.Entry(delay_row, textvariable=self.cmd_delay_base_var, width=6).pack(
            side=tk.LEFT, padx=(4, 12)
        )
        ttk.Label(delay_row, text="浮动").pack(side=tk.LEFT)
        ttk.Entry(delay_row, textvariable=self.cmd_delay_float_var, width=6).pack(
            side=tk.LEFT, padx=(4, 12)
        )
        ttk.Label(
            delay_row,
            text="每个号依次间隔 = 基础～基础+浮动（默认 3～8）",
            foreground="#888",
        ).pack(side=tk.LEFT)
        self.cmd_delay_base_var.trace_add("write", lambda *_a: self._save_cmd_delay())
        self.cmd_delay_float_var.trace_add("write", lambda *_a: self._save_cmd_delay())

        cmd_split = ttk.Panedwindow(tab_cmd, orient=tk.HORIZONTAL)
        cmd_split.pack(fill=tk.BOTH, expand=True)
        cmd_left = ttk.Frame(cmd_split, padding=(0, 0, 8, 0))
        cmd_right = ttk.Frame(cmd_split, padding=(8, 0, 0, 0))
        cmd_split.add(cmd_left, weight=1)
        cmd_split.add(cmd_right, weight=2)

        ttk.Label(cmd_left, text="指令").pack(anchor=tk.W)
        ttk.Button(cmd_left, text="启动", command=self._start_selected_cmd).pack(
            side=tk.BOTTOM, fill=tk.X, pady=(8, 0)
        )
        cmd_list_wrap = ttk.Frame(cmd_left)
        cmd_list_wrap.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.cmd_list = tk.Listbox(
            cmd_list_wrap,
            exportselection=False,
            activestyle="dotbox",
            font=("Microsoft YaHei UI", 11),
        )
        cmd_scroll = ttk.Scrollbar(cmd_list_wrap, orient=tk.VERTICAL, command=self.cmd_list.yview)
        self.cmd_list.configure(yscrollcommand=cmd_scroll.set)
        self.cmd_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        cmd_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self._cmd_catalog = [
            ("money", "自动打钱"),
            ("daily", "做日常"),
            ("junk_drop", "一键丢弃道具"),
            ("dojo", "百人"),
            ("hell", "噩梦百人"),
            ("crystal", "自动打挑战"),
            ("flora", "治疗回城"),
            ("auto_point", "一键加点"),
            ("brawl", "自动报名大乱斗"),
            ("item_transfer", "道具传递"),
            ("start_enc", "开始遇敌"),
            ("stop_enc", "停止遇敌"),
            ("fast_punch", "一键打卡"),
            ("save", "存钱"),
            ("skip", "打开跳过动画"),
            ("close", "一键关闭"),
        ]
        for _key, title in self._cmd_catalog:
            self.cmd_list.insert(tk.END, title)
        self.cmd_list.selection_set(0)
        self.cmd_list.bind("<<ListboxSelect>>", lambda _e: self._on_cmd_select())
        self.cmd_list.bind("<Double-Button-1>", lambda _e: self._start_selected_cmd())

        self.dojo_layer_var = tk.StringVar(value="31")
        self.hell_layer_var = tk.StringVar(value="2")
        self.crystal_diff_var = tk.StringVar(value="2")
        self.item_transfer_keyword_var = tk.StringVar(
            value=str(_cfg0.get("central_item_transfer_keyword") or "五仁")
        )
        self.trade_keyword_var = tk.StringVar(
            value=str(_cfg0.get("central_trade_keyword") or _cfg0.get("central_item_trade_keyword") or "五仁")
        )
        self.trade_stack_var = tk.StringVar(
            value=str(_cfg0.get("central_trade_stack", 20))
        )
        self.trade_from_bank_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_trade_from_bank", False))
        )
        self.trade_to_bank_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_trade_to_bank", False))
        )
        self.trade_lock_pos_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_trade_lock_pos", False))
        )
        self.trade_add_sender_var = tk.StringVar(value="")
        self.trade_receiver_var = tk.StringVar(value="")
        self._trade_label_to_iid: dict[str, str] = {}
        # 交易对象队列：[{iid, label, name}]，与 Listbox 行一一对应
        self._trade_sender_entries: list[dict] = []
        # 分发：单给出方 → 多接收方
        self.dist_keyword_var = tk.StringVar(
            value=str(_cfg0.get("central_dist_keyword") or self.trade_keyword_var.get() or "五仁")
        )
        self.dist_stack_var = tk.StringVar(
            value=str(_cfg0.get("central_dist_stack", _cfg0.get("central_trade_stack", 20)))
        )
        self.dist_qty_var = tk.StringVar(
            value=str(_cfg0.get("central_dist_qty", 10))
        )
        self.dist_from_bank_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_dist_from_bank", False))
        )
        self.dist_to_bank_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_dist_to_bank", False))
        )
        self.dist_lock_pos_var = tk.BooleanVar(
            value=bool(_cfg0.get("central_dist_lock_pos", False))
        )
        self.dist_add_recv_var = tk.StringVar(value="")
        self.dist_sender_var = tk.StringVar(value="")
        self._dist_recv_entries: list[dict] = []
        self.cmd_param = ttk.Frame(cmd_left)
        self.cmd_param_label = ttk.Label(self.cmd_param, text="层数")
        self.cmd_param_label.pack(side=tk.LEFT)
        self.cmd_layer_entry = ttk.Entry(self.cmd_param, width=16)
        self.cmd_layer_entry.pack(side=tk.LEFT, padx=(6, 0))

        ttk.Label(cmd_right, text="日志").pack(anchor=tk.W)
        self.cmd_result = tk.Text(cmd_right, wrap=tk.WORD, state=tk.DISABLED)
        self.cmd_result.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self._set_cmd_result("尚未下发。")
        self._on_cmd_select()
        self._build_trade_tab(tab_trade)
        self._build_dist_tab(tab_dist)
        self.monitor = MonitorTab(tab_watch, self.root)
        notebook.bind("<<NotebookTabChanged>>", self._on_notebook_tab)

        path_frm = ttk.LabelFrame(tab_multi, text="游戏目录", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 10))
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.game_root_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="选择目录", command=self.pick_game_dir, width=10).pack(side=tk.LEFT, padx=(6, 0))

        batch_frm = ttk.LabelFrame(tab_multi, text="批量一键（协议驱动，需先注入精简桥接）", padding=8)
        batch_frm.pack(fill=tk.X, pady=(0, 10))
        batch_row = ttk.Frame(batch_frm)
        batch_row.pack(fill=tk.X)
        self.batch_login_btn = ttk.Button(
            batch_row,
            text="一键登录并拉取多控",
            command=self.batch_login_fetch,
            width=22,
        )
        self.batch_login_btn.pack(side=tk.LEFT, padx=(0, 8))
        self.batch_summon_btn = ttk.Button(
            batch_row,
            text="一键召唤",
            command=self.batch_summon,
            width=14,
        )
        self.batch_summon_btn.pack(side=tk.LEFT)
        self.batch_status_var = tk.StringVar(value="批量状态：就绪")
        ttk.Label(
            batch_frm,
            textvariable=self.batch_status_var,
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
        ).pack(anchor=tk.W, pady=(6, 0))
        ttk.Label(
            batch_frm,
            text="「一键启动」会自动连接精简桥接并完成登录→进游戏→拉起离线多控→一键召唤（含 Tip 飘字）；"
            "下方批量按钮可按需单独触发登录/召唤，按 team≥5 协议判定聚齐（不依赖坐标）。",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
            wraplength=860,
        ).pack(anchor=tk.W, pady=(2, 0))

        acc_frm = ttk.LabelFrame(tab_multi, text="账号库", padding=8)
        acc_frm.pack(fill=tk.BOTH, expand=True)

        cols = ("label", "phone", "sec")
        self.acc_tree = ttk.Treeview(acc_frm, columns=cols, show="headings", selectmode="extended")
        self.acc_tree.heading("label", text="备注")
        self.acc_tree.heading("phone", text="手机号")
        self.acc_tree.heading("sec", text="二级码")
        self.acc_tree.column("label", width=160, anchor=tk.W)
        self.acc_tree.column("phone", width=160, anchor=tk.W)
        self.acc_tree.column("sec", width=90, anchor=tk.CENTER)
        self.acc_tree.pack(fill=tk.BOTH, expand=True)

        # 加大行高，列表更易读
        style = ttk.Style()
        try:
            style.configure("Acc.Treeview", rowheight=32, font=("Microsoft YaHei UI", 12))
            style.configure("Acc.Treeview.Heading", font=("Microsoft YaHei UI", 12, "bold"))
            self.acc_tree.configure(style="Acc.Treeview")
        except tk.TclError:
            pass

        btn_frm = ttk.Frame(acc_frm)
        btn_frm.pack(fill=tk.X, pady=(10, 0))
        self.launch_sel_btn = ttk.Button(
            btn_frm,
            text="一键启动选中",
            command=self.launch_selected_account,
            width=16,
        )
        self.launch_sel_btn.pack(side=tk.LEFT)
        self.launch_all_btn = ttk.Button(
            btn_frm,
            text="一键启动所有",
            command=self.launch_all_accounts,
            width=16,
        )
        self.launch_all_btn.pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(btn_frm, text="批量录入", command=self.import_accounts_excel, width=12).pack(side=tk.RIGHT)
        ttk.Button(btn_frm, text="批量导出", command=self.export_accounts_excel, width=12).pack(
            side=tk.RIGHT, padx=(0, 8)
        )
        ttk.Button(
            btn_frm, text="批量改二级码", command=self.batch_edit_secondary_code, width=12
        ).pack(side=tk.RIGHT, padx=(0, 8))
        ttk.Button(btn_frm, text="录入账号", command=self.add_account, width=12).pack(side=tk.RIGHT, padx=(0, 8))
        ttk.Button(btn_frm, text="修改账号", command=self.edit_account, width=12).pack(side=tk.RIGHT, padx=(0, 8))
        ttk.Button(btn_frm, text="删除账号", command=self.remove_account, width=12).pack(side=tk.RIGHT, padx=(0, 8))

        tile_frm = ttk.Frame(acc_frm)
        tile_frm.pack(fill=tk.X, pady=(8, 0))
        self.auto_tile_var = tk.BooleanVar(
            value=bool(load_settings().get("auto_tile_windows", False))
        )
        ttk.Checkbutton(
            tile_frm,
            text="自动分窗（批量启动后阶梯摆窗：第1个左上角，之后每次 +80,+80；第11个起右移1200px；最多一次20个）",
            variable=self.auto_tile_var,
            command=self._persist_auto_tile,
        ).pack(anchor=tk.W)
        self.auto_open_helper_var = tk.BooleanVar(
            value=bool(load_settings().get("auto_open_helper", False))
        )
        ttk.Checkbutton(
            tile_frm,
            text="序章助手自动打开（一进游戏就开，再拉多控/召唤；约3秒后缩到右上角；已手动打开则跳过）",
            variable=self.auto_open_helper_var,
            command=self._persist_auto_open_helper,
        ).pack(anchor=tk.W, pady=(4, 0))

        status_bar = ttk.Frame(tab_multi)
        status_bar.pack(fill=tk.X, pady=(8, 0))
        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(status_bar, textvariable=self.status_var, foreground="#666").pack(side=tk.LEFT)

        self.reload_accounts()

    def _on_notebook_tab(self, _event=None) -> None:
        try:
            idx = self._notebook.index(self._notebook.select())
        except tk.TclError:
            return
        # 0 多开 · 1 监视 · 2 指令 · 3 交易 · 4 分发
        self.monitor.set_active(idx == 1)

    def _set_status(self, text: str) -> None:
        self.root.after(0, lambda: self.status_var.set(text))

    def _persist_auto_tile(self) -> None:
        cfg = load_settings()
        cfg["auto_tile_windows"] = bool(self.auto_tile_var.get())
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _persist_auto_open_helper(self) -> None:
        cfg = load_settings()
        cfg["auto_open_helper"] = bool(self.auto_open_helper_var.get())
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _want_auto_open_helper(self) -> bool:
        try:
            return bool(self.auto_open_helper_var.get())
        except tk.TclError:
            return bool(load_settings().get("auto_open_helper", False))

    def _maybe_open_helper(self, instance_id: str, label: str) -> None:
        if not self._want_auto_open_helper():
            return
        try:
            ipc.open_helper_minimized(instance_id)
            self._set_status(f"[{label}] 已请求打开序章助手（约3秒后缩到右上角）")
        except Exception as exc:
            self._set_status(f"[{label}] 打开序章助手失败: {exc}")

    def pick_game_dir(self) -> None:
        chosen = filedialog.askdirectory(title="选择游戏根目录（含 cg37_Data）")
        if not chosen:
            return
        path = Path(chosen)
        if not (path / "cg37_Data").is_dir():
            messagebox.showerror("无效", "所选目录下没有 cg37_Data")
            return
        set_game_root(path)
        self.game_root_var.set(str(path))
        self._set_status(f"游戏目录已设为: {path}")

    def _game_root(self) -> Path:
        return Path(self.game_root_var.get().strip())

    def _bridge_is_ready(self) -> bool:
        root = self._game_root()
        if not root.is_dir():
            return False
        return is_mini_bridge_ready(root)

    def _warn_if_bridge_missing(self) -> bool:
        if self._bridge_is_ready():
            return True
        return messagebox.askyesno(
            "精简桥接未注入",
            "当前未检测到精简桥接，多开器启动后无法自动登录/召唤。\n\n"
            "请先在「序章补丁」中勾选「注入精简桥接」并应用。\n\n仍要启动游戏吗？",
        )

    def reload_accounts(self) -> None:
        for item in self.acc_tree.get_children():
            self.acc_tree.delete(item)
        for acc in load_accounts():
            sec = (acc.secondary_code or "").strip()
            self.acc_tree.insert(
                "",
                tk.END,
                iid=acc.id,
                values=(acc.label, acc.phone, sec if sec else "（空）"),
            )
        self._set_status(f"账号库共 {len(load_accounts())} 个账号")

    def _ask_secondary_code(self, *, initial: str = "", title: str = "二级码") -> str | None:
        """弹窗问二级码。返回 None=取消；空串=清空；6 位数字=有效码。"""
        raw = simpledialog.askstring(
            title,
            "二级码（6 位数字，可留空）:",
            initialvalue=initial or "",
            parent=self.root,
        )
        if raw is None:
            return None
        code, err = normalize_secondary_code(raw)
        if err:
            messagebox.showerror("二级码无效", err, parent=self.root)
            return None
        return code

    def add_account(self) -> None:
        label = simpledialog.askstring("备注", "账号备注（可选）:", parent=self.root) or ""
        phone = simpledialog.askstring("手机号", "手机号:", parent=self.root)
        if not phone:
            return
        password = simpledialog.askstring("密码", "密码:", show="*", parent=self.root)
        if password is None:
            return
        sec = self._ask_secondary_code(title="二级码")
        if sec is None:
            return
        try:
            upsert_account(
                AccountProfile.create(label, phone, password, secondary_code=sec)
            )
        except ValueError as exc:
            messagebox.showerror("录入失败", str(exc), parent=self.root)
            return
        self.reload_accounts()

    def export_accounts_excel(self) -> None:
        """批量导出当前账号。表格式与批量录入相同，导出的文件可直接再录入。"""
        try:
            from openpyxl import Workbook
            from openpyxl.styles import Alignment, Font, PatternFill
        except ImportError:
            messagebox.showerror(
                "缺少依赖",
                "需要 openpyxl 才能批量导出。\n请先执行：pip install openpyxl",
                parent=self.root,
            )
            return

        path = filedialog.asksaveasfilename(
            title="批量导出账号",
            defaultextension=".xlsx",
            filetypes=[("Excel 工作簿", "*.xlsx")],
            initialfile="中控账号批量导出.xlsx",
            parent=self.root,
        )
        if not path:
            return

        seen: set[str] = set()
        rows: list[tuple[str, str, str, str]] = []
        for acc in load_accounts():
            phone = (acc.phone or "").strip()
            if not phone or phone in seen:
                continue
            seen.add(phone)
            rows.append(
                (
                    (acc.label or phone).strip(),
                    phone,
                    acc.password or "",
                    acc.secondary_code or "",
                )
            )

        wb = Workbook()
        ws = wb.active
        ws.title = "账号"
        headers = ("备注", "手机号", "密码", "二级码")
        ws.append(list(headers))
        header_font = Font(name="微软雅黑", bold=True, color="FFFFFF")
        header_fill = PatternFill("solid", fgColor="1F4E79")
        for col, _ in enumerate(headers, start=1):
            cell = ws.cell(row=1, column=col)
            cell.font = header_font
            cell.fill = header_fill
            cell.alignment = Alignment(horizontal="center", vertical="center")
        for label, phone, password, sec in rows:
            ws.append([label, phone, password, sec])
            row_i = ws.max_row
            for col in (1, 2, 3, 4):
                cell = ws.cell(row=row_i, column=col)
                cell.number_format = "@"
                cell.alignment = Alignment(vertical="center")
        ws.column_dimensions["A"].width = 18
        ws.column_dimensions["B"].width = 18
        ws.column_dimensions["C"].width = 18
        ws.column_dimensions["D"].width = 12

        tip = wb.create_sheet("说明", 0)
        tip.column_dimensions["A"].width = 78
        tip["A1"] = "中控账号批量导出"
        tip["A1"].font = Font(name="微软雅黑", bold=True, size=14)
        tips = [
            "本文件由「批量导出」生成，可直接用多开器或中控的「批量录入」导入。",
            "1. 「账号」表列：备注、手机号、密码、二级码。表头不要改名。",
            "2. 二级码可空；填写则必须是 6 位数字。",
            "3. 手机号相同视为同一账号：已有则更新，没有则新增。",
            "4. 备注/密码/二级码与库里不一致时，以本表录入的内容为准。",
            "5. 同一文件里手机号重复时，靠后的那一行生效。",
            "6. 空行、手机号或密码为空的行会跳过。",
        ]
        for i, line in enumerate(tips, start=3):
            tip[f"A{i}"] = line

        try:
            wb.save(path)
        except OSError as exc:
            messagebox.showerror("保存失败", str(exc), parent=self.root)
            return

        self._set_status(f"已批量导出 {len(rows)} 个账号：{path}")
        messagebox.showinfo(
            "完成",
            f"已导出 {len(rows)} 个账号：\n{path}\n\n该文件可直接用于「批量录入」。",
            parent=self.root,
        )

    def import_accounts_excel(self) -> None:
        """从 Excel 批量录入账号（同手机号则更新）。"""
        try:
            from openpyxl import load_workbook
        except ImportError:
            messagebox.showerror(
                "缺少依赖",
                "需要 openpyxl 才能批量录入。\n请先执行：pip install openpyxl",
                parent=self.root,
            )
            return

        path = filedialog.askopenfilename(
            title="选择账号 Excel",
            filetypes=[("Excel 工作簿", "*.xlsx"), ("所有文件", "*.*")],
            parent=self.root,
        )
        if not path:
            return

        try:
            added, updated, skipped, errors = self._import_accounts_from_xlsx(path)
        except Exception as exc:
            messagebox.showerror("录入失败", str(exc), parent=self.root)
            return

        self.reload_accounts()
        msg = f"新增 {added}，更新 {updated}，跳过 {skipped}"
        if errors:
            detail = "\n".join(errors[:12])
            if len(errors) > 12:
                detail += f"\n…另有 {len(errors) - 12} 条"
            messagebox.showwarning("批量录入完成（有跳过）", f"{msg}\n\n{detail}", parent=self.root)
        else:
            messagebox.showinfo("批量录入完成", msg, parent=self.root)
        self._set_status(f"批量录入：{msg}")

    @staticmethod
    def _import_accounts_from_xlsx(path: str) -> tuple[int, int, int, list[str]]:
        from openpyxl import load_workbook

        wb = load_workbook(path, read_only=True, data_only=True)
        ws = None
        for name in ("账号", "accounts", "Accounts"):
            if name in wb.sheetnames:
                ws = wb[name]
                break
        if ws is None:
            ws = wb[wb.sheetnames[0]]

        rows = ws.iter_rows(values_only=True)
        try:
            header = next(rows)
        except StopIteration:
            wb.close()
            raise ValueError("Excel 为空")

        def norm(cell: object) -> str:
            if cell is None:
                return ""
            if isinstance(cell, float) and cell.is_integer():
                return str(int(cell))
            if isinstance(cell, int):
                return str(cell)
            return str(cell).strip()

        col_map: dict[str, int] = {}
        aliases = {
            "label": ("备注", "账号备注", "名称", "label", "name"),
            "phone": ("手机号", "手机", "账号", "帐号", "phone", "user", "username"),
            "password": ("密码", "password", "pwd", "pass"),
            "secondary_code": ("二级码", "二级密码", "安全锁", "secondary_code", "sec", "code"),
        }
        for idx, raw in enumerate(header or ()):
            key = norm(raw).lower()
            for field, names in aliases.items():
                if key in {n.lower() for n in names} and field not in col_map:
                    col_map[field] = idx
        if "phone" not in col_map or "password" not in col_map:
            wb.close()
            raise ValueError("表头需包含「手机号」「密码」列（备注/二级码可选）")

        existing_by_phone: dict[str, AccountProfile] = {}
        for acc in load_accounts():
            phone_key = (acc.phone or "").strip()
            if phone_key and phone_key not in existing_by_phone:
                existing_by_phone[phone_key] = acc
        pending: dict[str, tuple[str, str, str, bool]] = {}
        added = updated = skipped = 0
        errors: list[str] = []
        for row_i, row in enumerate(rows, start=2):
            if row is None:
                continue
            cells = list(row)
            phone = norm(cells[col_map["phone"]]) if col_map["phone"] < len(cells) else ""
            password = norm(cells[col_map["password"]]) if col_map["password"] < len(cells) else ""
            label = ""
            if "label" in col_map and col_map["label"] < len(cells):
                label = norm(cells[col_map["label"]])
            sec_raw = ""
            has_sec_col = "secondary_code" in col_map
            if has_sec_col and col_map["secondary_code"] < len(cells):
                sec_raw = norm(cells[col_map["secondary_code"]])
            if not phone and not password and not label and not sec_raw:
                continue
            if not phone or not password:
                skipped += 1
                errors.append(f"第{row_i}行：手机号或密码为空，已跳过")
                continue
            sec = ""
            if has_sec_col:
                sec, err = normalize_secondary_code(sec_raw)
                if err:
                    skipped += 1
                    errors.append(f"第{row_i}行：{err}，已跳过")
                    continue
            pending[phone] = (label, password, sec, has_sec_col)

        for phone, (label, password, sec, has_sec_col) in pending.items():
            label_use = label or phone
            if phone in existing_by_phone:
                acc = existing_by_phone[phone]
                same = (
                    acc.label == label_use
                    and acc.password == password
                    and (not has_sec_col or acc.secondary_code == sec)
                )
                if same:
                    continue
                acc.label = label_use
                acc.password = password
                if has_sec_col:
                    acc.secondary_code = sec
                upsert_account(acc)
                updated += 1
            else:
                acc = AccountProfile.create(
                    label, phone, password, secondary_code=sec if has_sec_col else ""
                )
                upsert_account(acc)
                existing_by_phone[phone] = acc
                added += 1

        wb.close()
        return added, updated, skipped, errors

    def edit_account(self) -> None:
        sel = self.acc_tree.selection()
        if not sel:
            messagebox.showwarning("未选择", "请先选择要修改的账号")
            return
        acc = next((a for a in load_accounts() if a.id == sel[0]), None)
        if acc is None:
            return
        label = simpledialog.askstring("备注", "账号备注（可选）:", initialvalue=acc.label, parent=self.root)
        if label is None:
            return
        phone = simpledialog.askstring("手机号", "手机号:", initialvalue=acc.phone, parent=self.root)
        if not phone:
            return
        password = simpledialog.askstring("密码", "密码:", initialvalue=acc.password, show="*", parent=self.root)
        if password is None:
            return
        sec = self._ask_secondary_code(
            initial=acc.secondary_code or "", title="二级码"
        )
        if sec is None:
            return
        acc.label = label.strip() or phone
        acc.phone = phone.strip()
        acc.password = password
        acc.secondary_code = sec
        try:
            upsert_account(acc)
        except ValueError as exc:
            messagebox.showerror("修改失败", str(exc), parent=self.root)
            return
        self.reload_accounts()

    def batch_edit_secondary_code(self) -> None:
        """批量改二级码：有选中则改选中，否则改全部。"""
        sel = list(self.acc_tree.selection())
        accounts = load_accounts()
        if sel:
            targets = [a for a in accounts if a.id in sel]
            scope = f"选中的 {len(targets)} 个账号"
        else:
            targets = list(accounts)
            scope = f"全部 {len(targets)} 个账号"
        if not targets:
            messagebox.showwarning("无账号", "账号库为空。", parent=self.root)
            return
        sec = self._ask_secondary_code(title="批量改二级码")
        if sec is None:
            return
        tip = "将写入二级码「" + (sec if sec else "（空）") + "」到" + scope + "？"
        if not messagebox.askyesno("确认批量改二级码", tip, parent=self.root):
            return
        n = 0
        for acc in targets:
            acc.secondary_code = sec
            upsert_account(acc)
            n += 1
        self.reload_accounts()
        self._set_status(f"已批量改二级码 {n} 个账号")
        messagebox.showinfo("完成", f"已更新 {n} 个账号的二级码。", parent=self.root)

    def remove_account(self) -> None:
        sel = self.acc_tree.selection()
        if not sel:
            messagebox.showwarning("未选择", "请先选择要删除的账号")
            return
        acc = next((a for a in load_accounts() if a.id == sel[0]), None)
        name = acc.label if acc else sel[0]
        if messagebox.askyesno("确认", f"删除账号「{name}」？"):
            delete_account(sel[0])
            self.reload_accounts()

    def launch_selected_account(self) -> None:
        sel = self.acc_tree.selection()
        if not sel:
            messagebox.showwarning("未选择", "请先选择账号")
            return
        if not self._warn_if_bridge_missing():
            return
        accounts = load_accounts()
        by_id = {a.id: a for a in accounts}
        chosen = [by_id[i] for i in sel if i in by_id]
        if not chosen:
            return
        self._launch_staggered(chosen, "启动选中")

    def launch_all_accounts(self) -> None:
        accounts = load_accounts()
        if not accounts:
            messagebox.showwarning("无账号", "账号库为空，请先录入账号。")
            return
        if not self._warn_if_bridge_missing():
            return
        self._launch_staggered(accounts, "一键启动所有")

    # --- 逐个启动（间隔 10 秒，避免同时初始化黑屏） ---

    LAUNCH_GAP_SEC = 10

    def _launch_staggered(self, accounts: list[AccountProfile], label: str) -> None:
        """按账号库顺序逐个启动，每个间隔 LAUNCH_GAP_SEC 秒，避免多开同时初始化黑屏。"""
        tile = bool(self.auto_tile_var.get())
        chosen = list(accounts)
        if tile and len(chosen) > TILE_MAX_WINDOWS:
            extra = len(chosen) - TILE_MAX_WINDOWS
            if not messagebox.askyesno(
                "自动分窗",
                f"自动分窗一次最多开 {TILE_MAX_WINDOWS} 个窗口。\n"
                f"当前选了 {len(chosen)} 个，将只启动前 {TILE_MAX_WINDOWS} 个（跳过 {extra} 个）。\n\n继续？",
            ):
                return
            chosen = chosen[:TILE_MAX_WINDOWS]
        threading.Thread(
            target=self._launch_staggered_worker,
            args=(chosen, label, tile),
            daemon=True,
        ).start()

    def _place_tiled_window(self, pid: int, slot: int, name: str) -> None:
        x, y = _tile_xy(slot)
        hwnd = _wait_hwnd_for_pid(pid)
        if not hwnd:
            self._set_status(f"[{name}] 窗口未出现，跳过分窗 ({x},{y})")
            return
        moved = _move_window(hwnd, x, y)
        # Unity 启动画面关掉后会换主窗口，再摆几次以免弹回默认位置
        for _ in range(4):
            time.sleep(1.2)
            again = _find_hwnd_for_pid(pid)
            if again:
                hwnd = again
                moved = _move_window(hwnd, x, y) or moved
        if moved:
            self._set_status(f"[{name}] 已分窗到 ({x},{y})")
        else:
            self._set_status(f"[{name}] 分窗失败 ({x},{y})")

    def _launch_staggered_worker(
        self, accounts: list[AccountProfile], label: str, tile: bool
    ) -> None:
        ok = 0
        errors: list[str] = []
        total = len(accounts)
        for idx, acc in enumerate(accounts, start=1):
            name = acc.label or acc.phone
            try:
                inst = launch_game(self._game_root())
                self.instances.append(inst)
                threading.Thread(
                    target=self._auto_workflow,
                    args=(inst.instance_id, acc.phone, acc.password, name),
                    daemon=True,
                ).start()
                ok += 1
                self._set_status(f"{label}进行中：{name}（{idx}/{total}）已启动")
                if tile:
                    self._place_tiled_window(inst.pid, idx - 1, name)
            except Exception as exc:
                errors.append(f"{name}: {exc}")

            if idx < total:
                self._set_status(f"{label}进行中：{idx}/{total} 已启动，{self.LAUNCH_GAP_SEC}秒后启动下一个…")
                time.sleep(self.LAUNCH_GAP_SEC)

        tile_part = "，已自动分窗" if tile else ""
        summary = f"{label}完成：成功 {ok}/{total}（已自动进入登录→拉多控→召唤流程，逐个间隔{self.LAUNCH_GAP_SEC}秒{tile_part}）"
        if errors:
            summary += "\n" + "\n".join(errors[:10])
        self._set_status(summary)
        if errors or label == "一键启动所有":
            self.root.after(0, lambda: messagebox.showwarning(label, summary, parent=self.root))

    def _auto_workflow(self, instance_id: str, phone: str, password: str, label: str) -> None:
        """启动后后台自动走精简桥接 workflow_step1：等桥接→登录→进游戏→拉多控→一键召唤。"""
        try:
            self._set_status(f"[{label}] 等待精简桥接连接…")
            if not ipc.wait_for_bridge(instance_id, timeout=240):
                self._set_status(f"[{label}] 桥接连接超时（未注入精简桥接？）")
                return
            self._set_status(f"[{label}] 桥接已连接，自动登录/开助手/拉多控/召唤…")
            ipc.workflow_step1_five_chars(
                instance_id,
                phone,
                password,
                open_helper=self._want_auto_open_helper(),
            )
            ok, msg = ipc.wait_workflow_done(instance_id, timeout=600)
            if ok:
                self._set_status(f"[{label}] 流程完成")
                self.root.after(0, lambda iid=instance_id: self.monitor.watch_once(iid))
            else:
                self._set_status(f"[{label}] 流程失败: {msg}")
        except Exception as exc:
            self._set_status(f"[{label}] 异常: {type(exc).__name__}: {exc}")

    # --- 批量一键（协议驱动，靠 bridge 命令与 team/multi_ready 判定） ---

    def _live_bridge_instances(self) -> list[str]:
        """返回当前有桥接心跳的实例 ID（按 PID 稳定排序）。"""
        rows = ipc.list_instance_snapshots()
        live = [r for r in rows if r.get("alive")]
        live.sort(key=lambda r: r.get("pid_txt") or 0)
        return [r["instance_id"] for r in live]

    def _set_cmd_result(self, text: str) -> None:
        box = getattr(self, "cmd_result", None)
        if box is None:
            return
        box.config(state=tk.NORMAL)
        box.delete("1.0", tk.END)
        box.insert(tk.END, text)
        box.config(state=tk.DISABLED)

    def _cmd_targets(self) -> list[tuple[str, str]]:
        """已开启监控、在线且已进游戏的窗口：(instance_id, 角色名)。"""
        watch = set(getattr(self.monitor, "_watch", set()) or set())
        rows = [r for r in ipc.list_instance_snapshots() if r.get("alive")]
        targets = []
        for row in rows:
            if row["instance_id"] not in watch:
                continue
            st = row.get("state") or {}
            if st.get("phase") != "in_game":
                continue
            overview = str(st.get("overview") or "")
            name = ""
            lines = overview.splitlines()
            for i, line in enumerate(lines):
                if line.strip() == "【队长】" and i + 1 < len(lines) and lines[i + 1].startswith("名称:"):
                    name = lines[i + 1].split(":", 1)[-1].strip()
                    break
            targets.append((row["instance_id"], name or row["instance_id"]))
        return targets

    def close_watched(self) -> None:
        """正在运行的监控窗口先一键断线，5 秒后关掉这些游戏窗口。"""
        watch = set(getattr(self.monitor, "_watch", set()) or set())
        running = {pid for pid, _exe in find_game_processes()}
        seen: set[int] = set()
        rows = []
        for row in ipc.list_instance_snapshots():
            if row.get("instance_id") not in watch:
                continue
            pid = int(row.get("pid_txt") or 0)
            if pid <= 0 or pid not in running or pid in seen:
                continue
            seen.add(pid)
            rows.append(row)
        if not rows:
            messagebox.showwarning("没有窗口", "没有正在运行的监控窗口。", parent=self.root)
            return
        if not messagebox.askyesno(
            "一键关闭",
            f"给 {len(rows)} 个正在运行的监控窗口发一键断线，5 秒后关闭这些窗口。继续？",
            parent=self.root,
        ):
            return

        self._cmd_gen = getattr(self, "_cmd_gen", 0) + 1
        self._set_cmd_result("正在给 " + str(len(rows)) + " 个监控窗口发一键断线…")

        def work() -> None:
            lines_out = []
            targets = []
            for row in rows:
                iid = row["instance_id"]
                pid = int(row.get("pid_txt") or 0)
                name = _snapshot_caption(row)
                targets.append((iid, pid, name))
                if not row.get("alive"):
                    lines_out.append("失败 " + name + "：桥接不在线，仍会在 5 秒后关闭窗口")
                    continue
                req = ipc.send_command(iid, "disconnect")
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                if not msg:
                    msg = "无应答" if not ok else "已断线"
                mark = "成功 " if ok and msg.startswith("已断线") else "失败 "
                lines_out.append(mark + name + "：" + msg)

            def paint_wait() -> None:
                self._set_cmd_result("\n".join(lines_out) + "\n\n5 秒后关闭窗口…")

            self.root.after(0, paint_wait)
            time.sleep(5)
            closed_ids = []
            close_lines = []
            for iid, pid, name in targets:
                gone, detail = _kill_game_pid(pid)
                close_lines.append(name + "：" + detail)
                if gone:
                    closed_ids.append(iid)

            def paint_done() -> None:
                if closed_ids:
                    self.monitor.unwatch(closed_ids)
                self._set_cmd_result("\n".join(lines_out + [""] + close_lines))

            self.root.after(0, paint_done)

        threading.Thread(target=work, name="close-watched", daemon=True).start()

    def _parse_layer(self, var: tk.StringVar, label: str) -> int | None:
        raw = str(var.get() or "").strip()
        try:
            layer = int(raw)
        except ValueError:
            layer = 0
        if layer < 1:
            messagebox.showwarning("层数无效", label + "必须是大于 0 的整数。", parent=self.root)
            return None
        return layer

    def _parse_cmd_delay(self) -> tuple[float, float]:
        """返回 (基础秒, 浮动秒)，非法输入回落默认 3 / 5。"""
        try:
            base = float(str(self.cmd_delay_base_var.get() or "").strip())
        except ValueError:
            base = 3.0
        try:
            span = float(str(self.cmd_delay_float_var.get() or "").strip())
        except ValueError:
            span = 5.0
        if base < 0:
            base = 0.0
        if span < 0:
            span = 0.0
        return base, span

    def _save_cmd_delay(self) -> None:
        base, span = self._parse_cmd_delay()
        cfg = load_settings()
        cfg["central_cmd_delay_base"] = base
        cfg["central_cmd_delay_float"] = span
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _cmd_gap_seconds(self) -> float:
        base, span = self._parse_cmd_delay()
        if span <= 0:
            return base
        return random.uniform(base, base + span)

    def _broadcast_script(
        self,
        title: str,
        cmd: str,
        params: dict | None = None,
        instant: bool = False,
    ) -> None:
        """下发指令。已启动记执行中，再等脚本最终结果。instant 为当场成功或失败，不再等待。"""
        targets = self._cmd_targets()
        if not targets:
            messagebox.showwarning("没有窗口", "没有已开启监控、且已进游戏的窗口。", parent=self.root)
            return

        self._save_cmd_delay()
        base, span = self._parse_cmd_delay()
        self._cmd_gen = getattr(self, "_cmd_gen", 0) + 1
        gen = self._cmd_gen
        gap_hint = f"{base:g}～{base + span:g}" if span > 0 else f"{base:g}"
        self._set_cmd_result(
            "正在下发" + title + "，共 " + str(len(targets))
            + " 个窗口，间隔 " + gap_hint + " 秒…"
        )

        def work() -> None:
            lines_out = []
            waiting = []

            def paint_partial() -> None:
                if gen != self._cmd_gen:
                    return
                self._set_cmd_result("\n".join(lines_out) if lines_out else "…")

            def paint() -> None:
                if gen != self._cmd_gen:
                    return
                self._set_cmd_result("\n".join(lines_out))

            for i, (iid, name) in enumerate(targets):
                if i > 0:
                    gap = self._cmd_gap_seconds()
                    lines_out.append("间隔 " + f"{gap:.1f}" + " 秒后再发 " + name + "…")
                    self.root.after(0, paint_partial)
                    time.sleep(gap)
                    if lines_out and lines_out[-1].startswith("间隔 "):
                        lines_out.pop()
                before = ipc.read_state(iid) or {}
                before_unix = int(before.get("ctrl_unix") or 0)
                before_msg = str(before.get("ctrl_msg") or "").strip()
                req = ipc.send_command(iid, cmd, **(params or {}))
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                if not msg:
                    msg = "无应答" if not ok else "已启动"
                if instant:
                    # 桥接以「已…」开头表示当场成功（已打开/已忽略/已加点/已打卡/已开始遇敌 等）
                    good = ok and msg.startswith("已")
                    lines_out.append(("成功 " if good else "失败 ") + name + "：" + msg)
                    self.root.after(0, paint_partial)
                    continue
                started = ok and msg.startswith("已启动")
                if started:
                    lines_out.append("执行中 " + name + "：" + msg)
                    waiting.append({
                        "iid": iid,
                        "name": name,
                        "before": before_unix,
                        "before_msg": before_msg,
                        "idx": len(lines_out) - 1,
                        "miss": 0,
                    })
                else:
                    lines_out.append("失败 " + name + "：" + msg)
                self.root.after(0, paint_partial)

            self.root.after(0, paint)
            while waiting:
                if gen != self._cmd_gen:
                    return
                time.sleep(1.0)
                still = []
                changed = False
                for item in waiting:
                    iid = item["iid"]
                    if not ipc.bridge_alive(iid):
                        item["miss"] = int(item["miss"]) + 1
                        if item["miss"] >= 20:
                            lines_out[item["idx"]] = "失败 " + item["name"] + "：窗口离线"
                            changed = True
                            continue
                        still.append(item)
                        continue
                    item["miss"] = 0
                    st = ipc.read_state(iid) or {}
                    unix = int(st.get("ctrl_unix") or 0)
                    cmsg = str(st.get("ctrl_msg") or "").strip()
                    newer = unix > item["before"] or (
                        unix == item["before"] and cmsg != item["before_msg"]
                    )
                    if newer and cmsg and not cmsg.startswith("进行中"):
                        mark = "成功 " if st.get("ctrl_ok") else "失败 "
                        lines_out[item["idx"]] = mark + item["name"] + "：" + cmsg
                        changed = True
                    else:
                        still.append(item)
                waiting = still
                if changed:
                    self.root.after(0, paint)

        threading.Thread(target=work, name="cmd-" + cmd, daemon=True).start()

    def _selected_cmd_key(self) -> str:
        sel = self.cmd_list.curselection()
        if not sel:
            return ""
        idx = int(sel[0])
        if idx < 0 or idx >= len(self._cmd_catalog):
            return ""
        return self._cmd_catalog[idx][0]

    def _on_cmd_select(self) -> None:
        key = self._selected_cmd_key()
        if key == "dojo":
            self.cmd_param_label.configure(text="百人层数")
            self.cmd_layer_entry.configure(textvariable=self.dojo_layer_var)
            if not self.cmd_param.winfo_ismapped():
                self.cmd_param.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
            return
        if key == "hell":
            self.cmd_param_label.configure(text="噩梦层数")
            self.cmd_layer_entry.configure(textvariable=self.hell_layer_var)
            if not self.cmd_param.winfo_ismapped():
                self.cmd_param.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
            return
        if key == "crystal":
            self.cmd_param_label.configure(text="难度(1-6)")
            self.cmd_layer_entry.configure(textvariable=self.crystal_diff_var)
            if not self.cmd_param.winfo_ismapped():
                self.cmd_param.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
            return
        if key == "item_transfer":
            self.cmd_param_label.configure(text="道具名(含)")
            self.cmd_layer_entry.configure(textvariable=self.item_transfer_keyword_var)
            if not self.cmd_param.winfo_ismapped():
                self.cmd_param.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
            return
        if self.cmd_param.winfo_ismapped():
            self.cmd_param.pack_forget()

    def _start_selected_cmd(self) -> None:
        key = self._selected_cmd_key()
        if key == "money":
            self.broadcast_money_farm()
        elif key == "daily":
            self.broadcast_daily()
        elif key == "junk_drop":
            self.broadcast_junk_drop()
        elif key == "dojo":
            self.broadcast_dojo()
        elif key == "hell":
            self.broadcast_dojo_hell()
        elif key == "crystal":
            self.broadcast_crystal_challenge()
        elif key == "flora":
            self.broadcast_flora_heal()
        elif key == "auto_point":
            self.broadcast_auto_point()
        elif key == "brawl":
            self.broadcast_brawl_sign()
        elif key == "item_transfer":
            self.broadcast_item_transfer()
        elif key == "start_enc":
            self.broadcast_start_encounter()
        elif key == "stop_enc":
            self.broadcast_stop_encounter()
        elif key == "fast_punch":
            self.broadcast_fast_punch()
        elif key == "save":
            self.broadcast_save_gold()
        elif key == "skip":
            self.broadcast_skip_anim()
        elif key == "close":
            self.close_watched()
        else:
            messagebox.showwarning("未选择", "请先在左侧选择一条指令。", parent=self.root)

    def broadcast_money_farm(self) -> None:
        self._broadcast_script("自动打钱", "money_farm")

    def broadcast_daily(self) -> None:
        self._broadcast_script("做日常", "daily")

    def broadcast_junk_drop(self) -> None:
        self._broadcast_script("一键丢弃道具", "junk_drop")

    def broadcast_dojo(self) -> None:
        layer = self._parse_layer(self.dojo_layer_var, "百人层数")
        if layer is None:
            return
        self._broadcast_script("百人", "dojo_run", {"layer": layer})

    def broadcast_dojo_hell(self) -> None:
        layer = self._parse_layer(self.hell_layer_var, "噩梦层数")
        if layer is None:
            return
        self._broadcast_script("噩梦百人", "dojo_hell", {"layer": layer})

    def broadcast_crystal_challenge(self) -> None:
        raw = str(self.crystal_diff_var.get() or "").strip()
        try:
            difficulty = int(raw)
        except ValueError:
            difficulty = 0
        if difficulty < 1 or difficulty > 6:
            messagebox.showwarning(
                "难度无效",
                "难度必须是 1 到 6 的整数（2 = 60级）。",
                parent=self.root,
            )
            return
        self._broadcast_script(
            "自动打挑战",
            "crystal_challenge",
            {"difficulty": difficulty},
        )

    def broadcast_flora_heal(self) -> None:
        self._broadcast_script("治疗回城", "flora_heal")

    def broadcast_auto_point(self) -> None:
        self._broadcast_script("一键加点", "auto_point", instant=True)

    def broadcast_brawl_sign(self) -> None:
        self._broadcast_script("自动报名大乱斗", "brawl_sign")

    def broadcast_item_transfer(self) -> None:
        keyword = (self.item_transfer_keyword_var.get() or "").strip() or "五仁"
        self.item_transfer_keyword_var.set(keyword)
        cfg = load_settings()
        cfg["central_item_transfer_keyword"] = keyword
        try:
            save_settings(cfg)
        except OSError:
            pass
        self._broadcast_script(
            "道具传递", "item_transfer", {"keyword": keyword}
        )

    def _build_trade_tab(self, tab: ttk.Frame) -> None:
        ttk.Label(
            tab,
            text=(
                "交易：未勾选锁定坐标时，交易对象回城点2→1000(65,73)；被交易对象（「一」）已在该点则不再回城。"
                "勾选锁定坐标后，以被交易对象坐标为准：被交易留原地，各交易对象走过去（已重合则直接开始）。"
                "交易对象可多个排队；被交易对象仅一个。"
                "每轮实际交格=min(给出方可交, 接收空格, 20)，空了/满了都会正常结束，不会卡死。"
                "全部交易：按列表顺序一个个搬完（含超银）或直到接收方满包。"
                "排队蓝底，完成白底。"
            ),
            foreground="#666",
            wraplength=900,
        ).pack(anchor=tk.W, pady=(4, 8))

        frm = ttk.LabelFrame(tab, text="交易", padding=10)
        frm.pack(fill=tk.BOTH, expand=False, pady=(0, 8))

        row1 = ttk.Frame(frm)
        row1.pack(fill=tk.X, pady=2)
        ttk.Label(row1, text="道具名称(含)", width=14).pack(side=tk.LEFT)
        ttk.Entry(row1, textvariable=self.trade_keyword_var, width=24).pack(
            side=tk.LEFT, padx=(0, 16)
        )
        ttk.Label(row1, text="超银堆叠", width=10).pack(side=tk.LEFT)
        ttk.Entry(row1, textvariable=self.trade_stack_var, width=6).pack(side=tk.LEFT)
        ttk.Label(row1, text="(每格上限，总取=空格×堆叠)", foreground="#666").pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Label(
            row1, text="(每格最多取出数量，默认20)", foreground="#888"
        ).pack(side=tk.LEFT, padx=(6, 0))

        mid = ttk.Frame(frm)
        mid.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        left = ttk.Frame(mid)
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        ttk.Label(left, text="交易对象（可多个，排队顺序）").pack(anchor=tk.W)
        list_wrap = ttk.Frame(left)
        list_wrap.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.trade_sender_list = tk.Listbox(
            list_wrap,
            height=8,
            exportselection=False,
            activestyle="dotbox",
            font=("Microsoft YaHei UI", 10),
            selectmode=tk.EXTENDED,
        )
        sb = ttk.Scrollbar(list_wrap, orient=tk.VERTICAL, command=self.trade_sender_list.yview)
        self.trade_sender_list.configure(yscrollcommand=sb.set)
        self.trade_sender_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sb.pack(side=tk.RIGHT, fill=tk.Y)

        add_row = ttk.Frame(left)
        add_row.pack(fill=tk.X, pady=(6, 0))
        self.trade_add_sender_cb = ttk.Combobox(
            add_row, textvariable=self.trade_add_sender_var, width=40, state="readonly"
        )
        self.trade_add_sender_cb.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(add_row, text="加入", command=self._trade_add_sender, width=6).pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Button(
            add_row, text="一键加入", command=self._trade_add_all_watched_senders, width=8
        ).pack(side=tk.LEFT, padx=(4, 0))
        ttk.Button(add_row, text="移除", command=self._trade_remove_senders, width=6).pack(
            side=tk.LEFT, padx=(4, 0)
        )

        opt_row = ttk.Frame(left)
        opt_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Checkbutton(
            opt_row, text="交易对象从超银取", variable=self.trade_from_bank_var
        ).pack(side=tk.LEFT)

        right = ttk.Frame(mid, padding=(16, 0, 0, 0))
        right.pack(side=tk.LEFT, fill=tk.Y)
        ttk.Label(right, text="被交易对象（单个）").pack(anchor=tk.W)
        self.trade_receiver_cb = ttk.Combobox(
            right, textvariable=self.trade_receiver_var, width=36, state="readonly"
        )
        self.trade_receiver_cb.pack(anchor=tk.W, pady=(4, 0))
        ttk.Checkbutton(
            right, text="存入超银", variable=self.trade_to_bank_var
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            right,
            text="锁定坐标",
            variable=self.trade_lock_pos_var,
        ).pack(anchor=tk.W, pady=(4, 0))
        ttk.Label(
            right,
            text="勾选后以被交易对象（「一」）坐标为准：交易对象走过去。",
            foreground="#888",
            wraplength=260,
            justify=tk.LEFT,
        ).pack(anchor=tk.W, pady=(2, 0))
        ttk.Label(
            right,
            text="蓝=排队等待\n白=已跑完\n黄=正在交易",
            foreground="#888",
            justify=tk.LEFT,
        ).pack(anchor=tk.W, pady=(12, 0))

        btn_row = ttk.Frame(frm)
        btn_row.pack(fill=tk.X, pady=(10, 0))
        ttk.Button(
            btn_row, text="刷新窗口列表", command=self._refresh_trade_windows, width=12
        ).pack(side=tk.LEFT)
        ttk.Button(
            btn_row, text="开始单次交易", command=self.start_single_trade, width=12
        ).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(
            btn_row, text="全部交易", command=self.start_all_trades, width=10
        ).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(
            btn_row, text="一键停止", command=self.stop_single_trade, width=10
        ).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Label(
            btn_row, text="回城点2→1000(65,73)", foreground="#888"
        ).pack(side=tk.LEFT, padx=(16, 0))

        ttk.Label(tab, text="日志").pack(anchor=tk.W, pady=(8, 0))
        self.trade_result = tk.Text(tab, wrap=tk.WORD, state=tk.DISABLED, height=14)
        self.trade_result.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self._set_trade_result(
            "尚未开始。刷新后把窗口加入交易对象列表，选好被交易对象，再单次或全部交易。"
        )
        self._refresh_trade_windows()

    def _set_trade_result(self, text: str) -> None:
        w = getattr(self, "trade_result", None)
        if w is None:
            return
        w.configure(state=tk.NORMAL)
        w.delete("1.0", tk.END)
        w.insert(tk.END, text or "")
        w.configure(state=tk.DISABLED)

    def _trade_paint_sender_row(self, index: int, status: str) -> None:
        """status: wait|run|done|idle"""
        lb = getattr(self, "trade_sender_list", None)
        if lb is None or index < 0 or index >= lb.size():
            return
        colors = {
            "wait": "#5DADE2",
            "run": "#F4D03F",
            "done": "#FFFFFF",
            "idle": "#FFFFFF",
            "fail": "#F5B7B1",
        }
        bg = colors.get(status, "#FFFFFF")
        fg = "#1A1A1A" if status != "wait" else "#0B3040"

        def apply() -> None:
            try:
                lb.itemconfigure(index, bg=bg, fg=fg)
            except tk.TclError:
                pass

        self.root.after(0, apply)

    def _trade_reset_sender_colors(self, mode: str = "wait") -> None:
        for i in range(len(self._trade_sender_entries)):
            self._trade_paint_sender_row(i, mode)

    def _refresh_trade_windows(self) -> None:
        labels: list[str] = []
        mapping: dict[str, str] = {}
        try:
            snaps = ipc.list_instance_snapshots()
        except Exception:
            snaps = []
        for s in snaps:
            iid = str(s.get("instance_id") or "")
            if not iid or not s.get("alive"):
                continue
            st = s.get("state") or {}
            if not isinstance(st, dict):
                st = {}
            name = str(st.get("name") or "").strip() or iid
            floor = st.get("floor") or 0
            x = st.get("x") or 0
            y = st.get("y") or 0
            label = f"{name}  [{iid}]  {floor}({x},{y})"
            labels.append(label)
            mapping[label] = iid
        self._trade_label_to_iid = mapping
        self.trade_add_sender_cb["values"] = labels
        self.trade_receiver_cb["values"] = labels
        if self.trade_add_sender_var.get() not in mapping:
            self.trade_add_sender_var.set(labels[0] if labels else "")
        if self.trade_receiver_var.get() not in mapping:
            # 默认不选已在交易对象列表里的
            used = {e["iid"] for e in self._trade_sender_entries}
            pick = ""
            for lab in labels:
                iid = mapping.get(lab, "")
                if iid and iid not in used:
                    pick = lab
                    break
            self.trade_receiver_var.set(pick or (labels[0] if labels else ""))
        # 刷新已加入列表的显示名（坐标可能变）
        self._trade_rebuild_sender_listbox(keep_colors=True)

        # 分发页共用同一窗口列表
        if getattr(self, "dist_add_recv_cb", None) is not None:
            self.dist_add_recv_cb["values"] = labels
            self.dist_sender_cb["values"] = labels
            if self.dist_add_recv_var.get() not in mapping:
                self.dist_add_recv_var.set(labels[0] if labels else "")
            if self.dist_sender_var.get() not in mapping:
                used_r = {e["iid"] for e in self._dist_recv_entries}
                pick_s = ""
                for lab in labels:
                    iid = mapping.get(lab, "")
                    if iid and iid not in used_r:
                        pick_s = lab
                        break
                self.dist_sender_var.set(pick_s or (labels[0] if labels else ""))
            self._dist_rebuild_recv_listbox(keep_colors=True)

    def _trade_rebuild_sender_listbox(self, keep_colors: bool = False) -> None:
        lb = self.trade_sender_list
        old_colors = []
        if keep_colors:
            for i in range(lb.size()):
                try:
                    old_colors.append(lb.itemcget(i, "bg"))
                except tk.TclError:
                    old_colors.append("#FFFFFF")
        lb.delete(0, tk.END)
        for e in self._trade_sender_entries:
            iid = e["iid"]
            st = ipc.read_state(iid) or {}
            name = str(st.get("name") or e.get("name") or iid)
            e["name"] = name
            floor = st.get("floor") or 0
            x = st.get("x") or 0
            y = st.get("y") or 0
            label = f"{name}  [{iid}]  {floor}({x},{y})"
            e["label"] = label
            lb.insert(tk.END, label)
        for i, bg in enumerate(old_colors):
            if i < lb.size():
                try:
                    lb.itemconfigure(i, bg=bg or "#FFFFFF")
                except tk.TclError:
                    pass

    def _trade_add_sender(self) -> None:
        lab = (self.trade_add_sender_var.get() or "").strip()
        iid = self._trade_label_to_iid.get(lab, "")
        if not iid:
            messagebox.showwarning("交易", "请先刷新并选择要加入的窗口。", parent=self.root)
            return
        recv_lab = (self.trade_receiver_var.get() or "").strip()
        recv_iid = self._trade_label_to_iid.get(recv_lab, "")
        if recv_iid and iid == recv_iid:
            messagebox.showwarning(
                "交易", "不能把被交易对象加入交易对象列表。", parent=self.root
            )
            return
        if any(e["iid"] == iid for e in self._trade_sender_entries):
            messagebox.showinfo("交易", "该窗口已在交易对象列表中。", parent=self.root)
            return
        st = ipc.read_state(iid) or {}
        name = str(st.get("name") or iid)
        self._trade_sender_entries.append({"iid": iid, "label": lab, "name": name})
        self.trade_sender_list.insert(tk.END, lab)
        idx = self.trade_sender_list.size() - 1
        self._trade_paint_sender_row(idx, "idle")

    def _trade_add_all_watched_senders(self) -> None:
        """一键加入：所有已开启监控的在线窗口，排除被交易对象和已在列表中的。"""
        self._refresh_trade_windows()
        watch = set(getattr(self.monitor, "_watch", set()) or set())
        recv_lab = (self.trade_receiver_var.get() or "").strip()
        recv_iid = self._trade_label_to_iid.get(recv_lab, "")
        have = {e["iid"] for e in self._trade_sender_entries}
        added = 0
        for lab, iid in list(self._trade_label_to_iid.items()):
            if not iid or iid not in watch:
                continue
            if recv_iid and iid == recv_iid:
                continue
            if iid in have:
                continue
            st = ipc.read_state(iid) or {}
            name = str(st.get("name") or iid)
            self._trade_sender_entries.append({"iid": iid, "label": lab, "name": name})
            self.trade_sender_list.insert(tk.END, lab)
            self._trade_paint_sender_row(self.trade_sender_list.size() - 1, "idle")
            have.add(iid)
            added += 1
        if added <= 0:
            messagebox.showinfo(
                "交易",
                "没有可加入的监控窗口（已排除被交易对象和列表中已有的）。",
                parent=self.root,
            )

    def _trade_remove_senders(self) -> None:
        sel = list(self.trade_sender_list.curselection())
        if not sel:
            messagebox.showinfo("交易", "请先在列表中选中要移除的交易对象。", parent=self.root)
            return
        for i in reversed(sel):
            if 0 <= i < len(self._trade_sender_entries):
                self._trade_sender_entries.pop(i)
            self.trade_sender_list.delete(i)

    def _save_trade_settings(self) -> None:
        cfg = load_settings()
        cfg["central_trade_keyword"] = (self.trade_keyword_var.get() or "").strip() or "五仁"
        try:
            cfg["central_trade_stack"] = max(
                1, int(str(self.trade_stack_var.get() or "20").strip())
            )
        except ValueError:
            cfg["central_trade_stack"] = 20
        cfg["central_trade_from_bank"] = bool(self.trade_from_bank_var.get())
        cfg["central_trade_to_bank"] = bool(self.trade_to_bank_var.get())
        cfg["central_trade_lock_pos"] = bool(self.trade_lock_pos_var.get())
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _trade_resolve_receiver(self) -> tuple[str, str] | None:
        lab_b = (self.trade_receiver_var.get() or "").strip()
        b_iid = self._trade_label_to_iid.get(lab_b, "")
        if not b_iid:
            messagebox.showwarning("交易", "请先选择被交易对象。", parent=self.root)
            return None
        st_b = ipc.read_state(b_iid) or {}
        name_b = str(st_b.get("name") or b_iid)
        return b_iid, name_b

    def _trade_params(self) -> tuple[str, int, bool, bool] | None:
        keyword = (self.trade_keyword_var.get() or "").strip() or "五仁"
        self.trade_keyword_var.set(keyword)
        try:
            stack = max(1, int(str(self.trade_stack_var.get() or "20").strip()))
        except ValueError:
            stack = 20
            self.trade_stack_var.set("20")
        from_bank = bool(self.trade_from_bank_var.get())
        to_bank = bool(self.trade_to_bank_var.get())
        self._save_trade_settings()
        return keyword, stack, from_bank, to_bank

    def _wait_ctrl_pair(
        self,
        waiting: list[dict],
        lines: list[str],
        paint,
        gen: int,
        gen_attr: str,
        timeout: float = 120.0,
    ) -> list[dict]:
        """等待 ctrl 结果。返回已结束项（含 ok/msg）；超时项 ok=False。取消返回空列表。"""
        done: list[dict] = []
        deadline = time.time() + timeout
        while waiting and time.time() < deadline:
            if gen != getattr(self, gen_attr, 0):
                return []
            time.sleep(1.0)
            still = []
            changed = False
            for item in waiting:
                iid = item["iid"]
                if not ipc.bridge_alive(iid):
                    item["miss"] = int(item["miss"]) + 1
                    if item["miss"] >= 20:
                        lines[item["idx"]] = "失败 " + item["name"] + "：窗口离线"
                        item["ok"] = False
                        item["msg"] = "窗口离线"
                        done.append(item)
                        changed = True
                        continue
                    still.append(item)
                    continue
                item["miss"] = 0
                st = ipc.read_state(iid) or {}
                unix = int(st.get("ctrl_unix") or 0)
                cmsg = str(st.get("ctrl_msg") or "").strip()
                if unix <= int(item["before"]) or not cmsg:
                    still.append(item)
                    continue
                if cmsg.startswith("进行中"):
                    still.append(item)
                    continue
                cok = bool(st.get("ctrl_ok"))
                lines[item["idx"]] = (
                    ("成功 " if cok else "失败 ") + item["name"] + "：" + cmsg
                )
                item["ok"] = cok
                item["msg"] = cmsg
                done.append(item)
                changed = True
            waiting[:] = still
            if changed:
                self.root.after(0, paint)
        for item in waiting:
            lines[item["idx"]] = "失败 " + item["name"] + "：等待结果超时"
            item["ok"] = False
            item["msg"] = "等待结果超时"
            done.append(item)
        waiting.clear()
        self.root.after(0, paint)
        return done

    def _trade_uid_from_state(self, st: dict | None) -> str:
        """交易用本窗角色 uid：优先 main_uid（勿用队伍队长，否则接收匹配失败）。"""
        if not isinstance(st, dict):
            return ""
        return str(st.get("main_uid") or st.get("captain_uid") or "").strip()

    def _resolve_trade_secondary_code(self, iid: str) -> tuple[str | None, str]:
        """从实例 state.phone 查账号库二级码。可空；非空须 6 位。失败 (None, 原因)。"""
        st = ipc.read_state(iid) or {}
        phone = str(st.get("phone") or "").strip()
        if not phone:
            return None, "无法读取登录手机号（请确认助手/监视已上报 phone）"
        acc = find_account_by_phone(phone)
        if acc is None:
            return None, "账号库无此手机号 " + phone
        code = (acc.secondary_code or "").strip()
        if not code:
            # 空码交给游戏侧：仅当 CurrSecurityCodeFlag 需要验证时才失败
            return "", ""
        code2, err = normalize_secondary_code(code)
        if err:
            return None, err
        return code2, ""

    def _start_and_wait(
        self,
        iid: str,
        name: str,
        cmd: str,
        lines: list[str],
        paint,
        gen: int,
        *,
        timeout: float = 120.0,
        gen_attr: str = "_trade_gen",
        **params,
    ) -> tuple[bool, str]:
        before = ipc.read_state(iid) or {}
        before_unix = int(before.get("ctrl_unix") or 0)
        req = ipc.send_command(iid, cmd, **params)
        ok, ack = ipc.wait_ack(iid, req, timeout=8)
        msg = str((ack or {}).get("msg") or "")
        if not ok or not (
            msg.startswith("已启动")
            or msg.startswith("已探测")
            or msg.startswith("已存")
            or msg.startswith("已整理")
            or msg.startswith("已验证")
        ):
            lines.append("失败 " + name + " " + cmd + "：" + (msg or "无应答"))
            self.root.after(0, paint)
            return False, msg
        if (
            msg.startswith("已探测")
            or msg.startswith("已存")
            or msg.startswith("已整理")
            or msg.startswith("已验证")
        ):
            lines.append(name + "：" + msg)
            self.root.after(0, paint)
            return True, msg
        lines.append("执行中 " + name + "：" + msg)
        idx = len(lines) - 1
        self.root.after(0, paint)
        waiting = [
            {
                "iid": iid,
                "name": name,
                "before": before_unix,
                "idx": idx,
                "miss": 0,
                "ok": False,
                "msg": "",
            }
        ]
        done = self._wait_ctrl_pair(
            waiting, lines, paint, gen, gen_attr, timeout=timeout
        )
        if not done:
            return False, "已取消"
        item = done[0]
        return bool(item.get("ok")), str(item.get("msg") or "")

    def _trade_gather_lock(
        self,
        a_iid: str,
        name_a: str,
        b_iid: str,
        name_b: str,
        lines: list[str],
        paint,
        gen: int,
        *,
        gen_attr: str = "_trade_gen",
    ) -> str:
        """stay(b) 留在原地，mover(a) 走到其当前坐标。已重合则跳过寻路。
        约定：b 永远是「一」那侧（多对一=被交易；一对多=分发者）。"""
        st_b = ipc.read_state(b_iid) or {}
        try:
            fl = int(st_b.get("floor") or 0)
            xx = int(st_b.get("x") or 0)
            yy = int(st_b.get("y") or 0)
        except (TypeError, ValueError):
            fl = xx = yy = 0
        if fl <= 0:
            lines.append("失败：读不到「一」侧坐标（" + name_b + "）")
            self.root.after(0, paint)
            return "fail"

        # 已重合：跳过寻路，避免空等
        st_a0 = ipc.read_state(a_iid) or {}
        try:
            afl = int(st_a0.get("floor") or 0)
            ax = int(st_a0.get("x") or 0)
            ay = int(st_a0.get("y") or 0)
        except (TypeError, ValueError):
            afl = ax = ay = 0
        if afl == fl and abs(ax - xx) <= 2 and abs(ay - yy) <= 2:
            lines.append(
                "锁定坐标：已重合 " + name_a + " ≈ " + name_b
                + f" {fl}({xx},{yy})，跳过寻路"
            )
            self.root.after(0, paint)
            return ""

        lines.append(
            "锁定坐标：" + name_b + f"（一）留在 {fl}({xx},{yy})，"
            + name_a + " 走过去"
        )
        self.root.after(0, paint)
        if gen != getattr(self, gen_attr, 0):
            return "cancel"

        before = ipc.read_state(a_iid) or {}
        before_unix = int(before.get("ctrl_unix") or 0)
        req = ipc.send_command(
            a_iid, "trade_goto_lock", floor=fl, x=xx, y=yy
        )
        ok, ack = ipc.wait_ack(a_iid, req, timeout=8)
        msg = str((ack or {}).get("msg") or "")
        if not ok or not msg.startswith("已启动"):
            lines.append("失败 " + name_a + " 锁定坐标：" + (msg or "无应答"))
            self.root.after(0, paint)
            return "fail"
        lines.append("执行中 " + name_a + "：" + msg)
        self.root.after(0, paint)
        waiting = [
            {
                "iid": a_iid,
                "name": name_a,
                "before": before_unix,
                "idx": len(lines) - 1,
                "miss": 0,
                "ok": False,
                "msg": "",
            }
        ]
        # 游戏侧非战斗行走 60 秒失败；战斗中不计时，墙钟放宽
        done = self._wait_ctrl_pair(
            waiting, lines, paint, gen, gen_attr, timeout=180
        )
        if not done:
            return "cancel"
        if not bool(done[0].get("ok")):
            lines.append("失败：" + name_a + " 未能到达锁定坐标")
            self.root.after(0, paint)
            return "fail"

        st = ipc.read_state(a_iid) or {}
        try:
            sfl = int(st.get("floor") or 0)
            sx = int(st.get("x") or 0)
            sy = int(st.get("y") or 0)
        except (TypeError, ValueError):
            sfl = sx = sy = 0
        if sfl != fl or abs(sx - xx) > 2 or abs(sy - yy) > 2:
            lines.append(
                "失败：" + name_a + f" 坐标不符 {sfl}({sx},{sy}) 期望{fl}({xx},{yy})"
            )
            self.root.after(0, paint)
            return "fail"
        lines.append(name_a + " 已到锁定坐标，开始交易")
        self.root.after(0, paint)
        return ""

    def _trade_at_fixed_spot(self, iid: str) -> bool:
        """非锁定集合点 1000(65,73)，两格内算已到位。"""
        st = ipc.read_state(iid) or {}
        try:
            fl = int(st.get("floor") or 0)
            xx = int(st.get("x") or 0)
            yy = int(st.get("y") or 0)
        except (TypeError, ValueError):
            return False
        return fl == 1000 and abs(xx - 65) <= 2 and abs(yy - 73) <= 2

    def _run_one_sender_trade(
        self,
        a_iid: str,
        name_a: str,
        b_iid: str,
        name_b: str,
        uid_a: str,
        uid_b: str,
        keyword: str,
        stack: int,
        from_bank: bool,
        to_bank: bool,
        lines: list[str],
        paint,
        gen: int,
    ) -> str:
        """跑完一个交易对象。返回 empty|full|fail|cancel。"""

        def start_wait(iid, name, cmd, timeout=120.0, **params):
            return self._start_and_wait(
                iid, name, cmd, lines, paint, gen, timeout=timeout, **params
            )

        # 首步：参与双方二级安全锁（游戏内需验证且账号库空码才失败）
        lines.append("二级验证：" + name_a + " + " + name_b)
        self.root.after(0, paint)
        for iid, name in ((a_iid, name_a), (b_iid, name_b)):
            if gen != self._trade_gen:
                return "cancel"
            code, err = self._resolve_trade_secondary_code(iid)
            if code is None:
                lines.append("失败 " + name + " 二级码：" + err)
                self.root.after(0, paint)
                return "fail"
            ok, _msg = start_wait(
                iid,
                name,
                "trade_security_verify",
                timeout=40,
                code=code or "",
            )
            if not ok:
                return "fail" if _msg != "已取消" else "cancel"

        if bool(self.trade_lock_pos_var.get()):
            gathered = self._trade_gather_lock(
                a_iid, name_a, b_iid, name_b, lines, paint, gen
            )
            if gathered:
                return gathered
        else:
            lines.append("集合：" + name_a + " + " + name_b + " 回城点2→1000(65,73)")
            self.root.after(0, paint)
            waiting = []
            for iid, name in ((a_iid, name_a), (b_iid, name_b)):
                if iid == b_iid and self._trade_at_fixed_spot(iid):
                    lines.append(name + " 已在交易点，跳过回城")
                    self.root.after(0, paint)
                    continue
                before = ipc.read_state(iid) or {}
                before_unix = int(before.get("ctrl_unix") or 0)
                req = ipc.send_command(iid, "trade_goto_spot")
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                if not ok or not msg.startswith("已启动"):
                    lines.append("失败 " + name + " 集合：" + (msg or "无应答"))
                    self.root.after(0, paint)
                    return "fail"
                lines.append("执行中 " + name + "：" + msg)
                waiting.append(
                    {
                        "iid": iid,
                        "name": name,
                        "before": before_unix,
                        "idx": len(lines) - 1,
                        "miss": 0,
                        "ok": False,
                        "msg": "",
                    }
                )
            self.root.after(0, paint)
            if waiting:
                done = self._wait_ctrl_pair(
                    waiting, lines, paint, gen, "_trade_gen", timeout=130
                )
                if not done:
                    return "cancel"
                if not all(bool(x.get("ok")) for x in done):
                    lines.append("失败：集合未双方成功")
                    self.root.after(0, paint)
                    return "fail"
            for iid, name in ((a_iid, name_a), (b_iid, name_b)):
                st = ipc.read_state(iid) or {}
                try:
                    fl = int(st.get("floor") or 0)
                    xx = int(st.get("x") or 0)
                    yy = int(st.get("y") or 0)
                except (TypeError, ValueError):
                    fl = xx = yy = 0
                if fl != 1000 or abs(xx - 65) > 2 or abs(yy - 73) > 2:
                    lines.append(
                        "失败：" + name + f" 坐标不符 {fl}({xx},{yy}) 期望1000(65,73)"
                    )
                    self.root.after(0, paint)
                    return "fail"
            lines.append("双方已到交易点")
            self.root.after(0, paint)

        round_n = 0
        stagnant = 0
        last_sig = None
        while round_n < 40:
            if gen != self._trade_gen:
                return "cancel"
            round_n += 1
            lines.append("—— " + name_a + " 第" + str(round_n) + "轮 ——")
            self.root.after(0, paint)

            if from_bank:
                ok, _msg = start_wait(
                    a_iid,
                    name_a,
                    "trade_bank_take",
                    timeout=100,
                    keyword=keyword,
                    stack=stack,
                )
                if not ok:
                    # 空仓/无货不应卡死整轮；继续探测背包里是否还有可交
                    soft = (
                        "超银空" in _msg
                        or "超银无货" in _msg
                        or "超银无回包" in _msg
                        or "超银无数据" in _msg
                        or "taken=0" in _msg
                    )
                    if soft:
                        lines.append(
                            "警告 " + name_a + " 超银取：" + (_msg or "") + "（按空仓继续）"
                        )
                        self.root.after(0, paint)
                    else:
                        return "fail" if _msg != "已取消" else "cancel"

            req = ipc.send_command(a_iid, "trade_probe", keyword=keyword)
            ok, ack = ipc.wait_ack(a_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已探测"):
                lines.append("失败 " + name_a + " 探测：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            n_a = 0
            try:
                part = msg.split("n=", 1)[-1].strip()
                n_a = int(part.split()[0])
            except (ValueError, IndexError):
                n_a = 0
            lines.append("探测 " + name_a + "：" + msg)

            req = ipc.send_command(b_iid, "trade_probe_empty")
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已探测"):
                lines.append("失败 " + name_b + " 空格：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            empty_b = 0
            try:
                part = msg.split("empty=", 1)[-1].strip()
                empty_b = int(part.split()[0])
            except (ValueError, IndexError):
                empty_b = 0
            lines.append("空格 " + name_b + "：" + msg)
            self.root.after(0, paint)

            if n_a <= 0:
                lines.append("完成：" + name_a + " 已无目标道具")
                self.root.after(0, paint)
                return "empty"

            if empty_b <= 0:
                if to_bank:
                    ok, _msg = start_wait(
                        b_iid,
                        name_b,
                        "trade_bank_store",
                        timeout=60,
                        keyword=keyword,
                    )
                    if not ok:
                        lines.append(
                            "警告 " + name_b + " 预存超银失败：" + (_msg or "")
                            + "（按满包处理）"
                        )
                        self.root.after(0, paint)
                    else:
                        req = ipc.send_command(b_iid, "trade_probe_empty")
                        ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
                        msg = str((ack or {}).get("msg") or "")
                        try:
                            empty_b = int(
                                msg.split("empty=", 1)[-1].strip().split()[0]
                            )
                        except (ValueError, IndexError):
                            empty_b = 0
                        lines.append("存后空格 " + name_b + "：empty=" + str(empty_b))
                        self.root.after(0, paint)
                if empty_b <= 0:
                    lines.append("完成：被交易对象背包已满")
                    self.root.after(0, paint)
                    return "full"

            # 给出5/空格10→交5；给出5/空格3→交3；官方上限20
            trade_limit = 20
            n = min(n_a, empty_b, trade_limit)
            lines.append(
                "本轮交" + str(n) + "格 = min(给出" + str(n_a)
                + ", 空格" + str(empty_b) + ", 上限20)"
            )
            self.root.after(0, paint)
            if n <= 0:
                lines.append("完成：无可交格数")
                self.root.after(0, paint)
                return "empty"

            sig = (n_a, empty_b)
            if sig == last_sig:
                stagnant += 1
                if stagnant >= 2:
                    lines.append(
                        "停止：连续两轮给出/空格无变化("
                        + str(n_a) + "/" + str(empty_b) + ")，避免卡死"
                    )
                    self.root.after(0, paint)
                    return "fail"
            else:
                stagnant = 0
            last_sig = sig

            before_b = ipc.read_state(b_iid) or {}
            before_unix_b = int(before_b.get("ctrl_unix") or 0)
            req = ipc.send_command(
                b_iid, "trade_prepare", expect_uid=uid_a, need_slots=n
            )
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已启动"):
                lines.append("失败 " + name_b + " 准备：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            lines.append("执行中 " + name_b + "：" + msg)
            idx_b = len(lines) - 1
            self.root.after(0, paint)
            time.sleep(0.4)

            before_a = ipc.read_state(a_iid) or {}
            before_unix_a = int(before_a.get("ctrl_unix") or 0)
            req = ipc.send_command(
                a_iid,
                "trade_send",
                partner_uid=uid_b,
                keyword=keyword,
                max_slots=n,
            )
            ok, ack = ipc.wait_ack(a_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已启动"):
                lines.append("失败 " + name_a + " 发起：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            lines.append("执行中 " + name_a + "：" + msg)
            idx_a = len(lines) - 1
            self.root.after(0, paint)

            waiting = [
                {
                    "iid": a_iid,
                    "name": name_a,
                    "before": before_unix_a,
                    "idx": idx_a,
                    "miss": 0,
                    "ok": False,
                    "msg": "",
                },
                {
                    "iid": b_iid,
                    "name": name_b,
                    "before": before_unix_b,
                    "idx": idx_b,
                    "miss": 0,
                    "ok": False,
                    "msg": "",
                },
            ]
            done = self._wait_ctrl_pair(
                waiting, lines, paint, gen, "_trade_gen", timeout=100
            )
            if not done:
                return "cancel"
            if not all(bool(x.get("ok")) for x in done):
                lines.append("失败：本轮交易未双方成功")
                self.root.after(0, paint)
                return "fail"

            # 整理：失败只警告，不中断整轮（uid 必须本窗 MainPlayerUid）
            time.sleep(0.5)
            req = ipc.send_command(b_iid, "trade_bag_sort")
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已整理"):
                lines.append("警告 " + name_b + " 整理：" + (msg or "无应答") + "（继续）")
                self.root.after(0, paint)
            else:
                lines.append(name_b + "：已整理背包")
                self.root.after(0, paint)
            time.sleep(0.3)

            if to_bank:
                ok, _msg = start_wait(
                    b_iid,
                    name_b,
                    "trade_bank_store",
                    timeout=60,
                    keyword=keyword,
                )
                if not ok:
                    lines.append(
                        "警告 " + name_b + " 存超银失败：" + (_msg or "")
                        + "（继续；下轮若满包则结束）"
                    )
                    self.root.after(0, paint)

            lines.append(
                name_a + " 第" + str(round_n) + "轮完成 交易" + str(n) + "格"
            )
            self.root.after(0, paint)

        lines.append("停止：达到最大轮数")
        self.root.after(0, paint)
        return "fail"

    def stop_single_trade(self) -> None:
        """中止中控交易编排，并向相关窗口下发 trade_stop。"""
        self._trade_gen = getattr(self, "_trade_gen", 0) + 1
        iids = [x for x in (getattr(self, "_trade_active_iids", None) or []) if x]
        if not iids:
            for e in self._trade_sender_entries:
                iids.append(e["iid"])
            recv = self._trade_resolve_receiver()
            if recv:
                iids.append(recv[0])
        seen = set()
        uniq = []
        for iid in iids:
            if iid in seen:
                continue
            seen.add(iid)
            uniq.append(iid)
        iids = uniq

        def work() -> None:
            lines: list[str] = ["已请求停止交易…"]
            self.root.after(0, lambda: self._set_trade_result("\n".join(lines)))
            if not iids:
                lines.append("无目标窗口")
                self.root.after(0, lambda: self._set_trade_result("\n".join(lines)))
                return
            for iid in iids:
                st = ipc.read_state(iid) or {}
                name = str(st.get("name") or iid)
                if not ipc.bridge_alive(iid):
                    lines.append("跳过 " + name + "：窗口离线")
                    self.root.after(
                        0, lambda L=list(lines): self._set_trade_result("\n".join(L))
                    )
                    continue
                req = ipc.send_command(iid, "trade_stop")
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                lines.append(("成功 " if ok else "失败 ") + name + "：" + (msg or "无应答"))
                self.root.after(
                    0, lambda L=list(lines): self._set_trade_result("\n".join(L))
                )
            lines.append("中控编排已中止")
            self.root.after(0, lambda: self._set_trade_result("\n".join(lines)))

        threading.Thread(target=work, daemon=True).start()

    def start_single_trade(self) -> None:
        """对列表中当前选中的一个交易对象执行（未选则用第一个）。"""
        if not self._trade_sender_entries:
            messagebox.showwarning("交易", "请先把窗口加入交易对象列表。", parent=self.root)
            return
        recv = self._trade_resolve_receiver()
        if recv is None:
            return
        b_iid, name_b = recv
        sel = list(self.trade_sender_list.curselection())
        idx = int(sel[0]) if sel else 0
        if idx < 0 or idx >= len(self._trade_sender_entries):
            messagebox.showwarning("交易", "交易对象无效。", parent=self.root)
            return
        entry = self._trade_sender_entries[idx]
        a_iid = entry["iid"]
        if a_iid == b_iid:
            messagebox.showwarning(
                "交易", "交易对象与被交易对象不能是同一窗口。", parent=self.root
            )
            return
        params = self._trade_params()
        if params is None:
            return
        keyword, stack, from_bank, to_bank = params
        st_a0 = ipc.read_state(a_iid) or {}
        st_b0 = ipc.read_state(b_iid) or {}
        uid_a = self._trade_uid_from_state(st_a0)
        uid_b = self._trade_uid_from_state(st_b0)
        name_a = str(st_a0.get("name") or entry.get("name") or a_iid)
        if not uid_a or not uid_b:
            messagebox.showerror(
                "交易",
                "读不到双方队长 uid。\nA=" + name_a + " B=" + name_b,
                parent=self.root,
            )
            return

        self._trade_gen = getattr(self, "_trade_gen", 0) + 1
        gen = self._trade_gen
        self._trade_active_iids = [a_iid, b_iid]
        self._trade_reset_sender_colors("idle")
        self._trade_paint_sender_row(idx, "run")
        self._set_trade_result(
            "单次交易：" + name_a + " → " + name_b
            + " 「" + keyword + "」…"
        )

        def work() -> None:
            lines: list[str] = []

            def paint() -> None:
                if gen != self._trade_gen:
                    return
                self._set_trade_result("\n".join(lines) if lines else "…")

            result = self._run_one_sender_trade(
                a_iid,
                name_a,
                b_iid,
                name_b,
                uid_a,
                uid_b,
                keyword,
                stack,
                from_bank,
                to_bank,
                lines,
                paint,
                gen,
            )
            if gen != self._trade_gen:
                return
            if result == "empty":
                self._trade_paint_sender_row(idx, "done")
                lines.append("单次交易结束：给出方已空")
            elif result == "full":
                self._trade_paint_sender_row(idx, "done")
                lines.append("单次交易结束：接收方已满")
            elif result == "cancel":
                lines.append("单次交易已取消")
            else:
                self._trade_paint_sender_row(idx, "fail")
                lines.append("单次交易失败")
            self.root.after(0, paint)

        threading.Thread(target=work, daemon=True).start()

    def start_all_trades(self) -> None:
        """按列表顺序逐个交易对象跑完，直到全部空或接收方满。"""
        if not self._trade_sender_entries:
            messagebox.showwarning("交易", "请先把窗口加入交易对象列表。", parent=self.root)
            return
        recv = self._trade_resolve_receiver()
        if recv is None:
            return
        b_iid, name_b = recv
        for e in self._trade_sender_entries:
            if e["iid"] == b_iid:
                messagebox.showwarning(
                    "交易",
                    "被交易对象不能出现在交易对象列表中：" + (e.get("name") or e["iid"]),
                    parent=self.root,
                )
                return
        params = self._trade_params()
        if params is None:
            return
        keyword, stack, from_bank, to_bank = params

        st_b0 = ipc.read_state(b_iid) or {}
        uid_b = self._trade_uid_from_state(st_b0)
        if not uid_b:
            messagebox.showerror("交易", "读不到被交易对象队长 uid。", parent=self.root)
            return

        senders = list(self._trade_sender_entries)
        self._trade_gen = getattr(self, "_trade_gen", 0) + 1
        gen = self._trade_gen
        self._trade_active_iids = [e["iid"] for e in senders] + [b_iid]
        self._trade_reset_sender_colors("wait")
        self._set_trade_result(
            "全部交易：" + str(len(senders)) + " 个给出方 → " + name_b
            + " 「" + keyword + "」…"
        )

        def work() -> None:
            lines: list[str] = []

            def paint() -> None:
                if gen != self._trade_gen:
                    return
                self._set_trade_result("\n".join(lines) if lines else "…")

            for i, entry in enumerate(senders):
                if gen != self._trade_gen:
                    return
                a_iid = entry["iid"]
                st_a = ipc.read_state(a_iid) or {}
                name_a = str(st_a.get("name") or entry.get("name") or a_iid)
                uid_a = self._trade_uid_from_state(st_a)
                lines.append("======== 交易对象 " + str(i + 1) + "/" + str(len(senders))
                             + " " + name_a + " ========")
                self.root.after(0, paint)
                if not uid_a:
                    lines.append("失败：" + name_a + " 无队长 uid，跳过")
                    self._trade_paint_sender_row(i, "fail")
                    self.root.after(0, paint)
                    continue
                self._trade_paint_sender_row(i, "run")
                result = self._run_one_sender_trade(
                    a_iid,
                    name_a,
                    b_iid,
                    name_b,
                    uid_a,
                    uid_b,
                    keyword,
                    stack,
                    from_bank,
                    to_bank,
                    lines,
                    paint,
                    gen,
                )
                if gen != self._trade_gen:
                    return
                if result == "empty":
                    self._trade_paint_sender_row(i, "done")
                    lines.append(name_a + " 已搬完，下一个…")
                    self.root.after(0, paint)
                    continue
                if result == "full":
                    self._trade_paint_sender_row(i, "done")
                    # 剩余排队保持蓝色
                    lines.append("全部交易结束：被交易对象已满（后续未跑）")
                    self.root.after(0, paint)
                    return
                if result == "cancel":
                    lines.append("全部交易已取消")
                    self.root.after(0, paint)
                    return
                self._trade_paint_sender_row(i, "fail")
                lines.append(name_a + " 失败，停止全部交易")
                self.root.after(0, paint)
                return

            lines.append("全部交易完成：所有交易对象已空")
            self.root.after(0, paint)

        threading.Thread(target=work, daemon=True).start()

    # ===== 分发（一对多：单给出方 → 多接收方）=====
    #
    # 与「交易」相反：右边选一个分发者，左边排队多个被分发对象。
    # 参数：超银堆叠（取仓每格上限）+ 单次交易数量（每个接收方目标交几格）。
    # 例：堆叠20、数量10、对方空格5 → 交 2 轮，每轮 5×20；未勾选存超银则第2轮空格不足，软失败并继续下一人。

    def _build_dist_tab(self, tab: ttk.Frame) -> None:
        ttk.Label(
            tab,
            text=(
                "分发：一个分发者（「一」）把道具交给多人。"
                "未勾选锁定坐标时双方回城点2→1000(65,73)；分发者已在该点则不再回城。"
                "勾选锁定坐标后，以分发者坐标为准：分发者留原地，各被分发对象走过去。"
                "单次交易数量=每个接收方目标交几格；单轮=min(剩余数量, 给出可交, 对方空格, 20)。"
                "例：数量10、给出5、空格10→先交5，给出空则整批停止；数量10、空格3未存超银→交3后软失败并继续下一人。"
            ),
            foreground="#666",
            wraplength=900,
        ).pack(anchor=tk.W, pady=(4, 8))

        frm = ttk.LabelFrame(tab, text="分发", padding=10)
        frm.pack(fill=tk.BOTH, expand=False, pady=(0, 8))

        row1 = ttk.Frame(frm)
        row1.pack(fill=tk.X, pady=2)
        ttk.Label(row1, text="道具名称(含)", width=14).pack(side=tk.LEFT)
        ttk.Entry(row1, textvariable=self.dist_keyword_var, width=20).pack(
            side=tk.LEFT, padx=(0, 12)
        )
        ttk.Label(row1, text="超银堆叠", width=8).pack(side=tk.LEFT)
        ttk.Entry(row1, textvariable=self.dist_stack_var, width=5).pack(side=tk.LEFT)
        ttk.Label(row1, text="单次交易数量", width=12).pack(side=tk.LEFT, padx=(12, 0))
        ttk.Entry(row1, textvariable=self.dist_qty_var, width=5).pack(side=tk.LEFT)
        ttk.Label(
            row1, text="(每接收方目标格数)", foreground="#888"
        ).pack(side=tk.LEFT, padx=(6, 0))

        mid = ttk.Frame(frm)
        mid.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        left = ttk.Frame(mid)
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        ttk.Label(left, text="被分发对象（可多个，排队顺序）").pack(anchor=tk.W)
        list_wrap = ttk.Frame(left)
        list_wrap.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self.dist_recv_list = tk.Listbox(
            list_wrap,
            height=8,
            exportselection=False,
            activestyle="dotbox",
            font=("Microsoft YaHei UI", 10),
            selectmode=tk.EXTENDED,
        )
        sb = ttk.Scrollbar(list_wrap, orient=tk.VERTICAL, command=self.dist_recv_list.yview)
        self.dist_recv_list.configure(yscrollcommand=sb.set)
        self.dist_recv_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sb.pack(side=tk.RIGHT, fill=tk.Y)

        add_row = ttk.Frame(left)
        add_row.pack(fill=tk.X, pady=(6, 0))
        self.dist_add_recv_cb = ttk.Combobox(
            add_row, textvariable=self.dist_add_recv_var, width=40, state="readonly"
        )
        self.dist_add_recv_cb.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(add_row, text="加入", command=self._dist_add_recv, width=6).pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Button(
            add_row, text="一键加入", command=self._dist_add_all_watched_recvs, width=8
        ).pack(side=tk.LEFT, padx=(4, 0))
        ttk.Button(add_row, text="移除", command=self._dist_remove_recvs, width=6).pack(
            side=tk.LEFT, padx=(4, 0)
        )

        opt_row = ttk.Frame(left)
        opt_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Checkbutton(
            opt_row, text="被分发对象存入超银", variable=self.dist_to_bank_var
        ).pack(side=tk.LEFT)

        right = ttk.Frame(mid, padding=(16, 0, 0, 0))
        right.pack(side=tk.LEFT, fill=tk.Y)
        ttk.Label(right, text="分发者（单个）").pack(anchor=tk.W)
        self.dist_sender_cb = ttk.Combobox(
            right, textvariable=self.dist_sender_var, width=36, state="readonly"
        )
        self.dist_sender_cb.pack(anchor=tk.W, pady=(4, 0))
        ttk.Checkbutton(
            right, text="分发者从超银取", variable=self.dist_from_bank_var
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            right,
            text="锁定坐标",
            variable=self.dist_lock_pos_var,
        ).pack(anchor=tk.W, pady=(4, 0))
        ttk.Label(
            right,
            text="勾选后以分发者坐标为准：被分发对象走过去。",
            foreground="#888",
            wraplength=260,
            justify=tk.LEFT,
        ).pack(anchor=tk.W, pady=(2, 0))
        ttk.Label(
            right,
            text="蓝=排队等待\n白=已跑完\n黄=正在分发",
            foreground="#888",
            justify=tk.LEFT,
        ).pack(anchor=tk.W, pady=(12, 0))

        btn_row = ttk.Frame(frm)
        btn_row.pack(fill=tk.X, pady=(10, 0))
        ttk.Button(
            btn_row, text="刷新窗口列表", command=self._refresh_trade_windows, width=12
        ).pack(side=tk.LEFT)
        ttk.Button(
            btn_row, text="开始单次分发", command=self.start_single_dist, width=12
        ).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(
            btn_row, text="全部分发", command=self.start_all_dists, width=10
        ).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(
            btn_row, text="一键停止", command=self.stop_dist, width=10
        ).pack(side=tk.LEFT, padx=(8, 0))

        ttk.Label(tab, text="日志").pack(anchor=tk.W, pady=(8, 0))
        self.dist_result = tk.Text(tab, wrap=tk.WORD, state=tk.DISABLED, height=14)
        self.dist_result.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        self._set_dist_result(
            "尚未开始。刷新后选好分发者，把窗口加入被分发列表，再单次或全部分发。"
        )
        self._refresh_trade_windows()

    def _set_dist_result(self, text: str) -> None:
        w = getattr(self, "dist_result", None)
        if w is None:
            return
        w.configure(state=tk.NORMAL)
        w.delete("1.0", tk.END)
        w.insert(tk.END, text or "")
        w.configure(state=tk.DISABLED)

    def _dist_paint_recv_row(self, index: int, status: str) -> None:
        lb = getattr(self, "dist_recv_list", None)
        if lb is None or index < 0 or index >= lb.size():
            return
        colors = {
            "wait": "#5DADE2",
            "run": "#F4D03F",
            "done": "#FFFFFF",
            "idle": "#FFFFFF",
            "fail": "#F5B7B1",
        }
        bg = colors.get(status, "#FFFFFF")
        fg = "#1A1A1A" if status != "wait" else "#0B3040"

        def apply() -> None:
            try:
                lb.itemconfigure(index, bg=bg, fg=fg)
            except tk.TclError:
                pass

        self.root.after(0, apply)

    def _dist_reset_recv_colors(self, mode: str = "wait") -> None:
        for i in range(len(self._dist_recv_entries)):
            self._dist_paint_recv_row(i, mode)

    def _dist_rebuild_recv_listbox(self, keep_colors: bool = False) -> None:
        lb = getattr(self, "dist_recv_list", None)
        if lb is None:
            return
        old_colors = []
        if keep_colors:
            for i in range(lb.size()):
                try:
                    old_colors.append(lb.itemcget(i, "bg"))
                except tk.TclError:
                    old_colors.append("#FFFFFF")
        lb.delete(0, tk.END)
        for e in self._dist_recv_entries:
            iid = e["iid"]
            st = ipc.read_state(iid) or {}
            name = str(st.get("name") or e.get("name") or iid)
            e["name"] = name
            floor = st.get("floor") or 0
            x = st.get("x") or 0
            y = st.get("y") or 0
            label = f"{name}  [{iid}]  {floor}({x},{y})"
            e["label"] = label
            lb.insert(tk.END, label)
        for i, bg in enumerate(old_colors):
            if i < lb.size():
                try:
                    lb.itemconfigure(i, bg=bg or "#FFFFFF")
                except tk.TclError:
                    pass

    def _dist_add_recv(self) -> None:
        lab = (self.dist_add_recv_var.get() or "").strip()
        iid = self._trade_label_to_iid.get(lab, "")
        if not iid:
            messagebox.showwarning("分发", "请先刷新并选择要加入的窗口。", parent=self.root)
            return
        send_lab = (self.dist_sender_var.get() or "").strip()
        send_iid = self._trade_label_to_iid.get(send_lab, "")
        if send_iid and iid == send_iid:
            messagebox.showwarning(
                "分发", "不能把分发者加入被分发对象列表。", parent=self.root
            )
            return
        if any(e["iid"] == iid for e in self._dist_recv_entries):
            messagebox.showinfo("分发", "该窗口已在被分发列表中。", parent=self.root)
            return
        st = ipc.read_state(iid) or {}
        name = str(st.get("name") or iid)
        self._dist_recv_entries.append({"iid": iid, "label": lab, "name": name})
        self.dist_recv_list.insert(tk.END, lab)
        self._dist_paint_recv_row(self.dist_recv_list.size() - 1, "idle")

    def _dist_add_all_watched_recvs(self) -> None:
        self._refresh_trade_windows()
        send_lab = (self.dist_sender_var.get() or "").strip()
        send_iid = self._trade_label_to_iid.get(send_lab, "")
        have = {e["iid"] for e in self._dist_recv_entries}
        n = 0
        for lab, iid in list(self._trade_label_to_iid.items()):
            if not iid or iid == send_iid or iid in have:
                continue
            st = ipc.read_state(iid) or {}
            name = str(st.get("name") or iid)
            self._dist_recv_entries.append({"iid": iid, "label": lab, "name": name})
            self.dist_recv_list.insert(tk.END, lab)
            self._dist_paint_recv_row(self.dist_recv_list.size() - 1, "idle")
            have.add(iid)
            n += 1
        if n <= 0:
            messagebox.showinfo(
                "分发",
                "没有可加入的窗口（已排除分发者和列表中已有的）。",
                parent=self.root,
            )

    def _dist_remove_recvs(self) -> None:
        sel = list(self.dist_recv_list.curselection())
        if not sel:
            messagebox.showinfo("分发", "请先选中要移除的被分发对象。", parent=self.root)
            return
        for i in reversed(sel):
            if 0 <= i < len(self._dist_recv_entries):
                self._dist_recv_entries.pop(i)
            self.dist_recv_list.delete(i)

    def _save_dist_settings(self) -> None:
        cfg = load_settings()
        cfg["central_dist_keyword"] = (self.dist_keyword_var.get() or "").strip() or "五仁"
        try:
            cfg["central_dist_stack"] = max(
                1, int(str(self.dist_stack_var.get() or "20").strip())
            )
        except ValueError:
            cfg["central_dist_stack"] = 20
        try:
            cfg["central_dist_qty"] = max(
                1, int(str(self.dist_qty_var.get() or "10").strip())
            )
        except ValueError:
            cfg["central_dist_qty"] = 10
        cfg["central_dist_from_bank"] = bool(self.dist_from_bank_var.get())
        cfg["central_dist_to_bank"] = bool(self.dist_to_bank_var.get())
        cfg["central_dist_lock_pos"] = bool(self.dist_lock_pos_var.get())
        try:
            save_settings(cfg)
        except OSError:
            pass

    def _dist_resolve_sender(self) -> tuple[str, str] | None:
        lab = (self.dist_sender_var.get() or "").strip()
        iid = self._trade_label_to_iid.get(lab, "")
        if not iid:
            messagebox.showwarning("分发", "请先选择分发者。", parent=self.root)
            return None
        st = ipc.read_state(iid) or {}
        name = str(st.get("name") or iid)
        return iid, name

    def _dist_params(self) -> tuple[str, int, int, bool, bool] | None:
        keyword = (self.dist_keyword_var.get() or "").strip() or "五仁"
        self.dist_keyword_var.set(keyword)
        try:
            stack = max(1, int(str(self.dist_stack_var.get() or "20").strip()))
        except ValueError:
            stack = 20
            self.dist_stack_var.set("20")
        try:
            qty = max(1, int(str(self.dist_qty_var.get() or "10").strip()))
        except ValueError:
            qty = 10
            self.dist_qty_var.set("10")
        from_bank = bool(self.dist_from_bank_var.get())
        to_bank = bool(self.dist_to_bank_var.get())
        self._save_dist_settings()
        return keyword, stack, qty, from_bank, to_bank

    def _run_one_dist_to_receiver(
        self,
        a_iid: str,
        name_a: str,
        b_iid: str,
        name_b: str,
        uid_a: str,
        uid_b: str,
        keyword: str,
        stack: int,
        qty: int,
        from_bank: bool,
        to_bank: bool,
        lines: list[str],
        paint,
        gen: int,
    ) -> str:
        """分发给一个接收方。返回 done|partial|empty|fail|cancel。
        done=数量交满；partial=中途空格不足（软失败，可继续下一人）；
        empty=分发者无货。"""

        def start_wait(iid, name, cmd, timeout=120.0, **params):
            return self._start_and_wait(
                iid,
                name,
                cmd,
                lines,
                paint,
                gen,
                timeout=timeout,
                gen_attr="_dist_gen",
                **params,
            )

        lines.append("二级验证：" + name_a + " + " + name_b)
        self.root.after(0, paint)
        for iid, name in ((a_iid, name_a), (b_iid, name_b)):
            if gen != self._dist_gen:
                return "cancel"
            code, err = self._resolve_trade_secondary_code(iid)
            if code is None:
                lines.append("失败 " + name + " 二级码：" + err)
                self.root.after(0, paint)
                return "fail"
            ok, _msg = start_wait(
                iid,
                name,
                "trade_security_verify",
                timeout=40,
                code=code or "",
            )
            if not ok:
                return "fail" if _msg != "已取消" else "cancel"

        if bool(self.dist_lock_pos_var.get()):
            # 「一」=分发者 a 留原地；被分发 b 走过去
            gathered = self._trade_gather_lock(
                b_iid,
                name_b,
                a_iid,
                name_a,
                lines,
                paint,
                gen,
                gen_attr="_dist_gen",
            )
            if gathered:
                return gathered
        else:
            lines.append("集合：" + name_a + " + " + name_b + " 回城点2→1000(65,73)")
            self.root.after(0, paint)
            waiting = []
            for iid, name in ((a_iid, name_a), (b_iid, name_b)):
                # 「一」=分发者：已在交易点则跳过回城
                if iid == a_iid and self._trade_at_fixed_spot(iid):
                    lines.append(name + "（分发者）已在交易点，跳过回城")
                    self.root.after(0, paint)
                    continue
                before = ipc.read_state(iid) or {}
                before_unix = int(before.get("ctrl_unix") or 0)
                req = ipc.send_command(iid, "trade_goto_spot")
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                if not ok or not msg.startswith("已启动"):
                    lines.append("失败 " + name + " 集合：" + (msg or "无应答"))
                    self.root.after(0, paint)
                    return "fail"
                lines.append("执行中 " + name + "：" + msg)
                waiting.append(
                    {
                        "iid": iid,
                        "name": name,
                        "before": before_unix,
                        "idx": len(lines) - 1,
                        "miss": 0,
                        "ok": False,
                        "msg": "",
                    }
                )
            self.root.after(0, paint)
            if waiting:
                done = self._wait_ctrl_pair(
                    waiting, lines, paint, gen, "_dist_gen", timeout=130
                )
                if not done:
                    return "cancel"
                if not all(bool(x.get("ok")) for x in done):
                    lines.append("失败：集合未双方成功")
                    self.root.after(0, paint)
                    return "fail"
            for iid, name in ((a_iid, name_a), (b_iid, name_b)):
                st = ipc.read_state(iid) or {}
                try:
                    fl = int(st.get("floor") or 0)
                    xx = int(st.get("x") or 0)
                    yy = int(st.get("y") or 0)
                except (TypeError, ValueError):
                    fl = xx = yy = 0
                if fl != 1000 or abs(xx - 65) > 2 or abs(yy - 73) > 2:
                    lines.append(
                        "失败：" + name + f" 坐标不符 {fl}({xx},{yy}) 期望1000(65,73)"
                    )
                    self.root.after(0, paint)
                    return "fail"
            lines.append("双方已到交易点")
            self.root.after(0, paint)

        remaining = max(1, int(qty))
        delivered = 0
        round_n = 0
        stagnant = 0
        last_sig = None
        lines.append(
            "目标：" + name_b + " 交" + str(remaining) + "格（堆叠" + str(stack) + "）"
        )
        self.root.after(0, paint)

        while remaining > 0 and round_n < 40:
            if gen != self._dist_gen:
                return "cancel"
            round_n += 1
            lines.append(
                "—— 分发→" + name_b + " 第" + str(round_n) + "轮 剩余"
                + str(remaining) + "格 ——"
            )
            self.root.after(0, paint)

            if from_bank:
                ok, _msg = start_wait(
                    a_iid,
                    name_a,
                    "trade_bank_take",
                    timeout=100,
                    keyword=keyword,
                    stack=stack,
                )
                if not ok:
                    soft = (
                        "超银空" in _msg
                        or "超银无货" in _msg
                        or "超银无回包" in _msg
                        or "超银无数据" in _msg
                        or "taken=0" in _msg
                    )
                    if soft:
                        lines.append(
                            "警告 " + name_a + " 超银取：" + (_msg or "") + "（按空仓继续）"
                        )
                        self.root.after(0, paint)
                    else:
                        return "fail" if _msg != "已取消" else "cancel"

            req = ipc.send_command(a_iid, "trade_probe", keyword=keyword)
            ok, ack = ipc.wait_ack(a_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已探测"):
                lines.append("失败 " + name_a + " 探测：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            n_a = 0
            try:
                n_a = int(msg.split("n=", 1)[-1].strip().split()[0])
            except (ValueError, IndexError):
                n_a = 0
            lines.append("探测 " + name_a + "：" + msg)

            req = ipc.send_command(b_iid, "trade_probe_empty")
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已探测"):
                lines.append("失败 " + name_b + " 空格：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            empty_b = 0
            try:
                empty_b = int(msg.split("empty=", 1)[-1].strip().split()[0])
            except (ValueError, IndexError):
                empty_b = 0
            lines.append("空格 " + name_b + "：" + msg)
            self.root.after(0, paint)

            if n_a <= 0:
                lines.append(
                    "分发者已无目标道具（已交" + str(delivered) + "/" + str(qty) + "格）"
                )
                self.root.after(0, paint)
                # 给出方空了：整批应停（empty），即使本接收方未满额
                return "empty"

            if empty_b <= 0:
                if to_bank:
                    ok, _msg = start_wait(
                        b_iid,
                        name_b,
                        "trade_bank_store",
                        timeout=60,
                        keyword=keyword,
                    )
                    if not ok:
                        lines.append(
                            "警告 " + name_b + " 预存超银失败：" + (_msg or "")
                            + "（按满包处理）"
                        )
                        self.root.after(0, paint)
                    else:
                        req = ipc.send_command(b_iid, "trade_probe_empty")
                        ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
                        msg = str((ack or {}).get("msg") or "")
                        try:
                            empty_b = int(
                                msg.split("empty=", 1)[-1].strip().split()[0]
                            )
                        except (ValueError, IndexError):
                            empty_b = 0
                        lines.append("存后空格 " + name_b + "：empty=" + str(empty_b))
                        self.root.after(0, paint)
                if empty_b <= 0:
                    lines.append(
                        "软失败：" + name_b + " 背包已满（已交" + str(delivered)
                        + "/" + str(qty) + "格），继续下一人"
                    )
                    self.root.after(0, paint)
                    return "partial"

            # 给出5/空格10 → 交5；给出5/空格3 → 交3；不会超过任何一侧
            n = min(remaining, n_a, empty_b, 20)
            lines.append(
                "本轮交" + str(n) + "格 = min(剩余" + str(remaining)
                + ", 给出" + str(n_a) + ", 空格" + str(empty_b) + ", 上限20)"
            )
            self.root.after(0, paint)
            if n <= 0:
                lines.append(
                    "软失败：无可交格数（已交" + str(delivered) + "格），继续下一人"
                )
                self.root.after(0, paint)
                return "partial"

            sig = (n_a, empty_b, remaining)
            if sig == last_sig:
                stagnant += 1
                if stagnant >= 2:
                    lines.append(
                        "软失败：连续两轮状态无变化，避免卡死（已交"
                        + str(delivered) + "格）"
                    )
                    self.root.after(0, paint)
                    return "partial"
            else:
                stagnant = 0
            last_sig = sig

            before_b = ipc.read_state(b_iid) or {}
            before_unix_b = int(before_b.get("ctrl_unix") or 0)
            req = ipc.send_command(
                b_iid, "trade_prepare", expect_uid=uid_a, need_slots=n
            )
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已启动"):
                lines.append("失败 " + name_b + " 准备：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            lines.append("执行中 " + name_b + "：" + msg)
            idx_b = len(lines) - 1
            self.root.after(0, paint)
            time.sleep(0.4)

            before_a = ipc.read_state(a_iid) or {}
            before_unix_a = int(before_a.get("ctrl_unix") or 0)
            req = ipc.send_command(
                a_iid,
                "trade_send",
                partner_uid=uid_b,
                keyword=keyword,
                max_slots=n,
            )
            ok, ack = ipc.wait_ack(a_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已启动"):
                lines.append("失败 " + name_a + " 发起：" + (msg or "无应答"))
                self.root.after(0, paint)
                return "fail"
            lines.append("执行中 " + name_a + "：" + msg)
            idx_a = len(lines) - 1
            self.root.after(0, paint)

            waiting = [
                {
                    "iid": a_iid,
                    "name": name_a,
                    "before": before_unix_a,
                    "idx": idx_a,
                    "miss": 0,
                    "ok": False,
                    "msg": "",
                },
                {
                    "iid": b_iid,
                    "name": name_b,
                    "before": before_unix_b,
                    "idx": idx_b,
                    "miss": 0,
                    "ok": False,
                    "msg": "",
                },
            ]
            done = self._wait_ctrl_pair(
                waiting, lines, paint, gen, "_dist_gen", timeout=100
            )
            if not done:
                return "cancel"
            both_ok = all(bool(x.get("ok")) for x in done)
            # 脚本回报失败但手动完成时：用接收方空格是否变少判断是否实际到账
            arrived = both_ok
            if not both_ok:
                time.sleep(0.4)
                req = ipc.send_command(b_iid, "trade_probe_empty")
                ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                empty_after = -1
                try:
                    empty_after = int(msg.split("empty=", 1)[-1].strip().split()[0])
                except (ValueError, IndexError):
                    empty_after = -1
                if empty_after >= 0 and empty_after < empty_b:
                    arrived = True
                    lines.append(
                        "警告：脚本未双方成功，但 "
                        + name_b
                        + " 空格 "
                        + str(empty_b)
                        + "→"
                        + str(empty_after)
                        + "，按已到账继续存超银"
                    )
                    self.root.after(0, paint)
                else:
                    lines.append(
                        "软失败：本轮交易未双方成功（已交" + str(delivered)
                        + "格），继续下一人"
                    )
                    self.root.after(0, paint)
                    return "partial" if delivered > 0 else "fail"

            time.sleep(0.5)
            req = ipc.send_command(b_iid, "trade_bag_sort")
            ok, ack = ipc.wait_ack(b_iid, req, timeout=8)
            msg = str((ack or {}).get("msg") or "")
            if not ok or not msg.startswith("已整理"):
                lines.append("警告 " + name_b + " 整理：" + (msg or "无应答") + "（继续）")
                self.root.after(0, paint)
            else:
                lines.append(name_b + "：已整理背包")
                self.root.after(0, paint)
            time.sleep(0.3)

            if to_bank:
                ok, _msg = start_wait(
                    b_iid,
                    name_b,
                    "trade_bank_store",
                    timeout=60,
                    keyword=keyword,
                )
                if not ok:
                    lines.append(
                        "警告 " + name_b + " 存超银失败：" + (_msg or "") + "（继续）"
                    )
                    self.root.after(0, paint)

            remaining -= n
            delivered += n
            lines.append(
                name_b + " 第" + str(round_n) + "轮完成 交" + str(n)
                + "格 累计" + str(delivered) + "/" + str(qty)
                + ("" if both_ok else "（空格兜底）")
            )
            self.root.after(0, paint)

        if remaining <= 0:
            lines.append("完成：" + name_b + " 已交满 " + str(delivered) + "格")
            self.root.after(0, paint)
            return "done"
        lines.append("停止：达到最大轮数 已交" + str(delivered))
        self.root.after(0, paint)
        return "partial"

    def stop_dist(self) -> None:
        self._dist_gen = getattr(self, "_dist_gen", 0) + 1
        iids = [x for x in (getattr(self, "_dist_active_iids", None) or []) if x]
        if not iids:
            for e in self._dist_recv_entries:
                iids.append(e["iid"])
            snd = self._dist_resolve_sender()
            if snd:
                iids.append(snd[0])
        seen: set[str] = set()
        uniq: list[str] = []
        for iid in iids:
            if iid in seen:
                continue
            seen.add(iid)
            uniq.append(iid)

        def work() -> None:
            lines: list[str] = ["已请求停止分发…"]
            self.root.after(0, lambda: self._set_dist_result("\n".join(lines)))
            for iid in uniq:
                st = ipc.read_state(iid) or {}
                name = str(st.get("name") or iid)
                if not ipc.bridge_alive(iid):
                    lines.append("跳过 " + name + "：窗口离线")
                    self.root.after(
                        0, lambda L=list(lines): self._set_dist_result("\n".join(L))
                    )
                    continue
                req = ipc.send_command(iid, "trade_stop")
                ok, ack = ipc.wait_ack(iid, req, timeout=8)
                msg = str((ack or {}).get("msg") or "")
                lines.append(("成功 " if ok else "失败 ") + name + "：" + (msg or "无应答"))
                self.root.after(
                    0, lambda L=list(lines): self._set_dist_result("\n".join(L))
                )
            lines.append("中控分发编排已中止")
            self.root.after(0, lambda: self._set_dist_result("\n".join(lines)))

        threading.Thread(target=work, daemon=True).start()

    def start_single_dist(self) -> None:
        if not self._dist_recv_entries:
            messagebox.showwarning("分发", "请先把窗口加入被分发对象列表。", parent=self.root)
            return
        snd = self._dist_resolve_sender()
        if snd is None:
            return
        a_iid, name_a = snd
        sel = list(self.dist_recv_list.curselection())
        idx = int(sel[0]) if sel else 0
        if idx < 0 or idx >= len(self._dist_recv_entries):
            messagebox.showwarning("分发", "被分发对象无效。", parent=self.root)
            return
        entry = self._dist_recv_entries[idx]
        b_iid = entry["iid"]
        if a_iid == b_iid:
            messagebox.showwarning(
                "分发", "分发者与被分发对象不能是同一窗口。", parent=self.root
            )
            return
        params = self._dist_params()
        if params is None:
            return
        keyword, stack, qty, from_bank, to_bank = params
        st_a0 = ipc.read_state(a_iid) or {}
        st_b0 = ipc.read_state(b_iid) or {}
        uid_a = self._trade_uid_from_state(st_a0)
        uid_b = self._trade_uid_from_state(st_b0)
        name_b = str(st_b0.get("name") or entry.get("name") or b_iid)
        if not uid_a or not uid_b:
            messagebox.showerror(
                "分发",
                "读不到双方队长 uid。\n分发者=" + name_a + " 接收=" + name_b,
                parent=self.root,
            )
            return

        self._dist_gen = getattr(self, "_dist_gen", 0) + 1
        gen = self._dist_gen
        self._dist_active_iids = [a_iid, b_iid]
        self._dist_reset_recv_colors("idle")
        self._dist_paint_recv_row(idx, "run")
        self._set_dist_result(
            "单次分发：" + name_a + " → " + name_b
            + " 「" + keyword + "」×" + str(qty) + "格…"
        )

        def work() -> None:
            lines: list[str] = []

            def paint() -> None:
                if gen != self._dist_gen:
                    return
                self._set_dist_result("\n".join(lines) if lines else "…")

            result = self._run_one_dist_to_receiver(
                a_iid,
                name_a,
                b_iid,
                name_b,
                uid_a,
                uid_b,
                keyword,
                stack,
                qty,
                from_bank,
                to_bank,
                lines,
                paint,
                gen,
            )
            if gen != self._dist_gen:
                return
            if result == "done":
                self._dist_paint_recv_row(idx, "done")
                lines.append("单次分发结束：已交满")
            elif result == "partial":
                self._dist_paint_recv_row(idx, "done")
                lines.append("单次分发结束：部分完成（空格不足等）")
            elif result == "empty":
                self._dist_paint_recv_row(idx, "done")
                lines.append("单次分发结束：分发者已空")
            elif result == "cancel":
                lines.append("单次分发已取消")
            else:
                self._dist_paint_recv_row(idx, "fail")
                lines.append("单次分发失败")
            self.root.after(0, paint)

        threading.Thread(target=work, daemon=True).start()

    def start_all_dists(self) -> None:
        if not self._dist_recv_entries:
            messagebox.showwarning("分发", "请先把窗口加入被分发对象列表。", parent=self.root)
            return
        snd = self._dist_resolve_sender()
        if snd is None:
            return
        a_iid, name_a = snd
        for e in self._dist_recv_entries:
            if e["iid"] == a_iid:
                messagebox.showwarning(
                    "分发",
                    "分发者不能出现在被分发列表中：" + (e.get("name") or e["iid"]),
                    parent=self.root,
                )
                return
        params = self._dist_params()
        if params is None:
            return
        keyword, stack, qty, from_bank, to_bank = params

        st_a0 = ipc.read_state(a_iid) or {}
        uid_a = self._trade_uid_from_state(st_a0)
        if not uid_a:
            messagebox.showerror("分发", "读不到分发者队长 uid。", parent=self.root)
            return

        recvs = list(self._dist_recv_entries)
        self._dist_gen = getattr(self, "_dist_gen", 0) + 1
        gen = self._dist_gen
        self._dist_active_iids = [a_iid] + [e["iid"] for e in recvs]
        self._dist_reset_recv_colors("wait")
        self._set_dist_result(
            "全部分发：" + name_a + " → " + str(len(recvs)) + " 人"
            + " 「" + keyword + "」各" + str(qty) + "格…"
        )

        def work() -> None:
            lines: list[str] = []

            def paint() -> None:
                if gen != self._dist_gen:
                    return
                self._set_dist_result("\n".join(lines) if lines else "…")

            for i, entry in enumerate(recvs):
                if gen != self._dist_gen:
                    return
                b_iid = entry["iid"]
                st_b = ipc.read_state(b_iid) or {}
                name_b = str(st_b.get("name") or entry.get("name") or b_iid)
                uid_b = self._trade_uid_from_state(st_b)
                lines.append(
                    "======== 被分发 " + str(i + 1) + "/" + str(len(recvs))
                    + " " + name_b + " ========"
                )
                self.root.after(0, paint)
                if not uid_b:
                    lines.append("失败：" + name_b + " 无队长 uid，跳过")
                    self._dist_paint_recv_row(i, "fail")
                    self.root.after(0, paint)
                    continue
                self._dist_paint_recv_row(i, "run")
                result = self._run_one_dist_to_receiver(
                    a_iid,
                    name_a,
                    b_iid,
                    name_b,
                    uid_a,
                    uid_b,
                    keyword,
                    stack,
                    qty,
                    from_bank,
                    to_bank,
                    lines,
                    paint,
                    gen,
                )
                if gen != self._dist_gen:
                    return
                if result == "done":
                    self._dist_paint_recv_row(i, "done")
                    lines.append(name_b + " 已交满，下一个…")
                    self.root.after(0, paint)
                    continue
                if result == "partial":
                    self._dist_paint_recv_row(i, "done")
                    lines.append(name_b + " 部分完成，继续下一人…")
                    self.root.after(0, paint)
                    continue
                if result == "empty":
                    self._dist_paint_recv_row(i, "done")
                    lines.append("全部分发结束：分发者已空（后续未跑）")
                    self.root.after(0, paint)
                    return
                if result == "cancel":
                    lines.append("全部分发已取消")
                    self.root.after(0, paint)
                    return
                # 硬失败：记红继续下一人（与「第二轮失败也继续」一致）
                self._dist_paint_recv_row(i, "fail")
                lines.append(name_b + " 失败，继续下一人…")
                self.root.after(0, paint)

            lines.append("全部分发完成")
            self.root.after(0, paint)

        threading.Thread(target=work, daemon=True).start()

    def broadcast_start_encounter(self) -> None:
        self._broadcast_script("开始遇敌", "start_encounter", instant=True)

    def broadcast_stop_encounter(self) -> None:
        self._broadcast_script("停止遇敌", "stop_encounter")

    def broadcast_fast_punch(self) -> None:
        self._broadcast_script("一键打卡", "fast_punch", instant=True)

    def broadcast_save_gold(self) -> None:
        self._broadcast_script("存钱", "save_gold")

    def broadcast_skip_anim(self) -> None:
        self._broadcast_script("打开跳过动画", "skip_anim", instant=True)

    def _require_accounts(self) -> list[AccountProfile] | None:
        accounts = load_accounts()
        if not accounts:
            messagebox.showwarning("无账号", "账号库为空，请先录入账号。")
            return None
        return accounts

    def batch_login_fetch(self) -> None:
        """批量：按账号库顺序对每个已注入桥接的实例 登录→进游戏→拉起离线多控。"""
        if not self._warn_if_bridge_missing():
            return
        accounts = self._require_accounts()
        if accounts is None:
            return
        iids = self._live_bridge_instances()
        if not iids:
            messagebox.showwarning(
                "无实例",
                "没有检测到已注入精简桥接的实例。\n请先启动游戏并确保已注入精简桥接。",
            )
            return
        if len(iids) > len(accounts):
            messagebox.showwarning(
                "账号不足",
                f"当前 {len(iids)} 个实例，但账号库只有 {len(accounts)} 个账号。\n"
                "多余实例将跳过登录。",
            )
        pairs = list(zip(iids, accounts))
        self._set_batch_busy(True, f"正在批量登录拉取 {len(pairs)} 个实例…")
        threading.Thread(target=self._batch_login_worker, args=(pairs,), daemon=True).start()

    def _batch_login_worker(self, pairs: list[tuple[str, AccountProfile]]) -> None:
        ok_count = 0
        lines: list[str] = []
        for iid, acc in pairs:
            label = acc.label or acc.phone
            st = ipc.read_state(iid) or {}
            phase = st.get("phase", "")
            try:
                if int(st.get("team_num") or 0) >= 2:
                    ok_count += 1
                    lines.append(f"[OK] {label}: 已在队伍中，跳过拉取")
                    self._set_batch_status(f"[{label}] 已组队，视为成功")
                    self._maybe_open_helper(iid, label)
                    continue
                if phase == "in_game":
                    self._set_batch_status(f"[{label}] 已进游戏，先开助手再拉多控…")
                    self._maybe_open_helper(iid, label)
                    ipc.multi_login_offline_all(iid)
                    multi_ok = ipc.wait_multi_ready(iid, timeout=120)
                else:
                    self._set_batch_status(f"[{label}] 登录→进游戏(开助手)→拉起多控…")
                    ipc.workflow_login_enter(
                        iid,
                        acc.phone,
                        acc.password,
                        open_helper=self._want_auto_open_helper(),
                    )
                    entered = ipc.wait_for_in_game(iid, timeout=300)
                    if not entered:
                        ok, msg = ipc.wait_workflow_done(iid, timeout=60)
                        if not ok:
                            lines.append(f"[FAIL] {label}: 登录/进游戏失败 {msg}")
                            self._set_batch_status(f"[{label}] 失败")
                            continue
                    after = ipc.read_state(iid) or {}
                    if int(after.get("team_num") or 0) >= 2:
                        ok_count += 1
                        lines.append(f"[OK] {label}: 已在队伍中，跳过拉取")
                        self._set_batch_status(f"[{label}] 已组队，视为成功")
                        continue
                    ipc.multi_login_offline_all(iid)
                    multi_ok = ipc.wait_multi_ready(iid, timeout=120)
                if multi_ok:
                    ok_count += 1
                    team_n = int((ipc.read_state(iid) or {}).get("team_num") or 0)
                    if team_n >= 2:
                        lines.append(f"[OK] {label}: 已在队伍中")
                    else:
                        lines.append(f"[OK] {label}: 已进游戏并拉起多控")
                    self._set_batch_status(f"[{label}] 完成")
                else:
                    lines.append(f"[WARN] {label}: 已进游戏但多控未全部上线（可稍后一键召唤）")
                    self._set_batch_status(f"[{label}] 多控未全上线")
            except Exception as exc:
                lines.append(f"[FAIL] {label}: {type(exc).__name__}: {exc}")
                self._set_batch_status(f"[{label}] 异常")
        summary = f"批量登录拉取完成：成功 {ok_count}/{len(pairs)}"
        if lines:
            summary += "\n" + "\n".join(lines)
        self._set_batch_done(summary)

    def batch_summon(self) -> None:
        """批量：对所有实例发一键召唤（协议），按 team≥5 判定聚齐。"""
        iids = self._live_bridge_instances()
        if not iids:
            messagebox.showwarning("无实例", "没有检测到已注入桥接的实例。")
            return
        self._set_batch_busy(True, f"正在一键召唤 {len(iids)} 个实例…")
        threading.Thread(target=self._batch_summon_worker, args=(iids,), daemon=True).start()

    def _batch_summon_worker(self, iids: list[str]) -> None:
        ok_count = 0
        lines: list[str] = []
        for iid in iids:
            st = ipc.read_state(iid) or {}
            if int(st.get("team_num") or 0) >= 2:
                ok_count += 1
                lines.append(f"[OK] {iid}: 已在队伍中，跳过召唤")
                self._maybe_open_helper(iid, iid)
                continue
            self._set_batch_status(f"[{iid}] 一键召唤…")
            self._maybe_open_helper(iid, iid)
            ipc.one_key_summon(iid)
            ok = ipc.wait_for_team(iid, timeout=90)
            if ok:
                ok_count += 1
                lines.append(f"[OK] {iid}: 队伍聚齐 (team≥5)")
            else:
                # 补一发队伍召集再试
                ipc.team_gather(iid)
                ok2 = ipc.wait_for_team(iid, timeout=60)
                if ok2:
                    ok_count += 1
                    lines.append(f"[OK] {iid}: 队伍召集后聚齐")
                else:
                    lines.append(f"[FAIL] {iid}: 召唤/召集未聚齐")
        summary = f"一键召唤完成：成功 {ok_count}/{len(iids)}"
        if lines:
            summary += "\n" + "\n".join(lines)
        self._set_batch_done(summary)

    def _set_batch_busy(self, busy: bool, status: str) -> None:
        state = ("disabled" if busy else "normal")
        for btn in (self.batch_login_btn, self.batch_summon_btn):
            btn.config(state=state)
        self.batch_status_var.set(status)

    def _set_batch_status(self, text: str) -> None:
        self.root.after(0, lambda: self.batch_status_var.set(text))

    def _set_batch_done(self, text: str) -> None:
        def _finish() -> None:
            self._set_batch_busy(False, "批量状态：就绪")
            self.batch_status_var.set(text)
        self.root.after(0, _finish)


def find_account_by_query(query: str) -> AccountProfile:
    q = (query or "").strip()
    if not q:
        raise ValueError("账号名为空")
    accounts = load_accounts()
    hits = [a for a in accounts if a.label == q or a.phone == q]
    if not hits:
        hits = [a for a in accounts if q in (a.label or "")]
    if not hits:
        names = "、".join((a.label or a.phone) for a in accounts) or "(空)"
        raise ValueError(f"账号库没有「{q}」。现有：{names}")
    if len(hits) > 1:
        names = "、".join(a.label or a.phone for a in hits)
        raise ValueError(f"「{q}」匹配到多个账号：{names}")
    return hits[0]


def cli_launch_and_login(label: str, *, login_only: bool = False) -> int:
    """不打开 GUI：启动一个号并一键登录。默认登录后拉离线多控并一键召唤。"""
    acc = find_account_by_query(label)
    root = get_game_root()
    name = acc.label or acc.phone
    phone_mask = (acc.phone[:3] + "***") if acc.phone else ""
    print(f"[INFO] 游戏目录 {root}", flush=True)
    print(f"[INFO] 账号 {name} {phone_mask}", flush=True)
    if not is_mini_bridge_ready(root):
        print("[FAIL] 当前游戏目录未注入精简桥接，无法自动登录", flush=True)
        return 1

    inst = launch_game(root)
    print(f"[OK] 已启动 pid={inst.pid} instance_id={inst.instance_id}", flush=True)
    print(f"PID={inst.pid}", flush=True)

    print(f"[INFO] [{name}] 等待精简桥接…", flush=True)
    if not ipc.wait_for_bridge(inst.instance_id, timeout=240):
        print(f"[FAIL] [{name}] 桥接连接超时 pid={inst.pid}", flush=True)
        return 2

    if login_only:
        print(f"[INFO] [{name}] 一键登录…", flush=True)
        want_helper = bool(load_settings().get("auto_open_helper"))
        ipc.workflow_login_enter(
            inst.instance_id,
            acc.phone,
            acc.password,
            open_helper=want_helper,
        )
        ok, msg, _st = ipc.wait_for_in_game(inst.instance_id, timeout=300)
        if ok:
            uid = str(msg or "")
            tail = uid[-4:] if len(uid) > 4 else uid
            print(f"[OK] [{name}] 已进游戏 pid={inst.pid} uid尾={tail}", flush=True)
            return 0
        print(f"[FAIL] [{name}] 登录失败 {msg} pid={inst.pid}", flush=True)
        return 3

    print(f"[INFO] [{name}] 登录→开助手→拉多控→召唤…", flush=True)
    want_helper = bool(load_settings().get("auto_open_helper"))
    ipc.workflow_step1_five_chars(
        inst.instance_id,
        acc.phone,
        acc.password,
        open_helper=want_helper,
    )
    ok, msg = ipc.wait_workflow_done(inst.instance_id, timeout=600)
    if ok:
        print(f"[OK] [{name}] 流程完成 pid={inst.pid}", flush=True)
        return 0
    print(f"[FAIL] [{name}] {msg} pid={inst.pid}", flush=True)
    return 3


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=APP_TITLE)
    parser.add_argument(
        "--launch",
        metavar="账号备注",
        help="启动指定账号：一键登录并拉多控+召唤（不打开 GUI）",
    )
    parser.add_argument(
        "--login-only",
        action="store_true",
        help="只登录进游戏，不拉离线多控、不召唤",
    )
    args = parser.parse_args(argv)
    if args.launch:
        try:
            return cli_launch_and_login(args.launch, login_only=args.login_only)
        except Exception as exc:
            print(f"[FAIL] {type(exc).__name__}: {exc}", flush=True)
            return 1
    if not ensure_single_instance(
        APP_TITLE,
        lock_key=CENTRAL_CONTROL_LOCK_KEY,
        message=(
            f"{APP_TITLE} 已在运行，不能重复打开。\n\n"
            "本程序仅允许运行一个窗口。"
        ),
    ):
        return 1
    app = CentralControlApp()
    app.root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
