// The schema is innocent: generation ran at ~9 tok/s under every shape. So where does the wall
// time go, and — the question that actually matters — does Ollama's own `total_duration`, which
// is the only number the game records, include time the request spent waiting?
//
// Run A: one request at a time, printing every duration Ollama reports against the client's wall.
// Run B: two at once, which is what a second client does to the first.

const ENDPOINT = 'http://127.0.0.1:11434/api/generate';
const MODEL = 'llama3.2:3b';

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

async function call(tag) {
  const started = Date.now();
  const r = await fetch(ENDPOINT, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      model: MODEL, prompt: PROMPT, stream: false, keep_alive: '30m', think: false,
      format: SCHEMA,
      options: { num_predict: 128, temperature: 0.8, num_ctx: 4096 },
    }),
  });
  const j = await r.json();
  const wall = (Date.now() - started) / 1000;

  const total = ns(j.total_duration);
  const load = ns(j.load_duration);
  const prefill = ns(j.prompt_eval_duration);
  const gen = ns(j.eval_duration);
  const out = j.eval_count ?? 0;

  console.log(
    tag.padEnd(12),
    `out ${String(out).padStart(4)}`,
    `| load ${load.toFixed(1).padStart(5)}`,
    `prefill ${prefill.toFixed(1).padStart(5)}`,
    `gen ${gen.toFixed(1).padStart(5)}`,
    `= parts ${(load + prefill + gen).toFixed(1).padStart(5)}s`,
    `| ollama total ${total.toFixed(1).padStart(6)}s`,
    `| client wall ${wall.toFixed(1).padStart(6)}s`,
    `|| recorded rate ${(out / (total || 1)).toFixed(2).padStart(5)} tok/s`,
    `vs real ${(out / (gen || 1)).toFixed(2)} tok/s`,
  );
}

(async () => {
  console.log(`Where the wall time goes — ${MODEL}\n`);
  console.log('"recorded rate" is what the game writes into a colonist\'s trace.\n');

  console.log('A — one at a time:');
  for (let i = 1; i <= 3; i++) await call(`solo ${i}`);

  console.log('\nB — two at once, which is what a second client does to the first:');
  await Promise.all([call('parallel A'), call('parallel B')]);
})();
