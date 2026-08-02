#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""RGBA 预览：透明区域用棋盘格显示，供 tkinter PhotoImage 使用。"""
from __future__ import annotations

from PIL import Image

_CHECKER_LIGHT = (0x55, 0x55, 0x55)
_CHECKER_DARK = (0x3A, 0x3A, 0x3A)


def make_checkerboard(width: int, height: int, cell: int = 8) -> Image.Image:
    base = Image.new("RGB", (width, height))
    px = base.load()
    for y in range(height):
        for x in range(width):
            color = _CHECKER_LIGHT if ((x // cell) + (y // cell)) % 2 == 0 else _CHECKER_DARK
            px[x, y] = color
    return base


def composite_rgba_on_checkerboard(img: Image.Image, cell: int = 8) -> Image.Image:
    """将 RGBA 叠到棋盘格上；alpha=0 区域显示为透明（棋盘格）。"""
    src = img.convert("RGBA")
    bg = make_checkerboard(src.width, src.height, cell)
    bg.paste(src, (0, 0), src)
    return bg


def scale_nearest(img: Image.Image, max_w: int, max_h: int) -> Image.Image:
    w, h = img.size
    scale = min(max_w / w, max_h / h, 1.0)
    if scale >= 1.0:
        return img.copy()
    nw = max(1, int(w * scale))
    nh = max(1, int(h * scale))
    return img.resize((nw, nh), Image.Resampling.NEAREST)


def frame_for_tk_preview(
    img: Image.Image,
    *,
    canvas_w: int = 360,
    canvas_h: int = 360,
    max_inner: int = 340,
) -> Image.Image:
    """居中缩放 RGBA 帧，透明底叠在棋盘格上（PhotoImage 可靠显示）。"""
    src = img.convert("RGBA")
    scaled = scale_nearest(src, max_inner, max_inner)
    sw, sh = scaled.size
    canvas = make_checkerboard(canvas_w, canvas_h)
    overlay = Image.new("RGBA", (canvas_w, canvas_h), (0, 0, 0, 0))
    overlay.paste(scaled, ((canvas_w - sw) // 2, (canvas_h - sh) // 2), scaled)
    canvas.paste(overlay, (0, 0), overlay)
    return canvas


def preview_layout_metrics(
    img: Image.Image,
    *,
    canvas_w: int = 360,
    canvas_h: int = 360,
    max_inner: int = 340,
) -> tuple[float, int, int, int, int]:
    """返回 scale, off_x, off_y, src_w, src_h（与 frame_for_tk_preview 一致）。"""
    src = img.convert("RGBA")
    w, h = src.size
    scale = min(max_inner / w, max_inner / h, 1.0)
    if scale >= 1.0:
        sw, sh = w, h
    else:
        sw = max(1, int(w * scale))
        sh = max(1, int(h * scale))
    off_x = (canvas_w - sw) // 2
    off_y = (canvas_h - sh) // 2
    return scale, off_x, off_y, w, h


def canvas_point_to_image_pixel(
    canvas_x: int,
    canvas_y: int,
    img: Image.Image,
    *,
    canvas_w: int = 360,
    canvas_h: int = 360,
    max_inner: int = 340,
) -> tuple[int, int] | None:
    """画布坐标 → 源图像像素；点在透明边距外返回 None。"""
    scale, off_x, off_y, w, h = preview_layout_metrics(
        img, canvas_w=canvas_w, canvas_h=canvas_h, max_inner=max_inner
    )
    sw = w if scale >= 1.0 else max(1, int(w * scale))
    sh = h if scale >= 1.0 else max(1, int(h * scale))
    rel_x = canvas_x - off_x
    rel_y = canvas_y - off_y
    if rel_x < 0 or rel_y < 0 or rel_x >= sw or rel_y >= sh:
        return None
    if scale >= 1.0:
        ix, iy = rel_x, rel_y
    else:
        ix = int(rel_x / scale)
        iy = int(rel_y / scale)
    return max(0, min(w - 1, ix)), max(0, min(h - 1, iy))


def sample_rgba_at_canvas_point(
    canvas_x: int,
    canvas_y: int,
    img: Image.Image,
    *,
    canvas_w: int = 360,
    canvas_h: int = 360,
    max_inner: int = 340,
) -> tuple[int, int, int, int] | None:
    pt = canvas_point_to_image_pixel(
        canvas_x, canvas_y, img, canvas_w=canvas_w, canvas_h=canvas_h, max_inner=max_inner
    )
    if pt is None:
        return None
    return img.convert("RGBA").getpixel(pt)
