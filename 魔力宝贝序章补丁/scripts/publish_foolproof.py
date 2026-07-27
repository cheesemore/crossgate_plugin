#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「傻瓜补丁」独立包（含 HotfixPatcher + 一键打补丁.bat）→ 发布/*.zip。

用法：
  python publish_foolproof.py                 # 默认含九动
  python publish_foolproof.py --no-nine       # 无九动变体
  python publish_foolproof.py --seal-catch    # 烧卡/抓宠合一（界面二选一）
  python publish_foolproof.py --burn-seal     # 同 --seal-catch（兼容旧参数）
  python publish_foolproof.py --auto-catch    # 同 --seal-catch（兼容旧参数）
"""
from __future__ import annotations

import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
TOOLKIT_ROOT = SCRIPTS_DIR.parent
GAME_ROOT = TOOLKIT_ROOT.parent
RELEASE_DIR = TOOLKIT_ROOT / "发布"
STAGING_DIR = RELEASE_DIR / "_foolproof_build"
DIST_DIR = RELEASE_DIR / "dist_foolproof"
PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixPatcher.csproj"
PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_release_staging"
REF_STUBS_BIN = GAME_ROOT / "tools" / "hotfix_patcher" / "ref_stubs" / "bin"
REF_STUBS_BUILD = GAME_ROOT / "tools" / "hotfix_patcher" / "build_ref_stubs.py"
AUTO_SEAL_SRC = GAME_ROOT / "tools" / "seqchapter_auto_seal"
AUTO_CATCH_SRC = GAME_ROOT / "tools" / "seqchapter_auto_catch"

_ARGV = sys.argv[1:]
# --burn-seal / --auto-catch 均导向合一包（界面二选一）
SEAL_CATCH = any(
    a in (
        "--seal-catch",
        "--burn-or-catch",
        "--burn-seal",
        "--burn-seal-cards",
        "--auto-catch",
        "--catch-pet",
        "--pet-catch",
        "/seal-catch",
        "/burn-seal",
        "/auto-catch",
    )
    for a in _ARGV
)
NO_NINE = SEAL_CATCH or any(
    a in ("--no-nine", "--without-nine", "/no-nine") for a in _ARGV
)

if SEAL_CATCH:
    APP_NAME = "傻瓜补丁_烧卡抓宠"
elif NO_NINE:
    APP_NAME = "傻瓜补丁_无九动"
else:
    APP_NAME = "傻瓜补丁"

# 同系列旧包前缀（含更名迁移）
SERIES_CLEANUP_PREFIXES = [APP_NAME]
if SEAL_CATCH:
    SERIES_CLEANUP_PREFIXES.extend(
        [
            "傻瓜补丁_烧封印",
            "傻瓜补丁_自动抓宠",
            "傻瓜补丁_捉宠版",
            "傻瓜补丁_自动烧卡",
        ]
    )

ENTRY = SCRIPTS_DIR / "foolproof_gui.py"
BAT_NAME = "一键打补丁.bat"

# 合一包：打开界面选烧卡/抓宠（不带 --auto）
if SEAL_CATCH:
    _AUTO_ARGS = ""
elif NO_NINE:
    _AUTO_ARGS = "--auto --no-nine"
else:
    _AUTO_ARGS = "--auto"

_BAT_RUN = (
    f'"%~dp0{APP_NAME}.exe"'
    if not _AUTO_ARGS
    else f'"%~dp0{APP_NAME}.exe" {_AUTO_ARGS}'
)

BAT_CONTENT = rf"""@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0{APP_NAME}.exe" (
  echo [错误] 找不到 {APP_NAME}.exe
  echo 请勿只拷贝本 bat，需解压整个「{APP_NAME}」文件夹。
  pause
  exit /b 1
)
echo 正在打开傻瓜补丁…
{_BAT_RUN}
exit /b %ERRORLEVEL%
"""

README_SEAL_CATCH = """魔力宝贝：序章 — 傻瓜补丁·烧卡/抓宠（二选一）

