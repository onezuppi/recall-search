namespace Recall.Core.Index;

/// <summary>A stored segment that has no vector yet in some embedding space.</summary>
public sealed record PendingSegment(long SegmentId, string Text);
