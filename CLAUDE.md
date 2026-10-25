# Project Emberloom — working rules

*Emberloom: What They Left Running.* A colony sim where every character thinks, remembers and inherits.
See [DESIGN.md](DESIGN.md) for the design and [README.md](README.md) for status and how to run it.

---

## The golden rule

> **Every mind call is self-contained — full context in, JSON out, no server-side state about the
> colony.**

This is not a style preference. It is the single constraint that keeps local play and hosted play
the same code path, and it must hold at all times.

**Why it matters.** The project is going to be hosted, with `Mind:Endpoint` defaulting to
`http://127.0.0.1:11434` so local is simply the default value. That only works while the thing
serving inference knows nothing about any colony. Hold the rule and a hosted deployment is a URL
plus auth plus a budget policy. Break it and hosted becomes a rewrite, because the server would
then need session affinity, colony mirroring, and a migration story for state it was never meant
to own.

**What it forbids.**

- No conversation, session, thread or colonist handle sent to the inference server.
- No server-side memory, index, embedding store, cache keyed by colonist, or "resume where we
  left off".
- No call that only works because a previous call was made to the same server.
- No retrieval that runs anywhere but the client. Vectors are computed by a stateless call
  (text in, vector out); the **index that stores and searches them stays local**. This is the most
  tempting place to break the rule, because moving the index server-side looks like an
  optimisation.

**What it permits.** Anything stateless. Sending a large prompt is fine. Sending the same context
repeatedly is fine — that is what the KV-cache prefix ordering is for. Computing an embedding
remotely is fine, because text goes in and a vector comes back with nothing retained.

**How it is enforced.** `TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing` in
`tests/Emberloom.Tests/KindlingTests.cs` asserts on the request's *shape* — the exact set of
top-level fields — rather than its prose, so it cannot rot as prompts change. Every new call type
should get the same assertion.

**If an instruction would violate this rule — including one from Jeremy — say so before
implementing it.** Name the specific breakage and offer the stateless alternative. Do not
silently comply, and do not silently refuse.

---

## Architecture, in one paragraph

Two layers. **Layer 1, the body** (`Emberloom.Core`) is deterministic, runs every tick, and makes
**zero model calls** — drives, utility scoring, pathing, the relationship ledger, episodic capture.
**Layer 2, the mind** (`Emberloom.Mind`) runs asynchronously and writes durable artifacts the body
then reads on every subsequent tick without inference. `Emberloom.Core` must never reference
`Emberloom.Mind`; that dependency direction is what makes the next rule testable.

**The game must run correctly with no Ollama present at all.** Colonists keep foraging, salvaging,
building and dying; they just stop forming beliefs. Every call type declares a templated fallback,
and the fallback is recorded in the model trace as a fallback so a colony running entirely on stubs
never looks like a colony that thought. `BodyWithoutMindTests` and
`AnUnreachableMindStillProducesAColonistWhoActs` cover this.

---

## Things already learned the hard way

Do not re-derive these.

- **A token cap is a correctness constraint, not a budget hint.** A schema-constrained call that
  hits its cap truncates mid-document and the whole result is unparseable. Caps live in
  `CallBudgets`, one table read by both the benchmark and the game.
- **Reasoning must be disabled** (`think: false`). On a thinking-capable model Ollama charges
  reasoning tokens against `num_predict` but returns them separately, so the cap buys reasoning
  instead of the answer. Measured: a one-word reply cost 38 tokens with thinking on, 2 with it off.
- **A request timeout is not a cancellation.** `TaskCanceledException` derives from
  `OperationCanceledException`; only rethrow when the caller's token is actually cancelled, or the
  likeliest form of an unavailable mind escapes as a fault instead of falling back.
- **Naming a concept in a schema does not explain it.** The model has to be told what the seven
  drives mean in this world, and what magnitudes to use, or it produces flat or floored weights.
- **Personality weight belongs inside the urgency curve, not multiplied onto it.** Applied outside,
  a low weight only defers a drive until it tops out and dominates anyway.
- **Relief is a rate, proportional to remaining need.** Without proportionality the body has no
  negative feedback and every drive pins at 0 or 1.
- **Measure a model's origin before placing it.** The Kenney furniture kit puts every origin at a
  corner (a floor tile spans x 0..1, z −1..0); the city, forest and character kits are centred.
  Treating corner-origin tiles as centred floated walls off the floor and buried rugs inside it.
  Rigged characters also need `SkeletonUtils.clone` — a plain `clone()` collapses them to a point.
