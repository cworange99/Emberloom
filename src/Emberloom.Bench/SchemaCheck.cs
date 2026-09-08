using System.Text.Json;
using System.Text.Json.Nodes;

namespace Emberloom.Bench;

public sealed record SchemaCheckResult(bool Ok, IReadOnlyList<string> Problems)
{
    public static readonly SchemaCheckResult Pass = new(true, Array.Empty<string>());

    public static SchemaCheckResult Fail(params string[] problems) => new(false, problems);
}

/// <summary>
/// Validates a model's output against the same JSON schema that was handed to Ollama's
/// <c>format</c> parameter. Ollama constrains generation, but constrained decoding can still
/// truncate at the token cap and leave a structurally invalid document — a model that needs one
/// retry in five is slower than its tok/s implies, and that is exactly what M0 is measuring.
/// </summary>
public static class SchemaCheck
{
    public static SchemaCheckResult Validate(string text, JsonNode? schema)
    {
        if (schema is null)
        {
            // Dialogue is unconstrained on purpose. The only failure is producing nothing.
            return string.IsNullOrWhiteSpace(text)
                ? SchemaCheckResult.Fail("empty completion")
                : SchemaCheckResult.Pass;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            return SchemaCheckResult.Fail($"unparseable JSON: {ex.Message}");
        }

        if (parsed is null)
        {
            return SchemaCheckResult.Fail("completion parsed to null");
        }

        var problems = new List<string>();
        Check(parsed, schema, "$", problems);
        return problems.Count == 0 ? SchemaCheckResult.Pass : new SchemaCheckResult(false, problems);
    }

    private static void Check(JsonNode? node, JsonNode schema, string path, List<string> problems)
    {
        var type = schema["type"]?.GetValue<string>();

        switch (type)
        {
            case "object":
                CheckObject(node, schema, path, problems);
                break;
            case "array":
                CheckArray(node, schema, path, problems);
                break;
            case "string":
                CheckString(node, schema, path, problems);
                break;
            case "number":
                CheckNumber(node, schema, path, problems);
                break;
        }
    }

    private static void CheckObject(JsonNode? node, JsonNode schema, string path, List<string> problems)
    {
        if (node is not JsonObject obj)
        {
            problems.Add($"{path}: expected object, got {Describe(node)}");
            return;
        }

        var properties = schema["properties"] as JsonObject;

        if (schema["required"] is JsonArray required)
        {
            foreach (var entry in required)
            {
                var name = entry?.GetValue<string>();
                if (name is null)
                {
                    continue;
                }

                if (!obj.ContainsKey(name))
                {
                    problems.Add($"{path}: missing required property '{name}'");
                    continue;
                }

                if (properties?[name] is { } propertySchema)
                {
                    Check(obj[name], propertySchema, $"{path}.{name}", problems);
                }
            }
        }
    }

    private static void CheckArray(JsonNode? node, JsonNode schema, string path, List<string> problems)
    {
        if (node is not JsonArray array)
        {
            problems.Add($"{path}: expected array, got {Describe(node)}");
            return;
        }

        if (schema["minItems"] is { } min && array.Count < min.GetValue<int>())
        {
            problems.Add($"{path}: {array.Count} items, minimum {min.GetValue<int>()}");
        }

        if (schema["maxItems"] is { } max && array.Count > max.GetValue<int>())
        {
            problems.Add($"{path}: {array.Count} items, maximum {max.GetValue<int>()}");
        }

        if (schema["items"] is { } itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Check(array[i], itemSchema, $"{path}[{i}]", problems);
            }
        }
    }

    private static void CheckString(JsonNode? node, JsonNode schema, string path, List<string> problems)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            problems.Add($"{path}: expected string, got {Describe(node)}");
            return;
        }

        if (schema["enum"] is JsonArray allowed)
        {
            var options = allowed.Select(o => o?.GetValue<string>()).ToList();
            if (!options.Contains(text))
            {
                problems.Add($"{path}: '{text}' not one of [{string.Join(", ", options)}]");
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add($"{path}: empty string");
        }
    }

    private static void CheckNumber(JsonNode? node, JsonNode schema, string path, List<string> problems)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number))
        {
            problems.Add($"{path}: expected number, got {Describe(node)}");
            return;
        }

        if (schema["minimum"] is { } min && number < min.GetValue<double>())
        {
            problems.Add($"{path}: {number} below minimum {min.GetValue<double>()}");
        }

        if (schema["maximum"] is { } max && number > max.GetValue<double>())
        {
            problems.Add($"{path}: {number} above maximum {max.GetValue<double>()}");
        }
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => "scalar"
    };
}
