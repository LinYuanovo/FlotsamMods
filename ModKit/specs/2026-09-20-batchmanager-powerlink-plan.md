# 批量管理 + 一键电网 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增两个 mod：`flotsam.batchmanager`（同型号建筑批量勾选 + 拆除/升级/开关 + 世界高亮防选错）与 `flotsam.powerlink`（一键按最短电缆自动连电网 + 既有线整理 + 智能互济 + 可选自动模式）。

**Architecture:** 遵循 ModKit 既有分层——纯算法放 `EnergyPlanner.cs`（零 Unity 依赖，可在游戏外用控制台测试工程 TDD）；触碰游戏类型的代码全部收敛到 Game 层（`GameEnergy.cs`、`GameBatch.cs`）；mod 工程只做生命周期/按键/HUD/窗口。UI 全走 NativeSkin + GameUi/UiWindow 既有封装，零 Harmony 补丁。设计依据见 `ModKit/specs/2026-09-20-batchmanager-powerlink-design.md`（游戏 API 行号均指 `_mod_recon/src/Assembly-CSharp.decompiled.cs`）。

**Tech Stack:** C# 9 / netstandard2.1（mod 侧，无 NuGet，引用游戏 Managed DLL）；net8.0 控制台（算法测试，`dotnet run`）；BepInEx host api 1.1。

---

## 验证方式与 git 说明（执行前必读）

- **git（用户已确认要提交）**：仓库已建初始提交（.gitignore 排除游戏运行时/构建产物）。提交由编排者统一执行（并行子代理不碰 git，避免 index.lock 竞争），提交粒度见下方编排。
- **并行编排（用户指定：不冲突的文件并行派子代理）**：
  - **Wave 1（两个子代理并行，文件集完全不相交）**：
    - 代理 P（电网轨）：Task 1 全部（EnergyPlanner.cs + tools/EnergyPlanner.Tests，`dotnet run` 跑到 11 绿）+ Task 2 Step 2.1（GameEnergy.cs）+ Task 4 Step 4.1/4.2/4.3（PowerLink 工程文件与 mod.json）。
    - 代理 Q（批量轨）：Task 3 Step 3.1（GameBatch.cs）+ Task 5 Step 5.1/5.2/5.3/5.4（BatchManager 工程文件与 mod.json）。
    - **两代理禁令**：不跑 build.ps1、不编译 `Flotsam.ModKit.Game`/任何 mod csproj（共享工程会编到对方半成品文件；集成编译在 Wave 2）、不碰 git、不改 build.ps1/文档/他人文件。代理 P 只允许 `dotnet run --project ModKit\tools\EnergyPlanner.Tests`（该工程只链接 EnergyPlanner.cs，完全隔离）。
  - **Wave 2（编排者串行）**：改 build.ps1（$mods 加两行）→ `build.ps1 -NoInstall` 集成编译并修复所有编译错误 → 重跑算法测试 → `build.ps1` 安装 → 分轨提交（planner+tests / build.ps1 / powerlink / batchmanager）→ 规格审查 + 代码质量审查（子代理），问题回给对应轨修复。
  - **Wave 3**：Task 6 文档更新 + 提交 → 用户游戏内验收（Task 4.6/5.7/6.4 清单）。
- 编译命令统一：`pwsh -File F:\Game\Flotsam\ModKit\build.ps1 -NoInstall`（只编译全部工程）。
- 安装命令：`pwsh -File F:\Game\Flotsam\ModKit\build.ps1`（编译 + 拷 DLL 到 `Mods\`）。**安装前游戏必须关闭**（DLL 被占用会拷贝失败）。
- 游戏内验证需要人工：启动 `Flotsam.exe` 进存档操作，然后读 `F:\Game\Flotsam\BepInEx\LogOutput.log`（每次启动覆盖）。执行到这些步骤时提示用户操作，再读日志核对。
- **改代码后必须重启游戏才生效**（Mono 程序集常驻）。
- 写 .cs/.json 文件一律用编辑工具（Write/Edit），**禁止 PowerShell Set-Content 打补丁**（交接报告 §5.5 事故记录）。
- 下文各 Task 内的步骤仍按序有效，但「编译/安装/提交」类步骤归 Wave 2/3 统一执行；Task 4.4/5.5（build.ps1）由编排者在 Wave 2 一次改完。

## 文件结构总览

```
ModKit/
  build.ps1                                        [改] $mods 表加两行
  specs/2026-09-20-batchmanager-powerlink-*.md     [已有] 设计+计划
  使用说明.md                                       [改] 追加两个 mod 用法
  开发交接报告.md                                   [改] 追加 §3.5/§3.6 等
  tools/EnergyPlanner.Tests/
    EnergyPlanner.Tests.csproj                     [新] net8.0 控制台，链接 EnergyPlanner.cs
    Program.cs                                     [新] 11 个算法测试
  src/Flotsam.ModKit.Game/
    EnergyPlanner.cs                               [新] 纯算法（无 Unity/游戏类型）
    GameEnergy.cs                                  [新] 电网快照/执行器
    GameBatch.cs                                   [新] 型号分组/批量执行/高亮
  src/mods/FlotsamMod.PowerLink/
    FlotsamMod.PowerLink.csproj                    [新]
    PowerLinkMod.cs                                [新]
  src/mods/FlotsamMod.BatchManager/
    FlotsamMod.BatchManager.csproj                 [新]
    BatchManagerMod.cs                             [新]
    BatchPanel.cs                                  [新]
