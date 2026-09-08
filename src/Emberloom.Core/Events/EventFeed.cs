namespace Emberloom.Core.Events;

/// <summary>
/// Which layer produced a line. The feed is colour-coded by this — it is how a viewer can see at
/// a glance how much of what they are watching came from a model and how much from plain code.
/// </summary>
public enum EventLayer
{
    Body,
    Mind,
    World,

    /// <summary>One of the player's five verbs.</summary>
    Player
}

public sealed record ColonyEvent
{
    public required long Tick { get; init; }
    public required double Day { get; init; }
    public required EventLayer Layer { get; init; }
    public required string Text { get; init; }
    public string? ColonistId { get; init; }
    public string? PlaceId { get; init; }

    /// <summary>Raised for the things worth slowing down for. The feed can filter on it.</summary>
    public bool IsNotable { get; init; }

    public override string ToString() => $"d{Day,6:F2}  {Layer,-6}  {Text}";
}

/// <summary>
/// The screen a viewer actually reads. One line per notable thing — it should be watchable the
/// way an aquarium is watchable.
/// </summary>
public sealed class EventFeed
{
    private readonly Queue<ColonyEvent> _events = new();
    private readonly int _capacity;

    public EventFeed(int capacity = 2000)
    {
        _capacity = capacity;
    }

    public event Action<ColonyEvent>? Appended;

    public IReadOnlyCollection<ColonyEvent> Events => _events;

    public void Append(ColonyEvent colonyEvent)
    {
        _events.Enqueue(colonyEvent);
        while (_events.Count > _capacity)
        {
            _events.Dequeue();
        }

        Appended?.Invoke(colonyEvent);
    }

    public IEnumerable<ColonyEvent> Recent(int count) => _events.TakeLast(count);

    public IEnumerable<ColonyEvent> Notable(int count) =>
        _events.Where(e => e.IsNotable).TakeLast(count);
}
