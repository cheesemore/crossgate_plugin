# -*- coding: utf-8 -*-
"""从 other_tbrolehaloconfig 提取人物光环（Id / Grano / Name）。

序章二进制在 Name/Memo 后还有资源名等附加字段，不能按纯 Luban Bean 顺序硬解。
Id 后跟压缩 int（常见 0xC0|hi, mid, lo → grano = hi<<16|mid<<8|lo），再 time=0 / icon=15 / Name。
游戏内 SetHalo / BattleChar.RoleHalo 使用的是 Grano（动画 Id），不是表 Id。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from seqchapter_paths import CONFIG_EXCELGENERAL, CONFIG_EXCELGENERAL_L  # noqa: E402

OUT = Path(__file__).resolve().parent / "role_halo.json"


def _utf8_strings(data: bytes) -> list[tuple[int, str]]:
    out: list[tuple[int, str]] = []
    i = 0
    while i < len(data):
        n = data[i]
        if 1 <= n < 0x80:
            chunk = data[i + 1 : i + 1 + n]
            if len(chunk) == n:
                try:
                    s = chunk.decode("utf-8")
                    if s and (
                        any("\u4e00" <= c <= "\u9fff" for c in s) or s.startswith("halo_")
                    ):
                        out.append((i, s))
                except UnicodeDecodeError:
                    pass
        i += 1
    return out


def _decode_compact_u24(data: bytes, pos: int) -> tuple[int, int] | None:
    """解码 0xC0|hi, mid, lo → 24-bit 无符号整数。成功返回 (value, end_pos)。"""
    if pos < 0 or pos + 3 > len(data):
        return None
    b0, b1, b2 = data[pos], data[pos + 1], data[pos + 2]
    if (b0 & 0xC0) != 0xC0:
        return None
    hi = b0 & 0x3F
    value = (hi << 16) | (b1 << 8) | b2
    return value, pos + 3


def load_role_halos(path: Path) -> list[dict]:
    data = path.read_bytes()
    strs = _utf8_strings(data)
    names = [
        (p, s)
        for p, s in strs
        if not s.startswith("halo_") and "获得" not in s and s != "暂无获得渠道"
    ]
    rows: list[dict] = []
    for np, name in names:
        # 名称前固定：… id, compact_grano, time=0, icon=15(0x0F), nameLen, name
        if np < 6 or data[np - 1] != 0x0F or data[np - 2] != 0x00:
            continue
        # compact int 占 3 字节，紧挨 time=0
        cpos = np - 5
        decoded = _decode_compact_u24(data, cpos)
        if decoded is None:
            continue
        grano, end = decoded
        if end != np - 2 or not (1000 <= grano <= 2_000_000):
            continue
        rid = int(data[cpos - 1]) if cpos > 0 else 0
        rows.append(
            {
                "id": rid if 1 <= rid <= 999 else len(rows) + 1,
                "grano": grano,
                "name": name,
                "memo": "暂无获得渠道",
            }
        )
    # 去重：同 grano 保留先出现
    seen: set[int] = set()
    uniq: list[dict] = []
    for r in rows:
        g = int(r["grano"] or 0)
        if g > 0 and g in seen:
            continue
        if g > 0:
            seen.add(g)
        uniq.append(r)
    uniq.sort(key=lambda r: int(r["id"] or 0))
    return uniq


def main() -> None:
    path = None
    for base in (CONFIG_EXCELGENERAL, CONFIG_EXCELGENERAL_L):
        cand = base / "other_tbrolehaloconfig.bytes"
        if cand.is_file() and cand.stat().st_size > 0:
            path = cand
            break
    if path is None:
        raise SystemExit("missing other_tbrolehaloconfig")
    rows = load_role_halos(path)
    OUT.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {OUT} n={len(rows)} from {path}")
    for r in rows:
        print(f"  id={r['id']} grano={r['grano']} name={r['name']}")


if __name__ == "__main__":
    main()
