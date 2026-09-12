import * as THREE from 'three'
import { GLTFLoader, type GLTF } from 'three/addons/loaders/GLTFLoader.js'
import { clone as cloneSkinned } from 'three/addons/utils/SkeletonUtils.js'
import { kitUrl } from './art'

/**
 * What the city and the room scenes share: loading kit models, building a colonist's figure, and
 * playing its animations. One path, so a person looks the same in both views and a change of art
 * kit touches one loader rather than two.
 */

export const rand = (seed: number) => {
  const x = Math.sin(seed * 127.1) * 43758.5453
  return x - Math.floor(x)
}

export const hash = (text: string) => {
  let h = 0
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0
  return Math.abs(h)
}

/**
 * Loads each kit model once. A missing model resolves to null rather than rejecting: a fresh clone
 * without the art kit still gets a working view, drawn in placeholders, and can say why.
 */
export class KitLoader {
  private readonly loader = new GLTFLoader()
  private readonly pending = new Map<string, Promise<GLTF | null>>()
  private readonly resolved = new Map<string, GLTF | null>()

  /** True once any model has failed to load — the kit is missing, or part of it is. */
  missing = false

  load(model: string): Promise<GLTF | null> {
    let promise = this.pending.get(model)
    if (!promise) {
      promise = this.loader
        .loadAsync(kitUrl(model))
        .catch(() => {
          this.missing = true
          return null
        })
        .then((gltf) => {
          this.resolved.set(model, gltf)
          return gltf
        })
      this.pending.set(model, promise)
    }
    return promise
  }

  /** A model already loaded, or null. For building a scene in one go once everything is in. */
  peek(model: string): GLTF | null {
    return this.resolved.get(model) ?? null
  }

  async instance(model: string, fallback: () => THREE.Object3D): Promise<THREE.Object3D> {
    const gltf = await this.load(model)
    return withShadows(gltf ? gltf.scene.clone(true) : fallback())
  }
}

export function withShadows<T extends THREE.Object3D>(object: T): T {
  object.traverse((node) => {
    node.castShadow = true
    node.receiveShadow = true
  })
  return object
}

/** A colonist's figure and the state of its animation. */
export interface Figure {
  root: THREE.Object3D
  mixer: THREE.AnimationMixer | null
  clips: THREE.AnimationClip[]
  action: THREE.AnimationAction | null
  clip: string | null
}

export function makeFigure(gltf: GLTF | null, scale: number): Figure {
  // Rigged characters need SkeletonUtils.clone: a plain clone() keeps the mesh but not its
  // skeleton binding, and the colonist renders collapsed into a point.
  const root = withShadows(gltf ? cloneSkinned(gltf.scene) : fallbackFigure())
  root.scale.setScalar(scale)

  return {
    root,
    mixer: gltf ? new THREE.AnimationMixer(root) : null,
    clips: gltf?.animations ?? [],
    action: null,
    clip: null,
  }
}

/** Cross-fades a figure to a named clip, falling back to idle when the kit has no such clip. */
export function playClip(figure: Figure, name: string, seed: number) {
  if (figure.clip === name || !figure.mixer) return

  const clip =
    figure.clips.find((c) => c.name === name) ?? figure.clips.find((c) => c.name === 'idle')
  if (!clip) return

  const next = figure.mixer.clipAction(clip)
  next.reset().fadeIn(0.2).play()
  figure.action?.fadeOut(0.2)

  // Started at a different moment each, or a room of colonists breathes in unison.
  if (figure.clip === null) next.time = rand(seed) * clip.duration

  figure.action = next
  figure.clip = name
}

function fallbackFigure(): THREE.Object3D {
  const mesh = new THREE.Mesh(
    new THREE.CapsuleGeometry(0.12, 0.34, 4, 8),
    new THREE.MeshLambertMaterial({ color: 0x6fbf92 }),
  )
  mesh.position.y = 0.29
  return new THREE.Group().add(mesh)
}
