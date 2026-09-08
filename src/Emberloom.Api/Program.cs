using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Emberloom.Api;
using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Relationships;
using Emberloom.Core.Simulation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SimulationHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationHost>());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
          .AllowAnyHeader()
          .AllowAnyMethod()));

var app = builder.Build();

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

var sim = app.Services.GetRequiredService<SimulationHost>();

// ---- world and state --------------------------------------------------------

app.MapGet("/api/world", () => sim.Read((colony, _) => new WorldDto(
    colony.Map.Places.Select(PlaceDto.From).ToList(),
    Emberloom.Core.Drives.DriveTable.SchemaKeys,
    SimulationClock.AllSpeeds.Select(s => s.ToString()).ToList())));

app.MapGet("/api/state", () => sim.Snapshot());

app.MapGet("/api/events", (int? take) => sim.Read((colony, _) =>
    colony.Feed.Recent(Math.Clamp(take ?? 200, 1, 2000)).Select(EventDto.From).ToList()));

// ---- one colonist, in full --------------------------------------------------

app.MapGet("/api/colonists/{id}", (string id) => sim.Read<IResult>((colony, body) =>
{
    var colonist = colony.Find(id);
    if (colonist is null)
    {
        return Results.NotFound();
    }

    var memories = colonist.Memory
        .MostSalient(colony.Day, 12)
        .Select(m => new MemoryRowDto(
            m.Id,
            Math.Round(m.Day, 2),
            m.Describe(),
            m.PlaceId,
            Math.Round(m.Valence, 2),
            Math.Round(m.Intensity, 2),
            Math.Round(m.Salience(colony.Day), 3)))
        .ToList();

    var relationships = colony.Relationships
        .Outgoing(colonist.Id)
        .Where(r => r.SharedMemories > 0 || Math.Abs(r.Affinity) > 0.01)
        .OrderByDescending(r => r.Affinity + r.Trust)
        .Select(r => new RelationshipRowDto(
            r.ToId,
            colony.Find(r.ToId)?.DisplayName ?? r.ToId,
            Math.Round(r.Affinity, 2),
            Math.Round(r.Trust, 2),
            Math.Round(r.Respect, 2),
            r.SharedMemories,
            r.Stage.ToString(),
            r.Narrative))
        .ToList();

    // The runners-up in this tick's scoring. Free, because the scorer already ranks every option
    // — and it is the honest version of the inspector's "chosen over forage (0.31)".
    var options = body.Scorer
        .Score(colonist, colony)
        .Take(6)
        .Select((o, i) => new ScoredOptionDto(
            o.Definition.Kind.ToString(),
            o.Target.Name,
            o.PartnerId,
            Math.Round(o.Score, 4),
            i == 0))
        .ToList();

    return Results.Ok(new ColonistDetailDto(
        ColonistDto.From(colonist, colony),
        colonist.Personality,
        memories,
        relationships,
        options));
}));

// ---- time controls ----------------------------------------------------------

app.MapPost("/api/speed", (SpeedRequest request) =>
{
    if (!Enum.TryParse<GameSpeed>(request.Speed, ignoreCase: true, out var speed))
    {
        return Results.BadRequest(new { error = $"Unknown speed '{request.Speed}'." });
    }

    sim.Clock.SetSpeed(speed);
    return Results.Ok(sim.ClockSnapshot());
});

app.MapPost("/api/auto-slow", (AutoSlowRequest request) =>
{
    sim.Clock.AutoSlowOnEvent = request.Enabled;
    return Results.Ok(sim.ClockSnapshot());
});

app.MapPost("/api/restart", (RestartRequest request) =>
{
    sim.Restart(Math.Clamp(request.Colonists ?? 8, 1, 60), request.Seed ?? Random.Shared.Next());
    return Results.Ok(sim.Snapshot());
});

// ---- live stream ------------------------------------------------------------

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var (id, reader) = sim.Subscribe();

    try
    {
        // Prime the client with the map and the current state before streaming deltas.
        await Send(socket, new { type = "world", payload = sim.Read((colony, _) => new WorldDto(
            colony.Map.Places.Select(PlaceDto.From).ToList(),
            Emberloom.Core.Drives.DriveTable.SchemaKeys,
            SimulationClock.AllSpeeds.Select(s => s.ToString()).ToList())) }, json, context.RequestAborted);

        await Send(socket, new { type = "state", payload = sim.Snapshot() }, json, context.RequestAborted);

        foreach (var backfill in sim.Read((colony, _) => colony.Feed.Recent(120).Select(EventDto.From).ToList()))
        {
            await Send(socket, new { type = "event", payload = backfill }, json, context.RequestAborted);
        }

        await foreach (var message in reader.ReadAllAsync(context.RequestAborted))
        {
            await Send(socket, message, json, context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client went away.
    }
    catch (WebSocketException)
    {
        // Socket closed mid-write; nothing to recover.
    }
    finally
    {
        sim.Unsubscribe(id);
    }
});

app.Run();

static async Task Send(WebSocket socket, object message, JsonSerializerOptions options, CancellationToken ct)
{
    if (socket.State != WebSocketState.Open)
    {
        return;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, options);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
}

internal sealed record SpeedRequest(string Speed);
internal sealed record AutoSlowRequest(bool Enabled);
internal sealed record RestartRequest(int? Colonists, int? Seed);
