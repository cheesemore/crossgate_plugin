#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建并打包「完整补丁」「简单补丁」两个 zip 发布物。"""
from __future__ import annotations

import shutil
import subprocess
import sys
import zipfile
from datetime import datetime
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
TOOLKIT_ROOT = SCRIPTS_DIR.parent
RELEASE_DIR = TOOLKIT_ROOT / "发布"
STAGING_DIR = RELEASE_DIR / "_build"
DIST_DIR = RELEASE_DIR / "dist"
GAME_ROOT = TOOLKIT_ROOT.parent
PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixPatcher.csproj"
PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_release_staging"

FULL_NAME = "完整补丁"
SIMPLE_NAME = "简单补丁"


def _run(cmd: list[str], *, cwd: Path | None = None) -> None:
    print("[CMD]", " ".join(cmd))
    proc = subprocess.run(cmd, cwd=cwd, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"命令失败 ({proc.returncode}): {' '.join(cmd)}")


def _ensure_pyinstaller() -> None:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        _run([sys.executable, "-m", "pip", "install", "pyinstaller"])


def publish_patcher_engine() -> Path:
    PATCHER_STAGING.mkdir(parents=True, exist_ok=True)
    if not PATCHER_CSPROJ.is_file():
        raise FileNotFoundError(f"找不到补丁引擎工程: {PATCHER_CSPROJ}")
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
        print(f"[WARN] 无法覆盖 {target}（可能被占用），发布将使用 _release_staging")
    print(f"[OK] 补丁引擎: {exe}")
    return exe


def _pyinstaller_base_args(name: str, entry: Path) -> list[str]:
    return [
        sys.executable,
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onedir",
        "--console" if name == SIMPLE_NAME else "--windowed",
        "--name",
        name,
        "--paths",
        str(SCRIPTS_DIR),
        "--distpath",
        str(DIST_DIR),
        "--workpath",
        str(STAGING_DIR / "work"),
        "--specpath",
        str(STAGING_DIR / "spec"),
        str(entry),
    ]


def build_exe(name: str, entry: Path) -> Path:
    _run(_pyinstaller_base_args(name, entry))
    out_dir = DIST_DIR / name
    exe = out_dir / f"{name}.exe"
    if not exe.is_file():
        raise RuntimeError(f"未生成: {exe}")
    patcher_dst = out_dir / "patcher"
    patcher_dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PATCHER_STAGING / "HotfixPatcher.exe", patcher_dst / "HotfixPatcher.exe")
    readme = out_dir / "使用说明.txt"
    if name == FULL_NAME:
        readme.write_text(
            "完整补丁（带界面）\n\n"
            "1. 将整个文件夹放在游戏根目录（与 cg37_Data 同级）\n"
            "2. 关闭游戏后运行 完整补丁.exe\n"
            "3. 选择游戏目录 → 点「初始化」→ 勾选补丁 →「应用补丁」\n",
            encoding="utf-8",
        )
    else:
        readme.write_text(
            "简单补丁（一键）\n\n"
            "1. 将整个文件夹放在游戏根目录（与 cg37_Data 同级）\n"
            "2. 关闭游戏后双击 简单补丁.exe\n"
            "3. 自动完成：初始化 → 还原 → 默认补丁\n",
            encoding="utf-8",
        )
    print(f"[OK] 已构建: {out_dir}")
    return out_dir


def zip_folder(folder: Path, zip_path: Path) -> None:
    if zip_path.is_file():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for item in folder.rglob("*"):
            if item.is_file():
                zf.write(item, item.relative_to(folder.parent).as_posix())
    print(f"[OK] ZIP: {zip_path} ({zip_path.stat().st_size:,} 字节)")


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)
    if STAGING_DIR.is_dir():
        shutil.rmtree(STAGING_DIR, ignore_errors=True)
    if DIST_DIR.is_dir():
        shutil.rmtree(DIST_DIR, ignore_errors=True)
    STAGING_DIR.mkdir(parents=True, exist_ok=True)
    DIST_DIR.mkdir(parents=True, exist_ok=True)

    print(f"=== 发布构建 {stamp} ===\n")
    _ensure_pyinstaller()
    publish_patcher_engine()

    full_dir = build_exe(FULL_NAME, SCRIPTS_DIR / "seqchapter_combo_gui.py")
    simple_dir = build_exe(SIMPLE_NAME, SCRIPTS_DIR / "simple_patch_cli.py")

    full_zip = RELEASE_DIR / f"{FULL_NAME}.zip"
    simple_zip = RELEASE_DIR / f"{SIMPLE_NAME}.zip"
    zip_folder(full_dir, full_zip)
    zip_folder(simple_dir, simple_zip)

    print("\n=== 发布完成 ===")
    print(f"  {full_zip}")
    print(f"  {simple_zip}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
