using OpusMutatum.Merging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace OpusMutatum;

public static class OpusMutatum {
    #region Program
    
    private static List<string> extraStringPaths = [];
    private static List<string> extraIntermediaryMappingPaths = [], extraNamedMappingPaths = [];
    
    private static AutoExitMode autoExit = AutoExitMode.Never;

    private enum ArgumentParsingMode {
        Argument,
        TasksPath,
        LightningExePath,
        StringsPath,
        ObfToIntermediaryMappingPath,
        IntermediaryToNamedMappingPath
    }

    private static void Main(string[] args) {
        try {
            HandleArguments(args);
            Globals.Tasks = TaskParser.ReadTasksFromFile();

            HandleSetup();

            foreach (var task in Globals.Tasks.Tasks) {
                RunTask(task);
            }
            System.Threading.Tasks.Task.WaitAll(Globals.ConcurrentTasks);
            Console.WriteLine("Done.");
            if (autoExit == AutoExitMode.Never) Console.ReadKey(); // keep taskText line open

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
            if (autoExit != AutoExitMode.Always) Console.ReadKey();
            Environment.ExitCode = 1;
        }
        System.Threading.Tasks.Task.WaitAll(Globals.ConcurrentTasks);
    }

    private static void HandleArguments(string[] args) {
        ArgumentParsingMode current = ArgumentParsingMode.Argument;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Globals.OperatingSystem = Globals.OS.Windows;
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Globals.OperatingSystem = Globals.OS.MacOS;
        else
            Globals.OperatingSystem = Globals.OS.Linux;

        foreach (string arg in args) {
            switch (current) {
                case ArgumentParsingMode.Argument:
                    // check if its "--mappings", "--intermediary", "--strings", "--lightning", "--tasks", "--linux", "--mac", --"win"
                    if (arg.Equals("--tasks"))
                        current = ArgumentParsingMode.TasksPath;
                    else if (arg.Equals("--lightning"))
                        current = ArgumentParsingMode.LightningExePath;
                    else if (arg.Equals("--strings"))
                        current = ArgumentParsingMode.StringsPath;
                    else if (arg.Equals("--intermediary"))
                        current = ArgumentParsingMode.ObfToIntermediaryMappingPath;
                    else if (arg.Equals("--mappings"))
                        current = ArgumentParsingMode.IntermediaryToNamedMappingPath;
                    else if (arg.Equals("--linux"))
                        Globals.OperatingSystem = Globals.OS.Linux;
                    else if (arg.Equals("--mac"))
                        Globals.OperatingSystem = Globals.OS.MacOS;
                    else if (arg.Equals("--win"))
                        Globals.OperatingSystem = Globals.OS.Windows;
                    else if (arg.Equals("--autoExit"))
                        autoExit = AutoExitMode.OnSuccess;
                    break;
                
                case ArgumentParsingMode.TasksPath:
                    TaskParser.TasksPath = arg;
                    current = ArgumentParsingMode.Argument;
                    break;

                case ArgumentParsingMode.LightningExePath:
                    Globals.PathToLightningExe = arg;
                    current = ArgumentParsingMode.Argument;
                    break;
                
                case ArgumentParsingMode.StringsPath:
                    extraStringPaths.Add(arg);
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

                default:
                    Console.WriteLine($"Invalid argument \"{arg}\"!");
                    break;
            }
        }
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
                case Command.Export:
                    Tasks.HandleExport(task.Args);
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
        Globals.PathToOutput = Globals.Tasks.ModdedGameDir;
        Globals.PathToTemporaryOutput = Path.Combine(Globals.Tasks.ModdedGameDir, "temp");
        Globals.PathToLightningExe ??= Globals.OperatingSystem == Globals.OS.MacOS
            ? "Opus Magnum.app/Contents/Resources/Lightning.exe"
            : Path.Combine(Globals.Tasks.VanillaGameDir, "Lightning.exe");
        Directory.CreateDirectory(Globals.PathToOutput);
        Directory.CreateDirectory(Globals.PathToTemporaryOutput);
        
        StringDumping.LoadStringsPaths(extraStringPaths);
        
        Remapping.PathToMappings = Globals.Tasks.MappingsDir;
        Remapping.LoadMappingsPaths(extraIntermediaryMappingPaths, extraNamedMappingPaths);
        
        ModLoader.PathToMods = Globals.Tasks.ModsDir;
        ModLoader.PathToUnpackedMods = Path.Combine(Globals.Tasks.ModdedGameDir, "UnpackedMods");

        autoExit = autoExit == AutoExitMode.Never ? Globals.Tasks.AutoExit : autoExit;
    }
    private static void HandleCleanup() {
        Directory.Delete(Globals.PathToTemporaryOutput, recursive: true);
    }

    #endregion
}
