using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Verifies ReadBack returns sRGB-encoded bytes that round-trip the known
    /// picker color in BOTH color spaces: gamma projects must copy raw bytes
    /// (no linear→sRGB conversion - that was the double-brightening bug), and
    /// linear projects must convert only when the RT is not sRGB. The
    /// assertions are color-space agnostic: an sRGB RT always holds picker
    /// bytes after a blit, and a linear RT holds linear bytes that ReadBack
    /// converts back to the picker values.
    /// </summary>
    [TestFixture]
    public class UniThumbReadBackColorTests
    {
        private const byte k_Red = 128;
        private const byte k_Green = 64;
        private const byte k_Blue = 191;
        private const byte k_Alpha = 128;
        private const int k_Tolerance = 2;

        [Test]
        public void ReadBack_LdrTarget_PreservesPickerBytes()
        {
            VerifyRoundTrip(RenderTextureFormat.Default);
        }

        [Test]
        public void ReadBack_HdrTarget_PreservesPickerBytes()
        {
            VerifyRoundTrip(RenderTextureFormat.DefaultHDR);
        }

        [Test]
        public void ReadBack_LdrTarget_PreservesAlpha()
        {
            Texture2D source = CreatePickerTexture();
            var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.Default);
            rt.Create();
            try
            {
                Blit(source, rt);
                Texture2D result = UniThumbCapture.ReadBack(rt);
                try
                {
                    Color32[] pixels = result.GetPixels32();
                    Assert.AreEqual(k_Alpha, pixels[0].a, "Alpha must pass through untouched");
                }
                finally
                {
                    Object.DestroyImmediate(result);
                }
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void ReadBack_ReturnsSrgbFlaggedTexture_InLinearColorSpace()
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
            {
                // Gamma projects do not distinguish sRGB vs linear texture
                // formats; the byte assertions above are the regression there.
                Assert.Ignore("Texture color-space flag only materializes in linear color space.");
            }

            var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.Default);
            rt.Create();
            try
            {
                Texture2D result = UniThumbCapture.ReadBack(rt);
                try
                {
                    Assert.IsTrue(
                        IsSRGBFormat(result.graphicsFormat),
                        $"Expected sRGB-flagged texture but got {result.graphicsFormat}"
                    );
                }
                finally
                {
                    Object.DestroyImmediate(result);
                }
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static void VerifyRoundTrip(RenderTextureFormat format)
        {
            Texture2D source = CreatePickerTexture();
            var rt = new RenderTexture(4, 4, 0, format);
            rt.Create();
            try
            {
                Blit(source, rt);
                Texture2D result = UniThumbCapture.ReadBack(rt);
                try
                {
                    Color32[] pixels = result.GetPixels32();
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        AssertPixel(pixels[i], $"pixel {i} (RT {format})");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(result);
                }
            }
            finally
            {
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(source);
            }
        }

        private static Texture2D CreatePickerTexture()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(k_Red, k_Green, k_Blue, k_Alpha);
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private static void Blit(Texture2D source, RenderTexture target)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = target;
            Graphics.Blit(source, target);
            RenderTexture.active = previousActive;
        }

        private static void AssertPixel(Color32 pixel, string context)
        {
            Assert.LessOrEqual(
                Mathf.Abs(pixel.r - k_Red),
                k_Tolerance,
                $"Red mismatch at {context}: got {pixel.r}"
            );
            Assert.LessOrEqual(
                Mathf.Abs(pixel.g - k_Green),
                k_Tolerance,
                $"Green mismatch at {context}: got {pixel.g}"
            );
            Assert.LessOrEqual(
                Mathf.Abs(pixel.b - k_Blue),
                k_Tolerance,
                $"Blue mismatch at {context}: got {pixel.b}"
            );
        }

        private static bool IsSRGBFormat(UnityEngine.Experimental.Rendering.GraphicsFormat format)
        {
            return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(format);
        }
    }
}
