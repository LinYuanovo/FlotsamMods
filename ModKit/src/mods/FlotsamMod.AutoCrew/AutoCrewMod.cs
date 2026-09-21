using System;
using System.Text;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using UnityEngine;

namespace FlotsamMods.AutoCrew
{
    public sealed class AutoCrewMod : FlotsamModBase
    {
        private IKeybind _markerKey;
        private IKeybind _landKey;
        private IHudButton _markerButton;
        private IHudButton _landButton;

        private bool _markerAuto;
        private bool _landAuto;
        private float _intervalSec = 2f;
        private int _markerMax = 5;
        private bool _verbose;

        private bool _running;
        private bool _iconsSet;
        private bool _pendMarkers, _pendLand;
        private float _pendAt = -1f;
        private float _nextBeat;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _markerKey = Keybinds.Register("autocrew.markers", KeyCode.Alpha5, "开关浮标自动派工(Ctrl+5)");
            _landKey = Keybinds.Register("autocrew.landmarks", KeyCode.Alpha6, "开关收集自动派工(Ctrl+6)");
            _markerAuto = Config.Get("markerActive", true);
            _landAuto = Config.Get("landmarkActive", true);
            _intervalSec = Mathf.Clamp(Config.Get("intervalSec", 2f), 0.5f, 30f);
            _markerMax = Mathf.Clamp(Config.Get("markerMax", 5), 1, 20);
            _verbose = Config.Get("verbose", false);
            if (Config.Get("schema", 0) < 1) { Config.Set("schema", 1); Config.Save(); }

            Log.Info($"loaded — markerAuto={_markerAuto}, landmarkAuto={_landAuto}, " +
                     $"interval={_intervalSec:0.##}s, markerMax={_markerMax}");
        }

        public override void OnEnable()
        {
            _markerButton = Ui.AddHudButton("autocrew.markers", MarkerLabel, ToggleMarkers,
                                            HudAnchor.RightMiddle, Config, "button");
            _markerButton.Visible = true;

            _landButton = Ui.AddHudButton("autocrew.landmarks", LandLabel, ToggleLandmarks,
                                          HudAnchor.RightMiddle, Config, "landbutton");
            _landButton.Visible = true;

            Events.On("MarkerPlaced", _ => { if (_markerAuto) Queue(true, false); });
            Events.On("MarkerManuallyRemoved", _ => { if (_markerAuto) Queue(true, false); });
            Events.On("LandmarkNotificationUpdate", _ => { if (_landAuto) Queue(false, true); });

            SyncButtons();
            Log.Info("auto-crew ready (heartbeat + marker/landmark events)");
        }

        public override void OnDisable()
        {
            try { _markerButton?.Destroy(); } catch { }
            try { _landButton?.Destroy(); } catch { }
            _markerButton = null;
            _landButton = null;
            _iconsSet = false;
            ClearPending();
            Log.Info("auto-crew removed");
        }

