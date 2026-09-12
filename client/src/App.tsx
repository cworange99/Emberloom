import { useEffect, useMemo, useRef, useState } from 'react'
import { CityView } from './CityView'
import { RoomView } from './RoomView'
import { ForkBanner } from './ForkBanner'
import { PrintVerb } from './PrintVerb'
import { PlaceVerbs } from './PlaceVerbs'
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
  const { world, state, events, connected, speech, setSpeed, setAutoSlow, restart } = useColony()
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [focus, setFocus] = useState<InspectorFocus>({ tab: 'now' })
  const [follow, setFollow] = useState(true)
  const [view, setView] = useState<View>(() => viewFromHash())

  // The view lives in the URL hash, so the browser's back button leaves a room and a reload stays
  // in it.
  useEffect(() => {
    const onHashChange = () => setView(viewFromHash())
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  // Tell the server what is on screen, so a conversation in the open room, or involving the
  // colonist in the inspector, is written before one nobody is watching.
  useEffect(() => {
    void fetch('/api/view', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        placeId: view.kind === 'room' ? view.placeId : null,
        colonistId: selectedId,
      }),
    }).catch(() => {})
  }, [view, selectedId])

  // The room view is mounted the first time a room is opened, then kept: going back to the city
  // and in again should not rebuild a scene from nothing.
  const [roomOpened, setRoomOpened] = useState(view.kind === 'room')
  useEffect(() => {
    if (view.kind === 'room') setRoomOpened(true)
  }, [view])

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

  const openRoom = (placeId: string) => {
    window.location.hash = 'room=' + encodeURIComponent(placeId)
  }

  const showCity = () => {
    window.location.hash = ''
  }

  const placeNames = useMemo(
    () => new Map((world?.places ?? []).map((p) => [p.id, p.name])),
    [world],
  )

  if (!world || !state) {
    return (
      <div className="app">
        <p className="status-line">{connected ? 'Waiting for the colony…' : 'Connecting…'}</p>
      </div>
    )
  }

  const { clock } = state
  const roomPlace = view.kind === 'room' ? world.places.find((p) => p.id === view.placeId) : undefined

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          What They Left Running
          <span>M6 · player verbs</span>
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

        <PrintVerb
          charge={state.loomCharge}
          blocked={
            state.fork?.isOpen && state.fork.kind === 'Print'
              ? 'The colony is deciding that right now.'
              : null
          }
          onPrinted={openColonist}
        />

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
            <dd title="The Loom's charge, and what your light and warmth cost it each day">
              {state.loomCharge.toFixed(0)}
              {state.upkeepPerDay > 0 && <span className="of">−{state.upkeepPerDay.toFixed(0)}/d</span>}
            </dd>
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
          <div
            className="readout is-mind"
            title={
              'Deep-lane calls affordable per colony-day at this speed, at ' +
              clock.sustainedCallsPerMinute.toFixed(1) +
              (clock.throughputMeasured
                ? ' calls/min, measured from the calls this machine has actually served'
                : ' calls/min from the benchmark, until enough calls have been served to measure')
            }
          >
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
          {/* The city and a room share the main view. Both stay built, and only the one showing
              draws, so switching between them is instant and keeps each camera where it was. */}
          <div className="stage">
            <CityView
              places={world.places}
              placeStates={state.places}
              colonists={state.colonists}
              isNight={state.isNight}
              selectedId={selectedId}
              onSelect={openColonist}
              onOpenPlace={openRoom}
              active={view.kind === 'city'}
              speech={speech}
            />
            {roomOpened && (
              <RoomView
                placeId={view.kind === 'room' ? view.placeId : null}
                places={world.places}
                colonists={state.colonists}
                isNight={state.isNight}
                selectedId={selectedId}
                active={view.kind === 'room'}
                onSelect={openColonist}
                onOpenPlace={openRoom}
                onBack={showCity}
                speech={speech}
              />
            )}
            {state.fork && <ForkBanner fork={state.fork} onSelect={openColonist} />}
            {roomPlace && (
              <PlaceVerbs
                place={roomPlace}
                state={state.places.find((p) => p.id === roomPlace.id)}
                charge={state.loomCharge}
              />
            )}
          </div>

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
              onOpenPlace={openRoom}
              placeNames={placeNames}
            />
          ) : (
            <Roster colonists={state.colonists} selectedId={selectedId} onSelect={openColonist} />
          )}
        </aside>
      </div>
    </div>
  )
}

type View = { kind: 'city' } | { kind: 'room'; placeId: string }

function viewFromHash(): View {
  const match = /^#room=(.+)$/.exec(window.location.hash)
  return match ? { kind: 'room', placeId: decodeURIComponent(match[1]) } : { kind: 'city' }
}

function formatTimeOfDay(fraction: number) {
  const totalMinutes = Math.floor(fraction * 24 * 60)
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}`
}
