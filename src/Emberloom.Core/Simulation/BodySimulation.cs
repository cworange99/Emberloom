using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Memory;
using Emberloom.Core.World;

namespace Emberloom.Core.Simulation;

/// <summary>
/// Layer 1. Runs every tick, for every colonist, with zero model calls. This is the half that
/// keeps running when Ollama is offline — the correctness test the whole architecture leans on.
/// </summary>
public sealed class BodySimulation
{
    private const double DayPerTick = 1.0 / Colony.TicksPerDay;

    /// <summary>Ticks between re-scoring. Deciding every tick is wasteful and makes colonists twitchy.</summary>
    private const int DecisionInterval = 6;

    private readonly Colony _colony;
    private readonly UtilityScorer _scorer;
    private readonly Random _random;
    private long _nextMemoryId;

    /// <summary>Actions carried through to completion. Distinct from actions merely started.</summary>
    public int ActionsCompleted { get; private set; }

    public BodySimulation(Colony colony, Random random)
    {
        _colony = colony;
        _random = random;
        _scorer = new UtilityScorer(random);
    }

    public UtilityScorer Scorer => _scorer;

    /// <summary>
    /// Raised when something happened that a mind might want to think about. In M1 nothing
    /// listens; from M2 the governor does.
    /// </summary>
    public event Action<Colonist, EpisodicMemory>? MemoryRecorded;

    public void Tick()
    {
        _colony.Tick++;

        AdvanceWorld();

        foreach (var colonist in _colony.Living.ToList())
        {
            AdvanceColonist(colonist);
        }

        if (_colony.Tick % Colony.TicksPerDay == 0)
        {
            EndOfDay();
        }
    }

    private void AdvanceWorld()
    {
        _colony.LoomCharge += _colony.LoomChargePerDay * DayPerTick;
    }

    private void AdvanceColonist(Colonist colonist)
    {
        colonist.Drives.Advance(DayPerTick);

        var here = _colony.Map[colonist.PlaceId];

        // Standing somewhere dark and unstable is its own pressure on Safety.
        if (here.Danger > 0.2 || (here.Light < 0.4 && _colony.IsNight))
        {
            colonist.Drives.Aggravate(Drive.Safety, here.Danger * 0.4 * DayPerTick);
        }

        if (colonist.Current is null || ShouldReconsider(colonist))
        {
            Decide(colonist);
        }

        if (colonist.Current is null)
        {
            return;
        }

        if (colonist.PlaceId != colonist.Current.Target.Id)
        {
            Travel(colonist);
            return;
        }

        Work(colonist);
    }

    private bool ShouldReconsider(Colonist colonist) =>
        colonist.Current!.IsComplete || _colony.Tick % DecisionInterval == 0;

    private void Decide(Colonist colonist)
    {
        var choice = _scorer.Choose(colonist, _colony);
        if (choice is null)
        {
            return;
        }

        var isSameAction = colonist.Current is { } current
                           && current.Definition.Kind == choice.Definition.Kind
                           && current.Target.Id == choice.Target.Id;

        if (isSameAction && !colonist.Current!.IsComplete)
        {
            return;
        }

        colonist.Current = new CurrentAction
        {
            Definition = choice.Definition,
            Target = choice.Target,
            PartnerId = choice.PartnerId,
            ChosenScore = choice.Score
        };

        colonist.DestinationPlaceId = choice.Target.Id;
    }

