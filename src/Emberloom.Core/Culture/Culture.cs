using Emberloom.Core.Colonists;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;

namespace Emberloom.Core.Culture;

/// <summary>What the colony might coin a word for (DESIGN §5, Coinage).</summary>
public enum OccasionKind
{
    /// <summary>Something of the makers' has been found, and nobody knows what it is.</summary>
    Artifact,

    /// <summary>Something done so often it has stopped being an event and become a practice.</summary>
    Ritual
}

/// <summary>An occasion for a word: who, and what about. The body notices it; a mind coins the word.</summary>
public sealed record Occasion(OccasionKind Kind, string ColonistId, string About, string PlaceId);

/// <summary>
/// A word the colony coined. It enters every prompt from then on and is reused verbatim — the
/// colony's own vocabulary, which is how a culture sounds different from the one next door.
/// </summary>
public sealed record CoinedWord
{
    public required string Term { get; init; }

    public required string Meaning { get; init; }

    public required string CoinedById { get; init; }

    public required double Day { get; init; }

    public required OccasionKind Occasion { get; init; }

    /// <summary>The Coinage call that wrote it, in the coiner's trace.</summary>
    public string? CallId { get; init; }
}

/// <summary>
/// One telling of the colony's story about its makers. Colony-level and inherited by everyone:
/// it goes into every prompt. Retold as the colony learns more, drifting a little each time
/// (DESIGN §7). The prompt and response are kept here, on this machine, like any call's.
/// </summary>
public sealed record MythTelling
{
    public required int Version { get; init; }

    public required string Story { get; init; }

    public required double ToldOnDay { get; init; }

    /// <summary>How much the colony had to go on when this was told. A retelling waits for more.</summary>
    public required int Evidence { get; init; }

    /// <summary>Beliefs held in the colony that go against this telling.</summary>
    public IReadOnlyList<string> ContradictedBeliefIds { get; init; } = [];

    public string? Model { get; init; }

    public string? Prompt { get; init; }

    public string? Response { get; init; }
}

/// <summary>When a word is coined, when the story is told, and how a colonist earns a name.</summary>
public static class CultureRules
{
    /// <summary>The eighth time the same thing is done, it has become a practice worth a name.</summary>
    public const int RitualRepeats = 8;

    /// <summary>A story needs something to be about. Three things seen, found or decided.</summary>
    public const int FirstTellingEvidence = 3;

    /// <summary>A retelling waits for this much more to go on, and at least a few days.</summary>
    public const int RetellAfterEvidence = 3;

    /// <summary>
    /// The overnight run retold the story every three or four colony days — 158 tellings, most
    /// of them word for word. With lives in months, a telling a month is plenty.
    /// </summary>
    public const double DaysBetweenTellings = 30;

    /// <summary>How many of the colony's newest words go into a prompt. The rest are still theirs.</summary>
    public const int LexiconInPrompts = 10;

    /// <summary>
    /// What the colony has to go on about its makers: what was found, what was shown, what was
    /// read from its dead, and what it has decided together.
    /// </summary>
    public static int Evidence(Colony colony) =>
        colony.ArtifactsRecovered
        + colony.Revealed.Count
        + colony.Cores.Count(c => c.Fate == CoreFate.Read)
        + colony.ForkHistory.Count;

    /// <summary>
    /// Whether the story is due to be told, or retold. Never before the colony has become anyone,
    /// and never while someone is still being written.
    /// </summary>
    public static bool MythDue(Colony colony)
    {
        if (colony.Day < ForkRules.EarliestDay || !colony.Living.Any() || colony.Living.Any(c => c.AwaitingKindling))
        {
            return false;
        }

        var evidence = Evidence(colony);

        return colony.Myth is null
            ? evidence >= FirstTellingEvidence
            : evidence - colony.Myth.Evidence >= RetellAfterEvidence
              && colony.Day - colony.Myth.ToldOnDay >= DaysBetweenTellings;
    }

    /// <summary>
    /// A colonist takes a name the first time they do something worth being named for — the first
    /// of Kindling's candidates nobody living already goes by. A mind never written has none to
    /// take, and keeps its designation.
    /// </summary>
    public static string? TakeAName(Colony colony, Colonist colonist)
    {
        if (colonist.Name is not null)
        {
            return null;
        }

        var taken = colony.Living
            .Select(c => c.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = colonist.NameCandidates.FirstOrDefault(n => !taken.Contains(n));
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        // A name, not a word: live at M7, Kindling's candidates came back as "spark" and "stone",
        // and read in the feed like a typo rather than someone.
        var name = char.ToUpperInvariant(candidate[0]) + candidate[1..];
        colonist.Name = name;
        return name;
    }
}
