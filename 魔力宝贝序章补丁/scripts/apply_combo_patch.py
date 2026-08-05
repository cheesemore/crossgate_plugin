#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 组合热补丁（由 GUI 调用；Agent 自测请用 .orig 或副本，勿写游戏 hotfix）。"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

from patch_common import (
    CUSTOMER_GM_LABELS,
    CUSTOMER_GM_MODES,
    GAME_ROOT,
    normalize_customer_gm_mode,
    EXPECTED_SIZE,
    bridge_variant_label,
    detect_bridge_variant,
    detect_customer_gm_mode,
    effective_expected_size,
    ensure_orig_backup,
    get_game_root,
    get_update_status,
    hotfix_orig,
    hotfix_path,
    is_bridge_patched,
    mark_hotfix_watch_stamp,
    run_patcher_capture,
    sha256_file,
    sniff_customer_gm,
    toolkit_root,
    verify_hotfix,
)
from patch_slack import assert_combo_slack_ok, format_slack_summary

STATE_PATH = toolkit_root() / "combo_patch_state.json"

DEFAULT_GIFT_CODES = [
    "VIP666",
    "VIP777",
    "VIP888",
    "VIP999",
    "MLBB666",
    "MLBB777",
    "mlbb521",
    "mlbb24",
    "mlbb0803",
]


def normalize_gift_codes(codes: list[str] | str | None) -> list[str]:
    """一行一个礼包码；去空行与 # 注释。"""
    if codes is None:
        return list(DEFAULT_GIFT_CODES)
    if isinstance(codes, str):
        lines = codes.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    else:
        lines = [str(x) for x in codes]
    out: list[str] = []
    for line in lines:
        s = line.strip()
        if not s or s.startswith("#"):
            continue
        out.append(s)
    return out or list(DEFAULT_GIFT_CODES)


def write_gift_codes_file(hotfix_dir: Path, codes: list[str] | str | None) -> Path:
    """写出 hotfixdata/seqchapter_gift_codes.txt（与 DLL 读取路径一致）。"""
    path = hotfix_dir / "seqchapter_gift_codes.txt"
    rows = normalize_gift_codes(codes)
    body = "# 新手礼包码（一行一个；# 开头为注释）\n" + "\n".join(rows) + "\n"
    path.write_text(body, encoding="utf-8")
    return path


def apply_vip(
    hotfix: Path,
    source: Path,
    scale: int = 5,
    *,
    vip_branch: bool = True,
    non_vip: bool = False,
    echo: float | None = None,
) -> tuple[bool, str]:
    # 约定：带加速/心跳回传的补丁一律默认携带 kill-report（掐断 CheckTimeScaleWarning /
    # SendTimeScaleWarning 倍速上报）。C# 侧默认开启，这里不传 --no-kill-report 即可；
    # 切勿在调用处显式关闭，除非有明确需求。
    args = [
        "vip-timescale-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
        "--scale",
        str(scale),
    ]
    if echo is not None:
        args += ["--echo", f"{echo:g}"]
    if non_vip and not vip_branch:
        args.append("--non-vip-only")
    elif non_vip:
        args.append("--non-vip")
    if not vip_branch and not non_vip and echo is not None:
        args.append("--echo-only")
    proc = run_patcher_capture(args)
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "可能已打过补丁" in out or "倍速补丁可能已打过" in out:
            return True, "战斗倍速：已是补丁状态（跳过）"
        return False, out.strip() or "战斗倍速补丁失败"
    if "[SKIP]" in out:
        return True, "战斗倍速：已是补丁状态（跳过）"
    parts = []
    if vip_branch:
        parts.append(f"VIP {scale}x")
    if non_vip:
        parts.append(f"非VIP {scale}x")
    if echo is not None:
        echo_label = f"回传{echo:g}x"
    else:
        echo_label = "回传1.0x" if non_vip else "回传1.5x"
    if not vip_branch and not non_vip:
        return True, f"战斗倍速：不改倍速，心跳{echo_label}"
    return True, "战斗倍速：" + "、".join(parts) + f"（打飞×{int(scale) * 4}，心跳{echo_label}）"


MAP_SPRINT_SCALES = (8, 10, 12)
MAP_SPRINT_LABELS = {8: "快", 10: "很快", 12: "飞快"}

SKILL_EFFECT_SCALES = (1.5, 2.0, 3.0, 5.0)
SKILL_EFFECT_LABELS = {1.5: "1.5x", 2.0: "2x", 3.0: "3x", 5.0: "5x"}

TRANSITION_SPEED_SCALES = (0.4, 0.2, 0.1)
TRANSITION_SPEED_LABELS = {0.4: "快", 0.2: "很快", 0.1: "飞快"}


