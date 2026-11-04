namespace Recall.Core.Scan;

/// <summary>The one spelling of a root folder path used for scanning, ignore rules and the index.</summary>
public static class RootPath
{
    /// <summary>
    /// The full path without a trailing separator, except for a drive root. "D:" would mean "the current folder on drive D",
    /// so "D:\" keeps its separator.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd('\\', '/');
    }
}
