// Which part of the Deliberation schema costs the time?
//
// Live, Deliberation runs at 1.0-1.1 tok/s on llama3.2:3b while every other deep call on the same
// model manages 3.9-6.5. The range is the tell: 1.0 to 1.1 across every sample, where Dialogue
// scatters from 0.9 to 7.1. A tight band is a deterministic cause, not contention.
//
// Same model, same prompt, same cap, same temperature. Only the schema changes.

const ENDPOINT = 'http://127.0.0.1:11434/api/generate';
const MODEL = 'llama3.2:3b';

// About 670 prompt tokens, which is what a live deliberation carries.
const FILLER = ('The colony stands in the plaza. The question is whether to let the Loom print '
  + 'another mind. The stores hold. There is enough for everyone here, with something behind it. '
  + 'For: a print adds one more mind to the colony, more company, more hands. Against: the charge '
  + 'is spent and gone; kept, it could light the dark places or open a door that will not open. ')
  .repeat(12);

const PROMPT = FILLER + '\nYou are Tuven. Say where you stand, and why, in the required JSON.';

const S = (d) => (d ? { type: 'string', description: d } : { type: 'string' });

const SHAPES = {
  'none (free prose)': null,

  'the real schema': {
    type: 'object',
    properties: {
      stance: { type: 'string', enum: ['for', 'against', 'abstain'] },
      reasoning: S('Why, in one sentence, grounded in this colonist\'s beliefs.'),
      line: S('What they actually say to the others. One quotable sentence.'),
      conviction: { type: 'number', minimum: 0, maximum: 1 },
    },
    required: ['stance', 'reasoning', 'line', 'conviction'],
  },

  'conviction unbounded': {
    type: 'object',
    properties: {
      stance: { type: 'string', enum: ['for', 'against', 'abstain'] },
      reasoning: S(), line: S(),
      conviction: { type: 'number' },
    },
    required: ['stance', 'reasoning', 'line', 'conviction'],
  },

  'no conviction at all': {
    type: 'object',
    properties: {
      stance: { type: 'string', enum: ['for', 'against', 'abstain'] },
      reasoning: S(), line: S(),
    },
    required: ['stance', 'reasoning', 'line'],
  },

  'conviction only, bounded': {
    type: 'object',
    properties: { conviction: { type: 'number', minimum: 0, maximum: 1 } },
    required: ['conviction'],
  },

  'strings only': {
    type: 'object',
    properties: { reasoning: S(), line: S() },
    required: ['reasoning', 'line'],
  },
};

const ns = (v) => (v ?? 0) / 1e9;

async function run(label, schema) {
  const body = {
    model: MODEL,
    prompt: PROMPT,
    stream: false,
    keep_alive: '30m',
    think: false,
    options: { num_predict: 128, temperature: 0.8, num_ctx: 4096 },
  };
  if (schema) body.format = schema;

  const started = Date.now();
  const r = await fetch(ENDPOINT, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const j = await r.json();
  const wall = (Date.now() - started) / 1000;

  const evalS = ns(j.eval_duration);
  const promptS = ns(j.prompt_eval_duration);
  const loadS = ns(j.load_duration);
  const out = j.eval_count ?? 0;

  console.log(
    label.padEnd(24),
    `in ${String(j.prompt_eval_count ?? 0).padStart(4)}`,
    `out ${String(out).padStart(4)}`,
    `| load ${loadS.toFixed(1).padStart(5)}s`,
    `prefill ${promptS.toFixed(1).padStart(5)}s`,
    `gen ${evalS.toFixed(1).padStart(6)}s`,
    `| gen ${(out / (evalS || 1)).toFixed(2).padStart(5)} tok/s`,
    `| wall ${wall.toFixed(1)}s`,
  );
}

(async () => {
  console.log(`Deliberation schema probe — ${MODEL}, ~670-token prompt, num_predict 128\n`);
  console.log('A warm-up first, so the first row does not pay for everyone.\n');
  await run('warm-up (discard)', SHAPES['the real schema']);
  console.log('');
  for (const [label, schema] of Object.entries(SHAPES)) {
    await run(label, schema);
  }
})();
