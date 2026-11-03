namespace Recall.Core.Search;

/// <summary>A segment found by the index. Text is an FTS excerpt or the segment start, depending on the query.</summary>
public sealed record SegmentRow(string Path, string Group, string Locator, string Text, DateTime Modified, long Size, double Rank, long SegmentId);

public sealed record FileRow(string Path, string Group, DateTime Modified, long Size);

/// <summary>A file that holds a segment with a stored vector.</summary>
public sealed record VectorRow(string Path, string Group, string Locator, DateTime Modified, long Size, long SegmentId);

/// <summary>Called once per stored vector with the id of its segment. The span is valid only during the call.</summary>
public delegate void VectorVisitor(long segmentId, ReadOnlySpan<float> vector);

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
    /// Passes every chunk vector of <paramref name="space"/> to <paramref name="visit"/>. Only the vector table is read, with no joins and no
    /// filters, so a scan stays cheap as the index grows. Vectors with another length than <paramref name="dimensions"/> are skipped.
    /// The similarity math belongs to the caller.
    /// </summary>
    void ScanVectors(string space, int dimensions, VectorVisitor visit);

    /// <summary>Ids of the segments whose file satisfies in:, type:, after: and before:. Null when the query has none of them.</summary>
    IReadOnlySet<long>? FilteredSegments(ParsedQuery filter);

    /// <summary>The files of the given segments, with the filters of the query applied.</summary>
    IReadOnlyList<VectorRow> VectorRows(ParsedQuery filter, IReadOnlyCollection<long> segmentIds);

    /// <summary>Start of a segment's text, or null when the segment is gone.</summary>
    string? SegmentText(long segmentId);
}
