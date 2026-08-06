using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// 一键加点 DLL。部署为 hotfixdata/SeqChapterAutoPoint.dll.bytes
/// 由助手面板「脚本」页「一键加点」按钮触发（RunAllFromUi）。
///
/// 人物：按「推荐加点」第一个方案加。职业匹配 PlayerData.Job % 10 == AddPlayerPointConfig.JobType，
///      取 UpPoint[0]（第一套方案，格式 血N攻N强N速N魔N），复刻 RoleBPChildPanel.ApplyPlanAddPoint
///      的分配算法（方案目标值分配 + 剩余点按权重分配），发 RoleManager.SendAddOrCutBp("加点", ...)。
/// 宠物：先加力量。单属性 BP 不能超过总数值的一半（爆点上限 = floor(总BP/2)，
///      总BP = 当前5项BP(除100) + 可加点数）。加到爆点极限，爆了就跳过剩下的点。
///      发 PetManager.SendResetPoint(uid, "加点", index, 0, 力量可加, 0, 0, 0)。
/// </summary>
public static class SeqChapterAutoPoint
{
    public const string AssetPath = "hotfixdata/SeqChapterAutoPoint.dll.bytes";
    public const string TypeName = "SeqChapterAutoPoint";

    /// <summary>分配精度：人物等级每级点数（复刻 ApplyPlanAddPoint）。</summary>
    public const int PointsPerLevel = 4;
    /// <summary>分配精度：人物初始点数（复刻 ApplyPlanAddPoint）。</summary>
    public const int BasePoints = 30;

    public static void Bootstrap()
    {
        // 一次性功能，无后台常驻。
    }

    /// <summary>面板「脚本」页按钮入口：全部角色按方案加点 + 全部宠物加力量。</summary>
    public static int RunAllFromUi()
    {
        try
        {
            var playerCount = AddPointsForAllPlayers();
            var petCount = AddPointsForAllPets();
            var msg = "一键加点完成：人物 " + playerCount + " 个，宠物 " + petCount + " 只";
            Tip(msg);
            return playerCount + petCount;
        }
        catch (Exception ex)
        {
            Tip("一键加点失败: " + RootMessage(ex));
            return 0;
        }
    }

