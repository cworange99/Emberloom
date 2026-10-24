using System.Text;
using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Life;

namespace Emberloom.Core.Chronicle;

/// <summary>
/// What a session of this colony amounted to, kept on disk while it runs.
/// <para>
/// Nothing in the colony persists — close the server and it is gone. That is tolerable for the
/// colony itself, which is meant to be mortal, but not for the account of it: the overnight run's
/// value was entirely in the file a recorder script happened to be writing beside it. This is that
/// recorder, brought inside the game, so every session leaves a record whether or not anyone
/// thought to set one up beforehand.
/// </para>
/// <para>
/// It is written by the body: plain counting and the colony's own words, no model call anywhere.
/// A colony running with no mind at all still writes its chronicle — it just has less to say in it.
/// </para>
/// </summary>
public sealed class SessionChronicle
{
    private readonly List<Day> _days = [];
    private readonly List<string> _openDay = [];
    private readonly Dictionary<EventLayer, int> _openDayDropped = [];

    public SessionChronicle(int seed, int colonists, DateTimeOffset startedAt)
    {
        Seed = seed;
        Colonists = colonists;
        StartedAt = startedAt;

        // Named for when it began and what it grew from, so a folder of these reads as a shelf and
        // two runs of the same seed never write over each other. To the second, because starting a
        // new colony from the client can happen twice inside a minute.
        FileName = $"colony-{startedAt.LocalDateTime:yyyy-MM-dd-HHmmss}-seed{seed}.md";
    }

    public int Seed { get; }

    public int Colonists { get; }

    public DateTimeOffset StartedAt { get; }

    public string FileName { get; }

    /// <summary>
    /// The last whole colony day folded into the record. Zero until the first one closes: the
    /// colony calls its first completed day "day 1", and the record has to agree with the feed.
    /// </summary>
    public int LastDayClosed { get; private set; }

    /// <summary>
    /// Something has happened that the file does not yet have. The host writes on this rather than
    /// on a timer alone, so a quiet colony is not rewritten every couple of minutes for nothing.
    /// </summary>
    public bool HasNews { get; private set; } = true;

    public IReadOnlyList<Day> Days => _days;

    /// <summary>
    /// One day of the record: what the colony was at the end of it, and the notable things in it.
    /// Kept as data rather than text so the file can be recomposed at any moment without replaying
    /// anything.
    /// </summary>
    public sealed record Day(
        int Number,
        int Living,
        int Born,
        int Died,
        int Words,
        int Tellings,
        IReadOnlyList<string> Lines,
        IReadOnlyDictionary<EventLayer, int> Dropped)
    {
        public int DroppedCount => Dropped.Values.Sum();

        /// <summary>
        /// What was lost, in words — M18. Both the overflow past a day's line budget and the lines
        /// an aged-out day gives up used to be thrown away as a bare number, which told a reader
        /// that something had happened and nothing about what. The layer is the one piece of
        /// structure every line carries, so it is the one thing that can be said about a line
        /// without having read it.
        /// </summary>
        public string DescribeDropped()
        {
            var parts = new List<string>();

            foreach (var (layer, count) in Dropped.OrderByDescending(d => d.Value))
            {
                if (count == 0)
                {
                    continue;
                }

                parts.Add(layer switch
                {
                    EventLayer.Body => $"{count} thing{Plural(count)} people did",
                    EventLayer.Mind => $"{count} thing{Plural(count)} people thought or said",
                    EventLayer.World => $"{count} thing{Plural(count)} that happened to the colony",
                    EventLayer.Player => $"{count} thing{Plural(count)} you did",
                    _ => $"{count} more"
                });
            }

            return string.Join(", ", parts);
        }

        private static string Plural(int count) => count == 1 ? string.Empty : "s";
    }

