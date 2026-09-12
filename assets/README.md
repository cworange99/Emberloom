# Art assets

The low-poly kits the 3D views are built from. All Kenney, all CC0 — no attribution
required, though `LICENSE-kenney.txt` travels with each kit anyway.

`assets/kit/` is deliberately **not in the repository** (see `.gitignore`) — decided 11 September
2026. It is about 10 MB of GLB, and the kits will reach a machine by a fetch script or the game's
installer instead. Until one of those exists, a fresh clone needs the kits placed by hand;
without them the 3D views draw placeholders and say so.

The Kenney kits are the development art. They may ship, but the look is not final. Every art
choice lives in `client/src/city/art.ts` (the city) and `client/src/city/rooms.ts` (interiors),
so moving to a different kit is an edit to those tables rather than to the scenes.

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
