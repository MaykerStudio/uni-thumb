using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Checks for UniThumb updates via the GitHub Releases API on domain reload.
    /// Caches the result for 24 hours in EditorPrefs. All errors are silently
    /// caught to avoid console noise.
    /// </summary>
    [InitializeOnLoad]
    public static class UniThumbUpdateChecker
    {
        #region Constants

        private const string k_GitHubApiUrl =
            "https://api.github.com/repos/MaykerStudio/uni-thumb/releases/latest";
        private const string k_LastCheckTimeKey = "UniThumb.LastCheckTime";
        private const string k_LatestVersionKey = "UniThumb.LatestVersion";
        private const string k_ReleaseUrlKey = "UniThumb.ReleaseUrl";
        private const double k_CheckIntervalHours = 24;

        #endregion

        #region Fields

        private static bool s_IsUpdateAvailable;
        private static string s_LatestVersion;
        private static string s_ReleaseUrl;
        private static bool s_CheckComplete;
        private static IEnumerator s_Coroutine;

        #endregion

        #region Properties

        public static bool IsUpdateAvailable => s_IsUpdateAvailable;

        public static string LatestVersion => s_LatestVersion;

        public static string ReleaseUrl => s_ReleaseUrl;

        public static bool IsCheckComplete => s_CheckComplete;

        #endregion

        #region Initialization

        static UniThumbUpdateChecker()
        {
            CheckForUpdate();
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

        #endregion

        #region Private Methods

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

        private static bool IsCacheValid()
        {
            if (!EditorPrefs.HasKey(k_LastCheckTimeKey))
            {
                return false;
            }

            long lastTicks = long.Parse(EditorPrefs.GetString(k_LastCheckTimeKey, "0"));
            DateTime lastCheck = new DateTime(lastTicks, DateTimeKind.Utc);
            TimeSpan elapsed = DateTime.UtcNow - lastCheck;
            return elapsed.TotalHours < 24;
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
