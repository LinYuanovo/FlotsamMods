using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>Outcome of one <see cref="GameAssignments.RunAutoPriority"/> pass.</summary>
    public sealed class AutoPriorityResult
    {
        public bool Blocked;              // could not run at all (not in a save / no agents / threw)
        public string BlockReason = "";
        public int Agents;                // drifters that had at least one managed assignment
        public int TasksRanked;           // task types that actually had a skill spread to rank
        public int Changed;               // assignments whose priority tier was written
        public int High, Mid, Low;        // ...split by the tier written

        public bool HasChanges => Changed > 0;

        /// <summary>Player-facing one-liner for a toast.</summary>
        public string SummaryText()
        {
            if (Blocked) return string.IsNullOrEmpty(BlockReason) ? "无法执行" : BlockReason;
            if (Changed == 0) return "优先级已匹配技能，无需调整";
            return $"已按技能重排 {Agents} 名小人：调整 {Changed} 项（高 {High}/中 {Mid}/次 {Low}）";
        }

        /// <summary>Compact line for the log.</summary>
        public string LogLine()
        {
            if (Blocked) return "blocked: " + BlockReason;
            return $"agents={Agents} tasksRanked={TasksRanked} changed={Changed} (high={High} mid={Mid} low={Low})";
        }
    }

    /// <summary>Outcome of one <see cref="GameAssignments.RunAutoSpend"/> pass.</summary>
    public sealed class AutoSpendResult
    {
        public bool Blocked;              // could not run at all (not in a save / no agents / threw)
        public string BlockReason = "";
        public int Agents;                // drifters that received at least one point
        public int PointsSpent;

        public bool HasChanges => PointsSpent > 0;

        /// <summary>Player-facing one-liner for a toast.</summary>
        public string SummaryText()
        {
            if (Blocked) return string.IsNullOrEmpty(BlockReason) ? "无法执行" : BlockReason;
            if (PointsSpent == 0) return "没有待分配的技能点";
            return $"已为 {Agents} 名小人自动加点 {PointsSpent}（❤爱好优先，其次高等级）";
        }

        /// <summary>Compact line for the log.</summary>
        public string LogLine()
        {
            if (Blocked) return "blocked: " + BlockReason;
            return $"agents={Agents} pointsSpent={PointsSpent}";
        }
    }

    /// <summary>
    /// Skill-based colony automation: automatic task priority + automatic attribute-point spend.
    ///
    /// Every drifter (<c>Agent</c>) carries one <c>Assignment</c> per work type
    /// (<c>AssignmentType</c>) whose <c>Priority</c> is one of the game's four real tiers
    /// <c>AssignmentPriority</c> = None/Lowest/Default/Highest (the per-drifter priority box in the
    /// native UI cycles exactly these, decompile 51953/51992). Each assignment is governed by a
    /// skill attribute: <c>DrifterAttributes.ReturnAssignmentAttribute</c> (9374) maps it to an
    /// <c>AttributeType</c>, and <c>ReturnAssignmentAttributePoints</c> (9410) is the drifter's
    /// combined level for it — the very number the game weighs in
    /// <c>Assignment.ReturnPriority</c> (14016).
    ///
    /// This pass groups the player colony's drifters by task and, for each task, splits them into
    /// the three usable tiers by RELATIVE rank of that skill (best third → Highest, middle →
    /// Default, rest → Lowest, ties sharing a tier). So the most-capable drifter wins the matching
    /// job even when everyone is still low level, which is what levels the colony fastest.
    ///
    /// Safety rails: tasks with no governing attribute (Studying/Idle/etc.) are never touched, and
    /// a task is only re-ranked when there is an actual skill spread (an all-tied task is left as
    /// the player/template set it). Nothing is ever set to <c>None</c>, so no job is barred from
    /// every drifter. Writes go through <c>Agent.TryUpdateAssignmentPriority</c> (5775) — the same
    /// call the native priority box makes — and only when the tier really differs, so a steady pass
    /// is silent and dispatches no game events. Never throws.
    /// </summary>
    public static class GameAssignments
    {
        /// <summary>Normalized-rank cutoffs over distinct skill values: g&lt;=high → Highest,
        /// g&lt;=mid → Default, else Lowest. Defaults split the field into thirds.</summary>
        public const float DefaultHighBelow = 1f / 3f;
        public const float DefaultMidBelow = 2f / 3f;

        private sealed class Cell
        {
            public Agent Agent;
            public Assignment Assignment;
            public int Skill;
        }

        public static AutoPriorityResult RunAutoPriority(
            float highBelow = DefaultHighBelow,
            float midBelow = DefaultMidBelow,
            bool verbose = false,
            Action<string> log = null)
        {
            var res = new AutoPriorityResult();
            try
            {
                if (!GameApi.IsPlaying) { res.Blocked = true; res.BlockReason = "不在存档中"; return res; }

                var community = GameApi.PlayerCommunity;
                var agents = community != null ? community.Agents : null;
                if (agents == null || agents.Count == 0) { res.Blocked = true; res.BlockReason = "暂无小人"; return res; }

                float hi = Mathf.Clamp01(highBelow);
                float mid = Mathf.Clamp01(midBelow);
                if (mid < hi) mid = hi;

                // 1) gather every managed, skill-governed assignment, grouped by task type
                var byType = new Dictionary<AssignmentType, List<Cell>>();
                var counted = new HashSet<Agent>();
                for (int i = 0; i < agents.Count; i++)
                {
                    var agent = agents[i];
                    if (agent == null) continue;
                    var attrs = agent.Attributes;         // auto-props: safe to read
                    var assignments = agent.Assignments;
                    if (attrs == null || assignments == null) continue;

                    bool any = false;
                    for (int a = 0; a < assignments.Count; a++)
                    {
                        var asn = assignments[a];
                        if (asn == null || !IsManagedTask(asn.Type)) continue;
                        if (SafeAttr(attrs, asn) == DrifterAttributes.AttributeType.None) continue;

                        if (!byType.TryGetValue(asn.Type, out var cells))
                            byType[asn.Type] = cells = new List<Cell>();
                        cells.Add(new Cell { Agent = agent, Assignment = asn, Skill = SafeSkill(attrs, asn) });
                        any = true;
                    }
                    if (any) counted.Add(agent);
                }
                res.Agents = counted.Count;

                // 2) per task: rank by relative skill and write the three tiers
                foreach (var kv in byType)
                {
                    var cells = kv.Value;
                    if (cells.Count < 2) continue;                 // a lone drifter has nobody to outrank

                    var distinct = new List<int>();
                    for (int c = 0; c < cells.Count; c++)
                        if (!distinct.Contains(cells[c].Skill)) distinct.Add(cells[c].Skill);
                    if (distinct.Count < 2) continue;              // everyone tied -> no signal, leave as-is
                    distinct.Sort((x, y) => y.CompareTo(x));       // best skill first

                    int k = distinct.Count;
                    res.TasksRanked++;
                    for (int c = 0; c < cells.Count; c++)
                    {
                        var cell = cells[c];
                        float g = (float)distinct.IndexOf(cell.Skill) / (k - 1);   // 0..1, 0 = best group
                        var tier = g <= hi ? AssignmentPriority.Highest
                                 : g <= mid ? AssignmentPriority.Default
                                 : AssignmentPriority.Lowest;
                        if (cell.Assignment.Priority == tier) continue;            // already correct
                        if (!SafeSet(cell.Agent, cell.Assignment.Type, tier)) continue;

                        res.Changed++;
                        if (tier == AssignmentPriority.Highest) res.High++;
                        else if (tier == AssignmentPriority.Default) res.Mid++;
                        else res.Low++;

                        if (verbose && log != null)
                            log($"priority {Name(cell.Agent)} · {cell.Assignment.Type} skill={cell.Skill} -> {tier}");
                    }
                }
            }
            catch (Exception e)
            {
                res.Blocked = true;
                res.BlockReason = "异常:" + e.GetType().Name;
                if (log != null) log("auto-priority failed: " + e);
            }
            return res;
        }

        /// <summary>
        /// Spends every drifter's banked attribute points through the game's own call
        /// (<c>DrifterAttributes.TryLevelAttribute</c>, 9214 — exactly what the native "+" button
        /// invokes, 50725). Pick order: ❤ affinity first (<c>ReturnAffinityAmount</c> 9364, the
        /// hearts shown by the drifter panel; more hearts wins), then the attribute that is
        /// already highest (<c>ReturnTotalAttributePoints</c>) — i.e. specialize instead of
        /// spreading. The order is fixed per pass, which is equivalent to re-sorting after every
        /// point: affinity never changes and levelling only raises the chosen attribute further.
        /// A maxed attribute hands over to the next best; points nobody can take (everything
        /// maxed) stay banked, same as the vanilla UI. Never throws.
        /// </summary>
        public static AutoSpendResult RunAutoSpend(bool verbose = false, Action<string> log = null)
        {
            var res = new AutoSpendResult();
            try
            {
                if (!GameApi.IsPlaying) { res.Blocked = true; res.BlockReason = "不在存档中"; return res; }

                var community = GameApi.PlayerCommunity;
                var agents = community != null ? community.Agents : null;
                if (agents == null || agents.Count == 0) { res.Blocked = true; res.BlockReason = "暂无小人"; return res; }

                for (int i = 0; i < agents.Count; i++)
                {
                    var agent = agents[i];
                    if (agent == null) continue;
                    var attrs = agent.Attributes;
                    if (attrs == null || SafePoints(attrs) <= 0) continue;

                    var order = SpendOrder(attrs);
                    if (order.Count == 0) continue;

                    var raised = new Dictionary<DrifterAttributes.AttributeType, int>();
                    int spent = 0;
                    while (SafePoints(attrs) > 0)
                    {
                        bool any = false;
                        for (int o = 0; o < order.Count; o++)
                        {
                            if (!SafeLevel(attrs, order[o])) continue;   // maxed/refused -> next best
                            spent++;
                            raised.TryGetValue(order[o], out int n);
                            raised[order[o]] = n + 1;
                            any = true;
                            break;
                        }
                        if (!any) break;                                 // everything maxed
                    }

                    if (spent > 0)
                    {
                        res.PointsSpent += spent;
                        res.Agents++;
                        if (verbose && log != null) log($"spend {Name(agent)}: +{spent} → {Describe(raised)}");
                    }
                }
            }
            catch (Exception e)
            {
                res.Blocked = true;
                res.BlockReason = "异常:" + e.GetType().Name;
                if (log != null) log("auto-spend failed: " + e);
            }
            return res;
        }

        /// <summary>Attribute pick order for one drifter: affinity desc, then current level desc.</summary>
        private static List<DrifterAttributes.AttributeType> SpendOrder(DrifterAttributes attrs)
        {
            var order = new List<DrifterAttributes.AttributeType>();
            var table = attrs.Attributes;
            if (table == null) return order;
            for (int t = 0; t < table.Length; t++)
                if (table[t] != null && table[t].Type != DrifterAttributes.AttributeType.None)
                    order.Add(table[t].Type);
            order.Sort((x, y) =>
            {
                int c = SafeAffinity(attrs, y).CompareTo(SafeAffinity(attrs, x));
                if (c != 0) return c;
                return SafeTotal(attrs, y).CompareTo(SafeTotal(attrs, x));
            });
            return order;
        }

        private static string Describe(Dictionary<DrifterAttributes.AttributeType, int> raised)
        {
            var sb = new StringBuilder();
            foreach (var kv in raised)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('×').Append(kv.Value);
            }
            return sb.ToString();
        }

        private static int SafePoints(DrifterAttributes attrs)
        {
            try { return attrs.SpendablePoints; } catch { return 0; }
        }

        private static int SafeAffinity(DrifterAttributes attrs, DrifterAttributes.AttributeType t)
        {
            try { return attrs.ReturnAffinityAmount(t); } catch { return 0; }
        }

        private static int SafeTotal(DrifterAttributes attrs, DrifterAttributes.AttributeType t)
        {
            try { return attrs.ReturnTotalAttributePoints(t); } catch { return 0; }
        }

        private static bool SafeLevel(DrifterAttributes attrs, DrifterAttributes.AttributeType t)
        {
            try { return attrs.TryLevelAttribute(t); } catch { return false; }
        }

        /// <summary>Real work with a governing skill. Studying/Idle (and the deprecated slots) are
        /// left to the player — auto-tuning those could stall levelling or idle behaviour.</summary>
        private static bool IsManagedTask(AssignmentType t)
        {
            switch (t)
            {
                case AssignmentType.None:
                case AssignmentType.Studying:
                case AssignmentType.Idle:
                case AssignmentType.Deprecated_Rescueing:
                case AssignmentType.Deprecated_Sailing:
                    return false;
                default:
                    return true;
            }
        }

        private static DrifterAttributes.AttributeType SafeAttr(DrifterAttributes attrs, Assignment asn)
        {
            try { return attrs.ReturnAssignmentAttribute(asn); }
            catch { return DrifterAttributes.AttributeType.None; }
        }

        private static int SafeSkill(DrifterAttributes attrs, Assignment asn)
        {
            try { return attrs.ReturnAssignmentAttributePoints(asn); }
            catch { return 0; }
        }

        private static bool SafeSet(Agent a, AssignmentType t, AssignmentPriority p)
        {
            try { a.TryUpdateAssignmentPriority(t, p); return true; }
            catch { return false; }
        }

        private static string Name(Agent a)
        {
            try { return a.Name ?? "?"; }
            catch { return "?"; }
        }
    }
}
