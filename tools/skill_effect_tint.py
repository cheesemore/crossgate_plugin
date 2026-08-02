#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""技能特效序列帧 HSV 调色与 bundle 写回。"""
from __future__ import annotations

import colorsys
import json
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

import UnityPy
from PIL import Image

from swap_pet_head import crop_sprite_from_atlas, paste_sprite_to_atlas, sprite_rect, texture_object, write_atlas_image


@dataclass
class TintParams:
    mode: str = "hsv"  # "hsv" | "rgb" | "advanced" | "black_coat"
    hue_shift: float = 0.0  # 度 -180..180
    saturation: float = 1.0  # 倍率 0..2
    value: float = 1.0  # 明度倍率 0..2
    red: float = 1.0  # RGB 通道倍率 0..2
    green: float = 1.0
    blue: float = 1.0
    # 黑色涂装：高饱和区域压暗去色，低饱和高光保留
    coat_sat_floor: float = 0.30
    coat_sat_preserve: float = 0.18
    coat_dark_max: float = 0.18
    coat_warm_hue_only: bool = False
    coat_black_to_gold: bool = False
    coat_keep_hue: bool = False
    coat_hue_keep: float = 0.35
    # 高级模式：仅对满足 RGBA 区间的像素做 HSV（None=该端不限制）
    lock_r_min: int | None = None
    lock_r_max: int | None = None
    lock_g_min: int | None = None
    lock_g_max: int | None = None
    lock_b_min: int | None = None
    lock_b_max: int | None = None
    lock_a_min: int | None = None
    lock_a_max: int | None = None

    def normalized(self) -> TintParams:
        mode_raw = str(self.mode).lower()
        if mode_raw == "advanced":
            mode = "advanced"
        elif mode_raw == "rgb":
            mode = "rgb"
        elif mode_raw in ("black_coat", "blackcoat", "black"):
            mode = "black_coat"
        else:
            mode = "hsv"

        def bound(v: int | None) -> int | None:
            if v is None:
                return None
            return max(0, min(255, int(v)))

        def unit(v: float) -> float:
            return max(0.0, min(1.0, float(v)))

        preserve = unit(self.coat_sat_preserve)
        floor = max(preserve, unit(self.coat_sat_floor))

        return TintParams(
            mode=mode,
            hue_shift=max(-180.0, min(180.0, float(self.hue_shift))),
            saturation=max(0.0, min(2.0, float(self.saturation))),
            value=max(0.0, min(2.0, float(self.value))),
            red=max(0.0, min(2.0, float(self.red))),
            green=max(0.0, min(2.0, float(self.green))),
            blue=max(0.0, min(2.0, float(self.blue))),
            coat_sat_floor=floor,
            coat_sat_preserve=preserve,
            coat_dark_max=unit(self.coat_dark_max),
            coat_warm_hue_only=bool(self.coat_warm_hue_only),
            coat_black_to_gold=bool(self.coat_black_to_gold),
            coat_keep_hue=bool(self.coat_keep_hue),
            coat_hue_keep=unit(self.coat_hue_keep),
            lock_r_min=bound(self.lock_r_min),
            lock_r_max=bound(self.lock_r_max),
            lock_g_min=bound(self.lock_g_min),
            lock_g_max=bound(self.lock_g_max),
            lock_b_min=bound(self.lock_b_min),
            lock_b_max=bound(self.lock_b_max),
            lock_a_min=bound(self.lock_a_min),
            lock_a_max=bound(self.lock_a_max),
        )


def is_neutral_tint(params: TintParams | None) -> bool:
    if params is None:
        return True
    p = params.normalized()
    if p.mode == "black_coat":
        return False
    if p.mode == "rgb":
        return p.red == 1.0 and p.green == 1.0 and p.blue == 1.0
    if p.hue_shift == 0 and p.saturation == 1.0 and p.value == 1.0:
        return True
    return False


