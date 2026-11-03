namespace Recall.Core.Ignore;

public static class BuiltInRules
{
    public const string Images = "images", Pdf = "pdf", Office = "office", Text = "text";

    public static readonly IReadOnlySet<string> SkipFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".pnpm", ".pnpm-store", "bower_components", "vendor", "Pods", ".git", ".svn", ".hg",
        "bin", "obj", "target", "dist", "build", "out", "coverage", ".angular", ".next", ".nuxt", ".svelte-kit",
        ".parcel-cache", ".turbo", ".godot", ".gradle", "DerivedData", ".terraform", ".venv", "venv", "env",
        "site-packages", "__pycache__", ".mypy_cache", ".pytest_cache", ".tox", ".idea", ".vs", ".vscode", ".cache",
        // Working folders of coding agents: transcripts, scratch files and whole repository copies (.claude\worktrees).
        ".claude", ".codex", ".cursor", ".gemini", ".aider", ".continue", ".windsurf", ".superpowers", ".omx",
    };

    private static readonly Dictionary<string, string> Groups = BuildGroups();

    private static Dictionary<string, string> BuildGroups()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in new[] { "jpg", "jpeg", "png", "webp", "bmp", "gif", "tif", "tiff", "heic" }) d["." + e] = Images;
        d[".pdf"] = Pdf;
        foreach (var e in new[] { "docx", "xlsx", "pptx" }) d["." + e] = Office;
        foreach (var e in new[] { "txt", "md" }) d["." + e] = Text;
        return d;
    }

    /// <summary>Every extension (with the leading dot) that belongs to some group.</summary>
    public static IReadOnlyCollection<string> KnownExtensions => Groups.Keys;

    /// <summary>Group of an extension (with the leading dot), or null when the type is not indexed.</summary>
    public static string? GroupOf(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        if (extension[0] != '.') extension = "." + extension;
        return Groups.TryGetValue(extension, out var g) ? g : null;
    }

    private static readonly string[] SecretExtensions = [".pem", ".key", ".pfx", ".p12", ".mafile", ".kdbx", ".ppk"];

    public static bool IsSecret(string fileName)
    {
        foreach (var e in SecretExtensions)
            if (fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return true;
        return fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Lazy<string[]> SystemRoots = new(ComputeSystemRoots);

    private static string[] ComputeSystemRoots()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p.TrimEnd('\\', '/'));
        }
        Add(Environment.GetEnvironmentVariable("SystemRoot"));
        Add(Environment.GetEnvironmentVariable("ProgramFiles"));
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        Add(Environment.GetEnvironmentVariable("ProgramData"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Path.GetTempPath());
        foreach (var drive in DriveInfo.GetDrives())
        {
            var r = drive.RootDirectory.FullName;
            Add(Path.Combine(r, "$Recycle.Bin"));
            Add(Path.Combine(r, "System Volume Information"));
        }
        return list.ToArray();
    }

    public static bool IsSystemPath(string fullPath)
    {
        var p = fullPath.TrimEnd('\\', '/');
        foreach (var root in SystemRoots.Value)
            if (IsSameOrUnder(p, root)) return true;
        // Other users' profiles: any child of the Users folder except the current profile.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        var users = Path.GetDirectoryName(profile);
        if (users != null && !IsSameOrUnder(p, profile) && IsSameOrUnder(p, users) && p.Length > users.Length)
            return true;
        return false;
    }

    private static bool IsSameOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
         && (path[root.Length] == '\\' || path[root.Length] == '/'));
}
