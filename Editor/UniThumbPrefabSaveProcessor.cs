using System.Collections.Generic;
using UnityEditor;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Asset-save hook for prefab auto-regeneration. OnWillSaveAssets is the
    /// single choke point for every prefab save (inspector Apply, Prefab
    /// Stage Ctrl+S, script-driven saves, multi-select saves): each saved
    /// prefab arrives as its own .prefab entry, so variants and nested
    /// prefabs need no special casing beyond the extension filter.
    /// PrefabStage.prefabSaved was rejected: it only covers Prefab Stage
    /// saves and would miss the other save routes. The hook never captures
    /// synchronously; it filters, checks the gate, and defers to delayCall.
    /// The input array is always returned unchanged.
    /// </summary>
    internal class UniThumbPrefabSaveProcessor : AssetModificationProcessor
    {
        #region Public Methods

        public static string[] OnWillSaveAssets(string[] paths)
        {
            return HandleWillSaveAssets(paths);
        }

        #endregion

        #region Internal Methods

        internal static string[] HandleWillSaveAssets(string[] paths)
        {
            if (paths == null)
            {
                return null;
            }
            List<string> prefabs = UniThumbPrefabAutoRegen.CollectPrefabPaths(paths);
            if (prefabs.Count > 0 && UniThumbPrefabAutoRegen.IsAutoRegenArmed())
            {
                UniThumbPrefabAutoRegen.RequestRegen(prefabs);
            }
            return paths;
        }

        internal static string[] HandleWillSaveAssetsForTest(string[] paths)
        {
            return HandleWillSaveAssets(paths);
        }

        #endregion
    }
}
