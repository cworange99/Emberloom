using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;

namespace Emberloom.Core.Verbs;

/// <summary>
/// Something the player would like the colony to do — M16. Not an order.
/// <para>
/// An intent is one more multiplicative term in <see cref="UtilityScorer"/>'s context modifier,
/// beside the darkness, danger and crowding terms that have always been there. It makes the thing
/// asked for more attractive; it never makes it chosen. A colonist who is starving still eats, and
/// the whole of <see cref="ColonyMode"/> is how hard the thumb presses — zero in Simulation, so a
/// research run is provably the same binary rather than a different build.
/// </para>
/// </summary>
/// <param name="Verb">What is being asked for.</param>
/// <param name="PlaceId">Where, if anywhere. Null means anywhere it can happen.</param>
/// <param name="ColonistId">Who, if anyone. Null means whoever is willing.</param>
/// <param name="Strength">How much is being asked, 0 to 1, before the mode scales it.</param>
/// <param name="ExpiresOnDay">
/// When the colony stops hearing it. Intents expire rather than being cancelled because a standing
/// instruction nobody remembers giving is how a simulation quietly becomes a puppet show.
/// </param>
public sealed record Intent(
    ActionKind Verb,
    string? PlaceId,
    string? ColonistId,
    double Strength,
    double ExpiresOnDay)
{
    public string Describe(Colony colony)
    {
        var who = ColonistId is null ? "anyone" : colony.Find(ColonistId)?.DisplayName ?? ColonistId;
        var where = PlaceId is null ? string.Empty : $" at {colony.Map.Find(PlaceId)?.Name ?? PlaceId}";

        return $"{who}: {ActionCatalog.ByKind(Verb).Verb}{where}";
    }
}

/// <summary>
/// How far an intent reaches, and what it is allowed to reach past. One code path at three
/// strengths — there is no second scorer and no branch on mode anywhere in the body.
/// </summary>
public static class IntentRules
{
    /// <summary>
    /// What the mode multiplies an intent's strength by.
    /// <para>
    /// Measured, not chosen: `--intent` sweeps it and reports what fraction of ticks the thing
    /// asked for actually wins, bucketed by how badly the colonist needed something else. What is
    /// wanted is a curve rather than a cliff, and a starving colonist overriding at every strength.
    /// </para>
    /// </summary>
    public static double Reach(ColonyMode mode) => ReachOverride ?? mode switch
    {
        // Present but inert. The term is still evaluated, so a Simulation run is the same code
        // doing the same work — it is just multiplying by one.
        ColonyMode.Simulation => 0.0,

        ColonyMode.Hybrid => 0.8,
        ColonyMode.Directed => 3.0,
        _ => 0.0
    };

    /// <summary>
    /// Set only by the sweep that chose the numbers above, exactly as ActionCatalog.ReliefScale is.
    /// Null in the game, always.
    /// </summary>
    public static double? ReachOverride { get; set; }

    /// <summary>The longest an intent is heard for, in colony days, whatever the mode.</summary>
    public const double LastsDays = 20;

    /// <summary>At most this many standing at once, oldest dropped. A list nobody reads is not a plan.</summary>
    public const int AtMostStanding = 4;

    /// <summary>
    /// What this intent is worth to this colonist considering this action here — 0 when it does not
    /// apply at all, so the common case costs one comparison.
    /// </summary>
    public static double Pull(Intent intent, Colony colony, Colonist colonist, ActionKind kind, string placeId)
    {
        if (intent.Verb != kind
            || (intent.PlaceId is { } where && where != placeId)
            || (intent.ColonistId is { } who && who != colonist.Id)
            || colony.Day >= intent.ExpiresOnDay)
        {
            return 0;
        }

        // In Hybrid the colony's own decisions outrank the player's, which is the same rule that
        // already refuses a Print verb while a Print vote is open: a council deciding something is
        // not a council to be leaned on about it.
        if (colony.Mode == ColonyMode.Hybrid && IsBeingArgued(colony, intent))
        {
            return 0;
        }

        // And it reaches nobody who is desperate. This is the mode promise made structural
        // rather than tuned: measured at reach 15, a colonist under real pressure still did what
        // they were asked 77% of the time, which makes the term an order and the mode a lie. The
        // asking fades as the need rises and is worth nothing at all once something is urgent.
        //
        // Urgency is already squared, so this bites late: a colonist with everything merely
        // unsatisfied hears you perfectly well, and one with a drive at the top does not.
        var dominant = colonist.Drives.Dominant(colonist.Weights);
        var deaf = Math.Clamp(1 - colonist.Drives.Urgency(dominant, colonist.Weights[dominant]), 0, 1);

        return intent.Strength * Reach(colony.Mode) * deaf;
    }

    private static bool IsBeingArgued(Colony colony, Intent intent) =>
        colony.OpenFork is { } fork
        && (fork.SubjectPlaceId is not null && fork.SubjectPlaceId == intent.PlaceId
            || fork.SubjectColonistId is not null && fork.SubjectColonistId == intent.ColonistId);
}
