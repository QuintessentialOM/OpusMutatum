using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpusMutatum;

public class VersionJsonConverter : JsonConverter<Version> {
    public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return Version.Parse(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options) {
        writer.WriteStringValue(value.ToString());
    }
}


public class VersionRangeJsonConverter : JsonConverter<VersionRange> {
    public override VersionRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        VersionRange toReturn = new();

        string[] versionRange = reader.GetString().Split(',');
        if (versionRange.Length == 1) {
            toReturn.InclusiveMin = true;
            toReturn.InclusiveMax = false;
            toReturn.VersionMax = null;
            try {
                toReturn.VersionMin = versionRange[0] == "" ? null : Version.Parse(versionRange[0]);
            } catch (Exception e) { throw new JsonException("Faliled to parse minimum version in range", e); }
            return toReturn;
        }
        if (versionRange.Length == 2) {

            char minInclusive = versionRange[0][0];
            if (minInclusive == '[') toReturn.InclusiveMin = true;
            else if (minInclusive == '(') toReturn.InclusiveMin = false;
            else throw new JsonException("Version range must begin with '[' for Min-Inclusive or '(' for Min-Exclusive.");
            versionRange[0] = versionRange[0][1..];

            char maxInclusive = versionRange[1][^1];
            if (maxInclusive == ']') toReturn.InclusiveMax = true;
            else if (maxInclusive == ')') toReturn.InclusiveMax = false;
            else throw new JsonException("Version range must end with ']' for Max-Inclusive or ')' for Max-Exclusive.");
            versionRange[1] = versionRange[1][..^1];

            try {
                toReturn.VersionMin = versionRange[0] == "" ? null : Version.Parse(versionRange[0]);
            } catch (Exception e) { throw new JsonException("Faliled to parse minimum version in range", e); }
            try {
                toReturn.VersionMax = versionRange[1] == "" ? null : Version.Parse(versionRange[1]);
            } catch (Exception e) { throw new JsonException("Faliled to parse maximum version in range", e); }

            return toReturn;
        }
        throw new JsonException("Invalid number of ',' characters in the VersionRange, multiple range sets are not supported.");
    }

    public override void Write(Utf8JsonWriter writer, VersionRange value, JsonSerializerOptions options) {
        writer.WriteStringValue(value.ToString());
        return;
    }
}

public class EnumConverter<T> : JsonConverter<T> where T : Enum {
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var value = reader.GetString();
        for (int i = 1; i < typeToConvert.GetFields().Length; i++) {
            if (value == (typeToConvert.GetFields()[i].Name)) return (T)(object)(i - 1);
        }
        throw new JsonException($"The provided Enum ('{typeToConvert.Name}') value was not recognised as a valid value.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) {
        writer.WriteStringValue(value.ToString());
    }
}
