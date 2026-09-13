using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Manual-only integration: context menus (Generate / Clear / Refresh All /
    /// Generate in Folder / Generate Prefab UniThumbs in Folder) and the build-scenes batch. These menus plus the
    /// window button are the ONLY
    /// capture entry points in the tool — there are zero automatic asset/scene
    /// hooks (no OnPostprocessAllAssets). The only scene hook is the opt-in
    /// AutoRegenerateOnSave: when enabled in settings AND the UniThumb window
    /// is open, saving the active scene regenerates its thumbnail via the
    /// sceneSaved callback. It is off by default, so nothing ever generates
    /// automatically unless the user explicitly opts in. Every handler enters the shared
    /// UniThumbGuard (M2, manual paths) to prevent double-trigger from
    /// multi-select context menu clicks.
    ///
    /// Batch (Refresh All) is two-pass: pass 1 captures + saves + verifies every
    /// requested scene (progress bar, one EditorApplication.update frame yielded
    /// between scenes, plus a shader-compile wait after each scene switch so a
    /// render never samples half-compiled URP variants), pass 2 applies icons
    /// for verified PNGs only. Refresh All
    /// regenerates ONLY explicitly requested scenes: missing (no thumbnail in
    /// storage) or the user-selected scene — never
    /// unrequested scenes. An opt-in Refresh Stale menu (default off) additionally
    /// regenerates stale thumbnails (source asset newer than the stored PNG,
    /// Storage tick compare). Prefabs share the same contract: Refresh All
    /// regenerates missing prefab thumbnails (existence-only check, same as
    /// scenes) plus the user-selected prefab; the folder batch regenerates
    /// EVERY scene AND prefab under the selected folders (an explicit request,
    /// not missing-only) and reuses the same two-pass pump. Prefab entries skip
    /// the scene-switch and shader-compile wait in the pump (CapturePrefab
    /// renders additively in the open scene, never opening anything) but keep
    /// the pass order: capture + save + verify per entry, icons in pass 2.
    /// selected folders (an explicit request, not missing-only) and reuses the
    /// same two-pass pump. Scenes outside the project (Packages/ etc.) are
    /// skipped with a warning, never captured. The active scene is switched per
    /// target scene and the original scene is restored when the batch finishes.
    /// Scene switches never prompt and never save: unless the opt-in
    /// Allow Batch Discard setting (Settings tab, Tracking card) is
    /// on, every generation entry point refuses to start while any open
    /// scene is dirty (abort-on-dirty default, checked before the guard).
    /// With the override on, unsaved modifications (including auto-changes
    /// triggered by opening a scene) are silently discarded, never saved.
    /// </summary>
    public static class UniThumbBatchMenus
    {
        #region BatchScope

        /// <summary>
        /// Batch asset-type filter. All keeps the historical mixed
        /// scene+prefab behaviour; ScenesOnly collects scenes; PrefabsOnly
        /// collects prefabs. Replaces the legacy prefabOnly bool (2 of 3
        /// states) while every bool overload delegates to a scope core.
        /// </summary>
        public enum BatchScope
        {
            All,
            ScenesOnly,
            PrefabsOnly,
        }

        #endregion

        #region Batch Snapshot

        /// <summary>
        /// Consistent view of the running batch pump for UI consumers (the tool
        /// window's batch section). Read via GetBatchSnapshot - never
        /// constructed by callers. Idle state: IsRunning=false, zeroed counters,
        /// null CurrentScene.
        /// </summary>
        public struct BatchSnapshot
        {
            public bool IsRunning;
            public int Total;
            public int Processed;
            public int Succeeded;
            public int Failed;
            public int Skipped;
            public string CurrentScene;
        }

        #endregion

        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";
        private const string k_GenerateMenuPath = "Assets/Generate UniThumb";
        private const string k_ClearMenuPath = "Assets/Clear UniThumb";
        private const string k_RefreshAllMenuPath = "Assets/Refresh All UniThumbs";
        private const string k_RefreshStaleMenuPath = "Assets/Refresh Stale UniThumbs";
        private const string k_GenerateFolderMenuPath = "Assets/Generate UniThumbs in Folder";
        private const string k_GeneratePrefabFolderMenuPath =
            "Assets/Generate Prefab UniThumbs in Folder";
        private const int k_GenerateMenuPriority = 1100;
        private const int k_ClearMenuPriority = 1101;
        private const int k_RefreshAllMenuPriority = 1102;
        private const int k_GenerateFolderMenuPriority = 1103;
        private const int k_GeneratePrefabFolderMenuPriority = 1104;
        private const int k_RefreshStaleMenuPriority = 1105;
        private const string k_UnsavedDiscardOverrideKey =
            "UniThumb.AllowBatchDiscardUnsavedChanges";

        /// <summary>
        /// Appended to every batch confirm dialog so the discard behaviour is
        /// disclosed at the decision point (Asset Store compliance).
        /// </summary>
        internal const string DiscardDisclosure =
            "\n\nUnsaved scene changes: thumbnail generation switches scenes and "
            + "discards unsaved changes instead of saving. Batches refuse to start "
            + "while any open scene is dirty; either save first or allow batch "
            + "discard in the UniThumb window Settings tab (Tracking card).";
        private const string k_FolderBatchKind = "Folder";
        private const string k_ProgressTitle = "UniThumb";
        private const string k_ProgressMessageFormat = "Generating thumbnail for '{0}'...";
        private const string k_ShaderCompileProgressMessageFormat =
            "Compiling shaders for '{0}'...";

        // Cap for the post-OpenScene shader-compile wait (pink-material fix):
        // URP compiles never-used shader variants asynchronously after a scene
        // switch; rendering before completion samples pink materials. After the
        // timeout the capture proceeds anyway, warned.
        private const float k_ShaderCompileTimeout = 10f;

        // Bulk generation cap (t11/AC-M19): menu + batch capture paths may never
        // request more than 2048px (a 4096px bulk run would blow the cache cap).
        // Single-scene window captures keep their 4096px ceiling (k_MaxResolution
        // in UniThumbCapture). Batch defaults are 512px, so this guard is
        // defensive - it must exist and be provable.
        private const int k_MaxBulkResolution = 2048;

        /// <summary>
        /// Time window (seconds) for the batch asset guid cache. While the pump
        /// is running, collectors reuse 1x t:Scene + 1x t:Prefab per batch;
        /// outside the pump every collector queries fresh (see GetCachedGuids).
        /// </summary>
        private const double k_AssetCacheSeconds = 60.0;

        #endregion

        #region Fields

        /// <summary>
        /// All mutable batch-pump state lives on this readonly holder so the
        /// class keeps zero static mutable fields (Asset Store Validator
        /// "Check Static Variables"). Domain-reload semantics are unchanged:
        /// a fresh holder is created on reload, exactly like the old statics.
        /// </summary>
        private static readonly BatchState s_State = new BatchState();

        /// <summary>
        /// Test seam for the dirty pre-check. Null means the real
        /// EditorSceneManager dirty scan runs.
        /// </summary>
        internal static Func<bool> UnsavedChangesProbeForTest;

        /// <summary>
        /// Opt-in stale regeneration for Refresh All (default off). False keeps
        /// the historical missing-only behaviour; true lets the parameterless
        /// CollectRefreshWork also queue stale thumbnails (see IsStale). The
        /// Refresh Stale menu passes its opt-in per-run instead of this flag,
        /// so the default Refresh All path never depends on shared state.
        /// </summary>
        internal static bool RefreshIncludesStale
        {
            get { return s_State.IncludeStale; }
            set { s_State.IncludeStale = value; }
        }

        /// <summary>
        /// Rollback bypass for the perf wave-2 batch asset guid cache. False
        /// (default) reuses one project-wide FindAssets per type while the
        /// batch pump is running (see GetCachedGuids); every public collector
        /// entry point queries fresh outside the pump. True restores a fresh
        /// FindAssets per collector call. Revert note: setting this true is the
        /// supported rollback when a collector-scope regression is suspected;
        /// no other change is needed.
        /// </summary>
        internal static bool DisableAssetCache
        {
            get { return s_State.DisableAssetCache; }
            set { s_State.DisableAssetCache = value; }
        }

        private static bool s_MigratedLegacyDiscard;

        private sealed class BatchState
        {
            public Queue<string> PendingScenes;
            public List<string> SucceededScenes;
            public List<string> FailedScenes;
            public int TotalScenes;
            public int ProcessedCount;
            public string OriginalScenePath;
            public bool SwitchedScenes;
            public string BatchKind;
            public int SkippedCount;

            // Capture settings snapshot for the running batch, taken once in
            // StartBatchPump from the window's remembered settings so mid-batch UI
            // edits cannot drift per-scene captures.
            public CaptureSettings BatchSettings;

            // Current scene being processed (GetBatchSnapshot.CurrentScene) and the
            // user cancel flag (RequestBatchCancel); both reset in CancelBatchState.
            public string CurrentScenePath;
            public bool CancelRequested;

            // Shader-compile wait state (pink-material fix): OpenScene kicks off an
            // async URP variant compile; the capture for the current scene is
            // deferred until ShaderUtil.anythingCompiling settles or the timeout
            // fires. Both reset in CancelBatchState.
            public bool WaitingForShaderCompile;
            public double WaitStartedAt;

            // Set when a pump step writes a thumbnail (UniThumbStorage.Save
            // succeeded); cleared at pump start. Drives the single teardown
            // AssetDatabase.Refresh in CancelBatchState when the active storage mode
            // is TrackedInAssets (PNGs written under Assets need one import pass so
            // they register with the asset pipeline). Library mode never refreshes.
            public bool WroteThumbnails;

            // Opt-in stale regeneration for Refresh All (default off): when true,
            // the parameterless CollectRefreshWork also queues thumbnails whose
            // source asset is newer than the stored PNG (see IsStale). The
            // Refresh Stale menu passes its opt-in per-run instead of this flag,
            // so the default Refresh All path never depends on shared state.
            public bool IncludeStale;

            // Batch asset guid cache (see GetCachedGuids): last t:Scene and
            // t:Prefab query results plus the
            // EditorApplication.timeSinceStartup tick of the newest entry.
            // Null means cold (query on next use).
            public string[] CachedSceneGuids;
            public string[] CachedPrefabGuids;
            public double AssetCacheTick;

            // Rollback bypass for the asset guid cache (see DisableAssetCache).
            public bool DisableAssetCache;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// True while the batch pump is active (a batch is queued and running).
        /// </summary>
        public static bool IsBatchRunning
        {
            get { return s_State.PendingScenes != null; }
        }

        /// <summary>
        /// Requests a graceful stop: the pump halts before the next scene and
        /// reports "cancelled after k of N scenes". Picked up at the top of
        /// OnBatchUpdate; harmless when no batch is running.
        /// </summary>
        public static void RequestBatchCancel()
        {
            s_State.CancelRequested = true;
        }

        /// <summary>
        /// Single consistent read of the pump state for UI consumers. Idle
        /// batches report IsRunning=false with zeroed counters and a null
        /// CurrentScene.
        /// </summary>
        public static BatchSnapshot GetBatchSnapshot()
        {
            if (s_State.PendingScenes == null)
            {
                return new BatchSnapshot();
            }
            return new BatchSnapshot
            {
                IsRunning = true,
                Total = s_State.TotalScenes,
                Processed = s_State.ProcessedCount,
                Succeeded = s_State.SucceededScenes != null ? s_State.SucceededScenes.Count : 0,
                Failed = s_State.FailedScenes != null ? s_State.FailedScenes.Count : 0,
                Skipped = s_State.SkippedCount,
                CurrentScene = s_State.CurrentScenePath,
            };
        }

        /// <summary>
        /// Starts the folder batch over a single project folder (recursive scene
        /// discovery, every scene regenerates - not missing-only). Validates the
        /// assets-relative path, asks for user confirmation, then enters the
        /// shared guard and the capture-readiness checks like the menu handlers
        /// before starting the pump. Returns false with a user-facing error
        /// string on any refusal or cancellation. The pump holds the guard
        /// until CompleteBatch/AbortBatch/CancelBatch -> CancelBatchState; every
        /// pre-pump exit releases it exactly once here (never a double Exit).
        /// </summary>
        public static bool TryStartFolderBatch(string folderPath, out string error)
        {
            return TryStartFolderBatch(folderPath, BatchScope.All, out error);
        }

        /// <summary>
        /// Folder batch with a prefab-only filter. False (default) keeps the
        /// mixed scene+prefab behaviour; true collects prefabs only via
        /// CollectFolderPrefabPaths. Guard/dialog/pump protocol matches the
        /// mixed path exactly. Legacy wrapper over the BatchScope core.
        /// </summary>
        public static bool TryStartFolderBatch(string folderPath, bool prefabOnly, out string error)
        {
            return TryStartFolderBatch(
                folderPath,
                prefabOnly ? BatchScope.PrefabsOnly : BatchScope.All,
                out error
            );
        }

        /// <summary>
        /// Folder batch scoped core. All regenerates every scene AND prefab;
        /// ScenesOnly regenerates scenes; PrefabsOnly regenerates prefabs.
        /// Validates the assets-relative path, asks for user confirmation,
        /// then enters the shared guard and the capture-readiness checks like
        /// the menu handlers before starting the pump. Returns false with a
        /// user-facing error string on any refusal or cancellation. The pump
        /// holds the guard until CompleteBatch/AbortBatch/CancelBatchState;
        /// every pre-pump exit releases it exactly once here.
        /// </summary>
        public static bool TryStartFolderBatch(
            string folderPath,
            BatchScope scope,
            out string error
        )
        {
            error = null;
            if (
                string.IsNullOrEmpty(folderPath)
                || !folderPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !AssetDatabase.IsValidFolder(folderPath)
            )
            {
                error =
                    "Not a valid project folder: '"
                    + folderPath
                    + "'. Pick a folder inside Assets/.";
                return false;
            }

            // Count the work and confirm BEFORE the guard: a cancelled dialog
            // must leave the guard free, no pump scheduled and no batch state
            // mutated. The work list is reused by the pump below.
            List<string> work;
            int skipped;
            try
            {
                work = CollectFolderWork(new List<string> { folderPath }, scope, out skipped);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                error = "Unexpected error while starting the batch: " + exception.Message;
                return false;
            }
            if (work.Count == 0)
            {
                error =
                    scope == BatchScope.PrefabsOnly
                        ? "No prefabs found in folder '" + folderPath + "'."
                    : scope == BatchScope.ScenesOnly
                        ? "No scenes found in folder '" + folderPath + "'."
                    : "No scenes or prefabs found in folder '" + folderPath + "'.";
                return false;
            }
            // Abort-on-dirty pre-check BEFORE any dialog or the guard: a
            // refusal must leave the guard free and no pump scheduled.
            string operationName =
                scope == BatchScope.PrefabsOnly ? "Generate Prefab UniThumbs"
                : scope == BatchScope.ScenesOnly ? "Generate Scene UniThumbs"
                : "Generate UniThumbs";
            string dirtyRefusal;
            if (!CheckNoUnsavedChangesOrRefuse(operationName, out dirtyRefusal))
            {
                error = dirtyRefusal;
                return false;
            }
            string countLabel =
                scope == BatchScope.PrefabsOnly ? work.Count + " prefab(s) in '"
                : scope == BatchScope.ScenesOnly ? work.Count + " scene(s) in '"
                : work.Count + " scene/prefab(s) in '";
            if (
                !ConfirmBatchStart(
                    operationName,
                    "Generate thumbnails for " + countLabel + folderPath + "'?" + DiscardDisclosure,
                    "Generate"
                )
            )
            {
                error = "Batch generation cancelled by user.";
                return false;
            }

            if (!ConfirmSceneViewAngleBatchWarning())
            {
                error = "Batch generation cancelled: Scene View angle warning.";
                return false;
            }

            if (!UniThumbGuard.TryEnter())
            {
                error = "Another thumbnail generation is already in progress.";
                return false;
            }

            bool pumpStarted = false;
            try
            {
                if (!CanRunCapture())
                {
                    error =
                        "The editor is busy (play mode, compiling or importing assets). Try again when it settles.";
                    return false;
                }

                s_State.BatchKind = k_FolderBatchKind;
                s_State.SkippedCount = skipped;
                s_State.CancelRequested = false;
                StartBatchPump(work);
                pumpStarted = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                error = "Unexpected error while starting the batch: " + exception.Message;
                return false;
            }
            finally
            {
                // Same guard protocol as the menu handlers: the pump holds the
                // guard until CancelBatchState; non-pump exits release it here.
                if (!pumpStarted)
                {
                    UniThumbGuard.Exit();
                }
            }
        }

        [MenuItem(k_GenerateMenuPath, false, k_GenerateMenuPriority)]
        public static void GenerateUniThumb()
        {
            string prefabPath;
            if (TryGetSelectedPrefabPath(out prefabPath))
            {
                GeneratePrefabThumbnail(prefabPath);
                return;
            }

            string scenePath;
            if (!TryGetSelectedScenePath(out scenePath))
            {
                Debug.LogWarning(
                    k_LogPrefix + "Generate UniThumb refused: select a SceneAsset first."
                );
                return;
            }

            // Abort-on-dirty pre-check BEFORE the guard: a refusal must leave
            // the guard free (single-scene path has no confirm dialog, so the
            // refusal message goes to the console).
            string dirtyRefusal;
            if (!CheckNoUnsavedChangesOrRefuse("Generate UniThumb", out dirtyRefusal))
            {
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Generate refused: another thumbnail generation is already in progress."
                );
                return;
            }

            try
            {
                if (!CanRunCapture())
                {
                    return;
                }

                CaptureResult result = CaptureScene(scenePath);
                if (!result.Success)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Capture failed for '"
                            + scenePath
                            + "': "
                            + (result.Warning ?? "unknown error.")
                    );
                    return;
                }

                bool saved = UniThumbStorage.Save(scenePath, result.PngBytes);
                if (!saved)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + scenePath
                            + "'. See Console for details."
                    );
                    return;
                }

                UniThumbIconService.ApplyIcon(scenePath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + scenePath + "'" + suffix);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        [MenuItem(k_GenerateMenuPath, true, k_GenerateMenuPriority)]
        private static bool ValidateGenerateUniThumb()
        {
            return IsSceneAssetSelected() || IsPrefabAssetSelected();
        }

        [MenuItem(k_ClearMenuPath, false, k_ClearMenuPriority)]
        public static void ClearUniThumb()
        {
            string prefabPath;
            if (TryGetSelectedPrefabPath(out prefabPath))
            {
                ClearPrefabThumbnail(prefabPath);
                return;
            }

            string scenePath;
            if (!TryGetSelectedScenePath(out scenePath))
            {
                Debug.LogWarning(
                    k_LogPrefix + "Clear UniThumb refused: select a SceneAsset first."
                );
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Clear refused: another thumbnail generation is already in progress."
                );
                return;
            }

            try
            {
                bool deleted = UniThumbStorage.Delete(scenePath);
                UniThumbIconService.ClearIcon(scenePath);
                string message = deleted
                    ? "Cleared thumbnail for '"
                    : "No thumbnail to clear for '";
                Debug.Log(k_LogPrefix + message + scenePath + "'.");
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        [MenuItem(k_ClearMenuPath, true, k_ClearMenuPriority)]
        private static bool ValidateClearUniThumb()
        {
            return IsSceneAssetSelected() || IsPrefabAssetSelected();
        }

        [MenuItem(k_RefreshAllMenuPath, false, k_RefreshAllMenuPriority)]
        public static void RefreshAllUniThumbs()
        {
            RunRefreshAll(false);
        }

        [MenuItem(k_RefreshStaleMenuPath, false, k_RefreshStaleMenuPriority)]
        public static void RefreshStaleUniThumbs()
        {
            RunRefreshAll(true);
        }

        /// <summary>
        /// Shared Refresh All core. False (default menu) queues only missing
        /// thumbnails plus the user selection; true (opt-in stale menu) also
        /// queues thumbnails whose source asset is newer than the stored PNG
        /// (see IsStale). Dialogs always run before the guard; the pump holds
        /// the guard until teardown. Scope defaults to All (legacy).
        /// </summary>
        private static void RunRefreshAll(bool includeStale)
        {
            RunRefreshAll(includeStale, BatchScope.All);
        }

        /// <summary>
        /// Shared Refresh All scoped core. Missing-only semantics unchanged;
        /// scope only filters which asset types are candidates. Work is
        /// collected BEFORE any dialog so counts match the queued work;
        /// dialogs always run before the guard; the pump holds the guard
        /// until teardown and the pre-collected list is reused by the pump.
        /// </summary>
        internal static void RunRefreshAll(bool includeStale, BatchScope scope)
        {
            string operationName = includeStale
                ? "Refresh Stale UniThumbs"
                : "Refresh All UniThumbs";
            string scopeSuffix =
                scope == BatchScope.ScenesOnly ? " (Scenes)"
                : scope == BatchScope.PrefabsOnly ? " (Prefabs)"
                : string.Empty;
            string logLabel = (includeStale ? "Refresh Stale" : "Refresh All") + scopeSuffix;
            string assetLabel =
                scope == BatchScope.ScenesOnly ? "scene(s)"
                : scope == BatchScope.PrefabsOnly ? "prefab(s)"
                : "scene/prefab(s)";
            // Abort-on-dirty pre-check FIRST: fail fast with no dialogs and no
            // guard held when unsaved changes would be discarded.
            string dirtyRefusal;
            if (!CheckNoUnsavedChangesOrRefuse(operationName, out dirtyRefusal))
            {
                return;
            }

            // Collect BEFORE any dialog so the confirm count matches the
            // queued work. A cancelled dialog or empty list leaves the guard
            // free and no pump scheduled.
            List<string> work;
            try
            {
                work = CollectRefreshWork(includeStale, scope);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return;
            }
            if (work.Count == 0)
            {
                Debug.Log(
                    k_LogPrefix
                        + logLabel
                        + ": no "
                        + assetLabel
                        + " need regeneration; nothing to do."
                );
                return;
            }

            // Confirmation gate before the guard: a cancelled dialog must leave
            // the guard free and no pump scheduled.
            if (
                !ConfirmBatchStart(
                    operationName + scopeSuffix,
                    "Regenerate thumbnails for "
                        + work.Count
                        + " "
                        + assetLabel
                        + "?"
                        + DiscardDisclosure,
                    "Regenerate"
                )
            )
            {
                Debug.Log(k_LogPrefix + logLabel + " cancelled by user.");
                return;
            }

            if (!ConfirmSceneViewAngleBatchWarning())
            {
                Debug.Log(k_LogPrefix + logLabel + " cancelled: Scene View angle warning.");
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + logLabel
                        + " refused: another thumbnail generation is already in progress."
                );
                return;
            }

            bool pumpStarted = false;
            try
            {
                if (!CanRunCapture())
                {
                    return;
                }

                StartBatchPump(work);
                pumpStarted = true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                // When the pump is running, the guard stays held until the pump
                // finishes (CompleteBatch/AbortBatch -> CancelBatchState). Only
                // non-pump exits release it here.
                if (!pumpStarted)
                {
                    UniThumbGuard.Exit();
                }
            }
        }

        [MenuItem(k_GenerateFolderMenuPath, false, k_GenerateFolderMenuPriority)]
        public static void GenerateFolderUniThumbs()
        {
            RunFolderBatchFromSelection(false);
        }

        [MenuItem(k_GenerateFolderMenuPath, true, k_GenerateFolderMenuPriority)]
        private static bool ValidateGenerateFolderUniThumbs()
        {
            return HasValidFolderSelection();
        }

        [MenuItem(k_GeneratePrefabFolderMenuPath, false, k_GeneratePrefabFolderMenuPriority)]
        public static void GeneratePrefabFolderUniThumbs()
        {
            RunFolderBatchFromSelection(true);
        }

        [MenuItem(k_GeneratePrefabFolderMenuPath, true, k_GeneratePrefabFolderMenuPriority)]
        private static bool ValidateGeneratePrefabFolderUniThumbs()
        {
            return HasValidFolderSelection();
        }

        /// <summary>
        /// Shared folder-batch entry for the Assets menus. False keeps the
        /// mixed scene+prefab flow verbatim; true filters to prefabs only.
        /// Guard is never held across dialogs; the pump holds it on success.
        /// Legacy wrapper over the BatchScope core.
        /// </summary>
        private static void RunFolderBatchFromSelection(bool prefabOnly)
        {
            RunFolderBatchFromSelection(prefabOnly ? BatchScope.PrefabsOnly : BatchScope.All);
        }

        /// <summary>
        /// Shared folder-batch entry scoped core. Guard is never held across
        /// dialogs; the pump holds it on success. Two-pass pump unchanged.
        /// </summary>
        private static void RunFolderBatchFromSelection(BatchScope scope)
        {
            List<string> folders = CollectSelectedFolders();
            if (folders.Count == 0)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + (
                            scope == BatchScope.PrefabsOnly
                                ? "Generate Prefab UniThumbs in Folder refused: select a folder in the Project window first."
                            : scope == BatchScope.ScenesOnly
                                ? "Generate Scene UniThumbs in Folder refused: select a folder in the Project window first."
                            : "Generate UniThumbs in Folder refused: select a folder in the Project window first."
                        )
                );
                return;
            }

            // Count the work and confirm BEFORE the guard: a cancelled dialog
            // must leave the guard free and no pump scheduled.
            int skipped;
            List<string> work;
            try
            {
                work = CollectFolderWork(folders, scope, out skipped);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return;
            }
            if (work.Count == 0)
            {
                Debug.Log(
                    k_LogPrefix
                        + (
                            scope == BatchScope.PrefabsOnly
                                ? "No prefabs found in folder; nothing to generate."
                            : scope == BatchScope.ScenesOnly
                                ? "No scenes found in folder; nothing to generate."
                            : "No scenes or prefabs found in folder; nothing to generate."
                        )
                );
                return;
            }
            // Abort-on-dirty pre-check BEFORE any dialog or the guard: a
            // refusal must leave the guard free and no pump scheduled.
            string operationName =
                scope == BatchScope.PrefabsOnly ? "Generate Prefab UniThumbs in Folder"
                : scope == BatchScope.ScenesOnly ? "Generate Scene UniThumbs in Folder"
                : "Generate UniThumbs in Folder";
            string dirtyRefusal;
            if (!CheckNoUnsavedChangesOrRefuse(operationName, out dirtyRefusal))
            {
                return;
            }
            string folderLabel =
                folders.Count == 1 ? "'" + folders[0] + "'" : folders.Count + " selected folders";
            string countLabel =
                scope == BatchScope.PrefabsOnly ? work.Count + " prefab(s) in "
                : scope == BatchScope.ScenesOnly ? work.Count + " scene(s) in "
                : work.Count + " scene/prefab(s) in ";
            if (
                !ConfirmBatchStart(
                    operationName,
                    "Generate thumbnails for " + countLabel + folderLabel + "?" + DiscardDisclosure,
                    "Generate"
                )
            )
            {
                Debug.Log(k_LogPrefix + operationName + " cancelled by user.");
                return;
            }

            if (!ConfirmSceneViewAngleBatchWarning())
            {
                Debug.Log(k_LogPrefix + operationName + " cancelled: Scene View angle warning.");
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + operationName
                        + " refused: another thumbnail generation is already in progress."
                );
                return;
            }

            bool pumpStarted = false;
            try
            {
                if (!CanRunCapture())
                {
                    return;
                }

                s_State.BatchKind = k_FolderBatchKind;
                s_State.SkippedCount = skipped;
                StartBatchPump(work);
                pumpStarted = true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                // Same guard protocol as Refresh All: the pump holds the guard
                // until CompleteBatch/AbortBatch -> CancelBatchState.
                if (!pumpStarted)
                {
                    UniThumbGuard.Exit();
                }
            }
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Opt-in override preserving the prior silent-discard throughput.
        /// Settings-asset backed (UniThumbSettings.AllowBatchDiscard), default
        /// off: while false, every generation entry point refuses to start
        /// when any open scene is dirty (see CheckNoUnsavedChangesOrRefuse,
        /// always called before UniThumbGuard.TryEnter so the guard is never
        /// held across the refusal). While true, batches run exactly as
        /// before, discarding unsaved changes on scene switches. Falls back
        /// to the legacy EditorPrefs value when the settings asset is
        /// unavailable. Kept as a property (not a plain settings read) so
        /// existing callers and tests keep working.
        /// </summary>
        internal static bool AllowDiscardUnsavedChanges
        {
            get
            {
                UniThumbSettings settings = LoadSettingsOrNull();
                if (settings == null)
                {
                    return EditorPrefs.GetBool(k_UnsavedDiscardOverrideKey, false);
                }
                MigrateLegacyDiscardOnce(settings);
                return settings.AllowBatchDiscard;
            }
            set
            {
                try
                {
                    UniThumbSettings settings = UniThumbSettings.Get();
                    if (settings != null)
                    {
                        settings.SetAllowBatchDiscard(value);
                    }
                }
                catch
                {
                    // Asset unavailable; EditorPrefs fallback below still applies.
                }
                EditorPrefs.SetBool(k_UnsavedDiscardOverrideKey, value);
            }
        }

        /// <summary>
        /// True when any open scene has unsaved changes. Test seam first,
        /// real EditorSceneManager dirty scan otherwise (read-only).
        /// </summary>
        internal static bool HasUnsavedSceneChanges()
        {
            if (UnsavedChangesProbeForTest != null)
            {
                return UnsavedChangesProbeForTest();
            }
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Abort-on-dirty gate for every generation entry point. Returns true
        /// when the batch may start (scene clean, or the opt-in override is
        /// on). Returns false with a user-facing refusal message otherwise;
        /// the refusal is also logged. Callers invoke this BEFORE
        /// UniThumbGuard.TryEnter and before any confirm dialog (fail fast,
        /// never hold the guard across UI).
        /// </summary>
        internal static bool CheckNoUnsavedChangesOrRefuse(
            string operationName,
            out string refusalMessage
        )
        {
            refusalMessage = null;
            if (!HasUnsavedSceneChanges() || AllowDiscardUnsavedChanges)
            {
                return true;
            }
            refusalMessage =
                k_LogPrefix
                + operationName
                + " refused: unsaved scene changes would be discarded. Save your scenes first, "
                + "or allow batch discard in the UniThumb window Settings tab (Tracking card).";
            Debug.LogWarning(refusalMessage);
            return false;
        }

        /// <summary>
        /// Play-mode exit reset (ExitingEditMode, idle only). Clears the test
        /// probe on every phase; clears the pump holder only when idle by
        /// reusing CancelBatchState. Skips the holder clear while a batch is
        /// running or the guard is held (never forces Guard.Exit mid-pump).
        /// SAFE (kept): k_* consts, IsBatchRunning derived view,
        /// AllowDiscardUnsavedChanges settings gate (must survive).
        /// Idempotent, null-safe, Editor-only.
        /// </summary>
        internal static void ResetForPlayModeExit()
        {
            UnsavedChangesProbeForTest = null;
            if (IsBatchRunning || UniThumbGuard.IsGenerating)
            {
                return;
            }
            CancelBatchState();
        }

        /// <summary>
        /// Loads the settings asset without throwing. Null when the asset
        /// pipeline is unavailable (the caller falls back to EditorPrefs).
        /// </summary>
        private static UniThumbSettings LoadSettingsOrNull()
        {
            try
            {
                return UniThumbSettings.Get();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One-time-per-session migration of pre-settings opt-ins: users who
        /// enabled batch discard via the old EditorPrefs-backed menu toggle
        /// keep their opt-in in the settings asset. Runs once so an explicit
        /// opt-out afterwards is never reverted.
        /// </summary>
        private static void MigrateLegacyDiscardOnce(UniThumbSettings settings)
        {
            if (s_MigratedLegacyDiscard)
            {
                return;
            }
            s_MigratedLegacyDiscard = true;
            try
            {
                if (EditorPrefs.GetBool(k_UnsavedDiscardOverrideKey, false))
                {
                    if (!settings.AllowBatchDiscard)
                    {
                        settings.SetAllowBatchDiscard(true);
                    }
                    EditorPrefs.DeleteKey(k_UnsavedDiscardOverrideKey);
                }
            }
            catch
            {
                // Migration is best-effort; the gate still works either way.
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Subscribes the idle-only play-mode reset. Unsubscribe-then-subscribe
        /// keeps re-registration idempotent across domain reloads.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Test probe clears on any phase (atomic delegate clear, no idle
            // guard); the pump holder below is ExitingEditMode + idle only.
            UnsavedChangesProbeForTest = null;
            if (change != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }
            if (IsBatchRunning || UniThumbGuard.IsGenerating)
            {
                return;
            }
            CancelBatchState();
        }

        private static bool IsSceneAssetSelected()
        {
            UnityEngine.Object active = Selection.activeObject;
            return active != null && active is SceneAsset;
        }

        /// <summary>
        /// True when the assets-relative path names a prefab asset. Pure
        /// string check (extension only), shared by the menu validators, the
        /// batch pump branch, and the window preview selection.
        /// </summary>
        internal static bool IsPrefabAssetPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrefabAssetSelected()
        {
            string prefabPath;
            return TryGetSelectedPrefabPath(out prefabPath);
        }

        internal static bool TryGetSelectedPrefabPath(out string prefabPath)
        {
            // An open Prefab Stage wins over the Project selection: with the
            // stage open Selection.activeObject is a scene object (no asset
            // path), so the selection-only check would miss prefab mode.
            string stagePrefabPath;
            if (UniThumbCapture.TryGetPrefabStageAssetPath(out stagePrefabPath))
            {
                prefabPath = stagePrefabPath;
                return true;
            }
            prefabPath = null;
            UnityEngine.Object active = Selection.activeObject;
            if (active == null || !(active is GameObject))
            {
                return false;
            }
            string path = AssetDatabase.GetAssetPath(active);
            if (!IsPrefabAssetPath(path))
            {
                return false;
            }
            prefabPath = path;
            return true;
        }

        /// <summary>
        /// Single-prefab generate path (Assets menu with a prefab selected, or
        /// the window Generate button with a prefab selected). Guard
        /// TryEnter/finally Exit around the whole block, mirroring the scene
        /// path. No dirty pre-check and no scene switching: CapturePrefab
        /// renders additively in the open scene without dirtying it, so no
        /// unsaved change can ever be discarded here.
        /// </summary>
        private static void GeneratePrefabThumbnail(string prefabPath)
        {
            // An open Prefab Stage wins over the selection path so capture
            // and save target the staged prefab (mirrors the window flow).
            string resolvedPrefabPath;
            if (UniThumbCapture.TryResolvePrefabAssetPath(prefabPath, out resolvedPrefabPath))
            {
                prefabPath = resolvedPrefabPath;
            }
            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Generate refused: another thumbnail generation is already in progress."
                );
                return;
            }

            try
            {
                if (!CanRunCapture())
                {
                    return;
                }

                CaptureSettings settings = ClampBulkResolution(
                    UniThumbCapture.GetLastSettingsOrDefault()
                );
                CaptureResult result = UniThumbCapture.CapturePrefab(prefabPath, settings);
                if (!result.Success)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Capture failed for '"
                            + prefabPath
                            + "': "
                            + (result.Warning ?? "unknown error.")
                    );
                    return;
                }

                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                bool saved = UniThumbStorage.SavePrefabThumbnail(guid, result.PngBytes);
                if (!saved)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + prefabPath
                            + "'. See Console for details."
                    );
                    return;
                }

                UniThumbIconService.ApplyIcon(prefabPath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + prefabPath + "'" + suffix);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        /// <summary>
        /// Single-prefab clear path (Assets menu with a prefab selected).
        /// Guard TryEnter/finally Exit around the whole block.
        /// </summary>
        private static void ClearPrefabThumbnail(string prefabPath)
        {
            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Clear refused: another thumbnail generation is already in progress."
                );
                return;
            }

            try
            {
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                bool deleted = UniThumbStorage.DeleteByGuid(guid);
                UniThumbIconService.ClearIcon(prefabPath);
                string message = deleted
                    ? "Cleared thumbnail for '"
                    : "No thumbnail to clear for '";
                Debug.Log(k_LogPrefix + message + prefabPath + "'.");
            }
            finally
            {
                UniThumbGuard.Exit();
            }
        }

        private static bool TryGetSelectedScenePath(out string scenePath)
        {
            scenePath = null;
            UnityEngine.Object active = Selection.activeObject;
            if (active == null || !(active is SceneAsset))
            {
                return false;
            }
            scenePath = AssetDatabase.GetAssetPath(active);
            return !string.IsNullOrEmpty(scenePath);
        }

        /// <summary>
        /// True when at least one selected asset is a project folder (DefaultAsset
        /// whose path AssetDatabase.IsValidFolder accepts). Non-folder selections
        /// keep the folder menu hidden/disabled.
        /// </summary>
        private static bool HasValidFolderSelection()
        {
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (obj is DefaultAsset)
                {
                    string path = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// All selected assets that are project folders, in Selection.objects
        /// order, duplicates removed. Empty when no valid folder is selected.
        /// </summary>
        private static List<string> CollectSelectedFolders()
        {
            var folders = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (!(obj is DefaultAsset))
                {
                    continue;
                }
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }
                if (seen.Add(path))
                {
                    folders.Add(path);
                }
            }
            return folders;
        }

        private static bool CanRunCapture()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Refused: play mode is active or about to change. Stop play mode first."
                );
                return false;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Refused: the editor is compiling or importing assets. Try again when it settles."
                );
                return false;
            }
            return true;
        }

        /// <summary>
        /// Opens the target scene when it is not the active scene, captures it, and
        /// restores the previous scene. Runs only after the abort-on-dirty
        /// pre-check passed, so unsaved changes are discarded before any
        /// switch only with the user's opt-in: generation never saves scene
        /// files and never shows a dialog.
        /// </summary>
        private static CaptureResult CaptureScene(string scenePath)
        {
            string originalPath = EditorSceneManager.GetActiveScene().path;
            bool switched = false;
            if (!string.Equals(originalPath, scenePath, StringComparison.Ordinal))
            {
                DiscardUnsavedSceneChanges();
                try
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    switched = true;
                }
                catch (Exception exception)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Warning = "Could not open scene '" + scenePath + "': " + exception.Message,
                    };
                }
            }

            try
            {
                CaptureSettings settings = ClampBulkResolution(
                    UniThumbCapture.GetLastSettingsOrDefault()
                );
                CaptureResult result = UniThumbCapture.Capture(settings);
                return result;
            }
            finally
            {
                if (switched)
                {
                    RestoreOriginalScene(originalPath, true);
                }
            }
        }

        /// <summary>
        /// Defensive bulk-resolution guard (t11/AC-M19): menu and batch capture
        /// paths may never exceed 2048px; single-scene window captures keep their
        /// 4096px ceiling. Batch defaults are 512px, so the clamp is normally
        /// dormant - it exists to be provable and to protect future resolution
        /// plumbing. Clamps BOTH axes to the cap and logs the clamp.
        /// </summary>
        internal static CaptureSettings ClampBulkResolution(CaptureSettings settings)
        {
            int clampedWidth = Math.Min(settings.Width, k_MaxBulkResolution);
            int clampedHeight = Math.Min(settings.Height, k_MaxBulkResolution);
            if (clampedWidth != settings.Width || clampedHeight != settings.Height)
            {
                Debug.Log(
                    k_LogPrefix
                        + "Resolution "
                        + settings.Width
                        + "x"
                        + settings.Height
                        + " clamped to "
                        + clampedWidth
                        + "x"
                        + clampedHeight
                        + " for bulk generation."
                );
                settings.Width = clampedWidth;
                settings.Height = clampedHeight;
            }
            return settings;
        }

        /// <summary>
        /// Candidate set for Refresh All: enabled EditorBuildSettings scenes PLUS
        /// every scene from the cached t:Scene guids PLUS every
        /// prefab from the cached t:Prefab guids (see GetCachedGuids). Returns only the entries that
        /// are explicitly requested for regeneration: missing (no thumbnail in
        /// storage, existence-only check, identical for scenes and prefabs) or
        /// the user-selected scene/prefab. Out-of-project scenes are skipped
        /// with a warning, never captured. Prefab semantics match scenes
        /// one-to-one: Refresh All never regenerates up-to-date prefabs, it
        /// only fills gaps (folder batch is the explicit-regenerate-everything
        /// path for prefabs too).
        /// </summary>
        private static List<string> CollectRefreshWork()
        {
            return CollectRefreshWork(RefreshIncludesStale);
        }

        /// <summary>
        /// Refresh All work list. False (default) keeps the historical
        /// missing-only contract: only IsMissing entries plus the user
        /// selection queue, with identical logs and counts. True (opt-in)
        /// additionally queues stale entries (see IsStale). Legacy wrapper
        /// over the BatchScope core (All).
        /// </summary>
        internal static List<string> CollectRefreshWork(bool includeStale)
        {
            return CollectRefreshWork(includeStale, BatchScope.All);
        }

        /// <summary>
        /// Refresh All scoped work list. Missing-only semantics unchanged;
        /// scope only filters candidate asset types: ScenesOnly skips prefab
        /// candidates, PrefabsOnly skips scene candidates (build scenes plus
        /// t:Scene), All keeps the historical mixed set. The user selection
        /// is honoured only when it matches the scope.
        /// </summary>
        internal static List<string> CollectRefreshWork(bool includeStale, BatchScope scope)
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            if (IncludesScenes(scope))
            {
                EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
                foreach (EditorBuildSettingsScene buildScene in buildScenes)
                {
                    if (buildScene == null || !buildScene.enabled)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(buildScene.path))
                    {
                        candidates.Add(buildScene.path);
                    }
                }
                string[] foundGuids = GetCachedGuids("t:Scene");
                foreach (string guid in foundGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        candidates.Add(path);
                    }
                }
            }
            if (IncludesPrefabs(scope))
            {
                string[] foundPrefabGuids = GetCachedGuids("t:Prefab");
                foreach (string guid in foundPrefabGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        candidates.Add(path);
                    }
                }
            }

            string selectedPath = null;
            bool hasSelection = false;
            if (IncludesScenes(scope))
            {
                string selectedScenePath;
                if (TryGetSelectedScenePath(out selectedScenePath))
                {
                    hasSelection = true;
                    selectedPath = selectedScenePath;
                }
            }
            if (IncludesPrefabs(scope))
            {
                string selectedPrefabPath;
                if (TryGetSelectedPrefabPath(out selectedPrefabPath))
                {
                    hasSelection = true;
                    selectedPath = selectedPrefabPath;
                }
            }

            var work = new List<string>();
            int skippedOutside = 0;
            int upToDate = 0;
            foreach (string scenePath in candidates)
            {
                if (!IsInProject(scenePath))
                {
                    skippedOutside++;
                    continue;
                }
                if (
                    IsMissing(scenePath)
                    || (includeStale && IsStale(scenePath))
                    || (
                        hasSelection
                        && string.Equals(scenePath, selectedPath, StringComparison.Ordinal)
                    )
                )
                {
                    work.Add(scenePath);
                }
                else
                {
                    upToDate++;
                }
            }

            Debug.Log(
                k_LogPrefix
                    + (includeStale ? "Refresh Stale" : "Refresh All")
                    + (
                        scope == BatchScope.ScenesOnly ? " (Scenes): "
                        : scope == BatchScope.PrefabsOnly ? " (Prefabs): "
                        : ": "
                    )
                    + work.Count
                    + " to regenerate, "
                    + upToDate
                    + " up to date, "
                    + skippedOutside
                    + " skipped (out of project)."
            );
            return work;
        }

        /// <summary>
        /// EVERY scene AND prefab under the selected folders, regardless of
        /// thumbnail presence: the
        /// user explicitly requested these folders, so all scenes and prefabs
        /// in them regenerate (unlike project-wide Refresh All, which is
        /// missing-only). Asset guids come from the shared project-wide cache
        /// (see GetCachedGuids: 1x t:Scene + 1x t:Prefab per batch) filtered
        /// by folder prefix in managed code; a
        /// defensive prefix check guarantees nothing outside a folder is
        /// queued. Out-of-project paths are counted as skipped, never queued.
        /// An entry is queued once even when nested folder selections overlap.
        /// Legacy wrapper over the BatchScope core (All).
        /// </summary>
        private static List<string> CollectFolderWork(List<string> folderPaths, out int skipped)
        {
            return CollectFolderWork(folderPaths, BatchScope.All, out skipped);
        }

        /// <summary>
        /// Folder collector with a prefab-only filter. False collects every
        /// scene AND prefab (mixed folder batch, unchanged); true collects
        /// prefabs only (prefab-only folder batch). Project-wide cached guids
        /// filtered per folder, deduped, out-of-project entries skipped.
        /// Legacy wrapper over the BatchScope core.
        /// </summary>
        private static List<string> CollectFolderWork(
            List<string> folderPaths,
            bool prefabOnly,
            out int skipped
        )
        {
            return CollectFolderWork(
                folderPaths,
                prefabOnly ? BatchScope.PrefabsOnly : BatchScope.All,
                out skipped
            );
        }

        /// <summary>
        /// Folder scoped core: All collects every scene AND prefab; ScenesOnly
        /// collects scenes; PrefabsOnly collects prefabs. Project-wide cached
        /// guids filtered per folder, deduped, out-of-project entries skipped.
        /// </summary>
        internal static List<string> CollectFolderWork(
            List<string> folderPaths,
            BatchScope scope,
            out int skipped
        )
        {
            var work = new HashSet<string>(StringComparer.Ordinal);
            skipped = 0;
            if (folderPaths == null)
            {
                return new List<string>(work);
            }
            foreach (string folderPath in folderPaths)
            {
                if (string.IsNullOrEmpty(folderPath))
                {
                    continue;
                }
                if (IncludesScenes(scope))
                {
                    CollectFolderWorkForType(folderPath, "t:Scene", work, ref skipped);
                }
                if (IncludesPrefabs(scope))
                {
                    CollectFolderWorkForType(folderPath, "t:Prefab", work, ref skipped);
                }
            }
            return new List<string>(work);
        }

        private static void CollectFolderWorkForType(
            string folderPath,
            string filter,
            HashSet<string> work,
            ref int skipped
        )
        {
            // Project-wide guids (fresh outside the pump, cached per type
            // while the pump runs: one FindAssets per type per batch no
            // matter how many folders are selected) filtered by folder prefix
            // in managed code. The defensive IsInsideFolder prefix check is
            // now the primary filter, so paths outside the requested folder
            // are silently ignored (never queued, never counted): the scoped
            // query could never return them, so skipping them silently keeps
            // the work set and the skipped count identical. In-folder but
            // out-of-project paths keep the skipped count (no warning).
            string[] foundGuids = GetCachedGuids(filter);
            foreach (string guid in foundGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                if (!IsInsideFolder(path, folderPath))
                {
                    continue;
                }
                if (!IsInProject(path))
                {
                    skipped++;
                    continue;
                }
                work.Add(path);
            }
        }

        /// <summary>
        /// Project-wide asset guid lookup shared by every batch collector
        /// (Refresh, folder, prefab-only). Pump-scoped: the cached result is
        /// reused only while the batch pump is running (IsBatchRunning);
        /// every public collector entry point (CollectFolderScenePaths,
        /// CollectFolderPrefabPaths, CollectRefreshWork, folder-batch counting)
        /// runs outside the pump and always queries fresh, so assets created
        /// after a previous query are never hidden by the 60s window.
        /// Invalidation rule: time-based within a pump (k_AssetCacheSeconds),
        /// explicit InvalidateAssetCache (called at StartBatchPump so each
        /// batch starts fresh, then reuses 1x t:Scene + 1x t:Prefab for the
        /// rest of the pump), and the DisableAssetCache bypass for rollback.
        /// Content and order match a fresh FindAssets call outside the pump,
        /// so collector outputs (including dedupe/skip counts) are identical
        /// to baseline.
        /// </summary>
        internal static string[] GetCachedGuids(string filter)
        {
            bool isSceneFilter = string.Equals(filter, "t:Scene", StringComparison.Ordinal);
            if (!s_State.DisableAssetCache && IsBatchRunning)
            {
                string[] cached = isSceneFilter
                    ? s_State.CachedSceneGuids
                    : s_State.CachedPrefabGuids;
                if (cached != null)
                {
                    double age = EditorApplication.timeSinceStartup - s_State.AssetCacheTick;
                    if (age >= 0.0 && age < k_AssetCacheSeconds)
                    {
                        return cached;
                    }
                }
            }
            string[] guids = AssetDatabase.FindAssets(filter);
            if (isSceneFilter)
            {
                s_State.CachedSceneGuids = guids;
            }
            else
            {
                s_State.CachedPrefabGuids = guids;
            }
            s_State.AssetCacheTick = EditorApplication.timeSinceStartup;
            return guids;
        }

        /// <summary>
        /// Clears the batch asset guid cache; the next collector re-queries.
        /// </summary>
        internal static void InvalidateAssetCache()
        {
            s_State.CachedSceneGuids = null;
            s_State.CachedPrefabGuids = null;
            s_State.AssetCacheTick = 0.0;
        }

        /// <summary>
        /// Public wrapper over the scoped CollectFolderWork core (All): every
        /// scene AND prefab under the folder (recursive cached FindAssets
        /// t:Scene + t:Prefab filtered by folder prefix, deduped,
        /// out-of-project entries skipped). Used by the window's Clear Folder
        /// Thumbnails flow for counting and deletion. Kept for backward
        /// compatibility; new callers use CollectFolderPaths with a scope.
        /// </summary>
        public static List<string> CollectFolderScenePaths(string folderPath)
        {
            return CollectFolderWork(new List<string> { folderPath }, BatchScope.All, out _);
        }

        /// <summary>
        /// Prefab-only folder collector: every prefab under the folder
        /// (recursive cached FindAssets t:Prefab filtered by folder prefix,
        /// deduped, out-of-project
        /// entries skipped). Scenes are never included. Wrapper over the
        /// scoped CollectFolderWork core (PrefabsOnly).
        /// </summary>
        public static List<string> CollectFolderPrefabPaths(string folderPath)
        {
            return CollectFolderWork(
                new List<string> { folderPath },
                BatchScope.PrefabsOnly,
                out _
            );
        }

        /// <summary>
        /// Scoped folder collector: All returns every scene AND prefab under
        /// the folder; ScenesOnly returns scenes; PrefabsOnly returns
        /// prefabs. Recursive cached FindAssets filtered by folder prefix,
        /// deduped, out-of-project entries skipped.
        /// </summary>
        public static List<string> CollectFolderPaths(string folderPath, BatchScope scope)
        {
            return CollectFolderWork(new List<string> { folderPath }, scope, out _);
        }

        /// <summary>
        /// True when the scope includes scene assets (All, ScenesOnly).
        /// </summary>
        internal static bool IncludesScenes(BatchScope scope)
        {
            return scope == BatchScope.All || scope == BatchScope.ScenesOnly;
        }

        /// <summary>
        /// True when the scope includes prefab assets (All, PrefabsOnly).
        /// </summary>
        internal static bool IncludesPrefabs(BatchScope scope)
        {
            return scope == BatchScope.All || scope == BatchScope.PrefabsOnly;
        }

        /// <summary>
        /// True when the scene path sits directly under the chosen folder (folder
        /// prefix + separator). Primary filter for the cached project-wide
        /// guids (see CollectFolderWorkForType).
        /// </summary>
        private static bool IsInsideFolder(string path, string folderPath)
        {
            string prefix = folderPath.TrimEnd('/') + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the scene has no thumbnail in storage (missing).
        /// Semantics are frozen: the default Refresh All path relies on this
        /// being an existence-only check.
        /// </summary>
        private static bool IsMissing(string scenePath)
        {
            return !UniThumbStorage.HasThumbnail(scenePath);
        }

        /// <summary>
        /// True when a thumbnail exists but the source asset is newer than the
        /// stored PNG (Storage tick compare, same clock as Load). Missing
        /// entries are NOT stale. Consulted only on the opt-in stale path;
        /// the default Refresh All path never calls it.
        /// </summary>
        internal static bool IsStale(string scenePath)
        {
            return UniThumbStorage.IsThumbnailStale(scenePath);
        }

        /// <summary>
        /// Simple prefix test: project assets live under "Assets/"; anything else
        /// (Packages/, asset-store/package cache paths) is out of project.
        /// </summary>
        private static bool IsInProject(string scenePath)
        {
            return !string.IsNullOrEmpty(scenePath)
                && scenePath.StartsWith("Assets/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Confirmation gate before any batch generation starts (folder batch
        /// menu, Refresh All menu, window folder button). Same idiom as the
        /// Clear Folder Thumbnails dialog. Callers invoke it BEFORE
        /// UniThumbGuard.TryEnter so a cancelled dialog leaves the guard
        /// free and no pump scheduled.
        /// </summary>
        private static bool ConfirmBatchStart(string title, string message, string confirm)
        {
            return EditorUtility.DisplayDialog(title, message, confirm, "Cancel");
        }

        /// <summary>
        /// Warns the user when "Use Scene View Angle" is enabled and they are
        /// about to start a batch. Scene View angle copies the current Scene
        /// View camera transform, which is tuned for one scene — when the batch
        /// switches to a different scene the camera may point at nothing,
        /// producing uniform-background captures. Returns true to proceed
        /// (user acknowledged or the setting is off), false to cancel.
        /// </summary>
        private static bool ConfirmSceneViewAngleBatchWarning()
        {
            CaptureSettings settings = UniThumbCapture.GetLastSettingsOrDefault();
            if (!settings.UseSceneViewAngle)
            {
                return true;
            }
            return EditorUtility.DisplayDialog(
                "Scene View Angle in Batch Mode",
                "\"Use Scene View Angle\" is enabled. "
                    + "This copies the current Scene View camera position, which is "
                    + "tuned for one scene. When the batch switches to other scenes "
                    + "the camera may point at nothing, producing empty thumbnails.\n\n"
                    + "Switch to automatic framing (disable \"Use Scene View Angle\") "
                    + "for reliable batch results."
                    + DiscardDisclosure,
                "Continue Anyway",
                "Cancel"
            );
        }

        /// <summary>
        /// Kicks off the async batch pump. The guard entered by the menu handler
        /// stays held until the pump completes or aborts.
        /// </summary>
        private static void StartBatchPump(List<string> work)
        {
            // Snapshot once at pump start: ProcessSceneCapture must not re-read
            // the store per scene (prevents mid-batch drift from UI edits).
            s_State.BatchSettings = UniThumbCapture.GetLastSettingsOrDefault();
            // Fresh asset guids for this batch; collectors and any late
            // re-collection then reuse 1x t:Scene + 1x t:Prefab (see
            // GetCachedGuids) for the rest of the pump.
            InvalidateAssetCache();
            // Fresh volume guids for this batch: the 30s cache covers a pump,
            // but must not leak across batches.
            UniThumbCapture.InvalidateVolumeProfileCache();
            s_State.PendingScenes = new Queue<string>(work);
            s_State.SucceededScenes = new List<string>();
            s_State.FailedScenes = new List<string>();
            s_State.TotalScenes = work.Count;
            s_State.ProcessedCount = 0;
            s_State.OriginalScenePath = EditorSceneManager.GetActiveScene().path;
            s_State.SwitchedScenes = false;
            s_State.WroteThumbnails = false;
            EditorApplication.update += OnBatchUpdate;
        }

        /// <summary>
        /// One scene per EditorApplication.update tick: the frame between ticks is
        /// the required M9 yield. Scene handling is two-phase: phase A opens the
        /// scene and arms the shader-compile wait, phase B (later ticks, once
        /// ShaderUtil.anythingCompiling settles or the timeout fires) captures.
        /// Rendering on the same tick as OpenScene would sample half-compiled URP
        /// variants (pink materials). When the queue drains, CompleteBatch runs
        /// pass 2 (icons).
        ///
        /// Single-scene-per-tick is frozen (P-L2): a batch-size knob was evaluated
        /// and SKIPPED - phase A arms the compile wait and phase B captures on a
        /// later tick, so dequeuing N scenes per tick would capture on the
        /// OpenScene tick and break the pink-material wait. Rollback note: on any
        /// pump regression keep this single-tick pump; do not batch dequeues.
        /// </summary>
        private static void OnBatchUpdate()
        {
            try
            {
                if (s_State.PendingScenes == null)
                {
                    CancelBatchState();
                    return;
                }
                if (s_State.CancelRequested)
                {
                    CancelBatch();
                    return;
                }
                if (s_State.WaitingForShaderCompile)
                {
                    // Phase B: the current scene was opened on an earlier tick.
                    // Capture only once the async shader compile settles - never
                    // on the OpenScene tick.
                    if (ShaderCompileSettled())
                    {
                        s_State.WaitingForShaderCompile = false;
                        s_State.WaitStartedAt = 0.0;
                        if (
                            ShowProgress(
                                string.Format(k_ProgressMessageFormat, s_State.CurrentScenePath)
                            )
                        )
                        {
                            s_State.CancelRequested = true;
                            return;
                        }
                        ProcessSceneCapture(s_State.CurrentScenePath);
                    }
                    else
                    {
                        if (
                            ShowProgress(
                                string.Format(
                                    k_ShaderCompileProgressMessageFormat,
                                    s_State.CurrentScenePath
                                )
                            )
                        )
                        {
                            s_State.CancelRequested = true;
                            return;
                        }
                    }
                    return;
                }
                if (s_State.PendingScenes.Count == 0)
                {
                    CompleteBatch();
                    return;
                }
                string scenePath = s_State.PendingScenes.Dequeue();
                s_State.ProcessedCount++;
                s_State.CurrentScenePath = scenePath;
                if (IsPrefabAssetPath(scenePath))
                {
                    // Prefab entries skip the scene-switch and shader-compile
                    // wait: CapturePrefab renders additively in the open scene,
                    // so the capture runs on this tick (no phase B).
                    if (ShowProgress(string.Format(k_ProgressMessageFormat, scenePath)))
                    {
                        s_State.CancelRequested = true;
                        return;
                    }
                    ProcessPrefabCapture(scenePath);
                    return;
                }
                // Phase A: open the scene and arm the compile wait; no capture on
                // this tick.
                ProcessScene(scenePath);
                if (s_State.WaitingForShaderCompile)
                {
                    if (
                        ShowProgress(string.Format(k_ShaderCompileProgressMessageFormat, scenePath))
                    )
                    {
                        s_State.CancelRequested = true;
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                AbortBatch();
            }
        }

        /// <summary>
        /// Phase A of one pump step: switches to the target scene, then arms the
        /// shader-compile wait so the capture runs on a later tick (phase B,
        /// ProcessSceneCapture). Failed switches are recorded, never thrown.
        /// </summary>
        private static void ProcessScene(string scenePath)
        {
            try
            {
                if (!TryActivateScene(scenePath))
                {
                    s_State.FailedScenes.Add(scenePath);
                    return;
                }
                s_State.WaitingForShaderCompile = true;
                s_State.WaitStartedAt = EditorApplication.timeSinceStartup;
            }
            catch (Exception exception)
            {
                s_State.FailedScenes.Add(scenePath);
                Debug.LogWarning(
                    k_LogPrefix
                        + "Unexpected error while switching to '"
                        + scenePath
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Phase B of one pump step: capture + save + verify the already-open
        /// scene. Runs only after the shader-compile wait settled (or timed out).
        /// Per-scene results are logged so partial failures are never silent.
        /// </summary>
        private static void ProcessSceneCapture(string scenePath)
        {
            try
            {
                CaptureSettings settings = ClampBulkResolution(s_State.BatchSettings);
                CaptureResult result = UniThumbCapture.Capture(settings);
                if (!result.Success)
                {
                    s_State.FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Capture failed for '"
                            + scenePath
                            + "': "
                            + (result.Warning ?? "unknown error.")
                    );
                    return;
                }

                bool saved = UniThumbStorage.Save(scenePath, result.PngBytes);
                if (!saved)
                {
                    s_State.FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + scenePath
                            + "'. See Console for details."
                    );
                    return;
                }
                s_State.WroteThumbnails = true;

                // Verification: the PNG must be decodable as a Texture2D, not just
                // written to disk. Load returns the texture Save just cached (no new
                // LoadImage in the common path); the dropped reference is cache-owned
                // and destroyed on eviction (Save/Delete) or the
                // domain-reload clear - never a transient texture, never a leak.
                if (UniThumbStorage.Load(scenePath) == null)
                {
                    s_State.FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Verification failed for '"
                            + scenePath
                            + "': thumbnail is missing or not importable."
                    );
                    return;
                }

                s_State.SucceededScenes.Add(scenePath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + scenePath + "'" + suffix);
            }
            catch (Exception exception)
            {
                s_State.FailedScenes.Add(scenePath);
                Debug.LogWarning(
                    k_LogPrefix
                        + "Unexpected error while generating for '"
                        + scenePath
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Prefab variant of ProcessSceneCapture: CapturePrefab renders
        /// additively in the already-open scene (no scene switch, no shader
        /// wait), persists via SavePrefabThumbnail keyed by the prefab GUID,
        /// and verifies via LoadPrefabThumbnail. Per-entry results are logged
        /// so partial failures are never silent.
        /// </summary>
        private static void ProcessPrefabCapture(string prefabPath)
        {
            try
            {
                CaptureSettings settings = ClampBulkResolution(s_State.BatchSettings);
                CaptureResult result = UniThumbCapture.CapturePrefab(prefabPath, settings);
                if (!result.Success)
                {
                    s_State.FailedScenes.Add(prefabPath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Capture failed for '"
                            + prefabPath
                            + "': "
                            + (result.Warning ?? "unknown error.")
                    );
                    return;
                }

                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                bool saved = UniThumbStorage.SavePrefabThumbnail(guid, result.PngBytes);
                if (!saved)
                {
                    s_State.FailedScenes.Add(prefabPath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + prefabPath
                            + "'. See Console for details."
                    );
                    return;
                }
                s_State.WroteThumbnails = true;

                // Verification: the PNG must be decodable as a Texture2D, not
                // just written to disk (cache-owned reference, never a leak).
                if (UniThumbStorage.LoadPrefabThumbnail(guid) == null)
                {
                    s_State.FailedScenes.Add(prefabPath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Verification failed for '"
                            + prefabPath
                            + "': thumbnail is missing or not importable."
                    );
                    return;
                }

                s_State.SucceededScenes.Add(prefabPath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + prefabPath + "'" + suffix);
            }
            catch (Exception exception)
            {
                s_State.FailedScenes.Add(prefabPath);
                Debug.LogWarning(
                    k_LogPrefix
                        + "Unexpected error while generating for '"
                        + prefabPath
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// True when the async shader compile kicked off by the scene switch has
        /// settled, or the k_ShaderCompileTimeout cap fired (capture proceeds
        /// anyway, warned). Phase B only runs on ticks after the OpenScene that
        /// armed the wait, so at least one update tick always elapses before the
        /// first poll.
        /// </summary>
        private static bool ShaderCompileSettled()
        {
            if (!IsShaderCompiling())
            {
                return true;
            }
            if (EditorApplication.timeSinceStartup - s_State.WaitStartedAt > k_ShaderCompileTimeout)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Shader compilation did not settle within "
                        + k_ShaderCompileTimeout
                        + "s; capturing '"
                        + s_State.CurrentScenePath
                        + "' anyway (pink materials possible)."
                );
                return true;
            }
            return false;
        }

        /// <summary>
        /// Timer-only fallback for the shader-compile wait: ShaderUtil is an
        /// internal editor API that may be unavailable in some Unity versions.
        /// When its state cannot be read, only the timeout governs the wait.
        /// </summary>
        internal static bool IsShaderCompiling()
        {
            try
            {
                return ShaderUtil.anythingCompiling;
            }
            catch (Exception)
            {
                return EditorApplication.timeSinceStartup - s_State.WaitStartedAt
                    <= k_ShaderCompileTimeout;
            }
        }

        /// <summary>
        /// Progress bar with the current processed/total fraction; callers pass
        /// the message (normal or shader-compile-wait).
        /// </summary>
        private static bool ShowProgress(string message)
        {
            float progress =
                s_State.TotalScenes > 0 ? s_State.ProcessedCount / (float)s_State.TotalScenes : 1f;
            return EditorUtility.DisplayCancelableProgressBar(k_ProgressTitle, message, progress);
        }

        /// <summary>
        /// Switches the active scene to the target when needed. Runs only after
        /// the abort-on-dirty pre-check passed, so unsaved changes are
        /// discarded first (DiscardUnsavedSceneChanges) only with the user's
        /// opt-in, and auto-changes triggered by opening scenes never raise
        /// Unity's save dialog or pause the batch.
        /// </summary>
        private static bool TryActivateScene(string scenePath)
        {
            string activePath = EditorSceneManager.GetActiveScene().path;
            if (string.Equals(activePath, scenePath, StringComparison.Ordinal))
            {
                return true;
            }

            DiscardUnsavedSceneChanges();

            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                s_State.SwitchedScenes = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix + "Could not open scene '" + scenePath + "': " + exception.Message
                );
                return false;
            }
        }

        /// <summary>
        /// Graceful cancel path: pass 2 (icons for verified scenes) still runs
        /// as a single ReapplyAllIcons sweep (one Project window repaint),
        /// then the summary reports "cancelled after k of N scenes" and
        /// teardown happens in the finally, exactly like CompleteBatch.
        /// </summary>
        private static void CancelBatch()
        {
            try
            {
                if (s_State.SucceededScenes != null && s_State.SucceededScenes.Count > 0)
                {
                    UniThumbIconService.ReapplyAllIcons();
                }
                string summary =
                    k_LogPrefix
                    + "Batch cancelled after "
                    + s_State.ProcessedCount
                    + " of "
                    + s_State.TotalScenes
                    + " scenes: "
                    + s_State.SucceededScenes.Count
                    + " generated, "
                    + s_State.FailedScenes.Count
                    + " failed, "
                    + s_State.SkippedCount
                    + " skipped.";
                Debug.Log(summary);
            }
            finally
            {
                RestoreOriginalScene(s_State.OriginalScenePath, s_State.SwitchedScenes);
                CancelBatchState();
            }
        }

        /// <summary>
        /// Pass 2: icons are applied once for the verified batch via a single
        /// ReapplyAllIcons sweep (one overlay rebuild, one Project window
        /// repaint), then the summary is logged and everything is torn
        /// down (progress bar, guard, subscription) in the finally.
        ///
        /// Single pass-2 icon sweep (P-L3, frozen): the capture pump (pass 1)
        /// never repaints per item - display is owned by this one sweep, which
        /// runs once after the queue drains. Kept as-is: collapsing the sweep
        /// into a single repaint would change display timing, so that stretch
        /// item is skipped.
        /// </summary>
        private static void CompleteBatch()
        {
            try
            {
                if (s_State.SucceededScenes != null && s_State.SucceededScenes.Count > 0)
                {
                    UniThumbIconService.ReapplyAllIcons();
                }

                string summary;
                if (s_State.BatchKind == k_FolderBatchKind)
                {
                    summary =
                        k_LogPrefix
                        + "Folder batch: "
                        + s_State.SucceededScenes.Count
                        + " generated, "
                        + (s_State.FailedScenes.Count + s_State.SkippedCount)
                        + " skipped/failed.";
                }
                else
                {
                    summary =
                        k_LogPrefix
                        + "Batch complete: "
                        + s_State.SucceededScenes.Count
                        + " succeeded, "
                        + s_State.FailedScenes.Count
                        + " failed of "
                        + s_State.TotalScenes
                        + " requested.";
                }
                if (s_State.FailedScenes.Count > 0)
                {
                    Debug.LogWarning(summary);
                }
                else
                {
                    Debug.Log(summary);
                }
            }
            finally
            {
                RestoreOriginalScene(s_State.OriginalScenePath, s_State.SwitchedScenes);
                CancelBatchState();
            }
        }

        private static void AbortBatch()
        {
            Debug.LogWarning(k_LogPrefix + "Batch aborted after an unexpected error.");
            RestoreOriginalScene(s_State.OriginalScenePath, s_State.SwitchedScenes);
            CancelBatchState();
        }

        /// <summary>
        /// Always leaves the editor on the scene the user was working on before the
        /// batch started. Unsaved changes in the last processed scene are silently
        /// discarded first so the final switch cannot raise a save dialog either.
        /// Failures here are logged, never thrown.
        /// </summary>
        private static void RestoreOriginalScene(string originalPath, bool switchedScenes)
        {
            if (!switchedScenes)
            {
                return;
            }
            DiscardUnsavedSceneChanges();
            try
            {
                if (string.IsNullOrEmpty(originalPath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(originalPath, OpenSceneMode.Single);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix + "Could not restore the previous scene: " + exception.Message
                );
            }
        }

        /// <summary>
        /// Silently discards unsaved changes across every open scene so scene
        /// switches during generation never raise Unity's "save modified scenes"
        /// dialog. Runs only after the abort-on-dirty pre-check passed (clean
        /// scene, or the AllowBatchDiscardUnsavedChanges opt-in override):
        /// by the time this is called the user has either saved or opted in.
        /// Opening a scene can auto-trigger modifications (script fixes,
        /// serialized-data upgrades) that would otherwise pause the batch on each
        /// switch; those changes are thrown away, matching an unconditional
        /// Don't-Save answer. A single-mode empty NewScene replaces all open
        /// scenes without saving and without any dialog; the caller immediately
        /// opens the intended scene into a clean editor. Scenes already clean are
        /// left untouched.
        /// </summary>
        private static void DiscardUnsavedSceneChanges()
        {
            bool anyDirty = false;
            for (int i = 0; i < EditorSceneManager.sceneCount && !anyDirty; i++)
            {
                anyDirty = EditorSceneManager.GetSceneAt(i).isDirty;
            }
            if (!anyDirty)
            {
                return;
            }
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix + "Could not discard unsaved scene changes: " + exception.Message
                );
            }
        }

        /// <summary>
        /// Shared teardown for every pump exit path: progress bar cleared, update
        /// subscription removed, state reset, guard released. Ends with exactly
        /// one AssetDatabase.Refresh when this cycle wrote thumbnails while the
        /// active storage mode was TrackedInAssets (new PNGs under Assets need a
        /// single import pass); Library mode and write-free cycles never refresh.
        /// </summary>
        private static void CancelBatchState()
        {
            try
            {
                EditorUtility.ClearProgressBar();
            }
            finally
            {
                // Release the guard FIRST so the tool never stays stuck if
                // a fallible teardown call throws (the root cause of the
                // stuck-guard bug). Exit is unconditional and idempotent.
                UniThumbGuard.Exit();

                EditorApplication.update -= OnBatchUpdate;
                s_State.PendingScenes = null;
                s_State.SucceededScenes = null;
                s_State.FailedScenes = null;
                s_State.TotalScenes = 0;
                s_State.ProcessedCount = 0;
                s_State.OriginalScenePath = null;
                s_State.SwitchedScenes = false;
                s_State.BatchKind = null;
                s_State.SkippedCount = 0;
                s_State.BatchSettings = default(CaptureSettings);
                s_State.CurrentScenePath = null;
                s_State.CancelRequested = false;
                s_State.WaitingForShaderCompile = false;
                s_State.WaitStartedAt = 0.0;
                s_State.WroteThumbnails = false;

                // Best-effort refresh for TrackedInAssets: wrap in try/catch
                // so a failure here cannot block the remaining teardown.
                try
                {
                    if (UniThumbSettings.Get().StorageMode == StorageMode.TrackedInAssets)
                    {
                        AssetDatabase.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        "[UniThumb] AssetDatabase.Refresh after batch failed: " + ex.Message
                    );
                }
            }
        }

        #endregion
    }
}