内容：烧卡用最高加速（10x/特效5x）；抓宠仍为 5x/特效2x；共同含一级含哥布林/蝙蝠·自动技能·跑速快·长按详情·无九动
不含：神奇九动、加速过场、助手桥接

打开补丁后请选择其一（只能打一个）：

【自动烧卡】
· 默认关闭。点侧栏「百科」：Tip「自动烧卡已开启」/「自动烧卡已关闭」；标题「★自动烧卡中★」
· 战斗 10x · 特效 5x。开启后非 VIP 自动战斗有封印卡则自动扔卡；无卡走常规自动

【自动抓宠】
· 默认关闭。点侧栏「百科」 Tip 开关；标题「★自动中★遇到1级N只」
· 战斗 5x · 特效 2x。场上有可抓一级（不含迷你蝙蝠）时 P1 扔卡、P2 一号技能、其余人物 G、宠物固定防御 W|0
· 退战（队长）：满宠可存仓/终检；无卡停挂机；开关不因退战自动关闭

请把封印卡放在背包。组队时各开各的客户端。队长存仓需月卡远程仓权限。

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」，在界面选择「自动烧卡」或「自动封印」后打补丁
4. 看弹窗：成功或失败都会提示

客户端更新后：先用启动器「更新」到最新，再运行本包。
若提示客户端状态异常：请删除本客户端，复制一份干净客户端，再重新打补丁。

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
"""

README_BURN = README_SEAL_CATCH  # 兼容旧引用
README_CATCH = README_SEAL_CATCH

README_NO_NINE = """魔力宝贝：序章 — 傻瓜补丁（无九动）

内容：VIP/非VIP 5x · 自动技能 · 跑速快 · 长按详情 · 特效2x · 遇敌一级含哥布林/蝙蝠
不含：神奇九动、加速过场、助手桥接

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」
4. 看弹窗：成功或失败都会提示

客户端更新后：先用启动器「更新」到最新，再运行本包（或换新版傻瓜补丁）。
若提示客户端状态异常：请删除本客户端，复制一份干净客户端，再重新打补丁。

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
"""

README_DEFAULT = """魔力宝贝：序章 — 傻瓜补丁

内容：VIP/非VIP 5x · 自动技能 · 跑速快 · 长按详情 · 特效2x · 神奇九动 · 遇敌一级含哥布林/蝙蝠
不含：加速过场、助手桥接

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」
4. 看弹窗：成功或失败都会提示

