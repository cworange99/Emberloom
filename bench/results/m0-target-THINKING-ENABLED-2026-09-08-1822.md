# M0 benchmark — target profile

Run 2026-09-08 18:22 -05:00 · 3 runs per scenario.

These measurements supersede every throughput estimate in the design's §2, §5 and §6.

## Machine

| | |
|---|---|
| CPU | Intel Core i7-14700K (28 logical cores) |
| GPU | NVIDIA GeForce RTX 5070 · 12 GB · driver 610.74 |
| RAM | 31.8 GB |
| OS | Microsoft Windows 11 Pro 10.0.26200 (X64) |
| Ollama | ollama version is 0.33.3 |
| Profile | **target** |

## Headline — sustained calls per minute

Under the expected call mix (reflection 35%, dialogue 30%, idle 15%, deliberation 10%, coinage 6%, kindling 4%), adjusted for schema retries.

| Deep lane | Fast lane | Sustained calls/min | Schema failures |
|---|---|---:|---:|
| `gemma4:12b` | `llama3.2:3b` | **14.7** | 12/18 |

## `gemma4:12b` (deep) + `llama3.2:3b` (fast)

| Scenario | Lane | Median wall | Median tok/s | Prompt tok | Out tok | Schema fails | Effective wall |
|---|---|---:|---:|---:|---:|---:|---:|
| kindling | deep | 6.26s | 56.8 | 307 | 328 | 2/3 | 18.79s |
| reflection | deep | 6.95s | 57.0 | 613 | 372 | 3/3 | 6.95s |
| dialogue | deep | 1.53s | 69.6 | 471 | 96 | 3/3 | 1.53s |
| deliberation | deep | 1.76s | 63.6 | 493 | 98 | 1/3 | 2.64s |
| coinage | deep | 2.08s | 67.9 | 261 | 112 | 3/3 | 2.08s |
| idle-thought | fast | 0.23s | 199.0 | 279 | 35 | 0/3 | 0.23s |

**Failures**

- `kindling`: unparseable JSON: Expected end of string, but instead reached end of data. LineNumber: 10 | BytePositionInLine: 614.
- `reflection`: $.weight_drift.sustenance: 0.9 above maximum 0.2
- `dialogue`: empty completion
- `deliberation`: $.conviction: 75 above maximum 1
- `coinage`: unparseable JSON: Expected end of string, but instead reached end of data. LineNumber: 1 | BytePositionInLine: 373.

<details><summary>Sample output per scenario</summary>

**kindling** — Once per colonist. The largest single call, and undroppable.

```json
{"name_candidates": ["Kael", "Kaelith", "Kaelen"], "personality": "C-09 is a cautious, methodical and observant. They are deeply skeptical of the community's collective 'truth' just because it is a tradition. They think before they think. They are observant ands it's their way of feeling safe. They don't trust the often- hesitant and more impulsive, more 'heroic' or 'in-**Designation:** C-09 **Sta...
```

**deliberation** — Undroppable. One of these per participant at a fork.

```json
{"stance": "against", "reasoning": "The Loom's charge is a finite, sacred relic of the makers' order, and a new life is a waste of a scrap of their legacy for a nameless sake.", "line": "The Loom is not a toy for the many, but a legacy of the makers; we should not waste a charge on a body that will only demand more from our scarcity.", "conviction": 0.9}
```

**idle-thought** — Fast lane. Writes nothing durable; it is what makes the Now tab read as a mind.

```json
{ "thought" : "Maybe Bram's right, maybe this stupid door doesn't need fixing, but I can still feel the weight of what I'm supposed to be finding." }
```

</details>

<details><summary>Residency (<code>ollama ps</code>) with both lanes warm</summary>

```
llama3.2:3b              1.88 GiB resident  1.88 GiB in VRAM  100% GPU
gemma4:12b               6.87 GiB resident  6.87 GiB in VRAM  100% GPU
```

</details>

