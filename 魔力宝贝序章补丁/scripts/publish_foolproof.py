#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「傻瓜补丁·融合版」独立包 → E:\\cross\\发布plugin\\*.zip。

九动版已无限期停发，本脚本只产出融合版。
用法：
  python publish_foolproof.py                  # 融合版（百科面板无九动）
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
CROSS_ROOT = GAME_ROOT.parent
RELEASE_DIR = CROSS_ROOT / "发布plugin"
STAGING_DIR = RELEASE_DIR / "_foolproof_build"
DIST_DIR = RELEASE_DIR / "dist_foolproof"
PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixPatcher.csproj"
PATCHER_STAGING = TOOLKIT_ROOT / "patcher" / "_release_staging"
REF_STUBS_BIN = GAME_ROOT / "tools" / "hotfix_patcher" / "ref_stubs" / "bin"
REF_STUBS_BUILD = GAME_ROOT / "tools" / "hotfix_patcher" / "build_ref_stubs.py"
AUTO_SEAL_SRC = GAME_ROOT / "tools" / "seqchapter_auto_seal"
AUTO_CATCH_SRC = GAME_ROOT / "tools" / "seqchapter_auto_catch"
AUTO_CATCH_SELL_SRC = GAME_ROOT / "tools" / "seqchapter_auto_catch_sell"
DAILY_CLAIM_SRC = GAME_ROOT / "tools" / "seqchapter_daily_claim"
BOSS_KEY_FPS_SRC = GAME_ROOT / "tools" / "seqchapter_boss_key_fps"
WIKI_FPS_SRC = GAME_ROOT / "tools" / "seqchapter_wiki_fps"
TEST_UI_SRC = GAME_ROOT / "tools" / "seqchapter_test_ui"
LV1_AUTO_SRC = GAME_ROOT / "tools" / "seqchapter_lv1_auto"
BATTLE_APPEAR_SRC = GAME_ROOT / "tools" / "seqchapter_battle_appear"
BATTLE_APPEAR_JSON = GAME_ROOT / "tools" / "battle_appear.json"
PET_RANK_BIN = GAME_ROOT / "tools" / "pet_rank.bin"
TOOLS_SRC = GAME_ROOT / "tools"

# 动画播放器（选皮肤）随包发布
ANIMATOR_PY = [
    "pet_appear_gui.py",
    "battle_appear_codec.py",
    "game_profile.py",
    "pet_anim_manager.py",
    "pet_preview.py",
    "preview_rgba.py",
    "pet_bundle_animdata.py",
    "skill_effect_tint.py",
    "parse_pet_table_v2.py",
    "pet_head_manager.py",
    "swap_pet_head.py",
]
ANIMATOR_DATA = [
    "pet_appear.json",
    "pet_appear.bin",
    "ride_skin.json",
    "role_halo.json",
    "pet_max_crest.json",
    "battle_appear.json",
]

# 九动版已无限期停发：本脚本只构建融合版，忽略任何 --nine-pack 类参数。
APP_NAME = "傻瓜补丁_融合版"
PANEL_MODES = "常规 / 抓宠 / 抓宠（不带宠）/ 抓宠卖银币 / 烧卡"

SERIES_CLEANUP_PREFIXES = [APP_NAME]
# 清理旧系列命名，避免发布目录堆积
SERIES_CLEANUP_PREFIXES.extend(
    [
        "傻瓜补丁_烧卡抓宠",
        "傻瓜补丁_烧封印",
        "傻瓜补丁_自动抓宠",
        "傻瓜补丁_捉宠版",
        "傻瓜补丁_自动烧卡",
        "傻瓜补丁_无九动",
        "傻瓜补丁_九动版",  # 停发后一并清理历史九动版产物
        "傻瓜补丁",  # 旧默认九动单包
    ]
)

ENTRY = SCRIPTS_DIR / "foolproof_gui.py"
BAT_NAME = "一键打补丁.bat"

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
"%~dp0{APP_NAME}.exe"
exit /b %ERRORLEVEL%
"""

README = f"""魔力宝贝：序章 — {APP_NAME}

【本包做什么】
· 侧栏「百科」→ 助手面板，战斗模式：{PANEL_MODES}
· 战斗模式默认：抓宠 / 抓宠（不带宠）/ 抓宠卖银币 / 烧卡（面板内互斥切换）
· 界面外层选项仅「战斗加速」：开→战斗倍速+心跳回传1.5x；关→原速+心跳回传1.0x
· 默认含：分享改日常、礼包码、客服→高级自动战斗

【用法】
1. 关掉游戏，解压到游戏目录（与 cg37.exe 同级或子文件夹）
2. 双击「一键打补丁.bat」
3. 勾选/取消「战斗加速」后点「一键打补丁」
4. 进游戏用百科面板切换战斗模式
5. 换皮预览：界面「启动动画预览」（依赖上方填写的游戏目录资源）

