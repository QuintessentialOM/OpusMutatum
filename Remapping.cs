using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace OpusMutatum;

public static class Remapping {
    #region Data Structures

	private class Mappings {
		public string NamespaceA { get; set; }
		public string NamespaceB { get; set; }
		public int nextClassIndex { get; set; }
		public int nextEnumIndex { get; set; }
		public int nextInterfaceIndex { get; set; }
		public int nextStructIndex { get; set; }
		public int nextDelegateIndex { get; set; }
		public int nextMethodIndex { get; set; }
		public int nextFieldIndex { get; set; }
		public int nextGenericIndex { get; set; }
		public int nextParamIndex { get; set; }
		public List<ClassMapping> Classes { get; set; }
	}

	private class ClassMapping {
		public string ClassFullNameA { get; set; } // Includes containing types for nested types
		public string ClassNameB { get; set; }
		public List<FieldMapping> Fields { get; set; }
		public List<MethodMapping> Methods { get; set; }
		public List<GenericParameterMapping> GenericParameters { get; set; }
	}

	private class FieldMapping {
		public string FieldNameA { get; set; }
		public string FieldNameB { get; set; }
	}

	private class MethodMapping {
		public string MethodNameA { get; set; }
		public string ReturnTypeFullNameA { get; set; }
		public List<string> ArgumentTypeFullNamesA { get; set; }
		public string MethodNameB { get; set; }
		public List<MethodParameterMapping> Parameters { get; set; }
		public List<GenericParameterMapping> GenericParameters { get; set; }
	}

	private class MethodParameterMapping {
		public string ParameterNameA { get; set; }
		public string ParameterNameB { get; set; }
	}

	// TODO method locals

	private class GenericParameterMapping {
		public string GenericNameA { get; set; }
		public string GenericNameB { get; set; }
	}

    #endregion

    #region Remappers

    private static readonly Dictionary<Guid, Mappings> ObfToIntermediaryMappings = new();
    private static readonly Dictionary<Guid, Dictionary<string, string>> IntermediaryToNamedMappings = new();

    private interface IRemapper {
		// these methods should return the current name if there is no remapping to be done
		string RemapType(TypeReference type);
		string RemapField(FieldReference field);
		string RemapMethod(MethodReference method);
		string RemapMethodParam(ParameterReference param, MethodReference method);
		string RemapGeneric(GenericParameter generic);
	}

    private class IntermediaryRemapper(Mappings mappings) : IRemapper {
        public string RemapField(FieldReference field)
            => FindType(field.DeclaringType)?.Fields.Where(f => f.FieldNameA == field.Name).SingleOrNull()?.FieldNameB ?? field.Name;

        public string RemapGeneric(GenericParameter generic)
            => generic.Type == GenericParameterType.Method
                ? FindMethod(generic.DeclaringMethod)?.GenericParameters.Where(g => g.GenericNameA == generic.Name)
                    .SingleOrNull()?.GenericNameB ?? generic.Name
                : FindType(generic.DeclaringType)?.GenericParameters.Where(g => g.GenericNameA == generic.Name)
                    .SingleOrNull()?.GenericNameB ?? generic.Name;

        public string RemapMethod(MethodReference method)
            => FindMethod(method)?.MethodNameB ?? method.Name;

        public string RemapMethodParam(ParameterReference param, MethodReference method)
            => FindMethod(method)?.Parameters.Where(p => p.ParameterNameA == param.Name).SingleOrNull()?.ParameterNameB ?? param.Name;

        public string RemapType(TypeReference type)
            => FindType(type)?.ClassNameB ?? type.Name;

        private TypeReference GetMainType(TypeReference type) {
            if (type.IsGenericParameter)
                throw new Exception($"Attempted to get main type of generic parameter `{type.FullName}`!");

            if (type.IsGenericInstance || type.IsArray || type.IsByReference || type.IsPointer)
                return GetMainType(type.GetElementType());

            return type;
        }

        private ClassMapping FindType(TypeReference type) {
            type = GetMainType(type); // Ignore generics, array types, reference types, etc
            return mappings.Classes.Where(cls => cls.ClassFullNameA == type.FullName).SingleOrNull();
        }

