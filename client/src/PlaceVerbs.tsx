import { useEffect, useState } from 'react'
import type { Place, PlaceState } from './types'

/** ProvisionRules on the server, which is what actually refuses what the Loom cannot afford. */
const PER_DAY = 1
const DOOR_COST = 15

/**
 * Provide and Withhold, for the room that is open: light for a dark place, warmth for a shelter,
 * and a way into a place that will not open. Everything runs on the Loom's charge, which is the
 * same charge a print needs.
 */
export function PlaceVerbs({ place, state, charge }: { place: Place; state: PlaceState | undefined; charge: number }) {
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => setStatus(null), [place.id])

  const act = async (verb: 'provide' | 'withhold', kind: 'Light' | 'Warmth' | 'Door') => {
    setBusy(true)
    try {
      const response = await fetch(`/api/verbs/${verb}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind, placeId: place.id }),
      })
      const body = (await response.json().catch(() => null)) as { error?: string } | null
      setStatus(response.ok ? null : (body?.error ?? 'The Loom would not do that.'))
    } catch {
      setStatus('The Loom would not do that.')
    } finally {
      setBusy(false)
    }
  }

  const blocked = state?.isBlocked ?? place.isBlocked
  const lit = state?.isLit ?? false
  const warmed = state?.isWarmed ?? false
  const canLight = !blocked && place.light < 1
  const canWarm = place.kind === 'Shelter'

  return (
    <div className="place-verbs" onPointerDown={(e) => e.stopPropagation()} onPointerUp={(e) => e.stopPropagation()}>
      <span className="head">The Loom's charge · {Math.floor(charge)}</span>

      {blocked && (
        <button disabled={busy} onClick={() => void act('provide', 'Door')}>
          open the way in · {DOOR_COST}
        </button>
      )}

      {canLight &&
        (lit ? (
          <button className="is-on" disabled={busy} onClick={() => void act('withhold', 'Light')}>
            cut the light
          </button>
        ) : (
          <button disabled={busy} onClick={() => void act('provide', 'Light')}>
            provide light · {PER_DAY}/day
          </button>
        ))}

      {canWarm &&
        (warmed ? (
          <button className="is-on" disabled={busy} onClick={() => void act('withhold', 'Warmth')}>
            cut the warmth
          </button>
        ) : (
          <button disabled={busy} onClick={() => void act('provide', 'Warmth')}>
            provide warmth · {PER_DAY}/day
          </button>
        ))}

      {!blocked && !canLight && !canWarm && <i>Nothing here the Loom can change.</i>}
      {status && <span className="verb-status">{status}</span>}
    </div>
  )
}
