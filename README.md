# Emberloom: What They Left Running

A city the forest took back, a machine still printing minds into it, and nobody left to say why.
Every character thinks, remembers and inherits — on your own hardware, offline.

See [DESIGN.md](DESIGN.md) for the design and [DESIGN-V2.md](DESIGN-V2.md) for version 2's scope,
which is where every measurement behind the balance is recorded. Build order and milestone status
below.

## Status

| Milestone | State |
|---|---|
| **M0** — benchmark before designing further | **Done, both profiles.** See [`bench/results/`](bench/results/). |
| **M1** — the body, with no mind at all | **Done.** Drives, utility scoring, city map, foraging/salvage/building/rest, relationship ledger, episodic capture, event feed, time controls — all with zero model calls — behind an HTTP/WebSocket API, with a React client watching it. |
| **M2** — governor and Kindling | **Done.** Printed colonists get model-written personalities and weights; the budget governor holds a rolling tokens-per-minute ceiling and slows time rather than dropping thinking. |
| **M3** — memory pipeline and Reflection | **Done.** Episodic memories coalesce, consolidate, and produce beliefs that cite the memories they came from — walkable in the inspector. |
| **M4** — the Mind Inspector | **Done.** Five tabs, every chain walkable end to end, and the prompt and response of every recent call kept locally. The Modest decision: the laptop plays via hosted inference and stays a development floor. |
| **M4.5** — the view | **Done.** The 3D city is the main view and rooms open beside the inspector, both live over the socket. The art kits ship by fetch script or installer rather than the repository: decided, and built when packaging comes. |
| **M5** — dialogue and deliberation | **Done.** Colonists speak when they meet, in bubbles over both views, and the words go into their memories. The colony faces two genuine forks — whether to let the Loom print, and whether to go up to a floor nobody has reached — and a council of five decides each one by argued majority vote. |
| **M6** — player verbs | **Done.** All five verbs, none of them an order. Speak to a colonist and they remember it and answer; Print a mind from a seed you write; Provide light, warmth or a way in on the Loom's charge, and Withhold it — they notice; Reveal something of the makers', once. |
| **M7** — generations | **Done.** Bodies age, slow and die; the dead leave cores the colony reads, keeps or destroys; bonded adults have children who carry their weights and dream their memories. Colonists earn names; the colony coins its own words and tells itself a story about its makers, retold as it learns more. The overnight run reached 648 colony days and about sixteen generations, and its myth was judged worth going on with. It also set the pace: at 1× a colony-day is now an hour and a life about three months. |
| **M8** — four founders, and a harness that can see | **Done.** Colonies start at four, and `Emberloom.Sim` became the instrument every balance claim in version 2 is settled with: how a colonist's day actually divides, in colonist-ticks. |
| **M9** — names at birth | **Done.** Everyone is named when they are printed or born, invented in `Emberloom.Core` so a colony with no model still has people rather than designations. The three deeds survive as epithets. |
| **M10** — the world grows | **Done.** Twelve buildable sites, a Build fork that asks about one at a time, and `Build` that finally changes the world — a third of every colonist's life had been going into work that left no mark. |
| **M11** — persistence | **Done.** A colony outlives the process: SQLite, a versioned document, beliefs and their vectors as their own rows. The random stream is saved with it, so a loaded colony lives the same life the saved one would have. |
| **M12** — wellbeing | **Done, read-only.** One number per colonist, integrated from how their days divide. It varies — 0.34 to 0.57 over a lifespan — and it tracks what it claims to: a colony with more company reads higher. |
| **M13** — tokens and the first paid venue | **Done.** Work pays the worker and an evening at a hall costs 0.25 of a day's wage, a price arrived at by sweeping it. Leisure had been chosen 0.0% of the time since the project began; it is 4.3% of the day now. |
| **M14** — the Loom, fed by work | **Done.** The charge stops being a trickle nobody earns. Labour makes it, every mouth draws on it, granaries set what can be kept, and "can we feed another?" is a question with an answer that depends on the colony. |
| **M15** — the couplings | **Done.** An empty store is felt as hunger, a life going badly is a shorter life, and a bed somebody else is in is not a bed. Every life in a struggling colony now runs shorter than it was born to. |
| **M16** — modes and intents | **Done.** Simulation, Hybrid and Directed, chosen at creation and fixed for life — one code path at three strengths, so a research run is provably this same binary. What you ask for weighs; somebody in real need cannot hear you at all. |
| **M17** — rooms that look like the building | **Done.** Furniture that belongs together is placed together: a rug under its table, chairs facing it, nothing standing inside anything else. And `assets/fetch-kits.ps1`, so a fresh clone is not an empty scene. |
| **M18** — the chronicle and the story | **Done.** The record is budgeted in lines rather than colony-days, and what it gives up it summarises. The myth is told what is new since it was last told — retellings keep 76% of their wording now, where 158 of them once came back near word for word. |
| **M19** — the title | **Done.** *Emberloom: What They Left Running*, after a trademark and storefront search. *Project Emberloom* stays as the repository. |
| **Inference on the devbox** | **Done.** `ASPNETCORE_ENVIRONMENT=Target` over an SSH tunnel points the mind at an RTX 5070 instead of this laptop's CPU, and nothing else in the project changes — which is what the golden rule was for. A three-seat vote: 276.2 seconds of inference became 2.0. |
| **Saves, the map, and the names** | **Done.** A colony can be kept under a name and gone back to, from a panel beside *print* — M11 built all of that and never gave it a front door. Place names on the 3D city are a toggle, off by default, with whatever the pointer is over still naming itself; and there is a flat map of the town from above, with every place named, for when you want all of them at once. |
| **After the first play** | **Done.** Playing it found the worst defect in the project: a council deliberates in the plaza, and deliberating relieves the very drive a fork is weighed on, so debating whether the colony wanted another person cured the loneliness and seed 4 refused sixteen prints in a row. A vote is now answered on where people stood when the question was put. Seed 4 went from seven living to the population cap. See [V2-REPORT.md](V2-REPORT.md). |

