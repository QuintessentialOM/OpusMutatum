using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace OpusMutatum;

public class ModMeta {

    public string ModId { get; set; } = "";

    [JsonConverter(typeof(VersionJsonConverter))]
    public Version Version { get; set; }
    public string ModPageURL { get; set; } = "";
    public string DLL { get; set; } = "";
    public string[] Authors { get; set; } = [];
    public string Icon { get; set; } = "";
    public string Mappings { get; set; } = "";
    public Dictionary<string, VersionRange> Dependencies { get; set; } = [];
    public string[] Conflicts { get; set; } = [];


    [JsonIgnore]
    public string PathToDirectory;
    [JsonIgnore]
    public string PathToArchive;
    [JsonIgnore]
    public bool HasDll;
}


[JsonConverter(typeof(VersionRangeJsonConverter))]
public class VersionRange {
    public bool InclusiveMin;
    public Version VersionMin;
    public bool InclusiveMax;
    public Version VersionMax;

    public bool Contains(Version version) =>
        (VersionMin == null || VersionMin < version || (InclusiveMin && VersionMin == version)) &&
        (VersionMax == null || VersionMax > version || (InclusiveMax && VersionMax == version));

    public override string ToString() {
        StringBuilder builder = new();
        builder.Append(InclusiveMin ? '[' : '(');
        if (VersionMin != null) builder.Append(VersionMin.ToString());
        builder.Append(',');
        if (VersionMax != null) builder.Append(VersionMax.ToString());
        builder.Append(InclusiveMax ? ']' : ')');
        return builder.ToString();
    }
}