        private MethodMapping FindMethod(MethodReference method)
            // TODO: generic params stripped when matching method signatures due to Cecil handling generic instance method references strangely
            // probably not ideal, but maybe it's fine?
                        => FindType(method.DeclaringType)?.Methods.Where(m => {
                if (m.MethodNameA != method.Name || m.ArgumentTypeFullNamesA.Count != method.Parameters.Count || m.GenericParameters.Count != method.GenericParameters.Count)
                    return false;
                var paramTypes = method.Parameters.Select(p => p.ParameterType.FullName).ToList();
                var returnType = method.ReturnType.FullName;
                // Substitute generic names so that they compare properly - method references sometimes have !!n for the n-th generic, instead of using the method definition's generic parameter name.
                foreach (var (from, to) in method.GenericParameters.Select(p => p.FullName).Zip(m.GenericParameters.Select(p => p.GenericNameA))) {
                    for (int i = 0; i < paramTypes.Count; i++) {
                        paramTypes[i] = paramTypes[i].Replace(from, to);
                    }
                    returnType = returnType.Replace(from, to);
                }
                return m.ReturnTypeFullNameA == returnType
                    && m.ArgumentTypeFullNamesA.Zip(paramTypes, (a, b) => (a, b))
                        .All(pair => pair.a == pair.b);
            }).SingleOrNull();
    }

    private class NamedRemapper(Dictionary<string, string> mappings) : IRemapper {
        public string RemapField(FieldReference field)
            => GetNamedForIntermediary(field.Name, field.DeclaringType);

        public string RemapGeneric(GenericParameter generic)
            => GetNamedForIntermediary(generic.Name,
                generic.Type == GenericParameterType.Method
                    ? generic.DeclaringMethod.DeclaringType
                    : generic.DeclaringType);

        public string RemapMethod(MethodReference method)
            => GetNamedForIntermediary(method.Name, method.DeclaringType);

        public string RemapMethodParam(ParameterReference param, MethodReference method)
            => GetNamedForIntermediary(param.Name, method.DeclaringType);

        public string RemapType(TypeReference type)
            => GetNamedForIntermediary(type.Name, type.DeclaringType);

        private string GetNamedForIntermediary(string intermediary, TypeReference owner) {
            if (!mappings.TryGetValue(intermediary, out string name))
                return intermediary;

            if (!name.Contains('.'))
                return name;

            string[] split = name.Split('.');
            name = split[^1];
            return name;
        }
    }

    private static void DoRemap(IRemapper remapper, Collection<TypeDefinition> types,
        Action<MethodReference, string, Instruction> onMethodReference = null,
        Action<TypeDefinition> onTypeDefinition = null) {
        // Renames are deferred so that everything compares against old names, rather than a mixture of old and new names
        Dictionary<MemberReference, string> deferredRenames = new();
        Dictionary<ParameterReference, string> deferredParamRenames = new();

        foreach (TypeDefinition type in types) {
            if (type.IsGenericInstance || type.IsGenericParameter || type.IsArray || type.IsByReference || type.IsPointer)
                throw new Exception($"Expected to only be remapping main types, not generic instances/generic parameters/arrays/references/pointers, but got `{type.FullName}`!");

            deferredRenames[type] = remapper.RemapType(type);
            onTypeDefinition?.Invoke(type);
            foreach (MethodDefinition method in type.Methods) {
                // rtspecialname is applied to constructors and operators
                if (!method.IsRuntimeSpecialName)
                    deferredRenames[method] = remapper.RemapMethod(method);
                foreach (GenericParameter generic in method.GenericParameters)
                    deferredRenames[generic] = remapper.RemapGeneric(generic);
                foreach (ParameterDefinition param in method.Parameters)
                    deferredParamRenames[param] = remapper.RemapMethodParam(param, method);

                // references to members in classes with generic parameters don't get remapped automatically
                // so here we update those references ourself
                if (method?.Body?.Instructions is not { } instrs)
                    continue;

                foreach (Instruction instr in instrs) {
                    if (instr is null)
                        continue;

                    if (instr.Operand is MethodReference { IsWindowsRuntimeProjection: false } mref) {
                        if (mref.IsGenericInstance)
                            mref = ((GenericInstanceMethod) mref).GetElementMethod();

                        // also take the opportunity to replace references to string decoder with the actual string
                        deferredRenames[mref] = remapper.RemapMethod(mref);
                        onMethodReference?.Invoke(mref, deferredRenames[mref], instr);
                    }

                    if (instr is { Operand: FieldReference fref })
                        deferredRenames[fref] = remapper.RemapField(fref);
                }

                foreach (CustomAttribute attr in method.CustomAttributes)
                    if (attr.HasConstructorArguments)
                        foreach (CustomAttributeArgument arg in attr.ConstructorArguments)
                            if (arg.Type.Name.Equals("Type"))
                                deferredRenames[arg.Value as TypeReference] =
                                    remapper.RemapType(arg.Value as TypeReference);

                // TODO: map locals
            }

            foreach (FieldDefinition field in type.Fields)
                deferredRenames[field] = remapper.RemapField(field);
            foreach (GenericParameter generic in type.GenericParameters)
                deferredRenames[generic] = remapper.RemapGeneric(generic);
        }

        foreach ((MemberReference mref, string newName) in deferredRenames)
            mref.Name = newName;
        foreach ((ParameterReference pref, string newName) in deferredParamRenames)
            pref.Name = newName;
    }

