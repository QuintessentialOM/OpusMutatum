using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpusMutatum;

public static partial class AppHosting {
    public static void RunAssembly(string assembly, string[] args = null, string[] manualDependencies = null) {
        CreateRuntimeConfigFiles(assembly, manualDependencies);

        string argsString = args is not null
            ? $"{assembly} {string.Join(' ', args)}"
            : assembly;
        Globals.RunAndWait($"dotnet {argsString}");
    }

    public static void CreateRuntimeConfigFiles(string assembly, string[] manualDependencies = null) {
        manualDependencies ??= [];

        Console.WriteLine($"Creating .NET runtime configuration files for {assembly}...");

        // determine current .NET version
        string frameworkName = Assembly.GetExecutingAssembly().GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        string frameworkVersionPrefix = ".NETCoreApp,Version=v";
        if (!frameworkName.StartsWith(frameworkVersionPrefix))
            throw new Exception($"OpusMutatum must be built to target .NET Core!");

        string frameworkVersion = frameworkName[frameworkVersionPrefix.Length..];
        if (!FrameworkVersionRegex().IsMatch(frameworkVersion))
            throw new Exception($"Invalid target .NET version: {frameworkVersion}!");

        // write .runtimeconfig.json
        using (FileStream fs = File.OpenWrite(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
        using (Utf8JsonWriter writer = new(fs, new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            writer.WriteStartObject("runtimeOptions");
            writer.WriteString("tfm", $"net{frameworkVersion}");
            writer.WriteStartObject("framework");
            writer.WriteString("name", "Microsoft.NETCore.App");
            writer.WriteString("version", $"{frameworkVersion}.0");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // write .deps.json
        Dictionary<string, Dictionary<string, Version>> dependencies = new();

        DiscoverAssemblies(assembly);
        foreach (string dependency in manualDependencies)
            DiscoverAssemblies(dependency);

        using (FileStream fs = File.OpenWrite(Path.ChangeExtension(assembly, ".deps.json")))
        using (Utf8JsonWriter writer = new(fs, new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();

            writer.WriteStartObject("runtimeTarget");
            writer.WriteString("name", frameworkName);
            writer.WriteString("signature", "");
            writer.WriteEndObject();

            writer.WriteStartObject("compilationOptions");
            writer.WriteEndObject();

            writer.WriteStartObject("targets");
            writer.WriteStartObject(frameworkName);
            foreach ((string dependencyPath, Dictionary<string, Version> dependencyDeps) in dependencies) {
                writer.WriteStartObject(
                    $"{Path.GetFileNameWithoutExtension(dependencyPath)}/{DependencyHandling.GetAssemblyVersion(dependencyPath)}");

                writer.WriteStartObject("runtime");
                writer.WriteStartObject(Path.GetFileName(dependencyPath));
                writer.WriteEndObject();
                writer.WriteEndObject();

                if (dependencyDeps.Count > 0) {
                    writer.WriteStartObject("dependencies");
                    foreach (var dep in dependencyDeps)
                        writer.WriteString(dep.Key, dep.Value.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartObject("libraries");
            foreach ((string asmPath, Dictionary<string, Version> asmDeps) in dependencies) {
                writer.WriteStartObject(
                    $"{Path.GetFileNameWithoutExtension(asmPath)}/{DependencyHandling.GetAssemblyVersion(asmPath)}");
                writer.WriteString("type", (asmPath == assembly) ? "project" : "reference");
                writer.WriteBoolean("servicable", false);
                writer.WriteString("sha512", string.Empty);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return;

        void DiscoverAssemblies(string asm) {
            if (dependencies.ContainsKey(asm))
                return;

            Dictionary<string, Version> deps = DependencyHandling.GetAssemblyReferences(asm);
            dependencies.Add(asm, deps);

            foreach ((string dep, Version _) in deps) {
                string depPath = Path.Combine(Path.GetDirectoryName(asm)!, $"{dep}.dll");
                if (File.Exists(depPath))
                    DiscoverAssemblies(depPath);
            }
        }
    }

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex FrameworkVersionRegex();
}
