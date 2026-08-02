# -*- coding: utf-8 -*-
"""进战形象配置编解码（类炉石 Deckstring：二进制 + Base64）。

文本形式：
  # 注释行可忽略
  CGAP1:<base64>

二进制 BAP1（小端）：
  magic[4] = 'BAP1'
  version u8 = 1
  enabled u8
  reserved u8 = 0
  固定 5 个槽位，每槽：
    pet_anim   i32   # 0=不配置（保持游戏原样）
    role_halo  i32   # 人物光环 Grano（动画Id）；0=不配置。旧版此槽为废弃 pet_halo，恒 0
    perfect    u8    # 0=不配置, 1=强制满档开；旧码 2=曾表示强制关（兼容）
    max_crest  i32   # 0=不配置
    char_anim  i32   # 0=不配置
    ride_skin  i32   # 0=不配置；游戏内应用为 RideSkin 配置 Id（兼容误填 Grano）
"""
from __future__ import annotations

import base64
import struct
from typing import Any

MAGIC = b"BAP1"
VERSION = 1
PREFIX = "CGAP1:"


def _i(v: Any, default: int = 0) -> int:
    try:
        n = int(v)
    except (TypeError, ValueError):
        return default
    return n


def normalize_slot(raw: dict | None, slot: int) -> dict:
    d = raw or {}
    pet_anim = _i(d.get("pet_anim", 0))
    # 新字段 role_halo；兼容误写的旧 key
    role_halo = _i(d.get("role_halo", d.get("pet_halo", 0)))
    max_crest = _i(d.get("max_crest", 0))
    char_anim = _i(d.get("char_anim", 0))
    ride_skin = _i(d.get("ride_skin", 0))
    if pet_anim < 0:
        pet_anim = 0
    if role_halo < 0:
        role_halo = 0
    if max_crest < 0:
        max_crest = 0
    if char_anim < 0:
        char_anim = 0
    if ride_skin < 0:
        ride_skin = 0

    if "perfect" not in d or d.get("perfect") is None:
        perfect_bin, perfect_json = 0, 0
    else:
        p = _i(d.get("perfect"), 0)
        if p == 1:
            perfect_bin, perfect_json = 1, 1
        else:
            perfect_bin, perfect_json = 0, 0

    return {
        "slot": slot,
        "pet_anim": pet_anim,
        "role_halo": role_halo,
        "perfect": perfect_json,
        "perfect_bin": perfect_bin,
        "max_crest": max_crest,
        "char_anim": char_anim,
        "ride_skin": ride_skin,
    }


def cfg_to_slots(cfg: dict) -> tuple[bool, list[dict]]:
    enabled = bool(cfg.get("enabled", False))
    by = {int(s.get("slot", 0)): s for s in (cfg.get("slots") or []) if isinstance(s, dict)}
    slots = [normalize_slot(by.get(i), i) for i in range(1, 6)]
    return enabled, slots


def encode_binary(enabled: bool, slots: list[dict]) -> bytes:
    if len(slots) != 5:
        raise ValueError("需要正好 5 个槽位")
    buf = bytearray()
    buf += MAGIC
    buf += bytes([VERSION, 1 if enabled else 0, 0])
    for i, s in enumerate(slots):
        ns = normalize_slot(s, i + 1)
        buf += struct.pack("<ii", ns["pet_anim"], ns["role_halo"])
        buf += bytes([ns["perfect_bin"] & 0xFF])
        buf += struct.pack("<iii", ns["max_crest"], ns["char_anim"], ns["ride_skin"])
    return bytes(buf)


def decode_binary(data: bytes) -> tuple[bool, list[dict]]:
    if len(data) < 7 + 5 * 21:
        raise ValueError(f"数据过短: {len(data)}")
    if data[:4] != MAGIC:
        raise ValueError(f"magic 不是 BAP1: {data[:4]!r}")
    ver = data[4]
    if ver != VERSION:
        raise ValueError(f"不支持版本 {ver}")
    enabled = data[5] != 0
    pos = 7
    slots: list[dict] = []
    for i in range(5):
        pet_anim, role_halo = struct.unpack_from("<ii", data, pos)
        pos += 8
        perfect_bin = data[pos]
        pos += 1
        max_crest, char_anim, ride_skin = struct.unpack_from("<iii", data, pos)
        pos += 12
        perfect = 1 if perfect_bin == 1 else 0
        slots.append(
            {
                "slot": i + 1,
                "pet_anim": pet_anim,
                "role_halo": role_halo if role_halo > 0 else 0,
                "perfect": perfect,
                "max_crest": max_crest,
                "char_anim": char_anim,
                "ride_skin": ride_skin,
                "perfect_bin": perfect_bin,
            }
        )
    return enabled, slots


