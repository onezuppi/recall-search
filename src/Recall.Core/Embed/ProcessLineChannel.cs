using System.Diagnostics;
using System.Text;

namespace Recall.Core.Embed;

/// <summary>One request line out, one reply line in. The seam between the NPU embedder and the process behind it.</summary>
public interface ILineChannel : IDisposable
{
    void WriteLine(string line);

    /// <summary>The next reply line, or null when the other side closed its output. Throws <see cref="TimeoutException"/> when none comes in time.</summary>
    string? ReadLine();
}

/// <summary>A child process with UTF-8 stdin and stdout. The process is started hidden and killed when the channel is disposed.</summary>
public sealed class ProcessLineChannel : ILineChannel
{
    private readonly Process _process;
    private readonly TimeSpan _replyTimeout;
    private readonly StreamWriter _stdin;

    private ProcessLineChannel(Process process, TimeSpan replyTimeout)
    {
        _process = process;
        _replyTimeout = replyTimeout;
        _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    /// <summary>Starts the executable and waits for a stderr line that starts with <paramref name="readyPrefix"/>.</summary>
    public static ProcessLineChannel Start(string exe, IReadOnlyList<string> args, string readyPrefix, TimeSpan startTimeout, TimeSpan replyTimeout)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        var process = new Process { StartInfo = info };
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) ready.TrySetException(new EmbeddingUnavailableException("the embedding server exited before it was ready"));
            else if (e.Data.StartsWith(readyPrefix, StringComparison.Ordinal)) ready.TrySetResult(e.Data);
        };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new EmbeddingUnavailableException($"cannot start the embedding server: {e.Message}", e);
        }
        process.BeginErrorReadLine();
        var channel = new ProcessLineChannel(process, replyTimeout);
        try
        {
            if (!ready.Task.Wait(startTimeout)) throw new EmbeddingUnavailableException("the embedding server did not become ready in time");
        }
        catch (AggregateException e) when (e.InnerException is EmbeddingUnavailableException inner)
        {
            channel.Dispose();
            throw inner;
        }
        catch (EmbeddingUnavailableException)
        {
            channel.Dispose();
            throw;
        }
        return channel;
    }

    public void WriteLine(string line) => _stdin.WriteLine(line);

    public string? ReadLine()
    {
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(_replyTimeout)) throw new TimeoutException("the embedding server did not answer in time");
        return read.Result;
    }

    public void Dispose()
    {
        try { _stdin.Close(); } catch (IOException) { /* the process is already gone */ }
        try
        {
            if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
        _process.Dispose();
    }
}
