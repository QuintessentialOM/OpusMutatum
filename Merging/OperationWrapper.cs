using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using static OpusMutatum.Merging.MethodGeneration;

namespace OpusMutatum.Merging;


// TODO: add better error state throws ( for example when stringLiteral is null ).
public class OperationWrapper(ModificationStash stash) {
    readonly Dictionary<Tuple<string, string, string>, List<MethodReference>> wrapTable = [];


    public void Push(CustomAttribute atrib, MethodDefinition method, TypeDefinition targetType) {
        string targetMethodName = (string)atrib.ConstructorArguments[0].Value;
        string wrapTarget = (string)atrib.ConstructorArguments[1].Value;
        string wrapMetadata = (string)atrib.ConstructorArguments[2].Value;
        string uniqueSpecifier = GetUniqueTargetSpecifier(wrapTarget, wrapMetadata);

        // TODO add support for method overrides for wrapTarget
        // TODO guarantee uniqueness even when only method params differ
        var identifier = new Tuple<string, string, string>(targetType.GetPatchFullName(), targetMethodName, uniqueSpecifier);
        if (!wrapTable.TryGetValue(identifier, out var callerMethods)) {
            callerMethods = [];
            wrapTable[identifier] = callerMethods;
        }
        callerMethods.Add(method);

        WrapOperationNameData nameData = new("<" + targetMethodName + ">_", "_" + uniqueSpecifier, callerMethods);
        stash.PushWrapOperation(targetType, method, targetMethodName, nameData, wrapMetadata, callerMethods.Count-1, wrapTarget);
    }

    public static string GetUniqueTargetSpecifier(string wrapTarget, string wrapMetadata) {
        return "<" + wrapTarget + ">_<" + wrapMetadata + ">";
        // TODO: Shorten, guarantee uniqueness
    }

    #region targetCall

    public static void GenerateForCall(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer) {
        var origCalledMethodDeclaringType = targetType.Module.FindType(callMetadata.Split("::")[0]);
        var origCalledMethod = origCalledMethodDeclaringType.FindMethodAddNonStaticBase(method.GetIDWithIgnore(name: callMetadata.Split("::")[1], type: origCalledMethodDeclaringType.FullName, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType));
        var modifiedMethod = targetType.FindMethodByName(methodName: targetMethodName);

        if (layer == 0) {
            var functMethod = GenerateFunctMethod(targetType, origCalledMethod, nameData.GenerateName(layer));
            InjectMethodCall(modifiedMethod, origCalledMethod, functMethod, method);
        } else {
            var compilerType = targetType.GetCompilerGeneratedType();
            var previousFunct = compilerType.FindMethodByName(nameData.GenerateName(layer - 1));
            var previousCall = GetPreviousCall(modifiedMethod, previousFunct);

            var functMethod = GenerateFunctMethod(targetType, origCalledMethod, nameData.GenerateName(layer));
            InjectMethodCall(functMethod, origCalledMethod, previousFunct, previousCall);
            ReplaceInjectedCall(modifiedMethod, previousFunct, functMethod, previousCall, method);
        }
    }