def encode_code(
    cfg: dict,
    *,
    with_comment: bool = True,
    slot_comments: list[str] | None = None,
) -> str:
    """生成 CGAP1 代码。slot_comments 为各槽说明行（可带或不带前导 #）。"""
    enabled, slots = cfg_to_slots(cfg)
    b64 = base64.b64encode(encode_binary(enabled, slots)).decode("ascii")
    body = PREFIX + b64
    if not with_comment:
        return body
    lines = [
        "### 序章进战形象配置（完整：人物/光环/宠物/满档/满档光环/坐骑）",
        "# 说明：游戏内应用 人物形象/人物光环/坐骑/宠物形象/满档/满档光环。",
        "# 约定：0=不配置；满档勾选=1；人物光环=Grano；坐骑=配置 Id（可误填 Grano 会反查）。",
    ]
    for raw in slot_comments or []:
        s = str(raw).rstrip()
        if not s:
            continue
        lines.append(s if s.lstrip().startswith("#") else f"# {s}")
    lines.append(body)
    return "\n".join(lines) + "\n"


def extract_payload(text: str) -> str:
    """从粘贴文本中取出 CGAP1:xxx 或裸 base64。"""
    lines = []
    for line in (text or "").splitlines():
        s = line.strip()
        if not s or s.startswith("#"):
            continue
        if s.startswith("###"):
            continue
        lines.append(s)
    if not lines:
        raise ValueError("没有可解析内容")
    for s in lines:
        if s.upper().startswith("CGAP1:"):
            return s.split(":", 1)[1].strip()
    blob = "".join(lines)
    if blob.upper().startswith("CGAP1:"):
        return blob.split(":", 1)[1].strip()
    return blob


def decode_code(text: str) -> dict:
    payload = extract_payload(text)
    payload = "".join(payload.split())
    raw = base64.b64decode(payload)
    enabled, slots = decode_binary(raw)
    clean = []
    for s in slots:
        clean.append(
            {
                "slot": s["slot"],
                "pet_anim": s["pet_anim"],
                "role_halo": s["role_halo"],
                "perfect": s["perfect"],
                "max_crest": s["max_crest"],
                "char_anim": s["char_anim"],
                "ride_skin": s["ride_skin"],
            }
        )
    return {"enabled": enabled, "slots": clean}


def slots_to_cfg(enabled: bool, slots: list[dict]) -> dict:
    return {
        "enabled": enabled,
        "comment": "0=不配置。应用 char_anim/role_halo/ride_skin/pet_anim/perfect/max_crest。",
        "slots": [
            {
                "slot": i + 1,
                "pet_anim": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["pet_anim"],
                "role_halo": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["role_halo"],
                "perfect": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["perfect"],
                "max_crest": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["max_crest"],
                "char_anim": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["char_anim"],
                "ride_skin": normalize_slot(slots[i] if i < len(slots) else {}, i + 1)["ride_skin"],
            }
            for i in range(5)
        ],
    }


if __name__ == "__main__":
    sample = {
        "enabled": True,
        "slots": [
            {
                "slot": 1,
                "pet_anim": 101201,
                "role_halo": 170231,
                "perfect": 1,
                "max_crest": 1,
            },
            {"slot": 2},
            {"slot": 3},
            {"slot": 4},
            {"slot": 5},
        ],
    }
    code = encode_code(sample)
    print(code)
    back = decode_code(code)
    assert back["enabled"] is True
    assert back["slots"][0]["pet_anim"] == 101201
    assert back["slots"][0]["role_halo"] == 170231
    assert back["slots"][0]["perfect"] == 1
    print("ok", back)