    /// <summary>
    /// Offered every line the colony logs. Only the notable ones are kept — the same ones the feed
    /// marks for a reader who is watching, which is the right filter for a reader who was not.
    /// </summary>
    public void Notice(ColonyEvent colonyEvent)
    {
        if (!colonyEvent.IsNotable)
        {
            return;
        }

        HasNews = true;

        // The day's own summary line is the tally this file already writes for every day.
        if (colonyEvent.Layer == EventLayer.World && colonyEvent.Text.StartsWith("Day ", StringComparison.Ordinal))
        {
            return;
        }

        if (_openDay.Count >= ChronicleRules.LinesPerDay)
        {
            _openDayDropped[colonyEvent.Layer] = _openDayDropped.GetValueOrDefault(colonyEvent.Layer) + 1;
            return;
        }

        _openDay.Add(colonyEvent.Quote is { Length: > 0 } quote
            ? $"{colonyEvent.Text} “{quote}”"
            : colonyEvent.Text);
    }

    /// <summary>
    /// Closes a colony day. Called once the colony has passed a day boundary; the counts are read
    /// off the colony rather than accumulated as it goes, so nothing can drift out of step with it.
    /// </summary>
    public void CloseDay(Colony colony, int day)
    {
        _days.Add(new Day(
            day,
            colony.Living.Count(),
            colony.Colonists.Count(c => c.BornOnDay > day - 1 && c.BornOnDay <= day),
            colony.Colonists.Count(c => c.DiedOnDay > day - 1 && c.DiedOnDay <= day),
            colony.Lexicon.Count,
            colony.Myth?.Version ?? 0,
            [.. _openDay],
            new Dictionary<EventLayer, int>(_openDayDropped)));

        _openDay.Clear();
        _openDayDropped.Clear();
        LastDayClosed = day;
        HasNews = true;

        KeepWithinBudget();
    }

    /// <summary>
    /// Thins the record back to <see cref="ChronicleRules.LinesKept"/> quoted lines, oldest first —
    /// M18. The whole file is rewritten every time it is amended, so something has to bound it.
    /// <para>
    /// A day gives up its lines in two steps rather than one. First it is thinned to the line it
    /// opened with and the rest become a tally of what was lost; only if the record is still over
    /// budget after every day has been thinned does the oldest give up its last line too. So a long
    /// run reads as a thinning rather than a cliff: five thousand days ago there is one line a day,
    /// last week there are twelve, and nowhere is there a stretch that simply says nothing happened
    /// when something did.
    /// </para>
    /// </summary>
    private void KeepWithinBudget()
    {
        var kept = _days.Sum(d => d.Lines.Count);

        for (var i = 0; i < _days.Count && kept > ChronicleRules.LinesKept; i++)
        {
            if (_days[i].Lines.Count <= ChronicleRules.LinesWhenThinned)
            {
                continue;
            }

            var thinned = _days[i].Lines.Take(ChronicleRules.LinesWhenThinned).ToList();
            var lost = _days[i].Lines.Count - thinned.Count;

            // What a thinned day gives up is the body's, not the world's: World lines are the
            // colony's turning points and they are few, so they are the ones that survive — but
            // nothing here has read the lines, so the honest thing is to count them as Body and
            // say so rather than guess at what each one was.
            var dropped = new Dictionary<EventLayer, int>(_days[i].Dropped);
            dropped[EventLayer.Body] = dropped.GetValueOrDefault(EventLayer.Body) + lost;

            _days[i] = _days[i] with { Lines = thinned, Dropped = dropped };
            kept -= lost;
        }

        // Still over: the oldest days give up their last line as well.
        for (var i = 0; i < _days.Count && kept > ChronicleRules.LinesKept; i++)
        {
            if (_days[i].Lines.Count == 0)
            {
                continue;
            }

            var dropped = new Dictionary<EventLayer, int>(_days[i].Dropped);
            dropped[EventLayer.Body] = dropped.GetValueOrDefault(EventLayer.Body) + _days[i].Lines.Count;

            kept -= _days[i].Lines.Count;
            _days[i] = _days[i] with { Lines = [], Dropped = dropped };
        }
    }

