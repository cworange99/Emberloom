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
    /// Null when paused: contemplation per colony-day is unbounded when no colony-days pass,
    /// and infinity is not a number JSON can carry (System.Text.Json throws on it, which used
    /// to turn the pause button into a 500).
    double? CallsPerColonyDay,
    double SustainedCallsPerMinute,
    bool ThroughputMeasured)
{
    /// <param name="measured">
    /// Whether the calls-per-minute figure comes from calls this machine actually served rather
    /// than the benchmark. The readout says which, because on the laptop they differ by four times.
    /// </param>
    public static ClockDto From(SimulationClock clock, double sustainedCallsPerMinute, bool measured = false) => new(
        clock.Speed.ToString(),
        SimulationClock.Label(clock.Speed),
        clock.AutoSlowOnEvent,
        clock.ThrottledByGovernor,
        Math.Round(clock.ColonyDaysPerRealMinute, 3),
        clock.Speed == GameSpeed.Paused
            ? null
            : Math.Round(clock.CallsPerColonyDay(sustainedCallsPerMinute), 2),
        sustainedCallsPerMinute,
        measured);
}

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

/// <summary>
/// One row of the Trace tab. The prompt and response are not in here: they are the heavy part, the
/// detail view polls every second, and nobody reads more than one at a time. A row says whether the
/// text is still held, and <see cref="CallTextDto"/> carries it when the row is opened.
/// </summary>
public sealed record TraceRowDto(
    string Id,
    string CallType,
    double Day,
    string Model,
    string Lane,
    int PromptTokens,
    int CompletionTokens,
    double Seconds,
    string Produced,
    bool WasFallback,
    IReadOnlyList<string> ProducedIds,
    bool HasText);

/// <summary>What one call sent and got back, for the Trace tab's opened row.</summary>
public sealed record CallTextDto(
    string Id,
    string CallType,
    double Day,
    string Model,
    string? System,
    string? Prompt,
    string? Response);

/// <summary>
/// A colonist's calls totalled by type — the Trace tab's header. Covers the whole lifetime even
/// though the rows themselves are capped.
/// </summary>
public sealed record CallTypeSummaryDto(string CallType, int Calls, int Tokens, int Fallbacks, double Seconds);

public sealed record ColonistDetailDto(
    ColonistDto Colonist,
    string Personality,
    string Seed,
    IReadOnlyList<TraceRowDto> Trace,
    IReadOnlyList<ConsolidatedRowDto> Consolidated,
    IReadOnlyList<BeliefRowDto> Beliefs,
    IReadOnlyList<MemoryRowDto> RecentMemories,
    IReadOnlyList<RelationshipRowDto> Relationships,
    IReadOnlyList<ScoredOptionDto> Considering,
    IReadOnlyList<CallTypeSummaryDto> TraceSummary,
    int TraceTotal);

/// <summary>
/// One episodic row, with its place and the other people in it resolved to names. This is the far
/// end of a belief's chain, and the inspector links onward from it — to the room it happened in and
/// to the minds of whoever else was there.
/// </summary>
public sealed record MemoryRowDto(
    string Id,
    double Day,
    string Text,
    string PlaceId,
    string PlaceName,
    IReadOnlyList<PersonRefDto> Others,
    int Repeats,
    double Valence,
    double Intensity,
    double Salience);

public sealed record PersonRefDto(string Id, string Name);

/// <summary>A written memory standing in for a batch of episodic rows, with the rows it came from.</summary>
public sealed record ConsolidatedRowDto(
    string Id,
    double Day,
    string Text,
    double Valence,
    double Salience,
    int RecallCount,
    IReadOnlyList<MemoryRowDto> Sources,
    string? FormedByCallId);

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
    IReadOnlyList<MemoryRowDto> Sources,
    string? FormedByCallId,
    int Reinforcements,
    string? LastReinforcedByCallId);

public sealed record RelationshipRowDto(string TowardId, string TowardName, double Affinity, double Trust, double Respect, int SharedMemories, string Stage, string? Narrative);

/// <summary>
/// The runners-up in the utility scoring. This is the honest version of the inspector's "chosen
/// over forage (0.31)" line, and it costs nothing because the scorer already computes them.
/// </summary>
public sealed record ScoredOptionDto(string Action, string PlaceName, string? PartnerId, string? PartnerName, double Score, bool Chosen);
