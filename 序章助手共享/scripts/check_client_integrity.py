#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查 cg37_Data / hotfixdata 是否混版（混版会在 il2cpp_init Brotli 阶段闪退）。"""
from __future__ import annotations

import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

HOTFIX_REL = Path("cg37_Data/assets/hotfixdata")
KEY_DLLS = (
    "cg37.exe",
    "UnityPlayer.dll",
    "GameAssembly.dll",
)


def main() -> int:
    root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
    hotfix_dir = root / HOTFIX_REL
    if not hotfix_dir.is_dir():
        print(f"找不到 {hotfix_dir}")
        return 1

    print(f"游戏目录: {root}")
    print()

    for name in KEY_DLLS:
        p = root / name
        if p.is_file():
            st = p.stat()
            print(f"{name:18} {st.st_size:>12,}  {datetime.fromtimestamp(st.st_mtime)}")
        else:
            print(f"{name:18} 缺失")

    print()
    rows: list[tuple[str, int, datetime]] = []
    for p in sorted(hotfix_dir.glob("*.bytes")):
        st = p.stat()
        rows.append((p.name, st.st_size, datetime.fromtimestamp(st.st_mtime)))

    print("hotfixdata/*.bytes:")
    for name, size, mtime in rows:
        print(f"  {name:32} {size:>10,}  {mtime.date()}")

    dates = Counter(mtime.date() for _, _, mtime in rows)
    sizes = {name: size for name, size, _ in rows}
    hotfix_size = sizes.get("hotfix.dll.bytes")

    print()
    if len(dates) > 1:
        print("⚠ 混版：hotfixdata 内文件日期不一致 → 极易 BrotliDecoder / il2cpp_init 闪退")
        for d, n in sorted(dates.items()):
            print(f"    {d}: {n} 个文件")
    else:
        print("hotfixdata 日期一致")

    if hotfix_size == 7_075_328:
        print(f"hotfix 体积: {hotfix_size:,}（2026-07-22 新版）")
    elif hotfix_size == 7_068_160:
        print(f"hotfix 体积: {hotfix_size:,}（2026-07-19 ~ 2026-07-22）")
    elif hotfix_size == 7_067_648:
        print(f"hotfix 体积: {hotfix_size:,}（2026-07-18 旧版）")
    elif hotfix_size == 7_055_360:
        print(f"hotfix 体积: {hotfix_size:,}（2026-07-15 版）")
    elif hotfix_size == 7_042_560:
        print(f"hotfix 体积: {hotfix_size:,}（2026-07 版）")
    elif hotfix_size == 6_991_360:
        print(f"hotfix 体积: {hotfix_size:,}（2026-06-27 版）")
    elif hotfix_size == 6_923_264:
        print(f"hotfix 体积: {hotfix_size:,}（2026-06-21~27 版）")
    elif hotfix_size:
        print(f"hotfix 体积: {hotfix_size:,}（未知版本）")

    bridge = hotfix_dir / "SeqChapterHelperBridge.dll.bytes"
    orig = hotfix_dir / "hotfix.dll.bytes.orig"
    if bridge.is_file():
        print("⚠ 存在 SeqChapterHelperBridge.dll.bytes（助手桥接）")
    if orig.is_file():
        print(f"存在 .orig 备份（{orig.stat().st_size:,} 字节）")
    else:
        print("无 .orig（正常，尚未初始化补丁工具）")

    print()
    print("【若闪退且未打补丁】请用启动器 Update.exe 完整「修复/更新」整个客户端，")
    print("不要只手动替换 cg37_Data 文件夹。修复后 hotfixdata 内所有 .bytes 应同一天。")
    print("修复前：多开器取消「启动前自动注入」。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
