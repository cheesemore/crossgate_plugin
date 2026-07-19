using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 一动使用魔法/道具后仍允许二动开技能栏：PlayerActionMagics[uid] 保持 false。
/// </summary>
internal static class BattlePlayerActionMagicsIlBuilder
{
    public static IEnumerable<MethodDefinition> FindMethodsNeedingPatch(ModuleDefinition module)
    {
        var battleProcesser = module.Types.First(t => t.Name == "BattleProcesser");
        return battleProcesser.Methods
            .Where(m => m.HasBody && NeedsPatch(m))
            .Concat(battleProcesser.NestedTypes.SelectMany(t => t.Methods.Where(m => m.HasBody && NeedsPatch(m))));
    }

    public static void ApplyToMethod(MethodDefinition method)
    {
        if (PatchMethod(method) == 0 && !NeedsPatch(method))
        {
            throw new InvalidOperationException($"方法 {method.FullName} 未找到 PlayerActionMagics = true 赋值点");
        }
    }

    internal static bool IsHookInstalled(ModuleDefinition module)
    {
        var battleProcesser = module.Types.First(t => t.Name == "BattleProcesser");
        var methods = battleProcesser.Methods
            .Where(m => m.HasBody)
            .Concat(battleProcesser.NestedTypes.SelectMany(t => t.Methods.Where(m => m.HasBody)));

        var foundTrue = false;
        foreach (var method in methods)
        {
            foreach (var site in FindAssignmentSites(method.Body.Instructions))
            {
                foundTrue = true;
                if (site.OpCode != OpCodes.Ldc_I4_0)
                {
                    return false;
                }
            }
        }

        return foundTrue;
    }

    private static bool NeedsPatch(MethodDefinition method)
    {
        return FindAssignmentSites(method.Body.Instructions).Any(i => i.OpCode == OpCodes.Ldc_I4_1);
    }

    private static int PatchMethod(MethodDefinition method)
    {
        var insns = method.Body.Instructions;
        var count = 0;
        foreach (var site in FindAssignmentSites(insns).ToList())
        {
            if (site.OpCode == OpCodes.Ldc_I4_0)
            {
                continue;
            }

            site.OpCode = OpCodes.Ldc_I4_0;
            site.Operand = null;
            count++;
        }

        return count;
    }

    internal static IEnumerable<Instruction> FindAssignmentSites(IList<Instruction> insns)
    {
        for (var i = 0; i < insns.Count - 3; i++)
        {
            if (insns[i].OpCode != OpCodes.Ldfld
                || insns[i].Operand is not FieldReference field
                || field.Name != "PlayerActionMagics")
            {
                continue;
            }

            if (insns[i + 1].OpCode != OpCodes.Ldsfld
                || insns[i + 1].Operand is not FieldReference accountField
                || accountField.Name != "CurrentAccount")
            {
                continue;
            }

            if (insns[i + 2].OpCode != OpCodes.Ldc_I4_1 && insns[i + 2].OpCode != OpCodes.Ldc_I4_0)
            {
                continue;
            }

            if (insns[i + 3].OpCode != OpCodes.Callvirt
                || insns[i + 3].Operand is not MethodReference setter
                || setter.Name != "set_Item")
            {
                continue;
            }

            yield return insns[i + 2];
        }
    }
}
