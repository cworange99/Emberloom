using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M15 — the three couplings that make the rest of v2 cost something. An empty store is felt as
/// hunger, a life going badly is a shorter life, and a bed somebody else is in is not a bed.
/// <para>
/// Two of the three are safety nets: measured over 2160 days, a healthy colony never empties its
/// stores and builds dwellings before it runs out of beds, so neither fires. That they *can* fire
/// is what these tests are for — a coupling that is inert in the common case and untested in the
/// uncommon one is not a coupling.
/// </para>
/// </summary>
public class CouplingTests
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

    // ---- the stores reach the body ----------------------------------------------------------

    [Fact]
    public void AnEmptyStoreIsFeltAsHunger()
    {
        var (colony, body) = Run(2);

        // The colony's own work would cover most of a day's draw, so it is turned off: what is
        // being tested is what an empty store does to a body, not whether the stores empty.
        colony.ChargePerWorkDay = 0;
        colony.LoomCharge = 0;

        foreach (var colonist in colony.Living)
        {
            colonist.Drives.Levels[Drive.Sustenance] = 0.1;
        }

        for (var tick = 0; tick < Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.True(colony.Shortfall > 0.9, $"an empty Loom should be a full shortfall (was {colony.Shortfall:F2})");
        Assert.All(colony.Living, c => Assert.True(c.Drives[Drive.Sustenance] > 0.1,
            "going without should press on Sustenance, not merely fail to relieve it"));
    }

    [Fact]
    public void AFullStoreIsFeltAsNothingAtAll()
    {
        var (colony, body) = Run(2);
        colony.LoomCharge = colony.StoreCapacity;

        body.Tick();

        Assert.Equal(0, colony.Shortfall);
    }

    // ---- despair shortens a life, and does not end one --------------------------------------

    /// <summary>
    /// The shape the scope asked for: despair makes somebody go early rather than being a second
    /// way to die. <c>AdvanceLives</c> keeps its one death trigger, every existing death test holds,
    /// and the chronicle's "42 of about 46 days" line stays truthful.
    /// </summary>
    [Fact]
    public void DespairTakesDaysOffALifeRatherThanEndingIt()
    {
        var (colony, body) = Run(2);
        var failing = colony.Living.First();
        var born = failing.LifespanDays;

        failing.Wellbeing = 0.05;

        for (var tick = 0; tick < 60 * Colony.TicksPerDay; tick++)
        {
            failing.Wellbeing = 0.05;
            body.Tick();
        }

        Assert.True(failing.IsAlive, "sixty bad days is not a death sentence");
        Assert.True(failing.DaysLostToDespair > 1, $"it should have cost something (lost {failing.DaysLostToDespair:F1})");
        Assert.Equal(born - failing.DaysLostToDespair, failing.LifespanDays, 3);
    }

    [Fact]
    public void AnOrdinaryHardLifeCostsNothing()
    {
        var (colony, body) = Run(2);
        var coping = colony.Living.First();
        var born = coping.LifespanDays;

        for (var tick = 0; tick < 30 * Colony.TicksPerDay; tick++)
        {
            coping.Wellbeing = WellbeingRules.DespairBelow + 0.01;
            body.Tick();
        }

        Assert.Equal(born, coping.LifespanDays, 6);
        Assert.Equal(0, coping.DaysLostToDespair);
    }

    /// <summary>
    /// The cap, which is what keeps despair from being an instakill by instalments. It is measured
    /// against the life as it was born rather than against what is left, so it cannot be walked
    /// past a day at a time.
    /// </summary>
    [Fact]
    public void DespairCanNeverTakeMoreThanItsShare()
    {
        var (colony, body) = Run(2);
        var hopeless = colony.Living.First();
        var born = hopeless.LifespanDays;

        for (var tick = 0; tick < 2000 * Colony.TicksPerDay && hopeless.IsAlive; tick++)
        {
            hopeless.Wellbeing = 0;
            body.Tick();
        }

        Assert.InRange(hopeless.DaysLostToDespair, 0, born * WellbeingRules.MostOfALifeDespairCanTake + 0.01);
    }

    /// <summary>
    /// The damper the sweep made necessary. Urgency is <c>(level × weight)²</c>, so it runs past 1,
    /// and without a cap the pressure term was worth more than the whole hour it discounts — which
    /// made the last two people in a dying colony read as exactly zero for every moment of their
    /// lives, since Talk needs a partner and a gathering needs a hall. Over 4000 days that was
    /// enough to take the colony extinct. See DESIGN-V2.md for the sweep either side of this line.
    /// </summary>
    [Fact]
    public void TheLastPersonAliveIsMiserableRatherThanAtZero()
    {
        var (colony, _) = Run(2);
        var alone = colony.Living.First();

        foreach (var drive in DriveTable.All)
        {
            alone.Drives.Levels[drive] = 1.0;
        }

        alone.Current = new CurrentAction
        {
            Definition = ActionCatalog.Rest,
            Target = colony.Map.Places.First(p => p.Kind == PlaceKind.Shelter),
            ChosenScore = 1
        };

        var moment = WellbeingRules.Moment(alone, travelling: false);

        Assert.True(moment > 0.2, $"an hour asleep is still sleep however bad things are (was {moment:F2})");
        Assert.True(moment < WellbeingRules.Ease(ActionKind.Rest), "and it should still be worse than an easy one");
    }

    // ---- a bed somebody else is in is not a bed ----------------------------------------------

    [Fact]
    public void TheColonyBeginsWithRoomForSixAndKnowsIt()
    {
        var (colony, _) = Run(1);

        Assert.Equal(6, colony.Beds);
        Assert.All(colony.Map.OfKind(PlaceKind.Shelter), p => Assert.Equal(3, p.Beds));
    }

    [Fact]
    public void SleepIsNotOfferedWhereThereIsNoBedFree()
    {
        var (colony, _) = Run(2);
        var shelter = colony.Map.OfKind(PlaceKind.Shelter).First();
        var scorer = new UtilityScorer(new ColonyRandom(1));

        var sleeper = colony.Living.First();
        sleeper.PlaceId = shelter.Id;

        Assert.Contains(scorer.Score(sleeper, colony),
            s => s.Definition.Kind == ActionKind.Rest && s.Target.Id == shelter.Id);

        // Fill it with other people, and it stops being somewhere this colonist can sleep.
        foreach (var other in colony.Living.Where(c => c.Id != sleeper.Id).Take(shelter.Beds))
        {
            other.PlaceId = shelter.Id;
            other.Current = new CurrentAction { Definition = ActionCatalog.Rest, Target = shelter, ChosenScore = 1 };
        }

        Assert.DoesNotContain(scorer.Score(sleeper, colony),
            s => s.Definition.Kind == ActionKind.Rest && s.Target.Id == shelter.Id);
    }

    /// <summary>
    /// And the colonist already in the bed is not kept out of it by their own occupancy, which
    /// would make a full room evict everybody in it every time they reconsidered.
    /// </summary>
    [Fact]
    public void SomebodyAlreadyAsleepIsNotTurnedOutOfTheirOwnBed()
    {
        var (colony, _) = Run(2);
        var shelter = colony.Map.OfKind(PlaceKind.Shelter).First();

        foreach (var colonist in colony.Living.Take(shelter.Beds))
        {
            colonist.PlaceId = shelter.Id;
            colonist.Current = new CurrentAction { Definition = ActionCatalog.Rest, Target = shelter, ChosenScore = 1 };
        }

        var sleeping = colony.Living.First();
        var scorer = new UtilityScorer(new ColonyRandom(1));

        Assert.Contains(scorer.Score(sleeping, colony),
            s => s.Definition.Kind == ActionKind.Rest && s.Target.Id == shelter.Id);
    }

    /// <summary>
    /// The colony notices. Beds are what a dwelling is for, and the body proposes one as soon as
    /// there are more people than places to put them — measured over 2160 days, seeds build between
    /// one and five of them and end with just enough.
    /// </summary>
    [Fact]
    public void TheColonyAsksForSomewhereToSleepOnceItHasRunOut()
    {
        var (colony, _) = Run(3);

        // Four founders in six beds: nothing presses.
        var site = colony.Map.Places.First(p => p.Becomes == PlaceKind.Dwelling);
        Assert.True(ForkRules.Pressure(colony, site) == 0,
            $"{colony.Living.Count()} in {colony.Beds} beds should press nobody "
            + $"(was {ForkRules.Pressure(colony, site):F2})");

        var random = new Random(2);
        for (var i = 0; i < 6; i++)
        {
            ColonyFactory.Print(colony, random, chargeIsFree: true);
        }

        // Ten people in six beds, and it is the only thing anybody wants to talk about.
        Assert.Equal(PlaceKind.Dwelling, ForkRules.Propose(colony)!.Becomes);
        Assert.True(ForkRules.Pressure(colony, site) > 0.5,
            $"{colony.Living.Count()} in {colony.Beds} beds should press hard "
            + $"(was {ForkRules.Pressure(colony, site):F2})");
    }

    [Fact]
    public void WhatDespairTookSurvivesASave()
    {
        var (colony, body) = Run(2);
        var failing = colony.Living.First();

        for (var tick = 0; tick < 40 * Colony.TicksPerDay; tick++)
        {
            failing.Wellbeing = 0.05;
            body.Tick();
        }

        var (loaded, _) = Emberloom.Core.Persistence.ColonySnapshot.Capture(colony, body).Restore();
        var same = loaded.Find(failing.Id)!;

        Assert.Equal(failing.DaysLostToDespair, same.DaysLostToDespair, 6);
        Assert.Equal(failing.LifespanDays, same.LifespanDays, 6);
    }
}
