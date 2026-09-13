using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpusMutatum;

public static partial class Remapping {
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

    private record NamedMappings(Dictionary<string, string> Mappings, Dictionary<string, Dictionary<int, string>> AddedEnumVariants);
    private record Backmapper(Mappings Mappings, Dictionary<string, Dictionary<int, string>> RemovedEnumVariants);

    #endregion

    #region Remappers

    private static readonly Dictionary<Guid, Mappings> ObfToIntermediaryMappings = []; // assembly mvid -> mappings
    private static Tuple<Version, NamedMappings> IntermediaryToNamedMappings = null; // unique mappings id -> mappings. //TODO: refactor named mappings loading
    private static readonly Dictionary<Guid, Backmapper> NamedToIntermediaryMappings = []; // combined from the IntermediaryToNamed and ObfToIntermediary mappings ( using Named for obfustacted )

    private interface IRemapper {
		// these methods should return the current name if there is no remapping to be done
		string RemapType(TypeReference type);
		string RemapField(FieldReference field);
		string RemapMethod(MethodReference method);
		string RemapMethodParam(ParameterReference param, MethodReference method);
		string RemapGeneric(GenericParameter generic);
        string RemapTypeByFullName(string fullName);
        string RemapMethodByTypeAndName(string declaringTypeFullName, string Name, string[] methodParams = null);
        string RemapFieldByTypeAndName(string declaringTypeFullName, string Name);
    }

    private class IntermediaryRemapper(Mappings mappings) : IRemapper {
        public string RemapField(FieldReference field)
            => FindType(field.DeclaringType)?.Fields.Where(f => f.FieldNameA == field.Name).SingleOrNull()?.FieldNameB ?? field.Name;

        public string RemapFieldByTypeAndName(string declaringTypeFullName, string Name) {
            RemapTypeByFullName(declaringTypeFullName, out var lastClass);
            return lastClass?.Fields.Where(method => method.FieldNameA == Name).SingleOrNull()?.FieldNameB ?? Name;
        }

        public string RemapGeneric(GenericParameter generic)
            => generic.Type == GenericParameterType.Method
                ? FindMethod(generic.DeclaringMethod)?.GenericParameters.Where(g => g.GenericNameA == generic.Name)
                    .SingleOrNull()?.GenericNameB ?? generic.Name
                : FindType(generic.DeclaringType)?.GenericParameters.Where(g => g.GenericNameA == generic.Name)
                    .SingleOrNull()?.GenericNameB ?? generic.Name;

        public string RemapMethod(MethodReference method) {
            if (!method.Name.StartsWith("orig_"))
                return FindMethod(method)?.MethodNameB ?? method.Name;
            string toReturn = FindMethod(method, method.Name[5..])?.MethodNameB;
            return toReturn == null ? method.Name : "orig_" + toReturn;
        }

        public string RemapMethodByTypeAndName(string declaringTypeFullName, string Name, string[] methodParams = null) {
            RemapTypeByFullName(declaringTypeFullName, out var lastClass);
            return lastClass?.Methods.Where(method => {
                if(method.MethodNameA != Name) return false;
                if (methodParams != null) {
                    if (methodParams.Length != method.ArgumentTypeFullNamesA.Count) return false;
                    for (int i = 0; i < methodParams.Length; i++) {
                        if (methodParams[i] != method.ArgumentTypeFullNamesA[i]) return false;
                    }
                }
                return true;
            }).SingleOrNull()?.MethodNameB ?? Name;
        }

        public string RemapMethodParam(ParameterReference param, MethodReference method) {
            if (!method.Name.StartsWith("orig_"))
                return FindMethod(method)?.Parameters.Where(p => p.ParameterNameA == param.Name).SingleOrNull()?.ParameterNameB ?? param.Name;
            return FindMethod(method, method.Name[5..])?.Parameters.Where(p => p.ParameterNameA == param.Name).SingleOrNull()?.ParameterNameB ?? param.Name;
        }

        public string RemapType(TypeReference type) {
            if (type.GetPatchFullName() == type.FullName)
                return FindType(type)?.ClassNameB ?? type.Name;
            if (type.Name.StartsWith("<>c")) return type.Name;
            string toReturn = FindType(type)?.ClassNameB;
            return toReturn == null ? type.Name : "patch_" + toReturn;
        }

