using Mono.Cecil;
using System;
using System.IO;
using System.Linq;

namespace OpusMutatum.Merging;

public static class Patching {
    public static string PathToQuintessential = "Quintessential.dll";
    public static string PathToPatchingDependencies = "";


    public static void RunMerge(string asmFrom, string asmTo = null, string[] dllPaths = null, bool mergeDllMvids = false) {

        asmTo ??= asmFrom;
        dllPaths ??= [];

        Console.WriteLine($"Running MonoMod for {asmFrom}...");

        string asmTmp = Path.Combine(Globals.PathToTemporaryOutput, Path.GetFileName(asmTo));
        try {
            Environment.SetEnvironmentVariable("MONOMOD_DEPDIRS", PathToPatchingDependencies);
            Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "0");

            RunMergeModder(asmFrom, asmTmp, dllPaths);

            if (mergeDllMvids) {
                string asmTmp2 = Path.Combine(Globals.PathToTemporaryOutput, "2_" + Path.GetFileName(asmTo));
                using var def = AssemblyDefinition.ReadAssembly(asmTmp);
                string[] assembliesMerged = dllPaths.Append(asmFrom).ToArray();
                def.MainModule.Mvid = GuidUtils.MergeAssemblyMvids(assembliesMerged);
                def.Write(asmTmp2);
                File.Move(asmTmp2, asmTo, overwrite: true);
            } else
                File.Move(asmTmp, asmTo, overwrite: true);
        } finally {
            File.Delete(asmTmp);
            File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
            File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
        }
    }
    public static void RunMergeModder(string asmFrom, string asmTo, string[] dllPaths = null) {
        try {

            using (MergeModder modder = new() {
                InputPath = asmFrom,
                OutputPath = asmTo,
                MissingDependencyThrow = false,
                LogVerboseEnabled = false
            }) {
                modder.Read();
                foreach (var mod in dllPaths)
                    modder.ReadMod(mod);

                modder.MapDependencies();
                modder.Module.PatchTargetArchitecture();
                modder.Log("[Main] Begin patching.");
                modder.PrePatchAssembly();
                modder.AutoPatch();
                modder.Write(null, null);
                modder.Log("[Main] Done.");
            }
        } catch {
            if (File.Exists(asmTo) && asmTo != asmFrom) File.Delete(asmTo);
            throw;
        }
    }

    public static void PatchTargetArchitecture(this ModuleDefinition module) {
        var moduleArch = module.Architecture;
        switch (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) {

            case System.Runtime.InteropServices.Architecture.Arm:
                if (moduleArch != TargetArchitecture.ARM || moduleArch != TargetArchitecture.ARMv7)
                    module.Architecture = TargetArchitecture.ARM;
                return;

            case System.Runtime.InteropServices.Architecture.Arm64:
                module.Architecture = TargetArchitecture.ARM64;
                return;

            case System.Runtime.InteropServices.Architecture.Armv6:
                if (moduleArch != TargetArchitecture.ARM || moduleArch != TargetArchitecture.ARMv7)
                    module.Architecture = TargetArchitecture.ARM;
                return;

            case System.Runtime.InteropServices.Architecture.X64:
            case System.Runtime.InteropServices.Architecture.X86:   // What should this be ???
                module.Architecture = TargetArchitecture.AMD64;
                return;

            case System.Runtime.InteropServices.Architecture.LoongArch64:
            case System.Runtime.InteropServices.Architecture.Ppc64le:
            case System.Runtime.InteropServices.Architecture.RiscV64:
            case System.Runtime.InteropServices.Architecture.S390x:
            case System.Runtime.InteropServices.Architecture.Wasm:
                return;
        }
    }
}
