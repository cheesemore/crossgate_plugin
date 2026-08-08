# -*- coding: utf-8 -*-
"""野生/BOSS：档位 → 倍率锁定 → 血魔区间 → 反推攻防敏精神回复。

链路（与 Coze 文档一致）:
  档位 → BP = (档+随机档) × [系系数×(等级-1) + 倍率/100]
  BP → 七维（宠物系数表）

倍率锁定（用户约定）:
  - 倍率 ≥20，步长优先 10，其次 5
  - 先试 20；整体偏低（观测 > 上限）则倍增 40→80→…
  - 偏高则在 (lo, hi) 内二分；优先锁 10 的倍数，锁不住再锁 5

区间（简化）:
  - 上限：满档 + 0系(0.045) + 10 随机全堆有利维
  - 下限：每项掉4（共20）+ 5系(0.040) + 10 随机全堆不利维
"""
from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parent
RANK_JSON = ROOT / "宠物单位档位全表.json"
SLIM_CSV = ROOT / "pet_rank_slim.csv"

# BP → 七维（宠物）
BP_TO_SEVEN = {
    "hp": {"body": 8.0, "str": 2.0, "pow": 3.0, "spd": 3.0, "mag": 1.0, "base": 20},
    "mp": {"body": 1.0, "str": 2.0, "pow": 2.0, "spd": 2.0, "mag": 10.0, "base": 20},
    "atk": {"body": 0.1, "str": 2.0, "pow": 0.2, "spd": 0.2, "mag": 0.1, "base": 20},
    "def": {"body": 0.1, "str": 0.2, "pow": 3.0, "spd": 0.2, "mag": 0.1, "base": 20},
    "agi": {"body": 0.1, "str": 0.2, "pow": 0.2, "spd": 2.0, "mag": 0.1, "base": 20},
    "spirit": {"body": -0.3, "str": -0.1, "pow": 0.2, "spd": -0.1, "mag": 0.8, "base": 100},
    "rec": {"body": 0.8, "str": -0.1, "pow": -0.1, "spd": 0.2, "mag": -0.3, "base": 100},
}

KEYS = ("body", "str", "pow", "spd", "mag")
COEFF_MAX = 0.045  # 0系
COEFF_MIN = 0.040  # 5系
COEFF_MID = 0.0425
RATE_MIN = 20
RATE_MAX = 640
RANDOM_TOTAL = 10


@dataclass
class PetRank:
    name: str
    temp_no: int
    img: int
    bases: Tuple[int, int, int, int, int]  # 体力强速魔
    rank_sane: bool = True


@dataclass
class StatBounds:
    hp_min: int
    hp_max: int
    mp_min: int
    mp_max: int


@dataclass
class EstimateResult:
    pet: PetRank
    level: int
    obs_hp: int
    obs_mp: int
    rate: int
    rate_step: int  # 10 or 5
    fit: bool
    bounds: StatBounds
    drops: Tuple[int, int, int, int, int]  # 体力强速魔各掉几档(0~4)
    match_pen: int  # |ΔHP|+|ΔMP|
    drop_t: float  # 兼容：1 - sum(drops)/20
    est: Dict[str, int]
    atk_range: Tuple[int, int]
    def_range: Tuple[int, int]
    agi_range: Tuple[int, int]
    spirit_range: Tuple[int, int]
    rec_range: Tuple[int, int]
    note: str = ""


def calc_seven(bp: Dict[str, float]) -> Dict[str, int]:
    out = {}
    for stat, coeff in BP_TO_SEVEN.items():
        val = coeff["base"]
        for k in KEYS:
            val += bp[k] * coeff[k]
        out[stat] = int(round(val))
    return out


def factor(level: int, rate: int, coeff: float) -> float:
    return coeff * (level - 1) + rate / 100.0


def grades_full(bases: Sequence[int]) -> List[int]:
    return [int(x) for x in bases]


def grades_drop20(bases: Sequence[int]) -> List[int]:
    return [max(0, int(x) - 4) for x in bases]


def bp_from(grades: Sequence[int], random_alloc: Sequence[int], level: int, rate: int, coeff: float) -> Dict[str, float]:
    f = factor(level, rate, coeff)
    return {KEYS[i]: (grades[i] + random_alloc[i]) * f for i in range(5)}


def random_all_on(idx: int) -> List[int]:
    r = [0] * 5
    r[idx] = RANDOM_TOTAL
    return r


