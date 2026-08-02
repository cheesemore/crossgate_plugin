#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""宠物形象预览：头像 / 战斗贴图 / 战斗动画帧。"""
from __future__ import annotations

import struct
import threading
from dataclasses import dataclass
from pathlib import Path

import UnityPy
from PIL import Image

from game_profile import GameProfile, LOCAL
from pet_anim_manager import (
    bundle_path,
    get_anim_chunk,
    scan_bundle_map,
)
from pet_head_manager import (
    extract_head_png,
    has_pethead_sprite,
    head_png_path,
    load_bundle as load_pethead_bundle,
    pethead_bundle_path,
    sprite_image,
)

# ActionType.Stand=0, Direction 优先左下/下（战斗常见朝向）
PREFERRED_DIRECTIONS = (6, 5, 7, 4, 0, 1, 2, 3)  # LeftDown, Down, Left, RightDown, ...
STAND_ACTION = 0


@dataclass
class AnimClip:
    action_type: int
    direction: int
    clip_id: int
    duration: int
    frame_count: int
    frame_sprites: list[int]
    frame_sounds: list[int]


@dataclass
class AnimMeta:
    pivot_x: float
    pivot_y: float
    width: int
    height: int


@dataclass
class FrameSlice:
    image: Image.Image
    pivot_x: float
    pivot_y: float


_coord_cache: dict[str, dict[int, dict[int, list[tuple[int, int]]]]] = {}


def display_direction(direction: int) -> int:
    if direction == 2:
        return 0
    if direction == 3:
        return 7
    if direction == 4:
        return 6
    return direction


def load_bundle_env(bundle_path: Path) -> tuple[bytes, int, UnityPy.Environment]:
    raw = bundle_path.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS 未找到: {bundle_path}")
    return raw, off, UnityPy.load(raw[off:])


def parse_animdata(chunk: bytes) -> tuple[list[AnimClip], AnimMeta]:
    if len(chunk) < 12:
        raise RuntimeError("animdata 块过短")
    pos = 0
    action_count = struct.unpack_from("<h", chunk, pos)[0]
    pos += 2
    clips: list[AnimClip] = []
    for _ in range(action_count):
        if pos + 2 > len(chunk):
            break
        action_type = struct.unpack_from("<H", chunk, pos)[0] & 0xFF
        pos += 2
        if pos + 2 > len(chunk):
            break
        dir_count = struct.unpack_from("<h", chunk, pos)[0]
        pos += 2
        for _ in range(dir_count):
            if pos + 2 > len(chunk):
                break
            direction = struct.unpack_from("<H", chunk, pos)[0]
            pos += 2
            if pos + 8 > len(chunk):
                break
            duration = struct.unpack_from("<I", chunk, pos)[0]
            pos += 4
            frame_count = struct.unpack_from("<I", chunk, pos)[0]
            pos += 4
            if frame_count < 0 or frame_count > 512 or pos + frame_count * 6 > len(chunk):
                raise RuntimeError(f"animdata 帧数异常: {frame_count}")
            frame_sprites: list[int] = []
            frame_sounds: list[int] = []
            for _fi in range(frame_count):
                frame_sprites.append(struct.unpack_from("<I", chunk, pos)[0])
                pos += 4
                frame_sounds.append(struct.unpack_from("<H", chunk, pos)[0])
                pos += 2
            clip_id = action_type * 1000 + display_direction(direction)
            clips.append(
                AnimClip(
                    action_type=action_type,
                    direction=direction,
                    clip_id=clip_id,
                    duration=duration,
                    frame_count=frame_count,
                    frame_sprites=frame_sprites,
                    frame_sounds=frame_sounds,
                )
            )
    if pos + 12 > len(chunk):
        meta = AnimMeta(0.5, 0.5, 0, 0)
    else:
        pivot_x = struct.unpack_from("<f", chunk, pos)[0]
        pos += 4
        pivot_y = struct.unpack_from("<f", chunk, pos)[0]
        pos += 4
        width = struct.unpack_from("<H", chunk, pos)[0]
        pos += 2
        height = struct.unpack_from("<H", chunk, pos)[0]
        meta = AnimMeta(pivot_x, pivot_y, width, height)
    return clips, meta


