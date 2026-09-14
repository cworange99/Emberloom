# What They Left Running

*Design plan, draft 2 — 7 September 2026. Ported into the repo from the plan artifact.*

A city the forest took back, a machine still printing minds into it, and nobody left to say why.
Every character thinks, remembers and inherits — on your own hardware, offline.

| | |
|---|---|
| Lineage | Creatures (1996), 30 years on |
| Brain | Local Ollama |
| Reuses | Morpheus character stack |
| Stack | Headless .NET 8 sim behind HTTP/WebSocket + React/TS client |

---

## The one-paragraph version

A city, abandoned long enough that the forest has come back up through it. Somewhere inside, a
machine called the **Loom** is still running on trickle charge, still printing one mind at a time
into bodies it builds from what's around it. Nobody told it to stop. The colonists it makes wake
into a world that is green, abundant, and full of the belongings of people who are not there — and
their real problem was never food. It's what any of it *meant*. They forage, salvage, argue, pair,
print successors and die, and what they believed passes to whoever comes next. Six generations in,
the colony has a founding story about its makers that is entirely wrong and universally believed,
and you can trace it back to the exact thing that actually happened.

### Three pillars

- **Not a commander.** You print, speak, reveal, provide and withhold. You never give an order. The
  colony is something you raise, not something you operate.
- **Cheap bodies, costly minds.** Every moment-to-moment decision is plain code. The model is spent
  only on personality, reflection, dialogue and genuine forks.
- **Fully inspectable.** Open any colonist and read their drives, memories, beliefs and
  relationships — and the raw model trace behind every one.

---

## 1. The concept

Creatures' heritable thing was biology — a genome that wired a neural net and mixed a bloodstream.
Here the heritable thing is **culture**. That's the one substitution that makes this modern rather
than nostalgic, and it only works because there's a language model underneath.

### Why printed minds earn their keep

Making new characters **diegetic** — the Loom prints them — turns an authoring menu into a mechanic
with real cost and real politics. Loom charge is the colony's scarcest resource, so deciding *who*
to print, and what to seed them with, is a genuine argument between people with different stakes.
Printed minds arrive with a **designation**, not a name; they take a name once they've done
something they consider worth being named for.

### What the setting buys mechanically

- **The economy inverts.** Food is easy. What's scarce is **Loom charge**, **salvage**
  (maker-artifacts, each findable only once) and **understanding**.
- **Drives shift up the stack.** With survival largely solved, the dominant drives become Standing,
  Meaning and Wonder. Scarcity makes characters *behave*; abundance makes them *think*.
- **The myth system gets a subject.** What happened to the makers is the central unanswerable
  question, so the colony's founding myth is *about its own creators*.

---

## 2. Two hardware profiles

The design targets a 12 GB RTX 5070 and degrades cleanly onto a 15 W laptop with no GPU. The gap is
roughly 5×, and the two-speed architecture is what lets one build serve both.

| | Modest — measured | Target — measured |
|---|---|---|
| Compute | Core 7 150U, CPU only | i7-14700K + RTX 5070 12 GB |
| Deep lane | `llama3.2:3b` · 8.4–9.8 tok/s *(est ~10)* | `gemma4:12b` Q4_K_M · **61–72 tok/s** *(est 40–60)* |
| Fast lane | `llama3.2:1b` · 16–18 tok/s *(est ~30)* | `llama3.2:3b` · **152–211 tok/s** |
| Sustained calls/min | **4.5–5.0** *(est 4–6)* | **20.4** *(est ~20)* |
| Schema failures | 0/18 once caps were fixed | 0/18 |
| Colonists | 8–12 (untested) | 25–40 (untested) |
| Role | Development floor; plays via hosted inference | The game as designed |

Both profiles are now measured; full reports in [`bench/results/`](bench/results/). The gap came out
at **4.25x**, close to the estimated 5x. Two things the estimates got wrong in opposite directions:
the Modest fast lane is about **half** the guessed speed, and the Target deep lane is **faster**
than the top of its estimated band.

