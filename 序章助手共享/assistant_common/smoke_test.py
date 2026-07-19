#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""桥接自动化冒烟测试（单开、无 GUI）。"""
from __future__ import annotations

import json
import struct
import subprocess
import sys
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

from .accounts import load_accounts
from .config import DATA_DIR, get_game_root
from .game import find_game_processes, launch_game
from .ipc import (
    bridge_alive,
    diagnose_bridge,
    enter_game as ipc_enter_game,
    login as ipc_login,
    multi_login_char as ipc_multi_login_char,
    fetch_multi_info as ipc_fetch_multi_info,
    click_multi_head as ipc_click_multi_head,
    select_multi_char as ipc_select_multi_char,
    close_share_panel as ipc_close_share_panel,
    one_key_summon as ipc_one_key_summon,
    read_state,
    state_path,
    wait_ack,
    wait_for_bridge,
)
from .subprocess_win import no_window_flags


@dataclass
class SmokeTestConfig:
    pre_launch_delay_sec: float = 10.0
    hold_sec: float = 20.0
    inject_before: bool = False
    force_inject_from_orig: bool = True
    kill_existing_game: bool = True
    kill_game_after: bool = True
    report_dir: Path = field(default_factory=lambda: DATA_DIR / "smoke_reports")
    black_mean_luma_threshold: float = 18.0
    black_dark_ratio_threshold: float = 0.92
    player_log_glob: str = "Player.log"
    login_send: bool = False
    login_phone: str = ""
    login_password: str = ""
    bridge_wait_sec: float = 120.0
    login_ui_delay_sec: float = 90.0
    login_ack_timeout_sec: float = 30.0
    enter_game_send: bool = False
    route_wait_sec: float = 120.0
    enter_game_ack_timeout_sec: float = 30.0
    enter_game_timeout_sec: float = 180.0
    step_max_retries: int = 3
    step_retry_delay_sec: float = 3.0
    multi_login_send: bool = False
    summon_send: bool = False
    multi_info_wait_sec: float = 90.0
    multi_char_wait_sec: float = 90.0
    team_wait_sec: float = 90.0
    multi_login_ack_timeout_sec: float = 30.0
    summon_ack_timeout_sec: float = 30.0


@dataclass
class SmokeTestResult:
    ok: bool
    exit_code: int
    started_at: str
    finished_at: str
    instance_id: str = ""
    pid: int | None = None
    game_running_after_hold: bool = False
    crashed: bool = False
    black_screen: bool = False
    black_screen_score: dict[str, float] = field(default_factory=dict)
    layer0_ok: bool = False
    bridge_alive: bool = False
    has_state_json: bool = False
    state_phase: str = ""
    main_uid: str = ""
    patch_variant: str = ""
    screenshot_path: str = ""
    player_log_tail: list[str] = field(default_factory=list)
    player_log_hits: list[str] = field(default_factory=list)
    diagnose_summary: str = ""
    diagnose_detail: str = ""
    diagnose_lines: list[str] = field(default_factory=list)
    diagnose: dict[str, Any] = field(default_factory=dict)
    state_snapshot: dict[str, Any] = field(default_factory=dict)
    login_attempted: bool = False
    login_ack_ok: bool = False
    login_ack_msg: str = ""
    login_req_id: str = ""
    enter_game_attempted: bool = False
    enter_game_ack_ok: bool = False
    enter_game_msg: str = ""
    enter_game_req_id: str = ""
    in_game_ok: bool = False
    login_ok: bool = False
    multi_login_ok: bool = False
    summon_ok: bool = False
    team_ok: bool = False
    failed_step: str = ""
    step_attempts: list[dict[str, Any]] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _log(msg: str) -> None:
    ts = datetime.now().strftime("%H:%M:%S")
    line = f"[{ts}] {msg}"
    print(line, flush=True)


def _resolve_login_credentials(cfg: SmokeTestConfig) -> tuple[str, str]:
    phone = (cfg.login_phone or "").strip()
    password = cfg.login_password or ""
    if phone and password:
        return phone, password
    accounts = load_accounts()
    if accounts:
        return accounts[0].phone, accounts[0].password
    return "", ""


