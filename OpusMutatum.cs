using OpusMutatum.Merging;
using System;
using System.Collections.Generic;
using System.IO;

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
        try {
            HandleArguments(args);
            Globals.Tasks = TaskParser.ReadTasksFromFile();

            HandleSetup();

            foreach (var task in Globals.Tasks.Tasks) {
                RunTask(task);
            }
            Console.WriteLine("Done.");
            if (!autoExit) Console.ReadKey(); // keep taskText line open

            HandleCleanup();

        } catch (Exception e) {
            Console.WriteLine("Error running Mutatum:");
            if (e is Globals.InternalProcessError) {
                var messages = "";
                var exc = e;
                bool first = true;
                while (exc != null) {
                    if (first) first = false;
                    else messages += "--: ";
                    messages += exc.Message + "\n";
                    exc = exc.InnerException;
                }
                if (!string.IsNullOrEmpty(messages))
                    Console.WriteLine(messages);
            } else
                Console.WriteLine(e.ToString());
            Console.ReadKey();
        }
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
            switch (task.Command) {
                case Command.Strings:
                    Tasks.HandleStrings(task.Args);
                    break;
                case Command.Intermediary:
                    Tasks.HandleIntermediarySteps(task.Args);
                    break;
                case Command.MergeDev:
                    Tasks.HandleMergeDev(task.Args);
                    break;
                case Command.Merge:
                    Tasks.HandleMerge(task.Args);
                    break;
                case Command.Copy:
                    Tasks.HandleCopy(task.Args);
                    break;
                case Command.NewMod:
                    Tasks.HandleNewMod(task.Args);
                    break;
                case Command.Run:
                    Tasks.HandleRun(task.Args);
                    break;
                default:
                    break;
            }
            Console.WriteLine();
        } catch (Exception e) {
            string taskText = task.Command.ToString() + " ";
            foreach (var arg in task.Args) taskText += arg + " ";
            if (e is Globals.InternalProcessError) throw new Globals.InternalProcessError("Error executing task '" + taskText.Trim() + "':", e);
            throw new Exception("Error executing task '" + taskText.Trim() + "':", e);
        }
    }

    private static void HandleSetup() {
        Globals.PathToOutput = Globals.Tasks.GameDir;
        Globals.PathToTemporaryOutput = Path.Combine(Globals.Tasks.GameDir, "temp");
        ModLoader.PathToMods = Globals.Tasks.ModsDir;
        ModLoader.PathToUnpackedMods = Path.Combine(Globals.Tasks.GameDir, "UnpackedMods");
        Remapping.PathToMappings = Globals.Tasks.MappingsDir;

        autoExit = Globals.Tasks.AutoExit || autoExit;

        Directory.CreateDirectory(Globals.PathToOutput);
        Directory.CreateDirectory(Globals.PathToTemporaryOutput);
    }
    private static void HandleCleanup() {
        Directory.Delete(Globals.PathToTemporaryOutput, recursive: true);
    }

    #endregion
}
