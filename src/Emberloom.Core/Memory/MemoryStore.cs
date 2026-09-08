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

    public void Record(EpisodicMemory memory) => _episodic.Add(memory);

    public void Add(ConsolidatedMemory memory) => _consolidated.Add(memory);

    public void Add(Belief belief) => _beliefs.Add(belief);

    /// <summary>The batch a reflection reads: the most salient rows nobody has made sense of yet.</summary>
    public IReadOnlyList<EpisodicMemory> ReflectionBatch(double currentDay, int max = 8) =>
        _episodic
            .Where(m => !m.Consolidated)
            .OrderByDescending(m => m.Salience(currentDay))
            .Take(max)
            .ToList();

    public IReadOnlyList<EpisodicMemory> MostSalient(double currentDay, int count) =>
        _episodic.OrderByDescending(m => m.Salience(currentDay)).Take(count).ToList();

    public EpisodicMemory? FindEpisodic(string id) => _episodic.FirstOrDefault(m => m.Id == id);

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
