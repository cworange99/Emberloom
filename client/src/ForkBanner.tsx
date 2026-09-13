import type { ForkState } from './types'

/**
 * A fork, over the main view: the question, the council, and each member's say as it arrives. It
 * stays up for a little while once decided, so the outcome can be read before the colony moves on.
 */
export function ForkBanner({ fork, onSelect }: { fork: ForkState; onSelect: (id: string) => void }) {
  const spoken = fork.council.filter((seat) => seat.stance !== null).length
  const count = (stance: 'for' | 'against') => fork.council.filter((seat) => seat.stance === stance).length

  const kicker = fork.isOpen
    ? 'The colony is deciding'
    : `Decided — ${fork.outcome ?? (fork.carried ? 'yes' : 'no')}`

  return (
    <section className={fork.isOpen ? 'fork' : 'fork is-decided'} role="status" aria-live="polite">
      <span className="fork-kicker">{kicker}</span>
      <h3>{fork.question}</h3>
      <p className="fork-meta">
        {fork.isOpen ? `${spoken} of ${fork.council.length} have spoken` : fork.proposal} · {count('for')} for ·{' '}
        {count('against')} against
      </p>

      <ol className="fork-council">
        {fork.council.map((seat) => (
          <li key={seat.id} className={seat.stance ? `is-${seat.stance}` : 'is-waiting'}>
            <button className="jump" onClick={() => onSelect(seat.id)} title={`Open ${seat.name} in the inspector`}>
              {seat.name}
            </button>
            <span className="fork-stance">
              {seat.stance ?? 'thinking…'}
              {seat.wasFallback && ' · no mind answered'}
            </span>
            {seat.line && <q>{seat.line}</q>}
          </li>
        ))}
      </ol>
    </section>
  )
}
