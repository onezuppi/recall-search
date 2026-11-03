using System.Security.Principal;
using Recall.Journal;

namespace Recall.Core.Tests;

/// <summary>Needs administrator rights and an NTFS temp folder. Skipped otherwise; run the test project from an elevated shell to execute it.</summary>
public class NativeJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-usn-" + Guid.NewGuid().ToString("N"));

    public NativeJournalTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static bool IsAdmin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [SkippableFact]
    public void Overwrite_in_place_is_reported_with_its_full_path()
    {
        Skip.IfNot(IsAdmin(), "needs administrator rights");
        var volume = Path.GetPathRoot(_dir)![..2];
        using var source = new NativeJournalSource();
        var collector = new JournalCollector(source);
        var file = Path.Combine(_dir, "note.txt");
        File.WriteAllText(file, "first");

        var start = collector.Collect(new JournalRequest(volume, 0, 0, [_dir], [".txt"]), [_dir]);
        Assert.Equal(JournalStatus.NoCursor, start.Status);

        File.WriteAllText(file, "second version, longer"); // in place: the folder's own time does not change
        var changes = collector.Collect(new JournalRequest(volume, start.JournalId, start.NextUsn, [_dir], [".txt"]), [_dir]);

        Assert.Equal(JournalStatus.Ok, changes.Status);
        Assert.Contains(file, changes.Files!, StringComparer.OrdinalIgnoreCase);
    }
}
