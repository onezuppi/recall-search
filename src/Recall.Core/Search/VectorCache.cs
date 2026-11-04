namespace Recall.Core.Search;

/// <summary>
/// Counts changes to the stored vectors. The agent writes the vectors and answers the searches in one process, so a counter is enough
/// to tell the <see cref="VectorCache"/> that its copy is out of date.
/// </summary>
public static class VectorVersion
{
    private static long _version;

    public static long Current => Volatile.Read(ref _version);

    public static void Bump() => Interlocked.Increment(ref _version);
}

/// <summary>
/// Keeps the vectors of one embedding space in one array, so a search does not read them from SQLite again (about 5 us per vector there,
/// well under 0.1 us here). The copy is rebuilt after <see cref="VectorVersion"/> changes, dropped after a quiet period, and not made at
/// all when it would exceed <paramref name="maxBytes"/>; the search then streams from the database as before.
/// </summary>
public sealed class VectorCache(long maxBytes = 512L * 1024 * 1024, TimeSpan? idle = null)
{
    private sealed record Snapshot(string Space, int Dimensions, long Version, float[] Data, long[] Ids);

    private readonly object _gate = new();
    private readonly TimeSpan _idle = idle ?? TimeSpan.FromMinutes(2);
    private Snapshot? _snapshot;
    private DateTime _lastUse;
    private Timer? _trim;

    /// <summary>True while a copy is held in memory.</summary>
    public bool IsLoaded { get { lock (_gate) return _snapshot != null; } }

    /// <param name="stream">Reads the vectors from the database; used to build the copy and when the copy is too big.</param>
    public void Scan(string space, int dimensions, Action<VectorVisitor> stream, VectorVisitor visit)
    {
        var snapshot = Acquire(space, dimensions, stream, visit);
        if (snapshot == null) return; // the build already visited every vector
        for (var i = 0; i < snapshot.Ids.Length; i++)
            visit(snapshot.Ids[i], snapshot.Data.AsSpan(i * dimensions, dimensions));
    }

    /// <summary>The usable copy, or null after a build or a too-big scan that has visited the vectors itself.</summary>
    private Snapshot? Acquire(string space, int dimensions, Action<VectorVisitor> stream, VectorVisitor visit)
    {
        lock (_gate)
        {
            _lastUse = DateTime.UtcNow;
            var version = VectorVersion.Current;
            if (_snapshot is { } s && s.Space == space && s.Dimensions == dimensions && s.Version == version) return s;
            _snapshot = null;
            Build(space, dimensions, version, stream, visit);
            return null;
        }
    }

    private void Build(string space, int dimensions, long version, Action<VectorVisitor> stream, VectorVisitor visit)
    {
        var capacity = (int)Math.Min(maxBytes / sizeof(float) / dimensions, int.MaxValue / dimensions);
        var ids = new List<long>();
        var data = new float[1024 * dimensions];
        var oversized = false;
        stream((id, vector) =>
        {
            visit(id, vector);
            if (oversized) return;
            if (ids.Count >= capacity) { oversized = true; ids.Clear(); data = []; return; }
            if ((ids.Count + 1) * dimensions > data.Length) Array.Resize(ref data, Math.Min(data.Length * 2, capacity * dimensions));
            vector.CopyTo(data.AsSpan(ids.Count * dimensions));
            ids.Add(id);
        });
        if (oversized) return;
        _snapshot = new Snapshot(space, dimensions, version, data, [.. ids]);
        _trim ??= new Timer(_ => Trim(), null, _idle, _idle);
    }

    private void Trim()
    {
        lock (_gate)
            if (_snapshot != null && DateTime.UtcNow - _lastUse >= _idle) _snapshot = null;
    }
}
