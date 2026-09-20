using Emberloom.Core.Chronicle;
using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Events;
using Emberloom.Core.Life;
using Emberloom.Core.Simulation;

namespace Emberloom.Tests;

/// <summary>
/// The account a session leaves behind. Its whole reason for existing is a server that stops
/// without warning, so these are mostly about what survives that.
/// </summary>
public class ChronicleTests
{
    private static (Colony Colony, BodySimulation Body, SessionChronicle Chronicle) Start(int seed = 7, int colonists = 4)
    {
        var random = new Random(seed);
        var colony = ColonyFactory.Create(colonists, random);
        var body = new BodySimulation(colony, random);
        var chronicle = new SessionChronicle(seed, colonists, DateTimeOffset.Now);

        colony.Feed.Appended += chronicle.Notice;
        return (colony, body, chronicle);
    }

    private static ChronicleStamp Stamp(bool ended = false) =>
        new(DateTimeOffset.Now, TimeSpan.FromMinutes(90), "1×", 120, 8, 2, ended);

    [Fact]
    public void ARecordExistsBeforeAnythingHasHappened()
    {
        var (colony, _, chronicle) = Start();

        var text = chronicle.Compose(colony, Stamp());

        // The first minute of a session is exactly when nobody has set anything up, and is as
        // likely a moment to lose the process as any other.
        Assert.Contains("seed 7", text);
        Assert.Contains("4 living", text);
        Assert.Contains("_No story yet._", text);
        Assert.Contains("## Who is alive", text);
    }

    [Fact]
    public void ADayKeepsItsNotableLinesAndDropsTheRest()
    {
        var (colony, _, chronicle) = Start();

        colony.Log(EventLayer.World, "The Loom drew breath.", notable: true);
        colony.Log(EventLayer.Body, "Someone walked to the plaza.");
        colony.Log(EventLayer.Mind, "Someone came to a belief.", notable: true);

        chronicle.CloseDay(colony, 1);

        var day = Assert.Single(chronicle.Days);
        Assert.Equal(1, day.Number);
        Assert.Equal(["The Loom drew breath.", "Someone came to a belief."], day.Lines);
    }

    [Fact]
    public void ADayCountsWhoWasBornAndWhoDiedInIt()
    {
        var (colony, _, chronicle) = Start();

        colony.Tick = Colony.TicksPerDay * 3;
        Assert.NotNull(ColonyFactory.Print(colony, new Random(1), chargeIsFree: true));

        var dying = colony.Living.First();
        dying.IsAlive = false;
        dying.DiedOnDay = colony.Day;

        chronicle.CloseDay(colony, 3);

        var day = Assert.Single(chronicle.Days);
        Assert.Equal(1, day.Born);
        Assert.Equal(1, day.Died);

        // Counted off the colony rather than accumulated, so day 2 claims neither of them.
        chronicle.CloseDay(colony, 4);
        Assert.Equal(0, chronicle.Days[1].Born);
        Assert.Equal(0, chronicle.Days[1].Died);
    }

    [Fact]
    public void TheDayInProgressIsWrittenDownTooSoAStopMidDayLosesNothing()
    {
        var (colony, _, chronicle) = Start();

        chronicle.CloseDay(colony, 1);
        colony.Log(EventLayer.Player, "You told C-01 that the light was yours to give.", notable: true);

        var text = chronicle.Compose(colony, Stamp(ended: true));

        Assert.Contains("You told C-01 that the light was yours to give.", text);
        Assert.Contains("where it stopped", text);
    }

    [Fact]
    public void OldDaysKeepTheirTallyAndLoseTheirLines()
    {
        var (colony, _, chronicle) = Start();

        for (var day = 1; day <= ChronicleRules.DetailDays + 5; day++)
        {
            colony.Log(EventLayer.World, $"Something happened on day {day}.", notable: true);
            chronicle.CloseDay(colony, day);
        }

        // Every colony day rewrites the whole file. Unbounded detail would have an Overnight run
        // writing megabytes every few real seconds by the second week.
        Assert.Empty(chronicle.Days[0].Lines);
        Assert.Equal(1, chronicle.Days[0].Number);
        Assert.NotEmpty(chronicle.Days[^1].Lines);

        var text = chronicle.Compose(colony, Stamp());
        Assert.Contains("### Day 1 —", text);
        Assert.DoesNotContain("Something happened on day 1.", text);
        Assert.Contains($"Something happened on day {ChronicleRules.DetailDays + 5}.", text);
    }

