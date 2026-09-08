namespace Emberloom.Core.Relationships;

/// <summary>
/// Morpheus's five-stage ladder, kept as a derived band over the numeric ledger rather than as
/// state of its own. Nothing writes a stage; it is always read off affinity and trust.
/// </summary>
public enum RelationshipStage
{
    Stranger,
    Acquaintance,
    Familiar,
    Trusted,
    Bonded
}

/// <summary>
/// One direction of a relationship: how <c>From</c> reads <c>To</c>. Asymmetric on purpose —
/// Bram trusting Sela says nothing about whether Sela trusts Bram.
/// </summary>
public sealed class Relationship
{
    public required string FromId { get; init; }

    public required string ToId { get; init; }

    /// <summary>-1 to +1. Do I like you.</summary>
    public double Affinity { get; private set; }

    /// <summary>-1 to +1. Will you do what you say.</summary>
    public double Trust { get; private set; }

    /// <summary>-1 to +1. Do I defer to you.</summary>
    public double Respect { get; private set; }

    /// <summary>How much of this pair's history is shared. Deepens the stage without moving the numbers.</summary>
    public int SharedMemories { get; private set; }

    /// <summary>
    /// The prose reading Layer 2 writes at reflection. Null until a mind has had something to say
    /// about them — the numbers are what the simulation runs on, this is what a player reads.
    /// </summary>
    public string? Narrative { get; set; }

    public void Adjust(double affinity = 0, double trust = 0, double respect = 0)
    {
        Affinity = Math.Clamp(Affinity + affinity, -1, 1);
        Trust = Math.Clamp(Trust + trust, -1, 1);
        Respect = Math.Clamp(Respect + respect, -1, 1);
    }

    public void NoteSharedMemory() => SharedMemories++;

    public RelationshipStage Stage
    {
        get
        {
            var bond = (Affinity + Trust) / 2;
            var familiarity = Math.Min(SharedMemories / 25.0, 1.0);

            return (bond, familiarity) switch
            {
                ( >= 0.62, >= 0.5) => RelationshipStage.Bonded,
                ( >= 0.40, >= 0.25) => RelationshipStage.Trusted,
                ( >= 0.15, >= 0.10) => RelationshipStage.Familiar,
                ( >= -0.20, _) when familiarity > 0 => RelationshipStage.Acquaintance,
                _ => RelationshipStage.Stranger
            };
        }
    }

    /// <summary>
    /// How much this relationship pulls a colonist toward an action involving the other person.
    /// The <c>relationship_modifier</c> term in the scoring formula.
    /// </summary>
    public double ActionModifier => 1.0 + Affinity * 0.35 + Respect * 0.15;
}

/// <summary>
/// Every ordered pair the colony has an opinion about. Adjusted in code, instantly, with no model
/// involvement — one of the things that keeps the body affordable at thousands of agents.
/// </summary>
public sealed class RelationshipLedger
{
    private readonly Dictionary<(string From, string To), Relationship> _entries = [];

    public Relationship Get(string fromId, string toId)
    {
        var key = (fromId, toId);
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new Relationship { FromId = fromId, ToId = toId };
        _entries[key] = created;
        return created;
    }

    public Relationship? Peek(string fromId, string toId) => _entries.GetValueOrDefault((fromId, toId));

    public IEnumerable<Relationship> Outgoing(string fromId) =>
        _entries.Values.Where(r => r.FromId == fromId);

    /// <summary>Adjusts both directions at once, which is what most shared events actually do.</summary>
    public void Mutual(string a, string b, double affinity = 0, double trust = 0, double respect = 0)
    {
        Get(a, b).Adjust(affinity, trust, respect);
        Get(b, a).Adjust(affinity, trust, respect);
    }

    public void NoteSharedMemory(string a, string b)
    {
        Get(a, b).NoteSharedMemory();
        Get(b, a).NoteSharedMemory();
    }

    /// <summary>
    /// Who this colonist would seek out. Used by the body to pick a conversation partner without
    /// asking anyone anything.
    /// </summary>
    public IEnumerable<Relationship> Closest(string fromId, int count) =>
        Outgoing(fromId).OrderByDescending(r => r.Affinity + r.Trust).Take(count);

    public void Forget(string colonistId)
    {
        var doomed = _entries.Keys.Where(k => k.From == colonistId || k.To == colonistId).ToList();
        foreach (var key in doomed)
        {
            _entries.Remove(key);
        }
    }
}
