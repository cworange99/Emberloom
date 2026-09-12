import { useEffect, useMemo, useRef, useState, type PointerEvent } from 'react'
import type { Colonist, Place } from './types'
import type { RoomScene } from './city/RoomScene'

interface Props {
  /** The room to show, or null while the city is showing — the last room stays built. */
  placeId: string | null
  places: Place[]
  colonists: Colonist[]
  isNight: boolean
  selectedId: string | null
  /** False while the city is showing; the scene stops drawing. */
  active: boolean
  onSelect: (id: string) => void
  onOpenPlace: (id: string) => void
  onBack: () => void
  /** Lines being spoken right now, by colonist id — shown over the speaker's head. */
  speech: ReadonlyMap<string, string>
}

/**
 * One place's interior, in the main view beside the inspector. Clicking a colonist opens their
 * mind; the city is one click back, and the browser's back button works too.
 */
export function RoomView({
  placeId,
  places,
  colonists,
  isNight,
  selectedId,
  active,
  onSelect,
  onOpenPlace,
  onBack,
  speech,
}: Props) {
  const hostRef = useRef<HTMLDivElement>(null)
  const sceneRef = useRef<RoomScene | null>(null)
  const downRef = useRef<{ x: number; y: number } | null>(null)
  const [mounted, setMounted] = useState(false)
  const [shownId, setShownId] = useState<string | null>(null)
  const [wantedId, setWantedId] = useState(placeId)
  const [walls, setWalls] = useState(true)
  const [missingKit, setMissingKit] = useState(false)
  const [tip, setTip] = useState<{ x: number; y: number; text: string } | null>(null)

  // While the city is showing, keep the last room rather than tearing it down.
  useEffect(() => {
    if (placeId) setWantedId(placeId)
  }, [placeId])

  const place = useMemo(() => places.find((p) => p.id === wantedId) ?? null, [places, wantedId])

  useEffect(() => {
    const host = hostRef.current
    if (!host) return

    let disposed = false
    let scene: RoomScene | null = null

    void import('./city/RoomScene').then(({ RoomScene }) => {
      if (disposed) return
      scene = new RoomScene(host)
      sceneRef.current = scene
      setMounted(true)
    })

    return () => {
      disposed = true
      scene?.dispose()
      sceneRef.current = null
      setMounted(false)
    }
  }, [])

  useEffect(() => {
    const scene = sceneRef.current
    if (!mounted || !scene || !place) return

    let current = true
    void scene.show(place, walls).then((result) => {
      if (!current) return
      setMissingKit(result.missingKit)
      setShownId(place.id)
    })

    return () => {
      current = false
    }
  }, [mounted, place, walls])

  useEffect(() => {
    if (mounted) sceneRef.current?.setColonists(colonists, selectedId)
  }, [mounted, colonists, selectedId])

  useEffect(() => {
    if (mounted) sceneRef.current?.setNight(isNight)
  }, [mounted, isNight])

  useEffect(() => {
    if (mounted) sceneRef.current?.setActive(active)
  }, [mounted, active])

  const occupants = colonists.filter((c) => c.placeId === shownId)

  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (event.buttons !== 0) {
      setTip(null)
      return
    }

    const id = sceneRef.current?.pick(event.clientX, event.clientY)
    const colonist = id ? colonists.find((c) => c.id === id) : undefined
    event.currentTarget.style.cursor = colonist ? 'pointer' : 'default'

    if (!colonist) {
      setTip(null)
      return
    }

    const rect = event.currentTarget.getBoundingClientRect()
    const doing = colonist.doing ? colonist.doing.toLowerCase() : 'deciding'
    setTip({
      x: event.clientX - rect.left + 14,
      y: event.clientY - rect.top + 14,
      text: `${colonist.displayName} — ${doing} · read their mind`,
    })
  }

  // Orbiting is a drag, so a click is told from an orbit by how far the pointer travelled.
  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    const down = downRef.current
    downRef.current = null
    if (!down || Math.hypot(event.clientX - down.x, event.clientY - down.y) > 5) return

    const id = sceneRef.current?.pick(event.clientX, event.clientY)
    if (id) onSelect(id)
  }

  return (
    <div
      className="room"
      ref={hostRef}
      hidden={!active}
      role="img"
      aria-label={`Inside ${place?.name ?? 'a place'}, with whoever is there. The list lists them too.`}
      onPointerMove={onPointerMove}
      onPointerLeave={() => setTip(null)}
      onPointerDown={(e) => (downRef.current = { x: e.clientX, y: e.clientY })}
      onPointerUp={onPointerUp}
    >
      {shownId === null && <div className="city-loading">going inside…</div>}

      <div className="room-bar" onPointerDown={(e) => e.stopPropagation()} onPointerUp={(e) => e.stopPropagation()}>
        <button onClick={onBack}>← city</button>
        <select
          value={wantedId ?? ''}
          onChange={(e) => onOpenPlace(e.target.value)}
          aria-label="Go to another place"
        >
          {places.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <button
          aria-pressed={walls}
          onClick={() => setWalls((w) => !w)}
          title="Show or hide the two far walls"
        >
          walls
        </button>
      </div>

      {shownId !== null && (
        <div className="city-labels" aria-hidden="true">
          {occupants.map((colonist) => (
            <div
              key={colonist.id}
              ref={(element) => sceneRef.current?.setLabel(`colonist:${colonist.id}`, element)}
              className={colonist.id === selectedId ? 'city-label is-colonist' : 'city-label'}
            >
              {colonist.displayName}
            </div>
          ))}
          {occupants
            .filter((colonist) => speech.has(colonist.id))
            .map((colonist) => (
              <div
                key={`speech:${colonist.id}`}
                ref={(element) => sceneRef.current?.setLabel(`speech:${colonist.id}`, element)}
                className="speech"
              >
                {speech.get(colonist.id)}
              </div>
            ))}
        </div>
      )}

      <div className="room-who" onPointerDown={(e) => e.stopPropagation()} onPointerUp={(e) => e.stopPropagation()}>
        <span className="head">{place?.name}</span>
        {occupants.length === 0 ? (
          <div>
            <i>nobody is here right now</i>
          </div>
        ) : (
          <ul>
            {occupants.map((colonist) => (
              <li key={colonist.id}>
                <button
                  className={colonist.id === selectedId ? 'is-selected' : undefined}
                  onClick={() => onSelect(colonist.id)}
                >
                  {colonist.displayName}
                </button>
                <span className="act">
                  {colonist.isTravelling
                    ? 'passing through'
                    : (colonist.doing ?? 'deciding').toLowerCase()}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {tip && (
        <div className="city-tip" style={{ left: tip.x, top: tip.y }}>
          {tip.text}
        </div>
      )}

      {missingKit && (
        <div className="city-notice">
          Art kit not found at /kit, so this room is drawn in placeholders. See assets/README.md.
        </div>
      )}
    </div>
  )
}
