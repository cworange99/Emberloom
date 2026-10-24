using Emberloom.Core.Colonists;
using Emberloom.Core.Relationships;

namespace Emberloom.Core.Life;

/// <summary>
/// Ageing, pairing and dying — DESIGN §8. All body: a life begins and ends with no model call,
/// and a colony with no mind still has generations. What the mind adds is who the child turns out
/// to be (Kindling) and what the colony decides to do with the dead (a core fork).
/// </summary>
public static class LifeRules
{
    /// <summary>
    /// A life, in colony days: about three real months at 1×, where a colony-day is an hour — long
    /// enough for someone to come to know the people in it (decided 13 September 2026). At the
    /// Overnight speed a life is about nine hours. It was 40 days while a day was two minutes, and
    /// the overnight run showed that far too quick to love anyone in.
    /// <para>
    /// Everything else about a life below is kept as the same share of it that the headless sim
    /// showed holds a colony steady at 8 to 16: stretch the life, and those stretch with it.
    /// </para>
    /// </summary>
    public const double BaseLifespanDays = 2160;

    /// <summary>Lives vary by this much either way, so a generation does not die in one afternoon.</summary>
    public const double LifespanSpread = 0.2;

    /// <summary>The last stretch of a life, when the body starts to slow.</summary>
    public const double ElderFraction = 0.85;

    /// <summary>How hard age presses on Rest, per colony day. The old sleep more.</summary>
    public const double ElderWearPerDay = 0.4;

    /// <summary>
    /// Nobody pairs until they have become someone: 30% of a life. Measured with the headless sim
    /// while a life was 40 days — at 15% a colony reached 8 generations in 3 lives, faster than
    /// anyone could reflect their way to a belief worth inheriting; at 30% it held steady.
    /// </summary>
    public const double MaturityDays = BaseLifespanDays * 0.3;

    public const double PairCooldownDays = BaseLifespanDays * 0.3;

    /// <summary>
    /// Whether this colonist's work feeds the colony yet — M14. The Loom prints grown people, so a
    /// printed mind pays its way from the hour it wakes; a child does not, and has
    /// <see cref="MaturityDays"/> of being fed by everyone else first. That is what makes a birth
    /// cost the colony something, and why a print's cost is the charge it was printed with instead.
    /// <para>
    /// Deliberately a lower bar than pairing, which keeps <see cref="MaturityDays"/> for everyone
    /// including the founders: being able to carry a load and being ready to raise someone are not
    /// the same threshold, and the second one is the slower of the two in every society.
    /// </para>
    /// </summary>
    public static bool IsGrown(Colonist colonist, double day) =>
        colonist.Lineage == LineageKind.Printed || colonist.AgeInDays(day) >= MaturityDays;

    /// <summary>
    /// How long a core waits for the colony before it is kept, unread, by default. Deaths can come
    /// faster than councils decide, and cores outrank prints: in the sim a backlog of five waiting
    /// cores held every Print fork off for good.
    /// </summary>
    public const double UndecidedCoreDays = 6;

    /// <summary>
    /// A bonded pair's chance, each day, of a successor. Pairing is cheap, not constant: a pair
    /// that can waits about 7% of a life on average, as it did at 0.35 a day in a 40-day life.
    /// </summary>
    public const double PairChancePerDay = 0.35 * 40 / BaseLifespanDays;

    /// <summary>The city has room, but not endless room — and every mind costs the governor.</summary>
    public const int PopulationCap = 20;

    /// <summary>How many of each parent's strongest memories a child dreams.</summary>
    public const int DreamsPerParent = 2;

    /// <summary>
    /// What a child's inherited memories read as: theirs, but not quite. "They half-believe and
    /// cannot source" them (DESIGN §8), and nothing in the memory says whose it was.
    /// </summary>
    public const string DreamVerb = "dreams, and half believes, that they";

    /// <summary>
    /// A printed mind's lifespan, fixed by its designation. Drawn this way rather than from the
    /// colony's random stream so that adding lives did not reshuffle every colony that already
    /// existed — the same seed still grows the same colony.
    /// </summary>
    public static double Lifespan(string designation)
    {
        var unit = StableHash(designation) % 1000 / 999.0;
        return BaseLifespanDays * (1 - LifespanSpread + 2 * LifespanSpread * unit);
    }

