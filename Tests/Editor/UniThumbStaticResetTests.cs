using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    /// <summary>
    /// State-simulation tests for the 20260907-static-reset play-mode resets.
    /// Each test dirties static state via reflection (simulating a stale holder
    /// or leaked test seam), invokes the ExitingEditMode reset entry point,
    /// and asserts the wave-1 verdict: must-reset items clear, safe items
    /// (EditorPrefs toggles, caches, configs) survive. Written first (Red):
    /// pre-fix they fail to compile against the missing ResetForPlayModeExit
    /// seams; post-fix they pass.
    /// </summary>
    [TestFixture]
    public class UniThumbStaticResetTests
    {
        private static object GetHolder(Type ownerType)
        {
            FieldInfo field = ownerType.GetField(
                "s_State",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(field, "s_State holder missing on " + ownerType.Name);
            return field.GetValue(null);
        }

        private static void SetHolderField(object holder, string name, object value)
        {
            FieldInfo field = holder
                .GetType()
                .GetField(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
            Assert.IsNotNull(field, "Holder field missing: " + name);
            field.SetValue(holder, value);
        }

        private static object GetHolderField(object holder, string name)
        {
            FieldInfo field = holder
                .GetType()
                .GetField(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
            Assert.IsNotNull(field, "Holder field missing: " + name);
            return field.GetValue(holder);
        }

        private static object GetPrivateStatic(Type ownerType, string name)
        {
            FieldInfo field = ownerType.GetField(
                name,
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(field, "Static field missing: " + ownerType.Name + "." + name);
            return field.GetValue(null);
        }

        private static void SetPrivateStatic(Type ownerType, string name, object value)
        {
            FieldInfo field = ownerType.GetField(
                name,
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.IsNotNull(field, "Static field missing: " + ownerType.Name + "." + name);
            field.SetValue(null, value);
        }

        private static void DirtyBatchHolderForTest()
        {
            object holder = GetHolder(typeof(UniThumbBatchMenus));
            SetHolderField(holder, "PendingScenes", null);
            SetHolderField(holder, "SucceededScenes", new List<string> { "Assets/A.unity" });
            SetHolderField(holder, "FailedScenes", new List<string> { "Assets/B.unity" });
            SetHolderField(holder, "TotalScenes", 7);
            SetHolderField(holder, "ProcessedCount", 3);
            SetHolderField(holder, "OriginalScenePath", "Assets/Original.unity");
            SetHolderField(holder, "SwitchedScenes", true);
            SetHolderField(holder, "BatchKind", "Folder");
            SetHolderField(holder, "SkippedCount", 2);
            CaptureSettings settings = UniThumbCapture.CreateDefaultSettings();
            settings.Width = 999;
            SetHolderField(holder, "BatchSettings", settings);
            SetHolderField(holder, "CurrentScenePath", "Assets/Current.unity");
            SetHolderField(holder, "CancelRequested", true);
            SetHolderField(holder, "WaitingForShaderCompile", true);
            SetHolderField(holder, "WaitStartedAt", 1234.5);
            SetHolderField(holder, "WroteThumbnails", true);
        }

        [TearDown]
        public void TearDown()
        {
            UniThumbWindow.BeginUiSessionOverride = null;
            UniThumbBatchMenus.UnsavedChangesProbeForTest = null;
            UniThumbCapture.SetPostProcessingAvailableForTest(null);
            UniThumbGuard.Exit();
            // Leave holders idle-clean for the next test.
            UniThumbBatchMenus.ResetForPlayModeExit();
            UniThumbCapture.ResetForPlayModeExit();
            UniThumbWindow.ResetForPlayModeExit();
            SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_Coroutine", null);
            SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_CheckComplete", false);
        }

        #region BatchMenus holder

        [Test]
        public void BatchReset_Idle_ClearsHolderViaSharedTeardown()
        {
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
            Assert.IsFalse(UniThumbGuard.IsGenerating);
            DirtyBatchHolderForTest();

            UniThumbBatchMenus.ResetForPlayModeExit();

            object holder = GetHolder(typeof(UniThumbBatchMenus));
            Assert.IsNull(GetHolderField(holder, "PendingScenes"));
            Assert.IsNull(GetHolderField(holder, "SucceededScenes"));
            Assert.IsNull(GetHolderField(holder, "FailedScenes"));
            Assert.AreEqual(0, GetHolderField(holder, "TotalScenes"));
            Assert.AreEqual(0, GetHolderField(holder, "ProcessedCount"));
            Assert.IsNull(GetHolderField(holder, "OriginalScenePath"));
            Assert.AreEqual(false, GetHolderField(holder, "SwitchedScenes"));
            Assert.IsNull(GetHolderField(holder, "BatchKind"));
            Assert.AreEqual(0, GetHolderField(holder, "SkippedCount"));
            CaptureSettings cleared = (CaptureSettings)GetHolderField(holder, "BatchSettings");
            Assert.AreEqual(0, cleared.Width);
            Assert.IsNull(GetHolderField(holder, "CurrentScenePath"));
            Assert.AreEqual(false, GetHolderField(holder, "CancelRequested"));
            Assert.AreEqual(false, GetHolderField(holder, "WaitingForShaderCompile"));
            Assert.AreEqual(0.0, GetHolderField(holder, "WaitStartedAt"));
            Assert.AreEqual(false, GetHolderField(holder, "WroteThumbnails"));
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
        }

        [Test]
        public void BatchReset_RunningBatch_SkipsHolderClear()
        {
            object holder = GetHolder(typeof(UniThumbBatchMenus));
            SetHolderField(holder, "PendingScenes", new Queue<string>(new[] { "Assets/A.unity" }));
            SetHolderField(holder, "TotalScenes", 5);
            Assert.IsTrue(UniThumbBatchMenus.IsBatchRunning);
            try
            {
                UniThumbBatchMenus.ResetForPlayModeExit();
                Assert.IsNotNull(GetHolderField(holder, "PendingScenes"));
                Assert.AreEqual(5, GetHolderField(holder, "TotalScenes"));
            }
            finally
            {
                SetHolderField(holder, "PendingScenes", null);
                SetHolderField(holder, "TotalScenes", 0);
                UniThumbGuard.Exit();
                UniThumbBatchMenus.ResetForPlayModeExit();
            }
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
        }

        [Test]
        public void BatchReset_GuardHeld_SkipsHolderClear()
        {
            Assert.IsTrue(UniThumbGuard.TryEnter());
            object holder = GetHolder(typeof(UniThumbBatchMenus));
            try
            {
                DirtyBatchHolderForTest();
                UniThumbBatchMenus.ResetForPlayModeExit();
                Assert.AreEqual(7, GetHolderField(holder, "TotalScenes"));
                Assert.AreEqual("Folder", GetHolderField(holder, "BatchKind"));
            }
            finally
            {
                UniThumbGuard.Exit();
                UniThumbBatchMenus.ResetForPlayModeExit();
            }
            Assert.IsFalse(UniThumbBatchMenus.IsBatchRunning);
        }

        [Test]
        public void BatchReset_ClearsProbeOnAnyPhase()
        {
            UniThumbBatchMenus.UnsavedChangesProbeForTest = () => true;
            UniThumbBatchMenus.ResetForPlayModeExit();
            Assert.IsNull(UniThumbBatchMenus.UnsavedChangesProbeForTest);

            // Probe also clears while a batch is running (no idle guard).
            object holder = GetHolder(typeof(UniThumbBatchMenus));
            SetHolderField(holder, "PendingScenes", new Queue<string>(new[] { "Assets/A.unity" }));
            try
            {
                UniThumbBatchMenus.UnsavedChangesProbeForTest = () => false;
                UniThumbBatchMenus.ResetForPlayModeExit();
                Assert.IsNull(UniThumbBatchMenus.UnsavedChangesProbeForTest);
                Assert.IsNotNull(GetHolderField(holder, "PendingScenes"));
            }
            finally
            {
                SetHolderField(holder, "PendingScenes", null);
                UniThumbBatchMenus.ResetForPlayModeExit();
            }
        }

        [Test]
        public void BatchReset_PreservesDiscardToggle()
        {
            bool original = UniThumbBatchMenus.AllowDiscardUnsavedChanges;
            try
            {
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = true;
                UniThumbBatchMenus.ResetForPlayModeExit();
                Assert.IsTrue(UniThumbBatchMenus.AllowDiscardUnsavedChanges);
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = false;
                UniThumbBatchMenus.ResetForPlayModeExit();
                Assert.IsFalse(UniThumbBatchMenus.AllowDiscardUnsavedChanges);
            }
            finally
            {
                UniThumbBatchMenus.AllowDiscardUnsavedChanges = original;
            }
        }

        #endregion

        #region Capture

        [Test]
        public void CaptureReset_Idle_RestoresRecordUndo()
        {
            Assert.IsFalse(UniThumbGuard.IsGenerating);
            UniThumbCapture.RecordUndo = false;
            UniThumbCapture.ResetForPlayModeExit();
            Assert.IsTrue(UniThumbCapture.RecordUndo);
        }

        [Test]
        public void CaptureReset_Generating_PreservesRecordUndo()
        {
            Assert.IsTrue(UniThumbGuard.TryEnter());
            try
            {
                UniThumbCapture.RecordUndo = false;
                UniThumbCapture.ResetForPlayModeExit();
                Assert.IsFalse(UniThumbCapture.RecordUndo);
            }
            finally
            {
                UniThumbGuard.Exit();
                UniThumbCapture.RecordUndo = true;
            }
        }

        [Test]
        public void CaptureReset_ClearsPostProcessingOverrideAnyPhase()
        {
            UniThumbCapture.SetPostProcessingAvailableForTest(true);
            UniThumbCapture.ResetForPlayModeExit();
            Assert.IsNull(GetCapturePostProcessingOverride());

            Assert.IsTrue(UniThumbGuard.TryEnter());
            try
            {
                UniThumbCapture.SetPostProcessingAvailableForTest(false);
                UniThumbCapture.ResetForPlayModeExit();
                Assert.IsNull(GetCapturePostProcessingOverride());
            }
            finally
            {
                UniThumbGuard.Exit();
                UniThumbCapture.SetPostProcessingAvailableForTest(null);
            }
        }

        private static object GetCapturePostProcessingOverride()
        {
            return GetHolderField(
                GetHolder(typeof(UniThumbCapture)),
                "PostProcessingAvailableOverride"
            );
        }

        #endregion

        #region UpdateChecker

        [Test]
        public void UpdateReset_InFlight_AbortsPumpAndCompletes()
        {
            SetPrivateStatic(
                typeof(UniThumbUpdateChecker),
                "s_Coroutine",
                new ArrayList().GetEnumerator()
            );
            SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_CheckComplete", false);

            UniThumbUpdateChecker.ResetForPlayModeExit();

            Assert.IsNull(GetPrivateStatic(typeof(UniThumbUpdateChecker), "s_Coroutine"));
            Assert.IsTrue(UniThumbUpdateChecker.IsCheckComplete);
        }

        [Test]
        public void UpdateReset_Idle_PreservesIncompleteFlag()
        {
            SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_Coroutine", null);
            SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_CheckComplete", false);

            UniThumbUpdateChecker.ResetForPlayModeExit();

            Assert.IsFalse(UniThumbUpdateChecker.IsCheckComplete);
        }

        [Test]
        public void UpdateReset_PreservesCacheMirrorsAndToggle()
        {
            bool toggleOriginal = UniThumbUpdateChecker.UpdateCheckEnabled;
            try
            {
                SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_Coroutine", null);
                SetPrivateStatic(typeof(UniThumbUpdateChecker), "s_CheckComplete", false);
                UniThumbUpdateChecker.SetUpdateCheckEnabled(true);
                UniThumbUpdateChecker.ResetForPlayModeExit();
                Assert.IsTrue(UniThumbUpdateChecker.UpdateCheckEnabled);
                UniThumbUpdateChecker.SetUpdateCheckEnabled(false);
                UniThumbUpdateChecker.ResetForPlayModeExit();
                Assert.IsFalse(UniThumbUpdateChecker.UpdateCheckEnabled);
            }
            finally
            {
                UniThumbUpdateChecker.SetUpdateCheckEnabled(toggleOriginal);
            }
        }

        #endregion

        #region Window

        [Test]
        public void WindowReset_ClearsBeginUiSessionOverride()
        {
            UniThumbWindow.BeginUiSessionOverride = (cam, scale) => null;
            UniThumbWindow.ResetForPlayModeExit();
            Assert.IsNull(UniThumbWindow.BeginUiSessionOverride);
        }

        #endregion
    }
}
