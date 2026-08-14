# -*- coding: utf-8 -*-
"""导出本地宠物形象表 pet_appear.bin（对齐 pet_rank.bin 用法）。

来源：
  - pet_tbcommenemybaseconfig → 名字 / tempNo / album / tribe / ETIMGNUMBER
  - pet_tbpefectpetmatconfig → 可满档材质（主表；有行即可开 Perfectpet）
  - pet_tbpefectpetskinconfig → 满档换皮（极少行）
  - other_tbridepetskinconfig → 人物坐骑皮肤（另存 ride_skin.csv/json）
  - pet_tbridepetskinconfig → 骑宠皮肤（另存 ride_skin.csv/json）
  - pet_tbpetmaxcresteffectconfig → 满档纹章特效（另存）

PAR1 仅含 perfectSkinId；can_perfect / perfect_mat 写在 csv/json。
"""
from __future__ import annotations

import csv
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from export_all_pet_unit_ranks import (  # noqa: E402
    load_all,
    read_int,
    read_size,
    read_string,
)
from seqchapter_paths import (  # noqa: E402
    CONFIG_EXCELGENERAL,
    CONFIG_EXCELGENERAL_L,
    CONFIG_EXTRACT,
)

TOOLS = Path(__file__).resolve().parent

OUT_BIN = TOOLS / "pet_appear.bin"
OUT_CSV = TOOLS / "pet_appear.csv"
OUT_JSON = TOOLS / "pet_appear.json"
OUT_RIDE_CSV = TOOLS / "ride_skin.csv"
OUT_RIDE_JSON = TOOLS / "ride_skin.json"
OUT_CREST_CSV = TOOLS / "pet_max_crest.csv"
OUT_CREST_JSON = TOOLS / "pet_max_crest.json"


def _resolve(name: str) -> Path:
    """只使用从 crosscopy 提取到 cross/tools/_config_extract 的表。"""
    for base in (CONFIG_EXCELGENERAL, CONFIG_EXCELGENERAL_L, CONFIG_EXTRACT):
        for cand in (base / f"{name}.bytes", base / name):
            if cand.is_file() and cand.stat().st_size > 0:
                return cand
    raise FileNotFoundError(
        f"找不到配置: {name}\n请先运行: python tools/extract_seqchapter_configs.py"
    )


def load_perfect_skin(path: Path) -> dict[int, int]:
    """换皮表（极少行）：原形象 → 满档 SkinId。"""
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    out: dict[int, int] = {}
    for _ in range(cnt):
        aid, pos = read_int(data, pos)
        sid, pos = read_int(data, pos)
        out[aid] = sid
    if pos != len(data):
        raise RuntimeError(f"perfect skin trailing junk pos={pos} len={len(data)}")
    return out


def load_perfect_mat(path: Path) -> dict[int, dict]:
    """满档材质表（主来源）：形象 Id → Material / Flag。有此行即可开 Perfectpet 满档特效。"""
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    out: dict[int, dict] = {}
    for _ in range(cnt):
        aid, pos = read_int(data, pos)
        mat, pos = read_string(data, pos)
        flag, pos = read_int(data, pos)
        out[aid] = {"material": mat, "flag": flag}
    if pos != len(data):
        raise RuntimeError(f"perfect mat trailing junk pos={pos} len={len(data)}")
    return out


def load_ride_skins_char(path: Path) -> list[dict]:
    """人物坐骑皮肤 other_tbridepetskinconfig → 战斗 RideSkin 配置 Id。"""
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    rows: list[dict] = []
    for _ in range(cnt):
        rid, pos = read_int(data, pos)
        grano, pos = read_int(data, pos)
        time_v, pos = read_int(data, pos)
        icon, pos = read_int(data, pos)
        name, pos = read_string(data, pos)
        memo, pos = read_string(data, pos)
        gorun, pos = read_int(data, pos)
        rows.append(
            {
                "kind": "char",
                "id": rid,
                "grano": grano,
                "time": time_v,
                "icon": icon,
                "name": name,
                "memo": memo,
                "gorun": gorun,
            }
        )
    if pos != len(data):
        raise RuntimeError(f"char ride trailing junk pos={pos} len={len(data)}")
    return rows


