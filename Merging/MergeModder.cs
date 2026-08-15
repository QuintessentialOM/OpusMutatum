using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MonoMod;
using MonoMod.InlineRT;
using MonoMod.Utils;
using OpusMutatum;
using System;

namespace OpusMutatum.Merging;

public class MergeModder : MonoModder {
    private const string LogID = "Merger";

    public readonly MethodLayerTable LayerTable;
    public readonly ModificationStash Stash;
    public readonly OperationWrapper OpWrapper;
    public readonly CodeExecutionManager ExecutionManager;

    public MergeModder() {
        LayerTable = new();
        ExecutionManager = new(this);
        Stash = new(LayerTable, ExecutionManager);
        OpWrapper = new(Stash);
    }

    public override void Log(string text) {
        Console.WriteLine($"[{LogID}] {text}");
    }
    public override void LogVerbose(string text) { }

    public override void AutoPatch() {
        Log("[AutoPatch] Parsing rules in loaded mods");
        foreach (ModuleDefinition mod4 in Mods) {
            ParseRules(mod4);
        }
        ExecutionManager.Compile();
        Log("[AutoPatch] PrePatch pass");
        foreach (ModuleDefinition mod5 in Mods) {
            PrePatchModule(mod5);
        }
        Log("[AutoPatch] Patch pass");
        foreach (ModuleDefinition mod6 in Mods) {
            PatchModule(mod6);
        }
        Log("[AutoPatch] PatchRefs pass");
        PatchRefs();
        if (PostProcessors != null) {
            Delegate[] invocationList = PostProcessors.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++) {
                Log($"[PostProcessor] PostProcessor pass #{i + 1}");
                ((PostProcessor)invocationList[i])?.Invoke(this);
            }
        }
    }


    static bool IsPatchType(TypeDefinition type) =>
        type.Name.StartsWith("patch_", StringComparison.Ordinal) || type.GetCustomAttribute("MonoMod.MonoModPatch") != null;
    public override void ParseRules(ModuleDefinition mod) {
        // Parse Custom defined rules ILInjectors
        foreach (TypeDefinition type in mod.Types) {
            if (IsPatchType(type)) {
                foreach (var method in type.Methods) {
                    if (method.GetCustomAttribute("MonoMod.MonoModILInject") is CustomAttribute ILInject) {
                        ExecutionManager.ReadMethod(method, "test");    // TODO replace "test" with mod_id;
                    }
                }
            }
        }
        base.ParseRules(mod);
    }


    public override void PatchModule(ModuleDefinition mod) {
        base.PatchModule(mod);

        Stash.ApplyAllILInjectors(this);
        Stash.ApplyAllWrapOperations();
    }

    public override MethodDefinition PatchMethod(TypeDefinition targetType, MethodDefinition method) {

        if (method.GetCustomAttribute("MonoMod.MonoModILInject") is CustomAttribute injectAtrib && injectAtrib != null) {
            Stash.PushILInjector(injectAtrib, targetType, method, "test");    // TODO replace "test" with mod_id;
            return null;
        }

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

        if (method.HasCustomAttribute("MonoMod.MonoModIgnore")) {
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
            if (item.BaseType != null && item.BaseType.FullName != "System.MulticastDelegate" && item.BaseType.FullName != "System.Delegate" && item.BaseType.FullName != "System.Enum")
                item.IsSealed = false;
        }
    }


    public virtual MethodReference GetMonoModIgnoreCtor() {
        if (_mmIgnoreCtor != null && _mmIgnoreCtor.Module != Module) {
            _mmIgnoreCtor = null;
        }
        if (_mmIgnoreCtor != null) {
            return _mmIgnoreCtor;
        }
        TypeDefinition typeDefinition = null;
        for (int i = 0; i < Module.Types.Count; i++) {
            if (!(Module.Types[i].Namespace == "MonoMod") || !(Module.Types[i].Name == "MonoModIgnore")) {
                continue;
            }
            typeDefinition = Module.Types[i];
            for (int j = 0; j < typeDefinition.Methods.Count; j++) {
                if (typeDefinition.Methods[j].IsConstructor && !typeDefinition.Methods[j].IsStatic) {
                    return _mmIgnoreCtor = typeDefinition.Methods[j];
                }
            }
        }
        LogVerbose("[MonoModIgnore] Adding MonoMod.MonoModIgnore");
        TypeReference typeReference = FindType("System.Attribute");
        typeReference = ((typeReference == null) ? Module.ImportReference(typeof(Attribute)) : Module.ImportReference(typeReference));
        typeDefinition = typeDefinition ?? new TypeDefinition("MonoMod", "MonoModIgnore", TypeAttributes.Public) {
            BaseType = typeReference
        };
        _mmIgnoreCtor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, Module.TypeSystem.Void);
        _mmIgnoreCtor.MetadataToken = GetMetadataToken(TokenType.Method);
        _mmIgnoreCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        _mmIgnoreCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", Module.TypeSystem.Void, typeReference) {
            HasThis = false
        }));
        _mmIgnoreCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        typeDefinition.Methods.Add(_mmIgnoreCtor);
        Module.Types.Add(typeDefinition);
        return _mmIgnoreCtor;
    }
    public virtual MethodReference GetMonoModNoNewCtor() {
        if (_mmNoNewCtor != null && _mmNoNewCtor.Module != Module) {
            _mmNoNewCtor = null;
        }
        if (_mmNoNewCtor != null) {
            return _mmNoNewCtor;
        }
        TypeDefinition typeDefinition = null;
        for (int i = 0; i < Module.Types.Count; i++) {
            if (!(Module.Types[i].Namespace == "MonoMod") || !(Module.Types[i].Name == "MonoModNoNew")) {
                continue;
            }
            typeDefinition = Module.Types[i];
            for (int j = 0; j < typeDefinition.Methods.Count; j++) {
                if (typeDefinition.Methods[j].IsConstructor && !typeDefinition.Methods[j].IsStatic) {
                    return _mmNoNewCtor = typeDefinition.Methods[j];
                }
            }
        }
        LogVerbose("[MonoModNoNew] Adding MonoMod.MonoModNoNew");
        TypeReference typeReference = FindType("System.Attribute");
        typeReference = ((typeReference == null) ? Module.ImportReference(typeof(Attribute)) : Module.ImportReference(typeReference));
        typeDefinition = typeDefinition ?? new TypeDefinition("MonoMod", "MonoModNoNew", TypeAttributes.Public) {
            BaseType = typeReference
        };
        _mmNoNewCtor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, Module.TypeSystem.Void);
        _mmNoNewCtor.MetadataToken = GetMetadataToken(TokenType.Method);
        _mmNoNewCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        _mmNoNewCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", Module.TypeSystem.Void, typeReference) {
            HasThis = false
        }));
        _mmNoNewCtor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        typeDefinition.Methods.Add(_mmNoNewCtor);
        Module.Types.Add(typeDefinition);
        return _mmNoNewCtor;
    }

    private MethodDefinition _mmIgnoreCtor;
    private MethodDefinition _mmNoNewCtor;
}
