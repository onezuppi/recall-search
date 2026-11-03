using Recall.Journal;
using Recall.UsnService;

// Runs elevated (a scheduled task as SYSTEM, see scripts/install-usn-service.ps1) and answers change-journal queries
// from the per-user agents. It holds no state: the agent keeps its own cursor.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

using var source = new NativeJournalSource();
try
{
    await new JournalServer(new JournalCollector(source)).RunAsync(stop.Token);
    return 0;
}
catch (IOException e)
{
    Console.Error.WriteLine($"cannot start: {e.Message}"); // the pipe name is already owned by another process
    return 2;
}
