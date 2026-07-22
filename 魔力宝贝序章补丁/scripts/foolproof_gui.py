#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 傻瓜补丁（一键固定组合）。

用法：
  傻瓜补丁.exe           打开界面
  傻瓜补丁.exe --auto    无界面自动打补丁（供 一键打补丁.bat）
"""
from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

from foolproof_apply import FoolproofError, resolve_game_root, run_foolproof_patch
from patch_common import DATA_DIR, EXPECTED_SIZE, detect_game_root_from_launcher, get_game_root


def _detect_no_nine() -> bool:
    if any(a in ("--no-nine", "--without-nine", "/no-nine") for a in sys.argv[1:]):
        return True
    bases: list[Path] = []
    if getattr(sys, "frozen", False):
        bases.append(Path(sys.executable).resolve().parent)
    bases.append(Path(__file__).resolve().parent)
    for base in bases:
        if (base / "无九动.flag").is_file() or (base / "NO_NINE").is_file():
            return True
    return False


NO_NINE = _detect_no_nine()


def show_popup(title: str, text: str, *, error: bool = False) -> None:
    """成功/失败弹窗（无主窗口时也能用）。"""
    root = tk.Tk()
    root.withdraw()
    try:
        if error:
            messagebox.showerror(title, text, parent=root)
        else:
            messagebox.showinfo(title, text, parent=root)
    finally:
        root.destroy()


def run_auto() -> int:
    """命令行/ bat 一键：自动找游戏目录 → 打补丁 → 弹窗。"""
    try:
        msgs = run_foolproof_patch(enable_nine=not NO_NINE)
        detail = "\n".join(msgs[-8:]) if msgs else "补丁已打好。"
        title = "傻瓜补丁（无九动）— 成功" if NO_NINE else "傻瓜补丁 — 成功"
        show_popup(title, f"补丁已打好。\n请启动游戏验证。\n\n{detail}")
        return 0
    except FoolproofError as exc:
        show_popup("傻瓜补丁 — 无法打补丁", str(exc), error=True)
        return 1
    except Exception as exc:
        show_popup("傻瓜补丁 — 错误", str(exc).strip() or "未知错误", error=True)
        return 1


class FoolproofApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        title = "魔力宝贝：序章 — 傻瓜补丁（无九动）" if NO_NINE else "魔力宝贝：序章 — 傻瓜补丁"
        self.root.title(title)
        self.root.geometry("560x480")
        self.root.minsize(520, 420)

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        head = "傻瓜补丁（无九动）" if NO_NINE else "傻瓜补丁（一键）"
        ttk.Label(
            outer,
            text=head,
            font=("Microsoft YaHei UI", 12, "bold"),
        ).pack(anchor=tk.W)
        nine_line = (
            "· 不含神奇九动 · 无加速过场 · 无桥接。"
            if NO_NINE
            else "· 神奇九动（优先 IL）· 无加速过场 · 无桥接。"
        )
        ttk.Label(
            outer,
            text=(
                "固定组合：\n"
                "VIP/非VIP 5x · 自动技能 · 跑速快 · 长按详情 · 特效2x\n"
                f"{nine_line}会自动向上查找游戏目录。"
            ),
            wraplength=520,
            foreground="#444",
        ).pack(anchor=tk.W, pady=(4, 10))

        path_frm = ttk.LabelFrame(outer, text=f"游戏目录（含 {DATA_DIR} / cg37.exe）", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 8))
        self.path_var = tk.StringVar()
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.path_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="浏览…", command=self.pick_dir, width=8).pack(side=tk.LEFT, padx=(6, 0))

        ttk.Label(
            outer,
            text=f"本工具适配 hotfix 体积：{EXPECTED_SIZE:,} 字节",
            foreground="#666",
        ).pack(anchor=tk.W)

        btn_row = ttk.Frame(outer)
        btn_row.pack(fill=tk.X, pady=10)
        self.apply_btn = tk.Button(
            btn_row,
            text="一键打补丁",
            command=self.on_apply,
            width=16,
            height=2,
            font=("Microsoft YaHei UI", 11, "bold"),
            fg="#0a5",
        )
        self.apply_btn.pack(side=tk.LEFT)
        ttk.Button(btn_row, text="退出", command=self.root.destroy, width=8).pack(
            side=tk.LEFT, padx=(10, 0)
        )

        log_frm = ttk.LabelFrame(outer, text="日志", padding=6)
        log_frm.pack(fill=tk.BOTH, expand=True)
        self.log = scrolledtext.ScrolledText(log_frm, height=14, font=("Consolas", 9), wrap=tk.WORD)
        self.log.pack(fill=tk.BOTH, expand=True)

        self._load_path()

    def _load_path(self) -> None:
        root = get_game_root() or detect_game_root_from_launcher()
        if root:
            self.path_var.set(str(root))

    def pick_dir(self) -> None:
        chosen = filedialog.askdirectory(title=f"选择游戏根目录（含 {DATA_DIR}）")
        if chosen:
            self.path_var.set(chosen)

    def _append(self, text: str) -> None:
        self.log.insert(tk.END, text + "\n")
        self.log.see(tk.END)

    def on_apply(self) -> None:
        raw = self.path_var.get().strip()
        explicit = Path(raw) if raw else None
        self.apply_btn.configure(state=tk.DISABLED)
        self.log.delete("1.0", tk.END)
        self._append("开始…")

        def work() -> None:
            try:
                root = resolve_game_root(explicit)
                self.root.after(0, lambda: self.path_var.set(str(root)))
                msgs = run_foolproof_patch(root, enable_nine=not NO_NINE)

                def ok() -> None:
                    for m in msgs:
                        self._append(m)
                    self.apply_btn.configure(state=tk.NORMAL)
                    messagebox.showinfo("完成", "补丁已打好。\n请启动游戏验证。")

                self.root.after(0, ok)
            except FoolproofError as exc:
                def fail_user() -> None:
                    self._append("[失败] " + str(exc))
                    self.apply_btn.configure(state=tk.NORMAL)
                    messagebox.showerror("无法打补丁", str(exc))

                self.root.after(0, fail_user)
            except Exception as exc:
                def fail() -> None:
                    self._append(f"[错误] {exc}")
                    self.apply_btn.configure(state=tk.NORMAL)
                    messagebox.showerror("错误", str(exc).strip() or "未知错误")

                self.root.after(0, fail)

        threading.Thread(target=work, daemon=True).start()

    def run(self) -> None:
        self.root.mainloop()


def main() -> int:
    if any(a in ("--auto", "-a", "/auto") for a in sys.argv[1:]):
        return run_auto()
    FoolproofApp().run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
