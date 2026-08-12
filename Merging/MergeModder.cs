using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MonoMod;
using MonoMod.Utils;
using OpusMutatum;
using System;

namespace OpusMutatum.Merging;

public class MergeModder : MonoModder {
    private const string LogID = "Merger";

    public readonly MethodLayerTable LayerTable;
    public readonly ModificationStash Stash;
    public readonly OperationWrapper OpWrapper;

    public MergeModder() {
        LayerTable = new();
        Stash = new(LayerTable);
        OpWrapper = new(Stash);
    }

    public override void Log(string text) {
        Console.WriteLine($"[{LogID}] {text}");
    }
    public override void LogVerbose(string text) { }

    public override void PatchModule(ModuleDefinition mod) {
        base.PatchModule(mod);

        Stash.ApplyAllILInjectors(this);
        Stash.ApplyAllWrapOperations();
    }

    public override MethodDefinition PatchMethod(TypeDefinition targetType, MethodDefinition method) {
        if (method.Name.StartsWith("orig_", StringComparison.Ordinal) || method.HasCustomAttribute("MonoMod.MonoModOriginal"))
            // Ignore original method stubs
            return null;

        if (method.GetCustomAttribute("MonoMod.MonoModWrapOperation") is CustomAttribute wrapAtrib && wrapAtrib != null) {
            OpWrapper.Push(wrapAtrib, method, targetType);
        }


        if (!AllowedSpecialName(method, targetType) || !MatchingConditionals(method, Module))
            // Ignore ignored methods
            return null;

        var typeName = targetType.GetPatchFullName();

        if (SkipList.Contains(method.GetID(type: typeName)))
            return null;

        // Back in the day when patch_ was the only available alternative, this only affected replacements.
        method.Name = method.GetPatchName();

        // If the method's a MonoModConstructor method, just update its attributes to make it look like one.
        if (method.HasCustomAttribute("MonoMod.MonoModConstructor")) {
            // Add MonoModOriginalName as the orig name data gets lost otherwise.
            if (!method.IsSpecialName && !method.HasCustomAttribute("MonoMod.MonoModOriginalName")) {
                var origNameAttrib = new CustomAttribute(GetMonoModOriginalNameCtor());
                origNameAttrib.ConstructorArguments.Add(new CustomAttributeArgument(Module.TypeSystem.String, "orig_" + method.Name));
                method.CustomAttributes.Add(origNameAttrib);
            }

            method.Name = method.IsStatic ? ".cctor" : ".ctor";
            method.IsSpecialName = true;
            method.IsRuntimeSpecialName = true;
        }

        MethodDefinition existingMethod = targetType.FindMethod(method.GetID(type: typeName));
        MethodDefinition origMethod = null;

        if (method.HasCustomAttribute("MonoMod.MonoModIgnore") && !method.HasCustomAttribute("MonoMod.MonoModILInject")) {
            // MonoModIgnore is a special case, as registered custom attributes should still be applied.
            if (existingMethod != null)
                foreach (CustomAttribute attrib in method.CustomAttributes)
                    if (CustomAttributeHandlers.ContainsKey(attrib.AttributeType.FullName) ||
                        CustomMethodAttributeHandlers.ContainsKey(attrib.AttributeType.FullName))
                        existingMethod.CustomAttributes.Add(attrib.Clone());
            return null;
        }

        if (existingMethod == null && method.HasCustomAttribute("MonoMod.MonoModNoNew"))
            return null;

        if (method.HasCustomAttribute("MonoMod.MonoModRemove") && !method.HasCustomAttribute("MonoMod.MonoModIgnore")) {
            if (existingMethod != null)
                targetType.Methods.Remove(existingMethod);
            return null;
        }

        if (method.GetCustomAttribute("MonoMod.MonoModILInject") is CustomAttribute injectAtrib && injectAtrib != null) {
            Stash.PushILInjector(injectAtrib, method, targetType);
            if (method.HasCustomAttribute("MonoMod.MonoModIgnore") || method.Body.CodeSize <= 2) // Return if method only contains 'nop' then 'ret' too.
                return null;
        }

        if (method.HasCustomAttribute("MonoMod.MonoModReplace")) {
            if (existingMethod != null) {
                existingMethod.CustomAttributes.Clear();
                existingMethod.Attributes = method.Attributes;
                existingMethod.IsPInvokeImpl = method.IsPInvokeImpl;
                existingMethod.ImplAttributes = method.ImplAttributes;
            }

        } else if (existingMethod != null) {
            origMethod = existingMethod.Clone();
            origMethod.Name = LayerTable.PushMethodLayer(targetType, method);
            origMethod.Attributes = existingMethod.Attributes & ~MethodAttributes.SpecialName & ~MethodAttributes.RTSpecialName;
            origMethod.MetadataToken = GetMetadataToken(TokenType.Method);
            origMethod.IsVirtual = false; // Fix overflow when calling orig_ method, but orig_ method already defined higher up

            origMethod.Overrides.Clear();
            foreach (MethodReference @override in method.Overrides)
                origMethod.Overrides.Add(@override);

            origMethod.CustomAttributes.Add(new CustomAttribute(GetMonoModOriginalCtor()));

            // Check if we've got custom attributes on our own orig_ method.
            MethodDefinition modOrigMethod = method.DeclaringType.FindMethod(method.GetID(name: method.GetOriginalName()));
            if (modOrigMethod != null)
                foreach (CustomAttribute attrib in modOrigMethod.CustomAttributes)
                    if (CustomAttributeHandlers.ContainsKey(attrib.AttributeType.FullName) ||
                        CustomMethodAttributeHandlers.ContainsKey(attrib.AttributeType.FullName))
                        origMethod.CustomAttributes.Add(attrib.Clone());
            targetType.Methods.Add(origMethod);
        }

        // Fix for .cctor not linking to orig_.cctor
        if (origMethod != null && method.IsConstructor && method.IsStatic && method.HasBody) {
            if (!method.HasCustomAttribute("MonoMod.MonoModConstructor")) {
                Collection<Instruction> instructions = method.Body.Instructions;
                ILProcessor ilProcessor = method.Body.GetILProcessor();
                ilProcessor.InsertBefore(instructions[instructions.Count - 1], ilProcessor.Create(OpCodes.Call, origMethod));
            } else {
                for (int i = 0; i < method.Body.Instructions.Count; i++) {
                    ILProcessor ilProcessor = method.Body.GetILProcessor();
                    if (method.Body.Instructions[i].OpCode == OpCodes.Call && method.Body.Instructions[i].Operand is MethodDefinition operand && (operand.Name == "orig_cctor" || operand.Name == "orig_ctor"))
                        ilProcessor.Replace(method.Body.Instructions[i], ilProcessor.Create(OpCodes.Call, origMethod));
                }
            }
        }

        if (existingMethod != null) {
            existingMethod.Body = method.Body.Clone(existingMethod);
            existingMethod.IsManaged = method.IsManaged;
            existingMethod.IsIL = method.IsIL;
            existingMethod.IsNative = method.IsNative;
            existingMethod.PInvokeInfo = method.PInvokeInfo;
            existingMethod.IsPreserveSig = method.IsPreserveSig;
            existingMethod.IsInternalCall = method.IsInternalCall;
            existingMethod.IsPInvokeImpl = method.IsPInvokeImpl;

            foreach (CustomAttribute attrib in method.CustomAttributes)
                existingMethod.CustomAttributes.Add(attrib.Clone());

            method = existingMethod;

        } else {
            var clone = new MethodDefinition(method.Name, method.Attributes, Module.TypeSystem.Void);
            clone.MetadataToken = GetMetadataToken(TokenType.Method);
            clone.CallingConvention = method.CallingConvention;
            clone.ExplicitThis = method.ExplicitThis;
            clone.MethodReturnType = method.MethodReturnType;
            clone.Attributes = method.Attributes;
            clone.ImplAttributes = method.ImplAttributes;
            clone.SemanticsAttributes = method.SemanticsAttributes;
            clone.DeclaringType = targetType;
            clone.ReturnType = method.ReturnType;
            clone.Body = method.Body.Clone(clone);
            clone.PInvokeInfo = method.PInvokeInfo;
            clone.IsPInvokeImpl = method.IsPInvokeImpl;

            foreach (GenericParameter genParam in method.GenericParameters)
                clone.GenericParameters.Add(genParam.Clone());

            foreach (ParameterDefinition param in method.Parameters)
                clone.Parameters.Add(param.Clone());

            foreach (CustomAttribute attrib in method.CustomAttributes)
                clone.CustomAttributes.Add(attrib.Clone());

            foreach (MethodReference @override in method.Overrides)
                clone.Overrides.Add(@override);

            clone.CustomAttributes.Add(new CustomAttribute(GetMonoModAddedCtor()));

            targetType.Methods.Add(clone);

            method = clone;
        }

        if (origMethod != null) {
            var origNameAttrib = new CustomAttribute(GetMonoModOriginalNameCtor());
            origNameAttrib.ConstructorArguments.Add(new CustomAttributeArgument(Module.TypeSystem.String, origMethod.Name));
            method.CustomAttributes.Add(origNameAttrib);

            for (int i = 0; i < method.Body.Instructions.Count; i++) {
                ILProcessor ilProcessor = method.Body.GetILProcessor();
                if (method.Body.Instructions[i].OpCode == OpCodes.Call && method.Body.Instructions[i].Operand is MethodDefinition operand && operand.Name == "orig_" + method.Name)
                    ilProcessor.Replace(method.Body.Instructions[i], ilProcessor.Create(OpCodes.Call, origMethod));
            }
        }

        return method;
    }

    public void PrePatchAssembly() {
        foreach (var item in Module.Types) {
            item.IsSealed = false;
        }
    }
}
