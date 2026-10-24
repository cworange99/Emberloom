import { useEffect, useState } from 'react'
import type { ColonyState, Intent, Place, PlaceState } from './types'
import { UNREACHABLE } from './ColonistPanel'

/** ProvisionRules on the server, which is what actually refuses what the Loom cannot afford. */
const PER_DAY = 0.02
const DOOR_COST = 15

/**
 * Provide and Withhold, for the room that is open: light for a dark place, warmth for a shelter,
 * and a way into a place that will not open. Everything runs on the Loom's charge, which is the
 * same charge a print needs.
 */
/** What can be asked for at a place, by what the place is. Matches the server's ActionCatalog. */
const ASKABLE: Record<string, string[]> = {
  Loom: ['Tend'],
  Clearing: ['Forage'],
  Ruin: ['Salvage'],
  Stairwell: ['Clear'],
  Site: ['Build'],
  Shelter: ['Rest'],
  Dwelling: ['Rest'],
  Hall: ['Gather'],
  Plaza: ['Talk'],
}

export function PlaceVerbs({
  place,
  state,
  charge,
  mode,
  intents,
}: {
  place: Place
  state: PlaceState | undefined
  charge: number
  mode: ColonyState['mode']
  intents: Intent[]
}) {
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
      setStatus(UNREACHABLE)
    } finally {
      setBusy(false)
    }
  }

  const ask = async (verb: string, standing: boolean) => {
    setBusy(true)
    try {
      const response = await fetch(`/api/verbs/${standing ? 'unask' : 'ask'}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ verb, placeId: place.id, strength: 1 }),
      })
      const body = (await response.json().catch(() => null)) as { error?: string } | null
      setStatus(response.ok ? null : (body?.error ?? 'The colony would not hear that.'))
    } catch {
      setStatus(UNREACHABLE)
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
      {/* The same number the header shows, named the same way. These verbs spend it, and a
          player who reads one figure here and another up there is reading two resources. */}
      <span className="head" title="The colony's one stock, shown in the header too: what they eat, what lights the dark places, and what a new mind is made of.">
        The Loom · {Math.floor(charge)}
      </span>

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

      {/* M16: asking is not ordering. In simulation nothing is offered at all. */}
      {mode !== 'Simulation' &&
        (ASKABLE[state?.kind ?? place.kind] ?? []).map((verb) => {
          const standing = intents.find((i) => i.verb === verb && i.placeId === place.id)

          return standing ? (
            <button key={verb} className="is-on" disabled={busy} onClick={() => void ask(verb, true)}>
              stop asking for {verb.toLowerCase()} here
            </button>
          ) : (
            <button key={verb} disabled={busy} onClick={() => void ask(verb, false)}>
              ask for {verb.toLowerCase()} here
            </button>
          )
        })}

      {status && <span className="verb-status">{status}</span>}
    </div>
  )
}
