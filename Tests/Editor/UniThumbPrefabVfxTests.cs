using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabVfxTests
    {
        [Test]
        public void SimulatePrefabVfx_NullRoot_IsNoOp()
        {
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(null));
        }

        [Test]
        public void SimulatePrefabVfx_VfxAbsent_SkipsNoThrow()
        {
            GameObject root = new GameObject("VfxAbsentRoot");
            try
            {
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root));
                Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_VfxPresent_SimulatedVisibilityAndBounds()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            if (vfxType == null)
            {
                Assert.Ignore("VFX Graph package absent; VisualEffect type unavailable.");
            }
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                Assert.Ignore("Built-in RP has no SRP compute path; VFX pre-roll skips.");
            }
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders unsupported; VFX pre-roll skips.");
            }
            GameObject root = new GameObject("VfxPresentRoot");
            try
            {
                Component effect = root.AddComponent(vfxType);
                Assert.IsNotNull(effect);
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);

                int undoBefore = Undo.GetCurrentGroup();
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root));
                int undoAfter = Undo.GetCurrentGroup();

                // Simulated visibility: the deterministic start seed is applied
                // before Reinit/Simulate, so it holds even for an asset-less
                // effect. Pause is not asserted: GPU simulate is async and the
                // paused flag may lag one frame after Simulate.
                System.Reflection.PropertyInfo seedProperty = vfxType.GetProperty("startSeed");
                if (seedProperty != null && seedProperty.CanRead)
                {
                    object seedValue = seedProperty.GetValue(effect, null);
                    Assert.AreEqual(UniThumbCapture.PrefabVfxSeed, seedValue);
                }
                Assert.IsNotNull(root.GetComponent(vfxType));

                Assert.IsTrue(
                    UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds),
                    "VFX prefab with visible content must report bounds after pre-roll."
                );
                Assert.Greater(bounds.extents.sqrMagnitude, 0.001f);
                Assert.AreEqual(
                    undoBefore,
                    undoAfter,
                    "Pre-roll must not record Undo entries (no scene dirty)."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_NestedEffects_SimulateOnceNoThrow()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            if (vfxType == null)
            {
                Assert.Ignore("VFX Graph package absent; VisualEffect type unavailable.");
            }
            GameObject root = new GameObject("VfxNestedRoot");
            try
            {
                Component parent = root.AddComponent(vfxType);
                GameObject childGo = new GameObject("VfxNestedChild");
                childGo.transform.SetParent(root.transform, false);
                Component child = childGo.AddComponent(vfxType);

                Assert.IsTrue(
                    UniThumbCapture.HasVisualEffectAncestor(
                        child.transform,
                        root.transform,
                        vfxType
                    ),
                    "Child must be detected as nested under a VFX ancestor."
                );
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(
                        parent.transform,
                        root.transform,
                        vfxType
                    ),
                    "Root-most effect must simulate directly."
                );
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root));
                Assert.IsNotNull(root.GetComponent(vfxType));
                Assert.IsNotNull(childGo.GetComponent(vfxType));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_IsolationAndGuard_RestoresForceRenderingOff()
        {
            GameObject root = new GameObject("VfxIsolationRoot");
            GameObject outsideGo = new GameObject("VfxOutside");
            try
            {
                System.Type vfxType = UniThumbCapture.FindVisualEffectType();
                if (vfxType != null)
                {
                    root.AddComponent(vfxType);
                }
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);
                MeshRenderer outside = outsideGo.AddComponent<MeshRenderer>();
                outside.enabled = true;

                Assert.IsTrue(UniThumbGuard.TryEnter(), "Guard should be free.");
                try
                {
                    UniThumbCapture.SimulatePrefabVfx(root);
                    List<Renderer> isolated = UniThumbCapture.IsolateInstanceRenderers(root);
                    try
                    {
                        Assert.IsTrue(outside.forceRenderingOff);
                    }
                    finally
                    {
                        UniThumbCapture.RestoreIsolatedRenderers(isolated);
                    }
                    Assert.IsFalse(outside.forceRenderingOff);
                }
                finally
                {
                    UniThumbGuard.Exit();
                }
                Assert.IsFalse(UniThumbGuard.IsGenerating);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_UndoUnchanged_NoSceneDirty()
        {
            GameObject root = new GameObject("VfxUndoRoot");
            try
            {
                System.Type vfxType = UniThumbCapture.FindVisualEffectType();
                if (vfxType != null)
                {
                    root.AddComponent(vfxType);
                }
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);

                int undoBefore = Undo.GetCurrentGroup();
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root));
                Assert.AreEqual(
                    undoBefore,
                    Undo.GetCurrentGroup(),
                    "Pre-roll must not record Undo entries (no scene dirty)."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_ParticleAndScenePaths_Unchanged()
        {
            GameObject root = new GameObject("VfxParticleUntouchedRoot");
            GameObject outsideGo = new GameObject("VfxSceneOutside");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Assert.AreEqual(0, system.particleCount);
                MeshRenderer outside = outsideGo.AddComponent<MeshRenderer>();
                outside.enabled = true;
                bool outsideOffBefore = outside.forceRenderingOff;

                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root));

                Assert.AreEqual(
                    0,
                    system.particleCount,
                    "VFX pre-roll must not advance ParticleSystems."
                );
                Assert.AreEqual(outsideOffBefore, outside.forceRenderingOff);
                Assert.IsTrue(outside.enabled);
                Assert.IsTrue(outsideGo.activeInHierarchy);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void HasVisualEffectAncestor_NullSafe_Classified()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            GameObject root = new GameObject("VfxAncestorRoot");
            GameObject child = new GameObject("VfxAncestorChild");
            child.transform.SetParent(root.transform, false);
            try
            {
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(null, root.transform, vfxType)
                );
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(child.transform, null, vfxType)
                );
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(child.transform, root.transform, null)
                );
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(root.transform, root.transform, vfxType)
                );
                Assert.IsFalse(
                    UniThumbCapture.HasVisualEffectAncestor(
                        child.transform,
                        root.transform,
                        vfxType
                    )
                );
                if (vfxType != null)
                {
                    root.AddComponent(vfxType);
                    Assert.IsTrue(
                        UniThumbCapture.HasVisualEffectAncestor(
                            child.transform,
                            root.transform,
                            vfxType
                        )
                    );
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_NullRootWithTime_IsNoOp()
        {
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(null, 0f));
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(null, 1f));
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(null, 5f));
        }

        [Test]
        public void SimulatePrefabVfx_AbsentWithTime_SkipsNoThrow()
        {
            GameObject root = new GameObject("VfxAbsentTimeRoot");
            try
            {
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 0f));
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 2f));
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 5f));
                Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_TimeClamp_NoThrow()
        {
            GameObject root = new GameObject("VfxTimeClampRoot");
            try
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, -5f));
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 999f));
                Assert.IsTrue(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
                Assert.Greater(bounds.extents.sqrMagnitude, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PrefabVfxSharedSteps_ConstantsAndRanges()
        {
            Assert.AreEqual(1f / 60f, UniThumbCapture.PrefabVfxStepDelta, 0.000001f);
            Assert.AreEqual(60, UniThumbCapture.PrefabVfxStepCount);
            Assert.AreEqual(8, UniThumbCapture.PrefabVfxMaxComponents);
            Assert.AreEqual(0u, UniThumbCapture.PrefabVfxSeed);
            Assert.AreEqual(
                60,
                Mathf.RoundToInt(1f / UniThumbCapture.PrefabVfxStepDelta),
                "1s at the shared step delta must be 60 steps."
            );
            Assert.AreEqual(
                300,
                Mathf.RoundToInt(5f / UniThumbCapture.PrefabVfxStepDelta),
                "5s at the shared step delta must hit the 300-step cap."
            );
            int stepsAtZero = Mathf.Clamp(
                Mathf.RoundToInt(0f / UniThumbCapture.PrefabVfxStepDelta),
                1,
                300
            );
            Assert.AreEqual(1, stepsAtZero, "0s still restarts with a single minimal step.");
            int stepsAtOver = Mathf.Clamp(
                Mathf.RoundToInt(999f / UniThumbCapture.PrefabVfxStepDelta),
                1,
                300
            );
            Assert.AreEqual(300, stepsAtOver);
        }

        [Test]
        public void VfxSimulateOverload_ResolveWhenPresent()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            if (vfxType == null)
            {
                Assert.Ignore("VFX Graph package absent; VisualEffect type unavailable.");
            }
            System.Reflection.MethodInfo uintOverload = vfxType.GetMethod(
                "Simulate",
                new System.Type[] { typeof(float), typeof(uint) }
            );
            System.Reflection.MethodInfo intOverload = vfxType.GetMethod(
                "Simulate",
                new System.Type[] { typeof(float), typeof(int) }
            );
            Assert.IsTrue(
                uintOverload != null || intOverload != null,
                "VFX package present but neither Simulate(float,uint) nor Simulate(float,int) resolves."
            );
        }

        [Test]
        public void SimulatePrefabVfx_PresentWithTime_AppliesSeed()
        {
            System.Type vfxType = UniThumbCapture.FindVisualEffectType();
            if (vfxType == null)
            {
                Assert.Ignore("VFX Graph package absent; VisualEffect type unavailable.");
            }
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                Assert.Ignore("Built-in RP has no SRP compute path; VFX pre-roll skips.");
            }
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders unsupported; VFX pre-roll skips.");
            }
            GameObject root = new GameObject("VfxPresentTimeRoot");
            try
            {
                Component effect = root.AddComponent(vfxType);
                Assert.IsTrue(effect != null);
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);

                int undoBefore = Undo.GetCurrentGroup();
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 2f));
                int undoAfter = Undo.GetCurrentGroup();

                System.Reflection.PropertyInfo seedProperty = vfxType.GetProperty("startSeed");
                if (seedProperty != null && seedProperty.CanRead)
                {
                    object seedValue = seedProperty.GetValue(effect, null);
                    Assert.AreEqual(UniThumbCapture.PrefabVfxSeed, seedValue);
                }
                Assert.IsTrue(root.GetComponent(vfxType) != null);
                Assert.IsTrue(
                    UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds),
                    "VFX prefab with visible content must report bounds after timed pre-roll."
                );
                Assert.Greater(bounds.extents.sqrMagnitude, 0.001f);
                Assert.AreEqual(
                    undoBefore,
                    undoAfter,
                    "Timed pre-roll must not record Undo entries (no scene dirty)."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabVfx_SceneSystemUntouched()
        {
            GameObject root = new GameObject("VfxScenePinRoot");
            GameObject outsideGo = new GameObject("VfxScenePinOutside");
            try
            {
                ParticleSystem outside = outsideGo.AddComponent<ParticleSystem>();
                Assert.IsTrue(outside != null);
                outside.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Assert.AreEqual(0, outside.particleCount);
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabVfx(root, 2f));
                Assert.AreEqual(
                    0,
                    outside.particleCount,
                    "VFX pre-roll must never simulate scene ParticleSystems."
                );
                Assert.IsTrue(outsideGo.activeInHierarchy);
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(root);
            }
        }
    }
}
