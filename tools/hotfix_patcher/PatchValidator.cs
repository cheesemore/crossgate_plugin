using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class PatchValidator
{
    public static void Validate(AssemblyDefinition assembly)
    {
        var module = assembly.MainModule;
        var bootstrap = module.Types.FirstOrDefault(t => t.FullName == "CrossgateMod.ModBootstrap")
            ?? throw new InvalidOperationException("校验失败: 缺少 ModBootstrap");

        var init = bootstrap.Methods.FirstOrDefault(m => m.Name == "Init" && m.IsStatic && m.HasBody)
            ?? throw new InvalidOperationException("校验失败: 缺少 ModBootstrap.Init");

        var gameManager = module.Types.FirstOrDefault(t => t.Name == "GameManagerHotfix")
            ?? throw new InvalidOperationException("校验失败: 缺少 GameManagerHotfix");

        var start = gameManager.Methods.FirstOrDefault(m => m.Name == "Start" && m.HasBody)
            ?? throw new InvalidOperationException("校验失败: 缺少 GameManagerHotfix.Start");

        if (init.Body.Instructions.Count < 3)
        {
            throw new InvalidOperationException(
                $"校验失败: ModBootstrap.Init 仅 {init.Body.Instructions.Count} 条 IL，可能已损坏");
        }

        if (start.Body.Instructions.Count < 8)
        {
            throw new InvalidOperationException(
                $"校验失败: GameManagerHotfix.Start 仅 {start.Body.Instructions.Count} 条 IL，拒绝写入");
        }

        if (!MethodCalls(start, init))
        {
            throw new InvalidOperationException("校验失败: Start 未调用 ModBootstrap.Init");
        }

        Console.WriteLine($"[OK] 校验通过: Start={start.Body.Instructions.Count} 条 IL, Init={init.Body.Instructions.Count} 条 IL");
    }

    private static bool MethodCalls(MethodDefinition method, MethodDefinition target)
    {
        foreach (var insn in method.Body.Instructions)
        {
            if (insn.OpCode == OpCodes.Call && insn.Operand is MethodReference called
                && called.FullName == target.FullName)
            {
                return true;
            }
        }

        return false;
    }
}
