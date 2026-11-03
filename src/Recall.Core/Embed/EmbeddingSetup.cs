using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>Builds the model host from the config and downloads the model when it is missing. A failed download is logged, not fatal.</summary>
public static class EmbeddingSetup
{
    public static string ModelsRoot => Path.Combine(RecallPaths.DataDir, "models");

    /// <summary>Null when embeddings are off in the config.</summary>
    public static async Task<EmbedderHost?> PrepareAsync(
        RecallConfig config, string modelsRoot, HttpClient http, TimeSpan unloadDelay, Action<string> log, CancellationToken ct)
    {
        var host = EmbedderHost.Create(config, modelsRoot, unloadDelay);
        if (host == null) return null;
        var spec = ModelCatalog.Find(config.EmbeddingModel!);
        if (spec == null) { log($"unknown embedding model '{config.EmbeddingModel}'"); return host; }
        if (ModelDownloader.IsDownloaded(modelsRoot, spec)) return host;
        try
        {
            var next = 0.1;
            log($"downloading model {spec.Id} ({spec.Files.Sum(f => f.Size) / (1024 * 1024)} MB)");
            var progress = new Progress<double>(p => { if (p >= next) { log($"model download {p:P0}"); next = Math.Floor(p * 10) / 10 + 0.1; } });
            await new ModelDownloader(http).EnsureAsync(spec, modelsRoot, progress, ct);
            log("model downloaded and verified");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            log($"model download failed, search stays text-only: {e.Message}");
        }
        return host;
    }
}
