#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""新序章多开器 — 管理账号库，一键启动并自动登录/拉取多控/一键召唤。

启动游戏后自动连接精简桥接（SeqChapterMiniBridge），依次走
workflow_step1（登录→进游戏→拉起离线多控→一键召唤）。全程靠协议
（IPC / team_num / multi_ready）判定，不依赖坐标；进度由游戏内 Tip 飘字反馈。
需先在「序章补丁」勾选「注入精简桥接」。
"""
from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk

SHARED = Path(__file__).resolve().parents[2] / "序章助手共享"
sys.path.insert(0, str(SHARED))

from assistant_common import ipc  # noqa: E402
from assistant_common.accounts import AccountProfile, delete_account, load_accounts, upsert_account  # noqa: E402
from assistant_common.config import get_game_root, set_game_root  # noqa: E402
from assistant_common.game import GameInstance, launch_game  # noqa: E402
from assistant_common.patch_bridge import is_bridge_patched  # noqa: E402
from assistant_common.single_instance import ensure_single_instance  # noqa: E402

APP_TITLE = "新序章多开器"


class MultiLauncherApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        self.root.title(APP_TITLE)
        self.root.geometry("900x820")
        self.root.minsize(780, 680)

        self.instances: list[GameInstance] = []
        self.game_root_var = tk.StringVar(value=str(get_game_root()))

        outer = ttk.Frame(self.root, padding=14)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text=APP_TITLE, font=("Microsoft YaHei UI", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(
            outer,
            text="管理账号库，一键启动游戏后自动连接精简桥接并完成 登录→拉多控→一键召唤。批量功能为协议驱动，需先注入精简桥接（序章补丁中勾选「注入精简桥接」）。",
            foreground="#555",
            wraplength=860,
        ).pack(anchor=tk.W, pady=(0, 10))

        path_frm = ttk.LabelFrame(outer, text="游戏目录", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 10))
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.game_root_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="选择目录", command=self.pick_game_dir, width=10).pack(side=tk.LEFT, padx=(6, 0))

        batch_frm = ttk.LabelFrame(outer, text="批量一键（协议驱动，需先注入精简桥接）", padding=8)
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

        acc_frm = ttk.LabelFrame(outer, text="账号库", padding=8)
        acc_frm.pack(fill=tk.BOTH, expand=True)

        cols = ("label", "phone")
        self.acc_tree = ttk.Treeview(acc_frm, columns=cols, show="headings", selectmode="extended")
        self.acc_tree.heading("label", text="备注")
        self.acc_tree.heading("phone", text="手机号")
        self.acc_tree.column("label", width=240, anchor=tk.W)
        self.acc_tree.column("phone", width=300, anchor=tk.W)
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
        ttk.Button(btn_frm, text="录入账号", command=self.add_account, width=12).pack(side=tk.RIGHT)
        ttk.Button(btn_frm, text="修改账号", command=self.edit_account, width=12).pack(side=tk.RIGHT, padx=(0, 8))
        ttk.Button(btn_frm, text="删除账号", command=self.remove_account, width=12).pack(side=tk.RIGHT, padx=(0, 8))

        status_bar = ttk.Frame(outer)
        status_bar.pack(fill=tk.X, pady=(8, 0))
        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(status_bar, textvariable=self.status_var, foreground="#666").pack(side=tk.LEFT)

        self.reload_accounts()

    def _set_status(self, text: str) -> None:
        self.root.after(0, lambda: self.status_var.set(text))

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
        try:
            return is_bridge_patched(self._game_root())
        except Exception:
            return False

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
            self.acc_tree.insert("", tk.END, iid=acc.id, values=(acc.label, acc.phone))
        self._set_status(f"账号库共 {len(load_accounts())} 个账号")

    def add_account(self) -> None:
        label = simpledialog.askstring("备注", "账号备注（可选）:", parent=self.root) or ""
        phone = simpledialog.askstring("手机号", "手机号:", parent=self.root)
        if not phone:
            return
        password = simpledialog.askstring("密码", "密码:", show="*", parent=self.root)
        if password is None:
            return
        upsert_account(AccountProfile.create(label, phone, password))
        self.reload_accounts()

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
        acc.label = label.strip() or phone
        acc.phone = phone.strip()
        acc.password = password
        upsert_account(acc)
        self.reload_accounts()

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
        ok = 0
        errors: list[str] = []
        for acc in chosen:
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
            except Exception as exc:
                errors.append(f"{name}: {exc}")
        summary = f"启动选中完成：成功 {ok}/{len(chosen)}（已自动进入登录→拉多控→召唤流程）"
        if errors:
            summary += "\n" + "\n".join(errors[:10])
        self._set_status(summary)
        if errors:
            messagebox.showwarning("启动选中", summary, parent=self.root)

    def launch_all_accounts(self) -> None:
        accounts = load_accounts()
        if not accounts:
            messagebox.showwarning("无账号", "账号库为空，请先录入账号。")
            return
        if not self._warn_if_bridge_missing():
            return
        ok = 0
        errors: list[str] = []
        for acc in accounts:
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
            except Exception as exc:
                errors.append(f"{name}: {exc}")
        summary = f"一键启动所有完成：成功 {ok}/{len(accounts)}（已自动进入登录→拉多控→召唤流程）"
        if errors:
            summary += "\n" + "\n".join(errors[:10])
        self._set_status(summary)
        messagebox.showinfo("一键启动所有", summary, parent=self.root)

    def _auto_workflow(self, instance_id: str, phone: str, password: str, label: str) -> None:
        """启动后后台自动走精简桥接 workflow_step1：等桥接→登录→进游戏→拉多控→一键召唤。"""
        try:
            self._set_status(f"[{label}] 等待精简桥接连接…")
            if not ipc.wait_for_bridge(instance_id, timeout=240):
                self._set_status(f"[{label}] 桥接连接超时（未注入精简桥接？）")
                return
            self._set_status(f"[{label}] 桥接已连接，自动登录/拉多控/召唤…")
            ipc.workflow_step1_five_chars(instance_id, phone, password)
            ok, msg = ipc.wait_workflow_done(instance_id, timeout=600)
            if ok:
                self._set_status(f"[{label}] 流程完成")
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
            self._set_batch_status(f"[{iid}] 一键召唤…")
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
