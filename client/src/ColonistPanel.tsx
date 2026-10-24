import { Fragment, useCallback, useEffect, useState, type ReactNode } from 'react'
import type {
  BeliefRow,
  CallText,
  Colonist,
  ColonistDetail,
  MemoryRow,
  PersonRef,
  Revelation,
  TraceRow,
} from './types'

const DRIVE_ORDER = ['sustenance', 'rest', 'safety', 'belonging', 'standing', 'meaning', 'wonder']

export type InspectorTab = 'now' | 'memory' | 'beliefs' | 'relationships' | 'trace'

/**
 * Where the inspector is looking: a tab, and optionally one row on it to bring into view. Held by
 * the app rather than the inspector, so it survives switching colonists — comparing two minds'
 * traces should not mean picking the Trace tab again every time.
 */
export interface InspectorFocus {
  tab: InspectorTab
  rowId?: string
}

/** What every row needs in order to link somewhere else. */
interface Links {
  focus: InspectorFocus
  goTo: (focus: InspectorFocus) => void
  openColonist: (id: string) => void
  openPlace: (id: string) => void
}

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
 * The Mind Inspector — five tabs, per the design: Now, Memory, Beliefs, Relationships, Trace.
 *
 * The tabs are where things live; the links are the point. A belief opens onto the memories it came
 * from, a memory onto the place it happened and the people who were there, and a belief or memory
 * onto the model call that wrote it — prompt, response and all. Every chain can be walked end to
 * end, and walked back from the Trace tab to whatever a call produced.
 */
