using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

/// <summary>兼容旧命令：秘阁 → 讨伐 Boss。</summary>
internal static class MigeBossPanelIlPatcher
{
    public static int Run(string[] args) => MigePanelIlPatcher.Run(PrependMode(args, "boss"));

    private static string[] PrependMode(string[] args, string mode)
    {
        if (args.Any(a => a == "--mode"))
        {
            return args;
        }

        return args.Concat(new[] { "--mode", mode }).ToArray();
    }
}
