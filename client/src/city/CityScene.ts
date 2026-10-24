import * as THREE from 'three'
import type { GLTF } from 'three/addons/loaders/GLTFLoader.js'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { Colonist, Place } from '../types'
import { KitLoader, hash, makeFigure, playClip, rand, type Figure } from './kit'
import {
  CITY_FOLK_SCALE,
  FOLK,
  INFILL,
  MAP_SCALE,
  PARASOLS,
  ROCKS,
  TREES,
  animationFor,
  artFor,
} from './art'

export type CityPick = { kind: 'colonist'; id: string } | { kind: 'place'; id: string }

/**
 * Walking pace in world units a second, and the longest a single walk may take. At 60x the body
 * can move a colonist on twice before a leisurely walk would finish, so a long walk speeds up
 * rather than falling behind the simulation it is drawing.
 */
const WALK_SPEED = 1.6
const MAX_WALK_SECONDS = 2.5

interface Walker extends Figure {
  placeId: string
  path: THREE.Vector3[]
  speed: number
  doing: string | null
  travelling: boolean
}

interface Bounds {
  minX: number
  maxX: number
  minZ: number
  maxZ: number
}

type Segment = [THREE.Vector3, THREE.Vector3]

/**
 * The reclaimed city, in low-poly 3D: the graphics spike made permanent.
 *
 * Imperative on purpose. React owns the page; this owns one canvas, builds the static city once
 * from the world model, and after that only moves colonists. Every place is laid out from the
 * position the world model gives it, so the city you see and the graph the body walks are the
 * same map — the spike kept its own copy with made-up coordinates, which is exactly what this
 * replaces.
 */
