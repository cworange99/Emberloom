import type { Place } from '../types'

/**
 * What each place looks like inside. Art direction, like art.ts: the world model knows a place is a
 * shelter; this decides it has two single beds and a low bookcase. Named places get a layout of
 * their own, and anything added to the world later falls back by kind.
 *
 * Model names are paths within the kit — furniture from the Kenney Furniture Kit, open-air scenery
 * from the Mini Forest kit.
 */
/**
 * Things that belong together — M17, and the whole of what was wrong with these rooms.
 * <p>
 * Until now every floor prop was placed on its own, evenly spaced around a ring at the room's
 * centre and given a random quarter turn. So a table and its chairs landed on opposite sides of
 * the room facing nothing, and a rug lay beside the table it was meant to be under. The props were
 * right and the arrangement was nonsense: there was no way to *say* that a chair is tucked under a
 * table, because the schema had no word for it.
 * </p>
 */
export interface Cluster {
  /** What the rest is arranged around. Placed at the cluster's spot. */
  centre: string
  /** Laid under the centre and never turned. Drawn first, so it is underneath. */
  under?: string
  /** Ringed around the centre, each one turned to face it. Chairs at a table. */
  around?: string[]
  /** Against the centre's side and touching it, alternating left and right. A lamp by a sofa. */
  beside?: string[]
}

export interface RoomSpec {
  /** In floor tiles. A tile is one unit, and a colonist is 0.67 tall, so a 7×6 room is a big one. */
  width: number
  depth: number
  /** Stood against the back wall, one per tile, never across the doorway. */
  wall: string[]
  /**
   * Out on the floor, kept clear of the middle so people stay visible. A bare name is a thing
   * standing on its own; a `Cluster` is a thing with its furniture around it.
   */
  open: (string | Cluster)[]
  /** Open-air: no walls, a grass floor, and scenery scattered rather than furniture lined up. */
  outdoor?: boolean
  /** Left to the forest: potted plants where nobody has cleared. */
  ruined?: boolean
}

const furniture = (...names: string[]) => names.map((name) => `room/${name}`)
const forest = (...names: string[]) => names.map((name) => `forest/${name}`)
const f = (name: string) => `room/${name}`

/** A table with its seats round it, and optionally a rug under the lot. */
const around = (centre: string, seats: string[], under?: string): Cluster => ({
  centre: f(centre),
  around: seats.map(f),
  under: under ? f(under) : undefined,
})

/** A thing with something standing next to it — a lamp by a sofa, a chair at a desk. */
const beside = (centre: string, ...others: string[]): Cluster => ({ centre: f(centre), beside: others.map(f) })

/** A thing on a rug, with nothing round it. */
const on = (centre: string, rug: string): Cluster => ({ centre: f(centre), under: f(rug) })

/**
 * A multi-storey building shows only the floor that matters. The Ninth Floor is somewhere you go,
 * not a level you scroll through, so there is never a reason to draw the eight below it.
 */
