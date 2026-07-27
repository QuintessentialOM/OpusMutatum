using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpusMutatum;

public static class Patching {
    public static string PathToMonoMod = "MonoMod.Patcher.dll";
    public static string PathToHookGen = "MonoMod.RuntimeDetour.HookGen.dll";
    public static string PathToPatchingDependencies = ".";

    public static string PathToQuintessential = "Quintessential.dll";

    private static bool TryLoadMonoMod(out Assembly monoModAssembly)
        => Globals.TryLoadAssembly(PathToMonoMod, out monoModAssembly);
    private static bool TryLoadHookGen(out Assembly monoModAssembly)
        => Globals.TryLoadAssembly(PathToHookGen, out monoModAssembly);

    public static void RunMonoMod(string asmFrom, string asmTo = null, string[] dllPaths = null) {
        if (!TryLoadMonoMod(out Assembly monoModAssembly)) {
            Console.WriteLine("Unable to load MonoMod, skipping patching!");
            return;
        }

        asmTo ??= asmFrom;
        dllPaths ??= [PathToPatchingDependencies];

        Console.WriteLine($"Running MonoMod for {asmFrom}...");

        string asmTmp = Path.Combine(Globals.PathToTemporaryOutput, Path.GetFileName(asmTo));
        try {
            Environment.SetEnvironmentVariable("MONOMOD_DEPDIRS", PathToPatchingDependencies);
            Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "0");

            int returnCode = (int) monoModAssembly.EntryPoint!.Invoke(null, [Enumerable.Repeat(asmFrom, 1).Concat(dllPaths).Append(asmTmp).ToArray()])!;
            if (returnCode != 0)
                File.Delete(asmTmp);

            if (!File.Exists(asmTmp))
                throw new Exception($"MonoMod failed to create a patched assembly: exit code {returnCode}!");

            File.Move(asmTmp, asmTo);
        } finally {
            File.Delete(asmTmp);
            File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
            File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
        }
    }

    public static void RunHookGen(string asmFrom, string asmTo = null) {
        if (!TryLoadHookGen(out Assembly hookGenAssembly)) {
            Console.WriteLine("Unable to load MonoMod HookGen, skipping patching!");
            return;
        }

        asmTo ??= asmFrom;

        Console.WriteLine($"Running MonoMod HookGen for {asmFrom}...");

        Environment.SetEnvironmentVariable("MONOMOD_DEPDIRS", PathToPatchingDependencies);
        Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "0");

        string mmHookAssemblyName = "MMHOOK_" + Path.ChangeExtension(Path.GetFileName(asmTo), "dll");
        hookGenAssembly.EntryPoint!.Invoke(null, [ new[] { "--private", asmFrom, Path.Combine(Path.GetDirectoryName(asmTo)!, mmHookAssemblyName) }]);
    }
}
