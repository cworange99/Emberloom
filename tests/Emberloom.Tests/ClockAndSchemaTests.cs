using Emberloom.Core.Colonists;
using Emberloom.Core.Relationships;
using Emberloom.Core.Simulation;
using Emberloom.Bench;
using Emberloom.Mind.Calls;
using Xunit;

namespace Emberloom.Tests;

public class ClockTests
{
    /// <summary>
    /// The Contemplation readout. Thinking-per-colony-day must fall as the player accelerates —
    /// nothing enforces it, it is arithmetic, and this is the test that says so out loud.
    /// </summary>
    [Fact]
    public void ThinkingPerColonyDayFallsAsSpeedRises()
    {
        var clock = new SimulationClock();
        const double measuredCallsPerMinute = 4.8; // M0, modest profile

        clock.SetSpeed(GameSpeed.Normal);
        var atNormal = clock.CallsPerColonyDay(measuredCallsPerMinute);

        clock.SetSpeed(GameSpeed.Faster);
        var atTenX = clock.CallsPerColonyDay(measuredCallsPerMinute);

        clock.SetSpeed(GameSpeed.Fastest);
        var atSixtyX = clock.CallsPerColonyDay(measuredCallsPerMinute);

        Assert.True(atNormal > atTenX);
        Assert.True(atTenX > atSixtyX);

        // And the fall is proportional to the speed multiplier, not merely monotonic.
        Assert.Equal(atNormal / 10, atTenX, precision: 6);
    }

