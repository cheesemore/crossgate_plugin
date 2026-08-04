using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

/// <summary>
/// 进战 + 地图形象钩子。
/// 应用：人物形象(AnimationId) / 人物光环(RoleHalo=Grano) / 坐骑(RideSkin=配置Id) /
///       宠物形象 / 满档 / 满档光环。
/// 持久化：AppData LocalLow 下按玩家 Uid 存档（删客户端不丢）。
/// 进战按 Uid 匹配；地图创建人物/宠物实体时，只要本地存过该 Uid 就套用（同机其它号过图可见）。
/// 约定：字段 0 = 不配置。只遍历真实存在的单位，缺人/缺宠不报错。
/// </summary>
public static class SeqChapterBattleAppear
{
    public const string AssetPath = "hotfixdata/SeqChapterBattleAppear.dll.bytes";
    public const string TypeName = "SeqChapterBattleAppear";
    public const string EntryName = "OnBattleCharsReceived";
    public const string WorldEntryName = "TryApplyWorldAppear";
    public const string CodePrefix = "CGAP1:";
    public const string UidStoreFileName = "battle_appear_uid.json";

    // 推荐方案1/2/3/4（点击等同立刻粘贴导入）
    public const string Preset1Code =
        "### 序章进战形象配置（完整：人物/光环/宠物/满档/满档光环/坐骑）\n"
        + "# 说明：游戏内应用 人物形象/人物光环/坐骑/宠物形象/满档/满档光环。\n"
        + "# 约定：0=不配置；满档勾选=1；人物光环=Grano；坐骑=配置 Id（可误填 Grano 会反查）。\n"
        + "# 1号位人物使用105509，人物光环5.青电，宠物使用101905（魔龙），开启满档效果，满档光环1.炫光律动，坐骑Id8206（奥术飞毯）\n"
        + "# 2号位人物使用105509，人物光环5.青电，宠物使用101905（魔龙），开启满档效果，满档光环4.烈焰之心，坐骑Id8206（奥术飞毯）\n"
        + "# 3号位人物使用105509，人物光环5.青电，宠物使用101905（魔龙），开启满档效果，满档光环4.烈焰之心，坐骑Id8206（奥术飞毯）\n"
        + "# 4号位人物使用105509，人物光环5.青电，宠物使用101905（魔龙），开启满档效果，满档光环5.燃焰之环，坐骑Id8206（奥术飞毯）\n"
        + "# 5号位人物使用105509，人物光环5.青电，宠物使用101905（魔龙），开启满档效果，满档光环5.燃焰之环，坐骑Id8206（奥术飞毯）\n"
        + "CGAP1:QkFQMQEBABGOAQDNmAIAAQEAAAAlnAEAuf8BABGOAQDNmAIAAQQAAAAlnAEAuf8BABGOAQDNmAIAAQQAAAAlnAEAuf8BABGOAQDNmAIAAQUAAAAlnAEAuf8BABGOAQDNmAIAAQUAAAAlnAEAuf8BAA==\n";

    public const string Preset2Code =
        "### 序章进战形象配置（完整：人物/光环/宠物/满档/满档光环/坐骑）\n"
        + "# 说明：游戏内应用 人物形象/人物光环/坐骑/宠物形象/满档/满档光环。\n"
        + "# 约定：0=不配置；满档勾选=1；人物光环=Grano；坐骑=配置 Id（可误填 Grano 会反查）。\n"
        + "# 1号位人物使用100955，人物光环5.青电，宠物使用131002（钢铁领主），开启满档效果，满档光环1.炫光律动，坐骑Id13（迷你修拉号）\n"
        + "# 2号位人物使用100955，人物光环5.青电，宠物使用131002（钢铁领主），开启满档效果，满档光环4.烈焰之心，坐骑Id13（迷你修拉号）\n"
        + "# 3号位人物使用100955，人物光环5.青电，宠物使用131002（钢铁领主），开启满档效果，满档光环4.烈焰之心，坐骑Id13（迷你修拉号）\n"
        + "# 4号位人物使用100955，人物光环5.青电，宠物使用131002（钢铁领主），开启满档效果，满档光环5.燃焰之环，坐骑Id13（迷你修拉号）\n"
        + "# 5号位人物使用100955，人物光环5.青电，宠物使用131002（钢铁领主），开启满档效果，满档光环5.燃焰之环，坐骑Id13（迷你修拉号）\n"
        + "CGAP1:QkFQMQEBALr/AQDNmAIAAQEAAABbigEAuP8BALr/AQDNmAIAAQQAAABbigEAuP8BALr/AQDNmAIAAQQAAABbigEAuP8BALr/AQDNmAIAAQUAAABbigEAuP8BALr/AQDNmAIAAQUAAABbigEAuP8BAA==\n";

    public const string Preset3Code =
        "### 序章进战形象配置（完整：人物/光环/宠物/满档/满档光环/坐骑）\n"
        + "# 说明：游戏内应用 人物形象/人物光环/坐骑/宠物形象/满档/满档光环。\n"
        + "# 约定：0=不配置；满档勾选=1；人物光环=Grano；坐骑=配置 Id（可误填 Grano 会反查）。\n"
        + "# 1号位人物使用118148，人物光环5.青电，宠物使用101922（寒冰牛头怪），开启满档效果，满档光环1.炫光律动，坐骑Id8202（迅捷巨狼）\n"
        + "# 2号位人物使用118148，人物光环5.青电，宠物使用101922（寒冰牛头怪），开启满档效果，满档光环4.烈焰之心，坐骑Id8202（迅捷巨狼）\n"
        + "# 3号位人物使用118148，人物光环5.青电，宠物使用101922（寒冰牛头怪），开启满档效果，满档光环4.烈焰之心，坐骑Id8202（迅捷巨狼）\n"
        + "# 4号位人物使用118148，人物光环5.青电，宠物使用101922（寒冰牛头怪），开启满档效果，满档光环5.燃焰之环，坐骑Id8202（迅捷巨狼）\n"
        + "# 5号位人物使用118148，人物光环5.青电，宠物使用101922（寒冰牛头怪），开启满档效果，满档光环5.燃焰之环，坐骑Id8202（迅捷巨狼）\n"
        + "CGAP1:QkFQMQEBACKOAQDNmAIAAQEAAACEzQEACiAAACKOAQDNmAIAAQQAAACEzQEACiAAACKOAQDNmAIAAQQAAACEzQEACiAAACKOAQDNmAIAAQUAAACEzQEACiAAACKOAQDNmAIAAQUAAACEzQEACiAAAA==\n";

