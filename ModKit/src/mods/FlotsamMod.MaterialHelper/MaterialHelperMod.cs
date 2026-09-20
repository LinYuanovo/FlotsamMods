using System;
using System.Collections.Generic;
using System.Reflection;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace FlotsamMods.MaterialHelper
{
    /// <summary>
    /// "What is this recipe short of, and who can make it?" for the workshop panel.
    ///
    /// The previous version offered one action — a hotkey that found A producer and queued 1 — and
    /// a badge on the recipe toggles. That answered a question nobody was asking (which building
    /// can make it) and could not be clicked where the player was actually looking, because the
    /// ingredient list the player sees while picking a recipe is a CURSOR-FOLLOWING TOOLTIP:
    /// `TooltipPanel.Update` re-anchors the box every frame and `TooltipTriggerBase.OnPointerMove`
    /// hides it the instant the pointer leaves the trigger, so the cells can never be reached.
    ///
    /// v4 therefore attacks it from three sides:
    ///  1. A permanent, draggable "材料助手" panel that mirrors the open building's requirements —
    ///     库存 / 需求 / 在途 per material, the nearest building that can produce it, and a 补齐
    ///     and 定位 button on every row. No hovering involved.
    ///  2. The vanilla ingredient cells (`RecipeItemDisplay`) really are clickable — they implement
    ///     `IPointerClickHandler` and expose PUBLIC `OnLeftClickEvent` / `OnRightClickEvent`
    ///     UnityEvents. Left click schedules the shortfall, right click opens the panel on that
    ///     material. No reflection needed.
    ///  3. The tooltip itself is pinned while the cursor is inside it, so the floating material
    ///     list can finally be read (and clicked) instead of vanishing.
    ///
    /// `一键补齐` walks the whole dependency chain: it schedules the shortfall, then the shortfall
    /// of the materials THAT need, up to a configurable depth.
    /// </summary>
    public sealed class MaterialHelperMod : FlotsamModBase
    {
        private static MaterialHelperMod _current;

        private GameObject _driverObject;
        private MaterialHelperDriver _driver;
        private MaterialPanel _panel;

        private static FieldInfo _tooltipObjectField;
        private static bool _pinned;
        private static BuildableTooltip _pinnedBuildable;
        private float _actionRetryUntil;
        private bool _actionDone;
        private bool _actionWasFill;

        public float Zoom { get; private set; } = 0.6f;
        public int ChainDepth { get; private set; } = 2;
        public bool ShowPanel = true;
        public bool PinTooltips { get; private set; } = true;
        public bool HookNativeCells { get; private set; } = true;
        public bool Verbose { get; private set; } = true;

        public IKeybind FillKey { get; private set; }
        public IKeybind PanelKey { get; private set; }
        public IModContext Context => Ctx;
        public MaterialPanel Panel => _panel;

        public void Toast(string message, ToastKind kind = ToastKind.Info)
        {
            try { Ui.Toast(message, kind); } catch { }
        }

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);

            Zoom = Mathf.Clamp(Config.Get("zoomLevel", 0.6f), 0.05f, 3f);
            ChainDepth = Mathf.Clamp(Config.Get("chainDepth", 2), 0, 5);
            ShowPanel = Config.Get("showPanel", true);
            PinTooltips = Config.Get("pinTooltips", true);
            HookNativeCells = Config.Get("hookNativeCells", true);
            Verbose = Config.Get("verbose", true);

            FillKey = Keybinds.Register("helper.fill", KeyCode.G, "一键补齐当前配方的整条缺料链");
            PanelKey = Keybinds.Register("helper.panel", KeyCode.H, "显示/隐藏材料助手面板");
        }

        public override void OnEnable()
        {
            _current = this;

            // 1) the vanilla ingredient cells
            if (HookNativeCells)
            {
                var updateDisplay = GamePatches.MethodByName(typeof(RecipeDisplay), "UpdateDisplay");
                if (updateDisplay != null)
                    Patches.Postfix(updateDisplay, AccessTools.Method(typeof(MaterialHelperMod), nameof(AfterRecipeDisplayUpdate)));
                else
                    Log.Warn("RecipeDisplay.UpdateDisplay not found — native material cells stay inert");
            }

            // 2) keep the floating material list reachable
            if (PinTooltips)
            {
                var update = GamePatches.MethodByName(typeof(TooltipPanel), "Update");
                if (update != null)
                    Patches.Prefix(update, AccessTools.Method(typeof(MaterialHelperMod), nameof(BeforeTooltipUpdate)));

                var hide = GamePatches.MethodByName(typeof(TooltipPanel), "HideTooltip_Internal");
                if (hide != null)
                    Patches.Prefix(hide, AccessTools.Method(typeof(MaterialHelperMod), nameof(BeforeTooltipHide)));

                if (update == null && hide == null)
                    Log.Warn("TooltipPanel not found — tooltip pinning disabled");
            }

            // 3) the construction tooltip hides itself via BuildableTooltip.HideTooltip (a direct
            //    SetActive), which bypasses TooltipPanel entirely - that is why pinning never
            //    worked. Suppress that hide while click-pinned.
            try
            {
                var btHide = GamePatches.MethodByName(typeof(BuildableTooltip), "HideTooltip");
                if (btHide != null)
                {
                    Patches.Prefix(btHide, AccessTools.Method(typeof(MaterialHelperMod), nameof(BeforeBuildableTooltipHide)));
                    Log.Info("patch: BuildableTooltip.HideTooltip <- BeforeBuildableTooltipHide");
                }
                else
                    Log.Warn("BuildableTooltip.HideTooltip not found - construction tooltip cannot be pinned");
            }
            catch (Exception e)
            {
                Log.Warn("buildable tooltip patch failed: " + e.Message);
            }

            // 4) the construction menu's material slots: click one to jump to a producer, or to
            //    be told where the item actually comes from
            try
            {
                var slotInit = GamePatches.Method(typeof(BuildableTooltipItemSlot), "Initialize",
                                                  typeof(CountedItemProperty), typeof(bool));
                if (slotInit != null)
                {
                    Patches.Postfix(slotInit, AccessTools.Method(typeof(MaterialHelperMod), nameof(AfterSlotInitialize)));
                    Log.Info("patch: BuildableTooltipItemSlot.Initialize <- AfterSlotInitialize");
                }
                else
                    Log.Warn("BuildableTooltipItemSlot.Initialize not found — construction slots stay inert");
            }
            catch (Exception e)
            {
                Log.Warn("construction slot hook failed: " + e.Message);
            }

            // 5) at-a-glance shortfall badge on the recipe toggles
            try
            {
                var stateTarget = GamePatches.MethodByName(typeof(ProductionPanelRecipeToggle), "UpdateState");
                if (stateTarget != null)
                    Patches.Postfix(stateTarget, AccessTools.Method(typeof(MaterialHelperMod), nameof(AfterToggleState)));
            }
            catch (Exception e)
            {
                Log.Warn("UpdateState hook failed: " + e.Message);
            }

            _driverObject = new GameObject("[FlotsamMod.MaterialHelper]");
            UnityEngine.Object.DontDestroyOnLoad(_driverObject);
            _driverObject.hideFlags = HideFlags.HideAndDontSave;
            _driver = _driverObject.AddComponent<MaterialHelperDriver>();
            _driver.Mod = this;

            _panel = new MaterialPanel(this);
            _panel.Build();

            Log.Info($"material helper ready — fill {FillKey.Key}, panel {PanelKey.Key}, " +
                     $"chain depth {ChainDepth}, native cells {HookNativeCells}, tooltip pin {PinTooltips}");
        }

        public override void OnDisable()
        {
            _pinned = false;

            try
            {
                foreach (var hook in UnityEngine.Object.FindObjectsOfType<IngredientRowHook>())
                    if (hook != null) hook.Detach();
                foreach (var badge in UnityEngine.Object.FindObjectsOfType<RecipeToggleBadge>())
                    if (badge != null) badge.Clear();
            }
            catch { }

            try { _panel?.Destroy(); } catch { }
            _panel = null;

            if (_driverObject != null)
            {
                UnityEngine.Object.Destroy(_driverObject);
                _driverObject = null;
                _driver = null;
            }
            _current = null;
        }

        public override void OnGameStart()
        {
            _panel?.Build();
            Ui.Toast($"材料助手就绪：打开工坊后按 {PanelKey.Key} 显示面板，{FillKey.Key} 一键补齐整条缺料链；" +
                     $"配方里的材料格现在可以直接点（左键补齐 / 右键定位）", ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            try { _panel?.Destroy(); } catch { }
            _panel = null;
        }

        internal void TogglePanel()
        {
            if (_panel == null) _panel = new MaterialPanel(this);
            _panel.Build();
            _panel.Toggle();
            ShowPanel = _panel.Visible;
            Config.Set("showPanel", ShowPanel);
            Config.Save();
        }

        // ------------------------------------------------------------ actions

        /// <summary>Schedules the whole missing chain of the open producer's selected recipe.</summary>
        internal void FillOpenProducer(bool dryRun)
        {
            var producer = GameProduction.GetOpenProducer(out var openBuildable);
            if (producer == null)
            {
                if (openBuildable != null)
                {
                    Toast("这座建筑没有生产配方；面板里已列出它的建造材料", ToastKind.Info);
                    OpenPanel();
                    return;
                }
                Toast("请先打开一个工坊（生产）建筑面板", ToastKind.Warning);
                return;
            }

            Producer.Recipe recipe = null;
            try { recipe = producer.SelectedRecipe; } catch { }
            if (recipe == null)
            {
                Toast("请先在工坊里选一个配方", ToastKind.Warning);
                OpenPanel();
                return;
            }

            if (dryRun) { OpenPanel(); return; }

            var report = new GameProduction.ChainReport();
            try
            {
                GameProduction.FillChain(recipe, 1, ChainDepth, report);
            }
            catch (Exception e)
            {
                Toast("补齐失败：" + e.Message, ToastKind.Error);
                return;
            }

            if (report.Empty)
            {
                Toast("当前配方不缺材料，或没有任何建筑能生产缺的材料", ToastKind.Info);
            }
            else
            {
                Toast($"一键补齐：{report.Summary()}（共 {report.Batches} 批）\n{report.Detail(6)}",
                      report.Blocked.Count == 0 ? ToastKind.Success : ToastKind.Warning);
            }

            OpenPanel();
            _panel?.Refresh(true);
        }

        private void OpenPanel()
        {
            if (_panel == null) _panel = new MaterialPanel(this);
            _panel.Build();
            if (!_panel.Visible)
            {
                _panel.SetVisible(true);
                ShowPanel = true;
                Config.Set("showPanel", true);
                Config.Save();
            }
        }

        /// <summary>Left-click on a vanilla ingredient cell.</summary>
        internal void OnNativeCellClicked(RecipeItemDisplay display)
        {
            var item = SafeItem(display);
            if (item == null) return;

            var recipe = SafeRecipe(display);
            int need = recipe != null ? GameProduction.RequiredAmount(recipe, item) : 1;
            if (need <= 0) need = 1;

            int have = GameProduction.HaveCount(item);
            int incoming = GameProduction.IncomingCount(item);
            int shortfall = need - have - incoming;

            if (shortfall <= 0)
            {
                Toast($"{GameProduction.ItemName(item)}：库存 {have}，在途 {incoming}，已经足够（需要 {need}）", ToastKind.Info);
                return;
            }

            var candidates = GameProduction.FindProducersFor(item);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Enabled) continue;

                var sub = GameProduction.RecipeProducing(candidates[i].Producer, item);
                int perBatch = GameProduction.ProducedPerBatch(sub, item);
                int batches = Mathf.Max(1, Mathf.CeilToInt(shortfall / (float)Mathf.Max(1, perBatch)));

                int scheduled;
                string error;
                if (!GameProduction.ScheduleBatches(candidates[i].Producer, item, batches, out scheduled, out error))
                {
                    Toast($"{GameBuildings.NameOf(candidates[i].Owner)}：{error}", ToastKind.Warning);
                    return;
                }

                Toast($"已安排 {GameProduction.ItemName(item)} x{scheduled * perBatch} @ " +
                      $"{GameBuildings.NameOf(candidates[i].Owner)}（缺 {shortfall}）", ToastKind.Success);

                if (sub != null && ChainDepth > 0)
                {
                    var report = new GameProduction.ChainReport();
                    GameProduction.FillChain(sub, scheduled, ChainDepth, report);
                    if (!report.Empty) Toast("连带补齐：" + report.Summary(), ToastKind.Info);
                }
                _panel?.Refresh(true);
                return;
            }

            Toast($"没有已完工且启用的建筑能生产 {GameProduction.ItemName(item)}（还缺 {shortfall}）", ToastKind.Warning);
            OpenPanel();
        }

        private SourcePopup _popup;
        private int _lastResolveFrame = -1;

        /// <summary>The construction-tooltip material slot (if any) under the pointer right now.</summary>
        private static ConstructionSlotHook HookUnderPointer()
        {
            try
            {
                foreach (var hook in UnityEngine.Object.FindObjectsOfType<ConstructionSlotHook>())
                {
                    if (hook == null || hook.gameObject == null || !hook.gameObject.activeInHierarchy) continue;
                    if (hook.Item == null) continue;
                    if (PointerOverBounds(hook.transform as RectTransform)) return hook;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// A material was clicked somewhere in the vanilla UI (construction tooltip or workshop
        /// recipe row). Fly to a building that can produce it; if nothing can, explain where the
        /// item actually comes from.
        /// </summary>
        internal void ResolveItem(ItemProperties item)
        {
            if (item == null) return;
            // The bounds-based click path and the slot overlay's OnPointerClick can both fire for
            // one physical click; only the first may act.
            if (Time.frameCount == _lastResolveFrame) return;
            _lastResolveFrame = Time.frameCount;
            string name = GameProduction.ItemName(item);

            var candidates = GameProduction.FindProducersFor(item);
            if (Verbose)
                Log.Info($"resolve: click on material '{name}' -> {candidates.Count} producer(s)");
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Enabled) continue;
                if (Verbose)
                    Log.Info($"resolve: locating {GameBuildings.NameOf(candidates[i].Owner)} ({candidates[i].Distance:0}u)");
                UnpinTooltip();
                GameProduction.OpenPanel(candidates[i].Producer, Zoom);
                Toast($"「{name}」可由 {GameBuildings.NameOf(candidates[i].Owner)} 生产" +
                      $"（{candidates[i].Distance:0} 单位），已打开它的生产面板", ToastKind.Success);
                return;
            }

            // Nothing produces it: open the "how do I get this" popup.
            if (Verbose) Log.Info($"resolve: no producer for '{name}' -> source popup");
            UnpinTooltip();
            if (_popup == null) _popup = new SourcePopup(this);
            _popup.Show(item);
        }

        internal void ClosePopup()
        {
            if (_popup != null) _popup.Hide();
        }

        /// <summary>
        /// Hold G and click the building you want to place: read whatever the construction
        /// tooltip currently lists and schedule every shortfall at the nearest able building,
        /// recursively including the materials those batches need in turn.
        /// </summary>
        internal void AimDetect()
        {
            var reqs = new List<KeyValuePair<ItemProperties, int>>();
            try
            {
                foreach (var hook in UnityEngine.Object.FindObjectsOfType<ConstructionSlotHook>())
                {
                    if (hook == null || hook.gameObject == null || !hook.gameObject.activeInHierarchy) continue;
                    if (hook.Item == null) continue;
                    reqs.Add(new KeyValuePair<ItemProperties, int>(hook.Item, hook.Need));
                }
            }
            catch { }

            if (reqs.Count == 0)
            {
                Log.Warn("aim-fill: construction tooltip lists no materials; nothing to fill");
                Toast("未检测到建造需求：请先让建造悬浮框显示出来", ToastKind.Warning);
                return;
            }

            Log.Info($"aim-fill: G+click on build entry, {reqs.Count} requirement(s)");
            var report = new GameProduction.ChainReport();
            for (int i = 0; i < reqs.Count; i++)
            {
                var item = reqs[i].Key;
                int need = Mathf.Max(1, reqs[i].Value);
                int shortfall = need - GameProduction.HaveCount(item) - GameProduction.IncomingCount(item);
                if (shortfall <= 0) continue;

                var candidates = GameProduction.FindProducersFor(item);
                bool scheduled = false;
                for (int c = 0; c < candidates.Count && !scheduled; c++)
                {
                    if (!candidates[c].Enabled) continue;

                    var sub = GameProduction.RecipeProducing(candidates[c].Producer, item);
                    int perBatch = GameProduction.ProducedPerBatch(sub, item);
                    int batches = Mathf.Max(1, Mathf.CeilToInt(shortfall / (float)Mathf.Max(1, perBatch)));

                    int done;
                    string error;
                    if (!GameProduction.ScheduleBatches(candidates[c].Producer, item, batches, out done, out error))
                    {
                        report.Blocked.Add($"{GameProduction.ItemName(item)} @ " +
                                           $"{GameBuildings.NameOf(candidates[c].Owner)}：{error}");
                        continue;
                    }

                    report.Batches += done;
                    report.Scheduled.Add($"{GameProduction.ItemName(item)} x{done * perBatch} @ " +
                                         $"{GameBuildings.NameOf(candidates[c].Owner)}");
                    if (sub != null && ChainDepth > 0)
                        GameProduction.FillChain(sub, done, ChainDepth, report);
                    scheduled = true;
                }

                if (!scheduled && report.Blocked.Count == 0)
                    report.Blocked.Add($"{GameProduction.ItemName(item)}：没有已完工且启用的生产建筑");
            }

            // The box has served its purpose; let the player see the result toast cleanly.
            UnpinTooltip();

            if (report.Empty)
            {
                Toast("检测完成：这座建筑的建造材料全部齐备", ToastKind.Success);
            }
            else
            {
                Toast($"已为「建造需求」补齐：{report.Summary()}（共 {report.Batches} 批）\n{report.Detail(6)}",
                      report.Blocked.Count == 0 ? ToastKind.Success : ToastKind.Warning);
            }
        }

        /// <summary>Right-click on a vanilla ingredient cell.</summary>
        internal void OnNativeCellRightClicked(RecipeItemDisplay display)
        {
            OpenPanel();
            _panel?.FocusItem(SafeItem(display));
        }

        private static ItemProperties SafeItem(RecipeItemDisplay display)
        {
            try { return display != null ? display.ItemProperties : null; } catch { return null; }
        }

        private static Producer.Recipe SafeRecipe(RecipeItemDisplay display)
        {
            try { return display != null ? display.Recipe : null; } catch { return null; }
        }

        private static Producer.Recipe SafeRecipe(RecipeDisplay display)
        {
            try { return display != null ? display.Recipe : null; } catch { return null; }
        }

        // ------------------------------------------------------------ patches

        /// <summary>
        /// Runs after the vanilla panel has (re)built its ingredient cells. The cells are pooled
        /// and re-initialized with a different item each time, so the hook component stays and
        /// reads the item at click time.
        /// </summary>
        public static void AfterRecipeDisplayUpdate(RecipeDisplay __instance)
        {
            try
            {
                var mod = _current;
                if (mod == null || __instance == null) return;

                var recipe = SafeRecipe(__instance);
                if (recipe == null) return;

                var cells = __instance.GetComponentsInChildren<RecipeItemDisplay>(true);
                for (int i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i];
                    if (cell == null) continue;
                    var item = SafeItem(cell);
                    if (item == null) continue;
                    if (GameProduction.RequiredAmount(recipe, item) <= 0) continue;  // produced-item cell

                    var hook = cell.GetComponent<IngredientRowHook>();
                    if (hook == null) hook = cell.gameObject.AddComponent<IngredientRowHook>();
                    hook.Bind(mod);
                }
            }
            catch (Exception e)
            {
                _current?.Log.Warn("recipe display hook failed: " + e.Message);
            }
        }

        /// <summary>
        /// The construction/build tooltip rebuilds its material slots whenever the inventory
        /// changes, so record which item each slot shows and make the slot clickable.
        /// </summary>
        public static void AfterSlotInitialize(BuildableTooltipItemSlot __instance, CountedItemProperty slotItem)
        {
            try
            {
                var mod = _current;
                if (mod == null || __instance == null) return;
                var item = slotItem != null ? slotItem.ItemProperties : null;
                if (item == null) return;

                var hook = __instance.GetComponent<ConstructionSlotHook>();
                bool fresh = hook == null;
                if (fresh) hook = __instance.gameObject.AddComponent<ConstructionSlotHook>();
                hook.Bind(mod, item, slotItem.Amount);
                if (fresh && mod.Verbose)
                    mod.Log.Info($"slot: hooked construction material '{GameProduction.ItemName(item)}' need {slotItem.Amount}");
            }
            catch (Exception e)
            {
                _current?.Log.Warn("construction slot hook failed: " + e.Message);
            }
        }

        /// <summary>Shortfall badge on a recipe toggle: click it to open the panel on that recipe.</summary>
        public static void AfterToggleState(ProductionPanelRecipeToggle __instance)
        {
            try
            {
                var mod = _current;
                if (mod == null || __instance == null) return;
                var badge = __instance.GetComponent<RecipeToggleBadge>();
                if (badge == null) badge = __instance.gameObject.AddComponent<RecipeToggleBadge>();
                badge.Bind(mod, __instance);
                badge.Refresh();
            }
            catch (Exception e)
            {
                _current?.Log.Warn("toggle state hook failed: " + e.Message);
            }
        }

        /// <summary>
        /// While click-pinned, the floating box stops following the cursor so its material cells
        /// can actually be aimed at and clicked.
        /// </summary>
        public static bool BeforeTooltipUpdate(TooltipPanel __instance)
        {
            try
            {
                var mod = _current;
                if (mod == null || !mod.PinTooltips || __instance == null) return true;
                return !_pinned;
            }
            catch { return true; }
        }

        /// <summary>
        /// The construction tooltip's own hide path. It SetActives its root directly, so this is
        /// the only place a pin can survive a hover-exit or a deselect of the build entry.
        /// </summary>
        public static bool BeforeBuildableTooltipHide(BuildableTooltip __instance)
        {
            try
            {
                var mod = _current;
                if (mod == null || !mod.PinTooltips || __instance == null) return true;
                return !_pinned;
            }
            catch { return true; }
        }

        /// <summary>While click-pinned, nothing may hide the tooltip — not hover-exit, not UI state.</summary>
        public static bool BeforeTooltipHide(TooltipPanel __instance)
        {
            try
            {
                var mod = _current;
                if (mod == null || !mod.PinTooltips || __instance == null) return true;
                return !_pinned;
            }
            catch { return true; }
        }

        /// <summary>
        /// Click-pin state machine, run every frame by the driver:
        ///   * tooltip showing + click on the building (or anywhere outside the box) → pin it, so
        ///     the player can then move the mouse up and click a missing material;
        ///   * click inside the box → stay pinned (the slot hook resolves the material);
        ///   * click outside the box while pinned → dismiss.
        /// </summary>
        internal void UpdatePin()
        {
            if (!PinTooltips) { ReleasePin(); return; }

            bool clicked = false;
            try { clicked = Input.GetMouseButtonDown(0); } catch { }

            var bt = _pinnedBuildable;
            if (bt == null || !IsAlive(bt.gameObject))
                bt = FindVisibleBuildableTooltip();
            bool btVisible = bt != null;

            var panel = TooltipPanel.Instance;
            bool tpVisible = panel != null && TooltipRect(panel) != null;

            if (!btVisible && !tpVisible)
            {
                if (_pinned) ReleasePin();
                return;
            }

            // Safety: leaving normal play (menus, map, dialogs) always clears the pin.
            try { if (_pinned && UIManager.State != UIState.Normal) { ReleasePin(); return; } } catch { }

            if (!clicked)
            {
                // Safety: if every box went away behind our back, drop the pin.
                if (_pinned && !btVisible && !tpVisible) ReleasePin();
                return;
            }

            bool overBox = (btVisible && PointerOverBounds(bt.transform as RectTransform))
                           || (tpVisible && PointerOverTooltip(panel));

            // The slot's raycast overlay does not reliably win the EventSystem raycast (something
            // above it swallows the click), so resolve material clicks straight from the pointer
            // bounds while pinned - deterministic, independent of raycast order.
            if (_pinned && clicked)
            {
                var hook = HookUnderPointer();
                if (hook != null)
                {
                    if (Verbose)
                        Log.Info($"slot: click on material row '{GameProduction.ItemName(hook.Item)}' -> resolve");
                    ResolveItem(hook.Item);
                    return;
                }
            }

            if (_pinned && !overBox)
            {
                // Clicked outside while pinned: dismiss, as requested.
                if (Verbose) Log.Info("pin: click outside the pinned box -> release");
                ReleasePin();
                return;
            }

            bool wasPinned = _pinned;
            _pinned = true;
            if (btVisible) _pinnedBuildable = bt;
            // While pinned, our own panel must not swallow the clicks meant for the tooltip's
            // material slots - that is why slot clicks never resolved before.
            SetPanelClickThrough(true);

            // The moment the box gets pinned by clicking the build entry is also the moment we
            // act on it: plain click = show that blueprint's requirements in the panel;
            // G held = schedule every shortfall right away.
            if (!wasPinned && !overBox)
            {
                bool gHeld = FillKey != null && FillKey.IsHeld;
                if (Verbose) Log.Info($"pin: click on build entry (G held={gHeld}) -> pin");
                _actionWasFill = gHeld;
                _actionDone = false;
                // The tooltip may only build its slots a frame or two after the click.
                _actionRetryUntil = Time.unscaledTime + 1.2f;
                TryRunAction();
            }
            else if (_pinned && !_actionDone && Time.unscaledTime < _actionRetryUntil)
            {
                TryRunAction();
            }
        }

        /// <summary>Runs the pinned-click action once the tooltip's slots actually exist.</summary>
        private void TryRunAction()
        {
            if (ReadSlotRequirements().Count == 0) return;
            _actionDone = true;
            if (_actionWasFill) AimDetect();
            else CaptureBlueprint();
        }

        /// <summary>Everything the pinned construction tooltip currently lists.</summary>
        private List<KeyValuePair<ItemProperties, int>> ReadSlotRequirements()
        {
            var reqs = new List<KeyValuePair<ItemProperties, int>>();
            try
            {
                foreach (var hook in UnityEngine.Object.FindObjectsOfType<ConstructionSlotHook>())
                {
                    if (hook == null || hook.gameObject == null || !hook.gameObject.activeInHierarchy) continue;
                    if (hook.Item == null) continue;
                    reqs.Add(new KeyValuePair<ItemProperties, int>(hook.Item, hook.Need));
                }
            }
            catch (Exception e)
            {
                Log.Warn("read slot requirements failed: " + e.Message);
            }
            return reqs;
        }

        /// <summary>Best-effort title of the pinned construction tooltip (its first label).</summary>
        private string BlueprintTitle()
        {
            var bt = _pinnedBuildable != null && IsAlive(_pinnedBuildable.gameObject)
                ? _pinnedBuildable : FindVisibleBuildableTooltip();
            if (bt == null) return "建造需求";
            try
            {
                foreach (var t in bt.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (t.text.IndexOf('/') >= 0) continue;   // counters like 2/6
                    return t.text;
                }
            }
            catch { }
            return "建造需求";
        }

        /// <summary>
        /// Plain click on the build entry: pin the box AND show that blueprint's requirements in
        /// the panel, so the player sees what is missing before deciding to fill it.
        /// </summary>
        private void CaptureBlueprint()
        {
            var reqs = ReadSlotRequirements();
            if (reqs.Count == 0)
            {
                if (Verbose) Log.Info("capture: construction tooltip lists no materials");
                return;
            }


            string title = BlueprintTitle();
            if (_panel == null) _panel = new MaterialPanel(this);
            _panel.ShowRequirements("建造需求：" + title, reqs);

            int shortCount = 0;
            var names = new System.Text.StringBuilder();
            foreach (var r in reqs)
            {
                bool short_ = GameProduction.HaveCount(r.Key) + GameProduction.IncomingCount(r.Key) < r.Value;
                if (short_) { shortCount++; if (names.Length > 0) names.Append(", "); names.Append(GameProduction.ItemName(r.Key)); }
            }
            Log.Info($"capture: '{title}' lists {reqs.Count} material(s), {shortCount} short [{names}]");
        }

        private static bool IsAlive(GameObject go) => go != null && go.activeInHierarchy;

        private static BuildableTooltip FindVisibleBuildableTooltip()
        {
            try
            {
                foreach (var t in UnityEngine.Object.FindObjectsOfType<BuildableTooltip>())
                    if (t != null && t.gameObject.activeInHierarchy) return t;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The buildable tooltip's ROOT rect does not cover its visible panel — the name, info
        /// and material containers are children laid out around it — so hit-testing the root
        /// counted every material-slot click as "outside the pinned box": the pin was released
        /// (hiding the tooltip) before the EventSystem could deliver the click to the slot hook,
        /// which is exactly why clicking a missing material did nothing. Test the union of the
        /// visible children's screen-space bounds instead.
        /// </summary>
        private static bool PointerOverBounds(RectTransform rt)
        {
            if (rt == null) return false;
            var canvas = rt.GetComponentInParent<Canvas>();
            var cam = GameKeys.CanvasCamera(canvas);
            var mouse = GameKeys.MousePosition;
            var corners = new Vector3[4];
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;

            var children = rt.GetComponentsInChildren<RectTransform>(false);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] == null) continue;
                children[i].GetWorldCorners(corners);
                for (int c = 0; c < 4; c++)
                {
                    Vector3 sp = cam != null
                        ? RectTransformUtility.WorldToScreenPoint(cam, corners[c])
                        : corners[c];
                    if (sp.x < minX) minX = sp.x;
                    if (sp.x > maxX) maxX = sp.x;
                    if (sp.y < minY) minY = sp.y;
                    if (sp.y > maxY) maxY = sp.y;
                }
                any = true;
            }
            if (!any) return false;
            return mouse.x >= minX - 6f && mouse.x <= maxX + 6f &&
                   mouse.y >= minY - 6f && mouse.y <= maxY + 6f;
        }

        private void SetPanelClickThrough(bool clickThrough)
        {
            try { if (_panel != null) _panel.SetClickThrough(clickThrough); } catch { }
        }

        private void ReleasePin()
        {
            if (!_pinned && _pinnedBuildable == null) return;
            if (Verbose) Log.Info("pin: released");
            _pinned = false;
            SetPanelClickThrough(false);
            var bt = _pinnedBuildable;
            _pinnedBuildable = null;
            try { if (bt != null) bt.HideTooltip(); } catch { }
            try { TooltipPanel.HideTooltip(); } catch { }
        }

        /// <summary>Drops the pin and hides the box (used after jumping to a producer).</summary>
        internal void UnpinTooltip() => ReleasePin();

        private static bool PointerOverTooltip(TooltipPanel panel)
        {
            var rt = TooltipRect(panel);
            if (rt == null) return false;

            var canvas = rt.GetComponentInParent<Canvas>();
            var cam = GameKeys.CanvasCamera(canvas);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, GameKeys.MousePosition, cam, out var local))
                return false;

            var rect = rt.rect;
            rect.xMin -= 8f;
            rect.yMin -= 8f;
            rect.xMax += 8f;
            rect.yMax += 8f;
            return rect.Contains(local);
        }

        private static RectTransform TooltipRect(TooltipPanel panel)
        {
            try
            {
                if (panel == null) return null;
                if (_tooltipObjectField == null)
                    _tooltipObjectField = typeof(TooltipPanel).GetField("_tooltipObject",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                if (_tooltipObjectField == null) return null;

                var go = _tooltipObjectField.GetValue(panel) as GameObject;
                if (go == null || !go.activeInHierarchy) return null;
                return go.transform as RectTransform;
            }
            catch { return null; }
        }
    }

    /// <summary>Polls the hotkeys and ticks the panel; needs a MonoBehaviour of its own.</summary>
    public sealed class MaterialHelperDriver : MonoBehaviour
    {
        public MaterialHelperMod Mod;

        private void Update()
        {
            if (Mod == null) return;

            try { Mod.UpdatePin(); } catch { }

            // Hold-G + click is handled inside UpdatePin (it owns the click/pin state machine).
            // A plain G tap keeps its old meaning: chain-fill the open producer's recipe.
            if (Mod.FillKey != null && Mod.FillKey.IsDown && !GameKeys.GetShiftHeld())
            {
                // Bare G only opens the panel; filling is explicit (hold G + click, or 一键补齐).
                try { Mod.FillOpenProducer(true); } catch { }
            }

            if (Mod.PanelKey != null && Mod.PanelKey.IsDown)
            {
                try { Mod.TogglePanel(); } catch { }
            }

            try { Mod.Panel?.Tick(); } catch { }
        }
    }

    /// <summary>
    /// Wires the mod's actions into a vanilla `RecipeItemDisplay`. The click events are PUBLIC
    /// UnityEvents, so this needs no reflection and no Harmony on the cell itself.
    /// </summary>
    public sealed class IngredientRowHook : MonoBehaviour
    {
        private MaterialHelperMod _mod;
        private RecipeItemDisplay _cell;
        private bool _listening;

        public void Bind(MaterialHelperMod mod)
        {
            _mod = mod;
            if (_listening) return;

            _cell = GetComponent<RecipeItemDisplay>();
            if (_cell == null) return;

            try
            {
                _cell.OnLeftClickEvent.AddListener(OnLeft);
                _cell.OnRightClickEvent.AddListener(OnRight);
                _listening = true;
            }
            catch { }
        }

        private void OnLeft()
        {
            try { _mod?.OnNativeCellClicked(_cell); } catch { }
        }

        private void OnRight()
        {
            try { _mod?.OnNativeCellRightClicked(_cell); } catch { }
        }

        public void Detach()
        {
            if (!_listening || _cell == null) { _listening = false; return; }
            try
            {
                _cell.OnLeftClickEvent.RemoveListener(OnLeft);
                _cell.OnRightClickEvent.RemoveListener(OnRight);
            }
            catch { }
            _listening = false;
            _mod = null;
        }

        private void OnDestroy()
        {
            Detach();
        }
    }

    /// <summary>
    /// Small native-styled badge on a recipe toggle showing how many materials are short.
    /// Clicking it opens the 材料助手 panel on that recipe instead of silently queueing something.
    /// </summary>
    public sealed class RecipeToggleBadge : MonoBehaviour
    {
        private const float Width = 54f;
        private const float Height = 18f;

        private MaterialHelperMod _mod;
        private ProductionPanelRecipeToggle _toggle;
        private GameObject _root;
        private TMPro.TMP_Text _label;
        private float _nextCheck;
        private int _lastMissing = -1;

        public void Bind(MaterialHelperMod mod, ProductionPanelRecipeToggle toggle)
        {
            _mod = mod;
            _toggle = toggle;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            Refresh();
        }

        public void Refresh()
        {
            if (_mod == null || _toggle == null) return;

            Producer.Recipe recipe = null;
            try { recipe = _toggle.Recipe; } catch { }
            if (recipe == null)
            {
                _lastMissing = -1;
                SetVisible(false);
                return;
            }

            int missing = CountShortfalls(recipe);
            if (missing != _lastMissing)
            {
                _lastMissing = missing;
                SetVisible(missing > 0);
            }
            if (missing > 0 && _label != null) _label.text = $"缺 {missing}";
        }

        private static int CountShortfalls(Producer.Recipe recipe)
        {
            int count = 0;
            try
            {
                var ingredients = recipe.Ingredients;
                if (ingredients == null) return 0;
                for (int i = 0; i < ingredients.Count; i++)
                {
                    var ing = ingredients[i];
                    if (ing == null || ing.ItemProperties == null || ing.Amount <= 0) continue;
                    // Exactly the game's own shortfall test (RecipeDisplay.UpdateDisplay). The
                    // badge re-runs for every toggle, so it stays on the cheap inventory lookup;
                    // the panel adds "already on the way" on top.
                    if (GameProduction.HaveCount(ing.ItemProperties) >= ing.Amount) continue;
                    count++;
                }
            }
            catch { }
            return count;
        }

        private void SetVisible(bool visible)
        {
            if (!visible)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }

            EnsureVisual();
            if (_root != null && !_root.activeSelf) _root.SetActive(true);
        }

        private void EnsureVisual()
        {
            if (_root != null) return;
            try
            {
                _root = GameUi.NewUi("ModShortfallBadge", transform);
                var rt = GameUi.Rect(_root);
                // Inside the toggle rect, right-aligned: wins the raycast only in its own box.
                GameUi.Anchor(rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(-2f, 0f), new Vector2(Width, Height));

                var img = _root.AddComponent<Image>();
                if (NativeSkin.ButtonSprite != null)
                {
                    img.sprite = NativeSkin.ButtonSprite;
                    if (NativeSkin.ButtonSprite.border.sqrMagnitude > 0f) img.type = Image.Type.Sliced;
                    img.color = new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.92f);
                }
                else img.color = new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.92f);
                img.raycastTarget = true;

                var button = _root.AddComponent<Button>();
                button.targetGraphic = img;
                button.onClick.AddListener(OnClick);

                _label = GameUi.Label(_root.transform, "缺", 12, new Color(1f, 1f, 1f, 1f), TextAnchor.MiddleCenter);
                _label.raycastTarget = false;
                GameUi.Stretch(GameUi.Rect(_label.gameObject), 1f, 0f, 1f, 0f);
            }
            catch (Exception e)
            {
                _mod?.Context?.Log.Warn("badge create failed: " + e.Message);
                _root = null;
            }
        }

        private void OnClick()
        {
            if (_mod == null || _toggle == null) return;
            try
            {
                var producer = _toggle.Producer;
                var recipe = _toggle.Recipe;
                if (producer != null && recipe != null) producer.SetSelectedRecipe(recipe.Index);
            }
            catch { }
            try { _mod.Panel?.FocusItem(null); } catch { }
        }

        public void Clear()
        {
            _mod = null;
            _lastMissing = -1;
            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); } catch { }
                _root = null;
            }
            _label = null;
        }
    }

    /// <summary>
    /// Makes a vanilla construction-tooltip material slot clickable. The slot is a pooled
    /// `BuildableTooltipItemSlot`, so the item it shows is re-recorded on every Initialize and
    /// read at click time. A transparent full-cover graphic provides the raycast target and a
    /// faint red wash marks slots that are actually short.
    /// </summary>
    public sealed class ConstructionSlotHook : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler,
                                               IPointerExitHandler
    {
        private MaterialHelperMod _mod;
        private ItemProperties _item;
        private int _need = 1;
        private Image _overlay;
        private bool _hover;
        private float _nextTint;

        public ItemProperties Item => _item;
        public int Need => _need;

        public void Bind(MaterialHelperMod mod, ItemProperties item, int need)
        {
            _mod = mod;
            _item = item;
            _need = Mathf.Max(1, need);
            EnsureOverlay();
        }

        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            try
            {
                var go = GameUi.NewUi("ModSlotClick", transform, typeof(Image));
                _overlay = go.GetComponent<Image>();
                _overlay.color = new Color(0f, 0f, 0f, 0f);
                _overlay.raycastTarget = true;
                GameUi.Stretch(GameUi.Rect(go));
            }
            catch { _overlay = null; }
        }

        public void OnPointerEnter(PointerEventData eventData) { _hover = true; }
        public void OnPointerExit(PointerEventData eventData) { _hover = false; }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_mod == null || _item == null) return;
            if (UiDragHandler.JustDragged) return;
            try { _mod.ResolveItem(_item); } catch { }
        }

        private void Update()
        {
            if (_overlay == null || _item == null) return;
            if (Time.unscaledTime < _nextTint) return;
            _nextTint = Time.unscaledTime + 0.4f;

            // Short slots get a faint red wash; hovering any slot brightens it, so the player
            // can tell the cells are clickable without the tooltip stealing the pointer.
            int have = GameProduction.HaveCount(_item);
            int need = 1;
            bool short_ = have < need;
            try
            {
                var slot = GetComponent<BuildableTooltipItemSlot>();
                if (slot != null)
                {
                    var field = typeof(BuildableTooltipItemSlot).GetField("_slotItem",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var cip = field != null ? field.GetValue(slot) as CountedItemProperty : null;
                    if (cip != null) { _item = cip.ItemProperties; need = Mathf.Max(1, cip.Amount); }
                    _need = need;
                }
            }
            catch { }
            short_ = have < need;

            var tint = _hover
                ? new Color(0.40f, 0.85f, 0.66f, 0.30f)
                : short_ ? new Color(0.90f, 0.35f, 0.30f, 0.16f) : new Color(0f, 0f, 0f, 0f);
            if (_overlay.color != tint) _overlay.color = tint;
        }

        private void OnDestroy()
        {
            _mod = null;
            _item = null;
            _overlay = null;
        }
    }

    /// <summary>
    /// "How do I get this?" popup for materials no building can produce. Lists what is in
    /// storage, what is already on the way, and the world objects whose salvage composition
    /// contains the item — the data-driven answer for scrap metal, driftwood and friends.
    /// </summary>
    public sealed class SourcePopup
    {
        private readonly MaterialHelperMod _mod;
        private GameObject _overlay;
        private UiWindow _window;
        private RectTransform _content;
        private TMP_Text _title;
        private readonly List<GameObject> _rows = new List<GameObject>();

        public SourcePopup(MaterialHelperMod mod)
        {
            _mod = mod;
        }

        public void Show(ItemProperties item)
        {
            if (item == null) return;
            EnsureWindow();
            if (_window == null) return;

            ClearRows();
            _window.SetTitle("如何获得：" + GameProduction.ItemName(item));

            int have = GameProduction.HaveCount(item);
            int incoming = GameProduction.IncomingCount(item);
            AddLine($"库存 {have} · 在途 {incoming}", have > 0 || incoming > 0 ? GameUi.Good : GameUi.Danger);

            var producers = GameProduction.FindProducersFor(item);
            if (producers.Count > 0)
            {
                AddHeader("可由以下建筑生产：");
                for (int i = 0; i < producers.Count && i < 6; i++)
                    AddProducerRow(producers[i], item);
            }
            else
            {
                AddHeader("没有建筑能生产它。来源：");
                var sources = GameProduction.SalvageSources(item);
                if (sources.Count == 0)
                {
                    AddLine("· 只能从漂浮垃圾 / 残骸打捞中获得", GameUi.DimText);
                }
                else
                {
                    for (int i = 0; i < sources.Count; i++)
                        AddLine("· 打捞 " + sources[i], GameUi.TextColor);
                }
                AddLine("提示：派漂流者去打捞标记点，或拖回漂浮物拆解。", GameUi.DimText);
            }

            _window.Visible = true;
        }

        public void Hide()
        {
            if (_window != null) _window.Visible = false;
        }

        private void EnsureWindow()
        {
            if (_window != null) return;
            if (!GameApi.IsPlaying) return;
            NativeSkin.Harvest();

            try
            {
                _overlay = _mod.Context.Ui.CreateOverlay("materialsource", 30800);
                _window = GameUi.Window(_overlay.transform, "如何获得", new Vector2(420f, 320f),
                                        new Vector2(0f, -120f), _mod.Context.Config, "popup",
                                        Hide, 28f);

                var scroll = GameUi.ScrollList(_window.Body, "Lines", out _content);
                GameUi.Stretch(GameUi.Rect(scroll.gameObject));
            }
            catch (Exception e)
            {
                _mod.Context.Log.Error("source popup build failed: " + e.Message);
                _window = null;
            }
        }

        private void AddHeader(string text)
        {
            if (_content == null) return;
            var row = GameUi.Row(_content, 22f, new Color(0f, 0f, 0f, 0.06f));
            var label = GameUi.Label(row.transform, text, 13, GameUi.Accent, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 8f, 1f, 8f, 1f);
            _rows.Add(row);
        }

        private void AddLine(string text, Color color)
        {
            if (_content == null) return;
            var row = GameUi.Row(_content, 22f, new Color(0f, 0f, 0f, 0f));
            var label = GameUi.Label(row.transform, text, 13, color, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 10f, 1f, 8f, 1f);
            _rows.Add(row);
        }

        private void AddProducerRow(GameProduction.Candidate candidate, ItemProperties item)
        {
            if (_content == null) return;
            var row = GameUi.Row(_content, 28f, new Color(0f, 0f, 0f, 0.03f));

            var icon = GameUi.Icon(row.transform, GameBuildings.IconOf(candidate.Owner), 20f);
            GameUi.Anchor(GameUi.Rect(icon.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                          new Vector2(8f, 0f), new Vector2(20f, 20f));

            var label = GameUi.Label(row.transform,
                                     $"{GameBuildings.NameOf(candidate.Owner)}  {candidate.Distance:0} 单位" +
                                     (candidate.Enabled ? "" : "（未启用）"),
                                     13, candidate.Enabled ? GameUi.TextColor : GameUi.DimText,
                                     TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            var lrt = GameUi.Rect(label.gameObject);
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.anchoredPosition = new Vector2(34f, 0f);
            lrt.sizeDelta = new Vector2(-100f, -4f);

            var go = GameUi.TextButton(row.transform, "打开", () =>
            {
                GameProduction.OpenPanel(candidate.Producer, _mod.Zoom);
                _mod.Toast($"已打开 {GameBuildings.NameOf(candidate.Owner)} 的生产面板", ToastKind.Info);
            }, 12);
            var grt = GameUi.Rect(go.gameObject);
            grt.anchorMin = new Vector2(1f, 0.5f);
            grt.anchorMax = new Vector2(1f, 0.5f);
            grt.pivot = new Vector2(1f, 0.5f);
            grt.anchoredPosition = new Vector2(-8f, 0f);
            grt.sizeDelta = new Vector2(56f, 22f);

            _rows.Add(row);
        }

        private void ClearRows()
        {
            foreach (var row in _rows)
                if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();
        }
    }
}