def bounds_at_rate(bases: Sequence[int], level: int, rate: int) -> StatBounds:
    """血魔上下限：满档/掉20 + 系极值 + 随机极值（上下限各自独立取包络）。"""
    g_hi = grades_full(bases)
    g_lo = grades_drop20(bases)

    # HP max: 随机全堆体力；HP min: 随机全堆魔法（对生命最不利之一）
    hp_max = calc_seven(bp_from(g_hi, random_all_on(0), level, rate, COEFF_MAX))["hp"]
    hp_min = calc_seven(bp_from(g_lo, random_all_on(4), level, rate, COEFF_MIN))["hp"]

    # MP max: 随机全堆魔法；MP min: 随机全堆体力
    mp_max = calc_seven(bp_from(g_hi, random_all_on(4), level, rate, COEFF_MAX))["mp"]
    mp_min = calc_seven(bp_from(g_lo, random_all_on(0), level, rate, COEFF_MIN))["mp"]

    if hp_min > hp_max:
        hp_min, hp_max = hp_max, hp_min
    if mp_min > mp_max:
        mp_min, mp_max = mp_max, mp_min
    return StatBounds(hp_min, hp_max, mp_min, mp_max)


def range_other_at_rate(bases: Sequence[int], level: int, rate: int) -> Dict[str, Tuple[int, int]]:
    """攻防敏精神回复的包络区间（同上限/下限假设）。"""
    g_hi = grades_full(bases)
    g_lo = grades_drop20(bases)
    # 对各项选有利/不利随机维（近似包络）
    favor = {
        "atk": 1,  # 力
        "def": 2,  # 强
        "agi": 3,  # 速
        "spirit": 4,  # 魔
        "rec": 0,  # 体
    }
    anti = {
        "atk": 4,
        "def": 0,
        "agi": 0,
        "spirit": 0,
        "rec": 4,
    }
    out = {}
    for stat in ("atk", "def", "agi", "spirit", "rec"):
        hi = calc_seven(bp_from(g_hi, random_all_on(favor[stat]), level, rate, COEFF_MAX))[stat]
        lo = calc_seven(bp_from(g_lo, random_all_on(anti[stat]), level, rate, COEFF_MIN))[stat]
        if lo > hi:
            lo, hi = hi, lo
        out[stat] = (lo, hi)
    return out


def status_vs_obs(bounds: StatBounds, obs_hp: int, obs_mp: int) -> int:
    """-1=计算整体偏低(需更高倍率); 1=整体偏高; 0=落入区间。"""
    if obs_hp > bounds.hp_max or obs_mp > bounds.mp_max:
        return -1
    if obs_hp < bounds.hp_min or obs_mp < bounds.mp_min:
        return 1
    return 0


def snap_step(x: int, step: int) -> int:
    return int(round(x / step) * step)


def _drop_ts(bounds: StatBounds, obs_hp: int, obs_mp: int) -> Tuple[float, float]:
    def one(obs: int, lo: int, hi: int) -> float:
        if hi <= lo:
            return 0.5
        return (obs - lo) / float(hi - lo)

    return one(obs_hp, bounds.hp_min, bounds.hp_max), one(obs_mp, bounds.mp_min, bounds.mp_max)


def _score_rate(bases: Sequence[int], level: int, rate: int, obs_hp: int, obs_mp: int) -> Tuple[float, float, float]:
    """越小越好: (血魔掉档不一致, 偏离中位, -优先10倍数)。"""
    b = bounds_at_rate(bases, level, rate)
    th, tm = _drop_ts(b, obs_hp, obs_mp)
    inconsist = abs(th - tm)
    mid_bias = abs(0.5 * (th + tm) - 0.5)
    prefer10 = 0.0 if rate % 10 == 0 else 0.05
    return inconsist + prefer10, mid_bias, inconsist


def soft_in_range(obs: int, lo: int, hi: int, tol: float = 0.05) -> bool:
    """允许随机档引起的约 tol 比例误差。"""
    if hi < lo:
        lo, hi = hi, lo
    pad = max(hi * tol, 8.0)
    return (lo - pad) <= obs <= (hi + pad)


def soft_fit(bounds: StatBounds, obs_hp: int, obs_mp: int, tol: float = 0.05) -> bool:
    return soft_in_range(obs_hp, bounds.hp_min, bounds.hp_max, tol) and soft_in_range(
        obs_mp, bounds.mp_min, bounds.mp_max, tol
    )