    /// <summary>
    /// 方案4：人物红蔷薇女王(100199) + 宠物七彩史莱姆(120082) + 坐骑0（不配置）。
    /// </summary>
    public const string Preset4Code =
        "### 序章进战形象配置（完整：人物/光环/宠物/满档/满档光环/坐骑）\n"
        + "# 说明：游戏内应用 人物形象/人物光环/坐骑/宠物形象/满档/满档光环。\n"
        + "# 约定：0=不配置；满档勾选=1；人物光环=Grano；坐骑=配置 Id（可误填 Grano 会反查）。\n"
        + "# 1号位人物使用100199（红蔷薇女王），人物光环5.青电，宠物使用120082（七彩史莱姆），开启满档效果，满档光环1.炫光律动，坐骑Id0\n"
        + "# 2号位人物使用100199（红蔷薇女王），人物光环5.青电，宠物使用120082（七彩史莱姆），开启满档效果，满档光环4.烈焰之心，坐骑Id0\n"
        + "# 3号位人物使用100199（红蔷薇女王），人物光环5.青电，宠物使用120082（七彩史莱姆），开启满档效果，满档光环4.烈焰之心，坐骑Id0\n"
        + "# 4号位人物使用100199（红蔷薇女王），人物光环5.青电，宠物使用120082（七彩史莱姆），开启满档效果，满档光环5.燃焰之环，坐骑Id0\n"
        + "# 5号位人物使用100199（红蔷薇女王），人物光环5.青电，宠物使用120082（七彩史莱姆），开启满档效果，满档光环5.燃焰之环，坐骑Id0\n"
        + "CGAP1:QkFQMQEBABLVAQAFAAAAAQEAAABnhwEAAAAAABLVAQAFAAAAAQQAAABnhwEAAAAAABLVAQAFAAAAAQQAAABnhwEAAAAAABLVAQAFAAAAAQUAAABnhwEAAAAAABLVAQAFAAAAAQUAAABnhwEAAAAAAA==\n";

    private static bool _cfgLoaded;
    private static bool _enabled;
    private static readonly SlotCfg[] Slots = new SlotCfg[5];
    private static string _loadedFrom = "";
    private static string _loadError = "";
    private static DateTime _lastReloadUtc = DateTime.MinValue;
    private static readonly Dictionary<string, SlotCfg> UidProfiles = new Dictionary<string, SlotCfg>(StringComparer.Ordinal);
    private static string _uidStorePath = "";
    private static string _uidStoreError = "";

    private struct SlotCfg
    {
        public bool HasAny;
        public int PetAnim;
        public int RoleHalo;
        public int Perfect;
        public int MaxCrest;
        public int CharAnim;
        public int RideSkin;
        public bool SetPetAnim;
        public bool SetRoleHalo;
        public bool SetPerfect;
        public bool SetMaxCrest;
        public bool SetCharAnim;
        public bool SetRideSkin;
    }

