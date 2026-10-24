# Version 2 scope — *What They Left Running*

## Context

Version 1 is finished and clean at `dd46ed2`: M0–M7 all shipped, 193 tests passing, the body runs with
no model at all. The last long run (3,787 colony days, 16½ real hours, five generations) was the first
time the whole thing could be judged as a *game* rather than as a set of milestones, and it exposed what
v1 is missing — stated in Jeremy's scope document of 23 October 2026, and confirmed against the code here.

The through-line of every item is the same: **v1 grows a population, not a civilisation.** The colonists
work, age, argue and die in a city that never changes. `Build` is in the action catalog and relieves
Meaning — and has no world effect whatsoever (`BodySimulation.ApplyWorkEffects` has cases for `Clear` and
`Talk`/`Teach` only). They have no leisure, no economy they participate in, no wellbeing that can be hurt,
and most of them live and die as "C-14". V2 makes the world grow, makes the people legible, and gives
them something to lose.

Alongside Jeremy's eight items, this scope absorbs every open item carried from v1 — listed in full under
*Carried over from v1*, each given a milestone home rather than left floating.

## Decisions taken (23 October 2026)

| | Decision |
|---|---|
| **The Loom's charge** | Kept, but **work now feeds it**. The separate food/grain stock from the scope doc is merged into it: colony labour produces the stock, prints and provisions draw it down, and new arrivals consume before they contribute. One colony-wide resource, not three. |
| **Personal tokens** | A second, smaller layer: colonists earn wages from that same output and spend them on leisure. This is what creates the work–life tension in item 5. *(If this is wrong, the alternative is leisure costing only time — say so and M13 simplifies.)* |
| **Persistence** | **In, and early** — M11, after the two milestones that add the most structural fields and before the four that add economy fields. |
| **Game modes** | **Three explicit modes chosen when a colony is created** and fixed for its life: Simulation, Hybrid, Directed. Implemented as one scorer term at three strengths, so there is no second code path. |
| **Delivery** | Sequenced milestones, each independently playable and watchable, as M1–M7 were. |
| **Starting population** | **4.** Reasoning under M8. |

## The golden rule

Unchanged and non-negotiable. Everything v2 adds — wellbeing, tokens, the work-fed Loom, build progress,
capacity, intents, the colony mode — is **Layer 1**: deterministic, zero model calls, `Emberloom.Core`
only. A colony with no Ollama builds, earns, socialises, grows unhappy and dies young; it just doesn't
think about it. Three specific traps, flagged at their milestones:

- **M9** — the name generator must live in Core. The obvious implementation (ask Kindling) both leaves an
  Ollama-less colony nameless and inverts the `Core → Mind` dependency direction.
- **M11** — embeddings may be computed remotely; **the index stays local**. CLAUDE.md names this as the
  most tempting place to break the rule.
- **M18** — "tell the myth what's new since the last telling" is exactly the shape that tempts a
  server-side cache. The diff is assembled host-side from colony state and sent in the prompt.

Any call whose top-level field set changes gets its own copy of
`TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing`.

---

## M8 — Fewer founders, and a harness that can see why

**Delivers.** Colonies start at 4. `Emberloom.Sim` becomes a measurement instrument: ticks per action
class as a share of elapsed time, leisure fraction, travel and idle, the population curve, first-birth and
first-print day, and per-drive means over a full 2160-day life.

**Why 4.** Six distinct Talk pairings (one at two founders, ten at five), so the novelty damper takes far
longer to bite. It sits *below* `CouncilSize = 5`, so `ForkRules.Council`'s `.Take(5)` returns the whole
colony — every early fork is a town meeting where everyone speaks, and the council silently becomes a
*selected* body as the colony passes five. `Needed = 3 of 4` is a supermajority, which is real early
friction. Two founders is structurally broken: `Detect` needs ≥2 living so one death ends forks forever,
every vote is unanimous-or-nothing, and with exactly one possible Talk partner the novelty damper starves
the colony of Belonging by construction.

**Touches.** `src/Emberloom.Sim/Program.cs` (the `colonists = 8` default and new readouts),
`SimulationHost.Restart`, `RestartRequest`, test fixtures assuming 8.

**Riskiest assumption.** *That there is slack in the day for leisure to be a real choice.*
`ActionCatalog`'s own comment puts seven drives at roughly 78% of a day when working flat out, with travel
eating the rest. If that holds, leisure has nowhere to live and items 5 and 6 fail at the premise.

