using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Verbs;
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

    /// <summary>
    /// Two colonists have started talking. The body knows that it is happening and moves the
    /// ledger; what they actually say is the mind's to write, and the host asks for it here.
    /// </summary>
    public event Action<Colonist, Colonist, Place>? ConversationStarted;

    /// <summary>A fork has opened. The host asks each council member's mind where they stand.</summary>
    public event Action<Fork>? ForkOpened;

    /// <summary>A fork has been decided — with the newly printed mind, when that is what was decided.</summary>
    public event Action<Fork, Colonist?>? ForkResolved;

    /// <summary>
    /// A successor, and its parents. The body has already made the child and given it their
    /// crossed weights and their dreams; the host asks a mind to write who it is.
    /// </summary>
    public event Action<Colonist, Colonist, Colonist>? ColonistBorn;

    public event Action<Colonist>? ColonistDied;

    /// <summary>Something the colony might coin a word for. The host asks a mind for the word.</summary>
    public event Action<Occasion>? Coinable;

    // ---- culture (M7) -----------------------------------------------------------------------

    /// <summary>A name, the first time they do something they consider worth being named for.</summary>
    private void EarnAName(Colonist colonist, string deed)
    {
        var designation = colonist.DisplayName;
        if (CultureRules.TakeAName(_colony, colonist) is not { } name)
        {
            return;
        }

        _colony.Log(EventLayer.Body, $"{designation} took the name {name}, after {deed}.", colonist, notable: true);
        Record(colonist, "took the name", $"{name}, after {deed}", colonist.PlaceId, valence: 0.8, intensity: 0.95);
    }

    /// <summary>
    /// Once a day: whether anything has been done so often it has become a practice. One a day at
    /// most, and each practice once — the colony names a ritual, it does not rename it.
    /// </summary>
    private void NoticeRituals()
    {
        foreach (var colonist in _colony.Living)
        {
            foreach (var memory in colonist.Memory.Episodic)
            {
                if (memory.Repeats + 1 < CultureRules.RitualRepeats
                    || memory.Object is null
                    || memory.Verb.StartsWith("heard", StringComparison.Ordinal)
                    || memory.Verb == LifeRules.DreamVerb)
                {
                    continue;
                }

                if (_colony.CoinedOccasions.Add($"ritual:{memory.Verb} {memory.Object}"))
                {
                    Coinable?.Invoke(new Occasion(OccasionKind.Ritual, colonist.Id, $"{memory.Verb} {memory.Object}", memory.PlaceId));
                    return;
                }
            }
        }
    }

    public void Tick()
    {
        _colony.Tick++;

        AdvanceWorld();
        AdvanceForks();

        foreach (var colonist in _colony.Living.ToList())
        {
            AdvanceColonist(colonist);
        }

        AdvanceLives();

        if (_colony.Tick % Colony.TicksPerDay == 0)
        {
            EndOfDay();
        }
    }

    // ---- lives (M7) -------------------------------------------------------------------------

    /// <summary>Ageing, every tick: the old slow, and a body whose time has come fails.</summary>
    private void AdvanceLives()
    {
        foreach (var colonist in _colony.Living.ToList())
        {
            var age = colonist.AgeInDays(_colony.Day);

            if (age >= colonist.LifespanDays)
            {
                Die(colonist);
                continue;
            }

            if (!colonist.IsElder && age >= colonist.LifespanDays * LifeRules.ElderFraction)
            {
                colonist.IsElder = true;
                _colony.Log(EventLayer.Body, $"{colonist.DisplayName} has begun to slow.", colonist, notable: true);
                Record(colonist, "felt their body begin to slow", null, colonist.PlaceId, valence: -0.4, intensity: 0.8);
            }

            if (colonist.IsElder)
            {
                colonist.Drives.Aggravate(Drive.Rest, LifeRules.ElderWearPerDay * DayPerTick);
            }
        }
    }

    /// <summary>
    /// A body fails. The mind is left in its core, for the colony to decide about; whoever was
    /// close has lost someone, and everyone else has heard.
    /// </summary>
    private void Die(Colonist colonist)
    {
        colonist.IsAlive = false;
        colonist.DiedOnDay = _colony.Day;
        colonist.Current = null;
        colonist.DestinationPlaceId = null;

        var place = _colony.Map[colonist.PlaceId];
        _colony.Log(EventLayer.Body,
            $"{colonist.DisplayName} died at {place.Name}, {colonist.AgeInDays(_colony.Day):F0} days old.",
            colonist, place.Id, notable: true);

        _colony.Cores.Add(new MindCore { ColonistId = colonist.Id, DiedOnDay = _colony.Day });

        foreach (var other in _colony.Living.ToList())
        {
            if (LifeRules.IsClose(_colony, other.Id, colonist.Id))
            {
                other.Drives.Aggravate(Drive.Belonging, 0.35);
                Record(other, "lost", colonist.DisplayName, place.Id, valence: -0.8, intensity: 0.95, with: colonist.Id);
            }
            else
            {
                Record(other, "heard of the death of", colonist.DisplayName, other.PlaceId, valence: -0.3, intensity: 0.5, with: colonist.Id);
            }
        }

        ColonistDied?.Invoke(colonist);
    }

    /// <summary>
    /// Once a day: whether any bonded pair has a successor. One birth a day at most — a colony that
    /// doubles overnight has outrun anyone's ability to know who anyone is.
    /// </summary>
    private void ConsiderPairings()
    {
        var living = _colony.Living.ToList();
        if (living.Count >= LifeRules.PopulationCap)
        {
            return;
        }

        for (var i = 0; i < living.Count; i++)
        {
            for (var j = i + 1; j < living.Count; j++)
            {
                if (LifeRules.CanPair(_colony, living[i], living[j]) && _random.NextDouble() < LifeRules.PairChancePerDay)
                {
                    Bear(living[i], living[j]);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A successor: the parents' weights crossed with mutation, and each parent's strongest
    /// memories arriving in the child as dreams it half-believes and cannot source. This is how a
    /// grudge outlives the person who formed it.
    /// </summary>
    /// <summary>
    /// A successor for two colonists, if they can pair — without the daily chance, which is
    /// ConsiderPairings'. With lives in months that chance is small, and a test that waited on the
    /// dice would wait hundreds of colony days.
    /// </summary>
    public Colonist? TryPair(Colonist left, Colonist right) =>
        LifeRules.CanPair(_colony, left, right) ? Bear(left, right) : null;

    private Colonist Bear(Colonist left, Colonist right)
    {
        var child = ColonyFactory.Pair(_colony, left, right, _random);
        left.LastPairedOnDay = right.LastPairedOnDay = _colony.Day;

        foreach (var parent in new[] { left, right })
        {
            var strongest = parent.Memory
                .MostSalient(_colony.Day, 12)
                .Where(m => m.Verb != LifeRules.DreamVerb)
                .Take(LifeRules.DreamsPerParent)
                .ToList();

            foreach (var memory in strongest)
            {
                Record(child, LifeRules.DreamVerb, memory.Describe(), memory.PlaceId,
                    memory.Valence, Math.Min(0.8, memory.Intensity));
            }
        }

        Record(left, "had a child with", right.DisplayName, left.PlaceId, valence: 0.8, intensity: 0.95, with: right.Id);
        Record(right, "had a child with", left.DisplayName, right.PlaceId, valence: 0.8, intensity: 0.95, with: left.Id);

        ColonistBorn?.Invoke(child, left, right);
        return child;
    }

    private void AdvanceWorld()
    {
        _colony.LoomCharge += (_colony.LoomChargePerDay - _colony.UpkeepPerDay) * DayPerTick;

        // What the player keeps running is paid for as it runs. When the Loom runs dry, the
        // newest provision goes out first, one a tick until the books balance — and each going
        // out is noticed, just as if it had been cut.
        if (_colony.LoomCharge < 0 && _colony.Provisions.Count > 0)
        {
            PlayerVerbs.Lapse(_colony, this, _colony.Provisions[^1]);
        }

        _colony.LoomCharge = Math.Max(0, _colony.LoomCharge);
    }

    private void AdvanceColonist(Colonist colonist)
    {
        colonist.Drives.Advance(DayPerTick);

        var here = _colony.Map[colonist.PlaceId];

        // Standing somewhere dark and unstable is its own pressure on Safety.
        if (here.Danger > 0.2 || (_colony.LightAt(here) < 0.4 && _colony.IsNight))
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
            EarnAName(colonist, $"being the first to reach {step.Name}");
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
        var reliefScale = DayPerTick * ActionCatalog.ReliefScale;

        // A shelter kept warm is a better night's sleep.
        if (current.Definition.Kind == ActionKind.Rest && _colony.IsWarmed(place.Id))
        {
            reliefScale *= ProvisionRules.WarmRestBonus;
        }

        colonist.Drives.Relieve(current.Definition.Relief, reliefScale);

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
                    EarnAName(colonist, $"opening the way into {place.Name}");
                    ForkRules.HoldTheWayUp(_colony, place);
                }

                break;

            case ActionKind.Talk or ActionKind.Teach when current.PartnerId is not null:
                if (current.Definition.Kind == ActionKind.Talk
                    && current.TicksElapsed == 1
                    && _colony.Find(current.PartnerId) is { IsAlive: true } partner
                    && partner.PlaceId == colonist.PlaceId)
                {
                    ConversationStarted?.Invoke(colonist, partner, place);
                }

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
                // Deliberately not logged. Routine work still becomes a memory — the mind will
                // want it at reflection — but a feed that reports every finished shift is a work
                // log, and the feed has to read like something worth watching.
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
                    Record(colonist, "taught", student.DisplayName, place.Id, 0.45, 0.5, with: student.Id);
                    Record(student, "was taught by", colonist.DisplayName, place.Id, 0.35, 0.45, with: colonist.Id);
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
        EarnAName(colonist, $"finding something of the makers' at {place.Name}");

        // Nobody knows what it is, so somebody has to call it something.
        Coinable?.Invoke(new Occasion(OccasionKind.Artifact, colonist.Id, place.Name, place.Id));
    }

    private void RecordConversation(Colonist colonist, string partnerId, Place place)
    {
        var partner = _colony.Find(partnerId);
        if (partner is null)
        {
            return;
        }

        // The ledger moves and both remember that it happened. What was said comes from the mind
        // (Dialogue) and is attached to these memories — now, or whenever it arrives.
        Record(colonist, "talked with", partner.DisplayName, place.Id, 0.3, 0.25, with: partner.Id);
        Record(partner, "talked with", colonist.DisplayName, place.Id, 0.3, 0.25, with: colonist.Id);

        // Words that arrived before the conversation was remembered: the mind answered faster than
        // the body finished talking. They belong on the memories just written.
        HearPendingWords(colonist, partner);
        HearPendingWords(partner, colonist);
    }

    private void HearPendingWords(Colonist colonist, Colonist other)
    {
        if (colonist.PendingWords.Remove(other.Id, out var words))
        {
            colonist.Memory.AttachWords(other.Id, words, _colony.Day);
        }
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

    /// <summary>
    /// Opens a fork when the colony faces one, and decides it once the council has spoken — or,
    /// with no minds working on it, once it has waited long enough, in which case anyone who never
    /// answered is counted by where they stand. Checked every tick because it is cheap; a new fork
    /// is looked for once a colony-hour.
    /// </summary>
    private void AdvanceForks()
    {
        if (_colony.OpenFork is { } open)
        {
            var timedOut = open.AwaitingMinds == 0 && _colony.Day - open.OpenedOnDay >= ForkRules.TimeoutDays;
            if (open.AllVoted || timedOut)
            {
                ResolveFork(open);
            }

            return;
        }

        if (_colony.Tick % 10 != 0)
        {
            return;
        }

        var fork = ForkRules.Detect(_colony);
        if (fork is null)
        {
            return;
        }

        _colony.OpenFork = fork;
        _colony.Log(EventLayer.World, $"The colony is deciding: {fork.Question}", placeId: fork.PlaceId, notable: true);
        ForkOpened?.Invoke(fork);
    }

    private void ResolveFork(Fork fork)
    {
        // Anyone the deadline caught without a say is counted by where they stand, and marked as
        // such, so the trace and the banner both show it was not their mind speaking.
        foreach (var id in fork.Participants.Where(id => !fork.Votes.ContainsKey(id)).ToList())
        {
            if (_colony.Find(id) is { } missing)
            {
                fork.Cast(id, ForkRules.FallbackVote(fork.Kind, missing));
            }
        }

        var carried = fork.HasMajority;

        // Which side got its way. A core has three fates, not two: a majority for reads it, a
        // majority against destroys it, and neither keeps it, unread. For any other fork, not
        // carrying is the status quo winning.
        Stance? prevailed = carried ? Stance.For
            : fork.Kind != ForkKind.Core || fork.HasMajorityAgainst ? Stance.Against
            : null;

        var outcome = fork.Kind == ForkKind.Core
            ? prevailed switch { Stance.For => "read", Stance.Against => "destroyed", _ => "kept, unread" }
            : carried ? "yes" : "no";

        fork.Decide(carried, prevailed, outcome, _colony.Day);
        _colony.OpenFork = null;
        _colony.ForkHistory.Add(fork);

        Colonist? printed = null;

        switch (fork.Kind)
        {
            case ForkKind.Print when carried:
                printed = ColonyFactory.Print(_colony, _random);
                break;

            case ForkKind.Print:
                _colony.PutOff(ForkKind.Print, _colony.Day + ForkRules.PrintAskAgainDays);
                break;

            case ForkKind.Ascend when carried && fork.SubjectPlaceId is { } above:
                _colony.ForbiddenPlaces.Remove(above);
                break;

            case ForkKind.Ascend:
                _colony.PutOff(ForkKind.Ascend, _colony.Day + ForkRules.AscendAskAgainDays);
                break;

            case ForkKind.Core when fork.SubjectColonistId is { } deadId:
                SettleCore(fork, deadId, prevailed);
                break;
        }

        // Abstentions and the bar are spelled out: "2 for, 0 against" read as a bug when it came out
        // "no", because the three who abstained — and the three it needed — were not on the line.
        var need = fork.Kind == ForkKind.Core
            ? $"a fate needed {fork.Needed} of {fork.Participants.Count}"
            : $"it needed {fork.Needed} of {fork.Participants.Count} for";
        _colony.Log(EventLayer.World,
            $"Decided: {outcome}, to “{fork.Question}” — {fork.Count(Stance.For)} for, " +
            $"{fork.Count(Stance.Against)} against, {fork.Count(Stance.Abstain)} abstained; {need}.",
            placeId: fork.PlaceId, notable: true);

        RememberTheFork(fork);
        ForkResolved?.Invoke(fork, printed);
    }

    /// <summary>
    /// What the colony decided about a core. Read, and what the dead believed reaches everyone —
    /// as something heard, not yet believed, so it has to survive their own reflection to stick.
    /// Destroyed, and those who were close watched it go. Kept, and it waits, unread.
    /// </summary>
    private void SettleCore(Fork fork, string deadId, Stance? prevailed)
    {
        var core = _colony.Cores.FirstOrDefault(c => c.ColonistId == deadId && c.Fate == CoreFate.Waiting);
        var dead = _colony.Find(deadId);
        if (core is null || dead is null)
        {
            return;
        }

        core.Fate = prevailed switch
        {
            Stance.For => CoreFate.Read,
            Stance.Against => CoreFate.Destroyed,
            _ => CoreFate.Archived
        };
        core.DecidedOnDay = _colony.Day;

        switch (core.Fate)
        {
            case CoreFate.Read:
                var held = LifeRules.WhatACoreHolds(dead, _colony.Day);
                core.Heard = held;
                foreach (var listener in _colony.Living.ToList())
                {
                    foreach (var line in held)
                    {
                        Record(listener, $"heard {dead.DisplayName}'s core read aloud:", $"“{line}”",
                            fork.PlaceId, valence: 0.2, intensity: 0.8, with: dead.Id);
                    }
                }

                _colony.Log(EventLayer.World, $"{dead.DisplayName}'s core was read to the colony.",
                    placeId: fork.PlaceId, notable: true);
                break;

            case CoreFate.Destroyed:
                foreach (var mourner in _colony.Living.Where(c => LifeRules.IsClose(_colony, c.Id, dead.Id)).ToList())
                {
                    mourner.Drives.Aggravate(Drive.Belonging, 0.2);
                    Record(mourner, "watched the colony destroy the core of", dead.DisplayName,
                        fork.PlaceId, valence: -0.6, intensity: 0.85, with: dead.Id);
                }

                _colony.Log(EventLayer.World, $"{dead.DisplayName}'s core was destroyed. Nobody will hear it now.",
                    placeId: fork.PlaceId, notable: true);
                break;

            default:
                _colony.Log(EventLayer.World, $"{dead.DisplayName}'s core was kept, unread.",
                    placeId: fork.PlaceId, notable: true);
                break;
        }
    }

    /// <summary>
    /// What a fork does to the people who argued it. Winning a vote is Standing and being overruled
    /// is Belonging — both straight from the design's drive table — and everyone who took a side
    /// remembers it, which is how a vote becomes something a mind later reflects on.
    /// </summary>
    private void RememberTheFork(Fork fork)
    {
        var sides = fork.Votes
            .Where(v => v.Value.Stance != Stance.Abstain)
            .Select(v => (Colonist: _colony.Find(v.Key), v.Value.Stance))
            .Where(x => x.Colonist is { IsAlive: true })
            .Select(x => (Colonist: x.Colonist!, x.Stance, Won: x.Stance == fork.Prevailed))
            .ToList();

        foreach (var (colonist, stance, won) in sides)
        {
            var side = stance == Stance.For ? "for" : "against";

            if (won)
            {
                colonist.Drives.Relieve(new DriveVector(new Dictionary<Drive, double> { [Drive.Standing] = 0.5 }));
                Record(colonist, $"argued {side} it, and the colony agreed:", fork.Question, fork.PlaceId, 0.55, 0.8);
            }
            else
            {
                colonist.Drives.Aggravate(Drive.Belonging, 0.25);
                Record(colonist, $"argued {side} it, and was overruled:", fork.Question, fork.PlaceId, -0.45, 0.8);
            }
        }

        // Standing on the same side draws people together; on opposite sides, a little apart.
        for (var i = 0; i < sides.Count; i++)
        {
            for (var j = i + 1; j < sides.Count; j++)
            {
                var sameSide = sides[i].Stance == sides[j].Stance;
                _colony.Relationships.Mutual(sides[i].Colonist.Id, sides[j].Colonist.Id, affinity: sameSide ? 0.04 : -0.02);
            }
        }
    }

    private void EndOfDay()
    {
        foreach (var colonist in _colony.Living)
        {
            colonist.Memory.Prune(_colony.Day);
        }

        ConsiderPairings();
        KeepUndecidedCores();
        NoticeRituals();
        LogDailySummary();
    }

    /// <summary>
    /// A core the colony never got round to is kept, unread. Not the one it is deciding right now.
    /// </summary>
    private void KeepUndecidedCores()
    {
        var overdue = _colony.Cores
            .Where(c => c.Fate == CoreFate.Waiting
                        && _colony.Day - c.DiedOnDay > LifeRules.UndecidedCoreDays
                        && _colony.OpenFork?.SubjectColonistId != c.ColonistId)
            .ToList();

        foreach (var core in overdue)
        {
            core.Fate = CoreFate.Archived;
            core.DecidedOnDay = _colony.Day;

            var name = _colony.Find(core.ColonistId)?.DisplayName ?? core.ColonistId;
            _colony.Log(EventLayer.World, $"Nobody decided what to do with {name}'s core. It was kept, unread.", notable: true);
        }
    }

    /// <summary>
    /// One line a day, naming whoever is doing the most distinctive thing. At speed this is what
    /// carries the feed — a reader skimming 60x should still be able to follow who the colony is
    /// becoming, rather than watching completions scroll past.
    /// </summary>
    private void LogDailySummary()
    {
        var day = (int)_colony.Day;
        if (day <= 0)
        {
            return;
        }

        var restless = _colony.Living
            .OrderByDescending(c => c.Drives[Drive.Wonder])
            .FirstOrDefault();

        var frontier = _colony.Map.Places.FirstOrDefault(p => p.IsBlocked && p.ClearingProgress > 0.02);

        var note = frontier is not null
            ? $"{frontier.Name} is {frontier.ClearingProgress:P0} open."
            : _colony.Map.Places.Any(p => p.SalvageRemaining > 0)
                ? $"{_colony.Map.Places.Sum(p => p.SalvageRemaining)} pieces of the makers' still out there."
                : "There is nothing left in the city they have not already handled.";

        var who = restless is null
            ? string.Empty
            : $" {restless.DisplayName} is the restless one — wonder at {restless.Drives[Drive.Wonder]:F2}.";

        _colony.Log(EventLayer.World, $"Day {day}. {note}{who}", notable: true);
    }

    /// <summary>
    /// Writes one raw episodic row. Cheap and constant — thousands accumulate, and Layer 2's job
    /// is not to generate them but to periodically read a batch and decide what they meant.
    /// </summary>
    /// <summary>
    /// Writes one episodic memory. Public so the player's verbs record through the same path as
    /// everything else the body notices: same ids, same folding of repeats, same event.
    /// </summary>
    public EpisodicMemory Record(
        Colonist colonist,
        string verb,
        string? about,
        string placeId,
        double valence,
        double intensity,
        string? with = null)
    {
        var memory = new EpisodicMemory
        {
            Id = $"m-{_nextMemoryId++}",
            Tick = _colony.Tick,
            Day = _colony.Day,
            // The other person, when there is one. The name is already in the text for the mind to
            // read; the id is what lets the inspector link a memory to the mind of whoever else was
            // there, and the two can diverge once colonists start taking names.
            Actors = with is null ? [colonist.Id] : [colonist.Id, with],
            Verb = verb,
            Object = about,
            PlaceId = placeId,
            Valence = valence,
            Intensity = intensity
        };

        colonist.Memory.Record(memory);
        MemoryRecorded?.Invoke(colonist, memory);
        return memory;
    }
}