    [Fact]
    public void ADayWithTooMuchInItIsCountedRatherThanQuoted()
    {
        var (colony, _, chronicle) = Start();

        for (var i = 0; i < ChronicleRules.LinesPerDay + 4; i++)
        {
            colony.Log(EventLayer.Body, $"Line {i}.", notable: true);
        }

        chronicle.CloseDay(colony, 1);

        var day = Assert.Single(chronicle.Days);
        Assert.Equal(ChronicleRules.LinesPerDay, day.Lines.Count);
        Assert.Equal(4, day.Dropped);
        Assert.Contains("and 4 more things happened", chronicle.Compose(colony, Stamp()));
    }

    [Fact]
    public void TheStoryAndTheColonysWordsAreInIt()
    {
        var (colony, _, chronicle) = Start();

        colony.MythHistory.Add(new MythTelling
        {
            Story = "The makers left the Loom running, and went out into the gray.",
            Version = 2,
            ToldOnDay = 12,
            Evidence = 4
        });

        var text = chronicle.Compose(colony, Stamp());

        Assert.Contains("The makers left the Loom running", text);
        Assert.Contains("Told 2 times", text);
    }

    [Fact]
    public void ReadingTheRecordDoesNotStopItBeingWritten()
    {
        var (colony, _, chronicle) = Start();

        colony.Log(EventLayer.World, "A core was read aloud.", notable: true);
        Assert.True(chronicle.HasNews);

        // Composing is what the API does when someone asks to read it. Only a successful save says
        // the file is up to date — otherwise a player reading along would suppress the next write.
        chronicle.Compose(colony, Stamp());
        Assert.True(chronicle.HasNews);

        chronicle.MarkWritten();
        Assert.False(chronicle.HasNews);
    }

    [Fact]
    public void ARunThatIsKilledMidWriteLeavesTheLastWholeRecord()
    {
        var folder = Path.Combine(Path.GetTempPath(), "emberloom-chronicle-" + Guid.NewGuid().ToString("N"));

        try
        {
            var path = ChronicleFile.Save(folder, "colony.md", "# first\n");
            Assert.Equal("# first\n", File.ReadAllText(path));

            ChronicleFile.Save(folder, "colony.md", "# second\n");
            Assert.Equal("# second\n", File.ReadAllText(path));

            // The scratch file is moved over the real one rather than written through it, so a
            // process killed mid-write cannot leave a half-finished account.
            Assert.Empty(Directory.GetFiles(folder, "*.writing"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void GenerationsCountTheDeepestLineOfDescent()
    {
        var (colony, _, _) = Start();

        Assert.Equal(1, LifeRules.Generations(colony));

        var parents = colony.Living.Take(2).ToList();
        var child = ColonyFactory.Pair(colony, parents[0], parents[1], new Random(2));
        Assert.Equal(2, LifeRules.Generations(colony));

        // A third generation, so the count is the depth of the line rather than "somebody has parents".
        ColonyFactory.Pair(colony, child, parents[0], new Random(3));
        Assert.Equal(3, LifeRules.Generations(colony));
    }

    [Fact]
    public void ARecordIsWrittenByTheBodyAloneSoAColonyWithNoMindStillLeavesOne()
    {
        var (colony, body, chronicle) = Start();

        for (var tick = 0; tick < Colony.TicksPerDay * 2; tick++)
        {
            body.Tick();
        }

        chronicle.CloseDay(colony, 1);
        chronicle.CloseDay(colony, 2);

        var text = chronicle.Compose(colony, new ChronicleStamp(DateTimeOffset.Now, TimeSpan.FromHours(2), "1×", 0, 0, 0));

        Assert.Contains("No mind is configured", text);
        Assert.Contains("### Day 2", text);

        // A mind that is there but has not finished its first call is a third thing, and saying
        // the colony had no mind would be a lie told at the very moment someone first looks.
        var waiting = chronicle.Compose(colony,
            new ChronicleStamp(DateTimeOffset.Now, TimeSpan.Zero, "1×", 0, 0, 0, Ended: false, MindConfigured: true));

        Assert.Contains("nothing served yet", waiting);
    }
}
