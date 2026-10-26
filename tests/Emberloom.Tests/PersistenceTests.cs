using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Memory;
using Emberloom.Core.Persistence;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M11 — a colony outlives the process it was started in.
/// <para>
/// The bar is not "it loads". It is that a loaded colony is the one that was saved: same people,
/// same memories, same ties, same half-finished buildings, and from there the same future. These
/// tests are the acceptance criteria, and they were written before the writer was.
/// </para>
/// </summary>
public class PersistenceTests
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

    /// <summary>Everything about a colony that a reader would notice, as one comparable string.</summary>
    private static string Describe(Colony colony) => string.Join("\n", new[]
        {
            $"tick {colony.Tick} charge {colony.LoomCharge:F4} salvage {colony.ArtifactsRecovered} seed {colony.Seed} mode {colony.Mode}",
            $"forks {colony.ForkHistory.Count} open {colony.OpenFork?.Id ?? "-"} cores {colony.Cores.Count} words {colony.Lexicon.Count} tellings {colony.MythHistory.Count}",
            $"declined {string.Join(",", colony.Declined.OrderBy(d => d.Key).Select(d => $"{d.Key}:{d.Value:F2}"))}"
        }
        .Concat(colony.Colonists.OrderBy(c => c.Id).Select(c =>
            $"{c.Id} {c.DisplayName} {c.Epithet ?? "-"} alive={c.IsAlive} age={c.BornOnDay:F3}/{c.LifespanDays:F3} " +
            $"at={c.PlaceId} doing={c.Current?.Definition.Kind.ToString() ?? "-"}+{c.Current?.TicksElapsed ?? 0} " +
            $"drives={c.Drives.Levels} mem={c.Memory.Episodic.Count}/{c.Memory.Beliefs.Count} calls={c.ModelCalls}"))
        .Concat(colony.Map.Places.OrderBy(p => p.Id).Select(p =>
            $"{p.Id} {p.Kind} explored={p.IsExplored} salvage={p.SalvageRemaining} clear={p.ClearingDone:F2} build={p.BuildDone:F2} approved={p.IsApproved}"))
        .Concat(colony.Relationships.All.OrderBy(r => r.FromId).ThenBy(r => r.ToId).Select(r =>
            $"{r.FromId}->{r.ToId} {r.Affinity:F4}/{r.Trust:F4}/{r.Respect:F4} shared={r.SharedMemories}")));

    [Fact]
    public void ASavedColonyComesBackAsTheColonyThatWasSaved()
    {
        var (colony, body) = Run(days: 200);
        var before = Describe(colony);

        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();

        Assert.Equal(before, Describe(loaded));
    }

    [Fact]
    public void ALoadedColonyHasTheSameFutureAsTheOneItWasSavedFrom()
    {
        var (colony, body) = Run(days: 200);
        var (loaded, loadedBody) = ColonySnapshot.Capture(colony, body).Restore();

        // The real test. Equal state is easy to fake; equal state that goes on to live the same
        // next fifty days means the save caught everything the simulation actually reads.
        for (var tick = 0; tick < 50 * Colony.TicksPerDay; tick++)
        {
            body.Tick();
            loadedBody.Tick();
        }

        Assert.Equal(Describe(colony), Describe(loaded));
    }

    [Fact]
    public void WhatEachMindRemembersSurvivesExactly()
    {
        var (colony, body) = Run(days: 120);
        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();

        foreach (var original in colony.Colonists)
        {
            var same = loaded.Find(original.Id)!;

            Assert.Equal(
                original.Memory.Episodic.Select(m => $"{m.Id}|{m.Verb}|{m.Object}|{m.Valence:F4}|{m.Intensity:F4}|{m.Repeats}|{m.Consolidated}"),
                same.Memory.Episodic.Select(m => $"{m.Id}|{m.Verb}|{m.Object}|{m.Valence:F4}|{m.Intensity:F4}|{m.Repeats}|{m.Consolidated}"));

            // A memory folded into another on load would be a different mind wearing the same name.
            Assert.Equal(original.Memory.Episodic.Count, same.Memory.Episodic.Count);
        }
    }

    [Fact]
    public void NewMemoriesAfterALoadDoNotReuseOldIds()
    {
        var (colony, body) = Run(days: 60);
        var highest = colony.Colonists.SelectMany(c => c.Memory.Episodic).Select(m => m.Id).ToHashSet();

        var (loaded, loadedBody) = ColonySnapshot.Capture(colony, body).Restore();

        for (var tick = 0; tick < 5 * Colony.TicksPerDay; tick++)
        {
            loadedBody.Tick();
        }

        // Beliefs cite memory ids, so a counter that restarted would silently rewire what every
        // belief in the colony claims to be evidence for. Dates cannot find the new ones — folding
        // a repeat bumps the day on the memory it folds into — so the property to assert is that
        // no id in the colony is used twice, and that the counter is past everything already there.
        var ids = loaded.Colonists.SelectMany(c => c.Memory.Episodic).Select(m => m.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(ids.Count > highest.Count, "the loaded colony should have gone on to remember more");
        Assert.True(loadedBody.NextMemoryId >= body.NextMemoryId);
    }

    [Fact]
    public void BeliefsKeepTheirEmbeddings()
    {
        var (colony, body) = Run(days: 30);
        var thinker = colony.Colonists[0];

        thinker.Memory.Add(new Belief
        {
            Id = "b-1",
            OwnerId = thinker.Id,
            FormedOnDay = colony.Day,
            Claim = "The Loom is owed something.",
            SourceMemoryIds = ["m-1"],
            Confidence = 0.8,
            Embedding = [0.25f, -0.5f, 0.75f]
        });

        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();
        var same = loaded.Find(thinker.Id)!.Memory.Beliefs.Single(b => b.Id == "b-1");

        // Every embedding cost a call. Dropping them would make a restored colony quietly worse at
        // noticing it already believes something, with no sign that anything had been lost.
        Assert.Equal([0.25f, -0.5f, 0.75f], same.Embedding);
        Assert.Equal("The Loom is owed something.", same.Claim);
    }

    [Fact]
    public void AColonyMidArgumentComesBackMidArgument()
    {
        var (colony, body) = Run(days: 30);

        // Thirty days in, a site is usually already going up — which stops another Build fork —
        // and the Loom is still short of a print. Give it something to argue about.
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 1;
        var fork = colony.OpenFork ?? ForkRules.Detect(colony);
        Assert.NotNull(fork);
        colony.OpenFork = fork;

        if (!fork!.Votes.ContainsKey(fork.Participants[0]))
        {
            fork.Cast(fork.Participants[0], new Vote(Stance.For, 0.7, "Raise it.", "because", WasFallback: false));
        }

        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();
        var same = loaded.OpenFork;

        Assert.NotNull(same);
        Assert.Equal(fork.Id, same!.Id);
        Assert.Equal(fork.Question, same.Question);
        Assert.Equal(fork.Participants, same.Participants);
        Assert.True(same.IsOpen);
        Assert.Equal(fork.Count(Stance.For), same.Count(Stance.For));
        Assert.Equal(fork.Votes[fork.Participants[0]].Line, same.Votes[fork.Participants[0]].Line);
    }

    [Fact]
    public void AHalfBuiltCityComesBackHalfBuilt()
    {
        var (colony, body) = Run(days: 400);

        var site = colony.Map.Places.First(p => p.Kind == PlaceKind.Site);
        site.IsApproved = true;
        site.BuildDone = site.BuildRequired / 2;

        var (loaded, _) = ColonySnapshot.Capture(colony, body).Restore();
        var same = loaded.Map.Find(site.Id)!;

        Assert.True(same.IsApproved);
        Assert.Equal(site.BuildDone, same.BuildDone);
        Assert.True(same.IsUnderConstruction);

        // And anything already raised is still standing, as what it was raised as.
        foreach (var raised in colony.Map.Places.Where(p => p.Kind is PlaceKind.Hall or PlaceKind.Granary or PlaceKind.Dwelling))
        {
            Assert.Equal(raised.Kind, loaded.Map.Find(raised.Id)!.Kind);
        }
    }

    /// <summary>
    /// **A colony that cannot be written down is a colony that is lost.**
    /// <para>
    /// The overnight run of 25 October 2026 failed to save 1,337 times in a row — once per colony
    /// day, from day 38 to day 1,986 — on <c>UNIQUE constraint failed: belief.colony, belief.id</c>,
    /// and nobody noticed because nothing in the game misbehaved. It simply could not be kept.
    /// </para>
    /// <para>
    /// The cause was an id taken from <c>Beliefs.Count</c>. A reflection drafts several beliefs and
    /// numbers them from the count; one of them is a near-duplicate and is merged into the row it
    /// duplicates rather than added. Fewer land than were numbered, the count falls behind, and the
    /// next reflection re-issues a live id. This reproduces that sequence exactly.
    /// </para>
    /// </summary>
    [Fact]
    public void MergingADuplicateBeliefDoesNotMakeTheNextIdACollision()
    {
        var (colony, _) = Run(days: 2);
        var colonist = colony.Living.First();

        // A reflection that drafts two and lands two.
        var first = NewBelief(colonist);
        var second = NewBelief(colonist);
        colonist.Memory.Add(first);
        colonist.Memory.Add(second);

        // A reflection that drafts two and lands one, because the other is a near-duplicate of a
        // belief already held and is merged into it in place.
        var third = NewBelief(colonist);
        var duplicate = NewBelief(colonist);
        colonist.Memory.Add(third);
        colonist.Memory.Replace(first, first with { Claim = "merged", Confidence = 0.9 });

        // And one more, which is where the old scheme handed out an id that was already in use.
        colonist.Memory.Add(NewBelief(colonist));

        var ids = colonist.Memory.Beliefs.Select(b => b.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.NotEqual(third.Id, duplicate.Id);
    }

    /// <summary>
    /// And a save of such a colony goes through, which is the thing that actually failed. The
    /// beliefs are written through the real path rather than hand-built, so the test fails if the
    /// id scheme regresses anywhere between here and the table.
    /// </summary>
    [Fact]
    public void AColonyWhoseBeliefsHaveBeenMergedCanStillBeWrittenDown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (colony, body) = Run(days: 40);

            foreach (var colonist in colony.Living)
            {
                for (var round = 0; round < 12; round++)
                {
                    var kept = NewBelief(colonist);
                    var merged = NewBelief(colonist);
                    colonist.Memory.Add(kept);

                    // Every other round, the second draft is merged away instead of added — which
                    // is what makes the count stop tracking the number of ids handed out.
                    if (round % 2 == 0)
                    {
                        colonist.Memory.Replace(kept, kept with { Claim = "merged " + round });
                    }
                    else
                    {
                        colonist.Memory.Add(merged);
                    }
                }
            }

            var store = new ColonyStore(Path.Combine(folder, "colonies.db"));
            store.Save("merged", colony, body);

            var loaded = store.Load("merged");
            Assert.NotNull(loaded);

            foreach (var colonist in loaded!.Value.Colony.Colonists)
            {
                var ids = colonist.Memory.Beliefs.Select(b => b.Id).ToList();
                Assert.Equal(ids.Count, ids.Distinct().Count());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A loaded colonist must not start counting from one again. None of the three sequences is in
    /// the snapshot — adding them would not help a colony saved before they existed — so they are
    /// read back off the ids already held.
    /// </summary>
    [Fact]
    public void ALoadedColonistCarriesOnFromTheIdsItAlreadyHandedOut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (colony, body) = Run(days: 40);
            var writer = colony.Living.First();

            for (var i = 0; i < 5; i++)
            {
                writer.Memory.Add(NewBelief(writer));
            }

            var before = writer.Memory.Beliefs.Select(b => b.Id).ToList();

            var store = new ColonyStore(Path.Combine(folder, "colonies.db"));
            store.Save("resumed", colony, body);
            var loaded = store.Load("resumed")!.Value.Colony;

            var same = loaded.Find(writer.Id)!;
            var next = same.NextBeliefId();

            Assert.DoesNotContain(next, before);
            Assert.DoesNotContain(same.NextCallId(), same.Trace.Select(t => t.Id));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>One belief, numbered the way the mind numbers them.</summary>
    private static Belief NewBelief(Colonist colonist) => new()
    {
        Id = colonist.NextBeliefId(),
        OwnerId = colonist.Id,
        FormedOnDay = 1,
        Claim = "something held",
        Confidence = 0.6,
        SourceMemoryIds = [],
    };

    [Fact]
    public void AColonyWrittenToDiskComesBackOffDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (colony, body) = Run(days: 150);
            var before = Describe(colony);

            // Written by one store and read by another, each opening the file fresh: the thing
            // this milestone exists for is a colony outliving the process, and two objects passing
            // a snapshot between them in memory would not prove that.
            new ColonyStore(Path.Combine(folder, "colonies.db")).Save("overnight", colony, body);
            var loaded = new ColonyStore(Path.Combine(folder, "colonies.db")).Load("overnight");

            Assert.NotNull(loaded);
            Assert.Equal(before, Describe(loaded!.Value.Colony));

            var shelf = Assert.Single(new ColonyStore(Path.Combine(folder, "colonies.db")).List());
            Assert.Equal("overnight", shelf.Name);
            Assert.Equal(colony.Living.Count(), shelf.Living);
            Assert.Equal(colony.Colonists.Count, shelf.EverLived);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SavingTwiceUnderOneNameKeepsTheLaterColony()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new ColonyStore(Path.Combine(folder, "colonies.db"));
            var (colony, body) = Run(days: 40);
            store.Save("autosave", colony, body);

            for (var tick = 0; tick < 40 * Colony.TicksPerDay; tick++)
            {
                body.Tick();
            }

            store.Save("autosave", colony, body);

            // An autosave writes over itself every colony day for the life of a run; a store that
            // accumulated rows instead would grow without bound and load the wrong one.
            var shelf = Assert.Single(store.List());
            Assert.Equal(colony.Day, shelf.Day, 3);
            Assert.Equal(Describe(colony), Describe(store.Load("autosave")!.Value.Colony));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AskingForAColonyThatWasNeverSavedIsNotAnError()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-store-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new ColonyStore(Path.Combine(folder, "colonies.db"));

            Assert.Null(store.Load("nothing here"));
            Assert.Empty(store.List());
            Assert.False(store.Delete("nothing here"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ASaveFromALaterBuildIsRefusedRatherThanMisread()
    {
        var (colony, body) = Run(days: 10);
        var snapshot = ColonySnapshot.Capture(colony, body) with { Version = ColonySnapshot.CurrentVersion + 1 };

        // Loading a format this build does not understand would produce a colony subtly unlike the
        // one that was saved, which is worse than refusing.
        Assert.Throws<NotSupportedException>(() => snapshot.Restore());
    }
}
