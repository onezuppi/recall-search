using Recall.Core.Ignore;

namespace Recall.Core.Scan;

/// <summary>Ignore-file scope of one directory, built on first use. Reused directories never read their ignore files.</summary>
internal sealed class ScopeNode(string dir, ScopeNode? parent, IgnorePolicy policy)
{
    private IReadOnlyList<GitIgnoreMatcher>? _scope;

    public IReadOnlyList<GitIgnoreMatcher> Scope =>
        _scope ??= policy.ScopeFor(dir, parent?.Scope ?? []);
}
