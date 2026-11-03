namespace Recall.Core.Scan;

/// <summary>
/// A root may lie inside another one, for example a watched folder inside a scanned one. The inner root owns its subtree:
/// the outer walk skips it, so no file belongs to two roots.
/// </summary>
public static class RootNesting
{
    /// <summary>True when <paramref name="path"/> is below <paramref name="parent"/>. A folder is not under itself.</summary>
    public static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The roots that lie below <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> Inside(string root, IEnumerable<string> allRoots) =>
        [.. allRoots.Where(r => IsUnder(r, root))];

    public static bool UnderAny(string path, IEnumerable<string> roots) => roots.Any(r => IsUnder(path, r));
}
