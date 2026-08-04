#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 傻瓜换装补丁。点侧栏「百科」循环切换 4 套装备。"""
from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

from foolproof_skin_apply import (
    FoolproofError,
    is_unclean_client_error,
    resolve_game_root,
    restore_hotfixdata_from_clean,
    run_foolproof_skin_patch,
)
from patch_common import EXPECTED_SIZE, detect_game_root_from_launcher, get_game_root

APP_TITLE = "傻瓜换装补丁"


def show_popup(title: str, text: str, *, error: bool = False) -> None:
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
    try:
        msgs = run_foolproof_skin_patch(on_log=lambda line: print(line, flush=True))
        detail = "\n".join(msgs[-8:]) if msgs else "补丁已打好。"
        show_popup(
            f"{APP_TITLE} — 成功",
            "补丁已打好。\n进游戏点「百科」切换装备套装（1→2→3→4）。\n\n" + detail,
        )
        return 0
    except FoolproofError as exc:
        hint = ""
        if is_unclean_client_error(exc):
            hint = "\n\n可打开界面，从干净目录恢复后再打。"
        show_popup(f"{APP_TITLE} — 失败", str(exc) + hint, error=True)
        return 1
    except Exception as exc:
        show_popup(f"{APP_TITLE} — 失败", str(exc), error=True)
        return 1


class SkinFoolproofApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("720x480")
        self.minsize(600, 400)

        body = ttk.Frame(self, padding=12)
        body.pack(fill=tk.BOTH, expand=True)

        row = ttk.Frame(body)
        row.pack(fill=tk.X)
        ttk.Label(row, text="游戏目录").pack(side=tk.LEFT)
        self.path_var = tk.StringVar()
        ttk.Entry(row, textvariable=self.path_var).pack(
            side=tk.LEFT, fill=tk.X, expand=True, padx=8
        )
        ttk.Button(row, text="浏览…", command=self.browse).pack(side=tk.LEFT)

        tip = (
            "打入后进游戏点侧栏「百科」，循环切换 4 套装备（1→2→3→4）。\n"
            "只做换装，无其它功能。客户端不干净时可先「从干净目录恢复」。"
        )
        ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 8))

        btns = ttk.Frame(body)
        btns.pack(fill=tk.X, pady=(0, 8))
        self.apply_btn = ttk.Button(btns, text="一键打补丁", command=self.on_apply)
        self.apply_btn.pack(side=tk.LEFT)
        self.restore_btn = ttk.Button(
            btns, text="从干净目录恢复…", command=self.on_restore_clean
        )
        self.restore_btn.pack(side=tk.LEFT, padx=(8, 0))
        ttk.Label(
            btns,
            text=f"期望 hotfix 体积 {EXPECTED_SIZE:,}",
            foreground="#666666",
        ).pack(side=tk.RIGHT)

        self.log = scrolledtext.ScrolledText(body, height=16, wrap=tk.WORD)
        self.log.pack(fill=tk.BOTH, expand=True)

        self._busy = False
        self._load_default_path()

    def _load_default_path(self) -> None:
        try:
            root = resolve_game_root(None)
            self.path_var.set(str(root))
        except Exception:
            root = get_game_root() or detect_game_root_from_launcher()
            if root:
                self.path_var.set(str(root))

    def browse(self) -> None:
        path = filedialog.askdirectory(title="选择游戏目录（含 cg37.exe）")
        if path:
            self.path_var.set(path)

    def _append(self, line: str) -> None:
        self.log.insert(tk.END, line + "\n")
        self.log.see(tk.END)

    def _game_root(self) -> Path | None:
        raw = self.path_var.get().strip()
        return Path(raw) if raw else None

    def _set_busy(self, busy: bool) -> None:
        self._busy = busy
        state = ["disabled"] if busy else ["!disabled"]
        self.apply_btn.state(state)
        self.restore_btn.state(state)

    def on_apply(self) -> None:
        if self._busy:
            return
        root = self._game_root()

        def work() -> None:
            try:
                msgs = run_foolproof_skin_patch(
                    root, on_log=lambda line: self.after(0, self._append, line)
                )
                detail = "\n".join(msgs[-6:]) if msgs else ""
                self.after(
                    0,
                    lambda: messagebox.showinfo(
                        APP_TITLE,
                        "补丁已打好。\n进游戏点「百科」切换装备套装（1→2→3→4）。\n\n"
                        + detail,
                        parent=self,
                    ),
                )
            except FoolproofError as exc:
                text = str(exc)
                if is_unclean_client_error(exc):
                    text += "\n\n可点「从干净目录恢复…」后再打。"
                self.after(
                    0,
                    lambda: messagebox.showerror(APP_TITLE, text, parent=self),
                )
            except Exception as exc:
                self.after(
                    0,
                    lambda: messagebox.showerror(APP_TITLE, str(exc), parent=self),
                )
            finally:
                self.after(0, self._set_busy, False)

        self._set_busy(True)
        self._append("======== 开始打补丁 ========")
        threading.Thread(target=work, daemon=True).start()

    def on_restore_clean(self) -> None:
        if self._busy:
            return
        game = self._game_root()
        if game is None:
            messagebox.showerror(APP_TITLE, "请先选择游戏目录。", parent=self)
            return
        clean = filedialog.askdirectory(title="选择干净客户端目录（未打补丁）")
        if not clean:
            return

        def work() -> None:
            try:
                msgs = restore_hotfixdata_from_clean(
                    Path(clean),
                    game,
                    on_log=lambda line: self.after(0, self._append, line),
                )
                self.after(
                    0,
                    lambda: messagebox.showinfo(
                        APP_TITLE,
                        "已恢复，可继续「一键打补丁」。\n\n" + "\n".join(msgs[-4:]),
                        parent=self,
                    ),
                )
            except FoolproofError as exc:
                self.after(
                    0,
                    lambda: messagebox.showerror(APP_TITLE, str(exc), parent=self),
                )
            except Exception as exc:
                self.after(
                    0,
                    lambda: messagebox.showerror(APP_TITLE, str(exc), parent=self),
                )
            finally:
                self.after(0, self._set_busy, False)

        self._set_busy(True)
        self._append("======== 从干净目录恢复 ========")
        threading.Thread(target=work, daemon=True).start()


def main() -> int:
    if any(a in ("--auto", "/auto") for a in sys.argv[1:]):
        return run_auto()
    app = SkinFoolproofApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
