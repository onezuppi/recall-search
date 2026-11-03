using Microsoft.ML.Tokenizers;

namespace Recall.Core.Embed;

/// <summary>
/// XLM-R tokenizer of the E5 models. The SentencePiece model has ids unk=0, bos=1, eos=2; the model expects the fairseq
/// layout bos=0, pad=1, eos=2, unk=3, so every regular piece id is shifted by one.
/// </summary>
public sealed class E5Tokenizer
{
    public const int Bos = 0, Pad = 1, Eos = 2, Unk = 3;
    private const int RegularPieceShift = 1;

    private readonly SentencePieceTokenizer _spm;
    private readonly int _maxTokens;

    public E5Tokenizer(string sentencePiecePath, int maxTokens)
    {
        using var stream = File.OpenRead(sentencePiecePath);
        _spm = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        _maxTokens = maxTokens;
    }

    /// <summary>Ids with the bos id first and the eos id last, cut to the model limit.</summary>
    public int[] Encode(string text)
    {
        var raw = _spm.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var count = Math.Min(raw.Count, _maxTokens - 2);
        var ids = new int[count + 2];
        ids[0] = Bos;
        for (var i = 0; i < count; i++) ids[i + 1] = raw[i] == 0 ? Unk : raw[i] + RegularPieceShift;
        ids[count + 1] = Eos;
        return ids;
    }
}