def has_lock_filter(params: TintParams) -> bool:
    p = params.normalized()
    return any(
        v is not None
        for v in (
            p.lock_r_min,
            p.lock_r_max,
            p.lock_g_min,
            p.lock_g_max,
            p.lock_b_min,
            p.lock_b_max,
            p.lock_a_min,
            p.lock_a_max,
        )
    )


def pixel_matches_lock(r: int, g: int, b: int, a: int, params: TintParams) -> bool:
    p = params.normalized()
    checks = (
        (p.lock_r_min, r, lambda v, x: x >= v),
        (p.lock_r_max, r, lambda v, x: x <= v),
        (p.lock_g_min, g, lambda v, x: x >= v),
        (p.lock_g_max, g, lambda v, x: x <= v),
        (p.lock_b_min, b, lambda v, x: x >= v),
        (p.lock_b_max, b, lambda v, x: x <= v),
        (p.lock_a_min, a, lambda v, x: x >= v),
        (p.lock_a_max, a, lambda v, x: x <= v),
    )
    for bound, channel, ok in checks:
        if bound is not None and not ok(bound, channel):
            return False
    return True


def format_lock_summary(params: TintParams) -> str:
    p = params.normalized()
    parts: list[str] = []

    def span(ch: str, lo: int | None, hi: int | None) -> None:
        if lo is None and hi is None:
            return
        if lo is not None and hi is not None:
            parts.append(f"{ch}[{lo}-{hi}]")
        elif lo is not None:
            parts.append(f"{ch}≥{lo}")
        else:
            parts.append(f"{ch}≤{hi}")

    span("R", p.lock_r_min, p.lock_r_max)
    span("G", p.lock_g_min, p.lock_g_max)
    span("B", p.lock_b_min, p.lock_b_max)
    span("A", p.lock_a_min, p.lock_a_max)
    return " ".join(parts)


def format_tint_summary(params: TintParams) -> str:
    p = params.normalized()
    if is_neutral_tint(p) and not (p.mode == "advanced" and has_lock_filter(p)):
        return ""
    lock = format_lock_summary(p) if p.mode == "advanced" and has_lock_filter(p) else ""
    if p.mode == "black_coat":
        warm = " · 仅暖色" if p.coat_warm_hue_only else ""
        extras: list[str] = []
        if p.coat_black_to_gold:
            extras.append("纯黑→暗金")
        if p.coat_keep_hue:
            extras.append(f"留色 {p.coat_hue_keep * 100:.0f}%")
        extra = f" · {' · '.join(extras)}" if extras else ""
        body = (
            f"黑装: 涂装 S≥{p.coat_sat_floor * 100:.0f}%"
            f" · 保留 S<{p.coat_sat_preserve * 100:.0f}%"
            f" · 深度 V≤{p.coat_dark_max * 100:.0f}%{warm}{extra}"
        )
    elif p.mode == "rgb":
        body = f"RGB: R={p.red * 100:.0f}% G={p.green * 100:.0f}% B={p.blue * 100:.0f}%"
    elif p.mode == "advanced":
        body = f"HSV: H={p.hue_shift:.0f}° S={p.saturation * 100:.0f}% V={p.value * 100:.0f}%"
    else:
        body = f"HSV: H={p.hue_shift:.0f}° S={p.saturation * 100:.0f}% V={p.value * 100:.0f}%"
    if lock:
        return f"锁定 {lock} | {body}"
    return body


def alpha_channel_unchanged(src: Image.Image, out: Image.Image) -> bool:
    """校验调色前后逐像素 Alpha 完全一致（含半透明像素）。"""
    s = src.convert("RGBA")
    o = out.convert("RGBA")
    if s.size != o.size:
        return False
    spx, opx = s.load(), o.load()
    for y in range(s.height):
        for x in range(s.width):
            if spx[x, y][3] != opx[x, y][3]:
                return False
    return True


