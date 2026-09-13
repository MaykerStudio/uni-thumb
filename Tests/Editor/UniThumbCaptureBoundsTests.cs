using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbCaptureBoundsTests
    {
        private SceneSetup _sceneSetup;

        [SetUp]
        public void SetUp()
        {
            _sceneSetup = new SceneSetup();
        }

        [TearDown]
        public void TearDown()
        {
            _sceneSetup.CleanUp();
        }

        [Test]
        public void TryGetTilemapOnlyBounds_WithTilemapEncapsulatesCellBounds()
        {
            // Create a Tilemap and TilemapRenderer
            GameObject tilemapObj = new GameObject("TestTilemap");
            Tilemap tilemap = tilemapObj.AddComponent<Tilemap>();
            TilemapRenderer tmr = tilemapObj.AddComponent<TilemapRenderer>();

            // Set up a grid for the tilemap
            GameObject gridObj = new GameObject("TestGrid");
            Grid grid = gridObj.AddComponent<Grid>();
            tilemapObj.transform.SetParent(gridObj.transform);

            // Track objects for cleanup
            _sceneSetup.Track(tilemapObj);
            _sceneSetup.Track(gridObj);

            // Create a tile asset and paint tiles to populate cellBounds
            Tile tile = ScriptableObject.CreateInstance<Tile>();
            tilemap.tileAnchor = new Vector3(0.5f, 0.5f, 0f);

            // Paint a 5x5 grid of tiles
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    tilemap.SetTile(new Vector3Int(x, y, 0), tile);
                }
            }

            // Call the method
            LayerMask layerMask = -1; // Everything
            bool result = UniThumbCapture.TryGetTilemapOnlyBounds(layerMask, out Bounds bounds);

            // Assert
            Assert.IsTrue(result, "Should return true when TilemapRenderer exists");
            Assert.IsTrue(bounds.size.x > 0, "Bounds X should be non-zero");
            Assert.IsTrue(bounds.size.y > 0, "Bounds Y should be non-zero");
        }

        [Test]
        public void TryGetTilemapOnlyBounds_WithoutTilemap_ReturnsFalse()
        {
            // Create a SpriteRenderer (non-tilemap)
            GameObject spriteObj = new GameObject("TestSprite");
            SpriteRenderer sr = spriteObj.AddComponent<SpriteRenderer>();
            // whiteTexture is 4x4 — use a rect that fits
            sr.sprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0, 0, 4, 4),
                Vector2.zero,
                1
            );

            // Track objects for cleanup
            _sceneSetup.Track(spriteObj);

            // Call the method
            LayerMask layerMask = -1; // Everything
            bool result = UniThumbCapture.TryGetTilemapOnlyBounds(layerMask, out Bounds bounds);

            // Assert
            Assert.IsFalse(result, "Should return false when no TilemapRenderer exists");
        }

        [Test]
        public void TryGetSceneBounds_ZeroTilemaps_FallsBackToLegacyBehaviour()
        {
            // Create a MeshRenderer (non-tilemap)
            GameObject meshObj = new GameObject("TestMesh");
            MeshFilter mf = meshObj.AddComponent<MeshFilter>();
            MeshRenderer mr = meshObj.AddComponent<MeshRenderer>();
            mf.mesh = CreateTestMesh();

            // Track objects for cleanup
            _sceneSetup.Track(meshObj);

            // Call the method
            LayerMask layerMask = -1; // Everything
            bool result = UniThumbCapture.TryGetSceneBounds(layerMask, out Bounds bounds);

            // Assert
            Assert.IsTrue(result, "Should return true when MeshRenderer exists");
            Assert.IsTrue(bounds.extents.sqrMagnitude > 0, "Bounds should have non-zero extents");
        }

        private Mesh CreateTestMesh()
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

        private class SceneSetup
        {
            private readonly List<GameObject> _createdObjects = new List<GameObject>();

            public SceneSetup() { }

            public void CleanUp()
            {
                foreach (GameObject obj in _createdObjects)
                {
                    if (obj != null)
                    {
                        Object.DestroyImmediate(obj);
                    }
                }
                _createdObjects.Clear();
            }

            public void Track(GameObject obj)
            {
                _createdObjects.Add(obj);
            }
        }
    }
}
