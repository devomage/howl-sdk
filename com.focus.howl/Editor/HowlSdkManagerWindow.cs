using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

namespace Focus.Howl.Editor
{
    public sealed class HowlSdkManagerWindow : EditorWindow
    {
        [Serializable]
        class Versions { public string latest; }

        readonly Dictionary<string, bool> desired = new Dictionary<string, bool>();
        string latestVersion;
        UnityWebRequest versionRequest;
        AddAndRemoveRequest request;
        string error;
        Vector2 scroll;

        [MenuItem("Tools/Howl/SDK Manager")]
        public static void Open()
        {
            var window = GetWindow<HowlSdkManagerWindow>("Howl SDK");
            window.minSize = new Vector2(460, 300);
        }

        void OnEnable()
        {
            RefreshDesired();
            FetchLatest();
            EditorApplication.update += Poll;
            Events.registeredPackages += OnRegisteredPackages;
        }

        void OnDisable()
        {
            EditorApplication.update -= Poll;
            Events.registeredPackages -= OnRegisteredPackages;
            versionRequest?.Dispose();
            versionRequest = null;
        }

        void OnRegisteredPackages(PackageRegistrationEventArgs _)
        {
            RefreshDesired();
            Repaint();
        }

        void RefreshDesired()
        {
            foreach (var m in HowlPackages.Modules)
                desired[m.Id] = m.Required || HowlPackages.Find(m.Id) != null;
        }

        void FetchLatest()
        {
            versionRequest?.Dispose();
            versionRequest = UnityWebRequest.Get(HowlPackages.VersionsUrl);
            versionRequest.timeout = 10;
            versionRequest.SendWebRequest();
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
                Repaint();
            }

            if (request != null && request.IsCompleted)
            {
                if (request.Status == StatusCode.Failure) error = request.Error.message;
                request = null;
                RefreshDesired();
                Repaint();
            }
        }

        void OnGUI()
        {
            var core = HowlPackages.Find(HowlPackages.CoreId);
            string coreVersion = core != null ? core.version : "?";
            bool local = core != null && HowlPackages.IsLocal(core);

            EditorGUILayout.LabelField("Howl SDK", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Installed: v" + coreVersion + (local ? " (local)" : ""), GUILayout.Width(200));
                string latest = latestVersion != null ? "v" + latestVersion : versionRequest != null ? "checking..." : "unavailable";
                EditorGUILayout.LabelField("Latest: " + latest);
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                {
                    RefreshDesired();
                    FetchLatest();
                }
            }

            if (!local && latestVersion != null && IsNewer(latestVersion, coreVersion))
            {
                EditorGUILayout.HelpBox("Howl SDK v" + latestVersion + " is available.", MessageType.Info);
                using (new EditorGUI.DisabledScope(request != null))
                {
                    if (GUILayout.Button("Update all modules to v" + latestVersion)) UpdateAll(latestVersion);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var m in HowlPackages.Modules) ModuleRow(m);
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);

            bool dirty = false;
            foreach (var m in HowlPackages.Modules)
                if (desired[m.Id] != (HowlPackages.Find(m.Id) != null)) dirty = true;

            using (new EditorGUI.DisabledScope(!dirty || request != null))
            {
                if (GUILayout.Button(request != null ? "Working..." : "Apply", GUILayout.Height(28))) Apply(coreVersion);
            }
        }

        void ModuleRow(HowlModule m)
        {
            var installed = HowlPackages.Find(m.Id);
            bool prerequisiteMissing = m.PrerequisiteId != null && HowlPackages.Find(m.PrerequisiteId) == null;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(m.Required || (prerequisiteMissing && installed == null) || request != null))
                    {
                        desired[m.Id] = EditorGUILayout.ToggleLeft(m.DisplayName, desired[m.Id], EditorStyles.boldLabel);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(installed != null ? "v" + installed.version : "Not installed", EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField(m.Description, EditorStyles.wordWrappedMiniLabel);

                if (prerequisiteMissing)
                {
                    string message = installed != null
                        ? m.PrerequisiteName + " not found. This module stays inactive until it is installed."
                        : "Requires " + m.PrerequisiteName + ".";
                    EditorGUILayout.HelpBox(message, MessageType.Warning);
                }
            }
        }

        void Apply(string version)
        {
            var add = new List<string>();
            var remove = new List<string>();
            foreach (var m in HowlPackages.Modules)
            {
                bool isInstalled = HowlPackages.Find(m.Id) != null;
                if (desired[m.Id] && !isInstalled) add.Add(HowlPackages.SourceFor(m.Id, version));
                else if (!desired[m.Id] && isInstalled) remove.Add(m.Id);
            }
            Send(add, remove);
        }

        void UpdateAll(string version)
        {
            var add = new List<string>();
            foreach (var m in HowlPackages.Modules)
                if (HowlPackages.Find(m.Id) != null) add.Add(HowlPackages.GitUrl(m.Id, version));
            Send(add, null);
        }

        // One AddAndRemove per action: a single resolve and a single domain reload.
        void Send(List<string> add, List<string> remove)
        {
            error = null;
            request = Client.AddAndRemove(
                add.Count > 0 ? add.ToArray() : null,
                remove != null && remove.Count > 0 ? remove.ToArray() : null);
        }

        static bool IsNewer(string candidate, string current) =>
            Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;
    }
}
