using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
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
        case "--prices":
            Prices(days, colonists, seed);
            return 0;
        case "--despair":
            Despair(days, colonists, seed);
            return 0;
        case "--intent":
            Intents(days, colonists, seed);
            return 0;
        case "--forks":
            Forks(days, colonists, seed);
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

// M12's whole question, as a number. Wellbeing is read-only at this milestone and the only thing
// worth knowing about it is whether it separates people: a figure everyone shares describes a
// colony with no priorities in it. Sampled per colonist per colony-day over their own life, so
// someone who died young is not averaged against a full lifespan.
var wellbeing = new Dictionary<string, (double Sum, int Days)>();

// The Loom, sampled daily. A single day is a bad sample of a stock: at M14 the last day of a run
// read 0.66 produced against a 2160-day average of something quite different, because whether the
// run ends at noon or at midnight decides how many people are asleep in it.
var chargeSamples = new List<(double Charge, double Produced, double Flow)>();

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

        foreach (var colonist in colony.Living)
        {
            var (sum, seen) = wellbeing.GetValueOrDefault(colonist.Id);
            wellbeing[colonist.Id] = (sum + colonist.Wellbeing, seen + 1);
        }

        chargeSamples.Add((colony.LoomCharge, colony.ProducedPerDay, colony.ChargeFlowPerDay));
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
Console.WriteLine($"Loom charge      {colony.LoomCharge,8:F1}   {Reserve(colony.DaysOfStores)} of stores, drawn {colony.DrawPerDay:F2}/day");
if (chargeSamples.Count > 0)
{
    var dry = chargeSamples.Count(s => s.Charge <= 0.01);
    Console.WriteLine(
        $"                           over the run: produced {chargeSamples.Average(s => s.Produced):F2}/day, "
        + $"net {chargeSamples.Average(s => s.Flow):+0.00;-0.00;0.00}, charge {chargeSamples.Min(s => s.Charge):F0}..{chargeSamples.Max(s => s.Charge):F0}, "
        + $"empty on {(double)dry / chargeSamples.Count:P0} of days");
}
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

var lives = wellbeing.Values.Where(w => w.Days >= 1).Select(w => w.Sum / w.Days).OrderBy(w => w).ToList();
if (lives.Count > 0)
{
    var mean = lives.Average();
    var spread = lives[^1] - lives[0];

    Console.WriteLine();
    Console.WriteLine($"Wellbeing over each life ({lives.Count} who lived a day or more)");
    Console.WriteLine($"  mean {mean:F3}   spread {spread:F3}   min {lives[0]:F3}   median {lives[lives.Count / 2]:F3}   max {lives[^1]:F3}");

    // A histogram rather than only the moments, because the thing that would kill the milestone is
    // a single spike, and a mean with a spread either side of it cannot show one.
    for (var band = 0; band < 10; band++)
    {
        var low = band / 10.0;
        var count = lives.Count(w => w >= low && (band == 9 ? w <= 1.0 : w < low + 0.1));
        Console.WriteLine($"  {low:F1}-{low + 0.1:F1}  {new string('#', count),-20} {count}");
    }
}

var lostDays = colony.Colonists.Select(c => c.DaysLostToDespair).Where(d => d > 0).ToList();
if (lostDays.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Despair took days off {lostDays.Count} of {colony.Colonists.Count} lives"
        + $" - {lostDays.Average():F0} on average, {lostDays.Max():F0} at worst, of about {LifeRules.BaseLifespanDays:F0}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Despair took nothing off anybody: nobody was below the line long enough to matter.");
}

var beds = colony.Beds;
Console.WriteLine($"Beds {beds} for {colony.Living.Count()} living"
    + $" - {colony.Map.Places.Count(p => p.Kind == PlaceKind.Dwelling)} built, {colony.Map.Places.Count(p => p.Kind == PlaceKind.Shelter)} found");