    [Fact]
    public void PausedTimeCostsNoThinking()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Paused);

        Assert.Equal(0, clock.TicksFor(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, clock.ColonyDaysPerRealMinute);
    }

    /// <summary>
    /// Pausing while the governor has the game throttled is the player's call, and releasing the
    /// throttle later must not overrule it by putting back the speed the throttle saved.
    /// </summary>
    [Fact]
    public void APauseDuringAThrottleOutlastsTheRelease()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Faster);
        clock.ThrottleForThought();
        Assert.Equal(GameSpeed.Normal, clock.Speed);

        clock.SetSpeed(GameSpeed.Paused);
        clock.ReleaseThrottle();

        Assert.Equal(GameSpeed.Paused, clock.Speed);
        Assert.False(clock.ThrottledByGovernor);
    }

    /// <summary>
    /// The same for choosing 1x during a throttle, which is the speed the throttle is already
    /// holding. That used to be ignored as "no change", leaving the throttle in place to restore
    /// 10x when it released.
    /// </summary>
    [Fact]
    public void ChoosingOneTimesDuringAThrottleIsKept()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Faster);
        clock.ThrottleForThought();

        clock.SetSpeed(GameSpeed.Normal);
        clock.ReleaseThrottle();

        Assert.Equal(GameSpeed.Normal, clock.Speed);
        Assert.False(clock.ThrottledByGovernor);
    }

    /// <summary>
    /// Contemplation-per-colony-day is unbounded while paused, and CallsPerColonyDay says so with
    /// PositiveInfinity. That is the honest answer in C# and an unserialisable one in JSON —
    /// System.Text.Json throws on it — so the pause button used to return a 500 and freeze the
    /// client on its last state. The DTO carries null instead, and this is the regression guard.
    /// </summary>
    [Fact]
    public void PausedClockStillSerialises()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Paused);
        Assert.True(double.IsPositiveInfinity(clock.CallsPerColonyDay(4.8)));

        var json = System.Text.Json.JsonSerializer.Serialize(Emberloom.Api.ClockDto.From(clock, 4.8));
        Assert.Contains("\"CallsPerColonyDay\":null", json);

        // And the running case still carries a number, so the guard did not just blank the field.
        clock.SetSpeed(GameSpeed.Normal);
        var running = Emberloom.Api.ClockDto.From(clock, 4.8);
        Assert.NotNull(running.CallsPerColonyDay);
        Assert.True(running.CallsPerColonyDay > 0);
        System.Text.Json.JsonSerializer.Serialize(running);
    }

    [Fact]
    public void GovernorCanSlowTheSimulationAndGiveTheSpeedBack()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Fastest);

        // A colony that fast-forwards past its own turning points is broken, so the sim slows
        // itself rather than dropping an undroppable call.
        clock.ThrottleForThought();
        Assert.Equal(GameSpeed.Normal, clock.Speed);
        Assert.True(clock.ThrottledByGovernor);

        clock.ReleaseThrottle();
        Assert.Equal(GameSpeed.Fastest, clock.Speed);
        Assert.False(clock.ThrottledByGovernor);
    }

    [Fact]
    public void AutoSlowDropsToNormalWhenSomethingIsBeingDecided()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Faster);

        clock.SlowForEvent();

        Assert.Equal(GameSpeed.Normal, clock.Speed);
    }

    [Fact]
    public void TheSpeedComesBackWhenTheColonyHasDecided()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Fastest);

        clock.SlowForEvent();
        Assert.Equal(GameSpeed.Normal, clock.Speed);
        Assert.True(clock.SlowedForEvent);

        // Without this the auto-slow was a one-way drop: the first fork of a long run pinned the
        // colony at 1x for the rest of it, and a run left at 60x was found crawling hours later.
        clock.ResumeAfterEvent();
        Assert.Equal(GameSpeed.Fastest, clock.Speed);
        Assert.False(clock.SlowedForEvent);
    }

    [Fact]
    public void OvernightIsNeverSlowedForADecision()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Overnight);

        // Overnight means nobody is watching. Slowing down so that somebody looks up is meaningless
        // at a speed defined by there being nobody there.
        clock.SlowForEvent();

        Assert.Equal(GameSpeed.Overnight, clock.Speed);
        Assert.False(clock.SlowedForEvent);
    }

    [Fact]
    public void APlayersSpeedDuringADecisionOutlastsTheResume()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Fastest);
        clock.SlowForEvent();

        clock.SetSpeed(GameSpeed.Fast);
        clock.ResumeAfterEvent();

        // Theirs wins, the same rule the governor's throttle follows.
        Assert.Equal(GameSpeed.Fast, clock.Speed);
        Assert.False(clock.SlowedForEvent);
    }

    [Fact]
    public void ChoosingOneTimesDuringADecisionIsKept()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Fastest);
        clock.SlowForEvent();

        // The auto-slow is already holding 1x, so this looks like no change at all — and used to
        // be treated as one, which let the resume put 60x back over the player's own choice.
        clock.SetSpeed(GameSpeed.Normal);
        clock.ResumeAfterEvent();

        Assert.Equal(GameSpeed.Normal, clock.Speed);
    }

    /// <summary>
    /// Both holds can be on at once, and the speed comes back only when both are off. Each used to
    /// guard itself with "the clock is already at 1x, so there is nothing to do", which is true of
    /// the speed and false of the hold.
    /// </summary>
    [Fact]
    public void AThrottleDuringADecisionDoesNotLoseTheSpeedBehindBoth()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Fastest);

        clock.SlowForEvent();
        clock.ThrottleForThought();

        Assert.True(clock.SlowedForEvent);
        Assert.True(clock.ThrottledByGovernor);
        Assert.Equal(GameSpeed.Normal, clock.Speed);

        clock.ResumeAfterEvent();
        Assert.Equal(GameSpeed.Normal, clock.Speed);

        clock.ReleaseThrottle();
        Assert.Equal(GameSpeed.Fastest, clock.Speed);
    }

    /// <summary>
    /// The other order, which is the one that was actually broken: a throttle lands first, the
    /// clock is already at 1x, and the fork that opens a moment later finds nothing to do. When the
    /// budget freed up the colony went back to 60x with the vote still open — which is how a vote
    /// Jeremy was watching ran for several colony-days with auto-slow switched on.
    /// </summary>
    [Fact]
    public void ADecisionDuringAThrottleStillHoldsTheSpeedDown()
    {
        var clock = new SimulationClock { AutoSlowOnEvent = true };
        clock.SetSpeed(GameSpeed.Fastest);

        clock.ThrottleForThought();
        clock.SlowForEvent();
        clock.ReleaseThrottle();

        Assert.True(clock.SlowedForEvent);
        Assert.Equal(GameSpeed.Normal, clock.Speed);

        clock.ResumeAfterEvent();
        Assert.Equal(GameSpeed.Fastest, clock.Speed);
    }

    [Fact]
    public void TicksAccumulateAcrossPartialFrames()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Normal);

        // A colony-day is an hour at 1x, so a tick is fifteen real seconds and no single 100 ms
        // frame is worth one. Thirty seconds of frames must still add up to two ticks, not 0.
        // One frame over, so floating-point summing cannot land at 1.9999 and floor to one tick.
        var frames = (int)Math.Ceiling(2 / SimulationClock.TicksPerRealSecondAt1X * 10) + 1;
        var total = 0;
        for (var i = 0; i < frames; i++)
        {
            total += clock.TicksFor(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(2, total);
    }
}

