using Emberloom.Mind.Calls;
using Emberloom.Mind.Governor;
using Xunit;

namespace Emberloom.Tests;

public class GovernorTests
{
    private static MindRequest Request(
        CallType call,
        CallPriority priority,
        string colonist = "c01",
        bool undroppable = false,
        double weight = 0,
        Func<CancellationToken, Task<MindCallOutcome>>? work = null) => new()
    {
        Call = call,
        Priority = priority,
        ColonistId = colonist,
        Undroppable = undroppable,
        Weight = weight,
        Work = work ?? (_ => Task.FromResult(new MindCallOutcome { PromptTokens = 10, CompletionTokens = 10 }))
    };

    [Fact]
    public async Task HighestPriorityIsServedFirst()
    {
        var order = new List<string>();
        var done = new SemaphoreSlim(0);

        await using var governor = new MindGovernor(tokensPerMinute: 0);

        Func<string, Func<CancellationToken, Task<MindCallOutcome>>> record = label => _ =>
        {
            lock (order) { order.Add(label); }
            done.Release();
            return Task.FromResult(new MindCallOutcome { PromptTokens = 1, CompletionTokens = 1 });
        };

        // Queued worst-first so ordering cannot be an accident of insertion.
        governor.Submit(Request(CallType.IdleThought, CallPriority.IdleThought, "c01", work: record("idle")));
        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c02", work: record("reflection")));
        governor.Submit(Request(CallType.Deliberation, CallPriority.Deliberation, "c03", undroppable: true, work: record("deliberation")));

        governor.Start();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(10)), "the governor stopped serving");
        }

        Assert.Equal(["deliberation", "reflection", "idle"], order);
    }

    [Fact]
    public async Task OnlyOneCallRunsAtATime()
    {
        var concurrent = 0;
        var peak = 0;
        var done = new SemaphoreSlim(0);

        await using var governor = new MindGovernor(tokensPerMinute: 0);

        Func<CancellationToken, Task<MindCallOutcome>> work = async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref concurrent);
            done.Release();
            return new MindCallOutcome { PromptTokens = 1, CompletionTokens = 1 };
        };

        for (var i = 0; i < 5; i++)
        {
            governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, $"c{i:00}", work: work));
        }

        governor.Start();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        // A second in-flight request halves both, on GPU and CPU alike.
        Assert.Equal(1, peak);
    }

    [Fact]
    public void TheQueueRefusesRatherThanGrowingForever()
    {
        using var governor = new MindGovernor(tokensPerMinute: 0, maxQueueDepth: 4);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, $"c{i:00}")));
        }

        // Nothing in the queue is cheaper than this, so it is refused outright.
        Assert.False(governor.Submit(Request(CallType.IdleThought, CallPriority.IdleThought, "c99")));
        Assert.Equal(4, governor.Stats().QueueDepth);
    }

    [Fact]
    public void SomethingMoreImportantEvictsSomethingLess()
    {
        using var governor = new MindGovernor(tokensPerMinute: 0, maxQueueDepth: 3);

        for (var i = 0; i < 3; i++)
        {
            governor.Submit(Request(CallType.IdleThought, CallPriority.IdleThought, $"c{i:00}"));
        }

        Assert.True(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c99")));
        Assert.Equal(3, governor.Stats().QueueDepth);
        Assert.True(governor.Stats().Dropped > 0);
    }

    /// <summary>
    /// A colony that fast-forwards past its own turning points is broken, and no amount of graceful
    /// degradation fixes that.
    /// </summary>
    [Fact]
    public void UndroppableWorkIsNeverEvicted()
    {
        using var governor = new MindGovernor(tokensPerMinute: 0, maxQueueDepth: 2);

        governor.Submit(Request(CallType.Kindling, CallPriority.Kindling, "c01", undroppable: true));
        governor.Submit(Request(CallType.Deliberation, CallPriority.Deliberation, "c02", undroppable: true));

        // The queue is full of undroppable work, so ordinary work is refused rather than displacing it.
        Assert.False(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c03")));
        Assert.Equal(2, governor.Stats().QueueDepth);
    }

    [Fact]
    public void OneColonistCannotMonopoliseTheColonysThinking()
    {
        using var governor = new MindGovernor(
            tokensPerMinute: 0,
            maxQueueDepth: 32,
            perColonistCooldown: TimeSpan.FromMinutes(5));

        Assert.True(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "dramatic")));

        governor.Start();
        Thread.Sleep(200);

        // Same colonist, still inside its cooldown.
        Assert.False(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "dramatic")));

        // Somebody else is unaffected.
        Assert.True(governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "somebody-else")));
    }

    [Fact]
    public void ACooldownDoesNotHoldUpATurningPoint()
    {
        using var governor = new MindGovernor(
            tokensPerMinute: 0,
            perColonistCooldown: TimeSpan.FromMinutes(5));

        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "sela"));
        governor.Start();
        Thread.Sleep(200);

        Assert.True(
            governor.Submit(Request(CallType.Deliberation, CallPriority.Deliberation, "sela", undroppable: true)),
            "an undroppable call must ignore the per-colonist cooldown");
    }

    /// <summary>
    /// The one place time bends for function: when the budget cannot serve an undroppable call,
    /// the simulation slows itself rather than skipping it.
    /// </summary>
    [Fact]
    public async Task AnUndroppableCallOverBudgetAsksTheSimulationToSlowDown()
    {
        var throttled = new TaskCompletionSource<bool>();

        await using var governor = new MindGovernor(tokensPerMinute: 50);
        governor.ThrottleRequested += on => { if (on) throttled.TrySetResult(true); };

        // Spend the whole minute's budget in one call.
        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c01",
            work: _ => Task.FromResult(new MindCallOutcome { PromptTokens = 40, CompletionTokens = 40 })));

        governor.Start();
        await Task.Delay(300);

        governor.Submit(Request(CallType.Deliberation, CallPriority.Deliberation, "c02", undroppable: true));

        var slowed = await Task.WhenAny(throttled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(throttled.Task, slowed);
    }

    [Fact]
    public async Task DroppableWorkOverBudgetWaitsWithoutSlowingTime()
    {
        var throttleRaised = false;

        await using var governor = new MindGovernor(tokensPerMinute: 50);
        governor.ThrottleRequested += on => { if (on) throttleRaised = true; };

        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c01",
            work: _ => Task.FromResult(new MindCallOutcome { PromptTokens = 40, CompletionTokens = 40 })));

        governor.Start();
        await Task.Delay(300);

        governor.Submit(Request(CallType.IdleThought, CallPriority.IdleThought, "c02"));
        await Task.Delay(500);

        Assert.False(throttleRaised, "ordinary work being over budget must not bend time");
    }

    [Fact]
    public async Task SpendingIsAccountedAgainstTheCeiling()
    {
        var done = new SemaphoreSlim(0);

        await using var governor = new MindGovernor(tokensPerMinute: 10_000);

        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c01", work: _ =>
        {
            done.Release();
            return Task.FromResult(new MindCallOutcome { PromptTokens = 300, CompletionTokens = 120 });
        }));

        governor.Start();
        Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(150);

        var stats = governor.Stats();
        Assert.Equal(420, stats.TokensInLastMinute);
        Assert.Equal(1, stats.Served);
    }

    [Fact]
    public async Task AFailedCallDoesNotStopTheGovernorServingTheQueue()
    {
        var served = new SemaphoreSlim(0);

        await using var governor = new MindGovernor(tokensPerMinute: 0);

        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c01",
            work: _ => throw new InvalidOperationException("the mind fell over")));

        governor.Submit(Request(CallType.Reflection, CallPriority.Reflection, "c02", work: _ =>
        {
            served.Release();
            return Task.FromResult(new MindCallOutcome { PromptTokens = 1, CompletionTokens = 1 });
        }));

        governor.Start();

        Assert.True(await served.WaitAsync(TimeSpan.FromSeconds(10)),
            "one failed call must not take the queue down with it");
    }

    /// <summary>
    /// Budgets are keyed by session from the start, so a hosted deployment gets per-tenant fairness
    /// without a rewrite. With one session this is simply a budget.
    /// </summary>
    [Fact]
    public void BudgetsAreHeldPerSession()
    {
        using var governor = new MindGovernor(tokensPerMinute: 1000);

        governor.BudgetFor("colony-a").Record(600);

        Assert.Equal(600, governor.BudgetFor("colony-a").TokensInLastMinute);
        Assert.Equal(0, governor.BudgetFor("colony-b").TokensInLastMinute);
        Assert.True(governor.BudgetFor("colony-b").HasHeadroom);
    }

    [Fact]
    public void PriorityAndDroppabilityFollowTheDesign()
    {
        Assert.Equal(CallPriority.Deliberation, MindRequest.PriorityFor(CallType.Deliberation));
        Assert.Equal(CallPriority.Kindling, MindRequest.PriorityFor(CallType.Kindling));
        Assert.Equal(CallPriority.IdleThought, MindRequest.PriorityFor(CallType.IdleThought));

        // Anything the player is looking at outranks everything else.
        Assert.Equal(CallPriority.PlayerFocus, MindRequest.PriorityFor(CallType.Dialogue, playerIsWatching: true));

        // What the player watches jumps everything skippable, but never the undroppables.
        Assert.True(CallPriority.PlayerFocus > CallPriority.Reflection);
        Assert.True(CallPriority.Kindling > CallPriority.PlayerFocus);
        Assert.True(CallPriority.Deliberation > CallPriority.PlayerFocus);

        Assert.True(MindRequest.IsUndroppable(CallType.Kindling));
        Assert.True(MindRequest.IsUndroppable(CallType.Deliberation));
        Assert.False(MindRequest.IsUndroppable(CallType.Reflection));
        Assert.False(MindRequest.IsUndroppable(CallType.IdleThought));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

public class BudgetLedgerTests
{
    [Fact]
    public void ACeilingOfZeroMeansNoCeiling()
    {
        var ledger = new BudgetLedger(0);
        ledger.Record(1_000_000);

        Assert.True(ledger.HasHeadroom);
    }

    [Fact]
    public void HeadroomRunsOutOnceTheCeilingIsSpent()
    {
        var ledger = new BudgetLedger(100);

        Assert.True(ledger.HasHeadroom);
        ledger.Record(120);
        Assert.False(ledger.HasHeadroom);
    }
}