        public override void OnGameStart()
        {
            Queue(_markerAuto, _landAuto, 1.5f);
            Ui.Toast($"浮标自动派工:{State(_markerAuto)}，收集自动派工:{State(_landAuto)}" +
                     "（Ctrl+5 / Ctrl+6 或 HUD 按钮切换；按选中物品数与单人载重自动定人数）",
                     ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            _running = false;
            _iconsSet = false;
            ClearPending();
            _nextBeat = 0f;
        }

        public override void OnTick()
        {
            if (!_iconsSet && GameApi.IsPlaying && NativeSkin.Available)
            {
                _iconsSet = true;
                try
                {
                    _markerButton?.SetIcon(NativeSkin.Find("Icon_Map_SalvageMarker", "SalvageMarker"));
                    _landButton?.SetIcon(NativeSkin.Find("UI_Icon_LandmarkAction_Salvage_64x64",
                                                         "LandmarkAction_Salvage"));
                }
                catch { }
            }

            if (_markerKey != null && _markerKey.IsDown && GameKeys.GetCtrlHeld()) ToggleMarkers();
            if (_landKey != null && _landKey.IsDown && GameKeys.GetCtrlHeld()) ToggleLandmarks();

            if (!_running && _pendAt >= 0f && Time.realtimeSinceStartup >= _pendAt)
            {
                bool markers = _pendMarkers, land = _pendLand;
                ClearPending();
                Run(markers, land, manual: false);
            }

            if ((_markerAuto || _landAuto) && !_running && _pendAt < 0f && CanAct())
            {
                if (Time.realtimeSinceStartup >= _nextBeat)
                {
                    _nextBeat = Time.realtimeSinceStartup + _intervalSec;
                    Run(_markerAuto, _landAuto, manual: false);
                }
            }
        }

        // ------------------------------------------------------------ state / labels

        private static string State(bool on) => on ? "开" : "关";
        private string MarkerLabel => _markerAuto ? "浮标派工:自动" : "浮标派工:手动";
        private string LandLabel => _landAuto ? "收集派工:自动" : "收集派工:手动";

        private void SyncButtons()
        {
            try { _markerButton?.SetLabel(MarkerLabel); } catch { }
            try { _landButton?.SetLabel(LandLabel); } catch { }
        }

        private bool CanAct()
        {
            return GameApi.IsPlaying && !GameApi.IsMapOpen && !GameApi.IsPaused;
        }

        private void ClearPending()
        {
            _pendMarkers = false;
            _pendLand = false;
            _pendAt = -1f;
        }

        private void Queue(bool markers, bool land, float delay = -1f)
        {
            if (!markers && !land) return;
            _pendMarkers |= markers;
            _pendLand |= land;
            _pendAt = Time.realtimeSinceStartup + (delay >= 0f ? delay : 0.5f);
        }

        // ------------------------------------------------------------ toggles

        private void ToggleMarkers()
        {
            _markerAuto = !_markerAuto;
            Config.Set("markerActive", _markerAuto);
            Config.Save();
            SyncButtons();

            if (!_markerAuto)
            {
                Ui.Toast("浮标自动派工已关闭（保留当前人数，可手动调整）", ToastKind.Info);
                Log.Info("auto-crew markers off");
                return;
            }
            if (GameApi.IsPlaying) Run(true, false, manual: true);
            else
            {
                Queue(true, false, 1f);
                Ui.Toast("浮标自动派工已开启（进入存档后生效）", ToastKind.Info);
            }
        }

        private void ToggleLandmarks()
        {
            _landAuto = !_landAuto;
            Config.Set("landmarkActive", _landAuto);
            Config.Save();
            SyncButtons();

            if (!_landAuto)
            {
                Ui.Toast("收集自动派工已关闭（保留当前人数，可手动调整）", ToastKind.Info);
                Log.Info("auto-crew landmarks off");
                return;
            }
            if (GameApi.IsPlaying) Run(false, true, manual: true);
            else
            {
                Queue(false, true, 1f);
                Ui.Toast("收集自动派工已开启（进入存档后生效）", ToastKind.Info);
            }
        }

        // ------------------------------------------------------------ the pass

        private void Run(bool markers, bool landmarks, bool manual)
        {
            if (_running) { Queue(markers, landmarks); return; }
            if (!GameApi.IsPlaying) { Queue(markers, landmarks, 1f); return; }

            _running = true;
            try
            {
                GameCrew.AutoCrewResult markerResult = null;
                GameCrew.AutoCrewResult landResult = null;

                if (markers)
                    markerResult = GameCrew.RunAutoMarkers(_markerMax, _verbose, m => Log.Info(m));

                if (landmarks)
                    landResult = GameCrew.RunAutoLandmarks(_verbose, m => Log.Info(m));

                Report(markerResult, landResult, manual);
            }
            catch (Exception e)
            {
                Log.Error("auto-crew pass failed", e);
            }
            finally
            {
                _running = false;
            }
        }

        private void Report(GameCrew.AutoCrewResult markerResult, GameCrew.AutoCrewResult landResult, bool manual)
        {
            bool didSomething = (markerResult != null && !markerResult.Blocked && markerResult.HasChanges)
                             || (landResult != null && !landResult.Blocked && landResult.HasChanges);

            if (manual)
            {
                var parts = new StringBuilder();
                if (markerResult != null) parts.Append(markerResult.SummaryText());
                if (landResult != null)
                {
                    if (parts.Length > 0) parts.Append('；');
                    parts.Append(landResult.SummaryText());
                }
                Ui.Toast(parts.Length > 0 ? parts.ToString() : "已就绪",
                         didSomething ? ToastKind.Success : ToastKind.Info);
            }

            if (didSomething || _verbose)
            {
                var line = new StringBuilder("run").Append(manual ? "(manual): " : "(auto): ");
                if (markerResult != null) line.Append("markers[").Append(markerResult.LogLine()).Append(']');
                if (landResult != null)
                {
                    if (markerResult != null) line.Append(' ');
                    line.Append("landmarks[").Append(landResult.LogLine()).Append(']');
                }
                Log.Info(line.ToString());
            }
        }
    }
}
