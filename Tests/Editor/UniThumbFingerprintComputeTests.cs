using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbFingerprintComputeTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in Object.FindObjectsOfType<GameObject>())
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Compute_EmptyScene_ReturnsZeroObjectCount()
        {
            UniThumbFingerprint.SceneFingerprint fp = UniThumbFingerprint.Compute();
            Assert.AreEqual(0, fp.ObjectCount);
        }

        [Test]
        public void Compute_WithObjects_ReturnsCorrectCounts()
        {
            GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject.CreatePrimitive(PrimitiveType.Cube);

            UniThumbFingerprint.SceneFingerprint fp = UniThumbFingerprint.Compute();

            Assert.GreaterOrEqual(fp.ObjectCount, 3);
        }

        [Test]
        public void Compute_WithMeshFilters_ReturnsNonZeroVertexCount()
        {
            GameObject.CreatePrimitive(PrimitiveType.Cube);

            UniThumbFingerprint.SceneFingerprint fp = UniThumbFingerprint.Compute();
            Assert.Greater(fp.TotalVertexCount, 0);
        }
    }
}
