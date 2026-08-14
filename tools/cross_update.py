#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本地目录更新规则：cross（实际游戏）↔ crosscopy（纯净官方副本）。

流程（客户端更新时按顺序执行）：
  1) python tools/cross_update.py status   —— 查看两个目录状态
  2) python tools/cross_update.py restore  —— 还原 cross 到官方原版（hotfix ← .orig，移走补丁 DLL）
  3) 用户手动在 crosscopy 里运行官方更新器（crosscopy 保持纯净，不打补丁）
  4) python tools/cross_update.py diff     —— 对比 cross vs crosscopy，输出报告（重点反外挂）
  5) python tools/cross_update.py sync     —— 用 crosscopy 更新 cross（同步新增/修改的官方文件）
  6) 在 cross 下重新打补丁（GUI：关闭游戏 → 初始化 → 应用补丁；DLL 由补丁自动部署，无需手动拷贝）

子命令：
  status      查看 cross 与 crosscopy 的状态（版本/大小/hash）
  restore     把 cross 还原为官方原版（需关闭游戏）
  diff        对比两个目录，生成更新报告（反外挂相关高亮）
  sync        把 crosscopy 中的官方差异同步到 cross（需关闭游戏）
  anti-cheat  只读：对比旧底稿与 crosscopy 新 hotfix，做反外挂深度分析
  auto-update 一条龙：检测更新→探测反外挂→同步→换新底稿→重打默认组合补丁

固定流程（crosscopy 更新后）：
  python tools/cross_update.py auto-update --dry-run   # 先只读探测反外挂
  python tools/cross_update.py auto-update             # 确认无风险后一条龙完成

路径可通过环境变量 CROSS_ROOT / CROSSCOPY_ROOT 覆盖，默认：
  CROSS    = E:\\cross\\魔力宝贝：序章
  CROSSCOPY = E:\\crosscopy\\魔力宝贝：序章
