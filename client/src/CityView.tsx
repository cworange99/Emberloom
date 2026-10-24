import { useEffect, useMemo, useRef, useState, type PointerEvent } from 'react'
import type { Colonist, Place, PlaceState } from './types'
import type { CityScene } from './city/CityScene'

interface Props {
  places: Place[]
  placeStates: PlaceState[] | undefined
  colonists: Colonist[]
  isNight: boolean
  selectedId: string | null
  onSelect: (id: string) => void
  onOpenPlace: (id: string) => void
  /** False while a room is showing; the scene stops drawing and keeps its camera. */
  active: boolean
  /** Lines being spoken right now, by colonist id — shown over the speaker's head. */
  speech: ReadonlyMap<string, string>
}

/**
 * The city, in low-poly 3D — the app's main view from M4.5, replacing the schematic map.
 *
 * Clicking a colonist opens their mind in the inspector; clicking a building opens its room.
 * The scene itself is loaded lazily: Three.js is most of the bundle, and the roster, feed and
 * inspector should not wait on it.
 */
export function CityView({
  places,
  placeStates,
  colonists,
  isNight,
  selectedId,
  onSelect,
  onOpenPlace,
  active,
  speech,
}: Props) {
  const hostRef = useRef<HTMLDivElement>(null)
  const sceneRef = useRef<CityScene | null>(null)
  const downRef = useRef<{ x: number; y: number } | null>(null)
  const [ready, setReady] = useState(false)
  const [missingKit, setMissingKit] = useState(false)
  const [tip, setTip] = useState<{ x: number; y: number; text: string } | null>(null)

  // Live place state over the world's once-sent description.
  const live = useMemo(() => new Map((placeStates ?? []).map((s) => [s.id, s])), [placeStates])
  const placeById = useMemo(() => new Map(places.map((p) => [p.id, p])), [places])

  useEffect(() => {
    const host = hostRef.current
    if (!host || places.length === 0) return

    let disposed = false
    let scene: CityScene | null = null

    void import('./city/CityScene').then(async ({ CityScene }) => {
      if (disposed) return
      scene = new CityScene(host)
      sceneRef.current = scene

      const result = await scene.build(places)
      if (disposed) return
      setMissingKit(result.missingKit)
      setReady(true)
    })

    return () => {
      disposed = true
      scene?.dispose()
      sceneRef.current = null
      setReady(false)
    }
  }, [places])

  useEffect(() => {
    if (ready) sceneRef.current?.setColonists(colonists, selectedId)
  }, [ready, colonists, selectedId])

  useEffect(() => {
    if (ready) sceneRef.current?.setNight(isNight)
  }, [ready, isNight])

  useEffect(() => {
    if (ready) sceneRef.current?.setActive(active)
  }, [ready, active])

  useEffect(() => {
    if (ready) sceneRef.current?.setSealed(new Map([...live].map(([id, s]) => [id, s.isBlocked])))
  }, [ready, live])

  useEffect(() => {
    // A site that has been raised reads as built, whatever its progress said on the way.
    if (ready) {
      sceneRef.current?.setBuilt(
        new Map([...live].map(([id, s]) => [id, s.isSite ? s.buildProgress : 1])),
      )
    }
  }, [ready, live])

  const describe = (x: number, y: number) => {
    const hit = sceneRef.current?.pick(x, y)
    if (!hit) return null

    if (hit.kind === 'colonist') {
      const colonist = colonists.find((c) => c.id === hit.id)
      if (!colonist) return null
      const where = placeById.get(colonist.placeId)?.name ?? colonist.placeId
      const doing = colonist.doing ? colonist.doing.toLowerCase() : 'deciding'
      return `${colonist.displayName} — ${doing} at ${where} · read their mind`
    }

    const place = placeById.get(hit.id)
    return place ? `${place.name} — look inside` : null
  }

  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (!ready || event.buttons !== 0) {
      setTip(null)
      return
    }

    const rect = event.currentTarget.getBoundingClientRect()
    const text = describe(event.clientX, event.clientY)
    event.currentTarget.style.cursor = text ? 'pointer' : 'default'
    setTip(text ? { x: event.clientX - rect.left + 14, y: event.clientY - rect.top + 14, text } : null)
  }

  // Orbiting is a drag, so a click is told from an orbit by how far the pointer travelled.
  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    const down = downRef.current
    downRef.current = null
    if (!down || Math.hypot(event.clientX - down.x, event.clientY - down.y) > 5) return

    const hit = sceneRef.current?.pick(event.clientX, event.clientY)
    if (hit?.kind === 'colonist') onSelect(hit.id)
    else if (hit?.kind === 'place') onOpenPlace(hit.id)
  }

  const selected = colonists.find((c) => c.id === selectedId)

  return (
    <div
      className="city"
      ref={hostRef}
      hidden={!active}
      role="img"
      aria-label="The reclaimed city in 3D. Colonists are shown where they are standing; the roster lists them too."
      onPointerMove={onPointerMove}
      onPointerLeave={() => setTip(null)}
      onPointerDown={(e) => (downRef.current = { x: e.clientX, y: e.clientY })}
      onPointerUp={onPointerUp}
    >
      {!ready && <div className="city-loading">building the city…</div>}

      {ready && (
        <div className="city-labels" aria-hidden="true">
          {places.map((place) => {
            const state = live.get(place.id)
            const sealed = state?.isBlocked ?? place.isBlocked
            const salvage = state?.salvageRemaining ?? place.salvageRemaining
            const cleared = state?.clearingProgress ?? place.clearingProgress

            return (
              <div
                key={place.id}
                ref={(element) => sceneRef.current?.setLabel(place.id, element)}
                className={sealed ? 'city-label is-sealed' : 'city-label'}
              >
                {place.name}
                {sealed ? (
                  <small>sealed · {Math.round(cleared * 100)}% cleared</small>
                ) : salvage > 0 ? (
                  <small>{salvage} still to find</small>
                ) : null}
              </div>
            )
          })}

          {[...speech].map(([id, text]) =>
            colonists.some((c) => c.id === id) ? (
              <div
                key={`speech:${id}`}
                ref={(element) => sceneRef.current?.setLabel(`speech:${id}`, element)}
                className="speech"
              >
                {text}
              </div>
            ) : null,
          )}

          {selected && (
            <div
              key={`colonist:${selected.id}`}
              ref={(element) => sceneRef.current?.setLabel(`colonist:${selected.id}`, element)}
              className="city-label is-colonist"
            >
              {selected.displayName}
            </div>
          )}
        </div>
      )}

      {tip && (
        <div className="city-tip" style={{ left: tip.x, top: tip.y }}>
          {tip.text}
        </div>
      )}

      {missingKit && (
        <div className="city-notice">
          Art kit not found at /kit, so the city is drawn in placeholders. See assets/README.md.
        </div>
      )}

      <div className="map-legend">
        <span>drag to orbit · scroll to zoom</span>
        <span>click a colonist to read their mind</span>
        <span>click a building to look inside</span>
        <span>{isNight ? 'night' : 'day'}</span>
      </div>
    </div>
  )
}