**Residency, the load-bearing claim.** Both lanes sit entirely in VRAM at 4096 context —
`gemma4:12b` at 6.87 GiB and `llama3.2:3b` at 1.88 GiB, 8.75 GiB of 11.9 GiB total, leaving about
**1.3 GB spare**. So the claim holds for a 12B pairing, but a true 14B (~9 GB) alongside a 3B
would be very tight — 12B is the safer target, not 14B.

**Decided at M4: the laptop does not ship as a local-play profile.** Measured in a 47-day colony
running on it, real reflections took about 54 s each. That is roughly one call a minute, a quarter
of what the benchmark's smaller call shapes suggested, so reflection took the whole budget and idle
thought was never served once. Beliefs formed, and every one traced back to its sources, but the
3B model's were bland and repetitive. So a laptop plays through hosted inference, which is already
the plan (§13), and stays the development floor it was meant to be. Fallbacks stay thin: a
deterministic stub, recorded as a fallback in the trace. No phrase banks.

---

## 3. The two-speed mind

Every colonist is two things at once. A **body** that acts every tick without ever touching the
model, and a **mind** that runs asynchronously, on a budget, and writes down what it concludes.

```
  LAYER 1 — THE BODY                GOVERNOR              LAYER 2 — THE MIND
  deterministic, every tick     priority queue            Ollama, async
  0 model calls                 1 concurrent              never blocks a tick
                                real-time tok/min
  seven drives            ──►   ──────────────►   ──►     kindling
  utility scoring               over budget:              reflection
  pathing / forage / salvage    drop or slow time         dialogue
  relationship ledger                                     deliberation
  episodic capture                                        coinage
       ▲
       └──────── durable artifacts rewrite the body's weights ────────┘
        personality weights · beliefs · consolidated memories ·
        relationship narratives · drive modifiers
```

The model never answers *"what should I do next?"* — the body already knows. The model answers
*"who is this person becoming?"*, and the body reads that answer on every subsequent tick.

> **If Ollama is offline, the game still runs.** Colonists keep foraging, salvaging, building,
> arguing and dying — they just stop forming new beliefs and speak in templated lines. That's the
> correctness test for this architecture, and it is an integration test from day one.

---

## 4. Layer 1 — the body

### Seven drives

Each `0.0–1.0`, each with its own decay rate. The physical decay rates sit low on purpose.

| Drive | Rises when | Satisfied by | Decay/day |
|---|---|---|---|
| Sustenance | Always, slowly | Foraging; the forest is generous | 0.12 |
| Rest | Working, climbing, arguing | Sleeping in a claimed shelter | 0.30 |
| Safety | Unstable ruins, live machinery, dark levels, injury | Cleared ground, light, proximity to trusted others | 0.18 |
| Belonging | Isolation, rejection, being overruled | Conversation, shared meals, being agreed with | 0.22 |
| Standing | Being ignored; watching others take credit | Being deferred to, naming things, winning a vote | 0.26 |
| Meaning | Repetition, idleness, loss | Finishing a build, teaching, ritual, being needed | 0.24 |
| Wonder | Unexplored levels, unopened doors, artifacts nobody understands | Salvage, reaching somewhere new, a theory that holds | 0.28 |

**Wonder is the setting's load-bearing drive.** Cut it and you have a well-fed colony with nothing
to do.

### Action selection

Plain utility AI, no planner:

```
score = Σ(drive_urgency × action_relief × personality_weight)
        × context_modifier × relationship_modifier
```

**`personality_weight` is the seam.** It's a number the model wrote, and it's the only place Layer 2
touches action selection — which is what makes a colonist who watched someone fall through a floor
behave measurably differently, with zero inference at decision time.

Add scored noise so identical colonists don't move in lockstep, and hysteresis so they don't thrash
between two near-equal actions.

### The relationship ledger

Per ordered pair: **affinity**, **trust**, **respect**, plus a shared-memory count. Adjusted in code,
instantly. Layer 2 later writes a *prose* reading of the same relationship, which is what a player
sees; the numbers are what the simulation runs on.

