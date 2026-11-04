namespace Recall.Core.Embed;

public enum EmbedKind { Passage, Query }

/// <summary>Turns texts into L2-normalized vectors. <see cref="Space"/> names the model revision the vectors belong to.</summary>
public interface IEmbedder : IDisposable
{
    string Space { get; }
    int Dimensions { get; }
    float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind);
}

/// <summary>An embedder that also turns picture files into vectors of the same space as its texts.</summary>
public interface IImageEmbedder
{
    /// <summary>The vector of the picture, or null when the file format is not supported.</summary>
    float[]? EmbedImage(string path);

    /// <summary>
    /// The vectors of several pictures, in the order of <paramref name="paths"/>; null for a picture that is unsupported or cannot be read.
    /// An embedder that runs pictures in step on its device overrides this; the default goes one by one.
    /// </summary>
    float[]?[] EmbedImages(IReadOnlyList<string> paths)
    {
        var vectors = new float[]?[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            try { vectors[i] = EmbedImage(paths[i]); }
            catch (InvalidDataException) { }
        }
        return vectors;
    }
}

/// <summary>The model cannot be used now (not downloaded, device not supported). Search and indexing continue without vectors.</summary>
public sealed class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
