import { useCallback, useEffect, useState } from 'react'
import { UNREACHABLE } from './ColonistPanel'

/**
 * The shelf: keeping a colony, and going back to one.
 *
 * M11 made a colony outlive the process — SQLite, a versioned document, the random stream saved
 * with it so a loaded colony lives the same life the saved one would have — and then gave it no
 * front door at all. Everything below has been sitting behind `/api/saves` since; this is the first
 * thing in the client that calls it.
 *
 * The autosave is listed with the rest rather than hidden, because it is the one that matters after
 * a crash and the one a player will actually want. It is written every colony day, so it is never
 * more than a colony day behind — but it is also the one that gets overwritten, which is why
 * keeping a colony under a name of your own is a separate act.
 */
export interface SavedColony {
  name: string
  savedAt: string
  seed: number
  day: number
  living: number
  everLived: number
  version: number
}

export function SavesPanel({ day, onLoaded }: { day: number; onLoaded: () => void }) {
  const [open, setOpen] = useState(false)
  const [saves, setSaves] = useState<SavedColony[] | null>(null)
  const [name, setName] = useState('')
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Loading a colony replaces the one on screen, and deleting one cannot be undone. Both ask first.
  const [confirming, setConfirming] = useState<{ what: 'load' | 'delete'; name: string } | null>(null)

  const refresh = useCallback(async () => {
    try {
      const response = await fetch('/api/saves')
      setSaves(response.ok ? ((await response.json()) as SavedColony[]) : [])
    } catch {
      setSaves([])
      setStatus(UNREACHABLE)
    }
  }, [])

  useEffect(() => {
    if (open) void refresh()
  }, [open, refresh])

  // Opening the panel is a cheap way to leave it open while the autosave moves underneath, so it
  // re-reads the shelf while it is showing rather than going stale in front of you.
  useEffect(() => {
    if (!open) return
    const timer = window.setInterval(() => void refresh(), 5000)
    return () => window.clearInterval(timer)
  }, [open, refresh])

  const act = async (verb: 'save' | 'load' | 'delete', target: string) => {
    setBusy(true)
    setStatus(null)
    setConfirming(null)

    try {
      const url = `/api/saves/${encodeURIComponent(target)}${verb === 'load' ? '/load' : ''}`
      const response = await fetch(url, { method: verb === 'delete' ? 'DELETE' : 'POST' })

      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { error?: string } | null
        setStatus(body?.error ?? `The colony could not be ${verb}d.`)
        return
      }

      if (verb === 'save') {
        setName('')
        setStatus(`Kept as “${target}”.`)
      } else if (verb === 'load') {
        setOpen(false)
        onLoaded()
        return
      }

      await refresh()
    } catch {
      setStatus(UNREACHABLE)
    } finally {
      setBusy(false)
    }
  }

  const proposed = name.trim()
  const clash = saves?.some((s) => s.name === proposed) ?? false

  return (
    <div className="print-verb">
      <button
        className="back"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        title="Keep this colony, or go back to one you kept"
      >
        saves
      </button>

      {open && (
        <form
          className="verb print-pop saves-pop"
          onSubmit={(e) => {
            e.preventDefault()
            if (proposed) void act('save', proposed)
          }}
        >
          <label htmlFor="save-name">Keep this colony</label>
          <div className="verb-foot">
            <input
              id="save-name"
              value={name}
              maxLength={60}
              placeholder={`day ${day.toFixed(0)} — a name you will recognise`}
              onChange={(e) => setName(e.target.value)}
            />
            <button type="submit" disabled={busy || proposed.length === 0}>
              {clash ? 'replace' : 'keep'}
            </button>
          </div>

          <label>On the shelf</label>
          {saves === null ? (
            <p className="verb-status">Reading the shelf…</p>
          ) : saves.length === 0 ? (
            <p className="verb-status">Nothing kept yet. The autosave appears after the first day.</p>
          ) : (
            <ul className="saves-list">
              {saves.map((save) => (
                <li key={save.name} className={save.name === 'autosave' ? 'is-auto' : undefined}>
                  <div className="saves-what">
                    <b>{save.name}</b>
                    <small>
                      day {save.day.toFixed(0)} · {save.living} living of {save.everLived} · seed{' '}
                      {save.seed} · kept {when(save.savedAt)}
                    </small>
                  </div>

                  {confirming?.name === save.name ? (
                    <div className="saves-confirm">
                      <span>
                        {confirming.what === 'load'
                          ? 'Replace the colony on screen?'
                          : 'Delete for good?'}
                      </span>
                      <button type="button" disabled={busy} onClick={() => void act(confirming.what, save.name)}>
                        yes
                      </button>
                      <button type="button" onClick={() => setConfirming(null)}>
                        no
                      </button>
                    </div>
                  ) : (
                    <div className="saves-acts">
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => setConfirming({ what: 'load', name: save.name })}
                      >
                        load
                      </button>
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => setConfirming({ what: 'delete', name: save.name })}
                      >
                        delete
                      </button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}

          <span className="verb-status">
            {status ??
              'The colony saves itself as “autosave” every colony day. A name of your own is one that will not be written over.'}
          </span>
        </form>
      )}
    </div>
  )
}

/** Shelf time, in the terms a shelf wants: how long ago, not a timestamp. */
function when(iso: string) {
  const seconds = (Date.now() - new Date(iso).getTime()) / 1000
  if (!Number.isFinite(seconds)) return 'at some point'
  if (seconds < 90) return 'just now'
  if (seconds < 5400) return `${Math.round(seconds / 60)} min ago`
  if (seconds < 172800) return `${Math.round(seconds / 3600)} hr ago`
  return `${Math.round(seconds / 86400)} days ago`
}