Mods/flotsam.powerlink/mod.json                    [新]
Mods/flotsam.batchmanager/mod.json                 [新]
```

---

### Task 1: EnergyPlanner 纯算法 + 测试工程（TDD）

**Files:**
- Create: `F:\Game\Flotsam\ModKit\tools\EnergyPlanner.Tests\EnergyPlanner.Tests.csproj`
- Create: `F:\Game\Flotsam\ModKit\tools\EnergyPlanner.Tests\Program.cs`
- Create: `F:\Game\Flotsam\ModKit\src\Flotsam.ModKit.Game\EnergyPlanner.cs`

- [ ] **Step 1.1 建测试工程文件**

`ModKit\tools\EnergyPlanner.Tests\EnergyPlanner.Tests.csproj`（在 `src\` 之外，build.ps1 不会扫到它；直接以源码链接方式编译被测文件，不引用 Unity）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>EnergyPlanner.Tests</AssemblyName>
    <RootNamespace>EnergyPlanner.Tests</RootNamespace>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\..\src\Flotsam.ModKit.Game\EnergyPlanner.cs" Link="EnergyPlanner.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 1.2 写测试（先红）**

`ModKit\tools\EnergyPlanner.Tests\Program.cs`：

```csharp
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
}
```

- [ ] **Step 1.3 建 EnergyPlanner.cs 桩（让测试编译但失败）**

`ModKit\src\Flotsam.ModKit.Game\EnergyPlanner.cs` 先只写类型与抛异常的桩：类型定义与 Step 1.5 的完整版一致，`Plan/CandidateEdges/Distance` 方法体先 `throw new NotImplementedException();`。**注意桩必须包含完整类型声明**（PlannerKind/PlannerNode/PlannerGrid/PlannerEdge/PlannerResult 按 Step 1.5 代码），否则测试编译不过。

- [ ] **Step 1.4 跑测试确认红**

```
dotnet run --project F:\Game\Flotsam\ModKit\tools\EnergyPlanner.Tests
```
预期：编译通过，输出 11 行 `FAIL ...: The method or operation is not implemented.`，退出码 1。

- [ ] **Step 1.5 实现 EnergyPlanner（绿）**

用下面完整实现替换桩（类型部分与桩相同，补全全部方法体）：

```csharp
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
        public float PlanTotal;
        public float Gain;
        public bool Rebuild;
        public string RebuildSkipReason = "";
        public bool HasWork => Add.Count > 0 || Remove.Count > 0 || Merge.Count > 0;
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
                    res.Gain = res.BaselineTotal - rebTotal;
                    float threshold = Math.Max(res.ExistingTotal * gainPct / 100f, MinGainAbsolute);
                    if (res.Gain >= threshold)
                    {
                        chosen = reb;
                        inNet = net;
                        res.Rebuild = true;
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
                    tree.Add(e);
                    used[e.A]++;
                    used[e.B]++;
                    if (powered[ra]) parent[rb] = ra; else parent[ra] = rb;
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
                float wx = x.Length * (x.Exists ? 1f - KeepBonus : 1f);
                float wy = y.Length * (y.Exists ? 1f - KeepBonus : 1f);
                int c = wx.CompareTo(wy);
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

            // 2) grow outward: cheapest edge with exactly one end already in the network
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var e in order)
                {
                    if (inNet[e.A] == inNet[e.B]) continue;
                    int inside = inNet[e.A] ? e.A : e.B;
                    int outside = inNet[e.A] ? e.B : e.A;
                    if (used[inside] >= nodes[inside].Capacity) continue;
                    if (used[outside] >= nodes[outside].Capacity) continue;
                    tree.Add(e);
                    used[inside]++;
                    used[outside]++;
                    inNet[outside] = true;
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
```

- [ ] **Step 1.6 跑测试确认绿**

```
dotnet run --project F:\Game\Flotsam\ModKit\tools\EnergyPlanner.Tests
```
预期：`11 passed, 0 failed`，退出码 0。若个别失败，修 EnergyPlanner（不是改断言），直到全绿。

- [ ] **Step 1.7 游戏工程整体编译**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1 -NoInstall
```
预期：所有工程（含 Flotsam.ModKit.Game）编译通过，无新警告/错误。

---

### Task 2: GameEnergy.cs（电网快照 + 执行器）

**Files:**
- Create: `F:\Game\Flotsam\ModKit\src\Flotsam.ModKit.Game\GameEnergy.cs`

- [ ] **Step 2.1 写 GameEnergy.cs 完整实现**

```csharp
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
```

- [ ] **Step 2.2 编译**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1 -NoInstall
```
预期：`Flotsam.ModKit.Game` 编译通过。若报 `EnergyGridBuildableComponent`/`EnergyGridManager` 等类型不存在，说明游戏版本变了——回查 `_mod_recon` 行号并调整（不应发生，1.0.1f7 已核实）。

---

### Task 3: GameBatch.cs（型号分组 + 批量执行 + 高亮）

**Files:**
- Create: `F:\Game\Flotsam\ModKit\src\Flotsam.ModKit.Game\GameBatch.cs`

- [ ] **Step 3.1 写 GameBatch.cs 完整实现**

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>One building type (shared BuildableProperties) and every instance of it.</summary>
    public sealed class TypeGroup
    {
        public BuildableProperties Props;
        public string Name = "";
        public Sprite Icon;
        public BuildableCategory Category;
        public readonly List<Buildable> Items = new List<Buildable>();
        public int Finished, Building, Busy, Upgradable, Inactive;
    }

    public enum BatchOp { Salvage, CancelSalvage, Upgrade, CancelUpgrade, Activate, Deactivate }

    public sealed class BatchOutcome
    {
        public int Ok;
        public readonly Dictionary<string, int> Skips = new Dictionary<string, int>();

        public void Skip(string reason)
        {
            Skips.TryGetValue(reason, out int n);
            Skips[reason] = n + 1;
        }

        public int TotalSkips()
        {
            int n = 0;
            foreach (var v in Skips.Values) n += v;
            return n;
        }

        public string SkipText()
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in Skips)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Value).Append(' ').Append(kv.Key);
            }
            return sb.ToString();
        }

        /// <summary>Player-facing one-liner, e.g. 已下令拆除 8 座，跳过 2 座（1 正在搬料、1 不可拆除）。</summary>
        public string Summary(string okVerb)
        {
            var sb = new StringBuilder();
            sb.Append(okVerb).Append(' ').Append(Ok).Append(" 座");
            if (Skips.Count > 0)
            {
                sb.Append("，跳过 ").Append(TotalSkips()).Append(" 座（").Append(SkipText()).Append('）');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Batch operations over player buildings. Every action is the exact call the game's own
    /// single-building buttons make (BuildableActionDeconstruct → Salvage/CancelDeconstruction,
    /// BuildableActionUpgrade → Upgrade/CancelUpgrade, BuildableActionOnOff → Activate/Deactivate,
    /// decompile 4156/4320/4246), with the same pre-checks. Never throws.
    /// </summary>
    public static class GameBatch
    {
        public static bool SupportsDeconstruct(Buildable b)
        {
            try { return b != null && b.Properties != null && b.Properties.ShowDurabilityElements; }
            catch { return false; }
        }

        public static bool SupportsUpgrade(Buildable b)
        {
            try { return b != null && b.Properties != null && b.Properties.Upgrade != null; }
            catch { return false; }
        }

        /// <summary>The game's own "show the on/off toggle" flag (decompile 17985).</summary>
        public static bool SupportsToggle(Buildable b)
        {
            try { return b != null && b.Properties != null && b.Properties.ShowActivationElement; }
            catch { return false; }
        }

        public static bool IsSalvaging(Buildable b)
        {
            try
            {
                return b != null && (b.BuildPhase == BuildPhase.SalvageShutdown
                                     || b.BuildPhase == BuildPhase.Deconstructing
                                     || (b.BuildPhase == BuildPhase.HaulTo && b.CancelConstructionAfterHaul));
            }
            catch { return false; }
        }

        public static bool IsUpgrading(Buildable b)
        {
            try
            {
                return b != null && (b.BuildPhase == BuildPhase.UpgradeShutdown
                                     || b.BuildPhase == BuildPhase.UpgradeHaulTo);
            }
            catch { return false; }
        }

        public static bool CanUpgradeNow(Buildable b)
        {
            try { return b != null && b.CanUpgrade(); }
            catch { return false; }
        }

        /// <summary>Groups by shared BuildableProperties (the type asset); unnamed fallbacks group
        /// by display name. Items keep the game's own ordering.</summary>
        public static List<TypeGroup> GroupByType(List<Buildable> all)
        {
            var byProps = new Dictionary<BuildableProperties, TypeGroup>();
            var byName = new Dictionary<string, TypeGroup>();
            var order = new List<TypeGroup>();
            if (all != null)
                foreach (var b in all)
                {
                    if (b == null) continue;
                    TypeGroup g;
                    var props = SafeProps(b);
                    if (props != null)
                    {
                        if (!byProps.TryGetValue(props, out g))
                        {
                            g = NewGroup(props, b);
                            byProps[props] = g;
                            order.Add(g);
                        }
                    }
                    else
                    {
                        string key = GameBuildings.NameOf(b);
                        if (!byName.TryGetValue(key, out g))
                        {
                            g = NewGroup(null, b);
                            g.Name = key;
                            byName[key] = g;
                            order.Add(g);
                        }
                    }
                    g.Items.Add(b);
                }

            foreach (var g in order)
            {
                GameBuildings.Sort(g.Items);
                CountStatuses(g);
            }
            order.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            return order;
        }

        private static TypeGroup NewGroup(BuildableProperties props, Buildable sample)
        {
            return new TypeGroup
            {
                Props = props,
                Name = TypeName(props, sample),
                Icon = GameBuildings.IconOf(sample),
                Category = SafeCategory(sample),
            };
        }

        /// <summary>Localized type name via the game's own I2 term, falling back to the first
        /// instance's name (which is the localized type name unless the player renamed it).</summary>
        public static string TypeName(BuildableProperties props, Buildable fallback)
        {
            try
            {
                if (props != null && !string.IsNullOrEmpty(props.LocalizedNameTerm))
                {
                    string t = I2.Loc.LocalizationManager.GetTranslation(props.LocalizedNameTerm);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { }
            try { return fallback != null ? (fallback.Name ?? "(未命名)") : "(未命名)"; }
            catch { return "(未命名)"; }
        }

        private static BuildableProperties SafeProps(Buildable b)
        {
            try { return b.Properties; } catch { return null; }
        }

        private static BuildableCategory SafeCategory(Buildable b)
        {
            try { return b.Properties != null ? b.Properties.Category : null; }
            catch { return null; }
        }

        private static void CountStatuses(TypeGroup g)
        {
            g.Finished = g.Building = g.Busy = g.Upgradable = g.Inactive = 0;
            foreach (var b in g.Items)
            {
                try
                {
                    if (IsSalvaging(b) || IsUpgrading(b)) { g.Busy++; continue; }
                    if (b.BuildPhase != BuildPhase.Finished) { g.Building++; continue; }
                    g.Finished++;
                    if (!b.IsActive) g.Inactive++;
                    if (CanUpgradeNow(b)) g.Upgradable++;
                }
                catch { }
            }
        }

        public static BatchOutcome Run(IEnumerable<Buildable> targets, BatchOp op)
        {
            var oc = new BatchOutcome();
            if (targets == null) return oc;
            foreach (var b in targets)
            {
                if (b == null) continue;
                try { RunOne(b, op, oc); }
                catch (Exception e) { oc.Skip("异常:" + e.GetType().Name); }
            }
            return oc;
        }

        private static void RunOne(Buildable b, BatchOp op, BatchOutcome oc)
        {
            switch (op)
            {
                case BatchOp.Salvage:
                    if (!SupportsDeconstruct(b)) { oc.Skip("不支持拆除"); return; }
                    if (IsSalvaging(b)) { oc.Skip("已在拆除中"); return; }
                    if (IsUpgrading(b)) { oc.Skip("升级中"); return; }
                    if (b.CanBeDeconstructed(out _)) { b.Salvage(); oc.Ok++; }
                    else oc.Skip(PhaseReason(b));
                    return;

                case BatchOp.CancelSalvage:
                    if (!IsSalvaging(b)) { oc.Skip("未在拆除中"); return; }
                    b.CancelDeconstruction();
                    oc.Ok++;
                    return;

                case BatchOp.Upgrade:
                    if (!SupportsUpgrade(b)) { oc.Skip("无升级"); return; }
                    if (IsUpgrading(b)) { oc.Skip("已在升级中"); return; }
                    if (CanUpgradeNow(b)) { b.Upgrade(); oc.Ok++; }
                    else oc.Skip("资源不足/未解锁");
                    return;

                case BatchOp.CancelUpgrade:
                    if (!IsUpgrading(b)) { oc.Skip("未在升级中"); return; }
                    b.CancelUpgrade();
                    oc.Ok++;
                    return;

                case BatchOp.Activate:
                    if (!SupportsToggle(b)) { oc.Skip("不支持开关"); return; }
                    if (b.BuildPhase != BuildPhase.Finished) { oc.Skip("未建成"); return; }
                    if (b.IsActive) { oc.Skip("已启用"); return; }
                    b.Activate();
                    oc.Ok++;
                    return;

                case BatchOp.Deactivate:
                    if (!SupportsToggle(b)) { oc.Skip("不支持开关"); return; }
                    if (b.BuildPhase != BuildPhase.Finished) { oc.Skip("未建成"); return; }
                    if (!b.IsActive) { oc.Skip("已停用"); return; }
                    b.Deactivate();
                    oc.Ok++;
                    return;
            }
        }

        private static string PhaseReason(Buildable b)
        {
            try
            {
                switch (b.BuildPhase)
                {
                    case BuildPhase.HaulFrom: return "正在搬料";
                    case BuildPhase.UpgradeHaulFrom: return "升级搬料中";
                    default: return "不可拆除";
                }
            }
            catch { return "不可拆除"; }
        }

        /// <summary>World outline via the game's own highlight (the green outline the cable
        /// cursor uses, decompile 108516 → OutlineRendererComponent.UpdateSelectedObject).</summary>
        public static void Highlight(Buildable b, bool on)
        {
            try
            {
                var outline = b != null ? b.OutlineRenderer : null;
                if (outline == null) return;
                if (on) outline.UpdateSelectedObject();
                else outline.ResetHighlightOutline();
            }
            catch { }
        }

        public static void HighlightAll(IEnumerable<Buildable> list, bool on)
        {
            if (list == null) return;
            foreach (var b in list) Highlight(b, on);
        }
    }
}
```

- [ ] **Step 3.2 编译并处理已知风险点**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1 -NoInstall
```
预期通过。两个可能的编译错误及处理：
1. `I2.Loc` 命名空间不存在 → 把 `TypeName` 里 I2 分支整体删掉，只留 `fallback.Name` 路径（设计文档允许的后备方案）。
2. `b.CancelConstructionAfterHaul` 不可访问（已核实 public，decompile 15951，理论不会出现）→ 从 `IsSalvaging` 移除该子条件。

---

### Task 4: PowerLink mod 工程 + 安装接线

**Files:**
- Create: `F:\Game\Flotsam\ModKit\src\mods\FlotsamMod.PowerLink\FlotsamMod.PowerLink.csproj`
- Create: `F:\Game\Flotsam\ModKit\src\mods\FlotsamMod.PowerLink\PowerLinkMod.cs`
- Create: `F:\Game\Flotsam\Mods\flotsam.powerlink\mod.json`
- Modify: `F:\Game\Flotsam\ModKit\build.ps1`（$mods 表）

- [ ] **Step 4.1 csproj**

`FlotsamMod.PowerLink.csproj`（照抄 BuildingFinder 模式）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>FlotsamMod.PowerLink</AssemblyName>
    <RootNamespace>FlotsamMods.PowerLink</RootNamespace>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Flotsam.ModKit.Abstractions\Flotsam.ModKit.Abstractions.csproj" Private="false" />
    <ProjectReference Include="..\..\Flotsam.ModKit.Game\Flotsam.ModKit.Game.csproj" Private="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4.2 PowerLinkMod.cs 完整实现**

```csharp
using System;
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

        private bool _running;
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
        }

        public override void OnEnable()
        {
            _runButton = Ui.AddHudButton("powerlink.run", "连电网", () => RunOnce(true),
                                         HudAnchor.RightMiddle, Config, "button");
            _runButton.Visible = true;
            _runButton.SetIcon(NativeSkin.Find("energy", "power", "bolt", "electric", "battery"));

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
            _running = false;
        }

        public override void OnTick()
        {
            if (_hotkey != null && _hotkey.IsDown && GameKeys.GetCtrlHeld()) RunOnce(true);

            if (!_auto || _running) return;
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

        private void RunOnce(bool manual)
        {
            if (_running) return;
            _running = true;
            try
            {
                var res = GameEnergy.RunAutoConnect(
                    optimizeExisting: manual && _optimize,   // rebuilds are manual-only (design §4.4)
                    gainPct: _gainPct,
                    connectPoles: _connectPoles,
                    mergePowered: _merge,
                    verbose: _verbose,
                    log: m => Log.Info(m));

                _nextHeartbeat = Time.realtimeSinceStartup + _interval;

                if (res.Blocked && !manual) return;

                if (manual || res.HasChanges)
                {
                    string text = res.SummaryText();
                    float now = Time.realtimeSinceStartup;
                    if (text != _lastToast || now - _lastToastAt > 5f)
                    {
                        Ui.Toast(text, res.HasChanges ? ToastKind.Success : ToastKind.Info);
                        _lastToast = text;
                        _lastToastAt = now;
                    }
                    Log.Info("run: " + res.LogLine());
                }
            }
            catch (Exception e)
            {
                Log.Error("auto-connect failed", e);
            }
            finally
            {
                _running = false;
            }
        }
    }
}
```

- [ ] **Step 4.3 mod.json（UTF-8）**

`F:\Game\Flotsam\Mods\flotsam.powerlink\mod.json`：

```json
{
  "schemaVersion": 1,
  "id": "flotsam.powerlink",
  "name": "一键电网",
  "version": "1.0.0",
  "author": "ModKit",
  "description": "Ctrl+2 一键把所有够得着的未供电建筑按最短电缆接入带电网络（可经已建电线杆接力）；可选整理既有连线、缺电×富余电网智能互济合并、常驻自动模式。连接由游戏原生逻辑执行并随存档保存。",
  "apiVersion": "1.1",
  "gameVersionRange": ">=1.0.0",
  "type": "code",
  "restartRequired": false,
  "entryAssembly": "FlotsamMod.PowerLink.dll",
  "entryType": "FlotsamMods.PowerLink.PowerLinkMod",
  "dependencies": [],
  "permissions": ["ui", "keybinds", "config"],
  "keybinds": [
    { "id": "powerlink.connect", "default": "Alpha2", "display": "一键连电网(Ctrl+2)" }
  ]
}
```

- [ ] **Step 4.4 build.ps1 加映射**

在 `$mods` ordered 表（`'FlotsamMod.MiniMap' = 'flotsam.minimap'` 之后）加两行（Task 6 会再加一行 BatchManager；本任务先加 PowerLink）：

```powershell
    'FlotsamMod.PowerLink'      = 'flotsam.powerlink'
