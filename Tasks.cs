using Mono.Cecil;
using OpusMutatum.Merging;
using System;
using System.IO;

namespace OpusMutatum;
public static class Tasks {
    public static void HandleStrings(string[] args) {
        bool onlyOnChange = false;
        foreach (var item in args) {
            switch (item) {
                case "--onlyOnChange":
                    onlyOnChange = true;
                    break;
                default:
                    Console.WriteLine($"Invalid Argument '{item}' for 'strings' task.");
                    break;
            }
        }

        string stringDumpingDir = Path.Combine(Globals.PathToOutput, StringDumping.PathToStringDumping);
        string stringDumperPath = Path.Combine(stringDumpingDir, "StringDumper_Lightning.exe");
        Directory.CreateDirectory(stringDumpingDir);

        if (onlyOnChange && GuidUtils.SameMvidAssemblies(Globals.PathToLightningExe, stringDumperPath)) {
            Console.WriteLine("Found cache, skipping string dumping.");
            return;
        }
        if (!Globals.TryLoadLightningExe(out AssemblyDefinition lightningExe))
            return;

        Console.WriteLine("Dumping strings...");
        Console.WriteLine("Creating string dumper...");
        StringDumping.CreateStringDumper(lightningExe, stringDumperPath);

        Console.WriteLine("Running string dumper...");
        StringDumping.EnsureDependenciesPresent(stringDumpingDir);
        StringDumping.RunStringDumperAndAddPath(stringDumperPath);

        Console.WriteLine();
    }

    public static void HandleIntermediarySteps(string[] args) {
        bool onlyOnChange = false;
        foreach (var item in args) {
            switch (item) {
                case "--onlyOnChange":
                    onlyOnChange = true;
                    break;
                default:
                    Console.WriteLine($"Invalid Argument '{item}' for 'intermediary' task.");
                    break;
            }
        }

        HandleCoreify(onlyOnChange);
        HandleDependencies();

        HandleIntermediary(onlyOnChange);
        Console.WriteLine();
        HandleQuintDevExe(onlyOnChange);
    }

    private static void HandleCoreify(bool onlyOnChange) {
        if (!File.Exists(Globals.PathToLightningExe)) {
            Console.WriteLine("Failed to find Lightning.exe!");
            return;
        }
        if (onlyOnChange && GuidUtils.SameMvidAssemblies(Globals.PathToLightningExe, Path.Combine(Globals.PathToOutput, Globals.PathToLightning))) {
            Console.WriteLine("Found cache, skipping coreification.");
            return;
        }

        Console.WriteLine("Coreifying Lightning.exe...");
        Coreification.Coreify(Globals.PathToLightningExe, Path.Combine(Globals.PathToOutput, Globals.PathToLightning));

        Console.WriteLine();
    }

    private static void HandleDependencies() {
        Console.WriteLine("Setting up native libraries...");
        DependencyHandling.SetupNativeLibs();

        Console.WriteLine("Creating symlinks...");
        ContentHandling.CreateContentSymlinks();

        Console.WriteLine();
    } // TODO: add caching

    private static void HandleIntermediary(bool onlyOnChange) {
        // TODO: MonoMod relinking?
        if (onlyOnChange && GuidUtils.SameMvidAssemblies(Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning), Path.Combine(Globals.PathToOutput, Globals.PathToLightning))) {
            Console.WriteLine("Found cache, skipping intermediary assembly generation.");
            return;
        }
        if (!Globals.TryLoadLightning(out AssemblyDefinition lightning))
            return;

        Console.WriteLine("Generating intermediary assembly...");
        Remapping.RemapToIntermediary(lightning);

