using Recall.Core.Ignore;

namespace Recall.Core.Scan;

/// <summary>Ignore-file scope of one directory, built on first use. Reused directories never read their ignore files.</summary>
internal sealed class ScopeNode(string dir, ScopeNode? parent, IgnorePolicy policy)
{
    private readonly Lazy<IReadOnlyList<GitIgnoreMatcher>> _scope =
        new(() => policy.ScopeFor(dir, parent?.Scope ?? []), LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<GitIgnoreMatcher> Scope => _scope.Value;
}
