using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M14 — the Loom, fed by work. Through v1 the charge arrived at a fixed trickle whatever anybody
/// did, which is why the Print fork had no argument in it: the council was not deciding whether the
/// colony could afford another mouth, it was waiting for a number to cross a line by itself.
/// Measured over 2160 days on the shipped seed, a colony reached 57.8 charge without one person
/// having earned any of it, and never printed once.
/// </summary>
public class LoomTests
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

    private static void RaiseGranaries(Colony colony, int how_many)
    {
        foreach (var site in colony.Map.Places.Where(p => p.Becomes == PlaceKind.Granary).Take(how_many))
        {
            site.Kind = PlaceKind.Granary;
            site.BuildDone = site.BuildRequired;
        }
    }

    [Fact]
    public void TheLoomIsFedByWorkAndNothingElse()
    {
        var (colony, _) = Run(3);

        Assert.Equal(0, colony.LoomChargePerDay);
        Assert.True(colony.ProducedPerDay > 0, "a colony that worked all day put something in the Loom");
    }

    /// <summary>
    /// The Loom prints grown people, so a printed mind pays its way at once. A child does not, and
    /// that is what makes a birth cost the colony something — a third of a lifetime of being fed
    /// before it starts feeding anyone.
    /// </summary>
    [Fact]
    public void AChildDrawsOnTheStoresBeforeItFeedsThem()
    {
        var (colony, _) = Run(1);
        var printed = colony.Living.First();

        Assert.True(LifeRules.IsGrown(printed, colony.Day));

        // Far enough on that the founders can pair, so the child is a real one rather than a
        // hand-built record that might not match what ColonyFactory makes.
        var (grown, body2) = Run((int)LifeRules.MaturityDays + 2);
        var pair = grown.Living.Take(2).ToList();

        var child = body2.TryPair(pair[0], pair[1]);
        Assert.NotNull(child);
        Assert.Equal(LineageKind.Paired, child!.Lineage);
        Assert.False(LifeRules.IsGrown(child, grown.Day));
        Assert.True(LifeRules.IsGrown(child, grown.Day + LifeRules.MaturityDays));
    }

    /// <summary>
    /// The living eat out of the same stock the work goes into, so what the colony can spare depends
    /// on how many of them there are. This is the whole of the resource argument.
    /// </summary>
    [Fact]
    public void MoreMouthsMeansMoreChargeNeededBeforeAPrintIsWorthArguingAbout()
    {
        var (small, _) = Run(1, colonists: 4);
        var (large, _) = Run(1, colonists: 12);

        Assert.True(large.DrawPerDay > small.DrawPerDay);
        Assert.True(ForkRules.ChargeNeededToPrint(large) > ForkRules.ChargeNeededToPrint(small) * 2,
            "twelve mouths should want plainly more behind a print than four");
    }

    /// <summary>
    /// What there is nowhere to keep is not kept, and a granary is what the colony builds to change
    /// that. Measured without a ceiling, a settled colony of twenty adults reached 2,592 charge and
    /// eight hundred days of stores, at which point the Print fork's resource argument was vacuous
    /// again from the other direction.
    /// </summary>
    [Fact]
    public void TheStoresOnlyHoldWhatThereIsSomewhereToPut()
    {
        var (colony, body) = Run(1);

        Assert.Equal(Colony.BaseStoreCapacity, colony.StoreCapacity);

        colony.LoomCharge = 10_000;
        body.Tick();

        // A tick's work is added after the draw is taken, so the charge can sit one tick's worth
        // over the line before the next tick trims it. Exactly on the line would be asserting the
        // order of two statements rather than the rule.
        Assert.InRange(colony.LoomCharge, colony.StoreCapacity - 0.01, colony.StoreCapacity + 0.05);
        Assert.True(colony.StoresOverflowing);
        Assert.Contains(colony.Feed.Events, e => e.Text.Contains("as full as they will go"));

        RaiseGranaries(colony, 2);
        Assert.Equal(Colony.BaseStoreCapacity + 2 * Colony.StorePerGranary, colony.StoreCapacity);

        colony.LoomCharge = 10_000;
        body.Tick();
        Assert.InRange(colony.LoomCharge, colony.StoreCapacity - 0.01, colony.StoreCapacity + 0.05);
    }

    /// <summary>
    /// A colony that has nowhere to keep anything cannot print at all, because what a print plus
    /// two months of one more mouth costs is more than the Loom's own cells hold at any population.
    /// Deliberate, and the strongest thing M10's buildings have ever done: you cannot feed another
    /// mouth until you have somewhere to keep food.
    /// </summary>
    [Fact]
    public void AColonyWithNowhereToKeepAnythingCannotPrint()
    {
        var (colony, _) = Run(1);
        colony.LoomCharge = Colony.BaseStoreCapacity;

        Assert.True(ForkRules.ChargeNeededToPrint(colony) > Colony.BaseStoreCapacity);
        Assert.False(ForkRules.CanFeedAnother(colony));

        RaiseGranaries(colony, 1);
        colony.LoomCharge = colony.StoreCapacity;
        Assert.True(ForkRules.CanFeedAnother(colony));
    }

    /// <summary>
    /// And the stock reaches the two readers of a fork in the two forms they can read: words for a
    /// mind, a number for a colony that has none. The words are the half that was *measured not to
    /// work on its own* — see ForkRules.CanFeedAnother.
    /// </summary>
    [Fact]
    public void TheStoresReachBothReadersOfTheFork()
    {
        var (colony, _) = Run(4);
        RaiseGranaries(colony, 1);
        colony.OpenFork = null;

        colony.LoomCharge = colony.StoreCapacity;
        var full = ForkRules.Detect(colony);

        Assert.True(full is { Kind: ForkKind.Print },
            $"expected a print fork; charge {colony.LoomCharge:F0}/{colony.StoreCapacity:F0}, "
            + $"needed {ForkRules.ChargeNeededToPrint(colony):F0}, got {full?.Kind.ToString() ?? "nothing"}");

        Assert.Contains("as full as they will go", full!.Stakes);
        Assert.True(full.Pressure > 0.5, $"full stores should argue for a print (was {full.Pressure:F2})");

        // No figures anywhere in it — CLAUDE.md's standing rule, and the thing most likely to rot.
        Assert.DoesNotContain(full.Stakes, char.IsDigit);
    }

    /// <summary>
    /// Pressure can be negative here, which it cannot be anywhere else: a print the colony cannot
    /// feed is worse than no print, where a building it merely does not need is only unnecessary.
    /// </summary>
    [Fact]
    public void ThinStoresArgueAgainstRatherThanWeaklyFor()
    {
        var (colony, _) = Run(1);
        RaiseGranaries(colony, 2);
        colony.LoomCharge = colony.StoreCapacity * 0.05;

        var sociable = colony.Living.First();
        sociable.Weights.Vector[Emberloom.Core.Drives.Drive.Belonging] = 1.6;
        sociable.Weights.Vector[Emberloom.Core.Drives.Drive.Safety] = 1.0;

        // Thin stores have to be able to turn somebody who would otherwise want the company.
        var thin = ForkRules.FallbackVote(ForkKind.Print, sociable, -1.0);
        var deep = ForkRules.FallbackVote(ForkKind.Print, sociable, 1.0);

        Assert.Equal(Stance.For, deep.Stance);
        Assert.NotEqual(Stance.For, thin.Stance);
    }

    /// <summary>
    /// Prints were never capped and births always were, which went unnoticed while the Print fork
    /// almost never carried. With labour feeding the Loom it carries often: three seeds measured at
    /// M14 ran to 29, 41 and 27 living before this was fixed, against a cap of 20.
    /// </summary>
    [Fact]
    public void TheColonyDoesNotAskAQuestionItWouldRefuseToHonour()
    {
        var (colony, _) = Run(1, colonists: LifeRules.PopulationCap);
        RaiseGranaries(colony, 3);
        colony.LoomCharge = colony.StoreCapacity;

        Assert.DoesNotContain(ForkRules.Detect(colony)?.Kind, new ForkKind?[] { ForkKind.Print });
    }

    /// <summary>The whole economy survives the process, like everything else — M11.</summary>
    [Fact]
    public void TheStoresSurviveASave()
    {
        var (colony, body) = Run(40);
        var snapshot = Emberloom.Core.Persistence.ColonySnapshot.Capture(colony, body);
        var (loaded, _) = snapshot.Restore();

        Assert.Equal(colony.LoomCharge, loaded.LoomCharge, 6);
        Assert.Equal(colony.ProducedPerDay, loaded.ProducedPerDay, 6);
        Assert.Equal(colony.DrawPerHeadPerDay, loaded.DrawPerHeadPerDay, 6);
        Assert.Equal(colony.ChargePerWorkDay, loaded.ChargePerWorkDay, 6);
    }
}
