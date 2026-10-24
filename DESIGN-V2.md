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

### M8's measured baseline — 23 October 2026

Over a full lifespan, `--days 2160 --colonists 4 --quiet`, seed 4. **This is the baseline every later
milestone compares against.** The 8-founder column is the same run at the old starting population.

| | 4 founders | 8 founders |
|---|---|---|
| work | **67.4%** — build 34.4, tend 22.7, forage 10.3, salvage 0.0, clear 0.0 | 68.2% |
| rest | 16.0% | 15.0% |
| social | 8.6% — talk 6.2, teach 2.3 | 9.1% |
| leisure (Wander) | **0.0%** | 0.0% |
| civic | 0.7% | 0.3% |
| travel | 4.5% | 4.5% |
| idle | 2.9% | 2.9% |
| **free** (social + leisure + idle) | **11.4%** — per person 9.1 / 12.1 / 16.8 | 12.0% |
| drive means | sus .21 rest .19 safe .16 **belo .55** stan .42 mean .09 wond .28 | belo .52 |
| first print · first birth | **day 948** · day 673 | day 87 · day 655 |
| population curve | 4 4 4 5 9 9 10 14 15 16 | 8 9 9 14 14 14 20 20 20 20 |

**Four findings that change later milestones.**

1. **A third of the colony's entire existence goes into `Build` — which does nothing.** 34.4% of all
   colonist-ticks, 44,856 completed builds, zero world effect. The colony is *already* trying to build a
   city. M10 does not have to persuade the scorer to allocate labour to construction; it has to give the
   labour already being spent somewhere to land. This retires M10's riskiest assumption before it was run.
2. **Leisure is literally never chosen.** Wander is 0.0% at both populations — it exists only as the
   fallback when nothing scores, and something always scores. A leisure *action* alone will be ignored;
   M13's venue has to relieve a drive nothing else reaches, which is why Belonging matters next.
3. **Belonging is the colony's dominant pressure** at 0.55, nearly three times Sustenance, and Talk is its
   only real supplier. The 4-founder fear was unfounded — 0.55 against 0.52 at eight, so the novelty
   damper is *not* biting at this scale and needs no change. But it confirms where M13's social venue
   should point.
4. **Do not raise `ReliefScale` to buy leisure.** The sweep at 2160 days:
   `0.55 → spread 0.56 · 1.0 → 0.70 · 2.0 → 0.59 · 3.0 → 0.49 · 5.0 → 0.12 · 8.0 → 0.08 · 12.0 → 0.13`.
   At 5.0 every drive is met and the spread collapses — the colony has no priorities and action selection
   becomes noise. 3.0 is the right knob setting and should stay. **Leisure has to come from reallocating
   the 34% that Build is currently wasting, not from more relief.**

Also measured: **Rest spends 16% of the day but completes only 165 times in 2160 days.** A Rest is 48
ticks and the colony reconsiders every 6, so rest is being abandoned mid-sleep almost every time.
Unexplained, probably harmless, worth a look when M12 starts reading rest as wellbeing input.

And `salvage 0.0% / clear 0.0%`: both run dry early — every place reads `salvage 0` by the end. Two of
nine actions are dead for most of a run, which is the "world doesn't grow" problem stated as a number.

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
