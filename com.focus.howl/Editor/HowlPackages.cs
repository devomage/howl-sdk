using System.IO;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Focus.Howl.Editor
{
    internal sealed class HowlModule
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public bool Required;
        public string PrerequisiteId;
        public string PrerequisiteName;
    }

    internal static class HowlPackages
    {
        public const string RepoUrl = "https://github.com/devomage/howl-sdk.git";
        public const string VersionsUrl = "https://raw.githubusercontent.com/devomage/howl-sdk/main/versions.json";
        public const string CoreId = "com.focus.howl";
        public const string InstallerId = "com.focus.howl.installer";
        public const string InstallerAssetsPath = "Assets/Howl SDK Installer";

        public static readonly HowlModule[] Modules =
        {
            new HowlModule
            {
                Id = CoreId,
                DisplayName = "Core",
                Description = "Emitter, worker thread, transports and this Manager.",
                Required = true
            },
            new HowlModule
            {
                Id = "com.focus.howl.opsive",
                DisplayName = "Opsive UCC Integration",
                Description = "Maps Ultimate Character Controller events to Howl events and info keys.",
                PrerequisiteId = "com.opsive.ultimatecharactercontroller",
                PrerequisiteName = "Opsive Ultimate Character Controller 3.3+"
            },
        };

        public static PackageInfo Find(string id)
        {
            foreach (var p in PackageInfo.GetAllRegisteredPackages())
                if (p.name == id) return p;
            return null;
        }

        public static bool IsLocal(PackageInfo p) => p.source == PackageSource.Local || p.source == PackageSource.Embedded;

        // All modules ship in lockstep from one repo tag, and from the same source as Core:
        // a git Core gets git modules at the same tag, a local dev Core gets the sibling folders.
        public static string SourceFor(string id, string version)
        {
            var core = Find(CoreId);
            if (core != null && core.source == PackageSource.Local)
                return "file:" + Path.GetFullPath(Path.Combine(core.resolvedPath, "..", id)).Replace('\\', '/');
            return GitUrl(id, version);
        }

        public static string GitUrl(string id, string version) => RepoUrl + "?path=/" + id + "#v" + version;
    }
}
