using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace OpusMutatum;

public static class EnumVariantInjection {
	public static void AddEnumVariants(Collection<TypeDefinition> types, Dictionary<string, Dictionary<int, string>> addedEnumVariants) {
		foreach (var type in types) {
			if (addedEnumVariants.ContainsKey(type.Name)) {
				if (!type.IsEnum)
					throw new Exception($"Attempting to add enum variants to non enum type {type.FullName}");
				
				// NOTE: could do validation that given integers don't already have corresponding fields? probably unnecessary

				foreach (var (variantValue, variantName) in addedEnumVariants[type.Name]) {
					var field = new FieldDefinition(variantName, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal, type);
					field.Constant = variantValue;
					type.Fields.Add(field);
				}
			}
		}
    }

    public static void RemoveEnumVariants(Collection<TypeDefinition> types, Dictionary<string, Dictionary<int, string>> removedEnumVariants) {
        foreach (var type in types) {
            if (removedEnumVariants.ContainsKey(type.Name)) {
                if (!type.IsEnum)
                    throw new Exception($"Attempting to add enum variants to non enum type {type.FullName}");

                // NOTE: could do validation for the corresponding field constants? probably unnecessary

                for (int i = 0; i < type.Fields.Count; i++) {
                    var field = type.Fields[i];
                    if (field.IsPublic && field.IsStatic && field.IsLiteral) {
                        if (removedEnumVariants[type.Name].Any(pair => pair.Value == field.Name)){
                            type.Fields.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }
        }
    }
}
