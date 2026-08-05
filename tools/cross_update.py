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
  status  查看 cross 与 crosscopy 的状态（版本/大小/hash）
  restore 把 cross 还原为官方原版（需关闭游戏）
  diff    对比两个目录，生成更新报告（反外挂相关高亮）
  sync    把 crosscopy 中的官方差异同步到 cross（需关闭游戏）

路径可通过环境变量 CROSS_ROOT / CROSSCOPY_ROOT 覆盖，默认：
  CROSS    = E:\\cross\\魔力宝贝：序章
  CROSSCOPY = E:\\crosscopy\\魔力宝贝：序章
"""
from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
import time
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
        elif args.full_hash or is_anticheat_relevant(r):
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

    args = parser.parse_args(argv)
    cross, copy = resolve_paths()
    try:
        return args.fn(cross, copy, args)
    except (FileNotFoundError, RuntimeError) as exc:
        _log(f"[错误] {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
