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

from apply_combo_patch import apply_combo, get_status
from patch_common import (
    CUSTOMER_GM_LABELS,
    DATA_DIR,
    EXPECTED_SIZE,
    adopt_client_hotfix_update,
    bridge_variant_label,
    detect_hotfix_drift,
    format_client_update_hint,
    format_hotfix_drift_hint,
    format_size_status,
    get_game_root,
    get_update_status,
    has_valid_orig_backup,
    initialize_hotfix_workspace,
    is_bridge_patched,
    mark_hotfix_watch_stamp,
    set_game_root,
    effective_expected_size,
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
        self.root.geometry("600x860")
        self.root.minsize(560, 800)
        self.action_buttons: list[tk.Widget] = []

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text="魔力宝贝：序章 客户端热补丁", font=("Microsoft YaHei UI", 11, "bold")).pack(
            anchor=tk.W
        )
        ttk.Label(
            outer,
            text="关闭游戏 → 选目录 → 初始化 → 应用补丁。初始化会自动完成所有准备工作（可重复点）。",
            wraplength=520,
            foreground="#555555",
        ).pack(anchor=tk.W, pady=(0, 6))
        ttk.Label(
            outer,
            text=f"目标：{DATA_DIR}/assets/hotfixdata/hotfix.dll.bytes",
            wraplength=500,
        ).pack(anchor=tk.W, pady=(0, 10))

        path_frm = ttk.LabelFrame(outer, text=f"游戏目录（含 {DATA_DIR} 文件夹）", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 8))
        self.path_var = tk.StringVar()
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.path_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="浏览…", command=self.pick_game_dir, width=8).pack(side=tk.LEFT, padx=(6, 0))

        opt_frm = ttk.LabelFrame(outer, text="补丁选项", padding=8)
        opt_frm.pack(fill=tk.X, pady=(0, 8))
        self.vip_var = tk.BooleanVar(value=True)
        self.vip_non_vip_var = tk.BooleanVar(value=True)
        self.vip_scale_var = tk.StringVar(value="5")
        self._patch_toggle_guard = False
        self.battle_nine_action_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(opt_frm, text="VIP 战斗倍速", variable=self.vip_var).pack(anchor=tk.W)
        vip_row = ttk.Frame(opt_frm)
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
            opt_frm,
            text="非VIP同样倍速",
            variable=self.vip_non_vip_var,
        ).pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))

        ttk.Checkbutton(
            opt_frm,
            text="神奇九动·IL原版（需足够间隙；与 DLL版/桥接互斥）",
            variable=self.battle_nine_action_var,
            command=self._on_battle_nine_action_toggle,
        ).pack(anchor=tk.W, pady=(8, 0))
        self.battle_nine_external_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            opt_frm,
            text="神奇九动·DLL版（推荐；与 IL原版/桥接互斥）",
            variable=self.battle_nine_external_var,
            command=self._on_battle_nine_external_toggle,
        ).pack(anchor=tk.W, pady=(4, 0))

        self.customer_gm_var = tk.BooleanVar(value=True)
        self.customer_gm_mode_var = tk.StringVar(value="autoskill")
        ttk.Checkbutton(
            opt_frm,
            text="侧栏客服改开功能",
            variable=self.customer_gm_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        for row_modes in (
            (
                ("blindbox", "盲盒3028"),
                ("lottery", "幸运秘宝3049"),
                ("challengeboss", "讨伐令3045"),
                ("bravetrial", "试炼3047"),
                ("crystal", "水晶阁"),
                ("tower", "无尽之塔"),
            ),
            (
                ("autoskill", "自动技能"),
                ("familyhall", "公会领地"),
            ),
        ):
            gm_row = ttk.Frame(opt_frm)
            gm_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
            for mode, text in row_modes:
                ttk.Radiobutton(
                    gm_row,
                    text=text,
                    variable=self.customer_gm_mode_var,
                    value=mode,
                ).pack(side=tk.LEFT, padx=(0, 8))

        self.map_sprint_var = tk.BooleanVar(value=True)
        self.map_sprint_scale_var = tk.StringVar(value="8")
        ttk.Checkbutton(opt_frm, text="Sprint 跑速", variable=self.map_sprint_var).pack(
            anchor=tk.W, pady=(8, 0)
        )
        sprint_row = ttk.Frame(opt_frm)
        sprint_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        for scale, text in (("8", "快"), ("10", "很快"), ("12", "飞快")):
            ttk.Radiobutton(
                sprint_row,
                text=text,
                variable=self.map_sprint_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))

        self.battle_longpress_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            opt_frm,
            text="战斗内长按单位显示详情（解除 PVE 模式限制）",
            variable=self.battle_longpress_var,
        ).pack(anchor=tk.W, pady=(8, 0))

        self.skill_effect_speed_var = tk.BooleanVar(value=True)
        self.skill_effect_scale_var = tk.StringVar(value="2")
        ttk.Checkbutton(
            opt_frm,
            text="战斗技能特效加速（火球/爆炸等帧动画，不影响回合读秒）",
            variable=self.skill_effect_speed_var,
        ).pack(anchor=tk.W, pady=(8, 0))
        effect_row = ttk.Frame(opt_frm)
        effect_row.pack(anchor=tk.W, padx=(18, 0), pady=(4, 0))
        ttk.Label(effect_row, text="倍速:").pack(side=tk.LEFT)
        for scale, text in (("1.5", "1.5x"), ("2", "2x"), ("3", "3x"), ("5", "5x")):
            ttk.Radiobutton(
                effect_row,
                text=text,
                variable=self.skill_effect_scale_var,
                value=scale,
            ).pack(side=tk.LEFT, padx=(0, 8))

        self.update_hint_var = tk.StringVar()
        ttk.Label(
            outer,
            textvariable=self.update_hint_var,
            foreground="#c0392b",
            wraplength=520,
            font=("Microsoft YaHei UI", 9),
        ).pack(anchor=tk.W, pady=(0, 4))

        self.status_var = tk.StringVar()
        ttk.Label(outer, textvariable=self.status_var, font=("Microsoft YaHei UI", 9)).pack(
            anchor=tk.W, pady=(0, 8)
        )

        apply_frm = ttk.Frame(outer)
        apply_frm.pack(fill=tk.X, pady=(0, 8))
        self.inject_bridge_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            apply_frm,
            text="注入桥接（序章助手连接所需；与神奇九动互斥，不能共存）",
            variable=self.inject_bridge_var,
            command=self._on_inject_bridge_toggle,
        ).pack(anchor=tk.W)

        btn_row = ttk.Frame(outer)
        btn_row.pack(fill=tk.X, pady=(0, 8))
        self.init_btn = tk.Button(
            btn_row,
            text="初始化",
            command=self.on_initialize,
            width=14,
            fg="red",
            activeforeground="red",
        )
        self.init_btn.pack(side=tk.LEFT)
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
        ttk.Button(btn_row, text="刷新状态", command=self.refresh_status, width=10).pack(side=tk.LEFT, padx=(6, 0))

        help_frm = ttk.LabelFrame(outer, text="说明", padding=6)
        help_frm.pack(fill=tk.BOTH, expand=True)
        self.help_text = scrolledtext.ScrolledText(help_frm, height=12, font=("Microsoft YaHei UI", 9), wrap=tk.WORD)
        self.help_text.pack(fill=tk.BOTH, expand=True)
        self.help_text.insert(tk.END, self._help_content())
        self.help_text.configure(state=tk.DISABLED)

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

    def _on_battle_nine_action_toggle(self) -> None:
        if self._patch_toggle_guard or not self.battle_nine_action_var.get():
            return
        self._patch_toggle_guard = True
        try:
            self.battle_nine_external_var.set(False)
            if self.inject_bridge_var.get():
                self.inject_bridge_var.set(False)
        finally:
            self._patch_toggle_guard = False

    def _on_battle_nine_external_toggle(self) -> None:
        if self._patch_toggle_guard or not self.battle_nine_external_var.get():
            return
        self._patch_toggle_guard = True
        try:
            self.battle_nine_action_var.set(False)
            if self.inject_bridge_var.get():
                self.inject_bridge_var.set(False)
        finally:
            self._patch_toggle_guard = False

    def _on_inject_bridge_toggle(self) -> None:
        if self._patch_toggle_guard or not self.inject_bridge_var.get():
            return
        self._patch_toggle_guard = True
        try:
            self.battle_nine_action_var.set(False)
            self.battle_nine_external_var.set(False)
        finally:
            self._patch_toggle_guard = False

    def _help_content(self) -> str:
        return f"""【怎么用（两步）】
1. 完全关闭 cg37.exe，选择游戏目录（含 {DATA_DIR}）
2. 点红色「初始化」→ 勾选补丁与「注入桥接」→ 点「应用补丁」→「启动游戏」

【初始化会自动做什么】
• 编译补丁引擎（需 .NET SDK，约半分钟）
• 优先采用游戏内干净 hotfix（与旧底稿哈希不同时视为客户端更新）
• 写入 tools/hotfix.dll.bytes.neworig，并重建 hotfix / .orig
• 清除上次补丁状态；初始化后需再点「应用补丁」
• 可重复点击（更新后、打补丁失败、想恢复原版后再打，都点初始化）

【应用补丁】
• 始终从干净 .orig 开始叠加所选补丁（无需手动还原）
• 勾选「注入桥接」会在补丁打完后注入序章助手桥接（多开器/助手需要）
• 神奇九动（IL原版 / DLL版）与注入桥接不能同时开：
  两者都改写 HotfixEntry.OnApplicationPause 作 DLL 加载器，后打的会覆盖先打的；
  另共用 .text 余量。当前实现无法共存（DLL 版也一样）。
• 「测算余量」可预检各补丁能否打进 .text VA 间隙（约 438B）
• 改补丁选项后：再点一次「应用补丁」即可
• 「启动游戏」会启动目录下的 cg37.exe

【客户端更新检测】
• 每次「初始化 / 应用补丁」成功后会标记当前 hotfix 指纹（体积+哈希）
• 打开工具若发现与标记不一致，状态栏会提示；点初始化或应用补丁时会询问是否自动修复
• 自动修复＝采用游戏内干净新 hotfix 重建底稿，不会用旧 .orig 盖新文件

【客户端更新后】
启动器「更新 / 修复」→ 关闭游戏 → 打开本工具（若提示更新则选「是」）→「应用补丁」

【当前补丁（默认）】
• 桥接 + VIP / 非VIP 战斗 5x · 客服→自动技能 · Sprint 8 · 战斗长按详情 · 技能特效 2x
• 可选：神奇九动（需取消桥接；间隙够优先 IL 原版，不够用 DLL 版）

期望 hotfix 体积：{EXPECTED_SIZE:,} 字节
"""

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
                "\n\n注意：当前 hotfix 看起来已含补丁。若自动修复失败，"
                "请先用启动器「更新/修复」拉回官方原版。"
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
                customer_gm=self.customer_gm_var.get(),
                map_sprint=self.map_sprint_var.get(),
                battle_longpress=self.battle_longpress_var.get(),
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
                or self.customer_gm_var.get()
                or self.map_sprint_var.get()
                or self.battle_longpress_var.get()
                or self.skill_effect_speed_var.get()
                or self.inject_bridge_var.get()
            ):
                messagebox.showwarning("未选择", "请至少勾选一项补丁，或勾选「注入桥接」")
                return
            if self.battle_nine_action_var.get() and self.battle_nine_external_var.get():
                messagebox.showwarning("冲突", "IL原版与 DLL版 不能同时启用。")
                return
            if self.inject_bridge_var.get() and (
                self.battle_nine_action_var.get() or self.battle_nine_external_var.get()
            ):
                messagebox.showwarning(
                    "冲突",
                    "九动与注入桥接不能同时启用，请只保留其中一项。",
                )
                return
            apply_combo(
                vip=self.vip_var.get(),
                vip_non_vip=self.vip_non_vip_var.get(),
                vip_scale=int(self.vip_scale_var.get()),
                battle_nine_action=self.battle_nine_action_var.get(),
                battle_nine_external=self.battle_nine_external_var.get(),
                customer_gm=self.customer_gm_var.get(),
                customer_gm_mode=self.customer_gm_mode_var.get(),
                map_sprint=self.map_sprint_var.get(),
                map_sprint_scale=int(self.map_sprint_scale_var.get()),
                battle_longpress=self.battle_longpress_var.get(),
                skill_effect_speed=self.skill_effect_speed_var.get(),
                skill_effect_scale=float(self.skill_effect_scale_var.get()),
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