def find_rate(bases: Sequence[int], level: int, obs_hp: int, obs_mp: int) -> Tuple[int, int, bool, str]:
    """先试 20/50/100（软容差），命中即停；否则再倍增+扫描。"""

    def st(r: int) -> int:
        return status_vs_obs(bounds_at_rate(bases, level, r), obs_hp, obs_mp)

    for r in (20, 50, 100):
        b = bounds_at_rate(bases, level, r)
        if soft_fit(b, obs_hp, obs_mp):
            return r, 10, True, f"quick@{r}"

    # 20 整体偏高：观测低于下限
    if st(RATE_MIN) > 0:
        return RATE_MIN, 10, False, "obs_below_min@20"

    # 倍增探上界：整体偏低则 20→40→80… 直到不再偏低
    probe = RATE_MIN
    while probe < RATE_MAX and st(probe) < 0:
        nxt = probe * 2
        if nxt == probe:
            break
        probe = min(RATE_MAX, nxt)
    if st(probe) < 0:
        return probe, 10, False, f"obs_above_max@{probe}"

    # 从 probe 向两侧扩满「血魔都落入」的窗口（再多扫一级倍增范围）
    scan_lo = RATE_MIN
    scan_hi = min(RATE_MAX, max(probe * 2, 40))

    def collect(step: int) -> List[int]:
        return [c for c in range(scan_lo, scan_hi + 1, step) if st(c) == 0]

    fit10 = collect(10)
    if fit10:
        best = min(fit10, key=lambda x: _score_rate(bases, level, x, obs_hp, obs_mp))
        return best, 10, True, f"fit10 n={len(fit10)} window={min(fit10)}-{max(fit10)}"

    fit5 = collect(5)
    if fit5:
        best = min(fit5, key=lambda x: _score_rate(bases, level, x, obs_hp, obs_mp))
        return best, 5, True, f"fit5 n={len(fit5)} window={min(fit5)}-{max(fit5)}"

    # 无完美落入：选越界惩罚最小的 5 倍数
    best_r, best_pen = RATE_MIN, 1e18
    for c in range(RATE_MIN, scan_hi + 1, 5):
        b = bounds_at_rate(bases, level, c)
        pen = 0.0
        if obs_hp > b.hp_max:
            pen += obs_hp - b.hp_max
        if obs_hp < b.hp_min:
            pen += b.hp_min - obs_hp
        if obs_mp > b.mp_max:
            pen += obs_mp - b.mp_max
        if obs_mp < b.mp_min:
            pen += b.mp_min - obs_mp
        if pen < best_pen:
            best_pen, best_r = pen, c
    return best_r, 5, False, f"nearest_penalty={best_pen:.0f}"


def _hp_mp_from_grades(g0: int, g1: int, g2: int, g3: int, g4: int, f: float) -> Tuple[int, int]:
    bp0, bp1, bp2, bp3, bp4 = g0 * f, g1 * f, g2 * f, g3 * f, g4 * f
    hp = int(round(20 + bp0 * 8 + bp1 * 2 + bp2 * 3 + bp3 * 3 + bp4 * 1))
    mp = int(round(20 + bp0 * 1 + bp1 * 2 + bp2 * 2 + bp3 * 2 + bp4 * 10))
    return hp, mp


def enum_drops_3125(
    bases: Sequence[int],
    level: int,
    rate: int,
    obs_hp: int,
    obs_mp: int,
    *,
    coeff: float = COEFF_MID,
    rnd: int = 2,
) -> Tuple[Tuple[int, int, int, int, int], int, Dict[str, int]]:
    """枚举每维掉0~4（5^5=3125），找 |ΔHP|+|ΔMP| 最小；随机档按均分 rnd。"""
    f = factor(level, rate, coeff)
    b0, b1, b2, b3, b4 = (int(x) for x in bases)
    best_pen = 10**9
    best_drops = (0, 0, 0, 0, 0)
    for d0 in range(5):
        g0 = b0 - d0 + rnd
        for d1 in range(5):
            g1 = b1 - d1 + rnd
            for d2 in range(5):
                g2 = b2 - d2 + rnd
                for d3 in range(5):
                    g3 = b3 - d3 + rnd
                    for d4 in range(5):
                        g4 = b4 - d4 + rnd
                        hp, mp = _hp_mp_from_grades(g0, g1, g2, g3, g4, f)
                        pen = abs(hp - obs_hp) + abs(mp - obs_mp)
                        if pen < best_pen:
                            best_pen = pen
                            best_drops = (d0, d1, d2, d3, d4)
                            if pen == 0:
                                grades = [b0 - d0, b1 - d1, b2 - d2, b3 - d3, b4 - d4]
                                est = calc_seven(bp_from(grades, [rnd] * 5, level, rate, coeff))
                                return best_drops, best_pen, est
    d0, d1, d2, d3, d4 = best_drops
    grades = [b0 - d0, b1 - d1, b2 - d2, b3 - d3, b4 - d4]
    est = calc_seven(bp_from(grades, [rnd] * 5, level, rate, coeff))
    return best_drops, best_pen, est


