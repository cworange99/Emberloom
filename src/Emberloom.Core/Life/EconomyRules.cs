using Emberloom.Core.Actions;

namespace Emberloom.Core.Life;

/// <summary>
/// What a colonist earns and what they can spend it on — M13. The smaller of v2's two economies:
/// the Loom's charge is the colony's, held in common and voted on, and this is the part of the
/// colony's output that belongs to whoever did the work.
/// <para>
/// It exists for one reason. Measured at M12, leisure is chosen 0.0% of the time at every
/// population, because it relieves almost nothing and the body always has something better to do.
/// A venue the colony had to build and the colonist has to pay for is leisure with stakes: it
/// relieves what nothing else reaches, and the price is what stops it being free time nobody
/// notices. If a price can be found that neither never binds nor always binds, the work–life
/// tension in the scope is real; if it cannot, the price is wrong rather than the idea.
/// </para>
/// </summary>
public static class EconomyRules
{
    /// <summary>
    /// What a full colony-day of work pays, in tokens. One, so every other figure here reads as a
    /// fraction of a day's labour and the sweep below has an obvious unit. Nobody works a whole
    /// day: measured, the colony spends 47.8% of its time on work, so a day earns about half a
    /// token and a week earns three and a half.
    /// </summary>
    public const double WagePerWorkDay = 1.0;

    /// <summary>
    /// What an evening at the hall costs. **Measured, not chosen** — see DESIGN-V2.md for the sweep
    /// this came out of. The failure modes either side are both silent: too cheap and the price is
    /// decoration, too dear and the action is dead code that looks implemented.
    /// </summary>
    public const double GatheringPrice = 0.25;

    /// <summary>
    /// What a colony-day of entirely empty stores does to everyone's Sustenance — M15, and the
    /// first time the colony's economy reaches anybody's body. Scaled by how much of the day's draw
    /// went unpaid, so a thin day is felt thinly.
    /// </summary>
    public const double HungerPerEmptyDay = 0.6;

    /// <summary>Whether this is the sort of work that pays. Travel does not, and nor does sleep.</summary>
    public static bool IsPaid(ActionKind kind) => kind switch
    {
        ActionKind.Forage or ActionKind.Salvage or ActionKind.Clear
            or ActionKind.Build or ActionKind.Tend => true,
        _ => false
    };
}
