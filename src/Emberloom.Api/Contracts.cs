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

/// <summary>One line of the feed. <c>Quote</c> is set when the line is someone speaking; the views show it over their head.</summary>
public sealed record EventDto(
    long Tick,
    double Day,
    string Layer,
    string Text,
    string? ColonistId,
    string? PlaceId,
    bool Notable,
    string? Quote)
{
    public static EventDto From(ColonyEvent e) =>
        new(e.Tick, Math.Round(e.Day, 3), e.Layer.ToString().ToLowerInvariant(), e.Text, e.ColonistId, e.PlaceId, e.IsNotable, e.Quote);
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
    double UpkeepPerDay,
    int ArtifactsRecovered,
    int Population,
    int ActionsCompleted,
    ClockDto Clock,
    GovernorDto? Governor,
    IReadOnlyList<ColonistDto> Colonists,
    IReadOnlyList<PlaceStateDto> Places,
    ForkDto? Fork);

/// <summary>
/// The fork the colony is deciding, or the one it has just decided — shown as a banner over the
/// main view, with each council member's say as it comes in.
/// </summary>
public sealed record ForkDto(
    string Id,
    string Kind,
    string Question,
    string Proposal,
    double OpenedOnDay,
    bool IsOpen,
    bool? Carried,
    IReadOnlyList<CouncilSeatDto> Council)
{
    /// <summary>How long a decided fork stays up, in colony days, so its outcome can be read.</summary>
    private const double ShowOutcomeDays = 0.5;

    public static ForkDto? From(Colony colony)
    {
        var fork = colony.OpenFork
            ?? colony.ForkHistory.LastOrDefault(f => colony.Day - (f.ResolvedOnDay ?? 0) < ShowOutcomeDays);

        if (fork is null)
        {
            return null;
        }

        return new ForkDto(
            fork.Id,
            fork.Kind.ToString(),
            fork.Question,
            fork.Proposal,
            Math.Round(fork.OpenedOnDay, 2),
            fork.IsOpen,
            fork.Carried,
            fork.Participants.Select(id =>
            {
                fork.Votes.TryGetValue(id, out var vote);
                return new CouncilSeatDto(
                    id,
                    colony.Find(id)?.DisplayName ?? id,
                    vote?.Stance.ToString().ToLowerInvariant(),
                    vote is null ? null : Math.Round(vote.Conviction, 2),
                    vote?.Line,
                    vote?.WasFallback ?? false);
            }).ToList());
    }
}

/// <summary>One seat on a council: who, and — once they have spoken — where they stand and what they said.</summary>
public sealed record CouncilSeatDto(
    string Id,
    string Name,
    string? Stance,
    double? Conviction,
    string? Line,
    bool WasFallback);

/// <summary>
/// The parts of a place that change while the colony runs. The map itself is sent once, at connect
/// (<see cref="WorldDto"/>); these ride along with every state push. Before M4.5 they did not, so a
/// stairwell being cleared or a ruin running out of salvage never showed until the page reloaded.
/// </summary>
public sealed record PlaceStateDto(
    string Id,
    bool IsBlocked,
    bool IsExplored,
    double ClearingProgress,
    int SalvageRemaining,
    bool IsLit,
    bool IsWarmed)
{
    public static PlaceStateDto From(Place place, Colony colony) => new(
        place.Id,
        place.IsBlocked,
        place.IsExplored,
        Math.Round(place.ClearingProgress, 3),
        place.SalvageRemaining,
        colony.IsLit(place.Id),
        colony.IsWarmed(place.Id));
}

/// <summary>One thing of the makers' the player could show, and — once shown — who saw it.</summary>
public sealed record RevelationDto(string Id, string Title, string? Unlocks, string? ShownTo);

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
