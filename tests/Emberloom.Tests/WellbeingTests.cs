using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Life;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M12 — how a life is going, as one number. Read-only at this milestone, so these tests are about
/// the number being honest rather than about anything it causes: that it moves in the direction the
/// life moves, that it cannot leave [0,1] however long a colony runs, and that a colony with no
/// mind has it like any other. What it will eventually cause is M15.
/// </summary>
public class WellbeingTests
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

    /// <summary>
    /// The claim the milestone rests on: company is better than labour, and the number says so.
    /// Two identical colonists, two different fortnights, nothing else different.
    /// </summary>
    [Fact]
    public void AFortnightOfCompanyIsWorthMoreThanAFortnightOfWork()
    {
        var (colony, _) = Run(1);
        var worker = colony.Living.First();
        var talker = colony.Living.Skip(1).First();

        // Same drives, so the only difference between them is what they spend the fortnight doing.
        foreach (var drive in DriveTable.All)
        {
            talker.Drives.Levels[drive] = worker.Drives.Levels[drive];
        }

        talker.Wellbeing = worker.Wellbeing = WellbeingRules.Start;

        var here = colony.Map[worker.PlaceId];
        worker.Current = new CurrentAction { Definition = ActionCatalog.ByKind(ActionKind.Tend), Target = here, ChosenScore = 1.0 };
        talker.Current = new CurrentAction { Definition = ActionCatalog.ByKind(ActionKind.Talk), Target = here, ChosenScore = 1.0 };

        for (var tick = 0; tick < 14 * Colony.TicksPerDay; tick++)
        {
            WellbeingRules.Advance(worker, travelling: false, 1.0 / Colony.TicksPerDay);
            WellbeingRules.Advance(talker, travelling: false, 1.0 / Colony.TicksPerDay);
        }

        Assert.True(talker.Wellbeing > worker.Wellbeing + 0.2,
            $"company should plainly beat labour (talker {talker.Wellbeing:F3} vs worker {worker.Wellbeing:F3})");
    }

    /// <summary>
    /// And that the thing pressing on someone reaches it. A colonist in desperate need is worse off
    /// doing exactly what the comfortable one is doing — which is what stops this being a clock
    /// that measures time use and calls it a life.
    /// </summary>
    [Fact]
    public void WhatIsPressingOnSomeoneReachesTheNumber()
    {
        var (colony, _) = Run(1);
        var easy = colony.Living.First();
        var desperate = colony.Living.Skip(1).First();

        foreach (var drive in DriveTable.All)
        {
            easy.Drives.Levels[drive] = 0.05;
            desperate.Drives.Levels[drive] = 0.05;
        }

        desperate.Drives.Levels[Drive.Sustenance] = 1.0;

        var here = colony.Map[easy.PlaceId];
        easy.Current = desperate.Current = new CurrentAction { Definition = ActionCatalog.ByKind(ActionKind.Rest), Target = here, ChosenScore = 1.0 };

        Assert.True(WellbeingRules.Moment(easy, travelling: false)
                    > WellbeingRules.Moment(desperate, travelling: false) + 0.1);
    }

    /// <summary>
    /// A leaky integrator cannot run away, and that is most of why it was chosen over adding and
    /// subtracting rates. Over a full lifespan, with deaths, births and everything the body does,
    /// nobody should ever have left [0,1] — if they could, M15's despair would be built on a number
    /// that silently saturates.
    /// </summary>
    [Fact]
    public void NobodyLeavesTheRangeOverAWholeLifespan()
    {
        var (colony, _) = Run(600);

        foreach (var colonist in colony.Colonists)
        {
            Assert.InRange(colonist.Wellbeing, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Layer 1, like everything in v2: a colony that has never reached a mind still knows how its
    /// people are. These runs make no model calls at all, so this is really an assertion about
    /// where the code lives — but it is the assertion that would fail first if it moved.
    /// </summary>
    [Fact]
    public void AColonyWithNoMindStillKnowsHowItsPeopleAre()
    {
        var (colony, _) = Run(120);

        Assert.All(colony.Living, c => Assert.NotEqual(WellbeingRules.Start, c.Wellbeing));
        Assert.All(colony.Living, c => Assert.NotEmpty(WellbeingRules.Describe(c.Wellbeing)));
    }

    /// <summary>
    /// It varies. The milestone's riskiest assumption was that it would not — that the scorer, whose
    /// whole job is to even out drives, would even this out too and hand back one number everybody
    /// shares. Measured over a lifespan at four founders the colony lives between 0.34 and 0.57;
    /// this is the floor under that, loose enough not to break when later milestones move it and
    /// tight enough to fail if the inputs ever stop separating people.
    /// </summary>
    [Fact]
    public void ItSeparatesPeople()
    {
        var (colony, _) = Run(600);

        var lives = colony.Living.Select(c => c.Wellbeing).ToList();
        Assert.True(lives.Count >= 3, "not enough of the colony survived to say anything about spread");
        Assert.True(lives.Max() - lives.Min() > 0.03,
            $"wellbeing should separate people, not converge (spread was {lives.Max() - lives.Min():F3})");
    }

    /// <summary>
    /// Mood was re-expressed on top of it, so a watcher should see the words change when a life
    /// changes rather than when a drive flickers. The word set is unchanged from v1.
    /// </summary>
    [Fact]
    public void MoodFollowsTheLifeRatherThanTheInstant()
    {
        var (colony, _) = Run(1);
        var colonist = colony.Living.First();

        colonist.Wellbeing = 0.9;
        var comfortable = colonist.Mood(colony.Day);

        colonist.Wellbeing = 0.1;
        Assert.NotEqual(comfortable, colonist.Mood(colony.Day));
    }
}