    /// <summary>
    /// 地图创建人物/宠物前调用。io 原地改写：
    /// [0] uid [1] kind(0人物/1宠物) [2] animId [3] rideSkinId [4] roleHole
    /// [5] PerfectPet [6] maxCrestUseId。返回是否有字段被覆盖。
    /// </summary>
    public static bool TryApplyWorldAppear(object[] io)
    {
        try
        {
            if (io == null || io.Length < 7)
            {
                return false;
            }

            EnsureConfig(force: false);
            LoadUidStore(force: false);
            if (!_enabled)
            {
                return false;
            }

            var uid = Convert.ToString(io[0] ?? "");
            if (string.IsNullOrEmpty(uid))
            {
                return false;
            }

            SlotCfg cfg;
            if (!UidProfiles.TryGetValue(uid, out cfg) || !cfg.HasAny)
            {
                return false;
            }

            var kind = Convert.ToInt32(io[1] ?? 0);
            var changed = false;
            if (kind == 0)
            {
                if (cfg.SetCharAnim)
                {
                    io[2] = cfg.CharAnim;
                    changed = true;
                }

                if (cfg.SetRideSkin)
                {
                    io[3] = ResolveRideSkinConfigId(cfg.RideSkin);
                    changed = true;
                }

                if (cfg.SetRoleHalo)
                {
                    io[4] = cfg.RoleHalo;
                    changed = true;
                }
            }
            else if (kind == 1)
            {
                if (cfg.SetPetAnim)
                {
                    io[2] = cfg.PetAnim;
                    changed = true;
                }

                if (cfg.SetPerfect)
                {
                    io[5] = cfg.Perfect != 0 ? 1 : 0;
                    changed = true;
                }

                if (cfg.SetMaxCrest)
                {
                    io[6] = cfg.MaxCrest;
                    changed = true;
                }
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }

    public static void OnBattleCharsReceived()
    {
        try
        {
            EnsureConfig(force: false);
            // 进战前再读一次 Uid 档（短读，不占用文件）
            LoadUidStore(force: true);
            if (!_enabled)
            {
                return;
            }

            var chars = GetBattleChars();
            if (chars == null)
            {
                return;
            }

            var baseIdx = ResolveAllyBaseIndex();
            if (baseIdx < 0)
            {
                return;
            }

            var indexToUid = BuildBattleIndexToUid();
            foreach (var ch in EnumerateChars(chars))
            {
                if (ch == null)
                {
                    continue;
                }

                var index = Convert.ToInt32(GetMember(ch, "Index") ?? -1);
                // 只处理我方 decade（0 或 10）；偷袭时前后排可能对调，不能再用 local 0~4=人 / 5~9=宠
                if (index < baseIdx || index > baseIdx + 9)
                {
                    continue;
                }

                var within = index - baseIdx; // 0~9
                var slot = within % 5; // 同列 1~5 号
                var ownerIndex = within < 5 ? baseIdx + within + 5 : baseIdx + within - 5;
                // 人宠配对：同 slot 的 ±5；Uid 档挂在人物 Index 上
                var uidIndex = IsBattleCharPlayer(ch) ? index : (IsBattleCharMonster(ch) ? ownerIndex : (within < 5 ? index : ownerIndex));
                var cfg = ResolveCfgForBattleIndex(uidIndex, slot, indexToUid);

                // 优先用 Bcflag：PLAYER=人物，MON=宠/怪。偷袭前后排对调时仍正确。
                if (IsBattleCharPlayer(ch))
                {
                    if (cfg.SetCharAnim || cfg.SetRoleHalo || cfg.SetRideSkin)
                    {
                        ApplyChar(ch, cfg);
                    }
                }
                else if (IsBattleCharMonster(ch))
                {
                    if (cfg.SetPetAnim || cfg.SetPerfect || cfg.SetMaxCrest)
                    {
                        ApplyPet(ch, cfg);
                    }
                }
                else if (within <= 4)
                {
                    // 旗标缺失时的旧逻辑回退（正常站位）
                    if (cfg.SetCharAnim || cfg.SetRoleHalo || cfg.SetRideSkin)
                    {
                        ApplyChar(ch, cfg);
                    }
                }
                else if (within <= 9)
                {
                    if (cfg.SetPetAnim || cfg.SetPerfect || cfg.SetMaxCrest)
                    {
                        ApplyPet(ch, cfg);
                    }
                }
            }
        }
        catch
        {
            // ignore — 绝不因个别槽位/缺宠打断进战
        }
    }

    private static SlotCfg ResolveCfgForBattleIndex(int battleIndex, int localSlot, Dictionary<int, string> indexToUid)
    {
        if (indexToUid != null && indexToUid.TryGetValue(battleIndex, out var uid)
            && !string.IsNullOrEmpty(uid) && UidProfiles.TryGetValue(uid, out var byUid) && byUid.HasAny)
        {
            return byUid;
        }

        if (localSlot >= 0 && localSlot < 5)
        {
            return Slots[localSlot];
        }

        return default;
    }

    // BC_FLAG.PLAYER = 4, MON = 8（与客户端枚举一致）
    private const long BcFlagPlayer = 4L;
    private const long BcFlagMonster = 8L;

    private static long ReadBcFlag(object ch)
    {
        try
        {
            var v = GetMember(ch, "Bcflag") ?? GetMember(ch, "bcflag");
            if (v == null)
            {
                return 0;
            }

            return Convert.ToInt64(v);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsBattleCharPlayer(object ch)
    {
        var f = ReadBcFlag(ch);
        return (f & BcFlagPlayer) != 0 && (f & BcFlagMonster) == 0;
    }

    private static bool IsBattleCharMonster(object ch)
    {
        var f = ReadBcFlag(ch);
        return (f & BcFlagMonster) != 0;
    }

    private static Dictionary<int, string> BuildBattleIndexToUid()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var holder = FindType("BattleRoleContainer");
            var f = holder?.GetField("AccountIndexDic", BindingFlags.Public | BindingFlags.Static);
            var dic = f?.GetValue(null) as IDictionary;
            if (dic == null)
            {
                return map;
            }

            foreach (DictionaryEntry kv in dic)
            {
                var uid = Convert.ToString(kv.Key ?? "");
                if (string.IsNullOrEmpty(uid))
                {
                    continue;
                }

                var idx = Convert.ToInt32(kv.Value ?? -1);
                if (idx >= 0)
                {
                    map[idx] = uid;
                }
            }
        }
        catch
        {
            // ignore
        }

        return map;
    }

    private static void ApplyChar(object ch, SlotCfg cfg)
    {
        // 人物 AnimationId / RoleHalo(Grano) / RideSkin(配置Id)。怪光环 Halo 不碰。
        if (cfg.SetCharAnim)
        {
            SetMember(ch, "AnimationId", cfg.CharAnim);
        }

        if (cfg.SetRoleHalo)
        {
            SetMember(ch, "RoleHalo", cfg.RoleHalo);
        }

        if (cfg.SetRideSkin)
        {
            // 游戏用 RideSkin→表 Id 查 Grano 再 SetRide；兼容误填 Grano
            SetMember(ch, "RideSkin", ResolveRideSkinConfigId(cfg.RideSkin));
        }
    }

    /// <summary>
    /// 写入 Proto/CharacterData.RideSkin 的值：
    /// - 人物坐骑表 Id（1~12）→ 原样（游戏 DataMap 能查到）
    /// - 骑宠皮 Id（8206/8207 等）→ 改写为 Grano（动画 Id）
    /// - 已是 Grano（≥100000）→ 原样
    /// 配合 hotfix 对 RideSkinGrano 的回退：表里没有且 ≥100000 时直接当动画 Id 给 SetRide。
    /// （运行时反射注入 DataMap 在 HybridCLR 下不可靠，故不用。）
    /// </summary>
    private static int ResolveRideSkinConfigId(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        try
        {
            EnsurePetRideAliasesLoaded();
            var tb = GetRidePetSkinTable();
            if (tb != null)
            {
                var dataMap = GetMember(tb, "DataMap") as IDictionary;
                if (dataMap != null && dataMap.Contains(value))
                {
                    return value;
                }

                var dataList = GetMember(tb, "DataList") as IEnumerable;
                if (dataList != null)
                {
                    foreach (var row in dataList)
                    {
                        if (row == null)
                        {
                            continue;
                        }

                        var grano = Convert.ToInt32(GetMember(row, "Grano") ?? 0);
                        if (grano == value)
                        {
                            return Convert.ToInt32(GetMember(row, "Id") ?? value);
                        }
                    }
                }
            }

            int aliasGrano;
            if (_petRideIdToGrano.TryGetValue(value, out aliasGrano) && aliasGrano > 0)
            {
                return aliasGrano;
            }

            if (value >= 100000)
            {
                return value;
            }
        }
        catch
        {
            // ignore
        }

        return value;
    }

    // 骑宠皮 Id→Grano（与 export_pet_appear_bin / ride_skin.json 对齐；json 可覆盖）
    private static readonly Dictionary<int, int> _petRideIdToGrano = new Dictionary<int, int>
    {
        { 1, 101330 },
        { 11, 101011 },
        { 12, 101711 },
        { 13, 131000 },
        { 16, 110338 },
        { 4098, 107011 },
        { 4099, 101722 },
        { 4100, 101503 },
        { 4101, 101202 },
        { 4102, 101522 },
        { 4103, 110530 },
        { 8200, 110384 },
        { 8201, 101205 },
        { 8202, 101031 },
        { 8206, 131001 },
        { 8207, 111638 },
    };

    private static bool _petRideAliasFileLoaded;

    private static void EnsurePetRideAliasesLoaded()
    {
        if (_petRideAliasFileLoaded)
        {
            return;
        }

        _petRideAliasFileLoaded = true;
        try
        {
            foreach (var path in RideSkinJsonCandidates())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                MergePetRideAliasesFromJson(File.ReadAllText(path, Encoding.UTF8));
                break;
            }
        }
        catch
        {
            // 内置表足够
        }
    }

    private static IEnumerable<string> RideSkinJsonCandidates()
    {
        var list = new List<string>();
        try
        {
            var dataPath = ReadUnityDataPath();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                list.Add(Path.Combine(gameRoot, "tools", "ride_skin.json"));
                list.Add(Path.Combine(dataPath, "assets", "hotfixdata", "ride_skin.json"));
            }
        }
        catch
        {
            // ignore
        }

        list.Add(@"E:\cross\魔力宝贝：序章\tools\ride_skin.json");
        return list;
    }