def estimate_enemy(
    pet: PetRank,
    level: int,
    obs_hp: int,
    obs_mp: int,
) -> EstimateResult:
    bases = pet.bases
    rate, step, fit, note = find_rate(bases, level, obs_hp, obs_mp)
    b = bounds_at_rate(bases, level, rate)
    drops, pen, est = enum_drops_3125(bases, level, rate, obs_hp, obs_mp)
    # 观测血魔覆盖（已知）
    est["hp"] = obs_hp
    est["mp"] = obs_mp
    drop_sum = sum(drops)
    drop_t = 1.0 - drop_sum / 20.0
    other = range_other_at_rate(bases, level, rate)
    return EstimateResult(
        pet=pet,
        level=level,
        obs_hp=obs_hp,
        obs_mp=obs_mp,
        rate=rate,
        rate_step=step,
        fit=fit,
        bounds=b,
        drops=drops,
        match_pen=pen,
        drop_t=drop_t,
        est=est,
        atk_range=other["atk"],
        def_range=other["def"],
        agi_range=other["agi"],
        spirit_range=other["spirit"],
        rec_range=other["rec"],
        note=f"{note};drops={drops};pen={pen}",
    )


def _usable_bases(bases: Sequence[int]) -> bool:
    """含 BOSS 超标档（如盖美拉体350）；排除哨兵/空洞。"""
    if any(v < 0 or v > 800 for v in bases):
        return False
    if sum(bases) < 20:
        return False
    # 全 0 或明显占位
    if sum(1 for v in bases if v == 0) >= 4:
        return False
    return True


def load_rank_table(path: Path = RANK_JSON) -> List[PetRank]:
    data = json.loads(path.read_text(encoding="utf-8"))
    rows = []
    for p in data["pets"]:
        bases = (
            int(p["baseVital"]),
            int(p["baseStr"]),
            int(p["baseTgh"]),
            int(p["baseQuick"]),
            int(p["baseMagic"]),
        )
        if not _usable_bases(bases):
            continue
        rows.append(
            PetRank(
                name=p["name"],
                temp_no=int(p["tempNo"]),
                img=int(p["imgNumber"]),
                bases=bases,
                rank_sane=bool(p.get("rankSane")),
            )
        )
    return rows


def export_slim_csv(rows: List[PetRank], path: Path = SLIM_CSV) -> None:
    lines = ["tempNo,name,img,vit,str,tgh,quick,magic"]
    for r in rows:
        b = r.bases
        # escape name commas
        name = r.name.replace(",", "，")
        lines.append(f"{r.temp_no},{name},{r.img},{b[0]},{b[1]},{b[2]},{b[3]},{b[4]}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def lookup(
    rows: List[PetRank],
    *,
    temp_no: Optional[int] = None,
    name: Optional[str] = None,
    img: Optional[int] = None,
) -> List[PetRank]:
    if temp_no is not None:
        hit = [r for r in rows if r.temp_no == temp_no]
        if hit:
            return hit
    hit = rows
    if name:
        hit = [r for r in hit if r.name == name]
    if img is not None:
        by_img = [r for r in hit if r.img == img]
        if by_img:
            hit = by_img
    return hit


def format_result(er: EstimateResult) -> str:
    b = er.bounds
    e = er.est
    d = er.drops
    return (
        f"{er.pet.name}(temp={er.pet.temp_no},img={er.pet.img}) lv{er.level}\n"
        f"  bases=体{er.pet.bases[0]}/力{er.pet.bases[1]}/强{er.pet.bases[2]}/速{er.pet.bases[3]}/魔{er.pet.bases[4]}\n"
        f"  rate={er.rate}(step{er.rate_step}) fit={er.fit} "
        f"drops=体{d[0]}力{d[1]}强{d[2]}速{d[3]}魔{d[4]} pen={er.match_pen}\n"
        f"  HP[{b.hp_min},{b.hp_max}] obs={er.obs_hp}  MP[{b.mp_min},{b.mp_max}] obs={er.obs_mp}\n"
        f"  EST atk={e['atk']} def={e['def']} agi={e['agi']} spirit={e['spirit']} rec={e['rec']}\n"
        f"  RNG atk={er.atk_range} def={er.def_range} agi={er.agi_range} "
        f"spirit={er.spirit_range} rec={er.rec_range}\n"
        f"  note={er.note}"
    )


