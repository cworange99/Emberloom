// CallBudgets puts deliberation at 128 tokens, measured against llama3.2:3b. gemma4:12b is what
// appsettings.Target.json names as the deep model for this GPU, and it writes longer. A cap is a
// correctness constraint, not a budget hint: a schema-constrained call that hits its cap truncates
// mid-document and the whole result is unparseable.

const OLLAMA = 'http://127.0.0.1:11436/api/generate';

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

async function trial(model, cap, n) {
  let ok = 0;
  const outs = [];
  for (let i = 0; i < n; i++) {
    const r = await fetch(OLLAMA, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        model, prompt: PROMPT, stream: false, keep_alive: '30m', think: false,
        format: SCHEMA, options: { num_predict: cap, temperature: 0.8, num_ctx: 4096 },
      }),
    });
    const j = await r.json();
    outs.push(j.eval_count ?? 0);
    try { JSON.parse(j.response); ok++; } catch { /* the failure being counted */ }
  }
  const mean = outs.reduce((a, b) => a + b, 0) / outs.length;
  console.log(
    `${model.padEnd(14)} cap ${String(cap).padStart(4)}`,
    `| parsed ${ok}/${n}`,
    `| out mean ${mean.toFixed(0).padStart(4)} max ${String(Math.max(...outs)).padStart(4)}`,
    `| hit the cap ${outs.filter((o) => o >= cap).length}/${n}`,
  );
}

(async () => {
  console.log('Does the deliberation cap hold for the model Target names?\n');
  await trial('gemma4:12b', 128, 6);
  await trial('gemma4:12b', 256, 6);
  await trial('llama3.2:3b', 128, 6);
})();