def _snapshot_state(instance_id: str) -> dict[str, Any]:
    st = read_state(instance_id) or {}
    return {
        "phase": st.get("phase"),
        "main_uid": st.get("main_uid"),
        "select_uid": st.get("select_uid"),
        "workflow_active": st.get("workflow_active"),
        "workflow_current": st.get("workflow_current"),
        "workflow_steps": st.get("workflow_steps"),
        "workflow_error": st.get("workflow_error"),
        "workflow_done": st.get("workflow_done"),
        "heartbeat_ts": st.get("heartbeat_ts"),
        "net_ready": st.get("net_ready"),
        "login_ui_ready": st.get("login_ui_ready"),
        "notice_panel_open": st.get("notice_panel_open"),
        "route_panel_open": st.get("route_panel_open"),
        "route_char_ready": st.get("route_char_ready"),
        "account_count": st.get("account_count"),
        "multi_count": st.get("multi_count"),
        "multi_online": st.get("multi_online"),
        "multi_ready": st.get("multi_ready"),
        "team_multi_count": st.get("team_multi_count"),
        "team_num": st.get("team_num"),
        "team_ok": st.get("team_ok"),
        "team_leader_ok": st.get("team_leader_ok"),
        "team_leader_uid": st.get("team_leader_uid"),
        "multi_slot0_uid": st.get("multi_slot0_uid"),
        "multi_online_slots": st.get("multi_online_slots"),
        "multi_team_slots": st.get("multi_team_slots"),
        "hp_mp_pool_max": st.get("hp_mp_pool_max"),
        "pool_hp": st.get("pool_hp"),
        "pool_mp": st.get("pool_mp"),
        "pool_enabled": st.get("pool_enabled"),
        "buff_ready": st.get("buff_ready"),
        "note": st.get("note"),
    }


def _attach_bridge_diagnostics(result: SmokeTestResult, instance_id: str) -> None:
    _log("刷新桥接诊断 …")
    diag = diagnose_bridge(instance_id)
    result.diagnose_summary = str(diag.get("summary") or "")
    result.diagnose_detail = str(diag.get("detail") or "")
    result.diagnose_lines = list(diag.get("lines") or [])
    result.patch_variant = str(diag.get("patch_variant") or "")
    result.diagnose = {
        k: v
        for k, v in diag.items()
        if k not in ("state",)
    }
    st = read_state(instance_id)
    result.state_snapshot = _snapshot_state(instance_id)
    result.has_state_json = state_path(instance_id).is_file()
    result.bridge_alive = bridge_alive(instance_id)
    result.state_phase = str((st or {}).get("phase") or "")
    result.main_uid = str((st or {}).get("main_uid") or "")
    result.layer0_ok = result.has_state_json and result.state_phase not in ("", "-")


def _is_logged_in(snap: dict[str, Any]) -> bool:
    ac = int(snap.get("account_count") or 0)
    if ac > 0 and snap.get("route_panel_open"):
        return True
    return bool(snap.get("route_char_ready"))


def _is_in_game(snap: dict[str, Any]) -> bool:
    if str(snap.get("main_uid") or "").strip():
        return True
    return str(snap.get("phase") or "") == "in_game"


def _slot_online(snap: dict[str, Any], index: int) -> bool:
    slots = str(snap.get("multi_online_slots") or "").split(",")
    if 0 <= index < len(slots):
        return slots[index] == "1"
    return False


def _is_team_ok(snap: dict[str, Any]) -> bool:
    team_num = int(snap.get("team_num") or 0)
    return team_num >= 5


def _finalize_team_after_summon(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
    *,
    wait_sec: float = 5.0,
) -> bool:
    """流程走完后等待，仅根据当前组队人数判定（不依赖召唤 ack）。"""
    _log(f"等待 {wait_sec:.0f}s 后检查组队状态 …")
    deadline = time.time() + wait_sec
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False
        time.sleep(0.5)

    snap = _snapshot_state(instance_id)
    team_num = int(snap.get("team_num") or 0)
    if _is_team_ok(snap):
        result.team_ok = True
        result.summon_ok = True
        _log(f"组队成功 team_num={team_num}/5")
        return True

    result.team_ok = False
    result.failed_step = "summon"
    msg = f"组队未成功 team_num={team_num}/5"
    result.errors.append(msg)
    _log(f"终止：{msg}")
    return False


def _wait_multi_info(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
) -> tuple[bool, dict[str, Any]]:
    deadline = time.time() + cfg.multi_info_wait_sec
    next_fetch = 0.0
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, _snapshot_state(instance_id)
        snap = _snapshot_state(instance_id)
        if int(snap.get("multi_count") or 0) > 0:
            return True, snap
        now = time.time()
        if now >= next_fetch:
            req_id = ipc_fetch_multi_info(instance_id)
            _log(f"  已发送 fetch_multi_info id={req_id}")
            wait_ack(instance_id, req_id, timeout=cfg.multi_login_ack_timeout_sec)
            next_fetch = now + 8.0
        time.sleep(0.5)
    return False, _snapshot_state(instance_id)


def _game_crashed(pid: int | None) -> bool:
    if not pid:
        return False
    return pid not in {p for p, _ in find_game_processes()}


