using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.World;

namespace Emberloom.Core.Actions;

public sealed record ScoredAction(
    ActionDefinition Definition,
    Place Target,
    string? PartnerId,
    double Score,
    double TravelPenalty)
{
    public override string ToString() => $"{Definition.Kind} @ {Target.Name} = {Score:F3}";
}

/// <summary>
/// Plain utility AI, no planner:
/// <code>score = Σ(drive_urgency × action_relief × personality_weight)
///         × context_modifier × relationship_modifier</code>
/// Runs every tick for every colonist and never touches a model. The only thing Layer 2
/// contributes here is <see cref="PersonalityWeights"/>.
/// </summary>
public sealed class UtilityScorer
{
    /// <summary>Keeps identical colonists from moving in lockstep.</summary>
    private const double NoiseScale = 0.06;

    /// <summary>
    /// The bonus an in-progress action gets when re-scored, so a colonist does not thrash between
    /// two near-equal options and never finishes either.
    /// </summary>
    private const double HysteresisBonus = 0.22;

    /// <summary>
    /// Additional commitment proportional to how far through the action already is. Without it,
    /// re-scoring every few ticks abandons work at 90% as readily as at 10%, and colonists spend
    /// the whole day starting things — which is exactly what they did before this existed.
    /// </summary>
    private const double CommitmentBonus = 0.9;

    /// <summary>
    /// How strongly a council member is drawn to the plaza while their fork sits. Strong — this is
    /// the one place the colony's business outranks a colonist's own — but still a score, so a
    /// member who is starving eats first.
    /// </summary>
    private const double CouncilPull = 4.0;

    private readonly Random _random;

    public UtilityScorer(Random random)
    {
        _random = random;
    }

    public ScoredAction? Choose(Colonist colonist, Colony colony)
    {
        var options = Score(colonist, colony);
        return options.Count == 0 ? null : options[0];
    }

