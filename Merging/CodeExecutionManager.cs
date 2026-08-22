using Mono.Cecil;
using MonoMod;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using static System.Reflection.BindingFlags;

namespace OpusMutatum.Merging;
public class CodeExecutionManager {
    private readonly MergeModder MergeModder;
    readonly Dictionary<string, Dictionary<string, Dictionary<string, System.Reflection.MethodInfo>>> CompiledMethods = [];
    TypeDefinition RuntimeCompiledTypeDef;

    public CodeExecutionManager(MergeModder mergeModder) {
        MergeModder = mergeModder;
        RuntimeCompiledTypeDef = new TypeDefinition("", "RuntimeCompiledTypeDef", TypeAttributes.Public) {
            DeclaringType = null,
            BaseType = MethodGeneration.GetObjectRef(),
            IsClass = true,
            IsSpecialName = true,
        };
    }

    public void ReadMethod(TypeDefinition type, MethodDefinition method, string modClassName) {
        if (!method.IsStatic) throw new Exception("Only static methods can be executed by Mutatum.");
        var nested = RuntimeCompiledTypeDef.GetCompilerGeneratedType(modClassName, false).GetCompilerGeneratedType("<"+ type.Name + ">", false);

        var copy = new MethodDefinition(method.Name, method.Attributes, method.ReturnType) {
            Body = method.Body,
            DeclaringType = nested
        };
        foreach (var parameter in method.Parameters) {
            copy.Parameters.Add(parameter.Clone());
        }

        var origGeneratedType = method.DeclaringType.NestedTypes.Where(type => type.Name == "<>c").SingleOrNull();
        var generatedType = nested.GetCompilerGeneratedType("<>c", false);
        CopyCompileGeneratedType(origGeneratedType, generatedType, method.Name);
        CopyDisplayClasses(nested, method);


        nested.Methods.Add(copy);
    }
    private void CopyCompileGeneratedType(TypeDefinition origGeneratedType, TypeDefinition generatedType, string methodName) {
        if (origGeneratedType == null) return;

        var generatedMethods = origGeneratedType.Methods.Where(m => m.Name.StartsWith("<" + methodName + ">"));

        if (!generatedType.Fields.Any(field => field.Name == "<>9")) {
            var funcField = new FieldDefinition("<>9", FieldAttributes.Static | FieldAttributes.Public | FieldAttributes.InitOnly, generatedType);
            generatedType.Fields.Add(funcField);

            var origCtor = origGeneratedType.FindMethodByName(".ctor");
            var copyCtor = new MethodDefinition(origCtor.Name, origCtor.Attributes, origCtor.ReturnType) {
                Body = origCtor.Body,
                DeclaringType = generatedType
            };
            generatedType.Methods.Add(copyCtor);

            var origCctor = origGeneratedType.FindMethodByName(".cctor");
            var copyCctor = new MethodDefinition(origCctor.Name, origCctor.Attributes, origCctor.ReturnType) {
                DeclaringType = generatedType
            };
            copyCctor.Body = new Mono.Cecil.Cil.MethodBody(copyCctor);
            ILCursor cursor = new(new ILContext(copyCctor));
            cursor.EmitNewobj(copyCtor);
            cursor.EmitStsfld(funcField);
            cursor.EmitRet();
            generatedType.Methods.Add(copyCctor);
        }

        foreach (var methodDef in generatedMethods) {

            var copyGen = new MethodDefinition(methodDef.Name, methodDef.Attributes, methodDef.ReturnType) {
                Body = methodDef.Body,
                DeclaringType = generatedType
            };
            foreach (var parameter in methodDef.Parameters) {
                copyGen.Parameters.Add(parameter.Clone());
            }
            generatedType.Methods.Add(copyGen);
            methodDef.CustomAttributes.Add(new CustomAttribute(MergeModder.GetMonoModIgnoreCtor()));

            string fieldName = "<>9" + methodDef.Name[(2 + methodName.Length + 1)..];

            var fieldDef = origGeneratedType.Fields.Single(field => field.Name == fieldName);
            var copyGenF = new FieldDefinition(fieldDef.Name, fieldDef.Attributes, fieldDef.FieldType) {
                IsStatic = fieldDef.IsStatic
            };
            generatedType.Fields.Add(copyGenF);
            fieldDef.CustomAttributes.Add(new CustomAttribute(MergeModder.GetMonoModIgnoreCtor()));
            fieldDef.CustomAttributes.Add(new CustomAttribute(MergeModder.GetMonoModNoNewCtor()));
        }
    }
    private void CopyDisplayClasses(TypeDefinition declaringType, MethodDefinition method) {
        foreach (var nestedType in method.DeclaringType.NestedTypes) {
            if (nestedType.Name.StartsWith("<>c__DisplayClass")) {
                if (nestedType.Methods.Any(method2 => method2.Name.StartsWith("<" + method.Name + ">"))) {
                    var displayClassDef = new TypeDefinition("", nestedType.Name, nestedType.Attributes) {
                        BaseType = nestedType.BaseType,
                        DeclaringType = declaringType
                    };
                    foreach (var m in nestedType.Methods) {
                        var mC = m.Clone();
                        mC.DeclaringType = displayClassDef;
                        displayClassDef.Methods.Add(mC);
                    }
                    foreach (var f in nestedType.Fields) {
                        f.DeclaringType = displayClassDef;
                        displayClassDef.Fields.Add(f);
                    }
                    nestedType.CustomAttributes.Add(new CustomAttribute(MergeModder.GetMonoModIgnoreCtor()));
                    nestedType.CustomAttributes.Add(new CustomAttribute(MergeModder.GetMonoModNoNewCtor()));
                    declaringType.NestedTypes.Add(displayClassDef);
                }
            }
        }
    }

