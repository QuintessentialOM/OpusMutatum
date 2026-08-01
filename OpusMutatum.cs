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

    private static void Main(string[] args) {
        HandleArguments(args);
        Globals.tasks = TaskParser.ReadTasksFromFile();

        HandleSetup();

        foreach (var task in Globals.tasks.tasks) {
            RunTask(task);
        }
        Console.WriteLine("Done.");
        if (!autoExit) Console.ReadKey(); // keep command line open

        HandleCleanup();
    }

    private static void HandleArguments(string[] args) {
        ArgumentParsingMode current = ArgumentParsingMode.Argument;

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
                    // check if its "--mappings", "--intermediary", "--strings", "--lightning", "--quintessential", "--intermediaryPath", "--linux", "--mac", --"win"
                    if (arg.Equals("--mappings"))
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
    }

    private static void RunTask(Task task) {
        try {
            switch (task.command) {
                case Command.Strings:
                    Tasks.HandleStrings(task.args);
                    break;
                case Command.Intermediary:
                    Tasks.HandleIntermediarySteps(task.args);
                    break;
                case Command.Merge:
                    Tasks.HandleMerge(task.args);
                    break;
                case Command.Copy:
                    Tasks.HandleCopy(task.args);
                    break;
                case Command.NewMod:
                    Tasks.HandleNewMod(task.args);
                    break;
                case Command.Run:
                    Tasks.HandleRun(task.args);
                    break;
                default:
                    break;
            }
            Console.WriteLine();
        } catch (Exception e) {
            Console.WriteLine("Error executing task:");
            Console.WriteLine(e.ToString());
        }
    }

    private static void HandleSetup() {
        Globals.PathToOutput = Globals.tasks.gameDir;
        Globals.PathToTemporaryOutput = Path.Combine(Globals.tasks.gameDir, "temp");
        Remapping.PathToMappings = Globals.tasks.mappingDir;
        //tasks.modsDir

        autoExit = Globals.tasks.autoExit || autoExit;

        Directory.CreateDirectory(Globals.PathToOutput);
        Directory.CreateDirectory(Globals.PathToTemporaryOutput);
    }
    private static void HandleCleanup() {
        Directory.Delete(Globals.PathToTemporaryOutput, recursive: true);
    }

    #endregion
}
