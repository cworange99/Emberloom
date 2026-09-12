import * as THREE from 'three'
import type { GLTF } from 'three/addons/loaders/GLTFLoader.js'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import type { Colonist, Place } from '../types'
import { FOLK, animationFor } from './art'
import { KitLoader, hash, makeFigure, playClip, rand, withShadows, type Figure } from './kit'
import { STRUCTURE, roomFor, type RoomSpec } from './rooms'

/**
 * Floor tiles are 0.05 thick. Everything stands on top of them — a rug is 0.01, and one placed at
 * zero is buried in the floor with only the part hanging past the edge showing.
 */
const FLOOR_TOP = 0.05

/**
 * True size. A wall is 1.29, a chair 0.47, a colonist 0.67: against furniture a person is a person,
 * which is the whole reason this view exists. The city draws the same models at 1.2x.
 */
const FOLK_SCALE = 1.0

const WALK_SPEED = 1.1

interface Occupant extends Figure {
  path: THREE.Vector3[]
  doing: string | null
  travelling: boolean
  leaving: boolean
}

/**
 * One place's interior, cut away to its two far walls so nothing stands between the camera and
 * the people in it. The room spike made permanent.
 *
 * Laid out by its edges, because every model in the furniture kit has its origin at a corner — a
 * floor tile spans x 0..1 and z −1..0, a wall has its thickness on −z. Structural tiles are placed
 * with that arithmetic written out; props go through place(), which recentres each on its own
 * footprint first so it rotates about its middle and its position means what it says.
 */
