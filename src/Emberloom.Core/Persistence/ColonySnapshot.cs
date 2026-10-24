using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Memory;
using Emberloom.Core.Relationships;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Emberloom.Core.World;

namespace Emberloom.Core.Persistence;

/// <summary>
/// A whole colony, flattened into plain records — M11.
/// <para>
/// Until now nothing survived the process: close the server and the colony was gone, which is why
/// the chronicle exists at all. A chronicle is an account of a colony, though, not the colony. With
/// buildings going up over hundreds of days and an economy coming, a run has to be able to outlive
/// the machine it started on.
/// </para>
/// <para>
/// This is deliberately a separate shape from the domain rather than the domain serialised. The
/// domain changes every milestone — four of the next five add fields — and a save format that
/// tracked it exactly would break every colony on disk each time. Here, a field added to
/// <see cref="Colonist"/> costs a line here and nothing on disk, and an old save loads with its
/// default. It also keeps the storage dependency out of Core: this layer is pure records and does
/// no I/O at all, so the whole of it is testable without a database.
/// </para>
/// <para>
/// What is NOT saved: the map's shape. Places are hand-authored in <see cref="CityMap.CreateDefault"/>
/// and only their mutable state is written, so a save is a colony rather than a world — which also
/// means a save made before a new site was added still loads.
/// </para>
/// </summary>
public sealed record ColonySnapshot
{
    /// <summary>
    /// Bumped whenever a load from an older file would be wrong rather than merely incomplete.
    /// Fields added without changing meaning do not need it: they arrive as defaults.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.Now;

    public required int Seed { get; init; }
    public required ColonyMode Mode { get; init; }
    public required long Tick { get; init; }
    public required double LoomCharge { get; init; }
    public required double LoomChargePerDay { get; init; }

    public double ChargePerWorkDay { get; init; } = 0.5;

    public double DrawPerHeadPerDay { get; init; } = 0.15;

    public double ProducedPerDay { get; init; }

    public double ProducingToday { get; init; }

    public IReadOnlyList<Verbs.Intent> Intents { get; init; } = [];

    public double Shortfall { get; init; }

    public bool StoresOverflowing { get; init; }
    public required int ArtifactsRecovered { get; init; }
    public required long NextMemoryId { get; init; }

    /// <summary>
    /// Where the random stream had got to. Null for a colony driven by a plain Random, which saves
    /// its state faithfully but cannot promise the same next day — see ColonyRandom.
    /// </summary>
    public ulong? RandomState { get; init; }

    public required IReadOnlyList<PlaceSnapshot> Places { get; init; }
    public required IReadOnlyList<ColonistSnapshot> Colonists { get; init; }
    public required IReadOnlyList<RelationshipSnapshot> Relationships { get; init; }
    public required IReadOnlyList<ProvisionSnapshot> Provisions { get; init; }
    public required IReadOnlyList<CoreSnapshot> Cores { get; init; }
    public required IReadOnlyList<CoinedWord> Lexicon { get; init; }
    public required IReadOnlyList<MythTelling> MythHistory { get; init; }
    public required IReadOnlyList<ForkSnapshot> ForkHistory { get; init; }
    public ForkSnapshot? OpenFork { get; init; }

    public required IReadOnlyList<string> ForbiddenPlaces { get; init; }
    public required IReadOnlyList<string> CoinedOccasions { get; init; }
    public required IReadOnlyDictionary<string, string> Revealed { get; init; }
    public required IReadOnlyDictionary<string, double> Declined { get; init; }
    public required IReadOnlyDictionary<ForkKind, double> AskAgainOn { get; init; }
    public required IReadOnlyList<EventSnapshot> Feed { get; init; }

    // ---- capture ---------------------------------------------------------------------------