```

- [ ] **Step 4.5 编译 + 安装（游戏关闭状态）**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1
```
预期：编译全绿，输出含 `-> Mods\flotsam.powerlink\FlotsamMod.PowerLink.dll`。

- [ ] **Step 4.6 游戏内验收（提示用户操作）**

请用户启动游戏进存档，然后核对：
1. 右侧 HUD 出现「连电网」「自动连网:关」两个按钮（原生皮肤、可拖拽）。
2. 有未供电建筑时按 Ctrl+2 → Toast 报接入数量；电缆出现；建筑的「未连接电网」红色故障消失。
3. 再次 Ctrl+2 → Toast「电网已完整，无需连接」（或超距提示）。
4. 点「自动连网:关」→ 标签变「自动连网:开」；建一座新用电建筑，几秒内自动接线。
5. 读 `BepInEx\LogOutput.log`：应有 `powerlink ready (...)`、每次动作一行 `run: added=... removed=...`；把 `Mods/flotsam.powerlink/config.json` 里 `verbose` 改 true 后再跑一次，应出现 `plan: nodes=...` 与逐边 `link/drop/merge` 行。
6. 存档→读档，mod 建立的连接保留。

---

### Task 5: BatchManager mod 工程（mod 类 + 完整面板）

**Files:**
- Create: `F:\Game\Flotsam\ModKit\src\mods\FlotsamMod.BatchManager\FlotsamMod.BatchManager.csproj`
- Create: `F:\Game\Flotsam\ModKit\src\mods\FlotsamMod.BatchManager\BatchManagerMod.cs`
- Create: `F:\Game\Flotsam\ModKit\src\mods\FlotsamMod.BatchManager\BatchPanel.cs`
- Create: `F:\Game\Flotsam\Mods\flotsam.batchmanager\mod.json`
- Modify: `F:\Game\Flotsam\ModKit\build.ps1`（$mods 表）

