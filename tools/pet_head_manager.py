#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""宠物头像：pethead.spriteatlas，sprite 名 = 形象 ID（如 101201）。"""
from __future__ import annotations

import copy
import json
import shutil
from collections.abc import Callable
from pathlib import Path

import UnityPy
from PIL import Image, ImageChops

from game_profile import LOCAL, GameProfile

LogFn = Callable[[str], None]

PETHEAD_BUNDLE = "44abe811fc38e1970cd5aff01377e142"


def pethead_bundle_path(profile: GameProfile = LOCAL) -> Path:
    return profile.assets / f"{PETHEAD_BUNDLE}.b"


def pethead_store_bundle(profile: GameProfile = LOCAL) -> Path:
    return profile.global_dir / "pethead.b"


def head_png_path(anim_id: int, profile: GameProfile = LOCAL) -> Path:
    return profile.appearances_dir / str(anim_id) / "head.png"


def head_atlas_meta_path(anim_id: int, profile: GameProfile = LOCAL) -> Path:
    return profile.appearances_dir / str(anim_id) / "head_atlas.json"


def load_bundle(path: Path) -> tuple[bytes, int, UnityPy.Environment]:
    raw = path.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS not found in {path}")
    return raw, off, UnityPy.load(raw[off:])


def save_bundle(raw: bytes, prefix_len: int, env: UnityPy.Environment, out: Path) -> None:
    body = env.file.save()
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(raw[:prefix_len] + body)


def _guid_parts(g) -> tuple[int, int, int, int]:
    if isinstance(g, dict):
        return tuple(int(g[f"data[{i}]"]) for i in range(4))
    return (int(g.data_0_), int(g.data_1_), int(g.data_2_), int(g.data_3_))


def normalize_render_key(key) -> tuple[tuple[int, int, int, int], int]:
    g, sub = key
    return _guid_parts(g), int(sub)


def keys_equal(a, b) -> bool:
    return normalize_render_key(a) == normalize_render_key(b)


def sprite_object(env: UnityPy.Environment, anim_id: int):
    name = str(anim_id)
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.read().m_Name == name:
            return obj
    raise RuntimeError(f"sprite {name} not found")


def atlas_object(env: UnityPy.Environment):
    for obj in env.objects:
        if obj.type.name == "SpriteAtlas":
            return obj
    raise RuntimeError("SpriteAtlas not found")


def sprite_names(env: UnityPy.Environment) -> set[str]:
    return {o.read().m_Name for o in env.objects if o.type.name == "Sprite"}


def has_pethead_sprite(anim_id: int, profile: GameProfile = LOCAL) -> bool:
    path = pethead_bundle_path(profile)
    if not path.exists():
        return False
    raw, off, env = load_bundle(path)
    return str(anim_id) in sprite_names(env)


def sprite_image(env: UnityPy.Environment, name: str) -> Image.Image:
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.read().m_Name == name:
            return obj.read().image.convert("RGBA")
    raise RuntimeError(f"sprite {name} not found")


def _find_render_entry(tt: dict, render_key) -> tuple[int, dict] | None:
    for i, entry in enumerate(tt["m_RenderDataMap"]):
        if keys_equal(entry[0], render_key):
            return i, entry[1]
    return None


def _backup_render_entry(anim_id: int, profile: GameProfile, log: LogFn = print) -> bool:
    if not has_pethead_sprite(anim_id, profile):
        return False
    raw, off, env = load_bundle(pethead_bundle_path(profile))
    dst_key = sprite_object(env, anim_id).read().m_RenderDataKey
    tt = atlas_object(env).read_typetree()
    found = _find_render_entry(tt, dst_key)
    if not found:
        return False
    _, data = found
    out = head_atlas_meta_path(anim_id, profile)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return True


def _copy_sprite_rd_typetree(env: UnityPy.Environment, dst_id: int, src_id: int) -> None:
    src_obj = sprite_object(env, src_id)
    dst_obj = sprite_object(env, dst_id)
    src_tt = src_obj.read_typetree()
    dst_tt = dst_obj.read_typetree()
    if "m_RD" in src_tt:
        dst_tt["m_RD"] = copy.deepcopy(src_tt["m_RD"])
    for field in ("m_VertexData", "m_IndexBuffer", "m_SubMeshes"):
        if field in src_tt:
            dst_tt[field] = copy.deepcopy(src_tt[field])
    dst_obj.save_typetree(dst_tt)


def _redirect_atlas_entry(env: UnityPy.Environment, dst_id: int, src_render_data: dict) -> dict:
    dst_key = sprite_object(env, dst_id).read().m_RenderDataKey
    atlas_obj = atlas_object(env)
    tt = atlas_obj.read_typetree()
    idx = _find_render_entry(tt, dst_key)
    if idx is None:
        raise RuntimeError(f"SpriteAtlas 无目标条目 {dst_id}")
    i, _ = idx
    entry = tt["m_RenderDataMap"][i]
    tt["m_RenderDataMap"][i] = (entry[0], copy.deepcopy(src_render_data))
    atlas_obj.save_typetree(tt)
    tr = src_render_data["textureRect"]
    return {
        "x": int(tr["x"]),
        "y": int(tr["y"]),
        "w": int(tr["width"]),
        "h": int(tr["height"]),
    }


