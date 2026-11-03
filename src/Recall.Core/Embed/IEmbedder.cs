namespace Recall.Core.Embed;

public enum EmbedKind { Passage, Query }

/// <summary>Turns texts into L2-normalized vectors. <see cref="Space"/> names the model revision the vectors belong to.</summary>
public interface IEmbedder : IDisposable
{
    string Space { get; }
    int Dimensions { get; }
    float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind);
}

/// <summary>The model cannot be used now (not downloaded, device not supported). Search and indexing continue without vectors.</summary>
public sealed class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
