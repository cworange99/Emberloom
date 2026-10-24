using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;

// M1 — the body, with no mind at all.
//
// Runs the colony headless and prints the event feed. Zero model calls: this is the fallback the
// whole architecture leans on, and it has to be watchable on its own.
//
// M8 — and the instrument V2's balance is settled with. Every claim in the V2 scope about overwork,
// leisure and work-life balance is a claim about how a colonist's day divides, and until now nothing
// measured that: action *completions* were counted, which says nothing, because a Talk is 12 ticks
// and a Rest is 48. The time-use table below is the number those milestones are judged against.

var days = 30;
var colonists = 4;
var seed = 4;
var diagnose = false;

for (var i = 0; i < args.Length; i++)
{
    var next = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--days" or "-d" when next is not null: days = int.Parse(next); i++; break;
        case "--colonists" or "-c" when next is not null: colonists = int.Parse(next); i++; break;
        case "--seed" or "-s" when next is not null: seed = int.Parse(next); i++; break;
        case "--diagnose" or "--quiet" or "-q": diagnose = true; break;
        case "--sweep":
            Sweep(days, colonists, seed);
            return 0;
        default:
            Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
            return 1;
    }
}

var random = new ColonyRandom(seed);
var colony = ColonyFactory.Create(colonists, random, seed);
var body = new BodySimulation(colony, random);

var completions = new Dictionary<ActionKind, int>();
var decisions = 0;

// How the day actually divides. Counted in colonist-ticks, which is the only honest unit: an
// action's share of the day is its duration times how often it is chosen, and completions alone
// hide that. Travel and idle are counted too — the scope's leisure question is really "what is
// left after the necessary", and travel is a large part of the answer.
var timeUse = new Dictionary<string, long>();
var leisurePerColonist = new Dictionary<string, (long Free, long Total)>();
var population = new List<int>();
var driveTotals = DriveTable.All.ToDictionary(d => d, _ => 0.0);
var driveSamples = 0;

colony.Feed.Appended += e =>
{
    if (!diagnose)
    {
        Console.WriteLine($"d{e.Day,6:F2}  {Tag(e.Layer)}  {e.Text}");
    }
};