def apply_rgb_to_image(img: Image.Image, params: TintParams) -> Image.Image:
    """对 RGBA 图像做 RGB 通道倍率调整；Alpha 原样保留，a=0 像素不参与。"""
    p = params.normalized()
    if p.mode != "rgb" or is_neutral_tint(p):
        return img.copy()

    src = img.convert("RGBA")
    out = Image.new("RGBA", src.size)
    spx = src.load()
    opx = out.load()
    for y in range(src.height):
        for x in range(src.width):
            r, g, b, a = spx[x, y]
            if a == 0:
                opx[x, y] = (0, 0, 0, 0)
                continue
            r2 = min(255, max(0, int(r * p.red)))
            g2 = min(255, max(0, int(g * p.green)))
            b2 = min(255, max(0, int(b * p.blue)))
            opx[x, y] = (r2, g2, b2, a)
    return out


def _apply_hsv_to_rgb(r: int, g: int, b: int, params: TintParams) -> tuple[int, int, int]:
    p = params.normalized()
    hue_delta = p.hue_shift / 360.0
    h, s, v = colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
    h = (h + hue_delta) % 1.0
    s = min(1.0, max(0.0, s * p.saturation))
    v = min(1.0, max(0.0, v * p.value))
    r2, g2, b2 = colorsys.hsv_to_rgb(h, s, v)
    return int(r2 * 255), int(g2 * 255), int(b2 * 255)


def apply_advanced_hsv_to_image(img: Image.Image, params: TintParams) -> Image.Image:
    """高级模式：仅对 RGBA 落在锁定区间的像素做 HSV，Alpha 不变。"""
    p = params.normalized()
    if p.mode != "advanced" or is_neutral_tint(p):
        return img.copy()
    if not has_lock_filter(p):
        return apply_hsv_to_image(img, p)

    src = img.convert("RGBA")
    out = src.copy()
    px = list(src.getdata())
    out_px: list[tuple[int, int, int, int]] = []
    for r, g, b, a in px:
        if pixel_matches_lock(r, g, b, a, p):
            r2, g2, b2 = _apply_hsv_to_rgb(r, g, b, p)
            out_px.append((r2, g2, b2, a))
        else:
            out_px.append((r, g, b, a))
    out.putdata(out_px)
    return out


def apply_hsv_to_image(img: Image.Image, params: TintParams) -> Image.Image:
    """对 RGBA 图像做 HSV 调整；Alpha 原样保留，a=0 像素不参与。"""
    p = params.normalized()
    if p.mode != "hsv" or is_neutral_tint(p):
        return img.copy()

    src = img.convert("RGBA")
    out = Image.new("RGBA", src.size)
    spx = src.load()
    opx = out.load()
    hue_delta = p.hue_shift / 360.0

    for y in range(src.height):
        for x in range(src.width):
            r, g, b, a = spx[x, y]
            if a == 0:
                opx[x, y] = (0, 0, 0, 0)
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
            h = (h + hue_delta) % 1.0
            s = min(1.0, max(0.0, s * p.saturation))
            v = min(1.0, max(0.0, v * p.value))
            r2, g2, b2 = colorsys.hsv_to_rgb(h, s, v)
            opx[x, y] = (int(r2 * 255), int(g2 * 255), int(b2 * 255), a)
    return out


def _black_coat_params_from_tint(p: TintParams):
    from black_coat_preview import BlackCoatParams

    return BlackCoatParams(
        sat_floor=p.coat_sat_floor,
        sat_preserve=p.coat_sat_preserve,
        dark_val_max=p.coat_dark_max,
        warm_hue_only=p.coat_warm_hue_only,
        black_to_gold=p.coat_black_to_gold,
        keep_original_hue=p.coat_keep_hue,
        hue_keep_ratio=p.coat_hue_keep,
    )


def apply_black_coat_to_image(img: Image.Image, params: TintParams) -> Image.Image:
    from black_coat_preview import apply_black_coat

    p = params.normalized()
    if p.mode != "black_coat":
        return img.copy()
    return apply_black_coat(img, _black_coat_params_from_tint(p))


