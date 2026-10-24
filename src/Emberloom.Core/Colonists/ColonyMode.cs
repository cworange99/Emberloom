namespace Emberloom.Core.Colonists;

/// <summary>
/// How far the player can reach into a colony. Chosen when the colony is created and fixed for its
/// life — a research run has to be a research run from the first tick to the last, or it is not
/// evidence of anything.
/// <para>
/// The seam is here from M10 so that M16 does not have to retrofit it, but only
/// <see cref="Simulation"/> and <see cref="Hybrid"/> differ in behaviour yet: the verbs are
/// unchanged and no intents exist. What makes this one code path rather than three is that the mode
/// is a *strength*, not a branch — see DESIGN-V2.md, M16. Simulation sets that strength to zero, so
/// a paper can say "intent strength 0" rather than "we ran a different binary".
/// </para>
/// </summary>
public enum ColonyMode
{
    /// <summary>
    /// Nobody interferes. The five verbs are refused, no intent carries any weight, and what the
    /// colony becomes is its own. This is the mode the whole project was begun to watch.
    /// </summary>
    Simulation,

    /// <summary>
    /// The verbs are yours, and an intent is a thumb on the scale that the colony's own decisions
    /// can overrule. The default: influence without orders, which is what v1 shipped.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Requests carry real weight — commission a building, ask for work, call a vote. Still a
    /// score and still refusable: someone who is starving eats first, whatever you asked for.
    /// </summary>
    Directed
}
