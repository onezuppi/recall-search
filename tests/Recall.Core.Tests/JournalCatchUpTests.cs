using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Journal;
using Recall.Core.Scan;
using Recall.Core.Tests.Support;
using Recall.Journal;

namespace Recall.Core.Tests;

public class JournalCatchUpTests : RunnerTestBase
{
    private sealed class FakeJournal(Func<JournalRequest, JournalResponse?> answer) : IChangeJournal
    {
        public List<JournalRequest> Requests { get; } = [];

        public Task<JournalResponse?> QueryAsync(JournalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private string RootPath => Path.GetFullPath(Root).TrimEnd('\\');

    private JournalResponse Answer(string status, long next, params string[] files) =>
        new(status, 5, next, files, [RootPath]);

    [Fact]
    public async Task Unavailable_service_leaves_everything_to_the_normal_scan()
    {
        using var s = Make();
        var catchUp = new JournalCatchUp(s.Store, new FakeJournal(_ => null));
        var plan = await catchUp.PrepareAsync([R()], default);
        Assert.Empty(plan.Covered);
        Assert.Empty(plan.NeedsFull);
    }

    [Fact]
    public async Task No_cursor_requires_a_full_walk_and_the_cursor_is_stored_only_on_commit()
    {
        using var s = Make();
        var journal = new FakeJournal(_ => Answer(JournalStatus.NoCursor, 100));
        var catchUp = new JournalCatchUp(s.Store, journal);

        var plan = await catchUp.PrepareAsync([R()], default);
        Assert.Contains(RootPath, plan.NeedsFull);
        Assert.Equal(0UL, journal.Requests[0].JournalId);

        await catchUp.PrepareAsync([R()], default); // not committed yet: still no cursor
        Assert.Equal(0UL, journal.Requests[1].JournalId);

        catchUp.Commit(plan);
        await catchUp.PrepareAsync([R()], default);
        Assert.Equal((5UL, 100L), (journal.Requests[2].JournalId, journal.Requests[2].StartUsn));
    }

    [Fact]
    public async Task Gap_is_handled_like_a_missing_cursor()
    {
        using var s = Make();
        var catchUp = new JournalCatchUp(s.Store, new FakeJournal(_ => Answer(JournalStatus.Gap, 300)));
        var plan = await catchUp.PrepareAsync([R()], default);
        Assert.Contains(RootPath, plan.NeedsFull);
        Assert.Empty(plan.Covered);
    }

    [Fact]
    public async Task Root_the_caller_cannot_list_is_not_covered()
    {
        using var s = Make();
        var denied = new JournalResponse(JournalStatus.Ok, 5, 100, [], []);
        var plan = await new JournalCatchUp(s.Store, new FakeJournal(_ => denied)).PrepareAsync([R()], default);
        Assert.Empty(plan.Covered);
        Assert.Empty(plan.NeedsFull);
    }

    [Fact]
    public async Task Overwrite_in_place_is_found_by_the_journal_without_a_full_walk()
    {
        using var s = Make();
        var runner = new IndexRunner(s.Store, new IgnorePolicy(s.Config, _ => false), [s.Text], new FakeOcr(""),
            fullScanInterval: TimeSpan.Zero); // age alone would force a full walk every time
        var file = Write(@"a\1.txt", "oldword");
        var cursor = new FakeJournal(_ => Answer(JournalStatus.NoCursor, 100));
        var first = new JournalCatchUp(s.Store, cursor);
        var plan1 = await first.PrepareAsync([R()], default);
        await runner.RunAsync([R()], null, default, ScanMode.Auto, plan1.Hints);
        first.Commit(plan1);

        File.WriteAllText(file, "newword and more");
        var second = new JournalCatchUp(s.Store, new FakeJournal(_ => Answer(JournalStatus.Ok, 200, file)));
        var plan2 = await second.PrepareAsync([R()], default);
        Assert.Contains(RootPath, plan2.Covered);
        await second.ApplyAsync(plan2, (_, paths) => runner.IndexFilesAsync(R(), paths, default));
        var pass = await runner.RunAsync([R()], null, default, ScanMode.Auto, plan2.Hints);

        Assert.Equal(1, Hits("newword"));
        Assert.Equal(0, Hits("oldword"));
        Assert.Equal(0, pass.DirsListed);
        Assert.Equal(2, pass.DirsReused);
    }
}
