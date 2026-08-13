using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MaykerStudio.SceneThumbnails
{
    /// <summary>
    /// Manual-only scene thumbnail tool window. The Generate button is the ONLY
    /// single-scene capture path in this window: no capture happens on
    /// open/close/repaint or on scene add/modify/save. The window also drives
    /// the folder batch (SceneThumbnailBatchMenus.TryStartFolderBatch) with
    /// progress and result feedback. Capture logic lives in
    /// SceneThumbnailCapture; the thumbnail folder is fixed and owned by
    /// SceneThumbnailStorage (shown read-only in the footer).
    /// UI Toolkit build (SceneThumbnailWindow.uxml/.uss); the window never uses
    /// the IMGUI painting path.
    /// </summary>
    public class SceneThumbnailWindow : EditorWindow
    {
        #region Constants

        private const string k_MenuPath = "Window/Scene Thumbnail Tool";
        private const string k_WindowTitle = "Scene Thumbnail Tool";
        private const string k_NoThumbnailLabel = "No thumbnail yet - press Generate Thumbnail";
        private const string k_InvalidFolderLabel =
            "Not a project folder - pick a folder inside Assets/";
        private const string k_DisabledFramingLabel =
            "Disabled while using the Scene View angle in perspective mode";
        private const string k_ColorDisabledLabel =
            "Background color only applies in Solid Color mode.";
        private const string k_UxmlPath =
            "Assets/Editor/SceneThumbnailTool/SceneThumbnailWindow.uxml";
        private const string k_UssPath =
            "Assets/Editor/SceneThumbnailTool/SceneThumbnailWindow.uss";
        private const int k_PreviewRefetchAttempts = 4;
        private const long k_PreviewRefetchDelayMs = 100;
        private const int k_MaxFolderMenuEntries = 500;

        private static readonly int[] k_PresetResolutions = { 16, 32, 64, 128, 256, 512 };

        #endregion

        #region Fields

        [SerializeField]
        private int _resolutionIndex = 1;

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
        private bool _useLightingOverride;

        [SerializeField]
        private Color _backgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f);

        [SerializeField]
        private BackgroundMode _backgroundMode = BackgroundMode.Skybox;

        [SerializeField]
        private bool _wantPostProcessing = true;

        [SerializeField]
        private LayerMask _layerMask = -1;

        [SerializeField]
        private bool _backgroundEffectsFoldout = true;

        [SerializeField]
        private string _batchFolderInput;

        [SerializeField]
        private string _batchFolderPath;

        private string _folderPath;
        private string _statusMessage = string.Empty;
        private MessageType _statusType = MessageType.None;

        // Cache-owned runtime texture (SceneThumbnailStorage is the sole owner):
        // never destroy it, only drop the reference. Re-fetched by GUID after
        // eviction (destroyed instances compare == null).
        private Texture2D _previewTexture;
        private string _previewGuid;
        private bool _previewStale;

        private string _batchResultMessage;
        private MessageType _batchResultType = MessageType.None;
        private bool _folderValid;
        private bool _batchFolderInputInvalid;
        private bool _cancelRequested;
        private bool _wasBatchRunning;
        private float _batchStartTime;

        private RenderTexture _livePreviewRT;
        private Texture2D _livePreviewTexture;
        private bool _previewDirty;

        private readonly List<LightSnapshot> _previewLightSnapshots = new List<LightSnapshot>();

        // Cached Scene View transform used to throttle live-preview re-renders
        // while the user navigates the Scene View (see OnSceneViewGui).
        private Vector3 _lastSceneViewPos;
        private Quaternion _lastSceneViewRot;
        private float _lastSceneViewSize;
        private float _lastSceneViewFov;

        #endregion

        #region UI Elements

        private Label _activeSceneLabel;
        private Button _generateButton;
        private Label _generateBusyLabel;
        private Label _noThumbnailLabel;
        private VisualElement _previewBox;
        private Image _previewImage;
        private VisualElement _previewCaptionRow;
        private Label _previewCaption;
        private Label _staleBadge;
        private DropdownField _resolutionPopup;
        private Toggle _orthographicToggle;
        private Toggle _sceneViewAngleToggle;
        private Slider _framingDistanceSlider;
        private Label _framingDisabledHint;
        private VisualElement _orbitControls;
        private Slider _orbitYawSlider;
        private Slider _orbitPitchSlider;
        private Button _presetFrontButton;
        private Button _presetThreequarterButton;
        private Button _presetTopButton;
        private Foldout _bgEffectsFoldout;
        private EnumField _bgModeField;
        private ColorField _bgColorField;
        private Label _bgColorHint;
        private Toggle _lightingToggle;
        private Toggle _postfxToggle;
        private MaskField _layersMask;
        private VisualElement _batchFolderRow;
        private TextField _batchFolderField;
        private Button _browseButton;
        private Button _useSceneFolderButton;
        private Label _batchInvalidLabel;
        private Button _generateFolderButton;
        private VisualElement _batchProgress;
        private ProgressBar _batchProgressBar;
        private Label _batchProgressCaption;
        private Button _cancelBatchButton;
        private HelpBox _batchResultHelp;
        private HelpBox _statusHelp;
        private Label _footerLabel;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            minSize = new Vector2(320f, 480f);
            maxSize = new Vector2(600f, 800f);
            _folderPath = SceneThumbnailStorage.EnsureFolder();
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
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChangedInEditMode;
            SceneThumbnailStorage.TextureEvicted += OnTextureEvicted;
            EditorApplication.update += OnBatchUpdateTick;
            SceneView.duringSceneGui += OnSceneViewGui;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnBatchUpdateTick;
            EditorApplication.update -= RenderLivePreview;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChangedInEditMode;
            SceneView.duringSceneGui -= OnSceneViewGui;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            SceneThumbnailStorage.TextureEvicted -= OnTextureEvicted;
            RestorePreviewLights();
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
        }

        private void CreateGUI()
        {
            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_UssPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }
            else
            {
                Debug.LogError("SceneThumbnailWindow: stylesheet not found at " + k_UssPath);
            }

            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_UxmlPath);
            if (tree == null)
            {
                Debug.LogError("SceneThumbnailWindow: UXML not found at " + k_UxmlPath);
                rootVisualElement.Add(
                    new Label("Failed to load SceneThumbnailWindow.uxml - see Console.")
                );
                return;
            }

            rootVisualElement.AddToClassList(
                EditorGUIUtility.isProSkin ? "theme-dark" : "theme-light"
            );
            rootVisualElement.Add(tree.CloneTree());

            CacheElements();
            WireCallbacks();
            ApplySectionIcons();
            _wasBatchRunning = SceneThumbnailBatchMenus.IsBatchRunning;
            PushState();
        }

        #endregion

        #region Public Methods

        [MenuItem(k_MenuPath)]
        public static void OpenWindow()
        {
            GetWindow<SceneThumbnailWindow>(k_WindowTitle);
        }

        #endregion

        #region Private Methods

        private void CacheElements()
        {
            _activeSceneLabel = rootVisualElement.Q<Label>("active-scene-label");
            _generateButton = rootVisualElement.Q<Button>("generate-button");
            _generateBusyLabel = rootVisualElement.Q<Label>("generate-busy-label");
            _noThumbnailLabel = rootVisualElement.Q<Label>("no-thumbnail-label");
            _previewBox = rootVisualElement.Q<VisualElement>("preview-box");
            _previewImage = rootVisualElement.Q<Image>("preview-image");
            _previewCaptionRow = rootVisualElement.Q<VisualElement>("preview-caption-row");
            _previewCaption = rootVisualElement.Q<Label>("preview-caption");
            _staleBadge = rootVisualElement.Q<Label>("stale-badge");
            _resolutionPopup = rootVisualElement.Q<DropdownField>("resolution-popup");
            _orthographicToggle = rootVisualElement.Q<Toggle>("orthographic-toggle");
            _sceneViewAngleToggle = rootVisualElement.Q<Toggle>("sceneview-angle-toggle");
            _framingDistanceSlider = rootVisualElement.Q<Slider>("framing-distance-slider");
            _framingDisabledHint = rootVisualElement.Q<Label>("framing-disabled-hint");
            _orbitControls = rootVisualElement.Q<VisualElement>("orbit-controls");
            _orbitYawSlider = rootVisualElement.Q<Slider>("orbit-yaw-slider");
            _orbitPitchSlider = rootVisualElement.Q<Slider>("orbit-pitch-slider");
            _presetFrontButton = rootVisualElement.Q<Button>("preset-front-button");
            _presetThreequarterButton = rootVisualElement.Q<Button>("preset-threequarter-button");
            _presetTopButton = rootVisualElement.Q<Button>("preset-top-button");
            _bgEffectsFoldout = rootVisualElement.Q<Foldout>("bg-effects-foldout");
            _bgModeField = rootVisualElement.Q<EnumField>("bg-mode-field");
            _bgColorField = rootVisualElement.Q<ColorField>("bg-color-field");
            _bgColorHint = rootVisualElement.Q<Label>("bg-color-hint");
            _lightingToggle = rootVisualElement.Q<Toggle>("lighting-toggle");
            _postfxToggle = rootVisualElement.Q<Toggle>("postfx-toggle");
            _layersMask = rootVisualElement.Q<MaskField>("layers-mask");
            _batchFolderRow = rootVisualElement.Q<VisualElement>("batch-folder-row");
            _batchFolderField = rootVisualElement.Q<TextField>("batch-folder-field");
            _browseButton = rootVisualElement.Q<Button>("browse-button");
            _useSceneFolderButton = rootVisualElement.Q<Button>("use-scene-folder-button");
            _batchInvalidLabel = rootVisualElement.Q<Label>("batch-invalid-label");
            _generateFolderButton = rootVisualElement.Q<Button>("generate-folder-button");
            _batchProgress = rootVisualElement.Q<VisualElement>("batch-progress");
            _batchProgressBar = rootVisualElement.Q<ProgressBar>("batch-progress-bar");
            _batchProgressCaption = rootVisualElement.Q<Label>("batch-progress-caption");
            _cancelBatchButton = rootVisualElement.Q<Button>("cancel-batch-button");
            _batchResultHelp = rootVisualElement.Q<HelpBox>("batch-result-help");
            _statusHelp = rootVisualElement.Q<HelpBox>("status-help");
            _footerLabel = rootVisualElement.Q<Label>("footer-label");
        }

        private void ApplySectionIcons()
        {
            SetSectionIcon("stt-icon-preview", "d_SceneAsset Icon");
            SetSectionIcon("stt-icon-resolution", "d_Settings Icon");
            SetSectionIcon("stt-icon-framing", "d_Camera Icon");
            SetSectionIcon("stt-icon-bgfx", "d_Skybox Icon");
            SetSectionIcon("stt-icon-layers", "d_TagManager Icon");
            SetSectionIcon("stt-icon-batch", "d_FolderOpened Icon");
        }

        private void SetSectionIcon(string elementName, string iconName)
        {
            Image icon = rootVisualElement.Q<Image>(elementName);
            if (icon == null)
            {
                return;
            }
            GUIContent content = EditorGUIUtility.IconContent(iconName);
            if (content == null || content.image == null)
            {
                return;
            }
            icon.image = content.image;
            icon.EnableInClassList("stt-hidden", false);
        }

        private void WireCallbacks()
        {
            if (_generateButton != null)
            {
                _generateButton.clicked += GenerateThumbnail;
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
                    ApplyOrbitPreset(0f, 0f);
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
            if (_bgEffectsFoldout != null)
            {
                _bgEffectsFoldout.RegisterValueChangedCallback(evt =>
                {
                    _backgroundEffectsFoldout = evt.newValue;
                });
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
            if (_generateFolderButton != null)
            {
                _generateFolderButton.clicked += StartFolderBatch;
            }
            if (_cancelBatchButton != null)
            {
                _cancelBatchButton.clicked += CancelBatch;
            }
            if (_batchFolderRow != null)
            {
                RegisterBatchFolderDragDrop(_batchFolderRow);
            }
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        private void PushState()
        {
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
            if (_orbitYawSlider != null)
            {
                _orbitYawSlider.SetValueWithoutNotify(_orbitYaw);
            }
            if (_orbitPitchSlider != null)
            {
                _orbitPitchSlider.SetValueWithoutNotify(_orbitPitch);
            }
            UpdateFramingState();

            if (_bgEffectsFoldout != null)
            {
                _bgEffectsFoldout.SetValueWithoutNotify(_backgroundEffectsFoldout);
            }
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
            if (_layersMask != null)
            {
                _layersMask.choices = new List<string>(
                    UnityEditorInternal.InternalEditorUtility.layers
                );
                _layersMask.value = _layerMask.value;
            }

            if (_batchFolderField != null)
            {
                _batchFolderField.SetValueWithoutNotify(_batchFolderInput ?? string.Empty);
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
            if (_bgColorHint != null)
            {
                _bgColorHint.text = k_ColorDisabledLabel;
            }
            if (_footerLabel != null)
            {
                _footerLabel.text =
                    "Thumbnail Folder: "
                    + _folderPath
                    + " | Ctrl+Enter: Generate | Ctrl+Shift+Enter: Batch | Esc: Cancel";
            }
            if (_batchProgressBar != null)
            {
                _batchProgressBar.highValue = 1f;
            }

            RefreshPreviewTexture();
            UpdatePreviewUI();
            MarkPreviewDirty();
            SetStatus(_statusMessage, _statusType);
            UpdateBatchUI();
        }

        private void UpdateActiveSceneLabel()
        {
            if (_activeSceneLabel == null)
            {
                return;
            }
            string scenePath = EditorSceneManager.GetActiveScene().path;
            _activeSceneLabel.text = string.IsNullOrEmpty(scenePath)
                ? "(unsaved / no scene open)"
                : scenePath;
        }

        private void UpdateGenerateState()
        {
            bool busy = SceneThumbnailGuard.IsGenerating;
            if (_generateButton != null)
            {
                _generateButton.SetEnabled(!busy);
            }
            if (_generateBusyLabel != null)
            {
                _generateBusyLabel.EnableInClassList("stt-hidden", !busy);
            }
        }

        private void UpdateFramingState()
        {
            bool framingDisabled = _useSceneViewAngle && !_orthographic2D;
            if (_framingDistanceSlider != null)
            {
                _framingDistanceSlider.SetEnabled(!framingDisabled);
            }
            if (_framingDisabledHint != null)
            {
                _framingDisabledHint.EnableInClassList("stt-hidden", !framingDisabled);
            }
            bool showOrbit = !_useSceneViewAngle || _orthographic2D;
            if (_orbitControls != null)
            {
                _orbitControls.EnableInClassList("stt-hidden", !showOrbit);
            }
        }

        private void UpdateBgColorState()
        {
            bool colorDisabled = _backgroundMode != BackgroundMode.SolidColor;
            if (_bgColorField != null)
            {
                _bgColorField.SetEnabled(!colorDisabled);
            }
            if (_bgColorHint != null)
            {
                _bgColorHint.EnableInClassList("stt-hidden", !colorDisabled);
            }
        }

        private void MarkPreviewDirty()
        {
            if (_previewDirty)
            {
                return;
            }
            _previewDirty = true;
            EditorApplication.update += RenderLivePreview;
            Repaint();
        }

        private void RenderLivePreview()
        {
            _previewDirty = false;
            EditorApplication.update -= RenderLivePreview;

            if (SceneThumbnailGuard.IsGenerating || rootVisualElement == null)
            {
                return;
            }

            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                return;
            }

            try
            {
                GetResolution(out int previewWidth, out int previewHeight);
                previewWidth = Mathf.Max(1, previewWidth);
                previewHeight = Mathf.Max(1, previewHeight);

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
                    _livePreviewRT = new RenderTexture(
                        previewWidth,
                        previewHeight,
                        24,
                        RenderTextureFormat.Default
                    );
                    _livePreviewRT.Create();
                }

                CaptureSettings settings = BuildSettings();
                GameObject tempGo = new GameObject("__SceneThumbnailPreviewCamera");
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                Camera cam = tempGo.AddComponent<Camera>();

                cam.aspect = (float)previewWidth / previewHeight;
                cam.enabled = false;

                if (settings.UseSceneViewAngle)
                {
                    if (!TryCopyPreviewFromSceneView(cam))
                    {
                        ApplyPreviewOrbitTransform(cam, settings);
                    }
                }
                else
                {
                    ApplyPreviewOrbitTransform(cam, settings);
                }

                cam.targetTexture = _livePreviewRT;
                cam.clearFlags =
                    settings.BackgroundMode == BackgroundMode.Skybox
                        ? CameraClearFlags.Skybox
                        : CameraClearFlags.SolidColor;
                cam.backgroundColor = settings.BackgroundColor;
                cam.cullingMask = settings.layerMask;

                try
                {
                    if (settings.UseLightingOverride)
                    {
                        DisableAllPreviewLights();
                    }
                    cam.Render();
                }
                finally
                {
                    RestorePreviewLights();
                }
                cam.targetTexture = null;

                RenderTexture prevActive = RenderTexture.active;
                RenderTexture.active = _livePreviewRT;
                if (
                    _livePreviewTexture == null
                    || _livePreviewTexture.width != previewWidth
                    || _livePreviewTexture.height != previewHeight
                )
                {
                    if (_livePreviewTexture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_livePreviewTexture);
                    }
                    _livePreviewTexture = new Texture2D(
                        previewWidth,
                        previewHeight,
                        TextureFormat.RGBA32,
                        false
                    );
                }
                _livePreviewTexture.ReadPixels(new Rect(0, 0, previewWidth, previewHeight), 0, 0);
                _livePreviewTexture.Apply();
                RenderTexture.active = prevActive;

                UnityEngine.Object.DestroyImmediate(tempGo);

                if (_previewImage != null)
                {
                    _previewImage.image = _livePreviewTexture;
                    _previewBox?.EnableInClassList("stt-hidden", false);
                    _noThumbnailLabel?.EnableInClassList("stt-hidden", true);
                    _previewCaptionRow?.EnableInClassList("stt-hidden", false);
                    if (_previewCaption != null)
                    {
                        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
                        _previewCaption.text = sceneName + " (preview)";
                    }
                    if (_staleBadge != null)
                    {
                        _staleBadge.EnableInClassList("stt-hidden", true);
                    }
                    Repaint();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SceneThumbnailTool] Live preview failed: " + ex.Message);
            }
        }

        private void DisableAllPreviewLights()
        {
            _previewLightSnapshots.Clear();
#if UNITY_6000_0_OR_NEWER
            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>();
#else
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
#endif
            foreach (Light light in lights)
            {
                if (light == null)
                {
                    continue;
                }
                _previewLightSnapshots.Add(
                    new LightSnapshot
                    {
                        Light = light,
                        Enabled = light.enabled,
                        Intensity = light.intensity,
                    }
                );
                light.enabled = false;
                light.intensity = 0f;
            }
        }

        private void RestorePreviewLights()
        {
            foreach (LightSnapshot snapshot in _previewLightSnapshots)
            {
                if (snapshot.Light == null)
                {
                    continue;
                }
                snapshot.Light.enabled = snapshot.Enabled;
                snapshot.Light.intensity = snapshot.Intensity;
            }
            _previewLightSnapshots.Clear();
        }

        private bool TryCopyPreviewFromSceneView(Camera cam)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return false;
            }
            cam.transform.SetPositionAndRotation(
                sv.camera.transform.position,
                sv.camera.transform.rotation
            );
            if (_orthographic2D)
            {
                if (!sv.camera.orthographic)
                {
                    return false;
                }
                cam.orthographic = true;
                cam.orthographicSize = sv.camera.orthographicSize;
            }
            else
            {
                cam.orthographic = sv.camera.orthographic;
                if (cam.orthographic)
                {
                    cam.orthographicSize = sv.camera.orthographicSize;
                }
                else
                {
                    cam.fieldOfView = sv.camera.fieldOfView;
                }
            }
            cam.nearClipPlane = Mathf.Max(0.01f, sv.camera.nearClipPlane);
            cam.farClipPlane = Mathf.Max(1000f, sv.camera.farClipPlane);
            return true;
        }

        private void ApplyPreviewOrbitTransform(Camera cam, CaptureSettings settings)
        {
            Vector3 center = Vector3.zero;
            float radius = 5f;
            Bounds bounds;
            bool hasBounds = TryGetPreviewSceneBounds(settings.layerMask, out bounds);
            if (hasBounds)
            {
                center = bounds.center;
                radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            }

            float fitFactor = Mathf.Max(settings.FitFactor, 0.5f);
            float orbitDistanceMultiplier = Mathf.Clamp(
                settings.orbitDistanceMultiplier,
                0.1f,
                10f
            );
            float distance = radius / Mathf.Tan(60f * 0.5f * Mathf.Deg2Rad) * fitFactor;
            distance *= orbitDistanceMultiplier;

            cam.orthographic = settings.orthographic;
            if (settings.orthographic)
            {
                cam.orthographicSize = hasBounds
                    ? Mathf.Max(bounds.extents.y, bounds.extents.x / cam.aspect)
                        * orbitDistanceMultiplier
                    : 5f * orbitDistanceMultiplier;
            }
            else
            {
                cam.fieldOfView = 60f;
            }

            float yaw = settings.OrbitYaw * Mathf.Deg2Rad;
            float pitch = Mathf.Clamp(settings.OrbitPitch, -89f, 89f) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(
                Mathf.Cos(pitch) * Mathf.Sin(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Cos(yaw)
            );
            cam.transform.position = center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
            cam.farClipPlane = Mathf.Max(1000f, distance + radius * 4f);
        }

        private static bool TryGetPreviewSceneBounds(LayerMask layerMask, out Bounds bounds)
        {
#if UNITY_6000_0_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
#else
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }
                if ((layerMask.value & (1 << renderer.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return any;
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
                string scenePath = EditorSceneManager.GetActiveScene().path;
                if (!string.IsNullOrEmpty(scenePath))
                {
                    _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                }
            }
            if (_previewTexture == null && !string.IsNullOrEmpty(_previewGuid))
            {
                SceneThumbnailStorage.TryGetCachedTexture(_previewGuid, out _previewTexture);
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
                string scenePath = EditorSceneManager.GetActiveScene().path;
                _previewCaption.text =
                    Path.GetFileNameWithoutExtension(scenePath)
                    + " "
                    + _previewTexture.width
                    + "x"
                    + _previewTexture.height;
            }
            if (_staleBadge != null)
            {
                _staleBadge.EnableInClassList("stt-hidden", !_previewStale);
            }
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
            bool running = SceneThumbnailBatchMenus.IsBatchRunning;
            bool busy = SceneThumbnailGuard.IsGenerating;
            if (_generateFolderButton != null)
            {
                _generateFolderButton.SetEnabled(!busy && _folderValid);
            }
            if (_batchProgress != null)
            {
                _batchProgress.EnableInClassList("stt-hidden", !running);
            }
            if (running)
            {
                UpdateBatchProgress(SceneThumbnailBatchMenus.GetBatchSnapshot());
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

        private void UpdateBatchProgress(SceneThumbnailBatchMenus.BatchSnapshot snapshot)
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
                "Scene "
                + snapshot.Processed
                + "/"
                + snapshot.Total
                + ": "
                + snapshot.CurrentScene
                + etaText;
            if (_batchProgressBar != null)
            {
                _batchProgressBar.value = value;
                _batchProgressBar.title = text;
            }
            if (_batchProgressCaption != null)
            {
                _batchProgressCaption.text = text;
            }
        }

        private void CancelBatch()
        {
            SceneThumbnailBatchMenus.RequestBatchCancel();
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
            SceneThumbnailFolderMenu.ShowMenu(screenRect, folders, OnFolderPicked);
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
            bool busy = SceneThumbnailGuard.IsGenerating;
            if (_generateFolderButton != null)
            {
                _generateFolderButton.SetEnabled(!busy && _folderValid);
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
            string error;
            if (!SceneThumbnailBatchMenus.TryStartFolderBatch(_batchFolderPath, out error))
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
            SceneThumbnailBatchMenus.BatchSnapshot snapshot =
                SceneThumbnailBatchMenus.GetBatchSnapshot();
            if (snapshot.IsRunning)
            {
                if (!_wasBatchRunning)
                {
                    _wasBatchRunning = true;
                }
                UpdateBatchProgress(snapshot);
                return;
            }
            if (_wasBatchRunning)
            {
                _wasBatchRunning = false;
                OnBatchEnded(snapshot);
            }
        }

        private void OnBatchEnded(SceneThumbnailBatchMenus.BatchSnapshot snapshot)
        {
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(scenePath))
            {
                _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                _previewTexture = SceneThumbnailStorage.Load(scenePath);
                _previewStale = SceneThumbnailStorage.IsSceneStale(scenePath);
            }
            if (snapshot.Processed >= snapshot.Total)
            {
                _batchResultMessage =
                    "Completed "
                    + snapshot.Total
                    + " scenes: "
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
                    + " scenes.";
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
                if (SceneThumbnailBatchMenus.IsBatchRunning)
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
            if (SceneThumbnailGuard.IsGenerating)
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

        private void OnHierarchyChanged()
        {
            MarkPreviewDirty();
        }

        private void OnUndoRedoPerformed()
        {
            MarkPreviewDirty();
        }

        private void OnActiveSceneChangedInEditMode(Scene previousScene, Scene newScene)
        {
            _previewTexture = null;
            _previewGuid = null;
            _previewStale = false;
            string scenePath = newScene.path;
            if (!string.IsNullOrEmpty(scenePath))
            {
                _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                SceneThumbnailStorage.TryGetCachedTexture(_previewGuid, out _previewTexture);
            }
            UpdateActiveSceneLabel();
            UpdatePreviewUI();
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
            SchedulePreviewRefetch(k_PreviewRefetchAttempts);
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
            if (!SceneThumbnailGuard.TryEnter())
            {
                SetStatus("A thumbnail generation is already in progress.", MessageType.Warning);
                return;
            }
            try
            {
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
                CaptureResult result = SceneThumbnailCapture.Capture(settings);
                if (!result.Success)
                {
                    SetStatus(
                        "Capture failed: " + (result.Warning ?? "unknown error."),
                        MessageType.Error
                    );
                    return;
                }

                if (!SceneThumbnailStorage.Save(scenePath, result.PngBytes))
                {
                    SetStatus(
                        "Save failed for '" + scenePath + "'. See Console.",
                        MessageType.Error
                    );
                    return;
                }

                // Match the menu-path ordering (SceneThumbnailBatchMenus): apply the
                // Project window icon immediately so the thumbnail shows without a
                // domain reload or Refresh All. Mutation-point texture source:
                // Load is allowed here (never on the UI refresh path).
                SceneThumbnailIconService.ApplyIcon(scenePath);

                _previewGuid = AssetDatabase.AssetPathToGUID(scenePath);
                _previewTexture = SceneThumbnailStorage.Load(scenePath);
                _previewStale = false;
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
                SceneThumbnailGuard.Exit();
            }
            UpdateGenerateState();
            UpdatePreviewUI();
        }

        private CaptureSettings BuildSettings()
        {
            int width;
            int height;
            GetResolution(out width, out height);
            CaptureSettings settings = SceneThumbnailCapture.CreateDefaultSettings();
            settings.Width = width;
            settings.Height = height;
            settings.UseSceneViewAngle = _useSceneViewAngle;
            settings.orthographic = _orthographic2D;
            settings.OrbitYaw = _orbitYaw;
            settings.OrbitPitch = _orbitPitch;
            settings.orbitDistanceMultiplier = _orbitDistanceMultiplier;
            settings.UseLightingOverride = _useLightingOverride;
            settings.BackgroundColor = _backgroundColor;
            settings.BackgroundMode = _backgroundMode;
            settings.WantPostProcessing = _wantPostProcessing;
            settings.layerMask = _layerMask;
            return settings;
        }

        private void GetResolution(out int width, out int height)
        {
            int index = Mathf.Clamp(_resolutionIndex, 0, k_PresetResolutions.Length - 1);
            width = k_PresetResolutions[index];
            height = k_PresetResolutions[index];
        }

        #endregion

        #region Nested Types

        private struct LightSnapshot
        {
            public Light Light;
            public bool Enabled;
            public float Intensity;
        }

        #endregion
    }
}
