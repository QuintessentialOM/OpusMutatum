using System.Diagnostics;

namespace OpusMutatum;
// Currently unused
public class RunDebugger {
    public string[] runArgs = [];
    public bool attachDebugger = false; // TODO: Find ways to attach VS and VSCode debuggers (optionaly ILSpy and dnSpy)

    public void BeforeProcessStart() {
    }
    public void HandleRunningProcess(Process p) {
    }
}
