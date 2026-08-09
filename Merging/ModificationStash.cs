using Mono.Cecil;
using MonoMod;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using static OpusMutatum.Merging.OperationWrapper;

namespace OpusMutatum.Merging;

public class ModificationStash(MethodLayerTable layerTable) {
    private record ILInjector(CustomAttribute atrib, MethodDefinition target, TypeDefinition targetType, string nameOverride);
    readonly List<ILInjector> ILInjectors = [];
    private record WrapOperation(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer);
    readonly Dictionary<string, List<WrapOperation>> WrapOperations = [];

    public void PushILInjector(CustomAttribute atrib, MethodDefinition target, TypeDefinition targetType, string nameOverride = null) {
        ILInjectors.Add(new(atrib, target, targetType, nameOverride));
    }
    public void PushWrapOperation(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer, string type) {
        if (!WrapOperations.ContainsKey(type)) WrapOperations[type] = [];
        WrapOperations[type].Add(new(targetType, method, targetMethodName, nameData, callMetadata, layer));
    }

    public void ApplyAllILInjectors(MonoModder modder) {
        foreach (var injector in ILInjectors) {

            string targetTypeName = injector.targetType.GetPatchFullName();
            string targetMethodName = injector.nameOverride ?? layerTable.TransformOriginalMethodName(injector.target.Name, injector.targetType.GetPatchFullName());

            var modifiedMethod = injector.targetType.FindMethod(injector.target.GetID(name: targetMethodName, type: targetTypeName));
            var modifierAttribute = (string)injector.atrib.ConstructorArguments[0].Value;
            if (!modifierAttribute.StartsWith("MonoMod.")) modifierAttribute = "MonoMod." + modifierAttribute;

            var del = modder.CustomMethodAttributeHandlers[modifierAttribute];
            del?.Invoke(null, [modifiedMethod, injector.atrib]);
            modifiedMethod.CustomAttributes.Add(injector.atrib.Clone()); //Why doesn't the attribute appear in the decompiled code? TODO: make it appear!?
        }
        ILInjectors.Clear();
    }
    public void ApplyAllWrapOperations() {
        foreach (var typeList in WrapOperations) {
            foreach (var wrapOperation in typeList.Value) {
                string targetMethodName = layerTable.TransformOriginalMethodName(wrapOperation.targetMethodName, wrapOperation.targetType.GetPatchFullName());

                switch (typeList.Key) {
                    case "Call":
                        GenerateForCall(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer);
                        break;
                    case "Field-Read":
                        GenerateForField(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer, true);
                        break;
                    case "Field-Write":
                        GenerateForField(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer, false);
                        break;
                    case "Literal-String":
                        GenerateForStringLiteral(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer);
                        break;
                    case "Literal-Numeric":
                        GenerateForNumericLiteral(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer, false);
                        break;
                    case "Literal-Enum":
                        GenerateForNumericLiteral(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer, true);
                        break;
                    case "New":
                        GenerateForNew(wrapOperation.targetType, wrapOperation.method, targetMethodName, wrapOperation.nameData, wrapOperation.callMetadata, wrapOperation.layer);
                        break;
                    default:
                        throw new Exception("Invalid wrap target point '" + typeList.Key + "' for method '" + wrapOperation.method.FullName + "'");
                }
            }
            typeList.Value.Clear();
        }
    }
}