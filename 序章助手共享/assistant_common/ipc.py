#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""与游戏内 SeqChapterHelperBridge 通信（文件 IPC）。"""
from __future__ import annotations

import json
import time
import uuid
from pathlib import Path
from typing import Any

from .config import DATA_DIR

BRIDGE_VERSION = 1


def instance_dir(instance_id: str) -> Path:
    path = DATA_DIR / "instances" / instance_id
    path.mkdir(parents=True, exist_ok=True)
    return path


def cmd_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "cmd.json"


def state_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "state.json"


def ack_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "ack.json"


def send_command(instance_id: str, cmd: str, **params: Any) -> str:
    req_id = uuid.uuid4().hex[:10]
    payload = {
        "id": req_id,
        "cmd": cmd,
        "params": params,
        "ts": int(time.time()),
        "v": BRIDGE_VERSION,
    }
    path = cmd_path(instance_id)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return req_id


def read_state(instance_id: str) -> dict | None:
    path = state_path(instance_id)
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None


def wait_ack(instance_id: str, req_id: str, timeout: float = 30.0) -> tuple[bool, dict | None]:
    deadline = time.time() + timeout
    ack_file = ack_path(instance_id)
    while time.time() < deadline:
        if ack_file.is_file():
            try:
                data = json.loads(ack_file.read_text(encoding="utf-8"))
                if data.get("id") == req_id:
                    return data.get("ok", False), data
            except (json.JSONDecodeError, OSError):
                pass
        time.sleep(0.2)
    return False, None


def bridge_alive(instance_id: str, max_age_sec: float = 12.0) -> bool:
    state = read_state(instance_id)
    if not state:
        return False
    ts = state.get("heartbeat_ts", 0)
    return (time.time() - ts) <= max_age_sec


def heartbeat_age_sec(instance_id: str) -> float | None:
    """距上次 heartbeat_ts 的秒数；无 state 或无心跳字段时返回 None。"""
    st = read_state(instance_id)
    if not st:
        return None
    ts = float(st.get("heartbeat_ts", 0) or 0)
    if ts <= 0:
        return None
    return time.time() - ts


def _read_pid_txt(instance_id: str) -> int | None:
    path = instance_dir(instance_id) / "pid.txt"
    if not path.is_file():
        return None
    try:
        return int(path.read_text(encoding="utf-8").strip())
    except (ValueError, OSError):
        return None


def list_instance_snapshots() -> list[dict[str, Any]]:
    """扫描 IPC 目录下所有 inst_* 实例快照。"""
    root = DATA_DIR / "instances"
    rows: list[dict[str, Any]] = []
    if not root.is_dir():
        return rows

    for path in sorted(root.iterdir()):
        if not path.is_dir() or not path.name.startswith("inst_"):
            continue
        iid = path.name
        st = read_state(iid)
        age = heartbeat_age_sec(iid) if st else None
        pid_txt = _read_pid_txt(iid)
        rows.append(
            {
                "instance_id": iid,
                "dir": str(path),
                "has_state": st is not None,
                "has_pid_txt": pid_txt is not None,
                "pid_txt": pid_txt,
                "phase": (st or {}).get("phase"),
                "note": (st or {}).get("note"),
                "heartbeat_age_sec": age,
                "alive": bridge_alive(iid),
                "state": st,
            }
        )
    return rows


def _resolve_target_instance(
    instance_id: str | None,
    *,
    allow_auto_match: bool = True,
) -> tuple[str, bool, dict | None, float | None, bool]:
    """解析目标实例 ID，返回 (iid, auto_matched, state, heartbeat_age, alive)。"""
    live = find_live_instance()
    iid = (instance_id or "").strip()
    auto_matched = False
    if not iid:
        iid = live or ""
        auto_matched = bool(iid)
    elif allow_auto_match and not bridge_alive(iid) and live and live != iid:
        iid = live
        auto_matched = True

    st = read_state(iid) if iid else None
    age = heartbeat_age_sec(iid) if iid else None
    alive = bridge_alive(iid) if iid else False
    return iid, auto_matched, st, age, alive


