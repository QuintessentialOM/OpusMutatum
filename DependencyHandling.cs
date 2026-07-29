using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;

namespace OpusMutatum;

public static partial class DependencyHandling {
    private static readonly string[] QuintessentialSystemLibs = []; // TODO: what goes in here? do we even need this?
    private static readonly string[] MonoSystemLibs = ["mscorlib.dll", "Mono.Posix.dll", "Mono.Security.dll"];

    private static readonly string Windows32BitNativeLibPath = "lib64-win-x86";
    private static readonly string Windows64BitNativeLibPath = "lib64-win-x64";
    private static readonly string LinuxNativeLibPath = "lib64-linux";
    private static readonly string MacOSNativeLibPath = "lib64-osx";

    public static bool IsSystemLib(string file) {
        if (Path.GetExtension(file) != ".dll")
            return false;

        if (Path.GetFileName(file).StartsWith("System.") &&
            !QuintessentialSystemLibs.Contains(Path.GetFileName(file)))
            return true;

        return MonoSystemLibs.Any(name => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static Version GetAssemblyVersion(string path) {
        using FileStream fs = File.OpenRead(path);
        using PEReader pe = new(fs);

        return pe.GetMetadataReader().GetAssemblyDefinition().Version;
    }

    public static Dictionary<string, Version> GetAssemblyReferences(string path) {
        using FileStream fs = File.OpenRead(path);
        using PEReader pe = new(fs);

        MetadataReader meta = pe.GetMetadataReader();

        Dictionary<string, Version> deps = new();
        foreach (AssemblyReference asmRef in meta.AssemblyReferences.Select(meta.GetAssemblyReference))
            deps.TryAdd(meta.GetString(asmRef.Name), asmRef.Version);

        return deps;
    }

    private static void ParseMonoNativeLibConfig(string configFile, Globals.OS os, Dictionary<string, string> dllMap, string dllNameScheme) {
        if (!File.Exists(configFile))
            return;

        Console.WriteLine($"Parsing Mono configuration file {configFile}...");

        string osString = os switch {
            Globals.OS.Windows => "windows", // Advancement Made! How Did We Get Here?
            Globals.OS.Linux => "linux",
            Globals.OS.MacOS => "osx",
            _ => throw new ArgumentOutOfRangeException(nameof(os))
        };

        // read the config file
        XmlDocument configDoc = new();
        configDoc.Load(configFile);
        foreach (XmlNode node in configDoc.DocumentElement!) {
            if (node is not XmlElement dllmapElement || node.Name != "dllmap")
                continue;

            // add an entry to the dllmap if the os matches
            if (dllmapElement.GetAttribute("os").Split(',').Contains(osString))
                dllMap[dllmapElement.GetAttribute("target")] = string.Format(dllNameScheme, dllmapElement.GetAttribute("dll"));
        }
    }

    public static void SetupNativeLibs() {
        string[] sourceLibPaths = []; // later entries take priority
        string libDestinationDir;
        Dictionary<string, string> dllMap = new();

        string lightningNativeLibConfig = Path.ChangeExtension(Globals.PathToLightningExe, ".exe.config");

        switch (Globals.OperatingSystem) {
            case Globals.OS.Windows:
                // setup windows native libs
                // TODO: i don't know how windows works
                libDestinationDir = Path.Combine(Globals.PathToOutput,
                    Environment.Is64BitOperatingSystem ? Windows64BitNativeLibPath : Windows32BitNativeLibPath);
                break;

            case Globals.OS.Linux:
                // setup linux native libs
                sourceLibPaths = ["lib64"];
                libDestinationDir = Path.Combine(Globals.PathToOutput, LinuxNativeLibPath);
                ParseMonoNativeLibConfig(lightningNativeLibConfig, Globals.OS.Linux, dllMap, "lib{0}.so");
                break;

            case Globals.OS.MacOS:
                // setup macos native libs
                // TODO: i don't know how macos works
                sourceLibPaths = ["osx"];
                libDestinationDir = Path.Combine(Globals.PathToOutput, MacOSNativeLibPath);
                ParseMonoNativeLibConfig(lightningNativeLibConfig, Globals.OS.MacOS, dllMap, "lib{0}.dylib");
                break;

            default: return;
        }

        // copy native libraries for the os
        if (!Directory.Exists(libDestinationDir))
            Directory.CreateDirectory(libDestinationDir);

        foreach (string libSrc in sourceLibPaths) {
            if (!Directory.Exists(libSrc))
                continue;

            if (File.Exists(libSrc)) {
                string libDst = Path.Combine(libDestinationDir, Path.GetFileName(libSrc));
                Console.WriteLine($"Copying native library from {libSrc} to {libDst}...");
                CopyNativeLib(libSrc, libDst);
            } else if (Directory.Exists(libSrc)) {
                Console.WriteLine($"Copying native libraries from {libSrc} to {libDestinationDir}...");
                foreach (string fileSrc in Directory.GetFiles(libSrc))
                    CopyNativeLib(fileSrc, Path.Combine(libDestinationDir, Path.GetRelativePath(libSrc, fileSrc)));
            }
        }

        return;

        void CopyNativeLib(string src, string dst) {
            string symlinkPath = null;
            if (dllMap.TryGetValue(Path.GetFileName(dst), out string mappedName)) {
                // on linux, additionally create a symlink for the unmapped path
                if (Globals.OperatingSystem == Globals.OS.Linux)
                    symlinkPath = dst;

                dst = Path.Combine(Path.GetDirectoryName(dst)!, mappedName);
            }

            File.Copy(src, dst, overwrite: true);

            if (symlinkPath is not null && symlinkPath != dst) {
                File.Delete(symlinkPath);
                File.CreateSymbolicLink(symlinkPath, Path.GetRelativePath(Path.GetDirectoryName(symlinkPath)!, dst));
            }
        }
    }

    public static void SetupNativeLibLoading() {
        string baseNativeLibPath = Path.Combine(AppContext.BaseDirectory, Globals.PathToOutput);

        switch (Globals.OperatingSystem) {
            case Globals.OS.Windows:
                SetDllDirectory(Path.Combine(baseNativeLibPath,
                    Environment.Is64BitProcess ? Windows64BitNativeLibPath : Windows32BitNativeLibPath));
                break;

            case Globals.OS.Linux:
                EnsureLibPathEnvVarSet("LD_LIBRARY_PATH", Path.Combine(baseNativeLibPath, LinuxNativeLibPath));
                break;

            case Globals.OS.MacOS:
                EnsureLibPathEnvVarSet("DYLD_LIBRARY_PATH", Path.Combine(baseNativeLibPath, MacOSNativeLibPath));
                break;

            default:
                return;
        }
    }

    // only windows makes us use an unmanaged api. wonderful
    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectory(string lpPathName);

    private static void EnsureLibPathEnvVarSet(string envVar, string libPath) {
        libPath = Path.GetFullPath(libPath);

        string[] ldPath = Environment.GetEnvironmentVariable(envVar)?.Split(":") ?? [];
        if (!ldPath.Any(path => !string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path) == libPath))
            Environment.SetEnvironmentVariable(envVar, $"{libPath}:{Environment.GetEnvironmentVariable(envVar)}");
    }
}
