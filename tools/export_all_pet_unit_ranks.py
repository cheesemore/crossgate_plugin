# -*- coding: utf-8 -*-
"""解包全部宠物单位档位（Luban 顺序解析 pet_tbcommenemybaseconfig）。

总档位 = ETBASE体力+力量+强度+速度+魔法 + 10（同图鉴 TotleNum / PetInfoTemplatePanel）
单维上限 = 对应 ETBASE + 2
"""
from __future__ import annotations

import csv
import json
from collections import Counter
from pathlib import Path

# 默认读从 crosscopy 提取到 cross/tools/_config_extract 的表（勿用 crossgate_cursor）
_TOOLS = Path(__file__).resolve().parent
_EXTRACT = _TOOLS / "_config_extract" / "excelgeneral" / "pet_tbcommenemybaseconfig.bytes"
CONFIG = (
    _EXTRACT
    if _EXTRACT.is_file()
    else _TOOLS / "_config_extract" / "excelgeneral" / "pet_tbcommenemybaseconfig"
)
OUT_DIR = _TOOLS
OUT_JSON = OUT_DIR / "宠物单位档位全表.json"
OUT_CSV = OUT_DIR / "宠物单位档位全表.csv"
OUT_CSV_BOOK = OUT_DIR / "宠物单位档位_图鉴.csv"
OUT_MD = OUT_DIR / "宠物单位档位全表.md"


def read_size(data: bytes, pos: int) -> tuple[int, int]:
    h = data[pos]
    pos += 1
    if h < 0x80:
        return h, pos
    if h < 0xC0:
        return ((h & 0x3F) << 8) | data[pos], pos + 1
    if h < 0xE0:
        return ((h & 0x1F) << 16) | (data[pos] << 8) | data[pos + 1], pos + 2
    if h < 0xF0:
        return (
            ((h & 0x0F) << 24)
            | (data[pos] << 16)
            | (data[pos + 1] << 8)
            | data[pos + 2]
        ), pos + 3
    return (
        (data[pos] << 24)
        | (data[pos + 1] << 16)
        | (data[pos + 2] << 8)
        | data[pos + 3]
    ), pos + 4


def read_int(data: bytes, pos: int) -> tuple[int, int]:
    """Luban compact int → 有符号 32 位（0xFFFFFFFF → -1）。"""
    v, pos = read_size(data, pos)
    if v >= 0x80000000:
        v -= 0x100000000
    return v, pos


def is_sane_base(v: int) -> bool:
    return 0 <= v <= 80


def read_string(data: bytes, pos: int) -> tuple[str, int]:
    n, pos = read_size(data, pos)
    if n < 0:
        raise ValueError("bad string len")
    return data[pos : pos + n].decode("utf-8"), pos + n


INT_FIELDS = [
    "ETTEMPNO",
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
]


def rare_label(rare: int) -> str:
    # 与旧导出不完全一致；保留原始 ETRARE，标签仅作参考
    if rare == 0:
        return "普卡/低"
    if rare == 1:
        return "中"
    if rare == 2:
        return "银卡?"
    if rare == 3:
        return "金卡?"
    if 4 <= rare <= 9:
        return "稀有"
    if rare >= 10 or rare > 1000:
        return "特殊/BOSS"
    return f"未知({rare})"


def load_all(path: Path) -> list[dict]:
    data = path.read_bytes()
    cnt, pos = read_size(data, 0)
    rows: list[dict] = []
    for _ in range(cnt):
        name, pos = read_string(data, pos)
        rec: dict = {"ETNAME": name}
        for k in INT_FIELDS:
            v, pos = read_int(data, pos)
            rec[k] = v
        for i in range(1, 11):
            v, pos = read_int(data, pos)
            rec[f"ETPETSKILL{i}"] = v
        for k in (
            "ETOLDBASEVITAL",
            "ETOLDBASESTR",
            "ETOLDBASETGH",
            "ETOLDBASEQUICK",
            "ETOLDBASEMAGIC",
        ):
            v, pos = read_int(data, pos)
            rec[k] = v
        rows.append(rec)
    if pos != len(data):
        raise RuntimeError(f"parse trailing junk: pos={pos} len={len(data)}")
    return rows


