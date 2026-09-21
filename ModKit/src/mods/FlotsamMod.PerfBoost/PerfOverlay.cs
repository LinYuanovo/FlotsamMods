using System;
using System.Text;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;

namespace FlotsamMods.PerfBoost
{
    /// <summary>
    /// The F9 performance window: a single text block refreshed at 4Hz, plus a throttled
    /// one-line snapshot into the mod log so a play session can be analysed afterwards.
    /// Built lazily once a save is loaded (native skin only exists there), torn down on
    /// game end — same lifecycle as SailHud.
    /// </summary>
    public sealed class PerfOverlay : MonoBehaviour
    {
        public PerfBoostMod Mod;

        private GameObject _overlay;
        private UiWindow _window;
        private TMP_Text _text;
        private readonly StringBuilder _sb = new StringBuilder(640);

        private bool _built;
        private float _nextRefresh;
        private float _nextLog;
        private float _nextBuildRetry;

        public void Build()
        {
            if (_built || Mod == null) return;
            if (Time.unscaledTime < _nextBuildRetry) return;
            if (!GameApi.IsPlaying) return;   // retried from Update until a save is running
            NativeSkin.Harvest();

            try
            {
                _overlay = Mod.Context.Ui.CreateOverlay("perfboost", 30500);
                _window = GameUi.Window(_overlay.transform, "性能面板",
                                        new Vector2(330f, 640f), new Vector2(720f, 240f),
                                        Mod.Context.Config, "window", () => Mod.SetOverlayVisible(false), 28f);
                _window.ContentMode = WindowContentMode.None;
                _window.MinSize = new Vector2(250f, 280f);
                _window.ApplyLayout();

                _text = GameUi.Label(_window.Body, "…", 13, GameUi.TextColor,
                                     TextAnchor.UpperLeft, true, bold: false);
                var trt = GameUi.Rect(_text.gameObject);
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(8f, 8f);
                trt.offsetMax = new Vector2(-8f, -8f);

                // Frame counters may only flow while the profiler engine takes frames —
                // force it on (config-gated) BEFORE creating the taps.
                if (Mod.ForceProfilerCfg)
                    GamePerf.EnsureProfilerStats(m => Mod.Context.Log.Info(m));

                string dump = Mod.Probe.Start(Mod.Verbose);
                _built = true;
                ApplyVisibility();
                Refresh();
                Mod.Context.Log.Info("perf overlay built — " + Mod.Probe.ValidReport());
                if (!string.IsNullOrEmpty(dump))
                    Mod.Context.Log.Info(dump);
            }
            catch (Exception e)
            {
                Mod.Context.Log.Error("perf overlay build failed: " + e.Message);
                // no per-frame retry spam / no orphaned overlay canvas on a failed build
                _nextBuildRetry = Time.unscaledTime + 2f;
                try { if (_overlay != null) UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
                _window = null;
                _text = null;
                _built = false;
            }
        }

        private void Update()
        {
            if (Mod == null) return;
            if (!_built)
            {
                Build();
                return;
            }

            Mod.Probe.Tick();
            if (GameApi.IsPlaying) GamePerf.HotspotFrame();
            ApplyVisibility();

            if (GameApi.IsPlaying && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + Mod.LogIntervalSec;
                LogSnapshot();
            }

            if (_window == null || !_window.Visible) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Refresh();
        }

        private void ApplyVisibility()
        {
            if (_window == null) return;
            bool want = Mod != null && Mod.ShowOverlay && GameApi.IsPlaying;
            if (_window.Visible != want) _window.Visible = want;
        }

        private void Refresh()
        {
            if (_text == null || Mod == null) return;
            Mod.Probe.Sample();

            var sb = _sb;
            sb.Length = 0;
            sb.Append(Mod.Probe.Fps.ToString("0.0")).Append(" FPS  (")
              .Append(Mod.Probe.FrameMs.ToString("0.0")).Append(" ms)   1%低 ")
              .Append(Mod.Probe.Low1Fps.ToString("0")).Append('\n');

            foreach (var tap in Mod.Probe.Taps)
                sb.Append(tap.Label).Append("  ").Append(tap.Format()).Append('\n');

            if (GamePerf.Hotspots.Count > 0)
            {
                double frames = GamePerf.HotspotFrames;
                double sumNs = 0;
                int shown = 0;
                sb.Append("— 脚本热点 (均值/单次峰值 ms) —\n");
                foreach (var h in GamePerf.Hotspots)
                {
                    sumNs += h.TotalNs;
                    double avg = frames > 0 ? h.TotalNs / 1e6 / frames : 0.0;
                    double max = h.MaxNs / 1e6;
                    // Rows with no signal are dropped (same rule as the log line) so the
                    // panel stays compact; their cost still counts in the total below.
                    if (avg < 0.05 && max < 1.0) continue;
                    sb.Append(h.Label).Append("  ").Append(avg.ToString("0.00"))
                      .Append(" / ").Append(max.ToString("0.0")).Append('\n');
                    shown++;
                }
                if (shown == 0) sb.Append("(当前帧无显著脚本热点)\n");
                sb.Append("脚本合计  ").Append(frames > 0 ? (sumNs / 1e6 / frames).ToString("0.00") : "0.00").Append('\n');
            }

            sb.Append("建筑 ").Append(CountBuildings())
              .Append(" · 小人 ").Append(CountAgents())
              .Append(" · 粒子瘦身 ").Append(GamePerf.ScaledParticleCount)
              .Append(" · 动画 ").Append(GamePerf.CulledAnimatorCount)
              .Append("(冻").Append(GamePerf.FrozenAnimatorCount).Append(')')
              .Append('\n');

            sb.Append(GamePerf.MemoryLine()).Append("  ·  ").Append(GamePerf.GcCountsLine()).Append('\n');

            sb.Append(Mod.OptimizationsSummary());
            _text.text = sb.ToString();
        }

        private void LogSnapshot()
        {
            if (Mod == null) return;
            Mod.Probe.Sample();
            var sb = _sb;
            sb.Length = 0;
            sb.Append("fps ").Append(Mod.Probe.Fps.ToString("0.0"))
              .Append(" (").Append(Mod.Probe.FrameMs.ToString("0.0")).Append("ms, 1%低 ")
              .Append(Mod.Probe.Low1Fps.ToString("0")).Append(')');
            foreach (var tap in Mod.Probe.Taps)
            {
                if (!tap.Valid) continue;
                sb.Append(" | ").Append(tap.Label).Append(' ').Append(tap.FormatLog());
            }
            if (GamePerf.Hotspots.Count > 0)
                sb.Append(" | 脚本 ").Append(GamePerf.HotspotLogLine());
            sb.Append(" | ").Append(GamePerf.MemoryLine());
            sb.Append(" | ").Append(GamePerf.GcCountsLine());
            sb.Append(" | 建筑 ").Append(CountBuildings())
              .Append(" 小人 ").Append(CountAgents());
            Mod.Context.Log.Info(sb.ToString());
            // New averaging window starts right after the snapshot.
            GamePerf.HotspotReset();
        }

        private static int CountBuildings()
        {
            try
            {
                var c = GameApi.PlayerCommunity;
                if (c == null || c.Buildables == null) return -1;
                return c.Buildables.Count;
            }
            catch { return -1; }
        }

        private static int CountAgents()
        {
            try
            {
                var c = GameApi.PlayerCommunity;
                if (c == null || c.Agents == null) return -1;
                return c.Agents.Count;
            }
            catch { return -1; }
        }

        /// <summary>Drops the window: harvested sprites die with the scene; rebuilt next save.</summary>
        public void Teardown()
        {
            if (_window != null) _window.Destroy();
            _window = null;
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
            _text = null;
            _built = false;
        }

        private void OnDestroy()
        {
            Teardown();
        }
    }
}
