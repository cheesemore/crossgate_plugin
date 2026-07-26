#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""助手桥接注入 — 序章补丁唯一维护入口（HotfixPatcher helper-bridge-patch）。"""
from __future__ import annotations

import shutil
import traceback
from datetime import datetime
from pathlib import Path

from patch_common import (
    BRIDGE_PATCHED_VARIANTS,
    bridge_dll_path,
    bridge_variant_label,
    detect_bridge_variant,
    effective_expected_size,
    hotfix_orig,
    hotfix_path,
    is_bridge_patched,
    run_patcher_capture,
)

BRIDGE_INJECT_LOG = Path.home() / ".seqchapter_helper" / "bridge_inject.log"


def bridge_inject_log_path() -> Path:
    return BRIDGE_INJECT_LOG


def _append_bridge_inject_log(
    game_root: Path,
    *,
    ok: bool,
    stage: str,
    detail: str,
    cmd: list[str] | None = None,
    returncode: int | None = None,
) -> None:
    ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    blocks = [
        f"=== {ts} ===",
        f"result: {'OK' if ok else 'FAIL'}",
        f"stage: {stage}",
        f"game_root: {game_root}",
        f"hotfix: {hotfix_path(game_root)}",
        f"orig: {hotfix_orig(game_root)}",
    ]
    if cmd:
        blocks.append("command: " + " ".join(cmd))
    if returncode is not None:
        blocks.append(f"exit_code: {returncode}")
    blocks.append("--- detail ---")
    blocks.append(detail.rstrip() or "(无输出)")
    blocks.append("")
    text = "\n".join(blocks)

    targets = [BRIDGE_INJECT_LOG, hotfix_path(game_root).parent / "bridge_inject.log"]
    for path in targets:
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as fh:
                fh.write(text)
        except OSError:
            continue


def _fail(
    game_root: Path,
    stage: str,
    detail: str,
    *,
    cmd: list[str] | None = None,
    returncode: int | None = None,
) -> tuple[bool, str]:
    _append_bridge_inject_log(
        game_root,
        ok=False,
        stage=stage,
        detail=detail,
        cmd=cmd,
        returncode=returncode,
    )
    log_hint = (
        f"\n\n── 失败日志 ──\n"
        f"{BRIDGE_INJECT_LOG}\n"
        f"{hotfix_path(game_root).parent / 'bridge_inject.log'}"
    )
    return False, detail.strip() + log_hint


def report_bridge_inject_failure(game_root: Path, stage: str, detail: str) -> str:
    _, msg = _fail(game_root, stage, detail)
    return msg


def ensure_orig_backup(game_root: Path) -> Path:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    if not hotfix.is_file():
        raise FileNotFoundError(f"找不到 hotfix: {hotfix}")
    if not orig.is_file():
        shutil.copy2(hotfix, orig)
    return orig


def detect_bootstrap_site(game_root: Path) -> str:
    hotfix = hotfix_path(game_root)
    if not hotfix.is_file():
        return "none"
    proc = run_patcher_capture(
        ["helper-bridge-patch", "--hotfix", str(hotfix), "--detect-bootstrap-site"]
    )
    out = ((proc.stdout or "") + (proc.stderr or "")).strip().lower()
    for site in ("quit", "pause", "none"):
        if site in out:
            return site
    return "none"


def _hotfix_uses_add_time_invoke(game_root: Path) -> bool:
    hotfix = hotfix_path(game_root)
    if not hotfix.is_file():
        return False
    proc = run_patcher_capture(["ildump", str(hotfix), "HotfixEntry.Start"])
    out = (proc.stdout or "") + (proc.stderr or "")
    return "AddTimeInvoke" in out


def remove_bridge_patch(game_root: Path) -> tuple[bool, str]:
    """用 .orig 覆盖 hotfix，并删除桥接 DLL。"""
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    bridge = bridge_dll_path(game_root)
    if not hotfix.is_file():
        return False, f"找不到 hotfix: {hotfix}"
    if not orig.is_file():
        return (
            False,
            f"找不到备份 {orig.name}。\n"
            "请先在序章补丁 GUI 点「初始化」。",
        )
    if not is_bridge_patched(game_root) and not bridge.is_file():
        return True, "当前 hotfix 未检测到桥接，无需取消"

    try:
        shutil.copy2(orig, hotfix)
    except OSError as exc:
        return False, f"无法写入 hotfix（请先关闭游戏）: {exc}"

    if bridge.is_file():
        try:
            bridge.unlink()
        except OSError as exc:
            return False, f"hotfix 已恢复，但无法删除桥接 DLL: {exc}"

    if is_bridge_patched(game_root):
        return (
            False,
            "已恢复 .orig，但仍检测到桥接 hook。\n"
            "请用序章补丁 GUI 重新「初始化」。",
        )
    return True, f"已从 {orig.name} 恢复 hotfix，桥接已取消"