    /// <summary>全部角色按推荐第一方案加点；返回实际加点的角色数。</summary>
    private static int AddPointsForAllPlayers()
    {
        // 角色列表：PlayerDataHolder.GetAllPlayers() → Dictionary<string, PlayerData>
        var allPlayers = GetStaticMethodResult("PlayerDataHolder", "GetAllPlayers") as IDictionary;
        if (allPlayers == null || allPlayers.Count == 0)
        {
            return 0;
        }

        var roleMgr = GetManagerInstance("RoleManager");
        if (roleMgr == null)
        {
            return 0;
        }

        var added = 0;
        foreach (var key in allPlayers.Keys)
        {
            object player = null;
            try
            {
                player = allPlayers[key];
            }
            catch
            {
                continue;
            }

            if (player == null)
            {
                continue;
            }

            if (AddPointsForPlayer(player, roleMgr))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>单角色按推荐第一方案加点；返回是否实际发送。</summary>
    private static bool AddPointsForPlayer(object player, object roleMgr)
    {
        var uid = Convert.ToString(GetMember(player, "Uid") ?? "") ?? "";
        var pointvalue = Convert.ToInt32(GetMember(player, "pointvalue") ?? 0);
        if (string.IsNullOrEmpty(uid) || pointvalue <= 0)
        {
            return false;
        }

        // 职业匹配：job - job % 10 == JobType（复刻 RoleBPChildPanel.OnClickPlanAddPointCallback）
        var job = Convert.ToInt32(GetMember(player, "Job") ?? 0);
        var level = Convert.ToInt32(GetMember(player, "level") ?? 1);
        var plan = FindFirstPlan(job);
        if (plan == null || plan.Length == 0)
        {
            return false;
        }

        // 解析方案：weights[5] = [血, 攻, 强, 速, 魔]
        var weights = ParsePlan(plan);
        var wsum = 0;
        for (var i = 0; i < 5; i++)
        {
            wsum += weights[i];
        }

        if (wsum <= 0)
        {
            return false;
        }

        // 复刻 ApplyPlanAddPoint：
        // totalAdd = 30 + (level - 1) * 4；DistributeByWeight(totalAdd, weights) → planAdd
        var totalAdd = BasePoints + (level - 1) * PointsPerLevel;
        var planAdd = DistributeByWeight(totalAdd, weights);

        // 当前属性（÷100）
        var cur = new[]
        {
            Convert.ToInt32(GetMember(player, "vital") ?? 0) / 100,
            Convert.ToInt32(GetMember(player, "str") ?? 0) / 100,
            Convert.ToInt32(GetMember(player, "tgh") ?? 0) / 100,
            Convert.ToInt32(GetMember(player, "dex") ?? 0) / 100,
            Convert.ToInt32(GetMember(player, "magic") ?? 0) / 100,
        };

        // maxAdd[i] = max(0, planAdd[i] - cur[i])
        var maxAdd = new int[5];
        for (var i = 0; i < 5; i++)
        {
            maxAdd[i] = Math.Max(0, planAdd[i] - cur[i]);
        }

        // pool = pointvalue → DistributeByWeightWithCap(pool, weights, maxAdd) → finalAdd
        var finalAdd = DistributeByWeightWithCap(pointvalue, weights, maxAdd);

        var total = finalAdd[0] + finalAdd[1] + finalAdd[2] + finalAdd[3] + finalAdd[4];
        if (total <= 0)
        {
            return false;
        }

        SendPlayerAddPoint(roleMgr, uid, finalAdd);
        return true;
    }

    /// <summary>全部角色的宠物先加力量到爆点极限；返回实际加点的宠物数。</summary>
    private static int AddPointsForAllPets()
    {
        var allPlayers = GetStaticMethodResult("PlayerDataHolder", "GetAllPlayers") as IDictionary;
        if (allPlayers == null || allPlayers.Count == 0)
        {
            return 0;
        }

        var petMgr = GetManagerInstance("PetManager");
        if (petMgr == null)
        {
            return 0;
        }

        var added = 0;
        foreach (var key in allPlayers.Keys)
        {
            var uid = Convert.ToString(key) ?? "";
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            var pets = GetPetDatasFromUid(uid);
            if (pets == null)
            {
                continue;
            }

            for (var i = 0; i < pets.Count; i++)
            {
                var pet = pets[i];
                if (pet == null)
                {
                    continue;
                }

                if (AddPointsForPet(uid, i, pet, petMgr))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>单只宠物：先加力量到爆点极限；返回是否实际发送。</summary>
    private static bool AddPointsForPet(string uid, int index, object pet, object petMgr)
    {
        // 空槽判断：useFlag == 0 或 Pointvalue <= 0
        var useFlag = Convert.ToInt32(GetMember(pet, "useFlag") ?? 0);
        if (useFlag == 0)
        {
            return false;
        }

        var data = GetMember(pet, "data");
        if (data == null)
        {
            return false;
        }

        // 用 data.Index 作为发送索引（与 PetBpPanel 一致；列表位置通常相同，但 data.Index 更权威）
        var dataIndex = Convert.ToInt32(GetMember(data, "Index") ?? index);
        index = dataIndex;

        var pointvalue = Convert.ToInt32(GetMember(data, "Pointvalue") ?? 0);
        if (pointvalue <= 0)
        {
            return false;
        }

        // 当前5项 BP（÷100）
        var vital = Convert.ToInt32(GetMember(data, "Vital") ?? 0) / 100;
        var atk = Convert.ToInt32(GetMember(data, "Atk") ?? 0) / 100;
        var def = Convert.ToInt32(GetMember(data, "Def") ?? 0) / 100;
        var quick = Convert.ToInt32(GetMember(data, "Quick") ?? 0) / 100;
        var magic = Convert.ToInt32(GetMember(data, "Magic") ?? 0) / 100;

        // 爆点上限 = floor((当前5项 + 可加点) / 2)
        var totalBp = vital + atk + def + quick + magic + pointvalue;
        var cap = (int)Math.Floor(totalBp / 2.0);

        // 当前力量
        var curAtk = atk;
        // 力量可加 = max(0, cap - curAtk)，再 clamp 到可加点数
        var addStr = Math.Max(0, cap - curAtk);
        addStr = Math.Min(addStr, pointvalue);
        if (addStr <= 0)
        {
            return false;
        }

        SendPetAddStr(petMgr, uid, index, addStr);
        return true;
    }

    private static string FindFirstPlan(int job)
    {
        try
        {
            var cfgMgr = GetManagerInstance("ConfigManager");
            if (cfgMgr == null)
            {
                return null;
            }

            var table = CallMethod(cfgMgr, "GetTbAddPlayerPointConfig");
            if (table == null)
            {
                return null;
            }

            var dataList = GetMember(table, "DataList") as IEnumerable;
            if (dataList == null)
            {
                return null;
            }

            foreach (var cfg in dataList)
            {
                if (cfg == null)
                {
                    continue;
                }

                var jobType = Convert.ToInt32(GetMember(cfg, "JobType") ?? 0);
                if (job - (job % 10) != jobType)
                {
                    continue;
                }

                var upPoint = GetMember(cfg, "UpPoint") as IList;
                if (upPoint == null || upPoint.Count == 0)
                {
                    return null;
                }

                // 第一套方案
                return Convert.ToString(upPoint[0]) ?? "";
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>解析 方案字符串（血N攻N强N速N魔N 或 N血N攻N强N速N魔 均兼容）→ [血, 攻, 强, 速, 魔]。</summary>
    private static int[] ParsePlan(string plan)
    {
        var result = new int[5];
        if (string.IsNullOrEmpty(plan))
        {
            return result;
        }

        // 属性字符（与 m_planValue 键一致）
        var keys = new[] { "血", "攻", "强", "速", "魔" };
        var i = 0;
        while (i < plan.Length)
        {
            if (!char.IsDigit(plan[i]))
            {
                i++;
                continue;
            }

            var numStart = i;
            while (i < plan.Length && char.IsDigit(plan[i]))
            {
                i++;
            }

            var numEnd = i;
            var value = int.TryParse(plan.Substring(numStart, numEnd - numStart), out var v) ? v : 0;

            // 数字前面的属性字符（格式：血1攻2强3速4魔5）
            if (numStart > 0)
            {
                var prev = plan.Substring(numStart - 1, 1);
                for (var k = 0; k < keys.Length; k++)
                {
                    if (string.Equals(prev, keys[k], StringComparison.Ordinal))
                    {
                        result[k] = value;
                        break;
                    }
                }
            }

            // 数字后面的属性字符（格式：1血2攻3强4速5魔）
            if (numEnd < plan.Length)
            {
                var next = plan.Substring(numEnd, 1);
                for (var k = 0; k < keys.Length; k++)
                {
                    if (string.Equals(next, keys[k], StringComparison.Ordinal))
                    {
                        result[k] = value;
                        break;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>复刻 RoleBPChildPanel.DistributeByWeight：按权重把 total 分配给各元素。</summary>
    private static int[] DistributeByWeight(int total, int[] weights)
    {
        var n = weights.Length;
        var result = new int[n];
        if (total <= 0)
        {
            return result;
        }

        var wsum = 0;
        for (var i = 0; i < n; i++)
        {
            wsum += weights[i];
        }

        if (wsum <= 0)
        {
            return result;
        }

        var assigned = 0;
        var fracs = new double[n];
        for (var i = 0; i < n; i++)
        {
            var raw = (double)total * weights[i] / wsum;
            var floor = (int)Math.Floor(raw);
            result[i] = floor;
            fracs[i] = raw - floor;
            assigned += floor;
        }

        var remaining = total - assigned;
        // 最大小数优先，逐个 +1
        while (remaining > 0)
        {
            var best = -1;
            var bestFrac = -1.0;
            for (var i = 0; i < n; i++)
            {
                if (weights[i] <= 0)
                {
                    continue;
                }

                if (fracs[i] > bestFrac)
                {
                    bestFrac = fracs[i];
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            result[best]++;
            fracs[best] = 0;
            remaining--;
        }

        return result;
    }

    /// <summary>复刻 RoleBPChildPanel.DistributeByWeightWithCap：按权重分配 pool，每项不超过 maxAdd。</summary>
    private static int[] DistributeByWeightWithCap(int pool, int[] weights, int[] maxAdd)
    {
        var n = weights.Length;
        var result = new int[n];
        if (pool <= 0)
        {
            return result;
        }

        var remaining = pool;
        while (remaining > 0)
        {
            // 本轮可参与分配的项：未达上限且权重 > 0
            var poolWeights = new int[n];
            var wsum = 0;
            for (var i = 0; i < n; i++)
            {
                if (maxAdd[i] > result[i] && weights[i] > 0)
                {
                    poolWeights[i] = weights[i];
                    wsum += weights[i];
                }
            }

            if (wsum <= 0)
            {
                break;
            }

            var round = DistributeByWeight(remaining, poolWeights);
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (poolWeights[i] <= 0)
                {
                    continue;
                }

                var room = maxAdd[i] - result[i];
                var add = Math.Min(round[i], room);
                result[i] += add;
                remaining -= add;
                if (add > 0)
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return result;
    }

    private static void SendPlayerAddPoint(object roleMgr, string uid, int[] finalAdd)
    {
        try
        {
            var send = roleMgr.GetType().GetMethod(
                "SendAddOrCutBp",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (send == null)
            {
                return;
            }

            send.Invoke(roleMgr, new object[]
            {
                "加点", finalAdd[0], finalAdd[1], finalAdd[2], finalAdd[3], finalAdd[4], uid
            });
        }
        catch
        {
            // ignore
        }
    }

    private static void SendPetAddStr(object petMgr, string uid, int index, int addStr)
    {
        try
        {
            var send = petMgr.GetType().GetMethod(
                "SendResetPoint",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (send == null)
            {
                return;
            }

            send.Invoke(petMgr, new object[]
            {
                uid, "加点", index, 0, addStr, 0, 0, 0
            });
        }
        catch
        {
            // ignore
        }
    }

    private static IList GetPetDatasFromUid(string uid)
    {
        try
        {
            var holder = FindType("PlayerDataHolder");
            if (holder == null)
            {
                return null;
            }

            var m = holder.GetMethod(
                "GetPetDatasFromUid",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null)
            {
                return null;
            }

            return m.Invoke(null, new object[] { uid }) as IList;
        }
        catch
        {
            return null;
        }
    }

    private static object GetManagerInstance(string managerName)
    {
        try
        {
            var inner = FindType(managerName);
            if (inner == null)
            {
                return null;
            }

            var managerType = FindType("Manager`1");
            if (managerType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "Manager`1" && t.IsGenericTypeDefinition)
                            {
                                managerType = t;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    if (managerType != null)
                    {
                        break;
                    }
                }
            }

            if (managerType == null)
            {
                return null;
            }

            var closed = managerType.MakeGenericType(inner);
            var prop = closed.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var inst = prop?.GetValue(null, null);
            if (inst != null)
            {
                return inst;
            }

            var field = closed.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static Type FindType(string name)
    {
        try
        {
            var hotfixAsm = FindHotfixAssembly();
            if (hotfixAsm != null)
            {
                var t = hotfixAsm.GetType(name, false, false)
                        ?? hotfixAsm.GetType("Hotfix." + name, false, false);
                if (t != null)
                {
                    return t;
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            return Type.GetType(name, false)
                   ?? Type.GetType(name + ", hotfix", false)
                   ?? Type.GetType("Hotfix." + name + ", hotfix", false);
        }
        catch
        {
            return null;
        }
    }

    private static Assembly FindHotfixAssembly()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(asm.GetName().Name, "hotfix", StringComparison.OrdinalIgnoreCase))
                {
                    return asm;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static object GetMember(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var t = obj.GetType();
        var p = t.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null)
        {
            return p.GetValue(obj);
        }

        var f = t.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return f?.GetValue(obj);
    }

    private static object GetStaticMember(string typeName, string name)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        var p = t.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        if (p != null)
        {
            return p.GetValue(null, null);
        }

        var f = t.GetField(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        return f?.GetValue(null);
    }

    private static object GetStaticMethodResult(string typeName, string methodName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            return null;
        }

        var m = t.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (m == null)
        {
            return null;
        }

        try
        {
            return m.Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }

    private static object CallMethod(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            var m = obj.GetType().GetMethod(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return m?.Invoke(obj, null);
        }
        catch
        {
            return null;
        }
    }

    private static void Tip(string msg)
    {
        try
        {
            if (string.IsNullOrEmpty(msg))
            {
                return;
            }

            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            MethodInfo tip = null;
            foreach (var m in notify.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Tip")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                {
                    tip = m;
                    if (ps.Length == 2)
                    {
                        break;
                    }
                }
            }

            if (tip == null)
            {
                return;
            }

            var ps2 = tip.GetParameters();
            if (ps2.Length >= 2)
            {
                tip.Invoke(notify, new object[] { msg, false });
            }
            else
            {
                tip.Invoke(notify, new object[] { msg });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string RootMessage(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null)
        {
            e = e.InnerException;
        }

        return e.Message;
    }
}
