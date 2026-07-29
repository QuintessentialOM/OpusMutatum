using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Coreifier;

public class FrameworkModder : MonoModder {
    private const string LogID = nameof(Coreifier);

    private static readonly HashSet<string> PrivateSystemLibs = ["System.Private.CoreLib"];

    public bool PreventInlining = true;

    private ModuleDefinition coreifierModule;

    public override void Dispose() {
        // don't dispose the main module but dispose the coreifier
        Module = null!;
        coreifierModule?.Dispose();

        base.Dispose();
    }

    public override void Log(string text)
        => Console.WriteLine($"[{LogID}] {text}");
    public override void LogVerbose(string text) { } // so much log spam. disabling it with `LogVerboseEnabled = false` only works on windows?

    public void AddReferenceIfMissing(AssemblyName asmName) {
        if (Module.AssemblyReferences.All(asmRef => asmRef.Name != asmName.Name))
            Module.AssemblyReferences.Add(AssemblyNameReference.Parse(asmName.FullName));
    }
    public void AddReferenceIfMissing(string name)
        => AddReferenceIfMissing(Assembly.GetExecutingAssembly().GetReferencedAssemblies().First(asmName => asmName.Name == name));

    public override void MapDependencies() {
        // ensure critical references are present
        AddReferenceIfMissing("System.Runtime");
        AddReferenceIfMissing(Assembly.GetExecutingAssembly().GetName());

        // we have to load our own module again every time because MonoMod messes with it. weh
        coreifierModule ??= ModuleDefinition.ReadModule(Assembly.GetExecutingAssembly().Location) ?? throw new Exception("Failed to load Coreifier assembly!");
        DependencyCache[Assembly.GetExecutingAssembly().FullName!] = coreifierModule;

        base.MapDependencies();
    }

    public override void AutoPatch() {
        // parse our own patching rules
        ParseRules(DependencyMap[Module].First(dep => dep.Assembly.Name.Name == "Coreifier"));

        base.AutoPatch();
    }

    public override void PatchRefs(ModuleDefinition mod) {
        base.PatchRefs(mod);

        // remove references to private system libraries
        for (int i = 0; i < mod.AssemblyReferences.Count; i++)
            if (PrivateSystemLibs.Contains(mod.AssemblyReferences[i].Name))
                mod.AssemblyReferences.RemoveAt(i--);
    }

    public override IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context) {
        IMetadataTokenProvider relinkedMtp = base.Relinker(mtp, context);

        if (relinkedMtp is TypeReference typeRef && PrivateSystemLibs.Contains(typeRef.Scope.Name))
            // don't reference System.Private.CoreLib directly
            return Module.ImportReference(FindType(typeRef.FullName));

        return relinkedMtp;
    }

    public override void PatchRefsInMethod(MethodDefinition method) {
        base.PatchRefsInMethod(method);

        // resolve uninstantiated generic typeref/def tokens inside of member methods by replacing them with generic type instances
        // CoreCLR seems to be more strict on this, because the faulty IL worked fine on .NET Framework / Mono
        if (method.DeclaringType.HasGenericParameters && method.Body is not null) {
            foreach (Instruction instr in method.Body.Instructions) {
                if (instr.OpCode == OpCodes.Ldtoken)
                    // ldtoken doesn't have the strict metadata checking
                    continue;

                if (instr.Operand is TypeReference typeRef
                    && typeRef.SafeResolve() == method.DeclaringType
                    && !typeRef.IsGenericInstance) {
                    GenericInstanceType typeInst = new(typeRef);
                    typeInst.GenericArguments.AddRange(method.DeclaringType.GenericParameters);
                    instr.Operand = typeInst;
                } else if (instr.Operand is MemberReference memberRef and not TypeReference
                    && memberRef.DeclaringType.SafeResolve() == method.DeclaringType
                    && !memberRef.DeclaringType.IsGenericInstance) {
                    GenericInstanceType typeInst = new(memberRef.DeclaringType);
                    typeInst.GenericArguments.AddRange(method.DeclaringType.GenericParameters);
                    memberRef.DeclaringType = typeInst;
                }
            }
        }
    }
}