def pick_stand_clip(clips: list[AnimClip]) -> AnimClip | None:
    stand = [c for c in clips if c.action_type == STAND_ACTION and c.frame_count > 0]
    if not stand:
        stand = [c for c in clips if c.frame_count > 0]
    if not stand:
        return None
    for d in PREFERRED_DIRECTIONS:
        for c in stand:
            if display_direction(c.direction) == d:
                return c
    return stand[0]


def pick_walk_clip(clips: list[AnimClip]) -> AnimClip | None:
    walk = [c for c in clips if c.action_type == 1 and c.frame_count > 0]
    if not walk:
        return pick_stand_clip(clips)
    for d in PREFERRED_DIRECTIONS:
        for c in walk:
            if display_direction(c.direction) == d:
                return c
    return walk[0]


def pick_first_clip(clips: list[AnimClip], clip_index: int = 0) -> AnimClip | None:
    """技能特效用首个有效片段（对应游戏 EffectAnimator.PlayFirstAnim）。"""
    valid = [c for c in clips if c.frame_count > 0]
    if not valid:
        return None
    if clip_index < 0 or clip_index >= len(valid):
        return valid[0]
    return valid[clip_index]


def extract_sprite(env: UnityPy.Environment, sprite_id: int) -> Image.Image | None:
    row = extract_sprite_frame(env, sprite_id)
    return row.image if row else None


def extract_sprite_frame(env: UnityPy.Environment, sprite_id: int) -> FrameSlice | None:
    """与 extract_sprite 同源切图，额外返回 pivot（Unity 归一化 → PIL 左上原点）。"""
    name = str(sprite_id)
    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue
        sp = obj.read()
        if sp.m_Name != name:
            continue
        img = sp.image.convert("RGBA")
        w, h = img.size
        px = sp.m_Pivot.x * w
        py = h * (1.0 - sp.m_Pivot.y)
        return FrameSlice(img, px, py)
    return None


def coord_clip_key(clip: AnimClip) -> int:
    """与 AnimationInfo.id / coord.bin 一致：原始 direction，不用 display 映射。"""
    return clip.action_type * 1000 + clip.direction


def load_coord_map(profile: GameProfile) -> dict[int, dict[int, list[tuple[int, int]]]]:
    cache_key = profile.key
    if cache_key in _coord_cache:
        return _coord_cache[cache_key]

    cr = profile.assets / "clientresource"
    info_path = cr / "coordinfo.bin"
    data_path = cr / "coord.bin"
    out: dict[int, dict[int, list[tuple[int, int]]]] = {}
    if info_path.exists() and data_path.exists():
        info = info_path.read_bytes()
        data = data_path.read_bytes()
        pos = 0
        while pos + 10 <= len(info):
            spr_no, addr, anime_count = struct.unpack_from("<IiH", info, pos)
            pos += 10
            clips: dict[int, list[tuple[int, int]]] = {}
            cpos = addr
            for _ in range(anime_count):
                if cpos + 8 > len(data):
                    break
                direction, action_type, frame_count = struct.unpack_from("<HHI", data, cpos)
                cpos += 8
                clip_key = action_type * 1000 + direction
                need = frame_count * 4
                if cpos + need > len(data):
                    break
                frames = [
                    struct.unpack_from("<hh", data, cpos + fi * 4)
                    for fi in range(frame_count)
                ]
                cpos += need
                clips[clip_key] = [(int(x), int(y)) for x, y in frames]
            out[spr_no] = clips

    _coord_cache[cache_key] = out
    return out


def frame_coord_offsets(
    anim_id: int,
    clip: AnimClip,
    slices: list[FrameSlice],
    profile: GameProfile,
) -> list[tuple[int, int]]:
    """每帧 offset；缺失时与 AnimatorItemSystem 回退一致。"""
    key = coord_clip_key(clip)
    coords = load_coord_map(profile).get(anim_id, {}).get(key)
    out: list[tuple[int, int]] = []
    for i, sl in enumerate(slices):
        if coords and i < len(coords):
            out.append(coords[i])
        else:
            out.append((0, int(-sl.pivot_y)))
    return out


