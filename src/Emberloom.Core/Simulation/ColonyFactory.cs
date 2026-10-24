using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Life;
using Emberloom.Core.World;

namespace Emberloom.Core.Simulation;

/// <summary>
/// Seeds a colony. In M1 personalities come from <see cref="Archetypes"/>; from M2 the same entry
/// points call Kindling instead, and nothing else about this changes.
/// </summary>
public static class ColonyFactory
{
    /// <summary>
    /// A new colony. The seed is passed in as well as being the random stream's, because names are
    /// derived from it rather than drawn from the stream — see <see cref="Names"/>.
    /// </summary>
    public static Colony Create(int colonists, Random random, int seed = 0, ColonyMode mode = ColonyMode.Hybrid)
    {
        // The mode is chosen here and nowhere else: it is init-only on Colony because a research
        // run has to be a research run from the first tick to the last, or it is not evidence of
        // anything. See ColonyMode.
        var colony = new Colony(CityMap.CreateDefault()) { Seed = seed, Mode = mode };

        for (var i = 0; i < colonists; i++)
        {
            Print(colony, random, chargeIsFree: true);
        }

        colony.Log(EventLayer.World,
            $"The Loom has printed {colonists} minds into the city. None of them were told why.",
            notable: true);

        return colony;
    }

    /// <summary>
    /// Instantiates a mind from the Loom. Deliberate, expensive, political — the charge spent here
    /// is charge not spent on anything else, and everyone knows it.
    /// </summary>
    public static Colonist? Print(Colony colony, Random random, bool chargeIsFree = false)
    {
        if (!chargeIsFree)
        {
            if (colony.LoomCharge < Colony.PrintCost)
            {
                return null;
            }

            colony.LoomCharge -= Colony.PrintCost;
        }

        var index = colony.Colonists.Count + 1;
        var archetype = Archetypes.Random(random);

        var colonist = new Colonist
        {
            Id = $"c{index:00}",
            Designation = $"C-{index:00}",
            Lineage = LineageKind.Printed,
            BornOnDay = colony.Day,
            // Stand-in until Kindling returns. A printed mind exists and acts from the tick it is
            // printed — waiting on inference to start living would put a model call on the
            // critical path, which is exactly what the two-layer split exists to avoid.
            Personality = archetype.Blurb,
            Weights = archetype.Weights.Clone(),
            Seed = SeedRoller.Roll(random),
            PlaceId = "loom",
            LifespanDays = LifeRules.Lifespan($"C-{index:00}"),

            // Named from the first tick, since M9. The designation stays on them as what the Loom
            // called them, and the inspector still shows it; the name is what everyone else uses.
            Name = Names.Unused($"C-{index:00}", colony)
        };

        // Printed minds wake with their drives partly loaded — they arrive already wanting things.
        foreach (var drive in DriveTable.All)
        {
            colonist.Drives.Aggravate(drive, random.NextDouble() * 0.35);
        }

        colony.Add(colonist);

        if (!chargeIsFree)
        {
            colony.Log(EventLayer.World,
                $"The Loom printed {colonist.Designation}. That charge is gone.",
                colonist, notable: true);
        }

        return colonist;
    }

    /// <summary>
    /// A successor from two colonists past a bond threshold. Weights cross over with mutation —
    /// real genetics, on values rather than bodies.
    /// </summary>
    public static Colonist Pair(Colony colony, Colonist left, Colonist right, Random random)
    {
        var index = colony.Colonists.Count + 1;

        var colonist = new Colonist
        {
            Id = $"c{index:00}",
            Designation = $"C-{index:00}",
            Lineage = LineageKind.Paired,
            ParentIds = [left.Id, right.Id],
            BornOnDay = colony.Day,
            Personality = $"Born to {left.DisplayName} and {right.DisplayName}.",
            // What Kindling writes the child from: who the parents are. The weights are already
            // theirs, crossed — Kindling writes the personality around them and keeps them.
            Seed = $"the child of {left.DisplayName} and {right.DisplayName}. " +
                   $"{left.DisplayName}: {left.Personality} {right.DisplayName}: {right.Personality}",
            Weights = PersonalityWeights.Crossover(left.Weights, right.Weights, random),
            PlaceId = left.PlaceId,
            LifespanDays = LifeRules.Lifespan(random),

            // A child is named by its parents, at once — the one thing every colony in every
            // culture does, and the thing v1 made them earn.
            Name = Names.Unused($"C-{index:00}", colony)
        };

        colony.Add(colonist);

        colony.Log(EventLayer.World,
            $"{left.DisplayName} and {right.DisplayName} have a successor: {colonist.Designation}.",
            colonist, notable: true);

        return colonist;
    }
}