        lightning.Write(Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning));
        Console.WriteLine();
    }

    public static void HandleMerge(string[] args) {
        bool onlyOnChange = false;
        bool asNamed = false;
        foreach (var item in args) {
            switch (item) {
                case "--onlyOnChange":
                    onlyOnChange = true;
                    break;
                case "--asNamed":
                    asNamed = true;
                    break;
                default:
                    Console.WriteLine($"Invalid Argument '{item}' for 'merge' task.");
                    break;
            }
        }


        string quintessentialPath = Path.Combine(Globals.PathToOutput, Patching.PathToQuintessential);
        string quintessentialFilename = Path.GetFileName(quintessentialPath);
        string intermediaryLightningPath = Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning);
        string moddedLightningPath = Path.Combine(Globals.PathToOutput, Globals.PathToModdedLightning);
        string quintDevLightningPath = Path.Combine(Globals.PathToOutput, Globals.PathToQuintDevLightning);

        if (!File.Exists(quintessentialPath)) {
            Console.WriteLine($"Failed to find {quintessentialFilename}, skipping merge!");
            return;
        }
        string[] assembliesMerged = [quintessentialPath, asNamed ? quintDevLightningPath : intermediaryLightningPath];
        if (onlyOnChange && GuidUtils.SameMvidAssemblies(assembliesMerged, moddedLightningPath)) {
            Console.WriteLine("Found cache, skipping modded assembly generation.");
            return;
        }

        Console.WriteLine($"Merging {quintessentialFilename}...");
        Patching.RunMerge(asNamed ? quintDevLightningPath : intermediaryLightningPath, moddedLightningPath, dllPaths: [quintessentialPath], true);

        Console.WriteLine();
    }

    public static void HandleDevExe() {
        // take ModdedLightning.exe, remap to named
        if (!Globals.TryLoadModdedLightning(out AssemblyDefinition moddedLightning))
            return;

        Console.WriteLine("Generating development assembly...");
        Remapping.RemapToNamed(moddedLightning);

        moddedLightning.Write(Path.Combine(Globals.PathToOutput, "DevLightning.dll"));
        Console.WriteLine();
    }

    public static void HandleQuintDevExe(bool onlyOnChange) {
        // take IntermediaryLightning.exe, remap to named (no merged quintessential)
        if (onlyOnChange && GuidUtils.SameMvidAssemblies(Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning), Path.Combine(Globals.PathToOutput, Globals.PathToQuintDevLightning))) {
            Console.WriteLine("Found cache, skipping Quintessential development assembly generation.");
            return;
        }
        if (!Globals.TryLoadIntermediaryLightning(out AssemblyDefinition intermediaryLightning))
            return;

        Console.WriteLine("Generating Quintessential development assembly...");
        Remapping.RemapToNamed(intermediaryLightning);

        intermediaryLightning.Write(Path.Combine(Globals.PathToOutput, Globals.PathToQuintDevLightning));
        Console.WriteLine();
    }

    public static void HandleRun(string[] args) {
        bool inputArgs = false;
        RunDebugger gameDebugger = new();
        foreach (var item in args) {
            if (inputArgs) {
                inputArgs = false;
                gameDebugger.runArgs = item.Trim(['"']).Split([' ']);
            } else {
                switch (item) {
                    case "-args":
                        inputArgs = true;
                        break;
                    case "--attachDebugger":
                        gameDebugger.attachDebugger = true;
                        break;
                    case "--readLogs":
                        gameDebugger.readLogs = true;
                        break;
                    default:
                        Console.WriteLine($"Invalid Argument '{item}' for 'run' task.");
                        break;
                }
            }
        }

        string pathToIntermediary = Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning);
        string pathToModded = Path.Combine(Globals.PathToOutput, Globals.PathToModdedLightning);

        string target = File.Exists(pathToModded)
            ? pathToModded
            : File.Exists(pathToIntermediary)
                ? pathToIntermediary
                : null;
        if (target is null) {
            Console.WriteLine("Failed to find target to run!");
            return;
        }

        Console.WriteLine($"Running {Path.GetFileName(target)}...");

        DependencyHandling.SetupNativeLibLoading();
        AppHosting.RunAssembly(target, gameDebugger.runArgs, debugger: gameDebugger);
    }

    public static void HandleCopy(string[] args) {
        string pathFrom = "";
        string pathTo = "";
        bool asFrom = false, asTo = false, asToMod = false;
        foreach (var item in args) {
            if (asFrom) {
                asFrom = false;
                pathFrom = item.Trim(['"']);
            } else if (asTo) {
                asTo = false;
                pathTo = item.Trim(['"']);
            } else if (asToMod) {
                asToMod = false;
                pathTo = Path.Combine(Globals.Tasks.ModsDir, item.Trim(['"']));
            } else {
                switch (item) {
                    case "-from":
                        asFrom = true;
                        break;
                    case "-to":
                        asTo = true;
                        break;
                    case "-toMod":
                        asToMod = true;
                        break;
                    default:
                        Console.WriteLine($"Invalid Argument '{item}' for 'copy' task.");
                        break;
                }
            }
        }
        if (pathFrom == "" || pathTo == "") {
            Console.WriteLine("Both -to and -from have to be specified for the 'copy' task.");
            return;
        }
        if (!Directory.Exists(pathTo)) {
            Directory.CreateDirectory(pathTo);
        }

        if (Directory.Exists(pathFrom)) {
            string pathWDir = Path.Combine(pathTo, Path.GetFileName(pathFrom));
            Console.WriteLine($"Copying directory: {pathFrom}");
            var allDirectories = Directory.GetDirectories(pathFrom, "*", SearchOption.AllDirectories);

            foreach (string dir in allDirectories) {
                string dirToCreate = dir.Replace(pathFrom, pathWDir);
                Directory.CreateDirectory(dirToCreate);
            }
            var allFiles = Directory.GetFiles(pathFrom, "*.*", SearchOption.AllDirectories);
            foreach (string filePath in allFiles) {
                File.Copy(filePath, filePath.Replace(pathFrom, pathWDir), true);
            }
        } else if (File.Exists(pathFrom)) {
            Console.WriteLine($"Copying file: {pathFrom}");
            File.Copy(pathFrom, Path.Combine(pathTo, Path.GetFileName(pathFrom)), overwrite: true);
        } else {
            Console.WriteLine($"Found nothing at '{pathFrom}' can't copy.");
        }
    }

    public static void HandleNewMod(string[] args) {
        string name = "";
        bool asName = false;
        foreach (var item in args) {
            if (asName) {
                asName = false;
                name = item.Trim(['"']);
            } else {
                switch (item) {
                    case "-name":
                        asName = true;
                        break;
                    default:
                        Console.WriteLine($"Invalid Argument '{item}' for 'newMod' task.");
                        break;
                }
            }
        }
        if (name == "") {
            Console.WriteLine("Mod name has to be specified with -name for the 'newMod' task.");
            return;
        }

        string path = Path.Combine(Globals.Tasks.ModsDir, name);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Console.WriteLine("Creating Mod: " + name);
        Directory.CreateDirectory(path);
    }
}