    /// <summary>A born mind's lifespan, drawn from the colony's random stream like everything else about them.</summary>
    public static double Lifespan(Random random) =>
        BaseLifespanDays * (1 - LifespanSpread + 2 * LifespanSpread * random.NextDouble());

    /// <summary>How one colonist feels about another: affinity and trust together.</summary>
    public static double Closeness(Colony colony, string fromId, string toId) =>
        colony.Relationships.Peek(fromId, toId) is { } r ? r.Affinity + r.Trust : 0;

    /// <summary>Close enough that their death is a loss rather than news.</summary>
    public static bool IsClose(Colony colony, string fromId, string toId) => Closeness(colony, fromId, toId) > 0.5;

    /// <summary>
    /// Two colonists past a bond threshold — both ways, since a bond is asymmetric — grown, not yet
    /// slowing, not recently parents, and not already family.
    /// </summary>
    public static bool CanPair(Colony colony, Colonist a, Colonist b)
    {
        if (!a.IsAlive || !b.IsAlive || a.Id == b.Id || a.IsElder || b.IsElder)
        {
            return false;
        }

        var day = colony.Day;

        if (a.AgeInDays(day) < MaturityDays || b.AgeInDays(day) < MaturityDays)
        {
            return false;
        }

        if (day - a.LastPairedOnDay < PairCooldownDays || day - b.LastPairedOnDay < PairCooldownDays)
        {
            return false;
        }

        if (a.ParentIds.Contains(b.Id) || b.ParentIds.Contains(a.Id) || a.ParentIds.Intersect(b.ParentIds).Any())
        {
            return false;
        }

        return colony.Relationships.Peek(a.Id, b.Id)?.Stage == RelationshipStage.Bonded
               && colony.Relationships.Peek(b.Id, a.Id)?.Stage == RelationshipStage.Bonded;
    }

    /// <summary>
    /// The deepest line of descent: a printed mind is generation one, its children two. Counted
    /// over everyone who has ever lived, because a line that died out still happened.
    /// </summary>
    public static int Generations(Colony colony)
    {
        var depth = new Dictionary<string, int>();

        int Of(Colonist colonist) => depth.TryGetValue(colonist.Id, out var known)
            ? known
            : depth[colonist.Id] = colonist.ParentIds.Count == 0
                ? 1
                : 1 + colonist.ParentIds
                    .Select(colony.Find)
                    .Where(parent => parent is not null)
                    .Select(parent => Of(parent!))
                    .DefaultIfEmpty(0)
                    .Max();

        return colony.Colonists.Count == 0 ? 0 : colony.Colonists.Max(Of);
    }

    /// <summary>
    /// What reading a core gives the colony: what the dead believed, in their own words — or, for a
    /// mind that never came to believe anything, what it lived through most sharply.
    /// </summary>
    public static IReadOnlyList<string> WhatACoreHolds(Colonist dead, double day)
    {
        var beliefs = dead.Memory.Beliefs
            .OrderByDescending(b => b.Confidence)
            .Take(3)
            .Select(b => b.Claim)
            .ToList();

        return beliefs.Count > 0
            ? beliefs
            : dead.Memory.MostSalient(day, 3).Select(m => $"I {m.Describe()}").ToList();
    }

    private static int StableHash(string text)
    {
        var h = 0;
        foreach (var ch in text)
        {
            h = (h * 31 + ch) & 0x7fffffff;
        }

        return h;
    }
}

/// <summary>What became of a dead mind's core. Waiting until the colony decides.</summary>
public enum CoreFate
{
    Waiting,
    Read,
    Archived,
    Destroyed
}

/// <summary>
/// A mind that outlived its body. The colony decides what to do with it — read it, keep it, or
/// destroy it — and that decision is a Deliberation (DESIGN §8).
/// </summary>
public sealed class MindCore
{
    public required string ColonistId { get; init; }

    public required double DiedOnDay { get; init; }

    public CoreFate Fate { get; set; } = CoreFate.Waiting;

    public double? DecidedOnDay { get; set; }

    /// <summary>What the colony heard when it was read. Part of what its story of the makers draws on.</summary>
    public IReadOnlyList<string> Heard { get; set; } = [];
}
