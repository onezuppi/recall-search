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

    public static IndexRunner Create(
        RecallConfig config, IIndexRepository store, IgnorePolicy policy, int? workers = null, IOcr? ocr = null, IPauseGate? pause = null,
        int? scanThreads = null, EmbedderHost? embedder = null, bool embedInline = true)
    {
        ocr ??= CreateOcr(config, workers);
        IExtractor[] extractors =
        [
            new GroupExtractor(new TextExtractor(), BuiltInRules.Text, policy.GroupOf),
            new GroupExtractor(new OpenXmlExtractor(), BuiltInRules.Office, policy.GroupOf),
            new GroupExtractor(new PdfExtractor(), BuiltInRules.Pdf, policy.GroupOf),
            new GroupExtractor(new ImageExtractor(ocr), BuiltInRules.Images, policy.GroupOf),
        ];
        return new IndexRunner(store, policy, extractors, ocr, workers, pause, scanThreads: scanThreads, embeddings: store as IEmbeddingRepository, embedder: embedder, embedInline: embedInline);
    }
}
