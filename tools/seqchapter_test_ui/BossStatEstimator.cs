using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// 野生/BOSS：档位表 + 观测血蓝 → 倍率锁定 → 攻防敏精神回复估计。
/// 算法与 tools/boss_stat_estimator.py 一致。
/// </summary>
public static class BossStatEstimator
{
    private const double CoeffMax = 0.045;
    private const double CoeffMin = 0.040;
    private const double CoeffMid = 0.0425;
    private const int RateMin = 20;
    private const int RateMax = 640;
    private const int RandomTotal = 10;

    private static readonly object LoadLock = new object();
    private static List<PetRank> _table;
    private static Dictionary<string, PetRank> _byName;
    private static string _loadedFrom;
    private static string _loadError;
    private const double SoftTol = 0.05;

    public struct PetRank
    {
        public int TempNo;
        public string Name;
        public int Img;
        public int Vit;
        public int Str;
        public int Tgh;
        public int Quick;
        public int Magic;
    }

    public struct StatBounds
    {
        public int HpMin;
        public int HpMax;
        public int MpMin;
        public int MpMax;
    }

    public struct Estimate
    {
        public bool Ok;
        public PetRank Pet;
        public int Level;
        public int ObsHp;
        public int ObsMp;
        public int Rate;
        public int RateStep;
        public bool Fit;
        public StatBounds Bounds;
        public double DropT;
        public int DropVit;
        public int DropStr;
        public int DropTgh;
        public int DropQuick;
        public int DropMagic;
        public int MatchPen;
        public int Atk;
        public int Def;
        public int Agi;
        public int Spirit;
        public int Rec;
        public int AtkMin;
        public int AtkMax;
        public int DefMin;
        public int DefMax;
        public int AgiMin;
        public int AgiMax;
        public int SpiritMin;
        public int SpiritMax;
        public int RecMin;
        public int RecMax;
        public string Note;
        public string Error;
    }

    public static string LoadedFrom
    {
        get { return _loadedFrom; }
    }

    public static string LoadError
    {
        get { return _loadError; }
    }

    public static int TableCount
    {
        get
        {
            EnsureLoaded();
            return _table != null ? _table.Count : 0;
        }
    }

    public static void EnsureLoaded()
    {
        if (_table != null)
        {
            return;
        }

        lock (LoadLock)
        {
            if (_table != null)
            {
                return;
            }

            foreach (var path in RankFileCandidates())
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    List<PetRank> rows = null;
                    if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        rows = ParseRankBin(File.ReadAllBytes(path));
                    }
                    else
                    {
                        rows = ParseSlimCsv(File.ReadAllText(path, Encoding.UTF8));
                    }

                    if (rows == null || rows.Count == 0)
                    {
                        continue;
                    }

                    _table = rows;
                    _byName = new Dictionary<string, PetRank>(rows.Count);
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        if (!string.IsNullOrEmpty(r.Name) && !_byName.ContainsKey(r.Name))
                        {
                            _byName[r.Name] = r;
                        }
                    }

