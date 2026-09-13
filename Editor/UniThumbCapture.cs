using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;
using UnityEngine.UI;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// What the capture camera clears to before rendering the scene.
    /// </summary>
    public enum BackgroundMode
    {
        Skybox,
        SolidColor,
        Transparent,
    }

    public enum LightingMode
    {
        None,
        Light2D,
        Light3D,
    }

    /// <summary>
    /// Prefab-only lighting fallback decision. Scene captures always use
    /// None; prefab captures with Light2DMode None get a temp light so the
    /// preview and PNG are lit even in empty scenes or under the lighting
    /// override (which disables every scene light and adds nothing).
    /// </summary>
    public enum PrefabFallbackLight
    {
        None,
        TempLight2D,
        TempLight3D,
    }

    /// <summary>
    /// Settings for a single scene thumbnail capture.
    /// </summary>
    public struct CaptureSettings
    {
        public int Width;
        public int Height;
        public Color BackgroundColor;
        public bool UseSceneViewAngle;

        /// <summary>
        /// True captures with an orthographic (2D) projection. In SceneView-angle
        /// mode the SceneView camera's orthographicSize is copied when the
        /// SceneView camera is orthographic; otherwise (and in orbit mode) the
        /// orthographicSize is derived from the fitted bounds and
        /// orbitDistanceMultiplier.
        /// </summary>
        public bool orthographic;
        public float OrbitYaw;
        public float OrbitPitch;
        public float FitFactor;

        /// <summary>
        /// Multiplier of the auto-fit orbit framing distance. Only used when
        /// UseSceneViewAngle is false (orbit mode); clamped to 0.1..10 on capture.
        /// </summary>
        public float orbitDistanceMultiplier;

        /// <summary>
        /// Field of view for orbit-mode capture (degrees). Controls perspective
        /// projection FOV when UseSceneViewAngle is false (orbit mode); clamped
        /// to 30..120 on capture.
        /// </summary>
        public float OrbitFov;
        public bool UseLightingOverride;
        public LayerMask layerMask;
        public BackgroundMode BackgroundMode;
        public bool WantPostProcessing;

        /// <summary>
        /// Explicit VolumeProfile for post-processing. Wins over scene Volumes
        /// when set and actually is a VolumeProfile; null means auto (scene
        /// Volume, else the first project profile with visible effects).
        /// Stored as UnityEngine.Object because the package asmdef cannot
        /// reference SRP Core types; validated at capture time.
        /// </summary>
        public UnityEngine.Object PostProcessingProfile;

        /// <summary>
        /// Capture UI canvases into the thumbnail. Shared by the scene and
        /// prefab paths (no prefab-specific flag): default true renders
        /// prefab UI by default; false culls UI and reproduces the baseline
        /// (pre-UI) prefab pixels. Flows from the window toggle via
        /// BuildSettings and persists via UniThumbSettings.
        /// </summary>
        public bool CaptureUi;

        /// <summary>
        /// UI zoom multiplier. In composite mode (CaptureUi with
        /// UseSceneViewAngle) it is applied as a center-kept sampling zoom in
        /// CompositeSceneViewUi: the canvas keeps scaleFactor 1 (full layout
        /// renders, no canvas-rect culling) and the UI band is scaled in the
        /// blend loop. In legacy square mode it overrides canvas.scaleFactor
        /// while the UI capture session is active (CanvasScaler disabled for the
        /// pass so it cannot re-apply its own scale). 1f means no override (zero
        /// mutation, byte-identical output).
        /// </summary>
        public float UiScale;
        public LightingMode Light2DMode;
        public float Light2DIntensity;

        /// <summary>
        /// Sorting layer IDs the temporary global Light2D should affect.
        /// Null or empty means all sorting layers (the Light2D default).
        /// </summary>
        public int[] Light2DSortingLayerIds;

        public float Light3DIntensity;

        /// <summary>
        /// Shared prefab-scoped Light3D intensity for the None-fallback
        /// TempLight3D plus explicit-Light3D prefab paths. Scene path keeps
        /// Light3DIntensity. Default 1.75 per diagnosis default-1.75.
        /// </summary>
        public float Light3DPrefabIntensity;
        public bool Light3DShadows;
        public Color Light3DColor;
        public float Light3DYaw;
        public float Light3DPitch;
        public float Light3DYawMin;
        public float Light3DYawMax;
        public float Light3DPitchMin;
        public float Light3DPitchMax;

        /// <summary>
        /// Particle/VFX preview time in seconds (0-5). Shared by ParticleSystem
        /// pre-roll and VFX Graph pre-roll; the thumbnail uses this preview value.
        /// </summary>
        public float ParticlePreviewTime;
    }

    /// <summary>
    /// Result of a scene thumbnail capture.
    /// </summary>
    public struct CaptureResult
    {
        public bool Success;
        public byte[] PngBytes;
        public string Warning;
    }

    /// <summary>
    /// Reusable pure C# API that renders the active scene to a PNG thumbnail.
    /// Never renders through SceneView.camera: a dedicated temp GameObject + Camera is used.
    /// </summary>
    public static class UniThumbCapture
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";
        private const string k_TempCameraName = "__UniThumbCaptureCamera";
        private const string k_TempRenderTextureName = "__UniThumbCaptureRT";
        private const int k_MinResolution = 16;
        private const int k_MaxResolution = 4096;
        private const int k_MaxReliableRenderPixels = 16000000;

        // Fallback-only estimate of the SceneView toolbar strip (px) subtracted
        // from sv.position when sv.camera.pixelWidth/pixelHeight report no
        // viewport (0/1 before the first render).
        private const float k_SceneViewToolbarHeight = 24f;
        private const float k_DefaultOrbitFov = 60f;
        private const float k_DefaultFitFactor = 2f;
        private const float k_MaxOrbitPitch = 89f;
        private const float k_MinFarClip = 1000f;
        private const string k_TempUiPassCameraName = "__UniThumbUiPassCamera";

        /// <summary>
        /// Multiplier applied to the Light3D UI intensity (0-5) when the
        /// project uses HDRP. HDRP directional lights use physical lux where
        /// sun ~100k lux; the URP/Built-in scale (0-5) is invisible without
        /// rescaling.
        /// </summary>
        private const float k_HdrpLight3DIntensityScale = 20000f;

        /// <summary>
        /// Time window (seconds) for the VolumeProfile guid cache. One batch
        /// pump collects then captures many scenes back-to-back, so a short
        /// window bounds FindAssets("t:VolumeProfile") to ~1x per batch while
        /// staying fresh across sessions. See GetVolumeProfileGuidsCached.
        /// </summary>
        private const double k_VolumeProfileCacheSeconds = 30.0;

        /// <summary>
        /// Fixed EV value used when adding an HDRP Exposure component to
        /// baseline volumes.  Exposed as internal static so it can be
        /// tweaked from UniThumbWindow or via ExecuteCode without a recompile.
        /// </summary>
        internal static float HdrpFixedExposure = 10f;

        /// <summary>
        /// Flat ambient color used while a prefab is isolated. Scene ambient
        /// (skybox/gradient/trilight) is an environment contribution, so the
        /// prefab path swaps it for a fixed mid-grey and restores it after.
        /// </summary>
        private static readonly Color k_PrefabNeutralAmbient = new Color(0.5f, 0.5f, 0.5f, 1f);

        /// <summary>
        /// Light3D default orientation used by the None-mode 3D fallback.
        /// Matches UniThumbSettings defaults (yaw 50, pitch -30). The
        /// fallback carries no user lighting intent, so it keys off these
        /// defaults clamped to the active limits instead of the current
        /// user yaw/pitch values. Explicit Light3D mode keeps user intent.
        /// </summary>
        internal const float k_Light3DDefaultYaw = 50f;

        internal const float k_Light3DDefaultPitch = -30f;

        internal const float k_Light3DYawDefaultMin = -180f;

        internal const float k_Light3DYawDefaultMax = 180f;

        internal const float k_Light3DPitchDefaultMin = -89f;

        internal const float k_Light3DPitchDefaultMax = 89f;

        /// <summary>
        /// Fixed above-horizon key-light pitch (degrees) for the prefab
        /// fallback TempLight3D last resort (see
        /// ResolveNoneFallbackOrientation): Skybox mode with a Procedural
        /// skybox assigned and a below-horizon clamped default pitch.
        /// Every other case uses the Light3D defaults. Explicit Light3D
        /// mode keeps the user rotation (intent) and is unaffected.
        /// </summary>
        private const float k_PrefabFallbackKeyPitch = 50f;

        /// <summary>
        /// Floor for the RT-proportional prefab UI scale on the Camera
        /// path. At tiny thumbnails (e.g. 128px vs a 1920 reference) the raw
        /// ratio (~0.07) would shrink UI to sub-pixel strokes; flooring at
        /// 0.25 keeps strokes rasterized while larger sizes keep the exact
        /// proportional mapping. Overlay-converted canvases never use this:
        /// they take the raw RT base with no floor (resolution wave 2 fix),
        /// because the floor is what broke proportionality (floored 0.25@128
        /// vs raw 0.16, 0.4578 vs 0.1875 frame-fraction; floored overflow at
        /// 16 reads uniform and fails the capture).
        internal const float k_PrefabRtScaleMin = 0.25f;

        #endregion

        #region Fields

        /// <summary>
        /// Mutable capture bookkeeping on a readonly holder so the class keeps
        /// zero static mutable fields (Asset Store Validator
        /// "Check Static Variables"). Domain-reload semantics unchanged: a fresh
        /// holder is created on reload, exactly like the old statics.
        /// </summary>
        private static readonly CaptureState s_State = new CaptureState();

        /// <summary>
        /// Controls whether mutation helpers (SwitchOverlayCanvases,
        /// DisableAllLights) call Undo.RecordObject. The live-preview path
        /// sets this to false because it restores values in finally blocks
        /// directly and never needs undo registration. Defaults to true
        /// (CaptureCore keeps it enabled).
        /// </summary>
        internal static bool RecordUndo
        {
            get => s_State.RecordUndo;
            set => s_State.RecordUndo = value;
        }

        /// <summary>
        /// Rollback bypass for the perf wave-2 scan caches (object sweeps and
        /// the VolumeProfile guid cache). False (default) uses the shared
        /// per-capture snapshot; true restores the legacy per-helper native
        /// sweeps and fresh FindAssets calls. Revert note: setting this true
        /// is the supported rollback when a sweep-scope regression is
        /// suspected; no other change is needed.
        /// </summary>
        internal static bool DisableScanCache
        {
            get { return s_State.DisableScanCache; }
            set { s_State.DisableScanCache = value; }
        }

        /// <summary>
        /// Native sweep count used by the most recent BeginScan (5:
        /// renderer, light, component, canvas-renderer, plus the per-capture
        /// UI Canvas sweep). Logged once per
        /// session; tests assert the per-capture budget.
        /// </summary>
        internal static int LastScanSweepCount
        {
            get { return s_State.LastScanSweepCount; }
        }

        /// <summary>
        /// Object counts from the most recent BeginScan, one per snapshot
        /// array. Read these when a prefab preview leaks scene content: a
        /// zero or short count names the missed sweep (scan-miss) without
        /// re-running the native queries.
        /// </summary>
        internal static int LastScanRendererCount
        {
            get { return s_State.LastScanRendererCount; }
        }

        /// <summary>
        /// Light count from the most recent BeginScan (see
        /// LastScanRendererCount).
        /// </summary>
        internal static int LastScanLightCount
        {
            get { return s_State.LastScanLightCount; }
        }

        /// <summary>
        /// Component count from the most recent BeginScan (see
        /// LastScanRendererCount).
        /// </summary>
        internal static int LastScanComponentCount
        {
            get { return s_State.LastScanComponentCount; }
        }

        /// <summary>
        /// Canvas-renderer count from the most recent BeginScan (see
        /// LastScanRendererCount).
        /// </summary>
        internal static int LastScanCanvasRendererCount
        {
            get { return s_State.LastScanCanvasRendererCount; }
        }

        private sealed class CaptureState
        {
            public bool ConversionLogged;

            // Settings snapshot remembered via RememberSettings so menu/batch capture
            // paths can reuse the window's configured settings instead of hardcoded
            // defaults. Null until the window warms it; struct value semantics make
            // the stored copy immune to later mutation of the caller's instance.
            public CaptureSettings? LastSettings;

            // When true (default), mutation helpers (SwitchOverlayCanvases,
            // DisableAllLights) call Undo.RecordObject so CaptureCore's
            // RevertAllDownToGroup can undo every capture mutation. The
            // live-preview path sets this to false: it restores values in
            // finally blocks directly and never needs undo registration,
            // preventing spurious dirty marks and undo-stack accumulation.
            public bool RecordUndo = true;

            // Test hook: forces the result of HasPostProcessingAvailable().
            // Null (default) uses the real scene/project checks.
            public bool? PostProcessingAvailableOverride;

            // Test hook: forces the result of PipelineHasOnly2DRenderers().
            // Null (default) uses the real pipeline asset checks.
            public bool? Renderer2DOnlyOverride;

            // Rollback bypass for the scan caches (see DisableScanCache).
            public bool DisableScanCache;

            // VolumeProfile guid cache (see GetVolumeProfileGuidsCached):
            // last query result plus the EditorApplication.timeSinceStartup
            // tick it was taken at. Null means cold (query on next use).
            public string[] CachedVolumeProfileGuids;
            public double CachedVolumeProfileTick;

            // Sweep accounting for the per-capture snapshot.
            public int LastScanSweepCount;
            public bool ScanCountLogged;

            // Per-type object counts from the most recent BeginScan
            // (diagnoses scan-miss isolation leaks: a scene renderer
            // missing from the snapshot shows up here as a short count).
            public int LastScanRendererCount;
            public int LastScanLightCount;
            public int LastScanComponentCount;
            public int LastScanCanvasRendererCount;

            // Warn-once latch for prefab VFX pre-roll failures.
            public bool VfxPreRollWarned;

            // Once-per-session latch for VFX pre-roll environment skips
            // (Built-in RP, missing compute). First skip logs once,
            // later skips stay silent so batch captures never spam.
            public bool VfxSkipLogged;

            // Cached VisualEffect reflection. Resolved once per session
            // (fresh holder per domain reload), then reused with zero
            // further assembly scans or GetMethods enumeration.
            public bool VfxTypeResolved;
            public bool VfxReflectionResolved;
            public System.Type VfxType;
            public System.Reflection.PropertyInfo VfxSeedProperty;
            public System.Reflection.PropertyInfo VfxPauseProperty;
            public System.Reflection.MethodInfo VfxReinitMethod;
            public System.Reflection.MethodInfo VfxSimulateMethod;
        }

        #endregion

        #region Public Methods

        public static CaptureSettings CreateDefaultSettings()
        {
            return new CaptureSettings
            {
                Width = 512,
                Height = 512,
                BackgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f),
                UseSceneViewAngle = true,
                orthographic = false,
                OrbitYaw = 45f,
                OrbitPitch = 25f,
                FitFactor = k_DefaultFitFactor,
                orbitDistanceMultiplier = 1f,
                OrbitFov = k_DefaultOrbitFov,
                UseLightingOverride = false,
                layerMask = -1,
                BackgroundMode = BackgroundMode.Skybox,
                WantPostProcessing = true,
                CaptureUi = true,
                UiScale = 1f,
                Light2DMode = LightingMode.None,
                Light2DIntensity = 1f,
                Light2DSortingLayerIds = null,
                Light3DIntensity = 1f,
                Light3DPrefabIntensity = 1.75f,
                Light3DShadows = false,
                Light3DColor = Color.white,
                Light3DYaw = 50f,
                Light3DPitch = -30f,
                Light3DYawMin = -180f,
                Light3DYawMax = 180f,
                Light3DPitchMin = -89f,
                Light3DPitchMax = 89f,
                ParticlePreviewTime = 1f,
            };
        }

        /// <summary>
        /// Remembers the settings snapshot so menu/batch capture paths can reuse
        /// the window's configured settings (resolution, background, lighting
        /// override, post-processing, UI, layer mask, framing). CaptureSettings
        /// is a struct, so the stored copy cannot be corrupted by later mutation
        /// of the caller's instance.
        /// </summary>
        public static void RememberSettings(CaptureSettings settings)
        {
            s_State.LastSettings = settings;
        }

        /// <summary>
        /// Returns the settings last remembered via RememberSettings, or
        /// CreateDefaultSettings() when the store was never set (e.g. before the
        /// window opened in this editor session).
        /// </summary>
        public static CaptureSettings GetLastSettingsOrDefault()
        {
            return s_State.LastSettings ?? CreateDefaultSettings();
        }

        /// <summary>
        /// True when settings.BackgroundMode is Transparent.
        /// </summary>
        internal static bool IsTransparentMode(CaptureSettings settings)
        {
            return settings.BackgroundMode == BackgroundMode.Transparent;
        }

        /// <summary>
        /// Returns false when Transparent mode (post-processing is opaque and
        /// would destroy alpha), otherwise passes through settings.WantPostProcessing
        /// unchanged. Pure: does not mutate the settings struct.
        /// </summary>
        internal static bool EffectiveWantPostProcessing(CaptureSettings settings)
        {
            return IsTransparentMode(settings) ? false : settings.WantPostProcessing;
        }

        /// <summary>
        /// Returns true when post-processing should actually be applied during
        /// capture: the user wants it (EffectiveWantPostProcessing) AND
        /// post-processing can run (HasPostProcessingAvailable).
        /// </summary>
        internal static bool ShouldApplyPostProcessing(CaptureSettings settings)
        {
            return ShouldApplyPostProcessing(settings, null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: the shared snapshot feeds
        /// the Volume check instead of issuing a dedicated native sweep.
        /// Resolution is identical to the parameterless form.
        /// </summary>
        internal static bool ShouldApplyPostProcessing(CaptureSettings settings, ScanCache scan)
        {
            return EffectiveWantPostProcessing(settings) && HasPostProcessingAvailable(scan);
        }

        /// <summary>
        /// True when post-processing can run: the open scene contains at least
        /// one Volume component, or the project has at least one VolumeProfile
        /// asset (which TryCreateTempPostProcessingVolume injects as a
        /// temporary Global Volume). Supports the SetPostProcessingAvailableForTest
        /// override so EditMode tests stay deterministic.
        /// </summary>
        internal static bool HasPostProcessingAvailable()
        {
            return HasPostProcessingAvailable(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore. Identical contract to the
        /// parameterless form; a non-null scan reuses its component snapshot
        /// for the Volume check while the profile check uses the cached guids.
        /// </summary>
        internal static bool HasPostProcessingAvailable(ScanCache scan)
        {
            if (s_State.PostProcessingAvailableOverride.HasValue)
            {
                return s_State.PostProcessingAvailableOverride.Value;
            }
            return HasPostProcessingVolumes(scan) || HasVolumeProfileAssets();
        }

        /// <summary>
        /// Builds the per-capture object snapshot shared by every sweep-based
        /// helper in one capture (bounds, lights, Light2D, Volumes, prefab
        /// isolation): exactly one native sweep per object type (renderer
        /// incl. inactive, light incl. inactive, component incl. inactive,
        /// canvas-renderer incl. inactive) instead of ~8 full sweeps.
        /// Every snapshot is include-inactive on purpose: prefab isolation
        /// must also neutralize disabled and inactive scene objects (they
        /// can be re-enabled mid-pass by edit-mode scripts), while readers
        /// with active-only contracts (TryFindDirectionalLight,
        /// HasPostProcessingVolumes) filter at consumption. Invalidation
        /// rule: the snapshot is method-local to a single
        /// CaptureCore/CapturePrefab call and is never stored across calls,
        /// so scene mutations between captures cannot go stale. Returns null
        /// when DisableScanCache is set; helpers then fall back to their
        /// legacy per-call sweeps with identical results.
        /// </summary>
        internal static ScanCache BeginScan()
        {
            if (s_State.DisableScanCache)
            {
                return null;
            }
            var scan = new ScanCache();
            int sweeps = 0;
#if UNITY_6000_0_OR_NEWER
            scan.Renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include
            );
            sweeps++;
            scan.Lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            sweeps++;
            scan.Components = UnityEngine.Object.FindObjectsByType<Component>(
                FindObjectsInactive.Include
            );
            sweeps++;
            scan.CanvasRenderers = UnityEngine.Object.FindObjectsByType<CanvasRenderer>(
                FindObjectsInactive.Include
            );
#else
            scan.Renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            sweeps++;
            scan.Lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            sweeps++;
            scan.Components = UnityEngine.Object.FindObjectsOfType<Component>(true);
            sweeps++;
            scan.CanvasRenderers = UnityEngine.Object.FindObjectsOfType<CanvasRenderer>(true);
#endif
            // The per-capture UI pass (UiCaptureSession.SwitchOverlayCanvases)
            // performs one more native Canvas sweep, so the per-capture total
            // is 5, not 4.
            sweeps++;
            s_State.LastScanSweepCount = sweeps;
            s_State.LastScanRendererCount = scan.Renderers != null ? scan.Renderers.Length : 0;
            s_State.LastScanLightCount = scan.Lights != null ? scan.Lights.Length : 0;
            s_State.LastScanComponentCount = scan.Components != null ? scan.Components.Length : 0;
            s_State.LastScanCanvasRendererCount =
                scan.CanvasRenderers != null ? scan.CanvasRenderers.Length : 0;
            if (!s_State.ScanCountLogged)
            {
                s_State.ScanCountLogged = true;
                Debug.Log(
                    k_LogPrefix
                        + "capture scan uses "
                        + sweeps
                        + " native sweeps per capture (renderer/light/component/canvas-renderer/canvas shared): renderers="
                        + s_State.LastScanRendererCount
                        + " lights="
                        + s_State.LastScanLightCount
                        + " components="
                        + s_State.LastScanComponentCount
                        + " canvasRenderers="
                        + s_State.LastScanCanvasRendererCount
                );
            }
            return scan;
        }

        /// <summary>
        /// Cached VolumeProfile guid lookup shared by HasVolumeProfileAssets
        /// and ResolvePostProcessingProfile. Invalidation rule: time-based
        /// (k_VolumeProfileCacheSeconds covers a batch pump), explicit
        /// InvalidateVolumeProfileCache (play-mode exit, batch-pump start), and the
        /// DisableScanCache bypass for rollback. Order and content match a
        /// fresh FindAssets call inside the window, so profile resolution is
        /// identical to baseline.
        /// </summary>
        internal static string[] GetVolumeProfileGuidsCached()
        {
            if (!s_State.DisableScanCache && s_State.CachedVolumeProfileGuids != null)
            {
                double age = EditorApplication.timeSinceStartup - s_State.CachedVolumeProfileTick;
                if (age >= 0.0 && age < k_VolumeProfileCacheSeconds)
                {
                    return s_State.CachedVolumeProfileGuids;
                }
            }
            string[] guids = AssetDatabase.FindAssets("t:VolumeProfile");
            s_State.CachedVolumeProfileGuids = guids;
            s_State.CachedVolumeProfileTick = EditorApplication.timeSinceStartup;
            return guids;
        }

        /// <summary>
        /// Clears the VolumeProfile guid cache; the next lookup re-queries.
        /// </summary>
        internal static void InvalidateVolumeProfileCache()
        {
            s_State.CachedVolumeProfileGuids = null;
            s_State.CachedVolumeProfileTick = 0.0;
        }

        /// <summary>
        /// Test hook: forces the result of HasPostProcessingAvailable().
        /// Pass null to restore the real scene checks.
        /// </summary>
        internal static void SetPostProcessingAvailableForTest(bool? value)
        {
            s_State.PostProcessingAvailableOverride = value;
        }

        /// <summary>
        /// Test hook: forces the result of PipelineHasOnly2DRenderers().
        /// Pass null to restore the real pipeline asset checks.
        /// </summary>
        internal static void SetRenderer2DOnlyForTest(bool? value)
        {
            s_State.Renderer2DOnlyOverride = value;
        }

        /// <summary>
        /// Play-mode exit reset (ExitingEditMode). Clears the post-processing
        /// test override on every phase; restores RecordUndo to true only when
        /// idle (guarded by !UniThumbGuard.IsGenerating, never mid-capture).
        /// SAFE (kept): k_* consts, HdrpFixedExposure user config,
        /// ConversionLogged/LastSettings. Idempotent, null-safe, Editor-only.
        /// </summary>
        internal static void ResetForPlayModeExit()
        {
            s_State.PostProcessingAvailableOverride = null;
            s_State.Renderer2DOnlyOverride = null;
            InvalidateVolumeProfileCache();
            if (UniThumbGuard.IsGenerating)
            {
                return;
            }
            s_State.RecordUndo = true;
        }

        /// <summary>
        /// Returns a fully transparent clear color when Transparent mode,
        /// otherwise passes through settings.BackgroundColor unchanged.
        /// Pure: does not mutate the settings struct.
        /// </summary>
        internal static Color EffectiveClearColor(CaptureSettings settings)
        {
            return IsTransparentMode(settings)
                ? new Color(0f, 0f, 0f, 0f)
                : settings.BackgroundColor;
        }

        /// <summary>
        /// True when the composite UI pass applies: CaptureUi AND UseSceneViewAngle
        /// are on, a SceneView with a camera is open, and the UI layer (5) is part
        /// of the layer mask. Sole eligibility source shared by CaptureCore and the
        /// live preview so the two cannot diverge.
        /// </summary>
        public static bool IsSceneViewUiCompositeEligible(CaptureSettings settings)
        {
            if (!settings.CaptureUi || !settings.UseSceneViewAngle)
            {
                return false;
            }
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return false;
            }
            return (settings.layerMask.value & (1 << 5)) != 0;
        }

        /// <summary>
        /// Renders the UI in a separate pass at the SceneView's ACTUAL viewport
        /// pixel size and composites it into the (square) scene pixels with a
        /// contain-fit mapping (no cropping), so the full UI visible in the
        /// SceneView appears in the thumbnail. Returns scenePixels unchanged
        /// when not eligible, when no overlay canvas exists, or on ANY internal
        /// failure (single Debug.LogWarning; exception containment). Owns its
        /// temp camera/RT/session and destroys them in finally; the caller
        /// destroys the input only when the result differs.
        ///
        /// UI scale (settings.UiScale) is applied as a sampling zoom, never to
        /// the canvas: canvas.scaleFactor stays 1 for the whole composite pass,
        /// so the FULL canvas layout always renders into the viewport-sized RT
        /// (zero canvas-rect culling). The zoom is a center-kept band rect (see
        /// the mapping below); clip-free zoom is limited by the band-vs-
        /// thumbnail aspect headroom (~1.9x for a wide SceneView viewport).
        /// Beyond that headroom the UI band overflows the frame and outer UI is
        /// cropped at the thumbnail edges - a clean frame crop, never
        /// mid-layout culling (unavoidable for a fixed square thumbnail).
        ///
        /// Why the real SceneView camera cannot be the canvas worldCamera: the
        /// canvas would render inside the SceneView's own pass, never into the
        /// capture RenderTexture. Why the UI pass RT is sized from
        /// sv.camera.pixelWidth/pixelHeight: CanvasScaler reads
        /// renderingDisplaySize, which follows the render target, so only an RT
        /// at the SceneView viewport's pixel size reproduces the editor layout
        /// (a square or aspect-derived RT re-lays the canvas out at different
        /// proportions). Why the original canvas planeDistance is not reused:
        /// overlay canvases sit at ~100, beyond the near clip range of the orbit
        /// fallback camera, which would clip the UI away; the session re-derives
        /// it from the camera's near plane instead. Blend rationale: wave-0
        /// probe P1 proved the ARGB32 UI RT holds premultiplied color (rgb ==
        /// a*C), so out.rgb = ui.rgb + scene.rgb * (1 - ui.a) always.
        /// </summary>
        public static Texture2D CompositeSceneViewUi(
            CaptureSettings settings,
            Texture2D scenePixels
        )
        {
            if (!IsSceneViewUiCompositeEligible(settings))
            {
                return scenePixels;
            }
            if (scenePixels.width != scenePixels.height)
            {
                // Contain-fit mapping assumes square output (all current
                // callers are square); refuse non-square input rather than
                // producing letterbox offsets for a non-square target (public
                // API guard).
                return scenePixels;
            }
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return scenePixels;
            }
            Camera svCam = sv.camera;
            bool isTransparent = IsTransparentMode(settings);

            // Output dimensions come from the pixels texture, not settings:
            // Capture clamps settings into width/height before CaptureCore, and
            // the downscale retry recurses at a smaller size with the original
            // settings - the composite must match the pixels it blends.
            int width = scenePixels.width;
            int height = scenePixels.height;

            // UI-pass RT sizing: match the SceneView's ACTUAL viewport pixel
            // size (sv.camera.pixelWidth/pixelHeight) so CanvasScaler lays the
            // canvas out identically to the editor (renderingDisplaySize
            // follows the render target). Fall back to the window rect minus
            // the toolbar strip when the camera reports no viewport (0/1
            // before the first render). Proportions stay exact; only the
            // 16M-px reliability cap downscales, proportionally (sqrt), so the
            // aspect survives there too. k_MinResolution floors both dims
            // defensively.
            int viewportW = svCam.pixelWidth;
            int viewportH = svCam.pixelHeight;
            if (viewportW < k_MinResolution || viewportH < k_MinResolution)
            {
                Rect windowRect = sv.position;
                viewportW = Mathf.Max(1, Mathf.RoundToInt(windowRect.width));
                viewportH = Mathf.Max(
                    1,
                    Mathf.RoundToInt(windowRect.height - k_SceneViewToolbarHeight)
                );
            }
            long viewportPixels = (long)viewportW * viewportH;
            if (viewportPixels > k_MaxReliableRenderPixels)
            {
                float shrink = Mathf.Sqrt(k_MaxReliableRenderPixels / (float)viewportPixels);
                viewportW = Mathf.Max(k_MinResolution, Mathf.RoundToInt(viewportW * shrink));
                viewportH = Mathf.Max(k_MinResolution, Mathf.RoundToInt(viewportH * shrink));
            }
            int uiW = Mathf.Max(k_MinResolution, viewportW);
            int uiH = Mathf.Max(k_MinResolution, viewportH);

            GameObject tempGo = null;
            Camera uiCam = null;
            RenderTexture wideRt = null;
            UiCaptureSession uiSession = null;
            Texture2D uiPixels = null;
            try
            {
                tempGo = new GameObject(k_TempUiPassCameraName);
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                uiCam = tempGo.AddComponent<Camera>();

                // Full projection copy from the SceneView camera (no orbit
                // fallback): a ScreenSpaceCamera canvas derives its world size
                // from the projection at planeDistance (perspective
                // 2*d*tan(fov/2), ortho 2*orthoSize), so a wrong projection
                // means a wrong UI size.
                uiCam.transform.SetPositionAndRotation(
                    svCam.transform.position,
                    svCam.transform.rotation
                );
                uiCam.orthographic = svCam.orthographic;
                if (uiCam.orthographic)
                {
                    uiCam.orthographicSize = svCam.orthographicSize;
                }
                else
                {
                    uiCam.fieldOfView = svCam.fieldOfView;
                }
                // near is clamped BEFORE planeDistance derives from it.
                uiCam.nearClipPlane = Mathf.Max(0.01f, svCam.nearClipPlane);
                uiCam.farClipPlane = Mathf.Max(k_MinFarClip, svCam.farClipPlane);
                // Framing math only; the RT re-derives the aspect at render.
                uiCam.aspect = uiW / (float)uiH;

                // Depth 24 silences the URP RenderGraph "output Render Texture
                // must have a depth buffer" advisory; the UI pass never reads
                // depth, the buffer is only allocated.
                wideRt = new RenderTexture(uiW, uiH, 24, RenderTextureFormat.ARGB32);
                wideRt.Create();
                // Premultiplied neutral clear (0,0,0,0).
                ClearRenderTexture(wideRt, new Color(0f, 0f, 0f, 0f));

                uiCam.targetTexture = wideRt;
                uiCam.clearFlags = CameraClearFlags.SolidColor;
                uiCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                // Layer 5 is UI-only for the wide pass (probe P2: a
                // ScreenSpaceCamera canvas renders with a UI-layer-only mask;
                // non-canvas renderers on layer 5 would also appear, same as any
                // camera with this mask).
                uiCam.cullingMask = settings.layerMask.value & (1 << 5);

                uiSession = UiCaptureSession.BeginUiPass(uiCam);
                if (uiSession == null)
                {
                    // No overlay canvas exists; scene-only thumbnail.
                    return scenePixels;
                }
                // Canvas layout derives from renderingDisplaySize, which follows
                // the render target; refresh again after targetTexture assignment.
                Canvas.ForceUpdateCanvases();

                uiCam.Render();
                uiPixels = ReadBack(wideRt);

                // Band-rect zoom mapping: the UI pass renders the FULL canvas
                // layout at scaleFactor 1 (viewport-sized rect, zero canvas
                // culling); settings.UiScale is applied here as a center-kept
                // sampling zoom. fitScaleUi = fitScale * uiScale scales the UI
                // band in thumbnail pixels; the offsets center it and go
                // NEGATIVE when the band is wider/taller than the thumbnail
                // (the band overflows the frame - outer UI crops cleanly at the
                // thumbnail edges, never mid-layout culling). The UI band is
                // sampled BILINEARLY (4-texel weighted average, float math):
                // nearest would alias badly at 128px thumbnails (downscale in
                // landscape, possibly upscale in portrait). At uiScale == 1f
                // fitScaleUi == fitScale and the offsets/guard match the
                // previous contain-fit mapping exactly (byte-identical output).
                float fitScale = Mathf.Min(width / (float)uiW, height / (float)uiH);
                float effectiveUiScale = Mathf.Clamp(settings.UiScale, 0.25f, 4f);
                float fitScaleUi = fitScale * effectiveUiScale;
                float xOffsetUi = (width - uiW * fitScaleUi) * 0.5f;
                float yOffsetUi = (height - uiH * fitScaleUi) * 0.5f;
                float bandRightUi = xOffsetUi + uiW * fitScaleUi;
                float bandBottomUi = yOffsetUi + uiH * fitScaleUi;

                // 32-bit blend path: GetPixels32 (4 B/px) keeps interpolation
                // + blend math in float to avoid banding; output stays Color32
                // like the original loop. Saves ~24 B/px transient vs the float
                // GetPixels x2 path (2 x W*H*16 B -> 2 x W*H*4 B).
                // Equality guard: output matches the float path exact or <=1 LSB
                // incl transparent mode (bilinear + premultiplied math unchanged).
                // Rollback note: revert to the GetPixels float path if the
                // equality check fails.
                Color32[] sceneColors = scenePixels.GetPixels32();
                Color32[] uiColors = uiPixels.GetPixels32();
                var output = new Color32[width * height];
                for (int y = 0; y < height; y++)
                {
                    int sceneRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        Color scene = sceneColors[sceneRow + x];
                        bool letterbox =
                            x < xOffsetUi || x >= bandRightUi || y < yOffsetUi || y >= bandBottomUi;
                        if (letterbox)
                        {
                            // Letterbox band: no UI covers this pixel; keep the
                            // scene pixel untouched (UI alpha 0 equivalent).
                            byte alpha = isTransparent
                                ? (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.a) * 255f)
                                : (byte)255;
                            output[sceneRow + x] = new Color32(
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.r) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.g) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(scene.b) * 255f),
                                alpha
                            );
                            continue;
                        }
                        float uiX = (x - xOffsetUi) / fitScaleUi;
                        float uiY = (y - yOffsetUi) / fitScaleUi;
                        Color ui = SampleBilinear32(uiColors, uiW, uiH, uiX, uiY);
                        float r;
                        float g;
                        float b;
                        // Premultiplied (probe P1): ui.rgb already holds a*C;
                        // premultiplied rgb <= a so the sum cannot exceed 1
                        // (clamped anyway).
                        r = ui.r + scene.r * (1f - ui.a);
                        g = ui.g + scene.g * (1f - ui.a);
                        b = ui.b + scene.b * (1f - ui.a);
                        output[sceneRow + x] = new Color32(
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(b) * 255f),
                            isTransparent
                                ? (byte)
                                    Mathf.RoundToInt(
                                        Mathf.Clamp01(ui.a + scene.a * (1f - ui.a)) * 255f
                                    )
                                : (byte)255
                        );
                    }
                }

                var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.SetPixels32(output);
                result.Apply();
                return result;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "UI composite pass failed, using scene-only thumbnail: "
                        + exception.Message
                );
                return scenePixels;
            }
            finally
            {
                if (uiSession != null)
                {
                    uiSession.Dispose();
                }
                if (uiPixels != null)
                {
                    UnityEngine.Object.DestroyImmediate(uiPixels);
                }
                if (uiCam != null)
                {
                    uiCam.targetTexture = null;
                }
                if (tempGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
                }
                if (wideRt != null)
                {
                    wideRt.Release();
                    UnityEngine.Object.DestroyImmediate(wideRt);
                }
            }
        }

        public static CaptureResult Capture(CaptureSettings settings)
        {
            s_State.ConversionLogged = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning = "Capture refused: play mode is active or about to change.",
                };
            }

            string openStagePath;
            if (TryGetPrefabStageAssetPath(out openStagePath))
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning =
                        "Capture refused: Prefab Stage is open ('"
                        + openStagePath
                        + "'). Close the stage or capture the prefab instead.",
                };
            }

            int width = Mathf.Clamp(settings.Width, k_MinResolution, k_MaxResolution);
            int height = Mathf.Clamp(settings.Height, k_MinResolution, k_MaxResolution);
            return CaptureCore(width, height, settings, null);
        }

        /// <summary>
        /// Renders a prefab asset to a PNG thumbnail WITHOUT opening it as a
        /// scene and without dirtying the open scene. The prefab is instantiated
        /// additively into the open scene, framed with the shared orbit-bounds
        /// math (computed from the instance subtree only), and isolated by
        /// switching every other Renderer to forceRenderingOff plus every
        /// outside CanvasRenderer to cull (both non-serialized runtime flags:
        /// zero scene dirtying, restored in finally). CanvasRenderer is not a
        /// Renderer subclass, so the Renderer pass alone misses world-space
        /// and ScreenSpaceCamera UI and overlay canvases switched onto the
        /// capture camera by UiCaptureSession. The instance is
        /// created under an Undo group and reverted in finally, with an explicit
        /// DestroyImmediate fallback. An open Prefab Stage wins over the passed
        /// path (TryResolvePrefabAssetPath): a stage-open capture renders the
        /// staged prefab, never the active scene. Scene captures (Capture) are
        /// untouched by this method. Pair with UniThumbGuard.TryEnter/Exit at
        /// the call site; persist the bytes via
        /// UniThumbStorage.SavePrefabThumbnail using AssetPathToGUID.
        /// Prefab UI reuses settings.CaptureUi (default true renders prefab
        /// UI by default; false culls UI with pixel parity to the old
        /// behavior). No prefab-specific UI setting exists.
        /// </summary>
        public static CaptureResult CapturePrefab(string prefabAssetPath, CaptureSettings settings)
        {
            string resolvedPrefabPath;
            if (TryResolvePrefabAssetPath(prefabAssetPath, out resolvedPrefabPath))
            {
                prefabAssetPath = resolvedPrefabPath;
            }
            if (string.IsNullOrEmpty(prefabAssetPath))
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning = "Prefab capture refused: asset path is null or empty.",
                };
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab == null)
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning =
                        "Prefab capture refused: '" + prefabAssetPath + "' is not a prefab asset.",
                };
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new CaptureResult
                {
                    Success = false,
                    Warning = "Capture refused: play mode is active or about to change.",
                };
            }

            s_State.ConversionLogged = false;
            int width = Mathf.Clamp(settings.Width, k_MinResolution, k_MaxResolution);
            int height = Mathf.Clamp(settings.Height, k_MinResolution, k_MaxResolution);

            GameObject instance = null;
            List<Renderer> isolated = null;
            List<CanvasRenderer> isolatedUi = null;
            List<TerrainSnapshot> isolatedTerrains = null;
            List<VisualEffectSnapshot> isolatedVfx = null;
            List<Renderer> isolatedHidden = null;
            List<CanvasRenderer> isolatedHiddenUi = null;
            PrefabEnvSnapshot prefabEnv = new PrefabEnvSnapshot();
            List<LightSnapshot> prefabLights = null;
            List<Light2DSnapshot> prefabLight2Ds = null;
            List<VolumeSnapshot> prefabVolumes = null;
            int prefabIsolationLayer = -1;
            List<LayerSnapshot> isolatedLayers = null;
            Undo.IncrementCurrentGroup();
            int preCaptureUndoGroup = Undo.GetCurrentGroup();
            try
            {
                // Snapshot BEFORE any mutation: BeginScan and the live
                // directional read must see the unmuted scene. A scan taken
                // after Instantiate/DisableAll would include the instance's
                // own objects and read disabled lights as absent, forcing a
                // wrong fallback and a black render. The pre-instance scan
                // also keeps instance lights/volumes out of the disable
                // sweeps, so the prefab's own setup survives untouched.
                ScanCache scan = BeginScan();
                Light preDisableDirectional = TryFindDirectionalLight(scan);
                prefabEnv = SnapshotPrefabEnvironment();

                instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Warning =
                            "Prefab capture failed: InstantiatePrefab returned null for '"
                            + prefabAssetPath
                            + "'.",
                    };
                }
                Undo.RegisterCreatedObjectUndo(instance, "UniThumb prefab capture");
                instance.hideFlags = HideFlags.HideAndDontSave;

                // Particle/VFX pre-roll BEFORE bounds: fresh instances hold zero
                // live particles (zero-extent bounds skipped below), so the
                // frame must be advanced first; bounds recomputed after.
                // Shared preview value (0-5s, 0 = restart-only frame).
                float previewTime = Mathf.Clamp(settings.ParticlePreviewTime, 0f, 5f);
                SimulatePrefabParticles(instance, previewTime);
                SimulatePrefabVfx(instance, previewTime);

                Bounds instanceBounds;
                Bounds? framing = TryGetPrefabBounds(
                    instance,
                    settings.layerMask,
                    out instanceBounds
                )
                    ? (Bounds?)instanceBounds
                    : GetOverlayFallbackFraming(instance);

                isolated = IsolateInstanceRenderers(instance, scan);
                isolatedUi = IsolateInstanceCanvasRenderers(instance, scan);
                isolatedTerrains = IsolateInstanceTerrains(instance, scan);
                isolatedVfx = IsolateInstanceVfx(instance, scan);
                isolatedHidden = IsolateHiddenInstanceRenderers(instance);
                isolatedHiddenUi = IsolateHiddenInstanceCanvasRenderers(instance);

                // Camera-level isolation: the hidden clone subtree moves onto
                // a scene-unused layer and the temp camera renders that layer
                // only (CaptureCore), so scene geometry cannot leak into the
                // shot even where flag-based isolation misses. Only the clone
                // subtree moves (user objects never move); the original layers
                // restore in finally. Fail-open: -1 keeps the flag-based path
                // when every layer is in use. Runs after bounds so framing
                // still filters the original layers via settings.layerMask.
                prefabIsolationLayer = FindPrefabIsolationLayer(scan);
                isolatedLayers = IsolateInstanceLayers(instance, prefabIsolationLayer);

                // Prefab-only environment: the renderer/canvas isolation above
                // hides scene GEOMETRY, but RenderSettings (skybox/fog/ambient/
                // reflections), scene lights, and scene Volumes still shape the
                // pixels. Neutralize them here (snapshot/restore in finally)
                // so the thumbnail holds the prefab subtree only on a neutral
                // background. The instance subtree (own lights/volumes) is
                // exempt: the pre-instance scan keeps them out of the disable
                // sweeps, and the re-enables below cover the DisableScanCache
                // rollback path (scan null: fresh sweeps include the instance).
                // (prefabEnv was snapshotted before Instantiate above.)
                NeutralizePrefabEnvironment(settings);
                if (PrefabLightingNeedsNeutralize(settings))
                {
                    prefabLights = DisableAllLights(scan);
                    prefabLight2Ds = DisableAllLight2Ds(scan);
                    ReenableSubtreeLights(instance, prefabLights);
                    ReenableSubtreeLight2Ds(instance, prefabLight2Ds);
                }
                prefabVolumes = DisableSceneVolumes(instance, scan);

                // Content-aware fallback, decided here where the live
                // pre-disable state (preDisableDirectional) and the instance
                // are both visible. The neutralize step above disabled every
                // scene light, so mode None always needs one prefab-scoped
                // key (perspective/3D content: directional; sprite-only
                // ortho: Light2D); a prefab with its own key light needs no
                // temp. Resolved once here and handed to CaptureCore so its
                // post-disable recompute cannot go stale and double-light.
                bool has3DContent = PrefabSubtreeHas3DContent(instance);
                bool needDirectional = !settings.orthographic || has3DContent;
                bool hasOwnKey = PrefabSubtreeHasOwnKeyLight(instance, needDirectional);
                bool prefabRenderer2DOnly = PipelineHasOnly2DRenderers();
                PrefabFallbackLight prefabFallback = ResolvePrefabFallbackLight(
                    settings.Light2DMode,
                    true,
                    settings.orthographic,
                    settings.UseLightingOverride,
                    preDisableDirectional != null,
                    has3DContent,
                    hasOwnKey,
                    prefabRenderer2DOnly
                );

                CaptureSettings orbitSettings = settings;
                orbitSettings.UseSceneViewAngle = false;
                // Scene Volumes are disabled above; without this the stale
                // scan snapshot (taken pre-disable) would report volumes and
                // skip the project-profile fallback inconsistently with the
                // live preview (fresh sweep, profiles apply). Prefab captures
                // only run post-processing for an explicitly assigned profile
                // or in Skybox mode (the sky benefits from tonemapping/color
                // grading, and the HDR target follows ShouldApplyPostProcessing
                // so keeping the flag also keeps HDR for sky shots).
                // Transparent mode stays off downstream via
                // EffectiveWantPostProcessing regardless of this flag.
                if (
                    orbitSettings.PostProcessingProfile == null
                    && orbitSettings.BackgroundMode != BackgroundMode.Skybox
                )
                {
                    orbitSettings.WantPostProcessing = false;
                }
                CaptureResult prefabResult = CaptureCore(
                    width,
                    height,
                    orbitSettings,
                    framing,
                    scan,
                    prefabFallback,
                    false,
                    instance,
                    prefabIsolationLayer
                );
                return prefabResult;
            }
            finally
            {
                RestoreIsolatedLayers(isolatedLayers);
                RestoreIsolatedRenderers(isolated);
                RestoreIsolatedCanvasRenderers(isolatedUi);
                RestoreIsolatedRenderers(isolatedHidden);
                RestoreIsolatedCanvasRenderers(isolatedHiddenUi);
                RestoreIsolatedTerrains(isolatedTerrains);
                RestoreIsolatedVfx(isolatedVfx);
                RestoreSceneVolumes(prefabVolumes);
                RestoreLights(prefabLights);
                RestoreGlobalLight2Ds(prefabLight2Ds);
                RestorePrefabEnvironment(prefabEnv);
                Undo.RevertAllDownToGroup(preCaptureUndoGroup);
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        /// <summary>
        /// Computes framing bounds from a prefab instance subtree only (active
        /// renderers within the layer mask, zero-extent renderers skipped like
        /// the scene sweep). World and ScreenSpaceCamera canvas RectTransform
        /// extents expand the bounds; ScreenSpaceOverlay canvases stay out
        /// (screen-space, no world extents). Null-safe: null root returns false.
        /// </summary>
        internal static bool TryGetPrefabBounds(
            GameObject root,
            LayerMask layerMask,
            out Bounds bounds
        )
        {
            bounds = new Bounds();
            if (root == null)
            {
                return false;
            }
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if ((layerMask.value & (1 << renderer.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (renderer.bounds.extents.sqrMagnitude < 0.001f)
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
            RectTransform[] rects = root.GetComponentsInChildren<RectTransform>(true);
            if (rects != null)
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    RectTransform rectTransform = rects[i];
                    if (rectTransform == null || !rectTransform.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    if ((layerMask.value & (1 << rectTransform.gameObject.layer)) == 0)
                    {
                        continue;
                    }
                    Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
                    if (canvas == null || !canvas.enabled || !canvas.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    if (canvas.gameObject != root && !canvas.transform.IsChildOf(root.transform))
                    {
                        continue;
                    }
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    {
                        continue;
                    }
                    Rect rect = rectTransform.rect;
                    Vector3 corner0 = rectTransform.TransformPoint(
                        new Vector3(rect.xMin, rect.yMin, 0f)
                    );
                    Vector3 corner1 = rectTransform.TransformPoint(
                        new Vector3(rect.xMax, rect.yMin, 0f)
                    );
                    Vector3 corner2 = rectTransform.TransformPoint(
                        new Vector3(rect.xMax, rect.yMax, 0f)
                    );
                    Vector3 corner3 = rectTransform.TransformPoint(
                        new Vector3(rect.xMin, rect.yMax, 0f)
                    );
                    Bounds uiBounds = new Bounds(corner0, Vector3.zero);
                    uiBounds.Encapsulate(corner1);
                    uiBounds.Encapsulate(corner2);
                    uiBounds.Encapsulate(corner3);
                    if (uiBounds.extents.sqrMagnitude < 0.001f)
                    {
                        continue;
                    }
                    if (!any)
                    {
                        bounds = uiBounds;
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(uiBounds);
                    }
                }
            }
            return any;
        }

        /// <summary>
        /// True when the prefab instance subtree holds at least one enabled
        /// screen-space canvas (Overlay or ScreenSpaceCamera) on an active
        /// GameObject. Such prefabs can yield null world bounds (Overlay has
        /// no world extents; an unlaid-out Camera canvas collapses to zero)
        /// yet still render UI once retargeted, so CapturePrefab frames them
        /// with the fixed fallback below instead of the scene-driven orbit.
        /// Null-safe: null root returns false.
        /// </summary>
        internal static bool PrefabHasScreenSpaceCanvas(GameObject root)
        {
            if (root == null)
            {
                return false;
            }
            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            if (canvases == null)
            {
                return false;
            }
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !canvas.isActiveAndEnabled)
                {
                    continue;
                }
                if (
                    canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    && canvas.renderMode != RenderMode.ScreenSpaceCamera
                )
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fixed fallback framing for bounds-less UI prefabs: a synthetic
        /// bounds centered on the instance position with 5-unit half-extents.
        /// Retargeted screen-space UI fills the frame at any camera pose, so
        /// the exact numbers only keep the orbit math (near plane, plane
        /// distance, orthographic size) in a sane range and the capture
        /// independent of unrelated open-scene contents. Returns null when
        /// the subtree holds no screen-space canvas (genuinely-empty renders
        /// keep the legacy null framing and still fail downstream).
        /// </summary>
        internal static Bounds? GetOverlayFallbackFraming(GameObject root)
        {
            if (root == null || !PrefabHasScreenSpaceCanvas(root))
            {
                return null;
            }
            return new Bounds(root.transform.position, new Vector3(10f, 10f, 10f));
        }

        /// <summary>
        /// Fixed camera distance for the Overlay-only screen-space fill path.
        /// The camera sits at z=-distance looking +Z; the converted Overlay
        /// canvas plane lands at planeDistance in front of it.
        /// </summary>
        internal const float k_OverlayOnlyCameraDistance = 10f;

        /// <summary>
        /// Fixed clip planes for the Overlay-only screen-space fill path.
        /// Independent of the orbit distance so resolution cannot move them.
        /// </summary>
        internal const float k_OverlayOnlyNear = 0.1f;

        /// <summary>
        /// Fixed far plane for the Overlay-only screen-space fill path.
        /// </summary>
        internal const float k_OverlayOnlyFar = 100f;

        /// <summary>
        /// Default reference size for the Overlay-only fill path when the
        /// subtree carries no ScaleWithScreenSize scaler. Matches the test
        /// reference (800x600) so unit and capture tests agree.
        /// </summary>
        internal static readonly Vector2 k_OverlayOnlyDefaultReference = new Vector2(800f, 600f);

        /// <summary>
        /// Asset-compatible activity check for prefab canvases: enabled plus
        /// activeSelf up the chain to the prefab root. isActiveAndEnabled
        /// and activeInHierarchy are false on uninstantiated prefab assets,
        /// so they cannot gate asset-side detection (tests) or hinder the
        /// instance path. Null-safe.
        /// </summary>
        internal static bool IsPrefabCanvasActive(Canvas canvas, GameObject root)
        {
            if (canvas == null || !canvas.enabled)
            {
                return false;
            }
            Transform current = canvas.transform;
            if (current == null)
            {
                return false;
            }
            while (current != null)
            {
                if (!current.gameObject.activeSelf)
                {
                    return false;
                }
                if (root != null && current.gameObject == root)
                {
                    break;
                }
                current = current.parent;
            }
            return true;
        }

        /// <summary>
        /// True when the prefab subtree is Overlay-only UI: no world bounds
        /// (Overlay has no world extents, so TryGetPrefabBounds is false),
        /// at least one enabled Overlay canvas in the mask, and zero enabled
        /// ScreenSpaceCamera canvases in the mask. Camera-carrying prefabs
        /// keep the orbit path (authored planeDistance matters there).
        /// Null-safe: null root returns false.
        /// </summary>
        internal static bool IsOverlayOnlyPrefab(GameObject root, LayerMask layerMask)
        {
            if (root == null)
            {
                return false;
            }
            if (TryGetPrefabBounds(root, layerMask, out Bounds ignored))
            {
                return false;
            }
            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            if (canvases == null)
            {
                return false;
            }
            bool hasOverlay = false;
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !IsPrefabCanvasActive(canvas, root))
                {
                    continue;
                }
                if ((layerMask.value & (1 << canvas.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (canvas.gameObject != root && !canvas.transform.IsChildOf(root.transform))
                {
                    continue;
                }
                if (canvas.renderMode == RenderMode.ScreenSpaceCamera)
                {
                    return false;
                }
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    hasOverlay = true;
                }
            }
            return hasOverlay;
        }

        /// <summary>
        /// Reference size driving the Overlay-only ortho fit: the first
        /// enabled Overlay canvas ScaleWithScreenSize referenceResolution in
        /// the subtree, else the 800x600 default. Null-safe.
        /// </summary>
        internal static Vector2 GetOverlayOnlyReferenceSize(GameObject root)
        {
            if (root != null)
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                if (canvases != null)
                {
                    for (int i = 0; i < canvases.Length; i++)
                    {
                        Canvas canvas = canvases[i];
                        if (canvas == null || !IsPrefabCanvasActive(canvas, root))
                        {
                            continue;
                        }
                        if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        {
                            continue;
                        }
                        CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                        if (scaler == null)
                        {
                            continue;
                        }
                        if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                        {
                            continue;
                        }
                        Vector2 reference = scaler.referenceResolution;
                        if (reference.x > 0f && reference.y > 0f)
                        {
                            return reference;
                        }
                    }
                }
            }
            return k_OverlayOnlyDefaultReference;
        }

        /// <summary>
        /// Content center of Overlay canvases in reference pixels: the mean
        /// anchoredPosition of direct RectTransform children under enabled
        /// Overlay canvases. Resolution-independent (authored values only,
        /// never RT-sized), so ApplyOverlayOnlyFraming can center the camera
        /// on the canvas rect instead of a fixed origin. Returns zero when
        /// no content exists. Null-safe.
        /// </summary>
        internal static Vector2 GetOverlayContentCenterInReferencePixels(GameObject root)
        {
            if (root == null)
            {
                return Vector2.zero;
            }
            Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
            if (canvases == null)
            {
                return Vector2.zero;
            }
            float sumX = 0f;
            float sumY = 0f;
            int count = 0;
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !IsPrefabCanvasActive(canvas, root))
                {
                    continue;
                }
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    continue;
                }
                Transform canvasTransform = canvas.transform;
                if (canvasTransform == null)
                {
                    continue;
                }
                int childCount = canvasTransform.childCount;
                for (int j = 0; j < childCount; j++)
                {
                    Transform child = canvasTransform.GetChild(j);
                    if (child == null || !child.gameObject.activeSelf)
                    {
                        continue;
                    }
                    RectTransform rect = child as RectTransform;
                    if (rect == null)
                    {
                        continue;
                    }
                    sumX += rect.anchoredPosition.x;
                    sumY += rect.anchoredPosition.y;
                    count++;
                }
            }
            if (count == 0)
            {
                return Vector2.zero;
            }
            return new Vector2(sumX / count, sumY / count);
        }

        /// <summary>
        /// Screen-space ortho fill for Overlay-only prefabs: fixed
        /// orthographic fit of the canvas reference rect (contain-fit via
        /// aspect), fixed near/far, fixed planeDistance 0.2 derived from the
        /// fixed near plane in the prefab UI session. The camera centers on
        /// the canvas rect content center (not a fixed origin) so anchored
        /// offsets cannot drift with RT size. Skips the orbit distance and
        /// the synthetic fallback bounds entirely; the RT-proportional canvas
        /// scale (density only) plus this fixed frustum holds a constant
        /// frame fraction at any thumbnail resolution. Null camera is a no-op.
        /// </summary>
        internal static void ApplyOverlayOnlyFraming(
            Camera cam,
            GameObject prefabRoot,
            int width,
            int height
        )
        {
            if (cam == null)
            {
                return;
            }
            float aspect = height > 0 ? width / (float)height : 1f;
            if (aspect <= 0f || float.IsNaN(aspect) || float.IsInfinity(aspect))
            {
                aspect = 1f;
            }
            cam.aspect = aspect;
            cam.orthographic = true;
            Vector2 reference = GetOverlayOnlyReferenceSize(prefabRoot);
            float ortho = reference.y * 0.5f;
            float byWidth = reference.x * 0.5f / aspect;
            if (byWidth > ortho)
            {
                ortho = byWidth;
            }
            if (ortho <= 0f || float.IsNaN(ortho) || float.IsInfinity(ortho))
            {
                ortho = k_OverlayOnlyDefaultReference.y * 0.5f;
            }
            cam.orthographicSize = ortho;
            float centerX = 0f;
            float centerY = 0f;
            if (prefabRoot != null)
            {
                Vector2 centerPixels = GetOverlayContentCenterInReferencePixels(prefabRoot);
                float worldPerPixel = 0f;
                if (reference.y > 0f)
                {
                    worldPerPixel = ortho * 2f / reference.y;
                }
                centerX = centerPixels.x * worldPerPixel;
                centerY = centerPixels.y * worldPerPixel;
            }
            cam.transform.position = new Vector3(centerX, centerY, -k_OverlayOnlyCameraDistance);
            cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            cam.nearClipPlane = k_OverlayOnlyNear;
            cam.farClipPlane = k_OverlayOnlyFar;
        }

        /// <summary>
        /// Fixed particle pre-roll (seconds) applied once to prefab instances
        /// before bounds framing and render. One second shows a representative
        /// emission frame for typical looping systems.
        /// </summary>
        internal const float PrefabParticleSimulateTime = 1f;

        /// <summary>
        /// Advances every ParticleSystem under a prefab instance root to a
        /// representative frame so fresh instances (zero live particles) both
        /// render and contribute bounds. Single call per capture/preview with
        /// order Instantiate -&gt; Simulate -&gt; Bounds -&gt; Isolate -&gt; Neutralize
        /// -&gt; Render; bounds are (re)computed after this call. Only
        /// root-most systems simulate with withChildren=true (restart once),
        /// so nested sub-emitters are not double-simulated. Trails render
        /// their first-frame positions only (single Simulate cannot build
        /// trail history). VFX Graph (VisualEffect) is handled separately by
        /// SimulatePrefabVfx. Null-safe: null root is a no-op.
        /// </summary>
        internal static void SimulatePrefabParticles(GameObject root)
        {
            SimulatePrefabParticles(root, PrefabParticleSimulateTime);
        }

        /// <summary>
        /// Time-parameterized variant of SimulatePrefabParticles. Time is
        /// clamped to 0..5s to bound cost on large particle counts; 0 means
        /// restart-only (Simulate(0, restart) clears to the first frame).
        /// </summary>
        internal static void SimulatePrefabParticles(GameObject root, float time)
        {
            if (root == null)
            {
                return;
            }
            float simulateTime = Mathf.Clamp(time, 0f, 5f);
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            if (systems == null)
            {
                return;
            }
            Transform rootTransform = root.transform;
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                {
                    continue;
                }
                if (HasParticleSystemAncestor(system.transform, rootTransform))
                {
                    continue;
                }
                try
                {
                    system.Simulate(simulateTime, true, true, false);
                }
                catch (System.Exception exception)
                {
                    Debug.LogWarning(
                        k_LogPrefix + "Particle pre-roll failed: " + exception.Message
                    );
                }
            }
        }

        /// <summary>
        /// True when any ancestor of child (up to and excluding root) carries
        /// a ParticleSystem, meaning the ancestor's withChildren=true Simulate
        /// already advanced this system. Null-safe.
        /// </summary>
        internal static bool HasParticleSystemAncestor(Transform child, Transform root)
        {
            if (child == null || root == null || child == root)
            {
                return false;
            }
            Transform current = child.parent;
            while (current != null)
            {
                if (current.GetComponent<ParticleSystem>() != null)
                {
                    return true;
                }
                if (current == root)
                {
                    break;
                }
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Fixed VFX pre-roll step (seconds) applied to prefab instances
        /// before bounds framing and render. Sixty steps of 1/60s advance
        /// every VisualEffect under the root by exactly one second.
        /// </summary>
        internal const float PrefabVfxStepDelta = 1f / 60f;

        /// <summary>
        /// Fixed VFX pre-roll step count. Locked with PrefabVfxStepDelta so
        /// the total pre-roll is exactly one second.
        /// </summary>
        internal const int PrefabVfxStepCount = 60;

        /// <summary>
        /// Fixed start seed applied to prefab VisualEffect instances before
        /// pre-roll so the captured frame is deterministic.
        /// </summary>
        internal const uint PrefabVfxSeed = 0;

        /// <summary>
        /// Max VisualEffect components simulated per prefab capture.
        /// Bounds large-prefab cost (each Reinit plus Simulate is CPU work);
        /// components past the cap are skipped silently, root-most first.
        /// </summary>
        internal const int PrefabVfxMaxComponents = 8;

        // dyn-05 decision matrix (owner: dyn-05 implementer; consumed by ver-06).
        // URP shim: verdict ADOPTED per 20260910 user approval (dyn-05 REJECT
        // reversed). Core HAS_URP sites dispatch to UrpBridge via UniThumbUrp
        // (zero Universal refs in core); absent clean via HAS_URP.
        // HDRP shim: removes HDAC/Exposure/frame-settings dynamic; absent fails
        // on URP-only (forced install); risk high (14 vs 17 fragile); verdict
        // REJECT shim, keep narrow dynamic plus isolated version-branched helper.
        // VFX shim: removes Simulate dynamic; absent fails on Built-in and 2D
        // (forced install); risk medium; verdict REJECT shim, keep cached narrow
        // dynamic plus once-log fail-open skip.
        // No-hard-ref: zero Universal/Core dynamic retained; dynamic surface is
        // VFXModule plus HDRP.Runtime plus absent guards only; verdict ADOPT.
        /// <summary>
        /// Finds the UnityEngine.VFX.VisualEffect type via reflection.
        /// dyn-05 KEEP-DYNAMIC: VFX Graph is absent on Built-in and 2D-only
        /// projects; a hard reference would force a package install, so this
        /// site stays dynamic with a fail-open null path (no throw).
        /// Returns null when the VFX Graph package is absent, so callers
        /// stay compile-safe on Unity 2022.3 and 6000.4 with no asmdef
        /// references and no using UnityEngine.VFX. Result is cached after
        /// the first resolve, so later captures skip the AppDomain scan.
        /// </summary>
        internal static System.Type FindVisualEffectType()
        {
            if (s_State.VfxTypeResolved)
            {
                return s_State.VfxType;
            }
            System.Type direct = System.Type.GetType(
                "UnityEngine.VFX.VisualEffect, UnityEngine.VFXModule"
            );
            if (direct != null)
            {
                s_State.VfxType = direct;
                s_State.VfxTypeResolved = true;
                return direct;
            }
            foreach (
                System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies()
            )
            {
                try
                {
                    System.Type candidate = assembly.GetType("UnityEngine.VFX.VisualEffect");
                    if (candidate != null)
                    {
                        s_State.VfxType = candidate;
                        s_State.VfxTypeResolved = true;
                        return candidate;
                    }
                }
                catch (System.Exception)
                {
                    // Skip unloadable assemblies.
                }
            }
            s_State.VfxType = null;
            s_State.VfxTypeResolved = true;
            return null;
        }

        /// <summary>
        /// Advances every VisualEffect under a prefab instance root to a
        /// representative frame so fresh instances render and contribute
        /// bounds. Fixed one second via Simulate(1/60, 60) with a
        /// deterministic start seed, then paused. Null-safe: null root is
        /// a no-op; a missing VFX Graph package returns without throwing.
        /// Per-component failures log one warning total and continue.
        /// Only root-most effects simulate with the full step block, so
        /// nested effects under the same root are not double-simulated
        /// (mirrors HasParticleSystemAncestor). At most
        /// PrefabVfxMaxComponents simulate per call; the remainder is
        /// skipped silently to bound large-prefab cost. Bounds are never
        /// rewritten here: TryGetPrefabBounds skips zero-extent renderers,
        /// so a pure-VFX prefab with no Renderer bounds returns false and
        /// CapturePrefab falls back to the orbit default exactly like an
        /// unemitted particle prefab.
        /// Platform notes: Renderer2D ignores VisualEffect output, and URP
        /// renders only a subset of the VFX Graph feature set, while HDRP
        /// auto-installs the VFX Graph package. OpenGL ES targets lack
        /// compute-shader support, so pre-roll is skipped there (see
        /// IsVfxPreRollUnsupported). Project~ version note (comment only,
        /// no manifest edit): the primary dev project targets Unity
        /// 6000.4.8f1 (VFX Graph 17.x line) and the fallback targets Unity
        /// 2022.3.19f1 (VFX Graph 14.x line); VisualEffect.Simulate and
        /// startSeed cover both lines via the uint/int overload resolve.
        /// Reflection (seed/pause/Reinit/Simulate) is resolved once and
        /// cached; steady-state captures allocate one arg array per call,
        /// with no LINQ and no closures.
        /// </summary>
        internal static void SimulatePrefabVfx(GameObject root)
        {
            SimulatePrefabVfx(root, 1f);
        }

        /// <summary>
        /// Time-parameterized variant of SimulatePrefabVfx. Time is clamped
        /// to 0..5s; steps are shared with the particle value as
        /// round(time/stepDelta) clamped to 1..300 (0 still restarts with a
        /// single minimal step). Deterministic start seed, then paused.
        /// Null-safe: null root is a no-op; a missing VFX Graph package
        /// returns without throwing. Per-component failures log one warning
        /// total and continue. Only root-most effects simulate, so nested
        /// effects under the same root are not double-simulated. At most
        /// PrefabVfxMaxComponents simulate per call. Bounds are never
        /// rewritten here (see parameterless docs).
        /// </summary>
        internal static void SimulatePrefabVfx(GameObject root, float time)
        {
            if (root == null)
            {
                return;
            }
            float previewTime = Mathf.Clamp(time, 0f, 5f);
            int steps = Mathf.Clamp(Mathf.RoundToInt(previewTime / PrefabVfxStepDelta), 1, 300);
            string skipReason;
            if (IsVfxPreRollUnsupported(out skipReason))
            {
                LogVfxSkipOnce(skipReason);
                return;
            }
            System.Type vfxType = FindVisualEffectType();
            if (vfxType == null)
            {
                return;
            }
            EnsureVfxReflectionCached(vfxType);
            Component[] components;
            try
            {
                components = root.GetComponentsInChildren(vfxType, true);
            }
            catch (System.Exception)
            {
                return;
            }
            if (components == null)
            {
                return;
            }
            System.Reflection.PropertyInfo seedProperty = s_State.VfxSeedProperty;
            System.Reflection.PropertyInfo pauseProperty = s_State.VfxPauseProperty;
            System.Reflection.MethodInfo reinitMethod = s_State.VfxReinitMethod;
            System.Reflection.MethodInfo simulateMethod = s_State.VfxSimulateMethod;
            object[] simulateArgs = BuildVfxSimulateArgs(simulateMethod, steps);
            Transform rootTransform = root.transform;
            int simulated = 0;
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }
                if (HasVisualEffectAncestor(component.transform, rootTransform, vfxType))
                {
                    continue;
                }
                if (simulated >= PrefabVfxMaxComponents)
                {
                    break;
                }
                simulated++;
                try
                {
                    if (seedProperty != null && seedProperty.CanWrite)
                    {
                        seedProperty.SetValue(component, PrefabVfxSeed, null);
                    }
                    if (reinitMethod != null)
                    {
                        reinitMethod.Invoke(component, null);
                    }
                    if (simulateMethod != null && simulateArgs != null)
                    {
                        simulateMethod.Invoke(component, simulateArgs);
                    }
                    if (pauseProperty != null && pauseProperty.CanWrite)
                    {
                        pauseProperty.SetValue(component, true, null);
                    }
                }
                catch (System.Exception exception)
                {
                    if (!s_State.VfxPreRollWarned)
                    {
                        s_State.VfxPreRollWarned = true;
                        Debug.LogWarning(k_LogPrefix + "VFX pre-roll failed: " + exception.Message);
                    }
                }
            }
        }

        /// <summary>
        /// True when any ancestor of child (up to and excluding root) carries
        /// a VisualEffect, meaning the ancestor's simulate block already
        /// advanced this effect. Mirrors HasParticleSystemAncestor. Null-safe.
        /// Plain parent walk with GetComponent(Type): no LINQ, no closures.
        /// </summary>
        internal static bool HasVisualEffectAncestor(
            Transform child,
            Transform root,
            System.Type vfxType
        )
        {
            if (child == null || root == null || vfxType == null || child == root)
            {
                return false;
            }
            Transform current = child.parent;
            while (current != null)
            {
                if (current.GetComponent(vfxType) != null)
                {
                    return true;
                }
                if (current == root)
                {
                    break;
                }
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// True when VFX pre-roll cannot render on this machine or project:
        /// Built-in RP (no currentRenderPipeline, VFX Graph needs SRP plus
        /// compute) or missing compute-shader support (OpenGL ES targets).
        /// Callers skip silently apart from the once-per-session log.
        /// </summary>
        private static bool IsVfxPreRollUnsupported(out string reason)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                reason = "Built-in RP has no SRP compute path";
                return true;
            }
            if (!SystemInfo.supportsComputeShaders)
            {
                reason = "compute shaders unsupported (e.g. OpenGL ES)";
                return true;
            }
            reason = null;
            return false;
        }

        /// <summary>
        /// Once-per-session skip log. First unsupported environment logs one
        /// warning; later skips stay silent so batch captures never spam.
        /// </summary>
        private static void LogVfxSkipOnce(string reason)
        {
            if (s_State.VfxSkipLogged)
            {
                return;
            }
            s_State.VfxSkipLogged = true;
            Debug.LogWarning(k_LogPrefix + "VFX pre-roll skipped (" + reason + ").");
        }

        /// <summary>
        /// Resolves seed/pause/Reinit/Simulate handles once per session via
        /// direct GetProperty/GetMethod lookups (no GetMethods enumeration,
        /// no LINQ, no closures) and caches them on the shared state.
        /// </summary>
        private static void EnsureVfxReflectionCached(System.Type vfxType)
        {
            if (s_State.VfxReflectionResolved || vfxType == null)
            {
                return;
            }
            try
            {
                s_State.VfxSeedProperty = vfxType.GetProperty("startSeed");
                s_State.VfxPauseProperty = vfxType.GetProperty("pause");
                s_State.VfxReinitMethod = vfxType.GetMethod("Reinit", System.Type.EmptyTypes);
                s_State.VfxSimulateMethod = FindVfxSimulateMethod(vfxType);
            }
            catch (System.Exception)
            {
                s_State.VfxSeedProperty = null;
                s_State.VfxPauseProperty = null;
                s_State.VfxReinitMethod = null;
                s_State.VfxSimulateMethod = null;
            }
            s_State.VfxReflectionResolved = true;
        }

        /// <summary>
        /// Resolves the two-argument VisualEffect.Simulate overload
        /// (float deltaTime plus uint/int step count) when present.
        /// Direct overload lookups only: no method enumeration, no LINQ.
        /// Returns null when the signature is unavailable.
        /// </summary>
        private static System.Reflection.MethodInfo FindVfxSimulateMethod(System.Type vfxType)
        {
            if (vfxType == null)
            {
                return null;
            }
            System.Reflection.MethodInfo uintOverload = vfxType.GetMethod(
                "Simulate",
                new System.Type[] { typeof(float), typeof(uint) }
            );
            if (uintOverload != null)
            {
                return uintOverload;
            }
            return vfxType.GetMethod("Simulate", new System.Type[] { typeof(float), typeof(int) });
        }

        /// <summary>
        /// Builds the Simulate argument pair for the resolved overload from
        /// a shared step count (round(time/stepDelta) clamped 1..300 by the
        /// caller). Returns null when the overload is unknown.
        /// </summary>
        private static object[] BuildVfxSimulateArgs(
            System.Reflection.MethodInfo simulateMethod,
            int steps
        )
        {
            if (simulateMethod == null)
            {
                return null;
            }
            System.Reflection.ParameterInfo[] parameters = simulateMethod.GetParameters();
            if (parameters == null || parameters.Length != 2)
            {
                return null;
            }
            int clampedSteps = Mathf.Clamp(steps, 1, 300);
            if (parameters[1].ParameterType == typeof(uint))
            {
                return new object[] { PrefabVfxStepDelta, (uint)clampedSteps };
            }
            if (parameters[1].ParameterType == typeof(int))
            {
                return new object[] { PrefabVfxStepDelta, clampedSteps };
            }
            return null;
        }

        /// <summary>
        /// Pure particle-slider visibility gate shared by the window and
        /// tests: the Particle Time slider shows only for a prefab target
        /// that contains particles. Static for EditMode tests.
        /// </summary>
        internal static bool ShouldShowParticleSlider(bool hasPrefabTarget, bool prefabHasParticles)
        {
            return hasPrefabTarget && prefabHasParticles;
        }

        /// <summary>
        /// True when the prefab asset at the given path contains a
        /// ParticleSystem or VisualEffect under its root. Read-only: the
        /// asset loads without instantiating, so the open scene is never
        /// dirtied and no Undo entry is recorded. Version-tolerant: the
        /// VisualEffect check resolves via the cached FindVisualEffectType
        /// reflection, so an absent VFX Graph package means
        /// ParticleSystem-only with no hard dependency. Exception-contained,
        /// never throws. No LINQ, no closures.
        /// </summary>
        internal static bool PrefabHasParticles(string prefabAssetPath)
        {
            if (!IsPrefabAssetPath(prefabAssetPath))
            {
                return false;
            }
            GameObject prefab = null;
            try
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            }
            catch (System.Exception)
            {
                return false;
            }
            return PrefabHasParticles(prefab);
        }

        /// <summary>
        /// True when the given prefab root holds a ParticleSystem or
        /// VisualEffect anywhere in its subtree (including inactive).
        /// Read-only GetComponentsInChildren probe: no instantiation, no
        /// Undo, no scene mutation. Null-safe, exception-contained.
        /// </summary>
        internal static bool PrefabHasParticles(GameObject prefabRoot)
        {
            if (prefabRoot == null)
            {
                return false;
            }
            try
            {
                ParticleSystem[] systems = prefabRoot.GetComponentsInChildren<ParticleSystem>(true);
                if (systems != null && systems.Length > 0)
                {
                    return true;
                }
                System.Type vfxType = FindVisualEffectType();
                if (vfxType == null)
                {
                    return false;
                }
                Component[] effects = prefabRoot.GetComponentsInChildren(vfxType, true);
                return effects != null && effects.Length > 0;
            }
            catch (System.Exception)
            {
                return false;
            }
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
            // Test override clears on any phase; RecordUndo restores on
            // ExitingEditMode when idle only (never mid-capture).
            s_State.PostProcessingAvailableOverride = null;
            s_State.Renderer2DOnlyOverride = null;
            if (change != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }
            if (UniThumbGuard.IsGenerating)
            {
                return;
            }
            s_State.RecordUndo = true;
        }

        private static CaptureResult CaptureCore(
            int width,
            int height,
            CaptureSettings settings,
            Bounds? prefabBounds,
            ScanCache scan = null,
            PrefabFallbackLight? prefabFallbackOverride = null,
            bool prefabFallbackMaterialized = false,
            GameObject prefabUiRoot = null,
            int prefabIsolationLayer = -1
        )
        {
            var result = new CaptureResult { Success = false };

            if (scan == null)
            {
                scan = BeginScan();
            }

            List<LightSnapshot> lightSnapshots = null;
            List<Light2DSnapshot> light2DSnapshots = null;
            List<Light2DSnapshot> existingLight2DSnapshots = null;
            LightSnapshot3D light3DSnapshot = null;
            List<LightSnapshot3D> extraDirectionalSnapshots = null;
            RenderTexture rt = null;
            GameObject tempGo = null;
            Camera cam = null;
            UiCaptureSession uiSession = null;
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

            // Snapshot the undo group before any scene mutations (canvas
            // property switches, light disable). Reverting to this group at
            // the end undoes every recorded change so the scene returns to
            // its pre-capture dirty state -- critical for the batch pump
            // which opens scenes sequentially and must not trigger
            // SaveCurrentModifiedScenesIfUserWantsTo popups.
            // IncrementCurrentGroup isolates the capture mutations from any
            // user action that shares the current group, so
            // RevertAllDownToGroup does not revert that action.
            Undo.IncrementCurrentGroup();
            int preCaptureUndoGroup = Undo.GetCurrentGroup();
            try
            {
                tempGo = new GameObject(k_TempCameraName);
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                cam = tempGo.AddComponent<Camera>();

                // The ortho fit formula reads camera.aspect before targetTexture
                // is assigned below; targetTexture re-derives the aspect at render
                // time, so this early set only feeds the fit calculation.
                cam.aspect = width / (float)height;

                if (prefabUiRoot != null && IsOverlayOnlyPrefab(prefabUiRoot, settings.layerMask))
                {
                    // Overlay-only prefab path: screen-space ortho fill with
                    // fixed clip planes. Skips the orbit distance and the
                    // synthetic fallback bounds; the fixed frustum plus the
                    // RT-proportional canvas scale (density only) holds a
                    // constant frame fraction at any resolution.
                    ApplyOverlayOnlyFraming(cam, prefabUiRoot, width, height);
                }
                else if (prefabUiRoot != null)
                {
                    // Prefab path: frame the instance subtree only, never the
                    // open scene contents. Bounds-less prefabs (particle/VFX
                    // with zero-extent bounds skipped above) use the shared
                    // empty fallback (origin, radius 5) matching the live
                    // preview so batch and preview pixels agree.
                    if (prefabBounds.HasValue)
                    {
                        ApplyOrbitTransformToBounds(
                            cam,
                            settings,
                            prefabBounds.Value.center,
                            Mathf.Max(prefabBounds.Value.extents.magnitude, 0.01f),
                            prefabBounds.Value.extents,
                            true,
                            true
                        );
                    }
                    else
                    {
                        ApplyOrbitTransformToBounds(
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
                else if (settings.UseSceneViewAngle)
                {
                    if (!TryCopyFromSceneView(cam, settings))
                    {
                        ApplyOrbitTransform(cam, settings, scan);
                    }
                }
                else
                {
                    ApplyOrbitTransform(cam, settings, scan);
                }

                rt = CreateCaptureTarget(width, height, settings, scan);
                cam.targetTexture = rt;
                cam.clearFlags =
                    settings.BackgroundMode == BackgroundMode.Skybox
                        ? CameraClearFlags.Skybox
                        : CameraClearFlags.SolidColor;
                cam.backgroundColor = EffectiveClearColor(settings);
                cam.cullingMask = settings.layerMask;
                // Prefab path with layer isolation: the hidden clone subtree
                // sits on a scene-unused layer, so the temp camera renders
                // that layer only and scene geometry cannot leak even where
                // flag-based isolation misses. Scene captures and fail-open
                // (-1) keep the settings mask.
                if (prefabUiRoot != null && prefabIsolationLayer >= 0 && prefabIsolationLayer <= 31)
                {
                    cam.cullingMask = 1 << prefabIsolationLayer;
                }

                // Add URP camera data so the correct renderer is used
                // (e.g. Renderer2D for 2D scenes). When UseSceneViewAngle is
                // on, copy the Scene View camera's renderer index and PP state
                // to match its rendering pipeline exactly. Prefab captures
                // always use orbit framing, but the camera still copies the
                // Scene View renderer when one is available (the helper falls
                // back to generic data otherwise): a generic default can
                // point at the wrong renderer and lose the sky or lights.
                if (settings.UseSceneViewAngle || prefabUiRoot != null)
                {
                    TryEnsureUrpCameraDataFromSceneView(cam);
                }
                else
                {
                    TryEnsureUrpCameraData(cam);
                }

                // Light3D needs a 3D renderer (Renderer2D ignores standard
                // Light components). Override the renderer index when Light3D
                // is active so the directional light is actually rendered.
                // Renderer2D-only pipelines skip every 3D feature: no switch
                // attempt (the bridge would warn), no temp 3D light below.
                bool renderer2DOnly = PipelineHasOnly2DRenderers();
                if (ShouldApplyLight3D(settings.Light2DMode, renderer2DOnly))
                {
                    TrySwitchTo3DCameraRenderer(cam);
                }

                // Snapshot directional state on EVERY pass (regardless of
                // lighting mode) BEFORE DisableAllLights: Light3D mode reuses
                // the primary afterwards (DisableAllLights sets enabled=false,
                // hiding it from TryFindDirectionalLight), while None/Light2D
                // passes mutate nothing but still run the shared restore below
                // so an escaped mutation can never persist.
                LightSnapshot3D preDisableSnapshot = SnapshotLight3D(TryFindDirectionalLight(scan));
                Light skip = null;
                if (preDisableSnapshot != null)
                {
                    skip = preDisableSnapshot.Light;
                }
                extraDirectionalSnapshots = SnapshotOtherDirectionalRotations(skip, scan);

                if (settings.UseLightingOverride)
                {
                    lightSnapshots = DisableAllLights(scan);
                    // The override must also kill 2D lights: Light2D is a
                    // separate component from Light and is not covered by
                    // DisableAllLights. When Light2DMode is Light2D the temp
                    // light is created after this, so the scene still gets lit.
                    light2DSnapshots = DisableAllLight2Ds(scan);
                }

                if (settings.Light2DMode == LightingMode.Light2D)
                {
                    // Disable existing Global Light2Ds to prevent the
                    // Renderer2D multiple-global-light error.
                    existingLight2DSnapshots = DisableExistingGlobalLight2Ds(scan);
                    tempLight2D = CreateTempLight2D(
                        settings.Light2DIntensity,
                        settings.Light2DSortingLayerIds
                    );
                }

                if (settings.Light2DMode == LightingMode.Light3D && renderer2DOnly)
                {
                    // Renderer2D-only downgrade: 3D features stay off (no
                    // directional reuse, no temp directional, no logs). A
                    // Global Light2D key keeps lit 2D content succeeding;
                    // null temp means ambient-only (warn-only placeholder
                    // downstream, never fail-closed).
                    existingLight2DSnapshots = DisableExistingGlobalLight2Ds(scan);
                    tempLight2D = CreateTempLight2D(
                        settings.Light2DIntensity,
                        settings.Light2DSortingLayerIds
                    );
                }

                if (ShouldApplyLight3D(settings.Light2DMode, renderer2DOnly))
                {
                    // Use the pre-disable snapshot when available (override
                    // already disabled it); otherwise search for an active
                    // directional. ResolveLight3DIntensity maps the UI value
                    // (0-5) to HDRP lux scale when the project uses HDRP.
                    Light existing = null;
                    if (preDisableSnapshot != null)
                    {
                        existing = preDisableSnapshot.Light;
                        light3DSnapshot = preDisableSnapshot;
                    }
                    else
                    {
                        existing = TryFindDirectionalLight(scan);
                        if (existing != null)
                        {
                            light3DSnapshot = SnapshotLight3D(existing);
                        }
                    }
                    // Prefab captures route the explicit Light3D key through
                    // the shared prefab intensity; scene captures keep the
                    // scene value. Clamp-then-resolve order holds on both.
                    float light3DIntensityValue =
                        prefabUiRoot != null
                            ? settings.Light3DPrefabIntensity
                            : settings.Light3DIntensity;
                    if (existing != null)
                    {
                        if (s_State.RecordUndo)
                        {
                            Undo.RecordObject(existing, "UniThumb capture");
                            Undo.RecordObject(existing.gameObject, "UniThumb capture");
                        }
                        existing.enabled = true;
                        existing.intensity = ResolveLight3DIntensity(
                            ClampLight3DIntensity(light3DIntensityValue)
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
                        tempLight3D = CreateTempDirectionalLight(
                            ResolveLight3DIntensity(ClampLight3DIntensity(light3DIntensityValue)),
                            settings.Light3DShadows,
                            settings.Light3DColor,
                            settings.Light3DYaw,
                            settings.Light3DPitch
                        );
                    }
                }

                // Prefab-only fallback: mode None adds no lights of its own
                // and the isolate step hides geometry (not lights), so an
                // empty scene (or an active lighting override) leaves the
                // prefab unlit. The decision comes from the caller when it
                // could see the live pre-disable scene state (CapturePrefab
                // resolves before DisableAllLights); otherwise it is
                // resolved here. Either way every None branch ends with one
                // prefab-scoped key (TempLight3D for perspective/3D content,
                // TempLight2D for sprite-only ortho; TempLight2D for every
                // prefab on Renderer2D-only pipelines where 3D is off).
                // Scene captures (no prefabUiRoot) are untouched. Temp objects
                // are destroyed in finally.
                PrefabFallbackLight prefabFallback;
                if (prefabFallbackOverride.HasValue)
                {
                    prefabFallback = prefabFallbackOverride.Value;
                }
                else
                {
                    prefabFallback = ResolvePrefabFallbackLight(
                        settings.Light2DMode,
                        prefabUiRoot != null,
                        settings.orthographic,
                        settings.UseLightingOverride,
                        preDisableSnapshot != null,
                        true,
                        false,
                        renderer2DOnly
                    );
                }
                // Prefab with its own key light (fallback None) on a
                // perspective capture: the camera must sit on a 3D URP
                // renderer before rendering, or the own directional key is
                // ignored (Renderer2D skips standard Lights) and the prefab
                // falls back to ambient only. TempLight3D is handled by
                // EnsurePrefabFallbackLights below (no Renderer2D-only
                // downgrade warning anymore: 2D-only never reaches for a 3D
                // renderer); explicit lighting modes keep their own renderer
                // handling above and are untouched here.
                if (
                    prefabUiRoot != null
                    && !settings.orthographic
                    && settings.Light2DMode == LightingMode.None
                    && prefabFallback == PrefabFallbackLight.None
                    && !renderer2DOnly
                )
                {
                    TrySwitchTo3DCameraRenderer(cam);
                }
                bool prefabTempsCreated = false;
                if (prefabFallbackMaterialized)
                {
                    // Downscale-retry recursion: the outer scope's temps
                    // still light the scene. Only point this new camera at
                    // the 3D renderer; never add a second set of temps
                    // (double-light). Renderer2D-only never switches.
                    if (prefabFallback == PrefabFallbackLight.TempLight3D && !renderer2DOnly)
                    {
                        TrySwitchTo3DCameraRenderer(cam);
                    }
                }
                else if (prefabFallback != PrefabFallbackLight.None)
                {
                    GameObject fallbackLight2D;
                    GameObject fallbackLight3D;
                    List<Light2DSnapshot> fallbackGlobals;
                    EnsurePrefabFallbackLights(
                        prefabFallback,
                        cam,
                        settings,
                        scan,
                        out fallbackLight2D,
                        out fallbackLight3D,
                        out fallbackGlobals
                    );
                    tempLight2D = fallbackLight2D;
                    tempLight3D = fallbackLight3D;
                    if (fallbackGlobals != null)
                    {
                        // Mode None guarantees existingLight2DSnapshots is
                        // still null here (the Light2D-mode branch above only
                        // runs for explicit Light2D, which forces None).
                        existingLight2DSnapshots = fallbackGlobals;
                    }
                    prefabTempsCreated = true;
                }

                if (IsHdrpPipeline())
                {
                    // HDRP needs PP frame settings ON for tonemapping
                    // (Exposure) and scene volumes to work. Without it the
                    // entire post-processing pass is skipped and the output
                    // is dark raw HDR with a black sky.
                    TryEnablePostProcessing(cam);

                    // Always create a high-priority Exposure override volume
                    // so the EV slider works regardless of PP toggle state.
                    tempHdrpExposureOverride = TryCreateHdrpExposureOverrideVolume(
                        out tempHdrpExposureProfile
                    );

                    // PP off and no scene volumes: inject the sky baseline so
                    // the capture still gets a sky background instead of black.
                    if (!ShouldApplyPostProcessing(settings, scan))
                    {
                        tempHdrpSkyBaseline = TryCreateHdrpSkyBaselineVolume(
                            out tempHdrpSkyProfile
                        );
                    }

                    if (ShouldApplyPostProcessing(settings, scan))
                    {
                        // Main PP volume: handles custom profiles and project
                        // profiles. Returns null when scene has volumes (they
                        // are used as-is).
                        tempPostFxVolume = TryCreateTempPostProcessingVolume(
                            settings,
                            scan,
                            out tempClonedProfile
                        );

                        // DOF/MotionBlur override: disables effects that break
                        // single-frame captures (wrong focus distance, motion
                        // smear).
                        tempHdrpCaptureOverride = TryCreateHdrpCaptureOverrideVolume(
                            out tempHdrpCaptureProfile
                        );
                    }
                }
                else if (ShouldApplyPostProcessing(settings, scan))
                {
                    // URP skips Volume post-processing in offscreen renders unless the
                    // camera carries UniversalAdditionalCameraData with
                    // renderPostProcessing=true. UACD is added whenever PP is
                    // requested AND at least one Volume exists in the scene:
                    // SubmitRenderRequest needs it too (M9).
                    TryEnablePostProcessing(cam);
                    // Ensure a Volume exists for the capture: an explicitly assigned
                    // profile always injects one; otherwise one is injected only when
                    // the scene has no Volume. Destroyed in finally (non-persistent).
                    tempPostFxVolume = TryCreateTempPostProcessingVolume(
                        settings,
                        scan,
                        out tempClonedProfile
                    );
                }

                if (IsSceneViewUiCompositeEligible(settings))
                {
                    // Composite mode: overlay canvases stay untouched during the
                    // scene pass; UI renders in the wide pass inside
                    // CompositeSceneViewUi after the image check.
                    uiSession = null;
                }
                else
                {
                    // Scene path (no prefabUiRoot) keeps the gated legacy
                    // session byte-identical (S3): CaptureUi on retargets
                    // every overlay canvas, off leaves uiSession null for
                    // baseline pixels. Prefab captures (prefabUiRoot set,
                    // UseSceneViewAngle false, never composite-eligible)
                    // always use the scoped prefab session instead, even
                    // when framing fell back to null (bounds-less UI
                    // prefabs): the instance carries HideAndDontSave, which
                    // FindObjectsOfType skips, so the scene-wide session
                    // would never retarget the instance canvas and the
                    // render would come back uniform-background. The scoped
                    // session finds it via GetComponentsInChildren:
                    // ScreenSpaceCamera canvases rebind worldCamera to the
                    // temp camera and Overlay canvases retarget under the
                    // instance subtree only (scene canvases stay culled and
                    // untouched). Overlay-converted canvases take the raw RT
                    // base with no k_PrefabRtScaleMin floor: the floor broke
                    // proportionality (floored 0.25@128 vs raw 0.16, 0.4578
                    // vs 0.1875 frame-fraction; floored overflow at 16 reads
                    // uniform and fails), while the raw base holds an
                    // identical fraction at every size. Camera canvases keep
                    // the floored base. CaptureUi off leaves uiSession null
                    // on both paths and reproduces baseline pixels.
                    if (!settings.CaptureUi)
                    {
                        uiSession = null;
                    }
                    else if (prefabUiRoot != null)
                    {
                        uiSession = UiCaptureSession.BeginPrefab(
                            prefabUiRoot,
                            cam,
                            settings.UiScale
                        );
                    }
                    else
                    {
                        uiSession = UiCaptureSession.Begin(cam, settings.UiScale);
                    }
                }

                bool ppOrSkyboxActive =
                    EffectiveWantPostProcessing(settings)
                    || settings.BackgroundMode == BackgroundMode.Skybox;

                bool usedSubmitFallback = false;
                Texture2D pixels;
                ImageCheck check;

                if (EffectiveWantPostProcessing(settings))
                {
                    // Synchronous first: camera.Render() with
                    // UACD.renderPostProcessing=true finishes the render on the
                    // main thread before ReadPixels, so the readback cannot race
                    // a render-thread SubmitRenderRequest (cross-scene mixing).
                    // SubmitRenderRequest (full camera stack, async) is only a
                    // retry when the synchronous render looks suspicious.
                    cam.Render();
                    pixels = ReadBack(rt);
                    check = Inspect(
                        pixels,
                        settings.BackgroundColor,
                        true,
                        IsTransparentMode(settings)
                    );
                    if (check == ImageCheck.UniformOther)
                    {
                        Debug.LogWarningFormat(
                            k_LogPrefix
                                + "camera.Render() produced {0}; attempting SubmitRenderRequest fallback.",
                            Describe(check)
                        );
                        if (TrySubmitRenderRequest(cam, rt))
                        {
                            Texture2D retry = ReadBack(rt);
                            ImageCheck retryCheck = Inspect(
                                retry,
                                settings.BackgroundColor,
                                true,
                                IsTransparentMode(settings)
                            );
                            if (retryCheck == ImageCheck.Ok)
                            {
                                UnityEngine.Object.DestroyImmediate(pixels);
                                pixels = retry;
                                check = ImageCheck.Ok;
                                usedSubmitFallback = true;
                            }
                            else
                            {
                                UnityEngine.Object.DestroyImmediate(retry);
                                check = retryCheck;
                            }
                        }
                    }
                }
                else
                {
                    cam.Render();
                    pixels = ReadBack(rt);
                    check = Inspect(
                        pixels,
                        settings.BackgroundColor,
                        ppOrSkyboxActive,
                        IsTransparentMode(settings)
                    );

                    if (check == ImageCheck.UniformOther)
                    {
                        Debug.LogWarningFormat(
                            k_LogPrefix
                                + "camera.Render() produced {0}; attempting SubmitRenderRequest fallback.",
                            Describe(check)
                        );
                        if (TrySubmitRenderRequest(cam, rt))
                        {
                            Texture2D retry = ReadBack(rt);
                            ImageCheck retryCheck = Inspect(
                                retry,
                                settings.BackgroundColor,
                                ppOrSkyboxActive,
                                IsTransparentMode(settings)
                            );
                            if (retryCheck == ImageCheck.Ok)
                            {
                                UnityEngine.Object.DestroyImmediate(pixels);
                                pixels = retry;
                                check = ImageCheck.Ok;
                                usedSubmitFallback = true;
                            }
                            else
                            {
                                UnityEngine.Object.DestroyImmediate(retry);
                                check = retryCheck;
                            }
                        }
                    }
                }

                if (check != ImageCheck.Ok && ExceedsReliablePixels(width, height))
                {
                    // URP 17 RenderGraph (Unity 6) silently drops all geometry when rendering
                    // into a RenderTexture with ~2^24 total pixels (4096x4096 exactly). Retry
                    // at a safe size and upscale the result to the requested resolution.
                    int smallW;
                    int smallH;
                    if (uiSession != null)
                    {
                        uiSession.Dispose();
                        uiSession = null;
                    }
                    Texture2D upscaled = TryDownscaleRetry(
                        width,
                        height,
                        settings,
                        out smallW,
                        out smallH,
                        prefabBounds,
                        // Fresh sweep (null): the shared scan predates the
                        // outer temp lights, so reusing it would hide them
                        // and the retry would add a second set (double-light).
                        null,
                        prefabFallback,
                        prefabTempsCreated,
                        prefabUiRoot,
                        prefabIsolationLayer
                    );
                    if (upscaled != null)
                    {
                        UnityEngine.Object.DestroyImmediate(pixels);
                        result.Success = true;
                        result.PngBytes = upscaled.EncodeToPNG();
                        result.Warning =
                            "Rendered at "
                            + smallW
                            + "x"
                            + smallH
                            + " and upscaled to "
                            + width
                            + "x"
                            + height
                            + ": URP RenderGraph drops geometry in RenderTextures above ~16M total pixels.";
                        UnityEngine.Object.DestroyImmediate(upscaled);
                        return result;
                    }
                }

                int retargetedUiCanvases = uiSession != null ? uiSession.SwitchedCount : 0;
                if (
                    check == ImageCheck.UniformBackground
                    && prefabUiRoot != null
                    && retargetedUiCanvases > 0
                )
                {
                    // UI-only prefab acceptance: the scoped session retargeted
                    // prefab UI yet the frame came back uniform (empty canvas).
                    // The pixels are exactly what the prefab holds, so accept
                    // instead of returning the corrupt placeholder. Genuinely
                    // empty renders (zero retargeted canvases) still fail, and
                    // 3D corruption (UniformOther) is untouched. Scene path
                    // (no prefabUiRoot) keeps corrupt detection as-is.
                    check = ImageCheck.Ok;
                    result.Warning =
                        "Prefab UI rendered a uniform frame; accepted (retargeted canvas present).";
                }

                if (check == ImageCheck.Ok)
                {
                    result.Success = true;
                    if (IsSceneViewUiCompositeEligible(settings))
                    {
                        Texture2D composite = CompositeSceneViewUi(settings, pixels);
                        if (composite != pixels)
                        {
                            UnityEngine.Object.DestroyImmediate(pixels);
                            pixels = composite;
                        }
                    }
                    result.PngBytes = pixels.EncodeToPNG();
                    if (usedSubmitFallback)
                    {
                        result.Warning =
                            "Capture succeeded via SubmitRenderRequest fallback after camera.Render() produced a suspicious image.";
                    }
                    UnityEngine.Object.DestroyImmediate(pixels);
                }
                else
                {
                    result.Warning =
                        "Render produced "
                        + Describe(check)
                        + "; returning background placeholder instead of a corrupt PNG.";
                    result.PngBytes = CreatePlaceholderPng(width, height, settings.BackgroundColor);
                    UnityEngine.Object.DestroyImmediate(pixels);
                }
            }
            finally
            {
                if (uiSession != null)
                {
                    uiSession.Dispose();
                }
                RestoreLights(lightSnapshots);
                if (tempLight2D != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempLight2D);
                }
                // Restore extra rotations (plus bake/affect) before the temp
                // light is destroyed; the primary snapshot restore below
                // heals the Light3D light itself.
                RestoreDirectionalRotations(extraDirectionalSnapshots);
                if (tempLight3D != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempLight3D);
                }
                RestoreLight3D(light3DSnapshot);
                // Mode-path snapshots first: when both the lighting override
                // and Light2D mode ran, the mode path re-snapshot the already
                // disabled globals (WasEnabled=false); restoring the override
                // snapshots last returns the original enabled state.
                RestoreGlobalLight2Ds(existingLight2DSnapshots);
                RestoreGlobalLight2Ds(light2DSnapshots);
                if (cam != null)
                {
                    cam.targetTexture = null;
                }
                if (tempGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempGo);
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
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                // Revert every scene mutation (canvas property switches,
                // light disable) recorded since preCaptureUndoGroup so the
                // scene returns to its pre-capture dirty state. Without this
                // the batch pump's TryActivateScene triggers
                // SaveCurrentModifiedScenesIfUserWantsTo on every scene
                // switch because Unity's undo stack marks the scene dirty.
                Undo.RevertAllDownToGroup(preCaptureUndoGroup);
            }

            return result;
        }

        private static Texture2D TryDownscaleRetry(
            int width,
            int height,
            CaptureSettings settings,
            out int smallW,
            out int smallH,
            Bounds? prefabBounds = null,
            ScanCache scan = null,
            PrefabFallbackLight? prefabFallbackOverride = null,
            bool prefabFallbackMaterialized = false,
            GameObject prefabUiRoot = null,
            int prefabIsolationLayer = -1
        )
        {
            float scale = Mathf.Sqrt(k_MaxReliableRenderPixels / (float)(width * height));
            smallW = Mathf.Clamp(Mathf.RoundToInt(width * scale), k_MinResolution, width);
            smallH = Mathf.Clamp(Mathf.RoundToInt(height * scale), k_MinResolution, height);
            if (smallW >= width && smallH >= height)
            {
                return null;
            }

            // Fresh sweep (null): any passed-in scan predates the outer temp
            // lights, so reusing it would hide them and the retry would add a
            // second set (double-light). The nested capture takes its own
            // sweep including the outer temp lights. The resolved fallback
            // decision is forwarded with materialized=true so the nested
            // capture reuses the outer temps instead of adding its own.
            CaptureResult small = CaptureCore(
                smallW,
                smallH,
                settings,
                prefabBounds,
                null,
                prefabFallbackOverride,
                prefabFallbackMaterialized,
                prefabUiRoot,
                prefabIsolationLayer
            );
            if (!small.Success)
            {
                return null;
            }
            return Upscale(small.PngBytes, width, height);
        }

        private static Texture2D Upscale(byte[] png, int width, int height)
        {
            var smallTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            smallTex.filterMode = FilterMode.Bilinear;
            if (!smallTex.LoadImage(png))
            {
                UnityEngine.Object.DestroyImmediate(smallTex);
                return null;
            }

            RenderTexture rt = null;
            RenderTexture previousActive = null;
            Texture2D bigTex = null;
            try
            {
                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                rt.Create();
                previousActive = RenderTexture.active;
                RenderTexture.active = rt;
                Graphics.Blit(smallTex, rt);
                bigTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                bigTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                bigTex.Apply();
                return bigTex;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                UnityEngine.Object.DestroyImmediate(smallTex);
            }
        }

        /// <summary>
        /// Single SceneView-angle copy source, shared with the live preview.
        /// Returns false when no SceneView camera exists, on projection
        /// mismatch (ortho request with perspective SceneView, perspective
        /// request with orthographic SceneView), or on degenerate clip/lens
        /// (zero/inverted planes, non-positive size/fov); callers fall back
        /// to ApplyOrbitTransform.
        /// </summary>
        internal static bool TryCopyFromSceneView(Camera cam, CaptureSettings settings)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return false;
            }
            Camera svCam = sv.camera;
            if (settings.orthographic && !svCam.orthographic)
            {
                return false;
            }
            if (!settings.orthographic && svCam.orthographic)
            {
                return false;
            }
            float srcNear = svCam.nearClipPlane;
            float srcFar = svCam.farClipPlane;
            if (srcNear <= 0f || srcFar <= 0f || srcFar <= srcNear)
            {
                return false;
            }
            if (svCam.orthographic)
            {
                float srcSize = svCam.orthographicSize;
                if (srcSize <= 0f || float.IsNaN(srcSize) || float.IsInfinity(srcSize))
                {
                    return false;
                }
            }
            else
            {
                float srcFov = svCam.fieldOfView;
                if (srcFov <= 0f || float.IsNaN(srcFov) || float.IsInfinity(srcFov))
                {
                    return false;
                }
            }
            cam.transform.SetPositionAndRotation(
                svCam.transform.position,
                svCam.transform.rotation
            );
            if (settings.orthographic)
            {
                // Ortho + SceneView angle: copy the SceneView camera's
                // orthographicSize when it is orthographic; a perspective
                // SceneView camera falls back to the orbit fit-size framing
                // (ApplyOrbitTransform).
                cam.orthographic = true;
                cam.orthographicSize = svCam.orthographicSize;
            }
            else
            {
                cam.orthographic = false;
                cam.fieldOfView = svCam.fieldOfView;
            }
            cam.nearClipPlane = Mathf.Max(0.01f, svCam.nearClipPlane);
            cam.farClipPlane = Mathf.Max(k_MinFarClip, svCam.farClipPlane);
            return true;
        }

        /// <summary>
        /// Single orbit-framing source, shared with the live preview so both
        /// cannot diverge.
        /// warnOnEmptyOrthoBounds: capture logs the empty-bounds ortho
        /// fallback; the live preview passes false because it repaints on
        /// every hierarchy change and must not spam the console.
        /// </summary>
        internal static void ApplyOrbitTransform(
            Camera cam,
            CaptureSettings settings,
            bool warnOnEmptyOrthoBounds = true
        )
        {
            ApplyOrbitTransform(cam, settings, null, warnOnEmptyOrthoBounds);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: bounds come from the shared
        /// snapshot instead of issuing dedicated native sweeps.
        /// </summary>
        internal static void ApplyOrbitTransform(
            Camera cam,
            CaptureSettings settings,
            ScanCache scan,
            bool warnOnEmptyOrthoBounds = true
        )
        {
            Vector3 center = Vector3.zero;
            float radius = 5f;
            Bounds bounds;
            bool hasBounds = TryGetSceneBounds(settings.layerMask, scan, out bounds);
            if (hasBounds)
            {
                center = bounds.center;
                radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            }

            ApplyOrbitTransformToBounds(
                cam,
                settings,
                center,
                radius,
                hasBounds ? bounds.extents : Vector3.zero,
                hasBounds,
                warnOnEmptyOrthoBounds
            );
        }

        /// <summary>
        /// Orbit-framing math shared by the scene path (bounds from the scene
        /// sweep) and the prefab path (bounds from the instance subtree).
        /// Pure camera transform: identical output for identical inputs.
        /// orthoExtents feeds the orthographic half-height fit only (the
        /// perspective distance comes from radius); pass bounds.extents with
        /// hasBounds, Vector3.zero for the empty-bounds fallback.
        /// </summary>
        internal static void ApplyOrbitTransformToBounds(
            Camera cam,
            CaptureSettings settings,
            Vector3 center,
            float radius,
            Vector3 orthoExtents,
            bool hasBounds,
            bool warnOnEmptyOrthoBounds = true
        )
        {
            float fitFactor = Mathf.Max(settings.FitFactor, 0.5f);
            float orbitDistanceMultiplier = Mathf.Clamp(
                settings.orbitDistanceMultiplier,
                0.1f,
                10f
            );
            float orbitFov = Mathf.Clamp(settings.OrbitFov, 30f, 120f);
            float distance = radius / Mathf.Tan(orbitFov * 0.5f * Mathf.Deg2Rad) * fitFactor;
            distance *= orbitDistanceMultiplier;

            cam.orthographic = settings.orthographic;
            if (settings.orthographic)
            {
                // Orthographic (2D): fit framing is driven by the larger of the
                // bounds half-height and half-width (half-width via aspect), so
                // the orbit distance only places the camera (near-plane
                // clipping). Empty bounds fall back to an origin camera with a
                // fixed half-height.
                cam.orthographicSize = hasBounds
                    ? Mathf.Max(orthoExtents.y, orthoExtents.x / cam.aspect)
                        * orbitDistanceMultiplier
                    : 5f * orbitDistanceMultiplier;
                if (!hasBounds && warnOnEmptyOrthoBounds)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "No renderers in the layer mask; orthographic capture fell back to origin framing (orthographicSize 5)."
                    );
                }
            }
            else
            {
                cam.fieldOfView = orbitFov;
            }

            float yaw = settings.OrbitYaw * Mathf.Deg2Rad;
            float pitch =
                Mathf.Clamp(settings.OrbitPitch, -k_MaxOrbitPitch, k_MaxOrbitPitch) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(
                Mathf.Cos(pitch) * Mathf.Sin(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Cos(yaw)
            );
            cam.transform.position = center + dir * distance;
            cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
            cam.farClipPlane = Mathf.Max(k_MinFarClip, distance + radius * 4f);
        }

        /// <summary>
        /// Attempts to compute scene bounds from TilemapRenderers only.
        /// Returns true with valid bounds when at least one TilemapRenderer
        /// with a paired Tilemap contributes non-empty cellBounds.
        /// Returns false when zero TilemapRenderers exist or none contribute.
        /// </summary>
        internal static bool TryGetTilemapOnlyBounds(LayerMask layerMask, out Bounds bounds)
        {
            return TryGetTilemapOnlyBounds(layerMask, null, out bounds);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: TilemapRenderers are
        /// filtered from the shared renderer snapshot (TilemapRenderer
        /// derives from Renderer) instead of issuing a dedicated sweep.
        /// </summary>
        internal static bool TryGetTilemapOnlyBounds(
            LayerMask layerMask,
            ScanCache scan,
            out Bounds bounds
        )
        {
            if (scan != null && scan.Renderers != null)
            {
                return TilemapOnlyBoundsCore(scan.Renderers, layerMask, out bounds);
            }
#if UNITY_6000_0_OR_NEWER
            TilemapRenderer[] tilemapRenderers =
                UnityEngine.Object.FindObjectsByType<TilemapRenderer>();
#else
            TilemapRenderer[] tilemapRenderers =
                UnityEngine.Object.FindObjectsOfType<TilemapRenderer>();
#endif
            return TilemapOnlyBoundsCore(tilemapRenderers, layerMask, out bounds);
        }

        private static bool TilemapOnlyBoundsCore(
            Renderer[] renderers,
            LayerMask layerMask,
            out Bounds bounds
        )
        {
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                TilemapRenderer tmr = renderer as TilemapRenderer;
                if (tmr == null)
                {
                    continue;
                }
                if (!tmr.enabled || !tmr.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if ((layerMask.value & (1 << tmr.gameObject.layer)) == 0)
                {
                    continue;
                }
                Tilemap tilemap = tmr.GetComponent<Tilemap>();
                if (tilemap == null)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "TilemapRenderer on '"
                            + tmr.gameObject.name
                            + "' has no paired Tilemap component; skipping."
                    );
                    continue;
                }
                BoundsInt cellBounds = tilemap.cellBounds;
                Vector3 worldMin = tilemap.CellToWorld(cellBounds.min);
                Vector3 worldMax = tilemap.CellToWorld(cellBounds.max);
                Vector3 tileSize = worldMax - worldMin;
                if (tileSize.sqrMagnitude < 0.001f)
                {
                    continue;
                }
                Bounds tileBounds = new Bounds((worldMin + worldMax) * 0.5f, tileSize);
                if (!any)
                {
                    bounds = tileBounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(tileBounds);
                }
            }
            return any;
        }

        /// <summary>
        /// Single scene-bounds source (renderers + tilemap cellBounds) for
        /// orbit framing, shared with the live preview.
        /// </summary>
        internal static bool TryGetSceneBounds(LayerMask layerMask, out Bounds bounds)
        {
            return TryGetSceneBounds(layerMask, null, out bounds);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: the renderer fallback
        /// sweeps the shared snapshot instead of issuing a dedicated sweep.
        /// </summary>
        internal static bool TryGetSceneBounds(
            LayerMask layerMask,
            ScanCache scan,
            out Bounds bounds
        )
        {
            // When TilemapRenderers exist, restrict bounds to tilemap cellBounds
            // only â€” non-tilemap renderers (particles, sprites, mesh, etc.)
            // must not contaminate the framing.
            if (TryGetTilemapOnlyBounds(layerMask, scan, out bounds))
            {
                return true;
            }

            // Zero tilemaps â€” fall through to the legacy full Renderer sweep.
            Renderer[] renderers;
            if (scan != null && scan.Renderers != null)
            {
                renderers = scan.Renderers;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
#else
                renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            }
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if ((layerMask.value & (1 << renderer.gameObject.layer)) == 0)
                {
                    continue;
                }
                // Skip renderers with zero/near-zero extents (e.g.
                // ParticleSystemRenderer at origin) â€” encapsulating their
                // zero-size bounds pulls the center toward the origin and
                // inflates the framing well beyond the actual scene content.
                if (renderer.bounds.extents.sqrMagnitude < 0.001f)
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

        /// <summary>
        /// Hides every open-scene Renderer except the prefab instance subtree
        /// via forceRenderingOff (a non-serialized runtime flag: no scene
        /// dirtying, no Undo bookkeeping). Returns the switched list for
        /// RestoreIsolatedRenderers in the same finally block. The temp capture
        /// camera and UI overlay canvases are unaffected (created after, or not
        /// Renderers).
        /// </summary>
        internal static List<Renderer> IsolateInstanceRenderers(GameObject instance)
        {
            return IsolateInstanceRenderers(instance, null);
        }

        /// <summary>
        /// True when <paramref name="child" /> belongs to the prefab instance
        /// subtree rooted at <paramref name="root" />, INCLUDING the root
        /// itself. Transform.IsChildOf excludes self, so a bare IsChildOf
        /// check misclassifies components sitting directly on the prefab
        /// root (lights, volumes, renderers) as outside the subtree.
        /// Null-safe: null child or root returns false.
        /// </summary>
        internal static bool IsInPrefabSubtree(Transform child, Transform root)
        {
            if (child == null || root == null)
            {
                return false;
            }
            return child == root || child.IsChildOf(root);
        }

        /// <summary>
        /// Scan-aware variant used by CapturePrefab: isolates from the shared
        /// snapshot instead of issuing a dedicated sweep. Disabled renderers
        /// and renderers on inactive GameObjects are isolated too (a mid-pass
        /// re-enable by an edit-mode script must not leak them into the
        /// prefab shot); hiding them early is harmless since they render
        /// nothing either way. Only already-isolated renderers are skipped:
        /// their prior flag value is unknown (no snapshot), so clearing it
        /// on restore could steal another pass. Restored by
        /// RestoreIsolatedRenderers in the same finally block.
        /// </summary>
        internal static List<Renderer> IsolateInstanceRenderers(GameObject instance, ScanCache scan)
        {
            var isolated = new List<Renderer>();
            Renderer[] renderers;
            if (scan != null && scan.Renderers != null)
            {
                renderers = scan.Renderers;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
#else
                renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            }
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer.forceRenderingOff)
                {
                    continue;
                }
                if (instance != null && IsInPrefabSubtree(renderer.transform, instance.transform))
                {
                    continue;
                }
                renderer.forceRenderingOff = true;
                isolated.Add(renderer);
            }
            return isolated;
        }

        /// <summary>
        /// Restores forceRenderingOff flags set by IsolateInstanceRenderers.
        /// Null-safe; skips renderers destroyed mid-capture.
        /// </summary>
        internal static void RestoreIsolatedRenderers(List<Renderer> isolated)
        {
            if (isolated == null)
            {
                return;
            }
            foreach (Renderer renderer in isolated)
            {
                if (renderer != null)
                {
                    renderer.forceRenderingOff = false;
                }
            }
        }

        /// <summary>
        /// Culls every open-scene CanvasRenderer except the prefab instance
        /// subtree via CanvasRenderer.cull (a non-serialized runtime flag: no
        /// scene dirtying, no Undo bookkeeping, restored in finally).
        /// CanvasRenderer does not derive from Renderer, so
        /// IsolateInstanceRenderers misses it: open-scene world-space and
        /// ScreenSpaceCamera UI plus overlay canvases switched onto the
        /// capture camera by UiCaptureSession would otherwise leak into the
        /// prefab shot. Culled renderers stay culled even after the canvas
        /// switch, so this single pass covers both UI kinds. Returns the
        /// culled list for RestoreIsolatedCanvasRenderers in the same finally
        /// block. The prefab instance subtree (root-inclusive IsInPrefabSubtree)
        /// is never touched.
        /// </summary>
        internal static List<CanvasRenderer> IsolateInstanceCanvasRenderers(GameObject instance)
        {
            return IsolateInstanceCanvasRenderers(instance, null);
        }

        /// <summary>
        /// Scan-aware variant used by CapturePrefab: culls from the shared
        /// canvas-renderer snapshot instead of issuing a dedicated sweep.
        /// The snapshot is include-inactive, so canvas renderers on inactive
        /// GameObjects are culled too (a mid-pass activation must not leak
        /// them into the prefab shot).
        /// </summary>
        internal static List<CanvasRenderer> IsolateInstanceCanvasRenderers(
            GameObject instance,
            ScanCache scan
        )
        {
            var isolated = new List<CanvasRenderer>();
            CanvasRenderer[] renderers;
            if (scan != null && scan.CanvasRenderers != null)
            {
                renderers = scan.CanvasRenderers;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                renderers = UnityEngine.Object.FindObjectsByType<CanvasRenderer>();
#else
                renderers = UnityEngine.Object.FindObjectsOfType<CanvasRenderer>();
#endif
            }
            foreach (CanvasRenderer renderer in renderers)
            {
                if (renderer == null || renderer.cull)
                {
                    continue;
                }
                if (instance != null && IsInPrefabSubtree(renderer.transform, instance.transform))
                {
                    continue;
                }
                renderer.cull = true;
                isolated.Add(renderer);
            }
            return isolated;
        }

        /// <summary>
        /// Restores cull flags set by IsolateInstanceCanvasRenderers.
        /// Null-safe; skips renderers destroyed mid-capture.
        /// </summary>
        internal static void RestoreIsolatedCanvasRenderers(List<CanvasRenderer> isolated)
        {
            if (isolated == null)
            {
                return;
            }
            foreach (CanvasRenderer renderer in isolated)
            {
                if (renderer != null)
                {
                    renderer.cull = false;
                }
            }
        }

        /// <summary>
        /// Disables every open-scene Terrain except the prefab instance subtree
        /// (snapshot/restore of enabled in finally). Terrain is not a Renderer,
        /// so IsolateInstanceRenderers never matches it and scene terrain would
        /// otherwise render into the prefab shot. Direct enabled assignment with
        /// Undo.RecordObject when RecordUndo is on (mirrors DisableAllLights).
        /// Returns null when nothing was disabled. Null-safe.
        /// </summary>
        internal static List<TerrainSnapshot> IsolateInstanceTerrains(GameObject instance)
        {
            return IsolateInstanceTerrains(instance, null);
        }

        /// <summary>
        /// Scan-aware variant used by CapturePrefab: filters the shared
        /// component snapshot instead of issuing a dedicated sweep. Disabled
        /// scene Terrains are isolated too (snapshot/restore of enabled in
        /// finally makes this safe: a disabled Terrain renders nothing, and
        /// a mid-pass enable can no longer leak it into the prefab shot).
        /// </summary>
        internal static List<TerrainSnapshot> IsolateInstanceTerrains(
            GameObject instance,
            ScanCache scan
        )
        {
            Component[] all;
            if (scan != null && scan.Components != null)
            {
                all = scan.Components;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                all = UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Exclude);
#else
                all = UnityEngine.Object.FindObjectsOfType<Component>();
#endif
            }
            Transform instanceTransform = instance == null ? null : instance.transform;
            var snapshots = new List<TerrainSnapshot>();
            foreach (Component component in all)
            {
                if (component == null)
                {
                    continue;
                }
                Terrain terrain = component as Terrain;
                if (terrain == null)
                {
                    continue;
                }
                if (
                    instanceTransform != null
                    && IsInPrefabSubtree(terrain.transform, instanceTransform)
                )
                {
                    continue;
                }
                snapshots.Add(
                    new TerrainSnapshot { Terrain = terrain, WasEnabled = terrain.enabled }
                );
                if (s_State.RecordUndo)
                {
                    Undo.RecordObject(terrain, "UniThumb capture");
                }
                terrain.enabled = false;
            }
            return snapshots.Count > 0 ? snapshots : null;
        }

        /// <summary>
        /// Restores enabled states captured by IsolateInstanceTerrains.
        /// Null-safe; skips terrains destroyed mid-capture.
        /// </summary>
        internal static void RestoreIsolatedTerrains(List<TerrainSnapshot> isolated)
        {
            if (isolated == null)
            {
                return;
            }
            foreach (TerrainSnapshot snapshot in isolated)
            {
                if (snapshot == null || snapshot.Terrain == null)
                {
                    continue;
                }
                snapshot.Terrain.enabled = snapshot.WasEnabled;
            }
        }

        /// <summary>
        /// Disables every open-scene VisualEffect except the prefab instance
        /// subtree (snapshot/restore of enabled in finally). VisualEffect is not
        /// a Renderer, so IsolateInstanceRenderers never matches it; pre-roll
        /// (SimulatePrefabVfx) only advances the instance effects, it never
        /// hides scene effects. Type-resolved via FindVisualEffectType (no hard
        /// VFX package reference); null type returns null (fail-open).
        /// Returns null when nothing was disabled. Null-safe.
        /// </summary>
        internal static List<VisualEffectSnapshot> IsolateInstanceVfx(GameObject instance)
        {
            return IsolateInstanceVfx(instance, null);
        }

        /// <summary>
        /// Scan-aware variant used by CapturePrefab: filters the shared
        /// component snapshot instead of issuing a dedicated sweep. Disabled
        /// scene effects are isolated too (snapshot/restore of enabled in
        /// finally makes this safe: a disabled effect renders nothing, and
        /// a mid-pass enable can no longer leak it into the prefab shot).
        /// </summary>
        internal static List<VisualEffectSnapshot> IsolateInstanceVfx(
            GameObject instance,
            ScanCache scan
        )
        {
            System.Type vfxType = FindVisualEffectType();
            if (vfxType == null)
            {
                return null;
            }
            Component[] all;
            if (scan != null && scan.Components != null)
            {
                all = scan.Components;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                all = UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Exclude);
#else
                all = UnityEngine.Object.FindObjectsOfType<Component>();
#endif
            }
            Transform instanceTransform = instance == null ? null : instance.transform;
            var snapshots = new List<VisualEffectSnapshot>();
            foreach (Component component in all)
            {
                if (component == null || component.GetType() != vfxType)
                {
                    continue;
                }
                Behaviour behaviour = component as Behaviour;
                if (behaviour == null)
                {
                    continue;
                }
                if (
                    instanceTransform != null
                    && IsInPrefabSubtree(behaviour.transform, instanceTransform)
                )
                {
                    continue;
                }
                snapshots.Add(
                    new VisualEffectSnapshot { Effect = behaviour, WasEnabled = behaviour.enabled }
                );
                if (s_State.RecordUndo)
                {
                    Undo.RecordObject(behaviour, "UniThumb capture");
                }
                behaviour.enabled = false;
            }
            return snapshots.Count > 0 ? snapshots : null;
        }

        /// <summary>
        /// Restores enabled states captured by IsolateInstanceVfx.
        /// Null-safe; skips effects destroyed mid-capture.
        /// </summary>
        internal static void RestoreIsolatedVfx(List<VisualEffectSnapshot> isolated)
        {
            if (isolated == null)
            {
                return;
            }
            foreach (VisualEffectSnapshot snapshot in isolated)
            {
                if (snapshot == null || snapshot.Effect == null)
                {
                    continue;
                }
                snapshot.Effect.enabled = snapshot.WasEnabled;
            }
        }

        /// <summary>
        /// Sweeps hidden-flagged Renderers (HideAndDontSave preview objects and
        /// hidden scene objects) that FindObjectsByType skips, filtered to valid
        /// loaded scenes so prefab-asset contents never match. Disabled
        /// renderers are isolated too (a mid-pass enable must not leak them);
        /// the prefab instance subtree (itself HideAndDontSave) is exempt.
        /// Already-isolated renderers (forceRenderingOff set by
        /// IsolateInstanceRenderers) are skipped: their prior flag value is
        /// unknown, so clearing it on restore could steal another pass.
        /// Restored via RestoreIsolatedRenderers in the same finally.
        /// </summary>
        internal static List<Renderer> IsolateHiddenInstanceRenderers(GameObject instance)
        {
            Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();
            var isolated = new List<Renderer>();
            if (all == null)
            {
                return isolated;
            }
            Transform instanceTransform = instance == null ? null : instance.transform;
            foreach (Renderer renderer in all)
            {
                if (renderer == null || renderer.forceRenderingOff)
                {
                    continue;
                }
                GameObject go = renderer.gameObject;
                if (go == null)
                {
                    continue;
                }
                if (renderer.hideFlags == HideFlags.None && go.hideFlags == HideFlags.None)
                {
                    continue;
                }
                UnityEngine.SceneManagement.Scene scene = go.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }
                if (
                    instanceTransform != null
                    && renderer.transform != null
                    && IsInPrefabSubtree(renderer.transform, instanceTransform)
                )
                {
                    continue;
                }
                renderer.forceRenderingOff = true;
                isolated.Add(renderer);
            }
            return isolated;
        }

        /// <summary>
        /// Hidden-flagged variant of IsolateInstanceCanvasRenderers: culls
        /// hidden-flagged CanvasRenderers in valid loaded scenes only.
        /// Restored via RestoreIsolatedCanvasRenderers in the same finally.
        /// </summary>
        internal static List<CanvasRenderer> IsolateHiddenInstanceCanvasRenderers(
            GameObject instance
        )
        {
            CanvasRenderer[] all = Resources.FindObjectsOfTypeAll<CanvasRenderer>();
            var isolated = new List<CanvasRenderer>();
            if (all == null)
            {
                return isolated;
            }
            Transform instanceTransform = instance == null ? null : instance.transform;
            foreach (CanvasRenderer renderer in all)
            {
                if (renderer == null || renderer.cull)
                {
                    continue;
                }
                GameObject go = renderer.gameObject;
                if (go == null)
                {
                    continue;
                }
                if (renderer.hideFlags == HideFlags.None && go.hideFlags == HideFlags.None)
                {
                    continue;
                }
                UnityEngine.SceneManagement.Scene scene = go.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }
                if (
                    instanceTransform != null
                    && renderer.transform != null
                    && IsInPrefabSubtree(renderer.transform, instanceTransform)
                )
                {
                    continue;
                }
                renderer.cull = true;
                isolated.Add(renderer);
            }
            return isolated;
        }

        /// <summary>
        /// True for prefab asset paths (GameObject asset path ending in
        /// .prefab, case-insensitive). Scene objects have no asset path and
        /// never match. Pure; shared by the stage-resolution seams.
        /// </summary>
        internal static bool IsPrefabAssetPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the asset path of the prefab currently open in the Prefab
        /// Stage, or false when no stage is open (or it has no prefab path).
        /// Null-safe and exception-contained: a throwing prefab-stage query
        /// (domain edge states) reports no stage rather than breaking capture.
        /// </summary>
        internal static bool TryGetPrefabStageAssetPath(out string prefabPath)
        {
            prefabPath = null;
            try
            {
                PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null)
                {
                    return false;
                }
                if (!IsPrefabAssetPath(stage.assetPath))
                {
                    return false;
                }
                prefabPath = stage.assetPath;
                return true;
            }
            catch (System.Exception)
            {
                prefabPath = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves the prefab capture source: an open Prefab Stage wins over
        /// the explicit path (selection), otherwise the explicit path wins
        /// when it is a prefab asset path. False when neither resolves.
        /// </summary>
        internal static bool TryResolvePrefabAssetPath(string explicitPath, out string resolvedPath)
        {
            string stagePath;
            if (TryGetPrefabStageAssetPath(out stagePath))
            {
                resolvedPath = stagePath;
                return true;
            }
            if (IsPrefabAssetPath(explicitPath))
            {
                resolvedPath = explicitPath;
                return true;
            }
            resolvedPath = null;
            return false;
        }

        /// <summary>
        /// Snapshots the RenderSettings environment (skybox/fog/ambient/
        /// reflections) so the prefab path can neutralize it and restore it
        /// in finally. Pure read; never mutates.
        /// </summary>
        internal static PrefabEnvSnapshot SnapshotPrefabEnvironment()
        {
            var snapshot = new PrefabEnvSnapshot
            {
                Valid = true,
                Skybox = RenderSettings.skybox,
                Fog = RenderSettings.fog,
                FogColor = RenderSettings.fogColor,
                FogDensity = RenderSettings.fogDensity,
                AmbientMode = RenderSettings.ambientMode,
                AmbientLight = RenderSettings.ambientLight,
                AmbientSkyColor = RenderSettings.ambientSkyColor,
                AmbientEquatorColor = RenderSettings.ambientEquatorColor,
                AmbientGroundColor = RenderSettings.ambientGroundColor,
                AmbientIntensity = RenderSettings.ambientIntensity,
                ReflectionIntensity = RenderSettings.reflectionIntensity,
            };
            return snapshot;
        }

        /// <summary>
        /// Neutralizes the RenderSettings environment for a prefab capture,
        /// per background mode: Skybox mode with an assigned sky keeps the
        /// skybox and switches ambient to Skybox so the sky lights the
        /// prefab (a Flat mid-grey here would kill the sky ambient
        /// contribution and leave the prefab flatly lit); every other case
        /// (SolidColor, Transparent, or Skybox with a null sky) clears the
        /// sky and uses a flat mid-grey ambient at full intensity with zero
        /// reflections. Fog is always off. EffectiveClearColor routing on
        /// the camera is untouched, so SolidColor/Transparent modes keep
        /// their clear color. The intensity set is critical: a Flat grey
        /// with ambientIntensity 0 contributes no light and leaves the
        /// prefab black once scene lights are neutralized. Pair with
        /// RestorePrefabEnvironment in the same finally block. Prefab path
        /// only; scene captures never call this.
        /// </summary>
        internal static void NeutralizePrefabEnvironment(CaptureSettings settings)
        {
            bool wantSky =
                settings.BackgroundMode == BackgroundMode.Skybox && RenderSettings.skybox != null;
            if (!wantSky && RenderSettings.skybox != null)
            {
                RenderSettings.skybox = null;
            }
            if (RenderSettings.fog)
            {
                RenderSettings.fog = false;
            }
            if (wantSky)
            {
                if (RenderSettings.ambientMode != AmbientMode.Skybox)
                {
                    RenderSettings.ambientMode = AmbientMode.Skybox;
                }
                if (RenderSettings.ambientIntensity != 1f)
                {
                    RenderSettings.ambientIntensity = 1f;
                }
            }
            else
            {
                if (RenderSettings.ambientMode != AmbientMode.Flat)
                {
                    RenderSettings.ambientMode = AmbientMode.Flat;
                }
                if (RenderSettings.ambientLight != k_PrefabNeutralAmbient)
                {
                    RenderSettings.ambientLight = k_PrefabNeutralAmbient;
                }
                if (RenderSettings.ambientIntensity != 1f)
                {
                    RenderSettings.ambientIntensity = 1f;
                }
            }
            if (RenderSettings.reflectionIntensity != 0f)
            {
                RenderSettings.reflectionIntensity = 0f;
            }
        }

        /// <summary>
        /// Restores a snapshot taken by SnapshotPrefabEnvironment.
        /// No-op for a default (Valid false) snapshot. Null-material safe:
        /// assigning a destroyed skybox back is guarded by the Unity null.
        /// </summary>
        internal static void RestorePrefabEnvironment(PrefabEnvSnapshot snapshot)
        {
            if (!snapshot.Valid)
            {
                return;
            }
            if (snapshot.Skybox == null)
            {
                RenderSettings.skybox = null;
            }
            else
            {
                RenderSettings.skybox = snapshot.Skybox;
            }
            RenderSettings.fog = snapshot.Fog;
            RenderSettings.fogColor = snapshot.FogColor;
            RenderSettings.fogDensity = snapshot.FogDensity;
            RenderSettings.ambientMode = snapshot.AmbientMode;
            RenderSettings.ambientLight = snapshot.AmbientLight;
            RenderSettings.ambientSkyColor = snapshot.AmbientSkyColor;
            RenderSettings.ambientEquatorColor = snapshot.AmbientEquatorColor;
            RenderSettings.ambientGroundColor = snapshot.AmbientGroundColor;
            RenderSettings.ambientIntensity = snapshot.AmbientIntensity;
            RenderSettings.reflectionIntensity = snapshot.ReflectionIntensity;
        }

        /// <summary>
        /// Finds a scene-unused layer for prefab camera isolation: the first
        /// user layer (31 down to 8) with no scan renderers or canvas
        /// renderers on it, so the hidden clone subtree can move onto it and
        /// the temp camera can render that layer only. Returns -1 (fail-open:
        /// callers keep the flag-based path) when every user layer is in use
        /// or the sweep fails. Pure; zero SRP references.
        /// </summary>
        internal static int FindPrefabIsolationLayer(ScanCache scan)
        {
            try
            {
                Renderer[] renderers = null;
                CanvasRenderer[] canvasRenderers = null;
                if (scan != null)
                {
                    renderers = scan.Renderers;
                    canvasRenderers = scan.CanvasRenderers;
                }
                if (renderers == null || canvasRenderers == null)
                {
#if UNITY_6000_0_OR_NEWER
                    renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                        FindObjectsInactive.Include
                    );
                    canvasRenderers = UnityEngine.Object.FindObjectsByType<CanvasRenderer>(
                        FindObjectsInactive.Include
                    );
#else
                    renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
                    canvasRenderers = UnityEngine.Object.FindObjectsOfType<CanvasRenderer>(true);
#endif
                }
                bool[] used = new bool[32];
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (renderer == null)
                        {
                            continue;
                        }
                        GameObject go = renderer.gameObject;
                        if (go == null)
                        {
                            continue;
                        }
                        int layer = go.layer;
                        if (layer >= 0 && layer < 32)
                        {
                            used[layer] = true;
                        }
                    }
                }
                if (canvasRenderers != null)
                {
                    for (int i = 0; i < canvasRenderers.Length; i++)
                    {
                        CanvasRenderer canvasRenderer = canvasRenderers[i];
                        if (canvasRenderer == null)
                        {
                            continue;
                        }
                        GameObject go = canvasRenderer.gameObject;
                        if (go == null)
                        {
                            continue;
                        }
                        int layer = go.layer;
                        if (layer >= 0 && layer < 32)
                        {
                            used[layer] = true;
                        }
                    }
                }
                for (int layer = 31; layer >= 8; layer--)
                {
                    if (!used[layer])
                    {
                        return layer;
                    }
                }
                return -1;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Moves the hidden clone subtree onto the isolation layer found by
        /// FindPrefabIsolationLayer (records original layers for restore).
        /// Only the clone subtree moves (user objects never move); the temp
        /// camera renders the isolation layer only (CaptureCore), so scene
        /// geometry cannot leak even where flag-based isolation misses.
        /// Returns null when the layer is invalid or the root is null;
        /// pair with RestoreIsolatedLayers in the same finally block.
        /// </summary>
        internal static List<LayerSnapshot> IsolateInstanceLayers(
            GameObject instance,
            int isolationLayer
        )
        {
            if (instance == null || isolationLayer < 0 || isolationLayer > 31)
            {
                return null;
            }
            try
            {
                Transform[] transforms = instance.GetComponentsInChildren<Transform>(true);
                if (transforms == null || transforms.Length == 0)
                {
                    return null;
                }
                var snapshots = new List<LayerSnapshot>(transforms.Length);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform == null)
                    {
                        continue;
                    }
                    GameObject go = transform.gameObject;
                    if (go == null)
                    {
                        continue;
                    }
                    snapshots.Add(new LayerSnapshot { Object = go, Layer = go.layer });
                    go.layer = isolationLayer;
                }
                return snapshots.Count > 0 ? snapshots : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Restores original layers recorded by IsolateInstanceLayers.
        /// Null-safe; skips objects destroyed mid-capture.
        /// </summary>
        internal static void RestoreIsolatedLayers(List<LayerSnapshot> isolated)
        {
            if (isolated == null)
            {
                return;
            }
            foreach (LayerSnapshot snapshot in isolated)
            {
                if (snapshot == null || snapshot.Object == null)
                {
                    continue;
                }
                snapshot.Object.layer = snapshot.Layer;
            }
        }

        /// <summary>
        /// True when the prefab path must force neutral lighting: the default
        /// LightingMode.None carries no lighting intent, so scene lights must
        /// not leak into the prefab shot (the shared fallback temp light takes
        /// over). Explicit Light2D/Light3D modes are opt-in and keep the
        /// existing scene-light behavior. Pure.
        /// </summary>
        internal static bool PrefabLightingNeedsNeutralize(CaptureSettings settings)
        {
            return settings.Light2DMode == LightingMode.None;
        }

        /// <summary>
        /// Re-enables prefab-subtree lights disabled by DisableAllLights: the
        /// subtree's own lights are part of the prefab, only scene lights stay
        /// off. Matches by hierarchy (root-inclusive IsInPrefabSubtree); restores the snapshot's
        /// enabled/intensity values. No sweep, no alloc, null-safe.
        /// </summary>
        internal static void ReenableSubtreeLights(GameObject root, List<LightSnapshot> snapshots)
        {
            if (root == null || snapshots == null)
            {
                return;
            }
            Transform rootTransform = root.transform;
            if (rootTransform == null)
            {
                return;
            }
            foreach (LightSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light == null)
                {
                    continue;
                }
                if (!IsInPrefabSubtree(snapshot.Light.transform, rootTransform))
                {
                    continue;
                }
                snapshot.Light.enabled = snapshot.Enabled;
                snapshot.Light.intensity = snapshot.Intensity;
            }
        }

        /// <summary>
        /// Re-enables prefab-subtree Light2D components disabled by
        /// DisableAllLight2Ds, mirroring ReenableSubtreeLights. Uses
        /// Behaviour.enabled directly so OnEnable fires and Light2DManager
        /// re-registers the light. No sweep, no alloc, null-safe.
        /// </summary>
        internal static void ReenableSubtreeLight2Ds(
            GameObject root,
            List<Light2DSnapshot> snapshots
        )
        {
            if (root == null || snapshots == null)
            {
                return;
            }
            Transform rootTransform = root.transform;
            if (rootTransform == null)
            {
                return;
            }
            foreach (Light2DSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light2D == null)
                {
                    continue;
                }
                if (!IsInPrefabSubtree(snapshot.Light2D.transform, rootTransform))
                {
                    continue;
                }
                Behaviour behaviour = snapshot.Light2D as Behaviour;
                if (behaviour != null)
                {
                    behaviour.enabled = snapshot.WasEnabled;
                }
            }
        }

        /// <summary>
        /// Disables every scene Volume component outside the prefab instance
        /// subtree so scene post-processing/color grading cannot leak into the
        /// prefab shot. The instance subtree keeps its own Volumes. Uses the
        /// shared scan snapshot when available (zero extra sweeps); falls back
        /// to a dedicated component sweep otherwise. Typeless under all
        /// pipelines (zero SRP refs on the core assembly); null when the
        /// Volume type is unavailable or no outside Volume exists. Pair with
        /// RestoreSceneVolumes in the same finally block.
        /// </summary>
        internal static List<VolumeSnapshot> DisableSceneVolumes(
            GameObject instance,
            ScanCache scan
        )
        {
            System.Type volumeType = FindVolumeType();
            if (volumeType == null)
            {
                return null;
            }
            // Exact-type match preserves the reflection-era semantics
            // (subclasses excluded).
            Component[] all;
            if (scan != null && scan.Components != null)
            {
                all = scan.Components;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                all = UnityEngine.Object.FindObjectsByType<Component>();
#else
                all = UnityEngine.Object.FindObjectsOfType<Component>();
#endif
            }
            Transform instanceTransform = instance == null ? null : instance.transform;
            var snapshots = new List<VolumeSnapshot>();
            foreach (Component component in all)
            {
                if (component == null || component.GetType() != volumeType)
                {
                    continue;
                }
                if (
                    instanceTransform != null
                    && IsInPrefabSubtree(component.transform, instanceTransform)
                )
                {
                    continue;
                }
                Behaviour behaviour = component as Behaviour;
                if (behaviour == null)
                {
                    continue;
                }
                snapshots.Add(
                    new VolumeSnapshot { Volume = behaviour, WasEnabled = behaviour.enabled }
                );
                behaviour.enabled = false;
            }
            return snapshots.Count > 0 ? snapshots : null;
        }

        /// <summary>
        /// Restores Volume enabled states captured by DisableSceneVolumes.
        /// Null-safe; skips volumes destroyed mid-capture.
        /// </summary>
        internal static void RestoreSceneVolumes(List<VolumeSnapshot> snapshots)
        {
            if (snapshots == null)
            {
                return;
            }
            foreach (VolumeSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Volume == null)
                {
                    continue;
                }
                snapshot.Volume.enabled = snapshot.WasEnabled;
            }
        }

        private static RenderTexture CreateRenderTexture(
            int width,
            int height,
            bool useHdr,
            Color clearColor,
            bool isTransparentMode
        )
        {
            RenderTextureFormat format;
            if (isTransparentMode)
            {
                format = RenderTextureFormat.ARGB32;
            }
            else
            {
                format = useHdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            }
            RenderTexture rt;
            try
            {
                rt = new RenderTexture(width, height, 24, format);
            }
            catch (System.Exception)
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.Default);
            }
            rt.name = k_TempRenderTextureName;
            rt.Create();
            ClearRenderTexture(rt, clearColor);
            return rt;
        }

        /// <summary>
        /// Creates a render-texture using the same format decision as CaptureCore:
        /// ARGB32 in transparent mode, DefaultHDR when post-processing is active
        /// (user wants it AND post-processing is available: a scene Volume or a
        /// project VolumeProfile; requires linear rendering for correct
        /// tonemapping/color grading), Default (sRGB) otherwise. The sRGB path
        /// matches the Scene View's rendering pipeline; ReadBack converts
        /// manually only when the project color space is Linear and the target
        /// is not sRGB. Shared by CaptureCore and RenderLivePreview so the two
        /// paths cannot diverge on RT format.
        /// </summary>
        internal static RenderTexture CreateCaptureTarget(
            int width,
            int height,
            CaptureSettings settings
        )
        {
            return CreateCaptureTarget(width, height, settings, null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore.
        /// </summary>
        internal static RenderTexture CreateCaptureTarget(
            int width,
            int height,
            CaptureSettings settings,
            ScanCache scan
        )
        {
            return CreateRenderTexture(
                width,
                height,
                ShouldApplyPostProcessing(settings, scan),
                EffectiveClearColor(settings),
                IsTransparentMode(settings)
            );
        }

        /// <summary>
        /// Defense-in-depth against premature readbacks: a fresh RenderTexture
        /// can still hold GPU memory recycled from an earlier capture (the GPU
        /// allocator reuses released RTs). Clearing right after Create makes any
        /// such readback return the clear color, which Inspect classifies as
        /// UniformBackground and rejects - never a stale scene's pixels.
        /// </summary>
        private static void ClearRenderTexture(RenderTexture rt, Color color)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                GL.Clear(true, true, color);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>
        /// 32-bit variant of SampleBilinear for the UI blend path. Inputs are
        /// raw sRGB bytes (GetPixels32); texels are normalized to 0..1 float
        /// and interpolated with the same 4-texel weights as SampleBilinear,
        /// so output matches the float path exact or <=1 LSB. No alloc.
        /// </summary>
        private static Color SampleBilinear32(
            Color32[] pixels,
            int texW,
            int texH,
            float x,
            float y
        )
        {
            int x0 = Mathf.Clamp((int)Mathf.Floor(x), 0, texW - 1);
            int y0 = Mathf.Clamp((int)Mathf.Floor(y), 0, texH - 1);
            int x1 = Mathf.Min(x0 + 1, texW - 1);
            int y1 = Mathf.Min(y0 + 1, texH - 1);
            float tx = x - x0;
            float ty = y - y0;
            Color c00 = pixels[y0 * texW + x0];
            Color c10 = pixels[y0 * texW + x1];
            Color c01 = pixels[y1 * texW + x0];
            Color c11 = pixels[y1 * texW + x1];
            float topR = c00.r + (c10.r - c00.r) * tx;
            float topG = c00.g + (c10.g - c00.g) * tx;
            float topB = c00.b + (c10.b - c00.b) * tx;
            float topA = c00.a + (c10.a - c00.a) * tx;
            float botR = c01.r + (c11.r - c01.r) * tx;
            float botG = c01.g + (c11.g - c01.g) * tx;
            float botB = c01.b + (c11.b - c01.b) * tx;
            float botA = c01.a + (c11.a - c01.a) * tx;
            return new Color(
                topR + (botR - topR) * ty,
                topG + (botG - topG) * ty,
                topB + (botB - topB) * ty,
                topA + (botA - topA) * ty
            );
        }

        /// <summary>
        /// 4-texel weighted (bilinear) sample of a Color[] texel grid at float
        /// texel coordinates. Integer texel indices are pixel centers; the far
        /// edge clamps to the last texel. Float math avoids banding.
        /// </summary>
        private static Color SampleBilinear(Color[] pixels, int texW, int texH, float x, float y)
        {
            int x0 = Mathf.Clamp((int)Mathf.Floor(x), 0, texW - 1);
            int y0 = Mathf.Clamp((int)Mathf.Floor(y), 0, texH - 1);
            int x1 = Mathf.Min(x0 + 1, texW - 1);
            int y1 = Mathf.Min(y0 + 1, texH - 1);
            float tx = x - x0;
            float ty = y - y0;
            Color c00 = pixels[y0 * texW + x0];
            Color c10 = pixels[y0 * texW + x1];
            Color c01 = pixels[y1 * texW + x0];
            Color c11 = pixels[y1 * texW + x1];
            float topR = c00.r + (c10.r - c00.r) * tx;
            float topG = c00.g + (c10.g - c00.g) * tx;
            float topB = c00.b + (c10.b - c00.b) * tx;
            float topA = c00.a + (c10.a - c00.a) * tx;
            float botR = c01.r + (c11.r - c01.r) * tx;
            float botG = c01.g + (c11.g - c01.g) * tx;
            float botB = c01.b + (c11.b - c01.b) * tx;
            float botA = c01.a + (c11.a - c01.a) * tx;
            return new Color(
                topR + (botR - topR) * ty,
                topG + (botG - topG) * ty,
                topB + (botB - topB) * ty,
                topA + (botA - topA) * ty
            );
        }

        internal static Texture2D ReadBack(RenderTexture rt)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                // The values in the RT are sRGB-encoded when the RT itself is
                // sRGB (the GPU applies linearâ†’sRGB on write) or when the
                // project renders in gamma color space (no conversion happens
                // anywhere). Only a linear (non-sRGB) RT inside a LINEAR color
                // space project holds linear values that must be converted
                // manually before PNG encoding.
                bool rtIsSrgb = rt.sRGB;
                bool projectIsLinear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                bool needsLinearToSrgb = projectIsLinear && !rtIsSrgb;

                // Match the texture's color space to the RT's byte space so
                // ReadPixels cannot apply an automatic color space conversion:
                // when the RT is linear (sRGB=false) and the texture is sRGB,
                // Unity applies linearâ†’gamma during ReadPixels, which would
                // double-convert on top of ConvertLinearToSrgb.
                bool linearFlag = needsLinearToSrgb;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, linearFlag);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                if (needsLinearToSrgb)
                {
                    ConvertLinearToSrgb(tex);
                    // The converted bytes are sRGB; re-flag the texture so
                    // preview/overlay display paths sample it as sRGB (a
                    // linear-flagged texture holding sRGB bytes renders
                    // brightened in linear color space projects).
                    return ReflagSrgb(tex);
                }
                return tex;
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>
        /// HDR targets in linear color space projects are linear (sRGB=false);
        /// ReadPixels + EncodeToPNG would store linear values as sRGB and the
        /// PNG comes out ~2.1x darker. Convert RGB via the canonical sRGB
        /// transfer curve in FLOAT space (before 8-bit quantization) so dark
        /// values keep full precision; alpha untouched. Only called when the
        /// project color space is Linear and the RT is not sRGB.
        /// </summary>
        private static void ConvertLinearToSrgb(Texture2D tex)
        {
            double startTime = EditorApplication.timeSinceStartup;
            Color[] pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = pixels[i];
                pixel.r = Mathf.LinearToGammaSpace(pixel.r);
                pixel.g = Mathf.LinearToGammaSpace(pixel.g);
                pixel.b = Mathf.LinearToGammaSpace(pixel.b);
                pixels[i] = pixel;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            if (!s_State.ConversionLogged)
            {
                s_State.ConversionLogged = true;
                double elapsedMs = (EditorApplication.timeSinceStartup - startTime) * 1000.0;
                Debug.Log(
                    k_LogPrefix
                        + "linear->sRGB conversion applied (linear color space, non-sRGB target) in "
                        + elapsedMs.ToString("0")
                        + " ms."
                );
            }
        }

        /// <summary>
        /// Returns a new sRGB-flagged texture with the same raw bytes as the
        /// given texture, then destroys the source. Used after
        /// ConvertLinearToSrgb so display paths sample the converted bytes as
        /// sRGB (the byte content and the color-space flag must agree).
        /// LoadRawTextureData copies bytes without any color conversion.
        /// </summary>
        private static Texture2D ReflagSrgb(Texture2D tex)
        {
            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.LoadRawTextureData(tex.GetRawTextureData());
            copy.Apply();
            UnityEngine.Object.DestroyImmediate(tex);
            return copy;
        }

        /// <summary>
        /// Finds the first enabled Directional Light in the scene. Returns null
        /// when no directional light exists.
        /// </summary>
        internal static Light TryFindDirectionalLight()
        {
            return TryFindDirectionalLight(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: searches the shared light
        /// snapshot instead of issuing a dedicated sweep. The
        /// isActiveAndEnabled filter keeps results identical to the legacy
        /// active-only sweep.
        /// </summary>
        internal static Light TryFindDirectionalLight(ScanCache scan)
        {
            Light[] lights;
            if (scan != null && scan.Lights != null)
            {
                lights = scan.Lights;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                lights = UnityEngine.Object.FindObjectsByType<Light>();
#else
                lights = UnityEngine.Object.FindObjectsOfType<Light>();
#endif
            }
            foreach (Light light in lights)
            {
                if (
                    light != null
                    && light.isActiveAndEnabled
                    && light.type == LightType.Directional
                )
                {
                    return light;
                }
            }
            return null;
        }

        /// <summary>
        /// Snapshots the full Light3D state (enabled, intensity, color,
        /// shadows, rotation, bake type, HDRP affect-sky flag) of
        /// <paramref name="light" />. Null-safe: null in, null out. Shared by
        /// capture and live preview so every pass snapshots regardless of
        /// lighting mode; only Light3D mode mutates.
        /// </summary>
        internal static LightSnapshot3D SnapshotLight3D(Light light)
        {
            if (light == null)
            {
                return null;
            }
            var snapshot = new LightSnapshot3D
            {
                Light = light,
                Enabled = light.enabled,
                Intensity = light.intensity,
                Color = light.color,
                Shadows = light.shadows,
                Rotation = light.transform.rotation,
                BakeType = light.lightmapBakeType,
            };
            return snapshot;
        }

        /// <summary>
        /// Clamps the Light3D UI intensity to the fixed [0, cap] range before
        /// pipeline scaling. The window already clamps to the active max via
        /// setters; this is the defense-in-depth bound for menu and batch
        /// paths. Pure: no allocation, no LINQ. Yaw and pitch limits drive
        /// slider range and the None-mode fallback orientation (see
        /// ResolveNoneFallbackOrientation); explicit Light3D rotation still
        /// uses the user yaw/pitch values directly.
        /// </summary>
        internal static float ClampLight3DIntensity(float uiValue)
        {
            return Mathf.Clamp(uiValue, 0f, UniThumbSettings.Light3DIntensityHardCap);
        }

        /// <summary>
        /// Maps the Light3D UI intensity (0-5) to the pipeline-appropriate
        /// scale. Returns the value unchanged for URP/Built-in; multiplies
        /// by the HDRP lux scale when running under HDRP so the directional
        /// light is visible at physical light unit magnitudes.
        /// </summary>
        internal static float ResolveLight3DIntensity(float uiValue)
        {
            return IsHdrpPipeline() ? uiValue * k_HdrpLight3DIntensityScale : uiValue;
        }

        /// <summary>
        /// Resolves the None-mode 3D fallback orientation from the Light3D
        /// defaults clamped to the active limits carried by the settings.
        /// Prefers the user-visible defaults (yaw 50, pitch -30); the fixed
        /// 50 pitch (k_PrefabFallbackKeyPitch) survives only as a last resort
        /// when the clamped default pitch sits below the horizon in Skybox
        /// mode with a Procedural skybox assigned (black sky). Legacy
        /// structs with uninitialized limits (min greater than or equal to
        /// max) fall back to the default -180/180 and -89/89 ranges. Pure:
        /// no allocation, no LINQ. Null-camera fallback for
        /// ResolvePrefabFallbackAim; prefab captures with a framed camera
        /// use the dynamic aim instead.
        /// </summary>
        internal static void ResolveNoneFallbackOrientation(
            CaptureSettings settings,
            out float yaw,
            out float pitch
        )
        {
            float yawMin = settings.Light3DYawMin;
            float yawMax = settings.Light3DYawMax;
            if (yawMin >= yawMax)
            {
                yawMin = k_Light3DYawDefaultMin;
                yawMax = k_Light3DYawDefaultMax;
            }
            float pitchMin = settings.Light3DPitchMin;
            float pitchMax = settings.Light3DPitchMax;
            if (pitchMin >= pitchMax)
            {
                pitchMin = k_Light3DPitchDefaultMin;
                pitchMax = k_Light3DPitchDefaultMax;
            }
            yaw = Mathf.Clamp(k_Light3DDefaultYaw, yawMin, yawMax);
            pitch = Mathf.Clamp(k_Light3DDefaultPitch, pitchMin, pitchMax);
            if (settings.BackgroundMode == BackgroundMode.Skybox && pitch < 0f)
            {
                if (IsProceduralSkybox())
                {
                    pitch = Mathf.Clamp(k_PrefabFallbackKeyPitch, pitchMin, pitchMax);
                }
            }
        }

        /// <summary>
        /// Resolves the prefab-only TempLight3D fallback orientation
        /// dynamically from the framed capture camera so the key light aims
        /// at the framed prefab content. The camera is already positioned by
        /// ApplyOrbitTransformToBounds over the instance subtree bounds
        /// (bounds center + orbit yaw/pitch/framing), so copying its view
        /// direction yields a headlight parallel to the view ray through the
        /// bounds center, for perspective and orthographic+3D prefabs alike.
        /// Null camera falls back to ResolveNoneFallbackOrientation. Limits
        /// clamping, legacy limit normalization, and the Procedural
        /// below-horizon last resort match the defaults path. Pure except
        /// the RenderSettings skybox read: no allocation, no LINQ. Prefab
        /// fallback only; explicit Light3D mode and the scene path are
        /// untouched.
        /// </summary>
        internal static void ResolvePrefabFallbackAim(
            Camera cam,
            CaptureSettings settings,
            out float yaw,
            out float pitch
        )
        {
            float yawMin = settings.Light3DYawMin;
            float yawMax = settings.Light3DYawMax;
            if (yawMin >= yawMax)
            {
                yawMin = k_Light3DYawDefaultMin;
                yawMax = k_Light3DYawDefaultMax;
            }
            float pitchMin = settings.Light3DPitchMin;
            float pitchMax = settings.Light3DPitchMax;
            if (pitchMin >= pitchMax)
            {
                pitchMin = k_Light3DPitchDefaultMin;
                pitchMax = k_Light3DPitchDefaultMax;
            }
            if (cam == null)
            {
                yaw = Mathf.Clamp(k_Light3DDefaultYaw, yawMin, yawMax);
                pitch = Mathf.Clamp(k_Light3DDefaultPitch, pitchMin, pitchMax);
            }
            else
            {
                Vector3 forward = cam.transform.forward;
                float clampedY = Mathf.Clamp(forward.y, -1f, 1f);
                float rawPitch = -Mathf.Asin(clampedY) * Mathf.Rad2Deg;
                float rawYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                yaw = Mathf.Clamp(rawYaw, yawMin, yawMax);
                pitch = Mathf.Clamp(rawPitch, pitchMin, pitchMax);
            }
            if (settings.BackgroundMode == BackgroundMode.Skybox && pitch < 0f)
            {
                if (IsProceduralSkybox())
                {
                    pitch = Mathf.Clamp(k_PrefabFallbackKeyPitch, pitchMin, pitchMax);
                }
            }
        }

        /// <summary>
        /// True when the active skybox material uses a Procedural sky shader.
        /// Guards the None-fallback last resort: only a Procedural sun below
        /// the horizon renders a black sky. Null-safe; no allocation.
        /// </summary>
        internal static bool IsProceduralSkybox()
        {
            Material skybox = RenderSettings.skybox;
            if (skybox == null)
            {
                return false;
            }
            Shader shader = skybox.shader;
            if (shader == null)
            {
                return false;
            }
            string shaderName = shader.name;
            if (string.IsNullOrEmpty(shaderName))
            {
                return false;
            }
            return shaderName.IndexOf("Procedural", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Disables every scene Light (including lights on inactive
        /// GameObjects) for the lighting override. Shared with the live
        /// preview. Pair with RestoreLights in the same finally block.
        /// </summary>
        internal static List<LightSnapshot> DisableAllLights()
        {
            return DisableAllLights(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: snapshots the shared
        /// light snapshot (already include-inactive, like the legacy sweep).
        /// </summary>
        internal static List<LightSnapshot> DisableAllLights(ScanCache scan)
        {
            Light[] lights;
            if (scan != null && scan.Lights != null)
            {
                lights = scan.Lights;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
#else
                lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
#endif
            }
            var snapshots = new List<LightSnapshot>(lights.Length);
            foreach (Light light in lights)
            {
                if (light == null)
                {
                    continue;
                }
                snapshots.Add(
                    new LightSnapshot
                    {
                        Light = light,
                        Enabled = light.enabled,
                        Intensity = light.intensity,
                    }
                );
                if (s_State.RecordUndo)
                {
                    Undo.RecordObject(light, "UniThumb capture");
                }
                light.enabled = false;
                light.intensity = 0f;
            }
            if (snapshots.Count == 0)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "No Light components found in scene; lighting override had nothing to disable."
                );
            }
            return snapshots;
        }

        /// <summary>
        /// Restores snapshots taken by DisableAllLights. Null-safe.
        /// </summary>
        internal static void RestoreLights(List<LightSnapshot> snapshots)
        {
            if (snapshots == null)
            {
                return;
            }
            foreach (LightSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light == null)
                {
                    continue;
                }
                snapshot.Light.enabled = snapshot.Enabled;
                snapshot.Light.intensity = snapshot.Intensity;
            }
        }

        /// <summary>
        /// Restores an existing Directional Light that was modified by Light3D
        /// mode. Null-safe: no-op when no snapshot was taken (temp light used instead).
        /// </summary>
        internal static void RestoreLight3D(LightSnapshot3D snapshot)
        {
            if (snapshot == null || snapshot.Light == null)
            {
                return;
            }
            if (s_State.RecordUndo)
            {
                Undo.RecordObject(snapshot.Light, "UniThumb capture");
            }
            snapshot.Light.enabled = snapshot.Enabled;
            snapshot.Light.intensity = snapshot.Intensity;
            snapshot.Light.color = snapshot.Color;
            snapshot.Light.shadows = snapshot.Shadows;
            snapshot.Light.transform.rotation = snapshot.Rotation;
            if (snapshot.Light.lightmapBakeType != snapshot.BakeType)
            {
                snapshot.Light.lightmapBakeType = snapshot.BakeType;
            }
        }

        /// <summary>
        /// Snapshots the rotation of every scene Directional Light except
        /// <paramref name="skip" /> (the primary Light3D light, handled by
        /// LightSnapshot3D), including disabled lights and lights on inactive
        /// GameObjects: the primary finder stays active-only, but the yaw
        /// sweep must rotate (and restore) every directional so the HDRP
        /// bound sun always moves. Call before DisableAllLights; the override
        /// disables lights which would hide them from a later search.
        /// </summary>
        internal static List<LightSnapshot3D> SnapshotOtherDirectionalRotations(Light skip)
        {
            return SnapshotOtherDirectionalRotations(skip, null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: snapshots the shared
        /// light snapshot (already include-inactive, like the legacy sweep).
        /// </summary>
        internal static List<LightSnapshot3D> SnapshotOtherDirectionalRotations(
            Light skip,
            ScanCache scan
        )
        {
            Light[] lights;
            if (scan != null && scan.Lights != null)
            {
                lights = scan.Lights;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
#else
                lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
#endif
            }
            var snapshots = new List<LightSnapshot3D>(lights.Length);
            foreach (Light light in lights)
            {
                if (light == null || light == skip || light.type != LightType.Directional)
                {
                    continue;
                }
                var extraSnapshot = new LightSnapshot3D
                {
                    Light = light,
                    Enabled = light.enabled,
                    Intensity = light.intensity,
                    Color = light.color,
                    Shadows = light.shadows,
                    Rotation = light.transform.rotation,
                    BakeType = light.lightmapBakeType,
                };
                snapshots.Add(extraSnapshot);
            }
            return snapshots;
        }

        /// <summary>
        /// Restores rotations captured by SnapshotOtherDirectionalRotations.
        /// Null-safe: no-op when no extras were snapshotted.
        /// </summary>
        internal static void RestoreDirectionalRotations(List<LightSnapshot3D> snapshots)
        {
            if (snapshots == null)
            {
                return;
            }
            foreach (LightSnapshot3D snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light == null)
                {
                    continue;
                }
                if (s_State.RecordUndo)
                {
                    Undo.RecordObject(snapshot.Light, "UniThumb capture");
                }
                snapshot.Light.transform.rotation = snapshot.Rotation;
                if (snapshot.Light.lightmapBakeType != snapshot.BakeType)
                {
                    snapshot.Light.lightmapBakeType = snapshot.BakeType;
                }
            }
        }

        /// <summary>
        /// Disables all existing Global Light2D components in the scene to
        /// prevent the Renderer2D multiple-global-light error. Must be paired
        /// with RestoreGlobalLight2Ds in the same finally block.
        ///
        /// Uses direct Behaviour.enabled assignment (not PropertyInfo.SetValue)
        /// because Behaviour.enabled is an extern property backed by native code;
        /// reflection-based SetValue does not reliably trigger OnDisable() which
        /// Light2DManager relies on to unregister the light from its global list.
        /// </summary>
        internal static List<Light2DSnapshot> DisableExistingGlobalLight2Ds()
        {
            return DisableExistingGlobalLight2Ds(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore.
        /// </summary>
        internal static List<Light2DSnapshot> DisableExistingGlobalLight2Ds(ScanCache scan)
        {
            return DisableLight2Ds(true, scan);
        }

        /// <summary>
        /// Disables EVERY Light2D component in the scene (Global, Point, Sprite,
        /// FreeForm...) regardless of light type. Used by the lighting override
        /// ("Disable scene lighting") so 2D-renderer thumbnails render with no
        /// 2D lights at all. Must be paired with RestoreGlobalLight2Ds in the
        /// same finally block. Same Behaviour.enabled rationale as
        /// DisableExistingGlobalLight2Ds.
        /// </summary>
        internal static List<Light2DSnapshot> DisableAllLight2Ds()
        {
            return DisableAllLight2Ds(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore.
        /// </summary>
        internal static List<Light2DSnapshot> DisableAllLight2Ds(ScanCache scan)
        {
            return DisableLight2Ds(false, scan);
        }

        private static List<Light2DSnapshot> DisableLight2Ds(bool onlyGlobal, ScanCache scan)
        {
#if HAS_URP
            // Shim path: exact-type match via UniThumbUrp preserves the
            // reflection-era semantics (subclasses excluded).
            int globalValue = 0;
            if (onlyGlobal)
            {
                globalValue = FindLight2DGlobalModeValue(UniThumbUrp.Light2DLightType);
            }

            // Find all Light2D components across the scene.
            Component[] all;
            if (scan != null && scan.Components != null)
            {
                all = scan.Components;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                all = UnityEngine.Object.FindObjectsByType<Component>();
#else
                all = UnityEngine.Object.FindObjectsOfType<Component>();
#endif
            }
            var snapshots = new List<Light2DSnapshot>();
            foreach (Component c in all)
            {
                if (c == null || !UniThumbUrp.IsLight2D(c))
                {
                    continue;
                }
                if (onlyGlobal)
                {
                    if (!UniThumbUrp.IsGlobalLight2D(c, globalValue))
                    {
                        continue;
                    }
                }
                // Disable it via Behaviour cast so OnDisable() fires and
                // Light2DManager unregisters the light.
                Behaviour behaviour = c as Behaviour;
                if (behaviour == null)
                {
                    continue;
                }
                snapshots.Add(new Light2DSnapshot { Light2D = c, WasEnabled = behaviour.enabled });
                behaviour.enabled = false;
            }
            return snapshots.Count > 0 ? snapshots : null;
#else
            // dyn-05 Light2D-absent guard: no URP package means no Light2D
            // type; fail open with no snapshots (callers restore null as no-op).
            return null;
#endif
        }

        /// <summary>
        /// Restores Global Light2D components that were disabled by
        /// DisableExistingGlobalLight2Ds. Uses Behaviour.enabled directly
        /// to ensure OnEnable() fires and Light2DManager re-registers them.
        /// </summary>
        internal static void RestoreGlobalLight2Ds(List<Light2DSnapshot> snapshots)
        {
            if (snapshots == null)
            {
                return;
            }
            foreach (Light2DSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.Light2D == null)
                {
                    continue;
                }
                Behaviour behaviour = snapshot.Light2D as Behaviour;
                if (behaviour != null)
                {
                    behaviour.enabled = snapshot.WasEnabled;
                }
            }
        }

        private static bool TrySubmitRenderRequest(Camera cam, RenderTexture rt)
        {
#if UNITY_2022_2_OR_NEWER
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest
            {
                destination = rt,
                slice = 0,
                face = CubemapFace.Unknown,
                mipLevel = 0,
            };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
                return true;
            }
#endif
            return false;
        }

        /// <summary>
        /// Adds render-pipeline camera data: UrpBridge shim dispatch under
        /// HAS_URP, dynamic HDRP fallback otherwise (core keeps zero SRP
        /// hard refs).
        /// URP: UniversalAdditionalCameraData with renderPostProcessing=true.
        /// HDRP: HDAdditionalCameraData with volumeLayerMask set to Everything.
        /// Returns false (with a warning naming the missing type) when neither
        /// pipeline is available; the capture then continues without post-processing.
        /// </summary>
        internal static bool TryEnablePostProcessing(Camera cam)
        {
            return EnsureUrpCameraData(cam, enablePostProcessing: true);
        }

        /// <summary>
        /// Ensures the camera has URP pipeline data (UACD) without enabling
        /// post-processing. This is needed so URP uses the correct renderer
        /// (e.g. Renderer2D for 2D scenes) even when post-processing is off.
        /// Without UACD, URP falls back to a default renderer that may not
        /// match the Scene View's renderer, causing color differences.
        /// </summary>
        internal static bool TryEnsureUrpCameraData(Camera cam)
        {
            return EnsureUrpCameraData(cam, enablePostProcessing: false);
        }

        /// <summary>
        /// Ensures the camera has URP pipeline data (UACD) with renderer index
        /// and post-processing state copied from the Scene View camera. This
        /// ensures the capture uses the same URP renderer (e.g. Renderer2D)
        /// and post-processing configuration as the Scene View, preventing
        /// color differences caused by different renderer pipelines.
        /// </summary>
        internal static bool TryEnsureUrpCameraDataFromSceneView(Camera cam)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                return EnsureUrpCameraData(cam, enablePostProcessing: false);
            }
            return EnsureUrpCameraDataFromSource(cam, sv.camera);
        }

        private static bool EnsureUrpCameraDataFromSource(Camera cam, Camera sourceCam)
        {
#if HAS_URP
            // Shim path: typed UniversalAdditionalCameraData handling lives
            // in UrpBridge; core keeps zero Universal refs.
            return UniThumbUrp.EnsureCameraDataFromSource(cam, sourceCam);
#else
            try
            {
                // No URP package (HAS_URP undefined): UACD cannot exist.
                // Establish the HDRP baseline so an HDRP SceneView source still
                // matches quality; Built-in simply reports the baseline.
                // Without the baseline the capture camera falls back to the
                // HDRP asset's default frame settings (post-processing ON) and
                // applies volume effects even when the UniThumb post-processing
                // toggle is off.
                bool baseline = EnsureUrpCameraData(cam, enablePostProcessing: false);

                // Copy HDAC settings from the SceneView source camera so the
                // capture camera matches its quality: disable TAA/SMAA (single
                // frame cannot benefit from temporal AA), and copy frame settings
                // (post-processing, shadow quality, etc.).
                const string k_HdacTypeName =
                    "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
                System.Type hdacType = System.Type.GetType(k_HdacTypeName);
                if (hdacType != null)
                {
                    Component captureHdac = cam.GetComponent(hdacType);
                    Component sourceHdac = sourceCam.GetComponent(hdacType);
                    if (captureHdac != null && sourceHdac != null)
                    {
                        // Disable antialiasing â€” single-frame render cannot use TAA/SMAA.
                        // HDAC.antialiasing is a public field of AntialiasingMode enum: None=0.
                        System.Reflection.FieldInfo aaField = hdacType.GetField(
                            "antialiasing",
                            BindingFlags.Public | BindingFlags.Instance
                        );
                        if (aaField != null)
                        {
                            aaField.SetValue(captureHdac, 0); // AntialiasingMode.None
                        }

                        // Copy the source camera's custom frame settings so the
                        // capture camera inherits post-processing, shadow quality,
                        // etc. from the SceneView.
                        System.Reflection.FieldInfo srcFsField = hdacType.GetField(
                            "m_RenderingPathCustomFrameSettings",
                            BindingFlags.NonPublic | BindingFlags.Instance
                        );
                        System.Reflection.FieldInfo dstFsField = hdacType.GetField(
                            "m_RenderingPathCustomFrameSettings",
                            BindingFlags.NonPublic | BindingFlags.Instance
                        );
                        if (srcFsField != null && dstFsField != null)
                        {
                            object srcFs = srcFsField.GetValue(sourceHdac);
                            dstFsField.SetValue(captureHdac, srcFs);

                            // Ensure customRenderingSettings is ON so the copied
                            // frame settings actually take effect.
                            System.Reflection.FieldInfo customField = hdacType.GetField(
                                "customRenderingSettings",
                                BindingFlags.Public | BindingFlags.Instance
                            );
                            if (customField != null)
                            {
                                customField.SetValue(captureHdac, true);
                            }
                        }
                    }
                }

                return baseline;
            }
            catch (System.Exception)
            {
                return false;
            }
#endif
        }

        private static bool EnsureUrpCameraData(Camera cam, bool enablePostProcessing)
        {
            try
            {
#if HAS_URP
                // Shim path (HAS_URP): typed UniversalAdditionalCameraData
                // handling lives in UrpBridge; the HDRP fallback is
                // #else-only, matching the pre-shim control flow.
                return UniThumbUrp.EnsureCameraData(cam, enablePostProcessing);
#else
                // No URP package: HDRP fallback below (UACD cannot exist
                // without the URP package). HDAdditionalCameraData is
                // likewise [DisallowMultipleComponent].
                const string k_HdacTypeName =
                    "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
                System.Type hdacType = System.Type.GetType(k_HdacTypeName);
                if (hdacType != null)
                {
                    Component component = cam.GetComponent(hdacType);
                    if (component == null)
                    {
                        component = cam.gameObject.AddComponent(hdacType);
                    }
                    if (component != null)
                    {
                        // Volume effects must be visible regardless of the
                        // layer the scene Volumes live on. HDRP 17 exposes
                        // volumeLayerMask as a public FIELD, not a property.
                        System.Reflection.FieldInfo maskField = hdacType.GetField(
                            "volumeLayerMask",
                            System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.Instance
                        );
                        if (maskField != null)
                        {
                            maskField.SetValue(component, new UnityEngine.LayerMask { value = -1 });
                        }

                        // Unlike URP (renderPostProcessing), HDRP gates
                        // post-processing through the camera's Frame Settings,
                        // which default to the HDRP asset's (PostProcess ON).
                        // Without an explicit override the capture camera would
                        // apply volume effects (bloom, ...) even when the
                        // UniThumb post-processing toggle is off, and the
                        // always-run baseline call (enablePostProcessing=false)
                        // would be unable to turn them off.
                        SetHdrpPostProcessingFrameSetting(component, enablePostProcessing);
                    }
                    return component != null;
                }

                if (enablePostProcessing)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Cannot enable post-processing: neither UniversalAdditionalCameraData (URP) nor HDAdditionalCameraData (HDRP) type found; capturing without post-processing."
                    );
                }
                return false;
#endif
            }
            catch (System.Exception exception)
            {
                if (enablePostProcessing)
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Cannot enable post-processing ("
                            + exception.GetType().Name
                            + ": "
                            + exception.Message
                            + "); capturing without post-processing."
                    );
                }
                return false;
            }
        }

        /// <summary>
        /// HDRP gates post-processing through HDAdditionalCameraData's Frame
        /// Settings, not a renderPostProcessing-style property. Enables
        /// customRenderingSettings, then sets the PostProcess bit to match
        /// the requested state. Pure reflection: the asmdef cannot reference
        /// HDRP. dyn-05 KEEP-DYNAMIC (absent on URP-only installs) with an
        /// isolated version-branched frame-settings helper below: HDRP 14
        /// (Unity 2022.3) vs HDRP 17 (Unity 6000.x) internals are
        /// version-fragile, so all field layout probing lives here and
        /// callers only see a fail-open void. Logs at warning level when a
        /// step fails (unlike the previous silent version) so
        /// misconfigurations surface.
        /// </summary>
        private static void SetHdrpPostProcessingFrameSetting(Component hdac, bool enable)
        {
            const string k_Prefix = "[UniThumb] HDRP PP frame setting: ";
            try
            {
                System.Type hdacType = hdac.GetType();

                // Step 1: customRenderingSettings = true
                System.Reflection.FieldInfo customField = hdacType.GetField(
                    "customRenderingSettings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                );
                if (customField == null)
                {
                    // Try property fallback for older HDRP versions
                    System.Reflection.PropertyInfo customProp = hdacType.GetProperty(
                        "customRenderingSettings",
                        System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.Instance
                    );
                    if (customProp != null && customProp.CanWrite)
                    {
                        customProp.SetValue(hdac, true, null);
                    }
                    else
                    {
                        Debug.LogWarning(
                            k_Prefix
                                + "customRenderingSettings field/property not found on "
                                + hdacType.Name
                                + "; PP cannot be toggled."
                        );
                        return;
                    }
                }
                else
                {
                    customField.SetValue(hdac, true);
                }

                // Step 2: Get the FrameSettings struct (isolated
                // version-branched probe: HDRP 17 on Unity 6000.x stores the
                // private m_RenderingPathCustomFrameSettings field; HDRP 14
                // on Unity 2022.3 exposes the public frameSettings property.
                // Both are probed in version order so a layout rename fails
                // open here instead of leaking into callers).
                object frameSettings = null;
                System.Type frameSettingsType = null;
                System.Reflection.FieldInfo fsField = null;
                System.Reflection.PropertyInfo fsProp = null;

#if UNITY_6000_0_OR_NEWER
                // HDRP 17 order: private field first, public property fallback.
                fsField = hdacType.GetField(
                    "m_RenderingPathCustomFrameSettings",
                    System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance
                );
                if (fsField != null)
                {
                    frameSettings = fsField.GetValue(hdac);
                    frameSettingsType = fsField.FieldType;
                }
                else
                {
                    fsProp = hdacType.GetProperty(
                        "frameSettings",
                        System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.Instance
                    );
                    if (fsProp != null)
                    {
                        frameSettings = fsProp.GetValue(hdac, null);
                        frameSettingsType = fsProp.PropertyType;
                    }
                }
#else
                // HDRP 14 order: public property first, private field fallback.
                fsProp = hdacType.GetProperty(
                    "frameSettings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                );
                if (fsProp != null)
                {
                    frameSettings = fsProp.GetValue(hdac, null);
                    frameSettingsType = fsProp.PropertyType;
                }
                else
                {
                    fsField = hdacType.GetField(
                        "m_RenderingPathCustomFrameSettings",
                        System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance
                    );
                    if (fsField != null)
                    {
                        frameSettings = fsField.GetValue(hdac);
                        frameSettingsType = fsField.FieldType;
                    }
                }
#endif

                if (frameSettings == null)
                {
                    Debug.LogWarning(
                        k_Prefix
                            + "FrameSettings not found on "
                            + hdacType.Name
                            + "; PP cannot be toggled."
                    );
                    return;
                }

                // Step 3: Resolve FrameSettingsField.Postprocess enum
                System.Type fieldEnumType = frameSettingsType.Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.FrameSettingsField"
                );
                if (fieldEnumType == null)
                {
                    Debug.LogWarning(k_Prefix + "FrameSettingsField enum not found.");
                    return;
                }
                object postProcessField = null;
                try
                {
                    postProcessField = System.Enum.Parse(fieldEnumType, "Postprocess");
                }
                catch (System.ArgumentException)
                {
                    try
                    {
                        postProcessField = System.Enum.Parse(fieldEnumType, "PostProcess");
                    }
                    catch (System.ArgumentException)
                    {
                        Debug.LogWarning(
                            k_Prefix + "PostProcess/Postprocess enum value not found."
                        );
                        return;
                    }
                }

                // Step 4: Call SetEnabled
                System.Reflection.MethodInfo setEnabled = frameSettingsType.GetMethod(
                    "SetEnabled",
                    new System.Type[] { fieldEnumType, typeof(bool) }
                );
                if (setEnabled == null)
                {
                    Debug.LogWarning(
                        k_Prefix
                            + "FrameSettings.SetEnabled method not found on "
                            + frameSettingsType.Name
                    );
                    return;
                }
                setEnabled.Invoke(frameSettings, new object[] { postProcessField, enable });

                // Step 5: Write back
                if (fsField != null)
                {
                    fsField.SetValue(hdac, frameSettings);
                }
                else if (fsProp != null && fsProp.CanWrite)
                {
                    fsProp.SetValue(hdac, frameSettings, null);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(k_Prefix + "Exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Points the camera at the first non-2D URP renderer (e.g.
        /// ForwardRenderer) so standard Light components are processed.
        /// Renderer2D only handles Light2D components and ignores standard
        /// lights. This method routes the pipeline asset's renderer sweep
        /// through the UrpBridge shim, finds the first renderer that is NOT a
        /// Renderer2D, and sets rendererIndex on the camera's UACD. Shared
        /// by explicit Light3D mode, the prefab None fallback, and the live
        /// preview, so the message stays mode-neutral. Returns false silently
        /// for indeterminate failures (no URP, no camera data, unreadable
        /// list): only a genuine Renderer2D-only pipeline warns, otherwise
        /// the None fallback would warn spuriously on 3D projects.
        /// </summary>
        internal static bool TrySwitchTo3DCameraRenderer(Camera cam)
        {
#if HAS_URP
            // Shim path: typed GetRenderer sweep lives in UrpBridge.
            return UniThumbUrp.TrySwitchTo3DRenderer(cam);
#else
            return false;
#endif
        }

        /// <summary>
        /// True only when the active URP pipeline asset exposes a non-empty
        /// renderer list whose every entry is a 2D renderer. False covers
        /// 3D pipelines and indeterminate states (no URP, unreadable list):
        /// callers must stay silent unless this returns true, or the None
        /// fallback warns spuriously on 3D projects (a failed switch reads
        /// as "no 3D renderer"). Single typed renderer sweep; no LINQ.
        /// </summary>
        internal static bool PipelineHasOnly2DRenderers()
        {
            if (s_State.Renderer2DOnlyOverride.HasValue)
            {
                return s_State.Renderer2DOnlyOverride.Value;
            }
#if HAS_URP
            // Shim path: typed GetRenderer sweep lives in UrpBridge.
            return UniThumbUrp.PipelineHasOnly2DRenderers();
#else
            return false;
#endif
        }

        private static ImageCheck Inspect(
            Texture2D tex,
            Color backgroundColor,
            bool ppOrSkyboxActive,
            bool isTransparentMode
        )
        {
            Color32[] pixels = tex.GetPixels32();
            int step = Mathf.Max(1, pixels.Length / 4096);
            Color32 first = pixels[0];
            for (int i = step; i < pixels.Length; i += step)
            {
                Color32 p = pixels[i];
                if (p.r != first.r || p.g != first.g || p.b != first.b || p.a != first.a)
                {
                    return ImageCheck.Ok;
                }
            }
            // Uniform frame detected. In Transparent mode a fully-transparent
            // (all RGBA 0) frame is a valid render (empty scene) - classify as
            // Ok so EncodeToPNG preserves the real alpha channel instead of
            // falling through to the opaque placeholder.
            if (isTransparentMode && first.a == 0 && first.r == 0 && first.g == 0 && first.b == 0)
            {
                return ImageCheck.Ok;
            }
            if (ppOrSkyboxActive)
            {
                // M10: with post-processing (tonemapping shifts the clear color away
                // from BackgroundColor) or a skybox background, a uniform image is a
                // valid background - never classify it as a corrupt render.
                return ImageCheck.UniformBackground;
            }
            bool isBackground =
                Mathf.Abs(first.r - backgroundColor.r * 255f) <= 3
                && Mathf.Abs(first.g - backgroundColor.g * 255f) <= 3
                && Mathf.Abs(first.b - backgroundColor.b * 255f) <= 3;
            return isBackground ? ImageCheck.UniformBackground : ImageCheck.UniformOther;
        }

        private static string Describe(ImageCheck check)
        {
            switch (check)
            {
                case ImageCheck.UniformBackground:
                    return "a uniform background image";
                case ImageCheck.UniformOther:
                    return "an all-black or all-one-color image";
                default:
                    return "an unknown image";
            }
        }

        private static bool ExceedsReliablePixels(int width, int height)
        {
            return (long)width * height > k_MaxReliableRenderPixels;
        }

        private static byte[] CreatePlaceholderPng(int width, int height, Color color)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color32 c = color;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>
        /// Returns true when at least one Volume component exists in the open
        /// scene. Such volumes are used as-is by the capture camera (no
        /// temporary volume is injected).
        /// </summary>
        internal static bool HasPostProcessingVolumes()
        {
            return HasPostProcessingVolumes(null);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: matches against the shared
        /// include-inactive component snapshot instead of issuing a dedicated
        /// full Component sweep. Components on inactive GameObjects are
        /// skipped explicitly so the result stays identical to the
        /// parameterless active-only form (an inactive Volume must not
        /// suppress the temporary post-processing injection).
        /// </summary>
        internal static bool HasPostProcessingVolumes(ScanCache scan)
        {
            System.Type volumeType = FindVolumeType();
            if (volumeType == null)
            {
                return false;
            }
            // Exact-type match preserves the reflection-era semantics
            // (subclasses excluded).
            Component[] all;
            if (scan != null && scan.Components != null)
            {
                all = scan.Components;
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                all = UnityEngine.Object.FindObjectsByType<Component>();
#else
                all = UnityEngine.Object.FindObjectsOfType<Component>();
#endif
            }
            foreach (Component c in all)
            {
                if (c == null || c.GetType() != volumeType)
                {
                    continue;
                }
                GameObject go = c.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when the project contains at least one VolumeProfile
        /// asset. A profile alone cannot run post-processing without a Volume,
        /// but TryCreateTempPostProcessingVolume injects a temporary Global
        /// Volume that uses it.
        /// </summary>
        internal static bool HasVolumeProfileAssets()
        {
            return GetVolumeProfileGuidsCached().Length > 0;
        }

        /// <summary>
        /// Ensures a Volume exists for the capture when post-processing is
        /// requested. When settings.PostProcessingProfile is set to a valid
        /// VolumeProfile, a temporary GLOBAL Volume (HideAndDontSave,
        /// __UniThumbTempGlobalVolume) using exactly that profile is created
        /// even when the scene has Volumes (explicit assignment wins). When no
        /// profile is assigned and the open scene already contains a Volume
        /// component, returns null (the scene's volumes apply as-is). Otherwise
        /// creates a temporary GLOBAL Volume backed by the first usable
        /// VolumeProfile asset found in the project (test artifacts skipped,
        /// see LoadUsableVolumeProfile). The volume is non-persistent: the
        /// caller destroys it in finally, it is never saved with the scene.
        /// Returns null when no profile is available or the Volume component
        /// cannot be created.
        /// </summary>
        /// <summary>
        /// Creates a temporary GLOBAL Volume for the capture. Returns the
        /// volume's GameObject (destroyed in finally by the caller). When an
        /// HDRP clone was created (EnsureHdrpBaselineComponents), it is stored
        /// in outClonedProfile and must also be destroyed by the caller.
        /// </summary>
        internal static GameObject TryCreateTempPostProcessingVolume(
            CaptureSettings settings,
            out UnityEngine.Object outClonedProfile
        )
        {
            return TryCreateTempPostProcessingVolume(settings, null, out outClonedProfile);
        }

        /// <summary>
        /// Scan-aware variant used by CaptureCore: profile resolution reuses
        /// the shared snapshot and the cached VolumeProfile guids.
        /// </summary>
        internal static GameObject TryCreateTempPostProcessingVolume(
            CaptureSettings settings,
            ScanCache scan,
            out UnityEngine.Object outClonedProfile
        )
        {
            return TryCreateTempPostProcessingVolumeCore(settings, scan, out outClonedProfile);
        }

        /// <summary>
        /// Core implementation that also reports whether a cloned profile was
        /// created (must be destroyed by the caller alongside the GO).
        /// </summary>
        private static GameObject TryCreateTempPostProcessingVolumeCore(
            CaptureSettings settings,
            ScanCache scan,
            out UnityEngine.Object clonedProfile
        )
        {
            clonedProfile = null;
            System.Type volumeType = FindVolumeType();
            System.Type profileType = FindVolumeProfileType(volumeType);
            if (volumeType == null || profileType == null)
            {
                return null;
            }
            UnityEngine.Object profile = ResolvePostProcessingProfile(settings, profileType, scan);
            if (profile == null)
            {
                return null;
            }

            try
            {
                // HDRP requires VisualEnvironment (sky) and Exposure (HDR-to-LDR
                // tonemapping) in the Volume for correct rendering. Without these
                // the capture renders a black sky and dark raw HDR values. When the
                // pipeline is HDRP, ensure the profile has these components -- clone
                // the asset first to avoid modifying it.
                bool isHdrp = IsHdrpPipeline();
                if (isHdrp)
                {
                    UnityEngine.Object enhanced = EnsureHdrpBaselineComponents(
                        profile,
                        profileType
                    );
                    if (enhanced != profile)
                    {
                        // EnsureHdrpBaselineComponents cloned the profile to add
                        // HDRP baseline components -- track it for cleanup.
                        clonedProfile = enhanced;
                    }
                    profile = enhanced;
                }

                var volumeGo = new GameObject("__UniThumbTempGlobalVolume");
                volumeGo.hideFlags = HideFlags.HideAndDontSave;
                Component volume = volumeGo.AddComponent(volumeType);
                if (volume == null)
                {
                    UnityEngine.Object.DestroyImmediate(volumeGo);
                    return null;
                }
                PropertyInfo isGlobalProp = volumeType.GetProperty(
                    "isGlobal",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (isGlobalProp != null && isGlobalProp.CanWrite)
                {
                    isGlobalProp.SetValue(volume, true, null);
                }
                PropertyInfo profileProp = volumeType.GetProperty(
                    "profile",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (profileProp != null && profileProp.CanWrite)
                {
                    profileProp.SetValue(volume, profile, null);
                }
                return volumeGo;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Could not create temporary post-processing volume ("
                        + exception.GetType().Name
                        + ": "
                        + exception.Message
                        + ")."
                );
                return null;
            }
        }

        /// <summary>
        /// Creates a minimal HDRP volume with only VisualEnvironment (sky) â€”
        /// no post-processing effects. Used when PP is off but the scene has
        /// no volumes, so the capture still gets a sky background instead of
        /// rendering black. The caller destroys the GO in finally.
        /// </summary>
        internal static GameObject TryCreateHdrpSkyBaselineVolume(
            out UnityEngine.Object createdProfile
        )
        {
            createdProfile = null;
            System.Type volumeType = FindVolumeType();
            System.Type profileType = FindVolumeProfileType(volumeType);
            if (volumeType == null || profileType == null)
            {
                return null;
            }

            const string k_HdacTypeName =
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
            System.Type hdacType = System.Type.GetType(k_HdacTypeName);
            if (hdacType == null)
            {
                return null;
            }
            System.Type veType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.VisualEnvironment"
            );
            if (veType == null)
            {
                return null;
            }

            try
            {
                // Create a fresh profile with only VisualEnvironment
                var profile = ScriptableObject.CreateInstance(profileType);
                createdProfile = profile;

                // Find VolumeProfile.Add<T>
                System.Type baseProfileType = profileType;
                while (baseProfileType != null && baseProfileType.Name != "VolumeProfile")
                {
                    baseProfileType = baseProfileType.BaseType;
                }
                if (baseProfileType == null)
                {
                    baseProfileType = profileType;
                }
                System.Reflection.MethodInfo addMethod = null;
                foreach (
                    System.Reflection.MethodInfo m in baseProfileType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance
                    )
                )
                {
                    if (m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        addMethod = m;
                        break;
                    }
                }
                if (addMethod == null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }

                System.Reflection.MethodInfo addVe = addMethod.MakeGenericMethod(veType);
                addVe.Invoke(profile, new object[] { false });

                // Add Exposure so HDRP applies tonemapping (HDR -> LDR).
                // Without it the output is dark raw HDR.
                System.Type expType = hdacType.Assembly.GetType(
                    "UnityEngine.Rendering.HighDefinition.Exposure"
                );
                if (expType != null)
                {
                    try
                    {
                        System.Reflection.MethodInfo addExp = addMethod.MakeGenericMethod(expType);
                        addExp.Invoke(profile, new object[] { false });

                        // Configure the Exposure component: Fixed mode (0)
                        System.Reflection.FieldInfo componentsField = profileType.GetField(
                            "components",
                            BindingFlags.Public | BindingFlags.Instance
                        );
                        var comps =
                            componentsField != null
                                ? componentsField.GetValue(profile) as System.Collections.IList
                                : null;
                        if (comps != null)
                        {
                            foreach (object c in comps)
                            {
                                if (c == null)
                                {
                                    continue;
                                }
                                if (expType.IsAssignableFrom(c.GetType()))
                                {
                                    SetVolumeParameterInt(c, "mode", 0);
                                    SetVolumeParameterFloat(c, "fixedExposure", HdrpFixedExposure);
                                }
                            }
                        }
                    }
                    catch (System.Exception)
                    {
                        // Exposure type not available
                    }
                }

                // Create Volume GO
                var volumeGo = new GameObject("__UniThumbTempGlobalVolume");
                volumeGo.hideFlags = HideFlags.HideAndDontSave;
                Component volume = volumeGo.AddComponent(volumeType);
                if (volume == null)
                {
                    UnityEngine.Object.DestroyImmediate(volumeGo);
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }
                PropertyInfo isGlobalProp = volumeType.GetProperty(
                    "isGlobal",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (isGlobalProp != null && isGlobalProp.CanWrite)
                {
                    isGlobalProp.SetValue(volume, true, null);
                }
                PropertyInfo profileProp = volumeType.GetProperty(
                    "profile",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (profileProp != null && profileProp.CanWrite)
                {
                    profileProp.SetValue(volume, profile, null);
                }
                return volumeGo;
            }
            catch (System.Exception exception)
            {
                if (createdProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                }
                Debug.LogWarning(
                    k_LogPrefix
                        + "Could not create HDRP sky baseline volume ("
                        + exception.GetType().Name
                        + ": "
                        + exception.Message
                        + ")."
                );
                return null;
            }
        }

        /// <summary>
        /// Creates a high-priority global Volume with only an Exposure component
        /// using the current HdrpFixedExposure value. In HDRP, Exposure controls
        /// tonemapping (HDR->LDR). The high priority ensures this overrides the
        /// scene's Exposure while other effects (bloom, vignette, etc.) pass
        /// through from lower-priority scene volumes.
        /// Called when HDRP is detected, regardless of scene volumes or PP toggle.
        /// The caller destroys the GO in finally.
        /// </summary>
        internal static GameObject TryCreateHdrpExposureOverrideVolume(
            out UnityEngine.Object createdProfile
        )
        {
            createdProfile = null;
            System.Type volumeType = FindVolumeType();
            System.Type profileType = FindVolumeProfileType(volumeType);
            if (volumeType == null || profileType == null)
            {
                return null;
            }

            const string k_HdacTypeName =
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
            System.Type hdacType = System.Type.GetType(k_HdacTypeName);
            if (hdacType == null)
            {
                return null;
            }
            System.Type expType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.Exposure"
            );
            if (expType == null)
            {
                return null;
            }

            try
            {
                var profile = ScriptableObject.CreateInstance(profileType);
                createdProfile = profile;

                // Find VolumeProfile.Add<T>
                System.Type baseProfileType = profileType;
                while (baseProfileType != null && baseProfileType.Name != "VolumeProfile")
                {
                    baseProfileType = baseProfileType.BaseType;
                }
                if (baseProfileType == null)
                {
                    baseProfileType = profileType;
                }
                System.Reflection.MethodInfo addMethod = null;
                foreach (
                    System.Reflection.MethodInfo m in baseProfileType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance
                    )
                )
                {
                    if (m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        addMethod = m;
                        break;
                    }
                }
                if (addMethod == null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }

                // Add Exposure with overrideState=false (default) so we only
                // override the two params we need: mode and fixedExposure.
                // Using true would override ALL params (limits, metering,
                // compensation, ...) with defaults, breaking the scene's
                // carefully tuned Exposure settings.
                System.Reflection.MethodInfo addExp = addMethod.MakeGenericMethod(expType);
                addExp.Invoke(profile, new object[] { false });

                // Configure Exposure: only override mode and fixedExposure.
                // Set overrideState=true on these two params so HDRP uses
                // our values; all other Exposure params pass through from
                // scene volumes or HDRP defaults.
                System.Reflection.FieldInfo componentsField = profileType.GetField(
                    "components",
                    BindingFlags.Public | BindingFlags.Instance
                );
                var comps =
                    componentsField != null
                        ? componentsField.GetValue(profile) as System.Collections.IList
                        : null;
                if (comps != null)
                {
                    foreach (object c in comps)
                    {
                        if (c == null)
                        {
                            continue;
                        }
                        if (expType.IsAssignableFrom(c.GetType()))
                        {
                            SetVolumeParameterOverride(c, "mode", 0);
                            SetVolumeParameterOverride(c, "fixedExposure", HdrpFixedExposure);
                        }
                    }
                }
                // Create Volume GO with highest priority
                var volumeGo = new GameObject("__UniThumbTempExposureVolume");
                volumeGo.hideFlags = HideFlags.HideAndDontSave;
                Component volume = volumeGo.AddComponent(volumeType);
                if (volume == null)
                {
                    UnityEngine.Object.DestroyImmediate(volumeGo);
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }
                PropertyInfo isGlobalProp = volumeType.GetProperty(
                    "isGlobal",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (isGlobalProp != null && isGlobalProp.CanWrite)
                {
                    isGlobalProp.SetValue(volume, true, null);
                }
                // Highest priority so this Exposure overrides scene volumes
                PropertyInfo priorityProp = volumeType.GetProperty(
                    "priority",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (priorityProp != null && priorityProp.CanWrite)
                {
                    priorityProp.SetValue(volume, float.MaxValue, null);
                }
                PropertyInfo profileProp = volumeType.GetProperty(
                    "profile",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (profileProp != null && profileProp.CanWrite)
                {
                    profileProp.SetValue(volume, profile, null);
                }

                return volumeGo;
            }
            catch (System.Exception exception)
            {
                if (createdProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                }
                Debug.LogWarning(
                    k_LogPrefix
                        + "Could not create HDRP exposure override volume ("
                        + exception.GetType().Name
                        + ": "
                        + exception.Message
                        + ")."
                );
                return null;
            }
        }

        /// <summary>
        /// Creates a high-priority global Volume that disables Depth of Field
        /// (focusMode=None) and Motion Blur (intensity=0) for the capture
        /// camera. Scene volumes may carry DOF calibrated for the SceneView
        /// camera or motion blur that smears single-frame renders; this
        /// override ensures crisp captures.
        /// Called when HDRP is detected, after the Exposure override volume.
        /// The caller destroys the GO in finally.
        /// </summary>
        internal static GameObject TryCreateHdrpCaptureOverrideVolume(
            out UnityEngine.Object createdProfile
        )
        {
            createdProfile = null;
            System.Type volumeType = FindVolumeType();
            System.Type profileType = FindVolumeProfileType(volumeType);
            if (volumeType == null || profileType == null)
            {
                return null;
            }

            const string k_HdacTypeName =
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
            System.Type hdacType = System.Type.GetType(k_HdacTypeName);
            if (hdacType == null)
            {
                return null;
            }
            System.Type dofType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.DepthOfField"
            );
            System.Type mbType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.MotionBlur"
            );
            if (dofType == null && mbType == null)
            {
                return null;
            }

            try
            {
                var profile = ScriptableObject.CreateInstance(profileType);
                createdProfile = profile;

                // Find VolumeProfile.Add<T>
                System.Type baseProfileType = profileType;
                while (baseProfileType != null && baseProfileType.Name != "VolumeProfile")
                {
                    baseProfileType = baseProfileType.BaseType;
                }
                if (baseProfileType == null)
                {
                    baseProfileType = profileType;
                }
                System.Reflection.MethodInfo addMethod = null;
                foreach (
                    System.Reflection.MethodInfo m in baseProfileType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance
                    )
                )
                {
                    if (m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        addMethod = m;
                        break;
                    }
                }
                if (addMethod == null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }

                // Add DepthOfField with overrideState=false (default) so we
                // only override focusMode to disable DOF.
                if (dofType != null)
                {
                    System.Reflection.MethodInfo addDof = addMethod.MakeGenericMethod(dofType);
                    addDof.Invoke(profile, new object[] { false });
                }

                // Add MotionBlur with overrideState=false (default) so we
                // only override intensity to disable motion blur.
                if (mbType != null)
                {
                    System.Reflection.MethodInfo addMb = addMethod.MakeGenericMethod(mbType);
                    addMb.Invoke(profile, new object[] { false });
                }

                // Configure the components we just added.
                System.Reflection.FieldInfo componentsField = profileType.GetField(
                    "components",
                    BindingFlags.Public | BindingFlags.Instance
                );
                var comps =
                    componentsField != null
                        ? componentsField.GetValue(profile) as System.Collections.IList
                        : null;
                if (comps != null)
                {
                    foreach (object c in comps)
                    {
                        if (c == null)
                        {
                            continue;
                        }
                        if (dofType != null && dofType.IsAssignableFrom(c.GetType()))
                        {
                            // focusMode = 0 (None) disables DOF entirely
                            SetVolumeParameterOverride(c, "focusMode", 0);
                        }
                        if (mbType != null && mbType.IsAssignableFrom(c.GetType()))
                        {
                            // intensity = 0 disables motion blur
                            SetVolumeParameterOverride(c, "intensity", 0f);
                        }
                    }
                }

                // Create Volume GO with highest priority
                var volumeGo = new GameObject("__UniThumbTempCaptureOverrideVolume");
                volumeGo.hideFlags = HideFlags.HideAndDontSave;
                Component volume = volumeGo.AddComponent(volumeType);
                if (volume == null)
                {
                    UnityEngine.Object.DestroyImmediate(volumeGo);
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                    return null;
                }
                PropertyInfo isGlobalProp = volumeType.GetProperty(
                    "isGlobal",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (isGlobalProp != null && isGlobalProp.CanWrite)
                {
                    isGlobalProp.SetValue(volume, true, null);
                }
                // Highest priority so this overrides scene volumes
                PropertyInfo priorityProp = volumeType.GetProperty(
                    "priority",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (priorityProp != null && priorityProp.CanWrite)
                {
                    priorityProp.SetValue(volume, float.MaxValue, null);
                }
                PropertyInfo profileProp = volumeType.GetProperty(
                    "profile",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (profileProp != null && profileProp.CanWrite)
                {
                    profileProp.SetValue(volume, profile, null);
                }
                return volumeGo;
            }
            catch (System.Exception exception)
            {
                if (createdProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(createdProfile);
                    createdProfile = null;
                }
                Debug.LogWarning(
                    k_LogPrefix
                        + "Could not create HDRP capture override volume ("
                        + exception.GetType().Name
                        + ": "
                        + exception.Message
                        + ")."
                );
                return null;
            }
        }

        /// <summary>
        /// Returns true when the project uses HDRP (HDAdditionalCameraData type
        /// exists in the assembly graph). dyn-05 KEEP-DYNAMIC: HDRP is absent
        /// on URP-only installs; a hard reference would force a package
        /// install, so this probe stays dynamic with a fail-open false path.
        /// </summary>
        internal static bool IsHdrpPipeline()
        {
            const string k_HdacTypeName =
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
            return System.Type.GetType(k_HdacTypeName) != null;
        }

        /// <summary>
        /// Ensures the given VolumeProfile contains VisualEnvironment and
        /// Exposure components (required by HDRP for sky rendering and
        /// HDR-to-LDR tonemapping). When either is missing, clones the
        /// profile asset to avoid modifying it, adds fresh components with
        /// default settings, and returns the clone. When both are already
        /// present, returns the original unchanged.
        /// </summary>
        private static UnityEngine.Object EnsureHdrpBaselineComponents(
            UnityEngine.Object profile,
            System.Type profileType
        )
        {
            if (profile == null)
            {
                return profile;
            }

            const string k_HdacTypeName =
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime";
            System.Type hdacType = System.Type.GetType(k_HdacTypeName);
            if (hdacType == null)
            {
                return profile;
            }

            System.Type veType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.VisualEnvironment"
            );
            System.Type expType = hdacType.Assembly.GetType(
                "UnityEngine.Rendering.HighDefinition.Exposure"
            );
            if (veType == null && expType == null)
            {
                return profile;
            }

            // Read the profile's components list
            System.Reflection.FieldInfo componentsField = profileType.GetField(
                "components",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (componentsField == null)
            {
                return profile;
            }

            var components = default(System.Collections.IList);
            try
            {
                components = componentsField.GetValue(profile) as System.Collections.IList;
            }
            catch (System.Exception)
            {
                return profile;
            }
            if (components == null)
            {
                return profile;
            }

            // Check which components exist and which need to be added
            bool hasVe = false;
            bool hasExp = false;
            foreach (object c in components)
            {
                if (c == null)
                {
                    continue;
                }
                System.Type t = c.GetType();
                if (veType != null && veType.IsAssignableFrom(t))
                {
                    hasVe = true;
                }
                if (expType != null && expType.IsAssignableFrom(t))
                {
                    hasExp = true;
                }
            }

            bool needsAdd = (veType != null && !hasVe) || (expType != null && !hasExp);

            // Clone the profile so we never modify the source asset
            UnityEngine.Object clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(profile);
                clone.name = profile.name;
            }
            catch (System.Exception)
            {
                return profile;
            }

            try
            {
                // Get the cloned components list for adding new components
                System.Collections.IList clonedComponents = null;
                if (needsAdd)
                {
                    try
                    {
                        clonedComponents =
                            componentsField.GetValue(clone) as System.Collections.IList;
                    }
                    catch (System.Exception)
                    {
                        // Best-effort: clone exists but components unreadable;
                        // Add<T> below will also fail, falling through to
                        // return the clone as-is (no new warnings).
                    }
                }

                // Find VolumeProfile.Add<T> for adding missing components
                System.Reflection.MethodInfo addMethod = null;
                if (needsAdd)
                {
                    System.Type baseProfileType = profileType;
                    while (baseProfileType != null && baseProfileType.Name != "VolumeProfile")
                    {
                        baseProfileType = baseProfileType.BaseType;
                    }
                    if (baseProfileType == null)
                    {
                        baseProfileType = profileType;
                    }
                    foreach (
                        System.Reflection.MethodInfo m in baseProfileType.GetMethods(
                            BindingFlags.Public | BindingFlags.Instance
                        )
                    )
                    {
                        if (m.Name == "Add" && m.IsGenericMethod && m.GetParameters().Length == 1)
                        {
                            addMethod = m;
                            break;
                        }
                    }
                }

                // Add VisualEnvironment if missing
                if (veType != null && !hasVe && addMethod != null)
                {
                    try
                    {
                        System.Reflection.MethodInfo addVe = addMethod.MakeGenericMethod(veType);
                        addVe.Invoke(clone, new object[] { false });
                    }
                    catch (System.Exception)
                    {
                        // Component type not available
                    }
                }

                // Add Exposure if missing
                if (expType != null && !hasExp && addMethod != null)
                {
                    try
                    {
                        System.Reflection.MethodInfo addExp = addMethod.MakeGenericMethod(expType);
                        addExp.Invoke(clone, new object[] { false });
                    }
                    catch (System.Exception)
                    {
                        // Component type not available
                    }
                }

                // Now configure existing (or just-added) components on the clone.
                // Always override -- the profile's original settings (auto-exposure,
                // skyType=None, etc.) would render incorrectly for captures.
                System.Collections.IList finalComps = null;
                try
                {
                    finalComps = componentsField.GetValue(clone) as System.Collections.IList;
                }
                catch (System.Exception)
                {
                    // Best-effort: clone exists but components unreadable;
                    // return the clone without configuration so the caller
                    // can still assign it to the volume (no new warnings).
                }
                if (finalComps == null)
                {
                    UnityEngine.Object resultEarly = clone;
                    clone = null;
                    return resultEarly;
                }
                foreach (object c in finalComps)
                {
                    if (c == null)
                    {
                        continue;
                    }
                    System.Type t = c.GetType();

                    // Configure VisualEnvironment: ProceduralSky (3)
                    if (veType != null && veType.IsAssignableFrom(t))
                    {
                        SetVolumeParameterInt(c, "skyType", 3);
                    }

                    // Configure Exposure: Fixed mode (0) at EV 14 so HDRP
                    // applies tonemapping (HDR -> LDR). Without an active
                    // Exposure component the output is dark raw HDR.
                    if (expType != null && expType.IsAssignableFrom(t))
                    {
                        SetVolumeParameterInt(c, "mode", 0);
                        SetVolumeParameterFloat(c, "fixedExposure", HdrpFixedExposure);
                    }
                }

                // Null out so finally does not destroy it -- the caller owns
                // the returned clone and is responsible for destroying it.
                UnityEngine.Object result = clone;
                clone = null;
                return result;
            }
            finally
            {
                // If the clone was created but an error occurred during
                // configuration, destroy it so it does not leak.
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
            }
        }

        /// <summary>
        /// Sets a VolumeParameter&lt;T&gt; int field on a VolumeComponent via
        /// reflection. Finds the named field, then sets its "value" sub-field.
        /// </summary>
        private static void SetVolumeParameterInt(object component, string fieldName, int newValue)
        {
            try
            {
                System.Reflection.FieldInfo field = component
                    .GetType()
                    .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    return;
                }
                object param = field.GetValue(component);
                if (param == null)
                {
                    return;
                }
                System.Type paramType = param.GetType();

                // Walk hierarchy to find value â€” try field first, then property, per level
                System.Reflection.FieldInfo valueField = null;
                System.Reflection.PropertyInfo valueProp = null;
                for (
                    System.Type t = paramType;
                    t != null && valueField == null && valueProp == null;
                    t = t.BaseType
                )
                {
                    valueField = t.GetField(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    valueProp = t.GetProperty(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (valueField != null)
                {
                    valueField.SetValue(param, newValue);
                }
                else if (valueProp != null && valueProp.CanWrite)
                {
                    valueProp.SetValue(param, newValue, null);
                }
            }
            catch (System.Exception exception)
            {
                // Best-effort (fail-open): log at debug level and continue
                // without the override.
                Debug.Log(
                    k_LogPrefix
                        + "SetVolumeParameter failed for '"
                        + fieldName
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Sets a VolumeParameter&lt;T&gt; float field on a VolumeComponent via
        /// reflection.
        /// </summary>
        private static void SetVolumeParameterFloat(
            object component,
            string fieldName,
            float newValue
        )
        {
            try
            {
                System.Reflection.FieldInfo field = component
                    .GetType()
                    .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    return;
                }
                object param = field.GetValue(component);
                if (param == null)
                {
                    return;
                }
                System.Type paramType = param.GetType();

                // Walk hierarchy to find value â€” try field first, then property, per level
                System.Reflection.FieldInfo valueField = null;
                System.Reflection.PropertyInfo valueProp = null;
                for (
                    System.Type t = paramType;
                    t != null && valueField == null && valueProp == null;
                    t = t.BaseType
                )
                {
                    valueField = t.GetField(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    valueProp = t.GetProperty(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (valueField != null)
                {
                    valueField.SetValue(param, newValue);
                }
                else if (valueProp != null && valueProp.CanWrite)
                {
                    valueProp.SetValue(param, newValue, null);
                }
            }
            catch (System.Exception exception)
            {
                // Best-effort (fail-open): log at debug level and continue
                // without the override.
                Debug.Log(
                    k_LogPrefix
                        + "SetVolumeParameter failed for '"
                        + fieldName
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Sets a VolumeParameter&lt;T&gt; int field on a VolumeComponent via
        /// reflection, with overrideState=true so HDRP uses the value in the
        /// volume stack. Finds the named field, sets its "overrideState"
        /// sub-field to true, then sets its "value" sub-field.
        /// </summary>
        private static void SetVolumeParameterOverride(
            object component,
            string fieldName,
            int newValue
        )
        {
            try
            {
                System.Reflection.FieldInfo field = component
                    .GetType()
                    .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    return;
                }
                object param = field.GetValue(component);
                if (param == null)
                {
                    return;
                }
                System.Type paramType = param.GetType();

                // Walk hierarchy to find overrideState â€” try field first, then property, per level
                System.Reflection.FieldInfo overrideField = null;
                System.Reflection.PropertyInfo overrideProp = null;
                for (
                    System.Type t = paramType;
                    t != null && overrideField == null && overrideProp == null;
                    t = t.BaseType
                )
                {
                    overrideField = t.GetField(
                        "overrideState",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    overrideProp = t.GetProperty(
                        "overrideState",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (overrideField != null)
                {
                    overrideField.SetValue(param, true);
                }
                else if (overrideProp != null && overrideProp.CanWrite)
                {
                    overrideProp.SetValue(param, true, null);
                }

                // Walk hierarchy to find value â€” try field first, then property, per level
                System.Reflection.FieldInfo valueField = null;
                System.Reflection.PropertyInfo valueProp = null;
                for (
                    System.Type t = paramType;
                    t != null && valueField == null && valueProp == null;
                    t = t.BaseType
                )
                {
                    valueField = t.GetField(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    valueProp = t.GetProperty(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (valueField != null)
                {
                    // Enums require Enum.ToObject for correct boxing on Mono.
                    object boxedValue = valueField.FieldType.IsEnum
                        ? System.Enum.ToObject(valueField.FieldType, newValue)
                        : newValue;
                    valueField.SetValue(param, boxedValue);
                }
                else if (valueProp != null && valueProp.CanWrite)
                {
                    object boxedValue = valueProp.PropertyType.IsEnum
                        ? System.Enum.ToObject(valueProp.PropertyType, newValue)
                        : newValue;
                    valueProp.SetValue(param, boxedValue, null);
                }
            }
            catch (System.Exception exception)
            {
                // Best-effort (fail-open): log at debug level and continue
                // without the override.
                Debug.Log(
                    k_LogPrefix
                        + "SetVolumeParameter failed for '"
                        + fieldName
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Sets a VolumeParameter&lt;T&gt; float field on a VolumeComponent via
        /// reflection, with overrideState=true so HDRP uses the value in the
        /// volume stack.
        /// </summary>
        private static void SetVolumeParameterOverride(
            object component,
            string fieldName,
            float newValue
        )
        {
            try
            {
                System.Reflection.FieldInfo field = component
                    .GetType()
                    .GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    return;
                }
                object param = field.GetValue(component);
                if (param == null)
                {
                    return;
                }
                System.Type paramType = param.GetType();

                // Walk hierarchy to find overrideState â€” try field first, then property, per level
                System.Reflection.FieldInfo overrideField = null;
                System.Reflection.PropertyInfo overrideProp = null;
                for (
                    System.Type t = paramType;
                    t != null && overrideField == null && overrideProp == null;
                    t = t.BaseType
                )
                {
                    overrideField = t.GetField(
                        "overrideState",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    overrideProp = t.GetProperty(
                        "overrideState",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (overrideField != null)
                {
                    overrideField.SetValue(param, true);
                }
                else if (overrideProp != null && overrideProp.CanWrite)
                {
                    overrideProp.SetValue(param, true, null);
                }

                // Walk hierarchy to find value â€” try field first, then property, per level
                System.Reflection.FieldInfo valueField = null;
                System.Reflection.PropertyInfo valueProp = null;
                for (
                    System.Type t = paramType;
                    t != null && valueField == null && valueProp == null;
                    t = t.BaseType
                )
                {
                    valueField = t.GetField(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                    valueProp = t.GetProperty(
                        "value",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    );
                }
                if (valueField != null)
                {
                    valueField.SetValue(param, newValue);
                }
                else if (valueProp != null && valueProp.CanWrite)
                {
                    valueProp.SetValue(param, newValue, null);
                }
            }
            catch (System.Exception exception)
            {
                // Best-effort (fail-open): log at debug level and continue
                // without the override.
                Debug.Log(
                    k_LogPrefix
                        + "SetVolumeParameter failed for '"
                        + fieldName
                        + "': "
                        + exception.Message
                );
            }
        }

        /// <summary>
        /// Resolves the VolumeProfile a temporary volume should use. The
        /// assigned settings.PostProcessingProfile wins when it is set and is
        /// actually a VolumeProfile (even over scene Volumes). Otherwise
        /// returns null when the scene has Volumes (honored as-is), else the
        /// first usable project profile (LoadUsableVolumeProfile).
        /// </summary>
        /// <summary>
        /// Scan-aware variant used by CaptureCore: the Volume check reuses
        /// the shared snapshot and the project profile lookup reuses the
        /// cached guids. Resolution is identical to baseline.
        /// </summary>
        private static UnityEngine.Object ResolvePostProcessingProfile(
            CaptureSettings settings,
            System.Type profileType,
            ScanCache scan
        )
        {
            UnityEngine.Object assigned = settings.PostProcessingProfile;
            if (assigned != null && profileType.IsAssignableFrom(assigned.GetType()))
            {
                return assigned;
            }
            if (HasPostProcessingVolumes(scan))
            {
                return null;
            }
            string[] guids = GetVolumeProfileGuidsCached();
            if (guids.Length == 0)
            {
                return null;
            }
            return LoadUsableVolumeProfile(guids, profileType);
        }

        /// <summary>
        /// Loads the first VolumeProfile whose effects would actually change
        /// the rendered image (HasVisiblePostProcessing), falling back to the
        /// first profile when none qualifies. This skips neutral/default
        /// profiles (e.g. URP's package test artifact) whose injection would
        /// make post-processing look broken.
        /// </summary>
        private static UnityEngine.Object LoadUsableVolumeProfile(
            string[] guids,
            System.Type profileType
        )
        {
            for (int i = 0; i < guids.Length; i++)
            {
                UnityEngine.Object profile = AssetDatabase.LoadAssetAtPath(
                    AssetDatabase.GUIDToAssetPath(guids[i]),
                    profileType
                );
                if (profile != null && HasVisiblePostProcessing(profile))
                {
                    return profile;
                }
            }
            return AssetDatabase.LoadAssetAtPath(
                AssetDatabase.GUIDToAssetPath(guids[0]),
                profileType
            );
        }

        /// <summary>
        /// True when the profile has at least one active component with an
        /// overridden parameter that differs from the component's default
        /// (fresh-instance) value, i.e. effects that would visibly change the
        /// rendered image. Skips parameters that cannot change output: null
        /// object references (fake-null Unity objects included) and TextureCurve
        /// parameters (reference equality), and the ProbeVolumesOptions
        /// component (probe-volume configuration only, no screen-space effect).
        /// Typeless under all pipelines (zero SRP refs on the core assembly):
        /// the parameter value has no non-generic getter, so it uses a narrow
        /// instance lookup (field, then property). HDRP component names stay
        /// string-matched: HDRP remains dynamic per dyn-05.
        /// </summary>
        private static bool HasVisiblePostProcessing(UnityEngine.Object profile)
        {
            System.Reflection.FieldInfo componentsField = profile
                .GetType()
                .GetField(
                    "components",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                );
            if (componentsField == null)
            {
                return false;
            }
            var components = componentsField.GetValue(profile) as System.Collections.IList;
            if (components == null)
            {
                return false;
            }
            foreach (object component in components)
            {
                if (component == null)
                {
                    continue;
                }
                System.Type type = component.GetType();
                if (type.Name == "ProbeVolumesOptions")
                {
                    continue;
                }
                System.Reflection.PropertyInfo activeProp = type.GetProperty(
                    "active",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                );
                bool active =
                    activeProp != null ? (bool)activeProp.GetValue(component, null) : true;
                if (!active)
                {
                    continue;
                }
                UnityEngine.Object fresh = ScriptableObject.CreateInstance(type);
                try
                {
                    foreach (
                        System.Reflection.FieldInfo field in type.GetFields(
                            System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.Instance
                        )
                    )
                    {
                        if (!IsVolumeParameterType(field.FieldType))
                        {
                            continue;
                        }
                        object param = field.GetValue(component);
                        if (param == null)
                        {
                            continue;
                        }
                        System.Reflection.PropertyInfo overrideProp = param
                            .GetType()
                            .GetProperty(
                                "overrideState",
                                System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Instance
                            );
                        if (overrideProp == null || !(bool)overrideProp.GetValue(param, null))
                        {
                            continue;
                        }
                        if (field.FieldType.Name.Contains("TextureCurve"))
                        {
                            continue;
                        }
                        System.Reflection.PropertyInfo valueProp = param
                            .GetType()
                            .GetProperty(
                                "value",
                                System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Instance
                            );
                        object value = valueProp != null ? valueProp.GetValue(param, null) : null;
                        // Unity-null object references (including fake-null
                        // missing textures) cannot change the rendered image.
                        bool isNullValue =
                            value == null || (value is UnityEngine.Object uo && uo == null);
                        if (isNullValue)
                        {
                            continue;
                        }
                        object freshParam = field.GetValue(fresh);
                        if (freshParam != null && !param.Equals(freshParam))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(fresh);
                }
            }
            return false;
        }

        /// <summary>
        /// True when the given type (or any base) is a VolumeParameter<T>
        /// (generic SRP Core parameter class). Name-based so the core
        /// assembly keeps zero SRP refs.
        /// </summary>
        private static bool IsVolumeParameterType(System.Type type)
        {
            for (System.Type current = type; current != null; current = current.BaseType)
            {
                if (current.Name == "VolumeParameter`1")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The UnityEngine.Rendering.VolumeProfile type (typeless lookup,
        /// null when SRP Core is unavailable). Lets the
        /// window restrict the PP Profile object field to VolumeProfile
        /// assets without hard SRP references on the core assembly.
        /// </summary>
        internal static System.Type GetPostProcessingProfileType()
        {
            return FindVolumeProfileType(FindVolumeType());
        }

        /// <summary>
        /// The UnityEngine.Rendering.VolumeProfile type: typeless
        /// assembly-qualified lookup under HAS_SRP_CORE, null when unavailable.
        /// dyn-05: obsoleted assembly-scan seam explicitly updated to a typed
        /// seam (not removed) so HDRP builders and the isolation-test mirror
        /// keep a single seam.
        /// </summary>
        private static System.Type FindVolumeProfileType(System.Type volumeType)
        {
#if HAS_SRP_CORE
            if (volumeType == null)
            {
                return null;
            }
            return System.Type.GetType(
                "UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime"
            );
#else
            return null;
#endif
        }

        /// <summary>
        /// The UnityEngine.Rendering.Volume type: typeless
        /// assembly-qualified lookup under HAS_SRP_CORE (which ships with
        /// URP and HDRP).
        /// Null without the Core package (fail-open: callers skip Volumes).
        /// dyn-05: obsoleted assembly-scan seam explicitly updated to a typed
        /// seam (not removed) so volume builders keep a single seam.
        /// </summary>
        private static System.Type FindVolumeType()
        {
#if HAS_SRP_CORE
            return System.Type.GetType(
                "UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime"
            );
#else
            return null;
#endif
        }

        /// <summary>
        /// The URP12+ Light2D type resolved through the UrpBridge shim
        /// (zero Universal refs in core). Null without URP (fail-open:
        /// callers warn or skip).
        /// </summary>
        private static System.Type FindLight2DType()
        {
#if HAS_URP
            return UniThumbUrp.Light2DType;
#else
            return null;
#endif
        }

        /// <summary>
        /// Probes the Light2D.Type enum for the Global variant by name.
        /// Returns the integer value of the Global member, the
        /// first enum value when no name matches, or 0 when the type carries
        /// no usable values. Fail-open by design: unknown names and
        /// reflection failures log a warning and fall through, never throw
        /// (mirrors the defensive Postprocess name lookup used for the HDRP
        /// frame settings).
        /// </summary>
        internal static int FindLight2DGlobalModeValue(System.Type lightTypeType)
        {
            if (lightTypeType == null || !lightTypeType.IsEnum)
            {
                Debug.LogWarning(
                    k_LogPrefix + "Light2D light-type enum not found; defaulting to 0."
                );
                return 0;
            }
            // Only "Global" identifies the global light type. Other member
            // names (Point, Sprite, Parametric, FreeForm) are non-global
            // kinds: accepting the first hit among them mislabels a
            // non-global value as global, so they are not candidates here.
            // When "Global" is absent (version drift), fall through to the
            // first-value fail-open below with a warning, never throw.
            if (System.Enum.TryParse(lightTypeType, "Global", true, out object parsed))
            {
                try
                {
                    if (parsed != null && System.Enum.IsDefined(lightTypeType, parsed))
                    {
                        return System.Convert.ToInt32(parsed);
                    }
                }
                catch (System.Exception)
                {
                    // Unusable value: fall through to the first-value default.
                }
            }
            // Fallback: return the first enum value (usually 0)
            try
            {
                System.Array values = System.Enum.GetValues(lightTypeType);
                if (values.Length > 0)
                {
                    Debug.LogWarning(
                        k_LogPrefix + "Light2D global mode not found; defaulting to 0."
                    );
                    return System.Convert.ToInt32(values.GetValue(0));
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning(
                    k_LogPrefix + "Could not read Light2D light-type values: " + exception.Message
                );
            }
            Debug.LogWarning(k_LogPrefix + "Light2D global mode not found; defaulting to 0.");
            return 0;
        }

        /// <summary>
        /// Resolves the Light2D "lightType" enum type from the Light2D
        /// component type. Shim fast path under HAS_URP; dynamic probe
        /// otherwise so the caller fails open instead of throwing.
        /// </summary>
        internal static System.Type TryGetLight2DTypeEnum(System.Type light2DType)
        {
#if HAS_URP
            if (UniThumbUrp.IsLight2DType(light2DType))
            {
                return UniThumbUrp.Light2DLightType;
            }
#endif
            try
            {
                if (light2DType == null)
                {
                    return null;
                }
                System.Reflection.PropertyInfo prop = light2DType.GetProperty("lightType");
                if (prop == null || prop.PropertyType == null || !prop.PropertyType.IsEnum)
                {
                    return null;
                }
                return prop.PropertyType;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Spawns a temporary GameObject with a Global Light2D component configured
        /// with the specified intensity. When sortingLayerIds is non-null and
        /// non-empty, the light is restricted to those sorting layers; otherwise
        /// it affects all layers (the Light2D default). The caller must destroy
        /// the returned GO when done (typically in a finally block).
        /// </summary>
        internal static GameObject CreateTempLight2D(float intensity, int[] sortingLayerIds = null)
        {
#if HAS_URP
            // Shim path: UrpBridge owns the typed Light2D setup (hide flags,
            // Global mode, sorting-layer serialization); core keeps zero
            // Universal refs.
            return UniThumbUrp.CreateTempGlobalLight(
                intensity,
                sortingLayerIds,
                FindLight2DGlobalModeValue(UniThumbUrp.Light2DLightType)
            );
#else
            // dyn-05 Light2D-absent guard: fail open with a warning so Built-in
            // and HDRP callers skip the temp light instead of throwing.
            Debug.LogWarning(
                k_LogPrefix + "Light2D type not found; cannot create temporary light."
            );
            return null;
#endif
        }

        /// <summary>
        /// Spawns a temporary GameObject with a Directional Light component
        /// configured with the specified intensity, shadow, color, and rotation.
        /// The light is Realtime with the HDRP affect-sky flag set (reflection,
        /// HDRP gate only) so PBR/Procedural skies follow Yaw/Pitch when the
        /// scene has no directional of its own; HDRI skies stay static by
        /// design. Limit: without any pre-existing HD sky data the flag set is
        /// best-effort (miss is logged). The caller must destroy the returned
        /// GO when done (typically in a finally block): a leaked temp light
        /// with the affect flag would keep altering the scene sky.
        /// </summary>
        internal static GameObject CreateTempDirectionalLight(
            float intensity,
            bool shadows,
            Color color,
            float yaw,
            float pitch
        )
        {
            GameObject go = new GameObject("__UniThumbTempLight3D");
            // HideInHierarchy (not HideAndDontSave): URP's light culling
            // excludes HideAndDontSave objects from FindObjectsByType,
            // making the light invisible to the render pipeline. HideInHierarchy
            // hides the GO from the Hierarchy while remaining visible to URP.
            go.hideFlags = HideFlags.HideInHierarchy;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            light.color = color;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            go.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            return go;
        }

        /// <summary>
        /// Pure prefab-only fallback decision shared by CaptureCore and the
        /// live preview so the two paths cannot diverge. Scene captures
        /// (hasPrefabBounds false) and explicit lighting modes always return
        /// None. The prefab None path neutralizes EVERY scene light
        /// (DisableAllLights in CapturePrefab), so the fallback must always
        /// supply one prefab-scoped key light: perspective captures and
        /// orthographic captures of 3D content need a temp directional (a
        /// Global Light2D does not light 3D meshes); orthographic captures
        /// of sprite-only content need a temp Global Light2D. The only
        /// exception is a prefab carrying its own enabled key light
        /// (prefabHasOwnKey), which lights itself: no temp avoids
        /// double-lighting nested/variant prefabs. hasSceneDirectional and
        /// useLightingOverride are retained for call-site compatibility but
        /// no longer gate the decision: a scene directional cannot light the
        /// prefab because the neutralize step disabled it.
        /// When renderer2DOnly is true (Renderer2D-only URP pipeline), every
        /// prefab without its own key resolves to TempLight2D: 3D lights are
        /// off there (Renderer2D ignores standard Lights), so a TempLight3D
        /// would render black. Sprite content stays lit via the Global
        /// Light2D; 3D content falls back to the neutral ambient (warn-only
        /// placeholder downstream when unrenderable, never fail-closed).
        /// Mixed pipelines and 3D behavior are unchanged (false default).
        /// </summary>
        internal static PrefabFallbackLight ResolvePrefabFallbackLight(
            LightingMode mode,
            bool hasPrefabBounds,
            bool orthographic,
            bool useLightingOverride,
            bool hasSceneDirectional,
            bool has3DContent = true,
            bool prefabHasOwnKey = false,
            bool renderer2DOnly = false
        )
        {
            if (mode != LightingMode.None || !hasPrefabBounds)
            {
                return PrefabFallbackLight.None;
            }
            if (prefabHasOwnKey)
            {
                return PrefabFallbackLight.None;
            }
            if (renderer2DOnly)
            {
                return PrefabFallbackLight.TempLight2D;
            }
            if (!orthographic || has3DContent)
            {
                return PrefabFallbackLight.TempLight3D;
            }
            return PrefabFallbackLight.TempLight2D;
        }

        /// <summary>
        /// True when the prefab instance subtree holds at least one enabled
        /// 3D renderer (MeshRenderer, SkinnedMeshRenderer,
        /// ParticleSystemRenderer, LineRenderer, TrailRenderer, or
        /// BillboardRenderer on an active GameObject). Drives the fallback
        /// branch: ortho captures of 3D content need a directional key,
        /// sprite-only content needs a Light2D. Null root defaults to true
        /// (directional key, the safe side: it lights 3D and leaves unlit
        /// sprites untouched). One GetComponentsInChildren sweep per type;
        /// no LINQ, no per-item alloc.
        /// </summary>
        internal static bool PrefabSubtreeHas3DContent(GameObject root)
        {
            if (root == null)
            {
                return true;
            }
            MeshRenderer[] meshes = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshRenderer mesh = meshes[i];
                if (mesh != null && mesh.enabled && mesh.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer mesh = skinned[i];
                if (mesh != null && mesh.enabled && mesh.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            ParticleSystemRenderer[] particles =
                root.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystemRenderer renderer = particles[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            LineRenderer[] lines = root.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < lines.Length; i++)
            {
                LineRenderer renderer = lines[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                TrailRenderer renderer = trails[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            BillboardRenderer[] billboards = root.GetComponentsInChildren<BillboardRenderer>(true);
            for (int i = 0; i < billboards.Length; i++)
            {
                BillboardRenderer renderer = billboards[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when the prefab instance subtree carries its own enabled key
        /// light for the requested fallback kind: an enabled Directional
        /// Light on an active GameObject when needDirectional is true,
        /// otherwise any enabled Light2D component (shim dispatch under
        /// HAS_URP, absent-package fallback otherwise).
        /// Lets ResolvePrefabFallbackLight skip the temp key so prefabs with
        /// built-in lighting are not double-lit. Null-safe; no LINQ.
        /// </summary>
        internal static bool PrefabSubtreeHasOwnKeyLight(GameObject root, bool needDirectional)
        {
            if (root == null)
            {
                return false;
            }
            if (needDirectional)
            {
                Light[] lights = root.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    Light light = lights[i];
                    if (
                        light != null
                        && light.enabled
                        && light.gameObject.activeInHierarchy
                        && light.type == LightType.Directional
                    )
                    {
                        return true;
                    }
                }
                return false;
            }
#if HAS_URP
            // Shim path: typed GetComponentsInChildren lives in UrpBridge.
            Component[] components = UniThumbUrp.GetLight2DComponents(root);
            if (components == null)
            {
                return false;
            }
#else
            System.Type light2DType = FindLight2DType();
            if (light2DType == null)
            {
                return false;
            }
            Component[] components = root.GetComponentsInChildren(light2DType, true);
#endif
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                Behaviour behaviour = component as Behaviour;
                if (
                    behaviour != null
                    && behaviour.enabled
                    && behaviour.gameObject.activeInHierarchy
                )
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when a failed TempLight3D renderer switch must downgrade to
        /// a Global Light2D (with its warning): only on a genuine
        /// Renderer2D-only pipeline where the Light2D type exists.
        /// Indeterminate switch failures and projects without the 2D
        /// package stay silent. Pure decision shared by the implementation
        /// below and the regression tests.
        /// </summary>
        internal static bool ShouldDowngradeToLight2D(bool switchSucceeded, bool renderer2DOnly)
        {
            return !switchSucceeded && renderer2DOnly && FindLight2DType() != null;
        }

        /// <summary>
        /// True when the explicit Light3D key may run: Light3D mode on any
        /// pipeline EXCEPT a genuine Renderer2D-only URP pipeline, where 3D
        /// lights are off (Renderer2D ignores standard Lights, so a temp
        /// directional would render black and the renderer switch would warn
        /// spuriously). Renderer2D-only callers downgrade to a Global Light2D
        /// key instead (warn-only placeholder downstream when unrenderable,
        /// never fail-closed). Pure decision shared by CaptureCore and the
        /// live preview so the two cannot diverge.
        /// </summary>
        internal static bool ShouldApplyLight3D(LightingMode mode, bool renderer2DOnly)
        {
            return mode == LightingMode.Light3D && !renderer2DOnly;
        }

        /// <summary>
        /// Materializes a resolved prefab fallback into temp lights. TempLight3D
        /// switches the capture camera to a 3D URP renderer first; on a
        /// genuine Renderer2D-only pipeline no 3D light is created, no switch
        /// is attempted, and no 3D warning is logged: a Global Light2D is
        /// added instead so sprite content stays lit while 3D meshes fall
        /// back to the neutral ambient (null temp means ambient-only; the
        /// caller placeholder path stays warn-only, never fail-closed).
        /// Indeterminate switch failures (no URP, no camera data) stay
        /// silent: the camera keeps its current renderer and the directional
        /// key still lights 3D content. Without the Light2D type (no 2D
        /// package) there is nothing to downgrade to, so that path is silent
        /// too. TempLight2D disables pre-existing globals first (multiple global
        /// lights are a Renderer2D error); the snapshots are returned via
        /// disabledGlobals for the caller's finally restore. Owns nothing
        /// else: the caller destroys the returned GOs in finally. Shared by
        /// CaptureCore and the live preview so the two cannot diverge.
        /// </summary>
        internal static void EnsurePrefabFallbackLights(
            PrefabFallbackLight fallback,
            Camera cam,
            CaptureSettings settings,
            ScanCache scan,
            out GameObject tempLight2D,
            out GameObject tempLight3D,
            out List<Light2DSnapshot> disabledGlobals
        )
        {
            tempLight2D = null;
            tempLight3D = null;
            disabledGlobals = null;
            if (fallback == PrefabFallbackLight.TempLight2D)
            {
                disabledGlobals = DisableExistingGlobalLight2Ds(scan);
                tempLight2D = CreateTempLight2D(
                    settings.Light2DIntensity,
                    settings.Light2DSortingLayerIds
                );
            }
            else if (fallback == PrefabFallbackLight.TempLight3D)
            {
                // A genuine Renderer2D-only pipeline cannot render the
                // directional key: create a Global Light2D directly with no
                // switch attempt, no TempLight3D, and no 3D warning. Every
                // other path below is byte-identical for 3D/mixed pipelines.
                bool renderer2DOnly = PipelineHasOnly2DRenderers();
                if (renderer2DOnly)
                {
                    disabledGlobals = DisableExistingGlobalLight2Ds(scan);
                    tempLight2D = CreateTempLight2D(
                        settings.Light2DIntensity,
                        settings.Light2DSortingLayerIds
                    );
                    return;
                }
                bool switched = true;
                if (cam != null)
                {
                    switched = TrySwitchTo3DCameraRenderer(cam);
                }
                // None-mode orientation follows the framed capture camera so
                // the key aims at the framed prefab content (see
                // ResolvePrefabFallbackAim): the camera already carries the
                // bounds center + orbit yaw/pitch/framing. The fixed
                // defaults survive only as the null-camera fallback and the
                // Procedural-sky last resort inside the resolver.
                float fallbackYaw;
                float fallbackPitch;
                ResolvePrefabFallbackAim(cam, settings, out fallbackYaw, out fallbackPitch);
                // None-fallback TempLight3D shares the prefab intensity with
                // the explicit-Light3D prefab path; scene intensity stays out.
                tempLight3D = CreateTempDirectionalLight(
                    ResolveLight3DIntensity(ClampLight3DIntensity(settings.Light3DPrefabIntensity)),
                    settings.Light3DShadows,
                    settings.Light3DColor,
                    fallbackYaw,
                    fallbackPitch
                );
                if (ShouldDowngradeToLight2D(switched, renderer2DOnly))
                {
                    Debug.LogWarning(
                        k_LogPrefix
                            + "Prefab fallback needs a 3D URP renderer, but only 2D renderers exist. "
                            + "Added a Global Light2D so sprite content stays lit; 3D meshes fall back to the neutral ambient."
                    );
                    disabledGlobals = DisableExistingGlobalLight2Ds(scan);
                    tempLight2D = CreateTempLight2D(
                        settings.Light2DIntensity,
                        settings.Light2DSortingLayerIds
                    );
                }
            }
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Per-capture object snapshot shared by every sweep-based helper in
        /// one CaptureCore/CapturePrefab call (built once by BeginScan: 4 native
        /// sweeps; the 5th per-capture sweep is the UI Canvas pass). Arrays mirror the legacy sweep scopes:
        /// Renderers and Lights include inactive objects (callers filter by
        /// activeInHierarchy/isActiveAndEnabled, so results match the legacy
        /// active-only sweeps); Components and CanvasRenderers are
        /// active-only (matching the legacy Light2D/Volume/isolate sweeps).
        /// Method-local only: never stored across captures (see BeginScan).
        /// </summary>
        internal sealed class ScanCache
        {
            public Renderer[] Renderers;
            public Light[] Lights;
            public Component[] Components;
            public CanvasRenderer[] CanvasRenderers;
        }

        private enum ImageCheck
        {
            Ok,
            UniformBackground,
            UniformOther,
        }

        /// <summary>
        /// Captures enabled+intensity of a Light disabled by the lighting
        /// override so it can be restored afterwards. Shared with the live
        /// preview (UniThumbWindow) so both paths snapshot and restore the
        /// same state, including lights on inactive GameObjects.
        /// </summary>
        internal sealed class LightSnapshot
        {
            public Light Light;
            public bool Enabled;
            public float Intensity;
        }

        /// <summary>
        /// Captures the enabled state of an existing Global Light2D so it can
        /// be disabled during capture (to avoid the Renderer2D multiple-global
        /// light error) and restored afterwards.
        /// </summary>
        internal sealed class Light2DSnapshot
        {
            public Component Light2D;
            public bool WasEnabled;
        }

        /// <summary>
        /// Captures the RenderSettings environment neutralized for prefab
        /// captures (skybox/fog/ambient/reflections). Value snapshot taken by
        /// SnapshotPrefabEnvironment; restored by RestorePrefabEnvironment.
        /// Valid is false for default-constructed snapshots (restore no-op).
        /// </summary>
        internal struct PrefabEnvSnapshot
        {
            public bool Valid;
            public Material Skybox;
            public bool Fog;
            public Color FogColor;
            public float FogDensity;
            public AmbientMode AmbientMode;
            public Color AmbientLight;
            public Color AmbientSkyColor;
            public Color AmbientEquatorColor;
            public Color AmbientGroundColor;
            public float AmbientIntensity;
            public float ReflectionIntensity;
        }

        /// <summary>
        /// Captures the enabled state of a scene Volume disabled for prefab
        /// isolation so it can be restored afterwards.
        /// </summary>
        internal sealed class VolumeSnapshot
        {
            public Behaviour Volume;
            public bool WasEnabled;
        }

        /// <summary>
        /// Captures the enabled state of a scene Terrain disabled for prefab
        /// isolation so it can be restored afterwards.
        /// </summary>
        internal sealed class TerrainSnapshot
        {
            public Terrain Terrain;
            public bool WasEnabled;
        }

        /// <summary>
        /// Captures the enabled state of a scene VisualEffect disabled for prefab
        /// isolation so it can be restored afterwards. Stored as Behaviour: the
        /// VFX type resolves via FindVisualEffectType reflection with no hard
        /// package reference.
        /// </summary>
        internal sealed class VisualEffectSnapshot
        {
            public Behaviour Effect;
            public bool WasEnabled;
        }

        /// <summary>
        /// Captures the original layer of a hidden-clone GameObject moved
        /// onto the prefab isolation layer, so RestoreIsolatedLayers can put
        /// it back afterwards. Stored as a live reference: objects destroyed
        /// mid-capture read as Unity-null and are skipped on restore.
        /// </summary>
        internal sealed class LayerSnapshot
        {
            public GameObject Object;
            public int Layer;
        }

        /// <summary>
        /// Captures the state of an existing Directional Light so Light3D mode
        /// can apply its settings during capture and restore the originals after.
        /// </summary>
        internal sealed class LightSnapshot3D
        {
            public Light Light;
            public bool Enabled;
            public float Intensity;
            public Color Color;
            public LightShadows Shadows;
            public Quaternion Rotation;
            public LightmapBakeType BakeType;
        }

        private struct CanvasUiState
        {
            public Canvas Canvas;
            public RenderMode RenderMode;
            public Camera WorldCamera;
            public float PlaneDistance;
            public int SortingOrder;
            public int Layer;

            /// <summary>
            /// True when this canvas was scale-frozen (CanvasScaler disabled)
            /// or a uiScale != 1f override mutated it; the scale fields below
            /// are only restored for mutated entries.
            /// </summary>
            public bool ScaleMutated;
            public float ScaleFactor;
            public CanvasScaler Scaler;
            public bool ScalerEnabled;
        }

        /// <summary>
        /// Switches active Screen Space Overlay canvases to the given camera so they
        /// render into its target texture, and restores them on Dispose. Idempotent.
        /// Only canvases with renderMode == ScreenSpaceOverlay are touched; WorldSpace
        /// and already ScreenSpaceCamera canvases are left alone. BeginUiPass shares
        /// this snapshot/restore machinery for the composite UI pass; it returns null
        /// when no overlay canvas exists so callers can skip the pass. The composite
        /// UI pass (BeginUiPass) additionally moves touched canvases to layer 5 (UI):
        /// ScreenSpaceCamera rendering is keyed to the Canvas GameObject's layer, and
        /// the wide pass culling mask keeps only the UI layer bit, so canvases on
        /// other layers would render nothing. Layers are restored in Dispose, which
        /// runs in finally even when the pass throws.
        ///
        /// When uiScale != 1f the switch loop also overrides canvas.scaleFactor
        /// (snapshotting the original) and disables the canvas' CanvasScaler for
        /// the pass: the scaler is [ExecuteAlways] and re-applies scaleFactor on
        /// every preWillRenderCanvases, so it would fight the override. Dispose
        /// restores scaleFactor and re-enables the scaler. At uiScale == 1f zero
        /// scale mutation happens, keeping output byte-identical. The scale
        /// override serves the legacy square pass only (Begin); the composite
        /// pass (BeginUiPass) always passes 1f and never mutates scale.
        /// </summary>
        public sealed class UiCaptureSession : System.IDisposable
        {
            private readonly List<CanvasUiState> _snapshots = new List<CanvasUiState>();
            private bool _disposed;

            /// <summary>
            /// Number of canvases retargeted by this session (one snapshot
            /// per touched canvas). Lets callers distinguish a session that
            /// retargeted prefab UI (count greater than zero) from an empty
            /// one, so a uniform render with retargeted UI can be accepted
            /// while genuinely-empty renders still fail. Read-only.
            /// </summary>
            internal int SwitchedCount => _snapshots.Count;

            /// <summary>
            /// Test hook: when true, SwitchPrefabCanvases throws after the
            /// first prefab canvas switch so tests can verify Dispose
            /// restores all canvas state on throw. Defaults to false.
            /// </summary>
            internal static bool ThrowInPrefabSwitchForTest;

            private UiCaptureSession() { }

            /// <summary>
            /// Begins a UI capture session for the legacy square pass. When
            /// uiScale != 1f the switched canvases' scaleFactor is overridden
            /// with the CanvasScaler disabled for the pass; Dispose restores
            /// everything even when the pass throws. Defaults to 1f (no
            /// mutation, byte-identical output).
            /// </summary>
            public static UiCaptureSession Begin(Camera cam, float uiScale = 1f)
            {
                UiCaptureSession session = new UiCaptureSession();
                try
                {
                    SwitchOverlayCanvases(session, cam, false, uiScale);
                    Canvas.ForceUpdateCanvases();
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// Composite-pass variant of Begin: same snapshot/restore machinery and
            /// planeDistance formula, plus a forced move of touched canvases to
            /// layer 5 (UI) so the wide pass culling mask (UI layer bit only)
            /// renders them. The composite pass never scales canvases: the shared
            /// switch runs with uiScale 1f, so zero canvas mutation happens
            /// (canvas.scaleFactor stays 1, CanvasScaler stays enabled) and the
            /// full canvas layout renders; settings.UiScale is applied as a
            /// center-kept zoom in the composite sampling step instead. Returns
            /// null (session already disposed) when zero Screen Space Overlay
            /// canvases exist so the caller can skip the UI pass and keep the
            /// scene-only pixels.
            /// </summary>
            public static UiCaptureSession BeginUiPass(Camera uiCam)
            {
                UiCaptureSession session = new UiCaptureSession();
                try
                {
                    int switched = SwitchOverlayCanvases(session, uiCam, true, 1f);
                    Canvas.ForceUpdateCanvases();
                    if (switched == 0)
                    {
                        session.Dispose();
                        return null;
                    }
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// Prefab-subtree variant of Begin for the prefab orbit pass.
            /// Retargets canvases under prefabRoot only; scene canvases are
            /// never touched (they stay culled by
            /// IsolateInstanceCanvasRenderers). ScreenSpaceCamera canvases
            /// rebind worldCamera to the temp capture camera (authored
            /// planeDistance kept); ScreenSpaceOverlay canvases convert to
            /// ScreenSpaceCamera on the temp camera exactly like the scene
            /// legacy pass. Overlay conversion fidelity is approximate by
            /// design (overlay has no camera; planeDistance re-derives from
            /// the temp near plane, sorting preserved). WorldSpace canvases
            /// are left alone (they render through the normal 3D path).
            /// Layer-mask fix (S4): each touched canvas layer bit is ORed
            /// into the temp camera cullingMask (temp-owned, no restore
            /// needed) so retargeted prefab canvases are never culled by a
            /// narrowed settings.layerMask. Scene path (Begin/BeginUiPass)
            /// is untouched. Dispose restores everything in finally even
            /// when the pass throws. Resolution-framing fix: every touched
            /// canvas is scale-frozen (scaleFactor snapshotted, CanvasScaler
            /// disabled, ForceUpdate once in BeginPrefab) so the temp RT size
            /// cannot re-lay the canvas out between framing and render, and
            /// the frozen scaler cannot fight the pass scale. The pass scale
            /// itself is RT-proportional (capture RT over the scaler
            /// referenceResolution, with a ConstantPixelSize/null fallback to
            /// GetOverlayOnlyReferenceSize): the raw base holds an identical
            /// frame-fraction at every resolution (0.1875 at 16/128/512/2048
            /// for the 400x300 fixed probe). Overlay-converted canvases take
            /// the raw base with no k_PrefabRtScaleMin floor (the floor broke
            /// proportionality: 0.25@128 vs 0.16 raw, and overflowed uniform
            /// at 16) multiplied by UiScale; Camera canvases take the floored
            /// base, UiScale stays Overlay-scoped.
            /// Null root or null camera returns an empty session (no-op).
            /// </summary>
            public static UiCaptureSession BeginPrefab(
                GameObject prefabRoot,
                Camera cam,
                float uiScale = 1f
            )
            {
                UiCaptureSession session = new UiCaptureSession();
                try
                {
                    SwitchPrefabCanvases(session, prefabRoot, cam, uiScale);
                    Canvas.ForceUpdateCanvases();
                    return session;
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// RT-proportional scale base for one prefab canvas: replicates the
            /// CanvasScaler ScaleWithScreenSize mapping with the capture RT size
            /// in place of the editor display size, so the canvas holds a constant
            /// reference-pixel layout across thumbnail resolutions. Overlay-
            /// converted canvases take this base raw (no floor); the Camera
            /// path floors it with k_PrefabRtScaleMin. A null scaler or a
            /// ConstantPixelSize scaler falls back to GetOverlayOnlyReferenceSize
            /// (first ScaleWithScreenSize reference else 800x600) with Expand
            /// mapping, so fixed-pixel prefabs (ButtonPanel, HealthBar) get the
            /// same raw RT/reference base instead of bypassing the scale while
            /// the canvas rect tracks RT size. Returns false (out 1f, caller
            /// keeps the legacy freeze) when the camera carries no RT (both
            /// production call sites assign targetTexture before BeginPrefab),
            /// or when the reference/RT sizes are degenerate.
            /// </summary>
            internal static bool TryComputePrefabRtScale(
                CanvasScaler scaler,
                Camera cam,
                out float rtScale
            )
            {
                return TryComputePrefabRtScale(scaler, cam, null, out rtScale);
            }

            /// <summary>
            /// Prefab-root variant of TryComputePrefabRtScale. The root feeds
            /// the ConstantPixelSize/null fallback reference only; a
            /// ScaleWithScreenSize scaler never reads it. Null root falls back
            /// to the 800x600 default via GetOverlayOnlyReferenceSize.
            /// </summary>
            internal static bool TryComputePrefabRtScale(
                CanvasScaler scaler,
                Camera cam,
                GameObject prefabRoot,
                out float rtScale
            )
            {
                rtScale = 1f;
                if (cam == null)
                {
                    return false;
                }
                RenderTexture target = cam.targetTexture;
                if (target == null)
                {
                    return false;
                }
                if (scaler != null)
                {
                    if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
                    {
                        return TryComputeScaleWithScreenSize(
                            scaler.referenceResolution,
                            scaler.screenMatchMode,
                            scaler.matchWidthOrHeight,
                            target.width,
                            target.height,
                            out rtScale
                        );
                    }
                }
                Vector2 fallbackReference = GetOverlayOnlyReferenceSize(prefabRoot);
                return TryComputeScaleWithScreenSize(
                    fallbackReference,
                    CanvasScaler.ScreenMatchMode.Expand,
                    0f,
                    target.width,
                    target.height,
                    out rtScale
                );
            }

            /// <summary>
            /// Pure ScaleWithScreenSize replica parameterized by explicit RT size:
            /// MatchWidthOrHeight lerps in log2 space by matchWidthOrHeight,
            /// Expand takes the min ratio, Shrink the max. Returns false for
            /// degenerate reference/RT sizes.
            /// </summary>
            internal static bool TryComputeScaleWithScreenSize(
                Vector2 referenceResolution,
                CanvasScaler.ScreenMatchMode matchMode,
                float matchWidthOrHeight,
                int rtWidth,
                int rtHeight,
                out float scale
            )
            {
                scale = 1f;
                if (referenceResolution.x <= 0f || referenceResolution.y <= 0f)
                {
                    return false;
                }
                if (rtWidth <= 0 || rtHeight <= 0)
                {
                    return false;
                }
                switch (matchMode)
                {
                    case CanvasScaler.ScreenMatchMode.Expand:
                        scale = Mathf.Min(
                            rtWidth / referenceResolution.x,
                            rtHeight / referenceResolution.y
                        );
                        return true;
                    case CanvasScaler.ScreenMatchMode.Shrink:
                        scale = Mathf.Max(
                            rtWidth / referenceResolution.x,
                            rtHeight / referenceResolution.y
                        );
                        return true;
                    default:
                        float match = Mathf.Clamp01(matchWidthOrHeight);
                        float logWidth = Mathf.Log(rtWidth / referenceResolution.x, 2f);
                        float logHeight = Mathf.Log(rtHeight / referenceResolution.y, 2f);
                        scale = Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, match));
                        return true;
                }
            }

            /// <summary>
            /// Scoped prefab canvas switch consumed by BeginPrefab. Returns
            /// the touched count. Null session/root/camera is a no-op zero.
            /// </summary>
            internal static int SwitchPrefabCanvases(
                UiCaptureSession session,
                GameObject prefabRoot,
                Camera cam,
                float uiScale
            )
            {
                int switched = 0;
                if (session == null || prefabRoot == null || cam == null)
                {
                    return switched;
                }
                Canvas[] canvases = prefabRoot.GetComponentsInChildren<Canvas>(true);
                if (canvases == null)
                {
                    return switched;
                }
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || !canvas.isActiveAndEnabled)
                    {
                        continue;
                    }
                    bool isOverlay = canvas.renderMode == RenderMode.ScreenSpaceOverlay;
                    bool isCamera = canvas.renderMode == RenderMode.ScreenSpaceCamera;
                    if (!isOverlay && !isCamera)
                    {
                        continue;
                    }
                    bool scaleMutated = false;
                    float scaleFactor = canvas.scaleFactor;
                    CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                    bool scalerEnabled = false;
                    if (scaler != null)
                    {
                        scalerEnabled = scaler.enabled;
                    }
                    if (s_State.RecordUndo)
                    {
                        Undo.RecordObject(canvas, "UniThumb capture");
                    }
                    if (scaler != null && scaler.enabled)
                    {
                        if (s_State.RecordUndo)
                        {
                            Undo.RecordObject(scaler, "UniThumb capture");
                        }
                        scaler.enabled = false;
                        scaleMutated = true;
                    }
                    // RT-proportional prefab UI scale: the capture RT size over
                    // the scaler referenceResolution, so frame-fraction is
                    // identical at 16 vs 2048 (density only). Overlay takes
                    // the raw base with no k_PrefabRtScaleMin floor multiplied
                    // by the UiScale setting; Camera takes the floored base
                    // (UiScale stays Overlay-scoped, as before). A null or
                    // ConstantPixelSize scaler falls back to the overlay
                    // reference size (800x600 default) with the same raw base,
                    // so fixed-pixel prefabs cannot bypass the scale while the
                    // canvas rect tracks RT size. The scaler stays disabled
                    // for the pass and Dispose restores everything. Without an
                    // RT on the camera the legacy freeze applies: Overlay keeps
                    // the uiScale != 1f override only, Camera keeps the snapshot.
                    float prefabRtScale;
                    bool hasPrefabRtBase = TryComputePrefabRtScale(
                        scaler,
                        cam,
                        prefabRoot,
                        out prefabRtScale
                    );
                    if (isOverlay && hasPrefabRtBase)
                    {
                        canvas.scaleFactor = prefabRtScale * Mathf.Clamp(uiScale, 0.25f, 4f);
                        scaleMutated = true;
                    }
                    else if (isOverlay && uiScale != 1f)
                    {
                        canvas.scaleFactor = uiScale;
                        scaleMutated = true;
                    }
                    else if (isCamera && hasPrefabRtBase)
                    {
                        canvas.scaleFactor = Mathf.Max(prefabRtScale, k_PrefabRtScaleMin);
                        scaleMutated = true;
                    }
                    session._snapshots.Add(
                        new CanvasUiState
                        {
                            Canvas = canvas,
                            RenderMode = canvas.renderMode,
                            WorldCamera = canvas.worldCamera,
                            PlaneDistance = canvas.planeDistance,
                            SortingOrder = canvas.sortingOrder,
                            Layer = canvas.gameObject.layer,
                            ScaleMutated = scaleMutated,
                            ScaleFactor = scaleFactor,
                            Scaler = scaler,
                            ScalerEnabled = scalerEnabled,
                        }
                    );
                    if (isOverlay)
                    {
                        canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        canvas.worldCamera = cam;
                        canvas.planeDistance = Mathf.Max(cam.nearClipPlane + 0.1f, 0.1f);
                    }
                    else
                    {
                        canvas.worldCamera = cam;
                    }
                    cam.cullingMask |= 1 << canvas.gameObject.layer;
                    switched++;
                    if (ThrowInPrefabSwitchForTest)
                    {
                        throw new System.InvalidOperationException(
                            "UniThumb prefab switch test throw."
                        );
                    }
                }
                return switched;
            }

            private static int SwitchOverlayCanvases(
                UiCaptureSession session,
                Camera cam,
                bool forceUiLayer,
                float uiScale
            )
            {
                int switched = 0;
#if UNITY_6000_0_OR_NEWER
                Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>();
#else
                Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
#endif
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || !canvas.isActiveAndEnabled)
                    {
                        continue;
                    }
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    {
                        continue;
                    }
                    bool scaleMutated = false;
                    float scaleFactor = 1f;
                    CanvasScaler scaler = null;
                    bool scalerEnabled = false;
                    if (s_State.RecordUndo)
                    {
                        Undo.RecordObject(canvas, "UniThumb capture");
                    }
                    if (uiScale != 1f)
                    {
                        // Scale override: CanvasScaler is [ExecuteAlways] and
                        // re-applies scaleFactor on every preWillRenderCanvases
                        // while enabled, so disable it (snapshot first) or the
                        // override would not survive to the render.
                        scaleFactor = canvas.scaleFactor;
                        scaler = canvas.GetComponent<CanvasScaler>();
                        if (scaler != null)
                        {
                            scalerEnabled = scaler.enabled;
                            if (s_State.RecordUndo)
                            {
                                Undo.RecordObject(scaler, "UniThumb capture");
                            }
                            scaler.enabled = false;
                        }
                        canvas.scaleFactor = uiScale;
                        scaleMutated = true;
                    }
                    session._snapshots.Add(
                        new CanvasUiState
                        {
                            Canvas = canvas,
                            RenderMode = canvas.renderMode,
                            WorldCamera = canvas.worldCamera,
                            PlaneDistance = canvas.planeDistance,
                            SortingOrder = canvas.sortingOrder,
                            Layer = canvas.gameObject.layer,
                            ScaleMutated = scaleMutated,
                            ScaleFactor = scaleFactor,
                            Scaler = scaler,
                            ScalerEnabled = scalerEnabled,
                        }
                    );
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = Mathf.Max(cam.nearClipPlane + 0.1f, 0.1f);
                    if (forceUiLayer)
                    {
                        // ScreenSpaceCamera rendering is keyed to the Canvas
                        // GameObject's layer; the wide pass culling mask is
                        // layerMask & (1<<5), so overlay canvases must sit on
                        // layer 5 (UI) to render into the pass.
                        if (s_State.RecordUndo)
                        {
                            Undo.RecordObject(canvas.gameObject, "UniThumb capture");
                        }
                        canvas.gameObject.layer = 5;
                    }
                    switched++;
                }
                return switched;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                List<CanvasUiState> scaleReapply = null;
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    CanvasUiState snapshot = _snapshots[i];
                    if (snapshot.Canvas == null)
                    {
                        continue;
                    }
                    snapshot.Canvas.renderMode = snapshot.RenderMode;
                    snapshot.Canvas.worldCamera = snapshot.WorldCamera;
                    snapshot.Canvas.planeDistance = snapshot.PlaneDistance;
                    snapshot.Canvas.sortingOrder = snapshot.SortingOrder;
                    snapshot.Canvas.gameObject.layer = snapshot.Layer;
                    if (snapshot.ScaleMutated)
                    {
                        snapshot.Canvas.scaleFactor = snapshot.ScaleFactor;
                        if (snapshot.Scaler != null && snapshot.ScalerEnabled)
                        {
                            // Re-enable after the final ForceUpdateCanvases
                            // below; the scale snapshot is re-applied after
                            // the re-enable then (see below).
                            if (scaleReapply == null)
                            {
                                scaleReapply = new List<CanvasUiState>();
                            }
                            scaleReapply.Add(snapshot);
                        }
                        else if (snapshot.Scaler != null)
                        {
                            snapshot.Scaler.enabled = false;
                        }
                    }
                }
                _snapshots.Clear();
                Canvas.ForceUpdateCanvases();
                if (scaleReapply != null)
                {
                    for (int i = 0; i < scaleReapply.Count; i++)
                    {
                        CanvasUiState snapshot = scaleReapply[i];
                        if (snapshot.Canvas == null)
                        {
                            continue;
                        }
                        if (snapshot.Scaler != null)
                        {
                            snapshot.Scaler.enabled = true;
                        }
                        // Re-apply after re-enable: the scaler is
                        // [ExecuteAlways] and recomputes scaleFactor
                        // synchronously on enable, which would otherwise
                        // clobber the just-restored snapshot.
                        snapshot.Canvas.scaleFactor = snapshot.ScaleFactor;
                    }
                }
            }
        }

        #endregion
    }
}