- [ ] **Step 5.1 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>FlotsamMod.BatchManager</AssemblyName>
    <RootNamespace>FlotsamMods.BatchManager</RootNamespace>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Flotsam.ModKit.Abstractions\Flotsam.ModKit.Abstractions.csproj" Private="false" />
    <ProjectReference Include="..\..\Flotsam.ModKit.Game\Flotsam.ModKit.Game.csproj" Private="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5.2 BatchManagerMod.cs**

```csharp
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using UnityEngine;

namespace FlotsamMods.BatchManager
{
    /// <summary>
    /// Batch management window: building types on the left, checkable instances on the right,
    /// batch salvage/upgrade/toggle with the game's own per-building calls. Checked buildings
    /// get the native world outline so the player can verify targets before committing.
    /// Ctrl+1 or the HUD button toggles the window.
    /// </summary>
    public sealed class BatchManagerMod : FlotsamModBase
    {
        private IKeybind _hotkey;
        private IHudButton _button;
        private GameObject _overlay;
        private BatchPanel _panel;

        internal bool HighlightChecked = true;
        internal int ConfirmThreshold = 5;
        internal float Zoom = 0.6f;
        internal bool SortByDistance;
        internal bool IncludeUnfinished = true;
        internal bool Verbose;

        internal ILog L => Log;
        internal IConfigService Cfg => Config;
        internal IUiService UiS => Ui;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _hotkey = Keybinds.Register("batch.toggle", KeyCode.Alpha1, "打开批量管理(Ctrl+1)");
            HighlightChecked = Config.Get("highlightChecked", true);
            ConfirmThreshold = Mathf.Max(1, Config.Get("confirmThreshold", 5));
            Zoom = Mathf.Clamp(Config.Get("zoomLevel", 0.6f), 0.05f, 3f);
            SortByDistance = Config.Get("sortByDistance", false);
            IncludeUnfinished = Config.Get("includeUnfinished", true);
            Verbose = Config.Get("verbose", false);
        }

        public override void OnEnable()
        {
            Events.On("BuildableBuilt", _ => MarkDirty());
            Events.On("BuildablePlaced", _ => MarkDirty());
            Events.On("BuildableSalvaged", _ => MarkDirty());
            Events.On("BuildableUpgraded", _ => MarkDirty());

            _button = Ui.AddHudButton("batchmanager.open", "批量管理", Toggle,
                                      HudAnchor.RightMiddle, Config, "button");
            _button.Visible = true;
            _button.SetIcon(NativeSkin.Find("build", "hammer", "construction"));

            Log.Info("batch manager ready");
        }

        public override void OnDisable()
        {
            try { _button?.Destroy(); } catch { }
            _button = null;
            Teardown();
            Log.Info("batch manager removed");
        }

        public override void OnGameStart()
        {
            Ui.Toast("批量管理就绪：Ctrl+1 开关；勾选即在世界中高亮，◀▶ 逐个跳转核对", ToastKind.Success);
        }

        public override void OnGameEnd() => Teardown();

        public override void OnTick()
        {
            EnsureUi();
            if (_hotkey != null && _hotkey.IsDown && GameKeys.GetCtrlHeld()) Toggle();
            try { _panel?.Tick(); } catch { }
        }

        private void EnsureUi()
        {
            if (_panel != null || !GameApi.IsPlaying) return;
            NativeSkin.Harvest();
            _overlay = Ui.CreateOverlay("batchmanager", 30500);
            _panel = new BatchPanel(this, _overlay.transform, Hide);
            Log.Info("batch window built; skin " + (NativeSkin.Available ? "native" : "procedural"));
        }

        private void Toggle()
        {
            EnsureUi();
            if (_panel == null) return;
            if (_panel.Visible) Hide();
            else _panel.Show();
        }

        private void Hide() => _panel?.Hide();

        private void MarkDirty() => _panel?.MarkDirty();

        private void Teardown()
        {
            if (_panel != null) { try { _panel.Destroy(); } catch { } }
            _panel = null;
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
        }
    }
}
```

- [ ] **Step 5.3 BatchPanel.cs（完整实现）**

