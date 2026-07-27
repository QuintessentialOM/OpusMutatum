using System;
using System.Collections.Generic;
using System.IO;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;

namespace OpusMutatum;

public static class OpusMutatum {
    #region Program

    private static bool autoExit = false;

    private enum ArgumentParsingMode {
        Argument,
        IntermediaryToNamedMappingPath,
        ObfToIntermediaryMappingPath,
        StringsPath,
        LightningExePath,
        QuintessentialPath
    }
    private enum RunAction {
        Run,
        Strings,
        Intermediary,
        Merge,
        Coreify,
        Setup,
        DevExe,
        QuintDevExe
    }

    private static void Main(string[] args) {
        RunAction action = HandleArguments(args);
        HandleSetup();

        Run(action);

        HandleCleanup();
    }

    private static RunAction HandleArguments(string[] args) {
        ArgumentParsingMode current = ArgumentParsingMode.Argument;
        RunAction action = RunAction.Setup;

        List<string> extraStringPaths = [];
        List<string> extraIntermediaryMappingPaths = [], extraNamedMappingPaths = [];

        Globals.OperatingSystem = Environment.OSVersion.Platform switch {
            PlatformID.Win32NT or PlatformID.Win32S or PlatformID.Win32Windows or PlatformID.WinCE => Globals.OS.Windows,
            PlatformID.MacOSX => Globals.OS.MacOS,
            _ => Globals.OS.Linux
        };

        foreach (string arg in args) {
            switch (current) {
                case ArgumentParsingMode.Argument:
                    // check if its "run", "strings", "intermediary", merge", "coreify", "setup", "devExe", "quintDevExe"
                    // or "--mappings", "--intermediary", "--strings", "--lightning", "--monomod", "--intermediaryPath", "--linux", "--mac", --"win"
                    if (arg.Equals("run"))
                        action = RunAction.Run;
                    else if (arg.Equals("strings"))
                        action = RunAction.Strings;
                    else if (arg.Equals("intermediary"))
                        action = RunAction.Intermediary;
                    else if (arg.Equals("merge"))
                        action = RunAction.Merge;
                    else if (arg.Equals("coreify"))
                        action = RunAction.Coreify;
                    else if (arg.Equals("setup"))
                        action = RunAction.Setup;
                    else if (arg.Equals("devExe"))
                        action = RunAction.DevExe;
                    else if (arg.Equals("quintDevExe"))
                        action = RunAction.QuintDevExe;

                    else if (arg.Equals("--mappings"))
                        current = ArgumentParsingMode.IntermediaryToNamedMappingPath;
                    else if (arg.Equals("--intermediary"))
                        current = ArgumentParsingMode.ObfToIntermediaryMappingPath;
                    else if (arg.Equals("--strings"))
                        current = ArgumentParsingMode.StringsPath;
                    else if (arg.Equals("--lightning"))
                        current = ArgumentParsingMode.LightningExePath;
                    else if (arg.Equals("--quintessential"))
                        current = ArgumentParsingMode.QuintessentialPath;
                    else if (arg.Equals("--linux"))
                        Globals.OperatingSystem = Globals.OS.Linux;
                    else if (arg.Equals("--mac"))
                        Globals.OperatingSystem = Globals.OS.MacOS;
                    else if (arg.Equals("--win"))
                        Globals.OperatingSystem = Globals.OS.Windows;
                    else if (arg.Equals("--autoExit"))
                        autoExit = true;
                    break;

                case ArgumentParsingMode.LightningExePath:
                    Globals.PathToLightningExe = arg;
                    current = ArgumentParsingMode.Argument;
                    break;

                case ArgumentParsingMode.ObfToIntermediaryMappingPath:
                    extraIntermediaryMappingPaths.Add(arg);
                    current = ArgumentParsingMode.Argument;
                    break;
                case ArgumentParsingMode.IntermediaryToNamedMappingPath:
                    extraNamedMappingPaths.Add(arg);
                    current = ArgumentParsingMode.Argument;
                    break;

                case ArgumentParsingMode.StringsPath:
                    extraStringPaths.Add(arg);
                    current = ArgumentParsingMode.Argument;
                    break;

                case ArgumentParsingMode.QuintessentialPath:
                    Patching.PathToQuintessential = arg;
                    current = ArgumentParsingMode.Argument;
                    break;

                default:
                    Console.WriteLine($"Invalid argument \"{arg}\"!");
                    break;
            }
        }

        StringDumping.LoadStringsPaths(extraStringPaths);
        Remapping.LoadMappingsPaths(extraIntermediaryMappingPaths, extraNamedMappingPaths);

        return action;
    }