    private void Travel(Colonist colonist)
    {
        var current = colonist.Current!;
        var here = _colony.Map[colonist.PlaceId];
        var step = _colony.Map.NextStep(here, current.Target);

        if (step is null)
        {
            // Nowhere to go — the target became unreachable. Drop it and re-decide next tick.
            colonist.Current = null;
            colonist.DestinationPlaceId = null;
            return;
        }

        current.TravelTicks++;

        // Distance costs ticks rather than being instant; a step takes longer the further it is.
        var stepCost = Math.Max(1, (int)(here.DistanceTo(step) / 14.0));
        if (current.TravelTicks % stepCost != 0)
        {
            return;
        }

        colonist.PlaceId = step.Id;

        if (!step.IsExplored && !step.IsBlocked)
        {
            step.IsExplored = true;
            _colony.Log(EventLayer.Body, $"{colonist.DisplayName} reached {step.Name} for the first time.",
                colonist, step.Id, notable: true);
            Record(colonist, "first reached", step.Name, step.Id, valence: 0.55, intensity: 0.7);
        }
    }

    private void Work(Colonist colonist)
    {
        var current = colonist.Current!;
        var place = current.Target;

        current.TicksElapsed++;

        // Relief is a rate per colony-day of work, not a lump sum per action. Applying it per
        // unit of time is what makes DurationTicks mean anything: an hour of sleep is worth more
        // than an hour of chat because it is an hour, not because the action "finished".
        colonist.Drives.Relieve(current.Definition.Relief, DayPerTick * ActionCatalog.ReliefScale);

        ApplyWorkEffects(colonist, current, place, DayPerTick);

        if (!current.IsComplete)
        {
            return;
        }

        CompleteAction(colonist, current, place);
    }

    private void ApplyWorkEffects(Colonist colonist, CurrentAction current, Place place, double share)
    {
        switch (current.Definition.Kind)
        {
            case ActionKind.Clear:
                place.ClearingDone += 1;
                if (!place.IsBlocked && !place.IsExplored)
                {
                    place.IsExplored = true;
                    _colony.Log(EventLayer.Body,
                        $"{colonist.DisplayName} broke through into {place.Name}.",
                        colonist, place.Id, notable: true);
                    Record(colonist, "opened the way into", place.Name, place.Id, 0.85, 0.95);
                }

                break;

            case ActionKind.Talk or ActionKind.Teach when current.PartnerId is not null:
                if (_colony.Tick % 6 == 0)
                {
                    Converse(colonist, current);
                }

                break;
        }

        if (current.Definition.ExposesToDanger && _random.NextDouble() < place.Danger * share * 0.10)
        {
            Injure(colonist, place);
        }
    }

    private void Converse(Colonist colonist, CurrentAction current)
    {
        var partner = _colony.Find(current.PartnerId!);
        if (partner is null || !partner.IsAlive || partner.PlaceId != colonist.PlaceId)
        {
            colonist.Current = null;
            return;
        }

        // The ledger moves in code, instantly. Layer 2 writes a prose reading of the same
        // relationship later; these numbers are what the simulation runs on.
        _colony.Relationships.Mutual(colonist.Id, partner.Id, affinity: 0.02, trust: 0.01);
        _colony.Relationships.NoteSharedMemory(colonist.Id, partner.Id);
    }

    private void CompleteAction(Colonist colonist, CurrentAction current, Place place)
    {
        ActionsCompleted++;

        switch (current.Definition.Kind)
        {
            case ActionKind.Forage:
                Record(colonist, "foraged at", place.Name, place.Id, 0.25, 0.3);
                break;

            case ActionKind.Salvage when place.SalvageRemaining > 0:
                RecoverArtifact(colonist, place);
                break;

            case ActionKind.Build:
                _colony.Log(EventLayer.Body, $"{colonist.DisplayName} finished work at {place.Name}.", colonist, place.Id);
                Record(colonist, "finished building at", place.Name, place.Id, 0.5, 0.55);
                break;

            case ActionKind.Rest:
                Record(colonist, "slept at", place.Name, place.Id, 0.2, 0.15);
                break;

            case ActionKind.Talk when current.PartnerId is not null:
                RecordConversation(colonist, current.PartnerId, place);
                break;

            case ActionKind.Teach when current.PartnerId is not null:
                var student = _colony.Find(current.PartnerId);
                if (student is not null)
                {
                    _colony.Relationships.Get(student.Id, colonist.Id).Adjust(respect: 0.06, trust: 0.03);
                    Record(colonist, "taught", student.DisplayName, place.Id, 0.45, 0.5);
                    Record(student, "was taught by", colonist.DisplayName, place.Id, 0.35, 0.45);
                }

                break;

            case ActionKind.Tend:
                Record(colonist, "tended", "the Loom", place.Id, 0.3, 0.4);
                break;

            case ActionKind.Clear:
                Record(colonist, "worked at clearing", place.Name, place.Id, 0.15, 0.35);
                break;
        }

        colonist.Current = null;
        colonist.DestinationPlaceId = null;
    }

