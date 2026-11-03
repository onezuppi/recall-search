using Recall.Core.Ocr;

namespace Recall.Core.Tests;

public class ReleasableOcrTests
{
    private sealed class Probe : IOcr
    {
        public TaskCompletionSource? Hold;
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) =>
            Hold == null ? Task.FromResult("t") : Hold.Task.ContinueWith(_ => "t");
    }

    [Fact]
    public async Task Creates_lazily_and_recreates_after_release()
    {
        var created = 0;
        var ocr = new ReleasableOcr(() => { created++; return new Probe(); });
        Assert.Equal(0, created);
        await ocr.RecognizeAsync([], default);
        await ocr.RecognizeAsync([], default);
        Assert.Equal(1, created);
        Assert.True(ocr.IsLoaded);

        ocr.Release();
        Assert.False(ocr.IsLoaded);
        await ocr.RecognizeAsync([], default);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task Release_during_a_call_waits_for_it_to_finish()
    {
        var probe = new Probe { Hold = new TaskCompletionSource() };
        var ocr = new ReleasableOcr(() => probe);
        var call = ocr.RecognizeAsync([], default);

        ocr.Release();
        Assert.True(ocr.IsLoaded);

        probe.Hold.SetResult();
        await call;
        Assert.False(ocr.IsLoaded);
    }
}
