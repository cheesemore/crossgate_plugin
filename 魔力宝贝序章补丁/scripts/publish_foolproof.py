#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「傻瓜补丁」独立包（九动版 / 融合版）→ E:\\cross序章\\发布plugin\\*.zip。

用法：
  python publish_foolproof.py --nine-pack      # 九动版（六选一：九动加速/无九动加速/抓宠/抓宠无宠人防/烧卡/慢速烧卡）
  python publish_foolproof.py --fusion-pack    # 融合版（五选一：普通加速/抓宠/抓宠无宠人防/烧卡/慢速烧卡）
  python publish_foolproof.py                  # 同 --fusion-pack
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
NINE_ACTION_SRC = GAME_ROOT / "tools" / "seqchapter_nine_action"
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

_ARGV = sys.argv[1:]
NINE_PACK = any(a in ("--nine-pack", "--with-nine-pack", "/nine-pack") for a in _ARGV)
# 默认融合版；显式 --nine-pack 才出九动版
FUSION_PACK = (not NINE_PACK) or any(
    a in ("--fusion-pack", "--fusion", "/fusion-pack") for a in _ARGV
)
if NINE_PACK:
    FUSION_PACK = False

if NINE_PACK:
    APP_NAME = "傻瓜补丁_九动版"
    ACCEL_NAME = "九动加速"
else:
    APP_NAME = "傻瓜补丁_融合版"
    ACCEL_NAME = "普通加速"

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
    ]
)
if NINE_PACK:
    SERIES_CLEANUP_PREFIXES.append("傻瓜补丁")  # 旧默认九动单包

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

_CHOICE_N = "六选一" if NINE_PACK else "五选一"
_NO_NINE_ACCEL_BLOCK = (
    """
【无九动加速】
· VIP/非VIP 5x · 特效 2x · 跑速快 · 长按详情 · 遇敌一级含哥布林/蝙蝠
· 不含神奇九动（加速组合与「九动加速」相同，只是不打九动）
"""
    if NINE_PACK
    else ""
)

README = f"""魔力宝贝：序章 — {APP_NAME}

打开补丁后请{_CHOICE_N}（只能打一种）：

【{ACCEL_NAME}】
· VIP/非VIP 5x · 特效 2x · 跑速快 · 长按详情 · 遇敌一级含哥布林/蝙蝠
{"· 含神奇九动·DLL版（本包不打 IL 九动，适配余量紧张客户端）" if NINE_PACK else "· 不含神奇九动"}
{_NO_NINE_ACCEL_BLOCK}
【分享切页（界面可勾选，默认勾上）】
· 侧栏「分享」：Tip 切页（日常 / 新手礼包码），2 秒内再点开始
· 日常：签到 + 月卡每日 + 在线礼包 + 指定道具（间隔0.4s）
· 新手礼包码：最多5角色兑换 VIP666/777/888/999、MLBB666/777、mlbb521、mlbb24（已领过不管）
· 不占用百科（抓宠/烧卡仍用百科 Tip）

【客服→高级自动战斗】
· 各模式默认带上：侧栏「客服」→ 自动技能设置（高级自动战斗）；官方入口太深

【助手面板（百科入口，各模式默认带）】
· 侧栏「百科」→ 助手面板「战斗」：九动版加速为 常规/九动/抓宠/烧卡；融合版为 常规/抓宠/烧卡；形象/脚本另页
· 进战形象：「形象」页粘贴/推荐方案/按账号 Uid 存档

【自动抓宠】
· 默认关闭。点侧栏「百科」Tip 开关；标题「★自动中★遇到1级N只」
· 战斗 5x · 特效 2x。可抓一级时 P1 扔卡、P2 一号技能、其余人物 G、宠物对齐防御键
· 不含九动

【自动抓宠（无宠人防御）】
· 与「自动抓宠」相同的开关/加速/流水线；一般不用
· 区别：无宠时人物 2动改防御；1动仍为 P1 扔卡、P2 一号技能、其余 G
· 不含九动

【自动烧卡】
· 默认关闭。点侧栏「百科」Tip；标题「★自动烧卡中★」
· 战斗 10x · 特效 5x · 跑速快。退战 Tip 余卡，无卡停遇敌
· 不含九动

【慢速烧卡】
· 烧卡 / Tip / 退战停遇敌 与「自动烧卡」相同
· 无任何加速（无战斗倍速、特效、跑速、过场）
· 不含九动

【打加速补丁（界面可勾选，默认勾上）】
· 关闭后：任意模式都不打战斗倍速 / 跑速 / 特效加速 / 过场加速
· 九动 / 抓宠 / 烧卡逻辑 / 日常礼包 / 进战形象 不受影响

【启动动画器】
· 界面按钮「启动动画器」：打开选皮肤预览 GUI，写出 battle_appear.json
· 需先打过带「进战形象」的补丁；配置会写到游戏目录 tools/battle_appear.json

共同不含：助手桥接。抓宠/烧卡请把封印卡放背包。

1. 关掉游戏
2. 把本文件夹解压到游戏目录（和 cg37.exe 放一起，或放在子文件夹里也行）
3. 双击「一键打补丁.bat」，在界面选择模式后打补丁
4. 看弹窗：成功或失败都会提示
5. 需要换皮时点「启动动画器」

客户端更新后：先用启动器「更新」到最新，再运行本包（或换新版傻瓜补丁）。
若提示客户端状态异常：可在界面点「从干净目录恢复…」，手动选择一份干净客户端目录
（无默认路径），恢复 hotfix 后再打补丁。

找不到游戏时会自动往上一级目录找，一直找到盘符为止。
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

    # 两包都带烧卡+抓宠+日常+助手面板+进战形象源；九动版额外带九动 DLL 源
    bundle_srcs: list[tuple[Path, str]] = [
        (AUTO_SEAL_SRC, "seqchapter_auto_seal"),
        (AUTO_CATCH_SRC, "seqchapter_auto_catch"),
        (DAILY_CLAIM_SRC, "seqchapter_daily_claim"),
        (BOSS_KEY_FPS_SRC, "seqchapter_boss_key_fps"),
        (WIKI_FPS_SRC, "seqchapter_wiki_fps"),
        (TEST_UI_SRC, "seqchapter_test_ui"),
        (LV1_AUTO_SRC, "seqchapter_lv1_auto"),
        (BATTLE_APPEAR_SRC, "seqchapter_battle_appear"),
    ]
    if NINE_PACK:
        bundle_srcs.append((NINE_ACTION_SRC, "seqchapter_nine_action"))

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
    if NINE_PACK:
        (out_dir / "九动版.flag").write_text("1\n", encoding="utf-8")
    else:
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


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    label = "九动版" if NINE_PACK else "融合版"
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
