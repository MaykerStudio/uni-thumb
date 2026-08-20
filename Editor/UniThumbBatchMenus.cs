using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Manual-only integration: context menus (Generate / Clear / Refresh All /
    /// Generate in Folder) and the build-scenes batch. These menus plus the
    /// window button are the ONLY
    /// capture entry points in the tool — there are zero asset/scene hooks
    /// (no OnPostprocessAllAssets, no EditorSceneManager.sceneSaving/sceneSaved),
    /// so nothing ever generates automatically. Every handler enters the shared
    /// UniThumbGuard (M2, manual paths) to prevent double-trigger from
    /// multi-select context menu clicks.
    ///
    /// Batch (Refresh All) is two-pass: pass 1 captures + saves + verifies every
    /// requested scene (progress bar, one EditorApplication.update frame yielded
    /// between scenes, plus a shader-compile wait after each scene switch so a
    /// render never samples half-compiled URP variants), pass 2 applies icons
    /// for verified PNGs only. Refresh All
    /// regenerates ONLY explicitly requested scenes: stale (missing/outdated per
    /// UniThumbStorage invalidation) or the user-selected scene — never
    /// unrequested scenes. The folder batch regenerates EVERY scene under the
    /// selected folders (an explicit request, not stale-only) and reuses the
    /// same two-pass pump. Scenes outside the project (Packages/ etc.) are
    /// skipped with a warning, never captured. The active scene is switched per
    /// target scene and the original scene is restored when the batch finishes.
    /// </summary>
    public static class UniThumbBatchMenus
    {
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
        private const string k_GenerateFolderMenuPath = "Assets/Generate UniThumbs in Folder";
        private const int k_GenerateMenuPriority = 1100;
        private const int k_ClearMenuPriority = 1101;
        private const int k_RefreshAllMenuPriority = 1102;
        private const int k_GenerateFolderMenuPriority = 1103;
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

        // Mirror of UniThumbStorage's invalidation key schema (k_PrefsPrefix
        // + "." + sceneGuid). Storage owns the schema; this copy lets Refresh All
        // decide staleness (outdated = scene LastWriteTimeUtc != stored ticks)
        // without touching storage internals. v3: bumped when CaptureUi defaulted
        // to true (UI canvases render into thumbnails); keep in sync on any bump.
        // Bumped v3 -> v4 when the UI composite feature changed the capture default
        // look (UI rendered at SceneView aspect when CaptureUi + UseSceneViewAngle
        // are both on), so existing thumbnails regenerate with the new layout.
        private const string k_PrefsPrefix = "SceneThumbs.v4";

        #endregion

        #region Fields

        /// <summary>
        /// All mutable batch-pump state lives on this readonly holder so the
        /// class keeps zero static mutable fields (Asset Store Validator
        /// "Check Static Variables"). Domain-reload semantics are unchanged:
        /// a fresh holder is created on reload, exactly like the old statics.
        /// </summary>
        private static readonly BatchState s_State = new BatchState();

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
        /// discovery, every scene regenerates - not stale-only). Validates the
        /// assets-relative path, asks for user confirmation, then enters the
        /// shared guard and the capture-readiness checks like the menu handlers
        /// before starting the pump. Returns false with a user-facing error
        /// string on any refusal or cancellation. The pump holds the guard
        /// until CompleteBatch/AbortBatch/CancelBatch -> CancelBatchState; every
        /// pre-pump exit releases it exactly once here (never a double Exit).
        /// </summary>
        public static bool TryStartFolderBatch(string folderPath, out string error)
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
                work = CollectFolderWork(new List<string> { folderPath }, out skipped);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                error = "Unexpected error while starting the batch: " + exception.Message;
                return false;
            }
            if (work.Count == 0)
            {
                error = "No scenes found in folder '" + folderPath + "'.";
                return false;
            }
            if (
                !ConfirmBatchStart(
                    "Generate UniThumbs",
                    "Generate thumbnails for " + work.Count + " scene(s) in '" + folderPath + "'?",
                    "Generate"
                )
            )
            {
                error = "Batch generation cancelled by user.";
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
            string scenePath;
            if (!TryGetSelectedScenePath(out scenePath))
            {
                Debug.LogWarning(
                    k_LogPrefix + "Generate UniThumb refused: select a SceneAsset first."
                );
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

                if (!UniThumbStorage.Save(scenePath, result.PngBytes))
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + scenePath
                            + "'. See Console for details."
                    );
                    return;
                }

                UniThumbStorage.SaveFingerprint(scenePath, UniThumbFingerprint.Compute());

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
            return IsSceneAssetSelected();
        }

        [MenuItem(k_ClearMenuPath, false, k_ClearMenuPriority)]
        public static void ClearUniThumb()
        {
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
            return IsSceneAssetSelected();
        }

        [MenuItem(k_RefreshAllMenuPath, false, k_RefreshAllMenuPriority)]
        public static void RefreshAllUniThumbs()
        {
            // Confirmation gate before the guard: a cancelled dialog must leave
            // the guard free and no pump scheduled.
            if (
                !ConfirmBatchStart(
                    "Refresh All UniThumbs",
                    "Regenerate thumbnails for all scenes?",
                    "Regenerate"
                )
            )
            {
                Debug.Log(k_LogPrefix + "Refresh All cancelled by user.");
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Refresh All refused: another thumbnail generation is already in progress."
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

                List<string> work = CollectRefreshWork();
                if (work.Count == 0)
                {
                    Debug.Log(
                        k_LogPrefix + "Refresh All: no scenes need regeneration; nothing to do."
                    );
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
            List<string> folders = CollectSelectedFolders();
            if (folders.Count == 0)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Generate UniThumbs in Folder refused: select a folder in the Project window first."
                );
                return;
            }

            // Count the work and confirm BEFORE the guard: a cancelled dialog
            // must leave the guard free and no pump scheduled.
            int skipped;
            List<string> work;
            try
            {
                work = CollectFolderWork(folders, out skipped);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return;
            }
            if (work.Count == 0)
            {
                Debug.Log(k_LogPrefix + "No scenes found in folder; nothing to generate.");
                return;
            }
            string folderLabel =
                folders.Count == 1 ? "'" + folders[0] + "'" : folders.Count + " selected folders";
            if (
                !ConfirmBatchStart(
                    "Generate UniThumbs",
                    "Generate thumbnails for " + work.Count + " scene(s) in " + folderLabel + "?",
                    "Generate"
                )
            )
            {
                Debug.Log(k_LogPrefix + "Generate in Folder cancelled by user.");
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Generate in Folder refused: another thumbnail generation is already in progress."
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

        [MenuItem(k_GenerateFolderMenuPath, true, k_GenerateFolderMenuPriority)]
        private static bool ValidateGenerateFolderUniThumbs()
        {
            return HasValidFolderSelection();
        }

        #endregion

        #region Private Methods

        private static bool IsSceneAssetSelected()
        {
            UnityEngine.Object active = Selection.activeObject;
            return active != null && active is SceneAsset;
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
        /// restores the previous scene. The save prompt runs before any switch so
        /// unsaved changes are never silently discarded.
        /// </summary>
        private static CaptureResult CaptureScene(string scenePath)
        {
            string originalPath = EditorSceneManager.GetActiveScene().path;
            bool switched = false;
            if (!string.Equals(originalPath, scenePath, StringComparison.Ordinal))
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Warning = "User cancelled the scene save prompt.",
                    };
                }
                if (string.IsNullOrEmpty(originalPath))
                {
                    originalPath = EditorSceneManager.GetActiveScene().path;
                }
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
                return UniThumbCapture.Capture(
                    ClampBulkResolution(UniThumbCapture.GetLastSettingsOrDefault())
                );
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
        private static CaptureSettings ClampBulkResolution(CaptureSettings settings)
        {
            if (settings.Width > k_MaxBulkResolution || settings.Height > k_MaxBulkResolution)
            {
                Debug.Log(
                    k_LogPrefix
                        + "Resolution "
                        + settings.Width
                        + "x"
                        + settings.Height
                        + " clamped to "
                        + k_MaxBulkResolution
                        + " for bulk generation."
                );
                settings.Width = k_MaxBulkResolution;
                settings.Height = k_MaxBulkResolution;
            }
            return settings;
        }

        /// <summary>
        /// Candidate set for Refresh All: enabled EditorBuildSettings scenes PLUS
        /// every scene found by AssetDatabase.FindAssets("t:Scene"). Returns only
        /// the scenes that are explicitly requested for regeneration: stale
        /// (missing or outdated per storage invalidation) or the user-selected
        /// scene. Out-of-project scenes are skipped with a warning, never captured.
        /// </summary>
        private static List<string> CollectRefreshWork()
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
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
            string[] foundGuids = AssetDatabase.FindAssets("t:Scene");
            foreach (string guid in foundGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    candidates.Add(path);
                }
            }

            string selectedPath;
            bool hasSelection = TryGetSelectedScenePath(out selectedPath);

            var work = new List<string>();
            int skippedOutside = 0;
            int upToDate = 0;
            foreach (string scenePath in candidates)
            {
                if (!IsInProject(scenePath))
                {
                    Debug.LogWarning(
                        k_LogPrefix + "Skipped out-of-project scene '" + scenePath + "'."
                    );
                    skippedOutside++;
                    continue;
                }
                if (
                    IsStale(scenePath)
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
                    + "Refresh All: "
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
        /// EVERY scene under the selected folders, regardless of staleness: the
        /// user explicitly requested these folders, so all scenes in them
        /// regenerate (unlike project-wide Refresh All, which is stale-only).
        /// FindAssets is scoped per folder; a defensive prefix check guarantees
        /// nothing outside a folder is queued. Out-of-project paths are counted
        /// as skipped, never queued. A scene is queued once even when nested
        /// folder selections overlap.
        /// </summary>
        private static List<string> CollectFolderWork(List<string> folderPaths, out int skipped)
        {
            var work = new HashSet<string>(StringComparer.Ordinal);
            skipped = 0;
            foreach (string folderPath in folderPaths)
            {
                string[] foundGuids = AssetDatabase.FindAssets("t:Scene", new[] { folderPath });
                foreach (string guid in foundGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    if (!IsInsideFolder(path, folderPath))
                    {
                        Debug.LogWarning(
                            k_LogPrefix + "Skipped scene outside folder '" + path + "'."
                        );
                        skipped++;
                        continue;
                    }
                    if (!IsInProject(path))
                    {
                        Debug.LogWarning(
                            k_LogPrefix + "Skipped out-of-project scene '" + path + "'."
                        );
                        skipped++;
                        continue;
                    }
                    work.Add(path);
                }
            }
            return new List<string>(work);
        }

        /// <summary>
        /// Public wrapper over CollectFolderWork: every scene under the folder
        /// (recursive FindAssets t:Scene, scoped, deduped, out-of-project entries
        /// skipped). Used by the window's Clear Folder Thumbnails flow for counting
        /// and deletion.
        /// </summary>
        public static List<string> CollectFolderScenePaths(string folderPath)
        {
            return CollectFolderWork(new List<string> { folderPath }, out _);
        }

        /// <summary>
        /// True when the scene path sits directly under the chosen folder (folder
        /// prefix + separator). Defensive: FindAssets scoping already implies it.
        /// </summary>
        private static bool IsInsideFolder(string path, string folderPath)
        {
            string prefix = folderPath.TrimEnd('/') + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the scene has no thumbnail in storage (missing) or its
        /// LastWriteTimeUtc no longer matches the ticks recorded at save time
        /// (outdated per UniThumbStorage invalidation), or when timestamps match
        /// but the scene fingerprint has changed (content stale). Legacy thumbnails
        /// without a stored fingerprint are never flagged stale by the fingerprint
        /// path.
        /// </summary>
        private static bool IsStale(string scenePath)
        {
            if (!UniThumbStorage.HasThumbnail(scenePath))
            {
                return true;
            }
            string guid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(guid))
            {
                return true;
            }
            long liveTicks = File.GetLastWriteTimeUtc(AbsolutePath(scenePath)).Ticks;
            long storedTicks = ReadPrefsTicks(guid);
            if (liveTicks != storedTicks)
            {
                return true;
            }

            // Timestamps match: fingerprint is the secondary staleness signal.
            UniThumbFingerprint.SceneFingerprint storedFp = UniThumbStorage.LoadFingerprint(guid);
            if (storedFp == default)
            {
                return false;
            }
            UniThumbFingerprint.SceneFingerprint currentFp = UniThumbFingerprint.Compute();
            return !storedFp.Equals(currentFp);
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

        private static long ReadPrefsTicks(string guid)
        {
            string raw = EditorPrefs.GetString(k_PrefsPrefix + "." + guid, string.Empty);
            long ticks;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            {
                return 0L;
            }
            return ticks;
        }

        private static string AbsolutePath(string assetsRelativePath)
        {
            if (Path.IsPathRooted(assetsRelativePath))
            {
                return assetsRelativePath;
            }
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetsRelativePath);
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
        /// Kicks off the async batch pump. The guard entered by the menu handler
        /// stays held until the pump completes or aborts.
        /// </summary>
        private static void StartBatchPump(List<string> work)
        {
            // Snapshot once at pump start: ProcessSceneCapture must not re-read
            // the store per scene (prevents mid-batch drift from UI edits).
            s_State.BatchSettings = UniThumbCapture.GetLastSettingsOrDefault();
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
                CaptureResult result = UniThumbCapture.Capture(
                    ClampBulkResolution(s_State.BatchSettings)
                );
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

                if (!UniThumbStorage.Save(scenePath, result.PngBytes))
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
                UniThumbStorage.SaveFingerprint(scenePath, UniThumbFingerprint.Compute());
                s_State.WroteThumbnails = true;

                // Verification: the PNG must be decodable as a Texture2D, not just
                // written to disk. Load returns the texture Save just cached (no new
                // LoadImage in the common path); the dropped reference is cache-owned
                // and destroyed on eviction (Save/Delete/staleness) or the
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
        /// True when the async shader compile kicked off by the scene switch has
        /// settled, or the k_ShaderCompileTimeout cap fired (capture proceeds
        /// anyway, warned). Phase B only runs on ticks after the OpenScene that
        /// armed the wait, so at least one update tick always elapses before the
        /// first poll.
        /// </summary>
        private static bool ShaderCompileSettled()
        {
            if (!ShaderUtil.anythingCompiling)
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
        /// Switches the active scene to the target when needed. The save prompt
        /// (SaveCurrentModifiedScenesIfUserWantsTo) protects unsaved changes; a
        /// cancelled prompt fails the scene, its changes are never silently
        /// discarded.
        /// </summary>
        private static bool TryActivateScene(string scenePath)
        {
            string activePath = EditorSceneManager.GetActiveScene().path;
            if (string.Equals(activePath, scenePath, StringComparison.Ordinal))
            {
                return true;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Skipped '"
                        + scenePath
                        + "': user cancelled the scene save prompt."
                );
                return false;
            }
            if (string.IsNullOrEmpty(s_State.OriginalScenePath))
            {
                s_State.OriginalScenePath = EditorSceneManager.GetActiveScene().path;
            }

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
        /// Graceful cancel path: pass 2 (icons for verified scenes) still runs,
        /// then the summary reports "cancelled after k of N scenes" and teardown
        /// happens in the finally, exactly like CompleteBatch.
        /// </summary>
        private static void CancelBatch()
        {
            try
            {
                foreach (string scenePath in s_State.SucceededScenes)
                {
                    UniThumbIconService.ApplyIcon(scenePath);
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
        /// Pass 2: icons are applied only for scenes whose PNG was written and
        /// verified in pass 1, then the summary is logged and everything is torn
        /// down (progress bar, guard, subscription) in the finally.
        /// </summary>
        private static void CompleteBatch()
        {
            try
            {
                foreach (string scenePath in s_State.SucceededScenes)
                {
                    UniThumbIconService.ApplyIcon(scenePath);
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
        /// batch started. Failures here are logged, never thrown.
        /// </summary>
        private static void RestoreOriginalScene(string originalPath, bool switchedScenes)
        {
            if (!switchedScenes)
            {
                return;
            }
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
                s_State.CurrentScenePath = null;
                s_State.CancelRequested = false;
                s_State.WaitingForShaderCompile = false;
                s_State.WaitStartedAt = 0.0;
                if (
                    s_State.WroteThumbnails
                    && UniThumbSettings.Get().StorageMode == StorageMode.TrackedInAssets
                )
                {
                    AssetDatabase.Refresh();
                }
                s_State.WroteThumbnails = false;
                UniThumbGuard.Exit();
            }
        }

        #endregion
    }
}
