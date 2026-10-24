namespace Emberloom.Core.World;

/// <summary>
/// The reclaimed city. A graph of places with a breadth-first path between any two — pathing is
/// explicitly one of the things that never gets a model call.
/// </summary>
public sealed class CityMap
{
    private readonly Dictionary<string, Place> _places = [];

    public IReadOnlyCollection<Place> Places => _places.Values;

    public Place this[string id] => _places[id];

    public Place? Find(string id) => _places.GetValueOrDefault(id);

    public void Add(Place place) => _places[place.Id] = place;

    public void Connect(string a, string b)
    {
        if (!_places.TryGetValue(a, out var placeA) || !_places.TryGetValue(b, out var placeB))
        {
            throw new ArgumentException($"Cannot connect unknown places '{a}' and '{b}'.");
        }

        if (!placeA.Neighbours.Contains(b))
        {
            placeA.Neighbours.Add(b);
        }

        if (!placeB.Neighbours.Contains(a))
        {
            placeB.Neighbours.Add(a);
        }
    }

    public IEnumerable<Place> OfKind(PlaceKind kind) => _places.Values.Where(p => p.Kind == kind);

    /// <summary>Places a colonist can currently reach and act in — blocked ones are excluded.</summary>
    public IEnumerable<Place> Reachable(Place from)
    {
        var seen = new HashSet<string> { from.Id };
        var queue = new Queue<Place>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var place = queue.Dequeue();
            yield return place;

            foreach (var neighbourId in place.Neighbours)
            {
                if (!seen.Add(neighbourId) || _places[neighbourId].IsBlocked)
                {
                    continue;
                }

                queue.Enqueue(_places[neighbourId]);
            }
        }
    }

    /// <summary>
    /// The next step from <paramref name="from"/> toward <paramref name="to"/>. Blocked places are
    /// traversable as destinations — you have to stand at a stairwell to clear it — but not through.
    /// </summary>
    public Place? NextStep(Place from, Place to)
    {
        if (from.Id == to.Id)
        {
            return null;
        }

        var cameFrom = new Dictionary<string, string>();
        var seen = new HashSet<string> { from.Id };
        var queue = new Queue<Place>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var place = queue.Dequeue();

            foreach (var neighbourId in place.Neighbours)
            {
                if (!seen.Add(neighbourId))
                {
                    continue;
                }

                cameFrom[neighbourId] = place.Id;

                if (neighbourId == to.Id)
                {
                    return Backtrack(cameFrom, from.Id, to.Id);
                }

                if (!_places[neighbourId].IsBlocked)
                {
                    queue.Enqueue(_places[neighbourId]);
                }
            }
        }

        return null;
    }

    private Place Backtrack(Dictionary<string, string> cameFrom, string startId, string goalId)
    {
        var current = goalId;
        while (cameFrom[current] != startId)
        {
            current = cameFrom[current];
        }

        return _places[current];
    }

    /// <summary>
    /// The starting city. Enough shape to make the seven drives pull in different directions:
    /// generous ground at the bottom, a Loom worth arguing over, and a tower nobody has opened.
    /// </summary>
    public static CityMap CreateDefault()
    {
        var map = new CityMap();

        map.Add(new Place
        {
            Id = "plaza", Name = "The Cracked Plaza", Kind = PlaceKind.Plaza,
            X = 0, Y = 0, Light = 1.0, Danger = 0.02, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "loom", Name = "The Loom Floor", Kind = PlaceKind.Loom,
            X = -30, Y = 10, Light = 0.7, Danger = 0.15, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "shelter-east", Name = "East Shelter", Kind = PlaceKind.Shelter,
            X = 34, Y = -8, Light = 0.8, Danger = 0.0, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "shelter-under", Name = "The Underhouse", Kind = PlaceKind.Shelter,
            X = -12, Y = -34, Light = 0.5, Danger = 0.04, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "orchard", Name = "The Orchard Street", Kind = PlaceKind.Clearing,
            X = 48, Y = 26, Light = 1.0, Danger = 0.0, ForageYield = 0.11, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "green-roof", Name = "The Green Roof", Kind = PlaceKind.Clearing,
            X = 34, Y = 56, Level = 2, Light = 1.0, Danger = 0.08, ForageYield = 0.09, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "river-edge", Name = "The River Edge", Kind = PlaceKind.Clearing,
            X = -46, Y = 40, Light = 1.0, Danger = 0.05, ForageYield = 0.13, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "market-hall", Name = "The Market Hall", Kind = PlaceKind.Ruin,
            X = 20, Y = -30, Light = 0.6, Danger = 0.12, SalvageRemaining = 4, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "substation", Name = "The Substation", Kind = PlaceKind.Ruin,
            X = -54, Y = -6, Light = 0.35, Danger = 0.34, SalvageRemaining = 3, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "tower-lobby", Name = "Tower Lobby", Kind = PlaceKind.Ruin,
            X = 2, Y = 32, Light = 0.55, Danger = 0.18, SalvageRemaining = 2, IsExplored = true
        });

        map.Add(new Place
        {
            Id = "east-stair", Name = "The East Stairwell", Kind = PlaceKind.Stairwell,
            X = -18, Y = 54, Level = 1, Light = 0.2, Danger = 0.42,
            ClearingRequired = 260, IsExplored = false
        });

        map.Add(new Place
        {
            Id = "ninth-floor", Name = "The Ninth Floor", Kind = PlaceKind.Ruin,
            X = -34, Y = 76, Level = 9, Light = 0.3, Danger = 0.5,
            SalvageRemaining = 5, ClearingRequired = 120, IsExplored = false
        });

        // ---- ground the colony can build on (M10) ------------------------------------------
        //
        // Every site exists from the first tick, as marked ground nobody has agreed to yet. That is
        // deliberate and it is what makes growth cheap: the per-tick snapshot already streams every
        // place, the city scene already toggles per-place visuals without rebuilding, and a site has
        // a room spec authored up front like anywhere else. Adding places at runtime would need a
        // push path and an incremental scene that do not exist, to show something less legible than
        // scaffolding that fills in.
        //
        // EVERY SITE MUST BE A GRAPH LEAF. Reachable() walks neighbours, so a site standing between
        // two built places would put itself on the only route between them. They are leaves, and
        // they are never blocked, so neither can happen — keep it that way.
        foreach (var (id, name, becomes, x, y, work, attachedTo) in new[]
        {
            ("site-row-north", "The North Row", PlaceKind.Dwelling, 10.0, 18.0, 12000.0, "plaza"),
            ("site-hall-plaza", "The Plaza Hall", PlaceKind.Hall, -14.0, 14.0, 18000.0, "plaza"),
            ("site-row-market", "The Market Row", PlaceKind.Dwelling, 28.0, -44.0, 12000.0, "market-hall"),
            ("site-hall-east", "The East Hall", PlaceKind.Hall, 50.0, -18.0, 18000.0, "shelter-east"),
            ("site-row-river", "The River Row", PlaceKind.Dwelling, -60.0, 52.0, 12000.0, "river-edge"),
            ("site-granary-river", "The River Store", PlaceKind.Granary, -56.0, 28.0, 15000.0, "river-edge"),
            ("site-row-orchard", "The Orchard Row", PlaceKind.Dwelling, 62.0, 36.0, 12000.0, "orchard"),
            ("site-granary-orchard", "The Orchard Store", PlaceKind.Granary, 58.0, 14.0, 15000.0, "orchard"),
            ("site-hall-under", "The Deep Hall", PlaceKind.Hall, -24.0, -46.0, 18000.0, "shelter-under"),
            ("site-granary-under", "The Underhouse Store", PlaceKind.Granary, -2.0, -48.0, 15000.0, "shelter-under"),
            ("site-row-tower", "The Tower Row", PlaceKind.Dwelling, 14.0, 44.0, 12000.0, "tower-lobby"),
            ("site-hall-loom", "The Loomside Hall", PlaceKind.Hall, -44.0, 4.0, 18000.0, "loom")
        })
        {
            map.Add(new Place
            {
                Id = id, Name = name, Kind = PlaceKind.Site, Becomes = becomes,
                X = x, Y = y, Light = 1.0, Danger = 0.0,
                BuildRequired = work, IsExplored = true
            });

            map.Connect(attachedTo, id);
        }

        map.Connect("plaza", "loom");
        map.Connect("plaza", "shelter-east");
        map.Connect("plaza", "shelter-under");
        map.Connect("plaza", "orchard");
        map.Connect("plaza", "market-hall");
        map.Connect("plaza", "tower-lobby");
        map.Connect("loom", "substation");
        map.Connect("loom", "river-edge");
        map.Connect("orchard", "green-roof");
        map.Connect("market-hall", "shelter-east");
        map.Connect("tower-lobby", "green-roof");
        map.Connect("tower-lobby", "east-stair");
        map.Connect("east-stair", "ninth-floor");

        return map;
    }
}
