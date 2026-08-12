using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace OpusMutatum;


static public class ModLoader {
    private const string modMetaFileName = "modMeta.jsonc";
    private const string validModIdChars = "abcdefghijklmnopqrstuvwxyz_0123456789";

    public static string PathToMods;
    public static string PathToUnpackedMods;
    private static string PathToBlacklist;
    public static List<ModMeta> Mods { private set; get; } = [];
    public static List<string> DllPaths { private set; get; } = [];
    public static bool IsCompleted { private set; get; } = false;

    private static void Log(string message) {
        Console.WriteLine("[ModLoader] " + message);
    }

    public static void LoadMods() {
        if (IsCompleted) return;

        Log("Starting mod loading...");
        if (!Directory.Exists(PathToMods))
            Directory.CreateDirectory(PathToMods);

        if (Directory.Exists(PathToUnpackedMods))    // TODO find a way to cache this
            Directory.Delete(PathToUnpackedMods,true);
        Directory.CreateDirectory(PathToUnpackedMods);


        List<string> blacklisted = [];
        PathToBlacklist = Path.Combine(PathToMods, "blacklist.txt");
        if (File.Exists(PathToBlacklist))
            blacklisted = File.ReadAllLines(PathToBlacklist).Select(l => (l.StartsWith("#") ? "" : l).Trim()).ToList();
        else
            File.WriteAllText(PathToBlacklist, "# This is the blacklist. Lines starting with # are ignored.\nExampleFolderThatIWantToBlacklist\nSomeZipIDontLike.zip");

        Log("Finding mods to load...");

        // Finding zip mods
        string[] files = Directory.GetFiles(PathToMods);
        foreach (var file in files) {
            string filename = Path.GetFileName(file);
            if (blacklisted.Contains(filename))
                continue;
            if (filename.EndsWith(".zip") && File.Exists(file))
                FindModInArchive(file);
        }

        // Find folder mods
        string[] folders = Directory.GetDirectories(PathToMods);
        foreach (var folder in folders) {
            string filename = Path.GetFileName(folder);
            if (blacklisted.Contains(filename))
                continue;
            FindModsInFolder(folder, allowSubDirectories: true);
        }
        if (Mods.Count == 0) {
            IsCompleted = true;
            Log($"Found no mods.");
            return;
        }

        // Load mods
        Log("Loading mods...");
        HashSet<string> ids = [];
        foreach (ModMeta mod in Mods) {
            if (ids.Contains(mod.ModId)) {
                throw new Exception("Duplicate mod wiht id " + mod.ModId + " found, use the blacklist to only permit at most one.");
            }
            ids.Add(mod.ModId);
        }

        Dictionary<string, List<Tuple<string, Version, VersionRange>>> invalidDependencies = [];

        // TODO: something better than O(n^2) ?
        Log("Stage 1: Verifying mod dependencies");
        foreach (ModMeta mod in Mods) {
            foreach (var dependency in mod.Dependencies) {
                var presentDependency = Mods.Where(mod => mod.ModId == dependency.Key).SingleOrNull();

                if (presentDependency == null) {
                    if (!invalidDependencies.TryGetValue(mod.ModId, out _)) invalidDependencies[mod.ModId] = [];
                    invalidDependencies[mod.ModId].Add(new(dependency.Key, null, dependency.Value));

                } else if (!dependency.Value.Contains(presentDependency.Version)) {
                    if (!invalidDependencies.TryGetValue(mod.ModId, out _)) invalidDependencies[mod.ModId] = [];
                    invalidDependencies[mod.ModId].Add(new(dependency.Key, presentDependency.Version, dependency.Value));

                }
            }
            foreach (var incompatibility in mod.Conflicts) {
                var invalidVal = Mods.Where(mod => mod.ModId == incompatibility).SingleOrNull();
                if (invalidVal != null) {
                    if (!invalidDependencies.TryGetValue(mod.ModId, out _)) invalidDependencies[mod.ModId] = [];
                    invalidDependencies[mod.ModId].Add(new(incompatibility, null, null));

                }
            }
        }
        if (invalidDependencies.Count != 0) {
            Log("Found invalid dependencies, failed to load!");
            Console.WriteLine();
            foreach (var item in invalidDependencies) {
                Log($"Mod \"{item.Key}\" has incorrect dependencies:");
                foreach (var dependencyIssue in item.Value) {
                    if (dependencyIssue.Item3 == null) {
                        Log($"-- Incompatibility with \"{dependencyIssue.Item1}\"");
                    } else if (dependencyIssue.Item2 == null) {
                        Log($"-- Missing \"{dependencyIssue.Item1}\" in range {dependencyIssue.Item3}");
                    } else {
                        Log($"-- Version of \"{dependencyIssue.Item1}\" not in range {dependencyIssue.Item3}, actual version {dependencyIssue.Item2}");
                    }
                }
                Console.WriteLine();
            }
            throw new Exception("Found invalid dependencies, failed to load!");
        }

        List<ModMeta> orderdMods = [];

        Log("Stage 2: Resolving the dependency tree");
        while (Mods.Count > 0) {

            bool foundNew = false;
            for (int i = 0; i < Mods.Count; i++) {
                if (Mods[i].Dependencies.All(dep => Contains(dep.Key, orderdMods))) {
                    orderdMods.Add(Mods[i]);
                    Mods.RemoveAt(i);
                    i--;
                    foundNew = true;
                }
            }

            if (!foundNew) {
                DiscoverDependencyCycle(Mods);
                throw new Exception("Failed to resolve the dependency tree.");
            }
        }
        Mods = orderdMods;

        // Extract Zip files
        foreach (var mod in Mods) { // Could we avoid this somehow
            if (mod.PathToArchive != null) {
                string path = Path.Combine(PathToUnpackedMods, Path.GetFileNameWithoutExtension(mod.PathToArchive));
                ZipFile.ExtractToDirectory(mod.PathToArchive, path);
                mod.PathToDirectory = path;
            }
        }

        // Collect dlls
        foreach (var mod in Mods) {
            if (mod.DLL != "") {
                if (mod.PathToDirectory != null) {
                    string dllPath = Path.Combine(mod.PathToDirectory, mod.DLL);
                    if (File.Exists(dllPath) && Path.GetExtension(dllPath) == ".dll") {
                        mod.HasDll = true;
                        DllPaths.Add(dllPath);
                    }
                }
            }
        }
        IsCompleted = true;

        int maxIdLength = Mods.MaxBy(mod => mod.ModId.Length).ModId.Length;
        foreach (var mod in Mods) {
            Log(mod.ModId + new string(' ', maxIdLength-mod.ModId.Length) +  " - [" + (mod.HasDll ? "#" : " ") +"] - " + mod.Version.ToString());
        }
        Log($"Finished mod loading - {Mods.Count} mods loaded; {DllPaths.Count} assemblies");

        CreateDataFile();
    }

