using System;
using UnityEditor.PackageManager;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Resolves the installed UniThumb package folder so UI assets (UXML/USS)
    /// load regardless of install method (embedded folder or tarball cache).
    /// The project-relative package path is computed once via PackageInfo;
    /// AssetDatabase.LoadAssetAtPath requires this "Packages/..." form.
    /// </summary>
    internal static class UniThumbPackagePaths
    {
        private const string k_EditorSubfolder = "/Editor";

        /// <summary>
        /// Lazy one-shot resolution holder: mutable static strings are flagged by
        /// the Asset Store Validator ("Check Static Variables"), so the resolved
        /// path lives behind a static readonly Lazy. Resolution still happens on
        /// first access (EnsureResolved / PackageAssetPath), never in a static
        /// initializer.
        /// </summary>
        private static readonly Lazy<string> s_PackageAssetPath = new Lazy<string>(Resolve);

        /// <summary>
        /// Forces the package path to resolve now. Must be called from OnEnable
        /// (never from a ScriptableObject constructor, static initializer, or
        /// CreateGUI during window restoration): PackageInfo.FindForAssembly
        /// internally calls GetPackageByAssetPath, which Unity only permits
        /// outside ScriptableObject construction.
        /// </summary>
        public static void EnsureResolved()
        {
            _ = PackageAssetPath;
        }

        /// <summary>
        /// Project-relative package root, e.g. "Packages/com.maykerstudio.unithumb".
        /// Empty when the package cannot be resolved.
        /// </summary>
        public static string PackageAssetPath => s_PackageAssetPath.Value;

        /// <summary>
        /// Project-relative Editor folder inside the package, e.g.
        /// "Packages/com.maykerstudio.unithumb/Editor".
        /// </summary>
        public static string EditorFolderAssetPath => PackageAssetPath + k_EditorSubfolder;

        private static string Resolve()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(UniThumbWindow).Assembly);
            return package != null ? package.assetPath : string.Empty;
        }
    }
}