def _load_src_render_data(src_id: int, src_profile: GameProfile) -> dict:
    meta = head_atlas_meta_path(src_id, src_profile)
    if meta.exists():
        return json.loads(meta.read_text(encoding="utf-8"))
    if not has_pethead_sprite(src_id, src_profile):
        raise RuntimeError(f"源无 pethead sprite {src_id}")
    raw, off, env = load_bundle(pethead_bundle_path(src_profile))
    src_key = sprite_object(env, src_id).read().m_RenderDataKey
    tt = atlas_object(env).read_typetree()
    found = _find_render_entry(tt, src_key)
    if not found:
        raise RuntimeError(f"源 SpriteAtlas 无条目 {src_id}")
    return copy.deepcopy(found[1])


def backup_global_pethead(profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    src = pethead_bundle_path(profile)
    if not src.exists():
        raise RuntimeError(f"pethead 包不存在: {src}")
    profile.global_dir.mkdir(parents=True, exist_ok=True)
    dst = pethead_store_bundle(profile)
    if not dst.exists() or dst.stat().st_size != src.stat().st_size:
        shutil.copy2(src, dst)
        log(f"  全局备份 [{profile.label}] pethead.b")


def extract_head_png(anim_id: int, profile: GameProfile = LOCAL, log: LogFn = print) -> bool:
    name = str(anim_id)
    if not has_pethead_sprite(anim_id, profile):
        return False
    path = pethead_bundle_path(profile)
    raw, off, env = load_bundle(path)
    img = sprite_image(env, name)
    out = head_png_path(anim_id, profile)
    out.parent.mkdir(parents=True, exist_ok=True)
    img.save(out)
    _backup_render_entry(anim_id, profile)
    log(f"  头像备份 [{profile.label}] {anim_id}")
    return True


def swap_pet_head(
    dst_id: int,
    src_id: int,
    *,
    dst_profile: GameProfile = LOCAL,
    src_profile: GameProfile = LOCAL,
    log: LogFn = print,
) -> None:
    """把源头像映射到目标 ID：改 SpriteAtlas 条目 + Sprite RD（BC7 图集不能靠像素粘贴）。"""
    dst_name, src_name = str(dst_id), str(src_id)
    dst_path = pethead_bundle_path(dst_profile)
    if not dst_path.exists():
        raise RuntimeError(f"pethead 包不存在: {dst_path}")
    if not has_pethead_sprite(dst_id, dst_profile):
        raise RuntimeError(f"目标 pethead 无 sprite {dst_name}")

    backup_global_pethead(dst_profile, log=log)
    _backup_render_entry(dst_id, dst_profile)

    src_render = _load_src_render_data(src_id, src_profile)
    log(f"  头像 {dst_name} <- {src_name} (atlas 重定向)")

    raw, off, env = load_bundle(dst_path)
    rect = _redirect_atlas_entry(env, dst_id, src_render)
    if src_profile.key == dst_profile.key:
        _copy_sprite_rd_typetree(env, dst_id, src_id)
    else:
        _, src_off, src_env = load_bundle(pethead_bundle_path(src_profile))
        dst_obj = sprite_object(env, dst_id)
        dst_tt = dst_obj.read_typetree()
        src_tt = sprite_object(src_env, src_id).read_typetree()
        if "m_RD" in src_tt:
            dst_tt["m_RD"] = copy.deepcopy(src_tt["m_RD"])
        dst_obj.save_typetree(dst_tt)

    save_bundle(raw, off, env, dst_path)

    raw2, off2, env2 = load_bundle(dst_path)
    if src_profile.key == dst_profile.key and has_pethead_sprite(src_id, dst_profile):
        diff = ImageChops.difference(sprite_image(env2, dst_name), sprite_image(env2, src_name))
        if diff.getbbox():
            raise RuntimeError(f"头像写入校验失败 {dst_name} 与 {src_name} 仍不一致")
    log(
        f"  pethead 已写入 {dst_path.name} "
        f"(指向 x={rect['x']} y={rect['y']} w={rect['w']} h={rect['h']})"
    )


def restore_global_pethead(profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    src = pethead_store_bundle(profile)
    dst = pethead_bundle_path(profile)
    if src.exists():
        shutil.copy2(src, dst)
        log(f"  恢复全局 pethead.b [{profile.label}]")


def restore_pet_head(anim_id: int, profile: GameProfile = LOCAL, log: LogFn = print) -> None:
    meta = head_atlas_meta_path(anim_id, profile)
    if not meta.exists():
        raise RuntimeError(f"无头像 atlas 备份: {anim_id}")
    if not has_pethead_sprite(anim_id, profile):
        raise RuntimeError(f"pethead 无 sprite {anim_id}")
    render_data = json.loads(meta.read_text(encoding="utf-8"))
    raw, off, env = load_bundle(pethead_bundle_path(profile))
    _redirect_atlas_entry(env, anim_id, render_data)
    save_bundle(raw, off, env, pethead_bundle_path(profile))
    log(f"  恢复头像 pethead sprite {anim_id}")
