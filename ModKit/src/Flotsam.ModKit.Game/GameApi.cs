using System;
using System.Reflection;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// The only place in the kit that touches game types directly for *state* queries.
    /// Keeping it in one assembly means a game update only has to be absorbed here.
    /// All members are defensive: they never throw during scene transitions.
    /// </summary>
    public static class GameApi
    {
        // ------------------------------------------------------------ references

        public static bool HasGameManager => GameManager.Instance != null;
        public static bool IsInitialized => GameManager.Initialized;
        public static UIManager Ui => GameManager.UIManager;
        public static World World => GameManager.WorldManager != null ? GameManager.WorldManager.World : null;
        public static WorldMap Map => GameManager.WorldMapManager != null ? GameManager.WorldMapManager.WorldMap : null;
        public static Community PlayerCommunity => Community.PlayerCommunity;

        public static bool IsPlaying
        {
            get
            {
                if (!HasGameManager || !IsInitialized) return false;
                if (GameManager.WorldManager == null || GameManager.UIManager == null) return false;
                return World != null && PlayerCommunity != null;
            }
        }

        // ------------------------------------------------------------ ui state

        public static bool IsPaused => GameManager.Gamepaused;

        public static bool IsTyping
        {
            get
            {
                var ui = Ui;
                if (ui == null) return false;
                return UIManager.State == UIState.Typing;
            }
        }

        public static bool CameraInputBlocked => UIManager.HasFlagsSet(PanelContainerFlags.BlockCameraInput);

        public static bool IsMapOpen
        {
            get
            {
                var map = Map;
                return map != null && map.isActiveAndEnabled;
            }
        }

        // ------------------------------------------------------------ townheart

        /// <summary>Movement speed already includes the townheart engine multiplier.</summary>
        public static float TownheartMovementSpeed
        {
            get { var m = Map; return m != null ? m.MovementSpeed : 0f; }
        }

        public static float TownheartRotationSpeed
        {
            get { var m = Map; return m != null ? m.RotationSpeed : 0f; }
        }

        public static bool TownMovementBlocked => MovementBlockerCount > 0;

        private static FieldInfo _blockersField;
        private static bool _blockerFieldMissing;

        /// <summary>
        /// Number of movement blockers OTHER THAN the WorldMap's own.
        ///
        /// `WorldMap.Deactivate` ends with `AddMovementBlocker(this)` and `Activate` with
        /// `RemoveMovementBlocker(this)`, so with the map closed the map itself is always a
        /// blocker and `IsTownMovementBlocked` is permanently true after the first map close.
        /// Reading it raw is what made closed-map driving stop working for the rest of the
        /// session; the remaining blockers (a dialogue that asks for it) are the real ones.
        /// </summary>
        public static int MovementBlockerCount
        {
            get
            {
                var map = Map;
                if (map == null) return 0;
                try
                {
                    if (_blockersField == null && !_blockerFieldMissing)
                    {
                        _blockersField = typeof(WorldMap).GetField("_movementBlockers",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        _blockerFieldMissing = _blockersField == null;
                    }
                    if (_blockersField == null) return 0;

                    var list = _blockersField.GetValue(map) as System.Collections.IList;
                    if (list == null) return 0;

                    int n = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var o = list[i];
                        if (o != null && !ReferenceEquals(o, map)) n++;
                    }
                    return n;
                }
                catch { return 0; }
            }
        }

        /// <summary>Names of the real movement blockers, for a HUD explanation.</summary>
        public static string MovementBlockers()
        {
            var map = Map;
            if (map == null) return "";
            try
            {
                if (_blockersField == null) return "";
                var list = _blockersField.GetValue(map) as System.Collections.IList;
                if (list == null) return "";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o == null || ReferenceEquals(o, map)) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(o.GetType().Name);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static FieldInfo _mapEngineField;

        /// <summary>
        /// The townheart engine lives as a private field on WorldMap; the community
        /// extendable lookup is tried first (public API), reflection is the fallback.
        /// </summary>
        public static Engine FindTownheartEngine()
        {
            var community = PlayerCommunity;
            if (community != null)
            {
                try
                {
                    if (community.TryReturnBuildableExtendable<Engine>(out var engine) && engine != null)
                        return engine;
                }
                catch { /* community may be rebuilding */ }
            }

            var map = Map;
            if (map == null) return null;
            try
            {
                if (_mapEngineField == null)
                    _mapEngineField = typeof(WorldMap).GetField("_engine",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                return _mapEngineField != null ? _mapEngineField.GetValue(map) as Engine : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------ cameras

        public static CameraController CameraController
        {
            get
            {
                try { return CameraController.Instance; }
                catch { return null; }
            }
        }

        /// <summary>Focus point used by the game's own buildable-overview row click.</summary>
        public static Vector3 FocusPoint
        {
            get
            {
                var cc = CameraController;
                if (cc == null) return Vector3.zero;
                try { return cc.ReturnFocusPoint(); }
                catch { return Vector3.zero; }
            }
        }

        public static bool LockCameraOn(GameObject target, float zoomLevel)
        {
            var cc = CameraController;
            if (cc == null || target == null) return false;
            try { cc.Lock(target, zoomLevel); return true; }
            catch { return false; }
        }
    }
}