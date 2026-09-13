using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbRenderer2DOnlyTests
    {
        #region Fields

        private enum FakeWithGlobalLate
        {
            FreeForm = 0,
            Point = 1,
            Global = 2,
        }

        private enum FakeWithGlobalValue
        {
            Point = 0,
            Sprite = 1,
            Global = 5,
        }

        private enum FakeWithoutGlobal
        {
            Point = 0,
            Sprite = 1,
        }

        #endregion

        #region Setup

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbCapture.SetRenderer2DOnlyForTest(null);
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbGuard.Exit();
            UniThumbCapture.SetRenderer2DOnlyForTest(null);
        }

        #endregion

        #region Public Methods

        [Test]
        public void Resolve_2DOnly_Perspective_ReturnsTempLight2D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight2D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    false,
                    true,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Resolve_2DOnly_Ortho3D_ReturnsTempLight2D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight2D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    true,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Resolve_2DOnly_OrthoSprite_ReturnsTempLight2D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight2D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    false,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Resolve_2DOnly_OwnKey_ReturnsNone()
        {
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    false,
                    true,
                    true,
                    true
                )
            );
        }

        [Test]
        public void Resolve_2DOnly_ExplicitModes_ReturnNone()
        {
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.Light3D,
                    true,
                    false,
                    false,
                    false,
                    true,
                    false,
                    true
                )
            );
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.Light2D,
                    true,
                    true,
                    false,
                    false,
                    false,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Resolve_2DOnly_ScenePath_ReturnsNone()
        {
            Assert.AreEqual(
                PrefabFallbackLight.None,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    false,
                    false,
                    false,
                    false,
                    true,
                    false,
                    true
                )
            );
        }

        [Test]
        public void Resolve_3D_PerspectiveAndOrtho3D_ReturnTempLight3D()
        {
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    false,
                    false,
                    false,
                    true,
                    false,
                    false
                )
            );
            Assert.AreEqual(
                PrefabFallbackLight.TempLight3D,
                UniThumbCapture.ResolvePrefabFallbackLight(
                    LightingMode.None,
                    true,
                    true,
                    false,
                    false,
                    true,
                    false,
                    false
                )
            );
        }

        [Test]
        public void ShouldApplyLight3D_Gates2DOnly()
        {
            Assert.IsFalse(UniThumbCapture.ShouldApplyLight3D(LightingMode.Light3D, true));
            Assert.IsTrue(UniThumbCapture.ShouldApplyLight3D(LightingMode.Light3D, false));
            Assert.IsFalse(UniThumbCapture.ShouldApplyLight3D(LightingMode.None, false));
            Assert.IsFalse(UniThumbCapture.ShouldApplyLight3D(LightingMode.Light2D, false));
            Assert.IsFalse(UniThumbCapture.ShouldApplyLight3D(LightingMode.None, true));
        }

        [Test]
        public void GlobalMode_OnlyGlobalAccepted_NotFirstHit()
        {
            Assert.AreEqual(
                2,
                UniThumbCapture.FindLight2DGlobalModeValue(typeof(FakeWithGlobalLate))
            );
            Assert.AreEqual(
                5,
                UniThumbCapture.FindLight2DGlobalModeValue(typeof(FakeWithGlobalValue))
            );
        }

        [Test]
        public void GlobalMode_MissingGlobal_FallsBackToFirstWithWarning()
        {
            LogAssert.Expect(
                LogType.Warning,
                "[UniThumb] Light2D global mode not found; defaulting to 0."
            );
            Assert.AreEqual(
                0,
                UniThumbCapture.FindLight2DGlobalModeValue(typeof(FakeWithoutGlobal))
            );
        }

        [Test]
        public void Ensure_2DOnly_TempLight2DPath_Silent()
        {
            GameObject tempLight2D = null;
            GameObject tempLight3D = null;
            List<UniThumbCapture.Light2DSnapshot> disabledGlobals = null;
            UniThumbCapture.SetRenderer2DOnlyForTest(true);
            List<string> warnings = new List<string>();
            void CaptureWarning(string message, string trace, LogType type)
            {
                if (type == LogType.Warning && message != null && message.StartsWith("[UniThumb]"))
                {
                    warnings.Add(message);
                }
            }
            try
            {
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                Application.logMessageReceived += CaptureWarning;
                try
                {
                    UniThumbCapture.EnsurePrefabFallbackLights(
                        PrefabFallbackLight.TempLight2D,
                        null,
                        settings,
                        null,
                        out tempLight2D,
                        out tempLight3D,
                        out disabledGlobals
                    );
                }
                finally
                {
                    Application.logMessageReceived -= CaptureWarning;
                }
                Assert.IsNull(tempLight3D, "TempLight2D path must not create 3D lights.");
                Assert.IsEmpty(warnings, "No warnings: " + string.Join(" | ", warnings));
                if (tempLight2D == null)
                {
                    Assert.Ignore("Light2D type not available in this project.");
                }
                Assert.IsNotNull(tempLight2D);
            }
            finally
            {
                UniThumbCapture.SetRenderer2DOnlyForTest(null);
                Object.DestroyImmediate(tempLight2D);
                Object.DestroyImmediate(tempLight3D);
                UniThumbCapture.RestoreGlobalLight2Ds(disabledGlobals);
            }
        }

        [Test]
        public void Ensure_2DOnly_ResolveThenMaterialize_No3DNoWarning()
        {
            PrefabFallbackLight resolved = UniThumbCapture.ResolvePrefabFallbackLight(
                LightingMode.None,
                true,
                false,
                false,
                false,
                true,
                false,
                true
            );
            Assert.AreEqual(PrefabFallbackLight.TempLight2D, resolved);
            GameObject camGo = new GameObject("UniThumbTest2DMatrixCam");
            GameObject tempLight2D = null;
            GameObject tempLight3D = null;
            List<UniThumbCapture.Light2DSnapshot> disabledGlobals = null;
            UniThumbCapture.SetRenderer2DOnlyForTest(true);
            List<string> warnings = new List<string>();
            void CaptureWarning(string message, string trace, LogType type)
            {
                if (type == LogType.Warning && message != null && message.StartsWith("[UniThumb]"))
                {
                    warnings.Add(message);
                }
            }
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
                Application.logMessageReceived += CaptureWarning;
                try
                {
                    UniThumbCapture.EnsurePrefabFallbackLights(
                        resolved,
                        cam,
                        settings,
                        null,
                        out tempLight2D,
                        out tempLight3D,
                        out disabledGlobals
                    );
                }
                finally
                {
                    Application.logMessageReceived -= CaptureWarning;
                }
                Assert.IsNull(tempLight3D, "No TempLight3D on a 2D-only pipeline.");
                Assert.IsEmpty(warnings, "No warnings: " + string.Join(" | ", warnings));
                if (tempLight2D == null)
                {
                    Assert.Ignore("Light2D type not available in this project.");
                }
                Assert.IsNotNull(tempLight2D);
            }
            finally
            {
                UniThumbCapture.SetRenderer2DOnlyForTest(null);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(tempLight2D);
                Object.DestroyImmediate(tempLight3D);
                UniThumbCapture.RestoreGlobalLight2Ds(disabledGlobals);
            }
        }

        [Test]
        public void Guard_NotHeldBy2DOnlyPaths()
        {
            Assert.IsFalse(UniThumbGuard.IsGenerating, "Guard must be free outside captures.");
            Assert.IsTrue(UniThumbGuard.TryEnter());
            try
            {
                Assert.IsTrue(UniThumbGuard.IsGenerating);
            }
            finally
            {
                UniThumbGuard.Exit();
            }
            Assert.IsFalse(UniThumbGuard.IsGenerating);
        }

        #endregion
    }
}
