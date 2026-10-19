using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Focus.Howl.Tools
{
    /// <summary>
    /// Records per-frame cost (frame time, main thread, GC allocated, Howl.Emit/Set time) into preallocated arrays and
    /// writes a CSV plus a one-line run summary for A/B comparison. F10 toggles the HUD, F11 starts/stops a run.
    /// Created automatically in Play Mode; the HUD starts hidden.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Howl/Perf Recorder")]
    public sealed class HowlPerfRecorder : MonoBehaviour
    {
        // Capacity per second of run; a fast build can exceed 1000 fps, and a run that fills up stops early.
        const int MaxFps = 2000;
        const int ManualRunSeconds = 1200;
        const int WarmupFrames = 30;
        const int WindowFrames = 120;
        const float HudInterval = 0.5f;

        public static HowlPerfRecorder Instance { get; private set; }

        [Tooltip("Length of a run started with F11, in seconds. 0 records until F11 again (max 20 minutes).")]
        public float RunSeconds = 600f;
        [Tooltip("Free text stored with each run, e.g. howl=on stress=off.")]
        public string Label = "";
        [NonSerialized] public string Status = "";

        public event Action RunStarted;
        public event Action<string> RunFinished;

        public bool IsRecording => m_Recording;
        public bool HudVisible => m_HudRoot != null && m_HudRoot.activeSelf;

        /// <summary>Wrap a frame's batch of Howl calls in this marker to have its cost recorded as howl_us.</summary>
        public static readonly ProfilerMarker CallsMarker = new ProfilerMarker("Howl.Calls");

        ProfilerRecorder m_MainThread, m_GcAlloc, m_Calls;

        float[] m_FrameMs, m_MainMs, m_HowlUs;
        int[] m_GcBytes;
        int m_Count, m_Skip;
        bool m_Recording;
        double m_StartTime;
        float m_Duration;
        int m_Collections0;
        long m_Events0;

        readonly float[] m_WinFrame = new float[WindowFrames];
        readonly float[] m_WinMain = new float[WindowFrames];
        readonly float[] m_WinHowl = new float[WindowFrames];
        readonly int[] m_WinGc = new int[WindowFrames];
        int m_WinIndex, m_WinCount;

        GameObject m_HudRoot;
        Text m_HudText;
        readonly StringBuilder m_Sb = new StringBuilder(512);
        float m_NextHud;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate() => Ensure();

        public static HowlPerfRecorder Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[Howl Perf]");
            DontDestroyOnLoad(go);
            return go.AddComponent<HowlPerfRecorder>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void OnEnable()
        {
            m_MainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            m_GcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            m_Calls = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Howl.Calls", 1);
        }

        void OnDisable()
        {
            if (m_Recording) Stop();
            m_MainThread.Dispose();
            m_GcAlloc.Dispose();
            m_Calls.Dispose();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void SetHud(bool visible)
        {
            if (visible && m_HudRoot == null) BuildHud();
            if (m_HudRoot != null) m_HudRoot.SetActive(visible);
            m_NextHud = 0f;
        }

        public void Begin()
        {
            if (m_Recording) return;
            float seconds = RunSeconds > 0f ? RunSeconds : ManualRunSeconds;
            int capacity = Mathf.CeilToInt(seconds * MaxFps) + 1;
            if (m_FrameMs == null || m_FrameMs.Length < capacity)
            {
                m_FrameMs = new float[capacity];
                m_MainMs = new float[capacity];
                m_HowlUs = new float[capacity];
                m_GcBytes = new int[capacity];
            }
            m_Count = 0;
            m_Skip = WarmupFrames;
            m_Duration = RunSeconds;
            m_Recording = true;
            RunStarted?.Invoke();
        }

        public void Stop()
        {
            if (!m_Recording) return;
            m_Recording = false;
            string path = m_Count > 0 ? Write() : null;
            RunFinished?.Invoke(path);
        }

        void Update()
        {
            if (HowlKeys.Pressed(KeyCode.F10)) SetHud(!HudVisible);
            if (HowlKeys.Pressed(KeyCode.F11))
            {
                if (m_Recording) Stop();
                else Begin();
            }

            // Profiler recorders report the previous frame, which is the last complete one.
            float frameMs = Time.unscaledDeltaTime * 1000f;
            float mainMs = m_MainThread.Valid && m_MainThread.LastValue > 0 ? m_MainThread.LastValue * 1e-6f : frameMs;
            int gcBytes = m_GcAlloc.Valid ? (int)m_GcAlloc.LastValue : 0;
            float howlUs = (m_Calls.Valid ? m_Calls.LastValue : 0) * 1e-3f;

            m_WinFrame[m_WinIndex] = frameMs;
            m_WinMain[m_WinIndex] = mainMs;
            m_WinHowl[m_WinIndex] = howlUs;
            m_WinGc[m_WinIndex] = gcBytes;
            m_WinIndex = (m_WinIndex + 1) % WindowFrames;
            if (m_WinCount < WindowFrames) m_WinCount++;

            if (m_Recording) Record(frameMs, mainMs, gcBytes, howlUs);

            if (HudVisible && Time.unscaledTime >= m_NextHud)
            {
                m_NextHud = Time.unscaledTime + HudInterval;
                UpdateHud();
            }
        }

        void Record(float frameMs, float mainMs, int gcBytes, float howlUs)
        {
            if (m_Skip > 0)
            {
                // Skip the frames around Begin (array allocation, world reset) so they don't pollute the run.
                if (--m_Skip == 0)
                {
                    m_StartTime = Time.realtimeSinceStartupAsDouble;
                    m_Collections0 = GC.CollectionCount(0);
                    m_Events0 = Howl.TotalEmitted;
                }
                return;
            }

            m_FrameMs[m_Count] = frameMs;
            m_MainMs[m_Count] = mainMs;
            m_GcBytes[m_Count] = gcBytes;
            m_HowlUs[m_Count] = howlUs;
            m_Count++;

            bool timeUp = m_Duration > 0f && Time.realtimeSinceStartupAsDouble - m_StartTime >= m_Duration;
            if (timeUp || m_Count >= m_FrameMs.Length) Stop();
        }

        string Write()
        {
            var inv = CultureInfo.InvariantCulture;
            string dir = Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PerfCaptures"))
                : Path.Combine(Application.persistentDataPath, "PerfCaptures");
            Directory.CreateDirectory(dir);

            string scene = SceneManager.GetActiveScene().name;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", inv);
            string path = Path.Combine(dir, scene + "_" + stamp + ".csv");

            using (var w = new StreamWriter(path))
            {
                w.WriteLine("frame,frame_ms,main_ms,gc_bytes,howl_us");
                for (int i = 0; i < m_Count; i++)
                    w.WriteLine(string.Format(inv, "{0},{1:0.###},{2:0.###},{3},{4:0.###}", i, m_FrameMs[i], m_MainMs[i], m_GcBytes[i], m_HowlUs[i]));
            }

            double seconds = Time.realtimeSinceStartupAsDouble - m_StartTime;
            var frame = Stats(m_FrameMs, m_Count);
            var main = Stats(m_MainMs, m_Count);
            var howl = Stats(m_HowlUs, m_Count);
            long gcTotal = 0;
            int gcFrames = 0;
            for (int i = 0; i < m_Count; i++)
            {
                gcTotal += m_GcBytes[i];
                if (m_GcBytes[i] > 0) gcFrames++;
            }
            int collections = GC.CollectionCount(0) - m_Collections0;
            long events = Howl.TotalEmitted - m_Events0;

            string summaryPath = Path.Combine(dir, "summary.csv");
            bool newFile = !File.Exists(summaryPath);
            using (var w = new StreamWriter(summaryPath, true))
            {
                if (newFile)
                    w.WriteLine("time,scene,label,frames,seconds,frame_avg_ms,frame_p99_ms,frame_max_ms,main_avg_ms,main_p99_ms,gc_total_bytes,gc_frames,gc_collections,howl_avg_us,howl_p99_us,howl_max_us,events,capture");
                w.WriteLine(string.Format(inv,
                    "{0},{1},\"{2}\",{3},{4:0.0},{5:0.###},{6:0.###},{7:0.###},{8:0.###},{9:0.###},{10},{11},{12},{13:0.###},{14:0.###},{15:0.###},{16},{17}",
                    stamp, scene, Label, m_Count, seconds, frame.Avg, frame.P99, frame.Max, main.Avg, main.P99,
                    gcTotal, gcFrames, collections, howl.Avg, howl.P99, howl.Max, events, Path.GetFileName(path)));
            }

            Debug.Log(string.Format(inv,
                "[Howl Perf] {0} frames / {1:0.0}s  frame avg {2:0.00} p99 {3:0.00} max {4:0.00} ms | main avg {5:0.00} p99 {6:0.00} ms | GC {7} B over {8} frames, {9} collections | Howl avg {10:0.0} p99 {11:0.0} max {12:0.0} us, {13} events [{14}]\n{15}",
                m_Count, seconds, frame.Avg, frame.P99, frame.Max, main.Avg, main.P99, gcTotal, gcFrames, collections,
                howl.Avg, howl.P99, howl.Max, events, Label, path));
            return path;
        }

        struct Summary { public float Avg, P99, Max; }

        static Summary Stats(float[] data, int count)
        {
            var s = new Summary();
            if (count == 0) return s;
            var sorted = new float[count];
            Array.Copy(data, sorted, count);
            Array.Sort(sorted);
            double sum = 0;
            for (int i = 0; i < count; i++) sum += sorted[i];
            s.Avg = (float)(sum / count);
            s.P99 = sorted[Mathf.Clamp(Mathf.CeilToInt(count * 0.99f) - 1, 0, count - 1)];
            s.Max = sorted[count - 1];
            return s;
        }

        void BuildHud()
        {
            m_HudRoot = new GameObject("Howl HUD", typeof(Canvas));
            m_HudRoot.transform.SetParent(transform, false);
            var canvas = m_HudRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Shadow));
            textGo.transform.SetParent(m_HudRoot.transform, false);
            var rect = (RectTransform)textGo.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-12f, -12f);
            rect.sizeDelta = new Vector2(560f, 400f);

            m_HudText = textGo.GetComponent<Text>();
            m_HudText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_HudText.fontSize = 14;
            m_HudText.alignment = TextAnchor.UpperRight;
            m_HudText.color = Color.white;
            m_HudText.raycastTarget = false;
        }

        // Runs at 2 Hz; the string it builds is the only allocation the recorder makes outside Begin/Stop.
        void UpdateHud()
        {
            float frameSum = 0f, frameMax = 0f, mainSum = 0f, howlSum = 0f, howlMax = 0f;
            long gcSum = 0;
            for (int i = 0; i < m_WinCount; i++)
            {
                frameSum += m_WinFrame[i];
                frameMax = Mathf.Max(frameMax, m_WinFrame[i]);
                mainSum += m_WinMain[i];
                howlSum += m_WinHowl[i];
                howlMax = Mathf.Max(howlMax, m_WinHowl[i]);
                gcSum += m_WinGc[i];
            }
            float n = Mathf.Max(1, m_WinCount);
            float frameAvg = frameSum / n;
            var inv = CultureInfo.InvariantCulture;

            m_Sb.Clear();
            m_Sb.Append("Howl Perf   [F10 hide]\n");
            m_Sb.Append(string.Format(inv, "frame  avg {0:0.00} ms   max {1:0.00} ms   ({2:0} fps)\n", frameAvg, frameMax, 1000f / Mathf.Max(0.001f, frameAvg)));
            m_Sb.Append(string.Format(inv, "main thread  avg {0:0.00} ms\n", mainSum / n));
            m_Sb.Append(string.Format(inv, "GC  {0:0} B/frame avg\n", gcSum / n));
            m_Sb.Append(string.Format(inv, "Howl  avg {0:0.0} us   max {1:0.0} us   events {2}{3}\n", howlSum / n, howlMax, Howl.TotalEmitted,
                !Howl.Active ? "   (inactive)" : Howl.IsLive ? "" : "   (dormant: no listener)"));
            var stats = Howl.Stats;
            if (stats != null)
                m_Sb.Append(string.Format(inv, "bridge  ws {0}  file {1}  {2:0} ev/s  {3:0} KB/s  ser {4:0} us  send {5:0} us  ring {6}/{7}  dropped {8}/{9}b\n",
                    stats.WebSocket, stats.File, stats.EventsPerSecond, stats.BytesPerSecond / 1024f, stats.SerializeUsAvg, stats.SendUsAvg,
                    stats.RingHighWater, stats.RingCapacity, stats.DroppedEvents, stats.DroppedBatches));
            if (m_Recording)
            {
                double elapsed = m_Skip > 0 ? 0 : Time.realtimeSinceStartupAsDouble - m_StartTime;
                m_Sb.Append(string.Format(inv, "REC {0:00}:{1:00}", (int)(elapsed / 60), (int)(elapsed % 60)));
                if (m_Duration > 0f) m_Sb.Append(string.Format(inv, " / {0:00}:{1:00}", (int)(m_Duration / 60), (int)(m_Duration % 60)));
                m_Sb.Append("   [F11 stop]\n");
            }
            else
            {
                m_Sb.Append(RunSeconds > 0f
                    ? string.Format(inv, "[F11 record {0:00}:{1:00} run]\n", (int)(RunSeconds / 60), (int)(RunSeconds % 60))
                    : "[F11 record]\n");
            }
            if (!string.IsNullOrEmpty(Label)) m_Sb.Append(Label).Append('\n');
            if (!string.IsNullOrEmpty(Status)) m_Sb.Append(Status);
            m_HudText.text = m_Sb.ToString();
        }
    }
}