    #endregion

    #region Remapping

    public static string PathToMappings = "Mappings";
    public static string PathToIntermediary = "intermediary";
    public static string PathToNamed = "named";

    private static readonly Dictionary<Guid, string> ObfToIntermediaryMappingsPaths = new();
    private static readonly Dictionary<Guid, string> IntermediaryToNamedMappingsPaths = new();

    public static void LoadMappingsPaths(List<string> extraIntermediaryMappingsPaths = null, List<string> extraNamedMappingsPaths = null) {
        DirectoryInfo mappingsDirectory = Directory.CreateDirectory(Path.Combine(Globals.PathToOutput, PathToMappings));
        DirectoryInfo intermediaryDirectory = Directory.CreateDirectory(Path.Combine(Globals.PathToOutput, PathToMappings, PathToIntermediary));
        DirectoryInfo namedDirectory = Directory.CreateDirectory(Path.Combine(Globals.PathToOutput, PathToMappings, PathToNamed));

        AddMappingsFiles(mappingsDirectory, ObfToIntermediaryMappingsPaths, recursive: false);
        AddMappingsFiles(mappingsDirectory, IntermediaryToNamedMappingsPaths, recursive: false);

        AddMappingsFiles(intermediaryDirectory, ObfToIntermediaryMappingsPaths, recursive: true);
        AddMappingsFiles(namedDirectory, IntermediaryToNamedMappingsPaths, recursive: true);

        if (extraIntermediaryMappingsPaths is not null)
            AddMappingsFiles(extraIntermediaryMappingsPaths, ObfToIntermediaryMappingsPaths);
        if (extraNamedMappingsPaths is not null)
            AddMappingsFiles(extraNamedMappingsPaths, IntermediaryToNamedMappingsPaths);
    }

    private static void AddMappingsFiles(List<string> mappingsPaths, Dictionary<Guid, string> mappings) {
        foreach (string path in mappingsPaths) {
            if (!StringDumping.TryParseMvidFromPath(path, out Guid mvid)) 
                mvid = StringDumping.AsDeterministicGuid(Path.GetFileNameWithoutExtension(path));

            if (!mappings.TryAdd(mvid, path))
                Console.WriteLine($"Encountered duplicate mappings file {path} for MVID `{mvid}`, skipping...");
        }
    }

    private static void AddMappingsFiles(DirectoryInfo mappingsDirectory, Dictionary<Guid, string> mappings, bool recursive = false)
        => AddMappingsFiles(mappingsDirectory.GetFiles("*", new EnumerationOptions { RecurseSubdirectories = recursive }).Select(file => file.FullName).ToList(), mappings);

