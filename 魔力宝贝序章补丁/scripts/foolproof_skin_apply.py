#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
傻瓜换装补丁：进战形象钩子 + 百科循环切换 4 套装备。

独立维护。打入后点侧栏「百科」：装备1→2→3→4→1…，无面板、无其它功能。
"""
from __future__ import annotations

import shutil
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path

from foolproof_client_utils import (
    FoolproofError,
    _cg37_running,
    _emit,
    _ensure_clean_baseline,
    is_unclean_client_error,
    resolve_game_root,
    restore_hotfixdata_from_clean,
)
from patch_common import (
    EXPECTED_SIZE,
    ensure_patcher,
    hotfix_orig,
    hotfix_path,
    mark_hotfix_watch_stamp,
    run_patcher_capture,
    set_game_root,
    toolkit_root,
    verify_hotfix,
)

LogFn = Callable[[str], None]

SKIN_PATCHER_NAME = "HotfixPatcherSkinCycle.exe"
APPEAR_DLL = "SeqChapterBattleAppear.dll.bytes"
SKIN_DLL = "SeqChapterWikiSkinCycle.dll.bytes"


def resolve_skin_patcher_exe() -> Path:
    """皮肤补丁专用引擎。"""
    root = toolkit_root()
    meipass = getattr(sys, "_MEIPASS", None)
    ordered: list[Path] = []
    if getattr(sys, "frozen", False):
        ordered.append(Path(sys.executable).resolve().parent / "patcher" / SKIN_PATCHER_NAME)
    if meipass:
        ordered.append(Path(meipass) / "patcher" / SKIN_PATCHER_NAME)
    ordered.extend(
        [
            root / "patcher" / "_skin_cycle_staging" / SKIN_PATCHER_NAME,
            root / "patcher" / SKIN_PATCHER_NAME,
            root.parent
            / "tools"
            / "hotfix_patcher_skin_cycle"
            / "bin"
            / "Release"
            / "net8.0"
            / "win-x64"
            / "publish"
            / SKIN_PATCHER_NAME,
            root.parent
            / "tools"
            / "hotfix_patcher_skin_cycle"
            / "bin"
            / "Release"
            / "net8.0"
            / SKIN_PATCHER_NAME,
        ]
    )
    for path in ordered:
        if path.is_file():
            return path
    raise FoolproofError(
        f"找不到 {SKIN_PATCHER_NAME}。\n"
        "请先运行「发布傻瓜换装补丁.bat」或编译 tools/hotfix_patcher_skin_cycle。"
    )


def run_skin_patcher_capture(args: list[str]) -> subprocess.CompletedProcess:
    exe = resolve_skin_patcher_exe()
    proc = subprocess.run(
        [str(exe), *args],
        check=False,
        capture_output=True,
    )
    from patch_common import _decode_patcher_bytes

    return subprocess.CompletedProcess(
        proc.args,
        proc.returncode,
        _decode_patcher_bytes(proc.stdout or b""),
        _decode_patcher_bytes(proc.stderr or b""),
    )


def _copy_battle_appear_json(game_root: Path, on_log: LogFn | None, messages: list[str]) -> None:
    cfg_src = game_root / "tools" / "battle_appear.json"
    if not cfg_src.is_file():
        here = Path(__file__).resolve()
        for cand in (
            here.parents[1] / "tools" / "battle_appear.json",
            here.parents[2] / "tools" / "battle_appear.json",
            Path(sys.executable).resolve().parent / "tools" / "battle_appear.json",
        ):
            if cand.is_file():
                cfg_src = cand
                break
    dst = hotfix_path(game_root).parent / "battle_appear.json"
    if cfg_src.is_file() and not dst.is_file():
        shutil.copy2(cfg_src, dst)
        _emit(messages, on_log, f"已写入 battle_appear.json → {dst.name}")


def run_foolproof_skin_patch(
    game_root: Path | None = None,
    *,
    on_log: LogFn | None = None,
) -> list[str]:
    """一键打傻瓜换装补丁。"""
    messages: list[str] = []

    _emit(messages, on_log, "正在解析游戏目录…")
    root = resolve_game_root(game_root)
    set_game_root(root)
    _emit(messages, on_log, f"游戏目录: {root}")

    _emit(messages, on_log, "正在检查 cg37.exe 是否在运行…")
    if _cg37_running():
        raise FoolproofError("检测到 cg37.exe 正在运行。\n请先完全关闭游戏后再打补丁。")
    _emit(messages, on_log, "未检测到运行中的游戏")

    _emit(messages, on_log, "正在检查 hotfix / 底稿…")
    _ensure_clean_baseline(
        root, messages, on_log=on_log, baseline_tag="foolproof_outfit"
    )

    try:
        resolve_skin_patcher_exe()
    except FoolproofError:
        ensure_patcher()

    hotfix = hotfix_path(root)
    orig = hotfix_orig(root)
    if not orig.is_file():
        raise FoolproofError(f"缺少原版备份 {orig.name}。请先从干净目录恢复后再打。")

    _emit(messages, on_log, "正在从 .orig 恢复干净 hotfix…")
    shutil.copy2(orig, hotfix)
    verify_hotfix(hotfix)
    _emit(messages, on_log, f"已恢复干净 hotfix（期望体积 {EXPECTED_SIZE:,}）")

    work = hotfix
    _emit(messages, on_log, "① 进战形象钩子…")
    try:
        proc = run_skin_patcher_capture(
            [
                "battle-appear-external-patch",
                "--hotfix",
                str(work),
                "--output",
                str(hotfix),
            ]
        )
    except FoolproofError:
        proc = run_patcher_capture(
            [
                "battle-appear-external-patch",
                "--hotfix",
                str(work),
                "--output",
                str(hotfix),
            ]
        )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        raise FoolproofError(out.strip() or "进战形象钩子补丁失败")
    _emit(messages, on_log, "进战形象钩子：OK")
    _copy_battle_appear_json(root, on_log, messages)

    work = hotfix
    _emit(messages, on_log, "② 百科换装循环…")
    proc = run_skin_patcher_capture(
        [
            "wiki-skin-cycle-patch",
            "--hotfix",
            str(work),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        raise FoolproofError(out.strip() or "百科换装循环补丁失败")
    _emit(messages, on_log, "百科换装：点百科切换装备套装 1→2→3→4")

    hd = hotfix.parent
    missing = [n for n in (APPEAR_DLL, SKIN_DLL) if not (hd / n).is_file()]
    if missing:
        raise FoolproofError("补丁后缺少 DLL：\n" + "\n".join(missing))

    mark_hotfix_watch_stamp(root, marked_by="foolproof_outfit")
    _emit(messages, on_log, "傻瓜换装补丁完成。")
    return messages


__all__ = [
    "FoolproofError",
    "is_unclean_client_error",
    "resolve_game_root",
    "restore_hotfixdata_from_clean",
    "run_foolproof_skin_patch",
    "resolve_skin_patcher_exe",
]