def apply_tint_to_image(img: Image.Image, params: TintParams) -> Image.Image:
    p = params.normalized()
    if p.mode == "black_coat":
        return apply_black_coat_to_image(img, p)
    if p.mode == "advanced":
        return apply_advanced_hsv_to_image(img, p)
    if p.mode == "rgb":
        return apply_rgb_to_image(img, p)
    return apply_hsv_to_image(img, p)


def apply_tint_to_frames(frames: list[Image.Image], params: TintParams) -> list[Image.Image]:
    return [apply_tint_to_image(f, params) for f in frames]


def apply_hsv_to_frames(frames: list[Image.Image], params: TintParams) -> list[Image.Image]:
    return apply_tint_to_frames(frames, params)


def sprite_ids_in_chunk(chunk: bytes) -> set[int]:
    from pet_preview import parse_animdata

    clips, _ = parse_animdata(chunk)
    ids: set[int] = set()
    for clip in clips:
        ids.update(sid for sid in clip.frame_sprites if sid > 0)
    return ids


def _apply_tint_to_env(env, sprite_ids: set[int], params: TintParams) -> None:
    """按图集 textureRect 写回，禁止缩放。"""
    p = params.normalized()
    by_tex: dict[int, list[tuple[int, int, int, int]]] = {}
    for sid in sorted(sprite_ids):
        rect = sprite_rect(env, str(sid))
        by_tex.setdefault(rect["tex_path"], []).append(
            (rect["x"], rect["y"], rect["w"], rect["h"])
        )
    if not by_tex:
        raise RuntimeError("图集中找不到任何 sprite 区域")
    for tex_path, regions in by_tex.items():
        tex_obj = texture_object(env, tex_path)
        tex = tex_obj.read()
        atlas = tex.image.convert("RGBA")
        for x, y, w, h in regions:
            region = crop_sprite_from_atlas(atlas, x, y, w, h)
            tinted = apply_tint_to_image(region, p)
            if tinted.size != (w, h):
                raise RuntimeError(f"调色后尺寸变化: {tinted.size} != {(w, h)}")
            paste_sprite_to_atlas(atlas, tinted, x, y, w, h, clear=True)
        write_atlas_image(tex_obj, atlas)


def _verify_bundle_body(bundle_raw: bytes, unity_off: int) -> None:
    env = UnityPy.load(bundle_raw[unity_off:])
    for obj in env.objects:
        if obj.type.name == "TextAsset" and obj.read().m_Name == "animdata":
            return
    raise RuntimeError("调色后 bundle 校验失败：缺少 animdata")


def apply_tint_to_bundle(
    bundle_path: Path,
    sprite_ids: set[int],
    params: TintParams,
    *,
    chunk: bytes | None = None,
) -> TintParams:
    """将 HSV 调色烘焙进 bundle 图集（图集像素写回 + lz4 + 校验）。"""
    from pet_preview import load_bundle_env

    p = params.normalized()
    if chunk is not None:
        sprite_ids = sprite_ids or sprite_ids_in_chunk(chunk)
    if not sprite_ids:
        raise RuntimeError("没有可调色的 sprite")

    raw, off, env = load_bundle_env(bundle_path)
    _apply_tint_to_env(env, sprite_ids, p)
    out = raw[:off] + env.file.save(packer="lz4")
    _verify_bundle_body(out, off)
    bundle_path.write_bytes(out)
    return p


def apply_tint_to_bundle_bytes(
    bundle_bytes: bytes,
    sprite_ids: set[int],
    params: TintParams,
) -> bytes:
    """内存中调色 bundle，供 pet_anim_manager 换形使用。"""
    if is_neutral_tint(params):
        return bundle_bytes
    if not sprite_ids:
        raise RuntimeError("没有可调色的 sprite")
    off = bundle_bytes.find(b"UnityFS")
    if off < 0:
        raise RuntimeError("bundle 缺少 UnityFS")
    env = UnityPy.load(bundle_bytes[off:])
    _apply_tint_to_env(env, sprite_ids, params.normalized())
    out = bundle_bytes[:off] + env.file.save(packer="lz4")
    _verify_bundle_body(out, off)
    return out


