using Mono.Cecil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;

namespace OpusMutatum.Merging;

public class MethodLayerTable {
    readonly Dictionary<Tuple<string, string>, int> layerTable = [];

    public string PushMethodLayer(TypeDefinition targetType, MethodDefinition method) {
        var typeName = targetType.GetPatchFullName();
        var identifier = new Tuple<string, string>(typeName, method.Name);

        MethodDefinition existingMethod = targetType.FindMethod(method.GetID(type: typeName));
        if (existingMethod == null) {
            return method.Name;
        }

        if (layerTable.TryGetValue(identifier, out var layerCount)) {

            layerTable[identifier] = layerCount + 1;
            return "layer_" + layerCount + "_" + method.Name;
        }

        layerTable[identifier] = 1;
        return "layer_0_" + method.Name;
    }

    public string TransformOriginalMethodName(string methodName, string typeName) {
        var identifier = new Tuple<string, string>(typeName, methodName);

        if (layerTable.TryGetValue(identifier, out var layerCount)) {
            return "layer_0_" + methodName;
        }
        return methodName;
    }

    public string ReduceToOriginalMethodName(string methodName, string typeName) {
        if (methodName.StartsWith("layer_0_")) {
            var identifier = new Tuple<string, string>(typeName, methodName[8..]);
            if (!layerTable.TryGetValue(identifier, out var layerCount) || layerCount < 1) {
                return methodName[8..];
            }
        }
        return methodName;
    }
}