### What the body records

Raw episodic memories, constantly and cheaply — `{ tick, actors, verb, object, place, valence,
intensity }`. Thousands accumulate. Layer 2's job is not to generate them; it's to periodically read
a batch and decide what they *meant*.

---

## 5. Layer 2 — the mind

Five call types, and no sixth. If a proposed feature needs a call that doesn't fit one of these
five, it's the wrong feature.

| Call | Fires when | Lane | Cap | Produces |
|---|---|---|---|---|
| **Kindling** | Once per colonist, at print or birth | deep | ~300 tok | Name candidates, prose personality, and the seven personality weights. One call, consumed forever. |
| **Reflection** | Memory salience crosses a threshold. A shallow variant — idle thought — fires on the fast lane during downtime. | deep / fast | ~200 / ~40 tok | Deep: consolidated memory, 0–2 beliefs each linked to source memories, relationship narrative, optional weight drift. Shallow: one thought, nothing durable. |
| **Dialogue** | Two colonists talk, in view or off-screen. Also when the player speaks. | deep | ~80 tok | Spoken lines. The only unconstrained-prose call. |
| **Deliberation** | The sim flags a genuine fork | deep | ~120 tok each | A structured stance and vote per participant, plus one quotable line. |
| **Coinage** | A settlement forms, a ritual repeats, a myth calcifies, an artifact is recovered | deep | ~60 tok | Names and phrasings that enter the colony's shared vocabulary and are reused verbatim. |

**Structured output is not optional.** Everything except Dialogue goes out with Ollama's `format`
parameter carrying a JSON schema. This is the single most valuable upgrade over Morpheus's
`OllamaService`, which streams free text and parses hopefully.

**Reasoning must be switched off, and this is not a style preference.** On a thinking-capable model
Ollama counts reasoning tokens against `num_predict` but returns them in a separate `thinking`
field, so the cap silently buys reasoning instead of the answer. Measured on `gemma4:12b`: a
one-word reply cost **38 tokens with thinking on and 2 with it off**, and the first Target
benchmark run came back with *every* schema-constrained call truncated and every dialogue line
empty — 96 tokens generated, nothing returned. Setting `think: false` fixed all 18 of 18 in the
re-run. Any per-call cap is meaningless on a thinking model unless reasoning is disabled, and the
setting is harmless on models that have no thinking mode.

**Token caps, measured.** Reflection needed 448 rather than the planned ~200; kindling 384, coinage
112, dialogue 96, deliberation 128, idle thought 64. A schema-constrained call that hits its cap
does not shorten gracefully — it truncates mid-document and the whole result is unparseable, so a
cap is a correctness constraint rather than a budget hint. They live in one `CallBudgets` table read
by both the benchmark and the governor.

**Prompt shape, for cache reuse.** Stable prefix first, volatile suffix last: world rules and
personality (cached across calls) → relationship state → the specific memories in question.

**What deliberately gets no model call:** pathfinding, construction, salvage resolution, resource
maths, and mood — derive mood from drive levels and recent memory valence.

---

## 6. The budget governor

One component owns the answer to "how much thinking is happening right now."

**What it enforces:** one concurrent request; a rolling *real-world* tokens-per-minute ceiling set
per profile; a per-colonist cooldown; a max queue depth beyond which the lowest-priority requests
are dropped, not queued forever.

**How it prioritises**, highest first:

1. **Deliberation at a fork — undroppable**
2. **Kindling a new mind — undroppable**
3. Anything the player is looking at right now
4. Reflection, ordered by accumulated salience
5. Coinage, off-screen dialogue, idle thought

*Revised at M5.* What the player is watching was first, above the undroppables. Live, watched
chatter at the plaza held the whole colony's Kindling back for three colony days, and the first
fork was argued by colonists who did not have personalities yet.

### Time is the player's, and it costs thought

