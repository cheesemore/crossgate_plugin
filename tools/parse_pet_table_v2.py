#!/usr/bin/env python3
"""
Parse pet_tbcommenemybaseconfig.

Decompiled TbCommEnemyBaseConfig: ReadSize() count, then per record EnemyCommBaseConfig.

Binary layout (verified against known records like 使魔 tempNo=32970):
  - ETNAME: 1-byte UTF-8 length + bytes  (matches ReadString when len < 128)
  - ETTEMPNO: uint16 big-endian          (NOT ReadInt varint — values like 0x80CA
    would decode as 202 as varint; file stores 80 CA as ushort BE)
  - remaining 52 fields: Luban unsigned varint (ReadInt)
"""
import json
import re
import struct
from pathlib import Path

import UnityPy

ROOT = Path(__file__).resolve().parents[1]
CONFIG_RAW = Path(__file__).resolve().parent / "configs" / "pet_tbcommenemybaseconfig"
CONFIG_BUNDLE = ROOT / "cross_Data" / "assets" / "4bd60e623f3f8796cb234b3f01f0c91a.b"

FIELD_NAMES = [
    "ETINITNUM",
    "ETLVUPPOINT",
    "ETTRIBE",
    "ETALBUMNO",
    "PetTypes",
    "ETFIXMAXHP",
    "ETBASEVITAL",
    "ETBASESTR",
    "ETBASETGH",
    "ETBASEQUICK",
    "ETBASEMAGIC",
    "ETMODLOYALTY",
    "ETGET",
    "ETRUN",
    "ETHITRATE",
    "ETAVOIDRATE",
    "ETEARTHAT",
    "ETWATERAT",
    "ETFIREAT",
    "ETWINDAT",
    "ETPOISON",
    "ETSLEEP",
    "ETSTONE",
    "ETDRUNK",
    "ETCONFUSION",
    "ETAMNESIA",
    "ETRARE",
    "ETLEVELUPRANDOMPATTERN",
    "ETCRITICAL",
    "ETCOUNTER",
    "ETSLOT",
    "ETIMGNUMBER",
    "ETMODEXP",
    "ETSIZE",
    "ETALBUMEXPLAINATION",
    "ETALBUMCANPETFLG",
    "ETPETSKILL1",
    "ETPETSKILL2",
    "ETPETSKILL3",
    "ETPETSKILL4",
    "ETPETSKILL5",
    "ETPETSKILL6",
    "ETPETSKILL7",
    "ETPETSKILL8",
    "ETPETSKILL9",
    "ETPETSKILL10",
    "ETOLDBASEVITAL",
    "ETOLDBASESTR",
    "ETOLDBASETGH",
    "ETOLDBASEQUICK",
    "ETOLDBASEMAGIC",
]

NAME_RE = re.compile(r"^[\u4e00-\u9fffA-Za-z0-9·\-（）()]+$")


def read_uint(data: bytes, pos: int) -> tuple[int, int]:
    h = data[pos]
    pos += 1
    if h < 0x80:
        return h, pos
    if h < 0xC0:
        return ((h & 0x3F) << 8) | data[pos], pos + 2
    if h < 0xE0:
        return ((h & 0x1F) << 16) | (data[pos] << 8) | data[pos + 1], pos + 3
    if h < 0xF0:
        return (
            ((h & 0x0F) << 24)
            | (data[pos] << 16)
            | (data[pos + 1] << 8)
            | data[pos + 2]
        ), pos + 4
    return (
        (data[pos] << 24)
        | (data[pos + 1] << 16)
        | (data[pos + 2] << 8)
        | data[pos + 3]
    ), pos + 5


