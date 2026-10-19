using UnityEngine;

namespace Focus.Howl.Tools
{
    /// <summary>Key presses that work with either input backend.</summary>
    public static class HowlKeys
    {
        public static bool Pressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && HOWL_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return false;
            var mapped = ToKey(key);
            return mapped != UnityEngine.InputSystem.Key.None && keyboard[mapped].wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(key);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM && HOWL_INPUT_SYSTEM
        static UnityEngine.InputSystem.Key ToKey(KeyCode key)
        {
            if (key >= KeyCode.F1 && key <= KeyCode.F12) return UnityEngine.InputSystem.Key.F1 + (key - KeyCode.F1);
            return UnityEngine.InputSystem.Key.None;
        }
#endif
    }
}