export class CityScene {
  private readonly renderer: THREE.WebGLRenderer
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 400)
  private readonly controls: OrbitControls
  private readonly hemi = new THREE.HemisphereLight(0xdfe8d8, 0x2a331f, 1.0)
  private readonly sun = new THREE.DirectionalLight(0xfff2d8, 2.2)
  private readonly kit = new KitLoader()
  private readonly clock = new THREE.Clock()
  private readonly raycaster = new THREE.Raycaster()
  private readonly observer: ResizeObserver
  private readonly fallbackMaterial = new THREE.MeshLambertMaterial({ color: 0xb9b4a6 })
  private readonly selectionRing: THREE.Mesh
  private readonly projected = new THREE.Vector3()

  private readonly centres = new Map<string, THREE.Vector3>()
  private readonly anchors = new Map<string, THREE.Vector3>()
  private readonly rubble = new Map<string, THREE.Object3D>()

  /** Buildings that are still going up, and the marked ground they are going up on. */
  private readonly rising = new Map<string, THREE.Object3D>()
  private readonly sites = new Map<string, THREE.Object3D>()
  private readonly walkers = new Map<string, Walker>()
  private readonly labels = new Map<string, HTMLElement>()
  private readonly pickables: THREE.Object3D[] = []

  private folk: Array<GLTF | null> = []
  private pending: Colonist[] | null = null
  private selectedId: string | null = null
  private frustum = 9
  private ready = false
  private disposed = false
  private frame = 0
  private active = true
  private running = false

  private readonly host: HTMLElement

  constructor(host: HTMLElement) {
    this.host = host
    this.renderer = new THREE.WebGLRenderer({ antialias: true })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    this.renderer.shadowMap.enabled = true
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap
    host.prepend(this.renderer.domElement)

    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enableDamping = true
    this.controls.maxPolarAngle = Math.PI / 2.15
    this.controls.minZoom = 0.5
    this.controls.maxZoom = 4
    this.controls.screenSpacePanning = true
    // Zoom toward the pointer rather than the middle of the view: on a map you scroll in on the
    // place you are looking at, and zooming on the centre pushed it off the edge.
    this.controls.zoomToCursor = true

    this.scene.add(this.hemi)
    this.sun.castShadow = true
    this.sun.shadow.mapSize.set(2048, 2048)
    this.scene.add(this.sun, this.sun.target)

    this.selectionRing = new THREE.Mesh(
      new THREE.RingGeometry(0.34, 0.46, 32),
      new THREE.MeshBasicMaterial({ color: 0xd9a441, side: THREE.DoubleSide }),
    )
    this.selectionRing.rotation.x = -Math.PI / 2
    this.selectionRing.visible = false
    this.scene.add(this.selectionRing)

    this.setNight(false)

    // The panel's size is not settled on the first frame, and a canvas sized from a stale layout
    // stays that size. Watching the container is the reliable fix (learned in the spike).
    this.observer = new ResizeObserver(() => this.resize())
    this.observer.observe(host)
    this.resize()
    this.loop()
  }

  /** Builds the static city. Returns whether the art kit was missing, so the view can say so. */
  async build(places: Place[]): Promise<{ missingKit: boolean }> {
    for (const place of places) {
      this.centres.set(place.id, new THREE.Vector3(place.x * MAP_SCALE, 0, place.y * MAP_SCALE))
    }

    const centres = [...this.centres.values()]
    const bounds: Bounds = {
      minX: Math.min(...centres.map((c) => c.x)) - 3,
      maxX: Math.max(...centres.map((c) => c.x)) + 3,
      minZ: Math.min(...centres.map((c) => c.z)) - 3,
      maxZ: Math.max(...centres.map((c) => c.z)) + 3,
    }

    // Every model at once, rather than one await per building.
    await Promise.all(
      [...INFILL, ...TREES, ...ROCKS, ...PARASOLS, ...FOLK, ...places.flatMap(modelsFor)].map((m) =>
        this.kit.load(m),
      ),
    )
    if (this.disposed) return { missingKit: this.kit.missing }

    this.buildGround()
    const streets = this.buildStreets(places)
    await this.buildPlaces(places)
    await this.buildInfill(bounds, streets)
    await this.buildForest(bounds, streets)
    this.folk = await Promise.all(FOLK.map((m) => this.kit.load(m)))
    if (this.disposed) return { missingKit: this.kit.missing }

    this.fit(bounds)
    this.ready = true

    if (this.pending) this.setColonists(this.pending, this.selectedId)
    return { missingKit: this.kit.missing }
  }

  // ---- live updates ------------------------------------------------------------------------

  setColonists(colonists: Colonist[], selectedId: string | null) {
    this.selectedId = selectedId
    this.pending = colonists
    if (!this.ready) return

    const present = new Set(colonists.map((c) => c.id))
    for (const [id, walker] of this.walkers) {
      if (!present.has(id)) {
        this.scene.remove(walker.root)
        this.pickables.splice(this.pickables.indexOf(walker.root), 1)
        this.walkers.delete(id)
      }
    }

    // Slots in a stable order, so nobody shuffles when someone else arrives.
    const byPlace = new Map<string, Colonist[]>()
    for (const colonist of colonists) {
      const group = byPlace.get(colonist.placeId) ?? []
      group.push(colonist)
      byPlace.set(colonist.placeId, group)
    }

    for (const [placeId, group] of byPlace) {
      const centre = this.centres.get(placeId)
      if (!centre) continue

      group.sort((a, b) => a.id.localeCompare(b.id))

      group.forEach((colonist, index) => {
        const target = slot(centre, index, group.length)
        let walker = this.walkers.get(colonist.id)

        if (!walker) {
          walker = this.spawn(colonist, target)
        } else if (walker.placeId !== placeId) {
          // Along the street, not across the blocks: the same L the street itself was drawn as.
          const corner = this.corner(walker.placeId, placeId)
          walker.path = corner ? [corner, target] : [target]
        } else if (end(walker).distanceTo(target) > 0.05) {
          walker.path = [target]
        }

        walker.speed = Math.max(WALK_SPEED, pathLength(walker) / MAX_WALK_SECONDS)
        walker.placeId = placeId
        walker.doing = colonist.doing
        walker.travelling = colonist.isTravelling
      })
    }
  }

  setNight(on: boolean) {
    // Real lighting rather than a tint laid over the top — most of the argument for 3D at all.
    this.sun.intensity = on ? 0.35 : 2.2
    this.sun.color.set(on ? 0x9fb4e8 : 0xfff2d8)
    this.hemi.intensity = on ? 0.35 : 1.0
    this.hemi.color.set(on ? 0x2a3350 : 0xdfe8d8)
    this.scene.background = new THREE.Color(on ? 0x0d1119 : 0x8fb8d8)
    this.scene.fog = new THREE.Fog(on ? 0x0d1119 : 0x8fb8d8, 60, 150)
  }

  /** Stops drawing while another view is showing, and picks up where it left off. */
  setActive(active: boolean) {
    this.active = active
    if (active && !this.running && !this.disposed) {
      this.clock.getDelta()
      this.loop()
    }
  }

  /** Rubble stands at a sealed place until the colony clears it. */
  setSealed(sealed: ReadonlyMap<string, boolean>) {
    for (const [placeId, rocks] of this.rubble) rocks.visible = sealed.get(placeId) ?? rocks.visible
  }

  /**
   * How far up each building is. A site shows marked ground until the colony approves it, then the
   * building grows out of the ground as the work goes in, and the ground patch goes when it stands.
   */
  setBuilt(progress: ReadonlyMap<string, number>) {
    for (const [placeId, building] of this.rising) {
      const done = progress.get(placeId) ?? 0
      building.visible = done > 0

      // Scaled from a fifth so the first day's work is visible rather than invisible, and never
      // past 1 so a finished building is exactly the size it was modelled at.
      building.scale.setScalar(done <= 0 ? 0.0001 : Math.min(1, 0.2 + done * 0.8))

      const ground = this.sites.get(placeId)
      if (ground) ground.visible = done < 1
    }
  }

  /** Registers an overlay element to be kept over a place (by id) or a colonist ('colonist:' + id). */
  setLabel(key: string, element: HTMLElement | null) {
    if (element) this.labels.set(key, element)
    else this.labels.delete(key)
  }

  pick(clientX: number, clientY: number): CityPick | null {
    const rect = this.renderer.domElement.getBoundingClientRect()
    const pointer = new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1,
    )
    this.raycaster.setFromCamera(pointer, this.camera)

    const picks = this.raycaster
      .intersectObjects(this.pickables, true)
      .map((hit) => hit.object.userData.pick as CityPick | undefined)
      .filter((p): p is CityPick => p !== undefined)

    // Someone standing in front of a building should win the click over the building.
    return picks.find((p) => p.kind === 'colonist') ?? picks[0] ?? null
  }

  dispose() {
    this.disposed = true
    cancelAnimationFrame(this.frame)
    this.observer.disconnect()
    this.controls.dispose()

    this.scene.traverse((object) => {
      const mesh = object as THREE.Mesh
      if (!mesh.isMesh) return
      mesh.geometry?.dispose()
      const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material]
      materials.forEach((m) => m?.dispose())
    })

    this.renderer.dispose()
    this.renderer.domElement.remove()
  }

  // ---- building the city -------------------------------------------------------------------

  private buildGround() {
    // The forest floor the city is standing in.
    const ground = new THREE.Mesh(
      new THREE.PlaneGeometry(200, 200),
      new THREE.MeshLambertMaterial({ color: 0x4a6b3f }),
    )
    ground.rotation.x = -Math.PI / 2
    ground.receiveShadow = true
    this.scene.add(ground)
  }

  /**
   * Streets along the real adjacencies, each drawn as an L so the city stays orthogonal rather
   * than cut through with diagonals. The corner is fixed per pair, so a colonist walking either
   * way takes the street that was drawn.
   */
  private buildStreets(places: Place[]): Segment[] {
    const material = new THREE.MeshLambertMaterial({ color: 0x5d5f56 })
    const segments: Segment[] = []
    const seen = new Set<string>()

    for (const place of places) {
      for (const neighbour of place.neighbours) {
        const key = [place.id, neighbour].sort().join('|')
        if (seen.has(key) || !this.centres.has(neighbour)) continue
        seen.add(key)

        const [a, b] = [place.id, neighbour].sort()
        const from = this.centres.get(a)!
        const to = this.centres.get(b)!
        const corner = this.corner(a, b)!

        for (const [p, q] of [
          [from, corner],
          [corner, to],
        ] as Segment[]) {
          const length = p.distanceTo(q) + 0.9
          const slab = new THREE.Mesh(new THREE.BoxGeometry(length, 0.04, 0.9), material)
          slab.position.set((p.x + q.x) / 2, 0.02, (p.z + q.z) / 2)
          slab.rotation.y = -Math.atan2(q.z - p.z, q.x - p.x)
          slab.receiveShadow = true
          this.scene.add(slab)
          segments.push([p, q])
        }
      }
    }

    return segments
  }

  private async buildPlaces(places: Place[]) {
    for (const place of places) {
      const centre = this.centres.get(place.id)!

      // A buildable site is drawn as the building it will become, hidden, standing on marked
      // ground. Nothing is loaded or added once the scene is built: raising it is a visibility
      // change and a scale, which is why growth costs this view nothing.
      const art = place.kind === 'Site' && place.becomes
        ? artFor({ ...place, kind: place.becomes })
        : artFor(place)

      const group = new THREE.Group()
      group.position.copy(centre)

      if (place.kind === 'Site') {
        const ground = new THREE.Mesh(
          new THREE.BoxGeometry(3.0, 0.04, 3.0),
          new THREE.MeshLambertMaterial({ color: 0x5a5347 }),
        )
        ground.position.y = 0.02
        ground.receiveShadow = true
        group.add(ground)
        this.sites.set(place.id, ground)
      }

      if (art.form === 'building') {
        const model = art.models[hash(place.id) % art.models.length]
        const building = await this.kit.instance(model, () =>
          this.fallbackBox(model.includes('skyscraper') ? 4.5 : 1.3),
        )
        building.rotation.y = (hash(`${place.id}:turn`) % 4) * (Math.PI / 2)
        group.add(building)

        if (place.kind === 'Site') {
          building.visible = false
          this.rising.set(place.id, building)
        }
      } else if (art.form === 'grove') {
        // A grove is trees rather than a building.
        for (let i = 0; i < 6; i++) {
          const tree = await this.kit.instance(TREES[0], () => this.fallbackTree())
          tree.scale.setScalar(0.85)
          tree.position.set((rand(i * 3 + place.x) - 0.5) * 2.6, 0, (rand(i * 7 + place.y) - 0.5) * 2.6)
          group.add(tree)
        }
      } else {
        // The plaza is open ground: a paved square with a few parasols still standing.
        const slab = new THREE.Mesh(
          new THREE.BoxGeometry(3.4, 0.06, 3.4),
          new THREE.MeshLambertMaterial({ color: 0x6c6f64 }),
        )
        slab.position.y = 0.03
        slab.receiveShadow = true
        group.add(slab)

        for (let i = 0; i < 4; i++) {
          const parasol = await this.kit.instance(PARASOLS[i % PARASOLS.length], () => new THREE.Group())
          parasol.position.set((rand(i * 4 + 1) - 0.5) * 2.6, 0.06, (rand(i * 8 + 3) - 0.5) * 2.6)
          parasol.rotation.y = rand(i * 6) * Math.PI * 2
          group.add(parasol)
        }
      }

      // Rubble at the foot of anything sealed, so you can see from across the city that it is shut.
      if (place.isBlocked) {
        const rocks = new THREE.Group()
        for (let i = 0; i < 4; i++) {
          const rock = await this.kit.instance(ROCKS[0], () => this.fallbackRock())
          rock.scale.setScalar(1.2)
          rock.position.set((rand(i * 5) - 0.5) * 1.8, 0, 1.1 + (rand(i * 9) - 0.5) * 0.9)
          rocks.add(rock)
        }
        this.rubble.set(place.id, rocks)
        group.add(rocks)
      }

      this.scene.add(group)
      this.markPickable(group, { kind: 'place', id: place.id })

      // The label rides just above whatever was drawn, however tall it turned out to be.
      const box = new THREE.Box3().setFromObject(group)
      this.anchors.set(place.id, new THREE.Vector3(centre.x, box.max.y + 0.2, centre.z))
    }
  }

  /**
   * Infill. Twelve named places alone read as monuments in a field; a city is mostly buildings
   * nobody has a reason to mention. Seeded, so the same world always draws the same city.
   */
  private async buildInfill(bounds: Bounds, streets: Segment[]) {
    const step = 2

    for (let x = Math.ceil(bounds.minX / step) * step; x <= bounds.maxX; x += step) {
      for (let z = Math.ceil(bounds.minZ / step) * step; z <= bounds.maxZ; z += step) {
        const n = rand(x * 37.3 + z * 91.7)
        if (n > 0.42) continue // leave gaps: the forest took some
        if (this.near(x, z, 1.7) || nearStreet(x, z, streets, 0.95)) continue

        const building = await this.kit.instance(INFILL[Math.floor(n * 1000) % INFILL.length], () =>
          this.fallbackBox(1.1),
        )
        building.position.set(x, 0, z)
        building.rotation.y = Math.floor(rand(x + z * 5) * 4) * (Math.PI / 2)
        this.scene.add(building)
      }
    }
  }

  /** The forest, coming back through the streets — thinned so the city is still the subject. */
  private async buildForest(bounds: Bounds, streets: Segment[]) {
    const width = bounds.maxX - bounds.minX + 6
    const depth = bounds.maxZ - bounds.minZ + 6

    for (let i = 0; i < 140; i++) {
      const x = bounds.minX - 3 + rand(i * 1.7) * width
      const z = bounds.minZ - 3 + rand(i * 2.3 + 5) * depth
      if (this.near(x, z, 1.6) || nearStreet(x, z, streets, 0.7)) continue

      const tree = await this.kit.instance(TREES[i % TREES.length], () => this.fallbackTree())
      tree.scale.setScalar(0.5 + rand(i) * 0.35)
      tree.position.set(x, 0, z)
      tree.rotation.y = rand(i * 11) * Math.PI * 2
      this.scene.add(tree)
    }
  }

  private fit(bounds: Bounds) {
    const centre = new THREE.Vector3((bounds.minX + bounds.maxX) / 2, 0, (bounds.minZ + bounds.maxZ) / 2)
    const extent = Math.max(bounds.maxX - bounds.minX, bounds.maxZ - bounds.minZ)

    this.frustum = extent * 0.36
    this.camera.position.copy(centre).add(new THREE.Vector3(1, 0.9, 1.15).normalize().multiplyScalar(40))
    this.controls.target.copy(centre)
    this.controls.update()

    const reach = extent * 0.75
    this.sun.position.copy(centre).add(new THREE.Vector3(18, 26, 12))
    this.sun.target.position.copy(centre)
    Object.assign(this.sun.shadow.camera, { left: -reach, right: reach, top: reach, bottom: -reach, near: 1, far: 90 })
    this.sun.shadow.camera.updateProjectionMatrix()

    this.resize()
  }

  // ---- colonists ---------------------------------------------------------------------------

  private spawn(colonist: Colonist, position: THREE.Vector3): Walker {
    // The same model the room view draws them with, chosen the same way — one person, both views.
    const gltf = this.folk.length > 0 ? this.folk[hash(colonist.id) % this.folk.length] : null
    const figure = makeFigure(gltf, CITY_FOLK_SCALE)
    figure.root.position.copy(position)

    this.scene.add(figure.root)
    this.markPickable(figure.root, { kind: 'colonist', id: colonist.id })

    const walker: Walker = {
      ...figure,
      placeId: colonist.placeId,
      path: [],
      speed: WALK_SPEED,
      doing: colonist.doing,
      travelling: colonist.isTravelling,
    }

    this.walkers.set(colonist.id, walker)
    return walker
  }

  private step(id: string, walker: Walker, dt: number) {
    const position = walker.root.position

    if (walker.path.length > 0) {
      const target = walker.path[0]
      const dx = target.x - position.x
      const dz = target.z - position.z
      const distance = Math.hypot(dx, dz)
      const move = walker.speed * dt

      if (distance <= move) {
        position.copy(target)
        walker.path.shift()
      } else {
        position.x += (dx / distance) * move
        position.z += (dz / distance) * move
      }

      if (distance > 1e-4) walker.root.rotation.y = Math.atan2(dx, dz)
      playClip(walker, 'walk', hash(id))
    } else {
      // Facing out from the place, which is toward the camera for the slots in front of it.
      const centre = this.centres.get(walker.placeId)
      if (centre) walker.root.rotation.y = Math.atan2(position.x - centre.x, position.z - centre.z)
      playClip(walker, walker.travelling ? 'idle' : animationFor(walker.doing), hash(id))
    }

    walker.mixer?.update(dt)

    if (id === this.selectedId) {
      this.selectionRing.visible = true
      this.selectionRing.position.set(position.x, 0.06, position.z)
    }
  }

  // ---- the frame ---------------------------------------------------------------------------

  private loop = () => {
    if (this.disposed || !this.active) {
      this.running = false
      return
    }
    this.running = true
    this.frame = requestAnimationFrame(this.loop)

    const dt = Math.min(this.clock.getDelta(), 0.1)
    this.selectionRing.visible = false
    for (const [id, walker] of this.walkers) this.step(id, walker, dt)

    this.controls.update()
    this.renderer.render(this.scene, this.camera)
    this.placeLabels()
  }

  /** Labels are HTML over the canvas — crisp at any zoom, and styled by the page — kept in step here. */
  private placeLabels() {
    const width = this.host.clientWidth
    const height = this.host.clientHeight

    for (const [key, element] of this.labels) {
      let anchor: THREE.Vector3 | null = null

      // Keys are 'colonist:<id>' for a name, 'speech:<id>' for a line being spoken, and a plain
      // place id for a place's label. Speech sits a little above the name, so both can show.
      const person = key.startsWith('colonist:') || key.startsWith('speech:')
      const lift = key.startsWith('speech:') ? 24 : 0

      if (person) {
        const walker = this.walkers.get(key.slice(key.indexOf(':') + 1))
        if (walker) {
          anchor = this.projected.copy(walker.root.position)
          anchor.y = 0.67 * CITY_FOLK_SCALE + 0.3
        }
      } else {
        const place = this.anchors.get(key)
        if (place) anchor = this.projected.copy(place)
      }

      if (!anchor) {
        element.style.visibility = 'hidden'
        continue
      }

      anchor.project(this.camera)
      const x = ((anchor.x + 1) / 2) * width
      const y = ((1 - anchor.y) / 2) * height - lift
      element.style.visibility = anchor.z > 1 ? 'hidden' : 'visible'
      element.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) translate(-50%, -100%)`
    }
  }

  private resize() {
    const width = this.host.clientWidth
    const height = this.host.clientHeight
    if (width === 0 || height === 0) return

    this.renderer.setSize(width, height)
    const aspect = width / height
    const f = this.frustum
    Object.assign(this.camera, { left: -f * aspect, right: f * aspect, top: f, bottom: -f })
    this.camera.updateProjectionMatrix()
  }

  // ---- helpers -----------------------------------------------------------------------------

  /** The fixed corner of the L between two places, the same whichever way it is walked. */
  private corner(aId: string, bId: string): THREE.Vector3 | null {
    const [first, second] = [aId, bId].sort()
    const a = this.centres.get(first)
    const b = this.centres.get(second)
    return a && b ? new THREE.Vector3(b.x, 0, a.z) : null
  }

  private near(x: number, z: number, radius: number) {
    for (const centre of this.centres.values()) {
      if (Math.hypot(centre.x - x, centre.z - z) < radius) return true
    }
    return false
  }

  private markPickable(root: THREE.Object3D, pick: CityPick) {
    root.traverse((node) => {
      node.userData.pick = pick
    })
    this.pickables.push(root)
  }

  private fallbackBox(height: number): THREE.Object3D {
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(0.9, height, 0.9), this.fallbackMaterial)
    mesh.position.y = height / 2
    return new THREE.Group().add(mesh)
  }

  private fallbackTree(): THREE.Object3D {
    const mesh = new THREE.Mesh(
      new THREE.ConeGeometry(0.35, 1.4, 7),
      new THREE.MeshLambertMaterial({ color: 0x2f6b3a }),
    )
    mesh.position.y = 0.7
    return new THREE.Group().add(mesh)
  }

  private fallbackRock(): THREE.Object3D {
    const mesh = new THREE.Mesh(new THREE.DodecahedronGeometry(0.2), this.fallbackMaterial)
    mesh.position.y = 0.12
    return new THREE.Group().add(mesh)
  }
}

function modelsFor(place: Place): string[] {
  const art = artFor(place)
  return art.form === 'building' ? art.models : []
}

/**
 * Where the nth of several colonists stands at a place: an arc in front of it, on the camera's
 * side, so nobody is hidden behind the building they are standing at. A second ring when crowded.
 */
function slot(centre: THREE.Vector3, index: number, count: number): THREE.Vector3 {
  const ring = Math.floor(index / 6)
  const inRing = Math.min(count - ring * 6, 6)
  const theta = Math.PI / 4 + ((index % 6) - (inRing - 1) / 2) * 0.62
  const radius = 0.95 + ring * 0.4
  return new THREE.Vector3(centre.x + Math.cos(theta) * radius, 0, centre.z + Math.sin(theta) * radius)
}

function end(walker: Walker): THREE.Vector3 {
  return walker.path.length > 0 ? walker.path[walker.path.length - 1] : walker.root.position
}

function pathLength(walker: Walker): number {
  let length = 0
  let from = walker.root.position
  for (const point of walker.path) {
    length += from.distanceTo(point)
    from = point
  }
  return length
}

function nearStreet(x: number, z: number, streets: Segment[], clearance: number): boolean {
  for (const [a, b] of streets) {
    const abx = b.x - a.x
    const abz = b.z - a.z
    const lengthSq = abx * abx + abz * abz
    const t = lengthSq === 0 ? 0 : Math.max(0, Math.min(1, ((x - a.x) * abx + (z - a.z) * abz) / lengthSq))
    if (Math.hypot(a.x + abx * t - x, a.z + abz * t - z) < clearance) return true
  }
  return false
}
