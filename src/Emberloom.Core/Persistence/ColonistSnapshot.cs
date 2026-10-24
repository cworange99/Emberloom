using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Life;
using Emberloom.Core.Memory;

namespace Emberloom.Core.Persistence;

/// <summary>
/// One mind and everything it is carrying: who it is, what it wants, what it remembers, what it
/// has come to believe, and the calls that wrote any of that.
/// <para>
/// Beliefs keep their embeddings. They are the expensive part of a colony — every one of them cost
/// a call to compute — and a load that dropped them would make a restored colony quietly stupider
/// than the one that was saved, because duplicate-belief detection falls back to word overlap
/// without them.
/// </para>
/// </summary>
public sealed record ColonistSnapshot
{
    public required string Id { get; init; }
    public required string Designation { get; init; }
    public string? Name { get; init; }
    public string? Epithet { get; init; }
    public required LineageKind Lineage { get; init; }
    public required IReadOnlyList<string> ParentIds { get; init; }
    public required double BornOnDay { get; init; }
    public required double DiedOnDay { get; init; }
    public required bool IsAlive { get; init; }
    public required double LifespanDays { get; init; }
    public required bool IsElder { get; init; }

    public double Wellbeing { get; init; } = WellbeingRules.Start;
    public required double LastPairedOnDay { get; init; }
    public required string Personality { get; init; }
    public required string Seed { get; init; }
    public required bool IsKindled { get; init; }
    public required IReadOnlyList<string> NameCandidates { get; init; }
    public required IReadOnlyDictionary<Drive, double> Weights { get; init; }
    public required IReadOnlyDictionary<Drive, double> Drives { get; init; }
    public required string PlaceId { get; init; }
    public string? DestinationPlaceId { get; init; }
    public string? LastThought { get; init; }
    public required IReadOnlyDictionary<string, string> PendingWords { get; init; }
    public CurrentActionSnapshot? Current { get; init; }
    public required IReadOnlyList<EpisodicMemory> Episodic { get; init; }
    public required IReadOnlyList<ConsolidatedMemory> Consolidated { get; init; }
    public required IReadOnlyList<Belief> Beliefs { get; init; }
    public required IReadOnlyList<ModelCallRecord> Trace { get; init; }

    public static ColonistSnapshot Of(Colonist colonist) => new()
    {
        Id = colonist.Id,
        Designation = colonist.Designation,
        Name = colonist.Name,
        Epithet = colonist.Epithet,
        Lineage = colonist.Lineage,
        ParentIds = colonist.ParentIds.ToList(),
        BornOnDay = colonist.BornOnDay,
        DiedOnDay = colonist.DiedOnDay,
        IsAlive = colonist.IsAlive,
        LifespanDays = colonist.LifespanDays,
        IsElder = colonist.IsElder,
        Wellbeing = colonist.Wellbeing,
        LastPairedOnDay = colonist.LastPairedOnDay,
        Personality = colonist.Personality,
        Seed = colonist.Seed,
        IsKindled = colonist.IsKindled,
        NameCandidates = colonist.NameCandidates.ToList(),
        Weights = DriveTable.All.ToDictionary(d => d, d => colonist.Weights[d]),
        Drives = DriveTable.All.ToDictionary(d => d, d => colonist.Drives[d]),
        PlaceId = colonist.PlaceId,
        DestinationPlaceId = colonist.DestinationPlaceId,
        LastThought = colonist.LastThought,
        PendingWords = colonist.PendingWords.ToDictionary(p => p.Key, p => p.Value),
        Current = colonist.Current is { } action
            ? new CurrentActionSnapshot(action.Definition.Kind, action.Target.Id, action.PartnerId,
                action.TicksElapsed, action.ChosenScore, action.TravelTicks)
            : null,
        Episodic = colonist.Memory.Episodic.ToList(),
        Consolidated = colonist.Memory.Consolidated.ToList(),
        Beliefs = colonist.Memory.Beliefs.ToList(),
        Trace = colonist.Trace.ToList()
    };

    public Colonist Rebuild(Colony colony)
    {
        var colonist = new Colonist
        {
            Id = Id,
            Designation = Designation,
            Name = Name,
            Epithet = Epithet,
            Lineage = Lineage,
            ParentIds = ParentIds.ToList(),
            BornOnDay = BornOnDay,
            DiedOnDay = DiedOnDay,
            IsAlive = IsAlive,
            LifespanDays = LifespanDays,
            IsElder = IsElder,
            Wellbeing = Wellbeing,
            LastPairedOnDay = LastPairedOnDay,
            Personality = Personality,
            Seed = Seed,
            IsKindled = IsKindled,
            NameCandidates = NameCandidates.ToList(),
            Weights = new PersonalityWeights(new DriveVector(Weights, fallback: 1.0)),
            PlaceId = PlaceId,
            DestinationPlaceId = DestinationPlaceId,
            LastThought = LastThought
        };

        foreach (var (drive, level) in Drives)
        {
            colonist.Drives.Levels[drive] = level;
        }

        foreach (var (id, words) in PendingWords)
        {
            colonist.PendingWords[id] = words;
        }

        // An action in flight is rebuilt against this build's catalog rather than the saved
        // definition: durations and relief are balance, and a loaded colony should run on the
        // balance it is loaded into, not the one it was saved from.
        if (Current is { } action && colony.Map.Find(action.TargetId) is { } target)
        {
            colonist.Current = new CurrentAction
            {
                Definition = ActionCatalog.ByKind(action.Kind),
                Target = target,
                PartnerId = action.PartnerId,
                TicksElapsed = action.TicksElapsed,
                ChosenScore = action.ChosenScore,
                TravelTicks = action.TravelTicks
            };
        }

        colonist.Memory.Restore(Episodic, Consolidated, Beliefs);

        foreach (var call in Trace)
        {
            colonist.RecordCall(call);
        }

        return colonist;
    }
}

public sealed record CurrentActionSnapshot(
    ActionKind Kind, string TargetId, string? PartnerId, int TicksElapsed, double ChosenScore, int TravelTicks);
