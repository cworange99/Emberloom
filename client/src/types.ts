export interface Place {
  id: string
  name: string
  kind: string
  x: number
  y: number
  level: number
  danger: number
  light: number
  isExplored: boolean
  isBlocked: boolean
  clearingProgress: number
  salvageRemaining: number
  neighbours: string[]
}

export interface Colonist {
  id: string
  designation: string
  name: string | null
  displayName: string
  lineage: string
  ageDays: number
  mood: string
  placeId: string
  doing: string | null
  doingTarget: string | null
  progress: number
  isTravelling: boolean
  thought: string | null
  memoryCount: number
  wantsReflection: boolean
  modelCalls: number
  drives: Record<string, number>
  weights: Record<string, number>
  dominantDrive: string
}

export type EventLayer = 'body' | 'mind' | 'world' | 'player'

export interface ColonyEvent {
  tick: number
  day: number
  layer: EventLayer
  text: string
  colonistId: string | null
  placeId: string | null
  notable: boolean
}

export interface Clock {
  speed: string
  speedLabel: string
  autoSlowOnEvent: boolean
  throttledByGovernor: boolean
  colonyDaysPerRealMinute: number
  callsPerColonyDay: number
  sustainedCallsPerMinute: number
}

export interface ColonyState {
  tick: number
  day: number
  timeOfDay: number
  isNight: boolean
  loomCharge: number
  artifactsRecovered: number
  population: number
  actionsCompleted: number
  clock: Clock
  colonists: Colonist[]
}

export interface World {
  places: Place[]
  driveNames: string[]
  speeds: string[]
}

export interface MemoryRow {
  id: string
  day: number
  text: string
  placeId: string
  valence: number
  intensity: number
  salience: number
}

export interface RelationshipRow {
  towardId: string
  towardName: string
  affinity: number
  trust: number
  respect: number
  sharedMemories: number
  stage: string
  narrative: string | null
}

export interface ScoredOption {
  action: string
  placeName: string
  partnerId: string | null
  score: number
  chosen: boolean
}

export interface ColonistDetail {
  colonist: Colonist
  personality: string
  recentMemories: MemoryRow[]
  relationships: RelationshipRow[]
  considering: ScoredOption[]
}
