#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「傻瓜换装补丁」→ E:\\cross\\发布plugin\\傻瓜换装补丁_*.zip。

仅百科点切换 4 套装备；不含动画预览、不含融合/九动面板信息。
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
CROSS_ROOT = GAME_ROOT.parent
RELEASE_DIR = CROSS_ROOT / "发布plugin"
STAGING_DIR = RELEASE_DIR / "_foolproof_skin_build"
DIST_DIR = RELEASE_DIR / "dist_foolproof_skin"

MAIN_PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixPatcher.csproj"
SKIN_PATCHER_CSPROJ = (
    GAME_ROOT / "tools" / "hotfix_patcher_skin_cycle" / "HotfixPatcherSkinCycle.csproj"
)
SKIN_PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_skin_cycle_staging"
MAIN_PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_release_staging"
REF_STUBS_BIN = GAME_ROOT / "tools" / "hotfix_patcher" / "ref_stubs" / "bin"
REF_STUBS_BUILD = GAME_ROOT / "tools" / "hotfix_patcher" / "build_ref_stubs.py"

BATTLE_APPEAR_SRC = GAME_ROOT / "tools" / "seqchapter_battle_appear"
WIKI_SKIN_SRC = GAME_ROOT / "tools" / "seqchapter_wiki_skin_cycle"
BATTLE_APPEAR_JSON = GAME_ROOT / "tools" / "battle_appear.json"

APP_NAME = "傻瓜换装补丁"
SERIES_CLEANUP_PREFIXES = [APP_NAME, "傻瓜皮肤补丁", "傻瓜补丁_皮肤版"]
ENTRY = SCRIPTS_DIR / "foolproof_skin_gui.py"
BAT_NAME = "一键打补丁.bat"

BAT_CONTENT = rf"""@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0{APP_NAME}.exe" (
  echo [错误] 找不到 {APP_NAME}.exe
  echo 请解压整个「{APP_NAME}」文件夹后再运行。
  pause
  exit /b 1
)
echo 正在打开傻瓜换装补丁…
"%~dp0{APP_NAME}.exe"
exit /b %ERRORLEVEL%
"""

README = """傻瓜换装补丁

用法：
1. 把整个 zip 解压到游戏目录（必须保留完整文件夹，含 _internal、patcher）
2. 关闭游戏后，运行「一键打补丁.bat」（不要只拷贝单个 .exe）
3. 进游戏后点侧栏「百科」，循环切换 4 套装备（1→2→3→4→1…）

注意：
· 若提示找不到 python39.dll，说明只拷了 exe、缺了 _internal，请重新解压整个包
· 只做百科换装，无其它功能；若提示客户端不干净，可用界面「从干净目录恢复」后再打
"""


def _run(cmd: list[str], *, cwd: Path | None = None) -> None:
    print("[CMD]", " ".join(cmd))
    subprocess.run(cmd, check=True, cwd=str(cwd) if cwd else None)


def cleanup_series_old_releases(release_dir: Path, prefixes: list[str]) -> None:
    for path in list(release_dir.glob("*.zip")):
        if any(path.name.startswith(p + "_") or path.stem == p for p in prefixes):
            print(f"[CLEAN] 删除旧包 {path.name}")
            path.unlink(missing_ok=True)
    for name in prefixes:
        dist = release_dir / "dist_foolproof_skin" / name
        if dist.is_dir():
            print(f"[CLEAN] 删除旧目录 {dist}")
            shutil.rmtree(dist, ignore_errors=True)


def _copy_ref_stubs(dst: Path) -> None:
    dst.mkdir(parents=True, exist_ok=True)
    src = REF_STUBS_BIN
    if not src.is_dir() or not any(src.glob("*.dll")):
        if REF_STUBS_BUILD.is_file():
            _run([sys.executable, str(REF_STUBS_BUILD)])
    count = 0
    if src.is_dir():
        for f in src.glob("*.dll"):
            shutil.copy2(f, dst / f.name)
            count += 1
    print(f"[OK] ref_stubs -> {dst} ({count} dll)")


def publish_patchers() -> None:
    SKIN_PATCHER_STAGING.mkdir(parents=True, exist_ok=True)
    MAIN_PATCHER_STAGING.mkdir(parents=True, exist_ok=True)

    if MAIN_PATCHER_CSPROJ.is_file():
        _run(
            [
                "dotnet",
                "publish",
                str(MAIN_PATCHER_CSPROJ),
                "-c",
                "Release",
                "-r",
                "win-x64",
                "--self-contained",
                "true",
                "-p:PublishSingleFile=true",
                "-p:InvariantGlobalization=true",
                "-o",
                str(MAIN_PATCHER_STAGING),
            ]
        )

    if not SKIN_PATCHER_CSPROJ.is_file():
        raise FileNotFoundError(f"找不到皮肤引擎工程: {SKIN_PATCHER_CSPROJ}")
    _run(
        [
            "dotnet",
            "publish",
            str(SKIN_PATCHER_CSPROJ),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:InvariantGlobalization=true",
            "-o",
            str(SKIN_PATCHER_STAGING),
        ]
    )
    exe = SKIN_PATCHER_STAGING / "HotfixPatcherSkinCycle.exe"
    if not exe.is_file():
        raise RuntimeError("HotfixPatcherSkinCycle.exe 编译失败")
    _copy_ref_stubs(SKIN_PATCHER_STAGING / "ref_stubs")
    target = TOOLKIT_ROOT / "patcher" / "HotfixPatcherSkinCycle.exe"
    try:
        shutil.copy2(exe, target)
        print(f"[OK] {target}")
    except OSError:
        print(f"[WARN] 无法覆盖 {target}，发布使用 staging")


