using System;
using System.Collections.Generic;
using FlotsamModKit.Game;

internal static class Program
{
    private static int _pass, _fail;

    private static int Main()
    {
        Run("T1 链式接入", T1Chain);
        Run("T2 超距不连", T2OutOfRange);
        Run("T3 槽位上限", T3Capacity);
        Run("T4 电线杆接力(剪枝仍保留)", T4PoleRelay);
        Run("T5 孤立杆按配置接入/剪除", T5LonelyPole);
        Run("T6 增量不动既有连线", T6IncrementalKeeps);
        Run("T7 重构: 星形改链形", T7RebuildStarToChain);
        Run("T8 重构: 剪冗余环", T8LoopPrune);
        Run("T9 智能互济合并", T9SmartMerge);
        Run("T10 微小收益不翻动", T10NoChurn);
        Run("T11 无电孤岛不被拆线", T11IslandPreserved);
        Run("T12 平手优先空槽多的端点", T12TieFreeSlots);
        Run("T13 平手优先接建筑而非杆", T13TieBuildingOverPole);

        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        try { body(); Console.WriteLine($"PASS {name}"); _pass++; }
        catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); _fail++; }
    }

    private static void Assert(bool cond, string what)
    {
        if (!cond) throw new Exception(what);
    }

    private static PlannerNode N(int id, float x, float z, int cap, int used, PlannerKind kind, int grid)
    {
        return new PlannerNode { Id = id, X = x, Z = z, Capacity = cap, Used = used, Kind = kind, GridId = grid };
    }

    private static PlannerNode B(int id, float x, int grid, int cap = 2, int used = 0)
    {
        return N(id, x, 0f, cap, used, PlannerKind.Building, grid);
    }

    private static PlannerNode P(int id, float x, int grid, int cap = 4, int used = 0)
    {
        return N(id, x, 0f, cap, used, PlannerKind.Pole, grid);
    }

    private static PlannerGrid G(bool powered, bool deficient = false, bool surplus = false)
    {
        return new PlannerGrid { Powered = powered, Deficient = deficient, Surplus = surplus };
    }

    private static PlannerEdge E(PlannerNode[] nodes, int a, int b)
    {
        return new PlannerEdge
        {
            A = a, B = b,
            Length = EnergyPlanner.Distance(nodes[a], nodes[b]),
            Exists = true,
        };
    }

    private static PlannerResult Plan(PlannerNode[] nodes, PlannerGrid[] grids, IList<PlannerEdge> existing,
                                      float range = 50f, bool optimize = false, float gainPct = 10f,
                                      bool poles = true, bool merge = true)
    {
        return EnergyPlanner.Plan(nodes, grids, existing, range, optimize, gainPct, poles, merge);
    }

    private static bool HasEdge(List<PlannerEdge> list, int a, int b)
    {
        long k = PlannerEdge.EdgeKey(a, b);
        foreach (var e in list) if (e.Key == k) return true;
        return false;
    }

    // T1: 镇心 + 三座建筑排成 40u 间距，线长上限 50 → 链式接入 3 根
    private static void T1Chain()
    {
        var nodes = new[] { B(0, 0f, 0, 4), B(1, 40f, 1), B(2, 80f, 2), B(3, 120f, 3) };
        var grids = new[] { G(true), G(false), G(false), G(false) };
        var res = Plan(nodes, grids, null);
        Assert(res.Add.Count == 3, $"add={res.Add.Count} want 3");
        Assert(HasEdge(res.Add, 0, 1) && HasEdge(res.Add, 1, 2) && HasEdge(res.Add, 2, 3), "want chain 0-1-2-3");
        Assert(res.Unreachable.Count == 0, "all reachable");
        Assert(res.Remove.Count == 0 && res.Merge.Count == 0, "nothing else");
    }

    // T2: 建筑在 200u 外 → 不连、计入 unreachable
    private static void T2OutOfRange()
    {
        var nodes = new[] { B(0, 0f, 0, 4), B(1, 200f, 1) };
        var grids = new[] { G(true), G(false) };
        var res = Plan(nodes, grids, null);
        Assert(res.Add.Count == 0, "no edge");
        Assert(res.Unreachable.Count == 1 && res.Unreachable[0] == 1, "b1 unreachable");
    }

    // T3: 镇心只有 1 个槽，两座等距建筑 → 只连 id 小的（确定性平手规则）
    private static void T3Capacity()
    {
        var nodes = new[] { B(0, 0f, 0, 1), B(1, 30f, 1), B(2, -30f, 2) };
        var grids = new[] { G(true), G(false), G(false) };
        var res = Plan(nodes, grids, null);
        Assert(res.Add.Count == 1, $"add={res.Add.Count} want 1");
        Assert(HasEdge(res.Add, 0, 1), "tie goes to lower id");
        Assert(res.Unreachable.Count == 1 && res.Unreachable[0] == 2, "b2 unreachable");
    }

    // T4: 建筑只够得着杆、杆够得着镇心 → 杆做接力，connectPoles=false 也不剪（不是叶子）
    private static void T4PoleRelay()
    {
        var nodes = new[] { B(0, 0f, 0, 4), P(1, 40f, 1), B(2, 80f, 2) };
        var grids = new[] { G(true), G(false), G(false) };
        var res = Plan(nodes, grids, null, poles: false);
        Assert(res.Add.Count == 2, $"add={res.Add.Count} want 2");
        Assert(HasEdge(res.Add, 0, 1) && HasEdge(res.Add, 1, 2), "relay kept");
        Assert(res.Unreachable.Count == 0, "reachable via pole");
    }

    // T5: 孤立备用杆 → connectPoles=true 接入；false 剪掉
    private static void T5LonelyPole()
    {
        var nodes = new[] { B(0, 0f, 0, 4), P(1, 40f, 1) };
        var grids = new[] { G(true), G(false) };
        var withPoles = Plan(nodes, grids, null, poles: true);
        Assert(withPoles.Add.Count == 1 && HasEdge(withPoles.Add, 0, 1), "pole connected when enabled");
        var noPoles = Plan(nodes, grids, null, poles: false);
        Assert(noPoles.Add.Count == 0, "lonely pole pruned");
    }

    // T6: 增量模式不动既有连线；新建筑从最近的已连建筑接出
    private static void T6IncrementalKeeps()
    {
        var nodes = new[] { B(0, 0f, 0, 4, 1), B(1, 20f, 0, 2, 1), B(2, 30f, 1) };
        var grids = new[] { G(true), G(false) };
        var existing = new List<PlannerEdge> { E(nodes, 0, 1) };
        var res = Plan(nodes, grids, existing);
        Assert(res.Add.Count == 1 && HasEdge(res.Add, 1, 2), "shortest add 1-2 (10u)");
        Assert(res.Remove.Count == 0, "incremental never removes");
        Assert(!res.Rebuild, "no rebuild");
        Assert(res.Unreachable.Count == 0, "all powered");
    }

    // T7: 星形(镇心直连两座)改链形更短 → 重构：拆 0-2、补 1-2
    private static void T7RebuildStarToChain()
    {
        var nodes = new[] { B(0, 0f, 0, 4, 2), B(1, 20f, 0, 2, 1), B(2, 40f, 0, 2, 1) };
        var grids = new[] { G(true) };
        var existing = new List<PlannerEdge> { E(nodes, 0, 1), E(nodes, 0, 2) };
        var res = Plan(nodes, grids, existing, optimize: true, gainPct: 10f);
        Assert(res.Rebuild, $"rebuild expected, gain={res.Gain} reason={res.RebuildSkipReason}");
        Assert(res.Add.Count == 1 && HasEdge(res.Add, 1, 2), "add 1-2");
        Assert(res.Remove.Count == 1 && HasEdge(res.Remove, 0, 2), "remove 0-2");
        Assert(Math.Abs(res.Gain - 20f) < 0.01f, $"gain={res.Gain} want 20");
    }

    // T8: 三角形冗余环 → 重构剪掉最长的那根
    private static void T8LoopPrune()
    {
        var nodes = new[] { B(0, 0f, 0, 4, 2), B(1, 20f, 0, 2, 2), B(2, 40f, 0, 2, 2) };
        var grids = new[] { G(true) };
        var existing = new List<PlannerEdge> { E(nodes, 0, 1), E(nodes, 1, 2), E(nodes, 0, 2) };
        var res = Plan(nodes, grids, existing, optimize: true, gainPct: 10f);
        Assert(res.Rebuild, "rebuild expected");
        Assert(res.Add.Count == 0, "no new cables");
        Assert(res.Remove.Count == 1 && HasEdge(res.Remove, 0, 2), "loop edge removed");
    }

    // T9: 缺电网与富余网够得着 → 合并；都富余 → 不合并
    private static void T9SmartMerge()
    {
        var nodes = new[] { B(0, 0f, 0, 4), B(1, 30f, 1, 4) };
        var defSur = new[] { G(true, false, true), G(true, true, false) };
        var res = Plan(nodes, defSur, null, merge: true);
        Assert(res.Merge.Count == 1 && HasEdge(res.Merge, 0, 1), "deficient x surplus merges");

        var bothSur = new[] { G(true, false, true), G(true, false, true) };
        var res2 = Plan(nodes, bothSur, null, merge: true);
        Assert(res2.Merge.Count == 0, "surplus x surplus never merges");

        var res3 = Plan(nodes, defSur, null, merge: false);
        Assert(res3.Merge.Count == 0, "merge disabled");
    }

    // T10: 既有布线已是最优 → 收益 0，不翻动（keep bonus + 阈值）
    private static void T10NoChurn()
    {
        var nodes = new[]
        {
            N(0, 0f, 0f, 4, 1, PlannerKind.Building, 0),
            N(1, 30f, 0f, 2, 2, PlannerKind.Building, 0),
            N(2, 30f, 20f, 4, 1, PlannerKind.Pole, 0),
        };
        var grids = new[] { G(true) };
        var existing = new List<PlannerEdge> { E(nodes, 0, 1), E(nodes, 1, 2) };
        var res = Plan(nodes, grids, existing, optimize: true, gainPct: 10f);
        Assert(!res.Rebuild, $"no churn, gain={res.Gain}");
        Assert(res.Add.Count == 0 && res.Remove.Count == 0, "untouched");
    }

    // T11: 够不着带电网络的无电孤岛（自己内部有连线）→ 重构不得拆它的线
    private static void T11IslandPreserved()
    {
        var nodes = new[] { B(0, 0f, 0, 4), B(1, 300f, 1, 2, 1), B(2, 320f, 1, 2, 1) };
        var grids = new[] { G(true), G(false) };
        var existing = new List<PlannerEdge> { E(nodes, 1, 2) };
        var res = Plan(nodes, grids, existing, optimize: true);
        Assert(res.Remove.Count == 0, "island wiring preserved");
        Assert(res.Unreachable.Count == 2, "island counts as unreachable");
    }

    // T12: 镇心(剩1槽)两侧等长(30)候选 → 平手优先外侧端点空槽多的 B1(free2 > B2 free1)
    private static void T12TieFreeSlots()
    {
        var nodes = new[]
        {
            N(0, 0f, 0f, 4, 3, PlannerKind.Building, 0),   // 镇心：cap4 used3，仅剩 1 槽
            N(1, 30f, 0f, 2, 0, PlannerKind.Building, 1),  // B1：free2
            N(2, -30f, 0f, 4, 3, PlannerKind.Building, 2), // B2：free1
        };
        var grids = new[] { G(true), G(false), G(false) };
        var res = Plan(nodes, grids, null);
        Assert(res.Add.Count == 1 && HasEdge(res.Add, 0, 1),
            $"tie goes to the end with more free slots (B1); add={res.Add.Count}");
        Assert(res.Unreachable.Count == 1 && res.Unreachable[0] == 2, "B2 unreachable");
    }

    // T13: 镇心两侧等长(30)、外侧端点空槽相等(各2) → 规则②优先接建筑而非杆
    private static void T13TieBuildingOverPole()
    {
        var nodes = new[]
        {
            N(0, 0f, 0f, 4, 0, PlannerKind.Building, 0),   // 镇心
            N(1, 30f, 0f, 4, 2, PlannerKind.Pole, 1),      // 杆：cap4 used2 → free2
            N(2, -30f, 0f, 2, 0, PlannerKind.Building, 2), // 建筑：cap2 → free2
        };
        var grids = new[] { G(true), G(false), G(false) };
        var res = Plan(nodes, grids, null, poles: true);
        Assert(res.Add.Count >= 1 && res.Add[0].Key == PlannerEdge.EdgeKey(0, 2),
            "equal free slots → first pick must be the building edge {0,2}, not the pole");
        Assert(HasEdge(res.Add, 0, 2), "building edge present");
    }
}