def compose_frames_with_coord(
    slices: list[FrameSlice],
    coords: list[tuple[int, int]],
    *,
    padding: int = 16,
    bg: tuple[int, int, int, int] = (0, 0, 0, 0),
) -> list[Image.Image]:
    """按 coord 相对首帧偏移贴到统一画布，切图仍用 UnityPy sprite.image。

    默认透明底：避免预览/调色时把背景灰像素当成精灵内容。
    """
    if not slices:
        return []

    ref_ox, ref_oy = coords[0]
    placements: list[tuple[int, int, Image.Image]] = []
    for sl, (ox, oy) in zip(slices, coords):
        paste_x = int(round(padding + (ox - ref_ox) - sl.pivot_x))
        paste_y = int(round(padding + (oy - ref_oy) - sl.pivot_y))
        placements.append((paste_x, paste_y, sl.image))

    min_x = min(px for px, _py, _ in placements)
    min_y = min(py for _px, py, _ in placements)
    max_x = max(px + im.width for px, _py, im in placements)
    max_y = max(py + im.height for _px, py, im in placements)

    shift_x = padding - min_x if min_x < padding else 0
    shift_y = padding - min_y if min_y < padding else 0
    canvas_w = max(max_x + shift_x, padding * 2)
    canvas_h = max(max_y + shift_y, padding * 2)

    composed: list[Image.Image] = []
    for paste_x, paste_y, img in placements:
        canvas = Image.new("RGBA", (canvas_w, canvas_h), bg)
        canvas.paste(img, (paste_x + shift_x, paste_y + shift_y), img)
        composed.append(canvas)
    return composed


def _resolve_bundle(anim_id: int, profile: GameProfile, bundle_map: dict[int, str] | None) -> Path:
    bm = bundle_map or scan_bundle_map(profile)
    bp = bundle_path(anim_id, bm, profile)
    if bp:
        return bp
    store_bp = profile.appearances_dir / str(anim_id) / "bundle.b"
    if store_bp.exists():
        return store_bp
    raise RuntimeError(f"未找到动画包 ID {anim_id} [{profile.label}]")


def _resolve_anim_chunk(anim_id: int, profile: GameProfile, bundle_map: dict[int, str] | None) -> bytes:
    """预览用当前客户端/工程内资源，不用 store 备份（备份可能是旧版或已换形后的块）。"""
    bm = bundle_map or scan_bundle_map(profile)
    return get_anim_chunk(anim_id, bm, profile)


def get_head_image(anim_id: int, profile: GameProfile = LOCAL) -> Image.Image:
    cached = head_png_path(anim_id, profile)
    if cached.exists():
        return Image.open(cached).convert("RGBA")
    if not has_pethead_sprite(anim_id, profile):
        raise RuntimeError(f"无 pethead 头像 sprite: {anim_id}")
    if not pethead_bundle_path(profile).exists():
        raise RuntimeError(f"pethead 包不存在 [{profile.label}]")
    _raw, _off, env = load_pethead_bundle(pethead_bundle_path(profile))
    return sprite_image(env, str(anim_id))


