import type { Place } from '../types'

/**
 * What each place looks like inside. Art direction, like art.ts: the world model knows a place is a
 * shelter; this decides it has two single beds and a low bookcase. Named places get a layout of
 * their own, and anything added to the world later falls back by kind.
 *
 * Model names are paths within the kit — furniture from the Kenney Furniture Kit, open-air scenery
 * from the Mini Forest kit.
 */
export interface RoomSpec {
  /** In floor tiles. A tile is one unit, and a colonist is 0.67 tall, so a 7×6 room is a big one. */
  width: number
  depth: number
  /** Stood against the back wall, one per tile, never across the doorway. */
  wall: string[]
  /** Out on the floor, kept clear of the middle so people stay visible. */
  open: string[]
  /** Open-air: no walls, a grass floor, and scenery scattered rather than furniture lined up. */
  outdoor?: boolean
  /** Left to the forest: potted plants where nobody has cleared. */
  ruined?: boolean
}

const furniture = (...names: string[]) => names.map((name) => `room/${name}`)
const forest = (...names: string[]) => names.map((name) => `forest/${name}`)

/**
 * A multi-storey building shows only the floor that matters. The Ninth Floor is somewhere you go,
 * not a level you scroll through, so there is never a reason to draw the eight below it.
 */
const BY_ID: Record<string, RoomSpec> = {
  'shelter-east': {
    width: 7, depth: 6,
    wall: furniture('bedSingle', 'bedSingle', 'cabinetBedDrawerTable', 'bookcaseOpenLow'),
    open: furniture('rugRectangle', 'lampRoundFloor', 'chairCushion'),
  },
  'shelter-under': {
    width: 6, depth: 6,
    wall: furniture('bedBunk', 'bedSingle', 'cabinetBed'),
    open: furniture('rugRound', 'tableCoffee', 'lampSquareFloor', 'chair'),
  },
  loom: {
    width: 8, depth: 6,
    wall: furniture('bookcaseClosedWide', 'cabinetTelevisionDoors', 'desk'),
    open: furniture('chairDesk', 'loungeSofa', 'rugSquare', 'lampRoundFloor'),
  },
  'market-hall': {
    width: 8, depth: 7,
    wall: furniture('kitchenCabinet', 'kitchenFridge', 'bookcaseClosed'),
    // The cloth-covered table: the cross-legged one reads as a flat X from above.
    open: furniture('tableCloth', 'chair', 'chair', 'stoolBar', 'pottedPlant'),
  },
  'tower-lobby': {
    width: 8, depth: 6,
    wall: furniture('bookcaseOpen', 'cabinetTelevision', 'stairs'),
    open: furniture('loungeSofa', 'loungeChair', 'tableCoffee', 'rugRounded', 'pottedPlant'),
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
    open: furniture('tableCloth', 'bench', 'bench', 'stoolBar', 'tableRound', 'chairCushion'),
  },
  Granary: {
    width: 7, depth: 6,
    wall: furniture('kitchenCabinet', 'kitchenCabinetDrawer', 'bookcaseClosedWide'),
    open: furniture('cardboardBoxClosed', 'cardboardBoxClosed', 'cardboardBoxOpen', 'table'),
  },
  Dwelling: {
    width: 7, depth: 6,
    wall: furniture('bedSingle', 'bedSingle', 'bedBunk', 'cabinetBedDrawer'),
    open: furniture('rugRectangle', 'lampSquareFloor', 'chairRounded'),
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
