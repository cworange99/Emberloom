import { useEffect, useState } from 'react'
import type { Colonist, ColonistDetail } from './types'

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
 * The seed of the Mind Inspector. At M1 there are no beliefs and no model trace to show, so it
 * shows what the body actually has: drives, what it chose, what it nearly chose instead, the raw
 * episodic rows, and the numeric ledger. The tabs arrive at M4 once there is more to fill them.
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