public class RelationshipTests
{
    [Fact]
    public void StageIsDerivedFromTheNumbersRatherThanStoredSeparately()
    {
        var ledger = new RelationshipLedger();
        var relationship = ledger.Get("a", "b");

        Assert.Equal(RelationshipStage.Stranger, relationship.Stage);

        relationship.Adjust(affinity: 0.7, trust: 0.7);
        for (var i = 0; i < 20; i++)
        {
            relationship.NoteSharedMemory();
        }

        Assert.Equal(RelationshipStage.Bonded, relationship.Stage);
    }

    [Fact]
    public void RelationshipsAreDirectional()
    {
        var ledger = new RelationshipLedger();
        ledger.Get("sela", "bram").Adjust(trust: 0.8);

        Assert.Equal(0.8, ledger.Get("sela", "bram").Trust, precision: 6);
        Assert.Equal(0.0, ledger.Get("bram", "sela").Trust, precision: 6);
    }
}

/// <summary>
/// M0 found that a schema-constrained call which hits its token cap truncates mid-document rather
/// than degrading gracefully. These lock in that the validator actually catches that.
/// </summary>
public class SchemaCheckTests
{
    [Fact]
    public void TruncatedJsonIsRejected()
    {
        var result = SchemaCheck.Validate(
            """{ "name": "The Still Pane", "rationale": "Because it never""",
            MindSchemas.Coinage());

        Assert.False(result.Ok);
    }

    [Fact]
    public void WellFormedCoinageIsAccepted()
    {
        var result = SchemaCheck.Validate(
            """{ "name": "The Still Pane", "rationale": "It kept showing a room nobody knew." }""",
            MindSchemas.Coinage());

        Assert.True(result.Ok, string.Join("; ", result.Problems));
    }

    [Fact]
    public void MissingRequiredFieldIsRejected()
    {
        var result = SchemaCheck.Validate("""{ "name": "The Still Pane" }""", MindSchemas.Coinage());

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("rationale"));
    }

    [Fact]
    public void OutOfRangeNumberIsRejected()
    {
        var result = SchemaCheck.Validate(
            """{ "stance": "for", "reasoning": "x", "line": "y", "conviction": 4.2 }""",
            MindSchemas.Deliberation());

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("above maximum"));
    }

    [Fact]
    public void ValueOutsideTheEnumIsRejected()
    {
        var result = SchemaCheck.Validate(
            """{ "stance": "maybe", "reasoning": "x", "line": "y", "conviction": 0.5 }""",
            MindSchemas.Deliberation());

        Assert.False(result.Ok);
    }

    [Fact]
    public void DialogueIsUnconstrainedButMustSaySomething()
    {
        Assert.True(SchemaCheck.Validate("Nine floors, and every one emptied on purpose.", null).Ok);
        Assert.False(SchemaCheck.Validate("   ", null).Ok);
    }
}

public class CallBudgetTests
{
    /// <summary>
    /// Every cap must sit above the longest completion M0 actually observed for that call type.
    /// A cap is not a budget hint — a schema call that hits it produces unparseable output.
    /// </summary>
    [Theory]
    [InlineData(CallType.Kindling, 276)]
    [InlineData(CallType.Reflection, 448)] // M0 saw 200; a live colony truncated at 448 after M4
    [InlineData(CallType.Dialogue, 55)]
    [InlineData(CallType.Deliberation, 91)]
    [InlineData(CallType.Coinage, 60)]
    [InlineData(CallType.IdleThought, 39)]
    public void CapsClearTheLongestCompletionMeasuredAtM0(CallType call, int longestObserved)
    {
        Assert.True(CallBudgets.For(call) > longestObserved,
            $"{call} cap {CallBudgets.For(call)} does not clear the {longestObserved} tokens seen at M0");
    }
}