                    _loadedFrom = path;
                    _loadError = null;
                    return;
                }
                catch (Exception ex)
                {
                    _loadError = path + ": " + ex.Message;
                }
            }

            _table = new List<PetRank>();
            _byName = new Dictionary<string, PetRank>();
            if (_loadError == null)
            {
                _loadError = "pet_rank.bin/csv not found";
            }
        }
    }

    private static IEnumerable<string> RankFileCandidates()
    {
        var list = new List<string>();
        Action<string> add = delegate(string p)
        {
            if (!string.IsNullOrEmpty(p) && !list.Contains(p))
            {
                list.Add(p);
            }
        };

        try
        {
            var baseDir = Environment.CurrentDirectory ?? "";
            if (!string.IsNullOrEmpty(baseDir))
            {
                add(Path.Combine(baseDir, "tools", "pet_rank.bin"));
                add(Path.GetFullPath(Path.Combine(baseDir, "..", "tools", "pet_rank.bin")));
                add(Path.Combine(baseDir, "tools", "pet_rank_slim.csv"));
            }

            var dataPathType = Type.GetType("UnityEngine.Application, UnityEngine");
            var dataPath = dataPathType?.GetProperty("dataPath")?.GetValue(null, null) as string;
            if (!string.IsNullOrEmpty(dataPath))
            {
                add(Path.GetFullPath(Path.Combine(dataPath, "..", "tools", "pet_rank.bin")));
                add(Path.GetFullPath(Path.Combine(dataPath, "..", "tools", "pet_rank_slim.csv")));
            }
        }
        catch
        {
            // ignore
        }

        add(@"E:\cross\魔力宝贝：序章\tools\pet_rank.bin");
        add(@"E:\cross\魔力宝贝：序章\tools\pet_rank_slim.csv");
        return list;
    }

    private static List<PetRank> ParseRankBin(byte[] data)
    {
        var rows = new List<PetRank>();
        if (data == null || data.Length < 8)
        {
            return rows;
        }

        if (data[0] != (byte)'P' || data[1] != (byte)'R' || data[2] != (byte)'K' || data[3] != (byte)'1')
        {
            return rows;
        }

        var count = BitConverter.ToInt32(data, 4);
        var pos = 8;
        for (var i = 0; i < count; i++)
        {
            if (pos + 2 > data.Length)
            {
                break;
            }

            var nlen = BitConverter.ToUInt16(data, pos);
            pos += 2;
            if (pos + nlen + 10 > data.Length)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(data, pos, nlen);
            pos += nlen;
            PetRank r;
            r.TempNo = 0;
            r.Name = name;
            r.Img = 0;
            r.Vit = BitConverter.ToInt16(data, pos);
            r.Str = BitConverter.ToInt16(data, pos + 2);
            r.Tgh = BitConverter.ToInt16(data, pos + 4);
            r.Quick = BitConverter.ToInt16(data, pos + 6);
            r.Magic = BitConverter.ToInt16(data, pos + 8);
            pos += 10;
            rows.Add(r);
        }

        return rows;
    }

    private static List<PetRank> ParseSlimCsv(string text)
    {
        var rows = new List<PetRank>();
        using (var reader = new StringReader(text))
        {
            string line;
            var first = true;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (first)
                {
                    first = false;
                    if (line.StartsWith("name", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("tempNo", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var parts = line.Split(',');
                PetRank r;
                r.TempNo = 0;
                r.Img = 0;
                // 新格式 name,vit,str,tgh,quick,magic
                if (parts.Length >= 6 && !char.IsDigit(parts[0].Length > 0 ? parts[0][0] : 'x'))
                {
                    r.Name = parts[0];
                    r.Vit = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    r.Str = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    r.Tgh = int.Parse(parts[3], CultureInfo.InvariantCulture);
                    r.Quick = int.Parse(parts[4], CultureInfo.InvariantCulture);
                    r.Magic = int.Parse(parts[5], CultureInfo.InvariantCulture);
                    rows.Add(r);
                    continue;
                }

                // 旧格式 tempNo,name,img,vit...
                if (parts.Length >= 8)
                {
                    r.TempNo = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    r.Name = parts[1];
                    r.Img = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    r.Vit = int.Parse(parts[3], CultureInfo.InvariantCulture);
                    r.Str = int.Parse(parts[4], CultureInfo.InvariantCulture);
                    r.Tgh = int.Parse(parts[5], CultureInfo.InvariantCulture);
                    r.Quick = int.Parse(parts[6], CultureInfo.InvariantCulture);
                    r.Magic = int.Parse(parts[7], CultureInfo.InvariantCulture);
                    rows.Add(r);
                }
            }
        }

        return rows;
    }

    /// <summary>查表：名字精确命中。查不到返回空（PK/无名不算）。</summary>
    public static List<PetRank> Lookup(string name, int img, int tempNo)
    {
        var hit = new List<PetRank>();
        try
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(name) || _byName == null)
            {
                return hit;
            }

            PetRank r;
            if (_byName.TryGetValue(name, out r))
            {
                hit.Add(r);
            }
        }
        catch
        {
            hit.Clear();
        }

        return hit;
    }

    public static Estimate EstimateEnemy(PetRank pet, int level, int obsHp, int obsMp)
    {
        var er = new Estimate();
        er.Ok = false;
        try
        {
            er.Pet = pet;
            er.Level = level;
            er.ObsHp = obsHp;
            er.ObsMp = obsMp;
            if (level <= 0 || obsHp <= 0)
            {
                er.Error = "bad level/hp";
                return er;
            }

            var bases = new[] { pet.Vit, pet.Str, pet.Tgh, pet.Quick, pet.Magic };
            int step;
            bool fit;
            string note;
            var rate = FindRate(bases, level, obsHp, obsMp, out step, out fit, out note);
            var bounds = BoundsAtRate(bases, level, rate);
            int d0, d1, d2, d3, d4, pen;
            var point = EnumDrops3125(bases, level, rate, obsHp, obsMp, out d0, out d1, out d2, out d3, out d4, out pen);
            int atkMin, atkMax, defMin, defMax, agiMin, agiMax, spiMin, spiMax, recMin, recMax;
            RangeOther(bases, level, rate, out atkMin, out atkMax, out defMin, out defMax,
                out agiMin, out agiMax, out spiMin, out spiMax, out recMin, out recMax);

            er.Ok = true;
            er.Rate = rate;
            er.RateStep = step;
            er.Fit = fit;
            er.Bounds = bounds;
            er.DropVit = d0;
            er.DropStr = d1;
            er.DropTgh = d2;
            er.DropQuick = d3;
            er.DropMagic = d4;
            er.MatchPen = pen;
            er.DropT = 1.0 - (d0 + d1 + d2 + d3 + d4) / 20.0;
            er.Atk = point[2];
            er.Def = point[3];
            er.Agi = point[4];
            er.Spirit = point[5];
            er.Rec = point[6];
            er.AtkMin = atkMin;
            er.AtkMax = atkMax;
            er.DefMin = defMin;
            er.DefMax = defMax;
            er.AgiMin = agiMin;
            er.AgiMax = agiMax;
            er.SpiritMin = spiMin;
            er.SpiritMax = spiMax;
            er.RecMin = recMin;
            er.RecMax = recMax;
            er.Note = (note ?? "") + ";pen=" + pen;
            return er;
        }
        catch
        {
            er.Ok = false;
            er.Error = "ex";
            return er;
        }
    }

    /// <summary>多候选时选 fit 且 match_pen 更小者。查不到/失败 → Ok=false，不抛异常。</summary>
    public static Estimate EstimateBest(string name, int img, int tempNo, int level, int obsHp, int obsMp)
    {
        var miss = new Estimate();
        miss.Ok = false;
        try
        {
            var hits = Lookup(name, img, tempNo);
            if (hits == null || hits.Count == 0)
            {
                return miss;
            }

            Estimate best = default(Estimate);
            var has = false;
            for (var i = 0; i < hits.Count; i++)
            {
                var er = EstimateEnemy(hits[i], level, obsHp, obsMp);
                if (!er.Ok)
                {
                    continue;
                }

                if (!has)
                {
                    best = er;
                    has = true;
                    continue;
                }

                if (er.Fit && !best.Fit)
                {
                    best = er;
                    continue;
                }

                if (er.Fit == best.Fit && er.MatchPen < best.MatchPen)
                {
                    best = er;
                }
            }

            return has ? best : miss;
        }
        catch
        {
            return miss;
        }
    }

    public static string FormatOneLine(Estimate er)
    {
        if (!er.Ok)
        {
            return "";
        }

        var b = er.Bounds;
        return "EST rate=" + er.Rate + (er.Fit ? "" : "?")
               + " drops=" + er.DropVit + "/" + er.DropStr + "/" + er.DropTgh + "/" + er.DropQuick + "/" + er.DropMagic
               + " pen=" + er.MatchPen
               + " HP[" + b.HpMin + "," + b.HpMax + "]"
               + " MP[" + b.MpMin + "," + b.MpMax + "]"
               + " atk=" + er.Atk + "[" + er.AtkMin + "-" + er.AtkMax + "]"
               + " def=" + er.Def + "[" + er.DefMin + "-" + er.DefMax + "]"
               + " agi=" + er.Agi + "[" + er.AgiMin + "-" + er.AgiMax + "]"
               + " spi=" + er.Spirit + "[" + er.SpiritMin + "-" + er.SpiritMax + "]"
               + " rec=" + er.Rec + "[" + er.RecMin + "-" + er.RecMax + "]"
               + " temp=" + er.Pet.TempNo
               + " " + er.Note;
    }

    // ----- core math -----

    private static double Factor(int level, int rate, double coeff)
    {
        return coeff * (level - 1) + rate / 100.0;
    }

    private static void CalcSeven(double[] bp, int[] seven)
    {
        // hp mp atk def agi spirit rec
        seven[0] = (int)Math.Round(20 + bp[0] * 8.0 + bp[1] * 2.0 + bp[2] * 3.0 + bp[3] * 3.0 + bp[4] * 1.0);
        seven[1] = (int)Math.Round(20 + bp[0] * 1.0 + bp[1] * 2.0 + bp[2] * 2.0 + bp[3] * 2.0 + bp[4] * 10.0);
        seven[2] = (int)Math.Round(20 + bp[0] * 0.1 + bp[1] * 2.0 + bp[2] * 0.2 + bp[3] * 0.2 + bp[4] * 0.1);
        seven[3] = (int)Math.Round(20 + bp[0] * 0.1 + bp[1] * 0.2 + bp[2] * 3.0 + bp[3] * 0.2 + bp[4] * 0.1);
        seven[4] = (int)Math.Round(20 + bp[0] * 0.1 + bp[1] * 0.2 + bp[2] * 0.2 + bp[3] * 2.0 + bp[4] * 0.1);
        seven[5] = (int)Math.Round(100 + bp[0] * -0.3 + bp[1] * -0.1 + bp[2] * 0.2 + bp[3] * -0.1 + bp[4] * 0.8);
        seven[6] = (int)Math.Round(100 + bp[0] * 0.8 + bp[1] * -0.1 + bp[2] * -0.1 + bp[3] * 0.2 + bp[4] * -0.3);
    }

    private static void BpFrom(int[] grades, int[] rnd, int level, int rate, double coeff, double[] bp)
    {
        var f = Factor(level, rate, coeff);
        for (var i = 0; i < 5; i++)
        {
            bp[i] = (grades[i] + rnd[i]) * f;
        }
    }

    private static void GradesFull(int[] bases, int[] g)
    {
        for (var i = 0; i < 5; i++)
        {
            g[i] = bases[i];
        }
    }

    private static void GradesDrop20(int[] bases, int[] g)
    {
        for (var i = 0; i < 5; i++)
        {
            g[i] = Math.Max(0, bases[i] - 4);
        }
    }

    private static void RandomAllOn(int idx, int[] rnd)
    {
        for (var i = 0; i < 5; i++)
        {
            rnd[i] = 0;
        }

        rnd[idx] = RandomTotal;
    }

    public static StatBounds BoundsAtRate(int[] bases, int level, int rate)
    {
        var gHi = new int[5];
        var gLo = new int[5];
        var rnd = new int[5];
        var bp = new double[5];
        var seven = new int[7];
        GradesFull(bases, gHi);
        GradesDrop20(bases, gLo);

        RandomAllOn(0, rnd);
        BpFrom(gHi, rnd, level, rate, CoeffMax, bp);
        CalcSeven(bp, seven);
        var hpMax = seven[0];

        RandomAllOn(4, rnd);
        BpFrom(gLo, rnd, level, rate, CoeffMin, bp);
        CalcSeven(bp, seven);
        var hpMin = seven[0];

        RandomAllOn(4, rnd);
        BpFrom(gHi, rnd, level, rate, CoeffMax, bp);
        CalcSeven(bp, seven);
        var mpMax = seven[1];

        RandomAllOn(0, rnd);
        BpFrom(gLo, rnd, level, rate, CoeffMin, bp);
        CalcSeven(bp, seven);
        var mpMin = seven[1];

        var b = new StatBounds();
        b.HpMin = Math.Min(hpMin, hpMax);
        b.HpMax = Math.Max(hpMin, hpMax);
        b.MpMin = Math.Min(mpMin, mpMax);
        b.MpMax = Math.Max(mpMin, mpMax);
        return b;
    }

    private static int StatusVsObs(StatBounds b, int obsHp, int obsMp)
    {
        if (obsHp > b.HpMax || obsMp > b.MpMax)
        {
            return -1;
        }

        if (obsHp < b.HpMin || obsMp < b.MpMin)
        {
            return 1;
        }

        return 0;
    }

    private static bool SoftInRange(int obs, int lo, int hi, double tol)
    {
        if (hi < lo)
        {
            var t = lo;
            lo = hi;
            hi = t;
        }

        var pad = Math.Max(hi * tol, 8.0);
        return obs >= lo - pad && obs <= hi + pad;
    }

    private static bool SoftFit(StatBounds b, int obsHp, int obsMp)
    {
        return SoftInRange(obsHp, b.HpMin, b.HpMax, SoftTol)
               && SoftInRange(obsMp, b.MpMin, b.MpMax, SoftTol);
    }

    private static void DropTs(StatBounds b, int obsHp, int obsMp, out double th, out double tm)
    {
        th = b.HpMax <= b.HpMin ? 0.5 : (obsHp - b.HpMin) / (double)(b.HpMax - b.HpMin);
        tm = b.MpMax <= b.MpMin ? 0.5 : (obsMp - b.MpMin) / (double)(b.MpMax - b.MpMin);
    }

    private static double ScoreRate(int[] bases, int level, int rate, int obsHp, int obsMp)
    {
        var b = BoundsAtRate(bases, level, rate);
        double th, tm;
        DropTs(b, obsHp, obsMp, out th, out tm);
        var inconsist = Math.Abs(th - tm);
        var midBias = Math.Abs(0.5 * (th + tm) - 0.5);
        var prefer10 = rate % 10 == 0 ? 0.0 : 0.05;
        return inconsist + prefer10 + midBias * 0.01;
    }

    private static int FindRate(int[] bases, int level, int obsHp, int obsMp, out int step, out bool fit, out string note)
    {
        // 默认先试 20/50/100（软容差），命中即停
        foreach (var quick in new[] { 20, 50, 100 })
        {
            if (SoftFit(BoundsAtRate(bases, level, quick), obsHp, obsMp))
            {
                step = 10;
                fit = true;
                note = "quick@" + quick;
                return quick;
            }
        }

        if (StatusVsObs(BoundsAtRate(bases, level, RateMin), obsHp, obsMp) > 0)
        {
            step = 10;
            fit = false;
            note = "obs_below_min@20";
            return RateMin;
        }

        var probe = RateMin;
        while (probe < RateMax && StatusVsObs(BoundsAtRate(bases, level, probe), obsHp, obsMp) < 0)
        {
            var nxt = probe * 2;
            if (nxt == probe)
            {
                break;
            }

            probe = Math.Min(RateMax, nxt);
        }

        if (StatusVsObs(BoundsAtRate(bases, level, probe), obsHp, obsMp) < 0)
        {
            step = 10;
            fit = false;
            note = "obs_above_max@" + probe;
            return probe;
        }

        var scanLo = RateMin;
        var scanHi = Math.Min(RateMax, Math.Max(probe * 2, 40));

        int best = -1;
        double bestScore = 1e18;
        var fitCount = 0;
        var fitMin = int.MaxValue;
        var fitMax = int.MinValue;
        for (var c = scanLo; c <= scanHi; c += 10)
        {
            if (StatusVsObs(BoundsAtRate(bases, level, c), obsHp, obsMp) != 0)
            {
                continue;
            }

            fitCount++;
            if (c < fitMin)
            {
                fitMin = c;
            }

            if (c > fitMax)
            {
                fitMax = c;
            }

            var sc = ScoreRate(bases, level, c, obsHp, obsMp);
            if (sc < bestScore)
            {
                bestScore = sc;
                best = c;
            }
        }

        if (best >= 0)
        {
            step = 10;
            fit = true;
            note = "fit10 n=" + fitCount + " window=" + fitMin + "-" + fitMax;
            return best;
        }

        best = RateMin;
        var bestPen = 1e18;
        for (var c = RateMin; c <= scanHi; c += 5)
        {
            var b = BoundsAtRate(bases, level, c);
            double pen = 0;
            if (obsHp > b.HpMax)
            {
                pen += obsHp - b.HpMax;
            }

            if (obsHp < b.HpMin)
            {
                pen += b.HpMin - obsHp;
            }

            if (obsMp > b.MpMax)
            {
                pen += obsMp - b.MpMax;
            }

            if (obsMp < b.MpMin)
            {
                pen += b.MpMin - obsMp;
            }

            if (pen < bestPen)
            {
                bestPen = pen;
                best = c;
            }
        }

        step = 5;
        fit = false;
        note = "nearest_penalty=" + bestPen.ToString("0", CultureInfo.InvariantCulture);
        return best;
    }

    /// <summary>每维掉0~4共3125种；随机档按均分+2；中位系。返回七维点估计。</summary>
    private static int[] EnumDrops3125(
        int[] bases, int level, int rate, int obsHp, int obsMp,
        out int d0, out int d1, out int d2, out int d3, out int d4, out int bestPen)
    {
        const int rnd = 2;
        var f = Factor(level, rate, CoeffMid);
        var b0 = bases[0];
        var b1 = bases[1];
        var b2 = bases[2];
        var b3 = bases[3];
        var b4 = bases[4];
        bestPen = int.MaxValue;
        d0 = d1 = d2 = d3 = d4 = 0;
        for (var a = 0; a < 5; a++)
        {
            var g0 = b0 - a + rnd;
            for (var b = 0; b < 5; b++)
            {
                var g1 = b1 - b + rnd;
                for (var c = 0; c < 5; c++)
                {
                    var g2 = b2 - c + rnd;
                    for (var d = 0; d < 5; d++)
                    {
                        var g3 = b3 - d + rnd;
                        for (var e = 0; e < 5; e++)
                        {
                            var g4 = b4 - e + rnd;
                            var bp0 = g0 * f;
                            var bp1 = g1 * f;
                            var bp2 = g2 * f;
                            var bp3 = g3 * f;
                            var bp4 = g4 * f;
                            var hp = (int)Math.Round(20 + bp0 * 8 + bp1 * 2 + bp2 * 3 + bp3 * 3 + bp4 * 1);
                            var mp = (int)Math.Round(20 + bp0 * 1 + bp1 * 2 + bp2 * 2 + bp3 * 2 + bp4 * 10);
                            var pen = Math.Abs(hp - obsHp) + Math.Abs(mp - obsMp);
                            if (pen < bestPen)
                            {
                                bestPen = pen;
                                d0 = a;
                                d1 = b;
                                d2 = c;
                                d3 = d;
                                d4 = e;
                                if (pen == 0)
                                {
                                    goto Done;
                                }
                            }
                        }
                    }
                }
            }
        }

        Done:
        var grades = new[] { b0 - d0, b1 - d1, b2 - d2, b3 - d3, b4 - d4 };
        var rndArr = new[] { rnd, rnd, rnd, rnd, rnd };
        var bp = new double[5];
        var seven = new int[7];
        BpFrom(grades, rndArr, level, rate, CoeffMid, bp);
        CalcSeven(bp, seven);
        return seven;
    }

    private static void RangeOther(
        int[] bases, int level, int rate,
        out int atkMin, out int atkMax,
        out int defMin, out int defMax,
        out int agiMin, out int agiMax,
        out int spiMin, out int spiMax,
        out int recMin, out int recMax)
    {
        // favor idx: atk力1 def强2 agi速3 spirit魔4 rec体0
        // anti: atk4 def0 agi0 spirit0 rec4
        int a0, a1, d0, d1, g0, g1, s0, s1, r0, r1;
        EnvStat(bases, level, rate, 2, 1, 4, out a0, out a1);
        EnvStat(bases, level, rate, 3, 2, 0, out d0, out d1);
        EnvStat(bases, level, rate, 4, 3, 0, out g0, out g1);
        EnvStat(bases, level, rate, 5, 4, 0, out s0, out s1);
        EnvStat(bases, level, rate, 6, 0, 4, out r0, out r1);
        atkMin = a0;
        atkMax = a1;
        defMin = d0;
        defMax = d1;
        agiMin = g0;
        agiMax = g1;
        spiMin = s0;
        spiMax = s1;
        recMin = r0;
        recMax = r1;
    }

    private static void EnvStat(int[] bases, int level, int rate, int sevenIdx, int favorRnd, int antiRnd, out int lo, out int hi)
    {
        var gHi = new int[5];
        var gLo = new int[5];
        var rnd = new int[5];
        var bp = new double[5];
        var seven = new int[7];
        GradesFull(bases, gHi);
        GradesDrop20(bases, gLo);
        RandomAllOn(favorRnd, rnd);
        BpFrom(gHi, rnd, level, rate, CoeffMax, bp);
        CalcSeven(bp, seven);
        hi = seven[sevenIdx];
        RandomAllOn(antiRnd, rnd);
        BpFrom(gLo, rnd, level, rate, CoeffMin, bp);
        CalcSeven(bp, seven);
        lo = seven[sevenIdx];
        if (lo > hi)
        {
            var t = lo;
            lo = hi;
            hi = t;
        }
    }
}
