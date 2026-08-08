#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""物理伤害试算：中值 × 种族克制 × 属性克制（不算魔法）。

用法示例：
  python physical_damage.py --demo
  python physical_damage.py --atk 220 --def 180 --lv 75 --foe-lv 75 \\
      --my-tribe 0 --foe-tribe 6 --my-elem 0,0,0,10 --foe-elem 10,0,0,0
  python physical_damage.py --name 火焰翼龙 --level 30 --hp 1200 --mp 400 \\
      --atk 200 --def 160 --lv 30 --my-elem 0,10,0,0
"""
from __future__ import annotations

import argparse
import math
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

from boss_stat_estimator import estimate_enemy, load_rank_table, lookup
from export_all_pet_unit_ranks import CONFIG, load_all

# 0~9 与 monitor / ETTRIBE 一致
TRIBE_NAMES = (
    "人形",
    "龙",
    "不死",
    "飞行",
    "昆虫",
    "植物",
    "野兽",
    "特殊",
    "金属",
    "邪魔",
)

# 全克链（攻击方 → 被克方）：+20 / 反向 -20
FULL_CHAIN_NEXT = {
    0: 6,  # 人形 → 野兽
    6: 5,  # 野兽 → 植物
    5: 2,  # 植物 → 不死
    2: 0,  # 不死 → 人形
    1: 3,  # 龙 → 飞行
    3: 4,  # 飞行 → 昆虫
    4: 7,  # 昆虫 → 特殊
    7: 8,  # 特殊 → 金属
    8: 1,  # 金属 → 龙
}

# 半克链：+10 / 反向 -15
HALF_CHAIN_NEXT = {
    0: 4,  # 人形 → 昆虫
    4: 5,  # 昆虫 → 植物
    5: 7,  # 植物 → 特殊
    7: 1,  # 特殊 → 龙
    1: 6,  # 龙 → 野兽
    6: 3,  # 野兽 → 飞行
    3: 2,  # 飞行 → 不死
    2: 8,  # 不死 → 金属
    8: 0,  # 金属 → 人形
}

THRESHOLD = 241


@dataclass
class UnitCombat:
    name: str
    level: int
    atk: int
    defence: int  # panel def
    tribe: int
    # 地水火风格（合计通常 10；敌方配置常为合计 100，调用方先 /10）
    earth: float
    water: float
    fire: float
    wind: float
    weapon: float = 1.15  # 持武；空手 1.0


def decay241(v: float, threshold: int = THRESHOLD) -> float:
    if v <= threshold:
        return float(v)
    return threshold + (v - threshold) * 0.3


def mid_damage(atk: float, defence: float) -> float:
    a = decay241(atk)
    d = decay241(defence)
    if d >= 0:
        return (a * a) / (a / 3.0 + d) if (a / 3.0 + d) > 0 else 1.0
    return (a * a) / (a / 3.0) - d


def race_bonus_pct(atk_tribe: int, def_tribe: int) -> int:
    """返回种族修正百分点（可加到 100 上）。"""
    if atk_tribe < 0 or def_tribe < 0:
        return 0
    # 邪魔
    if atk_tribe == 9 and def_tribe != 9:
        return 10
    if def_tribe == 9 and atk_tribe != 9:
        return -20
    if atk_tribe == 9 and def_tribe == 9:
        return 0

    if FULL_CHAIN_NEXT.get(atk_tribe) == def_tribe:
        return 20
    if FULL_CHAIN_NEXT.get(def_tribe) == atk_tribe:
        return -20
    if HALF_CHAIN_NEXT.get(atk_tribe) == def_tribe:
        return 10
    if HALF_CHAIN_NEXT.get(def_tribe) == atk_tribe:
        return -15
    return 0


def element_bonus_pct(
    ae: float,
    aw: float,
    af: float,
    ai: float,
    de: float,
    dw: float,
    df: float,
    di: float,
) -> float:
    """属性修正百分点（系数 0.3 × 克制格积差）。"""
    pos = ae * dw + aw * df + af * di + ai * de
    neg = aw * de + af * dw + ai * df + ae * di
    return 0.3 * (pos - neg)


def physical_hit(
    attacker: UnitCombat,
    defender: UnitCombat,
    *,
    skill: float = 1.0,
    float_lo: float = 0.9,
    float_hi: float = 1.1,
) -> Dict[str, float]:
    base = mid_damage(attacker.atk, defender.defence)
    race = race_bonus_pct(attacker.tribe, defender.tribe)
    elem = element_bonus_pct(
        attacker.earth,
        attacker.water,
        attacker.fire,
        attacker.wind,
        defender.earth,
        defender.water,
        defender.fire,
        defender.wind,
    )
    mult = (100.0 + race + elem) / 100.0
    mid = base * mult * attacker.weapon * skill
    return {
        "mid_raw": base,
        "race_pct": race,
        "elem_pct": elem,
        "weapon": attacker.weapon,
        "skill": skill,
        "mid": mid,
        "lo": mid * float_lo,
        "hi": mid * float_hi,
    }


def attrs_to_grids(earth: int, water: int, fire: int, wind: int) -> Tuple[float, float, float, float]:
    """配置常见合计 100 → 折成 10 格；若已是 ≤10 量级则原样。"""
    s = earth + water + fire + wind
    if s <= 0:
        return 0.0, 0.0, 0.0, 0.0
    if s > 20:  # 百分制
        return earth / 10.0, water / 10.0, fire / 10.0, wind / 10.0
    return float(earth), float(water), float(fire), float(wind)


def load_enemy_meta() -> Dict[str, dict]:
    """name → {tribe, earth, water, fire, wind, tempNo, bases}；同名保留总档更高。"""
    rows = load_all(CONFIG)
    best: Dict[str, dict] = {}
    for r in rows:
        name = r["ETNAME"]
        bases = (
            r["ETBASEVITAL"],
            r["ETBASESTR"],
            r["ETBASETGH"],
            r["ETBASEQUICK"],
            r["ETBASEMAGIC"],
        )
        cur = {
            "tribe": int(r["ETTRIBE"]),
            "earth": int(r["ETEARTHAT"]),
            "water": int(r["ETWATERAT"]),
            "fire": int(r["ETFIREAT"]),
            "wind": int(r["ETWINDAT"]),
            "tempNo": int(r["ETTEMPNO"]),
            "bases": bases,
            "total": sum(bases),
        }
        prev = best.get(name)
        if prev is None or cur["total"] > prev["total"]:
            best[name] = cur
    return best


def parse_elem(s: str) -> Tuple[float, float, float, float]:
    parts = [float(x.strip()) for x in s.split(",")]
    if len(parts) != 4:
        raise SystemExit("--my-elem/--foe-elem 需要 地,水,火,风 四个数")
    return parts[0], parts[1], parts[2], parts[3]


def fmt_hit(title: str, h: Dict[str, float]) -> str:
    return (
        f"{title}: 中值原始={h['mid_raw']:.1f} 种族{h['race_pct']:+.0f}% "
        f"属性{h['elem_pct']:+.1f}% 武器×{h['weapon']} → "
        f"中值={h['mid']:.1f} 区间[{h['lo']:.1f}, {h['hi']:.1f}]"
    )


def run_demo() -> None:
    meta = load_enemy_meta()
    print("=== Demo1：公式自洽（人形纯风 打 野兽纯地）===")
    me = UnitCombat("我(人形风)", 80, 300, 200, 0, 0, 0, 0, 10, 1.15)
    foe = UnitCombat("敌(野兽地)", 70, 250, 250, 6, 10, 0, 0, 0, 1.0)
    print(fmt_hit("我→敌", physical_hit(me, foe)))
    print(fmt_hit("敌→我", physical_hit(foe, me)))
    # 风克地 → +30%；人形打野兽全克 +20% → 合计 +50%
    print("  期望：我→敌 属性约+30、种族+20；敌→我 属性-30、种族-20")

    print("\n=== Demo2：表内怪 火焰翼龙（估属性后互殴）===")
    name = "火焰翼龙"
    m = meta.get(name)
    if not m:
        print("  未找到", name)
        return
    rows = load_rank_table()
    hits = lookup(rows, name=name)
    # 挑总档最高
    pet = max(hits, key=lambda r: sum(r.bases)) if hits else None
    if pet is None:
        print("  档位表无此名")
        return
    eg, wg, fg, ig = attrs_to_grids(m["earth"], m["water"], m["fire"], m["wind"])
    # 合成观测血魔再反推（倍率 20 满档附近）
    from boss_stat_estimator import COEFF_MID, bp_from, calc_seven

    bp = bp_from(pet.bases, [1, 1, 1, 1, 1], 40, 20, COEFF_MID)
    seven = calc_seven(bp)
    er = estimate_enemy(pet, 40, seven["hp"], seven["mp"])
    foe2 = UnitCombat(
        name,
        40,
        er.est["atk"],
        er.est["def"],
        m["tribe"],
        eg,
        wg,
        fg,
        ig,
        1.0,
    )
    # 我方：人形、纯水水晶（克火）
    me2 = UnitCombat("我(人形水)", 40, 180, 160, 0, 0, 10, 0, 0, 1.15)
    print(
        f"  {name} tribe={TRIBE_NAMES[m['tribe']]} "
        f"elem格=地{eg:.1f}/水{wg:.1f}/火{fg:.1f}/风{ig:.1f} "
        f"EST atk={foe2.atk} def={foe2.defence} rate={er.rate}"
    )
    print(fmt_hit("我→龙", physical_hit(me2, foe2)))
    print(fmt_hit("龙→我", physical_hit(foe2, me2)))

    print("\n=== Demo3：盖美拉类（若表内有）互殴骨架 ===")
    for cand in ("喷火兽盖美拉", "盖美拉"):
        if cand in meta:
            m3 = meta[cand]
            eg, wg, fg, ig = attrs_to_grids(m3["earth"], m3["water"], m3["fire"], m3["wind"])
            print(
                f"  {cand}: tribe={TRIBE_NAMES[m3['tribe']]} "
                f"rawE/W/F/Wi={m3['earth']}/{m3['water']}/{m3['fire']}/{m3['wind']} "
                f"→格 {eg:.1f}/{wg:.1f}/{fg:.1f}/{ig:.1f}"
            )
            # 均属性 30*4 时克制积差为 0
            me3 = UnitCombat("我(人形无属性)", 75, 220, 200, 0, 0, 0, 0, 0, 1.15)
            foe3 = UnitCombat(cand, 75, 240, 220, m3["tribe"], eg, wg, fg, ig, 1.0)
            print(fmt_hit("我→盖", physical_hit(me3, foe3)))
            print(fmt_hit("盖→我", physical_hit(foe3, me3)))
            break
    else:
        print("  未找到盖美拉条目")


def main() -> None:
    ap = argparse.ArgumentParser(description="物理伤害（含种族/属性克制）试算")
    ap.add_argument("--demo", action="store_true")
    ap.add_argument("--atk", type=int, help="我方攻击")
    ap.add_argument("--def", dest="defence", type=int, help="我方防御")
    ap.add_argument("--lv", type=int, default=1, help="我方等级")
    ap.add_argument("--foe-atk", type=int, default=None)
    ap.add_argument("--foe-def", type=int, default=None)
    ap.add_argument("--foe-lv", type=int, default=None)
    ap.add_argument("--my-tribe", type=int, default=0)
    ap.add_argument("--foe-tribe", type=int, default=0)
    ap.add_argument("--my-elem", type=str, default="0,0,0,0", help="地,水,火,风 格")
    ap.add_argument("--foe-elem", type=str, default="0,0,0,0")
    ap.add_argument("--my-weapon", type=float, default=1.15)
    ap.add_argument("--foe-weapon", type=float, default=1.0)
    ap.add_argument("--name", type=str, default=None, help="敌方名（查表+可选估属性）")
    ap.add_argument("--level", type=int, default=None, help="敌方等级（与 --name 联用）")
    ap.add_argument("--hp", type=int, default=None)
    ap.add_argument("--mp", type=int, default=None)
    args = ap.parse_args()

    if args.demo:
        run_demo()
        return

    if args.atk is None or args.defence is None:
        ap.error("请提供 --atk/--def，或使用 --demo")

    me_e = parse_elem(args.my_elem)
    foe_tribe = args.foe_tribe
    foe_e = parse_elem(args.foe_elem)
    foe_atk = args.foe_atk
    foe_def = args.foe_def
    foe_lv = args.foe_lv or args.lv
    foe_name = "敌方"

    if args.name:
        meta = load_enemy_meta()
        m = meta.get(args.name)
        if m is None:
            # 模糊
            hits = [k for k in meta if args.name in k]
            if not hits:
                raise SystemExit(f"配置中无名称含 {args.name}")
            m = meta[hits[0]]
            foe_name = hits[0]
            print(f"[提示] 使用 {foe_name}")
        else:
            foe_name = args.name
        foe_tribe = m["tribe"]
        foe_e = attrs_to_grids(m["earth"], m["water"], m["fire"], m["wind"])
        if args.hp is not None and args.mp is not None and args.level is not None:
            rows = load_rank_table()
            pets = lookup(rows, name=foe_name)
            if pets:
                pet = max(pets, key=lambda r: sum(r.bases))
                er = estimate_enemy(pet, args.level, args.hp, args.mp)
                foe_atk = er.est["atk"]
                foe_def = er.est["def"]
                foe_lv = args.level
                print(
                    f"[EST] {foe_name} lv{args.level} rate={er.rate} "
                    f"atk={foe_atk} def={foe_def} fit={er.fit}"
                )

    if foe_atk is None or foe_def is None:
        raise SystemExit("请提供 --foe-atk/--foe-def，或 --name + --level/--hp/--mp")

    me = UnitCombat("我方", args.lv, args.atk, args.defence, args.my_tribe, *me_e, args.my_weapon)
    foe = UnitCombat(foe_name, foe_lv, foe_atk, foe_def, foe_tribe, *foe_e, args.foe_weapon)
    print(
        f"我: lv{me.level} atk{me.atk} def{me.defence} "
        f"{TRIBE_NAMES[me.tribe]} 格({me.earth},{me.water},{me.fire},{me.wind})"
    )
    print(
        f"敌: lv{foe.level} atk{foe.atk} def{foe.defence} "
        f"{TRIBE_NAMES[foe.tribe]} 格({foe.earth},{foe.water},{foe.fire},{foe.wind})"
    )
    print(fmt_hit("我→敌", physical_hit(me, foe)))
    print(fmt_hit("敌→我", physical_hit(foe, me)))


if __name__ == "__main__":
    main()
