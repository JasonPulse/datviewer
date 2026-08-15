# FFXI DAT Viewer

A small Godot (C#) tool for browsing and inspecting Final Fantasy XI `.DAT` files.

> **No game data is included.** This repo and the released apps ship **zero** FFXI DATs — the viewer
> reads DATs from *your own* local FFXI install, which you point it at on first run.

## Download

Grab the latest **Windows** or **macOS** build from the
[Releases](https://github.com/JasonPulse/datviewer/releases) page, unzip, and run it. On first launch
a **Settings** panel opens — click **Browse…**, select your FFXI folder (the one containing `ROM/`,
`ROM2/` …, `FTABLE.DAT`), and you're in. Your choice is remembered for next time.

> macOS: the app is unsigned, so the first launch needs right-click → **Open** (or
> *System Settings → Privacy & Security → Open Anyway*).

Two jobs:

1. **Find where a DAT lives.** Two ways to browse, in the left panel's tabs:
   - **Library** — an AltanaViewer-style *named* catalog: pick a **Category** (PC / NPC / Effect /
     Image / Zones / Music), then drill down (PC → **Race** → **Slot** → item; NPC → **Family** →
     creature; …) and search by name. Picking "Mithran Separates" loads `ROM/52/21.DAT` for you —
     no need to know the number. Entries with no DAT under the current root are greyed out.
   - **ROM Tree** — the raw `ROM*` folder tree plus a **file id** jump box, for low-level browsing.

   Either way, for the selected file it shows the **ROM-relative path** (e.g. `ROM/9/2.DAT`) and the
   exact overlay path to mirror for an [XIPivot](https://github.com/Shirk/XIPivot) override
   (`<overlay>/ROM/9/2.DAT`), plus the numeric file id.
2. **See what a DAT contains.** Open any `.DAT` — retail or one you made — and preview it:
   - **Textures** — every IMG (0x20) chunk decoded (DXT1/DXT3/paletted/direct) as thumbnails.
   - **Model** — a posed, textured 3D preview, drag to orbit, wheel to zoom:
     - a self-contained model (creature / full NPC) shows posed;
     - an **equipment part** (e.g. a body piece) is shown **worn on a base race body**. The viewer
       **auto-detects the race + slot + item** so it dresses correctly without you knowing the piece:
       from the file's ROM path (retail, or a ROM ref in a mod's filename like
       `Savant's Bonnet ROM.253.26.DAT`), else race/slot keywords in the name (`…mithra_body_196.dat`,
       `Feet.DAT`), else — for a **hint-less** file like `60.dat` — the **slot** is read from where the
       mesh sits on the body and a **best-fit race** is chosen by trying the part on each race and taking
       the closest hug (labelled "best fit"; exact for distinctive parts, approximate between similar
       builds). You can still override **Race**/**Slot**. Uncheck *Wear on body* for the bare mesh.
     - **Animation** — models with `0x2b` clips get a transport bar: pick a **clip**, play/pause, toggle
       loop, change speed, and scrub the timeline (frame N/total). A standalone **animation DAT** (clips
       only, no mesh) is applied to the chosen race's body and plays its own clips.
     - **Spell effect** — an effect DAT (`0x05` particle generators) plays its particle effect
       (decoded via the vendored `EffectDecoder` + `EffectPlayer`) drawing the effect's own sprites.
   - **Chunks** — the chunk list with names, decoded types, and sizes.
   - **Hex** — a raw hex/ASCII dump.
   - **Music** — a dedicated top-level tab: pick a **Set** (sound folder / expansion), search, and
     click a track to play it in the bottom bar (play/pause, stop, scrubber, volume). Reads
     `<install>/soundN/win/music/data/musicNNN.bgw`. Early expansions (`sound`/`sound2`/`sound3`,
     PS-ADPCM) play; Aht Urhgan and later are ATRAC3 and can't be decoded (same limitation as the
     original Altana Viewer) — those report as unplayable.

All DAT decoding is handled by the vendored pure-C# decoders + posed-character build under `Vendor/`
(see `Vendor/README.md`) — there is no duplicated decode logic in the viewer itself.

### The named Library (`List/`)

The Library tab is powered by the CSV catalog under `List/`, vendored from
[voliathon/AltanaViewer](https://github.com/voliathon/AltanaViewer) (community-maintained; not
affiliated). Those CSVs map FFXI DAT references to readable, categorised names. Reference tokens are
resolved to ROM paths by `AltanaCatalog.ResolveRefPaths`:

| token | resolves to | meaning |
|-------|-------------|---------|
| `52/13` | `ROM/52/13.DAT` | 2-part = base ROM `dir/file` |
| `1/3/108` | `ROM/3/108.DAT` | 3-part = `romIndex/dir/file` (index 1 = base ROM) |
| `9/1/63` | `ROM9/1/63.DAT` | index N>1 = `ROMN` |
| `1/5/15-16` | `ROM/5/15.DAT`, `ROM/5/16.DAT` | range on the last number |
| `a;b;c` | each part resolved | composite model (several DATs) |

A composite selection (a creature, or a worn PC set) previews its **primary** DAT; the full resolved
part list is exposed on `Main.SelectedParts` for the model-assembly pass to consume. PC equipment
picked in the Library automatically drives the Model tab's *wear on body* controls to the matching
race/slot, so it renders worn.

## Using it

- **⚙ Settings** — point the viewer at your FFXI install (the folder with `ROM/`, `FTABLE.DAT`) and
  set the UI scale. The install path and scale are saved to `user://settings.cfg`. On first run with
  no valid install, Settings opens automatically. Startup order: `$DATVIEWER_ROOT` → saved setting →
  a sibling `corpus/` checkout (dev only) → onboarding.
- **Open .DAT…** — open any file, including user-made mod DATs (no file id needed). Remembers the
  last folder you browsed (saved to `user://settings.cfg`) and reopens there next time.
- **Drag & drop** — drag a `.DAT` from Finder/Explorer onto the window to open it.
- **ROM Tree search** — the ROM Tree tab has a search box: type any part of a ROM path or a file id to
  find files across the whole install (blank restores the folder tree).
- **Library** tab — browse models/art by name (PC/NPC/Effect/…); search filters the current list.
- **Animation** tab — pick a **Race**, then a named **action** (Battle, Emote, Ability, …) and it plays
  on that race's body in the Model tab.
- **Music** tab — browse and play music tracks by expansion.
- **ROM Tree** tab — raw folder browsing + the **file id** jump box.

## Build from source

```
git clone git@github.com:JasonPulse/datviewer.git
/Applications/Godot_mono.app/Contents/MacOS/Godot --path ./datviewer
```

Self-contained — Godot 4.7.1 + .NET 8, no other checkout needed. The FFXI DAT decoders and the
posed/skinned model build are **vendored** under `Vendor/` (see `Vendor/README.md`).

The `List/` catalog and `data/models` tables carry a `.gdignore` (Godot never imports/packs them) and
are read as loose files via an executable-relative path — so they work both from the project tree and
when shipped beside the exported app.

## Releases (CI)

Pushing Conventional Commits to `main` triggers `.github/workflows/release.yml`: semantic-release cuts
a version + GitHub Release, then Godot exports the **Windows** (cross-exported on Linux) and **macOS
universal** apps — with `List/` + `data/` bundled alongside — and attaches them. No game data is ever
in the repo or the CI environment.

### Headless / scripting

| Env | Effect |
|-----|--------|
| `DATVIEWER_UISCALE=<f>` | global UI scale (default `1.5`; raise for hi-DPI, e.g. `2.0`) |
| `DATVIEWER_ROOT=<dir>` | install root to load on start |
| `DATVIEWER_OPEN=<file>` | open this DAT on start |
| `DATVIEWER_TAB=0..3` | select tab (Textures/Model/Chunks/Hex) |
| `DATVIEWER_RACE=1..8` | wear race (1 Hume♂ … 7 Mithra, 8 Galka) |
| `DATVIEWER_SLOT=body\|head\|hands\|legs\|feet` | wear slot |
| `DATVIEWER_LIBTEST=<cat/group/slot>` | navigate the Library (e.g. `PC/Mithra/Body`) and load its first on-disk entry |
| `DATVIEWER_SETTINGS=1` | open the Settings panel on start |
| `DATVIEWER_SHOT=<png>` `DATVIEWER_SHOT_FRAME=<n>` | save a screenshot after N frames, then quit |
