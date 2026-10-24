namespace Emberloom.Core.Memory;

/// <summary>
/// One colonist's memory, across all four tiers. In M1 only the episodic tier is written; the
/// rest are here so the pruning rules and the reflection trigger are already correct when the
/// mind arrives in M3.
/// </summary>
public sealed class MemoryStore
{
    private readonly List<EpisodicMemory> _episodic = [];
    private readonly List<ConsolidatedMemory> _consolidated = [];
    private readonly List<Belief> _beliefs = [];

    /// <summary>
    /// Above this much unconsolidated salience, the colonist has enough unprocessed experience to
    /// be worth a reflection. The governor decides whether they actually get one.
    /// </summary>
    public const double ReflectionThreshold = 1.6;

    private const int EpisodicSoftCap = 400;

    public IReadOnlyList<EpisodicMemory> Episodic => _episodic;

    public IReadOnlyList<ConsolidatedMemory> Consolidated => _consolidated;

    public IReadOnlyList<Belief> Beliefs => _beliefs;

    /// <summary>Salience accumulated since the last reflection. What the governor's queue sorts on.</summary>
    public double UnconsolidatedSalience(double currentDay) =>
        _episodic.Where(m => !m.Consolidated).Sum(m => m.Salience(currentDay));

    public bool WantsReflection(double currentDay) =>
        UnconsolidatedSalience(currentDay) >= ReflectionThreshold;

    /// <summary>
    /// How long the same act, in the same place, folds into one memory rather than becoming a
    /// new row. Doing a thing twice in a morning is one memory of doing it, not two.
    /// </summary>
    private const double CoalesceWindowDays = 2.0;

    private const double CoalescedIntensityCeiling = 0.95;

    /// <summary>
    /// Records an episodic row, folding it into a recent identical one where there is one.
    /// <para>
    /// Without this the body buries the mind. Measured at M3: eight colonists accumulated ~370
    /// rows each in 28 colony-days, overwhelmingly the same handful of acts repeated — and since
    /// reflection consolidates a batch at a time, the backlog could never be worked off. Every
    /// colonist wanted a reflection permanently, which made the signal useless for deciding who
    /// most needed one.
    /// </para>
    /// <para>
    /// Folding is also the truer model. Repetition does not leave a hundred separate memories; it
    /// leaves one memory that got stronger, which is exactly what raising intensity encodes. The
    /// hundredth shift at the same shelter is not news. The first one was.
    /// </para>
    /// </summary>
    public void Record(EpisodicMemory memory)
    {
        var recent = _episodic.FindLast(m =>
            !m.Consolidated
            && memory.Day - m.Day <= CoalesceWindowDays
            && string.Equals(m.Verb, memory.Verb, StringComparison.Ordinal)
            && string.Equals(m.Object, memory.Object, StringComparison.Ordinal)
            && string.Equals(m.PlaceId, memory.PlaceId, StringComparison.Ordinal));

        if (recent is not null)
        {
            // The memory is of the whole run of it, so it sits at the latest occurrence and
            // carries a little more weight for having happened again.
            recent.Day = memory.Day;
            recent.Tick = memory.Tick;
            recent.Repeats++;
            recent.Intensity = Math.Min(
                CoalescedIntensityCeiling,
                recent.Intensity + memory.Intensity * 0.12);
            return;
        }

        _episodic.Add(memory);
    }

    /// <summary>
    /// Puts a saved mind back exactly as it was. Record() folds repeats and Add() appends, both of
    /// which are right while a colony runs and wrong when restoring one — a reload must not turn
    /// two memories of the same thing into one with Repeats bumped.
    /// </summary>
    public void Restore(
        IEnumerable<EpisodicMemory> episodic,
        IEnumerable<ConsolidatedMemory> consolidated,
        IEnumerable<Belief> beliefs)
    {
        _episodic.Clear();
        _consolidated.Clear();
        _beliefs.Clear();
        _episodic.AddRange(episodic);
        _consolidated.AddRange(consolidated);
        _beliefs.AddRange(beliefs);
    }

    public void Add(ConsolidatedMemory memory) => _consolidated.Add(memory);

    public void Add(Belief belief) => _beliefs.Add(belief);

    /// <summary>
    /// Swaps a belief for an updated copy of itself, in place. Its id and position are unchanged,
    /// so every chain already pointing at it still resolves.
    /// </summary>
    public void Replace(Belief existing, Belief updated)
    {
        var index = _beliefs.FindIndex(b => b.Id == existing.Id);
        if (index >= 0)
        {
            _beliefs[index] = updated;
        }
    }

    /// <summary>
    /// The batch a reflection reads: the most salient rows nobody has made sense of yet.
    /// <para>
    /// Twelve rather than eight because the mind has to keep pace with the body. Prompt tokens are
    /// cheap to evaluate and the output cap is unchanged, so a wider batch costs almost nothing and
    /// consolidates half again as much experience per call.
    /// </para>
    /// </summary>
    public IReadOnlyList<EpisodicMemory> ReflectionBatch(double currentDay, int max = 12) =>
        _episodic
            .Where(m => !m.Consolidated)
            .OrderByDescending(m => m.Salience(currentDay))
            .Take(max)
            .ToList();

    public IReadOnlyList<EpisodicMemory> MostSalient(double currentDay, int count) =>
        _episodic.OrderByDescending(m => m.Salience(currentDay)).Take(count).ToList();

    public EpisodicMemory? FindEpisodic(string id) => _episodic.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Puts what was said onto the conversation it was said in: the latest "talked with" memory
    /// about <paramref name="otherId"/> from the last day. False when there is none yet — the mind
    /// can answer before the body has finished the conversation that prompted it.
    /// </summary>
    public bool AttachWords(string otherId, string words, double currentDay)
    {
        var memory = _episodic.FindLast(m =>
            m.Verb == "talked with" && m.Actors.Contains(otherId) && currentDay - m.Day <= 1.0);

        if (memory is null)
        {
            return false;
        }

        memory.Words = words;
        return true;
    }

    /// <summary>
    /// Drops the least salient rows once the store outgrows its soft cap. Consolidated rows are
    /// dropped first — their meaning is already recorded a tier up, so the detail is expendable.
    /// </summary>
    public int Prune(double currentDay)
    {
        if (_episodic.Count <= EpisodicSoftCap)
        {
            return 0;
        }

        var target = _episodic.Count - EpisodicSoftCap;

        var doomed = _episodic
            .OrderBy(m => m.Consolidated ? 0 : 1)
            .ThenBy(m => m.Salience(currentDay))
            .Take(target)
            .ToHashSet();

        _episodic.RemoveAll(doomed.Contains);
        return doomed.Count;
    }
}
