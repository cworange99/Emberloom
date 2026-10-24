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
  /** What a site will become once raised. Null for everywhere the makers left behind. */
  becomes: string | null
  neighbours: string[]
}

export interface Colonist {
  id: string
  designation: string
  name: string | null
  displayName: string
  /** What they are known for, if they have done something worth it. Most people have none. */
  epithet: string | null
  lineage: string
  isAlive: boolean
  /** How long this body lasts, in colony days. */
  lifespanDays: number
  /** In the last stretch of their life, and slowing. */
  isElder: boolean
  /** Empty for a printed mind; the two who made them, for a born one. */
  parents: PersonRef[]
  ageDays: number
  mood: string
  /** M12: how the life is going, in [0,1], and the same thing as a word. */
  wellbeing: number
  wellbeingWord: string
  /** M13: what they have earned and not spent, and how many evenings it bought. */
  tokens: number
  eveningsBought: number
  /** M15: how many days of this life despair has already taken. */
  daysLostToDespair: number
  placeId: string
  doing: string | null
  doingTarget: string | null
  progress: number
  isTravelling: boolean
  thought: string | null
  memoryCount: number
  wantsReflection: boolean
  modelCalls: number
  isKindled: boolean
  nameCandidates: string[]
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
  /** Set when this line is someone speaking. The views show it over their head. */
  quote: string | null
}

export interface Clock {
  speed: string
  speedLabel: string
  autoSlowOnEvent: boolean
  throttledByGovernor: boolean
  /** True while a fork is holding the speed at 1×. It comes back when the colony decides. */
  slowedForEvent: boolean
  colonyDaysPerRealMinute: number
  callsPerColonyDay: number | null
  sustainedCallsPerMinute: number
  /** True once the figure comes from calls actually served rather than the benchmark. */
  throughputMeasured: boolean
}

export interface Governor {
  queueDepth: number
  tokensPerMinute: number
  tokensInLastMinute: number
  pressure: number
  dropped: number
  served: number
  fallbacks: number
  currentCall: string | null
  throttling: boolean
}

export interface Intent {
  verb: string
  placeId: string | null
  colonistId: string | null
  strength: number
  /** strength × what the mode multiplies it by; 0 in Simulation. */
  reach: number
  expiresOnDay: number
  reads: string
}

export interface ColonyState {
  tick: number
  day: number
  timeOfDay: number
  isNight: boolean
  loomCharge: number
  chargeToPrint: number
  canFeedAnother: boolean
  /** What the player's light and warmth cost, in charge per colony-day. */
  upkeepPerDay: number
  /** M14: what the stores hold, what the work put in, what the living take, and the net. */
  storeCapacity: number
  producedPerDay: number
  drawPerDay: number
  chargeFlowPerDay: number
  storesOverflowing: boolean
  /** M15: what fraction of the day's draw went unpaid, and how many beds the colony has. */
  shortfall: number
  beds: number
  /** M16: how far the player reaches into this colony, and what they have asked of it. */
  mode: 'Simulation' | 'Hybrid' | 'Directed'
  intents: Intent[]
  artifactsRecovered: number
  population: number
  actionsCompleted: number
  clock: Clock
  governor: Governor | null
  colonists: Colonist[]
  /** The parts of each place that change as the colony runs. The map itself arrives once, in World. */
  places: PlaceState[]
  /** The fork the colony is deciding, or the one it has just decided. */
  fork: ForkState | null
  /** The story the colony tells about its makers, once it has one. */
  myth: MythState | null
  /** The colony's own words, newest last. */
  lexicon: CoinedWord[]
}

export interface MythState {
  story: string
  /** How many times it has been told. */
  version: number
  toldOnDay: number
  /** Beliefs held in the colony that go against it. */
  contradicted: number
  /** True when this telling was written by hand in the story file, rather than told by a mind. */
  byHand: boolean
}

export interface CoinedWord {
  term: string
  meaning: string
  coinedBy: string
  coinedById: string
  day: number
}

/** One seat on a council. Stance, line and conviction arrive once that member has spoken. */
export interface CouncilSeat {
  id: string
  name: string
  stance: 'for' | 'against' | 'abstain' | null
  conviction: number | null
  line: string | null
  /** True when no mind answered and the colonist was counted by where their weights stand. */
  wasFallback: boolean
}

export interface ForkState {
  id: string
  kind: 'Print' | 'Ascend'
  question: string
  proposal: string
  openedOnDay: number
  isOpen: boolean
  carried: boolean | null
  /** What was decided, once it has been: "yes", "no", or a core's fate. */
  outcome: string | null
  council: CouncilSeat[]
}

export interface PlaceState {
  id: string
  isBlocked: boolean
  isExplored: boolean
  clearingProgress: number
  salvageRemaining: number
  /** Light the player keeps burning here on the Loom's charge. */
  isLit: boolean
  /** Warmth the player keeps running here — shelters only. */
  isWarmed: boolean
  /** What this place is now: a site becomes the building it was raised as. */
  kind: string
  /** True for ground the colony has not agreed to build on yet. */
  isSite: boolean
  /** 0 until the colony approves the ground, then how far up the building is. */
  buildProgress: number
}

/** One thing of the makers' the player could show. shownTo is set once it has been shown. */
export interface Revelation {
  id: string
  title: string
  unlocks: string | null
  shownTo: string | null
}

export interface World {
  places: Place[]
  driveNames: string[]
  speeds: string[]
}

export interface PersonRef {
  id: string
  name: string
}

/** One episodic row — the far end of a chain — with its place and the others in it named. */
export interface MemoryRow {
  id: string
  day: number
  text: string
  placeId: string
  placeName: string
  others: PersonRef[]
  repeats: number
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
  partnerName: string | null
  score: number
  chosen: boolean
}

export interface TraceRow {
  id: string
  callType: string
  day: number
  model: string
  lane: string
  promptTokens: number
  completionTokens: number
  seconds: number
  produced: string
  wasFallback: boolean
  /** Ids of the beliefs and consolidated memories this call wrote. */
  producedIds: string[]
  /** Whether the prompt and response are still held — only the most recent calls keep them. */
  hasText: boolean
}

/** What one call sent and got back. Fetched when a trace row is opened. */
export interface CallText {
  id: string
  callType: string
  day: number
  model: string
  system: string | null
  prompt: string | null
  response: string | null
}

export interface CallTypeSummary {
  callType: string
  calls: number
  tokens: number
  fallbacks: number
  seconds: number
}

export interface ConsolidatedRow {
  id: string
  day: number
  text: string
  valence: number
  salience: number
  recallCount: number
  sources: MemoryRow[]
  formedByCallId: string | null
}

export interface BeliefRow {
  id: string
  claim: string
  formedOnDay: number
  confidence: number
  recallCount: number
  contradictsColonyMyth: boolean
  sources: MemoryRow[]
  formedByCallId: string | null
  /** How many later reflections arrived at this claim again. */
  reinforcements: number
  lastReinforcedByCallId: string | null
}

export interface ColonistDetail {
  colonist: Colonist
  personality: string
  seed: string
  trace: TraceRow[]
  consolidated: ConsolidatedRow[]
  beliefs: BeliefRow[]
  recentMemories: MemoryRow[]
  relationships: RelationshipRow[]
  considering: ScoredOption[]
  traceSummary: CallTypeSummary[]
  /** Every call this mind has made. The trace rows themselves are capped; this is not. */
  traceTotal: number
}