客户端更新后：先用启动器「更新」到最新，再运行本包（或换新版傻瓜补丁）。
若提示客户端状态异常：请删除本客户端，复制一份干净客户端，再重新打补丁。

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
"""

README = (
    README_SEAL_CATCH
    if SEAL_CATCH
    else (README_NO_NINE if NO_NINE else README_DEFAULT)
)

_STAMP_RE = re.compile(r"^\d{8}_\d{6}$")


def _is_series_zip(stem: str, prefix: str) -> bool:
    """匹配「前缀.zip」或「前缀_年月日_时分秒.zip」，避免 傻瓜补丁 误删 傻瓜补丁_烧封印。"""
    if stem == prefix:
        return True
    head = prefix + "_"
    if not stem.startswith(head):
        return False
    return _STAMP_RE.fullmatch(stem[len(head) :]) is not None


def cleanup_series_old_releases(
    release_dir: Path,
    prefixes: list[str],
    *,
    keep: Path | None = None,
) -> list[Path]:
    """删除发布目录下同系列旧 zip（及 dist 下旧解压目录）。返回已删路径。"""
    removed: list[Path] = []
    if not release_dir.is_dir():
        return removed

    keep_resolved = keep.resolve() if keep is not None else None
    for zip_path in sorted(release_dir.glob("*.zip")):
        if keep_resolved is not None and zip_path.resolve() == keep_resolved:
            continue
        stem = zip_path.stem
        if any(_is_series_zip(stem, p) for p in prefixes):
            try:
                zip_path.unlink()
                removed.append(zip_path)
                print(f"[CLEAN] 删除旧包 {zip_path.name}")
            except OSError as exc:
                print(f"[WARN] 无法删除 {zip_path.name}: {exc}")

    # dist_foolproof / 解压目录
    for prefix in prefixes:
        for dist_root in (release_dir / "dist_foolproof", release_dir / "dist"):
            folder = dist_root / prefix
            if folder.is_dir():
                try:
                    shutil.rmtree(folder, ignore_errors=False)
                    removed.append(folder)
                    print(f"[CLEAN] 删除旧目录 {folder}")
                except OSError as exc:
                    print(f"[WARN] 无法删除目录 {folder}: {exc}")

    return removed


def _run(cmd: list[str], *, cwd: Path | None = None) -> None:
    print("[CMD]", " ".join(cmd))
    proc = subprocess.run(cmd, cwd=cwd, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"命令失败 ({proc.returncode}): {' '.join(cmd)}")


def ensure_ref_stubs() -> Path:
    """VERIFY 需要 UnityEngine.CoreModule 等 stubs；缺则编译。"""
    core = REF_STUBS_BIN / "UnityEngine.CoreModule.dll"
    if not core.is_file():
        if not REF_STUBS_BUILD.is_file():
            raise FileNotFoundError(f"找不到 ref_stubs 构建脚本: {REF_STUBS_BUILD}")
        print("[BUILD] ref_stubs…")
        _run([sys.executable, str(REF_STUBS_BUILD)])
    if not core.is_file():
        raise FileNotFoundError(f"ref_stubs 缺失: {core}")
    return REF_STUBS_BIN


def _copy_ref_stubs(dst: Path) -> None:
    src = ensure_ref_stubs()
    if dst.is_dir():
        shutil.rmtree(dst, ignore_errors=True)
    dst.mkdir(parents=True, exist_ok=True)
    for item in src.iterdir():
        if item.is_file() and item.suffix.lower() == ".dll":
            shutil.copy2(item, dst / item.name)
    print(f"[OK] ref_stubs -> {dst} ({len(list(dst.glob('*.dll')))} dll)")


def publish_patcher() -> Path:
    PATCHER_STAGING.mkdir(parents=True, exist_ok=True)
    if not PATCHER_CSPROJ.is_file():
        raise FileNotFoundError(f"找不到引擎工程: {PATCHER_CSPROJ}")
    _run(
        [
            "dotnet",
            "publish",
            str(PATCHER_CSPROJ),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:InvariantGlobalization=true",
            "-o",
            str(PATCHER_STAGING),
        ]
    )
    exe = PATCHER_STAGING / "HotfixPatcher.exe"
    if not exe.is_file():
        raise RuntimeError("HotfixPatcher.exe 编译失败")
    # 与 HotfixPatcher.exe 同级，供 ResolveRefStubDirs(BaseDirectory/ref_stubs)
    _copy_ref_stubs(PATCHER_STAGING / "ref_stubs")
    target = TOOLKIT_ROOT / "patcher" / "HotfixPatcher.exe"
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(exe, target)
        _copy_ref_stubs(TOOLKIT_ROOT / "patcher" / "ref_stubs")
    except OSError:
        print(f"[WARN] 无法覆盖 {target}，发布使用 staging")
    return exe


def build_exe() -> Path:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        _run([sys.executable, "-m", "pip", "install", "pyinstaller"])

    if DIST_DIR.is_dir():
        shutil.rmtree(DIST_DIR, ignore_errors=True)
    if STAGING_DIR.is_dir():
        shutil.rmtree(STAGING_DIR, ignore_errors=True)
    DIST_DIR.mkdir(parents=True, exist_ok=True)
    STAGING_DIR.mkdir(parents=True, exist_ok=True)

    _run(
        [
            sys.executable,
            "-m",
            "PyInstaller",
            "--noconfirm",
            "--clean",
            "--onedir",
            "--windowed",
            "--name",
            APP_NAME,
            "--paths",
            str(SCRIPTS_DIR),
            "--distpath",
            str(DIST_DIR),
            "--workpath",
            str(STAGING_DIR / "work"),
            "--specpath",
            str(STAGING_DIR / "spec"),
            str(ENTRY),
        ]
    )
    out_dir = DIST_DIR / APP_NAME
    exe = out_dir / f"{APP_NAME}.exe"
    if not exe.is_file():
        raise RuntimeError(f"未生成 {exe}")

    patcher_dst = out_dir / "patcher"
    patcher_dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PATCHER_STAGING / "HotfixPatcher.exe", patcher_dst / "HotfixPatcher.exe")
    # 傻瓜包独立运行时 VERIFY 必须能解析 UnityEngine.CoreModule
    _copy_ref_stubs(patcher_dst / "ref_stubs")

    # 烧卡/抓宠合一：随包带上两套 DLL 源码
    if SEAL_CATCH:
        if not (AUTO_SEAL_SRC / "SeqChapterAutoSeal.cs").is_file():
            raise FileNotFoundError(f"找不到自动烧卡源码: {AUTO_SEAL_SRC}")
        if not (AUTO_CATCH_SRC / "SeqChapterAutoCatch.cs").is_file():
            raise FileNotFoundError(f"找不到自动抓宠源码: {AUTO_CATCH_SRC}")
        for src, name in (
            (AUTO_SEAL_SRC, "seqchapter_auto_seal"),
            (AUTO_CATCH_SRC, "seqchapter_auto_catch"),
        ):
            seal_dst = patcher_dst / name
            if seal_dst.is_dir():
                shutil.rmtree(seal_dst, ignore_errors=True)
            shutil.copytree(src, seal_dst)
            tools_dst = out_dir / "tools" / name
            tools_dst.parent.mkdir(parents=True, exist_ok=True)
            if tools_dst.is_dir():
                shutil.rmtree(tools_dst, ignore_errors=True)
            shutil.copytree(src, tools_dst)

    (out_dir / BAT_NAME).write_text(BAT_CONTENT, encoding="utf-8")
    (out_dir / "使用说明.txt").write_text(README, encoding="utf-8")
    if SEAL_CATCH:
        (out_dir / "烧卡抓宠.flag").write_text("1\n", encoding="utf-8")
        (out_dir / "无九动.flag").write_text("1\n", encoding="utf-8")
    elif NO_NINE:
        (out_dir / "无九动.flag").write_text("1\n", encoding="utf-8")
    print(f"[OK] {out_dir}")
    return out_dir


def zip_folder(folder: Path, zip_path: Path) -> None:
    if zip_path.is_file():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for item in folder.rglob("*"):
            if item.is_file():
                zf.write(item, item.relative_to(folder.parent).as_posix())
    print(f"[OK] ZIP {zip_path} ({zip_path.stat().st_size:,} 字节)")


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    label = "烧卡抓宠" if SEAL_CATCH else ("无九动" if NO_NINE else "默认")
    print(f"=== 傻瓜补丁构建 {stamp}（{label}）===\n")
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)

    print("[CLEAN] 清理同系列旧发布物…")
    cleanup_series_old_releases(RELEASE_DIR, SERIES_CLEANUP_PREFIXES)

    publish_patcher()
    out_dir = build_exe()
    zip_path = RELEASE_DIR / f"{APP_NAME}_{stamp}.zip"
    zip_folder(out_dir, zip_path)
    print("\n=== 完成 ===")
    print(f"  {zip_path}")
    print(f"  目录: {out_dir}")
    print(f"  入口: {out_dir / BAT_NAME}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
