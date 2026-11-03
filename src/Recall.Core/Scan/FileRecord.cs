namespace Recall.Core.Scan;

public sealed record FileRecord(string Path, long Size, DateTime LastWriteUtc, string Group);