        public string RemapTypeByFullName(string fullName) {
            return RemapTypeByFullName(fullName, out var _);
        }
        public string RemapTypeByFullName(string fullName, out ClassMapping lastClass) {
            lastClass = null;
            var split = fullName.Split('/');
            StringBuilder splitBuilder = new();
            StringBuilder builder = new();
            for (int i = 0; i < split.Length; i++) {
                if (i != 0) {
                    builder.Append('/');
                    splitBuilder.Append('/');
                }
                splitBuilder.Append(split[i]);
                if (split[i].Contains('<')) {
                    string fullGeneric = split[i];
                    while (fullGeneric.Count('<') != fullGeneric.Count('>')) {
                        i++;
                        fullGeneric += "/" + split[i];
                    }
                    var mainGeneric = fullGeneric.Split('<')[0];
                    var genericSplit = fullGeneric[(mainGeneric.Length + 1)..].TrimEnd('>').Split(',');
                    StringBuilder genericBuilder = new(RemapTypeByFullName(mainGeneric, out var _) + "<");
                    for (int j = 0; j < genericSplit.Length; j++) {
                        if (j != 0) genericBuilder.Append(',');
                        genericBuilder.Append(RemapTypeByFullName(genericSplit[j], out var _));
                    }
                    genericBuilder.Append('>');
                    builder.Append(genericBuilder);
                } else {
                    lastClass = mappings.Classes.Where(cls => cls.ClassFullNameA == splitBuilder.ToString()).SingleOrNull();
                    builder.Append(lastClass?.ClassNameB ?? split[i]);
                }
            }
            return builder.ToString();
        }

        private TypeReference GetMainType(TypeReference type) {
            if (type.IsGenericParameter)
                throw new Exception($"Attempted to get main type of generic parameter `{type.FullName}`!");

            if (type.IsGenericInstance || type.IsArray || type.IsByReference || type.IsPointer)
                return GetMainType(type.GetElementType());

            return type;
        }

        private ClassMapping FindType(TypeReference type) {
            type = GetMainType(type); // Ignore generics, array types, reference types, etc
            return mappings.Classes.Where(cls => cls.ClassFullNameA == type.GetPatchFullName()).SingleOrNull();
        }

