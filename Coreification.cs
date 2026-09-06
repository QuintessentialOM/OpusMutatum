using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpusMutatum;

public static class Coreification {

    public static void Coreify(string asmFrom, string asmTo = null, HashSet<string> convertedAsms = null) {
        asmTo ??= asmFrom;
        convertedAsms ??= [];
        if (!File.Exists(asmFrom)) {
            Console.WriteLine($"Unable to load assembly {asmFrom}, skipping coreification!");
            return;
        }

        if (!convertedAsms.Add(asmFrom))
            return;

        string[] deps = [.. DependencyHandling.GetAssemblyReferences(asmFrom).Keys];

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
        Console.WriteLine($"Converting {asmFrom} to .NET Core...");

        string asmTmp = Path.Combine(Globals.PathToTemporaryOutput, Path.GetFileName(asmTo));
        try {
            // coreify the assembly to a temporary directory first, then move it to the destination
            Coreifier.Coreifier.Coreify(asmFrom, asmTmp);
            File.Move(asmTmp, asmTo, overwrite: true);
        } finally {
            // delete temporary files
            File.Delete(asmTmp);
            File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
            File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
        }
    }
}