export function ColonistDetailView({
  id,
  focus,
  onFocus,
  onBack,
  onOpenColonist,
  onOpenPlace,
  placeNames,
}: {
  id: string
  focus: InspectorFocus
  onFocus: (focus: InspectorFocus) => void
  onBack: () => void
  onOpenColonist: (id: string) => void
  onOpenPlace: (id: string) => void
  placeNames: ReadonlyMap<string, string>
}) {
  const [detail, setDetail] = useState<ColonistDetail | null>(null)

  useEffect(() => {
    let cancelled = false

    // A different colonist: drop the last one's detail rather than show it under the new name
    // until the first poll lands.
    setDetail(null)

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

  const loaded = detail !== null

  // Bring a linked row into view. Runs again when the detail first arrives, because a link can open
  // a colonist whose rows have not rendered yet — but not on every poll, or the panel would keep
  // yanking the scroll position back to the same row.
  useEffect(() => {
    if (!loaded || !focus.rowId) return
    document
      .getElementById(rowDomId(focus.rowId))
      ?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  }, [focus, loaded])

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
  const links: Links = { focus, goTo: onFocus, openColonist: onOpenColonist, openPlace: onOpenPlace }

  const tabs: Array<{ key: InspectorTab; label: string; count?: number }> = [
    { key: 'now', label: 'Now' },
    { key: 'memory', label: 'Memory', count: detail.consolidated.length },
    { key: 'beliefs', label: 'Beliefs', count: detail.beliefs.length },
    { key: 'relationships', label: 'Ties', count: detail.relationships.length },
    { key: 'trace', label: 'Trace', count: detail.traceTotal },
  ]

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
              {colonist.isAlive ? colonist.lineage : 'dead'} · {colonist.ageDays.toFixed(0)} of ~
              {colonist.lifespanDays.toFixed(0)} days
              {colonist.daysLostToDespair > 1 &&
                ` · ${colonist.daysLostToDespair.toFixed(0)} fewer than they were born with`}
              {colonist.isAlive && colonist.isElder && ' · slowing'}
              {colonist.isAlive && (
                <>
                  {' · '}
                  {colonist.mood} · {colonist.wellbeingWord} ·{' '}
                  {colonist.tokens.toFixed(2)} saved
                  {colonist.eveningsBought > 0 &&
                    ` · ${colonist.eveningsBought} evening${colonist.eveningsBought === 1 ? '' : 's'} bought`}
                  {' · at '}
                  <PlaceLink
                    id={colonist.placeId}
                    name={placeNames.get(colonist.placeId) ?? colonist.placeId}
                    links={links}
                  />
                </>
              )}
            </p>
            {colonist.parents.length > 0 && (
              <p>
                born to{' '}
                {colonist.parents.map((parent, index) => (
                  <span key={parent.id}>
                    {index > 0 && ' and '}
                    <PersonLink person={parent} links={links} />
                  </span>
                ))}
              </p>
            )}
          </div>
        </div>

        {/* The dead can be read in the inspector, but not spoken to or shown anything. */}
        {colonist.isAlive && (
          <>
            <SpeakBox colonistId={id} name={colonist.displayName} />
            <RevealBox colonistId={id} name={colonist.displayName} />
          </>
        )}

        <div className="tabs" role="tablist" aria-label="Mind Inspector">
          {tabs.map((tab) => (
            <button
              key={tab.key}
              role="tab"
              id={`inspector-tab-${tab.key}`}
              aria-selected={focus.tab === tab.key}
              aria-controls="inspector-panel"
              title={tab.key === 'relationships' ? 'Relationships' : undefined}
              onClick={() => onFocus({ tab: tab.key })}
            >
              {tab.label}
              {tab.count !== undefined && <span className="tab-count">{tab.count}</span>}
            </button>
          ))}
        </div>

        <div role="tabpanel" id="inspector-panel" aria-labelledby={`inspector-tab-${focus.tab}`}>
          {focus.tab === 'now' && <NowTab detail={detail} links={links} />}
          {focus.tab === 'memory' && <MemoryTab detail={detail} links={links} />}
          {focus.tab === 'beliefs' && <BeliefsTab detail={detail} links={links} />}
          {focus.tab === 'relationships' && <RelationshipsTab detail={detail} links={links} />}
          {focus.tab === 'trace' && <TraceTab colonistId={id} detail={detail} links={links} />}
        </div>
      </div>
    </>
  )
}

// ---- Speak ----------------------------------------------------------------------------------

const MAX_WORDS = 280

/** Shown when a verb's request fails outright, as opposed to the colony refusing it. */
export const UNREACHABLE = "Couldn't reach the colony — is the server running?"

/**
 * Speak, the second verb: say something to this one colonist. It is remembered at once and weighed
 * heavily; their answer comes when the governor has a mind free, over their head and in the feed.
 */
function SpeakBox({ colonistId, name }: { colonistId: string; name: string }) {
  const [text, setText] = useState('')
  const [status, setStatus] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  // A half-written sentence to someone else should not carry over to the next mind opened.
  useEffect(() => {
    setText('')
    setStatus(null)
  }, [colonistId])

  const send = async () => {
    const words = text.trim()
    if (!words || sending) return
    setSending(true)

    try {
      const response = await fetch('/api/verbs/speak', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ colonistId, text: words }),
      })

      if (response.ok) {
        setText('')
        setStatus(`${name} heard you. Their answer comes when a mind is free.`)
      } else {
        const body = (await response.json().catch(() => null)) as { error?: string } | null
        setStatus(body?.error ?? 'They did not hear you.')
      }
    } catch {
      // Not the colony refusing: the request never reached it. Say so plainly, or a stopped
      // server reads as a colonist ignoring you.
      setStatus(UNREACHABLE)
    } finally {
      setSending(false)
    }
  }

  return (
    <form
      className="verb speak"
      onSubmit={(e) => {
        e.preventDefault()
        void send()
      }}
    >
      <label htmlFor="speak-text">Speak to {name}</label>
      <textarea
        id="speak-text"
        rows={2}
        maxLength={MAX_WORDS}
        value={text}
        placeholder="They will remember it. Nobody else will hear."
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault()
            void send()
          }
        }}
      />
      <div className="verb-foot">
        <span className="verb-status">{status ?? `${text.length}/${MAX_WORDS}`}</span>
        <button type="submit" disabled={sending || !text.trim()}>
          speak
        </button>
      </div>
    </form>
  )
}

// ---- Reveal ---------------------------------------------------------------------------------

/**
 * Reveal, the third verb: show this colonist something of the makers'. Irreversible, so it takes
 * two presses — each thing can be shown once, to one mind, and never taken back.
 */
