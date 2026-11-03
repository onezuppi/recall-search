namespace Recall.Core.Embed;

public sealed class ModelDownloader
{
    public static string DirectoryOf(string modelsRoot, ModelSpec spec) => Path.Combine(modelsRoot, spec.Id, spec.Revision);
}