Speeds run pause, 1×, 3×, 10×, 60×, plus unattended overnight. Because the ceiling is measured in
*real-world* tokens per minute while demand scales with *game* time, thinking-per-colony-day falls
automatically as you speed up. So surface it: a **Contemplation readout** beside the speed control
("3.2 reflections per colony-day") that visibly drops as you accelerate.

> **The one place time bends for function.** Kindling and Deliberation are **undroppable**. If the
> queue can't serve them at the current speed, **the simulation slows itself** until it can. Pair it
> with an *auto-slow on event* toggle that drops to 1× when a Deliberation fires.

**As built at M5.** The body flags two forks: *Print* (the Loom holds more than a print costs, with
a margin; Belonging against Safety) and *Ascend* (a stairwell has been cleared, and the floor
above is closed to everyone until the colony decides; Wonder against Safety). The council is five
colonists, not the whole colony, because each seat is one deep call: the five whose weights on the
fork's two drives sit furthest from ordinary, since an indifferent council does not argue. They are
asked in order, and each prompt carries what the earlier ones said. That is still stateless: the
argument travels in the request, not in a server-side thread. It carries only on a majority of
the whole council by heads, three of five; abstaining keeps the status quo, and a "no" puts the
question off for some days. (It was conviction-weighted at first, until one "for" and four
abstentions printed a mind.) The fork waits for every
mind it asked, however long that takes at speed. Its three-day timeout only matters for a colony
with no mind, which still gets its turning points, decided on each member's weights and marked as
fallbacks. Winning a vote relieves Standing, being overruled aggravates Belonging, and everyone who
took a side remembers it.

Expose the governor itself as a live readout: queue depth, tokens/min, calls dropped, current lane.

---

## 7. Memory, belief, and myth

| Tier | Volume | Written by | Lives as |
|---|---|---|---|
| Episodic | thousands | Body, every tick | Structured rows with valence and intensity. Salience decays; low-salience rows are pruned. |
| Consolidated | dozens | Mind, at reflection | A written memory standing in for a batch of episodic rows. Embedded for retrieval. |
| Belief | 5–20 | Mind, at reflection | A durable claim about the world, **with links back to the memories that produced it**. |
| Myth | 1–5 per colony | Mind, at coinage | Colony-level, inherited by everyone, mutated slightly at every retelling. |

**As built at M7, phase B.**

- **Names.** A colonist takes the first of Kindling's name candidates that nobody living goes
  by, the first time they find something of the makers', open a way in, or reach somewhere first.
  This is body only. A mind never written has no candidates and keeps its designation.
- **Coinage.**
  - One Coinage call names something just found, or a practice done for the eighth time.
  - The word joins the colony's lexicon and goes into every prompt afterwards, to be reused
    verbatim, the ten newest at a time.
  - A word the colony already has is not coined twice. A sentence where the word should be is
    refused.
- **Myth.**
  - It is colony-level: its system prompt is the world alone. It is built from what the colony
    has to go on:
    - artifacts found
    - fragments revealed
    - what was read from its dead
    - the forks it has decided
  - It also sees the beliefs its colonists hold that bear on the makers.
  - It is first told once there are three such things. It is retold after three more, and no
    sooner than three days apart. Each retelling carries the last telling in its own prompt and
    is asked to drift, not start over.
  - It names the beliefs that go against it, which lights the inspector's "contradicts the colony
    myth" flag.
  - The story sits straight after the world rules in every prompt, where the cache covers it
    across the whole colony.
- **The golden rule holds.** The story and the lexicon are kept on this machine and sent in full
  each time. The server is never told there was an earlier telling; the earlier telling is simply
  in the prompt.

**The traceable belief** is the feature to build carefully, because it's the one people screenshot.
A belief is never a free-floating string — it carries the ids of the episodic memories that produced
it, so the inspector can walk the chain back to the tick where the real thing happened. Nothing in
that chain is authored.

**Retrieval:** standard RAG over embedded consolidated memories and beliefs, scored on
`similarity × salience × recency`. Morpheus's `DocumentIndexService` ports nearly verbatim; swap
pgvector for sqlite-vec so the game stays a single local process with no Docker.

