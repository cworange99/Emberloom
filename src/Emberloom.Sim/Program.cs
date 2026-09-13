using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;

// M1 — the body, with no mind at all.
//
// Runs the colony headless and prints the event feed. Zero model calls: this is the fallback the
// whole architecture leans on, and it has to be watchable on its own.

var days = 30;
var colonists = 8;
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
        case "--diagnose": diagnose = true; break;
        case "--sweep":
            Sweep(days, colonists, seed);
            return 0;
        default:
            Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
            return 1;
    }
}

var random = new Random(seed);
var colony = ColonyFactory.Create(colonists, random);
var body = new BodySimulation(colony, random);

var completions = new Dictionary<ActionKind, int>();
var decisions = 0;

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
    var state = place.IsBlocked ? $"blocked {place.ClearingProgress:P0}" : place.IsExplored ? "open" : "unvisited";
    Console.WriteLine($"  {place.Name,-22} {state,-16} salvage {place.SalvageRemaining}");
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

        var random = new Random(seed);
        var colony = ColonyFactory.Create(colonists, random);
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
