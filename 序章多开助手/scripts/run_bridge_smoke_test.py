#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
桥接自动化自测（单开，无 GUI）。

流程：清理进程 →（可选）注入 → 启动 →（--login 登录 → --enter-game 进游戏
      → --multi-login 拉起其余角色 → --summon 一键召唤）→ 诊断

用法:
  python run_bridge_smoke_test.py --inject --login
  python run_bridge_smoke_test.py --inject --login --enter-game
  python run_bridge_smoke_test.py --inject --login --enter-game --multi-login --summon

退出码:
  0  通过
  1  闪退  2  黑屏  3  错误  4  注入失败  5  无凭据
  6  登录失败（3 次重试后）  7  进入游戏失败（3 次重试后）
  8  多控拉起失败  9  召唤/组队失败
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHARED = ROOT / "序章助手共享"
if str(SHARED) not in sys.path:
    sys.path.insert(0, str(SHARED))

from assistant_common.smoke_test import (  # noqa: E402
    SmokeTestConfig,
    print_result_summary,
    run_smoke_test,
)


def main() -> int:
    parser = argparse.ArgumentParser(description="序章桥接冒烟测试（单开）")
    parser.add_argument("--pre-delay", type=float, default=10.0, help="启动游戏前等待秒数")
    parser.add_argument("--hold", type=float, default=20.0, help="非 --login 时启动后观察秒数")
    parser.add_argument("--inject", action="store_true", help="测试前从 .orig 注入桥接")
    parser.add_argument("--login", action="store_true", help="桥接就绪后登录（关公告+发 login）")
    parser.add_argument(
        "--enter-game",
        action="store_true",
        help="登录成功后自动 enter_game 并进游戏（需 --login）",
    )
    parser.add_argument(
        "--multi-login",
        action="store_true",
        help="进游戏后逐个拉起其余多控角色，每角色最多 3 次（需 --enter-game）",
    )
    parser.add_argument(
        "--summon",
        action="store_true",
        help="多控上线后一键召唤并检查组队，最多 3 次（需 --enter-game）",
    )
    parser.add_argument("--phone", type=str, default="", help="登录手机号")
    parser.add_argument("--password", type=str, default="", help="登录密码")
    parser.add_argument("--bridge-wait", type=float, default=120.0, help="等待桥接心跳秒数")
    parser.add_argument("--login-ui-delay", type=float, default=90.0, help="等待登录 UI 秒数")
    parser.add_argument("--login-ack-timeout", type=float, default=30.0, help="登录 ack 超时")
    parser.add_argument("--route-wait", type=float, default=120.0, help="等待选角界面秒数")
    parser.add_argument("--enter-game-timeout", type=float, default=180.0, help="等待进游戏秒数")
    parser.add_argument("--step-retries", type=int, default=3, help="每步最大重试次数")
    parser.add_argument("--step-retry-delay", type=float, default=3.0, help="步骤重试间隔秒数")
    parser.add_argument("--multi-info-wait", type=float, default=90.0, help="等待 MultiInfo 秒数")
    parser.add_argument("--multi-char-wait", type=float, default=90.0, help="单角色上线等待秒数")
    parser.add_argument("--team-wait", type=float, default=90.0, help="召唤后等待组队秒数")
    parser.add_argument("--no-kill-before", action="store_true", help="不预先结束已有 cg37")
    parser.add_argument("--no-kill-after", action="store_true", help="测试后保留游戏进程")
    parser.add_argument(
        "--kill-after",
        action="store_true",
        help="与 --login 联用时测试后关闭游戏",
    )
    parser.add_argument("--report-dir", type=str, default="", help="报告目录")
    args = parser.parse_args()

    if args.enter_game and not args.login:
        parser.error("--enter-game 需要同时指定 --login")
    if args.multi_login and not args.enter_game:
        parser.error("--multi-login 需要同时指定 --enter-game")
    if args.summon and not args.enter_game:
        parser.error("--summon 需要同时指定 --enter-game")

    if args.login:
        kill_after = args.kill_after
    else:
        kill_after = not args.no_kill_after

    cfg = SmokeTestConfig(
        pre_launch_delay_sec=args.pre_delay,
        hold_sec=args.hold,
        inject_before=args.inject,
        kill_existing_game=not args.no_kill_before,
        kill_game_after=kill_after,
        login_send=args.login,
        enter_game_send=args.enter_game,
        multi_login_send=args.multi_login,
        summon_send=args.summon,
        login_phone=args.phone,
        login_password=args.password,
        bridge_wait_sec=args.bridge_wait,
        login_ui_delay_sec=args.login_ui_delay,
        login_ack_timeout_sec=args.login_ack_timeout,
        route_wait_sec=args.route_wait,
        enter_game_timeout_sec=args.enter_game_timeout,
        step_max_retries=max(1, args.step_retries),
        step_retry_delay_sec=args.step_retry_delay,
        multi_info_wait_sec=args.multi_info_wait,
        multi_char_wait_sec=args.multi_char_wait,
        team_wait_sec=args.team_wait,
    )
    if args.report_dir.strip():
        cfg.report_dir = Path(args.report_dir).expanduser().resolve()

    result = run_smoke_test(cfg)
    print_result_summary(result)
    return result.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
