namespace Recall.Core.Index;

public sealed record RunProgress(string Root, long Done, long Total);

/// <summary>
/// Counts and stage times of one pass. Stage times are summed busy time across workers.
/// <see cref="Embed"/> is the busy time of the embedding stage; <see cref="EmbedError"/> is set when it failed (the files are stored anyway).
/// <see cref="Ocr"/> covers images embedded in documents; OCR of standalone image files is part of <see cref="Extract"/>.
/// </summary>
public sealed record RunReport(
    long Added, long Changed, long Removed, long Hashed, long Extracted, long Deduped, long Failed, long Skipped, long MissingRoots,
    TimeSpan Scan, TimeSpan Hash, TimeSpan Extract, TimeSpan Ocr, TimeSpan Elapsed,
    long DirsListed = 0, long DirsReused = 0, long EmbeddedChunks = 0, TimeSpan Embed = default, string? EmbedError = null);
