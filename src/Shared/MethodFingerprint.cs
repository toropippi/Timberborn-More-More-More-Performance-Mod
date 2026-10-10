using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace T3MP.Loading;

// SHA256 of a method body with every metadata token resolved to the member,
// type or string it names. A game rebuild renumbers tokens but leaves this
// fingerprint unchanged; an edited body, signature, local or handler changes it.
internal static class MethodFingerprint
{
    private static Dictionary<short, OpCode>? _opcodes;

    // Lambdas, local functions and iterator bodies the method creates are part
    // of its logic: compiler-generated methods it references are included.
    internal static string Of(MethodBase method) => Of(method, new HashSet<MethodBase>());

    private static string Of(MethodBase method, HashSet<MethodBase> visited)
    {
        visited.Add(method);
        var nested = new List<MethodBase>();
        var body = method.GetMethodBody();
        if (body == null) return "no-body";
        var il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        var typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var text = new StringBuilder();
        text.Append(Member(method)).Append(" init=").Append(body.InitLocals).Append('\n');
        foreach (var local in body.LocalVariables) text.Append("local ").Append(Type(local.LocalType)).Append(local.IsPinned ? " pinned" : "").Append('\n');
        foreach (var clause in body.ExceptionHandlingClauses)
        {
            text.Append("eh ").Append(clause.Flags).Append(' ').Append(clause.TryOffset).Append('+').Append(clause.TryLength).Append(' ')
                .Append(clause.HandlerOffset).Append('+').Append(clause.HandlerLength).Append(' ');
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(Type(clause.CatchType));
            else if (clause.Flags == ExceptionHandlingClauseOptions.Filter) text.Append(clause.FilterOffset);
            text.Append('\n');
        }
        var opcodes = _opcodes ??= typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);
        var at = 0;
        while (at < il.Length)
        {
            var offset = at;
            short value = il[at++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[at++]));
            var opcode = opcodes[value];
            text.Append(offset).Append(' ').Append(opcode.Name);
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: { var delta = (sbyte)il[at]; at += 1; text.Append(" ->").Append(at + delta); break; }
                case OperandType.InlineBrTarget: { var delta = BitConverter.ToInt32(il, at); at += 4; text.Append(" ->").Append(at + delta); break; }
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: text.Append(' ').Append(il[at]); at += 1; break;
                case OperandType.InlineVar: text.Append(' ').Append(BitConverter.ToUInt16(il, at)); at += 2; break;
                case OperandType.InlineI:
                case OperandType.ShortInlineR: text.Append(' ').Append(BitConverter.ToInt32(il, at)); at += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: text.Append(' ').Append(BitConverter.ToInt64(il, at)); at += 8; break;
                case OperandType.InlineString: text.Append(" \"").Append(method.Module.ResolveString(BitConverter.ToInt32(il, at))).Append('"'); at += 4; break;
                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, at); at += 4;
                    var end = at + count * 4;
                    for (var i = 0; i < count; i++) text.Append(" ->").Append(end + BitConverter.ToInt32(il, at + i * 4));
                    at = end;
                    break;
                }
                case OperandType.InlineSig: text.Append(" sig"); at += 4; break;
                default:
                {
                    var member = method.Module.ResolveMember(BitConverter.ToInt32(il, at), typeArguments, methodArguments)!;
                    text.Append(' ').Append(Member(member));
                    at += 4;
                    if (member is MethodBase target && Generated(target.DeclaringType, method.DeclaringType))
                    {
                        var follow = target is ConstructorInfo
                            ? target.DeclaringType!.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            : target;
                        if (follow != null && !visited.Contains(follow) && !nested.Contains(follow)) nested.Add(follow);
                    }
                    break;
                }
            }
            text.Append('\n');
        }
        foreach (var child in nested)
            if (!visited.Contains(child)) text.Append("nested ").Append(Of(child, visited)).Append('\n');
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    // A compiler-generated type (display class, <>c, iterator) nested in owner.
    private static bool Generated(Type? type, Type? owner)
    {
        if (type == null || owner == null || !type.Name.StartsWith("<", StringComparison.Ordinal)) return false;
        for (var outer = type.DeclaringType; outer != null; outer = outer.DeclaringType)
            if (outer == owner || outer.IsGenericType && owner.IsGenericType && outer.GetGenericTypeDefinition() == owner.GetGenericTypeDefinition()) return true;
        return false;
    }

    private static string Member(MemberInfo member) => member switch
    {
        Type type => Type(type),
        FieldInfo field => Type(field.DeclaringType) + "::" + field.Name + ":" + Type(field.FieldType),
        MethodBase method => Type(method.DeclaringType) + "::" + method.Name +
            (method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(Type)) + ">" : "") +
            "(" + string.Join(",", method.GetParameters().Select(p => Type(p.ParameterType))) + ")" +
            (method is MethodInfo info ? ":" + Type(info.ReturnType) : ""),
        _ => member.MemberType + ":" + member.Name,
    };

    // Namespace-qualified names without assembly versions, so an engine or
    // runtime update that keeps a type's identity keeps the fingerprint.
    private static string Type(Type? type)
    {
        if (type == null) return "?";
        if (type.IsGenericParameter) return "!" + type.Name;
        if (type.IsByRef) return Type(type.GetElementType()) + "&";
        if (type.IsPointer) return Type(type.GetElementType()) + "*";
        if (type.IsArray) return Type(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        var name = type.IsNested ? Type(type.DeclaringType) + "+" + type.Name : (type.Namespace == null ? "" : type.Namespace + ".") + type.Name;
        if (type.IsGenericType && !type.IsGenericTypeDefinition) name += "<" + string.Join(",", type.GetGenericArguments().Select(Type)) + ">";
        return name;
    }
}