function RevealBox({ colonistId, name }: { colonistId: string; name: string }) {
  const [revelations, setRevelations] = useState<Revelation[] | null>(null)
  const [chosen, setChosen] = useState('')
  const [confirming, setConfirming] = useState(false)
  const [status, setStatus] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const response = await fetch('/api/verbs/revelations')
      if (response.ok) setRevelations((await response.json()) as Revelation[])
    } catch {
      // Leaves the last list up; the verb is refused on the server if it is stale.
    }
  }, [])

  useEffect(() => {
    void load()
    setChosen('')
    setConfirming(false)
    setStatus(null)
  }, [colonistId, load])

  if (!revelations) return null

  const remaining = revelations.filter((r) => r.shownTo === null)
  const picked = remaining.find((r) => r.id === chosen)

  if (remaining.length === 0) {
    return <p className="verb-status verb-note">Everything of the makers' has been shown.</p>
  }

  const reveal = async () => {
    if (!picked) return
    if (!confirming) {
      setConfirming(true)
      return
    }

    try {
      const response = await fetch('/api/verbs/reveal', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revelationId: picked.id, colonistId }),
      })
      const body = (await response.json().catch(() => null)) as { error?: string } | null
      setStatus(response.ok ? `${name} has seen it. It cannot be unseen.` : (body?.error ?? 'It could not be shown.'))
    } catch {
      setStatus(UNREACHABLE)
    } finally {
      setConfirming(false)
      setChosen('')
      void load()
    }
  }

  return (
    <form
      className="verb reveal"
      onSubmit={(e) => {
        e.preventDefault()
        void reveal()
      }}
    >
      <label htmlFor="reveal-pick">Reveal something of the makers'</label>
      <select
        id="reveal-pick"
        value={chosen}
        onChange={(e) => {
          setChosen(e.target.value)
          setConfirming(false)
          setStatus(null)
        }}
      >
        <option value="">choose what to show {name}…</option>
        {remaining.map((r) => (
          <option key={r.id} value={r.id}>
            {r.title}
          </option>
        ))}
      </select>
      <div className="verb-foot">
        <span className="verb-status">
          {status ??
            (confirming
              ? 'Irreversible. Press again to show them.'
              : picked?.unlocks ?? `${remaining.length} left · each shown once, to one mind`)}
        </span>
        <button type="submit" disabled={!picked}>
          {confirming ? 'show them' : 'reveal'}
        </button>
      </div>
    </form>
  )
}

// ---- Now ------------------------------------------------------------------------------------

/** What they are thinking, doing and weighing right now — and who the Loom made them. */
function NowTab({ detail, links }: { detail: ColonistDetail; links: Links }) {
  const { colonist } = detail

  // The trace is newest first, so these are the latest of each.
  const thoughtCall = detail.trace.find((row) => row.callType === 'IdleThought')
  const kindling = detail.trace.find((row) => row.callType === 'Kindling')

  return (
    <>
      <section>
        <h3>
          On their mind <span className="tagline mind">idle thought · fast lane</span>
        </h3>
        {colonist.thought ? (
          <>
            <p className="thought">“{stripQuotes(colonist.thought)}”</p>
            {thoughtCall && (
              <p className="row-meta">
                <JumpLink onClick={() => links.goTo({ tab: 'trace', rowId: thoughtCall.id })}>
                  thought on d{thoughtCall.day.toFixed(1)} · see the call
                </JumpLink>
              </p>
            )}
          </>
        ) : (
          <p className="empty">Nothing has come to words yet.</p>
        )}
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
                {option.partnerId && (
                  <>
                    {' with '}
                    <PersonLink
                      person={{ id: option.partnerId, name: option.partnerName ?? option.partnerId }}
                      links={links}
                    />
                  </>
                )}
              </span>
              <span className="option-score">{option.score.toFixed(3)}</span>
            </div>
          ))}
        </div>
      </section>

      <section>
        <h3>Drives</h3>
        <DriveBars drives={colonist.drives} dominant={colonist.dominantDrive} />
      </section>

      <section>
        <h3>Weights — the seam</h3>
        <DriveBars drives={normaliseWeights(colonist.weights)} />
        <p className="row-meta" style={{ marginTop: 8 }}>
          Shown as a share of the 0.2–2.0 range. Written once by Kindling, nudged by Reflection, and
          read by the body on every tick without further inference.
        </p>
      </section>

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
        {colonist.epithet && (
          <p className="row-meta" style={{ marginTop: 10 }}>
            known for <b>{colonist.epithet}</b>
          </p>
        )}
        {colonist.nameCandidates.length > 0 && (
          <p className="row-meta" style={{ marginTop: 10 }}>
            might have been called{' '}
            {colonist.nameCandidates.map((n, i) => (
              <span key={n}>
                {i > 0 && ' · '}
                <b>{n}</b>
              </span>
            ))}
          </p>
        )}
        {kindling && (
          <p className="row-meta" style={{ marginTop: 6 }}>
            <JumpLink onClick={() => links.goTo({ tab: 'trace', rowId: kindling.id })}>
              {kindling.wasFallback ? 'the Kindling call that fell back' : 'the Kindling call that wrote this'}
            </JumpLink>
          </p>
        )}
      </section>
    </>
  )
}

