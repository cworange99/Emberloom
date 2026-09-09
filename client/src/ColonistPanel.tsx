import { useEffect, useState } from 'react'
import type { Colonist, ColonistDetail, MemoryRow } from './types'

const DRIVE_ORDER = ['sustenance', 'rest', 'safety', 'belonging', 'standing', 'meaning', 'wonder']

export function DriveBars({
  drives,
  dominant,
  mini = false,
}: {
  drives: Record<string, number>
  dominant?: string
  mini?: boolean
}) {
  return (
    <div className={mini ? 'drives mini' : 'drives'}>
      {DRIVE_ORDER.map((name) => {
        const value = drives[name] ?? 0
        const className =
          name === dominant ? 'drive-fill dominant' : value > 0.6 ? 'drive-fill hot' : 'drive-fill'

        return (
          <div className="drive" key={name} title={`${name} ${value.toFixed(2)}`}>
            <span className="drive-name">{name}</span>
            <span className="drive-track">
              <span className={className} style={{ width: `${Math.round(value * 100)}%` }} />
            </span>
            <span className="drive-val">{value.toFixed(2)}</span>
          </div>
        )
      })}
    </div>
  )
}

export function Roster({
  colonists,
  selectedId,
  onSelect,
}: {
  colonists: Colonist[]
  selectedId: string | null
  onSelect: (id: string) => void
}) {
  return (
    <>
      <div className="panel-head">
        <h2>Colonists · {colonists.length}</h2>
      </div>
      <ul className="roster">
        {colonists.map((colonist) => (
          <li key={colonist.id}>
            <button
              onClick={() => onSelect(colonist.id)}
              aria-current={colonist.id === selectedId}
            >
              <span className="roster-top">
                <span className={colonist.isKindled ? 'roster-name kindled' : 'roster-name'}>
                  {colonist.displayName}
                </span>
                <span className="roster-mood">{colonist.mood}</span>
              </span>
              <span className="roster-doing">
                {colonist.doing ? (
                  <>
                    {colonist.isTravelling ? 'walking to ' : ''}
                    {colonist.doingTarget} <em>{colonist.doing.toLowerCase()}</em>
                  </>
                ) : (
                  <em>deciding</em>
                )}
              </span>
              <DriveBars drives={colonist.drives} dominant={colonist.dominantDrive} mini />
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}

/**
 * The Mind Inspector. Reads top to bottom as the chain the design promises: the seed the Loom was
 * given, the personality a model wrote from it, the weights the body scores with, what it chose
 * and what it nearly chose instead, the beliefs it has come to hold, and the memories behind each
 * one. The tabbed layout arrives at M4; until then the order carries the argument.
 */
export function ColonistDetailView({ id, onBack }: { id: string; onBack: () => void }) {
  const [detail, setDetail] = useState<ColonistDetail | null>(null)

  useEffect(() => {
    let cancelled = false

    const load = async () => {
      try {
        const response = await fetch(`/api/colonists/${id}`)
        if (!response.ok) return
        const data = (await response.json()) as ColonistDetail
        if (!cancelled) setDetail(data)
      } catch {
        // The socket keeps the roster live; a failed detail poll just leaves the last one up.
      }
    }

    void load()
    const timer = window.setInterval(load, 1000)

    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [id])

  if (!detail) {
    return (
      <>
        <div className="panel-head">
          <button className="back" onClick={onBack}>
            ← roster
          </button>
        </div>
        <p className="status-line">Reading…</p>
      </>
    )
  }

  const { colonist } = detail

  return (
    <>
      <div className="panel-head">
        <button className="back" onClick={onBack}>
          ← roster
        </button>
        <h2>Mind Inspector</h2>
      </div>

      <div className="detail">
        <div className="detail-id">
          <div className="sigil">{colonist.designation.replace('-', '')}</div>
          <div>
            <h2>{colonist.displayName}</h2>
            <p>
              {colonist.lineage} · day {colonist.ageDays.toFixed(0)} · {colonist.mood}
            </p>
          </div>
        </div>

        <section>
          <h3>Seed — what the Loom was given</h3>
          <p className="seed">{detail.seed || 'nothing recorded'}</p>
        </section>

        <section>
          <h3>
            Personality{' '}
            {colonist.isKindled ? (
              <span className="tagline mind">written by the mind</span>
            ) : (
              <span className="tagline stub">fallback stub</span>
            )}
          </h3>
          <p className="personality">{detail.personality}</p>
          {colonist.nameCandidates.length > 0 && (
            <p className="row-meta" style={{ marginTop: 10 }}>
              may one day take the name{' '}
              {colonist.nameCandidates.map((n, i) => (
                <span key={n}>
                  {i > 0 && ' · '}
                  <b>{n}</b>
                </span>
              ))}
            </p>
          )}
        </section>

        <section>
          <h3>Drives</h3>
          <DriveBars drives={colonist.drives} dominant={colonist.dominantDrive} />
        </section>

        <section>
          <h3>Doing, and what it nearly did instead</h3>
          <div className="options">
            {detail.considering.map((option, index) => (
              <div
                key={`${option.action}-${option.placeName}-${index}`}
                className={option.chosen ? 'option is-chosen' : 'option'}
              >
                <span>
                  {option.action.toLowerCase()} · {option.placeName}
                </span>
                <span className="option-score">{option.score.toFixed(3)}</span>
              </div>
            ))}
          </div>
        </section>

        <section>
          <h3>Weights — the seam</h3>
          <DriveBars drives={normaliseWeights(colonist.weights)} />
          <p className="row-meta" style={{ marginTop: 8 }}>
            Shown as a share of the 0.2–2.0 range. Written once by Kindling, then read by the body on
            every tick without further inference.
          </p>
        </section>

        <section>
          <h3>Beliefs — each traceable to its source</h3>
          {detail.beliefs.length === 0 ? (
            <p className="empty">
              Nothing has happened to them that they have made up their mind about.
            </p>
          ) : (
            <div className="rows">
              {detail.beliefs.map((belief) => (
                <Chain
                  key={belief.id}
                  id={belief.id}
                  when="held"
                  text={`“${belief.claim}”`}
                  meta={
                    <>
                      formed d{belief.formedOnDay.toFixed(1)} · confidence{' '}
                      {belief.confidence.toFixed(2)}
                      {belief.recallCount > 0 && ` · recalled ${belief.recallCount}×`}
                      {belief.contradictsColonyMyth && (
                        <>
                          {' · '}
                          <b className="neg">contradicts the colony myth</b>
                        </>
                      )}
                    </>
                  }
                  sources={belief.sources}
                  emphasis
                />
              ))}
            </div>
          )}
        </section>

        <section>
          <h3>Consolidated memory — what the days came to</h3>
          {detail.consolidated.length === 0 ? (
            <p className="empty">Nothing has been thought about yet.</p>
          ) : (
            <div className="rows">
              {detail.consolidated.map((memory) => (
                <Chain
                  key={memory.id}
                  id={memory.id}
                  when={`d${memory.day.toFixed(1)}`}
                  text={memory.text}
                  meta={
                    <>
                      <span className={memory.valence >= 0 ? 'pos' : 'neg'}>
                        valence {memory.valence >= 0 ? '+' : ''}
                        {memory.valence.toFixed(2)}
                      </span>{' '}
                      · salience {memory.salience.toFixed(3)}
                      {memory.recallCount > 0 && ` · recalled ${memory.recallCount}×`}
                    </>
                  }
                  sources={memory.sources}
                />
              ))}
            </div>
          )}
        </section>

        <section>
          <h3>Episodic memory — most salient</h3>
          {detail.recentMemories.length === 0 ? (
            <p className="empty">Nothing has happened to them yet.</p>
          ) : (
            <div className="rows">
              {detail.recentMemories.map((memory) => (
                <div className="row" key={memory.id}>
                  <span className="row-when">d{memory.day.toFixed(1)}</span>
                  <div>
                    <p className="row-text">{memory.text}</p>
                    <p className="row-meta">
                      <span className={memory.valence >= 0 ? 'pos' : 'neg'}>
                        valence {memory.valence >= 0 ? '+' : ''}
                        {memory.valence.toFixed(2)}
                      </span>{' '}
                      · salience {memory.salience.toFixed(3)}
                    </p>
                  </div>
                </div>
              ))}
            </div>
          )}
          <p className="row-meta" style={{ marginTop: 10 }}>
            {colonist.memoryCount} rows held ·{' '}
            {colonist.wantsReflection
              ? 'enough unprocessed experience to want a reflection'
              : 'below the reflection threshold'}
          </p>
        </section>

        <section>
          <h3>Relationship ledger</h3>
          {detail.relationships.length === 0 ? (
            <p className="empty">They have not spent time with anyone yet.</p>
          ) : (
            <table className="rel-table">
              <thead>
                <tr>
                  <th>Toward</th>
                  <th>Aff</th>
                  <th>Trust</th>
                  <th>Resp</th>
                  <th>Shared</th>
                </tr>
              </thead>
              <tbody>
                {detail.relationships.map((relationship) => (
                  <tr key={relationship.towardId}>
                    <td>
                      <span className="rel-name">{relationship.towardName}</span>
                      <br />
                      <span className="rel-stage">{relationship.stage}</span>
                    </td>
                    <td>{signed(relationship.affinity)}</td>
                    <td>{signed(relationship.trust)}</td>
                    <td>{signed(relationship.respect)}</td>
                    <td>{relationship.sharedMemories}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <section>
          <h3>Model trace — every call this mind has made</h3>
          {detail.trace.length === 0 ? (
            <p className="empty">
              No calls yet. Everything above was produced by the body, with no inference at all.
            </p>
          ) : (
            <>
              <div className="rows">
                {detail.trace.map((row, index) => (
                  <div className="row" key={`${row.callType}-${row.day}-${index}`}>
                    <span className="row-when">d{row.day.toFixed(1)}</span>
                    <div>
                      <p className="row-text">
                        <b>{row.callType}</b>
                        {row.wasFallback ? (
                          <span className="tagline stub" style={{ marginLeft: 6 }}>
                            fallback
                          </span>
                        ) : (
                          <>
                            {' · '}
                            {row.model} · {row.promptTokens} in / {row.completionTokens} out ·{' '}
                            {row.seconds.toFixed(1)}s
                          </>
                        )}
                      </p>
                      <p className="row-meta">{row.produced}</p>
                    </div>
                  </div>
                ))}
              </div>
              <p className="row-meta" style={{ marginTop: 10 }}>
                {colonist.modelCalls} call{colonist.modelCalls === 1 ? '' : 's'} lifetime
              </p>
            </>
          )}
        </section>
      </div>
    </>
  )
}

/**
 * One row that can be opened to show what produced it.
 *
 * The chain is the whole point of the memory pipeline: a belief is never a free-floating string,
 * it carries the ids of the episodic rows it came from, and those rows are real events from this
 * playthrough rather than anything authored. Collapsed by default so the list stays readable —
 * the claim is what you scan, the chain is what you check.
 */
function Chain({
  id,
  when,
  text,
  meta,
  sources,
  emphasis = false,
}: {
  id: string
  when: string
  text: string
  meta: React.ReactNode
  sources: MemoryRow[]
  emphasis?: boolean
}) {
  const [open, setOpen] = useState(false)

  return (
    <div className="row">
      <span className="row-when">{when}</span>
      <div>
        <p className={emphasis ? 'row-text claim' : 'row-text'}>{text}</p>
        <p className="row-meta">
          {meta}
          {sources.length > 0 && (
            <>
              {' · '}
              <button
                className="chain-toggle"
                onClick={() => setOpen((v) => !v)}
                aria-expanded={open}
                aria-controls={`chain-${id}`}
              >
                {open ? 'hide' : 'from'} {sources.length} memor
                {sources.length === 1 ? 'y' : 'ies'}
              </button>
            </>
          )}
        </p>

        {open && (
          <ol className="chain" id={`chain-${id}`}>
            {sources.map((source) => (
              <li key={source.id} className={source.day === 0 ? 'chain-lost' : undefined}>
                <span className="chain-when">
                  {source.day > 0 ? `d${source.day.toFixed(1)}` : '—'}
                </span>
                <span className="chain-text">{source.text}</span>
                {source.day > 0 && (
                  <span className={source.valence >= 0 ? 'chain-val pos' : 'chain-val neg'}>
                    {source.valence >= 0 ? '+' : ''}
                    {source.valence.toFixed(2)}
                  </span>
                )}
              </li>
            ))}
          </ol>
        )}
      </div>
    </div>
  )
}

function signed(value: number) {
  return `${value >= 0 ? '+' : ''}${value.toFixed(2)}`
}

/** Weights run 0.2–2.0; the bars want 0–1. */
function normaliseWeights(weights: Record<string, number>) {
  const result: Record<string, number> = {}
  for (const [key, value] of Object.entries(weights)) {
    result[key] = Math.min(Math.max((value - 0.2) / 1.8, 0), 1)
  }
  return result
}
