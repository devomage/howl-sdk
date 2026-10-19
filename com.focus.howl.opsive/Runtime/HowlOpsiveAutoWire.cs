using System.Collections.Generic;
using Opsive.UltimateCharacterController.Camera;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Traits;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Focus.Howl.Opsive
{
    /// <summary>
    /// Finds UCC characters and Health objects at play start and attaches Howl sources. Nothing is saved into scenes.
    /// Rescans after the first frames to catch characters spawned in Start (the UCC demo instantiates its player there).
    /// Call <see cref="Wire(GameObject)"/> for objects spawned later.
    /// </summary>
    public static class HowlOpsiveAutoWire
    {
        static readonly int[] RescanFrames = { 1, 10 };

        static HowlOpsiveSettings s_Settings;
        static readonly HashSet<GameObject> s_CameraTargets = new HashSet<GameObject>();

        public static HowlOpsiveSettings Settings => s_Settings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            s_Settings = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            s_Settings = HowlOpsiveSettings.Load();
            if (!s_Settings.AutoWire) return;

            SceneManager.sceneLoaded += OnSceneLoaded;
            WireAll();

            var runner = new GameObject("[Howl Opsive]") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => WireAll();

        /// <summary>Scans all loaded scenes (including inactive objects) and wires anything not yet wired.</summary>
        public static void WireAll()
        {
            if (s_Settings == null) return;

            s_CameraTargets.Clear();
            foreach (var cameraController in Object.FindObjectsByType<CameraController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (cameraController.Character != null) s_CameraTargets.Add(cameraController.Character);

            int characters = 0, objects = 0;
            foreach (var locomotion in Object.FindObjectsByType<UltimateCharacterLocomotion>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (WireCharacter(locomotion.gameObject)) characters++;
            foreach (var health in Object.FindObjectsByType<Health>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (WireObject(health.gameObject)) objects++;

            if (s_Settings.LogWiring && (characters > 0 || objects > 0))
            {
                var player = HowlOpsiveRegistry.Player;
                Debug.Log($"[Howl] Opsive auto-wire: +{characters} characters, +{objects} objects. Player: {(player != null ? Howl.GetString(player.Name) : "none yet")}");
            }
        }

        /// <summary>Wires one spawned object: a UCC character or anything with Health.</summary>
        public static void Wire(GameObject go)
        {
            if (s_Settings == null || go == null) return;
            if (go.GetComponent<UltimateCharacterLocomotion>() != null) WireCharacter(go);
            else if (go.GetComponent<Health>() != null) WireObject(go);
        }

        static bool WireCharacter(GameObject go)
        {
            if (HowlOpsiveRegistry.Contains(go)) return false;
            HowlOpsiveEvents.Register();
            var entity = HowlOpsiveRegistry.Add(go, HowlEntityRole.Agent, "agent");
            go.AddComponent<HowlCharacterSource>().Init(entity, s_Settings, s_CameraTargets.Contains(go));
            return true;
        }

        static bool WireObject(GameObject go)
        {
            if (HowlOpsiveRegistry.Contains(go) || go.GetComponent<UltimateCharacterLocomotion>() != null) return false;
            HowlOpsiveEvents.Register();
            var entity = HowlOpsiveRegistry.Add(go, HowlEntityRole.Object, s_Settings.Categorize(go.name));
            go.AddComponent<HowlHealthSource>().Init(entity, s_Settings);
            return true;
        }

        sealed class Runner : MonoBehaviour
        {
            int m_Frame;
            int m_NextRescan;

            void Update()
            {
                m_Frame++;
                if (m_NextRescan < RescanFrames.Length && m_Frame >= RescanFrames[m_NextRescan])
                {
                    m_NextRescan++;
                    WireAll();
                }
                if (m_NextRescan >= RescanFrames.Length) enabled = false;
            }
        }
    }
}
