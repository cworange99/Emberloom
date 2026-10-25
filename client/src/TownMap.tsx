import { useMemo } from 'react'
import type { Colonist, Place, PlaceState } from './types'

/**
 * The town from above, flat and named.
 *
 * The 3D city is the thing you watch; this is the thing you read. It is drawn from the same
 * coordinates the scene uses — `Place.x` and `Place.y` are the city's own grid — so the two agree
 * about where everything is, and it carries the names at a size that does not fight the buildings
 * for room, which is the complaint it exists to answer.
 *
 * Deliberately plain SVG rather than a top-down camera. A flattened 3D view would still be made of
 * models, and the thing wanted here is a diagram: what the places are, which are raised, which are
 * sealed, what connects to what, and where everybody is.
 */
export function TownMap({
  places,
  placeStates,
  colonists,
  selectedId,
  active,
  onSelect,
  onOpenPlace,
  onBack,
}: {
  places: Place[]
  placeStates: PlaceState[]
  colonists: Colonist[]
  selectedId: string | null
  active: boolean
  onSelect: (id: string) => void
  onOpenPlace: (id: string) => void
  onBack: () => void
}) {
  const live = useMemo(() => new Map(placeStates.map((p) => [p.id, p])), [placeStates])

  const here = useMemo(() => {
    const byPlace = new Map<string, Colonist[]>()
    for (const colonist of colonists) {
      const list = byPlace.get(colonist.placeId)
      if (list) list.push(colonist)
      else byPlace.set(colonist.placeId, [colonist])
    }
    return byPlace
  }, [colonists])

  // The city's coordinates are world units — about 120 of them across — so the map is scaled to a
  // fixed size rather than to the pane. A map that reflows as you resize the window is a map you
  // have to re-learn every time; this one is always the same shape and scrolls if it has to.
  const ACROSS = 1060
  const PAD = 86

  const layout = useMemo(() => {
    const xs = places.map((p) => p.x)
    const ys = places.map((p) => p.y)
    const minX = Math.min(...xs)
    const minY = Math.min(...ys)
    const spanX = Math.max(...xs) - minX
    const spanY = Math.max(...ys) - minY

    // One scale for both axes, or the city comes out stretched and stops agreeing with the view
    // you were just looking at.
    const scale = ACROSS / Math.max(spanX, spanY, 1)

    return {
      minX, minY, scale,
      width: spanX * scale + PAD * 2,
      height: spanY * scale + PAD * 2,
    }
  }, [places])

  const { width, height } = layout
  const at = (p: Place) => ({
    x: (p.x - layout.minX) * layout.scale + PAD,
    y: (p.y - layout.minY) * layout.scale + PAD,
  })

  // Each pair once. Neighbours are listed from both ends, and a doubled line draws heavier than a
  // single one, so the map would quietly claim some streets were wider than others.
  const links = useMemo(() => {
    const byId = new Map(places.map((p) => [p.id, p]))
    const seen = new Set<string>()
    const out: Array<{ a: Place; b: Place }> = []

    for (const place of places) {
      for (const id of place.neighbours) {
        const other = byId.get(id)
        if (!other) continue
        const key = [place.id, id].sort().join('|')
        if (seen.has(key)) continue
        seen.add(key)
        out.push({ a: place, b: other })
      }
    }

    return out
  }, [places])

  const selectedPlace = colonists.find((c) => c.id === selectedId)?.placeId ?? null

  return (
    <div className="town-map" hidden={!active}>
      <div className="room-bar">
        <button className="back" onClick={onBack}>
          ← city
        </button>
        <span className="row-meta" style={{ margin: 0 }}>
          the town from above · click a building to look inside · click somebody to read their mind
        </span>
      </div>

      <div className="town-map-scroll">
        <svg
          viewBox={`0 0 ${width} ${height}`}
          // Fits the pane rather than setting its own pixel size, so the whole town is visible at
          // once — which is the only reason to leave the 3D view for it. Everything inside is
          // sized in viewBox units and shrinks with it, so the type is set large enough to survive.
          preserveAspectRatio="xMidYMid meet"
          role="img"
          aria-label="A flat map of the reclaimed city, with every place named."
        >
          {links.map(({ a, b }) => {
            const from = at(a)
            const to = at(b)
            // Right angles, the same L the walkers take, so the map shows the route rather than
            // the straight line nobody can walk.
            return (
              <polyline
                key={`${a.id}|${b.id}`}
                className="map-street"
                points={`${from.x},${from.y} ${to.x},${from.y} ${to.x},${to.y}`}
              />
            )
          })}

          {places.map((place) => {
            const state = live.get(place.id)
            const sealed = state?.isBlocked ?? place.isBlocked
            const isSite = state?.isSite ?? place.kind === 'Site'
            const progress = state?.buildProgress ?? 0
            const people = here.get(place.id) ?? []
            const { x, y } = at(place)

            const kind = sealed ? 'is-sealed' : isSite ? 'is-site' : 'is-standing'
            const r = isSite ? 14 : 21

            return (
              <g
                key={place.id}
                className={`map-place ${kind}${selectedPlace === place.id ? ' is-where' : ''}`}
                transform={`translate(${x}, ${y})`}
                onClick={() => !isSite && onOpenPlace(place.id)}
                style={{ cursor: isSite ? 'default' : 'pointer' }}
              >
                <title>
                  {place.name}
                  {sealed
                    ? ' — sealed'
                    : isSite
                      ? ` — unbuilt ground, ${Math.round(progress * 100)}% raised`
                      : ''}
                  {people.length > 0 ? ` · ${people.map((p) => p.displayName).join(', ')}` : ''}
                </title>

                <circle className="map-node" r={r} />
                {isSite && progress > 0 && (
                  <circle
                    className="map-raised"
                    r={r}
                    // A dashed arc standing in for how far the thing has been raised.
                    strokeDasharray={`${progress * 2 * Math.PI * r} ${2 * Math.PI * r}`}
                    transform="rotate(-90)"
                  />
                )}

                <text className="map-name" y={r + 19}>
                  {place.name}
                </text>
                {place.level !== 0 && (
                  <text className="map-level" y={r + 34}>
                    floor {place.level}
                  </text>
                )}
              </g>
            )
          })}

          {/* People last, so nobody is drawn under a building. */}
          {places.map((place) => {
            const people = here.get(place.id) ?? []
            if (people.length === 0) return null
            const { x, y } = at(place)

            return (
              <g key={`folk:${place.id}`} transform={`translate(${x}, ${y})`}>
                {people.map((person, index) => {
                  // Fanned around the node rather than stacked, so six people in a hall read as
                  // six people.
                  const angle = (index / Math.max(people.length, 1)) * Math.PI * 2 - Math.PI / 2
                  const ring = 33 + Math.floor(index / 8) * 13
                  return (
                    <circle
                      key={person.id}
                      className={person.id === selectedId ? 'map-folk is-selected' : 'map-folk'}
                      cx={Math.cos(angle) * ring}
                      cy={Math.sin(angle) * ring}
                      r={6}
                      onClick={(e) => {
                        e.stopPropagation()
                        onSelect(person.id)
                      }}
                    >
                      <title>
                        {person.displayName} — {person.doing?.toLowerCase() ?? 'deciding'}
                      </title>
                    </circle>
                  )
                })}
              </g>
            )
          })}
        </svg>
      </div>
    </div>
  )
}