    public static ColonySnapshot Capture(Colony colony, BodySimulation body) => new()
    {
        Seed = colony.Seed,
        Mode = colony.Mode,
        Tick = colony.Tick,
        LoomCharge = colony.LoomCharge,
        LoomChargePerDay = colony.LoomChargePerDay,
        ChargePerWorkDay = colony.ChargePerWorkDay,
        DrawPerHeadPerDay = colony.DrawPerHeadPerDay,
        ProducedPerDay = colony.ProducedPerDay,
        ProducingToday = colony.ProducingToday,
        Intents = colony.Intents.ToList(),
        Shortfall = colony.Shortfall,
        StoresOverflowing = colony.StoresOverflowing,
        ArtifactsRecovered = colony.ArtifactsRecovered,
        NextMemoryId = body.NextMemoryId,
        RandomState = body.RandomState,
        Places = colony.Map.Places.Select(PlaceSnapshot.Of).ToList(),
        Colonists = colony.Colonists.Select(ColonistSnapshot.Of).ToList(),
        Relationships = colony.Relationships.All.Select(RelationshipSnapshot.Of).ToList(),
        Provisions = colony.Provisions.Select(p => new ProvisionSnapshot(p.Kind, p.PlaceId, p.CostPerDay, p.SinceDay)).ToList(),
        Cores = colony.Cores.Select(c => new CoreSnapshot(c.ColonistId, c.DiedOnDay, c.Fate, c.DecidedOnDay, c.Heard)).ToList(),
        Lexicon = colony.Lexicon.ToList(),
        MythHistory = colony.MythHistory.ToList(),
        ForkHistory = colony.ForkHistory.Select(ForkSnapshot.Of).ToList(),
        OpenFork = colony.OpenFork is { } open ? ForkSnapshot.Of(open) : null,
        ForbiddenPlaces = colony.ForbiddenPlaces.ToList(),
        CoinedOccasions = colony.CoinedOccasions.ToList(),
        Revealed = colony.Revealed.ToDictionary(p => p.Key, p => p.Value),
        Declined = colony.Declined.ToDictionary(p => p.Key, p => p.Value),
        AskAgainOn = colony.AskAgainEntries.ToDictionary(p => p.Key, p => p.Value),
        Feed = colony.Feed.Events.Select(EventSnapshot.Of).ToList()
    };

    // ---- restore ---------------------------------------------------------------------------

    /// <summary>
    /// The colony this was taken from, and a body wound to the same point. The random stream is
    /// rebuilt from the seed and then advanced is *not* — a loaded colony draws the same numbers a
    /// fresh one of that seed would, which is a deliberate limit: a save is faithful in its state,
    /// not in its future. See the round-trip test for exactly what that guarantees.
    /// </summary>
    public (Colony Colony, BodySimulation Body) Restore()
    {
        if (Version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"This colony was saved by a later version of the game (save {Version}, this build reads {CurrentVersion}).");
        }

        var colony = new Colony(CityMap.CreateDefault())
        {
            Seed = Seed,
            Mode = Mode,
            Tick = Tick,
            LoomCharge = LoomCharge,
            LoomChargePerDay = LoomChargePerDay,
            ChargePerWorkDay = ChargePerWorkDay,
            DrawPerHeadPerDay = DrawPerHeadPerDay,
            ProducedPerDay = ProducedPerDay,
            ProducingToday = ProducingToday,
            Shortfall = Shortfall,
            StoresOverflowing = StoresOverflowing,
            ArtifactsRecovered = ArtifactsRecovered
        };

        foreach (var place in Places)
        {
            place.ApplyTo(colony.Map.Find(place.Id));
        }

        foreach (var colonist in Colonists)
        {
            colony.Add(colonist.Rebuild(colony));
        }

        foreach (var tie in Relationships)
        {
            colony.Relationships.Restore(tie.FromId, tie.ToId, tie.Affinity, tie.Trust, tie.Respect, tie.SharedMemories, tie.Narrative);
        }

        colony.Provisions.AddRange(Provisions.Select(p => new Provision(p.Kind, p.PlaceId, p.CostPerDay, p.SinceDay)));

        colony.Cores.AddRange(Cores.Select(c => new MindCore
        {
            ColonistId = c.ColonistId,
            DiedOnDay = c.DiedOnDay,
            Fate = c.Fate,
            DecidedOnDay = c.DecidedOnDay,
            Heard = c.Heard
        }));

        colony.Lexicon.AddRange(Lexicon);
        colony.MythHistory.AddRange(MythHistory);
        colony.Intents.AddRange(Intents);
        colony.ForkHistory.AddRange(ForkHistory.Select(f => f.Rebuild()));
        colony.OpenFork = OpenFork?.Rebuild();

        foreach (var id in ForbiddenPlaces)
        {
            colony.ForbiddenPlaces.Add(id);
        }

        foreach (var occasion in CoinedOccasions)
        {
            colony.CoinedOccasions.Add(occasion);
        }

        foreach (var (id, who) in Revealed)
        {
            colony.Revealed[id] = who;
        }

        foreach (var (id, until) in Declined)
        {
            colony.Declined[id] = until;
        }

        foreach (var (kind, day) in AskAgainOn)
        {
            colony.PutOff(kind, day);
        }

        foreach (var line in Feed)
        {
            colony.Feed.Append(line.Rebuild());
        }

        // The stream is wound back to exactly where it was, so the loaded colony goes on to live
        // the life the saved one would have. Falling back to the seed is for saves taken from a
        // colony that was not using a saveable stream.
        var random = RandomState is { } state ? ColonyRandom.FromState(state) : new ColonyRandom(Seed);
        var body = new BodySimulation(colony, random) { NextMemoryId = NextMemoryId };
        return (colony, body);
    }
}

