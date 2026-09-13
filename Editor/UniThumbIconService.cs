using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Applies scene and prefab thumbnails as Project window icons through a
    /// single overlay mechanism on all Unity versions (2022.3 LTS through
    /// Unity 6). Prefab entries need no special path: storage is GUID-keyed,
    /// so prefab PNGs ({prefabGuid}.png) land in the same GUID set and the
    /// same TryGetCachedTexture-only draw path covers them.
    ///
    /// OVERLAY-ONLY (plan v7): the legacy icon-override API is dropped entirely. It
    /// requires a real imported project asset (the meta stores a GUID reference),
    /// which is impossible for Library/SceneThumbnails/ PNG files, and it is a no-op
    /// for SceneAssets on Unity 6 (IN-135694). Icons are drawn by an
    /// EditorApplication.projectWindowItemOnGUI overlay on every version. ZERO meta
    /// writes: scene meta files are never touched.
    ///
    /// SINGLE TEXTURE OWNER (AC-2): UniThumbStorage owns every runtime texture.
    /// This service keeps no texture dictionary - s_GuidsWithThumbnails is the only
    /// extra state (a pre-built early-exit filter). The overlay callback fetches
    /// textures through the zero-I/O, zero-alloc
    /// UniThumbStorage.TryGetCachedTexture and never destroys textures.
    ///
    /// DISCOVERY (AC-1): the GUID set rebuilds from
    /// UniThumbStorage.EnumerateThumbnailGuids (System.IO.Directory.GetFiles
    /// over Library/SceneThumbnails). The asset database does not index Library/,
    /// so no database asset queries are ever used.
    ///
    /// PERSISTENCE: thumbnails live in Library files; after a domain reload the
    /// [InitializeOnLoadMethod] hook clears the storage cache first (t11/M20: this
    /// service is the SOLE domain-reload init; storage has no init hook of its own),
    /// rebuilds the GUID set from the Library files, and thumbnails reappear without
    /// regeneration. Warming is lazy: RebuildOverlayState enqueues every GUID into
    /// s_WarmQueue and a persistent EditorApplication.update pump (k_WarmPerFrame
    /// per tick, O(1) idle) loads them. Storage's eviction callback
    /// (RegisterEviction) re-enqueues LRU-evicted GUIDs, so icons reappear a few
    /// frames later without user action.
    ///
    /// Manual-only tool: no asset watchers. The overlay callback performs zero side
    /// effects (no I/O, no allocations, no repaints); repaints happen only at
    /// mutation points (ApplyIcon/ClearIcon/ReapplyAllIcons). The warm pump never
    /// repaints - icons appear on the next natural Project window repaint.
    /// </summary>
    public static class UniThumbIconService
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";

        private const float k_LabelStrip = 14f; // tile-mode bottom zone reserved for the label

        // Lazy warm drain budget (P-L1, frozen default 4): OnWarmUpdate loads at
        // most this many GUIDs per update tick. The idle path is one O(1)
        // Queue.Count check and the pump never repaints (icons appear on the
        // next natural Project window repaint). The lazy design stays: never
        // convert the pump to a synchronous load at init. Rollback note: on any
        // warm-pump regression revert this constant to 4 and the single
        // O(1) idle check in OnWarmUpdate.
        private const int k_WarmPerFrame = 4; // lazy warm pump: thumbnail loads per update tick
        #endregion

        #region Fields

        private static readonly HashSet<string> s_GuidsWithThumbnails = new HashSet<string>();
        private static readonly Queue<string> s_WarmQueue = new Queue<string>();

        /// <summary>
        /// Registration bookkeeping on a readonly holder so the class keeps zero
        /// static mutable fields (Asset Store Validator "Check Static Variables").
        /// Domain-reload semantics unchanged: a fresh holder is created on reload,
        /// so overlay/pump re-register in Initialize exactly as before.
        /// </summary>
        private static readonly IconState s_State = new IconState();

        private sealed class IconState
        {
            public bool OverlayRegistered;
            public bool PumpRegistered;
            public bool DrainLogged;
            public bool Enabled;
            public float ListPaddingX;
            public float ListPaddingY;
            public float MaxSize;
        }

        #endregion

        #region Unity Callbacks

        /// <summary>
        /// Sole domain-reload init (t11/M20): storage has no init hook of its own,
        /// so ordering is guaranteed - cache cleared first (stale destroyed-texture
        /// refs can never leak into the overlay state), overlay + warm pump
        /// registered once per domain, eviction re-warm subscribed, then the GUID
        /// set rebuilds from the Library files and every GUID is enqueued for lazy
        /// warm.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            UniThumbStorage.ClearCache();
            RefreshFromSettings();
            RegisterOverlay();
            RegisterPump();
            UniThumbStorage.RegisterEviction(OnTextureEvicted);
            int count = RebuildOverlayState();
            if (count > 0)
            {
                Debug.Log(
                    k_LogPrefix
                        + "Overlay ready for "
                        + count
                        + " thumbnails (scenes/prefabs, warming asynchronously)."
                );
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds the scene or prefab to the overlay GUID set so the thumbnail
        /// draws over the default icon. Returns false (with a warning) when
        /// the asset has no thumbnail in storage. Repaints the Project window.
        /// Never throws.
        /// </summary>
        public static bool ApplyIcon(string scenePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(
                    k_LogPrefix + "ApplyIcon refused for '" + scenePath + "': no asset GUID."
                );
                return false;
            }

            if (!UniThumbStorage.HasThumbnail(scenePath))
            {
                if (UniThumbStorage.Load(scenePath) == null)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "ApplyIcon skipped for '"
                            + scenePath
                            + "': no thumbnail in storage."
                    );
                    return false;
                }
            }

            s_GuidsWithThumbnails.Add(guid);
            EditorApplication.RepaintProjectWindow();
            return true;
        }

        /// <summary>
        /// Removes the scene or prefab from the overlay GUID set; the default
        /// icon shows again. Texture eviction is handled by the storage Delete
        /// caller - this service never destroys textures. Repaints the Project
        /// window. Never throws.
        /// </summary>
        public static bool ClearIcon(string scenePath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(
                    k_LogPrefix + "ClearIcon refused for '" + scenePath + "': no asset GUID."
                );
                return false;
            }

            s_GuidsWithThumbnails.Remove(guid);
            EditorApplication.RepaintProjectWindow();
            return true;
        }

        /// <summary>
        /// Rebuilds the overlay GUID set from storage (scenes that still have
        /// thumbnail files; stale entries pruned) and repaints the Project window.
        /// Returns the number of scenes with thumbnails.
        /// </summary>
        public static int ReapplyAllIcons()
        {
            int count = RebuildOverlayState();
            EditorApplication.RepaintProjectWindow();
            return count;
        }

        public static void RefreshFromSettings()
        {
            UniThumbSettings settings = UniThumbSettings.Get();
            bool wasEnabled = s_State.Enabled;
            float wasListX = s_State.ListPaddingX;
            float wasListY = s_State.ListPaddingY;
            float wasMaxSize = s_State.MaxSize;
            s_State.Enabled = settings.IconOverlayEnabled;
            s_State.ListPaddingX = settings.ListPaddingX;
            s_State.ListPaddingY = settings.ListPaddingY;
            s_State.MaxSize = settings.IconMaxSize;
            if (
                wasEnabled != s_State.Enabled
                || !Mathf.Approximately(wasListX, s_State.ListPaddingX)
                || !Mathf.Approximately(wasListY, s_State.ListPaddingY)
                || !Mathf.Approximately(wasMaxSize, s_State.MaxSize)
            )
            {
                EditorApplication.RepaintProjectWindow();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Registers the Project window overlay exactly once per domain load.
        /// </summary>
        private static void RegisterOverlay()
        {
            if (s_State.OverlayRegistered)
            {
                return;
            }
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            s_State.OverlayRegistered = true;
        }

        /// <summary>
        /// Registers the lazy warm pump exactly once per domain. Persistent: never
        /// unregistered (idle cost is a single Queue.Count == 0 check per tick).
        /// </summary>
        private static void RegisterPump()
        {
            if (s_State.PumpRegistered)
            {
                return;
            }
            EditorApplication.update += OnWarmUpdate;
            s_State.PumpRegistered = true;
        }

        /// <summary>
        /// Warm pump: drains up to k_WarmPerFrame GUIDs per update tick. Idle path
        /// is one O(1) count check. Never repaints - icons appear on the next
        /// natural Project window repaint. Logs once per drain cycle when the whole
        /// queue has been loaded.
        /// </summary>
        private static void OnWarmUpdate()
        {
            if (s_WarmQueue.Count == 0)
            {
                return;
            }
            for (int i = 0; i < k_WarmPerFrame && s_WarmQueue.Count > 0; i++)
            {
                string guid = s_WarmQueue.Dequeue();
                UniThumbStorage.LoadByGuid(guid);
            }
            if (s_WarmQueue.Count == 0 && !s_State.DrainLogged)
            {
                s_State.DrainLogged = true;
                int count = s_GuidsWithThumbnails.Count;
                if (count > 0)
                {
                    Debug.Log(k_LogPrefix + "All " + count + " thumbnails loaded.");
                }
            }
        }

        /// <summary>
        /// LRU eviction re-warm: a size-cap eviction re-enqueues the GUID so the
        /// pump reloads it from disk within a few frames and the icon reappears
        /// without user action.
        /// </summary>
        private static void OnTextureEvicted(string guid)
        {
            s_WarmQueue.Enqueue(guid);
        }

        /// <summary>
        /// Rebuilds the GUID set from the Library thumbnail files and enqueues
        /// every GUID for lazy warm. Deleted or renamed scenes drop out; scenes
        /// that gained a thumbnail file come in. Never uses database asset queries
        /// (Library/ is not indexed).
        /// </summary>
        private static int RebuildOverlayState()
        {
            s_GuidsWithThumbnails.Clear();
            s_WarmQueue.Clear();
            string[] guids = UniThumbStorage.EnumerateThumbnailGuids();
            for (int i = 0; i < guids.Length; i++)
            {
                s_GuidsWithThumbnails.Add(guids[i]);
                // Lazy warm (t11): no synchronous PNG reads at init. The pump
                // drains k_WarmPerFrame GUIDs per update tick; already-cached
                // entries resolve instantly inside LoadByGuid.
                s_WarmQueue.Enqueue(guids[i]);
            }

            s_State.DrainLogged = false;
            return s_GuidsWithThumbnails.Count;
        }

        /// <summary>
        /// Project window overlay callback. Zero side effects: no I/O, no
        /// allocations, no AssetDatabase calls, no repaints. Pure draw path. The
        /// GUID set filters before any work; the texture comes from the storage
        /// cache only (never from disk).
        /// </summary>
        private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
        {
            if (!s_State.Enabled)
            {
                return;
            }
            if (!s_GuidsWithThumbnails.Contains(guid))
            {
                return;
            }
            Texture2D thumbnail;
            if (!UniThumbStorage.TryGetCachedTexture(guid, out thumbnail))
            {
                return;
            }
            Rect slot = ComputeIconRect(selectionRect);
            if (slot.width < 1f || slot.height < 1f)
            {
                return;
            }
            GUI.DrawTexture(ComputeDrawRect(slot, thumbnail), thumbnail, ScaleMode.ScaleToFit);
        }

        /// <summary>
        /// Fits the texture inside the slot with its aspect preserved, caps the
        /// longest side at MaxSize, and centers the result in the slot. Pure
        /// math, no allocations. The final ScaleToFit draw is then a no-op scale,
        /// so the drawn rect is exactly what was computed here.
        /// </summary>
        private static Rect ComputeDrawRect(Rect slot, Texture2D texture)
        {
            float texAspect = texture.width / (float)texture.height;
            float width = slot.width;
            float height = slot.height;
            if (width / height > texAspect)
            {
                width = height * texAspect;
            }
            else
            {
                height = width / texAspect;
            }
            float longest = Mathf.Max(width, height);
            if (longest > s_State.MaxSize)
            {
                float scale = s_State.MaxSize / longest;
                width *= scale;
                height *= scale;
            }
            return new Rect(
                slot.x + (slot.width - width) * 0.5f,
                slot.y + (slot.height - height) * 0.5f,
                width,
                height
            );
        }

        /// <summary>
        /// Mode-aware icon rect (pure math, no allocations). List rows are
        /// wide-short (~16px tall): draw a square inset by the per-axis list
        /// padding. Tiles are tall (~96x110): draw the full rect minus the
        /// ~14px bottom strip the Project window fills with the label AFTER
        /// this callback; tile thumbnails have no extra padding.
        /// </summary>
        private static Rect ComputeIconRect(Rect selectionRect)
        {
            if (selectionRect.width > selectionRect.height * 1.3f)
            {
                float size = Mathf.Min(
                    selectionRect.width - 2 * s_State.ListPaddingX,
                    selectionRect.height - 2 * s_State.ListPaddingY
                );
                return new Rect(
                    selectionRect.x + s_State.ListPaddingX,
                    selectionRect.y + s_State.ListPaddingY,
                    size,
                    size
                );
            }
            float height =
                selectionRect.height - Mathf.Min(k_LabelStrip, selectionRect.height * 0.25f);
            if (height < 1f)
            {
                return new Rect(0f, 0f, 0f, 0f);
            }
            return new Rect(selectionRect.x, selectionRect.y, selectionRect.width, height);
        }

        #endregion
    }
}
