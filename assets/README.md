# Art assets

The low-poly kits the 3D views are built from. All Kenney, all CC0 — no attribution
required, though `LICENSE-kenney.txt` travels with each kit anyway.

`assets/kit/` is **not committed yet** (see `.gitignore`). It is about 10MB of GLB, and
whether that belongs in the repo or behind a fetch script is still an open question. Until
that is settled, a fresh clone needs the kits placed by hand.

## What goes where

| Folder | Kenney kit | Used by |
| --- | --- | --- |
| `kit/city/` | City Kit (Commercial) | the city view — named places, infill blocks, the towers |
| `kit/forest/` | Mini Forest | trees, rocks, the open-air rooms |
| `kit/folk/` | Mini Characters | colonists, in both views |
| `kit/room/` | Furniture Kit | interiors |

Download from <https://kenney.nl/assets>, take the `Models/GLB format` folder from each,
and drop it in under the name above.

**Each kit's `Textures/` folder must come with it.** The GLBs reference `colormap.png`
externally rather than embedding it, so a `kit/city/` without `kit/city/Textures/` loads
without error and renders every building pure white.

## How they are served

The API mounts `assets/kit/` at `/kit` (see `Program.cs`). They deliberately do not live in
`src/Emberloom.Api/wwwroot/`, because `npm run build` empties that folder.
