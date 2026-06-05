# Ink Anywhere — Recolorable Tattoos: research & design notes

Working notes for the next feature: let imported PNG tattoos be **recolored in-game**
using Paralives' native swatch/color UI. Captured 2026-06-04.

---

## Goal (from user)

Make imported tattoos recolorable. Caveat the user raised: a *colorful* image is hard
to recolor meaningfully. So the plan is:

- If the image is **one color / grayscale / only a few shades** → make it **recolorable**.
- If the image is **already colorful** → keep current behavior (full-color, locked).

Do this **without changing the existing working behavior** — only extend it.

---

## How recoloring works in Paralives (from decompiling `Paralives.dll`)

Decompiler used: `ilspycmd` (installed globally).
Assembly: `…/Paralives/Paralives_Data/Managed/Paralives.dll`.

### `ShaderType` enum (`Setting.ShaderType`)

```
Simple = 0
UNUSED = 1
GrayMask = 2          <-- recolorable path (texture used as grayscale mask + swatch color)
NonRecolorable = 3    <-- what we use now (image shown verbatim, full color)
TreeLeaves = 100 ... (world/object shaders, irrelevant)
Skin = 200, PatternThumbnailPreview = 201, PixelCensoring = 202
Doodle = 300, Painting = 301, ...
```

### `Setting.EquipmentTexture` (the per-texture struct we clone & repoint)

Color-relevant fields:
- `ulong TextureGUID` — main texture (we point this at our PNG).
- `ulong MaskTextureGUID` — mask texture. **We currently zero this.**
- `ShaderType ShaderType` — **default is `GrayMask`**. We force `NonRecolorable`.
- `Color Color = Color.white` — a per-texture tint multiplier.
- `ulong GlobalColor`, `bool UseSpecificSwatchColorIndex`, `int SpecificSwatchColorIndexToUse`
  — let a texture pull a specific swatch color zone.
- `bool AllowsPatterns`, z-order fields, `BodySides Side`, condition fields.

### `Setting.EquipmentItem` (the catalog item we clone)

Swatch / recolor-relevant fields:
- `ulong SwatchGroup` — references a `Swatches` group = the **palette of swatches**
  shown in the UI. **The template tattoo we clone already has this set.**
- `ulong DefaultSwatch`
- `UlongAndGuid[] ColorZoneNames`
- `SwatchColorZoneCount SwatchColorZoneCount`
- `SwatchThumbnailType SwatchThumbnailType = OneColor`
- (plus `Textures[]`, `IsDecal`, `DecalSectionData[]`, tags, etc.)

### What this means for us

The current mod (`Plugin.cs` ~line 327-340) does:
```csharp
et.TextureGUID    = texGuid;
et.MaskTextureGUID = 0uL;
et.ShaderType     = ShaderType.NonRecolorable;   // <-- color-locked
item.Textures = new[] { et };
```
Because we **shallow-clone a real decal tattoo template**, the clone already carries a
valid `SwatchGroup` / `ColorZoneNames` (the vanilla recolorable-tattoo palette). We throw
that away by forcing `NonRecolorable`.

**Hypothesis for the recolorable path:** keep `ShaderType.GrayMask` (do NOT override to
NonRecolorable) and keep the template's `SwatchGroup`. Then the game's swatch UI should
tint our texture by the selected swatch color. With GrayMask, the texture is almost
certainly used as a **grayscale luminance/alpha mask**, so:
- grayscale / single-color art → tints cleanly (what we want),
- full-color art → would get flattened to luminance then tinted (looks wrong → that's
  exactly why we keep colorful images on `NonRecolorable`).

### OPEN QUESTION (not yet verified — was mid-investigation when paused)

Exactly how `GrayMask` combines the main texture vs `MaskTextureGUID` vs swatch color in
the skin compositor. Types to inspect next:
- `CreateTextureMapJob` (struct)
- `UpdateCharacterVisualTexture`, `UpdateCharacterVisualCombineMeshTexture`
- `SwatchManager`, `SwatchSetter`, `MessageChangeItemSwatch` / `ChangeItemSwatchEvent`
- `ShaderDefiner` / `ShaderParameters`
Need to confirm: does GrayMask read **alpha** for coverage and **luminance** for the
mask, or does it need a separate mask texture in `MaskTextureGUID`? This determines
whether we can reuse our single PNG or must generate a grayscale mask from it.

---

## Proposed design (draft — pending the open question)

1. **Detect colorfulness when importing** (we already decode every PNG in the probe step
   at `Plugin.cs:295`). Compute a saturation metric over non-transparent pixels:
   - count distinct quantized hues / mean+max HSV saturation.
   - `grayscale/few-shade` → recolorable; else → colorful.
2. **Recolorable branch:** clone template, keep `ShaderType.GrayMask`, keep the template's
   `SwatchGroup` (so native swatches appear), point `TextureGUID` at our PNG (and possibly
   build a grayscale mask into `MaskTextureGUID` — depends on the open question).
3. **Colorful branch:** unchanged — `NonRecolorable`, `MaskTextureGUID = 0`.
4. **Optional manual override via filename**, matching the existing GUID-from-filename
   convention:
   - `name.recolor.png` → force recolorable
   - `name.color.png` → force full-color
   (auto-detect otherwise.)
5. Keep the watchdog/self-heal/thumbnail code as-is.

Risk to watch: the watchdog reloads textures via `_texture` reflection; make sure the
recolorable path doesn't fight the swatch system or get re-flattened each frame.

---

## Community feedback (Nexus Mods comments, mod #154, as of 2026-06-04)

Stats: 2 endorsements, 223 unique DLs, 262 total DLs, 1.4k views, v0.2.1.

### Confirmed working after 0.2.1
The 0.2.1 self-heal fix resolved the big issue (mod wiped when Steam Workshop mods loaded
or when re-entering a save). Multiple users confirmed it now works **with Workshop mods
enabled** (XxAri96xX, WhiteWolf1262).

### Feature requests from comments
- **MikeNeedsMods:** "could you do something for putting custom shirts in the game? as just
  decals or a pattern that will cover the clothing?" → T0M13 replied **"Next mod - incoming 😁"**
  → i.e. a **custom clothing texture / pattern** mod is the other promised direction.
- **SirHex19:** asked for a smoking-pipe tattoo → answered "you can add any PNG yourself";
  noted maybe ship **ready-made tattoo packs** later.
- General: a few users wanted clearer install help (BepInEx 5 x64 Mono; ParaInjector helped
  one user get scripts loading).

### Recurring install gotchas (already largely fixed / documented)
- Must be **BepInEx 5.4.x, x64, Mono** — not 32-bit, not BepInEx 6.
- Some users needed **ParaScript / ParaInjector** to get script mods loading.
- A June-3 game patch dropped the same day as release; one user (faitviteuh) still couldn't
  load it + had a crash when toggling a workshop mod — unconfirmed whether patch-related.

---

## Status: PAUSED for user

Recolor mechanics mostly mapped. The one thing left to verify before coding is the exact
`GrayMask` texture/mask/swatch combination (the OPEN QUESTION above). No code changed yet.
