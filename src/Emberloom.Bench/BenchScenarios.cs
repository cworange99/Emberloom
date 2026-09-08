using System.Text.Json.Nodes;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;

namespace Emberloom.Bench;

/// <summary>
/// One measured call shape. These are the real five call types at their real token caps, not
/// proxies — every throughput figure in the design is meant to be resized from what these do.
/// </summary>
public sealed record BenchScenario
{
    public required string Name { get; init; }

    public required Lane Lane { get; init; }

    /// <summary>Stable prefix: world rules and personality. Cached across calls by Ollama's KV cache.</summary>
    public required string System { get; init; }

    /// <summary>Volatile suffix: only what changed for this specific call.</summary>
    public required string Prompt { get; init; }

    public JsonNode? Schema { get; init; }

    public required CallType Call { get; init; }

    /// <summary>Read from <see cref="CallBudgets"/> so the bench and the governor never disagree.</summary>
    public int MaxTokens => CallBudgets.For(Call);

    public double Temperature { get; init; } = 0.8;

    /// <summary>What this call is for, in the report.</summary>
    public required string Purpose { get; init; }
}

public static class BenchScenarios
{
    /// <summary>
    /// The world rules block. Identical across every colonist and every call, so it sits first
    /// and the KV cache earns its keep.
    /// </summary>
    private const string WorldRules = """
        [World]
        A city the forest took back. The people who built it are gone; nothing recorded where or why.
        A machine called the Loom still runs on trickle charge, printing one mind at a time into
        bodies it builds from what is around it. Food is easy — the forest is generous and nobody
        competes for it. What is scarce is Loom charge, salvage from the makers, and understanding.
        Printed minds arrive with a designation, not a name, and take a name once they have done
        something they consider worth being named for.

        [Rules]
        Never mention being an AI, a model, or a simulation. Speak only from what this colonist
        could plausibly know. The makers are unknowable; no colonist has ever been told the answer.
        """;

    private const string SelaPersonality = """
        [Colonist]
        Sela, designation C-03. Printed day 4, now 62 days old. Guarded.
        Curious past the point of caution, and resents unearned things. Works alone by preference.
        She has spent four days on the ninth floor at a door that will not open and has no hinge
        she can find.

        [Weights]
        wonder 1.6x baseline, standing 1.2x, safety 0.7x, belonging 0.8x.

        [Beliefs]
        - "What the makers left is not ours to spend." (formed d39)
        - "They left in order. Whatever took them, it did not take them quickly." (formed d35)

        [Relationships]
        Bram: affinity +0.64, trust +0.71, respect +0.80. Does the work and does not collect on it.
        C-06: affinity -0.41, trust -0.52, respect -0.09. Printed with charge she had a use for.
        """;

    public static IReadOnlyList<BenchScenario> All(string profileNote = "") =>
    [
        new BenchScenario
        {
            Name = "kindling",
            Call = CallType.Kindling,
            Lane = Lane.Deep,
            Purpose = "Once per colonist. The largest single call, and undroppable.",
            System = WorldRules,
            Prompt = """
                [Task]
                The Loom has just finished printing a new mind, designation C-09. The colony argued
                over the seed and the argument was won by those who wanted someone careful.

                Seed: "steady, watches before acting, distrusts what the colony agrees on too easily"

                Write this mind into being. Give two or three name candidates it might one day
                choose for itself, a short prose personality, and the seven drive weights the body
                will score its actions with. Weights are multipliers on baseline urgency: 1.0 is
                ordinary, above 1.0 means this mind feels that drive more than most.
                """,
            Schema = MindSchemas.Kindling(),
        },

        new BenchScenario
        {
            Name = "reflection",
            Call = CallType.Reflection,
            Lane = Lane.Deep,
            Purpose = "The core loop. Turns episodic rows into a consolidated memory and beliefs.",
            System = WorldRules + "\n\n" + SelaPersonality,
            Prompt = """
                [Recent memories]
                m-4471 d47 Bram carried the heavy half up nine flights and never mentioned it afterward. valence +0.51
                m-4472 d47 Sela said nothing about it at the meal. valence -0.12
                m-4478 d48 Iyo told the others the ninth floor was a waste of days. valence -0.44
                m-4479 d48 Bram said the door was worth trying. Nobody agreed with him. valence +0.31
                m-4481 d48 Sela worked the door alone until dark. valence +0.08

                [Task]
                Decide what these meant. Write one consolidated memory standing in for the batch,
                zero to two beliefs (each linked to the memory ids that produced it), an updated
                reading of whoever this batch was about, and any drift in her drive weights.
                """,
            Schema = MindSchemas.Reflection(),
        },

        new BenchScenario
        {
            Name = "dialogue",
            Call = CallType.Dialogue,
            Lane = Lane.Deep,
            Purpose = "The only unconstrained-prose call. In-view conversations outrank off-screen ones.",
            System = WorldRules + "\n\n" + SelaPersonality,
            Prompt = """
                [Scene]
                Bram finds Sela on the ninth floor, at the door, after dark. He asks what she
                expects to find behind it.

                [Task]
                Write only Sela's reply. One or two sentences. No stage directions, no quotation marks.
                """,
            Schema = null,
        },

        new BenchScenario
        {
            Name = "deliberation",
            Call = CallType.Deliberation,
            Lane = Lane.Deep,
            Purpose = "Undroppable. One of these per participant at a fork.",
            System = WorldRules + "\n\n" + SelaPersonality,
            Prompt = """
                [Fork]
                The colony has charge for exactly one more print, or for one sustained attempt at
                forcing the ninth-floor door. Not both. Iyo has proposed printing. Four have spoken
                for it already.

                [Task]
                Give Sela's stance, her reasoning in one sentence, the line she says aloud to the
                others, and how strongly she holds it.
                """,
            Schema = MindSchemas.Deliberation(),
        },

        new BenchScenario
        {
            Name = "coinage",
            Call = CallType.Coinage,
            Lane = Lane.Deep,
            Purpose = "Enters the colony's shared vocabulary and is reused verbatim thereafter.",
            System = WorldRules,
            Prompt = """
                [Occasion]
                The colony has recovered an artifact from the sixth floor: a flat pane that shows a
                still image of a room nobody recognises, and keeps showing it without any charge
                the colony can detect. Three colonists have now argued about it twice.

                [Task]
                Coin the name the colony will use for this thing from now on, and say what in their
                history it came out of.
                """,
            Schema = MindSchemas.Coinage(),
        },

        new BenchScenario
        {
            Name = "idle-thought",
            Call = CallType.IdleThought,
            Lane = Lane.Fast,
            Purpose = "Fast lane. Writes nothing durable; it is what makes the Now tab read as a mind.",
            System = SelaPersonality,
            Prompt = """
                [Task]
                Sela is walking back down from the ninth floor with nothing to show for the day.
                Write one sentence of what is going through her head. Her voice, not a narrator's.
                """,
            Schema = MindSchemas.IdleThought(),
        }
    ];
}