```csharp
using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.BatchManager
{
    /// <summary>
    /// The batch window. Native skin throughout (UiWindow shell, native on/off icons as
    /// checkboxes, native locate icons, game's own building/category icons and TMP font).
    /// Layout: search + status + toolbar on top, type column left, instance list right,
    /// action bar and tips at the bottom (tips collapse via UiWindow.SetTips/TipsChanged).
    /// </summary>
    internal sealed class BatchPanel
    {
        private const float RowHeight = 30f;
        private const float TypeRowHeight = 28f;
        private const float LeftWidth = 236f;
        private const float TopBlock = 84f;                 // search + status + toolbar
        private const float ActionBarHeight = 36f;
        private const float TipsBlock = 48f;

        private readonly BatchManagerMod _mod;
        private readonly Action _requestClose;

        private UiWindow _window;
        private TMP_InputField _search;
        private TMP_Text _status;
        private RectTransform _leftContent;
        private RectTransform _rightContent;
        private RectTransform _leftArea;
        private RectTransform _rightArea;
        private TMP_Text _selCount;
        private TMP_Text _jumpInfo;
        private RectTransform _actionBar;
        private RectTransform _confirmBar;
        private TMP_Text _confirmLabel;
        private RectTransform _tips;

        private readonly List<TypeGroup> _groups = new List<TypeGroup>();
        private readonly List<GameObject> _leftRows = new List<GameObject>();
        private readonly List<GameObject> _rightRows = new List<GameObject>();
        private readonly List<Buildable> _rightItems = new List<Buildable>();
        private readonly Dictionary<Buildable, Image> _rowChecks = new Dictionary<Buildable, Image>();
        private readonly HashSet<Buildable> _checked = new HashSet<Buildable>();
        private readonly List<Buildable> _jumpList = new List<Buildable>();
        private readonly List<PlaceableAlertProperties> _malfunctions = new List<PlaceableAlertProperties>();

        private TypeGroup _selected;
        private string _query = "";
        private bool _dirty = true;
        private bool _visible;
        private float _nextHighlight;
        private int _jumpIndex;
        private bool _confirming;
        private float _confirmDeadline;
        private BatchOp _confirmOp;
        private string _confirmVerb = "";
        private UIState _stateBeforeTyping;
        private bool _typingPushed;

        public bool Visible => _visible;

        public BatchPanel(BatchManagerMod mod, Transform overlay, Action requestClose)
        {
            _mod = mod;
            _requestClose = requestClose;
            Build(overlay);
            _window.Visible = false;
        }

        // ------------------------------------------------------------ build

        private void Build(Transform overlay)
        {
            _window = GameUi.Window(overlay, "批量管理", new Vector2(780f, 680f), Vector2.zero,
                                    _mod.Cfg, "window", () => _requestClose(), 30f);
            var body = _window.Body.transform;

            _search = GameUi.SearchInput(body, "搜索建筑型号…", v => { _query = v ?? ""; MarkDirty(); });
            TopStrip(GameUi.Rect(_search.gameObject), 0f, 28f);
            var relay = _search.gameObject.AddComponent<InputFocusRelay>();
            relay.Selected = PushTyping;
            relay.Deselected = PopTyping;

            _status = GameUi.Label(body, "等待游戏数据…", 13, GameUi.DimText, TextAnchor.MiddleLeft);
            TopStrip(GameUi.Rect(_status.gameObject), -30f, 20f);

            BuildToolbar(body);

            // columns
            _leftArea = GameUi.Rect(GameUi.Flat(body, "LeftCol", new Color(0f, 0f, 0f, 0.10f)));
            ColumnsRect(_leftArea, 0f, LeftWidth);
            var leftScroll = GameUi.ScrollList(_leftArea, "LeftList", out _leftContent);
            GameUi.Stretch(GameUi.Rect(leftScroll.gameObject), 2f, 2f, 2f, 2f);

            _rightArea = GameUi.Rect(GameUi.Flat(body, "RightCol", new Color(0f, 0f, 0f, 0.06f)));
            ColumnsRect(_rightArea, LeftWidth + 6f, -1f);
            var rightScroll = GameUi.ScrollList(_rightArea, "RightList", out _rightContent);
            GameUi.Stretch(GameUi.Rect(rightScroll.gameObject), 2f, 2f, 2f, 2f);

            BuildActionBar(body);
            BuildConfirmBar(body);
            BuildTips(body);

            _window.TipsHeight = TipsBlock;
            _window.TipsChanged = OnTipsChanged;
            _window.SetTips(_tips);
            OnTipsChanged(true);
        }

        private void TopStrip(RectTransform rt, float y, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(0f, h);
        }

        private void ColumnsRect(RectTransform rt, float left, float widthOrNegativeOne)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(left, BottomReserve());
            rt.offsetMax = new Vector2(widthOrNegativeOne < 0f ? 0f : widthOrNegativeOne - 0f, -TopBlock);
            if (widthOrNegativeOne >= 0f)
            {
                // fixed-width left column: anchor both edges to the left
                rt.anchorMax = new Vector2(0f, 1f);
                rt.offsetMax = new Vector2(widthOrNegativeOne, -TopBlock);
            }
        }

        private float BottomReserve() => ActionBarHeight + 6f;   // tips space is added in OnTipsChanged

        private void BuildToolbar(Transform body)
        {
            var bar = GameUi.Rect(GameUi.NewUi("Toolbar", body));
            TopStrip(bar, -52f, 28f);

            float x = 2f;
            x = AddChip(bar, "全选", ref x, SelectAll);
            x = AddChip(bar, "反选", ref x, InvertAll);
            x = AddChip(bar, "清空", ref x, ClearAll);

            _selCount = GameUi.Label(bar.transform, "已选 0 座", 14, GameUi.TextColor, TextAnchor.MiddleLeft);
            var srt = GameUi.Rect(_selCount.gameObject);
            GameUi.Anchor(srt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x + 10f, 0f), new Vector2(120f, 24f));

            var prev = GameUi.TextButton(bar.transform, "◀", () => Jump(-1), 14);
            GameUi.Anchor(GameUi.Rect(prev.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-118f, 0f), new Vector2(30f, 24f));
            _jumpInfo = GameUi.Label(bar.transform, "-/-", 13, GameUi.DimText, TextAnchor.MiddleCenter);
            GameUi.Anchor(GameUi.Rect(_jumpInfo.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-64f, 0f), new Vector2(56f, 24f));
            var next = GameUi.TextButton(bar.transform, "▶", () => Jump(1), 14);
            GameUi.Anchor(GameUi.Rect(next.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-32f, 0f), new Vector2(30f, 24f));
        }

        private float AddChip(RectTransform bar, string label, ref float x, Action onClick)
        {
            var chip = GameUi.Chip(bar.transform, label, false, onClick, 13);
            GameUi.Anchor(GameUi.Rect(chip.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                          new Vector2(x, 0f), new Vector2(52f, 24f));
            x += 56f;
            return x;
        }

        private void BuildActionBar(Transform body)
        {
            _actionBar = GameUi.Rect(GameUi.NewUi("ActionBar", body));
            AnchorBar(_actionBar);

            string[] labels = { "批量拆除", "取消拆除", "批量升级", "取消升级", "启用", "停用" };
            BatchOp[] ops =
            {
                BatchOp.Salvage, BatchOp.CancelSalvage, BatchOp.Upgrade,
                BatchOp.CancelUpgrade, BatchOp.Activate, BatchOp.Deactivate,
            };
            Color?[] tints =
            {
                new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.22f),
                null, null, null, null, null,
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var btn = GameUi.TextButton(_actionBar.transform, labels[i],
                                            () => Exec(ops[idx], labels[idx], ops[idx] == BatchOp.Salvage),
                                            14, tints[idx]);
                var rt = GameUi.Rect(btn.gameObject);
                rt.anchorMin = new Vector2(idx / 6f, 0f);
                rt.anchorMax = new Vector2((idx + 1) / 6f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(3f, 3f);
                rt.offsetMax = new Vector2(-3f, -3f);
            }
        }

        private void BuildConfirmBar(Transform body)
        {
            _confirmBar = GameUi.Rect(GameUi.Flat(body, "ConfirmBar",
                                                  new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.16f)));
            AnchorBar(_confirmBar);

            _confirmLabel = GameUi.Label(_confirmBar.transform, "", 14, GameUi.Danger, TextAnchor.MiddleLeft);
            GameUi.Stretch(GameUi.Rect(_confirmLabel.gameObject), 10f, 2f, 220f, 2f);

            var no = GameUi.TextButton(_confirmBar.transform, "取消", LeaveConfirm, 14);
            GameUi.Anchor(GameUi.Rect(no.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-8f, 0f), new Vector2(90f, 28f));
            var yes = GameUi.TextButton(_confirmBar.transform, "确认执行", ConfirmNow, 14,
                                        new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.35f));
            GameUi.Anchor(GameUi.Rect(yes.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-104f, 0f), new Vector2(96f, 28f));

            _confirmBar.gameObject.SetActive(false);
        }

        private void AnchorBar(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(0f, ActionBarHeight);
        }

        private void BuildTips(Transform body)
        {
            _tips = GameUi.Rect(GameUi.Flat(body, "Tips", GameUi.SunkenBg));
            _tips.anchorMin = new Vector2(0f, 0f);
            _tips.anchorMax = new Vector2(1f, 0f);
            _tips.pivot = new Vector2(0.5f, 0f);
            _tips.sizeDelta = new Vector2(0f, TipsBlock);

            var text = GameUi.Label(_tips.transform,
                                    "勾选 = 世界内高亮描边（防选错）；◀▶ 在勾选建筑间逐个跳转核对。\n" +
                                    "批量拆除派小人执行、材料返还，可用「取消拆除」撤回；勾选数达到阈值会要求二次确认。",
                                    12, GameUi.DimText, TextAnchor.UpperLeft, wrap: true, bold: false);
            GameUi.Stretch(GameUi.Rect(text.gameObject), 10f, 5f, 10f, 5f);
        }

        private void OnTipsChanged(bool show)
        {
            if (_actionBar != null) _actionBar.anchoredPosition = new Vector2(0f, show ? TipsBlock + 4f : 4f);
            if (_confirmBar != null) _confirmBar.anchoredPosition = new Vector2(0f, show ? TipsBlock + 4f : 4f);
            if (_leftArea != null) _leftArea.offsetMin = new Vector2(_leftArea.offsetMin.x,
                                                                     ActionBarHeight + (show ? TipsBlock + 8f : 8f));
            if (_rightArea != null) _rightArea.offsetMin = new Vector2(_rightArea.offsetMin.x,
                                                                       ActionBarHeight + (show ? TipsBlock + 8f : 8f));
        }

        // ------------------------------------------------------------ lifecycle

        public void Show()
        {
            _visible = true;
            _window.Visible = true;
            _dirty = true;
        }

        public void Hide()
        {
            _visible = false;
            if (_window != null) _window.Visible = false;
            GameBatch.HighlightAll(_checked, false);
            LeaveConfirm();
            PopTyping();
        }

        public void Destroy()
        {
            GameBatch.HighlightAll(_checked, false);
            _checked.Clear();
            if (_window != null) { try { _window.Destroy(); } catch { } }
            _window = null;
        }

        public void MarkDirty() => _dirty = true;

        public void Tick()
        {
            if (_window == null) return;
            if (_dirty && _visible)
            {
                _dirty = false;
                Refresh();
            }
            if (_visible && _mod.HighlightChecked && Time.unscaledTime >= _nextHighlight)
            {
                _nextHighlight = Time.unscaledTime + 0.5f;
                GameBatch.HighlightAll(_checked, true);
            }
            if (_confirming && Time.unscaledTime > _confirmDeadline) LeaveConfirm();
        }

        // ------------------------------------------------------------ data & lists

        private void Refresh()
        {
            if (_window == null) return;
            PruneChecked();
            RebuildGroups();
            RebuildLeft();
            RebuildRight();
            UpdateToolbar();
        }

        private void PruneChecked()
        {
            List<Buildable> dead = null;
            foreach (var b in _checked)
                if (b == null) (dead ?? (dead = new List<Buildable>())).Add(b);
            if (dead != null)
                foreach (var b in dead) _checked.Remove(b);
        }

        private void RebuildGroups()
        {
            var all = GameBuildings.All();
            if (!_mod.IncludeUnfinished) all.RemoveAll(b => !GameBuildings.IsFinished(b));
            _groups.Clear();
            _groups.AddRange(GameBatch.GroupByType(all));

            var cats = GameBuildings.Categories();
            _groups.Sort((x, y) =>
            {
                int cx = x.Category != null ? cats.IndexOf(x.Category) : int.MaxValue;
                int cy = y.Category != null ? cats.IndexOf(y.Category) : int.MaxValue;
                if (cx != cy) return cx.CompareTo(cy);
                return string.CompareOrdinal(x.Name, y.Name);
            });

            if (_mod.SortByDistance)
            {
                var th = GameWorld.TownheartPosition;
                foreach (var g in _groups)
                    g.Items.Sort((a, b) => DistTo(a, th).CompareTo(DistTo(b, th)));
            }

            if (_selected != null && !_groups.Contains(_selected)) _selected = null;
            if (_status != null)
            {
                int buildings = 0;
                foreach (var g in _groups) buildings += g.Items.Count;
                _status.text = $"{_groups.Count} 个型号 / {buildings} 座建筑" +
                               (_mod.SortByDistance ? "（按距镇心排序）" : "");
            }
        }

        private static float DistTo(Buildable b, Vector3 th)
        {
            try
            {
                if (b == null) return float.MaxValue;
                var p = b.transform.position;
                float dx = p.x - th.x, dz = p.z - th.z;
                return dx * dx + dz * dz;
            }
            catch { return float.MaxValue; }
        }

        private List<TypeGroup> VisibleGroups()
        {
            var list = new List<TypeGroup>();
            string q = (_query ?? "").Trim().ToLowerInvariant();
            foreach (var g in _groups)
            {
                if (q.Length == 0
                    || (g.Name != null && g.Name.ToLowerInvariant().Contains(q))
                    || (g.Category != null && SafeCatName(g.Category).ToLowerInvariant().Contains(q)))
                    list.Add(g);
            }
            return list;
        }

        private static string SafeCatName(BuildableCategory c)
        {
            try { return c.Name.ToString(); } catch { return ""; }
        }

        private void RebuildLeft()
        {
            ClearRows(_leftRows);
            var visible = VisibleGroups();
            if (_selected == null && visible.Count > 0) _selected = visible[0];

            BuildableCategory lastCat = null;
            bool first = true;
            foreach (var g in visible)
            {
                if (first || g.Category != lastCat)
                {
                    lastCat = g.Category;
                    AddLeftHeader(SafeCatName(g.Category));
                }
                first = false;
                AddLeftRow(g);
            }
        }

        private void AddLeftHeader(string text)
        {
            var header = GameUi.Row(_leftContent, 22f, new Color(GameUi.Accent.r, GameUi.Accent.g, GameUi.Accent.b, 0.18f));
            var label = GameUi.Label(header.transform, text, 13, GameUi.Accent, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 8f, 1f, 8f, 1f);
            _leftRows.Add(header);
        }

        private void AddLeftRow(TypeGroup g)
        {
            bool selected = g == _selected;
            var row = GameUi.Row(_leftContent, TypeRowHeight, null);
            // Row() keeps the native sliced sprite white, so a selected row is tinted by
            // multiplying the Image colour (same trick as BuildingFinder's category buttons).
            if (selected)
            {
                Color c = GameUi.Accent;
                try { if (g.Category != null) c = g.Category.UIColor; } catch { }
                var rowImg = row.GetComponent<Image>();
                if (rowImg != null) rowImg.color = new Color(c.r * 0.55f + 0.45f, c.g * 0.55f + 0.45f, c.b * 0.55f + 0.45f, 1f);
            }
            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => SelectGroup(g));

            float textLeft = 8f;
            if (g.Icon != null)
            {
                var icon = GameUi.Icon(row.transform, g.Icon, 18f);
                GameUi.Anchor(GameUi.Rect(icon.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(6f, 0f), new Vector2(18f, 18f));
                textLeft = 28f;
            }

            var name = GameUi.Label(row.transform, g.Name, 13,
                                    selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            name.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(name.gameObject), textLeft, 1f, 96f, 1f);

            var count = GameUi.Label(row.transform, "×" + g.Items.Count, 13,
                                     selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleRight);
            count.raycastTarget = false;
            GameUi.Anchor(GameUi.Rect(count.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-6f, 0f), new Vector2(44f, 20f));

            string badge = StatusBadge(g);
            if (badge.Length > 0)
            {
                var badgeLabel = GameUi.Label(row.transform, badge, 11, GameUi.DimText, TextAnchor.MiddleRight);
                badgeLabel.raycastTarget = false;
                GameUi.Anchor(GameUi.Rect(badgeLabel.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(-52f, 0f), new Vector2(86f, 20f));
            }

            _leftRows.Add(row);
        }

        private static string StatusBadge(TypeGroup g)
        {
            var parts = new List<string>();
            if (g.Upgradable > 0) parts.Add("可升" + g.Upgradable);
            if (g.Busy > 0) parts.Add("拆/升中" + g.Busy);
            if (g.Building > 0) parts.Add("建造中" + g.Building);
            if (g.Inactive > 0) parts.Add("停用" + g.Inactive);
            return string.Join(" ", parts);
        }

        private void SelectGroup(TypeGroup g)
        {
            _selected = g;
            RebuildLeft();
            RebuildRight();
            UpdateToolbar();
        }

        private void RebuildRight()
        {
            ClearRows(_rightRows);
            _rightItems.Clear();
            _rowChecks.Clear();
            if (_selected == null) return;

            for (int i = 0; i < _selected.Items.Count; i++)
            {
                var b = _selected.Items[i];
                if (b == null) continue;
                _rightItems.Add(b);
                AddRightRow(b, _rightItems.Count - 1);
            }
        }

        private void AddRightRow(Buildable b, int index)
        {
            var row = GameUi.Row(_rightContent, RowHeight, index % 2 == 0 ? GameUi.RowBg : GameUi.RowBgAlt);
            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => ToggleCheck(b));

            bool on = _checked.Contains(b);
            var check = GameUi.NewUi("Check", row.transform, typeof(Image));
            var cimg = check.GetComponent<Image>();
            cimg.sprite = on ? NativeSkin.CheckOnSprite : NativeSkin.CheckOffSprite;
            cimg.preserveAspect = true;
            cimg.raycastTarget = false;
            if (cimg.sprite == null) cimg.color = on ? GameUi.Good : GameUi.DimText;
            GameUi.Anchor(GameUi.Rect(check), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                          new Vector2(7f, 0f), new Vector2(20f, 20f));
            _rowChecks[b] = cimg;

            var icon = GameBuildings.IconOf(b);
            if (icon != null)
            {
                var ii = GameUi.Icon(row.transform, icon, 20f);
                GameUi.Anchor(GameUi.Rect(ii.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(32f, 0f), new Vector2(20f, 20f));
            }

            bool finished = GameBuildings.IsFinished(b);
            var label = GameUi.Label(row.transform, GameBuildings.NameOf(b) + StatusSuffix(b), 14,
                                     finished ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 58f, 1f, 118f, 1f);

            _malfunctions.Clear();
            try
            {
                b.PopulateMalfunctions(_malfunctions,
                    finished ? PlaceableAlertProperties.AlertType.Minor
                             : PlaceableAlertProperties.AlertType.Major);
            }
            catch { }
            if (_malfunctions.Count > 0)
            {
                var warn = GameUi.Label(row.transform, "⚠ " + _malfunctions.Count, 13,
                                        new Color(0.96f, 0.78f, 0.32f), TextAnchor.MiddleRight);
                warn.raycastTarget = false;
                GameUi.Anchor(GameUi.Rect(warn.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(-34f, 0f), new Vector2(44f, 20f));
            }

            var locate = GameUi.IconButton(row.transform, NativeSkin.LocateSprite, () => Focus(b), 22f);
            GameUi.Anchor(GameUi.Rect(locate.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-6f, 0f), new Vector2(22f, 22f));

            _rightRows.Add(row);
        }

        private static string StatusSuffix(Buildable b)
        {
            try
            {
                if (GameBatch.IsSalvaging(b)) return "（拆除中）";
                if (GameBatch.IsUpgrading(b)) return "（升级中）";
                if (b.BuildPhase != BuildPhase.Finished) return "（建造中）";
                if (!b.IsActive) return "（停用）";
                return "";
            }
            catch { return ""; }
        }

        private void ClearRows(List<GameObject> rows)
        {
            foreach (var go in rows)
                if (go != null) UnityEngine.Object.Destroy(go);
            rows.Clear();
        }

        // ------------------------------------------------------------ check & jump

        private void ToggleCheck(Buildable b)
        {
            if (b == null) return;
            bool nowChecked;
            if (_checked.Contains(b)) { _checked.Remove(b); nowChecked = false; }
            else { _checked.Add(b); nowChecked = true; }

            if (_mod.HighlightChecked) GameBatch.Highlight(b, nowChecked);
            if (_rowChecks.TryGetValue(b, out var img) && img != null)
            {
                var sprite = nowChecked ? NativeSkin.CheckOnSprite : NativeSkin.CheckOffSprite;
                if (sprite != null) { img.sprite = sprite; img.color = Color.white; }
                else img.color = nowChecked ? GameUi.Good : GameUi.DimText;
            }
            UpdateToolbar();
        }

        private void SelectAll()
        {
            foreach (var b in _rightItems)
                if (b != null && _checked.Add(b) && _mod.HighlightChecked)
                    GameBatch.Highlight(b, true);
            RebuildRight();
            UpdateToolbar();
        }

        private void InvertAll()
        {
            foreach (var b in _rightItems)
            {
                if (b == null) continue;
                if (_checked.Contains(b))
                {
                    _checked.Remove(b);
                    if (_mod.HighlightChecked) GameBatch.Highlight(b, false);
                }
                else
                {
                    _checked.Add(b);
                    if (_mod.HighlightChecked) GameBatch.Highlight(b, true);
                }
            }
            RebuildRight();
            UpdateToolbar();
        }

        private void ClearAll()
        {
            if (_mod.HighlightChecked) GameBatch.HighlightAll(_checked, false);
            _checked.Clear();
            RebuildRight();
            UpdateToolbar();
        }

        private void UpdateToolbar()
        {
            if (_selCount != null) _selCount.text = "已选 " + _checked.Count + " 座";
            UpdateJumpInfo();
        }

        private void RebuildJumpList()
        {
            _jumpList.Clear();
            foreach (var b in _rightItems)
                if (b != null && _checked.Contains(b)) _jumpList.Add(b);
            if (_jumpIndex >= _jumpList.Count) _jumpIndex = 0;
        }

        private void UpdateJumpInfo()
        {
            RebuildJumpList();
            if (_jumpInfo != null)
                _jumpInfo.text = _jumpList.Count > 0 ? (_jumpIndex + 1) + "/" + _jumpList.Count : "-/-";
        }

        private void Jump(int dir)
        {
            RebuildJumpList();
            if (_jumpList.Count == 0)
            {
                _mod.UiS.Toast("先勾选建筑，再用 ◀▶ 逐个跳转", ToastKind.Info);
                return;
            }
            _jumpIndex = Mathf.FloorToInt(Mathf.Repeat(_jumpIndex + dir, _jumpList.Count));
            Focus(_jumpList[_jumpIndex]);
            UpdateJumpInfo();
        }

        private void Focus(Buildable b)
        {
            if (b == null) return;
            if (!GameBuildings.Focus(b, _mod.Zoom))
                _mod.UiS.Toast("无法定位该建筑（相机不可用）", ToastKind.Warning);
        }

        // ------------------------------------------------------------ batch actions

        private void Exec(BatchOp op, string verb, bool needConfirm)
        {
            if (_checked.Count == 0)
            {
                _mod.UiS.Toast("先在右侧勾选建筑", ToastKind.Warning);
                return;
            }
            if (needConfirm && _checked.Count >= _mod.ConfirmThreshold)
            {
                EnterConfirm(op, verb);
                return;
            }
            DoRun(op, verb);
        }

        private void DoRun(BatchOp op, string verb)
        {
            var targets = new List<Buildable>(_checked);
            BatchOutcome oc;
            try { oc = GameBatch.Run(targets, op); }
            catch (Exception e) { _mod.L.Error("batch run failed", e); return; }

            _mod.UiS.Toast(oc.Summary(verb), oc.Ok > 0 ? ToastKind.Success : ToastKind.Warning);
            _mod.L.Info($"{verb}: ok={oc.Ok} skip={oc.TotalSkips()} [{oc.SkipText()}]");
            MarkDirty();
        }

        private void EnterConfirm(BatchOp op, string verb)
        {
            _confirming = true;
            _confirmOp = op;
            _confirmVerb = verb;
            _confirmDeadline = Time.unscaledTime + 10f;
            string typeName = _selected != null ? _selected.Name : "建筑";
            _confirmLabel.text = $"确认对 {_checked.Count} 座「{typeName}」执行{verb}？";
            if (_actionBar != null) _actionBar.gameObject.SetActive(false);
            if (_confirmBar != null) _confirmBar.gameObject.SetActive(true);
        }

        private void ConfirmNow()
        {
            var op = _confirmOp;
            var verb = _confirmVerb;
            LeaveConfirm();
            DoRun(op, verb);
        }

        private void LeaveConfirm()
        {
            _confirming = false;
            if (_confirmBar != null) _confirmBar.gameObject.SetActive(false);
            if (_actionBar != null) _actionBar.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------ typing state

        private void PushTyping()
        {
            try
            {
                if (_typingPushed) return;
                _stateBeforeTyping = UIManager.State;
                UIManager.SetState(UIState.Typing);
                _typingPushed = true;
            }
            catch { }
        }

        private void PopTyping()
        {
            try
            {
                if (!_typingPushed) return;
                UIManager.SetState(_stateBeforeTyping);
                _typingPushed = false;
            }
            catch { }
        }
    }
}
```

