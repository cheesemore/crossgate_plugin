# -*- coding: utf-8 -*-
"""手动同步官方文件（排除补丁相关），修复 cross 版本混合导致的黑屏。
只复制 crosscopy 中属于"官方更新"的差异文件，不覆盖补丁工具文件。
"""
import hashlib
import shutil
from pathlib import Path

CROSS = Path(r"E:\cross\魔力宝贝：序章")
COPY = Path(r"E:\crosscopy\魔力宝贝：序章")

# 待同步的官方文件（来自 diff --full-hash 的修改清单，排除补丁工具文件）
OFFICIAL = [
    "UnityPlayer.dll",
    "Update.exe",
    "baselib.dll",
    r"cg37_Data\Update.exe",
    r"cg37_Data\Plugins\x86_64\NPGameDLL.dll",
    r"cg37_Data\Plugins\x86_64\OpenCCUnityBridge.dll",
    r"cg37_Data\Plugins\x86_64\ProcessPick.dll",
    r"cg37_Data\Plugins\x86_64\exprtk_condition.dll",
    r"cg37_Data\Plugins\x86_64\sqlite3.dll",
    r"cg37_Data\StreamingAssets\filestructure.bin",
    r"cg37_Data\assets\2d2ac038a83870def5a37f56545b8f2b.b",
    r"cg37_Data\assets\version.bin",
    r"cg37_Data\level0",
    r"cg37_Data\sharedassets0.assets",
]

def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()

for rel in OFFICIAL:
    src = COPY / rel
    dst = CROSS / rel
    if not src.is_file():
        print(f"[SKIP] 源缺失: {rel}")
        continue
    if dst.is_file() and sha(src) == sha(dst):
        print(f"[OK]   已一致: {rel}")
        continue
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    print(f"[SYNC] {rel}  ({dst.stat().st_size:,}B)")