    private static void FindModsInFolder(string dir, bool allowSubDirectories = false) {

        // Look for a mod meta
        ModMeta meta;
        string metaPath = Path.Combine(dir, modMetaFileName);
        if (File.Exists(metaPath)) {
            using StreamReader reader = new(metaPath);

            try {
                meta = DataSerializer.Deserialize<ModMeta>(metaPath);
                meta.PathToDirectory = dir;
                meta.PathToArchive = null;

                if (!meta.ModId.All(ch => validModIdChars.Contains(ch)))
                    throw new Exception($"Failed parsing {modMetaFileName} in {dir}, invalid mod id: '{meta.ModId}'.");
                Mods.Add(meta);
                Log($"Queuing mod \"{meta.ModId}\", version {meta.Version}.");
            } catch (Exception e) {
                throw new Exception($"Failed parsing {modMetaFileName} in {dir}", e);
            }
        } else if (allowSubDirectories) {

            string[] folders = Directory.GetDirectories(dir);
            foreach (var folder in folders) {
                FindModsInFolder(folder);
            }

            string[] files = Directory.GetFiles(PathToMods);
            foreach (var file in files) {
                string filename = Path.GetFileName(file);
                if (filename.EndsWith(".zip") && File.Exists(filename))
                    FindModInArchive(file);
            }
        }
    }
    private static void FindModInArchive(string archivePath) {
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Read);
        var modMetaEntry = archive.Entries.FirstOrDefault(entry => entry.Name == modMetaFileName, null);

        if (modMetaEntry != null) {
            var modMeta = modMetaEntry.Open();
            ModMeta meta = DataSerializer.Deserialize<ModMeta>(modMeta, Path.Combine(archivePath[..^4], modMetaFileName));
            meta.PathToDirectory = null;
            meta.PathToArchive = archivePath;

            if (!meta.ModId.All(ch => validModIdChars.Contains(ch)))
                throw new Exception($"Failed parsing {modMetaFileName} in {archivePath}, invalid mod id: '{meta.ModId}'.");
            Mods.Add(meta);
            Log($"Queuing mod \"{meta.ModId}\", version {meta.Version}.");
        }
    }

    private static void DiscoverDependencyCycle(List<ModMeta> mods) {

        // Remove mods where no other mod depends on them
        bool foundIndependent;
        do {
            foundIndependent = false;
            for (int i = 0; i < mods.Count; i++) {
                if (!HasDependent(mods[i].ModId, mods)) {
                    foundIndependent = true;
                    mods.RemoveAt(i);
                    i--;
                }
            }
        } while (foundIndependent);
        if (mods.Count == 1) throw new Exception("Failed to find dependency cycles");

        // Log mods & dependencies
        Log("Failed to resolve the dependency tree, possible cycle in the following mods:");
        int maxIdLength = Mods.MaxBy(mod => mod.ModId.Length).ModId.Length;
        foreach (var mod in Mods) {
            StringBuilder modMessage = new(mod.ModId + new string(' ', maxIdLength - mod.ModId.Length) + " - ");
            foreach (var dep in mod.Dependencies) {
                if (Contains(dep.Key, Mods)) modMessage.Append(dep.Key + ", ");
            }
            Log(modMessage.ToString());
        }
    }

    private static bool Contains(string depId, List<ModMeta> list) => list.Any(m => m.ModId == depId);
    private static bool HasDependent(string modId, List<ModMeta> list) => list.Any(m => m.Dependencies.Any(dep => dep.Key == modId));

    private static void CreateDataFile() {
        if (!IsCompleted) throw new Exception("LoadMods() must be called before CreateDataFile(), mods are not yet loaded.");

        OrderedDictionary<string, string> modLoaderData = new();
        for (int i = 0; i < Mods.Count; i++) {
            string modPath = Mods[i].PathToDirectory ?? Mods[i].PathToArchive;
            modLoaderData.Add(Mods[i].ModId, Path.IsPathRooted(modPath) ? modPath : Path.GetRelativePath(Globals.PathToOutput, modPath) );
        }

        DataSerializer.SetMultilineFormat(true);
        modLoaderData.Serialize(Path.Combine(PathToUnpackedMods, "modLoaderData.json"));
    }
}
