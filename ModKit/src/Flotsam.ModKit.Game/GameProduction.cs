using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Missing-material assistance: resolve "which placed building can produce item X"
    /// and schedule production through the game's own public API.
    ///
    /// Grounded in: Community.PlayerCommunity.Producers (live registry of placed
    /// producers), Producer.ReturnProducesItem, Producer.SetSelectedRecipe +
    /// Producer.IncreaseSelectedRecipeAmountToProduce (the only public path that both
    /// bumps the amount and enqueues), and the game's own pump in GameManager.LateUpdate
    /// which auto-starts production once materials arrive.
    /// </summary>
    public static class GameProduction
    {
        public static List<Producer> AllProducers()
        {
            var list = new List<Producer>();
            var community = GameApi.PlayerCommunity;
            if (community == null || community.Producers == null) return list;
            foreach (var ip in community.Producers)
            {
                if (ip is Producer p && p != null) list.Add(p);
            }
            return list;
        }

        public static bool CanProduce(Producer producer, ItemProperties item)
        {
            if (producer == null || item == null) return false;
            try { return producer.ReturnProducesItem(item); }
            catch { return false; }
        }

        public static bool IsWorkshop(Producer producer)
        {
            try
            {
                return producer != null && producer.ProductionProperties != null &&
                       producer.ProductionProperties.Type == Producer.Type.Workshop;
            }
            catch { return false; }
        }

        /// <summary>Nearest finished, enabled workshop-type producer of the item.</summary>
        public static Producer FindProducerFor(ItemProperties item, out Buildable owner,
                                               bool requireWorkshop = true)
        {
            owner = null;
            if (item == null) return null;

            Vector3 here = GameApi.FocusPoint;
            Producer best = null;
            float bestDistance = float.MaxValue;

            foreach (var p in AllProducers())
            {
                Buildable b;
                try { b = p.Buildable; } catch { continue; }
                if (b == null) continue;
                if (!GameBuildings.IsFinished(b)) continue;
                try { if (!p.IsEnabled()) continue; } catch { continue; }
                if (requireWorkshop && !IsWorkshop(p)) continue;
                if (!CanProduce(p, item)) continue;

                float d = (b.transform.position - here).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = p; owner = b; }
            }
            return best;
        }

        public static int FindRecipeIndex(Producer producer, ItemProperties item)
        {
            if (producer == null || item == null) return -1;
            try
            {
                var recipes = producer.ProductionProperties != null ? producer.ProductionProperties.Recipes : null;
                if (recipes == null) return -1;
                for (int i = 0; i < recipes.Count; i++)
                {
                    var r = recipes[i];
                    if (r != null && r.ReturnProducesItem(item)) return i;
                }
            }
            catch { }
            return -1;
        }

        public static int HaveCount(ItemProperties item)
        {
            try
            {
                var community = GameApi.PlayerCommunity;
                if (community == null || community.Inventory == null || item == null) return 0;
                return community.Inventory.ReturnCount(item);
            }
            catch { return 0; }
        }

        private static FieldInfo _itemNameField;

        /// <summary>
        /// The localized display name. `ItemProperties.LocalizedName` is public, so the
        /// reflection path is only a fallback for odd item types.
        /// </summary>
        public static string ItemName(ItemProperties item)
        {
            if (item == null) return "该材料";
            try
            {
                var localized = item.LocalizedName;
                var text = localized != null ? localized.ToString() : null;
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch { }
            try
            {
                if (_itemNameField == null)
                    _itemNameField = typeof(ItemProperties).GetField("_localizedName",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                if (_itemNameField != null)
                {
                    var localized = _itemNameField.GetValue(item);
                    if (localized != null)
                    {
                        var text = localized.ToString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
            }
            catch { }
            try { return item.name; } catch { }
            return "该材料";
        }

        /// <summary>The game's own icon for an item, optionally using a recipe's override.</summary>
        public static Sprite ItemIcon(ItemProperties item, Producer.Recipe recipe = null)
        {
            if (item == null) return null;
            try
            {
                if (recipe != null)
                {
                    var overridden = recipe.GetIcon(item);
                    if (overridden != null) return overridden;
                }
            }
            catch { }
            try { if (item.InventorySprite != null) return item.InventorySprite; } catch { }
            return null;
        }

        /// <summary>The game's own type colour for an item (used for slot backings).</summary>
        public static Color ItemTypeColor(ItemProperties item)
        {
            try { if (item != null && item.ItemType != null) return item.ItemType.Color; } catch { }
            return Color.white;
        }

        /// <summary>How many of an item a recipe instance requires (0 when absent).</summary>
        public static int RequiredAmount(Producer.Recipe recipe, ItemProperties item)
        {
            if (recipe == null || item == null) return 0;
            try
            {
                var ingredients = recipe.Ingredients;
                if (ingredients == null) return 0;
                for (int i = 0; i < ingredients.Count; i++)
                {
                    var ing = ingredients[i];
                    if (ing != null && ing.ItemProperties == item) return ing.Amount;
                }
            }
            catch { }
            return 0;
        }

        public static bool ProductionLimitReached(ItemProperties item, int amount = 1)
        {
            try
            {
                var rm = GameManager.ResourceManager;
                if (rm == null) return false;
                return rm.IsProductionLimitReached(item, amount);
            }
            catch { return false; }
        }

        /// <summary>Ingredients of a recipe that the community does not currently have enough of.</summary>
        public static List<CountedItemProperty> MissingIngredients(Producer.Recipe recipe)
        {
            var missing = new List<CountedItemProperty>();
            if (recipe == null) return missing;
            try
            {
                var ingredients = recipe.Ingredients;
                if (ingredients == null) return missing;
                for (int i = 0; i < ingredients.Count; i++)
                {
                    var ing = ingredients[i];
                    if (ing == null || ing.ItemProperties == null || ing.Amount <= 0) continue;
                    if (HaveCount(ing.ItemProperties) < ing.Amount) missing.Add(ing);
                }
            }
            catch { }
            return missing;
        }

        /// <summary>
        /// Selects the recipe for the item and enqueues `amount` units (or continuous).
        /// Returns false with a human-readable reason when nothing could be scheduled.
        /// </summary>
        public static bool Schedule(Producer producer, ItemProperties item, int amount,
                                    bool continuous, out string error)
        {
            error = null;
            if (producer == null) { error = "找不到能生产该材料的建筑"; return false; }

            try { if (!producer.IsEnabled()) { error = "该建筑当前未启用"; return false; } }
            catch { }

            int idx = FindRecipeIndex(producer, item);
            if (idx < 0) { error = "该建筑没有生产此材料的配方"; return false; }

            if (ProductionLimitReached(item, 1)) { error = "该材料产能已达上限"; return false; }

            try
            {
                producer.SetSelectedRecipe(idx);

                int queued = producer.QueuedRecipes != null ? producer.QueuedRecipes.Count : 0;
                int max = Mathf.Max(1, producer.MaximumQueuedRecipes);
                int n = continuous ? 1 : Mathf.Clamp(amount, 1, Mathf.Max(1, max - queued));

                for (int i = 0; i < n; i++)
                    producer.IncreaseSelectedRecipeAmountToProduce();

                if (continuous && producer.TryReturnRecipe(idx, out var recipe) && recipe != null)
                    recipe.ToggleContinuous();

                return true;
            }
            catch (Exception e)
            {
                error = "排产失败: " + e.Message;
                return false;
            }
        }

        /// <summary>Flies the camera to the producer and opens its vanilla building panel.</summary>
        public static bool OpenPanel(Producer producer, float zoomLevel)
        {
            if (producer == null) return false;
            Buildable b;
            try { b = producer.Buildable; } catch { return false; }
            if (b == null) return false;

            GameApi.LockCameraOn(b.gameObject, zoomLevel);
            try { b.OnSelected(true); } catch { }
            return true;
        }

        /// <summary>
        /// The producer whose vanilla building panel is currently open.
        ///
        /// This is the reliable entry point for a "go produce" action: the shortfall display
        /// is a hover element, so a row-click affordance can never be reached with the mouse.
        /// BuildablePanel.Buildable is public and Buildable is an IPanelContext, so this
        /// needs no reflection.
        /// </summary>
        public static Producer GetOpenProducer(out Buildable buildable)
        {
            buildable = null;
            try
            {
                var ui = GameManager.UIManager;
                if (ui == null) return null;
                if (!ui.TryGetPanel(PanelID.BuildablePanel, out var panel) || panel == null) return null;

                var buildablePanel = panel as BuildablePanel;
                if (buildablePanel == null) return null;

                buildable = buildablePanel.Buildable;
                if (buildable == null) return null;

                return buildable.TryReturnBuildableExtendable<Producer>(out var producer) ? producer : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Shortfall ingredients of the open producer's selected recipe that some placed
        /// building can actually produce (i.e. the ones worth offering a "go produce" for).
        /// </summary>
        public static List<CountedItemProperty> MissingProducibleIngredients(Producer producer)
        {
            var result = new List<CountedItemProperty>();
            if (producer == null) return result;

            Producer.Recipe recipe = null;
            try { recipe = producer.SelectedRecipe; } catch { }
            if (recipe == null) return result;

            foreach (var ingredient in MissingIngredients(recipe))
            {
                if (ingredient == null || ingredient.ItemProperties == null) continue;
                if (FindProducerFor(ingredient.ItemProperties, out _) != null) result.Add(ingredient);
            }
            return result;
        }

        /// <summary>Human readable "have/need" for an ingredient.</summary>
        public static string Describe(CountedItemProperty ingredient)
        {
            if (ingredient == null || ingredient.ItemProperties == null) return "?";
            return $"{ItemName(ingredient.ItemProperties)} {HaveCount(ingredient.ItemProperties)}/{ingredient.Amount}";
        }

        // ---------------------------------------------------------------- candidate list

        public struct Candidate
        {
            public Producer Producer;
            public Buildable Owner;
            public float Distance;
            public int QueueFree;
            public bool Enabled;
        }

        /// <summary>
        /// Every finished building that can produce the item, nearest first. Unlike
        /// <see cref="FindProducerFor"/> this keeps the whole list so the UI can offer a choice
        /// instead of silently picking one.
        /// </summary>
        public static List<Candidate> FindProducersFor(ItemProperties item, bool requireWorkshop = true)
        {
            var list = new List<Candidate>();
            if (item == null) return list;

            Vector3 here = GameApi.FocusPoint;
            foreach (var p in AllProducers())
            {
                Buildable b;
                try { b = p.Buildable; } catch { continue; }
                if (b == null) continue;
                if (!GameBuildings.IsFinished(b)) continue;
                if (requireWorkshop && !IsWorkshop(p)) continue;
                if (!CanProduce(p, item)) continue;

                bool enabled = true;
                try { enabled = p.IsEnabled(); } catch { }

                int free = 0;
                try { free = Mathf.Max(0, p.MaximumQueuedRecipes - (p.QueuedRecipes != null ? p.QueuedRecipes.Count : 0)); }
                catch { }

                list.Add(new Candidate
                {
                    Producer = p,
                    Owner = b,
                    Distance = Vector3.Distance(b.transform.position, here),
                    QueueFree = free,
                    Enabled = enabled
                });
            }

            // Enabled buildings first, then nearest.
            list.Sort((x, y) =>
            {
                if (x.Enabled != y.Enabled) return x.Enabled ? -1 : 1;
                return x.Distance.CompareTo(y.Distance);
            });
            return list;
        }

        /// <summary>
        /// How many of the item are already on the way: queued recipes plus the amounts the
        /// player asked for but that are still waiting for materials.
        /// </summary>
        public static int IncomingCount(ItemProperties item)
        {
            if (item == null) return 0;
            int total = 0;
            foreach (var p in AllProducers())
            {
                try
                {
                    if (p.QueuedRecipes != null)
                    {
                        foreach (var q in p.QueuedRecipes)
                        {
                            if (q == null || q.ProducedItems == null) continue;
                            foreach (var produced in q.ProducedItems)
                                if (produced != null && produced.ItemProperties == item) total += Mathf.Max(1, produced.Amount);
                        }
                    }

                    var recipes = p.Recipes;
                    if (recipes == null) continue;
                    foreach (var r in recipes)
                    {
                        if (r == null || !r.IsWaitingToBeQueued()) continue;
                        if (!Produces(r, item)) continue;
                        int pending = r.IsContinuous ? 0 : r.AmountToProduce - QueuedOf(p, r);
                        if (pending > 0) total += pending * ProducedPerBatch(r, item);
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>
        /// One pass over every producer filling `results[item] = units already on the way`.
        /// A panel listing a dozen materials would otherwise rescan every producer per row.
        /// </summary>
        public static void IncomingCounts(Dictionary<ItemProperties, int> results)
        {
            if (results == null) return;
            results.Clear();

            foreach (var p in AllProducers())
            {
                try
                {
                    if (p.QueuedRecipes != null)
                    {
                        foreach (var q in p.QueuedRecipes)
                        {
                            if (q == null || q.ProducedItems == null) continue;
                            foreach (var produced in q.ProducedItems)
                                if (produced != null && produced.ItemProperties != null)
                                    Accumulate(results, produced.ItemProperties, Mathf.Max(1, produced.Amount));
                        }
                    }

                    var recipes = p.Recipes;
                    if (recipes == null) continue;
                    foreach (var r in recipes)
                    {
                        if (r == null || r.IsContinuous || !r.IsWaitingToBeQueued()) continue;
                        int pending = r.AmountToProduce - QueuedOf(p, r);
                        if (pending <= 0) continue;

                        var produced = r.ProducedItems;
                        if (produced == null) continue;
                        for (int i = 0; i < produced.Count; i++)
                        {
                            var pi = produced[i];
                            if (pi == null || pi.ItemProperties == null) continue;
                            Accumulate(results, pi.ItemProperties, pending * Mathf.Max(1, pi.Amount));
                        }
                    }
                }
                catch { }
            }
        }

        private static void Accumulate(Dictionary<ItemProperties, int> map, ItemProperties key, int amount)
        {
            map.TryGetValue(key, out var current);
            map[key] = current + amount;
        }

        /// <summary>Reads one item out of a snapshot built by <see cref="IncomingCounts"/>.</summary>
        public static int Incoming(Dictionary<ItemProperties, int> snapshot, ItemProperties item)
        {
            if (snapshot == null || item == null) return 0;
            return snapshot.TryGetValue(item, out var n) ? n : 0;
        }

        /// <summary>
        /// One pass over every producer, grouping all able producers by the item they make.
        /// Nearest-and-enabled first within each list.
        /// </summary>
        public static void CandidateSnapshot(Dictionary<ItemProperties, List<Candidate>> results)
        {
            if (results == null) return;
            results.Clear();

            Vector3 here = GameApi.FocusPoint;
            foreach (var p in AllProducers())
            {
                Buildable b;
                try { b = p.Buildable; } catch { continue; }
                if (b == null) continue;
                if (!GameBuildings.IsFinished(b)) continue;
                if (!IsWorkshop(p)) continue;

                bool enabled = true;
                try { enabled = p.IsEnabled(); } catch { }
                int free = 0;
                try { free = Mathf.Max(0, p.MaximumQueuedRecipes - (p.QueuedRecipes != null ? p.QueuedRecipes.Count : 0)); }
                catch { }

                float distance = 0f;
                try { distance = Vector3.Distance(b.transform.position, here); } catch { }

                List<ItemProperties> items;
                try
                {
                    var recipes = p.Recipes;
                    if (recipes == null) continue;
                    items = new List<ItemProperties>();
                    foreach (var r in recipes)
                    {
                        if (r == null || r.ProducedItems == null) continue;
                        for (int i = 0; i < r.ProducedItems.Count; i++)
                        {
                            var pi = r.ProducedItems[i];
                            if (pi != null && pi.ItemProperties != null && !items.Contains(pi.ItemProperties))
                                items.Add(pi.ItemProperties);
                        }
                    }
                }
                catch { continue; }

                for (int i = 0; i < items.Count; i++)
                {
                    if (!results.TryGetValue(items[i], out var list))
                    {
                        list = new List<Candidate>();
                        results[items[i]] = list;
                    }
                    list.Add(new Candidate { Producer = p, Owner = b, Distance = distance, QueueFree = free, Enabled = enabled });
                }
            }

            foreach (var kv in results)
                kv.Value.Sort((x, y) =>
                {
                    if (x.Enabled != y.Enabled) return x.Enabled ? -1 : 1;
                    return x.Distance.CompareTo(y.Distance);
                });
        }

        /// <summary>Reads one item out of a snapshot built by <see cref="CandidateSnapshot"/>.</summary>
        public static List<Candidate> Candidates(Dictionary<ItemProperties, List<Candidate>> snapshot, ItemProperties item)
        {
            if (snapshot != null && item != null && snapshot.TryGetValue(item, out var list)) return list;
            return EmptyCandidates;
        }

        private static readonly List<Candidate> EmptyCandidates = new List<Candidate>();

        private static int QueuedOf(Producer producer, Producer.Recipe recipe)        {
            int n = 0;
            if (producer.QueuedRecipes == null) return 0;
            foreach (var q in producer.QueuedRecipes)
                if (q != null && q.Recipe == recipe) n++;
            return n;
        }

        private static bool Produces(Producer.Recipe recipe, ItemProperties item)
        {
            try
            {
                var produced = recipe.ProducedItems;
                if (produced == null) return false;
                for (int i = 0; i < produced.Count; i++)
                    if (produced[i] != null && produced[i].ItemProperties == item) return true;
            }
            catch { }
            return false;
        }

        /// <summary>How many of the item one batch of this recipe yields.</summary>
        public static int ProducedPerBatch(Producer.Recipe recipe, ItemProperties item)
        {
            try
            {
                var produced = recipe.ProducedItems;
                if (produced == null) return 1;
                for (int i = 0; i < produced.Count; i++)
                    if (produced[i] != null && produced[i].ItemProperties == item)
                        return Mathf.Max(1, produced[i].Amount);
            }
            catch { }
            return 1;
        }

        /// <summary>The recipe of this producer that yields the item.</summary>
        public static Producer.Recipe RecipeProducing(Producer producer, ItemProperties item)
        {
            if (producer == null || item == null) return null;
            try
            {
                var recipes = producer.Recipes;
                if (recipes == null) return null;
                for (int i = 0; i < recipes.Count; i++)
                {
                    var r = recipes[i];
                    if (r != null && Produces(r, item)) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Asks a producer for <paramref name="amount"/> batches of the item. Reports how many it
        /// actually accepted, since the queue and the 0..99 per-recipe cap both limit it.
        /// </summary>
        public static bool ScheduleBatches(Producer producer, ItemProperties item, int amount,
                                           out int scheduled, out string error)
        {
            scheduled = 0;
            error = null;
            if (producer == null) { error = "找不到能生产该材料的建筑"; return false; }
            if (item == null) { error = "无效的材料"; return false; }

            try { if (!producer.IsEnabled()) { error = "该建筑当前未启用"; return false; } } catch { }

            var recipe = RecipeProducing(producer, item);
            if (recipe == null) { error = "该建筑没有生产此材料的配方"; return false; }
            if (ProductionLimitReached(item, amount)) { error = "该材料产能已达上限"; return false; }

            try
            {
                producer.SetSelectedRecipe(recipe.Index);
                int requested = Mathf.Max(1, amount);
                for (int i = 0; i < requested; i++)
                {
                    int before = recipe.AmountToProduce;
                    producer.IncreaseSelectedRecipeAmountToProduce();
                    if (recipe.AmountToProduce <= before && !recipe.IsContinuous) break;
                    scheduled++;
                }
                return scheduled > 0;
            }
            catch (Exception e)
            {
                error = "排产失败: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Construction / upgrade materials of a placed building — the same list the game's own
        /// build tooltip shows (`PlaceableProperties.ReturnTooltipRequiredResources`).
        /// </summary>
        public static List<CountedItemProperty> ConstructionRequirements(Buildable buildable)
        {
            var list = new List<CountedItemProperty>();
            if (buildable == null) return list;
            try
            {
                var props = buildable.Properties as PlaceableProperties;
                if (props == null) return list;

                bool upgrading = false;
                try
                {
                    var phase = buildable.BuildPhase;
                    upgrading = phase == BuildPhase.UpgradeHaulTo || phase == BuildPhase.UpgradeHaulFrom ||
                                phase == BuildPhase.UpgradeShutdown;
                }
                catch { }

                var items = props.ReturnTooltipRequiredResources(upgrading);
                if (items == null || items.Length == 0) items = props.ReturnTooltipRequiredResources(false);
                if (items != null)
                    foreach (var i in items)
                        if (i != null && i.ItemProperties != null) list.Add(i);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// World objects whose salvage composition contains the item — the honest answer to
        /// "where does this come from" when no building can produce it (scrap metal, driftwood
        /// and friends only exist as salvage).
        /// </summary>
        public static List<string> SalvageSources(ItemProperties item, int max = 5)
        {
            var names = new List<string>();
            if (item == null) return names;
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<LandmarkInteractableWithComposition>())
                {
                    if (c == null) continue;
                    List<CountedItemProperty> comp = null;
                    try { comp = c.Composition; } catch { continue; }
                    if (comp == null) continue;

                    bool has = false;
                    for (int i = 0; i < comp.Count; i++)
                        if (comp[i] != null && comp[i].ItemProperties == item) { has = true; break; }
                    if (!has) continue;

                    string name = null;
                    try
                    {
                        var lb = c.GetComponentInParent<LandmarkBehaviour>();
                        if (lb != null) name = lb.Name;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(name)) { try { name = c.gameObject.name; } catch { } }
                    if (string.IsNullOrEmpty(name)) continue;

                    if (!names.Contains(name)) names.Add(name);
                    if (names.Count >= max) break;
                }
            }
            catch { }
            return names;
        }

        // ---------------------------------------------------------------- chain fill

        public class ChainReport
        {
            public readonly List<string> Scheduled = new List<string>();
            public readonly List<string> Blocked = new List<string>();
            public int Batches;

            public bool Empty => Scheduled.Count == 0 && Blocked.Count == 0;

            public string Summary()
            {
                if (Scheduled.Count == 0 && Blocked.Count == 0) return "没有需要补齐的材料";
                var sb = new StringBuilder();
                sb.Append("已安排 ").Append(Scheduled.Count).Append(" 项");
                if (Blocked.Count > 0) sb.Append("，").Append(Blocked.Count).Append(" 项无法安排");
                return sb.ToString();
            }

            public string Detail(int maxLines = 8)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Scheduled.Count && i < maxLines; i++)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append("· ").Append(Scheduled[i]);
                }
                for (int i = 0; i < Blocked.Count && i < 2; i++)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append("× ").Append(Blocked[i]);
                }
                if (Scheduled.Count + Blocked.Count > maxLines + 2)
                    sb.Append("\n…");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Recursively schedules everything a recipe is short of, including the intermediate
        /// materials those need in turn. `multiplier` is how many batches of THIS recipe are
        /// wanted, so a chain that feeds 3 planks per batch asks for the right amount of logs.
        /// </summary>
        public static void FillChain(Producer.Recipe recipe, int multiplier, int maxDepth, ChainReport report)
        {
            FillChain(recipe, multiplier, maxDepth, 0, report, new HashSet<Producer.Recipe>());
        }

        private static void FillChain(Producer.Recipe recipe, int multiplier, int maxDepth, int depth,
                                      ChainReport report, HashSet<Producer.Recipe> visiting)
        {
            if (recipe == null || report == null) return;
            if (depth > maxDepth) return;
            if (!visiting.Add(recipe)) return;   // A needs B needs A: stop the cycle

            try
            {
                var ingredients = recipe.Ingredients;
                if (ingredients == null) return;

                for (int i = 0; i < ingredients.Count; i++)
                {
                    var ing = ingredients[i];
                    if (ing == null || ing.ItemProperties == null || ing.Amount <= 0) continue;

                    int need = ing.Amount * Mathf.Max(1, multiplier);
                    int have = HaveCount(ing.ItemProperties);
                    int incoming = IncomingCount(ing.ItemProperties);
                    int shortfall = need - have - incoming;
                    if (shortfall <= 0) continue;

                    var candidates = FindProducersFor(ing.ItemProperties);
                    if (candidates.Count == 0)
                    {
                        report.Blocked.Add($"{ItemName(ing.ItemProperties)}：没有已完工的建筑能生产（缺 {shortfall}）");
                        continue;
                    }

                    int remaining = shortfall;
                    for (int c = 0; c < candidates.Count && remaining > 0; c++)
                    {
                        var cand = candidates[c];
                        if (!cand.Enabled) continue;

                        var sub = RecipeProducing(cand.Producer, ing.ItemProperties);
                        if (sub == null) continue;

                        int perBatch = ProducedPerBatch(sub, ing.ItemProperties);
                        int batches = Mathf.Max(1, Mathf.CeilToInt(remaining / (float)perBatch));

                        int scheduled;
                        string error;
                        if (!ScheduleBatches(cand.Producer, ing.ItemProperties, batches, out scheduled, out error))
                        {
                            report.Blocked.Add($"{ItemName(ing.ItemProperties)} @ {GameBuildings.NameOf(cand.Owner)}：{error}");
                            continue;
                        }

                        report.Batches += scheduled;
                        report.Scheduled.Add($"{ItemName(ing.ItemProperties)} x{scheduled} @ {GameBuildings.NameOf(cand.Owner)}");
                        remaining -= scheduled * perBatch;

                        // The batches we just asked for need THEIR own materials.
                        FillChain(sub, scheduled, maxDepth, depth + 1, report, visiting);
                    }

                    if (remaining > 0)
                        report.Blocked.Add($"{ItemName(ing.ItemProperties)}：仍缺 {remaining}（队列已满或产能受限）");
                }
            }
            finally
            {
                visiting.Remove(recipe);
            }
        }
    }
}