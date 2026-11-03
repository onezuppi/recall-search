namespace Recall.Core.Index;

/// <summary>A stored segment that has no vector yet in some embedding space.</summary>
public sealed record PendingSegment(long SegmentId, string Text);

/// <summary>One vector of a segment chunk. A segment with no embeddable text is marked done by <see cref="Blank"/>.</summary>
public sealed record EmbeddingRow(long SegmentId, int Chunk, float[] Vector)
{
    public const int BlankChunk = -1;

    public static EmbeddingRow Blank(long segmentId) => new(segmentId, BlankChunk, []);
}

/// <summary>Write side of the vectors: one set of rows per embedding space (model revision), next to the segments they describe.</summary>
public interface IEmbeddingRepository
{
    /// <summary>Segments of contents that some file with status ok points to and that have no row in <paramref name="space"/>, oldest first.</summary>
    IReadOnlyList<PendingSegment> PendingSegments(string space, int limit);

    /// <summary>Stores rows in one transaction. Rows of segments that were deleted in the meantime are dropped.</summary>
    void AddEmbeddings(string space, IReadOnlyList<EmbeddingRow> rows);

    /// <summary>Removes every vector of a space, for example after the user switched to another model.</summary>
    void DeleteSpace(string space);
}
