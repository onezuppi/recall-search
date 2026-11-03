namespace Recall.Core.Search;

/// <summary>A segment found by the index. Text is an FTS excerpt or the segment start, depending on the query.</summary>
public sealed record SegmentRow(string Path, string Group, string Locator, string Text, DateTime Modified, long Size, double Rank, long SegmentId);

public sealed record FileRow(string Path, string Group, DateTime Modified, long Size);

/// <summary>A stored chunk vector with the file it belongs to. The vector itself is passed next to it, see <see cref="VectorVisitor"/>.</summary>
public sealed record VectorRow(string Path, string Group, string Locator, DateTime Modified, long Size, long SegmentId);

/// <summary>Called once per stored vector. The span is valid only during the call.</summary>
public delegate void VectorVisitor(VectorRow row, ReadOnlySpan<float> vector);

/// <summary>Read side of the index. Opens one session per search so that all queries of a search share a connection.</summary>
public interface ISearchRepository
{
    /// <summary>Null when there is no index yet.</summary>
    ISearchSession? Open();
}

/// <summary>Queries of one search. Every method applies the filters of in:, type:, after: and before: from the parsed query.</summary>
public interface ISearchSession : IDisposable
{
    /// <summary>Segments matching an FTS expression and containing every short term, best bm25 first (Rank is the bm25 value).</summary>
    IReadOnlyList<SegmentRow> MatchSegments(ParsedQuery filter, string matchExpression, IReadOnlyList<string> shortTerms, int limit);

    /// <summary>Segments containing every short term (too short for the trigram index), newest file first.</summary>
    IReadOnlyList<SegmentRow> ScanSegments(ParsedQuery filter, IReadOnlyList<string> shortTerms, int limit);

    /// <summary>Files whose path contains every term, newest first.</summary>
    IReadOnlyList<FileRow> MatchFileNames(ParsedQuery filter, IReadOnlyList<string> terms, int limit);

    /// <summary>
    /// Passes every chunk vector of <paramref name="space"/> (file x chunk) that satisfies the filters to <paramref name="visit"/>.
    /// Vectors with another length than <paramref name="dimensions"/> are skipped. The similarity math belongs to the caller.
    /// </summary>
    void ScanVectors(ParsedQuery filter, string space, int dimensions, VectorVisitor visit);

    /// <summary>Start of a segment's text, or null when the segment is gone.</summary>
    string? SegmentText(long segmentId);
}
