#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「傻瓜补丁」独立包（含 HotfixPatcher + 一键打补丁.bat）→ 发布/傻瓜补丁.zip。

用法：
  python publish_foolproof.py           # 默认含九动
  python publish_foolproof.py --no-nine # 无九动变体
"""
from __future__ import annotations

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

NO_NINE = any(a in ("--no-nine", "--without-nine", "/no-nine") for a in sys.argv[1:])
APP_NAME = "傻瓜补丁_无九动" if NO_NINE else "傻瓜补丁"
ENTRY = SCRIPTS_DIR / "foolproof_gui.py"

BAT_NAME = "一键打补丁.bat"
_AUTO_ARGS = "--auto --no-nine" if NO_NINE else "--auto"
BAT_CONTENT = rf"""@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0{APP_NAME}.exe" (
  echo [错误] 找不到 {APP_NAME}.exe
  echo 请勿只拷贝本 bat，需解压整个「{APP_NAME}」文件夹。
  pause
  exit /b 1
)
echo 正在打补丁（会自动向上查找游戏目录）…
"%~dp0{APP_NAME}.exe" {_AUTO_ARGS}
exit /b %ERRORLEVEL%
"""

README = (
    """魔力宝贝：序章 — 傻瓜补丁（无九动）

内容：VIP/非VIP 5x · 自动技能 · 跑速快 · 长按详情 · 特效2x
不含：神奇九动、加速过场、助手桥接

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」
4. 看弹窗：成功或失败都会提示

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
"""
    if NO_NINE
    else """魔力宝贝：序章 — 傻瓜补丁

内容：VIP/非VIP 5x · 自动技能 · 跑速快 · 长按详情 · 特效2x · 神奇九动
不含：加速过场、助手桥接

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」
4. 看弹窗：成功或失败都会提示

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
"""
)


def _run(cmd: list[str], *, cwd: Path | None = None) -> None:
    print("[CMD]", " ".join(cmd))
    proc = subprocess.run(cmd, cwd=cwd, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"命令失败 ({proc.returncode}): {' '.join(cmd)}")


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
    target = TOOLKIT_ROOT / "patcher" / "HotfixPatcher.exe"
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(exe, target)
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

    (out_dir / BAT_NAME).write_text(BAT_CONTENT, encoding="utf-8")
    (out_dir / "使用说明.txt").write_text(README, encoding="utf-8")
    if NO_NINE:
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
    label = "无九动" if NO_NINE else "默认"
    print(f"=== 傻瓜补丁构建 {stamp}（{label}）===\n")
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)
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