def get_texture_image_from_bundle(bundle_path: Path) -> Image.Image:
    """读取 bundle 内主图集 Texture2D（完整贴图）。"""
    _raw, _off, env = load_bundle_env(bundle_path)
    textures: list[tuple[int, Image.Image]] = []
    for obj in env.objects:
        if obj.type.name != "Texture2D":
            continue
        tex = obj.read()
        img = tex.image.convert("RGBA")
        textures.append((tex.m_Width * tex.m_Height, img))
    if not textures:
        raise RuntimeError(f"动画包内无 Texture2D: {bundle_path}")
    textures.sort(key=lambda x: x[0], reverse=True)
    main = textures[0][1]
    if len(textures) == 1:
        return main
    thumb_w, thumb_h = 256, 256
    cols = min(3, len(textures))
    rows = (len(textures) + cols - 1) // cols
    grid = Image.new("RGBA", (cols * thumb_w, rows * thumb_h), (32, 32, 32, 255))
    for i, (_area, img) in enumerate(textures):
        t = img.copy()
        t.thumbnail((thumb_w - 8, thumb_h - 8), Image.Resampling.LANCZOS)
        x = (i % cols) * thumb_w + (thumb_w - t.width) // 2
        y = (i // cols) * thumb_h + (thumb_h - t.height) // 2
        grid.paste(t, (x, y), t)
    return grid


def get_battle_texture_image(anim_id: int, profile: GameProfile = LOCAL) -> Image.Image:
    bp = _resolve_bundle(anim_id, profile, None)
    return get_texture_image_from_bundle(bp)


def get_battle_texture_image_from_path(bundle_file: Path) -> Image.Image:
    return get_texture_image_from_bundle(bundle_file)


def get_battle_animation_frames_from_paths(
    bundle_file: Path,
    chunk: bytes,
    anim_id: int,
    profile: GameProfile = LOCAL,
    *,
    use_walk: bool = False,
) -> tuple[list[Image.Image], AnimClip, AnimMeta]:
    """从指定 bundle + animdata 块预览（外部形象库等）。"""
    clips, meta = parse_animdata(chunk)
    clip = pick_walk_clip(clips) if use_walk else pick_stand_clip(clips)
    if not clip:
        raise RuntimeError(f"animdata 无可用动画片段: {anim_id}")
    _raw, _off, env = load_bundle_env(bundle_file)
    slices: list[FrameSlice] = []
    missing = 0
    for sid in clip.frame_sprites:
        sl = extract_sprite_frame(env, sid)
        if sl is None:
            missing += 1
            ph = Image.new("RGBA", (64, 64), (255, 0, 255, 128))
            slices.append(FrameSlice(ph, 32.0, 32.0))
        else:
            slices.append(sl)
    if missing == len(clip.frame_sprites):
        raise RuntimeError(f"图集中找不到动画帧 sprite（anim_id={anim_id}）")
    coords = frame_coord_offsets(anim_id, clip, slices, profile)
    frames = compose_frames_with_coord(slices, coords)
    return frames, clip, meta


def get_battle_animation_frames(
    anim_id: int,
    profile: GameProfile = LOCAL,
    *,
    use_walk: bool = False,
) -> tuple[list[Image.Image], AnimClip, AnimMeta]:
    chunk = _resolve_anim_chunk(anim_id, profile, None)
    clips, meta = parse_animdata(chunk)
    clip = pick_walk_clip(clips) if use_walk else pick_stand_clip(clips)
    if not clip:
        raise RuntimeError(f"animdata 无可用动画片段: {anim_id}")
    bp = _resolve_bundle(anim_id, profile, None)
    _raw, _off, env = load_bundle_env(bp)
    slices: list[FrameSlice] = []
    missing = 0
    for sid in clip.frame_sprites:
        sl = extract_sprite_frame(env, sid)
        if sl is None:
            missing += 1
            ph = Image.new("RGBA", (64, 64), (255, 0, 255, 128))
            slices.append(FrameSlice(ph, 32.0, 32.0))
        else:
            slices.append(sl)
    if missing == len(clip.frame_sprites):
        raise RuntimeError(f"图集中找不到动画帧 sprite（anim_id={anim_id}）")
    coords = frame_coord_offsets(anim_id, clip, slices, profile)
    frames = compose_frames_with_coord(slices, coords)
    return frames, clip, meta


def get_effect_animation_frames(
    anim_id: int,
    profile: GameProfile = LOCAL,
    *,
    clip_index: int = 0,
    bundle_path: Path | None = None,
    anim_chunk: bytes | None = None,
) -> tuple[list[Image.Image], AnimClip, AnimMeta]:
    """技能特效帧：UnityPy Sprite.image 切图（与游戏一致）。"""
    chunk = anim_chunk if anim_chunk is not None else _resolve_anim_chunk(anim_id, profile, None)
    clips, meta = parse_animdata(chunk)
    clip = pick_first_clip(clips, clip_index)
    if not clip:
        raise RuntimeError(f"animdata 无可用特效片段: {anim_id}")
    bp = bundle_path if bundle_path is not None else _resolve_bundle(anim_id, profile, None)
    _raw, _off, env = load_bundle_env(bp)
    frames: list[Image.Image] = []
    missing = 0
    for sid in clip.frame_sprites:
        img = extract_sprite(env, sid)
        if img is None:
            missing += 1
            frames.append(Image.new("RGBA", (64, 64), (255, 0, 255, 128)))
        else:
            frames.append(img)
    if missing == len(clip.frame_sprites):
        raise RuntimeError(f"图集中找不到特效帧 sprite（anim_id={anim_id}）")
    return frames, clip, meta


def frame_interval_ms(clip: AnimClip) -> int:
    if clip.frame_count <= 0:
        return 200
    if clip.duration > 0:
        return max(50, int(clip.duration / clip.frame_count))
    return 120


def ensure_head_cached(anim_id: int, profile: GameProfile) -> None:
    if head_png_path(anim_id, profile).exists():
        return
    if has_pethead_sprite(anim_id, profile):
        extract_head_png(anim_id, profile, log=lambda _m: None)
