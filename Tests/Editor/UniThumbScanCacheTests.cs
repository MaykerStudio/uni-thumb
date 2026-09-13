using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbScanCacheTests
    {
        private readonly List<GameObject> _tracked = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject obj in _tracked)
            {
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }
            _tracked.Clear();
            UniThumbCapture.DisableScanCache = false;
            UniThumbBatchMenus.DisableAssetCache = false;
        }

        [Test]
        public void BeginScan_ReturnsSharedSnapshotWithinSweepBudget()
        {
            UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
            Assert.IsNotNull(scan, "BeginScan must return a cache when bypass is off");
            Assert.IsNotNull(scan.Renderers, "Renderers array must be set");
            Assert.IsNotNull(scan.Lights, "Lights array must be set");
            Assert.IsNotNull(scan.Components, "Components array must be set");
            Assert.IsNotNull(scan.CanvasRenderers, "CanvasRenderers array must be set");
            Assert.LessOrEqual(
                UniThumbCapture.LastScanSweepCount,
                5,
                "One capture scan must use at most 5 native sweeps"
            );
        }

        [Test]
        public void BeginScan_BypassReturnsNullAndDirectPathsStillWork()
        {
            LayerMask mask = -1;
            Bounds bypassedBounds;
            bool bypassedOk;
            UniThumbCapture.DisableScanCache = true;
            try
            {
                Assert.IsNull(UniThumbCapture.BeginScan(), "Bypass must return null");
                bypassedOk = UniThumbCapture.TryGetSceneBounds(mask, out bypassedBounds);
            }
            finally
            {
                UniThumbCapture.DisableScanCache = false;
            }

            UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
            Bounds cachedBounds;
            bool cachedOk = UniThumbCapture.TryGetSceneBounds(mask, scan, out cachedBounds);
            Assert.AreEqual(bypassedOk, cachedOk, "Bypassed and cached paths must agree");
            if (bypassedOk && cachedOk)
            {
                AssertBoundsEqual(bypassedBounds, cachedBounds);
            }
        }

        [Test]
        public void SceneBounds_ScanMatchesDirectSweep()
        {
            GameObject go = new GameObject("ScanCacheBoundsProbe");
            _tracked.Add(go);
            MeshFilter mf = go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mf.mesh = CreateQuad();
            Assert.IsNotNull(mr, "Probe renderer must exist");

            LayerMask mask = -1;
            Bounds direct;
            bool directOk = UniThumbCapture.TryGetSceneBounds(mask, out direct);
            UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
            Bounds cached;
            bool cachedOk = UniThumbCapture.TryGetSceneBounds(mask, scan, out cached);

            Assert.AreEqual(directOk, cachedOk, "Scan and direct must agree on bounds presence");
            if (directOk && cachedOk)
            {
                AssertBoundsEqual(direct, cached);
            }
        }

        [Test]
        public void DirectionalLight_ScanMatchesDirectSweep()
        {
            GameObject go = new GameObject("ScanCacheLightProbe");
            _tracked.Add(go);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.enabled = true;

            Light direct = UniThumbCapture.TryFindDirectionalLight();
            UniThumbCapture.ScanCache scan = UniThumbCapture.BeginScan();
            Light cached = UniThumbCapture.TryFindDirectionalLight(scan);

            Assert.IsNotNull(cached, "Scan must resolve an active directional light");
            Assert.AreEqual(
                LightType.Directional,
                cached.type,
                "Resolved light must be directional"
            );
            Assert.IsTrue(cached.isActiveAndEnabled, "Resolved light must be active");

            int activeDirectionals = 0;
            foreach (Light candidate in scan.Lights)
            {
                if (
                    candidate != null
                    && candidate.isActiveAndEnabled
                    && candidate.type == LightType.Directional
                )
                {
                    activeDirectionals++;
                }
            }
            if (activeDirectionals == 1)
            {
                Assert.AreSame(direct, cached, "Scan and direct must resolve the same light");
                Assert.AreSame(light, cached, "Single directional in scene must be the probe");
            }
        }

        [Test]
        public void VolumeProfileGuids_CachedResolutionMatchesFreshLookup()
        {
            string[] fresh = AssetDatabase.FindAssets("t:VolumeProfile");
            string[] first = UniThumbCapture.GetVolumeProfileGuidsCached();
            string[] second = UniThumbCapture.GetVolumeProfileGuidsCached();
            CollectionAssert.AreEqual(fresh, first, "Cached guids must equal a fresh lookup");
            CollectionAssert.AreEqual(first, second, "Repeat lookup must reuse the cache");
            Assert.AreEqual(
                fresh.Length > 0,
                UniThumbCapture.HasVolumeProfileAssets(),
                "HasVolumeProfileAssets must agree with a fresh lookup"
            );

            UniThumbCapture.InvalidateVolumeProfileCache();
            string[] after = UniThumbCapture.GetVolumeProfileGuidsCached();
            CollectionAssert.AreEqual(fresh, after, "Post-invalidate lookup must match fresh");
        }

        [Test]
        public void RefreshCollector_CachedOutputEqualsBypassedOutput()
        {
            List<string> bypassed;
            UniThumbBatchMenus.DisableAssetCache = true;
            try
            {
                bypassed = UniThumbBatchMenus.CollectRefreshWork(false);
            }
            finally
            {
                UniThumbBatchMenus.DisableAssetCache = false;
            }

            UniThumbBatchMenus.InvalidateAssetCache();
            List<string> cached = UniThumbBatchMenus.CollectRefreshWork(false);
            List<string> cachedAgain = UniThumbBatchMenus.CollectRefreshWork(false);

            CollectionAssert.AreEquivalent(
                bypassed,
                cached,
                "Cached refresh collector must match the bypassed output"
            );
            CollectionAssert.AreEquivalent(
                cached,
                cachedAgain,
                "Repeat refresh collection must reuse the cache identically"
            );
        }

        [Test]
        public void BatchAssetGuids_ReusedAcrossFiltersAndInvalidated()
        {
            UniThumbBatchMenus.InvalidateAssetCache();
            string[] scenesFirst = UniThumbBatchMenus.GetCachedGuids("t:Scene");
            string[] scenesSecond = UniThumbBatchMenus.GetCachedGuids("t:Scene");
            string[] prefabs = UniThumbBatchMenus.GetCachedGuids("t:Prefab");
            Assert.IsNotNull(scenesFirst, "Scene guids must be returned");
            Assert.IsNotNull(prefabs, "Prefab guids must be returned");
            CollectionAssert.AreEqual(scenesFirst, scenesSecond, "Scene guids must be reused");

            CollectionAssert.AreEqual(
                AssetDatabase.FindAssets("t:Scene"),
                scenesFirst,
                "Cached scene guids must equal a fresh lookup"
            );

            UniThumbBatchMenus.InvalidateAssetCache();
            string[] after = UniThumbBatchMenus.GetCachedGuids("t:Scene");
            CollectionAssert.AreEqual(scenesFirst, after, "Post-invalidate guids must match");
        }

        private static void AssertBoundsEqual(Bounds expected, Bounds actual)
        {
            Assert.AreEqual(expected.center.x, actual.center.x, 0.001f, "Center X must match");
            Assert.AreEqual(expected.center.y, actual.center.y, 0.001f, "Center Y must match");
            Assert.AreEqual(expected.center.z, actual.center.z, 0.001f, "Center Z must match");
            Assert.AreEqual(expected.size.x, actual.size.x, 0.001f, "Size X must match");
            Assert.AreEqual(expected.size.y, actual.size.y, 0.001f, "Size Y must match");
            Assert.AreEqual(expected.size.z, actual.size.z, 0.001f, "Size Z must match");
        }

        private static Mesh CreateQuad()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-1, -1, 0),
                new Vector3(1, -1, 0),
                new Vector3(1, 1, 0),
                new Vector3(-1, 1, 0),
            };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