---

## 8. Birth and inheritance

**Printing** — deliberate, expensive, political. The Loom spends charge to instantiate a mind from a
seed. The player can author it, or the colony can — and when the colony does, it argues first.
*Carries over: the colony's current values, sharpened by whoever won the argument. No memories.*

**Pairing** — emergent, cheap, personal. Two colonists past a bond threshold produce a successor.
Personality weights cross over with mutation, plus an **inheritance set**: each parent's most
salient memories arrive in the child as dreams they half-believe and cannot source.
*Carries over: weights, and a handful of misattributed memories. This is how a grudge outlives the
person who formed it.*

Both paths end in a single Kindling call.

**Death.** Bodies fail. The mind persists as a core the colony must decide what to do with: read it,
archive it, or destroy it. That decision is a Deliberation.

**As built at M7, phase A.** All of this is body. A colony with no mind still has generations, and
the headless sim shows them: 12 in 240 colony days, with the population holding between 8 and 14.

- **Lifespans.** A life is 2,160 colony days, give or take a fifth: about three real months at
  1×, where a colony-day is an hour. For its last 15% the colonist slows and rests more.
  - It started as 40 days while a day was two real minutes, which made a life shorter than an
    hour. After the overnight run, Jeremy slowed the day to an hour and set a life to months, so
    that someone could come to know and love the people in it (13 September 2026). The faster
    speeds are for the experiment.
  - Everything else about a life is kept as the same share of it that the sim showed holds a
    colony steady: maturity and the pairing cooldown at 30% of a life, the Loom's trickle at
    about five prints' worth per life, light and warmth at a third of that trickle each.
  - A printed mind's lifespan comes from its designation, not the colony's random stream, so
    adding lives did not reshuffle existing colonies.
- **Death.** Whoever was close has lost someone; everyone else has heard.
- **The core fork.** It opens before any other, and its council is whoever was closest to the
  dead. There are three fates:
  - A majority for reads it. What the dead believed reaches everyone as something heard, which
    still has to survive their own reflection to become a belief.
  - A majority against destroys it.
  - Neither keeps it, unread.
  - A core nobody gets to within six days is kept, unread. Otherwise, with cores outranking
    prints, a backlog of cores held every Print fork off in the sim.
- **Pairing.**
  - A pair must be bonded both ways, both past 30% of a life, not slowing, and not already
    family.
  - Each such pair waits on average about 7% of a life for a child. There is at most one birth a
    day and a cap of 20 colonists.
  - A maturity of 15% of a life produced a generation every fortnight of a 40-day life, faster
    than anyone could reflect their way to a belief worth inheriting.
- **Inheritance.**
  - A child's weights are its parents', crossed, and Kindling writes a personality around them
    without overwriting them.
  - A child also gets two of each parent's strongest memories as dreams. They are recorded as its
    own, with nothing to say whose they were.

---

## 9. The Mind Inspector

Creatures let you watch neurons fire. Thirty years on, the equivalent isn't prettier neurons — it's
a mind that can be **read**. Five tabs: **Now**, **Memory**, **Beliefs**, **Relationships**,
**Trace**.

The **Trace** tab lists every model call this mind has ever made — call type, model, tokens in/out,
duration, and what it produced. It costs almost nothing to build and is the most convincing thing in
the game for anyone technical.

A belief flagged as *contradicting the colony myth* — a colonist who is right and outnumbered — is
the most interesting thing this system can produce.

**As built at M4.** The links matter more than the tabs. A belief opens onto the episodic rows it
cites. Each row names the place it happened, which opens in the room view, and the other people in
it, whose minds open in the inspector on the same tab. Beliefs and consolidated memories both name
the Reflection call that formed them, and that call opens onto its exact prompt and response. The
Trace tab runs the chain the other way, from each call to whatever it wrote. Idle thought is in the
trace too, since it is most of the calls.

