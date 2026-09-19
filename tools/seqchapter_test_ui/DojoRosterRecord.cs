using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 普通百人（9201 且地图名不含「噩梦」）开战记录。
/// 同层怪物组只留最新一场；怪物血蓝按 ID 维护最低/最高。
/// <c>EnableDojoRosterRecord</c> 本机为 true；发傻瓜包时由 publish_foolproof 改成 false。
/// </summary>
public static partial class SeqChapterTestUi
{
    /// <summary>false=傻瓜包不编译进可用路径（设置不显示、不记）。本机保持 true。</summary>
    private const bool EnableDojoRosterRecord = true;
    /// <summary>设置页开关，默认开。仅本机 const 为 true 时生效。</summary>
    private static bool _dojoRosterOn = true;

    private const int DojoRosterMapId = 9201;
    private const int DojoRosterProbeMs = 1000;
    private const int DojoRosterProbeMax = 5;

    private static bool _dojoRosterLoaded;
    private static bool _dojoRosterInBattle;
    private static bool _dojoRosterCaptured;
    private static int _dojoRosterProbe;
    private static long _dojoRosterNextProbeMs;
    private static int _dojoRosterSeenLayer = -1;

    private static readonly Dictionary<int, DojoRosterFloor> _dojoRosterFloors
        = new Dictionary<int, DojoRosterFloor>();
    private static readonly Dictionary<string, DojoRosterMonster> _dojoRosterMonsters
        = new Dictionary<string, DojoRosterMonster>();

    private struct DojoRosterUnit
    {
        public int Pos;
        public string Name;
        public int AnimId;
        public int TempNo;
        public int Level;
        public int MaxHp;
        public int MaxMp;
        public int Rate;
        public string Key;
    }

    private sealed class DojoRosterFloor
    {
        public int Layer;
        public string FloorName = "";
        public List<DojoRosterUnit> Units = new List<DojoRosterUnit>();
    }

    private sealed class DojoRosterMonster
    {
        public string Key = "";
        public string Name = "";
        public int AnimId;
        public int TempNo;
        public int Level;
        public int HpMin;
        public int HpMax;
        public int MpMin;
        public int MpMax;
        public int RateMin;
        public int RateMax;
        public int Seen;
        public int LastLayer;
    }

    private static void TickDojoRoster()
    {
        if (!EnableDojoRosterRecord || !_dojoRosterOn)
        {
            return;
        }

        try
        {
            EnsureDojoRosterLoaded();
            var inBattle = IsInBattleNow();
            if (!inBattle)
            {
                _dojoRosterInBattle = false;
                _dojoRosterCaptured = false;
                _dojoRosterProbe = 0;
                return;
            }

            if (!_dojoRosterInBattle)
            {
                _dojoRosterInBattle = true;
                _dojoRosterCaptured = false;
                _dojoRosterProbe = 0;
                _dojoRosterNextProbeMs = NowMs() + DojoRosterProbeMs;
            }

            if (_dojoRosterCaptured)
            {
                return;
            }

            var now = NowMs();
            if (now < _dojoRosterNextProbeMs)
            {
                return;
            }

            int floor;
            string floorName;
            int mapResId;
            TryGetCurrentMapInfo(out floor, out floorName, out mapResId);
            if (!IsNormalDojoRosterMap(floor, floorName))
            {
                _dojoRosterCaptured = true;
                if (floor == DojoRosterMapId)
                {
                    WriteLog("dojo-roster skip 非普通百人 floor=" + floor + " name=" + floorName);
                }

                return;
            }

            int layer;
            if (!TryParseDojoLayerName(floorName, out layer) || layer <= 0)
            {
                _dojoRosterProbe++;
                _dojoRosterNextProbeMs = now + DojoRosterProbeMs;
                if (_dojoRosterProbe >= DojoRosterProbeMax)
                {
                    _dojoRosterCaptured = true;
                    WriteLog("dojo-roster 层数未解析 name=" + floorName);
                }

                return;
            }

            if (layer != _dojoRosterSeenLayer)
            {
                _dojoRosterSeenLayer = layer;
                WriteLog("dojo-roster 在普通百人 第" + layer + "道场 name=" + floorName);
            }

            List<DojoRosterUnit> units;
            string wait;
            if (!TryCollectDojoRosterUnits(out units, out wait))
            {
                _dojoRosterProbe++;
                _dojoRosterNextProbeMs = now + DojoRosterProbeMs;
                if (_dojoRosterProbe >= DojoRosterProbeMax)
                {
                    _dojoRosterCaptured = true;
                    WriteLog("dojo-roster 开战信息未齐 layer=" + layer + " " + wait);
                }

                return;
            }

            CommitDojoRoster(layer, floorName, units);
            _dojoRosterCaptured = true;
        }
        catch (Exception ex)
        {
            _dojoRosterCaptured = true;
            WriteLog("TickDojoRoster EX: " + RootMessage(ex));
        }
    }

