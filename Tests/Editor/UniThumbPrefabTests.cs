using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabTests
    {
        private const string k_TestGuid = "abcdef1234567890abcdef1234567890";

        [TearDown]
        public void TearDown()
        {
            UniThumbStorage.DeleteByGuid(k_TestGuid);
            UniThumbStorage.ClearCache();
        }

        [Test]
        public void SavePrefabThumbnail_RefusesEmptyGuid()
        {
            byte[] png = CreateTestPng();
            Assert.IsFalse(UniThumbStorage.SavePrefabThumbnail(null, png));
            Assert.IsFalse(UniThumbStorage.SavePrefabThumbnail(string.Empty, png));
        }

        [Test]
        public void SavePrefabThumbnail_RefusesEmptyBytes()
        {
            Assert.IsFalse(UniThumbStorage.SavePrefabThumbnail(k_TestGuid, null));
            Assert.IsFalse(UniThumbStorage.SavePrefabThumbnail(k_TestGuid, new byte[0]));
        }

        [Test]
        public void PrefabThumbnail_RoundTrip_SurvivesCacheEviction()
        {
            byte[] png = CreateTestPng();
            Assert.IsTrue(UniThumbStorage.SavePrefabThumbnail(k_TestGuid, png));
            Assert.IsTrue(UniThumbStorage.HasPrefabThumbnail(k_TestGuid));

            // Evict the in-memory cache: reload must come back from disk by GUID.
            UniThumbStorage.ClearCache();
            Texture2D reloaded = UniThumbStorage.LoadPrefabThumbnail(k_TestGuid);
            Assert.IsNotNull(reloaded, "Prefab PNG must reload from disk by GUID after eviction.");
            Assert.IsTrue(UniThumbStorage.HasPrefabThumbnail(k_TestGuid));
        }

        [Test]
        public void DeleteByGuid_RemovesGuidEntry()
        {
            byte[] png = CreateTestPng();
            Assert.IsTrue(UniThumbStorage.SavePrefabThumbnail(k_TestGuid, png));
            Assert.IsTrue(UniThumbStorage.DeleteByGuid(k_TestGuid));
            Assert.IsFalse(UniThumbStorage.HasPrefabThumbnail(k_TestGuid));
        }

        [Test]
        public void CapturePrefab_RefusesInvalidAssetPath()
        {
            CaptureResult missing = UniThumbCapture.CapturePrefab(
                "Assets/DoesNotExist.prefab",
                UniThumbCapture.CreateDefaultSettings()
            );
            Assert.IsFalse(missing.Success);

            CaptureResult empty = UniThumbCapture.CapturePrefab(
                string.Empty,
                UniThumbCapture.CreateDefaultSettings()
            );
            Assert.IsFalse(empty.Success);
        }

        [Test]
        public void TryGetPrefabBounds_SubtreeRenderer_ReturnsBounds()
        {
            GameObject root = new GameObject("PrefabBoundsRoot");
            GameObject child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(root.transform, false);
            try
            {
                bool result = UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds);
                Assert.IsTrue(result);
                Assert.IsTrue(
                    bounds.extents.sqrMagnitude > 0,
                    "Prefab bounds should have non-zero extents."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TryGetPrefabBounds_NullRoot_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(null, -1, out Bounds bounds));
        }

        [Test]
        public void IsolateInstanceCanvasRenderers_CullsOnlyOutsideInstance()
        {
            GameObject instanceRoot = new GameObject("PrefabUiInstanceRoot");
            GameObject insideGo = new GameObject("PrefabUiInside");
            insideGo.transform.SetParent(instanceRoot.transform, false);
            CanvasRenderer inside = insideGo.AddComponent<CanvasRenderer>();
            GameObject outsideGo = new GameObject("OpenSceneUiOutside");
            CanvasRenderer outside = outsideGo.AddComponent<CanvasRenderer>();
            try
            {
                Assert.IsFalse(inside.cull);
                Assert.IsFalse(outside.cull);
                System.Collections.Generic.List<CanvasRenderer> isolated =
                    UniThumbCapture.IsolateInstanceCanvasRenderers(instanceRoot);
                try
                {
                    Assert.IsFalse(
                        inside.cull,
                        "Prefab instance CanvasRenderers must keep rendering."
                    );
                    Assert.IsTrue(
                        outside.cull,
                        "Open-scene CanvasRenderers must be culled during prefab capture."
                    );
                }
                finally
                {
                    UniThumbCapture.RestoreIsolatedCanvasRenderers(isolated);
                }
                Assert.IsFalse(inside.cull);
                Assert.IsFalse(outside.cull);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(instanceRoot);
            }
        }

        private static byte[] CreateTestPng()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new Color[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
