using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace OpusMutatum.Merging;
public static class MethodGeneration {
    static readonly string GeneratedClassName = "<>mut";

    #region generation
    public static TypeDefinition GetCompilerGeneratedType(this TypeDefinition targetType, string generatedTypeName = null, bool addCctor = true) {
        generatedTypeName ??= GeneratedClassName;
        var generated = targetType.NestedTypes.FirstOrDefault(typeDef => typeDef.Name == generatedTypeName, defaultValue: null);
        if (generated != null) return generated;

        generated = new TypeDefinition(targetType.Namespace, generatedTypeName, TypeAttributes.NestedPublic | TypeAttributes.Sealed) {
            DeclaringType = targetType,
            BaseType = GetObjectRef(),
            IsClass = true,
            IsSpecialName = true
        };

        targetType.NestedTypes.Add(generated);
        if (addCctor) generated.GetCctor(); // Just to generate the .cctor as the first method;
        return generated;
    }
    public static FieldReference GetCompilerGeneratedFuncField(this TypeDefinition targetType, MethodDefinition method) {
        if (!method.IsStatic) throw new Exception("GetCompilerGeneratedFuncField must use a static method.");
        var compilerType = targetType.GetCompilerGeneratedType();
        var cctor = GetCctor(compilerType);
        var funcCtor = method.GetFuncCtor();
        var funcField = new FieldDefinition(method.Name + "_f", FieldAttributes.Static | FieldAttributes.Public | FieldAttributes.InitOnly, funcCtor.DeclaringType);

        compilerType.Fields.Add(funcField);

        ILCursor cursor = new(new ILContext(cctor));
        cursor.EmitNop();
        cursor.EmitLdnull();
        cursor.EmitLdftn(method);
        cursor.EmitNewobj(funcCtor);
        cursor.EmitStsfld(funcField);

        return funcField;
    }
    private static MethodDefinition GetCctor(this TypeDefinition targetType) {
        var generated = targetType.Methods.FirstOrDefault(method => method.Name == ".cctor", defaultValue: null);
        if (generated != null) return generated;

        generated = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Private, GetVoidRef());
        generated.Body = new MethodBody(generated);
        generated.DeclaringType = targetType;