def _format_bridge_summary(
    iid: str,
    st: dict | None,
    age: float | None,
    alive: bool,
    *,
    patched: bool | None = None,
    game_running: bool | None = None,
    running_count: int | None = None,
) -> tuple[str, str]:
    if alive:
        wf = (st or {}).get("workflow_current") or ""
        if wf:
            summary = f"桥接：已连接 | 自动流程：{wf}"
        else:
            summary = f"桥接：已连接 | phase={st.get('phase', '?') if st else '?'}"
        detail = f"心跳 {age:.0f}s 前" if age is not None else ""
        return summary, detail

    if st and st.get("phase") == "boot_error":
        err = st.get("note") or st.get("workflow_error") or "未知错误"
        summary = f"桥接：启动失败 | {err}"
        if patched is not None:
            detail = f"详见 state.json · 注入={'已注入' if patched else '未注入'}"
        else:
            detail = "详见 state.json · 点「刷新桥接」查看注入状态"
        return summary, detail

    if st:
        phase = st.get("phase", "?")
        if age is not None:
            summary = f"桥接：等待心跳 | phase={phase} | 上次心跳 {age:.0f}s 前"
        else:
            summary = f"桥接：已启动无心跳 | phase={phase}"
        if patched is not None and game_running is not None:
            detail = f"注入={'已注入' if patched else '未注入'} · 游戏进程={'运行中' if game_running else '未匹配'}"
        else:
            detail = "点「刷新桥接」查看注入/进程详情"
        return summary, detail

    if iid:
        if patched is False:
            summary = "桥接：未连接 | hotfix 未注入桥接"
        elif patched is True and game_running is False:
            summary = "桥接：未连接 | 游戏未运行或 PID 不匹配"
        else:
            summary = "桥接：未连接 | 无 state.json（游戏内 loader 未执行）"
        if patched is not None:
            detail = f"实例 {iid} · 注入={'已注入' if patched else '未注入'}"
        else:
            detail = f"实例 {iid} · 点「刷新桥接」查看完整诊断"
        return summary, detail

    summary = "桥接：未连接 | 请填写实例 ID 或用多开器启动"
    if running_count is not None and patched is not None:
        detail = f"注入={'已注入' if patched else '未注入'} · cg37 进程 {running_count} 个"
    else:
        detail = "点「刷新桥接」查看完整诊断"
    return summary, detail


def bridge_status_quick(
    instance_id: str | None = None,
    *,
    allow_auto_match: bool = True,
) -> dict[str, Any]:
    """轻量桥接状态：只读 IPC 文件，不启动子进程（供自动轮询）。"""
    iid, auto_matched, st, age, alive = _resolve_target_instance(
        instance_id,
        allow_auto_match=allow_auto_match,
    )
    summary, detail = _format_bridge_summary(iid, st, age, alive)
    return {
        "instance_id": iid,
        "alive": alive,
        "auto_matched": auto_matched,
        "summary": summary,
        "detail": detail,
        "lines": [],
        "state": st,
    }