def save_tint_meta(dest_dir: Path, anim_id: int, params: TintParams, sprite_ids: set[int]) -> None:
    dest_dir.mkdir(parents=True, exist_ok=True)
    data = {
        "anim_id": anim_id,
        "params": asdict(params.normalized()),
        "sprite_ids": sorted(sprite_ids),
        "updated_at": datetime.now(timezone.utc).isoformat(),
    }
    (dest_dir / "tint.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def _self_check_alpha_preservation() -> None:
    img = Image.new("RGBA", (4, 4), (0, 0, 0, 0))
    img.putpixel((1, 1), (200, 100, 50, 255))
    img.putpixel((2, 1), (200, 100, 50, 128))
    for mode, params in (
        ("hsv", TintParams(mode="hsv", hue_shift=90, saturation=1.5, value=0.7)),
        ("rgb", TintParams(mode="rgb", red=1.5, green=0.8, blue=1.2)),
    ):
        out = apply_tint_to_image(img, params)
        if out.getpixel((0, 0))[3] != 0:
            raise RuntimeError(f"{mode}: 全透明像素 alpha 被改变")
        if out.getpixel((1, 1))[3] != 255 or out.getpixel((2, 1))[3] != 128:
            raise RuntimeError(f"{mode}: 不透明/半透明 alpha 被改变")
        if not alpha_channel_unchanged(img, out):
            raise RuntimeError(f"{mode}: alpha 通道与源图不一致")

    adv = Image.new("RGBA", (4, 4), (0, 0, 0, 0))
    adv.putpixel((1, 0), (200, 50, 50, 255))
    adv.putpixel((2, 0), (50, 200, 50, 255))
    adv.putpixel((3, 0), (200, 50, 50, 5))
    adv_r = apply_advanced_hsv_to_image(
        adv,
        TintParams(mode="advanced", hue_shift=90, saturation=1.5, value=0.8, lock_r_min=129),
    )
    if adv_r.getpixel((1, 0)) == adv.getpixel((1, 0)):
        raise RuntimeError("advanced: 高 R 像素未调色")
    if adv_r.getpixel((2, 0)) != adv.getpixel((2, 0)):
        raise RuntimeError("advanced: 低 R 像素被误调色")
    adv_a = apply_advanced_hsv_to_image(
        adv,
        TintParams(mode="advanced", hue_shift=90, saturation=1.5, value=0.8, lock_a_max=9),
    )
    if adv_a.getpixel((3, 0)) == adv.getpixel((3, 0)):
        raise RuntimeError("advanced: 低 A 像素未调色")
    if adv_a.getpixel((1, 0)) != adv.getpixel((1, 0)):
        raise RuntimeError("advanced: 高 A 像素被误调色")
    if not alpha_channel_unchanged(adv, adv_r) or not alpha_channel_unchanged(adv, adv_a):
        raise RuntimeError("advanced: alpha 被改变")


def load_tint_meta(dest_dir: Path) -> tuple[TintParams, set[int]] | None:
    path = dest_dir / "tint.json"
    if not path.exists():
        return None
    data = json.loads(path.read_text(encoding="utf-8"))
    raw = data.get("params", {})
    if "mode" not in raw:
        raw = {**raw, "mode": "hsv"}
    known = {f.name for f in TintParams.__dataclass_fields__.values()}
    p = TintParams(**{k: v for k, v in raw.items() if k in known})
    ids = set(int(x) for x in data.get("sprite_ids", []))
    return p, ids


if __name__ == "__main__":
    _self_check_alpha_preservation()
    print("alpha preservation OK (HSV + RGB)")