**Test it first, before anything else in the milestone.** Run `--days 2160 --colonists 4` and read the
leisure fraction; then `--sweep` to see whether a higher `ActionCatalog.ReliefScale` (currently 3.0, "the
one balance knob") buys leisure without flattening the drive spread. One command. It validates or kills
the economy work before a line of it is written.

**Second measurement, same run.** Mean Belonging over 2160 days at 4 founders against 8. The Talk novelty
damper is `1/(1 + SharedMemories/30)` and Talk is the *only* real supplier of Belonging; if it trends
toward pinning at 1.0, widen the denominator (one constant) before starting any economy work.

### The measured baseline — re-measured 24 October 2026, at M12

Over a full lifespan, `--days 2160 --colonists 4 --quiet`, seed 4. **This is the baseline every later
milestone compares against.** The 8-founder column is the same run at the old starting population.

**Why this table was re-measured.** The figures first recorded here were taken at M8, before M10 gated
`Build` and before M11 replaced `System.Random` with a saveable stream. The RNG change shifted every
seed's draws, so the old decimals described runs that can no longer be reproduced. Re-measuring was
listed as the first task of M12, and it immediately found a defect — see *What re-measuring found*.

| | 4 founders | 8 founders |
|---|---|---|
| work | **47.8%** — tend 33.5, forage 12.8, build 1.5, salvage 0.0, clear 0.0 | 46.1% |
| rest | **32.0%** | 30.8% |
| social | 9.0% — teach 5.0, talk 3.9 | 13.0% |
| leisure (Wander) | **0.0%** | 0.0% |
| civic | 3.5% | 2.0% |
| travel | 4.8% | 5.0% |
| idle | 3.0% | 3.0% |
| **free** (social + leisure + idle) | **11.9%** — per person 10.2 / 13.6 / 24.9 | 16.1% |
| drive means | sus .17 rest .10 safe .24 **belo .58 stan .61** mean .18 wond .19 | — |
| wellbeing over each life | **mean 0.412, spread 0.232** (0.336 / 0.389 / 0.569) | mean 0.477, spread 0.108 |
| first print · first birth | **never** · day 661 | — |
| population curve | 4 4 4 6 6 6 8 8 8 10 | — |

**What changed from the M8 figures, and what it means.**

1. **The thirty percent of the day that M10 took back from `Build` went to sleep, not to leisure.**
   Build fell from 34.4% to 1.5% — M10 did what it was meant to — but rest rose 16.0% → 32.0% and free
   time did not move at all (11.4% → 11.9%). The scope assumed reclaiming Build's wasted labour would
   create room for leisure to compete for. It created room for Rest to expand into, because Rest is
   always available, always scores something, and never runs out. **M13's venue is not competing for
   slack; it is competing with sleep.** That is a different and harder problem than the one the scope
   described, and it is the most important line in this table.
2. **Standing has overtaken Belonging as the colony's dominant pressure** — .61 against .58, where at M8
   it was .42 against .55, and Meaning doubled (.09 → .18). Gating Build did this: Build was the main
   supplier of Meaning and a route to Standing, and with it rationed those two drives now go unmet. M13
   should point at Belonging *and* Standing; a venue that only supplies company leaves half of it.
3. **Leisure is still literally never chosen.** Wander is 0.0% at both populations, unchanged. It exists
   only as the fallback when nothing scores, and something always scores.
4. **The Loom never prints on seed 4 in 2160 days.** First print was day 948 at M8 and is now *never*;
   the colony grows by birth alone. The charge accrues at ~0.059/day against a cost of 25, and nothing
   about that is a decision anybody makes. This is the argument for M14 stated as a measurement.
5. **`ReliefScale` must stay at 3.0, for a different reason than before.** The sweep now reads
   `0.55 → spread 0.38 · 1.0 → 0.23 · 2.0 → 0.74 · 3.0 → 0.40 · 5.0 → 0.53 · 8.0 → 0.26 · 12.0 → 0.20`,
   so the old finding — that the drive spread collapses at 5.0 — no longer holds numerically. The
   conclusion does, on better evidence: **completed actions fall from 93,431 at 3.0 to 68,572 at 5.0 and
   20,118 at 12.0.** A colony whose needs are all met stops doing anything, which is the same failure
   seen from the other side. At 3.0 the physical drives sit low (.18/.10/.24) and the social drives carry
   the pressure, which is the bar the sweep's own doc comment sets.

Still true from M8, unchanged: `salvage 0.0% / clear 0.0%` — both run dry early, so two of nine actions
are dead for most of a run. And **Rest spends a third of the day but completes rarely**: a Rest is 48
ticks and the colony reconsiders every 6, so sleep is abandoned mid-sleep almost every time. At 32% of
the day that is no longer a curiosity, and it is the first thing to look at if M13's venue cannot get a
look in.

### What re-measuring found: the shipped seed never built anything

Seed 4 is the seed in `appsettings.json` — the colony the game actually starts. Over 2160 days it raised
**nothing**, refusing all twelve sites across twenty votes, and would have gone on refusing them for
ever. Seeds 1, 2, 3, 5, 6 and 7 all raised every site by day 1200, which is why M10's own measurement
missed it.

The cause was structural rather than a seed quirk. A colony with no mind votes through
`ForkRules.FallbackVote`, which read nothing but personality weights and drive levels — and the council
is *itself* selected by those same fixed weights. So the answer to "shall we build?" was a fixed function
of who was alive, decided at colony creation and never revisited. A council of three who all rest easy
refuses the city permanently, and the 400-day `Declined` expiry only re-asks the same people the same
question.

The fix is `Fork.Pressure`: the same facts `Stakes` states in words, as a number in [0,1], so the voter
who cannot read prose gets the same information the model does. Beds are not negotiable (crowding past
three to a room presses up to 1.0), a hall is agreed to (0.5 for the first), a store is argued over (0.1
— deliberately too small to carry anyone who would rather rest). Seed 4 now raises a hall and a dwelling
and declines the rest, which is the intended behaviour: a colony that builds what it is short of.

**The general lesson, worth keeping:** any mindless fallback that is a pure function of unchanging state
is a decision made once and then frozen. This is the second time it has bitten — the first was seed 5's
2–2 deadlock at M10, fixed with odd councils. Every fallback added from here should be checked against
"what world fact would change this vote?"

### M12's measured outcome — 24 October 2026

`Colonist.Wellbeing`, a leaky integrator toward how the current tick is to live, half-life 5 colony days.
Read-only: nothing scores it, gates on it or dies of it.

**The riskiest assumption is retired: it does vary.** Over a lifespan at four founders the colony lives
between **0.336 and 0.569**, mean 0.412 — a 70% relative spread, not the convergence on 0.5 that was
feared. And it varies *for the right reasons*: at eight founders, where social time measures 13.0%
against 9.0% and free time 16.1% against 11.9%, mean wellbeing is **0.477 against 0.412**. The number
tracks a quantity measured independently of it, which is the strongest evidence available at this
milestone that the inputs are right.

**Two findings for the milestones that depend on it.**

1. **Everybody is coping and nobody is flourishing.** The whole distribution sits in 0.3–0.6. That is an
   honest description of a colony that works half its waking life, sleeps a third of it, gets 9% company
   and has chronic unmet Standing and Belonging — and it is the number M13, M14 and M15 exist to move. If
   wellbeing already read "well" here, those milestones would have nothing to show.
2. **A bigger colony is a happier and a more *uniform* one** — spread 0.108 at eight founders against
   0.232 at four. Scale supplies company to everyone equally. Differences between people will have to
   come from things that are not shared out evenly, which is exactly what M15's residential capacity and
   M13's priced venue are.

Also noted: using mean urgency across all seven drives instead of the dominant one was measured and
rejected — spread 0.246 against 0.232, no better, and harder to explain. And because the measured range
is 0.34–0.57 rather than the middle of [0,1], the `Describe` and `Mood` bands are calibrated to it
explicitly. **They want re-reading after each of M13, M14 and M15**, and the comment on them says so.

## M9 — Names at birth

**Delivers.** Every colonist is named when printed or born — invented, short, name-like (Luma, Vael, Oryn,
Asha). The three deeds survive as **epithets** ("Vael, who opened the way into the Ninth Floor") instead of
as the gate on having a name at all.

**Why this fixes it.** Today a name requires one of three one-shot deeds (first to reach a place, break
through a blocked place, recover an artifact), all finite and front-loaded, so late generations have
nothing left to earn one from. Worse, `KindlingCall.Fallback` writes an **empty** candidate list, so any
colonist kindled while the mind was unreachable can never be named at all — and `EarnAName` returns
silently when there is no candidate, so the failure is invisible.

**Touches.** A deterministic syllable generator in `Emberloom.Core`, seeded from the designation via the
same stable-hash idiom as `LifeRules.Lifespan(string)` so a seed still grows the same colony;
`ColonyFactory.Print`/`Pair` assign at creation; `CultureRules.TakeAName` becomes an *upgrade* path.
In `KindlingCall.BuildPrompt`, the instruction asking for "short, plain, the kind of word someone would
take from a thing they found rather than a name from a book" is literally what produces "Rock" and
"River" — it is replaced with one asking for invented, non-lexical names.

**Riskiest assumption.** *That a 3B model produces exotic-but-name-like output rather than fantasy sludge
or plain nouns.*

**Test it cheaply.** 50 Kindling samples through `Emberloom.Bench` against both lanes, and read the list.
Half a day, before touching Core. If the model can't do it, the Core generator is primary and Kindling's
candidates are an optional upgrade — which is the right architecture regardless.

## M10 — The world grows

**Delivers.** Buildable sites exist from day one in an unbuilt state; a Build fork raises them one at a
time; `Build` finally changes the world.

**The cheap shape.** An unbuilt site sits in `Colony.ForbiddenPlaces` (the scorer already skips those
entirely), carries `BuildRequired > BuildDone` mirroring the existing `ClearingRequired`/`ClearingDone`
pair, and does nothing until raised. **This needs no socket work and no scene rebuild**: `Snapshot()`
already streams a `PlaceStateDto` for *every* place every tick, and `CityScene.setSealed()` already
toggles per-place visuals live. Add two DTO fields and a `setBuilt` method mirroring `setSealed`.

**The fork stays binary.** The body proposes one site by a deterministic rule (fewest beds per head →
a shelter; lowest leisure → a hall) and asks "Do we raise the Hall of Looms next?" On a *no*, `PutOff` and
the next detection proposes the next-best site. The colony still chooses *what* gets built, through a
sequence of binary questions, and `Stance`, `Needed`, `Pulls`, `MindSchemas.Deliberation()`,
`DeliberationCall.Parse`, `ForkDto` and `ForkBanner.tsx` are untouched. Multiple-choice forks are a later
milestone, done once — see *Deferred*.

**Touches.** `World/Place.cs`, `CityMap.CreateDefault` (10–14 new leaf sites),
`UtilityScorer.IsAvailable` (already a static switch on `(definition, place)` containing
`Build => !place.IsBlocked` — a one-line change), `ApplyWorkEffects`, `CompleteAction`,
`ForkRules.Detect`/`Pulls`/`ResolveFork`, `PlaceStateDto`, `CityScene.ts`, `rooms.ts`.

**Hard constraint.** `CityMap.Reachable` excludes blocked places from traversal and `ForbiddenPlaces` is
filtered *after* reachability, so **an unbuilt site that bridges two built areas cuts the map**. Unbuilt
sites must be graph leaves. Put that in a comment in `CreateDefault` or someone loses a day to it later.

**Riskiest assumption.** *That the scorer will allocate labour to Build at all.* Build relieves Meaning
2.20 against Salvage's Wonder 2.60 and Forage's Sustenance 2.00; a site could sit half-raised forever.

**Test it cheaply.** Before the fork, the DTO or any client work: add `BuildRequired`, make the one-line
`IsAvailable` change, open one site unconditionally and run `--days 400 --colonists 4`. Count build ticks.
If nothing is built, the fix is local and known — a completion bonus, or a "the colony agreed to this"
modifier in `ContextModifier` of the same family as the existing `CouncilPull = 4.0`.

**Also lands here.** The `ColonyMode` field and the API refusal guard as a stub with no intents defined,
so M16 never has to retrofit the seam.

## M11 — Persistence

**Delivers.** A colony survives the process: SQLite plus sqlite-vec for the belief/memory index, as
DESIGN §11 commits.

**Why here.** Early, as decided — but after M9 and M10, which add the most *structural* fields
(`Place.BuildRequired`/`BuildDone`, `Colonist.Name` at creation). The four economy milestones after it add
plain scalars to `Colony` and `Colonist`, which is the cheapest kind of schema churn to absorb.

**Shape that keeps churn cheap.** Persist the colony as a **versioned document** for the slow-moving
structure, with separate tables only for the hot, numerous rows — episodic memories, beliefs and their
vectors, the event feed, model-call traces. Adding a scalar in M12–M15 then costs nothing but a default.

**Unblocks.** The 40-call trace retention cap (`Colonist.TraceTextRetention`), whose real fix is writing
traces to the store rather than raising the number.

**Golden-rule flag.** The vector index stays local. Only `text in → vector out` crosses the wire.

**Riskiest assumption.** *That save/load round-trips a live colony faithfully* — the relationship ledger,
in-flight actions, open forks and awaited mind calls are all live state.

**Test it cheaply.** A round-trip test: run 200 days headless, save, load into a fresh process, run both
forward 50 more days with the same seed, assert identical colony state. This is the acceptance bar and it
should be written before the writer is.

## M12 — Wellbeing, read-only

**Delivers.** A numeric `Colonist.Wellbeing` in [0,1], integrated per tick from work share (down),
rest/leisure/social (up, social fastest) and drive urgency (pressing). **It changes no behaviour at all** —
no death, no scorer term, no gating. `Mood()` keeps working and becomes a band of the number.

**Why read-only first.** It cannot destabilise `ReliefScale` and cannot break the 193 tests, so the
question "does this number actually vary between people?" gets answered before anything depends on it.

**Riskiest assumption.** *That wellbeing varies across colonists* rather than everyone converging on 0.5
because the scorer equalises drives by design. A number everyone shares has no priorities and no game in it.

**Test it cheaply.** Implement the integrator alone; run 2160 days at 4 founders; print the per-colonist
distribution and its spread — the same test `--sweep` already applies to drive spread. Near-zero spread
means the inputs are wrong.

## M13 — Tokens and the first paid venue

**Delivers.** Work accrues tokens to the worker; one new social action at one built venue spends them;
Belonging gets its second supplier.

**Touches.** `Colonist.Tokens`; one new `ActionCatalog` definition; `UtilityScorer.IsAvailable` for
affordability (a colonist who can't pay simply isn't offered it — no scorer change);
`BodySimulation.AdvanceWorld` for accrual, following the `X * 40 / BaseLifespanDays` per-colony-day idiom
`LoomChargePerDay` and the provisions already use.

**Riskiest assumption.** *That a price produces alternation rather than a corner solution* — it either
never binds (decoration) or always binds (dead code), and the window between may be narrow.

**Test it cheaply.** Sweep the price over a log range, 0.1× to 10× of a day's earnings, and plot the
leisure fraction. A step rather than a slope means the hard affordability gate is the wrong mechanism and
a *soft* price — a score penalty proportional to the shortfall — is right.

**Do this here.** `ApplyWorkEffects` and `CompleteAction` are switches with **no default arm**, so a new
action silently has no world effect. Add a `default:` that throws in debug builds. One line, and every
future action fails loudly instead of quietly.

**Re-run `--sweep`.** This milestone changes relief supply against the fixed 1.60/day demand.

### M13's measured outcome — 24 October 2026

`Colonist.Tokens`, earned by the hour for work and nothing else, and `Gather` — an evening at a hall
the colony voted to raise, priced at **0.25**, a little over half what the colony actually earns in a
day. Affordability is a hard gate: a colonist who cannot pay is not offered it.

**The price was measured, not chosen.** `--prices` sweeps it over two decades against a fixed
1.00-per-working-day wage, seed 4, 2160 days:

| price | leisure | social | rest | work | evenings | purse left |
|---|---|---|---|---|---|---|
| 0.05 | 20.3% | **0.9%** | 24.1% | 42.9% | 33,932 | **445.1** |
| 0.10 | 20.1% | **0.8%** | 25.1% | 42.2% | 33,345 | **247.2** |
| **0.25** | **4.3%** | 7.5% | 29.3% | 46.4% | 25,932 | **0.2** |
| 0.50 | 2.0% | 8.9% | 31.0% | 46.8% | 13,569 | 0.3 |
| 0.90 | 1.0% | 7.9% | 31.5% | 48.0% | 6,650 | 0.5 |
| 1.50 | 0.5% | 9.2% | 32.3% | 46.6% | 4,314 | 0.8 |
| 2.50 | 0.3% | 8.7% | 31.7% | 47.8% | 2,465 | 1.3 |
| 5.00 | 0.2% | 8.8% | 31.7% | 48.2% | 1,322 | 2.7 |

**It is a slope, not a step** — leisure falls smoothly from 20.3% to 0.2% across the whole range. That
is the answer to the question the scope raised: **the hard affordability gate is the right mechanism,
and the soft score penalty it listed as the fallback is not needed.** Keep the gate.

The corner the scope warned about is real and visible at the cheap end. Below 0.25 the price does not
bind at all: purses accumulate to 247 and 445 tokens, which is money that means nothing, and the hall
out-competes Talk so completely that **conversation almost stops** — social falls to 0.8%, which would
have quietly broken the colony's only supplier of relationships. The knee is between 0.10 and 0.25,
where the purse goes to zero. 0.25 is the first price at which the colony spends everything it earns.

**What it did to the colony, against the M12 baseline.**

| | M12 | M13 | |
|---|---|---|---|
| leisure | 0.0% | **4.3%** | chosen, for the first time in the project |
| rest | 32.0% | **29.3%** | the scope was wrong and the baseline was right: leisure came out of sleep |
| social | 9.0% | 7.5% | and a little out of conversation |
| work | 47.8% | 46.4% | |
| travel | 4.8% | 6.7% | there is one hall, and people walk to it |
| free | 11.9% | **14.4%** | |
| Belonging | .58 | **.47** | |
| Standing | **.61** | **.47** | the first real relief Standing has ever had |
| wellbeing | 0.412 | **0.454** | |
| wellbeing spread | 0.232 | 0.151 | |
| survived 2160 days | 10 | 12 | |

Three things worth carrying forward:

1. **The venue took its time out of sleep, exactly as M12's baseline predicted.** Rest fell 2.7 points,
   social 1.5, work 1.4, and travel rose 1.9. Nothing else in the project has moved Rest off its
   plateau.
2. **Standing fell 0.61 → 0.47 and Belonging 0.58 → 0.47.** Gather was weighted toward Standing on
   purpose — Talk already buys company, and what nothing supplied was being *seen* — and the two
   dominant pressures of the M12 colony are now level with each other and with nothing in particular.
3. **Wellbeing rose but its spread narrowed again**, 0.232 → 0.151, which is the same effect eight
   founders had. One hall open to everybody is shared out evenly. **Differences between people will
   have to come from something that is not** — which is precisely M15's residential capacity, and is now
   the second measurement pointing at it.

Also landed here, as the scope asked: `ApplyWorkEffects` and `CompleteAction` no longer have switches
without a default arm. Every action must now name its world effect or be listed explicitly among those
whose only effect is relief, and `EveryActionDeclaresWhatItDoes` walks the whole catalog so a tenth
action cannot be added without that decision being made. This is the defect that let `Build` spend the
whole of version 1 doing nothing while looking implemented.

**`--sweep` re-run after the catalog change**, as the verification section requires:
`0.55 → spread 0.35 (every drive pinned at ~1.0) · 1.0 → 0.38 · 2.0 → 0.29 · 3.0 → 0.24 · 5.0 → 0.34 ·
8.0 → 0.24 · 12.0 → 0.13`, with completions `82,880 → 56,739 → 34,775 → 16,076` across 3.0/5.0/8.0/12.0.
**3.0 stays.** At it the physical drives sit at .20/.13/.27 and Belonging and Standing carry what
pressure is left, which is the bar the sweep's own doc comment sets. The *spread* figure has become
less informative now that Gather supplies the two drives that used to stand out — the throughput
column is the one to read.


## M14 — The Loom, fed by work

**Delivers.** The charge stops being a trickle nobody earns. Colony labour produces it; prints and
provisions draw it down; every living colonist draws a little per day, and the newly printed or newly born
draw before they contribute.

**Why this is the Print fork's missing argument.** Today the Loom gains ~0.059/day regardless of anything,
a print costs 25, and saving for one takes ~420 colony days. The council has no resource question to
weigh, only a threshold to cross. With production tied to labour and a per-head draw, "can we feed
another?" becomes a real argument that the body can state and the council can read.

**Touches.** `Colony` (production and draw rates beside `LoomCharge`), `BodySimulation.AdvanceWorld` for
the draw and `CompleteAction` for production, `ForkRules.Detect`'s `Stakes` strings, `ColonyStateDto`, a
client readout.

**Riskiest assumption.** *That the council's deliberation actually weighs the figure.* CLAUDE.md is
explicit that raw numbers in prompts come back as beliefs, so the stock must enter as **words** — "the
stores are thin", "there is more than enough" — and whether a 3B model shifts stance on that wording is
open. The M5 lesson about a loose Print `Stakes` being misread as a reason to print is the warning.

**Test it cheaply.** Before writing production or draw code, run the *existing* Print deliberation through
`Emberloom.Bench` with three hand-written `Stakes` variants — thin, adequate, plentiful — 30 samples each,
and measure the For/Against split. If stance doesn't move, the resource is invisible to the politics and
needs a body-side gate in `Detect` instead of a prompt-side one. That tells you which half to build.

**Decision to surface here.** `LifeRules.PopulationCap = 20` is a hamlet, not a civilisation — and it is
really a *thinking budget*, since every living mind costs the governor. Raising it means minds for the
notable and bodies for the rest, which is an architecture decision rather than a constant change. This is
the single most underestimated item in the v2 scope and it should be decided deliberately, here.

### M14's measured outcome — 24 October 2026

Labour feeds the Loom. `Colony.LoomChargePerDay` — the flat 0.059 a day that arrived whatever anybody
did — is now **zero**, and the colony's one stock is produced by paid work from whoever is grown,
drawn down by every living mouth, and bounded by how many granaries have been raised.

#### The riskiest assumption was tested first, and it is false

The scope's bet was that a council told the stores were thin would vote against a print, so the
resource could enter the politics as words. `Emberloom.Bench --stakes` ran three worded variants of
the Print stakes past llama3.2:3b, thirty samples each, before any of M14 was written:

| stores | for | against | abstain | fell back | mean conviction |
|---|---|---|---|---|---|
| thin | **15** | 2 | 13 | 0 | 0.50 |
| adequate | 10 | 1 | 19 | 0 | 0.30 |
| plentiful | **17** | 0 | 13 | 0 | 0.50 |

**Thin stores produced more for-votes than adequate ones, and against never rose above 2 of 30.** A
3B model will not oppose a print on the strength of the stock, and if anything reads scarcity as
urgency to act — the M5 lesson exactly, where a looser Against clause was read as a reason *to*
print. Schema adherence was perfect (zero fallbacks), so this is not a parsing problem. The one real
signal is indirect and worth keeping: an adequate store produced nineteen abstentions against
thirteen and dropped conviction from 0.50 to 0.30, so the model does register that a middling stock
is a reason to be unsure.

**So the stores constrain the colony in the body, and the council argues about whether it *wants*
another mind rather than whether it can afford one.** That is `ForkRules.CanFeedAnother`, and the
scope named it as the fallback: "if stance doesn't move, the resource is invisible to the politics
and needs a body-side gate in `Detect` instead". It doesn't move. The gate is in `Detect`.

#### What the economy looks like

- **`ChargePerWorkDay = 0.5`** — one colonist-day of paid work, from anyone grown.
- **`DrawPerHeadPerDay = 0.16`** — every living colonist, grown or not.
- **`LifeRules.IsGrown`** — the Loom prints adults, so a printed mind pays its way at once; a child
  has `MaturityDays` (648, a third of a life) of being fed first. A birth therefore costs the colony
  two years of food, and a print costs 25 charge up front. The two kinds of arrival cost differently,
  which is new.
- **`ChargeNeededToPrint(colony)`** replaces the flat 45: a print's cost plus **sixty days of feeding
  one more mouth than there are now**. About 73 charge at four founders, about 250 at twenty.
- **`Colony.StoreCapacity`** — 60 in the Loom's own cells, +120 for each raised granary. Surplus past
  it is not kept, and the colony says so in the feed.

Measured over 2160 days at four founders, `--quiet`, six seeds:

| seed | produced | net | charge range | first print | population |
|---|---|---|---|---|---|
| **4** | 1.14/day | **+0.09** | 40..174 | **never** | 4 → 12, by birth alone |
| 1 | 2.76 | +0.65 | 40..420 | day 353 | 4 → 20 |
| 2 | 3.28 | +1.05 | 40..420 | day 425 | 4 → 20 |
| 3 | 2.30 | +0.55 | 40..420 | day 518 | 4 → 20 |
| 5 | 2.81 | +0.73 | 40..420 | day 500 | 4 → 20 |
| 6 | 2.63 | +0.64 | 40..420 | day 418 | 4 → 20 |

**This is the first time the Print fork has had an answer that depends on the colony.** Five seeds
save up and print, somewhere between day 353 and 518 — rare in a life, which is what the original
design asked for. Seed 4 never does, and not because anybody is frozen: its stores never get past
41% of capacity and its net is +0.09 a day, so it genuinely cannot afford another mouth and grows by
birth alone. Before M14 the same seed reached 57.8 charge over the same 2160 days with nobody having
earned any of it, and never printed either — the difference is that now there is a reason.

**A colony with nowhere to keep anything cannot print at all.** What a print plus two months of one
more mouth costs is more than the Loom's 60 cells hold at any population, so a granary is a
precondition. That is deliberate and it is the strongest thing M10's buildings have ever done: *you
cannot feed another mouth until you have somewhere to keep food.* It also finally gives the Granary
vote — the one deliberately given the least pressure, because nothing obliges anyone to want one — a
real answer behind it, and build time rose from 1.4% to 2.7% of the day because of it.

#### Two defects this milestone found

1. **Prints were never capped and births always were.** `ConsiderPairings` has checked
   `PopulationCap` since M7; the print path never did, which went unnoticed while the Print fork
   almost never carried. With labour feeding the Loom it carries often, and three seeds ran to **29,
   41 and 27 living** against a cap of 20 before the cap was moved into `Detect`. The body should not
   ask a question whose answer it would then refuse to honour.
2. **The council seated only champions, and that was a quiet bias.** `Council` took whoever was
   furthest from ordinary on either of the fork's two drives — which systematically seats whichever
   side of the question has the more extreme archetype in it. The Print fork sets Belonging against
   Safety, and the largest single weight in the table is the cautious archetype's 1.7 on Safety, so a
   cautious colonist sat at *every* print; with a stub vote being a function of weights that never
   change, seed 4 refused every print for its whole existence while sitting on two hundred charge.
   Build had the same shape at M10 and was patched by changing its axis, which fixed that fork and
   not the rule underneath it. The council now seats **the champion each way and fills the rest with
   the least committed**, so there is someone in the room who can be persuaded — which is what the
   two things that *can* move, the world's pressure and a mind's reading of the stakes, need in
   order to matter at all.

   **This is the third time the same class of bug has appeared.** Seed 5's 2–2 deadlock at M10, seed
   4's permanent refusal to build at M12, seed 4's permanent refusal to print here. Any mindless
   fallback that is a pure function of unchanging state is a decision made once and then frozen, and
   council *selection* is part of that state.

#### The decision the scope reserved for here: `PopulationCap`

**Not taken — it needs Jeremy.** The cap is now honoured by both arrivals, so the behaviour is
coherent at 20, and every seed but one reaches it by about day 700 and then sits there for 1,400
days. That is the hamlet the scope complained about, and it is reached early.

What the measurements say about it:
- A colony of 20 is **not** short of food. At the cap, with no children, production runs well ahead
  of the draw, and the stores sit full and spoiling until a granary is raised. So the cap is not
  doing economic work; the economy would support more.
- It is doing *thinking* work: every living mind costs the governor, and the Contemplation readout
  measures served calls against a fixed rate. Twenty minds is roughly what the laptop sustains.
- So raising it means **minds for the notable and bodies for the rest**, which is an architecture
  change — a second tier of colonist that acts but does not reflect, and a rule for promotion
  between them. That is a milestone, not a constant.

Recommendation: leave it at 20 for v2 and schedule the two-tier change as its own milestone after
M19, where the tier rule can be designed against a colony that is otherwise finished. The alternative
— raising the constant and letting the governor drop calls — degrades every mind in the colony
rather than choosing which ones matter.


## M15 — The couplings

**Delivers.** Shortfall aggravates Sustenance through the existing `Drives.Aggravate` channel; sustained
low wellbeing shortens life; built shelters carry residential capacity that gates Rest.

**Despair shortens a life — it does not kill.** `Colonist.LifespanDays` is settable, so wellbeing
subtracts from it and `AdvanceLives` keeps its single death entry point and single trigger. Every existing
death test holds, the chronicle's "42 of about 46 days" line stays truthful, and despair reads as *going
early* rather than as an instakill.

**Riskiest assumption.** *That the wellbeing→lifespan feedback is stable rather than a death spiral.* A
death aggravates Belonging by +0.35 in everyone close — the largest single aggravation in the codebase —
which lowers wellbeing, which shortens more lives. There is no damping on this today.

**Test it cheaply.** Add the lifespan subtraction alone, behind one multiplier, and sweep it across 4000
days, plotting population. You are looking for the multiplier where the colony flips from "stable with
occasional early deaths" to "extinct". A sharp threshold means the coupling needs a floor — cheap now,
impossible to retrofit once the number is tuned.

### M15's measured outcome — 24 October 2026

Three couplings, which are what make everything v2 added cost something: the stores reach the body as
hunger, a life going badly is a shorter life, and a bed somebody else is in is not a bed.

#### The death spiral was real, and it was not where the scope expected

The scope's riskiest assumption was that the wellbeing→lifespan feedback would run away: a death
aggravates Belonging by 0.35 in everyone close, the largest single aggravation in the codebase, which
lowers their wellbeing, which shortens their lives, which brings the next death forward, with nothing
damping it. `--despair` sweeps the rate over 4000 days looking for the edge, and found one:

| days lost per day | peak | ended | mean life | avg lost | worst | wellbeing | extinct |
|---|---|---|---|---|---|---|---|
| 0.0 | 12 | 9 | 2254 | 0 | 0 | 0.401 | no |
| 1.0 | 12 | 9 | 2188 | 10 | 46 | 0.386 | no |
| **1.5** | 10 | **2** | 2185 | 89 | 262 | **0.162** | no |
| 3.0 | 11 | 5 | 2178 | 123 | 413 | 0.442 | no |
| 6.0 | 10 | **1** | 2003 | 296 | 1034 | **0.107** | no |
| 12.0 | 9 | **0** | 1745 | 425 | 1013 | 0.000 | **day 3958** |
| 25.0 | 9 | **0** | 1734 | 499 | 1013 | 0.000 | **day 3300** |

Not a slope — a **bistable cliff**, and non-monotonic across it (1.5 collapsed to two survivors while
3.0 recovered to five), which is the signature of a system that sometimes runs away and sometimes
doesn't depending on when the first death lands.

**But the cause was not the lifespan coupling.** It was `WellbeingRules.Moment`. Urgency is
`(level × weight)²`, so it runs to 4 rather than to 1, and the pressure term was subtracted from the
hour unclamped — so it could be worth more than the entire hour it was discounting. Talk needs a
partner and a gathering needs a hall, so **the last two people in a dying colony can never relieve
Belonging at all**: their urgency pins above 2, every moment of their lives evaluates to exactly
zero, and their remaining years burn off as fast as the cap allows. The spiral was loneliness, not
despair.

The fix is one clamp — `MostPressureCanTake = 0.30` — and it says something defensible: however badly
you need something, the hour you spend asleep is still sleep. It leaves every figure measured at M12,
M13 and M14 intact, because a colonist under ordinary pressure is nowhere near the cap. Re-swept:

| days lost per day | 0.0 | 0.5 | 1.0 | 1.5 | 3.0 | 6.0 | 12.0 | 25.0 |
|---|---|---|---|---|---|---|---|---|
| mean life, seed 4 | 2254 | 2249 | 2239 | 2214 | 2219 | 2050 | 1916 | 1705 |
| mean life, seed 1 | 2363 | 2363 | 2362 | 2362 | 2360 | 2357 | 2350 | 2311 |
| mean life, seed 2 | 2401 | 2400 | 2400 | 2400 | 2399 | 2397 | 2394 | 2386 |
| extinct, any seed | no | no | no | no | no | no | no | no |

A smooth slope, no extinction anywhere across two decades of rate, and — the useful part — the
coupling **discriminates**: at 6.0 the struggling colony loses 200 days off a mean life and the two
healthy ones lose 6 and 4. Despair costs a colony that is actually failing and is nearly invisible to
one that isn't, which is what it is for.

`DespairDaysPerDay = 6.0`, with `MostOfALifeDespairCanTake = 0.4` so the worst possible life still
runs three fifths of its course. `AdvanceLives` keeps its single death trigger and every existing
death test holds.

#### What the three couplings did

Over 2160 days at four founders:

| | seed 4 | seed 1 | seed 2 | seed 3 |
|---|---|---|---|---|
| lives shortened | **8 of 8** | 4 of 20 | 5 of 20 | 4 of 20 |
| days lost, average · worst | **90 · 252** | 65 · 146 | 13 · 39 | 10 · 34 |
| beds · living | 10 · 7 | 26 · 20 | 26 · 20 | 26 · 20 |
| dwellings built | 1 | 5 | 5 | 5 |
| rest | 30.5% | 22.6% | 23.3% | 22.4% |
| wellbeing | 0.424 | 0.486 | 0.493 | 0.495 |
| population | 4 → 7 | 4 → 20 | 4 → 20 | 4 → 20 |

**Every life in the constrained colony is now shorter than it was born to be**, by about four percent,
while the comfortable colonies lose a few lives' worth of years between twenty people. Seed 4's
population fell from 10 at M14 to 7, and its wellbeing from 0.453 to 0.424 — the couplings cost it,
which is the point of them.

**Two of the three are safety nets that a healthy colony never trips.** The stores never empty on any
seed, so hunger never fires; and the colony builds dwellings before it runs out of beds, so sleep is
never refused. That is the system working rather than the system being inert — the bed *pressure* is
what causes the dwellings to be built, and `CouplingTests` drives both couplings into their failure
modes directly, because a coupling that is untested in the uncommon case is not a coupling.

#### A defect found on the way: the city was built in the wrong order

`ForkRules.Propose` picks what the colony is most obviously short of and then takes the nearest site
of that kind to the plaza. It was written as
`sites.Where(wanted).Concat(sites).OrderBy(distance).First()`, which sorts the *concatenation* — so a
nearer site of the wrong kind outranks a farther site of the right one. Measured directly: a colony
with ten people in six beds proposed a hall. The preference now resolves before the distance sort,
and the `Concat` fallback means what it was meant to mean — having run out of the kind you want, not
having to walk further. Dwellings built per seed went from 1 to 5 on the three comfortable seeds.


## M16 — Modes and intents

**Delivers.** `ColonyMode` — **Simulation**, **Hybrid**, **Directed** — chosen when a colony is created
and fixed for its life, plus the standing **Intent** primitive: `(verb, subject, strength, expiresOnDay)`
on `Colony` beside `Provisions`.

**One code path, three strengths.** An intent is not an order. It enters as one more multiplicative term
in `UtilityScorer.ContextModifier`, beside the darkness, danger and crowding terms already there — the
same shape as the existing `CouncilPull = 4.0`. Simulation sets its strength to **zero**: the term is
present but inert, so a research run is provably the same binary rather than a different build. Directed
sets it high; a starving colonist still eats first. Hybrid sets it low **and** lets colony decisions veto
it, reusing the guard that already refuses Print during an open Print vote.

**Touches.** `Colony`, `UtilityScorer.ContextModifier`, the API verb guards (`VerbReply` already returns
`Results.Conflict` with a sentence, and the client already renders refusals), a mode picker on the
new-colony control.

**Riskiest assumption.** *That a multiplicative term reads as influence rather than as an order that
sometimes fails confusingly.*

**Test it cheaply.** Sweep the strength constant and measure what fraction of ticks the intended action
actually wins, bucketed by drive urgency. You want a curve, not a cliff — and the starving-colonist case
must visibly override at every strength.

## M17 — Rooms that look like what the building is

**Delivers.** Interiors that read as what the sign says, and an art-kit fetch script so a fresh clone
isn't an empty scene.

**What's actually wrong today.** Not the prop choice — `rooms.ts` hand-authors a `RoomSpec` per place id,
and someone did put kitchen furniture in the market hall. It's the **layout rule**: wall props are spread
evenly along the back wall by index, floor props are placed on a **ring** around the room's centre at a
random quarter-turn, with no grouping and no collision test. A chair cannot be tucked under its table
because the schema has no way to say so. `place.name` is never read.

**Touches.** `rooms.ts` (`RoomSpec` gains grouping/anchoring/facing; `roomFor` reads the name), new
`PlaceKind` values and specs for the built structures, `RoomScene.buildRoom`'s placement, `art.ts`, plus
the fetch script and the `Textures/` trap documented in `assets/README.md`.

**Riskiest assumption.** *That the 141 room GLBs cover the new building types* — a bar, a hall, a granary.

**Test it cheaply.** List the 141 filenames against the planned kinds **during M10**, before the site list
is finalised. If there are no tavern props, the site list changes, not the art.

**Why it's late.** Pure client, zero risk to the simulation, maximum visual payoff — and good work to hold
in reserve if the economy milestones drag.

## M18 — The chronicle and the myth

**Delivers.** Four carried-over items, all small and all in the same neighbourhood.

- **The detail window becomes a budget in lines, not colony-days.** `ChronicleRules.DetailDays = 60` is
  speed-blind: at Overnight a colony day is 15 real seconds, so 60 days is a **15-real-minute** window.
  The 3,787-day run kept quotable lines for 60 days and bare tallies for 3,727. A line budget is
  speed-blind in the right direction.
- **Dropped lines get summarised rather than discarded** — today both the per-day overflow past
  `LinesPerDay = 12` and the aged-out days are simply thrown away.
- **The myth is told what is new since the last telling.** The prompt currently supplies the previous
  telling and the full evidence list but never marks which evidence is new, while instructing it to keep
  most of the last telling — which is why 158 tellings converged to near word-for-word. `MythTelling.Evidence`
  already stores the count at telling time and is used only as a gate; the diff is assembled host-side.
- **The five placeholder fragments in `Revelations.cs` get real content**, Jeremy's to author. Two carry
  mechanical effects (`charge-lines`, `stair-seal`) that need re-deciding alongside the prose.

## M19 — The title

Rename last — but **run the trademark and storefront search now**, in parallel with M8. DESIGN §13 has
flagged it since September as "worth checking early… before it hardens into filenames", and it has never
been done. It is a non-code task whose outcome determines filenames, the chronicle header and possibly the
repo name; deferring the *search* to the end risks a forced rename after eleven milestones.

**Recommendation: *Emberloom: What They Left Running*.** "Running" is doing real work — it is literally
true of the Loom, of the colony as a process, and of the overnight runs themselves; it says *they are gone
and it is still going*, which is the premise. "Left Behind" says only that they are gone, and is a phrase
already worn smooth by other work. "Project" in front reads as an internal codename and weakens a
storefront name — keep it for the repository, where it is doing its job. The search should cover
*Emberloom*, since that is the word becoming the brand.

---

## Deferred, with reasons

- **Multiple-choice forks.** Generalising `Stance` touches six places including a hard-coded three-literal
  schema, the parser, council selection and the fallback vote — and `ForkRules.Pulls` assumes exactly two
  opposed drives, which an N-option fork doesn't have. Build it once, later, when there are two or three
  uses. The best first customer is a tax rate. Cheap early test when the time comes: bench an N-way enum
  deliberation against the 3B and measure schema adherence — a model risk, not a code risk.
- **Commercial job slots.** Recommend cutting. The utility scorer **is** the job market: it already
  allocates labour by desire, location and risk tolerance. Slots put a second allocator on top of the
  first and the two will fight over the same decision. Residential capacity is different and is in M15 —
  it gates Rest and "there was nowhere to sleep" is visible in the feed. Job slots are a spreadsheet
  nobody can see. Revisit only with sim evidence that the scorer under-allocates to a needed job.
- **A literal tax.** A percentage on earned tokens is the same arithmetic as splitting output at the point
  of production, plus a knob and a UI. The one thing it buys that a split doesn't is *something to vote
  on* — so revisit it as the first customer of multiple-choice forks, not before.
- **Runtime `CityMap.Add`.** A generous fixed site list reads as growth, and scaffold-to-building is more
  legible than a building appearing from nothing. Revisit only if a run exhausts the list.
- **Unity client, custom art, hosted deployment (auth + budget policy).** Unchanged from DESIGN §13.
  `MindRequest.ColonyId` already exists for the hosted case.

## Carried over from v1

| Item | Home |
|---|---|
| Chronicle detail window is speed-blind; dropped lines discarded | M18 |
| Myth retellings converge — prompt never says what is new | M18 |
| `Revelations.cs` holds five placeholders | M18 |
| No persistence of any kind | M11 |
| Trace text retention capped at 40 calls, early calls unrecoverable | M11 |
| Art kits need a fetch script or installer | M17 |
| Trademark/storefront search never done | Now, in parallel with M8 |
| Kindling fallback leaves a colonist permanently unnameable | M9 |
| No name-earning deed survives the mid-game | M9 |
| `Build` has no world effect | M10 |
| A fork-printed mind's Kindling never hears the colony argued for it (`Provenance` unused for that path) | M10 |
| `PopulationCap = 20` is a thinking budget masquerading as a constant | Decided at M14 |
| Core forks have no ask-again cooldown while Print and Ascend do | Name it at M10; probably intentional |
| Marketability still deferred | After M17, when it can be seen |
| DESIGN §7 says retellings come 3 days apart; code says 30 | Fix with M18 |
| DESIGN §7 tier table misdescribes Myth as written "at coinage" | Fix with M18 |
| `README.md:1` has a stray duplicated M1 table row above the title | Fix in M8 |
| `SeedRoller`'s doc comment is stale (M5 and M6 shipped) | Fix in M8 |

## Verification

**Per milestone**, in order of what catches most:

1. **`dotnet test` stays green** — 193 tests at the start, growing. Run before every commit (CLAUDE.md).
   Use `-o scratch/testout` whenever a server is holding the normal output open.
2. **The headless sim is the acceptance instrument**, not the browser. Every milestone from M8 onward has
   a named `src/Emberloom.Sim` run with a number to read. Balance claims are settled there, cheaply, before
   anything reaches the client.
3. **`--sweep` after any `ActionCatalog` change**, compared against M8's recorded baseline. The bar is
   already written in the sweep's doc comment: physical drives low, Standing/Meaning/Wonder carrying the
   pressure. M13 adds a Belonging supplier and *will* move it.
4. **A long run before calling a milestone done** — at Overnight, read the chronicle rather than watching.
   That is what the chronicle is for, and after M18 it will actually hold the whole run.
5. **Model-facing changes go through `Emberloom.Bench` first** (M9's names, M14's worded stakes, and any
   future N-way schema). An afternoon of samples beats a week of balance work on a prompt the model
   can't follow.
6. **The no-mind path stays covered.** `BodyWithoutMindTests` and
   `AnUnreachableMindStillProducesAColonistWhoActs` must keep passing at every milestone — with names,
   building, tokens, the work-fed Loom and wellbeing all being Layer 1, a colony with Ollama switched off
   should still do all of it.

**End-to-end, at the close of the sequence:** start a colony at 4 founders in Hybrid mode, run it to the
first raised building, save it, restart the process, load it, confirm the colony continues with its
people named and its buildings standing, then run it overnight and read the chronicle in the morning.
