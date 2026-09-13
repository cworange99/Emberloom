import { useState } from 'react'
import { UNREACHABLE } from './ColonistPanel'

/** Colony.PrintCost on the server — which is what actually refuses a print it cannot afford. */
const PRINT_COST = 25
const MAX_SEED = 400

/**
 * Print, the first of the five verbs: spend the Loom's charge on a new mind, from a seed you write
 * or one rolled. The mind that wakes acts at once and is written by Kindling from the seed; the
 * inspector opens on it, so you can watch that happen.
 */
export function PrintVerb({
  charge,
  blocked,
  onPrinted,
}: {
  charge: number
  /** Why printing is not possible right now, when the colony is deciding exactly that. */
  blocked: string | null
  onPrinted: (id: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [seed, setSeed] = useState('')
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const why = blocked ?? (charge < PRINT_COST ? `The Loom needs ${PRINT_COST} charge to print.` : null)

  const roll = async () => {
    try {
      const response = await fetch('/api/verbs/seed')
      if (response.ok) setSeed(((await response.json()) as { seed: string }).seed)
    } catch {
      // Rolling is a convenience; an empty seed is rolled on the server anyway.
    }
  }

  const print = async () => {
    setBusy(true)
    setStatus(null)

    try {
      const response = await fetch('/api/verbs/print', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ seed: seed.trim() || null }),
      })
      const body = (await response.json().catch(() => null)) as { colonistId?: string; error?: string } | null

      if (response.ok && body?.colonistId) {
        setOpen(false)
        setSeed('')
        onPrinted(body.colonistId)
      } else {
        setStatus(body?.error ?? 'The Loom would not print.')
      }
    } catch {
      setStatus(UNREACHABLE)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="print-verb">
      <button
        className="back"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        title={why ?? "Spend the Loom's charge on a new mind"}
      >
        print · {PRINT_COST}
      </button>

      {open && (
        <form
          className="verb print-pop"
          onSubmit={(e) => {
            e.preventDefault()
            void print()
          }}
        >
          <label htmlFor="print-seed">What should the Loom be given?</label>
          <textarea
            id="print-seed"
            rows={4}
            maxLength={MAX_SEED}
            value={seed}
            placeholder="A few words about who this mind should be. Leave it empty and one is rolled. Nobody here will have asked for it."
            onChange={(e) => setSeed(e.target.value)}
          />
          <div className="verb-foot">
            <span className="verb-status">
              {status ?? why ?? `${Math.floor(charge)} charge · a print costs ${PRINT_COST}`}
            </span>
            <button type="button" onClick={() => void roll()}>
              roll one
            </button>
            <button type="submit" disabled={busy || why !== null}>
              print
            </button>
          </div>
        </form>
      )}
    </div>
  )
}