const BY_ID: Record<string, RoomSpec> = {
  'shelter-east': {
    width: 7, depth: 6,
    wall: furniture('bedSingle', 'bedSingle', 'cabinetBedDrawerTable', 'bookcaseOpenLow'),
    open: [around('tableCoffee', ['chairCushion'], 'rugRectangle'), beside('loungeChair', 'lampRoundFloor')],
  },
  'shelter-under': {
    width: 6, depth: 6,
    wall: furniture('bedBunk', 'bedSingle', 'cabinetBed'),
    open: [around('tableCoffee', ['chair'], 'rugRound'), 'room/lampSquareFloor'],
  },
  loom: {
    width: 8, depth: 6,
    wall: furniture('bookcaseClosedWide', 'cabinetTelevisionDoors', 'desk'),
    open: [beside('desk', 'chairDesk'), on('loungeSofa', 'rugSquare'), 'room/lampRoundFloor'],
  },
  'market-hall': {
    width: 8, depth: 7,
    wall: furniture('kitchenCabinet', 'kitchenFridge', 'bookcaseClosed'),
    // The cloth-covered table: the cross-legged one reads as a flat X from above.
    open: [around('tableCloth', ['chair', 'chair', 'stoolBar']), 'room/pottedPlant'],
  },
  'tower-lobby': {
    width: 8, depth: 6,
    wall: furniture('bookcaseOpen', 'cabinetTelevision', 'stairs'),
    open: [around('tableCoffee', ['loungeSofa', 'loungeChair'], 'rugRounded'), 'room/pottedPlant'],
  },
  'ninth-floor': {
    width: 7, depth: 6, ruined: true,
    wall: furniture('bookcaseOpen', 'cardboardBoxClosed', 'cardboardBoxOpen'),
    open: furniture('books', 'cardboardBoxOpen', 'pottedPlant'),
  },
  substation: {
    width: 6, depth: 6, ruined: true,
    wall: furniture('cabinetTelevisionDoors', 'cardboardBoxClosed', 'bookcaseClosed'),
    open: furniture('cardboardBoxOpen', 'lampSquareFloor', 'books'),
  },
  'east-stair': {
    width: 5, depth: 5, ruined: true,
    wall: furniture('stairs', 'cardboardBoxClosed'),
    open: furniture('cardboardBoxOpen', 'books'),
  },

  // Open-air places are rooms too, as far as this view is concerned. Clicking the orchard has to
  // land somewhere, and "outdoors" is an honest answer rather than a dead link.
  plaza: {
    width: 9, depth: 8, outdoor: true, wall: [],
    open: forest('rocks-low', 'stones', 'plant', 'tree'),
  },
  orchard: {
    width: 8, depth: 8, outdoor: true, wall: [],
    open: forest('tree', 'tree-high', 'tree', 'plant'),
  },
  'green-roof': {
    width: 7, depth: 7, outdoor: true, wall: [],
    open: forest('plant', 'plant', 'tree', 'stones'),
  },
  'river-edge': {
    width: 8, depth: 7, outdoor: true, wall: [],
    open: forest('rocks-low', 'tree-high', 'stones', 'plant'),
  },
}

const BY_KIND: Record<string, RoomSpec> = {
  Shelter: BY_ID['shelter-east'],
  Loom: BY_ID.loom,
  Ruin: BY_ID.substation,
  Stairwell: BY_ID['east-stair'],
  Clearing: BY_ID.orchard,
  Plaza: BY_ID.plaza,

  // What the colony built for itself (M10). A hall is tables and benches and nothing to work at;
  // a granary is what is kept; a dwelling is beds the colony made rather than found.
  Hall: {
    width: 9, depth: 7,
    wall: furniture('kitchenBar', 'kitchenBarEnd', 'bookcaseOpenLow'),
    open: [
      around('tableCloth', ['bench', 'bench', 'chairCushion']),
      around('tableRound', ['stoolBar', 'stoolBar', 'stoolBar']),
      'room/pottedPlant',
    ],
  },
  Granary: {
    width: 7, depth: 6,
    wall: furniture('kitchenCabinet', 'kitchenCabinetDrawer', 'bookcaseClosedWide'),
    open: [
      beside('table', 'cardboardBoxClosed', 'cardboardBoxOpen'),
      beside('cardboardBoxClosed', 'cardboardBoxClosed'),
      'room/trashcan',
    ],
  },
  Dwelling: {
    width: 7, depth: 6,
    wall: furniture('bedSingle', 'bedSingle', 'bedBunk', 'cabinetBedDrawer'),
    open: [beside('sideTableDrawers', 'lampSquareTable'), around('tableCoffee', ['chairRounded'], 'rugRectangle')],
  },
  // Marked ground, before anything stands on it. Outdoors, and wall must be present even when it
  // is empty — buildRoom walks it unconditionally.
  Site: {
    width: 7, depth: 7, outdoor: true, wall: [],
    open: forest('stones', 'rocks-low', 'plant'),
  },
}

export function roomFor(place: Place): RoomSpec {
  return BY_ID[place.id] ?? BY_KIND[place.kind] ?? BY_ID.substation
}

/** The pieces every room is built from. */
export const STRUCTURE = {
  floor: 'room/floorFull',
  wall: 'room/wall',
  window: 'room/wallWindow',
  doorway: 'room/wallDoorway',
  plant: 'room/pottedPlant',
}