    public System.Reflection.MethodInfo GetExecutingMethod(string methodName, string patchTypeName, string modClassName) {
        if (CompiledMethods.TryGetValue(modClassName, out var patchTypeMethods)) {
            if (patchTypeMethods.TryGetValue("<" + patchTypeName + ">", out var typeMethods)) {
                if (typeMethods.TryGetValue(methodName, out var method)) {
                    return method;
                }
            }
        }
        return null;
    }

    public void Compile() {
        if (RuntimeCompiledTypeDef != null && RuntimeCompiledTypeDef.NestedTypes.Count > 0) {
            MergeModder.Module.Types.Add(RuntimeCompiledTypeDef);
            Type runtimeCompiledType = ExecuteRules(MergeModder, RuntimeCompiledTypeDef);
            MergeModder.Module.Types.Remove(RuntimeCompiledTypeDef);

            RuntimeCompiledTypeDef = new TypeDefinition("", "RuntimeCompiledTypeDef", TypeAttributes.Public) {
                DeclaringType = null,
                BaseType = MergeModder.Module.TypeSystem.Object,
                IsClass = true,
                IsSpecialName = true
            };

            foreach (var modType in runtimeCompiledType.GetNestedTypes()) {
                Dictionary<string, Dictionary<string, System.Reflection.MethodInfo>> modTypes = [];
                foreach (var type in modType.GetNestedTypes()) {
                    Dictionary<string, System.Reflection.MethodInfo> patchTypes = [];
                    foreach (var method in type.GetMethods(Static | NonPublic | Public | DeclaredOnly)) {
                        patchTypes.Add(method.Name, method);
                    }
                    modTypes.Add(type.Name, patchTypes);
                }
                CompiledMethods.Add(modType.Name, modTypes);
            }
        }
    }
    private static Type ExecuteRules(MergeModder self, TypeDefinition orig) {

        var wrapper = ModuleDefinition.CreateModule(
            $"{self.Module.Name[..^4]}.MonoModRules [MMILRT, ID:{MonoModRulesManager.GetId(self)}]",
            new ModuleParameters() {
                Architecture = self.Module.Architecture,
                AssemblyResolver = self.AssemblyResolver,
                Kind = ModuleKind.Dll,
                MetadataResolver = self.Module.MetadataResolver,
                Runtime = TargetRuntime.Net_2_0
            }
        );
        wrapper.PatchTargetArchitecture();
        var wrapperMod = new MonoModCodeRulesModder() {
            Module = wrapper,
            Orig = orig,

            CleanupEnabled = false,

            DependencyDirs = self.DependencyDirs,
            MissingDependencyResolver = self.MissingDependencyResolver
        };
        wrapperMod.WriterParameters.WriteSymbols = false;
        wrapperMod.WriterParameters.SymbolWriterProvider = null;

        var missingDependencyThrow = self.MissingDependencyThrow;
        self.MissingDependencyThrow = false;

        // Copy all dependencies.
        wrapper.AssemblyReferences.AddRange(self.Module.AssemblyReferences);
        wrapperMod.DependencyMap[wrapper] = [.. self.DependencyMap[self.Module]];

        // Only add a copy of the map - adding the MMILRT asm itself to the map only causes issues.
        wrapperMod.DependencyCache.AddRange(self.DependencyCache);
        foreach (KeyValuePair<ModuleDefinition, List<ModuleDefinition>> mapping in self.DependencyMap)
            wrapperMod.DependencyMap[mapping.Key] = [.. mapping.Value];

        wrapperMod.Mods.AddRange(self.Mods);
        // Required as the relinker only deep-relinks if the method the type comes from is a mod.
        // Fixes nasty reference import sharing issues.
        wrapperMod.Mods.Add(self.Module);

        wrapperMod.PrePatchType(orig, forceAdd: true);
        wrapperMod.PatchType(orig);
        wrapperMod.PatchRefs(); // Runs any special passes in-between, f.e. upgrading from pre-split to post-split.

        System.Reflection.Assembly asm;
        using (var asmStream = new MemoryStream()) {
            wrapperMod.Write(asmStream);
            asmStream.Seek(0, SeekOrigin.Begin);
            asm = ReflectionHelper.Load(asmStream);
        }

        //using (FileStream debugStream = File.OpenWrite(Path.Combine(
        //    self.DependencyDirs[0], $"{orig.Module.Name.Substring(0, orig.Module.Name.Length - 4)}.MonoModRules-MMILRT.dll")))
        //    wrapperMod.Write(debugStream);

        self.MissingDependencyThrow = missingDependencyThrow;

        Type rules = asm.GetType(orig.FullName);
        RuntimeHelpers.RunClassConstructor(rules.TypeHandle);

        return rules;
    }

