namespace Recall.Core.Ocr;

/// <summary>
/// Creates the wrapped OCR on first use and lets the agent drop it (engines hold memory) while it is paused.
/// A call that is already running keeps its instance; the drop happens when the last such call ends.
/// </summary>
public sealed class ReleasableOcr(Func<IOcr> create) : IOcr
{
    private readonly object _gate = new();
    private IOcr? _inner;
    private int _active;
    private bool _releasePending;

    public bool IsLoaded
    {
        get { lock (_gate) return _inner != null; }
    }

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        IOcr inner;
        lock (_gate)
        {
            inner = _inner ??= create();
            _releasePending = false;
            _active++;
        }
        try { return await inner.RecognizeAsync(image, ct); }
        finally
        {
            lock (_gate)
            {
                if (--_active == 0 && _releasePending) Drop();
            }
        }
    }

    /// <summary>Drops the engines now, or after the running calls end.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_active > 0) _releasePending = true;
            else Drop();
        }
    }

    private void Drop()
    {
        _releasePending = false;
        if (_inner == null) return;
        (_inner as IDisposable)?.Dispose();
        _inner = null;
    }
}
