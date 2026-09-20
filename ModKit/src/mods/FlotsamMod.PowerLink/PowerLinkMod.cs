using System;
using System.IO;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using UnityEngine;

namespace FlotsamMods.PowerLink
{
    /// <summary>
    /// One-key energy-grid auto-connect. Ctrl+2 (or the HUD button) connects every unpowered
    /// building to the powered network with the shortest total cable, optionally re-organizing
    /// existing wiring when that is meaningfully shorter, and merging a deficient powered grid
    /// into a surplus one. The optional auto mode repeats the incremental pass a few seconds
    /// after any building is built/placed, plus a low-frequency heartbeat.
    /// Logging policy (design §5): one summary line per run with changes; per-edge detail only
    /// when verbose; never anything per-frame.
    /// </summary>
    public sealed class PowerLinkMod : FlotsamModBase
    {
        private IKeybind _hotkey;
        private IHudButton _runButton;
        private IHudButton _autoButton;

        private bool _auto;
        private float _interval = 10f;
        private bool _optimize = true;
        private float _gainPct = 10f;
        private bool _connectPoles = true;
        private bool _merge = true;
        private bool _verbose;

        private int _connectsPerTick = 4;
        private EnergyRunSession _session;
        private bool _sessionManual;
        private bool _iconSet;
        private float _nextHeartbeat;
        private float _pendingAt = -1f;
        private string _lastToast = "";
        private float _lastToastAt = -99f;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _hotkey = Keybinds.Register("powerlink.connect", KeyCode.Alpha2, "一键连电网(Ctrl+2)");
            _auto = Config.Get("autoMode", false);
            _interval = Mathf.Clamp(Config.Get("autoIntervalSec", 10f), 3f, 120f);
            _optimize = Config.Get("optimizeExisting", true);
            _gainPct = Mathf.Clamp(Config.Get("optimizeGainPct", 10f), 0f, 90f);
            _connectPoles = Config.Get("connectPolesToGrid", true);
            _merge = Config.Get("mergePoweredGrids", true);
            _verbose = Config.Get("verbose", false);
            _connectsPerTick = Mathf.RoundToInt(Mathf.Clamp(Config.Get("connectsPerTick", 4f), 1f, 50f));
            if (Config.Get("schema", 0) < 1) { Config.Set("schema", 1); Config.Save(); }
        }

        public override void OnEnable()
        {
            _runButton = Ui.AddHudButton("powerlink.run", "连电网", () => RunOnce(true),
                                         HudAnchor.RightMiddle, Config, "button");
            _runButton.Visible = true;

            _autoButton = Ui.AddHudButton("powerlink.auto", AutoLabel, ToggleAuto,
                                          HudAnchor.RightMiddle, Config, "autobutton");
            _autoButton.Visible = true;

            Events.On("BuildableBuilt", _ => QueueAuto());
            Events.On("BuildablePlaced", _ => QueueAuto());

            Log.Info($"powerlink ready (auto={_auto}, optimize={_optimize}, gainPct={_gainPct:0.#}, " +
                     $"poles={_connectPoles}, merge={_merge})");
        }

        public override void OnDisable()
        {
            try { _runButton?.Destroy(); } catch { }
            try { _autoButton?.Destroy(); } catch { }
            _runButton = null;
            _autoButton = null;
            _pendingAt = -1f;
            _iconSet = false;
            _session = null;
            Log.Info("powerlink removed");
        }

