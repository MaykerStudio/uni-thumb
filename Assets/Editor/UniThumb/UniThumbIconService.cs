using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Applies scene thumbnails as Project window icons through a single overlay
    /// mechanism on all Unity versions (2022.3 LTS through Unity 6).
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
    /// per tick, O(1) idle) loads them. Storage's TextureEvicted re-enqueues
    /// LRU-evicted GUIDs, so icons reappear a few frames later without user action.
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
        private const float k_IconPadding = 2f; // list-mode icon inset from row edges
        private const float k_IconMaxSize = 14f; // threshold: rects larger than this use aspect matching (tile mode)
        private const float k_LabelStrip = 14f; // tile-mode bottom zone reserved for the label
        private const int k_WarmPerFrame = 4; // lazy warm pump: thumbnail loads per update tick
        #endregion

        #region Fields

        private static readonly HashSet<string> s_GuidsWithThumbnails = new HashSet<string>();
        private static readonly Queue<string> s_WarmQueue = new Queue<string>();
        private static bool s_OverlayRegistered;
        private static bool s_PumpRegistered;
        private static bool s_DrainLogged;

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
            RegisterOverlay();
            RegisterPump();
            UniThumbStorage.TextureEvicted += OnTextureEvicted;
            int count = RebuildOverlayState();
            if (count > 0)
            {
                Debug.Log(
                    k_LogPrefix
                        + "Overlay ready for "
                        + count
                        + " scene thumbnails (warming asynchronously)."
                );
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds the scene to the overlay GUID set so the thumbnail draws over the
        /// default scene icon. Returns false (with a warning) when the scene has no
        /// thumbnail in storage. Repaints the Project window. Never throws.
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
        /// Removes the scene from the overlay GUID set; the default scene icon shows
        /// again. Texture eviction is handled by the storage Delete caller - this
        /// service never destroys textures. Repaints the Project window. Never
        /// throws.
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

        #endregion

        #region Private Methods

        /// <summary>
        /// Registers the Project window overlay exactly once per domain load.
        /// </summary>
        private static void RegisterOverlay()
        {
            if (s_OverlayRegistered)
            {
                return;
            }
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            s_OverlayRegistered = true;
        }

        /// <summary>
        /// Registers the lazy warm pump exactly once per domain. Persistent: never
        /// unregistered (idle cost is a single Queue.Count == 0 check per tick).
        /// </summary>
        private static void RegisterPump()
        {
            if (s_PumpRegistered)
            {
                return;
            }
            EditorApplication.update += OnWarmUpdate;
            s_PumpRegistered = true;
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
                UniThumbStorage.LoadByGuid(s_WarmQueue.Dequeue());
            }
            if (s_WarmQueue.Count == 0 && !s_DrainLogged)
            {
                s_DrainLogged = true;
                int count = s_GuidsWithThumbnails.Count;
                if (count > 0)
                {
                    Debug.Log(k_LogPrefix + "All " + count + " scene thumbnails loaded.");
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
            string[] guids = UniThumbStorage.EnumerateThumbnailGuids();
            for (int i = 0; i < guids.Length; i++)
            {
                s_GuidsWithThumbnails.Add(guids[i]);
                // Lazy warm (t11): no synchronous PNG reads at init. The pump
                // drains k_WarmPerFrame GUIDs per update tick; already-cached
                // entries resolve instantly inside LoadByGuid.
                s_WarmQueue.Enqueue(guids[i]);
            }
            s_DrainLogged = false;
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
            if (!s_GuidsWithThumbnails.Contains(guid))
            {
                return;
            }
            Texture2D thumbnail;
            if (!UniThumbStorage.TryGetCachedTexture(guid, out thumbnail))
            {
                return;
            }
            Rect iconRect = ComputeIconRect(selectionRect);
            if (iconRect.width < 1f || iconRect.height < 1f)
            {
                return;
            }
            Rect drawRect;
            if (iconRect.width > k_IconMaxSize && iconRect.height > k_IconMaxSize)
            {
                // Tile mode: large rect, apply aspect matching to avoid distortion
                float texAspect = thumbnail.width / (float)thumbnail.height;
                float rectAspect = iconRect.width / iconRect.height;
                if (rectAspect > texAspect)
                {
                    float w = iconRect.height * texAspect;
                    drawRect = new Rect(
                        iconRect.x + (iconRect.width - w) * 0.5f,
                        iconRect.y,
                        w,
                        iconRect.height
                    );
                }
                else if (rectAspect < texAspect)
                {
                    float h = iconRect.width / texAspect;
                    drawRect = new Rect(
                        iconRect.x,
                        iconRect.y + (iconRect.height - h) * 0.5f,
                        iconRect.width,
                        h
                    );
                }
                else
                {
                    drawRect = iconRect;
                }
            }
            else
            {
                // List mode: small rect, draw at full size (no aspect reduction)
                drawRect = iconRect;
            }
            GUI.DrawTexture(drawRect, thumbnail, ScaleMode.ScaleToFit);
        }

        /// <summary>
        /// Mode-aware icon rect (pure math, no allocations). List rows are
        /// wide-short (~16px tall): draw a left square sized to fill the row
        /// height minus padding. Tiles are tall (~96x110): draw the full rect
        /// minus the ~14px bottom strip the Project window fills with the label
        /// AFTER this callback.
        /// </summary>
        private static Rect ComputeIconRect(Rect selectionRect)
        {
            if (selectionRect.width > selectionRect.height * 1.3f)
            {
                float size = Mathf.Min(
                    selectionRect.width,
                    selectionRect.height - 2 * k_IconPadding
                );
                return new Rect(
                    selectionRect.x + k_IconPadding,
                    selectionRect.y + k_IconPadding,
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
