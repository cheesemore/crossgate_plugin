#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
傻瓜补丁：诊断客户端后一键打补丁（由 GUI 两包调用）。

两包：
  · 九动版：九动加速（DLL 九动）/ 无九动加速 / 抓宠 / 抓宠无宠人防 / 烧卡 / 慢速烧卡
  · 融合版：普通加速（无九动）/ 抓宠 / 抓宠无宠人防 / 烧卡 / 慢速烧卡

预设见 patch_defaults：
  FOOLPROOF_COMBO / NO_NINE / BURN_SEAL / BURN_SEAL_SLOW / AUTO_CATCH / AUTO_CATCH_NOPET

活 hotfix 不干净时：界面可选手选干净目录恢复（restore_hotfixdata_from_clean），无默认源。
体积与 EXPECTED_SIZE 绑定；客户端更新导致体积变化时需发新版傻瓜补丁。
"""
from __future__ import annotations

import shutil
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
    mark_hotfix_watch_stamp,
    save_baseline_meta,
    set_game_root,
    sha256_file,
    updated_hotfix_candidate,
)
from patch_defaults import (
    FOOLPROOF_BURN_SEAL_COMBO_KWARGS,
    FOOLPROOF_BURN_SEAL_SLOW_COMBO_KWARGS,
    FOOLPROOF_AUTO_CATCH_COMBO_KWARGS,
    FOOLPROOF_AUTO_CATCH_NOPET_COMBO_KWARGS,
    FOOLPROOF_COMBO_KWARGS,
    FOOLPROOF_NO_NINE_COMBO_KWARGS,
)
from patch_slack import format_slack_summary, slack_report

LogFn = Callable[[str], None]

# 活 hotfix 不像干净官方原版时的统一提示（热更新半覆盖 / 仍含旧补丁等）
UNCLEAN_CLIENT_HINT = (
    "当前客户端状态异常（hotfix 不是干净官方原版）。\n"
    "常见原因：热更新未完整覆盖、仍含旧补丁、半更新。\n\n"
    "可在傻瓜补丁界面选择从干净目录恢复 hotfix 后再打，\n"
    "或自行用启动器修复/换干净客户端后再打补丁。"
)


class FoolproofError(RuntimeError):
    """面向用户的失败说明（可直接弹窗）。"""


def unclean_client_error(detail: str = "") -> FoolproofError:
    """怪状态：引导恢复干净 hotfix 或换客户端。"""
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


def restore_hotfixdata_from_clean(
    clean_root: Path,
    game_root: Path,
    *,
    on_log: LogFn | None = None,
) -> list[str]:
    """从用户指定的干净客户端同步 hotfixdata（及常用主程序），再写好 .orig。

    不设默认源：clean_root 必须由调用方显式传入。
    """
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

    # 去掉干净源里没有的扩展 DLL，避免旧补丁残留
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
            "请换与本傻瓜补丁同版本的干净客户端，或换对应新版傻瓜补丁。"
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
    """保证 .orig / neworig 为「本包期望体积」的干净原版。

    - 活 hotfix 体积正确且像官方原版 → 强制覆盖旧 .orig / neworig 后继续打补丁
    - 活 hotfix 不像干净原版（热更新半覆盖 / 仍含旧补丁等）→ 一律提示换干净客户端
      （不再回退用可能过期的本地 .orig，避免叠在怪状态上）
    """
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

    # 丢掉与本包体积不符的旧底稿，避免挑到上一版 .orig
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

    # 活文件若与底稿不一致（理论上 live_ok 时不应发生），写回后再打
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
            f"客户端 hotfix 版本与本傻瓜补丁不匹配\n"
            f"（实际 {size:,}，本包期望 {EXPECTED_SIZE:,}）。\n"
            f"提示：{KNOWN_OLD_SIZES[size]}\n\n"
            f"请先用启动器把客户端更新到最新，\n"
            f"再使用对应新版本的傻瓜补丁（旧包请删掉）。"
        )
    return FoolproofError(
        f"hotfix 体积与本傻瓜补丁不匹配\n"
        f"（实际 {size:,}，本包期望 {EXPECTED_SIZE:,}）。\n\n"
        f"请先用启动器「更新」客户端到最新，\n"
        f"再下载/使用适配该体积的新傻瓜补丁。"
    )


def run_foolproof_patch(
    game_root: Path | None = None,
    *,
    enable_nine: bool = True,
    burn_seal: bool = False,
    burn_seal_slow: bool = False,
    auto_catch: bool = False,
    auto_catch_nopet: bool = False,
    catch_pet: bool | None = None,
    daily_claim: bool = True,
    newbie_gift_code: bool = True,
    gift_codes: list[str] | str | None = None,
    apply_accel: bool = True,
    on_log: LogFn | None = None,
) -> list[str]:
    """一键诊断并打傻瓜补丁。成功返回消息列表；失败抛 FoolproofError。

    on_log：每产生一条进度即回调（GUI 可用来实时刷日志）。
    burn_seal：自动烧卡版（强制无九动 + 自动烧卡 · 最高加速）。
    burn_seal_slow：慢速烧卡版（烧卡逻辑同 burn_seal，但无任何加速）。
    auto_catch：自动抓宠版（强制无九动 + 自动抓宠）。catch_pet 为 auto_catch 旧别名。
    auto_catch_nopet：自动抓宠·无宠人防御（一级时 P2 人物防御；与普通抓宠互斥）。
    daily_claim：分享切页·日常领取（默认开）。
    newbie_gift_code：分享切页·新手礼包码（默认开）。
    gift_codes：可编辑礼包码（一行一个）；None 用默认 VIP/MLBB。
    apply_accel：是否打加速类 IL（战斗倍速/跑速/特效/过场）；默认开。关闭则全部不打。
    """
    messages: list[str] = []

    if catch_pet is not None:
        auto_catch = bool(catch_pet) or auto_catch
    seal_modes = sum(bool(x) for x in (burn_seal, burn_seal_slow, auto_catch, auto_catch_nopet))
    if seal_modes > 1:
        raise FoolproofError("自动烧卡 / 慢速烧卡 / 自动抓宠 / 自动抓宠（无宠人防御）只能选一个。")
    # 抓宠/烧卡档：只改加速预设，助手面板仍带抓宠+烧卡；九动仅「九动版·加速」带
    if burn_seal or burn_seal_slow or auto_catch or auto_catch_nopet:
        enable_nine = False

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

    _emit(messages, on_log, "附加：老板键限帧（隐藏→10FPS，恢复→还原）")

    if burn_seal_slow:
        _emit(
            messages,
            on_log,
            "预设：慢速烧卡档（助手面板：常规/抓宠/烧卡 · 无九动 · 无任何加速 · 一级含蝙蝠/哥布林）",
        )
        kwargs = dict(FOOLPROOF_BURN_SEAL_SLOW_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["auto_catch_nopet_external"] = False
        kwargs["wiki_test_ui"] = True
        kwargs["level_one_include_all"] = True
        kwargs["vip"] = False
        kwargs["vip_non_vip"] = False
        kwargs["map_sprint"] = False
        kwargs["skill_effect_speed"] = False
        kwargs["transition_speed"] = False
        nine_label = "无"
        nine_checks: list[str] = []
        extra_checks = ["auto_seal_external", "auto_catch_external", "level_one_include_all"]
    elif burn_seal:
        _emit(
            messages,
            on_log,
            "预设：烧卡加速档（助手面板：常规/抓宠/烧卡 · 无九动 · 倍速10x/特效5x · 一级含蝙蝠/哥布林）",
        )
        kwargs = dict(FOOLPROOF_BURN_SEAL_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["auto_catch_nopet_external"] = False
        kwargs["wiki_test_ui"] = True
        kwargs["level_one_include_all"] = True
        kwargs["vip_scale"] = 10
        kwargs["skill_effect_speed"] = True
        kwargs["skill_effect_scale"] = 5.0
        nine_label = "无"
        nine_checks: list[str] = []
        extra_checks = ["auto_seal_external", "auto_catch_external", "level_one_include_all"]
    elif auto_catch_nopet:
        _emit(
            messages,
            on_log,
            "预设：抓宠(无宠人防)档（助手面板：常规/抓宠无宠/烧卡 · 无九动 · 一级时 P2 人物防御）",
        )
        kwargs = dict(FOOLPROOF_AUTO_CATCH_NOPET_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = False
        kwargs["auto_catch_nopet_external"] = True
        kwargs["wiki_test_ui"] = True
        kwargs["level_one_include_all"] = True
        nine_label = "无"
        nine_checks = []
        extra_checks = ["auto_catch_nopet_external", "auto_seal_external", "level_one_include_all"]
    elif auto_catch:
        _emit(
            messages,
            on_log,
            "预设：抓宠加速档（助手面板：常规/抓宠/烧卡 · 无九动 · 5x）",
        )
        kwargs = dict(FOOLPROOF_AUTO_CATCH_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["auto_catch_nopet_external"] = False
        kwargs["wiki_test_ui"] = True
        kwargs["level_one_include_all"] = True
        nine_label = "无"
        nine_checks = []
        extra_checks = ["auto_catch_external", "auto_seal_external", "level_one_include_all"]
    elif enable_nine:
        use_il = False
        nine_label = "DLL版"
        _emit(
            messages,
            on_log,
            "预设：九动版加速（助手面板四选一：常规/九动/抓宠/烧卡 · 九动 DLL）",
        )
        kwargs = dict(FOOLPROOF_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = True
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["wiki_test_ui"] = True
        nine_checks = ["nine_external"]
        extra_checks = ["level_one_include_all"] if kwargs.get("level_one_include_all") else []
    else:
        use_il = False
        nine_label = "无"
        _emit(
            messages,
            on_log,
            "预设：融合/无九动加速（助手面板三选一：常规/抓宠/烧卡）",
        )
        kwargs = dict(FOOLPROOF_NO_NINE_COMBO_KWARGS)
        kwargs["battle_nine_action"] = False
        kwargs["battle_nine_external"] = False
        kwargs["auto_seal_external"] = True
        kwargs["auto_catch_external"] = True
        kwargs["wiki_test_ui"] = True
        nine_checks = []
        extra_checks = ["level_one_include_all"] if kwargs.get("level_one_include_all") else []

    kwargs["from_orig"] = True
    kwargs["inject_bridge"] = False
    kwargs["daily_claim"] = bool(daily_claim)
    kwargs["newbie_gift_code"] = bool(newbie_gift_code)
    kwargs["gift_codes"] = gift_codes
    kwargs["game_root"] = root
    kwargs["on_log"] = on_log

    # 不打加速：关闭全部加速类 IL（倍速/跑速/特效/过场）
    if not apply_accel:
        kwargs["vip"] = False
        kwargs["vip_non_vip"] = False
        kwargs["map_sprint"] = False
        kwargs["skill_effect_speed"] = False
        kwargs["transition_speed"] = False
        _emit(messages, on_log, "加速补丁：关（不打战斗倍速/跑速/特效/过场）")
    else:
        _emit(messages, on_log, "加速补丁：开")

    _emit(messages, on_log, "正在余量预检（启动补丁引擎，首次可能较慢）…")
    try:
        if burn_seal_slow or not apply_accel:
            precheck = ["longpress"]
        else:
            precheck = ["vip", "sprint", "longpress", "skill_effect"]
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

    seal_part = " · 自动烧卡" if kwargs.get("auto_seal_external") else ""
    if kwargs.get("auto_catch_nopet_external"):
        catch_part = " · 自动抓宠(无宠人防)"
    elif kwargs.get("auto_catch_external"):
        catch_part = " · 自动抓宠"
    else:
        catch_part = ""
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
    if kwargs.get("transition_speed"):
        tr = kwargs.get("transition_speed_scale", 0.4)
        tr_part = f" · 过场{tr}s"
    else:
        tr_part = " · 无加速过场"
    if burn_seal_slow or not apply_accel:
        profile = "慢速烧卡 · " if burn_seal_slow else ""
        _emit(
            messages,
            on_log,
            f"已应用：{profile}无战斗倍速 · 原速跑图 · 长按详情"
            f"{tr_part} · 无特效加速{seal_part}{catch_part}{gm_part}{daily_part}{nine_part}"
            + (" · 一级含蝙蝠/哥布林" if kwargs.get("level_one_include_all") else ""),
        )
    else:
        vip = kwargs.get("vip_scale", 5)
        fx = kwargs.get("skill_effect_scale", 2.0)
        if burn_seal:
            profile = "自动烧卡 · "
        elif auto_catch_nopet:
            profile = "自动抓宠(无宠人防) · "
        elif auto_catch:
            profile = "自动抓宠 · "
        else:
            profile = ""
        _emit(
            messages,
            on_log,
            f"已应用：{profile}VIP{vip}x · Sprint快 · 长按详情"
            f"{tr_part} · 特效{fx}x{seal_part}{catch_part}{gm_part}{daily_part}{nine_part}"
            + (" · 一级含蝙蝠/哥布林" if kwargs.get("level_one_include_all") else ""),
        )
    try:
        if burn_seal_slow:
            marked = "foolproof_burn_seal_slow"
        elif burn_seal:
            marked = "foolproof_burn_seal"
        elif auto_catch_nopet:
            marked = "foolproof_auto_catch_nopet"
        elif auto_catch:
            marked = "foolproof_auto_catch"
        else:
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