def diagnose_bridge(
    instance_id: str | None = None,
    *,
    allow_auto_match: bool = True,
) -> dict[str, Any]:
    """完整桥接诊断（含进程列表、hotfix 注入检测）；仅手动「刷新桥接」时调用。"""
    from . import patch_bridge
    from .config import get_game_root
    from .game import find_game_processes

    game_root = get_game_root()
    running = find_game_processes()
    running_pids = {pid for pid, _ in running}

    patched = patch_bridge.is_bridge_patched(game_root)
    patch_variant = patch_bridge.detect_patch_variant(game_root)
    patch_variant_label = patch_bridge.patch_variant_label(patch_variant)
    bridge_dll = patch_bridge.bridge_dll_path(game_root)
    hotfix = patch_bridge.hotfix_path(game_root)
    inject_log = patch_bridge.bridge_inject_log_path()
    loader_log = DATA_DIR / "loader.log"

    snapshots = list_instance_snapshots()
    iid, auto_matched, st, age, alive = _resolve_target_instance(
        instance_id,
        allow_auto_match=allow_auto_match,
    )
    pid_txt = _read_pid_txt(iid) if iid else None

    pid_from_iid: int | None = None
    if iid.startswith("inst_"):
        try:
            pid_from_iid = int(iid[5:])
        except ValueError:
            pid_from_iid = None

    expected_pid = pid_txt if pid_txt is not None else pid_from_iid
    game_running = expected_pid in running_pids if expected_pid is not None else False

    lines: list[str] = []
    lines.append(f"实例 ID: {iid or '(未指定)'}")
    if auto_matched:
        lines.append("  → 已自动匹配到有心跳 / 最近活动的实例")
    lines.append(f"IPC 根目录: {DATA_DIR / 'instances'}")
    lines.append("")

    lines.append("[ hotfix / 注入 ]")
    lines.append(f"  游戏目录: {game_root}")
    lines.append(f"  hotfix 存在: {hotfix.is_file()}  ({hotfix})")
    lines.append(f"  桥接 DLL: {bridge_dll.is_file()}  ({bridge_dll.name})")
    lines.append(f"  hotfix 已注入桥接: {'是' if patched else '否'}")
    lines.append(f"  注入方案: {patch_variant_label} ({patch_variant})")
    if patch_variant == "embedded":
        lines.append("    → 嵌入类型版会导致黑屏/loader 不跑，请多开器点「立即注入桥接」从 .orig 重做")
    elif patch_variant == "cecil_light":
        lines.append("    → Assembly.Load 字节在 HybridCLR 下常失败；请关闭游戏后重新注入以升级二进制 LoadFrom")
    elif patch_variant in ("binary_loadfrom", "cecil_light_loadfrom", "cecil_light_loadbytes"):
        bootstrap_site = patch_bridge.detect_bootstrap_site(game_root)
        if patch_variant == "cecil_light_loadbytes":
            lines.append("    → Load 字节 + OnApplicationPause loader + AddTimeInvoke/GameManager")
        elif bootstrap_site == "pause":
            lines.append("    → 旧版 hook（Start 末尾直接 call）；请关闭游戏后点「立即注入桥接」升级 Timer 延迟版")
        else:
            lines.append("    → LoadFrom 落盘 + Start 后 AddTimeInvoke 3s；进游戏约 3–5s 后应出现 state.json")
        lines.append("    → 仍无 state 请看 IPC boot_error 或 Player.log")
    if inject_log.is_file():
        try:
            tail = inject_log.read_text(encoding="utf-8", errors="replace").splitlines()[-8:]
            lines.append(f"  注入日志 ({inject_log.name}) 末尾:")
            lines.extend(f"    {ln}" for ln in tail if ln.strip())
        except OSError:
            pass
    if loader_log.is_file():
        try:
            tail = loader_log.read_text(encoding="utf-8", errors="replace").splitlines()[-12:]
            lines.append(f"  游戏内 loader 日志 ({loader_log.name}) 末尾:")
            lines.extend(f"    {ln}" for ln in tail if ln.strip())
        except OSError:
            pass
    else:
        lines.append(f"  游戏内 loader 日志: 无 ({loader_log.name})")
        if patch_variant in ("binary_loadfrom", "cecil_light_loadfrom"):
            lines.append("    → 轻量版不写 loader.log；若仍无 state.json，请看 IPC 是否出现 boot_error")
        else:
            lines.append("    → loader 可能从未执行，或在写日志前即崩溃")
    lines.append("")

    lines.append(f"[ 游戏进程 cg37.exe ]  共 {len(running)} 个")
    if running:
        for pid, _exe in running:
            mark = " ← 当前实例" if expected_pid == pid else ""
            lines.append(f"  PID {pid}{mark}")
    else:
        lines.append("  (无运行中的 cg37.exe)")
    lines.append("")

    if iid:
        lines.append(f"[ 当前实例 {iid} ]")
        inst_path = instance_dir(iid)
        lines.append(f"  目录: {inst_path}")
        lines.append(f"  state.json: {'有' if (inst_path / 'state.json').is_file() else '无'}")
        lines.append(f"  pid.txt: {pid_txt if pid_txt is not None else '无'}")
        if expected_pid is not None:
            lines.append(
                f"  期望 PID {expected_pid} 是否在运行: {'是' if game_running else '否'}"
            )
        if st:
            lines.append(f"  phase: {st.get('phase', '?')}")
            note = st.get("note") or st.get("workflow_error")
            if note:
                lines.append(f"  note: {note}")
            if age is not None:
                lines.append(f"  heartbeat: {age:.1f}s 前 ({'有效' if alive else '已过期 (>12s)'})")
            else:
                lines.append("  heartbeat: 无 / 未写入")
        else:
            lines.append("  → 游戏内 Bootstrap 尚未写入 state（loader 未跑或加载失败）")
            lines.append("    （pid.txt 由多开器写入，不能代表桥接应已连接）")
        if st and st.get("phase") == "loader_probe":
            lines.append("  → loader hook 已执行（probe）；外部 Bootstrap 可能未成功")
        lines.append(f"  桥接存活 (heartbeat≤12s): {'是' if alive else '否'}")
    else:
        lines.append("[ 当前实例 ] 未指定且无自动匹配")
    lines.append("")

    lines.append(f"[ 全部 IPC 实例 ]  共 {len(snapshots)} 个")
    if snapshots:
        for s in snapshots:
            hb = (
                f"心跳 {s['heartbeat_age_sec']:.0f}s前"
                if s["heartbeat_age_sec"] is not None
                else "无心跳"
            )
            alive_mark = " ✓" if s["alive"] else ""
            lines.append(
                f"  {s['instance_id']}: state={'有' if s['has_state'] else '无'}"
                f" pid.txt={s['pid_txt'] or '-'}"
                f" phase={s['phase'] or '-'}"
                f" {hb}{alive_mark}"
            )
    else:
        lines.append("  (尚无 inst_* 目录 — 请用多开器启动游戏)")
    live = find_live_instance()
    if live:
        lines.append(f"  find_live_instance → {live}")

    summary, detail = _format_bridge_summary(
        iid,
        st,
        age,
        alive,
        patched=patched,
        game_running=game_running,
        running_count=len(running),
    )

    return {
        "instance_id": iid,
        "alive": alive,
        "auto_matched": auto_matched,
        "summary": summary,
        "detail": detail,
        "lines": lines,
        "patched": patched,
        "patch_variant": patch_variant,
        "game_running": game_running,
        "state": st,
    }