- **Benchmark call shapes understate live ones.** A live batch of twelve memories writes longer
  and runs slower than the M0 shapes did. At M4, 13% of reflections on the laptop stopped at the
  448-token cap, and real throughput was a quarter of the benchmark's. Set caps from live traces
  (the inspector's Trace tab shows completion tokens per call), and let the Contemplation readout
  measure served calls rather than trust a config figure.
- **Keep raw numbers out of prompts.** The model reads every figure as something the colonist
  knows and hands it back as a belief ("Teachers who have a valence of +0.35 with me…").
  Feelings and weights go in as words.
- **A decision must not be a pure function of state that the act of deciding changes.** Found four
  times. The first three were votes frozen because nothing could move them. The fourth moved
  *against itself*: a council deliberates in the plaza, `Deliberate` relieves Belonging at 1.20 a
  day, and the Print fork weighs Belonging against Safety — so debating whether the colony was
  lonely enough to want another person cured the loneliness, and seed 4 refused sixteen prints in a
  row. Anything a fork is weighed on is snapshotted when the question is put (`Fork.Pressure`,
  `Fork.Standing`). Check this whenever a new fork axis or a new relief is added.
- **The golden rule's first dividend was a config file.** Moving inference to the devbox's RTX
  5070 is `Mind:Endpoint` and nothing else: run with `ASPNETCORE_ENVIRONMENT=Target` over a tunnel
  (`ssh -N -L 127.0.0.1:11436:127.0.0.1:11434 devbox`). A three-seat vote went from 276.2 seconds
  of inference to **2.0**, and the governor's queue from pinned at its 32-deep cap to zero. The
  tunnel lives as long as its ssh process, and it does drop — use `scripts/devbox-tunnel.sh`,
  which reconnects. Recovery needs no restart: HttpClient reopens the connection and the call
  carries its own context, so the colony is left with a minute of stub votes, marked as stubs.
- **A cap measured against one model is not a cap.** `gemma4:12b` writes a mean of 118 tokens into
  deliberation's 128 where llama3.2:3b writes 52, and loses one vote in six to a truncated
  document. It is also thinking-capable — the first such model actually run here — and reproduces
  M0's trap exactly when `think` is on. Moving the deep lane to a 12B model needs `CallBudgets`
  re-measured with it.
- **A benchmark taken in bursts does not predict a system that runs continuously.** This laptop
  reaches 10 tok/s and holds 1.5–6.5: twenty calls nose to tail decay from 9.94 (first five) to
  6.52 (last five), with individual runs at 1.56. M0 sampled three runs per scenario with gaps, so
  every figure in `CallBudgets` and the governor's ceiling is sized from roughly six times what the
  machine sustains. The game always runs in the sustained regime, which is why a three-seat vote
  costs four real minutes. Re-measure with `bench/probes/sustained-probe.js`; the fix is
  `Mind:Endpoint`, not code.
- **A call's wall time is not its speed.** Ollama's `total_duration` includes every second the
  request spent queued behind another one, so completion tokens over it measures congestion and
  reports it as throughput. Measured: a deliberation's recorded rate was 1.06 tok/s while the same
  model on the same prompt with the same schema generated at 9.4. `ModelCallRecord` now carries
  `LoadSeconds`, `PrefillSeconds` and `GenerateSeconds` as well, and any claim about speed has to
  be made from those. Schema shape, incidentally, costs nothing measurable.
- **An outcome tally cannot find a decision that was never put.** Twelve milestones of measurement
  missed the above because every readout counted what happened. `Emberloom.Sim --forks` counts the
  days the question was *not* asked and names the gate that held it shut. When something "never
  happens", measure the gate, not the outcome.
- **A `<span>` inside a block is `display: inline`, and an inline box has no width or height.**
  Every drive bar in the client measured 0x0 from M1 to M19 — nineteen milestones of looking at
  them — because the number printed beside each bar was right, so the panel read as correct.
- **Embeddings don't separate a paraphrase from a related belief below ~0.92.** On
  nomic-embed-text, "I am useful" / "My usefulness is proven" scored 0.85, below two genuinely
  different beliefs about the Loom. Merging duplicate beliefs is tuned for precision: a missed
  duplicate costs a repeated line, while a wrong merge attaches one belief's evidence to another's
  words.

## Conventions

- Comments explain *why*, especially where a line encodes something learned by measurement.
- Prompts are assembled stable-prefix-first (`WorldPrompt`) so the KV cache covers the expensive part.
- Run `dotnet test` before committing. Client builds into the API's `wwwroot` via `npm run build`.
