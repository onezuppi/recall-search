using System.Buffers.Binary;
using System.Text;
using Recall.Journal;

namespace Recall.Core.Tests;

public class JournalCollectorTests
{
    private const ulong Id = 42;
    private static readonly string[] Md = [".md", ".txt"];

    private sealed class FakeSource(JournalState state, IReadOnlyList<UsnRecord> records, Dictionary<ulong, string?> dirs) : IJournalSource
    {
        public JournalState Query(string volume) => state;

        public (long NextUsn, IReadOnlyList<UsnRecord> Records) Read(string volume, ulong journalId, long startUsn)
        {
            var batch = records.Where(r => r.Usn >= startUsn).ToList();
            return batch.Count == 0 ? (startUsn, batch) : (state.NextUsn, batch);
        }

        public string? DirectoryPath(string volume, ulong fileId) => dirs.GetValueOrDefault(fileId);
    }

    private static UsnRecord Rec(long usn, ulong parent, string name, uint reason, uint attrs = 0x20) =>
        new((ulong)usn, parent, usn, reason, attrs, name);

    private static JournalCollector Make(IReadOnlyList<UsnRecord> records, long first = 0, long next = 1000, int max = 1000) =>
        new(new FakeSource(new JournalState(Id, first, next), records, new() { [1] = @"C:\docs\sub", [2] = @"C:\other", [3] = null }), max);

    private static JournalRequest Req(ulong id = Id, long start = 10, string[]? extensions = null) =>
        new("C:", id, start, [@"C:\docs"], extensions ?? Md);

    private static readonly string[] Roots = [@"C:\docs"];

    [Fact]
    public void Request_without_cursor_returns_only_the_current_end()
    {
        var r = Make([Rec(20, 1, "a.md", UsnRecord.ReasonDataOverwrite)]).Collect(Req(id: 0, start: 0), Roots);
        Assert.Equal(JournalStatus.NoCursor, r.Status);
        Assert.Equal((Id, 1000L), (r.JournalId, r.NextUsn));
        Assert.Null(r.Files);
    }

    [Theory]
    [InlineData(7UL, 10L)]   // other journal id: the journal was recreated
    [InlineData(Id, 5L)]     // cursor older than the first record still kept
    [InlineData(Id, 2000L)]  // cursor beyond the end
    public void Stale_cursor_is_a_gap(ulong id, long start)
    {
        var r = Make([], first: 8).Collect(Req(id, start), Roots);
        Assert.Equal(JournalStatus.Gap, r.Status);
        Assert.Equal(1000, r.NextUsn);
    }

    [Fact]
    public void Overwritten_file_under_a_root_is_listed_with_the_new_cursor()
    {
        var r = Make([Rec(20, 1, "a.md", UsnRecord.ReasonDataOverwrite | 0x80000000)]).Collect(Req(), Roots);
        Assert.Equal(JournalStatus.Ok, r.Status);
        Assert.Equal([@"C:\docs\sub\a.md"], r.Files);
        Assert.Equal(1000, r.NextUsn);
        Assert.Equal(Roots, r.Roots);
    }

    [Fact]
    public void Records_that_cannot_change_content_or_lie_outside_the_request_are_dropped()
    {
        var r = Make([
            Rec(11, 1, "dir", UsnRecord.ReasonFileCreate, attrs: 0x10),     // folder
            Rec(12, 1, "rename-old.md", 0x1000),                              // RENAME_OLD_NAME only
            Rec(13, 1, "pic.png", UsnRecord.ReasonDataOverwrite),             // extension not requested
            Rec(14, 2, "other.md", UsnRecord.ReasonDataOverwrite),            // outside the root
            Rec(15, 3, "gone.md", UsnRecord.ReasonDataOverwrite),             // parent folder deleted
            Rec(16, 1, "kept.md", UsnRecord.ReasonDataOverwrite),
            Rec(17, 1, "keep.TXT", UsnRecord.ReasonFileCreate),
        ]).Collect(Req(start: 11), Roots);
        Assert.Equal([@"C:\docs\sub\keep.TXT", @"C:\docs\sub\kept.md"], r.Files!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Same_file_written_many_times_is_listed_once()
    {
        var records = Enumerable.Range(0, 50).Select(i => Rec(20 + i, 1, "log.txt", UsnRecord.ReasonDataExtend)).ToList();
        Assert.Single(Make(records).Collect(Req(), Roots).Files!);
    }

    [Fact]
    public void Too_many_files_turn_into_a_gap()
    {
        var records = Enumerable.Range(0, 10).Select(i => Rec(20 + i, 1, $"f{i}.md", UsnRecord.ReasonFileCreate)).ToList();
        Assert.Equal(JournalStatus.Gap, Make(records, max: 5).Collect(Req(), Roots).Status);
    }

    [Fact]
    public void Missing_journal_is_reported_as_unavailable()
    {
        var source = new ThrowingSource();
        Assert.Equal(JournalStatus.Unavailable, new JournalCollector(source).Collect(Req(), Roots).Status);
    }

    private sealed class ThrowingSource : IJournalSource
    {
        public JournalState Query(string volume) => throw new JournalUnavailableException("no journal");
        public (long NextUsn, IReadOnlyList<UsnRecord> Records) Read(string volume, ulong journalId, long startUsn) => throw new NotSupportedException();
        public string? DirectoryPath(string volume, ulong fileId) => throw new NotSupportedException();
    }

    [Fact]
    public void Parser_reads_v2_records_and_skips_other_versions()
    {
        var buffer = new List<byte>();
        buffer.AddRange(BitConverter.GetBytes(777L));
        buffer.AddRange(V2Record(major: 2, frn: 5, parent: 6, usn: 99, reason: 0x80000001, attrs: 0x20, name: "doc.md"));
        buffer.AddRange(V2Record(major: 3, frn: 8, parent: 6, usn: 100, reason: 1, attrs: 0x20, name: "skipped.md"));
        buffer.AddRange(V2Record(major: 2, frn: 9, parent: 6, usn: 101, reason: 2, attrs: 0x10, name: "folder"));

        var (next, records) = UsnRecordParser.Parse(buffer.ToArray());

        Assert.Equal(777, next);
        Assert.Equal(["doc.md", "folder"], records.Select(r => r.Name));
        Assert.Equal((5UL, 6UL, 99L, 0x80000001u), (records[0].FileId, records[0].ParentId, records[0].Usn, records[0].Reason));
        Assert.True(records[1].IsDirectory);
    }

    [Fact]
    public void Parser_stops_at_a_truncated_record()
    {
        var buffer = new List<byte>(BitConverter.GetBytes(5L));
        buffer.AddRange(V2Record(2, 1, 2, 3, 1, 0x20, "a.md").SkipLast(4));
        Assert.Empty(UsnRecordParser.Parse(buffer.ToArray()).Records);
    }

    private static byte[] V2Record(ushort major, ulong frn, ulong parent, long usn, uint reason, uint attrs, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) / 8 * 8;
        var b = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), major);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), 60);
        nameBytes.CopyTo(b, 60);
        return b;
    }
}
