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

namespace InkAnywhere
{
    [BepInPlugin(Guid, "Ink Anywhere", "0.1.0")]
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

        private void Awake() => Instance = this;

        private bool _updateSeen;
        private bool _dumped;
        private float _nextCheck;

        // Results for the status panel.
        private int _loaded;
        private readonly List<string> _failures = new List<string>();
        private bool _panelOpen = true;

        // True while the Paramaker tattoo (decal) section is open — shows the + button.
        private bool _inTattooSection;
        private float _nextSectionCheck;

        // Watchdog: the game keeps unloading our texture and its own reload returns
        // null, which NREs the skin compositor. We track each texture GUID -> source
        // PNG path and re-load it ourselves whenever the asset's texture goes null.
        private static readonly FieldInfo TexField =
            typeof(AssetTexture).GetField("_texture", BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly Dictionary<ulong, string> _texPaths = new Dictionary<ulong, string>();

        private void Update()
        {
            if (!_updateSeen)
            {
                _updateSeen = true;
                Log.LogInfo("Runner.Update is ticking!");
            }

            // Once the equipment data is loaded, auto-inject all PNGs from the folder.
            if (!_dumped && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 1f;
                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems != null && eq.EquipmentItems.Length > 0)
                {
                    _dumped = true;
                    Inject();
                }
            }

            // Keep our textures alive every frame so the skin compositor never NREs.
            if (_texPaths.Count > 0)
                foreach (var guid in _texPaths.Keys)
                    EnsureTexture(guid);

            // Detect whether we're in the tattoo section (a few times a second).
            if (Time.unscaledTime >= _nextSectionCheck)
            {
                _nextSectionCheck = Time.unscaledTime + 0.25f;
                _inTattooSection = false;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<UICharacterCreatorContextualList>())
                    if (l != null && l.isActiveAndEnabled && l.UIDecalPositions != null) { _inTattooSection = true; break; }
            }
        }

        // Reload our PNG into the asset whenever its texture is missing/destroyed.
        private void EnsureTexture(ulong texGuid)
        {
            var asset = AssetManager.Instance.GetAssetOfType<AssetTexture>(texGuid);
            if (asset == null || TexField == null) return;

            var current = TexField.GetValue(asset) as Texture2D;
            if (current != null) return; // still valid, nothing to do

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
            asset.IsLoaded = true;
        }

        // Flip to true to bring back the in-game debug panel (status + failures + buttons).
        private static readonly bool ShowDebugUI = false;

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

            if (!ShowDebugUI) return;

            // Collapsed: a small button in the corner to reopen the panel.
            if (!_panelOpen)
            {
                if (GUI.Button(new Rect(10, 10, 130, 26), "Ink Anywhere"))
                    _panelOpen = true;
                return;
            }

            bool hasErrors = _failures.Count > 0;
            float h = 92 + (hasErrors ? Mathf.Min(_failures.Count, 6) * 20 + 24 : 0);
            GUI.Box(new Rect(10, 10, 380, h), "Ink Anywhere");

            GUI.Label(new Rect(20, 34, 360, 22),
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

            if (GUI.Button(new Rect(20, y, 120, 28), "Refresh"))
                Inject();
            if (GUI.Button(new Rect(148, y, 130, 28), "Open folder"))
                Application.OpenURL("file://" + Plugin.TattooFolder);
            if (GUI.Button(new Rect(286, y, 84, 28), "Hide"))
                _panelOpen = false;
        }

        // ---- Turn every PNG in the folder into a tattoo in the catalog ----
        private void Inject()
        {
            try
            {
                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems == null) return;

                // Template = an existing tattoo we clone (reuses its tags/swatch/decal sections).
                var template = eq.EquipmentItems.FirstOrDefault(e => e != null && e.IsDecal);
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
                    try
                    {
                        // Validate the image actually decodes before doing anything.
                        var probe = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                        bool ok = probe.LoadImage(File.ReadAllBytes(path)) && probe.width > 2 && probe.height > 2;
                        UnityEngine.Object.Destroy(probe);
                        if (!ok) { _failures.Add(name + " — not a valid image"); continue; }

                        ulong texGuid = Hash(name + "|tex");
                        ulong equipGuid = Hash(name + "|equip");
                        ulong iconGuid = Hash(name + "|icon");

                        if (eq.EquipmentItems.Any(e => e != null && e.GUID == equipGuid)) { _loaded++; continue; }

                        // Texture (kept alive by the watchdog).
                        if (AssetManager.Instance.GetAssetOfType<AssetTexture>(texGuid) == null)
                        {
                            var asset = AssetManager.Instance.RegisterAsset(path, texGuid, false, 0uL) as AssetTexture;
                            if (asset == null) { _failures.Add(name + " — texture register failed"); continue; }
                            asset.IsClampTextureWrapMode = true;
                        }
                        _texPaths[texGuid] = path;
                        EnsureTexture(texGuid);

                        // Tidy square thumbnail (regenerated each run so it self-heals).
                        string iconPath = Path.Combine(iconDir, name + ".png");
                        MakeSquareIcon(path, iconPath);
                        if (AssetManager.Instance.GetAssetOfType<AssetTexture>(iconGuid) == null)
                        {
                            var ia = AssetManager.Instance.RegisterAsset(iconPath, iconGuid, false, 0uL) as AssetTexture;
                            if (ia != null) ia.IsClampTextureWrapMode = true;
                        }
                        _texPaths[iconGuid] = iconPath;
                        EnsureTexture(iconGuid);

                        // Clone a real tattoo and repoint it at our texture, full-color.
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
                            et.ShaderType = ShaderType.NonRecolorable;
                            item.Textures = new[] { et };
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
                    MakePlusIcon(plusPath);
                    ulong plusIcon = Hash("__add__|icon");
                    if (AssetManager.Instance.GetAssetOfType<AssetTexture>(plusIcon) == null)
                    {
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

                // Force the catalog UI to rebuild on its next frame.
                foreach (var list in UnityEngine.Object.FindObjectsOfType<UICharacterCreatorContextualList>())
                    typeof(UICharacterCreatorContextualList)
                        .GetField("_lastTagsHash", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(list, 0uL);

                Log.LogInfo($"[inject] done: loaded={_loaded}, new={newlyAdded}, failed={_failures.Count}");
            }
            catch (Exception e) { Log.LogError("[inject] fatal " + e); }
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
        internal void PickAndAddPng()
        {
            try
            {
                string file = OpenPngDialog();
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;

                string dest = Path.Combine(Plugin.TattooFolder, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, dest, true);

                Inject();
                Log.LogInfo("[add] imported " + Path.GetFileName(file));
            }
            catch (Exception e) { Log.LogError("[add] " + e); }
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
                Runner.Instance?.PickAndAddPng();
                return false; // skip the normal equip behaviour
            }
            return true;
        }
    }
}
