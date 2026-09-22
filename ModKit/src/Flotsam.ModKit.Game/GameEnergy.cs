using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    public sealed class EnergyRunResult
    {
        public int AddedCables;
        public int RemovedCables;
        public int MergedCables;
        public int ConnectedBuildings;
        public int Unreachable;
        public bool Rebuilt;
        public float GainPct;
        public string RebuildSkipReason = "";
        /// <summary>Game state did not allow running (no save / map open / paused).</summary>
        public bool Blocked;
        /// <summary>Energy-storage buildables (batteries) NOT on the townheart grid after the
        /// run: they are powered but add no ship range. Live-state count, filled at Done.</summary>
        public int StoragesOffTownheart;

        public bool HasChanges => AddedCables + RemovedCables + MergedCables > 0;

        public string SummaryText()
        {
            if (Blocked) return "当前无法连网（未进存档/地图打开/暂停）";
            string body;
            if (!HasChanges)
            {
                body = Unreachable > 0
                    ? $"电网已完整；{Unreachable} 座建筑超出线长（需要电线杆）"
                    : "电网已完整，无需连接";
            }
            else
            {
                var sb = new StringBuilder();
                if (RemovedCables > 0) sb.Append($"整理移除 {RemovedCables} 根、");
                if (ConnectedBuildings > 0) sb.Append($"接入 {ConnectedBuildings} 座建筑、");
                if (AddedCables > 0 || MergedCables == 0)
                {
                    sb.Append($"新增 {AddedCables} 根电缆");
                    if (Rebuilt) sb.Append($"（总长 -{GainPct:0.#}%）");
                    if (MergedCables > 0) sb.Append($"，互济合并 {MergedCables} 处");
                }
                else
                {
                    sb.Append($"互济合并 {MergedCables} 处");
                    if (Rebuilt) sb.Append($"（总长 -{GainPct:0.#}%）");
                }
                if (Unreachable > 0) sb.Append($"；{Unreachable} 座超距无法连接");
                body = sb.ToString();
            }
            if (StoragesOffTownheart > 0)
                body += $"；提示：{StoragesOffTownheart} 个电池未接入船只电网（不增续航）";
            return body;
        }

        public string LogLine()
        {
            var sb = new StringBuilder();
            sb.Append("added=").Append(AddedCables)
              .Append(" removed=").Append(RemovedCables)
              .Append(" merged=").Append(MergedCables)
              .Append(" buildings=").Append(ConnectedBuildings)
              .Append(" unreachable=").Append(Unreachable)
              .Append(" rebuild=").Append(Rebuilt ? "yes" : "no");
            if (Rebuilt) sb.Append(" gain=").Append(GainPct.ToString("0.#")).Append('%');
            if (!string.IsNullOrEmpty(RebuildSkipReason)) sb.Append(" rebuildSkip=").Append(RebuildSkipReason);
            if (StoragesOffTownheart > 0) sb.Append(" storagesOff=").Append(StoragesOffTownheart);
            return sb.ToString();
        }
    }

    /// <summary>
    /// One spread-out auto-connect run: BeginRun plans everything up front, then Step drains
    /// the operation queue a few cables per tick so a big plan never fires dozens of
    /// Connect/Disconnect calls inside a single frame (suspected hard-crash trigger).
    /// Every operation writes a trace line to disk BEFORE it executes — each line opens and
    /// closes the file, so even a hard crash (which loses the BepInEx/Unity log buffers)
    /// leaves the exact connector pair that killed the game on disk.
    /// </summary>
    public sealed class EnergyRunSession
    {
        internal enum OpKind { Remove = 0, Add = 1, Merge = 2 }

        internal struct Op
        {
            public OpKind Kind;
            public PlannerEdge Edge;
        }

        /// <summary>A session that cannot finish in this wall-clock time is abandoned.</summary>
        private const float TimeoutSeconds = 120f;

        private readonly GameEnergy.Snapshot _snap;
        private readonly List<Op> _ops;
        private readonly EnergyRunResult _res;
        private readonly bool _verbose;
        private readonly Action<string> _log;
        private readonly Action<string> _warn;
        private readonly string _tracePath;
        private readonly bool _traceStarted;
        private readonly HashSet<int> _touched = new HashSet<int>();
        private readonly float _startedAt;
        private int _next;
        private bool _done;

        /// <summary>True once the whole queue has been executed (or the session was abandoned).</summary>
        public bool Done => _done;
        public EnergyRunResult Result => _res;

        internal EnergyRunSession(GameEnergy.Snapshot snap, List<Op> ops, EnergyRunResult res,
                                  bool verbose, Action<string> log, Action<string> warn,
                                  string tracePath, bool traceStarted)
        {
            _snap = snap;
            _ops = ops ?? new List<Op>();
            _res = res;
            _verbose = verbose;
            _log = log;
            _warn = warn;
            _tracePath = tracePath;
            _traceStarted = traceStarted;
            _startedAt = Time.realtimeSinceStartup;
            if (_ops.Count == 0) Finish();
        }

        /// <summary>Execute at most maxOps queued operations. Re-checks GameEnergy.Ready
        /// before every single one; when the game stops being ready the queue simply waits
        /// for a later tick. Abandons the rest of the queue after TimeoutSeconds.</summary>
        public void Step(int maxOps)
        {
            if (_done) return;
            if (maxOps < 1) maxOps = 1;

            if (Time.realtimeSinceStartup - _startedAt > TimeoutSeconds)
            {
                int dropped = _ops.Count - _next;
                _warn?.Invoke($"auto-connect session timed out ({TimeoutSeconds:0}s), dropping {dropped} pending ops");
                if (_traceStarted)
                    GameEnergy.TraceLine(_tracePath, $"timeout after {TimeoutSeconds:0}s, dropping {dropped} pending ops");
                Finish();
                return;
            }

            int executed = 0;
            while (_next < _ops.Count && executed < maxOps)
            {
                if (!GameEnergy.Ready) return;   // paused/map open/left save: resume next tick
                Execute(_ops[_next]);
                _next++;
                executed++;
            }
            if (_next >= _ops.Count) Finish();
        }

        private void Execute(Op op)
        {
            string desc = GameEnergy.Describe(_snap, op.Edge);
            bool ok;
            switch (op.Kind)
            {
                case OpKind.Remove:
                    GameEnergy.TraceLine(_tracePath, "pre drop " + desc);
                    ok = GameEnergy.TryDisconnect(_snap, op.Edge);
                    GameEnergy.TraceLine(_tracePath, ok ? "post ok" : "post skip(not-connected)");
                    if (ok)
                    {
                        _res.RemovedCables++;
                        if (_verbose) _log?.Invoke("drop " + desc);
                    }
                    break;
                case OpKind.Add:
                    GameEnergy.TraceLine(_tracePath, "pre connect " + desc);
                    ok = GameEnergy.TryConnect(_snap, op.Edge);
                    GameEnergy.TraceLine(_tracePath, ok ? "post ok" : "post skip(legality)");
                    if (ok)
                    {
                        _res.AddedCables++;
                        GameEnergy.CountBuildings(_snap, op.Edge, _touched);
                        _res.ConnectedBuildings = _touched.Count;
                        if (_verbose) _log?.Invoke("link " + desc);
                    }
                    else if (_verbose) _log?.Invoke("skip(legality) " + desc);
                    break;
                default:
                    GameEnergy.TraceLine(_tracePath, "pre merge " + desc);
                    ok = GameEnergy.TryConnect(_snap, op.Edge);
                    GameEnergy.TraceLine(_tracePath, ok ? "post ok" : "post skip(legality)");
                    if (ok)
                    {
                        _res.MergedCables++;
                        if (_verbose) _log?.Invoke("merge " + desc);
                    }
                    break;
            }
        }

        private void Finish()
        {
            _res.ConnectedBuildings = _touched.Count;
            // Live-state check AFTER all connects/merges: which batteries ended up off the
            // ship grid (powered by some island → no range contribution).
            _res.StoragesOffTownheart = GameEnergy.CountStoragesOffTownheart(_snap, _verbose, _log);
            if (_traceStarted) GameEnergy.TraceLine(_tracePath, "run end " + _res.LogLine());
            _done = true;
        }
    }

    /// <summary>
    /// Energy-grid snapshot + auto-connect planner. The only place touching game energy types;
    /// all planning math lives in EnergyPlanner (pure, tested in tools/EnergyPlanner.Tests).
    /// Every connection re-checks the game's own legality rules right before it is made, and
    /// every call is defensive: nothing throws during scene transitions.
    /// </summary>
    public static class GameEnergy
    {
        public static bool Ready => GameApi.IsPlaying && !GameApi.IsMapOpen && !GameApi.IsPaused;

        /// <summary>Trace files are capped at this size; older content is dropped wholesale.</summary>
        private const long TraceMaxBytes = 1024 * 1024;

        /// <summary>BuildableSettings.CableLinkRange — the game's own max cable length.</summary>
        public static float CableLinkRange()
        {
            try
            {
                var gs = GameManager.Settings;
                return gs != null && gs.BuildableSettings != null ? gs.BuildableSettings.CableLinkRange : 0f;
            }
            catch { return 0f; }
        }

        private static Vector3 Level(Vector3 p) { return new Vector3(p.x, 0f, p.z); }

        private static Vector3 ConnPos(EnergyGridConnector c)
        {
            var t = c.ConnectionTransform != null ? c.ConnectionTransform : c.transform;
            return Level(t.position);
        }

        private static float HorizDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        internal sealed class Snapshot
        {
            public readonly List<EnergyGridConnector> Connectors = new List<EnergyGridConnector>();
            public readonly List<PlannerEdge> Existing = new List<PlannerEdge>();
            public PlannerNode[] Nodes = new PlannerNode[0];
            public PlannerGrid[] Grids = new PlannerGrid[0];
            public float Range;
        }

        // ------------------------------------------------------------- crash trace
        // One File.AppendAllText per line = open/flush/close per line: slow-ish but the
        // volume is tiny and it survives hard crashes that lose all buffered log output.

        internal static void TraceLine(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Truncates an oversized trace file, then writes the run-start line.</summary>
        internal static void TraceRunStart(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > TraceMaxBytes)
                    File.WriteAllText(path, "trace truncated" + Environment.NewLine);
            }
            catch { }
            TraceLine(path, line);
        }

        // ------------------------------------------------------------- snapshot

        private static Snapshot TakeSnapshot(Action<string> log)
        {
            float range = CableLinkRange();
            if (range <= 0f)
            {
                if (log != null) log("snapshot: CableLinkRange unavailable");
                return null;
            }

            var snap = new Snapshot { Range = range };
            var gridList = new List<EnergyGrid>();
            var gridIndex = new Dictionary<EnergyGrid, int>();
            var index = new Dictionary<EnergyGridConnector, int>();
            var positions = new List<Vector3>();
            var capacities = new List<int>();

            foreach (var grid in EnergyGridManager.Grids)
            {
                if (grid == null || grid.Links == null) continue;
                if (!gridIndex.ContainsKey(grid))
                {
                    gridIndex[grid] = gridList.Count;
                    gridList.Add(grid);
                }
                foreach (var c in grid.Links)
                {
                    if (c == null || index.ContainsKey(c)) continue;
                    int cap = 0;
                    try { cap = c.Connections != null ? c.Connections.Length : 0; } catch { }
                    if (cap <= 0) continue;
                    index[c] = snap.Connectors.Count;
                    snap.Connectors.Add(c);
                    Vector3 p = Vector3.zero;
                    try { p = ConnPos(c); } catch { }
                    positions.Add(p);
                    capacities.Add(cap);
                }
            }

            int n = snap.Connectors.Count;
            snap.Nodes = new PlannerNode[n];
            for (int i = 0; i < n; i++)
            {
                var c = snap.Connectors[i];
                int used = 0;
                int gridId = -1;
                try
                {
                    var conns = c.Connections;
                    if (conns != null)
                        for (int j = 0; j < conns.Length; j++)
                            if (conns[j] != null) used++;
                    if (c.EnergyGrid != null && gridIndex.TryGetValue(c.EnergyGrid, out var g)) gridId = g;
                }
                catch { }
                snap.Nodes[i] = new PlannerNode
                {
                    Id = i,
                    X = positions[i].x,
                    Z = positions[i].z,
                    Capacity = capacities[i],
                    Used = used,
                    Kind = KindOf(c),
                    GridId = gridId,
                };
            }

            var seen = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                EnergyGridConnector[] conns;
                try { conns = snap.Connectors[i].Connections; } catch { continue; }
                if (conns == null) continue;
                for (int j = 0; j < conns.Length; j++)
                {
                    var other = conns[j];
                    if (other == null) continue;
                    if (!index.TryGetValue(other, out int k)) continue;
                    if (!seen.Add(PlannerEdge.EdgeKey(i, k))) continue;
                    snap.Existing.Add(new PlannerEdge
                    {
                        A = i,
                        B = k,
                        Length = HorizDist(positions[i], positions[k]),
                        Exists = true,
                    });
                }
            }

            snap.Grids = new PlannerGrid[gridList.Count];
            for (int g = 0; g < gridList.Count; g++)
            {
                var grid = gridList[g];
                var pg = new PlannerGrid();
                try
                {
                    pg.IsTownheart = grid.IsTownheartGrid;
                    pg.Powered = grid.IsTownheartGrid
                                 || grid.ReturnEnergyProduction() > 0f
                                 || grid.ReturnStorageEnergy() > 0f;
                    pg.Deficient = grid.GridEfficiency < 0.999f;
                    pg.Surplus = !pg.Deficient
                                 && (grid.ReturnEnergyProduction() >= grid.ReturnEnergyRequirement()
                                     || grid.ReturnStorageEnergy() > 0f);
                }
                catch { }
                snap.Grids[g] = pg;
            }
            return snap;
        }

        /// <summary>Pole buildables ALSO carry an EnergyGridBuildableComponent, so the old
        /// `is EnergyGridBuildableComponent` check misclassified poles as buildings (breaking
        /// tie-break and pole-pruning semantics). EnergyGridPole lives on the same GameObject
        /// as its connector (decompile 20885/20912: Initialize does
        /// Connector = GetComponent&lt;EnergyGridConnector&gt;()), so probe it first.</summary>
        private static PlannerKind KindOf(EnergyGridConnector c)
        {
            try
            {
                if (c.GetComponent<EnergyGridPole>() != null) return PlannerKind.Pole;
            }
            catch { }
            return c is EnergyGridBuildableComponent ? PlannerKind.Building : PlannerKind.Pole;
        }

        // ------------------------------------------------------------- run

        /// <summary>
        /// Plans the full one-key pipeline: snapshot → plan (incremental, or rebuild when
        /// optimizeExisting and the gain is worth it) → operation queue (Remove all → Add →
        /// Merge). Returns immediately WITHOUT touching any cable; drain the queue with
        /// EnergyRunSession.Step over the following ticks. Cable visuals, grid merges and
        /// save persistence are all the game's own code paths (EnergyGrid.Connect/Disconnect
        /// dispatch the native events). Blocked/empty plans come back as already-Done
        /// sessions carrying the appropriate Result.
        /// </summary>
        public static EnergyRunSession BeginRun(bool optimizeExisting, float gainPct, bool connectPoles,
                                                bool mergePowered, bool verbose,
                                                Action<string> log, Action<string> warnLog,
                                                string tracePath, int maxOpsPerStep, bool manual = false)
        {
            var res = new EnergyRunResult();
            Action<string> warn = warnLog ?? log;

            if (!Ready)
            {
                res.Blocked = true;
                return new EnergyRunSession(null, null, res, verbose, log, warn, tracePath, false);
            }

            Snapshot snap;
            try { snap = TakeSnapshot(verbose ? log : null); }
            catch (Exception e)
            {
                warn?.Invoke("snapshot failed: " + e.Message);
                res.Blocked = true;
                return new EnergyRunSession(null, null, res, verbose, log, warn, tracePath, false);
            }
            // Snapshot unavailable (no CableLinkRange / snapshot exception): report as blocked
            // so a manual run never toasts "grid complete" on a failure. A genuinely empty
            // snapshot (no connectors at all) keeps the old "nothing to do" semantics.
            if (snap == null)
            {
                res.Blocked = true;
                return new EnergyRunSession(null, null, res, verbose, log, warn, tracePath, false);
            }
            if (snap.Nodes.Length == 0)
                return new EnergyRunSession(null, null, res, verbose, log, warn, tracePath, false);
            // Design §5 Warn level: large snapshots make the O(n²) candidate scan noticeable.
            if (snap.Connectors.Count > 500)
                warn?.Invoke($"snapshot: {snap.Connectors.Count} connectors (>500)，候选边扫描为 O(n²)、未做空间分桶，" +
                             "一键连网可能短暂卡顿");

            PlannerResult plan;
            try
            {
                plan = EnergyPlanner.Plan(snap.Nodes, snap.Grids, snap.Existing, snap.Range,
                                          optimizeExisting, gainPct, connectPoles, mergePowered);
            }
            catch (Exception e)
            {
                warn?.Invoke("plan failed: " + e.Message);
                res.Blocked = true;
                return new EnergyRunSession(null, null, res, verbose, log, warn, tracePath, false);
            }

            res.Rebuilt = plan.Rebuild;
            res.RebuildSkipReason = plan.RebuildSkipReason;
            res.Unreachable = plan.Unreachable.Count;
            res.GainPct = plan.RebuildBaseline > 0f ? plan.Gain / plan.RebuildBaseline * 100f : 0f;
            if (verbose)
                log?.Invoke($"plan: nodes={snap.Nodes.Length} grids={snap.Grids.Length} existing={snap.Existing.Count} " +
                            $"add={plan.Add.Count} remove={plan.Remove.Count} merge={plan.Merge.Count} " +
                            $"unreachable={plan.Unreachable.Count} toTownheart={plan.AddedToTownheart}");

            var ops = new List<EnergyRunSession.Op>(plan.Remove.Count + plan.Add.Count + plan.Merge.Count);
            foreach (var e in plan.Remove) ops.Add(new EnergyRunSession.Op { Kind = EnergyRunSession.OpKind.Remove, Edge = e });
            foreach (var e in plan.Add) ops.Add(new EnergyRunSession.Op { Kind = EnergyRunSession.OpKind.Add, Edge = e });
            foreach (var e in plan.Merge) ops.Add(new EnergyRunSession.Op { Kind = EnergyRunSession.OpKind.Merge, Edge = e });

            // Trace only real work (or a manual run, which the user explicitly asked for);
            // auto-mode heartbeats with nothing to do must not touch the file.
            bool traceStarted = ops.Count > 0 || manual;
            if (traceStarted)
                TraceRunStart(tracePath,
                    $"run start ({(manual ? "manual" : "auto")}) nodes={snap.Nodes.Length} existing={snap.Existing.Count} " +
                    $"add={plan.Add.Count} remove={plan.Remove.Count} merge={plan.Merge.Count} " +
                    $"unreachable={plan.Unreachable.Count} rebuild={(plan.Rebuild ? "yes" : "no")}");

            return new EnergyRunSession(snap, ops, res, verbose, log, warn, tracePath, traceStarted);
        }

        // ------------------------------------------------------------- execution helpers

        internal static void CountBuildings(Snapshot snap, PlannerEdge e, HashSet<int> set)
        {
            if (snap.Nodes[e.A].Kind == PlannerKind.Building && snap.Nodes[e.A].Used == 0) set.Add(e.A);
            if (snap.Nodes[e.B].Kind == PlannerKind.Building && snap.Nodes[e.B].Used == 0) set.Add(e.B);
        }

        /// <summary>Counts energy-storage buildables (batteries) whose LIVE grid is not the
        /// townheart grid — they may be powered by an isolated island, which contributes
        /// nothing to ship range. Reads connector.EnergyGrid after execution (grids have
        /// merged by then), never the stale snapshot values. Fully defensive.</summary>
        internal static int CountStoragesOffTownheart(Snapshot snap, bool verbose, Action<string> log)
        {
            if (snap == null) return 0;
            int count = 0;
            for (int i = 0; i < snap.Connectors.Count; i++)
            {
                try
                {
                    var bc = snap.Connectors[i] as EnergyGridBuildableComponent;
                    if (bc == null || bc.Buildable == null) continue;
                    if (!bc.Buildable.TryReturnBuildableExtendable<EnergyStorage>(out var storage)) continue;
                    var conn = storage != null ? storage.Connector : null;
                    var grid = conn != null ? conn.EnergyGrid : null;
                    if (grid == null || !grid.IsTownheartGrid)
                    {
                        count++;
                        if (verbose) log?.Invoke("storage off townheart: " + NameOf(snap, i));
                    }
                }
                catch { }
            }
            return count;
        }

        internal static string Describe(Snapshot snap, PlannerEdge e)
        {
            return NameOf(snap, e.A) + " <-> " + NameOf(snap, e.B) + " d=" + e.Length.ToString("0.#");
        }

        private static string NameOf(Snapshot snap, int id)
        {
            try
            {
                var c = snap.Connectors[id];
                var bc = c as EnergyGridBuildableComponent;
                if (bc != null && bc.Buildable != null) return bc.Buildable.Name ?? ("building#" + id);
                return c != null ? c.GetType().Name + "#" + id : "null#" + id;
            }
            catch { return "#" + id; }
        }

        /// <summary>Re-checks the game's own legality rules (EnergyGridConnectCursorProperties)
        /// against live state right before touching anything.</summary>
        private static bool LegalNow(Snapshot snap, PlannerEdge e)
        {
            try
            {
                var a = snap.Connectors[e.A];
                var b = snap.Connectors[e.B];
                if (a == null || b == null) return false;
                if (!a.CanConnect() || !b.CanConnect()) return false;
                if (a.IsConnected(b)) return false;
                if (HorizDist(ConnPos(a), ConnPos(b)) >= snap.Range) return false;
                return true;
            }
            catch { return false; }
        }

        internal static bool TryConnect(Snapshot snap, PlannerEdge e)
        {
            try
            {
                if (!LegalNow(snap, e)) return false;
                EnergyGrid.Connect(snap.Connectors[e.A], snap.Connectors[e.B]);
                return true;
            }
            catch { return false; }
        }

        internal static bool TryDisconnect(Snapshot snap, PlannerEdge e)
        {
            try
            {
                var a = snap.Connectors[e.A];
                var b = snap.Connectors[e.B];
                if (a == null || b == null || !a.IsConnected(b)) return false;
                EnergyGrid.Disconnect(a, b);
                return true;
            }
            catch { return false; }
        }
    }
}
