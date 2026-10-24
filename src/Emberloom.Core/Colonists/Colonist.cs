using Emberloom.Core.Actions;
using Emberloom.Core.Drives;
using Emberloom.Core.Life;
using Emberloom.Core.Memory;

namespace Emberloom.Core.Colonists;

public enum LineageKind
{
    /// <summary>The Loom spent charge on this one. Deliberate, expensive, political.</summary>
    Printed,

    /// <summary>Two colonists past a bond threshold. Emergent, cheap, personal.</summary>
    Paired
}

/// <summary>
/// One mind and the body it runs. Everything on here except <see cref="Personality"/>,
/// <see cref="Name"/> and the consolidated tiers of <see cref="Memory"/> is written by Layer 1.
/// </summary>
public sealed class Colonist
{
    public required string Id { get; init; }

    /// <summary>What the Loom called them. Printed minds arrive with this and nothing else.</summary>
    public required string Designation { get; init; }

    /// <summary>
    /// Null until they have done something they consider worth being named for. The colony
    /// remembers who named themselves what, and after what.
    /// </summary>
    public string? Name { get; set; }

    public string DisplayName => Name ?? Designation;

    /// <summary>
    /// What they are known for, if they have done something worth it — "opening the way into the
    /// Ninth Floor". Set once, by the first such deed. Not everyone has one, and that is the point:
    /// it is the distinction a name used to be, now that a name is simply everyone's.
    /// </summary>
    public string? Epithet { get; set; }

    public required LineageKind Lineage { get; init; }

    public IReadOnlyList<string> ParentIds { get; init; } = [];

    public required double BornOnDay { get; init; }

    /// <summary>Prose written by Kindling. An archetype blurb stands in until it returns.</summary>
    public required string Personality { get; set; }

    /// <summary>The prose the Loom was given to print this mind from. Consumed once, at Kindling.</summary>
    public string Seed { get; set; } = string.Empty;

    public required PersonalityWeights Weights { get; set; }

    public DriveState Drives { get; } = new();

    public MemoryStore Memory { get; } = new();

    public required string PlaceId { get; set; }

    public CurrentAction? Current { get; set; }

    /// <summary>Where they are walking to, if the chosen action is somewhere else.</summary>
    public string? DestinationPlaceId { get; set; }

    public bool IsAlive { get; set; } = true;

    public double DiedOnDay { get; set; } = -1;

    /// <summary>How long this body lasts, in colony days. Set at print or birth; see LifeRules.</summary>
    public double LifespanDays { get; set; } = LifeRules.BaseLifespanDays;

    /// <summary>In the last stretch of their life, and slowing. Set once, so it is noticed once.</summary>
    public bool IsElder { get; set; }

    /// <summary>When they last had a successor. Pairing has a cooldown.</summary>
    public double LastPairedOnDay { get; set; } = double.NegativeInfinity;

    /// <summary>The most recent thing Layer 2 had them think. Null until the fast lane runs in M3.</summary>
    public string? LastThought { get; set; }

    /// <summary>
    /// Words the mind wrote for a conversation the body had not finished yet, keyed by the other
    /// person. Attached to the conversation's memory as soon as the body writes it.
    /// </summary>
    public Dictionary<string, string> PendingWords { get; } = [];

    private readonly List<ModelCallRecord> _trace = [];
    private int _callSequence;

    /// <summary>
    /// How many of the most recent calls keep their full prompt and response. The text is the heavy
    /// part of a record — a few KB against a few hundred bytes for everything else — and an overnight
    /// run makes thousands of calls, so older records keep their measurements and lose the text.
    /// </summary>
    public const int TraceTextRetention = 40;

    /// <summary>
    /// Every model call this mind has made, newest last. The inspector's Trace tab reads this,
    /// and it is the cheapest convincing thing in the game for anyone technical.
    /// <para>
    /// A snapshot rather than the live list: calls are recorded from the mind's thread while the
    /// API reads from its own, and handing out the list itself would let a read race a write.
    /// </para>
    /// </summary>
    public IReadOnlyList<ModelCallRecord> Trace
    {
        get { lock (_trace) { return _trace.ToList(); } }
    }

    public int ModelCalls
    {
        get { lock (_trace) { return _trace.Count; } }
    }

    public int TokensSpent
    {
        get { lock (_trace) { return _trace.Sum(t => t.TotalTokens); } }
    }

    public ModelCallRecord? FindCall(string callId)
    {
        lock (_trace) { return _trace.Find(t => t.Id == callId); }
    }

    /// <summary>
    /// Reserves an id for a call about to be recorded. Taken before a call's artifacts are written,
    /// so a belief can cite the call that formed it. A sequence rather than the trace length, so an
    /// id is never handed out twice however calls interleave.
    /// </summary>
    public string NextCallId()
    {
        lock (_trace) { return $"{Id}-call-{++_callSequence}"; }
    }

    /// <summary>
    /// True once a mind has written this colonist rather than an archetype standing in. Printed
    /// colonists exist and act immediately; Kindling upgrades them when it returns.
    /// </summary>
    public bool IsKindled { get; set; }

    /// <summary>
    /// True while a Kindling call for this colonist is queued or running. Not the same as
    /// <see cref="IsKindled"/> being false: a colonist whose Kindling fell back is done waiting,
    /// and a colony with no mind never waits at all. Forks hold off while anyone is waiting, so
    /// nobody argues a turning point before they are someone.
    /// </summary>
    public bool AwaitingKindling { get; set; }

    /// <summary>Names this colonist might take, written at Kindling. They take one by earning it.</summary>
    public IReadOnlyList<string> NameCandidates { get; set; } = [];

    public void RecordCall(ModelCallRecord record)
    {
        lock (_trace)
        {
            _trace.Add(string.IsNullOrEmpty(record.Id) ? record with { Id = NextCallId() } : record);

            // Each record added pushes exactly one record out of the retention window, so stripping
            // that one keeps every older record stripped too.
            var aged = _trace.Count - TraceTextRetention - 1;
            if (aged >= 0 && _trace[aged].HasText)
            {
                _trace[aged] = _trace[aged] with { System = null, Prompt = null, Response = null };
            }
        }
    }

    public double AgeInDays(double currentDay) => currentDay - BornOnDay;

    /// <summary>
    /// Mood is derived, never generated. A whole model call for one adjective is the wrong trade
    /// at any budget, so it comes off drive levels and the valence of what just happened.
    /// </summary>
    public string Mood(double currentDay)
    {
        var recentValence = Memory.Episodic
            .Where(m => currentDay - m.Day < 1.5)
            .Select(m => m.Valence * m.Intensity)
            .DefaultIfEmpty(0)
            .Average();

        var dominant = Drives.Dominant(Weights);
        var pressure = Drives.Urgency(dominant, Weights[dominant]);

        return (recentValence, pressure) switch
        {
            ( > 0.25, < 0.4) => "settled",
            ( > 0.25, _) => "driven",
            ( < -0.25, > 0.5) => "raw",
            ( < -0.25, _) => "sullen",
            (_, > 0.6) => "restless",
            (_, < 0.2) => "easy",
            _ => "guarded"
        };
    }
}