客户端不干净时：界面「从干净目录恢复…」选手选干净客户端后再打。
"""

_STAMP_RE = re.compile(r"^\d{8}_\d{6}$")


def _is_series_zip(stem: str, prefix: str) -> bool:
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

    # 只清本包输出，避免打九动版时删掉融合版目录（或反之）
    app_out = DIST_DIR / APP_NAME
    if app_out.is_dir():
        shutil.rmtree(app_out, ignore_errors=True)
    if STAGING_DIR.is_dir():
        shutil.rmtree(STAGING_DIR, ignore_errors=True)
    DIST_DIR.mkdir(parents=True, exist_ok=True)
    STAGING_DIR.mkdir(parents=True, exist_ok=True)

    # 动画器依赖 UnityPy/PIL；勿 --collect-all（会拖进 torch/pandas 把包打到数百 MB）
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
        "--paths",
        str(TOOLS_SRC),
        "--distpath",
        str(DIST_DIR),
        "--workpath",
        str(STAGING_DIR / "work"),
        "--specpath",
        str(STAGING_DIR / "spec"),
        "--collect-submodules",
        "UnityPy",
        "--collect-data",
        "UnityPy",
        "--hidden-import",
        "PIL",
        "--hidden-import",
        "PIL.Image",
        "--hidden-import",
        "PIL.ImageTk",
        "--hidden-import",
        "UnityPy",
        # 排除分析时偶发牵连的重型库
        "--exclude-module",
        "torch",
        "--exclude-module",
        "torchvision",
        "--exclude-module",
        "torchaudio",
        "--exclude-module",
        "pandas",
        "--exclude-module",
        "scipy",
        "--exclude-module",
        "matplotlib",
        "--exclude-module",
        "numba",
        "--exclude-module",
        "llvmlite",
        "--exclude-module",
        "sklearn",
        "--exclude-module",
        "cv2",
    ]
    for mod in (
        "pet_appear_gui",
        "battle_appear_codec",
        "game_profile",
        "pet_anim_manager",
        "pet_preview",
        "preview_rgba",
        "pet_bundle_animdata",
        "skill_effect_tint",
        "parse_pet_table_v2",
        "pet_head_manager",
        "swap_pet_head",
    ):
        pyi_cmd.extend(["--hidden-import", mod])
    for name in ANIMATOR_PY + ANIMATOR_DATA:
        src = TOOLS_SRC / name
        if src.is_file():
            # onedir: 解到 MEIPASS/animator/
            pyi_cmd.extend(["--add-data", f"{src};animator"])
    pyi_cmd.append(str(ENTRY))
    _run(pyi_cmd)
    out_dir = DIST_DIR / APP_NAME
    exe = out_dir / f"{APP_NAME}.exe"
    if not exe.is_file():
        raise RuntimeError(f"未生成 {exe}")

    patcher_dst = out_dir / "patcher"
    patcher_dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PATCHER_STAGING / "HotfixPatcher.exe", patcher_dst / "HotfixPatcher.exe")
    _copy_ref_stubs(patcher_dst / "ref_stubs")

    # 融合版带烧卡+抓宠+日常+助手面板+进战形象源（九动已停发，不再打包九动 DLL 源）
    bundle_srcs: list[tuple[Path, str]] = [
        (AUTO_SEAL_SRC, "seqchapter_auto_seal"),
        (AUTO_CATCH_SRC, "seqchapter_auto_catch"),
        (AUTO_CATCH_SELL_SRC, "seqchapter_auto_catch_sell"),
        (DAILY_CLAIM_SRC, "seqchapter_daily_claim"),
        (BOSS_KEY_FPS_SRC, "seqchapter_boss_key_fps"),
        (WIKI_FPS_SRC, "seqchapter_wiki_fps"),
        (TEST_UI_SRC, "seqchapter_test_ui"),
        (LV1_AUTO_SRC, "seqchapter_lv1_auto"),
        (BATTLE_APPEAR_SRC, "seqchapter_battle_appear"),
    ]

    for src, name in bundle_srcs:
        if not src.is_dir():
            raise FileNotFoundError(f"找不到源码目录: {src}")
        seal_dst = patcher_dst / name
        if seal_dst.is_dir():
            shutil.rmtree(seal_dst, ignore_errors=True)
        shutil.copytree(src, seal_dst)
        tools_dst = out_dir / "tools" / name
        tools_dst.parent.mkdir(parents=True, exist_ok=True)
        if tools_dst.is_dir():
            shutil.rmtree(tools_dst, ignore_errors=True)
        shutil.copytree(src, tools_dst)

    # 超级AI 档位表 + 进战形象默认配置（运行时从游戏根/tools 或开发路径加载）
    tools_root = out_dir / "tools"
    tools_root.mkdir(parents=True, exist_ok=True)
    if PET_RANK_BIN.is_file():
        shutil.copy2(PET_RANK_BIN, tools_root / "pet_rank.bin")
        print(f"[OK] pet_rank.bin -> {tools_root}")
    else:
        print(f"[WARN] 缺少 {PET_RANK_BIN}，超级AI估属性可能不可用")
    if BATTLE_APPEAR_JSON.is_file():
        shutil.copy2(BATTLE_APPEAR_JSON, tools_root / "battle_appear.json")
        print(f"[OK] battle_appear.json -> {tools_root}")
    else:
        print(f"[WARN] 缺少 {BATTLE_APPEAR_JSON}")

    # 动画播放器（选皮肤）：旁路 animator/ + tools/ 各一份，便于启动与写配置
    anim_dst = out_dir / "animator"
    if anim_dst.is_dir():
        shutil.rmtree(anim_dst, ignore_errors=True)
    anim_dst.mkdir(parents=True, exist_ok=True)
    copied_anim = 0
    for name in ANIMATOR_PY + ANIMATOR_DATA:
        src = TOOLS_SRC / name
        if not src.is_file():
            print(f"[WARN] 动画器缺少 {src}")
            continue
        shutil.copy2(src, anim_dst / name)
        # 同步一份到 tools/，与开发布局一致
        shutil.copy2(src, tools_root / name)
        copied_anim += 1
    print(f"[OK] animator -> {anim_dst} ({copied_anim} files)")

    (out_dir / BAT_NAME).write_text(BAT_CONTENT, encoding="utf-8")
    (out_dir / "使用说明.txt").write_text(README, encoding="utf-8")
    (out_dir / "融合版.flag").write_text("1\n", encoding="utf-8")
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
    """打包后硬校验：缺运行时/引擎/源码任一关键文件则失败，避免发出残包。"""
    file_required = [
        folder / f"{APP_NAME}.exe",
        folder / "_internal" / "python39.dll",
        folder / "_internal" / "base_library.zip",
        folder / "patcher" / "HotfixPatcher.exe",
        folder / BAT_NAME,
    ]
    # 外置 DLL 源码（引擎编译外部 DLL 必需，随包旁路）
    dir_required = [
        folder / "patcher" / "ref_stubs",
        folder / "patcher" / "seqchapter_auto_seal",
        folder / "patcher" / "seqchapter_auto_catch",
        folder / "patcher" / "seqchapter_auto_catch_sell",
        folder / "patcher" / "seqchapter_daily_claim",
        folder / "patcher" / "seqchapter_battle_appear",
        folder / "patcher" / "seqchapter_test_ui",
    ]
    missing = [str(p.relative_to(folder)) for p in file_required if not p.is_file()]
    missing += [
        str(p.relative_to(folder)) for p in dir_required if not p.is_dir()
    ]
    if missing:
        raise RuntimeError("发布目录缺关键文件:\n  - " + "\n  - ".join(missing))

    with zipfile.ZipFile(zip_path, "r") as zf:
        names = set(zf.namelist())
    zip_required = [
        f"{APP_NAME}/{APP_NAME}.exe",
        f"{APP_NAME}/_internal/python39.dll",
        f"{APP_NAME}/_internal/base_library.zip",
        f"{APP_NAME}/patcher/HotfixPatcher.exe",
        f"{APP_NAME}/{BAT_NAME}",
    ]
    zip_dir_prefixes = [
        f"{APP_NAME}/patcher/ref_stubs/",
        f"{APP_NAME}/patcher/seqchapter_auto_seal/",
        f"{APP_NAME}/patcher/seqchapter_battle_appear/",
    ]
    zip_missing = [n for n in zip_required if n not in names]
    zip_missing += [p for p in zip_dir_prefixes if not any(n.startswith(p) for n in names)]
    if zip_missing:
        raise RuntimeError("ZIP 缺关键文件:\n  - " + "\n  - ".join(zip_missing))

    py_size = (folder / "_internal" / "python39.dll").stat().st_size
    print(
        f"[VERIFY] 目录与 ZIP 均含 python39.dll（{py_size:,} 字节）"
        "、引擎、ref_stubs 与外置 DLL 源码，可独立打补丁"
    )


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    print(f"=== 傻瓜补丁构建 {stamp}（融合版）===\n")
    RELEASE_DIR.mkdir(parents=True, exist_ok=True)

    print("[CLEAN] 清理同系列旧发布物…")
    cleanup_series_old_releases(RELEASE_DIR, SERIES_CLEANUP_PREFIXES)

    publish_patcher()
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
