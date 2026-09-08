import { useCallback, useEffect, useRef, useState } from 'react'
import type { ColonyEvent, ColonyState, World } from './types'

const MAX_EVENTS = 600

type Message =
  | { type: 'world'; payload: World }
  | { type: 'state'; payload: ColonyState }
  | { type: 'event'; payload: ColonyEvent }

/**
 * One socket, held open for the life of the page. The server pushes a full state snapshot every
 * simulated frame and appends events as they happen, so the client never models the colony
 * itself — it only draws what it was last told.
 */
export function useColony() {
  const [world, setWorld] = useState<World | null>(null)
  const [state, setState] = useState<ColonyState | null>(null)
  const [events, setEvents] = useState<ColonyEvent[]>([])
  const [connected, setConnected] = useState(false)

  const socketRef = useRef<WebSocket | null>(null)
  const retryRef = useRef<number | null>(null)

  useEffect(() => {
    let disposed = false

    const connect = () => {
      if (disposed) return

      const protocol = window.location.protocol === 'https:' ? 'wss' : 'ws'
      const socket = new WebSocket(`${protocol}://${window.location.host}/ws`)
      socketRef.current = socket

      socket.onopen = () => setConnected(true)

      socket.onmessage = (frame) => {
        const message = JSON.parse(frame.data as string) as Message

        if (message.type === 'world') {
          setWorld(message.payload)
        } else if (message.type === 'state') {
          setState(message.payload)
        } else {
          setEvents((previous) => {
            const next = [...previous, message.payload]
            return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next
          })
        }
      }

      socket.onclose = () => {
        setConnected(false)
        if (!disposed) {
          // The sim keeps running without us; reconnecting just resumes watching it.
          retryRef.current = window.setTimeout(connect, 1200)
        }
      }

      socket.onerror = () => socket.close()
    }

    connect()

    return () => {
      disposed = true
      if (retryRef.current) window.clearTimeout(retryRef.current)
      socketRef.current?.close()
    }
  }, [])

  const setSpeed = useCallback(async (speed: string) => {
    await fetch('/api/speed', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ speed }),
    })
  }, [])

  const setAutoSlow = useCallback(async (enabled: boolean) => {
    await fetch('/api/auto-slow', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
  }, [])

  const restart = useCallback(async (colonists: number) => {
    setEvents([])
    await fetch('/api/restart', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ colonists, seed: Math.floor(Math.random() * 100000) }),
    })
  }, [])

  return { world, state, events, connected, setSpeed, setAutoSlow, restart }
}
