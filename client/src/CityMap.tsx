import { useMemo } from 'react'
import type { Colonist, Place } from './types'

interface Props {
  places: Place[]
  colonists: Colonist[]
  isNight: boolean
  selectedId: string | null
  onSelect: (id: string) => void
}

const PLACE_FILL: Record<string, string> = {
  Clearing: 'var(--body-layer)',
  Ruin: 'var(--muted)',
  Shelter: 'var(--ink-soft)',
  Stairwell: 'var(--flag)',
  Loom: 'var(--mind-layer)',
  Plaza: 'var(--ink-soft)',
}

/**
 * A top-down read of the city. Deliberately schematic rather than pretty — at M1 the job is to
 * make the simulation legible: who is where, what is still sealed, and what is left to find.
 */
export function CityMap({ places, colonists, isNight, selectedId, onSelect }: Props) {
  const view = useMemo(() => {
    if (places.length === 0) return { minX: 0, minY: 0, width: 100, height: 100 }

    const pad = 34
    const xs = places.map((p) => p.x)
    const ys = places.map((p) => p.y)
    const minX = Math.min(...xs) - pad
    const minY = Math.min(...ys) - pad
    return {
      minX,
      minY,
      width: Math.max(...xs) + pad - minX,
      height: Math.max(...ys) + pad - minY,
    }
  }, [places])

  const byId = useMemo(() => new Map(places.map((p) => [p.id, p])), [places])

  // Colonists standing in the same place get fanned out so they do not stack into one dot.
  const occupancy = new Map<string, number>()

  const edges = useMemo(() => {
    const seen = new Set<string>()
    const result: Array<{ a: Place; b: Place; blocked: boolean }> = []

    for (const place of places) {
      for (const neighbourId of place.neighbours) {
        const key = [place.id, neighbourId].sort().join('|')
        if (seen.has(key)) continue
        seen.add(key)

        const neighbour = byId.get(neighbourId)
        if (neighbour) {
          result.push({ a: place, b: neighbour, blocked: place.isBlocked || neighbour.isBlocked })
        }
      }
    }

    return result
  }, [places, byId])

  return (
    <div className="map">
      <svg
        viewBox={`${view.minX} ${view.minY} ${view.width} ${view.height}`}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label="The reclaimed city, with each colonist shown at the place they are standing"
      >
        {edges.map(({ a, b, blocked }) => (
          <line
            key={`${a.id}-${b.id}`}
            x1={a.x}
            y1={a.y}
            x2={b.x}
            y2={b.y}
            stroke={blocked ? 'var(--flag)' : 'var(--line)'}
            strokeWidth={blocked ? 0.9 : 0.7}
            strokeDasharray={blocked ? '3 2.5' : undefined}
            opacity={blocked ? 0.75 : 1}
          />
        ))}

        {places.map((place) => {
          const radius = place.kind === 'Loom' ? 9 : place.kind === 'Plaza' ? 8 : 6.5
          return (
            <g key={place.id}>
              {place.salvageRemaining > 0 && (
                <circle
                  cx={place.x}
                  cy={place.y}
                  r={radius + 3.5}
                  fill="none"
                  stroke="var(--mind-layer)"
                  strokeWidth={0.7}
                  strokeDasharray="1.5 1.5"
                  opacity={0.85}
                />
              )}
              <circle
                cx={place.x}
                cy={place.y}
                r={radius}
                fill={place.isBlocked ? 'none' : PLACE_FILL[place.kind] ?? 'var(--muted)'}
                fillOpacity={place.isExplored ? 0.22 : 0.08}
                stroke={place.isBlocked ? 'var(--flag)' : PLACE_FILL[place.kind] ?? 'var(--muted)'}
                strokeWidth={place.isBlocked ? 1.2 : 0.9}
                strokeDasharray={place.isBlocked ? '2 2' : undefined}
              />
              {place.isBlocked && place.clearingProgress > 0 && (
                <path
                  d={describeArc(place.x, place.y, radius + 2, place.clearingProgress)}
                  fill="none"
                  stroke="var(--flag)"
                  strokeWidth={1.6}
                  strokeLinecap="round"
                />
              )}
              <text
                x={place.x}
                y={place.y - radius - 3.5}
                textAnchor="middle"
                fontFamily="var(--mono)"
                fontSize={4.2}
                letterSpacing={0.25}
                fill="var(--muted)"
              >
                {place.name}
              </text>
            </g>
          )
        })}

        {colonists.map((colonist) => {
          const place = byId.get(colonist.placeId)
          if (!place) return null

          const index = occupancy.get(place.id) ?? 0
          occupancy.set(place.id, index + 1)

          const angle = (index / 6) * Math.PI * 2
          const spread = index === 0 ? 0 : 5.5 + Math.floor(index / 6) * 3.5
          const cx = place.x + Math.cos(angle) * spread
          const cy = place.y + Math.sin(angle) * spread
          const selected = colonist.id === selectedId

          return (
            <g
              key={colonist.id}
              onClick={() => onSelect(colonist.id)}
              style={{ cursor: 'pointer' }}
              role="button"
              aria-label={`${colonist.displayName}, ${colonist.doing ?? 'idle'}`}
            >
              <circle
                cx={cx}
                cy={cy}
                r={selected ? 3.4 : 2.4}
                fill={colonist.isTravelling ? 'var(--surface)' : 'var(--body-layer)'}
                stroke={selected ? 'var(--mind-layer)' : 'var(--body-layer)'}
                strokeWidth={selected ? 1.4 : 0.8}
              />
              {selected && (
                <text
                  x={cx}
                  y={cy + 7}
                  textAnchor="middle"
                  fontFamily="var(--mono)"
                  fontSize={3.8}
                  fill="var(--mind-layer)"
                  fontWeight={700}
                >
                  {colonist.displayName}
                </text>
              )}
            </g>
          )
        })}
      </svg>

      <div className="map-legend">
        <span>
          <i style={{ background: 'var(--body-layer)' }} />
          colonist
        </span>
        <span>
          <i style={{ background: 'var(--surface)', border: '1px solid var(--body-layer)' }} />
          travelling
        </span>
        <span>
          <i style={{ background: 'var(--mind-layer)' }} />
          salvage left
        </span>
        <span>
          <i style={{ background: 'var(--flag)' }} />
          sealed
        </span>
        <span>{isNight ? 'night' : 'day'}</span>
      </div>
    </div>
  )
}

/** Progress arc around a stairwell that is being cleared. */
function describeArc(cx: number, cy: number, r: number, progress: number) {
  const end = Math.min(progress, 0.9999) * Math.PI * 2 - Math.PI / 2
  const start = -Math.PI / 2
  const x1 = cx + r * Math.cos(start)
  const y1 = cy + r * Math.sin(start)
  const x2 = cx + r * Math.cos(end)
  const y2 = cy + r * Math.sin(end)
  const large = progress > 0.5 ? 1 : 0
  return `M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2}`
}
