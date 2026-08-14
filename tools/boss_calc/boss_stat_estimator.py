#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Boss/野生算档核心算法（纯 Python，无第三方依赖）。

算法移植自 tools/seqchapter_test_ui/BossStatEstimator.cs。
输入：档位表 + 名称/等级/观测血量（蓝量可选）→ 推断倍率 rate、掉档、攻防敏精回。
血量留空：enumerate_rate_ranges()/aggregate_ranges() 直接给出各倍率下属性范围。

与 C# 版的差异：
- C# EstimateBest 只返回单个最佳；本模块 enumerate_schemes() 枚举多方案并按
  (是否硬匹配, 掉档惩罚, 倍率常见性) 排序，返回 top-N，供 GUI 展示。
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field
from pathlib import Path

COEFF_MAX = 0.045
COEFF_MIN = 0.040
COEFF_MID = 0.0425
RATE_MIN = 20
RATE_MAX = 640
RANDOM_TOTAL = 10
SOFT_TOL = 0.05

# 七维索引
HP, MP, ATK, DEF, AGI, SPIRIT, REC = 0, 1, 2, 3, 4, 5, 6


@dataclass
class PetRank:
    name: str
    vit: int
    str_: int
    tgh: int
    quick: int
    magic: int


@dataclass
class StatBounds:
    hp_min: int
    hp_max: int
    mp_min: int
    mp_max: int


@dataclass
class Scheme:
    rate: int
    hard: bool            # 观测值落在硬范围内
    soft: bool            # 观测值落在 5% 软范围内
    drops: list[int]      # [dVit, dStr, dTgh, dQuick, dMagic]
    drop_pen: int         # 掉档枚举惩罚
    drop_total: int       # 掉档总数（越少越接近满档，越可能）
    point: list[int]      # 七维点估计 [hp, mp, atk, def, agi, spirit, rec]
    bounds: StatBounds
    ranges: dict          # atk/def/agi/spirit/rec → (lo, hi)
    note: str = ""
    score: tuple = field(default_factory=tuple)

    @property
    def fit_label(self) -> str:
        if self.hard:
            return "精确匹配"
        if self.soft:
            return "软匹配(±5%)"
        return "近似(惩罚=%d)" % self.drop_pen


