using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Text;

namespace MonoMod.Utils;

#pragma warning disable CS8632

public static partial class Extensions {

    /// <summary>
    /// Get a reference ID that is similar to the full name, but consistent between System.Reflection and Mono.Cecil.
    /// </summary>
    /// <param name="method">The method to get the ID for.</param>
    /// <param name="name">The name to use instead of the reference's own name.</param>
    /// <param name="type">The ID to use instead of the reference's declaring type ID.</param>
    /// <param name="withType">Whether the type ID should be included or not. System.Reflection avoids it by default.</param>
    /// <param name="simple">Whether the ID should be "simple" (name only).</param>
    /// <param name="ignoreLastParam">Whether to skip writing the last param.</param>
    /// <param name="nonStaticBaseType">A type to add infront of all parameters.</param>
    /// <returns>The ID.</returns>
    public static string GetIDWithIgnore(this MethodReference method, string? name = null, string? type = null, bool withType = true, bool simple = false, bool ignoreLastParam = true, bool ignoreFirstParam = false, TypeReference nonStaticBaseType = null, TypeReference returnTypeOverride = null) {
        Helpers.ThrowIfArgumentNull(method);

        var builder = new StringBuilder();

        if (simple) {
            if (withType && (type != null || method.DeclaringType != null))
                builder.Append(type ?? method.DeclaringType.GetPatchFullName()).Append("::");
            builder.Append(name ?? method.Name);
            return builder.ToString();
        }

        builder
            .Append((returnTypeOverride ?? method.ReturnType).GetPatchFullName())
            .Append(' ');

        if (withType && (type != null || method.DeclaringType != null))
            builder.Append(type ?? method.DeclaringType.GetPatchFullName()).Append("::");

        builder
            .Append(name ?? method.Name);

        if (method is GenericInstanceMethod gim && gim.GenericArguments.Count != 0) {
            builder.Append('<');
            var arguments = gim.GenericArguments;
            for (var i = 0; i < arguments.Count; i++) {
                if (i > 0)
                    builder.Append(',');
                builder.Append(arguments[i].GetPatchFullName());
            }
            builder.Append('>');

        } else if (method.GenericParameters.Count != 0) {
            builder.Append('<');
            var arguments = method.GenericParameters;
            for (var i = 0; i < arguments.Count; i++) {
                if (i > 0)
                    builder.Append(',');
                builder.Append(arguments[i].Name);
            }
            builder.Append('>');
        }

        builder.Append('(');

        if (method.HasParameters || nonStaticBaseType != null) {
            var parameters = method.Parameters.ToArray();
            int modifFirst = (ignoreFirstParam && nonStaticBaseType == null) ? 1 : 0;
            int modifLast = ignoreLastParam ? 1 : 0;
            var i = modifFirst;

            if (nonStaticBaseType != null && !ignoreFirstParam) {
                builder.Append(nonStaticBaseType.GetPatchFullName());
                i++;
            }

            for (int j = modifFirst; j < parameters.Length - modifLast; j++, i++) {
                var parameter = parameters[j];
                if (i > modifFirst)
                    builder.Append(',');

                if (parameter.ParameterType.IsSentinel)
                    builder.Append("...,");

                builder.Append(parameter.ParameterType.GetPatchFullName());
            }
        }

        builder.Append(')');

        return builder.ToString();
    }

    /// <summary>
    /// Find a method for a given ID.
    /// </summary>
    /// <param name="type">The type to search in.</param>
    /// <param name="id">The method ID.</param>
    /// <param name="simple">Whether to perform a simple search pass as well or not.</param>
    /// <param name="appendNonStaticBase">Appends the DeclaringType for non static methods for the search targets as the first param.</param>
    /// <returns>The first matching method or null.</returns>
    public static MethodDefinition FindMethodAddNonStaticBase(this TypeDefinition type, string id, bool simple = true, bool appendNonStaticBase = true) {
        if (!appendNonStaticBase) return type.FindMethod(id, simple);

        Helpers.ThrowIfArgumentNull(type);
        Helpers.ThrowIfArgumentNull(id);
        if (simple && !id.Contains(' ', StringComparison.Ordinal)) {
            // First simple pass: With type name (just "Namespace.Type::MethodName")
            foreach (var method in type.Methods)
                if (method.GetIDWithIgnore(simple: true, ignoreLastParam: false, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType) == id)
                    return method;
            // Second simple pass: Without type name (basically name only)
            foreach (var method in type.Methods)
                if (method.GetIDWithIgnore(withType: false, simple: true, ignoreLastParam: false, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType) == id)
                    return method;
        }

        // First pass: With type name (f.e. global searches)
        foreach (var method in type.Methods)
            if (method.GetIDWithIgnore(ignoreLastParam: false, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType) == id)
                return method;
        // Second pass: Without type name (f.e. LinkTo)
        foreach (var method in type.Methods)
            if (method.GetIDWithIgnore(withType: false, ignoreLastParam: false, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType) == id)
                return method;

        return null;
    }

    /// <summary>
    /// Search forward and moves the cursor to the next sequence of instructions matching the corresponding predicates.
    /// </summary>
    /// <returns><see langword="true"/> if a matching instruction was found; <see langword="false"/> if one was not.</returns>
    public static bool TryGotoNext(this ILCursor cursor, MoveType moveType = MoveType.Before, params Func<Instruction, int, bool>[] predicates) {
        Helpers.ThrowIfArgumentNull(predicates);

        var instrs = cursor.Instrs;
        var i = cursor.Index;
        if (cursor.SearchTarget == SearchTarget.Next)
            i++;

        for (; i + predicates.Length <= instrs.Count; i++) {
            for (var j = 0; j < predicates.Length; j++) {
                if (!(predicates[j]?.Invoke(instrs[i + j], i + j) ?? true)) {
                    goto Next;
                }
            }

            cursor.Goto(moveType == MoveType.After ? i + predicates.Length - 1 : i, moveType, true);
            return true;

        Next:
            continue;
        }
        return false;
    }
}