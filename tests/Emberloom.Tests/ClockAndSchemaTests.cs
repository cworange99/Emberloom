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
    public void TicksAccumulateAcrossPartialFrames()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(GameSpeed.Normal);

        // 2 ticks per real second at 1x, so ten 100ms frames is 2 ticks, not 0.
        var total = 0;
        for (var i = 0; i < 10; i++)
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
    [InlineData(CallType.Reflection, 200)]
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
