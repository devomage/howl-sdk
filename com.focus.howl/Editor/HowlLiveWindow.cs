using System.Text;
using UnityEditor;
using UnityEngine;

namespace Focus.Howl.Editor
{
    /// <summary>Live view of the emitter during Play Mode: recent events, per-event counts and info keys.</summary>
    public sealed class HowlLiveWindow : EditorWindow
    {
        const double RepaintInterval = 0.1;

        readonly HowlEvent[] m_Recent = new HowlEvent[Howl.RecentCapacity];
        int m_Tab;
        Vector2 m_Scroll;
        double m_NextRepaint;
        string m_Filter = "";

        static readonly string[] Tabs = { "Events", "Counts", "Info" };

        [MenuItem("Tools/Howl/Live View")]
        public static void Open() => GetWindow<HowlLiveWindow>("Howl Live");

        void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < m_NextRepaint) return;
            m_NextRepaint = EditorApplication.timeSinceStartup + RepaintInterval;
            Repaint();
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                m_Tab = GUILayout.Toolbar(m_Tab, Tabs, EditorStyles.toolbarButton, GUILayout.Width(210));
                if (GUILayout.Button(new GUIContent("Clear", "Clear recent events and counts"), EditorStyles.toolbarButton)) Howl.ClearHistory();
                if (GUILayout.Button(new GUIContent("Copy", "Copy this tab (filtered) to the clipboard"), EditorStyles.toolbarButton)) Copy(BuildTab(m_Tab));
                if (GUILayout.Button(new GUIContent("Copy All", "Copy all tabs (filtered) to the clipboard"), EditorStyles.toolbarButton)) Copy(BuildAll());
                GUILayout.FlexibleSpace();
                m_Filter = EditorGUILayout.TextField(m_Filter, EditorStyles.toolbarSearchField, GUILayout.Width(180));
                GUILayout.Label("Total: " + Howl.TotalEmitted, EditorStyles.miniLabel);
                Howl.Active = GUILayout.Toggle(Howl.Active, "Active", EditorStyles.toolbarButton);
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode to see events.", MessageType.Info);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            switch (m_Tab)
            {
                case 0: DrawEvents(); break;
                case 1: DrawCounts(); break;
                case 2: DrawInfo(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawEvents()
        {
            int count = Howl.CopyRecent(m_Recent);
            for (int i = 0; i < count; i++)
            {
                string line = Howl.FormatEvent(m_Recent[i]);
                if (!Matches(line)) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(m_Recent[i].Time.ToString("0.000"), EditorStyles.miniLabel, GUILayout.Width(70));
                    GUILayout.Label(line, EditorStyles.label);
                }
            }
        }

        void DrawCounts()
        {
            var events = Howl.Events;
            for (ushort id = 1; id < events.Count; id++)
            {
                if (!Matches(events[id].Name)) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(events[id].Name, GUILayout.Width(180));
                    GUILayout.Label(Howl.GetEventCount(id).ToString(), EditorStyles.boldLabel);
                }
            }
        }

        void DrawInfo()
        {
            var keys = Howl.Keys;
            for (ushort id = 1; id < keys.Count; id++)
            {
                string path = keys[id].Category + "." + keys[id].Key;
                if (!Matches(path)) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(path, GUILayout.Width(260));
                    GUILayout.Label(Howl.FormatValue(Howl.GetValue(id)), GUILayout.Width(180));
                    GUILayout.Label("v" + Howl.GetVersion(id), EditorStyles.miniLabel);
                }
            }
        }

        string BuildAll() =>
            "# Events (newest first)\n" + BuildTab(0) + "\n# Counts\n" + BuildTab(1) + "\n# Info\n" + BuildTab(2);

        string BuildTab(int tab)
        {
            var sb = new StringBuilder();
            switch (tab)
            {
                case 0:
                    int count = Howl.CopyRecent(m_Recent);
                    for (int i = 0; i < count; i++)
                    {
                        string line = Howl.FormatEvent(m_Recent[i]);
                        if (Matches(line)) sb.Append(m_Recent[i].Time.ToString("0.000")).Append('\t').AppendLine(line);
                    }
                    break;
                case 1:
                    var events = Howl.Events;
                    for (ushort id = 1; id < events.Count; id++)
                        if (Matches(events[id].Name)) sb.Append(events[id].Name).Append('\t').Append(Howl.GetEventCount(id)).AppendLine();
                    break;
                case 2:
                    var keys = Howl.Keys;
                    for (ushort id = 1; id < keys.Count; id++)
                    {
                        string path = keys[id].Category + "." + keys[id].Key;
                        if (Matches(path)) sb.Append(path).Append('\t').Append(Howl.FormatValue(Howl.GetValue(id))).Append('\t').Append('v').Append(Howl.GetVersion(id)).AppendLine();
                    }
                    break;
            }
            return sb.ToString();
        }

        void Copy(string text)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            ShowNotification(new GUIContent("Copied " + text.Split('\n').Length + " lines"), 1.0);
        }

        bool Matches(string text) =>
            string.IsNullOrEmpty(m_Filter) || text.IndexOf(m_Filter, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