    private void RecoverArtifact(Colonist colonist, Place place)
    {
        place.SalvageRemaining--;
        _colony.ArtifactsRecovered++;

        // Salvage is archaeology: each artifact is findable exactly once, and it is evidence in
        // an argument nobody can settle. From M5 this is where Coinage fires.
        _colony.Log(EventLayer.Body,
            $"{colonist.DisplayName} recovered something of the makers' at {place.Name}.",
            colonist, place.Id, notable: true);

        Record(colonist, "recovered an artifact at", place.Name, place.Id, valence: 0.75, intensity: 0.9);
    }

    private void RecordConversation(Colonist colonist, string partnerId, Place place)
    {
        var partner = _colony.Find(partnerId);
        if (partner is null)
        {
            return;
        }

        // In M1 conversation has no content — the ledger moves and both parties remember that it
        // happened. Dialogue proper is M5.
        Record(colonist, "talked with", partner.DisplayName, place.Id, 0.3, 0.25);
        Record(partner, "talked with", colonist.DisplayName, place.Id, 0.3, 0.25);
    }

    private void Injure(Colonist colonist, Place place)
    {
        colonist.Drives.Aggravate(Drive.Safety, 0.35);
        colonist.Drives.Aggravate(Drive.Rest, 0.25);
        colonist.Current = null;

        _colony.Log(EventLayer.Body,
            $"{colonist.DisplayName} was hurt at {place.Name}.", colonist, place.Id, notable: true);

        Record(colonist, "was hurt at", place.Name, place.Id, valence: -0.7, intensity: 0.85);

        // Everyone present sees it, and a floor giving way is the kind of thing that changes how
        // someone reads a stairwell for the rest of their life.
        foreach (var witness in _colony.ColonistsAt(place.Id).Where(c => c.Id != colonist.Id))
        {
            witness.Drives.Aggravate(Drive.Safety, 0.20);
            Record(witness, "saw", $"{colonist.DisplayName} hurt at {place.Name}", place.Id, -0.55, 0.75);
            _colony.Relationships.Get(witness.Id, colonist.Id).Adjust(affinity: 0.01);
        }
    }

    private void EndOfDay()
    {
        foreach (var colonist in _colony.Living)
        {
            var pruned = colonist.Memory.Prune(_colony.Day);
            if (pruned > 0)
            {
                _colony.Log(EventLayer.World,
                    $"{colonist.DisplayName} let go of {pruned} small memories.", colonist);
            }
        }
    }

    /// <summary>
    /// Writes one raw episodic row. Cheap and constant — thousands accumulate, and Layer 2's job
    /// is not to generate them but to periodically read a batch and decide what they meant.
    /// </summary>
    private void Record(
        Colonist colonist,
        string verb,
        string? about,
        string placeId,
        double valence,
        double intensity)
    {
        var memory = new EpisodicMemory
        {
            Id = $"m-{_nextMemoryId++}",
            Tick = _colony.Tick,
            Day = _colony.Day,
            Actors = [colonist.Id],
            Verb = verb,
            Object = about,
            PlaceId = placeId,
            Valence = valence,
            Intensity = intensity
        };

        colonist.Memory.Record(memory);
        MemoryRecorded?.Invoke(colonist, memory);
    }
}