def main() -> None:
    ap = argparse.ArgumentParser(description="BOSS/野生属性反推")
    ap.add_argument("--export-slim", action="store_true", help="导出 pet_rank_slim.csv")
    ap.add_argument("--temp", type=int, default=None)
    ap.add_argument("--name", type=str, default=None)
    ap.add_argument("--img", type=int, default=None)
    ap.add_argument("--level", type=int, default=1)
    ap.add_argument("--hp", type=int, default=None, help="观测 MaxHp")
    ap.add_argument("--mp", type=int, default=None, help="观测 MaxMp")
    ap.add_argument("--demo", action="store_true", help="跑文档熊男近似 demo")
    ap.add_argument("--bench", action="store_true", help="性能测试 3125 枚举")
    args = ap.parse_args()

    if args.bench:
        import time

        pet = PetRank("bench", 0, 0, (82, 47, 22, 12, 17))
        bp = bp_from(pet.bases, [2, 2, 2, 2, 2], 25, 50, COEFF_MID)
        seven = calc_seven(bp)
        oh, om = seven["hp"], seven["mp"]
        enum_drops_3125(pet.bases, 25, 50, 1350, 720)
        n = 200
        t0 = time.perf_counter()
        for _ in range(n):
            enum_drops_3125(pet.bases, 25, 50, 1350, 720)
        dt = time.perf_counter() - t0
        print(f"enum3125 worst x{n}: {dt * 1000:.2f}ms total, {dt * 1e6 / n:.1f}us/call")
        t0 = time.perf_counter()
        for _ in range(n):
            estimate_enemy(pet, 25, oh, om)
        dt = time.perf_counter() - t0
        print(f"full estimate x{n}: {dt * 1000:.2f}ms total, {dt * 1000 / n:.3f}ms/call")
        t0 = time.perf_counter()
        for _ in range(20):
            estimate_enemy(pet, 25, 1350, 720)
        print(f"20 enemies worst: {(time.perf_counter() - t0) * 1000:.3f}ms")
        return

    rows = load_rank_table()
    if args.export_slim:
        export_slim_csv(rows)
        print(f"exported {len(rows)} -> {SLIM_CSV}")
        return

    if args.demo:
        # 杀熊者殴兹那克：体82 力47 强22 速12 魔17，倍率50，25级
        pet = PetRank("殴兹那克(demo)", 0, 0, (82, 47, 22, 12, 17))
        # 用满档中位系+均分随机估一个「观测」血魔，再反推应锁到 ~50
        bp = bp_from(pet.bases, [2, 2, 2, 2, 2], 25, 50, COEFF_MID)
        seven = calc_seven(bp)
        er = estimate_enemy(pet, 25, seven["hp"], seven["mp"])
        print(format_result(er))
        print(f"  (synthetic obs from rate50 mid: hp={seven['hp']} mp={seven['mp']})")
        return

    hits = lookup(rows, temp_no=args.temp, name=args.name, img=args.img)
    if not hits:
        raise SystemExit("未找到宠物")
    if len(hits) > 1 and args.hp is None:
        print(f"匹配 {len(hits)} 条，请加 --temp/--img 或提供 --hp/--mp:")
        for h in hits[:20]:
            print(f"  temp={h.temp_no} img={h.img} {h.name} {h.bases}")
        raise SystemExit(1)

    pet = hits[0]
    if args.hp is None or args.mp is None:
        # 仅打印若干倍率下的血魔区间
        print(f"{pet.name} temp={pet.temp_no} img={pet.img} bases={pet.bases} lv={args.level}")
        for r in (20, 30, 40, 50, 60, 80, 100):
            b = bounds_at_rate(pet.bases, args.level, r)
            print(f"  x{r}: HP[{b.hp_min},{b.hp_max}] MP[{b.mp_min},{b.mp_max}]")
        return

    # 多候选时选对观测拟合最好的
    best: Optional[EstimateResult] = None
    for h in hits:
        er = estimate_enemy(h, args.level, args.hp, args.mp)
        if best is None or (er.fit and not best.fit) or (
            er.fit == best.fit and er.match_pen < best.match_pen
        ):
            best = er
    assert best is not None
    print(format_result(best))


if __name__ == "__main__":
    main()
