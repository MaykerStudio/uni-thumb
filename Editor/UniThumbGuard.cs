using System;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Shared re-entrancy guard for thumbnail generation paths (window
    /// button, context menus, batch, and the opt-in Regenerate on Save
    /// hooks for scenes and prefabs). Prevents double-trigger from rapid clicks or
    /// multi-select menu items. Automatic capture happens only through the
    /// opt-in Regenerate on Save setting (scene and prefab saves, while the
    /// window is open); everything else needs an explicit user action.
    /// </summary>
    public static class UniThumbGuard
    {
        #region Fields

        /// <summary>
        /// Guard flag on a readonly holder so the class keeps zero static mutable
        /// fields (Asset Store Validator "Check Static Variables"). Domain-reload
        /// semantics unchanged: a fresh holder is created on reload, so the guard
        /// starts released exactly as before.
        /// </summary>
        private static readonly GuardState s_State = new GuardState();

        private sealed class GuardState
        {
            public bool IsGenerating;
            public DateTime? EntryTime;
        }

        /// <summary>
        /// Seconds after which a held guard is considered stale and eligible for
        /// forced release. Single captures are synchronous; only the batch pump
        /// legitimately holds the guard across frames (and it always sets batch
        /// state first). 5 seconds is generous for a single capture.
        /// </summary>
        private const double k_StaleThresholdSeconds = 5.0;

        #endregion

        #region Properties

        public static bool IsGenerating
        {
            get { return s_State.IsGenerating; }
        }

        /// <summary>
        /// Timestamp when TryEnter last succeeded. Null when guard is released.
        /// </summary>
        internal static DateTime? EntryTime
        {
            get { return s_State.EntryTime; }
        }

        /// <summary>
        /// True when the guard appears stuck: held longer than
        /// <see cref="k_StaleThresholdSeconds"/> without any batch pump running.
        /// Used by <see cref="RecoverIfStale"/> for defense-in-depth recovery.
        /// </summary>
        internal static bool IsStale
        {
            get
            {
                if (!s_State.IsGenerating)
                {
                    return false;
                }

                // Batch pump legitimately holds the guard across frames;
                // never force-release while a batch is active.
                if (UniThumbBatchMenus.IsBatchRunning)
                {
                    return false;
                }

                if (s_State.EntryTime == null)
                {
                    return true;
                }

                return (DateTime.UtcNow - s_State.EntryTime.Value).TotalSeconds
                    > k_StaleThresholdSeconds;
            }
        }

        #endregion

        #region Public Methods

        public static bool TryEnter()
        {
            if (s_State.IsGenerating)
            {
                // Defense-in-depth: recover a stale hold via the shared
                // RecoverIfStale path (it logs) rather than leaving the
                // tool bricked.
                if (!RecoverIfStale())
                {
                    return false;
                }
            }
            s_State.IsGenerating = true;
            s_State.EntryTime = DateTime.UtcNow;
            return true;
        }

        public static void Exit()
        {
            s_State.IsGenerating = false;
            s_State.EntryTime = null;
        }

        /// <summary>
        /// Defense-in-depth: if the guard is stale (held too long without an
        /// active batch), force-release it so the tool is not bricked. Returns
        /// true if recovery was performed.
        /// </summary>
        public static bool RecoverIfStale()
        {
            if (!IsStale)
            {
                return false;
            }

            Debug.LogWarning(
                "[UniThumb] Guard recovery: forcing release of stale " + "re-entrancy guard."
            );
            Exit();
            return true;
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Test seam: allows tests to simulate a stale entry by setting an
        /// arbitrary past timestamp without sleeping.
        /// </summary>
        internal static void SetEntryTimeForTest(DateTime? entryTime)
        {
            s_State.EntryTime = entryTime;
        }

        #endregion
    }
}
