#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""魔力宝贝：序章 — 热补丁工具：路径配置与 HotfixPatcher 调用。"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

# 序章补丁工具包 — 唯一维护目录（相对游戏仓库根）
CANONICAL_TOOLKIT_NAME = "魔力宝贝序章补丁"

PATCHER_SUBDIR = "patcher"
DATA_DIR = "cg37_Data"
HOTFIX_REL = Path(DATA_DIR) / "assets" / "hotfixdata" / "hotfix.dll.bytes"
BRIDGE_DLL_REL = Path(DATA_DIR) / "assets" / "hotfixdata" / "SeqChapterHelperBridge.dll.bytes"
PARTIALCONFIG_REL = Path(DATA_DIR) / "partialconfig.bin"
PARTIALCONFIG_STREAMING_REL = Path(DATA_DIR) / "StreamingAssets" / "partialconfig.bin"
KEEP_CHANNELS = frozenset({"1100", "1102"})
DEFAULT_CHANNEL = "1101"
OLD_HOTFIX_SIZE = 6_879_744
EXPECTED_SIZE = 7_075_328
KNOWN_OLD_SIZES: dict[int, str] = {
    6_879_744: "2026-6-21 之前",
    6_923_264: "2026-6-21 ~ 2026-6-27",
    6_991_360: "2026-6-27 ~ 2026-7",
    7_042_560: "2026-7 ~ 2026-7-15",
    7_055_360: "2026-7-15 ~ 2026-7-18",
    7_067_648: "2026-7-18 ~ 2026-7-19",
    7_068_160: "2026-7-19 ~ 2026-7-22",
}
UPDATED_HOTFIX_REL = Path("tools") / "hotfix.dll.bytes.neworig"
BASELINE_META_REL = Path("tools") / "hotfix_baseline.json"
HOTFIX_WATCH_STAMP_NAME = "hotfix_watch_stamp.json"
CUSTOMER_GM_MODES = (
    "blindbox", "lottery", "crystal", "autoskill",
    "challengeboss", "bravetrial",
)
CUSTOMER_GM_LABELS = {
    "original": "原版 QQ 客服",
    "blindbox": "盲盒(3028)",
    "lottery": "幸运秘宝(3049)",
    "crystal": "水晶阁",
    "boss": "讨伐 Boss",
    "tower": "无尽之塔(已下架)",
    "ruby": "露比试炼",
    "autoskill": "自动技能设置",
    "challengeboss": "讨伐令(3045)",
    "bravetrial": "英雄试炼(3047)",
    "familyhall": "公会领地(已下架)",
    "unknown": "未知",
}


def _is_frozen() -> bool:
    return getattr(sys, "frozen", False)


def toolkit_root() -> Path:
    if _is_frozen():
        return Path(sys.executable).resolve().parent
    scripts = Path(__file__).resolve().parent
    packaged = scripts.parent
    if (packaged / PATCHER_SUBDIR).is_dir():
        return packaged
    return scripts


SCRIPTS_DIR = Path(__file__).resolve().parent
PACKAGED_ROOT = SCRIPTS_DIR.parent
PACKAGE_NAME = CANONICAL_TOOLKIT_NAME
GAME_ROOT = SCRIPTS_DIR.parent.parent
DEFAULT_TOOLKIT_DIR = GAME_ROOT / PACKAGE_NAME

if _is_frozen():
    TOOLKIT_ROOT = toolkit_root()
    PATCHER_DIR = TOOLKIT_ROOT / PATCHER_SUBDIR
elif (PACKAGED_ROOT / PATCHER_SUBDIR).is_dir():
    TOOLKIT_ROOT = PACKAGED_ROOT
    PATCHER_DIR = PACKAGED_ROOT / PATCHER_SUBDIR
else:
    TOOLKIT_ROOT = SCRIPTS_DIR.parent.parent
    PATCHER_DIR = GAME_ROOT / "tools" / "hotfix_patcher" / "bin" / "Release" / "net8.0"

PATCHER_EXE = PATCHER_DIR / "HotfixPatcher.exe"
PATCHER_DLL = PATCHER_DIR / "HotfixPatcher.dll"
DEV_PATCHER_DLL = GAME_ROOT / "tools" / "hotfix_patcher" / "bin" / "Release" / "net8.0" / "HotfixPatcher.dll"
DEV_PATCHER_EXE = GAME_ROOT / "tools" / "hotfix_patcher" / "bin" / "publish_test" / "HotfixPatcher.exe"
STAGING_PATCHER_EXE = PATCHER_DIR / "_staging" / "HotfixPatcher.exe"
PATCHER_STAGING_DIR = PATCHER_DIR / "_staging"
PATCHER_BUILD_DIR = PATCHER_DIR / "_staging_build"
PATCHER_BUILD_POINTER = PATCHER_DIR / "_staging_latest.txt"
DEV_PATCHER_CSPROJ = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixPatcher.csproj"

CONFIG_PATH = toolkit_root() / "patch_config.json"


def load_config() -> dict:
    if CONFIG_PATH.is_file():
        return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    return {}


def save_config(data: dict) -> None:
    CONFIG_PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def get_game_root() -> Path | None:
    raw = load_config().get("game_root", "").strip()
    if raw:
        path = Path(raw)
        if path.is_dir() and (path / DATA_DIR).is_dir():
            return path
    detected = detect_game_root_from_launcher()
    if detected is not None:
        return detected
    dev = GAME_ROOT
    if (dev / DATA_DIR).is_dir():
        return dev
    return None


def detect_game_root_from_launcher() -> Path | None:
    """从 exe/工具目录起逐级向上查找含 cg37.exe + cg37_Data 的游戏根目录。"""
    starts: list[Path] = []
    if _is_frozen():
        starts.append(Path(sys.executable).resolve().parent)
    starts.append(toolkit_root())
    try:
        starts.append(Path.cwd().resolve())
    except OSError:
        pass
    if not _is_frozen() and (GAME_ROOT / DATA_DIR).is_dir():
        starts.append(GAME_ROOT)

    seen: set[str] = set()
    for base in starts:
        found = find_game_root_walking_up(base)
        if found is None:
            continue
        key = str(found.resolve()).lower()
        if key in seen:
            continue
        seen.add(key)
        return found
    return None


