using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Renderer-index validation matrix for the URP missing-renderer
    /// fallback warning ("Renderer at index 1 is missing, falling back to
    /// Default Renderer"). On 2D-only single-renderer pipelines a stale
    /// source index copied onto temp cameras re-arms that warning on every
    /// cam.Render; validated copies must keep the default silently.
    /// All assertions route through UniThumbUrp so no Universal references
    /// leak into the test assembly.
    /// </summary>
    [TestFixture]
    public class UniThumbUrpRendererIndexTests
    {
        #region Setup

        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
            UniThumbUrp.ResetForTest();
            UniThumbCapture.SetRenderer2DOnlyForTest(null);
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbGuard.Exit();
            UniThumbUrp.ResetForTest();
            UniThumbCapture.SetRenderer2DOnlyForTest(null);
        }

        #endregion

        #region Public Methods

        [Test]
        public void GetRendererCount_MatchesRendererDataList()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            int count = UniThumbUrp.GetRendererCount();
            Assert.GreaterOrEqual(count, 1, "URP project must report >= 1 renderer.");
            Assert.AreEqual(
                ReadRendererDataListSize(),
                count,
                "Wrap-aware sweep must equal m_RendererDataList size."
            );
        }

        [Test]
        public void CopyFromSource_KeepsDefaultEmitsNoFallbackWarning()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            GameObject srcGo = new GameObject("UniThumbRendererIndexSrc");
            GameObject dstGo = new GameObject("UniThumbRendererIndexDst");
            List<string> fallbackWarnings = new List<string>();
            Application.LogCallback handler = (message, trace, type) =>
            {
                if (type == LogType.Warning && IsFallbackWarning(message))
                {
                    fallbackWarnings.Add(message);
                }
            };
            try
            {
                Camera src = srcGo.AddComponent<Camera>();
                Camera dst = dstGo.AddComponent<Camera>();
                Assert.IsTrue(UniThumbUrp.EnsureCameraData(src, false));
                Assert.IsTrue(UniThumbUrp.EnsureCameraData(dst, false));
                Application.logMessageReceived += handler;
                try
                {
                    Assert.IsTrue(UniThumbUrp.EnsureCameraDataFromSource(dst, src));
                    RenderToTempTarget(dst);
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                }
                Assert.IsEmpty(
                    fallbackWarnings,
                    "No fallback warnings: " + string.Join(" | ", fallbackWarnings)
                );
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(srcGo);
                Object.DestroyImmediate(dstGo);
            }
        }

        [Test]
        public void OutOfRange_SetRendererNeverCalled_StaysDefaultSilent()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            int count = UniThumbUrp.GetRendererCount();
            if (count < 1)
            {
                Assert.Ignore("No URP renderers in this project.");
            }
            GameObject go = new GameObject("UniThumbRendererIndexRange");
            List<string> fallbackWarnings = new List<string>();
            Application.LogCallback handler = (message, trace, type) =>
            {
                if (type == LogType.Warning && IsFallbackWarning(message))
                {
                    fallbackWarnings.Add(message);
                }
            };
            try
            {
                Camera cam = go.AddComponent<Camera>();
                Assert.IsTrue(UniThumbUrp.EnsureCameraData(cam, false));
                Assert.AreEqual(-1, ReadRendererIndex(cam), "Precondition: default index.");
                Application.logMessageReceived += handler;
                try
                {
                    Assert.IsFalse(UniThumbUrp.TrySetRendererByIndex(cam, -1));
                    Assert.IsFalse(UniThumbUrp.TrySetRendererByIndex(cam, count));
                    Assert.IsFalse(UniThumbUrp.TrySetRendererByIndex(cam, 32));
                    RenderToTempTarget(cam);
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                }
                Assert.AreEqual(
                    -1,
                    ReadRendererIndex(cam),
                    "Out-of-range indexes must leave the default renderer."
                );
                Assert.IsEmpty(
                    fallbackWarnings,
                    "No fallback warnings: " + string.Join(" | ", fallbackWarnings)
                );
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MixedPipeline_ValidIndex_PreservedWithoutWarning()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            int count = UniThumbUrp.GetRendererCount();
            if (count < 2)
            {
                Assert.Ignore("Needs a mixed multi-renderer pipeline; this one has " + count + ".");
            }
            GameObject go = new GameObject("UniThumbRendererIndexMixed");
            List<string> fallbackWarnings = new List<string>();
            Application.LogCallback handler = (message, trace, type) =>
            {
                if (type == LogType.Warning && IsFallbackWarning(message))
                {
                    fallbackWarnings.Add(message);
                }
            };
            try
            {
                Camera cam = go.AddComponent<Camera>();
                Assert.IsTrue(UniThumbUrp.EnsureCameraData(cam, false));
                Application.logMessageReceived += handler;
                try
                {
                    Assert.IsTrue(UniThumbUrp.TrySetRendererByIndex(cam, 1));
                    RenderToTempTarget(cam);
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                }
                Assert.AreEqual(1, ReadRendererIndex(cam), "Valid index must be preserved.");
                Assert.IsEmpty(
                    fallbackWarnings,
                    "No fallback warnings: " + string.Join(" | ", fallbackWarnings)
                );
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SceneViewCopy_On2DOnly_EmitsNoFallbackWarning()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            if (!UniThumbUrp.PipelineHasOnly2DRenderers())
            {
                Assert.Ignore("Needs a 2D-only pipeline for this case.");
            }
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                Assert.Ignore("No active SceneView camera in this session.");
            }
            GameObject go = new GameObject("UniThumbRendererIndexSceneView");
            List<string> fallbackWarnings = new List<string>();
            Application.LogCallback handler = (message, trace, type) =>
            {
                if (type == LogType.Warning && IsFallbackWarning(message))
                {
                    fallbackWarnings.Add(message);
                }
            };
            try
            {
                Camera cam = go.AddComponent<Camera>();
                Application.logMessageReceived += handler;
                try
                {
                    Assert.IsTrue(UniThumbUrp.EnsureCameraDataFromSource(cam, sv.camera));
                    RenderToTempTarget(cam);
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                }
                Assert.IsEmpty(
                    fallbackWarnings,
                    "No fallback warnings: " + string.Join(" | ", fallbackWarnings)
                );
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        #endregion

        #region Private Methods

        private static bool IsFallbackWarning(string message)
        {
            return message != null
                && message.IndexOf("falling back", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RenderToTempTarget(Camera cam)
        {
            RenderTexture target = RenderTexture.GetTemporary(64, 64, 16);
            try
            {
                RenderTexture previous = cam.targetTexture;
                cam.targetTexture = target;
                try
                {
                    cam.Render();
                }
                finally
                {
                    cam.targetTexture = previous;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static int ReadRendererIndex(Camera cam)
        {
            Component uacd = null;
            Component[] components = cam.GetComponents(typeof(Component));
            for (int i = 0; i < components.Length; i++)
            {
                Component candidate = components[i];
                if (
                    candidate != null
                    && candidate.GetType().Name == "UniversalAdditionalCameraData"
                )
                {
                    uacd = candidate;
                    break;
                }
            }
            if (uacd == null)
            {
                return -1;
            }
            SerializedObject so = new SerializedObject(uacd);
            SerializedProperty indexProp = so.FindProperty("m_RendererIndex");
            if (indexProp == null)
            {
                return -1;
            }
            return indexProp.intValue;
        }

        private static int ReadRendererDataListSize()
        {
            Object pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null)
            {
                return 0;
            }
            SerializedObject so = new SerializedObject(pipeline);
            SerializedProperty listProp = so.FindProperty("m_RendererDataList");
            if (listProp == null)
            {
                return -1;
            }
            return listProp.arraySize;
        }

        #endregion
    }
}
