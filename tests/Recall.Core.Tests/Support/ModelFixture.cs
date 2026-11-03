using Recall.Core.Config;
using Recall.Core.Embed;

namespace Recall.Core.Tests.Support;

/// <summary>Finds the real model in the user's data folder. Model tests skip when it was not downloaded.</summary>
public static class ModelFixture
{
    public static ModelSpec Spec { get; } = ModelCatalog.Find("multilingual-e5-small")!;

    public static string Dir => ModelDownloader.DirectoryOf(Path.Combine(RecallPaths.DataDir, "models"), Spec);

    public static bool Available => Spec.Files.All(f => File.Exists(Path.Combine(Dir, f.FileName)));

    public static void RequireModel() => Skip.IfNot(Available, "model multilingual-e5-small is not downloaded");
}
