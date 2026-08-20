#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章日常工作流入口（固化、少思考）。

用法（在游戏仓库根目录）：
  python tools/workflow.py status
  python tools/workflow.py update --dry-run
  python tools/workflow.py update
  python tools/workflow.py update --confirm-anticheat
  python tools/workflow.py repatch
  python tools/workflow.py publish-foolproof
  python tools/workflow.py publish-all

说明：
  status            cross / crosscopy / 补丁状态一览
  update            客户端更新一条龙（调用 cross_update.auto-update）
  repatch           用 DEFAULT_COMBO_KWARGS 从 .orig 重打 cross（需关游戏）
  publish-foolproof 打傻瓜补丁融合版（龙族护航已卸载）
  publish-all       按 publish_packs.json 默认清单发布
"""
from __future__ import annotations

import argparse
import importlib
import json
import subprocess
import sys
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parent
SCRIPTS = ROOT / "魔力宝贝序章补丁" / "scripts"


def _ensure_scripts_path() -> None:
    s = str(SCRIPTS)
    if s not in sys.path:
        sys.path.insert(0, s)


def _run(cmd: list[str], *, cwd: Path | None = None) -> int:
    print("[CMD]", " ".join(cmd), flush=True)
    return subprocess.call(cmd, cwd=str(cwd or ROOT))


def cmd_status(_: argparse.Namespace) -> int:
    rc = _run([sys.executable, str(TOOLS / "cross_update.py"), "status"])
    _ensure_scripts_path()
    try:
        from apply_combo_patch import get_status
        from bridge_inject import detect_bridge_variant, is_bridge_patched
        from patch_common import hotfix_path

        st = get_status(ROOT)
        lc = st.get("last_combo") or {}
        hf = hotfix_path(ROOT)
        flag = hf.parent / "seqchapter_dragon_loop.flag"
        print("\n--- 补丁摘要 ---")
        print(f"hotfix size_ok={st.get('size_ok')} size={st.get('size')}")
        print(f"bridge={is_bridge_patched(ROOT)} ({detect_bridge_variant(ROOT)})")
        print(f"dragon_loop_flag={flag.is_file()}")
        print(f"inject_bridge={lc.get('inject_bridge')} dragon_loop_ui={lc.get('dragon_loop_ui')}")
        print(f"kill_timescale_report={lc.get('kill_timescale_report')} wiki_test_ui={lc.get('wiki_test_ui')}")
        print(f"daily_claim={lc.get('daily_claim')} customer_gm={lc.get('customer_gm')}/{lc.get('customer_gm_mode')}")
    except Exception as exc:
        print(f"[WARN] 补丁状态读取失败: {exc}")
    return rc


def cmd_update(args: argparse.Namespace) -> int:
    cmd = [sys.executable, str(TOOLS / "cross_update.py"), "auto-update"]
    if args.dry_run:
        cmd.append("--dry-run")
    if args.confirm_anticheat:
        cmd.append("--confirm-anticheat")
    if args.force:
        cmd.append("--force")
    return _run(cmd)


def cmd_repatch(_: argparse.Namespace) -> int:
    _ensure_scripts_path()
    from apply_combo_patch import apply_combo
    from patch_defaults import DEFAULT_COMBO_KWARGS

    kw = dict(DEFAULT_COMBO_KWARGS)
    print("[INFO] 使用 DEFAULT_COMBO_KWARGS 从 .orig 重打…", flush=True)
    print(json.dumps({k: kw[k] for k in (
        "inject_bridge", "dragon_loop_ui", "kill_timescale_report",
        "wiki_test_ui", "daily_claim", "customer_gm", "customer_gm_mode",
        "combat_accel", "vip", "from_orig",
    ) if k in kw}, ensure_ascii=False, indent=2), flush=True)
    msgs = apply_combo(**kw)
    for line in msgs[-12:]:
        print(line)
    print("[OK] repatch 完成")
    return 0


def cmd_publish_foolproof(_: argparse.Namespace) -> int:
    return _run([sys.executable, str(SCRIPTS / "publish_foolproof.py")], cwd=SCRIPTS.parent)


def cmd_publish_all(_: argparse.Namespace) -> int:
    return _run([sys.executable, str(SCRIPTS / "publish_default_packs.py")], cwd=SCRIPTS.parent)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="序章日常工作流（固化入口）")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status", help="cross/crosscopy/补丁状态")

    p_up = sub.add_parser("update", help="客户端更新一条龙（cross_update auto-update）")
    p_up.add_argument("--dry-run", action="store_true")
    p_up.add_argument("--confirm-anticheat", action="store_true")
    p_up.add_argument("--force", action="store_true")

    sub.add_parser("repatch", help="默认组合重打 cross")
    sub.add_parser("publish-foolproof", help="发布傻瓜补丁融合版")
    sub.add_parser("publish-all", help="按 publish_packs.json 发布")

    args = p.parse_args(argv)
    handlers = {
        "status": cmd_status,
        "update": cmd_update,
        "repatch": cmd_repatch,
        "publish-foolproof": cmd_publish_foolproof,
        "publish-all": cmd_publish_all,
    }
    return handlers[args.cmd](args)


if __name__ == "__main__":
    raise SystemExit(main())
