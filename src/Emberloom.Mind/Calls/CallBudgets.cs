namespace Emberloom.Mind.Calls;

public enum CallType
{
    Kindling,
    Reflection,
    IdleThought,
    Dialogue,
    Deliberation,
    Coinage,

    /// <summary>The colony's story of its makers, told or retold. Colony-level, not any one mind's.</summary>
    Myth
}

/// <summary>
/// The per-call-type token ceilings. One table, read by the benchmark and by the governor, so
/// that retuning a cap is a single edit rather than a hunt.
/// <para>
/// M0 finding: a schema-constrained call that hits its cap does not degrade gracefully — it
/// truncates mid-document and the whole result is unparseable. So a cap is not a budget hint,
/// it is a correctness constraint, and it must sit above what the schema actually needs.
/// </para>
/// </summary>
public static class CallBudgets
{
    private static readonly Dictionary<CallType, int> MaxTokens = new()
    {
        // Design said ~300. Measured 224–276 across models, so 300 has no headroom on a verbose
        // model; 384 clears the longest observed completion.
        [CallType.Kindling] = 384,

        // Design said ~200. Measured at M0: truncated at 200 on every run of every model — the
        // consolidated memory, up to two source-linked beliefs, a relationship note and seven
        // drift values simply do not fit. This is the cap the design got most wrong. Raised to 448
        // then, and again after M4, when the inspector's trace showed 12 of 92 live reflections on
        // the laptop stopping at exactly 448 and being thrown away after ~71 s of CPU each. A live
        // batch of twelve memories writes longer than the benchmark's shapes did.
        [CallType.Reflection] = 576,

        // Design said ~40. Measured 22–39, so 40 is marginal. 64 costs almost nothing on the
        // fast lane and removes the truncation risk entirely.
        [CallType.IdleThought] = 64,

        // Design said ~80. Measured 34–55, unconstrained, and truncation here only shortens a
        // line rather than corrupting a document. 96 is comfortable.
        [CallType.Dialogue] = 96,

        // Design said ~120. Measured 69–91. Holds.
        [CallType.Deliberation] = 128,

        // Design said ~60. Measured exactly 60 with 2 of 3 truncating — a name plus a sentence
        // of rationale needs more room than it looks.
        [CallType.Coinage] = 112,

        // Not in M0. Three or four sentences of story (~110 tokens) and a short list of belief
        // ids. Set with headroom per the rule above, and to be checked against live traces.
        [CallType.Myth] = 320
    };

    public static int For(CallType call) => MaxTokens[call];

    /// <summary>Lets the benchmark sweep a cap without editing the table.</summary>
    public static void Override(CallType call, int maxTokens) => MaxTokens[call] = maxTokens;

    public static bool TryParse(string name, out CallType call) =>
        Enum.TryParse(name.Replace("-", string.Empty), ignoreCase: true, out call);

    public static IReadOnlyDictionary<CallType, int> Snapshot() => new Dictionary<CallType, int>(MaxTokens);
}
