namespace Recall.Agent;

public interface IAgentLog
{
    void Info(string message);
    void Error(string message, Exception? error = null);
}

public sealed class NullLog : IAgentLog
{
    public void Info(string message) { }
    public void Error(string message, Exception? error = null) { }
}

/// <summary>Appends to a text file and rolls it over to "&lt;file&gt;.1" once it passes <paramref name="maxBytes"/>.</summary>
public sealed class RollingLog(string path, long maxBytes = 5 * 1024 * 1024, bool echoToConsole = false) : IAgentLog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? error = null) =>
        Write("ERROR", error == null ? message : $"{message}: {error}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        if (echoToConsole) Console.WriteLine(line);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never take the agent down */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
