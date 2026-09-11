| **M1** — the body, with no mind at all | **Done.** Drives, utility scoring, city map, foraging/salvage/building/rest, relationship ledger, episodic capture, event feed, time controls, HTTP/WebSocket API and React client. Zero model calls. |
# What They Left Running

A city the forest took back, a machine still printing minds into it, and nobody left to say why.
Every character thinks, remembers and inherits — on your own hardware, offline.

See [DESIGN.md](DESIGN.md) for the full design. Build order and milestone status below.

## Status

| Milestone | State |
|---|---|
| **M0** — benchmark before designing further | **Done, both profiles.** See [`bench/results/`](bench/results/). |
| **M1** — the body, with no mind at all | **Done.** Drives, utility scoring, city map, foraging/salvage/building/rest, relationship ledger, episodic capture, event feed, time controls — all with zero model calls — behind an HTTP/WebSocket API, with a React client watching it. |
| **M2** — governor and Kindling | **Done.** Printed colonists get model-written personalities and weights; the budget governor holds a rolling tokens-per-minute ceiling and slows time rather than dropping thinking. |
| **M3** — memory pipeline and Reflection | **Done.** Episodic memories coalesce, consolidate, and produce beliefs that cite the memories they came from — walkable in the inspector. |
| **M4** — the Mind Inspector | **Done.** Five tabs, every chain walkable end to end, and the prompt and response of every recent call kept locally. The Modest decision: the laptop plays via hosted inference and stays a development floor. |
| **M4.5** — the view | **In progress.** The 3D city and rooms from the graphics spike, becoming the app's real presentation. |
| M5 — dialogue and deliberation | Not started |
| M6 — player verbs | Not started |
| M7 — generations | Not started |

## Layout

```
src/Emberloom.Core    the body — drives, actions, world, memory, relationships (no model calls)
src/Emberloom.Mind    the mind — Ollama transport, call schemas, token budgets
src/Emberloom.Bench   M0 benchmark harness
src/Emberloom.Sim     headless watcher for the body, and the balance sweep
src/Emberloom.Api     HTTP/WebSocket host; also mounts spikes/ and assets/kit/
client/               React + TypeScript client, built into the API's wwwroot
spikes/               the 3D views — hand-written, served at /spike
assets/               the Kenney CC0 art kits, served at /kit (see assets/README.md)
tests/Emberloom.Tests including the day-one test that the body runs with no Ollama at all
```

## Running things

Watch a colony for 30 colony-days with no model involved at all:

```bash
dotnet run --project src/Emberloom.Sim -- --days 30 --colonists 8
```

Summary only, no event feed:

```bash
dotnet run --project src/Emberloom.Sim -- --days 25 --diagnose
```

Sweep the one balance knob and see where the drives settle:

```bash
dotnet run --project src/Emberloom.Sim -- --days 25 --sweep
```

Benchmark this machine (M0). Every throughput figure in the design is sized from this:

```bash
dotnet run --project src/Emberloom.Bench -c Release -- --models llama3.2:3b,qwen2.5:3b --fast llama3.2:1b --runs 3
```

On the target profile:

```bash
dotnet run --project src/Emberloom.Bench -c Release -- -m qwen3:14b -f llama3.2:3b -r 5
```

Tests:

```bash
dotnet test
```

## What M0 found

### Target — RTX 5070, i7-14700K, 12 GB VRAM

- **20.4 sustained calls/min**, against the design's ~20 estimate. Confirmed.
- **61–72 tok/s** on a `gemma4:12b` Q4_K_M deep lane, against an estimated 40–60. Better than planned.
- **152–211 tok/s** on a `llama3.2:3b` fast lane, once it is on the GPU rather than beside it.
- **0 of 18 schema failures.**
- **Both lanes fully resident in VRAM** at 4096 context — 6.87 GiB + 1.88 GiB of 11.9 GiB, about
  1.3 GB spare. The residency claim holds for a 12B pairing; a true 14B would be very tight, so
  **12B is the safer deep-lane target than 14B**.
- Gap to the modest profile is **4.25x**, close to the estimated 5x.

**The trap worth knowing about: thinking mode.** On a reasoning-capable model, Ollama charges
reasoning tokens against `num_predict` but returns them separately, so the token cap buys thinking
rather than output. The first Target run had every schema call truncated and every dialogue line
empty — 96 tokens generated, nothing returned. `think: false` fixed all 18. That run is kept as
`m0-target-THINKING-ENABLED-*` for comparison.

