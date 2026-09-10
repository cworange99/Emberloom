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

// The static-file middleware refuses content types it does not know, and glTF binary is not in
// its default map — without this every model 404s while the page itself loads fine.
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".glb"] = "model/gltf-binary";
contentTypes.Mappings[".gltf"] = "model/gltf+json";

app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

// The graphics spikes and the art kit are served from outside wwwroot on purpose: the client's
// `npm run build` empties wwwroot, and hand-written spike pages living in a folder that a build
// step deletes is a trap I have already walked into once. Both mounts are optional so a published
// build without them still starts.
static void MountIfPresent(WebApplication app, string relative, string requestPath,
    Microsoft.AspNetCore.StaticFiles.IContentTypeProvider types)
{
    var root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", relative));
    if (!Directory.Exists(root))
    {
        return;
    }

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root),
        RequestPath = requestPath,
        ContentTypeProvider = types,
    });
}

MountIfPresent(app, "spikes", "/spike", contentTypes);
MountIfPresent(app, Path.Combine("assets", "kit"), "/kit", contentTypes);

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

    var trace = colonist.Trace
        .Select(t => new TraceRowDto(
            t.CallType,
            Math.Round(t.Day, 2),
            t.Model,
            t.Lane,
            t.PromptTokens,
            t.CompletionTokens,
            Math.Round(t.Seconds, 2),
            t.Produced,
            t.WasFallback))
        .Reverse()
        .ToList();

    // The source rows are resolved here rather than in the client so a belief always arrives with
    // its chain attached. Episodic rows are pruned over time, so a source that has aged out is
    // reported as such rather than silently dropped — a broken link is information.
    MemoryRowDto ResolveSource(string id)
    {
        var found = colonist.Memory.FindEpisodic(id);

        return found is null
            ? new MemoryRowDto(id, 0, "(pruned — too small to keep)", string.Empty, 0, 0, 0)
            : new MemoryRowDto(
                found.Id,
                Math.Round(found.Day, 2),
                found.Describe(),
                found.PlaceId,
                Math.Round(found.Valence, 2),
                Math.Round(found.Intensity, 2),
                Math.Round(found.Salience(colony.Day), 3));
    }

    var consolidated = colonist.Memory.Consolidated
        .OrderByDescending(m => m.Salience(colony.Day))
        .Select(m => new ConsolidatedRowDto(
            m.Id,
            Math.Round(m.Day, 2),
            m.Text,
            Math.Round(m.Valence, 2),
            Math.Round(m.Salience(colony.Day), 3),
            m.RecallCount,
            m.SourceMemoryIds.Select(ResolveSource).ToList()))
        .ToList();

    var beliefs = colonist.Memory.Beliefs
        .OrderByDescending(b => b.Confidence)
        .Select(b => new BeliefRowDto(
            b.Id,
            b.Claim,
            Math.Round(b.FormedOnDay, 2),
            Math.Round(b.Confidence, 2),
            b.RecallCount,
            b.ContradictsColonyMyth,
            b.SourceMemoryIds.Select(ResolveSource).ToList()))
        .ToList();

    return Results.Ok(new ColonistDetailDto(
        ColonistDto.From(colonist, colony),
        colonist.Personality,
        colonist.Seed,
        trace,
        consolidated,
        beliefs,
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