class BossStatEstimator:
    def __init__(self, rank_file: str | Path | None = None):
        self._table: list[PetRank] = []
        self._by_name: dict[str, list[PetRank]] = {}
        self._loaded_from = ""
        self._load_error = ""
        if rank_file is not None:
            self.ensure_loaded(rank_file)

    # ---------- 档位表 ----------
    def ensure_loaded(self, rank_file: str | Path | None = None) -> None:
        if self._table:
            return
        for path in self._rank_file_candidates(rank_file):
            p = Path(path)
            try:
                if not p.is_file():
                    continue
                rows = self._parse_rank_bin(p.read_bytes()) if p.suffix.lower() == ".bin" else self._parse_slim_csv(p.read_text(encoding="utf-8", errors="replace"))
                if not rows:
                    continue
                self._table = rows
                self._by_name = {}
                for r in rows:
                    if not r.name:
                        continue
                    group = self._by_name.get(r.name)
                    if group is None:
                        self._by_name[r.name] = [r]
                    else:
                        group.append(r)
                self._loaded_from = str(p)
                self._load_error = ""
                return
            except Exception as ex:  # noqa: BLE001
                self._load_error = "%s: %s" % (p, ex)
        self._table = []
        self._by_name = {}
        if not self._load_error:
            self._load_error = "pet_rank.bin/csv not found"

    @staticmethod
    def _rank_file_candidates(explicit: str | Path | None) -> list[Path]:
        cands: list[Path] = []
        here = Path(__file__).resolve().parent
        if explicit:
            cands.append(Path(explicit))
        cands.extend([
            here / "pet_rank.bin",
            here / "pet_rank_slim.csv",
            here.parent / "pet_rank.bin",
            here.parent / "tools" / "pet_rank.bin",
        ])
        # 去重
        seen: list[str] = []
        out: list[Path] = []
        for c in cands:
            s = str(c)
            if s not in seen:
                seen.append(s)
                out.append(c)
        return out

    def _parse_rank_bin(self, data: bytes) -> list[PetRank]:
        rows: list[PetRank] = []
        if len(data) < 8 or data[:4] != b"PRK1":
            return rows
        count = struct.unpack_from("<i", data, 4)[0]
        pos = 8
        for _ in range(count):
            if pos + 2 > len(data):
                break
            nlen = struct.unpack_from("<H", data, pos)[0]
            pos += 2
            if pos + nlen + 10 > len(data):
                break
            name = data[pos : pos + nlen].decode("utf-8", errors="replace")
            pos += nlen
            vit, s, t, q, m = struct.unpack_from("<5h", data, pos)
            pos += 10
            rows.append(PetRank(name, vit, s, t, q, m))
        return rows

    def _parse_slim_csv(self, text: str) -> list[PetRank]:
        rows: list[PetRank] = []
        for i, line in enumerate(text.splitlines()):
            line = line.strip()
            if not line:
                continue
            if i == 0 and (line.lower().startswith("name") or line.lower().startswith("tempno")):
                continue
            parts = line.split(",")
            try:
                # 新格式 name,vit,str,tgh,quick,magic
                if len(parts) >= 6 and not parts[0].strip().isdigit():
                    rows.append(PetRank(
                        parts[0].strip(),
                        int(parts[1]), int(parts[2]), int(parts[3]),
                        int(parts[4]), int(parts[5]),
                    ))
                # 旧格式 tempNo,name,img,vit,str,tgh,quick,magic
                elif len(parts) >= 8:
                    rows.append(PetRank(
                        parts[1].strip(),
                        int(parts[3]), int(parts[4]), int(parts[5]),
                        int(parts[6]), int(parts[7]),
                    ))
            except (ValueError, IndexError):
                continue
        return rows

    @property
    def table_count(self) -> int:
        self.ensure_loaded()
        return len(self._table)

    @property
    def loaded_from(self) -> str:
        return self._loaded_from

    @property
    def load_error(self) -> str:
        return self._load_error

    def names(self) -> list[str]:
        self.ensure_loaded()
        return list(self._by_name.keys())

    def lookup(self, name: str) -> list[PetRank]:
        """按名称精确查全部同名变体（含同名不同档位）。无则返回空列表。"""
        self.ensure_loaded()
        group = self._by_name.get(name)
        return list(group) if group else []

    def lookup_best(self, name: str) -> PetRank | None:
        """取同名中档位总和最高的变体（默认首选，兼容旧行为）。"""
        variants = self.lookup(name)
        if not variants:
            return None
        return max(variants, key=lambda r: (sum(self.bases_of(r)), r.vit))

    def fuzzy(self, name: str, limit: int = 20) -> list[str]:
        """名称模糊搜索：精确 > 前缀 > 包含，按出现顺序（唯一名）。"""
        self.ensure_loaded()
        name = (name or "").strip()
        if not name:
            return []
        exact, prefix, contain = [], [], []
        for n in self._by_name:
            if n == name:
                exact.append(n)
            elif n.startswith(name):
                prefix.append(n)
            elif name in n:
                contain.append(n)
        return (exact + prefix + contain)[:limit]

    # ---------- 核心数学 ----------
    @staticmethod
    def _factor(level: int, rate: int, coeff: float) -> float:
        return coeff * (level - 1) + rate / 100.0

    @staticmethod
    def _calc_seven(bp: list[float]) -> list[int]:
        b0, b1, b2, b3, b4 = bp
        return [
            int(round(20 + b0 * 8 + b1 * 2 + b2 * 3 + b3 * 3 + b4 * 1)),
            int(round(20 + b0 * 1 + b1 * 2 + b2 * 2 + b3 * 2 + b4 * 10)),
            int(round(20 + b0 * 0.1 + b1 * 2 + b2 * 0.2 + b3 * 0.2 + b4 * 0.1)),
            int(round(20 + b0 * 0.1 + b1 * 0.2 + b2 * 3 + b3 * 0.2 + b4 * 0.1)),
            int(round(20 + b0 * 0.1 + b1 * 0.2 + b2 * 0.2 + b3 * 2 + b4 * 0.1)),
            int(round(100 + b0 * -0.3 + b1 * -0.1 + b2 * 0.2 + b3 * -0.1 + b4 * 0.8)),
            int(round(100 + b0 * 0.8 + b1 * -0.1 + b2 * -0.1 + b3 * 0.2 + b4 * -0.3)),
        ]

    @staticmethod
    def _bp_from(grades: list[int], rnd: list[int], level: int, rate: int, coeff: float) -> list[float]:
        f = BossStatEstimator._factor(level, rate, coeff)
        return [(grades[i] + rnd[i]) * f for i in range(5)]

    @staticmethod
    def _rank_coeff(drop_total: int) -> float:
        """野生宠物成长系数：由掉档总数决定（0系=0.045 → 5系=0.040，每掉4档降0.001）。
        coeff = 0.045 - 0.00025 × 掉档总数。0.0425 只是掉10档的中点值，
        固定用它会让满档 Boss 被低估、高掉档 Boss 被高估。
        """
        return COEFF_MAX - (COEFF_MAX - COEFF_MIN) / 20.0 * max(0, min(20, drop_total))

    @staticmethod
    def _grades_full(bases: list[int]) -> list[int]:
        return list(bases)

    @staticmethod
    def _grades_drop20(bases: list[int]) -> list[int]:
        return [max(0, b - 4) for b in bases]

    @staticmethod
    def _random_all_on(idx: int) -> list[int]:
        rnd = [0] * 5
        rnd[idx] = RANDOM_TOTAL
        return rnd

    @staticmethod
    def bases_of(rank: PetRank) -> list[int]:
        return [rank.vit, rank.str_, rank.tgh, rank.quick, rank.magic]

    def bounds_at_rate(self, bases: list[int], level: int, rate: int) -> StatBounds:
        g_hi = self._grades_full(bases)
        g_lo = self._grades_drop20(bases)
        # hp max: 满档 + 随机全投体 + 大系数
        seven = self._calc_seven(self._bp_from(g_hi, self._random_all_on(0), level, rate, COEFF_MAX))
        hp_max = seven[HP]
        # hp min: 掉20档 + 随机全投魔 + 小系数
        seven = self._calc_seven(self._bp_from(g_lo, self._random_all_on(4), level, rate, COEFF_MIN))
        hp_min = seven[HP]
        # mp max: 满档 + 随机全投魔 + 大系数
        seven = self._calc_seven(self._bp_from(g_hi, self._random_all_on(4), level, rate, COEFF_MAX))
        mp_max = seven[MP]
        # mp min: 掉20档 + 随机全投体 + 小系数
        seven = self._calc_seven(self._bp_from(g_lo, self._random_all_on(0), level, rate, COEFF_MIN))
        mp_min = seven[MP]
        return StatBounds(min(hp_min, hp_max), max(hp_min, hp_max),
                          min(mp_min, mp_max), max(mp_min, mp_max))

    @staticmethod
    def _status_vs_obs(b: StatBounds, obs_hp: int, obs_mp: int | None) -> int:
        if obs_hp > b.hp_max or (obs_mp is not None and obs_mp > b.mp_max):
            return -1
        if obs_hp < b.hp_min or (obs_mp is not None and obs_mp < b.mp_min):
            return 1
        return 0

    @staticmethod
    def _soft_in_range(obs: int, lo: int, hi: int, tol: float) -> bool:
        if hi < lo:
            lo, hi = hi, lo
        # pad 与区间宽度成比例，而非与上限绝对值成比例。
        # 否则高血量 Boss 的 ±5% 上限容差会大到把明显越界的倍率也放进来
        # （例：80级帕鲁凯斯的亡灵 HP=8437，rate=20 上限 8183 越界 254 也被误判软匹配）。
        pad = max((hi - lo) * tol, 8.0)
        return lo - pad <= obs <= hi + pad

    def _soft_fit(self, b: StatBounds, obs_hp: int, obs_mp: int | None) -> bool:
        return self._soft_in_range(obs_hp, b.hp_min, b.hp_max, SOFT_TOL) and (
            obs_mp is None or self._soft_in_range(obs_mp, b.mp_min, b.mp_max, SOFT_TOL)
        )

    def enum_drops(self, bases: list[int], level: int, rate: int,
                   obs_hp: int, obs_mp: int | None) -> tuple[list[int], int, int, list[int]]:
        """枚举每维掉档 0~4 共 3125 种，随机档=2，系数随掉档总数精确变化。
        返回 (最优掉档 [dVit..dMagic], 惩罚, 掉档总数, 七维点估计)。
        """
        rnd = 2
        b0, b1, b2, b3, b4 = bases
        best_pen = 1 << 62
        best = [0, 0, 0, 0, 0]
        best_total = 5 * 4
        for a in range(5):
            g0 = b0 - a + rnd
            for b in range(5):
                g1 = b1 - b + rnd
                for c in range(5):
                    g2 = b2 - c + rnd
                    for d in range(5):
                        g3 = b3 - d + rnd
                        for e in range(5):
                            g4 = b4 - e + rnd
                            total = a + b + c + d + e
                            f = self._factor(level, rate, self._rank_coeff(total))
                            hp = int(round(20 + (g0 * f) * 8 + (g1 * f) * 2 + (g2 * f) * 3 + (g3 * f) * 3 + (g4 * f) * 1))
                            mp = int(round(20 + (g0 * f) * 1 + (g1 * f) * 2 + (g2 * f) * 2 + (g3 * f) * 2 + (g4 * f) * 10))
                            pen = abs(hp - obs_hp)
                            if obs_mp is not None:
                                pen += abs(mp - obs_mp)
                            if pen < best_pen or (pen == best_pen and total < best_total):
                                best_pen = pen
                                best = [a, b, c, d, e]
                                best_total = total
        grades = [b0 - best[0], b1 - best[1], b2 - best[2], b3 - best[3], b4 - best[4]]
        bp = self._bp_from(grades, [rnd] * 5, level, rate, self._rank_coeff(best_total))
        seven = self._calc_seven(bp)
        return best, best_pen, best_total, seven

    @dataclass
    class DropScheme:
        """给定倍率下的一种掉档档位方案。"""
        drops: list[int]          # [dVit, dStr, dTgh, dQuick, dMagic]
        drop_total: int
        pen: int                  # |Δhp|+|Δmp|
        hp: int
        mp: int
        seven: list[int]          # 七维点估计 [hp, mp, atk, def, agi, spirit, rec]
        ranges: dict              # atk/def/agi/spirit/rec → (lo, hi)

    def enumerate_drops_at_rate(self, bases: list[int], level: int, rate: int,
                                obs_hp: int, obs_mp: int | None,
                                top: int = 10) -> list["DropScheme"]:
        """锁定倍率后，枚举所有能解释观测值的掉档档位方案，按可能性排列。

        倍率已由 enumerate_schemes 锁定；此方法针对该倍率枚举每维掉档 0~4，
        保留 pen 最小的组合。pen 越小越可能；pen 相同则掉档总数越小越可能
        （越接近满档越像野生/BOSS 的常见档位）。
        """
        rnd = 2
        b0, b1, b2, b3, b4 = bases
        all_hits: list["DropScheme"] = []

        for a in range(5):
            g0 = b0 - a + rnd
            for b in range(5):
                g1 = b1 - b + rnd
                for c in range(5):
                    g2 = b2 - c + rnd
                    for d in range(5):
                        g3 = b3 - d + rnd
                        for e in range(5):
                            g4 = b4 - e + rnd
                            total = a + b + c + d + e
                            f = self._factor(level, rate, self._rank_coeff(total))
                            hp = int(round(20 + (g0 * f) * 8 + (g1 * f) * 2 + (g2 * f) * 3 + (g3 * f) * 3 + (g4 * f) * 1))
                            mp = int(round(20 + (g0 * f) * 1 + (g1 * f) * 2 + (g2 * f) * 2 + (g3 * f) * 2 + (g4 * f) * 10))
                            pen = abs(hp - obs_hp)
                            if obs_mp is not None:
                                pen += abs(mp - obs_mp)
                            drops = [a, b, c, d, e]
                            seven = self._calc_seven([
                                g0 * f, g1 * f, g2 * f, g3 * f, g4 * f,
                            ])
                            all_hits.append(self.DropScheme(
                                drops=drops, drop_total=total,
                                pen=pen, hp=hp, mp=mp, seven=seven,
                                ranges=self.range_other(bases, level, rate),
                            ))

        # 只保留最佳 pen；若最佳 pen 为 0（完全匹配），可能有多个组合都精确命中
        best_pen = min(h.pen for h in all_hits)
        candidates = [h for h in all_hits if h.pen == best_pen]
        if len(candidates) < top:
            # 如果精确命中太少，再补 pen 稍大的候选
            rest = [h for h in all_hits if h.pen != best_pen]
            rest.sort(key=lambda h: (h.pen, h.drop_total))
            candidates.extend(rest[: top - len(candidates)])

        candidates.sort(key=lambda h: (h.pen, h.drop_total))
        return candidates[:top]

    def range_other(self, bases: list[int], level: int, rate: int) -> dict:
        """攻防敏精回范围。favor 随机档投给主属性，anti 投给无关属性。"""
        r = {}
        r["atk"] = self._env_stat(bases, level, rate, ATK, 1, 4)   # favor 力, anti 魔
        r["def"] = self._env_stat(bases, level, rate, DEF, 2, 0)   # favor 强, anti 体
        r["agi"] = self._env_stat(bases, level, rate, AGI, 3, 0)   # favor 速, anti 体
        r["spirit"] = self._env_stat(bases, level, rate, SPIRIT, 4, 0)  # favor 魔, anti 体
        r["rec"] = self._env_stat(bases, level, rate, REC, 0, 4)   # favor 体, anti 魔
        return r

    def _env_stat(self, bases: list[int], level: int, rate: int,
                  seven_idx: int, favor_rnd: int, anti_rnd: int) -> tuple[int, int]:
        g_hi = self._grades_full(bases)
        g_lo = self._grades_drop20(bases)
        hi = self._calc_seven(self._bp_from(g_hi, self._random_all_on(favor_rnd), level, rate, COEFF_MAX))[seven_idx]
        lo = self._calc_seven(self._bp_from(g_lo, self._random_all_on(anti_rnd), level, rate, COEFF_MIN))[seven_idx]
        return (lo, hi) if lo <= hi else (hi, lo)

    # ---------- 无观测值范围 ----------
    SEVEN_LABELS = ("hp", "mp", "atk", "def", "agi", "spirit", "rec")

    def all_seven_ranges(self, bases: list[int], level: int, rate: int) -> dict:
        """给定倍率下的七维属性范围：满档/掉20档、随机档极端、系数极端。"""
        b = self.bounds_at_rate(bases, level, rate)
        r = self.range_other(bases, level, rate)
        return {
            "hp": (b.hp_min, b.hp_max),
            "mp": (b.mp_min, b.mp_max),
            "atk": r["atk"],
            "def": r["def"],
            "agi": r["agi"],
            "spirit": r["spirit"],
            "rec": r["rec"],
        }

    def enumerate_rate_ranges(self, rank: PetRank, level: int,
                              rates=None) -> list[dict]:
        """不输入血量：按倍率列出七维属性范围，供直接查看。"""
        if rank is None:
            return []
        bases = self.bases_of(rank)
        if rates is None:
            rates = self.COMMON_RATES
        return [{"rate": rate, **self.all_seven_ranges(bases, level, rate)} for rate in rates]

    def aggregate_ranges(self, rank: PetRank, level: int) -> dict:
        """20..640 全倍率聚合：各属性取跨倍率 min/max（绝对可能范围）。"""
        bases = self.bases_of(rank)
        keys = self.SEVEN_LABELS
        lo = {k: 1 << 62 for k in keys}
        hi = {k: -(1 << 62) for k in keys}
        for rate in range(RATE_MIN, RATE_MAX + 1, 10):
            r = self.all_seven_ranges(bases, level, rate)
            for k in keys:
                a, b = r[k]
                if a < lo[k]:
                    lo[k] = a
                if b > hi[k]:
                    hi[k] = b
        return {k: (lo[k], hi[k]) for k in keys}

    # ---------- 多方案 ----------
    # 常见倍率优先级：100 > 50 > 20（用户要求）
    COMMON_RATES = (100, 50, 20)

    def enumerate_schemes(self, rank: PetRank, level: int, obs_hp: int,
                          obs_mp: int | None = None, top: int = 8) -> list[Scheme]:
        """锁定成长倍率并给出多方案。

        倍率=成长倍率：rate 越大 → 每级成长系数越大 → 属性越高。
        优先只推荐常见倍率 100/50/20：只要其中存在能匹配观测值的倍率，
        就只返回这些，不再推荐其它倍率。
        仅当常见倍率全部匹配不上时，才退回全扫描。
        """
        if rank is None:
            return []
        bases = self.bases_of(rank)

        def build(rate: int) -> Scheme | None:
            b = self.bounds_at_rate(bases, level, rate)
            status = self._status_vs_obs(b, obs_hp, obs_mp)
            soft_fit = self._soft_fit(b, obs_hp, obs_mp)
            drops, pen, total, seven = self.enum_drops(bases, level, rate, obs_hp, obs_mp)
            ranges = self.range_other(bases, level, rate)
            hard_fit = status == 0
            # 排序 key：倍率优先级（100>50>20）；再按掉档惩罚；再按掉档总数
            rate_pref = self.COMMON_RATES.index(rate) if rate in self.COMMON_RATES else len(self.COMMON_RATES)
            score = (rate_pref, pen, total, rate)
            return Scheme(rate=rate, hard=hard_fit, soft=soft_fit, drops=drops,
                          drop_pen=pen, drop_total=total, point=seven, bounds=b,
                          ranges=ranges, score=score)

        # 1) 只检查常见倍率 100/50/20；精确匹配优先于软匹配
        common: list[Scheme] = []
        for rate in self.COMMON_RATES:
            s = build(rate)
            if s is not None:
                common.append(s)

        common_hard = [s for s in common if s.hard]
        common_soft = [s for s in common if not s.hard and s.soft]

        # 常见倍率精确匹配 → 只返回这些（软匹配的倍率被剔除，避免
        # 血值实际越界的倍率混进结果，如 rate=20 对 8437 血）
        if common_hard:
            common_hard.sort(key=lambda s: s.score)
            return common_hard[:top]

        # 常见倍率无精确匹配、但有软匹配 → 才用软匹配结果
        if common_soft:
            common_soft.sort(key=lambda s: s.score)
            return common_soft[:top]

        # 2) 常见倍率全不匹配：全扫描 20..640 step 10
        hard: list[Scheme] = []
        soft: list[Scheme] = []
        nearest: list[Scheme] = []
        for rate in range(RATE_MIN, RATE_MAX + 1, 10):
            s = build(rate)
            if s is None:
                continue
            if s.hard:
                hard.append(s)
            elif s.soft:
                soft.append(s)
            else:
                nearest.append(s)

        # 仅在 10 步长没有硬匹配时，补 5 步长候选（相邻倍率间可能有 5 档）
        if not hard:
            for rate in range(RATE_MIN, RATE_MAX + 1, 5):
                if rate % 10 == 0:
                    continue
                s = build(rate)
                if s is None:
                    continue
                if s.hard:
                    hard.append(s)
                elif s.soft:
                    soft.append(s)
                else:
                    nearest.append(s)

        if hard:
            hard.sort(key=lambda s: s.score)
            return hard[:top]

        if soft:
            soft.sort(key=lambda s: s.score)
            return soft[:top]

        # 没有任何匹配：退回最近惩罚倍率（最多 3 个）
        nearest.sort(key=lambda s: s.score)
        return nearest[:min(3, len(nearest))]

    def format_scheme(self, s: Scheme) -> str:
        b = s.bounds
        parts = [
            "rate=%d %s" % (s.rate, s.fit_label),
            "drops=%d/%d/%d/%d/%d pen=%d total=%d" % (*s.drops, s.drop_pen, s.drop_total),
            "HP[%d,%d] MP[%d,%d]" % (b.hp_min, b.hp_max, b.mp_min, b.mp_max),
            "atk=%d[%d-%d] def=%d[%d-%d] agi=%d[%d-%d] spi=%d[%d-%d] rec=%d[%d-%d]" % (
                s.point[ATK], *s.ranges["atk"],
                s.point[DEF], *s.ranges["def"],
                s.point[AGI], *s.ranges["agi"],
                s.point[SPIRIT], *s.ranges["spirit"],
                s.point[REC], *s.ranges["rec"],
            ),
            "est_hp=%d est_mp=%d" % (s.point[HP], s.point[MP]),
        ]
        return " ".join(parts)


