using System.IO;

namespace OpusMutatum;

public static class ContentHandling {
    private static string PathToContent = "Content";
    private static string PathToPackedContent = "PackedContent";

    public static void CreateContentSymlinks() {
        string vanillaPathToContent = Path.Combine(Directory.GetCurrentDirectory(), PathToContent),
            vanillaPathToPackedContent = Path.Combine(Directory.GetCurrentDirectory(), PathToPackedContent);
        string moddedPathToContent = Path.Combine(Globals.PathToOutput, PathToContent),
            moddedPathToPackedContent = Path.Combine(Globals.PathToOutput, PathToPackedContent);

        try {
            // try to create symlinks to the Content and PackedContent folders
            if (!Directory.Exists(moddedPathToContent))
                Directory.CreateSymbolicLink(moddedPathToContent, vanillaPathToContent);
            if (!Directory.Exists(moddedPathToPackedContent))
                Directory.CreateSymbolicLink(moddedPathToPackedContent, vanillaPathToPackedContent);
        } catch {
            if (Globals.OperatingSystem != Globals.OS.Windows)
                throw;

            // else deep-copy them
            if (!Directory.Exists(moddedPathToContent))
                CopyDirectory(vanillaPathToContent, moddedPathToContent);
            if (!Directory.Exists(moddedPathToPackedContent))
                CopyDirectory(vanillaPathToPackedContent, moddedPathToPackedContent);
        }
    }

    private static void CopyDirectory(string src, string dst) {
        Directory.CreateDirectory(dst);

        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)));

        foreach (string dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetRelativePath(src, dir)));
    }
}