    /// <summary>
    /// The whole file, as it should stand right now. Composed from scratch every time: the standing
    /// figures at the top are always current, and a half-finished day shows what is in it so far.
    /// </summary>
    public string Compose(Colony colony, ChronicleStamp stamp)
    {
        var text = new StringBuilder();
        var living = colony.Living.ToList();

        text.AppendLine($"# Emberloom — a colony begun {StartedAt.LocalDateTime:d MMMM yyyy 'at' HH:mm}, from seed {Seed}");
        text.AppendLine();
        text.AppendLine(stamp.Ended
            ? $"_Ended {stamp.At.LocalDateTime:d MMM HH:mm} on colony day {colony.Day:F1}, after {Spell(stamp.Elapsed)} — {Spell(colony.Day)} of colony time._"
            : $"_Standing at {stamp.At.LocalDateTime:d MMM HH:mm}, colony day {colony.Day:F1}, {Spell(stamp.Elapsed)} in, running at {stamp.Speed}._");
        text.AppendLine();

        var born = colony.Colonists.Count(c => c.BornOnDay > 0);
        var died = colony.Colonists.Count(c => !c.IsAlive);

        var generations = LifeRules.Generations(colony);

        text.AppendLine($"- **{living.Count} living** · {Colonists} to begin with · {born} born or printed since · {died} died · {generations} generation{(generations == 1 ? string.Empty : "s")}");
        text.AppendLine($"- The stores hold {colony.LoomCharge:F0} of {colony.StoreCapacity:F0}"
                        + $" · work puts in {colony.ProducedPerDay:F2} a day, the living take {colony.DrawPerDay:F2}"
                        + (colony.UpkeepPerDay > 0 ? $", what you provide costs {colony.UpkeepPerDay:F2}" : string.Empty)
                        + $" · {(colony.StoresOverflowing ? "**spoiling for want of a granary**" : $"{colony.ChargeFlowPerDay:+0.00;-0.00;0.00} a day")}"
                        + $" · {colony.ArtifactsRecovered} pieces of the makers' recovered");
        text.AppendLine($"- {colony.Lexicon.Count} words of their own · the story {(colony.Myth is null ? "still untold" : $"told {Times(colony.Myth.Version)}")} · {colony.ForkHistory.Count} forks decided{(colony.OpenFork is not null ? ", one open now" : string.Empty)}");

        // How the colony is, as the colony would say it — M12. Reading a long run back, "nine
        // living" says whether they survived and nothing at all about whether it was worth it.
        if (living.Count > 0)
        {
            var well = living.Average(c => c.Wellbeing);
            var worst = living.OrderBy(c => c.Wellbeing).First();

            text.AppendLine($"- Taken together they are **{WellbeingRules.Describe(well)}**"
                            + $" · the hardest life here is {worst.DisplayName}'s, and they are {WellbeingRules.Describe(worst.Wellbeing)}"
                            + $" · {colony.Beds} beds for {living.Count} of them");

            // M15: despair shortens a life rather than ending one, so the only place it is visible
            // is here. A run where nobody lost anything says so, because that is also worth knowing.
            var shortened = colony.Colonists.Where(c => c.DaysLostToDespair > 1).ToList();
            text.AppendLine(shortened.Count == 0
                ? "- Nobody here has been failing long enough for it to cost them anything."
                : $"- **{shortened.Count} of them have lost time to it** — {shortened.Average(c => c.DaysLostToDespair):F0} days each on average, "
                  + $"{shortened.Max(c => c.DaysLostToDespair):F0} at the worst, off lives of about {LifeRules.BaseLifespanDays:F0}");
        }

        // Three states, not two: a colony with no mind configured is a supported way to play, and
        // must not read the same as one whose first call has simply not come back yet.
        text.AppendLine(stamp switch
        {
            { Served: 0, Dropped: 0, Fallbacks: 0, MindConfigured: false } =>
                "- No mind is configured — the body is running on its own, as it is built to.",
            { Served: 0, Dropped: 0, Fallbacks: 0 } =>
                "- The mind: nothing served yet.",
            _ =>
                $"- The mind: {stamp.Served} calls served, {stamp.Dropped} refused for something worth more, {stamp.Fallbacks} fell back on a template"
        });

        text.AppendLine();
        text.AppendLine("## The story, as it is told now");
        text.AppendLine();
        text.AppendLine(colony.Myth is { } myth
            ? myth.Story + $"\n\n_Told {Times(myth.Version)}, last on day {myth.ToldOnDay:F0}{(myth.ByHand ? ", written by you" : string.Empty)}._"
            : "_No story yet._");

        var against = living
            .SelectMany(c => c.Memory.Beliefs.Where(b => b.ContradictsColonyMyth).Select(b => (c, b)))
            .Take(ChronicleRules.BeliefsShown)
            .ToList();

        if (against.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Beliefs that go against it");
            text.AppendLine();
            foreach (var (who, belief) in against)
            {
                text.AppendLine($"- **{who.DisplayName}** — “{belief.Claim}”");
            }
        }

        var held = living
            .SelectMany(c => c.Memory.Beliefs.Select(b => (Who: c, Belief: b)))
            .OrderByDescending(x => x.Belief.Confidence)
            .ThenByDescending(x => x.Belief.RecallCount)
            .Take(ChronicleRules.BeliefsShown)
            .ToList();

        if (held.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## What they hold most firmly");
            text.AppendLine();
            foreach (var (who, belief) in held)
            {
                text.AppendLine($"- **{who.DisplayName}** — “{belief.Claim}”");
            }
        }

        text.AppendLine();
        text.AppendLine("## Who is alive");
        text.AppendLine();

        if (living.Count == 0)
        {
            text.AppendLine("_Nobody. The colony is over._");
        }

        foreach (var colonist in living.OrderBy(c => c.BornOnDay))
        {
            var parents = colonist.ParentIds
                .Select(id => colony.Find(id)?.DisplayName ?? id)
                .ToList();

            var lineage = parents.Count > 0
                ? $"child of {string.Join(" and ", parents)}"
                : colonist.Lineage.ToString().ToLowerInvariant();

            text.AppendLine(
                $"- **{colonist.DisplayName}**{(colonist.Epithet is { } known ? $", known for {known}," : string.Empty)} {lineage}, " +
                $"{colonist.AgeInDays(colony.Day):F0} of about {colonist.LifespanDays:F0} days{(colonist.IsElder ? ", slowing" : string.Empty)}. {colonist.Personality}");
        }

        var dead = colony.Colonists
            .Where(c => !c.IsAlive)
            .OrderByDescending(c => c.DiedOnDay)
            .ToList();

        if (dead.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Who has died");
            text.AppendLine();

            foreach (var colonist in dead.Take(ChronicleRules.DeadShown))
            {
                var core = colony.Cores.FirstOrDefault(c => c.ColonistId == colonist.Id);
                var fate = core is null ? "no core" : core.Fate.ToString().ToLowerInvariant();
                text.AppendLine(
                    $"- **{colonist.DisplayName}** — day {colonist.DiedOnDay:F0}, {colonist.AgeInDays(colonist.DiedOnDay):F0} days old, core {fate}." +
                    (colonist.Epithet is { } known ? $" Known for {known}." : string.Empty));
            }

            if (dead.Count > ChronicleRules.DeadShown)
            {
                text.AppendLine($"- _and {dead.Count - ChronicleRules.DeadShown} more, further back — they are in the days below._");
            }
        }

        text.AppendLine();
        text.AppendLine("## The days");

        foreach (var day in _days)
        {
            text.AppendLine();
            text.AppendLine($"### Day {day.Number} — {day.Living} living{Delta("born", day.Born)}{Delta("died", day.Died)}");
            text.AppendLine();

            if (day.Lines.Count == 0)
            {
                text.AppendLine(day.DroppedCount > 0
                    ? $"_Kept as a tally only — {day.DescribeDropped()}, none of them written down any more._"
                    : "_Nothing worth writing down._");
                continue;
            }

            foreach (var line in day.Lines)
            {
                text.AppendLine($"- {line}");
            }

            if (day.DroppedCount > 0)
            {
                text.AppendLine($"- _and {day.DescribeDropped()}._");
            }
        }

        // The day in progress, so a session that ends mid-day still leaves what was in it.
        if (_openDay.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"### Day {LastDayClosed + 1} — {(stamp.Ended ? "where it stopped" : "still going")}");
            text.AppendLine();
            foreach (var line in _openDay)
            {
                text.AppendLine($"- {line}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Said once the composed text is safely on disk — not by composing it. The same text is served
    /// to anyone who asks for it over the API, and a reader looking at the record must not be able
    /// to stop it being written.
    /// </summary>
    public void MarkWritten() => HasNews = false;

    private static string Delta(string word, int count) => count == 0 ? string.Empty : $", {count} {word}";

    private static string Times(int version) => version == 1 ? "once" : $"{version} times";

    private static string Spell(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min"
            : $"{(int)elapsed.TotalMinutes} min";

    private static string Spell(double days) =>
        days < 1 ? $"{days * 24:F0} hours" : $"{days:F0} days";
}

/// <summary>
/// What only the host knows when the chronicle is written: the wall clock, the speed, and what the
/// mind has been doing. Kept out of the colony so the record stays composable from the body alone.
/// </summary>
public sealed record ChronicleStamp(
    DateTimeOffset At,
    TimeSpan Elapsed,
    string Speed,
    int Served,
    int Dropped,
    int Fallbacks,
    bool Ended = false,
    bool MindConfigured = false);

/// <summary>
/// Putting the record on disk. Written whole to a neighbouring file and then moved over the real
/// one: the thing this feature exists for is a server that stops without warning, and a process
/// killed halfway through a write must not be able to leave a truncated account behind.
/// </summary>
public static class ChronicleFile
{
    public static string Save(string folder, string fileName, string text)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, fileName);
        var pending = path + ".writing";

        File.WriteAllText(pending, text);
        File.Move(pending, path, overwrite: true);

        return path;
    }
}

public static class ChronicleRules
{
    /// <summary>
    /// How many quoted lines the whole record keeps — M18, and a budget in lines rather than in
    /// colony-days on purpose.
    /// <para>
    /// It used to be the last 60 days, which is speed-blind in the wrong direction: a colony day is
    /// an hour at 1× and fifteen real seconds at Overnight, so the same constant meant two and a
    /// half months of watching or fifteen minutes of it. The 3,787-day run kept quotable lines for
    /// sixty days and bare tallies for three thousand seven hundred and twenty-seven.
    /// </para>
    /// <para>
    /// A line budget is speed-blind in the right direction instead. A quiet colony keeps its whole
    /// life; a busy one keeps the stretch that had things in it. And it bounds the file directly,
    /// which is what the old constant was really for — the whole thing is rewritten every time it
    /// is amended, so an unbounded record at Overnight would rewrite megabytes every few seconds.
    /// </para>
    /// </summary>
    public const int LinesKept = 900;

    /// <summary>Lines kept for one day. Beyond this a day is counted rather than quoted.</summary>
    public const int LinesPerDay = 12;

    /// <summary>What a day is thinned to when the record is over budget, before it loses its lines entirely.</summary>
    public const int LinesWhenThinned = 1;

    public const int DeadShown = 20;

    public const int BeliefsShown = 12;

    /// <summary>
    /// The longest a session can lose. A colony day is an hour of real time at 1×, and a crash that
    /// threw away an hour of a record kept against crashes would be a poor joke, so the file is also
    /// amended on this interval whenever anything notable has happened since the last one.
    /// </summary>
    public static readonly TimeSpan AtMostEvery = TimeSpan.FromMinutes(2);
}
