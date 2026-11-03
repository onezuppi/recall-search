using System.IO.Pipes;
using System.Text;
using Recall.Core.Ipc;

namespace Recall.Core.Tests;

public class PipeTests : IDisposable
{
    private const int MaxRequestBytes = 64 * 1024;

    private readonly string _name = "RecallSearchTest." + Guid.NewGuid().ToString("N");
    private readonly string _savedExe = AgentPaths.ExpectedExe;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public PipeTests() => AgentPaths.ExpectedExe = Environment.ProcessPath!;

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        AgentPaths.ExpectedExe = _savedExe;
    }

    private static Task<IpcResponse> Echo(IpcRequest r, CancellationToken ct) =>
        Task.FromResult(new IpcResponse(true, Hits: [new IpcHit(@"C:\a\b.txt", "text", "", "echo:" + r.Query, DateTime.UtcNow, 3)]));

    private void StartServer(Func<IpcRequest, CancellationToken, Task<IpcResponse>>? handler = null)
    {
        _serverTask = new PipeServer(handler ?? Echo, _name).RunAsync(_cts.Token);
        Thread.Sleep(200);
    }

    [Fact]
    public async Task Roundtrip_search_request()
    {
        StartServer();
        var r = await PipeClient.SendAsync(new IpcRequest(IpcOps.Search, "hello"), TimeSpan.FromSeconds(5), default, _name);
        Assert.True(r.Ok);
        Assert.Equal("echo:hello", Assert.Single(r.Hits!).Snippet);
    }

    [Fact]
    public async Task Handles_several_clients_in_a_row()
    {
        StartServer();
        for (var i = 0; i < 5; i++)
        {
            var r = await PipeClient.SendAsync(new IpcRequest(IpcOps.Search, "q" + i), TimeSpan.FromSeconds(5), default, _name);
            Assert.Equal("echo:q" + i, r.Hits![0].Snippet);
        }
    }

    [Fact]
    public async Task Second_server_on_same_name_fails()
    {
        StartServer();
        var second = new PipeServer(Echo, _name).RunAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => second);
        Assert.True(ex is UnauthorizedAccessException or IOException, ex.GetType().Name);
    }

    [Fact]
    public async Task Client_rejects_server_from_unexpected_path()
    {
        StartServer();
        AgentPaths.ExpectedExe = @"C:\definitely\not\here\Recall.Agent.exe";
        await Assert.ThrowsAsync<UntrustedServerException>(() =>
            PipeClient.SendAsync(new IpcRequest(IpcOps.Ping), TimeSpan.FromSeconds(5), default, _name));
    }

    [Fact]
    public async Task Oversized_request_is_rejected_without_crash()
    {
        StartServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var raw = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000, cts.Token);
            var big = new byte[MaxRequestBytes + 10];
            Array.Fill(big, (byte)'a');
            big[^1] = (byte)'\n';
            await raw.WriteAsync(big, cts.Token);
            var buf = new byte[1024];
            var n = await raw.ReadAsync(buf, cts.Token);
            Assert.Contains("too large", Encoding.UTF8.GetString(buf, 0, n));
        }
        var ok = await PipeClient.SendAsync(new IpcRequest(IpcOps.Ping), TimeSpan.FromSeconds(5), cts.Token, _name);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Garbage_request_gets_an_error_response()
    {
        StartServer();
        using var raw = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(5000);
        await raw.WriteAsync("not json\n"u8.ToArray());
        await raw.FlushAsync();
        var buf = new byte[1024];
        var n = await raw.ReadAsync(buf);
        Assert.Contains("\"ok\":false", Encoding.UTF8.GetString(buf, 0, n));
    }

    [Fact]
    public async Task Missing_server_times_out()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            PipeClient.SendAsync(new IpcRequest(IpcOps.Ping), TimeSpan.FromMilliseconds(300), default, _name));
    }

    [Fact]
    public void Pipe_name_contains_the_user_sid() =>
        Assert.Equal("RecallSearch." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, PipeNames.ForCurrentUser());

}
