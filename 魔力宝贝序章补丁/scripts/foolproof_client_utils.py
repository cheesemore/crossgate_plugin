#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""傻瓜补丁共用：游戏目录解析、干净客户端恢复、hotfix 底稿检查。

不含九动/融合/面板等包类型文案，换装包可安全依赖本模块。
"""
from __future__ import annotations

import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path

from patch_common import (
    DATA_DIR,
    EXPECTED_SIZE,
    KNOWN_OLD_SIZES,
    _is_clean_hotfix_file,
    _is_frozen,
    _safe_copy2,
    bridge_dll_path,
    clear_combo_patch_state,
    detect_game_root_from_launcher,
    find_game_root_walking_up,
    get_game_root,
    hotfix_orig,
    hotfix_path,
    save_baseline_meta,
    set_game_root,
    sha256_file,
    updated_hotfix_candidate,
)

LogFn = Callable[[str], None]

UNCLEAN_CLIENT_HINT = (
    "当前客户端状态异常（hotfix 不是干净官方原版）。\n"
    "常见原因：热更新未完整覆盖、仍含旧补丁、半更新。\n\n"
    "可在界面选择从干净目录恢复 hotfix 后再打，\n"
    "或自行用启动器修复/换干净客户端后再打补丁。"
)


class FoolproofError(RuntimeError):
    """面向用户的失败说明（可直接弹窗）。"""


def unclean_client_error(detail: str = "") -> FoolproofError:
    text = UNCLEAN_CLIENT_HINT
    detail = (detail or "").strip()
    if detail:
        text = f"{text}\n\n详情：{detail}"
    return FoolproofError(text)


def is_unclean_client_error(exc: BaseException) -> bool:
    text = str(exc)
    return (
        "不是干净官方原版" in text
        or "客户端状态异常" in text
        or "干净目录恢复" in text
    )


def _emit(messages: list[str], on_log: LogFn | None, text: str) -> None:
    messages.append(text)
    if on_log is None:
        return
    for line in text.splitlines() or [text]:
        on_log(line)


def _cg37_running() -> bool:
    try:
        out = subprocess.check_output(
            ["tasklist", "/FI", "IMAGENAME eq cg37.exe", "/NH"],
            text=True,
            encoding="mbcs",
            errors="replace",
            timeout=15,
        )
        return "cg37.exe" in out.lower()
    except Exception:
        return False


def resolve_game_root(explicit: Path | None = None) -> Path:
    """解析游戏根目录：显式路径 / 配置 / 从 exe·当前目录向上查到盘符。"""
    if explicit is not None:
        root = explicit.resolve()
        if root.is_file():
            root = root.parent
        found = find_game_root_walking_up(root)
        if found is None:
            raise FoolproofError(
                f"从「{explicit}」向上找不到含 cg37.exe 与 {DATA_DIR} 的游戏目录。\n"
                f"请把本工具解压到游戏目录（或任意子目录）后再试。"
            )
        set_game_root(found)
        return found

    root = get_game_root()
    if root is not None and (root / "cg37.exe").is_file() and (root / DATA_DIR).is_dir():
        return root

    root = detect_game_root_from_launcher()
    if root is not None:
        set_game_root(root)
        return root

    starts = []
    if _is_frozen():
        starts.append(Path(sys.executable).resolve().parent)
    starts.append(Path.cwd())
    for start in starts:
        found = find_game_root_walking_up(start)
        if found is not None:
            set_game_root(found)
            return found

    raise FoolproofError(
        f"未找到游戏目录（已从本程序所在位置向上查到盘符根）。\n\n"
        f"请将本工具解压到游戏目录（与 cg37.exe / {DATA_DIR} 同级，"
        f"或解压到任意子文件夹亦可），再运行「一键打补丁.bat」。"
    )


def restore_hotfixdata_from_clean(
    clean_root: Path,
    game_root: Path,
    *,
    on_log: LogFn | None = None,
) -> list[str]:
    """从用户指定的干净客户端同步 hotfixdata（及常用主程序），再写好 .orig。"""
    messages: list[str] = []
    if clean_root is None:
        raise FoolproofError("未指定干净客户端目录。")

    _emit(messages, on_log, "正在解析干净目录 / 游戏目录…")
    clean = resolve_game_root(clean_root)
    game = resolve_game_root(game_root)
    if clean.resolve() == game.resolve():
        raise FoolproofError("干净目录不能与当前游戏目录相同，请另选一份干净客户端。")

    if _cg37_running():
        raise FoolproofError("检测到 cg37.exe 正在运行。\n请先完全关闭游戏后再恢复。")

    src_hf = clean / "cg37_Data" / "assets" / "hotfixdata"
    dst_hf = game / "cg37_Data" / "assets" / "hotfixdata"
    if not src_hf.is_dir():
        raise FoolproofError(f"干净目录缺少 hotfixdata：\n{src_hf}")
    dst_hf.mkdir(parents=True, exist_ok=True)

    _emit(messages, on_log, f"干净目录: {clean}")
    _emit(messages, on_log, f"游戏目录: {game}")

    for name in ("cg37.exe", "GameAssembly.dll", "UnityPlayer.dll", "baselib.dll"):
        src = clean / name
        dst = game / name
        if src.is_file():
            if _safe_copy2(src, dst):
                _emit(messages, on_log, f"已同步 {name}")
            else:
                _emit(messages, on_log, f"跳过 {name}（可能被占用或已一致）")

    copied = 0
    for src in sorted(src_hf.glob("*.bytes")):
        dst = dst_hf / src.name
        if _safe_copy2(src, dst):
            copied += 1
            _emit(messages, on_log, f"已同步 hotfixdata/{src.name}")
        else:
            _emit(messages, on_log, f"跳过 hotfixdata/{src.name}（写入失败）")
    if copied == 0:
        raise FoolproofError("未能从干净目录写入任何 hotfixdata/*.bytes。")

    for dst in list(dst_hf.glob("SeqChapter*")):
        if not (src_hf / dst.name).is_file():
            try:
                dst.unlink()
                _emit(messages, on_log, f"已删除残留 {dst.name}")
            except OSError as exc:
                _emit(messages, on_log, f"警告：无法删除 {dst.name}（{exc}）")

    hf = hotfix_path(game)
    orig = hotfix_orig(game)
    if not hf.is_file():
        raise FoolproofError("恢复后仍找不到 hotfix.dll.bytes。")
    size = hf.stat().st_size
    if size != EXPECTED_SIZE:
        raise FoolproofError(
            f"干净目录的 hotfix 体积为 {size:,}，本包期望 {EXPECTED_SIZE:,}。\n"
            "请换与本补丁同版本的干净客户端，或换对应新版补丁。"
        )
    if not _safe_copy2(hf, orig):
        raise FoolproofError("无法写入 hotfix.dll.bytes.orig。")
    _emit(messages, on_log, f"已写入 .orig（{size:,} 字节）")

    live_ok, live_reason = _is_clean_hotfix_file(hf)
    if not live_ok:
        raise unclean_client_error(
            f"同步后仍不像干净原版：{live_reason}\n请确认所选目录本身是未打补丁的官方客户端。"
        )

    clear_combo_patch_state()
    _emit(messages, on_log, "干净 hotfix 恢复完成，可继续打补丁。")
    return messages


def _size_mismatch_error(size: int) -> FoolproofError:
    if size in KNOWN_OLD_SIZES:
        return FoolproofError(
            f"客户端 hotfix 版本与本补丁不匹配\n"
            f"（实际 {size:,}，本包期望 {EXPECTED_SIZE:,}）。\n"
            f"提示：{KNOWN_OLD_SIZES[size]}\n\n"
            f"请先用启动器把客户端更新到最新，\n"
            f"再使用对应新版本的补丁（旧包请删掉）。"
        )
    return FoolproofError(
        f"hotfix 体积为 {size:,}，本包期望 {EXPECTED_SIZE:,}。\n"
        "请确认客户端版本，或从干净目录恢复后再打。"
    )


def _ensure_clean_baseline(
    root: Path,
    messages: list[str],
    on_log: LogFn | None = None,
    *,
    baseline_tag: str = "foolproof",
) -> Path:
    """保证 .orig / neworig 为「本包期望体积」的干净原版。"""
    hf = hotfix_path(root)
    if not hf.is_file():
        raise unclean_client_error("找不到 hotfix.dll.bytes")

    size = hf.stat().st_size
    _emit(messages, on_log, f"hotfix 路径: {hf}")
    _emit(messages, on_log, f"hotfix 体积: {size:,} 字节（本包期望 {EXPECTED_SIZE:,}）")

    if size != EXPECTED_SIZE:
        raise _size_mismatch_error(size)

    orig = hotfix_orig(root)
    neworig = updated_hotfix_candidate(root)

    for label, path in ((".orig", orig), ("neworig", neworig)):
        if path.is_file() and path.stat().st_size != EXPECTED_SIZE:
            bak = path.with_name(f"{path.name}.bak_size_{path.stat().st_size}")
            try:
                if bak.is_file():
                    bak.unlink()
                path.rename(bak)
                _emit(
                    messages,
                    on_log,
                    f"已隔离过期底稿 {label}（旧体积）→ {bak.name}",
                )
            except OSError:
                try:
                    path.unlink()
                    _emit(messages, on_log, f"已删除过期底稿 {label}（旧体积）")
                except OSError as exc:
                    _emit(messages, on_log, f"警告：无法移除过期 {label}（{exc}）")

    _emit(messages, on_log, "正在确认干净原版…")
    live_ok, live_reason = _is_clean_hotfix_file(hf)
    if not live_ok:
        _emit(messages, on_log, f"活 hotfix 不是干净原版：{live_reason}")
        raise unclean_client_error(live_reason)

    src, label = hf, "hotfix(更新后原版)"

    neworig.parent.mkdir(parents=True, exist_ok=True)
    _emit(messages, on_log, f"底稿来源: {label} → 强制同步 neworig / .orig …")
    if _safe_copy2(src, neworig):
        _emit(messages, on_log, f"已写入底稿 neworig（{EXPECTED_SIZE:,} 字节）")
    else:
        _emit(messages, on_log, "底稿 neworig 已与来源一致")
    if _safe_copy2(neworig, orig):
        _emit(messages, on_log, "已写入/覆盖 hotfix.dll.bytes.orig")
    else:
        _emit(messages, on_log, ".orig 已与底稿一致")

    if sha256_file(hf) != sha256_file(neworig):
        if not _safe_copy2(neworig, hf):
            raise FoolproofError(
                "无法把干净底稿写回 hotfix.dll.bytes。\n"
                "请确认游戏已完全关闭后重试。"
            )
        _emit(messages, on_log, "已将 hotfix 恢复为干净原版，准备打补丁")

    _emit(messages, on_log, "正在计算底稿 SHA256…")
    digest = sha256_file(neworig)
    save_baseline_meta(
        root,
        {
            "expected_size": EXPECTED_SIZE,
            "neworig_sha256": digest,
            "source": f"{baseline_tag}:{label}",
            "synced_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "notes": f"{baseline_tag} 底稿；EXPECTED_SIZE={EXPECTED_SIZE:,}",
        },
    )
    _emit(messages, on_log, f"底稿 SHA256: {digest[:16]}…")

    bridge = bridge_dll_path(root)
    if bridge.is_file():
        try:
            bridge.unlink()
            _emit(messages, on_log, "已移除残留助手桥接 DLL")
        except OSError:
            _emit(messages, on_log, "警告：无法删除桥接 DLL（请确认游戏已关闭）")

    clear_combo_patch_state()
    return orig