def find_live_instance(max_age_sec: float = 12.0) -> str | None:
    """返回最近有心跳的实例 ID（用于助手自动匹配 PID）。"""
    root = DATA_DIR / "instances"
    if not root.is_dir():
        return None
    best_id: str | None = None
    best_ts = 0.0
    now = time.time()
    for path in root.iterdir():
        if not path.is_dir() or not path.name.startswith("inst_"):
            continue
        st = read_state(path.name) or {}
        ts = float(st.get("heartbeat_ts", 0) or 0)
        if ts <= 0 or (now - ts) > max_age_sec:
            continue
        if ts >= best_ts:
            best_ts = ts
            best_id = path.name
    return best_id


def wait_for_in_game(instance_id: str, timeout: float = 240.0) -> tuple[bool, str, dict | None]:
    """轮询 state，直到 main_uid 非空、workflow_error 或超时。"""
    deadline = time.time() + timeout
    last_st: dict | None = None
    while time.time() < deadline:
        st = read_state(instance_id) or {}
        last_st = st
        err = st.get("workflow_error")
        if err:
            return False, str(err), st
        main_uid = str(st.get("main_uid") or "").strip()
        if main_uid:
            return True, main_uid, st
        if st.get("workflow_done") and main_uid:
            return True, main_uid, st
        phase = str(st.get("phase") or "")
        if phase == "in_game" and main_uid:
            return True, main_uid, st
        time.sleep(0.5)
    note = ""
    if last_st:
        note = (
            f"phase={last_st.get('phase')} "
            f"wf={last_st.get('workflow_current')} "
            f"steps={last_st.get('workflow_steps')}"
        )
    return False, f"进入游戏超时 ({note.strip()})".strip(), last_st


