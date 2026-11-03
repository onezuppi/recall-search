using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;

namespace Recall.Agent;

/// <summary>Composition of the indexing pipeline: the one place that knows which extractors and which OCR engine the agent uses.</summary>
internal static class IndexRunnerFactory
{
    public static IndexRunner Create(RecallConfig config, IIndexRepository store, IgnorePolicy policy, int? workers = null)
    {
        var ocr = new WindowsOcr(config.OcrLanguages, workers ?? IndexRunner.DefaultWorkers);
        IExtractor[] extractors = [new TextExtractor(), new OpenXmlExtractor(), new PdfExtractor(), new ImageExtractor(ocr)];
        return new IndexRunner(store, policy, extractors, ocr, workers);
    }
}
