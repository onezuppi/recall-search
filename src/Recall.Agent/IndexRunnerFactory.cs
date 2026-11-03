using Recall.Core.Activity;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;

namespace Recall.Agent;

/// <summary>Composition of the indexing pipeline: the one place that knows which extractors and which OCR engine the agent uses.</summary>
internal static class IndexRunnerFactory
{
    public static IOcr CreateOcr(RecallConfig config, int? workers = null) =>
        new WindowsOcr(config.OcrLanguages, workers ?? IndexRunner.DefaultWorkers);

    public static IndexRunner Create(RecallConfig config, IIndexRepository store, IgnorePolicy policy, int? workers = null, IOcr? ocr = null, IPauseGate? pause = null, EmbedderHost? embedder = null)
    {
        ocr ??= CreateOcr(config, workers);
        IExtractor[] extractors = [new TextExtractor(), new OpenXmlExtractor(), new PdfExtractor(), new ImageExtractor(ocr)];
        return new IndexRunner(store, policy, extractors, ocr, workers, pause, embeddings: store as IEmbeddingRepository, embedder: embedder);
    }
}
