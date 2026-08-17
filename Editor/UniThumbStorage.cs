using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Scene-to-thumbnail mapping keyed by scene GUID. Thumbnails are PNG files named
    /// {sceneGuid}.png under the ACTIVE storage folder, resolved from
    /// UniThumbSettings at call time (never cached statically). LibraryCache mode:
    /// Library/SceneThumbnails/ - outside the project, like Unity prefab previews,
    /// no .meta files, no AssetDatabase import, no VCS churn. TrackedInAssets mode:
    /// Assets/UniThumb/Thumbnails/ - inside Assets; the folder is imported via one
    /// AssetDatabase.Refresh on creation and PNGs get .meta files (gitignore them).
    /// GUID keys survive scene renames.
    ///
    /// Invalidation: per-scene EditorPrefs key SceneThumbs.v4.{guid} stores
    /// File.GetLastWriteTimeUtc(scenePath).Ticks recorded at save time (long does not
    /// fit EditorPrefs.SetInt, so it is stored as an invariant-culture string). Load
    /// compares the live scene file ticks against the cached record and reloads from
    /// disk when stale. Load never regenerates: this tool is strictly manual-only.
    ///
    /// Cache ownership: the cache owns every Texture2D it returns (runtime textures,
    /// HideAndDontSave). DestroyImmediate happens on eviction (Save/Delete/staleness),
    /// on size-cap LRU eviction (t11: 256MB cap, least-recently-touched first) and on
    /// the icon-service clear. Callers must never destroy textures from Load.
    ///
    /// UI-agnostic: no EditorWindow or GUI references. All entry points are
    /// user-triggered (window button, context menus, batch). Passive storage.
    /// </summary>
    public static class UniThumbStorage
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";
        private const string k_StorageFolderName = "SceneThumbnails";
        private const string k_TrackedParentFolderName = "UniThumb";
        private const string k_TrackedFolderName = "Thumbnails";
        private const string k_PrefsPrefix = "SceneThumbs.v4";

        // t11 size cap: 512px thumbnails are ~1MB each, 4096px ones ~64MB each.
        // Insertion evicts least-recently-touched entries while over the cap.
        private const int k_MaxCacheBytes = 256 * 1024 * 1024;

        // MIGRATION NOTE: bumped v1 -> v2 when the capture-fidelity defaults changed
        // (t8-capture-fidelity: backgroundMode=Skybox + wantPostProcessing=true).
        // The bump makes every existing thumbnail look stale, so Refresh All
        // regenerates them with the new defaults. Future default changes that alter
        // thumbnail look should bump the prefix again. When the key schema changes,
        // add a migration method that reads old keys, converts values, writes them
        // under the new prefix, then deletes the old keys. Never reuse a prefix with
        // a new schema. UniThumbBatchMenus mirrors this prefix for its
        // Refresh-All staleness check; keep both in sync on migration.
        // Bumped v2 -> v3 when CaptureUi defaulted to true (UI canvases now render
        // into thumbnails), so existing thumbnails regenerate with UI included.
        // Bumped v3 -> v4 when the UI composite feature changed the capture default
        // look (UI rendered at SceneView aspect when CaptureUi + UseSceneViewAngle
        // are both on), so existing thumbnails regenerate with the new layout.

        #endregion

        #region Fields

        /// <summary>
        /// Cache value: the owned texture plus LRU/size-cap bookkeeping. Ticks are
        /// the scene LastWriteTimeUtc recorded at cache time (staleness check).
        /// TouchStamp is a monotonically increasing LRU sequence, re-stamped on
        /// TryGetCachedTexture hits. ByteSize is width * height * 4, valid only
        /// after LoadImage succeeds (dimensions are known then).
        /// </summary>
        private struct CacheEntry
        {
            public Texture2D Texture;
            public long Ticks;
            public uint TouchStamp;
            public int ByteSize;
        }

        private static readonly Dictionary<string, CacheEntry> s_Cache =
            new Dictionary<string, CacheEntry>();

        /// <summary>
        /// Mutable cache bookkeeping (LRU sequence, byte accounting, eviction
        /// callback registry) on a readonly holder so the class keeps zero static
        /// mutable fields and zero static events (Asset Store Validator
        /// "Check Static Variables"). Domain-reload semantics unchanged: a fresh
        /// holder is created on reload, exactly like the old statics.
        /// </summary>
        private static readonly StorageState s_State = new StorageState();

        private sealed class StorageState
        {
            public uint TouchCounter;
            public int TotalBytes;
            public Action<string> TextureEvicted;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Registers a callback invoked with the scene GUID whenever the size cap
        /// evicts an entry (LRU). The icon service re-enqueues the GUID for lazy
        /// re-warm, so evicted thumbnails reappear within a few frames. Not fired
        /// by Delete or ClearCache - those paths intentionally drop the entry for
        /// good. Unregister with UnregisterEviction to avoid leaks.
        /// </summary>
        public static void RegisterEviction(Action<string> callback)
        {
            s_State.TextureEvicted += callback;
        }

        /// <summary>
        /// Removes a callback previously registered via RegisterEviction.
        /// </summary>
        public static void UnregisterEviction(Action<string> callback)
        {
            s_State.TextureEvicted -= callback;
        }

        /// <summary>
        /// Destroys every cached texture and empties the cache, resetting the LRU
        /// sequence and the byte accounting. Idempotent: destroying an
        /// already-destroyed texture is a no-op. Called by UniThumbIconService
        /// initialization - the sole domain-reload init (t11/M20): the icon service
        /// clears here, then rebuilds and enqueues the warm queue.
        /// </summary>
        public static void ClearCache()
        {
            foreach (CacheEntry entry in s_Cache.Values)
            {
                if (entry.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.Texture);
                }
            }
            s_Cache.Clear();
            s_State.TouchCounter = 0;
            s_State.TotalBytes = 0;
        }

        /// <summary>
        /// Creates the ACTIVE mode folder (with parents) if missing and returns its
        /// absolute path: Library/SceneThumbnails/ in LibraryCache mode,
        /// Assets/UniThumb/Thumbnails/ in TrackedInAssets mode. Library mode is
        /// System.IO only (Directory.CreateDirectory): no .meta files, the folder
        /// never registers with the asset pipeline. Tracked mode runs exactly one
        /// AssetDatabase.Refresh right after creating a NEW folder so it imports -
        /// never per file, only once per folder creation. The folder is owned by
        /// storage: callers must never override it.
        /// </summary>
        public static string EnsureFolder()
        {
            string folder = ActiveFolderPath();
            bool existed = Directory.Exists(folder);
            Directory.CreateDirectory(folder);
            if (!existed && UniThumbSettings.Get().StorageMode == StorageMode.TrackedInAssets)
            {
                AssetDatabase.Refresh();
            }
            return folder;
        }

        /// <summary>
        /// Writes {sceneGuid}.png under the ACTIVE storage folder (see ActiveFolderPath:
        /// Library/SceneThumbnails/ or Assets/UniThumb/Thumbnails/), records the scene
        /// LastWriteTimeUtc ticks in EditorPrefs (invalidation record) and warms the
        /// in-memory cache. File.WriteAllBytes only: no per-file AssetDatabase.ImportAsset,
        /// no AssetDatabase.Refresh, no TextureImporter. Library mode writes no .meta
        /// files; tracked-mode .meta files appear at the next Refresh (batch teardown or
        /// window-orchestrated). Refuses empty GUIDs: {}.png is never written.
        /// </summary>
        public static bool Save(string scenePath, byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0)
            {
                Debug.LogWarning(k_LogPrefix + "Save refused: PNG byte buffer is null or empty.");
                return false;
            }

            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Save refused for '"
                        + scenePath
                        + "': AssetPathToGUID returned an empty GUID (scene is new or not imported). No {}.png written."
                );
                return false;
            }

            Evict(guid);
            string pngPath = PngPathFor(guid);
            try
            {
                EnsureFolder();
                File.WriteAllBytes(pngPath, pngBytes);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }

            long sceneTicks = File.GetLastWriteTimeUtc(AbsoluteProjectPath(scenePath)).Ticks;
            WritePrefsTicks(guid, sceneTicks);

            Texture2D texture = CreateTextureFromBytes(pngBytes);
            if (texture != null)
            {
                InsertIntoCache(guid, texture, sceneTicks);
            }
            return true;
        }

        /// <summary>
        /// Returns the cached Texture2D reference (never a copy; owned by the cache).
        /// On a cache hit only a metadata stat of the scene file runs to detect
        /// staleness - no PNG read, no EditorPrefs read. A stale scene file
        /// (LastWriteTimeUtc changed since the cache record) evicts and reloads from
        /// disk. Returns null when the thumbnail file is missing (Library cleanup).
        /// Not for use inside GUI repaint callbacks; those must consume
        /// TryGetCachedTexture only.
        /// </summary>
        public static Texture2D Load(string scenePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Load refused for '"
                        + scenePath
                        + "': AssetPathToGUID returned an empty GUID (scene is new or not imported)."
                );
                return null;
            }

            long sceneTicks = File.GetLastWriteTimeUtc(AbsoluteProjectPath(scenePath)).Ticks;
            CacheEntry entry;
            if (s_Cache.TryGetValue(guid, out entry) && entry.Texture != null)
            {
                if (sceneTicks == entry.Ticks)
                {
                    return entry.Texture;
                }
                Evict(guid);
            }

            long storedTicks = ReadPrefsTicks(guid);
            if (storedTicks != 0L && storedTicks != sceneTicks)
            {
                // A prefs record exists but no longer matches the scene file: the
                // scene was modified after the last save. Reload from disk and keep
                // the prefs record untouched - it stays the "outdated" signal for
                // Refresh All until the user re-saves. Manual-only tool: never
                // regenerate here.
                Debug.Log(
                    k_LogPrefix
                        + "Thumbnail for '"
                        + scenePath
                        + "' is stale (scene modified after save); reloaded from disk."
                );
            }

            return LoadFromDisk(guid, sceneTicks);
        }

        /// <summary>
        /// Warms the cache for a known scene GUID without an assets-relative path
        /// (init-time sibling of Load). Resolves the scene path via
        /// AssetDatabase.GUIDToAssetPath and reuses Load's staleness rules: a cache
        /// hit with a matching scene LastWriteTimeUtc stays, a stale entry is evicted
        /// and reloaded, and the PNG read/import itself happens in LoadFromDisk - no
        /// duplicated read code. Returns false silently when the GUID has no scene
        /// (deleted scene or orphaned Library PNG): no warning, no cache entry.
        /// </summary>
        public static bool LoadByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }
            string scenePath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(scenePath))
            {
                return false;
            }
            long sceneTicks = File.GetLastWriteTimeUtc(AbsoluteProjectPath(scenePath)).Ticks;
            CacheEntry entry;
            if (s_Cache.TryGetValue(guid, out entry) && entry.Texture != null)
            {
                if (sceneTicks == entry.Ticks)
                {
                    return true;
                }
                Evict(guid);
            }
            return LoadFromDisk(guid, sceneTicks) != null;
        }

        /// <summary>
        /// Cache-only lookup for overlay callbacks: zero file I/O, zero EditorPrefs
        /// reads, zero allocations, no staleness checks. Returns the cached instance
        /// for a known GUID or null for an unknown one. A hit re-stamps the entry
        /// for LRU (uint increment + struct write-back). Safe to call from GUI
        /// repaint callbacks (per-frame hot path).
        /// </summary>
        public static bool TryGetCachedTexture(string guid, out Texture2D texture)
        {
            CacheEntry entry;
            if (s_Cache.TryGetValue(guid, out entry))
            {
                texture = entry.Texture;
                if (texture != null)
                {
                    // LRU touch: uint increment + dictionary value write-back only.
                    // No allocations, no LinkedList - safe in the repaint path.
                    entry.TouchStamp = ++s_State.TouchCounter;
                    s_Cache[guid] = entry;
                }
                return texture != null;
            }
            texture = null;
            return false;
        }

        /// <summary>
        /// Removes the thumbnail PNG and the EditorPrefs invalidation key together.
        /// The cached texture is dropped first (cache removal, then
        /// DestroyImmediate); callers repaint after the call. Library mode deletes
        /// the raw file (File.Delete only, no AssetDatabase involvement). Tracked
        /// mode uses AssetDatabase.DeleteAsset so the PNG and its .meta are removed
        /// cleanly, with a raw File.Delete of png + .meta fallback when the asset
        /// path is invalid (not yet imported). Never calls AssetDatabase.Refresh.
        /// Returns true when the PNG file was deleted.
        /// </summary>
        public static bool Delete(string scenePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Delete refused for '"
                        + scenePath
                        + "': AssetPathToGUID returned an empty GUID (scene is new or not imported)."
                );
                return false;
            }

            Evict(guid);
            EditorPrefs.DeleteKey(KeyFor(guid));
            string pngPath = PngPathFor(guid);
            if (!File.Exists(pngPath))
            {
                return false;
            }
            if (UniThumbSettings.Get().StorageMode == StorageMode.TrackedInAssets)
            {
                return DeleteTrackedPng(pngPath);
            }
            File.Delete(pngPath);
            return true;
        }

        /// <summary>
        /// True when a thumbnail mapping exists for the scene (cache first, then a
        /// disk existence check). Safe for menu validation; icon repaint callbacks
        /// must use TryGetCachedTexture.
        /// </summary>
        public static bool HasThumbnail(string scenePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }
            CacheEntry entry;
            if (s_Cache.TryGetValue(guid, out entry) && entry.Texture != null)
            {
                return true;
            }
            return File.Exists(PngPathFor(guid));
        }

        /// <summary>
        /// Scans the ACTIVE mode storage folder for existing thumbnail PNGs and
        /// returns their scene GUIDs (PNG file names ARE scene GUIDs, the storage
        /// naming contract). Directory.GetFiles only - never AssetDatabase.FindAssets,
        /// keeping the scan symmetric across modes (Library mode is outside the
        /// asset pipeline; tracked mode reads raw disk rather than the asset DB).
        /// </summary>
        public static string[] EnumerateThumbnailGuids()
        {
            string folder = ActiveFolderPath();
            if (!Directory.Exists(folder))
            {
                return Array.Empty<string>();
            }
            string[] files = Directory.GetFiles(folder, "*.png", SearchOption.TopDirectoryOnly);
            string[] guids = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                guids[i] = Path.GetFileNameWithoutExtension(files[i]);
            }
            return guids;
        }

        /// <summary>
        /// Number of PNG thumbnails in the ACTIVE mode storage folder
        /// (TopDirectoryOnly; same scan as EnumerateThumbnailGuids). Returns 0
        /// when the folder does not exist yet. Used by the window's storage-mode
        /// switch to decide whether the move-migration dialog is needed.
        /// </summary>
        public static int CountThumbnails()
        {
            string folder = ActiveFolderPath();
            if (!Directory.Exists(folder))
            {
                return 0;
            }
            return Directory.GetFiles(folder, "*.png", SearchOption.TopDirectoryOnly).Length;
        }

        /// <summary>
        /// Absolute path of the folder used by the ACTIVE storage mode:
        /// Library/SceneThumbnails/ (LibraryCache) or Assets/UniThumb/Thumbnails/
        /// (TrackedInAssets). Resolved at call time from UniThumbSettings - never
        /// cached statically. Does not create the folder; display-only (the window
        /// footer shows it).
        /// </summary>
        public static string ActiveStorageFolderPath()
        {
            return ActiveFolderPath();
        }

        /// <summary>
        /// Absolute path of the folder a given storage mode uses, regardless of
        /// the active mode. Display-only: does not create the folder. Used by the
        /// window for the migration dialog, the result status, and the footer.
        /// </summary>
        public static string StorageFolderPath(StorageMode mode)
        {
            return FolderPathForMode(mode);
        }

        /// <summary>
        /// Moves every thumbnail PNG in the current active folder into the target
        /// mode's folder. Files that already exist in the target are skipped
        /// (overwrite:false), so re-runs are idempotent and the returned count is
        /// the number actually moved. Each source file is deleted only after its
        /// copy succeeded; when the source is the tracked (Assets) folder the
        /// moved PNG's orphaned .meta is deleted too, so no stale sidecar is
        /// left for a later AssetDatabase.Refresh to clean. The target folder is
        /// created up front (even for zero files); the source folder is removed
        /// when it ends up empty, together with the tracked folder's own .meta
        /// so Refresh does not recreate the folder (Unity warns on a .meta that
        /// has no folder). The storage mode is NOT switched and no
        /// AssetDatabase.Refresh is issued - the caller (window) orchestrates
        /// the refresh and the mode switch after the move.
        /// </summary>
        public static int MoveThumbnailsTo(StorageMode targetMode)
        {
            string targetFolder = FolderPathForMode(targetMode);
            Directory.CreateDirectory(targetFolder);
            string sourceFolder = ActiveFolderPath();
            if (!Directory.Exists(sourceFolder))
            {
                return 0;
            }
            bool sourceIsTracked = FolderPathForMode(StorageMode.TrackedInAssets) == sourceFolder;
            string[] files = Directory.GetFiles(
                sourceFolder,
                "*.png",
                SearchOption.TopDirectoryOnly
            );
            int moved = 0;
            foreach (string file in files)
            {
                string target = Path.Combine(targetFolder, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    continue;
                }
                try
                {
                    File.Copy(file, target, false);
                    File.Delete(file);
                    if (sourceIsTracked)
                    {
                        string meta = file + ".meta";
                        if (File.Exists(meta))
                        {
                            File.Delete(meta);
                        }
                    }
                    moved++;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        k_LogPrefix + "Move failed for '" + file + "': " + exception.Message
                    );
                }
            }
            if (
                Directory.Exists(sourceFolder)
                && Directory.GetFileSystemEntries(sourceFolder).Length == 0
            )
            {
                Directory.Delete(sourceFolder, false);
                if (sourceIsTracked)
                {
                    string folderMeta = sourceFolder + ".meta";
                    if (File.Exists(folderMeta))
                    {
                        File.Delete(folderMeta);
                    }
                }
            }
            return moved;
        }

        /// <summary>
        /// True when the scene file's LastWriteTimeUtc ticks differ from the ticks
        /// in the invalidation record (EditorPrefs key SceneThumbs.v4.{guid}).
        /// False for null/empty paths, unknown GUIDs, or scenes with no record yet
        /// (nothing saved = not stale). Callers: mutation points only (after
        /// Generate / after a batch), never on the UI refresh path.
        /// </summary>
        public static bool IsSceneStale(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return false;
            }
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }
            string raw = EditorPrefs.GetString(KeyFor(guid), string.Empty);
            long storedTicks;
            if (
                !long.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out storedTicks
                )
            )
            {
                return false;
            }
            long liveTicks = File.GetLastWriteTimeUtc(AbsoluteProjectPath(scenePath)).Ticks;
            return storedTicks != liveTicks;
        }

        #endregion

        #region Private Methods

        private static string LibraryFolderPath()
        {
            return Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                "Library",
                k_StorageFolderName
            );
        }

        private static string TrackedFolderPath()
        {
            return Path.Combine(
                Application.dataPath,
                k_TrackedParentFolderName,
                k_TrackedFolderName
            );
        }

        private static string FolderPathForMode(StorageMode mode)
        {
            return mode == StorageMode.TrackedInAssets ? TrackedFolderPath() : LibraryFolderPath();
        }

        private static string ActiveFolderPath()
        {
            return FolderPathForMode(UniThumbSettings.Get().StorageMode);
        }

        private static string PngPathFor(string guid)
        {
            return Path.Combine(ActiveFolderPath(), guid + ".png");
        }

        /// <summary>
        /// Converts an absolute path under Application.dataPath to an assets-relative
        /// path ("Assets/..."), or null when the path is outside Assets. Normalizes
        /// separators first (Path.Combine yields backslashes on Windows).
        /// </summary>
        private static string AssetsRelativePath(string absolutePath)
        {
            string normalized = absolutePath.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath, StringComparison.Ordinal))
            {
                return "Assets" + normalized.Substring(dataPath.Length);
            }
            return null;
        }

        /// <summary>
        /// Tracked-mode delete: AssetDatabase.DeleteAsset removes the PNG and its
        /// .meta cleanly. When the asset path is invalid (PNG not yet imported, so
        /// no .meta exists) or the delete fails, falls back to raw File.Delete of
        /// the png and its .meta so the thumbnail never survives half-deleted.
        /// </summary>
        private static bool DeleteTrackedPng(string pngPath)
        {
            string assetsPath = AssetsRelativePath(pngPath);
            if (!string.IsNullOrEmpty(assetsPath) && AssetDatabase.DeleteAsset(assetsPath))
            {
                return true;
            }
            File.Delete(pngPath);
            string metaPath = pngPath + ".meta";
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
            return true;
        }

        private static string AbsoluteProjectPath(string assetsRelativePath)
        {
            if (Path.IsPathRooted(assetsRelativePath))
            {
                return assetsRelativePath;
            }
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetsRelativePath);
        }

        private static string KeyFor(string guid)
        {
            return k_PrefsPrefix + "." + guid;
        }

        private static long ReadPrefsTicks(string guid)
        {
            string raw = EditorPrefs.GetString(KeyFor(guid), string.Empty);
            long ticks;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            {
                return 0L;
            }
            return ticks;
        }

        private static void WritePrefsTicks(string guid, long ticks)
        {
            EditorPrefs.SetString(KeyFor(guid), ticks.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Drops the cache entry and destroys the texture it owned. Callers that
        /// still hold the reference get a destroyed (== null) object, never a live
        /// one: the cache is the single owner.
        /// </summary>
        private static void Evict(string guid)
        {
            CacheEntry entry;
            if (!s_Cache.TryGetValue(guid, out entry))
            {
                return;
            }
            s_Cache.Remove(guid);
            s_State.TotalBytes -= entry.ByteSize;
            if (entry.Texture != null)
            {
                UnityEngine.Object.DestroyImmediate(entry.Texture);
            }
        }

        /// <summary>
        /// Decodes PNG bytes into a runtime texture owned by the cache. The 4-argument
        /// Texture2D constructor is used (the 2-argument one is deprecated); LoadImage
        /// replaces the data and allocates its own size, and it resets sampling state,
        /// so filterMode/wrapMode are configured AFTER LoadImage. No mipmaps, no
        /// AssetDatabase involvement. Returns null when the bytes are not a valid PNG.
        /// </summary>
        private static Texture2D CreateTextureFromBytes(byte[] pngBytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            if (!texture.LoadImage(pngBytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return null;
            }
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        /// <summary>
        /// Reads the PNG from disk and warms the cache. A missing file (Library
        /// cleanup) returns null silently - a legitimate state, not an error.
        /// </summary>
        private static Texture2D LoadFromDisk(string guid, long sceneTicks)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(PngPathFor(guid));
            }
            catch (Exception)
            {
                return null;
            }
            Texture2D texture = CreateTextureFromBytes(bytes);
            if (texture != null)
            {
                InsertIntoCache(guid, texture, sceneTicks);
            }
            return texture;
        }

        /// <summary>
        /// Adds a decoded texture to the cache with fresh LRU/byte bookkeeping,
        /// then enforces the size cap (LRU eviction while over budget). The only
        /// cache-insert path: Save and LoadFromDisk both land here.
        /// </summary>
        private static void InsertIntoCache(string guid, Texture2D texture, long sceneTicks)
        {
            var entry = new CacheEntry
            {
                Texture = texture,
                Ticks = sceneTicks,
                TouchStamp = ++s_State.TouchCounter,
                ByteSize = texture.width * texture.height * 4,
            };
            s_Cache[guid] = entry;
            s_State.TotalBytes += entry.ByteSize;
            EvictWhileOverBudget();
        }

        /// <summary>
        /// Evicts least-recently-touched entries (min TouchStamp, O(n) scan over a
        /// ~hundreds-entry dictionary) until s_State.TotalBytes fits the cap. Each
        /// eviction destroys the owned texture, subtracts its bytes and fires
        /// TextureEvicted so the icon service re-warms the GUID. Defensive break:
        /// an empty cache can never exceed the cap (byte-accounting invariant).
        /// </summary>
        private static void EvictWhileOverBudget()
        {
            int cap = k_MaxCacheBytes;
            while (s_State.TotalBytes > cap)
            {
                string victimGuid = null;
                uint minStamp = uint.MaxValue;
                foreach (KeyValuePair<string, CacheEntry> pair in s_Cache)
                {
                    if (pair.Value.TouchStamp < minStamp)
                    {
                        minStamp = pair.Value.TouchStamp;
                        victimGuid = pair.Key;
                    }
                }
                if (victimGuid == null)
                {
                    break;
                }
                CacheEntry victim = s_Cache[victimGuid];
                s_Cache.Remove(victimGuid);
                s_State.TotalBytes -= victim.ByteSize;
                if (victim.Texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(victim.Texture);
                }
                s_State.TextureEvicted?.Invoke(victimGuid);
            }
        }

        #endregion
    }
}
