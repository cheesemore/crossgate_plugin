using Mono.Cecil;

namespace CrossgateMod.Patcher;

/// <summary>
/// 将 Roslyn 编译的 SeqChapterHelperBridge.dll 类型克隆进 hotfix 程序集。
/// </summary>
internal static class BridgeTypeInjector
{
    public static void InjectBridgeTypes(AssemblyDefinition hotfix, string bridgeDllPath)
    {
        RemoveExistingBridge(hotfix);

        using var bridge = AssemblyDefinition.ReadAssembly(bridgeDllPath);
        var targetModule = hotfix.MainModule;

        foreach (var sourceType in bridge.MainModule.Types)
        {
            if (sourceType.Name == "<Module>")
            {
                continue;
            }

            var cloned = ModTypeInjector.CloneTypePublic(targetModule, sourceType);
            targetModule.Types.Add(cloned);
            Console.WriteLine("[ADD] " + sourceType.FullName);
        }
    }

    private static void RemoveExistingBridge(AssemblyDefinition hotfix)
    {
        var remove = hotfix.MainModule.Types
            .Where(t => t.Name == "SeqChapterHelperBridge")
            .ToList();

        foreach (var type in remove)
        {
            hotfix.MainModule.Types.Remove(type);
            Console.WriteLine("[DEL] " + type.FullName);
        }
    }
}
