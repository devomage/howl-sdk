using System.IO;
using UnityEditor;
using UnityEngine;

namespace Focus.Howl.Opsive.Editor
{
    static class HowlOpsiveSettingsMenu
    {
        const string DefaultFolder = "Assets/Howl/Resources";

        [MenuItem("Tools/Howl/Opsive Settings")]
        static void SelectOrCreate()
        {
            var settings = Resources.Load<HowlOpsiveSettings>(HowlOpsiveSettings.ResourcePath);
            if (settings == null)
            {
                Directory.CreateDirectory(DefaultFolder);
                AssetDatabase.Refresh();
                settings = ScriptableObject.CreateInstance<HowlOpsiveSettings>();
                AssetDatabase.CreateAsset(settings, DefaultFolder + "/" + HowlOpsiveSettings.ResourcePath + ".asset");
                AssetDatabase.SaveAssets();
                Debug.Log("[Howl] Created " + AssetDatabase.GetAssetPath(settings) + ". It can live in any Resources folder.");
            }
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }
    }
}
