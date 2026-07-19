using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CrossgateMod.Patcher;

internal static class ModTypeInjector
{
    private const string ModNamespace = "CrossgateMod";

    public static void InjectTypes(AssemblyDefinition hotfix, string modDllPath)
    {
        RemoveCrossgateModTypes(hotfix);

        using var mod = AssemblyDefinition.ReadAssembly(modDllPath);
        var targetModule = hotfix.MainModule;

        foreach (var sourceType in mod.MainModule.Types)
        {
            if (sourceType.Name == "<Module>" || sourceType.Namespace != ModNamespace)
            {
                continue;
            }

            var cloned = CloneType(targetModule, sourceType);
            targetModule.Types.Add(cloned);
            Console.WriteLine("[ADD] " + sourceType.FullName);
        }
    }

    private static void RemoveCrossgateModTypes(AssemblyDefinition hotfix)
    {
        var remove = hotfix.MainModule.Types
            .Where(t => t.Namespace == ModNamespace)
            .ToList();

        foreach (var type in remove)
        {
            hotfix.MainModule.Types.Remove(type);
            Console.WriteLine("[DEL] " + type.FullName);
        }
    }

    /// <summary>供 BridgeTypeInjector 复用类型克隆逻辑。</summary>
    internal static TypeDefinition CloneTypePublic(ModuleDefinition target, TypeDefinition source)
        => CloneType(target, source);

    private static TypeDefinition CloneType(ModuleDefinition target, TypeDefinition source)
    {
        var type = new TypeDefinition(
            source.Namespace,
            source.Name,
            source.Attributes,
            target.ImportReference(source.BaseType));

        foreach (var iface in source.Interfaces)
        {
            type.Interfaces.Add(new InterfaceImplementation(target.ImportReference(iface.InterfaceType)));
        }

        foreach (var field in source.Fields)
        {
            type.Fields.Add(new FieldDefinition(
                field.Name,
                field.Attributes,
                target.ImportReference(field.FieldType)));
        }

        foreach (var method in source.Methods)
        {
            if (method.IsRuntime || method.IsRuntimeSpecialName)
            {
                continue;
            }

            var clonedMethod = new MethodDefinition(
                method.Name,
                method.Attributes,
                target.ImportReference(method.ReturnType));

            foreach (var genericParam in method.GenericParameters)
            {
                clonedMethod.GenericParameters.Add(new GenericParameter(genericParam.Name, clonedMethod));
            }

            foreach (var param in method.Parameters)
            {
                clonedMethod.Parameters.Add(new ParameterDefinition(
                    param.Name,
                    param.Attributes,
                    target.ImportReference(param.ParameterType)));
            }

            if (method.HasBody)
            {
                clonedMethod.Body = CloneBody(target, method.Body, clonedMethod);
            }

            type.Methods.Add(clonedMethod);
        }

        return type;
    }

    private static MethodBody CloneBody(ModuleDefinition target, MethodBody source, MethodDefinition owner)
    {
        var body = new MethodBody(owner);
        var il = body.GetILProcessor();
        var map = new Dictionary<Instruction, Instruction>();
        var variables = new List<VariableDefinition>();

        foreach (var variable in source.Variables)
        {
            variables.Add(new VariableDefinition(target.ImportReference(variable.VariableType)));
            body.Variables.Add(variables[^1]);
        }

        foreach (var instr in source.Instructions)
        {
            map[instr] = Instruction.Create(OpCodes.Nop);
        }

        foreach (var instr in source.Instructions)
        {
            var clone = CloneInstruction(instr, map, target, owner, variables);
            map[instr] = clone;
            il.Append(clone);
        }

        foreach (var handler in source.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = map[handler.TryStart],
                TryEnd = map[handler.TryEnd],
                HandlerStart = map[handler.HandlerStart],
                HandlerEnd = map[handler.HandlerEnd],
                CatchType = handler.CatchType == null ? null : target.ImportReference(handler.CatchType),
                FilterStart = handler.FilterStart == null ? null : map[handler.FilterStart],
            });
        }

        body.InitLocals = source.InitLocals;
        body.MaxStackSize = source.MaxStackSize;
        return body;
    }

    private static Instruction CloneInstruction(
        Instruction source,
        IReadOnlyDictionary<Instruction, Instruction> map,
        ModuleDefinition target,
        MethodDefinition owner,
        IReadOnlyList<VariableDefinition> variables)
    {
        switch (source.OpCode.OperandType)
        {
            case OperandType.InlineNone:
                return Instruction.Create(source.OpCode);
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget:
                return Instruction.Create(source.OpCode, map[(Instruction)source.Operand!]);
            case OperandType.InlineSwitch:
                return Instruction.Create(
                    source.OpCode,
                    ((Instruction[])source.Operand!).Select(x => map[x]).ToArray());
            case OperandType.ShortInlineI:
                return Instruction.Create(source.OpCode, (sbyte)source.Operand!);
            case OperandType.InlineI:
                return Instruction.Create(source.OpCode, (int)source.Operand!);
            case OperandType.InlineI8:
                return Instruction.Create(source.OpCode, (long)source.Operand!);
            case OperandType.ShortInlineR:
                return Instruction.Create(source.OpCode, (float)source.Operand!);
            case OperandType.InlineR:
                return Instruction.Create(source.OpCode, (double)source.Operand!);
            case OperandType.InlineString:
                return Instruction.Create(source.OpCode, (string)source.Operand!);
            case OperandType.InlineVar:
            case OperandType.ShortInlineVar:
                return Instruction.Create(source.OpCode, variables[((VariableDefinition)source.Operand!).Index]);
            case OperandType.InlineArg:
            case OperandType.ShortInlineArg:
                return Instruction.Create(source.OpCode, owner.Parameters[((ParameterDefinition)source.Operand!).Index]);
            case OperandType.InlineField:
                return Instruction.Create(source.OpCode, target.ImportReference((FieldReference)source.Operand!));
            case OperandType.InlineMethod:
                return Instruction.Create(source.OpCode, target.ImportReference((MethodReference)source.Operand!));
            case OperandType.InlineType:
                return Instruction.Create(source.OpCode, target.ImportReference((TypeReference)source.Operand!));
            case OperandType.InlineTok:
                return source.Operand switch
                {
                    TypeReference tr => Instruction.Create(source.OpCode, target.ImportReference(tr)),
                    FieldReference fr => Instruction.Create(source.OpCode, target.ImportReference(fr)),
                    MethodReference mr => Instruction.Create(source.OpCode, target.ImportReference(mr)),
                    _ => throw new NotSupportedException("InlineTok: " + source.Operand?.GetType().Name),
                };
            default:
                throw new NotSupportedException(
                    $"不支持的 IL 操作数类型: {source.OpCode} ({source.OpCode.OperandType})");
        }
    }

}