var earners = colony.Colonists.Where(c => c.AgeInDays(colony.Day) >= 1).ToList();
if (earners.Count > 0)
{
    var evenings = earners.Sum(c => c.EveningsBought);
    var paid = earners.Count(c => c.EveningsBought > 0);

    Console.WriteLine();
    Console.WriteLine($"Tokens (price {EconomyRules.GatheringPrice:F2} of a {EconomyRules.WagePerWorkDay:F2}-token working day)");
    Console.WriteLine($"  {evenings} evenings bought by {paid} of {earners.Count} who lived · purses now"
        + $" min {earners.Min(c => c.Tokens):F1} median {earners.OrderBy(c => c.Tokens).ElementAt(earners.Count / 2).Tokens:F1} max {earners.Max(c => c.Tokens):F1}");
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
Console.WriteLine($"{"Colonist",-10} {"Mood",-9} {"Well",-12} {"Where",-20} {"Doing",-14} {"Mem",5} {"Refl?",6}  drives");
foreach (var colonist in colony.Living)
{
    var where = colony.Map[colonist.PlaceId].Name;
    var doing = colonist.Current?.Definition.Kind.ToString() ?? "-";
    Console.WriteLine(
        $"{colonist.DisplayName,-10} {colonist.Mood(colony.Day),-9} {$"{colonist.Wellbeing:F2} " + WellbeingRules.Describe(colonist.Wellbeing),-12} {where,-20} {doing,-14} " +
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
/// <summary>
/// How far an intent should reach — M16's riskiest assumption, which is that a multiplicative term
/// reads as *influence* rather than as an order that sometimes fails confusingly.
/// <para>
/// What is wanted is a curve rather than a cliff: the fraction of ticks the asked-for action wins
/// should rise smoothly with the strength, and a colonist who badly needs something else should
/// override at every strength. The second column is the test that matters — if a starving colonist
/// does what they were told, the term is an order and the mode is a lie.
/// </para>
/// </summary>
static void Intents(int days, int colonists, int seed)
{
    Console.WriteLine($"Intent sweep — {days} colony-days, {colonists} founders, seed {seed}");
    Console.WriteLine("Asking everyone to Tend the Loom, forever, and counting who listens.");
    Console.WriteLine();
    Console.WriteLine($"{"reach",6} {"asked wins",11} {"when pressed",13} {"(of)",9} {"when easy",10} {"work",6} {"rest",6} {"well",6}");

    foreach (var reach in new[] { 0.0, 0.25, 0.5, 0.8, 1.5, 3.0, 6.0, 15.0 })
    {
        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed, ColonyMode.Directed);
        var body = new BodySimulation(colony, random);

        // One standing request, renewed for ever, so the measurement is of the term rather than of
        // the expiry. Strength 1 and the reach under test: the product is what the scorer sees.
        IntentRules.ReachOverride = reach;

        var asked = 0L;
        var total = 0L;
        var pressedAsked = 0L;
        var pressed = 0L;
        var easyAsked = 0L;
        var easy = 0L;
        var buckets = new Dictionary<string, long>();

        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            colony.Intents.Clear();
            colony.Intents.Add(new Intent(ActionKind.Tend, null, null, 1.0, colony.Day + 10));

            body.Tick();

            foreach (var colonist in colony.Living)
            {
                var doing = colonist.Current?.Definition.Kind;
                var dominant = colonist.Drives.Dominant(colonist.Weights);
                var urgency = colonist.Drives.Urgency(dominant, colonist.Weights[dominant]);

                total++;
                if (doing == ActionKind.Tend) { asked++; }

                // "Pressed" is the starving-colonist case the mode promise rests on.
                if (urgency > 0.6) { pressed++; if (doing == ActionKind.Tend) { pressedAsked++; } }
                else if (urgency < 0.2) { easy++; if (doing == ActionKind.Tend) { easyAsked++; } }

                var bucket = colonist.Current is not { } action ? "idle"
                    : colonist.PlaceId != action.Target.Id ? "travel"
                    : action.Definition.Kind.ToString();
                buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
            }
        }

        double Share(params string[] kinds) => total == 0 ? 0 : kinds.Sum(k => buckets.GetValueOrDefault(k)) / (double)total;

        Console.WriteLine(
            $"{reach,6:F2} {(total == 0 ? 0 : (double)asked / total),11:P1} "
            + $"{(pressed == 0 ? 0 : (double)pressedAsked / pressed),13:P1} {pressed,9:N0} "
            + $"{(easy == 0 ? 0 : (double)easyAsked / easy),10:P1} "
            + $"{Share("Forage", "Salvage", "Clear", "Build", "Tend"),6:P1} {Share("Rest"),6:P1} "
            + $"{colony.Living.Select(c => c.Wellbeing).DefaultIfEmpty(0).Average(),6:F3}");
    }

    IntentRules.ReachOverride = null;
}

/// <summary>
/// How hard despair should shorten a life — M15's one real feedback loop, and the only sweep in the
/// project looking for a cliff rather than a value.
/// <para>
/// A death aggravates Belonging by 0.35 in everyone close, the largest single aggravation in the
/// codebase. That lowers their wellbeing, which shortens their lives, which brings the next death
/// forward. Nothing damps it. So what is wanted here is not the "best" multiplier but the shape of
/// the edge: a slope means the coupling is safe to tune, and a cliff means it needs a floor, which
/// is cheap now and impossible once a number has been tuned against it.
/// </para>
/// </summary>
static void Despair(int days, int colonists, int seed)
{
    Console.WriteLine($"Despair sweep — {days} colony-days, {colonists} founders, seed {seed}");
    Console.WriteLine("Looking for the multiplier where a colony stops being able to replace itself.");
    Console.WriteLine();
    Console.WriteLine($"{"days/d",6} {"peak",6} {"ended",6} {"lived",6} {"died",5} {"mean life",10} {"lost",7} {"worst",7} {"well",6} {"extinct",8}");

    foreach (var scale in new[] { 0.0, 0.5, 1.0, 1.5, 3.0, 6.0, 12.0, 25.0 })
    {
        WellbeingRules.DespairDaysPerDay = scale;

        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed);
        var body = new BodySimulation(colony, random);

        var peak = colony.Living.Count();
        var extinctOn = -1.0;

        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();

            if (colony.Tick % Colony.TicksPerDay != 0)
            {
                continue;
            }

            peak = Math.Max(peak, colony.Living.Count());
            if (extinctOn < 0 && !colony.Living.Any())
            {
                extinctOn = colony.Day;
            }
        }

        var dead = colony.Colonists.Where(c => !c.IsAlive).ToList();
        var lost = colony.Colonists.Select(c => c.DaysLostToDespair).ToList();

        Console.WriteLine(
            $"{scale,6:F1} {peak,6} {colony.Living.Count(),6} {colony.Colonists.Count,6} {dead.Count,5} "
            + $"{(dead.Count == 0 ? 0 : dead.Average(c => c.AgeInDays(c.DiedOnDay))),10:F0} "
            + $"{lost.DefaultIfEmpty(0).Average(),7:F0} {lost.DefaultIfEmpty(0).Max(),7:F0} "
            + $"{colony.Living.Select(c => c.Wellbeing).DefaultIfEmpty(0).Average(),6:F3} "
            + $"{(extinctOn < 0 ? "no" : $"day {extinctOn:F0}"),8}");
    }

    WellbeingRules.DespairDaysPerDay = 6.0;
}

