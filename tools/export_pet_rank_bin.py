# -*- coding: utf-8 -*-
"""导出精简档位包 pet_rank.bin（含 BOSS 超模）。

格式 PRK1:
  magic[4]='PRK1'
  count int32 LE
  重复 count 次:
    nameLen uint16 LE
    name UTF-8
    vit,str,tgh,quick,magic 各 int16 LE
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from boss_stat_estimator import SLIM_CSV, export_slim_csv, load_rank_table

OUT_BIN = Path(__file__).resolve().parent / "pet_rank.bin"


def main() -> None:
    rows = load_rank_table()
    # 同名保留档位总和更高者（BOSS 优先）
    best = {}
    for r in rows:
        prev = best.get(r.name)
        if prev is None or sum(r.bases) > sum(prev.bases):
            best[r.name] = r
    uniq = sorted(best.values(), key=lambda x: (-sum(x.bases), x.name))

    # 同步精简 CSV（仅计算字段）
    export_slim_csv(uniq, SLIM_CSV)
    # 覆盖为无 temp/img 的更瘦 CSV
    lines = ["name,vit,str,tgh,quick,magic"]
    for r in uniq:
        name = r.name.replace(",", "，")
        b = r.bases
        lines.append(f"{name},{b[0]},{b[1]},{b[2]},{b[3]},{b[4]}")
    SLIM_CSV.write_text("\n".join(lines) + "\n", encoding="utf-8")

    buf = bytearray()
    buf += b"PRK1"
    buf += struct.pack("<i", len(uniq))
    for r in uniq:
        nb = r.name.encode("utf-8")
        if len(nb) > 65535:
            continue
        buf += struct.pack("<H", len(nb))
        buf += nb
        b = r.bases
        for v in b:
            iv = max(-32768, min(32767, int(v)))
            buf += struct.pack("<h", iv)

    OUT_BIN.write_bytes(buf)
    print(f"pets={len(uniq)} bin={OUT_BIN} bytes={len(buf)} csv={SLIM_CSV}")


if __name__ == "__main__":
    main()