def parse_record_at(data: bytes, start: int) -> tuple[dict, int] | None:
    pos = start
    if pos >= len(data) - 10:
        return None
    ln = data[pos]
    pos += 1
    if not (1 <= ln <= 40):
        return None
    try:
        name = data[pos : pos + ln].decode("utf-8")
    except UnicodeDecodeError:
        return None
    if not NAME_RE.fullmatch(name):
        return None
    pos += ln
    if pos + 2 > len(data):
        return None
    temp = struct.unpack_from(">H", data, pos)[0]
    if not (0 < temp < 65535):
        return None
    pos += 2
    fields = {"ETNAME": name, "ETTEMPNO": temp}
    try:
        for fn in FIELD_NAMES:
            v, pos = read_uint(data, pos)
            fields[fn] = v
    except IndexError:
        return None
    # 青春端/永恒端新宠物 PetTypes 可 >200，不再当作脏数据丢弃
    return fields, pos


def scan_all_records(data: bytes) -> list[dict]:
    by_temp: dict[int, dict] = {}
    for off in range(len(data) - 60):
        parsed = parse_record_at(data, off)
        if not parsed:
            continue
        rec, _ = parsed
        if rec["ETTEMPNO"] not in by_temp:
            by_temp[rec["ETTEMPNO"]] = rec
    return sorted(by_temp.values(), key=lambda r: r["ETTEMPNO"])


def try_sequential(data: bytes) -> tuple[list[dict], int] | None:
    pos = 0
    count, pos = read_uint(data, pos)
    records = []
    for _ in range(count):
        parsed = parse_record_at(data, pos)
        if not parsed:
            return None
        rec, pos = parsed
        records.append(rec)
    return records, pos


def load_config_bytes_for(config_bundle: Path, config_raw: Path | None = None) -> bytes:
    raw = config_bundle.read_bytes()
    off = raw.find(b"UnityFS")
    env = UnityPy.load(raw[off:])
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        if ta.m_Name == "pet_tbcommenemybaseconfig":
            s = ta.m_Script
            return s if isinstance(s, bytes) else s.encode("utf-8", "surrogateescape")
    if config_raw and config_raw.exists():
        return config_raw.read_bytes()
    raise RuntimeError(f"config not found in {config_bundle}")


def load_config_bytes() -> bytes:
    return load_config_bytes_for(CONFIG_BUNDLE, CONFIG_RAW)


def main():
    data = load_config_bytes()
    count, _ = read_uint(data, 0)
    print(f"header count (ReadSize) = {count}, file size = {len(data)}")

    seq = try_sequential(data)
    if seq and seq[1] == len(data):
        records = seq[0]
        print(f"sequential parse OK: {len(records)} records")
    else:
        end = seq[1] if seq else "n/a"
        records = scan_all_records(data)
        print(f"sequential parse failed (end={end}), scan found {len(records)} unique tempNo")

    targets = ["使魔", "魔龙德拉贡", "魔龙", "迷你蝙蝠", "哥布林", "水蓝鸟魔"]
    print("\n=== 目标宠物 (ETIMGNUMBER = UI/图鉴形象, 非战斗 AnimationId) ===")
    for name in targets:
        for r in records:
            if r["ETNAME"] == name:
                img = r["ETIMGNUMBER"]
                img_s = str(img) if img != 0xFFFFFFFF else "(无/0xFFFFFFFF)"
                print(
                    f"  {r['ETNAME']:10} tempNo={r['ETTEMPNO']:5} "
                    f"ETSLOT={r['ETSLOT']:2} ETIMGNUMBER={img_s} PetTypes={r['PetTypes']}"
                )

    print("\n=== 名称含「龙」且 ETIMGNUMBER 有效 (100000-199999) ===")
    for r in records:
        if "龙" in r["ETNAME"] and 100000 <= r["ETIMGNUMBER"] < 200000:
            print(f"  {r['ETNAME']:14} temp={r['ETTEMPNO']:5} ETIMG={r['ETIMGNUMBER']}")

    out = Path(__file__).parent / "pet_table_parsed.json"
    out.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nexported {len(records)} -> {out}")


if __name__ == "__main__":
    main()
