using Emberloom.Core.Colonists;

namespace Emberloom.Core.Verbs;

/// <summary>
/// One thing of the makers' the player can show a colonist. Fragments, never answers: the makers
/// stay unknowable, and a revelation is evidence someone will have to make sense of.
/// </summary>
public sealed record Revelation(
    string Id,
    string Title,
    string Shown,
    double Valence,
    string? Unlocks,
    Action<Colony>? Apply = null);

/// <summary>
/// What there is to reveal. Irreversible, each shown once, to one colonist, who remembers it as
/// vividly as anything the body records and can pass it on only by talking. The colony keeps a
/// record of what has been shown, for the myth system to read at M7.
/// <para>
/// PLACEHOLDER CONTENT. These five were written to exercise the verb, not as the game's lore:
/// the fragments themselves are Jeremy's to author. Anything that unlocks a capability says so in
/// <see cref="Revelation.Unlocks"/>, and the body change is in <see cref="Revelation.Apply"/>.
/// </para>
/// </summary>
public static class Revelations
{
    public static readonly IReadOnlyList<Revelation> All =
    [
        new("ledger", "a page of the Loom's ledger",
            "A page of the Loom's own ledger: a column of designations, hundreds long, every one struck through but the last few.",
            Valence: -0.4,
            Unlocks: null),

        new("charge-lines", "where the Loom's charge comes from",
            "Behind the Loom Floor, its cables run down into the ground, not up to the light. Something under the city feeds it.",
            Valence: 0.3,
            Unlocks: "The Loom gathers charge faster, now that someone knows what it drinks from.",
            Apply: colony => colony.LoomChargePerDay += 0.8),

        new("stair-seal", "how the stairwell was sealed",
            "The East Stairwell was sealed from above, not below, with tools none of them have, by someone who meant it to stay shut.",
            Valence: -0.2,
            Unlocks: "Whoever clears the East Stairwell now knows where to push: what is left takes half the work.",
            Apply: colony =>
            {
                if (colony.Map.Find("east-stair") is { IsBlocked: true } stair)
                {
                    stair.ClearingRequired = stair.ClearingDone + (stair.ClearingRequired - stair.ClearingDone) / 2;
                }
            }),

        new("planting", "the planting map",
            "A map of the city with the forest already drawn on it, street by street, before any of it grew. Someone meant the trees.",
            Valence: 0.2,
            Unlocks: null),

        new("ninth-plan", "a plan of the Ninth Floor",
            "A plan of the Ninth Floor: rows of narrow rooms, each exactly long enough for one of them to lie down in.",
            Valence: -0.3,
            Unlocks: null)
    ];

    public static Revelation? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}
