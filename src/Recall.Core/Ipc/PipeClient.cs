using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Recall.Core.Ipc;

public static class PipeClient
{
    /// <summary>
    /// Sends one request. Throws <see cref="TimeoutException"/> when no server answers in time (agent not running),
    /// <see cref="UntrustedServerException"/> when the server is not the current user's agent at the expected path.
    /// </summary>
    public static async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct, string? pipeName = null)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName ?? PipeNames.ForCurrentUser(), PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
        VerifyServer(pipe);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var json = JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Default.IpcRequest);
        await Wire.WriteLineAsync(pipe, json, cts.Token);
        var line = await Wire.ReadLineAsync(pipe, Wire.MaxResponseBytes, cts.Token)
                   ?? throw new IOException("agent closed the connection without a response");
        return JsonSerializer.Deserialize(line, IpcJson.Default.IpcResponse)
               ?? throw new IOException("agent sent an empty response");
    }

    private static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            throw new UntrustedServerException("cannot identify the pipe server process");
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) throw new UntrustedServerException("cannot open the pipe server process");
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(process, 0, sb, ref size))
                throw new UntrustedServerException("cannot read the pipe server image path");
            var actual = System.IO.Path.GetFullPath(sb.ToString());
            var expected = System.IO.Path.GetFullPath(AgentPaths.ExpectedExe);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UntrustedServerException($"pipe server runs from an unexpected path: {actual}");

            var owner = ProcessUserSid(process);
            var me = WindowsIdentity.GetCurrent().User!;
            if (owner == null || !owner.Equals(me))
                throw new UntrustedServerException("pipe server belongs to a different user");
        }
        finally { CloseHandle(process); }
    }

    private static SecurityIdentifier? ProcessUserSid(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buf, needed, out _)) return null;
                // TOKEN_USER starts with a SID_AND_ATTRIBUTES whose first field is the SID pointer.
                return new SecurityIdentifier(Marshal.ReadIntPtr(buf));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
