using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace OpusMutatum;

public static class Globals {
    // OS enum, since Linux and Mac are different
    public enum OS {
        Windows,
        Linux,
        MacOS
    };
    public static OS OperatingSystem = OS.Windows;

    public static string PathToOutput = "modded";
    public static string PathToTemporaryOutput = "modded/temp";

    public static string PathToLightningExe = "Lightning.exe";

    public static string PathToLightning = "Lightning.dll";
    public static string PathToIntermediaryLightning = "IntermediaryLightning.dll";
    public static string PathToModdedLightning = "ModdedLightning.dll";
    public static string PathToNamedLightning = "NamedLightning.dll";

    public static MutatumTasks Tasks = null;

    private static readonly Dictionary<string, Assembly> CachedAssemblies = new();
    private static readonly Dictionary<string, AssemblyDefinition> CachedAssemblyDefs = new();

    public static bool TryLoadAssembly(string path, out Assembly assembly) {
        assembly = null;
        string filename = Path.GetFileName(path);

        if (CachedAssemblies.TryGetValue(path, out assembly)) {
            Console.WriteLine($"Loaded {filename} from cache.");
            return true;
        }

        if (!File.Exists(path)) {
            Console.WriteLine($"{filename} not found!");
            return false;
        }

        Console.WriteLine($"Reading {filename}...");
        try {
            CachedAssemblies[path] = assembly = Assembly.LoadFrom(path);
        } catch (Exception e) {
            Console.WriteLine($"Failed to load {filename}: {e.Message}");
            return false;
        }

        Console.WriteLine($"Found {filename}: {assembly!.FullName}");
        return true;
    }

    public static bool TryLoadAssemblyDef(string path, out AssemblyDefinition assemblyDef, bool logConsoleNormal = true) {
        assemblyDef = null;
        string filename = Path.GetFileName(path);

        if (CachedAssemblyDefs.TryGetValue(path, out assemblyDef)) {
            if (logConsoleNormal) Console.WriteLine($"Loaded {filename} from cache.");
            return true;
        }

        if (!File.Exists(path)) {
            Console.WriteLine($"{filename} not found!");
            return false;
        }

        if (logConsoleNormal) Console.WriteLine($"Reading {filename}...");
        try {
            CachedAssemblyDefs[path] = assemblyDef = AssemblyDefinition.ReadAssembly(path)
                ?? throw new Exception("Failed to read assembly definition.");
        } catch (Exception e) {
            Console.WriteLine($"Failed to load {filename}: {e.Message}");
            return false;
        }

        if (logConsoleNormal) Console.WriteLine($"Found {filename}: {assemblyDef!.FullName}");
        return true;
    }

    public static bool TryLoadLightningExe(out AssemblyDefinition lightningExeAssemblyDef)
        => TryLoadAssemblyDef(PathToLightningExe, out lightningExeAssemblyDef);

    public static bool TryLoadLightning(out AssemblyDefinition lightningAssemblyDef)
        => TryLoadAssemblyDef(Path.Combine(PathToOutput, PathToLightning), out lightningAssemblyDef);
    public static bool TryLoadIntermediaryLightning(out AssemblyDefinition intermediaryLightningAssemblyDef, bool logConsoleNormal = true)
        => TryLoadAssemblyDef(Path.Combine(PathToOutput, PathToIntermediaryLightning), out intermediaryLightningAssemblyDef, logConsoleNormal);
    public static bool TryLoadModdedLightning(out AssemblyDefinition moddedLightningAssemblyDef)
        => TryLoadAssemblyDef(Path.Combine(PathToOutput, PathToModdedLightning), out moddedLightningAssemblyDef);

    public static void RunAndWait(string command, RunDebugger debugger = null) {

        Console.WriteLine($"Running `{command}`...");

        ProcessStartInfo startInfo = OperatingSystem switch {
            OS.Windows => new ProcessStartInfo {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "cmd.exe",
                Arguments = $"/C \"{command}\""
            },
            OS.Linux => new ProcessStartInfo {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\""
            },
            OS.MacOS => new ProcessStartInfo {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\""
            },
            _ => new ProcessStartInfo()
        };
        startInfo.RedirectStandardOutput = true;
        startInfo.UseShellExecute = false;

        if (startInfo.EnvironmentVariables.ContainsKey("Path"))
            startInfo.EnvironmentVariables["Path"] = startInfo.EnvironmentVariables["Path"] + ";" + Directory.GetCurrentDirectory();

        Process process = new() { StartInfo = startInfo };
        debugger?.BeforeProcessStart();
        process.Start();
        debugger?.HandleRunningProcess(process);
        process.WaitForExit();

        Console.WriteLine($"Process exited with code {process.ExitCode}.");
        string output = process.StandardOutput.ReadToEnd();
        if (!string.IsNullOrEmpty(output)) {
            Console.WriteLine("Process output:");
            Console.WriteLine(output);
        }
    }
    public static void RunAndWaitDotnet(string argsString, RunDebugger debugger = null) {

        if (Environment.GetEnvironmentVariables() != null && Environment.GetEnvironmentVariables().Contains("Path")) {
            var exePath = Environment.GetEnvironmentVariable("Path").Split(";").Single(path => path.EndsWith("dotnet\\") || path.EndsWith("dotnet/") || path.EndsWith("dotnet"));
            exePath = Directory.EnumerateDirectories(exePath).SingleOrDefault(path => path.EndsWith("x64"), exePath);
            exePath = Directory.EnumerateFiles(exePath).Single(path => Path.GetFileName(path) == "dotnet.exe");

            var command = "\"" + exePath + "\" " + argsString;
            RunAndWait(command, debugger);
        } else
            RunAndWait($"dotnet {argsString}", debugger);
    }
}
