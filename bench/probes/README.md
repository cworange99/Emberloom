# Probes

Two throwaway scripts kept because what they found is worth being able to re-find. Both talk
straight to Ollama at `127.0.0.1:11434` and need nothing built.

```bash
node bench/probes/schema-probe.js
node bench/probes/queue-probe.js
```

**`schema-probe.js`** — does a call's JSON schema cost generation time? Same model, same
~670-token prompt, same cap; only the schema changes, from free prose up to the real Deliberation
shape. Written on 24 October 2026 because Deliberation was the only call carrying a bounded
`Number(0, 1)` and it was running at a sixth of its neighbours' apparent rate.

> **Answer: no.** Generation sits at 8–9.7 tokens a second whatever the schema asks for.

**`queue-probe.js`** — then where does the wall time go? Prints every duration the server reports
against the client's own clock, once serially and once with two requests at a time.

> **Answer: `total_duration` tracks the client's wall clock exactly, and it includes every second
> the request spent queued behind another one.** Measured: 73.9s of `total_duration` against 5.2s
> of load, prefill and generation combined.

That is why `ModelCallRecord` carries `LoadSeconds`, `PrefillSeconds` and `GenerateSeconds`
alongside `Seconds`. **Completion tokens over `Seconds` measures how busy the inference server was
and reports it as how fast the model is** — which is how a perfectly healthy call type came to look
six times slower than every other call on the same model.

Run either while a colony is running and you are the *second* client, so the numbers include
waiting for the game. For a clean baseline, pause the colony first.
