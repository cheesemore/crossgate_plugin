#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Boss/野生算档小工具（独立 GUI）。

输入：Boss 名称 + 等级 + 血量（蓝量可选），点击「推算」。
输出：若干个推断方案，按可能性排列。每个方案给出倍率 rate、掉档、攻防敏精回估计。
血量留空：直接查看该 Boss 在各常见倍率（及全倍率绝对范围）下的七维属性范围。
数据源：tools/pet_rank.bin（或 pet_rank_slim.csv）。
"""
from __future__ import annotations

import os
import sys
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, ttk

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from boss_stat_estimator import BossStatEstimator  # noqa: E402


def _pick_rank_file() -> Path | None:
    cands = [
        HERE / "pet_rank.bin",
        HERE / "pet_rank_slim.csv",
        HERE.parent / "pet_rank.bin",
        HERE.parent / "tools" / "pet_rank.bin",
    ]
    for c in cands:
        if c.is_file():
            return c
    return None


COLUMNS = (
    ("rank", "排名", 50),
    ("rate", "倍率", 70),
    ("fit", "类型", 70),
    ("drops", "掉档", 150),
    ("hp", "HP", 90),
    ("mp", "MP", 90),
    ("atk", "攻击", 110),
    ("def", "防御", 110),
    ("agi", "敏捷", 110),
    ("spi", "精神", 110),
    ("rec", "回复", 110),
)


class BossStatApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.est = BossStatEstimator(_pick_rank_file())
        self.schemes: list = []
        self._ctx: tuple = (None, 1, 0, None)
        self._rows: list = []
        self._all_names: list[str] = []
        root.title("Boss属性计算器")
        root.geometry("1180x640")

        self._build_top()
        self._build_table()
        self._build_detail()

        # 加载档位表
        if self.est.table_count:
            self._all_names = self.est.names()
            self.name_var.set("")
            self.status_var.set("已加载 %d 条档位数据（%s）。血量留空点「推算」可查看属性范围。" % (
                self.est.table_count, self.est.loaded_from))
            self.name_combo["values"] = self._all_names
        else:
            self.status_var.set("档位表加载失败：" + (self.est.load_error or "未知错误"))

    # ---------- UI ----------
    def _build_top(self) -> None:
        top = ttk.Frame(self.root, padding=8)
        top.pack(side="top", fill="x")

        ttk.Label(top, text="名称：").grid(row=0, column=0, sticky="e")
        self.name_var = tk.StringVar()
        self.name_combo = ttk.Combobox(
            top, textvariable=self.name_var, width=26,
            values=self._all_names or [],
        )
        self.name_combo.grid(row=0, column=1, padx=4, sticky="we")
        self.name_combo.bind("<KeyRelease>", self._on_name_key)
        self.name_combo.bind("<<ComboboxSelected>>", lambda e: self._on_name_key(e))

        ttk.Label(top, text="等级：").grid(row=0, column=2, padx=(12, 0), sticky="e")
        self.level_var = tk.StringVar(value="1")
        ttk.Spinbox(top, from_=1, to=200, width=6, textvariable=self.level_var).grid(
            row=0, column=3, padx=4
        )

        ttk.Label(top, text="血量(可选)：").grid(row=0, column=4, padx=(12, 0), sticky="e")
        self.hp_var = tk.StringVar()
        ttk.Entry(top, textvariable=self.hp_var, width=10).grid(row=0, column=5, padx=4)

        ttk.Label(top, text="蓝量(可选)：").grid(row=0, column=6, padx=(12, 0), sticky="e")
        self.mp_var = tk.StringVar()
        ttk.Entry(top, textvariable=self.mp_var, width=10).grid(row=0, column=7, padx=4)

        btn = ttk.Button(top, text="推算", command=self._run)
        btn.grid(row=0, column=8, padx=(12, 0))

        self.status_var = tk.StringVar()
        ttk.Label(top, textvariable=self.status_var, foreground="#666").grid(
            row=1, column=0, columnspan=9, sticky="w", pady=(6, 0)
        )
        top.columnconfigure(1, weight=1)

    def _build_table(self) -> None:
        wrap = ttk.Frame(self.root, padding=(8, 0))
        wrap.pack(side="top", fill="both", expand=True)

        cols = [c[0] for c in COLUMNS]
        self.tree = ttk.Treeview(wrap, columns=cols, show="headings", height=12)
        for cid, text, width in COLUMNS:
            self.tree.heading(cid, text=text)
            self.tree.column(cid, width=width, anchor="center", stretch=False)
        self.tree.column("rate", stretch=True)
        vsb = ttk.Scrollbar(wrap, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        self.tree.bind("<<TreeviewSelect>>", self._on_select)

    def _build_detail(self) -> None:
        wrap = ttk.Frame(self.root, padding=(8, 8))
        wrap.pack(side="bottom", fill="x")
        self.detail_var = tk.StringVar(
            value="血量留空点「推算」：查看该 Boss 各倍率下的属性范围；输入血量：锁定倍率与档位。")
        lbl = ttk.Label(wrap, textvariable=self.detail_var, foreground="#333",
                        justify="left", anchor="w", wraplength=1140)
        lbl.pack(fill="x")

    # ---------- 交互 ----------
    def _on_name_key(self, _evt=None) -> None:
        val = self.name_var.get().strip()
        if not val:
            self.name_combo["values"] = self._all_names
            return
        hits = self.est.fuzzy(val)
        self.name_combo["values"] = hits or [val]

    def _parse_int(self, var: tk.StringVar, label: str, default: int | None = None) -> int | None:
        s = var.get().strip()
        if not s:
            if default is None:
                messagebox.showwarning("输入缺失", "请填写%s" % label)
                return None
            return default
        try:
            v = int(s)
        except ValueError:
            messagebox.showwarning("输入错误", "%s必须是整数：%s" % (label, s))
            return None
        if v < 0:
            messagebox.showwarning("输入错误", "%s不能为负" % label)
            return None
        return v

    def _pick_variant(self, name: str, variants) -> object | None:
        """名称命中多个档位变体时弹出选择框；只有一个则直接返回。"""
        if len(variants) == 1:
            return variants[0]
        # 弹出带滚动条的变体选择列表
        top = tk.Toplevel(self.root)
        top.title("选择「%s」的档位变体" % name)
        top.geometry("480x340")
        top.transient(self.root)
        top.grab_set()

        ttk.Label(top, text="同名 %d 个变体（档位总和越接近满档越可能）：" % len(variants),
                  padding=8).pack(fill="x")

        frame = ttk.Frame(top, padding=(8, 0))
        frame.pack(fill="both", expand=True)
        tree = ttk.Treeview(frame, columns=("bases", "sum"), show="headings", height=10)
        tree.heading("bases", text="档位（体/力/强/速/魔）")
        tree.heading("sum", text="总和")
        tree.column("bases", width=220, anchor="center")
        tree.column("sum", width=80, anchor="center")
        vsb = ttk.Scrollbar(frame, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=vsb.set)
        tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")

        for r in variants:
            b = self.est.bases_of(r)
            tree.insert("", "end", values=("%d/%d/%d/%d/%d" % tuple(b), sum(b)))

        sel = {"rank": None}

        def on_ok() -> None:
            s = tree.selection()
            if not s:
                messagebox.showwarning("未选择", "请选择一个档位变体。", parent=top)
                return
            idx = tree.index(s[0])
            sel["rank"] = variants[idx]
            top.destroy()

        def on_double(_e) -> None:
            on_ok()

        tree.bind("<Double-1>", on_double)
        btn = ttk.Button(top, text="确定", command=on_ok)
        btn.pack(pady=8)

        self.root.wait_window(top)
        return sel["rank"]

    def _run(self) -> None:
        if not self.est.table_count:
            messagebox.showerror("档位表缺失", self.est.load_error or "未找到 pet_rank.bin")
            return
        name = self.name_var.get().strip()
        variants = self.est.lookup(name)
        if not variants:
            hits = self.est.fuzzy(name)
            if hits:
                messagebox.showwarning("名称未命中", "表中没有「%s」。相近名称：\n%s" % (name, "、".join(hits[:12])))
            else:
                messagebox.showwarning("名称未命中", "表中没有「%s」，请检查名称。" % name)
            return

        rank = self._pick_variant(name, variants)
        if rank is None:
            return

        level = self._parse_int(self.level_var, "等级", 1)
        if level is None:
            return
        hp_text = self.hp_var.get().strip()
        if not hp_text:
            # 不输入血量 → 范围模式：直接查看各倍率下属性范围
            self._run_range(rank, level)
            return
        obs_hp = self._parse_int(self.hp_var, "血量")
        if obs_hp is None:
            return
        mp_text = self.mp_var.get().strip()
        obs_mp = self._parse_int(self.mp_var, "蓝量") if mp_text else None
        if mp_text and obs_mp is None:
            return

        self.schemes = self.est.enumerate_schemes(rank, level, obs_hp, obs_mp, top=12)
        # 记录观测上下文，供点击详情时再次计算
        self._ctx = (rank, level, obs_hp, obs_mp)
        self._rows = []  # [(rate, "scheme", DropScheme) | (rate, "range", dict), ...] 与表格行一一对应

        self.tree.delete(*self.tree.get_children())
        if not self.schemes:
            self.detail_var.set("没有找到任何可解释的倍率方案。")
            return

        bases = self.est.bases_of(rank)
        # 每个候选倍率取最可能的 top 档位，扁平化展示（无需二次选择）
        flat: list[tuple[int, object, list[int]]] = []  # (rate, DropScheme, drops)
        for s in self.schemes:
            drops = self.est.enumerate_drops_at_rate(bases, level, s.rate, obs_hp, obs_mp, top=4)
            for d in drops:
                flat.append((s.rate, d, d.drops))

        if not flat:
            self.detail_var.set("没有找到可解释的档位。")
            return

        rank_no = 0
        for rate, d, drops in flat:
            rank_no += 1
            seven = d.seven
            rng = d.ranges
            self.tree.insert("", "end", values=(
                rank_no,
                rate,
                "档位",
                "%d/%d/%d/%d/%d" % tuple(drops),
                d.hp,
                d.mp,
                "%d (%d-%d)" % (seven[2], *rng["atk"]),
                "%d (%d-%d)" % (seven[3], *rng["def"]),
                "%d (%d-%d)" % (seven[4], *rng["agi"]),
                "%d (%d-%d)" % (seven[5], *rng["spirit"]),
                "%d (%d-%d)" % (seven[6], *rng["rec"]),
            ))
            self._rows.append((rate, "scheme", d))

        self.status_var.set("「%s」等级 %d 血量 %d：%d 个倍率方案，%d 组档位候选" % (
            rank.name, level, obs_hp, len(self.schemes), len(flat)))
        self._show_detail(0)

    def _run_range(self, rank, level) -> None:
        """无血量观测：按倍率列出七维属性范围。"""
        self._ctx = (rank, level, None, None)
        self._rows = []
        self.tree.delete(*self.tree.get_children())

        range_rows = self.est.enumerate_rate_ranges(rank, level, rates=self.est.COMMON_RATES)
        if not range_rows:
            self.detail_var.set("档位表数据无法计算属性范围。")
            return

        rank_no = 0
        for r in range_rows:
            rank_no += 1
            self.tree.insert("", "end", values=(
                rank_no,
                r["rate"],
                "范围",
                "-",
                "%d-%d" % r["hp"],
                "%d-%d" % r["mp"],
                "%d-%d" % r["atk"],
                "%d-%d" % r["def"],
                "%d-%d" % r["agi"],
                "%d-%d" % r["spirit"],
                "%d-%d" % r["rec"],
            ))
            self._rows.append((r["rate"], "range", r))

        agg = self.est.aggregate_ranges(rank, level)
        self.tree.insert("", "end", values=(
            "全", "任意", "绝对范围", "-",
            "%d-%d" % agg["hp"], "%d-%d" % agg["mp"],
            "%d-%d" % agg["atk"], "%d-%d" % agg["def"],
            "%d-%d" % agg["agi"], "%d-%d" % agg["spirit"],
            "%d-%d" % agg["rec"],
        ))
        self._rows.append(("全部倍率 20~640", "range", {"rate": "全部倍率 20~640", **agg}))

        self.status_var.set("「%s」等级 %d（未输入血量）：常见倍率 100/50/20 属性范围，末行为全倍率绝对范围。" % (
            rank.name, level))
        self._show_detail(0)

    def _on_select(self, _evt=None) -> None:
        sel = self.tree.selection()
        if not sel:
            return
        idx = self.tree.index(sel[0])
        self._show_detail(idx)

    def _show_detail(self, idx: int) -> None:
        rows = getattr(self, "_rows", None)
        if not rows or idx < 0 or idx >= len(rows):
            return
        rate, kind, d = rows[idx]
        if kind == "range":
            hp_lo, hp_hi = d["hp"]
            mp_lo, mp_hi = d["mp"]
            atk_lo, atk_hi = d["atk"]
            def_lo, def_hi = d["def"]
            agi_lo, agi_hi = d["agi"]
            spi_lo, spi_hi = d["spirit"]
            rec_lo, rec_hi = d["rec"]
            lines = [
                "倍率 %s —— 未输入血量，仅按档位表给出的属性范围" % rate,
                "HP=%d~%d   MP=%d~%d" % (hp_lo, hp_hi, mp_lo, mp_hi),
                "攻击=%d~%d  防御=%d~%d  敏捷=%d~%d" % (atk_lo, atk_hi, def_lo, def_hi, agi_lo, agi_hi),
                "精神=%d~%d  回复=%d~%d" % (spi_lo, spi_hi, rec_lo, rec_hi),
                "范围覆盖满档~掉20档、随机档极端、成长系数极端的全部可能。",
                "输入实测血量（蓝量可选）点「推算」，可锁定具体倍率与档位。",
            ]
            self.detail_var.set("\n".join(lines))
            return
        rng = d.ranges
        # pen 很小（≤3）视为高置信：受取整影响，完美命中不一定存在
        high_conf = d.pen <= 3
        lines = [
            "倍率 rate=%d    档位=体%d/力%d/强%d/速%d/魔%d    偏差=%d" % (
                rate, *d.drops, d.pen),
            "估计 HP=%d  MP=%d    攻击=%d[%d-%d] 防御=%d[%d-%d] 敏捷=%d[%d-%d]" % (
                d.hp, d.mp,
                d.seven[2], *rng["atk"],
                d.seven[3], *rng["def"],
                d.seven[4], *rng["agi"],
            ),
            "精神=%d[%d-%d] 回复=%d[%d-%d]" % (
                d.seven[5], *rng["spirit"],
                d.seven[6], *rng["rec"],
            ),
        ]
        if high_conf:
            lines.append("高置信匹配（偏差≤3，受整数取整影响）。")
        else:
            lines.append("该档位为最近似匹配（未精确命中，可能需微调倍率/档位）。")
        # 同倍率下的其它档位提示
        same = [r for r, _, dr in rows if r == rate]
        if len(same) > 1:
            lines.append("倍率 %d 下共列出 %d 组档位候选，请结合实战更多观测（蓝量/攻防）缩小范围。" % (rate, len(same)))
        self.detail_var.set("\n".join(lines))


def main() -> None:
    root = tk.Tk()
    try:
        style = ttk.Style()
        if "vista" in style.theme_names():
            style.theme_use("vista")
    except tk.TclError:
        pass
    BossStatApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
