using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using GenericParameter = Mono.Cecil.GenericParameter;
using MemberReference = Mono.Cecil.MemberReference;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace OpusMutatum {

	public static class OpusMutatum {
		static string PathToOutput = "modded";
		static string PathToTemporaryOutput = "modded/temp";

		// For intermediary or devExe
		static string PathToLightning = "Lightning.exe";
		static string PathToIntermediaryLightning = "IntermediaryLightning.exe";
		static string PathToModdedLightning = "ModdedLightning.exe";

		// for merge
		static string PathToQuintessential = "Quintessential.dll";

		// for strings
		static string StringDeobfName = null;
		static string StringDeobfIntermediaryName = "method_131";

		// for coreification
		static string PathToCoreifier = "Coreifier.dll";
		static Assembly CoreifierAssembly;
		static MethodInfo CoreifierEntryPoint;

		static string[] QuintessentialSystemLibs = []; // TODO: what goes in here? do we even need this?
		static string[] MonoSystemLibs = ["mscorlib.dll", "Mono.Posix.dll", "Mono.Security.dll"];
		static string[] MonoConfigFiles = ["monoconfig", "monomachineconfig"];

		// for mappings
		static string PathToMappings = "Mappings";
		static List<string> IntermediaryToNamedMappingPaths = new List<string>();
		static List<string> ObfToIntermediaryMappingPaths = new List<string>();
		static List<string> StringsPaths = new List<string>();

		static AssemblyDefinition LightningAssemblyDef, IntermediaryLightningAssemblyDef, ModdedLightningAssemblyDef;

		static Mappings ObfToIntermediaryMappings;
		static Dictionary<string, string> IntermediaryToNamedMappings = new Dictionary<string, string>();
		static Dictionary<int, string> Strings = new Dictionary<int, string>();

		static bool AutoExit = false;
		private static bool NamedOut = false;
		// OS enum, since Linux and Mac are different
		public enum OS {
			Windows,
			Linux,
			Mac
		};

		public static OS OpSystem = OS.Windows;

		static void Main(string[] args) {
			ArgumentParsingMode current = ArgumentParsingMode.Argument;
			RunAction action = RunAction.Setup;

			switch(Environment.OSVersion.Platform)
			{
				case PlatformID.Win32NT:
				case PlatformID.Win32S:
				case PlatformID.Win32Windows:
				case PlatformID.WinCE:
					OpSystem = OS.Windows;
					break;
				case PlatformID.MacOSX:
					OpSystem = OS.Mac;
					break;
				default:
					OpSystem = OS.Linux;
					break;
			};

			foreach(var arg in args) {
				switch(current) {
					case ArgumentParsingMode.Argument:
						// check if its "run", "strings", "intermediary", merge", "coreify", "setup", "devExe", "quintDevExe"
						// or "--mappings", "--intermediary", "--strings", "--lightning", "--monomod", "--intermediaryPath", "--linux", "--mac", --"win"
						if (arg.Equals("run"))
							action = RunAction.Run;
						else if(arg.Equals("strings"))
							action = RunAction.Strings;
						else if(arg.Equals("intermediary"))
							action = RunAction.Intermediary;
						else if(arg.Equals("merge"))
							action = RunAction.Merge;
						else if (arg.Equals("coreify"))
							action = RunAction.Coreify;
						else if(arg.Equals("setup"))
							action = RunAction.Setup;
						else if(arg.Equals("devExe"))
							action = RunAction.DevExe;
						else if (arg.Equals("quintDevExe"))
							action = RunAction.QuintDevExe;
						else if(arg.Equals("--mappings"))
							current = ArgumentParsingMode.IntermediaryToNamedMappingPath;
						else if(arg.Equals("--intermediary"))
							current = ArgumentParsingMode.ObfToIntermediaryMappingPath;
						else if(arg.Equals("--strings"))
							current = ArgumentParsingMode.StringsPath;
						else if(arg.Equals("--lightning"))
							current = ArgumentParsingMode.LightningPath;
						else if(arg.Equals("--stringdeobfname"))
							current = ArgumentParsingMode.StringDeobfName;
						else if(arg.Equals("--stringdeobfintname"))
							current = ArgumentParsingMode.StringDeobfIntermediaryName;
						else if(arg.Equals("--linux"))
							OpSystem = OS.Linux;
						else if(arg.Equals("--mac"))
							OpSystem = OS.Mac;
						else if(arg.Equals("--win"))
							OpSystem = OS.Windows;
						else if (arg.Equals("--autoExit"))
							AutoExit = true;
						else if (arg.Equals("--namedExe"))
							NamedOut = true;
						break;
					case ArgumentParsingMode.IntermediaryToNamedMappingPath:
						IntermediaryToNamedMappingPaths.Add(arg);
						current = ArgumentParsingMode.Argument;
						break;
					case ArgumentParsingMode.ObfToIntermediaryMappingPath:
						ObfToIntermediaryMappingPaths.Add(arg);
						current = ArgumentParsingMode.Argument;
						break;
					case ArgumentParsingMode.StringsPath:
						StringsPaths.Add(arg);
						current = ArgumentParsingMode.Argument;
						break;
					case ArgumentParsingMode.LightningPath:
						PathToLightning = arg;
						current = ArgumentParsingMode.Argument;
						break;
					case ArgumentParsingMode.StringDeobfName:
						StringDeobfName = arg;
						current = ArgumentParsingMode.Argument;
						break;
					case ArgumentParsingMode.StringDeobfIntermediaryName:
						StringDeobfIntermediaryName = arg;
						current = ArgumentParsingMode.Argument;
						break;
					default:
						Console.WriteLine("Invalid argument \"" + arg + "\"!");
						break;
				}
			}

			if(StringsPaths.Count == 0) {
				StringsPaths.Add(Path.Combine(PathToOutput, "StringDumping/out.csv"));
				StringsPaths.Add(Path.Combine(PathToOutput, "out.csv"));
			}

			DirectoryInfo mappingsDirectory = Directory.CreateDirectory(Path.Combine(PathToOutput, PathToMappings));
			if (ObfToIntermediaryMappingPaths.Count == 0) {
				foreach (var path in mappingsDirectory.GetFiles()) {
					ObfToIntermediaryMappingPaths.Add(path.FullName);
				}
			}
			if (IntermediaryToNamedMappingPaths.Count == 0) {
				foreach (var path in mappingsDirectory.GetFiles()) {
					IntermediaryToNamedMappingPaths.Add(path.FullName);
				}
			}

			HandleSetup();

			try {
				switch(action) {
					case RunAction.Strings:
						HandleStrings();
						break;
					case RunAction.Intermediary:
						HandleIntermediary();
						break;
					case RunAction.Merge:
						HandleMerge();
						break;
					case RunAction.Coreify:
						HandleCoreify();
						break;
					case RunAction.Setup:
						HandleCoreify();
						HandleStrings();
						HandleIntermediary();
						if (NamedOut) {
							HandleQuintDevExe();
						}
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
			} catch(Exception e) {
				Console.WriteLine("Error executing task:");
				Console.WriteLine(e.ToString());
			}
			Console.WriteLine("Done.");
			// keep command line open
			if (!AutoExit) Console.ReadKey();

			HandleCleanup();
		}

		static void HandleSetup() {
			Directory.CreateDirectory(PathToOutput);
			Directory.CreateDirectory(PathToTemporaryOutput);
		}

		static void HandleCleanup() {
			Directory.Delete(PathToTemporaryOutput, true);
		}

		static void HandleRun() {
			// just run MONOMODDED_IntermediaryLightning.exe
		}

		static void FindStringDeobfMethod(MethodDefinition mainMethod) {
			if (StringDeobfName == null) {
				if (mainMethod.Body != null && mainMethod.Body.Instructions != null) {
					var candidateMethods = new HashSet<string>();
					foreach (var instr in mainMethod.Body.Instructions) {
						if (instr.Operand is MethodReference methodRef && methodRef.Resolve() != null) {
							var method = methodRef.Resolve();
							// deobf method should be a static method of signature (int) => string
							if (method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.Int32" && method.ReturnType.FullName == "System.String") {
								candidateMethods.Add($"{method.DeclaringType.Name}.{method.Name}");
							}
						}
					}
					// fail unless we found exactly one match
					if (candidateMethods.Count == 1){
						StringDeobfName = candidateMethods.Single();
						return;
					}
				}
				throw new Exception("Failed to find string deobf method");
			}
		}

		static void HandleStrings() {
			Console.WriteLine("Dumping strings...");
			if (!LoadLightning()) return;
			var module = LightningAssemblyDef.MainModule;
			var mainMethod = module.EntryPoint;
			FindStringDeobfMethod(mainMethod);
			var ssplit = StringDeobfName.Split('.');
			var parse = module.FindMethod(ssplit[0], ssplit[1]);

			Console.WriteLine("Finding keys...");
			// get all the keys this way
			var refs = new List<Instruction>();
			IMetadataScope mscorlibScope = null;
			foreach(var type in CollectNestedTypes(LightningAssemblyDef.MainModule.Types)) {
				if(type == null) continue;
				foreach (var method in type.Methods) {
					if(method != null && method.HasBody && method.Body != null && method.Body.Instructions != null) {
						foreach (var instr in method.Body.Instructions) {
							if(instr == null) continue;

							if(instr.OpCode.Code == Code.Call && instr.Operand is MethodReference operand && operand.Resolve() != null && operand.Resolve().Equals(parse))
								refs.Add(instr);
							// Find the mscorlib scope while we're here - maybe there's an easier way, but we're already searching through all the instructions regardless so it's fine
							if (mscorlibScope == null && instr.Operand is MethodReference methodRef
									&& methodRef.DeclaringType.Scope.Name == "mscorlib" && methodRef.DeclaringType.Scope.MetadataScopeType == MetadataScopeType.AssemblyNameReference) {
								mscorlibScope = methodRef.DeclaringType.Scope;
							}
						}
					}
				}
			}

			var keys = new HashSet<int>();

			foreach(var instr in refs)
				keys.Add((int)instr.Previous.Operand);

			Console.WriteLine($"Found {keys.Count} string keys");

			var proc = mainMethod.Body.GetILProcessor();

			var stringType = module.TypeSystem.String;
			var voidType = module.TypeSystem.Void;

			// Manual construction of a bunch of type and method references because Cecil is jank
			var concat = new MethodReference("Concat", stringType, stringType);
			for (int i = 0; i < 3; i++) concat.Parameters.Add(new ParameterDefinition(stringType));

			var streamWriterType = new TypeReference("System.IO", "StreamWriter", module, mscorlibScope);
			var textWriterType = new TypeReference("System.IO", "TextWriter", module, mscorlibScope);

			var streamWriterConstructor = new MethodReference(".ctor", voidType, streamWriterType) {
				HasThis = true
			};
			streamWriterConstructor.Parameters.Add(new ParameterDefinition(stringType));
			streamWriterConstructor = module.ImportReference(streamWriterConstructor);

			var writeLine = new MethodReference("WriteLine", voidType, textWriterType) {
				HasThis = true
			};
			writeLine.Parameters.Add(new ParameterDefinition(stringType));
			writeLine = module.ImportReference(writeLine);

			var dispose = new MethodReference("Close", voidType, textWriterType) {
				HasThis = true
			};
			dispose = module.ImportReference(dispose);

			Console.WriteLine("Creating string dumper...");
			proc.Clear();
			proc.Append(proc.Create(OpCodes.Ldstr, Path.Combine(PathToOutput, "./out.csv")));
			proc.Append(proc.Create(OpCodes.Newobj, streamWriterConstructor));
			foreach(var key in keys) {
				proc.Append(proc.Create(OpCodes.Dup));
				proc.Append(proc.Create(OpCodes.Ldstr, key.ToString()));
				proc.Append(proc.Create(OpCodes.Ldstr, "~,~"));
				proc.Append(proc.Create(OpCodes.Ldc_I4, key));
				proc.Append(proc.Create(OpCodes.Call, parse));
				proc.Append(proc.Create(OpCodes.Call, concat));
				proc.Append(proc.Create(OpCodes.Callvirt, writeLine));
			}
			proc.Append(proc.Create(OpCodes.Callvirt, dispose));
			proc.Append(proc.Create(OpCodes.Ret));

			string stringDumpingDir = Path.Combine(PathToOutput, "StringDumping");
			string stringDumperPath = Path.Combine(stringDumpingDir, "StringDumper.exe");
			Directory.CreateDirectory(stringDumpingDir);
			module.Write(stringDumperPath);

			// Yells at you if System and Steamworks aren't in the StringDumping directory
			if(OpSystem != OS.Windows && !File.Exists(Path.Combine(stringDumpingDir, "System.dll")) && !File.Exists(Path.Combine(stringDumpingDir, "Steamworks.NET.dll"))) {
				File.Copy("./System.dll", Path.Combine(stringDumpingDir, "System.dll"));
				File.Copy("./Steamworks.NET.dll", Path.Combine(stringDumpingDir, "Steamworks.NET.dll"));
			}
			Console.WriteLine("Running string dumper...");
			// run the string dumper automatically
			// Need to set the executable flag on unix
			if (Environment.OSVersion.Platform == PlatformID.Unix)
				File.SetUnixFileMode(stringDumperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			RunAndWait(stringDumperPath, "");
			Console.WriteLine();
		}

		static void HandleIntermediary() {
			// TODO: MonoMod relinking?
			Console.WriteLine("Generating intermediary EXE...");
			if (!LoadLightning()) return;
			if (!LoadStrings()) return;
			// take Lightning.exe, remap to Intermediary
			if (!LoadObfToIntermediaryMappings()) return;
			List<(Instruction, int)> stringsToBeInlined = new List<(Instruction, int)>();
			DoRemap(new IntermediaryRemapper(), CollectNestedTypes(LightningAssemblyDef.MainModule.Types),
				(mref, newName, instr) => {
					if(newName.Equals(StringDeobfIntermediaryName) && mref.Parameters.Count == 1)
						if(instr.Previous.OpCode == OpCodes.Ldc_I4)
							stringsToBeInlined.Add((instr, (int)instr.Previous.Operand));
				},
				type => {

					if(type.IsNested)
						type.IsNestedPublic = true;
					else
						type.IsPublic = true;

				});
			if(stringsToBeInlined.Count > 0)
				foreach(var stringFunc in stringsToBeInlined)
					if(Strings.ContainsKey(stringFunc.Item2)) {
						stringFunc.Item1.Previous.Set(OpCodes.Nop, null);
						stringFunc.Item1.Set(OpCodes.Ldstr, Strings[stringFunc.Item2]);
					} else
						Console.WriteLine($"Missing string for {stringFunc.Item2}");

			LightningAssemblyDef.Write(Path.Combine(PathToOutput, PathToIntermediaryLightning));
			Console.WriteLine();
		}

		static bool LoadLightning() {
			Console.WriteLine("Reading Lightning.exe...");

			try {
				LightningAssemblyDef = AssemblyDefinition.ReadAssembly(Path.Combine(PathToOutput, PathToLightning))
					?? throw new Exception("Failed to read assembly definition.");
			} catch (Exception e) {
				Console.WriteLine($"Failed to load Lightning.exe: {e.Message}");
				return false;
			}
			Console.WriteLine("Found Lightning executable: " + LightningAssemblyDef.FullName);
			return true;
		}

		static bool LoadModdedLightning() {
			Console.WriteLine("Reading modded Lightning.exe...");

			try {
				string moddedLightningPath = Path.Combine(PathToOutput, PathToModdedLightning);
				ModdedLightningAssemblyDef = AssemblyDefinition.ReadAssembly(moddedLightningPath)
					?? throw new Exception("Failed to read assembly definition.");
			} catch (Exception e) {
				Console.WriteLine($"Failed to load modded Lightning.exe: {e.Message}");
				return false;
			}

			Console.WriteLine("Found modded Lightning executable: " + ModdedLightningAssemblyDef.FullName);
			return true;
		}

		static bool LoadIntermediaryLightning() {
			Console.WriteLine("Reading intermediary Lightning.exe...");

			try {
				string intermediaryLightningPath = Path.Combine(PathToOutput, PathToIntermediaryLightning);
				IntermediaryLightningAssemblyDef = AssemblyDefinition.ReadAssembly(intermediaryLightningPath)
					?? throw new Exception("Failed to read assembly definition.");
			} catch (Exception e) {
				Console.WriteLine($"Failed to load intermediary Lightning.exe: {e.Message}");
				return false;
			}

			Console.WriteLine("Found intermediary Lightning executable: " + IntermediaryLightningAssemblyDef.FullName);
			return true;
		}

		static bool LoadCoreifier() {
			if (!File.Exists(PathToCoreifier)) {
				Console.WriteLine("Coreifier.dll not found");
				return false;
			}
			Console.WriteLine("Reading Coreifier.dll...");

			try {
				CoreifierAssembly = Assembly.LoadFrom(PathToCoreifier);
			} catch (Exception e) {
				Console.WriteLine($"Failed to load Coreifier.dll: {e.Message}");
				return false;
			}
			Console.WriteLine("Found Coreifier.dll: " + CoreifierAssembly.FullName);

			CoreifierEntryPoint = CoreifierAssembly?
				.GetType("Coreifier.Coreifier")?
				.GetMethod("Coreify", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string)], null);
			if (CoreifierEntryPoint == null) {
				Console.WriteLine("Failed to find coreifier entrypoint.");
				return false;
			}
			Console.WriteLine("Found coreifier entrypoint.");

			return true;
		}

		static bool LoadStrings() {
			if(StringsPaths.Count > 0) {
				foreach(var path in StringsPaths) {
					if(!File.Exists(path))
						continue;

					string[] lines = File.ReadAllLines(path);
					int lastIndex = 0;
					foreach(string line in lines) {
						string[] split = line.Split(new string[] { "~,~" }, StringSplitOptions.None);
						if(split.Length > 1) {
							// if we *can* split on this line, then we're definitely at the first line of a string
							try {
								lastIndex = int.Parse(split[0]);
								Strings[lastIndex] = split[1];
							} catch(FormatException) { }
						} else {
							// if this line isn't blank (or even if it is), then we're continuing a previous multi-line string, so append
							Strings[lastIndex] = Strings[lastIndex] + "\n" + line;
						}
					}
				}
				Console.WriteLine("Loaded " + Strings.Count + " strings.");
				return true;
			}

			return false;
		}

		public static void DoRemap(Remapper remapper, Collection<TypeDefinition> types, Action<MethodReference, string, Instruction> onMethodReference, Action<TypeDefinition> onTypeDefinition) {
			// Renames are deferred so that everything compares against old names, rather than a mixture of old and new names
			var deferredRenames = new Dictionary<MemberReference, string>();
			var deferredParamRenames = new Dictionary<ParameterReference, string>();
			foreach(var type in types) {
				if (type.IsGenericInstance || type.IsGenericParameter || type.IsArray || type.IsByReference || type.IsPointer)
					throw new Exception($"Expected to only be remapping main types, not generic instances/generic parameters/arrays/references/pointers, but got {type.FullName}");
				deferredRenames[type] = remapper.RemapType(type);
				onTypeDefinition(type);
				foreach (var method in type.Methods) {
					// rtspecialname is applied to constructors and operators
					if(!method.IsRuntimeSpecialName)
						deferredRenames[method] = remapper.RemapMethod(method);
					foreach(var generic in method.GenericParameters)
						deferredRenames[generic] = remapper.RemapGeneric(generic);
					foreach(var param in method.Parameters)
						deferredParamRenames[param] = remapper.RemapMethodParam(param, method);
					// references to members in classes with generic parameters don't get remapped automatically
					// so here we update those references ourself
					if(method.Body != null && method.Body.Instructions != null) {
						foreach(var instr in method.Body.Instructions) {
							if(instr != null && instr.Operand is MethodReference mref && !mref.IsWindowsRuntimeProjection) {
								if(mref.IsGenericInstance)
									mref = ((GenericInstanceMethod)mref).GetElementMethod();
								deferredRenames[mref] = remapper.RemapMethod(mref);
								// also take the oppurtunity to replace references to string decoder with the actual string
								onMethodReference(mref, deferredRenames[mref], instr);
							}

							if(instr != null && instr.Operand is FieldReference fref)
								deferredRenames[fref] = remapper.RemapField(fref);
						}
					}
					foreach(var attr in method.CustomAttributes)
						if(attr.HasConstructorArguments)
							foreach(var arg in attr.ConstructorArguments)
								if(arg.Type.Name.Equals("Type"))
									deferredRenames[arg.Value as TypeReference] = remapper.RemapType(arg.Value as TypeReference);
					// TODO: map locals
				}
				foreach(var field in type.Fields)
					deferredRenames[field] = remapper.RemapField(field);
				foreach(var generic in type.GenericParameters)
					deferredRenames[generic] = remapper.RemapGeneric(generic);
			}
			foreach (var pair in deferredRenames) {
				pair.Key.Name = pair.Value;
			}
			foreach (var pair in deferredParamRenames) {
				pair.Key.Name = pair.Value;
			}
		}

		static Collection<TypeDefinition> CollectNestedTypes(Collection<TypeDefinition> topLevel) {
			var types = new Collection<TypeDefinition>();
			foreach(var type in topLevel)
				VisitTypes(type, t => types.Add(t));
			return types;
		}

		static void VisitTypes(TypeDefinition top, Action<TypeDefinition> act) {
			act(top);
			foreach(var type in top.NestedTypes)
				VisitTypes(type, act);
		}

		static void HandleMerge() {
			if (File.Exists(PathToQuintessential)) {
				Console.WriteLine("Modding Lightning...");
				string moddedOutputPath = Path.Combine(PathToOutput, PathToModdedLightning);
				string moodedInputPath = Path.Combine(PathToOutput, NamedOut ? "QuintDevLightning.exe" : PathToIntermediaryLightning);

				using (MergeModder modder = new() {
					InputPath = moodedInputPath,
					OutputPath = moddedOutputPath,
					MissingDependencyThrow = false,
					LogVerboseEnabled = false
				}) {
					modder.Read();
					modder.ReadMod(PathToQuintessential);
					modder.MapDependencies();
					modder.AutoPatch();
					modder.Write(null, null);
					modder.Log("[Main] Done.");
				}

				//RunAndWait(Path.Combine(Directory.GetCurrentDirectory(), PathToMonoMod), $"{moodedInputPath} {PathToQuintessential} {moddedOutputPath}");
				//if (!File.Exists(moddedOutputPath)) {
				//	Console.WriteLine("Failed to mod!");
				//	return;
				//}
				//if (File.Exists(PathToHookGen)) {
				//	Console.WriteLine("Generating hooks...");
				//	RunAndWait(Path.Combine(Directory.GetCurrentDirectory(), PathToHookGen), moddedOutputPath);
				//	if (OpSystem != OS.Windows) {
				//		// Fixes the SDL2.dll not found error
				//		File.Copy("./Lightning.exe.config", Path.Combine(PathToOutput, Path.ChangeExtension(PathToModdedLightning, ".exe.config")), true);
				//		// These are the files you run to make the thing do the thing. yes
				//		File.Copy("./Lightning.bin.x86", Path.Combine(PathToOutput, Path.ChangeExtension(PathToModdedLightning, ".bin.x86")), true);
				//		File.Copy("./Lightning.bin.x86_64", Path.Combine(PathToOutput, Path.ChangeExtension(PathToModdedLightning, ".bin.x86_64")), true);
				//	}
				//}
			} else {
				Console.WriteLine("Quintessential not found, skipping merging.");
			}
			Console.WriteLine();
		}

		// TODO: setup native libs + symlinks aaaa
		static void HandleCoreify() {
			if (!File.Exists(PathToLightning)) {
				Console.WriteLine("Failed to find Lightning.exe");
				return;
			}

			if (!LoadCoreifier()) {
				Console.WriteLine("Unable to load coreifier, skipping coreification!");
				return;
			}

			Coreify(PathToLightning, Path.Combine(PathToOutput, PathToLightning));
			foreach (string path in MonoConfigFiles)
				File.Copy(path, Path.Combine(PathToOutput, path), overwrite: true);

			Console.WriteLine();
		}

		static void Coreify(string asmFrom, string asmTo = null, HashSet<string> convertedAsms = null) {
			asmTo ??= asmFrom;
			convertedAsms ??= [];
			if (!File.Exists(asmFrom)) {
				Console.WriteLine($"Unable to load assembly {asmFrom}, skipping coreification!");
				return;
			}
			if (!convertedAsms.Add(asmFrom))
				return;

			// coreify dependencies first
			string[] deps = GetAssemblyReferences(asmFrom).Keys.ToArray();
            if (deps.Contains("Coreifier"))
                return;

			foreach (string dep in deps) {
				string srcDepPath = Path.Combine(Path.GetDirectoryName(asmFrom)!, $"{dep}.dll");
				string dstDepPath = Path.Combine(Path.GetDirectoryName(asmTo)!, $"{dep}.dll");

				// recursively handle dependencies
				if (File.Exists(srcDepPath)) {
					if (!IsSystemLibrary(srcDepPath))
						// only coreify non-system deps
						Coreify(srcDepPath, dstDepPath, convertedAsms);
					else if (srcDepPath != dstDepPath)
						// otherwise copy the dep
						File.Copy(srcDepPath, dstDepPath, overwrite: true);
				} else if (File.Exists(dstDepPath) && !IsSystemLibrary(srcDepPath))
					// only coreify non-system deps
					Coreify(dstDepPath, convertedAsms: convertedAsms);
			}

			CoreifySingle(asmFrom, asmTo);
			return;

			static Dictionary<string, Version> GetAssemblyReferences(string path) {
				using FileStream fs = File.OpenRead(path);
				using PEReader pe = new(fs);

				MetadataReader meta = pe.GetMetadataReader();

				Dictionary<string, Version> deps = new();
				foreach (AssemblyReference asmRef in meta.AssemblyReferences.Select(meta.GetAssemblyReference))
					deps.TryAdd(meta.GetString(asmRef.Name), asmRef.Version);

				return deps;
			}

			static bool IsSystemLibrary(string file) {
				if (Path.GetExtension(file) != ".dll")
					return false;

				if (Path.GetFileName(file).StartsWith("System.") &&
					!QuintessentialSystemLibs.Contains(Path.GetFileName(file)))
					return true;

				return MonoSystemLibs.Any(name => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
			}
		}

		static void CoreifySingle(string asmFrom, string asmTo) {
			Console.WriteLine($"Converting {asmFrom} to .NET Core...");

			string asmTmp = Path.Combine(PathToTemporaryOutput, Path.GetFileName(asmTo));
			try {
				CoreifierEntryPoint.Invoke(null, [asmFrom, asmTmp]);
				File.Move(asmTmp, asmTo, overwrite: true);
			} finally {
				File.Delete(asmTmp);
				File.Delete(Path.ChangeExtension(asmTmp, "pdb"));
				File.Delete(Path.ChangeExtension(asmTmp, "mdb"));
			}
		}

		static void HandleDevExe() {
			// take ModdedLightning.exe, remap to named
			Console.WriteLine("Generating dev EXE...");

			if (!LoadModdedLightning()) return;
			if (!LoadIntermediaryToNamedMappings()) return;

			DoRemap(new NamedRemapper(), CollectNestedTypes(ModdedLightningAssemblyDef.MainModule.Types), (mref, newName, instr) => { }, typeDef => { });
			ModdedLightningAssemblyDef.Write(Path.Combine(PathToOutput, "DevLightning.exe"));
			Console.WriteLine();
		}
		static void HandleQuintDevExe() {
			// take IntermediaryLightning.exe, remap to named (no merged quintessential)
			Console.WriteLine("Generating dev quint EXE...");

			if (!LoadIntermediaryLightning()) return;
			if (!LoadIntermediaryToNamedMappings()) return;

			DoRemap(new NamedRemapper(), CollectNestedTypes(IntermediaryLightningAssemblyDef.MainModule.Types), (mref, newName, instr) => { }, typeDef => { });
			IntermediaryLightningAssemblyDef.Write(Path.Combine(PathToOutput, "QuintDevLightning.exe"));
			Console.WriteLine();
		}
		static void RunAndWait(string file, string param){
			Console.WriteLine("Running " + file);
			if(!File.Exists(file)) {
				Console.WriteLine("Failed to run " + file + ", file not found.");
				return;
			}
			Process process = new Process();
			// I'm unsure if Windows needs the file to have quotes
			// Just in case I'm leaving that in
			if(OpSystem == OS.Windows) {
				file = "\"" + file + "\"";
			}
			process.StartInfo.FileName = file;
			process.StartInfo.Arguments = param;
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.UseShellExecute = false;
			process.Start();
			process.WaitForExit();
			Console.WriteLine("Process output:");
			Console.WriteLine(process.StandardOutput.ReadToEnd());
			Console.WriteLine();
		}

		static string GetNamedForIntermediary(string intermediary, TypeReference owner) {
			if(!IntermediaryToNamedMappings.ContainsKey(intermediary))
				return intermediary;
			string name = IntermediaryToNamedMappings[intermediary];
			if(name.Contains(".")) {
				string[] split = name.Split('.');
				name = split[split.Length - 1];
			}

			return name;
		}

		class NamedRemapper : Remapper
		{
			public string RemapField(FieldReference field)
			{
				return GetNamedForIntermediary(field.Name, field.DeclaringType);
			}

			public string RemapGeneric(GenericParameter generic)
			{
				return GetNamedForIntermediary(generic.Name, generic.Type == GenericParameterType.Method ? generic.DeclaringMethod.DeclaringType : generic.DeclaringType);
			}

			public string RemapMethod(MethodReference method)
			{
				return GetNamedForIntermediary(method.Name, method.DeclaringType);
			}

			public string RemapMethodParam(ParameterReference param, MethodReference method)
			{
				return GetNamedForIntermediary(param.Name, method.DeclaringType);
			}

			public string RemapType(TypeReference type)
			{
				return GetNamedForIntermediary(type.Name, type.DeclaringType);
			}
		}

		static bool LoadIntermediaryToNamedMappings() {
			bool loaded = false;
			foreach(var path in IntermediaryToNamedMappingPaths) {
				if(!File.Exists(path))
					continue;

				string[] lines = File.ReadAllLines(path);
				if (lines.Length <= 1 || !lines[0].StartsWith("Mapping version: ")) continue;
				Console.WriteLine("Found valid named mappings: " + Path.GetFileName(path));

				for (int i = 1; i < lines.Length; i++) {
					var line = lines[i];

					if(string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
						continue;
					if(!line.Contains(","))
						Console.WriteLine($"Invalid line in {path}: \"{line}\", missing comma.");
					string[] parts = line.Split(',');
					IntermediaryToNamedMappings[parts[0]] = parts[1];
				}
				loaded = true;
			}

			if (!loaded)
				Console.WriteLine("Failed to find valid named mappings!");
			return loaded;
		}

		class IntermediaryRemapper : Remapper
		{
			public string RemapField(FieldReference field)
			{
				return FindType(field.DeclaringType)?.Fields.Where(f => f.FieldNameA == field.Name).SingleOrNull()?.FieldNameB ?? field.Name;
			}

			public string RemapGeneric(GenericParameter generic)
			{
				if (generic.Type == GenericParameterType.Method) {
					return FindMethod(generic.DeclaringMethod)?.GenericParameters.Where(g => g.GenericNameA == generic.Name).SingleOrNull()?.GenericNameB ?? generic.Name;
				} else {
					return FindType(generic.DeclaringType)?.GenericParameters.Where(g => g.GenericNameA == generic.Name).SingleOrNull()?.GenericNameB ?? generic.Name;
				}
			}

			public string RemapMethod(MethodReference method)
			{
				return FindMethod(method)?.MethodNameB ?? method.Name;
			}

			public string RemapMethodParam(ParameterReference param, MethodReference method)
			{
				return FindMethod(method)?.Parameters.Where(p => p.ParameterNameA == param.Name).SingleOrNull()?.ParameterNameB ?? param.Name;
			}

			public string RemapType(TypeReference type)
			{
				return FindType(type)?.ClassNameB ?? type.Name;
			}

			private TypeReference GetMainType(TypeReference type) {
				if (type.IsGenericParameter) throw new Exception($"Attempted to get main type of a generic parameter {type.FullName}");
				if (type.IsGenericInstance || type.IsArray || type.IsByReference || type.IsPointer) return GetMainType(type.GetElementType());
				return type;
			}

			private ClassMapping FindType(TypeReference type) {
				type = GetMainType(type); // Ignore generics, array types, reference types, etc
				return ObfToIntermediaryMappings.Classes.Where(cls => cls.ClassFullNameA == type.FullName).SingleOrNull();
			}

			private MethodMapping FindMethod(MethodReference method) {
				// TODO: generic params stripped when matching method signatures due to Cecil handling generic instance method references strangely
				// probably not ideal, but maybe it's fine?
				return FindType(method.DeclaringType)?.Methods.Where(m => {
					return m.MethodNameA == method.Name
							&& (m.ReturnTypeFullNameA.Split('`')[0] == method.ReturnType.FullName.Split('`')[0] || method.ReturnType.FullName.StartsWith("!"))
							&& m.ArgumentTypeFullNamesA.Count == method.Parameters.Count
							&& m.ArgumentTypeFullNamesA.Zip(method.Parameters, (a,b)=>(a,b)).All(pair => pair.b.ParameterType.FullName.StartsWith("!") || pair.a.Split('`', '<')[0] == pair.b.ParameterType.FullName.Split('`', '<')[0]);
				}).SingleOrNull();
			}
		}

		static bool LoadObfToIntermediaryMappings() {
			// TODO choose file based on Lightning.exe MVID
			foreach (var path in ObfToIntermediaryMappingPaths) {
				if(!File.Exists(path))
					continue;

				using (StreamReader file = File.OpenText(path)) {
					try {
						ObfToIntermediaryMappings = new JsonSerializer().Deserialize<Mappings>(new JsonTextReader(file));
					} catch {
						continue;
					}

					Console.WriteLine("Found valid intermediary mappings: " + Path.GetFileName(path));
					return true;
				}
			}

			Console.WriteLine("Failed to find valid intermediary mappings!");
			return false;
		}

		enum ArgumentParsingMode{
			Argument, IntermediaryToNamedMappingPath, ObfToIntermediaryMappingPath, StringsPath, LightningPath,
			StringDeobfName, StringDeobfIntermediaryName
		}

		enum RunAction{
			Run, Strings, Intermediary, Merge, Coreify, Setup, DevExe, QuintDevExe
		}
	}
}
