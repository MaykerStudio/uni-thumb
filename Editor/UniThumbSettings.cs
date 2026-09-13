using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

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

        private const float k_Light3DIntensityMin = 0f;
        private const float k_Light3DIntensityDefaultMax = 5f;
        private const float k_Light3DIntensityMaxCap = 10f;
        private const float k_Light3DPrefabIntensityDefault = 1.75f;
        private const float k_Light3DYawDefaultMin = -180f;
        private const float k_Light3DYawDefaultMax = 180f;
        private const float k_Light3DPitchDefaultMin = -89f;
        private const float k_Light3DPitchDefaultMax = 89f;
        private const float k_LightLimitEpsilon = 0.01f;
        private const float k_ParticlePreviewTimeMin = 0f;
        private const float k_ParticlePreviewTimeMax = 5f;
        private const float k_ParticlePreviewTimeDefault = 1f;

        #endregion

        #region Fields

        [SerializeField]
        private StorageMode m_StorageMode = StorageMode.LibraryCache;

        [SerializeField]
        private bool m_AutoRegenerateOnSave = false;

        [SerializeField]
        private bool m_CheckForUpdates = false;

        [SerializeField]
        private bool m_AllowBatchDiscard = false;

        [SerializeField]
        private bool m_CaptureSettingsInitialized = false;

        [SerializeField]
        private bool m_IconSizeRangeMigrated = false;

        [SerializeField]
        private int m_ResolutionIndex = 3;

        [SerializeField]
        private bool m_UseSceneViewAngle = true;

        [SerializeField]
        private bool m_Orthographic2D = false;

        [SerializeField]
        private float m_OrbitYaw = 45f;

        [SerializeField]
        private float m_OrbitPitch = 25f;

        [SerializeField]
        private float m_OrbitDistanceMultiplier = 1f;

        [SerializeField]
        private float m_OrbitFov = 60f;

        [SerializeField]
        private float m_FitFactor = 2f;

        [SerializeField]
        private bool m_UseLightingOverride = false;

        [SerializeField]
        private Color m_BackgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f);

        [SerializeField]
        private BackgroundMode m_BackgroundMode = BackgroundMode.Skybox;

        [SerializeField]
        private bool m_WantPostProcessing = true;

        /// <summary>
        /// Explicit VolumeProfile used when post-processing is enabled. Stored
        /// as UnityEngine.Object because the package asmdef cannot reference
        /// SRP Core types; validated at capture time. Null = auto.
        /// </summary>
        [SerializeField]
        private UnityEngine.Object m_PostProcessingProfile;

        [SerializeField]
        private bool m_CaptureUi = true;

        [SerializeField]
        private float m_UiScale = 1f;

        [SerializeField]
        private int m_LayerMask = -1;

        [SerializeField]
        private LightingMode m_LightingMode = LightingMode.None;

        [SerializeField]
        private float m_Light2DIntensity = 1f;

        /// <summary>
        /// Bitmask of sorting layer indices the temporary global Light2D should
        /// affect. Each bit corresponds to an index in SortingLayer.layers.
        /// -1 (all bits set) means all sorting layers.
        /// </summary>
        [SerializeField]
        private int m_Light2DSortingLayers = -1;

        [SerializeField]
        private float m_Light3DIntensity = 1f;

        /// <summary>
        /// Shared prefab-scoped Light3D intensity driving both the None-mode
        /// TempLight3D fallback and the explicit-Light3D prefab branch. Default
        /// 1.75 adds ~75 percent key energy over the scene 1.0 to offset the
        /// single-key plus flat-grey-ambient prefab path; scene value untouched.
        /// </summary>
        [SerializeField]
        private float m_Light3DPrefabIntensity = 1.75f;

        [SerializeField]
        private float m_Light3DIntensityMax = 5f;

        [SerializeField]
        private bool m_Light3DShadows;

        [SerializeField]
        private Color m_Light3DColor = Color.white;

        [SerializeField]
        private float m_Light3DYaw = 50f;

        [SerializeField]
        private float m_Light3DYawMin = -180f;

        [SerializeField]
        private float m_Light3DYawMax = 180f;

        [SerializeField]
        private float m_Light3DPitch = -30f;

        [SerializeField]
        private float m_Light3DPitchMin = -89f;

        [SerializeField]
        private float m_Light3DPitchMax = 89f;

        [SerializeField]
        private bool m_IconOverlayEnabled = true;

        /// <summary>
        /// Per-axis icon overlay padding for Project window list rows. Legacy
        /// assets serialized a single m_IconPadding; FormerlySerializedAs maps
        /// that value into both fields on first deserialization.
        /// </summary>
        [FormerlySerializedAs("m_IconPadding")]
        [SerializeField]
        private float m_ListPaddingX = 2f;

        [FormerlySerializedAs("m_IconPadding")]
        [SerializeField]
        private float m_ListPaddingY = 2f;

        [SerializeField]
        private float m_IconMaxSize = 128f;

        [SerializeField]
        private int m_CacheSizeMb = 256;

        [SerializeField]
        private float m_ParticlePreviewTime = 1f;

        [SerializeField]
        private bool m_ParticlePreviewTimeMigrated = false;

        #endregion

        #region Properties

        /// <summary>
        /// Current storage mode. LibraryCache is the default and the existing
        /// behavior; TrackedInAssets is opt-in via SetStorageMode.
        /// </summary>
        public StorageMode StorageMode => m_StorageMode;

        /// <summary>
        /// When true, UniThumb automatically regenerates scene and prefab
        /// thumbnails in the background after every save, but only while
        /// the window is open. Off by default.
        /// </summary>
        public bool AutoRegenerateOnSave => m_AutoRegenerateOnSave;

        /// <summary>
        /// When true, the opt-in update check may run via Tools > UniThumb >
        /// Check for Updates. Off by default (no automatic network access).
        /// Backs UniThumbUpdateChecker.UpdateCheckEnabled; existing assets
        /// without this field deserialize as false, matching the old default.
        /// </summary>
        public bool CheckForUpdates => m_CheckForUpdates;

        /// <summary>
        /// When true, batch generation may switch scenes and silently discard
        /// unsaved changes instead of refusing to start. Off by default
        /// (abort-on-dirty); existing assets without this field deserialize
        /// as false, matching the old default. Backs
        /// UniThumbBatchMenus.AllowDiscardUnsavedChanges; legacy EditorPrefs
        /// opt-ins migrate once on first read.
        /// </summary>
        public bool AllowBatchDiscard => m_AllowBatchDiscard;

        /// <summary>
        /// True after capture settings have been written to this asset at least
        /// once. When false, the window uses its own defaults on first open
        /// (existing asset migration path).
        /// </summary>
        public bool CaptureSettingsInitialized => m_CaptureSettingsInitialized;

        /// <summary>Resolution preset index (0-7 mapping to 16/32/64/128/256/512/1024/2048).</summary>
        public int ResolutionIndex => m_ResolutionIndex;

        /// <summary>Use the Scene View camera angle for capture framing.</summary>
        public bool UseSceneViewAngle => m_UseSceneViewAngle;

        /// <summary>Capture in orthographic (2D) projection.</summary>
        public bool Orthographic2D => m_Orthographic2D;

        /// <summary>Orbit yaw angle in degrees.</summary>
        public float OrbitYaw => m_OrbitYaw;

        /// <summary>Orbit pitch angle in degrees.</summary>
        public float OrbitPitch => m_OrbitPitch;

        /// <summary>Multiplier of the auto-fit orbit framing distance.</summary>
        public float OrbitDistanceMultiplier => m_OrbitDistanceMultiplier;

        /// <summary>Field of view for orbit-mode capture (degrees).</summary>
        public float OrbitFov => m_OrbitFov;

        /// <summary>Fit factor for orbit framing.</summary>
        public float FitFactor => m_FitFactor;

        /// <summary>Override scene lighting during capture.</summary>
        public bool UseLightingOverride => m_UseLightingOverride;

        /// <summary>Background color for SolidColor mode.</summary>
        public Color BackgroundColor => m_BackgroundColor;

        /// <summary>Background clear mode (Skybox, SolidColor, Transparent).</summary>
        public BackgroundMode BackgroundMode => m_BackgroundMode;

        /// <summary>Enable post-processing during capture.</summary>
        public bool WantPostProcessing => m_WantPostProcessing;

        /// <summary>
        /// Explicit VolumeProfile used when post-processing is enabled;
        /// null means auto. Not typed as VolumeProfile because the package
        /// asmdef cannot reference SRP Core types.
        /// </summary>
        public UnityEngine.Object PostProcessingProfile => m_PostProcessingProfile;

        /// <summary>
        /// Capture UI canvases into the thumbnail. Shared by the scene and
        /// prefab paths (no prefab-specific flag, no migration): default
        /// true renders prefab UI by default; false suppresses prefab UI
        /// with pixel parity to the old behavior.
        /// </summary>
        public bool CaptureUi => m_CaptureUi;

        /// <summary>UI zoom multiplier.</summary>
        public float UiScale => m_UiScale;

        /// <summary>Layer mask for renderers included in capture.</summary>
        public int LayerMask => m_LayerMask;

        /// <summary>Lighting mode for capture (None or Light2D).</summary>
        public LightingMode LightingMode => m_LightingMode;

        /// <summary>Intensity of the temporary Light2D (0-5).</summary>
        public float Light2DIntensity => m_Light2DIntensity;

        /// <summary>Bitmask of sorting layer indices for the temporary Light2D.</summary>
        public int Light2DSortingLayers => m_Light2DSortingLayers;

        /// <summary>Intensity of the temporary Directional Light (0-Max).</summary>
        public float Light3DIntensity => m_Light3DIntensity;

        /// <summary>Shared prefab-scoped Directional Light intensity (0-Max).</summary>
        public float Light3DPrefabIntensity => m_Light3DPrefabIntensity;

        /// <summary>Fixed minimum for the temporary Directional Light intensity.</summary>
        public float Light3DIntensityMin => k_Light3DIntensityMin;

        /// <summary>Customizable maximum for the temporary Directional Light intensity.</summary>
        public float Light3DIntensityMax => m_Light3DIntensityMax;

        /// <summary>
        /// Absolute hard cap for the temporary Directional Light intensity max
        /// (fixed 10). Capture clamps to [0, cap] before pipeline scaling so
        /// menu and batch paths stay bounded even when they bypass the window.
        /// </summary>
        internal static float Light3DIntensityHardCap => k_Light3DIntensityMaxCap;

        /// <summary>Whether the temporary Directional Light casts shadows.</summary>
        public bool Light3DShadows => m_Light3DShadows;

        /// <summary>Color of the temporary Directional Light.</summary>
        public Color Light3DColor => m_Light3DColor;

        /// <summary>Yaw (horizontal rotation) of the temporary Directional Light.</summary>
        public float Light3DYaw => m_Light3DYaw;

        /// <summary>Customizable minimum yaw for the temporary Directional Light.</summary>
        public float Light3DYawMin => m_Light3DYawMin;

        /// <summary>Customizable maximum yaw for the temporary Directional Light.</summary>
        public float Light3DYawMax => m_Light3DYawMax;

        /// <summary>Pitch (vertical rotation) of the temporary Directional Light.</summary>
        public float Light3DPitch => m_Light3DPitch;

        /// <summary>Customizable minimum pitch for the temporary Directional Light.</summary>
        public float Light3DPitchMin => m_Light3DPitchMin;

        /// <summary>Customizable maximum pitch for the temporary Directional Light.</summary>
        public float Light3DPitchMax => m_Light3DPitchMax;

        /// <summary>Whether the Project window icon overlay is enabled.</summary>
        public bool IconOverlayEnabled => m_IconOverlayEnabled;

        /// <summary>Horizontal padding around list-view icons in pixels.</summary>
        public float ListPaddingX => m_ListPaddingX;

        /// <summary>Vertical padding around list-view icons in pixels.</summary>
        public float ListPaddingY => m_ListPaddingY;

        /// <summary>Maximum size of the icon overlay in pixels.</summary>
        public float IconMaxSize => m_IconMaxSize;

        /// <summary>Maximum cache size in megabytes.</summary>
        public int CacheSizeMb => m_CacheSizeMb;

        /// <summary>Particle/VFX preview time in seconds (0-5).</summary>
        public float ParticlePreviewTime => m_ParticlePreviewTime;

        /// <summary>Fixed minimum for the particle/VFX preview time.</summary>
        public float ParticlePreviewTimeMin => k_ParticlePreviewTimeMin;

        /// <summary>Fixed maximum for the particle/VFX preview time.</summary>
        public float ParticlePreviewTimeMax => k_ParticlePreviewTimeMax;

        #endregion

        #region Public Methods

        /// <summary>
        /// Loads the settings asset from disk, creating it (and its folder) on first
        /// access. Load-at-call-time only: no permanent static cache, so the returned
        /// value always reflects the on-disk asset and never goes stale across domain
        /// reloads or after the window changes it. Idempotent - repeat calls return
        /// the same asset; the only side effect is the one-time legacy icon-size
        /// range migration (TryMigrateIconSizeRange) on pre-migration assets.
        /// </summary>
        public static UniThumbSettings Get()
        {
            UniThumbSettings settings = AssetDatabase.LoadAssetAtPath<UniThumbSettings>(
                k_AssetPath
            );
            if (settings != null)
            {
                settings.TryMigrateIconSizeRange();
                settings.TryNormalizeLightLimits();
                settings.TryNormalizeParticlePreviewTime();
                return settings;
            }

            EnsureAssetFolder();
            settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            try
            {
                AssetDatabase.CreateAsset(settings, k_AssetPath);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    "[UniThumb] Could not persist settings asset ("
                        + exception.Message
                        + "); using in-memory defaults."
                );
            }
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

        /// <summary>
        /// Enables or disables auto-regeneration on save and marks the asset
        /// dirty so the change persists across domain reloads.
        /// </summary>
        public void SetAutoRegenerateOnSave(bool value)
        {
            m_AutoRegenerateOnSave = value;
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Enables or disables the opt-in update check and marks the asset
        /// dirty so the change persists across domain reloads.
        /// </summary>
        public void SetCheckForUpdates(bool value)
        {
            m_CheckForUpdates = value;
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Enables or disables silent discard of unsaved scene changes during
        /// batch generation and marks the asset dirty so the change persists
        /// across domain reloads.
        /// </summary>
        public void SetAllowBatchDiscard(bool value)
        {
            m_AllowBatchDiscard = value;
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Enables or disables the Project window icon overlay and marks the
        /// asset dirty so the change persists across domain reloads.
        /// </summary>
        public void SetIconOverlayEnabled(bool value)
        {
            m_IconOverlayEnabled = value;
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Sets a per-axis icon overlay padding (clamped to 0..8) and marks the
        /// asset dirty so the change persists across domain reloads.
        /// </summary>
        public void SetListPaddingX(float value)
        {
            m_ListPaddingX = Mathf.Clamp(value, 0f, 8f);
            EditorUtility.SetDirty(this);
        }

        /// <inheritdoc cref="SetListPaddingX"/>
        public void SetListPaddingY(float value)
        {
            m_ListPaddingY = Mathf.Clamp(value, 0f, 8f);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Sets the icon overlay maximum size in pixels (clamped to 16..256) and
        /// marks the asset dirty so the change persists across domain reloads.
        /// </summary>
        public void SetIconMaxSize(float value)
        {
            m_IconMaxSize = Mathf.Clamp(value, 16f, 256f);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// One-time migration for the icon max size semantics change. Assets
        /// saved before the change hold values in the old 8..64 range that were
        /// used only as a list/tile threshold, never as a drawn-size cap; under
        /// the new cap semantics they would collapse tile thumbnails to tiny
        /// icons. Maps any old-range value to the new default (128, effectively
        /// uncapped for typical tile sizes) exactly once.
        /// </summary>
        private void TryMigrateIconSizeRange()
        {
            if (m_IconSizeRangeMigrated)
            {
                return;
            }
            m_IconSizeRangeMigrated = true;
            if (m_IconMaxSize <= 64f)
            {
                m_IconMaxSize = 128f;
                EditorUtility.SetDirty(this);
            }
        }

        /// <summary>
        /// Normalizes Light3D limit fields on legacy assets that predate them
        /// (missing fields deserialize as 0) and clamps current values into the
        /// active ranges. Intensity min is fixed at 0; a non-positive or
        /// over-cap max resets to the default or cap. Yaw/pitch ranges with
        /// min greater than or equal to max reset to defaults. Marks dirty only
        /// when a change was made.
        /// </summary>
        private void TryNormalizeLightLimits()
        {
            bool changed = false;
            if (m_Light3DIntensityMax <= 0f)
            {
                m_Light3DIntensityMax = k_Light3DIntensityDefaultMax;
                changed = true;
            }
            else if (m_Light3DIntensityMax > k_Light3DIntensityMaxCap)
            {
                m_Light3DIntensityMax = k_Light3DIntensityMaxCap;
                changed = true;
            }
            if (m_Light3DYawMin >= m_Light3DYawMax)
            {
                m_Light3DYawMin = k_Light3DYawDefaultMin;
                m_Light3DYawMax = k_Light3DYawDefaultMax;
                changed = true;
            }
            if (m_Light3DPitchMin >= m_Light3DPitchMax)
            {
                m_Light3DPitchMin = k_Light3DPitchDefaultMin;
                m_Light3DPitchMax = k_Light3DPitchDefaultMax;
                changed = true;
            }
            float clampedIntensity = Mathf.Clamp(
                m_Light3DIntensity,
                k_Light3DIntensityMin,
                m_Light3DIntensityMax
            );
            if (clampedIntensity != m_Light3DIntensity)
            {
                m_Light3DIntensity = clampedIntensity;
                changed = true;
            }
            if (m_Light3DPrefabIntensity == 0f)
            {
                m_Light3DPrefabIntensity = k_Light3DPrefabIntensityDefault;
                changed = true;
            }
            float clampedPrefabIntensity = Mathf.Clamp(
                m_Light3DPrefabIntensity,
                k_Light3DIntensityMin,
                m_Light3DIntensityMax
            );
            if (clampedPrefabIntensity != m_Light3DPrefabIntensity)
            {
                m_Light3DPrefabIntensity = clampedPrefabIntensity;
                changed = true;
            }
            float clampedYaw = Mathf.Clamp(m_Light3DYaw, m_Light3DYawMin, m_Light3DYawMax);
            if (clampedYaw != m_Light3DYaw)
            {
                m_Light3DYaw = clampedYaw;
                changed = true;
            }
            float clampedPitch = Mathf.Clamp(m_Light3DPitch, m_Light3DPitchMin, m_Light3DPitchMax);
            if (clampedPitch != m_Light3DPitch)
            {
                m_Light3DPitch = clampedPitch;
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(this);
            }
        }

        /// <summary>
        /// One-time migration for the particle preview time. Assets saved
        /// before the field existed deserialize as 0; maps that legacy 0 to
        /// the default (1.0) exactly once so an explicit user 0 afterwards
        /// survives. Clamps into 0..5 either way. Marks dirty only on change.
        /// </summary>
        private void TryNormalizeParticlePreviewTime()
        {
            bool changed = false;
            if (!m_ParticlePreviewTimeMigrated)
            {
                m_ParticlePreviewTimeMigrated = true;
                if (m_ParticlePreviewTime == 0f)
                {
                    m_ParticlePreviewTime = k_ParticlePreviewTimeDefault;
                    changed = true;
                }
            }
            float clamped = Mathf.Clamp(
                m_ParticlePreviewTime,
                k_ParticlePreviewTimeMin,
                k_ParticlePreviewTimeMax
            );
            if (clamped != m_ParticlePreviewTime)
            {
                m_ParticlePreviewTime = clamped;
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(this);
            }
        }

        /// <summary>
        /// Sets the maximum cache size in megabytes (clamped to 32..2048) and
        /// marks the asset dirty so the change persists across domain reloads.
        /// </summary>
        public void SetCacheSizeMb(int value)
        {
            m_CacheSizeMb = Mathf.Clamp(value, 32, 2048);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Sets the particle/VFX preview time in seconds (clamped to 0..5)
        /// and marks the asset dirty so the change persists across reloads.
        /// </summary>
        public void SetParticlePreviewTime(float value)
        {
            m_ParticlePreviewTime = Mathf.Clamp(
                value,
                k_ParticlePreviewTimeMin,
                k_ParticlePreviewTimeMax
            );
            EditorUtility.SetDirty(this);
        }

        #region Capture Settings Setters

        public void SetResolutionIndex(int value)
        {
            m_ResolutionIndex = value;
            EditorUtility.SetDirty(this);
        }

        public void SetUseSceneViewAngle(bool value)
        {
            m_UseSceneViewAngle = value;
            EditorUtility.SetDirty(this);
        }

        public void SetOrthographic2D(bool value)
        {
            m_Orthographic2D = value;
            EditorUtility.SetDirty(this);
        }

        public void SetOrbitYaw(float value)
        {
            m_OrbitYaw = value;
            EditorUtility.SetDirty(this);
        }

        public void SetOrbitPitch(float value)
        {
            m_OrbitPitch = value;
            EditorUtility.SetDirty(this);
        }

        public void SetOrbitDistanceMultiplier(float value)
        {
            m_OrbitDistanceMultiplier = value;
            EditorUtility.SetDirty(this);
        }

        public void SetOrbitFov(float value)
        {
            m_OrbitFov = value;
            EditorUtility.SetDirty(this);
        }

        public void SetFitFactor(float value)
        {
            m_FitFactor = value;
            EditorUtility.SetDirty(this);
        }

        public void SetUseLightingOverride(bool value)
        {
            m_UseLightingOverride = value;
            EditorUtility.SetDirty(this);
        }

        public void SetBackgroundColor(Color value)
        {
            m_BackgroundColor = value;
            EditorUtility.SetDirty(this);
        }

        public void SetBackgroundMode(BackgroundMode value)
        {
            m_BackgroundMode = value;
            EditorUtility.SetDirty(this);
        }

        public void SetWantPostProcessing(bool value)
        {
            m_WantPostProcessing = value;
            EditorUtility.SetDirty(this);
        }

        public void SetPostProcessingProfile(UnityEngine.Object value)
        {
            m_PostProcessingProfile = value;
            EditorUtility.SetDirty(this);
        }

        public void SetCaptureUi(bool value)
        {
            m_CaptureUi = value;
            EditorUtility.SetDirty(this);
        }

        public void SetUiScale(float value)
        {
            m_UiScale = Mathf.Clamp(value, 0.25f, 4f);
            EditorUtility.SetDirty(this);
        }

        public void SetLayerMask(int value)
        {
            m_LayerMask = value;
            EditorUtility.SetDirty(this);
        }

        public void SetLightingMode(LightingMode value)
        {
            m_LightingMode = value;
            EditorUtility.SetDirty(this);
        }

        public void SetLight2DIntensity(float value)
        {
            m_Light2DIntensity = Mathf.Clamp(value, 0f, 5f);
            EditorUtility.SetDirty(this);
        }

        public void SetLight2DSortingLayers(int value)
        {
            m_Light2DSortingLayers = value;
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DIntensity(float value)
        {
            m_Light3DIntensity = Mathf.Clamp(value, k_Light3DIntensityMin, m_Light3DIntensityMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DPrefabIntensity(float value)
        {
            m_Light3DPrefabIntensity = Mathf.Clamp(
                value,
                k_Light3DIntensityMin,
                m_Light3DIntensityMax
            );
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DIntensityMax(float value)
        {
            m_Light3DIntensityMax = Mathf.Clamp(
                value,
                k_LightLimitEpsilon,
                k_Light3DIntensityMaxCap
            );
            m_Light3DIntensity = Mathf.Clamp(
                m_Light3DIntensity,
                k_Light3DIntensityMin,
                m_Light3DIntensityMax
            );
            m_Light3DPrefabIntensity = Mathf.Clamp(
                m_Light3DPrefabIntensity,
                k_Light3DIntensityMin,
                m_Light3DIntensityMax
            );
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DShadows(bool value)
        {
            m_Light3DShadows = value;
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DColor(Color value)
        {
            m_Light3DColor = value;
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DYaw(float value)
        {
            m_Light3DYaw = Mathf.Clamp(value, m_Light3DYawMin, m_Light3DYawMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DYawMin(float value)
        {
            m_Light3DYawMin = Mathf.Min(value, m_Light3DYawMax - k_LightLimitEpsilon);
            m_Light3DYaw = Mathf.Clamp(m_Light3DYaw, m_Light3DYawMin, m_Light3DYawMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DYawMax(float value)
        {
            m_Light3DYawMax = Mathf.Max(value, m_Light3DYawMin + k_LightLimitEpsilon);
            m_Light3DYaw = Mathf.Clamp(m_Light3DYaw, m_Light3DYawMin, m_Light3DYawMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DPitch(float value)
        {
            m_Light3DPitch = Mathf.Clamp(value, m_Light3DPitchMin, m_Light3DPitchMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DPitchMin(float value)
        {
            m_Light3DPitchMin = Mathf.Min(value, m_Light3DPitchMax - k_LightLimitEpsilon);
            m_Light3DPitch = Mathf.Clamp(m_Light3DPitch, m_Light3DPitchMin, m_Light3DPitchMax);
            EditorUtility.SetDirty(this);
        }

        public void SetLight3DPitchMax(float value)
        {
            m_Light3DPitchMax = Mathf.Max(value, m_Light3DPitchMin + k_LightLimitEpsilon);
            m_Light3DPitch = Mathf.Clamp(m_Light3DPitch, m_Light3DPitchMin, m_Light3DPitchMax);
            EditorUtility.SetDirty(this);
        }

        #endregion

        /// <summary>
        /// Saves all capture settings from a UniThumbWindow instance into this
        /// settings asset. Sets the initialized flag so subsequent opens load
        /// from the asset instead of using EditorWindow defaults.
        /// </summary>
        public void SaveCaptureSettings(UniThumbWindow window)
        {
            window.PersistCaptureSettings(this);
            m_CaptureSettingsInitialized = true;
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Loads saved capture settings into a UniThumbWindow instance. Only
        /// applies when m_CaptureSettingsInitialized is true (i.e. after at
        /// least one save). Returns false when no saved settings exist yet.
        /// </summary>
        public bool LoadCaptureSettings(UniThumbWindow window)
        {
            if (!m_CaptureSettingsInitialized)
            {
                return false;
            }
            window.ApplyPersistedCaptureSettings(this);
            return true;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates Assets/UniThumb/ when missing. Directory.CreateDirectory on the
        /// absolute path is used instead of AssetDatabase.CreateFolder because the
        /// latter fails when the folder exists on disk but is not yet imported; a
        /// Refresh afterwards registers it with the asset pipeline. When the
        /// post-import validity check fails, the final verdict is deferred to
        /// EditorApplication.delayCall to handle the compile/refresh race where
        /// ForceSynchronousImport does not finish registering before the check.
        /// </summary>
        private static void EnsureAssetFolder()
        {
            if (AssetDatabase.IsValidFolder(k_AssetFolder))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "UniThumb"));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (!AssetDatabase.IsValidFolder(k_AssetFolder))
            {
                EditorApplication.delayCall += DeferredFolderCheck;
            }
        }

        private static void DeferredFolderCheck()
        {
            if (!AssetDatabase.IsValidFolder(k_AssetFolder))
            {
                Debug.LogWarning(
                    "[UniThumb] Could not register Assets/UniThumb folder; settings may not persist."
                );
            }
        }

        #endregion
    }
}
