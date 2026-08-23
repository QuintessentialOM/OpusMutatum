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
    public static OrderedDictionary<string, string> DllPaths { private set; get; } = [];
    public static bool IsCompleted { private set; get; } = false;
    private static bool ModsCollected = false;

    public static void LoadMods() {
        if (IsCompleted) return;

        Log("Starting mod loading...");
        FindMods();
        List<ModMeta> mods = Mods;
        if (mods.Count == 0) {
            IsCompleted = true;
            Log($"Found no mods.");
            return;
        }
        Log("Loading mods...");
        Log("Stage 1: Verifying mod dependencies");
        VerifyDependencies(mods);
        Log("Stage 2: Resolving the dependency tree");
        OrderMods(ref mods);
        Log("Stage 3: Organising");
        ExtractArchives(mods);
        DllPaths = CollectDlls(mods);
        Mods = mods;

        LogModList(mods);
        IsCompleted = true;
        CreateDataFile();
    }
    public static OrderedDictionary<string, string> GetDevMods(string developedModId, List<string> forceLoadIds, bool logModList = false) {
        if (IsCompleted) throw new Exception("GetDevMods() must be called before LoadMods()");

        Log("Starting dev mod loading...");
        FindMods();
        List<ModMeta> devMods = Mods.FindAll(mod => forceLoadIds.Contains(mod.ModId));
        if (devMods.Count != forceLoadIds.Count) throw new Exception("Failed to find mods for all provided dev ids.");

        // Add all dependencies
        for (int i = 0; i < devMods.Count; i++) {
            foreach (var dependency in devMods[i].Dependencies) {
                if (!devMods.Any(mod => mod.ModId == dependency.Key)) {
                    try {
                        devMods.Add(Mods.Where(mod => mod.ModId == dependency.Key).Single());
                    } catch (Exception e) { throw new Exception("Failed to find dependency of dev mods: " + dependency.Key, e); }
                }
            }
        }
        devMods.RemoveAll(mod => mod.ModId == developedModId); // We do not want to depend on ourselves

        VerifyDependencies(devMods);
        OrderMods(ref devMods);
        ExtractArchives(devMods);
        var dlls = CollectDlls(devMods);
        if (logModList) LogModList(devMods);
        return dlls;
    }

    private static void FindMods() {
        if (ModsCollected) return;
        ModsCollected = true;

        Log("Finding mods to load...");
        if (!Directory.Exists(PathToMods))
            Directory.CreateDirectory(PathToMods);

        if (Directory.Exists(PathToUnpackedMods))    // TODO find a way to cache this
            Directory.Delete(PathToUnpackedMods, true);
        Directory.CreateDirectory(PathToUnpackedMods);


        List<string> blacklisted = [];
        PathToBlacklist = Path.Combine(PathToMods, "blacklist.txt");
        if (File.Exists(PathToBlacklist))
            blacklisted = File.ReadAllLines(PathToBlacklist).Select(l => (l.StartsWith("#") ? "" : l).Trim()).ToList();
        else
            File.WriteAllText(PathToBlacklist, "# This is the blacklist. Lines starting with # are ignored.\nExampleFolderThatIWantToBlacklist\nSomeZipIDontLike.zip");

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

        HashSet<string> ids = [];
        foreach (ModMeta mod in Mods) {
            if (ids.Contains(mod.ModId)) {
                throw new Exception("Duplicate mod wiht id " + mod.ModId + " found, use the blacklist to only permit at most one.");
            }
            ids.Add(mod.ModId);
        }
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

    private static void VerifyDependencies(List<ModMeta> mods) {
        Dictionary<string, List<Tuple<string, Version, VersionRange>>> invalidDependencies = [];

        foreach (ModMeta mod in mods) {
            foreach (var dependency in mod.Dependencies) {
                var presentDependency = mods.Where(mod => mod.ModId == dependency.Key).SingleOrNull();

                if (presentDependency == null) {
                    if (!invalidDependencies.TryGetValue(mod.ModId, out _)) invalidDependencies[mod.ModId] = [];
                    invalidDependencies[mod.ModId].Add(new(dependency.Key, null, dependency.Value));

                } else if (!dependency.Value.Contains(presentDependency.Version)) {
                    if (!invalidDependencies.TryGetValue(mod.ModId, out _)) invalidDependencies[mod.ModId] = [];
                    invalidDependencies[mod.ModId].Add(new(dependency.Key, presentDependency.Version, dependency.Value));

                }
            }
            foreach (var incompatibility in mod.Conflicts) {
                var invalidVal = mods.Where(mod => mod.ModId == incompatibility).SingleOrNull();
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
    }
    private static void OrderMods(ref List<ModMeta> mods) {
        List<ModMeta> orderdMods = [];
        while (mods.Count > 0) {

            bool foundNew = false;
            for (int i = 0; i < mods.Count; i++) {
                if (mods[i].Dependencies.All(dep => Contains(dep.Key, orderdMods))) {
                    orderdMods.Add(mods[i]);
                    mods.RemoveAt(i);
                    i--;
                    foundNew = true;
                }
            }

            if (!foundNew) {
                LogDependencyCycle(mods);
                throw new Exception("Failed to resolve the dependency tree.");
            }
        }
        mods = orderdMods;
    }
    private static void LogDependencyCycle(List<ModMeta> mods) {

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
        int maxIdLength = mods.MaxBy(mod => mod.ModId.Length).ModId.Length;
        foreach (var mod in mods) {
            StringBuilder modMessage = new(mod.ModId + new string(' ', maxIdLength - mod.ModId.Length) + " - ");
            foreach (var dep in mod.Dependencies) {
                if (Contains(dep.Key, mods)) modMessage.Append(dep.Key + ", ");
            }
            Log(modMessage.ToString());
        }
    }

    private static void ExtractArchives(List<ModMeta> mods) {
        foreach (var mod in mods) { // Could we avoid this somehow
            if (mod.PathToDirectory == null && mod.PathToArchive != null) {
                string path = Path.Combine(PathToUnpackedMods, Path.GetFileNameWithoutExtension(mod.PathToArchive));
                ZipFile.ExtractToDirectory(mod.PathToArchive, path);
                mod.PathToDirectory = path;
            }
        }
    }
    private static OrderedDictionary<string, string> CollectDlls(List<ModMeta> mods) {
        OrderedDictionary<string, string> dlls = [];
        foreach (var mod in mods) {
            if (mod.DLL != "") {
                if (mod.PathToDirectory != null) {
                    string dllPath = Path.Combine(mod.PathToDirectory, mod.DLL);
                    if (File.Exists(dllPath) && Path.GetExtension(dllPath) == ".dll") {
                        mod.HasDll = true;
                        dlls.Add(mod.ModId, dllPath);
                    }
                }
            }
        }
        return dlls;
    }
    private static void LogModList(List<ModMeta> mods) {
        int maxIdLength = mods.MaxBy(mod => mod.ModId.Length)?.ModId.Length ?? 0;
        Log($"Finished mod loading - {mods.Count} mods loaded; {DllPaths.Count} assemblies");

        foreach (var mod in mods) {
            Log(mod.ModId + new string(' ', maxIdLength - mod.ModId.Length) + " - [" + (mod.HasDll ? "#" : " ") + "] - " + mod.Version.ToString());
        }
    }

    private static bool Contains(string depId, List<ModMeta> list) => list.Any(m => m.ModId == depId);
    private static bool HasDependent(string modId, List<ModMeta> list) => list.Any(m => m.Dependencies.Any(dep => dep.Key == modId));
    private static void Log(string message) => Console.WriteLine("[ModLoader] " + message);

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
