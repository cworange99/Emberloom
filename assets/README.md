# Art assets

The low-poly kits the 3D views are built from. All Kenney, all CC0 — no attribution
required, though `LICENSE-kenney.txt` travels with each kit anyway.

`assets/kit/` is deliberately **not in the repository** (see `.gitignore`) — decided 11 September
2026. It is about 10 MB of GLB. A fresh clone gets the kits with:

```powershell
./assets/fetch-kits.ps1
```

which reads each asset's page on kenney.nl and takes the `.zip` link from it — Kenney's download
URLs carry a cache-busting hash that changes whenever an asset is updated, so a hardcoded link
would work until the day it quietly didn't. Kits already present are skipped; `-Force` replaces
them, and `-From <folder>` unpacks zips you already have instead of fetching anything.

One kit failing is a warning rather than a stop: three kits and a warning is a working game
missing some scenery, where none is placeholders everywhere. Without the kits the 3D views draw
placeholders and say so.

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

The script does this; by hand, download from <https://kenney.nl/assets>, take the
`Models/GLB format` folder from each, and drop it in under the name above.

**Each kit's `Textures/` folder must come with it.** The city, forest and folk GLBs reference
`colormap.png` externally rather than embedding it, so a `kit/city/` without
`kit/city/Textures/` loads without error and renders every building pure white. The furniture
kit is the exception — it embeds its own, and its `Textures/` is empty here and should be.

This is a trap worth repeating because it is one the fetch script itself fell into: PowerShell's
`Get-ChildItem -Path <folder> -Include *.png` matches against the folder's own name and copies
nothing, silently. The script now counts what landed and says so per kit rather than reporting
that a `Textures/` directory exists.

## How they are served

The API mounts `assets/kit/` at `/kit` (see `Program.cs`). They deliberately do not live in
`src/Emberloom.Api/wwwroot/`, because `npm run build` empties that folder.
