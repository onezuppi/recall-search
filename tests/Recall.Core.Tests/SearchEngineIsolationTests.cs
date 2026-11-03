using Recall.Core.Search;

namespace Recall.Core.Tests;

/// <summary>The engine talks to the index only through <see cref="ISearchRepository"/>, so it can run without SQLite.</summary>
public class SearchEngineIsolationTests
{
    private sealed class NoIndex : ISearchRepository
    {
        public ISearchSession? Open() => null;
    }

    private sealed class OneFile : ISearchRepository, ISearchSession
    {
        public ISearchSession? Open() => this;
        public IReadOnlyList<SegmentRow> MatchSegments(ParsedQuery filter, string matchExpression, IReadOnlyList<string> shortTerms, int limit) => [];
        public IReadOnlyList<SegmentRow> ScanSegments(ParsedQuery filter, IReadOnlyList<string> shortTerms, int limit) => [];
        public IReadOnlyList<FileRow> MatchFileNames(ParsedQuery filter, IReadOnlyList<string> terms, int limit) =>
            [new FileRow(@"C:\docs\report.docx", "", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 10)];
        public string? SegmentText(long segmentId) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Missing_index_gives_no_hits()
    {
        Assert.Empty(new SearchEngine(new NoIndex()).Search("report", 5));
    }

    [Fact]
    public void File_name_hits_get_a_group_from_the_extension()
    {
        var hit = Assert.Single(new SearchEngine(new OneFile()).Search("report", 5));
        Assert.Equal("office", hit.Group);
        Assert.Equal("report.docx", hit.Snippet);
    }
}