    private static bool TryLoadObfToIntermediaryMappings(AssemblyDefinition assembly, out Mappings mappings) {
        mappings = new Mappings();

        Guid mvid = assembly.GetMvid();
        if (ObfToIntermediaryMappings.TryGetValue(mvid, out mappings)) {
            Console.WriteLine("Found valid intermediary mappings from cache.");
            return true;
        }

        if (!ObfToIntermediaryMappingsPaths.TryGetValue(mvid, out string path) || !File.Exists(path))
            goto fail;

        try {
            string serialized = File.ReadAllText(path);
            ObfToIntermediaryMappings[mvid] = mappings = JsonSerializer.Deserialize<Mappings>(serialized);
        } catch {
            goto fail;
        }

        Console.WriteLine($"Found valid intermediary mappings: {Path.GetFileName(path)}");
        return true;

    fail:
        Console.WriteLine("Failed to find valid intermediary mappings!");
        return false;
    }

    private static bool TryLoadIntermediaryToNamedMappings(AssemblyDefinition assembly, out Dictionary<string, string> mappings) {
        Guid mvid = assembly.GetMvid();
        if (IntermediaryToNamedMappings.TryGetValue(mvid, out mappings)) {
            Console.WriteLine("Found valid named mappings from cache.");
            return true;
        }

        mappings = new Dictionary<string, string>();

        foreach (var pair in IntermediaryToNamedMappingsPaths) {
            string path = pair.Value;
            if (File.Exists(path)) {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length > 1 && lines[0].StartsWith("Mapping version: ")) {

                    for (int i = 1; i < lines.Length; i++) {
                        string line = lines[i];

                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                            continue;
                        if (!line.Contains(',')) {
                            Console.WriteLine($"Missing ',' at {line}");
                        }

                        string[] parts = line.Split(',');
                        mappings[parts[0]] = parts[1];
                    }

                    Console.WriteLine($"Found valid named mappings: {Path.GetFileName(path)}");
                }
            }
        }

        IntermediaryToNamedMappings[mvid] = mappings;
        if (mappings.Count == 0) {
            Console.WriteLine("Failed to find valid named mappings!");
            return false;
        }

        return true;
    }

    public static void RemapToIntermediary(AssemblyDefinition obfAssemblyDef) {
        if (!StringDumping.TryLoadStrings(obfAssemblyDef, out Dictionary<int, string> strings))
            return;
        if (!StringDumping.TryFindStringDeobfMethod(obfAssemblyDef, out MethodDefinition stringDeobfMethod))
            return;
        if (!TryLoadObfToIntermediaryMappings(obfAssemblyDef, out Mappings mappings))
            return;

        List<(Instruction, int)> stringsToBeInlined = [];
        DoRemap(new IntermediaryRemapper(mappings), StringDumping.CollectNestedTypes(obfAssemblyDef.MainModule.Types),
            (mref, _, instr) => {
                // this is called before renames happen so the string deobf method check should match
                if (mref.FullName == stringDeobfMethod.FullName
                    && mref.Parameters.Count == 1
                    && instr.Previous.OpCode == OpCodes.Ldc_I4)
                    stringsToBeInlined.Add((instr, (int) instr.Previous.Operand));
            },
            type => {
                if (type.IsNested)
                    type.IsNestedPublic = true;
                else
                    type.IsPublic = true;
            });

        if (stringsToBeInlined.Count > 0) {
            foreach ((Instruction instr, int stringKey) in stringsToBeInlined)
                if (strings.TryGetValue(stringKey, out string s)) {
                    instr.Previous.Set(OpCodes.Nop, null);
                    instr.Set(OpCodes.Ldstr, s);
                } else
                    Console.WriteLine($"Missing string for key `{stringKey}`!");
        }
    }

    public static void RemapToNamed(AssemblyDefinition intermediaryAssemblyDef) {
        if (!TryLoadIntermediaryToNamedMappings(intermediaryAssemblyDef, out Dictionary<string, string> mappings))
            return;

        DoRemap(new NamedRemapper(mappings), StringDumping.CollectNestedTypes(intermediaryAssemblyDef.MainModule.Types));
    }

    #endregion
}
