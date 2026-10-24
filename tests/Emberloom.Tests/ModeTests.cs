using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M16 — three modes, one code path. A mode is a *strength*, not a branch: Simulation sets it to
/// zero, so a research run is provably this same binary doing the same work and multiplying by one.
/// <para>
/// The milestone's riskiest assumption was that a multiplicative term reads as influence rather
/// than as an order that sometimes fails confusingly. Measured over 600 days, the fraction of ticks
/// the asked-for action wins rises smoothly from 32% to 65% across the whole range of reach — a
/// curve, not a cliff. But a colonist under real pressure still obeyed 77% of the time at the far
/// end, which would have made the term an order; that promise is now structural rather than tuned.
/// </para>
/// </summary>
public class ModeTests
{
    private static (Colony Colony, BodySimulation Body) Run(
        int days,
        ColonyMode mode = ColonyMode.Hybrid,
        int seed = 4,
        int colonists = 4)
    {
        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed, mode);
        var body = new BodySimulation(colony, random);

        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        return (colony, body);
    }

    [Fact]
    public void AModeIsChosenAtTheStartAndCannotBeChangedAfter()
    {
        var (simulation, _) = Run(1, ColonyMode.Simulation);
        var (directed, _) = Run(1, ColonyMode.Directed);

        Assert.Equal(ColonyMode.Simulation, simulation.Mode);
        Assert.Equal(ColonyMode.Directed, directed.Mode);

        // Init-only on Colony: there is no setter to call, which is the point. A run that could
        // change mode halfway is not evidence of anything.
        var setter = typeof(Colony).GetProperty(nameof(Colony.Mode))!.SetMethod!;
        Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), m => m.Name == "IsExternalInit");
    }

    [Fact]
    public void NothingReachesIntoASimulation()
    {
        var (colony, body) = Run(3, ColonyMode.Simulation);
        var colonist = colony.Living.First();

        Assert.False(PlayerVerbs.Speak(colony, body, colonist, "hello").Ok);
        Assert.False(PlayerVerbs.Print(colony, body, new Random(1), null).Ok);
        Assert.False(PlayerVerbs.Provide(colony, body, ProvideKind.Light, colonist.PlaceId).Ok);
        Assert.False(PlayerVerbs.Ask(colony, ActionKind.Tend, null, null, 1.0).Ok);
        Assert.Empty(colony.Intents);
    }

    /// <summary>
    /// And the term is still *evaluated* in Simulation — it multiplies by one. A mode that skipped
    /// the code would make a research run a different binary, which is exactly what the one-code-
    /// path rule exists to prevent.
    /// </summary>
    [Fact]
    public void TheTermIsInertRatherThanAbsent()
    {
        var (colony, _) = Run(3, ColonyMode.Simulation);
        var colonist = colony.Living.First();

        Assert.Equal(0, IntentRules.Reach(ColonyMode.Simulation));

        var intent = new Intent(ActionKind.Tend, null, null, 1.0, colony.Day + 10);
        Assert.Equal(0, IntentRules.Pull(intent, colony, colonist, ActionKind.Tend, colonist.PlaceId));
    }

    [Fact]
    public void AskingMakesSomethingMoreAttractiveWithoutMakingItHappen()
    {
        var (colony, _) = Run(3, ColonyMode.Directed);
        var colonist = colony.Living.First();
        var loom = colony.Map.OfKind(PlaceKind.Loom).First();

        var scorer = new UtilityScorer(new ColonyRandom(1));
        var before = scorer.Score(colonist, colony).First(s => s.Definition.Kind == ActionKind.Tend).Score;

        Assert.True(PlayerVerbs.Ask(colony, ActionKind.Tend, loom.Id, null, 1.0).Ok);

        var after = scorer.Score(colonist, colony).First(s => s.Definition.Kind == ActionKind.Tend).Score;

        Assert.True(after > before * 1.5, $"asking should weigh plainly ({before:F3} → {after:F3})");
    }

    /// <summary>
    /// The promise the whole mode rests on, and the one the sweep caught being broken: somebody in
    /// real need cannot hear you at all. Structural rather than tuned, so it holds at every reach
    /// including ones nobody has measured.
    /// </summary>
    [Fact]
    public void SomebodyWhoBadlyNeedsSomethingElseCannotHearYou()
    {
        var (colony, _) = Run(3, ColonyMode.Directed);
        var desperate = colony.Living.First();
        var intent = new Intent(ActionKind.Tend, null, null, 1.0, colony.Day + 10);

        foreach (var drive in DriveTable.All)
        {
            desperate.Drives.Levels[drive] = 0.05;
        }

        var comfortable = IntentRules.Pull(intent, colony, desperate, ActionKind.Tend, desperate.PlaceId);
        Assert.True(comfortable > 0.5, "a colonist with nothing pressing hears it perfectly well");

        desperate.Drives.Levels[Drive.Sustenance] = 1.0;
        desperate.Weights.Vector[Drive.Sustenance] = 1.4;

        Assert.Equal(0, IntentRules.Pull(intent, colony, desperate, ActionKind.Tend, desperate.PlaceId));
    }

    /// <summary>
    /// In Hybrid the colony's own decisions outrank the player's, which is the same rule that
    /// already refuses a Print verb while a Print vote is open. In Directed they do not.
    /// </summary>
    [Fact]
    public void ACouncilDecidingSomethingIsNotACouncilToBeLeanedOnAboutIt()
    {
        foreach (var mode in new[] { ColonyMode.Hybrid, ColonyMode.Directed })
        {
            var (colony, _) = Run(4, mode);
            var colonist = colony.Living.First();
            var site = colony.Map.Places.First(p => p.Kind == PlaceKind.Site);

            var intent = new Intent(ActionKind.Build, site.Id, null, 1.0, colony.Day + 10);
            Assert.True(IntentRules.Pull(intent, colony, colonist, ActionKind.Build, site.Id) > 0);

            colony.OpenFork = new Fork
            {
                Id = "arguing",
                Kind = ForkKind.Build,
                Question = "Do we raise it?",
                Proposal = "We raise it.",
                Stakes = "There is ground there.",
                SubjectPlaceId = site.Id,
                PlaceId = colonist.PlaceId,
                OpenedOnDay = colony.Day,
                Participants = [colonist.Id]
            };

            var heard = IntentRules.Pull(intent, colony, colonist, ActionKind.Build, site.Id);

            if (mode == ColonyMode.Hybrid)
            {
                Assert.Equal(0, heard);
            }
            else
            {
                Assert.True(heard > 0, "Directed means what it says");
            }
        }
    }

    [Fact]
    public void TheSameRequestTwiceIsOneRequestRestated()
    {
        var (colony, _) = Run(3, ColonyMode.Directed);

        PlayerVerbs.Ask(colony, ActionKind.Tend, null, null, 1.0);
        PlayerVerbs.Ask(colony, ActionKind.Tend, null, null, 1.0);

        Assert.Single(colony.Intents);
    }

    [Fact]
    public void TheColonyStopsHearingARequestAfterAWhile()
    {
        var (colony, body) = Run(3, ColonyMode.Directed);
        PlayerVerbs.Ask(colony, ActionKind.Tend, null, null, 1.0);

        for (var tick = 0; tick < (IntentRules.LastsDays + 1) * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.Empty(colony.Intents);
        Assert.Contains(colony.Feed.Events, e => e.Text.Contains("stopped hearing"));
    }

    [Fact]
    public void OnlySoManyRequestsStandAtOnce()
    {
        var (colony, _) = Run(3, ColonyMode.Directed);

        foreach (var kind in new[]
                 {
                     ActionKind.Tend, ActionKind.Forage, ActionKind.Rest,
                     ActionKind.Talk, ActionKind.Build, ActionKind.Salvage
                 })
        {
            PlayerVerbs.Ask(colony, kind, null, null, 1.0);
        }

        Assert.Equal(IntentRules.AtMostStanding, colony.Intents.Count);
        Assert.DoesNotContain(colony.Intents, i => i.Verb == ActionKind.Tend);
    }

    [Fact]
    public void WhatWasAskedSurvivesASave()
    {
        var (colony, body) = Run(4, ColonyMode.Directed);
        PlayerVerbs.Ask(colony, ActionKind.Tend, null, null, 0.7);

        var (loaded, _) = Emberloom.Core.Persistence.ColonySnapshot.Capture(colony, body).Restore();

        Assert.Equal(ColonyMode.Directed, loaded.Mode);
        Assert.Single(loaded.Intents);
        Assert.Equal(0.7, loaded.Intents[0].Strength, 6);
    }
}
