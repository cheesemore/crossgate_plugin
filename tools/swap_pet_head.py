#!/usr/bin/env python3
"""
Swap pet head icons: 使魔 (type 8) <- 魔龙德拉贡 (type 68 / sprite 130068).

Patches:
  - headitem atlas sprite 252007 (staticpic id 252008 for type 8)
  - pethead atlas sprite 130008 (130000 + type 8)

Usage:
  python tools/swap_pet_head.py          # dry-run
  python tools/swap_pet_head.py --apply  # backup + write bundles
"""
import argparse
import shutil
from pathlib import Path

import UnityPy
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "cross_Data" / "assets"
BACKUP = Path(__file__).resolve().parent / "backup"

# 使魔 = pet type 8
# 魔龙德拉贡 = 巨龙 type 68 -> pethead sprite 130068
SRC_SPRITE = "130068"
DST_HEADITEM = "252007"   # staticpic 252008 -> headitem/252007.png
DST_PETHEAD = "130008"    # 130000 + 8

HEADITEM_BUNDLE = "683a4606cbe385330b8429fcd827d612"
PETHEAD_BUNDLE = "44abe811fc38e1970cd5aff01377e142"


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


def atlas_lookup(env: UnityPy.Environment) -> dict:
    keymap = {}
    for obj in env.objects:
        if obj.type.name != "SpriteAtlas":
            continue
        for key, data in obj.read().m_RenderDataMap:
            tr = data.textureRect
            keymap[key] = {
                "x": int(tr.x),
                "y": int(tr.y),
                "w": int(tr.width),
                "h": int(tr.height),
                "tex_path": data.texture.path_id,
            }
    return keymap


def normalize_path_id(path_id: int) -> int:
    """Unity path_id 与 JSON 无符号整数互转。"""
    path_id = int(path_id)
    if path_id > 2**63 - 1:
        return path_id - 2**64
    return path_id


def unity_rect_to_pil_box(x: int, y: int, w: int, h: int, tex_height: int) -> tuple[int, int, int, int]:
    """SpriteAtlas textureRect（Unity 左下原点 y）→ PIL 左上原点 crop/paste 框。"""
    y_top = int(tex_height - y - h)
    return int(x), y_top, int(x + w), y_top + int(h)


def crop_sprite_from_atlas(atlas: Image.Image, x: int, y: int, w: int, h: int) -> Image.Image:
    box = unity_rect_to_pil_box(x, y, w, h, atlas.height)
    return atlas.crop(box).copy()


RGBA32_FORMAT = 4  # UnityEngine.TextureFormat.RGBA32


def write_atlas_image(tex_obj, atlas: Image.Image) -> None:
    """写回图集像素；改为 RGBA32 避免 BC7 有损重压缩导致回写偏差。"""
    tex = tex_obj.read()
    tex.m_TextureFormat = RGBA32_FORMAT
    tex.image = atlas.convert("RGBA")
    tex.save()


def paste_sprite_to_atlas(
    atlas: Image.Image,
    img: Image.Image,
    x: int,
    y: int,
    w: int,
    h: int,
    *,
    clear: bool = True,
) -> None:
    x0, y0, x1, y1 = unity_rect_to_pil_box(x, y, w, h, atlas.height)
    if img.size != (w, h):
        raise ValueError(f"sprite size {img.size} != {(w, h)}")
    if clear:
        atlas.paste(Image.new("RGBA", (w, h), (0, 0, 0, 0)), (x0, y0))
    atlas.paste(img.convert("RGBA"), (x0, y0))


def sprite_rect(env: UnityPy.Environment, name: str) -> dict:
    keymap = atlas_lookup(env)
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.read().m_Name == name:
            rect = keymap.get(obj.read().m_RenderDataKey)
            if rect is None:
                raise RuntimeError(f"sprite {name} not found in SpriteAtlas render map")
            return rect
    raise RuntimeError(f"sprite {name} not found")


def sprite_image(env: UnityPy.Environment, name: str) -> Image.Image:
    for obj in env.objects:
        if obj.type.name == "Sprite" and obj.read().m_Name == name:
            return obj.read().image.convert("RGBA")
    raise RuntimeError(f"sprite {name} not found")


def texture_object(env: UnityPy.Environment, path_id: int):
    pid = normalize_path_id(path_id)
    for obj in env.objects:
        if obj.type.name == "Texture2D" and obj.path_id == pid:
            return obj
    raise RuntimeError(f"Texture2D path_id={path_id} not found")


def paste_into_atlas(
    env: UnityPy.Environment,
    src_img: Image.Image,
    dst_sprite: str,
) -> dict:
    rect = sprite_rect(env, dst_sprite)
    tex_obj = texture_object(env, rect["tex_path"])
    tex = tex_obj.read()
    atlas = tex.image.convert("RGBA")
    resized = src_img.resize((rect["w"], rect["h"]), Image.Resampling.LANCZOS)
    paste_sprite_to_atlas(atlas, resized, rect["x"], rect["y"], rect["w"], rect["h"])
    write_atlas_image(tex_obj, atlas)
    return rect


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="Perform replacement")
    args = parser.parse_args()

    headitem_path = ASSETS / f"{HEADITEM_BUNDLE}.b"
    pethead_path = ASSETS / f"{PETHEAD_BUNDLE}.b"

    print("使魔头像目标:")
    print(f"  headitem sprite {DST_HEADITEM} -> {headitem_path.name}")
    print(f"  pethead sprite {DST_PETHEAD} -> {pethead_path.name}")
    print("魔龙德拉贡头像源:")
    print(f"  pethead sprite {SRC_SPRITE}")

    pet_raw, pet_off, pet_env = load_bundle(pethead_path)
    dragon = sprite_image(pet_env, SRC_SPRITE)
    print(f"源图尺寸: {dragon.size}")

    head_raw, head_off, head_env = load_bundle(headitem_path)

    head_rect = paste_into_atlas(head_env, dragon, DST_HEADITEM)
    print(f"headitem 已合成到图集区域 x={head_rect['x']} y={head_rect['y']} "
          f"w={head_rect['w']} h={head_rect['h']}")

    pet_rect = paste_into_atlas(pet_env, dragon, DST_PETHEAD)
    print(f"pethead 已合成到图集区域 x={pet_rect['x']} y={pet_rect['y']} "
          f"w={pet_rect['w']} h={pet_rect['h']}")

    if not args.apply:
        print("\n预览模式，未写入。确认后执行:")
        print("  python tools/swap_pet_head.py --apply")
        return

    BACKUP.mkdir(parents=True, exist_ok=True)
    for bundle_id, src_path in [
        (HEADITEM_BUNDLE, headitem_path),
        (PETHEAD_BUNDLE, pethead_path),
    ]:
        backup_file = BACKUP / f"{bundle_id}.b.orig"
        if not backup_file.exists():
            shutil.copy2(src_path, backup_file)
            print("已备份:", backup_file)

    save_bundle(head_raw, head_off, head_env, headitem_path)
    save_bundle(pet_raw, pet_off, pet_env, pethead_path)
    print("已写入:", headitem_path)
    print("已写入:", pethead_path)
    print("请重启游戏，查看使魔头像是否显示为魔龙德拉贡。")


if __name__ == "__main__":
    main()