    private static bool IsNormalDojoRosterMap(int floor, string floorName)
    {
        if (floor != DojoRosterMapId || string.IsNullOrEmpty(floorName))
        {
            return false;
        }

        if (floorName.IndexOf("噩梦", StringComparison.Ordinal) >= 0
            || floorName.IndexOf("恶梦", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseDojoLayerName(string floorName, out int layer)
    {
        layer = 0;
        if (string.IsNullOrEmpty(floorName))
        {
            return false;
        }

        var idx = floorName.IndexOf('第');
        var idx2 = floorName.IndexOf("道场", StringComparison.Ordinal);
        if (idx < 0 || idx2 <= idx)
        {
            return false;
        }

        var numPart = floorName.Substring(idx + 1, idx2 - idx - 1).Trim();
        int n;
        if (int.TryParse(numPart, out n) && n > 0)
        {
            layer = n;
            return true;
        }

        n = ParseChineseDigits(numPart);
        if (n > 0)
        {
            layer = n;
            return true;
        }

        return false;
    }

    private static bool TryCollectDojoRosterUnits(out List<DojoRosterUnit> units, out string wait)
    {
        units = new List<DojoRosterUnit>();
        wait = "";
        var container = FindType("BattleRoleContainer");
        var dic = container?.GetField("BattleRoleDic", BindingFlags.Public | BindingFlags.Static)
                  ?.GetValue(null) as IDictionary;
        if (dic == null)
        {
            wait = "无 BattleRoleDic";
            return false;
        }

        var playerIdx = Convert.ToInt32(GetStaticMember("BattleDataHolder", "battlePlayerIndex") ?? -1);
        var allySide = playerIdx < 10;
        var pendingHp = 0;
        foreach (DictionaryEntry kv in dic)
        {
            var role = kv.Value;
            if (role == null)
            {
                continue;
            }

            var idx = Convert.ToInt32(GetMember(role, "Index") ?? kv.Key ?? -1);
            var roleData = GetMember(role, "RoleData");
            var ch = roleData != null ? GetMember(roleData, "Char") : null;
            if (ch == null)
            {
                continue;
            }

            var bc = Convert.ToInt64(GetMember(ch, "Bcflag") ?? 0);
            var mine = (allySide && idx < 10) || (!allySide && idx >= 10);
            var isPlayer = (bc & 4L) != 0;
            if (mine || isPlayer)
            {
                continue;
            }

            var name = Convert.ToString(GetMember(ch, "Name") ?? "") ?? "";
            var maxHp = Convert.ToInt32(GetMember(ch, "MaxHp") ?? 0);
            var maxMp = Convert.ToInt32(GetMember(ch, "MaxMp") ?? 0);
            var level = Convert.ToInt32(GetMember(ch, "Level") ?? 0);
            var animId = Convert.ToInt32(GetMember(ch, "AnimationId") ?? 0);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (maxHp <= 0 || level <= 0)
            {
                pendingHp++;
                continue;
            }

            var unit = new DojoRosterUnit();
            unit.Pos = idx;
            unit.Name = name;
            unit.AnimId = animId;
            unit.Level = level;
            unit.MaxHp = maxHp;
            unit.MaxMp = maxMp;
            unit.TempNo = 0;
            unit.Rate = 0;
            try
            {
                var est = BossStatEstimator.EstimateBest(name, animId, 0, level, maxHp, maxMp);
                if (est.Ok)
                {
                    unit.TempNo = est.Pet.TempNo;
                    unit.Rate = est.Rate;
                }
            }
            catch
            {
                // 估档失败仍记血蓝
            }

            unit.Key = unit.TempNo > 0
                ? ("t" + unit.TempNo)
                : ("a" + animId + "|" + name);
            units.Add(unit);
        }

        if (units.Count == 0 || pendingHp > 0)
        {
            wait = "敌方未齐 units=" + units.Count + " pending=" + pendingHp;
            return false;
        }

        units.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        return true;
    }

    private static void CommitDojoRoster(int layer, string floorName, List<DojoRosterUnit> units)
    {
        DojoRosterFloor prev;
        _dojoRosterFloors.TryGetValue(layer, out prev);
        LogDojoRosterPosDelta(layer, prev, units);

        var floor = new DojoRosterFloor();
        floor.Layer = layer;
        floor.FloorName = floorName ?? "";
        floor.Units = units;
        _dojoRosterFloors[layer] = floor;

        for (var i = 0; i < units.Count; i++)
        {
            TouchDojoRosterMonster(layer, units[i]);
        }

        SaveDojoRoster();
        var sb = new StringBuilder();
        sb.Append("dojo-roster 第").Append(layer).Append("道场 单位").Append(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var u = units[i];
            sb.Append(" | ").Append(u.Name)
                .Append("#").Append(u.TempNo > 0 ? u.TempNo.ToString() : ("anim" + u.AnimId))
                .Append(" lv").Append(u.Level)
                .Append(" pos").Append(u.Pos)
                .Append(" hp").Append(u.MaxHp)
                .Append(" mp").Append(u.MaxMp)
                .Append(" rate").Append(u.Rate);
        }

        WriteLog(sb.ToString());
    }

    private static void LogDojoRosterPosDelta(int layer, DojoRosterFloor prev, List<DojoRosterUnit> units)
    {
        if (prev == null || prev.Units == null || prev.Units.Count == 0)
        {
            WriteLog("dojo-roster 第" + layer + "道场 首次录入");
            return;
        }

        var oldPos = GroupDojoRosterPos(prev.Units);
        var newPos = GroupDojoRosterPos(units);
        var changed = false;
        foreach (var kv in newPos)
        {
            List<int> oldList;
            if (!oldPos.TryGetValue(kv.Key, out oldList))
            {
                changed = true;
                WriteLog("dojo-roster 第" + layer + "道场 新单位 " + kv.Key
                         + " pos=" + string.Join(",", kv.Value.ConvertAll(x => x.ToString()).ToArray()));
                continue;
            }

            if (!SameIntList(oldList, kv.Value))
            {
                changed = true;
                WriteLog("dojo-roster 第" + layer + "道场 位置变化 " + kv.Key
                         + " " + string.Join(",", oldList.ConvertAll(x => x.ToString()).ToArray())
                         + " -> " + string.Join(",", kv.Value.ConvertAll(x => x.ToString()).ToArray()));
            }
        }

        foreach (var kv in oldPos)
        {
            if (!newPos.ContainsKey(kv.Key))
            {
                changed = true;
                WriteLog("dojo-roster 第" + layer + "道场 本场未见 " + kv.Key);
            }
        }

        if (!changed)
        {
            WriteLog("dojo-roster 第" + layer + "道场 位置未变，覆盖为最新");
        }
    }

    private static Dictionary<string, List<int>> GroupDojoRosterPos(List<DojoRosterUnit> units)
    {
        var map = new Dictionary<string, List<int>>();
        for (var i = 0; i < units.Count; i++)
        {
            var key = units[i].Key ?? "";
            List<int> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<int>();
                map[key] = list;
            }

            list.Add(units[i].Pos);
        }

        foreach (var kv in map)
        {
            kv.Value.Sort();
        }

        return map;
    }

    private static bool SameIntList(List<int> a, List<int> b)
    {
        if (a == null || b == null || a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void TouchDojoRosterMonster(int layer, DojoRosterUnit unit)
    {
        DojoRosterMonster mon;
        if (!_dojoRosterMonsters.TryGetValue(unit.Key, out mon))
        {
            mon = new DojoRosterMonster();
            mon.Key = unit.Key;
            mon.Name = unit.Name;
            mon.AnimId = unit.AnimId;
            mon.TempNo = unit.TempNo;
            mon.Level = unit.Level;
            mon.HpMin = unit.MaxHp;
            mon.HpMax = unit.MaxHp;
            mon.MpMin = unit.MaxMp;
            mon.MpMax = unit.MaxMp;
            mon.RateMin = unit.Rate;
            mon.RateMax = unit.Rate;
            mon.Seen = 1;
            mon.LastLayer = layer;
            _dojoRosterMonsters[unit.Key] = mon;
            return;
        }

        mon.Name = unit.Name;
        mon.AnimId = unit.AnimId;
        if (unit.TempNo > 0)
        {
            mon.TempNo = unit.TempNo;
        }

        mon.Level = unit.Level;
        mon.LastLayer = layer;
        mon.Seen++;
        if (unit.MaxHp < mon.HpMin)
        {
            mon.HpMin = unit.MaxHp;
        }

        if (unit.MaxHp > mon.HpMax)
        {
            mon.HpMax = unit.MaxHp;
        }

        if (unit.MaxMp < mon.MpMin)
        {
            mon.MpMin = unit.MaxMp;
        }

        if (unit.MaxMp > mon.MpMax)
        {
            mon.MpMax = unit.MaxMp;
        }

        if (unit.Rate > 0)
        {
            if (mon.RateMin <= 0 || unit.Rate < mon.RateMin)
            {
                mon.RateMin = unit.Rate;
            }

            if (unit.Rate > mon.RateMax)
            {
                mon.RateMax = unit.Rate;
            }
        }
    }

    private static string GetDojoRosterPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".seqchapter_helper", "dojo_roster.jsonl");
        }
        catch
        {
            return Path.Combine(Environment.CurrentDirectory, "dojo_roster.jsonl");
        }
    }

    private static void EnsureDojoRosterLoaded()
    {
        if (_dojoRosterLoaded)
        {
            return;
        }

        _dojoRosterLoaded = true;
        _dojoRosterFloors.Clear();
        _dojoRosterMonsters.Clear();
        try
        {
            var path = GetDojoRosterPath();
            if (!File.Exists(path))
            {
                WriteLog("dojo-roster 新表 " + path);
                return;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var floorUnits = new Dictionary<int, List<DojoRosterUnit>>();
            var floorNames = new Dictionary<int, string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line) || line[0] != '{')
                {
                    continue;
                }

                var kind = JsonFieldStr(line, "k");
                if (kind == "unit")
                {
                    var layer = JsonFieldInt(line, "layer", 0);
                    if (layer <= 0)
                    {
                        continue;
                    }

                    var unit = new DojoRosterUnit();
                    unit.Pos = JsonFieldInt(line, "pos", -1);
                    unit.Name = JsonFieldStr(line, "name");
                    unit.AnimId = JsonFieldInt(line, "animId", 0);
                    unit.TempNo = JsonFieldInt(line, "tempNo", 0);
                    unit.Level = JsonFieldInt(line, "level", 0);
                    unit.MaxHp = JsonFieldInt(line, "maxHp", 0);
                    unit.MaxMp = JsonFieldInt(line, "maxMp", 0);
                    unit.Rate = JsonFieldInt(line, "rate", 0);
                    unit.Key = JsonFieldStr(line, "key");
                    if (string.IsNullOrEmpty(unit.Key))
                    {
                        unit.Key = unit.TempNo > 0
                            ? ("t" + unit.TempNo)
                            : ("a" + unit.AnimId + "|" + unit.Name);
                    }

                    List<DojoRosterUnit> list;
                    if (!floorUnits.TryGetValue(layer, out list))
                    {
                        list = new List<DojoRosterUnit>();
                        floorUnits[layer] = list;
                    }

                    list.Add(unit);
                    var fname = JsonFieldStr(line, "floorName");
                    if (!string.IsNullOrEmpty(fname))
                    {
                        floorNames[layer] = fname;
                    }
                }
                else if (kind == "mon")
                {
                    var mon = new DojoRosterMonster();
                    mon.Key = JsonFieldStr(line, "key");
                    if (string.IsNullOrEmpty(mon.Key))
                    {
                        continue;
                    }

                    mon.Name = JsonFieldStr(line, "name");
                    mon.AnimId = JsonFieldInt(line, "animId", 0);
                    mon.TempNo = JsonFieldInt(line, "tempNo", 0);
                    mon.Level = JsonFieldInt(line, "level", 0);
                    mon.HpMin = JsonFieldInt(line, "hpMin", 0);
                    mon.HpMax = JsonFieldInt(line, "hpMax", 0);
                    mon.MpMin = JsonFieldInt(line, "mpMin", 0);
                    mon.MpMax = JsonFieldInt(line, "mpMax", 0);
                    mon.RateMin = JsonFieldInt(line, "rateMin", 0);
                    mon.RateMax = JsonFieldInt(line, "rateMax", 0);
                    mon.Seen = JsonFieldInt(line, "seen", 0);
                    mon.LastLayer = JsonFieldInt(line, "lastLayer", 0);
                    _dojoRosterMonsters[mon.Key] = mon;
                }
            }

            foreach (var kv in floorUnits)
            {
                var floor = new DojoRosterFloor();
                floor.Layer = kv.Key;
                string fname;
                floor.FloorName = floorNames.TryGetValue(kv.Key, out fname) ? fname : "";
                floor.Units = kv.Value;
                _dojoRosterFloors[kv.Key] = floor;
            }

            WriteLog("dojo-roster 已载入 层" + _dojoRosterFloors.Count
                     + " 怪" + _dojoRosterMonsters.Count + " " + path);
        }
        catch (Exception ex)
        {
            WriteLog("dojo-roster load EX: " + RootMessage(ex));
        }
    }

    private static void SaveDojoRoster()
    {
        try
        {
            var path = GetDojoRosterPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            var layers = new List<int>(_dojoRosterFloors.Keys);
            layers.Sort();
            for (var i = 0; i < layers.Count; i++)
            {
                var floor = _dojoRosterFloors[layers[i]];
                var units = floor.Units ?? new List<DojoRosterUnit>();
                for (var u = 0; u < units.Count; u++)
                {
                    var unit = units[u];
                    sb.Append("{\"k\":\"unit\",\"layer\":").Append(floor.Layer)
                        .Append(",\"floorName\":\"").Append(JsonEscape(floor.FloorName)).Append("\"")
                        .Append(",\"pos\":").Append(unit.Pos)
                        .Append(",\"name\":\"").Append(JsonEscape(unit.Name)).Append("\"")
                        .Append(",\"animId\":").Append(unit.AnimId)
                        .Append(",\"tempNo\":").Append(unit.TempNo)
                        .Append(",\"level\":").Append(unit.Level)
                        .Append(",\"maxHp\":").Append(unit.MaxHp)
                        .Append(",\"maxMp\":").Append(unit.MaxMp)
                        .Append(",\"rate\":").Append(unit.Rate)
                        .Append(",\"key\":\"").Append(JsonEscape(unit.Key)).Append("\"}\n");
                }
            }

            var keys = new List<string>(_dojoRosterMonsters.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                var mon = _dojoRosterMonsters[keys[i]];
                sb.Append("{\"k\":\"mon\",\"key\":\"").Append(JsonEscape(mon.Key)).Append("\"")
                    .Append(",\"name\":\"").Append(JsonEscape(mon.Name)).Append("\"")
                    .Append(",\"animId\":").Append(mon.AnimId)
                    .Append(",\"tempNo\":").Append(mon.TempNo)
                    .Append(",\"level\":").Append(mon.Level)
                    .Append(",\"hpMin\":").Append(mon.HpMin)
                    .Append(",\"hpMax\":").Append(mon.HpMax)
                    .Append(",\"mpMin\":").Append(mon.MpMin)
                    .Append(",\"mpMax\":").Append(mon.MpMax)
                    .Append(",\"rateMin\":").Append(mon.RateMin)
                    .Append(",\"rateMax\":").Append(mon.RateMax)
                    .Append(",\"seen\":").Append(mon.Seen)
                    .Append(",\"lastLayer\":").Append(mon.LastLayer)
                    .Append("}\n");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            WriteLog("dojo-roster save EX: " + RootMessage(ex));
        }
    }

    private static int JsonFieldInt(string line, string key, int fallback)
    {
        var mark = "\"" + key + "\":";
        var i = line.IndexOf(mark, StringComparison.Ordinal);
        if (i < 0)
        {
            return fallback;
        }

        i += mark.Length;
        var end = i;
        if (end < line.Length && line[end] == '-')
        {
            end++;
        }

        while (end < line.Length && line[end] >= '0' && line[end] <= '9')
        {
            end++;
        }

        int n;
        if (int.TryParse(line.Substring(i, end - i), out n))
        {
            return n;
        }

        return fallback;
    }

    private static string JsonFieldStr(string line, string key)
    {
        var mark = "\"" + key + "\":\"";
        var i = line.IndexOf(mark, StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }

        i += mark.Length;
        var sb = new StringBuilder();
        for (; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                var n = line[++i];
                if (n == 'n')
                {
                    sb.Append('\n');
                }
                else if (n == 'r')
                {
                    sb.Append('\r');
                }
                else if (n == 't')
                {
                    sb.Append('\t');
                }
                else
                {
                    sb.Append(n);
                }

                continue;
            }

            if (c == '"')
            {
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