export class RoomScene {
  private readonly host: HTMLElement
  private readonly renderer: THREE.WebGLRenderer
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 120)
  private readonly controls: OrbitControls
  private readonly hemi = new THREE.HemisphereLight(0xe8eee0, 0x39402f, 1.1)
  private readonly sun = new THREE.DirectionalLight(0xfff4e2, 2.0)
  private readonly kit = new KitLoader()
  private readonly clock = new THREE.Clock()
  private readonly raycaster = new THREE.Raycaster()
  private readonly observer: ResizeObserver
  private readonly ring: THREE.Mesh
  private readonly projected = new THREE.Vector3()
  private readonly occupants = new Map<string, Occupant>()
  private readonly labels = new Map<string, HTMLElement>()

  private room = new THREE.Group()
  private folk: Array<GLTF | null> = []
  private placeId: string | null = null
  private spec: RoomSpec | null = null
  private floor = FLOOR_TOP
  private readonly door = new THREE.Vector3()
  private frustum = 4.6
  private pending: Colonist[] = []
  private selectedId: string | null = null
  private builds = 0
  private active = true
  private running = false
  private disposed = false
  private frame = 0

  constructor(host: HTMLElement) {
    this.host = host
    this.renderer = new THREE.WebGLRenderer({ antialias: true })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    this.renderer.shadowMap.enabled = true
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap
    host.prepend(this.renderer.domElement)

    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enableDamping = true
    this.controls.maxPolarAngle = Math.PI / 2.2
    this.controls.minZoom = 0.6
    this.controls.maxZoom = 3.5
    this.controls.zoomToCursor = true

    this.scene.add(this.hemi)
    this.sun.position.set(6, 11, 5)
    this.sun.castShadow = true
    this.sun.shadow.mapSize.set(2048, 2048)
    Object.assign(this.sun.shadow.camera, { left: -10, right: 10, top: 10, bottom: -10, near: 0.5, far: 40 })
    this.scene.add(this.sun)

    // The one you are reading in the inspector. On the floor, under their feet; their label turns
    // gold as well, so the marker never depends on a ring the character might be standing over.
    this.ring = new THREE.Mesh(
      new THREE.RingGeometry(0.3, 0.4, 32),
      new THREE.MeshBasicMaterial({ color: 0xd9a441, side: THREE.DoubleSide }),
    )
    this.ring.rotation.x = -Math.PI / 2
    this.ring.visible = false
    this.scene.add(this.ring, this.room)

    this.setNight(false)
    this.observer = new ResizeObserver(() => this.resize())
    this.observer.observe(host)
    this.resize()
    this.loop()
  }

  /**
   * Builds a place's interior. A different place starts empty, with whoever is there already
   * standing in it; the same place rebuilt (the walls toggled) keeps its people where they are.
   */
  async show(place: Place, walls: boolean): Promise<{ missingKit: boolean }> {
    const build = ++this.builds
    const spec = roomFor(place)

    await Promise.all(
      [...Object.values(STRUCTURE), ...spec.wall, ...spec.open, ...FOLK].map((m) => this.kit.load(m)),
    )
    // A later call won the race: someone clicked through to another room while this one loaded.
    if (this.disposed || build !== this.builds) return { missingKit: this.kit.missing }

    this.folk = FOLK.map((m) => this.kit.peek(m))
    const samePlace = this.placeId === place.id

    this.scene.remove(this.room)
    this.room = new THREE.Group()
    this.scene.add(this.room)

    if (!samePlace) {
      for (const occupant of this.occupants.values()) this.scene.remove(occupant.root)
      this.occupants.clear()
    }

    this.placeId = place.id
    this.spec = spec
    this.floor = spec.outdoor ? 0 : FLOOR_TOP
    this.buildRoom(spec, walls)
    if (!samePlace) this.fit(spec)

    this.setColonists(this.pending, this.selectedId, !samePlace)
    return { missingKit: this.kit.missing }
  }

  /**
   * Who is here. Someone arriving walks in through the door, someone leaving walks out of it — so
   * a room you are watching has comings and goings rather than people blinking in and out.
   */
  setColonists(colonists: Colonist[], selectedId: string | null, settle = false) {
    this.pending = colonists
    this.selectedId = selectedId
    if (!this.placeId || !this.spec) return

    const here = colonists
      .filter((c) => c.placeId === this.placeId)
      .sort((a, b) => a.id.localeCompare(b.id))
    const present = new Set(here.map((c) => c.id))

    for (const [id, occupant] of this.occupants) {
      if (!present.has(id) && !occupant.leaving) {
        occupant.leaving = true
        occupant.path = [this.door.clone()]
      }
    }

    here.forEach((colonist, index) => {
      const target = this.slot(index, here.length)
      let occupant = this.occupants.get(colonist.id)

      if (!occupant) {
        occupant = this.spawn(colonist, settle ? target : this.door)
        if (!settle) occupant.path = [target]
      } else if (occupant.leaving || end(occupant).distanceTo(target) > 0.05) {
        occupant.leaving = false
        occupant.path = [target]
      }

      occupant.doing = colonist.doing
      occupant.travelling = colonist.isTravelling
    })
  }

  setNight(on: boolean) {
    this.sun.intensity = on ? 0.35 : 2.0
    this.sun.color.set(on ? 0x9fb4e8 : 0xfff4e2)
    this.hemi.intensity = on ? 0.4 : 1.1
    this.scene.background = new THREE.Color(on ? 0x0e1218 : 0x232b1e)
  }

  /** Stops drawing while another view is showing, and picks up where it left off. */
  setActive(active: boolean) {
    this.active = active
    if (active && !this.running && !this.disposed) {
      this.clock.getDelta()
      this.loop()
    }
  }

  /** Registers an overlay element to be kept over a colonist, keyed 'colonist:' + id. */
  setLabel(key: string, element: HTMLElement | null) {
    if (element) this.labels.set(key, element)
    else this.labels.delete(key)
  }

  /** The colonist under the pointer, if any. Furniture is scenery, and is never picked. */
  pick(clientX: number, clientY: number): string | null {
    const rect = this.renderer.domElement.getBoundingClientRect()
    const pointer = new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1,
    )
    this.raycaster.setFromCamera(pointer, this.camera)

    const roots = [...this.occupants.values()].filter((o) => !o.leaving).map((o) => o.root)
    for (const hit of this.raycaster.intersectObjects(roots, true)) {
      const id = hit.object.userData.pick as string | undefined
      if (id) return id
    }
    return null
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

  // ---- building a room ---------------------------------------------------------------------

  private buildRoom(spec: RoomSpec, walls: boolean) {
    const { width: w, depth: d } = spec
    const x0 = -w / 2 // the floor's back-left corner
    const z0 = -d / 2
    const floor = this.floor
    const door = Math.floor(w / 2) // which back-wall tile is the doorway

    if (spec.outdoor) {
      const grass = new THREE.Mesh(
        new THREE.PlaneGeometry(w + 2, d + 2),
        new THREE.MeshLambertMaterial({ color: 0x4a6b3f }),
      )
      grass.rotation.x = -Math.PI / 2
      grass.receiveShadow = true
      this.room.add(grass)
    } else if (this.kit.peek(STRUCTURE.floor)) {
      // Tile (i, j) covers x0+i..x0+i+1 by z0+j..z0+j+1; its origin is the +z corner.
      for (let i = 0; i < w; i++) {
        for (let j = 0; j < d; j++) this.put(STRUCTURE.floor, x0 + i, 0, z0 + j + 1)
      }
    } else {
      // No kit: a plain floor, so the room is still somewhere to stand.
      const slab = new THREE.Mesh(
        new THREE.BoxGeometry(w, FLOOR_TOP, d),
        new THREE.MeshLambertMaterial({ color: 0xc9b596 }),
      )
      slab.position.y = FLOOR_TOP / 2
      slab.receiveShadow = true
      this.room.add(slab)
    }

    if (walls && !spec.outdoor) {
      // Only the two far walls: that is the cutaway. Both stand on the floor's edge with their
      // thickness outside it and their inner face toward the room.

      // Back wall, along z = z0. Unrotated, a wall's face is at local z = 0.
      for (let i = 0; i < w; i++) {
        const model =
          i === door ? STRUCTURE.doorway : rand(i * 3 + 1) > 0.72 ? STRUCTURE.window : STRUCTURE.wall
        this.put(model, x0 + i, 0, z0)
      }

      // Left wall, along x = x0. A quarter turn maps local x 0..1 onto z −1..0, so tile j is
      // anchored at its +z end.
      for (let j = 0; j < d; j++) {
        const model = rand(j * 7 + 2) > 0.72 ? STRUCTURE.window : STRUCTURE.wall
        this.put(model, x0, 0, z0 + j + 1, Math.PI / 2)
      }

      // With both walls flush to the floor, the only gap is the 0.05 square where their
      // thicknesses would meet. A sliver of plain wall fills it exactly; the kit's own corner
      // piece is 0.55 across, made for a different layout, and stood out as a second post.
      const post = this.put(STRUCTURE.wall, x0 - 0.05, 0, z0)
      if (post) post.scale.x = 0.05
    }

    // Against the back wall, one per tile, skipping the doorway so nothing blocks it.
    const slots = [...Array(w).keys()].filter((i) => i !== door)
    spec.wall.forEach((model, k) => {
      const slot = slots[Math.min(slots.length - 1, Math.round(((k + 0.5) * slots.length) / spec.wall.length - 0.5))]
      const prop = this.place(model, x0 + slot + 0.5, 0, { floor })
      // Back against the wall: its centre sits half its own depth out from the face.
      if (prop) prop.position.z = z0 + prop.userData.size.z / 2 + 0.03
    })

    // Keep a prop's whole footprint on the floor, whichever way round it ended up.
    const keepOnFloor = (prop: THREE.Object3D | null, margin = 0.1) => {
      if (!prop) return
      const half = Math.max(prop.userData.size.x, prop.userData.size.z) / 2 + margin
      prop.position.x = Math.min(x0 + w - half, Math.max(x0 + half, prop.position.x))
      prop.position.z = Math.min(z0 + d - half, Math.max(z0 + half, prop.position.z))
    }

    if (spec.outdoor) {
      // Outdoors there is no wall to line things up against, so scenery is scattered and repeated.
      // Four props on a lawn read as a diagram; twenty read as a place.
      for (let i = 0; i < 22; i++) {
        const angle = rand(i * 5 + 1) * Math.PI * 2
        const radius = 1.3 + rand(i * 9 + 2) * (Math.min(w, d) / 2 - 0.6)
        this.place(spec.open[i % spec.open.length], Math.cos(angle) * radius, Math.sin(angle) * radius, {
          ry: rand(i * 11) * Math.PI * 2,
          scale: 0.7 + rand(i * 3) * 0.5,
          floor,
        })
      }
    } else {
      spec.open.forEach((model, i) => {
        const angle = (i / Math.max(spec.open.length, 1)) * Math.PI * 2
        const prop = this.place(model, Math.cos(angle) * (w / 2.9), Math.sin(angle) * (d / 2.9) + 0.4, {
          ry: Math.floor(rand(i * 11) * 4) * (Math.PI / 2),
          floor,
        })
        keepOnFloor(prop)
      })
    }

    if (spec.ruined && !spec.outdoor) {
      for (let i = 0; i < 4; i++) {
        const plant = this.place(STRUCTURE.plant, x0 + 0.5 + rand(i) * (w - 1), z0 + 1 + rand(i * 3) * (d - 2), {
          ry: rand(i * 7) * 6,
          floor,
        })
        keepOnFloor(plant)
      }
    }

    // People come and go through the doorway — or, outdoors, from the far edge.
    this.door.set(spec.outdoor ? 0 : x0 + door + 0.5, floor, z0 + (spec.outdoor ? 0.2 : 0.35))
  }

  /** A structural tile at its corner-origin position. */
  private put(model: string, x: number, y: number, z: number, ry = 0): THREE.Object3D | null {
    const gltf = this.kit.peek(model)
    if (!gltf) return null

    const object = withShadows(gltf.scene.clone(true))
    object.position.set(x, y, z)
    object.rotation.y = ry
    this.room.add(object)
    return object
  }

  /**
   * A prop, centred on (cx, cz) and standing on the floor. Returns the wrapper with its footprint
   * in userData.size, so callers can keep it clear of walls and edges.
   */
  private place(
    model: string,
    cx: number,
    cz: number,
    { ry = 0, scale = 1, floor = FLOOR_TOP }: { ry?: number; scale?: number; floor?: number } = {},
  ): THREE.Object3D | null {
    const gltf = this.kit.peek(model)
    if (!gltf) return null

    const object = withShadows(gltf.scene.clone(true))
    object.updateMatrixWorld(true)
    const box = new THREE.Box3().setFromObject(object)
    const centre = box.getCenter(new THREE.Vector3())
    object.position.set(-centre.x, -box.min.y, -centre.z)

    const wrapper = new THREE.Group()
    wrapper.add(object)
    wrapper.position.set(cx, floor, cz)
    wrapper.rotation.y = ry
    wrapper.scale.setScalar(scale)
    wrapper.userData.size = box.getSize(new THREE.Vector3()).multiplyScalar(scale)
    this.room.add(wrapper)
    return wrapper
  }

  private fit(spec: RoomSpec) {
    this.frustum = Math.max(spec.width, spec.depth) * 0.58
    this.camera.position.set(9, 8, 9)
    this.camera.zoom = 1
    this.controls.target.set(0, 0.5, 0)
    this.controls.update()
    this.resize()
  }

  // ---- people ------------------------------------------------------------------------------

  private spawn(colonist: Colonist, at: THREE.Vector3): Occupant {
    // The same model the city draws them with, chosen the same way — one person, both views.
    const gltf = this.folk.length > 0 ? this.folk[hash(colonist.id) % this.folk.length] : null
    const figure = makeFigure(gltf, FOLK_SCALE)
    figure.root.position.set(at.x, this.floor, at.z)
    figure.root.traverse((node) => {
      node.userData.pick = colonist.id
    })
    this.scene.add(figure.root)

    const occupant: Occupant = {
      ...figure,
      path: [],
      doing: colonist.doing,
      travelling: colonist.isTravelling,
      leaving: false,
    }
    this.occupants.set(colonist.id, occupant)
    return occupant
  }

  private slot(index: number, count: number): THREE.Vector3 {
    const { width, depth } = this.spec!
    const angle = (index / Math.max(count, 1)) * Math.PI * 2 + 0.6
    return new THREE.Vector3(Math.cos(angle) * (width / 5), this.floor, Math.sin(angle) * (depth / 5) + 0.3)
  }

  /** Advances one occupant. Returns false once someone leaving has reached the door. */
  private step(id: string, occupant: Occupant, dt: number): boolean {
    const position = occupant.root.position

    if (occupant.path.length > 0) {
      const target = occupant.path[0]
      const dx = target.x - position.x
      const dz = target.z - position.z
      const distance = Math.hypot(dx, dz)
      const move = WALK_SPEED * dt

      if (distance <= move) {
        position.x = target.x
        position.z = target.z
        occupant.path.shift()
      } else {
        position.x += (dx / distance) * move
        position.z += (dz / distance) * move
      }

      if (distance > 1e-4) occupant.root.rotation.y = Math.atan2(dx, dz)
      playClip(occupant, 'walk', hash(id))
    } else if (occupant.leaving) {
      return false
    } else {
      // Turned three-quarters toward the camera, each a little differently, so a room of people
      // reads as people rather than a parade.
      occupant.root.rotation.y = Math.PI / 4 + (rand(hash(id)) - 0.5) * 0.9
      playClip(occupant, occupant.travelling ? 'idle' : animationFor(occupant.doing), hash(id))
    }

    occupant.mixer?.update(dt)

    if (id === this.selectedId && !occupant.leaving) {
      this.ring.visible = true
      this.ring.position.set(position.x, this.floor + 0.02, position.z)
    }

    return true
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
    this.ring.visible = false

    for (const [id, occupant] of this.occupants) {
      if (!this.step(id, occupant, dt)) {
        this.scene.remove(occupant.root)
        this.occupants.delete(id)
      }
    }

    this.controls.update()
    this.renderer.render(this.scene, this.camera)
    this.placeLabels()
  }

  private placeLabels() {
    const width = this.host.clientWidth
    const height = this.host.clientHeight

    for (const [key, element] of this.labels) {
      const occupant = this.occupants.get(key.slice('colonist:'.length))
      if (!occupant) {
        element.style.visibility = 'hidden'
        continue
      }

      const anchor = this.projected.copy(occupant.root.position)
      anchor.y = this.floor + 0.67 * FOLK_SCALE + 0.12
      anchor.project(this.camera)

      const x = ((anchor.x + 1) / 2) * width
      const y = ((1 - anchor.y) / 2) * height
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
}

function end(occupant: Occupant): THREE.Vector3 {
  return occupant.path.length > 0 ? occupant.path[occupant.path.length - 1] : occupant.root.position
}
