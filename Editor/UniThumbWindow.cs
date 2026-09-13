using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using UnityEngine.UIElements;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Scene and prefab thumbnail tool window. The Generate button
    /// is the single-item capture path in this window: a prefab selection
    /// captures the prefab (CapturePrefab, no scene switch), otherwise the
    /// active scene. No capture happens on
    /// open/close/repaint or on scene add/modify; the opt-in Regenerate on
    /// Save setting covers scene and prefab saves while the window is open. The window also drives
    /// the folder batch (UniThumbBatchMenus.TryStartFolderBatch) with
    /// progress and result feedback. Capture logic lives in
    /// UniThumbCapture; the thumbnail storage location follows the
    /// UniThumbSettings storage mode (Library cache or tracked inside
    /// Assets) and is shown in the footer.
    /// UI Toolkit build (UniThumbWindow.uxml/.uss); the window never uses
    /// the IMGUI painting path.
    /// </summary>
    public class UniThumbWindow : EditorWindow
    {
        #region Constants

        private const string k_MenuPath = "Window/UniThumb";
        private const string k_WindowTitle = "UniThumb";
        private const string k_NoThumbnailLabel = "No thumbnail yet - press Generate Thumbnail";
        private const string k_InvalidFolderLabel =
            "Not a project folder - pick a folder inside Assets/";
        private const string k_DisabledFramingLabel =
            "Disabled while using the current Scene View angle";
        private const string k_LibraryCacheHint =
            "Thumbnails are stored in Library/SceneThumbnails. Machine-local and regenerable, not shared through Git.";
        private const string k_TrackedInAssetsHint =
            "Thumbnails are stored in Assets/UniThumb/Thumbnails and can be committed to Git to share them. Switching storage mode moves existing thumbnails to the new location.";
        private static string UxmlPath =>
            UniThumbPackagePaths.EditorFolderAssetPath + "/UniThumbWindow.uxml";
        private static string UssPath =>
            UniThumbPackagePaths.EditorFolderAssetPath + "/UniThumbWindow.uss";
        private const int k_PreviewRefetchAttempts = 8;
        private const long k_PreviewRefetchDelayMs = 100;

        /// <summary>
        /// Trailing debounce window (seconds) for hierarchy/undo-redo preview
        /// requests. Rapid bursts collapse to one render after the window goes
        /// quiet; selection changes with a new key stay immediate.
        /// </summary>
        private const double k_PreviewDebounceSeconds = 0.2;

        /// <summary>
        /// Idle defer window (seconds) for new-key selections. The heavy
        /// prefab preview (Instantiate plus Render plus ReadBack) never runs
        /// synchronously inside the MouseUp selection event; it waits for the
        /// editor to go quiet so the click itself never holds. Hierarchy and
        /// undo-redo bursts keep the shorter trailing debounce above.
        /// </summary>
        private const double k_PreviewIdleDeferSeconds = 0.6;

        /// <summary>
        /// Modal progress title for the single-item Generate flows.
        /// </summary>
        private const string k_GenerateProgressTitle = "UniThumb Generate";
        private const int k_MaxFolderMenuEntries = 500;

        private static readonly int[] k_PresetResolutions =
        {
            16,
            32,
            64,
            128,
            256,
            512,
            1024,
            2048,
        };

        #endregion

        #region Fields

        [SerializeField]
        private int _resolutionIndex = 3;

        [SerializeField]
        private bool _useSceneViewAngle = true;

        [SerializeField]
        private bool _orthographic2D = false;

        [SerializeField]
        private float _orbitYaw = 45f;

        [SerializeField]
        private float _orbitPitch = 25f;

        [SerializeField]
        private float _orbitDistanceMultiplier = 1f;

        [SerializeField]
        private float _orbitFov = 60f;

        [SerializeField]
        private float _fitFactor = 2f;

        [SerializeField]
        private bool _useLightingOverride;

        [SerializeField]
        private Color _backgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f);

        [SerializeField]
        private BackgroundMode _backgroundMode = BackgroundMode.Skybox;

        [SerializeField]
        private bool _wantPostProcessing = true;

        /// <summary>
        /// Explicit VolumeProfile for post-processing. Wins over scene Volumes
        /// when set; null means auto (scene Volume, else the first project
        /// profile with visible effects). Stored as UnityEngine.Object because
        /// the package asmdef cannot reference SRP Core types; validated at
        /// capture time.
        /// </summary>
        [SerializeField]
        private UnityEngine.Object _postProcessingProfile;

        /// <summary>
        /// Shared scene+prefab UI toggle (surfaced by capture-ui-toggle).
        /// Default true renders prefab UI by default; false culls UI and
        /// reproduces baseline prefab pixels. Flows to CaptureSettings via
        /// BuildSettings and persists via UniThumbSettings (no new setting).
        /// </summary>
        [SerializeField]
        private bool _captureUi = true;

        [SerializeField]
        private float _uiScale = 1f;

        [SerializeField]
        private LayerMask _layerMask = -1;

        [SerializeField]
        private LightingMode _lightingMode = LightingMode.None;

        [SerializeField]
        private float _light2DIntensity = 1f;

        [SerializeField]
        private int _light2DSortingLayers = -1;

        [SerializeField]
        private float _light3DIntensity = 1f;

        [SerializeField]
        private float _light3DPrefabIntensity = 1.75f;

        [SerializeField]
        private bool _light3DShadows;

        [SerializeField]
        private Color _light3DColor = Color.white;

        [SerializeField]
        private float _light3DYaw = 50f;

        [SerializeField]
        private float _light3DPitch = -30f;

        [SerializeField]
        private float _light3DIntensityMax = 5f;

        [SerializeField]
        private float _light3DYawMin = -180f;

        [SerializeField]
        private float _light3DYawMax = 180f;

        [SerializeField]
        private float _light3DPitchMin = -89f;

        [SerializeField]
        private float _light3DPitchMax = 89f;

        [SerializeField]
        private float _particlePreviewTime = 1f;

        [SerializeField]
        private string _batchFolderInput;

        [SerializeField]
        private string _batchFolderPath;

        [SerializeField]
        private UniThumbBatchMenus.BatchScope _batchScope = UniThumbBatchMenus.BatchScope.All;

        private string _statusMessage = string.Empty;
        private MessageType _statusType = MessageType.None;

        // Cache-owned runtime texture (UniThumbStorage is the sole owner):
        // never destroy it, only drop the reference. Re-fetched by GUID after
        // eviction (destroyed instances compare == null).
        private Texture2D _previewTexture;
        private string _previewGuid;

        // Prefab preview mode: set when a prefab asset is selected in the
        // Project window (see OnProjectSelectionChanged). While set, the
        // preview box shows the prefab's cached thumbnail and the live scene
        // preview stays off (RenderLivePreview early-outs); the Generate and
        // Delete buttons operate on the prefab instead of the active scene.
        // UI refresh reads the cache only (TryGetCachedTexture), same as scenes.
        private string _previewPrefabPath;

        private string _batchResultMessage;
        private MessageType _batchResultType = MessageType.None;
        private bool _folderValid;
        private bool _batchFolderInputInvalid;
        private bool _cancelRequested;
        private bool _wasBatchRunning;

        // Last snapshot seen while the pump was running. The batch resets its
        // state during teardown, before this window observes the idle tick, so
        // OnBatchEnded must read the cached final counters instead.
        private UniThumbBatchMenus.BatchSnapshot _lastRunningBatchSnapshot;
        private float _batchStartTime;

        private RenderTexture _livePreviewRT;
        private Texture2D _livePreviewTexture;
        private bool _previewDirty;

        // MouseUp stall guards: the selection key collapses repeated
        // selection events, the render key skips re-renders when prefab
        // path plus settings plus selection are unchanged, and the defer
        // tick implements the trailing hierarchy/undo debounce.
        private string _lastSelectionKey;
        private string _lastRenderKey;
        private double _previewDeferUntil;
        private int _previewRenderCount;
        private int _previewSkipCount;

        // Idle-pump guards for the heavy preview: _previewRenderInFlight
        // marks a synchronous heavy render on the stack (re-entrant ticks
        // coalesce instead of stacking), _previewCancelRequested drops a
        // pending dirty preview, and _previewOwnMutationDepth filters the
        // hierarchy events fired by our own preview Instantiate and Destroy
        // calls so they never invalidate the render key.
        private bool _previewRenderInFlight;
        private bool _previewCancelRequested;
        private int _previewOwnMutationDepth;
        private int _previewOwnEventFilteredCount;

        // Guard-held defer accounting: each RenderLivePreview tick that
        // early-outs while UniThumbGuard is held increments
        // _previewGuardDeferCount (same-key silent-defer probe). The log
        // below is throttled to once per second so a stuck batch cannot
        // spam the console while still leaving a trace.
        private int _previewGuardDeferCount;
        private double _previewGuardDeferLastLogTime;

        // Warn-once latch for the prefab-asset-missing early-out: the path
        // that already warned, so a persistently missing prefab reschedules
        // silently after the first warning instead of spamming per tick.
        private string _previewMissingPrefabWarnedPath;

        /// <summary>
        /// True while the Generate progress bar was shown for the current
        /// single-item flow. Reset at flow start; EditMode assertion seam.
        /// </summary>
        internal bool GenerateProgressWasShown { get; private set; }

        /// <summary>
        /// True when the user cancelled the Generate flow via the progress
        /// Cancel button. Reset at flow start; EditMode assertion seam.
        /// </summary>
        internal bool GenerateWasCancelled { get; private set; }

        /// <summary>
        /// Drag probe seam: true means pointer drag is active, so the heavy
        /// preview stays deferred. Null runs the default hotControl check.
        /// </summary>
        internal static Func<bool> PreviewDragProbeOverride;

        /// <summary>
        /// Generate progress seam: shows progress, returns cancel state.
        /// Null runs EditorUtility.DisplayCancelableProgressBar.
        /// </summary>
        internal static Func<string, string, float, bool> GenerateProgressDisplayOverride;

        /// <summary>
        /// Generate progress clear seam. Null runs EditorUtility.ClearProgressBar.
        /// </summary>
        internal static Action GenerateProgressClearOverride;

        private readonly List<UniThumbCapture.LightSnapshot> _previewLightSnapshots =
            new List<UniThumbCapture.LightSnapshot>();

        // Cached Scene View transform used to throttle live-preview re-renders
        // while the user navigates the Scene View (see OnSceneViewGui).
        private Vector3 _lastSceneViewPos;
        private Quaternion _lastSceneViewRot;
        private float _lastSceneViewSize;
        private float _lastSceneViewFov;

        /// <summary>
        /// Test seam for the live-preview UI capture session. Null means the
        /// real UiCaptureSession.Begin runs; tests install a delegate that
        /// throws to prove the temp preview camera is still destroyed.
        /// </summary>
        internal static Func<
            Camera,
            float,
            UniThumbCapture.UiCaptureSession
        > BeginUiSessionOverride;

        /// <summary>
        /// Test seam for the prefab live-preview UI session. Null means the
        /// real UiCaptureSession.BeginPrefab runs; tests install a delegate
        /// that throws to prove the temp preview camera and instance are
        /// still destroyed. Scene path keeps using BeginUiSessionOverride.
        /// </summary>
        internal static Func<
            GameObject,
            Camera,
            float,
            UniThumbCapture.UiCaptureSession
        > BeginPrefabUiSessionOverride;

        #endregion

        #region UI Elements

        private Label _activeSceneLabel;
        private Label _sourceTitleLabel;
        private Label _sourceModeBadge;
        private VisualElement _previewModeRow;
        private Label _previewModeBadge;
        private Button _generateButton;
        private Button _deleteThumbnailButton;
        private Label _generateBusyLabel;
        private Label _noThumbnailLabel;
        private VisualElement _previewBox;
        private Image _previewImage;
        private VisualElement _previewCaptionRow;
        private Label _previewCaption;
        private Slider _particlePreviewTimeSlider;
        private DropdownField _resolutionPopup;
        private Toggle _orthographicToggle;
        private Toggle _sceneViewAngleToggle;
        private Slider _framingDistanceSlider;
        private SliderInt _orbitFovSlider;
        private Slider _fitFactorSlider;
        private Label _framingDisabledHint;
        private VisualElement _orbitControls;
        private Slider _orbitYawSlider;
        private Slider _orbitPitchSlider;
        private Button _presetFrontButton;
        private Button _presetThreequarterButton;
        private Button _presetTopButton;
        private EnumField _bgModeField;
        private ColorField _bgColorField;
        private EnumField _storageModeField;
        private Label _storageModeHint;
        private bool _syncingStorageMode;
        private Toggle _autoRegenToggle;
        private Toggle _updateCheckToggle;
        private Toggle _allowDiscardToggle;
        private VisualElement _updateBanner;
        private Label _updateBannerLabel;
        private Button _updateBannerDismissButton;
        private Toggle _lightingToggle;
        private Toggle _postfxToggle;
        private ObjectField _postfxProfileField;
        private Toggle _captureUiToggle;
        private Slider _uiScaleSlider;
        private FloatField _hdrpExposureField;
        private MaskField _layersMask;
        private VisualElement _batchFolderRow;
        private TextField _batchFolderField;
        private Button _browseButton;
        private Button _useSceneFolderButton;
        private EnumField _batchScopeField;
        private Label _batchInvalidLabel;
        private Button _generateFolderButton;
        private Button _clearFolderButton;
        private VisualElement _batchProgress;
        private ProgressBar _batchProgressBar;
        private Label _batchProgressCaption;
        private Button _cancelBatchButton;
        private HelpBox _batchResultHelp;
        private HelpBox _statusHelp;
        private Label _footerLabel;
        private Toggle _iconOverlayToggle;
        private Slider _listPaddingXSlider;
        private Slider _listPaddingYSlider;
        private Slider _iconMaxSizeSlider;
        private SliderInt _cacheSizeSlider;
        private EnumField _lightingModeField;
        private Slider _light2DIntensitySlider;
        private MaskField _light2DSortingLayersMask;
        private Slider _light3DIntensitySlider;
        private Slider _light3DPrefabIntensitySlider;
        private Label _light3DPrefabIntensityMaxLabel;
        private Toggle _light3DShadowsToggle;
        private ColorField _light3DColorField;
        private Slider _light3DYawSlider;
        private Slider _light3DPitchSlider;
        private FloatField _limitsIntensityMaxField;
        private FloatField _limitsYawMinField;
        private FloatField _limitsYawMaxField;
        private FloatField _limitsPitchMinField;
        private FloatField _limitsPitchMaxField;
        private Button _sceneTabButton;
        private Button _settingsTabButton;
        private ScrollView _sceneTabScroll;
        private ScrollView _settingsTabScroll;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            minSize = new Vector2(320f, 480f);
            maxSize = new Vector2(600f, 800f);
            UniThumbPackagePaths.EnsureResolved();
            UniThumbStorage.EnsureFolder();
            if (!string.IsNullOrEmpty(_batchFolderPath))
            {
                RevalidateBatchFolder();
            }
            else
            {
                _folderValid = false;
            }
            if (string.IsNullOrEmpty(_batchFolderInput))
            {
                _batchFolderInput = _batchFolderPath ?? string.Empty;
            }
            _batchFolderInputInvalid = false;
            // Fresh preview identity: the first render after open always
            // executes (no stale skip), and debounce starts disarmed.
            _lastSelectionKey = null;
            _lastRenderKey = null;
            _previewDeferUntil = 0;
            _previewCancelRequested = false;
            _previewRenderInFlight = false;
            _previewOwnMutationDepth = 0;
            _previewGuardDeferCount = 0;
            _previewGuardDeferLastLogTime = 0;
            _previewMissingPrefabWarnedPath = null;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChangedInEditMode;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.hierarchyChanged += OnHierarchyChangedForPreview;
            Selection.selectionChanged += OnProjectSelectionChanged;
            PrefabStage.prefabStageOpened += OnPrefabStageChanged;
            PrefabStage.prefabStageClosing += OnPrefabStageChanged;
            Undo.undoRedoPerformed += OnUndoRedoForPreview;
            UniThumbStorage.RegisterEviction(OnTextureEvicted);
            EditorApplication.update += OnBatchUpdateTick;
            SceneView.duringSceneGui += OnSceneViewGui;

            // Re-warm the capture settings store after domain reloads so menu/batch
            // paths keep using the window's configured settings even when the
            // window is not reopened since the reload. BuildSettings reads only
            // serialized fields, which are restored before OnEnable.
            UniThumbCapture.RememberSettings(BuildSettings());
            // Pre-CreateGUI no-op via the null guard; PushState re-applies
            // once the slider exists.
            UpdateParticleSliderVisibility();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnBatchUpdateTick;
            EditorApplication.update -= RenderLivePreview;
            _previewDirty = false;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChangedInEditMode;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.hierarchyChanged -= OnHierarchyChangedForPreview;
            Selection.selectionChanged -= OnProjectSelectionChanged;
            PrefabStage.prefabStageOpened -= OnPrefabStageChanged;
            PrefabStage.prefabStageClosing -= OnPrefabStageChanged;
            Undo.undoRedoPerformed -= OnUndoRedoForPreview;
            SceneView.duringSceneGui -= OnSceneViewGui;
            UniThumbStorage.UnregisterEviction(OnTextureEvicted);
            UniThumbCapture.RestoreLights(_previewLightSnapshots);
            _previewLightSnapshots.Clear();
            if (_livePreviewRT != null)
            {
                _livePreviewRT.Release();
                UnityEngine.Object.DestroyImmediate(_livePreviewRT);
                _livePreviewRT = null;
            }
            if (_livePreviewTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_livePreviewTexture);
                _livePreviewTexture = null;
            }
            // Persist capture settings so they survive window close/reopen.
            // OnDisable-only: simpler than per-callback persistence; covers
            // the normal close case. Crash resilience can be added later by
            // calling PersistCaptureSettings in mutation callbacks.
            UniThumbSettings settings = UniThumbSettings.Get();
            if (settings != null)
            {
                settings.SaveCaptureSettings(this);
            }
        }

        private void CreateGUI()
        {
            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }
            else
            {
                Debug.LogError("UniThumbWindow: stylesheet not found at " + UssPath);
            }

            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (tree == null)
            {
                Debug.LogError("UniThumbWindow: UXML not found at " + UxmlPath);
                rootVisualElement.Add(
                    new Label("Failed to load UniThumbWindow.uxml - see Console.")
                );
                return;
            }

            rootVisualElement.AddToClassList(
                EditorGUIUtility.isProSkin ? "theme-dark" : "theme-light"
            );
            rootVisualElement.Add(tree.CloneTree());

            CacheElements();
            WireCallbacks();
            EditorApplication.delayCall += ApplySectionIcons;
            _wasBatchRunning = UniThumbBatchMenus.IsBatchRunning;
            // Load persisted capture settings from UniThumbSettings.asset when
            // available (CaptureSettingsInitialized == true). This runs after
            // CacheElements (UI refs ready) but before PushState (which pushes
            // field values to UI controls). First-time users keep the familiar
            // EditorWindow defaults until the first OnDisable save.
            UniThumbSettings settings = UniThumbSettings.Get();
            if (settings != null && settings.CaptureSettingsInitialized)
            {
                ApplyPersistedCaptureSettings(settings);
            }
            PushState();
            SelectTab(0);
        }

        #endregion

        #region Public Methods

        [MenuItem(k_MenuPath)]
        public static void OpenWindow()
        {
            GetWindow<UniThumbWindow>(k_WindowTitle);
        }

        /// <summary>
        /// Play-mode exit reset (ExitingEditMode, any phase). Clears the
        /// live-preview UI session test override. SAFE (kept): k_* consts,
        /// k_PresetResolutions, UxmlPath/UssPath getters. Idempotent,
        /// null-safe, Editor-only.
        /// </summary>
        internal static void ResetForPlayModeExit()
        {
            BeginUiSessionOverride = null;
            BeginPrefabUiSessionOverride = null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Subscribes the play-mode reset. Unsubscribe-then-subscribe keeps
        /// re-registration idempotent across domain reloads.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Test seam clears on any phase (atomic delegate clear); there is
            // no idle guard because the override is read once per preview.
            ResetForPlayModeExit();
        }

        private void CacheElements()
        {
            _activeSceneLabel = rootVisualElement.Q<Label>("active-scene-label");
            _sourceTitleLabel = rootVisualElement.Q<Label>("source-title-label");
            _sourceModeBadge = rootVisualElement.Q<Label>("source-mode-badge");
            _previewModeRow = rootVisualElement.Q<VisualElement>("preview-mode-row");
            _previewModeBadge = rootVisualElement.Q<Label>("preview-mode-badge");
            _generateButton = rootVisualElement.Q<Button>("generate-button");
            _deleteThumbnailButton = rootVisualElement.Q<Button>("delete-thumbnail-button");
            _clearFolderButton = rootVisualElement.Q<Button>("clear-folder-button");
            _generateBusyLabel = rootVisualElement.Q<Label>("generate-busy-label");
            _noThumbnailLabel = rootVisualElement.Q<Label>("no-thumbnail-label");
            _previewBox = rootVisualElement.Q<VisualElement>("preview-box");
            _previewImage = rootVisualElement.Q<Image>("preview-image");
            _previewCaptionRow = rootVisualElement.Q<VisualElement>("preview-caption-row");
            _previewCaption = rootVisualElement.Q<Label>("preview-caption");
            _particlePreviewTimeSlider = rootVisualElement.Q<Slider>(
                "particle-preview-time-slider"
            );
            _resolutionPopup = rootVisualElement.Q<DropdownField>("resolution-popup");
            _orthographicToggle = rootVisualElement.Q<Toggle>("orthographic-toggle");
            _sceneViewAngleToggle = rootVisualElement.Q<Toggle>("sceneview-angle-toggle");
            _framingDistanceSlider = rootVisualElement.Q<Slider>("framing-distance-slider");
            _orbitFovSlider = rootVisualElement.Q<SliderInt>("orbit-fov-slider");
            _fitFactorSlider = rootVisualElement.Q<Slider>("fit-factor-slider");
            _framingDisabledHint = rootVisualElement.Q<Label>("framing-disabled-hint");
            _orbitControls = rootVisualElement.Q<VisualElement>("orbit-controls");
            _orbitYawSlider = rootVisualElement.Q<Slider>("orbit-yaw-slider");
            _orbitPitchSlider = rootVisualElement.Q<Slider>("orbit-pitch-slider");
            _presetFrontButton = rootVisualElement.Q<Button>("preset-front-button");
            _presetThreequarterButton = rootVisualElement.Q<Button>("preset-threequarter-button");
            _presetTopButton = rootVisualElement.Q<Button>("preset-top-button");
            _bgModeField = rootVisualElement.Q<EnumField>("bg-mode-field");
            _bgColorField = rootVisualElement.Q<ColorField>("bg-color-field");
            _storageModeField = rootVisualElement.Q<EnumField>("storage-mode-field");
            _storageModeHint = rootVisualElement.Q<Label>("storage-mode-hint");
            _autoRegenToggle = rootVisualElement.Q<Toggle>("auto-regen-toggle");
            _updateCheckToggle = rootVisualElement.Q<Toggle>("update-check-toggle");
            _allowDiscardToggle = rootVisualElement.Q<Toggle>("allow-batch-discard-toggle");
            _lightingToggle = rootVisualElement.Q<Toggle>("lighting-toggle");
            _postfxToggle = rootVisualElement.Q<Toggle>("postfx-toggle");
            _postfxProfileField = rootVisualElement.Q<ObjectField>("postfx-profile-field");
            if (_postfxProfileField != null)
            {
                // Restrict the picker to VolumeProfile assets. The type is
                // resolved via reflection because the package asmdef cannot
                // reference SRP Core types; without SRP Core the field falls
                // back to accepting any asset (post-processing is unavailable
                // then anyway).
                System.Type profileType = UniThumbCapture.GetPostProcessingProfileType();
                _postfxProfileField.objectType = profileType ?? typeof(UnityEngine.Object);
            }
            _captureUiToggle = rootVisualElement.Q<Toggle>("capture-ui-toggle");
            _uiScaleSlider = rootVisualElement.Q<Slider>("ui-scale-slider");
            _hdrpExposureField = rootVisualElement.Q<FloatField>("hdrp-exposure-field");
            if (_hdrpExposureField != null && UniThumbCapture.IsHdrpPipeline())
            {
                _hdrpExposureField.RemoveFromClassList("stt-hidden");
            }
            _layersMask = rootVisualElement.Q<MaskField>("layers-mask");
            _batchFolderRow = rootVisualElement.Q<VisualElement>("batch-folder-row");
            _batchFolderField = rootVisualElement.Q<TextField>("batch-folder-field");
            _browseButton = rootVisualElement.Q<Button>("browse-button");
            _useSceneFolderButton = rootVisualElement.Q<Button>("use-scene-folder-button");
            _batchScopeField = rootVisualElement.Q<EnumField>("batch-scope-field");
            _batchInvalidLabel = rootVisualElement.Q<Label>("batch-invalid-label");
            _generateFolderButton = rootVisualElement.Q<Button>("generate-folder-button");
            _batchProgress = rootVisualElement.Q<VisualElement>("batch-progress");
            _batchProgressBar = rootVisualElement.Q<ProgressBar>("batch-progress-bar");
            _batchProgressCaption = rootVisualElement.Q<Label>("batch-progress-caption");
            _cancelBatchButton = rootVisualElement.Q<Button>("cancel-batch-button");
            _batchResultHelp = rootVisualElement.Q<HelpBox>("batch-result-help");
            _statusHelp = rootVisualElement.Q<HelpBox>("status-help");
            _footerLabel = rootVisualElement.Q<Label>("footer-label");
            _updateBanner = rootVisualElement.Q<VisualElement>("stt-update-banner");
            _updateBannerLabel = rootVisualElement.Q<Label>("update-banner-label");
            _updateBannerDismissButton = rootVisualElement.Q<Button>(
                "update-banner-dismiss-button"
            );
            _iconOverlayToggle = rootVisualElement.Q<Toggle>("icon-overlay-toggle");
            _listPaddingXSlider = rootVisualElement.Q<Slider>("list-padding-x-slider");
            _listPaddingYSlider = rootVisualElement.Q<Slider>("list-padding-y-slider");
            _iconMaxSizeSlider = rootVisualElement.Q<Slider>("icon-max-size-slider");
            _cacheSizeSlider = rootVisualElement.Q<SliderInt>("cache-size-slider");
            _lightingModeField = rootVisualElement.Q<EnumField>("lighting-mode");
            _light2DIntensitySlider = rootVisualElement.Q<Slider>("light2d-intensity");
            _light2DSortingLayersMask = rootVisualElement.Q<MaskField>("light2d-sorting-layers");
            _light3DIntensitySlider = rootVisualElement.Q<Slider>("light3d-intensity");
            _light3DPrefabIntensitySlider = rootVisualElement.Q<Slider>("light3d-prefab-intensity");
            _light3DPrefabIntensityMaxLabel = rootVisualElement.Q<Label>(
                "light3d-prefab-intensity-max"
            );
            _light3DShadowsToggle = rootVisualElement.Q<Toggle>("light3d-shadows");
            _light3DColorField = rootVisualElement.Q<ColorField>("light3d-color");
            _light3DYawSlider = rootVisualElement.Q<Slider>("light3d-yaw");
            _light3DPitchSlider = rootVisualElement.Q<Slider>("light3d-pitch");
            _limitsIntensityMaxField = rootVisualElement.Q<FloatField>("limits-intensity-max");
            if (_limitsIntensityMaxField == null)
            {
                _limitsIntensityMaxField = rootVisualElement.Q<FloatField>(
                    "limits-intensity-max-field"
                );
            }
            _limitsYawMinField = rootVisualElement.Q<FloatField>("limits-yaw-min");
            if (_limitsYawMinField == null)
            {
                _limitsYawMinField = rootVisualElement.Q<FloatField>("limits-yaw-min-field");
            }
            _limitsYawMaxField = rootVisualElement.Q<FloatField>("limits-yaw-max");
            if (_limitsYawMaxField == null)
            {
                _limitsYawMaxField = rootVisualElement.Q<FloatField>("limits-yaw-max-field");
            }
            _limitsPitchMinField = rootVisualElement.Q<FloatField>("limits-pitch-min");
            if (_limitsPitchMinField == null)
            {
                _limitsPitchMinField = rootVisualElement.Q<FloatField>("limits-pitch-min-field");
            }
            _limitsPitchMaxField = rootVisualElement.Q<FloatField>("limits-pitch-max");
            if (_limitsPitchMaxField == null)
            {
                _limitsPitchMaxField = rootVisualElement.Q<FloatField>("limits-pitch-max-field");
            }
            if (_light3DYawSlider != null)
            {
                // HDRI skies keep a fixed texture orientation: the yaw sweep
                // moves Procedural/PBR skies only. Documented on the control
                // itself (no sky-type detection in code).
                _light3DYawSlider.tooltip =
                    "Horizontal rotation of the directional light (-180 to 180). Moves Procedural/PBR skies; HDRI skies stay static by design (fixed texture orientation).";
            }
            if (_light3DPitchSlider != null)
            {
                _light3DPitchSlider.tooltip =
                    "Vertical angle of the directional light (-90 to 90). Moves Procedural/PBR skies; HDRI skies stay static by design (fixed texture orientation).";
            }
            _sceneTabButton = rootVisualElement.Q<Button>("scene-tab-button");
            _settingsTabButton = rootVisualElement.Q<Button>("settings-tab-button");
            _sceneTabScroll = rootVisualElement.Q<ScrollView>("scene-tab-scroll");
            _settingsTabScroll = rootVisualElement.Q<ScrollView>("settings-tab-scroll");
        }

        private void ApplySectionIcons()
        {
            SetSectionIcon("stt-icon-preview", "d_SceneAsset Icon");
            SetSectionIcon("stt-icon-resolution", "d_Settings Icon");
            SetFoldoutSectionIcon("framing-foldout", "d_Camera Icon");
            SetFoldoutSectionIcon("bgfx-foldout", "d_Skybox Icon");
            SetFoldoutSectionIcon("lighting-foldout", "d_Light Icon", "d_LightGroup Icon");
            SetSectionIcon(
                "stt-icon-layers",
                "filterbylabel",
                "d_TagManager Icon",
                "d_SortingLayer Icon",
                "d_Layers Icon"
            );
            SetSectionIcon("stt-icon-batch", "d_FolderOpened Icon");
            SetSectionIcon("stt-icon-storage", "d_Folder Icon", "d_FolderSaved Icon");
            SetSectionIcon(
                "stt-icon-tracking",
                "d_PrefabRegular Icon",
                "d_PrefabVariant Icon",
                "d_Prefab Icon"
            );
            SetSectionIcon("stt-icon-overlay", "d_PrefabRegular Icon", "d_Prefab Icon");
            SetSectionIcon("stt-icon-cache", "d_Profiler.Memory Icon", "d_SmallToggle Icon");
            SetSectionIcon("stt-icon-limits", "d_Light Icon", "d_LightGroup Icon");
        }

        private void SetSectionIcon(string elementName, params string[] iconNames)
        {
            Image icon = rootVisualElement.Q<Image>(elementName);
            if (icon == null)
            {
                return;
            }
            foreach (string iconName in iconNames)
            {
                try
                {
                    // Suppress Unity's error log for missing icons; the
                    // try-catch handles the exception but the error is
                    // logged before it is thrown.
                    bool logEnabled = Debug.unityLogger.logEnabled;
                    Debug.unityLogger.logEnabled = false;
                    GUIContent content = EditorGUIUtility.IconContent(iconName);
                    Debug.unityLogger.logEnabled = logEnabled;
                    if (content != null && content.image != null)
                    {
                        icon.image = content.image;
                        icon.EnableInClassList("stt-hidden", false);
                        return;
                    }
                }
                catch
                {
                    // Icon not available in this Unity version; try next.
                }
            }
            // No icon found; leave hidden.
        }

        /// <summary>
        /// Finds a Foldout by name, creates an Image element with the given icon,
        /// and inserts it into the Foldout's toggle header before the label text.
        /// The toggle header is an internal element with class "unity-toggle__input"
        /// that contains the checkmark and label.
        /// </summary>
        private void SetFoldoutSectionIcon(string foldoutName, params string[] iconNames)
        {
            Foldout foldout = rootVisualElement.Q<Foldout>(foldoutName);
            if (foldout == null)
            {
                return;
            }
            // The toggle header is the internal "unity-toggle__input" element.
            VisualElement toggleInput = foldout.Q(className: "unity-toggle__input");
            if (toggleInput == null || toggleInput.childCount < 2)
            {
                return;
            }
            foreach (string iconName in iconNames)
            {
                try
                {
                    bool logEnabled = Debug.unityLogger.logEnabled;
                    Debug.unityLogger.logEnabled = false;
                    GUIContent content = EditorGUIUtility.IconContent(iconName);
                    Debug.unityLogger.logEnabled = logEnabled;
                    if (content != null && content.image != null)
                    {
                        var icon = new Image();
                        icon.image = content.image;
                        icon.name = "stt-foldout-icon";
                        icon.AddToClassList("stt-section-icon");
                        // Insert at index 1: after checkmark (0), before label (1)
                        toggleInput.Insert(1, icon);
                        return;
                    }
                }
                catch
                {
                    // Icon not available in this Unity version; try next.
                }
            }
        }

        private void WireCallbacks()
        {
            if (_generateButton != null)
            {
                _generateButton.clicked += GenerateThumbnail;
            }
            if (_deleteThumbnailButton != null)
            {
                _deleteThumbnailButton.clicked += DeleteActiveUniThumb;
            }
            if (_particlePreviewTimeSlider != null)
            {
                _particlePreviewTimeSlider.RegisterValueChangedCallback(evt =>
                {
                    _particlePreviewTime = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_clearFolderButton != null)
            {
                _clearFolderButton.clicked += ClearFolderThumbnails;
            }
            if (_resolutionPopup != null)
            {
                _resolutionPopup.RegisterValueChangedCallback(OnResolutionChanged);
            }
            if (_orthographicToggle != null)
            {
                _orthographicToggle.RegisterValueChangedCallback(evt =>
                {
                    _orthographic2D = evt.newValue;
                    UpdateFramingState();
                    MarkPreviewDirty();
                });
            }
            if (_sceneViewAngleToggle != null)
            {
                _sceneViewAngleToggle.RegisterValueChangedCallback(evt =>
                {
                    _useSceneViewAngle = evt.newValue;
                    UpdateFramingState();
                    MarkPreviewDirty();
                });
            }
            if (_framingDistanceSlider != null)
            {
                _framingDistanceSlider.RegisterValueChangedCallback(evt =>
                {
                    _orbitDistanceMultiplier = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_orbitFovSlider != null)
            {
                _orbitFovSlider.RegisterValueChangedCallback(evt =>
                {
                    _orbitFov = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_fitFactorSlider != null)
            {
                _fitFactorSlider.RegisterValueChangedCallback(evt =>
                {
                    _fitFactor = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_orbitYawSlider != null)
            {
                _orbitYawSlider.RegisterValueChangedCallback(evt =>
                {
                    _orbitYaw = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_orbitPitchSlider != null)
            {
                _orbitPitchSlider.RegisterValueChangedCallback(evt =>
                {
                    _orbitPitch = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_presetFrontButton != null)
            {
                _presetFrontButton.clicked += () =>
                {
                    ApplyOrbitPreset(-180f, 0f);
                    MarkPreviewDirty();
                };
            }
            if (_presetThreequarterButton != null)
            {
                _presetThreequarterButton.clicked += () =>
                {
                    ApplyOrbitPreset(45f, 25f);
                    MarkPreviewDirty();
                };
            }
            if (_presetTopButton != null)
            {
                _presetTopButton.clicked += () =>
                {
                    ApplyOrbitPreset(0f, 85f);
                    MarkPreviewDirty();
                };
            }
            if (_bgModeField != null)
            {
                _bgModeField.RegisterValueChangedCallback(evt =>
                {
                    _backgroundMode = (BackgroundMode)evt.newValue;
                    UpdateBgColorState();
                    MarkPreviewDirty();
                });
            }
            if (_bgColorField != null)
            {
                _bgColorField.RegisterValueChangedCallback(evt =>
                {
                    _backgroundColor = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_storageModeField != null)
            {
                _storageModeField.RegisterValueChangedCallback(OnStorageModeChanged);
            }
            if (_autoRegenToggle != null)
            {
                _autoRegenToggle.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetAutoRegenerateOnSave(evt.newValue);
                });
            }
            if (_updateCheckToggle != null)
            {
                _updateCheckToggle.RegisterValueChangedCallback(evt =>
                {
                    UniThumbUpdateChecker.SetUpdateCheckEnabled(evt.newValue);
                });
            }
            if (_allowDiscardToggle != null)
            {
                _allowDiscardToggle.RegisterValueChangedCallback(evt =>
                {
                    UniThumbBatchMenus.AllowDiscardUnsavedChanges = evt.newValue;
                });
            }
            if (_iconOverlayToggle != null)
            {
                _iconOverlayToggle.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetIconOverlayEnabled(evt.newValue);
                    UniThumbIconService.RefreshFromSettings();
                });
            }
            if (_listPaddingXSlider != null)
            {
                _listPaddingXSlider.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetListPaddingX(evt.newValue);
                    UniThumbIconService.RefreshFromSettings();
                });
            }
            if (_listPaddingYSlider != null)
            {
                _listPaddingYSlider.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetListPaddingY(evt.newValue);
                    UniThumbIconService.RefreshFromSettings();
                });
            }
            if (_iconMaxSizeSlider != null)
            {
                _iconMaxSizeSlider.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetIconMaxSize(evt.newValue);
                    UniThumbIconService.RefreshFromSettings();
                });
            }
            if (_cacheSizeSlider != null)
            {
                _cacheSizeSlider.RegisterValueChangedCallback(evt =>
                {
                    UniThumbSettings.Get().SetCacheSizeMb(evt.newValue);
                    _cacheSizeSlider.label = "Cache Limit (" + evt.newValue + " MB)";
                });
            }
            if (_lightingToggle != null)
            {
                _lightingToggle.RegisterValueChangedCallback(evt =>
                {
                    _useLightingOverride = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_postfxToggle != null)
            {
                _postfxToggle.RegisterValueChangedCallback(evt =>
                {
                    _wantPostProcessing = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_postfxProfileField != null)
            {
                _postfxProfileField.RegisterValueChangedCallback(evt =>
                {
                    _postProcessingProfile = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_captureUiToggle != null)
            {
                _captureUiToggle.RegisterValueChangedCallback(evt =>
                {
                    _captureUi = evt.newValue;
                    _uiScaleSlider?.SetEnabled(evt.newValue);
                    MarkPreviewDirty();
                });
            }
            if (_uiScaleSlider != null)
            {
                _uiScaleSlider.RegisterValueChangedCallback(evt =>
                {
                    float snapped = Mathf.Round(evt.newValue / 0.05f) * 0.05f;
                    if (Mathf.Abs(snapped - evt.newValue) > 0.0001f)
                    {
                        _uiScaleSlider.SetValueWithoutNotify(snapped);
                    }
                    _uiScale = snapped;
                    MarkPreviewDirty();
                });
            }
            if (_hdrpExposureField != null)
            {
                _hdrpExposureField.RegisterValueChangedCallback(evt =>
                {
                    UniThumbCapture.HdrpFixedExposure = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_layersMask != null)
            {
                _layersMask.RegisterValueChangedCallback(evt =>
                {
                    _layerMask = (LayerMask)evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_batchFolderField != null)
            {
                _batchFolderField.RegisterValueChangedCallback(evt =>
                {
                    _batchFolderInput = evt.newValue;
                    ValidateBatchFolderInput();
                    RefreshGenerateFolderEnabled();
                });
                _batchFolderField.RegisterCallback<FocusOutEvent>(evt =>
                {
                    ValidateBatchFolderInput();
                    RefreshGenerateFolderEnabled();
                });
            }
            if (_browseButton != null)
            {
                _browseButton.clicked += OpenFolderMenu;
            }
            if (_useSceneFolderButton != null)
            {
                _useSceneFolderButton.clicked += UseActiveSceneFolderForBatch;
            }
            if (_batchScopeField != null)
            {
                _batchScopeField.RegisterValueChangedCallback(evt =>
                {
                    _batchScope = (UniThumbBatchMenus.BatchScope)evt.newValue;
                });
            }
            if (_generateFolderButton != null)
            {
                _generateFolderButton.clicked += StartFolderBatch;
            }
            if (_cancelBatchButton != null)
            {
                _cancelBatchButton.clicked += CancelBatch;
            }
            if (_updateBannerDismissButton != null)
            {
                _updateBannerDismissButton.clicked += DismissUpdateBanner;
            }
            if (_sceneTabButton != null)
            {
                _sceneTabButton.clicked += () => SelectTab(0);
            }
            if (_settingsTabButton != null)
            {
                _settingsTabButton.clicked += () => SelectTab(1);
            }
            if (_batchFolderRow != null)
            {
                RegisterBatchFolderDragDrop(_batchFolderRow);
            }
            if (_lightingModeField != null)
            {
                _lightingModeField.RegisterValueChangedCallback(evt =>
                {
                    _lightingMode = (LightingMode)evt.newValue;
                    UpdateLightingVisibility();
                    MarkPreviewDirty();
                });
            }
            if (_light2DIntensitySlider != null)
            {
                _light2DIntensitySlider.RegisterValueChangedCallback(evt =>
                {
                    _light2DIntensity = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light2DSortingLayersMask != null)
            {
                _light2DSortingLayersMask.RegisterValueChangedCallback(evt =>
                {
                    _light2DSortingLayers = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DIntensitySlider != null)
            {
                _light3DIntensitySlider.RegisterValueChangedCallback(evt =>
                {
                    _light3DIntensity = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DPrefabIntensitySlider != null)
            {
                _light3DPrefabIntensitySlider.RegisterValueChangedCallback(evt =>
                {
                    _light3DPrefabIntensity = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DShadowsToggle != null)
            {
                _light3DShadowsToggle.RegisterValueChangedCallback(evt =>
                {
                    _light3DShadows = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DColorField != null)
            {
                _light3DColorField.RegisterValueChangedCallback(evt =>
                {
                    _light3DColor = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DYawSlider != null)
            {
                _light3DYawSlider.RegisterValueChangedCallback(evt =>
                {
                    _light3DYaw = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_light3DPitchSlider != null)
            {
                _light3DPitchSlider.RegisterValueChangedCallback(evt =>
                {
                    _light3DPitch = evt.newValue;
                    MarkPreviewDirty();
                });
            }
            if (_limitsIntensityMaxField != null)
            {
                _limitsIntensityMaxField.RegisterValueChangedCallback(evt =>
                {
                    _light3DIntensityMax = Mathf.Clamp(evt.newValue, 0.01f, 10f);
                    _limitsIntensityMaxField.SetValueWithoutNotify(_light3DIntensityMax);
                    _light3DIntensity = Mathf.Clamp(_light3DIntensity, 0f, _light3DIntensityMax);
                    _light3DPrefabIntensity = Mathf.Clamp(
                        _light3DPrefabIntensity,
                        0f,
                        _light3DIntensityMax
                    );
                    UpdateLight3DSliderRanges();
                    MarkPreviewDirty();
                });
            }
            if (_limitsYawMinField != null)
            {
                _limitsYawMinField.RegisterValueChangedCallback(evt =>
                {
                    _light3DYawMin = Mathf.Min(evt.newValue, _light3DYawMax - 0.01f);
                    _limitsYawMinField.SetValueWithoutNotify(_light3DYawMin);
                    _light3DYaw = Mathf.Clamp(_light3DYaw, _light3DYawMin, _light3DYawMax);
                    UpdateLight3DSliderRanges();
                    MarkPreviewDirty();
                });
            }
            if (_limitsYawMaxField != null)
            {
                _limitsYawMaxField.RegisterValueChangedCallback(evt =>
                {
                    _light3DYawMax = Mathf.Max(evt.newValue, _light3DYawMin + 0.01f);
                    _limitsYawMaxField.SetValueWithoutNotify(_light3DYawMax);
                    _light3DYaw = Mathf.Clamp(_light3DYaw, _light3DYawMin, _light3DYawMax);
                    UpdateLight3DSliderRanges();
                    MarkPreviewDirty();
                });
            }
            if (_limitsPitchMinField != null)
            {
                _limitsPitchMinField.RegisterValueChangedCallback(evt =>
                {
                    _light3DPitchMin = Mathf.Min(evt.newValue, _light3DPitchMax - 0.01f);
                    _limitsPitchMinField.SetValueWithoutNotify(_light3DPitchMin);
                    _light3DPitch = Mathf.Clamp(_light3DPitch, _light3DPitchMin, _light3DPitchMax);
                    UpdateLight3DSliderRanges();
                    MarkPreviewDirty();
                });
            }
            if (_limitsPitchMaxField != null)
            {
                _limitsPitchMaxField.RegisterValueChangedCallback(evt =>
                {
                    _light3DPitchMax = Mathf.Max(evt.newValue, _light3DPitchMin + 0.01f);
                    _limitsPitchMaxField.SetValueWithoutNotify(_light3DPitchMax);
                    _light3DPitch = Mathf.Clamp(_light3DPitch, _light3DPitchMin, _light3DPitchMax);
                    UpdateLight3DSliderRanges();
                    MarkPreviewDirty();
                });
            }
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        private void SelectTab(int index)
        {
            _sceneTabScroll?.EnableInClassList("stt-hidden", index != 0);
            _settingsTabScroll?.EnableInClassList("stt-hidden", index != 1);
            _sceneTabButton?.EnableInClassList("stt-tab-selected", index == 0);
            _settingsTabButton?.EnableInClassList("stt-tab-selected", index == 1);
        }

        private void PushState()
        {
            UpdateUpdateBanner();
            UpdateActiveSceneLabel();
            UpdateGenerateState();

            if (_resolutionPopup != null)
            {
                List<string> choices = new List<string>(k_PresetResolutions.Length);
                for (int i = 0; i < k_PresetResolutions.Length; i++)
                {
                    choices.Add(k_PresetResolutions[i] + "x" + k_PresetResolutions[i]);
                }
                _resolutionPopup.choices = choices;
                _resolutionPopup.index = Mathf.Clamp(_resolutionIndex, 0, choices.Count - 1);
            }
            if (_orthographicToggle != null)
            {
                _orthographicToggle.SetValueWithoutNotify(_orthographic2D);
            }
            if (_sceneViewAngleToggle != null)
            {
                _sceneViewAngleToggle.SetValueWithoutNotify(_useSceneViewAngle);
            }
            if (_framingDistanceSlider != null)
            {
                _framingDistanceSlider.SetValueWithoutNotify(_orbitDistanceMultiplier);
            }
            if (_orbitFovSlider != null)
            {
                _orbitFovSlider.SetValueWithoutNotify((int)_orbitFov);
            }
            if (_fitFactorSlider != null)
            {
                _fitFactorSlider.SetValueWithoutNotify(_fitFactor);
            }
            if (_orbitYawSlider != null)
            {
                _orbitYawSlider.SetValueWithoutNotify(_orbitYaw);
            }
            if (_orbitPitchSlider != null)
            {
                _orbitPitchSlider.SetValueWithoutNotify(_orbitPitch);
            }
            UpdateFramingState();

            if (_particlePreviewTimeSlider != null)
            {
                _particlePreviewTimeSlider.SetValueWithoutNotify(_particlePreviewTime);
            }
            UpdateParticleSliderVisibility();

            if (_bgModeField != null)
            {
                // UXML EnumFields are untyped until Init; Init also sets the
                // initial value. There is no EnumType property on
                // UnityEngine.UIElements.EnumField in Unity 6000.4.
                _bgModeField.Init((Enum)_backgroundMode);
            }
            if (_bgColorField != null)
            {
                _bgColorField.SetValueWithoutNotify(_backgroundColor);
            }
            UpdateBgColorState();
            if (_lightingToggle != null)
            {
                _lightingToggle.SetValueWithoutNotify(_useLightingOverride);
            }
            if (_postfxToggle != null)
            {
                _postfxToggle.SetValueWithoutNotify(_wantPostProcessing);
            }
            if (_postfxProfileField != null)
            {
                _postfxProfileField.SetValueWithoutNotify(_postProcessingProfile);
            }
            if (_captureUiToggle != null)
            {
                _captureUiToggle.SetValueWithoutNotify(_captureUi);
            }
            if (_uiScaleSlider != null)
            {
                _uiScaleSlider.SetValueWithoutNotify(_uiScale);
                _uiScaleSlider.SetEnabled(_captureUi);
            }
            if (_hdrpExposureField != null)
            {
                _hdrpExposureField.SetValueWithoutNotify(UniThumbCapture.HdrpFixedExposure);
            }
            if (_layersMask != null)
            {
                _layersMask.choices = new List<string>(
                    UnityEditorInternal.InternalEditorUtility.layers
                );
                _layersMask.value = _layerMask.value;
            }
            if (_light2DSortingLayersMask != null)
            {
                SortingLayer[] sortingLayers = SortingLayer.layers;
                var names = new List<string>(sortingLayers.Length);
                for (int i = 0; i < sortingLayers.Length; i++)
                {
                    names.Add(sortingLayers[i].name);
                }
                _light2DSortingLayersMask.choices = names;
                // Validate: if any set bit exceeds available layers, reset to all
                int maxValid = sortingLayers.Length > 0 ? (1 << sortingLayers.Length) - 1 : 0;
                int validValue =
                    (_light2DSortingLayers & maxValid) != 0
                        ? _light2DSortingLayers & maxValid
                        : maxValid;
                _light2DSortingLayersMask.value = validValue;
            }

            if (_batchFolderField != null)
            {
                _batchFolderField.SetValueWithoutNotify(_batchFolderInput ?? string.Empty);
            }
            if (_batchScopeField != null)
            {
                // UXML EnumFields are untyped until Init (Unity 6000.4 has
                // no EnumType property), same as _bgModeField above.
                _batchScopeField.Init((Enum)_batchScope);
            }
            if (_noThumbnailLabel != null)
            {
                _noThumbnailLabel.text = k_NoThumbnailLabel;
            }
            if (_batchInvalidLabel != null)
            {
                _batchInvalidLabel.text = k_InvalidFolderLabel;
            }
            if (_framingDisabledHint != null)
            {
                _framingDisabledHint.text = k_DisabledFramingLabel;
            }
            // Storage mode is pushed from UniThumbSettings, never from the
            // field itself; the guard keeps Init from reaching the change
            // callback during state push.
            _syncingStorageMode = true;
            try
            {
                if (_storageModeField != null)
                {
                    // UXML EnumFields are untyped until Init (Unity 6000.4 has
                    // no EnumType property), same as _bgModeField above.
                    _storageModeField.Init((Enum)UniThumbSettings.Get().StorageMode);
                }
            }
            finally
            {
                _syncingStorageMode = false;
            }
            if (_storageModeHint != null)
            {
                _storageModeHint.text = StorageModeHintText(UniThumbSettings.Get().StorageMode);
            }
            if (_autoRegenToggle != null)
            {
                _autoRegenToggle.SetValueWithoutNotify(UniThumbSettings.Get().AutoRegenerateOnSave);
                _autoRegenToggle.tooltip = UniThumbPrefabAutoRegen.AutoRegenTooltip;
            }
            if (_updateCheckToggle != null)
            {
                _updateCheckToggle.SetValueWithoutNotify(UniThumbUpdateChecker.UpdateCheckEnabled);
            }
            if (_allowDiscardToggle != null)
            {
                _allowDiscardToggle.SetValueWithoutNotify(
                    UniThumbBatchMenus.AllowDiscardUnsavedChanges
                );
            }
            if (_iconOverlayToggle != null)
            {
                _iconOverlayToggle.SetValueWithoutNotify(UniThumbSettings.Get().IconOverlayEnabled);
            }
            if (_listPaddingXSlider != null)
            {
                _listPaddingXSlider.SetValueWithoutNotify(UniThumbSettings.Get().ListPaddingX);
            }
            if (_listPaddingYSlider != null)
            {
                _listPaddingYSlider.SetValueWithoutNotify(UniThumbSettings.Get().ListPaddingY);
            }
            if (_iconMaxSizeSlider != null)
            {
                _iconMaxSizeSlider.SetValueWithoutNotify(UniThumbSettings.Get().IconMaxSize);
            }
            if (_cacheSizeSlider != null)
            {
                _cacheSizeSlider.SetValueWithoutNotify(UniThumbSettings.Get().CacheSizeMb);
                _cacheSizeSlider.label =
                    "Cache Limit (" + UniThumbSettings.Get().CacheSizeMb + " MB)";
            }
            UpdateFooterLabel();
            if (_lightingModeField != null)
            {
                _lightingModeField.Init(_lightingMode);
                _lightingModeField.SetValueWithoutNotify(_lightingMode);
            }
            if (_light2DIntensitySlider != null)
            {
                _light2DIntensitySlider.SetValueWithoutNotify(_light2DIntensity);
            }
            if (_limitsIntensityMaxField != null)
            {
                _limitsIntensityMaxField.SetValueWithoutNotify(_light3DIntensityMax);
            }
            if (_limitsYawMinField != null)
            {
                _limitsYawMinField.SetValueWithoutNotify(_light3DYawMin);
            }
            if (_limitsYawMaxField != null)
            {
                _limitsYawMaxField.SetValueWithoutNotify(_light3DYawMax);
            }
            if (_limitsPitchMinField != null)
            {
                _limitsPitchMinField.SetValueWithoutNotify(_light3DPitchMin);
            }
            if (_limitsPitchMaxField != null)
            {
                _limitsPitchMaxField.SetValueWithoutNotify(_light3DPitchMax);
            }
            _light3DIntensity = Mathf.Clamp(_light3DIntensity, 0f, _light3DIntensityMax);
            _light3DPrefabIntensity = Mathf.Clamp(
                _light3DPrefabIntensity,
                0f,
                _light3DIntensityMax
            );
            _light3DYaw = Mathf.Clamp(_light3DYaw, _light3DYawMin, _light3DYawMax);
            _light3DPitch = Mathf.Clamp(_light3DPitch, _light3DPitchMin, _light3DPitchMax);
            UpdateLight3DSliderRanges();
            if (_light3DShadowsToggle != null)
            {
                _light3DShadowsToggle.SetValueWithoutNotify(_light3DShadows);
            }
            if (_light3DColorField != null)
            {
                _light3DColorField.SetValueWithoutNotify(_light3DColor);
            }
            UpdateLightingVisibility();
            if (_batchProgressBar != null)
            {
                _batchProgressBar.highValue = 1f;
            }

            RefreshPreviewTexture();
            UpdateActiveSceneLabel();
            UpdatePreviewUI();
            MarkPreviewDirty();
            SetStatus(_statusMessage, _statusType);
            UpdateBatchUI();
        }

        private void UpdateActiveSceneLabel()
        {
            bool isPrefab = IsPrefabPreview;
            if (_activeSceneLabel != null)
            {
                if (isPrefab)
                {
                    _activeSceneLabel.text = string.IsNullOrEmpty(_previewPrefabPath)
                        ? "(prefab)"
                        : _previewPrefabPath;
                }
                else
                {
                    string scenePath = EditorSceneManager.GetActiveScene().path;
                    _activeSceneLabel.text = string.IsNullOrEmpty(scenePath)
                        ? "(unsaved / no scene open)"
                        : scenePath;
                }
            }
            UpdateSourceModeUI();
        }

        /// <summary>
        /// Prefab-mode indicator text seams. Static for EditMode tests; the
        /// chip and header read these so both modes stay consistent.
        /// </summary>
        internal static string SourceModeBadgeText(bool isPrefabPreview)
        {
            return isPrefabPreview ? "Prefab" : "Scene";
        }

        internal static string SourceModeTitleText(bool isPrefabPreview)
        {
            return isPrefabPreview ? "Selected Prefab" : "Active Scene";
        }

        /// <summary>
        /// Prefab-mode visual indicator: header chip plus context text, and a
        /// chip above the preview while a prefab is selected. Class and text
        /// assignment only, so hidden-tab content stays safe. Called from
        /// UpdateActiveSceneLabel and UpdatePreviewUI to cover selection,
        /// scene-switch, and post-generate refresh paths.
        /// </summary>
        private void UpdateSourceModeUI()
        {
            bool isPrefab = IsPrefabPreview;
            if (_sourceTitleLabel != null)
            {
                _sourceTitleLabel.text = SourceModeTitleText(isPrefab);
            }
            if (_sourceModeBadge != null)
            {
                _sourceModeBadge.text = SourceModeBadgeText(isPrefab);
                _sourceModeBadge.EnableInClassList("stt-mode-badge-prefab", isPrefab);
                _sourceModeBadge.EnableInClassList("stt-mode-badge-scene", !isPrefab);
                _sourceModeBadge.EnableInClassList("stt-hidden", false);
            }
            if (_previewModeBadge != null)
            {
                _previewModeBadge.EnableInClassList("stt-hidden", !isPrefab);
            }
            if (_previewModeRow != null)
            {
                _previewModeRow.EnableInClassList("stt-hidden", !isPrefab);
            }
            if (_generateButton != null)
            {
                _generateButton.tooltip = isPrefab
                    ? "Generate thumbnail for the selected prefab (orbit framing)."
                    : "Generate thumbnail for the active scene.";
            }
            if (_deleteThumbnailButton != null)
            {
                _deleteThumbnailButton.tooltip = isPrefab
                    ? "Deletes the thumbnail of the selected prefab."
                    : "Deletes the thumbnail of the currently open scene.";
            }
        }

        private void UpdateGenerateState()
        {
            bool busy = UniThumbGuard.IsGenerating;
            if (_generateButton != null)
            {
                _generateButton.SetEnabled(!busy);
            }
            if (_deleteThumbnailButton != null)
            {
                _deleteThumbnailButton.SetEnabled(!busy);
            }
            if (_generateBusyLabel != null)
            {
                _generateBusyLabel.EnableInClassList("stt-hidden", !busy);
            }
        }

        /// <summary>
        /// Prefab preview probe: true for a prefab asset path. Prefabs always
        /// use orbit framing (CapturePrefab forces UseSceneViewAngle off), so
        /// framing gates treat prefab mode as orbit mode. Static for EditMode
        /// tests.
        /// </summary>
        internal static bool ShouldUsePrefabOrbitFraming(string previewPrefabPath)
        {
            return !string.IsNullOrEmpty(previewPrefabPath);
        }

        /// <summary>
        /// Pure framing-gate source shared by UpdateFramingState and tests:
        /// the Scene View angle only disables framing in scene mode.
        /// </summary>
        internal static bool ComputeFramingDisabled(
            bool useSceneViewAngle,
            string previewPrefabPath
        )
        {
            return useSceneViewAngle && !ShouldUsePrefabOrbitFraming(previewPrefabPath);
        }

        /// <summary>
        /// True while a prefab asset is selected (prefab preview mode).
        /// </summary>
        internal bool IsPrefabPreview => ShouldUsePrefabOrbitFraming(_previewPrefabPath);

        /// <summary>
        /// True when the orbit sliders are disabled and hidden.
        /// </summary>
        internal bool IsFramingDisabled =>
            ComputeFramingDisabled(_useSceneViewAngle, _previewPrefabPath);

        /// <summary>
        /// Render identity for the live-preview skip: prefab path (or active
        /// scene path), selection GUID, and the full capture settings
        /// fingerprint. Two renders with equal keys would produce identical
        /// pixels, so the second is skipped. Pure and static for EditMode
        /// tests.
        /// </summary>
        internal static string BuildPreviewRenderKey(
            string prefabPath,
            string guid,
            string scenePath,
            CaptureSettings settings
        )
        {
            string target = !string.IsNullOrEmpty(prefabPath)
                ? "prefab:" + prefabPath
                : "scene:" + (scenePath ?? string.Empty);
            return target
                + "|"
                + (guid ?? string.Empty)
                + "|"
                + BuildPreviewSettingsFingerprint(settings);
        }

        /// <summary>
        /// Settings half of the preview render key: every BuildSettings field
        /// that reaches the capture, joined invariantly. Any user-facing
        /// setting change flips the key so the preview re-renders; content
        /// edits (invisible here) invalidate the key through
        /// InvalidatePreviewRenderKey instead.
        /// </summary>
        internal static string BuildPreviewSettingsFingerprint(CaptureSettings settings)
        {
            System.Globalization.CultureInfo invariant = System
                .Globalization
                .CultureInfo
                .InvariantCulture;
            string profileKey;
            if (settings.PostProcessingProfile != null)
            {
#if UNITY_6000_4_OR_NEWER
                profileKey = settings.PostProcessingProfile.GetEntityId().ToString();
#else
                profileKey = settings.PostProcessingProfile.GetInstanceID().ToString(invariant);
#endif
            }
            else
            {
                profileKey = "0";
            }
            string sortingLayersKey =
                settings.Light2DSortingLayerIds == null
                    ? "null"
                    : string.Join(
                        ",",
                        System.Array.ConvertAll(
                            settings.Light2DSortingLayerIds,
                            id => id.ToString(invariant)
                        )
                    );
            return string.Join(
                "|",
                new string[]
                {
                    settings.Width.ToString(invariant),
                    settings.Height.ToString(invariant),
                    settings.UseSceneViewAngle.ToString(),
                    settings.orthographic.ToString(),
                    settings.OrbitYaw.ToString(invariant),
                    settings.OrbitPitch.ToString(invariant),
                    settings.orbitDistanceMultiplier.ToString(invariant),
                    settings.OrbitFov.ToString(invariant),
                    settings.FitFactor.ToString(invariant),
                    settings.UseLightingOverride.ToString(),
                    settings.BackgroundColor.ToString(),
                    settings.BackgroundMode.ToString(),
                    settings.WantPostProcessing.ToString(),
                    profileKey,
                    settings.CaptureUi.ToString(),
                    settings.UiScale.ToString(invariant),
                    settings.layerMask.value.ToString(invariant),
                    settings.Light2DMode.ToString(),
                    settings.Light2DIntensity.ToString(invariant),
                    sortingLayersKey,
                    settings.Light3DIntensity.ToString(invariant),
                    settings.Light3DPrefabIntensity.ToString(invariant),
                    settings.Light3DShadows.ToString(),
                    settings.Light3DColor.ToString(),
                    settings.Light3DYaw.ToString(invariant),
                    settings.Light3DPitch.ToString(invariant),
                    settings.ParticlePreviewTime.ToString(invariant),
                }
            );
        }

        private void UpdateFramingState()
        {
            // The Scene View angle drives the framing entirely in scene
            // mode: sliders stay disabled and orbit controls stay hidden
            // whenever it is on, including with Orthographic (2D) enabled
            // (the orbit fit only kicks in as a SceneView-mode fallback).
            // Prefab mode always uses orbit framing, so the gate never
            // applies while a prefab is selected.
            bool framingDisabled = IsFramingDisabled;
            if (_framingDistanceSlider != null)
            {
                _framingDistanceSlider.SetEnabled(!framingDisabled);
            }
            if (_orbitFovSlider != null)
            {
                _orbitFovSlider.SetEnabled(!framingDisabled);
            }
            if (_fitFactorSlider != null)
            {
                _fitFactorSlider.SetEnabled(!framingDisabled);
            }
            if (_framingDisabledHint != null)
            {
                _framingDisabledHint.EnableInClassList("stt-hidden", !framingDisabled);
            }
            if (_orbitControls != null)
            {
                _orbitControls.EnableInClassList("stt-hidden", framingDisabled);
            }
            // The Scene View angle is scene-only: while a prefab is selected
            // the toggle is disabled with an explanatory tooltip so the
            // no-op is visible instead of silent.
            if (_sceneViewAngleToggle != null)
            {
                _sceneViewAngleToggle.SetEnabled(!IsPrefabPreview);
                _sceneViewAngleToggle.tooltip = IsPrefabPreview
                    ? "Scene View angle applies to scenes only; prefabs always use orbit framing."
                    : "When off, orbit yaw/pitch below drive the capture camera.";
            }
        }

        /// <summary>
        /// Particle Time slider visibility: shown only while the current
        /// target (stage-open prefab wins, else the selected prefab)
        /// contains a ParticleSystem or VisualEffect; hidden for scenes
        /// and particle-less prefabs. Display-only: the slider value and
        /// its persist flow stay intact while hidden. Null-guarded so
        /// hidden-tab content and pre-CreateGUI calls stay safe.
        /// </summary>
        private void UpdateParticleSliderVisibility()
        {
            if (_particlePreviewTimeSlider == null)
            {
                return;
            }
            string prefabPath;
            bool hasPrefab = TryGetSelectedPrefabPath(out prefabPath);
            bool hasParticles = hasPrefab && UniThumbCapture.PrefabHasParticles(prefabPath);
            bool visible = UniThumbCapture.ShouldShowParticleSlider(hasPrefab, hasParticles);
            _particlePreviewTimeSlider.style.display = visible
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        private void UpdateBgColorState()
        {
            bool colorDisabled = _backgroundMode != BackgroundMode.SolidColor;
            if (_bgColorField != null)
            {
                _bgColorField.SetEnabled(!colorDisabled);
            }
        }

        private void UpdateLight3DSliderRanges()
        {
            if (_light3DIntensitySlider != null)
            {
                _light3DIntensitySlider.lowValue = 0f;
                _light3DIntensitySlider.highValue = _light3DIntensityMax;
                _light3DIntensitySlider.SetValueWithoutNotify(_light3DIntensity);
            }
            if (_light3DPrefabIntensitySlider != null)
            {
                _light3DPrefabIntensitySlider.lowValue = 0f;
                _light3DPrefabIntensitySlider.highValue = _light3DIntensityMax;
                _light3DPrefabIntensitySlider.SetValueWithoutNotify(_light3DPrefabIntensity);
            }
            if (_light3DYawSlider != null)
            {
                _light3DYawSlider.lowValue = _light3DYawMin;
                _light3DYawSlider.highValue = _light3DYawMax;
                _light3DYawSlider.SetValueWithoutNotify(_light3DYaw);
            }
            if (_light3DPitchSlider != null)
            {
                _light3DPitchSlider.lowValue = _light3DPitchMin;
                _light3DPitchSlider.highValue = _light3DPitchMax;
                _light3DPitchSlider.SetValueWithoutNotify(_light3DPitch);
            }
            UpdatePrefabIntensityMaxLabel();
        }

        internal static string FormatPrefabIntensityMaxLabel(float activeMax)
        {
            return "Max "
                + activeMax.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void UpdatePrefabIntensityMaxLabel()
        {
            if (_light3DPrefabIntensityMaxLabel == null)
            {
                return;
            }
            _light3DPrefabIntensityMaxLabel.text = FormatPrefabIntensityMaxLabel(
                _light3DIntensityMax
            );
        }

        private void UpdateLightingVisibility()
        {
            bool showIntensity = _lightingMode == LightingMode.Light2D;
            if (_light2DIntensitySlider != null)
            {
                _light2DIntensitySlider.EnableInClassList("stt-hidden", !showIntensity);
            }
            if (_light2DSortingLayersMask != null)
            {
                _light2DSortingLayersMask.EnableInClassList("stt-hidden", !showIntensity);
            }
            bool showLight3D = _lightingMode == LightingMode.Light3D;
            if (_light3DIntensitySlider != null)
            {
                _light3DIntensitySlider.EnableInClassList("stt-hidden", !showLight3D);
            }
            if (_light3DShadowsToggle != null)
            {
                _light3DShadowsToggle.EnableInClassList("stt-hidden", !showLight3D);
            }
            if (_light3DColorField != null)
            {
                _light3DColorField.EnableInClassList("stt-hidden", !showLight3D);
            }
            if (_light3DYawSlider != null)
            {
                _light3DYawSlider.EnableInClassList("stt-hidden", !showLight3D);
            }
            if (_light3DPitchSlider != null)
            {
                _light3DPitchSlider.EnableInClassList("stt-hidden", !showLight3D);
            }
            // Prefab intensity drives the explicit Light3D key plus the
            // None-fallback temp light, so it stays visible in None and
            // Light3D modes; Light2D mode never uses it.
            bool showPrefabIntensity =
                _lightingMode == LightingMode.Light3D || _lightingMode == LightingMode.None;
            if (_light3DPrefabIntensitySlider != null)
            {
                _light3DPrefabIntensitySlider.EnableInClassList("stt-hidden", !showPrefabIntensity);
            }
            if (_light3DPrefabIntensityMaxLabel != null)
            {
                _light3DPrefabIntensityMaxLabel.EnableInClassList(
                    "stt-hidden",
                    !showPrefabIntensity
                );
            }
        }

        /// <summary>
        /// Storage-mode change handler (the EnumField fires for every user pick).
        /// An unchanged selection only refreshes the hint. A real switch counts
        /// the thumbnails first; when any exist the user is asked whether to move
        /// them to the new location - Cancel reverts the field and the hint.
        /// On confirm the move runs BEFORE the mode switch (source is still the
        /// old folder), then the settings asset is persisted, the tracked folder
        /// is imported once, and the cache, icons and footer are refreshed.
        /// </summary>
        private void OnStorageModeChanged(ChangeEvent<Enum> evt)
        {
            if (_syncingStorageMode)
            {
                return;
            }
            if (UniThumbGuard.IsGenerating || UniThumbBatchMenus.IsBatchRunning)
            {
                if (_storageModeField != null)
                {
                    _storageModeField.SetValueWithoutNotify(
                        (Enum)UniThumbSettings.Get().StorageMode
                    );
                }
                UpdateStorageModeState();
                SetStatus(
                    "Storage mode switch refused: a generation or batch is in progress.",
                    MessageType.Warning
                );
                return;
            }
            StorageMode newMode = (StorageMode)evt.newValue;
            StorageMode oldMode = UniThumbSettings.Get().StorageMode;
            if (newMode == oldMode)
            {
                UpdateStorageModeState();
                return;
            }

            string targetFolder = UniThumbStorage.StorageFolderPath(newMode).Replace('\\', '/');
            int count = UniThumbStorage.CountThumbnails();
            if (count > 0)
            {
                bool move = EditorUtility.DisplayDialog(
                    "UniThumb",
                    "Move "
                        + count
                        + " existing thumbnail(s) to "
                        + targetFolder
                        + "? Files are removed from the old location after a successful move.",
                    "Move",
                    "Cancel"
                );
                if (!move)
                {
                    if (_storageModeField != null)
                    {
                        _storageModeField.SetValueWithoutNotify((Enum)oldMode);
                    }
                    UpdateStorageModeState();
                    SetStatus("Storage mode unchanged", MessageType.Info);
                    return;
                }
            }

            int moved = UniThumbStorage.MoveThumbnailsTo(newMode);
            UniThumbSettings settings = UniThumbSettings.Get();
            settings.SetStorageMode(newMode);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            // One refresh pass after every move: imports the PNGs as assets when
            // moving to TrackedInAssets (creating their .meta files) and removes
            // the orphaned folder .meta when moving back to LibraryCache.
            AssetDatabase.Refresh();
            UniThumbStorage.ClearCache();
            UniThumbIconService.ReapplyAllIcons();
            SetStatus(
                moved > 0
                    ? "Moved " + moved + " thumbnail(s) to " + targetFolder
                    : "No thumbnails to move",
                MessageType.Info
            );
            PushState();
        }

        /// <summary>
        /// Pushes the storage-mode hint text matching the active mode. Called
        /// from PushState and from the change handler (after apply or revert).
        /// </summary>
        private void UpdateStorageModeState()
        {
            if (_storageModeHint != null)
            {
                _storageModeHint.text = StorageModeHintText(UniThumbSettings.Get().StorageMode);
            }
        }

        private static string StorageModeHintText(StorageMode mode)
        {
            return mode == StorageMode.TrackedInAssets ? k_TrackedInAssetsHint : k_LibraryCacheHint;
        }

        /// <summary>
        /// Renders the footer: absolute path of the ACTIVE storage folder plus
        /// the keyboard shortcuts. Re-run after a storage-mode switch so the
        /// path reflects the new location.
        /// </summary>
        private void UpdateFooterLabel()
        {
            if (_footerLabel == null)
            {
                return;
            }
            _footerLabel.text =
                "Thumbnail Folder: "
                + UniThumbStorage.ActiveStorageFolderPath()
                + " | Ctrl+Enter: Generate | Ctrl+Shift+Enter: Batch | Esc: Cancel";
        }

        /// <summary>
        /// Flags the live preview for a re-render on the next editor update
        /// tick. Repaint path only - performs no I/O. Invoked directly by
        /// the window on hierarchy/undo-redo events.
        /// </summary>
        internal void MarkPreviewDirty()
        {
            if (_previewDirty)
            {
                return;
            }
            _previewDirty = true;
            EditorApplication.update += RenderLivePreview;
            Repaint();
        }

        /// <summary>
        /// Read-only preview dirty flag exposed for EditMode test assertions.
        /// </summary>
        internal bool IsPreviewDirty => _previewDirty;

        /// <summary>
        /// Heavy live-preview renders executed since open. Tests assert this
        /// stays put when a same-key selection or a collapsed burst must not
        /// re-render.
        /// </summary>
        internal int PreviewRenderCount => _previewRenderCount;

        /// <summary>
        /// RenderLivePreview early-outs taken because prefab path, settings,
        /// and selection matched the last heavy render.
        /// </summary>
        internal int PreviewSkipCount => _previewSkipCount;

        /// <summary>
        /// Drops the last-render key so the next RenderLivePreview always
        /// executes the heavy path. Called by content-context changes
        /// (hierarchy, undo-redo, prefab stage, scene switch) that the
        /// settings fingerprint cannot see. Generate paths do not call it:
        /// they refresh the cached texture directly and the unchanged-key
        /// skip correctly preserves it.
        /// </summary>
        internal void InvalidatePreviewRenderKey()
        {
            _lastRenderKey = null;
        }

        /// <summary>
        /// Test seam: clears the idle defer so the next RenderLivePreview
        /// runs immediately instead of waiting for the quiet window.
        /// </summary>
        internal void PreviewIdleReadyForTest()
        {
            _previewDeferUntil = 0;
        }

        /// <summary>
        /// Drops a pending dirty preview without rendering. The next
        /// RenderLivePreview tick consumes the request and unsubscribes.
        /// </summary>
        internal void CancelPendingPreview()
        {
            _previewCancelRequested = true;
        }

        /// <summary>
        /// True while a heavy preview render is on the stack. Re-entrant
        /// ticks coalesce instead of stacking a second heavy render.
        /// </summary>
        internal bool PreviewRenderInFlight => _previewRenderInFlight;

        /// <summary>
        /// Own hierarchy events filtered during preview mutations. Proof
        /// counter for the self-trigger fix; real edits never increment it.
        /// </summary>
        internal int PreviewOwnEventFilteredCount => _previewOwnEventFilteredCount;

        /// <summary>
        /// Marks the start of the preview's own hierarchy mutations
        /// (Instantiate plus temp objects). Hierarchy events fired while
        /// the depth is positive are ours and must not invalidate the key.
        /// Real user edits cannot interleave: the whole heavy section runs
        /// synchronously on the main thread.
        /// </summary>
        internal void BeginPreviewOwnMutation()
        {
            _previewOwnMutationDepth++;
        }

        /// <summary>
        /// Marks the end of the preview's own hierarchy mutations.
        /// </summary>
        internal void EndPreviewOwnMutation()
        {
            if (_previewOwnMutationDepth > 0)
            {
                _previewOwnMutationDepth--;
            }
        }

        /// <summary>
        /// True while the preview holds its own-mutation window.
        /// </summary>
        internal bool IsPreviewOwnMutation => _previewOwnMutationDepth > 0;

        /// <summary>
        /// True while the pointer is dragging. The heavy preview stays
        /// deferred (dirty plus subscribed) so MouseUp bursts coalesce to
        /// one render after release. Test seam via PreviewDragProbeOverride.
        /// </summary>
        internal static bool IsPreviewPointerDragging()
        {
            if (PreviewDragProbeOverride != null)
            {
                return PreviewDragProbeOverride();
            }
            return GUIUtility.hotControl != 0;
        }

        /// <summary>
        /// Shows the cancellable Generate progress bar. Returns true when
        /// the user pressed Cancel. Test seam via GenerateProgress overrides.
        /// </summary>
        private bool ShowGenerateProgress(string label, float progress)
        {
            GenerateProgressWasShown = true;
            if (GenerateProgressDisplayOverride != null)
            {
                return GenerateProgressDisplayOverride(k_GenerateProgressTitle, label, progress);
            }
            return EditorUtility.DisplayCancelableProgressBar(
                k_GenerateProgressTitle,
                label,
                progress
            );
        }

        /// <summary>
        /// Clears the Generate progress bar. Runs in finally so a throw or
        /// cancel can never leave the modal bar on screen.
        /// </summary>
        private void ClearGenerateProgress()
        {
            if (GenerateProgressClearOverride != null)
            {
                GenerateProgressClearOverride();
                return;
            }
            EditorUtility.ClearProgressBar();
        }

        /// <summary>
        /// Acquires the live-preview UI capture session. Runs the
        /// BeginUiSessionOverride test seam when installed, otherwise the
        /// real UiCaptureSession.Begin.
        /// </summary>
        internal static UniThumbCapture.UiCaptureSession BeginUiSession(Camera cam, float uiScale)
        {
            if (BeginUiSessionOverride != null)
            {
                return BeginUiSessionOverride(cam, uiScale);
            }
            return UniThumbCapture.UiCaptureSession.Begin(cam, uiScale);
        }

        /// <summary>
        /// Acquires the prefab live-preview UI session scoped to the instance
        /// subtree (ScreenSpaceCamera rebind plus Overlay retarget). Runs the
        /// BeginPrefabUiSessionOverride test seam when installed, otherwise
        /// the real UiCaptureSession.BeginPrefab. Null root or null camera
        /// returns an empty no-op session via BeginPrefab. Mirrors the capture
        /// helper exactly: the RT-proportional scale derives from the preview
        /// RT pixel size only (the resolution preset is ignored except for its
        /// pixels), including the ConstantPixelSize/null fallback to the
        /// overlay reference size, with the raw base times UiScale on overlay
        /// canvases and the fixed 0.2 planeDistance.
        /// </summary>
        internal static UniThumbCapture.UiCaptureSession BeginPrefabUiSession(
            GameObject prefabRoot,
            Camera cam,
            float uiScale
        )
        {
            if (BeginPrefabUiSessionOverride != null)
            {
                return BeginPrefabUiSessionOverride(prefabRoot, cam, uiScale);
            }
            return UniThumbCapture.UiCaptureSession.BeginPrefab(prefabRoot, cam, uiScale);
        }

        internal void RenderLivePreview()
        {
            // Check guard and validity FIRST, before clearing dirty state or
            // unsubscribing. When the guard is held (e.g. batch in progress),
            // re-schedule so the pending preview survives until the guard
            // releases, rather than being consumed and dropped permanently.
            if (UniThumbGuard.IsGenerating)
            {
                if (rootVisualElement == null)
                {
                    // Window destroyed - clean up completely.
                    _previewDirty = false;
                    EditorApplication.update -= RenderLivePreview;
                    return;
                }

                // Stale-guard watchdog: a stuck hold (crash between
                // TryEnter/Exit with no batch running) is force-released
                // here so the preview never bricks silently; the healthy
                // path below then renders the pending preview.
                if (UniThumbGuard.RecoverIfStale())
                {
                    Debug.LogWarning(
                        "[UniThumb] Live preview recovered a stale guard hold "
                            + "(deferred "
                            + _previewGuardDeferCount
                            + " ticks); rendering the pending preview."
                    );
                    _previewGuardDeferCount = 0;
                }
                else
                {
                    // Keep subscription alive while the guard is held.
                    // Each tick early-returns here; once the guard releases
                    // the next tick renders the pending preview and unsubscribes
                    // via the normal healthy path below.
                    _previewGuardDeferCount++;
                    double now = EditorApplication.timeSinceStartup;
                    if (now - _previewGuardDeferLastLogTime >= 1.0)
                    {
                        _previewGuardDeferLastLogTime = now;
                        Debug.Log(
                            "[UniThumb] Live preview deferred: guard held "
                                + "(batchRunning="
                                + UniThumbBatchMenus.IsBatchRunning
                                + " deferCount="
                                + _previewGuardDeferCount
                                + " renders="
                                + _previewRenderCount
                                + " skips="
                                + _previewSkipCount
                                + ")."
                        );
                    }
                    return;
                }
            }

            if (rootVisualElement == null)
            {
                _previewDirty = false;
                EditorApplication.update -= RenderLivePreview;
                return;
            }

            // Cancel request: drop the pending preview without rendering.
            if (_previewCancelRequested)
            {
                _previewCancelRequested = false;
                _previewDirty = false;
                EditorApplication.update -= RenderLivePreview;
                return;
            }

            // Re-entrant tick while a heavy render is on the stack: stay
            // dirty and subscribed so one follow-up render fires after the
            // flight instead of stacking a second heavy render now.
            if (_previewRenderInFlight)
            {
                return;
            }

            // Pointer drag (MouseUp bursts): stay dirty and subscribed so
            // the burst coalesces to one render after release, never during
            // the drag that would read as a UI hold.
            if (IsPreviewPointerDragging())
            {
                return;
            }

            // Trailing debounce: a hierarchy/undo burst armed the preview
            // with its quiet window still ahead. Stay subscribed and dirty
            // so one render fires once the window passes.
            if (_previewDirty && EditorApplication.timeSinceStartup < _previewDeferUntil)
            {
                return;
            }

            // Prefab preview mode renders the selected prefab live with the
            // current orbit framing (prefabs always use orbit framing, never
            // the Scene View angle). The instance is a hidden clone of the
            // prefab asset, destroyed in finally: no scene dirtying and no
            // Undo bookkeeping (mirrors the temp preview camera below).
            bool isPrefabPreview = IsPrefabPreview;

            _previewDirty = false;
            EditorApplication.update -= RenderLivePreview;

            // Unsaved-scene guard runs BEFORE the in-flight flag is set:
            // setting it first and returning here leaked
            // _previewRenderInFlight=true with no reset (the reset lives in
            // the try/finally below), so every later tick early-outed blank
            // with zero logs. Dirty is consumed: the scene-switch and save
            // handlers re-arm the preview once a saved scene is active.
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (!isPrefabPreview && string.IsNullOrEmpty(scenePath))
            {
                Debug.Log("[UniThumb] Live preview skipped: active scene is not saved yet.");
                return;
            }

            _previewRenderInFlight = true;

            GameObject prefabInstance = null;
            List<Renderer> prefabIsolated = null;
            List<CanvasRenderer> prefabIsolatedUi = null;
            List<Renderer> prefabIsolatedHidden = null;
            List<CanvasRenderer> prefabIsolatedHiddenUi = null;
            List<UniThumbCapture.TerrainSnapshot> prefabIsolatedTerrains = null;
            List<UniThumbCapture.VisualEffectSnapshot> prefabIsolatedVfx = null;
            UniThumbCapture.PrefabEnvSnapshot prefabEnv = new UniThumbCapture.PrefabEnvSnapshot();
            UniThumbCapture.ScanCache previewScan = null;
            Light preDisablePreviewDir = null;
            UniThumbCapture.LightSnapshot3D preDisableSnapshot3D = null;
            List<UniThumbCapture.LightSnapshot> prefabPreviewLights = null;
            List<UniThumbCapture.Light2DSnapshot> prefabPreviewLight2Ds = null;
            List<UniThumbCapture.VolumeSnapshot> prefabPreviewVolumes = null;
            int prefabPreviewIsolationLayer = -1;
            List<UniThumbCapture.LayerSnapshot> prefabIsolatedLayers = null;

            try
            {
                GetResolution(out int previewWidth, out int previewHeight);
                previewWidth = Mathf.Max(1, previewWidth);
                previewHeight = Mathf.Max(1, previewHeight);

                CaptureSettings settings = BuildSettings();
                // Prefab mode always uses orbit framing, matching the
                // CapturePrefab forced override.
                if (isPrefabPreview)
                {
                    settings.UseSceneViewAngle = false;
                    // Scene Volumes are neutralized below; only an explicitly
                    // assigned profile runs post-processing in neutral modes,
                    // while Skybox mode keeps the user flag (mirrors
                    // CapturePrefab so preview and capture cannot diverge).
                    if (
                        settings.PostProcessingProfile == null
                        && settings.BackgroundMode != BackgroundMode.Skybox
                    )
                    {
                        settings.WantPostProcessing = false;
                    }
                }

                // Unchanged-key skip: prefab path, settings, and selection
                // all match the last heavy render, so the pixels would be
                // identical. Consume the dirty flag without instantiating,
                // scanning, or rendering anything (the MouseUp stall fix for
                // repeated same-selection events). Content edits invalidate
                // the key, so genuine changes always re-render.
                string previewKey = BuildPreviewRenderKey(
                    _previewPrefabPath,
                    _previewGuid,
                    scenePath,
                    settings
                );
                if (previewKey == _lastRenderKey)
                {
                    _previewSkipCount++;
                    _previewDirty = false;
                    EditorApplication.update -= RenderLivePreview;
                    return;
                }

                if (
                    _livePreviewRT == null
                    || !_livePreviewRT.IsCreated()
                    || _livePreviewRT.width != previewWidth
                    || _livePreviewRT.height != previewHeight
                )
                {
                    if (_livePreviewRT != null)
                    {
                        _livePreviewRT.Release();
                        UnityEngine.Object.DestroyImmediate(_livePreviewRT);
                    }
                    _livePreviewRT = UniThumbCapture.CreateCaptureTarget(
                        previewWidth,
                        previewHeight,
                        settings
                    );
                }
                GameObject tempGo = null;
                Camera cam = null;
                GameObject tempLight2D = null;
                GameObject tempLight3D = null;
                GameObject tempPostFxVolume = null;
                GameObject tempHdrpCaptureOverride = null;
                GameObject tempHdrpExposureOverride = null;
                GameObject tempHdrpSkyBaseline = null;
                UnityEngine.Object tempClonedProfile = null;
                UnityEngine.Object tempHdrpSkyProfile = null;
                UnityEngine.Object tempHdrpExposureProfile = null;
                UnityEngine.Object tempHdrpCaptureProfile = null;
                UniThumbCapture.LightSnapshot3D light3DSnapshot = null;
                System.Collections.Generic.List<UniThumbCapture.LightSnapshot3D> extraDirectionalSnapshots3D =
                    null;
                System.Collections.Generic.List<UniThumbCapture.Light2DSnapshot> light2DSnapshots =
                    null;
                System.Collections.Generic.List<UniThumbCapture.Light2DSnapshot> existingLight2DSnapshots =
                    null;
                UniThumbCapture.UiCaptureSession uiSession = null;
                // Own-mutation window: every hierarchy event fired below
                // (prefab Instantiate, temp camera and lights, isolation
                // flags, and all matching Destroys in finally) is ours and
                // must not invalidate the render key. Closed at the end of
                // the inner finally, after the last Destroy.
                bool suppressOwnHierarchy = false;
                try
                {
                    BeginPreviewOwnMutation();
                    suppressOwnHierarchy = true;
                    if (isPrefabPreview)
                    {
                        // Snapshot BEFORE any mutation (mirrors
                        // CapturePrefab): the scan and the live directional
                        // read must see the unmuted scene. A post-disable
                        // read sees disabled lights as absent, forcing a
                        // wrong fallback and a black preview. The
                        // pre-instance scan also keeps the instance's own
                        // lights/volumes out of the disable sweeps below.
                        previewScan = UniThumbCapture.BeginScan();
                        preDisablePreviewDir = UniThumbCapture.TryFindDirectionalLight(previewScan);
                        prefabEnv = UniThumbCapture.SnapshotPrefabEnvironment();
                        preDisableSnapshot3D = UniThumbCapture.SnapshotLight3D(
                            preDisablePreviewDir
                        );
                        // An open Prefab Stage wins over the selection-derived
                        // preview path (mirrors CapturePrefab resolution); the
                        // resolved path also feeds the caption below.
                        string prefabSourcePath = _previewPrefabPath;
                        string stagePrefabPath;
                        if (UniThumbCapture.TryGetPrefabStageAssetPath(out stagePrefabPath))
                        {
                            prefabSourcePath = stagePrefabPath;
                            _previewPrefabPath = prefabSourcePath;
                        }
                        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(
                            prefabSourcePath
                        );
                        if (prefabAsset == null)
                        {
                            // Dirty was already consumed above: re-arm so the
                            // miss is retried on a later tick instead of being
                            // dropped silently until the next selection change.
                            // Warn-once per path so a persistently missing
                            // prefab cannot spam the console per tick.
                            if (
                                !string.Equals(
                                    prefabSourcePath,
                                    _previewMissingPrefabWarnedPath,
                                    StringComparison.Ordinal
                                )
                            )
                            {
                                _previewMissingPrefabWarnedPath = prefabSourcePath;
                                Debug.LogWarning(
                                    "[UniThumb] Live preview skipped: prefab asset not found at '"
                                        + prefabSourcePath
                                        + "'."
                                );
                            }
                            MarkPreviewDirty();
                            _previewDeferUntil =
                                EditorApplication.timeSinceStartup + k_PreviewDebounceSeconds;
                            return;
                        }
                        prefabInstance = (GameObject)UnityEngine.Object.Instantiate(prefabAsset);
                        prefabInstance.hideFlags = HideFlags.HideAndDontSave;
                        // Particle/VFX pre-roll BEFORE bounds/cam.Render (mirrors
                        // CapturePrefab): fresh instances hold zero live
                        // particles, so bounds stay empty without this.
                        // Shared preview value (0-5s, 0 = restart-only frame).
                        float previewTime = Mathf.Clamp(settings.ParticlePreviewTime, 0f, 5f);
                        UniThumbCapture.SimulatePrefabParticles(prefabInstance, previewTime);
                        UniThumbCapture.SimulatePrefabVfx(prefabInstance, previewTime);
                        prefabIsolated = UniThumbCapture.IsolateInstanceRenderers(
                            prefabInstance,
                            previewScan
                        );
                        prefabIsolatedUi = UniThumbCapture.IsolateInstanceCanvasRenderers(
                            prefabInstance,
                            previewScan
                        );
                        prefabIsolatedTerrains = UniThumbCapture.IsolateInstanceTerrains(
                            prefabInstance,
                            previewScan
                        );
                        prefabIsolatedVfx = UniThumbCapture.IsolateInstanceVfx(
                            prefabInstance,
                            previewScan
                        );
                        prefabIsolatedHidden = UniThumbCapture.IsolateHiddenInstanceRenderers(
                            prefabInstance
                        );
                        prefabIsolatedHiddenUi =
                            UniThumbCapture.IsolateHiddenInstanceCanvasRenderers(prefabInstance);
                    }
                    tempGo = new GameObject("__UniThumbPreviewCamera");
                    tempGo.hideFlags = HideFlags.HideAndDontSave;
                    cam = tempGo.AddComponent<Camera>();

                    cam.aspect = (float)previewWidth / previewHeight;
                    cam.enabled = false;

                    // Framing runs through UniThumbCapture's shared helpers so the
                    // preview cannot diverge from capture output. The preview
                    // suppresses the empty-bounds ortho warning (continuous
                    // repaints must not spam the console).
                    if (isPrefabPreview)
                    {
                        // Prefab framing: orbit math over the instance subtree
                        // bounds only, never the open scene contents. Silent
                        // on empty bounds (continuous repaints must not spam
                        // the console). Overlay-only prefabs mirror capture's
                        // screen-space ortho fill so preview and capture sit
                        // in the same frame; other bounds-less UI prefabs
                        // share capture's GetOverlayFallbackFraming helper
                        // (the old inline radius-5 fallback diverged 1.73x
                        // from the capture 10^3 fallback); genuinely-empty
                        // prefabs keep the legacy empty fallback.
                        Bounds prefabBounds;
                        bool hasPrefabBounds = UniThumbCapture.TryGetPrefabBounds(
                            prefabInstance,
                            settings.layerMask,
                            out prefabBounds
                        );
                        if (UniThumbCapture.IsOverlayOnlyPrefab(prefabInstance, settings.layerMask))
                        {
                            UniThumbCapture.ApplyOverlayOnlyFraming(
                                cam,
                                prefabInstance,
                                previewWidth,
                                previewHeight
                            );
                        }
                        else if (hasPrefabBounds)
                        {
                            UniThumbCapture.ApplyOrbitTransformToBounds(
                                cam,
                                settings,
                                prefabBounds.center,
                                Mathf.Max(prefabBounds.extents.magnitude, 0.01f),
                                prefabBounds.extents,
                                true,
                                false
                            );
                        }
                        else
                        {
                            Bounds? overlayFallback = UniThumbCapture.GetOverlayFallbackFraming(
                                prefabInstance
                            );
                            if (overlayFallback.HasValue)
                            {
                                Bounds fallback = overlayFallback.Value;
                                UniThumbCapture.ApplyOrbitTransformToBounds(
                                    cam,
                                    settings,
                                    fallback.center,
                                    Mathf.Max(fallback.extents.magnitude, 0.01f),
                                    fallback.extents,
                                    true,
                                    false
                                );
                            }
                            else
                            {
                                UniThumbCapture.ApplyOrbitTransformToBounds(
                                    cam,
                                    settings,
                                    Vector3.zero,
                                    5f,
                                    Vector3.zero,
                                    false,
                                    false
                                );
                            }
                        }
                    }
                    else if (settings.UseSceneViewAngle)
                    {
                        if (!UniThumbCapture.TryCopyFromSceneView(cam, settings))
                        {
                            UniThumbCapture.ApplyOrbitTransform(cam, settings, false);
                        }
                    }
                    else
                    {
                        UniThumbCapture.ApplyOrbitTransform(cam, settings, false);
                    }

                    // Camera-level isolation (mirrors CapturePrefab): runs
                    // after framing so bounds still filter the original layers
                    // via settings.layerMask. The hidden clone subtree moves
                    // onto a scene-unused layer and the temp camera renders
                    // that layer only below. Only the clone subtree moves;
                    // original layers restore in finally. Fail-open (-1 keeps
                    // the flag-based path).
                    if (isPrefabPreview && prefabInstance != null)
                    {
                        prefabPreviewIsolationLayer = UniThumbCapture.FindPrefabIsolationLayer(
                            previewScan
                        );
                        prefabIsolatedLayers = UniThumbCapture.IsolateInstanceLayers(
                            prefabInstance,
                            prefabPreviewIsolationLayer
                        );
                    }

                    cam.targetTexture = _livePreviewRT;
                    cam.clearFlags =
                        settings.BackgroundMode == BackgroundMode.Skybox
                            ? CameraClearFlags.Skybox
                            : CameraClearFlags.SolidColor;
                    cam.backgroundColor = UniThumbCapture.EffectiveClearColor(settings);
                    cam.cullingMask = settings.layerMask;
                    // Prefab path with layer isolation: the hidden clone
                    // subtree sits on a scene-unused layer, so the preview
                    // camera renders that layer only and scene geometry cannot
                    // leak even where flag-based isolation misses. Scene
                    // previews and fail-open (-1) keep the settings mask.
                    if (
                        isPrefabPreview
                        && prefabInstance != null
                        && prefabPreviewIsolationLayer >= 0
                        && prefabPreviewIsolationLayer <= 31
                    )
                    {
                        cam.cullingMask = 1 << prefabPreviewIsolationLayer;
                    }

                    // Add URP camera data so the correct renderer is used
                    // (e.g. Renderer2D for 2D scenes). When UseSceneViewAngle is
                    // on, copy the Scene View camera's renderer index and PP state
                    // to match its rendering pipeline exactly. Prefab previews
                    // use orbit framing but still copy the Scene View renderer
                    // when available (mirrors CaptureCore); a generic default
                    // can point at the wrong renderer and lose sky or lights.
                    if (settings.UseSceneViewAngle || isPrefabPreview)
                    {
                        UniThumbCapture.TryEnsureUrpCameraDataFromSceneView(cam);
                    }
                    else
                    {
                        UniThumbCapture.TryEnsureUrpCameraData(cam);
                    }

                    // Light3D needs a 3D renderer (Renderer2D ignores standard
                    // Light components). Override the renderer index when Light3D
                    // is active so the directional light is actually rendered.
                    // Renderer2D-only pipelines skip every 3D feature here,
                    // mirroring CaptureCore (no switch, no temp 3D below).
                    bool previewRenderer2DOnly = UniThumbCapture.PipelineHasOnly2DRenderers();
                    if (
                        UniThumbCapture.ShouldApplyLight3D(
                            settings.Light2DMode,
                            previewRenderer2DOnly
                        )
                    )
                    {
                        UniThumbCapture.TrySwitchTo3DCameraRenderer(cam);
                    }

                    // In composite mode the overlay canvases are left untouched during
                    // the scene pass; the UI is rendered separately at SceneView aspect
                    // and composited after the readback (CompositeSceneViewUi below).
                    bool compositeEligible = UniThumbCapture.IsSceneViewUiCompositeEligible(
                        settings
                    );
                    // Live preview restores every mutation in finally blocks directly,
                    // so undo recording is unnecessary here and would only pollute the
                    // undo stack with spurious "UniThumb capture" records.
                    UniThumbCapture.RecordUndo = false;
                    if (isPrefabPreview)
                    {
                        // Prefab-only environment (mirrors CapturePrefab): the
                        // renderer/canvas isolation above hides scene geometry,
                        // this neutralizes scene RenderSettings/lights/Volumes
                        // so the preview holds the prefab subtree only.
                        // Restored in finally; RecordUndo stays false.
                        // (prefabEnv was snapshotted before Instantiate above.)
                        UniThumbCapture.NeutralizePrefabEnvironment(settings);
                        if (UniThumbCapture.PrefabLightingNeedsNeutralize(settings))
                        {
                            prefabPreviewLights = UniThumbCapture.DisableAllLights(previewScan);
                            prefabPreviewLight2Ds = UniThumbCapture.DisableAllLight2Ds(previewScan);
                            UniThumbCapture.ReenableSubtreeLights(
                                prefabInstance,
                                prefabPreviewLights
                            );
                            UniThumbCapture.ReenableSubtreeLight2Ds(
                                prefabInstance,
                                prefabPreviewLight2Ds
                            );
                        }
                        prefabPreviewVolumes = UniThumbCapture.DisableSceneVolumes(
                            prefabInstance,
                            previewScan
                        );
                    }
                    // Prefab previews (UseSceneViewAngle false above) are never
                    // composite-eligible, so this shared flag gates their UI
                    // too: on renders prefab UI via the scoped prefab session
                    // (camera rebind plus overlay retarget under the instance
                    // subtree; scene canvases stay culled), off skips the
                    // session and reproduces baseline culled pixels.
                    if (settings.CaptureUi && !compositeEligible)
                    {
                        uiSession = isPrefabPreview
                            ? BeginPrefabUiSession(prefabInstance, cam, settings.UiScale)
                            : BeginUiSession(cam, settings.UiScale);
                    }
                    // Snapshot directional state on EVERY pass (shared capture
                    // helpers) before the lighting override disables lights:
                    // Light3D reuses the primary afterwards, while None/Light2D
                    // passes mutate nothing but still run the shared restore in
                    // finally so an escaped mutation can never persist.
                    // Prefab path: the snapshot was taken before Instantiate
                    // above (live scene state); only the scene path reads here.
                    if (!isPrefabPreview)
                    {
                        preDisableSnapshot3D = UniThumbCapture.SnapshotLight3D(
                            UniThumbCapture.TryFindDirectionalLight()
                        );
                    }
                    Light skipPreview = null;
                    if (preDisableSnapshot3D != null)
                    {
                        skipPreview = preDisableSnapshot3D.Light;
                    }
                    extraDirectionalSnapshots3D = isPrefabPreview
                        ? UniThumbCapture.SnapshotOtherDirectionalRotations(
                            skipPreview,
                            previewScan
                        )
                        : UniThumbCapture.SnapshotOtherDirectionalRotations(skipPreview);

                    if (settings.UseLightingOverride)
                    {
                        _previewLightSnapshots.Clear();
                        _previewLightSnapshots.AddRange(UniThumbCapture.DisableAllLights());
                        // The override also kills 2D lights (Light2D is not a
                        // Light component); the temp Light2D below is created
                        // after this, so it still lights the scene.
                        light2DSnapshots = UniThumbCapture.DisableAllLight2Ds();
                    }
                    if (settings.Light2DMode == LightingMode.Light2D)
                    {
                        existingLight2DSnapshots = UniThumbCapture.DisableExistingGlobalLight2Ds();
                        tempLight2D = UniThumbCapture.CreateTempLight2D(
                            settings.Light2DIntensity,
                            settings.Light2DSortingLayerIds
                        );
                    }
                    if (settings.Light2DMode == LightingMode.Light3D && previewRenderer2DOnly)
                    {
                        // Renderer2D-only downgrade (mirrors CaptureCore): no
                        // directional reuse, no temp directional, no logs.
                        existingLight2DSnapshots = UniThumbCapture.DisableExistingGlobalLight2Ds();
                        tempLight2D = UniThumbCapture.CreateTempLight2D(
                            settings.Light2DIntensity,
                            settings.Light2DSortingLayerIds
                        );
                    }
                    if (
                        UniThumbCapture.ShouldApplyLight3D(
                            settings.Light2DMode,
                            previewRenderer2DOnly
                        )
                    )
                    {
                        // Use the pre-disable snapshot when available (override
                        // already disabled it); otherwise search for an active
                        // directional. ResolveLight3DIntensity maps the UI value
                        // (0-5) to HDRP lux scale when the project uses HDRP.
                        Light existing = null;
                        if (preDisableSnapshot3D != null)
                        {
                            existing = preDisableSnapshot3D.Light;
                            light3DSnapshot = preDisableSnapshot3D;
                        }
                        else
                        {
                            existing = UniThumbCapture.TryFindDirectionalLight();
                            if (existing != null)
                            {
                                light3DSnapshot = UniThumbCapture.SnapshotLight3D(existing);
                            }
                        }
                        // Prefab previews mirror CaptureCore: the explicit
                        // Light3D key routes through the shared prefab
                        // intensity (clamp-then-resolve); scene previews keep
                        // the scene value untouched.
                        if (existing != null)
                        {
                            existing.enabled = true;
                            existing.intensity = isPrefabPreview
                                ? UniThumbCapture.ResolveLight3DIntensity(
                                    UniThumbCapture.ClampLight3DIntensity(
                                        settings.Light3DPrefabIntensity
                                    )
                                )
                                : UniThumbCapture.ResolveLight3DIntensity(
                                    settings.Light3DIntensity
                                );
                            existing.color = settings.Light3DColor;
                            existing.shadows = settings.Light3DShadows
                                ? LightShadows.Soft
                                : LightShadows.None;
                            existing.transform.rotation = Quaternion.Euler(
                                settings.Light3DPitch,
                                settings.Light3DYaw,
                                0f
                            );
                        }
                        else
                        {
                            tempLight3D = UniThumbCapture.CreateTempDirectionalLight(
                                isPrefabPreview
                                    ? UniThumbCapture.ResolveLight3DIntensity(
                                        UniThumbCapture.ClampLight3DIntensity(
                                            settings.Light3DPrefabIntensity
                                        )
                                    )
                                    : UniThumbCapture.ResolveLight3DIntensity(
                                        settings.Light3DIntensity
                                    ),
                                settings.Light3DShadows,
                                settings.Light3DColor,
                                settings.Light3DYaw,
                                settings.Light3DPitch
                            );
                        }
                    }
                    // Prefab-only fallback (mirrors CaptureCore): mode None
                    // neutralized every scene light above, so the preview
                    // needs one prefab-scoped key (directional for
                    // perspective/3D content, Light2D for sprite-only ortho).
                    // Decided here where the live pre-disable state and the
                    // instance are both visible. Temp objects are destroyed in
                    // the finally below; RecordUndo stays false throughout.
                    bool previewHas3DContent = true;
                    bool previewHasOwnKey = false;
                    if (isPrefabPreview && prefabInstance != null)
                    {
                        previewHas3DContent = UniThumbCapture.PrefabSubtreeHas3DContent(
                            prefabInstance
                        );
                        previewHasOwnKey = UniThumbCapture.PrefabSubtreeHasOwnKeyLight(
                            prefabInstance,
                            !settings.orthographic || previewHas3DContent
                        );
                    }
                    PrefabFallbackLight prefabFallback = UniThumbCapture.ResolvePrefabFallbackLight(
                        settings.Light2DMode,
                        isPrefabPreview,
                        settings.orthographic,
                        settings.UseLightingOverride,
                        preDisableSnapshot3D != null,
                        previewHas3DContent,
                        previewHasOwnKey,
                        previewRenderer2DOnly
                    );
                    // Prefab with its own key light (fallback None) on a
                    // perspective preview: point the camera at a 3D URP
                    // renderer first (mirrors CaptureCore), or the own
                    // directional key is ignored and the preview is ambient
                    // only. TempLight3D is handled by
                    // EnsurePrefabFallbackLights below. Renderer2D-only
                    // never switches.
                    if (
                        isPrefabPreview
                        && !settings.orthographic
                        && settings.Light2DMode == LightingMode.None
                        && prefabFallback == PrefabFallbackLight.None
                        && !previewRenderer2DOnly
                    )
                    {
                        UniThumbCapture.TrySwitchTo3DCameraRenderer(cam);
                    }
                    if (prefabFallback != PrefabFallbackLight.None)
                    {
                        GameObject fallbackLight2D;
                        GameObject fallbackLight3D;
                        System.Collections.Generic.List<UniThumbCapture.Light2DSnapshot> fallbackGlobals;
                        UniThumbCapture.EnsurePrefabFallbackLights(
                            prefabFallback,
                            cam,
                            settings,
                            previewScan,
                            out fallbackLight2D,
                            out fallbackLight3D,
                            out fallbackGlobals
                        );
                        tempLight2D = fallbackLight2D;
                        tempLight3D = fallbackLight3D;
                        if (fallbackGlobals != null)
                        {
                            existingLight2DSnapshots = fallbackGlobals;
                        }
                    }
                    if (UniThumbCapture.IsHdrpPipeline())
                    {
                        UniThumbCapture.TryEnablePostProcessing(cam);
                        tempHdrpExposureOverride =
                            UniThumbCapture.TryCreateHdrpExposureOverrideVolume(
                                out tempHdrpExposureProfile
                            );
                        // PP off and no scene volumes: inject the sky baseline
                        // so the preview still gets a sky instead of black.
                        if (!UniThumbCapture.ShouldApplyPostProcessing(settings))
                        {
                            tempHdrpSkyBaseline = UniThumbCapture.TryCreateHdrpSkyBaselineVolume(
                                out tempHdrpSkyProfile
                            );
                        }
                        if (UniThumbCapture.ShouldApplyPostProcessing(settings))
                        {
                            tempPostFxVolume = UniThumbCapture.TryCreateTempPostProcessingVolume(
                                settings,
                                out tempClonedProfile
                            );
                            tempHdrpCaptureOverride =
                                UniThumbCapture.TryCreateHdrpCaptureOverrideVolume(
                                    out tempHdrpCaptureProfile
                                );
                        }
                    }
                    else if (UniThumbCapture.ShouldApplyPostProcessing(settings))
                    {
                        UniThumbCapture.TryEnablePostProcessing(cam);
                        tempPostFxVolume = UniThumbCapture.TryCreateTempPostProcessingVolume(
                            settings,
                            out tempClonedProfile
                        );
                    }
                    cam.Render();

                    if (_livePreviewTexture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_livePreviewTexture);
                    }
                    _livePreviewTexture = UniThumbCapture.ReadBack(_livePreviewRT);

                    // Composite the UI rendered at SceneView aspect when eligible; the
                    // helper re-checks eligibility and returns the input unchanged on
                    // any failure (canvas restore included), so only replace on differ.
                    if (compositeEligible)
                    {
                        Texture2D composite = UniThumbCapture.CompositeSceneViewUi(
                            settings,
                            _livePreviewTexture
                        );
                        if (composite != _livePreviewTexture)
                        {
                            UnityEngine.Object.DestroyImmediate(_livePreviewTexture);
                            _livePreviewTexture = composite;
                        }
                    }

                    // Heavy render completed: remember its key so identical
                    // follow-ups skip, and count it for EditMode assertions.
                    _lastRenderKey = previewKey;
                    _previewRenderCount++;
                    _previewMissingPrefabWarnedPath = null;

                    if (_previewImage != null)
                    {
                        _previewImage.image = _livePreviewTexture;
                        _previewBox?.EnableInClassList("stt-hidden", false);
                        _noThumbnailLabel?.EnableInClassList("stt-hidden", true);
                        _previewCaptionRow?.EnableInClassList("stt-hidden", false);
                        if (_previewCaption != null)
                        {
                            string captionName = isPrefabPreview
                                ? Path.GetFileNameWithoutExtension(_previewPrefabPath)
                                : Path.GetFileNameWithoutExtension(scenePath);
                            _previewCaption.text = captionName + " (preview)";
                        }
                        Repaint();
                    }
                }
                finally
                {
                    if (uiSession != null)
                    {
                        uiSession.Dispose();
                    }
                    UniThumbCapture.RestoreLights(_previewLightSnapshots);
                    _previewLightSnapshots.Clear();
                    if (tempLight2D != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempLight2D);
                    }
                    // Restore extra rotations (plus bake/affect) before the
                    // temp light is destroyed; the primary snapshot restore
                    // below heals the Light3D light itself.
                    UniThumbCapture.RestoreDirectionalRotations(extraDirectionalSnapshots3D);
                    if (tempLight3D != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempLight3D);
                    }
                    if (tempPostFxVolume != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempPostFxVolume);
                    }
                    if (tempHdrpSkyProfile != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpSkyProfile);
                    }
                    if (tempHdrpSkyBaseline != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpSkyBaseline);
                    }
                    if (tempHdrpExposureOverride != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpExposureOverride);
                    }
                    if (tempHdrpExposureProfile != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpExposureProfile);
                    }
                    if (tempHdrpCaptureOverride != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpCaptureOverride);
                    }
                    if (tempHdrpCaptureProfile != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempHdrpCaptureProfile);
                    }
                    if (tempClonedProfile != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempClonedProfile);
                    }
                    UniThumbCapture.RestoreLight3D(light3DSnapshot);
                    // Mode-path snapshots first (see CaptureCore): the override
                    // snapshots restore the original state last.
                    UniThumbCapture.RestoreGlobalLight2Ds(existingLight2DSnapshots);
                    UniThumbCapture.RestoreGlobalLight2Ds(light2DSnapshots);
                    // Prefab preview cleanup: restore the isolation flags
                    // first (they touch live scene objects), then destroy
                    // the hidden instance. Environment/lights/Volumes come
                    // after the mode-path restores above (they snapshot the
                    // original state). Both restore helpers are
                    // null-safe, so scene mode passes nulls here.
                    UniThumbCapture.RestoreIsolatedRenderers(prefabIsolated);
                    UniThumbCapture.RestoreIsolatedCanvasRenderers(prefabIsolatedUi);
                    UniThumbCapture.RestoreIsolatedRenderers(prefabIsolatedHidden);
                    UniThumbCapture.RestoreIsolatedCanvasRenderers(prefabIsolatedHiddenUi);
                    UniThumbCapture.RestoreIsolatedTerrains(prefabIsolatedTerrains);
                    UniThumbCapture.RestoreIsolatedVfx(prefabIsolatedVfx);
                    UniThumbCapture.RestoreIsolatedLayers(prefabIsolatedLayers);
                    UniThumbCapture.RestoreSceneVolumes(prefabPreviewVolumes);
                    UniThumbCapture.RestoreLights(prefabPreviewLights);
                    UniThumbCapture.RestoreGlobalLight2Ds(prefabPreviewLight2Ds);
                    UniThumbCapture.RestorePrefabEnvironment(prefabEnv);
                    if (prefabInstance != null)
                    {
                        UnityEngine.Object.DestroyImmediate(prefabInstance);
                    }
                    // Mirror CaptureCore: the temp preview camera is declared
                    // null outside the try, created inside it, and destroyed
                    // here with a null check, so a throw anywhere above
                    // (including UiCaptureSession.Begin) cannot leak it.
                    if (cam != null)
                    {
                        cam.targetTexture = null;
                    }
                    if (tempGo != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tempGo);
                    }
                    // Last mutation done: close the own-mutation window so
                    // later real edits invalidate the key again. Restore
                    // order above is untouched (renderers, canvas renderers,
                    // volumes, lights, env, camera).
                    if (suppressOwnHierarchy)
                    {
                        EndPreviewOwnMutation();
                        suppressOwnHierarchy = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UniThumb] Live preview failed: " + ex.Message);
            }
            finally
            {
                UniThumbCapture.RecordUndo = true;
                _previewRenderInFlight = false;
            }
        }

        private void OnResolutionChanged(ChangeEvent<string> evt)
        {
            _resolutionIndex = _resolutionPopup.index;
            MarkPreviewDirty();
        }

        private void ApplyOrbitPreset(float yaw, float pitch)
        {
            _orbitYaw = yaw;
            _orbitPitch = pitch;
            if (_orbitYawSlider != null)
            {
                _orbitYawSlider.SetValueWithoutNotify(yaw);
            }
            if (_orbitPitchSlider != null)
            {
                _orbitPitchSlider.SetValueWithoutNotify(pitch);
            }
        }

        /// <summary>
        /// UI refresh-path texture source: cache-only (TryGetCachedTexture),
        /// never Load/HasThumbnail here. Drops references to destroyed
        /// cache-owned textures (eviction DestroyImmediate) and re-fetches by
        /// GUID - the cache re-warms within a few frames after eviction
        /// (retried via SchedulePreviewRefetch).
        /// </summary>
        private bool RefreshPreviewTexture()
        {
            if (string.IsNullOrEmpty(_previewGuid))
            {
                string prefabPath;
                if (TryGetSelectedPrefabPath(out prefabPath))
                {
                    _previewPrefabPath = prefabPath;
                    _previewGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                }
                else
                {
                    string scenePath = EditorSceneManager.GetActiveScene().path;
                    if (!string.IsNullOrEmpty(scenePath))
                    {
                        _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                    }
                }
            }
            if (_previewTexture == null && !string.IsNullOrEmpty(_previewGuid))
            {
                UniThumbStorage.TryGetCachedTexture(_previewGuid, out _previewTexture);
            }
            return _previewTexture != null;
        }

        private void UpdatePreviewUI()
        {
            bool hasPreview = _previewTexture != null;
            if (_previewBox != null)
            {
                _previewBox.EnableInClassList("stt-hidden", !hasPreview);
            }
            if (_noThumbnailLabel != null)
            {
                _noThumbnailLabel.EnableInClassList("stt-hidden", hasPreview);
            }
            if (_previewImage != null)
            {
                _previewImage.image = hasPreview ? _previewTexture : null;
            }
            if (_previewCaptionRow != null)
            {
                _previewCaptionRow.EnableInClassList("stt-hidden", !hasPreview);
            }
            if (hasPreview && _previewCaption != null)
            {
                string captionName;
                string captionSuffix = string.Empty;
                if (!string.IsNullOrEmpty(_previewPrefabPath))
                {
                    captionName = Path.GetFileNameWithoutExtension(_previewPrefabPath);
                    captionSuffix = " (prefab)";
                }
                else
                {
                    string scenePath = EditorSceneManager.GetActiveScene().path;
                    captionName = Path.GetFileNameWithoutExtension(scenePath);
                }
                _previewCaption.text =
                    captionName
                    + " "
                    + _previewTexture.width
                    + "x"
                    + _previewTexture.height
                    + captionSuffix;
            }
            UpdateSourceModeUI();
        }

        private void SetStatus(string message, MessageType type)
        {
            _statusMessage = message ?? string.Empty;
            _statusType = type;
            if (_statusHelp == null)
            {
                return;
            }
            _statusHelp.text = _statusMessage;
            _statusHelp.messageType = ToHelpBoxMessageType(type);
            _statusHelp.EnableInClassList("stt-hidden", string.IsNullOrEmpty(_statusMessage));
        }

        private static HelpBoxMessageType ToHelpBoxMessageType(MessageType type)
        {
            switch (type)
            {
                case MessageType.Info:
                    return HelpBoxMessageType.Info;
                case MessageType.Warning:
                    return HelpBoxMessageType.Warning;
                case MessageType.Error:
                    return HelpBoxMessageType.Error;
                default:
                    return HelpBoxMessageType.None;
            }
        }

        private void UpdateBatchUI()
        {
            bool running = UniThumbBatchMenus.IsBatchRunning;
            bool busy = UniThumbGuard.IsGenerating;
            if (_generateFolderButton != null)
            {
                _generateFolderButton.SetEnabled(!busy && _folderValid);
            }
            if (_clearFolderButton != null)
            {
                _clearFolderButton.SetEnabled(!busy && _folderValid);
            }
            if (_batchProgress != null)
            {
                _batchProgress.EnableInClassList("stt-hidden", !running);
            }
            if (running)
            {
                UpdateBatchProgress(UniThumbBatchMenus.GetBatchSnapshot());
            }
            if (_batchResultHelp != null)
            {
                bool showResult = !running && !string.IsNullOrEmpty(_batchResultMessage);
                _batchResultHelp.EnableInClassList("stt-hidden", !showResult);
                if (showResult)
                {
                    _batchResultHelp.text = _batchResultMessage;
                    _batchResultHelp.messageType = ToHelpBoxMessageType(_batchResultType);
                }
            }
            if (_batchInvalidLabel != null)
            {
                _batchInvalidLabel.EnableInClassList("stt-hidden", !_batchFolderInputInvalid);
            }
        }

        private void UpdateBatchProgress(UniThumbBatchMenus.BatchSnapshot snapshot)
        {
            float value = snapshot.Total > 0 ? snapshot.Processed / (float)snapshot.Total : 0f;

            // Calculate ETA
            float elapsed = (float)EditorApplication.timeSinceStartup - _batchStartTime;
            string etaText = "";
            if (snapshot.Processed > 0 && snapshot.Processed < snapshot.Total && elapsed > 0.5f)
            {
                float rate = snapshot.Processed / elapsed;
                float remaining = (snapshot.Total - snapshot.Processed) / rate;
                if (remaining >= 60f)
                    etaText = " | ~" + Mathf.CeilToInt(remaining / 60f) + "m remaining";
                else
                    etaText = " | ~" + Mathf.CeilToInt(remaining) + "s remaining";
            }

            string text =
                "Asset "
                + snapshot.Processed
                + "/"
                + snapshot.Total
                + ": "
                + snapshot.CurrentScene
                + etaText;
            if (_batchProgressBar != null)
            {
                _batchProgressBar.value = value;
                _batchProgressBar.title = "";
            }
            if (_batchProgressCaption != null)
            {
                _batchProgressCaption.text = text;
            }
        }

        private void CancelBatch()
        {
            UniThumbBatchMenus.RequestBatchCancel();
            _cancelRequested = true;
        }

        private void RevalidateBatchFolder()
        {
            if (string.IsNullOrEmpty(_batchFolderPath))
            {
                _folderValid = false;
                return;
            }
            bool valid =
                _batchFolderPath.StartsWith("Assets/", StringComparison.Ordinal)
                && AssetDatabase.IsValidFolder(_batchFolderPath);
            _folderValid = valid;
            if (!valid)
            {
                _batchFolderPath = null;
            }
        }

        private void OpenFolderMenu()
        {
            Rect screenRect = GUIUtility.GUIToScreenRect(_browseButton.worldBound);
            List<string> folders = CollectProjectFolders();
            UniThumbFolderMenu.ShowMenu(screenRect, folders, OnFolderPicked);
        }

        private void OnFolderPicked(string path)
        {
            SetBatchFolderInput(path);
            SetStatus("Batch folder: " + path, MessageType.Info);
        }

        private static List<string> CollectProjectFolders()
        {
            List<string> folders = new List<string>();
            CollectProjectFoldersRecursive("Assets", folders);
            return folders;
        }

        private static void CollectProjectFoldersRecursive(string path, List<string> folders)
        {
            if (folders.Count >= k_MaxFolderMenuEntries)
            {
                return;
            }
            folders.Add(path);
            string[] children = AssetDatabase.GetSubFolders(path);
            for (int i = 0; i < children.Length; i++)
            {
                CollectProjectFoldersRecursive(children[i], folders);
            }
        }

        private void UseActiveSceneFolderForBatch()
        {
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                SetStatus("Scene not saved yet", MessageType.Warning);
                return;
            }
            string folder = Path.GetDirectoryName(scenePath);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }
            SetBatchFolderInput(folder.Replace('\\', '/'));
            SetStatus("Batch folder: " + folder.Replace('\\', '/'), MessageType.Info);
        }

        private void SetBatchFolderInput(string path)
        {
            _batchFolderInput = path ?? string.Empty;
            ValidateBatchFolderInput();
            RefreshGenerateFolderEnabled();
            if (_batchFolderField != null)
            {
                _batchFolderField.SetValueWithoutNotify(_batchFolderInput);
            }
        }

        private void ValidateBatchFolderInput()
        {
            if (string.IsNullOrEmpty(_batchFolderInput))
            {
                _batchFolderInputInvalid = false;
                return;
            }
            if (IsValidProjectFolder(_batchFolderInput))
            {
                _batchFolderPath = _batchFolderInput;
                _folderValid = true;
                _batchFolderInputInvalid = false;
            }
            else
            {
                _batchFolderInputInvalid = true;
                _batchFolderPath = null;
                _folderValid = false;
                RefreshGenerateFolderEnabled();
            }
        }

        private void RefreshGenerateFolderEnabled()
        {
            bool busy = UniThumbGuard.IsGenerating;
            if (_generateFolderButton != null)
            {
                _generateFolderButton.SetEnabled(!busy && _folderValid);
            }
            if (_clearFolderButton != null)
            {
                _clearFolderButton.SetEnabled(!busy && _folderValid);
            }
            if (_batchInvalidLabel != null)
            {
                _batchInvalidLabel.EnableInClassList("stt-hidden", !_batchFolderInputInvalid);
            }
        }

        private static bool IsValidProjectFolder(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.StartsWith("Assets/", StringComparison.Ordinal)
                && AssetDatabase.IsValidFolder(path);
        }

        private void RegisterBatchFolderDragDrop(VisualElement row)
        {
            row.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                bool valid = GetDroppedFolderPath() != null;
                DragAndDrop.visualMode = valid
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.None;
                row.EnableInClassList("stt-drag-over", valid);
                evt.StopPropagation();
            });
            row.RegisterCallback<DragPerformEvent>(evt =>
            {
                string folderPath = GetDroppedFolderPath();
                bool valid = folderPath != null;
                row.EnableInClassList("stt-drag-over", false);
                if (valid)
                {
                    SetBatchFolderInput(folderPath);
                }
                DragAndDrop.visualMode = valid
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.None;
                evt.StopPropagation();
            });
            row.RegisterCallback<DragExitedEvent>(evt =>
            {
                row.EnableInClassList("stt-drag-over", false);
                evt.StopPropagation();
            });
        }

        private static string GetDroppedFolderPath()
        {
            if (DragAndDrop.paths == null || DragAndDrop.paths.Length != 1)
            {
                return null;
            }
            string path = DragAndDrop.paths[0];
            if (
                string.IsNullOrEmpty(path)
                || !path.StartsWith("Assets/", StringComparison.Ordinal)
                || !AssetDatabase.IsValidFolder(path)
            )
            {
                return null;
            }
            return path;
        }

        private void StartFolderBatch()
        {
            if (!_folderValid || string.IsNullOrEmpty(_batchFolderPath))
            {
                SetStatus(
                    "Select a valid project folder inside Assets/ first.",
                    MessageType.Warning
                );
                return;
            }
            // Snapshot the current UI settings before the pump starts; the pump
            // reads the store once in StartBatchPump so mid-batch UI edits cannot
            // drift per-scene captures.
            UniThumbCapture.RememberSettings(BuildSettings());
            string error;
            if (!UniThumbBatchMenus.TryStartFolderBatch(_batchFolderPath, _batchScope, out error))
            {
                SetStatus("Could not start folder batch: " + error, MessageType.Warning);
                return;
            }
            _cancelRequested = false;
            _batchStartTime = (float)EditorApplication.timeSinceStartup;
            _batchResultMessage = null;
            _batchResultType = MessageType.None;
            UpdateBatchUI();
        }

        /// <summary>
        /// Drives the batch UI. Subscribed in OnEnable/OnDisable so a batch
        /// started from the menu (or while the window is closed) is picked up
        /// on the first tick. The _wasBatchRunning flag keeps the old state
        /// machine: first running tick flips it on (pickup), first idle tick
        /// after a running batch flips it off and finalizes the result.
        /// </summary>
        private void OnBatchUpdateTick()
        {
            UniThumbBatchMenus.BatchSnapshot snapshot = UniThumbBatchMenus.GetBatchSnapshot();
            if (snapshot.IsRunning)
            {
                if (!_wasBatchRunning)
                {
                    _wasBatchRunning = true;
                }
                _lastRunningBatchSnapshot = snapshot;
                UpdateBatchProgress(snapshot);
                return;
            }
            if (_wasBatchRunning)
            {
                _wasBatchRunning = false;
                OnBatchEnded(_lastRunningBatchSnapshot);
            }
        }

        private void OnBatchEnded(UniThumbBatchMenus.BatchSnapshot snapshot)
        {
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(scenePath))
            {
                _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                _previewTexture = UniThumbStorage.Load(scenePath);
            }
            if (snapshot.Processed >= snapshot.Total)
            {
                _batchResultMessage =
                    "Completed "
                    + snapshot.Total
                    + " assets (scenes/prefabs): "
                    + snapshot.Succeeded
                    + " generated"
                    + (snapshot.Failed > 0 ? ", " + snapshot.Failed + " failed" : "")
                    + (snapshot.Skipped > 0 ? ", " + snapshot.Skipped + " skipped" : "")
                    + ".";
                _batchResultType = snapshot.Failed == 0 ? MessageType.Info : MessageType.Warning;
            }
            else if (_cancelRequested)
            {
                _batchResultMessage =
                    "Batch cancelled after "
                    + snapshot.Processed
                    + " of "
                    + snapshot.Total
                    + " assets (scenes/prefabs).";
                _batchResultType = MessageType.Warning;
            }
            else
            {
                _batchResultMessage = "Batch aborted: unexpected error (see Console).";
                _batchResultType = MessageType.Warning;
            }
            _cancelRequested = false;
            UpdatePreviewUI();
            UpdateBatchUI();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                if (UniThumbBatchMenus.IsBatchRunning)
                {
                    evt.StopPropagation();
                    CancelBatch();
                }
                return;
            }
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }
            if ((evt.modifiers & (EventModifiers.Control | EventModifiers.Command)) == 0)
            {
                return;
            }
            if (IsTextInputFocused(evt))
            {
                return;
            }
            evt.StopPropagation();
            if (UniThumbGuard.IsGenerating)
            {
                return;
            }
            if (evt.shiftKey)
            {
                StartFolderBatch();
                return;
            }
            GenerateThumbnail();
        }

        private static bool IsTextInputFocused(KeyDownEvent evt)
        {
            VisualElement focused =
                (evt.target as VisualElement)?.focusController?.focusedElement as VisualElement;
            if (focused == null)
            {
                return false;
            }
            if (focused is TextField || focused is IntegerField)
            {
                return true;
            }
            if (
                focused.GetFirstAncestorOfType<TextField>() != null
                || focused.GetFirstAncestorOfType<IntegerField>() != null
            )
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Renders the live preview while the user navigates the Scene View.
        /// Delta checks against the cached transform values throttle this to
        /// meaningful moves only (repaints fire every frame during navigation).
        /// </summary>
        private void OnSceneViewGui(SceneView sv)
        {
            if (!_useSceneViewAngle || sv == null || sv.camera == null)
            {
                return;
            }
            Vector3 position = sv.camera.transform.position;
            Quaternion rotation = sv.camera.transform.rotation;
            bool orthographic = sv.orthographic;
            float sizeOrFov = orthographic ? sv.camera.orthographicSize : sv.camera.fieldOfView;
            float lastSizeOrFov = orthographic ? _lastSceneViewSize : _lastSceneViewFov;

            bool moved =
                (position - _lastSceneViewPos).sqrMagnitude > 1e-6f
                || Quaternion.Angle(rotation, _lastSceneViewRot) * Mathf.Deg2Rad > 0.001f
                || Mathf.Abs(sizeOrFov - lastSizeOrFov) > 0.001f;
            if (!moved)
            {
                return;
            }

            _lastSceneViewPos = position;
            _lastSceneViewRot = rotation;
            if (orthographic)
            {
                _lastSceneViewSize = sizeOrFov;
            }
            else
            {
                _lastSceneViewFov = sizeOrFov;
            }
            MarkPreviewDirty();
        }

        /// <summary>
        /// Prefab selection probe for the preview and the Generate/Delete
        /// buttons. True when the active Project window selection is a prefab
        /// asset (GameObject whose asset path ends in .prefab); scene objects
        /// have no asset path and never match. Static for EditMode tests.
        /// </summary>
        internal static bool TryGetSelectedPrefabPath(out string prefabPath)
        {
            return UniThumbBatchMenus.TryGetSelectedPrefabPath(out prefabPath);
        }

        /// <summary>
        /// Project selection reaction. A prefab selection switches the preview
        /// box to the prefab's cached thumbnail (cache-only); any other
        /// selection returns to the active-scene preview. The live preview is
        /// re-armed in both modes: prefab mode renders the selected prefab
        /// live with the current orbit framing. Internal for EditMode tests.
        /// </summary>
        internal void OnProjectSelectionChanged()
        {
            // Equality guard: repeated MouseUp selection events with an
            // unchanged key still flag the preview dirty (cheap), but the
            // render skips on the unchanged render key, so zero heavy work
            // runs. A changed key idles 0.6s after the last input: the heavy
            // Instantiate plus Render plus ReadBack never runs inside the
            // MouseUp event, so the click itself never holds.
            string prefabPath;
            bool hasPrefab = TryGetSelectedPrefabPath(out prefabPath);
            string key = hasPrefab ? prefabPath : EditorSceneManager.GetActiveScene().path;
            if (!string.Equals(key, _lastSelectionKey, StringComparison.Ordinal))
            {
                _lastSelectionKey = key;
                _previewDeferUntil = EditorApplication.timeSinceStartup + k_PreviewIdleDeferSeconds;
            }
            RefreshPreviewForSelection();
            MarkPreviewDirty();
        }

        /// <summary>
        /// Hierarchy reaction: scene/prefab content may have changed (the
        /// settings fingerprint cannot see it), so the render key is dropped
        /// and the render deferred to the trailing debounce window, collapsing
        /// drag bursts to one render. Internal for EditMode tests.
        /// </summary>
        internal void OnHierarchyChangedForPreview()
        {
            // Own-mutation filter: hierarchy events fired by the preview's
            // own Instantiate and Destroy calls arrive here synchronously.
            // They carry no content change, so the render key stays valid
            // and no re-render is scheduled (zero net new renders). Real
            // edits take the invalidate-plus-defer path below.
            if (IsPreviewOwnMutation)
            {
                _previewOwnEventFilteredCount++;
                return;
            }
            InvalidatePreviewRenderKey();
            _previewDeferUntil = EditorApplication.timeSinceStartup + k_PreviewDebounceSeconds;
            MarkPreviewDirty();
        }

        /// <summary>
        /// Undo-redo reaction: same invalidate-plus-defer as hierarchy edits.
        /// Internal for EditMode tests.
        /// </summary>
        internal void OnUndoRedoForPreview()
        {
            InvalidatePreviewRenderKey();
            _previewDeferUntil = EditorApplication.timeSinceStartup + k_PreviewDebounceSeconds;
            MarkPreviewDirty();
        }

        /// <summary>
        /// Prefab Stage open/close reaction. Entering the stage retargets the
        /// preview to the staged prefab (stage wins over selection in
        /// RefreshPreviewForSelection); closing returns to selection/scene.
        /// Same refresh path as selection changes; TryGetCachedTexture only.
        /// </summary>
        private void OnPrefabStageChanged(PrefabStage stage)
        {
            RefreshPreviewForSelection();
            InvalidatePreviewRenderKey();
            MarkPreviewDirty();
        }

        /// <summary>
        /// Resolves the preview target from the current selection: prefab
        /// selection wins (GUID from the prefab asset path), otherwise the
        /// active scene. Texture source is the cache only
        /// (TryGetCachedTexture, never Load/HasThumbnail here). Refreshes the
        /// scene label and preview UI to match.
        /// </summary>
        private void RefreshPreviewForSelection()
        {
            _previewTexture = null;
            _previewGuid = null;
            _previewPrefabPath = null;
            string prefabPath;
            if (TryGetSelectedPrefabPath(out prefabPath))
            {
                _previewPrefabPath = prefabPath;
                _previewGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                UniThumbStorage.TryGetCachedTexture(_previewGuid, out _previewTexture);
            }
            else
            {
                string scenePath = EditorSceneManager.GetActiveScene().path;
                if (!string.IsNullOrEmpty(scenePath))
                {
                    _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                    UniThumbStorage.TryGetCachedTexture(_previewGuid, out _previewTexture);
                }
            }
            UpdateActiveSceneLabel();
            UpdatePreviewUI();
            // Selection changes the framing gate (prefab mode forces orbit),
            // so refresh toggle/slider state to match.
            UpdateFramingState();
            UpdateParticleSliderVisibility();
        }

        /// <summary>
        /// Scene-switch reaction. Refreshes the preview cache refs and
        /// updates scene UI.
        /// </summary>
        internal void OnActiveSceneChangedInEditMode(Scene previousScene, Scene newScene)
        {
            RefreshPreviewForSelection();
            InvalidatePreviewRenderKey();
            MarkPreviewDirty();
        }

        private void OnTextureEvicted(string guid)
        {
            if (
                string.IsNullOrEmpty(guid)
                || string.IsNullOrEmpty(_previewGuid)
                || !string.Equals(guid, _previewGuid, StringComparison.Ordinal)
            )
            {
                return;
            }
            _previewTexture = null;
            InvalidatePreviewRenderKey();
            MarkPreviewDirty();
            SchedulePreviewRefetch(k_PreviewRefetchAttempts);
        }

        /// <summary>
        /// Scene-save reaction. Triggers auto-regeneration when enabled
        /// (independent of any staleness tracking). Fires only while the
        /// UniThumb window is open.
        /// </summary>
        internal void OnSceneSaved(Scene scene)
        {
            if (string.IsNullOrEmpty(_previewGuid))
            {
                return;
            }
            string scenePath = scene.path;
            if (string.IsNullOrEmpty(scenePath))
            {
                return;
            }

            // Prefab Stage open: the saved scene is the stage preview scene, not
            // the active content scene. Regenerating the scene thumbnail now would
            // bake stage/prefab pixels into the scene slot, so skip silently.
            string openStagePath;
            if (UniThumbCapture.TryGetPrefabStageAssetPath(out openStagePath))
            {
                return;
            }

            // Auto-regen: deferred generation on save when auto-regeneration
            // is enabled. Fires only while the UniThumb window is open.
            UniThumbSettings settings = UniThumbSettings.Get();
            if (
                settings.AutoRegenerateOnSave
                && !UniThumbGuard.IsGenerating
                && string.Equals(scenePath, EditorSceneManager.GetActiveScene().path)
            )
            {
                EditorApplication.delayCall += () =>
                {
                    if (this == null || rootVisualElement == null)
                    {
                        return;
                    }
                    if (!UniThumbGuard.IsGenerating)
                    {
                        RegeneratePreviewCore();
                    }
                };
            }
        }

        private void SchedulePreviewRefetch(int attemptsLeft)
        {
            if (attemptsLeft <= 0 || rootVisualElement == null)
            {
                return;
            }
            rootVisualElement
                .schedule.Execute(() =>
                {
                    bool refetched = RefreshPreviewTexture();
                    UpdateActiveSceneLabel();
                    UpdatePreviewUI();
                    if (!refetched)
                    {
                        SchedulePreviewRefetch(attemptsLeft - 1);
                    }
                })
                .ExecuteLater(k_PreviewRefetchDelayMs);
        }

        private void GenerateThumbnail()
        {
            if (UniThumbGuard.IsGenerating)
            {
                SetStatus("A thumbnail generation is already in progress.", MessageType.Warning);
                return;
            }
            string prefabPath;
            if (TryGetSelectedPrefabPath(out prefabPath))
            {
                GeneratePrefabThumbnail(prefabPath);
                return;
            }
            RegeneratePreviewCore();
        }

        /// <summary>
        /// Prefab capture-and-save flow for the Generate button while a prefab
        /// asset is selected. Guard TryEnter/finally Exit mirrors
        /// RegeneratePreviewCore. No dirty pre-check: CapturePrefab renders
        /// additively without dirtying the open scene. Shows a cancellable
        /// progress bar around the synchronous capture so the fixed cold
        /// cost reads as progress instead of a stuck UI. Internal for
        /// EditMode tests.
        /// </summary>
        internal void GeneratePrefabThumbnail(string prefabPath)
        {
            // An open Prefab Stage wins over the selection path so capture,
            // save, icon, and preview refs all target the staged prefab.
            string resolvedPrefabPath;
            if (UniThumbCapture.TryResolvePrefabAssetPath(prefabPath, out resolvedPrefabPath))
            {
                prefabPath = resolvedPrefabPath;
            }
            if (!UniThumbGuard.TryEnter())
            {
                return;
            }
            GenerateProgressWasShown = false;
            GenerateWasCancelled = false;
            try
            {
                CaptureSettings settings = BuildSettings();
                UniThumbCapture.RememberSettings(settings);
                bool cancelled = ShowGenerateProgress(
                    "Capturing prefab '" + Path.GetFileNameWithoutExtension(prefabPath) + "'...",
                    0.25f
                );
                if (cancelled)
                {
                    GenerateWasCancelled = true;
                    SetStatus("Prefab thumbnail generation cancelled.", MessageType.Info);
                    return;
                }
                CaptureResult result = UniThumbCapture.CapturePrefab(prefabPath, settings);
                if (!result.Success)
                {
                    SetStatus(
                        "Capture failed: " + (result.Warning ?? "unknown error."),
                        MessageType.Error
                    );
                    return;
                }

                cancelled = ShowGenerateProgress("Saving thumbnail...", 0.9f);
                if (cancelled)
                {
                    GenerateWasCancelled = true;
                    SetStatus("Prefab thumbnail generation cancelled.", MessageType.Info);
                    return;
                }
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                bool saved = UniThumbStorage.SavePrefabThumbnail(guid, result.PngBytes);
                if (!saved)
                {
                    SetStatus(
                        "Save failed for '" + prefabPath + "'. See Console.",
                        MessageType.Error
                    );
                    return;
                }

                UniThumbIconService.ApplyIcon(prefabPath);

                _previewPrefabPath = prefabPath;
                _previewGuid = guid;
                _previewTexture = UniThumbStorage.LoadPrefabThumbnail(guid);
                string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? string.Empty
                    : " (warning: " + result.Warning + ")";
                SetStatus(
                    "Saved "
                        + settings.Width
                        + "x"
                        + settings.Height
                        + " thumbnail for prefab '"
                        + prefabName
                        + "'."
                        + suffix,
                    MessageType.Info
                );
            }
            finally
            {
                ClearGenerateProgress();
                UniThumbGuard.Exit();
            }
            UpdateGenerateState();
            UpdatePreviewUI();
        }

        /// <summary>
        /// Prefab auto-regeneration entry point (prefab save with Regenerate
        /// on Save on, while the window is open). Same capture/storage/icon
        /// path as the manual Generate button (CapturePrefab, no scene
        /// switch, no scene dirtying). Guard TryEnter/finally Exit; a busy
        /// guard skips silently. Refreshes the preview refs only when the
        /// regenerated prefab is the current preview target. Internal for
        /// the save processor and EditMode tests.
        /// </summary>
        internal void RegeneratePrefabThumbnailOnSave(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
            {
                return;
            }
            // An open Prefab Stage wins over the passed path so capture and
            // save target the staged prefab (mirrors GeneratePrefabThumbnail).
            string resolvedPrefabPath;
            if (UniThumbCapture.TryResolvePrefabAssetPath(prefabPath, out resolvedPrefabPath))
            {
                prefabPath = resolvedPrefabPath;
            }
            if (!UniThumbGuard.TryEnter())
            {
                return;
            }
            try
            {
                CaptureSettings settings = BuildSettings();
                UniThumbCapture.RememberSettings(settings);
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
                bool saved = UniThumbStorage.SavePrefabThumbnail(guid, result.PngBytes);
                if (!saved)
                {
                    return;
                }

                UniThumbIconService.ApplyIcon(prefabPath);

                // Mutation-point texture source: Load is allowed here (never
                // on the UI refresh path). Storage owns the texture; only the
                // reference is kept.
                if (string.Equals(_previewPrefabPath, prefabPath, StringComparison.Ordinal))
                {
                    _previewGuid = guid;
                    _previewTexture = UniThumbStorage.LoadPrefabThumbnail(guid);
                }
                MarkPreviewDirty();
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            UpdateGenerateState();
            UpdatePreviewUI();
        }

        /// <summary>
        /// Core capture-and-save flow shared by the Generate button and the
        /// auto-regeneration hook. TryEnter/Exit guards the whole block so
        /// callers never touch the guard directly. Shows a cancellable
        /// progress bar around the synchronous capture so the fixed cold
        /// cost reads as progress instead of a stuck UI.
        /// </summary>
        private void RegeneratePreviewCore()
        {
            if (!UniThumbGuard.TryEnter())
            {
                return;
            }
            GenerateProgressWasShown = false;
            GenerateWasCancelled = false;
            try
            {
                // Prefab Stage open: a scene capture now would render the stage
                // scene contents into the active scene slot. Abort with status
                // text; capture the staged prefab via the prefab flow instead.
                string openStagePath;
                if (UniThumbCapture.TryGetPrefabStageAssetPath(out openStagePath))
                {
                    SetStatus(
                        "Cannot generate scene thumbnail while the Prefab Stage is open ('"
                            + openStagePath
                            + "'). Close the stage or capture the prefab instead.",
                        MessageType.Warning
                    );
                    return;
                }
                string scenePath = EditorSceneManager.GetActiveScene().path;
                if (string.IsNullOrEmpty(scenePath))
                {
                    SetStatus(
                        "Cannot generate: no saved active scene. Save the scene first.",
                        MessageType.Error
                    );
                    return;
                }

                CaptureSettings settings = BuildSettings();
                UniThumbCapture.RememberSettings(settings);
                bool cancelled = ShowGenerateProgress(
                    "Capturing scene '" + Path.GetFileNameWithoutExtension(scenePath) + "'...",
                    0.25f
                );
                if (cancelled)
                {
                    GenerateWasCancelled = true;
                    SetStatus("Thumbnail generation cancelled.", MessageType.Info);
                    return;
                }
                CaptureResult result = UniThumbCapture.Capture(settings);
                if (!result.Success)
                {
                    SetStatus(
                        "Capture failed: " + (result.Warning ?? "unknown error."),
                        MessageType.Error
                    );
                    return;
                }

                // Save the captured PNG.
                bool cancelledBeforeSave = ShowGenerateProgress("Saving thumbnail...", 0.9f);
                if (cancelledBeforeSave)
                {
                    GenerateWasCancelled = true;
                    SetStatus("Thumbnail generation cancelled.", MessageType.Info);
                    return;
                }
                bool saved = UniThumbStorage.Save(scenePath, result.PngBytes);
                if (!saved)
                {
                    SetStatus(
                        "Save failed for '" + scenePath + "'. See Console.",
                        MessageType.Error
                    );
                    return;
                }

                // Match the menu-path ordering (UniThumbBatchMenus): apply the
                // Project window icon immediately so the thumbnail shows without a
                // domain reload or Refresh All. Mutation-point texture source:
                // Load is allowed here (never on the UI refresh path).
                UniThumbIconService.ApplyIcon(scenePath);

                _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                _previewTexture = UniThumbStorage.Load(scenePath);
                string sceneName = Path.GetFileNameWithoutExtension(scenePath);
                string suffix = string.IsNullOrEmpty(result.Warning)
                    ? string.Empty
                    : " (warning: " + result.Warning + ")";
                SetStatus(
                    "Saved "
                        + settings.Width
                        + "x"
                        + settings.Height
                        + " thumbnail for '"
                        + sceneName
                        + "'."
                        + suffix,
                    MessageType.Info
                );
            }
            finally
            {
                ClearGenerateProgress();
                UniThumbGuard.Exit();
            }
            UpdateGenerateState();
            UpdatePreviewUI();
        }

        private void DeleteActiveUniThumb()
        {
            string prefabPath;
            if (TryGetSelectedPrefabPath(out prefabPath))
            {
                DeletePrefabThumbnail(prefabPath);
                return;
            }
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                SetStatus(
                    "Cannot delete: no saved active scene. Save the scene first.",
                    MessageType.Warning
                );
                return;
            }
            if (!UniThumbGuard.TryEnter())
            {
                SetStatus("A thumbnail generation is already in progress.", MessageType.Warning);
                return;
            }
            try
            {
                bool deleted = UniThumbStorage.Delete(scenePath);
                UniThumbIconService.ClearIcon(scenePath);
                string sceneName = Path.GetFileNameWithoutExtension(scenePath);
                SetStatus(
                    deleted
                        ? "Deleted thumbnail for '" + sceneName + "'."
                        : "No thumbnail to delete for '" + sceneName + "'.",
                    deleted ? MessageType.Info : MessageType.Info
                );
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            _previewTexture = null;
            UpdateGenerateState();
            UpdatePreviewUI();
            MarkPreviewDirty();
        }

        /// <summary>
        /// Prefab delete flow for the Delete button while a prefab asset is
        /// selected. Guard TryEnter/finally Exit mirrors the scene path;
        /// MarkPreviewDirty re-arms the live prefab preview so the box
        /// re-renders (empty-bounds orbit framing) instead of going stale.
        /// </summary>
        private void DeletePrefabThumbnail(string prefabPath)
        {
            if (!UniThumbGuard.TryEnter())
            {
                SetStatus("A thumbnail generation is already in progress.", MessageType.Warning);
                return;
            }
            try
            {
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                bool deleted = UniThumbStorage.DeleteByGuid(guid);
                UniThumbIconService.ClearIcon(prefabPath);
                string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
                SetStatus(
                    deleted
                        ? "Deleted thumbnail for prefab '" + prefabName + "'."
                        : "No thumbnail to delete for prefab '" + prefabName + "'.",
                    MessageType.Info
                );
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            _previewTexture = null;
            UpdateGenerateState();
            UpdatePreviewUI();
            MarkPreviewDirty();
        }

        /// <summary>
        /// Scoped asset-type noun for batch UI labels. Consumes
        /// UniThumbBatchMenus.BatchScope (never redefined here).
        /// </summary>
        internal static string BatchScopeNoun(UniThumbBatchMenus.BatchScope scope)
        {
            switch (scope)
            {
                case UniThumbBatchMenus.BatchScope.ScenesOnly:
                    return "scene(s)";
                case UniThumbBatchMenus.BatchScope.PrefabsOnly:
                    return "prefab(s)";
                default:
                    return "scene/prefab(s)";
            }
        }

        /// <summary>
        /// Scoped clear-dialog message seam (scope plus count plus folder).
        /// Static for EditMode tests.
        /// </summary>
        internal static string BuildClearFolderMessage(
            UniThumbBatchMenus.BatchScope scope,
            int count,
            string folderPath
        )
        {
            return "Delete "
                + count
                + " thumbnail(s) for "
                + BatchScopeNoun(scope)
                + " under '"
                + folderPath
                + "'? This cannot be undone."
                + UniThumbBatchMenus.DiscardDisclosure;
        }

        private void ClearFolderThumbnails()
        {
            if (!_folderValid || string.IsNullOrEmpty(_batchFolderPath))
            {
                SetStatus("Pick a valid batch folder first.", MessageType.Warning);
                return;
            }
            List<string> assets = UniThumbBatchMenus.CollectFolderPaths(
                _batchFolderPath,
                _batchScope
            );
            List<string> targets = new List<string>();
            for (int i = 0; i < assets.Count; i++)
            {
                if (UniThumbStorage.HasThumbnail(assets[i]))
                {
                    targets.Add(assets[i]);
                }
            }
            if (targets.Count == 0)
            {
                SetStatus(
                    "No "
                        + BatchScopeNoun(_batchScope)
                        + " thumbnails to clear in '"
                        + _batchFolderPath
                        + "'.",
                    MessageType.Info
                );
                return;
            }
            bool confirmed = EditorUtility.DisplayDialog(
                "Clear Folder Thumbnails",
                BuildClearFolderMessage(_batchScope, targets.Count, _batchFolderPath),
                "Delete",
                "Cancel"
            );
            if (!confirmed)
            {
                SetStatus("Folder thumbnail clear cancelled.", MessageType.Info);
                return;
            }
            if (!UniThumbGuard.TryEnter())
            {
                SetStatus("A thumbnail generation is already in progress.", MessageType.Warning);
                return;
            }
            int deleted = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (UniThumbStorage.Delete(targets[i]))
                    {
                        deleted++;
                    }
                }
                UniThumbIconService.ReapplyAllIcons();
                SetStatus(
                    "Deleted "
                        + deleted
                        + " of "
                        + targets.Count
                        + " thumbnails in '"
                        + _batchFolderPath
                        + "'.",
                    deleted == targets.Count ? MessageType.Info : MessageType.Warning
                );
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            ResetActiveScenePreview();
            UpdateGenerateState();
            UpdateBatchUI();
            MarkPreviewDirty();
        }

        private void ResetActiveScenePreview()
        {
            RefreshPreviewForSelection();
        }

        private CaptureSettings BuildSettings()
        {
            int width;
            int height;
            GetResolution(out width, out height);
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = width;
            settings.Height = height;
            settings.UseSceneViewAngle = _useSceneViewAngle;
            settings.orthographic = _orthographic2D;
            settings.OrbitYaw = _orbitYaw;
            settings.OrbitPitch = _orbitPitch;
            settings.orbitDistanceMultiplier = _orbitDistanceMultiplier;
            settings.OrbitFov = _orbitFov;
            settings.FitFactor = _fitFactor;
            settings.UseLightingOverride = _useLightingOverride;
            settings.BackgroundColor = _backgroundColor;
            settings.BackgroundMode = _backgroundMode;
            settings.WantPostProcessing = _wantPostProcessing;
            settings.PostProcessingProfile = _postProcessingProfile;
            // Shared scene+prefab UI toggle (default true renders prefab UI;
            // off culls UI with baseline parity). No prefab-specific setting.
            settings.CaptureUi = _captureUi;
            settings.UiScale = Mathf.Clamp(_uiScale, 0.25f, 4f);
            settings.layerMask = _layerMask;
            settings.Light2DMode = _lightingMode;
            settings.Light2DIntensity = _light2DIntensity;
            settings.Light2DSortingLayerIds = SortingLayerMaskToIds(_light2DSortingLayers);
            settings.Light3DIntensity = _light3DIntensity;
            settings.Light3DPrefabIntensity = _light3DPrefabIntensity;
            settings.Light3DShadows = _light3DShadows;
            settings.Light3DColor = _light3DColor;
            settings.Light3DYaw = _light3DYaw;
            settings.Light3DPitch = _light3DPitch;
            settings.Light3DYawMin = _light3DYawMin;
            settings.Light3DYawMax = _light3DYawMax;
            settings.Light3DPitchMin = _light3DPitchMin;
            settings.Light3DPitchMax = _light3DPitchMax;
            settings.ParticlePreviewTime = Mathf.Clamp(_particlePreviewTime, 0f, 5f);
            return settings;
        }

        /// <summary>
        /// Writes all capture-related fields from this window into the given
        /// settings asset via individual setter calls. Called by
        /// UniThumbSettings.SaveCaptureSettings in OnDisable.
        /// </summary>
        internal void PersistCaptureSettings(UniThumbSettings settings)
        {
            settings.SetResolutionIndex(_resolutionIndex);
            settings.SetUseSceneViewAngle(_useSceneViewAngle);
            settings.SetOrthographic2D(_orthographic2D);
            settings.SetOrbitYaw(_orbitYaw);
            settings.SetOrbitPitch(_orbitPitch);
            settings.SetOrbitDistanceMultiplier(_orbitDistanceMultiplier);
            settings.SetOrbitFov(_orbitFov);
            settings.SetFitFactor(_fitFactor);
            settings.SetUseLightingOverride(_useLightingOverride);
            settings.SetBackgroundColor(_backgroundColor);
            settings.SetBackgroundMode(_backgroundMode);
            settings.SetWantPostProcessing(_wantPostProcessing);
            settings.SetPostProcessingProfile(_postProcessingProfile);
            settings.SetCaptureUi(_captureUi);
            settings.SetUiScale(_uiScale);
            settings.SetLayerMask(_layerMask);
            settings.SetLightingMode(_lightingMode);
            settings.SetLight2DIntensity(_light2DIntensity);
            settings.SetLight2DSortingLayers(_light2DSortingLayers);
            settings.SetLight3DIntensityMax(_light3DIntensityMax);
            settings.SetLight3DIntensity(_light3DIntensity);
            settings.SetLight3DPrefabIntensity(_light3DPrefabIntensity);
            settings.SetLight3DShadows(_light3DShadows);
            settings.SetLight3DColor(_light3DColor);
            settings.SetLight3DYawMin(_light3DYawMin);
            settings.SetLight3DYawMax(_light3DYawMax);
            settings.SetLight3DYaw(_light3DYaw);
            settings.SetLight3DPitchMin(_light3DPitchMin);
            settings.SetLight3DPitchMax(_light3DPitchMax);
            settings.SetLight3DPitch(_light3DPitch);
            settings.SetParticlePreviewTime(_particlePreviewTime);
        }

        /// <summary>
        /// Reads capture settings from the given settings asset and applies
        /// them to this window's serialized fields. Called by
        /// UniThumbSettings.LoadCaptureSettings in OnEnable, BEFORE PushState
        /// so the persisted values flow into UI controls.
        /// </summary>
        internal void ApplyPersistedCaptureSettings(UniThumbSettings settings)
        {
            _resolutionIndex = settings.ResolutionIndex;
            _useSceneViewAngle = settings.UseSceneViewAngle;
            _orthographic2D = settings.Orthographic2D;
            _orbitYaw = settings.OrbitYaw;
            _orbitPitch = settings.OrbitPitch;
            _orbitDistanceMultiplier = settings.OrbitDistanceMultiplier;
            _orbitFov = settings.OrbitFov;
            _fitFactor = settings.FitFactor;
            _useLightingOverride = settings.UseLightingOverride;
            _backgroundColor = settings.BackgroundColor;
            _backgroundMode = settings.BackgroundMode;
            _wantPostProcessing = settings.WantPostProcessing;
            _postProcessingProfile = settings.PostProcessingProfile;
            _captureUi = settings.CaptureUi;
            _uiScale = settings.UiScale;
            _layerMask = settings.LayerMask;
            _lightingMode = settings.LightingMode;
            _light2DIntensity = settings.Light2DIntensity;
            _light2DSortingLayers = settings.Light2DSortingLayers;
            _light3DIntensityMax = settings.Light3DIntensityMax;
            _light3DIntensity = Mathf.Clamp(settings.Light3DIntensity, 0f, _light3DIntensityMax);
            _light3DPrefabIntensity = Mathf.Clamp(
                settings.Light3DPrefabIntensity,
                0f,
                _light3DIntensityMax
            );
            _light3DShadows = settings.Light3DShadows;
            _light3DColor = settings.Light3DColor;
            _light3DYawMin = settings.Light3DYawMin;
            _light3DYawMax = settings.Light3DYawMax;
            _light3DYaw = Mathf.Clamp(settings.Light3DYaw, _light3DYawMin, _light3DYawMax);
            _light3DPitchMin = settings.Light3DPitchMin;
            _light3DPitchMax = settings.Light3DPitchMax;
            _light3DPitch = Mathf.Clamp(settings.Light3DPitch, _light3DPitchMin, _light3DPitchMax);
            _particlePreviewTime = Mathf.Clamp(settings.ParticlePreviewTime, 0f, 5f);
        }

        private void GetResolution(out int width, out int height)
        {
            int index = Mathf.Clamp(_resolutionIndex, 0, k_PresetResolutions.Length - 1);
            width = k_PresetResolutions[index];
            height = k_PresetResolutions[index];
        }

        /// <summary>
        /// Converts a sorting-layer-index bitmask (one bit per SortingLayer.layers
        /// entry) into an array of sorting layer IDs suitable for Light2D's
        /// m_ApplyToSortingLayers. Returns null when all bits are set (all layers).
        /// </summary>
        private static int[] SortingLayerMaskToIds(int mask)
        {
            SortingLayer[] layers = SortingLayer.layers;
            if (layers.Length == 0)
            {
                return null;
            }
            if (mask == -1 || mask == (1 << layers.Length) - 1)
            {
                return null;
            }
            var ids = new List<int>();
            for (int i = 0; i < layers.Length; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    ids.Add(layers[i].id);
                }
            }
            return ids.Count > 0 ? ids.ToArray() : null;
        }

        private void UpdateUpdateBanner()
        {
            if (_updateBanner == null || !UniThumbUpdateChecker.IsCheckComplete)
            {
                return;
            }

            bool show = UniThumbUpdateChecker.IsUpdateAvailable;
            _updateBanner.EnableInClassList("stt-hidden", !show);

            if (show && _updateBannerLabel != null)
            {
                _updateBannerLabel.text =
                    "UniThumb "
                    + UniThumbUpdateChecker.LatestVersion
                    + " is available (current: "
                    + GetCurrentVersion()
                    + ")";
            }
        }

        private string GetCurrentVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(UniThumbWindow).Assembly
            );
            return info?.version ?? "unknown";
        }

        private void DismissUpdateBanner()
        {
            _updateBanner?.EnableInClassList("stt-hidden", true);
        }

        #endregion
    }
}
