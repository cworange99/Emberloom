using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;

namespace Emberloom.Core.Life;

/// <summary>
/// How a colonist's life is going, as one number — M12. Deliberately read-only at this milestone:
/// nothing scores it, nothing dies of it, nothing is gated on it. It exists so the question "does
/// this vary between people at all?" can be answered before anything depends on the answer.
/// <para>
/// It is a leaky integrator, not an accumulator. Each tick the body works out what that tick was
/// like to live — <see cref="Moment"/> — and wellbeing drifts toward it. That shape was chosen over
/// adding and subtracting rates for two reasons. It cannot run away, so there is no pair of
/// constants whose balance decides whether every colonist pins at 0 or at 1; and its resting value
/// is the time-weighted average of a colonist's own days, so two people differ exactly insofar as
/// their days differ. A number everyone shares would have no game in it.
/// </para>
/// </summary>
public static class WellbeingRules
{
    /// <summary>
    /// Where a new colonist starts. Above the measured resting value on purpose: whatever else is
    /// true of being printed into a ruin, it has not worn anybody down yet.
    /// </summary>
    public const double Start = 0.6;

    /// <summary>
    /// How fast wellbeing follows the life being lived, as a half-life in colony days. Five days is
    /// slow enough that one bad afternoon is not a crisis and fast enough that a fortnight of
    /// nothing but work shows. M15's "sustained low wellbeing" then means weeks, as it should.
    /// </summary>
    public const double HalfLifeDays = 5.0;

    /// <summary>
    /// What the drive pressing hardest takes off the moment. Urgency is already squared, so this
    /// bites late and hard rather than evenly — which is the intent: a colonist with everything
    /// merely unsatisfied is fine, and a colonist with one thing desperately unmet is not.
    /// </summary>
    public const double PressureBite = 0.5;

    /// <summary>
    /// What a tick spent this way is worth before the drives are weighed, in [0,1].
    /// <para>
    /// The ordering is the claim this milestone makes about the game, so it is worth stating
    /// plainly: company is the best of it, sleep and idleness restore less than company, the work
    /// that keeps everyone alive is the cost of living, and walking across a ruined city to get to
    /// it is the worst part of the day. Leisure sits just under company rather than above it
    /// because M13's venue is a *social* venue — the point of buying leisure is to buy each other.
    /// </para>
    /// </summary>
    public static double Ease(ActionKind? kind) => kind switch
    {
        // The best hour of the day, and the colony had to build the room and earn the token to
        // have it. If an evening at the hall were not the high point there would be no reason to
        // pay for one.
        ActionKind.Gather => 1.00,

        ActionKind.Talk or ActionKind.Teach => 0.95,
        ActionKind.Wander => 0.85,
        ActionKind.Rest => 0.80,
        ActionKind.Deliberate => 0.55,

        // Doing nothing is not resting. It is what is left when nothing was worth choosing.
        null => 0.45,

        // Forage, Salvage, Clear, Build, Tend — the whole of the colony's working life, which at
        // M12 is a little under half of every colonist's existence.
        _ => 0.35
    };

    /// <summary>Walking there is not doing it, and the city is large and broken.</summary>
    public const double TravellingEase = 0.40;

    /// <summary>
    /// What this one tick is like to live, in [0,1]: what they are doing, less whatever is pressing
    /// on them hardest while they do it.
    /// </summary>
    public static double Moment(Colonist colonist, bool travelling)
    {
        var ease = travelling ? TravellingEase : Ease(colonist.Current?.Definition.Kind);

        var dominant = colonist.Drives.Dominant(colonist.Weights);
        var urgency = colonist.Drives.Urgency(dominant, colonist.Weights[dominant]);

        return Math.Clamp(ease - Math.Min(urgency * PressureBite, MostPressureCanTake), 0, 1);
    }

