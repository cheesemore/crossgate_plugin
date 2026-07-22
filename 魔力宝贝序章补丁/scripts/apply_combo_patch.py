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


def apply_vip(
    hotfix: Path,
    source: Path,
    scale: int = 5,
    *,
    vip_branch: bool = True,
    non_vip: bool = False,
) -> tuple[bool, str]:
    args = [
        "vip-timescale-patch",
        "--hotfix",
        str(source),
        "--output",
        str(hotfix),
        "--scale",
        str(scale),
    ]
    if non_vip and not vip_branch:
        args.append("--non-vip-only")
    elif non_vip:
        args.append("--non-vip")
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
    return True, "战斗倍速：" + "、".join(parts)


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


def _apply_gameplay_patches(
    hotfix: Path,
    orig: Path,
    *,
    vip: bool,
    vip_scale: int,
    vip_non_vip: bool,
    battle_nine_action: bool,
    battle_nine_external: bool,
    customer_gm: bool,
    customer_gm_mode: str,
    map_sprint: bool,
    map_sprint_scale: int,
    battle_longpress: bool,
    transition_speed: bool,
    transition_speed_scale: float,
    skill_effect_speed: bool,
    skill_effect_scale: float,
    pet_equip_unlock: bool,
) -> tuple[list[str], Path]:
    """在现有 hotfix 上叠加玩法补丁（不还原 .orig）。返回 (messages, work_path)。"""
    messages: list[str] = []
    work = hotfix

    if battle_nine_external:
        ok, msg = apply_battle_nine_external(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
        work = hotfix

    if vip or vip_non_vip:
        if vip_scale not in (3, 5, 10):
            raise ValueError("vip_scale 须为 3、5 或 10")
        ok, msg = apply_vip(hotfix, work, vip_scale, vip_branch=vip, non_vip=vip_non_vip)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
        work = hotfix

    if battle_nine_action:
        ok, msg = apply_battle_nine_action(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
        work = hotfix

    if customer_gm:
        sniff_target = orig if orig.is_file() else hotfix
        ok, sniff_out = sniff_customer_gm(sniff_target)
        if not ok:
            raise RuntimeError(f"客服入口嗅探失败:\n{sniff_out}")
        ok, msg = apply_customer_gm(hotfix, normalize_customer_gm_mode(customer_gm_mode))
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)

    if map_sprint:
        if map_sprint_scale not in MAP_SPRINT_SCALES:
            raise ValueError("map_sprint_scale 须为 8、10 或 12")
        ok, msg = apply_map_sprint(hotfix, map_sprint_scale)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)

    if battle_longpress:
        ok, msg = apply_battle_longpress(hotfix, work)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
        work = hotfix

    if transition_speed:
        if transition_speed_scale not in TRANSITION_SPEED_SCALES:
            raise ValueError("transition_speed_scale 须为 0.4、0.2 或 0.1")
        ok, msg = apply_transition_speed(hotfix, work, transition_speed_scale)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
        work = hotfix

    if skill_effect_speed:
        if skill_effect_scale not in SKILL_EFFECT_SCALES:
            raise ValueError("skill_effect_scale 须为 1.5、2、3 或 5")
        ok, msg = apply_skill_effect_speed(hotfix, work, skill_effect_scale)
        if not ok:
            raise RuntimeError(msg)
        messages.append(msg)
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
    battle_nine_action: bool = False,
    battle_nine_external: bool = False,
    customer_gm: bool = False,
    customer_gm_mode: str = "autoskill",
    map_sprint: bool = False,
    map_sprint_scale: int = 8,
    battle_longpress: bool = False,
    transition_speed: bool = False,
    transition_speed_scale: float = 0.4,
    skill_effect_speed: bool = False,
    skill_effect_scale: float = 2.0,
    pet_equip_unlock: bool = False,
    inject_bridge: bool = False,
    from_orig: bool = False,
    game_root: Path | None = None,
) -> list[str]:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    messages: list[str] = []

    if from_orig:
        if not orig.is_file():
            raise FileNotFoundError(
                f"缺少原版备份 {orig.name}。请先将 hotfix.dll.bytes 复制为 {orig.name}"
            )
        shutil.copy2(orig, hotfix)
        messages.append("已从 .orig 恢复为干净 hotfix，再叠加所选补丁")

    verify_hotfix(hotfix)
    work = hotfix

    if battle_nine_action and battle_nine_external:
        raise RuntimeError("神奇九动 IL原版 与 DLL版 不能同时启用，请只选一项。")
    if inject_bridge and (battle_nine_action or battle_nine_external):
        raise RuntimeError(
            "九动与助手桥接不能同时启用（共用 OnApplicationPause / .text 余量），请二选一。"
        )

    slack_data, slack_warnings = assert_combo_slack_ok(
        game_root=game_root,
        vip=vip,
        vip_non_vip=vip_non_vip,
        battle_nine_action=battle_nine_action,
        battle_nine_external=battle_nine_external,
        customer_gm=customer_gm,
        map_sprint=map_sprint,
        battle_longpress=battle_longpress,
        transition_speed=transition_speed,
        skill_effect_speed=skill_effect_speed,
        inject_bridge=inject_bridge,
    )
    if slack_data:
        messages.append("余量测算:\n" + format_slack_summary(slack_data))
    for w in slack_warnings:
        messages.append("[余量] " + w)

    gameplay_flags = (
        vip
        or vip_non_vip
        or battle_nine_action
        or battle_nine_external
        or customer_gm
        or map_sprint
        or battle_longpress
        or transition_speed
        or skill_effect_speed
        or pet_equip_unlock
    )

    patch_kwargs = dict(
        vip=vip,
        vip_scale=vip_scale,
        vip_non_vip=vip_non_vip,
        battle_nine_action=battle_nine_action,
        battle_nine_external=battle_nine_external,
        customer_gm=customer_gm,
        customer_gm_mode=customer_gm_mode,
        map_sprint=map_sprint,
        map_sprint_scale=map_sprint_scale,
        battle_longpress=battle_longpress,
        transition_speed=transition_speed,
        transition_speed_scale=transition_speed_scale,
        skill_effect_speed=skill_effect_speed,
        skill_effect_scale=skill_effect_scale,
        pet_equip_unlock=pet_equip_unlock,
    )

    if inject_bridge:
        from bridge_inject import apply_bridge_patch

        messages.append("桥接：先在干净 .orig 上注入（玩法补丁随后叠加）…")
        ok, msg = apply_bridge_patch(game_root, force_from_orig=True)
        if not ok:
            raise RuntimeError(msg)
        variant = detect_bridge_variant(game_root)
        label = bridge_variant_label(variant)
        summary = msg.splitlines()[0] if msg else "助手桥接注入成功"
        messages.append(f"桥接：{summary}" + (f"（{label}）" if label else ""))

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
        "battle_nine_action": battle_nine_action,
        "battle_nine_external": battle_nine_external,
        "customer_gm": customer_gm,
        "customer_gm_mode": customer_gm_mode if customer_gm else "",
        "map_sprint": map_sprint,
        "map_sprint_scale": map_sprint_scale if map_sprint else 0,
        "battle_longpress": battle_longpress,
        "transition_speed": transition_speed,
        "transition_speed_scale": transition_speed_scale if transition_speed else 0,
        "skill_effect_speed": skill_effect_speed,
        "skill_effect_scale": skill_effect_scale if skill_effect_speed else 0,
        "pet_equip_unlock": pet_equip_unlock,
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
        messages.append("已标记当前 hotfix 指纹（供下次检测客户端更新）")
    except Exception as exc:
        messages.append(f"警告：标记 hotfix 指纹失败（{exc}）")
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
            transition_speed=args.transition_speed,
            transition_speed_scale=args.transition_speed_scale,
            skill_effect_speed=args.skill_effect_speed,
            skill_effect_scale=args.skill_effect_scale,
            pet_equip_unlock=args.pet_equip_unlock,
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
