namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Shared re-entrancy guard for MANUAL thumbnail generation paths only (window
    /// button, context menus, batch). Prevents double-trigger from rapid clicks or
    /// multi-select menu items. Capture is never started automatically by this tool:
    /// every generation must go through an explicit user action.
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
        }

        #endregion

        #region Properties

        public static bool IsGenerating
        {
            get { return s_State.IsGenerating; }
        }

        #endregion

        #region Public Methods

        public static bool TryEnter()
        {
            if (s_State.IsGenerating)
            {
                return false;
            }
            s_State.IsGenerating = true;
            return true;
        }

        public static void Exit()
        {
            s_State.IsGenerating = false;
        }

        #endregion
    }
}