## Layout

```
src/Emberloom.Core    the body — drives, actions, world, memory, relationships (no model calls)
src/Emberloom.Mind    the mind — Ollama transport, call schemas, token budgets
src/Emberloom.Bench   M0 benchmark harness
src/Emberloom.Sim     headless watcher for the body, the balance sweep, and the fork census
src/Emberloom.Api     HTTP/WebSocket host; also mounts assets/kit/ at /kit
client/               React + TypeScript client, built into the API's wwwroot
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

**Speech.** When two colonists start talking, one Dialogue call writes both lines, and they appear
as bubbles over the pair in whichever view shows them. A conversation you are watching — its place
open, or one of the pair selected — jumps the queue. Lines go into both colonists' memories of the
conversation, so a later reflection can quote them.

**Forks.** Twice the colony has to decide something together: when the Loom has charge to spare
(*do we print another mind?*) and when a stairwell is cleared (*do we go up?* — nobody may, until
it is decided). A council of five, the colonists who feel it most either way, is drawn to the
plaza, and each one's mind writes a stance, a reason and a line aloud; later speakers hear the
earlier ones. A banner over the view shows the argument as it happens. It carries only if a
majority of the whole council votes for it; abstaining keeps things as they are. With no mind at all the fork still gets decided after three
colony-days, on where each member's personality leans, and the banner says no mind answered.

**What a session leaves behind.** Nothing in a colony survives the process — that is by design for
the colony and no use at all for the account of it. So every session keeps its own record in
`chronicles/`, one Markdown file per colony, amended at the end of every colony day and, in
between, whenever something notable has happened and the last amendment is two minutes old. It
opens with where the colony stands now — who is alive, what it believes, the story as it is told —
and then has the days in order. It is written by the body alone: no model call, so a colony running
with no Ollama at all still leaves one. The file is written whole to a scratch name and moved into
place, because the stop it exists for is the one nobody planned. `GET /api/chronicle` returns the
same text without going to find the file, and `Chronicle:Folder` in `appsettings.json` says where
they go.

## The 3D views

The main view is the colony itself, in low-poly 3D (`client/src/city/`):

- **The city** is laid out from the world model's own place positions, live over the socket, and
  colonists walk the streets between places. Click a colonist to read their mind; click a building
  to go inside.
- **A room** is one place's interior, cut away to two walls so nothing stands between the camera
  and the people. Colonists walk in through the door and out again, and each plays an animation
  for what they are doing. Click one to read their mind. **← city** goes back, and so does the
  browser's back button.

The inspector's place links open rooms too, so a belief leads to the memory behind it and on to
the room where it happened.

Both need the art kits in `assets/kit/`, which are deliberately **not in the repository** — see
[`assets/README.md`](assets/README.md). Without them the views draw placeholders and say so.

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
