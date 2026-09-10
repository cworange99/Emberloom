using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;

namespace Emberloom.Api;

/// <summary>
/// The wire shapes. Deliberately flat and denormalised — the client redraws from a snapshot
/// rather than maintaining its own model of the colony.
/// </summary>
public sealed record PlaceDto(
    string Id,
    string Name,
    string Kind,
    double X,
    double Y,
    int Level,
    double Danger,
    double Light,
    bool IsExplored,
    bool IsBlocked,
    double ClearingProgress,
    int SalvageRemaining,
    IReadOnlyList<string> Neighbours)
{
    public static PlaceDto From(Place place) => new(
        place.Id,
        place.Name,
        place.Kind.ToString(),
        place.X,
        place.Y,
        place.Level,
        place.Danger,
        place.Light,
        place.IsExplored,
        place.IsBlocked,
        place.ClearingProgress,
        place.SalvageRemaining,
        place.Neighbours);
}

public sealed record ColonistDto(
    string Id,
    string Designation,
    string? Name,
    string DisplayName,
    string Lineage,
    double AgeDays,
    string Mood,
    string PlaceId,
    string? Doing,
    string? DoingTarget,
    double Progress,
    bool IsTravelling,
    string? Thought,
    int MemoryCount,
    bool WantsReflection,
    int ModelCalls,
    bool IsKindled,
    IReadOnlyList<string> NameCandidates,
    IReadOnlyDictionary<string, double> Drives,
    IReadOnlyDictionary<string, double> Weights,
    string DominantDrive)
{
    public static ColonistDto From(Colonist colonist, Colony colony) => new(
        colonist.Id,
        colonist.Designation,
        colonist.Name,
        colonist.DisplayName,
        colonist.Lineage.ToString(),
        Math.Round(colonist.AgeInDays(colony.Day), 2),
        colonist.Mood(colony.Day),
        colonist.PlaceId,
        colonist.Current?.Definition.Kind.ToString(),
        colonist.Current?.Target.Name,
        colonist.Current?.Progress ?? 0,
        colonist.Current is not null && colonist.PlaceId != colonist.Current.Target.Id,
        colonist.LastThought,
        colonist.Memory.Episodic.Count,
        colonist.Memory.WantsReflection(colony.Day),
        colonist.ModelCalls,
        colonist.IsKindled,
        colonist.NameCandidates,
        colonist.Drives.Levels.ToSchemaDictionary(),
        colonist.Weights.Vector.ToSchemaDictionary(),
        DriveTable.Describe(colonist.Drives.Dominant(colonist.Weights)));
}

public sealed record EventDto(long Tick, double Day, string Layer, string Text, string? ColonistId, string? PlaceId, bool Notable)
{
    public static EventDto From(ColonyEvent e) =>
        new(e.Tick, Math.Round(e.Day, 3), e.Layer.ToString().ToLowerInvariant(), e.Text, e.ColonistId, e.PlaceId, e.IsNotable);
}

/// <summary>
/// What the speed control and the Contemplation readout need. Even with no mind attached, the
/// readout is meaningful — it is the arithmetic of §6, and it should be visible from M1.
/// </summary>
public sealed record ClockDto(
    string Speed,
    string SpeedLabel,
    bool AutoSlowOnEvent,
    bool ThrottledByGovernor,
    double ColonyDaysPerRealMinute,
    double CallsPerColonyDay,
    double SustainedCallsPerMinute);

/// <summary>
/// The governor, exposed. On a game whose whole premise is local AI, how much thinking is
/// happening right now is genuinely interesting rather than a debug leftover — and it is what
/// lets someone on better hardware turn the ceiling up and watch the colony think harder.
/// </summary>
public sealed record GovernorDto(
    int QueueDepth,
    int TokensPerMinute,
    int TokensInLastMinute,
    double Pressure,
    int Dropped,
    int Served,
    int Fallbacks,
    string? CurrentCall,
    bool Throttling);

public sealed record ColonyStateDto(
    long Tick,
    double Day,
    double TimeOfDay,
    bool IsNight,
    double LoomCharge,
    int ArtifactsRecovered,
    int Population,
    int ActionsCompleted,
    ClockDto Clock,
    GovernorDto? Governor,
    IReadOnlyList<ColonistDto> Colonists);

/// <summary>Sent once when a client connects, so it does not have to rebuild the map every frame.</summary>
public sealed record WorldDto(
    IReadOnlyList<PlaceDto> Places,
    IReadOnlyList<string> DriveNames,
    IReadOnlyList<string> Speeds);

public sealed record TraceRowDto(
    string CallType,
    double Day,
    string Model,
    string Lane,
    int PromptTokens,
    int CompletionTokens,
    double Seconds,
    string Produced,
    bool WasFallback);

public sealed record ColonistDetailDto(
    ColonistDto Colonist,
    string Personality,
    string Seed,
    IReadOnlyList<TraceRowDto> Trace,
    IReadOnlyList<ConsolidatedRowDto> Consolidated,
    IReadOnlyList<BeliefRowDto> Beliefs,
    IReadOnlyList<MemoryRowDto> RecentMemories,
    IReadOnlyList<RelationshipRowDto> Relationships,
    IReadOnlyList<ScoredOptionDto> Considering);

public sealed record MemoryRowDto(string Id, double Day, string Text, string PlaceId, double Valence, double Intensity, double Salience);

/// <summary>A written memory standing in for a batch of episodic rows, with the rows it came from.</summary>
public sealed record ConsolidatedRowDto(
    string Id,
    double Day,
    string Text,
    double Valence,
    double Salience,
    int RecallCount,
    IReadOnlyList<MemoryRowDto> Sources);

/// <summary>
/// A belief and the chain behind it. The source rows travel with it deliberately: a belief the
/// inspector cannot walk back to the tick it came from is the one thing this design promises
/// never to show.
/// </summary>
public sealed record BeliefRowDto(
    string Id,
    string Claim,
    double FormedOnDay,
    double Confidence,
    int RecallCount,
    bool ContradictsColonyMyth,
    IReadOnlyList<MemoryRowDto> Sources);

public sealed record RelationshipRowDto(string TowardId, string TowardName, double Affinity, double Trust, double Respect, int SharedMemories, string Stage, string? Narrative);

/// <summary>
/// The runners-up in the utility scoring. This is the honest version of the inspector's "chosen
/// over forage (0.31)" line, and it costs nothing because the scorer already computes them.
/// </summary>
public sealed record ScoredOptionDto(string Action, string PlaceName, string? PartnerId, double Score, bool Chosen);
