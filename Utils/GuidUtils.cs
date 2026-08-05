using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpusMutatum;
internal class GuidUtils {

    public static bool TryParseMvidFromPath(string path, out Guid mvid) {
        mvid = Guid.Empty;

        string filename = Path.GetFileNameWithoutExtension(path);
        int index = filename.LastIndexOf('_');
        if (index < 0)
            return false;

        string guidString = filename[(index + 1)..];
        return Guid.TryParse(guidString, out mvid);
    }

    public static Guid AsDeterministicGuid(string str) {

        int hash = 17;
        foreach (char c in str) {
            hash = hash * 23 + c.GetHashCode();
        }

        var stringId = new byte[16];
        for (int i = 0; i < 16; i++) {
            stringId[i] = (byte)hash;
            hash = hash * 29 + str[Math.Abs(hash) % str.Length].GetHashCode();
        }
        return new(stringId);
    }

    public static Guid MergeAssemblyMvids(string[] paths) {
        List<byte[]> guidBytes = [];
        foreach (var path in paths) {
            if (File.Exists(path)) {
                using var asmDef = AssemblyDefinition.ReadAssembly(path);
                guidBytes.Add(asmDef.GetMvid().ToByteArray());
            }
        }
        if (guidBytes.Count == 1) return new(guidBytes[0]);

        var idBytes = new byte[16];
        for (int i = 0; i < 16; i++) {
            idBytes[i] = 17;
            for (int j = 0; j < guidBytes.Count; j++) {
                idBytes[i] = (byte)(idBytes[i] * 29 + guidBytes[j][i]);
            }
        }
        return new(idBytes);
    }
    public static bool SameMvidAssemblies(string path1, string path2) {
        // Skipp the use of caching because compared assemblies might change.

        string filename1 = Path.GetFileName(path1);
        string filename2 = Path.GetFileName(path2);
        Console.WriteLine($"Comparing {filename1} & {filename2} assemblies.");

        if (!File.Exists(path1) || !File.Exists(path2)) return false;

        AssemblyDefinition assemblyDef1 = AssemblyDefinition.ReadAssembly(path1);
        AssemblyDefinition assemblyDef2 = AssemblyDefinition.ReadAssembly(path2);

        return assemblyDef1.GetMvid() == assemblyDef2.GetMvid();
    }

    public static bool SameMvidAssemblies(string[] paths, string path) {
        // Skipp the use of caching because compared assemblies might change.

        string filename = Path.GetFileName(path);
        Console.WriteLine($"Comparing multiple paths & {filename} assemblies.");

        if (!File.Exists(path) || paths.Any(path => !File.Exists(path))) return false;

        AssemblyDefinition assemblyDef = AssemblyDefinition.ReadAssembly(path);

        return assemblyDef.GetMvid() == MergeAssemblyMvids(paths);
    }
}
