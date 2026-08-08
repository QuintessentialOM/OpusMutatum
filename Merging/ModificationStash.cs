using Mono.Cecil;
using MonoMod;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using static OpusMutatum.Merging.OperationWrapper;

namespace OpusMutatum.Merging;

public class ModificationStash(MethodLayerTable layerTable) {

    readonly List<Tuple<CustomAttribute, MethodDefinition, TypeDefinition, string>> ILInjectors = [];
    readonly Dictionary<string, List<Tuple<TypeDefinition, MethodDefinition, string, WrapOperationNameData, string, int>>> WrapOperations = [];

    public void PushILInjector(CustomAttribute atrib, MethodDefinition target, TypeDefinition targetType, string nameOverride = null) {
        ILInjectors.Add(new Tuple<CustomAttribute, MethodDefinition, TypeDefinition, string>(atrib, target, targetType, nameOverride));
    }
    public void PushWrapOperation(TypeDefinition targetType, MethodDefinition method, string targetMethodName, WrapOperationNameData nameData, string callMetadata, int layer, string type) {
        if (!WrapOperations.ContainsKey(type)) WrapOperations[type] = [];
        WrapOperations[type].Add(new Tuple<TypeDefinition, MethodDefinition, string, WrapOperationNameData, string, int>(targetType, method, targetMethodName, nameData, callMetadata, layer));
    }

    public void ApplyAllILInjectors(MonoModder modder) {
        foreach (var injector in ILInjectors) {

            string targetTypeName = injector.Item3.GetPatchFullName();
            string targetMethodName = injector.Item4 ?? layerTable.TransformOriginalMethodName(injector.Item2.Name, injector.Item3.GetPatchFullName());

            var modifiedMethod = injector.Item3.FindMethod(injector.Item2.GetID(name: targetMethodName, type: targetTypeName));
            var modifierAttribute = (string)injector.Item1.ConstructorArguments[0].Value;
            if (!modifierAttribute.StartsWith("MonoMod.")) modifierAttribute = "MonoMod." + modifierAttribute;

            var del = modder.CustomMethodAttributeHandlers[modifierAttribute];
            del?.Invoke(null, [modifiedMethod, injector.Item1]);
            modifiedMethod.CustomAttributes.Add(injector.Item1.Clone()); //Why doesn't the attribute appear in the decompiled code?
        }
        ILInjectors.Clear();
    }
    public void ApplyAllWrapOperations() {
        foreach (var typeList in WrapOperations) {
            foreach (var wrapOperation in typeList.Value) {
                string targetMethodName = layerTable.TransformOriginalMethodName(wrapOperation.Item3, wrapOperation.Item1.GetPatchFullName());

                switch (typeList.Key) {
                    case "Call":
                        OperationWrapper.GenerateForCall(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6);
                        break;
                    case "Field-Read":
                        OperationWrapper.GenerateForField(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6, true);
                        break;
                    case "Field-Write":
                        OperationWrapper.GenerateForField(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6, false);
                        break;
                    case "Literal-String":
                        OperationWrapper.GenerateForStringLiteral(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6);
                        break;
                    case "Literal-Numeric":
                        OperationWrapper.GenerateForNumericLiteral(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6, false);
                        break;
                    case "Literal-Enum":
                        OperationWrapper.GenerateForNumericLiteral(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6, true);
                        break;
                    case "New":
                        OperationWrapper.GenerateForNew(wrapOperation.Item1, wrapOperation.Item2, targetMethodName, wrapOperation.Item4, wrapOperation.Item5, wrapOperation.Item6);
                        break;
                    default:
                        throw new Exception("Invalid wrap target point '" + typeList.Key + "' for method '" + wrapOperation.Item2.FullName + "'");
                }
            }
            typeList.Value.Clear();
        }
    }
}