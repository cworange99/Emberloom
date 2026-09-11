import type { Place } from '../types'

/**
 * How the city looks. The world model owns where each place is, what kind it is and what state it
 * is in; how it is drawn is a presentation decision, and lives here rather than in Core. Places
 * that need a particular look are named; everything else falls back by kind, so a place added to
 * the world model later still gets drawn as something sensible.
 *
 * All art is Kenney CC0, served by the API from assets/kit at /kit (see assets/README.md).
 */

/**
 * World units per map unit. The world model lays places out 20–40 map units apart; at 0.14 they
 * land two to five building-widths apart — a block with a street between. The graphics spike's
 * first attempt spaced them twice as far, which left the buildings as specks and forced the camera
 * so far back that nobody was visible at any size.
 */
export const MAP_SCALE = 0.14

/**
 * A Kenney character is 0.67 tall and a house 1.29. Drawn true to size, a colonist is easy to lose
 * among the infill at city distance; 1.5x was King Kong against a one-storey shelter. 1.2x reads.
 * The room view draws the same models at true size.
 */
export const CITY_FOLK_SCALE = 1.2

export const kitUrl = (model: string) => `/kit/${model}.glb`

export type PlaceArt = { form: 'building'; models: string[] } | { form: 'grove' } | { form: 'plaza' }

/** Places whose look carries meaning. The towers are tall on purpose: the ninth floor is a plot point. */
const BY_ID: Record<string, PlaceArt> = {
  'tower-lobby': { form: 'building', models: ['city/building-skyscraper-b'] },
  'east-stair': { form: 'building', models: ['city/building-skyscraper-d'] },
  'ninth-floor': { form: 'building', models: ['city/building-skyscraper-d'] },
}

const BY_KIND: Record<string, PlaceArt> = {
  Plaza: { form: 'plaza' },
  Clearing: { form: 'grove' },
  Shelter: { form: 'building', models: ['city/building-a', 'city/building-c'] },
  Loom: { form: 'building', models: ['city/building-e'] },
  Ruin: { form: 'building', models: ['city/building-h', 'city/building-k'] },
  Stairwell: { form: 'building', models: ['city/building-skyscraper-d'] },
}

export function artFor(place: Place): PlaceArt {
  return BY_ID[place.id] ?? BY_KIND[place.kind] ?? { form: 'building', models: ['city/building-b'] }
}

/** The buildings nobody has a reason to mention. Scenery: the simulation knows nothing about them. */
export const INFILL = [
  'building-b', 'building-d', 'building-f', 'building-g', 'building-i', 'building-j',
  'building-l', 'building-m', 'building-n', 'low-detail-building-wide-a', 'low-detail-building-wide-b',
].map((name) => `city/${name}`)

export const TREES = ['forest/tree', 'forest/tree-high', 'forest/plant']
export const ROCKS = ['forest/rocks-low', 'forest/stones']
export const PARASOLS = ['city/detail-parasol-a', 'city/detail-parasol-b']

export const FOLK = ['a', 'b', 'c', 'd', 'e', 'f'].flatMap((s) => [
  `folk/character-male-${s}`,
  `folk/character-female-${s}`,
])

/**
 * Which of the kit's 32 clips suits what a colonist is doing. Wander stays idle rather than walk:
 * walking on the spot reads as a bug, and actual walking is shown by actually walking.
 */
const ANIMATION_FOR: Record<string, string> = {
  Rest: 'sit',
  Talk: 'emote-yes',
  Teach: 'interact-right',
  Forage: 'pick-up',
  Salvage: 'interact-right',
  Clear: 'attack-melee-right',
  Build: 'interact-right',
  Tend: 'interact-left',
}

export const animationFor = (doing: string | null) => (doing && ANIMATION_FOR[doing]) || 'idle'

/**
 * A place's interior. Until M4.5's second step brings the room view into the app, this is the
 * standalone room page, opened in its own tab.
 */
export const roomUrl = (placeId: string) => `/spike/room.html?place=${encodeURIComponent(placeId)}`
