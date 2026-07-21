using System.Text.Json;
using Mono.Cecil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 测算 hotfix .text VA 间隙余量，并评估各补丁「能不能打」。
/// 追加 IL 不得超过下一节 VirtualAddress；禁止后移节区 VA（否则启动未响应）。
/// </summary>
internal static class SlackReport
{
    public static int Run(string[] args)
    {
        string? hotfix = null;
        var asJson = false;
        var check = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotfix" when i + 1 < args.Length:
                    hotfix = Path.GetFullPath(args[++i]);
                    break;
                case "--json":
                    asJson = true;
                    break;
                case "--check" when i + 1 < args.Length:
                    foreach (var id in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        check.Add(id);
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(hotfix) || !File.Exists(hotfix))
        {
            Console.WriteLine(
                "用法: HotfixPatcher slack-report --hotfix <hotfix.dll.bytes> [--json] [--check id1,id2,...]\n" +
                "常见 id: vip, sprint, longpress, customer_gm, skill_effect, bridge, nine, nine_queue, nine_magics, nine_external");
            return 1;
        }

        var pe = File.ReadAllBytes(hotfix);
        HotfixSize.Require(pe);

        var text = PeLayout.GetSection(pe, ".text");
        var vaGap = PeLayout.GetTextVaGapBytes(pe);
        var rawSlack = (int)(text.SizeOfRawData - text.VirtualSize);
        var usable = Math.Min(vaGap, rawSlack);

        var profiles = BuildProfiles(hotfix, pe);
        var selected = check.Count == 0
            ? profiles
            : profiles.Where(p => check.Contains(p.Id)).ToList();

        if (check.Count > 0)
        {
            var unknown = check
                .Where(id => profiles.All(p => !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine("[FAIL] 未知补丁 id: " + string.Join(", ", unknown));
                return 1;
            }
        }

        // 「nine」与 nine_queue/nine_magics 互斥计费：选 nine 时不加两项子项
        selected = DeduplicateNine(selected);

        var budget = usable;
        var allOk = true;
        var evaluated = new List<(PatchSlackProfile Profile, bool Can, int BudgetAfter)>();
        foreach (var p in selected)
        {
            var can = p.AlreadyApplied || p.GrowthBytes <= 0 || p.GrowthBytes <= budget;
            if (!can && p.Mode == "external_dll")
            {
                can = false;
            }

            if (!p.AlreadyApplied && can && p.GrowthBytes > 0)
            {
                budget -= p.GrowthBytes;
            }

            if (!can)
            {
                allOk = false;
            }

            evaluated.Add((p, can, budget));
        }

        if (asJson)
        {
            var payload = new
            {
                hotfix,
                file_size = pe.Length,
                text_virtual_size = text.VirtualSize,
                text_raw_size = text.SizeOfRawData,
                va_gap_bytes = vaGap,
                raw_slack_bytes = rawSlack,
                usable_append_bytes = usable,
                remaining_after_check = budget,
                all_can_apply = allOk,
                patches = evaluated.Select(e => new
                {
                    id = e.Profile.Id,
                    name = e.Profile.Name,
                    growth_bytes = e.Profile.GrowthBytes,
                    mode = e.Profile.Mode,
                    already = e.Profile.AlreadyApplied,
                    can_apply = e.Can,
                    note = e.Profile.Note,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"[SLACK] 文件 {pe.Length:N0} 字节");
            Console.WriteLine(
                $"[SLACK] .text VS=0x{text.VirtualSize:X} RS=0x{text.SizeOfRawData:X}  raw_slack={rawSlack}  va_gap={vaGap}  usable={usable}");
            Console.WriteLine("[SLACK] 规则: 可执行追加 ≤ min(raw_slack, va_gap)；禁止后移 .rsrc/.reloc VA；禁止后迁邻居方法");
            foreach (var (p, can, _) in evaluated)
            {
                var mark = p.AlreadyApplied ? "已打" : can ? "可打" : (p.Mode == "external_dll" ? "需DLL版" : "不够");
                Console.WriteLine(
                    $"[SLACK] [{mark}] {p.Id,-14} +{p.GrowthBytes,4}B  {p.Mode,-12}  {p.Name}" +
                    (string.IsNullOrEmpty(p.Note) ? "" : $"  — {p.Note}"));
            }

            Console.WriteLine($"[SLACK] 测算后剩余可用追加: {budget} 字节");
            Console.WriteLine(allOk ? "[OK] 所选补丁余量足够" : "[FAIL] 有补丁余量不足或需 DLL 版");
        }

        return allOk ? 0 : 2;
    }

    private static List<PatchSlackProfile> DeduplicateNine(List<PatchSlackProfile> selected)
    {
        var hasNine = selected.Any(p => p.Id.Equals("nine", StringComparison.OrdinalIgnoreCase));
        if (!hasNine)
        {
            return selected;
        }

        return selected
            .Where(p => !p.Id.Equals("nine_queue", StringComparison.OrdinalIgnoreCase)
                        && !p.Id.Equals("nine_magics", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<PatchSlackProfile> BuildProfiles(string hotfixPath, byte[] pe)
    {
        var list = new List<PatchSlackProfile>();
        using var asm = ReadAsm(hotfixPath);

        list.Add(new PatchSlackProfile("vip", "战斗倍速", 0, "inplace", false, "改 ldc.r4，不占 VA 间隙"));
        list.Add(new PatchSlackProfile("sprint", "Sprint 跑速", 0, "inplace", false, "改 float，不占 VA 间隙"));
        list.Add(new PatchSlackProfile("longpress", "战斗长按", 0, "inplace", false, "beq.s→br.s，不占 VA 间隙"));
        list.Add(new PatchSlackProfile("transition", "加速过场", 0, "inplace", false, "CrossBlocks 0.8→0.4/0.2/0.1，不占 VA 间隙"));
        list.Add(new PatchSlackProfile("customer_gm", "客服入口", 0, "inplace", false, "Compact 回调，通常变小"));

        var skill = EstimateSkillEffectGrowth(asm);
        list.Add(new PatchSlackProfile(
            "skill_effect", "技能特效加速", skill.Bytes, skill.Mode, skill.Already, skill.Note));

        list.Add(new PatchSlackProfile(
            "bridge", "助手桥接", 200, "append", false, "建议上限约 200B（与九动互斥）"));

        list.Add(new PatchSlackProfile(
            "nine_external",
            "神奇九动·DLL版",
            180,
            "append",
            false,
            "Pause 加载器+Magics；与 IL原版/桥接互斥"));

        var nine = EstimateNineGrowth(asm, pe);
        list.Add(new PatchSlackProfile(
            "nine_queue", "神奇九动·队列", nine.QueueGrowth, nine.QueueMode, nine.QueueAlready, nine.QueueNote));
        list.Add(new PatchSlackProfile(
            "nine_magics", "神奇九动·Magics", 0, "inplace", nine.MagicsAlready, "ldc.i4.1→0，不占间隙"));
        list.Add(new PatchSlackProfile(
            "nine",
            "神奇九动(队列+Magics)",
            nine.QueueAlready ? 0 : nine.QueueGrowth,
            nine.QueueMode,
            nine.QueueAlready && nine.MagicsAlready,
            nine.CombinedNote));

        return list;
    }

    private static (int Bytes, string Mode, bool Already, string Note) EstimateSkillEffectGrowth(
        AssemblyDefinition asm)
    {
        try
        {
            var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == "EffectEntity");
            var play = type?.Methods.FirstOrDefault(m => m.Name == "Play" && m.HasBody);
            if (play == null)
            {
                return (49, "append", false, "未找到 EffectEntity.Play，按典型 +49 估算");
            }

            if (SkillEffectSpeedIlPatcher.IsHookInstalled(play))
            {
                return (0, "append", true, "已打过");
            }

            return (49, "append", false, "追加 PlaySpeed 乘法 IL（约 49B）");
        }
        catch (Exception ex)
        {
            return (49, "append", false, "估算失败按 49B: " + ex.Message);
        }
    }

    private static NineEstimate EstimateNineGrowth(AssemblyDefinition asm, byte[] pe)
    {
        var vaGap = PeLayout.GetTextVaGapBytes(pe);
        try
        {
            var bp = asm.MainModule.Types.First(t => t.Name == "BattleProcesser");
            var onPlayer = bp.Methods.First(m => m.Name == "OnCommandPlayerCallback" && m.HasBody);
            var queueAlready = BattleNineActionIlBuilder.IsHookInstalled(onPlayer);
            var magicsAlready = BattlePlayerActionMagicsIlBuilder.IsHookInstalled(asm.MainModule);

            if (queueAlready)
            {
                return new NineEstimate(0, "inplace", true, magicsAlready, "队列已打", "已打");
            }

            var snapshot = MethodBodyBlob.Read(pe, onPlayer.RVA);
            BattleNineActionIlBuilder.ApplyOnCommandPlayerPatches(onPlayer, asm.MainModule);
            IlSerializer.RecalculateOffsets(onPlayer.Body);
            var newBody = IlSerializer.Serialize(onPlayer.Body, snapshot);

            if (newBody.Length <= snapshot.Length)
            {
                return new NineEstimate(0, "inplace", false, magicsAlready,
                    $"新体 {newBody.Length}≤原体 {snapshot.Length}，可原地",
                    "队列可原地；Magics 原地");
            }

            if (newBody.Length <= vaGap)
            {
                return new NineEstimate(newBody.Length, "append", false, magicsAlready,
                    $"整法追加 {newBody.Length}B ≤ VA间隙 {vaGap}",
                    "队列可追加进间隙");
            }

            var growth = newBody.Length - snapshot.Length;
            var note =
                $"整法需 {newBody.Length}B（原体 {snapshot.Length}，+{growth}），VA间隙仅 {vaGap}；" +
                "后迁邻居会未响应 → 需 DLL 版";
            return new NineEstimate(newBody.Length, "external_dll", false, magicsAlready, note,
                "队列压不进间隙，需 DLL 版；Magics 仍可 --magics-only");
        }
        catch (Exception ex)
        {
            return new NineEstimate(
                581,
                "external_dll",
                false,
                false,
                "测算失败: " + ex.Message,
                "测算失败，默认需 DLL 版");
        }
    }

    private static AssemblyDefinition ReadAsm(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        return AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });
    }

    private sealed record PatchSlackProfile(
        string Id,
        string Name,
        int GrowthBytes,
        string Mode,
        bool AlreadyApplied,
        string Note);

    private sealed record NineEstimate(
        int QueueGrowth,
        string QueueMode,
        bool QueueAlready,
        bool MagicsAlready,
        string QueueNote,
        string CombinedNote);
}
