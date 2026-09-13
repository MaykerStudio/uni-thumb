using System;
using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbHdrpProfileSwapTests
    {
        [Test]
        public void TryCreateTempPostProcessingVolume_NullProfile_UsesProjectFallback()
        {
            // Documented fallback contract (UniThumbCapture docstring plus
            // ResolvePostProcessingProfile): a null assigned profile falls
            // back to the first usable project VolumeProfile when the scene
            // has no Volumes.
            var settings = new CaptureSettings { PostProcessingProfile = null };

            GameObject result = UniThumbCapture.TryCreateTempPostProcessingVolume(
                settings,
                out UnityEngine.Object clonedProfile
            );

            try
            {
                if (UniThumbCapture.HasPostProcessingVolumes())
                {
                    // Validator-required reason: the null expectation applies
                    // only in the scene-has-volumes branch (scene volumes are
                    // honored as-is, so no temp volume is injected).
                    Assert.IsNull(
                        result,
                        "Scene volumes apply as-is, so no temp volume is expected"
                    );
                }
                else if (!UniThumbCapture.HasVolumeProfileAssets())
                {
                    Assert.IsNull(
                        result,
                        "No project profile exists, so no temp volume is expected"
                    );
                }
                else
                {
                    Assert.IsNotNull(
                        result,
                        "Null profile should fall back to the project profile"
                    );
                    Assert.AreEqual(
                        "__UniThumbTempGlobalVolume",
                        result.name,
                        "Fallback must inject the temp global volume"
                    );
                }
            }
            finally
            {
                if (result != null)
                {
                    UnityEngine.Object.DestroyImmediate(result);
                }
                if (clonedProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(clonedProfile);
                }
            }
        }

        [Test]
        public void TryCreateTempPostProcessingVolume_WrongTypeProfile_UsesProjectFallback()
        {
            // Use a ScriptableObject that is NOT a VolumeProfile to
            // simulate a stale or wrong-type ObjectField reference.
            // ResolvePostProcessingProfile rejects non-VolumeProfile types,
            // so the project fallback applies when the scene has no Volumes.
            ScriptableObject fakeProfile = ScriptableObject.CreateInstance<ScriptableObject>();

            var settings = new CaptureSettings { PostProcessingProfile = fakeProfile };

            GameObject result = UniThumbCapture.TryCreateTempPostProcessingVolume(
                settings,
                out UnityEngine.Object clonedProfile
            );

            try
            {
                if (UniThumbCapture.HasPostProcessingVolumes())
                {
                    // Validator-required reason: the null expectation applies
                    // only in the scene-has-volumes branch (scene volumes are
                    // honored as-is, so no temp volume is injected).
                    Assert.IsNull(
                        result,
                        "Scene volumes apply as-is, so no temp volume is expected"
                    );
                }
                else if (!UniThumbCapture.HasVolumeProfileAssets())
                {
                    Assert.IsNull(
                        result,
                        "No project profile exists, so no temp volume is expected"
                    );
                }
                else
                {
                    Assert.IsNotNull(
                        result,
                        "Wrong-type profile should fall back to the project profile"
                    );
                    Assert.AreEqual(
                        "__UniThumbTempGlobalVolume",
                        result.name,
                        "Fallback must inject the temp global volume"
                    );
                }
            }
            finally
            {
                if (result != null)
                {
                    UnityEngine.Object.DestroyImmediate(result);
                }
                if (clonedProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(clonedProfile);
                }
                UnityEngine.Object.DestroyImmediate(fakeProfile);
            }
        }

        [Test]
        public void TryCreateTempPostProcessingVolume_DestroyedProfile_UsesProjectFallback()
        {
            // Simulate the ObjectField deferred preview tick scenario:
            // a profile that was valid but has since been destroyed.
            // In Unity, DestroyImmediate makes the C# reference "fake-null"
            // (Unity's == null returns true), but the managed wrapper is
            // still non-null. The method must handle this without throwing,
            // and the project fallback applies when the scene has no Volumes.
            ScriptableObject staleProfile = ScriptableObject.CreateInstance<ScriptableObject>();
            UnityEngine.Object.DestroyImmediate(staleProfile);

            var settings = new CaptureSettings { PostProcessingProfile = staleProfile };

            GameObject result = UniThumbCapture.TryCreateTempPostProcessingVolume(
                settings,
                out UnityEngine.Object clonedProfile
            );

            try
            {
                if (UniThumbCapture.HasPostProcessingVolumes())
                {
                    // Validator-required reason: the null expectation applies
                    // only in the scene-has-volumes branch (scene volumes are
                    // honored as-is, so no temp volume is injected).
                    Assert.IsNull(
                        result,
                        "Scene volumes apply as-is, so no temp volume is expected"
                    );
                }
                else if (!UniThumbCapture.HasVolumeProfileAssets())
                {
                    Assert.IsNull(
                        result,
                        "No project profile exists, so no temp volume is expected"
                    );
                }
                else
                {
                    Assert.IsNotNull(
                        result,
                        "Destroyed profile should fall back to the project profile"
                    );
                    Assert.AreEqual(
                        "__UniThumbTempGlobalVolume",
                        result.name,
                        "Fallback must inject the temp global volume"
                    );
                }
            }
            finally
            {
                if (result != null)
                {
                    UnityEngine.Object.DestroyImmediate(result);
                }
                if (clonedProfile != null)
                {
                    UnityEngine.Object.DestroyImmediate(clonedProfile);
                }
            }
        }

        [Test]
        public void IsHdrpPipeline_AbsentHdrp_SkipsWithoutThrow()
        {
            Assert.DoesNotThrow(() => UniThumbCapture.IsHdrpPipeline());
            Type hdacType = Type.GetType(
                "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime"
            );
            if (hdacType != null)
            {
                Assert.Ignore("HDRP package present; absent-path not exercised.");
            }
            Assert.IsFalse(UniThumbCapture.IsHdrpPipeline());
        }
    }
}
