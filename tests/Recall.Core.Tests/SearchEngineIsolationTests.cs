using Recall.Core.Embed;
using Recall.Core.Search;
using Recall.Core.Tests.Support;

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
        public void ScanVectors(ParsedQuery filter, string space, int dimensions, VectorVisitor visit) { }
        public string? SegmentText(long segmentId) => null;
        public void Dispose() { }
    }

    /// <summary>Two files with hand-made vectors; the engine must rank them by cosine without touching SQLite.</summary>
    private sealed class TwoVectors(Func<string, float[]> vectorOf) : ISearchRepository, ISearchSession
    {
        public string? RequestedSpace;
        public ISearchSession? Open() => this;
        public IReadOnlyList<SegmentRow> MatchSegments(ParsedQuery filter, string matchExpression, IReadOnlyList<string> shortTerms, int limit) => [];
        public IReadOnlyList<SegmentRow> ScanSegments(ParsedQuery filter, IReadOnlyList<string> shortTerms, int limit) => [];
        public IReadOnlyList<FileRow> MatchFileNames(ParsedQuery filter, IReadOnlyList<string> terms, int limit) => [];
        public string? SegmentText(long segmentId) => segmentId == 1 ? "bill payment due friday" : "borscht recipe";

        public void ScanVectors(ParsedQuery filter, string space, int dimensions, VectorVisitor visit)
        {
            RequestedSpace = space;
            var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            visit(new VectorRow(@"C:\dill.txt", "text", "", when, 1, 1), vectorOf("invoice"));
            visit(new VectorRow(@"C:\dorscht.txt", "text", "", when, 1, 2), vectorOf("borscht"));
        }

        public void Dispose() { }
    }

    private static EmbedderHost HostWith(FakeEmbedder fake, float minSimilarity = 0.5f) => new(fake.Space, () => fake, TimeSpan.Zero, minSimilarity);

    [Fact]
    public void Vector_hits_come_from_the_session_and_are_cut_by_the_similarity_floor()
    {
        var fake = new FakeEmbedder();
        var repo = new TwoVectors(w => fake.Embed([w], EmbedKind.Passage)[0]);
        var hits = new SearchEngine(repo, HostWith(fake)).Search("invoice", 5);
        Assert.Equal([@"C:\dill.txt"], hits.Select(h => h.Path));
        Assert.Equal("fake@1", repo.RequestedSpace);
        Assert.Equal("bill payment due friday", hits[0].Snippet);
    }

    [Fact]
    public void Lower_floor_lets_weak_vector_hits_through()
    {
        var fake = new FakeEmbedder();
        var repo = new TwoVectors(w => fake.Embed([w], EmbedKind.Passage)[0]);
        var hits = new SearchEngine(repo, HostWith(fake, minSimilarity: -1f)).Search("invoice", 5);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void A_crashing_model_leaves_text_results_alone()
    {
        var repo = new OneFile();
        var host = new EmbedderHost("m@1", () => throw new InvalidOperationException("boom"), TimeSpan.Zero);
        Assert.Single(new SearchEngine(repo, host).Search("report", 5));
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