        private MethodMapping FindMethod(MethodReference method, string nameOverride = null) {
            nameOverride ??= method.Name;
            // TODO: generic params stripped when matching method signatures due to Cecil handling generic instance method references strangely
            // probably not ideal, but maybe it's fine?
            return FindType(method.DeclaringType)?.Methods.Where(m => {
                if (m.MethodNameA != nameOverride || m.ArgumentTypeFullNamesA.Count != method.Parameters.Count || m.GenericParameters.Count != method.GenericParameters.Count)
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
    }

    private class NamedRemapper(Dictionary<string, string> mappings) : IRemapper {
        public string RemapField(FieldReference field)
            => GetNamedForIntermediary(field.Name, field.DeclaringType);

        public string RemapFieldByTypeAndName(string declaringTypeFullName, string Name) {
            return GetNamedForIntermediary(Name, null);
        }

        public string RemapGeneric(GenericParameter generic)
            => GetNamedForIntermediary(generic.Name,
                generic.Type == GenericParameterType.Method
                    ? generic.DeclaringMethod.DeclaringType
                    : generic.DeclaringType);

        public string RemapMethod(MethodReference method) {
            if (method.Name.StartsWith("orig_"))
                return "orig_" + GetNamedForIntermediary(method.Name[5..], method.DeclaringType);
            return GetNamedForIntermediary(method.Name, method.DeclaringType);
        }

        public string RemapMethodByTypeAndName(string declaringTypeFullName, string Name, string[] methodParams = null) {
            return GetNamedForIntermediary(Name.Split('<')[0], null);
        }

        public string RemapMethodParam(ParameterReference param, MethodReference method)
            => GetNamedForIntermediary(param.Name, method.DeclaringType);

        public string RemapType(TypeReference type) {
            if (type.GetPatchFullName() != type.FullName) {
                if (type.Name.StartsWith("patch_")) {
                    return "patch_" + GetNamedForIntermediary(type.Name[6..], type.DeclaringType);
                }

            }
            return GetNamedForIntermediary(type.Name, type.DeclaringType);
        }

        public string RemapTypeByFullName(string fullName) {
            var split = fullName.Split('/');
            StringBuilder builder = new();
            for (int i = 0; i < split.Length; i++) {
                if (i != 0) builder.Append('/');
                if (split[i].Contains('<')) {
                    string fullGeneric = split[i];
                    while (fullGeneric.Count('<') != fullGeneric.Count('>')) {
                        i++;
                        fullGeneric += "/" + split[i];
                    }
                    var mainGeneric = fullGeneric.Split('<')[0].TrimEnd('`');
                    var genericSplit = fullGeneric.Split('<')[1].TrimEnd('>').Split(',');
                    StringBuilder genericBuilder = new(RemapTypeByFullName(mainGeneric) + "<");
                    for (int j = 0; j < genericSplit.Length; j++) {
                        if (j != 0) genericBuilder.Append(',');
                        genericBuilder.Append(RemapTypeByFullName(genericSplit[j]));
                    }
                    genericBuilder.Append('>');
                    builder.Append(genericBuilder);
                } else
                    builder.Append(GetNamedForIntermediary(split[i], null));
            }
            return builder.ToString();
        }

        public string GetNamedForIntermediary(string intermediary, TypeReference owner) {
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

                if (method.GetCustomAttribute("MonoMod.MonoModILInject") is CustomAttribute ilAttrib) {
                    ilAttrib.MapAttributeAsMethodAt(0, remapper, type);
                }
                if (method.GetCustomAttribute("MonoMod.MonoModWrapOperation") is CustomAttribute woAttrib) {
                    woAttrib.MapAttributeAsMethodAt(0, remapper, type);
                    var woType = woAttrib.GetAttributeValue(1);
                    var target = woAttrib.GetAttributeValue(2);
                    if (!target.Contains('.') && (woType == "Call" || woType == "New" || woType.StartsWith("Field"))) {
                        var split = target.Split("::");
                        var woTargetType = remapper.RemapTypeByFullName(split[0]);
                        if (split.Length > 0) {
                            if (woType == "Call") {
                                woTargetType += "::" + remapper.RemapMethodByTypeAndName(split[0], split[1]);
                            } else {
                                woTargetType += "::" + remapper.RemapFieldByTypeAndName(split[0], split[1]);
                            }
                        }
                        woAttrib.SetAttributeValue(2, woTargetType);
                    }
                }
                // TODO: map locals
            }
            if (type.GetCustomAttribute("MonoMod.MonoModPatch") is CustomAttribute patchAttrib) {
                patchAttrib.SetAttributeValue(0, remapper.RemapTypeByFullName(patchAttrib.GetAttributeValue(0)));
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

    private static void SetAttributeValue(this CustomAttribute attribute, int index, string newValue) {
        attribute.ConstructorArguments[index] = new CustomAttributeArgument(attribute.ConstructorArguments[index].Type, newValue);
    }
    private static string GetAttributeValue(this CustomAttribute attribute, int index) {
        return (string)attribute.ConstructorArguments[index].Value;
    }

    private static void MapAttributeAsMethodAt(this CustomAttribute attribute, int index, IRemapper remapper, TypeDefinition patchDeclaringType) {
        string orig = attribute.GetAttributeValue(index);
        if (!orig.Contains(' ') && !orig.Contains(':') && !orig.Contains('(')) {
            var ptFlN = patchDeclaringType.GetPatchFullName();
            attribute.SetAttributeValue(index, remapper.RemapMethodByTypeAndName(ptFlN, orig));
            return;
        }
        var split = orig.Split(' ');
        var returnType = remapper.RemapTypeByFullName(split[0]);
        split = split[1].Split('(');
        var origMethod = split[0].Split("::");
        var origParams = split[1].TrimEnd(')').Split(',');
        if (origParams.Length == 1 && origParams[0] == "") origParams = [];
        var method = remapper.RemapTypeByFullName(origMethod[0]) + "::" + remapper.RemapMethodByTypeAndName(origMethod[0], origMethod[1], origParams);
        StringBuilder paramBuilder = new();
        for (int i = 0; i < origParams.Length; i++) {
            if (i != 0) paramBuilder.Append(',');
            paramBuilder.Append(remapper.RemapTypeByFullName(origParams[i]));
        }
        attribute.SetAttributeValue(index, returnType + " " + method + "(" + paramBuilder.ToString() + ")");
        return;
    }

    #endregion

    #region Remapping

    public static string PathToMappings = "modded/Mappings";
    public static string PathToIntermediary = "intermediary";
    public static string PathToNamed = "named";

    private static readonly Dictionary<Guid, string> ObfToIntermediaryMappingsPaths = new();
    private static readonly Dictionary<Guid, string> IntermediaryToNamedMappingsPaths = new();

    public static void LoadMappingsPaths(List<string> extraIntermediaryMappingsPaths = null, List<string> extraNamedMappingsPaths = null) {
        DirectoryInfo mappingsDirectory = Directory.CreateDirectory(PathToMappings);
        DirectoryInfo intermediaryDirectory = Directory.CreateDirectory(Path.Combine(PathToMappings, PathToIntermediary));
        DirectoryInfo namedDirectory = Directory.CreateDirectory(Path.Combine(PathToMappings, PathToNamed));

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
            if (!GuidUtils.TryParseMvidFromPath(path, out Guid mvid))
                mvid = GuidUtils.AsDeterministicGuid(Path.GetFileNameWithoutExtension(path));

            if (!mappings.TryAdd(mvid, path))
                Console.WriteLine($"Encountered duplicate mappings file {path} for MVID `{mvid}`, skipping...");
        }
    }

    private static void AddMappingsFiles(DirectoryInfo mappingsDirectory, Dictionary<Guid, string> mappings, bool recursive = false)
        => AddMappingsFiles(mappingsDirectory.GetFiles("*", new EnumerationOptions { RecurseSubdirectories = recursive }).Select(file => file.FullName).ToList(), mappings);

    private static bool TryLoadObfToIntermediaryMappings(AssemblyDefinition assembly, out Mappings mappings, bool logConsoleNormal = true) {
        mappings = new Mappings();

        Guid mvid = assembly.GetMvid();
        if (ObfToIntermediaryMappings.TryGetValue(mvid, out mappings)) {
            if (logConsoleNormal) Console.WriteLine("Found valid intermediary mappings from cache.");
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

        if (logConsoleNormal) Console.WriteLine($"Found valid intermediary mappings: {Path.GetFileName(path)}");
        return true;

    fail:
        Console.WriteLine("-<!>- Failed to find valid intermediary mappings!");
        return false;
    }

    private static bool TryLoadIntermediaryToNamedMappings(out NamedMappings namedMappings, out Version mappingVersion, bool logConsoleNormal = true) {
        if (IntermediaryToNamedMappings != null) {
            mappingVersion = IntermediaryToNamedMappings.Item1;
            namedMappings = IntermediaryToNamedMappings.Item2;
            if (logConsoleNormal) Console.WriteLine("Found valid named mappings from cache.");
            return true;
        }

        Dictionary<string, string> mappings = [];
        Dictionary<string, Dictionary<int, string>> addedEnumVariants = [];

        mappingVersion = new();
        List<string> pathsForVersion = [];
        foreach (var pair in IntermediaryToNamedMappingsPaths) {
            string path = pair.Value;
            if (File.Exists(path)) {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length > 1 && lines[0].StartsWith("Mapping version: ")) {
                    var version = Version.Parse(lines[0].Split("Mapping version: ")[1]);
                    if (version > mappingVersion) {
                        pathsForVersion = [];
                        mappingVersion = version;
                    }
                    if (mappingVersion == version) {
                        pathsForVersion.Add(path);
                    }
                }
            }
        }
        foreach (var path in pathsForVersion) {
            string[] lines = File.ReadAllLines(path);

            for (int i = 1; i < lines.Length; i++) {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;
                if (!line.Contains(',')) {
                    Console.WriteLine($"Missing ',' at {line}");
                }

                string[] parts = line.Split(',');
                if (parts[0].Contains('.')) {
                    string[] enumVariantParts = parts[0].Split('.');
                    var enumName = enumVariantParts[0];
                    var enumVariantValueStr = enumVariantParts[1].Trim();
                    // why can int.Parse not just handle `0x` prefixes itself? smh
                    int enumVariantValue = enumVariantValueStr.StartsWith("0x") ? int.Parse(enumVariantValueStr[2..], NumberStyles.HexNumber) : int.Parse(enumVariantValueStr);
                    if (!addedEnumVariants.ContainsKey(enumName))
                        addedEnumVariants[enumName] = [];
                    addedEnumVariants[enumName][enumVariantValue] = parts[1];
                } else {
                    mappings[parts[0]] = parts[1];
                }
            }
            if (logConsoleNormal) Console.WriteLine($"Found valid named mappings: {Path.GetFileName(path)}");
        }

        namedMappings = new NamedMappings(mappings, addedEnumVariants);
        IntermediaryToNamedMappings = new(mappingVersion, namedMappings);

        if (mappings.Count == 0) {
            Console.WriteLine("-<!>- Failed to find valid named mappings!");
            return false;
        }

        return true;
    }

    public static Version GetNamedMappingsVersion() {
        TryLoadIntermediaryToNamedMappings(out var _, out var version, false);
        return version;
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

    public static void RemapToNamed(AssemblyDefinition intermediaryAssemblyDef, bool logConsoleNormal = true) {
        if (!TryLoadIntermediaryToNamedMappings(out NamedMappings namedMappings, out var _, logConsoleNormal))
            return;

        var allTypes = StringDumping.CollectNestedTypes(intermediaryAssemblyDef.MainModule.Types);

        EnumVariantInjection.AddEnumVariants(allTypes, namedMappings.AddedEnumVariants);

        DoRemap(new NamedRemapper(namedMappings.Mappings), allTypes);
    }


    #endregion

    #region Backmapping

    public static void RemapNamedToIntermediary(AssemblyDefinition namedAssemblyDef, bool logConsoleNormal = true) {

        Globals.TryLoadLightning(out var origAssembly, false);
        if (!TryLoadNamedToIntermediaryMappings(origAssembly, out Backmapper backmapper, logConsoleNormal))
            return;

        var allTypes = StringDumping.CollectNestedTypes(namedAssemblyDef.MainModule.Types);

        EnumVariantInjection.RemoveEnumVariants(allTypes, backmapper.RemovedEnumVariants);

        DoRemap(new IntermediaryRemapper(backmapper.Mappings), allTypes);
    }

    private static bool TryLoadNamedToIntermediaryMappings(AssemblyDefinition originalAssembly, out Backmapper backmapper, bool logConsoleNormal = true) {

        Guid mvid = originalAssembly.GetMvid();
        if (NamedToIntermediaryMappings.TryGetValue(mvid, out backmapper)) {
            if (logConsoleNormal) Console.WriteLine("Found valid backmapper from cache.");
            return true;
        }

        TryLoadObfToIntermediaryMappings(originalAssembly, out Mappings oToI, logConsoleNormal);
        TryLoadIntermediaryToNamedMappings(out NamedMappings iToN, out var _, logConsoleNormal);
        var namedRemapper = new NamedRemapper(iToN.Mappings);

        Dictionary<string, string> intermediaryTypeNamePairs = [];
        foreach (var classMapping in oToI.Classes) {
            intermediaryTypeNamePairs.Add(classMapping.ClassFullNameA, classMapping.ClassNameB);
        }
        var mappings = new Mappings {
            NamespaceA = "named",
            NamespaceB = oToI.NamespaceB,
            nextClassIndex = oToI.nextClassIndex,
            nextEnumIndex = oToI.nextEnumIndex,
            nextInterfaceIndex = oToI.nextInterfaceIndex,
            nextStructIndex = oToI.nextStructIndex,
            nextDelegateIndex = oToI.nextDelegateIndex,
            nextMethodIndex = oToI.nextMethodIndex,
            nextParamIndex = oToI.nextParamIndex,
            Classes = [],
        };
        foreach (var classMapping in oToI.Classes){
            ClassMapping newClM = new() {
                ClassFullNameA = MapNameFromObfuscatedToNamed(classMapping.ClassFullNameA, intermediaryTypeNamePairs, namedRemapper, null),
                ClassNameB = classMapping.ClassNameB,
                Methods = [],
                Fields = [],
                GenericParameters = [],
            };
            foreach (var methodMapping in classMapping.Methods) {
                MethodMapping newMM = new() {
                    MethodNameA = MapNameFromObfuscatedToNamed(methodMapping.MethodNameA, methodMapping.MethodNameB, namedRemapper),
                    MethodNameB = methodMapping.MethodNameB,
                    ReturnTypeFullNameA = MapNameFromObfuscatedToNamed(methodMapping.ReturnTypeFullNameA, intermediaryTypeNamePairs, namedRemapper, classMapping.GenericParameters),
                    ArgumentTypeFullNamesA = [],
                    GenericParameters = [],
                    Parameters = [],
                };
                foreach (var argumentType in methodMapping.ArgumentTypeFullNamesA) {
                    newMM.ArgumentTypeFullNamesA.Add(MapNameFromObfuscatedToNamed(argumentType, intermediaryTypeNamePairs, namedRemapper, classMapping.GenericParameters));
                }
                foreach (var parameter in methodMapping.Parameters) {
                    newMM.Parameters.Add(new() {
                        ParameterNameA = MapNameFromObfuscatedToNamed(parameter.ParameterNameA, parameter.ParameterNameB, namedRemapper),
                        ParameterNameB = parameter.ParameterNameB,
                    });
                }
                foreach (var generic in methodMapping.GenericParameters) {
                    newMM.GenericParameters.Add(new() {
                        GenericNameA = MapNameFromObfuscatedToNamed(generic.GenericNameA, generic.GenericNameB, namedRemapper),
                        GenericNameB = generic.GenericNameB,
                    });
                }
                newClM.Methods.Add(newMM);
            }
            foreach (var fieldMapping in classMapping.Fields) {
                FieldMapping newFldM = new() {
                    FieldNameA = MapNameFromObfuscatedToNamed(fieldMapping.FieldNameA, fieldMapping.FieldNameB, namedRemapper),
                    FieldNameB = fieldMapping.FieldNameB,
                };
                newClM.Fields.Add(newFldM);
            }
            foreach (var generic in classMapping.GenericParameters) {
                GenericParameterMapping newGenericM = new() {
                    GenericNameA = MapNameFromObfuscatedToNamed(generic.GenericNameA, generic.GenericNameB, namedRemapper),
                    GenericNameB = generic.GenericNameB,
                };
                newClM.GenericParameters.Add(newGenericM);
            }
            mappings.Classes.Add(newClM);
        }
        backmapper = new Backmapper(mappings, iToN.AddedEnumVariants);
        NamedToIntermediaryMappings[mvid] = backmapper;

        //DataSerializer.SetMultilineFormat(true);
        //mappings.Serialize(Path.Combine(PathToMappings, "namedtointerm.json"));

        return false;
    }

    private static string MapNameFromObfuscatedToNamed(string obfuscated, string intermediary, NamedRemapper remapper) {
        return intermediary != null ? remapper.GetNamedForIntermediary(intermediary, null) : obfuscated;
    }
    private static string MapNameFromObfuscatedToNamed(string obfuscated, Dictionary<string, string> intermediaryTypeNamePairs, NamedRemapper remapper, List<GenericParameterMapping> generics) {
        if (obfuscated == null) return null;
        var splits = obfuscated.Split("#=");
        if (splits.Length == 1) return obfuscated;

        StringBuilder builder = new(splits[0]);
        string storage = "";
        if (builder.ToString().EndsWith('/')) {
            storage = builder.ToString().Split(['/', ',', '\u0026', '\u003C', '[', ']'])[^2] + "/";
        }
        foreach (var item in splits[1..]) {
            string key;
            if (item.Split("==").Length > 1) {
                key = "==";
            } else if (item.Split("=").Length > 1) {
                key = "=";
            } else {
                if (item.EndsWith('/')) {
                    string origName2 = "#=" + item[..^1];
                    storage += origName2 + "/";
                    if (intermediaryTypeNamePairs.TryGetValue(storage[..^1], out var intermediary)) {
                        builder.Append(MapNameFromObfuscatedToNamed(intermediary, intermediary, remapper) + "/");
                        continue;
                    }
                } else if (intermediaryTypeNamePairs.TryGetValue(storage + "#=" + item, out var intermediary)) {
                    return builder.ToString() + MapNameFromObfuscatedToNamed(intermediary, intermediary, remapper);
                }
                throw new Exception(item);
            }
            string origName = "#=" + item.Split(key)[0] + key; // Split must have a count of 2.
            string name = storage + origName;
            if (item.Split(key)[1] == "/") {
                storage += "#=" + item;
            } else {
                storage = "";
            }

            string nameOrig = name;
            var generic = generics?.SingleOrDefault(generic => generic.GenericNameA == name, null);
            if (generic != null) {
                name = MapNameFromObfuscatedToNamed(generic.GenericNameB, generic.GenericNameB, remapper);
            } else if (intermediaryTypeNamePairs.TryGetValue(name, out var intermediary)) {
                name = MapNameFromObfuscatedToNamed(intermediary, intermediary, remapper);
            }
            if (name != nameOrig)
                builder.Append(name);
            else builder.Append(origName);
            builder.Append(item.Split(key)[1]);
        }
        return builder.ToString();
    }

    #endregion

    #region Xml

    public static XDocument MapXmlDocument(XDocument xml, bool logConsoleNormal = true) {
        TryLoadIntermediaryToNamedMappings(out var mappings, out var _, logConsoleNormal);
        var mapped = MapXmlDocument(xml, mappings);
        return mapped;
    }
    public static XDocument BackmapXmlDocument(XDocument xml, bool logConsoleNormal = true) {
        Globals.TryLoadLightning(out var origAssembly, false);
        TryLoadNamedToIntermediaryMappings(origAssembly, out var mappings, logConsoleNormal);
        var mapped = MapXmlDocument(xml, mappings.Mappings);
        return mapped;
    }
    private static XDocument MapXmlDocument(XDocument xml, Mappings mappings) {
        var remapped = RemapXmlAfter(xml, mappings, "member name=\"", true);
        return RemapXmlAfter(remapped, mappings, "cref=\"", false);
        // TODO "!:" expressions.
    }
    private static XDocument MapXmlDocument(XDocument xml, NamedMappings mappings) {
        string doc = xml.ToString();
        List<string> orderedKeys = []; 
        foreach (var pair in mappings.Mappings) {
            orderedKeys.Add(pair.Key);
        }
        orderedKeys.Sort();
        for (int i = orderedKeys.Count - 1; i >= 0; i--) {
            doc = doc.Replace(orderedKeys[i], mappings.Mappings[orderedKeys[i]]);
            // We'll just ignore that a few other miscellaneous things might get replaced.
            // It's a feature, not a bug!
        }
        return XDocument.Parse(doc);
    }

    private static XDocument RemapXmlAfter(XDocument xml, Mappings mappings, string after, bool enableParamMapping) {

        string doc = xml.ToString();
        var split1 = doc.Split(after);

        StringBuilder builder = new(split1[0]);
        foreach (var split in split1[1..]) {
            builder.Append(after);
            XmlDocItemType itemType;
            if (split.StartsWith("T:")) {
                builder.Append("T:");
                itemType = XmlDocItemType.Type;
            } else if (split.StartsWith("M:")) {
                builder.Append("M:");
                itemType = XmlDocItemType.Method;
            } else if (split.StartsWith("F:")) {
                builder.Append("F:");
                itemType = XmlDocItemType.Field;
            } else {
                builder.Append(split);
                continue;
            }
            var parsed = split.Split('"')[0][2..];

            string[] arguments = [];
            if (itemType == XmlDocItemType.Method) {
                var split2 = parsed.Split('(');
                if (split2.Length > 1) {
                    arguments = split2[1].TrimEnd(')').Split(',');
                }
                parsed = split2[0];
            }

            builder.Append(GetMappedXml(parsed.Split('.'), mappings, itemType, arguments, out var methodParamMapping, out var typeParamMapping));

            if (arguments.Length > 0) {
                builder.Append('(');

                for (int i = 0; i < arguments.Length; i++) {
                    builder.Append(GetMappedXml(arguments[i].Split('.'), mappings, XmlDocItemType.Type, null, out var _, out var _));
                    builder.Append(',');
                }
                builder.Remove(builder.Length - 1, 1);
                builder.Append(')');
            }
            string remainder = split[split.Split('"')[0].Length..];
            if (enableParamMapping && methodParamMapping.Count > 0) {
                remainder = RemapXmlParamsAfter(remainder, methodParamMapping, "param name=\"");
                remainder = RemapXmlParamsAfter(remainder, methodParamMapping, "paramref name=\"");
            }
            if (enableParamMapping && typeParamMapping.Count > 0) {
                remainder = RemapXmlParamsAfter(remainder, typeParamMapping, "typeparam name=\"");
                remainder = RemapXmlParamsAfter(remainder, typeParamMapping, "typeparamref name=\"");
            }
            builder.Append(remainder);

        }
        return XDocument.Parse(builder.ToString());
    }
    private static string RemapXmlParamsAfter(string remainder, Dictionary<string, string> paramMapping, string after) {
        var split1 = remainder.Split(after);
        StringBuilder builder = new(split1[0]);
        foreach (var split in split1[1..]) {
            builder.Append(after);
            var parsed = split.Split('"')[0];

            if (paramMapping.TryGetValue(parsed, out var mapped)) {
                builder.Append(mapped);
            } else builder.Append(parsed);

            builder.Append(split[split.Split('"')[0].Length..]);
        }
        return builder.ToString();
    }

    private static string GetMappedXml(string[] origTypes, Mappings mappings, XmlDocItemType type, string[] arguments, out Dictionary<string, string> methodParamMapping, out Dictionary<string, string> typeParamMapping) {
        methodParamMapping = [];
        typeParamMapping = [];
        StringBuilder builder = new();
        ClassMapping classMapping = null;
        for (int i = 0; i < origTypes.Length -1; i++) {
            builder.Append(GetMappedXmlTypeWithGenerics(origTypes[i], mappings, out classMapping));
            builder.Append('.');
        }
        string last = origTypes[^1];
        switch (type) {
            default:
            case XmlDocItemType.Type:
                if (classMapping != null) {
                    foreach (var param in classMapping.GenericParameters) {
                        typeParamMapping[param.GenericNameA] = param.GenericNameB;
                    }
                }
                builder.Append(GetMappedXmlTypeWithGenerics(last, mappings, out classMapping));
                if (classMapping != null) {
                    foreach (var param in classMapping.GenericParameters) {
                        typeParamMapping[param.GenericNameA] = param.GenericNameB;
                    }
                }
                break;
            case XmlDocItemType.Field:
                var mappingF = classMapping?.Fields.SingleOrDefault(fl => fl.FieldNameA == last, null);
                builder.Append(mappingF?.FieldNameB ?? last);
                if (classMapping != null) {
                    foreach (var param in classMapping.GenericParameters) {
                        typeParamMapping[param.GenericNameA] = param.GenericNameB;
                    }
                }
                break;
            case XmlDocItemType.Method:
                var mappingM = classMapping?.Methods.SingleOrDefault(md => {
                    bool isMatching = md.MethodNameA == last && md.Parameters.Count == arguments.Length;
                    if (isMatching) {
                        for (int i = 0; i < arguments.Length; i++) {
                            string substituted = XmlMethodArgumentRegex().Replace(md.ArgumentTypeFullNamesA[i].Replace(">", "}"), "{");
                            if (substituted != arguments[i]) return false;
                        }
                    }
                    return isMatching;
                }, null);
                builder.Append(mappingM?.MethodNameB ?? last);
                if (classMapping != null) {
                    foreach (var param in classMapping.GenericParameters) {
                        typeParamMapping[param.GenericNameA] = param.GenericNameB;
                    }
                }
                if (mappingM != null) {
                    foreach (var param in mappingM.Parameters) {
                        methodParamMapping[param.ParameterNameA] = param.ParameterNameB;
                    }
                    foreach (var param in mappingM.GenericParameters) {
                        typeParamMapping[param.GenericNameA] = param.GenericNameB;
                    }
                }
                break;
        }
        return builder.ToString();
    }
    private static string GetMappedXmlTypeWithGenerics(string orig, Mappings mappings, out ClassMapping classMapping) {

        string[] split = orig.Split('{');
        classMapping = mappings.Classes.SingleOrDefault(cl => cl.ClassFullNameA == split[0], null);
        StringBuilder builder = new(classMapping?.ClassNameB ?? split[0] );
        if (split.Length > 1) {
            builder.Append('{');
            split = split[1].TrimEnd('}').Split(',');
            foreach (var item in split) {
                builder.Append (GetMappedXml(item.Split('.'), mappings, XmlDocItemType.Type, null, out var _, out var _));
                builder.Append(',');
            }
            builder.Remove(builder.Length - 1, 1);
            builder.Append('}');
        }
        return builder.ToString();
    }

    private enum XmlDocItemType {
        None,
        Type,
        Field,
        Method,
    }

    [GeneratedRegex(@"`\d+<")]
    private static partial Regex XmlMethodArgumentRegex();

    #endregion
}
