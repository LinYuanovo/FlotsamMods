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
