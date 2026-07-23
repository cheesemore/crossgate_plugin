#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
傻瓜补丁：与 GUI 默认勾选一致，自动判断客户端状态后一键打补丁。

默认组合（见 patch_defaults.FOOLPROOF_COMBO_KWARGS）：
  VIP/非VIP 5x · 客服→自动技能 · Sprint 快 · 长按详情 · 无加速过场
  · 技能特效 2x · 无九动 · 无桥接

带九动包：run_foolproof_patch(enable_nine=True) 时运行时择优 IL/DLL。
GUI / 简单补丁默认过场为「快」0.4s（见 DEFAULT_COMBO_KWARGS）。

体积与 HotfixSize.Expected / EXPECTED_SIZE 绑定；客户端更新导致体积变化时
需发新版傻瓜补丁（平时不改，等稳定版再更新）。
"""
from __future__ import annotations

import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path

from apply_combo_patch import apply_combo
from patch_common import (
    DATA_DIR,
    EXPECTED_SIZE,
    KNOWN_OLD_SIZES,
    _is_frozen,
    _safe_copy2,
    bridge_dll_path,
    clear_combo_patch_state,
    detect_game_root_from_launcher,
    find_game_root_walking_up,
    get_game_root,
    hotfix_orig,
    hotfix_path,
    mark_hotfix_watch_stamp,
    pick_clean_hotfix_source,
    save_baseline_meta,
    set_game_root,
    sha256_file,
    updated_hotfix_candidate,
)
from patch_defaults import FOOLPROOF_COMBO_KWARGS, FOOLPROOF_NO_NINE_COMBO_KWARGS
from patch_slack import format_slack_summary, slack_report

LogFn = Callable[[str], None]


class FoolproofError(RuntimeError):
    """面向用户的失败说明（可直接弹窗）。"""


def _emit(messages: list[str], on_log: LogFn | None, text: str) -> None:
    """追加并实时回调（多行按行回调，便于 GUI 立刻刷新）。"""
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
        # 选中的可能是子目录，同样向上找
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

    # 最后再从 cwd / exe 暴力向上
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
        f"请将「傻瓜补丁」解压到游戏目录（与 cg37.exe / {DATA_DIR} 同级，"
        f"或解压到任意子文件夹亦可），再运行「一键打补丁.bat」。"
    )


def choose_nine_il(game_root: Path, on_log: LogFn | None = None) -> bool:
    """True=打 IL 九动；False=打 DLL 版。默认倾向 IL（与 GUI 默认勾选一致）。"""
    if on_log:
        on_log("正在测算九动余量（启动补丁引擎，首次可能较慢）…")
    try:
        data = slack_report(game_root=game_root, prefer_orig=True, check=["nine"])
    except Exception as exc:
        if on_log:
            on_log(f"九动余量测算失败，仍优先尝试 IL（{exc}）")
        return True  # 探测失败时仍优先尝试 IL，失败再回退 DLL

    va_gap = int(data.get("va_gap_bytes") or 0)
    for p in data.get("patches") or []:
        if p.get("id") not in ("nine", "nine_queue"):
            continue
        growth = int(p.get("growth_bytes") or 0)
        mode = p.get("mode")
        if mode == "external_dll":
            return False
        if p.get("already") or p.get("can_apply"):
            return True
        if growth > 0 and va_gap >= growth:
            return True
        return False
    return True


def _ensure_clean_baseline(
    root: Path,
    messages: list[str],
    on_log: LogFn | None = None,
) -> Path:
    """保证 .orig / neworig 为干净原版且体积=EXPECTED_SIZE，返回 orig 路径。"""
    hf = hotfix_path(root)
    if not hf.is_file():
        raise FoolproofError(
            "找不到 hotfix.dll.bytes。\n"
            "请用启动器「更新/修复」或重新下载客户端后再试。"
        )

    _emit(messages, on_log, f"hotfix 路径: {hf}")
    _emit(messages, on_log, f"hotfix 体积: {hf.stat().st_size:,} 字节（期望 {EXPECTED_SIZE:,}）")
    _emit(messages, on_log, "正在挑选干净底稿…")

    try:
        src, label = pick_clean_hotfix_source(root)
    except RuntimeError as exc:
        size = hf.stat().st_size
        if size != EXPECTED_SIZE:
            raise _size_mismatch_error(size) from exc
        raise FoolproofError(
            "当前 hotfix 已改过，且找不到干净原版底稿，无法安全打补丁。\n\n"
            "请关闭游戏 → 启动器「更新/修复」恢复官方 hotfix → 再运行本工具。\n"
            "若修复后仍失败，请重新下载客户端。\n\n"
            f"技术详情：{exc}"
        ) from exc

    src_size = src.stat().st_size
    if src_size != EXPECTED_SIZE:
        raise _size_mismatch_error(src_size)

    if label.startswith("hotfix") and hf.stat().st_size != EXPECTED_SIZE:
        raise _size_mismatch_error(hf.stat().st_size)

    neworig = updated_hotfix_candidate(root)
    orig = hotfix_orig(root)
    neworig.parent.mkdir(parents=True, exist_ok=True)
    _emit(messages, on_log, f"底稿来源: {label} → 同步 neworig / .orig …")
    if _safe_copy2(src, neworig):
        _emit(messages, on_log, f"已同步底稿 neworig（来源 {label}，{EXPECTED_SIZE:,} 字节）")
    else:
        _emit(messages, on_log, f"底稿 neworig 已是最新（来源 {label}）")
    if _safe_copy2(neworig, orig):
        _emit(messages, on_log, "已写入/更新 hotfix.dll.bytes.orig")
    else:
        _emit(messages, on_log, ".orig 已与底稿一致")

    _emit(messages, on_log, "正在计算底稿 SHA256…")
    digest = sha256_file(neworig)
    save_baseline_meta(
        root,
        {
            "expected_size": EXPECTED_SIZE,
            "neworig_sha256": digest,
            "source": f"foolproof:{label}",
            "synced_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "notes": f"傻瓜补丁底稿；EXPECTED_SIZE={EXPECTED_SIZE:,}",
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


def _size_mismatch_error(size: int) -> FoolproofError:
    if size in KNOWN_OLD_SIZES:
        return FoolproofError(
            f"客户端 hotfix 版本过旧或过新（实际 {size:,}，本工具期望 {EXPECTED_SIZE:,}）。\n"
            f"提示：{KNOWN_OLD_SIZES[size]}\n\n"
            f"请用启动器「更新/修复」到与本傻瓜补丁匹配的版本；\n"
            f"若已是最新仍不匹配，请等待适配后的傻瓜补丁更新，或重新下载客户端。"
        )
    return FoolproofError(
        f"hotfix 体积不匹配（实际 {size:,}，本工具期望 {EXPECTED_SIZE:,}）。\n\n"
        f"常见原因：客户端刚更新、文件损坏、或拷错目录。\n"
        f"请启动器「更新/修复」后重试；仍不行请重新下载客户端，\n"
        f"或等待适配该体积的傻瓜补丁。"
    )


def run_foolproof_patch(
    game_root: Path | None = None,
    *,
    enable_nine: bool = True,
    on_log: LogFn | None = None,
) -> list[str]:
    """一键诊断并打傻瓜补丁。成功返回消息列表；失败抛 FoolproofError。

    on_log：每产生一条进度即回调（GUI 可用来实时刷日志）。
    """
    messages: list[str] = []

    _emit(messages, on_log, "正在解析游戏目录…")
    root = resolve_game_root(game_root)
    _emit(messages, on_log, f"游戏目录: {root}")

    _emit(messages, on_log, "正在检查 cg37.exe 是否在运行…")
    if _cg37_running():
        raise FoolproofError("检测到 cg37.exe 正在运行。\n请先完全关闭游戏后再打补丁。")
    _emit(messages, on_log, "未检测到运行中的游戏")

    _emit(messages, on_log, "正在检查 hotfix / 底稿…")
    _ensure_clean_baseline(root, messages, on_log=on_log)

    if enable_nine:
        use_il = choose_nine_il(root, on_log=on_log)
        nine_label = "IL原版" if use_il else "DLL版"
        _emit(messages, on_log, f"神奇九动：选用 {nine_label}")
        kwargs = dict(FOOLPROOF_COMBO_KWARGS)
        kwargs["battle_nine_action"] = use_il
        kwargs["battle_nine_external"] = not use_il
        nine_checks = ["nine"] if use_il else ["nine_external"]
    else:
        use_il = False
        nine_label = "无"
        _emit(messages, on_log, "神奇九动：本包不打九动")
        kwargs = dict(FOOLPROOF_NO_NINE_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        nine_checks = []

    kwargs["from_orig"] = True
    kwargs["inject_bridge"] = False
    kwargs["game_root"] = root
    kwargs["on_log"] = on_log

    _emit(messages, on_log, "正在余量预检（启动补丁引擎，首次可能较慢）…")
    try:
        data = slack_report(
            game_root=root,
            prefer_orig=True,
            check=["vip", "sprint", "longpress", "customer_gm", "skill_effect"]
            + (["transition"] if kwargs.get("transition_speed") else [])
            + nine_checks,
        )
        _emit(messages, on_log, "余量预检:\n" + format_slack_summary(data))
    except Exception as exc:
        _emit(messages, on_log, f"余量预检跳过（{exc}）")

    _emit(messages, on_log, "开始叠加补丁…")
    try:
        patch_msgs = apply_combo(**kwargs)
    except Exception as exc:
        text = str(exc).strip() or exc.__class__.__name__
        if "体积" in text or "Expected" in text or "应为" in text:
            raise FoolproofError(
                "补丁引擎拒绝当前 hotfix（体积/版本不匹配）。\n"
                "请启动器修复或重新下载客户端；若客户端已是更新版，需等待傻瓜补丁更新。\n\n"
                f"详情：{text}"
            ) from exc
        if enable_nine and ("余量" in text or "间隙" in text):
            if use_il:
                _emit(messages, on_log, f"IL 九动失败，改试 DLL 版…（{text}）")
                kwargs["battle_nine_action"] = False
                kwargs["battle_nine_external"] = True
                try:
                    patch_msgs = apply_combo(**kwargs)
                    nine_label = "DLL版（IL 回退）"
                except Exception as exc2:
                    raise FoolproofError(
                        "打补丁失败（九动 IL/DLL 均不可用或其它补丁失败）。\n\n"
                        f"{exc2}"
                    ) from exc2
            else:
                raise FoolproofError(f"打补丁失败：\n{text}") from exc
        else:
            raise FoolproofError(f"打补丁失败：\n{text}") from exc

    # apply_combo 已通过 on_log 实时输出；这里只汇总进 messages，避免 GUI 重复刷
    for msg in patch_msgs:
        if msg not in messages:
            messages.append(msg)

    fx = kwargs.get("skill_effect_scale", 2.0)
    nine_part = f" · 九动{nine_label}" if enable_nine else " · 无九动"
    if kwargs.get("transition_speed"):
        tr = kwargs.get("transition_speed_scale", 0.4)
        tr_part = f" · 过场{tr}s"
    else:
        tr_part = " · 无加速过场"
    _emit(
        messages,
        on_log,
        f"已应用：VIP5x · 自动技能 · Sprint快 · 长按详情{tr_part} · 特效{fx}x{nine_part}",
    )
    try:
        mark_hotfix_watch_stamp(root, marked_by="foolproof" if enable_nine else "foolproof_no_nine")
        _emit(messages, on_log, "已标记 hotfix 指纹")
    except Exception as exc:
        _emit(messages, on_log, f"警告：标记指纹失败（{exc}）")

    _emit(messages, on_log, "完成。请启动游戏验证。")
    return messages
