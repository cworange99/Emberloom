# Version 2 — the report

*Emberloom: What They Left Running* · branch `Version-2` · 24 October 2026 · 268 tests green ·
the body still runs with no Ollama at all.

Twelve milestones, M8 to M19, plus two rounds of fixes found by playing it. The design and every
measurement behind it are in [DESIGN-V2.md](DESIGN-V2.md); this is the short version.

---

## What version 2 was for

Version 1 grew a **population**, not a **civilisation**. The colonists worked, aged, argued and died
in a city that never changed. `Build` was in the action catalogue, relieved Meaning, took a third of
the colony's entire waking life — and had **no effect on the world whatsoever**. They had no
leisure, no economy they took part in, no wellbeing that could be hurt, and most of them lived and
died as "C-14".

## What shipped

| | |
|---|---|
| **M8** | Four founders, and `Emberloom.Sim` becomes a measurement instrument: how the day divides, in colonist-ticks |
| **M9** | Everyone is named at birth, from a generator in Core. The three deeds survive as epithets |
| **M10** | **The world grows.** Unbuilt sites from day one; a Build fork raises them; `Build` finally changes something |
| **M11** | **Persistence.** SQLite, versioned document, a colony outlives the process |
| **M12** | **Wellbeing**, read-only: a leaky integrator of how the days are spent |
| **M13** | **Tokens and a paid venue.** Work earns; an evening at a hall costs; Belonging gets a second supplier |
| **M14** | **The Loom, fed by work.** One stock: labour makes it, the living eat it, a print spends it |
| **M15** | **The couplings.** Shortage aggravates hunger; despair shortens a life; shelters have beds |
| **M16** | **Modes and intents.** Simulation / Hybrid / Directed, as one scorer term at three strengths |
| **M17** | **Rooms that look like what the building is**, and `assets/fetch-kits.ps1` so a fresh clone isn't empty |
| **M18** | **The chronicle and the myth.** A line budget, losses summarised by layer, retellings that retell |
| **M19** | **The title**, and the trademark search that should have happened in September |

## What measuring changed, rather than tuned

Every milestone from M8 had a named run with a number to read. Five of those measurements changed
**what got built**, not what got set:

1. **The 30% of the day M10 freed from `Build` went to sleep, not to leisure** (rest 16% → 32%, free
   time unchanged). M13's venue was therefore competing with Rest rather than for slack.
2. **A 3B model will not vote against a print because the stores are thin.** Three worded variants,
   thirty samples each: *thin* produced **more** for-votes than *adequate*, and against never passed
   2 of 30. Scarcity reads as a reason to act. The resource constraint went into the body.
3. **An intent strong enough to be heard was strong enough to be obeyed by someone starving.** No
   constant fixes that, so an intent now fades as need rises — a structural promise, not a number.
4. **The despair coupling took colonies extinct, and the cause was somewhere else**: `Moment`
   subtracted an unclamped urgency, and urgency is `(level × weight)²`, so it runs to 4. The last
   two people in a dying colony evaluated to exactly zero, for ever.
5. **Myth retellings kept ~100% of their wording.** The prompt handed a mind the previous telling and
   the full evidence with no mark on either. Marking what is new took it to **76% kept**.

## The one class of bug — four times

> **A decision must not be a pure function of state that the act of deciding changes.**

- **M12** — `Fork.Pressure`. Seed 4's council refused twelve sites twenty times over 600 days,
  because nothing in a mindless vote could tell them eleven people were sharing two beds.
- **M14** — council *selection* is part of that state. The largest weight in the table is the
  cautious archetype's 1.7 on Safety, and the Print fork sets Belonging against Safety, so a
  cautious colonist was seated **first** at every print.
- **M15** — `Propose` sorted the concatenation, so a nearer site of the wrong kind outranked a
  farther site of the right one. A colony with ten people in six beds proposed a hall.
