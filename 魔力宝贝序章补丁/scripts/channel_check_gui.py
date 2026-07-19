#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""独立工具：检查游戏 partialconfig.bin 渠道号（不参与补丁 GUI 打包）。"""
from __future__ import annotations

import sys
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from patch_common import DATA_DIR, get_channel_status, get_game_root, set_game_root


class ChannelCheckApp:
    def __init__(self) -> None:
        self.root = tk.Tk()
        self.root.title("魔力宝贝：序章 — 渠道号检查")
        self.root.geometry("620x320")
        self.root.minsize(560, 280)

        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        ttk.Label(outer, text="渠道号检查", font=("Microsoft YaHei UI", 11, "bold")).pack(anchor=tk.W)
        ttk.Label(
            outer,
            text=f"读取 {DATA_DIR}/partialconfig.bin 与 StreamingAssets 副本",
            foreground="#555555",
        ).pack(anchor=tk.W, pady=(0, 10))

        path_frm = ttk.LabelFrame(outer, text=f"游戏目录（含 {DATA_DIR}）", padding=8)
        path_frm.pack(fill=tk.X, pady=(0, 8))
        self.path_var = tk.StringVar()
        row = ttk.Frame(path_frm)
        row.pack(fill=tk.X)
        ttk.Entry(row, textvariable=self.path_var).pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(row, text="浏览…", command=self.pick_game_dir, width=8).pack(side=tk.LEFT, padx=(6, 0))

        info_frm = ttk.LabelFrame(outer, text="检查结果", padding=8)
        info_frm.pack(fill=tk.BOTH, expand=True, pady=(0, 8))
        self.result_var = tk.StringVar(value="请选择游戏目录后点「检查」")
        ttk.Label(info_frm, textvariable=self.result_var, justify=tk.LEFT, wraplength=560).pack(
            anchor=tk.W, fill=tk.BOTH, expand=True
        )

        btn_row = ttk.Frame(outer)
        btn_row.pack(fill=tk.X)
        ttk.Button(btn_row, text="检查", command=self.on_check, width=10).pack(side=tk.LEFT)
        ttk.Button(btn_row, text="刷新", command=self.on_check, width=10).pack(side=tk.LEFT, padx=6)

        root = get_game_root()
        if root:
            self.path_var.set(str(root))
            self.on_check()

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
        self.on_check()

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

    def on_check(self) -> None:
        try:
            root = self._resolve_root()
            status = get_channel_status(root)
            lines = [
                f"注册用渠道号：{status['display_value'] or '（缺失）'}",
                "",
                f"主文件：{status['main_value'] or '（缺失）'}",
                f"  {status['main_path']}",
                "",
                f"StreamingAssets：{status['streaming_value'] or '（缺失）'}",
                f"  {status['streaming_path']}",
            ]
            if status["main_value"] and status["streaming_value"] and not status["consistent"]:
                lines.extend(["", "警告：两处渠道号不一致"])
            self.result_var.set("\n".join(lines))
        except Exception as exc:
            self.result_var.set(f"检查失败：{exc}")


def main() -> int:
    app = ChannelCheckApp()
    app.root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
