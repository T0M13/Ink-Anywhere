using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
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

        private bool _updateSeen;
        private bool _dumped;
        private float _nextCheck;
        private string _status = "starting...";

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

        private void OnGUI()
        {
            GUI.color = Color.white;
            GUI.Box(new Rect(10, 10, 360, 126), "Ink Anywhere — mod is running");
            GUI.Label(new Rect(18, 36, 344, 22), _status);

            if (GUI.Button(new Rect(18, 62, 170, 30), "Scan + Dump"))
            {
                Scan();
                DumpTattoos();
            }
            if (GUI.Button(new Rect(196, 62, 166, 30), "Open folder"))
                Application.OpenURL("file://" + Plugin.TattooFolder);

            if (GUI.Button(new Rect(18, 96, 344, 30), "Inject PNGs as tattoos"))
                Inject();
        }

        // ---- Phase 1: turn each PNG into a real tattoo in the catalog ----
        private void Inject()
        {
            try
            {
                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems == null) { _status = "equipment not loaded"; return; }

                // Template = an existing tattoo we clone (reuses its tags/swatch/decal sections/shader).
                var template = eq.EquipmentItems.FirstOrDefault(e => e != null && e.IsDecal);
                if (template == null) { _status = "no template tattoo found"; return; }
                Log.LogInfo($"[inject] using template '{template.DisplayName}' model={template.CharacterModelGUID}");

                var pngs = Directory.GetFiles(Plugin.TattooFolder, "*.png");
                int added = 0;
                foreach (var path in pngs)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    ulong texGuid = Hash(name + "|tex");
                    ulong equipGuid = Hash(name + "|equip");

                    if (eq.EquipmentItems.Any(e => e != null && e.GUID == equipGuid)) { continue; } // already added

                    // 1) Register the PNG as a texture asset, then hand it to the
                    //    watchdog which loads + keeps it alive (the game's own reload
                    //    path returns null and NREs the compositor).
                    if (AssetManager.Instance.GetAssetOfType<AssetTexture>(texGuid) == null)
                    {
                        var asset = AssetManager.Instance.RegisterAsset(path, texGuid, false, 0uL) as AssetTexture;
                        if (asset == null) { Log.LogWarning("[inject] not a texture asset: " + name); continue; }
                        asset.IsClampTextureWrapMode = true;
                    }
                    _texPaths[texGuid] = path;
                    EnsureTexture(texGuid);
                    Log.LogInfo($"[inject] texture '{name}' ready (guid={texGuid})");

                    // 2) Clone the template tattoo and repoint it at our texture, full-color.
                    var item = ShallowClone(template);
                    item.GUID = equipGuid;
                    item.DisplayName = "Ink: " + name;
                    item.VisibleInCatalog = true;
                    item.TextureIconGUID = texGuid;
                    if (template.Textures != null && template.Textures.Length > 0)
                    {
                        var et = ShallowClone(template.Textures[0]);
                        et.TextureGUID = texGuid;
                        et.MaskTextureGUID = 0uL;                 // no recolor mask
                        et.ShaderType = ShaderType.NonRecolorable; // show the PNG's own colors
                        item.Textures = new[] { et };
                    }

                    eq.EquipmentItems = eq.EquipmentItems.Concat(new[] { item }).ToArray();
                    added++;
                    Log.LogInfo($"[inject] added '{item.DisplayName}' equipGUID={equipGuid} texGUID={texGuid}");
                }

                // Rebuild the GUID lookup so the item can be equipped/saved.
                typeof(Equipment).GetMethod("RefreshDictionary",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(eq, null);

                // Force the catalog UI to rebuild on its next frame.
                foreach (var list in UnityEngine.Object.FindObjectsOfType<UICharacterCreatorContextualList>())
                    typeof(UICharacterCreatorContextualList)
                        .GetField("_lastTagsHash", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(list, 0uL);

                _status = $"injected {added} tattoo(s) — open the Tattoo category";
                Log.LogInfo($"[inject] done, added {added}");
            }
            catch (Exception e) { Log.LogError("[inject] " + e); _status = "inject error (see console)"; }
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

        private void Scan()
        {
            try
            {
                var pngs = Directory.GetFiles(Plugin.TattooFolder, "*.png");
                Log.LogInfo($"[scan] {pngs.Length} PNG(s)");
                foreach (var path in pngs)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(File.ReadAllBytes(path)))
                    {
                        Log.LogWarning("[scan] could not decode " + Path.GetFileName(path));
                        continue;
                    }
                    Log.LogInfo($"[scan] OK {Path.GetFileName(path)} {tex.width}x{tex.height} alpha={HasAlpha(tex)}");
                }
            }
            catch (Exception e) { Log.LogError("[scan] " + e); }
        }

        private void DumpTattoos()
        {
            try
            {
                var eq = Settings.Get<Equipment>();
                if (eq?.EquipmentItems == null) { Log.LogWarning("[dump] Equipment not ready"); _status = "equipment not loaded yet"; return; }

                var decals = eq.EquipmentItems.Where(e => e != null && e.IsDecal).ToList();
                Log.LogInfo($"[dump] total equipment={eq.EquipmentItems.Length}, decal/tattoo items={decals.Count}");
                _status = $"equipment={eq.EquipmentItems.Length}, tattoos={decals.Count}";

                foreach (var e in decals.Take(3))
                {
                    Log.LogInfo($"[dump] --- '{e.DisplayName}' GUID={e.GUID} model={e.CharacterModelGUID} visibleInCatalog={e.VisibleInCatalog}");
                    if (e.Tags != null)
                        foreach (var t in e.Tags) Log.LogInfo($"[dump]    tag={t}");
                    if (e.Textures != null)
                        foreach (var tx in e.Textures) Log.LogInfo($"[dump]    texGUID={tx.TextureGUID} side={tx.Side}");
                    Log.LogInfo($"[dump]    swatchGroup={e.SwatchGroup} defaultSwatch={e.DefaultSwatch} canChangeOpacity={e.CanChangeOpacity}");
                    Log.LogInfo($"[dump]    decalSectionData count={(e.DecalSectionData?.Length ?? 0)} iconGUID={e.TextureIconGUID}");
                }

                var models = eq.EquipmentItems.Select(e => e.CharacterModelGUID).Distinct().Take(10);
                Log.LogInfo($"[dump] character model GUIDs: {string.Join(", ", models)}");
            }
            catch (Exception ex) { Log.LogError("[dump] " + ex); }
        }

        private static bool HasAlpha(Texture2D tex)
        {
            foreach (var p in tex.GetPixels32())
                if (p.a < 255) return true;
            return false;
        }
    }
}
