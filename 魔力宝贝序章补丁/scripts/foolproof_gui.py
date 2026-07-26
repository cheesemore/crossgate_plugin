#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 傻瓜补丁（一键固定组合）。

用法：
  傻瓜补丁.exe                打开界面
  傻瓜补丁.exe --auto         无界面自动打补丁（供 一键打补丁.bat）
  傻瓜补丁.exe --auto --burn-seal    仅自动烧卡（兼容旧包）
  傻瓜补丁.exe --auto --auto-catch   仅自动抓宠（兼容旧包）
  傻瓜补丁.exe --auto --no-nine      无九动
烧卡/抓宠合一包（烧卡抓宠.flag）：界面二选一，只能打一种。
"""
from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

from foolproof_apply import FoolproofError, resolve_game_root, run_foolproof_patch
from patch_common import DATA_DIR, EXPECTED_SIZE, detect_game_root_from_launcher, get_game_root


def _flag_bases() -> list[Path]:
    bases: list[Path] = []
    if getattr(sys, "frozen", False):
        bases.append(Path(sys.executable).resolve().parent)
    bases.append(Path(__file__).resolve().parent)
    return bases


def _has_flag(*names: str) -> bool:
    for base in _flag_bases():
        for name in names:
            if (base / name).is_file():
                return True
    return False


def _detect_seal_catch_choice() -> bool:
    """烧卡/抓宠合一包：界面可选其一。"""
    if any(
        a in ("--seal-catch", "--burn-or-catch", "/seal-catch")
        for a in sys.argv[1:]
    ):
        return True
    return _has_flag("烧卡抓宠.flag", "SEAL_CATCH", "烧卡或抓宠.flag")


def _detect_burn_seal() -> bool:
    if any(
        a in ("--burn-seal", "--burn-seal-cards", "--auto-burn", "/burn-seal")
        for a in sys.argv[1:]
    ):
        return True
    return _has_flag("烧封印.flag", "BURN_SEAL", "自动烧卡.flag")


def _detect_auto_catch() -> bool:
    if any(
        a in (
            "--auto-catch",
            "--catch-pet",
            "--pet-catch",
            "/auto-catch",
            "/catch-pet",
        )
        for a in sys.argv[1:]
    ):
        return True
    return _has_flag(
        "自动抓宠.flag",
        "AUTO_CATCH",
        "捉宠.flag",
        "CATCH_PET",
    )


def _detect_no_nine() -> bool:
    if any(a in ("--no-nine", "--without-nine", "/no-nine") for a in sys.argv[1:]):
        return True
    return _has_flag("无九动.flag", "NO_NINE")


SEAL_CATCH_CHOICE = _detect_seal_catch_choice()
# 合一包优先；旧独占 flag 仍可用
BURN_SEAL = (not SEAL_CATCH_CHOICE) and _detect_burn_seal()
AUTO_CATCH = (not SEAL_CATCH_CHOICE) and _detect_auto_catch()
NO_NINE = (
    SEAL_CATCH_CHOICE
    or BURN_SEAL
    or AUTO_CATCH
    or _detect_no_nine()
)


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


def _profile_title() -> str:
    if SEAL_CATCH_CHOICE:
        return "傻瓜补丁（烧卡/抓宠）"
    if BURN_SEAL:
        return "傻瓜补丁（自动烧卡）"
    if AUTO_CATCH:
        return "傻瓜补丁（自动抓宠）"
    if NO_NINE:
        return "傻瓜补丁（无九动）"
    return "傻瓜补丁"


def run_auto() -> int:
    """命令行/ bat 一键：自动找游戏目录 → 打补丁 → 弹窗。"""
    if SEAL_CATCH_CHOICE:
        # 合一包必须指定其一，否则请开界面选
        argv = sys.argv[1:]
        burn = any(a in ("--burn-seal", "--auto-burn", "/burn-seal") for a in argv)
        catch = any(
            a in ("--auto-catch", "--catch-pet", "/auto-catch") for a in argv
        )
        if burn and catch:
            show_popup(
                f"{_profile_title()} — 失败",
                "不能同时指定自动烧卡与自动抓宠，请只选一个。",
                error=True,
            )
            return 1
        if not burn and not catch:
            show_popup(
                f"{_profile_title()} — 失败",
                "烧卡/抓宠合一包请打开界面选择，或：\n"
                "  --auto --burn-seal\n"
                "  --auto --auto-catch",
                error=True,
            )
            return 1
        burn_seal, auto_catch = burn, catch
    else:
        burn_seal, auto_catch = BURN_SEAL, AUTO_CATCH

    try:
        msgs = run_foolproof_patch(
            enable_nine=not NO_NINE,
            burn_seal=burn_seal,
            auto_catch=auto_catch,
            on_log=lambda line: print(line, flush=True),
        )
        detail = "\n".join(msgs[-8:]) if msgs else "补丁已打好。"
        show_popup(f"{_profile_title()} — 成功", f"补丁已打好。\n请启动游戏验证。\n\n{detail}")
        return 0
    except FoolproofError as exc:
        show_popup(f"{_profile_title()} — 失败", str(exc), error=True)
        return 1
    except Exception as exc:
        show_popup(f"{_profile_title()} — 失败", str(exc), error=True)
        return 1


class FoolproofApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(_profile_title())
        self.geometry("640x520")
        self.minsize(560, 420)

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

        self.mode_var = tk.StringVar(value="")
        if SEAL_CATCH_CHOICE:
            tip = (
                "固定组合（烧卡 / 抓宠 二选一）：\n"
                "共同：VIP/非VIP 5x · 特效 2x · 一级含蝙蝠/哥布林 · 自动技能 · 跑速快 · 长按详情 · 无九动\n"
                "· 自动烧卡：点百科 Tip 开关；开启后非 VIP 自动战有卡就扔\n"
                "· 自动封印（抓宠）：点百科 Tip「自动封印已开启/关闭」；一级非迷你蝙蝠按 P1/P2 分工\n"
                "只能打一种（共用侧栏百科开关）。不含：神奇九动、加速过场、助手桥接"
            )
            ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))
            mode_row = ttk.Frame(body)
            mode_row.pack(anchor=tk.W, pady=(0, 8))
            ttk.Label(mode_row, text="选择补丁：").pack(side=tk.LEFT)
            ttk.Radiobutton(
                mode_row,
                text="自动烧卡",
                variable=self.mode_var,
                value="burn",
            ).pack(side=tk.LEFT, padx=(8, 12))
            ttk.Radiobutton(
                mode_row,
                text="自动封印",
                variable=self.mode_var,
                value="catch",
            ).pack(side=tk.LEFT)
            self.mode_var.set("burn")
        elif BURN_SEAL:
            tip = (
                "固定组合（自动烧卡）：\n"
                "默认关；点侧栏百科 Tip「自动烧卡已开启」/「自动烧卡已关闭」\n"
                "VIP/非VIP 5x（中档）· 特效 2x · 一级含蝙蝠/哥布林\n"
                "· 自动技能 · 跑速快 · 长按详情\n"
                "不含：神奇九动、加速过场、助手桥接、自动抓宠"
            )
            ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))
        elif AUTO_CATCH:
            tip = (
                "固定组合（自动抓宠）：\n"
                "默认关；点侧栏百科 Tip 开关（开：自动抓宠已开启 / 关：自动战斗已关闭）\n"
                "一级且非迷你蝙蝠：P1 扔封印卡 · P2 一号技能 · 其余人物/宠物防御\n"
                "VIP/非VIP 5x · 特效 2x · 一级含蝙蝠/哥布林 · 无九动"
            )
            ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))
        elif NO_NINE:
            tip = (
                "固定组合（无九动）：\n"
                "VIP/非VIP 5x · 特效 2x · 一级含蝙蝠/哥布林 · 自动技能 · 跑速快 · 长按详情\n"
                "不含：神奇九动、加速过场、助手桥接、自动烧卡/抓宠"
            )
            ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))
        else:
            tip = (
                "固定组合：VIP/非VIP 5x · 神奇九动 · 特效 2x · 一级含蝙蝠/哥布林\n"
                "· 自动技能 · 跑速快 · 长按详情"
            )
            ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))

        btns = ttk.Frame(body)
        btns.pack(fill=tk.X, pady=(0, 8))
        self.apply_btn = ttk.Button(btns, text="一键打补丁", command=self.on_apply)
        self.apply_btn.pack(side=tk.LEFT)
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

    def _resolve_modes(self) -> tuple[bool, bool]:
        if SEAL_CATCH_CHOICE:
            mode = self.mode_var.get()
            if mode == "burn":
                return True, False
            if mode == "catch":
                return False, True
            raise FoolproofError("请选择：自动烧卡 或 自动抓宠（只能打一个）。")
        return BURN_SEAL, AUTO_CATCH

    def on_apply(self) -> None:
        if self._busy:
            return
        raw = self.path_var.get().strip()
        game_root = Path(raw) if raw else None
        try:
            burn_seal, auto_catch = self._resolve_modes()
        except FoolproofError as exc:
            messagebox.showerror(f"{_profile_title()} — 失败", str(exc))
            return

        self._busy = True
        self.apply_btn.state(["disabled"])
        self.log.delete("1.0", tk.END)

        def work() -> None:
            try:
                msgs = run_foolproof_patch(
                    game_root,
                    enable_nine=not NO_NINE,
                    burn_seal=burn_seal,
                    auto_catch=auto_catch,
                    on_log=lambda line: self.after(0, self._append, line),
                )
                self.after(
                    0,
                    lambda: messagebox.showinfo(
                        f"{_profile_title()} — 成功",
                        "补丁已打好。\n请启动游戏验证。\n\n" + "\n".join(msgs[-6:]),
                    ),
                )
            except FoolproofError as exc:
                self.after(0, self._append, str(exc))
                self.after(
                    0,
                    lambda: messagebox.showerror(f"{_profile_title()} — 失败", str(exc)),
                )
            except Exception as exc:
                self.after(0, self._append, str(exc))
                self.after(
                    0,
                    lambda: messagebox.showerror(f"{_profile_title()} — 失败", str(exc)),
                )
            finally:
                def done() -> None:
                    self._busy = False
                    self.apply_btn.state(["!disabled"])

                self.after(0, done)

        threading.Thread(target=work, daemon=True).start()


def main() -> int:
    if "--auto" in sys.argv[1:] or "/auto" in sys.argv[1:]:
        return run_auto()
    app = FoolproofApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
