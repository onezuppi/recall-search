using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>Downloads the configured model when it is missing. A failed download is logged, not fatal.</summary>
public static class EmbeddingSetup
{
    public static string ModelsRoot => Path.Combine(RecallPaths.DataDir, "models");

    /// <summary>Downloads the configured model when it is missing. Does nothing when embeddings are off.</summary>
    public static async Task EnsureModelAsync(RecallConfig config, string modelsRoot, HttpClient http, Action<string> log, CancellationToken ct)
    {
        if (config.EmbeddingModel == null) return;
        var spec = ModelCatalog.Find(config.EmbeddingModel);
        if (spec == null) { log($"unknown embedding model '{config.EmbeddingModel}'"); return; }
        if (ModelDownloader.IsDownloaded(modelsRoot, spec)) return;
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
    }
}
