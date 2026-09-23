using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace OpusMutatum;

public static class EnumVariantInjection {
	public static void AddEnumVariants(Collection<TypeDefinition> types, Dictionary<string, Dictionary<int, string>> addedEnumVariants) {
		foreach (var type in types) {
			if (addedEnumVariants.ContainsKey(type.Name)) {
				if (!type.IsEnum)
					throw new Exception($"Attempting to add enum variants to non enum type {type.FullName}");
				
				// TODO could do validation that given integers don't already have corresponding fields? probably unnecessary

				foreach (var (variantValue, variantName) in addedEnumVariants[type.Name]) {
					var field = new FieldDefinition(variantName, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal, type);
					field.Constant = variantValue;
					type.Fields.Add(field);
				}
			}
		}
	}
}
