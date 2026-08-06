#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 热补丁 GUI。"""
from __future__ import annotations

import os
import subprocess
import sys
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

from apply_combo_patch import (
    DEFAULT_GIFT_CODES,
    apply_combo,
    get_status,
    restore_hotfix,
)
from patch_common import (
    CUSTOMER_GM_LABELS,
    DATA_DIR,
    adopt_client_hotfix_update,
    bridge_variant_label,
    detect_hotfix_drift,
    ensure_orig_backup,
    format_client_update_hint,
    format_hotfix_drift_hint,
    format_size_status,
    get_game_root,
    get_update_status,
    has_valid_orig_backup,
    hotfix_orig,
    hotfix_path,
    initialize_hotfix_workspace,
    is_bridge_patched,
    mark_hotfix_watch_stamp,
    save_baseline_meta,
    set_game_root,
    sha256_file,
    updated_hotfix_candidate,
    effective_expected_size,
    _safe_copy2,
)
from patch_slack import (
    assert_combo_slack_ok,
    format_slack_summary,
    slack_report,
)


class ComboPatchApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        self.root.title("魔力宝贝：序章 — 热补丁")
        self.root.geometry("620x640")
        self.root.minsize(560, 560)
        self.action_buttons: list[tk.Widget] = []

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        # 底部按钮栏先 pack，保证始终可见（去掉说明区后窗口变矮时不会被选项挤没）
        btn_row = ttk.Frame(outer)
        btn_row.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
        self.init_btn = tk.Button(
            btn_row,
            text="初始化",
            command=self.on_initialize,
            width=14,
            fg="red",
            activeforeground="red",
        )
        self.init_btn.pack(side=tk.LEFT)
        self.backup_btn = ttk.Button(
            btn_row, text="制作备份", command=self.on_create_backup, width=10
        )
        self.backup_btn.pack(side=tk.LEFT, padx=(10, 0))
        self.restore_btn = ttk.Button(
            btn_row, text="恢复备份", command=self.on_restore_backup, width=10
        )
        self.restore_btn.pack(side=tk.LEFT, padx=(6, 0))
        self._add_action_button(
            btn_row,
            ttk.Button(btn_row, text="应用补丁", command=self.on_apply, width=12),
        ).pack(side=tk.LEFT, padx=(10, 0))
        self._add_action_button(
            btn_row,
            ttk.Button(btn_row, text="测算余量", command=self.on_slack_check, width=10),
        ).pack(side=tk.LEFT, padx=(6, 0))
        self._add_action_button(
            btn_row,
            ttk.Button(btn_row, text="启动游戏", command=self.on_launch_game, width=10),
        ).pack(side=tk.LEFT, padx=(6, 0))

        self.status_var = tk.StringVar()
        ttk.Label(outer, textvariable=self.status_var, font=("Microsoft YaHei UI", 9)).pack(
            side=tk.BOTTOM, anchor=tk.W, fill=tk.X, pady=(0, 4)
        )
        self.update_hint_var = tk.StringVar()
        ttk.Label(
            outer,
            textvariable=self.update_hint_var,
            foreground="#c0392b",
            wraplength=520,
            font=("Microsoft YaHei UI", 9),
        ).pack(side=tk.BOTTOM, anchor=tk.W, fill=tk.X, pady=(0, 2))

        body = ttk.Frame(outer)
        body.pack(side=tk.TOP, fill=tk.BOTH, expand=True)

        ttk.Label(body, text="魔力宝贝：序章 客户端热补丁", font=("Microsoft YaHei UI", 11, "bold")).pack(
            anchor=tk.W
        )
        ttk.Label(
            body,
            text="关闭游戏 → 选目录 → 初始化 → 应用补丁。初始化会自动完成所有准备工作（可重复点）。",
            wraplength=520,
            foreground="#555555",
        ).pack(anchor=tk.W, pady=(0, 6))
        ttk.Label(
            body,
            text=f"目标：{DATA_DIR}/assets/hotfixdata/hotfix.dll.bytes",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 10))

        path_frm = ttk.LabelFrame(body, text=f"游戏目录（含 {DATA_DIR} 文件夹）", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 8))
        self.path_var = tk.StringVar()
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.path_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="浏览…", command=self.pick_game_dir, width=8).pack(side=tk.LEFT, padx=(6, 0))

        self.vip_var = tk.BooleanVar(value=False)  # 默认不打战斗倍速（加速默认关）
        self.vip_non_vip_var = tk.BooleanVar(value=False)
        self.vip_scale_var = tk.StringVar(value="5")
        self._patch_toggle_guard = False
        self.battle_nine_action_var = tk.BooleanVar(value=False)
        self.battle_nine_external_var = tk.BooleanVar(value=False)
        self.auto_seal_external_var = tk.BooleanVar(value=True)
        self.auto_catch_external_var = tk.BooleanVar(value=True)
        self.auto_catch_nopet_external_var = tk.BooleanVar(value=True)
        self.auto_catch_sell_external_var = tk.BooleanVar(value=True)
        self.count_farm_var = tk.BooleanVar(value=True)
        self.area_extract_var = tk.BooleanVar(value=True)
        self.auto_point_var = tk.BooleanVar(value=True)
        self.lv1_auto_external_var = tk.BooleanVar(value=False)
        self.auto_sell_external_var = tk.BooleanVar(value=False)
        self.plugin_host_var = tk.BooleanVar(value=False)
        self.inject_bridge_var = tk.BooleanVar(value=False)
        self.customer_gm_var = tk.BooleanVar(value=True)
        self.customer_gm_mode_var = tk.StringVar(value="autoskill")
        self.map_sprint_var = tk.BooleanVar(value=False)  # 地图跑速默认关（加速类）
        self.map_sprint_scale_var = tk.StringVar(value="8")
        self.battle_longpress_var = tk.BooleanVar(value=True)
        self.level_one_include_all_var = tk.BooleanVar(value=True)
        self.transition_speed_var = tk.BooleanVar(value=False)
        self.transition_speed_scale_var = tk.StringVar(value="0.4")
        self.skill_effect_speed_var = tk.BooleanVar(value=False)  # 技能特效加速默认关（加速类）
        self.skill_effect_scale_var = tk.StringVar(value="2")
        self.daily_claim_var = tk.BooleanVar(value=True)
        self.newbie_gift_code_var = tk.BooleanVar(value=True)
        self.boss_key_fps_var = tk.BooleanVar(value=True)
        self.wiki_fps_var = tk.BooleanVar(value=False)
        self.wiki_test_ui_var = tk.BooleanVar(value=True)
        self.battle_appear_var = tk.BooleanVar(value=False)

        notebook = ttk.Notebook(body)
        notebook.pack(fill=tk.BOTH, expand=True, pady=(0, 8))

        tab_common = ttk.Frame(notebook, padding=8)
        tab_battle = ttk.Frame(notebook, padding=8)
        notebook.add(tab_common, text="常用")
        notebook.add(tab_battle, text="战斗扩展")

        # --- 常用 ---
        ttk.Checkbutton(tab_common, text="VIP 战斗倍速（默认关：3x/5x/10x + 打飞×4N；开启会连带掐断倍速检测上报）", variable=self.vip_var).pack(
            anchor=tk.W
        )
        vip_row = ttk.Frame(tab_common)
        vip_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        ttk.Label(vip_row, text="倍速:").pack(side=tk.LEFT)
        for scale in ("3", "5", "10"):
            ttk.Radiobutton(
                vip_row,
                text=f"{scale}x",
                variable=self.vip_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Checkbutton(
            tab_common,
            text="非VIP同样倍速",
            variable=self.vip_non_vip_var,
        ).pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))

        ttk.Checkbutton(
            tab_common,
            text="侧栏客服→高级自动战斗（默认开；官方入口太深）",
            variable=self.customer_gm_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        for row_modes in (
            (
                ("autoskill", "高级自动战斗"),
                ("blindbox", "盲盒3028"),
                ("lottery", "幸运秘宝3049"),
                ("challengeboss", "讨伐令3045"),
                ("bravetrial", "试炼3047"),
                ("crystal", "水晶阁"),
            ),
        ):
            gm_row = ttk.Frame(tab_common)
            gm_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
            for mode, text in row_modes:
                ttk.Radiobutton(
                    gm_row,
                    text=text,
                    variable=self.customer_gm_mode_var,
                    value=mode,
                ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Checkbutton(tab_common, text="Sprint 跑速", variable=self.map_sprint_var).pack(
            anchor=tk.W, pady=(8, 0)
        )
        sprint_row = ttk.Frame(tab_common)
        sprint_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        for scale, text in (("8", "快"), ("10", "很快"), ("12", "飞快")):
            ttk.Radiobutton(
                sprint_row,
                text=text,
                variable=self.map_sprint_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Checkbutton(
            tab_common,
            text="战斗内长按单位显示详情（解除 PVE 模式限制）",
            variable=self.battle_longpress_var,
        ).pack(anchor=tk.W, pady=(8, 0))

        ttk.Checkbutton(
            tab_common,
            text="加速过场（进出战斗十字格；不影响协议 8 退场回执）",
            variable=self.transition_speed_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        transition_row = ttk.Frame(tab_common)
        transition_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        for scale, text in (("0.4", "快"), ("0.2", "很快"), ("0.1", "飞快")):
            ttk.Radiobutton(
                transition_row,
                text=text,
                variable=self.transition_speed_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Checkbutton(
            tab_common,
            text="战斗技能特效加速（火球/爆炸等帧动画，不影响回合读秒）",
            variable=self.skill_effect_speed_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        effect_row = ttk.Frame(tab_common)
        effect_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        ttk.Label(effect_row, text="倍速:").pack(side=tk.LEFT)
        for scale, text in (("1.5", "1.5x"), ("2", "2x"), ("3", "3x"), ("5", "5x")):
            ttk.Radiobutton(
                effect_row,
                text=text,
                variable=self.skill_effect_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Checkbutton(
            tab_common,
            text="分享改日常（默认开；侧栏「分享」切页→日常领取；不占百科）",
            variable=self.daily_claim_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            tab_common,
            text="新手礼包码领取（默认开；与日常同分享切页；最多5角色）",
            variable=self.newbie_gift_code_var,
        ).pack(anchor=tk.W, pady=(4, 0))
        ttk.Label(
            tab_common,
            text="礼包码（一行一个，可改；打补丁时写入游戏 hotfixdata）",
            foreground="#555555",
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        self.gift_codes_box = scrolledtext.ScrolledText(tab_common, height=5, width=40, wrap=tk.WORD)
        self.gift_codes_box.pack(anchor=tk.W, padx=(18, 0), pady=(2, 0), fill=tk.X)
        self.gift_codes_box.insert("1.0", "\n".join(DEFAULT_GIFT_CODES))

        ttk.Checkbutton(
            tab_common,
            text="切后台/老板键限帧（默认开；失焦或隐藏时 30FPS，恢复时还原）",
            variable=self.boss_key_fps_var,
        ).pack(anchor=tk.W, pady=(8, 0))

        ttk.Checkbutton(
            tab_common,
            text="百科限帧（默认关；侧栏「百科」切换 10FPS；与助手面板/抓宠/烧卡互斥）",
            variable=self.wiki_fps_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            tab_common,
            text="百科→助手面板（默认开；抓宠/烧卡等在面板内切换；与限帧互斥）",
            variable=self.wiki_test_ui_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            tab_common,
            text="进战形象钩子（人物/光环/坐骑/宠形象/满档；百科→形象；按Uid存档）",
            variable=self.battle_appear_var,
        ).pack(anchor=tk.W, pady=(8, 0))

        # --- 战斗扩展 ---
        ttk.Label(
            tab_battle,
            text="面板模式（助手面板默认开）：抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡/计数挂机 可同打，面板内互斥切换。九动·DLL/遇1级/盗贼/插件Host/桥接仍互斥。",
            wraplength=520,
            foreground="#555555",
        ).pack(anchor=tk.W, pady=(0, 8))

        ttk.Checkbutton(
            tab_battle,
            text="神奇九动·IL原版（需足够 .text 间隙；当前余量紧一般不勾）",
            variable=self.battle_nine_action_var,
            command=lambda: self._on_nine_il_toggle(),
        ).pack(anchor=tk.W)
        ttk.Checkbutton(
            tab_battle,
            text="神奇九动·DLL版（已停发；默认关）",
            variable=self.battle_nine_external_var,
            command=lambda: self._on_battle_exclusive_toggle("nine_dll"),
        ).pack(anchor=tk.W, pady=(4, 0))
        ttk.Checkbutton(
            tab_battle,
            text="自动烧卡·DLL版（默认；有封印卡就扔，面板切开关）",
            variable=self.auto_seal_external_var,
            command=lambda: self._on_battle_exclusive_toggle("seal"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认开启。助手面板内切换；仅队长本机回合烧队长背包；队员不烧。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="自动抓宠·DLL版（默认；自动封印：一级非迷你蝙蝠，面板切开关）",
            variable=self.auto_catch_external_var,
            command=lambda: self._on_battle_exclusive_toggle("catch"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认开启。助手面板内切换。有一级：P1 扔卡 · P2 一号技能 · 其余人物 G · 宠物固定防御 W|0（SkillId74）。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="自动抓宠·无宠二动（默认；不带宠时第二动防御；面板切开关）",
            variable=self.auto_catch_nopet_external_var,
            command=lambda: self._on_battle_exclusive_toggle("catch_nopet"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Checkbutton(
            tab_battle,
            text="抓宠卖银币·DLL版（默认；掉档≥Y且无@→回收，面板切开关）",
            variable=self.auto_catch_sell_external_var,
            command=lambda: self._on_battle_exclusive_toggle("catch_sell"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认开启。抓宠卖银币：名字已#跳过；掉档≥Y且无@→回收；其余改名后满仓存仓。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="计数挂机（默认；面板战斗页互斥切换；标题 ★挂机中★ 已战斗X次）",
            variable=self.count_farm_var,
            command=lambda: self._on_battle_exclusive_toggle("count_farm"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认开启。仅计数+标题：每次进战斗 +1，标题「★挂机中★ 已战斗N次」；关闭清零恢复。不拦截战斗，可与抓宠/烧卡同开。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="采集自动提取（默认；面板战斗页独立开关，与战斗模式共存）",
            variable=self.area_extract_var,
            command=lambda: self._on_battle_exclusive_toggle("area_extract"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认开启。不需要自动采集：已采集物共5格，单格满999时自动提取该格到账号银行（每格最多重试1次）。采集数据推送时即检+每10分钟兜底。可在助手面板战斗页开/关，脚本页「立刻提取采集物」可手动触发一次，标题合并显示「★自动提取★X格已满」。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="一键加点（默认部署；面板脚本页按钮，人物按推荐第一方案+宠物先加力量）",
            variable=self.auto_point_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认部署。在助手面板「脚本」页点「一键加点」：所有角色按职业推荐第一套方案加点（血/攻/强/速/魔按方案权重分配，可加点数有剩余时按权重继续分）；所有宠物优先加力量，加到爆点极限（单属性 BP 不超过总 BP 一半）即止，溢出点不再分配。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="遇1级自动·DLL版（P1封印/P2技能1/其余防御；点百科 Tip 开关）",
            variable=self.lv1_auto_external_var,
            command=lambda: self._on_battle_exclusive_toggle("lv1"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认关闭。仅 LevelOneFlag；无1级走原自动；开启时忽略「遇1级停」。与九动DLL/烧卡/抓宠/桥接互斥。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="盗贼辅助·DLL版（远程出售魔石；点百科 Tip 开关；不进傻瓜补丁）",
            variable=self.auto_sell_external_var,
            command=lambda: self._on_battle_exclusive_toggle("sell"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="默认关闭。点百科 Tip「盗贼辅助已开启/关闭」。开启后标题「★盗贼辅助★N次战斗后出售」；每 10 次退战给全部角色远程出售魔石（需月卡）。可与 IL 九动同打。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="插件 Host·实验（百科打开自绘面板；一期仅骨架，功能二期接入）",
            variable=self.plugin_host_var,
            command=lambda: self._on_battle_exclusive_toggle("host"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="点侧栏百科打开最高层级面板；烧卡/抓宠/盗贼在面板内互斥勾选（一期为占位）。与其它扩展 DLL 暂互斥。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))
        ttk.Checkbutton(
            tab_battle,
            text="注入桥接·DLL版（序章助手连接所需）",
            variable=self.inject_bridge_var,
            command=lambda: self._on_battle_exclusive_toggle("bridge"),
        ).pack(anchor=tk.W, pady=(8, 0))
        ttk.Label(
            tab_battle,
            text="与九动DLL/烧卡/抓宠/盗贼辅助互斥（共用 OnApplicationPause 加载器）。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))

        ttk.Checkbutton(
            tab_battle,
            text="遇敌一级停止也含哥布林/迷你蝙蝠（取消原版排除）",
            variable=self.level_one_include_all_var,
        ).pack(anchor=tk.W, pady=(12, 0))
        ttk.Label(
            tab_battle,
            text="原版会排除 AnimationId 101800/101242；补丁将比较常量改为无效 ID，体积不变。",
            wraplength=500,
            foreground="#666666",
            font=("Microsoft YaHei UI", 8),
        ).pack(anchor=tk.W, padx=(18, 0), pady=(2, 0))

        self.load_saved_path()
        self.refresh_status()

    def _add_action_button(self, _parent: ttk.Frame, button: tk.Widget) -> tk.Widget:
        self.action_buttons.append(button)
        return button

    def _set_actions_enabled(self, enabled: bool) -> None:
        for button in self.action_buttons:
            if isinstance(button, ttk.Button):
                button.state(["!disabled"] if enabled else ["disabled"])
            else:
                button.configure(state=tk.NORMAL if enabled else tk.DISABLED)

    def _on_nine_il_toggle(self) -> None:
        """IL 九动仅与九动 DLL 互斥。"""
        if self._patch_toggle_guard:
            return
        if not self.battle_nine_action_var.get():
            return
        self._patch_toggle_guard = True
        try:
            self.battle_nine_external_var.set(False)
        finally:
            self._patch_toggle_guard = False

    def _on_battle_exclusive_toggle(self, which: str) -> None:
        """DLL 互斥：九动DLL / 遇1级 / 盗贼辅助 / 插件Host / 桥接。勾九动DLL 时顺带关掉 IL 九动。

        面板模式下 抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡 可共存（面板内互斥切换），
        勾它们时不取消其它 DLL。
        """
        if self._patch_toggle_guard:
            return
        var_map = {
            "nine_dll": self.battle_nine_external_var,
            "seal": self.auto_seal_external_var,
            "catch": self.auto_catch_external_var,
            "catch_nopet": self.auto_catch_nopet_external_var,
            "catch_sell": self.auto_catch_sell_external_var,
            "count_farm": self.count_farm_var,
            "area_extract": self.area_extract_var,
            "auto_point": self.auto_point_var,
            "lv1": self.lv1_auto_external_var,
            "sell": self.auto_sell_external_var,
            "host": self.plugin_host_var,
            "bridge": self.inject_bridge_var,
        }
        exclusive_keys = ("nine_dll", "lv1", "sell", "host", "bridge")
        active = var_map.get(which)
        if active is None or not active.get():
            return
        self._patch_toggle_guard = True
        try:
            for key in exclusive_keys:
                if key != which:
                    var_map[key].set(False)
            if which == "nine_dll":
                self.battle_nine_action_var.set(False)
        finally:
            self._patch_toggle_guard = False

    def load_saved_path(self) -> None:
        root = get_game_root()
        if root:
            self.path_var.set(str(root))

    def pick_game_dir(self) -> None:
        chosen = filedialog.askdirectory(title=f"选择游戏根目录（含 {DATA_DIR}）")
        if not chosen:
            return
        path = Path(chosen)
        if not (path / DATA_DIR).is_dir():
            messagebox.showerror("目录无效", f"所选文件夹下没有 {DATA_DIR} 子目录")
            return
        set_game_root(path)
        self.path_var.set(str(path))
        self.refresh_status()

    def _resolve_root(self) -> Path:
        text = self.path_var.get().strip()
        if text:
            path = Path(text)
            if (path / DATA_DIR).is_dir():
                set_game_root(path)
                return path
        root = get_game_root()
        if root and (root / DATA_DIR).is_dir():
            return root
        raise FileNotFoundError("请先选择有效的游戏目录")

    def refresh_status(self) -> None:
        ready = False
        drifted = False
        try:
            root = self._resolve_root()
            st = get_status(root)
            upd = get_update_status(root)
            drift = detect_hotfix_drift(root)
            if drift.get("reason") == "own_patch_unmarked":
                try:
                    mark_hotfix_watch_stamp(root, marked_by="resync_own_patch")
                    drift = detect_hotfix_drift(root)
                except Exception:
                    pass
            drifted = bool(drift.get("drifted"))
            ready = bool(upd.get("ready"))
            expected = upd.get("expected_size") or effective_expected_size(root)
            init_ok = "已初始化" if ready else "待初始化"
            parts = [
                f"游戏: {root.name}",
                init_ok,
                f"hotfix: {format_size_status(st.get('size'), st.get('orig_size'), expected=expected)}",
                f"客服: {CUSTOMER_GM_LABELS.get(st.get('customer_gm_mode', 'unknown'), st.get('customer_gm_mode', ''))}",
            ]
            if drifted:
                parts.append("疑似客户端更新")
            if st.get("last_combo"):
                lc = st["last_combo"]
                parts.append(f"上次: VIP {lc.get('vip_scale', '?')}x")
            if st.get("bridge_patched"):
                bv = st.get("bridge_variant", "")
                parts.append(f"桥接: 已注入 ({bridge_variant_label(bv)})")
            elif is_bridge_patched(root):
                parts.append("桥接: 已注入")
            else:
                parts.append("桥接: 未注入")
            self.status_var.set(" | ".join(parts))
            drift_hint = format_hotfix_drift_hint(drift)
            hint = drift_hint or format_client_update_hint(upd)
            self.update_hint_var.set(f"⚠ {hint}" if hint else "")
        except Exception as exc:
            self.status_var.set(f"状态: {exc}")
            self.update_hint_var.set("")
        # 漂移时仍允许点「应用补丁」，以便弹出自动修复确认
        self._set_actions_enabled(ready or drifted)
        self._update_backup_buttons()

    def _update_backup_buttons(self) -> None:
        """制作备份 / 恢复备份：不依赖「已初始化」，无 .orig 时恢复不可点。"""
        can_backup = False
        can_restore = False
        try:
            root = self._resolve_root()
            can_backup = hotfix_path(root).is_file()
            can_restore = hotfix_orig(root).is_file()
        except Exception:
            pass
        self.backup_btn.state(["!disabled"] if can_backup else ["disabled"])
        self.restore_btn.state(["!disabled"] if can_restore else ["disabled"])

    def on_create_backup(self) -> None:
        try:
            root = self._resolve_root()
            hf = hotfix_path(root)
            if not hf.is_file():
                messagebox.showerror("制作备份", f"找不到 hotfix：\n{hf}")
                return
            expected = effective_expected_size(root)
            size = hf.stat().st_size
            warn = ""
            if size != expected:
                warn = (
                    f"\n\n注意：当前 hotfix 体积 {size:,} 与工具期望 {expected:,} 不一致，"
                    "仍可备份，但可能无法用于打补丁。"
                )
            orig = hotfix_orig(root)
            overwrite = "将覆盖已有 .orig 备份。\n" if orig.is_file() else ""
            if not messagebox.askyesno(
                "制作备份",
                f"把当前游戏 hotfix 备份为 hotfix.dll.bytes.orig？\n"
                f"{overwrite}"
                f"请确认已是干净原版（未打补丁），且游戏已关闭。"
                f"{warn}",
            ):
                return
            ensure_orig_backup(root, source=hf, expected=size if size != expected else None)
            # 同步 neworig，便于后续初始化 / 傻瓜补丁使用同一底稿
            neworig = updated_hotfix_candidate(root)
            neworig.parent.mkdir(parents=True, exist_ok=True)
            _safe_copy2(hf, neworig)
            digest = sha256_file(neworig)
            save_baseline_meta(
                root,
                {
                    "expected_size": size,
                    "neworig_sha256": digest,
                    "source": "manual_backup",
                    "notes": "GUI「制作备份」写入",
                },
            )
            messagebox.showinfo("制作备份", f"已备份：\n{orig}")
            self.refresh_status()
        except Exception as exc:
            messagebox.showerror("制作备份失败", str(exc).strip() or "未知错误")

    def on_restore_backup(self) -> None:
        try:
            root = self._resolve_root()
            orig = hotfix_orig(root)
            if not orig.is_file():
                messagebox.showerror("恢复备份", "还没有 .orig 备份，请先「制作备份」。")
                self._update_backup_buttons()
                return
            if not messagebox.askyesno(
                "恢复备份",
                "用 .orig 覆盖当前 hotfix.dll.bytes？\n"
                "已打的补丁会丢掉。请先关闭游戏。",
            ):
                return
            restore_hotfix(root)
            messagebox.showinfo("恢复备份", "已从 .orig 恢复 hotfix。")
            self.refresh_status()
        except Exception as exc:
            messagebox.showerror("恢复备份失败", str(exc).strip() or "未知错误")

    def _confirm_fix_client_update_if_needed(self, root: Path) -> bool:
        """若 hotfix 与标记不一致，询问是否自动采新底稿。返回 False 表示用户取消。"""
        drift = detect_hotfix_drift(root)
        if drift.get("reason") == "own_patch_unmarked":
            try:
                mark_hotfix_watch_stamp(root, marked_by="resync_own_patch")
            except Exception:
                pass
            drift = detect_hotfix_drift(root)
        if not drift.get("drifted"):
            return True
        detail = drift.get("detail") or "指纹不一致"
        dirty_note = ""
        if drift.get("reason") == "content_changed_dirty":
            dirty_note = (
                "\n\n注意：当前 hotfix 看起来不是干净原版。"
                "若自动修复失败，请删除本客户端，复制干净客户端后再打补丁。"
            )
        if not messagebox.askyesno(
            "检测到游戏有更新",
            "检查到游戏有更新，是否自动修复？\n\n"
            "将采用当前游戏内干净 hotfix 重建底稿（neworig / .orig），\n"
            "不会用旧备份覆盖新文件。\n\n"
            f"详情：{detail}"
            f"{dirty_note}",
        ):
            return False
        try:
            msgs = adopt_client_hotfix_update(root)
            messagebox.showinfo(
                "已自动修复",
                "\n".join(msgs) if msgs else "底稿已按新版 hotfix 重建。",
            )
            self.refresh_status()
            return True
        except Exception as exc:
            messagebox.showerror("自动修复失败", str(exc).strip() or "未知错误")
            return False

    def on_initialize(self) -> None:
        self._run_initialize(confirm=True)

    def _run_initialize(self, *, confirm: bool) -> bool:
        try:
            root = self._resolve_root()
            if not self._confirm_fix_client_update_if_needed(root):
                return False
            if confirm and not messagebox.askyesno(
                "初始化",
                "将自动完成以下步骤（可重复执行）：\n\n"
                "1. 编译补丁引擎\n"
                "2. 采用游戏内干净 hotfix（优先识别客户端更新）写入底稿\n"
                "3. 用新底稿重建 hotfix.dll.bytes 与 .orig\n"
                "4. 清除上次补丁状态（需再点「应用补丁」）\n"
                "5. 标记当前 hotfix 指纹\n\n"
                "请先关闭 cg37.exe。\n\n"
                "继续？",
            ):
                return False
            initialize_hotfix_workspace(root, force=True)
            if confirm:
                messagebox.showinfo("初始化", "成功")
            self.refresh_status()
            return True
        except Exception as exc:
            messagebox.showerror("初始化失败", str(exc).strip() or "未知错误")
            return False

    def on_slack_check(self) -> None:
        try:
            root = self._resolve_root()
            data, warnings = assert_combo_slack_ok(
                game_root=root,
                vip=self.vip_var.get(),
                vip_non_vip=self.vip_non_vip_var.get(),
                battle_nine_action=self.battle_nine_action_var.get(),
                battle_nine_external=self.battle_nine_external_var.get(),
                auto_seal_external=self.auto_seal_external_var.get(),
                auto_catch_external=(
                    self.auto_catch_external_var.get() or self.auto_catch_sell_external_var.get()
                ),
                auto_catch_nopet_external=self.auto_catch_nopet_external_var.get(),
                lv1_auto_external=self.lv1_auto_external_var.get(),
                auto_sell_external=self.auto_sell_external_var.get(),
                customer_gm=self.customer_gm_var.get(),
                map_sprint=self.map_sprint_var.get(),
                battle_longpress=self.battle_longpress_var.get(),
                level_one_include_all=self.level_one_include_all_var.get(),
                transition_speed=self.transition_speed_var.get(),
                skill_effect_speed=self.skill_effect_speed_var.get(),
                inject_bridge=self.inject_bridge_var.get(),
            )
            if not data:
                data = slack_report(game_root=root, prefer_orig=True)
            text = format_slack_summary(data)
            if warnings:
                text += "\n\n" + "\n".join(warnings)
            messagebox.showinfo("余量测算", text)
        except Exception as exc:
            messagebox.showerror("测算失败", str(exc))

    def on_launch_game(self) -> None:
        try:
            root = self._resolve_root()
            exe = root / "cg37.exe"
            if not exe.is_file():
                messagebox.showerror("找不到游戏", f"目录下没有 cg37.exe：\n{root}")
                return
            if sys.platform == "win32":
                os.startfile(str(exe))  # type: ignore[attr-defined]
            else:
                subprocess.Popen([str(exe)], cwd=str(root))
        except Exception as exc:
            messagebox.showerror("启动失败", str(exc))

    def on_apply(self) -> None:
        try:
            root = self._resolve_root()
            if not self._confirm_fix_client_update_if_needed(root):
                return
            if not has_valid_orig_backup(root):
                if messagebox.askyesno(
                    "需要初始化",
                    "尚未初始化（缺少有效的 .orig 备份）。\n\n是否现在自动初始化？",
                ):
                    if not self._run_initialize(confirm=False):
                        return
                else:
                    return
            if not (
                self.vip_var.get()
                or self.vip_non_vip_var.get()
                or self.battle_nine_action_var.get()
                or self.battle_nine_external_var.get()
                or self.auto_seal_external_var.get()
                or self.auto_catch_external_var.get()
                or self.auto_catch_nopet_external_var.get()
                or self.auto_catch_sell_external_var.get()
                or self.lv1_auto_external_var.get()
                or self.auto_sell_external_var.get()
                or self.plugin_host_var.get()
                or self.customer_gm_var.get()
                or self.map_sprint_var.get()
                or self.battle_longpress_var.get()
                or self.level_one_include_all_var.get()
                or self.transition_speed_var.get()
                or self.skill_effect_speed_var.get()
                or self.daily_claim_var.get()
                or self.newbie_gift_code_var.get()
                or self.boss_key_fps_var.get()
                or self.wiki_fps_var.get()
                or self.wiki_test_ui_var.get()
                or self.battle_appear_var.get()
                or self.inject_bridge_var.get()
            ):
                messagebox.showwarning("未选择", "请至少勾选一项补丁")
                return
            dll_exclusive = [
                self.battle_nine_external_var.get(),
                self.lv1_auto_external_var.get(),
                self.auto_sell_external_var.get(),
                self.plugin_host_var.get(),
                self.inject_bridge_var.get(),
            ]
            if sum(1 for x in dll_exclusive if x) > 1:
                messagebox.showerror(
                    "互斥冲突",
                    "九动·DLL / 遇1级自动 / 盗贼辅助 / 插件 Host / 桥接 只能勾一类。\n"
                    "（面板模式下 烧卡/抓宠/抓宠（无宠二动）/抓宠卖银币 可同打，面板内切换）",
                )
                return
            if self.battle_nine_action_var.get() and self.battle_nine_external_var.get():
                messagebox.showerror("互斥冲突", "神奇九动 IL原版 与 DLL版 不能同时勾选。")
                return
            panel_mode = self.wiki_test_ui_var.get()
            wiki_users: list[str] = []
            if not panel_mode:
                if (
                    self.auto_catch_external_var.get()
                    or self.auto_catch_nopet_external_var.get()
                    or self.auto_catch_sell_external_var.get()
                ):
                    wiki_users.append("自动抓宠")
                if self.lv1_auto_external_var.get():
                    wiki_users.append("遇1级自动")
                if self.auto_seal_external_var.get():
                    wiki_users.append("自动烧卡")
            if self.auto_sell_external_var.get():
                wiki_users.append("盗贼辅助")
            if self.plugin_host_var.get():
                wiki_users.append("插件Host")
            if self.wiki_fps_var.get():
                wiki_users.append("百科限帧")
            if self.wiki_test_ui_var.get():
                wiki_users.append("助手面板")
            if len(wiki_users) > 1:
                messagebox.showerror(
                    "互斥冲突",
                    "侧栏百科只能占一类：" + " / ".join(wiki_users) + "。",
                )
                return

            apply_combo(
                vip=self.vip_var.get(),
                vip_non_vip=self.vip_non_vip_var.get(),
                vip_scale=int(self.vip_scale_var.get()),
                battle_nine_action=self.battle_nine_action_var.get(),
                battle_nine_external=self.battle_nine_external_var.get(),
                auto_seal_external=self.auto_seal_external_var.get(),
                auto_catch_external=self.auto_catch_external_var.get(),
                auto_catch_nopet_external=self.auto_catch_nopet_external_var.get(),
                auto_catch_sell_external=self.auto_catch_sell_external_var.get(),
                lv1_auto_external=self.lv1_auto_external_var.get(),
                auto_sell_external=self.auto_sell_external_var.get(),
                count_farm=self.count_farm_var.get(),
                area_extract=self.area_extract_var.get(),
                auto_point=self.auto_point_var.get(),
                plugin_host=self.plugin_host_var.get(),
                customer_gm=self.customer_gm_var.get(),
                customer_gm_mode=self.customer_gm_mode_var.get(),
                map_sprint=self.map_sprint_var.get(),
                map_sprint_scale=int(self.map_sprint_scale_var.get()),
                battle_longpress=self.battle_longpress_var.get(),
                level_one_include_all=self.level_one_include_all_var.get(),
                transition_speed=self.transition_speed_var.get(),
                transition_speed_scale=float(self.transition_speed_scale_var.get()),
                skill_effect_speed=self.skill_effect_speed_var.get(),
                skill_effect_scale=float(self.skill_effect_scale_var.get()),
                daily_claim=self.daily_claim_var.get(),
                newbie_gift_code=self.newbie_gift_code_var.get(),
                gift_codes=self.gift_codes_box.get("1.0", "end"),
                boss_key_fps=self.boss_key_fps_var.get(),
                wiki_fps=self.wiki_fps_var.get(),
                wiki_test_ui=self.wiki_test_ui_var.get(),
                battle_appear=self.battle_appear_var.get(),
                inject_bridge=self.inject_bridge_var.get(),
                from_orig=True,
                game_root=root,
            )
            messagebox.showinfo("应用补丁", "成功")
            self.refresh_status()
        except Exception as exc:
            messagebox.showerror("应用补丁失败", str(exc).strip() or "未知错误")


def main() -> int:
    app = ComboPatchApp()
    app.root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
