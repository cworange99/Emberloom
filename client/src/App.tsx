import { useEffect, useRef, useState } from 'react'
import { CityMap } from './CityMap'
import { ColonistDetailView, Roster, type InspectorFocus } from './ColonistPanel'
import { useColony } from './useColony'

const SPEEDS: Array<{ value: string; label: string }> = [
  { value: 'Paused', label: 'pause' },
  { value: 'Normal', label: '1×' },
  { value: 'Fast', label: '3×' },
  { value: 'Faster', label: '10×' },
  { value: 'Fastest', label: '60×' },
  { value: 'Overnight', label: 'overnight' },
]

export default function App() {
  const { world, state, events, connected, setSpeed, setAutoSlow, restart } = useColony()
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [focus, setFocus] = useState<InspectorFocus>({ tab: 'now' })
  const [follow, setFollow] = useState(true)

  const feedRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (follow && feedRef.current) {
      feedRef.current.scrollTop = feedRef.current.scrollHeight
    }
  }, [events, follow])

  // Opening a colonist keeps whichever tab was open — comparing two minds' beliefs or traces is
  // the common case — and drops any row focus, which belonged to the previous mind.
  const openColonist = (id: string) => {
    setSelectedId(id)
    setFocus((current) => ({ tab: current.tab }))
  }

  if (!world || !state) {
    return (
      <div className="app">
        <p className="status-line">{connected ? 'Waiting for the colony…' : 'Connecting…'}</p>
      </div>
    )
  }

  const { clock } = state

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          What They Left Running
          <span>M4 · the mind inspector</span>
        </div>

        <div className="speeds" role="group" aria-label="Simulation speed">
          {SPEEDS.map((speed) => (
            <button
              key={speed.value}
              aria-pressed={clock.speed === speed.value}
              onClick={() => void setSpeed(speed.value)}
            >
              {speed.label}
            </button>
          ))}
        </div>

        <label className="toggle">
          <input
            type="checkbox"
            checked={clock.autoSlowOnEvent}
            onChange={(e) => void setAutoSlow(e.target.checked)}
          />
          auto-slow
        </label>

        <label className="toggle">
          <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} />
          follow feed
        </label>

        <button className="back" onClick={() => void restart(8)}>
          new colony
        </button>

        <dl className="readouts">
          <div className="readout">
            <dt>Day</dt>
            <dd>{state.day.toFixed(2)}</dd>
          </div>
          <div className={state.isNight ? 'readout is-night' : 'readout'}>
            <dt>Hour</dt>
            <dd>{formatTimeOfDay(state.timeOfDay)}</dd>
          </div>
          <div className="readout">
            <dt>Charge</dt>
            <dd>{state.loomCharge.toFixed(0)}</dd>
          </div>
          <div className="readout">
            <dt>Salvage</dt>
            <dd>{state.artifactsRecovered}</dd>
          </div>
          <div className="readout">
            <dt>Actions</dt>
            <dd>{state.actionsCompleted}</dd>
          </div>
          {/* The Contemplation readout from §6: thinking-per-colony-day falls as you accelerate. */}
          <div className="readout is-mind" title="Deep-lane calls affordable per colony-day at this speed">
            <dt>Contemplation</dt>
            <dd>
              {clock.callsPerColonyDay === null ? '—' : `${clock.callsPerColonyDay.toFixed(1)}/day`}
            </dd>
          </div>

          {/* The governor, exposed. On a game whose whole premise is local AI, how much thinking
              is happening right now is worth watching rather than hiding as a debug leftover. */}
          {state.governor && (
            <>
              <div
                className={state.governor.throttling ? 'readout is-flag' : 'readout'}
                title={
                  state.governor.throttling
                    ? 'A turning point could not be served at this speed, so the simulation slowed itself'
                    : 'Calls waiting. The governor serves one at a time.'
                }
              >
                <dt>Queue</dt>
                <dd>{state.governor.queueDepth}</dd>
              </div>
              <div className="readout is-mind" title="Rolling real-world tokens per minute, against the ceiling">
                <dt>Tok/min</dt>
                <dd>
                  {state.governor.tokensInLastMinute}
                  <span className="of">/{state.governor.tokensPerMinute}</span>
                </dd>
              </div>
              <div className="readout" title="Calls served, and calls refused because something mattered more">
                <dt>Served</dt>
                <dd>
                  {state.governor.served}
                  {state.governor.dropped > 0 && (
                    <span className="of">−{state.governor.dropped}</span>
                  )}
                </dd>
              </div>
            </>
          )}
        </dl>
      </header>

      {!connected && <div className="disconnected">Disconnected — the colony is still running. Reconnecting…</div>}

      <div className="columns">
        <div className="left">
          <CityMap
            places={world.places}
            colonists={state.colonists}
            isNight={state.isNight}
            selectedId={selectedId}
            onSelect={openColonist}
          />

          <div className="feed">
            <div className="feed-head">
              <h2>Event feed</h2>
              <span className="row-meta" style={{ margin: 0 }}>
                green is the body · brass is the mind
              </span>
            </div>
            <div className="feed-list" ref={feedRef}>
              {events.length === 0 ? (
                <p className="feed-empty">Nothing has happened yet. Try a faster speed.</p>
              ) : (
                events.map((event, index) => (
                  <div
                    className={event.notable ? 'feed-row is-notable' : 'feed-row'}
                    key={`${event.tick}-${index}`}
                  >
                    <span className="feed-day">d{event.day.toFixed(2)}</span>
                    <span className={`feed-layer ${event.layer}`}>{event.layer}</span>
                    <span>{event.text}</span>
                  </div>
                ))
              )}
            </div>
          </div>
        </div>

        <aside className="right">
          {selectedId ? (
            <ColonistDetailView
              id={selectedId}
              focus={focus}
              onFocus={setFocus}
              onBack={() => setSelectedId(null)}
              onOpenColonist={openColonist}
            />
          ) : (
            <Roster colonists={state.colonists} selectedId={selectedId} onSelect={openColonist} />
          )}
        </aside>
      </div>
    </div>
  )
}

function formatTimeOfDay(fraction: number) {
  const totalMinutes = Math.floor(fraction * 24 * 60)
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}`
}