def selftest() -> None:
    """用真实数据验证：构造已知倍率的观测，应能找回该倍率；且匹配不上的倍率不出现。"""
    est = BossStatEstimator()
    print("table=%d from=%s err=%s" % (est.table_count, est.loaded_from, est.load_error))
    rank = est.lookup_best("鼠王")
    assert rank is not None, "鼠王 not in table"
    bases = est.bases_of(rank)
    for rate in (20, 50, 100):
        b = est.bounds_at_rate(bases, 30, rate)
        mid_hp = (b.hp_min + b.hp_max) // 2
        schemes = est.enumerate_schemes(rank, 30, mid_hp, top=8)
        rates = [s.rate for s in schemes]
        print("--- obs level=30 hp=%d (construct rate=%d) -> %s ---" % (mid_hp, rate, rates))
        for i, s in enumerate(schemes, 1):
            print("  #%d %s" % (i, est.format_scheme(s)))
        # 倍率隔离：真实 rate 必须在结果中；隔一个数量级（20 vs 100）绝不能同时出现
        assert rate in rates, "construct rate=%d missing" % rate
        if rate == 20:
            assert not any(r >= 100 for r in rates), "rate=20 obs must not match rate>=100"
        if rate == 100:
            assert not any(r <= 20 for r in rates), "rate=100 obs must not match rate<=20"

    # 无观测值范围：常见倍率范围单调（rate 越大范围越高），绝对范围包含常见范围
    agg = est.aggregate_ranges(rank, 30)
    for rr in est.enumerate_rate_ranges(rank, 30, rates=(100, 50, 20)):
        assert rr["hp"][0] <= rr["hp"][1], "hp range inverted"
        assert rr["hp"][0] >= agg["hp"][0] and rr["hp"][1] <= agg["hp"][1], "agg must cover common"
    print("range-mode OK")

    # 帕鲁凯斯的亡灵 80级 HP=8437：rate=20 上限 8183，8437 越界。
    # 修复前会被 5% 上限容差误判为软匹配而混入结果；修复后只应返回 rate=50 精确匹配。
    pr = est.lookup_best("帕鲁凯斯的亡灵")
    if pr is not None:
        schemes = est.enumerate_schemes(pr, 80, 8437, top=8)
        rates = [s.rate for s in schemes]
        print("palukesi schemes:", rates)
        assert rates == [50], "palukesi 8437hp must lock rate=50 exactly, got %s" % rates
        for s in schemes:
            assert s.hard, "palukesi rate=%d must be exact fit" % s.rate
    else:
        print("skip palukesi case: not in table")

    # 同名变体：重名怪物应返回全部档位不同的变体；纯重复（档位相同）只留 1 条
    multi = est.lookup("亡灵骑士")
    if multi:
        print("亡灵骑士 variants:", [est.bases_of(v) for v in multi])
        assert len(multi) >= 2, "亡灵骑士应有多档位变体"
        sums = sorted({sum(est.bases_of(v)) for v in multi})
        assert len(sums) >= 2, "亡灵骑士变体档位应不同"
    multi2 = est.lookup("丘比特")
    if multi2:
        print("丘比特 variants:", [est.bases_of(v) for v in multi2])
        assert len(multi2) >= 2, "丘比特应有多档位变体"
    print("dup-variant OK")


if __name__ == "__main__":
    selftest()
