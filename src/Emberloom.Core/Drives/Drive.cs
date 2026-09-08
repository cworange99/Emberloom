namespace Emberloom.Core.Drives;

/// <summary>
/// The seven drives every colonist runs on. Each sits at 0.0–1.0 and decays on its own clock.
/// The physical drives at the top of this list decay slowly on purpose: in a reclaimed city the
/// bottom of the stack is nearly solved, and the colony lives at the top of it.
/// </summary>
public enum Drive
{
    Sustenance = 0,
    Rest = 1,
    Safety = 2,
    Belonging = 3,
    Standing = 4,
    Meaning = 5,

    /// <summary>
    /// The load-bearing drive. With survival solved, Wonder is what moves the colony into the
    /// parts of the city it should not go, and what generates the beliefs that calcify into myth.
    /// </summary>
    Wonder = 6
}

public static class DriveTable
{
    public static readonly IReadOnlyList<Drive> All =
    [
        Drive.Sustenance,
        Drive.Rest,
        Drive.Safety,
        Drive.Belonging,
        Drive.Standing,
        Drive.Meaning,
        Drive.Wonder
    ];

    public const int Count = 7;

    /// <summary>Per-colony-day decay rates, from the design's drive table.</summary>
    private static readonly IReadOnlyDictionary<Drive, double> DecayPerDay = new Dictionary<Drive, double>
    {
        [Drive.Sustenance] = 0.12,
        [Drive.Rest] = 0.30,
        [Drive.Safety] = 0.18,
        [Drive.Belonging] = 0.22,
        [Drive.Standing] = 0.26,
        [Drive.Meaning] = 0.24,
        [Drive.Wonder] = 0.28
    };

    private static readonly IReadOnlyDictionary<Drive, string> RisesWhen = new Dictionary<Drive, string>
    {
        [Drive.Sustenance] = "always, slowly",
        [Drive.Rest] = "working, climbing, arguing",
        [Drive.Safety] = "unstable ruins, live machinery, dark levels, injury",
        [Drive.Belonging] = "isolation, rejection, being overruled",
        [Drive.Standing] = "being ignored; watching others take credit",
        [Drive.Meaning] = "repetition, idleness, loss",
        [Drive.Wonder] = "unexplored levels, unopened doors, artifacts nobody understands"
    };

    private static readonly IReadOnlyDictionary<Drive, string> SatisfiedBy = new Dictionary<Drive, string>
    {
        [Drive.Sustenance] = "foraging; the forest is generous",
        [Drive.Rest] = "sleeping in a claimed shelter",
        [Drive.Safety] = "cleared ground, light, proximity to trusted others",
        [Drive.Belonging] = "conversation, shared meals, being agreed with",
        [Drive.Standing] = "being deferred to, naming things, winning a vote",
        [Drive.Meaning] = "finishing a build, teaching, ritual, being needed",
        [Drive.Wonder] = "salvage, reaching somewhere new, a theory that holds"
    };

    public static double DecayRatePerDay(Drive drive) => DecayPerDay[drive];

    public static string Describe(Drive drive) => drive.ToString().ToLowerInvariant();

    public static string RisesWhenDescription(Drive drive) => RisesWhen[drive];

    public static string SatisfiedByDescription(Drive drive) => SatisfiedBy[drive];

    /// <summary>The lowercase drive names, in canonical order. Used as JSON schema keys.</summary>
    public static IReadOnlyList<string> SchemaKeys { get; } =
        All.Select(Describe).ToList();
}
