using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Recall.Journal;

namespace Recall.UsnService;

/// <summary>
/// Named pipe server for the change journal. Any signed-in user may connect. Each request is checked under the caller's own
/// identity: a root is covered only when the caller can list it, so a user learns nothing about folders they cannot open.
/// </summary>
public sealed class JournalServer(JournalCollector collector)
{
    private readonly object _gate = new(); // the journal source shares volume handles between requests
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task RunAsync(CancellationToken ct)
    {
        var security = CreateSecurity();
        var options = PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance;
        var tasks = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var server = NamedPipeServerStreamAcl.Create(
                    JournalPipe.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, options, 0, 0, security);
                options = PipeOptions.Asynchronous;
                try { await server.WaitForConnectionAsync(ct); }
                catch { server.Dispose(); throw; }
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(Task.Run(() => ServeAsync(server, ct), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(tasks);
    }

    private static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return ps;
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);
                var line = await JournalPipe.ReadLineAsync(server, JournalPipe.MaxRequestBytes, cts.Token);
                var response = line == null ? Error("request too large or incomplete") : Handle(server, line);
                await JournalPipe.WriteLineAsync(server, JsonSerializer.SerializeToUtf8Bytes(response, JournalJson.Default.JournalResponse), ct);
                using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
                drain.CancelAfter(TimeSpan.FromSeconds(2));
                var sink = new byte[4096];
                while (await server.ReadAsync(sink, drain.Token) > 0) { }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private JournalResponse Handle(NamedPipeServerStream server, byte[] line)
    {
        JournalRequest? request;
        try { request = JsonSerializer.Deserialize(line, JournalJson.Default.JournalRequest); }
        catch (JsonException) { request = null; }
        if (request == null || !IsVolume(request.Volume)) return Error("bad request");

        try
        {
            var allowed = new List<string>();
            server.RunAsClient(() => allowed.AddRange(request.Roots.Where(r => CallerCanList(r, request.Volume))));
            lock (_gate) return collector.Collect(request, allowed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Error(e.Message);
        }
    }

    private static bool IsVolume(string v) => v.Length == 2 && char.IsAsciiLetter(v[0]) && v[1] == ':';

    private static bool CallerCanList(string root, string volume)
    {
        try
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(volume, StringComparison.OrdinalIgnoreCase)) return false;
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static JournalResponse Error(string message) => new(JournalStatus.Unavailable, Error: message);
}
