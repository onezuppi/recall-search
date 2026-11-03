using Recall.Core.Embed;

namespace Recall.Core.Tests;

public class EmbedCatalogTests
{
    [Fact]
    public void Catalog_pins_e5_small_with_hashes()
    {
        var m = ModelCatalog.Find("multilingual-e5-small");
        Assert.NotNull(m);
        Assert.Equal(384, m.Dimensions);
        Assert.Equal("614241f622f53c4eeff9890bdc4f31cfecc418b3", m.Revision);
        Assert.Equal("multilingual-e5-small@614241f622f53c4eeff9890bdc4f31cfecc418b3", m.Space);
        Assert.Equal(2, m.Files.Count);
        Assert.All(m.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.Equal(118346824, m.Onnx.Size);
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_for_unknown()
    {
        Assert.NotNull(ModelCatalog.Find("Multilingual-E5-Small"));
        Assert.Null(ModelCatalog.Find("nope"));
    }
}