def _run_login_flow(
    result: SmokeTestResult,
    instance_id: str,
    phone: str,
    password: str,
    cfg: SmokeTestConfig,
) -> None:
    """正式流程：登录 →（可选）进入游戏；每步最多重试 step_max_retries 次。"""
    result.login_attempted = True
    max_try = max(1, cfg.step_max_retries)

    _log(f"等待桥接心跳（最多 {cfg.bridge_wait_sec:.0f}s）…")
    if not wait_for_bridge(instance_id, timeout=cfg.bridge_wait_sec):
        result.failed_step = "login"
        result.login_ack_msg = "桥接未就绪（无心跳）"
        result.errors.append(result.login_ack_msg)
        _log(f"终止：登录步骤失败 — {result.login_ack_msg}")
        return

    snap = _snapshot_state(instance_id)
    _log(
        f"桥接就绪 phase={snap.get('phase')} login_ui={snap.get('login_ui_ready')} "
        f"net_ready={snap.get('net_ready')}"
    )

    if not _run_step_with_retries(
        "login",
        "登录",
        lambda: _try_login_once(result, instance_id, phone, password, cfg),
        result,
        cfg,
        max_try,
    ):
        return

    result.login_ok = True

    if cfg.enter_game_send:
        if not _run_step_with_retries(
            "enter_game",
            "进入游戏",
            lambda: _try_enter_game_once(result, instance_id, cfg),
            result,
            cfg,
            max_try,
        ):
            return

    if cfg.multi_login_send:
        if not result.in_game_ok:
            result.failed_step = "multi_login"
            result.errors.append("未进入游戏，无法拉起多控角色")
            _log("终止：未进入游戏，跳过拉起多控")
            return
        _run_multi_login_flow(result, instance_id, cfg, max_try)
        if result.failed_step:
            return

    if cfg.summon_send:
        if not result.in_game_ok:
            result.failed_step = "summon"
            result.errors.append("未进入游戏，无法召唤")
            _log("终止：未进入游戏，跳过召唤")
            return
        if cfg.multi_login_send and not result.multi_login_ok:
            result.failed_step = "summon"
            result.errors.append("多控未全部上线，跳过召唤")
            _log("终止：多控未全部上线，跳过召唤")
            return

        snap = _snapshot_state(instance_id)
        if _is_team_ok(snap):
            result.summon_ok = True
            result.team_ok = True
            _log(
                f"队伍已满足条件 team_num={snap.get('team_num')}，跳过召唤"
            )
            _close_share_panel_with_retry(result, instance_id, cfg)
            return

        if not _run_step_with_retries(
            "click_multi_head_0",
            "点击第一个多控头像",
            lambda: _try_click_multi_head_once(result, instance_id, 0, cfg),
            result,
            cfg,
            max_try,
        ):
            return

        _log("点头像后等待 2s …")
        time.sleep(2.0)

        if not _run_step_with_retries(
            "select_multi_char_0",
            "选中第一个多控",
            lambda: _try_select_multi_char_once(result, instance_id, 0, cfg),
            result,
            cfg,
            max_try,
        ):
            return

        _log("选中后等待 1s …")
        time.sleep(1.0)

        _run_step_with_retries(
            "summon",
            "一键召唤",
            lambda: _try_summon_once(result, instance_id, cfg),
            result,
            cfg,
            max_try,
        )

        _finalize_team_after_summon(result, instance_id, cfg)
        _close_share_panel_with_retry(result, instance_id, cfg)


def _close_share_panel_with_retry(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
    *,
    max_wait_sec: float = 20.0,
) -> None:
    """组队判定后关闭推广/分享面板；面板可能延迟弹出，故轮询重试。"""
    _log(f"关闭推广/分享面板（最多 {max_wait_sec:.0f}s）…")
    deadline = time.time() + max_wait_sec
    closed = False
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            _log("  游戏已退出，停止关闭推广面板")
            return
        req_id = ipc_close_share_panel(instance_id)
        ok, ack = wait_ack(instance_id, req_id, timeout=cfg.summon_ack_timeout_sec)
        ack_msg = str((ack or {}).get("msg") or "")
        low = ack_msg.lower()
        if ok and "closed" in low:
            _log(f"  推广面板已关闭：{ack_msg}")
            closed = True
            break
        if ok and "not open" in low:
            time.sleep(0.5)
            continue
        if not ok:
            _log(f"  关闭推广面板 ack 失败：{ack_msg or req_id}")
        time.sleep(0.5)
    if not closed:
        _log("  推广面板未在时限内关闭（可能未弹出或已关）")


def _close_share_panel(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
) -> None:
    _close_share_panel_with_retry(result, instance_id, cfg, max_wait_sec=20.0)


