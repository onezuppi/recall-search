using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Recall.Core.Ipc;

/// <summary>
/// Per-user named pipe server. The ACL grants the current user only and denies network logons.
/// The first instance is created with FirstPipeInstance, so a process that already owns the name makes startup fail.
/// </summary>
public sealed class PipeServer(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler, string? pipeName = null)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name = pipeName ?? PipeNames.ForCurrentUser();

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
                    _name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
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
        ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return ps;
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ReadTimeout);
                var line = await Wire.ReadLineAsync(server, Wire.MaxRequestBytes, readCts.Token);
                IpcResponse response;
                if (line == null) response = new IpcResponse(false, "request too large or incomplete");
                else
                {
                    IpcRequest? req = null;
                    try { req = JsonSerializer.Deserialize(line, IpcJson.Default.IpcRequest); }
                    catch (JsonException) { }
                    if (req == null) response = new IpcResponse(false, "bad request");
                    else
                    {
                        try { response = await handler(req, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { response = new IpcResponse(false, "internal error"); }
                    }
                }
                await Wire.WriteLineAsync(server, Wire.Serialize(response), ct);
                // Keep reading until the client hangs up (also unblocks a client still sending an oversized request).
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                drainCts.CancelAfter(TimeSpan.FromSeconds(2));
                var sink = new byte[4096];
                while (await server.ReadAsync(sink, drainCts.Token) > 0) { }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }
}