The prompt and response are kept for each colonist's 40 most recent calls, on this machine, and
fetched only when a row is opened. The inference server is asked to remember none of it, so the
golden rule holds. Calls that wrote something durable are always listed however old, so every
"formed by" link resolves; only idle thought is capped. The Relationships tab is labelled *Ties* so
all five tabs fit the panel's width.

---

## 10. How you play it

**Simulation mode.** No player. Time runs fast, the camera follows whoever is most interesting. The
screen a viewer actually reads is the event feed — one line per notable thing, colour-coded by
whether it came from the body or the mind. Watchable the way an aquarium is watchable.

**Interactive mode — five verbs, none of them "order":**

| Verb | What it does | What it costs |
|---|---|---|
| **Print** | Instantiate a new mind. Author its seed in prose, or roll one. | Loom charge |
| **Speak** | Address any colonist. What you say is remembered, weighted heavily, and can become a belief. | 1 dialogue call |
| **Reveal** | Show them something of the makers'. Seeds beliefs, unlocks capability, feeds the myth. | Irreversible |
| **Provide** | Route charge into light, warmth, or a door that won't open. | Loom charge |
| **Withhold** | Cut what you were providing. They notice. | Free, and expensive |

**As built at M6, phase A.**

- **What the colonists think you are.** Nothing. You are "a voice with no body", and no prompt ties
  you to the Loom or the makers, because the colonists were never told.
- **Speak.**
  - What you say becomes a memory straight away, whether or not a mind ever answers. It is as
    intense as anything the body records, so it leads the next reflection's batch.
  - Nobody else hears it.
  - Their answer is one Dialogue-budget call. It carries your words, and anything the voice has
    said to them before, read from their own memories.
  - It is submitted undroppable. The player is waiting, and a governor that quietly dropped the
    answer, or held it behind the per-colonist cooldown, would look like the game ignoring them.
- **Print.**
  - Spends the charge on a seed you write, or on a rolled one. A written seed reaches Kindling
    as its provenance.
  - Refused while the colony is voting on a Print fork. A player who could print over an open
    vote would make every Print fork decorative.
  - Everyone else remembers the charge going when nobody asked.

**As built at M6, phase B.**

- **One pool.** Provide and Print draw on the same charge.
- **Light.** A dark place counts as lit for working and for fear at night. It costs 1 charge a day.
- **Warmth.** Shelters only. Rest there relieves half again as much, and scores that much
  better. It costs 1 charge a day.
- **The price of both.** Against a trickle of 3.2 a day, two lamps leave barely enough ever to
  print again.
- **A door that will not open.** It opens for 15 charge, once. Everyone hears it opened by
  itself, and nobody saw who did it.
  - It is refused on a floor the colony has voted to leave shut, the same way Print is refused
    during a Print vote.
  - A floor above an opened stairwell still waits on an Ascend fork.
- **Withhold.** It costs nothing. Whoever is there, or was there in the last two days,
  remembers it going out, and anyone there feels less safe.
- **Running dry.** When the Loom runs dry, the newest provision goes out first, and is noticed
  exactly the same way. The colony cannot tell cutting from running out, and is not told.
- **Reveal.** Shows one colonist one fragment of the makers', once, irreversibly.
  - It is as vivid as any memory, and a relief to Wonder.
  - Some fragments change what the colony can do: the Loom gathers faster, or the stairwell
    takes half the work to clear.
  - The colony records what has been shown, and to whom, for the myth system at M7.
  - The five fragments in `Revelations.cs` are placeholders to exercise the verb. The real ones
    are still to be written.

**Settled for now:** ship influence-only. But design the colony's job queue so a direct-assignment
verb could be added behind a setting without touching the architecture.

---

## 11. Stack & Morpheus reuse

Headless **.NET 8** simulation service behind HTTP + WebSocket, and a **React/TypeScript** client.
Persistence is **SQLite + sqlite-vec**: single file, no Docker, no Postgres.