"""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import os
import re
import shutil
import struct
import sys
import time
from collections import defaultdict
from datetime import datetime
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
PROJECT = TOOLS.parent

DEFAULT_CROSS_ROOT = Path(r"E:\cross\魔力宝贝：序章")
DEFAULT_CROSSCOPY_ROOT = Path(r"E:\crosscopy\魔力宝贝：序章")

DATA_DIR = "cg37_Data"
HOTFIX_REL = Path(DATA_DIR) / "assets" / "hotfixdata" / "hotfix.dll.bytes"
ORIG_REL = Path(HOTFIX_REL).with_name("hotfix.dll.bytes.orig")

# 补丁产物（还原时需要移走/备份）；路径相对游戏根，含 hotfixdata 子目录
PATCH_ARTIFACTS = (
    "SeqChapter*.dll.bytes",
    "battle_appear.json",
    "seqchapter_*.txt",
)

# 反外挂重点文件：出现变化时在报告中高亮
ANTI_CHEAT_HINTS = (
    "GameAssembly.dll",
    "UnityPlayer.dll",
    "baselib.dll",
    "Update.exe",
    "Uninstall.exe",
    "cg37.exe",
    "hotfix.dll.bytes",
    "hotfix.core.dll.bytes",
    "moli.dll.bytes",
    "assembly-csharp",
    "globalgamemanagers",
    "il2cpp_data",
    ".setup",
    "data.unity3d",
)

# 官方更新器解压缓存目录（跨版本后旧缓存无意义，sync 时安全对齐）
INSTALL_MARKER = r"cg37_Data\install"

# ---- 反外挂深度分析（hotfix 内容） ----
# 关键词在 UTF-16LE / UTF-8 双编码下直接字节搜索（不依赖解码）
SECURITY_KEYWORDS = [
    "cheat", "anticheat", "anti", "detect", "ban", "report", "speed", "accelerate",
    "加速", "外挂", "检测", "封号", "校验", "CRC", "MD5", "hash", "signature",
    "心跳", "heartbeat", "Time.timeScale", "时间", "Kick", "KickPlayer", "GM",
    "security", "Security", "Warden", "防沉迷", "verify", "Validate", "Checksum", "篡改",
]
SECURITY_STRING_PATTERNS = re.compile(
    r"(?i)(cheat|anticheat|anti.?cheat|detect|ban|report|speed|accelerate|"
    r"外挂|检测|封号|校验|heartbeat|kick|warden|security|verify|validate|checksum|"
    r"篡改|time\.timescale|防沉迷|gm\b|crc|md5|signature|hash)",
)


def search_keywords(data: bytes, keywords: list[str]) -> dict[str, list[str]]:
    hits: dict[str, list[str]] = defaultdict(list)
    for kw in keywords:
        for enc in ("utf-16-le", "utf-8"):
            try:
                needle = kw.encode(enc)
            except Exception:
                continue
            if needle in data:
                hits[kw].append(enc)
    return dict(hits)


def extract_utf16_strings(data: bytes, min_len: int = 4) -> set[str]:
    strings: set[str] = set()
    i = 0
    n = len(data)
    while i < n - 1:
        if data[i] >= 0x20 and data[i + 1] == 0:
            start = i
            chars = []
            while i < n - 1:
                lo, hi = data[i], data[i + 1]
                if hi != 0:
                    break
                if lo == 0:
                    break
                if lo < 0x20 and lo not in (9, 10, 13):
                    break
                try:
                    chars.append(chr(lo))
                except ValueError:
                    break
                i += 2
            s = "".join(chars)
            if len(s) >= min_len:
                strings.add(s)
        i += 1
    return strings


def extract_utf8_strings(data: bytes, min_len: int = 4) -> set[str]:
    strings: set[str] = set()
    cur: list[int] = []
    for b in data:
        if 0x20 <= b <= 0x7E or b in (9, 10, 13) or b >= 0xC0:
            cur.append(b)
        else:
            if len(cur) >= min_len:
                try:
                    s = bytes(cur).decode("utf-8", errors="strict")
                    if len(s) >= min_len:
                        strings.add(s)
                except UnicodeDecodeError:
                    pass
            cur = []
    if len(cur) >= min_len:
        try:
            s = bytes(cur).decode("utf-8", errors="strict")
            if len(s) >= min_len:
                strings.add(s)
        except UnicodeDecodeError:
            pass
    return strings


def pe_size(data: bytes) -> int | None:
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if e_lfanew + 0x18 > len(data):
        return None
    if data[e_lfanew : e_lfanew + 4] != b"PE\x00\x00":
        return None
    opt_off = e_lfanew + 0x18
    magic = struct.unpack_from("<H", data, opt_off)[0]
    if magic in (0x10B, 0x20B):  # PE32 / PE32+
        return struct.unpack_from("<I", data, opt_off + 0x38)[0]
    return None


def analyze_hotfix_anticheat(old_data: bytes, new_data: bytes) -> dict:
    """只读对比两份 hotfix，返回反外挂相关分析（不写盘）。"""
    out: dict = {}
    out["old_size"] = len(old_data)
    out["new_size"] = len(new_data)
    out["size_delta"] = len(new_data) - len(old_data)
    out["old_pe_size_of_image"] = pe_size(old_data)
    out["new_pe_size_of_image"] = pe_size(new_data)

    old_kw = search_keywords(old_data, SECURITY_KEYWORDS)
    new_kw = search_keywords(new_data, SECURITY_KEYWORDS)
    out["keywords_new_not_old"] = sorted(set(new_kw) - set(old_kw))
    out["keywords_only_in_new"] = {k: v for k, v in new_kw.items() if k not in old_kw}

    all_old = extract_utf16_strings(old_data) | extract_utf8_strings(old_data)
    all_new = extract_utf16_strings(new_data) | extract_utf8_strings(new_data)
    new_only = all_new - all_old
    sec_new = sorted(s for s in new_only if SECURITY_STRING_PATTERNS.search(s))
    out["security_strings_only_in_new"] = sec_new[:120]
    out["security_strings_only_in_new_count"] = len(sec_new)

    interesting_new = sorted(
        s for s in new_only
        if SECURITY_STRING_PATTERNS.search(s)
        or (re.search(r"[\u4e00-\u9fff]", s)
            and re.search(r"(检测|校验|加速|外挂|封号|心跳|篡改|防沉迷|安全|举报|踢|GM|时间)", s))
    )[:80]
    out["interesting_new_strings"] = interesting_new

    has_new_security = bool(out["keywords_new_not_old"] or sec_new)
    if len(old_data) == len(new_data) and old_data == new_data:
        out["verdict"] = "no"
        out["verdict_note"] = "新旧 hotfix 完全一致"
    else:
        out["verdict"] = "yes" if has_new_security else ("uncertain" if new_kw else "no")
    return out


def format_anticheat_report(ac: dict, old_label: str, new_label: str) -> str:
    lines = [
        "=" * 70,
        "反外挂内容对比（hotfix 深度分析）",
        f"旧: {old_label}",
        f"新: {new_label}",
        "-" * 70,
        f"体积: {ac['old_size']:,} -> {ac['new_size']:,} 字节 (Δ{ac['size_delta']:+,})",
        f"PE SizeOfImage: {ac.get('old_pe_size_of_image')} -> {ac.get('new_pe_size_of_image')}",
        f"新增关键词: {len(ac['keywords_new_not_old'])}  |  新增安全字符串: {ac['security_strings_only_in_new_count']}",
    ]
    if ac["keywords_new_not_old"]:
        lines.append("[新增关键词] " + ", ".join(ac["keywords_new_not_old"]))
    sec = ac["security_strings_only_in_new"]
    if sec:
        lines.append("[新增安全字符串]")
        for s in sec[:60]:
            lines.append(f"    {s}")
    inter = ac["interesting_new_strings"]
    if inter:
        lines.append("[其他值得注意的新字符串]")
        for s in inter[:40]:
            lines.append(f"    {s}")
    verdict = ac["verdict"]
    note = {
        "yes": "⚠ 检测到反外挂/上报相关变化，打补丁前请人工核对",
        "uncertain": "存在安全类关键词但无新增，风险低",
        "no": "✓ 未检测到反外挂相关变化",
    }[verdict]
    if ac.get("verdict_note"):
        note += f"（{ac['verdict_note']}）"
    lines.append("=" * 70)
    lines.append(f"结论: {note}")
    return "\n".join(lines)


def _log(msg: str) -> None:
    print(msg)


def resolve_paths() -> tuple[Path, Path]:
    cross = Path(os.environ.get("CROSS_ROOT") or DEFAULT_CROSS_ROOT).resolve()
    copy = Path(os.environ.get("CROSSCOPY_ROOT") or DEFAULT_CROSSCOPY_ROOT).resolve()
    return cross, copy


def ensure_roots(cross: Path, copy: Path) -> None:
    for label, p in (("CROSS", cross), ("CROSSCOPY", copy)):
        if not p.is_dir():
            raise FileNotFoundError(f"{label} 目录不存在: {p}")
        if not (p / DATA_DIR).is_dir():
            raise RuntimeError(f"{label} 不是有效游戏目录（缺少 {DATA_DIR}）: {p}")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def file_digest(path: Path) -> dict:
    stat = path.stat()
    return {"size": stat.st_size, "sha256": sha256_file(path), "mtime": stat.st_mtime}


def iter_game_files(root: Path) -> list[Path]:
    """递归收集游戏目录下所有文件（排除 .git 与明显临时/备份目录）。"""
    skip_dirs = {".git", "__pycache__", "_seq_patch_backup"}
    out: list[Path] = []
    for base, dirs, files in os.walk(root):
        base_p = Path(base)
        rel = base_p.relative_to(root)
        dirs[:] = [d for d in dirs if d not in skip_dirs]
        for f in files:
            out.append(base_p / f)
    return out


def is_anticheat_relevant(rel: str) -> bool:
    low = rel.lower()
    return any(hint.lower() in low for hint in ANTI_CHEAT_HINTS)


def _should_hash_verify(rel: str) -> bool:
    """默认做哈希校验的路径：同大小但内容可能更新的关键文件。

    覆盖启动场景/全局配置/插件/热更程序集等小文件；排除 StreamingAssets 与
    assets 下的资源文件（量大、通常体积变化，交给 --full-hash 全量比对）。
    """
    low = rel.lower().replace("\\", "/")
    for prefix in ("cg37_data/streamingassets/", "cg37_data/assets/", "cg37_data/install/"):
        if low.startswith(prefix):
            return False
    core_names = (
        "level0", "level1", "sharedassets0.assets", "globalgamemanagers",
        "globalgamemanagers.assets", "globalgamemanagers.assets.resS",
        "resources.assets", "resources.assets.resS",
        "boot.config", "app.info", "server_filestructure.bin", "filestructure.bin",
        "softinfo.bin", "abinfo.bin", "scriptingassemblies.json",
        "runtimeinitializeonloads.json",
        "npgamedll.dll", "openccunitybridge.dll", "processpick.dll", "liblzma.dll",
        "exprtk_condition.dll", "sqlite3.dll",
        "hotfix.dll.bytes", "hotfix.core.dll.bytes", "assembly-csharp.dll.bytes",
        "assembly-csharp-e.dll.bytes", "chineseconverter.dll.bytes", "moli.dll.bytes",
        "uninstall.dat", "unitycrashhandler64.exe",
        "update.exe", "uninstall.exe", "cg37.exe",
        "gamedll.dll", "baselib.dll", "unityplayer.dll",
    )
    return any(name in low for name in core_names)



def cmd_status(cross: Path, copy: Path, args) -> int:
    ensure_roots(cross, copy)
    for label, p in (("CROSS", cross), ("CROSSCOPY", copy)):
        _log(f"===== {label}: {p} =====")
        exe = p / "cg37.exe"
        _log(f"  cg37.exe: {'存在' if exe.is_file() else '缺失'}")
        for name in ("Update.exe", "Uninstall.exe"):
            _log(f"  {name}: {'存在' if (p / name).is_file() else '缺失'}")
        setups = sorted(p.glob("*.setup"))
        if setups:
            _log("  更新包: " + ", ".join(f.stem for f in setups))
        else:
            _log("  更新包: 无")
        hf = p / HOTFIX_REL
        if hf.is_file():
            info = file_digest(hf)
            _log(f"  hotfix.dll.bytes: {info['size']:,} 字节  sha {info['sha256'][:12]}…")
        else:
            _log("  hotfix.dll.bytes: 缺失")
        orig = p / ORIG_REL
        if orig.is_file():
            oinfo = file_digest(orig)
            match = "一致" if oinfo["sha256"] == (info["sha256"] if hf.is_file() else None) else "不一致"
            _log(f"  hotfix.dll.bytes.orig: {oinfo['size']:,} 字节（与 hotfix {match}）")
        else:
            _log("  hotfix.dll.bytes.orig: 缺失")
        arts = [a for a in PATCH_ARTIFACTS if list(p.glob(a))]
        if arts:
            _log(f"  补丁残留: {', '.join(arts)}")
        else:
            _log("  补丁残留: 无")
    return 0


def _move_patch_artifacts(game_root: Path) -> list[str]:
    """把补丁产物移到 _seq_patch_backup_<时间戳> 目录，返回消息。

    补丁 DLL 与配置文件都位于 cg37_Data/assets/hotfixdata/（SeqChapter*.dll.bytes、
    battle_appear.json、seqchapter_*.txt）；旧版曾在根目录放 SeqChapter 文件，一并扫。
    """
    msgs: list[str] = []
    moved: list[Path] = []
    hotfixdata = game_root / DATA_DIR / "assets" / "hotfixdata"
    scan_dirs = [game_root]
    if hotfixdata.is_dir():
        scan_dirs.append(hotfixdata)
    for base in scan_dirs:
        for pat in PATCH_ARTIFACTS:
            for f in base.glob(pat):
                if f not in moved:
                    moved.append(f)
    if not moved:
        msgs.append("未发现补丁残留文件")
        return msgs
    bak = game_root / f"_seq_patch_backup_{datetime.now():%Y%m%d_%H%M%S}"
    bak.mkdir(parents=True, exist_ok=True)
    for f in moved:
        try:
            shutil.move(str(f), bak / f.name)
            msgs.append(f"  已移走: {f.name}")
        except OSError as exc:
            msgs.append(f"  [WARN] 移动失败 {f.name}: {exc}")
    return msgs


def cmd_restore(cross: Path, copy: Path, args) -> int:
    ensure_roots(cross, copy)
    _check_game_closed(cross)
    msgs: list[str] = []
    msgs.append(f"还原 {cross} 到官方原版…")

    hf = cross / HOTFIX_REL
    orig = cross / ORIG_REL
    if orig.is_file():
        if hf.is_file() and sha256_file(hf) == sha256_file(orig):
            msgs.append("  hotfix 已与 .orig 一致（跳过）")
        else:
            shutil.copy2(orig, hf)
            msgs.append(f"  hotfix ← .orig（{orig.stat().st_size:,} 字节）")
    elif hf.is_file():
        msgs.append("  [WARN] 缺少 .orig，无法还原 hotfix（保持现状）")
    else:
        msgs.append("  [WARN] hotfix 与 .orig 均不存在")

    msgs.extend(_move_patch_artifacts(cross))

    combo_state = _combo_state_path()
    if combo_state.is_file():
        combo_state.unlink()
        msgs.append("  已清除补丁状态 combo_patch_state.json")

    for line in msgs:
        _log(line)
    _log("还原完成：cross 已回到官方原版，可继续在 crosscopy 中运行官方更新器。")
    return 0


def _combo_state_path() -> Path:
    cands = (
        PROJECT / "魔力宝贝序章补丁" / "combo_patch_state.json",
        PROJECT / "魔力宝贝序章补丁" / "scripts" / "combo_patch_state.json",
    )
    for c in cands:
        if c.is_file():
            return c
    return cands[0]


def _check_game_closed(game_root: Path) -> None:
    exe = game_root / "cg37.exe"
    if not exe.is_file():
        return
    import subprocess

    proc = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq cg37.exe", "/NH"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if "cg37.exe" in (proc.stdout or ""):
        _log("[错误] 游戏正在运行，请先关闭 cg37.exe 再执行该操作。")
        raise SystemExit(2)


def cmd_diff(cross: Path, copy: Path, args) -> int:
    ensure_roots(cross, copy)
    _log("正在对比两个目录，请稍候…")
    cross_files = iter_game_files(cross)
    copy_files = iter_game_files(copy)
    _log(f"  CROSS 文件数: {len(cross_files)}")
    _log(f"  CROSSCOPY 文件数: {len(copy_files)}")

    # 用相对各自 root 的路径作 key
    cross_map = {str(p.relative_to(cross)): p for p in cross_files}
    copy_map = {str(p.relative_to(copy)): p for p in copy_files}

    added = sorted(set(copy_map) - set(cross_map))      # 仅 crosscopy 有 → 官方新增
    removed = sorted(set(cross_map) - set(copy_map))    # 仅 cross 有 → 补丁残留/已删除
    common = sorted(set(cross_map) & set(copy_map))

    modified: list[str] = []
    for rel in common:
        cp = copy_map[rel]
        gp = cross_map[rel]
        if cp.stat().st_size != gp.stat().st_size:
            modified.append(rel)
            continue
        if args.full_hash or is_anticheat_relevant(rel):
            if sha256_file(cp) != sha256_file(gp):
                modified.append(rel)

    lines: list[str] = []
    lines.append("=" * 70)
    lines.append(f"cross ↔ crosscopy 更新对比报告")
    lines.append(f"时间: {datetime.now():%Y-%m-%d %H:%M:%S}")
    lines.append(f"CROSS    = {cross}")
    lines.append(f"CROSSCOPY = {copy}")
    lines.append(f"cross 文件数={len(cross_files)}  crosscopy 文件数={len(copy_files)}")
    lines.append(f"新增(仅crosscopy)={len(added)}  修改={len(modified)}  删除(仅cross)={len(removed)}")
    lines.append("=" * 70)

    def section(title: str, items: list[str]) -> None:
        lines.append(f"\n### {title}（{len(items)}）")
        if not items:
            lines.append("    （无）")
            return
        cache = [i for i in items if i.startswith(INSTALL_MARKER)]
        real = [i for i in items if not i.startswith(INSTALL_MARKER)]
        if real:
            anti = [i for i in real if is_anticheat_relevant(i)]
            others = [i for i in real if not is_anticheat_relevant(i)]
            if anti:
                lines.append("  [反外挂重点]")
                for i in anti:
                    lines.append(f"    !!! {i}")
            if others:
                lines.append("  [普通文件]")
                for i in others:
                    lines.append(f"    {i}")
        if cache:
            lines.append("  [更新器缓存 install\\]")
            for i in cache:
                lines.append(f"    {i}")

    section("官方新增（crosscopy 有，cross 无）", added)
    section("修改（两边都有但内容不同）", modified)
    section("cross 独有（补丁残留或已删除）", removed)

    report = "\n".join(lines)
    _log(report)

    report_dir = PROJECT / "tools" / "update_reports"
    report_dir.mkdir(parents=True, exist_ok=True)
    rp = report_dir / f"cross_update_{datetime.now():%Y%m%d_%H%M%S}.txt"
    rp.write_text(report, encoding="utf-8")
    _log(f"\n报告已保存: {rp}")

    # 反外挂总结
    anti_any = [i for i in added + modified if is_anticheat_relevant(i)]
    if anti_any:
        _log("\n[提示] 检测到反外挂相关文件变化，请重点核对（尤其 GameAssembly.dll / hotfix / .setup / 新增的 .exe/.dll）。")
    else:
        _log("\n[提示] 未检测到反外挂重点文件变化。")
    return 0


def cmd_anticheat(cross: Path, copy: Path, args) -> int:
    """只读：对比 cross(旧) 与 crosscopy(新) 的 hotfix，做反外挂深度分析。"""
    ensure_roots(cross, copy)

    hf_rel = HOTFIX_REL
    old_candidates = [
        cross / ORIG_REL,                 # 干净旧底稿（优先）
        copy / hf_rel,                    # 回退：crosscopy 当前（无 .orig 时）
    ]
    old_path = next((p for p in old_candidates if p.is_file()), None)
    new_path = copy / hf_rel

    if old_path is None or not new_path.is_file():
        _log(f"[错误] 缺少对比文件: old={old_path} new={new_path}")
        return 1

    old_data = old_path.read_bytes()
    new_data = new_path.read_bytes()
    ac = analyze_hotfix_anticheat(old_data, new_data)
    old_label = f"{cross.parent.name}/{cross.name}/{old_path.relative_to(cross)}"
    if old_path.samefile(copy / hf_rel):
        old_label = f"{copy.parent.name}/{copy.name}/hotfix.dll.bytes（回退：cross 无 .orig）"
    print(format_anticheat_report(ac, old_label, f"{copy.parent.name}/{copy.name}/hotfix.dll.bytes"))
    return 0 if ac["verdict"] == "no" else 2 if ac["verdict"] == "yes" else 1


def cmd_sync(cross: Path, copy: Path, args) -> int:
    ensure_roots(cross, copy)
    _check_game_closed(cross)
    _log("从 crosscopy 同步到 cross（install 缓存目录镜像对齐，其余只复制官方新增/修改文件）…")

    cross_files = iter_game_files(cross)
    copy_files = iter_game_files(copy)
    cross_map = {str(p.relative_to(cross)): p for p in cross_files}
    copy_map = {str(p.relative_to(copy)): p for p in copy_files}

    added = sorted(set(copy_map) - set(cross_map))
    common = sorted(set(copy_map) & set(cross_map))
    modified: list[str] = []
    for r in common:
        if copy_map[r].stat().st_size != cross_map[r].stat().st_size:
            modified.append(r)
        elif args.full_hash or is_anticheat_relevant(r) or _should_hash_verify(r):
            if sha256_file(copy_map[r]) != sha256_file(cross_map[r]):
                modified.append(r)
    removed = sorted(set(cross_map) - set(copy_map))

    to_copy = sorted(set(added) | set(modified))
    to_delete = [r for r in removed if r.startswith(INSTALL_MARKER)]

    if not to_copy and not to_delete:
        _log("无差异，无需同步。")
        return 0

    if to_copy:
        _log(f"待同步 {len(to_copy)} 个文件：")
        for rel in to_copy:
            tag = "  [反外挂] " if is_anticheat_relevant(rel) else "  "
            _log(f"{tag}{rel}")
    if to_delete:
        _log(f"待清理 {len(to_delete)} 个旧更新器缓存文件（install\\，cross 独有）：")
        for rel in to_delete:
            _log(f"  [清理] {rel}")

    if args.dry_run:
        _log("\n（--dry-run：仅列出，未实际复制/删除）")
        return 0

    n = 0
    for rel in to_copy:
        src = copy_map[rel]
        dst = cross_map.get(rel) or (cross / rel)
        try:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            n += 1
        except OSError as exc:
            _log(f"  [WARN] 同步失败 {rel}: {exc}")

    d = 0
    for rel in to_delete:
        target = cross_map[rel]
        try:
            target.unlink()
            d += 1
        except OSError as exc:
            _log(f"  [WARN] 清理失败 {rel}: {exc}")

    # 清理空的 install 目录层级
    inst = cross / INSTALL_MARKER
    if inst.is_dir():
        for _ in range(3):
            emptied = False
            for dpath, dirnames, filenames in os.walk(inst, topdown=False):
                if not dirnames and not filenames:
                    try:
                        os.rmdir(dpath)
                        emptied = True
                    except OSError:
                        pass
            if not emptied:
                break

    _log(f"同步完成：复制 {n} 个、清理旧缓存 {d} 个。")
    _log("接下来请在 cross 目录下重新打补丁（GUI：关闭游戏 → 初始化 → 应用补丁）。")
    return 0


def _load_baseline_meta(cross: Path) -> dict | None:
    meta = cross / "tools" / "hotfix_baseline.json"
    if not meta.is_file():
        return None
    try:
        return json.loads(meta.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _patch_scripts_dir() -> Path:
    return PROJECT / "魔力宝贝序章补丁" / "scripts"


def _apply_default_combo(cross: Path, on_log=None) -> list[str]:
    """在 cross 目录跑默认组合补丁（DEFAULT_COMBO_KWARGS，from_orig=True）。"""
    import importlib.util

    scripts = _patch_scripts_dir()
    sys.path.insert(0, str(scripts))
    try:
        spec = importlib.util.find_spec("apply_combo_patch")
        if spec is None:
            raise FileNotFoundError(f"找不到 apply_combo_patch.py: {scripts}")
        ac = importlib.import_module("apply_combo_patch")
        pd = importlib.import_module("patch_defaults")
        msgs = ac.apply_combo(**pd.DEFAULT_COMBO_KWARGS, game_root=cross, on_log=on_log)
        return msgs
    finally:
        if str(scripts) in sys.path:
            sys.path.remove(str(scripts))


def cmd_auto_update(cross: Path, copy: Path, args) -> int:
    """一条龙：crosscopy 有更新 → 探测反外挂 → 同步 → 换新底稿 → 重打默认组合补丁。

    流程（固定化，减少人工判断）：
      1) 对比 crosscopy hotfix 与 baseline neworig_sha256 —— 是否官方更新。
      2) 有更新 → 反外挂深度分析（cross .orig 旧底稿 vs crosscopy 新 hotfix）。
         - verdict=yes 或 --require-clear 且非 no → 停下，报告人工核对。
      3) --dry-run 只到第 2 步（只读，不写任何文件）。
      4) sync 同步官方文件到 cross（复用 cmd_sync 逻辑）。
      5) 从 crosscopy 复制干净 hotfix 到 cross 的 tools/hotfix.dll.bytes.neworig，
         更新 EXPECTED_SIZE 常量（体积变时）并重建引擎。
      6) sync_client_baseline（对齐 .orig + baseline meta）。
      7) 重打默认组合补丁（from_orig=True，从干净底稿出发）。
    """
    ensure_roots(cross, copy)

    copy_hotfix = copy / HOTFIX_REL
    if not copy_hotfix.is_file():
        _log(f"[错误] crosscopy 缺少 hotfix: {copy_hotfix}")
        return 1

    meta = _load_baseline_meta(cross)
    meta_sha = (meta or {}).get("neworig_sha256")
    copy_sha = sha256_file(copy_hotfix)
    copy_size = copy_hotfix.stat().st_size

    if meta_sha and copy_sha == meta_sha:
        _log("crosscopy hotfix 与基线一致 → 未检测到官方更新。")
        if not args.force:
            _log("如需强制重打补丁，请加 --force。")
            return 0
        _log("--force：继续强制重打补丁。")

    if not meta_sha:
        _log("[提示] 无基线记录（tools/hotfix_baseline.json 缺失）——按首次/新底稿处理。")

    # ---- 2) 反外挂深度分析（只读） ----
    old_candidates = [
        cross / ORIG_REL,
        cross / HOTFIX_REL,
    ]
    old_path = next((p for p in old_candidates if p.is_file()), None)
    if old_path is None:
        _log("[提示] cross 无 .orig/hotfix 可作旧底稿参考，跳过反外挂内容对比。")
        ac: dict | None = None
    else:
        old_label = f"{cross.parent.name}/{cross.name}/{old_path.relative_to(cross)}"
        new_label = f"{copy.parent.name}/{copy.name}/hotfix.dll.bytes"
        ac = analyze_hotfix_anticheat(old_path.read_bytes(), copy_hotfix.read_bytes())
        print(format_anticheat_report(ac, old_label, new_label))

    if args.dry_run:
        _log("\n--dry-run：仅探测，未做任何写入。")
        return 0

    if ac is not None and ac["verdict"] == "yes":
        if getattr(args, "confirm_anticheat", False):
            _log("\n[确认] 已人工核对反外挂变化（--confirm-anticheat），判定无风险，继续同步补丁。")
        else:
            _log("\n[停止] 检测到反外挂/上报相关变化，需人工核对后再同步补丁。")
            _log("确认无风险后请加 --confirm-anticheat 重新执行。")
            return 3
    if getattr(args, "require_clear", False) and ac is not None and ac["verdict"] != "no":
        _log("\n[停止] --require-clear：反外挂结论非 no，暂停同步。")
        return 3

    # ---- 3) 同步官方文件 ----
    _log("\n[同步] 用 crosscopy 更新 cross 的官方文件…")
    rc = cmd_sync(cross, copy, args)
    if rc != 0:
        return rc

    # ---- 4) 换新底稿：crosscopy hotfix → cross tools/neworig ----
    _log("\n[底稿] 从 crosscopy 复制干净 hotfix 作为新底稿…")
    neworig = cross / "tools" / "hotfix.dll.bytes.neworig"
    neworig.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(copy_hotfix, neworig)
        _log(f"  已复制: crosscopy hotfix → {neworig.relative_to(cross)}")
    except OSError as exc:
        _log(f"  [错误] 复制底稿失败: {exc}")
        return 4

    # ---- 5) 同步 EXPECTED_SIZE / 重建引擎 / 对齐 .orig 与 baseline ----
    _log("\n[基线] 更新 EXPECTED_SIZE 常量、重建引擎、对齐 .orig/baseline…")
    scripts = _patch_scripts_dir()
    sys.path.insert(0, str(scripts))
    try:
        pc = importlib.import_module("patch_common")
        bump_msgs = pc._bump_expected_size_constants(copy_size)
        for m in bump_msgs:
            _log("  " + m)
        if not bump_msgs:
            _log("  EXPECTED_SIZE 未变，无需重建引擎")
        base_msgs = pc.sync_client_baseline(cross, force=True)
        for m in base_msgs:
            _log("  " + m)
    finally:
        if str(scripts) in sys.path:
            sys.path.remove(str(scripts))

    # ---- 6) 重打默认组合补丁 ----
    _log("\n[补丁] 从干净 .orig 重打默认组合…")
    msgs = _apply_default_combo(cross)
    for m in msgs:
        _log("  [OK] " + m)
    _log("\n完成：cross 已更新至 crosscopy 版本并重打默认组合补丁。")
    _log("提示：请关闭游戏后再打补丁；运行中的客户端需重启新窗口才生效。")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_status = sub.add_parser("status", help="查看两个目录状态")
    p_status.set_defaults(fn=cmd_status)

    p_restore = sub.add_parser("restore", help="还原 cross 到官方原版")
    p_restore.set_defaults(fn=cmd_restore)

    p_diff = sub.add_parser("diff", help="对比并生成更新报告")
    p_diff.add_argument("--full-hash", action="store_true", help="同大小文件也逐一比对 hash（较慢）")
    p_diff.set_defaults(fn=cmd_diff)

    p_sync = sub.add_parser("sync", help="用 crosscopy 更新 cross")
    p_sync.add_argument("--dry-run", action="store_true", help="只列出不复制")
    p_sync.add_argument("--full-hash", action="store_true", help="同大小文件也逐一比对 hash")
    p_sync.set_defaults(fn=cmd_sync)

    p_ac = sub.add_parser("anti-cheat", help="只读：hotfix 反外挂深度分析（旧底稿 vs crosscopy 新版本）")
    p_ac.set_defaults(fn=cmd_anticheat)

    p_au = sub.add_parser(
        "auto-update",
        help="一条龙：crosscopy 有更新→探测反外挂→同步→换新底稿→重打默认组合补丁",
    )
    p_au.add_argument("--dry-run", action="store_true", help="只探测反外挂，不写任何文件")
    p_au.add_argument("--force", action="store_true", help="基线一致时也强制重打补丁")
    p_au.add_argument("--require-clear", action="store_true", help="反外挂结论非 no 即停止（默认仅 yes 停止）")
    p_au.add_argument("--confirm-anticheat", action="store_true", help="人工已核对反外挂变化无风险，跳过拦截继续同步补丁")
    p_au.add_argument("--full-hash", action="store_true", help="sync 阶段同大小文件也逐一比对 hash")
    p_au.set_defaults(fn=cmd_auto_update)

    args = parser.parse_args(argv)
    cross, copy = resolve_paths()
    try:
        return args.fn(cross, copy, args)
    except (FileNotFoundError, RuntimeError) as exc:
        _log(f"[错误] {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
