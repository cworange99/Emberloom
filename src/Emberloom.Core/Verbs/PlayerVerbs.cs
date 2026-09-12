using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;

namespace Emberloom.Core.Verbs;

/// <summary>What a verb did, or why it could not.</summary>
public sealed record VerbResult(bool Ok, string? Refusal = null, Colonist? Colonist = null, EpisodicMemory? Memory = null)
{
    public static VerbResult Refused(string why) => new(false, why);
}

/// <summary>
/// The player's verbs, as the body sees them (DESIGN §10). Influence, never orders: every verb
/// changes what a colonist has lived through or what the colony has to work with, and the body
/// goes on choosing for itself. Nothing here calls a model — where a verb wants a mind to answer,
/// the host asks for that separately, and the verb has already happened whether or not it comes.
/// </summary>
public static class PlayerVerbs
{
    /// <summary>
    /// What a colonist remembers when the player speaks. The colonists were never told what the
    /// player is, so neither are the prompts: it is a voice, and nothing about it says the Loom.
    /// </summary>
    public const string HeardVerb = "heard a voice with no body say";

    public const int MaxWords = 280;

    public const int MaxSeed = 400;

    /// <summary>
    /// Speak: address one colonist. What you say is remembered, and weighted heavily — as intense
    /// as anything the body records, so it is near the top of the next reflection's batch and can
    /// become a belief. Nobody else hears it.
    /// </summary>
    public static VerbResult Speak(Colony colony, BodySimulation body, Colonist colonist, string text)
    {
        var words = text.Trim();

        if (words.Length == 0)
        {
            return VerbResult.Refused("Say something.");
        }

        if (words.Length > MaxWords)
        {
            return VerbResult.Refused($"Keep it under {MaxWords} characters.");
        }

        if (!colony.Living.Contains(colonist))
        {
            return VerbResult.Refused($"{colonist.DisplayName} can no longer hear anything.");
        }

        var memory = body.Record(colonist, HeardVerb, $"“{words}”", colonist.PlaceId, valence: 0.6, intensity: 1.0);

        colony.Log(EventLayer.Player, $"You, to {colonist.DisplayName}: “{words}”", colonist,
            placeId: colonist.PlaceId, notable: true);

        return new VerbResult(true, Colonist: colonist, Memory: memory);
    }

    /// <summary>
    /// Print: spend the Loom's charge on a new mind, from a seed you write or one rolled. Refused
    /// while the colony is arguing over exactly that — a player who could print over the top of an
    /// open vote would make every Print fork decorative. Everyone feels the charge go.
    /// </summary>
    public static VerbResult Print(Colony colony, BodySimulation body, Random random, string? seed)
    {
        if (colony.OpenFork is { Kind: ForkKind.Print, IsOpen: true })
        {
            return VerbResult.Refused("The colony is deciding that right now. Let them finish.");
        }

        if (colony.LoomCharge < Colony.PrintCost)
        {
            return VerbResult.Refused(
                $"The Loom needs {Colony.PrintCost:F0} charge to print, and holds {Math.Floor(colony.LoomCharge):F0}.");
        }

        var authored = string.IsNullOrWhiteSpace(seed) ? null : seed.Trim();
        if (authored?.Length > MaxSeed)
        {
            return VerbResult.Refused($"Keep the seed under {MaxSeed} characters.");
        }

        var printed = ColonyFactory.Print(colony, random);
        if (printed is null)
        {
            return VerbResult.Refused("The Loom would not print.");
        }

        if (authored is not null)
        {
            printed.Seed = authored;
        }

        colony.Log(EventLayer.Player,
            authored is null
                ? $"You had the Loom print {printed.Designation}, from a rolled seed."
                : $"You had the Loom print {printed.Designation}, from: “{authored}”",
            printed, notable: true);

        // Nobody here asked for this one, and the charge was everyone's.
        foreach (var other in colony.Living.Where(c => !ReferenceEquals(c, printed)))
        {
            body.Record(other, "felt the Loom spend its charge on", $"{printed.Designation}, though nobody here asked it to",
                other.PlaceId, valence: -0.2, intensity: 0.5);
        }

        return new VerbResult(true, Colonist: printed);
    }
}
