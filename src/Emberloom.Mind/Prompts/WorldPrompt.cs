using System.Text;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Relationships;

namespace Emberloom.Mind.Prompts;

/// <summary>
/// Assembles prompts in labelled blocks, ordered <em>stable prefix first, volatile suffix last</em>,
/// so Ollama's KV cache can reuse the expensive part across calls. Morpheus's
/// <c>SystemPromptService</c> already assembled prompts this way; what it needed was its blocks
/// reordered by how often they change.
/// <para>
/// Everything here is pure string assembly from state the caller passes in. That is deliberate:
/// a mind call carries its whole context, so the thing serving it never needs to know anything
/// about the colony. That property is what keeps a remote inference endpoint a config change
/// rather than a rewrite.
/// </para>
/// </summary>
public static class WorldPrompt
{
    /// <summary>
    /// Never changes, for any colonist, for the life of a colony. First in every prompt so the
    /// cache hit covers as much as possible.
    /// </summary>
    public const string WorldRules = """
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
        Write plainly. No stage directions, no headings, no lists unless asked for one.
        """;

    /// <summary>
    /// A colonist's identity and personality — stable for their whole life apart from slow weight
    /// drift, so it sits second.
    /// </summary>
    public static string Colonist(Colonist colonist, double currentDay)
    {
        var builder = new StringBuilder();

        builder.AppendLine("[Colonist]");
        builder.AppendLine(
            $"{colonist.DisplayName}, designation {colonist.Designation}. " +
            $"{(colonist.Lineage == LineageKind.Printed ? "Printed" : "Born")} day {colonist.BornOnDay:F0}, " +
            $"now {colonist.AgeInDays(currentDay):F0} days old. {colonist.Mood(currentDay)}.");

        if (!string.IsNullOrWhiteSpace(colonist.Personality))
        {
            builder.AppendLine(colonist.Personality);
        }

        builder.AppendLine();
        builder.AppendLine("[Weights]");
        builder.AppendLine(Weights(colonist.Weights));

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Only the weights that differ from baseline, phrased as a person would notice them. A wall
    /// of "1.00" for the five ordinary drives teaches the model nothing and costs prompt tokens.
    /// </summary>
    public static string Weights(PersonalityWeights weights)
    {
        var notable = DriveTable.All
            .Select(d => (Drive: d, Weight: weights[d]))
            .Where(w => Math.Abs(w.Weight - 1.0) >= 0.15)
            .OrderByDescending(w => Math.Abs(w.Weight - 1.0))
            .Select(w => $"{DriveTable.Describe(w.Drive)} {w.Weight:F1}x")
            .ToList();

        return notable.Count == 0
            ? "nothing pulls harder than anything else."
            : string.Join(", ", notable) + " baseline.";
    }

    /// <summary>Semi-stable: how this colonist currently reads the people around them.</summary>
    public static string Relationships(
        Colonist colonist,
        Colony colony,
        int max = 4)
    {
        var rows = colony.Relationships
            .Outgoing(colonist.Id)
            .Where(r => r.SharedMemories > 0)
            .OrderByDescending(r => Math.Abs(r.Affinity) + Math.Abs(r.Trust))
            .Take(max)
            .ToList();

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Relationships]");

        foreach (var row in rows)
        {
            var other = colony.Find(row.ToId)?.DisplayName ?? row.ToId;
            builder.Append($"{other}: {Describe(row)}.");

            if (!string.IsNullOrWhiteSpace(row.Narrative))
            {
                builder.Append(' ').Append(row.Narrative);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string Describe(Relationship relationship) => relationship.Stage switch
    {
        RelationshipStage.Bonded => "close, and trusted with things that matter",
        RelationshipStage.Trusted => "trusted",
        RelationshipStage.Familiar => "familiar, not yet close",
        RelationshipStage.Acquaintance => "known a little",
        _ => "barely known"
    };

    /// <summary>The whole stable-to-semi-stable prefix, in cache order.</summary>
    public static string SystemFor(Colonist colonist, Colony colony)
    {
        var builder = new StringBuilder();
        builder.AppendLine(WorldRules);
        builder.AppendLine();
        builder.AppendLine(Colonist(colonist, colony.Day));

        var relationships = Relationships(colonist, colony);
        if (!string.IsNullOrWhiteSpace(relationships))
        {
            builder.AppendLine();
            builder.AppendLine(relationships);
        }

        return builder.ToString().TrimEnd();
    }
}
