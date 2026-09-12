namespace Emberloom.Core.Verbs;

/// <summary>What the player can keep running on the Loom's charge, paid for by the day.</summary>
public enum ProvisionKind
{
    Light,
    Warmth
}

/// <summary>What Provide can route charge into. A door is paid for once; light and warmth by the day.</summary>
public enum ProvideKind
{
    Light,
    Warmth,
    Door
}

public sealed record Provision(ProvisionKind Kind, string PlaceId, double CostPerDay, double SinceDay);

public static class ProvisionRules
{
    /// <summary>
    /// A day's light, or a day's warmth. Against a trickle of 3.2 a day, two of them leave barely
    /// enough to print again — which is the point: Provide and Print draw on the same charge, and
    /// every lamp kept burning is a mind not printed.
    /// </summary>
    public const double LightPerDay = 1.0;

    public const double WarmthPerDay = 1.0;

    /// <summary>A door that will not open, opened. Paid once, and dear.</summary>
    public const double DoorCost = 15;

    /// <summary>Rest in a shelter kept warm relieves this much more, and is scored that much better.</summary>
    public const double WarmRestBonus = 1.5;

    public static double CostPerDay(ProvisionKind kind) =>
        kind == ProvisionKind.Light ? LightPerDay : WarmthPerDay;

    public static string Word(ProvisionKind kind) =>
        kind == ProvisionKind.Light ? "light" : "warmth";
}
