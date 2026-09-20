using System;
using System.Collections.Generic;

namespace FlotsamModKit.Game
{
    /// <summary>Connector classification for planning: buildings are targets, poles are relays.</summary>
    public enum PlannerKind { Building = 0, Pole = 1 }

    /// <summary>One energy-grid connector flattened to plain data (no game types).</summary>
    public struct PlannerNode
    {
        public int Id;
        public float X;
        public float Z;
        /// <summary>Total cable slots.</summary>
        public int Capacity;
        /// <summary>Slots already occupied by existing connections.</summary>
        public int Used;
        public PlannerKind Kind;
        /// <summary>Index into the PlannerGrid array; -1 when unknown.</summary>
        public int GridId;
    }

    /// <summary>One EnergyGrid flattened to plain data.</summary>
    public struct PlannerGrid
    {
        /// <summary>Townheart grid, or produces, or holds energy.</summary>
        public bool Powered;
        /// <summary>GridEfficiency below 1 (demand exceeds supply).</summary>
        public bool Deficient;
        /// <summary>Not deficient and has spare production or storage.</summary>
        public bool Surplus;
    }

    /// <summary>A possible or existing cable between two nodes.</summary>
    public struct PlannerEdge
    {
        public int A;
        public int B;
        public float Length;
        /// <summary>True when the cable already exists in game.</summary>
        public bool Exists;

        public long Key => EdgeKey(A, B);

