using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpusMutatum.Merging;
public static class AssemblyDocumentation {
    public static string PathToDocumentation = "documentation";

    public static string[] GetDefaultFiles() {
        string dirPath = Path.Combine(Remapping.PathToMappings, PathToDocumentation);
        if (File.Exists(dirPath))
            return [.. Directory.GetFiles(dirPath).Where(file => Path.GetExtension(file) == ".xml")];
        Directory.CreateDirectory(dirPath);
        return [];
    }

    public static XDocument Merge(string[] dllPaths, string assemblyName) {
        return Merge([.. dllPaths.SelectMany(path => {
            try {
                return new XDocument[] { XDocument.Load(Path.ChangeExtension(path,".xml")) };
            } catch { }
            return [];
        })], assemblyName);
    }

    public static XDocument Merge(XDocument[] documents, string assemblyName) {
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
