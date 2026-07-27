using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace OpusMutatum;

public static class StringDumping {
    public static readonly string PathToStringDumping = "StringDumping";
    public static readonly string PathToStrings = "strings";

    private static readonly string[] StringDumpingDependencies = ["System.dll", "Steamworks.NET.dll"];

    private static readonly Dictionary<Guid, string> StringsPaths = new();
    private static readonly Dictionary<Guid, Dictionary<int, string>> Strings = new();
    private static readonly Dictionary<Guid, MethodDefinition> StringDeobfMethods = new();

    public static void LoadStringsPaths(List<string> extraStringsPaths) {
        DirectoryInfo stringsDirectory = Directory.CreateDirectory(Path.Combine(Globals.PathToOutput, PathToStringDumping, PathToStrings));
        string[] stringsPaths = stringsDirectory.GetFiles().Select(file => file.FullName).Concat(extraStringsPaths).ToArray();

        foreach (string path in stringsPaths) {
            if (!TryParseMvidFromPath(path, out Guid mvid))
                continue;

            if (!StringsPaths.TryAdd(mvid, path))
                Console.WriteLine($"Encountered duplicate strings file {path} for MVID `{mvid}`, skipping...");
        }
    }

    public static bool TryParseMvidFromPath(string path, out Guid mvid) {
        mvid = Guid.Empty;

        string filename = Path.GetFileNameWithoutExtension(path);
        int index = filename.LastIndexOf('_');
        if (index < 0)
            return false;

        string guidString = filename[(index + 1)..];
        return Guid.TryParse(guidString, out mvid);
    }

    public static bool TryLoadStrings(AssemblyDefinition assembly, out Dictionary<int, string> strings) {
        Guid mvid = assembly.GetMvid();
        if (Strings.TryGetValue(mvid, out strings)) {
            Console.WriteLine("Found strings from cache.");
            return true;
        }

        strings = new Dictionary<int, string>();

        if (!StringsPaths.TryGetValue(mvid, out string path) || !File.Exists(path))
            goto fail;

        string[] lines = File.ReadAllLines(path);
        int lastIndex = 0;
        foreach (string line in lines) {
            string[] split = line.Split("~,~");
            if (split.Length > 1) {
                // if we *can* split on this line, then we're definitely at the first line of a string
                try {
                    lastIndex = int.Parse(split[0]);
                    strings[lastIndex] = split[1];
                } catch (FormatException) { }
            } else {
                // if this line isn't blank (or even if it is), then we're continuing a previous multi-line string, so append
                strings[lastIndex] += "\n" + line;
            }
        }
        Strings[mvid] = strings;

        Console.WriteLine($"Loaded {strings.Count} strings from \"{Path.GetFileName(path)}\".");
        return true;

    fail:
        Console.WriteLine("Failed to load strings.");
        return false;
    }

    public static bool TryFindStringDeobfMethod(AssemblyDefinition assemblyDef, out MethodDefinition stringDeobfMethod) {
        stringDeobfMethod = null;

        Guid mvid = assemblyDef.GetMvid();
        if (StringDeobfMethods.TryGetValue(mvid, out stringDeobfMethod)) {
            Console.WriteLine("Found string deobfuscation method from cache.");
            return true;
        }

        ModuleDefinition module = assemblyDef.MainModule;
        MethodDefinition mainMethod = module.EntryPoint;

        if (mainMethod.Body?.Instructions is not { } instrs)
            goto fail;

        HashSet<string> candidateMethods = [];
        foreach (Instruction instr in instrs) {
            if (instr.Operand is not MethodReference methodRef || methodRef.Resolve() is not { } method)
                continue;

            // deobf method should be a static method of signature (int) => string
            if (method.IsStatic
                && method.Parameters.Count == 1
                && method.Parameters[0].ParameterType.FullName == "System.Int32"
                && method.ReturnType.FullName == "System.String") {
                candidateMethods.Add($"{method.DeclaringType.Name}.{method.Name}");
            }
        }

        // fail unless we found exactly one match
        if (candidateMethods.Count != 1)
            goto fail;

        string[] typeAndMethod = candidateMethods.Single().Split('.');
        StringDeobfMethods[mvid] = stringDeobfMethod = module.FindMethod(typeAndMethod[0], typeAndMethod[1]);
        Console.WriteLine("Found string deobfuscation method.");
        return true;

    fail:
        Console.WriteLine("Failed to find string deobfuscation method.");
        return false;
    }

