# -*- coding: utf-8 -*-
"""hotfix .text VA 间隙余量测算（调用 HotfixPatcher slack-report）。"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from patch_common import hotfix_orig, hotfix_path, run_patcher_capture


def slack_report(
    hotfix: Path | None = None,
    *,
    game_root: Path | None = None,
    check: list[str] | None = None,
    prefer_orig: bool = True,
) -> dict[str, Any]:
    """返回 slack-report JSON。prefer_orig 时优先用 .orig 测干净余量。"""
    target = hotfix
    if target is None:
        orig = hotfix_orig(game_root)
        cur = hotfix_path(game_root)
        if prefer_orig and orig.is_file():
            target = orig
        else:
            target = cur

    args = ["slack-report", "--hotfix", str(target), "--json"]
    if check:
        args.extend(["--check", ",".join(check)])

    proc = run_patcher_capture(args)
    out = (proc.stdout or "").strip()
    if not out:
        raise RuntimeError((proc.stderr or "").strip() or "slack-report 无输出")

    start = out.find("{")
    if start < 0:
        raise RuntimeError(out)
    data = json.loads(out[start:])
    data["_returncode"] = proc.returncode
    data["_hotfix_used"] = str(target)
    return data


def format_slack_summary(data: dict[str, Any]) -> str:
    lines = [
        f".text 可用追加 usable={data.get('usable_append_bytes')} "
        f"(va_gap={data.get('va_gap_bytes')}, raw_slack={data.get('raw_slack_bytes')})",
        f"测算源: {data.get('_hotfix_used', '')}",
    ]
    for p in data.get("patches") or []:
        mark = (
            "已打"
            if p.get("already")
            else (
                "可打"
                if p.get("can_apply")
                else ("需DLL版" if p.get("mode") == "external_dll" else "不够")
            )
        )
        note = p.get("note") or ""
        lines.append(
            f"  [{mark}] {p.get('id')}: +{p.get('growth_bytes')}B ({p.get('mode')}) {p.get('name')}"
            + (f" — {note}" if note else "")
        )
    rem = data.get("remaining_after_check")
    if rem is not None:
        lines.append(f"测算后剩余: {rem}B")
    lines.append("总体: " + ("足够" if data.get("all_can_apply") else "不足/需DLL版"))
    return "\n".join(lines)


def combo_check_ids(
    *,
    vip: bool = False,
    vip_non_vip: bool = False,
    battle_nine_action: bool = False,
    battle_nine_external: bool = False,
    customer_gm: bool = False,
    map_sprint: bool = False,
    battle_longpress: bool = False,
    skill_effect_speed: bool = False,
    inject_bridge: bool = False,
) -> list[str]:
    ids: list[str] = []
    if vip or vip_non_vip:
        ids.append("vip")
    if battle_nine_action:
        ids.append("nine")
    if battle_nine_external:
        ids.append("nine_external")
    if customer_gm:
        ids.append("customer_gm")
    if map_sprint:
        ids.append("sprint")
    if battle_longpress:
        ids.append("longpress")
    if skill_effect_speed:
        ids.append("skill_effect")
    if inject_bridge:
        ids.append("bridge")
    return ids


def assert_combo_slack_ok(
    *,
    game_root: Path | None = None,
    vip: bool = False,
    vip_non_vip: bool = False,
    battle_nine_action: bool = False,
    battle_nine_external: bool = False,
    customer_gm: bool = False,
    map_sprint: bool = False,
    battle_longpress: bool = False,
    skill_effect_speed: bool = False,
    inject_bridge: bool = False,
) -> tuple[dict[str, Any], list[str]]:
    """
    应用前测算。返回 (report, warnings)。
    IL 九动若间隙不足：硬失败，请改用神奇九动·DLL版。
    """
    ids = combo_check_ids(
        vip=vip,
        vip_non_vip=vip_non_vip,
        battle_nine_action=battle_nine_action,
        battle_nine_external=battle_nine_external,
        customer_gm=customer_gm,
        map_sprint=map_sprint,
        battle_longpress=battle_longpress,
        skill_effect_speed=skill_effect_speed,
        inject_bridge=inject_bridge,
    )
    if not ids:
        return {}, []

    data = slack_report(game_root=game_root, check=ids, prefer_orig=True)
    warnings: list[str] = []
    hard_fail: list[str] = []

    va_gap = int(data.get("va_gap_bytes") or 0)
    usable = int(data.get("usable_append_bytes") or 0)

    for p in data.get("patches") or []:
        if p.get("already") or p.get("can_apply"):
            continue
        pid = p.get("id")
        growth = int(p.get("growth_bytes") or 0)
        # IL 九动：slack-report 的 usable 常受 raw_slack 限制偏紧；
        # BinaryPeWriter 可从 .reloc/.rsrc 压缩腾 raw，只要 VA 间隙够即可。
        if pid in ("nine", "nine_queue") and p.get("mode") != "external_dll":
            if growth > 0 and va_gap >= growth:
                warnings.append(
                    f"神奇九动(IL) 测算 usable={usable}B < {growth}B，但 va_gap={va_gap}B 足够，"
                    f"将依赖节区压缩扩 raw 后写入。"
                )
                continue
            hard_fail.append(
                f"神奇九动(IL) 间隙不足（需 {growth}B，va_gap={va_gap}B / usable={usable}B）。"
                f"请改用「神奇九动·DLL版」，或等客户端 .text 余量增大后再用 IL 版。"
            )
            continue
        if pid in ("nine", "nine_queue") and p.get("mode") == "external_dll":
            hard_fail.append(
                f"神奇九动(IL) 间隙不足（需 {p.get('growth_bytes')}B，可用 {usable}B）。"
                f"请改用「神奇九动·DLL版」，或等客户端 .text 余量增大后再用 IL 版。"
            )
            continue
        hard_fail.append(f"{p.get('name')}({pid}): +{p.get('growth_bytes')}B — {p.get('note')}")

    if hard_fail:
        raise RuntimeError(
            "补丁余量不足，已中止：\n"
            + "\n".join(hard_fail)
            + "\n\n"
            + format_slack_summary(data)
        )

    return data, warnings
