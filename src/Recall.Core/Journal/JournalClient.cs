using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Recall.Journal;

namespace Recall.Core.Journal;

/// <summary>Source of "which files changed since this cursor" answers. Null means no journal service is available.</summary>
public interface IChangeJournal
{
    Task<JournalResponse?> QueryAsync(JournalRequest request, CancellationToken ct);
}

/// <summary>
/// Talks to the elevated Recall.UsnService over its named pipe. The server must run from the administrator-only install folder:
/// a pipe owned by any other image is refused, so a user-level process cannot feed the agent made-up change lists.
/// </summary>
public sealed class JournalClient : IChangeJournal
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static string ExpectedExe { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RecallSearch", "Journal", "Recall.UsnService.exe");

    public async Task<JournalResponse?> QueryAsync(JournalRequest request, CancellationToken ct)
    {
        if (!File.Exists(@"\\.\pipe\" + JournalPipe.Name)) return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", JournalPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(2000, ct);
            if (!IsExpectedServer(pipe)) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            await JournalPipe.WriteLineAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, JournalJson.Default.JournalRequest), cts.Token);
            var line = await JournalPipe.ReadLineAsync(pipe, JournalPipe.MaxResponseBytes, cts.Token);
            return line == null ? null : JsonSerializer.Deserialize(line, JournalJson.Default.JournalResponse);
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsExpectedServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size)
                   && string.Equals(Path.GetFullPath(sb.ToString()), Path.GetFullPath(ExpectedExe), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
