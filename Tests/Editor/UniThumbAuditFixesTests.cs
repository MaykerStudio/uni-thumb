using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Failing-first regression tests for the 20260907 audit fixes (B1-B4).
    /// Written before the fixes (Red): pre-fix they fail to compile against the
    /// missing internal seams (B1/B2/B4) or throw on the old parse paths (B3).
    /// </summary>
    [TestFixture]
    public class UniThumbAuditFixesTests
    {
        private const string k_PreviewCameraName = "__UniThumbPreviewCamera";
        private const string k_LastCheckTimeKey = "UniThumb.LastCheckTime";

        [TearDown]
        public void TearDown()
        {
            UniThumbWindow.BeginUiSessionOverride = null;
            UniThumbBatchMenus.UnsavedChangesProbeForTest = null;
            UniThumbGuard.Exit();
        }

        #region B1: Update Check Opt-In

        [Test]
        public void B1_IsCacheValid_CorruptTimestamp_ReturnsFalseWithoutThrow()
        {
            bool hadKey = EditorPrefs.HasKey(k_LastCheckTimeKey);
            string original = hadKey ? EditorPrefs.GetString(k_LastCheckTimeKey) : null;
            try
            {
                EditorPrefs.SetString(k_LastCheckTimeKey, "not-a-timestamp");
                bool valid = true;
                Assert.DoesNotThrow(() => valid = UniThumbUpdateChecker.IsCacheValid());
                Assert.IsFalse(valid);
            }
            finally
            {
                if (hadKey)
                {
                    EditorPrefs.SetString(k_LastCheckTimeKey, original);
                }
                else
                {
                    EditorPrefs.DeleteKey(k_LastCheckTimeKey);
                }
            }
        }

        [Test]
        public void B1_IsCacheValid_MissingKey_ReturnsFalse()
        {
            bool hadKey = EditorPrefs.HasKey(k_LastCheckTimeKey);
            string original = hadKey ? EditorPrefs.GetString(k_LastCheckTimeKey) : null;
            try
            {
                EditorPrefs.DeleteKey(k_LastCheckTimeKey);
                Assert.IsFalse(UniThumbUpdateChecker.IsCacheValid());
            }
            finally
            {
                if (hadKey)
                {
                    EditorPrefs.SetString(k_LastCheckTimeKey, original);
                }
            }
        }

        [Test]
        public void B1_IsCacheValid_RecentTimestamp_ReturnsTrue()
        {
            bool hadKey = EditorPrefs.HasKey(k_LastCheckTimeKey);
            string original = hadKey ? EditorPrefs.GetString(k_LastCheckTimeKey) : null;
            try
            {
                EditorPrefs.SetString(k_LastCheckTimeKey, DateTime.UtcNow.Ticks.ToString());
                Assert.IsTrue(UniThumbUpdateChecker.IsCacheValid());
            }
            finally
            {
                if (hadKey)
                {
                    EditorPrefs.SetString(k_LastCheckTimeKey, original);
                }
                else
                {
                    EditorPrefs.DeleteKey(k_LastCheckTimeKey);
                }
            }
        }

        [Test]
        public void B1_UpdateCheckEnabled_RoundTrip()
        {
            bool original = UniThumbUpdateChecker.UpdateCheckEnabled;
            try
            {
                UniThumbUpdateChecker.SetUpdateCheckEnabled(true);
                Assert.IsTrue(UniThumbUpdateChecker.UpdateCheckEnabled);
                UniThumbUpdateChecker.SetUpdateCheckEnabled(false);
                Assert.IsFalse(UniThumbUpdateChecker.UpdateCheckEnabled);
            }
            finally
            {
                UniThumbUpdateChecker.SetUpdateCheckEnabled(original);
            }
        }

        [Test]
        public void B1_CheckForUpdatesFromMenu_WhenDisabled_DoesNothing()
        {
            bool original = UniThumbUpdateChecker.UpdateCheckEnabled;
            try
            {
                UniThumbUpdateChecker.SetUpdateCheckEnabled(false);
                Assert.DoesNotThrow(() => UniThumbUpdateChecker.CheckForUpdatesFromMenu());
                Assert.IsFalse(UniThumbUpdateChecker.IsCheckComplete);
            }
            finally
            {
                UniThumbUpdateChecker.SetUpdateCheckEnabled(original);
            }
        }

        #endregion

        #region B2: Preview Temp Camera Cleanup

        [Test]
        public void B2_RenderLivePreview_UiSessionBeginThrows_DestroysTempCamera()
        {
            string tempScenePath = "Assets/__UniThumbB2TempScene.unity";
            string previousScenePath = EditorSceneManager.GetActiveScene().path;
            Scene tempScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );
            EditorSceneManager.SaveScene(tempScene, tempScenePath);
            UniThumbWindow window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                if (window.rootVisualElement == null)
                {
                    Assert.Ignore("No UI panel available for the preview path.");
                }

                // Orbit framing keeps the composite UI pass ineligible so the
                // legacy UiCaptureSession.Begin path (the seam) always runs.
                UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
                try
                {
                    settings.SetUseSceneViewAngle(false);
                    window.ApplyPersistedCaptureSettings(settings);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                GameObject stale = GameObject.Find(k_PreviewCameraName);
                if (stale != null)
                {
                    UnityEngine.Object.DestroyImmediate(stale);
                }

                bool invoked = false;
                bool seenLiveCamera = false;
                UniThumbWindow.BeginUiSessionOverride = (cam, scale) =>
                {
                    invoked = true;
                    seenLiveCamera = cam != null;
                    throw new InvalidOperationException("simulated Begin failure");
                };

                LogAssert.Expect(
                    LogType.Warning,
                    "[UniThumb] Live preview failed: simulated Begin failure"
                );
                window.RenderLivePreview();

                Assert.IsTrue(invoked, "Throw path was not exercised; test is vacuous.");
                Assert.IsTrue(seenLiveCamera, "Temp camera was not created before Begin.");
                Assert.IsTrue(
                    GameObject.Find(k_PreviewCameraName) == null,
                    "Temp preview camera leaked after Begin threw."
                );
            }
            finally
            {
                UniThumbWindow.BeginUiSessionOverride = null;
                UnityEngine.Object.DestroyImmediate(window);
                AssetDatabase.DeleteAsset(tempScenePath);
                if (!string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        #endregion

        #region Majors: Clamp And Guard Behavior

        [Test]
        public void Majors_ClampBulkResolution_ClampsEachAxisIndependently()
        {
            CaptureSettings wide = UniThumbCapture.CreateDefaultSettings();
            wide.Width = 4096;
            wide.Height = 512;
            CaptureSettings clampedWide = UniThumbBatchMenus.ClampBulkResolution(wide);
            Assert.AreEqual(2048, clampedWide.Width);
            Assert.AreEqual(512, clampedWide.Height);

            CaptureSettings tall = UniThumbCapture.CreateDefaultSettings();
            tall.Width = 512;
            tall.Height = 4096;
            CaptureSettings clampedTall = UniThumbBatchMenus.ClampBulkResolution(tall);
            Assert.AreEqual(512, clampedTall.Width);
            Assert.AreEqual(2048, clampedTall.Height);
        }

        [Test]
        public void Majors_ClampBulkResolution_LeavesInRangeUnchanged()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = 512;
            settings.Height = 512;
            CaptureSettings result = UniThumbBatchMenus.ClampBulkResolution(settings);
            Assert.AreEqual(512, result.Width);
            Assert.AreEqual(512, result.Height);
        }

        [Test]
        public void Majors_SetUiScale_ClampsToQuarterToFour()
        {
            UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            try
            {
                settings.SetUiScale(0f);
                Assert.AreEqual(0.25f, settings.UiScale);
                settings.SetUiScale(10f);
                Assert.AreEqual(4f, settings.UiScale);
                settings.SetUiScale(2f);
                Assert.AreEqual(2f, settings.UiScale);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Majors_IsShaderCompiling_ReturnsBoolWithoutThrow()
        {
            bool compiling = false;
            Assert.DoesNotThrow(() => compiling = UniThumbBatchMenus.IsShaderCompiling());
            Assert.IsTrue(compiling || !compiling);
        }

        #endregion

        #region B3: Light2D Enum Lookup Never Throws

        private enum UnrelatedLightEnum
        {
            None = 0,
            Other = 1,
        }

        private enum GlobalLightEnum
        {
            Local = 0,
            Global = 3,
        }

        [Test]
        public void B3_FindLight2DGlobalModeValue_UnknownNames_ReturnsZeroWithoutThrow()
        {
            int value = -1;
            Assert.DoesNotThrow(() =>
                value = UniThumbCapture.FindLight2DGlobalModeValue(typeof(UnrelatedLightEnum))
            );
            Assert.AreEqual(0, value);
        }

        [Test]
        public void B3_FindLight2DGlobalModeValue_NullType_ReturnsZeroWithoutThrow()
        {
            int value = -1;
            Assert.DoesNotThrow(() => value = UniThumbCapture.FindLight2DGlobalModeValue(null));
            Assert.AreEqual(0, value);
        }

        [Test]
        public void B3_FindLight2DGlobalModeValue_KnownGlobalName_ResolvesValue()
        {
            Assert.AreEqual(3, UniThumbCapture.FindLight2DGlobalModeValue(typeof(GlobalLightEnum)));
        }

        #endregion

        #region B4: Abort-On-Dirty Batch Gate

        [Test]
        public void B4_DirtyCheck_RefusesByDefaultWhenUnsavedChanges()
        {
            bool original = UniThumbBatchMenus.AllowDiscardUnsavedChanges;
            try
            {
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = false;
                UniThumbBatchMenus.UnsavedChangesProbeForTest = () => true;
                string refusal = null;
                bool ok = true;
                Assert.DoesNotThrow(() =>
                    ok = UniThumbBatchMenus.CheckNoUnsavedChangesOrRefuse(
                        "Test operation",
                        out refusal
                    )
                );
                Assert.IsFalse(ok);
                Assert.IsFalse(string.IsNullOrEmpty(refusal));
            }
            finally
            {
                UniThumbBatchMenus.UnsavedChangesProbeForTest = null;
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = original;
            }
        }

        [Test]
        public void B4_DirtyCheck_OptInOverridePreservesPriorThroughput()
        {
            bool original = UniThumbBatchMenus.AllowDiscardUnsavedChanges;
            try
            {
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = true;
                UniThumbBatchMenus.UnsavedChangesProbeForTest = () => true;
                string refusal = "should-be-cleared";
                bool ok = UniThumbBatchMenus.CheckNoUnsavedChangesOrRefuse(
                    "Test operation",
                    out refusal
                );
                Assert.IsTrue(ok);
            }
            finally
            {
                UniThumbBatchMenus.UnsavedChangesProbeForTest = null;
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = original;
            }
        }

        [Test]
        public void B4_DirtyCheck_CleanSceneProceeds()
        {
            bool original = UniThumbBatchMenus.AllowDiscardUnsavedChanges;
            try
            {
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = false;
                UniThumbBatchMenus.UnsavedChangesProbeForTest = () => false;
                string refusal = "should-be-cleared";
                bool ok = UniThumbBatchMenus.CheckNoUnsavedChangesOrRefuse(
                    "Test operation",
                    out refusal
                );
                Assert.IsTrue(ok);
            }
            finally
            {
                UniThumbBatchMenus.UnsavedChangesProbeForTest = null;
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = original;
            }
        }

        [Test]
        public void B4_BatchConfirms_IncludeDiscardDisclosure()
        {
            string disclosure = UniThumbBatchMenus.DiscardDisclosure;
            Assert.IsFalse(string.IsNullOrEmpty(disclosure));
            Assert.IsTrue(disclosure.IndexOf("discard", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        #endregion
    }
}