public sealed record PlaceSnapshot(
    string Id,
    PlaceKind Kind,
    bool IsExplored,
    int SalvageRemaining,
    double ClearingDone,
    double BuildDone,
    bool IsApproved)
{
    public static PlaceSnapshot Of(Place place) => new(
        place.Id, place.Kind, place.IsExplored, place.SalvageRemaining,
        place.ClearingDone, place.BuildDone, place.IsApproved);

    /// <summary>
    /// Only the mutable half. A place missing from the map is one this build does not have — a save
    /// from a version with sites this one lacks loads without it rather than refusing to load.
    /// </summary>
    public void ApplyTo(Place? place)
    {
        if (place is null)
        {
            return;
        }

        place.Kind = Kind;
        place.IsExplored = IsExplored;
        place.SalvageRemaining = SalvageRemaining;
        place.ClearingDone = ClearingDone;
        place.BuildDone = BuildDone;
        place.IsApproved = IsApproved;
    }
}

public sealed record RelationshipSnapshot(
    string FromId, string ToId, double Affinity, double Trust, double Respect, int SharedMemories, string? Narrative)
{
    public static RelationshipSnapshot Of(Relationship tie) =>
        new(tie.FromId, tie.ToId, tie.Affinity, tie.Trust, tie.Respect, tie.SharedMemories, tie.Narrative);
}

public sealed record ProvisionSnapshot(ProvisionKind Kind, string PlaceId, double CostPerDay, double SinceDay);

public sealed record CoreSnapshot(string ColonistId, double DiedOnDay, CoreFate Fate, double? DecidedOnDay, IReadOnlyList<string> Heard);

public sealed record EventSnapshot(long Tick, double Day, EventLayer Layer, string Text, string? ColonistId, string? PlaceId, bool IsNotable, string? Quote)
{
    public static EventSnapshot Of(ColonyEvent e) =>
        new(e.Tick, e.Day, e.Layer, e.Text, e.ColonistId, e.PlaceId, e.IsNotable, e.Quote);

    public ColonyEvent Rebuild() => new()
    {
        Tick = Tick, Day = Day, Layer = Layer, Text = Text,
        ColonistId = ColonistId, PlaceId = PlaceId, IsNotable = IsNotable, Quote = Quote
    };
}

public sealed record VoteSnapshot(string ColonistId, Stance Stance, double Conviction, string Line, string Reasoning, bool WasFallback);

public sealed record ForkSnapshot(
    string Id,
    ForkKind Kind,
    string Question,
    string Proposal,
    string Stakes,
    string? SubjectPlaceId,
    string? SubjectColonistId,
    string ForMeans,
    string AgainstMeans,
    string AbstainMeans,
    string PlaceId,
    double OpenedOnDay,
    IReadOnlyList<string> Participants,
    IReadOnlyList<VoteSnapshot> Votes,
    bool? Carried,
    Stance? Prevailed,
    string? Outcome,
    double? ResolvedOnDay,
    double Pressure = 0,

    /// <summary>
    /// Where each member stood when the question was put. Defaulted rather than required so a
    /// colony saved before this existed still loads; an open fork restored without it is answered
    /// on the standing of whoever is in the room, which is what every fork used to do.
    /// </summary>
    IReadOnlyDictionary<string, double>? Standing = null)
{
    public static ForkSnapshot Of(Fork fork) => new(
        fork.Id, fork.Kind, fork.Question, fork.Proposal, fork.Stakes,
        fork.SubjectPlaceId, fork.SubjectColonistId,
        fork.ForMeans, fork.AgainstMeans, fork.AbstainMeans,
        fork.PlaceId, fork.OpenedOnDay, fork.Participants.ToList(),
        fork.Votes.Select(v => new VoteSnapshot(v.Key, v.Value.Stance, v.Value.Conviction, v.Value.Line, v.Value.Reasoning, v.Value.WasFallback)).ToList(),
        fork.Carried, fork.Prevailed, fork.Outcome, fork.ResolvedOnDay, fork.Pressure,
        fork.Standing.ToDictionary(p => p.Key, p => p.Value));

    public Fork Rebuild()
    {
        var fork = new Fork
        {
            Id = Id,
            Kind = Kind,
            Question = Question,
            Proposal = Proposal,
            Stakes = Stakes,
            Pressure = Pressure,
            SubjectPlaceId = SubjectPlaceId,
            SubjectColonistId = SubjectColonistId,
            ForMeans = ForMeans,
            AgainstMeans = AgainstMeans,
            AbstainMeans = AbstainMeans,
            PlaceId = PlaceId,
            OpenedOnDay = OpenedOnDay,
            Participants = Participants.ToList()
        }.WithStanding(Standing ?? new Dictionary<string, double>());

        foreach (var vote in Votes)
        {
            fork.Cast(vote.ColonistId, new Vote(vote.Stance, vote.Conviction, vote.Line, vote.Reasoning, vote.WasFallback));
        }

        if (Carried is { } carried && Outcome is { } outcome && ResolvedOnDay is { } decided)
        {
            fork.Decide(carried, Prevailed, outcome, decided);
        }

        return fork;
    }
}
