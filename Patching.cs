using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpusMutatum;

public static class Patching {
    public static string PathToMonoMod = "MonoMod.Patcher.dll";
    public static string PathToPatchingDependencies = "";

    public static string PathToQuintessential = "Quintessential.dll";

    private static bool TryLoadMonoMod(out Assembly monoModAssembly)
        => Globals.TryLoadAssembly(PathToMonoMod, out monoModAssembly);

    public static void RunMonoMod(string asmFrom, string asmTo = null, string[] dllPaths = null) {
        if (!TryLoadMonoMod(out Assembly monoModAssembly)) {
            Console.WriteLine("Unable to load MonoMod, skipping patching!");
            return;
        }

        asmTo ??= asmFrom;
        dllPaths ??= [];

        Console.WriteLine($"Running MonoMod for {asmFrom}...");

        string asmTmp = Path.Combine(Globals.PathToTemporaryOutput, Path.GetFileName(asmTo));
        try {
            Environment.SetEnvironmentVariable("MONOMOD_DEPDIRS", PathToPatchingDependencies);
            Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "0");

            string[] args = Enumerable.Repeat(asmFrom, 1).Concat(dllPaths).Append(asmTmp).ToArray();
            int returnCode = (int) monoModAssembly.EntryPoint!.Invoke(null, [args])!;
            if (returnCode != 0)
                File.Delete(asmTmp);

            if (!File.Exists(asmTmp))
                throw new Exception($"MonoMod failed to create a patched assembly: exit code {returnCode}!");
            File.Move(asmTmp, asmTo, overwrite: true);
        } finally {
            File.Delete(asmTmp);
            File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
            File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
        }
    }
}
