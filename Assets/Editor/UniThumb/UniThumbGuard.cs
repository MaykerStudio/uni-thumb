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
        #region Properties

        public static bool IsGenerating { get; private set; }

        #endregion

        #region Public Methods

        public static bool TryEnter()
        {
            if (IsGenerating)
            {
                return false;
            }
            IsGenerating = true;
            return true;
        }

        public static void Exit()
        {
            IsGenerating = false;
        }

        #endregion
    }
}