    /// <summary>
    /// The floor under the pressure term, and the damper on M15's one feedback loop.
    /// <para>
    /// Urgency is <c>(level × weight)²</c>, so it runs to 4 rather than to 1, and without a cap the
    /// pressure term can be worth more than the entire hour it is discounting. That is not an
    /// abstract worry: Talk needs a partner and a gathering needs a hall, so the last two people in
    /// a dying colony can never relieve Belonging at all, their urgency pins above 2, and every
    /// moment of their lives reads as exactly zero. Measured at M15 over 4000 days, that was enough
    /// to make the despair coupling bistable — at 1.5 days lost per day a colony of twelve fell to
    /// two with mean wellbeing 0.162, and at 12 it went extinct — because a lonely survivor's life
    /// shortened as fast as the cap allowed and each death made the rest lonelier.
    /// </para>
    /// <para>
    /// Capping it says something defensible and keeps every measured figure above intact, since a
    /// colonist under ordinary pressure is nowhere near the cap: however badly you need something,
    /// the hour you spend asleep is still sleep.
    /// </para>
    /// </summary>
    public const double MostPressureCanTake = 0.30;

    /// <summary>
    /// Moves a colonist's wellbeing one tick toward the life they are living. Called once per
    /// colonist per tick and nothing reads the result but the inspector — see the type comment.
    /// </summary>
    public static void Advance(Colonist colonist, bool travelling, double dayPerTick)
    {
        // Converted from a half-life so the constant above stays something you can reason about in
        // days rather than a per-tick decay nobody can picture.
        var rate = 1 - Math.Pow(0.5, dayPerTick / HalfLifeDays);

        colonist.Wellbeing += (Moment(colonist, travelling) - colonist.Wellbeing) * rate;
    }

    /// <summary>
    /// Below this, a life is being shortened — M15. Set at the bottom of the band the colony
    /// measured into at M12 and M13 (0.40 to 0.50), so an ordinary hard life does not kill anybody
    /// and a plainly bad one does.
    /// </summary>
    public const double DespairBelow = 0.38;

    /// <summary>
    /// How many days a life loses for each colony-day spent a full point below
    /// <see cref="DespairBelow"/> — **measured, not chosen**; see DESIGN-V2.md for the sweep.
    /// <para>
    /// This is the one number in v2 with a feedback loop behind it. A death aggravates Belonging by
    /// 0.35 in everyone close, the largest single aggravation in the codebase, which lowers their
    /// wellbeing, which shortens their lives, which brings the next death forward. Nothing damps
    /// that, so the sweep was looking for the value at which a colony flips from "stable with
    /// occasional early deaths" to extinct, and for whether that edge is a slope or a cliff.
    /// </para>
    /// </summary>
    /// <para>
    /// Settable rather than const for the same reason ActionCatalog.ReliefScale is: the sweep that
    /// decided it has to be able to turn it. The game never writes it.
    /// </para>
    public static double DespairDaysPerDay { get; set; } = 6.0;

    /// <summary>
    /// The most of a life despair can take, as a fraction of what it would have been. Despair makes
    /// someone go early; it is not a second way to die. At 0.4 the worst possible life still runs
    /// three fifths of its course, <c>AdvanceLives</c> keeps its single death trigger, and the
    /// chronicle's "42 of about 46 days" line stays truthful.
    /// </summary>
    public const double MostOfALifeDespairCanTake = 0.4;

    /// <summary>
    /// What this colony-day of living like this costs the rest of the life, in days. Zero for
    /// anybody who is merely having a hard time.
    /// </summary>
    public static double DaysLostIn(double wellbeing, double days) =>
        wellbeing >= DespairBelow ? 0 : (DespairBelow - wellbeing) * DespairDaysPerDay * days;

    /// <summary>
    /// The number as a word, for the feed and the inspector. Bands rather than a scale because a
    /// colonist does not know their own wellbeing to two decimal places, and because the chronicle
    /// reads better for it.
    /// </summary>
/// <para>
    /// The thresholds are read off M12's own measurement rather than guessed at, which is why they
    /// are not evenly spaced: over a full lifespan at four founders the colony lives between 0.34
    /// and 0.57 and averages 0.41, so a scale pitched at the middle of [0,1] would have called
    /// every single colonist the same word. "Flourishing" and "failing" are deliberately out of
    /// reach at this milestone — M13's venues, M14's stores and M15's beds are what should bring
    /// them into it, and these bands are how that will be visible. **Re-read them against a fresh
    /// measurement after each of those.**
    /// </para>
    public static string Describe(double wellbeing) => wellbeing switch
    {
        >= 0.60 => "flourishing",
        >= 0.48 => "well",
        >= 0.36 => "coping",
        >= 0.25 => "worn",
        _ => "failing"
    };
}
