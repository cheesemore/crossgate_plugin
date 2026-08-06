#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 傻瓜补丁（融合版，百科助手面板）。

九动版已无限期停发，发布只产融合版（对历史九动版包仍兼容识别）。

界面外层选项：「战斗加速」（开→战斗倍速+心跳回传1.5x；关→原速+心跳回传1.0x）
与「跳帧」（切后台/老板键限帧 30FPS）。抓宠/烧卡等在游戏内百科助手面板切换。

用法：
  傻瓜补丁_*.exe
  傻瓜补丁_*.exe --auto
  傻瓜补丁_*.exe --auto --no-accel [--no-frameskip]
"""
from __future__ import annotations

import os
import subprocess
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
    # 九动版已无限期停发；仅保留对历史发布包（含九动版.flag）的兼容识别。
    return _has_flag("九动版.flag", "NINE_PACK")


# 九动版已停发：发布包只产融合版；本处仅对历史九动版包保持兼容。
NINE_PACK = _detect_nine_pack()


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


def _panel_modes_tip() -> str:
    if NINE_PACK:
        return "常规 / 九动 / 抓宠 / 抓宠卖银币 / 烧卡"
    return "常规 / 抓宠（无宠二动）/ 抓宠 / 抓宠卖银币 / 烧卡 / 计数挂机 / 采集自动提取"


def run_auto() -> int:
    try:
        # 加速默认关（2026-08 起：战斗倍速默认连带掐断倍速检测上报，默认不打）
        apply_accel = any(
            a in ("--accel", "--speed", "/accel") for a in sys.argv[1:]
        )
        apply_frameskip = not any(
            a in ("--no-frameskip", "--no-bossfps", "/no-frameskip") for a in sys.argv[1:]
        )
        msgs = run_foolproof_patch(
            enable_nine=NINE_PACK,
            apply_accel=apply_accel,
            apply_frameskip=apply_frameskip,
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
        self.geometry("820x560")
        self.minsize(680, 460)

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
        tip = (
            f"本包：傻瓜补丁·{pack_name}\n"
            f"· 侧栏「百科」→ 助手面板，战斗模式：{_panel_modes_tip()}\n"
            "· 外层选项：「战斗加速」（开→战斗倍速+心跳回传1.5x；关→原速+心跳回传1.0x）\n"
            "             与「跳帧」（切后台/老板键限帧 30FPS）\n"
            "· 采集自动提取：战斗页独立开关（满999格提入账号银行，每格最多重试1次）\n"
            "· 脚本页「立刻提取采集物」可手动触发一次\n"
            "· 分享改日常、礼包码默认带上\n"
            "· 「启动动画预览」使用上方游戏目录读取资源（需已填对目录）\n"
            "若提示客户端不干净：可点「从干净目录恢复…」。"
        )
        ttk.Label(body, text=tip, justify=tk.LEFT).pack(anchor=tk.W, pady=(10, 8))

        self.apply_accel_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            body,
            text="战斗加速（默认关：3x 倍速+心跳回传1.5x；开启=倍速，会连带掐断倍速检测上报）",
            variable=self.apply_accel_var,
        ).pack(anchor=tk.W, pady=(0, 6))

        self.apply_frameskip_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            body,
            text="跳帧（默认开：切后台/老板键隐藏时限帧 30FPS；关闭=维持前台帧率）",
            variable=self.apply_frameskip_var,
        ).pack(anchor=tk.W, pady=(0, 6))

        ttk.Label(body, text="礼包码（一行一个，可改）", foreground="#555555").pack(
            anchor=tk.W
        )
        self.gift_codes_box = scrolledtext.ScrolledText(body, height=4, width=36, wrap=tk.WORD)
        self.gift_codes_box.pack(anchor=tk.W, pady=(2, 8), fill=tk.X)
        self.gift_codes_box.insert("1.0", "\n".join(DEFAULT_GIFT_CODES))

        btns = ttk.Frame(body)
        btns.pack(fill=tk.X, pady=(0, 8))
        self.apply_btn = ttk.Button(btns, text="一键打补丁", command=self.on_apply)
        self.apply_btn.pack(side=tk.LEFT)
        self.animator_btn = ttk.Button(
            btns, text="启动动画预览", command=self.on_launch_animator
        )
        self.animator_btn.pack(side=tk.LEFT, padx=(8, 0))
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
        self.animator_btn.state(state)

    def _animator_candidates(self) -> list[Path]:
        cands: list[Path] = []
        if getattr(sys, "frozen", False):
            exe_dir = Path(sys.executable).resolve().parent
            cands.extend(
                [
                    exe_dir / "animator" / "pet_appear_gui.py",
                    exe_dir / "tools" / "pet_appear_gui.py",
                ]
            )
            meipass = getattr(sys, "_MEIPASS", None)
            if meipass:
                cands.append(Path(meipass) / "animator" / "pet_appear_gui.py")
        else:
            scripts = Path(__file__).resolve().parent
            cands.append(scripts.parent.parent / "tools" / "pet_appear_gui.py")
        game = self._game_root()
        if game is not None:
            cands.append(Path(game) / "tools" / "pet_appear_gui.py")
        out: list[Path] = []
        seen: set[str] = set()
        for p in cands:
            try:
                key = str(p.resolve())
            except OSError:
                continue
            if key in seen or not p.is_file():
                continue
            seen.add(key)
            out.append(p)
        return out

    def on_launch_animator(self) -> None:
        if self._busy:
            return
        game = self._game_root()
        env = os.environ.copy()
        if game is not None:
            env["SEQCHAPTER_ROOT"] = str(Path(game).resolve())

        if game is None or not Path(game).is_dir():
            messagebox.showerror(
                f"{_profile_title()} — 失败",
                "请先填写正确的游戏目录（动画预览依赖该目录资源）。",
            )
            return

        if getattr(sys, "frozen", False):
            try:
                subprocess.Popen(
                    [sys.executable, "--run-animator"],
                    env=env,
                    cwd=str(Path(sys.executable).resolve().parent),
                )
                self._append(
                    "已启动动画预览（独立进程；资源目录="
                    + str(Path(game).resolve())
                    + "）"
                )
            except Exception as exc:
                messagebox.showerror(
                    f"{_profile_title()} — 失败", f"无法启动动画预览：\n{exc}"
                )
            return

        cands = self._animator_candidates()
        if not cands:
            messagebox.showerror(
                f"{_profile_title()} — 失败",
                "找不到动画预览脚本 pet_appear_gui.py。",
            )
            return
        script = cands[0]
        try:
            subprocess.Popen(
                [sys.executable, str(script)],
                env=env,
                cwd=str(script.parent),
            )
            self._append(
                f"已启动动画预览：{script}（资源目录={Path(game).resolve()}）"
            )
        except Exception as exc:
            messagebox.showerror(
                f"{_profile_title()} — 失败", f"无法启动动画预览：\n{exc}"
            )

    def on_restore_clean(self) -> None:
        if self._busy:
            return
        game_root = self._game_root()
        if game_root is None:
            messagebox.showerror(f"{_profile_title()} — 失败", "请先填写游戏目录。")
            return
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
            + "\n\n是否现在选择一份干净客户端目录，恢复 hotfix 后再打补丁？",
        ):
            return
        clean = filedialog.askdirectory(
            title="选择干净客户端目录（含 cg37.exe；勿选当前游戏目录）"
        )
        if not clean:
            return
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
        game_root = self._game_root()

        def work() -> None:
            try:
                msgs = run_foolproof_patch(
                    game_root,
                    enable_nine=NINE_PACK,
                    gift_codes=self.gift_codes_box.get("1.0", "end"),
                    apply_accel=bool(self.apply_accel_var.get()),
                    apply_frameskip=bool(self.apply_frameskip_var.get()),
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


def _run_animator_entrypoint() -> int:
    bases: list[Path] = []
    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            bases.extend([Path(meipass) / "tools", Path(meipass) / "animator", Path(meipass)])
        bases.extend([exe_dir / "tools", exe_dir, exe_dir / "animator"])
    else:
        bases.append(Path(__file__).resolve().parent.parent.parent / "tools")
    for b in bases:
        if b.is_dir() and str(b) not in sys.path:
            sys.path.insert(0, str(b))
    try:
        from pet_appear_gui import main as anim_main  # type: ignore
    except Exception as exc:
        show_popup(
            _profile_title() + " — 动画预览",
            f"无法加载动画预览：\n{exc}",
            error=True,
        )
        return 1
    anim_main()
    return 0


def main() -> int:
    argv = sys.argv[1:]
    if any(a in ("--run-animator", "--animator", "/run-animator") for a in argv):
        return _run_animator_entrypoint()
    if "--auto" in argv or "/auto" in argv:
        return run_auto()
    app = FoolproofApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