    private static void Run(RunAction action) {
        try {
            switch (action) {
                case RunAction.Setup:
                    HandleStrings();

                    HandleCoreify();
                    HandleDependencies();

                    HandleIntermediary();

                    HandleMerge();
                    break;

                case RunAction.Strings:
                    HandleStrings();
                    break;

                case RunAction.Coreify:
                    HandleCoreify();
                    HandleDependencies();
                    break;

                case RunAction.Intermediary:
                    HandleIntermediary();
                    break;

                case RunAction.Merge:
                    HandleMerge();
                    break;

                case RunAction.QuintDevExe:
                    HandleQuintDevExe();
                    break;
                case RunAction.DevExe:
                    HandleDevExe();
                    break;

                case RunAction.Run:
                default:
                    HandleRun();
                    break;
            }
        } catch (Exception e) {
            Console.WriteLine("Error executing task:");
            Console.WriteLine(e.ToString());
        }

        Console.WriteLine("Done.");
        // keep command line open
        if (!autoExit) Console.ReadKey();
    }

    private static void HandleSetup() {
        Directory.CreateDirectory(Globals.PathToOutput);
        Directory.CreateDirectory(Globals.PathToTemporaryOutput);
    }
    private static void HandleCleanup() {
        Directory.Delete(Globals.PathToTemporaryOutput, recursive: true);
    }

    #endregion

    #region Actions

    private static void HandleStrings() {
        if (!Globals.TryLoadLightningExe(out AssemblyDefinition lightningExe))
            return;

        Console.WriteLine("Dumping strings...");

        string stringDumpingDir = Path.Combine(Globals.PathToOutput, StringDumping.PathToStringDumping);
        string stringDumperPath = Path.Combine(stringDumpingDir, "StringDumper_Lightning.exe");
        Directory.CreateDirectory(stringDumpingDir);

        Console.WriteLine("Creating string dumper...");
        StringDumping.CreateStringDumper(lightningExe, stringDumperPath);

        Console.WriteLine("Running string dumper...");
        StringDumping.EnsureDependenciesPresent(stringDumpingDir);
        AppHosting.RunExe(stringDumperPath);

        Console.WriteLine();
    }

    private static void HandleCoreify() {
        if (!File.Exists(Globals.PathToLightningExe)) {
            Console.WriteLine("Failed to find Lightning.exe!");
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
    }

    private static void HandleIntermediary() {
        // TODO: MonoMod relinking?
        if (!Globals.TryLoadLightning(out AssemblyDefinition lightning))
            return;

        Console.WriteLine("Generating intermediary assembly...");
        Remapping.RemapToIntermediary(lightning);

        lightning.Write(Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning));
        Console.WriteLine();
    }

    private static void HandleMerge() {
        string quintessentialPath = Path.Combine(Globals.PathToOutput, Patching.PathToQuintessential);
        if (!File.Exists(quintessentialPath)) {
            Console.WriteLine("Failed to find Quintessential.dll, skipping merge!");
            return;
        }

        string intermediaryLightningPath = Path.Combine(Globals.PathToOutput, Globals.PathToIntermediaryLightning);
        string moddedLightningPath = Path.Combine(Globals.PathToOutput, Globals.PathToModdedLightning);

        Console.WriteLine("Merging Quintessential.dll...");
        Patching.RunMonoMod(intermediaryLightningPath, moddedLightningPath, dllPaths: [quintessentialPath]);

        Console.WriteLine();
    }

    private static void HandleDevExe() {
        // take ModdedLightning.exe, remap to named
        if (!Globals.TryLoadModdedLightning(out AssemblyDefinition moddedLightning))
            return;

        Console.WriteLine("Generating development assembly...");
        Remapping.RemapToNamed(moddedLightning);

        moddedLightning.Write(Path.Combine(Globals.PathToOutput, "DevLightning.dll"));
        Console.WriteLine();
    }

    private static void HandleQuintDevExe() {
        // take IntermediaryLightning.exe, remap to named (no merged quintessential)
        if (!Globals.TryLoadIntermediaryLightning(out AssemblyDefinition intermediaryLightning))
            return;

        Console.WriteLine("Generating Quintessential development assembly...");
        Remapping.RemapToNamed(intermediaryLightning);

        intermediaryLightning.Write(Path.Combine(Globals.PathToOutput, "QuintDevLightning.dll"));
        Console.WriteLine();
    }

    private static void HandleRun() {
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
        AppHosting.RunAssembly(target);
    }

    #endregion
}