for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
{
    var before = colony.Living.Select(c => (c.Id, c.Current?.Definition.Kind, c.Current?.TicksElapsed)).ToList();

    body.Tick();

    foreach (var colonist in colony.Living)
    {
        // Travelling is checked first: a colonist walking to a forage patch has Forage as their
        // current action but is not foraging, and counting the walk as work would overstate work
        // by however far the city is wide.
        var bucket = colonist.Current is not { } action ? "idle"
            : colonist.PlaceId != action.Target.Id ? "travel"
            : action.Definition.Kind.ToString();

        timeUse[bucket] = timeUse.GetValueOrDefault(bucket) + 1;

        var (free, total) = leisurePerColonist.GetValueOrDefault(colonist.Id);
        leisurePerColonist[colonist.Id] = (free + (IsFree(bucket) ? 1 : 0), total + 1);
    }

    if (colony.Tick % Colony.TicksPerDay == 0)
    {
        population.Add(colony.Living.Count());

        // Drives sampled over the whole run, not read off the final tick: the last tick is one
        // arbitrary moment and says nothing about whether the colony was ever under pressure.
        if (colony.Living.Any())
        {
            foreach (var drive in DriveTable.All)
            {
                driveTotals[drive] += colony.Living.Average(c => c.Drives[drive]);
            }

            driveSamples++;
        }
    }

    foreach (var colonist in colony.Living)
    {
        var previous = before.FirstOrDefault(b => b.Id == colonist.Id);
        if (previous.Item2 is { } kind && previous.Item3 is { } elapsed)
        {
            // An action that vanished having reached its duration completed; one that vanished
            // early was abandoned.
            if (colonist.Current is null || colonist.Current.Definition.Kind != kind)
            {
                decisions++;
                if (elapsed + 1 >= DurationOf(kind))
                {
                    completions[kind] = completions.GetValueOrDefault(kind) + 1;
                }
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine($"=== after {days} colony-days, {colonists} colonists ===");
Console.WriteLine($"Loom charge      {colony.LoomCharge,8:F1}");
Console.WriteLine($"Artifacts        {colony.ArtifactsRecovered,8}");
Console.WriteLine($"Living           {colony.Living.Count(),8}");
Console.WriteLine($"Born             {colony.Colonists.Count(c => c.Lineage == LineageKind.Paired),8}");
Console.WriteLine($"Died             {colony.Colonists.Count(c => !c.IsAlive),8}");
Console.WriteLine($"Generations      {Generations(colony),8}");
Console.WriteLine($"Cores            {string.Join(", ", colony.Cores.GroupBy(c => c.Fate).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}"))}");
Console.WriteLine($"Feed lines       {colony.Feed.Events.Count,8}");
Console.WriteLine($"Action switches  {decisions,8}");
Console.WriteLine();

Console.WriteLine("Actions completed");
foreach (var (kind, count) in completions.OrderByDescending(p => p.Value))
{
    Console.WriteLine($"  {kind,-14} {count,6}");
}

var colonistTicks = timeUse.Values.Sum();
if (colonistTicks > 0)
{
    Console.WriteLine();
    Console.WriteLine($"=== how the day divides ({colonistTicks:N0} colonist-ticks) ===");

    foreach (var (label, kinds) in Classes())
    {
        var share = kinds.Sum(k => timeUse.GetValueOrDefault(k)) * 100.0 / colonistTicks;
        var parts = kinds.Count == 1
            ? string.Empty
            : "   " + string.Join("  ", kinds
                .Select(k => (Kind: k, Share: timeUse.GetValueOrDefault(k) * 100.0 / colonistTicks))
                .Where(x => x.Share > 0)
                .OrderByDescending(x => x.Share)
                .Select(x => $"{x.Kind.ToLowerInvariant()} {x.Share:F1}"));

        Console.WriteLine($"  {label,-12} {share,5:F1}%{parts}");
    }

    var discretionary = timeUse.Where(p => IsFree(p.Key)).Sum(p => p.Value) * 100.0 / colonistTicks;
    Console.WriteLine($"  {"",-12} ------");
    Console.WriteLine($"  {"free",-12} {discretionary,5:F1}%   social + leisure + idle — the room leisure would have to compete for");

    var shares = leisurePerColonist.Values
        .Where(v => v.Total >= Colony.TicksPerDay)
        .Select(v => v.Free * 100.0 / v.Total)
        .OrderBy(x => x)
        .ToList();

    if (shares.Count > 0)
    {
        Console.WriteLine(
            $"  {"per person",-12} min {shares[0],4:F1}  median {shares[shares.Count / 2],4:F1}  max {shares[^1],4:F1}" +
            $"   (over {shares.Count} who lived a day or more)");
    }
}

if (driveSamples > 0)
{
    Console.WriteLine();
    Console.WriteLine("Drive means over the whole run (not the last tick)");
    Console.Write("  ");
    foreach (var drive in DriveTable.All)
    {
        Console.Write($"{drive.ToString()[..4].ToLowerInvariant()} {driveTotals[drive] / driveSamples,5:F2}   ");
    }

    Console.WriteLine();
}

if (population.Count > 1)
{
    var firstPrint = colony.Colonists
        .Where(c => c.Lineage == LineageKind.Printed && c.BornOnDay > 0)
        .Select(c => (double?)c.BornOnDay)
        .DefaultIfEmpty(null)
        .Min();

    var firstBirth = colony.Colonists
        .Where(c => c.Lineage == LineageKind.Paired)
        .Select(c => (double?)c.BornOnDay)
        .DefaultIfEmpty(null)
        .Min();

    Console.WriteLine();
    Console.WriteLine($"Population   began {population[0]}, peak {population.Max()}, ended {population[^1]}");
    Console.WriteLine($"  first print {(firstPrint is null ? "never" : $"day {firstPrint:F0}"),-12} first birth {(firstBirth is null ? "never" : $"day {firstBirth:F0}")}");
    Console.Write("  curve ");

    // Ten samples across the run: enough to see a collapse or a plateau without a chart.
    for (var i = 0; i < 10; i++)
    {
        Console.Write($"{population[i * (population.Count - 1) / 9],4}");
    }

    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine($"{"Colonist",-10} {"Mood",-9} {"Where",-20} {"Doing",-14} {"Mem",5} {"Refl?",6}  drives");
foreach (var colonist in colony.Living)
{
    var where = colony.Map[colonist.PlaceId].Name;
    var doing = colonist.Current?.Definition.Kind.ToString() ?? "-";
    Console.WriteLine(
        $"{colonist.DisplayName,-10} {colonist.Mood(colony.Day),-9} {where,-20} {doing,-14} " +
        $"{colonist.Memory.Episodic.Count,5} {(colonist.Memory.WantsReflection(colony.Day) ? "yes" : "no"),6}  " +
        $"{colonist.Drives.Levels}");
}

Console.WriteLine();
Console.WriteLine("Places");
foreach (var place in colony.Map.Places.OrderBy(p => p.Level).ThenBy(p => p.Name))
{
    var state = place.IsBlocked ? $"blocked {place.ClearingProgress:P0}"
        : place.Kind != PlaceKind.Site ? place.IsExplored ? "open" : "unvisited"
        : !place.IsApproved ? "unbuilt ground"
        : $"building {place.BuildProgress:P0}";

    Console.WriteLine($"  {place.Name,-22} {place.Kind,-10} {state,-16} salvage {place.SalvageRemaining}");
}

return 0;

/// <summary>
/// Sweeps the one balance knob and reports where the drives actually settle.
/// <para>
/// A healthy colony sits in the middle of the range: drives near 0 mean every need is trivially
/// met, the scores flatten, and action selection collapses into noise; drives near 1 mean the
/// colony cannot keep up and does nothing but subsist. What the design wants is the physical
/// drives low and the top of the stack — Standing, Meaning, Wonder — carrying the pressure.
/// </para>
/// </summary>
static void Sweep(int days, int colonists, int seed)
{
    Console.WriteLine($"Relief scale sweep — {days} colony-days, {colonists} colonists, seed {seed}");
    Console.WriteLine();
    Console.WriteLine($"{"scale",6} {"sus",6} {"rest",6} {"safe",6} {"belg",6} {"stand",6} {"mean",6} {"wond",6}  {"spread",7} {"done",6}");

    foreach (var scale in new[] { 0.55, 1.0, 2.0, 3.0, 5.0, 8.0, 12.0 })
    {
        ActionCatalog.ReliefScale = scale;

        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed);
        var body = new BodySimulation(colony, random);
        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        var means = DriveTable.All
            .ToDictionary(d => d, d => colony.Living.Average(c => c.Drives[d]));

        // A colony where every drive reads the same number has no priorities. Spread is what
        // makes one colonist's dominant drive differ from another's.
        var spread = means.Values.Max() - means.Values.Min();

        Console.Write($"{scale,6:F2}");
        foreach (var drive in DriveTable.All)
        {
            Console.Write($" {means[drive],6:F2}");
        }

        Console.WriteLine($"  {spread,7:F2} {body.ActionsCompleted,6}");
    }

    ActionCatalog.ReliefScale = 3.0;
}

/// <summary>
/// How a day's ticks are grouped for reading. Wander is the colony's only leisure today — it is
/// explicitly the fallback when nothing else scores, and V2's whole social economy is an argument
/// that this line should be something a colonist chooses rather than settles for.
/// </summary>
static IReadOnlyList<(string Label, IReadOnlyList<string> Kinds)> Classes() =>
[
    ("work", new[] { "Forage", "Salvage", "Clear", "Build", "Tend" }),
    ("rest", new[] { "Rest" }),
    ("social", new[] { "Talk", "Teach" }),
    ("leisure", new[] { "Wander" }),
    ("civic", new[] { "Deliberate" }),
    ("travel", new[] { "travel" }),
    ("idle", new[] { "idle" })
];

/// <summary>Time not already claimed by work, rest, travel or a sitting council.</summary>
static bool IsFree(string bucket) => bucket is "Wander" or "Talk" or "Teach" or "idle";

/// <summary>The deepest line of descent: printed minds are generation one, their children two.</summary>
static int Generations(Colony colony)
{
    var depth = new Dictionary<string, int>();

    int Of(Colonist c) => depth.TryGetValue(c.Id, out var d)
        ? d
        : depth[c.Id] = c.ParentIds.Count == 0
            ? 1
            : 1 + c.ParentIds.Select(colony.Find).Where(p => p is not null).Select(p => Of(p!)).DefaultIfEmpty(0).Max();

    return colony.Colonists.Count == 0 ? 0 : colony.Colonists.Max(Of);
}

static string Tag(EventLayer layer) => layer switch
{
    EventLayer.Body => "body ",
    EventLayer.Mind => "mind ",
    EventLayer.Player => "you  ",
    _ => "world"
};

// Deliberate is not in All — the scorer offers it only to a sitting council — but a council member
// finishing a session still has to be counted.
static int DurationOf(ActionKind kind) =>
    ActionCatalog.All.Append(ActionCatalog.Deliberate).First(a => a.Kind == kind).DurationTicks;