        public override void OnGameStart()
        {
            _nextHeartbeat = Time.realtimeSinceStartup + _interval;
            Ui.Toast($"一键电网就绪：Ctrl+2 或点「连电网」；自动连网当前{(_auto ? "开" : "关")}",
                     ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            _pendingAt = -1f;
            _session = null;   // drop any half-finished run; connectors are save-scoped anyway
        }

        public override void OnTick()
        {
            if (_hotkey != null && _hotkey.IsDown && GameKeys.GetCtrlHeld()) RunOnce(true);

            // Drain the spread-out run a few cables per tick (crash hardening: never a big
            // burst of Connect/Merge inside one frame).
            if (_session != null && !_session.Done)
            {
                _session.Step(_connectsPerTick);
                if (_session.Done) ReportRun(_session.Result, _sessionManual);
            }

            // Icon lookup only works once the game has spawned its UI (NativeSkin harvests
            // in-game sprites), so defer it out of OnEnable, which may run at the main menu.
            if (!_iconSet && GameApi.IsPlaying)
            {
                NativeSkin.Harvest();   // idempotent, shared across mods
                if (NativeSkin.Available)
                {
                    _iconSet = true;
                    try { _runButton?.SetIcon(NativeSkin.Find("energy", "power", "bolt", "electric", "battery")); }
                    catch { }
                }
            }

            if (!_auto) return;
            if (_session != null && !_session.Done) return;   // run in flight, wait for it
            float now = Time.realtimeSinceStartup;
            if (_pendingAt >= 0f && now >= _pendingAt)
            {
                _pendingAt = -1f;
                RunOnce(false);
            }
            else if (now >= _nextHeartbeat)
            {
                RunOnce(false);
            }
        }

        private void QueueAuto()
        {
            if (_auto) _pendingAt = Time.realtimeSinceStartup + 3f;
        }

        private string AutoLabel => _auto ? "自动连网:开" : "自动连网:关";

        private void ToggleAuto()
        {
            _auto = !_auto;
            Config.Set("autoMode", _auto);
            Config.Save();
            try { _autoButton?.SetLabel(AutoLabel); } catch { }
            if (_auto) _nextHeartbeat = Time.realtimeSinceStartup + _interval;
            else _pendingAt = -1f;
            Ui.Toast($"自动连网已{(_auto ? "开启" : "关闭")}", ToastKind.Info);
            Log.Info("auto mode " + (_auto ? "on" : "off"));
        }

        /// <summary>Crash-diagnostic trace: one line per cable operation, flushed to disk
        /// immediately so it survives a hard crash that loses all buffered log output.</summary>
        private string TracePath
        {
            get
            {
                try { return Ctx != null ? Path.Combine(Ctx.ModDirectory, "trace.log") : null; }
                catch { return null; }
            }
        }

        private void RunOnce(bool manual)
        {
            if (_session != null && !_session.Done) return;   // a run is already in flight
            try
            {
                _sessionManual = manual;
                _session = GameEnergy.BeginRun(
                    optimizeExisting: manual && _optimize,   // rebuilds are manual-only (design §4.4)
                    gainPct: _gainPct,
                    connectPoles: _connectPoles,
                    mergePowered: _merge,
                    verbose: _verbose,
                    log: m => Log.Info(m),
                    warnLog: m => Log.Warn(m),
                    tracePath: TracePath,
                    maxOpsPerStep: _connectsPerTick,
                    manual: manual);

                _nextHeartbeat = Time.realtimeSinceStartup + _interval;

                // Blocked or nothing to do: the session is born Done, report right away.
                if (_session.Done) ReportRun(_session.Result, manual);
            }
            catch (Exception e)
            {
                Log.Error("auto-connect failed", e);
            }
        }

        private void ReportRun(EnergyRunResult res, bool manual)
        {
            if (res == null) return;
            if (res.Blocked && !manual) return;

            if (manual || res.HasChanges)
            {
                string text = res.SummaryText();
                if (!manual && res.HasChanges) text = "自动连网：" + text;   // design §4.3
                float now = Time.realtimeSinceStartup;
                // Manual runs bypass the 5s same-text dedupe; the summary log line is
                // emitted exactly when the toast is (design §5: no toast-duplicate lines).
                if (manual || text != _lastToast || now - _lastToastAt > 5f)
                {
                    Ui.Toast(text, res.HasChanges ? ToastKind.Success : ToastKind.Info);
                    _lastToast = text;
                    _lastToastAt = now;
                    Log.Info("run: " + res.LogLine());
                }
            }
        }
    }
}