    private sealed class MonoModCodeRulesModder : MonoModder {

        public TypeDefinition Orig;

        public override void Log(string text) {
            Console.Write("[MonoMod] [RulesCodeModder] ");
            Console.WriteLine(text);
        }

        public override IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
            // Bypass the relinker for the MonoMod rules type + all its nested types
            if (mtp is TypeReference typeRef && Orig.Module.GetType(typeRef.FullName) is TypeDefinition origType)
                for (; origType != null; origType = origType.DeclaringType)
                    if (origType == Orig)
                        return Module.GetType(typeRef.FullName);

            if (mtp is TypeDefinition typeDef && mtp.ToString().StartsWith("patch_") && mtp.ToString().EndsWith("<>c") && context is MethodDefinition methodDef) {
                return methodDef.DeclaringType.NestedTypes.Where(type => type.Name == "<>c").Single();
            }
            if (mtp is TypeDefinition typeDef2 && mtp.ToString().StartsWith("patch_") && mtp.ToString().EndsWith("<>c") && context is TypeDefinition typeDef3 && context.ToString().EndsWith("<>c")) {
                return typeDef3;
            }

            if (mtp is TypeDefinition displayClass && displayClass.Name.StartsWith("<>c__DisplayClass") && context is MethodDefinition methodDef2) {
                return methodDef2.DeclaringType.NestedTypes.Single(type => type.Name == displayClass.Name);
            }

            return base.Relinker(mtp, context);
        }

    }
}
