import type { CoinedWord, MythState } from './types'

/**
 * What the colony shares: the story it tells about its makers, and the words it has coined. Both
 * go into every prompt, so this is also what every mind in the colony is thinking with.
 */
export function ColonyLore({
  myth,
  lexicon,
  onSelect,
}: {
  myth: MythState | null
  lexicon: CoinedWord[]
  onSelect: (id: string) => void
}) {
  if (!myth && lexicon.length === 0) return null

  return (
    <section className="lore">
      {myth && (
        <>
          <h3>
            What the colony tells itself{' '}
            <span className="tagline mind">
              told {myth.version === 1 ? 'once' : `${myth.version} times`} · d{myth.toldOnDay.toFixed(0)}
            </span>
          </h3>
          <p className="lore-myth">{myth.story}</p>
          {myth.contradicted > 0 && (
            <p className="row-meta">
              {myth.contradicted} belief{myth.contradicted === 1 ? '' : 's'} in the colony go against it —
              open a mind's Beliefs tab to find whose.
            </p>
          )}
        </>
      )}

      {lexicon.length > 0 && (
        <>
          <h3>Their words</h3>
          <dl className="lore-words">
            {lexicon.map((word) => (
              <div key={word.term}>
                <dt>{word.term}</dt>
                <dd>
                  {word.meaning}{' '}
                  <button className="jump" onClick={() => onSelect(word.coinedById)}>
                    — {word.coinedBy}
                  </button>
                </dd>
              </div>
            ))}
          </dl>
        </>
      )}
    </section>
  )
}