    public static MethodDefinition GenerateFunctMethod(TypeDefinition targetType, MethodDefinition origCalledMethod, string generatedMethodName) {
        var baseLayer = new MethodDefinition(generatedMethodName, MethodAttributes.Public | MethodAttributes.Static, origCalledMethod.ReturnType);
        baseLayer.DeclaringType = targetType.GetCompilerGeneratedType();
        baseLayer.DeclaringType.Methods.Add(baseLayer);
        ILCursor cursor = new(new ILContext(baseLayer));
        int i = 0;

        if (origCalledMethod.IsCallvirt()) {
            baseLayer.Parameters.Add(new ParameterDefinition("@baseType", ParameterAttributes.None, origCalledMethod.DeclaringType));
            cursor.EmitLdargOptimised(i);
            i++;
        }
        foreach (var parameter in origCalledMethod.Parameters) {
            baseLayer.Parameters.Add(parameter.Clone());
            cursor.EmitLdargOptimised(i);
            i++;
        }
        cursor.EmitCall(origCalledMethod);
        cursor.EmitRet();

        return baseLayer;
    }
    public static void InjectMethodCall(MethodDefinition targetMethod, MethodDefinition origCalledMethod, MethodDefinition functMethod, MethodDefinition calledMethod) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.Before,
                instr => instr.MatchCallWeak(origCalledMethod)
            )) {
                cursor.Remove();
                cursor.EmitLdnull();
                cursor.EmitLdftn(functMethod);
                cursor.EmitNewobj(functMethod.GetFuncCtor());
                cursor.EmitCall(calledMethod);
            }
        }
    }
    
    #endregion

    #region targetField

    public static void GenerateForField(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer, bool isRead) {
        var origCalledFieldDeclaringType = targetType.Module.FindType(callMetadata.Split("::")[0]);
        var origCalledField = origCalledFieldDeclaringType.FindFieldDeep(name: callMetadata.Split("::")[1]);
        var modifiedMethod = targetType.FindMethodByName(methodName: targetMethodName);

        if (layer == 0) {
            
            var funcMethod = isRead ? GenerateFunctReadFieldMethod(targetType, origCalledField, nameData.GenerateName(layer))
                                   : GenerateFunctWriteFieldMethod(targetType, origCalledField, nameData.GenerateName(layer));
            InjectMethodCall(modifiedMethod, origCalledField, funcMethod, method, isRead);
        } else {
            var compilerType = targetType.GetCompilerGeneratedType();
            var previousFunct = compilerType.FindMethodByName(nameData.GenerateName(layer - 1));
            var previousCall = GetPreviousCall(modifiedMethod, previousFunct);

            var functMethod = isRead ? GenerateFunctReadFieldMethod(targetType, origCalledField, nameData.GenerateName(layer))
                                    : GenerateFunctWriteFieldMethod(targetType, origCalledField, nameData.GenerateName(layer));
            if (isRead) {
                InjectMethodCall(functMethod, origCalledField, previousFunct, previousCall, true);
                ReplaceInjectedCall(modifiedMethod, previousFunct, functMethod, previousCall, method);
            } else {
                InjectMethodCall(previousFunct, origCalledField, functMethod, method, false);
            }
        }
    }


    public static MethodDefinition GenerateFunctReadFieldMethod(TypeDefinition targetType, FieldDefinition origCalledField, string generatedMethodName) {
        var baseLayer = new MethodDefinition(generatedMethodName, MethodAttributes.Public | MethodAttributes.Static, origCalledField.FieldType);
        baseLayer.DeclaringType = targetType.GetCompilerGeneratedType();
        baseLayer.DeclaringType.Methods.Add(baseLayer);
        ILCursor cursor = new(new ILContext(baseLayer));
        int i = 0;

        if (!origCalledField.IsStatic) {
            baseLayer.Parameters.Add(new ParameterDefinition("@baseType", ParameterAttributes.None, origCalledField.DeclaringType));
            cursor.EmitLdargOptimised(i);
            i++;
            cursor.EmitLdfld(origCalledField);
        } else
            cursor.EmitLdsfld(origCalledField);
        cursor.EmitRet();

        return baseLayer;
    }
    public static MethodDefinition GenerateFunctWriteFieldMethod(TypeDefinition targetType, FieldDefinition origCalledField, string generatedMethodName) {
        var baseLayer = new MethodDefinition(generatedMethodName, MethodAttributes.Public | MethodAttributes.Static, GetVoidRef());
        baseLayer.DeclaringType = targetType.GetCompilerGeneratedType();
        baseLayer.DeclaringType.Methods.Add(baseLayer);
        ILCursor cursor = new(new ILContext(baseLayer));
        int i = 0;

        if (!origCalledField.IsStatic) {
            baseLayer.Parameters.Add(new ParameterDefinition("@baseType", ParameterAttributes.None, origCalledField.DeclaringType));
            cursor.EmitLdargOptimised(i);
            i++;
        }
        cursor.EmitLdargOptimised(i);
        baseLayer.Parameters.Add(new ParameterDefinition("fieldType", ParameterAttributes.None, origCalledField.FieldType));
        if (origCalledField.IsStatic) cursor.EmitStsfld(origCalledField);
        else                          cursor.EmitStfld(origCalledField);
        
        cursor.EmitRet();

        return baseLayer;
    }
    public static void InjectMethodCall(MethodDefinition targetMethod, FieldDefinition origCalledField, MethodDefinition functMethod, MethodDefinition calledMethod, bool isRead) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.Before,
                instr => isRead ? instr.MatchLdfldWeak(origCalledField) : instr.MatchStfldWeak(origCalledField)
            )) {
                cursor.Remove();
                cursor.EmitLdnull();
                cursor.EmitLdftn(functMethod);
                cursor.EmitNewobj(functMethod.GetFuncCtor());
                cursor.EmitCall(calledMethod);
            }
        }
    }

    #endregion

    #region targetLiteral

    public static void GenerateForStringLiteral(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer) {
        // The callMetadata is the searched stringLiteral
        var modifiedMethod = targetType.FindMethodByName(methodName: targetMethodName);
        if (layer == 0) {
            InjectMethodCallAfterString(modifiedMethod, callMetadata, method, layer);
        } else {
            InjectMethodCallAfterPrevious(modifiedMethod, nameData.calls[layer - 1], method);
        }
    }
    public static void GenerateForNumericLiteral(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer, bool isEnum) {
        // The callMetadata is the searched stringLiteral
        var modifiedMethod = targetType.FindMethodByName(methodName: targetMethodName);
        int @int = 0; long @long = 0; float @float = 0; double @double = 0; TypeDefinition @enum = null;
        NumericLiteralType type;

        if (isEnum) {
            type = NumericLiteralType.Enum;
            string enumType =  callMetadata[..callMetadata.LastIndexOf('.')];
            string enumValue = callMetadata[(callMetadata.LastIndexOf('.')+1)..];
            @enum = targetType.Module.FindType(enumType);
            @int = (int)@enum.Fields.Where(fieldDef => fieldDef.Name == enumValue).Single().Constant;
        } else
            type = ParseNumericLiteral(callMetadata, ref @int, ref @long, ref @float, ref @double);

        if (layer == 0) {
            InjectMethodCallAfterNumeric(modifiedMethod, type, method, @int, @long, @float, @double, @enum);
        } else {
            InjectMethodCallAfterPrevious(modifiedMethod, nameData.calls[layer - 1], method);
        }
    }

    public static void InjectMethodCallAfterString(MethodDefinition targetMethod, string stringLiteral, MethodDefinition calledMethod, int layer) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.After,
                instr => instr.MatchLdstr(stringLiteral)
            )) {
                cursor.EmitCall(calledMethod);
                if (!targetMethod.IsStatic) {
                    cursor.GotoFirstArgumentInsert(out int origIndex);
                    cursor.EmitLdarg0();
                    cursor.Index = origIndex;
                }
            }
        }
    }
    public static void InjectMethodCallAfterNumeric(MethodDefinition targetMethod, NumericLiteralType type, MethodDefinition calledMethod, int @int, long @long, float @float, double @double, TypeReference @enum) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.After,
                (instr, index) => instr.MatchNumericLiteralWithAnalysis(cursor, index, type, @int, @long, @float, @double, @enum)
            )) {
                cursor.EmitCall(calledMethod);
                if (!targetMethod.IsStatic) {
                    cursor.GotoFirstArgumentInsert(out int origIndex);
                    cursor.EmitLdarg0();
                    cursor.Index = origIndex;
                }
            }
        }
    }
    public static void InjectMethodCallAfterPrevious(MethodDefinition targetMethod, MethodReference previousCall, MethodDefinition calledMethod) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.After,
                instr => instr.MatchCallWeak(previousCall)
            )) {
                cursor.EmitCall(calledMethod);
                if (!targetMethod.IsStatic) {
                    cursor.GotoFirstArgumentInsert(out int origIndex);
                    cursor.EmitLdarg0();
                    cursor.Index = origIndex;
                }
            }
        }
    }

    #endregion

    #region targetNew

    public static void GenerateForNew(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer) {
        var modifiedMethod = targetType.FindMethodByName(methodName: targetMethodName);
        var objType = targetType.Module.FindType(callMetadata);
        var ctorMehotd = objType.FindMethod(method.GetIDWithIgnore(name: ".ctor", type: objType.FullName, returnTypeOverride: GetVoidRef(), ignoreFirstParam: !modifiedMethod.IsStatic, nonStaticBaseType: method.IsStatic ? null : method.DeclaringType));

        if (layer == 0) {
            var functMethod = GenerateFunctMethodWithNew(targetType, ctorMehotd, objType, nameData.GenerateName(layer));
            InjectMethodCallAtNew(modifiedMethod, ctorMehotd, functMethod, method);
        } else {
            var compilerType = targetType.GetCompilerGeneratedType();
            var previousFunct = compilerType.FindMethodByName(nameData.GenerateName(layer - 1));
            var previousCall = GetPreviousCall(modifiedMethod, previousFunct);

            var functMethod = GenerateFunctMethodWithNew(targetType, ctorMehotd, objType, nameData.GenerateName(layer));
            InjectMethodCallAtNew(functMethod, ctorMehotd, previousFunct, previousCall);
            ReplaceInjectedCall(modifiedMethod, previousFunct, functMethod, previousCall, method);
        }
    }
    
    public static MethodDefinition GenerateFunctMethodWithNew(TypeDefinition targetType, MethodDefinition ctorMethod, TypeDefinition objType, string generatedMethodName) {
        var baseLayer = new MethodDefinition(generatedMethodName, MethodAttributes.Public | MethodAttributes.Static, objType);
        baseLayer.DeclaringType = targetType.GetCompilerGeneratedType();
        baseLayer.DeclaringType.Methods.Add(baseLayer);
        ILCursor cursor = new(new ILContext(baseLayer));
        int i = 0;
        
        foreach (var parameter in ctorMethod.Parameters) {
            baseLayer.Parameters.Add(parameter.Clone());
            cursor.EmitLdargOptimised(i);
            i++;
        }
        cursor.EmitNewobj(ctorMethod);
        cursor.EmitRet();

        return baseLayer;
    }
    public static void InjectMethodCallAtNew(MethodDefinition targetMethod, MethodDefinition origCalledMethod, MethodDefinition functMethod, MethodDefinition calledMethod) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.Before,
                instr => instr.MatchNewobjWeak(origCalledMethod)
            )) {
                cursor.Remove();
                cursor.EmitLdnull();
                cursor.EmitLdftn(functMethod);
                cursor.EmitNewobj(functMethod.GetFuncCtor());
                cursor.EmitCall(calledMethod);
                if (!targetMethod.IsStatic) {
                    cursor.GotoFirstArgumentInsert(out int origIndex);
                    cursor.EmitLdarg0();
                    cursor.Index = origIndex;
                }
            }
        }
    }

    #endregion


    public static MethodDefinition GetPreviousCall(MethodDefinition targetMethod, MethodDefinition functMethod) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.After,
                instr => instr.MatchLdftn(functMethod),
                instr => instr.MatchNewobjWeak(functMethod.GetFuncCtor()),
                instr => instr.OpCode == OpCodes.Call
            )) {
                return ((MethodReference)cursor.Previous.Operand).SafeResolve();
            }
        }
        throw new Exception($"Failed to find associated call for'{functMethod.Name}' in '{targetMethod}'");
    }
    public static void ReplaceInjectedCall(MethodDefinition targetMethod, MethodDefinition fromFunct, MethodDefinition toFunct, MethodDefinition fromCall, MethodDefinition toCall) {
        if (targetMethod.HasBody) {
            ILCursor cursor = new(new ILContext(targetMethod));
            while (cursor.TryGotoNext(MoveType.Before,
                instr => instr.MatchLdftn(fromFunct),
                instr => instr.MatchNewobjWeak(fromFunct.GetFuncCtor()),
                instr => instr.MatchCall(fromCall)
            )) {
                cursor.RemoveRange(3);
                cursor.EmitLdftn(toFunct);
                cursor.EmitNewobj(toFunct.GetFuncCtor());
                cursor.EmitCall(toCall);
            }
        }
    }

    public readonly struct WrapOperationNameData(string prefix, string postfix, List<MethodReference> calls) {
        public readonly string GenerateName(int id) {
            return prefix + id + postfix;
        }
        public readonly string prefix = prefix;
        public readonly string postfix = postfix;
        public readonly List<MethodReference> calls = calls;
    }
}