    /// <summary>从 ride_skin.json 合并 kind=pet_skin 的 id/grano（简易扫描，无第三方 JSON 库）。</summary>
    private static void MergePetRideAliasesFromJson(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var pos = 0;
        while (pos < text.Length)
        {
            var kindIdx = text.IndexOf("\"kind\"", pos, StringComparison.OrdinalIgnoreCase);
            if (kindIdx < 0)
            {
                break;
            }

            var colon = text.IndexOf(':', kindIdx);
            if (colon < 0)
            {
                break;
            }

            var q1 = text.IndexOf('"', colon + 1);
            if (q1 < 0)
            {
                break;
            }

            var q2 = text.IndexOf('"', q1 + 1);
            if (q2 < 0)
            {
                break;
            }

            var kind = text.Substring(q1 + 1, q2 - q1 - 1);
            pos = q2 + 1;
            if (!string.Equals(kind, "pet_skin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 在该对象附近取 id / grano（向前向后各 200 字符）
            var start = Math.Max(0, kindIdx - 80);
            var end = Math.Min(text.Length, q2 + 200);
            var slice = text.Substring(start, end - start);
            int id, grano;
            if (!TryReadJsonInt(slice, "id", out id) || !TryReadJsonInt(slice, "grano", out grano))
            {
                continue;
            }

            if (id > 0 && grano > 0)
            {
                _petRideIdToGrano[id] = grano;
            }
        }
    }

    private static bool TryReadJsonInt(string slice, string key, out int value)
    {
        value = 0;
        var keyTok = "\"" + key + "\"";
        var idx = slice.IndexOf(keyTok, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var colon = slice.IndexOf(':', idx + keyTok.Length);
        if (colon < 0)
        {
            return false;
        }

        var p = colon + 1;
        while (p < slice.Length && char.IsWhiteSpace(slice[p]))
        {
            p++;
        }

        var neg = false;
        if (p < slice.Length && slice[p] == '-')
        {
            neg = true;
            p++;
        }

        if (p >= slice.Length || !char.IsDigit(slice[p]))
        {
            return false;
        }

        long n = 0;
        while (p < slice.Length && char.IsDigit(slice[p]))
        {
            n = n * 10 + (slice[p] - '0');
            if (n > int.MaxValue)
            {
                return false;
            }

            p++;
        }

        value = (int)(neg ? -n : n);
        return true;
    }

    private static object GetRidePetSkinTable()
    {
        var mgrType = FindType("ConfigManager");
        if (mgrType == null)
        {
            return null;
        }

        object instance = null;
        for (var cur = mgrType; cur != null; cur = cur.BaseType)
        {
            var inst = cur.GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                ?.GetValue(null, null);
            if (inst != null)
            {
                instance = inst;
                break;
            }
        }

        if (instance == null)
        {
            return null;
        }

        return instance.GetType()
            .GetMethod("GetTbRidePetSkinConfig", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(instance, null);
    }

    /// <summary>往 other_tbridepetskinconfig.DataMap 注入 Id→Grano，供 RideSkinGrano 查询。</summary>
    private static void EnsureRideAliasRow(object tb, IDictionary dataMap, int id, int grano)
    {
        if (tb == null || dataMap == null || id <= 0 || grano <= 0)
        {
            return;
        }

        if (dataMap.Contains(id))
        {
            return;
        }

        var rowType = FindType("cfg.Other.RidePetSkinConfig") ?? FindType("RidePetSkinConfig");
        if (rowType == null)
        {
            return;
        }

        object row;
        try
        {
            row = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(rowType);
        }
        catch
        {
            return;
        }

        if (!TrySetRideRowField(row, "Id", id) || !TrySetRideRowField(row, "Grano", grano))
        {
            return;
        }

        TrySetRideRowField(row, "Time", 0);
        TrySetRideRowField(row, "Icon", 0);
        TrySetRideRowField(row, "GoRun", 0);
        TrySetRideRowField(row, "Name", "alias");
        TrySetRideRowField(row, "Memo", "seqchapter");

        try
        {
            dataMap[id] = row;
            var list = GetMember(tb, "DataList") as IList;
            list?.Add(row);
        }
        catch
        {
            // ignore
        }
    }

    private static bool TrySetRideRowField(object row, string name, object value)
    {
        try
        {
            var t = row.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null)
            {
                // private set 的自动属性：CanWrite 仍可能为 true
                if (p.GetSetMethod(nonPublic: true) != null)
                {
                    p.SetValue(row, Convert.ChangeType(value, p.PropertyType), null);
                    return true;
                }
            }

            var f = t.GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(row, Convert.ChangeType(value, f.FieldType));
                return true;
            }

            f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (f != null)
            {
                f.SetValue(row, Convert.ChangeType(value, f.FieldType));
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void ApplyPet(object ch, SlotCfg cfg)
    {
        // 仅覆盖显式配置的字段；0/未设置 = 游戏原样
        if (cfg.SetPetAnim)
        {
            SetMember(ch, "AnimationId", cfg.PetAnim);
        }

        if (cfg.SetPerfect)
        {
            SetMember(ch, "Perfectpet", cfg.Perfect != 0 ? 1 : 0);
        }

        if (cfg.SetMaxCrest)
        {
            SetMember(ch, "MaxCrestUseId", cfg.MaxCrest);
        }
    }

    /// <summary>粘贴 CGAP1:… → 写本地 json + 按在线角色 Uid 写入 AppData 档。成功返回 null。</summary>
    public static string ImportFromCode(string text)
    {
        try
        {
            var payload = ExtractPayload(text);
            var raw = Convert.FromBase64String(payload);
            bool enabledInCode;
            var slots = DecodeBinary(raw, out enabledInCode);
            var enabled = true;
            _ = enabledInCode;
            WriteConfigEverywhere(enabled, slots);
            var uids = CollectOnlineUids();
            MergeSaveUidProfiles(uids, slots, enabled);
            ReloadConfig();
            LoadUidStore(force: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>导入推荐方案（等同立刻粘贴该套 CGAP1）。成功返回 null。</summary>
    public static string ImportPreset(int index)
    {
        if (index == 1)
        {
            return ImportFromCode(Preset1Code);
        }

        if (index == 2)
        {
            return ImportFromCode(Preset2Code);
        }

        if (index == 3)
        {
            return ImportFromCode(Preset3Code);
        }

        if (index == 4)
        {
            return ImportFromCode(Preset4Code);
        }

        return "无效方案编号";
    }

    /// <summary>清空当前登录角色（SelectPlayerUid / MainPlayerUid）在 AppData 中的形象档。</summary>
    public static string ClearCurrentUidProfile()
    {
        try
        {
            var uid = GetCurrentUid();
            if (string.IsNullOrEmpty(uid))
            {
                return "未找到当前角色 Uid";
            }

            LoadUidStore(force: true);
            if (!UidProfiles.Remove(uid))
            {
                WriteUidStoreAtomic(_enabled);
                return "当前角色无存档: " + uid;
            }

            WriteUidStoreAtomic(_enabled);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>上线/打开形象页时调用：只读 AppData Uid 档到内存。</summary>
    public static void LoadUidProfilesOnReady()
    {
        try
        {
            LoadUidStore(force: true);
        }
        catch
        {
            // ignore
        }
    }

    public static string GetUidStorePathPublic()
    {
        return ResolveUidStorePath();
    }

    /// <summary>保存钩子开关（保留现有槽位配置），并写盘。</summary>
    public static void SetEnabled(bool enabled)
    {
        EnsureConfig(force: true);
        try
        {
            var slots = SnapshotSlots();
            WriteConfigEverywhere(enabled, slots);
            _enabled = enabled;
            _cfgLoaded = true;
            _lastReloadUtc = DateTime.UtcNow;
        }
        catch
        {
            _enabled = enabled;
        }
    }

    private static SlotCfg[] SnapshotSlots()
    {
        var slots = new SlotCfg[5];
        for (var i = 0; i < 5; i++)
        {
            slots[i] = Slots[i];
        }

        return slots;
    }

    private static SlotCfg[] EmptySlots()
    {
        return new SlotCfg[5];
    }

    private static void WriteConfigEverywhere(bool enabled, SlotCfg[] slots)
    {
        var path = ResolveWritePath();
        WriteJson(path, enabled, slots);
        try
        {
            var dataPath = ReadUnityDataPath();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var hf = Path.Combine(dataPath, "assets", "hotfixdata", "battle_appear.json");
                if (!string.Equals(Path.GetFullPath(hf), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(hf, enabled, slots);
                }
            }
        }
        catch
        {
            // ignore
        }

        // 同步内存
        _enabled = enabled;
        for (var i = 0; i < 5 && i < slots.Length; i++)
        {
            Slots[i] = slots[i];
        }
    }

    public static void ReloadConfig()
    {
        _cfgLoaded = false;
        _lastReloadUtc = DateTime.MinValue;
        EnsureConfig(force: true);
    }

    public static bool IsEnabled()
    {
        EnsureConfig(force: false);
        return _enabled;
    }

    public static string Status()
    {
        EnsureConfig(force: false);
        LoadUidStore(force: false);
        var sb = new StringBuilder();
        sb.Append("enabled=").Append(_enabled);
        sb.Append(" profiles=").Append(UidProfiles.Count);
        sb.Append(" uidFile=").Append(string.IsNullOrEmpty(_uidStorePath) ? ResolveUidStorePath() : _uidStorePath);
        if (!string.IsNullOrEmpty(_uidStoreError))
        {
            sb.Append(" uidErr=").Append(_uidStoreError);
        }

        if (!string.IsNullOrEmpty(_loadError))
        {
            sb.Append(" err=").Append(_loadError);
        }

        var cur = GetCurrentUid();
        if (!string.IsNullOrEmpty(cur))
        {
            sb.Append(" cur=").Append(cur);
            if (UidProfiles.TryGetValue(cur, out var p) && p.HasAny)
            {
                sb.Append(" [char=").Append(p.CharAnim);
                sb.Append(" pet=").Append(p.PetAnim);
                sb.Append(" crest=").Append(p.MaxCrest);
                sb.Append(" halo=").Append(p.RoleHalo);
                sb.Append(" ride=").Append(p.RideSkin).Append(']');
            }
            else
            {
                sb.Append(" [无当前Uid档]");
            }
        }

        return sb.ToString();
    }

    private static string ExtractPayload(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("空代码");
        }

        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s[0] == '#')
            {
                continue;
            }

            if (s.StartsWith("###", StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append(s);
        }

        var blob = sb.ToString();
        if (blob.Length == 0)
        {
            throw new InvalidOperationException("没有可解析内容");
        }

        if (blob.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            blob = blob.Substring(CodePrefix.Length);
        }

        // 去空白
        var clean = new StringBuilder(blob.Length);
        foreach (var c in blob)
        {
            if (!char.IsWhiteSpace(c))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    private static SlotCfg[] DecodeBinary(byte[] data, out bool enabled)
    {
        if (data == null || data.Length < 7 + 5 * 21)
        {
            throw new InvalidOperationException("代码过短");
        }

        if (data[0] != (byte)'B' || data[1] != (byte)'A' || data[2] != (byte)'P' || data[3] != (byte)'1')
        {
            throw new InvalidOperationException("magic 不是 BAP1");
        }

        if (data[4] != 1)
        {
            throw new InvalidOperationException("不支持的版本 " + data[4]);
        }

        enabled = data[5] != 0;
        var pos = 7;
        var slots = new SlotCfg[5];
        for (var i = 0; i < 5; i++)
        {
            var petAnim = BitConverter.ToInt32(data, pos);
            pos += 4;
            var roleHalo = BitConverter.ToInt32(data, pos);
            pos += 4;
            var perfectBin = data[pos];
            pos += 1;
            var maxCrest = BitConverter.ToInt32(data, pos);
            pos += 4;
            var charAnim = BitConverter.ToInt32(data, pos);
            pos += 4;
            var rideSkin = BitConverter.ToInt32(data, pos);
            pos += 4;

            var cfg = new SlotCfg();
            // 0 = 不配置
            cfg.SetPetAnim = petAnim > 0;
            cfg.PetAnim = petAnim;
            cfg.SetRoleHalo = roleHalo > 0;
            cfg.RoleHalo = roleHalo > 0 ? roleHalo : 0;
            if (perfectBin == 1)
            {
                cfg.SetPerfect = true;
                cfg.Perfect = 1;
            }
            else if (perfectBin == 2)
            {
                // 旧码：强制关满档；新约定 0=不配置，仅兼容旧粘贴码
                cfg.SetPerfect = true;
                cfg.Perfect = 0;
            }

            cfg.SetMaxCrest = maxCrest > 0;
            cfg.MaxCrest = maxCrest;
            cfg.SetCharAnim = charAnim > 0;
            cfg.CharAnim = charAnim > 0 ? charAnim : 0;
            cfg.SetRideSkin = rideSkin > 0;
            cfg.RideSkin = rideSkin > 0 ? rideSkin : 0;
            cfg.HasAny = cfg.SetPetAnim || cfg.SetPerfect || cfg.SetMaxCrest || cfg.SetCharAnim
                || cfg.SetRoleHalo || cfg.SetRideSkin;
            slots[i] = cfg;
        }

        return slots;
    }

    private static void WriteJson(string path, bool enabled, SlotCfg[] slots)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"enabled\": ").Append(enabled ? "true" : "false").AppendLine(",");
        sb.AppendLine("  \"comment\": \"0=不配置。应用 char_anim/role_halo/ride_skin/pet_anim/perfect/max_crest。\",");
        sb.AppendLine("  \"slots\": [");
        for (var i = 0; i < 5; i++)
        {
            var s = slots[i];
            sb.Append("    { \"slot\": ").Append(i + 1);
            sb.Append(", \"pet_anim\": ").Append(s.SetPetAnim ? s.PetAnim : 0);
            sb.Append(", \"role_halo\": ").Append(s.SetRoleHalo ? s.RoleHalo : 0);
            // json: 仅 1=强制开；0=不配置（旧二进制强制关导入后也会落成 0）
            sb.Append(", \"perfect\": ").Append(s.SetPerfect && s.Perfect != 0 ? 1 : 0);
            sb.Append(", \"max_crest\": ").Append(s.SetMaxCrest ? s.MaxCrest : 0);
            sb.Append(", \"char_anim\": ").Append(s.CharAnim > 0 ? s.CharAnim : 0);
            sb.Append(", \"ride_skin\": ").Append(s.RideSkin > 0 ? s.RideSkin : 0);
            sb.Append(" }");
            if (i < 4)
            {
                sb.Append(',');
            }

            sb.AppendLine();
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string ResolveWritePath()
    {
        try
        {
            var dataPath = ReadUnityDataPath();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                return Path.Combine(gameRoot, "tools", "battle_appear.json");
            }
        }
        catch
        {
            // ignore
        }

        return @"E:\cross\魔力宝贝：序章\tools\battle_appear.json";
    }

    private static int ResolveAllyBaseIndex()
    {
        try
        {
            var holder = FindType("BattleDataHolder");
            var p = holder?.GetProperty("selfRoleIndex", BindingFlags.Public | BindingFlags.Static);
            if (p != null)
            {
                return Convert.ToInt32(p.GetValue(null, null)) >= 10 ? 10 : 0;
            }

            var f = holder?.GetField("selfRoleIndex", BindingFlags.Public | BindingFlags.Static);
            if (f != null)
            {
                return Convert.ToInt32(f.GetValue(null)) >= 10 ? 10 : 0;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static object GetBattleChars()
    {
        var instance = GetManagerInstance("BattleManager");
        return instance == null ? null : GetMember(instance, "BattleChars");
    }

    private static object GetManagerInstance(string typeName)
    {
        var mgrType = FindType(typeName);
        if (mgrType == null)
        {
            return null;
        }

        for (var cur = mgrType; cur != null; cur = cur.BaseType)
        {
            try
            {
                var inst = cur.GetProperty(
                        "Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                    ?.GetValue(null, null);
                if (inst != null)
                {
                    return inst;
                }
            }
            catch
            {
                // next
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateChars(object chars)
    {
        if (chars is IEnumerable en)
        {
            foreach (var item in en)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }

    private static void EnsureConfig(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && _cfgLoaded && (now - _lastReloadUtc).TotalSeconds < 1.5)
        {
            return;
        }

        _lastReloadUtc = now;
        var found = false;
        foreach (var path in ConfigCandidates())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                LoadConfig(path);
                _loadedFrom = path;
                _loadError = "";
                found = true;
                break;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
            }
        }

        if (!found)
        {
            // 无本地 json：钩子默认关；有 Uid 档也不自动开，需面板手动开
            _enabled = false;
            if (string.IsNullOrEmpty(_loadError))
            {
                _loadError = "battle_appear.json not found (钩子默认关)";
            }
        }

        _cfgLoaded = true;
        LoadUidStore(force: true);
        // 不因存在 Uid 档而自动开启；进游戏默认关，由面板「钩子」开关控制
    }

    private static void LoadConfig(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        _enabled = ReadBool(text, "enabled", false);
        for (var s = 0; s < 5; s++)
        {
            Slots[s] = default;
        }

        var slotsIdx = text.IndexOf("\"slots\"", StringComparison.OrdinalIgnoreCase);
        if (slotsIdx < 0)
        {
            return;
        }

        var arrStart = text.IndexOf('[', slotsIdx);
        if (arrStart < 0)
        {
            return;
        }

        var pos = arrStart + 1;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] != '{' && text[pos] != ']')
            {
                pos++;
            }

            if (pos >= text.Length || text[pos] == ']')
            {
                break;
            }

            var start = pos;
            var brace = 0;
            for (; pos < text.Length; pos++)
            {
                if (text[pos] == '{')
                {
                    brace++;
                }
                else if (text[pos] == '}')
                {
                    brace--;
                    if (brace == 0)
                    {
                        pos++;
                        break;
                    }
                }
            }

            ParseSlotObject(text.Substring(start, pos - start));
        }
    }

    private static void ParseSlotObject(string obj)
    {
        var slot = ReadInt(obj, "slot", 0);
        if (slot < 1 || slot > 5)
        {
            return;
        }

        var cfg = new SlotCfg();
        // 所有 ID 类字段：>0 才配置；0 = 保持游戏原样
        if (TryReadInt(obj, "pet_anim", out var petAnim) && petAnim > 0)
        {
            cfg.SetPetAnim = true;
            cfg.PetAnim = petAnim;
        }

        // role_halo = 人物光环 Grano；忽略旧 pet_halo
        if (TryReadInt(obj, "role_halo", out var roleHalo) && roleHalo > 0)
        {
            cfg.SetRoleHalo = true;
            cfg.RoleHalo = roleHalo;
        }

        // perfect: 仅 1=强制开；0/-1=不配置
        if (TryReadIntAllowZero(obj, "perfect", out var perfect) && perfect == 1)
        {
            cfg.SetPerfect = true;
            cfg.Perfect = 1;
        }

        if (TryReadInt(obj, "max_crest", out var crest) && crest > 0)
        {
            cfg.SetMaxCrest = true;
            cfg.MaxCrest = crest;
        }

        if (TryReadInt(obj, "char_anim", out var ca) && ca > 0)
        {
            cfg.SetCharAnim = true;
            cfg.CharAnim = ca;
        }

        if (TryReadInt(obj, "ride_skin", out var rs) && rs > 0)
        {
            cfg.SetRideSkin = true;
            cfg.RideSkin = rs;
        }

        cfg.HasAny = cfg.SetPetAnim || cfg.SetPerfect || cfg.SetMaxCrest || cfg.SetCharAnim
            || cfg.SetRoleHalo || cfg.SetRideSkin;
        Slots[slot - 1] = cfg;
    }

    private static bool TryReadInt(string json, string key, out int value)
    {
        if (!TryReadIntAllowZero(json, key, out value))
        {
            return false;
        }

        // 0 / 负 = 不设置（ID 类）
        if (value <= 0)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadIntAllowZero(string json, string key, out int value)
    {
        value = 0;
        var needle = "\"" + key + "\"";
        var idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var colon = json.IndexOf(':', idx + needle.Length);
        if (colon < 0)
        {
            return false;
        }

        var p = colon + 1;
        while (p < json.Length && char.IsWhiteSpace(json[p]))
        {
            p++;
        }

        if (p < json.Length && json[p] == 'n')
        {
            return false;
        }

        var end = p;
        if (end < json.Length && (json[end] == '-' || json[end] == '+'))
        {
            end++;
        }

        while (end < json.Length && char.IsDigit(json[end]))
        {
            end++;
        }

        if (end <= p)
        {
            return false;
        }

        if (!int.TryParse(json.Substring(p, end - p), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value < 0)
        {
            return false;
        }

        return true;
    }

    private static int ReadInt(string json, string key, int defaultValue)
    {
        return TryReadIntAllowZero(json, key, out var v) ? v : defaultValue;
    }

    private static bool ReadBool(string json, string key, bool defaultValue)
    {
        var needle = "\"" + key + "\"";
        var idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return defaultValue;
        }

        var colon = json.IndexOf(':', idx + needle.Length);
        if (colon < 0)
        {
            return defaultValue;
        }

        var p = colon + 1;
        while (p < json.Length && char.IsWhiteSpace(json[p]))
        {
            p++;
        }

        if (p + 4 <= json.Length && string.Compare(json, p, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return true;
        }

        if (p + 5 <= json.Length && string.Compare(json, p, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return false;
        }

        return defaultValue;
    }

    private static IEnumerable<string> ConfigCandidates()
    {
        var list = new List<string>();
        try
        {
            var dataPath = ReadUnityDataPath();
            if (!string.IsNullOrEmpty(dataPath))
            {
                var gameRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                list.Add(Path.Combine(gameRoot, "tools", "battle_appear.json"));
                list.Add(Path.Combine(dataPath, "assets", "hotfixdata", "battle_appear.json"));
            }
        }
        catch
        {
            // ignore
        }

        list.Add(@"E:\cross\魔力宝贝：序章\tools\battle_appear.json");
        return list;
    }

    private static string ReadUnityDataPath()
    {
        try
        {
            var app = FindType("UnityEngine.Application");
            return app?.GetProperty("dataPath", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadUnityPersistentDataPath()
    {
        try
        {
            var app = FindType("UnityEngine.Application");
            return app?.GetProperty("persistentDataPath", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveUidStorePath()
    {
        try
        {
            var p = ReadUnityPersistentDataPath();
            if (!string.IsNullOrEmpty(p))
            {
                return Path.Combine(p, UidStoreFileName);
            }
        }
        catch
        {
            // ignore
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "LocalLow",
            "魔力永恒",
            "魔力宝贝：序章",
            UidStoreFileName);
    }

    private static string GetCurrentUid()
    {
        try
        {
            var holder = FindType("PlayerDataHolder");
            var sel = holder?.GetProperty("SelectPlayerUid", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null) as string;
            if (!string.IsNullOrEmpty(sel))
            {
                return sel;
            }

            return holder?.GetField("MainPlayerUid", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string
                ?? holder?.GetProperty("MainPlayerUid", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> CollectOnlineUids()
    {
        var result = new List<string>();
        try
        {
            var teamMgr = GetManagerInstance("TeamManager");
            var multi = GetMember(teamMgr, "MultiInfo");
            var players = GetMember(multi, "Players") as IList;
            if (players != null)
            {
                foreach (var p in players)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    var uid = Convert.ToString(GetMember(p, "Uid") ?? "");
                    var online = Convert.ToInt32(GetMember(p, "Online") ?? 0);
                    if (!string.IsNullOrEmpty(uid) && online >= 1 && !result.Contains(uid))
                    {
                        result.Add(uid);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (result.Count == 0)
        {
            var cur = GetCurrentUid();
            if (!string.IsNullOrEmpty(cur))
            {
                result.Add(cur);
            }
        }

        return result;
    }

    private static int MergeSaveUidProfiles(List<string> uids, SlotCfg[] slots, bool enabled)
    {
        LoadUidStore(force: true);
        var n = 0;
        if (uids == null || slots == null)
        {
            WriteUidStoreAtomic(enabled);
            return 0;
        }

        for (var i = 0; i < uids.Count && i < 5; i++)
        {
            var uid = uids[i];
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            UidProfiles[uid] = slots[i];
            n++;
        }

        WriteUidStoreAtomic(enabled);
        return n;
    }

    private static void LoadUidStore(bool force)
    {
        if (!force && UidProfiles.Count > 0 && !string.IsNullOrEmpty(_uidStorePath))
        {
            return;
        }

        _uidStorePath = ResolveUidStorePath();
        _uidStoreError = "";
        try
        {
            if (!File.Exists(_uidStorePath))
            {
                return;
            }

            string text;
            using (var fs = new FileStream(_uidStorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                text = sr.ReadToEnd();
            }

            ParseUidStoreText(text);
        }
        catch (Exception ex)
        {
            _uidStoreError = ex.Message;
        }
    }

    private static void ParseUidStoreText(string text)
    {
        UidProfiles.Clear();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 可选全局 enabled
        if (text.IndexOf("\"enabled\"", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _enabled = ReadBool(text, "enabled", _enabled);
        }

        var profilesIdx = text.IndexOf("\"profiles\"", StringComparison.OrdinalIgnoreCase);
        if (profilesIdx < 0)
        {
            return;
        }

        var objStart = text.IndexOf('{', profilesIdx);
        if (objStart < 0)
        {
            return;
        }

        var pos = objStart + 1;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] != '"' && text[pos] != '}')
            {
                pos++;
            }

            if (pos >= text.Length || text[pos] == '}')
            {
                break;
            }

            var keyStart = pos + 1;
            var keyEnd = text.IndexOf('"', keyStart);
            if (keyEnd < 0)
            {
                break;
            }

            var uid = text.Substring(keyStart, keyEnd - keyStart);
            pos = keyEnd + 1;
            var brace = text.IndexOf('{', pos);
            if (brace < 0)
            {
                break;
            }

            var start = brace;
            var depth = 0;
            for (pos = brace; pos < text.Length; pos++)
            {
                if (text[pos] == '{')
                {
                    depth++;
                }
                else if (text[pos] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        pos++;
                        break;
                    }
                }
            }

            var body = text.Substring(start, pos - start);
            var cfg = ParseProfileObject(body);
            if (!string.IsNullOrEmpty(uid) && cfg.HasAny)
            {
                UidProfiles[uid] = cfg;
            }
        }
    }

    private static SlotCfg ParseProfileObject(string obj)
    {
        var cfg = new SlotCfg();
        if (TryReadInt(obj, "pet_anim", out var petAnim) && petAnim > 0)
        {
            cfg.SetPetAnim = true;
            cfg.PetAnim = petAnim;
        }

        if (TryReadInt(obj, "role_halo", out var roleHalo) && roleHalo > 0)
        {
            cfg.SetRoleHalo = true;
            cfg.RoleHalo = roleHalo;
        }

        if (TryReadIntAllowZero(obj, "perfect", out var perfect) && perfect == 1)
        {
            cfg.SetPerfect = true;
            cfg.Perfect = 1;
        }

        if (TryReadInt(obj, "max_crest", out var crest) && crest > 0)
        {
            cfg.SetMaxCrest = true;
            cfg.MaxCrest = crest;
        }

        if (TryReadInt(obj, "char_anim", out var ca) && ca > 0)
        {
            cfg.SetCharAnim = true;
            cfg.CharAnim = ca;
        }

        if (TryReadInt(obj, "ride_skin", out var rs) && rs > 0)
        {
            cfg.SetRideSkin = true;
            cfg.RideSkin = rs;
        }

        cfg.HasAny = cfg.SetPetAnim || cfg.SetPerfect || cfg.SetMaxCrest || cfg.SetCharAnim
            || cfg.SetRoleHalo || cfg.SetRideSkin;
        return cfg;
    }

    private static void WriteUidStoreAtomic(bool enabled)
    {
        var path = ResolveUidStorePath();
        _uidStorePath = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"enabled\": ").Append(enabled ? "true" : "false").AppendLine(",");
        sb.AppendLine("  \"comment\": \"按玩家Uid存形象；多开同文件最后写入为准。\",");
        sb.AppendLine("  \"profiles\": {");
        var keys = new List<string>(UidProfiles.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var uid = keys[i];
            var s = UidProfiles[uid];
            sb.Append("    \"").Append(EscapeJson(uid)).Append("\": {");
            sb.Append(" \"pet_anim\": ").Append(s.SetPetAnim ? s.PetAnim : 0);
            sb.Append(", \"role_halo\": ").Append(s.SetRoleHalo ? s.RoleHalo : 0);
            sb.Append(", \"perfect\": ").Append(s.SetPerfect && s.Perfect != 0 ? 1 : 0);
            sb.Append(", \"max_crest\": ").Append(s.SetMaxCrest ? s.MaxCrest : 0);
            sb.Append(", \"char_anim\": ").Append(s.CharAnim > 0 ? s.CharAnim : 0);
            sb.Append(", \"ride_skin\": ").Append(s.RideSkin > 0 ? s.RideSkin : 0);
            sb.Append(" }");
            if (i < keys.Count - 1)
            {
                sb.Append(',');
            }

            sb.AppendLine();
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            try
            {
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static object GetMember(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p != null)
        {
            return p.GetValue(obj, null);
        }

        return t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(obj);
    }

    private static void SetMember(object obj, string name, object value)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, Convert.ChangeType(value, p.PropertyType), null);
            return;
        }

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (f != null)
        {
            f.SetValue(obj, Convert.ChangeType(value, f.FieldType));
        }
    }

    private static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(name);
                if (t != null)
                {
                    return t;
                }

                foreach (var tt in asm.GetTypes())
                {
                    if (tt.Name == name || tt.FullName == name)
                    {
                        return tt;
                    }
                }
            }
            catch
            {
                // continue
            }
        }

        return null;
    }
}
