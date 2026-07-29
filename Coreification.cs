using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpusMutatum;

public static class Coreification {
    public static string PathToCoreifier = "Coreifier.dll";

    private static bool TryLoadCoreifier(out MethodInfo coreifierEntryPoint) {
        coreifierEntryPoint = null;

        if (!Globals.TryLoadAssembly(PathToCoreifier, out Assembly coreifierAssembly))
            return false;

        coreifierEntryPoint = coreifierAssembly?
            .GetType("Coreifier.Coreifier")?
            .GetMethod("Coreify", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string)], null);
        if (coreifierEntryPoint is null) {
            Console.WriteLine("Failed to find coreifier entrypoint.");
            return false;
        }

        Console.WriteLine("Found coreifier entrypoint.");
        return true;
    }

    public static void Coreify(string asmFrom, string asmTo = null, HashSet<string> convertedAsms = null) {
        asmTo ??= asmFrom;
        convertedAsms ??= [];
        if (!File.Exists(asmFrom)) {
            Console.WriteLine($"Unable to load assembly {asmFrom}, skipping coreification!");
            return;
        }

        if (!convertedAsms.Add(asmFrom))
            return;

        string[] deps = DependencyHandling.GetAssemblyReferences(asmFrom).Keys.ToArray();
        if (Globals.OperatingSystem != Globals.OS.Windows && deps.Contains("Coreifier"))
            // if the assembly is already coreified, skip it
            return;

        // coreify dependencies first
        foreach (string dep in deps) {
            string srcDepPath = Path.Combine(Path.GetDirectoryName(asmFrom)!, $"{dep}.dll");
            string dstDepPath = Path.Combine(Path.GetDirectoryName(asmTo)!, $"{dep}.dll");

            // recursively handle transitive dependencies + only coreify non-system ones
            if (File.Exists(srcDepPath) && !DependencyHandling.IsSystemLib(srcDepPath))
                Coreify(srcDepPath, dstDepPath, convertedAsms);
            else if (File.Exists(dstDepPath) && !DependencyHandling.IsSystemLib(dstDepPath))
                Coreify(dstDepPath, convertedAsms: convertedAsms);
        }

        // coreify the assembly
        CoreifySingle(asmFrom, asmTo);
    }

    private static void CoreifySingle(string asmFrom, string asmTo) {
        if (!TryLoadCoreifier(out MethodInfo coreifierEntryPoint)) {
            Console.WriteLine("Unable to load coreifier, skipping coreification!");
            return;
        }

        Console.WriteLine($"Converting {asmFrom} to .NET Core...");

        string asmTmp = Path.Combine(Globals.PathToTemporaryOutput, Path.GetFileName(asmTo));
        try {
            // coreify the assembly to a temporary directory first, then move it to the destination
            coreifierEntryPoint.Invoke(null, [asmFrom, asmTmp]);
            File.Move(asmTmp, asmTo, overwrite: true);
        } finally {
            // delete temporary files
            File.Delete(asmTmp);
            File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
            File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
        }
    }
}
