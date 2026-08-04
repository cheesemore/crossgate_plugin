#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓宠卖银币设置：填写回收掉档阈值 Y（默认 6）。"""
from __future__ import annotations

import sys
import tkinter as tk
from tkinter import messagebox, ttk

from catch_sell_config import (
    DEFAULT_RECYCLE_MIN_GRADE,
    config_path,
    load_recycle_min_grade,
    save_recycle_min_grade,
)


class CatchSellConfigApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("抓宠卖银币设置")
        self.geometry("420x220")
        self.minsize(360, 180)
        self.attributes("-topmost", True)

        body = ttk.Frame(self, padding=14)
        body.pack(fill=tk.BOTH, expand=True)

        ttk.Label(
            body,
            text="优先在游戏内：百科 → 助手面板 → 战斗模式\n"
            "里设置「回收掉档阈值 Y」（与这里同一配置文件）。\n\n"
            "规则：名字已 # → 跳过；掉档≥Y 且无 @ → 回收；其余改名存仓。",
            justify=tk.LEFT,
        ).pack(anchor=tk.W)

        row = ttk.Frame(body)
        row.pack(fill=tk.X, pady=(16, 8))
        ttk.Label(row, text="回收掉档阈值 Y").pack(side=tk.LEFT)
        self.y_var = tk.StringVar(value=str(load_recycle_min_grade()))
        ttk.Entry(row, textvariable=self.y_var, width=8).pack(side=tk.LEFT, padx=8)
        ttk.Label(row, text=f"（默认 {DEFAULT_RECYCLE_MIN_GRADE}）", foreground="#666").pack(
            side=tk.LEFT
        )

        self.path_var = tk.StringVar(value=str(config_path()))
        ttk.Label(body, textvariable=self.path_var, foreground="#666666", wraplength=380).pack(
            anchor=tk.W, pady=(4, 12)
        )

        btns = ttk.Frame(body)
        btns.pack(fill=tk.X)
        ttk.Button(btns, text="保存", command=self.on_save).pack(side=tk.LEFT)
        ttk.Button(btns, text="恢复默认", command=self.on_default).pack(side=tk.LEFT, padx=8)

    def on_default(self) -> None:
        self.y_var.set(str(DEFAULT_RECYCLE_MIN_GRADE))

    def on_save(self) -> None:
        raw = self.y_var.get().strip()
        try:
            y = int(raw)
        except ValueError:
            messagebox.showerror(self.title(), "Y 请填写非负整数。", parent=self)
            return
        if y < 0:
            messagebox.showerror(self.title(), "Y 不能为负。", parent=self)
            return
        path = save_recycle_min_grade(y)
        self.path_var.set(str(path))
        messagebox.showinfo(
            self.title(),
            f"已保存 Y={y}\n下次退战流水线生效（无需重打补丁）。",
            parent=self,
        )


def main() -> int:
    app = CatchSellConfigApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
