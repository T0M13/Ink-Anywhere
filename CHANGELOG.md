# Changelog

## 0.3.2 Beta - 2026-06-08

### Fixed

- **Custom tattoos disappearing on load (more robust fix).** 0.3.1 relied on a timer, which
  still missed some setups (especially with Workshop mods). Now we hook the moment each
  character's visual loads and inject + re-render right then — deterministic, no timing race.

## 0.3.1 Beta - 2026-06-06

### Fixed

- **Custom tattoos now show when loading a save** — previously a saved character's tattoo
  was blank until you opened the Paramaker and backed out. The mod now re-injects in live
  mode and re-renders loaded characters so worn tattoos appear immediately.

(Includes everything from 0.3.0 below.)

## 0.3.0 Beta - 2026-06-06

### Added

- **Recolorable tattoos.** Tattoos that are one colour / a few shades can now be recoloured
  with the game's native colour swatches, per placement (each decal keeps its own colour).
  Colourful images stay full-colour. Pure **#000 black** is supported.
- **Delete tattoos in-game** via a small "x" on each custom tile (with a confirmation), which
  also deletes the PNG.

### Changed / Performance

- **Fixes the background slowdown.** All work is now event-driven and only runs while you're
  in character customization — nothing per-frame during normal play.
- Recolouring uses the native GrayMask shader (GPU, per-instance) instead of CPU repainting,
  so there's no frame drop when changing colours and no texture glitches.
- Filtered out the harmless "Texture is null" log spam the game emits while rebuilding the
  catalog (it was costing FPS to log).
- Thumbnails composited on a light background so dark tattoos are visible; catalog refreshes
  reliably after add/delete.

## 0.2.2 Beta (experimental) - 2026-06-05

Performance + reliability rework. Experimental — 0.2.1 remains the stable release.

### Fixed / Changed

- **Fixes background slowdown.** The mod no longer does any per-frame work during normal
  gameplay. Previously it polled every second and ran the texture upkeep every frame even
  outside character customization.
- **Event-driven now.** All logic is hooked to the Paramaker catalog refresh, so it only
  runs while you're in character customization, and re-injects our tattoos/"+" tile exactly
  when the game rebuilds the catalog (Workshop mods, save changes) — closing the gap where
  items could briefly go missing.
- Removed the repeated `FindObjectsOfType` scans; the texture watchdog is throttled and only
  active in the Paramaker.

## 0.2.1 Beta - 2026-06-03

### Fixed

- **Mod no longer disappears when Steam Workshop mods are enabled or when re-entering a save.**
  The game rebuilds its tattoo catalog in those cases, which wiped the injected tattoos and
  the "+" tile. The mod now self-heals: it detects when its items are missing and re-injects
  them automatically, so the "+" tile and custom tattoos stay available.

## 0.2.0 Beta - 2026-06-03

### Added

- In-grid delete: custom tattoo tiles now show a small "x" button to remove them.
- Confirmation dialog (native Yes/No prompt) before deleting a custom tattoo.
- Deleting a tattoo unequips it, removes it from the catalog, and deletes its PNG file.

### Changed

- Thumbnails are now composited onto a light background so dark/black tattoos are clearly visible.

### Fixed

- Adding or deleting a tattoo now fully refreshes the catalog in-place (rebuilds for a
  short window so new thumbnails — including the "+" tile — appear without reopening the section).

## 0.1.0 Beta - 2026-06-02

First public beta release of Ink Anywhere for Paralives.

### Released

- Published the Nexus Mods page:
  <https://www.nexusmods.com/paralives/mods/154>
- Published the GitHub repository:
  <https://github.com/T0M13/Ink-Anywhere>
- Published the GitHub release download:
  <https://github.com/T0M13/Ink-Anywhere/releases/tag/v0.1.0-beta>
- Prepared a Steam Workshop companion/listing for discovery and install instructions.

### Added

- Import custom PNG files as full-color tattoos in Paralives.
- Load PNG files from the `CustomTattoos` folder on game start.
- Add an in-game `+` tile to the tattoo catalog.
- Open a native PNG file picker from the `+` tile.
- Register imported PNGs as Paralives texture assets at runtime.
- Clone an existing tattoo item so custom PNGs use the normal tattoo catalog and placement UI.
- Preserve full image colors with `ShaderType.NonRecolorable`.
- Generate clean square catalog thumbnails for imported PNGs.
- Expand decal scaling to `0.05` through `6`.
- Skip invalid image files safely.
- Reload custom textures if the game unloads them during runtime.
- Fall back to an on-screen add button if the in-grid tile hook is unavailable.

### Packaging

- Added a player-ready release zip:
  `InkAnywhere_0.1.0_Beta.zip`
- The zip extracts into this game-folder layout:

```text
BepInEx/plugins/InkAnywhere/ParalivesInkAnywhere.dll
README.md
inkanywhere_banner.png
```

- Added a direct GitHub download button to the README.
- Added install instructions for players who do not want to build from source.
- Added README banner, screenshots, and support links.

### Requirements

- Paralives
- BepInEx 5 x64 Mono

### Install

1. Install BepInEx 5 x64 Mono into the Paralives folder.
2. Run Paralives once, then close it.
3. Download `InkAnywhere_0.1.0_Beta.zip` from GitHub Releases or Nexus Mods.
4. Extract the zip into the Paralives game folder.
5. Confirm the DLL is here:
   `Paralives/BepInEx/plugins/InkAnywhere/ParalivesInkAnywhere.dll`
6. Launch Paralives.
7. Add PNGs with the in-game `+` tile, or place PNG files in:
   `Paralives/CustomTattoos/`

### Steam Workshop Notes

Steam Workshop is used as a companion/listing for this mod. The Workshop item
can explain the mod and point users to the release download, but the code-based
BepInEx plugin must still be installed from GitHub Releases or Nexus Mods.

### Known Limitations

- Early beta. Game updates may break the mod.
- Windows-only file picker implementation.
- Requires BepInEx.
- Steam Workshop subscription alone does not install the DLL.
