using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using CustomAttributeNamedArgument = Mono.Cecil.CustomAttributeNamedArgument;

namespace Coreifier;

public static class Coreifier {
    internal static void Coreify(string inputAsm, string outputAsm = null) {
        ModuleDefinition module = null;
        try {
            // read the module
            ReaderParameters readerParams = new(ReadingMode.Immediate) { ReadSymbols = true };
            try {
                module = ModuleDefinition.ReadModule(inputAsm, readerParams);
            } catch (SymbolsNotFoundException) {
                readerParams.ReadSymbols = false;
                module = ModuleDefinition.ReadModule(inputAsm, readerParams);
            } catch (SymbolsNotMatchingException) {
                readerParams.ReadSymbols = false;
                module = ModuleDefinition.ReadModule(inputAsm, readerParams);
            }

            // convert the module + write it out
            Coreify(module);
            module.Write(outputAsm ?? inputAsm, new WriterParameters { WriteSymbols = readerParams.ReadSymbols });
        } finally {
            module?.Dispose();
        }
    }

    internal static void Coreify(ModuleDefinition module, bool preventInlining = true) {
        var assembly = Assembly.GetEntryAssembly();
        // set runtime version + clear 32-bit flags
        module.RuntimeVersion = assembly.ImageRuntimeVersion;
        module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);

        // patch target framework attribute + get the mscorlib scope
        IMetadataScope mscorlibScope = null;
        bool isFrameworkModule = false;

        TargetFrameworkAttribute mutatumTargetFrameworkAttr = assembly.GetCustomAttribute<TargetFrameworkAttribute>()
            ?? throw new InvalidOperationException("OpusMutatum must be built to target .NET Core!");
        CustomAttribute lightningTargetFrameworkAttr = module.Assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == typeof(TargetFrameworkAttribute).FullName);
        if (lightningTargetFrameworkAttr is not null) {
            // if the target framework attribute already exists, we know whether it's a framework module
            mscorlibScope = lightningTargetFrameworkAttr.AttributeType.Scope;
            if (((string) lightningTargetFrameworkAttr.ConstructorArguments[0].Value).StartsWith(".NETFramework"))
                isFrameworkModule = true;

            // patch the attribute to target core
            lightningTargetFrameworkAttr.ConstructorArguments[0] = new CustomAttributeArgument(module.TypeSystem.String, mutatumTargetFrameworkAttr.FrameworkName);
            lightningTargetFrameworkAttr.Properties.Clear();
            lightningTargetFrameworkAttr.Properties.Add(new CustomAttributeNamedArgument(nameof(mutatumTargetFrameworkAttr.FrameworkDisplayName), new CustomAttributeArgument(module.TypeSystem.String, mutatumTargetFrameworkAttr.FrameworkDisplayName)));
        } else {
            // else fall back to assembly references
            mscorlibScope = module.AssemblyReferences.FirstOrDefault(asmRef => asmRef.Name == "mscorlib");
            isFrameworkModule = mscorlibScope is not null && module.AssemblyReferences.All(asmRef => asmRef.Name != "System.Runtime");
        }
        // skip coreification if the module is already using core
        if (!isFrameworkModule)
            return;

        // patch debuggable attribute
        DebuggableAttribute mutatumDebuggableAttr = assembly.GetCustomAttribute<DebuggableAttribute>()
            ?? throw new InvalidOperationException("OpusMutatum must be built in the Debug configuration in order to invoke Coreifier!");
        CustomAttribute lightningDebuggableAttr = module.Assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == typeof(DebuggableAttribute).FullName);

        TypeDefinition debuggableAttrType = new TypeReference("System.Diagnostics", "DebuggableAttribute", module, mscorlibScope).Resolve(),
            debuggingModesType = debuggableAttrType.NestedTypes.First(t => t.Name == "DebuggingModes");
        CustomAttributeArgument mutatumDebuggingFlags = new(debuggingModesType, mutatumDebuggableAttr.DebuggingFlags);

        if (lightningDebuggableAttr is not null)
            // if a debuggable attribute already exists, patch the debugging flags
            lightningDebuggableAttr.ConstructorArguments[0] = mutatumDebuggingFlags;
        else {
            // else create one
            MethodReference debuggableAttrCtor = new(".ctor", module.TypeSystem.Void, debuggableAttrType) { HasThis = true };
            debuggableAttrCtor.Parameters.Add(new ParameterDefinition(debuggingModesType));
            lightningDebuggableAttr = new CustomAttribute(module.ImportReference(debuggableAttrCtor));
            lightningDebuggableAttr.ConstructorArguments.Add(mutatumDebuggingFlags);

            module.Assembly.CustomAttributes.Add(lightningDebuggableAttr);
        }

        // relink framework code
        using (FrameworkModder modder = new() {
                    Module = module,
                    MissingDependencyThrow = false,
                    LogVerboseEnabled = false,
                    PreventInlining = preventInlining
                }) {
            modder.MapDependencies();
            modder.AutoPatch();
        }
    }
}
