using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbFormatRuleTests
    {
        [TearDown]
        public void TearDown()
        {
            UniThumbCapture.SetPostProcessingAvailableForTest(null);
        }

        [Test]
        public void CreateCaptureTarget_WantPostProcessingFalse_ReturnsLDRFormat()
        {
            // When the user disables post-processing, the target is LDR
            // regardless of scene Volumes or project VolumeProfiles.
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.WantPostProcessing = false;
            settings.BackgroundMode = BackgroundMode.SolidColor;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(64, 64, settings);
            try
            {
                Assert.IsTrue(IsLDRFormat(rt.format), $"Expected LDR format but got {rt.format}");
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CreateCaptureTarget_WantPostProcessingTrue_PpUnavailable_ReturnsLDRFormat()
        {
            // Post-processing requested but unavailable (override forces no
            // scene Volume and no project VolumeProfile) → LDR RT.
            UniThumbCapture.SetPostProcessingAvailableForTest(false);
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.WantPostProcessing = true;
            settings.BackgroundMode = BackgroundMode.SolidColor;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(64, 64, settings);
            try
            {
                Assert.IsTrue(IsLDRFormat(rt.format), $"Expected LDR format but got {rt.format}");
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CreateCaptureTarget_WantPostProcessingTrue_PpAvailable_ReturnsHDRFormat()
        {
            // Post-processing requested and available (override forces a
            // Volume/VolumeProfile present) → HDR RT (float, linear).
            UniThumbCapture.SetPostProcessingAvailableForTest(true);
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.WantPostProcessing = true;
            settings.BackgroundMode = BackgroundMode.SolidColor;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(64, 64, settings);
            try
            {
                Assert.IsTrue(IsHDRFormat(rt.format), $"Expected HDR format but got {rt.format}");
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CreateCaptureTarget_WantPostProcessingFalse_Skybox_ReturnsLDRFormat()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.WantPostProcessing = false;
            settings.BackgroundMode = BackgroundMode.Skybox;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(64, 64, settings);
            try
            {
                Assert.IsTrue(IsLDRFormat(rt.format), $"Expected LDR format but got {rt.format}");
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CreateCaptureTarget_TransparentMode_ReturnsARGB32()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.WantPostProcessing = true;
            settings.BackgroundMode = BackgroundMode.Transparent;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(64, 64, settings);
            try
            {
                Assert.AreEqual(RenderTextureFormat.ARGB32, rt.format);
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void CreateCaptureTarget_ReturnsCreatedRT()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.BackgroundMode = BackgroundMode.SolidColor;

            RenderTexture rt = UniThumbCapture.CreateCaptureTarget(32, 32, settings);
            try
            {
                Assert.IsTrue(rt.IsCreated());
                Assert.AreEqual(32, rt.width);
                Assert.AreEqual(32, rt.height);
                Assert.Greater(rt.depth, 0, "RT should have a depth buffer");
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Check if the format is an HDR-capable format (floating point).
        /// DefaultHDR resolves platform-specifically (ARGBHalf, ARGBFloat, etc.).
        /// </summary>
        private static bool IsHDRFormat(RenderTextureFormat format)
        {
            return format == RenderTextureFormat.DefaultHDR
                || format == RenderTextureFormat.ARGBHalf
                || format == RenderTextureFormat.ARGBFloat
                || format == RenderTextureFormat.RGFloat
                || format == RenderTextureFormat.RGHalf
                || format == RenderTextureFormat.RFloat
                || format == RenderTextureFormat.RHalf;
        }

        /// <summary>
        /// Check if the format is an LDR format (8-bit integer per channel).
        /// Default resolves platform-specifically (ARGB32, RGB565, etc.).
        /// </summary>
        private static bool IsLDRFormat(RenderTextureFormat format)
        {
            return format == RenderTextureFormat.Default
                || format == RenderTextureFormat.ARGB32
                || format == RenderTextureFormat.RGB565
                || format == RenderTextureFormat.ARGB4444
                || format == RenderTextureFormat.ARGB1555
                || format == RenderTextureFormat.ARGB2101010;
        }
    }
}
