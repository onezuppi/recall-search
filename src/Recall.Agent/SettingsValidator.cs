using Recall.Core.Config;
using Recall.Core.Ignore;

namespace Recall.Agent;

/// <summary>Checks what the user asked to add against the current configuration. Messages go straight to the settings UI.</summary>
internal sealed class SettingsValidator(Func<string, bool> isSystemPath)
{
    private const int MaxExtensionLength = 16;
    public static readonly string[] Groups = [BuiltInRules.Text, BuiltInRules.Images, BuiltInRules.Office, BuiltInRules.Pdf];

    /// <summary>Returns an error, or null with the normalized absolute path in <paramref name="full"/>.</summary>
    public string? Root(RecallConfig config, string? value, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(value)) return "Folder path is empty";
        if (!Path.IsPathFullyQualified(value)) return "Folder path must be absolute";
        string absolute;
        try { absolute = Path.GetFullPath(value); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Folder path is invalid";
        }
        if (string.Equals(Path.GetPathRoot(absolute), absolute, StringComparison.OrdinalIgnoreCase))
            return "A whole drive cannot be added";
        full = absolute.TrimEnd('\\', '/');
        if (!Directory.Exists(full)) return "Folder does not exist";
        if (isSystemPath(full)) return "System folders cannot be indexed";
        foreach (var r in config.Roots)
        {
            if (SamePath(r.Path, full)) return "Folder is already in the list";
            if (IsUnder(full, r.Path)) return $"Folder is already covered by {r.Path}";
            if (IsUnder(r.Path, full)) return $"Folder contains {r.Path}, remove that one first";
        }
        return null;
    }

    public static string? Pattern(RecallConfig config, string pattern) =>
        ExcludeRules.Validate(pattern)
        ?? (config.GlobalIgnorePatterns.Contains(pattern) ? "Pattern is already in the list" : null);

    /// <summary>Returns an error, or null with the extension in its stored form (lower case, leading dot).</summary>
    public static string? FileType(RecallConfig config, string? extension, string? group, out string normalized)
    {
        normalized = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Length == 0) return "Extension is empty";
        if (normalized.Length > MaxExtensionLength || !normalized.All(char.IsAsciiLetterOrDigit))
            return "Extension may contain only letters and digits";
        normalized = "." + normalized;
        if (BuiltInRules.GroupOf(normalized) != null) return $"{normalized} is a built-in type";
        if (config.CustomFileTypes.ContainsKey(normalized)) return $"{normalized} is already in the list";
        return Groups.Contains(group ?? "") ? null : "File group must be text, images, office or pdf";
    }

    public static bool SamePath(string a, string? b) =>
        b != null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
}
