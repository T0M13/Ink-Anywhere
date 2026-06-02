# Ink Anywhere — Paralives custom PNG tattoo mod

Import your own PNGs (transparent background) as tattoos and place them anywhere
on the body using the game's native decal placement (move / scale / rotate / flip).

A BepInEx (Unity/C#) script mod. Not for Steam Workshop (script mods aren't
allowed there) — distribute via Nexus / GitHub / 6ix Plugin Hub.

## Status

- **Phase 0 (done, builds):** loads as a plugin, creates a `CustomTattoos`
  folder next to `Paralives.exe`, and on hotkey **F6** decodes every PNG there
  into a Unity texture and logs size + alpha. Proves the PNG→engine path.
- **Phase 1+ (next):** register the texture with `AssetManager`, build an
  `EquipmentItem(IsDecal=true)`, inject it into the Paramaker catalog, reuse the
  game's `UIDecalPlacement`. See the roadmap comment in `Plugin.cs`.

## Build

```powershell
dotnet build -c Release
```

The build auto-copies the DLL to
`...\Paralives\BepInEx\plugins\InkAnywhere\`. Paths are set in the `.csproj`
(`GameManaged`, `PluginsDir`) — edit if your install moves.

## Test (Phase 0)

1. Launch Paralives once after installing BepInEx so it generates
   `BepInEx\config` and `BepInEx\LogOutput.log`.
2. Confirm load: `LogOutput.log` shows `Ink Anywhere loaded.`
3. Put a transparent `.png` in `...\Paralives\CustomTattoos\`.
4. In-game press **F6** → the log reports `OK <file> WxH ... hasAlpha=True`.

## Key game internals (from decompiling Paralives.dll)

- Tattoo = `Setting.EquipmentItem` + `IsDecal` + `EquipmentTexture.TextureGUID`.
- `AssetManager.Instance` — GUID-keyed assets; `RegisterAsset(...)`, `GetSprite(guid)`.
- `UIDecalPlacement.Show(...)` — full placement UI, already in the engine.
- `ModManager.Instance.LocalMod` — writable system mod for persisting content.
- `EquipmentItemDuplicator` — the game's own "create equipment item" routine.

Decompiled reference source:
`..\ParalivesBetterRelationships\_tools\Paralives.decompiled\`
