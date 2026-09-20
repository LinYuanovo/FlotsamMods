using UnityEngine;
using Rewired;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Keyboard reading that deliberately does NOT depend on any Rewired action map
    /// being enabled: this is what makes it possible to read movement keys while the
    /// world map (and therefore the map input category) is closed.
    /// </summary>
    public static class GameKeys
    {
        public static bool RewiredReady
        {
            get
            {
                try { return ReInput.isReady; }
                catch { return false; }
            }
        }

        private static Keyboard Keyboard
        {
            get
            {
                if (!RewiredReady) return null;
                try { return ReInput.controllers.Keyboard; }
                catch { return null; }
            }
        }

        public static bool GetKey(KeyCode key)
        {
            var kb = Keyboard;
            return kb != null && key != KeyCode.None && kb.GetKey(key);
        }

        public static bool GetKeyDown(KeyCode key)
        {
            var kb = Keyboard;
            return kb != null && key != KeyCode.None && kb.GetKeyDown(key);
        }

        public static bool GetKeyUp(KeyCode key)
        {
            var kb = Keyboard;
            return kb != null && key != KeyCode.None && kb.GetKeyUp(key);
        }

        /// <summary>
        /// Reads whatever key the player has currently bound to a game action, so mod
        /// defaults follow the game's own rebinding UI. Returns KeyCode.None if unbound.
        /// </summary>
        public static KeyCode GetBoundKey(int actionId)
        {
            try
            {
                var map = FlotsamInputManager.GetFirstKeyboardMapWithAction(actionId, true);
                return map != null ? map.keyCode : KeyCode.None;
            }
            catch { return KeyCode.None; }
        }

        public static KeyCode GetTownheartForwardKey() => GetBoundKey(RewiredConsts.Action.Townheart__Forward);
        public static KeyCode GetTownheartBackwardKey() => GetBoundKey(RewiredConsts.Action.Townheart_Backward);
        public static KeyCode GetTownheartRotateLeftKey() => GetBoundKey(RewiredConsts.Action.Townheart__Rotate_Left);
        public static KeyCode GetTownheartRotateRightKey() => GetBoundKey(RewiredConsts.Action.Townheart_Rotate_Right);

        public static bool GetShiftHeld()
        {
            return GetKey(KeyCode.LeftShift) || GetKey(KeyCode.RightShift);
        }

        public static bool GetAltHeld()
        {
            return GetKey(KeyCode.LeftAlt) || GetKey(KeyCode.RightAlt);
        }

        public static bool GetCtrlHeld()
        {
            return GetKey(KeyCode.LeftControl) || GetKey(KeyCode.RightControl);
        }

        /// <summary>
        /// Cursor position in screen pixels. Read through the game's own input manager first:
        /// Rewired may be backed by the new Input System, in which case UnityEngine.Input throws.
        /// </summary>
        public static Vector2 MousePosition
        {
            get
            {
                try { return FlotsamInputManager.MousePosition; } catch { }
                try { return Input.mousePosition; } catch { }
                return Vector2.zero;
            }
        }

        /// <summary>The camera a UI canvas needs for screen&lt;-&gt;local conversions.</summary>
        public static Camera CanvasCamera(UnityEngine.Canvas canvas)
        {
            try
            {
                if (canvas == null) return null;
                return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            }
            catch { return null; }
        }
    }
}