def wait_for_bridge(instance_id: str, timeout: float = 180.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if bridge_alive(instance_id):
            return True
        time.sleep(0.5)
    return False


def wait_workflow_done(instance_id: str, timeout: float = 360.0) -> tuple[bool, str]:
    deadline = time.time() + timeout
    while time.time() < deadline:
        st = read_state(instance_id) or {}
        err = st.get("workflow_error")
        if err:
            return False, str(err)
        if st.get("workflow_done"):
            return True, str(st.get("note") or st.get("phase") or "完成")
        if st.get("phase") in ("workflow_done", "workflow_error"):
            return st.get("phase") == "workflow_done", str(st.get("note", ""))
        if not st.get("workflow_active") and st.get("workflow_steps", 0) == 0:
            phase = st.get("phase", "")
            if phase == "in_game":
                return True, "已进入游戏"
        time.sleep(0.5)
    return False, "自动流程超时"


# --- 游戏内等价操作（由 Bridge 调用 TeamManager / Login 等方法） ---

def login(instance_id: str, phone: str, password: str) -> str:
    return send_command(instance_id, "login", phone=phone, password=password)


def enter_game(instance_id: str) -> str:
    return send_command(instance_id, "enter_game")


def multi_login_offline_all(instance_id: str) -> str:
    """对 5 控槽位中离线的角色发送「登陆角色」。"""
    return send_command(instance_id, "multi_login_offline_all")


def multi_login_char(instance_id: str, index: int) -> str:
    """对指定多控槽位发送「登陆角色」。"""
    return send_command(instance_id, "multi_login_char", index=index)


def fetch_multi_info(instance_id: str) -> str:
    """请求服务器下发多控列表（获取多控）。"""
    return send_command(instance_id, "fetch_multi_info")


def click_multi_head(instance_id: str, index: int = 0) -> str:
    """MulitPanel 中点击第 index 个角色头像（OnClickHead）。"""
    return send_command(instance_id, "click_multi_head", index=index)


def select_multi_char(instance_id: str, index: int = 0) -> str:
    """MulitPanel 中选中第 index 个角色（OnClickMulit）。"""
    return send_command(instance_id, "select_multi_char", index=index)


def close_share_panel(instance_id: str) -> str:
    """关闭推广/分享面板（ShareNoticePanel / ActivityPanel）。"""
    return send_command(instance_id, "close_share_panel")


def fetch_resource_status(instance_id: str) -> str:
    """向服务器请求血魔池状态与 BUFF 数据（刷新 pool/buff 字段）。"""
    return send_command(instance_id, "fetch_resource_status")


def create_team(instance_id: str) -> str:
    """创建队伍（多控一键召唤前可能需要）。"""
    return send_command(instance_id, "create_team")


def team_gather(instance_id: str) -> str:
    """队伍召集（一键召唤后拉齐队员）。"""
    return send_command(instance_id, "team_gather")


def switch_char_by_index(instance_id: str, index: int) -> str:
    """点击第 index 个头像切换（0=第一个）。"""
    return send_command(instance_id, "switch_char", index=index)


def one_key_summon(instance_id: str) -> str:
    return send_command(instance_id, "one_key_summon")


def nav_general(instance_id: str, floor: int, x: int, y: int) -> str:
    """世界地图跨图导航：GeneralPointMoveTo(mapIndex, x, y)。"""
    return send_command(instance_id, "nav_general", floor=floor, x=x, y=y)


def nav_walk_map(instance_id: str, floor: int, x: int, y: int) -> str:
    """WalkSystem.MoveTo(MapPoint)。"""
    return send_command(instance_id, "nav_walk_map", floor=floor, x=x, y=y)


def nav_walk_same(instance_id: str, x: int, y: int) -> str:
    """同图点击寻路：WalkSystem.MoveTo(Vector2Int)。"""
    return send_command(instance_id, "nav_walk_same", x=x, y=y)


def nav_task(instance_id: str, floor: int, x: int, y: int) -> str:
    """任务寻路：MissionSystem.TaskMoveTo。"""
    return send_command(instance_id, "nav_task", floor=floor, x=x, y=y)


def nav_stop(instance_id: str) -> str:
    """停止寻路并取消任务路径。"""
    return send_command(instance_id, "nav_stop")


def workflow_login_enter(instance_id: str, phone: str, password: str) -> str:
    """登录 → 选服 → 进入游戏（不含多控/召唤）。"""
    return send_command(
        instance_id,
        "workflow_login_enter",
        phone=phone,
        password=password,
    )


def workflow_step1_five_chars(instance_id: str, phone: str, password: str) -> str:
    """一键流程：登录 → 进游戏 → 拉起离线多控 → 点头像选第一个 → 一键召唤 → 关分享。"""
    return send_command(
        instance_id,
        "workflow_step1",
        phone=phone,
        password=password,
    )


def send_proto(
    instance_id: str,
    *,
    opcode: int = 0,
    proto_type: str,
    fields: dict[str, Any] | None = None,
    uid: str | None = None,
    opcode_name: str | None = None,
    data_b64: str | None = None,
) -> str:
    """通用 Protobuf 发包：NetManager.SendMessage(opcode, proto)。"""
    params: dict[str, Any] = {"proto_type": proto_type}
    if opcode > 0:
        params["opcode"] = opcode
    if opcode_name:
        params["opcode_name"] = opcode_name
    if fields:
        params["fields"] = fields
    if uid:
        params["uid"] = uid
    if data_b64:
        params["data_b64"] = data_b64
    return send_command(instance_id, "send_proto", **params)


def send_gm(instance_id: str, text: str, uid: str | None = None) -> str:
    """GM 频道聊天（等同 GM 商店 additem 等命令入口）。"""
    params: dict[str, Any] = {"text": text}
    if uid:
        params["uid"] = uid
    return send_command(instance_id, "send_gm", **params)


def open_panel(instance_id: str, panel_id: str, uid: str | None = None) -> str:
    """打开序章补丁替换客服入口对应的游戏面板。"""
    params: dict[str, Any] = {"panel_id": panel_id}
    if uid:
        params["uid"] = uid
    return send_command(instance_id, "open_panel", **params)


def char_cache_path(instance_id: str) -> Path:
    return instance_dir(instance_id) / "char_cache.json"


def parse_char_roster(st: dict | None) -> list[dict[str, str]]:
    """从 state 解析多控角色列表 [{index, name, uid}, ...]。"""
    if not st:
        return []
    names = str(st.get("char_names") or "").split("|")
    uids = str(st.get("char_uids") or "").split("|")
    chars: list[dict[str, str]] = []
    for i, name in enumerate(names):
        uid = uids[i].strip() if i < len(uids) else ""
        name = name.strip()
        if not name or name == "-" or not uid or uid == "-":
            continue
        chars.append({"index": str(i + 1), "name": name, "uid": uid})
    return chars


def load_char_cache(instance_id: str) -> list[dict[str, str]]:
    path = char_cache_path(instance_id)
    if not path.is_file():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return []
    rows = data.get("chars")
    if not isinstance(rows, list):
        return []
    out: list[dict[str, str]] = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        uid = str(row.get("uid") or "").strip()
        name = str(row.get("name") or "").strip()
        if not uid or not name:
            continue
        out.append(
            {
                "index": str(row.get("index") or len(out) + 1),
                "name": name,
                "uid": uid,
            }
        )
    return out


def update_char_cache(instance_id: str, st: dict | None) -> list[dict[str, str]]:
    """心跳更新角色缓存；state 无有效 uid 时回退到本地缓存。"""
    chars = parse_char_roster(st)
    if chars:
        char_cache_path(instance_id).write_text(
            json.dumps({"chars": chars}, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return chars
    return load_char_cache(instance_id)


def send_use_item(
    instance_id: str,
    *,
    haveitemindex: int,
    uid: str,
    x: int,
    y: int,
    toindex: int = 0,
    selectindex: int = -1,
    usecount: int = 1,
) -> str:
    """普通使用道具：LSSPROTO_USE_ITEM_FUNC (16)。"""
    fields = {
        "X": x,
        "Y": y,
        "Haveitemindex": haveitemindex,
        "Toindex": toindex,
        "Selectindex": selectindex,
        "Usecount": usecount,
        "KUid": uid,
    }
    return send_proto(instance_id, opcode=16, proto_type="Proto_CS_UseItem", fields=fields)


def send_account_bank_deposit(
    instance_id: str,
    *,
    haveitemindex: int,
    uid: str,
) -> str:
    """账号银行存道具（单格）：LSSPROTO_ACCOUNT_BANK_FUNC (2003)。"""
    fields = {
        "Type": "存道具",
        "Index": 0,
        "Num": 0,
        "IndexList": [haveitemindex],
        "KUid": uid,
    }
    return send_proto(
        instance_id,
        opcode=2003,
        proto_type="Proto_CS_Bank",
        fields=fields,
        uid=uid,
        opcode_name="LSSPROTO_ACCOUNT_BANK_FUNC",
    )


def send_gem_inlay(
    instance_id: str,
    *,
    equip_index: int,
    gem_index: int,
    uid: str,
) -> str:
    """装备镶嵌宝石：LSSPROTO_RECIPE_FUNC (1010)。"""
    fields = {
        "Type": "镶嵌宝石",
        "Id": equip_index,
        "Haveitemindex": gem_index,
        "Num": 0,
        "ItemCostType": 0,
        "MakeStoreType": 0,
        "KUid": uid,
    }
    return send_proto(
        instance_id,
        opcode=1010,
        proto_type="Proto_CS_Recipe",
        fields=fields,
        uid=uid,
        opcode_name="LSSPROTO_RECIPE_FUNC",
    )