def _run_multi_login_flow(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
    max_try: int,
) -> None:
    _log(f"等待多控信息（最多 {cfg.multi_info_wait_sec:.0f}s）…")
    ready, snap = _wait_multi_info(result, instance_id, cfg)
    if not ready:
        result.failed_step = "multi_login"
        msg = (
            f"MultiInfo 未就绪 multi_count={snap.get('multi_count') or 0}"
        )
        result.errors.append(msg)
        _log(f"终止：{msg}")
        return

    multi_count = int(snap.get("multi_count") or 0)
    _log(
        f"多控槽位={multi_count} 已在线={snap.get('multi_online')} "
        f"slots={snap.get('multi_online_slots') or '-'}"
    )
    if multi_count <= 0:
        result.failed_step = "multi_login"
        msg = "MultiInfo 无有效槽位"
        result.errors.append(msg)
        _log(f"终止：{msg}")
        return

    offline_indices = [
        i
        for i in range(multi_count)
        if not _slot_online(snap, i)
    ]
    if not offline_indices:
        result.multi_login_ok = True
        _log("所有多控槽位均已在线")
        return

    _log(f"待拉起槽位: {offline_indices}")

    for index in offline_indices:
        step_id = f"multi_login_char_{index}"
        ok = _run_step_with_retries(
            step_id,
            f"拉起角色{index}",
            lambda idx=index: _try_multi_login_char_once(result, instance_id, idx, cfg),
            result,
            cfg,
            max_try,
        )
        if not ok:
            return

    snap = _snapshot_state(instance_id)
    if not snap.get("multi_ready"):
        result.failed_step = "multi_login"
        msg = (
            f"多控未全部上线 online={snap.get('multi_online')}/"
            f"{snap.get('multi_count')} slots={snap.get('multi_online_slots') or '-'}"
        )
        result.errors.append(msg)
        _log(f"终止：{msg}")
        return

    result.multi_login_ok = True
    _log(
        f"多控全部上线 multi_online={snap.get('multi_online')} "
        f"team_multi={snap.get('team_multi_count')}"
    )


def _run_step_with_retries(
    step_id: str,
    step_label: str,
    try_once,
    result: SmokeTestResult,
    cfg: SmokeTestConfig,
    max_try: int,
) -> bool:
    last_msg = ""
    for attempt in range(1, max_try + 1):
        _log(f"[{step_label}] 第 {attempt}/{max_try} 次尝试 …")
        ok, msg = try_once()
        last_msg = msg
        result.step_attempts.append(
            {
                "step": step_id,
                "step_label": step_label,
                "attempt": attempt,
                "ok": ok,
                "msg": msg,
                "state": _snapshot_state(result.instance_id),
            }
        )
        if ok:
            _log(f"[{step_label}] 第 {attempt} 次成功：{msg}")
            return True
        _log(f"[{step_label}] 第 {attempt} 次失败：{msg}")
        if attempt < max_try:
            _log(f"[{step_label}] {cfg.step_retry_delay_sec:.0f}s 后重试 …")
            time.sleep(cfg.step_retry_delay_sec)

    result.failed_step = step_id
    fail_text = f"{step_label}失败：已重试 {max_try} 次仍失败（{last_msg}）"
    result.errors.append(fail_text)
    if step_id == "login":
        result.login_ack_msg = last_msg
    elif step_id == "enter_game":
        result.enter_game_msg = last_msg
    elif step_id == "summon":
        pass
    _log(f"终止：{fail_text}")
    return False


