using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Behavioral probe pin: Unity coerces a live ScreenSpaceCamera canvas
    /// with a null worldCamera back to ScreenSpaceOverlay (native validation,
    /// verified on Unity 6000.4). The prefab UI suites therefore author Camera
    /// canvases with a live (disabled) camera instead of null; if a future
    /// Unity version drops the coercion, this pin fails loudly so the
    /// authoring approach can be re-examined. No rendering involved.
    /// </summary>
    [TestFixture]
    public class UniThumbProbeTests
    {
        [Test]
        public void Probe_NullWorldCamera_CoercesCameraCanvasToOverlay()
        {
            GameObject cameraGo = new GameObject("ProbeAuthoredCamera");
            Camera authoredCam = cameraGo.AddComponent<Camera>();
            GameObject canvasGo = new GameObject("ProbeCanvas", typeof(RectTransform));
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            try
            {
                canvas.worldCamera = authoredCam;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                Assert.AreEqual(
                    RenderMode.ScreenSpaceCamera,
                    canvas.renderMode,
                    "Camera mode must stick while a live camera is assigned."
                );
                canvas.worldCamera = null;
                Assert.AreEqual(
                    RenderMode.ScreenSpaceOverlay,
                    canvas.renderMode,
                    "Nulling the camera must coerce back to Overlay (pinned engine behavior)."
                );
                Assert.IsNull(canvas.worldCamera);
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(cameraGo);
            }
        }
    }
}