def find_game_root_walking_up(start: Path | None = None) -> Path | None:
    """从 start 起向上查找游戏根（含 cg37.exe 与 cg37_Data），直到盘符根。"""
    if start is None:
        if _is_frozen():
            start = Path(sys.executable).resolve().parent
        else:
            start = Path.cwd()
    cur = start.resolve()
    if cur.is_file():
        cur = cur.parent
    while True:
        exe = cur / "cg37.exe"
        data = cur / DATA_DIR
        if exe.is_file() and data.is_dir():
            return cur
        parent = cur.parent
        if parent == cur:
            return None
        cur = parent


def set_game_root(path: Path) -> None:
    cfg = load_config()
    cfg["game_root"] = str(path.resolve())
    save_config(cfg)


def hotfix_path(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请在 GUI 中选择包含 {DATA_DIR} 的文件夹")
    return root / HOTFIX_REL


def hotfix_orig(game_root: Path | None = None) -> Path:
    path = hotfix_path(game_root)
    return path.with_name(path.name + ".orig")


def bridge_dll_path(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请在 GUI 中选择包含 {DATA_DIR} 的文件夹")
    return root / BRIDGE_DLL_REL


BRIDGE_PATCHED_VARIANTS = frozenset(
    {
        "cecil_light",
        "cecil_light_loadbytes",
        "cecil_light_loadfrom",
        "binary_loadfrom",
    }
)

BRIDGE_VARIANT_LABELS = {
    "binary_loadfrom": "二进制轻量版（LoadFrom 落盘，推荐）",
    "cecil_light_loadbytes": "轻量版（Load 字节 + ModLoader 挂点）",
    "cecil_light_loadfrom": "轻量版（LoadFrom 落盘）",
    "cecil_light": "轻量版（Assembly.Load 字节，桥接可能连不上）",
    "embedded": "嵌入类型版（易黑屏，需重新注入）",
    "not_patched": "未注入",
    "broken": "已损坏 / hook 校验失败",
    "missing": "hotfix 缺失",
    "unknown": "未知",
}


def bridge_variant_label(variant: str) -> str:
    return BRIDGE_VARIANT_LABELS.get(variant, variant)


def is_bridge_patched(game_root: Path | None = None) -> bool:
    return detect_bridge_variant(game_root) in BRIDGE_PATCHED_VARIANTS


def detect_bridge_variant(game_root: Path | None = None) -> str:
    return detect_bridge_variant_on_file(hotfix_path(game_root))


def partialconfig_paths(game_root: Path) -> tuple[Path, Path]:
    return (
        game_root / PARTIALCONFIG_REL,
        game_root / PARTIALCONFIG_STREAMING_REL,
    )


def read_partialconfig_channel(path: Path) -> str | None:
    if not path.is_file():
        return None
    text = path.read_text(encoding="utf-8", errors="replace").strip()
    return text or None


def get_channel_status(game_root: Path | None = None) -> dict:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    main_path, streaming_path = partialconfig_paths(root)
    main_value = read_partialconfig_channel(main_path)
    streaming_value = read_partialconfig_channel(streaming_path)
    values = [v for v in (main_value, streaming_value) if v is not None]
    return {
        "game_root": str(root),
        "main_path": str(main_path),
        "streaming_path": str(streaming_path),
        "main_value": main_value,
        "streaming_value": streaming_value,
        "display_value": values[0] if values else None,
        "consistent": len(set(values)) <= 1,
    }


def normalize_channel_on_init(game_root: Path | None = None) -> None:
    root = game_root or get_game_root()
    if root is None:
        return
    for path in partialconfig_paths(root):
        current = read_partialconfig_channel(path)
        if current in KEEP_CHANNELS:
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(DEFAULT_CHANNEL, encoding="utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _iter_patcher_build_dirs() -> list[Path]:
    dirs: list[Path] = []
    latest = _read_latest_build_dir()
    if latest is not None:
        dirs.append(latest)
    if PATCHER_DIR.is_dir():
        extra = sorted(
            (
                path
                for path in PATCHER_DIR.iterdir()
                if path.is_dir()
                and (path.name == "_staging_build" or path.name.startswith("_staging_build_"))
                and (path / "HotfixPatcher.exe").is_file()
            ),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        )
        for path in extra:
            if path not in dirs:
                dirs.append(path)
    if STAGING_PATCHER_EXE.is_file() and PATCHER_STAGING_DIR not in dirs:
        dirs.append(PATCHER_STAGING_DIR)
    return dirs


def _newest_patcher_exe(candidates: list[Path]) -> Path | None:
    best: Path | None = None
    best_mtime = -1.0
    for path in candidates:
        if not path.is_file():
            continue
        mtime = path.stat().st_mtime
        if mtime > best_mtime:
            best = path
            best_mtime = mtime
    return best


def resolve_patcher_exe() -> Path | None:
    root = toolkit_root()
    meipass = getattr(sys, "_MEIPASS", None)
    ordered: list[Path] = []

    latest = _read_latest_build_dir()
    if latest is not None:
        ordered.append(latest / "HotfixPatcher.exe")

    for build_dir in _iter_patcher_build_dirs():
        candidate = build_dir / "HotfixPatcher.exe"
        if candidate not in ordered:
            ordered.append(candidate)

    if not _is_frozen() and DEV_PATCHER_EXE.is_file():
        ordered.append(DEV_PATCHER_EXE)

    ordered.extend(
        [
            root / PATCHER_SUBDIR / "HotfixPatcher.exe.new",
            STAGING_PATCHER_EXE,
            root / PATCHER_SUBDIR / "HotfixPatcher.exe",
            root / "patcher_publish" / "HotfixPatcher.exe.new",
            root / "patcher_publish" / "HotfixPatcher.exe",
        ]
    )
    if meipass:
        ordered.append(Path(meipass) / PATCHER_SUBDIR / "HotfixPatcher.exe")

    seen: set[str] = set()
    candidates: list[Path] = []
    for path in ordered:
        key = str(path.resolve()).lower() if path.is_absolute() else str(path).lower()
        if key in seen:
            continue
        seen.add(key)
        if path.is_file():
            candidates.append(path)
    return _newest_patcher_exe(candidates)


def patcher_launcher() -> list[str]:
    exe = resolve_patcher_exe()
    if exe is not None:
        return [str(exe)]
    if PATCHER_DLL.is_file():
        return ["dotnet", str(PATCHER_DLL)]
    if DEV_PATCHER_DLL.is_file():
        return ["dotnet", str(DEV_PATCHER_DLL)]
    return []


def ensure_patcher() -> list[str]:
    launcher = patcher_launcher()
    if launcher:
        return launcher
    if not DEV_PATCHER_CSPROJ.is_file():
        raise FileNotFoundError(
            f"找不到补丁程序。请先点「初始化」编译补丁引擎，"
            f"或将 HotfixPatcher.exe 放入 {PACKAGED_ROOT / PATCHER_SUBDIR}"
        )
    out_dir = DEV_PATCHER_DLL.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        ["dotnet", "build", str(DEV_PATCHER_CSPROJ), "-c", "Release", "-o", str(out_dir)],
        check=True,
    )
    if DEV_PATCHER_DLL.is_file():
        return ["dotnet", str(DEV_PATCHER_DLL)]
    raise FileNotFoundError(f"编译后仍找不到: {DEV_PATCHER_DLL}")


def _same_path(a: Path, b: Path) -> bool:
    """src/dst 指向同一文件时 shutil.copy2 会抛 SameFileError。"""
    try:
        return os.path.samefile(a, b)
    except OSError:
        try:
            return a.resolve() == b.resolve()
        except OSError:
            return str(a) == str(b)


def _safe_copy2(src: Path, dst: Path) -> bool:
    """复制文件；若源与目标相同则跳过并返回 False。"""
    if _same_path(src, dst):
        return False
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    return True


def _combo_state_path() -> Path:
    return toolkit_root() / "combo_patch_state.json"


def _last_combo_sha256() -> str | None:
    path = _combo_state_path()
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    sha = data.get("sha256")
    return sha if isinstance(sha, str) and sha else None


def clear_combo_patch_state() -> bool:
    path = _combo_state_path()
    if not path.is_file():
        return False
    path.unlink()
    return True


def _is_clean_hotfix_file(path: Path) -> tuple[bool, str]:
    """是否可作为干净 hotfix 底稿（无桥接、非上次补丁产物、客服入口未改写）。"""
    if not path.is_file():
        return False, "文件不存在"
    variant = detect_bridge_variant_on_file(path)
    if variant not in ("not_patched", "missing"):
        return False, f"已含桥接/补丁 ({variant})"
    size = path.stat().st_size
    if size < 6_000_000:
        return False, f"体积过小 ({size:,})"
    digest = sha256_file(path)
    last_sha = _last_combo_sha256()
    if last_sha and digest == last_sha:
        return False, "内容与上次应用补丁一致（非干净原版）"
    try:
        mode = detect_customer_gm_mode(path)
    except Exception:
        mode = "unknown"
    if mode not in ("original", "unknown"):
        return False, f"客服入口已改写 ({mode})"
    return True, ""


def _copy_file_best_effort(src: Path, dst: Path) -> bool:
    try:
        return _safe_copy2(src, dst)
    except OSError:
        return False


def _copy_tree_best_effort(src_dir: Path, dst_dir: Path) -> bool:
    if not src_dir.is_dir():
        return False
    dst_dir.mkdir(parents=True, exist_ok=True)
    ok = True
    for item in src_dir.iterdir():
        target = dst_dir / item.name
        try:
            if item.is_dir():
                if not _copy_tree_best_effort(item, target):
                    ok = False
            else:
                shutil.copy2(item, target)
        except OSError:
            ok = False
    return ok


def _prepare_patcher_build_dir() -> Path:
    """编译到全新目录，避免删除/覆盖正在使用的 HotfixPatcher.exe。"""
    build_dir = PATCHER_DIR / f"_staging_build_{int(time.time())}"
    build_dir.mkdir(parents=True, exist_ok=True)
    return build_dir


def _read_latest_build_dir() -> Path | None:
    if PATCHER_BUILD_POINTER.is_file():
        raw = PATCHER_BUILD_POINTER.read_text(encoding="utf-8").strip()
        if raw:
            path = Path(raw)
            if not path.is_absolute():
                path = PATCHER_DIR / path
            if (path / "HotfixPatcher.exe").is_file():
                return path
    build_dirs = sorted(
        (
            path
            for path in PATCHER_DIR.glob("_staging_build*")
            if path.is_dir() and (path / "HotfixPatcher.exe").is_file()
        ),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    return build_dirs[0] if build_dirs else None


def _remember_latest_build_dir(build_dir: Path) -> None:
    try:
        rel = build_dir.relative_to(PATCHER_DIR)
        pointer_value = rel.as_posix()
    except ValueError:
        pointer_value = str(build_dir)
    PATCHER_BUILD_POINTER.write_text(pointer_value + "\n", encoding="utf-8")


def _sync_patcher_tree(src_dir: Path, dst_dir: Path) -> bool:
    try:
        dst_dir.mkdir(parents=True, exist_ok=True)
        for item in src_dir.iterdir():
            target = dst_dir / item.name
            if item.is_dir():
                shutil.copytree(item, target, dirs_exist_ok=True)
            else:
                shutil.copy2(item, target)
        return True
    except OSError:
        return False


def rebuild_patcher_engine() -> list[str]:
    messages: list[str] = []
    if _is_frozen() and resolve_patcher_exe() is not None:
        messages.append("使用内置补丁引擎（发布版跳过重新编译）")
        return messages
    if not DEV_PATCHER_CSPROJ.is_file():
        messages.append("跳过补丁引擎：未找到 tools/hotfix_patcher 源码")
        return messages

    build_dir = _prepare_patcher_build_dir()

    proc = subprocess.run(
        [
            "dotnet",
            "publish",
            str(DEV_PATCHER_CSPROJ),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:InvariantGlobalization=true",
            "-o",
            str(build_dir),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    out = (proc.stdout or "") + (proc.stderr or "")
    if proc.returncode != 0:
        raise RuntimeError(out.strip() or "补丁引擎 dotnet publish 失败")

    built = build_dir / "HotfixPatcher.exe"
    if not built.is_file():
        raise RuntimeError("补丁引擎编译完成，但未找到 HotfixPatcher.exe")

    stub_src = GAME_ROOT / "tools" / "hotfix_patcher" / "ref_stubs" / "bin"
    stub_dst = build_dir / "ref_stubs"
    if stub_src.is_dir():
        if _sync_patcher_tree(stub_src, stub_dst):
            messages.append("已同步 ref_stubs 到补丁引擎目录")

    _remember_latest_build_dir(build_dir)

    try:
        rel = build_dir.relative_to(PATCHER_DIR)
        messages.append(f"已编译补丁引擎（patcher/{rel}）")
    except ValueError:
        messages.append(f"已编译补丁引擎（{build_dir.name}）")

    if _sync_patcher_tree(build_dir, PATCHER_STAGING_DIR):
        messages.append("已同步到 patcher/_staging")
    else:
        messages.append("patcher/_staging 被占用，已保留新引擎在编译目录（打补丁会自动选用最新版）")

    if _copy_file_best_effort(built, DEV_PATCHER_EXE):
        try:
            rel = DEV_PATCHER_EXE.relative_to(GAME_ROOT)
            messages.append(f"已同步到 {rel}")
        except ValueError:
            messages.append(f"已同步到 {DEV_PATCHER_EXE.name}")
    if _copy_file_best_effort(built, PATCHER_EXE):
        messages.append("已更新 patcher/HotfixPatcher.exe")
        new_path = PATCHER_DIR / "HotfixPatcher.exe.new"
        if new_path.is_file():
            new_path.unlink()
    elif _copy_file_best_effort(built, PATCHER_DIR / "HotfixPatcher.exe.new"):
        messages.append("patcher/HotfixPatcher.exe 占用中，已写入 HotfixPatcher.exe.new（关闭 GUI 后可手动替换）")
    elif PATCHER_EXE.is_file():
        messages.append("patcher/HotfixPatcher.exe 占用中，将使用最新编译目录内的引擎")

    return messages


def run_patcher_capture(args: list[str]) -> subprocess.CompletedProcess:
    cmd = [*ensure_patcher(), *args]
    return subprocess.run(
        cmd,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def detect_customer_gm_mode(hotfix: Path) -> str:
    proc = run_patcher_capture(["customer-gm-patch", "--hotfix", str(hotfix), "--detect"])
    if proc.returncode != 0:
        return "unknown"
    return (proc.stdout or "").strip() or "unknown"


def sniff_customer_gm(hotfix: Path) -> tuple[bool, str]:
    proc = run_patcher_capture(["customer-gm-patch", "--hotfix", str(hotfix), "--sniff"])
    out = (proc.stdout or "") + (proc.stderr or "")
    return proc.returncode == 0, out.strip()


def normalize_customer_gm_mode(mode: str) -> str:
    if mode in ("dojo", "tower", "endless", "无尽之塔", "无尽"):
        return "autoskill"
    if mode in ("boss", "ruby", "gm1", "gm2", "gm3", "gm4", "gm5"):
        return "autoskill"
    if mode in ("3045", "讨伐令", "suppress"):
        return "challengeboss"
    if mode in ("3047", "英雄试炼", "hero_trials"):
        return "bravetrial"
    if mode in ("3046", "地鼠", "地鼠抽奖", "earthmouse", "diglett"):
        return "autoskill"
    if mode in ("3050", "boss大陆", "水晶副本", "crystal_sw", "bossland"):
        return "autoskill"
    if mode in ("collection", "collect", "采集", "采集面板"):
        return "autoskill"
    if mode in ("公会", "公会领地", "传送公会", "family", "guild", "familyhall"):
        return "autoskill"
    if mode in ("宠物改造", "改造", "pet-reform", "reform", "petreform"):
        return "autoskill"
    if mode in CUSTOMER_GM_MODES:
        return mode
    return "autoskill"


def updated_hotfix_candidate(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请在 GUI 中选择包含 {DATA_DIR} 的文件夹")
    return root / UPDATED_HOTFIX_REL


def baseline_meta_path(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    return root / BASELINE_META_REL


def load_baseline_meta(game_root: Path | None = None) -> dict | None:
    path = baseline_meta_path(game_root)
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def save_baseline_meta(game_root: Path, data: dict) -> Path:
    path = baseline_meta_path(game_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return path


def hotfix_watch_stamp_path() -> Path:
    return toolkit_root() / HOTFIX_WATCH_STAMP_NAME


def mark_hotfix_watch_stamp(
    game_root: Path | None = None,
    *,
    marked_by: str = "manual",
) -> dict:
    """初始化 / 打补丁成功后记录当前游戏 hotfix 指纹（size+sha 为主，mtime 辅助）。"""
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    info = _file_digest_info(hotfix_path(root))
    if info is None:
        raise FileNotFoundError("无法标记：游戏内 hotfix.dll.bytes 不存在")
    payload = {
        "game_root": str(root.resolve()),
        "size": info["size"],
        "sha256": info["sha256"],
        "mtime": info["mtime"],
        "marked_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "marked_by": marked_by,
    }
    path = hotfix_watch_stamp_path()
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    return payload


def load_hotfix_watch_stamp(game_root: Path | None = None) -> dict | None:
    root = game_root or get_game_root()
    if root is None:
        return None
    path = hotfix_watch_stamp_path()
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    stamped_root = data.get("game_root")
    if stamped_root:
        try:
            if Path(stamped_root).resolve() != root.resolve():
                return None
        except OSError:
            return None
    if not data.get("sha256") or not data.get("size"):
        return None
    return data


def detect_hotfix_drift(game_root: Path | None = None) -> dict:
    """对比上次标记与当前游戏 hotfix；sha/size 不一致视为漂移（可能客户端已更新）。"""
    root = game_root or get_game_root()
    empty = {
        "has_stamp": False,
        "drifted": False,
        "reason": "no_root",
        "detail": "",
        "current": None,
        "stamp": None,
        "is_own_patch": False,
        "looks_like_client_update": False,
    }
    if root is None:
        return empty
    stamp = load_hotfix_watch_stamp(root)
    info = _file_digest_info(hotfix_path(root))
    result = {
        "has_stamp": stamp is not None,
        "drifted": False,
        "reason": "ok",
        "detail": "",
        "current": info,
        "stamp": stamp,
        "is_own_patch": False,
        "looks_like_client_update": False,
    }
    if info is None:
        result.update(drifted=True, reason="missing", detail="游戏内 hotfix 缺失")
        return result
    if stamp is None:
        result["reason"] = "no_stamp"
        return result
    if stamp.get("sha256") == info["sha256"] and int(stamp.get("size") or 0) == info["size"]:
        return result
    last = _last_combo_sha256()
    if last and info["sha256"] == last:
        result["is_own_patch"] = True
        result["reason"] = "own_patch_unmarked"
        return result
    result["drifted"] = True
    size_changed = int(stamp.get("size") or 0) != info["size"]
    clean, clean_reason = _is_clean_hotfix_file(hotfix_path(root))
    result["looks_like_client_update"] = bool(size_changed or clean)
    if size_changed:
        result["reason"] = "size_changed"
        result["detail"] = (
            f"体积 {int(stamp.get('size') or 0):,} → {info['size']:,}；"
            f"sha {str(stamp.get('sha256'))[:12]}… → {info['sha256'][:12]}…"
        )
    elif clean:
        result["reason"] = "content_changed_clean"
        result["detail"] = (
            f"体积未变但内容已变（干净原版）；"
            f"sha {str(stamp.get('sha256'))[:12]}… → {info['sha256'][:12]}…"
        )
    else:
        result["reason"] = "content_changed_dirty"
        result["detail"] = (
            f"内容已变且不像干净原版（{clean_reason}）；"
            f"sha {str(stamp.get('sha256'))[:12]}… → {info['sha256'][:12]}…"
        )
    return result


def format_hotfix_drift_hint(drift: dict) -> str:
    if not drift.get("drifted"):
        return ""
    detail = drift.get("detail") or ""
    return "检测到游戏 hotfix 与上次标记不一致（可能已更新）" + (f"：{detail}" if detail else "")


def _format_size_literal(n: int) -> str:
    return f"{n:_}"


def _bump_expected_size_constants(new_size: int) -> list[str]:
    """体积变化时同步 Python/C# 常量并重建引擎。"""
    global EXPECTED_SIZE, KNOWN_OLD_SIZES
    messages: list[str] = []
    old = EXPECTED_SIZE
    if new_size == old:
        return messages

    old_lit = _format_size_literal(old)
    new_lit = _format_size_literal(new_size)
    pc_path = Path(__file__).resolve()
    text = pc_path.read_text(encoding="utf-8")
    if f"EXPECTED_SIZE = {old_lit}" in text:
        text = text.replace(f"EXPECTED_SIZE = {old_lit}", f"EXPECTED_SIZE = {new_lit}", 1)
    elif f"EXPECTED_SIZE = {old}" in text:
        text = text.replace(f"EXPECTED_SIZE = {old}", f"EXPECTED_SIZE = {new_lit}", 1)
    else:
        raise RuntimeError(f"无法在 patch_common.py 中定位 EXPECTED_SIZE={old}")

    known_line = f'    {old_lit}: "自动标记：更新前旧版",\n'
    if f"{old_lit}:" not in text and f"{old}:" not in text:
        anchor = "KNOWN_OLD_SIZES: dict[int, str] = {\n"
        if anchor not in text:
            raise RuntimeError("无法在 patch_common.py 中定位 KNOWN_OLD_SIZES")
        text = text.replace(anchor, anchor + known_line, 1)
    pc_path.write_text(text, encoding="utf-8")
    messages.append(f"已更新 patch_common.EXPECTED_SIZE：{old:,} → {new_size:,}")

    hs_path = GAME_ROOT / "tools" / "hotfix_patcher" / "HotfixSize.cs"
    if hs_path.is_file():
        hs = hs_path.read_text(encoding="utf-8")
        if f"Expected = {old_lit}" in hs:
            hs = hs.replace(f"Expected = {old_lit}", f"Expected = {new_lit}", 1)
        elif f"Expected = {old}" in hs:
            hs = hs.replace(f"Expected = {old}", f"Expected = {new_lit}", 1)
        else:
            raise RuntimeError(f"无法在 HotfixSize.cs 中定位 Expected={old}")
        hs_path.write_text(hs, encoding="utf-8")
        messages.append(f"已更新 HotfixSize.Expected：{old:,} → {new_size:,}")
    else:
        messages.append("警告：未找到 HotfixSize.cs，跳过 C# 常量更新")

    EXPECTED_SIZE = new_size
    KNOWN_OLD_SIZES = {**KNOWN_OLD_SIZES, old: "自动标记：更新前旧版"}
    messages.extend(rebuild_patcher_engine())
    return messages


def adopt_client_hotfix_update(game_root: Path | None = None) -> list[str]:
    """
    自动修复：用当前游戏内干净 hotfix 重建底稿（neworig / .orig / baseline），
    绝不拿旧 .orig 覆盖新文件。体积变了则同步工具常量并重建引擎。
    """
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    hf = hotfix_path(root)
    ok, reason = _is_clean_hotfix_file(hf)
    if not ok:
        raise RuntimeError(
            "无法自动修复：游戏内 hotfix 不是干净原版"
            f"（{reason}）。\n\n"
            "请先关闭游戏，用启动器「更新/修复」拉回官方 hotfix，"
            "或从 crosscopy 等备份拷贝干净文件后再试。"
        )

    messages: list[str] = []
    size = hf.stat().st_size
    digest = sha256_file(hf)
    if size != EXPECTED_SIZE:
        messages.extend(_bump_expected_size_constants(size))

    neworig = updated_hotfix_candidate(root)
    neworig.parent.mkdir(parents=True, exist_ok=True)
    if _safe_copy2(hf, neworig):
        messages.append(f"已采用游戏内 hotfix 写入 neworig（{size:,} 字节）")
    else:
        messages.append(f"neworig 已与游戏 hotfix 一致（{size:,} 字节）")

    orig = hotfix_orig(root)
    if _safe_copy2(neworig, orig):
        messages.append("已重建 hotfix.dll.bytes.orig")
    else:
        messages.append(".orig 已与 neworig 一致")

    data_dir = root / DATA_DIR
    payload = {
        "expected_size": size,
        "neworig_sha256": digest,
        "source": "adopt_client_update",
        "synced_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "notes": f"自动修复采新底稿；工具 EXPECTED_SIZE={EXPECTED_SIZE:,}",
    }
    if data_dir.is_dir():
        payload["cg37_data_mtime"] = data_dir.stat().st_mtime
    save_baseline_meta(root, payload)

    hf_dir = hf.parent
    for name in ("SeqChapterHelperBridge.dll.bytes", "SeqChapterNineAction.dll.bytes"):
        extra = hf_dir / name
        if extra.is_file():
            try:
                extra.unlink()
                messages.append(f"已移除残留 {name}")
            except OSError:
                messages.append(f"警告：无法删除 {name}（请关闭游戏后手动删）")

    if clear_combo_patch_state():
        messages.append("已清除上次补丁状态")
    mark_hotfix_watch_stamp(root, marked_by="adopt")
    messages.append("自动修复完成：请再点「应用补丁」叠回所需功能")
    return messages


def effective_expected_size(game_root: Path | None = None) -> int:
    meta = load_baseline_meta(game_root)
    if meta and meta.get("expected_size"):
        try:
            return int(meta["expected_size"])
        except (TypeError, ValueError):
            pass
    try:
        candidate = updated_hotfix_candidate(game_root)
        if candidate.is_file():
            return candidate.stat().st_size
    except FileNotFoundError:
        pass
    return EXPECTED_SIZE


def detect_bridge_variant_on_file(hotfix_file: Path) -> str:
    if not hotfix_file.is_file():
        return "missing"
    proc = run_patcher_capture(
        ["helper-bridge-patch", "--hotfix", str(hotfix_file), "--detect-variant"]
    )
    out = ((proc.stdout or "") + (proc.stderr or "")).strip().lower()
    for variant in (
        "binary_loadfrom",
        "cecil_light_loadbytes",
        "cecil_light_loadfrom",
        "cecil_light",
        "embedded",
        "not_patched",
        "broken",
        "missing",
        "unknown",
    ):
        if variant in out:
            return variant
    return "unknown"


def _file_digest_info(path: Path) -> dict | None:
    if not path.is_file():
        return None
    stat = path.stat()
    return {
        "path": str(path),
        "size": stat.st_size,
        "sha256": sha256_file(path),
        "mtime": stat.st_mtime,
    }


def pick_clean_hotfix_source(
    game_root: Path,
    *,
    skip_labels: frozenset[str] | None = None,
) -> tuple[Path, str]:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    neworig = updated_hotfix_candidate(game_root)
    meta = load_baseline_meta(game_root) or {}
    meta_sha = meta.get("neworig_sha256")
    errors: dict[str, str] = {}
    skip = skip_labels or frozenset()

    def _is_usable(label: str, path: Path) -> bool:
        if label in skip:
            return False
        ok, reason = _is_clean_hotfix_file(path)
        if not ok:
            errors[label] = reason
            return False
        return True

    # 客户端更新后 hotfix 往往是最新干净版；.orig / neworig 可能仍是旧底稿（同体积不同 SHA）
    candidates: list[tuple[str, Path]] = []
    for label, path in (("hotfix", hotfix), (".orig", orig), ("neworig", neworig)):
        if _is_usable(label, path):
            candidates.append((label, path))

    # 1) 游戏内干净 hotfix 与已记录底稿不同 → 优先当作客户端更新后的新原版
    if "hotfix" not in skip and _is_usable("hotfix", hotfix):
        hotfix_sha = sha256_file(hotfix)
        if not meta_sha:
            return hotfix, "hotfix"
        if hotfix_sha != meta_sha:
            return hotfix, "hotfix(客户端更新)"

    # 2) 与 meta 一致的候选（重复初始化、尚未被更新覆盖）
    if meta_sha:
        for label, path in candidates:
            if sha256_file(path) == meta_sha:
                return path, label

    # 3) 其余干净候选（hotfix → .orig → neworig）
    if candidates:
        label, path = candidates[0]
        return path, label

    hint = "\n".join(f"{k}: {v}" for k, v in errors.items()) if errors else "找不到可用的干净 hotfix"
    raise RuntimeError(
        "无法同步客户端底稿：当前 hotfix / .orig 均不可用。\n"
        f"{hint}\n\n"
        "请先在启动器里「修复/更新」客户端，确认 hotfix 为干净原版后再同步。"
    )


def sync_client_baseline(game_root: Path | None = None, *, force: bool = False) -> list[str]:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    messages: list[str] = []
    candidate = updated_hotfix_candidate(root)

    try:
        src, label = pick_clean_hotfix_source(root, skip_labels=frozenset({"neworig"}))
    except RuntimeError:
        ok, reason = _is_clean_hotfix_file(candidate)
        if ok:
            src, label = candidate, "neworig(已有底稿)"
            messages.append(
                "游戏内 hotfix / .orig 已含补丁，将沿用 tools/hotfix.dll.bytes.neworig 作为底稿"
            )
        else:
            raise RuntimeError(
                f"无法同步客户端底稿：游戏内 hotfix 不可用，且 neworig 也不可用（{reason}）。\n\n"
                "请先在启动器「修复/更新」客户端，或从 crosscopy 恢复干净 hotfix 后再初始化。"
            ) from None

    size = src.stat().st_size
    digest = sha256_file(src)
    meta = load_baseline_meta(root)

    if (
        not force
        and candidate.is_file()
        and candidate.stat().st_size == size
        and sha256_file(candidate) == digest
        and meta
        and meta.get("neworig_sha256") == digest
    ):
        messages.append(f"客户端底稿已是最新（{size:,} 字节，来源 {label}）")
        return messages

    candidate.parent.mkdir(parents=True, exist_ok=True)
    if _safe_copy2(src, candidate):
        messages.append(f"已同步 tools/hotfix.dll.bytes.neworig（{size:,} 字节，来源 {label}）")
    else:
        messages.append(
            f"tools/hotfix.dll.bytes.neworig 已是最新内容（{size:,} 字节，来源 {label}，跳过自复制）"
        )
    digest = sha256_file(candidate)
    data_dir = root / DATA_DIR
    payload = {
        "expected_size": size,
        "neworig_sha256": digest,
        "source": label,
        "synced_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "notes": f"序章 hotfix 底稿；工具 EXPECTED_SIZE={EXPECTED_SIZE:,}",
    }
    if data_dir.is_dir():
        payload["cg37_data_mtime"] = data_dir.stat().st_mtime
    save_baseline_meta(root, payload)
    messages.append(f"SHA256: {digest[:16]}…")

    orig = hotfix_orig(root)
    if orig.is_file() and orig.stat().st_size != size:
        messages.append("警告：现有 .orig 与新版体积不一致，初始化将自动重建 .orig")
    elif orig.is_file() and sha256_file(orig) != digest:
        if _safe_copy2(candidate, orig):
            messages.append("已从 neworig 自动对齐 hotfix.dll.bytes.orig（内容与底稿一致）")
        elif _same_path(candidate, orig):
            messages.append("hotfix.dll.bytes.orig 已与 neworig 一致（跳过自复制）")
    return messages


def get_update_status(game_root: Path | None = None) -> dict:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请选择包含 {DATA_DIR} 的文件夹")
    expected = effective_expected_size(root)
    meta = load_baseline_meta(root) or {}
    hotfix = hotfix_path(root)
    orig = hotfix_orig(root)
    neworig = updated_hotfix_candidate(root)

    hotfix_info = _file_digest_info(hotfix)
    orig_info = _file_digest_info(orig)
    neworig_info = _file_digest_info(neworig)

    hotfix_size = hotfix_info["size"] if hotfix_info else None
    orig_size = orig_info["size"] if orig_info else None
    neworig_size = neworig_info["size"] if neworig_info else None

    # 仅「真正干净原版」偏离底稿才视为客户端更新；已打玩法补丁（无桥接）不算。
    hotfix_clean = False
    if hotfix_info:
        hotfix_clean, _ = _is_clean_hotfix_file(hotfix)

    neworig_matches_meta = bool(
        neworig_info
        and meta.get("neworig_sha256") == neworig_info["sha256"]
        and meta.get("expected_size") == neworig_size
    )
    orig_matches_neworig = bool(
        orig_info and neworig_info and orig_info["sha256"] == neworig_info["sha256"]
    )
    orig_size_ok = orig_size == expected if orig_size is not None else False

    hotfix_differs_baseline = bool(
        hotfix_info and neworig_info and hotfix_info["sha256"] != neworig_info["sha256"]
    )
    needs_sync_neworig = bool(
        not neworig_info
        or not neworig_matches_meta
        or (hotfix_clean and hotfix_size == expected and neworig_size is not None and neworig_size != expected)
        or (hotfix_clean and hotfix_differs_baseline)
    )
    needs_reinit = bool(
        needs_sync_neworig
        or not orig_info
        or not orig_size_ok
        or (neworig_info and not orig_matches_neworig)
    )
    # ready = 底稿工作区可用；当前 hotfix 可以是已打补丁状态
    ready = bool(orig_size_ok and orig_matches_neworig and neworig_matches_meta and not needs_reinit)

    old_size_hint = ""
    if hotfix_size in KNOWN_OLD_SIZES:
        old_size_hint = KNOWN_OLD_SIZES[hotfix_size]

    return {
        "expected_size": expected,
        "hotfix_exists": hotfix_info is not None,
        "hotfix_size": hotfix_size,
        "hotfix_clean": hotfix_clean,
        "hotfix_sha256": hotfix_info["sha256"] if hotfix_info else None,
        "orig_exists": orig_info is not None,
        "orig_size": orig_size,
        "orig_size_ok": orig_size_ok,
        "orig_sha256": orig_info["sha256"] if orig_info else None,
        "neworig_exists": neworig_info is not None,
        "neworig_size": neworig_size,
        "neworig_sha256": neworig_info["sha256"] if neworig_info else None,
        "baseline_meta": meta,
        "neworig_matches_meta": neworig_matches_meta,
        "orig_matches_neworig": orig_matches_neworig,
        "needs_sync_neworig": needs_sync_neworig,
        "needs_reinit": needs_reinit,
        "ready": ready,
        "old_size_hint": old_size_hint,
        "toolkit_expected_size": EXPECTED_SIZE,
        "size_constant_stale": expected != EXPECTED_SIZE and hotfix_size == EXPECTED_SIZE,
    }


def format_client_update_hint(status: dict) -> str:
    if status.get("ready"):
        return ""
    hotfix_size = status.get("hotfix_size")
    expected = status.get("expected_size")
    if hotfix_size and expected and hotfix_size != expected:
        hint = status.get("old_size_hint") or f"期望 {expected:,}"
        return f"hotfix 体积 {hotfix_size:,}（{hint}）— 请启动器「更新/修复」后点「初始化」"
    if status.get("needs_sync_neworig") and status.get("hotfix_clean"):
        return "检测到客户端原版已变化 — 请关闭游戏后点红色「初始化」重建底稿，再「应用补丁」"
    return "请先关闭游戏，再点红色「初始化」"


def format_size_status(hotfix_size: int | None, orig_size: int | None = None, *, expected: int | None = None) -> str:
    expected = expected or EXPECTED_SIZE
    if hotfix_size is None:
        return "缺失"
    if hotfix_size == expected:
        if orig_size is None:
            return f"正常 ({hotfix_size:,})"
        if orig_size == expected:
            return f"正常 ({hotfix_size:,}，.orig 已对齐)"
        return f"hotfix 正常 ({hotfix_size:,})，.orig 过期 ({orig_size:,})"
    if hotfix_size in KNOWN_OLD_SIZES:
        candidate = None
        try:
            candidate = updated_hotfix_candidate()
        except FileNotFoundError:
            pass
        hint = f"当前为 {KNOWN_OLD_SIZES[hotfix_size]} 旧版"
        if candidate is not None and candidate.is_file() and candidate.stat().st_size == expected:
            hint += "，请点「同步客户端底稿」后「初始化」"
        else:
            hint += "，请先在启动器更新/修复客户端后再同步底稿"
        return f"异常 {hotfix_size:,}（{hint}，期望 {expected:,}）"
    if hotfix_size == OLD_HOTFIX_SIZE:
        candidate = None
        try:
            candidate = updated_hotfix_candidate()
        except FileNotFoundError:
            pass
        hint = "当前为更新前旧版"
        if candidate is not None and candidate.is_file() and candidate.stat().st_size == expected:
            hint += "，请点「初始化」"
        else:
            hint += "，请先在启动器更新/修复客户端后再点「初始化」"
        return f"异常 {hotfix_size:,}（{hint}，期望 {expected:,}）"
    return f"异常 {hotfix_size:,}（期望 {expected:,}）"


def verify_hotfix(path: Path, *, expected: int | None = None) -> None:
    expected = expected or (path.stat().st_size if path.is_file() else EXPECTED_SIZE)
    if not path.is_file():
        raise FileNotFoundError(f"找不到 hotfix: {path}")
    size = path.stat().st_size
    if size != expected:
        raise ValueError(format_size_status(size, expected=expected))


def restore_updated_hotfix(game_root: Path | None = None) -> Path:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请在 GUI 中选择包含 {DATA_DIR} 的文件夹")
    candidate = updated_hotfix_candidate(root)
    hotfix = hotfix_path(root)
    orig = hotfix_orig(root)
    expected = effective_expected_size(root)
    if not candidate.is_file():
        raise FileNotFoundError(
            f"找不到更新版 hotfix 样本:\n{candidate}\n"
            f"请先在启动器里「修复/更新」客户端，再点「同步客户端底稿」。"
        )
    cand_size = candidate.stat().st_size
    if cand_size != expected and cand_size != EXPECTED_SIZE:
        raise ValueError(
            f"更新版 hotfix 样本体积不对: {cand_size}（期望 {expected} 或工具常量 {EXPECTED_SIZE}）"
        )
    _safe_copy2(candidate, hotfix)
    if orig.is_file() and orig.stat().st_size != cand_size:
        orig.unlink()
    return hotfix


def has_valid_orig_backup(game_root: Path | None = None) -> bool:
    orig = hotfix_orig(game_root)
    expected = effective_expected_size(game_root)
    if not orig.is_file() or orig.stat().st_size != expected:
        return False
    try:
        st = get_update_status(game_root)
    except FileNotFoundError:
        return True
    if st.get("neworig_sha256") and st.get("orig_sha256"):
        return bool(st.get("orig_matches_neworig"))
    return True


def _workspace_fully_ready(root: Path, update_st: dict) -> bool:
    return bool(
        update_st.get("ready")
        and update_st.get("orig_matches_neworig")
        and update_st.get("neworig_matches_meta")
        and has_valid_orig_backup(root)
    )


def _copy_baseline_to_hotfix_and_orig(root: Path, canonical: Path) -> list[str]:
    messages: list[str] = []
    size = canonical.stat().st_size
    hotfix = hotfix_path(root)
    orig = hotfix_orig(root)
    if _safe_copy2(canonical, hotfix):
        messages.append(f"已从 neworig 恢复干净 hotfix（{size:,} 字节）")
    elif _same_path(canonical, hotfix):
        messages.append(f"游戏 hotfix 已与 neworig 一致（{size:,} 字节，跳过写入）")
    else:
        messages.append(f"无法写入 hotfix（请先关闭游戏）: {hotfix}")
    if _safe_copy2(canonical, orig):
        messages.append("已从 neworig 重建 hotfix.dll.bytes.orig")
    elif _same_path(canonical, orig):
        messages.append("hotfix.dll.bytes.orig 已与 neworig 一致（跳过写入）")
    else:
        messages.append(f"无法写入 .orig（请先关闭游戏）: {orig}")
    return messages


def _run_full_baseline_reset(root: Path) -> list[str]:
    """同步客户端底稿 → 用 neworig 覆盖 hotfix 与 .orig → 清除旧补丁状态。"""
    messages: list[str] = []
    messages.extend(sync_client_baseline(root, force=True))

    canonical = updated_hotfix_candidate(root)
    if not canonical.is_file():
        raise FileNotFoundError(
            f"缺少客户端底稿:\n{canonical}\n\n"
            "请先在启动器里「更新 / 修复」客户端，关闭游戏后再点「初始化」。"
        )

    if canonical.stat().st_size < 6_000_000:
        raise ValueError(f"neworig 体积异常: {canonical.stat().st_size:,}")

    messages.extend(_copy_baseline_to_hotfix_and_orig(root, canonical))
    if clear_combo_patch_state():
        messages.append("已清除上次补丁状态（combo_patch_state），请重新「应用补丁」")
    messages.append("初始化完成：底稿 / hotfix / .orig 已对齐为当前客户端干净原版")
    return messages


def initialize_hotfix_workspace(game_root: Path | None = None, *, force: bool = False) -> tuple[Path, Path, list[str]]:
    root = game_root or get_game_root()
    if root is None:
        raise FileNotFoundError(f"未设置游戏目录，请在 GUI 中选择包含 {DATA_DIR} 的文件夹")
    messages: list[str] = []
    update_st = get_update_status(root)

    messages.extend(rebuild_patcher_engine())

    if force:
        messages.extend(_run_full_baseline_reset(root))
    elif _workspace_fully_ready(root, update_st):
        messages.append("已完成初始化，可直接打补丁")
    else:
        messages.extend(_run_full_baseline_reset(root))

    normalize_channel_on_init(root)
    try:
        mark_hotfix_watch_stamp(root, marked_by="init")
        messages.append("已标记当前 hotfix 指纹（供下次检测客户端更新）")
    except Exception as exc:
        messages.append(f"警告：标记 hotfix 指纹失败（{exc}）")
    return hotfix_path(root), hotfix_orig(root), messages


def ensure_orig_backup(
    game_root: Path | None = None,
    *,
    expected: int | None = None,
    source: Path | None = None,
) -> Path:
    hotfix = hotfix_path(game_root)
    orig = hotfix_orig(game_root)
    expected = expected or effective_expected_size(game_root)
    canonical = source
    if canonical is None:
        candidate = updated_hotfix_candidate(game_root)
        meta = load_baseline_meta(game_root) or {}
        if (
            candidate.is_file()
            and meta.get("neworig_sha256")
            and sha256_file(candidate) == meta.get("neworig_sha256")
        ):
            canonical = candidate
        else:
            canonical = hotfix
    if not canonical.is_file():
        raise FileNotFoundError(f"找不到用于创建 .orig 的源文件: {canonical}")
    if canonical.stat().st_size != expected:
        raise ValueError(
            f"源文件体积 {canonical.stat().st_size:,} 与期望 {expected:,} 不一致"
        )

    needs_replace = not orig.is_file() or orig.stat().st_size != expected
    if not needs_replace:
        needs_replace = sha256_file(orig) != sha256_file(canonical)

    if needs_replace:
        if orig.is_file():
            orig.unlink()
        _safe_copy2(canonical, orig)
    return orig