def build_exe() -> Path:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        _run([sys.executable, "-m", "pip", "install", "pyinstaller"])

    app_out = DIST_DIR / APP_NAME
    if app_out.is_dir():
        shutil.rmtree(app_out, ignore_errors=True)
    if STAGING_DIR.is_dir():
        shutil.rmtree(STAGING_DIR, ignore_errors=True)
    DIST_DIR.mkdir(parents=True, exist_ok=True)
    STAGING_DIR.mkdir(parents=True, exist_ok=True)

    pyi_cmd = [
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
        "--exclude-module",
        "torch",
        "--exclude-module",
        "UnityPy",
        "--hidden-import",
        "foolproof_skin_apply",
        "--hidden-import",
        "foolproof_client_utils",
        "--hidden-import",
        "patch_common",
        str(ENTRY),
    ]
    _run(pyi_cmd)
    out_dir = DIST_DIR / APP_NAME
    exe = out_dir / f"{APP_NAME}.exe"
    if not exe.is_file():
        raise RuntimeError(f"未生成 {exe}")

    patcher_dst = out_dir / "patcher"
    patcher_dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(
        SKIN_PATCHER_STAGING / "HotfixPatcherSkinCycle.exe",
        patcher_dst / "HotfixPatcherSkinCycle.exe",
    )
    main_exe = MAIN_PATCHER_STAGING / "HotfixPatcher.exe"
    if main_exe.is_file():
        shutil.copy2(main_exe, patcher_dst / "HotfixPatcher.exe")
    _copy_ref_stubs(patcher_dst / "ref_stubs")

    for src, name in (
        (BATTLE_APPEAR_SRC, "seqchapter_battle_appear"),
        (WIKI_SKIN_SRC, "seqchapter_wiki_skin_cycle"),
    ):
        if not src.is_dir():
            raise FileNotFoundError(f"找不到源码目录: {src}")
        for base in (patcher_dst, out_dir / "tools"):
            dst = base / name
            if dst.is_dir():
                shutil.rmtree(dst, ignore_errors=True)
            shutil.copytree(src, dst)

    tools_root = out_dir / "tools"
    tools_root.mkdir(parents=True, exist_ok=True)
    if BATTLE_APPEAR_JSON.is_file():
        shutil.copy2(BATTLE_APPEAR_JSON, tools_root / "battle_appear.json")
        print(f"[OK] battle_appear.json -> {tools_root}")

    (out_dir / BAT_NAME).write_text(BAT_CONTENT, encoding="utf-8")
    (out_dir / "使用说明.txt").write_text(README, encoding="utf-8")
    (out_dir / "傻瓜换装.flag").write_text("1\n", encoding="utf-8")
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


def verify_pack(folder: Path, zip_path: Path) -> None:
    """打包后硬校验：缺 PyInstaller 运行时/皮肤引擎/主引擎/源码则失败，避免发出残包。"""
    file_required = [
        folder / f"{APP_NAME}.exe",
        folder / "_internal" / "python39.dll",
        folder / "_internal" / "base_library.zip",
        folder / "patcher" / "HotfixPatcherSkinCycle.exe",
        folder / "patcher" / "HotfixPatcher.exe",  # 主引擎 fallback（battle-appear 钩子）
        folder / BAT_NAME,
    ]
    dir_required = [
        folder / "patcher" / "ref_stubs",
        folder / "patcher" / "seqchapter_battle_appear",
        folder / "patcher" / "seqchapter_wiki_skin_cycle",
    ]
    missing = [str(p.relative_to(folder)) for p in file_required if not p.is_file()]
    missing += [str(p.relative_to(folder)) for p in dir_required if not p.is_dir()]
    if missing:
        raise RuntimeError("发布目录缺关键文件:\n  - " + "\n  - ".join(missing))

    with zipfile.ZipFile(zip_path, "r") as zf:
        names = set(zf.namelist())
    zip_required = [
        f"{APP_NAME}/{APP_NAME}.exe",
        f"{APP_NAME}/_internal/python39.dll",
        f"{APP_NAME}/_internal/base_library.zip",
        f"{APP_NAME}/patcher/HotfixPatcherSkinCycle.exe",
        f"{APP_NAME}/patcher/HotfixPatcher.exe",
        f"{APP_NAME}/{BAT_NAME}",
    ]
    zip_dir_prefixes = [
        f"{APP_NAME}/patcher/ref_stubs/",
        f"{APP_NAME}/patcher/seqchapter_battle_appear/",
        f"{APP_NAME}/patcher/seqchapter_wiki_skin_cycle/",
    ]
    zip_missing = [n for n in zip_required if n not in names]
    zip_missing += [p for p in zip_dir_prefixes if not any(n.startswith(p) for n in names)]
    if zip_missing:
        raise RuntimeError("ZIP 缺关键文件:\n  - " + "\n  - ".join(zip_missing))

    py_size = (folder / "_internal" / "python39.dll").stat().st_size
    print(
        f"[VERIFY] 目录与 ZIP 均含 python39.dll（{py_size:,} 字节）、"
        "皮肤/主引擎、ref_stubs 与皮肤源码，可独立打补丁"
    )


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    print(f"=== 傻瓜换装补丁构建 {stamp} ===\n")
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)
    print("[CLEAN] 清理同系列旧发布物…")
    cleanup_series_old_releases(RELEASE_DIR, SERIES_CLEANUP_PREFIXES)
    publish_patchers()
    out_dir = build_exe()
    zip_path = RELEASE_DIR / f"{APP_NAME}_{stamp}.zip"
    zip_folder(out_dir, zip_path)
    verify_pack(out_dir, zip_path)
    print("\n=== 完成 ===")
    print(f"  {zip_path}")
    print(f"  目录: {out_dir}")
    print(f"  入口: {out_dir / BAT_NAME}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