def apply_bridge_patch(game_root: Path, *, force_from_orig: bool = False) -> tuple[bool, str]:
    """注入 SeqChapterHelperBridge（外部 DLL + hook，hotfix 体积不变）。

    若 hook 已正确注入，仍会走 patcher 刷新外部 DLL（逻辑更新无需重打玩法补丁）。
    """
    hotfix = hotfix_path(game_root)
    if not hotfix.is_file():
        return _fail(game_root, "precheck", f"找不到 hotfix: {hotfix}")

    variant = detect_bridge_variant(game_root)
    if variant == "embedded":
        force_from_orig = True

    refresh_dll_only = False
    if is_bridge_patched(game_root) and not force_from_orig:
        if variant == "binary_loadfrom":
            refresh_dll_only = True
        elif variant == "cecil_light_loadfrom":
            site = detect_bootstrap_site(game_root)
            if site == "quit" and _hotfix_uses_add_time_invoke(game_root):
                refresh_dll_only = True
            else:
                force_from_orig = True
        elif variant == "cecil_light_loadbytes":
            site = detect_bootstrap_site(game_root)
            if site == "pause" and _hotfix_uses_add_time_invoke(game_root):
                refresh_dll_only = True
            else:
                force_from_orig = True
        elif variant == "cecil_light":
            force_from_orig = True
        else:
            refresh_dll_only = True

    try:
        ensure_orig_backup(game_root)
    except FileNotFoundError as exc:
        return _fail(game_root, "backup", str(exc))

    # 仅刷新 DLL 时必须以当前已打补丁的 hotfix 为输入，避免从 .orig 抹掉玩法补丁。
    source = hotfix if refresh_dll_only else (hotfix_orig(game_root) if force_from_orig else hotfix)
    args = ["helper-bridge-patch", "--hotfix", str(source), "--output", str(hotfix)]
    try:
        proc = run_patcher_capture(args)
    except FileNotFoundError as exc:
        return _fail(game_root, "patcher_missing", str(exc), cmd=args)
    except Exception:
        return _fail(game_root, "patcher_exception", traceback.format_exc(), cmd=args)

    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return _fail(
            game_root,
            "patcher_exit",
            out.strip() or f"HotfixPatcher 退出码 {proc.returncode}",
            cmd=args,
            returncode=proc.returncode,
        )

    if not is_bridge_patched(game_root):
        return _fail(
            game_root,
            "post_verify",
            "注入完成但未通过 patched 检测:\n" + out.strip(),
            cmd=args,
            returncode=proc.returncode,
        )

    variant = detect_bridge_variant(game_root)
    if variant in ("broken", "embedded", "unknown"):
        remove_bridge_patch(game_root)
        return _fail(
            game_root,
            "bridge_variant",
            f"桥接校验异常（{variant}），已自动还原 hotfix。\n"
            "请关闭游戏后重试，或先在序章补丁 GUI「初始化」。",
            cmd=args,
            returncode=proc.returncode,
        )

    detail = out.strip() or "助手桥接注入成功"
    if refresh_dll_only and "刷新" not in detail:
        detail = "助手桥接 DLL 已刷新（hook 未改）\n" + detail

    _append_bridge_inject_log(
        game_root,
        ok=True,
        stage="done" if not refresh_dll_only else "refresh_dll",
        detail=detail,
        cmd=args,
        returncode=0,
    )
    return True, detail


def verify_hotfix_size(game_root: Path) -> None:
    hotfix = hotfix_path(game_root)
    if not hotfix.is_file():
        raise FileNotFoundError(f"找不到 hotfix: {hotfix}")
    expected = effective_expected_size(game_root)
    size = hotfix.stat().st_size
    if size != expected:
        raise RuntimeError(
            f"hotfix 体积异常: {size}，期望 {expected}。"
            "请用序章补丁 GUI「初始化」。"
        )
