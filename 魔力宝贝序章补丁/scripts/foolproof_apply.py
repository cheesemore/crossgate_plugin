#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
傻瓜补丁：诊断客户端后一键打补丁（由 GUI 调用）。

发布物为融合版（百科助手面板：常规/抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡）。
九动版已无限期停发，enable_nine 仅保留给历史包兼容调用。
界面外层选项：「战斗加速」（开→战斗倍速+心跳回传1.5x；关→原速+心跳回传1.0x）
与「跳帧」（切后台/老板键限帧 10FPS）。

活 hotfix 不干净时：界面可选手选干净目录恢复（restore_hotfixdata_from_clean），无默认源。
体积与 EXPECTED_SIZE 绑定；客户端更新导致体积变化时需发新版傻瓜补丁。
"""
from __future__ import annotations

import shutil
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path

from apply_combo_patch import apply_combo
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
    _is_frozen,
    hotfix_orig,
    hotfix_path,
    mark_hotfix_watch_stamp,
    set_game_root,
)
from patch_defaults import (
    FOOLPROOF_COMBO_KWARGS,
    FOOLPROOF_NO_NINE_COMBO_KWARGS,
)
from patch_slack import format_slack_summary, slack_report

LogFn = Callable[[str], None]

# 兼容旧导入名
UNCLEAN_CLIENT_HINT = (
    "当前客户端状态异常（hotfix 不是干净官方原版）。\n"
    "常见原因：热更新未完整覆盖、仍含旧补丁、半更新。\n\n"
    "可在傻瓜补丁界面选择从干净目录恢复 hotfix 后再打，\n"
    "或自行用启动器修复/换干净客户端后再打补丁。"
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


def run_foolproof_patch(
    game_root: Path | None = None,
    *,
    enable_nine: bool = True,
    daily_claim: bool = True,
    newbie_gift_code: bool = True,
    gift_codes: list[str] | str | None = None,
    apply_accel: bool = False,
    apply_frameskip: bool = True,
    on_log: LogFn | None = None,
    # 旧多档参数已废弃：一律走百科助手面板，忽略下列开关
    burn_seal: bool = False,
    burn_seal_slow: bool = False,
    auto_catch: bool = False,
    auto_catch_nopet: bool = False,
    catch_pet: bool | None = None,
) -> list[str]:
    """一键诊断并打傻瓜补丁（百科助手面板版）。成功返回消息列表；失败抛 FoolproofError。

    enable_nine：九动版 True / 融合版 False（由包类型决定，面板内是否出现九动）。
    apply_accel：战斗加速开关，默认关。开→战斗倍速+心跳回传1.5x；关→原速+心跳回传1.0x。
        注意：战斗倍速补丁默认连带掐断倍速检测上报（CheckTimeScaleWarning /
        SendTimeScaleWarning 打成空方法，防检测）；默认不打加速即避开该改动。
    apply_frameskip：跳帧开关（切后台/老板键限帧 10FPS），默认开。
    daily_claim / newbie_gift_code：分享切页（默认开）。
    gift_codes：可编辑礼包码；None 用默认。
    """
    messages: list[str] = []
    _ = (burn_seal, burn_seal_slow, auto_catch, auto_catch_nopet, catch_pet)

    _emit(messages, on_log, "正在解析游戏目录…")
    root = resolve_game_root(game_root)
    _emit(messages, on_log, f"游戏目录: {root}")

    _emit(messages, on_log, "正在检查 cg37.exe 是否在运行…")
    if _cg37_running():
        raise FoolproofError("检测到 cg37.exe 正在运行。\n请先完全关闭游戏后再打补丁。")
    _emit(messages, on_log, "未检测到运行中的游戏")

    _emit(messages, on_log, "正在检查 hotfix / 底稿…")
    _ensure_clean_baseline(root, messages, on_log=on_log)

    if daily_claim or newbie_gift_code:
        pages = []
        if daily_claim:
            pages.append("日常")
        if newbie_gift_code:
            pages.append("新手礼包码")
        _emit(messages, on_log, "附加：分享切页 → " + "+".join(pages))
    else:
        _emit(messages, on_log, "附加：不打分享切页（保留原版分享）")

    if enable_nine:
        nine_label = "DLL版"
        _emit(
            messages,
            on_log,
            "预设：百科助手面板（常规/九动/抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡）",
        )
        kwargs = dict(FOOLPROOF_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = True
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["auto_catch_nopet_external"] = True
        kwargs["auto_catch_sell_external"] = True
        kwargs["wiki_test_ui"] = True
        nine_checks = ["nine_external"]
        catch_check = ["auto_catch_external"]
    else:
        nine_label = "无"
        _emit(
            messages,
            on_log,
            "预设：百科助手面板（常规/抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡 · 无九动）",
        )
        kwargs = dict(FOOLPROOF_NO_NINE_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["auto_catch_nopet_external"] = True
        kwargs["auto_catch_sell_external"] = True
        kwargs["wiki_test_ui"] = True
        nine_checks = []
        catch_check = ["auto_catch_external", "auto_catch_nopet_external"]

    extra_checks = ["auto_seal_external", "auto_catch_sell_external"] + catch_check
    if kwargs.get("level_one_include_all"):
        extra_checks.append("level_one_include_all")

    kwargs["from_orig"] = True
    kwargs["inject_bridge"] = False
    kwargs["daily_claim"] = bool(daily_claim)
    kwargs["newbie_gift_code"] = bool(newbie_gift_code)
    kwargs["gift_codes"] = gift_codes
    kwargs["game_root"] = root
    kwargs["on_log"] = on_log

    if not apply_accel:
        # 加速关：完全不碰 vip-timescale-patch（不设 vip_echo → 不打心跳回传，避免连带 kill-report）
        kwargs["vip"] = False
        kwargs["vip_non_vip"] = False
        kwargs["vip_echo"] = None
        kwargs["map_sprint"] = False
        kwargs["skill_effect_speed"] = False
        kwargs["transition_speed"] = False
        _emit(messages, on_log, "加速补丁：关（不打战斗倍速/心跳回传/跑速/特效/过场，不触碰倍速检测上报）")
    else:
        # 加速开：战斗倍速（VIP+非VIP 同倍速）+ 心跳回传 1.5x（强制绑定 kill-report，掐断倍速检测上报）
        kwargs["vip"] = True
        kwargs["vip_non_vip"] = True
        kwargs["vip_echo"] = 1.5
        _emit(messages, on_log, "加速补丁：开（战斗倍速 + 心跳回传固定 1.5x，强制携带 kill-report）")

    kwargs["boss_key_fps"] = bool(apply_frameskip)
    _emit(
        messages,
        on_log,
        "跳帧（切后台/老板键限帧 10FPS）：开" if apply_frameskip else "跳帧（切后台/老板键限帧 10FPS）：关",
    )

    _emit(messages, on_log, "正在余量预检（启动补丁引擎，首次可能较慢）…")
    try:
        precheck = ["longpress"] if not apply_accel else ["vip", "sprint", "longpress", "skill_effect"]
        data = slack_report(
            game_root=root,
            prefer_orig=True,
            check=precheck
            + (["transition"] if kwargs.get("transition_speed") else [])
            + nine_checks
            + extra_checks,
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
                "请确认已用启动器更新到最新客户端，并使用对应新版傻瓜补丁。\n\n"
                f"详情：{text}"
            ) from exc
        raise FoolproofError(f"打补丁失败：\n{text}") from exc

    for msg in patch_msgs:
        if msg not in messages:
            messages.append(msg)

    panel_part = (
        " · 百科面板(常规/九动/抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡)"
        if enable_nine
        else " · 百科面板(常规/抓宠（无宠二动）/抓宠/抓宠卖银币/烧卡)"
    )
    frameskip_part = " · 跳帧开" if apply_frameskip else " · 跳帧关"
    daily_part = ""
    if kwargs.get("daily_claim") or kwargs.get("newbie_gift_code"):
        bits = []
        if kwargs.get("daily_claim"):
            bits.append("日常")
        if kwargs.get("newbie_gift_code"):
            bits.append("礼包码")
        daily_part = " · 分享切页(" + "+".join(bits) + ")"
    gm_part = " · 客服→高级自动战斗" if kwargs.get("customer_gm") else ""
    nine_part = f" · 九动{nine_label}" if enable_nine else " · 无九动"
    if not apply_accel:
        _emit(
            messages,
            on_log,
            f"已应用：无战斗倍速 · 原速跑图 · 长按详情 · 无特效加速 · 心跳回传1.0x"
            f"{panel_part}{gm_part}{daily_part}{nine_part}{frameskip_part}"
            + (" · 一级含蝙蝠/哥布林" if kwargs.get("level_one_include_all") else ""),
        )
    else:
        vip = kwargs.get("vip_scale", 5)
        fx = kwargs.get("skill_effect_scale", 2.0)
        echo = kwargs.get("vip_echo", 1.5)
        sprint_part = " · Sprint快" if kwargs.get("map_sprint") else ""
        _emit(
            messages,
            on_log,
            f"已应用：VIP{vip}x{sprint_part} · 长按详情 · 特效{fx}x · 心跳回传{echo:g}x"
            f"{panel_part}{gm_part}{daily_part}{nine_part}{frameskip_part}"
            + (" · 一级含蝙蝠/哥布林" if kwargs.get("level_one_include_all") else ""),
        )
    try:
        marked = "foolproof" if enable_nine else "foolproof_no_nine"
        mark_hotfix_watch_stamp(root, marked_by=marked)
        _emit(messages, on_log, "已标记 hotfix 指纹")
    except Exception as exc:
        _emit(messages, on_log, f"警告：标记指纹失败（{exc}）")

    if kwargs.get("wiki_test_ui"):
        _deploy_pet_rank_bin(root, messages, on_log=on_log)

    if kwargs.get("battle_appear"):
        _emit(messages, on_log, "进战形象：已打钩子（百科→形象 / 推荐方案 / 按Uid存档）")

    _emit(messages, on_log, "完成。请启动游戏验证。")
    return messages


def _deploy_pet_rank_bin(
    game_root: Path,
    messages: list[str],
    *,
    on_log: LogFn | None = None,
) -> None:
    """把超级AI用的 pet_rank.bin 拷到游戏根 tools/（包内或开发目录均尝试）。"""
    candidates: list[Path] = []
    if _is_frozen():
        base = Path(sys.executable).resolve().parent
        candidates.append(base / "tools" / "pet_rank.bin")
    here = Path(__file__).resolve().parent
    candidates.append(here.parent.parent / "tools" / "pet_rank.bin")
    src = next((p for p in candidates if p.is_file()), None)
    if src is None:
        return
    dst_dir = game_root / "tools"
    dst = dst_dir / "pet_rank.bin"
    try:
        dst_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        _emit(messages, on_log, f"已部署档位表: {dst}")
    except OSError as exc:
        _emit(messages, on_log, f"警告：部署 pet_rank.bin 失败（{exc}）")