/// <summary>
/// What an evening at the hall should cost — M13's one open question, answered by measurement.
/// <para>
/// The failure the scope warned about is a corner solution: a price that never binds is
/// decoration, and one that always binds is dead code that looks implemented. Worse, the gate is
/// hard — a colonist who cannot pay is not offered the evening at all — so the honest shape to
/// look for is a *slope*. A step means the gate is the wrong mechanism and a score penalty
/// proportional to the shortfall is right.
/// </para>
/// </summary>
static void Prices(int days, int colonists, int seed)
{
    Console.WriteLine($"Gathering price sweep — {days} colony-days, {colonists} colonists, seed {seed}");
    Console.WriteLine($"A full working day earns {EconomyRules.WagePerWorkDay:F2}; the colony works about half of one.");
    Console.WriteLine();
    Console.WriteLine($"{"price",6} {"leisure",8} {"social",7} {"rest",6} {"work",6} {"evenings",9} {"bought",7} {"stand",6} {"belg",6} {"well",6} {"purse",6}");

    foreach (var price in new[] { 0.05, 0.1, 0.25, 0.5, 0.9, 1.5, 2.5, 5.0 })
    {
        ActionCatalog.Gather.Price = price;

        var random = new ColonyRandom(seed);
        var colony = ColonyFactory.Create(colonists, random, seed);
        var body = new BodySimulation(colony, random);

        var buckets = new Dictionary<string, long>();

        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();

            foreach (var colonist in colony.Living)
            {
                var bucket = colonist.Current is not { } action ? "idle"
                    : colonist.PlaceId != action.Target.Id ? "travel"
                    : action.Definition.Kind.ToString();

                buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
            }
        }

        var total = (double)buckets.Values.Sum();
        double Share(params string[] kinds) => total == 0 ? 0 : kinds.Sum(k => buckets.GetValueOrDefault(k)) / total;

        var lived = colony.Colonists.Where(c => c.AgeInDays(colony.Day) >= 1).ToList();
        var bought = lived.Count == 0 ? 0 : (double)lived.Count(c => c.EveningsBought > 0) / lived.Count;

        Console.WriteLine(
            $"{price,6:F2} {Share("Gather"),8:P1} {Share("Talk", "Teach"),7:P1} {Share("Rest"),6:P1} "
            + $"{Share("Forage", "Salvage", "Clear", "Build", "Tend"),6:P1} "
            + $"{lived.Sum(c => c.EveningsBought),9} {bought,7:P0} "
            + $"{colony.Living.DefaultIfEmpty().Average(c => c?.Drives[Drive.Standing] ?? 0),6:F2} "
            + $"{colony.Living.DefaultIfEmpty().Average(c => c?.Drives[Drive.Belonging] ?? 0),6:F2} "
            + $"{colony.Living.DefaultIfEmpty().Average(c => c?.Wellbeing ?? 0),6:F3} "
            + $"{lived.DefaultIfEmpty().Average(c => c?.Tokens ?? 0),6:F1}");
    }

    ActionCatalog.Gather.Price = EconomyRules.GatheringPrice;
}

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
/// <summary>
/// Every question the colony actually faced, and — far more usefully — every day it faced none.
/// <para>
/// Written after version 2 closed, because the first thing Jeremy noticed playing it was that
/// almost two colony-years went by with no new minds, and nothing in the project could say whether
/// that was a colony deciding against growth or a colony never being asked. A tally of forks cannot
/// tell those apart: the interesting number is not how the votes went but how many days passed with
/// the Loom able to print and nobody putting the question. So this samples the Print gate once a
/// colony-day and attributes every day the question was not asked to whichever gate held it shut.
/// </para>
/// </summary>
static void Forks(int days, int colonists, int seed)
{
    var random = new ColonyRandom(seed);
    var colony = ColonyFactory.Create(colonists, random, seed);
    var body = new BodySimulation(colony, random);

    var couldPrint = 0;
    var heldBy = new Dictionary<string, int>();
    var asked = 0;
    var lastDay = -1;

    // Who sat on each print, and what each of them was actually weighing. A tally of outcomes says
    // the colony refused; this says whether it could ever have done otherwise.
    var seatings = new List<string>();
    body.ForkOpened += fork =>
    {
        if (fork.Kind != ForkKind.Print)
        {
            return;
        }

        foreach (var id in fork.Participants)
        {
            if (colony.Find(id) is not { } voter)
            {
                continue;
            }

            var weight = voter.Weights[Drive.Belonging] - voter.Weights[Drive.Safety];
            var level = (voter.Drives[Drive.Belonging] - voter.Drives[Drive.Safety]) * 0.5;
            var world = fork.Pressure * ForkRules.PressureWeight;
            seatings.Add(
                $"  d{fork.OpenedOnDay,-6:F0} {voter.DisplayName,-10} "
                + $"who they are {weight,6:F2}  how they are {level,6:F2}  the world {world,6:F2}"
                + $"  =  {weight + level + world,6:F2}");
        }
    };

    // And the same breakdown again when the vote is counted, because the two moments are up to
    // three colony days apart and the council spends them deliberating in the plaza.
    body.ForkResolved += (fork, _) =>
    {
        if (fork.Kind != ForkKind.Print)
        {
            return;
        }

        foreach (var (id, vote) in fork.Spoken)
        {
            if (colony.Find(id) is not { } voter)
            {
                continue;
            }

            var weight = voter.Weights[Drive.Belonging] - voter.Weights[Drive.Safety];
            var level = (voter.Drives[Drive.Belonging] - voter.Drives[Drive.Safety]) * 0.5;
            var world = fork.Pressure * ForkRules.PressureWeight;
            seatings.Add(
                $"     counted {voter.DisplayName,-10} "
                + $"who they are {weight,6:F2}  how they are {level,6:F2}  the world {world,6:F2}"
                + $"  =  {weight + level + world,6:F2}  {vote.Stance}");
        }

        seatings.Add(string.Empty);
    };

    for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
    {
        body.Tick();

        var day = (int)colony.Day;
        if (day == lastDay)
        {
            continue;
        }

        lastDay = day;

        // In Detect's own order, because that is what decides which gate a day is spent behind.
        // A day can be held by several of these; it is attributed to the first, so the column
        // reads as "what would have to change for the question to be asked tomorrow".
        var held =
            colony.OpenFork is not null ? "another question already open"
            : colony.Living.Count() < 2 ? "too few living"
            : colony.Living.Any(c => c.AwaitingKindling) ? "someone is still being written"
            : colony.Cores.Any(c => c.Fate == CoreFate.Waiting) ? "a core is waiting to be decided"
            : colony.ForbiddenPlaces.Count > 0 && colony.Day >= colony.AskAgainOn(ForkKind.Ascend)
                ? "a way up comes first"
            : colony.Living.Count() >= LifeRules.PopulationCap ? "at the population cap"
            : !ForkRules.CanFeedAnother(colony) ? "the stores could not feed another"
            : colony.Day < colony.AskAgainOn(ForkKind.Print) ? "put off after a no"
            : null;

        if (held is null)
        {
            asked++;
        }
        else
        {
            heldBy[held] = heldBy.GetValueOrDefault(held) + 1;
        }

        if (colony.LoomCharge >= ForkRules.ChargeNeededToPrint(colony)
            && colony.Living.Count() < LifeRules.PopulationCap)
        {
            couldPrint++;
        }
    }

    Console.WriteLine($"Fork census — {days} colony-days, {colonists} founders, seed {seed}, no mind at all");
    Console.WriteLine();

    Console.WriteLine($"{"",-8}{"asked",6}{"carried",9}{"refused",9}{"first",7}{"last",7}{"mean gap",10}");
    foreach (var kind in Enum.GetValues<ForkKind>())
    {
        var of = colony.ForkHistory.Where(f => f.Kind == kind).ToList();
        if (of.Count == 0)
        {
            Console.WriteLine($"{kind,-8}{0,6}");
            continue;
        }

        var opened = of.Select(f => f.OpenedOnDay).ToList();
        var gaps = opened.Zip(opened.Skip(1), (a, b) => b - a).ToList();

        Console.WriteLine(
            $"{kind,-8}{of.Count,6}{of.Count(f => f.Carried == true),9}{of.Count(f => f.Carried != true),9}"
            + $"{$"d{opened[0]:F0}",7}{$"d{opened[^1]:F0}",7}"
            + $"{(gaps.Count == 0 ? "-" : $"{gaps.Average():F0}"),10}");
    }

    Console.WriteLine();
    Console.WriteLine($"The print question, day by day, over {days:N0} days:");
    Console.WriteLine($"  {"the Loom could have printed",-36}{couldPrint,7:N0}");
    Console.WriteLine($"  {"the question was free to be asked",-36}{asked,7:N0}");
    foreach (var (reason, count) in heldBy.OrderByDescending(p => p.Value))
    {
        Console.WriteLine($"  {"held: " + reason,-36}{count,7:N0}");
    }

    Console.WriteLine();
    Console.WriteLine("What each council weighed at each print (over 0.05 is a for, under -0.05 against):");
    foreach (var line in seatings)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine("Every fork the colony faced:");
    foreach (var fork in colony.ForkHistory)
    {
        Console.WriteLine(
            $"  d{fork.OpenedOnDay,-6:F0} {fork.Kind,-7} {(fork.Outcome ?? "-"),-9}"
            + $" for {fork.Count(Stance.For)} against {fork.Count(Stance.Against)} abstain {fork.Count(Stance.Abstain)}"
            + $"  of {fork.Participants.Count}, needed {fork.Needed}"
            + $"  pressure {fork.Pressure,6:F2}   {fork.Question}");
    }

    Console.WriteLine();
    Console.WriteLine($"Ended at {colony.Living.Count()} living, charge {colony.LoomCharge:F0}, "
                      + $"a print needs {ForkRules.ChargeNeededToPrint(colony):F0}.");
}

/// How a day's ticks are grouped for reading. Wander was the colony's only leisure through M12 and
/// measured 0.0% at every population — it is explicitly the fallback when nothing else scores, and
/// something always scores. Gather, from M13, is the line to read instead: a hall the colony voted
/// to raise and an evening a colonist pays for out of what the work earned.
/// </summary>
static IReadOnlyList<(string Label, IReadOnlyList<string> Kinds)> Classes() =>
[
    ("work", new[] { "Forage", "Salvage", "Clear", "Build", "Tend" }),
    ("rest", new[] { "Rest" }),
    ("social", new[] { "Talk", "Teach" }),
    ("leisure", new[] { "Gather", "Wander" }),
    ("civic", new[] { "Deliberate" }),
    ("travel", new[] { "travel" }),
    ("idle", new[] { "idle" })
];

/// <summary>Time not already claimed by work, rest, travel or a sitting council.</summary>
static string Reserve(double days) => double.IsInfinity(days) ? "no end" : $"{days:F0} days";

static bool IsFree(string bucket) => bucket is "Gather" or "Wander" or "Talk" or "Teach" or "idle";

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
