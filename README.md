# What They Left Running

A city the forest took back, a machine still printing minds into it, and nobody left to say why.
Every character thinks, remembers and inherits — on your own hardware, offline.

See [DESIGN.md](DESIGN.md) for the full design. Build order and milestone status below.

## Status

| Milestone | State |
|---|---|
| **M0** — benchmark before designing further | **Done** on the modest profile. See [`bench/results/`](bench/results/). Target profile (RTX 5070) still to run. |
| **M1** — the body, with no mind at all | **Core done.** Drives, utility scoring, city map, foraging/salvage/building/rest, relationship ledger, episodic capture, event feed, time controls. Zero model calls. HTTP/WebSocket API and React client not yet built. |
| M2 — governor and Kindling | Not started |
| M3 — memory pipeline and Reflection | Not started |
| M4 — the Mind Inspector | Not started |
| M5 — dialogue and deliberation | Not started |
| M6 — player verbs | Not started |
| M7 — generations | Not started |

## Layout

```
src/Emberloom.Core    the body — drives, actions, world, memory, relationships (no model calls)
src/Emberloom.Mind    the mind — Ollama transport, call schemas, token budgets
src/Emberloom.Bench   M0 benchmark harness
src/Emberloom.Sim     headless watcher for the body, and the balance sweep
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

On the modest profile (Core 7 150U, no GPU, 15.7 GB):

- **4.5–5.0 sustained calls/min**, against the design's 4–6 estimate. Confirmed.
- **~8–10 tok/s** on a 3B deep lane, against the design's ~10. Confirmed.
- **~16–18 tok/s** on a 1B fast lane, against the design's ~30. **Roughly half the estimate.**
- Every schema failure was the token cap truncating mid-document, not the model getting it wrong.
  A schema-constrained call that hits its cap does not degrade gracefully — the whole result is
  unparseable. Caps are a correctness constraint, not a budget hint. Revised in
  `CallBudgets`; reflection needed 448 rather than the design's ~200.
- Ollama returns 500 trying to hold a third model resident on this machine.
