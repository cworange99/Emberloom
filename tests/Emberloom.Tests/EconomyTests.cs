using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Life;
using Emberloom.Core.Persistence;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M13 — the first thing in this world that costs money. Work pays the worker, and an evening at a
/// hall the colony voted to raise is bought out of it.
/// <para>
/// The measured claim behind the milestone: leisure was chosen 0.0% of the time at every population
/// through M12, because Wander relieves almost nothing and the body always had something better to
/// do. These tests are the mechanism; DESIGN-V2.md holds the measurement.
/// </para>
/// </summary>
public class EconomyTests
{
    private static (Colony Colony, BodySimulation Body) Run(int days, int seed = 4, int colonists = 4)
    {
        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed);
        var body = new BodySimulation(colony, random);

        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        return (colony, body);
    }

    private static Place Hall(Colony colony)
    {
        var site = colony.Map.Places.First(p => p.Becomes == PlaceKind.Hall);
        site.Kind = PlaceKind.Hall;
        site.IsExplored = true;
        return site;
    }

    [Fact]
    public void WorkPaysAndTheRestOfLifeDoesNot()
    {
        var (colony, _) = Run(20);

        Assert.All(colony.Living, c => Assert.True(c.Tokens >= 0));
        Assert.Contains(colony.Colonists, c => c.Tokens > 0 || c.EveningsBought > 0);

        // Nothing but work pays, so nobody can have earned more than the colony-days they have
        // lived — which is also the ceiling the price sweep is quoted in fractions of.
        foreach (var colonist in colony.Colonists)
        {
            var earned = colonist.Tokens + colonist.EveningsBought * ActionCatalog.Gather.Price;
            Assert.True(earned <= colonist.AgeInDays(colony.Day) * EconomyRules.WagePerWorkDay + 0.01,
                $"{colonist.DisplayName} earned {earned:F2} in {colonist.AgeInDays(colony.Day):F2} days");
        }
    }

    /// <summary>
    /// The affordability gate, which is the whole mechanism: an evening a colonist cannot pay for is
    /// not on their list at all. Measured as a slope rather than a step across two decades of price,
    /// which is why this hard gate was kept over a score penalty — see DESIGN-V2.md.
    /// </summary>
    [Fact]
    public void AnEveningYouCannotAffordIsNotOffered()
    {
        var (colony, _) = Run(1);
        var hall = Hall(colony);

        var colonist = colony.Living.First();
        colonist.PlaceId = hall.Id;

        // Make an evening the obviously right thing to want, so the only thing keeping it off the
        // list is the price.
        foreach (var drive in DriveTable.All)
        {
            colonist.Drives.Levels[drive] = 0.0;
        }

        colonist.Drives.Levels[Drive.Standing] = 1.0;

        var scorer = new UtilityScorer(new ColonyRandom(1));

        colonist.Tokens = 0;
        Assert.DoesNotContain(scorer.Score(colonist, colony), s => s.Definition.Kind == ActionKind.Gather);

        colonist.Tokens = ActionCatalog.Gather.Price;
        Assert.Contains(scorer.Score(colonist, colony), s => s.Definition.Kind == ActionKind.Gather);
    }

    /// <summary>
    /// Paid on the way in. A colonist who walks out halfway through has still spent the token, which
    /// is what stops the price being something you try your luck against.
    /// </summary>
    [Fact]
    public void TheEveningIsPaidForOnTheWayIn()
    {
        var (colony, body) = Run(1);
        var hall = Hall(colony);

        var colonist = colony.Living.First();
        colonist.PlaceId = hall.Id;
        colonist.Tokens = 10;
        colonist.Current = new CurrentAction
        {
            Definition = ActionCatalog.Gather,
            Target = hall,
            ChosenScore = 99
        };

        // One tick, far short of the twenty an evening takes.
        body.Tick();

        Assert.Equal(1, colonist.EveningsBought);
        Assert.True(colonist.Tokens < 10, "the token should be gone before the evening is over");
    }

    /// <summary>
    /// A hall is the only place it can happen, which is M10's payoff: the colony's one piece of
    /// leisure exists because the colony voted to build somewhere for it.
    /// </summary>
    [Fact]
    public void ThereIsNowhereToSpendItUntilTheColonyBuildsOne()
    {
        var (colony, _) = Run(1);

        Assert.DoesNotContain(colony.Map.Places, p => p.Kind == PlaceKind.Hall);

        var colonist = colony.Living.First();
        colonist.Tokens = 100;

        var scorer = new UtilityScorer(new ColonyRandom(1));
        Assert.DoesNotContain(scorer.Score(colonist, colony), s => s.Definition.Kind == ActionKind.Gather);

        Hall(colony);
        Assert.Contains(scorer.Score(colonist, colony), s => s.Definition.Kind == ActionKind.Gather);
    }

    /// <summary>
    /// Layer 1. A colony with no mind earns, saves and spends like any other — these runs make no
    /// model calls at all.
    /// </summary>
    [Fact]
    public void AColonyWithNoMindStillHasAnEconomy()
    {
        var (colony, _) = Run(400);

        Assert.Contains(colony.Colonists, c => c.EveningsBought > 0);
    }

    /// <summary>A purse survives the process, like everything else about a colonist — M11.</summary>
    [Fact]
    public void APurseSurvivesASave()
    {
        var (colony, body) = Run(60);
        var before = colony.Colonists.Select(c => (c.Id, c.Tokens, c.EveningsBought)).ToList();

        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();

        foreach (var (id, tokens, evenings) in before)
        {
            var same = loaded.Find(id)!;
            Assert.Equal(tokens, same.Tokens, 6);
            Assert.Equal(evenings, same.EveningsBought);
        }
    }

    /// <summary>
    /// Every action now has to say what it does when it runs and when it finishes, because a switch
    /// with no default arm is how Build spent the whole of version 1 doing nothing while looking
    /// implemented. This walks the catalog so a tenth action cannot be added without deciding.
    /// </summary>
    [Fact]
    public void EveryActionDeclaresWhatItDoes()
    {
        var (colony, body) = Run(1);
        var colonist = colony.Living.First();

        foreach (var definition in ActionCatalog.All.Append(ActionCatalog.Deliberate))
        {
            var place = colony.Map.Places.FirstOrDefault(p => definition.CanHappenAt(p))
                        ?? colony.Map[colonist.PlaceId];

            colonist.Tokens = 100;
            colonist.PlaceId = place.Id;
            colonist.Current = new CurrentAction { Definition = definition, Target = place, ChosenScore = 1 };

            // One tick for the running effect, then forced to its end for the completing one.
            body.Tick();

            if (colonist.Current is { } running)
            {
                running.TicksElapsed = definition.DurationTicks - 1;
                body.Tick();
            }
        }
    }
}
