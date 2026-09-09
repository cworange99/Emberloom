using System.Text.Json;
using Emberloom.Core.Colonists;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

/// <summary>
/// Reflection's shallow variant — an unprompted thought on the fast lane during downtime.
/// <para>
/// It writes nothing durable, which is the whole point: it is cheap enough to run often, and it is
/// what makes the inspector's Now tab read as a mind rather than a status panel. Because it is
/// droppable and produces no artifact, it is the lowest-priority call in the design.
/// </para>
/// </summary>
public static class IdleThoughtCall
{
    public static async Task<string?> ExecuteAsync(
        OllamaClient client,
        Colonist colonist,
        Colony colony,
        CancellationToken ct = default)
    {
        var doing = colonist.Current is { } current
            ? $"{current.Definition.Verb} {current.Target.Name}"
            : "walking, with nothing decided";

        var recent = colonist.Memory
            .MostSalient(colony.Day, 3)
            .Select(m => $"- {m.Describe()}")
            .ToList();

        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(colonist, colony),
            Prompt = $"""
                [Now]
                {colonist.DisplayName} is {doing}. It is {(colony.IsNight ? "dark" : "daylight")}.

                [Lately]
                {(recent.Count == 0 ? "- nothing much" : string.Join("\n", recent))}

                [Task]
                One sentence of what is going through their head. Their voice, not a narrator's.
                Not a summary of what they are doing — they already know that.
                """,
            Schema = MindSchemas.IdleThought(),
            MaxTokens = CallBudgets.For(CallType.IdleThought),
            Temperature = 1.0,
            Lane = Lane.Fast
        };

        try
        {
            var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(result.Text);

            var thought = document.RootElement.TryGetProperty("thought", out var element)
                ? element.GetString()?.Trim()
                : null;

            return string.IsNullOrWhiteSpace(thought) ? null : thought;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Nothing durable was going to come of it, so there is nothing to fall back to.
            // Silence is the correct degradation for a thought that never arrived.
            return null;
        }
    }
}
