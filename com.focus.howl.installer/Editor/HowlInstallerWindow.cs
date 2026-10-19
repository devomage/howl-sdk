using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Focus.Howl.Installer
{
    [InitializeOnLoad]
    static class HowlInstallerAutoOpen
    {
        static HowlInstallerAutoOpen()
        {
            if (SessionState.GetBool(HowlInstallerWindow.ShownKey, false)) return;
            SessionState.SetBool(HowlInstallerWindow.ShownKey, true);
            EditorApplication.delayCall += () =>
            {
                if (!HowlInstallerWindow.IsCoreInstalled()) HowlInstallerWindow.Open();
            };
        }
    }

    // Bootstrapper only: installs Core, then the Howl SDK Manager (inside Core) owns modules and updates.
    // Must not reference Core, since it runs before Core exists.
    public sealed class HowlInstallerWindow : EditorWindow
    {
        internal const string ShownKey = "Howl.Installer.Shown";
        const string RepoUrl = "https://github.com/devomage/howl-sdk.git";
        const string VersionsUrl = "https://raw.githubusercontent.com/devomage/howl-sdk/main/versions.json";
        const string FallbackVersion = "0.1.1";
        const string CoreId = "com.focus.howl";
        const string InstallerId = "com.focus.howl.installer";
        const string UccId = "com.opsive.ultimatecharactercontroller";

        [Serializable]
        class Versions { public string latest; }

        string gitVersion;
        bool uccPresent;
        string latestVersion;
        bool versionChecked;
        [NonSerialized] UnityWebRequest versionRequest;
        // Not serialized: a request restored after domain reload is an empty shell that reports Failure with no Error.
        [NonSerialized] AddAndRemoveRequest installRequest;
        string error;

        [MenuItem("Tools/Howl/Installer")]
        public static void Open()
        {
            var window = GetWindow<HowlInstallerWindow>(true, "Howl SDK Installer");
            window.minSize = new Vector2(440, 240);
        }

        internal static bool IsCoreInstalled() => Find(CoreId) != null;

        static PackageInfo Find(string id)
        {
            foreach (var p in PackageInfo.GetAllRegisteredPackages())
                if (p.name == id) return p;
            return null;
        }

        void OnEnable()
        {
            gitVersion = GetGitVersion();
            uccPresent = Find(UccId) != null;
            versionRequest = UnityWebRequest.Get(VersionsUrl);
            versionRequest.timeout = 10;
            versionRequest.SendWebRequest();
            EditorApplication.update += Poll;
        }

        void OnDisable()
        {
            EditorApplication.update -= Poll;
            versionRequest?.Dispose();
            versionRequest = null;
        }

        void Poll()
        {
            if (versionRequest != null && versionRequest.isDone)
            {
                if (versionRequest.result == UnityWebRequest.Result.Success)
                {
                    try { latestVersion = JsonUtility.FromJson<Versions>(versionRequest.downloadHandler.text).latest; }
                    catch (ArgumentException) { }
                }
                versionRequest.Dispose();
                versionRequest = null;
                versionChecked = true;
                Repaint();
            }

            if (installRequest != null && installRequest.IsCompleted)
            {
                if (installRequest.Status == StatusCode.Failure) error = installRequest.Error?.message ?? "Package Manager request failed.";
                installRequest = null;
                Repaint();
            }
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Howl SDK Installer", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Installs the Howl SDK core package. The Howl SDK Manager takes over from there: integrations, samples and updates.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            bool unityOk = IsUnitySupported();
            bool gitOk = gitVersion != null;
            string version = string.IsNullOrEmpty(latestVersion) ? FallbackVersion : latestVersion;

            Row(unityOk, "Unity", unityOk ? Application.unityVersion : Application.unityVersion + ": requires Unity 6000.0 or newer");
            Row(gitOk, "Git", gitOk ? gitVersion : "Not found on PATH. Package Manager needs git for git URL packages.");
            Row(true, "Opsive UCC", uccPresent ? "Detected. The Opsive integration is available in the Manager." : "Not detected (optional)");
            Row(true, "Howl SDK", !versionChecked ? "Checking latest version..." : "v" + version + (string.IsNullOrEmpty(latestVersion) ? " (offline fallback)" : ""));
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);

            if (IsCoreInstalled())
            {
                EditorGUILayout.HelpBox("Howl SDK is installed.", MessageType.Info);
                if (GUILayout.Button("Open Howl SDK Manager")) EditorApplication.ExecuteMenuItem("Tools/Howl/SDK Manager");
                return;
            }

            using (new EditorGUI.DisabledScope(!unityOk || !gitOk || !versionChecked || installRequest != null))
            {
                if (GUILayout.Button(installRequest != null ? "Installing..." : "Install Howl SDK v" + version, GUILayout.Height(32)))
                    Install(version);
            }
        }

        void Install(string version)
        {
            error = null;
            string coreUrl = RepoUrl + "?path=/" + CoreId + "#v" + version;
            // A package-installed bootstrapper removes itself in the same resolve. An Assets copy (.unitypackage)
            // is deleted by Core on first run.
            string[] remove = PackageInfo.FindForAssembly(typeof(HowlInstallerWindow).Assembly) != null ? new[] { InstallerId } : null;
            installRequest = Client.AddAndRemove(new[] { coreUrl }, remove);
        }

        static void Row(bool ok, string label, string detail)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prev = GUI.color;
                GUI.color = ok ? new Color(0.45f, 0.85f, 0.45f) : new Color(0.95f, 0.4f, 0.4f);
                GUILayout.Label(ok ? "✓" : "✗", GUILayout.Width(16));
                GUI.color = prev;
                EditorGUILayout.LabelField(label, GUILayout.Width(90));
                EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
            }
        }

        static bool IsUnitySupported()
        {
#if UNITY_6000_0_OR_NEWER
            return true;
#else
            return false;
#endif
        }

        static string GetGitVersion()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);
                return process.ExitCode == 0 ? output : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
