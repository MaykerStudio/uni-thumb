using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Prefab auto-regeneration on save. Pure filter/gate helpers (testable
    /// without an open window) plus the deferred execution path that reuses
    /// the manual prefab capture/storage/icon flow (CapturePrefab,
    /// SavePrefabThumbnail, ApplyIcon). Armed only when the
    /// AutoRegenerateOnSave setting is on AND the UniThumb window is open;
    /// every capture is wrapped in UniThumbGuard TryEnter/finally Exit.
    /// </summary>
    internal static class UniThumbPrefabAutoRegen
    {
        #region Constants

        private const string k_AutoRegenTooltip =
            "Automatically regenerate scene and prefab thumbnails when they are saved. Runs only while this window is open";

        #endregion

        #region Properties

        internal static string AutoRegenTooltip => k_AutoRegenTooltip;

        #endregion

        #region Public Methods

        /// <summary>
        /// Filters an OnWillSaveAssets path array down to prefab asset paths.
        /// Variants and nested prefabs are separate .prefab assets, so each
        /// arrives as its own entry and is kept; repeats are deduplicated.
        /// Never returns null.
        /// </summary>
        public static List<string> CollectPrefabPaths(string[] paths)
        {
            List<string> result = new List<string>();
            if (paths == null)
            {
                return result;
            }
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (!UniThumbBatchMenus.IsPrefabAssetPath(path))
                {
                    continue;
                }
                if (seen.Contains(path))
                {
                    continue;
                }
                seen.Add(path);
                result.Add(path);
            }
            return result;
        }

        /// <summary>
        /// Gate for prefab auto-regeneration: same opt-in plus window-open
        /// requirement as the scene path, plus a free guard (no re-entrancy).
        /// </summary>
        public static bool ShouldAutoRegenerate(
            bool settingEnabled,
            bool windowOpen,
            bool guardBusy
        )
        {
            return settingEnabled && windowOpen && !guardBusy;
        }

        /// <summary>
        /// Live gate reading the real setting, window, and guard state.
        /// </summary>
        public static bool IsAutoRegenArmed()
        {
            UniThumbSettings settings = UniThumbSettings.Get();
            if (settings == null || !settings.AutoRegenerateOnSave)
            {
                return false;
            }
            if (!EditorWindow.HasOpenInstances<UniThumbWindow>())
            {
                return false;
            }
            return !UniThumbGuard.IsGenerating;
        }

        /// <summary>
        /// Schedules background regeneration for saved prefab paths. The
        /// capture runs on delayCall (after the save completes), never
        /// synchronously inside OnWillSaveAssets.
        /// </summary>
        public static void RequestRegen(List<string> prefabPaths)
        {
            if (prefabPaths == null || prefabPaths.Count == 0)
            {
                return;
            }
            string[] copy = prefabPaths.ToArray();
            EditorApplication.delayCall += () => RegenNow(copy);
        }

        #endregion

        #region Private Methods

        private static void RegenNow(string[] prefabPaths)
        {
            if (prefabPaths == null || prefabPaths.Length == 0)
            {
                return;
            }
            UniThumbWindow[] windows = Resources.FindObjectsOfTypeAll<UniThumbWindow>();
            for (int i = 0; i < prefabPaths.Length; i++)
            {
                string prefabPath = prefabPaths[i];
                if (string.IsNullOrEmpty(prefabPath))
                {
                    continue;
                }
                if (UniThumbGuard.IsGenerating)
                {
                    continue;
                }
                if (windows != null && windows.Length > 0 && windows[0] != null)
                {
                    windows[0].RegeneratePrefabThumbnailOnSave(prefabPath);
                }
                else
                {
                    RegenerateWithoutWindow(prefabPath);
                }
            }
        }

        private static void RegenerateWithoutWindow(string prefabPath)
        {
            if (!UniThumbGuard.TryEnter())
            {
                return;
            }
            try
            {
                CaptureSettings settings = UniThumbCapture.GetLastSettingsOrDefault();
                CaptureResult result = UniThumbCapture.CapturePrefab(prefabPath, settings);
                if (!result.Success)
                {
                    return;
                }
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                if (string.IsNullOrEmpty(guid))
                {
                    return;
                }
                if (!UniThumbStorage.SavePrefabThumbnail(guid, result.PngBytes))
                {
                    return;
                }
                UniThumbIconService.ApplyIcon(prefabPath);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        #endregion
    }
}
