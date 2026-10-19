using System;
using System.IO;
using UnityEngine;

namespace Focus.Howl
{
    /// <summary>
    /// Bridge settings. Loaded from Resources/HowlSettings when present, defaults otherwise.
    /// Command-line overrides: -howl-ws &lt;url&gt;, -howl-no-ws, -howl-file, -howl-file-dir &lt;dir&gt;, -howl-tick &lt;ms&gt;, -howl-off.
    /// </summary>
    [CreateAssetMenu(menuName = "Howl/Settings", fileName = "HowlSettings")]
    public sealed class HowlSettings : ScriptableObject
    {
        public const string ResourcePath = "HowlSettings";

        [Tooltip("Start the worker before the first scene loads.")]
        public bool AutoStart = true;

        [Header("WebSocket sink (game is the client)")]
        public bool WebSocket = true;
        public string WebSocketUrl = "ws://127.0.0.1:7777";

        [Header("File sink (JSONL)")]
        public bool FileLog;
        [Tooltip("Empty: <project>/HowlLogs in the Editor, <persistentDataPath>/HowlLogs in players.")]
        public string FileDirectory = "";

        [Header("Worker")]
        [Range(1, 50)] public int TickMs = 8;
        [Tooltip("Events the ring holds before dropping (rounded up to a power of two).")]
        public int RingCapacity = 16384;

        /// <summary>Resolved on the main thread at load; the worker can't touch Application.*.</summary>
        [NonSerialized] public string ResolvedFileDirectory;

        public static HowlSettings Load()
        {
            var asset = Resources.Load<HowlSettings>(ResourcePath);
            var settings = asset != null ? Instantiate(asset) : CreateInstance<HowlSettings>();
            settings.ApplyCommandLine(Environment.GetCommandLineArgs());
            settings.ResolvedFileDirectory = !string.IsNullOrEmpty(settings.FileDirectory)
                ? settings.FileDirectory
                : Application.isEditor
                    ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HowlLogs"))
                    : Path.Combine(Application.persistentDataPath, "HowlLogs");
            return settings;
        }

        void ApplyCommandLine(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "-howl-ws": if (next != null) { WebSocket = true; WebSocketUrl = next; i++; } break;
                    case "-howl-no-ws": WebSocket = false; break;
                    case "-howl-file": FileLog = true; break;
                    case "-howl-file-dir": if (next != null) { FileDirectory = next; i++; } break;
                    case "-howl-tick": if (next != null && int.TryParse(next, out int ms)) { TickMs = Mathf.Clamp(ms, 1, 50); i++; } break;
                    case "-howl-off": AutoStart = false; break;
                }
            }
        }
    }
}
