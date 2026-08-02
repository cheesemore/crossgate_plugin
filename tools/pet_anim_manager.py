#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
宠物战斗形象管理：检索 / 备份 / 替换 / 恢复

战斗形象由服务器 AnimationId 决定；本地需同时改 animdata.bin 条目 + 对应 .b 动画包。

用法:
  python pet_anim_manager.py list              # 输出可换形表
  python pet_anim_manager.py scan-bundles     # 重建动画包索引缓存
  python pet_anim_manager.py backup-all       # 备份所有可换形象到 pet_anim_store/
  python pet_anim_manager.py swap --dst 使魔 --src 雪蕾洁
  python pet_anim_manager.py swap --dst-id 101201 --src-id 110599
  python pet_anim_manager.py restore --id 101201
  python pet_anim_manager.py restore-all      # 从 store 恢复全部已备份形象 + 全局 animdata
  python pet_anim_manager.py status
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import shutil
import struct
import sys
from collections.abc import Callable
from datetime import datetime, timezone
from pathlib import Path

import UnityPy

from game_profile import CROSS_HEAD_BLOCK, ETERNAL, LOCAL, PROFILES, GameProfile
from pet_bundle_animdata import bundle_has_animdata, extract_animdata_from_bundle, write_animdata_to_bundle
from skill_effect_tint import (
    TintParams,
    apply_tint_to_bundle_bytes,
    format_tint_summary,
    is_neutral_tint,
    sprite_ids_in_chunk,
)
from parse_pet_table_v2 import FIELD_NAMES, load_config_bytes_for, parse_record_at, read_uint
from pet_head_manager import (
    extract_head_png,
    has_pethead_sprite,
    restore_global_pethead,
    restore_pet_head,
    swap_pet_head,
)

LogFn = Callable[[str], None]

# 兼容旧代码 / GUI
ROOT = LOCAL.root
ASSETS = LOCAL.assets
ANIMDATA_DIR = LOCAL.animdata_dir
ANIMDATA_INFO = LOCAL.animdata_info
ANIMDATA_BIN = LOCAL.animdata_bin
STORE = LOCAL.store
CACHE_DIR = LOCAL.cache_dir
BUNDLE_MAP_FILE = LOCAL.bundle_map_file
PET_INDEX_FILE = LOCAL.pet_index_file
MANIFEST_FILE = LOCAL.manifest_file
TABLE_MD = LOCAL.table_md
TABLE_CSV = LOCAL.table_csv
GLOBAL_DIR = LOCAL.global_dir
APPEARANCES_DIR = LOCAL.appearances_dir


# ---------------------------------------------------------------------------
# animdata helpers
# ---------------------------------------------------------------------------

def load_anim_index(info: bytes) -> list[tuple[int, int]]:
    count = struct.unpack_from("<i", info, 0)[0]
    pos = 4
    out = []
    for _ in range(count):
        aid, off = struct.unpack_from("<II", info, pos)
        pos += 8
        out.append((aid, off))
    return out


def extract_chunks(data: bytes, index: list[tuple[int, int]]) -> dict[int, bytes]:
    ordered = sorted(index, key=lambda x: x[1])
    chunks = {}
    for i, (aid, off) in enumerate(ordered):
        end = ordered[i + 1][1] if i + 1 < len(ordered) else len(data)
        chunks[aid] = data[off:end]
    return chunks


def rebuild_animdata(chunks: dict[int, bytes], index: list[tuple[int, int]]) -> tuple[bytes, bytes]:
    ordered_aids = [aid for aid, _ in sorted(index, key=lambda x: x[1])]
    new_data = bytearray()
    new_index = []
    for aid in ordered_aids:
        blob = chunks[aid]
        new_index.append((aid, len(new_data)))
        new_data.extend(blob)
    info = bytearray()
    info.extend(struct.pack("<i", len(new_index)))
    for aid, off in new_index:
        info.extend(struct.pack("<II", aid, off))
    return bytes(info), bytes(new_data)


def profile_is_monolithic(profile: GameProfile) -> bool:
    return profile.animdata_mode == "monolithic"


def read_anim_store(profile: GameProfile = LOCAL) -> tuple[dict[int, bytes], list[tuple[int, int]]]:
    if not profile_is_monolithic(profile):
        return {}, []
    info = profile.animdata_info.read_bytes()
    data = profile.animdata_bin.read_bytes()
    index = load_anim_index(info)
    return extract_chunks(data, index), index


def write_anim_store(
    chunks: dict[int, bytes],
    index: list[tuple[int, int]],
    profile: GameProfile = LOCAL,
) -> None:
    if not profile_is_monolithic(profile):
        return
    info, data = rebuild_animdata(chunks, index)
    profile.animdata_info.write_bytes(info)
    profile.animdata_bin.write_bytes(data)


def get_anim_chunk(anim_id: int, bundle_map: dict[int, str], profile: GameProfile) -> bytes:
    bp = bundle_path(anim_id, bundle_map, profile)
    if not bp:
        raise RuntimeError(f"无动画包 ID {anim_id}")
    if profile_is_monolithic(profile):
        chunks, _ = read_anim_store(profile)
        if anim_id not in chunks:
            raise RuntimeError(f"animdata 无 ID {anim_id}")
        return chunks[anim_id]
    return extract_animdata_from_bundle(bp)


def set_anim_chunk(
    anim_id: int,
    chunk: bytes,
    bundle_map: dict[int, str],
    profile: GameProfile,
    log: LogFn = print,
) -> None:
    if profile_is_monolithic(profile):
        chunks, index = read_anim_store(profile)
        old = len(chunks.get(anim_id, b""))
        log(f"  animdata {anim_id} ({old}B) <- chunk ({len(chunk)}B)")
        chunks[anim_id] = chunk
        write_anim_store(chunks, index, profile)
        return
    bp = bundle_path(anim_id, bundle_map, profile)
    if not bp:
        raise RuntimeError(f"无动画包 ID {anim_id}")
    log(f"  包内 animdata {anim_id} ({len(chunk)}B) -> {bp.stem[:12]}...")
    write_animdata_to_bundle(bp, chunk)


# ---------------------------------------------------------------------------
# bundle map
# ---------------------------------------------------------------------------

