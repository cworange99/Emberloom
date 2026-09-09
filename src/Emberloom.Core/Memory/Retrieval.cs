namespace Emberloom.Core.Memory;

public sealed record RetrievedMemory(
    string Id,
    string Text,
    double Similarity,
    double Salience,
    double Recency,
    double Score,
    bool IsBelief);

/// <summary>
/// Retrieval over a colonist's consolidated memories and beliefs, scored on
/// <c>similarity × salience × recency</c> — the Generative Agents scoring, well proven and cheap.
/// <para>
/// <b>This index is deliberately local, and must stay that way.</b> Computing an embedding is a
/// stateless call — text in, vector out, nothing retained — so it may happen on a remote inference
/// server. Storing and searching those vectors is different: an index keyed by colonist living on
/// the server would be exactly the colony state the golden rule forbids it to hold, and would turn
/// hosted deployment from a URL change into a migration problem. See CLAUDE.md.
/// </para>
/// <para>
/// The search is a linear scan with cosine similarity. At the volumes the design predicts — dozens
/// of consolidated memories and 5–20 beliefs per colonist — that is faster than any index would be,
/// and it keeps the door open for sqlite-vec when persistence arrives without changing the scoring.
/// </para>
/// </summary>
public static class Retrieval
{
    /// <summary>
    /// The most relevant things this colonist could bring to mind, best first. Recall bumps the
    /// salience of what it surfaces, so what a mind keeps returning to stays available to it.
    /// </summary>
    public static IReadOnlyList<RetrievedMemory> Search(
        MemoryStore store,
        float[] query,
        double currentDay,
        int count = 5,
        bool bumpRecall = true)
    {
        if (query.Length == 0)
        {
            return [];
        }

        var results = new List<(RetrievedMemory Row, object Source)>();

        foreach (var memory in store.Consolidated)
        {
            if (memory.Embedding is not { Length: > 0 })
            {
                continue;
            }

            var similarity = CosineSimilarity(query, memory.Embedding);
            var salience = memory.Salience(currentDay);
            var recency = Recency(currentDay - memory.Day, halfLifeDays: 30);

            results.Add((
                new RetrievedMemory(
                    memory.Id,
                    memory.Text,
                    similarity,
                    salience,
                    recency,
                    similarity * salience * recency,
                    IsBelief: false),
                memory));
        }

        foreach (var belief in store.Beliefs)
        {
            if (belief.Embedding is not { Length: > 0 })
            {
                continue;
            }

            var similarity = CosineSimilarity(query, belief.Embedding);

            // A belief does not fade the way an episode does — holding one is the point of having
            // formed it — so its salience is its confidence and its recency decays far slower.
            var salience = belief.Confidence;
            var recency = Recency(currentDay - belief.FormedOnDay, halfLifeDays: 120);

            results.Add((
                new RetrievedMemory(
                    belief.Id,
                    belief.Claim,
                    similarity,
                    salience,
                    recency,
                    similarity * salience * recency,
                    IsBelief: true),
                belief));
        }

        var top = results
            .OrderByDescending(r => r.Row.Score)
            .Take(count)
            .ToList();

        if (bumpRecall)
        {
            foreach (var (_, source) in top)
            {
                switch (source)
                {
                    case ConsolidatedMemory memory:
                        memory.RecallCount++;
                        break;
                    case Belief belief:
                        belief.RecallCount++;
                        break;
                }
            }
        }

        return top.Select(r => r.Row).ToList();
    }

    public static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0;
        }

        double dot = 0, leftMagnitude = 0, rightMagnitude = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        // Shifted into 0..1 so a negative similarity cannot flip the sign of the whole score and
        // rank an actively irrelevant memory above a merely unrelated one.
        var cosine = dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
        return (cosine + 1) / 2;
    }

    private static double Recency(double ageInDays, double halfLifeDays) =>
        Math.Pow(0.5, Math.Max(0, ageInDays) / halfLifeDays);
}
