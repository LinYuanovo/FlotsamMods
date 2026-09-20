using System;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FlotsamMods.KeyboardMove
{
    /// <summary>
    /// Drive the townheart with rebindable keys while the WORLD MAP IS CLOSED.
    ///
    /// Why the previous versions failed (all verified against the decompiled game):
    ///
    ///  1. `WorldMap.Deactivate` ends with `AddMovementBlocker(this)` and `Activate` removes it
    ///     again, so once the player has closed the map a single time, `IsTownMovementBlocked`
    ///     is permanently true. The driver gated on it and therefore silently refused to move
    ///     for the rest of the session — "keyboard move still doesn't work".
    ///     `GameApi.MovementBlockerCount` now ignores the map's own self-block.
    ///
    ///  2. `MovementEvent.DispatchTownheartMove` is a *settle* operation, not a movement
    ///     primitive. `World.RepositionTownheart` re-bases every landmark/POI/road of every tile
    ///     and despawns whatever leaves the spawn radius (MapRadius + 250), then `TownheartMoved`
    ///     rebuilds the terrain grid, refreshes every pathfinding obstacle and warps all drifters.
    ///     Committing it every 0.15 s was continuous respawn churn — "all the buildings around
    ///     me disappeared". The game itself performs exactly ONE such dispatch, when the map
    ///     closes. This mod now does the same: accumulate a voyage, settle once.
    ///
    ///  3. `WorldMapTownheartPhycis2D.ApplyMovementAndRotation` early-outs on `movement == 0`, so
    ///     the native mover cannot rotate unless it is also thrusting (and cannot rotate at all
    ///     while the engine cools down). That is why heading never changed (`rot 0°` in the log).
    ///     `GameMovement.Step` rotates independently.
    ///
    ///  4. Keeping the WorldMap GameObject active ("headless map") renders the map's own sea and
    ///     landmark meshes at world coordinates straight into the gameplay camera. The map
    ///     hierarchy is no longer touched at all; only `WorldMapTownheart.Teleport` /
    ///     `OnStartMove` / `OnEndMove` are called, which work on an inactive hierarchy and keep
    ///     quest objectives and region tracking in sync.
    /// </summary>
    public sealed class KeyboardMoveMod : FlotsamModBase
    {
        private static KeyboardMoveMod _current;

        private IKeybind _forward, _backward, _rotateLeft, _rotateRight, _toggle, _settle;
        private GameObject _driverObject;
        private SailDriver _driver;
        private SailHud _hud;
        private IHudButton _hudButton;

        public float SpeedMultiplier { get; private set; } = 1f;
        public float CommitDistance { get; private set; } = 250f;
        public float CommitRotation { get; private set; } = 90f;
        public bool ShowHud { get; private set; } = true;
        public bool Verbose { get; private set; } = true;
        public bool DrivingEnabled { get; private set; } = true;

        public IModContext Context => Ctx;
        public IKeybind ToggleBind => _toggle;
        public IKeybind SettleBind => _settle;
        public IKeybind ForwardKey => _forward;
        public IKeybind BackwardKey => _backward;
        public IKeybind RotateLeftKey => _rotateLeft;
        public IKeybind RotateRightKey => _rotateRight;
        public SailDriver Driver => _driver;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);

            _forward = Keybinds.Register("move.forward", Default(GameKeys.GetTownheartForwardKey(), KeyCode.W), "前进");
            _backward = Keybinds.Register("move.backward", Default(GameKeys.GetTownheartBackwardKey(), KeyCode.S), "后退");
            _rotateLeft = Keybinds.Register("move.rotateLeft", Default(GameKeys.GetTownheartRotateLeftKey(), KeyCode.A), "左转");
            _rotateRight = Keybinds.Register("move.rotateRight", Default(GameKeys.GetTownheartRotateRightKey(), KeyCode.D), "右转");
            _toggle = Keybinds.Register("move.toggle", KeyCode.F8, "启用/暂停键盘航行");
            _settle = Keybinds.Register("move.settle", KeyCode.Return, "立即靠港（提交当前位置）");

            SpeedMultiplier = Mathf.Clamp(Config.Get("speedMultiplier", 1f), 0.1f, 8f);
            CommitDistance = Mathf.Clamp(Config.Get("commitDistance", 250f), 25f, 5000f);
            CommitRotation = Mathf.Clamp(Config.Get("commitRotation", 90f), 5f, 360f);
            ShowHud = Config.Get("showHud", true);
            Verbose = Config.Get("verbose", true);
            DrivingEnabled = Config.Get("drivingEnabled", true);

            Log.Info($"keybinds {_forward.Key}/{_backward.Key}/{_rotateLeft.Key}/{_rotateRight.Key}, " +
                     $"toggle {_toggle.Key}, settle {_settle.Key}, speed x{SpeedMultiplier:0.##}, " +
                     $"settle every {CommitDistance:0}u or {CommitRotation:0}°");
        }

        private static KeyCode Default(KeyCode fromGame, KeyCode fallback)
            => fromGame != KeyCode.None ? fromGame : fallback;

        public override void OnEnable()
        {
            _current = this;

            _driverObject = new GameObject("[FlotsamMod.KeyboardMove]");
            UnityEngine.Object.DontDestroyOnLoad(_driverObject);
            _driverObject.hideFlags = HideFlags.HideAndDontSave;

            _driver = _driverObject.AddComponent<SailDriver>();
            _driver.Mod = this;

            _hud = _driverObject.AddComponent<SailHud>();
            _hud.Mod = this;
            _hud.Driver = _driver;

            // The instrument panel can be closed from its own title bar, so it needs a way back.
            _hudButton = Ui.AddHudButton("keyboardmove.hud", "航行仪表", ToggleHud, HudAnchor.LeftTop,
                                         Config, "button");
            _hudButton.Visible = true;
            _hudButton.SetLabel(ShowHud ? "隐藏仪表" : "航行仪表");

            // The world map must never open on top of an uncommitted voyage: the map reads its
            // own townheart as the voyage start, and commits start->end when it closes. If the
            // world model is still at the old position while the map shows the new one, closing
            // the map commits a zero move and the town snaps back - "position scrambled".
            // Committing in a PREFIX on Activate guarantees the world is re-based first.
            try
            {
                var activate = GamePatches.MethodByName(typeof(WorldMap), "Activate");
                if (activate != null)
                {
                    Patches.Prefix(activate, AccessTools.Method(typeof(KeyboardMoveMod), nameof(BeforeMapActivate)));
                    Patches.Postfix(activate, AccessTools.Method(typeof(KeyboardMoveMod), nameof(AfterMapActivate)));
                    Log.Info("patch: WorldMap.Activate <- BeforeMapActivate / AfterMapActivate");
                }
                else
                    Log.Warn("WorldMap.Activate not found - voyage commits fall back to the settle key");

                var deactivate = GamePatches.MethodByName(typeof(WorldMap), "Deactivate");
                if (deactivate != null)
                {
                    Patches.Postfix(deactivate, AccessTools.Method(typeof(KeyboardMoveMod), nameof(AfterMapDeactivate)));
                    Log.Info("patch: WorldMap.Deactivate <- AfterMapDeactivate");
                }
                else
                    Log.Warn("WorldMap.Deactivate not found - map close diagnostics unavailable");

                var dispatch = GamePatches.Method(typeof(MovementEvent), "DispatchTownheartMove",
                                                  typeof(Vector3), typeof(Vector3),
                                                  typeof(Quaternion), typeof(Quaternion), typeof(float));
                if (dispatch != null)
                {
                    Patches.Prefix(dispatch, AccessTools.Method(typeof(KeyboardMoveMod), nameof(BeforeTownheartMoveDispatch)));
                    Log.Info("patch: MovementEvent.DispatchTownheartMove <- BeforeTownheartMoveDispatch");
                }
                else
                    Log.Warn("MovementEvent.DispatchTownheartMove not found - commit diagnostics unavailable");
            }
            catch (Exception e)
            {
                Log.Warn("WorldMap.Activate patch failed: " + e.Message);
            }

            Log.Info("keyboard sailing ready (virtual voyage + single settle)");
        }

        private static void BeforeMapActivate()
        {
            try
            {
                var driver = _current?._driver;
                if (driver != null) driver.CommitForMap();
            }
            catch (Exception e)
            {
                if (_current != null) _current.Log.Warn("commit-for-map failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------ map diagnostics

        private static System.Reflection.FieldInfo _mapStartField, _mapDistField, _mapPhysicsField;

        /// <summary>
        /// One-line snapshot of every position that takes part in a map open/close cycle: the
        /// voyage start the map recorded, how far it thinks it moved, the map townheart transform
        /// (what Deactivate commits as the destination), the map physics body and the real world
        /// position. Any disagreement between these is the "position scrambled" bug.
        /// </summary>
        internal static string MapState(WorldMap map)
        {
            if (map == null) return "map=null";
            try
            {
                if (_mapStartField == null)
                {
                    const System.Reflection.BindingFlags flags =
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var t = typeof(WorldMap);
                    _mapStartField = t.GetField("_startPosition", flags);
                    _mapDistField = t.GetField("_distanceMoved", flags);
                    _mapPhysicsField = t.GetField("_townheartPhysics", flags);
                }

                var start = _mapStartField != null ? (Vector3)_mapStartField.GetValue(map) : Vector3.zero;
                var moved = _mapDistField != null ? (float)_mapDistField.GetValue(map) : -1f;
                var physics = _mapPhysicsField != null
                    ? _mapPhysicsField.GetValue(map) as WorldMapTownheartPhycis2D : null;
                var th = map.Townheart;
                var thp = th != null ? th.transform.position : Vector3.zero;
                var php = physics != null ? physics.Position : Vector3.zero;
                var world = GameMovement.TownheartPosition;
                return $"start=({start.x:0},{start.z:0}) moved={moved:0.#} " +
                       $"mapTownheart=({thp.x:0},{thp.z:0}) mapPhysics=({php.x:0},{php.z:0}) " +
                       $"world=({world.x:0},{world.z:0})";
            }
            catch (Exception e)
            {
                return "mapstate error: " + e.Message;
            }
        }

        public static void AfterMapActivate(WorldMap __instance)
        {
            try { if (_current != null) _current.Log.Info("mapdiag: activate   " + MapState(__instance)); } catch { }
        }

        public static void AfterMapDeactivate(WorldMap __instance)
        {
            try { if (_current != null) _current.Log.Info("mapdiag: deactivate " + MapState(__instance)); } catch { }
        }

        public static void BeforeTownheartMoveDispatch(Vector3 positionFrom, Vector3 positionTo, float distance)
        {
            try
            {
                if (_current == null) return;
                _current.Log.Info($"mapdiag: dispatch from=({positionFrom.x:0},{positionFrom.z:0}) " +
                                  $"to=({positionTo.x:0},{positionTo.z:0}) d={distance:0.#}");
            }
            catch { }
        }

        public override void OnDisable()
        {
            try { _driver?.Settle("mod disabled"); } catch { }
            try { _hudButton?.Destroy(); } catch { }
            _hudButton = null;
            if (_driverObject != null)
            {
                UnityEngine.Object.Destroy(_driverObject);
                _driverObject = null;
                _driver = null;
                _hud = null;
            }
            _current = null;
            Log.Info("sailing driver removed, vanilla behaviour restored");
        }

        public override void OnGameEnd()
        {
            try { _driver?.Settle("game ended"); } catch { }
            try { _hud?.Teardown(); } catch { }
        }

        public override void OnGameStart()
        {
            try { _driver?.ResetTrip(); } catch { }
            Ui.Toast($"键盘航行就绪：地图关闭时 {_forward.Key}/{_backward.Key} 进退、" +
                     $"{_rotateLeft.Key}/{_rotateRight.Key} 转向，松手即靠港，{_settle.Key} 立即靠港，" +
                     $"{_toggle.Key} 暂停", ToastKind.Success);
        }

        public void SetDrivingEnabled(bool value)
        {
            DrivingEnabled = value;
            Config.Set("drivingEnabled", value);
            Config.Save();
            if (!value) { try { _driver?.Settle("paused by player"); } catch { } }
        }

        internal void ToggleDriving()
        {
            SetDrivingEnabled(!DrivingEnabled);
            Ui.Toast(DrivingEnabled ? "键盘航行：开" : "键盘航行：关（已靠港）", ToastKind.Info);
        }

        internal void ToggleHud()
        {
            ShowHud = !ShowHud;
            Config.Set("showHud", ShowHud);
            Config.Save();
            if (_hud != null) _hud.ApplyVisibility();
            if (_hudButton != null) _hudButton.SetLabel(ShowHud ? "隐藏仪表" : "航行仪表");
        }

        internal void Report(string message, ToastKind kind)
        {
            try { Ui.Toast(message, kind); } catch { }
        }

        internal void ReportSettleFailure(string error)
        {
            Report("靠港失败：" + (error ?? "未知原因") + "（已回到上一次成功的位置）", ToastKind.Warning);
            if (Verbose) Log.Warn("settle failed: " + error);
        }

        internal void ReportSettled(float distance)
        {
            if (!Verbose) return;
            Log.Info($"settled after {distance:0.#}u at ({GameMovement.TownheartPosition.x:0},{GameMovement.TownheartPosition.z:0})");
        }
    }

    /// <summary>
    /// Accumulates a virtual voyage from the game's own movement parameters and settles it into
    /// the world model once, the way closing the map does.
    /// </summary>
    public sealed class SailDriver : MonoBehaviour
    {
        public KeyboardMoveMod Mod;

        private Vector3 _base;
        private Quaternion _baseRotation;
        private Vector3 _position;
        private Quaternion _rotation;

        private float _legDistance;
        private float _legRotation;
        private float _tripDistance;
        private float _speed;
        private bool _initialized;
        private bool _mapWasOpen;

        public bool Sailing { get; private set; }
        public string Blocked { get; private set; }
        public float LegDistance => _legDistance;
        public float TripDistance => _tripDistance;
        public float Speed => _speed;
        public float Heading => HeadingOf(_rotation);
        public Vector3 Position => Sailing ? _position : GameMovement.TownheartPosition;
        public Quaternion Rotation => Sailing ? _rotation : GameMovement.TownheartRotation;

        public static float HeadingOf(Quaternion rotation)
        {
            var dir = rotation * Vector3.forward;
            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return (angle % 360f + 360f) % 360f;
        }

        /// <summary>Zeroes the session odometer; called on every game start.</summary>
        public void ResetTrip()
        {
            _tripDistance = 0f;
            _legDistance = 0f;
            _legRotation = 0f;
            _initialized = false;
            if (Sailing) Settle("new game");
        }

        public static string CompassOf(float heading)
        {
            string[] names = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
            int index = Mathf.RoundToInt(heading / 45f) % 8;
            return names[index];
        }

        private void FixedUpdate()
        {
            if (Mod == null) return;

            if (GameApi.IsMapOpen)
            {
                if (!_mapWasOpen)
                {
                    _mapWasOpen = true;
                    Mod.Context.Log.Info($"map open: world=({GameMovement.TownheartPosition.x:0},{GameMovement.TownheartPosition.z:0}) " +
                                         $"virtual=({_position.x:0},{_position.z:0}) sailing={Sailing} | " +
                                         KeyboardMoveMod.MapState(GameApi.Map));
                }
                // The Activate prefix already committed any in-flight voyage. If we are still
                // sailing here the prefix missed (map opened through another path): commit now,
                // because dropping the voyage would snap the town back to the old position.
                if (Sailing)
                {
                    Mod.Context.Log.Warn("map open with an uncommitted voyage - committing late");
                    SettleCore("map open fallback");
                }
                Blocked = "map open";
                _speed = 0f;
                GameMovement.CancelAuto();
                return;
            }

            if (_mapWasOpen)
            {
                _mapWasOpen = false;
                _initialized = false;
                Mod.Context.Log.Info($"map closed: world=({GameMovement.TownheartPosition.x:0},{GameMovement.TownheartPosition.z:0}) " +
                                     $"virtual=({_position.x:0},{_position.z:0}) | " +
                                     KeyboardMoveMod.MapState(GameApi.Map));
            }

            string reason = GameMovement.BlockReason(false);
            if (reason != null)
            {
                Blocked = reason;
                _speed = 0f;
                GameMovement.CancelAuto();
                if (Sailing) Settle("blocked: " + reason);
                return;
            }

            if (!Mod.DrivingEnabled)
            {
                Blocked = "已暂停（" + Mod.ToggleBind.Key + " 恢复）";
                _speed = 0f;
                GameMovement.CancelAuto();
                if (Sailing) Settle("paused");
                return;
            }

            bool forwardHeld = Held(Mod, "forward");
            bool backwardHeld = Held(Mod, "backward");
            bool leftHeld = Held(Mod, "left");
            bool rightHeld = Held(Mod, "right");

            float forward = (forwardHeld ? 1f : 0f) - (backwardHeld ? 1f : 0f);
            float rotate = (rightHeld ? 1f : 0f) - (leftHeld ? 1f : 0f);

            // Minimap click-to-sail: steer the virtual voyage until the marker is reached. Any
            // movement key takes over from the autopilot, exactly like grabbing the wheel.
            if (GameMovement.AutoActive)
            {
                if (forwardHeld || backwardHeld || leftHeld || rightHeld)
                {
                    GameMovement.CancelAuto();
                    Mod.Report("已手动接管航行", ToastKind.Info);
                }
                else
                {
                    float remain = (GameMovement.AutoTarget - _position).magnitude;
                    if (remain <= GameMovement.AutoArriveRadius)
                    {
                        GameMovement.CancelAuto();
                        Settle("autopilot arrived");
                        Mod.Report($"已抵达小地图目标（{remain:0} 单位内靠港）", ToastKind.Success);
                        forward = 0f;
                        rotate = 0f;
                    }
                    else
                    {
                        GameMovement.AutoInput(_position, _rotation, out forward, out rotate);
                    }
                }
            }

            if (Mathf.Approximately(forward, 0f) && Mathf.Approximately(rotate, 0f))
            {
                _speed = 0f;
                Blocked = null;
                if (Sailing) Settle("released");
                return;
            }

            if (!Sailing) BeginVoyage();

            float dt = Time.fixedUnscaledDeltaTime;
            var step = GameMovement.Step(_position, _rotation, forward, rotate, dt, Mod.SpeedMultiplier);

            // World-map-equivalent movement protection: refuse any step that would cross into a
            // landmark/construction obstacle circle. Autopilot into a building cancels instead of
            // parking the town inside it.
            if (step.Moved)
            {
                Vector2 from2D = new Vector2(_position.x, _position.z);
                Vector2 to2D = new Vector2(step.ToPosition.x, step.ToPosition.z);
                Vector2 base2D = new Vector2(_base.x, _base.z);
                string obstacleWhy;
                if (GameMovement.BlocksMovement(from2D, to2D, base2D, out obstacleWhy))
                {
                    if (GameMovement.AutoActive)
                    {
                        GameMovement.CancelAuto();
                        Settle("autopilot blocked by obstacle");
                        Mod.Report($"目标在{obstacleWhy}保护范围内，无法靠近，自动航行已取消", ToastKind.Warning);
                    }
                    step.Moved = false;
                    step.Distance = 0f;
                    step.ToPosition = _position;
                    Blocked = $"离{obstacleWhy}太近，无法靠近";
                }
                else
                {
                    Blocked = step.Blocked;
                }
            }
            else
            {
                Blocked = step.Blocked;
            }
            _position = step.ToPosition;
            _rotation = step.ToRotation;
            _legDistance += step.Distance;
            _legRotation += Mathf.Abs(step.RotationDegrees);
            _tripDistance += step.Distance;
            _speed = dt > 0f ? step.Distance / dt : 0f;

            GameMovement.ConsumeEnergy(step.Distance);
            ClampIntoWorld();
            GameMovement.EnsureTiles(_position);
            PublishVoyage();
            GameMovement.SyncMapTownheart(_position, _rotation);

            if (_legDistance >= Mod.CommitDistance || _legRotation >= Mod.CommitRotation)
                Settle("threshold");
        }

        private static bool Held(KeyboardMoveMod mod, string which)
        {
            switch (which)
            {
                case "forward": return IsHeld(mod.ForwardKey);
                case "backward": return IsHeld(mod.BackwardKey);
                case "left": return IsHeld(mod.RotateLeftKey);
                default: return IsHeld(mod.RotateRightKey);
            }
        }

        private static bool IsHeld(IKeybind bind) => bind != null && bind.IsHeld;

        private void BeginVoyage()
        {
            EnsureSeeded();
            _base = GameMovement.TownheartPosition;
            _baseRotation = GameMovement.TownheartRotation;
            _position = _base;
            _rotation = _baseRotation;
            _legDistance = 0f;
            _legRotation = 0f;

            Sailing = true;
            GameMovement.Sailing = true;
            PublishVoyage();
            GameMovement.NotifyStartMoving();
        }

        private void EnsureSeeded()
        {
            if (_initialized) return;
            _initialized = true;
            _base = GameMovement.TownheartPosition;
            _baseRotation = GameMovement.TownheartRotation;
            _position = _base;
            _rotation = _baseRotation;
        }

        private void PublishVoyage()
        {
            GameMovement.VoyagePosition = _position;
            GameMovement.VoyageRotation = _rotation;
        }

        /// <summary>Never let the voyage drift outside the tiles the world has actually spawned.</summary>
        private void ClampIntoWorld()
        {
            Rect rect;
            if (!GameMovement.TryGetTravelRect(out rect)) return;
            const float margin = 16f;
            _position.x = Mathf.Clamp(_position.x, rect.xMin + margin, rect.xMax - margin);
            _position.z = Mathf.Clamp(_position.z, rect.yMin + margin, rect.yMax - margin);
            _position.y = 0f;
        }

        /// <summary>
        /// The single settle: one `DispatchTownheartMove`, exactly like `WorldMap.Deactivate`.
        /// </summary>
        public void Settle(string reason)
        {
            if (!Sailing) return;
            if (GameApi.IsMapOpen)
            {
                // The map owns movement while open; the Activate prefix commits before that.
                Sailing = false;
                GameMovement.Sailing = false;
                return;
            }
            SettleCore(reason);
        }

        /// <summary>
        /// Called from the Harmony prefix on `WorldMap.Activate`: the world model must already be
        /// at the voyage position when the map reads its townheart as the voyage start, otherwise
        /// closing the map commits a zero move and the town snaps back ("position scrambled").
        /// </summary>
        public void CommitForMap()
        {
            if (!Sailing) return;
            Mod.Context.Log.Info($"commit-for-map: world=({_base.x:0},{_base.z:0}) -> " +
                                 $"({_position.x:0},{_position.z:0}) leg={_legDistance:0}");
            SettleCore("map opening");
        }

        private void SettleCore(string reason)
        {
            Sailing = false;
            GameMovement.Sailing = false;
            _speed = 0f;

            float distance = _legDistance;
            float rotation = _legRotation;

            if (distance > 0.01f || rotation > 0.05f)
            {
                string error;
                if (GameMovement.Commit(_base, _position, _baseRotation, _rotation, distance, out error))
                {
                    _base = new Vector3(_position.x, 0f, _position.z);
                    _baseRotation = _rotation;
                    _legDistance = 0f;
                    _legRotation = 0f;
                    if (Mod != null) Mod.ReportSettled(distance);
                }
                else
                {
                    if (Mod != null) Mod.ReportSettleFailure(error);
                    _base = GameMovement.TownheartPosition;
                    _baseRotation = GameMovement.TownheartRotation;
                    _position = _base;
                    _rotation = _baseRotation;
                    _legDistance = 0f;
                    _legRotation = 0f;
                }
            }

            PublishVoyage();
            GameMovement.SyncMapTownheart(_base, _baseRotation);
            GameMovement.NotifyStopMoving();

            if (Mod != null)
                Mod.Context.Log.Info($"settled ({reason}) d={distance:0.#} rot={rotation:0.#}° " +
                                     $"world=({_base.x:0},{_base.z:0})");
        }

        private void Update()
        {
            if (Mod == null) return;

            if (Mod.ToggleBind != null && Mod.ToggleBind.IsDown)
            {
                try { Mod.ToggleDriving(); } catch { }
            }

            if (Mod.SettleBind != null && Mod.SettleBind.IsDown && Sailing)
            {
                float leg = _legDistance;
                Settle("player requested");
                Mod.Report(leg > 0.01f
                    ? $"已靠港：本段航行 {leg:0} 单位，世界已更新"
                    : "已靠港：本段没有位移", ToastKind.Info);
            }
        }

        private void OnDisable()
        {
            try { Settle("driver disabled"); } catch { }
        }

        private void OnDestroy()
        {
            try { Settle("driver destroyed"); } catch { }
            GameMovement.Sailing = false;
        }
    }
}
