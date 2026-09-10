using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace OpusMutatum;

public static class TaskParser {

    static readonly string tasksFileName = "tasks.txt";
    static readonly string exampleFileData = "Tasks:\r\n* strings --onlyOnChange\r\n* intermediary --onlyOnChange\r\n* merge --onlyOnChange --asNamed\r\n* run --readLogs\r\n\r\nAutoExit:\r\n  true";

    private static string tasksCloneTargetPath = "";
    public static MutatumTasks ReadTasksFromFile() {

        string tasksFilePath = Path.Combine(Directory.GetCurrentDirectory(), tasksFileName);
        if (!File.Exists(tasksFilePath)) {
            Console.WriteLine("Didn't find config file at: " + tasksFilePath);
            Console.WriteLine("Generating default file.");
            using var file = File.CreateText(tasksFilePath);
            file.WriteLine(exampleFileData);
        }
        var tasks = FromFile(tasksFilePath);
        if (tasksCloneTargetPath != "") CloneTasksTxt(tasksCloneTargetPath);
        return tasks;
    }

    private static MutatumTasks FromFile(string filePath) {
        MutatumTasks toReturn = new();

        using (StreamReader st = new(filePath)) {
            string line;
            ReadingMode readingMode = ReadingMode.None;
            bool isList = false;

            while ((line = st.ReadLine()) != null) {
                if (line == "") continue;
                line = line.Trim([' ']);
                if (line.StartsWith('#')) { continue; }

                if (line.StartsWith('*') && readingMode != ReadingMode.None) {
                    line = line[1..].Trim([' ']);
                    isList = true;
                } else if (isList) readingMode = ReadingMode.None;

                if (readingMode == ReadingMode.None) {
                    isList = false;
                    if (line == "Tasks:") {
                        readingMode = ReadingMode.Tasks;
                    } else if (line == "DevMods:") {
                        readingMode = ReadingMode.DevMods;
                    } else if (line == "GameDir:") {
                        readingMode = ReadingMode.GameDir;
                    } else if (line == "ModsDir:") {
                        readingMode = ReadingMode.ModsDir;
                    } else if (line == "MappingsDir:") {
                        readingMode = ReadingMode.MappingsDir;
                    } else if (line == "BoundVSProjects:") {
                        readingMode = ReadingMode.BoundVSProjects;
                    } else if (line == "AutoExit:") {
                        readingMode = ReadingMode.AutoExit;
                    } else if (line == "CopyTasksPath:") {
                        readingMode = ReadingMode.CopyTasksPath;
                    }
                } else {
                    ReadData(toReturn, readingMode, line.Trim());
                    if (!isList) readingMode = ReadingMode.None;
                }
            }
        }
        return toReturn;
    }

    private static void ReadData(MutatumTasks tasks, ReadingMode mode, string line) {
        switch (mode) {
            case ReadingMode.Tasks:
                tasks.Tasks.Add(new Task(line));
                break;
            case ReadingMode.DevMods:
                tasks.DevModIds.Add(line);
                break;
            case ReadingMode.GameDir:
                tasks.GameDir = line;
                break;
            case ReadingMode.ModsDir:
                tasks.ModsDir = line;
                break;
            case ReadingMode.MappingsDir:
                tasks.MappingsDir = line;
                break;
            case ReadingMode.BoundVSProjects:
                tasks.BoundVSProjects.Add(line);
                break;
            case ReadingMode.AutoExit:
                tasks.AutoExit = line == "true";
                break;
            case ReadingMode.CopyTasksPath:
                tasksCloneTargetPath = line;
                break;
            default:
                break;
        }
    }

    private static void CloneTasksTxt(string path) {
        if (Directory.Exists(path)) {
            path = Path.Combine(path, tasksFileName);
        } else {
            path = Path.ChangeExtension(path, ".txt");
        }
        if (File.Exists(path))
            File.Delete(path);
        string tasksFilePath = Path.Combine(Directory.GetCurrentDirectory(), tasksFileName);
        using var file = File.CreateText(path);
        using (StreamReader st = new(tasksFilePath)) {
            bool commentOut = false;
            bool isList = false;
            string line;
            while ((line = st.ReadLine()) != null) {

            Write:
                if (!commentOut) {
                    if (line.TrimStart(' ').StartsWith("CopyTasksPath:")) commentOut = true;

                    if (commentOut) {
                        file.WriteLine("# " + line.TrimStart(' '));
                    } else
                        file.WriteLine(line);
                } else if (line.TrimStart(' ').StartsWith('#')) {
                    file.WriteLine(line);
                } else if (line.TrimStart(' ') == "") {
                    file.WriteLine(line);
                    commentOut = false;
                    isList = false;
                } else if (line.TrimStart(' ').StartsWith('*')) {
                    file.WriteLine("#" + line.TrimStart(' ')[1..]);
                    isList = true;
                } else if (!isList) {
                    file.WriteLine("# " + line.TrimStart(' '));
                    commentOut = false;
                } else {
                    commentOut = false;
                    isList = false;
                    goto Write;
                }
            }
        }
    }

    private enum ReadingMode {
        None,
        Tasks,
        DevMods,
        GameDir,
        ModsDir,
        MappingsDir,
        BoundVSProjects,
        AutoExit,
        CopyTasksPath
    }
}

public class MutatumTasks {
    public List<Task> Tasks = [];

    public string GameDir = "./modded";
    public string ModsDir = "./modded/Mods";
    public string MappingsDir = "./modded/Mappings";

    public bool AutoExit = false;

    public List<string> DevModIds = [];
    public List<string> BoundVSProjects = []; // TODO: not just vs
}

public class Task {
    public Command Command;
    public string[] Args;

    public Task(string line) {
        string[] items = line.Trim().Split(' ');
        List<string> argsList = [];

        if (!Commands.TryGetValue(items[0], out this.Command)) throw new Exception("The command specified at: " + line + " is invalid");

        bool isMultiArg = false;
        string collectedArguments = "";
        for (int i = 1; i < items.Length; i++) {
            if (isMultiArg) {
                collectedArguments += " " + items[i];

                if (!items[i].EndsWith('\"')) continue;
                isMultiArg = false;
                argsList.Add(collectedArguments);
                continue;
            }
            if (items[i].StartsWith('\"') && !items[i].EndsWith('\"')) {
                collectedArguments = items[i];
                isMultiArg = true;
                continue;
            }
            argsList.Add(items[i]);
        }
        Args = [.. argsList];
    }

    private static readonly Dictionary<string, Command> Commands = new() {
        { "strings", Command.Strings },
        { "intermediary", Command.Intermediary },
        { "devMerge", Command.MergeDev },
        { "merge", Command.Merge },
        { "newMod", Command.NewMod },
        { "copy", Command.Copy },
        { "run", Command.Run }
    };
}

public enum Command {
    Strings,
    Intermediary,
    MergeDev,
    Merge,
    NewMod,
    Copy,
    Run
}
