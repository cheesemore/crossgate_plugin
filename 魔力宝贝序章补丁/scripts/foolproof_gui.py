#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 傻瓜补丁（两包）。

发布物：
  傻瓜补丁_九动版   → 九动加速 / 无九动加速 / 抓宠 / 烧卡 / 慢速烧卡
  傻瓜补丁_融合版   → 普通加速 / 抓宠 / 烧卡 / 慢速烧卡

用法：
  傻瓜补丁_*.exe              打开界面选模式
  傻瓜补丁_*.exe --auto --accel
  傻瓜补丁_九动版.exe --auto --accel-no-nine
  傻瓜补丁_*.exe --auto --burn-seal
  傻瓜补丁_*.exe --auto --burn-seal-slow
  傻瓜补丁_*.exe --auto --auto-catch
  傻瓜补丁_*.exe --auto --auto-catch-nopet

拒绝自愈时，界面可选手选干净目录恢复 hotfix（无默认源）后再打。
"""
from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

from apply_combo_patch import DEFAULT_GIFT_CODES
from foolproof_apply import (
    FoolproofError,
    is_unclean_client_error,
    resolve_game_root,
    restore_hotfixdata_from_clean,
    run_foolproof_patch,
)
from patch_common import EXPECTED_SIZE, detect_game_root_from_launcher, get_game_root


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


def _detect_nine_pack() -> bool:
    if any(a in ("--nine-pack", "--with-nine-pack", "/nine-pack") for a in sys.argv[1:]):
        return True
    return _has_flag("九动版.flag", "NINE_PACK")


def _detect_fusion_pack() -> bool:
    if any(
        a in ("--fusion-pack", "--fusion", "--seal-catch", "--burn-or-catch", "/fusion-pack")
        for a in sys.argv[1:]
    ):
        return True
    return _has_flag(
        "融合版.flag",
        "FUSION_PACK",
        "烧卡抓宠.flag",
        "SEAL_CATCH",
        "烧卡或抓宠.flag",
    )


# 两包：有九动版旗标 → 九动版；否则融合版（开发无旗标也当融合）
NINE_PACK = _detect_nine_pack()
FUSION_PACK = not NINE_PACK
ACCEL_LABEL = "九动加速" if NINE_PACK else "普通加速"


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


def _profile_title() -> str:
    if NINE_PACK:
        return "傻瓜补丁（九动版）"
    return "傻瓜补丁（融合版）"


def _argv_mode() -> str | None:
    """命令行模式：accel / accel_no_nine / burn / burn_slow / catch / catch_nopet。"""
    argv = sys.argv[1:]
    if any(a in ("--burn-seal-slow", "--slow-burn-seal", "--auto-burn-slow", "/burn-seal-slow") for a in argv):
        return "burn_slow"
    if any(a in ("--burn-seal", "--burn-seal-cards", "--auto-burn", "/burn-seal") for a in argv):
        return "burn"
    if any(
        a in ("--auto-catch-nopet", "--catch-nopet", "--auto-catch-no-pet", "/auto-catch-nopet")
        for a in argv
    ):
        return "catch_nopet"
    if any(a in ("--auto-catch", "--catch-pet", "/auto-catch") for a in argv):
        return "catch"
    if any(
        a in ("--accel-no-nine", "--no-nine-accel", "--normal-accel", "/accel-no-nine")
        for a in argv
    ):
        return "accel_no_nine"
    if any(a in ("--accel", "--normal", "--nine-accel", "/accel") for a in argv):
        return "accel"
    return None


def _mode_to_flags(mode: str) -> tuple[bool, bool, bool, bool, bool]:
    """enable_nine, burn, burn_slow, catch, catch_nopet。"""
    if mode == "accel":
        return NINE_PACK, False, False, False, False
    if mode == "accel_no_nine":
        return False, False, False, False, False
    if mode == "burn":
        return False, True, False, False, False
    if mode == "burn_slow":
        return False, False, True, False, False
    if mode == "catch":
        return False, False, False, True, False
    if mode == "catch_nopet":
        return False, False, False, False, True
    raise FoolproofError("请选择一种模式（只能打一个）。")


def run_auto() -> int:
    mode = _argv_mode()
    if mode is None:
        lines = [
            "请打开界面选择模式，或指定其一：",
            "  --auto --accel",
        ]
        if NINE_PACK:
            lines.append("  --auto --accel-no-nine")
        lines.extend(
            [
                "  --auto --auto-catch",
                "  --auto --auto-catch-nopet",
                "  --auto --burn-seal",
                "  --auto --burn-seal-slow",
                "可选：--no-daily（不打日常切页） --no-gift（不打新手礼包码切页）",
            ]
        )
        show_popup(f"{_profile_title()} — 失败", "\n".join(lines), error=True)
        return 1
    try:
        enable_nine, burn, burn_slow, catch, catch_nopet = _mode_to_flags(mode)
        daily = not any(
            a in ("--no-daily", "--no-share-daily", "/no-daily") for a in sys.argv[1:]
        )
        gift = not any(
            a in ("--no-gift", "--no-newbie-gift", "/no-gift") for a in sys.argv[1:]
        )
        msgs = run_foolproof_patch(
            enable_nine=enable_nine,
            burn_seal=burn,
            burn_seal_slow=burn_slow,
            auto_catch=catch,
            auto_catch_nopet=catch_nopet,
            daily_claim=daily,
            newbie_gift_code=gift,
            on_log=lambda line: print(line, flush=True),
        )
        detail = "\n".join(msgs[-8:]) if msgs else "补丁已打好。"
        show_popup(f"{_profile_title()} — 成功", f"补丁已打好。\n请启动游戏验证。\n\n{detail}")
        return 0
    except FoolproofError as exc:
        hint = ""
        if is_unclean_client_error(exc):
            hint = "\n\n也可打开界面，选择从干净目录恢复后再打。"
        show_popup(f"{_profile_title()} — 失败", str(exc) + hint, error=True)
        return 1
    except Exception as exc:
        show_popup(f"{_profile_title()} — 失败", str(exc), error=True)
        return 1


class FoolproofApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(_profile_title())
        self.geometry("920x640")
        self.minsize(780, 500)

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

        pack_name = "九动版" if NINE_PACK else "融合版"
        choice_n = "六选一" if NINE_PACK else "五选一"
        tip = (
            f"本包：傻瓜补丁·{pack_name}（{choice_n}，只能打一种）\n"
            f"· {ACCEL_LABEL}：VIP/非VIP 5x · 特效 2x · 跑速快 · 长按 · 一级含蝙蝠/哥布林"
            + (" · 神奇九动·DLL" if NINE_PACK else " · 无九动")
            + "\n"
        )
        if NINE_PACK:
            tip += "· 无九动加速：同上加速组合，但不打九动\n"
        tip += (
            "· 分享改日常（可选）：勾选后侧栏「分享」→ 每日签到/领月卡每日/在线礼包可领档，并使用水晶碎片袋·高级水晶石·声望之花·生命之华·魔法结晶·高级声望勋章·时间水晶·工时小闹钟（间隔0.4s）；不占百科\n"
            "· 客服→高级自动战斗：各模式默认带上（侧栏客服开自动技能设置；官方入口太深）\n"
            "· 自动抓宠：点百科 Tip；战斗 5x · 特效 2x；标题「★自动中★…」；有宠时宠防御\n"
            "· 自动抓宠（无宠人防御）：同上；无宠时 2动人物防御，1动仍 P2 放技能/其余防御\n"
            "· 自动烧卡：点百科 Tip；战斗 10x · 特效 5x；标题「★自动烧卡中★」\n"
            "· 慢速烧卡：烧卡逻辑同上，但无任何加速\n"
            "抓宠/烧卡/慢速烧卡 均不含九动（与加速模式互斥）。不含加速过场、助手桥接。\n"
            "若提示客户端不干净：可点「从干净目录恢复…」（需你手动选目录，无默认源）。"
        )
        ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 6))

        self.mode_var = tk.StringVar(value="accel")
        mode_row = ttk.Frame(body)
        mode_row.pack(anchor=tk.W, pady=(0, 8))
        ttk.Label(mode_row, text="选择补丁：").pack(side=tk.LEFT)
        mode_choices: list[tuple[str, str]] = [("accel", ACCEL_LABEL)]
        if NINE_PACK:
            mode_choices.append(("accel_no_nine", "无九动加速"))
        mode_choices.extend(
            (
                ("catch", "自动抓宠"),
                ("catch_nopet", "自动抓宠（无宠人防御）"),
                ("burn", "自动烧卡"),
                ("burn_slow", "慢速烧卡"),
            )
        )
        for value, text in mode_choices:
            ttk.Radiobutton(
                mode_row,
                text=text,
                variable=self.mode_var,
                value=value,
            ).pack(side=tk.LEFT, padx=(8, 0))

        self.daily_claim_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            body,
            text="分享改日常（默认开；分享切页·日常领取）",
            variable=self.daily_claim_var,
        ).pack(anchor=tk.W, pady=(0, 2))
        self.newbie_gift_code_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            body,
            text="新手礼包码领取（默认开；与日常同分享切页；最多5角色）",
            variable=self.newbie_gift_code_var,
        ).pack(anchor=tk.W, pady=(0, 2))
        ttk.Label(
            body,
            text="礼包码（一行一个，可改）",
            foreground="#555555",
        ).pack(anchor=tk.W, padx=(18, 0))
        self.gift_codes_box = scrolledtext.ScrolledText(body, height=4, width=36, wrap=tk.WORD)
        self.gift_codes_box.pack(anchor=tk.W, padx=(18, 0), pady=(2, 8), fill=tk.X)
        self.gift_codes_box.insert("1.0", "\n".join(DEFAULT_GIFT_CODES))

        btns = ttk.Frame(body)
        btns.pack(fill=tk.X, pady=(0, 8))
        self.apply_btn = ttk.Button(btns, text="一键打补丁", command=self.on_apply)
        self.apply_btn.pack(side=tk.LEFT)
        self.restore_btn = ttk.Button(
            btns,
            text="从干净目录恢复…",
            command=self.on_restore_clean,
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

    def on_restore_clean(self) -> None:
        if self._busy:
            return
        game_root = self._game_root()
        if game_root is None:
            messagebox.showerror(f"{_profile_title()} — 失败", "请先填写游戏目录。")
            return
        # 不设默认源：必须用户自选
        clean = filedialog.askdirectory(
            title="选择干净客户端目录（含 cg37.exe；勿选当前游戏目录）"
        )
        if not clean:
            return

        self._set_busy(True)
        self.log.delete("1.0", tk.END)

        def work() -> None:
            try:
                msgs = restore_hotfixdata_from_clean(
                    Path(clean),
                    game_root,
                    on_log=lambda line: self.after(0, self._append, line),
                )
                self.after(
                    0,
                    lambda: messagebox.showinfo(
                        f"{_profile_title()} — 恢复成功",
                        "已从干净目录恢复 hotfix。\n可继续点「一键打补丁」。\n\n"
                        + "\n".join(msgs[-6:]),
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
                self.after(0, lambda: self._set_busy(False))

        threading.Thread(target=work, daemon=True).start()

    def _offer_restore_then_retry(self, err: FoolproofError) -> None:
        if not is_unclean_client_error(err):
            messagebox.showerror(f"{_profile_title()} — 失败", str(err))
            return
        if not messagebox.askyesno(
            f"{_profile_title()} — 客户端不干净",
            str(err)
            + "\n\n是否现在选择一份干净客户端目录，恢复 hotfix 后再打补丁？\n"
            "（不会预填路径，需你手动选择）",
        ):
            return
        clean = filedialog.askdirectory(
            title="选择干净客户端目录（含 cg37.exe；勿选当前游戏目录）"
        )
        if not clean:
            return
        game_root = self._game_root()
        self._set_busy(True)

        def work() -> None:
            try:
                root = self._game_root()
                if root is None:
                    raise FoolproofError("请先填写游戏目录。")
                restore_hotfixdata_from_clean(
                    Path(clean),
                    root,
                    on_log=lambda line: self.after(0, self._append, line),
                )
                self.after(0, self._append, "恢复完成，重新打补丁…")
                self.after(0, self._apply_core)
            except Exception as exc:
                self.after(0, self._append, str(exc))
                self.after(
                    0,
                    lambda: messagebox.showerror(f"{_profile_title()} — 失败", str(exc)),
                )
                self.after(0, lambda: self._set_busy(False))

        threading.Thread(target=work, daemon=True).start()

    def _apply_core(self) -> None:
        """在工作线程或恢复后调用；结束后会解除 busy。"""
        game_root = self._game_root()
        try:
            enable_nine, burn, burn_slow, catch, catch_nopet = _mode_to_flags(self.mode_var.get())
        except FoolproofError as exc:
            messagebox.showerror(f"{_profile_title()} — 失败", str(exc))
            self._set_busy(False)
            return

        def work() -> None:
            try:
                msgs = run_foolproof_patch(
                    game_root,
                    enable_nine=enable_nine,
                    burn_seal=burn,
                    burn_seal_slow=burn_slow,
                    auto_catch=catch,
                    auto_catch_nopet=catch_nopet,
                    daily_claim=bool(self.daily_claim_var.get()),
                    newbie_gift_code=bool(self.newbie_gift_code_var.get()),
                    gift_codes=self.gift_codes_box.get("1.0", "end"),
                    on_log=lambda line: self.after(0, self._append, line),
                )
                self.after(
                    0,
                    lambda: messagebox.showinfo(
                        f"{_profile_title()} — 成功",
                        "补丁已打好。\n请启动游戏验证。\n\n" + "\n".join(msgs[-6:]),
                    ),
                )
                self.after(0, lambda: self._set_busy(False))
            except FoolproofError as exc:
                self.after(0, self._append, str(exc))

                def on_err() -> None:
                    self._set_busy(False)
                    self._offer_restore_then_retry(exc)

                self.after(0, on_err)
            except Exception as exc:
                self.after(0, self._append, str(exc))
                self.after(
                    0,
                    lambda: messagebox.showerror(f"{_profile_title()} — 失败", str(exc)),
                )
                self.after(0, lambda: self._set_busy(False))

        threading.Thread(target=work, daemon=True).start()

    def on_apply(self) -> None:
        if self._busy:
            return
        self._set_busy(True)
        self.log.delete("1.0", tk.END)
        self._apply_core()


def main() -> int:
    if "--auto" in sys.argv[1:] or "/auto" in sys.argv[1:]:
        return run_auto()
    app = FoolproofApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
