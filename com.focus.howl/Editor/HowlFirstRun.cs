using UnityEditor;
using UnityEditor.PackageManager;

namespace Focus.Howl.Editor
{
    [InitializeOnLoad]
    static class HowlFirstRun
    {
        static HowlFirstRun()
        {
            EditorApplication.delayCall += () =>
            {
                string key = "Howl.FirstRun." + PlayerSettings.productGUID;
                if (EditorPrefs.GetBool(key, false)) return;
                EditorPrefs.SetBool(key, true);
                CleanUpInstaller();
                HowlSdkManagerWindow.Open();
            };
        }

        // A package-installed bootstrapper normally removes itself during install. An Assets copy
        // (.unitypackage) can't, so it's removed here.
        static void CleanUpInstaller()
        {
            if (HowlPackages.Find(HowlPackages.InstallerId) != null)
                Client.Remove(HowlPackages.InstallerId);
            if (AssetDatabase.IsValidFolder(HowlPackages.InstallerAssetsPath))
                AssetDatabase.DeleteAsset(HowlPackages.InstallerAssetsPath);
        }
    }
}
