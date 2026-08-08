# -*- coding: utf-8 -*-
"""从 **crosscopy**（干净只读）提取 Luban 配置到 **cross** 工作目录 tools/_config_extract。

绝不写入 crosscopy。需要 UnityPy。
"""
from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from seqchapter_paths import (  # noqa: E402
    BUNDLE_EXCELGENERAL,
    BUNDLE_EXCELGENERAL_L,
    CLEAN_ASSETS,
    CLEAN_ROOT,
    CONFIG_EXTRACT,
    WORK_ROOT,
    assert_not_clean,
)

try:
    import UnityPy
except ImportError as exc:
    raise SystemExit("需要 UnityPy：pip install UnityPy") from exc

BUNDLES = {
    "excelgeneral": BUNDLE_EXCELGENERAL,
    "excelgeneral_l": BUNDLE_EXCELGENERAL_L,
}


def md5(data: bytes) -> str:
    return hashlib.md5(data).hexdigest()


def script_bytes(ta) -> bytes:
    s = ta.m_Script
    if isinstance(s, bytes):
        return s
    return s.encode("utf-8", "surrogateescape")


def load_bundle(path: Path):
    raw = path.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS not found: {path}")
    return UnityPy.load(raw[off:])


def extract_bundle(bundle_id: str, subdir: str, assets: Path, out_root: Path) -> int:
    src = assets / f"{bundle_id}.b"
    if not src.is_file():
        raise FileNotFoundError(f"找不到配置包（只读源）: {src}")
    dest = out_root / subdir
    assert_not_clean(dest)
    dest.mkdir(parents=True, exist_ok=True)
    env = load_bundle(src)
    n = 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        name = ta.m_Name
        data = script_bytes(ta)
        (dest / f"{name}.bytes").write_bytes(data)
        n += 1
    return n


def main() -> int:
    ap = argparse.ArgumentParser(description="从 crosscopy 提取配置到 cross/tools/_config_extract")
    ap.add_argument(
        "--assets",
        type=Path,
        default=CLEAN_ASSETS,
        help="只读 assets 目录（默认 crosscopy）",
    )
    ap.add_argument(
        "--out",
        type=Path,
        default=CONFIG_EXTRACT,
        help="写出目录（默认 cross/tools/_config_extract）",
    )
    args = ap.parse_args()
    assets = args.assets.resolve()
    out = args.out.resolve()
    assert_not_clean(out)
    # 源必须在 crosscopy 下（或用户显式传入时至少禁止写出到源）
    print(f"只读源 assets: {assets}")
    print(f"写出到:       {out}")
    print(f"CLEAN_ROOT:   {CLEAN_ROOT}")
    print(f"WORK_ROOT:    {WORK_ROOT}")
    if CLEAN_ROOT.resolve() not in assets.parents and assets != CLEAN_ASSETS.resolve():
        print("警告: --assets 不是默认 crosscopy，请确认未指向工作目录误写。")

    out.mkdir(parents=True, exist_ok=True)
    total = 0
    for subdir, bid in BUNDLES.items():
        print(f"extract {bid} -> {subdir}/ ...")
        n = extract_bundle(bid, subdir, assets, out)
        total += n
        print(f"  {n} files")

    # 快速校验关键表
    for name in (
        "pet_tbcommenemybaseconfig",
        "pet_tbpefectpetmatconfig",
        "pet_tbpefectpetskinconfig",
        "other_tbridepetskinconfig",
        "pet_tbpetmaxcresteffectconfig",
    ):
        p = out / "excelgeneral" / f"{name}.bytes"
        if p.is_file():
            print(f"  OK {name} size={p.stat().st_size} md5={md5(p.read_bytes())[:12]}")
        else:
            print(f"  MISSING {name}")

    print(f"done total={total}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
