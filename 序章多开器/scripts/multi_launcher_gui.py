#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章多开器 — 管理多套账号、启动多个游戏实例并绑定序章助手。"""
from __future__ import annotations

import subprocess
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk

SHARED = Path(__file__).resolve().parents[2] / "序章助手共享"
ASSISTANT = Path(__file__).resolve().parents[2] / "魔力宝贝序章助手"
PATCH_TOOLKIT = Path(__file__).resolve().parents[2] / "魔力宝贝序章补丁"
sys.path.insert(0, str(SHARED))

from assistant_common.accounts import AccountProfile, delete_account, load_accounts, upsert_account  # noqa: E402
from assistant_common.config import get_game_root, load_settings, save_settings, set_game_root  # noqa: E402
from assistant_common.game import GameInstance, find_game_processes, launch_game  # noqa: E402
from assistant_common.patch_bridge import is_bridge_patched  # noqa: E402
from assistant_common.single_instance import ensure_single_instance  # noqa: E402
from assistant_common import ipc  # noqa: E402

APP_TITLE = "序章多开器"


class MultiLauncherApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        self.root.title(APP_TITLE)
        self.root.geometry("740x620")
        self.root.minsize(680, 560)

        cfg = load_settings()
        self.instances: list[GameInstance] = []
        self.game_root_var = tk.StringVar(value=str(get_game_root()))
        self.auto_workflow_var = tk.BooleanVar(value=cfg.get("auto_assistant_workflow", False))

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text=APP_TITLE, font=("Microsoft YaHei UI", 12, "bold")).pack(anchor=tk.W)
        ttk.Label(
            outer,
            text="记录多套账号，多开 cg37.exe，并为每个窗口绑定「序章助手」。"
            "热补丁与桥接注入请在「序章补丁」中完成。",
            foreground="#555",
            wraplength=660,
        ).pack(anchor=tk.W, pady=(0, 8))

        path_frm = ttk.LabelFrame(outer, text="游戏目录", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 8))
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.game_root_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="浏览", command=self.pick_game_dir, width=8).pack(side=tk.LEFT, padx=(6, 0))

        status_row = ttk.Frame(path_frm)
        status_row.pack(fill=tk.X, pady=(8, 0))
        self.bridge_status_var = tk.StringVar(value="")
        ttk.Label(status_row, textvariable=self.bridge_status_var, foreground="#666").pack(side=tk.LEFT)
        ttk.Button(status_row, text="打开序章补丁", command=self.open_patch_gui, width=12).pack(side=tk.RIGHT)

        assist_frm = ttk.Frame(path_frm)
        assist_frm.pack(fill=tk.X, pady=(6, 0))
        ttk.Checkbutton(
            assist_frm,
            text="打开助手后自动操作（登录 → 进游戏 → 五控 → 召唤）",
            variable=self.auto_workflow_var,
            command=self._save_launcher_settings,
        ).pack(side=tk.LEFT)

        batch_frm = ttk.LabelFrame(outer, text="批量一键（协议驱动，需先注入桥接）", padding=8)
        batch_frm.pack(fill=tk.X, pady=(0, 8))
        batch_row = ttk.Frame(batch_frm)
        batch_row.pack(fill=tk.X)
        self.batch_login_btn = ttk.Button(
            batch_row,
            text="一键登录并拉取多控",
            command=self.batch_login_fetch,
            width=20,
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
        ttk.Label(batch_frm, textvariable=self.batch_status_var, font=("Microsoft YaHei UI", 8), foreground="#888").pack(
            anchor=tk.W, pady=(6, 0)
        )
        ttk.Label(
            batch_frm,
            text="「一键登录并拉取多控」按账号库顺序给已注入桥接的实例自动登录→进游戏→拉起离线多控；"
            "「一键召唤」对所有实例发一键召唤，按 team>=5 协议判定聚齐（不依赖坐标）。",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
            wraplength=680,
        ).pack(anchor=tk.W, pady=(2, 0))

        action_frm = ttk.LabelFrame(outer, text="快捷操作", padding=8)
        action_frm.pack(fill=tk.X, pady=(0, 8))
        action_row = ttk.Frame(action_frm)
        action_row.pack(fill=tk.X)
        self.launch_acc_btn = ttk.Button(
            action_row,
            text="启动并绑定助手",
            command=self.launch_selected_account,
            width=18,
        )
        self.launch_acc_btn.pack(side=tk.LEFT, padx=(0, 8))
        self.open_asst_btn = ttk.Button(
            action_row,
            text="打开助手",
            command=self.open_assistant_for_selected,
            width=12,
        )
        self.open_asst_btn.pack(side=tk.LEFT, padx=(0, 8))
        self.open_asst_acc_btn = ttk.Button(
            action_row,
            text="仅打开助手",
            command=self.open_assistant_for_account,
            width=12,
        )
        self.open_asst_acc_btn.pack(side=tk.LEFT)
        self.launch_blank_btn = ttk.Button(
            action_row,
            text="空启动游戏",
            command=self.launch_blank,
            width=12,
        )
        self.launch_blank_btn.pack(side=tk.RIGHT)
        ttk.Label(
            action_frm,
            text="先选左侧账号再「启动并绑定助手」；对已运行实例选右侧后点「打开助手」。",
            font=("Microsoft YaHei UI", 8),
            foreground="#888",
            wraplength=680,
        ).pack(anchor=tk.W, pady=(6, 0))

        paned = ttk.Panedwindow(outer, orient=tk.HORIZONTAL)
        paned.pack(fill=tk.BOTH, expand=True, pady=(0, 8))

        acc_frm = ttk.LabelFrame(paned, text="账号库", padding=6)
        inst_frm = ttk.LabelFrame(paned, text="运行中的实例", padding=6)
        paned.add(acc_frm, weight=1)
        paned.add(inst_frm, weight=1)

        self.acc_tree = ttk.Treeview(acc_frm, columns=("phone", "label"), show="headings", height=7)
        self.acc_tree.heading("label", text="备注")
        self.acc_tree.heading("phone", text="手机号")
        self.acc_tree.column("label", width=100)
        self.acc_tree.column("phone", width=120)
        self.acc_tree.pack(fill=tk.BOTH, expand=True)
        acc_btn = ttk.Frame(acc_frm)
        acc_btn.pack(fill=tk.X, pady=(6, 0))
        ttk.Button(acc_btn, text="添加账号", command=self.add_account, width=10).pack(side=tk.LEFT)
        ttk.Button(acc_btn, text="删除账号", command=self.remove_account, width=10).pack(side=tk.LEFT, padx=4)

        self.inst_tree = ttk.Treeview(inst_frm, columns=("pid", "iid"), show="headings", height=7)
        self.inst_tree.heading("pid", text="PID")
        self.inst_tree.heading("iid", text="实例 ID")
        self.inst_tree.column("pid", width=80)
        self.inst_tree.column("iid", width=180)
        self.inst_tree.pack(fill=tk.BOTH, expand=True)
        inst_btn = ttk.Frame(inst_frm)
        inst_btn.pack(fill=tk.X, pady=(6, 0))
        ttk.Button(inst_btn, text="刷新进程", command=self.refresh_instances, width=10).pack(side=tk.LEFT)

        self.reload_accounts()
        self.refresh_instances()
        self.refresh_bridge_status()

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
        self.refresh_bridge_status()

    def _save_launcher_settings(self) -> None:
        cfg = load_settings()
        cfg["auto_assistant_workflow"] = bool(self.auto_workflow_var.get())
        save_settings(cfg)

    def open_patch_gui(self) -> None:
        bat = PATCH_TOOLKIT / "启动补丁GUI.bat"
        py = PATCH_TOOLKIT / "scripts" / "seqchapter_combo_gui.py"
        try:
            if bat.is_file():
                subprocess.Popen(["cmd", "/c", "start", "", str(bat)], shell=False)
            elif py.is_file():
                subprocess.Popen([sys.executable, str(py)])
            else:
                messagebox.showerror("未找到", f"找不到序章补丁：{PATCH_TOOLKIT}")
        except OSError as exc:
            messagebox.showerror("打开失败", str(exc))

    def _bridge_is_ready(self) -> bool:
        try:
            return is_bridge_patched(self._game_root())
        except Exception:
            return False

    def refresh_bridge_status(self) -> None:
        try:
            if self._bridge_is_ready():
                self.bridge_status_var.set("桥接：已注入")
            else:
                self.bridge_status_var.set("桥接：未注入 — 请在序章补丁中应用补丁并注入桥接")
        except Exception:
            self.bridge_status_var.set("桥接：未知")

    def _warn_if_auto_workflow_without_bridge(self) -> bool:
        if not self.auto_workflow_var.get() or self._bridge_is_ready():
            return True
        return messagebox.askyesno(
            "桥接未连接",
            "当前未注入助手桥接，「自动操作」不会生效，但游戏可以正常启动。\n\n"
            "请先在「序章补丁」中完成桥接注入。\n\n仍要启动并打开助手吗？",
        )

    def reload_accounts(self) -> None:
        for item in self.acc_tree.get_children():
            self.acc_tree.delete(item)
        for acc in load_accounts():
            self.acc_tree.insert("", tk.END, iid=acc.id, values=(acc.label, acc.phone))

    def add_account(self) -> None:
        label = simpledialog.askstring("备注", "账号备注（可选）:", parent=self.root) or ""
        phone = simpledialog.askstring("手机号", "手机号:", parent=self.root)
        if not phone:
            return
        password = simpledialog.askstring("密码", "密码:", show="*", parent=self.root)
        if password is None:
            return
        profile = AccountProfile.create(label, phone, password)
        upsert_account(profile)
        self.reload_accounts()

    def remove_account(self) -> None:
        sel = self.acc_tree.selection()
        if not sel:
            return
        if messagebox.askyesno("确认", "删除所选账号？"):
            delete_account(sel[0])
            self.reload_accounts()

    def _game_root(self) -> Path:
        return Path(self.game_root_var.get().strip())

    def launch_selected_account(self) -> None:
        sel = self.acc_tree.selection()
        if not sel:
            messagebox.showwarning("未选择", "请先选择账号")
            return
        acc = next((a for a in load_accounts() if a.id == sel[0]), None)
        if acc is None:
            return
        if not self._warn_if_auto_workflow_without_bridge():
            return
        try:
            inst = launch_game(self._game_root())
            self.instances.append(inst)
            self.refresh_instances()
            self._open_assistant(inst.instance_id, acc.phone, acc.password)
        except Exception as exc:
            messagebox.showerror("启动失败", str(exc))

    def launch_blank(self) -> None:
        try:
            inst = launch_game(self._game_root())
            self.instances.append(inst)
            self.refresh_instances()
        except Exception as exc:
            messagebox.showerror("启动失败", str(exc))

    def refresh_instances(self) -> None:
        for item in self.inst_tree.get_children():
            self.inst_tree.delete(item)
        known = {i.pid: i for i in self.instances}
        for pid, _ in find_game_processes():
            inst = known.get(pid)
            iid = inst.instance_id if inst else f"inst_{pid}"
            if inst is None:
                self.instances.append(GameInstance(instance_id=iid, pid=pid))
            self.inst_tree.insert("", tk.END, iid=str(pid), values=(pid, iid))

    # --- 批量一键（协议驱动，靠 bridge 命令与 team>=5 判定） ---

    def _live_bridge_instances(self) -> list[str]:
        """返回当前有桥接心跳的实例 ID（按 PID 稳定排序）。"""
        rows = ipc.list_instance_snapshots()
        live = [r for r in rows if r.get("alive")]
        live.sort(key=lambda r: r.get("pid_txt") or 0)
        return [r["instance_id"] for r in live]

    def _require_accounts(self) -> list[AccountProfile] | None:
        accounts = load_accounts()
        if not accounts:
            messagebox.showwarning("无账号", "账号库为空，请先在左侧「账号库」添加账号。")
            return None
        return accounts

    def batch_login_fetch(self) -> None:
        """批量：按账号库顺序对每个已注入桥接的实例 登录→进游戏→拉起离线多控。"""
        if not self._warn_if_auto_workflow_without_bridge():
            return
        accounts = self._require_accounts()
        if accounts is None:
            return
        iids = self._live_bridge_instances()
        if not iids:
            messagebox.showwarning("无实例", "没有检测到已注入桥接的实例。\n请先启动游戏并确保已注入助手桥接。")
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
                if phase == "in_game":
                    self._set_batch_status(f"[{label}] 已进游戏，拉起离线多控…")
                    ipc.multi_login_offline_all(iid)
                    multi_ok = ipc.wait_multi_ready(iid, timeout=120)
                else:
                    self._set_batch_status(f"[{label}] 登录→进游戏→拉起多控…")
                    ipc.workflow_login_enter(iid, acc.phone, acc.password)
                    entered = ipc.wait_for_in_game(iid, timeout=300)
                    if not entered:
                        ok, msg = ipc.wait_workflow_done(iid, timeout=60)
                        if not ok:
                            lines.append(f"[FAIL] {label}: 登录/进游戏失败 {msg}")
                            self._set_batch_status(f"[{label}] 失败")
                            continue
                    ipc.multi_login_offline_all(iid)
                    multi_ok = ipc.wait_multi_ready(iid, timeout=120)
                if multi_ok:
                    ok_count += 1
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
        """批量：对所有实例发一键召唤（协议），按 team>=5 判定聚齐。"""
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
            self._set_batch_status(f"[{iid}] 一键召唤…")
            ipc.one_key_summon(iid)
            ok = ipc.wait_for_team(iid, timeout=90)
            if ok:
                ok_count += 1
                lines.append(f"[OK] {iid}: 队伍聚齐 (team>=5)")
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

    def open_assistant_for_selected(self) -> None:
        sel = self.inst_tree.selection()
        if not sel:
            messagebox.showwarning("未选择", "请选择实例")
            return
        if not self._bridge_is_ready():
            if not messagebox.askyesno(
                "桥接未注入",
                "当前未检测到助手桥接，助手可能显示「未连接」。\n\n"
                "请先在「序章补丁」中注入桥接后再启动游戏。\n\n仍要打开助手吗？",
            ):
                return
        pid = int(sel[0])
        inst = next((i for i in self.instances if i.pid == pid), None)
        iid = inst.instance_id if inst else f"inst_{pid}"
        phone = password = ""
        acc_sel = self.acc_tree.selection()
        if acc_sel:
            acc = next((a for a in load_accounts() if a.id == acc_sel[0]), None)
            if acc:
                phone, password = acc.phone, acc.password
        self._open_assistant(iid, phone, password)

    def open_assistant_for_account(self) -> None:
        """不启动游戏，仅打开助手（需先在右侧选中运行中的实例）。"""
        inst_sel = self.inst_tree.selection()
        if inst_sel:
            self.open_assistant_for_selected()
            return
        acc_sel = self.acc_tree.selection()
        if not acc_sel:
            messagebox.showwarning(
                "未选择",
                "请先在右侧「运行中的实例」选中一个进程，\n或在左侧选中账号后再点「仅打开助手」。",
            )
            return
        acc = next((a for a in load_accounts() if a.id == acc_sel[0]), None)
        if acc is None:
            return
        procs = find_game_processes()
        if len(procs) == 1:
            pid = procs[0][0]
            iid = f"inst_{pid}"
            self._open_assistant(iid, acc.phone, acc.password)
            return
        messagebox.showwarning(
            "需要选择实例",
            "当前有多个游戏进程或未检测到游戏。\n"
            "请先在右侧选中对应实例，再点「打开助手」或「仅打开助手」。",
        )

    def _open_assistant(self, instance_id: str, phone: str = "", password: str = "") -> None:
        script = ASSISTANT / "scripts" / "main_gui.py"
        args = [sys.executable, str(script), instance_id]
        if phone:
            args.append(phone)
        if password:
            args.append(password)
        if self.auto_workflow_var.get():
            args.append("auto")
        subprocess.Popen(args, creationflags=subprocess.DETACHED_PROCESS if hasattr(subprocess, "DETACHED_PROCESS") else 0)


def main() -> int:
    if not ensure_single_instance(
        APP_TITLE,
        message=(
            f"{APP_TITLE} 已在运行，不能重复打开。\n\n"
            "本程序仅允许运行一个窗口；请在已打开的窗口中管理游戏多开。"
        ),
    ):
        return 1
    app = MultiLauncherApp()
    app.root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
