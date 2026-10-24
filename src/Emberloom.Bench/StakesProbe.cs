using Emberloom.Core.Colonists;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;

namespace Emberloom.Bench;

/// <summary>
/// M14's riskiest assumption, measured before a line of M14 was written: **does a council's stance
/// actually move when the stakes say the stores are thin?**
/// <para>
/// The whole milestone rests on it. Tying the Loom's charge to labour is meant to give the Print
/// fork the argument it has never had — "can we feed another?" instead of "has a threshold been
/// crossed?" — and the colony's stock has to reach the council as *words*, because CLAUDE.md is
/// explicit that a raw figure in a prompt comes back as a belief ("The charge is at 41.2, which
/// means…"). If a 3B model does not shift on the wording, the resource is invisible to the politics
/// and the gate has to go in the body instead. That is a different half of the milestone to build,
/// so it is worth an afternoon to know which.
/// </para>
/// <para>
/// The M5 warning is the precedent: a looser Print stakes line was read by the same size of model as
/// a reason *to* print, the opposite of what it said.
/// </para>
/// </summary>
internal static class StakesProbe
{
    /// <summary>
    /// Three readings of the same fact, in the register the game already uses. Nothing in them is a
    /// figure, and nothing but the stock differs — the For and Against clauses are word-for-word the
    /// live ones, so whatever moves is the stock.
    /// </summary>
    private static readonly (string Name, string Stakes)[] Variants =
    [
        ("thin", "The stores are thin — what the colony gathers is eaten, and there is little behind it. "
                 + "For: a print adds one more mind to the colony — more company, more hands. "
                 + "Against: the charge is spent and gone; kept, it could light the dark places or open a door that will not open."),

        ("adequate", "The stores hold. There is enough for everyone here, with something behind it. "
                     + "For: a print adds one more mind to the colony — more company, more hands. "
                     + "Against: the charge is spent and gone; kept, it could light the dark places or open a door that will not open."),

        ("plentiful", "The stores are full — more is gathered than anyone eats, and it sits in the dark going nowhere. "
                      + "For: a print adds one more mind to the colony — more company, more hands. "
                      + "Against: the charge is spent and gone; kept, it could light the dark places or open a door that will not open.")
    ];

    public static async Task<int> RunAsync(Uri endpoint, string model, int samples, CancellationToken ct)
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

        // A colony far enough in that its people have beliefs and memories to argue from. The
        // deliberation prompt reads both, and a council with empty histories argues generically.
        var random = new ColonyRandom(4);
        var colony = ColonyFactory.Create(4, random, 4);
        var body = new BodySimulation(colony, random);
        for (var tick = 0; tick < 40 * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Console.WriteLine($"Stakes probe — {model}, {samples} samples per variant, {colony.Living.Count()} living");
        Console.WriteLine("Does a worded stock move a council's stance? If not, the gate belongs in the body.");
        Console.WriteLine();
        Console.WriteLine($"{"stores",10} {"for",6} {"against",8} {"abstain",8} {"fell back",10} {"conviction",11}");

        foreach (var (name, stakes) in Variants)
        {
            var tally = new Dictionary<Stance, int>();
            var convictions = new List<double>();
            var fellBack = 0;

            for (var sample = 0; sample < samples && !ct.IsCancellationRequested; sample++)
            {
                // A fresh fork each sample, with nothing said yet: later speakers in a real council
                // hear earlier ones, and that would confound the one thing being measured.
                var fork = Print(colony, stakes);
                var speaker = colony.Living.ElementAt(sample % colony.Living.Count());

                var result = await DeliberationCall.ExecuteAsync(client, speaker, colony, fork, ct);

                tally[result.Vote.Stance] = tally.GetValueOrDefault(result.Vote.Stance) + 1;
                convictions.Add(result.Vote.Conviction);
                if (result.Vote.WasFallback)
                {
                    fellBack++;
                }
            }

            Console.WriteLine(
                $"{name,10} {tally.GetValueOrDefault(Stance.For),6} {tally.GetValueOrDefault(Stance.Against),8} "
                + $"{tally.GetValueOrDefault(Stance.Abstain),8} {fellBack,10} {convictions.DefaultIfEmpty(0).Average(),11:F2}");
        }

        return 0;
    }

    private static Fork Print(Colony colony, string stakes) => new()
    {
        Id = "probe",
        Kind = ForkKind.Print,
        Question = "Do we let the Loom print another mind?",
        Proposal = "The Loom spends its charge on one more mind.",
        Stakes = stakes,
        PlaceId = colony.Map.Places.First().Id,
        OpenedOnDay = colony.Day,
        Participants = colony.Living.Select(c => c.Id).ToList()
    };
}
