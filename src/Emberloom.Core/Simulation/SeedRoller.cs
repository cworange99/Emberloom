namespace Emberloom.Core.Simulation;

/// <summary>
/// Rolls a seed for the Loom when nobody authored one.
/// <para>
/// Seeds are prose because that is the whole creative input to a new mind — the player can write
/// one directly (the Print verb, M6), or the colony can argue its way to one (a Deliberation, M5).
/// Both of those shipped — this is what stands in when neither wrote one: the founding cast, and
/// every mind a Print fork carries. It composes rather than picking from a list so that a long run
/// does not produce the same eight people repeatedly.
/// </para>
/// </summary>
public static class SeedRoller
{
    private static readonly string[] Dispositions =
    [
        "curious past the point of caution",
        "steady, and slow to be moved",
        "watchful, and quiet about it",
        "restless; cannot stand a repeated day",
        "warm, and needs to be liked",
        "exacting, and hard on themselves first",
        "stubborn in a way that looks like patience",
        "quick to speak, quicker to commit"
    ];

    private static readonly string[] Frictions =
    [
        "resents unearned things",
        "distrusts what the colony agrees on too easily",
        "cannot leave a question alone once it is asked",
        "would rather be useful than be right",
        "keeps score without meaning to",
        "flinches from anything overhead that creaks",
        "believes work is the only honest argument",
        "wants to be the one who names things"
    ];

    private static readonly string[] Leanings =
    [
        "drawn to the parts of the city nobody has opened",
        "happiest with a task that finishes",
        "watches people more closely than places",
        "counts the charge, always",
        "goes back to the same room again and again",
        "cannot be kept indoors for long",
        "looks after whoever is newest",
        "prefers the work nobody volunteers for"
    ];

    public static string Roll(Random random) =>
        $"{Pick(Dispositions, random)}, {Pick(Frictions, random)}; {Pick(Leanings, random)}";

    private static string Pick(IReadOnlyList<string> options, Random random) =>
        options[random.Next(options.Count)];
}
