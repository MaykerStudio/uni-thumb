using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbUrpShimTests
    {
        [TearDown]
        public void ResetShimProbe()
        {
            UniThumbUrp.ResetForTest();
            UniThumbCapture.SetRenderer2DOnlyForTest(null);
        }

        [Test]
        public void Probe_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _ = UniThumbUrp.IsAvailable);
        }

        [Test]
        public void FailOpen_NullInputs_ReturnDefaultsWithoutThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                Assert.IsFalse(UniThumbUrp.EnsureCameraData(null, false));
                Assert.IsFalse(UniThumbUrp.EnsureCameraDataFromSource(null, null));
                Assert.IsFalse(UniThumbUrp.TrySwitchTo3DRenderer(null));
                Assert.IsFalse(UniThumbUrp.IsLight2D(null));
                Assert.IsFalse(UniThumbUrp.IsLight2DType(null));
                Assert.IsFalse(UniThumbUrp.IsGlobalLight2D(null, 0));
                Assert.AreEqual(-1, UniThumbUrp.GetLightTypeValue(null));
                Assert.IsNull(UniThumbUrp.GetLight2DComponents(null));
                Assert.IsFalse(UniThumbUrp.SetupExampleLight2D(null, Color.white, 1f, 5f));
            });
        }

        [Test]
        public void PipelineProbe_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                _ = UniThumbUrp.PipelineHasOnly2DRenderers();
                Assert.GreaterOrEqual(UniThumbUrp.DetectRendererDimension(), -1);
                Assert.IsNotNull(UniThumbUrp.ActivePipelineName());
                Assert.IsNotNull(UniThumbUrp.ActiveRendererName());
            });
        }

        [Test]
        public void ShimAvailable_ResolvesLight2DTypes()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            Assert.IsNotNull(UniThumbUrp.Light2DType);
            Assert.IsNotNull(UniThumbUrp.Light2DLightType);
            Assert.IsTrue(UniThumbUrp.Light2DLightType.IsEnum);
            Assert.IsNotNull(UniThumbUrp.Light2DFullName);
        }

        [Test]
        public void ShimAvailable_GlobalModeValue_ResolvesWithoutThrow()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            int value = UniThumbCapture.FindLight2DGlobalModeValue(UniThumbUrp.Light2DLightType);
            Assert.GreaterOrEqual(value, 0);
        }

        [Test]
        public void ShimAvailable_TempGlobalLight_CreateAndDestroy()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            int globalValue = UniThumbCapture.FindLight2DGlobalModeValue(
                UniThumbUrp.Light2DLightType
            );
            GameObject temp = null;
            try
            {
                temp = UniThumbUrp.CreateTempGlobalLight(1f, null, globalValue);
                Assert.IsNotNull(temp);
                Component[] found = UniThumbUrp.GetLight2DComponents(temp);
                Assert.IsNotNull(found);
                Assert.AreEqual(1, found.Length);
                Assert.IsTrue(UniThumbUrp.IsLight2D(found[0]));
                Assert.IsTrue(UniThumbUrp.IsGlobalLight2D(found[0], globalValue));
            }
            finally
            {
                if (temp != null)
                {
                    Object.DestroyImmediate(temp);
                }
            }
        }

        [Test]
        public void RendererSwitchLog_SecondSwitchStaysSilent()
        {
            if (!UniThumbUrp.IsAvailable)
            {
                Assert.Ignore("URP shim bridge absent; URP-only probe.");
            }
            GameObject go = new GameObject("UniThumbRendererSwitchProbe");
            Camera cam = go.AddComponent<Camera>();
            int switchLogs = 0;
            Application.LogCallback handler = (condition, stackTrace, type) =>
            {
                if (
                    condition != null
                    && condition.StartsWith(
                        "[UniThumb] Light3D: switched camera renderer",
                        System.StringComparison.Ordinal
                    )
                )
                {
                    switchLogs++;
                }
            };
            try
            {
                Application.logMessageReceived += handler;
                Assert.IsTrue(
                    UniThumbUrp.EnsureCameraData(cam, false),
                    "Probe camera needs URP camera data."
                );
                UniThumbUrp.ResetRendererSwitchLogForTest();
                bool first = UniThumbUrp.TrySwitchTo3DRenderer(cam);
                if (!first)
                {
                    Assert.Ignore("No switchable 3D URP renderer in this project.");
                }
                bool second = UniThumbUrp.TrySwitchTo3DRenderer(cam);
                Assert.IsTrue(
                    second,
                    "Renderer switch behavior must stay identical (both switches succeed)."
                );
                Assert.LessOrEqual(
                    switchLogs,
                    1,
                    "Renderer-switch info log must fire at most once per session."
                );
            }
            finally
            {
                Application.logMessageReceived -= handler;
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
                UniThumbUrp.ResetRendererSwitchLogForTest();
            }
        }
    }
}