**实现注意（执行时核对，勿改语义）：**
1. `GameUi.Anchor` 的实际签名以 `GameUi.cs:163` 为准；若没有带 pivot 参数的重载，用上面 5 参形式 `(rt, anchor, pivot, anchoredPos, size)` ——Window 内部就是这么用的，两处调用保持一致即可。
2. `ColumnsRect` 里左栏是固定宽度（anchorMax.x=0 + offsetMax.x=LeftWidth），右栏 offsetMin.x=LeftWidth+6、offsetMax.x=0；如果实现时发现 GameUi.Flat 返回的 RectTransform 默认锚点干扰，按 BuildingFinder.BuildUi 152-207 行的锚点写法对齐。
3. `GameUi.Chip(parent,label,on,onClick,size)` 无定位副作用，位置由本文件的 Anchor 决定。
4. 行的点击=切换勾选；[定位] 是子级 Button，UGUI 不会把子按钮点击冒泡到行按钮。

- [ ] **Step 5.4 mod.json（UTF-8）**

`F:\Game\Flotsam\Mods\flotsam.batchmanager\mod.json`：

```json
{
  "schemaVersion": 1,
  "id": "flotsam.batchmanager",
  "name": "批量管理",
  "version": "1.0.0",
  "author": "ModKit",
  "description": "按建筑型号分组勾选多个实例，批量拆除/取消拆除、批量升级/取消升级、启用/停用；勾选即在世界中高亮描边，◀▶ 逐个跳转核对，超过阈值的批量拆除需内联二次确认。Ctrl+1 开关窗口。",
  "apiVersion": "1.1",
  "gameVersionRange": ">=1.0.0",
  "type": "code",
  "restartRequired": false,
  "entryAssembly": "FlotsamMod.BatchManager.dll",
  "entryType": "FlotsamMods.BatchManager.BatchManagerMod",
  "dependencies": [],
  "permissions": ["ui", "keybinds", "config"],
  "keybinds": [
    { "id": "batch.toggle", "default": "Alpha1", "display": "打开批量管理(Ctrl+1)" }
  ]
}
```

