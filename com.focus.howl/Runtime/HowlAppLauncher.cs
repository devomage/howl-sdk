using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Focus.Howl
{
    /// <summary>
    /// Opens the Howl app through the howl:// protocol when no app has connected shortly after start
    /// (auto-launch needs an Overwolf game ID; until then the game offers the launch itself).
    /// Windows only, once per session, and only when the protocol is registered, so a machine without the app
    /// never sees a "pick an app" dialog.
    /// </summary>
    static class HowlAppLauncher
    {
        public const string Scheme = "howl";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Launched = false;

        static bool s_Launched;

        public static void Schedule(HowlSettings settings)
        {
            if (s_Launched || !settings.LaunchApp || !settings.WebSocket || !IsProtocolRegistered()) return;
            var runner = new GameObject("[Howl Launcher]") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(runner);
            var launcher = runner.AddComponent<Runner>();
            launcher.Url = BuildUrl(settings);
            launcher.LaunchAt = Time.realtimeSinceStartup + settings.LaunchDelay;
        }

        static string BuildUrl(HowlSettings settings)
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            return Scheme + "://launch?ws=" + Uri.EscapeDataString(settings.WebSocketUrl) +
                   "&game=" + Uri.EscapeDataString(Application.productName) +
                   "&pid=" + pid;
        }

        sealed class Runner : MonoBehaviour
        {
            public string Url;
            public float LaunchAt;

            void Update()
            {
                if (Howl.AppConnected || s_Launched)
                {
                    Destroy(gameObject);
                    return;
                }
                if (Time.realtimeSinceStartup < LaunchAt) return;
                s_Launched = true;
                Application.OpenURL(Url);
                Destroy(gameObject);
            }
        }

        public static bool IsProtocolRegistered()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                // HKEY_CLASSES_ROOT merges per-user (HKCU\Software\Classes) and machine registrations.
                if (RegOpenKeyEx(HkeyClassesRoot, Scheme + "\\shell\\open\\command", 0, KeyRead, out var key) != 0) return false;
                RegCloseKey(key);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
#else
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        static readonly UIntPtr HkeyClassesRoot = new UIntPtr(0x80000000u);
        const int KeyRead = 0x20019;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out UIntPtr result);

        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(UIntPtr hKey);
#endif
    }
}