def apply_map_sprint(hotfix: Path, scale: int = 8) -> tuple[bool, str]:
    if scale not in MAP_SPRINT_SCALES:
        return False, f"无效 Sprint 跑速: {scale}"
    proc = run_patcher_capture(
        [
            "map-sprint-speed-patch",
            "--hotfix",
            str(hotfix),
            "--output",
            str(hotfix),
            "--scale",
            str(scale),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "Sprint 跑速补丁失败"
    if "[SKIP]" in out:
        return True, f"Sprint 跑速：已是 {MAP_SPRINT_LABELS.get(scale, scale)}（跳过）"
    label = MAP_SPRINT_LABELS.get(scale, str(scale))
    return True, f"Sprint 跑速：{label}（基础 {scale}，仍叠加坐骑/月卡）"


def apply_customer_gm(hotfix: Path, mode: str = "autoskill") -> tuple[bool, str]:
    mode = normalize_customer_gm_mode(mode)
    if mode not in CUSTOMER_GM_MODES:
        return False, f"无效客服模式: {mode}"
    proc = run_patcher_capture(
        [
            "customer-gm-patch",
            "--hotfix",
            str(hotfix),
            "--output",
            str(hotfix),
            "--mode",
            mode,
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "客服按钮补丁失败"
    label = CUSTOMER_GM_LABELS.get(mode, mode)
    return True, f"客服按钮：{label}"


def apply_wiki_download_res(hotfix: Path) -> tuple[bool, str]:
    proc = run_patcher_capture(
        [
            "wiki-download-res-patch",
            "--hotfix",
            str(hotfix),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "百科→资源下载补丁失败"
    if "[SKIP]" in out:
        return True, "百科按钮：已是资源下载面板（跳过）"
    return True, "百科按钮：打开资源下载面板"


def apply_wiki_label(hotfix: Path, source: Path) -> tuple[bool, str]:
    """侧栏百科点击后按钮文字改为「百科1」。"""
    proc = run_patcher_capture(
        [
            "wiki-label-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "百科文字→百科1 补丁失败"
    if "[SKIP]" in out:
        return True, "百科按钮：已是「百科1」（跳过）"
    return True, "百科按钮：点击后文字变为「百科1」"

def apply_skill_effect_speed(hotfix: Path, source: Path, scale: float = 1.5) -> tuple[bool, str]:
    if scale not in SKILL_EFFECT_SCALES:
        return False, f"无效技能特效倍速: {scale}"
    proc = run_patcher_capture(
        [
            "skill-effect-speed-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
            "--scale",
            str(scale),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "旧版技能特效补丁" in out:
            return False, out.strip()
        if "[SKIP]" in out or "可能已打过" in out:
            label = SKILL_EFFECT_LABELS.get(scale, str(scale))
            return True, f"技能特效：已是 {label}（跳过）"
        return False, out.strip() or "技能特效加速补丁失败"
    if "[SKIP]" in out:
        label = SKILL_EFFECT_LABELS.get(scale, str(scale))
        return True, f"技能特效：已是 {label}（跳过）"
    label = SKILL_EFFECT_LABELS.get(scale, str(scale))
    return True, f"技能特效帧动画：{label}（不影响回合读秒 / VIP 倍速）"


def apply_battle_longpress(hotfix: Path, source: Path) -> tuple[bool, str]:
    proc = run_patcher_capture(
        [
            "battle-longpress-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "已去除 P_vs_E" in out:
            return True, "战斗长按详情：已是补丁状态（跳过）"
        return False, out.strip() or "战斗长按详情补丁失败"
    if "[SKIP]" in out:
        return True, "战斗长按详情：已是补丁状态（跳过）"
    return True, "战斗长按：任意战斗类型可查看单位详情"


def apply_level_one_include_all(hotfix: Path, source: Path) -> tuple[bool, str]:
    """遇敌一级停止：不再排除哥布林/迷你蝙蝠（排除常量→999999）。"""
    proc = run_patcher_capture(
        [
            "level-one-include-all-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "已改为无效值" in out:
            return True, "遇敌一级：已含哥布林/迷你蝙蝠（跳过）"
        return False, out.strip() or "遇敌一级含全部宠物补丁失败"
    if "[SKIP]" in out:
        return True, "遇敌一级：已含哥布林/迷你蝙蝠（跳过）"
    return True, "遇敌一级停止：哥布林/迷你蝙蝠也会计入（不再排除）"


def apply_transition_speed(hotfix: Path, source: Path, scale: float = 0.4) -> tuple[bool, str]:
    if scale not in TRANSITION_SPEED_SCALES:
        return False, f"无效过场时长: {scale}"
    proc = run_patcher_capture(
        [
            "transition-speed-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
            "--scale",
            str(scale),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "可能已打过" in out:
            label = TRANSITION_SPEED_LABELS.get(scale, str(scale))
            return True, f"加速过场：已是 {label}（跳过）"
        return False, out.strip() or "加速过场补丁失败"
    if "[SKIP]" in out:
        label = TRANSITION_SPEED_LABELS.get(scale, str(scale))
        return True, f"加速过场：已是 {label}（跳过）"
    label = TRANSITION_SPEED_LABELS.get(scale, str(scale))
    return True, f"加速过场：{label}（CrossBlocks {scale}s，原版 0.8s）"


def apply_battle_nine_action(
    hotfix: Path,
    source: Path,
) -> tuple[bool, str]:
    """原版 IL 九动（整法扩写，需足够 VA 间隙）。"""
    proc = run_patcher_capture(
        [
            "battle-nine-action-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "9动" in out or "神奇九动" in out or "AcountList" in out:
            return True, "神奇九动(IL)：已是补丁状态（跳过）"
        return False, out.strip() or "神奇九动(IL)补丁失败"
    if "[SKIP]" in out:
        return True, "神奇九动(IL)：已是补丁状态（跳过）"
    return True, "神奇九动(IL)：P1 P2 P3 P4 P1 P2 P3 P4 P5"


def apply_battle_nine_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """神奇九动·DLL版：Magics + SeqChapterNineAction.dll.bytes。"""
    proc = run_patcher_capture(
        [
            "battle-nine-external-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "神奇九动·DLL版补丁失败"
    if "[SKIP]" in out:
        return True, "神奇九动·DLL版：已是补丁状态（跳过）"
    return True, "神奇九动·DLL版：已部署 DLL + 加载钩 + Magics"


def apply_player_action_magics(hotfix: Path, source: Path) -> tuple[bool, str]:
    """仅 Magics：一动技能/道具后二动仍可开技能栏（无宠二动依赖；不含九动队列）。"""
    proc = run_patcher_capture(
        [
            "battle-nine-action-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
            "--magics-only",
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        if "[SKIP]" in out or "Magics" in out or "PlayerActionMagics" in out:
            return True, "无宠二动·Magics：已是补丁状态（跳过）"
        return False, out.strip() or "无宠二动·Magics 补丁失败"
    if "[SKIP]" in out:
        return True, "无宠二动·Magics：已是补丁状态（跳过）"
    return True, "无宠二动·Magics：一动技能后二动仍可开技能栏"


def apply_daily_claim_external(
    hotfix: Path,
    source: Path,
    *,
    daily: bool = True,
    gift: bool = True,
    gift_codes: list[str] | str | None = None,
) -> tuple[bool, str]:
    """日常/新手礼包码·分享入口：SeqChapterDailyClaim.dll.bytes + 侧栏分享切页。"""
    proc = run_patcher_capture(
        [
            "daily-claim-external-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "日常·分享入口补丁失败"

    # 覆盖切页开关 + 可编辑礼包码（与 GUI 一致）
    opts_dir = hotfix.parent
    try:
        (opts_dir / "seqchapter_share_opts.txt").write_text(
            f"daily={1 if daily else 0}\ngift={1 if gift else 0}\n",
            encoding="utf-8",
        )
        codes = normalize_gift_codes(gift_codes)
        write_gift_codes_file(opts_dir, codes)
    except OSError as exc:
        return False, f"分享 opts/礼包码 写入失败: {exc}"

    parts = []
    if daily:
        parts.append("日常领取")
    if gift:
        parts.append(f"新手礼包码×{len(normalize_gift_codes(gift_codes))}")
    label = "+".join(parts) if parts else "（未启用页）"
    if "[SKIP]" in out and "日常" in out:
        return True, f"分享切页：已是补丁状态（opts→{label}）"
    return True, f"分享切页：侧栏「分享」→ {label}（2秒内再点开始）"


def apply_battle_appear_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """进战形象钩子：OnCommandCharCallback → SeqChapterBattleAppear + battle_appear.json。"""
    proc = run_patcher_capture(
        [
            "battle-appear-external-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "进战形象钩子补丁失败"
    # 确保配置存在
    cfg_src = GAME_ROOT / "tools" / "battle_appear.json"
    cfg_dst = hotfix.parent / "battle_appear.json"
    tools_bin = GAME_ROOT / "tools" / "pet_appear.bin"
    try:
        if cfg_src.is_file():
            # 不覆盖玩家已改配置
            if not cfg_dst.is_file():
                shutil.copy2(cfg_src, cfg_dst)
        if tools_bin.is_file():
            # 形象表供预览工具；钩子本身只读 json
            pass
    except OSError as exc:
        return False, f"形象钩子配置复制失败: {exc}"
    if "[APPEAR] OnCommandCharCallback 钩已存在" in out:
        return True, "进战形象钩子：已是补丁状态（刷新 DLL）"
    return True, "进战形象钩子：进战按 battle_appear.json 替换我方1~5形象"


def apply_boss_key_fps_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """切后台/老板键限帧：失焦或隐藏时 10FPS，恢复时还原（不占百科/Pause）。"""
    proc = run_patcher_capture(
        [
            "boss-key-fps-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "切后台/老板键限帧补丁失败"
    if "[SKIP]" in out and ("老板键" in out or "限帧" in out):
        return True, "切后台/老板键限帧：已是补丁状态（跳过）"
    return True, "切后台/老板键限帧：失焦或隐藏→10FPS（关 VSync），恢复→还原"


def apply_wiki_fps_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """百科限帧：侧栏百科切换 10FPS（与抓宠/烧卡百科互斥）。已停用默认；保留补丁入口。"""
    proc = run_patcher_capture(
        [
            "wiki-fps-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "百科限帧补丁失败"
    if "[SKIP]" in out and "限帧" in out:
        return True, "百科限帧：已是补丁状态（跳过）"
    return True, "百科限帧：侧栏「百科」切换 10FPS / 恢复"


def apply_wiki_test_ui_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """百科→助手面板：抓宠/烧卡等在面板内切换（玩法 DLL 用 panel 模式部署）。"""
    proc = run_patcher_capture(
        [
            "wiki-test-ui-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "百科→助手面板 补丁失败"
    if "[SKIP]" in out and ("助手面板" in out or "测试UI" in out):
        return True, "百科→助手面板：已是补丁状态（跳过）"
    return True, "百科→助手面板：概况 / 战斗模式 / 抓宠烧卡切换 / 形象"


def apply_vip_auto_monthcard_bypass(hotfix: Path, source: Path) -> tuple[bool, str]:
    """DoAutoFight 跳过月卡：无 VIP 但开关开着仍走 DoVip*。"""
    proc = run_patcher_capture(
        [
            "vip-auto-monthcard-bypass-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "VIP自动月卡 bypass 失败"
    if "[SKIP]" in out and "bypass" in out:
        return True, "VIP自动月卡 bypass：已是补丁状态（跳过）"
    return True, "VIP自动月卡 bypass：开关开着即走 VIP 自动逻辑"


def apply_auto_seal_external(
    hotfix: Path, source: Path, *, panel: bool = False
) -> tuple[bool, str]:
    """自动烧卡·DLL版：DLL + 战斗钩；panel=True 时不占百科/Pause（助手面板切换）。"""
    args = [
        "auto-seal-external-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
    ]
    if panel:
        args.append("--panel")
    proc = run_patcher_capture(args)
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "自动烧卡·DLL版补丁失败"
    if "[SKIP]" in out and "自动" in out:
        return True, "自动烧卡·DLL版：已是补丁状态（跳过）"
    if panel:
        return True, "自动烧卡·DLL版：已部署 DLL + 战斗钩（面板模式）"
    return True, "自动烧卡·DLL版：已部署 DLL + 战斗钩 + 百科开关"


def apply_auto_catch_external(
    hotfix: Path, source: Path, *, panel: bool = False
) -> tuple[bool, str]:
    """自动抓宠·DLL版：DLL + AutoFight/DoVip 钩；panel=True 时不占百科/Pause。"""
    args = [
        "auto-catch-external-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
    ]
    if panel:
        args.append("--panel")
    proc = run_patcher_capture(args)
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "自动抓宠·DLL版补丁失败"
    if "[SKIP]" in out and "自动抓宠" in out:
        return True, "自动抓宠·DLL版：已是补丁状态（跳过）"
    if panel:
        return True, "自动抓宠·DLL版：已部署 DLL + AutoFight/DoVip 钩（面板模式）"
    return True, "自动抓宠·DLL版：已部署 DLL + AutoFight/DoVip 钩 + 百科开关"


def apply_auto_catch_nopet_external(
    hotfix: Path, source: Path, *, panel: bool = False
) -> tuple[bool, str]:
    """自动抓宠·无宠人防御；panel=True 时不占百科/Pause。"""
    args = [
        "auto-catch-nopet-external-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
    ]
    if panel:
        args.append("--panel")
    proc = run_patcher_capture(args)
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "自动抓宠·无宠人防御补丁失败"
    if "[SKIP]" in out and ("自动抓宠" in out or "无宠" in out):
        return True, "自动抓宠·无宠人防御：已是补丁状态（跳过）"
    if panel:
        return True, "自动抓宠·无宠人防御：已部署 DLL + 钩（面板模式）"
    return True, "自动抓宠·无宠人防御：已部署 DLL + AutoFight/DoVip 钩 + 百科开关"


def apply_auto_catch_sell_external(
    hotfix: Path, source: Path, *, panel: bool = False
) -> tuple[bool, str]:
    """抓宠卖银币·DLL版；panel=True 时不占百科/Pause，与普通抓宠 DLL 共存。"""
    args = [
        "auto-catch-sell-external-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
    ]
    if panel:
        args.append("--panel")
    proc = run_patcher_capture(args)
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "抓宠卖银币·DLL版补丁失败"
    if panel:
        return True, "抓宠卖银币·DLL版：已部署 DLL + 战斗分发钩（面板模式）"
    return True, "抓宠卖银币·DLL版：已部署 DLL + 战斗分发钩 + 百科开关"



def apply_lv1_auto_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """遇1级自动·DLL版：SeqChapterLv1Auto.dll.bytes + Player/Pet/VIP 钩 + 百科开关。"""
    proc = run_patcher_capture(
        [
            "lv1-auto-external-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "遇1级自动·DLL版补丁失败"
    if "[SKIP]" in out and "遇1级" in out:
        return True, "遇1级自动·DLL版：已是补丁状态（跳过）"
    return True, "遇1级自动·DLL版：P1封印/P2技能1/其余防御 + 百科开关"


def apply_auto_sell_external(hotfix: Path, source: Path) -> tuple[bool, str]:
    """盗贼辅助·DLL版：SeqChapterAutoSell.dll.bytes + 百科开关；每10场退战远程出售魔石。"""
    proc = run_patcher_capture(
        [
            "auto-sell-external-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "盗贼辅助·DLL版补丁失败"
    if "[SKIP]" in out and "盗贼辅助" in out:
        return True, "盗贼辅助·DLL版：已是补丁状态（跳过）"
    return True, "盗贼辅助·DLL版：已部署 DLL + 百科开关"


def apply_plugin_host(hotfix: Path, source: Path) -> tuple[bool, str]:
    """插件 Host：SeqChapterPluginHost.dll.bytes + Pause 加载 + 百科开自绘面板。"""
    proc = run_patcher_capture(
        [
            "plugin-host-patch",
            "--hotfix",
            str(source),
            "--output",
            str(hotfix),
        ]
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        return False, out.strip() or "插件 Host 补丁失败"
    if "[SKIP]" in out and "Host" in out:
        return True, "插件 Host：已是补丁状态（跳过）"
    return True, "插件 Host：已部署 DLL + Pause 加载 + 百科开面板"


def _emit_combo(messages: list[str], on_log, text: str) -> None:
    messages.append(text)
    if on_log is None:
        return
    for line in text.splitlines() or [text]:
        on_log(line)


def _apply_gameplay_patches(
    hotfix: Path,
    orig: Path,
    *,
    vip: bool,
    vip_scale: int,
    vip_non_vip: bool,
    vip_echo: float | None = None,
    battle_nine_action: bool,
    battle_nine_external: bool,
    player_action_magics: bool = False,
    auto_seal_external: bool,
    auto_catch_external: bool,
    auto_catch_nopet_external: bool = False,
    auto_catch_sell_external: bool = False,
    lv1_auto_external: bool = False,
    auto_sell_external: bool,
    plugin_host: bool,
    customer_gm: bool,
    customer_gm_mode: str,
    map_sprint: bool,
    map_sprint_scale: int,
    battle_longpress: bool,
    level_one_include_all: bool,
    transition_speed: bool,
    transition_speed_scale: float,
    skill_effect_speed: bool,
    skill_effect_scale: float,
    pet_equip_unlock: bool,
    wiki_download_res: bool,
    wiki_label: bool = False,
    daily_claim: bool = True,
    newbie_gift_code: bool = True,
    gift_codes: list[str] | str | None = None,
    boss_key_fps: bool = False,
    wiki_fps: bool = False,
    wiki_test_ui: bool = False,
    battle_appear: bool = False,
    on_log=None,
) -> tuple[list[str], Path]:
    """在现有 hotfix 上叠加玩法补丁（不还原 .orig）。返回 (messages, work_path)。"""
    messages: list[str] = []
    work = hotfix
    # 助手面板开启时：抓宠/烧卡等只打 DLL+战斗钩，不占百科/Pause
    panel_mode = bool(wiki_test_ui)

    if battle_nine_external:
        _emit_combo(messages, on_log, "正在打：神奇九动·DLL版…")
        ok, msg = apply_battle_nine_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    elif player_action_magics:
        # 九动 DLL 版已含 Magics；融合/无九动时单独打，供面板「无宠二动」
        _emit_combo(messages, on_log, "正在打：无宠二动·Magics…")
        ok, msg = apply_player_action_magics(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if vip or vip_non_vip or vip_echo is not None:
        if vip_scale not in (3, 5, 10):
            raise ValueError("vip_scale 须为 3、5 或 10")
        if vip_echo is not None and not vip and not vip_non_vip:
            # 仅心跳回传（不改倍速）：加速关闭档
            _emit_combo(messages, on_log, f"正在打：心跳回传固定 {vip_echo:g}x…")
            ok, msg = apply_vip(
                hotfix,
                work,
                vip_scale,
                vip_branch=False,
                non_vip=False,
                echo=vip_echo,
            )
        else:
            _emit_combo(messages, on_log, f"正在打：战斗倍速 {vip_scale}x…")
            ok, msg = apply_vip(
                hotfix,
                work,
                vip_scale,
                vip_branch=vip,
                non_vip=vip_non_vip,
                echo=vip_echo,
            )
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    # 无月卡也能走 VIP 自动：仅旁路 MonthCardOpen，开关仍由玩家控制
    _emit_combo(messages, on_log, "正在打：VIP自动月卡 bypass…")
    ok, msg = apply_vip_auto_monthcard_bypass(hotfix, work)
    if not ok:
        raise RuntimeError(msg)
    _emit_combo(messages, on_log, msg)
    work = hotfix

    if battle_nine_action:
        _emit_combo(messages, on_log, "正在打：神奇九动·IL原版…")
        ok, msg = apply_battle_nine_action(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if customer_gm:
        sniff_target = orig if orig.is_file() else hotfix
        _emit_combo(messages, on_log, "正在打：客服入口→高级自动战斗…")
        ok, sniff_out = sniff_customer_gm(sniff_target)
        if not ok:
            raise RuntimeError(f"客服入口嗅探失败:\n{sniff_out}")
        ok, msg = apply_customer_gm(hotfix, normalize_customer_gm_mode(customer_gm_mode))
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)

    if map_sprint:
        if map_sprint_scale not in MAP_SPRINT_SCALES:
            raise ValueError("map_sprint_scale 须为 8、10 或 12")
        _emit_combo(messages, on_log, "正在打：地图跑速…")
        ok, msg = apply_map_sprint(hotfix, map_sprint_scale)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)

    if battle_longpress:
        _emit_combo(messages, on_log, "正在打：长按详情…")
        ok, msg = apply_battle_longpress(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if level_one_include_all:
        _emit_combo(messages, on_log, "正在打：遇敌一级含哥布林/迷你蝙蝠…")
        ok, msg = apply_level_one_include_all(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if transition_speed:
        if transition_speed_scale not in TRANSITION_SPEED_SCALES:
            raise ValueError("transition_speed_scale 须为 0.4、0.2 或 0.1")
        _emit_combo(messages, on_log, "正在打：加速过场…")
        ok, msg = apply_transition_speed(hotfix, work, transition_speed_scale)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if skill_effect_speed:
        if skill_effect_scale not in SKILL_EFFECT_SCALES:
            raise ValueError("skill_effect_scale 须为 1.5、2、3 或 5")
        _emit_combo(messages, on_log, "正在打：技能特效加速…")
        ok, msg = apply_skill_effect_speed(hotfix, work, skill_effect_scale)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if wiki_download_res:
        _emit_combo(messages, on_log, "正在打：百科→资源下载…")
        ok, msg = apply_wiki_download_res(hotfix)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)

    # 自动烧卡/抓宠/盗贼辅助/百科文字最后打：Cecil 改方法体，避免挡在二进制补丁前面
    if auto_seal_external:
        _emit_combo(
            messages,
            on_log,
            "正在打：自动烧卡·DLL版" + ("（面板模式）…" if panel_mode else "…"),
        )
        ok, msg = apply_auto_seal_external(hotfix, work, panel=panel_mode)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if auto_catch_external:
        _emit_combo(
            messages,
            on_log,
            "正在打：自动抓宠·DLL版" + ("（面板模式）…" if panel_mode else "…"),
        )
        ok, msg = apply_auto_catch_external(hotfix, work, panel=panel_mode)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if auto_catch_nopet_external:
        _emit_combo(
            messages,
            on_log,
            "正在打：自动抓宠·无宠人防御" + ("（面板模式）…" if panel_mode else "…"),
        )
        ok, msg = apply_auto_catch_nopet_external(hotfix, work, panel=panel_mode)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if auto_catch_sell_external:
        _emit_combo(
            messages,
            on_log,
            "正在打：抓宠卖银币·DLL版" + ("（面板模式）…" if panel_mode else "…"),
        )
        ok, msg = apply_auto_catch_sell_external(hotfix, work, panel=panel_mode)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if lv1_auto_external:
        _emit_combo(messages, on_log, "正在打：遇1级自动·DLL版…")
        ok, msg = apply_lv1_auto_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if auto_sell_external:
        _emit_combo(messages, on_log, "正在打：盗贼辅助·DLL版…")
        ok, msg = apply_auto_sell_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if plugin_host:
        _emit_combo(messages, on_log, "正在打：插件 Host（百科面板）…")
        ok, msg = apply_plugin_host(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix
    if wiki_label:
        _emit_combo(messages, on_log, "正在打：百科文字→百科1…")
        ok, msg = apply_wiki_label(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    # 分享切页最后打：日常 / 新手礼包码（只改分享按钮）
    if daily_claim or newbie_gift_code:
        _emit_combo(messages, on_log, "正在打：分享切页（日常/新手礼包码）…")
        ok, msg = apply_daily_claim_external(
            hotfix,
            work,
            daily=bool(daily_claim),
            gift=bool(newbie_gift_code),
            gift_codes=gift_codes,
        )
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    # 切后台 / 老板键限帧（失焦或隐藏 → 10FPS）
    if boss_key_fps:
        _emit_combo(messages, on_log, "正在打：切后台/老板键限帧…")
        ok, msg = apply_boss_key_fps_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    # 百科限帧：默认关（已去除）；占百科按钮
    if wiki_fps:
        _emit_combo(messages, on_log, "正在打：百科限帧…")
        ok, msg = apply_wiki_fps_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    # 百科→助手面板（玩法 DLL 已用面板模式部署，不抢百科）
    if wiki_test_ui:
        _emit_combo(messages, on_log, "正在打：百科→助手面板…")
        ok, msg = apply_wiki_test_ui_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if battle_appear:
        _emit_combo(messages, on_log, "正在打：进战形象钩子…")
        ok, msg = apply_battle_appear_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        _emit_combo(messages, on_log, msg)
        work = hotfix

    if pet_equip_unlock:
        raise RuntimeError("宠物四装备孔补丁已停用（会导致宠物界面崩溃）")

    verify_hotfix(hotfix)
    return messages, work


def apply_combo(
    *,
    vip: bool = True,
    vip_scale: int = 5,
    vip_non_vip: bool = False,
    vip_echo: float | None = None,
    battle_nine_action: bool = False,
    battle_nine_external: bool = False,
    player_action_magics: bool = False,
    auto_seal_external: bool = False,
    auto_catch_external: bool = False,
    auto_catch_nopet_external: bool = False,
    auto_catch_sell_external: bool = False,
    lv1_auto_external: bool = False,
    auto_sell_external: bool = False,
    plugin_host: bool = False,
    customer_gm: bool = False,
    customer_gm_mode: str = "autoskill",
    map_sprint: bool = False,
    map_sprint_scale: int = 8,
    battle_longpress: bool = False,
    level_one_include_all: bool = True,
    transition_speed: bool = False,
    transition_speed_scale: float = 0.4,
    skill_effect_speed: bool = False,
    skill_effect_scale: float = 2.0,
    pet_equip_unlock: bool = False,
    wiki_download_res: bool = False,
    wiki_label: bool = False,
    daily_claim: bool = True,
    newbie_gift_code: bool = True,
    gift_codes: list[str] | str | None = None,
    boss_key_fps: bool = False,
    wiki_fps: bool = False,
    wiki_test_ui: bool = False,
    battle_appear: bool = False,
    inject_bridge: bool = False,
    from_orig: bool = False,
    game_root: Path | None = None,
    on_log=None,
) -> list[str]:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    messages: list[str] = []

    if from_orig:
        if not orig.is_file():
            raise FileNotFoundError(
                f"缺少原版备份 {orig.name}。请先将 hotfix.dll.bytes 复制为 {orig.name}"
            )
        _emit_combo(messages, on_log, "正在从 .orig 恢复干净 hotfix…")
        shutil.copy2(orig, hotfix)
        _emit_combo(messages, on_log, "已从 .orig 恢复为干净 hotfix，再叠加所选补丁")

    verify_hotfix(hotfix)
    work = hotfix

    if battle_nine_action and battle_nine_external:
        raise RuntimeError("神奇九动 IL原版 与 DLL版 不能同时启用，请只选一项。")

    if wiki_download_res and wiki_label:
        raise RuntimeError("百科→资源下载 与 百科文字→百科1 不能同时启用。")

    if auto_catch_external and auto_catch_nopet_external:
        raise RuntimeError("自动抓宠·DLL 与 自动抓宠·无宠人防御 不能同时启用，请只选一项。")

    # 百科→助手面板时：抓宠/烧卡/遇1级走面板模式（不占百科），可与助手同开
    panel_mode = bool(wiki_test_ui)

    wiki_users = [
        (
            "自动抓宠·DLL",
            (auto_catch_external or auto_catch_nopet_external or auto_catch_sell_external)
            and not panel_mode,
        ),
        ("遇1级自动·DLL", lv1_auto_external and not panel_mode),
        ("自动烧卡·DLL", auto_seal_external and not panel_mode),
        ("盗贼辅助·DLL", auto_sell_external),
        ("插件 Host", plugin_host),
        ("百科→资源下载", wiki_download_res),
        ("百科文字→百科1", wiki_label),
        ("百科限帧", wiki_fps),
        ("百科→助手面板", wiki_test_ui),
    ]
    wiki_on = [name for name, on in wiki_users if on]
    if len(wiki_on) > 1:
        raise RuntimeError(
            "侧栏百科按钮互斥，只能选一类：\n"
            + "、".join(wiki_on)
        )

    if (auto_catch_external or auto_catch_nopet_external) and wiki_download_res and not panel_mode:
        raise RuntimeError("自动抓宠·DLL 已占用侧栏百科按钮，不能同时启用百科→资源下载。")

    if (auto_catch_external or auto_catch_nopet_external) and wiki_label and not panel_mode:
        raise RuntimeError("自动抓宠·DLL 已占用侧栏百科按钮，不能同时启用百科文字→百科1。")

    if auto_seal_external and wiki_download_res and not panel_mode:
        raise RuntimeError("自动烧卡·DLL 已占用侧栏百科按钮，不能同时启用百科→资源下载。")

    if auto_seal_external and wiki_label and not panel_mode:
        raise RuntimeError("自动烧卡·DLL 已占用侧栏百科按钮，不能同时启用百科文字→百科1。")

    if auto_sell_external and wiki_download_res:
        raise RuntimeError("盗贼辅助·DLL 已占用侧栏百科按钮，不能同时启用百科→资源下载。")

    if auto_sell_external and wiki_label:
        raise RuntimeError("盗贼辅助·DLL 已占用侧栏百科按钮，不能同时启用百科文字→百科1。")

    if plugin_host and wiki_download_res:
        raise RuntimeError("插件 Host 已占用侧栏百科按钮，不能同时启用百科→资源下载。")

    if plugin_host and wiki_label:
        raise RuntimeError("插件 Host 已占用侧栏百科按钮，不能同时启用百科文字→百科1。")

    # Pause 互斥：面板模式下抓宠/烧卡/遇1级不占 Pause，可并存；九动 DLL 仍独占 Pause（除非仅面板加载）
    exclusive_flags = [
        ("神奇九动·DLL", battle_nine_external),
        ("自动烧卡·DLL", auto_seal_external and not panel_mode),
        ("自动抓宠·DLL", auto_catch_external and not panel_mode),
        ("自动抓宠·无宠人防御", auto_catch_nopet_external and not panel_mode),
        ("抓宠卖银币·DLL", auto_catch_sell_external and not panel_mode),
        ("遇1级自动·DLL", lv1_auto_external and not panel_mode),
        ("盗贼辅助·DLL", auto_sell_external),
        ("插件 Host", plugin_host),
        ("注入桥接·DLL", inject_bridge),
    ]
    exclusive_on = [name for name, on in exclusive_flags if on]
    if len(exclusive_on) > 1:
        raise RuntimeError(
            "战斗扩展 DLL 互斥（只能勾一类）："
            "神奇九动·DLL / 自动烧卡·DLL / 自动抓宠·DLL / 自动抓宠·无宠人防御 / 遇1级自动·DLL / "
            "盗贼辅助·DLL / 插件 Host / 注入桥接·DLL。\n"
            "（勾选百科→助手面板时，抓宠/烧卡/遇1级改为面板模式，可与助手+九动 DLL 同开；"
            "九动版面板：常规/九动/无宠二动/抓宠/烧卡）\n"
            f"当前同时勾选了：{'、'.join(exclusive_on)}"
        )

    _emit_combo(messages, on_log, "正在做组合余量校验…")
    slack_data, slack_warnings = assert_combo_slack_ok(
        game_root=game_root,
        vip=vip,
        vip_non_vip=vip_non_vip,
        battle_nine_action=battle_nine_action,
        battle_nine_external=battle_nine_external,
        auto_seal_external=auto_seal_external,
        auto_catch_external=auto_catch_external,
        auto_catch_nopet_external=auto_catch_nopet_external,
        lv1_auto_external=lv1_auto_external,
        auto_sell_external=auto_sell_external,
        customer_gm=customer_gm,
        map_sprint=map_sprint,
        battle_longpress=battle_longpress,
        level_one_include_all=level_one_include_all,
        transition_speed=transition_speed,
        skill_effect_speed=skill_effect_speed,
        inject_bridge=inject_bridge,
    )
    if slack_data:
        _emit_combo(messages, on_log, "余量测算:\n" + format_slack_summary(slack_data))
    for w in slack_warnings:
        _emit_combo(messages, on_log, "[余量] " + w)

    gameplay_flags = (
        vip
        or vip_non_vip
        or vip_echo is not None
        or battle_nine_action
        or battle_nine_external
        or player_action_magics
        or auto_seal_external
        or auto_catch_external
        or auto_catch_nopet_external
        or auto_catch_sell_external
        or lv1_auto_external
        or auto_sell_external
        or plugin_host
        or customer_gm
        or map_sprint
        or battle_longpress
        or level_one_include_all
        or transition_speed
        or skill_effect_speed
        or pet_equip_unlock
        or wiki_download_res
        or wiki_label
        or daily_claim
        or newbie_gift_code
        or boss_key_fps
        or wiki_fps
        or wiki_test_ui
        or battle_appear
    )

    patch_kwargs = dict(
        vip=vip,
        vip_scale=vip_scale,
        vip_non_vip=vip_non_vip,
        vip_echo=vip_echo,
        battle_nine_action=battle_nine_action,
        battle_nine_external=battle_nine_external,
        player_action_magics=player_action_magics,
        auto_seal_external=auto_seal_external,
        auto_catch_external=auto_catch_external,
        auto_catch_nopet_external=auto_catch_nopet_external,
        auto_catch_sell_external=auto_catch_sell_external,
        lv1_auto_external=lv1_auto_external,
        auto_sell_external=auto_sell_external,
        plugin_host=plugin_host,
        customer_gm=customer_gm,
        customer_gm_mode=customer_gm_mode,
        map_sprint=map_sprint,
        map_sprint_scale=map_sprint_scale,
        battle_longpress=battle_longpress,
        level_one_include_all=level_one_include_all,
        transition_speed=transition_speed,
        transition_speed_scale=transition_speed_scale,
        skill_effect_speed=skill_effect_speed,
        skill_effect_scale=skill_effect_scale,
        pet_equip_unlock=pet_equip_unlock,
        wiki_download_res=wiki_download_res,
        wiki_label=wiki_label,
        daily_claim=daily_claim,
        newbie_gift_code=newbie_gift_code,
        gift_codes=gift_codes,
        boss_key_fps=boss_key_fps,
        wiki_fps=wiki_fps,
        wiki_test_ui=wiki_test_ui,
        battle_appear=battle_appear,
        on_log=on_log,
    )

    if inject_bridge:
        from bridge_inject import apply_bridge_patch

        _emit_combo(messages, on_log, "桥接：先在干净 .orig 上注入（玩法补丁随后叠加）…")
        ok, msg = apply_bridge_patch(game_root, force_from_orig=True)
        if not ok:
            raise RuntimeError(msg)
        variant = detect_bridge_variant(game_root)
        label = bridge_variant_label(variant)
        summary = msg.splitlines()[0] if msg else "助手桥接注入成功"
        _emit_combo(messages, on_log, f"桥接：{summary}" + (f"（{label}）" if label else ""))

        if gameplay_flags:
            patch_msgs, work = _apply_gameplay_patches(hotfix, orig, **patch_kwargs)
            messages.extend(patch_msgs)
    elif gameplay_flags:
        patch_msgs, work = _apply_gameplay_patches(hotfix, orig, **patch_kwargs)
        messages.extend(patch_msgs)

    state = {
        "vip": vip,
        "vip_non_vip": vip_non_vip,
        "vip_scale": vip_scale,
        "vip_echo": vip_echo,
        "battle_nine_action": battle_nine_action,
        "battle_nine_external": battle_nine_external,
        "player_action_magics": player_action_magics,
        "auto_seal_external": auto_seal_external,
        "auto_catch_external": auto_catch_external,
        "auto_catch_nopet_external": auto_catch_nopet_external,
        "auto_catch_sell_external": auto_catch_sell_external,
        "lv1_auto_external": lv1_auto_external,
        "auto_sell_external": auto_sell_external,
        "plugin_host": plugin_host,
        "customer_gm": customer_gm,
        "customer_gm_mode": customer_gm_mode if customer_gm else "",
        "map_sprint": map_sprint,
        "map_sprint_scale": map_sprint_scale if map_sprint else 0,
        "battle_longpress": battle_longpress,
        "level_one_include_all": level_one_include_all,
        "transition_speed": transition_speed,
        "transition_speed_scale": transition_speed_scale if transition_speed else 0,
        "skill_effect_speed": skill_effect_speed,
        "skill_effect_scale": skill_effect_scale if skill_effect_speed else 0,
        "pet_equip_unlock": pet_equip_unlock,
        "wiki_download_res": wiki_download_res,
        "wiki_label": wiki_label,
        "daily_claim": daily_claim,
        "newbie_gift_code": newbie_gift_code,
        "gift_codes": normalize_gift_codes(gift_codes) if newbie_gift_code else [],
        "boss_key_fps": boss_key_fps,
        "wiki_fps": wiki_fps,
        "wiki_test_ui": wiki_test_ui,
        "battle_appear": battle_appear,
        "inject_bridge": inject_bridge,
        "bridge_patched": is_bridge_patched(game_root),
        "bridge_variant": detect_bridge_variant(game_root),
        "sha256": sha256_file(hotfix),
        "size": hotfix.stat().st_size if hotfix.is_file() else EXPECTED_SIZE,
        "game_root": str(game_root or get_game_root() or ""),
    }
    STATE_PATH.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    try:
        mark_hotfix_watch_stamp(game_root, marked_by="apply")
        _emit_combo(messages, on_log, "已标记当前 hotfix 指纹（供下次检测客户端更新）")
    except Exception as exc:
        _emit_combo(messages, on_log, f"警告：标记 hotfix 指纹失败（{exc}）")
    return messages


def restore_hotfix(game_root: Path | None = None) -> None:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    if not orig.is_file():
        raise FileNotFoundError(f"找不到原版备份: {orig}")
    shutil.copy2(orig, hotfix)
    if STATE_PATH.exists():
        STATE_PATH.unlink()


def get_status(game_root: Path | None = None) -> dict:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    expected = effective_expected_size(game_root)
    status = {
        "hotfix_exists": hotfix.is_file(),
        "orig_exists": orig.is_file(),
        "size_ok": False,
        "expected_size": expected,
        "customer_gm_mode": "unknown",
    }
    if hotfix.is_file():
        status["size"] = hotfix.stat().st_size
        status["size_ok"] = status["size"] == expected
        try:
            status["customer_gm_mode"] = detect_customer_gm_mode(hotfix)
        except Exception:
            pass
    if orig.is_file():
        status["orig_size"] = orig.stat().st_size
        status["orig_size_ok"] = status["orig_size"] == expected
    if STATE_PATH.is_file():
        status["last_combo"] = json.loads(STATE_PATH.read_text(encoding="utf-8"))
    try:
        status["bridge_patched"] = is_bridge_patched(game_root)
        status["bridge_variant"] = detect_bridge_variant(game_root)
    except Exception:
        status["bridge_patched"] = False
        status["bridge_variant"] = "unknown"
    try:
        status["client_update"] = get_update_status(game_root)
    except Exception as exc:
        status["client_update"] = {"error": str(exc)}
    return status


def main() -> int:
    parser = argparse.ArgumentParser(description="魔力宝贝：序章 热补丁")
    parser.add_argument("--from-orig", action="store_true", help="从 .orig 干净底稿再打所选补丁")
    parser.add_argument("--no-vip", action="store_true")
    parser.add_argument(
        "--no-battle-nine-action",
        action="store_true",
        help="不打神奇九动补丁",
    )
    parser.add_argument("--vip-non-vip", action="store_true", help="非VIP同样倍速")
    parser.add_argument("--vip-scale", type=int, choices=[3, 5, 10], default=5)
    parser.add_argument("--customer-gm", action="store_true", help="客服按钮改开：盲盒/秘宝/讨伐令/试炼/水晶/自动技能")
    parser.add_argument("--customer-gm-mode", choices=CUSTOMER_GM_MODES, default="autoskill")
    parser.add_argument("--map-sprint", action="store_true", help="Sprint 跑速 8/10/12")
    parser.add_argument("--map-sprint-scale", type=int, choices=[8, 10, 12], default=8)
    parser.add_argument(
        "--battle-longpress",
        action="store_true",
        help="任意战斗类型长按单位可打开 BattleMessageTips 详情",
    )
    parser.add_argument(
        "--level-one-include-all",
        action="store_true",
        help="遇敌一级停止：哥布林/迷你蝙蝠也计入（排除常量改为无效 ID）",
    )
    parser.add_argument(
        "--transition-speed",
        action="store_true",
        help="加速过场：进出战斗 CrossBlocks 0.4/0.2/0.1s",
    )
    parser.add_argument(
        "--transition-speed-scale",
        type=float,
        choices=[0.4, 0.2, 0.1],
        default=0.4,
        help="过场时长：0.4=快 0.2=很快 0.1=飞快",
    )
    parser.add_argument(
        "--skill-effect-speed",
        action="store_true",
        help="战斗技能特效帧动画 1.5/2/3/5 倍速（不影响回合读秒）",
    )
    parser.add_argument(
        "--skill-effect-scale",
        type=float,
        choices=[1.5, 2.0, 3.0, 5.0],
        default=2.0,
    )
    parser.add_argument(
        "--inject-bridge",
        action="store_true",
        help="叠加补丁后注入序章助手桥接",
    )
    parser.add_argument(
        "--pet-equip-unlock",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--wiki-download-res",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--wiki-label",
        action="store_true",
        help="侧栏百科点击后按钮文字变为「百科1」",
    )
    parser.add_argument("--sniff-gm", action="store_true", help="嗅探 GM 面板与客服入口")
    parser.add_argument("--restore", action="store_true")
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--ensure-orig", action="store_true", help="若缺少 .orig 则从当前 hotfix 创建")
    args = parser.parse_args()

    try:
        if args.ensure_orig:
            path = ensure_orig_backup()
            print(f"[OK] 原版备份: {path}")
            return 0
        sniff_target = hotfix_orig() if hotfix_orig().is_file() else hotfix_path()
        if args.sniff_gm:
            ok, out = sniff_customer_gm(sniff_target)
            print(out)
            return 0 if ok else 1
        if args.status:
            print(json.dumps(get_status(), ensure_ascii=False, indent=2))
            return 0
        if args.restore:
            restore_hotfix()
            print("[OK] 已恢复原版 hotfix.dll.bytes")
            return 0
        msgs = apply_combo(
            vip=not args.no_vip,
            vip_non_vip=args.vip_non_vip,
            vip_scale=args.vip_scale,
            battle_nine_action=not args.no_battle_nine_action,
            customer_gm=args.customer_gm,
            customer_gm_mode=args.customer_gm_mode,
            map_sprint=args.map_sprint,
            map_sprint_scale=args.map_sprint_scale,
            battle_longpress=args.battle_longpress,
            level_one_include_all=args.level_one_include_all,
            transition_speed=args.transition_speed,
            transition_speed_scale=args.transition_speed_scale,
            skill_effect_speed=args.skill_effect_speed,
            skill_effect_scale=args.skill_effect_scale,
            pet_equip_unlock=args.pet_equip_unlock,
            wiki_download_res=args.wiki_download_res,
            wiki_label=args.wiki_label,
            inject_bridge=args.inject_bridge,
            from_orig=args.from_orig,
        )
        for m in msgs:
            print(f"[OK] {m}")
        return 0
    except Exception as exc:
        print(f"[FAIL] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
