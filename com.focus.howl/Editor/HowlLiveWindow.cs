using System.Text;
using UnityEditor;
using UnityEngine;

namespace Focus.Howl.Editor
{
    /// <summary>
    /// Live view of the emitter during Play Mode: recent events, per-event counts and info keys.
    /// Text is rebuilt at most 4x per second and drawn as one label, so the window stays cheap under stress.
    /// </summary>
    public sealed class HowlLiveWindow : EditorWindow
    {
        // Editor-only and drawn on the main thread, so keep it light; close it for perf runs.
        const double RefreshInterval = 0.5;
        const int MaxEventRows = 60;

        readonly HowlEvent[] m_Recent = new HowlEvent[Howl.RecentCapacity];
        readonly StringBuilder m_Sb = new StringBuilder(16 * 1024);
        int m_Tab;
        Vector2 m_Scroll;
        double m_NextRefresh;
        string m_Filter = "";
        string m_Text = "";
        GUIStyle m_TextStyle;

        static readonly string[] Tabs = { "Events", "Counts", "Info" };

        [MenuItem("Tools/Howl/Live View")]
        public static void Open() => GetWindow<HowlLiveWindow>("Howl Live");

        void OnEnable() => Refresh();

        void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < m_NextRefresh) return;
            Refresh();
            Repaint();
        }

        void Refresh()
        {
            m_NextRefresh = EditorApplication.timeSinceStartup + RefreshInterval;
            m_Text = Build(m_Tab, MaxEventRows);
        }

        void OnGUI()
        {
            if (m_TextStyle == null)
                m_TextStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, richText = false, alignment = TextAnchor.UpperLeft };

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                int tab = GUILayout.Toolbar(m_Tab, Tabs, EditorStyles.toolbarButton, GUILayout.Width(210));
                if (GUILayout.Button(new GUIContent("Clear", "Clear recent events and counts"), EditorStyles.toolbarButton))
                {
                    Howl.ClearHistory();
                    Refresh();
                }
                if (GUILayout.Button(new GUIContent("Copy", "Copy this tab (filtered) to the clipboard"), EditorStyles.toolbarButton)) Copy(Build(m_Tab, int.MaxValue));
                if (GUILayout.Button(new GUIContent("Copy All", "Copy all tabs (filtered) to the clipboard"), EditorStyles.toolbarButton)) Copy(BuildAll());
                GUILayout.FlexibleSpace();
                string filter = EditorGUILayout.TextField(m_Filter, EditorStyles.toolbarSearchField, GUILayout.Width(180));
                GUILayout.Label("Total: " + Howl.TotalEmitted, EditorStyles.miniLabel);
                Howl.Active = GUILayout.Toggle(Howl.Active, "Active", EditorStyles.toolbarButton);

                if (tab != m_Tab || filter != m_Filter)
                {
                    m_Tab = tab;
                    m_Filter = filter;
                    Refresh();
                }
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode to see events.", MessageType.Info);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            GUILayout.Label(m_Text, m_TextStyle);
            EditorGUILayout.EndScrollView();
        }

        string BuildAll() =>
            "# Events (newest first)\n" + Build(0, int.MaxValue) + "\n# Counts\n" + Build(1, int.MaxValue) + "\n# Info\n" + Build(2, int.MaxValue);

        string Build(int tab, int maxRows)
        {
            var sb = m_Sb;
            sb.Clear();
            int rows = 0;
            switch (tab)
            {
                case 0:
                    int count = Howl.CopyRecent(m_Recent);
                    for (int i = 0; i < count && rows < maxRows; i++)
                    {
                        string line = Howl.FormatEvent(m_Recent[i]);
                        if (!Matches(line)) continue;
                        sb.Append(m_Recent[i].Time.ToString("0.000")).Append('\t').AppendLine(line);
                        rows++;
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