// ---- Memory ---------------------------------------------------------------------------------

function MemoryTab({ detail, links }: { detail: ColonistDetail; links: Links }) {
  const { colonist } = detail

  return (
    <>
      <section>
        <h3>Consolidated — what the days came to</h3>
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
                    <Valence value={memory.valence} /> · salience {memory.salience.toFixed(3)}
                    {memory.recallCount > 0 && ` · recalled ${memory.recallCount}×`}
                  </>
                }
                sources={memory.sources}
                formedBy={memory.formedByCallId}
                trace={detail.trace}
                links={links}
              />
            ))}
          </div>
        )}
      </section>

      <section>
        <h3>Episodic — most salient</h3>
        {detail.recentMemories.length === 0 ? (
          <p className="empty">Nothing has happened to them yet.</p>
        ) : (
          <div className="rows">
            {detail.recentMemories.map((memory) => (
              <div
                className={links.focus.rowId === memory.id ? 'row is-focused' : 'row'}
                id={rowDomId(memory.id)}
                key={memory.id}
              >
                <span className="row-when">d{memory.day.toFixed(1)}</span>
                <div>
                  <p className="row-text">{memory.text}</p>
                  <p className="row-meta">
                    <Valence value={memory.valence} /> · salience {memory.salience.toFixed(3)} ·{' '}
                    <Where memory={memory} links={links} />
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
    </>
  )
}

// ---- Beliefs --------------------------------------------------------------------------------

function BeliefsTab({ detail, links }: { detail: ColonistDetail; links: Links }) {
  return (
    <section>
      <h3>Beliefs — each traceable to its source</h3>
      {detail.beliefs.length === 0 ? (
        <p className="empty">Nothing has happened to them that they have made up their mind about.</p>
      ) : (
        <div className="rows">
          {detail.beliefs.map((belief: BeliefRow) => (
            <Chain
              key={belief.id}
              id={belief.id}
              when="held"
              text={`“${belief.claim}”`}
              meta={
                <>
                  formed d{belief.formedOnDay.toFixed(1)} · confidence {belief.confidence.toFixed(2)}
                  {belief.recallCount > 0 && ` · recalled ${belief.recallCount}×`}
                  {belief.reinforcements > 0 && ' · reinforced ' + belief.reinforcements + '×'}
                  {belief.contradictsColonyMyth && (
                    <>
                      {' · '}
                      <b className="neg">contradicts the colony myth</b>
                    </>
                  )}
                </>
              }
              sources={belief.sources}
              formedBy={belief.formedByCallId}
              reinforcedBy={belief.lastReinforcedByCallId}
              trace={detail.trace}
              links={links}
              emphasis
            />
          ))}
        </div>
      )}
      <p className="row-meta" style={{ marginTop: 10 }}>
        A belief that cites a memory which does not exist is discarded before it is ever shown.
      </p>
    </section>
  )
}

// ---- Relationships --------------------------------------------------------------------------

function RelationshipsTab({ detail, links }: { detail: ColonistDetail; links: Links }) {
  return (
    <section>
      <h3>Relationship ledger — how they read each person</h3>
      {detail.relationships.length === 0 ? (
        <p className="empty">They have not spent time with anyone yet.</p>
      ) : (
        <>
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
                <Fragment key={relationship.towardId}>
                  <tr>
                    <td>
                      <span className="rel-name">
                        <PersonLink
                          person={{ id: relationship.towardId, name: relationship.towardName }}
                          links={links}
                        />
                      </span>
                      <br />
                      <span className="rel-stage">{relationship.stage}</span>
                    </td>
                    <td>{signed(relationship.affinity)}</td>
                    <td>{signed(relationship.trust)}</td>
                    <td>{signed(relationship.respect)}</td>
                    <td>{relationship.sharedMemories}</td>
                  </tr>
                  {relationship.narrative && (
                    <tr className="rel-note">
                      <td colSpan={5}>“{relationship.narrative}”</td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
          <p className="row-meta" style={{ marginTop: 10 }}>
            One direction only — how {detail.colonist.displayName} reads them, not how they read back.
            The numbers are what the body runs on; a line in quotes is what Reflection wrote.
          </p>
        </>
      )}
    </section>
  )
}

// ---- Trace ----------------------------------------------------------------------------------

function TraceTab({
  colonistId,
  detail,
  links,
}: {
  colonistId: string
  detail: ColonistDetail
  links: Links
}) {
  const [only, setOnly] = useState('all')

  // A link into the trace names one row; a filter hiding it would make the link look broken.
  useEffect(() => {
    if (links.focus.rowId) setOnly('all')
  }, [links.focus.rowId])

  if (detail.traceTotal === 0) {
    return (
      <section>
        <h3>Every call this mind has made</h3>
        <p className="empty">
          No calls yet. Everything in this inspector was produced by the body, with no inference at
          all.
        </p>
      </section>
    )
  }

  const rows = only === 'all' ? detail.trace : detail.trace.filter((row) => row.callType === only)
  const tokens = detail.traceSummary.reduce((sum, s) => sum + s.tokens, 0)
  const hidden = detail.traceTotal - detail.trace.length

  return (
    <>
      <section>
        <h3>Every call this mind has made</h3>
        <table className="trace-summary">
          <thead>
            <tr>
              <th>Call</th>
              <th>Calls</th>
              <th>Tokens</th>
              <th>Time</th>
              <th>Fallback</th>
            </tr>
          </thead>
          <tbody>
            {detail.traceSummary.map((s) => (
              <tr key={s.callType}>
                <td>{callLabel(s.callType)}</td>
                <td>{s.calls}</td>
                <td>{s.tokens.toLocaleString()}</td>
                <td>{s.seconds.toFixed(0)}s</td>
                <td className={s.fallbacks > 0 ? 'neg' : undefined}>{s.fallbacks}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="row-meta" style={{ marginTop: 8 }}>
          {detail.traceTotal} call{detail.traceTotal === 1 ? '' : 's'} · {tokens.toLocaleString()}{' '}
          tokens. Prompts and responses are
          kept for the last 40 calls, on this machine — the inference server remembers nothing.
        </p>
      </section>

      <section>
        <div className="trace-filter" role="group" aria-label="Filter the trace by call type">
          {['all', ...detail.traceSummary.map((s) => s.callType)].map((type) => (
            <button key={type} aria-pressed={only === type} onClick={() => setOnly(type)}>
              {type === 'all' ? 'all' : callLabel(type)}
            </button>
          ))}
        </div>
        <div className="rows">
          {rows.map((row) => (
            <TraceEntry key={row.id} colonistId={colonistId} row={row} links={links} />
          ))}
        </div>
        {hidden > 0 && (
          <p className="row-meta" style={{ marginTop: 10 }}>
            Every call that wrote something is listed, with the most recent idle thoughts.{' '}
            {hidden} older idle thought{hidden === 1 ? ' is' : 's are'} counted above but not listed.
          </p>
        )}
      </section>
    </>
  )
}

/** One call. Opens to show exactly what was sent and what came back. */
function TraceEntry({ colonistId, row, links }: { colonistId: string; row: TraceRow; links: Links }) {
  const focused = links.focus.rowId === row.id
  const [open, setOpen] = useState(focused && row.hasText)
  const [text, setText] = useState<CallText | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    if (focused && row.hasText) setOpen(true)
  }, [focused, row.hasText])

  // Fetched on first open rather than with every poll — the text is the heavy part of a call.
  useEffect(() => {
    if (!open || text) return
    let cancelled = false

    fetch(`/api/colonists/${colonistId}/calls/${encodeURIComponent(row.id)}`)
      .then((response) => (response.ok ? (response.json() as Promise<CallText>) : Promise.reject()))
      .then((data) => {
        if (!cancelled) setText(data)
      })
      .catch(() => {
        if (!cancelled) setFailed(true)
      })

    return () => {
      cancelled = true
    }
  }, [open, text, colonistId, row.id])

  return (
    <div className={focused ? 'row is-focused' : 'row'} id={rowDomId(row.id)}>
      <span className="row-when">d{row.day.toFixed(1)}</span>
      <div>
        <p className="row-text">
          <b>{callLabel(row.callType)}</b>
          {row.wasFallback ? (
            <span className="tagline stub">fallback</span>
          ) : (
            <span className="trace-cost">
              {' · '}
              {row.model} · {row.promptTokens} in / {row.completionTokens} out ·{' '}
              {row.seconds.toFixed(1)}s
            </span>
          )}
        </p>
        <p className="row-produced">{row.produced}</p>
        <p className="row-meta">
          {row.lane} lane
          {row.producedIds.map((artifact) => (
            <span key={artifact}>
              {' · '}
              <JumpLink onClick={() => links.goTo({ tab: tabFor(artifact), rowId: artifact })}>
                {artifact.startsWith('b-') ? 'the belief' : 'the memory'}
              </JumpLink>
            </span>
          ))}
          {' · '}
          {row.hasText ? (
            <button
              className="chain-toggle"
              onClick={() => setOpen((v) => !v)}
              aria-expanded={open}
            >
              {open ? 'hide' : 'show'} prompt &amp; response
            </button>
          ) : row.wasFallback ? (
            <span>no call reached a model</span>
          ) : (
            <span title="Only the most recent 40 calls keep their text">text no longer held</span>
          )}
        </p>

        {open && (
          <div className="call-text">
            {failed ? (
              <p className="empty">This call's text could not be loaded.</p>
            ) : !text ? (
              <p className="empty">Loading…</p>
            ) : (
              <>
                {text.system && <CallBlock label="System" body={text.system} collapsed />}
                {text.prompt && <CallBlock label="Prompt" body={text.prompt} />}
                <CallBlock label="Response" body={pretty(text.response)} />
              </>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

function CallBlock({
  label,
  body,
  collapsed: startCollapsed = false,
}: {
  label: string
  body: string
  collapsed?: boolean
}) {
  // The system prompt is the world rules and the personality — long, and the same on every call —
  // so it starts folded. The prompt and response are what differs, so they start open.
  const [collapsed, setCollapsed] = useState(startCollapsed)

  return (
    <div className="call-block">
      <button className="call-label" onClick={() => setCollapsed((c) => !c)} aria-expanded={!collapsed}>
        {label} <span>{body.length.toLocaleString()} chars</span>
      </button>
      {!collapsed && <pre>{body}</pre>}
    </div>
  )
}

// ---- shared pieces --------------------------------------------------------------------------

/**
 * One row that can be opened to show what produced it.
 *
 * The chain is the whole point of the memory pipeline: a belief is never a free-floating string,
 * it carries the ids of the episodic rows it came from, and those rows are real events from this
 * playthrough rather than anything authored. Collapsed by default so the list stays readable — the
 * claim is what you scan, the chain is what you check. Opened automatically when a link lands here.
 */
function Chain({
  id,
  when,
  text,
  meta,
  sources,
  formedBy,
  reinforcedBy = null,
  trace,
  links,
  emphasis = false,
}: {
  id: string
  when: string
  text: string
  meta: ReactNode
  sources: MemoryRow[]
  formedBy: string | null
  reinforcedBy?: string | null
  trace: TraceRow[]
  links: Links
  emphasis?: boolean
}) {
  const focused = links.focus.rowId === id
  const [open, setOpen] = useState(focused)

  useEffect(() => {
    if (focused) setOpen(true)
  }, [focused])

  const call = formedBy ? trace.find((row) => row.id === formedBy) : undefined
  const reinforcing = reinforcedBy ? trace.find((row) => row.id === reinforcedBy) : undefined

  return (
    <div className={focused ? 'row is-focused' : 'row'} id={rowDomId(id)}>
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
          {call && (
            <>
              {' · '}
              <JumpLink onClick={() => links.goTo({ tab: 'trace', rowId: call.id })}>
                formed by {callLabel(call.callType)} d{call.day.toFixed(1)}
                {call.wasFallback ? ' (fallback)' : ''}
              </JumpLink>
            </>
          )}
          {reinforcing && (
            <>
              {' · '}
              <JumpLink onClick={() => links.goTo({ tab: 'trace', rowId: reinforcing.id })}>
                last reinforced d{reinforcing.day.toFixed(1)}
              </JumpLink>
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
                <span className="chain-text">
                  {source.text}
                  {source.day > 0 && (
                    <span className="chain-where">
                      <Where memory={source} links={links} />
                    </span>
                  )}
                </span>
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

/** Where a memory happened and who else was there — each a link onward. */
function Where({ memory, links }: { memory: MemoryRow; links: Links }) {
  return (
    <>
      {memory.placeId && (
        <>
          at <PlaceLink id={memory.placeId} name={memory.placeName} links={links} />
        </>
      )}
      {memory.others.length > 0 && (
        <>
          {' · with '}
          {memory.others.map((person, index) => (
            <span key={person.id}>
              {index > 0 && ', '}
              <PersonLink person={person} links={links} />
            </span>
          ))}
        </>
      )}
    </>
  )
}

/** Opens another mind in the inspector, on the same tab. */
function PersonLink({ person, links }: { person: PersonRef; links: Links }) {
  return (
    <button
      className="jump"
      onClick={() => links.openColonist(person.id)}
      title={`Open ${person.name} in the inspector`}
    >
      {person.name}
    </button>
  )
}

/** Opens the place's room in the main view, beside the inspector. */
function PlaceLink({ id, name, links }: { id: string; name: string; links: Links }) {
  return (
    <button className="jump" onClick={() => links.openPlace(id)} title={`Look inside ${name}`}>
      {name}
    </button>
  )
}

function JumpLink({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return (
    <button className="jump" onClick={onClick}>
      {children} →
    </button>
  )
}

function Valence({ value }: { value: number }) {
  return (
    <span className={value >= 0 ? 'pos' : 'neg'}>
      valence {value >= 0 ? '+' : ''}
      {value.toFixed(2)}
    </span>
  )
}

const CALL_LABELS: Record<string, string> = { IdleThought: 'Idle thought' }

function callLabel(callType: string) {
  return CALL_LABELS[callType] ?? callType
}

function rowDomId(rowId: string) {
  return `insp-${rowId}`
}

/** Artifact ids carry their kind: b- is a belief, cm- a consolidated memory. */
function tabFor(artifactId: string): InspectorTab {
  return artifactId.startsWith('b-') ? 'beliefs' : 'memory'
}

/** Small models sometimes quote their own sentence; the inspector adds its own quotes. */
function stripQuotes(text: string) {
  return text.replace(/^["“”']+|["“”']+$/g, '').trim()
}

/** Responses are schema-constrained JSON; indent them so a person can read one. */
function pretty(body: string | null) {
  if (!body) return '(empty)'
  try {
    return JSON.stringify(JSON.parse(body), null, 2)
  } catch {
    return body
  }
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