def scan_bundle_map(profile: GameProfile = LOCAL, force: bool = False) -> dict[int, str]:
    profile.cache_dir.mkdir(parents=True, exist_ok=True)
    if profile.bundle_map_file.exists() and not force:
        return {
            int(k): v
            for k, v in json.loads(profile.bundle_map_file.read_text(encoding="utf-8")).items()
        }

    print(f"扫描动画包索引 [{profile.label}]（首次较慢）...")
    mapping: dict[int, str] = {}
    files = list(profile.assets.glob("*.b"))
    for i, p in enumerate(files):
        if i and i % 200 == 0:
            print(f"  {i}/{len(files)}")
        try:
            raw = p.read_bytes()
            off = raw.find(b"UnityFS")
            if off < 0:
                continue
            env = UnityPy.load(raw[off:])
            for obj in env.objects:
                if obj.type.name != "AssetBundle":
                    continue
                name = obj.read().m_Name
                for part in name.replace("/", "_").split("_"):
                    if part.isdigit() and len(part) >= 5:
                        mapping[int(part)] = p.stem
                        break
        except Exception:
            continue

    profile.bundle_map_file.write_text(
        json.dumps(mapping, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"  索引 {len(mapping)} 个动画 ID -> {profile.bundle_map_file}")
    return mapping


def bundle_path(
    anim_id: int,
    bundle_map: dict[int, str],
    profile: GameProfile = LOCAL,
) -> Path | None:
    h = bundle_map.get(anim_id)
    if not h:
        return None
    p = profile.assets / f"{h}.b"
    return p if p.exists() else None


# ---------------------------------------------------------------------------
# pet index
# ---------------------------------------------------------------------------

def resolve_battle_anim_id(pet: dict) -> int:
    """游戏战斗实际使用的形象 ID（配置表 ETSLOT），与扫描 anim_id 可能不同。"""
    slot = int(pet.get("ETSLOT") or 0)
    anim_id = int(pet.get("anim_id") or 0)
    if 100000 <= slot < 200000:
        return slot
    return anim_id


class ManifestNameIndex:
    """工程备份 manifest 中「名字 → 形象 ID」索引。"""

    def __init__(self, profile: GameProfile) -> None:
        self.by_name: dict[str, list[int]] = {}
        self.names_by_aid: dict[int, list[str]] = {}
        manifest = load_manifest(profile)
        for aid_str, meta in manifest.get("appearances", {}).items():
            aid = int(aid_str)
            names = [
                n
                for n in (meta.get("names") or [])
                if n and not str(n).startswith("形象_")
            ]
            if not names:
                names = [f"形象_{aid}"]
            self.names_by_aid[aid] = names
            for n in names:
                self.by_name.setdefault(n, [])
                if aid not in self.by_name[n]:
                    self.by_name[n].append(aid)

    def is_exclusive(self, anim_id: int, name: str) -> bool:
        names = self.names_by_aid.get(anim_id, [])
        return len(names) == 1 and names[0] == name


def resolve_export_anim_id(
    pet: dict,
    profile: GameProfile,
    index: ManifestNameIndex | None = None,
) -> int:
    """导出用形象 ID：永恒端用 manifest 校正共享包误绑，否则与 GUI 预览一致用扫描 ID。"""
    scan_id = int(pet.get("anim_id") or 0)
    if profile.key != "eternal":
        return scan_id
    if index is None:
        index = ManifestNameIndex(profile)
    name = pet["name"]
    hits = index.by_name.get(name, [])
    if not hits:
        return scan_id

    exclusive = [aid for aid in hits if index.is_exclusive(aid, name)]
    # 如烛九阴：扫描误绑多名字共用包 101913，manifest 有专属 120061
    if len(exclusive) == 1 and exclusive[0] != scan_id:
        if scan_id not in hits:
            return exclusive[0]
        scan_names = index.names_by_aid.get(scan_id, [])
        if len(scan_names) >= 4:
            return exclusive[0]

    if scan_id in hits:
        return scan_id
    if len(exclusive) == 1:
        return exclusive[0]
    if len(hits) == 1:
        return hits[0]
    return scan_id


def build_anim_id_name_map(profile: GameProfile) -> dict[int, list[str]]:
    """形象 ID → 关联名字（配置表 + manifest，不去重宠物名）。"""
    names: dict[int, list[str]] = {}

    def add(aid: int, name: str) -> None:
        if not aid or not name:
            return
        bucket = names.setdefault(aid, [])
        if name not in bucket:
            bucket.append(name)

    manifest = load_manifest(profile)
    for aid_str, meta in manifest.get("appearances", {}).items():
        aid = int(aid_str)
        for n in meta.get("names") or []:
            add(aid, str(n))

    try:
        for r in build_pet_index(profile, force=False):
            add(int(r.get("anim_id") or 0), str(r.get("name") or ""))
    except Exception:
        pass

    return names


def pick_appearance_display_name(
    anim_id: int, name_map: dict[int, list[str]]
) -> tuple[str, list[str], bool]:
    """返回 (导出用显示名, 全部别名, 是否有真实宠物名)。"""
    aliases = list(name_map.get(anim_id, []))
    real = [n for n in aliases if n and not str(n).startswith("形象_")]
    if len(real) == 1:
        return real[0], aliases, True
    if real:
        return sorted(real, key=lambda n: (len(n), n))[0], aliases, True
    return f"形象_{anim_id}", aliases, False


def build_image_export_catalog(profile: GameProfile) -> list[dict]:
    """按动画包/形象 ID 列出所有可尝试导出的战斗形象（含无宠物名）。"""
    bundle_map = scan_bundle_map(profile, force=False)
    name_map = build_anim_id_name_map(profile)
    out: list[dict] = []
    for aid in sorted(load_valid_anim_ids(bundle_map, profile)):
        display, aliases, has_name = pick_appearance_display_name(aid, name_map)
        out.append(
            {
                "anim_id": aid,
                "name": display,
                "aliases": aliases,
                "has_pet_name": has_name,
            }
        )
    return out
    s = 0
    img = rec.get("anim_id", 0)
    if 100000 <= img < 200000:
        s += 100
    if rec.get("id_source") == "ETIMGNUMBER":
        s += 40
    elif str(rec.get("id_source", "")).startswith("scan_101"):
        s += 25
    pt = rec.get("PetTypes", 0)
    if 0 < pt <= 512 or pt == 0xFFFFFFFF:
        s += 20
    slot = rec.get("ETSLOT", 0)
    if 0 < slot <= 20:
        s += 10
    return s


def scan_config_anim_candidates(data: bytes, offset: int, window: int = 256) -> list[int]:
    """从记录二进制扫描 100000-199999 的候选形象 ID（窗口尽量放宽）。"""
    ln = data[offset]
    pos = offset + 1 + ln + 2
    end = min(pos + window, len(data))
    chunk = data[pos:end]
    found: list[int] = []
    seen: set[int] = set()
    for scan in range(max(0, len(chunk) - 5)):
        try:
            v, _ = read_uint(chunk, scan)
        except IndexError:
            continue
        if 100000 <= v < 200000 and v not in seen:
            seen.add(v)
            found.append(v)
    return found


def _battle_ok_cache_path(profile: GameProfile) -> Path:
    return profile.cache_dir / "battle_asset_ok.json"


def load_battle_ok_cache(profile: GameProfile) -> dict[int, bool]:
    path = _battle_ok_cache_path(profile)
    if not path.exists():
        return {}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        return {int(k): bool(v) for k, v in raw.items()}
    except (json.JSONDecodeError, ValueError, TypeError):
        return {}


def save_battle_ok_cache(profile: GameProfile, cache: dict[int, bool]) -> None:
    profile.cache_dir.mkdir(parents=True, exist_ok=True)
    _battle_ok_cache_path(profile).write_text(
        json.dumps({str(k): v for k, v in sorted(cache.items())}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def can_read_battle_assets(
    anim_id: int,
    bundle_map: dict[int, str],
    profile: GameProfile,
    *,
    bundle_file: Path | None = None,
) -> bool:
    """校验能否从本地动画包读到战斗帧（animdata + 至少一帧 Sprite）。"""
    bp = bundle_file or bundle_path(anim_id, bundle_map, profile)
    if not bp or not bp.exists() or not bundle_has_animdata(bp):
        return False
    try:
        from pet_preview import extract_sprite, parse_animdata, pick_stand_clip, pick_walk_clip

        raw = bp.read_bytes()
        off = raw.find(b"UnityFS")
        if off < 0:
            return False
        env = UnityPy.load(raw[off:])
        chunk = extract_animdata_from_bundle(bp)
        clips, _ = parse_animdata(chunk)
        clip = pick_stand_clip(clips) or pick_walk_clip(clips)
        if not clip or not clip.frame_sprites:
            return False
        sprite_names = {
            obj.read().m_Name
            for obj in env.objects
            if obj.type.name == "Sprite"
        }
        for sid in clip.frame_sprites:
            if sid > 0 and str(sid) in sprite_names and extract_sprite(env, sid) is not None:
                return True
        return False
    except Exception:
        return False


def battle_assets_readable(
    anim_id: int,
    bundle_map: dict[int, str],
    profile: GameProfile,
    cache: dict[int, bool],
) -> bool:
    if anim_id in cache:
        return cache[anim_id]
    ok = can_read_battle_assets(anim_id, bundle_map, profile)
    cache[anim_id] = ok
    return ok


def resolve_battle_readable(
    anim_id: int,
    bp: Path | None,
    bundle_map: dict[int, str],
    profile: GameProfile,
    cache: dict[int, bool],
    *,
    validate_battle: bool,
) -> bool:
    if anim_id in cache:
        return cache[anim_id]
    if not validate_battle:
        return bool(bp and bp.exists())
    return battle_assets_readable(anim_id, bundle_map, profile, cache)


def pick_anim_id(
    etimg: int,
    candidates: list[int],
    valid_ids: set[int],
    bundle_map: dict[int, str] | None = None,
) -> tuple[int, str]:
    """选定形象 ID 及来源说明。"""
    bundle_map = bundle_map or {}

    ordered: list[int] = []
    seen: set[int] = set()

    def add(c: int) -> None:
        if 100000 <= c < 200000 and c not in seen:
            seen.add(c)
            ordered.append(c)

    if 100000 <= etimg < 200000:
        add(etimg)
    for c in candidates:
        add(c)

    if not ordered:
        if 100000 <= etimg < 200000 and etimg in bundle_map:
            return etimg, "ETIMGNUMBER_fallback"
        return 0, ""

    def rank(c: int) -> tuple[int, int, int, int, int]:
        in_valid = 1 if c in valid_ids else 0
        in_map = 1 if c in bundle_map else 0
        seg = 0
        if 101000 <= c < 102000:
            seg = 3
        elif 110000 <= c < 120000:
            seg = 2
        elif 100000 <= c < 101000:
            seg = 1
        cfg = 1 if c == etimg and 100000 <= etimg < 200000 else 0
        return (in_valid, in_map, cfg, seg, c)

    best = max(ordered, key=rank)
    if best == etimg and 100000 <= etimg < 200000:
        src = "ETIMGNUMBER" if etimg in valid_ids else "ETIMGNUMBER_fallback"
    elif 101000 <= best < 102000:
        src = "scan_101xxx"
    elif 110000 <= best < 120000:
        src = "scan_110xxx"
    else:
        src = "scan_other"
    return best, src


def load_valid_anim_ids(bundle_map: dict[int, str], profile: GameProfile = LOCAL) -> set[int]:
    if not profile_is_monolithic(profile):
        valid: set[int] = set()
        for aid, _h in bundle_map.items():
            bp = bundle_path(aid, bundle_map, profile)
            if bp and bundle_has_animdata(bp):
                valid.add(aid)
        return valid
    ref_info = profile.global_dir / "animdatainfo.bin"
    ref_data = profile.global_dir / "animdata.bin"
    if ref_info.exists() and ref_data.exists():
        info, data = ref_info.read_bytes(), ref_data.read_bytes()
    else:
        info, data = profile.animdata_info.read_bytes(), profile.animdata_bin.read_bytes()
    chunks = extract_chunks(data, load_anim_index(info))
    return {aid for aid in chunks if aid in bundle_map}


def bundle_plain_id_count(
    anim_id: int,
    bundle_map: dict[int, str],
    profile: GameProfile = LOCAL,
) -> int:
    h = bundle_map.get(anim_id)
    if not h:
        return 0
    p = profile.assets / f"{h}.b"
    if not p.exists():
        return 0
    raw = p.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        return 0
    return raw[off:].count(str(anim_id).encode())


def chunk_header_tag(chunk: bytes) -> str:
    if len(chunk) < 2:
        return "?"
    if chunk[0] == 9:
        return "09"
    if chunk[0] == 8:
        return "08"
    return f"{chunk[0]:02x}"


def id_segment(anim_id: int) -> str:
    if 101000 <= anim_id < 102000:
        return "101xxx"
    if 110000 <= anim_id < 120000:
        return "110xxx"
    if 100000 <= anim_id < 101000:
        return "100xxx"
    if 100000 <= anim_id < 200000:
        return "其他"
    return "-"


def classify_swap_tier(
    anim_id: int,
    chunk: bytes,
    plain_id: int,
    id_source: str,
    in_anim: bool,
    has_bundle: bool,
) -> str:
    if not anim_id or not (in_anim and has_bundle):
        return "无资源" if anim_id else "无形象ID"
    seg = id_segment(anim_id)
    hdr = chunk_header_tag(chunk)
    cfg_ok = id_source == "ETIMGNUMBER"
    scan_ok = id_source.startswith("scan_101")

    # 与使魔(101201)同类：钢铁领主、水蓝鸟魔等（101xxx + 09头 + 明文ID）
    if seg == "101xxx" and plain_id >= 1 and hdr == "09":
        return "推荐"
    if seg == "101xxx" and plain_id >= 1:
        return "可换"
    if seg == "101xxx" and (cfg_ok or scan_ok):
        return "可换"
    if seg == "101xxx":
        return "可换"
    if seg == "110xxx":
        return "谨慎"
    if seg == "100xxx":
        return "谨慎"
    return "可换"


def build_pet_index(profile: GameProfile = LOCAL, force: bool = False) -> list[dict]:
    profile.cache_dir.mkdir(parents=True, exist_ok=True)
    if profile.pet_index_file.exists() and not force:
        return json.loads(profile.pet_index_file.read_text(encoding="utf-8"))

    bundle_map = scan_bundle_map(profile)
    valid_ids = load_valid_anim_ids(bundle_map, profile)
    data = load_config_bytes_for(profile.config_bundle)
    by_key: dict[tuple[str, int], dict] = {}
    for off in range(len(data) - 60):
        parsed = parse_record_at(data, off)
        if not parsed:
            continue
        rec, _ = parsed
        candidates = scan_config_anim_candidates(data, off)
        anim_id, id_source = pick_anim_id(rec["ETIMGNUMBER"], candidates, valid_ids, bundle_map)
        entry = {
            "name": rec["ETNAME"],
            "tempNo": rec["ETTEMPNO"],
            "anim_id": anim_id,
            "ETIMGNUMBER": rec["ETIMGNUMBER"],
            "id_source": id_source,
            "PetTypes": rec["PetTypes"],
            "ETSLOT": rec["ETSLOT"],
            "offset": off,
        }
        key = (rec["ETNAME"], rec["ETTEMPNO"])
        old = by_key.get(key)
        if not old or score_record(entry) > score_record(old):
            by_key[key] = entry

    rows = sorted(by_key.values(), key=lambda r: (r["name"], r["tempNo"]))
    profile.pet_index_file.write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    return rows


def load_reference_chunks(profile: GameProfile = LOCAL) -> dict[int, bytes]:
    if not profile_is_monolithic(profile):
        return {}
    ref_info = profile.global_dir / "animdatainfo.bin"
    ref_data = profile.global_dir / "animdata.bin"
    if ref_info.exists() and ref_data.exists():
        info, data = ref_info.read_bytes(), ref_data.read_bytes()
    else:
        info, data = profile.animdata_info.read_bytes(), profile.animdata_bin.read_bytes()
    return extract_chunks(data, load_anim_index(info))


def _enriched_list_cache(profile: GameProfile) -> Path:
    return profile.cache_dir / "pet_list_enriched.json"


def enrich_with_resources(
    rows: list[dict],
    bundle_map: dict[int, str],
    profile: GameProfile = LOCAL,
    *,
    validate_battle: bool = False,
) -> list[dict]:
    chunks = load_reference_chunks(profile)
    battle_cache = load_battle_ok_cache(profile)
    out = []
    for r in rows:
        aid = r["anim_id"]
        if not aid:
            r["status"] = "无形象ID"
            r["tier"] = "无形象ID"
            r["battle_readable"] = False
            out.append(r)
            continue
        bp = bundle_path(aid, bundle_map, profile)
        has_bundle = bp is not None
        chunk = b""
        in_anim = False
        if profile_is_monolithic(profile):
            chunk = chunks.get(aid, b"")
            in_anim = aid in chunks
        elif has_bundle and validate_battle and bp:
            try:
                chunk = extract_animdata_from_bundle(bp)
                in_anim = bool(chunk)
            except Exception:
                pass
        elif has_bundle:
            in_anim = True
        plain = 0 if not validate_battle else bundle_plain_id_count(aid, bundle_map, profile)
        battle_ok = resolve_battle_readable(
            aid, bp, bundle_map, profile, battle_cache, validate_battle=validate_battle
        )
        r["chunk_size"] = len(chunk)
        r["chunk_hdr"] = chunk_header_tag(chunk) if chunk else ("?" if has_bundle else "-")
        r["id_segment"] = id_segment(aid)
        r["plain_id"] = plain
        r["bundle_hash"] = bp.stem if bp else ""
        r["battle_readable"] = battle_ok
        r["tier"] = classify_swap_tier(
            aid, chunk, plain, r.get("id_source", ""), in_anim, has_bundle
        )
        if battle_ok:
            r["status"] = "可换"
        elif in_anim and has_bundle:
            r["status"] = "可换"
        elif in_anim:
            r["status"] = "仅animdata"
        elif has_bundle:
            r["status"] = "仅包"
        else:
            r["status"] = "无资源"
        out.append(r)
    if validate_battle:
        save_battle_ok_cache(profile, battle_cache)
    return out


def pick_by_name(rows: list[dict], name: str, require_swappable: bool = True) -> dict:
    hits = [r for r in rows if r["name"] == name]
    if not hits:
        raise SystemExit(f"未找到宠物: {name}")
    if require_swappable:
        hits = [r for r in hits if r.get("status") == "可换"]
        if not hits:
            raise SystemExit(f"宠物 {name} 没有可换形资源")
    return max(hits, key=score_record)


# ---------------------------------------------------------------------------
# manifest / backup
# ---------------------------------------------------------------------------

def load_manifest(profile: GameProfile = LOCAL) -> dict:
    if profile.manifest_file.exists():
        return json.loads(profile.manifest_file.read_text(encoding="utf-8"))
    return {
        "version": 1,
        "profile": profile.key,
        "label": profile.label,
        "created": None,
        "appearances": {},
        "swaps": [],
    }


def save_manifest(m: dict, profile: GameProfile = LOCAL) -> None:
    profile.manifest_file.parent.mkdir(parents=True, exist_ok=True)
    profile.manifest_file.write_text(json.dumps(m, ensure_ascii=False, indent=2), encoding="utf-8")


def backup_global_animdata(profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    profile.global_dir.mkdir(parents=True, exist_ok=True)
    if profile_is_monolithic(profile):
        for src, name in (
            (profile.animdata_info, "animdatainfo.bin"),
            (profile.animdata_bin, "animdata.bin"),
        ):
            dst = profile.global_dir / name
            if not dst.exists() or dst.stat().st_size != src.stat().st_size:
                shutil.copy2(src, dst)
                log(f"  全局备份 [{profile.label}] {name}")
    else:
        log(f"  [{profile.label}] 无 animdata.bin，跳过全局 animdata 备份")


def backup_appearance(
    anim_id: int,
    names: list[str],
    bundle_map: dict[int, str],
    manifest: dict,
    profile: GameProfile = LOCAL,
    log: LogFn = print,
) -> None:
    bp = bundle_path(anim_id, bundle_map, profile)
    if not bp:
        raise RuntimeError(f"无动画包 ID {anim_id}")
    chunk = get_anim_chunk(anim_id, bundle_map, profile)

    dest = profile.appearances_dir / str(anim_id)
    dest.mkdir(parents=True, exist_ok=True)

    (dest / "animdata_chunk.bin").write_bytes(chunk)
    shutil.copy2(bp, dest / "bundle.b")

    raw = bp.read_bytes()
    prefix_off = raw.find(b"UnityFS")
    (dest / "bundle_prefix.bin").write_bytes(raw[:prefix_off])

    meta = {
        "anim_id": anim_id,
        "names": sorted(set(names)),
        "bundle_hash": bp.stem,
        "chunk_size": len(chunk),
        "backed_up_at": datetime.now(timezone.utc).isoformat(),
    }
    (dest / "meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    if has_pethead_sprite(anim_id, profile):
        extract_head_png(anim_id, profile, log=log)
        meta["has_head"] = True
        (dest / "meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

    manifest["appearances"][str(anim_id)] = meta
    if not manifest.get("created"):
        manifest["created"] = meta["backed_up_at"]
    save_manifest(manifest, profile)
    log(f"  备份形象 [{profile.label}] {anim_id} ({', '.join(meta['names'][:3])})")


def collect_backup_targets(
    rows: list[dict],
    bundle_map: dict[int, str],
    profile: GameProfile,
    log: LogFn = print,
) -> dict[int, list[str]]:
    """汇总需备份的形象 ID。bundle_only 客户端额外纳入全部本地有效动画包。"""
    targets: dict[int, list[str]] = {}
    names_by_id: dict[int, list[str]] = {}

    for r in rows:
        aid = r.get("anim_id") or 0
        if not aid:
            continue
        names_by_id.setdefault(aid, []).append(r["name"])
        if r.get("status") == "可换":
            targets.setdefault(aid, [])
            if r["name"] not in targets[aid]:
                targets[aid].append(r["name"])

    pet_linked = len(targets)
    if profile.animdata_mode == "bundle_only":
        valid = load_valid_anim_ids(bundle_map, profile)
        for aid in valid:
            names = list(dict.fromkeys(names_by_id.get(aid, [f"形象_{aid}"])))
            if aid not in targets:
                targets[aid] = names
            else:
                for n in names:
                    if n not in targets[aid]:
                        targets[aid].append(n)
        log(
            f"  [bundle_only] 备份目标: 宠物关联 {pet_linked} + 本地有效包 {len(valid)} "
            f"-> 合计 {len(targets)} 个形象 ID"
        )
    return targets


def cmd_backup_all(args: argparse.Namespace, profile: GameProfile = LOCAL) -> None:
    bundle_map = scan_bundle_map(profile, force=args.force_scan)
    rows = enrich_with_resources(
        build_pet_index(profile, force=args.force_scan), bundle_map, profile, validate_battle=True
    )
    swappable_ids = collect_backup_targets(rows, bundle_map, profile)

    print(f"=== 备份全局 animdata [{profile.label}] ===")
    backup_global_animdata(profile)

    manifest = load_manifest(profile)
    print(f"=== 备份 {len(swappable_ids)} 个形象 ===")
    for aid in sorted(swappable_ids):
        if str(aid) in manifest["appearances"] and not args.force:
            continue
        backup_appearance(aid, swappable_ids[aid], bundle_map, manifest, profile)

    save_manifest(manifest, profile)
    print(f"\n完成 -> {profile.store}")


def _pick_best_by_name(rows: list[dict]) -> dict[str, dict]:
    tier_rank = {"推荐": 4, "可换": 3, "谨慎": 2, "无资源": 1, "无形象ID": 0, "仅animdata": 1, "仅包": 1}
    by_name: dict[str, dict] = {}
    for r in rows:
        old = by_name.get(r["name"])
        tr = tier_rank.get(r.get("tier", ""), 0)
        old_tr = tier_rank.get(old.get("tier", ""), 0) if old else -1
        if not old or tr > old_tr or (tr == old_tr and score_record(r) > score_record(old)):
            by_name[r["name"]] = r
    return by_name


def _table_row(r: dict) -> str:
    return (
        f"| {r['name']} | {r['anim_id']} | {r['tempNo']} | {r['PetTypes']} | "
        f"{r.get('tier','')} | {r.get('id_segment','')} | {r.get('chunk_hdr','')} | "
        f"{r.get('plain_id',0)} | {r.get('id_source','')} | {r.get('chunk_size',0)} |"
    )


def cmd_list(args: argparse.Namespace, profile: GameProfile = LOCAL) -> None:
    bundle_map = scan_bundle_map(profile, force=args.force)
    rows = enrich_with_resources(
        build_pet_index(profile, force=args.force), bundle_map, profile, validate_battle=args.force
    )
    by_name = _pick_best_by_name(rows)

    swappable = [r for r in by_name.values() if r.get("status") == "可换"]
    recommended = sorted(
        [r for r in swappable if r.get("tier") == "推荐"],
        key=lambda x: (x["anim_id"], x["name"]),
    )
    normal = sorted(
        [r for r in swappable if r.get("tier") == "可换"],
        key=lambda x: (x["anim_id"], x["name"]),
    )
    caution = sorted(
        [r for r in swappable if r.get("tier") == "谨慎"],
        key=lambda x: (x["anim_id"], x["name"]),
    )
    show = sorted(by_name.values(), key=lambda x: (x.get("anim_id") or 999999, x["name"]))
    if not args.all:
        show = swappable

    hdr = (
        "| 名字 | 形象ID | tempNo | 种类 | 分级 | ID段 | anim头 | 包内明文ID | ID来源 | chunk |"
    )
    sep = "|------|--------|--------|------|------|------|--------|------------|--------|-------|"

    lines_md = [
        "# 宠物换形对照表（分级）",
        "",
        f"生成时间: {datetime.now().strftime('%Y-%m-%d %H:%M')}",
        f"可换形: **{len(swappable)}** 个名字 | 推荐: **{len(recommended)}** | 谨慎: **{len(caution)}**",
        "",
        "## 换形共同点（已验证）",
        "",
        "**推荐（使魔槽优先试，如钢铁领主）** 需同时满足：",
        "",
        "- 形象 ID 在 **101xxx**（与使魔 `101201` 同系列）",
        "- animdata 块头 **`09`**（与使魔原版同结构族）",
        "- 动画包内能找到 **明文形象 ID**（替换脚本可改字符串）",
        "- 配置表 `ETIMGNUMBER` 或二进制扫描 **101xxx** 与资源 ID 一致",
        "",
        "**谨慎（如雪蕾洁/雪儿波波）** 典型特征：",
        "",
        "- 形象 ID 在 **110xxx**",
        "- animdata 块头 **`08`**，帧表更短，与使魔结构不同",
        "- 包内 **无明文 ID**，只能靠 hash 替换，易错位",
        "- 配置表字段常无效，ID 靠扫描推断",
        "",
        "> 换形目标：使魔出战 AnimationId = **101201** → 改 `101201` 的 animdata + `1a827fab...b` 包。",
        "",
        f"## 推荐可换（{len(recommended)}）",
        "",
        hdr,
        sep,
    ]
    for r in recommended:
        lines_md.append(_table_row(r))

    lines_md += ["", f"## 可换（{len(normal)}）", "", hdr, sep]
    for r in normal:
        lines_md.append(_table_row(r))

    lines_md += ["", f"## 谨慎 / 110xxx（{len(caution)}）", "", hdr, sep]
    for r in caution:
        lines_md.append(_table_row(r))

    lines_md += [
        "",
        "## 使魔槽常用参考",
        "",
        "| 名字 | 形象ID | 分级 | 说明 |",
        "|------|--------|------|------|",
    ]
    for ref in ["使魔", "钢铁领主", "水蓝鸟魔", "迷你蝙蝠", "雪蕾洁", "雪儿波波", "魔龙德拉贡"]:
        r = by_name.get(ref)
        if r:
            note = "已验证可换" if ref == "钢铁领主" else ""
            if ref in ("雪蕾洁", "雪儿波波"):
                note = "110xxx 谨慎"
            lines_md.append(
                f"| {ref} | {r.get('anim_id','-')} | {r.get('tier','-')} | {note} |"
            )
        else:
            lines_md.append(f"| {ref} | - | - | 未收录 |")

    csv_rows = show
    catalog_n = 0
    if profile.animdata_mode == "bundle_only" and profile.manifest_file.exists():
        catalog = get_store_backed_pets(profile)
        if catalog:
            csv_rows = catalog
            catalog_n = len(catalog)

    csv_lines = [
        "名字,形象ID,tempNo,种类,分级,ID段,anim头,包内明文ID,ID来源,chunk,bundle,status"
    ]
    for r in csv_rows:
        csv_lines.append(
            f"{r['name']},{r.get('anim_id','')},{r['tempNo']},{r['PetTypes']},"
            f"{r.get('tier','')},{r.get('id_segment','')},{r.get('chunk_hdr','')},"
            f"{r.get('plain_id',0)},{r.get('id_source','')},{r.get('chunk_size',0)},"
            f"{r.get('bundle_hash','')},{r.get('status','')}"
        )

    profile.table_md.write_text("\n".join(lines_md), encoding="utf-8")
    profile.table_csv.write_text("\n".join(csv_lines), encoding="utf-8")
    extra = f" | 工程备份目录 {catalog_n} 个形象" if catalog_n else ""
    print(f"推荐 {len(recommended)} | 可换 {len(normal)} | 谨慎 {len(caution)} | 合计可换 {len(swappable)}{extra}")
    print(f"  {profile.table_md}")
    print(f"  {profile.table_csv}")


# ---------------------------------------------------------------------------
# swap / restore
# ---------------------------------------------------------------------------

def patch_animdata_pair(
    chunks: dict[int, bytes],
    dst_id: int,
    src_chunk: bytes,
    src_id: int,
    log: LogFn = print,
) -> None:
    if dst_id not in chunks:
        raise RuntimeError(f"animdata 缺少目标 ID {dst_id}")
    log(f"  animdata {dst_id} ({len(chunks[dst_id])}B) <- 备份 {src_id} ({len(src_chunk)}B)")
    chunks[dst_id] = src_chunk


def _bundle_inner_hash_raw(raw: bytes, label: str = "bundle") -> str:
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS missing: {label}")
    env = UnityPy.load(raw[off:])
    for obj in env.objects:
        if obj.type.name != "AssetBundle":
            continue
        name = obj.read().m_Name
        if "_" in name:
            return name.rsplit("_", 1)[-1].removesuffix(".b")
    raise RuntimeError(f"AssetBundle 名解析失败: {label}")


def _bundle_inner_hash(bundle_path: Path) -> str:
    return _bundle_inner_hash_raw(bundle_path.read_bytes(), bundle_path.name)


def patch_bundle_from_raw(
    dst_id: int,
    src_id: int,
    dst_hash: str,
    src_bundle_raw: bytes,
    log: LogFn = print,
    dst_profile: GameProfile = LOCAL,
) -> None:
    dst_path = dst_profile.assets / f"{dst_hash}.b"
    if not dst_path.exists():
        raise RuntimeError(f"目标动画包缺失: {dst_hash}")

    dst_raw = dst_path.read_bytes()
    dst_off = dst_raw.find(b"UnityFS")
    if dst_off < 0:
        raise RuntimeError(f"UnityFS missing: {dst_path}")

    src_off = src_bundle_raw.find(b"UnityFS")
    if src_off < 0:
        raise RuntimeError("备份包缺少 UnityFS")

    src_body = bytearray(src_bundle_raw[src_off:])
    src_inner = _bundle_inner_hash_raw(src_bundle_raw, f"备份 {src_id}").encode("ascii")
    dst_inner = _bundle_inner_hash(dst_path).encode("ascii")
    if src_inner in src_body:
        src_body = src_body.replace(src_inner, dst_inner)
        log(f"    替换内嵌 hash {src_inner.decode()[:8]}... -> {dst_inner.decode()[:8]}...")

    src_s, dst_s = str(src_id).encode(), str(dst_id).encode()
    if len(src_s) != len(dst_s):
        raise RuntimeError(f"ID 位数不同，无法替换字符串: {src_id} vs {dst_id}")
    n = bytes(src_body).count(src_s)
    if n:
        src_body = src_body.replace(src_s, dst_s)
        log(f"    替换包内 ID 字符串 x{n}")
    else:
        log(f"    源包无明文 {src_id}，仅替换 UnityFS 内容 + 内嵌 hash")

    out = dst_raw[:dst_off] + bytes(src_body)

    env = UnityPy.load(out[dst_off:])
    ok_ab = ok_anim = False
    ab_name = ""
    for obj in env.objects:
        if obj.type.name == "AssetBundle":
            ab_name = obj.read().m_Name
            ok_ab = str(dst_id) in ab_name
        elif obj.type.name == "TextAsset" and obj.read().m_Name == "animdata":
            ok_anim = True
    if not ok_anim:
        raise RuntimeError(f"包校验失败(无 animdata): {dst_hash}")
    if not ok_ab:
        log(f"    提示: 内嵌 AssetBundle 名仍为 {ab_name}（外层仍映射 {dst_id}）")

    dst_path.write_bytes(out)
    log(f"  包 {dst_hash[:12]}... ({len(out)} bytes)")


def patch_bundle(dst_id: int, src_id: int, dst_hash: str, src_hash: str, log: LogFn = print) -> None:
    src_path = ASSETS / f"{src_hash}.b"
    if not src_path.exists():
        raise RuntimeError("源动画包文件缺失")
    patch_bundle_from_raw(dst_id, src_id, dst_hash, src_path.read_bytes(), log=log)


def has_store_appearance(anim_id: int, profile: GameProfile = LOCAL) -> bool:
    d = profile.appearances_dir / str(anim_id)
    return (d / "animdata_chunk.bin").exists() and (d / "bundle.b").exists()


def load_store_appearance(anim_id: int, profile: GameProfile = LOCAL) -> tuple[bytes, bytes, dict]:
    src_dir = profile.appearances_dir / str(anim_id)
    if not has_store_appearance(anim_id, profile):
        raise RuntimeError(f"[{profile.label}] 备份中无形象 {anim_id}，请先运行 backup-all")
    meta = json.loads((src_dir / "meta.json").read_text(encoding="utf-8"))
    chunk = (src_dir / "animdata_chunk.bin").read_bytes()
    bundle = (src_dir / "bundle.b").read_bytes()
    return chunk, bundle, meta


def get_swappable_pets(
    profile: GameProfile = LOCAL,
    force: bool = False,
    *,
    validate_battle: bool = False,
) -> list[dict]:
    """列出可检索宠物：有形象 ID 即收录；能读到战斗贴图的优先排前。"""
    cache_path = _enriched_list_cache(profile)
    index_path = profile.pet_index_file
    if (
        not force
        and not validate_battle
        and cache_path.exists()
        and index_path.exists()
        and cache_path.stat().st_mtime >= index_path.stat().st_mtime
    ):
        try:
            cached = json.loads(cache_path.read_text(encoding="utf-8"))
            if isinstance(cached, list) and cached:
                return cached
        except (json.JSONDecodeError, TypeError):
            pass

    bundle_map = scan_bundle_map(profile, force=force)
    rows = enrich_with_resources(
        build_pet_index(profile, force=force),
        bundle_map,
        profile,
        validate_battle=validate_battle,
    )
    listed = [r for r in rows if r.get("anim_id")]
    result = sorted(
        _pick_best_by_name(listed).values(),
        key=lambda r: (
            0 if r.get("battle_readable") else 1,
            {"推荐": 0, "可换": 1, "谨慎": 2, "无资源": 3, "无形象ID": 9}.get(r.get("tier", ""), 5),
            r["name"],
        ),
    )
    if result:
        profile.cache_dir.mkdir(parents=True, exist_ok=True)
        cache_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def build_store_catalog_rows(profile: GameProfile, *, validate_battle: bool = False) -> list[dict]:
    """从工程备份 manifest 构建完整外形目录（含无宠物名关联的 ID）。"""
    manifest = load_manifest(profile)
    battle_cache = load_battle_ok_cache(profile)
    out: list[dict] = []
    for aid_str, meta in manifest.get("appearances", {}).items():
        aid = int(aid_str)
        if not has_store_appearance(aid, profile):
            continue
        names = list(dict.fromkeys(meta.get("names") or [f"形象_{aid}"]))
        display = next((n for n in names if not str(n).startswith("形象_")), names[0])
        chunk_path = profile.appearances_dir / str(aid) / "animdata_chunk.bin"
        bundle_path_store = profile.appearances_dir / str(aid) / "bundle.b"
        chunk = chunk_path.read_bytes() if chunk_path.exists() else b""
        plain = 0
        if bundle_path_store.exists():
            plain = bundle_path_store.read_bytes().count(str(aid).encode())
        bh = meta.get("bundle_hash", "")
        if validate_battle and bundle_path_store.exists():
            battle_ok = can_read_battle_assets(
                aid, {aid: bh}, profile, bundle_file=bundle_path_store
            )
            battle_cache[aid] = battle_ok
        elif aid in battle_cache:
            battle_ok = battle_cache[aid]
        else:
            battle_ok = bundle_path_store.exists() and bundle_has_animdata(bundle_path_store)
        out.append(
            {
                "name": display,
                "anim_id": aid,
                "tempNo": 0,
                "PetTypes": 0,
                "tier": classify_swap_tier(aid, chunk, plain, "", bool(chunk), bool(bh)),
                "id_segment": id_segment(aid),
                "chunk_hdr": chunk_header_tag(chunk),
                "plain_id": plain,
                "id_source": "store",
                "chunk_size": len(chunk) or meta.get("chunk_size", 0),
                "status": "可换" if battle_ok else ("仅包" if bundle_path_store.exists() else "无资源"),
                "battle_readable": battle_ok,
                "bundle_hash": bh,
                "aliases": names,
            }
        )
    if validate_battle:
        save_battle_ok_cache(profile, battle_cache)
    return sorted(out, key=lambda r: r["anim_id"])


def get_store_backed_pets(profile: GameProfile, *, validate_battle: bool = False) -> list[dict]:
    """从工程备份目录列出可作跨版源的外形（能读战斗贴图的优先保留）。"""
    by_anim = {r["anim_id"]: r for r in build_store_catalog_rows(profile, validate_battle=validate_battle)}
    if not by_anim:
        return []

    # 合并配置表名字（local / eternal 各自 pet_swap_table_*.csv）
    if profile.table_csv.exists():
        with profile.table_csv.open(encoding="utf-8-sig", newline="") as f:
            for row in csv.DictReader(f):
                try:
                    aid = int(row["形象ID"])
                except (KeyError, ValueError):
                    continue
                if aid not in by_anim:
                    continue
                pet_name = row["名字"]
                entry = by_anim[aid]
                tr = row.get("分级", "")
                old_tr = entry.get("tier", "")
                tier_rank = {"推荐": 0, "可换": 1, "谨慎": 2}
                if pet_name and not pet_name.startswith("形象_"):
                    entry["name"] = pet_name
                if tier_rank.get(tr, 9) < tier_rank.get(old_tr, 9):
                    entry["tier"] = tr
                for k, csv_k, typ in (
                    ("tempNo", "tempNo", int),
                    ("PetTypes", "种类", int),
                    ("id_segment", "ID段", str),
                    ("chunk_hdr", "anim头", str),
                    ("plain_id", "包内明文ID", int),
                    ("id_source", "ID来源", str),
                    ("chunk_size", "chunk", int),
                    ("bundle_hash", "bundle", str),
                ):
                    if row.get(csv_k):
                        entry[k] = typ(row[csv_k])

    if profile.pet_index_file.exists():
        try:
            for r in build_pet_index(profile, force=False):
                aid = r.get("anim_id") or 0
                entry = by_anim.get(aid)
                if entry and entry["name"].startswith("形象_") and r.get("name"):
                    entry["name"] = r["name"]
        except Exception:
            pass

    return sorted(
        by_anim.values(),
        key=lambda r: (
            0 if r.get("battle_readable") else 1,
            {"推荐": 0, "可换": 1, "谨慎": 2}.get(r.get("tier", ""), 9),
            r["name"],
        ),
    )


def perform_swap_from_store(
    dst_id: int,
    src_id: int,
    dst_name: str,
    src_name: str,
    *,
    dst_profile: GameProfile = LOCAL,
    src_profile: GameProfile = LOCAL,
    rows: list[dict] | None = None,
    swap_head: bool = True,
    tint: TintParams | None = None,
    log: LogFn = print,
) -> None:
    if dst_id == src_id and dst_profile.key == src_profile.key:
        raise RuntimeError("源与目标形象 ID 相同")

    dst_bundle_map = scan_bundle_map(dst_profile)
    if rows is None:
        rows = enrich_with_resources(build_pet_index(dst_profile), dst_bundle_map, dst_profile)

    dst_hash = dst_bundle_map.get(dst_id)
    if not dst_hash:
        raise RuntimeError(f"缺少目标动画包映射: {dst_id}")
    if not has_store_appearance(src_id, src_profile):
        raise RuntimeError(
            f"源外形「{src_name}」({src_id}) 在 [{src_profile.label}] 未备份，请先运行 backup-all"
        )

    src_chunk, src_bundle, src_meta = load_store_appearance(src_id, src_profile)
    if tint is not None and not is_neutral_tint(tint):
        t = tint.normalized()
        sprite_ids = sprite_ids_in_chunk(src_chunk)
        summary = format_tint_summary(t)
        log(f"  调色 ({t.mode.upper()}): {summary} ({len(sprite_ids)} sprites)")
        src_bundle = apply_tint_to_bundle_bytes(src_bundle, sprite_ids, t)
        log("  调色已烘焙进源包（图集像素写回 + lz4 + animdata 校验）")
    cross = dst_profile.key != src_profile.key
    tag = "跨版本" if cross else "从备份"
    log(
        f"=== 换形 [{dst_profile.label}]: {dst_name} ({dst_id}) "
        f"<- [{src_profile.label}] {src_name} ({src_id}) [{tag}] ==="
    )
    log(f"  源备份: chunk {len(src_chunk)}B, 包 {src_meta.get('bundle_hash', '')[:12]}...")

    manifest = load_manifest(dst_profile)
    log("备份目标（若尚未备份）...")
    backup_global_animdata(dst_profile, log=log)
    if str(dst_id) not in manifest["appearances"]:
        dst_names = [r["name"] for r in rows if r.get("anim_id") == dst_id]
        backup_appearance(
            dst_id, dst_names or [dst_name], dst_bundle_map, manifest, dst_profile, log=log
        )

    set_anim_chunk(dst_id, src_chunk, dst_bundle_map, dst_profile, log=log)
    patch_bundle_from_raw(
        dst_id, src_id, dst_hash, src_bundle, log=log, dst_profile=dst_profile
    )

    head_ok = False
    cross_head_note = CROSS_HEAD_BLOCK.get(src_profile.key)
    if swap_head and dst_profile.key != src_profile.key and cross_head_note:
        log(f"--- 头像：{src_profile.label}跨版本暂禁 ---")
        log(f"  说明: {cross_head_note}")
        swap_head = False
    if swap_head:
        log("--- 替换头像 (pethead) ---")
        try:
            swap_pet_head(
                dst_id,
                src_id,
                dst_profile=dst_profile,
                src_profile=src_profile,
                log=log,
            )
            head_ok = True
        except Exception as e:
            log(f"  头像替换跳过: {e}")

    manifest["swaps"].append(
        {
            "time": datetime.now(timezone.utc).isoformat(),
            "dst_id": dst_id,
            "src_id": src_id,
            "dst_name": dst_name,
            "src_name": src_name,
            "dst_profile": dst_profile.key,
            "src_profile": src_profile.key,
            "from_store": True,
            "cross": cross,
            "swap_head": swap_head,
            "head_ok": head_ok,
        }
    )
    save_manifest(manifest, dst_profile)
    log("完成。请先关闭游戏再启动验证（跳过热更新）。")


def perform_swap_from_external(
    dst_id: int,
    external_name: str,
    dst_name: str,
    *,
    dst_profile: GameProfile = LOCAL,
    rows: list[dict] | None = None,
    log: LogFn = print,
) -> None:
    """用外部库形象替换当前魔力目标（操作与同版本换形一致，源来自 pet_external_store）。"""
    from pet_external_store import entry_exists, load_external_appearance

    if not entry_exists(external_name):
        raise RuntimeError(f"外部形象不存在: {external_name}")

    src_chunk, src_bundle, src_meta = load_external_appearance(external_name)
    src_id = int(src_meta.get("anim_id", 0))
    if not src_id:
        raise RuntimeError(f"外部形象「{external_name}」缺少 anim_id")
    src_display = src_meta.get("source_pet") or external_name

    dst_bundle_map = scan_bundle_map(dst_profile)
    if rows is None:
        rows = enrich_with_resources(build_pet_index(dst_profile), dst_bundle_map, dst_profile)

    dst_hash = dst_bundle_map.get(dst_id)
    if not dst_hash:
        raise RuntimeError(f"缺少目标动画包映射: {dst_id}")

    log(
        f"=== 外部输入 [{dst_profile.label}]: {dst_name} ({dst_id}) "
        f"<- 外部库「{external_name}」({src_id}) ==="
    )
    log(f"  外部源: chunk {len(src_chunk)}B, 包 {str(src_meta.get('bundle_hash', ''))[:12]}...")

    manifest = load_manifest(dst_profile)
    log("备份目标（若尚未备份）...")
    backup_global_animdata(dst_profile, log=log)
    if str(dst_id) not in manifest["appearances"]:
        dst_names = [r["name"] for r in rows if r.get("anim_id") == dst_id]
        backup_appearance(
            dst_id, dst_names or [dst_name], dst_bundle_map, manifest, dst_profile, log=log
        )

    set_anim_chunk(dst_id, src_chunk, dst_bundle_map, dst_profile, log=log)
    patch_bundle_from_raw(
        dst_id, src_id, dst_hash, src_bundle, log=log, dst_profile=dst_profile
    )

    log("--- 头像：外部源不含头像，跳过 ---")

    manifest["swaps"].append(
        {
            "time": datetime.now(timezone.utc).isoformat(),
            "dst_id": dst_id,
            "src_id": src_id,
            "dst_name": dst_name,
            "src_name": src_display,
            "external_name": external_name,
            "dst_profile": dst_profile.key,
            "from_external": True,
            "swap_head": False,
            "head_ok": False,
        }
    )
    save_manifest(manifest, dst_profile)
    log("完成。请先关闭游戏再启动验证（跳过热更新）。")


def cmd_swap(args: argparse.Namespace) -> None:
    bundle_map = scan_bundle_map()
    rows = enrich_with_resources(build_pet_index(), bundle_map)

    if args.dst_id and args.src_id:
        dst_id, src_id = args.dst_id, args.src_id
        dst_name = args.dst or str(dst_id)
        src_name = args.src or str(src_id)
    else:
        if not args.dst or not args.src:
            raise SystemExit("请指定 --dst/--src 名字，或 --dst-id/--src-id")
        dst_rec = pick_by_name(rows, args.dst)
        src_rec = pick_by_name(rows, args.src)
        dst_id, src_id = dst_rec["anim_id"], src_rec["anim_id"]
        dst_name, src_name = args.dst, args.src

    perform_swap_from_store(dst_id, src_id, dst_name, src_name, rows=rows)


def restore_appearance(anim_id: int, profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    src_dir = profile.appearances_dir / str(anim_id)
    if not src_dir.exists():
        raise RuntimeError(f"[{profile.label}] store 中无备份: {anim_id}")

    meta = json.loads((src_dir / "meta.json").read_text(encoding="utf-8"))
    chunk = (src_dir / "animdata_chunk.bin").read_bytes()
    bundle_hash = meta["bundle_hash"]
    dst_bundle = profile.assets / f"{bundle_hash}.b"

    bundle_map = scan_bundle_map(profile)
    set_anim_chunk(anim_id, chunk, bundle_map, profile, log=log)

    if (src_dir / "bundle.b").exists():
        shutil.copy2(src_dir / "bundle.b", dst_bundle)
        log(f"  恢复包 {bundle_hash[:12]}...")

    head_png = src_dir / "head.png"
    if head_png.exists():
        try:
            restore_pet_head(anim_id, profile, log=log)
        except Exception as e:
            log(f"  头像恢复跳过: {e}")


def cmd_restore(args: argparse.Namespace, profile: GameProfile = LOCAL) -> None:
    if args.id:
        print(f"=== 恢复形象 {args.id} ===")
        restore_appearance(args.id, profile)
        print("完成")
        return

    if args.name:
        rows = enrich_with_resources(build_pet_index(profile), scan_bundle_map(profile), profile)
        rec = pick_by_name(rows, args.name, require_swappable=False)
        if not rec.get("anim_id"):
            raise SystemExit(f"{args.name} 无有效形象ID")
        print(f"=== 恢复 {args.name} ({rec['anim_id']}) ===")
        restore_appearance(rec["anim_id"], profile)
        print("完成")
        return

    raise SystemExit("请指定 --id 或 --name")


def cmd_restore_all(args: argparse.Namespace, profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    manifest = load_manifest(profile)
    if not manifest.get("appearances"):
        raise SystemExit(f"无备份，请先运行 backup-all ({profile.store})")

    if profile_is_monolithic(profile):
        log("=== 恢复全局 animdata ===")
        for name in ("animdatainfo.bin", "animdata.bin"):
            src = profile.global_dir / name
            if src.exists():
                shutil.copy2(src, profile.animdata_dir / name)
                log(f"  {name}")
    restore_global_pethead(profile, log=log)

    log(f"=== 恢复 {len(manifest['appearances'])} 个形象 ===")
    for aid in sorted(manifest["appearances"], key=int):
        restore_appearance(int(aid), profile, log=log)

    log("全部恢复完成")


def cmd_status(args: argparse.Namespace, profile: GameProfile = LOCAL) -> None:
    manifest = load_manifest(profile)
    n_app = len(manifest.get("appearances", {}))
    n_swap = len(manifest.get("swaps", []))
    print(f"备份目录: {profile.store}")
    print(f"  已备份形象: {n_app}")
    print(f"  历史换形记录: {n_swap}")
    if manifest.get("swaps"):
        last = manifest["swaps"][-1]
        print(f"  最近一次: {last['dst_name']}({last['dst_id']}) <- {last['src_name']}({last['src_id']})")
    print(f"对照表: {profile.table_md}")


def setup_profile_pets(profile: GameProfile, force: bool = False, log: LogFn = print) -> None:
    if not profile.exists():
        raise RuntimeError(f"目录不存在: {profile.root}")
    log(f"=== 解包 [{profile.label}] 宠物列表 ===")
    log(f"  游戏目录: {profile.root}")
    log(f"  资源模式: {profile.animdata_mode}")
    log(f"  备份目录: {profile.store}")

    force_flag = force

    class Args:
        force = force_flag
        force_scan = force_flag
        all = False

    cmd_backup_all(Args(), profile=profile)
    cmd_list(Args(), profile=profile)
    pets = get_swappable_pets(profile, force=force, validate_battle=force)
    store_n = len(get_store_backed_pets(profile))
    n_bak = sum(1 for p in pets if has_store_appearance(p["anim_id"], profile))
    n_battle = sum(1 for p in pets if p.get("battle_readable"))
    if profile.animdata_mode == "bundle_only":
        log(f"收录 {len(pets)} 个名字 | 战斗贴图可读 {n_battle} | 工程备份外形 {store_n} 个 ID")
    else:
        log(f"收录 {len(pets)} 个名字 | 战斗贴图可读 {n_battle} | 已备份 {n_bak}")
    log(f"  列表: {profile.table_csv}")
    log(f"  备份: {profile.store}")


def setup_eternal_pets(force: bool = False, log: LogFn = print) -> None:
    """解包魔力永恒宠物列表并备份形象到当前工程。"""
    if not ETERNAL.exists():
        raise RuntimeError(f"魔力永恒目录不存在: {ETERNAL.root}")
    setup_profile_pets(ETERNAL, force=force, log=log)


def main() -> None:
    parser = argparse.ArgumentParser(description="宠物战斗形象管理")
    parser.add_argument("--force", action="store_true", help="强制重建缓存/覆盖备份")
    parser.add_argument(
        "--profile",
        choices=["local", "eternal"],
        default="local",
        help="游戏实例（local / eternal）",
    )
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_list = sub.add_parser("list", help="输出名字-形象对照表")
    p_list.add_argument("--all", action="store_true", help="包含无资源条目")
    p_list.add_argument("--force", action="store_true", help="重建宠物/包索引")
    p_list.set_defaults(func=cmd_list)

    p_scan = sub.add_parser("scan-bundles", help="扫描并缓存动画包索引")
    p_scan.set_defaults(func=lambda a: scan_bundle_map(force=True))

    p_bak = sub.add_parser("backup-all", help="备份所有可换形象")
    p_bak.add_argument("--force-scan", action="store_true")
    p_bak.set_defaults(func=cmd_backup_all)

    p_swap = sub.add_parser("swap", help="替换形象（改目标 ID 的资源）")
    p_swap.add_argument("--dst", help="目标宠物名（如 使魔）")
    p_swap.add_argument("--src", help="源外形宠物名（如 雪蕾洁）")
    p_swap.add_argument("--dst-id", type=int)
    p_swap.add_argument("--src-id", type=int)
    p_swap.set_defaults(func=cmd_swap)

    p_res = sub.add_parser("restore", help="从 store 恢复单个形象")
    p_res.add_argument("--id", type=int, help="形象 ID")
    p_res.add_argument("--name", help="宠物名")
    p_res.set_defaults(func=cmd_restore)

    p_ra = sub.add_parser("restore-all", help="恢复全部备份")
    p_ra.set_defaults(func=cmd_restore_all)

    p_st = sub.add_parser("status", help="查看备份/换形状态")
    p_st.set_defaults(func=cmd_status)

    p_et = sub.add_parser("setup-eternal", help="解包并备份魔力永恒宠物到当前工程")
    p_et.set_defaults(func=lambda a: setup_eternal_pets(force=a.force))

    args = parser.parse_args()
    profile = PROFILES[args.profile]
    if args.cmd == "scan-bundles":
        scan_bundle_map(profile, force=True)
        return
    args.force_scan = getattr(args, "force_scan", False) or args.force
    if args.cmd == "setup-eternal":
        setup_eternal_pets(force=args.force)
        return
    if args.cmd == "swap":
        cmd_swap(args)
    elif args.cmd == "restore":
        cmd_restore(args, profile)
    else:
        args.func(args, profile)


if __name__ == "__main__":
    main()
