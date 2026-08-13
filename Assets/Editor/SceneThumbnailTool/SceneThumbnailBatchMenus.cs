using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.SceneThumbnails
{
    /// <summary>
    /// Manual-only integration: context menus (Generate / Clear / Refresh All /
    /// Generate in Folder) and the build-scenes batch. These menus plus the
    /// window button are the ONLY
    /// capture entry points in the tool — there are zero asset/scene hooks
    /// (no OnPostprocessAllAssets, no EditorSceneManager.sceneSaving/sceneSaved),
    /// so nothing ever generates automatically. Every handler enters the shared
    /// SceneThumbnailGuard (M2, manual paths) to prevent double-trigger from
    /// multi-select context menu clicks.
    ///
    /// Batch (Refresh All) is two-pass: pass 1 captures + saves + verifies every
    /// requested scene (progress bar, one EditorApplication.update frame yielded
    /// between scenes, plus a shader-compile wait after each scene switch so a
    /// render never samples half-compiled URP variants), pass 2 applies icons
    /// for verified PNGs only. Refresh All
    /// regenerates ONLY explicitly requested scenes: stale (missing/outdated per
    /// SceneThumbnailStorage invalidation) or the user-selected scene — never
    /// unrequested scenes. The folder batch regenerates EVERY scene under the
    /// selected folders (an explicit request, not stale-only) and reuses the
    /// same two-pass pump. Scenes outside the project (Packages/ etc.) are
    /// skipped with a warning, never captured. The active scene is switched per
    /// target scene and the original scene is restored when the batch finishes.
    /// </summary>
    public static class SceneThumbnailBatchMenus
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

        private const string k_LogPrefix = "[SceneThumbnailTool] ";
        private const string k_GenerateMenuPath = "Assets/Generate Scene Thumbnail";
        private const string k_ClearMenuPath = "Assets/Clear Scene Thumbnail";
        private const string k_RefreshAllMenuPath = "Assets/Refresh All Scene Thumbnails";
        private const string k_GenerateFolderMenuPath =
            "Assets/Generate Scene Thumbnails in Folder";
        private const int k_GenerateMenuPriority = 1100;
        private const int k_ClearMenuPriority = 1101;
        private const int k_RefreshAllMenuPriority = 1102;
        private const int k_GenerateFolderMenuPriority = 1103;
        private const string k_FolderBatchKind = "Folder";
        private const string k_ProgressTitle = "Scene Thumbnails";
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
        // in SceneThumbnailCapture). Batch defaults are 512px, so this guard is
        // defensive - it must exist and be provable.
        private const int k_MaxBulkResolution = 2048;

        // Mirror of SceneThumbnailStorage's invalidation key schema (k_PrefsPrefix
        // + "." + sceneGuid). Storage owns the schema; this copy lets Refresh All
        // decide staleness (outdated = scene LastWriteTimeUtc != stored ticks)
        // without touching storage internals. v2: bumped with the capture-fidelity
        // defaults (skybox + post-processing); keep in sync on any bump.
        private const string k_PrefsPrefix = "SceneThumbs.v2";

        #endregion

        #region Fields

        private static Queue<string> s_PendingScenes;
        private static List<string> s_SucceededScenes;
        private static List<string> s_FailedScenes;
        private static int s_TotalScenes;
        private static int s_ProcessedCount;
        private static string s_OriginalScenePath;
        private static bool s_SwitchedScenes;
        private static string s_BatchKind;
        private static int s_SkippedCount;

        // Current scene being processed (GetBatchSnapshot.CurrentScene) and the
        // user cancel flag (RequestBatchCancel); both reset in CancelBatchState.
        private static string s_CurrentScenePath;
        private static bool s_CancelRequested;

        // Shader-compile wait state (pink-material fix): OpenScene kicks off an
        // async URP variant compile; the capture for the current scene is
        // deferred until ShaderUtil.anythingCompiling settles or the timeout
        // fires. Both reset in CancelBatchState.
        private static bool s_WaitingForShaderCompile;
        private static double s_WaitStartedAt;

        #endregion

        #region Public Methods

        /// <summary>
        /// True while the batch pump is active (a batch is queued and running).
        /// </summary>
        public static bool IsBatchRunning
        {
            get { return s_PendingScenes != null; }
        }

        /// <summary>
        /// Requests a graceful stop: the pump halts before the next scene and
        /// reports "cancelled after k of N scenes". Picked up at the top of
        /// OnBatchUpdate; harmless when no batch is running.
        /// </summary>
        public static void RequestBatchCancel()
        {
            s_CancelRequested = true;
        }

        /// <summary>
        /// Single consistent read of the pump state for UI consumers. Idle
        /// batches report IsRunning=false with zeroed counters and a null
        /// CurrentScene.
        /// </summary>
        public static BatchSnapshot GetBatchSnapshot()
        {
            if (s_PendingScenes == null)
            {
                return new BatchSnapshot();
            }
            return new BatchSnapshot
            {
                IsRunning = true,
                Total = s_TotalScenes,
                Processed = s_ProcessedCount,
                Succeeded = s_SucceededScenes != null ? s_SucceededScenes.Count : 0,
                Failed = s_FailedScenes != null ? s_FailedScenes.Count : 0,
                Skipped = s_SkippedCount,
                CurrentScene = s_CurrentScenePath,
            };
        }

        /// <summary>
        /// Starts the folder batch over a single project folder (recursive scene
        /// discovery, every scene regenerates - not stale-only). Validates the
        /// assets-relative path, enters the shared guard and the capture-readiness
        /// checks like the menu handlers, then starts the pump. Returns false with
        /// a user-facing error string on any refusal. The pump holds the guard
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

            if (!SceneThumbnailGuard.TryEnter())
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

                int skipped;
                List<string> work = CollectFolderWork(new List<string> { folderPath }, out skipped);
                if (work.Count == 0)
                {
                    error = "No scenes found in folder '" + folderPath + "'.";
                    return false;
                }

                s_BatchKind = k_FolderBatchKind;
                s_SkippedCount = skipped;
                s_CancelRequested = false;
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
                    SceneThumbnailGuard.Exit();
                }
            }
        }

        [MenuItem(k_GenerateMenuPath, false, k_GenerateMenuPriority)]
        public static void GenerateSceneThumbnail()
        {
            string scenePath;
            if (!TryGetSelectedScenePath(out scenePath))
            {
                Debug.LogWarning(
                    k_LogPrefix + "Generate Scene Thumbnail refused: select a SceneAsset first."
                );
                return;
            }

            if (!SceneThumbnailGuard.TryEnter())
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

                if (!SceneThumbnailStorage.Save(scenePath, result.PngBytes))
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + scenePath
                            + "'. See Console for details."
                    );
                    return;
                }

                SceneThumbnailIconService.ApplyIcon(scenePath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + scenePath + "'" + suffix);
            }
            finally
            {
                SceneThumbnailGuard.Exit();
            }
        }

        [MenuItem(k_GenerateMenuPath, true, k_GenerateMenuPriority)]
        private static bool ValidateGenerateSceneThumbnail()
        {
            return IsSceneAssetSelected();
        }

        [MenuItem(k_ClearMenuPath, false, k_ClearMenuPriority)]
        public static void ClearSceneThumbnail()
        {
            string scenePath;
            if (!TryGetSelectedScenePath(out scenePath))
            {
                Debug.LogWarning(
                    k_LogPrefix + "Clear Scene Thumbnail refused: select a SceneAsset first."
                );
                return;
            }

            if (!SceneThumbnailGuard.TryEnter())
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Clear refused: another thumbnail generation is already in progress."
                );
                return;
            }

            try
            {
                bool deleted = SceneThumbnailStorage.Delete(scenePath);
                SceneThumbnailIconService.ClearIcon(scenePath);
                string message = deleted
                    ? "Cleared thumbnail for '"
                    : "No thumbnail to clear for '";
                Debug.Log(k_LogPrefix + message + scenePath + "'.");
            }
            finally
            {
                SceneThumbnailGuard.Exit();
            }
        }

        [MenuItem(k_ClearMenuPath, true, k_ClearMenuPriority)]
        private static bool ValidateClearSceneThumbnail()
        {
            return IsSceneAssetSelected();
        }

        [MenuItem(k_RefreshAllMenuPath, false, k_RefreshAllMenuPriority)]
        public static void RefreshAllSceneThumbnails()
        {
            if (!SceneThumbnailGuard.TryEnter())
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
                    SceneThumbnailGuard.Exit();
                }
            }
        }

        [MenuItem(k_GenerateFolderMenuPath, false, k_GenerateFolderMenuPriority)]
        public static void GenerateFolderSceneThumbnails()
        {
            List<string> folders = CollectSelectedFolders();
            if (folders.Count == 0)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Generate Scene Thumbnails in Folder refused: select a folder in the Project window first."
                );
                return;
            }

            if (!SceneThumbnailGuard.TryEnter())
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

                int skipped;
                List<string> work = CollectFolderWork(folders, out skipped);
                if (work.Count == 0)
                {
                    Debug.Log(k_LogPrefix + "No scenes found in folder; nothing to generate.");
                    return;
                }

                s_BatchKind = k_FolderBatchKind;
                s_SkippedCount = skipped;
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
                    SceneThumbnailGuard.Exit();
                }
            }
        }

        [MenuItem(k_GenerateFolderMenuPath, true, k_GenerateFolderMenuPriority)]
        private static bool ValidateGenerateFolderSceneThumbnails()
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
                return SceneThumbnailCapture.Capture(
                    ClampBulkResolution(SceneThumbnailCapture.CreateDefaultSettings())
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
        /// (outdated per SceneThumbnailStorage invalidation).
        /// </summary>
        private static bool IsStale(string scenePath)
        {
            if (!SceneThumbnailStorage.HasThumbnail(scenePath))
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
            return liveTicks != storedTicks;
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
        /// Kicks off the async batch pump. The guard entered by the menu handler
        /// stays held until the pump completes or aborts.
        /// </summary>
        private static void StartBatchPump(List<string> work)
        {
            s_PendingScenes = new Queue<string>(work);
            s_SucceededScenes = new List<string>();
            s_FailedScenes = new List<string>();
            s_TotalScenes = work.Count;
            s_ProcessedCount = 0;
            s_OriginalScenePath = EditorSceneManager.GetActiveScene().path;
            s_SwitchedScenes = false;
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
                if (s_PendingScenes == null)
                {
                    CancelBatchState();
                    return;
                }
                if (s_CancelRequested)
                {
                    CancelBatch();
                    return;
                }
                if (s_WaitingForShaderCompile)
                {
                    // Phase B: the current scene was opened on an earlier tick.
                    // Capture only once the async shader compile settles - never
                    // on the OpenScene tick.
                    if (ShaderCompileSettled())
                    {
                        s_WaitingForShaderCompile = false;
                        s_WaitStartedAt = 0.0;
                        ShowProgress(string.Format(k_ProgressMessageFormat, s_CurrentScenePath));
                        ProcessSceneCapture(s_CurrentScenePath);
                    }
                    else
                    {
                        ShowProgress(
                            string.Format(k_ShaderCompileProgressMessageFormat, s_CurrentScenePath)
                        );
                    }
                    return;
                }
                if (s_PendingScenes.Count == 0)
                {
                    CompleteBatch();
                    return;
                }
                string scenePath = s_PendingScenes.Dequeue();
                s_ProcessedCount++;
                s_CurrentScenePath = scenePath;
                // Phase A: open the scene and arm the compile wait; no capture on
                // this tick.
                ProcessScene(scenePath);
                if (s_WaitingForShaderCompile)
                {
                    ShowProgress(string.Format(k_ShaderCompileProgressMessageFormat, scenePath));
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
                    s_FailedScenes.Add(scenePath);
                    return;
                }
                s_WaitingForShaderCompile = true;
                s_WaitStartedAt = EditorApplication.timeSinceStartup;
            }
            catch (Exception exception)
            {
                s_FailedScenes.Add(scenePath);
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
                CaptureResult result = SceneThumbnailCapture.Capture(
                    ClampBulkResolution(SceneThumbnailCapture.CreateDefaultSettings())
                );
                if (!result.Success)
                {
                    s_FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Capture failed for '"
                            + scenePath
                            + "': "
                            + (result.Warning ?? "unknown error.")
                    );
                    return;
                }

                if (!SceneThumbnailStorage.Save(scenePath, result.PngBytes))
                {
                    s_FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Save failed for '"
                            + scenePath
                            + "'. See Console for details."
                    );
                    return;
                }

                // Verification: the PNG must be decodable as a Texture2D, not just
                // written to disk. Load returns the texture Save just cached (no new
                // LoadImage in the common path); the dropped reference is cache-owned
                // and destroyed on eviction (Save/Delete/staleness) or the
                // domain-reload clear - never a transient texture, never a leak.
                if (SceneThumbnailStorage.Load(scenePath) == null)
                {
                    s_FailedScenes.Add(scenePath);
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Verification failed for '"
                            + scenePath
                            + "': thumbnail is missing or not importable."
                    );
                    return;
                }

                s_SucceededScenes.Add(scenePath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? "."
                    : " (warning: " + result.Warning + ")";
                Debug.Log(k_LogPrefix + "Thumbnail generated for '" + scenePath + "'" + suffix);
            }
            catch (Exception exception)
            {
                s_FailedScenes.Add(scenePath);
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
            if (EditorApplication.timeSinceStartup - s_WaitStartedAt > k_ShaderCompileTimeout)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Shader compilation did not settle within "
                        + k_ShaderCompileTimeout
                        + "s; capturing '"
                        + s_CurrentScenePath
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
        private static void ShowProgress(string message)
        {
            float progress = s_TotalScenes > 0 ? s_ProcessedCount / (float)s_TotalScenes : 1f;
            EditorUtility.DisplayProgressBar(k_ProgressTitle, message, progress);
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
            if (string.IsNullOrEmpty(s_OriginalScenePath))
            {
                s_OriginalScenePath = EditorSceneManager.GetActiveScene().path;
            }

            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                s_SwitchedScenes = true;
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
                foreach (string scenePath in s_SucceededScenes)
                {
                    SceneThumbnailIconService.ApplyIcon(scenePath);
                }
                string summary =
                    k_LogPrefix
                    + "Batch cancelled after "
                    + s_ProcessedCount
                    + " of "
                    + s_TotalScenes
                    + " scenes: "
                    + s_SucceededScenes.Count
                    + " generated, "
                    + s_FailedScenes.Count
                    + " failed, "
                    + s_SkippedCount
                    + " skipped.";
                Debug.Log(summary);
            }
            finally
            {
                RestoreOriginalScene(s_OriginalScenePath, s_SwitchedScenes);
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
                foreach (string scenePath in s_SucceededScenes)
                {
                    SceneThumbnailIconService.ApplyIcon(scenePath);
                }

                string summary;
                if (s_BatchKind == k_FolderBatchKind)
                {
                    summary =
                        k_LogPrefix
                        + "Folder batch: "
                        + s_SucceededScenes.Count
                        + " generated, "
                        + (s_FailedScenes.Count + s_SkippedCount)
                        + " skipped/failed.";
                }
                else
                {
                    summary =
                        k_LogPrefix
                        + "Batch complete: "
                        + s_SucceededScenes.Count
                        + " succeeded, "
                        + s_FailedScenes.Count
                        + " failed of "
                        + s_TotalScenes
                        + " requested.";
                }
                if (s_FailedScenes.Count > 0)
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
                RestoreOriginalScene(s_OriginalScenePath, s_SwitchedScenes);
                CancelBatchState();
            }
        }

        private static void AbortBatch()
        {
            Debug.LogWarning(k_LogPrefix + "Batch aborted after an unexpected error.");
            RestoreOriginalScene(s_OriginalScenePath, s_SwitchedScenes);
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
        /// subscription removed, state reset, guard released.
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
                s_PendingScenes = null;
                s_SucceededScenes = null;
                s_FailedScenes = null;
                s_TotalScenes = 0;
                s_ProcessedCount = 0;
                s_OriginalScenePath = null;
                s_SwitchedScenes = false;
                s_BatchKind = null;
                s_SkippedCount = 0;
                s_CurrentScenePath = null;
                s_CancelRequested = false;
                s_WaitingForShaderCompile = false;
                s_WaitStartedAt = 0.0;
                SceneThumbnailGuard.Exit();
            }
        }

        #endregion
    }
}