### Modest — Core 7 150U, no GPU, 15.7 GB

- **4.5–5.0 sustained calls/min**, against the design's 4–6 estimate. Confirmed.
- **~8–10 tok/s** on a 3B deep lane, against the design's ~10. Confirmed.
- **~16–18 tok/s** on a 1B fast lane, against the design's ~30. **Roughly half the estimate.**
- Every schema failure was the token cap truncating mid-document, not the model getting it wrong.
  A schema-constrained call that hits its cap does not degrade gracefully — the whole result is
  unparseable. Caps are a correctness constraint, not a budget hint. Revised in
  `CallBudgets`; reflection needed 448 rather than the design's ~200.
- Ollama returns 500 trying to hold a third model resident on this machine.
- **Live, at M4**, real reflections averaged ~54 s each (951 tokens in, 328 out). That is about
  1.1 calls a minute, a quarter of the benchmark figure. 13% of them hit the old 448-token
  Reflection cap and were thrown away; the cap is now 576. On this evidence the laptop plays
  through hosted inference and stays a development floor (DESIGN §2).

## Watching it

The client builds into the API's `wwwroot`, so the whole thing runs as one process:

```bash
cd client && npm install && npm run build
```

```bash
dotnet run --project src/Emberloom.Api --urls http://localhost:5223
```

Then open http://localhost:5223. For client development with hot reload, run the API and
`npm run dev` in `client/` side by side — Vite proxies `/api` and `/ws` through to port 5223.

The screen shows the city, the event feed colour-coded by which layer produced each line, the
roster, and a first cut of the Mind Inspector — including the runners-up in each colonist's
utility scoring, which is the honest version of the design's "chosen over forage (0.31)".

The **Contemplation readout** beside the speed control is §6's arithmetic: how many deep calls
the colony can afford per colony-day at the current speed. It starts from the benchmark figure in
`appsettings.json`, then switches to a measured one once three deep calls have been served,
because the two differ a lot. On this laptop the benchmark said 4.8 calls/min and live
reflections came in at about 1.1. Hover the readout to see which figure it is using.

## The 3D views

From M4.5 the city is the app's main view: `client/src/city/`, laid out from the world model's own
place positions and live over the socket. Click a colonist to read their mind; click a building to
look inside. The room view is still the standalone page below until M4.5's second step brings it
into the app, and the spike city page is superseded by the app.

The spike pages are served straight from `spikes/` rather than `wwwroot` — the client build empties
`wwwroot`, and hand-written pages should not live somewhere a build step deletes. They read the live
colony from `/api/state`; they do not write to it.

- **http://localhost:5223/spike/spike3d.html** — the city. Twelve named places among the infill
  blocks, the forest coming back through the streets, real day/night lighting. Hovering names a
  place; clicking a building or a colonist opens that place's interior.
- **http://localhost:5223/spike/room.html** — one interior, cut away to two walls so nothing stands
  between the camera and the people. Colonists play an animation matched to what they are actually
  doing. Arriving from a colonist click marks them with a gold ring and an overhead pin.

Both need the art kits in `assets/kit/`, which are **not in the repo** — see
[`assets/README.md`](assets/README.md) for what to download and where to put it. Without them the
pages load and stay empty; with the kits but without each kit's `Textures/` folder, every model
renders pure white.

Sizing is deliberate and differs between the views. A Kenney character is 0.67 units tall and a
city house is 1.29, so in the city colonists are scaled 1.2× or they disappear among the buildings;
in a room, where a chair is 0.47 and a wall 1.29, everything is drawn at true size. That is the
whole reason the room view exists.

### Benchmarking a remote machine

The harness can drive an Ollama on another box, which avoids installing the toolchain there. Open a
tunnel — note `127.0.0.1` rather than `localhost`, since the latter resolves to IPv6 and Ollama
binds IPv4 only:

```bash
ssh -N -L 127.0.0.1:11436:127.0.0.1:11434 devbox
```

Then point the benchmark at it, supplying the remote machine's details so the report is not
labelled with the wrong hardware:

```bash
dotnet run --project src/Emberloom.Bench -c Release -- --endpoint http://127.0.0.1:11436 --models gemma4:12b --fast llama3.2:3b --runs 3 --profile target --cpu "Intel Core i7-14700K" --gpu "NVIDIA GeForce RTX 5070" --ram 31.8 --cores 28
```

A tunnel adds roughly 50–150 ms per call to wall time, which makes the resulting calls/min slightly
pessimistic. It does not affect tok/s, which comes from Ollama's own counters.