    /// <summary>Every option this colonist can see right now, best first. The inspector shows the runners-up.</summary>
    public List<ScoredAction> Score(Colonist colonist, Colony colony)
    {
        var results = new List<ScoredAction>();
        var here = colony.Map[colonist.PlaceId];
        var reachable = colony.Map.Reachable(here).ToList();

        // Blocked places adjoining somewhere reachable are valid destinations — you have to stand
        // at a stairwell to clear it — even though you cannot path through them.
        var frontier = reachable
            .SelectMany(p => p.Neighbours)
            .Select(id => colony.Map[id])
            .Where(p => p.IsBlocked)
            .Distinct();

        // A floor the colony has not agreed to go to is off everyone's list, however much they want
        // it. That is what a fork's "no" means, and what "not yet" means before it is asked.
        foreach (var place in reachable.Concat(frontier).Distinct().Where(p => !colony.ForbiddenPlaces.Contains(p.Id)))
        {
            foreach (var definition in ActionCatalog.All)
            {
                if (!definition.CanHappenAt(place) || !IsAvailable(definition, place))
                {
                    continue;
                }

                if (definition.RequiresCompany)
                {
                    foreach (var partner in colony.ColonistsAt(place.Id).Where(c => c.Id != colonist.Id))
                    {
                        results.Add(Evaluate(colonist, colony, definition, place, partner));
                    }
                }
                else
                {
                    results.Add(Evaluate(colonist, colony, definition, place, partner: null));
                }
            }
        }

        if (colony.OpenFork is { } fork
            && fork.Participants.Contains(colonist.Id)
            && colony.Map.Find(fork.PlaceId) is { } hall)
        {
            var sitting = Evaluate(colonist, colony, ActionCatalog.Deliberate, hall, partner: null);
            results.Add(sitting with { Score = sitting.Score * CouncilPull });
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    private static bool IsAvailable(ActionDefinition definition, Place place) => definition.Kind switch
    {
        ActionKind.Forage => place.ForageYield > 0,
        ActionKind.Salvage => place.SalvageRemaining > 0 && !place.IsBlocked,
        ActionKind.Clear => place.IsBlocked,
        ActionKind.Build => !place.IsBlocked,
        _ => !place.IsBlocked
    };

    private ScoredAction Evaluate(
        Colonist colonist,
        Colony colony,
        ActionDefinition definition,
        Place place,
        Colonist? partner)
    {
        var raw = 0.0;

        foreach (var drive in DriveTable.All)
        {
            raw += colonist.Drives.Urgency(drive, colonist.Weights[drive])
                   * definition.Relief[drive];
        }

        var contextModifier = ContextModifier(colonist, colony, definition, place);

        var relationshipModifier = 1.0;

        if (partner is not null)
        {
            var relationship = colony.Relationships.Get(colonist.Id, partner.Id);

            // Liking someone makes their company more attractive — but on its own that is pure
            // positive feedback, and the whole colony ends up in one room talking forever. The
            // hundredth conversation with the same person is worth less than the first.
            var novelty = 1.0 / (1.0 + relationship.SharedMemories / 30.0);

            relationshipModifier = relationship.ActionModifier * novelty;
        }

        var travelPenalty = TravelPenalty(colony, colonist, place);

        var score = raw * contextModifier * relationshipModifier * travelPenalty;

        // Noise scales with the score so it perturbs choices rather than inventing them.
        score *= 1.0 + (_random.NextDouble() * 2 - 1) * NoiseScale;

        if (colonist.Current is { } current
            && current.Definition.Kind == definition.Kind
            && current.Target.Id == place.Id
            && !current.IsComplete)
        {
            // Ticks already spent walking count as commitment too, otherwise a colonist re-scores
            // mid-journey, picks something nearer, and never arrives anywhere far.
            var invested = current.TicksElapsed > 0
                ? CommitmentBonus * current.Progress
                : Math.Min(CommitmentBonus, current.TravelTicks * 0.06);

            score *= 1.0 + HysteresisBonus + invested;
        }

        return new ScoredAction(definition, place, partner?.Id, score, travelPenalty);
    }

    /// <summary>
    /// The <c>context_modifier</c> term: darkness, danger, whether the colonist is already
    /// standing there, and how a cautious mind reads a place a bold one walks straight into.
    /// </summary>
    private static double ContextModifier(
        Colonist colonist,
        Colony colony,
        ActionDefinition definition,
        Place place)
    {
        var modifier = 1.0;

        if (definition.ExposesToDanger && place.Danger > 0)
        {
            // A colonist weighted heavily on Safety reads the same stairwell as a worse idea.
            //
            // This has to bite hard, and it is the main reason it is exponential rather than
            // linear. Personality cannot change how much *time* a colonist spends satisfying a
            // drive — that is fixed by the drive's decay rate, and a low weight just lets the
            // drive settle higher until it demands the same attention anyway. What personality
            // can change is *which route* they take to satisfy it. So the difference between a
            // bold colonist and a careful one lives here, in how they price the risky route
            // against the safe one, not in how badly either of them wants the thing.
            var aversion = place.Danger * colonist.Weights[Drive.Safety] * (0.5 + colonist.Drives[Drive.Safety]);
            modifier *= Math.Exp(-3.2 * aversion);
        }

        if (colony.LightAt(place) < 0.5 && colony.IsNight)
        {
            modifier *= definition.Kind == ActionKind.Rest ? 1.0 : 0.35;
        }

        if (definition.Kind == ActionKind.Rest && !colony.IsNight)
        {
            modifier *= 0.45;
        }

        if (definition.Kind == ActionKind.Rest && colony.IsWarmed(place.Id))
        {
            modifier *= Verbs.ProvisionRules.WarmRestBonus;
        }

        if (definition.Kind == ActionKind.Forage)
        {
            modifier *= 0.6 + place.ForageYield * 3.5;
        }

        if (definition.Kind == ActionKind.Wander)
        {
            // Wandering is what is left when nothing else scores. It should rarely win outright.
            modifier *= 0.5;
        }

        if (!place.IsExplored)
        {
            modifier *= 1.0 + 0.4 * colonist.Weights[Drive.Wonder];
        }

        // Solitary work gets less attractive the more crowded a place already is, so the colony
        // spreads across the city instead of collapsing into whichever room it drifted into
        // first. Social actions are exempt — company is the entire point of them.
        if (!definition.RequiresCompany)
        {
            var others = colony.ColonistsAt(place.Id).Count(c => c.Id != colonist.Id);
            modifier *= 1.0 / (1.0 + others * 0.18);
        }

        return modifier;
    }

    /// <summary>Distance is a straight multiplicative discount — near work beats far work, all else equal.</summary>
    private static double TravelPenalty(Colony colony, Colonist colonist, Place place)
    {
        var here = colony.Map[colonist.PlaceId];
        if (here.Id == place.Id)
        {
            return 1.0;
        }

        var distance = here.DistanceTo(place);
        return 1.0 / (1.0 + distance / 90.0);
    }
}
