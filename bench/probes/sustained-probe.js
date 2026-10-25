// Does this machine hold ten tokens a second, or only reach it?
//
// Every measurement that said "fast" was taken in a short burst after an idle spell: the M0 bench
// runs three samples per scenario with gaps, and these probes have been a handful of calls at a
// time. Every measurement that said "slow" came out of the game, where the governor empties a
// thirty-deep queue back to back with no gaps at all.
//
// So: the same call, twenty times, nose to tail, and watch what happens to the rate.

const OLLAMA = 'http://127.0.0.1:11434/api/generate';

const PROMPT = ('The colony stands in the plaza. The question is whether to let the Loom print '
  + 'another mind. The stores hold. For: more company, more hands. Against: the charge is spent '
  + 'and gone; kept, it could light the dark places or open a door that will not open. ')
  .repeat(12) + '\nSay where you stand, in the required JSON.';

const SCHEMA = {
  type: 'object',
  properties: {
    stance: { type: 'string', enum: ['for', 'against', 'abstain'] },
    reasoning: { type: 'string' },
    line: { type: 'string' },
    conviction: { type: 'number', minimum: 0, maximum: 1 },
  },
  required: ['stance', 'reasoning', 'line', 'conviction'],
};

const ns = (v) => (v ?? 0) / 1e9;

(async () => {
  console.log('Twenty deliberation-shaped calls, nose to tail, llama3.2:3b.\n');
  console.log('run   out    gen   tok/s   elapsed');

  const started = Date.now();
  const rates = [];

  for (let i = 1; i <= 20; i++) {
    const r = await fetch(OLLAMA, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        model: 'llama3.2:3b', prompt: PROMPT, stream: false, keep_alive: '30m', think: false,
        format: SCHEMA, options: { num_predict: 128, temperature: 0.8, num_ctx: 4096 },
      }),
    });
    const j = await r.json();
    const gen = ns(j.eval_duration);
    const out = j.eval_count ?? 0;
    const rate = out / (gen || 1);
    rates.push(rate);

    console.log(
      String(i).padStart(3),
      String(out).padStart(5),
      gen.toFixed(1).padStart(7) + 's',
      rate.toFixed(2).padStart(7),
      ((Date.now() - started) / 1000).toFixed(0).padStart(8) + 's',
    );
  }

  const mean = (a) => a.reduce((x, y) => x + y, 0) / a.length;
  console.log(`\nfirst five ${mean(rates.slice(0, 5)).toFixed(2)} tok/s`
    + ` · last five ${mean(rates.slice(-5)).toFixed(2)} tok/s`);
})();
