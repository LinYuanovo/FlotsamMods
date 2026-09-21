using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace FlotsamMods.PerfBoost
{
    /// <summary>
    /// Frame statistics: smoothed FPS + a 1%-low from a ring buffer of frame times, plus a set
    /// of ProfilerRecorder taps.
    ///
    /// Delivery evidence across sessions: the ONLY session that ever received real samples
    /// (18:09 v1.0.0, live Draw Call counts) was also the only one that did NOT call
    /// ProfilerRecorderHandle.GetAvailable/GetDescription — every session that enumerates
    /// the recorders first (v1.1.0~v1.3.0, all three ctor variants) gets Valid-but-forever-zero
    /// taps. So taps are now created FIRST via the name-only ctor (category auto-resolved),
    /// and the marker enumeration only runs when explicitly requested (verbose diagnostics).
    /// Script timing comes from GamePerf hotspots, not taps.
    /// </summary>
    public sealed class PerfProbe
    {
        public sealed class Tap
        {
            public string Label;
            public ProfilerRecorder Recorder;
            public bool Valid;
            public ProfilerMarkerDataUnit Unit;

            private long Read()
            {
                try { return Recorder.LastValue; } catch { return 0L; }
            }

            public string Format()
            {
                if (!Valid) return "-";
                long v = Read();
                switch (Unit)
                {
                    case ProfilerMarkerDataUnit.TimeNanoseconds: return (v / 1e6).ToString("0.00") + " ms";
                    case ProfilerMarkerDataUnit.Bytes: return (v / 1024.0 / 1024.0).ToString("0.0") + " MB";
                    default: return v.ToString("0");
                }
            }

            /// <summary>Compact form for the log line (no decimals for counts).</summary>
            public string FormatLog()
            {
                if (!Valid) return "-";
                long v = Read();
                switch (Unit)
                {
                    case ProfilerMarkerDataUnit.TimeNanoseconds: return (v / 1e6).ToString("0.00") + "ms";
                    case ProfilerMarkerDataUnit.Bytes: return (v / 1024.0 / 1024.0).ToString("0.0") + "MB";
                    default: return v.ToString("0");
                }
            }
        }

        private readonly List<Tap> _taps = new List<Tap>();
        public IReadOnlyList<Tap> Taps => _taps;

        // label -> alias names, matched exactly against the available recorders.
        // Names come from the 217-entry dump this class writes on first build (Unity
        // 6000.3.16 release player): built-in loop markers (BehaviourUpdate,
        // Physics.Processing, …) do NOT exist there — only thread/frame-time counters,
        // memory counters, render-stat counters and the URP render-graph markers. Script
        // timing is covered by GamePerf hotspots instead, not by taps.
        // NOTE: the render-stat rows (Draw Calls/SetPass/三角形/批次) read 0 under the
        // D3D12 backend (stats not fed; D3D11 works).
        // NOTE: units are declared here (time / bytes / count) because the diagnostic
        // GetDescription roundtrip is no longer part of tap creation. Time-span MARKERS
        // (the URP camera markers, shadowmap draws, culling, GC.Collect) are compiled out
        // of this release player — they report Valid but永远 zero — so the panel gets its
        // timing from GamePerf stopwatches instead. What works here are the frame-time
        // counters (main/render thread) and the native stat counters.
        private static readonly (string Label, string[] Aliases, ProfilerMarkerDataUnit Unit)[] Wanted =
        {
            ("主线程", new[] { "CPU Main Thread Frame Time" }, ProfilerMarkerDataUnit.TimeNanoseconds),
            ("渲染线程", new[] { "CPU Render Thread Frame Time" }, ProfilerMarkerDataUnit.TimeNanoseconds),
            ("GPU帧", new[] { "GPU Frame Time" }, ProfilerMarkerDataUnit.TimeNanoseconds),
            ("GC内存", new[] { "GC Used Memory" }, ProfilerMarkerDataUnit.Bytes),
            ("Draw Calls", new[] { "Draw Calls Count" }, ProfilerMarkerDataUnit.Count),
            ("SetPass", new[] { "SetPass Calls Count" }, ProfilerMarkerDataUnit.Count),
            ("三角形", new[] { "Triangles Count" }, ProfilerMarkerDataUnit.Count),
            ("批次", new[] { "Batches Count" }, ProfilerMarkerDataUnit.Count),
            ("阴影投射", new[] { "Shadow Casters Count" }, ProfilerMarkerDataUnit.Count),
            ("渲染纹理数", new[] { "Render Textures Count" }, ProfilerMarkerDataUnit.Count),
            ("渲染纹理MB", new[] { "Render Textures Bytes" }, ProfilerMarkerDataUnit.Bytes),
        };

        // ------------------------------------------------------------ fps statistics

        private const int RingSize = 240;   // ~4s at 60fps
        private readonly float[] _ring = new float[RingSize];
        private int _ringPos;
        private int _ringCount;

        public float Fps { get; private set; }
        public float FrameMs { get; private set; }
        public float Low1Fps { get; private set; }

        public void Tick()
        {
            _ring[_ringPos] = Time.unscaledDeltaTime;
            _ringPos = (_ringPos + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }

        private void ComputeStats()
        {
            int n = _ringCount;
            if (n == 0) { Fps = 0; FrameMs = 0; Low1Fps = 0; return; }
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += _ring[i];
            float avg = sum / n;
            FrameMs = avg * 1000f;
            Fps = avg > 0f ? 1f / avg : 0f;

            int worst = Math.Max(1, n / 100);
            var copy = new float[n];
            Array.Copy(_ring, copy, n);
            Array.Sort(copy);
            float wsum = 0f;
            for (int i = 0; i < worst; i++) wsum += copy[n - 1 - i];
            float wavg = wsum / worst;
            Low1Fps = wavg > 0f ? 1f / wavg : 0f;
        }

        // ------------------------------------------------------------ lifecycle

        /// <summary>Create all taps FIRST (name-only ctor, category auto-resolved — no
        /// marker enumeration beforehand). When <paramref name="enumerateForDiagnostics"/>
        /// is set (verbose), the available-marker dump is also produced for tuning.</summary>
        public string Start(bool enumerateForDiagnostics)
        {
            Stop();
            foreach (var w in Wanted)
            {
                Tap tap = new Tap { Label = w.Label, Unit = w.Unit };
                foreach (var alias in w.Aliases)
                {
                    try
                    {
                        var r = new ProfilerRecorder(alias, 1, ProfilerRecorderOptions.Default);
                        if (r.Valid)
                        {
                            // ROOT CAUSE of the five zero-data sessions found via the ctor
                            // matrix (IsRunning was false on every variant): recorders do
                            // NOT auto-start in this player — the explicit Start() is what
                            // connects them to the data flow.
                            r.Start();
                            tap.Recorder = r;
                            tap.Valid = true;
                            break;
                        }
                        try { r.Dispose(); } catch { }
                    }
                    catch { }
                }
                _taps.Add(tap);
            }
            if (!enumerateForDiagnostics) return null;
            try
            {
                var handles = new List<ProfilerRecorderHandle>(512);
                ProfilerRecorderHandle.GetAvailable(handles);
                var dump = new StringBuilder(handles.Count * 24);
                dump.Append("available recorders (").Append(handles.Count).Append("): ");
                foreach (var h in handles)
                {
                    string name;
                    try { name = ProfilerRecorderHandle.GetDescription(h).Name; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    dump.Append(name).Append(" | ");
                }
                return dump.ToString();
            }
            catch (Exception e)
            {
                return "probe enumeration failed: " + e.Message;
            }
        }

        public void Sample() => ComputeStats();

        public string ValidReport()
        {
            int ok = 0;
            var bad = new List<string>();
            foreach (var t in _taps)
            {
                if (t.Valid) ok++;
                else bad.Add(t.Label);
            }
            return ok + "/" + _taps.Count + " probes live" +
                   (bad.Count > 0 ? " — missing: " + string.Join(", ", bad.ToArray()) : "");
        }

        public void Stop()
        {
            foreach (var t in _taps)
            {
                try { if (t.Recorder.Valid) t.Recorder.Dispose(); } catch { }
            }
            _taps.Clear();
        }
    }
}