        public static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }
    }

    public sealed class PlannerResult
    {
        /// <summary>Cables to create (never contains existing ones).</summary>
        public readonly List<PlannerEdge> Add = new List<PlannerEdge>();
        /// <summary>Existing cables to remove (rebuild mode only).</summary>
        public readonly List<PlannerEdge> Remove = new List<PlannerEdge>();
        /// <summary>Cables merging deficient powered grids into surplus ones.</summary>
        public readonly List<PlannerEdge> Merge = new List<PlannerEdge>();
        /// <summary>Ids of buildings that stay without power after the plan.</summary>
        public readonly List<int> Unreachable = new List<int>();
        public float ExistingTotal;
        /// <summary>Existing + incremental additions (the "do not rebuild" baseline).</summary>
        public float BaselineTotal;
        /// <summary>Gain denominator when a rebuild was accepted: removable existing length
        /// (island-preserved cables excluded) + incremental additions.</summary>
        public float RebuildBaseline;
        public float PlanTotal;
        public float Gain;
        public bool Rebuild;
        public string RebuildSkipReason = "";
    }

    /// <summary>
    /// Pure cable-planning algorithms over plain data. Mirrors the game's own legality rules
    /// (EnergyGridConnectCursorProperties, decompile 90218): horizontal distance below
    /// CableLinkRange, both ends have a free slot, not already connected. No Unity or game
    /// types appear here, so the logic is testable outside the game (tools/EnergyPlanner.Tests).
    /// </summary>
    public static class EnergyPlanner
    {
        /// <summary>Existing cables count as this much shorter so ties keep the player's wiring.</summary>
        public const float KeepBonus = 0.02f;
        /// <summary>A rebuild needs at least this much absolute gain, in world units.</summary>
        public const float MinGainAbsolute = 5f;

        public static float Distance(PlannerNode a, PlannerNode b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>All legal cable candidates, sorted by length (ties by key) for determinism.</summary>
        public static List<PlannerEdge> CandidateEdges(PlannerNode[] nodes, float range)
        {
            var edges = new List<PlannerEdge>();
            if (nodes == null || range <= 0f) return edges;
            float r2 = range * range;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Capacity <= 0 || nodes[i].GridId < 0) continue;
                for (int j = i + 1; j < nodes.Length; j++)
                {
                    if (nodes[j].Capacity <= 0 || nodes[j].GridId < 0) continue;
                    float dx = nodes[i].X - nodes[j].X;
                    float dz = nodes[i].Z - nodes[j].Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < r2)
                        edges.Add(new PlannerEdge { A = i, B = j, Length = (float)Math.Sqrt(d2) });
                }
            }
            edges.Sort(CompareByLengthThenKey);
            return edges;
        }

        private static int CompareByLengthThenKey(PlannerEdge x, PlannerEdge y)
        {
            int c = x.Length.CompareTo(y.Length);
            return c != 0 ? c : x.Key.CompareTo(y.Key);
        }

        public static PlannerResult Plan(PlannerNode[] nodes, PlannerGrid[] grids, IList<PlannerEdge> existing,
                                         float range, bool optimizeExisting, float gainPct,
                                         bool connectPoles, bool mergePowered)
        {
            var res = new PlannerResult();
            if (nodes == null || nodes.Length == 0 || grids == null || grids.Length == 0 || range <= 0f)
            {
                res.RebuildSkipReason = "no-data";
                return res;
            }

            var exist = new HashSet<long>();
            if (existing != null)
                foreach (var e in existing)
                    if (exist.Add(PlannerEdge.EdgeKey(e.A, e.B)))
                        res.ExistingTotal += e.Length;

            var cand = CandidateEdges(nodes, range);
            for (int i = 0; i < cand.Count; i++)
            {
                var e = cand[i];
                e.Exists = exist.Contains(e.Key);
                cand[i] = e;
            }

            // Incremental plan: never touch existing cables.
            var inc = GrowIncremental(nodes, grids, cand, connectPoles);
            float incAdd = 0f;
            foreach (var e in inc) incAdd += e.Length;
            res.BaselineTotal = res.ExistingTotal + incAdd;
            res.PlanTotal = res.BaselineTotal;

            var chosen = inc;
            bool[] inNet = null;

            // Rebuild plan: full optimum, applied only when the gain beats the churn threshold.
            if (optimizeExisting && exist.Count > 0)
            {
                List<PlannerEdge> reb;
                bool[] net;
                string why;
                if (TryRebuild(nodes, grids, cand, connectPoles, out reb, out net, out why))
                {
                    float rebTotal = 0f;
                    foreach (var e in reb) rebTotal += e.Length;
                    // Existing cables with an end outside the rebuilt network are island-
                    // preserved (never removed), so their length is not savable: counting it
                    // would inflate Gain with a phantom the rebuild can never cash in.
                    float retainedTotal = 0f;
                    var retainedSeen = new HashSet<long>();
                    if (existing != null)
                        foreach (var e in existing)
                        {
                            if (!retainedSeen.Add(PlannerEdge.EdgeKey(e.A, e.B))) continue;
                            if (!(net[e.A] && net[e.B])) retainedTotal += e.Length;
                        }
                    float removableTotal = res.ExistingTotal - retainedTotal;
                    res.Gain = (removableTotal + incAdd) - rebTotal;
                    float threshold = Math.Max(removableTotal * gainPct / 100f, MinGainAbsolute);
                    if (res.Gain >= threshold)
                    {
                        chosen = reb;
                        inNet = net;
                        res.Rebuild = true;
                        res.RebuildBaseline = removableTotal + incAdd;
                        res.PlanTotal = rebTotal;
                    }
                    else res.RebuildSkipReason = "gain<" + threshold.ToString("0.#");
                }
                else res.RebuildSkipReason = why;
            }

            var chosenKeys = new HashSet<long>();
            foreach (var e in chosen) chosenKeys.Add(e.Key);

            if (res.Rebuild)
            {
                foreach (var e in chosen)
                    if (!exist.Contains(e.Key)) res.Add.Add(e);
                if (existing != null)
                    foreach (var e in existing)
                    {
                        long k = PlannerEdge.EdgeKey(e.A, e.B);
                        if (chosenKeys.Contains(k)) continue;
                        // Only drop cables whose BOTH ends stay inside the rebuilt network;
                        // wiring of untouched (unpowered) islands is preserved.
                        if (inNet != null && inNet[e.A] && inNet[e.B])
                        {
                            var rem = e;
                            rem.Exists = true;
                            rem.Length = Distance(nodes[e.A], nodes[e.B]);
                            res.Remove.Add(rem);
                        }
                    }
            }
            else
            {
                res.Add.AddRange(chosen);
            }

            // Buildings still without power after Remove/Add (merges do not change reachability).
            var removeKeys = new HashSet<long>();
            foreach (var e in res.Remove) removeKeys.Add(e.Key);
            var uf = new UnionFind(nodes.Length);
            if (existing != null)
                foreach (var e in existing)
                    if (!removeKeys.Contains(PlannerEdge.EdgeKey(e.A, e.B)))
                        uf.Union(e.A, e.B);
            foreach (var e in res.Add) uf.Union(e.A, e.B);
            var compPowered = new Dictionary<int, bool>();
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i].GridId >= 0 && grids[nodes[i].GridId].Powered)
                    compPowered[uf.Find(i)] = true;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Kind != PlannerKind.Building) continue;
                bool ok = nodes[i].GridId >= 0 && grids[nodes[i].GridId].Powered;
                if (!ok) compPowered.TryGetValue(uf.Find(i), out ok);
                if (!ok) res.Unreachable.Add(i);
            }

            if (mergePowered)
                SmartMerge(nodes, grids, cand, existing, removeKeys, res);

            return res;
        }

        private static bool HasFreeSlot(PlannerNode n, int planned)
        {
            return n.Capacity - n.Used - planned > 0;
        }

        /// <summary>Length tolerance for treating two candidate edges as equal-length ties.</summary>
        private const float TieEpsilon = 1e-3f;

        /// <summary>Design §4.2.1 tie-break among equal-length eligible edges: the outside
        /// (not-yet-connected) end with more free slots wins, then Building over Pole, then
        /// the lower edge key. Returns true when the candidate beats the current best.</summary>
        private static bool TieBetter(PlannerNode candOut, int candFree, long candKey,
                                      PlannerNode bestOut, int bestFree, long bestKey)
        {
            if (candFree != bestFree) return candFree > bestFree;
            bool candBuilding = candOut.Kind == PlannerKind.Building;
            bool bestBuilding = bestOut.Kind == PlannerKind.Building;
            if (candBuilding != bestBuilding) return candBuilding;
            return candKey < bestKey;
        }

        /// <summary>Component-level Prim: repeatedly join the cheapest edge between the powered
        /// network and an unpowered grid, merging whole grids exactly like EnergyGrid.Connect.</summary>
        private static List<PlannerEdge> GrowIncremental(PlannerNode[] nodes, PlannerGrid[] grids,
                                                         List<PlannerEdge> cand, bool connectPoles)
        {
            var tree = new List<PlannerEdge>();
            int ng = grids.Length;
            var parent = new int[ng];
            var powered = new bool[ng];
            for (int g = 0; g < ng; g++) { parent[g] = g; powered[g] = grids[g].Powered; }
            var used = new int[nodes.Length];

            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < cand.Count; i++)
                {
                    var e = cand[i];
                    if (e.Exists) continue;
                    int ra = Find(parent, nodes[e.A].GridId);
                    int rb = Find(parent, nodes[e.B].GridId);
                    if (ra == rb) continue;
                    if (powered[ra] == powered[rb]) continue;   // exactly one side must be powered
                    if (!HasFreeSlot(nodes[e.A], used[e.A])) continue;
                    if (!HasFreeSlot(nodes[e.B], used[e.B])) continue;

                    // Design §4.2.1 tie-break: scan the equal-length tail (cand is sorted by
                    // length) and prefer the eligible edge whose outside end has more free
                    // slots, then Building over Pole, then the lower key.
                    var best = e;
                    int bestRa = ra, bestRb = rb;
                    int bestOut = powered[ra] ? e.B : e.A;
                    int bestFree = FreeIncremental(nodes[bestOut], used[bestOut]);
                    for (int j = i + 1; j < cand.Count && cand[j].Length - e.Length <= TieEpsilon; j++)
                    {
                        var c = cand[j];
                        if (c.Exists) continue;
                        int cra = Find(parent, nodes[c.A].GridId);
                        int crb = Find(parent, nodes[c.B].GridId);
                        if (cra == crb) continue;
                        if (powered[cra] == powered[crb]) continue;
                        if (!HasFreeSlot(nodes[c.A], used[c.A])) continue;
                        if (!HasFreeSlot(nodes[c.B], used[c.B])) continue;
                        int cOut = powered[cra] ? c.B : c.A;
                        int cFree = FreeIncremental(nodes[cOut], used[cOut]);
                        if (TieBetter(nodes[cOut], cFree, c.Key, nodes[bestOut], bestFree, best.Key))
                        {
                            best = c; bestRa = cra; bestRb = crb; bestOut = cOut; bestFree = cFree;
                        }
                    }

                    tree.Add(best);
                    used[best.A]++;
                    used[best.B]++;
                    if (powered[bestRa]) parent[bestRb] = bestRa; else parent[bestRa] = bestRb;
                    progress = true;
                    break;                                       // rescan from the shortest edge
                }
            }
            if (!connectPoles) PrunePoleLeaves(nodes, tree);
            return tree;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        /// <summary>Incremental-mode free slots: capacity minus existing minus planned.</summary>
        private static int FreeIncremental(PlannerNode n, int planned)
        {
            return n.Capacity - n.Used - planned;
        }

        /// <summary>Rebuild ordering weight: existing edges count as KeepBonus shorter.</summary>
        private static float Weight(PlannerEdge e)
        {
            return e.Length * (e.Exists ? 1f - KeepBonus : 1f);
        }

        /// <summary>Iteratively drop tree edges whose leaf is a pole: poles only earn a cable
        /// when they relay towards something else.</summary>
        private static void PrunePoleLeaves(PlannerNode[] nodes, List<PlannerEdge> tree)
        {
            bool changed = true;
            while (changed && tree.Count > 0)
            {
                changed = false;
                var degree = new Dictionary<int, int>();
                foreach (var e in tree)
                {
                    degree.TryGetValue(e.A, out int da); degree[e.A] = da + 1;
                    degree.TryGetValue(e.B, out int db); degree[e.B] = db + 1;
                }
                for (int i = tree.Count - 1; i >= 0; i--)
                {
                    var e = tree[i];
                    bool aLeaf = degree[e.A] == 1 && nodes[e.A].Kind == PlannerKind.Pole;
                    bool bLeaf = degree[e.B] == 1 && nodes[e.B].Kind == PlannerKind.Pole;
                    if (aLeaf || bLeaf) { tree.RemoveAt(i); changed = true; }
                }
            }
        }

        /// <summary>Full optimum: an internal MST per powered grid (keeps every powered grid
        /// connected while dropping redundant loops), then Prim outward. Existing edges get
        /// KeepBonus so equal-length ties keep the player's wiring.</summary>
        private static bool TryRebuild(PlannerNode[] nodes, PlannerGrid[] grids, List<PlannerEdge> cand,
                                       bool connectPoles,
                                       out List<PlannerEdge> tree, out bool[] inNet, out string why)
        {
            tree = new List<PlannerEdge>();
            why = null;
            inNet = new bool[nodes.Length];
            var used = new int[nodes.Length];

            var order = new List<PlannerEdge>(cand);
            order.Sort((x, y) =>
            {
                int c = Weight(x).CompareTo(Weight(y));
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });

            // 1) internal MST per powered grid
            var gridNodes = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i < nodes.Length; i++)
            {
                int g = nodes[i].GridId;
                if (g < 0 || !grids[g].Powered) continue;
                inNet[i] = true;
                if (!gridNodes.TryGetValue(g, out var set)) { set = new HashSet<int>(); gridNodes[g] = set; }
                set.Add(i);
            }
            foreach (var kv in gridNodes)
            {
                var set = kv.Value;
                if (set.Count < 2) continue;
                var local = new UnionFind(nodes.Length);
                foreach (var e in order)
                {
                    if (!set.Contains(e.A) || !set.Contains(e.B)) continue;
                    if (local.Find(e.A) == local.Find(e.B)) continue;
                    if (used[e.A] >= nodes[e.A].Capacity || used[e.B] >= nodes[e.B].Capacity) continue;
                    tree.Add(e);
                    used[e.A]++;
                    used[e.B]++;
                    local.Union(e.A, e.B);
                }
            }

            // 2) grow outward: cheapest edge with exactly one end already in the network.
            // Note: absorbed nodes keep their island-preserved old cables, which are not
            // charged to the rebuild's slot accounting here; in extreme cases that can
            // over-schedule a slot — execution-time LegalNow re-checks every edge and
            // skips the illegal one, and the next run self-heals.
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < order.Count; i++)
                {
                    var e = order[i];
                    if (inNet[e.A] == inNet[e.B]) continue;
                    int inside = inNet[e.A] ? e.A : e.B;
                    int outside = inNet[e.A] ? e.B : e.A;
                    if (used[inside] >= nodes[inside].Capacity) continue;
                    if (used[outside] >= nodes[outside].Capacity) continue;

                    // Design §4.2.1 tie-break among equal-weight edges (order is sorted):
                    // outside end with more free slots (rebuild: Capacity - used), then
                    // Building over Pole, then lower key.
                    var best = e;
                    int bestOut = outside;
                    int bestFree = nodes[outside].Capacity - used[outside];
                    float w = Weight(e);
                    for (int j = i + 1; j < order.Count && Weight(order[j]) - w <= TieEpsilon; j++)
                    {
                        var c = order[j];
                        if (inNet[c.A] == inNet[c.B]) continue;
                        int cIn = inNet[c.A] ? c.A : c.B;
                        int cOut = inNet[c.A] ? c.B : c.A;
                        if (used[cIn] >= nodes[cIn].Capacity) continue;
                        if (used[cOut] >= nodes[cOut].Capacity) continue;
                        int cFree = nodes[cOut].Capacity - used[cOut];
                        if (TieBetter(nodes[cOut], cFree, c.Key, nodes[bestOut], bestFree, best.Key))
                        {
                            best = c; bestOut = cOut; bestFree = cFree;
                        }
                    }

                    tree.Add(best);
                    used[best.A]++;
                    used[best.B]++;
                    inNet[bestOut] = true;
                    progress = true;
                    break;
                }
            }

            if (!connectPoles) PrunePoleLeaves(nodes, tree);
            return true;
        }

        /// <summary>Connect deficient powered grids to surplus ones, shortest cable first.
        /// Uses pre-merge efficiency flags; the game recomputes efficiency after every merge.</summary>
        private static void SmartMerge(PlannerNode[] nodes, PlannerGrid[] grids, List<PlannerEdge> cand,
                                       IList<PlannerEdge> existing, HashSet<long> removeKeys,
                                       PlannerResult res)
        {
            var uf = new UnionFind(nodes.Length);
            var used = new int[nodes.Length];
            if (existing != null)
                foreach (var e in existing)
                    if (!removeKeys.Contains(PlannerEdge.EdgeKey(e.A, e.B)))
                    { uf.Union(e.A, e.B); used[e.A]++; used[e.B]++; }
            foreach (var e in res.Add) { uf.Union(e.A, e.B); used[e.A]++; used[e.B]++; }

            var deficient = new Dictionary<int, bool>();
            var surplus = new Dictionary<int, bool>();
            for (int i = 0; i < nodes.Length; i++)
            {
                int g = nodes[i].GridId;
                if (g < 0 || !grids[g].Powered) continue;
                int r = uf.Find(i);
                if (grids[g].Deficient) deficient[r] = true;
                if (grids[g].Surplus) surplus[r] = true;
            }

            var planned = new HashSet<long>();
            foreach (var e in res.Add) planned.Add(e.Key);
            if (existing != null)
                foreach (var e in existing)
                    if (!removeKeys.Contains(PlannerEdge.EdgeKey(e.A, e.B)))
                        planned.Add(PlannerEdge.EdgeKey(e.A, e.B));

            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var e in cand)
                {
                    long k = e.Key;
                    if (planned.Contains(k)) continue;
                    int ra = uf.Find(e.A), rb = uf.Find(e.B);
                    if (ra == rb) continue;
                    bool aDef = deficient.TryGetValue(ra, out var d1) && d1;
                    bool bDef = deficient.TryGetValue(rb, out var d2) && d2;
                    bool aSur = surplus.TryGetValue(ra, out var s1) && s1;
                    bool bSur = surplus.TryGetValue(rb, out var s2) && s2;
                    if (!((aDef && bSur) || (bDef && aSur))) continue;
                    if (used[e.A] >= nodes[e.A].Capacity) continue;
                    if (used[e.B] >= nodes[e.B].Capacity) continue;

                    var m = e;
                    m.Exists = false;
                    res.Merge.Add(m);
                    planned.Add(k);
                    used[e.A]++;
                    used[e.B]++;
                    uf.Union(e.A, e.B);
                    int nr = uf.Find(e.A);
                    deficient[nr] = false;
                    surplus[nr] = aSur || bSur;
                    progress = true;
                    break;
                }
            }
        }

        private sealed class UnionFind
        {
            private readonly int[] _parent;
            public UnionFind(int n) { _parent = new int[n]; for (int i = 0; i < n; i++) _parent[i] = i; }
            public int Find(int x) { while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; } return x; }
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[b] = a; }
        }
    }
}
