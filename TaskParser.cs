using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace OpusMutatum;

public static class TaskParser {

    static readonly string tasksFileName = "tasks.txt";
    static readonly string exampleFileData = "Tasks:\r\n* strings --onlyOnChange\r\n* intermediary --onlyOnChange\r\n* merge --onlyOnChange\r\n* run --readLogs\r\n\r\nAutoExit:\r\n  true";
    public static MutatumTasks ReadTasksFromFile() {

        string tasksFilePath = Path.Combine(Directory.GetCurrentDirectory(), tasksFileName);
        if (!File.Exists(tasksFilePath)) {
            Console.WriteLine("Didn't find config file at: " + tasksFilePath);
            Console.WriteLine("Generating default file.");
            using var file = File.CreateText(tasksFilePath);
            file.WriteLine(exampleFileData);
        }
        return FromFile(tasksFilePath);
    }

    private static MutatumTasks FromFile(string filePath) {
        MutatumTasks toReturn = new();

        using (StreamReader st = new(filePath)) {
            string line;
            ReadingMode readingMode = ReadingMode.None;
            bool isList = false;

            while ((line = st.ReadLine()) != null) {
                if (line == "") continue;
                line = line.Trim(new char[] { ' ' });
                if (line.StartsWith("#")) { readingMode = ReadingMode.None; continue; }

                if (line.StartsWith("*") && readingMode != ReadingMode.None) {
                    line = line.Substring(1).Trim(new char[] { ' ' });
                    isList = true;
                } else if (isList) readingMode = ReadingMode.None;

                if (readingMode == ReadingMode.None) {
                    isList = false;
                    if (line == "Tasks:") {
                        readingMode = ReadingMode.Tasks;
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
                tasks.tasks.Add(new Task(line));
                break;
            case ReadingMode.GameDir:
                tasks.gameDir = line;
                break;
            case ReadingMode.ModsDir:
                tasks.modsDir = line;
                break;
            case ReadingMode.MappingsDir:
                tasks.mappingDir = line;
                break;
            case ReadingMode.BoundVSProjects:
                tasks.boundVSProjects.Add(line);
                break;
            case ReadingMode.AutoExit:
                tasks.autoExit = line == "true";
                break;
            default:
                break;
        }
    }

    private enum ReadingMode {
        None,
        Tasks,
        GameDir,
        ModsDir,
        MappingsDir,
        BoundVSProjects,
        AutoExit
    }
}

public class MutatumTasks {
    public List<Task> tasks = [];

    public string gameDir = "./modded";
    public string modsDir = "./modded/Mods";
    public string mappingDir = "./modded/Mappings";

    public bool autoExit = false;

    public List<string> boundVSProjects;
}

public class Task {
    public Command command;
    public string[] args;

    public Task(string line) {
        string[] items = line.Trim().Split(' ');
        List<string> argsList = new List<string>();

        if (!commands.TryGetValue(items[0], out this.command)) throw new Exception("The command specified at: " + line + " is invalid");

        bool isMultiArg = false;
        string collectedArguments = "";
        for (int i = 1; i < items.Length; i++) {
            if (isMultiArg) {
                collectedArguments += " " + items[i];

                if (!items[i].EndsWith("\"")) continue;
                isMultiArg = false;
                argsList.Add(collectedArguments);
                continue;
            }
            if (items[i].StartsWith("\"") && !items[i].EndsWith("\"")) {
                collectedArguments = items[i];
                isMultiArg = true;
                continue;
            }
            argsList.Add(items[i]);
        }
        args = argsList.ToArray();
    }

    private static Dictionary<string, Command> commands = new Dictionary<string, Command>() {
        { "strings", Command.Strings },
        { "intermediary", Command.Intermediary },
        { "merge", Command.Merge },
        { "newMod", Command.NewMod },
        { "copy", Command.Copy },
        { "run", Command.Run }
    };
}

public enum Command {
    Strings,
    Intermediary,
    Merge,
    NewMod,
    Copy,
    Run
}