def load_ride_skins_pet(path: Path) -> list[dict]:
    """骑宠皮肤 pet_tbridepetskinconfig（按反编译 RidePetSkinConfig 顺序解析）。

    结构：Id, Grano, Time, Name, Memo, GoRun, Petid, Quality,
          Cost(count × {Type, Id, Count}), Cond(count × int)
    """
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    rows: list[dict] = []
    for _ in range(cnt):
        rid, pos = read_int(data, pos)
        grano, pos = read_int(data, pos)
        time_v, pos = read_int(data, pos)
        name, pos = read_string(data, pos)
        memo, pos = read_string(data, pos)
        gorun, pos = read_int(data, pos)
        petid, pos = read_int(data, pos)
        quality, pos = read_int(data, pos)
        costn, pos = read_size(data, pos)
        for _ in range(costn):
            _, pos = read_int(data, pos)  # Type
            _, pos = read_int(data, pos)  # Id
            _, pos = read_int(data, pos)  # Count
        condn, pos = read_size(data, pos)
        for _ in range(condn):
            _, pos = read_int(data, pos)
        rows.append(
            {
                "kind": "pet_skin",
                "id": rid,
                "grano": grano,
                "time": time_v,
                "icon": 0,
                "name": name,
                "memo": memo,
                "gorun": gorun,
                "petid": petid,
                "quality": quality,
            }
        )
    if pos != len(data):
        raise RuntimeError(f"pet ride trailing junk pos={pos} len={len(data)}")
    return rows


def load_all_ride_rows() -> list[dict]:
    """合并人物坐骑皮肤 + 骑宠皮肤（序章人物坐骑表本身只有十余条）。"""
    rows: list[dict] = []
    try:
        rows.extend(load_ride_skins_char(_resolve("other_tbridepetskinconfig")))
    except FileNotFoundError:
        pass
    try:
        rows.extend(load_ride_skins_pet(_resolve("pet_tbridepetskinconfig")))
    except FileNotFoundError:
        pass
    # 稳定排序：人物皮肤在前，再按 id
    rows.sort(key=lambda r: (0 if r.get("kind") == "char" else 1, int(r.get("id") or 0)))
    return rows


# 兼容旧名
def load_ride_skins(path: Path) -> list[dict]:
    return load_ride_skins_char(path)


def _try_string(data: bytes, pos: int) -> tuple[str, int] | None:
    try:
        n, p = read_size(data, pos)
        if n < 1 or n > 256 or p + n > len(data):
            return None
        s = data[p : p + n].decode("utf-8")
        return s, p + n
    except Exception:
        return None


def load_crest(path: Path) -> list[dict]:
    """满档光环表。序章版在 Id 与 Name 之间多一个 compact int，需跳过。"""
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    rows: list[dict] = []
    for _ in range(cnt):
        cid, pos = read_int(data, pos)
        got = _try_string(data, pos)
        if got is None:
            _, pos = read_int(data, pos)  # 序章额外字段
            got = _try_string(data, pos)
        if got is None:
            raise RuntimeError(f"crest name parse fail id={cid} pos={pos}")
        name, pos = got
        nattr, pos = read_string(data, pos)
        vattr, pos = read_int(data, pos)
        effect, pos = read_int(data, pos)
        rows.append(
            {
                "id": cid,
                "name": name,
                "nattr": nattr,
                "vattr": vattr,
                "effect": effect,
            }
        )
    if pos != len(data):
        raise RuntimeError(f"crest trailing junk pos={pos} len={len(data)}")
    return rows


def build_appear_rows(
    enemy_rows: list[dict],
    perfect_skin: dict[int, int],
    perfect_mat: dict[int, dict],
) -> list[dict]:
    """同形象 ID 合并名字；优先保留图鉴号更小、名字更「正常」的主名。"""
    by_anim: dict[int, dict] = {}
    for r in enemy_rows:
        anim = int(r["ETIMGNUMBER"])
        if anim <= 0:
            continue
        name = str(r.get("ETNAME") or "").strip()
        if not name:
            continue
        temp = int(r["ETTEMPNO"])
        album = int(r["ETALBUMNO"])
        tribe = int(r["ETTRIBE"])
        mat = perfect_mat.get(anim) or {}
        cur = by_anim.get(anim)
        if cur is None:
            by_anim[anim] = {
                "name": name,
                "aliases": [name],
                "temp_no": temp,
                "album_no": album,
                "tribe": tribe,
                "anim_id": anim,
                "perfect_skin_id": int(perfect_skin.get(anim, 0)),
                "can_perfect": 1 if anim in perfect_mat else 0,
                "perfect_mat": str(mat.get("material") or ""),
                "perfect_mat_flag": int(mat.get("flag") or 0),
            }
            continue
        if name not in cur["aliases"]:
            cur["aliases"].append(name)
        prefer = (
            (name.startswith("BOSS") or name.startswith("Ｂ")),
            album if album > 0 else 10**9,
            temp,
            name,
        )
        cur_pref = (
            (cur["name"].startswith("BOSS") or cur["name"].startswith("Ｂ")),
            cur["album_no"] if cur["album_no"] > 0 else 10**9,
            cur["temp_no"],
            cur["name"],
        )
        if prefer < cur_pref:
            cur["name"] = name
            cur["temp_no"] = temp
            cur["album_no"] = album
            cur["tribe"] = tribe
        if cur["perfect_skin_id"] == 0 and anim in perfect_skin:
            cur["perfect_skin_id"] = int(perfect_skin[anim])
        if not cur.get("can_perfect") and anim in perfect_mat:
            cur["can_perfect"] = 1
            cur["perfect_mat"] = str(mat.get("material") or "")
            cur["perfect_mat_flag"] = int(mat.get("flag") or 0)
    rows = sorted(by_anim.values(), key=lambda x: (x["album_no"] or 10**9, x["anim_id"], x["name"]))
    return rows