def _try_login_once(
    result: SmokeTestResult,
    instance_id: str,
    phone: str,
    password: str,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    snap = _snapshot_state(instance_id)
    if _is_logged_in(snap):
        return True, f"已在选角界面 account_count={snap.get('account_count')}"

    deadline = time.time() + cfg.login_ui_delay_sec
    ui_ready = False
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, "等待登录 UI 时游戏闪退"
        snap = _snapshot_state(instance_id)
        if _is_logged_in(snap):
            return True, f"已在选角界面 account_count={snap.get('account_count')}"
        if snap.get("login_ui_ready"):
            ui_ready = True
            break
        time.sleep(0.5)

    if not ui_ready:
        return False, "登录 UI 未就绪（超时）"

    req_id = ipc_login(instance_id, phone, password)
    result.login_req_id = req_id
    _log(f"  已发送 login id={req_id}（账号 {phone[:3]}***）")

    ok, ack = wait_ack(instance_id, req_id, timeout=cfg.login_ack_timeout_sec)
    result.login_ack_ok = ok
    ack_msg = str((ack or {}).get("msg") or "")
    if ack:
        result.login_ack_msg = ack_msg
    if not ok:
        return False, ack_msg or "login ack 失败"

    verify_deadline = time.time() + cfg.route_wait_sec
    while time.time() < verify_deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, "等待登录结果时游戏闪退"
        snap = _snapshot_state(instance_id)
        if _is_logged_in(snap):
            return True, f"登录成功 account_count={snap.get('account_count')}"
        time.sleep(0.5)

    snap = _snapshot_state(instance_id)
    return False, (
        f"登录未进入选角界面 route_panel={snap.get('route_panel_open')} "
        f"account_count={snap.get('account_count')}"
    )


def _try_enter_game_once(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    result.enter_game_attempted = True

    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    snap = _snapshot_state(instance_id)
    if _is_in_game(snap):
        uid = str(snap.get("main_uid") or "")
        result.in_game_ok = True
        result.main_uid = uid
        return True, f"已在游戏中 main_uid={uid}"

    deadline = time.time() + cfg.route_wait_sec
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, "等待选角界面时游戏闪退"
        snap = _snapshot_state(instance_id)
        if _is_in_game(snap):
            uid = str(snap.get("main_uid") or "")
            result.in_game_ok = True
            result.main_uid = uid
            return True, f"已在游戏中 main_uid={uid}"
        ac = int(snap.get("account_count") or 0)
        if snap.get("route_char_ready") and ac > 0:
            break
        time.sleep(0.5)
    else:
        snap = _snapshot_state(instance_id)
        return False, (
            f"选角未就绪 route_panel={snap.get('route_panel_open')} "
            f"account_count={snap.get('account_count')}"
        )

    req_id = ipc_enter_game(instance_id)
    result.enter_game_req_id = req_id
    _log(f"  已发送 enter_game id={req_id}")

    ok, ack = wait_ack(instance_id, req_id, timeout=cfg.enter_game_ack_timeout_sec)
    result.enter_game_ack_ok = ok
    ack_msg = str((ack or {}).get("msg") or "")
    if ack:
        result.enter_game_msg = ack_msg
    if not ok:
        return False, ack_msg or "enter_game ack 失败"

    in_deadline = time.time() + cfg.enter_game_timeout_sec
    while time.time() < in_deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, "等待进游戏时游戏闪退"
        snap = _snapshot_state(instance_id)
        if _is_in_game(snap):
            uid = str(snap.get("main_uid") or "")
            result.in_game_ok = True
            result.main_uid = uid
            return True, f"进入游戏成功 main_uid={uid or '(phase=in_game)'}"
        time.sleep(0.5)

    snap = _snapshot_state(instance_id)
    return False, (
        f"进入游戏超时 phase={snap.get('phase')} "
        f"main_uid={snap.get('main_uid') or '-'}"
    )


def _try_multi_login_char_once(
    result: SmokeTestResult,
    instance_id: str,
    index: int,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    snap = _snapshot_state(instance_id)
    if _slot_online(snap, index):
        return True, f"角色{index}已在线"

    req_id = ipc_multi_login_char(instance_id, index)
    _log(f"  已发送 multi_login_char index={index} id={req_id}")

    ok, ack = wait_ack(instance_id, req_id, timeout=cfg.multi_login_ack_timeout_sec)
    ack_msg = str((ack or {}).get("msg") or "")
    if not ok:
        return False, ack_msg or "multi_login_char ack 失败"

    deadline = time.time() + cfg.multi_char_wait_sec
    while time.time() < deadline:
        if _game_crashed(result.pid):
            result.crashed = True
            return False, "等待角色上线时游戏闪退"
        snap = _snapshot_state(instance_id)
        if _slot_online(snap, index):
            return True, (
                f"角色{index}上线 slots={snap.get('multi_online_slots') or '-'}"
            )
        time.sleep(0.5)

    snap = _snapshot_state(instance_id)
    return False, (
        f"角色{index}上线超时 slots={snap.get('multi_online_slots') or '-'}"
    )


def _try_click_multi_head_once(
    result: SmokeTestResult,
    instance_id: str,
    index: int,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    req_id = ipc_click_multi_head(instance_id, index)
    _log(f"  已发送 click_multi_head index={index} id={req_id}")

    ok, ack = wait_ack(instance_id, req_id, timeout=cfg.multi_login_ack_timeout_sec)
    ack_msg = str((ack or {}).get("msg") or "")
    if not ok:
        return False, ack_msg or "click_multi_head ack 失败"
    return True, ack_msg or f"click_multi_head index={index} ok"


def _try_select_multi_char_once(
    result: SmokeTestResult,
    instance_id: str,
    index: int,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    req_id = ipc_select_multi_char(instance_id, index)
    _log(f"  已发送 select_multi_char index={index} id={req_id}")

    ok, ack = wait_ack(instance_id, req_id, timeout=cfg.multi_login_ack_timeout_sec)
    ack_msg = str((ack or {}).get("msg") or "")
    if not ok:
        return False, ack_msg or "select_multi_char ack 失败"
    return True, ack_msg or f"select_multi_char index={index} ok"


def _try_summon_once(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
) -> tuple[bool, str]:
    if _game_crashed(result.pid):
        result.crashed = True
        return False, "游戏进程已退出"

    req_id = ipc_one_key_summon(instance_id)
    _log(f"  已发送 one_key_summon id={req_id}")

    ok, ack = wait_ack(instance_id, req_id, timeout=min(cfg.summon_ack_timeout_sec, 8.0))
    ack_msg = str((ack or {}).get("msg") or "")
    if ok:
        return True, ack_msg or "one_key_summon 已发送"
    return True, ack_msg or "one_key_summon 已发送（ack 未返回，稍后检查组队）"


def _run_send_login(
    result: SmokeTestResult,
    instance_id: str,
    phone: str,
    password: str,
    cfg: SmokeTestConfig,
) -> None:
    """兼容旧入口。"""
    _run_login_flow(result, instance_id, phone, password, cfg)


def _run_enter_game(
    result: SmokeTestResult,
    instance_id: str,
    cfg: SmokeTestConfig,
) -> None:
    """兼容旧入口（无重试包装）。"""
    _try_enter_game_once(result, instance_id, cfg)


def terminate_game_processes(grace_sec: float = 3.0) -> None:
    procs = find_game_processes()
    if not procs:
        return
    for pid, _exe in procs:
        _log(f"结束 cg37.exe PID {pid}")
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/F"],
            capture_output=True,
            creationflags=no_window_flags(),
        )
    deadline = time.time() + grace_sec
    while time.time() < deadline:
        if not find_game_processes():
            return
        time.sleep(0.25)


def _find_player_log(game_root: Path) -> Path | None:
    local_low = Path.home() / "AppData" / "LocalLow"
    if not local_low.is_dir():
        return None
    candidates: list[Path] = []
    for p in local_low.rglob("Player.log"):
        try:
            if p.stat().st_mtime <= 0:
                continue
        except OSError:
            continue
        candidates.append(p)
    if not candidates:
        return None
    candidates.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    for p in candidates:
        if game_root.name in str(p) or "序章" in str(p):
            return p
    return candidates[0]


def _read_log_tail(path: Path, *, max_lines: int = 40) -> list[str]:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    lines = [ln.rstrip() for ln in text.splitlines() if ln.strip()]
    return lines[-max_lines:]


def _scan_log_hits(lines: list[str]) -> list[str]:
    keys = (
        "TypeLoadException",
        "NullReferenceException",
        "BadImageFormatException",
        "crash has been intercepted",
        "Delay Call Exception",
        "OnApplicationQuit",
        "HotfixEntry:Start",
        "LoginGetToken",
        "RouteSelectPanel",
    )
    hits: list[str] = []
    for ln in lines:
        for k in keys:
            if k in ln and ln not in hits:
                hits.append(ln[:240])
                break
    return hits[:12]


def capture_window_bmp_for_pid(pid: int, out_path: Path) -> bool:
    if sys.platform != "win32" or pid <= 0:
        return False
    out_path.parent.mkdir(parents=True, exist_ok=True)
    ps_path = str(out_path).replace("'", "''")
    ps = f"""
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$proc = Get-Process -Id {pid} -ErrorAction Stop
$hwnd = [Int64]$proc.MainWindowHandle
if ($hwnd -eq 0) {{ exit 2 }}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public struct WinRect {{ public int Left, Top, Right, Bottom; }}
public class WinCapture {{
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out WinRect r);
}}
'@
$r = New-Object WinRect
[void][WinCapture]::GetWindowRect([IntPtr]$hwnd, [ref]$r)
$w = [Math]::Max(64, $r.Right - $r.Left)
$h = [Math]::Max(64, $r.Bottom - $r.Top)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w,$h)))
$bmp.Save('{ps_path}', [System.Drawing.Imaging.ImageFormat]::Bmp)
"""
    try:
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            creationflags=no_window_flags(),
        )
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False
    return proc.returncode == 0 and out_path.is_file() and out_path.stat().st_size > 1000


def analyze_bmp_dark_ratio(
    bmp_path: Path,
    *,
    dark_luma: float = 32.0,
    sample_step: int = 8,
) -> dict[str, float]:
    try:
        data = bmp_path.read_bytes()
    except OSError:
        return {"mean_luma": 255.0, "dark_ratio": 0.0, "samples": 0.0}
    if len(data) < 54 or data[:2] != b"BM":
        return {"mean_luma": 255.0, "dark_ratio": 0.0, "samples": 0.0}
    offset = struct.unpack_from("<I", data, 10)[0]
    width = struct.unpack_from("<i", data, 18)[0]
    height = struct.unpack_from("<i", data, 22)[0]
    bpp = struct.unpack_from("<H", data, 28)[0]
    if width <= 0 or height <= 0 or bpp not in (24, 32):
        return {"mean_luma": 255.0, "dark_ratio": 0.0, "samples": 0.0}
    row_bytes = ((width * bpp + 31) // 32) * 4
    h = abs(height)
    step = max(1, sample_step)
    total = 0
    dark = 0
    luma_sum = 0.0
    for y in range(0, h, step):
        row_off = offset + y * row_bytes
        for x in range(0, width, step):
            i = row_off + x * (bpp // 8)
            if i + 2 >= len(data):
                continue
            b, g, r = data[i], data[i + 1], data[i + 2]
            luma = 0.2126 * r + 0.7152 * g + 0.0722 * b
            luma_sum += luma
            total += 1
            if luma < dark_luma:
                dark += 1
    if total == 0:
        return {"mean_luma": 255.0, "dark_ratio": 0.0, "samples": 0.0}
    return {
        "mean_luma": luma_sum / total,
        "dark_ratio": dark / total,
        "samples": float(total),
    }


def is_black_screen(
    score: dict[str, float],
    *,
    mean_threshold: float,
    dark_ratio_threshold: float,
) -> bool:
    if score.get("samples", 0) < 16:
        return False
    return (
        score.get("mean_luma", 255.0) < mean_threshold
        or score.get("dark_ratio", 0.0) >= dark_ratio_threshold
    )


def run_smoke_test(cfg: SmokeTestConfig | None = None) -> SmokeTestResult:
    cfg = cfg or SmokeTestConfig()
    started = datetime.now()
    result = SmokeTestResult(
        ok=False,
        exit_code=3,
        started_at=started.isoformat(timespec="seconds"),
        finished_at="",
    )
    game_root = get_game_root()

    try:
        if cfg.kill_existing_game:
            _log("清理已有 cg37.exe …")
            terminate_game_processes()

        if cfg.inject_before:
            from . import patch_bridge

            _log("注入桥接（from .orig）…")
            ok, msg = patch_bridge.apply_bridge_patch(
                game_root,
                force_from_orig=cfg.force_inject_from_orig,
            )
            if not ok:
                result.errors.append(f"inject_failed: {msg}")
                result.exit_code = 4
                return _finalize(result, cfg)

        _log(f"等待 {cfg.pre_launch_delay_sec:.0f}s 后启动游戏 …")
        time.sleep(cfg.pre_launch_delay_sec)

        _log("启动游戏（单开）…")
        inst = launch_game(game_root)
        result.instance_id = inst.instance_id
        result.pid = inst.pid
        _log(f"已启动 PID {inst.pid} · {inst.instance_id}")

        if cfg.login_send:
            phone, password = _resolve_login_credentials(cfg)
            if not phone or not password:
                result.errors.append("login_credentials_missing")
                result.exit_code = 5
                return _finalize(result, cfg)
            _run_login_flow(result, inst.instance_id, phone, password, cfg)
        else:
            _log(f"保持 {cfg.hold_sec:.0f}s 观察 …")
            time.sleep(cfg.hold_sec)

        running = {p for p, _ in find_game_processes()}
        result.game_running_after_hold = inst.pid in running
        result.crashed = result.crashed or (not result.game_running_after_hold)

        stamp = started.strftime("%Y%m%d_%H%M%S")
        cfg.report_dir.mkdir(parents=True, exist_ok=True)
        shot = cfg.report_dir / f"smoke_{stamp}_pid{inst.pid}.bmp"

        if result.game_running_after_hold and inst.pid:
            if capture_window_bmp_for_pid(inst.pid, shot):
                result.screenshot_path = str(shot)
                score = analyze_bmp_dark_ratio(shot)
                result.black_screen_score = score
                result.black_screen = is_black_screen(
                    score,
                    mean_threshold=cfg.black_mean_luma_threshold,
                    dark_ratio_threshold=cfg.black_dark_ratio_threshold,
                )
                _log(
                    f"截屏分析 mean_luma={score.get('mean_luma', 0):.1f} "
                    f"dark_ratio={score.get('dark_ratio', 0):.2%} "
                    f"black={result.black_screen}"
                )
            else:
                result.errors.append("screenshot_failed")
                _log("截屏失败（窗口未找到或 PowerShell 不可用）")

        plog = _find_player_log(game_root)
        if plog:
            tail = _read_log_tail(plog)
            result.player_log_tail = tail
            result.player_log_hits = _scan_log_hits(tail)

        _attach_bridge_diagnostics(result, inst.instance_id)

        if result.crashed:
            result.exit_code = 1
            result.ok = False
        elif result.black_screen:
            result.exit_code = 2
            result.ok = False
        elif cfg.login_send and cfg.enter_game_send:
            if result.failed_step == "login":
                result.exit_code = 6
            elif result.failed_step == "enter_game" or not result.in_game_ok:
                result.exit_code = 7
            elif result.failed_step == "summon" or (
                cfg.summon_send and not result.team_ok
            ):
                result.exit_code = 9
            elif (
                result.failed_step == "multi_login"
                or result.failed_step.startswith("multi_login_char_")
                or (cfg.multi_login_send and not result.multi_login_ok)
            ):
                result.exit_code = 8
            else:
                result.exit_code = 0
            result.ok = (
                result.in_game_ok
                and not result.failed_step
                and not result.crashed
                and not result.black_screen
                and (not cfg.multi_login_send or result.multi_login_ok)
                and (not cfg.summon_send or result.team_ok)
            )
        elif cfg.login_send:
            if result.failed_step == "login" or not result.login_ok:
                result.exit_code = 6
            else:
                result.exit_code = 0
            result.ok = (
                result.login_ok
                and not result.failed_step
                and not result.crashed
                and not result.black_screen
            )
        else:
            result.exit_code = 0 if result.layer0_ok else 0
            result.ok = not result.crashed and not result.black_screen

        _log(
            f"结论: crash={result.crashed} black={result.black_screen} "
            f"layer0={result.layer0_ok} login_ok={result.login_ok} "
            f"in_game={result.in_game_ok} multi={result.multi_login_ok} "
            f"team={result.team_ok} failed_step={result.failed_step or '-'}"
        )
        return _finalize(result, cfg)
    except Exception as exc:
        result.errors.append(f"{type(exc).__name__}: {exc}")
        result.exit_code = 3
        return _finalize(result, cfg)
    finally:
        if cfg.kill_game_after:
            _log("关闭游戏 …")
            terminate_game_processes()


def _finalize(result: SmokeTestResult, cfg: SmokeTestConfig) -> SmokeTestResult:
    result.finished_at = datetime.now().isoformat(timespec="seconds")
    cfg.report_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    out = cfg.report_dir / f"smoke_{stamp}.json"
    out.write_text(
        json.dumps(result.to_dict(), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    _log(f"报告已写入 {out}")
    return result


def print_result_summary(result: SmokeTestResult) -> None:
    print()
    print("=" * 56)
    print(f"  闪退: {'是' if result.crashed else '否'}")
    print(f"  黑屏: {'是' if result.black_screen else '否'}")
    print(f"  第0层(layer0): {'通过' if result.layer0_ok else '未通过'}")
    print(f"  state.json: {'有' if result.has_state_json else '无'}")
    print(f"  桥接心跳: {'有' if result.bridge_alive else '无'}")
    if result.login_attempted:
        print(f"  登录: {'成功' if result.login_ok else '失败'}")
        if result.login_req_id:
            print(f"    req_id={result.login_req_id}")
        if result.login_ack_msg:
            print(f"    → {result.login_ack_msg}")
    if result.enter_game_attempted:
        print(f"  进入游戏: {'成功' if result.in_game_ok else '失败'}")
        if result.enter_game_req_id:
            print(f"    req_id={result.enter_game_req_id}")
        if result.enter_game_msg:
            print(f"    → {result.enter_game_msg}")
    elif result.login_attempted and not result.enter_game_attempted:
        print("  （仅登录模式，未发 enter_game）")
    if result.multi_login_ok or any(
        str(r.get("step") or "").startswith("multi_login_char_")
        for r in result.step_attempts
    ):
        print(f"  多控拉起: {'成功' if result.multi_login_ok else '失败'}")
    if result.summon_ok or any(r.get("step") == "summon" for r in result.step_attempts):
        print(f"  一键召唤/组队: {'成功' if result.team_ok else '失败'}")
    if result.failed_step:
        label = {
            "login": "登录",
            "enter_game": "进入游戏",
            "multi_login": "多控拉起",
            "summon": "一键召唤",
        }.get(result.failed_step, result.failed_step)
        if result.failed_step.startswith("multi_login_char_"):
            label = f"拉起角色{result.failed_step.rsplit('_', 1)[-1]}"
        print(f"  失败步骤: {label}（已用尽重试）")
    if result.step_attempts:
        print("  步骤重试记录:")
        for row in result.step_attempts:
            mark = "✓" if row.get("ok") else "✗"
            print(
                f"    {mark} [{row.get('step_label')}] "
                f"第{row.get('attempt')}次: {row.get('msg')}"
            )
    if result.main_uid:
        print(f"  main_uid: {result.main_uid}")
    if result.state_snapshot:
        ss = result.state_snapshot
        print(
            f"  状态快照: phase={ss.get('phase')} "
            f"multi={ss.get('multi_online')}/{ss.get('multi_count')} "
            f"team={ss.get('team_multi_count')} "
            f"wf={ss.get('workflow_current')} err={ss.get('workflow_error') or '-'}"
        )
    if result.screenshot_path:
        print(f"  截屏: {result.screenshot_path}")
    if result.player_log_hits:
        print("  Player.log 命中:")
        for ln in result.player_log_hits[:5]:
            print(f"    · {ln}")
    print(f"  诊断摘要: {result.diagnose_summary}")
    if result.diagnose_detail:
        print(f"  诊断详情: {result.diagnose_detail}")
    if result.diagnose_lines:
        print("  桥接诊断:")
        for ln in result.diagnose_lines:
            print(f"    {ln}")
    print("=" * 56)
