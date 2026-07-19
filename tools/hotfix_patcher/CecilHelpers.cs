using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal static class CecilHelpers
{
    public static IEnumerable<TypeDefinition> NestedTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        {
            foreach (var child in NestedTypes(nested))
            {
                yield return child;
            }
        }
    }
}
