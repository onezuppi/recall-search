namespace Recall.Core.Embed;

/// <summary>An embedder that can end up unusable while it is still loaded, for example when its server process stopped answering.</summary>
public interface IEmbedderHealth
{
    /// <summary>True once the embedder cannot answer any more. <see cref="EmbedderHost"/> then creates a fresh one on the next use.</summary>
    bool IsBroken { get; }
}
