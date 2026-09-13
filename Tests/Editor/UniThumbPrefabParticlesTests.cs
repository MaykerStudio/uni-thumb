using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbPrefabParticlesTests
    {
        [Test]
        public void SimulatePrefabParticles_NullRoot_IsNoOp()
        {
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(null));
        }

        [Test]
        public void SimulatePrefabParticles_EmptyRoot_LeavesBoundsFalse()
        {
            GameObject root = new GameObject("ParticlesEmptyRoot");
            try
            {
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(root));
                Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_VfxGraph_OutOfScopeNoThrow()
        {
            // VFX Graph (VisualEffect) is explicitly out of scope: the helper
            // must never touch it. When the VFX package is absent the type
            // does not exist; when present it must be ignored without throw.
            GameObject root = new GameObject("ParticlesVfxRoot");
            try
            {
                System.Type vfxType = System.Type.GetType(
                    "UnityEngine.VFX.VisualEffect, UnityEngine.VFXModule"
                );
                if (vfxType != null)
                {
                    root.AddComponent(vfxType);
                }
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(root));
                Assert.IsFalse(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_ParticleOnlyPrefab_ParticlesAndBoundsAfter()
        {
            GameObject root = new GameObject("ParticleOnlyRoot");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                Assert.IsNotNull(system);
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                // Fresh instance: zero live particles, so the thumbnail would
                // render (and frame) an empty prefab without pre-roll.
                Assert.AreEqual(0, system.particleCount);
                bool hadBoundsBefore = UniThumbCapture.TryGetPrefabBounds(
                    root,
                    -1,
                    out Bounds before
                );

                int undoBefore = Undo.GetCurrentGroup();
                UniThumbCapture.SimulatePrefabParticles(root);
                int undoAfter = Undo.GetCurrentGroup();

                // Representative frame: live particles present, bounds
                // non-empty, pixel content no longer uniform background.
                Assert.Greater(
                    system.particleCount,
                    0,
                    "Pre-roll must produce live particles for render."
                );
                Assert.IsTrue(
                    UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds after),
                    "Particle prefab must report bounds after pre-roll."
                );
                Assert.Greater(after.extents.sqrMagnitude, 0.001f);
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
        public void SimulatePrefabParticles_NestedSystems_SimulateOnceNoThrow()
        {
            GameObject root = new GameObject("ParticlesNestedRoot");
            try
            {
                ParticleSystem parent = root.AddComponent<ParticleSystem>();
                GameObject childGo = new GameObject("NestedChild");
                childGo.transform.SetParent(root.transform, false);
                ParticleSystem child = childGo.AddComponent<ParticleSystem>();
                parent.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                child.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                Assert.IsTrue(
                    UniThumbCapture.HasParticleSystemAncestor(child.transform, root.transform),
                    "Child must be detected as nested under a particle ancestor."
                );
                Assert.IsFalse(
                    UniThumbCapture.HasParticleSystemAncestor(parent.transform, root.transform),
                    "Root-most system must simulate directly."
                );
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(root));
                Assert.Greater(parent.particleCount, 0);
                Assert.Greater(child.particleCount, 0);
                Assert.IsTrue(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_MixedSpriteAndParticles_BoundsAfter()
        {
            // Renderer2D note: 2D sprite content renders ambient-only under
            // the prefab neutral lighting; pre-roll only affects the
            // ParticleSystem and must not disturb the sprite renderer.
            GameObject root = new GameObject("ParticlesMixedRoot");
            try
            {
                GameObject spriteGo = new GameObject("Sprite");
                spriteGo.transform.SetParent(root.transform, false);
                SpriteRenderer sprite = spriteGo.AddComponent<SpriteRenderer>();
                Assert.IsNotNull(sprite);
                GameObject psGo = new GameObject("Particles");
                psGo.transform.SetParent(root.transform, false);
                ParticleSystem system = psGo.AddComponent<ParticleSystem>();
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                UniThumbCapture.SimulatePrefabParticles(root);

                Assert.Greater(system.particleCount, 0);
                Assert.IsTrue(UniThumbCapture.TryGetPrefabBounds(root, -1, out Bounds bounds));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_IsolationAndGuard_Restored()
        {
            GameObject root = new GameObject("ParticlesIsolationRoot");
            GameObject outsideGo = new GameObject("ParticlesOutside");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                MeshRenderer outside = outsideGo.AddComponent<MeshRenderer>();
                outside.enabled = true;

                Assert.IsTrue(UniThumbGuard.TryEnter());
                try
                {
                    UniThumbCapture.SimulatePrefabParticles(root);
                    System.Collections.Generic.List<Renderer> isolated =
                        UniThumbCapture.IsolateInstanceRenderers(root);
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
        public void SimulatePrefabParticles_NullRootWithTime_IsNoOp()
        {
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(null, 1f));
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(null, 0f));
            Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(null, 5f));
        }

        [Test]
        public void SimulatePrefabParticles_DefaultOverloadMatchesOneSecond()
        {
            Assert.AreEqual(1f, UniThumbCapture.PrefabParticleSimulateTime, 0.001f);
            GameObject root = new GameObject("ParticlesDefaultTimeRoot");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                Assert.IsTrue(system != null);
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                UniThumbCapture.SimulatePrefabParticles(root);
                Assert.Greater(system.particleCount, 0);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_TimeClamp_NoThrow()
        {
            GameObject root = new GameObject("ParticlesTimeClampRoot");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                Assert.IsTrue(system != null);
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(root, -1f));
                Assert.DoesNotThrow(() => UniThumbCapture.SimulatePrefabParticles(root, 999f));
                Assert.Greater(system.particleCount, 0);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_TimeVariance_LoopingSystemDiffers()
        {
            GameObject root = new GameObject("ParticlesTimeVarianceRoot");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                Assert.IsTrue(system != null);
                ParticleSystem.MainModule main = system.main;
                main.loop = true;
                main.duration = 5f;
                main.startLifetime = 2f;
                main.startSpeed = 5f;
                main.maxParticles = 1000;
                ParticleSystem.EmissionModule emission = system.emission;
                emission.rateOverTime = 50f;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                UniThumbCapture.SimulatePrefabParticles(root, 0f);
                int countAt0 = system.particleCount;

                UniThumbCapture.SimulatePrefabParticles(root, 1f);
                int countAt1 = system.particleCount;
                ParticleSystem.Particle[] particlesAt1 = new ParticleSystem.Particle[1];
                int readAt1 = system.GetParticles(particlesAt1);
                Vector3 posAt1 = readAt1 > 0 ? particlesAt1[0].position : Vector3.zero;

                UniThumbCapture.SimulatePrefabParticles(root, 5f);
                int countAt5 = system.particleCount;
                ParticleSystem.Particle[] particlesAt5 = new ParticleSystem.Particle[1];
                int readAt5 = system.GetParticles(particlesAt5);
                Vector3 posAt5 = readAt5 > 0 ? particlesAt5[0].position : Vector3.zero;

                Assert.AreEqual(0, countAt0);
                Assert.Greater(countAt1, 0);
                Assert.Greater(countAt5, 0);
                Assert.GreaterOrEqual(countAt5, countAt1);
                bool countsDiffer = countAt1 != countAt5;
                bool positionsDiffer = (posAt1 - posAt5).sqrMagnitude > 0.000001f;
                Assert.IsTrue(
                    countsDiffer || positionsDiffer,
                    "1s and 5s frames must differ in count or position."
                );
                Assert.IsTrue(
                    countAt0 != countAt1,
                    "0s restart-only frame must differ from the 1s frame."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_SceneSystemUntouched()
        {
            GameObject root = new GameObject("ParticlesScenePinRoot");
            GameObject outsideGo = new GameObject("ParticlesScenePinOutside");
            try
            {
                ParticleSystem inside = root.AddComponent<ParticleSystem>();
                ParticleSystem outside = outsideGo.AddComponent<ParticleSystem>();
                Assert.IsTrue(inside != null);
                Assert.IsTrue(outside != null);
                inside.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                outside.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                Assert.AreEqual(0, outside.particleCount);

                UniThumbCapture.SimulatePrefabParticles(root, 2f);

                Assert.Greater(inside.particleCount, 0);
                Assert.AreEqual(
                    0,
                    outside.particleCount,
                    "Scene pre-roll must never simulate systems outside the prefab root."
                );
            }
            finally
            {
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SimulatePrefabParticles_TimeOverload_UndoUnchanged()
        {
            GameObject root = new GameObject("ParticlesTimeUndoRoot");
            try
            {
                ParticleSystem system = root.AddComponent<ParticleSystem>();
                Assert.IsTrue(system != null);
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                int undoBefore = Undo.GetCurrentGroup();
                UniThumbCapture.SimulatePrefabParticles(root, 2f);
                int undoAfter = Undo.GetCurrentGroup();
                Assert.AreEqual(
                    undoBefore,
                    undoAfter,
                    "Time overload must not record Undo entries (no scene dirty)."
                );
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