    public static (HashSet<int>, MethodDefinition) FindStringKeys(AssemblyDefinition assemblyDef) {
        Console.WriteLine("Finding string keys...");

        ModuleDefinition module = assemblyDef.MainModule;
        if (!TryFindStringDeobfMethod(assemblyDef, out MethodDefinition stringDeobfMethod)) {
            Console.WriteLine("Failed to find string keys.");
            return (null, null);
        }

        // get all the keys this way
        List<Instruction> refs = [];
        foreach (TypeDefinition type in CollectNestedTypes(module.Types)) {
            if (type is null)
                continue;

            foreach (MethodDefinition method in type.Methods) {
                if (method?.Body?.Instructions is not { } instrs)
                    continue;

                foreach (Instruction instr in instrs) {
                    if (instr is null)
                        continue;

                    if (instr.OpCode.Code == Code.Call
                        && instr.Operand is MethodReference operand
                        && (operand.Resolve()?.Equals(stringDeobfMethod) ?? false))
                        refs.Add(instr);
                }
            }
        }

        HashSet<int> stringKeys = refs.Select(@ref => (int) @ref.Previous!.Operand).ToHashSet();
        Console.WriteLine($"Found {stringKeys.Count} string keys.");
        return (stringKeys, stringDeobfMethod);
    }

    public static Collection<TypeDefinition> CollectNestedTypes(Collection<TypeDefinition> topLevel) {
        Collection<TypeDefinition> types = [];
        foreach (var type in topLevel)
            VisitTypes(type, types.Add);

        return types;

        static void VisitTypes(TypeDefinition top, Action<TypeDefinition> action) {
            action(top);
            foreach (TypeDefinition type in top.NestedTypes)
                VisitTypes(type, action);
        }
    }

    // must be called with the non-coreified game
    public static void CreateStringDumper(AssemblyDefinition assemblyDef, string path) {
        ModuleDefinition module = assemblyDef.MainModule;
        string mvid = module.Mvid.ToString();

        string outputStringsPath = Path.Combine(Globals.PathToOutput, PathToStringDumping, PathToStrings, $"out_{mvid}.csv");
        (HashSet<int> stringKeys, MethodDefinition stringDeobfMethod) = FindStringKeys(assemblyDef);
        if (stringKeys is null || stringDeobfMethod is null) {
            Console.WriteLine("Failed to write string dumper.");
            return;
        }

        IMetadataScope mscorlibScope = module.AssemblyReferences.First(asmRef => asmRef.Name == "mscorlib");
        TypeReference stringType = module.TypeSystem.String;
        TypeReference voidType = module.TypeSystem.Void;

        // Manual construction of a bunch of type and method references because Cecil is jank
        MethodReference concat = new("Concat", stringType, stringType);
        for (int i = 0; i < 3; i++) concat.Parameters.Add(new ParameterDefinition(stringType));

        TypeReference streamWriterType = new("System.IO", "StreamWriter", module, mscorlibScope);
        TypeReference textWriterType = new("System.IO", "TextWriter", module, mscorlibScope);

        MethodReference streamWriterConstructor = new(".ctor", voidType, streamWriterType) { HasThis = true };
        streamWriterConstructor.Parameters.Add(new ParameterDefinition(stringType));
        streamWriterConstructor = module.ImportReference(streamWriterConstructor);

        MethodReference textWriterWriteLine = new("WriteLine", voidType, textWriterType) { HasThis = true };
        textWriterWriteLine.Parameters.Add(new ParameterDefinition(stringType));
        textWriterWriteLine = module.ImportReference(textWriterWriteLine);

        MethodReference textWriterDispose = new("Close", voidType, textWriterType) { HasThis = true };
        textWriterDispose = module.ImportReference(textWriterDispose);

        Console.WriteLine("Building string dumper...");
        ILProcessor proc = module.EntryPoint.Body.GetILProcessor();

        proc.Clear();
        proc.Append(proc.Create(OpCodes.Ldstr, outputStringsPath));
        proc.Append(proc.Create(OpCodes.Newobj, streamWriterConstructor));

        foreach (int key in stringKeys) {
            proc.Append(proc.Create(OpCodes.Dup));
            proc.Append(proc.Create(OpCodes.Ldstr, key.ToString()));
            proc.Append(proc.Create(OpCodes.Ldstr, "~,~"));
            proc.Append(proc.Create(OpCodes.Ldc_I4, key));
            proc.Append(proc.Create(OpCodes.Call, stringDeobfMethod));
            proc.Append(proc.Create(OpCodes.Call, concat));
            proc.Append(proc.Create(OpCodes.Callvirt, textWriterWriteLine));
        }

        proc.Append(proc.Create(OpCodes.Callvirt, textWriterDispose));
        proc.Append(proc.Create(OpCodes.Ret));

        module.Write(path);
        Console.WriteLine($"String dumper written to {path}.");
    }

    public static void EnsureDependenciesPresent(string directory) {
        // copy dependency dlls to the directory directly since the dumper is a framework binary :(
        foreach (string dependency in StringDumpingDependencies) {
            string destination = Path.Combine(directory, dependency);
            if (!File.Exists(destination))
                File.Copy(dependency, destination);
        }
    }
}
