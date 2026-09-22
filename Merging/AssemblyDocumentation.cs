using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpusMutatum.Merging;
public static class AssemblyDocumentation {
    public static string PathToDocumentation = "documentation";

    private static bool WasInit = false;
    private static void Dump() {
        WasInit = true;
        if (!Directory.Exists(Path.Combine(Remapping.PathToMappings, "remapped")))
            Directory.CreateDirectory(Path.Combine(Remapping.PathToMappings, "remapped"));
        foreach (var filePath in GetDefaultFiles()) {
            try {
                var xml = XDocument.Load(filePath);
                xml = Remapping.BackmapXmlDocument(xml);
                xml.Save(Path.Combine(Remapping.PathToMappings, "remapped",Path.GetFileName(filePath)));
            } catch { }
        }
    }

    private static string[] GetDefaultFiles() {
        string dirPath = Path.Combine(Remapping.PathToMappings, PathToDocumentation);
        if (Directory.Exists(dirPath))
            return [.. Directory.GetFiles(dirPath).Where(file => Path.GetExtension(file) == ".xml")];
        Directory.CreateDirectory(dirPath);
        return [];
    }

    // TODO merge with knowledge gathered from assembly merging to replace patch-classes.
    public static XDocument Merge(OrderedDictionary<ModMeta, string> modPaths, string assemblyName) {
        //if (!WasInit) Dump();
        return Merge([..GetDefaultFiles().SelectMany(path => {
            try {
                return new XDocument[] { Remapping.MapXmlDocument(XDocument.Load(path), false) };
            } catch { }
            return [];
        }), .. modPaths.SelectMany(path => {
            try {
                XDocument doc = XDocument.Load(Path.ChangeExtension(path.Value, ".xml"));
                ConvertDocumentationMappingVersion(path, ref doc);
                return new XDocument[] { doc };
            } catch { }
            return [];
        })], assemblyName);
    }
    public static void ConvertDocumentationMappingVersion(KeyValuePair<ModMeta, string> modPair, ref XDocument xml) {
        string ver = modPair.Key.OldMappings ?? modPair.Key.Mappings;
        if (ver == Remapping.GetNamedMappingsVersion().ToString()) return;

        if (ver != "Intermediary") {
            if (ver != "") throw new Exception("Unknown mapping '" + ver + "' for xml documentation: " + Path.ChangeExtension(modPair.Value, ".xml"));
            // -TODO: Return here, the following code is only here to find bugs. It shouldn't make changes to the documentation
            return;
            //xml = Remapping.BackmapXmlDocument(xml, false);
        }
        xml = Remapping.MapXmlDocument(xml, false);
    }

    private static XDocument Merge(XDocument[] documents, string assemblyName) {
        XDocument result = new();
        result.Add(new XElement("doc"));
        result.Element("doc").Add(new XElement("assembly"));
        result.Element("doc").Element("assembly").Add(new XElement("name", assemblyName));
        result.Element("doc").Add(new XElement("members"));
        var members = result.Element("doc").Element("members");

        foreach (var doc in documents) {
            foreach (XElement element in doc.Element("doc").Element("members").Elements()) {

                var original = members.Elements().FirstOrDefault(e => e == element, null);
                if (original != null) {
                    foreach (var el in element.Elements()) {

                        var originalSub = original.Elements().FirstOrDefault(orig =>
                            orig.Name == element.Name &&
                            orig.Attribute("name") == element.Attribute("name"), null);
                        if (originalSub != null) {
                            originalSub.Value = (originalSub.Value.Trim() ?? "") + "\n" + (el.Value ?? "");
                        } else {
                            original.Add(el);
                        }
                    }
                } else {
                    members.Add(element);
                }
            }
        }
        return result;
    }
}
