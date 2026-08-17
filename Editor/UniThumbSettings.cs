using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Where generated thumbnails live. LibraryCache keeps the current behavior:
    /// PNGs under Library/SceneThumbnails/ (outside Assets, no .meta files, no VCS
    /// churn). TrackedInAssets is the alternative for projects that want thumbnails
    /// inside Assets (future capture flow; this task only defines the mode).
    /// </summary>
    public enum StorageMode
    {
        LibraryCache,
        TrackedInAssets,
    }

    /// <summary>
    /// Tool settings asset (Assets/UniThumb/UniThumbSettings.asset), auto-created on
    /// first access. The single source of truth for options that batch menus and the
    /// icon overlay must resolve with no window open. No [CreateAssetMenu]: the asset
    /// is created by Get() only, never by hand.
    /// </summary>
    public sealed class UniThumbSettings : ScriptableObject
    {
        #region Constants

        private const string k_AssetFolder = "Assets/UniThumb";
        private const string k_AssetPath = "Assets/UniThumb/UniThumbSettings.asset";

        #endregion

        #region Fields

        [SerializeField]
        private StorageMode m_StorageMode = StorageMode.LibraryCache;

        #endregion

        #region Properties

        /// <summary>
        /// Current storage mode. LibraryCache is the default and the existing
        /// behavior; TrackedInAssets is opt-in via SetStorageMode.
        /// </summary>
        public StorageMode StorageMode => m_StorageMode;

        #endregion

        #region Public Methods

        /// <summary>
        /// Loads the settings asset from disk, creating it (and its folder) on first
        /// access. Load-at-call-time only: no permanent static cache, so the returned
        /// value always reflects the on-disk asset and never goes stale across domain
        /// reloads or after the window changes it. Idempotent - repeat calls return
        /// the same asset without side effects.
        /// </summary>
        public static UniThumbSettings Get()
        {
            UniThumbSettings settings = AssetDatabase.LoadAssetAtPath<UniThumbSettings>(
                k_AssetPath
            );
            if (settings != null)
            {
                return settings;
            }

            EnsureAssetFolder();
            settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            AssetDatabase.CreateAsset(settings, k_AssetPath);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>
        /// Changes the storage mode and marks the asset dirty so the change
        /// persists. Called by the window; later callers re-load via Get().
        /// </summary>
        public void SetStorageMode(StorageMode mode)
        {
            m_StorageMode = mode;
            EditorUtility.SetDirty(this);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates Assets/UniThumb/ when missing. Directory.CreateDirectory on the
        /// absolute path is used instead of AssetDatabase.CreateFolder because the
        /// latter fails when the folder exists on disk but is not yet imported; a
        /// Refresh afterwards registers it with the asset pipeline.
        /// </summary>
        private static void EnsureAssetFolder()
        {
            if (AssetDatabase.IsValidFolder(k_AssetFolder))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "UniThumb"));
            AssetDatabase.Refresh();
        }

        #endregion
    }
}
