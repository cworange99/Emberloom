namespace Emberloom.Core.Memory;

/// <summary>
/// Whether a new belief restates one a mind already holds.
/// <para>
/// Compares embeddings where both claims have one, and falls back to overlap of content words where
/// either does not — an unreachable embedding model costs precision here, never correctness. The
/// embeddings are the same local vectors retrieval uses; nothing is asked of the server beyond the
/// stateless text-in, vector-out call that made them.
/// </para>
/// </summary>
public static class BeliefSimilarity
{
    /// <summary>
    /// Raw cosine at or above which two claims' embeddings say the same thing. Calibrated at M4 on
    /// the 848 within-mind belief pairs of a 47-day laptop colony, embedded with nomic-embed-text.
    /// At 0.92 and above there were three pairs, all plain restatements ("Trust is earned through
    /// actions, not just interactions" / "…not just presence"). Between 0.90 and 0.92, restatements
    /// mix with pairs that differ in direction — teaching others against being taught — and by 0.86
    /// two genuinely different beliefs about the Loom score higher than some true restatements do.
    /// "I am useful" / "My usefulness is proven" sits at 0.85, in that crowd, so it is deliberately
    /// left apart: a missed duplicate costs a repeated line, while a wrong merge attaches one
    /// belief's evidence to another belief's words.
    /// </summary>
    public const double EmbeddingThreshold = 0.92;

    /// <summary>
    /// Share of content words in common at or above which two claims say the same thing, used when
    /// there are no embeddings. On the same 848 pairs, 0.6 and above caught two restatements and
    /// nothing else.
    /// </summary>
    public const double WordThreshold = 0.6;

    public static bool Restates(Belief candidate, Belief held)
    {
        var (score, byEmbedding) = Compare(candidate, held);
        return score >= (byEmbedding ? EmbeddingThreshold : WordThreshold);
    }

    public static (double Score, bool ByEmbedding) Compare(Belief a, Belief b)
    {
        if (a.Embedding is { Length: > 0 } left
            && b.Embedding is { Length: > 0 } right
            && left.Length == right.Length)
        {
            return (Cosine(left, right), true);
        }

        return (WordOverlap(a.Claim, b.Claim), false);
    }

    /// <summary>
    /// Plain cosine, -1 to 1. Not <see cref="Retrieval.CosineSimilarity"/>, which is shifted into
    /// 0..1 for ranking and would make a threshold here mean something different.
    /// </summary>
    public static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftMagnitude = 0, rightMagnitude = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        return leftMagnitude <= 0 || rightMagnitude <= 0
            ? 0
            : dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    /// <summary>Jaccard overlap of the two claims' content words, crudely stemmed.</summary>
    public static double WordOverlap(string left, string right)
    {
        var a = ContentWords(left);
        var b = ContentWords(right);

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static HashSet<string> ContentWords(string text) =>
        text.ToLowerInvariant()
            .Split(NonLetters, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !Stopwords.Contains(word))
            .Select(Stem)
            .Where(word => word.Length >= 2)
            .ToHashSet();

    /// <summary>
    /// Deliberately crude: enough that "useful" meets "usefulness" and "make" meets "making". A real
    /// stemmer would be better and is not worth a dependency for a fallback path.
    /// </summary>
    private static string Stem(string word)
    {
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var suffix in Suffixes)
            {
                if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal))
                {
                    word = word[..^suffix.Length];
                    changed = true;
                    break;
                }
            }
        }

        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    private static readonly string[] Suffixes = ["ness", "ing", "ful", "ed", "ly", "es", "s"];

    private static readonly char[] NonLetters =
        " \t\r\n.,;:!?'\"“”‘’()[]{}-—–/\\0123456789".ToCharArray();

    private static readonly HashSet<string> Stopwords =
    [
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "for", "with", "at", "by",
        "from", "as", "is", "are", "am", "was", "were", "be", "been", "being", "it", "its", "this",
        "that", "these", "those", "i", "me", "my", "we", "our", "us", "you", "your", "they", "them",
        "their", "he", "she", "his", "her", "can", "will", "would", "should", "could", "not", "no",
        "so", "than", "then", "when", "who", "what", "which", "do", "does", "did", "have", "has",
        "had", "just", "more", "most", "very", "all", "any", "each", "every", "s"
    ];
}