- **After release** — **the vote argued itself out of existence.** A council deliberates in the
  plaza; `Deliberate` relieves Belonging at 1.20 a day, the strongest supply anywhere; the Print
  fork weighs Belonging against Safety. Debating whether the colony was lonely enough to want
  another person **cured the loneliness**. Seed 4 was asked sixteen times in 2,160 days and refused
  every one — twice from a position where every seated voter had been in favour when the question
  was put.

The first three were decisions frozen because nothing could move them. The fourth moved, against
itself, every time, by construction. It is fixed by `Fork.Standing`: the personal half of a vote is
read **once, when the question is put**, beside the pressure that has been snapshotted for the same
reason since M12.

## The hour a vote took

Not the model being slow. A deliberation call takes about twenty-two seconds on this laptop, and a
three-seat council measures **105 real seconds end to end, costing the colony 0.02 of a day** — the
clock drops to 1x when the question opens and comes back to 60x by itself when it is counted.

Three separate faults were adding up to the hour:

1. **The governor refused undroppable work** when the queue was at depth with nothing droppable in
   it to make room — exactly what the queue looks like when the colony is busy kindling and a fork
   opens. A refused Deliberation is a **council seat that silently never gets a mind**. The depth
   cap exists so the queue does not serve stale work for ever; undroppable work is never stale, so
   it now always enqueues.
2. **A fork with nothing left to wait for stood until its deadline anyway.** The seats that were
   submitted came back inside a minute, and the fork then sat out the rest of its three colony-days
   — **three real hours at 1x, with the auto-slow holding the clock there** — in front of a question
   nobody was still answering. That is the hour, and the "several days in colony time" is the
   deadline it was waiting out. The deadline is now only the backstop.
3. **The auto-slow could be swallowed entirely.** The governor throttle and a fork's auto-slow each
   saved the speed they interrupted, and each guarded itself with *"already at 1x, nothing to do"* —
   true of the speed, false of the hold. A throttle landing first ate the fork's hold, and when it
   lifted the colony went back to 60x **with the vote still open**. Each hold is now a flag and
   nothing is saved: the clock runs at the player's choice or 1x, whichever is slower.

## Where the colony stands now

Seed 4 — the seed the game ships — over a full 2,160-day lifespan, four founders, no mind at all:

| | v1 baseline (M8) | shipped v2 | after the vote fix |
|---|---|---|---|
| work | 67.4% | 46.2% | **46.8%** |
| rest | 16.0% | 30.5% | 25.5% |
| social | 8.6% | 6.0% | **12.0%** |
| leisure | **0.0%** | 4.5% | 4.0% |
| free time | 11.4% | 12.9% | **18.5%** |
| first print | day 948 | **never** | **day 464** |
| ended at | 16 living | 7 living | **20 living** (the cap) |
| died early of despair | — | 8 of 8 lives, 90 days each | **none** |
| wellbeing | — | 0.424 *coping* | **0.488 _well_** |

Prints carried, three seeds, before and after the fix: **0 of 16 → 12 of 19** (seed 4),
15 of 15 (seed 7), 13 of 21 (seed 11). Builds carried on seed 4: **5 of 35 → 12 of 19**.

`--sweep` at the new balance: at `ReliefScale` 3.0 the spread is 0.34, the physical drives sit at
0.15 / 0.19 / 0.27, and Belonging and Standing carry the pressure at 0.49 / 0.48. That is the bar
written in the sweep's own doc comment. 3.0 stays.

## The instruments

Every balance claim in this report can be re-run:

```bash
dotnet run --project src/Emberloom.Sim -- --days 2160 --colonists 4 --seed 4 --quiet
```

