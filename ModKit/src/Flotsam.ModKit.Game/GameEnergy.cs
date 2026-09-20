using System;
using System.Collections.Generic;
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

        public bool HasChanges => AddedCables + RemovedCables + MergedCables > 0;

        public string SummaryText()
        {
            if (Blocked) return "当前无法连网（未进存档/地图打开/暂停）";
            if (!HasChanges)
                return Unreachable > 0
                    ? $"电网已完整；{Unreachable} 座建筑超出线长（需要电线杆）"
                    : "电网已完整，无需连接";
            var sb = new StringBuilder();
            if (RemovedCables > 0) sb.Append($"整理移除 {RemovedCables} 根、");
            if (ConnectedBuildings > 0) sb.Append($"接入 {ConnectedBuildings} 座建筑、");
            sb.Append($"新增 {AddedCables} 根电缆");
            if (Rebuilt) sb.Append($"（总长 -{GainPct:0.#}%）");
            if (MergedCables > 0) sb.Append($"，互济合并 {MergedCables} 处");
            if (Unreachable > 0) sb.Append($"；{Unreachable} 座超距无法连接");
            return sb.ToString();
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
            return sb.ToString();
        }
    }

    /// <summary>
    /// Energy-grid snapshot + auto-connect runner. The only place touching game energy types;
    /// all planning math lives in EnergyPlanner (pure, tested in tools/EnergyPlanner.Tests).
    /// Every connection re-checks the game's own legality rules right before it is made, and
    /// every call is defensive: nothing throws during scene transitions.
    /// </summary>
    public static class GameEnergy
    {
        public static bool Ready => GameApi.IsPlaying && !GameApi.IsMapOpen && !GameApi.IsPaused;

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

        private sealed class Snapshot
        {
            public readonly List<EnergyGridConnector> Connectors = new List<EnergyGridConnector>();
            public readonly List<PlannerEdge> Existing = new List<PlannerEdge>();
            public PlannerNode[] Nodes = new PlannerNode[0];
            public PlannerGrid[] Grids = new PlannerGrid[0];
            public float Range;
        }

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
                bool isBuilding = c is EnergyGridBuildableComponent;
                snap.Nodes[i] = new PlannerNode
                {
                    Id = i,
                    X = positions[i].x,
                    Z = positions[i].z,
                    Capacity = capacities[i],
                    Used = used,
                    Kind = isBuilding ? PlannerKind.Building : PlannerKind.Pole,
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

        /// <summary>
        /// Runs the full one-key pipeline: snapshot → plan (incremental, or rebuild when
        /// optimizeExisting and the gain is worth it) → remove → add → smart-merge.
        /// Cable visuals, grid merges and save persistence are all the game's own code paths
        /// (EnergyGrid.Connect/Disconnect dispatch the native events).
        /// </summary>
        public static EnergyRunResult RunAutoConnect(bool optimizeExisting, float gainPct, bool connectPoles,
                                                     bool mergePowered, bool verbose, Action<string> log)
        {
            var res = new EnergyRunResult();
            if (!Ready) { res.Blocked = true; return res; }

            Snapshot snap;
            try { snap = TakeSnapshot(verbose ? log : null); }
            catch (Exception e)
            {
                if (log != null) log("snapshot failed: " + e.Message);
                res.Blocked = true;
                return res;
            }
            if (snap == null || snap.Nodes.Length == 0) return res;

            PlannerResult plan;
            try
            {
                plan = EnergyPlanner.Plan(snap.Nodes, snap.Grids, snap.Existing, snap.Range,
                                          optimizeExisting, gainPct, connectPoles, mergePowered);
            }
            catch (Exception e)
            {
                if (log != null) log("plan failed: " + e.Message);
                return res;
            }

            res.Rebuilt = plan.Rebuild;
            res.RebuildSkipReason = plan.RebuildSkipReason;
            res.Unreachable = plan.Unreachable.Count;
            if (plan.ExistingTotal > 0f) res.GainPct = plan.Gain / plan.ExistingTotal * 100f;
            if (verbose)
                log($"plan: nodes={snap.Nodes.Length} grids={snap.Grids.Length} existing={snap.Existing.Count} " +
                    $"add={plan.Add.Count} remove={plan.Remove.Count} merge={plan.Merge.Count} " +
                    $"unreachable={plan.Unreachable.Count}");

            var touched = new HashSet<int>();

            // Removals first: they free the slots the rebuild needs.
            foreach (var e in plan.Remove)
            {
                if (TryDisconnect(snap, e))
                {
                    res.RemovedCables++;
                    if (verbose) log("drop " + Describe(snap, e));
                }
            }
            foreach (var e in plan.Add)
            {
                if (TryConnect(snap, e))
                {
                    res.AddedCables++;
                    CountBuildings(snap, e, touched);
                    if (verbose) log("link " + Describe(snap, e));
                }
                else if (verbose) log("skip(legality) " + Describe(snap, e));
            }
            foreach (var e in plan.Merge)
            {
                if (TryConnect(snap, e))
                {
                    res.MergedCables++;
                    if (verbose) log("merge " + Describe(snap, e));
                }
            }
            res.ConnectedBuildings = touched.Count;
            return res;
        }

        private static void CountBuildings(Snapshot snap, PlannerEdge e, HashSet<int> set)
        {
            if (snap.Nodes[e.A].Kind == PlannerKind.Building && snap.Nodes[e.A].Used == 0) set.Add(e.A);
            if (snap.Nodes[e.B].Kind == PlannerKind.Building && snap.Nodes[e.B].Used == 0) set.Add(e.B);
        }

        private static string Describe(Snapshot snap, PlannerEdge e)
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

        private static bool TryConnect(Snapshot snap, PlannerEdge e)
        {
            try
            {
                if (!LegalNow(snap, e)) return false;
                EnergyGrid.Connect(snap.Connectors[e.A], snap.Connectors[e.B]);
                return true;
            }
            catch { return false; }
        }

        private static bool TryDisconnect(Snapshot snap, PlannerEdge e)
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
