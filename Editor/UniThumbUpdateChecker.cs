using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Opt-in update check via the GitHub Releases API. Nothing runs
    /// automatically: there is no static constructor polling and no
    /// automatic network access. The only entry point is the explicit
    /// Check for Updates menu action, which itself is
    /// gated by the Check for Updates setting (UniThumbSettings asset,
    /// default off; legacy EditorPrefs opt-ins migrate once on first read).
    /// The sole InitializeOnLoad hook subscribes the in-flight-only
    /// play-mode reset (no network, never starts a check).
    /// Successful results are cached for 24 hours in EditorPrefs. Cache
    /// corruption is treated as a cache miss, never thrown.
    /// </summary>
    public static class UniThumbUpdateChecker
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";
        private const string k_GitHubApiUrl =
            "https://api.github.com/repos/MaykerStudio/uni-thumb/releases/latest";
        private const string k_LastCheckTimeKey = "UniThumb.LastCheckTime";
        private const string k_LatestVersionKey = "UniThumb.LatestVersion";
        private const string k_ReleaseUrlKey = "UniThumb.ReleaseUrl";
        private const string k_UpdateCheckEnabledKey = "UniThumb.UpdateCheckEnabled";
        private const string k_CheckMenuPath = "Tools/UniThumb/Check for Updates";
        private const double k_CheckIntervalHours = 24;

        #endregion

        #region Fields

        private static bool s_IsUpdateAvailable;
        private static string s_LatestVersion;
        private static string s_ReleaseUrl;
        private static bool s_CheckComplete;
        private static IEnumerator s_Coroutine;
        private static bool s_MigratedLegacyToggle;

        #endregion

        #region Properties

        public static bool IsUpdateAvailable => s_IsUpdateAvailable;

        public static string LatestVersion => s_LatestVersion;

        public static string ReleaseUrl => s_ReleaseUrl;

        public static bool IsCheckComplete => s_CheckComplete;

        /// <summary>
        /// Opt-in gate for the explicit update check. Settings-asset backed
        /// (UniThumbSettings.CheckForUpdates), default off. The Check for
        /// Updates menu action refuses to run while this is false.
        /// Falls back to the legacy EditorPrefs value when the settings
        /// asset is unavailable.
        /// </summary>
        public static bool UpdateCheckEnabled
        {
            get
            {
                UniThumbSettings settings = LoadSettingsOrNull();
                if (settings == null)
                {
                    return EditorPrefs.GetBool(k_UpdateCheckEnabledKey, false);
                }
                MigrateLegacyToggleOnce(settings);
                return settings.CheckForUpdates;
            }
        }

        #endregion

        #region Public Methods

        public static void ForceCheck()
        {
            EditorPrefs.DeleteKey(k_LastCheckTimeKey);
            s_IsUpdateAvailable = false;
            s_LatestVersion = null;
            s_ReleaseUrl = null;
            s_CheckComplete = false;
            CheckForUpdate();
        }

        /// <summary>
        /// Enables or disables the opt-in update check gate. Persists via
        /// the UniThumbSettings asset so the choice survives domain reloads
        /// and editor restarts; the legacy EditorPrefs key is kept in sync
        /// as a fallback for asset-unavailable contexts.
        /// </summary>
        public static void SetUpdateCheckEnabled(bool value)
        {
            try
            {
                UniThumbSettings settings = UniThumbSettings.Get();
                if (settings != null)
                {
                    settings.SetCheckForUpdates(value);
                }
            }
            catch
            {
                // Asset unavailable; EditorPrefs fallback below still applies.
            }
            EditorPrefs.SetBool(k_UpdateCheckEnabledKey, value);
        }

        /// <summary>
        /// Explicit menu entry point for the update check (Asset Store
        /// compliance: no automatic network access). Gated by
        /// UpdateCheckEnabled: while the toggle is off this logs an opt-in
        /// hint and performs no network request.
        /// </summary>
        [MenuItem(k_CheckMenuPath)]
        public static void CheckForUpdatesFromMenu()
        {
            if (!UpdateCheckEnabled)
            {
                Debug.Log(
                    k_LogPrefix
                        + "Update check is off (opt-in). Enable it first in the UniThumb window Settings tab (Tracking card)."
                );
                return;
            }
            ForceCheck();
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// True when a fresh (under 24h) check result is cached. A missing or
        /// corrupt timestamp is a cache miss, never an exception.
        /// </summary>
        internal static bool IsCacheValid()
        {
            if (!EditorPrefs.HasKey(k_LastCheckTimeKey))
            {
                return false;
            }

            long lastTicks;
            if (!long.TryParse(EditorPrefs.GetString(k_LastCheckTimeKey, "0"), out lastTicks))
            {
                return false;
            }
            DateTime lastCheck = new DateTime(lastTicks, DateTimeKind.Utc);
            TimeSpan elapsed = DateTime.UtcNow - lastCheck;
            return elapsed.TotalHours < 24;
        }

        /// <summary>
        /// Play-mode exit reset (ExitingEditMode, in-flight only). Aborts the
        /// pump (unsubscribes PumpCoroutine, drops s_Coroutine before the
        /// request is destroyed) then fail-safe completes to unblock the
        /// banner. Idle checks are untouched. SAFE (kept): k_* consts,
        /// s_IsUpdateAvailable/s_LatestVersion/s_ReleaseUrl 24h cache mirrors,
        /// UpdateCheckEnabled settings gate (must survive, default off).
        /// Idempotent, null-safe, Editor-only. Preserves the menu-gated
        /// default-off gate (never starts a check here).
        /// </summary>
        internal static void ResetForPlayModeExit()
        {
            if (s_Coroutine == null)
            {
                return;
            }
            try
            {
                EditorApplication.update -= PumpCoroutine;
            }
            finally
            {
                s_Coroutine = null;
                s_CheckComplete = true;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Subscribes the in-flight-only play-mode reset. Unsubscribe-then-
        /// subscribe keeps re-registration idempotent across domain reloads.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }
            ResetForPlayModeExit();
        }

        /// <summary>
        /// Loads the settings asset without throwing. Null when the asset
        /// pipeline is unavailable (the caller falls back to EditorPrefs).
        /// </summary>
        private static UniThumbSettings LoadSettingsOrNull()
        {
            try
            {
                return UniThumbSettings.Get();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One-time-per-session migration of pre-settings opt-ins: users who
        /// enabled the check via the old EditorPrefs-backed menu toggle keep
        /// their opt-in in the settings asset. Runs once so an explicit
        /// opt-out afterwards is never reverted.
        /// </summary>
        private static void MigrateLegacyToggleOnce(UniThumbSettings settings)
        {
            if (s_MigratedLegacyToggle)
            {
                return;
            }
            s_MigratedLegacyToggle = true;
            try
            {
                if (EditorPrefs.GetBool(k_UpdateCheckEnabledKey, false))
                {
                    if (!settings.CheckForUpdates)
                    {
                        settings.SetCheckForUpdates(true);
                    }
                    EditorPrefs.DeleteKey(k_UpdateCheckEnabledKey);
                }
            }
            catch
            {
                // Migration is best-effort; the gate still works either way.
            }
        }

        private static void CheckForUpdate()
        {
            s_CheckComplete = false;

            if (IsCacheValid())
            {
                ReadFromCache();
                s_CheckComplete = true;
                return;
            }

            EditorApplication.update += PumpCoroutine;
        }

        private static void ReadFromCache()
        {
            s_LatestVersion = EditorPrefs.GetString(k_LatestVersionKey, null);
            s_ReleaseUrl = EditorPrefs.GetString(k_ReleaseUrlKey, null);
            if (string.IsNullOrEmpty(s_LatestVersion))
            {
                return;
            }

            try
            {
                System.Version current = System.Version.Parse(PackageVersion());
                System.Version latest = System.Version.Parse(s_LatestVersion);
                s_IsUpdateAvailable = latest > current;
            }
            catch
            {
                s_IsUpdateAvailable = false;
            }
        }

        private static void PumpCoroutine()
        {
            if (s_Coroutine == null)
            {
                s_Coroutine = CheckForUpdateCoroutine();
            }

            try
            {
                // If Current is an unfinished AsyncOperation, wait for it.
                UnityEngine.AsyncOperation asyncOp =
                    s_Coroutine.Current as UnityEngine.AsyncOperation;
                if (asyncOp != null && !asyncOp.isDone)
                {
                    return;
                }

                if (!s_Coroutine.MoveNext())
                {
                    EditorApplication.update -= PumpCoroutine;
                    s_Coroutine = null;
                }
            }
            catch
            {
                EditorApplication.update -= PumpCoroutine;
                s_Coroutine = null;
                s_CheckComplete = true;
            }
        }

        private static IEnumerator CheckForUpdateCoroutine()
        {
            using (UnityWebRequest request = UnityWebRequest.Get(k_GitHubApiUrl))
            {
                request.timeout = 10;
                request.SetRequestHeader("Accept", "application/vnd.github.v3+json");
                yield return request.SendWebRequest();

                if (
                    request.result == UnityWebRequest.Result.ConnectionError
                    || request.result == UnityWebRequest.Result.ProtocolError
                    || request.result == UnityWebRequest.Result.DataProcessingError
                )
                {
                    s_CheckComplete = true;
                    yield break;
                }

                ProcessResponse(request.downloadHandler.text);
            }

            s_CheckComplete = true;
        }

        private static void ProcessResponse(string json)
        {
            string tagName = ExtractJsonValue(json, "tag_name");
            if (string.IsNullOrEmpty(tagName))
            {
                return;
            }

            string version = tagName.TrimStart('v');
            string htmlUrl = ExtractJsonValue(json, "html_url");

            try
            {
                System.Version current = System.Version.Parse(PackageVersion());
                System.Version latest = System.Version.Parse(version);

                if (latest > current)
                {
                    s_IsUpdateAvailable = true;
                    s_LatestVersion = version;
                    s_ReleaseUrl = htmlUrl;
                }
            }
            catch
            {
                // Version parsing failed; silently skip.
            }

            SaveToCache(version, htmlUrl);
        }

        private static void SaveToCache(string version, string url)
        {
            EditorPrefs.SetString(k_LatestVersionKey, version);
            EditorPrefs.SetString(k_ReleaseUrlKey, url ?? string.Empty);
            EditorPrefs.SetString(k_LastCheckTimeKey, DateTime.UtcNow.Ticks.ToString());
        }

        private static string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(UniThumbUpdateChecker).Assembly
            );
            return info?.version ?? "0.0.0";
        }

        private static string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\"";
            int keyIndex = json.IndexOf(search, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return null;
            }

            int colonIndex = json.IndexOf(':', keyIndex + search.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            int quoteStart = json.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0)
            {
                return null;
            }

            int quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0)
            {
                return null;
            }

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        #endregion
    }
}
