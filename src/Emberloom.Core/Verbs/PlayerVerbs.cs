using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.World;
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
    /// <summary>
    /// Whether the player may reach into this colony at all — M16. A colony begun as a simulation
    /// refuses every verb for its whole life, which is what makes a research run evidence of
    /// anything: the mode is fixed at creation and there is no way to change it afterwards.
    /// </summary>
    public static VerbResult? NobodyInterferes(Colony colony) => colony.Mode == ColonyMode.Simulation
        ? VerbResult.Refused("This colony was begun as a simulation. Nothing reaches into it, including you.")
        : null;

    /// <summary>
    /// Ask: a standing request the colony hears and may ignore — M16, and the only verb that is not
    /// a one-off. It is one more term in the utility score, so what it does is make something more
    /// attractive rather than make it happen; <see cref="IntentRules"/> holds how far it reaches
    /// and why somebody in real need cannot hear it at all.
    /// </summary>
    public static VerbResult Ask(
        Colony colony,
        ActionKind verb,
        string? placeId,
        string? colonistId,
        double strength)
    {
        if (NobodyInterferes(colony) is { } no)
        {
            return no;
        }

        if (placeId is not null && colony.Map.Find(placeId) is null)
        {
            return VerbResult.Refused($"There is nowhere called '{placeId}'.");
        }

        var who = colonistId is null ? null : colony.Find(colonistId);
        if (colonistId is not null && who is not { IsAlive: true })
        {
            return VerbResult.Refused("There is nobody here by that name.");
        }

        var intent = new Intent(
            verb,
            placeId,
            colonistId,
            Math.Clamp(strength, 0, 1),
            colony.Day + IntentRules.LastsDays);

        // The same request twice is one request, restated — otherwise asking repeatedly would
        // multiply the term against itself and an order could be assembled out of suggestions.
        colony.Intents.RemoveAll(i => i.Verb == intent.Verb && i.PlaceId == intent.PlaceId && i.ColonistId == intent.ColonistId);
        colony.Intents.Add(intent);

        while (colony.Intents.Count > IntentRules.AtMostStanding)
        {
            colony.Intents.RemoveAt(0);
        }

        colony.Log(EventLayer.World, $"Something has asked the colony for this — {intent.Describe(colony)}.", notable: true);
        return new VerbResult(true, Colonist: who);
    }

    public static VerbResult Speak(Colony colony, BodySimulation body, Colonist colonist, string text)
    {
        if (NobodyInterferes(colony) is { } refused)
        {
            return refused;
        }

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
        if (NobodyInterferes(colony) is { } refused)
        {
            return refused;
        }

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

    /// <summary>
    /// Provide: route charge into light, warmth, or a door that will not open. Light and warmth
    /// are paid by the day for as long as they run; a door once. Whoever is there sees it happen.
    /// </summary>
    public static VerbResult Provide(Colony colony, BodySimulation body, ProvideKind kind, string placeId)
    {
        if (NobodyInterferes(colony) is { } refused)
        {
            return refused;
        }

        var place = colony.Map.Find(placeId);
        if (place is null)
        {
            return VerbResult.Refused("There is no such place.");
        }

        var holds = Math.Floor(colony.LoomCharge);

        if (kind == ProvideKind.Door)
        {
            if (!place.IsBlocked)
            {
                return VerbResult.Refused($"Nothing is keeping anyone out of {place.Name}.");
            }

            // A "no" at an Ascend fork is the colony's. Opening the door over it would make the
            // vote decorative, the same way printing over an open Print fork would.
            if (colony.ForbiddenPlaces.Contains(place.Id))
            {
                return VerbResult.Refused($"Whether to go up to {place.Name} is the colony's to decide.");
            }

            if (colony.LoomCharge < ProvisionRules.DoorCost)
            {
                return VerbResult.Refused(
                    $"Opening it takes {ProvisionRules.DoorCost:F0} charge, and the Loom holds {holds:F0}.");
            }

            colony.LoomCharge -= ProvisionRules.DoorCost;
            place.ClearingDone = place.ClearingRequired;

            colony.Log(EventLayer.Player, $"You routed charge into {place.Name}, and the way in opened.",
                placeId: place.Id, notable: true);

            // Nobody saw who did it. Everyone hears that it happened.
            foreach (var colonist in colony.Living)
            {
                body.Record(colonist, "heard that the way into", $"{place.Name} opened by itself",
                    colonist.PlaceId, valence: 0.3, intensity: 0.7);
            }

            // A way up is still the colony's to take.
            ForkRules.HoldTheWayUp(colony, place);
            return new VerbResult(true);
        }

        var provision = kind == ProvideKind.Light ? ProvisionKind.Light : ProvisionKind.Warmth;
        var word = ProvisionRules.Word(provision);
        var cost = ProvisionRules.CostPerDay(provision);

        if (colony.IsProvided(provision, place.Id))
        {
            return VerbResult.Refused($"There is already {word} at {place.Name}.");
        }

        if (provision == ProvisionKind.Light && place.Light >= 1.0)
        {
            return VerbResult.Refused($"{place.Name} is already in full light.");
        }

        if (provision == ProvisionKind.Warmth && place.Kind != PlaceKind.Shelter)
        {
            return VerbResult.Refused("Warmth only holds in a shelter.");
        }

        if (colony.LoomCharge < cost)
        {
            return VerbResult.Refused($"Keeping {word} there costs {cost:F0} charge a day, and the Loom holds {holds:F0}.");
        }

        colony.Provisions.Add(new Provision(provision, place.Id, cost, colony.Day));

        colony.Log(EventLayer.Player, $"You routed charge into {word} at {place.Name}. It costs {cost:F0} a day.",
            placeId: place.Id, notable: true);

        foreach (var colonist in colony.ColonistsAt(place.Id))
        {
            body.Record(colonist, provision == ProvisionKind.Light ? "saw the light come on at" : "felt the warmth come on at",
                place.Name, place.Id, valence: 0.4, intensity: 0.6);
        }

        return new VerbResult(true);
    }

    /// <summary>
    /// Withhold: cut what you were providing. Free, and expensive — they notice. Whoever is there
    /// feels it at once, and whoever had been using it lately remembers it going.
    /// </summary>
    public static VerbResult Withhold(Colony colony, BodySimulation body, ProvisionKind kind, string placeId)
    {
        if (NobodyInterferes(colony) is { } refused)
        {
            return refused;
        }

        var provision = colony.Provisions.FirstOrDefault(p => p.Kind == kind && p.PlaceId == placeId);
        if (provision is null)
        {
            return VerbResult.Refused("You are not providing that.");
        }

        var place = colony.Map[provision.PlaceId];
        colony.Provisions.Remove(provision);

        colony.Log(EventLayer.Player, $"You cut the {ProvisionRules.Word(kind)} at {place.Name}.",
            placeId: place.Id, notable: true);

        Notice(colony, body, provision);
        return new VerbResult(true);
    }

    /// <summary>
    /// The Loom ran short: a provision goes out on its own, and is noticed exactly as though it had
    /// been cut. The colony cannot tell the difference, and is not told.
    /// </summary>
    public static void Lapse(Colony colony, BodySimulation body, Provision provision)
    {
        var place = colony.Map[provision.PlaceId];
        colony.Provisions.Remove(provision);

        colony.Log(EventLayer.World, $"The Loom ran short, and the {ProvisionRules.Word(provision.Kind)} at {place.Name} went out.",
            placeId: place.Id, notable: true);

        Notice(colony, body, provision);
    }

    /// <summary>
    /// Reveal: show one colonist something of the makers'. Irreversible — each thing is shown once,
    /// to one mind, which remembers it as vividly as anything and can only pass it on by talking.
    /// Some things change what the colony can do; the rest are evidence for someone to make sense of.
    /// </summary>
    public static VerbResult Reveal(Colony colony, BodySimulation body, Colonist colonist, string revelationId)
    {
        if (NobodyInterferes(colony) is { } refused)
        {
            return refused;
        }

        var revelation = Revelations.Find(revelationId);
        if (revelation is null)
        {
            return VerbResult.Refused("There is nothing like that to show.");
        }

        if (colony.Revealed.ContainsKey(revelation.Id))
        {
            return VerbResult.Refused("That has already been shown. It cannot be shown again.");
        }

        if (!colony.Living.Contains(colonist))
        {
            return VerbResult.Refused($"{colonist.DisplayName} can no longer see anything.");
        }

        colony.Revealed[revelation.Id] = colonist.Id;

        var memory = body.Record(colonist, "was shown something of the makers':", revelation.Shown,
            colonist.PlaceId, revelation.Valence, intensity: 1.0);

        // Whatever it means, it is something nobody understands — which is what Wonder is for.
        colonist.Drives.Relieve(new DriveVector(new Dictionary<Drive, double> { [Drive.Wonder] = 0.6 }));

        colony.Log(EventLayer.Player, $"You showed {colonist.DisplayName} {revelation.Title}.", colonist, notable: true);

        if (revelation.Apply is not null)
        {
            revelation.Apply(colony);
            colony.Log(EventLayer.World, revelation.Unlocks ?? "Something changed.", colonist, notable: true);
        }

        return new VerbResult(true, Colonist: colonist, Memory: memory);
    }

    /// <summary>"They notice": whoever is there now, and whoever has been there in the last two days.</summary>
    private static void Notice(Colony colony, BodySimulation body, Provision provision)
    {
        var place = colony.Map[provision.PlaceId];
        var word = ProvisionRules.Word(provision.Kind);

        foreach (var colonist in colony.Living.ToList())
        {
            var here = colonist.PlaceId == place.Id;
            var usedIt = here || colonist.Memory
                .MostSalient(colony.Day, 40)
                .Any(m => m.PlaceId == place.Id && colony.Day - m.Day < 2);

            if (!usedIt)
            {
                continue;
            }

            body.Record(colonist, $"noticed the {word} go out at", place.Name, place.Id,
                valence: -0.5, intensity: here ? 0.85 : 0.6);

            if (here)
            {
                colonist.Drives.Aggravate(Drive.Safety, 0.15);
            }
        }
    }
}