def main() -> None:
    raw = load_all(CONFIG)
    # DataMap 以 ETTEMPNO 为键；同 temp 后者覆盖前者（与客户端一致用首次也可）
    by_temp: dict[int, dict] = {}
    for r in raw:
        by_temp[r["ETTEMPNO"]] = r

    out_rows = []
    for temp, r in by_temp.items():
        vit = r["ETBASEVITAL"]
        stre = r["ETBASESTR"]
        tgh = r["ETBASETGH"]
        quick = r["ETBASEQUICK"]
        magic = r["ETBASEMAGIC"]
        bases = [vit, stre, tgh, quick, magic]
        sane = all(is_sane_base(x) for x in bases)
        total = vit + stre + tgh + quick + magic + 10 if sane else None
        album = r["ETALBUMNO"]
        in_book = 1 <= album <= 500
        out_rows.append(
            {
                "name": r["ETNAME"],
                "tempNo": temp,
                "albumNo": album if in_book else None,
                "albumRaw": album,
                "inHandbook": in_book,
                "rankSane": sane,
                "cardTierHint": rare_label(r["ETRARE"]),
                "ETRARE": r["ETRARE"],
                "baseVital": vit,
                "baseStr": stre,
                "baseTgh": tgh,
                "baseQuick": quick,
                "baseMagic": magic,
                "totalRank": total if total is not None else "",
                "rankDetail": (
                    f"体{vit}+力{stre}+强{tgh}+速{quick}+魔{magic}+10={total}"
                    if sane
                    else f"体{vit}/力{stre}/强{tgh}/速{quick}/魔{magic}（非标准档位/哨兵）"
                ),
                "capVital": vit + 2 if sane else "",
                "capStr": stre + 2 if sane else "",
                "capTgh": tgh + 2 if sane else "",
                "capQuick": quick + 2 if sane else "",
                "capMagic": magic + 2 if sane else "",
                "capDetail": (
                    f"体{vit+2}/力{stre+2}/强{tgh+2}/速{quick+2}/魔{magic+2}"
                    if sane
                    else ""
                ),
                "fixMaxHp": r["ETFIXMAXHP"],
                "tribe": r["ETTRIBE"],
                "petTypes": r["PetTypes"],
                "imgNumber": r["ETIMGNUMBER"],
                "slot": r["ETSLOT"],
            }
        )

    def sort_key(x):
        tr = x["totalRank"]
        score = tr if isinstance(tr, int) else -1
        return (-score, x["name"], x["tempNo"])

    out_rows.sort(key=sort_key)
    tier_counts = Counter(r["cardTierHint"] for r in out_rows)
    handbook_n = sum(1 for r in out_rows if r["inHandbook"])
    sane_n = sum(1 for r in out_rows if r["rankSane"])
    book_sane = [r for r in out_rows if r["inHandbook"] and r["rankSane"]]

    payload = {
        "note": (
            "来源: pet_tbcommenemybaseconfig（Luban 顺序解析，记录数与文件尾对齐）。\n"
            "总档位 = ETBASEVITAL+STR+TGH+QUICK+MAGIC+10。\n"
            "单维上限 = 各维 ETBASE+2。\n"
            "卡片档位 hint 仅参考 ETRARE 粗分；图鉴普/银/金请再对 book 表。\n"
            "个体洗档当前值不在本表。"
        ),
        "source": str(CONFIG),
        "summary": {
            "rawRecords": len(raw),
            "uniqueTempNo": len(out_rows),
            "saneRankPets": sane_n,
            "handbookPets": handbook_n,
            "handbookSanePets": len(book_sane),
            "tierHintCounts": dict(tier_counts),
        },
        "pets": out_rows,
    }
    OUT_JSON.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    headers = [
        "名称",
        "tempNo",
        "图鉴No",
        "在图鉴",
        "档位有效",
        "总档位",
        "体",
        "力",
        "强",
        "速",
        "魔",
        "档位明细",
        "上限体",
        "上限力",
        "上限强",
        "上限速",
        "上限魔",
        "上限明细",
        "ETRARE",
        "稀有度提示",
        "种族",
        "PetTypes",
        "形象号",
        "固定HP",
    ]

    def write_csv(path: Path, rows: list[dict]) -> None:
        with path.open("w", encoding="utf-8-sig", newline="") as f:
            w = csv.writer(f)
            w.writerow(headers)
            for r in rows:
                w.writerow(
                    [
                        r["name"],
                        r["tempNo"],
                        r["albumNo"] or "",
                        "是" if r["inHandbook"] else "否",
                        "是" if r["rankSane"] else "否",
                        r["totalRank"],
                        r["baseVital"],
                        r["baseStr"],
                        r["baseTgh"],
                        r["baseQuick"],
                        r["baseMagic"],
                        r["rankDetail"],
                        r["capVital"],
                        r["capStr"],
                        r["capTgh"],
                        r["capQuick"],
                        r["capMagic"],
                        r["capDetail"],
                        r["ETRARE"],
                        r["cardTierHint"],
                        r["tribe"],
                        r["petTypes"],
                        r["imgNumber"],
                        r["fixMaxHp"],
                    ]
                )

    write_csv(OUT_CSV, out_rows)
    write_csv(OUT_CSV_BOOK, book_sane)

    sane_sorted = [r for r in out_rows if r["rankSane"]]
    lines = [
        "# 宠物单位档位全表",
        "",
        payload["note"].replace("\n", "  \n"),
        "",
        f"- 原始记录：**{len(raw)}**",
        f"- 唯一 tempNo：**{len(out_rows)}**",
        f"- 五维档位有效（0~80）：**{sane_n}**",
        f"- 图鉴内：**{handbook_n}**（其中档位有效 **{len(book_sane)}**）",
        f"- 源文件：`{CONFIG}`",
        "",
        "## 总档位 TOP 40（仅有效档位）",
        "",
        "| 名称 | tempNo | 图鉴 | 总档 | 明细 |",
        "|------|--------|------|------|------|",
    ]
    for r in sane_sorted[:40]:
        lines.append(
            f"| {r['name']} | {r['tempNo']} | {r['albumNo'] or '-'} | "
            f"{r['totalRank']} | {r['rankDetail']} |"
        )
    lines += [
        "",
        f"- 全表：`{OUT_CSV.name}`",
        f"- 图鉴有效档：`{OUT_CSV_BOOK.name}`",
        f"- JSON：`{OUT_JSON.name}`",
    ]
    OUT_MD.write_text("\n".join(lines), encoding="utf-8")

    print("raw", len(raw), "unique", len(out_rows), "sane", sane_n, "bookSane", len(book_sane))
    print("TOP8 sane:")
    for r in sane_sorted[:8]:
        print(" ", r["totalRank"], r["name"], r["tempNo"], r["rankDetail"])
    print("WROTE", OUT_CSV)
    print("WROTE", OUT_CSV_BOOK)
    print("WROTE", OUT_JSON)
    print("WROTE", OUT_MD)


if __name__ == "__main__":
    main()
