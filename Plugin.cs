using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Setting;
using UnityEngine;
using UnityEngine.UI;

namespace InkAnywhere
{
    [BepInPlugin(Guid, "Ink Anywhere", "0.2.2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.tomi.paralives.inkanywhere";

        internal static ManualLogSource Log;
        internal static string TattooFolder;

        private void Awake()
        {
            Log = Logger;
            try
            {
                TattooFolder = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath) ?? ".", "CustomTattoos");
                Directory.CreateDirectory(TattooFolder);

                Log.LogInfo("===== Ink Anywhere BUILD-D (self-runner) loaded =====");
                Log.LogInfo("Drop PNGs in: " + TattooFolder);

                // Per-frame callbacks on the BepInEx manager object aren't firing on
                // this game, so host our logic on our own scene GameObject instead.
                var go = new GameObject("InkAnywhereRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<Runner>();
                Log.LogInfo("Runner GameObject created.");

                // Harmony lets us intercept clicks on our in-grid "+" tile. If it fails
                // to load, the mod still works and falls back to a floating button.
                try
                {
                    new Harmony(Guid).PatchAll();
                    Runner.HarmonyOk = true;
                    Log.LogInfo("Harmony patched OK (in-grid + tile enabled).");
                }
                catch (Exception he)
                {
                    Runner.HarmonyOk = false;
                    Log.LogWarning("Harmony unavailable, using fallback button: " + he.Message);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Awake failed: " + e);
            }
        }
    }

    /// <summary>Hosts the per-frame logic + overlay on its own GameObject.</summary>
    public class Runner : MonoBehaviour
    {
        private static ManualLogSource Log => Plugin.Log;

        public static Runner Instance;
        public static bool HarmonyOk;

        // Fixed GUID for our special "+ Add tattoo" catalog tile.
        public const ulong AddButtonGuid = 0xADDA7700ADDA7700UL;
        private static readonly bool DevMode = true;
        private const int DevEventLimit = 10;

        private void Awake() => Instance = this;

        private bool _updateSeen;
        private float _nextWatchdog;

        // Results for the status panel.
        private int _loaded;
        private readonly List<string> _failures = new List<string>();
        private bool _panelOpen = true;

        // True while the Paramaker tattoo (decal) section is open — shows the + button.
        private bool _inTattooSection;

        // Set by the catalog hook (OnCatalogRefreshing); means "we're in the Paramaker now".
        private UICharacterCreatorContextualList _activeList;
        private float _lastCatalogActive;
        private float _nextPresenceCheck;

        // Deferred catalog rebuild: one rebuild next frame after an add/delete.
        private bool _rebuildPending;

        // Watchdog: the game keeps unloading our texture and its own reload returns
        // null, which NREs the skin compositor. We track each texture GUID -> source
        // PNG path and re-load it ourselves whenever the asset's texture goes null.
        private static readonly FieldInfo TexField =
            typeof(AssetTexture).GetField("_texture", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo SpriteField =
            typeof(AssetTexture).GetField("_sprite", BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly Dictionary<ulong, string> _texPaths = new Dictionary<ulong, string>();
        private readonly List<string> _devEvents = new List<string>();

        // Recolor uses native GrayMask for colours, but #000 reads as dark grey through that
        // shader. So when a decal's swatch is (near) black we swap in a pure-black, verbatim
        // texture (built once per tattoo, cached) for true #000. Colours still use GrayMask.
        private readonly HashSet<ulong> _recolorTexGuids = new HashSet<ulong>();
        private readonly Dictionary<ulong, Texture2D> _blackTextures = new Dictionary<ulong, Texture2D>();

        private float _devFps;
        private float _devFrameMs;
        private float _devWorstFrameMs;
        private float _devLowestFps;
        private int _devCatalogHooks;
        private int _devTattooSectionHooks;
        private int _devRefreshRequests;
        private int _devRefreshTicks;
        private int _devRefreshErrors;
        private int _devInjectCalls;
        private int _devInjectedNew;
        private int _devTextureChecks;
        private int _devTextureReloads;
        private int _devAddTileClicks;
        private int _devPickerStarts;
        private int _devPickerCancels;
        private int _devPickerImports;
        private int _devTilesDecorated;
        private int _devDeleteButtonsShown;
        private int _devDeleteRequests;
        private bool _devLastTattooSection;

        private void Update()
        {
            if (DevMode) UpdateDevFrameStats();

            if (!_updateSeen)
            {
                _updateSeen = true;
                Log.LogInfo("Runner.Update is ticking!");
                DevEvent("runner update ticking");
            }

            // Everything else only matters in the Paramaker. The catalog hook
            // (OnCatalogRefreshing) sets _lastCatalogActive every frame the tattoo/clothing
            // catalog updates, and handles (re)injecting wiped items. So when we're not in
            // there, Update does nothing — no game-wide polling.
            bool catalogActive = Time.unscaledTime - _lastCatalogActive < 1.5f;
            if (!catalogActive) { _inTattooSection = false; return; }

            // Keep our textures valid while editing (the compositor reads them). Throttled —
            // the game only unloads them occasionally, so twice a second is plenty.
            if (_texPaths.Count > 0 && Time.unscaledTime >= _nextWatchdog)
            {
                _nextWatchdog = Time.unscaledTime + 0.5f;
                foreach (var guid in _texPaths.Keys)
                    EnsureTexture(guid);
            }

            // Apply a requested catalog rebuild here (not mid-click). Our textures are
            // protected + pre-warmed, so one rebuild is enough.
            if (_rebuildPending)
            {
                _rebuildPending = false;
                RefreshCatalog();
            }
        }

        // Called (via Harmony) every time the Paramaker catalog list refreshes — i.e. only
        // while you're in character customization. This is our event hook: it marks the
        // catalog active and re-injects our items if the game wiped them.
        internal void OnCatalogRefreshing(UICharacterCreatorContextualList list)
        {
            _activeList = list;
            _lastCatalogActive = Time.unscaledTime;
            _inTattooSection = list != null && list.UIDecalPositions != null;
            if (DevMode)
            {
                _devCatalogHooks++;
                if (_inTattooSection) _devTattooSectionHooks++;
                if (_devLastTattooSection != _inTattooSection)
                {
                    _devLastTattooSection = _inTattooSection;
                    DevEvent(_inTattooSection ? "tattoo section active" : "tattoo section inactive");
                }
            }

            // This hook fires every frame the catalog updates — keep it nearly free.
            // The "are our items still here?" scan only needs to run a couple times a sec.
            if (Time.unscaledTime < _nextPresenceCheck) return;
            _nextPresenceCheck = Time.unscaledTime + 0.5f;

            var eq = Settings.Get<Equipment>();
            if (eq?.EquipmentItems == null || eq.EquipmentItems.Length == 0) return;
            if (!OurItemsPresent(eq))
            {
                DevEvent("catalog items missing, injecting");
                Inject();
                // Force this same refresh to rebuild now that our items are back.
                if (list != null)
                    typeof(UICharacterCreatorContextualList)
                        .GetField("_lastTagsHash", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(list, 0uL);
            }
        }

        // Ask for a couple of catalog rebuilds (sprites are already pre-warmed, so this
        // just needs to redraw once or twice — not spam rebuilds).
        private void RequestRefresh()
        {
            _rebuildPending = true;
            if (DevMode) _devRefreshRequests++;
            DevEvent("refresh requested");
        }

        // Force the tattoo catalog to fully rebuild now (like closing + reopening it).
        private void RefreshCatalog()
        {
            if (DevMode) _devRefreshTicks++;
            var list = _activeList;
            if (list == null) return;

            // Load our textures (with keep-alive flags) BEFORE the rebuild reads their
            // sprites — otherwise the game logs "Texture is null" and tiles build blank.
            foreach (var guid in _texPaths.Keys)
            {
                EnsureTexture(guid);
                try { var _ = AssetManager.Instance.GetSprite(guid); } catch { /* not ready yet */ }
            }

            try
            {
                typeof(UICharacterCreatorContextualList)
                    .GetField("_lastTagsHash", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(list, 0uL);
                list.RefreshListContent();
            }
            catch (Exception e)
            {
                if (DevMode) _devRefreshErrors++;
                DevEvent("refresh error: " + e.Message);
                Log.LogError("[refresh] " + e);
            }
        }

        // Reload our PNG into the asset whenever its texture is missing/destroyed.
        private void EnsureTexture(ulong texGuid)
        {
            if (DevMode) _devTextureChecks++;
            var asset = AssetManager.Instance.GetAssetOfType<AssetTexture>(texGuid);
            if (asset == null || TexField == null) return;

            var current = TexField.GetValue(asset) as Texture2D;
            // Keep "ours" = a texture WE loaded with HideAndDontSave, which Unity's
            // UnloadUnusedAssets won't destroy. If it's missing, or it's a game-loaded
            // copy (hideFlags None — those get unloaded and leave a blank tile), reload
            // our protected copy.
            if (current != null && current.hideFlags != HideFlags.None) return;

            if (!_texPaths.TryGetValue(texGuid, out var path) || !File.Exists(path)) return;

            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.LoadImage(File.ReadAllBytes(path));
            tex.Apply();

            asset.GenerateTextureQualities = GenerateTextureQualityTypes.No;
            asset.IsCropped = false;
            TexField.SetValue(asset, tex);
            SpriteField?.SetValue(asset, null); // regenerate the sprite from the new texture
            asset.IsLoaded = true;
            if (DevMode) _devTextureReloads++;
            DevEvent("texture reloaded: " + Path.GetFileName(path));
        }

        private void OnGUI()
        {
            // Fallback floating button only if the in-grid "+" tile isn't available.
            if (_inTattooSection && !HarmonyOk)
            {
                const float bw = 200f, bh = 36f;
                var style = new GUIStyle(GUI.skin.button) { fontSize = 15 };
                if (GUI.Button(new Rect(Screen.width - bw - 24, 24, bw, bh), "＋  Add Tattoo (PNG)", style))
                    PickAndAddPng();
            }

            if (!DevMode) return;

            // Collapsed: a small button in the corner to reopen the panel.
            if (!_panelOpen)
            {
                if (GUI.Button(new Rect(10, 10, 130, 26), "Ink Anywhere"))
                    _panelOpen = true;
                return;
            }

            bool hasErrors = _failures.Count > 0;
            float h = 430 + (hasErrors ? Mathf.Min(_failures.Count, 6) * 20 + 24 : 0);
            GUI.Box(new Rect(10, 10, 520, h), "Ink Anywhere Dev");

            GUI.Label(new Rect(20, 34, 490, 22),
                hasErrors
                    ? $"{_loaded} tattoo(s) loaded — {_failures.Count} could not be added:"
                    : $"{_loaded} custom tattoo(s) loaded.");

            float y = 56;
            if (hasErrors)
            {
                var prev = GUI.color;
                GUI.color = new Color(1f, 0.6f, 0.6f);
                foreach (var f in _failures.Take(6))
                {
                    GUI.Label(new Rect(28, y, 352, 20), "• " + f);
                    y += 20;
                }
                if (_failures.Count > 6)
                {
                    GUI.Label(new Rect(28, y, 352, 20), $"…and {_failures.Count - 6} more");
                    y += 20;
                }
                GUI.color = prev;
                y += 4;
            }

            DrawDevDiagnostics(ref y);
            y += 4;

            if (GUI.Button(new Rect(20, y, 120, 28), "Inject"))
                Inject();
            if (GUI.Button(new Rect(148, y, 120, 28), "Rebuild UI"))
                RefreshCatalog();
            if (GUI.Button(new Rect(276, y, 104, 28), "Open folder"))
                Application.OpenURL("file://" + Plugin.TattooFolder);
            if (GUI.Button(new Rect(388, y, 58, 28), "Reset"))
                ResetDevStats();
            if (GUI.Button(new Rect(454, y, 58, 28), "Hide"))
                _panelOpen = false;
        }

        private void DrawDevDiagnostics(ref float y)
        {
            bool catalogActive = Time.unscaledTime - _lastCatalogActive < 1.5f;
            var eq = Settings.Get<Equipment>();
            int catalogItems = eq?.EquipmentItems?.Length ?? 0;
            int customItems = CountCustomItems(eq);
            bool plusPresent = eq?.EquipmentItems != null &&
                               eq.EquipmentItems.Any(e => e != null && e.GUID == AddButtonGuid);

            DevLine(ref y, $"FPS avg {_devFps:0.0} | frame {_devFrameMs:0.0} ms | worst {_devWorstFrameMs:0.0} ms | low {_devLowestFps:0.0} fps");
            DevLine(ref y, $"State catalog={catalogActive} tattoo={_inTattooSection} harmony={HarmonyOk} plus={plusPresent}");
            DevLine(ref y, $"Catalog items={catalogItems} custom={customItems} loaded={_loaded} failures={_failures.Count}");
            DevLine(ref y, $"Hooks catalog={_devCatalogHooks} tattoo={_devTattooSectionHooks} inject={_devInjectCalls} new={_devInjectedNew}");
            DevLine(ref y, $"Refresh requested={_devRefreshRequests} ticks={_devRefreshTicks} errors={_devRefreshErrors}");
            DevLine(ref y, $"Textures tracked={_texPaths.Count} checks={_devTextureChecks} reloads={_devTextureReloads}");
            DevLine(ref y, "Recolor: native GrayMask (white mask + per-instance swatch)");
            DevLine(ref y, $"UI addClicks={_devAddTileClicks} picker={_devPickerStarts}/{_devPickerImports}/{_devPickerCancels} tiles={_devTilesDecorated} deleteBtns={_devDeleteButtonsShown} deletes={_devDeleteRequests}");

            if (_devEvents.Count == 0) return;
            y += 4;
            DevLine(ref y, "Recent:");
            foreach (var evt in _devEvents)
                DevLine(ref y, "- " + evt);
        }

        private static void DevLine(ref float y, string text)
        {
            GUI.Label(new Rect(20, y, 500, 20), text);
            y += 18f;
        }

        private void UpdateDevFrameStats()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            _devFrameMs = dt * 1000f;
            float fps = 1f / dt;
            _devFps = _devFps <= 0f ? fps : Mathf.Lerp(_devFps, fps, 0.08f);
            _devLowestFps = _devLowestFps <= 0f ? fps : Mathf.Min(_devLowestFps, fps);
            _devWorstFrameMs = Mathf.Max(_devWorstFrameMs, _devFrameMs);
        }

        private void ResetDevStats()
        {
            _devFps = 0f;
            _devFrameMs = 0f;
            _devWorstFrameMs = 0f;
            _devLowestFps = 0f;
            _devCatalogHooks = 0;
            _devTattooSectionHooks = 0;
            _devRefreshRequests = 0;
            _devRefreshTicks = 0;
            _devRefreshErrors = 0;
            _devInjectCalls = 0;
            _devInjectedNew = 0;
            _devTextureChecks = 0;
            _devTextureReloads = 0;
            _devAddTileClicks = 0;
            _devPickerStarts = 0;
            _devPickerCancels = 0;
            _devPickerImports = 0;
            _devTilesDecorated = 0;
            _devDeleteButtonsShown = 0;
            _devDeleteRequests = 0;
            _devEvents.Clear();
            DevEvent("stats reset");
        }

        private void DevEvent(string message)
        {
            if (!DevMode) return;

            _devEvents.Insert(0, $"{Time.realtimeSinceStartup:0.0}s {message}");
            if (_devEvents.Count > DevEventLimit)
                _devEvents.RemoveAt(_devEvents.Count - 1);
        }

        private static int CountCustomItems(Equipment eq)
        {
            return eq?.EquipmentItems?.Count(e => e != null && e.DisplayName != null &&
                                                  e.DisplayName.StartsWith("Ink: ")) ?? 0;
        }

        // Are our injected items still in the catalog? (They get wiped on mod reload.)
        private static bool OurItemsPresent(Equipment eq)
        {
            if (HarmonyOk)
                return eq.EquipmentItems.Any(e => e != null && e.GUID == AddButtonGuid);
            return eq.EquipmentItems.Any(e => e != null && e.DisplayName != null &&
                                              e.DisplayName.StartsWith("Ink: "));
        }

        // ---- Turn every PNG in the folder into a tattoo in the catalog ----
        private void Inject()
        {
            try
            {
                if (DevMode) _devInjectCalls++;
                DevEvent("inject start");

                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems == null) return;

                // Template = an existing tattoo we clone (reuses its tags/swatch/decal sections).
                // Prefer a real "Tattoo" so recolorable items inherit the tattoo swatch palette.
                var template = eq.EquipmentItems.FirstOrDefault(e =>
                                   e != null && e.IsDecal && e.DisplayName != null && e.DisplayName.StartsWith("Tattoo"))
                               ?? eq.EquipmentItems.FirstOrDefault(e => e != null && e.IsDecal);
                if (template == null) return;

                // Allow much smaller/larger tattoo scaling than the default 0.25–1.5.
                var decals = Settings.Get<Decals>();
                if (decals != null) { decals.DecalScaleMin = 0.05f; decals.DecalScaleMax = 6f; }

                // Drop our "+" tile so we can re-add it last after the real tattoos.
                eq.EquipmentItems = eq.EquipmentItems.Where(e => e == null || e.GUID != AddButtonGuid).ToArray();

                var iconDir = Path.Combine(Plugin.TattooFolder, "_icons");
                Directory.CreateDirectory(iconDir);

                _failures.Clear();
                _loaded = 0;
                int newlyAdded = 0;

                foreach (var path in Directory.GetFiles(Plugin.TattooFolder, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    ulong equipGuid = Hash(name + "|equip");

                    // Skip anything already in the catalog BEFORE any decoding — keeps
                    // re-injects/refreshes cheap (only brand-new files do heavy work).
                    if (eq.EquipmentItems.Any(e => e != null && e.GUID == equipGuid)) { _loaded++; continue; }

                    try
                    {
                        // Validate + decide recolorable (grayscale/single-hue) vs full-color.
                        var probe = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                        bool ok = probe.LoadImage(File.ReadAllBytes(path)) && probe.width > 2 && probe.height > 2;
                        bool recolor = ok && !IsColorful(probe);
                        UnityEngine.Object.Destroy(probe);
                        if (!ok) { _failures.Add(name + " — not a valid image"); continue; }

                        ulong texGuid = Hash(name + "|tex");
                        ulong iconGuid = Hash(name + "|icon");

                        // On-body texture. Colorful = the original PNG (NonRecolorable, shown
                        // as-is). Recolorable = a generated WHITE mask + GrayMask shader, so the
                        // game tints it per-decal from the swatch — native, GPU, per-instance,
                        // and saved with the character. (No runtime repainting.)
                        string texSource = recolor ? Path.Combine(iconDir, name + ".mask.png") : path;
                        if (AssetManager.Instance.GetAssetOfType<AssetTexture>(texGuid) == null)
                        {
                            if (recolor && !File.Exists(texSource)) MakeRecolorMask(path, texSource);
                            var asset = AssetManager.Instance.RegisterAsset(texSource, texGuid, false, 0uL) as AssetTexture;
                            if (asset == null) { _failures.Add(name + " — texture register failed"); continue; }
                            asset.IsClampTextureWrapMode = true;
                        }
                        _texPaths[texGuid] = texSource;
                        EnsureTexture(texGuid);
                        if (recolor) _recolorTexGuids.Add(texGuid);

                        // Tidy square thumbnail (built once per session; survives re-injects).
                        string iconPath = Path.Combine(iconDir, name + ".png");
                        if (AssetManager.Instance.GetAssetOfType<AssetTexture>(iconGuid) == null)
                        {
                            if (!File.Exists(iconPath)) MakeSquareIcon(path, iconPath);
                            var ia = AssetManager.Instance.RegisterAsset(iconPath, iconGuid, false, 0uL) as AssetTexture;
                            if (ia != null) ia.IsClampTextureWrapMode = true;
                        }
                        _texPaths[iconGuid] = iconPath;
                        EnsureTexture(iconGuid);
                        // Create the sprite now so the tile never builds blank.
                        try { var _ = AssetManager.Instance.GetSprite(iconGuid); } catch { }

                        // Clone a real tattoo and repoint it at our texture.
                        var item = ShallowClone(template);
                        item.GUID = equipGuid;
                        item.DisplayName = "Ink: " + name;
                        item.VisibleInCatalog = true;
                        item.TextureIconGUID = iconGuid;
                        if (template.Textures != null && template.Textures.Length > 0)
                        {
                            var et = ShallowClone(template.Textures[0]);
                            et.TextureGUID = texGuid;
                            et.MaskTextureGUID = 0uL;
                            // Recolorable: GrayMask tints the white mask by the per-instance
                            // swatch colour (native, GPU). Colorful: NonRecolorable = original.
                            et.ShaderType = recolor ? ShaderType.GrayMask : ShaderType.NonRecolorable;
                            item.Textures = new[] { et };
                        }

                        // Recolorable tattoos default to black (still recolorable via swatches),
                        // so a black design stays black unless the player picks another color.
                        // Full opacity by default so black can be truly black (the game otherwise
                        // attenuates tattoo opacity, blending skin through = never pure black).
                        if (recolor)
                        {
                            ulong black = EnsureBlackSwatch(item.SwatchGroup);
                            if (black != 0uL) item.DefaultSwatch = black;
                            item.CanChangeOpacity = true;
                            item.DefaultOpacity = 1f;
                            item.OpacityAttenuationRatio = 1f;
                        }

                        eq.EquipmentItems = eq.EquipmentItems.Concat(new[] { item }).ToArray();
                        newlyAdded++;
                        _loaded++;
                    }
                    catch (Exception ex)
                    {
                        _failures.Add(name + " — error");
                        Log.LogError($"[inject] failed '{name}': {ex.Message}");
                    }
                }

                // Append the "+" tile last (only if Harmony can intercept its click).
                if (HarmonyOk)
                {
                    string plusPath = Path.Combine(iconDir, "_add.png");
                    ulong plusIcon = Hash("__add__|icon");
                    if (AssetManager.Instance.GetAssetOfType<AssetTexture>(plusIcon) == null)
                    {
                        MakePlusIcon(plusPath);
                        var pa = AssetManager.Instance.RegisterAsset(plusPath, plusIcon, false, 0uL) as AssetTexture;
                        if (pa != null) pa.IsClampTextureWrapMode = true;
                    }
                    _texPaths[plusIcon] = plusPath;
                    EnsureTexture(plusIcon);

                    var plus = ShallowClone(template);
                    plus.GUID = AddButtonGuid;
                    plus.DisplayName = "Add custom tattoo";
                    plus.VisibleInCatalog = true;
                    plus.TextureIconGUID = plusIcon;
                    eq.EquipmentItems = eq.EquipmentItems.Concat(new[] { plus }).ToArray();
                }

                // Rebuild the GUID lookup so items can be equipped/saved.
                typeof(Equipment).GetMethod("RefreshDictionary",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(eq, null);

                // Fully rebuild the catalog UI over the next moment.
                RequestRefresh();

                if (DevMode) _devInjectedNew += newlyAdded;
                DevEvent($"inject done loaded={_loaded} new={newlyAdded} failed={_failures.Count}");
                Log.LogInfo($"[inject] done: loaded={_loaded}, new={newlyAdded}, failed={_failures.Count}");
            }
            catch (Exception e)
            {
                DevEvent("inject fatal: " + e.Message);
                Log.LogError("[inject] fatal " + e);
            }
        }

        // Make a clean square thumbnail: the image fit + centered on transparency.
        private static void MakeSquareIcon(string srcPath, string outPath, int size = 256)
        {
            var src = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            src.LoadImage(File.ReadAllBytes(srcPath));

            // Opaque light background so dark/black tattoos are clearly visible.
            var bg = new Color(0.93f, 0.92f, 0.90f, 1f);
            var icon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;
            icon.SetPixels(pixels);

            // Fit the image, centered, and alpha-blend it over the background.
            float aspect = (float)src.width / src.height;
            int pad = Mathf.RoundToInt(size * 0.06f); // small margin
            int box = size - pad * 2;
            int w = aspect >= 1f ? box : Mathf.RoundToInt(box * aspect);
            int h = aspect >= 1f ? Mathf.RoundToInt(box / aspect) : box;
            int ox = (size - w) / 2, oy = (size - h) / 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = src.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                    var blended = Color.Lerp(bg, new Color(c.r, c.g, c.b, 1f), c.a);
                    icon.SetPixel(ox + x, oy + y, blended);
                }
            icon.Apply();

            File.WriteAllBytes(outPath, icon.EncodeToPNG());
            UnityEngine.Object.Destroy(src);
            UnityEngine.Object.Destroy(icon);
        }

        // Build a white mask for the GrayMask shader: RGB = white, alpha = how "inked"
        // each pixel is. The game then tints white by the swatch colour per decal
        // (white x colour = colour). Generated once and cached to disk.
        private static void MakeRecolorMask(string srcPath, string outPath)
        {
            var src = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            src.LoadImage(File.ReadAllBytes(srcPath));
            var px = src.GetPixels32();

            // If the image relies on transparency, use alpha as coverage; if it's an opaque
            // image, use darkness (1 - luminance) so dark designs become the inked area.
            int transparent = 0;
            for (int i = 0; i < px.Length; i++) if (px[i].a < 250) transparent++;
            bool hasTransparency = transparent > px.Length * 0.02f;

            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                int a;
                if (c.a == 0) a = 0;
                else if (hasTransparency) a = c.a;
                else
                {
                    float lum = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                    a = Mathf.Clamp(Mathf.RoundToInt(c.a * (1f - lum)), 0, 255);
                }
                px[i] = new Color32(255, 255, 255, (byte)a);
            }

            src.SetPixels32(px);
            src.Apply();
            File.WriteAllBytes(outPath, src.EncodeToPNG());
            UnityEngine.Object.Destroy(src);
        }

        // For each of OUR recolor decals whose chosen swatch is (near) #000, swap GrayMask
        // for a pure-black verbatim texture so true black is actually black. Colours are
        // left on GrayMask. The black texture is built once per tattoo and cached — no churn.
        internal void ApplyBlackOverride(SkinLayer[] layers)
        {
            if (layers == null || _recolorTexGuids.Count == 0) return;

            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.Texture == null || layer.TextureType != TextureType.Albedo) continue;
                if (layer.ShaderType != ShaderType.GrayMask) continue;
                if (!IsNearBlack(layer.Colors)) continue;

                ulong texGuid = FindRecolorTexGuid(layer.Texture);
                if (texGuid == 0uL) continue;

                var black = GetBlackTexture(texGuid, layer.Texture);
                if (black == null) continue;

                layer.Texture = black;
                layer.ShaderType = ShaderType.NonRecolorable;
                layer.MaskTexture = 0uL;
                layer.Opacity = 1f;
                layers[i] = layer;
            }
        }

        private ulong FindRecolorTexGuid(Texture2D tex)
        {
            foreach (var g in _recolorTexGuids)
            {
                var a = AssetManager.Instance.GetAssetOfType<AssetTexture>(g);
                if (a != null && a.Texture == tex) return g;
            }
            return 0uL;
        }

        private Texture2D GetBlackTexture(ulong texGuid, Texture2D maskTex)
        {
            if (_blackTextures.TryGetValue(texGuid, out var existing) && existing != null) return existing;
            if (maskTex == null) return null;

            Color32[] px;
            try { px = maskTex.GetPixels32(); } catch { return null; }
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, px[i].a); // black, keep coverage

            var black = new Texture2D(maskTex.width, maskTex.height, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            black.SetPixels32(px);
            black.Apply(false, false);
            _blackTextures[texGuid] = black;
            return black;
        }

        private static bool IsNearBlack(Color[] colors)
        {
            if (colors == null || colors.Length == 0) return false;
            var c = colors[0];
            return c.r < 0.04f && c.g < 0.04f && c.b < 0.04f;
        }

        // Draw a simple "+" tile icon (a plus on a light background).
        private static void MakePlusIcon(string outPath, int size = 256)
        {
            var bg = new Color(0.90f, 0.90f, 0.92f, 1f);
            var line = new Color(0.30f, 0.45f, 0.70f, 1f);
            var icon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

            int c = size / 2, half = Mathf.RoundToInt(size * 0.22f), th = Mathf.RoundToInt(size * 0.06f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool vert = Mathf.Abs(x - c) <= th && Mathf.Abs(y - c) <= half;
                    bool horiz = Mathf.Abs(y - c) <= th && Mathf.Abs(x - c) <= half;
                    if (vert || horiz) pixels[y * size + x] = line;
                }
            icon.SetPixels(pixels);
            icon.Apply();
            File.WriteAllBytes(outPath, icon.EncodeToPNG());
            UnityEngine.Object.Destroy(icon);
        }

        // Open a PNG file picker, copy the chosen file into the folder, and inject it.
        internal void RecordAddTileClick()
        {
            if (DevMode) _devAddTileClicks++;
            DevEvent("add tile clicked");
        }

        internal void PickAndAddPng()
        {
            try
            {
                if (DevMode) _devPickerStarts++;
                DevEvent("picker open");
                string file = OpenPngDialog();
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                {
                    if (DevMode) _devPickerCancels++;
                    DevEvent("picker canceled");
                    return;
                }

                string dest = Path.Combine(Plugin.TattooFolder, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, dest, true);

                Inject();
                if (DevMode) _devPickerImports++;
                DevEvent("imported " + Path.GetFileName(file));
                Log.LogInfo("[add] imported " + Path.GetFileName(file));
            }
            catch (Exception e)
            {
                DevEvent("add error: " + e.Message);
                Log.LogError("[add] " + e);
            }
        }

        // ---- In-grid delete: add a small "x" to our custom tattoo tiles ----

        // Called from the Init postfix for every catalog tile.
        internal void DecorateTile(UIEquipmentItem tile, EquipmentItem equipment)
        {
            if (tile == null) return;
            bool ours = equipment != null && equipment.DisplayName != null &&
                        equipment.DisplayName.StartsWith("Ink: ");
            if (DevMode && ours) _devTilesDecorated++;

            var existing = tile.transform.Find("InkDeleteBtn");
            if (!ours)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            var go = existing != null ? existing.gameObject : CreateDeleteButton(tile.transform);
            go.SetActive(true);
            if (DevMode) _devDeleteButtonsShown++;

            string name = equipment.DisplayName.Substring("Ink: ".Length);
            var btn = go.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => DeleteCustomTattoo(name));
        }

        private static GameObject CreateDeleteButton(Transform parent)
        {
            var go = new GameObject("InkDeleteBtn",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-5f, -5f);
            rt.sizeDelta = new Vector2(24f, 24f);

            var img = go.GetComponent<Image>();
            img.sprite = DeleteSprite();
            img.raycastTarget = true;

            go.GetComponent<LayoutElement>().ignoreLayout = true; // don't let the grid reposition it
            go.transform.SetAsLastSibling();
            return go;
        }

        private static Sprite _deleteSprite;
        private static Sprite DeleteSprite()
        {
            if (_deleteSprite != null) return _deleteSprite;
            int s = 64;
            var t = new Texture2D(s, s, TextureFormat.ARGB32, false)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };

            var px = new Color[s * s];
            var center = new Vector2(s / 2f, s / 2f);
            float r = s / 2f - 1.5f;
            var red = new Color(0.85f, 0.22f, 0.22f);
            float arm = s * 0.24f, thick = 3.0f;

            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float circleA = Mathf.Clamp01(r - Vector2.Distance(p, center) + 0.5f); // soft edge

                    float u = p.x - center.x, v = p.y - center.y;
                    float xA = 0f;
                    if (Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) <= arm)
                    {
                        float dToX = Mathf.Min(Mathf.Abs(u - v), Mathf.Abs(u + v)) / 1.41421f;
                        xA = Mathf.Clamp01(thick - dToX + 0.5f); // soft white X
                    }

