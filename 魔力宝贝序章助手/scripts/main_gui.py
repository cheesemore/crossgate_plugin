#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章助手 — 单实例控制（登录 / 五控 / 召唤）。"""
from __future__ import annotations

import json
import re
import sys
import threading
import tkinter as tk
from collections.abc import Callable
from pathlib import Path
from tkinter import messagebox, scrolledtext, simpledialog, ttk

SHARED = Path(__file__).resolve().parents[2] / "序章助手共享"
sys.path.insert(0, str(SHARED))

from assistant_common.config import DATA_DIR  # noqa: E402
from assistant_common import ipc  # noqa: E402
from assistant_common import waypoints  # noqa: E402
from assistant_common import npc_bookmarks  # noqa: E402
from assistant_common import game as game_util  # noqa: E402
from assistant_common.customer_panels import CUSTOMER_PANELS  # noqa: E402


class SeqChapterAssistantApp:
    def __init__(
        self,
        instance_id: str | None = None,
        phone: str | None = None,
        password: str | None = None,
        *,
        auto_workflow: bool = False,
    ) -> None:
        self.root = tk.Tk()
        self.root.title("序章助手")
        self.root.geometry("580x920")
        self.root.minsize(520, 760)

        self.instance_id = tk.StringVar(value=instance_id or "")
        self.phone_var = tk.StringVar(value=phone or "")
        self.password_var = tk.StringVar(value=password or "")
        self.status_var = tk.StringVar(value="就绪")
        self.bridge_var = tk.StringVar(value="桥接：未检测")
        self.bridge_detail_var = tk.StringVar(value="")

        self._auto_mode = bool(auto_workflow)
        self._auto_started = False
        self._auto_running = False
        self._auto_finished = False
        self._bound_instance_id = (instance_id or "").strip()
        self._last_bridge_summary = ""
        self._action_widgets: list[tk.Widget] = []
        self._last_pos_key = ""
        self._last_pos_floor = None
        self._last_npc_floor = None
        self._last_npc_signature = ""
        self._selected_npc_objindex = ""
        self._selected_waypoint_id: str | None = None
        self._packet_burst_active = False
        self._packet_burst_after_id: str | None = None
        self._use_item_seq_active = False
        self._use_item_seq_after_id: str | None = None
        self._use_item_seq_state: dict | None = None
        self._special_pkg_active = False
        self._special_pkg_after_id: str | None = None
        self._special_pkg_state: dict | None = None
        self._char_options: list[tuple[str, str]] = []
        self._last_bridge_state: dict = {}

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text="序章助手", font=("Microsoft YaHei UI", 12, "bold")).pack(anchor=tk.W)
        ttk.Label(
            outer,
            text="通过游戏内桥接调用 Login / TeamManager 等方法，不模拟点击。",
            foreground="#555",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 8))

        notebook = ttk.Notebook(outer)
        notebook.pack(fill=tk.BOTH, expand=True)

        tab_ops = ttk.Frame(notebook, padding=4)
        tab_game = ttk.Frame(notebook, padding=4)
        tab_nav = ttk.Frame(notebook, padding=4)
        tab_npc = ttk.Frame(notebook, padding=4)
        tab_packet = ttk.Frame(notebook, padding=4)
        tab_panels = ttk.Frame(notebook, padding=4)
        notebook.add(tab_ops, text="操作")
        notebook.add(tab_game, text="游戏信息")
        notebook.add(tab_nav, text="寻路")
        notebook.add(tab_npc, text="NPC")
        notebook.add(tab_packet, text="发包")
        notebook.add(tab_panels, text="打开面板")

        self._build_ops_tab(tab_ops)
        self._build_game_tab(tab_game)
        self._build_nav_tab(tab_nav)
        self._build_npc_tab(tab_npc)
        self._build_packet_tab(tab_packet)
        self._build_panel_tab(tab_panels)

        ttk.Label(outer, textvariable=self.status_var).pack(anchor=tk.W, pady=(6, 0))

        if self._auto_mode:
            self._set_actions_locked(True)
            self.status_var.set("等待游戏桥接…")

        self.refresh_bridge()
        self._schedule_bridge_refresh()

    def _build_ops_tab(self, parent: ttk.Frame) -> None:
        if self._auto_mode:
            ttk.Label(
                parent,
                text="多开器已启动：桥接就绪后将自动登录 → 进游戏 → 五控 → 召唤",
                foreground="#0066aa",
                wraplength=480,
            ).pack(anchor=tk.W, pady=(0, 6))

        inst_frm = ttk.LabelFrame(parent, text="游戏实例", padding=8)
        inst_frm.pack(fill=tk.X, pady=(0, 8))
        row = ttk.Frame(inst_frm)
        row.pack(fill=tk.X)
        ttk.Label(row, text="实例 ID:").pack(side=tk.LEFT)
        self.instance_entry = ttk.Entry(row, textvariable=self.instance_id, width=28)
        self.instance_entry.pack(side=tk.LEFT, padx=6)
        ttk.Button(row, text="当前窗口", command=self.on_pick_foreground_window, width=9).pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(row, text="选择窗口…", command=self.on_pick_game_window, width=9).pack(side=tk.LEFT, padx=(0, 4))
        refresh_btn = ttk.Button(row, text="刷新桥接", command=lambda: self.refresh_bridge(verbose=True), width=10)
        refresh_btn.pack(side=tk.LEFT)
        ttk.Label(
            inst_frm,
            text="先点游戏窗口使其前台，或「选择窗口…」按标题匹配，再点「刷新桥接」。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=480,
        ).pack(anchor=tk.W, pady=(4, 0))

        ttk.Label(inst_frm, textvariable=self.bridge_var, foreground="#0066aa").pack(anchor=tk.W, pady=(6, 0))
        ttk.Label(
            inst_frm,
            textvariable=self.bridge_detail_var,
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=480,
        ).pack(anchor=tk.W)
        ttk.Label(
            inst_frm,
            text=f"IPC 目录: {DATA_DIR / 'instances'}",
            font=("Consolas", 8),
            foreground="#888",
        ).pack(anchor=tk.W)

        login_frm = ttk.LabelFrame(parent, text="账号登录", padding=8)
        login_frm.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(login_frm, text="手机号").grid(row=0, column=0, sticky=tk.W, pady=2)
        phone_entry = ttk.Entry(login_frm, textvariable=self.phone_var, width=36)
        phone_entry.grid(row=0, column=1, sticky=tk.EW, pady=2)
        ttk.Label(login_frm, text="密码").grid(row=1, column=0, sticky=tk.W, pady=2)
        pwd_entry = ttk.Entry(login_frm, textvariable=self.password_var, show="*", width=36)
        pwd_entry.grid(row=1, column=1, sticky=tk.EW, pady=2)
        login_frm.columnconfigure(1, weight=1)
        login_btn = ttk.Button(login_frm, text="一键登录", command=self.on_login)
        login_btn.grid(row=2, column=1, sticky=tk.E, pady=(8, 0))

        step_frm = ttk.LabelFrame(parent, text="一机五控 · 第一步", padding=8)
        step_frm.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(
            step_frm,
            text="目标：5 角色上线 → 点头像选第 1 个 → 一键召唤（已组队则跳过）",
            wraplength=460,
        ).pack(anchor=tk.W, pady=(0, 6))

        btn_row = ttk.Frame(step_frm)
        btn_row.pack(fill=tk.X)
        multi_btn = ttk.Button(btn_row, text="拉起 5 角色", command=self.on_multi_login, width=14)
        multi_btn.pack(side=tk.LEFT, padx=(0, 6))
        head_btn = ttk.Button(
            btn_row,
            text="点头像 #1",
            command=lambda: self.on_click_multi_head(0),
            width=12,
        )
        head_btn.pack(side=tk.LEFT, padx=(0, 6))
        select_btn = ttk.Button(
            btn_row,
            text="选中 #1",
            command=lambda: self.on_select_multi_char(0),
            width=10,
        )
        select_btn.pack(side=tk.LEFT, padx=(0, 6))
        summon_btn = ttk.Button(btn_row, text="一键召唤", command=self.on_summon, width=12)
        summon_btn.pack(side=tk.LEFT)

        workflow_btn = ttk.Button(
            step_frm,
            text="▶ 一键完成第一步（登录+五控+点头像+召唤）",
            command=self.on_workflow_step1,
        )
        workflow_btn.pack(fill=tk.X, pady=(10, 0))

        misc_frm = ttk.LabelFrame(parent, text="单步操作", padding=8)
        misc_frm.pack(fill=tk.X, pady=(0, 8))
        enter_btn = ttk.Button(misc_frm, text="进入游戏（选服后）", command=self.on_enter_game)
        enter_btn.pack(anchor=tk.W)

        log_frm = ttk.LabelFrame(parent, text="日志", padding=6)
        log_frm.pack(fill=tk.BOTH, expand=True)
        self.log = scrolledtext.ScrolledText(log_frm, height=8, font=("Consolas", 9))
        self.log.pack(fill=tk.BOTH, expand=True)

        self._action_widgets = [
            self.instance_entry,
            phone_entry,
            pwd_entry,
            login_btn,
            multi_btn,
            head_btn,
            select_btn,
            summon_btn,
            workflow_btn,
            enter_btn,
        ]

    def _build_panel_tab(self, parent: ttk.Frame) -> None:
        ttk.Label(
            parent,
            text="打开序章补丁替换「客服按钮」后的游戏面板（需已注入桥接并在游戏中）。",
            foreground="#555",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 8))

        pick_frm = ttk.LabelFrame(parent, text="面板列表", padding=8)
        pick_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 8))

        self._panel_id_map: dict[str, str] = {}
        panel_labels: list[str] = []
        for row in CUSTOMER_PANELS:
            label = str(row["label"])
            panel_labels.append(label)
            self._panel_id_map[label] = str(row["id"])

        self.panel_pick_var = tk.StringVar(value=panel_labels[0] if panel_labels else "")
        ttk.Label(pick_frm, text="选择面板:").pack(anchor=tk.W)
        self.panel_pick_cb = ttk.Combobox(
            pick_frm,
            textvariable=self.panel_pick_var,
            values=panel_labels,
            state="readonly",
            width=36,
        )
        self.panel_pick_cb.pack(fill=tk.X, pady=(4, 8))

        char_row = ttk.Frame(pick_frm)
        char_row.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(char_row, text="角色 KUid:").pack(side=tk.LEFT)
        self.panel_char_var = tk.StringVar(value="")
        self.panel_char_cb = ttk.Combobox(
            char_row,
            textvariable=self.panel_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.panel_char_cb.pack(side=tk.LEFT, padx=4, fill=tk.X, expand=True)
        ttk.Label(char_row, text="(盲盒/自动技能需要)", font=("Microsoft YaHei UI", 8), foreground="#666").pack(
            side=tk.LEFT
        )

        ttk.Button(pick_frm, text="打开面板", command=self.on_open_customer_panel, width=14).pack(anchor=tk.W)

        self.panel_open_log = scrolledtext.ScrolledText(pick_frm, height=6, font=("Consolas", 9))
        self.panel_open_log.pack(fill=tk.BOTH, expand=True, pady=(8, 0))

        ttk.Label(
            parent,
            text="说明：多数面板直接 Open()；盲盒先发「获取数据」等回包；无尽之塔会切 BOSS 挑战 Tab。",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
            wraplength=500,
        ).pack(anchor=tk.W)

    def on_open_customer_panel(self) -> None:
        iid = self._require_instance()
        if not iid:
            return
        label = self.panel_pick_var.get().strip()
        panel_id = self._panel_id_map.get(label)
        if not panel_id:
            messagebox.showwarning("未选择", "请选择要打开的面板")
            return
        uid = self._char_label_to_uid(self.panel_char_var.get())

        def work() -> None:
            ok = False
            msg = ""
            try:
                req = ipc.open_panel(iid, panel_id, uid=uid or None)
                ok, ack = ipc.wait_ack(iid, req, timeout=45)
                msg = (ack or {}).get("msg", "") if ack else ""
            except Exception as exc:
                msg = str(exc)

            line = f"[{'OK' if ok else 'FAIL'}] {label} ({panel_id}): {msg}\n"

            def append() -> None:
                self.panel_open_log.insert(tk.END, line)
                self.panel_open_log.see(tk.END)

            self.root.after(0, append)

        threading.Thread(target=work, daemon=True).start()

    def _build_packet_tab(self, parent: ttk.Frame) -> None:
        ttk.Label(
            parent,
            text="选择包类型后填写对应字段；X/Y 等自动项由心跳更新。",
            foreground="#555",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 8))

        type_frm = ttk.Frame(parent)
        type_frm.pack(fill=tk.X, pady=(0, 8))
        ttk.Label(type_frm, text="包类型:").pack(side=tk.LEFT)
        self._packet_type_map = {
            "使用道具": "use_item",
            "连续使用道具": "use_item_seq",
            "开特殊包裹": "special_pkg",
            "镶嵌宝石": "gem_inlay",
            "学技能": "learn_skill",
            "宠物学技能": "learn_pet_skill",
            "战斗封印": "battle_seal",
            "宠物改造": "reform",
            "Protobuf 通用": "proto",
            "GM 命令": "gm",
        }
        self.packet_type_var = tk.StringVar(value="使用道具")
        type_cb = ttk.Combobox(
            type_frm,
            textvariable=self.packet_type_var,
            values=list(self._packet_type_map.keys()),
            state="readonly",
            width=18,
        )
        type_cb.pack(side=tk.LEFT, padx=6)
        type_cb.bind("<<ComboboxSelected>>", lambda _e: self._show_packet_form())

        self.packet_form_host = ttk.Frame(parent)
        self.packet_form_host.pack(fill=tk.BOTH, expand=True)

        # --- 使用道具 ---
        self.packet_use_item_frm = ttk.LabelFrame(self.packet_form_host, text="使用道具", padding=8)
        slot_row = ttk.Frame(self.packet_use_item_frm)
        slot_row.pack(fill=tk.X, pady=2)
        ttk.Label(slot_row, text="背包格:").pack(side=tk.LEFT)
        self.packet_slot_var = tk.StringVar(value="1")
        slot_entry = ttk.Entry(slot_row, textvariable=self.packet_slot_var, width=6)
        slot_entry.pack(side=tk.LEFT, padx=(4, 8))
        ttk.Label(
            slot_row,
            text="填 1 = 游戏格 8（+7），最小 1",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)
        slot_entry.bind("<FocusOut>", lambda _e: self._normalize_packet_slot())
        slot_entry.bind("<Return>", lambda _e: self._normalize_packet_slot())

        count_row = ttk.Frame(self.packet_use_item_frm)
        count_row.pack(fill=tk.X, pady=2)
        ttk.Label(count_row, text="数量:").pack(side=tk.LEFT)
        self.packet_use_count_var = tk.StringVar(value="1")
        ttk.Entry(count_row, textvariable=self.packet_use_count_var, width=6).pack(side=tk.LEFT, padx=(4, 8))
        ttk.Label(
            count_row,
            text="Usecount，任意整数（含 -1）",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        char_row = ttk.Frame(self.packet_use_item_frm)
        char_row.pack(fill=tk.X, pady=(8, 2))
        ttk.Label(char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_char_var = tk.StringVar(value="")
        self.packet_char_cb = ttk.Combobox(
            char_row,
            textvariable=self.packet_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        self.packet_pos_var = tk.StringVar(value="坐标：等待心跳…")
        ttk.Label(
            self.packet_use_item_frm,
            textvariable=self.packet_pos_var,
            font=("Consolas", 9),
            foreground="#0066aa",
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            self.packet_use_item_frm,
            text="协议 16 · Proto_CS_UseItem · Toindex=0 · Selectindex=-1",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
        ).pack(anchor=tk.W, pady=(4, 0))

        # --- 连续使用道具（任务序列） ---
        self.packet_use_item_seq_frm = ttk.LabelFrame(
            self.packet_form_host, text="连续使用道具（按任务顺序）", padding=8
        )
        seq_char_row = ttk.Frame(self.packet_use_item_seq_frm)
        seq_char_row.pack(fill=tk.X, pady=(0, 6))
        ttk.Label(seq_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_seq_char_var = tk.StringVar(value="")
        self.packet_seq_char_cb = ttk.Combobox(
            seq_char_row,
            textvariable=self.packet_seq_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_seq_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        ttk.Label(
            self.packet_use_item_seq_frm,
            text="每行一条任务：背包格,次数（格号同「使用道具」，填 41 = 游戏格 48）",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(anchor=tk.W)
        ttk.Label(
            self.packet_use_item_seq_frm,
            text="示例：41,99  然后  42,99",
            font=("Consolas", 8),
            foreground="#888",
        ).pack(anchor=tk.W, pady=(0, 4))
        self.packet_seq_tasks_text = scrolledtext.ScrolledText(
            self.packet_use_item_seq_frm, height=6, font=("Consolas", 9)
        )
        self.packet_seq_tasks_text.pack(fill=tk.BOTH, expand=True, pady=(0, 6))
        self.packet_seq_tasks_text.insert(tk.END, "41,99\n42,99")

        self.packet_seq_pos_var = tk.StringVar(value="坐标：等待心跳…")
        ttk.Label(
            self.packet_use_item_seq_frm,
            textvariable=self.packet_seq_pos_var,
            font=("Consolas", 9),
            foreground="#0066aa",
        ).pack(anchor=tk.W, pady=(0, 6))

        seq_btn_row = ttk.Frame(self.packet_use_item_seq_frm)
        seq_btn_row.pack(fill=tk.X)
        ttk.Label(seq_btn_row, text="间隔(ms):").pack(side=tk.LEFT)
        self.packet_seq_interval_var = tk.StringVar(value="500")
        ttk.Entry(seq_btn_row, textvariable=self.packet_seq_interval_var, width=8).pack(
            side=tk.LEFT, padx=(4, 12)
        )
        self.packet_seq_start_btn = ttk.Button(
            seq_btn_row, text="开始任务", command=self.on_use_item_seq_start, width=10
        )
        self.packet_seq_start_btn.pack(side=tk.LEFT, padx=(0, 6))
        self.packet_seq_cancel_btn = ttk.Button(
            seq_btn_row,
            text="取消任务",
            command=self.on_use_item_seq_cancel,
            width=10,
            state=tk.DISABLED,
        )
        self.packet_seq_cancel_btn.pack(side=tk.LEFT)
        self.packet_seq_status_var = tk.StringVar(value="")
        ttk.Label(
            self.packet_use_item_seq_frm,
            textvariable=self.packet_seq_status_var,
            font=("Microsoft YaHei UI", 8),
            foreground="#0066aa",
        ).pack(anchor=tk.W, pady=(6, 0))

        # --- 开特殊包裹 ---
        self.packet_special_pkg_frm = ttk.LabelFrame(
            self.packet_form_host, text="开特殊包裹（使用 + 逐格存账号银行）", padding=8
        )
        sp_char_row = ttk.Frame(self.packet_special_pkg_frm)
        sp_char_row.pack(fill=tk.X, pady=(0, 6))
        ttk.Label(sp_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_sp_char_var = tk.StringVar(value="")
        self.packet_sp_char_cb = ttk.Combobox(
            sp_char_row,
            textvariable=self.packet_sp_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_sp_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        sp_range_row = ttk.Frame(self.packet_special_pkg_frm)
        sp_range_row.pack(fill=tk.X, pady=2)
        ttk.Label(sp_range_row, text="起始格 A:").pack(side=tk.LEFT)
        self.packet_sp_a_var = tk.StringVar(value="22")
        ttk.Entry(sp_range_row, textvariable=self.packet_sp_a_var, width=6).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(sp_range_row, text="结束格 B:").pack(side=tk.LEFT)
        self.packet_sp_b_var = tk.StringVar(value="55")
        ttk.Entry(sp_range_row, textvariable=self.packet_sp_b_var, width=6).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(sp_range_row, text="每格使用次数:").pack(side=tk.LEFT)
        self.packet_sp_use_count_var = tk.StringVar(value="99")
        ttk.Entry(sp_range_row, textvariable=self.packet_sp_use_count_var, width=6).pack(side=tk.LEFT, padx=(4, 0))

        ttk.Label(
            self.packet_special_pkg_frm,
            text="格号为游戏 Haveitemindex（与协议一致）。每轮：使用当前格 N 次 → 逐格存 8..A-1 到账号银行 → 下一格，直到 B。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=520,
        ).pack(anchor=tk.W, pady=(4, 4))

        self.packet_sp_pos_var = tk.StringVar(value="坐标：等待心跳…")
        ttk.Label(
            self.packet_special_pkg_frm,
            textvariable=self.packet_sp_pos_var,
            font=("Consolas", 9),
            foreground="#0066aa",
        ).pack(anchor=tk.W, pady=(0, 6))

        sp_btn_row = ttk.Frame(self.packet_special_pkg_frm)
        sp_btn_row.pack(fill=tk.X)
        ttk.Label(sp_btn_row, text="间隔(ms):").pack(side=tk.LEFT)
        self.packet_sp_interval_var = tk.StringVar(value="500")
        ttk.Entry(sp_btn_row, textvariable=self.packet_sp_interval_var, width=8).pack(
            side=tk.LEFT, padx=(4, 12)
        )
        self.packet_sp_start_btn = ttk.Button(
            sp_btn_row, text="开始", command=self.on_special_pkg_start, width=10
        )
        self.packet_sp_start_btn.pack(side=tk.LEFT, padx=(0, 6))
        self.packet_sp_cancel_btn = ttk.Button(
            sp_btn_row,
            text="取消",
            command=self.on_special_pkg_cancel,
            width=10,
            state=tk.DISABLED,
        )
        self.packet_sp_cancel_btn.pack(side=tk.LEFT)
        self.packet_sp_status_var = tk.StringVar(value="")
        ttk.Label(
            self.packet_special_pkg_frm,
            textvariable=self.packet_sp_status_var,
            font=("Microsoft YaHei UI", 8),
            foreground="#0066aa",
        ).pack(anchor=tk.W, pady=(6, 0))

        # --- 镶嵌宝石 ---
        self.packet_gem_inlay_frm = ttk.LabelFrame(
            self.packet_form_host, text="镶嵌宝石", padding=8
        )
        gem_pos_row = ttk.Frame(self.packet_gem_inlay_frm)
        gem_pos_row.pack(fill=tk.X, pady=2)
        ttk.Label(gem_pos_row, text="装备位置:").pack(side=tk.LEFT)
        self.packet_gem_equip_var = tk.StringVar(value="0")
        ttk.Entry(gem_pos_row, textvariable=self.packet_gem_equip_var, width=6).pack(
            side=tk.LEFT, padx=(4, 12)
        )
        ttk.Label(gem_pos_row, text="宝石位置:").pack(side=tk.LEFT)
        self.packet_gem_stone_var = tk.StringVar(value="8")
        ttk.Entry(gem_pos_row, textvariable=self.packet_gem_stone_var, width=6).pack(
            side=tk.LEFT, padx=(4, 0)
        )

        ttk.Label(
            self.packet_gem_inlay_frm,
            text="均为游戏 Haveitemindex：身上装备 0~7，背包从 8 起。其它字段用默认（ItemCostType/MakeStoreType=0）。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=520,
        ).pack(anchor=tk.W, pady=(4, 6))

        gem_char_row = ttk.Frame(self.packet_gem_inlay_frm)
        gem_char_row.pack(fill=tk.X, pady=(0, 2))
        ttk.Label(gem_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_gem_char_var = tk.StringVar(value="")
        self.packet_gem_char_cb = ttk.Combobox(
            gem_char_row,
            textvariable=self.packet_gem_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_gem_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        ttk.Label(
            self.packet_gem_inlay_frm,
            text="协议 1010 · Proto_CS_Recipe · Type=镶嵌宝石",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
        ).pack(anchor=tk.W, pady=(6, 0))

        # --- 学技能（高风险，需确认角色） ---
        self.packet_learn_skill_frm = ttk.LabelFrame(
            self.packet_form_host, text="学技能（人物）", padding=8
        )
        ttk.Label(
            self.packet_learn_skill_frm,
            text="协议 2000 · Proto_CS_Skill · Type=人物学习技能。错误栏位/技能会顶掉已有技能，务必核对角色。",
            font=("Microsoft YaHei UI", 8),
            foreground="#a04000",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 6))

        sk_char_row = ttk.Frame(self.packet_learn_skill_frm)
        sk_char_row.pack(fill=tk.X, pady=2)
        ttk.Label(sk_char_row, text="给谁学:").pack(side=tk.LEFT)
        self.packet_skill_char_var = tk.StringVar(value="")
        self.packet_skill_char_cb = ttk.Combobox(
            sk_char_row,
            textvariable=self.packet_skill_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_skill_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        sk_saved_row = ttk.Frame(self.packet_learn_skill_frm)
        sk_saved_row.pack(fill=tk.X, pady=2)
        ttk.Label(sk_saved_row, text="调用 NPC:").pack(side=tk.LEFT)
        self.packet_skill_saved_var = tk.StringVar(value="")
        self.packet_skill_saved_cb = ttk.Combobox(
            sk_saved_row,
            textvariable=self.packet_skill_saved_var,
            values=[],
            state="readonly",
            width=28,
        )
        self.packet_skill_saved_cb.pack(side=tk.LEFT, padx=(4, 6), fill=tk.X, expand=True)
        ttk.Button(
            sk_saved_row, text="选用", command=self.on_packet_skill_use_saved_npc, width=6
        ).pack(side=tk.LEFT)
        ttk.Button(
            sk_saved_row, text="刷新列表", command=self._refresh_skill_saved_npc_cb, width=8
        ).pack(side=tk.LEFT, padx=(4, 0))

        sk_npc_row = ttk.Frame(self.packet_learn_skill_frm)
        sk_npc_row.pack(fill=tk.X, pady=2)
        ttk.Label(sk_npc_row, text="或手输 Objindex:").pack(side=tk.LEFT)
        self.packet_skill_npc_var = tk.StringVar(value="")
        ttk.Entry(sk_npc_row, textvariable=self.packet_skill_npc_var, width=10).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Button(
            sk_npc_row, text="填入当前选中", command=self.on_packet_skill_fill_npc, width=12
        ).pack(side=tk.LEFT)
        ttk.Label(
            sk_npc_row,
            text="Objindex 会话有效，切图后请重选/重填",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
        ).pack(side=tk.LEFT, padx=(8, 0))

        sk_slot_row = ttk.Frame(self.packet_learn_skill_frm)
        sk_slot_row.pack(fill=tk.X, pady=2)
        ttk.Label(sk_slot_row, text="技能栏位:").pack(side=tk.LEFT)
        self.packet_skill_slot_var = tk.StringVar(value="0")
        ttk.Entry(sk_slot_row, textvariable=self.packet_skill_slot_var, width=6).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Label(
            sk_slot_row,
            text="Skillindex：0=第1个格子，1=第2个…",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        # 技能 ID + 右侧查询按钮
        sk_id_wrap = ttk.Frame(self.packet_learn_skill_frm)
        sk_id_wrap.pack(fill=tk.X, pady=2)
        sk_id_left = ttk.Frame(sk_id_wrap)
        sk_id_left.pack(side=tk.LEFT, fill=tk.X, expand=True)
        sk_id_row = ttk.Frame(sk_id_left)
        sk_id_row.pack(fill=tk.X)
        ttk.Label(sk_id_row, text="技能 ID:").pack(side=tk.LEFT)
        self.packet_skill_id_var = tk.StringVar(value="")
        skill_id_entry = ttk.Entry(sk_id_row, textvariable=self.packet_skill_id_var, width=8)
        skill_id_entry.pack(side=tk.LEFT, padx=(4, 8))
        skill_id_entry.bind("<KeyRelease>", lambda _e: self._refresh_skill_id_hint())
        skill_id_entry.bind("<FocusOut>", lambda _e: self._refresh_skill_id_hint())
        self.packet_skill_name_var = tk.StringVar(value="名称：-")
        ttk.Label(
            sk_id_row,
            textvariable=self.packet_skill_name_var,
            font=("Microsoft YaHei UI", 9),
            foreground="#0066aa",
        ).pack(side=tk.LEFT)
        ttk.Label(
            sk_id_left,
            text="Buyindex = 技能本体 ID。可点右侧「技能查询」检索。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(anchor=tk.W, pady=(2, 0))
        ttk.Button(
            sk_id_wrap,
            text="技能查询",
            command=lambda: self.on_open_skill_search(target="char"),
            width=10,
        ).pack(side=tk.RIGHT, padx=(8, 0), anchor=tk.N)

        sk_btn_row = ttk.Frame(self.packet_learn_skill_frm)
        sk_btn_row.pack(fill=tk.X, pady=(10, 0))
        ttk.Button(
            sk_btn_row,
            text="确认并发包（学技能）",
            command=self.on_learn_skill_send,
            width=22,
        ).pack(side=tk.LEFT)
        ttk.Label(
            sk_btn_row,
            text="会弹出二次确认：明确显示给谁学",
            font=("Microsoft YaHei UI", 8),
            foreground="#a04000",
        ).pack(side=tk.LEFT, padx=(8, 0))

        self._refresh_skill_saved_npc_cb()

        # --- 宠物学技能 ---
        self.packet_learn_pet_skill_frm = ttk.LabelFrame(
            self.packet_form_host, text="学技能（宠物）", padding=8
        )
        ttk.Label(
            self.packet_learn_pet_skill_frm,
            text="协议 2000 · Type=宠物学习技能。Buyindex 与人物共用技能本体 ID（如 63=超强补血魔法）。务必确认角色与宠物。",
            font=("Microsoft YaHei UI", 8),
            foreground="#a04000",
            wraplength=520,
        ).pack(anchor=tk.W, pady=(0, 6))

        psk_char_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_char_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_pet_skill_char_var = tk.StringVar(value="")
        self.packet_pet_skill_char_cb = ttk.Combobox(
            psk_char_row,
            textvariable=self.packet_pet_skill_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_pet_skill_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        psk_pet_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_pet_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_pet_row, text="给哪只宠物:").pack(side=tk.LEFT)
        self.packet_pet_skill_pet_var = tk.StringVar(value="")
        self.packet_pet_skill_pet_cb = ttk.Combobox(
            psk_pet_row,
            textvariable=self.packet_pet_skill_pet_var,
            values=[],
            state="readonly",
            width=28,
        )
        self.packet_pet_skill_pet_cb.pack(side=tk.LEFT, padx=(4, 6), fill=tk.X, expand=True)
        ttk.Button(
            psk_pet_row, text="刷新宠物", command=self._refresh_pet_skill_pet_cb, width=8
        ).pack(side=tk.LEFT)

        psk_pet_id_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_pet_id_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_pet_id_row, text="或手输 Petindex:").pack(side=tk.LEFT)
        self.packet_pet_skill_petindex_var = tk.StringVar(value="")
        ttk.Entry(psk_pet_id_row, textvariable=self.packet_pet_skill_petindex_var, width=8).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Label(
            psk_pet_id_row,
            text="= 宠物 data.Index（非栏位 0~4）",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        psk_saved_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_saved_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_saved_row, text="调用 NPC:").pack(side=tk.LEFT)
        self.packet_pet_skill_saved_var = tk.StringVar(value="")
        self.packet_pet_skill_saved_cb = ttk.Combobox(
            psk_saved_row,
            textvariable=self.packet_pet_skill_saved_var,
            values=[],
            state="readonly",
            width=28,
        )
        self.packet_pet_skill_saved_cb.pack(side=tk.LEFT, padx=(4, 6), fill=tk.X, expand=True)
        ttk.Button(
            psk_saved_row, text="选用", command=self.on_packet_pet_skill_use_saved_npc, width=6
        ).pack(side=tk.LEFT)

        psk_npc_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_npc_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_npc_row, text="或手输 Objindex:").pack(side=tk.LEFT)
        self.packet_pet_skill_npc_var = tk.StringVar(value="")
        ttk.Entry(psk_npc_row, textvariable=self.packet_pet_skill_npc_var, width=10).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Button(
            psk_npc_row, text="填入当前选中", command=self.on_packet_pet_skill_fill_npc, width=12
        ).pack(side=tk.LEFT)

        psk_slot_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_slot_row.pack(fill=tk.X, pady=2)
        ttk.Label(psk_slot_row, text="技能栏位:").pack(side=tk.LEFT)
        self.packet_pet_skill_slot_var = tk.StringVar(value="0")
        ttk.Entry(psk_slot_row, textvariable=self.packet_pet_skill_slot_var, width=6).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Label(
            psk_slot_row,
            text="Skillindex：0=第1个格子，1=第2个…",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        psk_id_wrap = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_id_wrap.pack(fill=tk.X, pady=2)
        psk_id_left = ttk.Frame(psk_id_wrap)
        psk_id_left.pack(side=tk.LEFT, fill=tk.X, expand=True)
        psk_id_row = ttk.Frame(psk_id_left)
        psk_id_row.pack(fill=tk.X)
        ttk.Label(psk_id_row, text="技能 ID:").pack(side=tk.LEFT)
        self.packet_pet_skill_id_var = tk.StringVar(value="")
        pet_skill_id_entry = ttk.Entry(psk_id_row, textvariable=self.packet_pet_skill_id_var, width=8)
        pet_skill_id_entry.pack(side=tk.LEFT, padx=(4, 8))
        pet_skill_id_entry.bind("<KeyRelease>", lambda _e: self._refresh_pet_skill_id_hint())
        pet_skill_id_entry.bind("<FocusOut>", lambda _e: self._refresh_pet_skill_id_hint())
        self.packet_pet_skill_name_var = tk.StringVar(value="名称：-")
        ttk.Label(
            psk_id_row,
            textvariable=self.packet_pet_skill_name_var,
            font=("Microsoft YaHei UI", 9),
            foreground="#0066aa",
        ).pack(side=tk.LEFT)
        ttk.Label(
            psk_id_left,
            text="与人物同一张技能表。例：63=超强补血魔法，57=攻击吸收。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(anchor=tk.W, pady=(2, 0))
        ttk.Button(
            psk_id_wrap,
            text="技能查询",
            command=lambda: self.on_open_skill_search(target="pet"),
            width=10,
        ).pack(side=tk.RIGHT, padx=(8, 0), anchor=tk.N)

        psk_btn_row = ttk.Frame(self.packet_learn_pet_skill_frm)
        psk_btn_row.pack(fill=tk.X, pady=(10, 0))
        ttk.Button(
            psk_btn_row,
            text="确认并发包（宠物学技能）",
            command=self.on_learn_pet_skill_send,
            width=24,
        ).pack(side=tk.LEFT)
        ttk.Label(
            psk_btn_row,
            text="二次确认：角色 + 宠物名",
            font=("Microsoft YaHei UI", 8),
            foreground="#a04000",
        ).pack(side=tk.LEFT, padx=(8, 0))

        # --- 战斗封印 ---
        self.packet_battle_seal_frm = ttk.LabelFrame(
            self.packet_form_host, text="战斗封印（扔封印卡）", padding=8
        )
        ttk.Label(
            self.packet_battle_seal_frm,
            text="需在战斗中。先「扫描背包」找封印卡格子，再发包 I|bag|target（对齐 AutoSeal：Type4–6 / Flg&0x100）。",
            font=("Microsoft YaHei UI", 8),
            foreground="#555",
            wraplength=520,
        ).pack(anchor=tk.W, pady=(0, 6))

        seal_char_row = ttk.Frame(self.packet_battle_seal_frm)
        seal_char_row.pack(fill=tk.X, pady=2)
        ttk.Label(seal_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_seal_char_var = tk.StringVar(value="")
        self.packet_seal_char_cb = ttk.Combobox(
            seal_char_row,
            textvariable=self.packet_seal_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_seal_char_cb.pack(side=tk.LEFT, padx=(4, 0), fill=tk.X, expand=True)

        seal_scan_row = ttk.Frame(self.packet_battle_seal_frm)
        seal_scan_row.pack(fill=tk.X, pady=(6, 2))
        ttk.Button(
            seal_scan_row,
            text="扫描背包封印卡",
            command=self.on_list_seal_cards,
            width=16,
        ).pack(side=tk.LEFT)
        self.packet_seal_scan_var = tk.StringVar(value="尚未扫描")
        ttk.Label(
            seal_scan_row,
            textvariable=self.packet_seal_scan_var,
            font=("Microsoft YaHei UI", 8),
            foreground="#0066aa",
        ).pack(side=tk.LEFT, padx=(8, 0))

        seal_list_wrap = ttk.Frame(self.packet_battle_seal_frm)
        seal_list_wrap.pack(fill=tk.BOTH, expand=True, pady=(4, 4))
        self.packet_seal_list = tk.Listbox(
            seal_list_wrap,
            height=6,
            font=("Consolas", 9),
            exportselection=False,
        )
        seal_sb = ttk.Scrollbar(seal_list_wrap, orient=tk.VERTICAL, command=self.packet_seal_list.yview)
        self.packet_seal_list.configure(yscrollcommand=seal_sb.set)
        self.packet_seal_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        seal_sb.pack(side=tk.RIGHT, fill=tk.Y)
        self.packet_seal_list.bind("<<ListboxSelect>>", self._on_seal_card_selected)
        self._seal_card_rows: list[dict[str, str]] = []

        seal_idx_row = ttk.Frame(self.packet_battle_seal_frm)
        seal_idx_row.pack(fill=tk.X, pady=2)
        ttk.Label(seal_idx_row, text="背包格 Index:").pack(side=tk.LEFT)
        self.packet_seal_bag_var = tk.StringVar(value="-1")
        ttk.Entry(seal_idx_row, textvariable=self.packet_seal_bag_var, width=8).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        ttk.Label(
            seal_idx_row,
            text="-1=自动第一张；点列表可填入",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        seal_tgt_row = ttk.Frame(self.packet_battle_seal_frm)
        seal_tgt_row.pack(fill=tk.X, pady=2)
        ttk.Label(seal_tgt_row, text="目标位:").pack(side=tk.LEFT)
        self.packet_seal_target_var = tk.StringVar(value="-1")
        ttk.Entry(seal_tgt_row, textvariable=self.packet_seal_target_var, width=8).pack(
            side=tk.LEFT, padx=(4, 8)
        )
        self.packet_seal_prefer_lv1_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            seal_tgt_row,
            text="自动时优先 1 级怪",
            variable=self.packet_seal_prefer_lv1_var,
        ).pack(side=tk.LEFT)
        ttk.Label(
            seal_tgt_row,
            text="  -1=自动选敌",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(side=tk.LEFT)

        seal_btn_row = ttk.Frame(self.packet_battle_seal_frm)
        seal_btn_row.pack(fill=tk.X, pady=(10, 0))
        ttk.Button(
            seal_btn_row,
            text="战斗中扔封印卡",
            command=self.on_battle_seal_send,
            width=18,
        ).pack(side=tk.LEFT)
        ttk.Label(
            seal_btn_row,
            text="须已进战斗且轮到可指令",
            font=("Microsoft YaHei UI", 8),
            foreground="#a04000",
        ).pack(side=tk.LEFT, padx=(8, 0))

        # --- Protobuf 通用 ---
        self.packet_proto_frm = ttk.LabelFrame(self.packet_form_host, text="Protobuf 通用", padding=8)

        preset_row = ttk.Frame(self.packet_proto_frm)
        preset_row.pack(fill=tk.X, pady=(0, 6))
        self._packet_presets: list[tuple[str, int, str, str]] = [
            ("玩家BUFF", 1020, "Proto_CS_PlayerBuff", '{"Type":"玩家BUFF数据"}'),
            ("血魔池状态", 1019, "Proto_CS_HpFp", '{"Type":"血魔池状态"}'),
            ("血魔池设置", 1019, "Proto_CS_HpFp", '{"Type":"血魔池设置"}'),
            ("商店信息", 1038, "Proto_CS_Shop", '{"Type":"商店信息","TabIndex":0}'),
            ("商店购买", 1038, "Proto_CS_Shop", '{"Type":"购买商品","TabIndex":0,"Curr":0}'),
            ("活动", 1047, "Proto_CS_Activity", '{"Type":"活动信息"}'),
            ("背包", 2001, "Proto_CS_Backpack", '{"Type":"背包信息"}'),
        ]
        preset_names = [p[0] for p in self._packet_presets]
        self.packet_preset_var = tk.StringVar(value=preset_names[0] if preset_names else "")
        ttk.Label(preset_row, text="模板:").pack(side=tk.LEFT)
        ttk.Combobox(
            preset_row,
            textvariable=self.packet_preset_var,
            values=preset_names,
            state="readonly",
            width=14,
        ).pack(side=tk.LEFT, padx=6)
        ttk.Button(preset_row, text="填入", command=self.on_packet_fill_preset, width=8).pack(side=tk.LEFT)

        row1 = ttk.Frame(self.packet_proto_frm)
        row1.pack(fill=tk.X, pady=2)
        ttk.Label(row1, text="协议号:").pack(side=tk.LEFT)
        self.packet_opcode_var = tk.StringVar(value="1020")
        ttk.Entry(row1, textvariable=self.packet_opcode_var, width=8).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(row1, text="Proto 类型:").pack(side=tk.LEFT)
        self.packet_proto_var = tk.StringVar(value="Proto_CS_PlayerBuff")
        ttk.Entry(row1, textvariable=self.packet_proto_var, width=24).pack(side=tk.LEFT, padx=4, fill=tk.X, expand=True)

        proto_char_row = ttk.Frame(self.packet_proto_frm)
        proto_char_row.pack(fill=tk.X, pady=2)
        ttk.Label(proto_char_row, text="KUid:").pack(side=tk.LEFT)
        self.packet_proto_char_var = tk.StringVar(value="")
        self.packet_proto_char_cb = ttk.Combobox(
            proto_char_row,
            textvariable=self.packet_proto_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_proto_char_cb.pack(side=tk.LEFT, padx=4, fill=tk.X, expand=True)
        ttk.Label(proto_char_row, text="(空=当前选角)", font=("Microsoft YaHei UI", 8), foreground="#666").pack(side=tk.LEFT)

        ttk.Label(self.packet_proto_frm, text="字段 JSON:").pack(anchor=tk.W, pady=(6, 2))
        self.packet_fields_text = scrolledtext.ScrolledText(self.packet_proto_frm, height=5, font=("Consolas", 9))
        self.packet_fields_text.pack(fill=tk.BOTH, expand=True, pady=(0, 6))
        self.packet_fields_text.insert(tk.END, '{"Type":"玩家BUFF数据"}')

        ttk.Label(self.packet_proto_frm, text="或 Base64 原始 Protobuf（填此项时忽略 JSON）:").pack(anchor=tk.W)
        self.packet_b64_var = tk.StringVar(value="")
        ttk.Entry(self.packet_proto_frm, textvariable=self.packet_b64_var).pack(fill=tk.X, pady=(2, 0))

        # --- GM ---
        self.packet_gm_frm = ttk.LabelFrame(self.packet_form_host, text="GM 命令", padding=8)
        ttk.Label(
            self.packet_gm_frm,
            text="等同 GM 商店：additem 道具ID 数量、makepet 等",
            foreground="#666",
            wraplength=480,
        ).pack(anchor=tk.W, pady=(0, 4))
        self.packet_gm_var = tk.StringVar(value="")
        ttk.Entry(self.packet_gm_frm, textvariable=self.packet_gm_var).pack(fill=tk.X, pady=(0, 6))
        gm_char_row = ttk.Frame(self.packet_gm_frm)
        gm_char_row.pack(fill=tk.X)
        ttk.Label(gm_char_row, text="角色:").pack(side=tk.LEFT)
        self.packet_gm_char_var = tk.StringVar(value="")
        self.packet_gm_char_cb = ttk.Combobox(
            gm_char_row,
            textvariable=self.packet_gm_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_gm_char_cb.pack(side=tk.LEFT, padx=4, fill=tk.X, expand=True)

        # --- 宠物改造 ---
        self.packet_reform_frm = ttk.LabelFrame(self.packet_form_host, text="宠物改造", padding=8)
        ttk.Label(
            self.packet_reform_frm,
            text="协议 4034 · Proto_CS_ReformMutation · 游戏内 SendRrfrom",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
        ).pack(anchor=tk.W)
        ttk.Label(
            self.packet_reform_frm,
            text="Type=「宠物改造」发起改造；Type=「替换改造」确认替换（对应改造 / 改造确认）",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 6))

        ref_row1 = ttk.Frame(self.packet_reform_frm)
        ref_row1.pack(fill=tk.X, pady=2)
        ttk.Label(ref_row1, text="协议号:").pack(side=tk.LEFT)
        self.packet_reform_opcode_var = tk.StringVar(value="4034")
        ttk.Entry(ref_row1, textvariable=self.packet_reform_opcode_var, width=8).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(ref_row1, text="Proto:").pack(side=tk.LEFT)
        self.packet_reform_proto_var = tk.StringVar(value="Proto_CS_ReformMutation")
        ttk.Entry(ref_row1, textvariable=self.packet_reform_proto_var, width=24).pack(
            side=tk.LEFT, padx=4, fill=tk.X, expand=True
        )

        ref_row2 = ttk.Frame(self.packet_reform_frm)
        ref_row2.pack(fill=tk.X, pady=2)
        ttk.Label(ref_row2, text="Type:").pack(side=tk.LEFT)
        self.packet_reform_type_var = tk.StringVar(value="宠物改造")
        ttk.Entry(ref_row2, textvariable=self.packet_reform_type_var, width=14).pack(side=tk.LEFT, padx=(4, 8))
        ttk.Button(ref_row2, text="改造", width=6, command=lambda: self.packet_reform_type_var.set("宠物改造")).pack(
            side=tk.LEFT, padx=(0, 4)
        )
        ttk.Button(ref_row2, text="确认", width=6, command=lambda: self.packet_reform_type_var.set("替换改造")).pack(
            side=tk.LEFT
        )

        ref_row3 = ttk.Frame(self.packet_reform_frm)
        ref_row3.pack(fill=tk.X, pady=2)
        ttk.Label(ref_row3, text="Gird:").pack(side=tk.LEFT)
        self.packet_reform_gird_var = tk.StringVar(value="0")
        ttk.Entry(ref_row3, textvariable=self.packet_reform_gird_var, width=6).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(ref_row3, text="(宠物 Index / 格位)", font=("Microsoft YaHei UI", 8), foreground="#666").pack(
            side=tk.LEFT
        )

        ref_row4 = ttk.Frame(self.packet_reform_frm)
        ref_row4.pack(fill=tk.X, pady=2)
        self.packet_reform_backup_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(ref_row4, text="Backup（改造时是否用备份档 ReformCostBackup）", variable=self.packet_reform_backup_var).pack(
            anchor=tk.W
        )

        ref_char_row = ttk.Frame(self.packet_reform_frm)
        ref_char_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Label(ref_char_row, text="KUid:").pack(side=tk.LEFT)
        self.packet_reform_char_var = tk.StringVar(value="")
        self.packet_reform_char_cb = ttk.Combobox(
            ref_char_row,
            textvariable=self.packet_reform_char_var,
            values=[],
            state="readonly",
            width=32,
        )
        self.packet_reform_char_cb.pack(side=tk.LEFT, padx=4, fill=tk.X, expand=True)
        ttk.Label(ref_char_row, text="(空=当前选角)", font=("Microsoft YaHei UI", 8), foreground="#666").pack(side=tk.LEFT)

        pkt_log_frm = ttk.LabelFrame(parent, text="发包日志", padding=6)
        pkt_log_frm.pack(fill=tk.BOTH, expand=True, pady=(8, 8))
        self.packet_log = scrolledtext.ScrolledText(pkt_log_frm, height=4, font=("Consolas", 9))
        self.packet_log.pack(fill=tk.BOTH, expand=True)

        bottom_frm = ttk.Frame(parent)
        bottom_frm.pack(fill=tk.X)
        self.packet_bottom_frm = bottom_frm
        ttk.Label(bottom_frm, text="连续间隔(ms):").pack(side=tk.LEFT)
        self.packet_interval_var = tk.StringVar(value="500")
        ttk.Entry(bottom_frm, textvariable=self.packet_interval_var, width=8).pack(side=tk.LEFT, padx=(4, 12))
        self.packet_send_btn = ttk.Button(bottom_frm, text="发包", command=self.on_packet_send_once, width=10)
        self.packet_send_btn.pack(side=tk.LEFT, padx=(0, 6))
        self.packet_burst_btn = ttk.Button(bottom_frm, text="连续发包", command=self.on_packet_send_burst, width=10)
        self.packet_burst_btn.pack(side=tk.LEFT, padx=(0, 6))
        self.packet_burst_cancel_btn = ttk.Button(
            bottom_frm,
            text="取消连续",
            command=self.on_packet_send_cancel,
            width=10,
            state=tk.DISABLED,
        )
        self.packet_burst_cancel_btn.pack(side=tk.LEFT)

        self._show_packet_form()

    def _show_packet_form(self) -> None:
        if not hasattr(self, "packet_form_host"):
            return
        label = self.packet_type_var.get().strip()
        kind = self._packet_type_map.get(label, "use_item")
        for frm in (
            self.packet_use_item_frm,
            self.packet_use_item_seq_frm,
            self.packet_special_pkg_frm,
            self.packet_gem_inlay_frm,
            getattr(self, "packet_learn_skill_frm", None),
            getattr(self, "packet_learn_pet_skill_frm", None),
            getattr(self, "packet_battle_seal_frm", None),
            self.packet_reform_frm,
            self.packet_proto_frm,
            self.packet_gm_frm,
        ):
            if frm is not None:
                frm.pack_forget()
        if kind == "use_item":
            self.packet_use_item_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "use_item_seq":
            self.packet_use_item_seq_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "special_pkg":
            self.packet_special_pkg_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "gem_inlay":
            self.packet_gem_inlay_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "learn_skill":
            self.packet_learn_skill_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "learn_pet_skill":
            self.packet_learn_pet_skill_frm.pack(fill=tk.BOTH, expand=True)
            self._refresh_pet_skill_pet_cb()
        elif kind == "battle_seal":
            self.packet_battle_seal_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "reform":
            self.packet_reform_frm.pack(fill=tk.BOTH, expand=True)
        elif kind == "proto":
            self.packet_proto_frm.pack(fill=tk.BOTH, expand=True)
        else:
            self.packet_gm_frm.pack(fill=tk.BOTH, expand=True)

        show_bottom = kind not in (
            "use_item_seq",
            "special_pkg",
            "learn_skill",
            "learn_pet_skill",
            "battle_seal",
        )
        if hasattr(self, "packet_bottom_frm"):
            if show_bottom:
                self.packet_bottom_frm.pack(fill=tk.X)
            else:
                self.packet_bottom_frm.pack_forget()

    def _normalize_packet_slot(self) -> None:
        raw = self.packet_slot_var.get().strip()
        try:
            n = int(raw)
        except ValueError:
            n = 1
        if n < 1:
            n = 1
        self.packet_slot_var.set(str(n))

    def _parse_packet_slot(self) -> int | None:
        self._normalize_packet_slot()
        try:
            user_slot = int(self.packet_slot_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "背包格必须是整数")
            return None
        if user_slot < 1:
            messagebox.showwarning("输入无效", "背包格不能小于 1")
            return None
        return user_slot + 7

    def _parse_packet_use_count(self) -> int | None:
        raw = self.packet_use_count_var.get().strip()
        try:
            return int(raw)
        except ValueError:
            messagebox.showwarning("输入无效", "Usecount 必须是整数（可为负数，如 -1）")
            return None

    def _update_packet_chars(self, st: dict) -> None:
        if not hasattr(self, "packet_char_cb"):
            return
        iid = self._target_instance_id()
        chars = ipc.update_char_cache(iid, st) if iid else ipc.parse_char_roster(st)
        labels: list[str] = []
        options: list[tuple[str, str]] = []
        for ch in chars:
            label = f"{ch['index']}.{ch['name']}"
            labels.append(label)
            options.append((label, ch["uid"]))
        self._char_options = options

        for cb, var in (
            (self.packet_char_cb, self.packet_char_var),
            (self.packet_seq_char_cb, self.packet_seq_char_var),
            (self.packet_sp_char_cb, self.packet_sp_char_var),
            (self.packet_gem_char_cb, self.packet_gem_char_var),
            *(([(self.packet_skill_char_cb, self.packet_skill_char_var)] if hasattr(self, "packet_skill_char_cb") else [])),
            *(([(self.packet_pet_skill_char_cb, self.packet_pet_skill_char_var)] if hasattr(self, "packet_pet_skill_char_cb") else [])),
            *(([(self.packet_seal_char_cb, self.packet_seal_char_var)] if hasattr(self, "packet_seal_char_cb") else [])),
            (self.packet_proto_char_cb, self.packet_proto_char_var),
            (self.packet_gm_char_cb, self.packet_gm_char_var),
            (self.packet_reform_char_cb, self.packet_reform_char_var),
            *(([(self.panel_char_cb, self.panel_char_var)] if hasattr(self, "panel_char_cb") else [])),
        ):
            prev = var.get()
            cb["values"] = labels
            if prev in labels:
                var.set(prev)
            elif labels:
                select_uid = str(st.get("select_uid") or "")
                picked = labels[0]
                for lbl, uid in options:
                    if uid == select_uid:
                        picked = lbl
                        break
                var.set(picked)
            else:
                var.set("")

        x = int(st.get("pos_x") or 0)
        y = int(st.get("pos_y") or 0)
        if x > 0 or y > 0:
            pos_txt = f"坐标：({x}, {y})  ·  发包时自动取用"
            self.packet_pos_var.set(pos_txt)
            if hasattr(self, "packet_seq_pos_var"):
                self.packet_seq_pos_var.set(pos_txt)
            if hasattr(self, "packet_sp_pos_var"):
                self.packet_sp_pos_var.set(pos_txt)
        elif hasattr(self, "packet_pos_var"):
            self.packet_pos_var.set("坐标：未进游戏 / 等待心跳…")
            if hasattr(self, "packet_seq_pos_var"):
                self.packet_seq_pos_var.set("坐标：未进游戏 / 等待心跳…")
            if hasattr(self, "packet_sp_pos_var"):
                self.packet_sp_pos_var.set("坐标：未进游戏 / 等待心跳…")

        self._refresh_pet_skill_pet_cb()
        self._refresh_skill_saved_npc_cb()

    def _char_label_to_uid(self, label: str) -> str | None:
        label = label.strip()
        if not label:
            return None
        for lbl, uid in self._char_options:
            if lbl == label:
                return uid
        return None

    def _get_packet_pos(self) -> tuple[int, int] | None:
        st = self._last_bridge_state
        x = int(st.get("pos_x") or 0)
        y = int(st.get("pos_y") or 0)
        if x <= 0 and y <= 0:
            messagebox.showwarning("无坐标", "当前未检测到有效坐标，请确认角色已进游戏")
            return None
        return x, y

    def _build_use_item_send_fn(self):
        pos = self._get_packet_pos()
        if pos is None:
            return None
        x, y = pos
        haveindex = self._parse_packet_slot()
        if haveindex is None:
            return None
        usecount = self._parse_packet_use_count()
        if usecount is None:
            return None
        uid = self._char_label_to_uid(self.packet_char_var.get())
        if not uid:
            messagebox.showwarning("未选角色", "请先在「角色」下拉中选择要操作的号")
            return None
        user_slot = int(self.packet_slot_var.get().strip())

        def send_fn(iid: str) -> str:
            return ipc.send_use_item(
                iid,
                haveitemindex=haveindex,
                uid=uid,
                x=x,
                y=y,
                usecount=usecount,
            )

        label = f"use_item slot={user_slot}→{haveindex} x{usecount} ({x},{y}) uid={uid[:8]}…"
        return label, send_fn

    def _build_gem_inlay_send_fn(self):
        try:
            equip_index = int(self.packet_gem_equip_var.get().strip())
            gem_index = int(self.packet_gem_stone_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "装备位置 / 宝石位置必须是整数")
            return None
        if equip_index < 0 or gem_index < 0:
            messagebox.showwarning("输入无效", "位置不能为负数")
            return None
        uid = self._char_label_to_uid(self.packet_gem_char_var.get())
        if not uid:
            messagebox.showwarning("未选角色", "请先在「角色」下拉中选择要操作的号")
            return None

        def send_fn(iid: str) -> str:
            return ipc.send_gem_inlay(
                iid,
                equip_index=equip_index,
                gem_index=gem_index,
                uid=uid,
            )

        label = f"gem_inlay equip={equip_index} gem={gem_index} uid={uid[:8]}…"
        return label, send_fn

    @staticmethod
    def _skill_slot_label(slot: int) -> str:
        """Skillindex 文案：0=第1个格子。"""
        return f"第{slot + 1}个格子（Skillindex={slot}，0=第1个格子）"

    def _load_skill_id_name_map_file(self, filename: str) -> dict[int, str]:
        mapping: dict[int, str] = {}
        path = Path(__file__).resolve().parents[2] / "tools" / filename
        try:
            if not path.is_file():
                return mapping
            for line in path.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if (
                    not line
                    or line.startswith("技")
                    or line.startswith("宠")
                    or line.startswith("Skill")
                    or line.startswith("来源")
                    or line.startswith("格式")
                    or line.startswith("说明")
                    or line.startswith("-")
                ):
                    continue
                parts = line.split("\t")
                if len(parts) < 2:
                    parts = line.split(None, 1)
                if len(parts) < 2:
                    continue
                try:
                    mapping[int(parts[0].strip())] = parts[1].strip()
                except ValueError:
                    continue
        except OSError:
            pass
        return mapping

    def _skill_id_name_map(self) -> dict[int, str]:
        cached = getattr(self, "_skill_id_name_cache", None)
        if cached is not None:
            return cached
        mapping = self._load_skill_id_name_map_file("skill_id_name_map.txt")
        self._skill_id_name_cache = mapping
        return mapping

    def _pet_skill_id_name_map(self) -> dict[int, str]:
        """宠物 Buyindex 与人物共用本体 ID；优先读宠物表，缺则回退人物表。"""
        cached = getattr(self, "_pet_skill_id_name_cache", None)
        if cached is not None:
            return cached
        mapping = self._load_skill_id_name_map_file("pet_skill_id_name_map.txt")
        if not mapping:
            mapping = self._skill_id_name_map()
        self._pet_skill_id_name_cache = mapping
        return mapping

    def _refresh_skill_id_hint(self) -> None:
        if not hasattr(self, "packet_skill_name_var"):
            return
        raw = self.packet_skill_id_var.get().strip()
        if not raw:
            self.packet_skill_name_var.set("名称：-")
            return
        try:
            sid = int(raw)
        except ValueError:
            self.packet_skill_name_var.set("名称：无效 ID")
            return
        name = self._skill_id_name_map().get(sid)
        self.packet_skill_name_var.set(f"名称：{name}" if name else "名称：未知（仍可发包）")

    def on_packet_skill_fill_npc(self) -> None:
        vals = self._selected_npc_values() if hasattr(self, "_selected_npc_values") else None
        oid = ""
        if vals:
            oid = str(vals[0])
        elif getattr(self, "_selected_npc_objindex", ""):
            oid = str(self._selected_npc_objindex)
        if not oid:
            messagebox.showinfo("填入 NPC", "请先到「NPC」页选中一个 NPC")
            return
        self.packet_skill_npc_var.set(oid)
        self.status_var.set(f"已填入 NPC Objindex={oid}")

    def on_learn_skill_send(self) -> None:
        if self._packet_burst_active or getattr(self, "_use_item_seq_active", False) or getattr(
            self, "_special_pkg_active", False
        ):
            messagebox.showwarning("忙碌中", "请先停止其它连续发包任务")
            return

        char_label = self.packet_skill_char_var.get().strip()
        uid = self._char_label_to_uid(char_label)
        if not uid:
            messagebox.showwarning("未选角色", "请先在「给谁学」中选择要学习技能的角色")
            return

        try:
            npc_obj = int(self.packet_skill_npc_var.get().strip())
            skill_slot = int(self.packet_skill_slot_var.get().strip())
            skill_id = int(self.packet_skill_id_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "NPC Objindex / 技能栏位 / 技能 ID 必须是整数")
            return
        if npc_obj < 0:
            messagebox.showwarning("输入无效", "NPC Objindex 不能为负")
            return
        if skill_slot < 0:
            messagebox.showwarning("输入无效", "技能栏位不能为负")
            return

        skill_name = self._skill_id_name_map().get(skill_id, "未知")
        char_name = char_label
        if "." in char_label:
            char_name = char_label.split(".", 1)[1].strip() or char_label
        slot_text = self._skill_slot_label(skill_slot)

        confirm = messagebox.askyesno(
            "确认学技能 — 请核对角色",
            "即将发包学技能，请确认是给下面这个角色学：\n\n"
            f"★ 角色：{char_name}\n"
            f"  下拉项：{char_label}\n"
            f"  UID：{uid}\n\n"
            f"NPC Objindex：{npc_obj}\n"
            f"技能格子：{slot_text}\n"
            f"技能 ID（Buyindex）：{skill_id}  {skill_name}\n\n"
            "点「是」才会发送；点「否」取消。",
            icon="warning",
            default=messagebox.NO,
        )
        if not confirm:
            self._packet_log_line("[学技能] 已取消（未确认角色）")
            return

        # 二次确认：角色 + 第几个格子（0=第1格）
        confirm2 = messagebox.askyesno(
            "再次确认 — 给谁的第几个格子",
            f"确定给「{char_name}」\n"
            f"的【{slot_text}】\n"
            f"学习【{skill_id} {skill_name}】？\n\n"
            "提醒：Skillindex 0 = 第1个格子。",
            icon="warning",
            default=messagebox.NO,
        )
        if not confirm2:
            self._packet_log_line("[学技能] 已取消（二次确认）")
            return

        iid = self._require_instance()
        if not iid:
            return

        def send_fn(instance_id: str) -> str:
            return ipc.send_learn_skill(
                instance_id,
                uid=uid,
                skill_slot=skill_slot,
                npc_objindex=npc_obj,
                skill_id=skill_id,
            )

        label = f"learn_skill char={char_name} slot={skill_slot} skill={skill_id} npc={npc_obj}"
        self._packet_log_line(f"[学技能] {label}")
        self._send_packet(label, send_fn, wait_ack=True)

    def _on_seal_card_selected(self, _event=None) -> None:
        if not hasattr(self, "packet_seal_list"):
            return
        sel = self.packet_seal_list.curselection()
        if not sel:
            return
        idx = int(sel[0])
        rows = getattr(self, "_seal_card_rows", [])
        if idx < 0 or idx >= len(rows):
            return
        self.packet_seal_bag_var.set(str(rows[idx].get("index", "-1")))

    def on_list_seal_cards(self) -> None:
        iid = self._require_instance()
        if not iid:
            return
        uid = self._char_label_to_uid(self.packet_seal_char_var.get())
        try:
            msg = ipc.list_seal_cards(iid, uid=uid)
        except Exception as exc:
            messagebox.showerror("扫描失败", str(exc))
            return
        rows = ipc.parse_seal_cards(iid)
        self._seal_card_rows = rows
        self.packet_seal_list.delete(0, tk.END)
        for row in rows:
            self.packet_seal_list.insert(
                tk.END,
                f"idx={row.get('index', '?')}  type={row.get('type', '?')}  "
                f"flg={row.get('flg', '?')}  {row.get('name', '')}",
            )
        self.packet_seal_scan_var.set(f"共 {len(rows)} 张 · {msg}")
        self._packet_log_line(f"[战斗封印] 扫描 {msg}")
        if rows and self.packet_seal_bag_var.get().strip() in ("", "-1"):
            self.packet_seal_bag_var.set(str(rows[0].get("index", "-1")))

    def on_battle_seal_send(self) -> None:
        if self._packet_burst_active or getattr(self, "_use_item_seq_active", False) or getattr(
            self, "_special_pkg_active", False
        ):
            messagebox.showwarning("忙碌中", "请先停止其它连续发包任务")
            return

        uid = self._char_label_to_uid(self.packet_seal_char_var.get())
        try:
            bag_index = int(self.packet_seal_bag_var.get().strip())
            target_index = int(self.packet_seal_target_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "背包格 Index / 目标位必须是整数（可用 -1）")
            return

        prefer_lv1 = bool(self.packet_seal_prefer_lv1_var.get())
        iid = self._require_instance()
        if not iid:
            return

        def send_fn(instance_id: str) -> str:
            return ipc.battle_seal(
                instance_id,
                uid=uid,
                bag_index=bag_index,
                target_index=target_index,
                prefer_lv1=prefer_lv1,
            )

        label = (
            f"battle_seal bag={bag_index} target={target_index} "
            f"prefer_lv1={int(prefer_lv1)} uid={uid or '-'}"
        )
        self._packet_log_line(f"[战斗封印] {label}")
        self._send_packet(label, send_fn, wait_ack=True)

    def _build_proto_send_fn(self):
        try:
            opcode = int(self.packet_opcode_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "协议号必须是整数")
            return None

        proto_type = self.packet_proto_var.get().strip()
        if not proto_type:
            messagebox.showwarning("输入无效", "请填写 Proto 类型名")
            return None

        data_b64 = self.packet_b64_var.get().strip() or None
        fields: dict | None = None
        if not data_b64:
            raw = self.packet_fields_text.get("1.0", tk.END).strip()
            if raw:
                try:
                    parsed = json.loads(raw)
                except json.JSONDecodeError as exc:
                    messagebox.showwarning("JSON 无效", str(exc))
                    return None
                if not isinstance(parsed, dict):
                    messagebox.showwarning("JSON 无效", "字段必须是 JSON 对象 {}")
                    return None
                fields = parsed

        uid = self._char_label_to_uid(self.packet_proto_char_var.get())

        def send_fn(iid: str) -> str:
            return ipc.send_proto(
                iid,
                opcode=opcode,
                proto_type=proto_type,
                fields=fields,
                uid=uid,
                data_b64=data_b64,
            )

        label = f"send_proto {opcode} {proto_type}"
        return label, send_fn

    def _build_gm_send_fn(self):
        text = self.packet_gm_var.get().strip()
        if not text:
            messagebox.showwarning("输入无效", "请填写 GM 命令")
            return None
        uid = self._char_label_to_uid(self.packet_gm_char_var.get())

        def send_fn(iid: str) -> str:
            return ipc.send_gm(iid, text, uid=uid)

        return f"send_gm {text[:24]}", send_fn

    def _build_reform_send_fn(self):
        try:
            opcode = int(self.packet_reform_opcode_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "协议号必须是整数")
            return None
        proto_type = self.packet_reform_proto_var.get().strip()
        if not proto_type:
            messagebox.showwarning("输入无效", "请填写 Proto 类型")
            return None
        type_str = self.packet_reform_type_var.get().strip()
        if not type_str:
            messagebox.showwarning("输入无效", "请填写 Type")
            return None
        try:
            gird = int(self.packet_reform_gird_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "Gird 必须是整数")
            return None
        backup = bool(self.packet_reform_backup_var.get())
        uid = self._char_label_to_uid(self.packet_reform_char_var.get())
        fields = {
            "Type": type_str,
            "Gird": gird,
            "Backup": backup,
        }

        def send_fn(iid: str) -> str:
            return ipc.send_proto(
                iid,
                opcode=opcode,
                proto_type=proto_type,
                fields=fields,
                uid=uid,
                opcode_name="LSSPROTO_PET_REFORM_MUTATION_FUNC",
            )

        label = f"reform {type_str} gird={gird} backup={backup}"
        return label, send_fn

    def _build_current_packet_send_fn(self):
        kind = self._packet_type_map.get(self.packet_type_var.get().strip(), "use_item")
        if kind == "use_item":
            return self._build_use_item_send_fn()
        if kind == "gem_inlay":
            return self._build_gem_inlay_send_fn()
        if kind == "use_item_seq":
            messagebox.showinfo("连续使用道具", "请在本页点击「开始任务」")
            return None
        if kind == "special_pkg":
            messagebox.showinfo("开特殊包裹", "请在本页点击「开始」")
            return None
        if kind == "learn_skill":
            messagebox.showinfo("学技能", "请在本页点击「确认并发包（学技能）」")
            return None
        if kind == "learn_pet_skill":
            messagebox.showinfo("宠物学技能", "请在本页点击「确认并发包（宠物学技能）」")
            return None
        if kind == "battle_seal":
            messagebox.showinfo("战斗封印", "请在本页点击「战斗中扔封印卡」")
            return None
        if kind == "reform":
            return self._build_reform_send_fn()
        if kind == "proto":
            return self._build_proto_send_fn()
        return self._build_gm_send_fn()

    @staticmethod
    def _parse_use_item_tasks(raw: str) -> list[tuple[int, int]]:
        tasks: list[tuple[int, int]] = []
        for line in raw.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            cleaned = re.sub(r"[格次×xX]", " ", line)
            cleaned = cleaned.replace("，", ",").replace("、", ",").replace(";", ",")
            parts = [p.strip() for p in cleaned.replace(",", " ").split() if p.strip()]
            if len(parts) < 2:
                raise ValueError(f"无法解析任务行: {line}")
            slot = int(parts[0])
            count = int(parts[1])
            if slot < 1:
                raise ValueError(f"背包格不能小于 1: {line}")
            if count < 1:
                raise ValueError(f"次数不能小于 1: {line}")
            tasks.append((slot, count))
        if not tasks:
            raise ValueError("请至少填写一条任务")
        return tasks

    def _set_use_item_seq_ui(self, active: bool) -> None:
        self._use_item_seq_active = active
        start_state = tk.DISABLED if active else tk.NORMAL
        cancel_state = tk.NORMAL if active else tk.DISABLED
        if hasattr(self, "packet_seq_start_btn"):
            self.packet_seq_start_btn.config(state=start_state)
        if hasattr(self, "packet_seq_cancel_btn"):
            self.packet_seq_cancel_btn.config(state=cancel_state)

    def on_use_item_seq_start(self) -> None:
        if self._use_item_seq_active or self._special_pkg_active or self._packet_burst_active:
            return
        try:
            tasks = self._parse_use_item_tasks(self.packet_seq_tasks_text.get("1.0", tk.END))
            interval_ms = int(self.packet_seq_interval_var.get().strip())
        except ValueError as exc:
            messagebox.showwarning("输入无效", str(exc))
            return
        if interval_ms < 50:
            messagebox.showwarning("输入无效", "连续间隔不能小于 50 毫秒")
            return
        pos = self._get_packet_pos()
        if pos is None:
            return
        x, y = pos
        uid = self._char_label_to_uid(self.packet_seq_char_var.get())
        if not uid:
            messagebox.showwarning("未选角色", "请先在「角色」下拉中选择要操作的号")
            return
        iid = self._require_instance()
        if not iid:
            return

        total = sum(c for _, c in tasks)
        self._use_item_seq_state = {
            "tasks": tasks,
            "task_i": 0,
            "left": tasks[0][1],
            "uid": uid,
            "x": x,
            "y": y,
            "interval_ms": interval_ms,
            "sent": 0,
            "total": total,
        }
        self._set_use_item_seq_ui(True)
        self._update_use_item_seq_status()
        self._packet_log_line(
            f"[任务] 开始 共 {len(tasks)} 段 / {total} 次  间隔 {interval_ms}ms"
        )
        self._use_item_seq_tick()

    def on_use_item_seq_cancel(self) -> None:
        if not self._use_item_seq_active:
            return
        self._use_item_seq_active = False
        self._use_item_seq_state = None
        if self._use_item_seq_after_id is not None:
            try:
                self.root.after_cancel(self._use_item_seq_after_id)
            except tk.TclError:
                pass
            self._use_item_seq_after_id = None
        self._set_use_item_seq_ui(False)
        if hasattr(self, "packet_seq_status_var"):
            self.packet_seq_status_var.set("已取消")
        self._packet_log_line("[任务] 已取消")

    def _update_use_item_seq_status(self) -> None:
        if not hasattr(self, "packet_seq_status_var") or not self._use_item_seq_state:
            return
        st = self._use_item_seq_state
        tasks: list[tuple[int, int]] = st["tasks"]
        ti = int(st["task_i"])
        slot, total_in_task = tasks[ti]
        left = int(st["left"])
        done_in_task = total_in_task - left
        self.packet_seq_status_var.set(
            f"第 {ti + 1}/{len(tasks)} 段 · 格 {slot} · "
            f"{done_in_task}/{total_in_task} · 总进度 {st['sent']}/{st['total']}"
        )

    def _use_item_seq_tick(self) -> None:
        if not self._use_item_seq_state:
            return
        self._use_item_seq_active = True
        st = self._use_item_seq_state
        tasks: list[tuple[int, int]] = st["tasks"]
        ti = int(st["task_i"])
        if ti >= len(tasks):
            self.on_use_item_seq_cancel()
            if hasattr(self, "packet_seq_status_var"):
                self.packet_seq_status_var.set("全部完成")
            self._packet_log_line("[任务] 全部完成")
            return

        user_slot, _ = tasks[ti]
        haveindex = user_slot + 7
        uid = str(st["uid"])
        x, y = int(st["x"]), int(st["y"])
        label = f"use_item seq 格{user_slot}→{haveindex} ({st['sent'] + 1}/{st['total']})"
        self._update_use_item_seq_status()
        self._packet_log_line(f"[任务] {label}")

        def send_fn(iid: str) -> str:
            return ipc.send_use_item(
                iid,
                haveitemindex=haveindex,
                uid=uid,
                x=x,
                y=y,
            )

        self._send_packet(label, send_fn, wait_ack=False, on_done=self._use_item_seq_after_send)

    def _use_item_seq_after_send(self, ok: bool) -> None:
        if not self._use_item_seq_state:
            return
        st = self._use_item_seq_state
        st["sent"] = int(st["sent"]) + 1
        st["left"] = int(st["left"]) - 1
        if int(st["left"]) <= 0:
            st["task_i"] = int(st["task_i"]) + 1
            tasks: list[tuple[int, int]] = st["tasks"]
            if int(st["task_i"]) >= len(tasks):
                self._use_item_seq_active = False
                self._use_item_seq_state = None
                self._set_use_item_seq_ui(False)
                if hasattr(self, "packet_seq_status_var"):
                    self.packet_seq_status_var.set("全部完成")
                self._packet_log_line("[任务] 全部完成")
                return
            st["left"] = tasks[int(st["task_i"])][1]
            self._packet_log_line(
                f"[任务] 进入下一段 · 格 {tasks[int(st['task_i'])][0]}"
            )
        interval_ms = int(st["interval_ms"])
        self._use_item_seq_after_id = self.root.after(interval_ms, self._use_item_seq_tick)

    def _set_special_pkg_ui(self, active: bool) -> None:
        self._special_pkg_active = active
        start_state = tk.DISABLED if active else tk.NORMAL
        cancel_state = tk.NORMAL if active else tk.DISABLED
        if hasattr(self, "packet_sp_start_btn"):
            self.packet_sp_start_btn.config(state=start_state)
        if hasattr(self, "packet_sp_cancel_btn"):
            self.packet_sp_cancel_btn.config(state=cancel_state)

    def on_special_pkg_start(self) -> None:
        if self._special_pkg_active or self._use_item_seq_active or self._packet_burst_active:
            return
        try:
            a_pos = int(self.packet_sp_a_var.get().strip())
            b_pos = int(self.packet_sp_b_var.get().strip())
            uses_per_pos = int(self.packet_sp_use_count_var.get().strip())
            interval_ms = int(self.packet_sp_interval_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "A / B / 次数 / 间隔必须是整数")
            return
        if a_pos < 0 or b_pos < a_pos:
            messagebox.showwarning("输入无效", "需满足 0 ≤ A ≤ B")
            return
        if uses_per_pos < 1:
            messagebox.showwarning("输入无效", "每格使用次数至少为 1")
            return
        if interval_ms < 50:
            messagebox.showwarning("输入无效", "间隔不能小于 50 毫秒")
            return
        pos = self._get_packet_pos()
        if pos is None:
            return
        x, y = pos
        uid = self._char_label_to_uid(self.packet_sp_char_var.get())
        if not uid:
            messagebox.showwarning("未选角色", "请先在「角色」下拉中选择要操作的号")
            return
        iid = self._require_instance()
        if not iid:
            return

        bank_min = 8
        total_use = (b_pos - a_pos + 1) * uses_per_pos
        bank_slots = max(0, a_pos - bank_min)
        total_bank = (b_pos - a_pos + 1) * bank_slots
        self._special_pkg_state = {
            "phase": "use",
            "pos": a_pos,
            "use_left": uses_per_pos,
            "bank_slot": bank_min,
            "a": a_pos,
            "b": b_pos,
            "bank_min": bank_min,
            "uses_per_pos": uses_per_pos,
            "uid": uid,
            "x": x,
            "y": y,
            "interval_ms": interval_ms,
            "use_sent": 0,
            "bank_sent": 0,
            "total_use": total_use,
            "total_bank": total_bank,
        }
        self._set_special_pkg_ui(True)
        self._update_special_pkg_status()
        self._packet_log_line(
            f"[特殊包裹] 开始 A={a_pos} B={b_pos} 每格{uses_per_pos}次 "
            f"预计使用{total_use}次 存银行{total_bank}次 间隔{interval_ms}ms"
        )
        self._special_pkg_tick()

    def on_special_pkg_cancel(self) -> None:
        if not self._special_pkg_active:
            return
        self._special_pkg_active = False
        self._special_pkg_state = None
        if self._special_pkg_after_id is not None:
            try:
                self.root.after_cancel(self._special_pkg_after_id)
            except tk.TclError:
                pass
            self._special_pkg_after_id = None
        self._set_special_pkg_ui(False)
        if hasattr(self, "packet_sp_status_var"):
            self.packet_sp_status_var.set("已取消")
        self._packet_log_line("[特殊包裹] 已取消")

    def _update_special_pkg_status(self) -> None:
        if not hasattr(self, "packet_sp_status_var") or not self._special_pkg_state:
            return
        st = self._special_pkg_state
        phase = str(st["phase"])
        pos = int(st["pos"])
        a_pos = int(st["a"])
        b_pos = int(st["b"])
        if phase == "use":
            uses_per_pos = int(st["uses_per_pos"])
            left = int(st["use_left"])
            self.packet_sp_status_var.set(
                f"使用格 {pos} ({b_pos - pos + 1} 段剩余) · "
                f"本段 {uses_per_pos - left}/{uses_per_pos} · "
                f"总 {st['use_sent']}/{st['total_use']}"
            )
        else:
            bank_slot = int(st["bank_slot"])
            self.packet_sp_status_var.set(
                f"存银行格 {bank_slot} (< {a_pos}) · "
                f"开包格 {pos} · 总 {st['bank_sent']}/{st['total_bank']}"
            )

    def _special_pkg_tick(self) -> None:
        if not self._special_pkg_state:
            return
        self._special_pkg_active = True
        st = self._special_pkg_state
        phase = str(st["phase"])
        uid = str(st["uid"])
        interval_ms = int(st["interval_ms"])

        if phase == "use":
            pos = int(st["pos"])
            x, y = int(st["x"]), int(st["y"])
            label = f"special_pkg use {pos} ({int(st['use_sent']) + 1}/{st['total_use']})"
            self._update_special_pkg_status()
            self._packet_log_line(f"[特殊包裹] {label}")

            def send_fn(iid: str) -> str:
                return ipc.send_use_item(
                    iid,
                    haveitemindex=pos,
                    uid=uid,
                    x=x,
                    y=y,
                )

            self._send_packet(label, send_fn, wait_ack=False, on_done=self._special_pkg_after_send)
            return

        bank_slot = int(st["bank_slot"])
        a_pos = int(st["a"])
        if bank_slot >= a_pos:
            self._special_pkg_advance_after_bank()
            return

        label = f"special_pkg bank {bank_slot} ({int(st['bank_sent']) + 1}/{st['total_bank']})"
        self._update_special_pkg_status()
        self._packet_log_line(f"[特殊包裹] {label}")

        def send_fn(iid: str) -> str:
            return ipc.send_account_bank_deposit(
                iid,
                haveitemindex=bank_slot,
                uid=uid,
            )

        self._send_packet(label, send_fn, wait_ack=False, on_done=self._special_pkg_after_send)

    def _special_pkg_advance_after_bank(self) -> None:
        if not self._special_pkg_state:
            return
        st = self._special_pkg_state
        pos = int(st["pos"])
        b_pos = int(st["b"])
        if pos >= b_pos:
            self._special_pkg_active = False
            self._special_pkg_state = None
            self._set_special_pkg_ui(False)
            if hasattr(self, "packet_sp_status_var"):
                self.packet_sp_status_var.set("全部完成")
            self._packet_log_line("[特殊包裹] 全部完成")
            return
        st["pos"] = pos + 1
        st["phase"] = "use"
        st["use_left"] = int(st["uses_per_pos"])
        st["bank_slot"] = int(st["bank_min"])
        self._packet_log_line(f"[特殊包裹] 进入下一开包格 {st['pos']}")
        interval_ms = int(st["interval_ms"])
        self._special_pkg_after_id = self.root.after(interval_ms, self._special_pkg_tick)

    def _special_pkg_after_send(self, ok: bool) -> None:
        if not self._special_pkg_state:
            return
        st = self._special_pkg_state
        phase = str(st["phase"])
        interval_ms = int(st["interval_ms"])

        if phase == "use":
            st["use_sent"] = int(st["use_sent"]) + 1
            st["use_left"] = int(st["use_left"]) - 1
            if int(st["use_left"]) <= 0:
                st["phase"] = "bank"
                st["bank_slot"] = int(st["bank_min"])
                self._packet_log_line(
                    f"[特殊包裹] 格 {st['pos']} 使用完毕，开始存银行 (< {st['a']})"
                )
        else:
            st["bank_sent"] = int(st["bank_sent"]) + 1
            st["bank_slot"] = int(st["bank_slot"]) + 1
            if int(st["bank_slot"]) >= int(st["a"]):
                self._special_pkg_advance_after_bank()
                return

        self._special_pkg_after_id = self.root.after(interval_ms, self._special_pkg_tick)

    def on_pick_foreground_window(self) -> None:
        win = game_util.get_foreground_cg37_window()
        if win is None:
            messagebox.showwarning(
                "未找到游戏窗口",
                "当前前台窗口不是 cg37.exe。\n请先点击目标游戏窗口，再点「当前窗口」。",
            )
            return
        self._apply_instance_from_window(win)

    def on_pick_game_window(self) -> None:
        wins = game_util.list_cg37_windows()
        if not wins:
            messagebox.showwarning("未找到", "没有运行中的 cg37.exe 窗口")
            return
        if len(wins) == 1:
            self._apply_instance_from_window(wins[0])
            return

        dlg = tk.Toplevel(self.root)
        dlg.title("选择游戏窗口")
        dlg.geometry("520x320")
        dlg.transient(self.root)
        dlg.grab_set()

        ttk.Label(
            dlg,
            text="按窗口标题选择（可多开时靠标题区分）：",
            wraplength=480,
        ).pack(anchor=tk.W, padx=12, pady=(12, 6))

        cols = ("title", "pid", "iid")
        tree = ttk.Treeview(dlg, columns=cols, show="headings", height=8)
        tree.heading("title", text="窗口标题")
        tree.heading("pid", text="PID")
        tree.heading("iid", text="实例 ID")
        tree.column("title", width=280, anchor=tk.W)
        tree.column("pid", width=64, anchor=tk.CENTER)
        tree.column("iid", width=120, anchor=tk.W)
        tree.pack(fill=tk.BOTH, expand=True, padx=12, pady=(0, 8))
        for w in wins:
            tree.insert("", tk.END, values=(w.title, w.pid, w.instance_id))

        btn_row = ttk.Frame(dlg)
        btn_row.pack(fill=tk.X, padx=12, pady=(0, 12))

        def choose() -> None:
            sel = tree.selection()
            if not sel:
                messagebox.showwarning("未选择", "请先选中一行", parent=dlg)
                return
            vals = tree.item(sel[0], "values")
            win = game_util.GameWindow(
                pid=int(vals[1]),
                title=str(vals[0]),
                instance_id=str(vals[2]),
            )
            dlg.destroy()
            self._apply_instance_from_window(win)

        ttk.Button(btn_row, text="确定", command=choose, width=10).pack(side=tk.RIGHT)
        ttk.Button(btn_row, text="取消", command=dlg.destroy, width=10).pack(side=tk.RIGHT, padx=(0, 6))
        tree.bind("<Double-1>", lambda _e: choose())

    def _apply_instance_from_window(self, win: game_util.GameWindow) -> None:
        self._bound_instance_id = ""
        self.instance_id.set(win.instance_id)
        self.log_line(f"[实例] {win.instance_id}  PID={win.pid}  标题={win.title}")
        self.status_var.set(f"已绑定 {win.instance_id}，请点「刷新桥接」")
        self.refresh_bridge(verbose=True)

    def _set_packet_burst_ui(self, active: bool) -> None:
        self._packet_burst_active = active
        burst_state = tk.DISABLED if active else tk.NORMAL
        cancel_state = tk.NORMAL if active else tk.DISABLED
        for w in (self.packet_send_btn, self.packet_burst_btn):
            w.config(state=burst_state)
        self.packet_burst_cancel_btn.config(state=cancel_state)

    def on_packet_send_once(self) -> None:
        if self._packet_burst_active:
            return
        built = self._build_current_packet_send_fn()
        if not built:
            return
        label, send_fn = built
        self._packet_log_line(f"[发送] {label}")
        self._send_packet(label, send_fn, wait_ack=True)

    def on_packet_send_burst(self) -> None:
        if self._packet_burst_active:
            return
        try:
            interval_ms = int(self.packet_interval_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "连续间隔必须是整数毫秒")
            return
        if interval_ms < 50:
            messagebox.showwarning("输入无效", "连续间隔不能小于 50 毫秒")
            return
        built = self._build_current_packet_send_fn()
        if not built:
            return
        label, send_fn = built
        self._set_packet_burst_ui(True)
        self._packet_log_line(f"[连续] 启动 {label}  间隔 {interval_ms}ms")
        self._packet_burst_send_fn = send_fn
        self._packet_burst_label = label
        self._packet_burst_interval_ms = interval_ms
        self._packet_burst_tick()

    def on_packet_send_cancel(self) -> None:
        if not self._packet_burst_active:
            return
        self._packet_burst_active = False
        if self._packet_burst_after_id is not None:
            try:
                self.root.after_cancel(self._packet_burst_after_id)
            except tk.TclError:
                pass
            self._packet_burst_after_id = None
        self._set_packet_burst_ui(False)
        self._packet_log_line("[连续] 已取消")

    def _packet_burst_tick(self) -> None:
        if not self._packet_burst_active:
            return
        self._packet_log_line(f"[连续] {self._packet_burst_label}")
        self._send_packet(self._packet_burst_label, self._packet_burst_send_fn, wait_ack=False)
        self._packet_burst_after_id = self.root.after(
            self._packet_burst_interval_ms,
            self._packet_burst_tick,
        )

    def _send_packet(
        self,
        label: str,
        send_fn,
        *,
        wait_ack: bool,
        on_done: Callable[[bool], None] | None = None,
    ) -> None:
        if (
            self._auto_running
            and not self._packet_burst_active
            and not self._use_item_seq_active
            and not self._special_pkg_active
        ):
            return
        iid = self._require_instance()
        if not iid:
            if self._packet_burst_active:
                self.on_packet_send_cancel()
            if self._use_item_seq_active:
                self.on_use_item_seq_cancel()
            if self._special_pkg_active:
                self.on_special_pkg_cancel()
            return
        if not ipc.bridge_alive(iid):
            if not messagebox.askyesno(
                "桥接未就绪",
                "未检测到游戏内助手桥接。\n\n仍要发送命令？",
            ):
                if self._packet_burst_active:
                    self.on_packet_send_cancel()
                if self._use_item_seq_active:
                    self.on_use_item_seq_cancel()
                if self._special_pkg_active:
                    self.on_special_pkg_cancel()
                return

        def do() -> None:
            ok = True
            try:
                req = send_fn(iid)
                self.root.after(0, lambda: self._packet_log_line(f"[CMD] id={req}"))
                if wait_ack:
                    ok, ack = ipc.wait_ack(iid, req, timeout=45)
                    msg = (ack or {}).get("msg", "") if ack else ""
                    self.root.after(0, lambda: self._packet_log_line(f"[ACK] ok={ok} {msg}"))
            except Exception as exc:
                ok = False
                self.root.after(0, lambda: self._packet_log_line(f"[ERR] {exc}"))
                if self._packet_burst_active:
                    self.root.after(0, self.on_packet_send_cancel)
                if self._use_item_seq_active:
                    self.root.after(0, self.on_use_item_seq_cancel)
                if self._special_pkg_active:
                    self.root.after(0, self.on_special_pkg_cancel)
            finally:
                if on_done is not None:
                    self.root.after(0, lambda: on_done(ok))

        threading.Thread(target=do, daemon=True).start()

    def on_packet_fill_preset(self) -> None:
        name = self.packet_preset_var.get().strip()
        for label, opcode, proto, fields_json in self._packet_presets:
            if label == name:
                self.packet_opcode_var.set(str(opcode))
                self.packet_proto_var.set(proto)
                self.packet_fields_text.delete("1.0", tk.END)
                self.packet_fields_text.insert(tk.END, fields_json)
                self.packet_b64_var.set("")
                return

    def _packet_log_line(self, msg: str) -> None:
        if not hasattr(self, "packet_log"):
            return
        from datetime import datetime

        self.packet_log.insert(tk.END, f"{datetime.now():%H:%M:%S} {msg}\n")
        self.packet_log.see(tk.END)

    def _build_game_tab(self, parent: ttk.Frame) -> None:
        team_frm = ttk.LabelFrame(parent, text="队伍", padding=8)
        team_frm.pack(fill=tk.X, pady=(0, 8))
        self.team_status_var = tk.StringVar(value="队伍：-")
        ttk.Label(team_frm, textvariable=self.team_status_var, wraplength=480).pack(anchor=tk.W)

        pool_frm = ttk.LabelFrame(parent, text="血池 / 魔池（全队共用）", padding=8)
        pool_frm.pack(fill=tk.X, pady=(0, 8))
        self.pool_hp_var = tk.StringVar(value="血池：-")
        self.pool_mp_var = tk.StringVar(value="魔池：-")
        ttk.Label(pool_frm, textvariable=self.pool_hp_var, font=("Microsoft YaHei UI", 10)).pack(anchor=tk.W)
        ttk.Label(pool_frm, textvariable=self.pool_mp_var, font=("Microsoft YaHei UI", 10)).pack(anchor=tk.W, pady=(4, 0))

        char_frm = ttk.LabelFrame(parent, text="各角色", padding=8)
        char_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 8))

        cols = ("name", "hp", "mp", "hp_pool", "mp_pool", "stone", "vip")
        self.char_tree = ttk.Treeview(
            char_frm,
            columns=cols,
            show="headings",
            height=6,
        )
        headings = {
            "name": ("角色", 72),
            "hp": ("HP", 72),
            "mp": ("MP", 72),
            "hp_pool": ("血池", 44),
            "mp_pool": ("魔池", 44),
            "stone": ("每日掉落", 96),
            "vip": ("VIP", 44),
        }
        for col, (text, width) in headings.items():
            self.char_tree.heading(col, text=text)
            self.char_tree.column(col, width=width, anchor=tk.CENTER if col != "name" else tk.W)
        self.char_tree.pack(fill=tk.BOTH, expand=True)

        ttk.Label(
            parent,
            text="HP/MP 由心跳自动更新；血池/魔池开关、魔石、VIP 需点「刷新资源」向服务器拉取 BUFF",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
            wraplength=480,
        ).pack(anchor=tk.W, pady=(0, 4))
        refresh_res_btn = ttk.Button(
            parent,
            text="刷新资源（主动请求）",
            command=self.on_fetch_resource,
        )
        refresh_res_btn.pack(anchor=tk.W)
        self._action_widgets.append(refresh_res_btn)

    def _build_nav_tab(self, parent: ttk.Frame) -> None:
        pos_frm = ttk.LabelFrame(parent, text="当前位置（心跳自动更新）", padding=8)
        pos_frm.pack(fill=tk.X, pady=(0, 8))
        self.nav_pos_var = tk.StringVar(value="地图：-  坐标：-")
        ttk.Label(pos_frm, textvariable=self.nav_pos_var, font=("Consolas", 10)).pack(anchor=tk.W)
        self.nav_detail_var = tk.StringVar(value="mapId=- floor=- currentFloor=-")
        ttk.Label(pos_frm, textvariable=self.nav_detail_var, font=("Consolas", 8), foreground="#666").pack(
            anchor=tk.W, pady=(4, 0)
        )

        log_frm = ttk.LabelFrame(parent, text="位置变化记录", padding=6)
        log_frm.pack(fill=tk.X, pady=(0, 8))
        self.nav_log = scrolledtext.ScrolledText(log_frm, height=4, font=("Consolas", 9))
        self.nav_log.pack(fill=tk.X)

        target_frm = ttk.LabelFrame(parent, text="目标坐标", padding=8)
        target_frm.pack(fill=tk.X, pady=(0, 8))
        self.nav_name_var = tk.StringVar()
        self.nav_floor_var = tk.StringVar()
        self.nav_x_var = tk.StringVar()
        self.nav_y_var = tk.StringVar()
        ttk.Label(target_frm, text="名称").grid(row=0, column=0, sticky=tk.W, pady=2)
        ttk.Entry(target_frm, textvariable=self.nav_name_var, width=28).grid(row=0, column=1, sticky=tk.EW, pady=2)
        ttk.Label(target_frm, text="地图号(floor)").grid(row=1, column=0, sticky=tk.W, pady=2)
        ttk.Entry(target_frm, textvariable=self.nav_floor_var, width=12).grid(row=1, column=1, sticky=tk.W, pady=2)
        xy_row = ttk.Frame(target_frm)
        xy_row.grid(row=2, column=0, columnspan=2, sticky=tk.W, pady=2)
        ttk.Label(xy_row, text="X").pack(side=tk.LEFT)
        ttk.Entry(xy_row, textvariable=self.nav_x_var, width=8).pack(side=tk.LEFT, padx=(4, 12))
        ttk.Label(xy_row, text="Y").pack(side=tk.LEFT)
        ttk.Entry(xy_row, textvariable=self.nav_y_var, width=8).pack(side=tk.LEFT, padx=4)
        target_frm.columnconfigure(1, weight=1)

        save_row = ttk.Frame(target_frm)
        save_row.grid(row=3, column=0, columnspan=2, sticky=tk.EW, pady=(8, 0))
        ttk.Button(save_row, text="填入当前位置", command=self.on_nav_fill_current, width=14).pack(
            side=tk.LEFT, padx=(0, 6)
        )
        ttk.Button(save_row, text="保存坐标点", command=self.on_nav_save_waypoint, width=12).pack(side=tk.LEFT)

        wp_frm = ttk.LabelFrame(parent, text="已保存坐标", padding=6)
        wp_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 8))
        cols = ("name", "floor", "x", "y", "map_id")
        self.waypoint_tree = ttk.Treeview(wp_frm, columns=cols, show="headings", height=5)
        for col, text, width in (
            ("name", "名称", 100),
            ("floor", "地图号", 64),
            ("x", "X", 48),
            ("y", "Y", 48),
            ("map_id", "mapId", 56),
        ):
            self.waypoint_tree.heading(col, text=text)
            self.waypoint_tree.column(col, width=width, anchor=tk.CENTER if col != "name" else tk.W)
        self.waypoint_tree.pack(fill=tk.BOTH, expand=True)
        self.waypoint_tree.bind("<<TreeviewSelect>>", self.on_waypoint_select)
        self.waypoint_tree.bind("<Double-1>", lambda _e: self.on_nav_to_selected())
        wp_btn_row = ttk.Frame(wp_frm)
        wp_btn_row.pack(fill=tk.X, pady=(6, 0))
        ttk.Button(wp_btn_row, text="删除选中", command=self.on_nav_delete_waypoint, width=10).pack(side=tk.LEFT)
        ttk.Button(wp_btn_row, text="载入到目标", command=self.on_nav_load_selected, width=12).pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Button(wp_btn_row, text="导航", command=self.on_nav_waypoint_general, width=10).pack(
            side=tk.LEFT, padx=(6, 0)
        )

        nav_frm = ttk.LabelFrame(parent, text="寻路命令", padding=8)
        nav_frm.pack(fill=tk.X)
        ttk.Label(
            nav_frm,
            text="地图号即 MapPoint.mapIndex（与 currentFloor 一致）。跨图请用「导航」。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=480,
        ).pack(anchor=tk.W, pady=(0, 6))
        row1 = ttk.Frame(nav_frm)
        row1.pack(fill=tk.X, pady=(0, 4))
        ttk.Button(row1, text="导航 (GeneralPoint)", command=self.on_nav_general, width=22).pack(
            side=tk.LEFT, padx=(0, 6)
        )
        ttk.Button(row1, text="Walk MapPoint", command=self.on_nav_walk_map, width=16).pack(side=tk.LEFT)
        row2 = ttk.Frame(nav_frm)
        row2.pack(fill=tk.X, pady=(0, 4))
        ttk.Button(row2, text="同图寻路 (Vector2Int)", command=self.on_nav_walk_same, width=22).pack(
            side=tk.LEFT, padx=(0, 6)
        )
        ttk.Button(row2, text="任务寻路 (TaskMoveTo)", command=self.on_nav_task, width=16).pack(side=tk.LEFT)
        ttk.Button(nav_frm, text="停止寻路", command=self.on_nav_stop).pack(anchor=tk.W, pady=(6, 0))

        self._reload_waypoint_list()

    def log_line(self, msg: str) -> None:
        self.log.insert(tk.END, msg + "\n")
        self.log.see(tk.END)

    def _schedule_bridge_refresh(self) -> None:
        self.refresh_bridge()
        self.root.after(1500, self._schedule_bridge_refresh)

    def _set_actions_locked(self, locked: bool) -> None:
        state = tk.DISABLED if locked else tk.NORMAL
        for w in self._action_widgets:
            w.config(state=state)

    def _require_instance(self) -> str | None:
        iid = self._target_instance_id()
        if not iid:
            messagebox.showwarning("缺少实例 ID", "请填写实例 ID（多开器启动时会自动分配）")
            return None
        return iid

    def refresh_bridge(self, verbose: bool = False) -> None:
        iid = self._target_instance_id()
        allow_auto_match = not bool(self._bound_instance_id)
        if verbose:
            diag = ipc.diagnose_bridge(
                iid if iid else None,
                allow_auto_match=allow_auto_match,
            )
        else:
            diag = ipc.bridge_status_quick(
                iid if iid else None,
                allow_auto_match=allow_auto_match,
            )

        resolved = diag.get("instance_id") or ""
        if resolved and resolved != iid and allow_auto_match:
            self.instance_id.set(resolved)
        elif self._bound_instance_id:
            self.instance_id.set(self._bound_instance_id)

        summary = diag.get("summary", "桥接：未知")
        detail = diag.get("detail", "")
        self.bridge_var.set(summary)
        self.bridge_detail_var.set(detail)

        if verbose or summary != self._last_bridge_summary:
            if verbose:
                from datetime import datetime

                self.log_line(f"── 桥接诊断 {datetime.now():%H:%M:%S} ──")
                for line in diag.get("lines", []):
                    self.log_line(line)
            self._last_bridge_summary = summary

        self._update_game_panel(diag.get("state") or {})
        self._update_nav_panel(diag.get("state") or {})
        self._update_npc_panel(diag.get("state") or {})
        st = diag.get("state") or {}
        self._last_bridge_state = st
        self._update_packet_chars(st)

        if self._auto_mode and not self._auto_finished and not diag.get("alive"):
            if self._bound_instance_id:
                self.status_var.set(f"等待 {self._bound_instance_id} 桥接…")

        if diag.get("alive"):
            self._maybe_start_auto_workflow()
            return

        if self._auto_mode and not self._auto_finished:
            self._set_actions_locked(True)

    def _target_instance_id(self) -> str:
        if self._bound_instance_id:
            return self._bound_instance_id
        return self.instance_id.get().strip()

    def _maybe_start_auto_workflow(self) -> None:
        if not self._auto_mode or self._auto_started:
            return
        iid = self._target_instance_id()
        phone = self.phone_var.get().strip()
        pwd = self.password_var.get()
        if not iid or not phone or not pwd:
            return
        if not ipc.bridge_alive(iid):
            return

        self._auto_started = True
        self._auto_running = True
        self._set_actions_locked(True)
        self.status_var.set("桥接已连接，自动流程执行中…")
        self.log_line("[AUTO] 桥接就绪，开始：登录 → 进游戏 → 五控 → 召唤")

        def worker() -> None:
            ok = False
            msg = ""
            try:
                req = ipc.workflow_step1_five_chars(iid, phone, pwd)
                self.root.after(0, lambda: self.log_line(f"[AUTO] 已下发 workflow_step1 id={req}"))
                ok, msg = ipc.wait_workflow_done(iid, timeout=360)
                self.root.after(0, lambda: self.log_line(f"[AUTO] 结束 ok={ok} {msg}"))
            except Exception as exc:
                ok = False
                msg = str(exc)
                self.root.after(0, lambda: self.log_line(f"[AUTO] 异常 {exc}"))
            finally:
                self._auto_running = False
                self._auto_finished = True

                def done() -> None:
                    self._set_actions_locked(False)
                    if ok:
                        self.status_var.set("自动流程完成")
                    else:
                        self.status_var.set("自动流程失败")
                        messagebox.showerror("自动流程失败", msg or "未知错误")
                    self.refresh_bridge()

                self.root.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    @staticmethod
    def _split_field(st: dict, key: str) -> list[str]:
        return str(st.get(key) or "").split("|")

    @staticmethod
    def _on_off(raw: str) -> str:
        if raw == "1":
            return "开"
        if raw == "0":
            return "关"
        return "?"

    def _update_game_panel(self, st: dict) -> None:
        if not st:
            return

        team_num = int(st.get("team_num") or 0)
        team_ok = bool(st.get("team_ok"))
        leader = str(st.get("team_leader_uid") or "-")
        slot0 = str(st.get("multi_slot0_uid") or "-")
        self.team_status_var.set(
            f"{team_num}/5 {'✓ 已组满' if team_ok else '× 未就绪'}  "
            f"队长 UID={leader}  ·  1号 UID={slot0}"
        )

        pool_max = int(st.get("hp_mp_pool_max") or 0)
        pool_hp = st.get("pool_hp")
        pool_mp = st.get("pool_mp")
        pool_hp_max = st.get("pool_hp_max")
        pool_mp_max = st.get("pool_mp_max")
        buff_ready = st.get("buff_ready")
        if buff_ready:
            self.pool_hp_var.set(f"血池  {pool_hp}/{pool_hp_max}  （单格上限 {pool_max}）")
            self.pool_mp_var.set(f"魔池  {pool_mp}/{pool_mp_max}  （单格上限 {pool_max}）")
        else:
            self.pool_hp_var.set(f"血池  待刷新  （单格上限 {pool_max or '?'}）")
            self.pool_mp_var.set(f"魔池  待刷新  （单格上限 {pool_max or '?'}）")

        names = self._split_field(st, "char_names")
        hps = self._split_field(st, "char_hp")
        mps = self._split_field(st, "char_mp")
        hp_on = self._split_field(st, "char_hp_pool_on")
        mp_on = self._split_field(st, "char_mp_pool_on")
        stones = self._split_field(st, "char_stone")
        stone_limits = self._split_field(st, "char_stone_limit")
        vips = self._split_field(st, "char_vip")

        for item in self.char_tree.get_children():
            self.char_tree.delete(item)

        for i, name in enumerate(names):
            if name == "-" or not name:
                continue
            hp = hps[i] if i < len(hps) else "-"
            mp = mps[i] if i < len(mps) else "-"
            hp_pool = self._on_off(hp_on[i]) if i < len(hp_on) else "?"
            mp_pool = self._on_off(mp_on[i]) if i < len(mp_on) else "?"
            stone = stones[i] if i < len(stones) else "?"
            limit = stone_limits[i] if i < len(stone_limits) else "?"
            stone_txt = f"{stone}/{limit}" if stone != "-" else "-"
            vip = "是" if (i < len(vips) and vips[i] == "1") else ("否" if i < len(vips) and vips[i] == "0" else "?")
            self.char_tree.insert(
                "",
                tk.END,
                values=(f"{i + 1}.{name}", hp, mp, hp_pool, mp_pool, stone_txt, vip),
            )

    def _update_nav_panel(self, st: dict) -> None:
        if not st or not hasattr(self, "nav_pos_var"):
            return

        map_id = int(st.get("pos_map_id") or 0)
        floor = int(st.get("pos_floor") or 0)
        current_floor = int(st.get("pos_current_floor") or 0)
        nav_floor = int(st.get("pos_nav_floor") or 0)
        x = int(st.get("pos_x") or 0)
        y = int(st.get("pos_y") or 0)
        pos_key = str(st.get("pos_key") or "")

        if nav_floor > 0 or x > 0 or y > 0 or map_id > 0:
            self.nav_pos_var.set(f"地图号 {nav_floor}  ·  坐标 ({x}, {y})")
            self.nav_detail_var.set(
                f"mapId={map_id}  playerData.floor={floor}  MapManager.currentFloor={current_floor}"
            )
        else:
            self.nav_pos_var.set("地图：未进游戏 / 无坐标")
            self.nav_detail_var.set("mapId=- floor=- currentFloor=-")

        if pos_key and pos_key != self._last_pos_key:
            if self._last_pos_key:
                old_floor = self._last_pos_floor
                if old_floor is not None and old_floor != nav_floor:
                    self._nav_log_line(f"[切图] {old_floor} → {nav_floor}  坐标 ({x},{y})")
                else:
                    self._nav_log_line(f"[移动] 地图号 {nav_floor}  ({x},{y})")
            self._last_pos_key = pos_key
            self._last_pos_floor = nav_floor

    def _build_npc_tab(self, parent: ttk.Frame) -> None:
        info_frm = ttk.LabelFrame(parent, text="当前地图 NPC（切图自动刷新）", padding=8)
        info_frm.pack(fill=tk.X, pady=(0, 6))
        self.npc_info_var = tk.StringVar(value="地图号：-  数量：-")
        ttk.Label(info_frm, textvariable=self.npc_info_var, font=("Consolas", 10)).pack(anchor=tk.W)
        ttk.Label(
            info_frm,
            text="来自 characterDatas（含 LuaNpc 学技能老师；不含玩家/敌人/宠物/摊位）。Objindex 切图/重登会变。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(4, 0))

        list_frm = ttk.LabelFrame(parent, text="当前地图列表", padding=6)
        list_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 6))
        cols = ("objindex", "name", "x", "y", "type")
        self.npc_tree = ttk.Treeview(list_frm, columns=cols, show="headings", height=8)
        for col, text, width in (
            ("objindex", "Objindex", 72),
            ("name", "名字", 140),
            ("x", "X", 48),
            ("y", "Y", 48),
            ("type", "类型", 100),
        ):
            self.npc_tree.heading(col, text=text)
            self.npc_tree.column(col, width=width, anchor=tk.CENTER if col != "name" else tk.W)
        scroll = ttk.Scrollbar(list_frm, orient=tk.VERTICAL, command=self.npc_tree.yview)
        self.npc_tree.configure(yscrollcommand=scroll.set)
        self.npc_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.npc_tree.bind("<<TreeviewSelect>>", self._on_npc_select)

        btn_row = ttk.Frame(parent)
        btn_row.pack(fill=tk.X, pady=(0, 6))
        ttk.Button(btn_row, text="复制 Objindex", command=self.on_npc_copy_objindex, width=12).pack(
            side=tk.LEFT
        )
        ttk.Button(btn_row, text="复制选中行", command=self.on_npc_copy_row, width=10).pack(
            side=tk.LEFT, padx=(6, 0)
        )
        ttk.Button(btn_row, text="立即刷新", command=lambda: self.refresh_bridge(verbose=False), width=8).pack(
            side=tk.LEFT, padx=(6, 0)
        )

        save_frm = ttk.LabelFrame(parent, text="保存 NPC（可加备注，供学技能调用）", padding=6)
        save_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 6))
        note_row = ttk.Frame(save_frm)
        note_row.pack(fill=tk.X, pady=(0, 4))
        ttk.Label(note_row, text="备注:").pack(side=tk.LEFT)
        self.npc_save_note_var = tk.StringVar(value="")
        ttk.Entry(note_row, textvariable=self.npc_save_note_var).pack(
            side=tk.LEFT, padx=(4, 8), fill=tk.X, expand=True
        )
        ttk.Button(note_row, text="保存选中", command=self.on_npc_save_bookmark, width=10).pack(
            side=tk.LEFT
        )
        ttk.Label(
            save_frm,
            text="例：学超补、医奶老师。保存后可在「发包 → 学技能」里调用。",
            font=("Microsoft YaHei UI", 8),
            foreground="#666",
        ).pack(anchor=tk.W)

        saved_list = ttk.Frame(save_frm)
        saved_list.pack(fill=tk.BOTH, expand=True, pady=(4, 0))
        saved_cols = ("note", "name", "objindex", "floor", "xy")
        self.npc_saved_tree = ttk.Treeview(saved_list, columns=saved_cols, show="headings", height=5)
        for col, text, width in (
            ("note", "备注", 100),
            ("name", "名字", 100),
            ("objindex", "Objindex", 72),
            ("floor", "地图", 48),
            ("xy", "坐标", 72),
        ):
            self.npc_saved_tree.heading(col, text=text)
            self.npc_saved_tree.column(col, width=width, anchor=tk.W if col in ("note", "name") else tk.CENTER)
        saved_scroll = ttk.Scrollbar(saved_list, orient=tk.VERTICAL, command=self.npc_saved_tree.yview)
        self.npc_saved_tree.configure(yscrollcommand=saved_scroll.set)
        self.npc_saved_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        saved_scroll.pack(side=tk.RIGHT, fill=tk.Y)

        saved_btn = ttk.Frame(parent)
        saved_btn.pack(fill=tk.X)
        ttk.Button(saved_btn, text="删除已存", command=self.on_npc_delete_bookmark, width=10).pack(
            side=tk.LEFT
        )
        ttk.Button(
            saved_btn, text="更新 Objindex（按同名匹配当前图）", command=self.on_npc_refresh_bookmark_obj, width=28
        ).pack(side=tk.LEFT, padx=(6, 0))
        ttk.Button(
            saved_btn, text="用于学技能", command=self.on_npc_use_bookmark_for_skill, width=10
        ).pack(side=tk.LEFT, padx=(6, 0))

        log_frm = ttk.LabelFrame(parent, text="切图记录", padding=6)
        log_frm.pack(fill=tk.X, pady=(8, 0))
        self.npc_log = scrolledtext.ScrolledText(log_frm, height=2, font=("Consolas", 9))
        self.npc_log.pack(fill=tk.X)

        self._refresh_npc_saved_tree()

    def _update_npc_panel(self, st: dict) -> None:
        if not hasattr(self, "npc_tree"):
            return
        floor = int(st.get("npc_map_floor") or st.get("pos_nav_floor") or st.get("pos_current_floor") or 0)
        npcs = ipc.parse_npc_roster(st)
        count = int(st.get("npc_count") or len(npcs))
        sig = str(st.get("npc_objindex") or "")

        if floor and self._last_npc_floor is not None and floor != self._last_npc_floor:
            self._npc_log_line(f"[切图] {self._last_npc_floor} → {floor}  NPC {count} 个")

        if floor != self._last_npc_floor or sig != self._last_npc_signature:
            for item in self.npc_tree.get_children():
                self.npc_tree.delete(item)
            for i, row in enumerate(npcs):
                self.npc_tree.insert(
                    "",
                    tk.END,
                    iid=f"{row['objindex']}_{i}",
                    values=(row["objindex"], row["name"], row["x"], row["y"], row["type"]),
                )
            self._last_npc_floor = floor if floor else self._last_npc_floor
            self._last_npc_signature = sig

        if floor > 0 or count > 0:
            self.npc_info_var.set(f"地图号 {floor or '-'}  ·  NPC {count} 个")
        else:
            self.npc_info_var.set("地图号：-  数量：-（未进游戏或桥接未上报）")

    def _npc_log_line(self, msg: str) -> None:
        if not hasattr(self, "npc_log"):
            return
        from datetime import datetime

        self.npc_log.insert(tk.END, f"{datetime.now():%H:%M:%S} {msg}\n")
        self.npc_log.see(tk.END)

    def _selected_npc_values(self) -> tuple | None:
        sel = self.npc_tree.selection() if hasattr(self, "npc_tree") else ()
        if not sel:
            return None
        vals = self.npc_tree.item(sel[0], "values")
        return vals if vals else None

    def _on_npc_select(self, _event=None) -> None:
        vals = self._selected_npc_values()
        self._selected_npc_objindex = str(vals[0]) if vals else ""

    def on_npc_copy_objindex(self) -> None:
        vals = self._selected_npc_values()
        oid = str(vals[0]) if vals else self._selected_npc_objindex
        if not oid:
            messagebox.showinfo("复制", "请先选中一个 NPC")
            return
        self.root.clipboard_clear()
        self.root.clipboard_append(oid)
        self.status_var.set(f"已复制 Objindex={oid}")

    def on_npc_copy_row(self) -> None:
        vals = self._selected_npc_values()
        if not vals:
            messagebox.showinfo("复制", "请先选中一个 NPC")
            return
        text = "\t".join(str(v) for v in vals)
        self.root.clipboard_clear()
        self.root.clipboard_append(text)
        self.status_var.set("已复制选中行")

    def _refresh_npc_saved_tree(self) -> None:
        if not hasattr(self, "npc_saved_tree"):
            return
        for item in self.npc_saved_tree.get_children():
            self.npc_saved_tree.delete(item)
        for it in npc_bookmarks.list_bookmarks():
            self.npc_saved_tree.insert(
                "",
                tk.END,
                iid=str(it.get("id")),
                values=(
                    it.get("note") or "",
                    it.get("name") or "",
                    it.get("objindex") or "",
                    it.get("floor") or "",
                    f"{it.get('x', 0)},{it.get('y', 0)}",
                ),
            )
        self._refresh_skill_saved_npc_cb()

    def _selected_npc_bookmark_id(self) -> str | None:
        if not hasattr(self, "npc_saved_tree"):
            return None
        sel = self.npc_saved_tree.selection()
        return sel[0] if sel else None

    def on_npc_save_bookmark(self) -> None:
        vals = self._selected_npc_values()
        if not vals:
            messagebox.showinfo("保存", "请先在上方列表选中一个 NPC")
            return
        try:
            objindex = int(vals[0])
        except (TypeError, ValueError):
            messagebox.showwarning("保存", "Objindex 无效")
            return
        name = str(vals[1]) if len(vals) > 1 else "-"
        try:
            x = int(vals[2]) if len(vals) > 2 else 0
            y = int(vals[3]) if len(vals) > 3 else 0
        except ValueError:
            x, y = 0, 0
        npc_type = str(vals[4]) if len(vals) > 4 else ""
        floor = int((self._last_bridge_state or {}).get("npc_map_floor") or self._last_npc_floor or 0)
        note = self.npc_save_note_var.get().strip()
        if not note:
            note = simpledialog.askstring("备注", f"给「{name}」加个备注（可空，默认用名字）:", parent=self.root) or ""
        item = npc_bookmarks.add_bookmark(
            objindex=objindex,
            name=name,
            x=x,
            y=y,
            floor=floor,
            npc_type=npc_type,
            note=note,
        )
        self.npc_save_note_var.set("")
        self._refresh_npc_saved_tree()
        self._npc_log_line(f"[保存] {npc_bookmarks.bookmark_label(item)}")
        self.status_var.set(f"已保存 NPC：{item.get('note')}")

    def on_npc_delete_bookmark(self) -> None:
        bid = self._selected_npc_bookmark_id()
        if not bid:
            messagebox.showinfo("删除", "请先在已存列表中选中一项")
            return
        item = npc_bookmarks.get_bookmark(bid)
        label = npc_bookmarks.bookmark_label(item) if item else bid
        if not messagebox.askyesno("删除", f"删除已存 NPC？\n{label}"):
            return
        if npc_bookmarks.delete_bookmark(bid):
            self._refresh_npc_saved_tree()
            self._npc_log_line(f"[删除] {label}")

    def on_npc_refresh_bookmark_obj(self) -> None:
        bid = self._selected_npc_bookmark_id()
        if not bid:
            messagebox.showinfo("更新", "请先在已存列表中选中一项")
            return
        item = npc_bookmarks.get_bookmark(bid)
        if not item:
            return
        name = str(item.get("name") or "")
        live = self._find_live_npc_by_name(name)
        if not live:
            messagebox.showwarning(
                "更新",
                f"当前地图找不到同名 NPC「{name}」。\n请先进对的图并刷新列表。",
            )
            return
        updated = npc_bookmarks.update_bookmark(
            bid,
            objindex=int(live["objindex"]),
            x=int(live.get("x") or 0),
            y=int(live.get("y") or 0),
            floor=int((self._last_bridge_state or {}).get("npc_map_floor") or 0),
            type=live.get("type") or item.get("type") or "",
        )
        self._refresh_npc_saved_tree()
        if updated:
            self.npc_saved_tree.selection_set(bid)
            self._npc_log_line(
                f"[更新] {npc_bookmarks.bookmark_label(updated)} ← 当前图匹配"
            )
            self.status_var.set(f"已更新 Objindex={updated.get('objindex')}")

    def on_npc_use_bookmark_for_skill(self) -> None:
        bid = self._selected_npc_bookmark_id()
        if not bid:
            messagebox.showinfo("学技能", "请先在已存列表中选中一项")
            return
        if not self._apply_npc_bookmark_to_skill(bid):
            return
        self.packet_type_var.set("学技能")
        self._show_packet_form()
        self.status_var.set("已填入学技能页，请核对角色与技能后发包")

    def _find_live_npc_by_name(self, name: str) -> dict[str, str] | None:
        name = (name or "").strip()
        if not name or name == "-":
            return None
        st = self._last_bridge_state or {}
        for row in ipc.parse_npc_roster(st):
            if str(row.get("name") or "").strip() == name:
                return row
        return None

    def _apply_npc_bookmark_to_skill(self, bookmark_id: str, *, target: str = "char") -> bool:
        item = npc_bookmarks.get_bookmark(bookmark_id)
        if not item:
            messagebox.showwarning("NPC", "找不到该书签")
            return False
        name = str(item.get("name") or "")
        live = self._find_live_npc_by_name(name)
        if live:
            oid = int(live["objindex"])
            npc_bookmarks.update_bookmark(
                bookmark_id,
                objindex=oid,
                x=int(live.get("x") or 0),
                y=int(live.get("y") or 0),
                floor=int((self._last_bridge_state or {}).get("npc_map_floor") or item.get("floor") or 0),
            )
            if target == "pet":
                self.packet_pet_skill_npc_var.set(str(oid))
            else:
                self.packet_skill_npc_var.set(str(oid))
            self._refresh_npc_saved_tree()
            self._refresh_skill_saved_npc_cb()
            self.status_var.set(f"已用当前图「{name}」Objindex={oid}")
        else:
            oid = int(item.get("objindex") or 0)
            if target == "pet":
                self.packet_pet_skill_npc_var.set(str(oid))
            else:
                self.packet_skill_npc_var.set(str(oid))
            messagebox.showwarning(
                "Objindex 可能过期",
                f"当前图未找到同名「{name}」，已填入保存时的 Objindex={oid}。\n"
                "切图/重登后编号会变，建议进对的图后点「更新 Objindex」。",
            )
        label = npc_bookmarks.bookmark_label(npc_bookmarks.get_bookmark(bookmark_id) or item)
        if target == "pet" and hasattr(self, "packet_pet_skill_saved_cb"):
            labels = list(self.packet_pet_skill_saved_cb["values"] or [])
            if label in labels:
                self.packet_pet_skill_saved_var.set(label)
        elif hasattr(self, "packet_skill_saved_cb"):
            labels = list(self.packet_skill_saved_cb["values"] or [])
            if label in labels:
                self.packet_skill_saved_var.set(label)
        return True

    def _refresh_skill_saved_npc_cb(self) -> None:
        labels: list[str] = []
        self._skill_saved_options: list[tuple[str, str]] = []
        for it in npc_bookmarks.list_bookmarks():
            label = npc_bookmarks.bookmark_label(it)
            labels.append(label)
            self._skill_saved_options.append((label, str(it.get("id"))))

        for cb_name, var_name in (
            ("packet_skill_saved_cb", "packet_skill_saved_var"),
            ("packet_pet_skill_saved_cb", "packet_pet_skill_saved_var"),
        ):
            if not hasattr(self, cb_name):
                continue
            cb = getattr(self, cb_name)
            var = getattr(self, var_name)
            prev = var.get()
            cb["values"] = labels
            if prev in labels:
                var.set(prev)
            elif labels:
                var.set(labels[0])
            else:
                var.set("")

    def on_packet_skill_use_saved_npc(self) -> None:
        label = self.packet_skill_saved_var.get().strip()
        bid = None
        for lbl, iid in getattr(self, "_skill_saved_options", []):
            if lbl == label:
                bid = iid
                break
        if not bid:
            messagebox.showinfo("调用 NPC", "请先在 NPC 页保存，并在下拉中选择")
            return
        self._apply_npc_bookmark_to_skill(bid, target="char")

    def on_packet_pet_skill_use_saved_npc(self) -> None:
        label = self.packet_pet_skill_saved_var.get().strip()
        bid = None
        for lbl, iid in getattr(self, "_skill_saved_options", []):
            if lbl == label:
                bid = iid
                break
        if not bid:
            messagebox.showinfo("调用 NPC", "请先在 NPC 页保存，并在下拉中选择")
            return
        self._apply_npc_bookmark_to_skill(bid, target="pet")

    def on_packet_pet_skill_fill_npc(self) -> None:
        vals = self._selected_npc_values() if hasattr(self, "_selected_npc_values") else None
        oid = str(vals[0]) if vals else str(getattr(self, "_selected_npc_objindex", "") or "")
        if not oid:
            messagebox.showinfo("填入 NPC", "请先到「NPC」页选中一个 NPC")
            return
        self.packet_pet_skill_npc_var.set(oid)
        self.status_var.set(f"已填入 NPC Objindex={oid}")

    def _refresh_pet_skill_id_hint(self) -> None:
        if not hasattr(self, "packet_pet_skill_name_var"):
            return
        raw = self.packet_pet_skill_id_var.get().strip()
        if not raw:
            self.packet_pet_skill_name_var.set("名称：-")
            return
        try:
            sid = int(raw)
        except ValueError:
            self.packet_pet_skill_name_var.set("名称：无效 ID")
            return
        name = self._pet_skill_id_name_map().get(sid)
        self.packet_pet_skill_name_var.set(f"名称：{name}" if name else "名称：未知（仍可发包）")

    def _refresh_pet_skill_pet_cb(self) -> None:
        if not hasattr(self, "packet_pet_skill_pet_cb"):
            return
        st = self._last_bridge_state or {}
        pets = ipc.parse_pet_roster(st)
        self._pet_skill_options: list[tuple[str, str, str]] = []
        labels: list[str] = []
        for p in pets:
            label = f"槽{p['slot']} Lv{p['level']} {p['name']} (Index={p['index']})"
            labels.append(label)
            self._pet_skill_options.append((label, p["index"], p["name"]))
        prev = self.packet_pet_skill_pet_var.get()
        self.packet_pet_skill_pet_cb["values"] = labels
        if prev in labels:
            self.packet_pet_skill_pet_var.set(prev)
        elif labels:
            self.packet_pet_skill_pet_var.set(labels[0])
        else:
            self.packet_pet_skill_pet_var.set("")

    def _resolve_pet_skill_petindex(self) -> tuple[int, str] | None:
        """返回 (Petindex, 显示名)。优先手输，否则下拉。"""
        raw = self.packet_pet_skill_petindex_var.get().strip()
        if raw:
            try:
                return int(raw), f"手输 Index={raw}"
            except ValueError:
                messagebox.showwarning("输入无效", "Petindex 必须是整数")
                return None
        label = self.packet_pet_skill_pet_var.get().strip()
        for lbl, idx, name in getattr(self, "_pet_skill_options", []):
            if lbl == label:
                return int(idx), name
        messagebox.showwarning(
            "未选宠物",
            "请在「给哪只宠物」下拉选择，或手输 Petindex。\n"
            "若列表为空：刷新桥接（需新版桥接上报宠物）。",
        )
        return None

    def on_learn_pet_skill_send(self) -> None:
        if self._packet_burst_active or getattr(self, "_use_item_seq_active", False) or getattr(
            self, "_special_pkg_active", False
        ):
            messagebox.showwarning("忙碌中", "请先停止其它连续发包任务")
            return

        char_label = self.packet_pet_skill_char_var.get().strip()
        uid = self._char_label_to_uid(char_label)
        if not uid:
            messagebox.showwarning("未选角色", "请先选择角色")
            return

        pet_resolved = self._resolve_pet_skill_petindex()
        if pet_resolved is None:
            return
        pet_index, pet_name = pet_resolved

        try:
            npc_obj = int(self.packet_pet_skill_npc_var.get().strip())
            skill_slot = int(self.packet_pet_skill_slot_var.get().strip())
            skill_id = int(self.packet_pet_skill_id_var.get().strip())
        except ValueError:
            messagebox.showwarning("输入无效", "NPC Objindex / 技能栏位 / 技能 ID 必须是整数")
            return
        if npc_obj < 0 or skill_slot < 0:
            messagebox.showwarning("输入无效", "Objindex / 技能栏位不能为负")
            return

        skill_name = self._pet_skill_id_name_map().get(skill_id, "未知")
        char_name = char_label.split(".", 1)[1].strip() if "." in char_label else char_label
        slot_text = self._skill_slot_label(skill_slot)

        confirm = messagebox.askyesno(
            "确认宠物学技能 — 请核对角色与宠物",
            "即将发包宠物学技能，请确认：\n\n"
            f"★ 角色：{char_name}\n"
            f"  UID：{uid}\n"
            f"★ 宠物：{pet_name}\n"
            f"  Petindex：{pet_index}\n\n"
            f"NPC Objindex：{npc_obj}\n"
            f"技能格子：{slot_text}\n"
            f"技能 ID：{skill_id}  {skill_name}\n\n"
            "点「是」继续；「否」取消。",
            icon="warning",
            default=messagebox.NO,
        )
        if not confirm:
            self._packet_log_line("[宠物学技能] 已取消（未确认）")
            return

        confirm2 = messagebox.askyesno(
            "再次确认 — 给谁的第几个格子",
            f"确定给「{char_name}」的宠物「{pet_name}」\n"
            f"的【{slot_text}】\n"
            f"学习【{skill_id} {skill_name}】？\n\n"
            "提醒：Skillindex 0 = 第1个格子。",
            icon="warning",
            default=messagebox.NO,
        )
        if not confirm2:
            self._packet_log_line("[宠物学技能] 已取消（二次确认）")
            return

        iid = self._require_instance()
        if not iid:
            return

        def send_fn(instance_id: str) -> str:
            return ipc.send_learn_pet_skill(
                instance_id,
                uid=uid,
                pet_index=pet_index,
                skill_slot=skill_slot,
                npc_objindex=npc_obj,
                skill_id=skill_id,
            )

        label = (
            f"learn_pet_skill char={char_name} pet={pet_name}#{pet_index} "
            f"slot={skill_slot} skill={skill_id} npc={npc_obj}"
        )
        self._packet_log_line(f"[宠物学技能] {label}")
        self._send_packet(label, send_fn, wait_ack=True)

    def on_open_skill_search(self, target: str = "char") -> None:
        if target == "pet":
            mapping = self._pet_skill_id_name_map()
            missing_hint = (
                "未找到技能表：tools/pet_skill_id_name_map.txt"
                "（也无 tools/skill_id_name_map.txt 可回退）"
            )
            title = "宠物技能查询（Buyindex 与人物共用 ID）"
            tip_text = "输入名称或 ID 过滤。宠物学习技能的 Buyindex 与人物同一套本体 ID。"
        else:
            mapping = self._skill_id_name_map()
            missing_hint = "未找到技能表：tools/skill_id_name_map.txt"
            title = "技能查询（人物）"
            tip_text = "输入名称或 ID 过滤，双击或点「选用」填入。"
        if not mapping:
            messagebox.showwarning("技能查询", missing_hint)
            return

        dlg = tk.Toplevel(self.root)
        dlg.title(title)
        dlg.geometry("420x480")
        dlg.transient(self.root)
        dlg.grab_set()

        tip = ttk.Label(dlg, text=tip_text)
        tip.pack(anchor=tk.W, padx=8, pady=(8, 4))

        q_var = tk.StringVar(value="")
        q_row = ttk.Frame(dlg)
        q_row.pack(fill=tk.X, padx=8, pady=(0, 6))
        ttk.Label(q_row, text="检索:").pack(side=tk.LEFT)
        q_entry = ttk.Entry(q_row, textvariable=q_var)
        q_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(4, 0))
        q_entry.focus_set()

        tree_frm = ttk.Frame(dlg)
        tree_frm.pack(fill=tk.BOTH, expand=True, padx=8)
        tree = ttk.Treeview(tree_frm, columns=("id", "name"), show="headings", height=16)
        tree.heading("id", text="ID")
        tree.heading("name", text="名称")
        tree.column("id", width=64, anchor=tk.CENTER)
        tree.column("name", width=280, anchor=tk.W)
        scroll = ttk.Scrollbar(tree_frm, orient=tk.VERTICAL, command=tree.yview)
        tree.configure(yscrollcommand=scroll.set)
        tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)

        items = sorted(mapping.items(), key=lambda kv: kv[0])

        def refill(_event=None) -> None:
            q = q_var.get().strip().lower()
            tree.delete(*tree.get_children())
            for sid, name in items:
                if q and q not in str(sid) and q not in name.lower():
                    continue
                tree.insert("", tk.END, iid=str(sid), values=(sid, name))

        def choose(_event=None) -> None:
            sel = tree.selection()
            if not sel:
                messagebox.showinfo("选用", "请先选中一个技能", parent=dlg)
                return
            sid = sel[0]
            if target == "pet":
                self.packet_pet_skill_id_var.set(sid)
                self._refresh_pet_skill_id_hint()
            else:
                self.packet_skill_id_var.set(sid)
                self._refresh_skill_id_hint()
            dlg.destroy()

        q_var.trace_add("write", lambda *_: refill())
        tree.bind("<Double-1>", choose)
        refill()

        btn_row = ttk.Frame(dlg)
        btn_row.pack(fill=tk.X, padx=8, pady=8)
        ttk.Button(btn_row, text="选用", command=choose, width=10).pack(side=tk.RIGHT)
        ttk.Button(btn_row, text="关闭", command=dlg.destroy, width=8).pack(side=tk.RIGHT, padx=(0, 6))

    def _nav_log_line(self, msg: str) -> None:
        if not hasattr(self, "nav_log"):
            return
        from datetime import datetime

        self.nav_log.insert(tk.END, f"{datetime.now():%H:%M:%S} {msg}\n")
        self.nav_log.see(tk.END)

    def _reload_waypoint_list(self) -> None:
        if not hasattr(self, "waypoint_tree"):
            return
        selected = self._selected_waypoint_id
        for item in self.waypoint_tree.get_children():
            self.waypoint_tree.delete(item)
        for wp in waypoints.list_waypoints():
            iid = str(wp.get("id") or "")
            self.waypoint_tree.insert(
                "",
                tk.END,
                iid=iid,
                values=(
                    wp.get("name", ""),
                    wp.get("floor", 0),
                    wp.get("x", 0),
                    wp.get("y", 0),
                    wp.get("map_id", 0),
                ),
            )
            if selected and iid == selected:
                self.waypoint_tree.selection_set(iid)

    def _parse_nav_target(self) -> tuple[int, int, int] | None:
        try:
            floor = int(self.nav_floor_var.get().strip())
            x = int(self.nav_x_var.get().strip())
            y = int(self.nav_y_var.get().strip())
        except ValueError:
            messagebox.showwarning("坐标无效", "请填写有效的地图号、X、Y（整数）")
            return None
        if floor <= 0:
            messagebox.showwarning("坐标无效", "地图号(floor) 必须大于 0")
            return None
        return floor, x, y

    def on_nav_fill_current(self) -> None:
        iid = self._target_instance_id()
        st = ipc.read_state(iid) if iid else None
        if not st:
            messagebox.showinfo("无数据", "请先连接游戏桥接")
            return
        nav_floor = int(st.get("pos_nav_floor") or st.get("pos_current_floor") or st.get("pos_floor") or 0)
        x = int(st.get("pos_x") or 0)
        y = int(st.get("pos_y") or 0)
        if nav_floor <= 0:
            messagebox.showinfo("无坐标", "当前未检测到有效地图号")
            return
        self.nav_floor_var.set(str(nav_floor))
        self.nav_x_var.set(str(x))
        self.nav_y_var.set(str(y))
        if not self.nav_name_var.get().strip():
            self.nav_name_var.set(f"地图{nav_floor}({x},{y})")

    def on_nav_save_waypoint(self) -> None:
        parsed = self._parse_nav_target()
        if not parsed:
            return
        floor, x, y = parsed
        name = self.nav_name_var.get().strip()
        iid = self._target_instance_id()
        st = ipc.read_state(iid) if iid else None
        map_id = int(st.get("pos_map_id") or 0) if st else 0
        item = waypoints.add_waypoint(name, floor=floor, x=x, y=y, map_id=map_id)
        self._selected_waypoint_id = str(item.get("id"))
        self._reload_waypoint_list()
        self._nav_log_line(f"[保存] {item.get('name')} floor={floor} ({x},{y})")

    def on_waypoint_select(self, _event=None) -> None:
        sel = self.waypoint_tree.selection()
        self._selected_waypoint_id = sel[0] if sel else None

    def on_nav_load_selected(self) -> None:
        parsed = self._get_selected_waypoint_coords()
        if not parsed:
            return
        floor, x, y, name = parsed
        self.nav_name_var.set(name)
        self.nav_floor_var.set(str(floor))
        self.nav_x_var.set(str(x))
        self.nav_y_var.set(str(y))

    def _get_selected_waypoint_coords(self) -> tuple[int, int, int, str] | None:
        sel = self.waypoint_tree.selection()
        if not sel:
            messagebox.showinfo("未选择", "请先选中一个坐标点")
            return None
        vals = self.waypoint_tree.item(sel[0], "values")
        if len(vals) < 4:
            messagebox.showwarning("坐标无效", "选中坐标点数据异常")
            return None
        name = str(vals[0])
        try:
            floor = int(vals[1])
            x = int(vals[2])
            y = int(vals[3])
        except ValueError:
            messagebox.showwarning("坐标无效", "选中坐标点数据异常")
            return None
        if floor <= 0:
            messagebox.showwarning("坐标无效", "地图号无效")
            return None
        return floor, x, y, name

    def on_nav_waypoint_general(self) -> None:
        parsed = self._get_selected_waypoint_coords()
        if not parsed:
            return
        floor, x, y, name = parsed
        self._nav_log_line(f"[导航] → {name}  floor={floor} ({x},{y})")
        self._send("nav_general", lambda i: ipc.nav_general(i, floor, x, y))

    def on_nav_delete_waypoint(self) -> None:
        sel = self.waypoint_tree.selection()
        if not sel:
            messagebox.showinfo("未选择", "请先选中要删除的坐标点")
            return
        wp_id = sel[0]
        vals = self.waypoint_tree.item(wp_id, "values")
        if waypoints.delete_waypoint(wp_id):
            self._selected_waypoint_id = None
            self._reload_waypoint_list()
            self._nav_log_line(f"[删除] {vals[0] if vals else wp_id}")
        else:
            messagebox.showwarning("删除失败", "找不到该坐标点")

    def on_nav_to_selected(self) -> None:
        self.on_nav_waypoint_general()

    def on_nav_general(self) -> None:
        parsed = self._parse_nav_target()
        if not parsed:
            return
        floor, x, y = parsed
        self._send("nav_general", lambda i: ipc.nav_general(i, floor, x, y))

    def on_nav_walk_map(self) -> None:
        parsed = self._parse_nav_target()
        if not parsed:
            return
        floor, x, y = parsed
        self._send("nav_walk_map", lambda i: ipc.nav_walk_map(i, floor, x, y))

    def on_nav_walk_same(self) -> None:
        try:
            x = int(self.nav_x_var.get().strip())
            y = int(self.nav_y_var.get().strip())
        except ValueError:
            messagebox.showwarning("坐标无效", "请填写有效的 X、Y")
            return
        self._send("nav_walk_same", lambda i: ipc.nav_walk_same(i, x, y))

    def on_nav_task(self) -> None:
        parsed = self._parse_nav_target()
        if not parsed:
            return
        floor, x, y = parsed
        self._send("nav_task", lambda i: ipc.nav_task(i, floor, x, y))

    def on_nav_stop(self) -> None:
        self._send("nav_stop", ipc.nav_stop)

    def _run_async(self, label: str, fn) -> None:
        def worker() -> None:
            try:
                self.status_var.set(f"执行中: {label}")
                fn()
                self.status_var.set("完成")
            except Exception as exc:
                self.status_var.set("失败")
                self.root.after(0, lambda: messagebox.showerror("失败", str(exc)))

        threading.Thread(target=worker, daemon=True).start()

    def _send(self, label: str, send_fn, wait: bool = True) -> None:
        if self._auto_running:
            return
        iid = self._require_instance()
        if not iid:
            return
        if not ipc.bridge_alive(iid):
            if not messagebox.askyesno(
                "桥接未就绪",
                "未检测到游戏内助手桥接。\n\n"
                "请确认：\n"
                "1. 多开器已注入桥接\n"
                "2. 实例 ID 与游戏进程一致（inst_{PID}）\n\n"
                "仍要发送命令？",
            ):
                return

        def do() -> None:
            req = send_fn(iid)
            self.root.after(0, lambda: self.log_line(f"[CMD] {label} id={req}"))
            if wait:
                ok, ack = ipc.wait_ack(iid, req, timeout=45)
                self.root.after(0, lambda: self.log_line(f"[ACK] {label} ok={ok} {ack or ''}"))
            self.root.after(0, self.refresh_bridge)

        self._run_async(label, do)

    def on_login(self) -> None:
        phone, pwd = self.phone_var.get().strip(), self.password_var.get()
        if not phone or not pwd:
            messagebox.showwarning("输入不完整", "请填写手机号和密码")
            return
        self._send("login", lambda i: ipc.login(i, phone, pwd))

    def on_enter_game(self) -> None:
        self._send("enter_game", ipc.enter_game)

    def on_multi_login(self) -> None:
        self._send("multi_login_offline_all", ipc.multi_login_offline_all)

    def on_click_multi_head(self, index: int) -> None:
        self._send(f"click_multi_head[{index}]", lambda i: ipc.click_multi_head(i, index))

    def on_select_multi_char(self, index: int) -> None:
        self._send(f"select_multi_char[{index}]", lambda i: ipc.select_multi_char(i, index))

    def on_summon(self) -> None:
        self._send("one_key_summon", ipc.one_key_summon)

    def on_fetch_resource(self) -> None:
        self._send("fetch_resource_status", ipc.fetch_resource_status)

    def on_workflow_step1(self) -> None:
        phone, pwd = self.phone_var.get().strip(), self.password_var.get()
        if not phone or not pwd:
            messagebox.showwarning("输入不完整", "一键流程需要账号密码")
            return
        self._send(
            "workflow_step1",
            lambda i: ipc.workflow_step1_five_chars(i, phone, pwd),
            wait=True,
        )


def main() -> int:
    iid = sys.argv[1].strip() if len(sys.argv) > 1 else None
    phone = sys.argv[2].strip() if len(sys.argv) > 2 else None
    password = sys.argv[3] if len(sys.argv) > 3 else None
    auto = False
    if len(sys.argv) > 4:
        auto = sys.argv[4].strip().lower() in ("1", "true", "auto", "yes")
    app = SeqChapterAssistantApp(
        instance_id=iid,
        phone=phone,
        password=password,
        auto_workflow=auto,
    )
    app.root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
