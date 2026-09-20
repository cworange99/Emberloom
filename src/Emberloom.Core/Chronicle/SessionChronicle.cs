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
    private int _openDayDropped;

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
        int Dropped);

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
            _openDayDropped++;
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
            _openDayDropped));

        _openDay.Clear();
        _openDayDropped = 0;
        LastDayClosed = day;
        HasNews = true;

        // Older days keep their tally and lose their lines. A day costs about a kilobyte, and the
        // whole file is rewritten every time it is amended: unbounded, an Overnight run that went
        // for weeks would end up rewriting megabytes every few real seconds.
        for (var i = 0; i < _days.Count - ChronicleRules.DetailDays; i++)
        {
            if (_days[i].Lines.Count > 0)
            {
                _days[i] = _days[i] with { Lines = [], Dropped = 0 };
            }
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

        text.AppendLine($"# A colony begun {StartedAt.LocalDateTime:d MMMM yyyy 'at' HH:mm}, from seed {Seed}");
        text.AppendLine();
        text.AppendLine(stamp.Ended
            ? $"_Ended {stamp.At.LocalDateTime:d MMM HH:mm} on colony day {colony.Day:F1}, after {Spell(stamp.Elapsed)} — {Spell(colony.Day)} of colony time._"
            : $"_Standing at {stamp.At.LocalDateTime:d MMM HH:mm}, colony day {colony.Day:F1}, {Spell(stamp.Elapsed)} in, running at {stamp.Speed}._");
        text.AppendLine();

        var born = colony.Colonists.Count(c => c.BornOnDay > 0);
        var died = colony.Colonists.Count(c => !c.IsAlive);

        var generations = LifeRules.Generations(colony);

        text.AppendLine($"- **{living.Count} living** · {Colonists} to begin with · {born} born or printed since · {died} died · {generations} generation{(generations == 1 ? string.Empty : "s")}");
        text.AppendLine($"- The Loom holds {colony.LoomCharge:F0} charge{(colony.UpkeepPerDay > 0 ? $", spending {colony.UpkeepPerDay:F2} a day on what you provide" : string.Empty)} · {colony.ArtifactsRecovered} pieces of the makers' recovered");
        text.AppendLine($"- {colony.Lexicon.Count} words of their own · the story {(colony.Myth is null ? "still untold" : $"told {Times(colony.Myth.Version)}")} · {colony.ForkHistory.Count} forks decided{(colony.OpenFork is not null ? ", one open now" : string.Empty)}");

        if (stamp.Served > 0 || stamp.Dropped > 0 || stamp.Fallbacks > 0)
        {
            text.AppendLine($"- The mind: {stamp.Served} calls served, {stamp.Dropped} refused for something worth more, {stamp.Fallbacks} fell back on a template");
        }
        else
        {
            text.AppendLine("- No mind was reached this session — the body ran on its own.");
        }

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
                $"- **{colonist.DisplayName}** — {lineage}, {colonist.AgeInDays(colony.Day):F0} of about {colonist.LifespanDays:F0} days{(colonist.IsElder ? ", slowing" : string.Empty)}. {colonist.Personality}");
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
                text.AppendLine($"- **{colonist.DisplayName}** — day {colonist.DiedOnDay:F0}, {colonist.AgeInDays(colonist.DiedOnDay):F0} days old, core {fate}.");
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
                text.AppendLine(day.Dropped > 0 || day.Number <= LastDayClosed - ChronicleRules.DetailDays
                    ? "_Kept as a tally only; the detail of this day has aged out._"
                    : "_Nothing worth writing down._");
                continue;
            }

            foreach (var line in day.Lines)
            {
                text.AppendLine($"- {line}");
            }

            if (day.Dropped > 0)
            {
                text.AppendLine($"- _and {day.Dropped} more things happened._");
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
    bool Ended = false);

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
    /// <summary>How many recent days keep their lines. Older days keep their tally. See CloseDay.</summary>
    public const int DetailDays = 60;

    /// <summary>Lines kept for one day. Beyond this a day is counted rather than quoted.</summary>
    public const int LinesPerDay = 12;

    public const int DeadShown = 20;

    public const int BeliefsShown = 12;

    /// <summary>
    /// The longest a session can lose. A colony day is an hour of real time at 1×, and a crash that
    /// threw away an hour of a record kept against crashes would be a poor joke, so the file is also
    /// amended on this interval whenever anything notable has happened since the last one.
    /// </summary>
    public static readonly TimeSpan AtMostEvery = TimeSpan.FromMinutes(2);
}
