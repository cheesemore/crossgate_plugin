// 临时探针：枚举战斗加速相关的四个目标点
// 1) BattleRole/MoveToDestination>d__ 状态机里 fast?9:6 的 ldc.r4 6f/9f
// 2) BaseAction/<KickOff>b__* 里 count>=3 的 ldc.i4.3
// 3) BowAttack.Attack 里 arrowSpeed=12f 的 ldc.r4 12
// 4) BombAttack.Attack 里 arrowSpeed=6f 的 ldc.r4 6
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ProbeCombatAccel
{
    public static int Run(string[] args)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var stubDir in Program.ResolveRefStubDirsPublic())
        {
            resolver.AddSearchDirectory(stubDir);
        }

        using var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
        });

        DumpMoveToDestinationStateMachine(asm);
        DumpKickOff(asm);
        DumpAttackConstants(asm, "BowAttack");
        DumpAttackConstants(asm, "BombAttack");

        return 0;
    }

    private static void DumpMoveToDestinationStateMachine(AssemblyDefinition asm)
    {
        Console.WriteLine("=== MoveToDestination 状态机 IL ===");
        var battleRole = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BattleRole");
        if (battleRole == null)
        {
            Console.WriteLine("  BattleRole not found");
            return;
        }

        var d199 = battleRole.NestedTypes.FirstOrDefault(t => t.Name == "<MoveToDestination>d__199")
            ?? battleRole.NestedTypes.FirstOrDefault(t => t.Name.Contains("MoveToDestination") && t.Name.Contains("d__"));
        if (d199 == null)
        {
            Console.WriteLine("  <MoveToDestination>d__199 not found");
            return;
        }

        foreach (var method in d199.Methods.Where(m => m.HasBody))
        {
            Console.WriteLine($"  TYPE {d199.Name} METHOD {method.Name}");
            foreach (var insn in method.Body.Instructions)
            {
                Console.WriteLine($"    IL_{insn.Offset:x4}: {insn.OpCode} {(insn.Operand?.ToString() ?? "")}");
            }
        }
    }

    private static void DumpKickOff(AssemblyDefinition asm)
    {
        Console.WriteLine("=== KickOff 撞墙次数（BaseAction 嵌套类型） ===");
        var baseAction = asm.MainModule.Types.FirstOrDefault(t => t.Name == "BaseAction");
        if (baseAction == null)
        {
            Console.WriteLine("  BaseAction not found");
            return;
        }

        foreach (var nested in CecilHelpers.NestedTypes(baseAction))
        {
            if (!nested.Name.Contains("DisplayClass28", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in nested.Methods.Where(m => m.HasBody))
            {
                Console.WriteLine($"  TYPE {nested.Name} METHOD {method.Name}");
                foreach (var insn in method.Body.Instructions)
                {
                    Console.WriteLine($"    IL_{insn.Offset:x4}: {insn.OpCode} {(insn.Operand?.ToString() ?? "")}");
                }
            }
        }
    }

    private static void DumpAttackConstants(AssemblyDefinition asm, string typeName)
    {
        Console.WriteLine($"=== {typeName} Attack 状态机 IL ===");
        var type = asm.MainModule.Types.FirstOrDefault(t => t.Name == typeName);
        if (type == null)
        {
            Console.WriteLine($"  {typeName} not found");
            return;
        }

        var attackState = type.NestedTypes.FirstOrDefault(t => t.Name.Contains("<Attack>") && t.Name.Contains("d__"));
        if (attackState == null)
        {
            Console.WriteLine($"  <Attack>d__* not found in {typeName}");
            return;
        }

        foreach (var method in attackState.Methods.Where(m => m.HasBody && m.Name == "MoveNext"))
        {
            Console.WriteLine($"  TYPE {attackState.Name} METHOD {method.Name}");
            foreach (var insn in method.Body.Instructions)
            {
                Console.WriteLine($"    IL_{insn.Offset:x4}: {insn.OpCode} {(insn.Operand?.ToString() ?? "")}");
            }
        }
    }
}
