using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;

namespace Emberloom.Bench;

/// <summary>
/// Does a retelling actually retell? — M18's one measurable claim.
/// <para>
/// The overnight run told the story 158 times and most of them came back word for word. The cause
/// was in the prompt: a mind was handed the previous telling and the complete evidence list with no
/// mark on either, and instructed to keep most of how it was last told. There was nothing in it
/// that could change the story, so nothing did. M18 marks what is new and asks for a story that
/// accounts for it.
/// </para>
/// <para>
/// This tells the story repeatedly with the colony finding more between each telling, and prints
/// how much of each telling's wording survives into the next. Near 100% is the failure that was
/// observed; somewhere well short of it is a story growing to fit.
/// </para>
/// </summary>
internal static class MythProbe
{
    public static async Task<int> RunAsync(Uri endpoint, string model, int tellings, CancellationToken ct)
    {
        var options = new OllamaOptions
        {
            Endpoint = endpoint,
            Deep = new LaneConfig { Model = model },
            Fast = new LaneConfig { Model = model }
        };

        using var client = new OllamaClient(options);
        if (!await client.IsAvailableAsync(ct))
        {
            Console.Error.WriteLine($"Ollama is not reachable at {endpoint}.");
            return 2;
        }

        var random = new ColonyRandom(4);
        var colony = ColonyFactory.Create(4, random, 4);
        var body = new BodySimulation(colony, random);
        for (var tick = 0; tick < 10 * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Console.WriteLine($"Myth drift — {model}, {tellings} tellings, seed 4");
        Console.WriteLine("Each telling happens after the colony has found more. 'kept' is how much");
        Console.WriteLine("of the previous telling's wording survives: near 100% is the old failure.");
        Console.WriteLine();

        var revelations = Revelations.All.Select(r => r.Id).ToList();
        var kept = new List<double>();

        for (var round = 0; round < tellings && !ct.IsCancellationRequested; round++)
        {
            // Something new between each telling, which is what MythDue waits for anyway: a few
            // more pieces of the makers', and now and then something shown to somebody.
            colony.ArtifactsRecovered += 3;
            if (round > 0 && round <= revelations.Count)
            {
                PlayerVerbs.Reveal(colony, body, colony.Living.First(), revelations[round - 1]);
            }

            var previous = colony.Myth?.Story;
            var result = await MythCall.ExecuteAsync(client, MythCall.BuildPrompt(colony), ct);

            colony.MythHistory.Add(new MythTelling
            {
                Version = colony.MythHistory.Count + 1,
                Story = result.Story,
                ToldOnDay = colony.Day,
                Evidence = CultureRules.Evidence(colony),
                ArtifactsThen = colony.ArtifactsRecovered,
                RevealedThen = colony.Revealed.Keys.ToList()
            });

            colony.Tick += (long)(CultureRules.DaysBetweenTellings * Colony.TicksPerDay);

            var overlap = previous is null ? double.NaN : Overlap(previous, result.Story);
            if (!double.IsNaN(overlap))
            {
                kept.Add(overlap);
            }

            Console.WriteLine($"[{round + 1}] {(double.IsNaN(overlap) ? "    -" : $"{overlap,5:P0}")} kept"
                              + $"{(result.FromModel ? string.Empty : "  (FELL BACK)")}");
            Console.WriteLine($"      {result.Story}");
            Console.WriteLine();
        }

        if (kept.Count > 0)
        {
            Console.WriteLine($"Kept from one telling to the next: mean {kept.Average():P0}, "
                              + $"most {kept.Max():P0}, least {kept.Min():P0}");
        }

        return 0;
    }

    /// <summary>
    /// How much of the earlier telling's vocabulary is still in the later one. Words rather than
    /// characters, and one-directional: a story that keeps every word it had and adds more has
    /// kept 100%, which is the thing being looked for.
    /// </summary>
    private static double Overlap(string before, string after)
    {
        var was = Words(before);
        if (was.Count == 0)
        {
            return 0;
        }

        var now = Words(after);
        return (double)was.Count(now.Contains) / was.Count;
    }

    private static HashSet<string> Words(string text) =>
        text.Split([' ', '\n', '\r', '\t', '.', ',', ';', ':', '—', '“', '”', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 3)
            .ToHashSet();
}