def write_par1(rows: list[dict], path: Path) -> None:
    buf = bytearray()
    buf += b"PAR1"
    buf += struct.pack("<i", len(rows))
    for r in rows:
        nb = r["name"].encode("utf-8")
        if len(nb) > 65535:
            continue
        buf += struct.pack("<H", len(nb))
        buf += nb
        buf += struct.pack(
            "<iihii",
            int(r["temp_no"]),
            int(r["album_no"]),
            max(-32768, min(32767, int(r["tribe"]))),
            int(r["anim_id"]),
            int(r["perfect_skin_id"]),
        )
    path.write_bytes(buf)


def main() -> None:
    enemy_path = _resolve("pet_tbcommenemybaseconfig")
    perfect_path = _resolve("pet_tbpefectpetskinconfig")
    perfect_mat_path = _resolve("pet_tbpefectpetmatconfig")
    crest_path = _resolve("pet_tbpetmaxcresteffectconfig")
    print(f"source enemy={enemy_path}")
    print(f"source crest={crest_path}")

    enemy = load_all(enemy_path)
    perfect = load_perfect_skin(perfect_path)
    perfect_mat = load_perfect_mat(perfect_mat_path)
    rows = build_appear_rows(enemy, perfect, perfect_mat)
    write_par1(rows, OUT_BIN)

    with OUT_CSV.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(
            [
                "name",
                "aliases",
                "temp_no",
                "album_no",
                "tribe",
                "anim_id",
                "can_perfect",
                "perfect_mat",
                "perfect_mat_flag",
                "perfect_skin_id",
            ]
        )
        for r in rows:
            w.writerow(
                [
                    r["name"],
                    "|".join(r["aliases"]),
                    r["temp_no"],
                    r["album_no"],
                    r["tribe"],
                    r["anim_id"],
                    r.get("can_perfect", 0),
                    r.get("perfect_mat", ""),
                    r.get("perfect_mat_flag", 0),
                    r["perfect_skin_id"],
                ]
            )
    OUT_JSON.write_text(
        json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    rides = load_all_ride_rows()
    with OUT_RIDE_CSV.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(
            f,
            fieldnames=[
                "kind", "id", "grano", "time", "icon", "name", "memo", "gorun",
                "petid", "quality",
            ],
        )
        w.writeheader()
        w.writerows(rides)
    OUT_RIDE_JSON.write_text(
        json.dumps(rides, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    n_char = sum(1 for r in rides if r.get("kind") == "char")
    n_pet = sum(1 for r in rides if r.get("kind") == "pet_skin")
    print(f"rides total={len(rides)} char_skin={n_char} pet_skin={n_pet}")

    crests = load_crest(crest_path)
    with OUT_CREST_CSV.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["id", "name", "nattr", "vattr", "effect"])
        w.writeheader()
        w.writerows(crests)
    OUT_CREST_JSON.write_text(
        json.dumps(crests, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    can_n = sum(1 for r in rows if r.get("can_perfect"))
    print(
        f"pets={len(rows)} can_perfect={can_n} skin_remap={len(perfect)} "
        f"mat={len(perfect_mat)} bin={OUT_BIN} bytes={OUT_BIN.stat().st_size}"
    )
    print(f"rides={len(rides)} -> {OUT_RIDE_CSV.name}")
    print(f"crests={len(crests)} -> {OUT_CREST_CSV.name}")
    print("note: 满档主看 peffectmat（材质）；换皮表 peffectskin 极少")


if __name__ == "__main__":
    main()