- [ ] **Step 5.5 build.ps1 加映射**

`$mods` 表加：

```powershell
    'FlotsamMod.BatchManager'   = 'flotsam.batchmanager'
```

- [ ] **Step 5.6 编译 + 安装（游戏关闭）**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1
```
预期：全绿 + `-> Mods\flotsam.batchmanager\FlotsamMod.BatchManager.dll`。

- [ ] **Step 5.7 游戏内验收（提示用户操作）**

1. Ctrl+1 / HUD「批量管理」开窗口；左栏型号分组数、数量与建筑总览一致；选中型号右栏列实例。
2. 勾选一行 → 世界中该建筑出现描边高亮；取消勾选消失；关窗全部消失。
3. ◀▶ 在勾选建筑间跳转（相机定位 + 打开原生面板），行内 [定位] 同样生效。
4. 全选/反选/清空正确；搜索能过滤型号。
5. 勾 2-3 座点「批量拆除」→ 直接执行，小人去拆，Toast 汇总正确；勾 ≥5 座 → 出现红色确认条，「取消」或 10s 超时退回；「确认执行」才拆。「取消拆除」能撤回。
6. 资源足够时「批量升级」进入升级流程；不足跳过并计入 Toast；「启用/停用」对机器类建筑生效，对不支持的建筑显示「跳过 n 不支持开关」。
7. 隐藏提示（标题栏对勾钮）→ 窗口收起且操作栏下方无空白带。
8. 日志：`batch manager ready`、`batch window built`、每次批量一行 `批量拆除: ok=… skip=… […]`；无每帧输出。
9. 退出存档再进 → 窗口重建正常、无残留高亮。

---

### Task 6: 文档更新 + 最终验收

**Files:**
- Modify: `F:\Game\Flotsam\ModKit\使用说明.md`（追加两节）
- Modify: `F:\Game\Flotsam\ModKit\开发交接报告.md`（§1 仓库结构、§3 追加 3.5/3.6、§4 追加行号、§6 待办）

- [ ] **Step 6.1 使用说明.md 追加**

在文件末尾追加（保持既有措辞风格）：

```markdown
## 批量管理（flotsam.batchmanager）v1.0.0