                    var col = Color.Lerp(red, Color.white, xA);
                    col.a = circleA;
                    px[y * s + x] = col;
                }

            t.SetPixels(px);
            t.Apply();
            _deleteSprite = Sprite.Create(t, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f));
            _deleteSprite.hideFlags = HideFlags.HideAndDontSave;
            return _deleteSprite;
        }

        // Ask for confirmation before deleting (uses the game's native Yes/No prompt).
        private void DeleteCustomTattoo(string name)
        {
            try
            {
                if (DevMode) _devDeleteRequests++;
                DevEvent("delete requested " + name);
                UI.Get<UIPrompt>().ShowYesNo(
                    "Delete custom tattoo",
                    $"Delete \"{name}\" and remove its PNG file? This cannot be undone.",
                    () => DoDeleteCustomTattoo(name), false, null);
            }
            catch (Exception e)
            {
                // If the prompt isn't available for some reason, fall back to deleting.
                Log.LogWarning("[delete] prompt failed, deleting directly: " + e.Message);
                DoDeleteCustomTattoo(name);
            }
        }

        // Actually delete: unequip it, remove the catalog item, delete the files, refresh.
        private void DoDeleteCustomTattoo(string name)
        {
            try
            {
                ulong equipGuid = Hash(name + "|equip");
                ulong texGuid = Hash(name + "|tex");
                ulong iconGuid = Hash(name + "|icon");

                // Unequip from whichever character is being edited.
                foreach (var list in UnityEngine.Object.FindObjectsOfType<UICharacterCreatorContextualList>())
                {
                    var player = list.UICharacterCreator?.PlayerOwner?.Player;
                    if (player == null) continue;
                    var ch = AssetManager.Instance.GetCharacter(player.GetSelectedCharacterGUID());
                    ch?.GetCurrentCharacterModel()?.CurrentEquipment?.RemoveAll(e => e.EquipmentGUID == equipGuid);
                }

                // Remove the catalog item.
                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems != null)
                {
                    eq.EquipmentItems = eq.EquipmentItems.Where(e => e == null || e.GUID != equipGuid).ToArray();
                    typeof(Equipment).GetMethod("RefreshDictionary",
                        BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(eq, null);
                }

                // Stop tracking + delete the source files (incl. the generated mask).
                _texPaths.Remove(texGuid);
                _texPaths.Remove(iconGuid);
                _recolorTexGuids.Remove(texGuid);
                if (_blackTextures.TryGetValue(texGuid, out var blk) && blk != null)
                    UnityEngine.Object.Destroy(blk);
                _blackTextures.Remove(texGuid);
                TryDelete(Path.Combine(Plugin.TattooFolder, name + ".png"));
                TryDelete(Path.Combine(Plugin.TattooFolder, "_icons", name + ".png"));
                TryDelete(Path.Combine(Plugin.TattooFolder, "_icons", name + ".mask.png"));

                RequestRefresh();
                DevEvent("deleted " + name);
                Log.LogInfo("[delete] removed " + name);
            }
            catch (Exception e)
            {
                DevEvent("delete error: " + e.Message);
                Log.LogError("[delete] " + e);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Log.LogWarning("[delete] could not delete " + path + ": " + e.Message); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        private static string OpenPngDialog()
        {
            var ofn = new OpenFileName();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.lpstrFilter = "PNG images\0*.png\0All files\0*.*\0\0";
            ofn.lpstrFile = new string('\0', 1024);
            ofn.nMaxFile = 1024;
            ofn.lpstrFileTitle = new string('\0', 256);
            ofn.nMaxFileTitle = 256;
            ofn.lpstrInitialDir = Plugin.TattooFolder;
            ofn.lpstrTitle = "Choose a PNG tattoo";
            // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            ofn.Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;
            return GetOpenFileNameW(ref ofn) ? ofn.lpstrFile.TrimEnd('\0') : null;
        }

        // Ensure a pure-black swatch exists in the tattoo group (the vanilla palette's
        // darkest is only a dark gray). Injected once per group; returns its GUID.
        private static ulong EnsureBlackSwatch(ulong group)
        {
            if (group == 0uL) return 0uL;
            var settings = SwatchManager.SwatchSettings;
            if (settings?.AllSwatches == null) return 0uL;

            ulong guid = Hash("__ink_black_swatch__" + group);
            if (settings.AllSwatches.Any(s => s != null && s.GUID == guid)) return guid;

            var tmpl = settings.AllSwatches.FirstOrDefault(s => s != null && s.SwatchGroup == group);
            if (tmpl == null) return 0uL;

            var black = ShallowClone(tmpl);
            black.GUID = guid;
            // Deep-copy the colors so we don't recolor the template, then force black.
            if (tmpl.SwatchColors != null && tmpl.SwatchColors.Length > 0)
                black.SwatchColors = tmpl.SwatchColors
                    .Select(sc => { var n = ShallowClone(sc); n.PaletteColor = 0uL; n.Color = Color.black; return n; })
                    .ToArray();
            else
                black.SwatchColors = new[] { new SwatchColor { Color = Color.black } };

            settings.AllSwatches = settings.AllSwatches.Concat(new[] { black }).ToArray();
            Log.LogInfo("[recolor] injected pure-black swatch for group " + group);
            return guid;
        }

        // Heuristic: does the image have multiple distinct hues (true color) → keep locked,
        // vs grayscale / single-hue → recolorable via the swatch UI?
        private static bool IsColorful(Texture2D tex)
        {
            Color32[] px;
            try { px = tex.GetPixels32(); }
            catch { return true; } // unreadable → safe default = locked full-color (current behavior)

            int step = Mathf.Max(1, px.Length / 4096); // sample up to ~4k pixels
            int opaque = 0, saturated = 0;
            var hueBins = new int[12];
            for (int i = 0; i < px.Length; i += step)
            {
                var c = px[i];
                if (c.a < 25) continue;
                opaque++;
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                float max = Mathf.Max(r, Mathf.Max(g, b));
                float min = Mathf.Min(r, Mathf.Min(g, b));
                float sat = max <= 0.0001f ? 0f : (max - min) / max;
                if (sat < 0.18f) continue; // near-gray pixel
                saturated++;
                Color.RGBToHSV(new Color(r, g, b), out float hue, out _, out _);
                hueBins[Mathf.Clamp((int)(hue * 12f), 0, 11)]++;
            }

            if (opaque == 0) return false;
            if ((float)saturated / opaque < 0.12f) return false; // mostly grayscale → recolorable

            int significantHues = 0;
            foreach (var count in hueBins)
                if (count > saturated * 0.10f) significantHues++;
            return significantHues > 1; // many hues → colorful; single hue → recolorable
        }

        // Stable 64-bit FNV-1a hash so the same file keeps the same GUID across runs.
        private static ulong Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            return h;
        }

        // Copy all instance fields into a new instance of the same type.
        private static T ShallowClone<T>(T src) where T : class
        {
            var t = src.GetType();
            var dst = (T)Activator.CreateInstance(t);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                f.SetValue(dst, f.GetValue(src));
            return dst;
        }

    }

    // Intercept a click on our "+" catalog tile: open the picker instead of equipping.
    [HarmonyPatch(typeof(UIEquipmentItem), "OnListItemClicked")]
    internal static class AddTileClickPatch
    {
        private static bool Prefix(UIEquipmentItem __instance)
        {
            if (__instance != null && __instance.Equipment != null &&
                __instance.Equipment.GUID == Runner.AddButtonGuid)
            {
                Runner.Instance?.RecordAddTileClick();
                Runner.Instance?.PickAndAddPng();
                return false; // skip the normal equip behaviour
            }
            return true;
        }
    }

    // Add a delete "x" to our custom tattoo tiles (and hide it on other tiles, since
    // tiles are pooled and reused).
    [HarmonyPatch(typeof(UIEquipmentItem), "Init")]
    internal static class TileInitPatch
    {
        private static void Postfix(UIEquipmentItem __instance, EquipmentItem equipment)
        {
            Runner.Instance?.DecorateTile(__instance, equipment);
        }
    }

    // Our main event hook: fires whenever the Paramaker catalog refreshes (only while in
    // character customization). Drives injection + texture upkeep, so we never poll the
    // whole game from Update.
    [HarmonyPatch(typeof(UICharacterCreatorContextualList), "RefreshListContent")]
    internal static class CatalogRefreshPatch
    {
        private static void Prefix(UICharacterCreatorContextualList __instance)
        {
            Runner.Instance?.OnCatalogRefreshing(__instance);
        }
    }

    // Lightweight: just before the skin is composited, force true #000 black on our recolor
    // decals whose swatch is black (GrayMask alone can't reach pure black).
    [HarmonyPatch(typeof(SkinLayeringManager), "RequestSkinRender")]
    internal static class BlackOverridePatch
    {
        private static void Prefix(SkinLayer[] skinLayers)
        {
            Runner.Instance?.ApplyBlackOverride(skinLayers);
        }
    }
}