Presentation is **low-poly 3D in Three.js**, on an orthographic isometric camera, built from
Kenney's CC0 kits. This replaces the original plan of a stylised 2D canvas in PixiJS. The 2D
version was built and rejected on sight: tiles that do not quite line up read as a worse version
of a 1987 game rather than a stylised one, and the thing that actually sells a colony — being able
to see somebody in a room doing something — is exactly what a top-down tile grid hides. Low-poly 3D
turns out to be *less* work for a better result, because the art is a solved problem the moment you
stop drawing it yourself, and because real lighting gives you day and night for free.

Two views, and the pair is the point:

- **City.** The whole map, twelve named places among the blocks and the forest coming back through
  the streets. Answers *where is everyone*. Colonists are drawn a little oversized here (1.2×) or
  they vanish among the buildings.
- **Room.** One interior, cut away — two walls only, so nothing stands between the camera and the
  people. A multi-storey building shows only the floor that matters; the Ninth Floor is somewhere
  you go, not a level you scroll through. Answers *what are they doing*. Everything is drawn at true
  size in here, which is what makes the city's fudge affordable.

Clicking a building or a colonist in the city opens that place's room. That link carries a place id
and nothing else, so the seam between the views holds no state.

The reasoning over Unity: this game's best screen is a dense reading interface, and Three.js sits
beside one rather than replacing it. The de-risking move is unchanged — **the simulation is headless
behind an API from day one**, so the presentation choice stays reversible, and swapping PixiJS for
Three.js without touching a line of `Emberloom.Core` is the proof.

| From Morpheus | Take it? | Notes |
|---|---|---|
| `OllamaService` | **Port** | Near-verbatim. Add `format` JSON-schema support and `keep_alive`; add a second lane. |
| `OllamaEmbeddingService` | **Port** | Verbatim. Point at a small embedding model. |
| `DocumentIndexService` | **Adapt** | Swap pgvector for sqlite-vec; add salience × recency to the score. |
| `SystemPromptService` | **Adapt** | Keep labelled-block assembly, reorder blocks by volatility. |
| `CharacterEvolutionService` | **Adapt** | Already 80% of Reflection. Retarget at episodic memories, emit beliefs. |
| `SummarizationService` | **Adapt** | Becomes memory consolidation. |
| `RelationshipStages` | **Port** | Five-stage ladder as a derived band over the numeric ledger. |
| Gated reveals | **Port** | Maps onto colonists withholding what they've found until trust is earned. |
| Character data-file layout | **Port** | `data/<slug>/*.md` per-mind files are readable and diffable. |
| `GameController` / `GameSessionService` | **Reference** | Copy the shape, not the code. |
| `EmotionalStateService` | **Skip** | Derive mood from drives and memory valence. |
| Postgres, Docker, Identity, payments, TTS/STT, image/video gen, plugins, admin | **Skip** | All platform, none of it game. |

---

## 12. Build order

Ordered so the riskiest assumption is tested first and each milestone is playable — or at least
watchable — on its own.