- 开/关：`Ctrl+1` 或右侧 HUD「批量管理」按钮。
- 左栏选型号（按游戏分类分组），右栏勾选实例；勾选 = 世界中描边高亮，`◀ ▶` 在勾选建筑间逐个跳转核对，行内 [定位] 直接飞过去。
- 操作栏：批量拆除（红）/取消拆除/批量升级/取消升级/启用/停用；拆除派小人执行、材料返还，可撤回。
- 勾选数 ≥5（配置 `confirmThreshold`）时批量拆除需内联二次确认（10 秒超时自动取消）。
- 配置：`highlightChecked`、`confirmThreshold`、`sortByDistance`（按距镇心排序）、`includeUnfinished`、`zoomLevel`、`verbose`、`window.*`、`button.*`。

## 一键电网（flotsam.powerlink）v1.0.0

- 一键连接：`Ctrl+2` 或 HUD「连电网」——把所有够得着的未供电建筑/电线杆按最短电缆接入带电网络（可用已建电线杆接力），并做缺电×富余电网的智能互济合并；结果 Toast 汇总。
- 整理：一键时若既有连线明显不是最短（收益 ≥ `optimizeGainPct`，默认 10%），会先拆后连按最优重排；微小差异不动。自动模式只做增量、不整理。
- 自动连网：HUD「自动连网:开/关」——新建筑建好后约 3 秒自动接线，另有 `autoIntervalSec` 心跳兜底。
- 超出线长（CableLinkRange）的建筑无法直连，Toast 会提示「需要电线杆」；mod 不会替你建杆。
- 配置：`autoMode`、`autoIntervalSec`、`optimizeExisting`、`optimizeGainPct`、`connectPolesToGrid`（false=孤立备用杆不拉线）、`mergePoweredGrids`、`verbose`、`button.*`、`autobutton.*`。
- mod 建立的连接与手动连接完全等价，随存档保存。
```

- [ ] **Step 6.2 开发交接报告.md 更新**

1. §1 仓库结构 `mods/` 下追加两行：
```
      FlotsamMod.BatchManager/   BatchManagerMod.cs + BatchPanel.cs
      FlotsamMod.PowerLink/      PowerLinkMod.cs
```
`Flotsam.ModKit.Game/` 文件清单追加 `GameBatch.cs GameEnergy.cs EnergyPlanner.cs`；`ModKit/` 根追加 `tools/EnergyPlanner.Tests/`（net8.0 控制台，`dotnet run --project ModKit\tools\EnergyPlanner.Tests`，11 个纯算法测试，改 EnergyPlanner 必跑）。
2. §0 表格「四个 mod」措辞改「六个 mod」；开头「对应已安装版本」行更新日期。
3. §3 追加：

```markdown
### 3.5 flotsam.batchmanager「批量管理」v1.0.0
- 按键：`Ctrl+1`（Alpha1+GetCtrlHeld 组合，KeybindService 不支持修饰键）。配置：`highlightChecked/confirmThreshold/sortByDistance/includeUnfinished/zoomLevel/verbose/window.*/button.*`。
- 型号分组 = 共享 `BuildableProperties` 引用（升级会换 Properties，事件驱动刷新兜底）；类型名走 `I2.Loc.LocalizationManager.GetTranslation(LocalizedNameTerm)`，失败退 `Buildable.Name`。
- 批量执行 = 逐个调原生同款方法（Salvage/CancelDeconstruction/Upgrade/CancelUpgrade/Activate/Deactivate，GameBatch.Run 预检+跳过原因计数）；开关支持判定 `Properties.ShowActivationElement`（decompile 17985）。
- 世界高亮 = `Buildable.OutlineRenderer.UpdateSelectedObject()/ResetHighlightOutline()`（原生电缆 cursor 同款）；**原生 OnDeselected 会 ResetHighlightOutline（17244），所以窗口可见期间 2Hz 重刷**；Hide/Destroy/GameEnd 必须全清。
- 拆除内联确认（阈值 confirmThreshold，10s 超时）；原生 PopUpDialog 无可自定义文案的通用确认 API，勿再找。

### 3.6 flotsam.powerlink「一键电网」v1.0.0
- 按键：`Ctrl+2`。HUD 两钮：连电网（手动一键）/自动连网开关（`autoMode`）。
- 算法在 `EnergyPlanner.cs`（纯数据、零 Unity 依赖，tools/EnergyPlanner.Tests 有 11 个测试）：增量=电网级 Prim（整网合并语义）；整理=每个带电网内部 MST+向外 Prim，既有边 2% keep bonus，收益 ≥ max(existing×gainPct%, 5u) 才重构；杆叶子剪枝（connectPolesToGrid=false 时）；智能互济=缺电(GridEfficiency<1)×富余才合并。
- GameEnergy.RunAutoConnect：快照 `EnergyGridManager.Grids→Links`；带电=IsTownheartGrid||发电>0||储电>0；执行序 先 Remove(EnergyGrid.Disconnect) 后 Add/Merge(EnergyGrid.Connect)；每条边执行前复核原生规则（CanConnect×2/未互连/水平距<CableLinkRange）。电缆可视、并/拆网、存档持久化全是游戏自己的事件链，mod 零补丁。
- 自动模式：BuildableBuilt/Placed 防抖 3s + autoIntervalSec 心跳；**不要订阅 EnergyGridsUpdated（自触发风暴）**；重入保护 `_running`。
- 重构干跑把槽位全视为空闲，被保留的孤岛旧线可能占槽 → 运行时 LegalNow 复核兜底，个别边 skip(legality) 属预期，verbose 可见。
```

4. §4 追加行号：
```markdown
- 电网：`EnergyGrid.Connect/Disconnect` 39166/39182；`EnergyGridConnector` 39370（IsInRange 39612 用 `CableLinkRange`）；连线合法性完整复刻自 `EnergyGridConnectCursorProperties.Connect/UpdateComponentsInRange` 90325/90417；`EnergyGridManager.Grids` 39706；建筑带电组件 `EnergyGridBuildableComponent` 20505（CanConnect 要求 Finished 20717）；电线杆 `EnergyGridPole` 20885。
- 批量：`Buildable.Salvage/CancelDeconstruction/Upgrade/CancelUpgrade/CanUpgrade/CanBeDeconstructed/Activate/Deactivate` 16718/16763/16589/16560/16643/16891/16436/16446；`ShowActivationElement` 17985；`OutlineRendererComponent.UpdateSelectedObject/ResetHighlightOutline` 165096/165178；高亮参考 `HighlightManager.HighlightObject` 108516。
```
5. §6 待办追加：
```markdown
- [ ] PowerLink 连接器 >500 时的空间分桶未做（当前 O(n²)，大图卡顿再优化）。
- [ ] BatchManager 左栏型号过多时无虚拟列表（maxRows 沿用 400 上限思路，超出截断）。
- [ ] EnergyPlanner 改动必须跑 tools/EnergyPlanner.Tests（11 tests）。
```

- [ ] **Step 6.3 最终构建 + 安装**

```
pwsh -File F:\Game\Flotsam\ModKit\build.ps1
dotnet run --project F:\Game\Flotsam\ModKit\tools\EnergyPlanner.Tests
```
预期：构建全绿；测试 `11 passed, 0 failed`。

- [ ] **Step 6.4 全量游戏验收（设计文档 §7 清单）**

提示用户重启游戏逐项过设计文档 `2026-09-20-batchmanager-powerlink-design.md` §7 验收标准 1-11，并读 `BepInEx\LogOutput.log` 核对两 mod 的日志卫生（正常操作一段后只有单行汇总，verbose 才有明细）。

---

## Self-Review 记录

- **规格覆盖**：设计 §3（Mod A 全部要素→Task 3/5）、§4（Mod B 管线含整理/互济/自动模式→Task 1/2/4）、§5（工程/素材/日志约定→各任务+Task 6 文档）、§7 验收（→Task 4.6/5.7/6.4）均有对应任务。
- **占位符**：无 TBD；所有代码步骤含完整代码；文档步骤含完整追加文本。
- **类型一致性**：`PlannerNode/PlannerGrid/PlannerEdge/PlannerResult/EnergyPlanner.Plan(...)` 签名在 Task 1 定义，Task 2 调用一致；`BatchOp/BatchOutcome/TypeGroup/GameBatch.*` 在 Task 3 定义，Task 5 调用一致；`EnergyRunResult.SummaryText/LogLine/HasChanges/Blocked` 在 Task 2 定义，Task 4 调用一致。
- **已知实现期风险**（均有兜底，见对应步骤）：I2.Loc 引用（退 Buildable.Name）、CancelConstructionAfterHaul 访问性（已核实 public，decompile 15951）、重构干跑的槽位近似（LegalNow 运行时复核兜底）。`GameUi.Anchor` 已核实为 5 参 `(rt, anchor, pivot, anchoredPos, size)`（GameUi.cs:163），计划内调用均已对齐。
