#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一次性发布：独立 exe + 源码版文件夹 → 根目录 ZIP。"""
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
RELEASE_WORK = TOOLKIT_ROOT / "发布" / "_one_time_bundle"
BUNDLE_NAME = "序章补丁"
ZIP_PATH = GAME_ROOT / f"{BUNDLE_NAME}.zip"

PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher"
PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_release_staging"
EXE_NAME = "序章补丁GUI"
ENTRY = SCRIPTS_DIR / "seqchapter_combo_gui.py"

SOURCE_EXCLUDE_DIRS = {
    "__pycache__",
    ".git",
    "bin",
    "obj",
    "_build",
    "_staging",
    "_release_staging",
    "_test_build",
    "dist",
    "work",
    "spec",
}
SOURCE_EXCLUDE_GLOBS = ("*.pyc", "*.pyo", "*.log", "*.zip")


def _run(cmd: list[str], *, cwd: Path | None = None) -> None:
    print("[CMD]", " ".join(cmd))
    proc = subprocess.run(cmd, cwd=cwd, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        raise RuntimeError(f"命令失败 ({proc.returncode}): {' '.join(cmd)}")


def publish_patcher() -> Path:
    PATCHER_STAGING.mkdir(parents=True, exist_ok=True)
    _run(
        [
            "dotnet",
            "publish",
            str(PATCHER_CSPROJ / "HotfixPatcher.csproj"),
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
    shutil.copy2(exe, TOOLKIT_ROOT / "patcher" / "HotfixPatcher.exe")
    return exe


def build_gui_exe(out_root: Path) -> Path:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        _run([sys.executable, "-m", "pip", "install", "pyinstaller"])

    dist = out_root / "_pyi_dist"
    work = out_root / "_pyi_work"
    spec = out_root / "_pyi_spec"
    if dist.is_dir():
        shutil.rmtree(dist)
    if work.is_dir():
        shutil.rmtree(work)
    if spec.is_dir():
        shutil.rmtree(spec)

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
            EXE_NAME,
            "--paths",
            str(SCRIPTS_DIR),
            "--distpath",
            str(dist),
            "--workpath",
            str(work),
            "--specpath",
            str(spec),
            str(ENTRY),
        ]
    )
    app_dir = dist / EXE_NAME
    exe = app_dir / f"{EXE_NAME}.exe"
    if not exe.is_file():
        raise RuntimeError(f"未生成 {exe}")
    patcher_dst = app_dir / "patcher"
    patcher_dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PATCHER_STAGING / "HotfixPatcher.exe", patcher_dst / "HotfixPatcher.exe")
    return app_dir


def _should_skip(path: Path, base: Path) -> bool:
    rel = path.relative_to(base)
    for part in rel.parts:
        if part in SOURCE_EXCLUDE_DIRS:
            return True
        if part.startswith(("_build", "_test", "_staging", "_release")):
            return True
    if path.suffix.lower() == ".exe" and "patcher" not in rel.parts[:1]:
        # hotfix_patcher 目录下除 patcher/ 外不打包 exe 产物
        if rel.parts and rel.parts[0] == "hotfix_patcher":
            return True
    return any(path.match(g) for g in SOURCE_EXCLUDE_GLOBS)


def copy_source_tree(dst: Path) -> None:
    dst.mkdir(parents=True, exist_ok=True)

    def copy_tree(src: Path, target: Path) -> None:
        target.mkdir(parents=True, exist_ok=True)
        for item in src.iterdir():
            if _should_skip(item, src):
                continue
            dest = target / item.name
            if item.is_dir():
                copy_tree(item, dest)
            elif item.suffix.lower() == ".exe" and src.name == "hotfix_patcher":
                continue
            else:
                shutil.copy2(item, dest)

    # Python 工具
    copy_tree(SCRIPTS_DIR, dst / "scripts")
    copy_tree(TOOLKIT_ROOT / "patcher", dst / "patcher")
    for name in (
        "启动补丁GUI.bat",
        "重建补丁引擎.bat",
        "检查渠道号.bat",
        "补丁维护.md",
        "patch_config.json",
    ):
        src = TOOLKIT_ROOT / name
        if src.is_file():
            shutil.copy2(src, dst / name)

    # C# 补丁引擎源码
    cs_src = GAME_ROOT / "tools" / "hotfix_patcher"
    cs_dst = dst / "hotfix_patcher"
    copy_tree(cs_src, cs_dst)


def write_user_readme(bundle_root: Path) -> None:
    (bundle_root / "README.txt").write_text(
        "魔力宝贝：序章 — 序章补丁发布包\n"
        "================================\n\n"
        "【独立运行】\n"
        f"  进入「{EXE_NAME}」文件夹，双击 {EXE_NAME}.exe\n"
        "  将整个文件夹放在游戏根目录（与 cg37_Data 同级）后使用最佳。\n\n"
        "【标准流程】\n"
        "  1. 完全关闭 cg37.exe\n"
        "  2. 运行 GUI → 选择游戏目录\n"
        "  3. 点「同步客户端底稿」（客户端更新后）→ 红色「初始化」\n"
        "  4. 勾选补丁 →「应用补丁」\n"
        "  5. 启动游戏验证；有问题用「一键还原」\n\n"
        "【默认补丁】\n"
        "  VIP 5x + 非VIP 5x / 客服→自动技能 / Sprint 8 / 战斗长按详情 / 技能特效 2x\n\n"
        "【源码版】\n"
        "  见「源码版」文件夹与 说明.md（供开发者 / AI 修改）\n\n"
        f"构建时间: {datetime.now():%Y-%m-%d %H:%M:%S}\n",
        encoding="utf-8",
    )

    gui_dir = bundle_root / EXE_NAME
    (gui_dir / "使用说明.txt").write_text(
        f"{EXE_NAME}\n\n"
        "1. 建议放在游戏根目录（与 cg37_Data 同级）\n"
        "2. 关闭游戏后运行本 exe\n"
        "3. 初始化 → 应用补丁\n",
        encoding="utf-8",
    )


def zip_bundle(bundle_root: Path, zip_path: Path) -> None:
    if zip_path.is_file():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for item in bundle_root.rglob("*"):
            if item.is_file():
                arc = Path(BUNDLE_NAME) / item.relative_to(bundle_root)
                zf.write(item, arc.as_posix())
    print(f"[OK] ZIP: {zip_path} ({zip_path.stat().st_size:,} 字节)")


def main() -> int:
    if RELEASE_WORK.is_dir():
        shutil.rmtree(RELEASE_WORK, ignore_errors=True)
    bundle_root = RELEASE_WORK / BUNDLE_NAME
    bundle_root.mkdir(parents=True)

    print("=== 1/4 编译 HotfixPatcher ===")
    publish_patcher()

    print("=== 2/4 打包 GUI exe ===")
    app_dir = build_gui_exe(RELEASE_WORK)
    shutil.move(str(app_dir), str(bundle_root / EXE_NAME))

    print("=== 3/4 复制源码版 ===")
    copy_source_tree(bundle_root / "源码版")
    write_user_readme(bundle_root)

    print("=== 4/4 生成 ZIP ===")
    zip_bundle(bundle_root, ZIP_PATH)

    print("\n=== 完成 ===")
    print(f"  {ZIP_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
