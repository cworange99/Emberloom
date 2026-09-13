using System.Text.Json.Nodes;
using Emberloom.Core.Drives;

namespace Emberloom.Mind.Calls;

/// <summary>
/// The JSON schemas for every call type except Dialogue, which is the one deliberately
/// unconstrained call. These go straight into Ollama's <c>format</c> parameter.
/// </summary>
public static class MindSchemas
{
    /// <summary>
    /// Kindling — once per colonist, at print or birth. Produces name candidates, a prose
    /// personality, and the seven weights the body scores actions with.
    /// </summary>
    public static JsonNode Kindling() => Object(
        properties: new JsonObject
        {
            ["name_candidates"] = Array(String(), minItems: 2, maxItems: 3),
            ["personality"] = String("Two or three sentences, in the third person."),
            ["weights"] = DriveWeights(),
            ["seed_summary"] = String("One short clause naming what this mind cares about most.")
        },
        required: ["name_candidates", "personality", "weights", "seed_summary"]);

    /// <summary>
    /// Reflection (deep) — reads a batch of episodic memories and decides what they meant.
    /// The belief-to-memory links are what make the inspector's traceable chain possible, so
    /// <c>source_memory_ids</c> is required rather than optional.
    /// </summary>
    public static JsonNode Reflection() => Object(
        properties: new JsonObject
        {
            ["consolidated"] = Object(
                properties: new JsonObject
                {
                    ["text"] = String("One or two sentences standing in for the whole batch."),
                    ["valence"] = Number(-1, 1),
                    ["source_memory_ids"] = Array(String(), minItems: 1)
                },
                required: ["text", "valence", "source_memory_ids"]),
            ["beliefs"] = Array(
                Object(
                    properties: new JsonObject
                    {
                        ["claim"] = String("A durable claim about the world, in this colonist's voice."),
                        ["source_memory_ids"] = Array(String(), minItems: 1),
                        ["confidence"] = Number(0, 1)
                    },
                    required: ["claim", "source_memory_ids", "confidence"]),
                maxItems: 2),
            ["relationship_note"] = Object(
                properties: new JsonObject
                {
                    ["about"] = String("Colonist designation, or empty if this batch was not about anyone."),
                    ["reading"] = String("How this colonist would describe them now, in one sentence.")
                },
                required: ["about", "reading"]),
            ["weight_drift"] = DriveDrift()
        },
        required: ["consolidated", "beliefs", "relationship_note", "weight_drift"]);

    /// <summary>Reflection (shallow) — idle thought on the fast lane. Writes nothing durable.</summary>
    public static JsonNode IdleThought() => Object(
        properties: new JsonObject
        {
            ["thought"] = String("One sentence of unprompted internal monologue.")
        },
        required: ["thought"]);

    /// <summary>
    /// Deliberation — one participant's stance at a genuine fork, plus the line they say aloud.
    /// These become the colony's remembered turning points.
    /// </summary>
    public static JsonNode Deliberation() => Object(
        properties: new JsonObject
        {
            ["stance"] = Enum("for", "against", "abstain"),
            ["reasoning"] = String("Why, in one sentence, grounded in this colonist's beliefs."),
            ["line"] = String("What they actually say to the others. One quotable sentence."),
            ["conviction"] = Number(0, 1)
        },
        required: ["stance", "reasoning", "line", "conviction"]);

    /// <summary>
    /// Coinage — a name or phrasing that enters the colony's shared vocabulary and is reused
    /// verbatim thereafter.
    /// </summary>
    public static JsonNode Coinage() => Object(
        properties: new JsonObject
        {
            ["name"] = String("The coined name or phrase. Short."),
            ["rationale"] = String("What in the colony's history this came out of, in one sentence.")
        },
        required: ["name", "rationale"]);

    /// <summary>
    /// Myth — the story the colony tells about its makers, and which of the beliefs it was shown
    /// go against it. Those are the colonists who are right and outnumbered, or wrong and alone.
    /// </summary>
    public static JsonNode Myth() => Object(
        properties: new JsonObject
        {
            ["story"] = String("The story as the colony tells it aloud. Three or four sentences."),
            ["contradicted_by"] = Array(String())
        },
        required: ["story", "contradicted_by"]);

    // ---- builders ---------------------------------------------------------

    /// <summary>The seven personality weights, as multipliers on baseline drive urgency.</summary>
    private static JsonNode DriveWeights()
    {
        var properties = new JsonObject();
        foreach (var key in Core.Drives.DriveTable.SchemaKeys)
        {
            properties[key] = Number(0.2, 2.0);
        }

        return Object(properties, Core.Drives.DriveTable.SchemaKeys);
    }

    /// <summary>Signed adjustments to those weights, emitted by reflection.</summary>
    private static JsonNode DriveDrift()
    {
        var properties = new JsonObject();
        foreach (var key in Core.Drives.DriveTable.SchemaKeys)
        {
            properties[key] = Number(-0.2, 0.2);
        }

        return Object(properties, Core.Drives.DriveTable.SchemaKeys);
    }

    private static JsonNode Object(JsonObject properties, IReadOnlyList<string> required)
    {
        var requiredArray = new JsonArray();
        foreach (var name in required)
        {
            requiredArray.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = requiredArray,
            ["additionalProperties"] = false
        };
    }

    private static JsonNode String(string? description = null)
    {
        var node = new JsonObject { ["type"] = "string" };
        if (description is not null)
        {
            node["description"] = description;
        }

        return node;
    }

    private static JsonNode Number(double minimum, double maximum) => new JsonObject
    {
        ["type"] = "number",
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JsonNode Enum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return new JsonObject { ["type"] = "string", ["enum"] = array };
    }

    private static JsonNode Array(JsonNode items, int? minItems = null, int? maxItems = null)
    {
        var node = new JsonObject { ["type"] = "array", ["items"] = items };
        if (minItems is not null)
        {
            node["minItems"] = minItems.Value;
        }

        if (maxItems is not null)
        {
            node["maxItems"] = maxItems.Value;
        }

        return node;
    }
}
