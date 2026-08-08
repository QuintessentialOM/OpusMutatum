using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OpusMutatum;
public class RunDebugger {
    private const int logReadRetries = 4;

    public string[] runArgs = [];
    public bool readLogs = false;
    public bool attachDebugger = false; // TODO: Find ways to attach VS and VSCode debuggers (optionaly ILSpy and dnSpy)

    public void BeforeProcessStart() {
        if (readLogs && File.Exists(Path.Combine(Globals.PathToOutput, "log.txt"))) {
            File.Delete(Path.Combine(Globals.PathToOutput, "log.txt"));
        }
    }
    public void HandleRuningProcess(Process p) {
        AsyncStreamRedirector gameLog = null;

        var task = System.Threading.Tasks.Task.Run(async () => {
            if (readLogs) {
                for (int i = 0; i < logReadRetries; i++) {
                    Thread.Sleep(1000);
                    if (File.Exists(Path.Combine(Globals.PathToOutput, "log.txt"))) {
                        try {
                            FileStream logStream = new FileStream(Path.Combine(Globals.PathToOutput, "log.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            if (logStream != null && logStream.CanRead) {
                                Console.WriteLine();
                                Console.WriteLine("Reading Logs:");
                                gameLog = new AsyncStreamRedirector(logStream, Console.OpenStandardOutput(), false);
                            }

                        } catch (Exception e) {
                            Console.WriteLine();
                            Console.WriteLine("Failed to read logs:");
                            Console.WriteLine(e.ToString());
                        }
                        break;
                    }
                }
            }
        });
    }

    // Based on https://gist.github.com/antopor/5515bed636c3d99395ea
    class AsyncStreamRedirector {
        private readonly CancellationTokenSource cancellation;

        public AsyncStreamRedirector(Stream Source, Stream Sink, bool exitOnEmptySource = true) {
            cancellation = new CancellationTokenSource();

            var task = System.Threading.Tasks.Task.Run(async () => {
                byte[] buffer = new byte[16384];
                while (!cancellation.IsCancellationRequested) {

                    var inputCount = await Source.ReadAsync(buffer, 0, 16384, cancellation.Token);
                    if (!exitOnEmptySource) {
                        Thread.Sleep(2); // TODO: find a better way to wait for new available data 
                    } else if (inputCount <= 0) break;

                    await Sink.WriteAsync(buffer, 0, inputCount, cancellation.Token);
                    await Sink.FlushAsync(cancellation.Token);
                }
            }, cancellation.Token);
        }

        public void Cancel() {
            cancellation.Cancel();
        }
    }
}