| | Milestone | Proves |
|---|---|---|
| **M0** | **Benchmark both machines before designing further.** Per machine, pull a 1B, a 3–4B and (Target only) a 12–14B at Q4_K_M. Measure single-stream tok/s on the real call shapes with both lanes resident and a long `keep_alive`. Record schema-adherence failure rate per model and VRAM headroom. | Every number in §2, §5 and §6. **These results supersede all of them.** |
| **M1** | **The body, with no mind at all.** Seven drives, utility scoring, the city map, foraging and salvage, building, sleeping, the relationship ledger, episodic capture, the event feed, the full time-control UI. Personality weights are hand-authored constants. Zero model calls. | The sim is watchable on its own — the fallback the whole architecture leans on. |
| **M2** | **Governor and Kindling.** The queue, real-time ceiling, undroppable flags, time-slowing, Contemplation readout. Then the first real call type. | The seam between the layers, at the cheapest possible call rate. |
| **M3** | **Memory pipeline and Reflection.** Consolidation, beliefs with source links, embedding and retrieval, idle thought on the fast lane. | The core loop. If this isn't compelling to watch, rethink here. |
| **M4** | **The Mind Inspector.** All five tabs including Trace, belief-to-memory chains clickable end to end. Also the Modest-profile decision point. | The marquee feature, and the best debugging tool for everything after it. |
| **M4.5** | **The view.** The low-poly city and cutaway rooms from the graphics spike become the app's real presentation. (1) The 3D city replaces the schematic map: laid out from the world model's own place positions, live over the socket, colonists walking the streets between places; clicking a colonist opens the inspector, and clicking a building opens its room. (2) The room view moves into the app beside the inspector. (3) The art kits stay out of the repository — decided 11 September 2026 — and reach a machine by a fetch script or the game's installer, so a fresh clone is not an empty scene. The Kenney kits are the development art and may ship, but the look is not final: all art choices live in one client-side table, so a different kit is a table change rather than a scene rewrite. | The world is something you look into, not a diagram — which M5's speech and M6's verbs both need. |
| **M5** | **Dialogue and Deliberation.** Colonists speak, in view and off. Genuine forks with structured arguments and auto-slow. | Where the colony stops being a simulation and starts being a cast. |
| **M6** | **Player verbs.** Print, Speak, Reveal, Provide, Withhold. | First genuinely playable version. |
| **M7** | **Generations.** Pairing with weight crossover, inheritance dreams, ageing, death, cores, Coinage. Then a long unattended run overnight. | The long game — and what decides whether this is worth selling. |

---

## 13. Settled & still open

### Settled

| | |
|---|---|
| **Client** | React + TypeScript over a headless .NET 8 sim behind HTTP/WebSocket. A Unity client stays possible later at no architectural cost. |
| **Agency** | Influence only. Five verbs, no orders. The job queue is designed so direct assignment could be enabled behind a setting later. |
| **Time** | Fully player-controlled, with the thinking budget as the natural cost of speed, and forks protected from being skipped. At 1× a colony-day is one real hour and a life about three real months, decided 13 September 2026: lives someone can come to know. The faster speeds are for the experiment. |
| **Setting** | The Loom in a reclaimed city. Printed minds kept as the mechanic. |
| **Hardware** | RTX 5070 is the target. The laptop is a development floor, and plays through hosted inference rather than its own mind — decided 10 September 2026 at M4, on the measurements in §2. |
| **Presentation** | **Low-poly 3D in Three.js**, decided 9 September 2026 after a 2D tile spike was built and rejected. Kenney CC0 kits now, custom art later if the game earns it. Two views — city and cutaway room — with click-through between them. §11 has the reasoning. |
| **Inference** | **Hosted**, decided 8 September 2026 — with `Mind:Endpoint` defaulting to `http://127.0.0.1:11434`, so purely local play is the default value rather than a separate build. This costs almost nothing *provided* the golden rule in [CLAUDE.md](CLAUDE.md) holds: every mind call is self-contained, and the server holds no colony state. Retrieval is the likeliest place to break it — embeddings may be computed remotely, but the index stays local. |

### Deferred, each with a decision point

- **Decided at M0 — which models, and what the real budget is.** Every throughput figure in this
  document is an estimate for the hardware class rather than a measurement.
- **Decided at M7 — whether this is a product.** Judgeable only after a long unattended run produces
  a myth worth reading. *Judged 13 September 2026:* the overnight run went on for 648 colony days,
  about sixteen generations, and retold its story 158 times. Jeremy found the myth compelling
  enough to go on with, though vague. The retellings drifted early, then settled into near word
  for word; the colony's beliefs converged on the myth's own words ("masonry", "the drift", "the
  gray"). Marketability is left for later. The game was begun as an experiment in an AI
  civilisation and how it evolves, which is why simulation mode needs no interference at all.
- **Worth checking early — the name.** *What They Left Running* is worth a trademark and storefront
  search before it hardens into filenames.

---

*Morpheus source reviewed at `77377a7` — `Game/`, `Services/`, `Models/` under `backend/MorpheusI.Api`.*