| run | the question it answers |
|---|---|
| `--quiet` | how the day divides, in colonist-ticks, over a full lifespan |
| `--forks` | every question the colony faced — **and every day it faced none, and why** |
| `--sweep` | whether `ReliefScale` still sits at the peak of the drive spread |
| `--prices` | what an evening out should cost |
| `--despair` | where the wellbeing→lifespan coupling flips from stable to extinct |
| `--intent` | whether a starving colonist still eats first at every mode strength |
| `Bench --stakes` | whether worded stakes move a council's stance at all |
| `Bench --myth` | how much of a telling survives into the next one |

`--forks` is new, and it exists because of the defect above. **A tally of outcomes cannot tell a
colony that decided against growth from a colony that was never able to decide for it.** Every other
readout counts what happened; this one counts what did not, and names the gate that held it shut.

## What playing it found

Version 2 closed on measurement. Then Jeremy played it, and four days of play found more than the
twelve milestones had — because every instrument built along the way measured *outcomes*, and each
of these was a fault in a *mechanism* that produced perfectly plausible outcomes.

| found by | what it was |
|---|---|
| clicking **new colony** mid-run | the host died writing the outgoing colony's record while the tick loop wrote the same file, and `UnauthorizedAccessException` does not derive from `IOException` |
| noticing **no new minds in two colony-years** | a council deliberates in the plaza, and deliberating relieves the very drive the vote is weighed on — the colony cured its own loneliness by debating it |
| noticing **the health bars are black** | every drive bar in the client had measured 0×0 since M1 — a `<span>` in a block is `display: inline` |
| asking **where is the happiness metric** | it had been on screen since M12, as a single word between the mood and the purse |
| timing **a vote at four minutes** | the laptop reaches 10 tok/s and holds 1.5–6.5, and `CallBudgets` was sized from burst figures six times that |
| **letting it run overnight** | the colony had been unable to save itself for 1,337 consecutive colony days without anything misbehaving |

The last one is the sharpest. The colony ran correctly for nineteen hundred days while being quietly
impossible to keep, and the only reason it surfaced at all is that the run was long enough for a
belief merge to happen and that a log got read when an unrelated check timed out.

> **A system can be wrong in a way that nothing it reports about itself will ever show.** Every one
> of these was invisible to the tests, the sim runs and the chronicle, and visible within minutes to
> somebody using the thing.

## Where it ended

Shut down on 26 October 2026 at day **3,963**, twenty living of forty-four who had lived, on GPU
inference through the devbox.

The save fix got the verification it needed by accident: that colony ran on after the fix went in
and autosaved every colony day for nearly four thousand days.

| | before the fix | after |
|---|---|---|
| autosaves succeeded | **0** | **3,456** |
| autosaves failed | **1,337** (every one) | **1** |
| `belief.id` constraint violations | every save | **0** |

The single failure was a different and much rarer thing — `NOT NULL constraint failed:
colony.document`, once in 3,457, self-recovered on the next colony day. Not diagnosed, not
reproduced, and written down here rather than guessed at.

## The golden rule

Held throughout. Everything version 2 added — wellbeing, tokens, the work-fed Loom, build progress,
capacity, intents, the colony mode, the snapshotted vote — is **Layer 1**: deterministic,
zero model calls, `Emberloom.Core` only. A colony with no Ollama builds, earns, socialises, grows
unhappy, votes and dies; it just does not think about it. `BodyWithoutMindTests` and
`AnUnreachableMindStillProducesAColonistWhoActs` pass at every milestone.

M18 was the sharpest temptation — *"tell the myth what is new since the last telling"* sounds like
something a server should remember. The diff is assembled host-side from colony state and goes out
in the prompt like everything else.

## Still open, and both yours

- **`LifeRules.PopulationCap = 20`** — a thinking budget wearing a constant's clothes. It did not
  bind before this week because the colony could not vote itself bigger; now all three seeds sit at
  20 for most of their lives. Raising it means **minds for the notable and bodies for the rest**,
  which is a milestone of its own.
- **The five fragments in `Revelations.cs`** are placeholder prose. Their two mechanical effects were
  re-decided at M18 and are independent of the words, so new prose drops in without touching the
  body.