        ILCursor cursor = new(new ILContext(generated));
        cursor.EmitRet();
        targetType.Methods.Add(generated);
        return generated;
    }

    public static MethodReference GetFuncCtor(this MethodReference toFunct) {
        return GetFuncCtor(toFunct.ReturnType, [.. toFunct.Parameters.Select(par => par.ParameterType)]);
    }
    public static MethodReference GetFuncCtor(TypeReference returnType, TypeReference[] paramTypes) {
        var coreLibModule = returnType.Module.ImportReference(typeof(Action)).Resolve().Module;
        bool isAction = returnType.FullName == "System.Void";
        
        var functType = isAction ? coreLibModule.GetType("System.Action" + (paramTypes.Length > 0 ? "`" + paramTypes.Length : ""))
                                 : coreLibModule.GetType("System.Func`" + (1 + paramTypes.Length));
        var ctorMethod = (MethodReference)functType.FindMethodByName(methodName: ".ctor").Clone();

        var genericInstanceType = new GenericInstanceType(functType);
        genericInstanceType.GenericArguments.AddRange(paramTypes);
        if (!isAction) genericInstanceType.GenericArguments.Add(returnType);

        ctorMethod.DeclaringType = genericInstanceType;
        return ctorMethod;
    }

    public static TypeReference GetVoidRef() {
        Globals.TryLoadIntermediaryLightning(out AssemblyDefinition assembly, logConsoleNormal: false); // Any assembly on .Net 10 should do
        return assembly.MainModule.TypeSystem.Void;
    }
    public static TypeReference GetIntRef() {
        Globals.TryLoadIntermediaryLightning(out AssemblyDefinition assembly, logConsoleNormal: false);
        return assembly.MainModule.TypeSystem.Int32;
    }
    public static TypeReference GetUIntRef() {
        Globals.TryLoadIntermediaryLightning(out AssemblyDefinition assembly, logConsoleNormal: false);
        return assembly.MainModule.TypeSystem.UInt32;
    }
    public static TypeReference GetBoolRef() {
        Globals.TryLoadIntermediaryLightning(out AssemblyDefinition assembly, logConsoleNormal: false);
        return assembly.MainModule.TypeSystem.Boolean;
    }
    public static TypeReference GetObjectRef() {
        Globals.TryLoadIntermediaryLightning(out AssemblyDefinition assembly, logConsoleNormal: false);
        return assembly.MainModule.TypeSystem.Object;
    }
    #endregion

    #region utils

    public static ILCursor GotoFirstArgumentInsert(this ILCursor cursor, out int origIndex) { // call after the target method instruction.
        if (!cursor.Previous.IsMethodCall()) throw new Exception("Instruction must call a method to use this Goto method.");
        origIndex = cursor.Index;
        int deltaStack = -1;
        try { 
            cursor.MoveToSameStackLevelInstruction(ref deltaStack, forwards: false);
        } catch (Exception e) {
            if (e.Message.StartsWith("Reached end of method: ") && cursor.Index == 0 && deltaStack == 0) return cursor;
            else throw;
        }
        return cursor;
    }

    public static ILCursor EmitLdargOptimised(this ILCursor cursor, int i) {
        return i switch {
            0 => cursor.Emit(OpCodes.Ldarg_0),
            1 => cursor.Emit(OpCodes.Ldarg_1),
            2 => cursor.Emit(OpCodes.Ldarg_2),
            3 => cursor.Emit(OpCodes.Ldarg_3),
            _ => cursor.Emit(OpCodes.Ldarg_S, (byte)i),
        };
    }

    public static bool MatchCallWeak(this Instruction instr, MethodReference value) {
        //Console.WriteLine(instr);
        if (instr.MatchCallOrCallvirt(out MethodReference value2)) {
            return value.GetID() == value2.GetID() &&
                value.HasThis == value2.HasThis;
        }
        return false;
    }
    public static bool MatchLdfldWeak(this Instruction instr, FieldReference value) {
        //Console.WriteLine(instr);
        if (instr.MatchLdfld(out FieldReference value2) || instr.MatchLdsfld(out value2)) {
            return value.FullName == value2.FullName;
        }
        return false;
    }
    public static bool MatchStfldWeak(this Instruction instr, FieldReference value) {
        //Console.WriteLine(instr);
        if (instr.MatchStfld(out FieldReference value2) || instr.MatchStsfld(out value2)) {
            return value.FullName == value2.FullName;
        }
        return false;
    }
    public static bool MatchNewobjWeak(this Instruction instr, MethodReference value) {
        //Console.WriteLine(instr);
        if (instr.MatchNewobj(out MethodReference value2)) {
            return value.GetID() == value2.GetID() &&
                value.HasThis == value2.HasThis;
        }
        return false;
    }
    public static bool MatchNumericLiteral(this Instruction instr, NumericLiteralType type, int @int, long @long, float @float, double @double) {
        return GetLdcType(type) switch {
            NumericLdcType.i4 => instr.MatchLdcI4(@int),
            NumericLdcType.i8 => instr.MatchLdcI8(@long),
            NumericLdcType.r4 => instr.MatchLdcR4(@float),
            NumericLdcType.r8 => instr.MatchLdcR8(@double),
            _ => throw new InvalidOperationException("Invalid NumericLdcType: " + type)
        };
    }

    public static bool IsMethodCall(this Instruction instr) {
        return instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt || instr.OpCode == OpCodes.Newobj || instr.OpCode == OpCodes.Calli;
    }
    public static bool IsBranch(this Instruction instr) => branchCodes0.Contains(instr.OpCode.Code) || branchCodes1.Contains(instr.OpCode.Code) || branchCodes2.Contains(instr.OpCode.Code);
    private static Code[] branchCodes0 = [
        Code.Br,
        Code.Br_S,
    ];
    private static Code[] branchCodes1 = [
        Code.Brfalse,
        Code.Brtrue,
        Code.Brfalse_S,
        Code.Brtrue_S,
    ];
    private static Code[] branchCodes2 = [
        Code.Beq,
        Code.Bge,
        Code.Bgt,
        Code.Ble,
        Code.Blt,
        Code.Bne_Un,
        Code.Bge_Un,
        Code.Bgt_Un,
        Code.Ble_Un,
        Code.Blt_Un,
        Code.Beq_S,
        Code.Bge_S,
        Code.Bgt_S,
        Code.Ble_S,
        Code.Blt_S,
        Code.Bne_Un_S,
        Code.Bge_Un_S,
        Code.Bgt_Un_S,
        Code.Ble_Un_S,
        Code.Blt_Un_S,
    ];

    public static NumericLiteralType ParseNumericLiteral(string data, ref int @int, ref long @long, ref float @float, ref double @double) {
        uint @uint = 0; ulong @ulong = 0; bool @bool = false;
        var type = ParseNumericLiteral(data, ref @int, ref @uint, ref @long, ref @ulong, ref @float, ref @double, ref @bool);
        @int += (int)@uint + (@bool ? 1 : 0);
        @long += (int)@ulong;
        return type;
    }
    public static NumericLiteralType ParseNumericLiteral(string data, ref int @int, ref uint @uint, ref long @long, ref ulong @ulong, ref float @float, ref double @double, ref bool @bool) {
        string orig = data;
        if (data == "") throw new Exception("Unable to parse numeric literal from empty string. ");
        if (data == "true") { @bool = true; return NumericLiteralType.Bool; }
        if (data == "false") { @bool = false; return NumericLiteralType.Bool; }

        // TODO add Scientific Notation parsing

        bool isNegative = false;
        if (data.StartsWith('-')) {
            isNegative = true;
            data = data[1..];
        }
        string validNumericChars = "0123456789";

        if (!data.Contains('.')) {
            if (data.EndsWith("UL")) {
                data = data[..^2];
                if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
                if (isNegative) throw new Exception($"Unsigned numeric literals can't be negative. ( in: '{orig}')");
                if (!ulong.TryParse(data, out @ulong)) throw new Exception($"Failed to parse ulong in: '{orig}'");
                return NumericLiteralType.ULong;
            }
            if (data.EndsWith('L') || data.EndsWith('l')) {
                data = data[..^1];
                if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
                if (isNegative) data = "-" + data;
                if (!long.TryParse(data, out @long)) throw new Exception($"Failed to parse Long in: '{orig}'");
                return NumericLiteralType.Long;
            }
            if (data.EndsWith('U')) {
                data = data[..^1];
                if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
                if (isNegative) throw new Exception($"Unsigned numeric literals can't be negative. ( in: '{orig}')");
                if (!uint.TryParse(data, out @uint)) throw new Exception($"Failed to parse uint in: '{orig}'");
                return NumericLiteralType.UInt;
            }
            if (!"fFdDmM".Contains(data[^1])) {
                if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
                if (isNegative) data = "-" + data;
                if (!int.TryParse(data, out @int)) throw new Exception($"Failed to parse Int in: '{orig}'");
                return NumericLiteralType.Int;
            }
        }

        string[] parts = data.Split('.');
        if (parts.Length > 2) throw new Exception("Numeric literal must not contain more than 1 '.' character.");
        validNumericChars += ".";

        if (data.EndsWith('F') || data.EndsWith('f')) {
            data = data[..^1];
            if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
            if (isNegative) data = "-" + data;
            if (!float.TryParse(data, out @float)) throw new Exception($"Failed to parse float in: '{orig}'");
            return NumericLiteralType.Float;
        }

        if (data.EndsWith('D') || data.EndsWith('d') || "0123456789".Contains(data[^1])) {
            if (data.EndsWith('D') || data.EndsWith('d')) data = data[..^1];
            if (!data.All(ch => validNumericChars.Contains(ch))) throw new Exception($"The numeric literal '{orig}' contains invalid characters.");
            if (isNegative) data = "-" + data;
            if (!double.TryParse(data, out @double)) throw new Exception($"Failed to parse double in: '{orig}'");
            return NumericLiteralType.Double;
        }

        if (data.EndsWith('M') || data.EndsWith('m')) {
            throw new Exception($"Parsing decimal types is not supported: '{orig}'");
        }

        throw new Exception($"Unable to parse numeric literal: '{orig}'");
    }

    private static NumericLdcType GetLdcType(NumericLiteralType numericLiteralType) {
        return numericLiteralType switch {
            NumericLiteralType.Int => NumericLdcType.i4,
            NumericLiteralType.UInt => NumericLdcType.i4,
            NumericLiteralType.Long => NumericLdcType.i8,
            NumericLiteralType.ULong => NumericLdcType.i8,
            NumericLiteralType.Float => NumericLdcType.r4,
            NumericLiteralType.Double => NumericLdcType.r8,
            NumericLiteralType.Bool => NumericLdcType.i4,
            NumericLiteralType.Enum => NumericLdcType.i4,
            _ => throw new InvalidOperationException("Invalid NumericLiteralType: " + numericLiteralType)
        };
    }

    public enum NumericLiteralType { Int, UInt, Long, ULong, Float, Double, Bool, Enum }
    private enum NumericLdcType { i4, i8, r4, r8 }

    #endregion

    #region analysis

    public static bool MatchNumericLiteralWithAnalysis(this Instruction instr, ILCursor cursor, int instrIndex, NumericLiteralType type, int @int, long @long, float @float, double @double, TypeReference @enum) {
        if (instr.MatchNumericLiteral(type, @int, @long, @float, @double)) {
            var ldcType = GetLdcType(type);
            if (ldcType == NumericLdcType.r4 || ldcType == NumericLdcType.r8) return true;

            var originalIndex = cursor.Index;
            cursor.Index = instrIndex;
            var typeRef = cursor.InferCurrentInstructionTypeWithStackAnalysis();
            cursor.Index = originalIndex;

            if (ldcType == NumericLdcType.i8) {
                if (typeRef.FullName != "System.UInt32" || typeRef.FullName != "System.Int32")
                    throw new Exception($"Found Invalid type '{typeRef.FullName}' for '{instr}'\nin method: '{cursor.Method.FullName}'.\nWhile searching for '{type}'.");
                if (type == NumericLiteralType.ULong) return typeRef.FullName == "System.UInt32";
                return typeRef.FullName == "System.Int32";
            }

            if (type == NumericLiteralType.Enum) return typeRef.FullName == @enum.FullName;
            if (type == NumericLiteralType.Int) return typeRef.FullName == "System.Int32";
            if (type == NumericLiteralType.UInt) return typeRef.FullName == "System.UInt32";
            if (type == NumericLiteralType.Bool) return typeRef.FullName == "System.Boolean";
            throw new Exception($"Unaccounted NumericLiteralType: '{type}'");
        }
        return false;
    }

    public static TypeReference InferCurrentInstructionTypeWithStackAnalysis(this ILCursor cursor) { // start before the instruction the question was asked for
        cursor.Index++;
        var pushBehaviour = cursor.Previous.OpCode.StackBehaviourPush;
        if (pushBehaviour != StackBehaviour.Push1 && pushBehaviour != StackBehaviour.Pushi && pushBehaviour != StackBehaviour.Pushi8 && pushBehaviour != StackBehaviour.Pushr4 && pushBehaviour != StackBehaviour.Pushr8)
            throw new Exception("Cannot infer TypeReference from non-single numeric value pushing Instructions.");
        
        Queue<Tuple<int, int, bool>> branchQueue = []; // cursorIndex, deltaStack, forwards
        branchQueue.Enqueue(new(cursor.Index, -1, true));
        TypeReference ambigousType = null;

        do {
            var searchLocation = branchQueue.Dequeue();
            cursor.Index = searchLocation.Item1;
            int deltaStack = searchLocation.Item2;
            bool forwards = searchLocation.Item3;

            cursor.MoveToSameStackLevelInstruction(ref deltaStack, forwards, branchQueue, true);

            bool isAmbigous = false;
            var toReturn = forwards ? cursor.ResolveCulsorInstructionTypeAsPop(ref deltaStack, ref isAmbigous)
                                    : cursor.ResolveCulsorInstructionTypeAsPush(ref deltaStack, ref isAmbigous);
            if (toReturn != null && !isAmbigous) return toReturn;
            if (toReturn != null) ambigousType = toReturn;

        } while (branchQueue.Count > 0);

        return ambigousType;
    }
    public static void MoveToSameStackLevelInstruction(this ILCursor cursor, ref int deltaStack, bool forwards = true, Queue<Tuple<int, int, bool>> branchQueue = null, bool doSpecialTypeFind = false) { // start before the instruction the question was asked for
        bool isVoidReturnMethod = cursor.Method.ReturnType.Name == "System.Void";
        if (forwards) {
            @continue:
            do {
                deltaStack += cursor.Prev.GetDeltaStackPush(isVoidReturnMethod);
                cursor.HandelBranch(ref deltaStack, true, branchQueue);
                deltaStack += cursor.Next.GetDeltaStackPop(isVoidReturnMethod);
                cursor.Index++;
                if (cursor.Index > cursor.Instrs.Count) throw new Exception("Reached end of method: " + cursor.Method.FullName);
            } while (deltaStack >= 0);
            if (doSpecialTypeFind) {
                switch (cursor.Prev.OpCode.Code) {
                    case Code.Not:
                    case Code.Neg:
                        goto @continue;
                    case Code.Dup:  // deltaStack is -1
                        branchQueue?.Enqueue(new(cursor.Index, -2, true));
                        goto @continue;
                    case Code.And:
                    case Code.Add:
                    case Code.Sub:
                    case Code.Mul:  // deltaStack is -1 or -2
                        branchQueue?.Enqueue(new(cursor.Index, deltaStack == -1 ? -2 : -1, false));
                        deltaStack = -1;
                        goto @continue;
                    case Code.Beq:
                    case Code.Beq_S:
                    case Code.Rem_Un:
                    case Code.Bge:
                    case Code.Bge_S:
                    case Code.Bgt:
                    case Code.Bgt_S:
                    case Code.Ble:
                    case Code.Ble_S:
                    case Code.Blt:
                    case Code.Blt_S:
                    case Code.Bge_Un:
                    case Code.Bge_Un_S:
                    case Code.Bgt_Un:
                    case Code.Bgt_Un_S:
                    case Code.Ble_Un:
                    case Code.Ble_Un_S:
                    case Code.Blt_Un:
                    case Code.Blt_Un_S:
                    case Code.Bne_Un:
                    case Code.Bne_Un_S:
                    case Code.Ceq:
                    case Code.Cgt:
                    case Code.Clt:
                    case Code.Cgt_Un:
                    case Code.Clt_Un:   // deltaStack is -1 or -2
                        branchQueue?.Enqueue(new(cursor.Index, deltaStack == -1 ? -2 : -1, false));
                        return;
                }
            }
        } else {
            @continue:
            do {
                cursor.Index--;
                if (cursor.Index <= 0) throw new Exception("Reached end of method: " + cursor.Method.FullName);
                deltaStack -= cursor.Next.GetDeltaStackPop(isVoidReturnMethod);
                cursor.HandelBranch(ref deltaStack, false, branchQueue);
                deltaStack -= cursor.Prev.GetDeltaStackPush(isVoidReturnMethod);
            } while (deltaStack >= 0);
            if (doSpecialTypeFind) {
                switch (cursor.Prev.OpCode.Code) {
                    case Code.Not:
                    case Code.Neg:
                        goto @continue;
                    case Code.Dup:
                        branchQueue?.Enqueue(new(cursor.Index, deltaStack == -1 ? -2 : -1, true));
                        goto @continue;
                    case Code.And:
                    case Code.Add:
                    case Code.Sub:
                    case Code.Mul:
                        branchQueue?.Enqueue(new(cursor.Index, -2, false));
                        goto @continue;
                    case Code.Ldelem_I:
                    case Code.Ldelem_I1:
                    case Code.Ldelem_I2:
                    case Code.Ldelem_I4:
                    case Code.Ldelem_I8:
                    case Code.Ldelem_Any:
                    case Code.Ldelem_U1:
                    case Code.Ldelem_U2:
                    case Code.Ldelem_U4:
                        if (deltaStack == -1) throw new Exception("When searching for Array type the array reference cannot be used.");
                        deltaStack = -1;
                        goto @continue;
                }
            }
        }
    }

    private static TypeReference ResolveCulsorInstructionTypeAsPop(this ILCursor cursor, ref int deltaStack, ref bool isAmbigous) {

        switch (cursor.Previous.OpCode.Code) {
            case Code.Call:
            case Code.Calli:
            case Code.Callvirt:
            case Code.Newobj:
                var operandMethod = (IMethodSignature)cursor.Previous.Operand;
                if (!operandMethod.HasParameters) break;
                int parameterIndex = -deltaStack;
                if (operandMethod.HasThis) {
                    if (parameterIndex == 1) {
                        try {
                            var operandMethodRef = (MethodReference)operandMethod;
                            return operandMethodRef.DeclaringType;
                        } catch (InvalidCastException e) {
                            throw new Exception("Failed to get TypeReference from stack data.", e);
                        }
                    }
                    parameterIndex--; // NonStatic method & we're not searching for the base type
                }
                return operandMethod.Parameters[parameterIndex - 1].ParameterType;
            case Code.Ret:
                return cursor.Method.ReturnType;
            case Code.Starg:
            case Code.Starg_S:
                return cursor.Method.Parameters[(int)cursor.Previous.Operand].ParameterType;
            case Code.Stfld:
            case Code.Stsfld:
                return ((FieldReference)cursor.Previous.Operand).FieldType;
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
                int index = (int)cursor.Previous.OpCode.Code - 10;
                return cursor.Method.Body.Variables[index].VariableType;
            case Code.Stloc:
            case Code.Stloc_S:
                index = (int)cursor.Previous.Operand;
                return cursor.Method.Body.Variables[index].VariableType;
            case Code.Box:
            case Code.Stobj:
                return (TypeReference)cursor.Previous.Operand;
            case Code.And:          // Could be Bool     ? - This Opcode should never be found
            case Code.Neg:          // Could be Bool     ? - This Opcode should never be found
            case Code.Add:          // Could be Unsigned ? - This Opcode should never be found
            case Code.Sub:          // Could be Unsigned ? - This Opcode should never be found 
            case Code.Mul:          // Could be Unsigned ? - This Opcode should never be found
            case Code.Div:          // Could be Unsigned ?
            case Code.Add_Ovf:
            case Code.Sub_Ovf:
            case Code.Mul_Ovf:
            case Code.Rem:
            case Code.Stind_I:
            case Code.Stind_I1:
            case Code.Stind_I2:
            case Code.Stind_I4:
            case Code.Stind_I8:     // Maybe should return long instead as a type - This Opcode should never be found
            case Code.Or:
            case Code.Xor:
            case Code.Shl:
            case Code.Shr:
            case Code.Ldelema:      // Should check if it's the correct one of the removed values
            case Code.Ldelem_I1:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_U1:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_I2:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_U2:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_I4:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_U4:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_I8:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_I:     // Should check if it's the correct one of the removed values
            case Code.Ldelem_R4:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_R8:    // Should check if it's the correct one of the removed values
            case Code.Ldelem_Ref:   // Should check if it's the correct one of the removed values
            case Code.Stelem_I:     // Should check if it's the correct one of the removed values
            case Code.Stelem_I1:    // Should check if it's the correct one of the removed values
            case Code.Stelem_I2:    // Should check if it's the correct one of the removed values
            case Code.Stelem_I4:    // Should check if it's the correct one of the removed values
            case Code.Stelem_I8:    // Should check if it's the correct one of the removed values
            case Code.Stelem_R4:    // Should check if it's the correct one of the removed values
            case Code.Stelem_R8:    // Should check if it's the correct one of the removed values
            case Code.Stelem_Ref:   // Should check if it's the correct one of the removed values
            case Code.Ldelem_Any:   // Should check if it's the correct one of the removed values
            case Code.Stelem_Any:   // Should check if it's the correct one of the removed values
                return GetIntRef();
            case Code.Conv_I:       // Could be Anything ??
            case Code.Conv_I1:      // Could be Anything ??
            case Code.Conv_I2:      // Could be Anything ??
            case Code.Conv_I4:      // Could be Anything ??
            case Code.Conv_I8:      // Could be Anything ??
            case Code.Conv_R4:      // Could be Anything ??
            case Code.Conv_R8:      // Could be Anything ??
            case Code.Conv_U:       // Could be Anything ??
            case Code.Conv_U4:      // Could be Anything ??
            case Code.Conv_U8:      // Could be Anything ??
            case Code.Conv_Ovf_I:   // Could be Anything ??
            case Code.Conv_Ovf_I1:  // Could be Anything ??
            case Code.Conv_Ovf_I2:  // Could be Anything ??
            case Code.Conv_Ovf_I4:  // Could be Anything ??
            case Code.Conv_Ovf_I8:  // Could be Anything ??
            case Code.Conv_Ovf_U1:  // Could be Anything ??
            case Code.Conv_Ovf_U2:  // Could be Anything ??
            case Code.Conv_Ovf_U4:  // Could be Anything ??
            case Code.Conv_Ovf_U8:  // Could be Anything ??
            case Code.Conv_Ovf_U:   // Could be Anything ??
            case Code.Conv_U2:      // Could be Anything ??
            case Code.Conv_U1:      // Could be Anything ??
                isAmbigous = true;
                return GetIntRef();
            case Code.Add_Ovf_Un:
            case Code.Sub_Ovf_Un:
            case Code.Mul_Ovf_Un:
            case Code.Div_Un:
            case Code.Shr_Un:
            case Code.Conv_R_Un:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_Ovf_I1_Un:
            case Code.Conv_Ovf_I2_Un:
            case Code.Conv_Ovf_I4_Un:
            case Code.Conv_Ovf_I8_Un:
            case Code.Conv_Ovf_U_Un:
            case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_U4_Un:
            case Code.Conv_Ovf_U8_Un:
                return GetUIntRef();
            case Code.Brfalse:      // Could be also be an == 0 check
            case Code.Brfalse_S:    // Could be also be an == 0 check
            case Code.Brtrue:       // Could be also be a  != 0 check
            case Code.Brtrue_S:     // Could be also be a  != 0 check
                isAmbigous = true;
                return GetBoolRef();
            case Code.Beq:
            case Code.Beq_S:
            case Code.Rem_Un:
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Ble:
            case Code.Ble_S:
            case Code.Blt:
            case Code.Blt_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
            case Code.Bne_Un:
            case Code.Bne_Un_S:
            case Code.Ceq:
            case Code.Cgt:
            case Code.Clt:
            case Code.Cgt_Un:
            case Code.Clt_Un:
                return null;        // Use the Queue instead
            case Code.Nop:
            case Code.Break:
            case Code.Ldarg:
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldarg_S:
            case Code.Ldarga:
            case Code.Ldarga_S:
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloca:
            case Code.Ldloca_S:
            case Code.Ldnull:
            case Code.Ldc_I4_M1:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
            case Code.Ldc_I4_S:
            case Code.Ldc_I4:
            case Code.Ldc_I8:
            case Code.Ldc_R4:
            case Code.Ldc_R8:
            case Code.Jmp:
            case Code.Br:
            case Code.Br_S:
            case Code.Switch:
            case Code.Ldind_I:
            case Code.Ldind_I1:
            case Code.Ldind_I2:
            case Code.Ldind_I4:
            case Code.Ldind_I8:
            case Code.Ldind_U1:
            case Code.Ldind_U2:
            case Code.Ldind_U4:
            case Code.Ldind_R4:
            case Code.Ldind_R8:
            case Code.Ldind_Ref:
            case Code.Stind_R4:
            case Code.Stind_R8:
            case Code.Stind_Ref:
            case Code.Cpobj:
            case Code.Ldobj:
            case Code.Ldstr:
            case Code.Castclass:
            case Code.Isinst:
            case Code.Unbox:
            case Code.Unbox_Any:
            case Code.Throw:
            case Code.Ldfld:
            case Code.Ldflda:
            case Code.Newarr:
            case Code.Ldlen:
            case Code.Refanyval:
            case Code.Ckfinite:
            case Code.Mkrefany:
            case Code.Ldtoken:
            case Code.Endfinally:
            case Code.Leave:
            case Code.Leave_S:
            case Code.Arglist:
            case Code.Ldftn:
            case Code.Ldvirtftn:
            case Code.Localloc:
            case Code.Endfilter:
            case Code.Unaligned:
            case Code.Volatile:
            case Code.Tail:
            case Code.Initobj:
            case Code.Constrained:
            case Code.Cpblk:
            case Code.Initblk:
            case Code.No:
            case Code.Rethrow:
            case Code.Sizeof:
            case Code.Refanytype:
            case Code.Readonly:
            case Code.Ldsfld:
            case Code.Ldsflda:
            case Code.Not:
            case Code.Dup:
                throw new Exception("Invalid OpCode found: " + cursor.Previous.OpCode.Name);
            case Code.Pop:
                throw new NotImplementedException("Unable to find type for 'pop' Instruction, type is popped of the stack without use.");
            default:
                throw new Exception("Unexpected OpCode found: " + cursor.Previous.OpCode.Name);
        }
        throw new NotImplementedException();
    }
    private static TypeReference ResolveCulsorInstructionTypeAsPush(this ILCursor cursor, ref int deltaStack, ref bool isAmbigous) {
        
        switch (cursor.Previous.OpCode.Code) {
            case Code.Call:
            case Code.Calli:
            case Code.Callvirt:
                return ((IMethodSignature)cursor.Previous.Operand).ReturnType;
            case Code.Newobj:
                return ((MethodReference)cursor.Previous.Operand).DeclaringType;
            case Code.Ldfld:
            case Code.Ldsfld:
                return ((FieldReference)cursor.Previous.Operand).FieldType;
            case Code.Newarr:   // This is not the type of the array but the type of it's elements might searched here.
            case Code.Unbox:
            case Code.Unbox_Any:
                return (TypeReference)cursor.Previous.Operand;
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
                var index = (int)cursor.Previous.OpCode.Code - 2;
                if (cursor.Method.IsStatic) {
                    if (index == 0) return cursor.Method.DeclaringType;
                    else index--;
                }
                return cursor.Method.Parameters[index].ParameterType;
            case Code.Ldarg:
            case Code.Ldarg_S:
                index = (int)cursor.Previous.Operand;
                if (cursor.Method.IsStatic) {
                    if (index == 0) return cursor.Method.DeclaringType;
                    else index--;
                }
                return cursor.Method.Parameters[index].ParameterType;
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
                index = (int)cursor.Previous.OpCode.Code - 6;
                return cursor.Method.Body.Variables[index].VariableType;
            case Code.Ldloc:
            case Code.Ldloc_S:
                index = (int)cursor.Previous.Operand;
                return cursor.Method.Body.Variables[index].VariableType;
            case Code.And:          // Could be something else ? - This Opcode should never be found
            case Code.Neg:          // Could be something else ? - This Opcode should never be found
            case Code.Add:          // Could be something else ? - This Opcode should never be found
            case Code.Sub:          // Could be something else ? - This Opcode should never be found 
            case Code.Mul:          // Could be something else ? - This Opcode should never be found
            case Code.Div:          // Could be something else ?
            case Code.Add_Ovf:
            case Code.Sub_Ovf:
            case Code.Mul_Ovf:
            case Code.Rem:
            case Code.Ldind_I:      // Could be something else ?
            case Code.Ldind_I1:     // Could be something else ?
            case Code.Ldind_I2:     // Could be something else ?
            case Code.Ldind_I4:     // Could be something else ?
            case Code.Ldind_I8:     // Maybe should return long instead as a type - This Opcode should never be found
            case Code.Or:
            case Code.Xor:
            case Code.Shl:
            case Code.Shr:
            case Code.Sizeof:       // Might be uint instead ?
                return GetIntRef();
            case Code.Ldc_I4:
            case Code.Ldc_I8:       // Maybe should return long instead as a type - This Opcode should never be found
            case Code.Ldc_I4_M1:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
            case Code.Ldc_I4_S:
            case Code.Conv_I:       // Could be Anything ??
            case Code.Conv_I1:      // Could be Anything ??
            case Code.Conv_I2:      // Could be Anything ??
            case Code.Conv_I4:      // Could be Anything ??
            case Code.Conv_I8:      // Could be Anything ??
            case Code.Conv_Ovf_I:   // Could be Anything ??
            case Code.Conv_Ovf_I1:  // Could be Anything ??
            case Code.Conv_Ovf_I2:  // Could be Anything ??
            case Code.Conv_Ovf_I4:  // Could be Anything ??
            case Code.Conv_Ovf_I8:  // Could be Anything ??
            case Code.Conv_Ovf_I_Un:// Could be Anything ??
            case Code.Conv_Ovf_I1_Un:// Could be Anything ??
            case Code.Conv_Ovf_I2_Un:// Could be Anything ??
            case Code.Conv_Ovf_I4_Un:// Could be Anything ??
            case Code.Conv_Ovf_I8_Un:// Could be Anything ??
                isAmbigous = true;
                return GetIntRef();
            case Code.Ldind_U1:     // Could be something else ?
            case Code.Ldind_U2:     // Could be something else ?
            case Code.Ldind_U4:     // Could be something else ?
            case Code.Add_Ovf_Un:
            case Code.Sub_Ovf_Un:
            case Code.Mul_Ovf_Un:
            case Code.Div_Un:
            case Code.Rem_Un:
            case Code.Shr_Un:
            case Code.Ldlen:        // Might be int instead ?
                return GetUIntRef();
            case Code.Conv_U:       // Could be Anything ??
            case Code.Conv_U1:      // Could be Anything ??
            case Code.Conv_U2:      // Could be Anything ??
            case Code.Conv_U4:      // Could be Anything ??
            case Code.Conv_U8:      // Could be Anything ??
            case Code.Conv_Ovf_U:   // Could be Anything ??
            case Code.Conv_Ovf_U1:  // Could be Anything ??
            case Code.Conv_Ovf_U2:  // Could be Anything ??
            case Code.Conv_Ovf_U4:  // Could be Anything ??
            case Code.Conv_Ovf_U8:  // Could be Anything ??
            case Code.Conv_Ovf_U_Un:// Could be Anything ??
            case Code.Conv_Ovf_U1_Un:// Could be Anything ??
            case Code.Conv_Ovf_U2_Un:// Could be Anything ??
            case Code.Conv_Ovf_U4_Un:// Could be Anything ??
            case Code.Conv_Ovf_U8_Un:// Could be Anything ??
                isAmbigous = true;
                return GetUIntRef();
            case Code.Cgt:
            case Code.Clt:
            case Code.Cgt_Un:
            case Code.Clt_Un:
            case Code.Ceq:
                return GetBoolRef();
            case Code.Ldelem_I:
            case Code.Ldelem_I1:
            case Code.Ldelem_I2:
            case Code.Ldelem_I4:
            case Code.Ldelem_I8:
            case Code.Ldelem_Any:
                return null;
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
                isAmbigous = true;
                return GetUIntRef();
            case Code.Nop:
            case Code.Break:
            case Code.Ldc_R4:
            case Code.Ldc_R8:
            case Code.Jmp:
            case Code.Br:
            case Code.Br_S:
            case Code.Switch:
            case Code.Ldind_R4:
            case Code.Ldind_R8:
            case Code.Ldind_Ref:
            case Code.Stind_R4:
            case Code.Stind_R8:
            case Code.Stind_Ref:
            case Code.Ldflda:
            case Code.Ldsflda:
            case Code.Ldarga:
            case Code.Ldarga_S:
            case Code.Ldloca:
            case Code.Ldloca_S:
            case Code.Ldnull:
            case Code.Ret:
            case Code.Starg:
            case Code.Starg_S:
            case Code.Stfld:
            case Code.Stsfld:
            case Code.Stloc:
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Stloc_S:
            case Code.Stind_I:
            case Code.Stind_I1:
            case Code.Stind_I2:
            case Code.Stind_I4:
            case Code.Stind_I8:
            case Code.Box:
            case Code.Stobj:
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Ble:
            case Code.Ble_S:
            case Code.Blt:
            case Code.Blt_S:
            case Code.Ldelema:
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_Ref:
            case Code.Stelem_Any:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_Ref:
            case Code.Conv_R4:
            case Code.Conv_R8:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
            case Code.Bne_Un:
            case Code.Bne_Un_S:
            case Code.Conv_R_Un:
            case Code.Brfalse:
            case Code.Brfalse_S:
            case Code.Brtrue:
            case Code.Brtrue_S:
            case Code.Beq:
            case Code.Beq_S:
            case Code.Cpobj:
            case Code.Ldobj:
            case Code.Ldstr:
            case Code.Castclass:
            case Code.Isinst:
            case Code.Throw:
            case Code.Refanyval:
            case Code.Ckfinite:
            case Code.Mkrefany:
            case Code.Ldtoken:
            case Code.Endfinally:
            case Code.Leave:
            case Code.Leave_S:
            case Code.Arglist:
            case Code.Ldftn:
            case Code.Ldvirtftn:
            case Code.Localloc:
            case Code.Endfilter:
            case Code.Unaligned:
            case Code.Volatile:
            case Code.Tail:
            case Code.Initobj:
            case Code.Constrained:
            case Code.Cpblk:
            case Code.Initblk:
            case Code.No:
            case Code.Rethrow:
            case Code.Refanytype:
            case Code.Readonly:
            case Code.Not:
            case Code.Dup:
                throw new Exception("Invalid OpCode found: " + cursor.Previous.OpCode.Name);
            case Code.Pop:
                throw new NotImplementedException("Unable to find type for 'pop' Instruction, type is popped of the stack without use.");
            default:
                throw new Exception("Unexpected OpCode found: " + cursor.Previous.OpCode.Name);
        }
        throw new NotImplementedException();
    }

    public static int GetDeltaStack(this Instruction instr, bool isVoidReturnMethod) {
        return instr.GetDeltaStackPush(isVoidReturnMethod) - instr.GetDeltaStackPop(isVoidReturnMethod);
    }
    public static int GetDeltaStackPop(this Instruction instr, bool isVoidReturnMethod) {
        int? pop = GetDeltaStack(instr.OpCode.StackBehaviourPop);
        if (pop == null) {
            switch (instr.OpCode.Code) {
                case Code.Call:
                case Code.Calli:
                case Code.Callvirt:
                case Code.Newobj:
                    var operandMethod = (IMethodSignature)instr.Operand;
                    pop = ( operandMethod.HasThis && instr.OpCode.Code != Code.Newobj) ? -1 : 0; // !IsStatic ???
                    if (!operandMethod.HasParameters) break;
                    pop -= operandMethod.Parameters.Count();
                    break;
                case Code.Ret:
                    pop = isVoidReturnMethod ? 0 : -1;
                    break;
            }
        }
        return (int)pop;
    }
    public static int GetDeltaStackPush(this Instruction instr, bool isVoidReturnMethod) {
        int? push = GetDeltaStack(instr.OpCode.StackBehaviourPush);
        if (push == null) {
            switch (instr.OpCode.Code) {
                case Code.Call:
                case Code.Calli:
                case Code.Callvirt:
                    var operandMethod = (IMethodSignature)instr.Operand;
                    push = operandMethod.ReturnType.Name == "System.Void" ? 0 : 1;
                    break;
            }
        }
        return (int)push;
    }
    public static int? GetDeltaStack(StackBehaviour stackBehaviour) {
        return stackBehaviour switch {
            StackBehaviour.Pop0 => -0,
            StackBehaviour.Pop1 => -1,
            StackBehaviour.Pop1_pop1 => -2,
            StackBehaviour.Popi => -1,
            StackBehaviour.Popi_pop1 => -2,
            StackBehaviour.Popi_popi => -2,
            StackBehaviour.Popi_popi8 => -2,
            StackBehaviour.Popi_popi_popi => -3,
            StackBehaviour.Popi_popr4 => -2,
            StackBehaviour.Popi_popr8 => -2,
            StackBehaviour.Popref => -1,
            StackBehaviour.Popref_pop1 => -2,
            StackBehaviour.Popref_popi => -2,
            StackBehaviour.Popref_popi_popi => -3,
            StackBehaviour.Popref_popi_popi8 => -3,
            StackBehaviour.Popref_popi_popr4 => -3,
            StackBehaviour.Popref_popi_popr8 => -3,
            StackBehaviour.Popref_popi_popref => -3,
            StackBehaviour.PopAll => int.MinValue,
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Pushi => 1,
            StackBehaviour.Pushi8 => 1,
            StackBehaviour.Pushr4 => 1,
            StackBehaviour.Pushr8 => 1,
            StackBehaviour.Pushref => 1,
            StackBehaviour.Varpop => null,
            StackBehaviour.Varpush => null,
            _ => throw new InvalidOperationException("Invalid StackBehaviour: " + stackBehaviour)
        };
    }

    private static void HandelBranch(this ILCursor cursor, ref int deltaStack, bool forwards, Queue<Tuple<int, int, bool>> branchQueue) {
        if (!cursor.Next.IsBranch()) return;
        var target = ((Instruction)cursor.Next.Operand);
        var branchDeltaStack = deltaStack;

        if (branchCodes0.Contains(cursor.Next.OpCode.Code)) {
            if (forwards) { cursor.Goto(target); return; }
            throw new Exception("Hit Branch wall while searching in: " + cursor.Method.FullName);
        }
        if (branchCodes1.Contains(cursor.Next.OpCode.Code)) branchDeltaStack--;
        else branchDeltaStack -= 2;
        if (branchDeltaStack < 0) return;

        var origIndex = cursor.Index;
        branchQueue.Enqueue(new(cursor.Goto(target).Index, branchDeltaStack, true));
        cursor.Index = origIndex;
    }

    #endregion
}
