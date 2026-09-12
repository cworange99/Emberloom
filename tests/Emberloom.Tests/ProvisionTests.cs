using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M6 phase B — the Loom's charge. Provide spends it on light, warmth or a door; Withhold cuts it,
/// free, and they notice; running dry does the same on its own. Reveal shows something of the
/// makers', once. All body: none of it waits on a mind.
/// </summary>
public class ProvisionTests
{
    private static (Colony Colony, BodySimulation Body) Start()
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(8, random);
        return (colony, new BodySimulation(colony, random));
    }

    [Fact]
    public void LightMakesADarkPlaceCountAsLit()
    {
        var (colony, body) = Start();
        var substation = colony.Map["substation"];
        colony.LoomCharge = 10;

        Assert.True(PlayerVerbs.Provide(colony, body, ProvideKind.Light, "substation").Ok);

        Assert.Equal(1.0, colony.LightAt(substation));
        Assert.Equal(ProvisionRules.LightPerDay, colony.UpkeepPerDay);

        // Twice is refused, and so is lighting somewhere already bright.
        Assert.False(PlayerVerbs.Provide(colony, body, ProvideKind.Light, "substation").Ok);
        Assert.False(PlayerVerbs.Provide(colony, body, ProvideKind.Light, "plaza").Ok);
    }

    [Fact]
    public void LightIsPaidForByTheDay()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 10;
        PlayerVerbs.Provide(colony, body, ProvideKind.Light, "substation");

        for (var tick = 0; tick < Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.Equal(10 + colony.LoomChargePerDay - ProvisionRules.LightPerDay, colony.LoomCharge, precision: 3);
    }

    [Fact]
    public void WhenTheLoomRunsDryTheNewestGoesOutFirst()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 2;
        colony.LoomChargePerDay = 0;
        PlayerVerbs.Provide(colony, body, ProvideKind.Light, "substation");
        PlayerVerbs.Provide(colony, body, ProvideKind.Warmth, "shelter-under");

        for (var tick = 0; tick < 3 * Colony.TicksPerDay && colony.Provisions.Count == 2; tick++)
        {
            body.Tick();
        }

        var left = Assert.Single(colony.Provisions);
        Assert.Equal(ProvisionKind.Light, left.Kind);
        Assert.Contains(colony.Feed.Recent(500), e => e.Text == "The Loom ran short, and the warmth at The Underhouse went out.");
    }

    [Fact]
    public void CuttingItIsFreeAndTheyNotice()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 10;
        PlayerVerbs.Provide(colony, body, ProvideKind.Light, "substation");

        var there = colony.Colonists[0];
        there.PlaceId = "substation";
        var elsewhere = colony.Colonists[1];
        elsewhere.PlaceId = "orchard";
        var safety = there.Drives[Drive.Safety];

        var result = PlayerVerbs.Withhold(colony, body, ProvisionKind.Light, "substation");

        Assert.True(result.Ok);
        Assert.Empty(colony.Provisions);
        Assert.Equal(10, colony.LoomCharge);
        Assert.Contains(there.Memory.Episodic, m => m.Verb == "noticed the light go out at");
        Assert.True(there.Drives[Drive.Safety] > safety);
        Assert.DoesNotContain(elsewhere.Memory.Episodic, m => m.Verb == "noticed the light go out at");

        Assert.False(PlayerVerbs.Withhold(colony, body, ProvisionKind.Light, "substation").Ok);
    }

    [Fact]
    public void WarmthOnlyHoldsInAShelter()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 10;

        Assert.False(PlayerVerbs.Provide(colony, body, ProvideKind.Warmth, "plaza").Ok);
        Assert.True(PlayerVerbs.Provide(colony, body, ProvideKind.Warmth, "shelter-under").Ok);
        Assert.True(colony.IsWarmed("shelter-under"));
    }

    [Fact]
    public void ADoorThatWouldNotOpenOpens()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 20;
        var stair = colony.Map["east-stair"];

        var result = PlayerVerbs.Provide(colony, body, ProvideKind.Door, "east-stair");

        Assert.True(result.Ok);
        Assert.False(stair.IsBlocked);
        Assert.Equal(20 - ProvisionRules.DoorCost, colony.LoomCharge);

        // Nobody saw who did it; everyone heard. And the floor above still waits for the colony.
        Assert.All(colony.Living, c => Assert.Contains(c.Memory.Episodic, m => m.Verb == "heard that the way into"));
        Assert.Contains("ninth-floor", colony.ForbiddenPlaces);
    }

    [Fact]
    public void ADoorCostsWhatItCosts()
    {
        var (colony, body) = Start();
        colony.LoomCharge = ProvisionRules.DoorCost - 1;

        Assert.False(PlayerVerbs.Provide(colony, body, ProvideKind.Door, "east-stair").Ok);
        Assert.True(colony.Map["east-stair"].IsBlocked);
    }

    /// <summary>Opening a door the colony voted to leave shut would make its vote decorative.</summary>
    [Fact]
    public void TheColonysNoIsRespected()
    {
        var (colony, body) = Start();
        colony.LoomCharge = 50;
        colony.ForbiddenPlaces.Add("ninth-floor");

        var result = PlayerVerbs.Provide(colony, body, ProvideKind.Door, "ninth-floor");

        Assert.False(result.Ok);
        Assert.Contains("colony's to decide", result.Refusal);
    }

    [Fact]
    public void ARevelationIsShownOnceAndCanChangeWhatTheColonyCanDo()
    {
        var (colony, body) = Start();
        var shown = colony.Colonists[0];
        var rate = colony.LoomChargePerDay;

        var result = PlayerVerbs.Reveal(colony, body, shown, "charge-lines");

        Assert.True(result.Ok);
        Assert.Equal(rate + 0.8, colony.LoomChargePerDay, precision: 6);
        Assert.Equal(shown.Id, colony.Revealed["charge-lines"]);

        var memory = shown.Memory.FindEpisodic(result.Memory!.Id)!;
        Assert.Equal("was shown something of the makers':", memory.Verb);
        Assert.Contains(colony.Feed.Recent(5), e => e.Layer == EventLayer.Player);

        // Irreversible, and once: nobody else can be shown it again.
        Assert.False(PlayerVerbs.Reveal(colony, body, colony.Colonists[1], "charge-lines").Ok);
        Assert.Equal(rate + 0.8, colony.LoomChargePerDay, precision: 6);
    }

    [Fact]
    public void TheStairwellSealHalvesWhatIsLeftToClear()
    {
        var (colony, body) = Start();
        var stair = colony.Map["east-stair"];
        stair.ClearingDone = 60;

        PlayerVerbs.Reveal(colony, body, colony.Colonists[0], "stair-seal");

        Assert.Equal(160, stair.ClearingRequired, precision: 6);
    }

    [Fact]
    public void ThereIsNothingElseToShow()
    {
        var (colony, body) = Start();

        Assert.False(PlayerVerbs.Reveal(colony, body, colony.Colonists[0], "the-answer").Ok);
    }
}
