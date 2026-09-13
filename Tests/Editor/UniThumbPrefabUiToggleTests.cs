using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// Prefab UI toggle-reuse tests (wave 2 toggle): the prefab capture and
    /// preview paths reuse the existing capture-UI flag, so no new setting
    /// exists. Default true renders prefab UI by default (behavior change);
    /// toggle off suppresses UI with pixel parity to the old behavior.
    /// </summary>
    [TestFixture]
    public class UniThumbPrefabUiToggleTests
    {
        [Test]
        public void Default_CaptureUi_IsTrue_Settings()
        {
            UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            try
            {
                Assert.IsTrue(settings.CaptureUi);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Default_CaptureUi_IsTrue_CaptureSettings()
        {
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            Assert.IsTrue(settings.CaptureUi);
        }

        [Test]
        public void Default_CaptureUi_IsTrue_Window()
        {
            UniThumbWindow window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                Assert.IsTrue(ReadWindowCaptureUi(window));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void NoNewPrefabUiSetting()
        {
            foreach (
                MemberInfo member in typeof(UniThumbSettings).GetMembers(
                    BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.Instance
                        | BindingFlags.Static
                )
            )
            {
                string name = member.Name.ToLowerInvariant();
                Assert.IsFalse(
                    name.Contains("prefab")
                        && (
                            name.Contains("ui")
                            || name.Contains("canvas")
                            || name.Contains("overlay")
                        ),
                    "Unexpected prefab UI setting: " + member.Name + "."
                );
            }
            foreach (
                MemberInfo member in typeof(CaptureSettings).GetMembers(
                    BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.Instance
                        | BindingFlags.Static
                )
            )
            {
                string name = member.Name.ToLowerInvariant();
                Assert.IsFalse(
                    name.Contains("prefab")
                        && (
                            name.Contains("ui")
                            || name.Contains("canvas")
                            || name.Contains("overlay")
                        ),
                    "Unexpected prefab UI setting: " + member.Name + "."
                );
            }
        }

        [Test]
        public void SetCaptureUi_RoundTrips()
        {
            UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            try
            {
                settings.SetCaptureUi(false);
                Assert.IsFalse(settings.CaptureUi);
                settings.SetCaptureUi(true);
                Assert.IsTrue(settings.CaptureUi);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildSettings_FlowsCaptureUiToPrefabPath()
        {
            UniThumbWindow window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                WriteWindowCaptureUi(window, false);
                Assert.IsFalse(InvokeBuildSettings(window).CaptureUi);
                WriteWindowCaptureUi(window, true);
                Assert.IsTrue(InvokeBuildSettings(window).CaptureUi);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void PersistRoundTrip_PreservesCaptureUi()
        {
            UniThumbSettings settings = ScriptableObject.CreateInstance<UniThumbSettings>();
            UniThumbWindow window = ScriptableObject.CreateInstance<UniThumbWindow>();
            try
            {
                settings.SetCaptureUi(false);
                window.ApplyPersistedCaptureSettings(settings);
                Assert.IsFalse(InvokeBuildSettings(window).CaptureUi);
                window.PersistCaptureSettings(settings);
                Assert.IsFalse(settings.CaptureUi);
                settings.SetCaptureUi(true);
                window.ApplyPersistedCaptureSettings(settings);
                window.PersistCaptureSettings(settings);
                Assert.IsTrue(settings.CaptureUi);
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void PrefabPath_NeverCompositeEligible_UiGatedOnFlag()
        {
            CaptureSettings on = UniThumbCapture.CreateDefaultSettings();
            on.UseSceneViewAngle = false;
            on.CaptureUi = true;
            Assert.IsFalse(
                UniThumbCapture.IsSceneViewUiCompositeEligible(on),
                "Prefab orbit path must use the legacy UI session, gated on the shared flag."
            );
            CaptureSettings off = UniThumbCapture.CreateDefaultSettings();
            off.UseSceneViewAngle = false;
            off.CaptureUi = false;
            Assert.IsFalse(
                UniThumbCapture.IsSceneViewUiCompositeEligible(off),
                "Toggle off must skip every UI session (baseline parity: UI culled as before)."
            );
        }

        private static bool ReadWindowCaptureUi(UniThumbWindow window)
        {
            FieldInfo field = typeof(UniThumbWindow).GetField(
                "_captureUi",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(field, "Missing _captureUi.");
            return (bool)field.GetValue(window);
        }

        private static void WriteWindowCaptureUi(UniThumbWindow window, bool value)
        {
            FieldInfo field = typeof(UniThumbWindow).GetField(
                "_captureUi",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(field, "Missing _captureUi.");
            field.SetValue(window, value);
        }

        private static CaptureSettings InvokeBuildSettings(UniThumbWindow window)
        {
            MethodInfo method = typeof(UniThumbWindow).GetMethod(
                "BuildSettings",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(method, "Missing BuildSettings.");
            return (CaptureSettings)method.Invoke(window, null);
        }
    }
}
