using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class MethodTokenResolver
{
    public static uint ResolveCallToken(ModuleDefinition module, string declaringTypeName, string methodName)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Call || insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (!Matches(called, declaringTypeName, methodName))
                    {
                        continue;
                    }

                    var token = called.MetadataToken.ToUInt32();
                    if (token != 0)
                    {
                        return token;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"未在 hotfix 中找到 call {declaringTypeName}.{methodName} 的元数据 token");
    }

    public static uint ResolveCallTokenLoose(ModuleDefinition module, string declaringTypeFragment, string methodName)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Call || insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (!string.Equals(called.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var fullName = called.DeclaringType?.FullName ?? "";
                    if (!fullName.Contains(declaringTypeFragment, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var token = called.MetadataToken.ToUInt32();
                    if (token != 0 && (token & 0xFFFFFF) != 0)
                    {
                        return token;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"未在 hotfix 中找到 call *{declaringTypeFragment}*.{methodName} 的元数据 token");
    }

    public static uint ResolveCallvirtToken(ModuleDefinition module, string declaringTypeName, string methodName)
    {
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var insn in method.Body.Instructions)
                {
                    if (insn.OpCode != OpCodes.Callvirt || insn.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (!Matches(called, declaringTypeName, methodName))
                    {
                        continue;
                    }

                    var token = called.MetadataToken.ToUInt32();
                    if (token != 0)
                    {
                        return token;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"未在 hotfix 中找到 callvirt {declaringTypeName}.{methodName} 的元数据 token");
    }

    private static bool Matches(MethodReference called, string declaringTypeName, string methodName)
    {
        if (!string.Equals(called.Name, methodName, StringComparison.Ordinal))
        {
            return false;
        }

        var typeName = called.DeclaringType?.Name ?? "";
        if (string.Equals(typeName, declaringTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        return called.DeclaringType?.FullName?.EndsWith("." + declaringTypeName, StringComparison.Ordinal) == true;
    }
}
