using MonoMod;
using System;

namespace OpusMutatum;
class MergeModder : MonoModder {
    private const string LogID = "Merger";
    public override void Log(string text)
        => Console.WriteLine($"[{LogID}] {text}");
    public override void LogVerbose(string text) { }

}
