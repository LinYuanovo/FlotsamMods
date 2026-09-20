using System;
using System.Reflection;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using HarmonyLib;
using PajamaLlama.Flotsam.Morale;
using UnityEngine;

namespace FlotsamMods.GameplayTweaks
{
    /// <summary>
    /// A single panel of balance sliders, each a percentage of the vanilla behaviour.
    ///
    /// Everything is a Harmony postfix that reads a static multiplier and rewrites the game's own
    /// result, so the effect is live and reverts the moment the mod is disabled (the host revokes
    /// every patch in <c>ModEntry.SafeDisable</c>). The seven knobs, and the exact game value each
    /// one bends (all line numbers are in _mod_recon/src/Assembly-CSharp.decompiled.cs):
    ///
    ///  1. 重量能耗影响  — town weight does NOT lower top speed in Flotsam; it raises the energy
    ///     cost of moving. `Engine.ReturnEelsPerUnit` (22405) bills
    ///     `GameplaySettings.ComputeEelsPerUnit(TownWeight)` (143089). We scale only the part that
    ///     weight ADDS over the zero-weight baseline: `base + (result - base) * pct`. 0% makes
    ///     weight free, 100% is vanilla.
    ///  2. 载重上限倍率 — placement is gated by tug capacity: `PlaceableProperties.ReturnCanBePlaced`
    ///     -> `Engine.CanTug` (22441) -> `buildingWeight <= TownAvailableTugCapacity`, and
    ///     `TownAvailableTugCapacity = max(ReturnTownTugCapacity() - TownWeight, 0)` (22255-22257).
    ///     Scaling `Engine.ReturnTownTugCapacity` (22419) lifts the ceiling so an overweight town
    ///     can keep building. It does not touch TownWeight, so knob 1 is unaffected.
    ///  3. 发电效率 — `EnergyGrid.ReturnEnergyProduction` (39070) sums each producer's `Production`.
    ///     Scaling the getter of all three producers (passive 21711 / item 21098 / manual 21431)
    ///     boosts every generator, and everything derived from Production stays consistent.
    ///  4-5. 建筑美观贡献 — `Community.BeautyScore` (7344) is the sum of `Buildable.ReturnBeautyScore`
    ///     (17387). We scale negative and positive scores separately, then force
    ///     `Community.UpdateBeautyScore` (7783) so the cached total reflects the change at once.
    ///  6-7. 美观→士气修正 — `TownBeautyMoraleEffect.ReturnModifier` (217301) maps the score through
    ///     Threshold->Modifier bands and is summed by `Morale.ReturnMoraleModifierSum` (217798).
    ///     Scaled live at read time, so no recompute is needed.
    /// </summary>
    public sealed class GameplayTweaksMod : FlotsamModBase
    {
        private static GameplayTweaksMod _current;

        // ------------------------------------------------------------ live state (read by patches)

        /// <summary>Master switch. When off every patch returns the vanilla value.</summary>
        public static bool Active = true;

        public static float WeightEnergy = 1f;     // 0..3   重量能耗影响
        public static float TugCapacity = 1f;      // 1..10  载重上限倍率
        public static float Generator = 1f;        // 0..5   发电效率
        public static float BeautyNegBuild = 1f;   // 0..2   建筑·负面美观贡献
        public static float BeautyPosBuild = 1f;   // 0..5   建筑·正面美观贡献
        public static float BeautyNegMorale = 1f;  // 0..2   士气·负面修正
        public static float BeautyPosMorale = 1f;  // 0..5   士气·正面修正

        // ------------------------------------------------------------ config keys

        internal const string KActive = "active";
        internal const string KWeightEnergy = "weightEnergyPct";
        internal const string KTugCapacity = "tugCapacityPct";
        internal const string KGenerator = "generatorPct";
        internal const string KBeautyNegBuild = "beautyNegBuildPct";
        internal const string KBeautyPosBuild = "beautyPosBuildPct";
        internal const string KBeautyNegMorale = "beautyNegMoralePct";
        internal const string KBeautyPosMorale = "beautyPosMoralePct";
        internal const string KShowPanel = "showPanel";
        internal const string KVerbose = "verbose";

        // ------------------------------------------------------------ eels baseline cache

        private static MethodInfo _computeEels;
        private static float _eelBaseline = -1f;
        private static GameplaySettings _eelBaselineFor;
        [ThreadStatic] private static bool _eelGuard;

        // ------------------------------------------------------------ instance

        private IKeybind _panelKey;
        private IHudButton _hudButton;
        private TweaksPanel _panel;

        public bool ShowPanel { get; private set; }
        public bool Verbose { get; private set; }

        public IModContext Context => Ctx;
        public IKeybind PanelKey => _panelKey;
        public TweaksPanel Panel => _panel;

        // ------------------------------------------------------------ lifecycle

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);

            _panelKey = Keybinds.Register("tweaks.panel", KeyCode.F7, "显示/隐藏游戏性调整面板",
                                          KeybindOptions.AllowWhilePaused);

            Active = Config.Get(KActive, true);
            WeightEnergy = Pct(Config.Get(KWeightEnergy, 100), 0f, 300f);
            TugCapacity = Pct(Config.Get(KTugCapacity, 100), 100f, 1000f);
            Generator = Pct(Config.Get(KGenerator, 100), 0f, 500f);
            BeautyNegBuild = Pct(Config.Get(KBeautyNegBuild, 100), 0f, 200f);
            BeautyPosBuild = Pct(Config.Get(KBeautyPosBuild, 100), 0f, 500f);
            BeautyNegMorale = Pct(Config.Get(KBeautyNegMorale, 100), 0f, 200f);
            BeautyPosMorale = Pct(Config.Get(KBeautyPosMorale, 100), 0f, 500f);
            ShowPanel = Config.Get(KShowPanel, false);
            Verbose = Config.Get(KVerbose, false);

            Log.Info($"loaded — active={Active}, weight-energy {WeightEnergy:P0}, tug {TugCapacity:P0}, " +
                     $"generator {Generator:P0}, beauty build -{BeautyNegBuild:P0}/+{BeautyPosBuild:P0}, " +
                     $"morale -{BeautyNegMorale:P0}/+{BeautyPosMorale:P0}, panel key {_panelKey.Key}");
        }

        private static float Pct(int stored, float minPct, float maxPct)
            => Mathf.Clamp(stored, minPct, maxPct) / 100f;

        public override void OnEnable()
        {
            _current = this;
            int ok = 0;

            // 1) weight -> movement energy cost
            _computeEels = GamePatches.Method(typeof(GameplaySettings), "ComputeEelsPerUnit", typeof(float)) as MethodInfo;
            ok += Register(_computeEels, nameof(AfterComputeEels), "GameplaySettings.ComputeEelsPerUnit");

            // 2) tug capacity -> build placement ceiling
            ok += Register(GamePatches.MethodByName(typeof(Engine), "ReturnTownTugCapacity"),
                           nameof(AfterTownTugCapacity), "Engine.ReturnTownTugCapacity");

            // 3) generator output (three producer types share one postfix)
            ok += Register(GamePatches.PropertyGetter(typeof(EnergyPassiveGenerator), "Production"),
                           nameof(AfterProduction), "EnergyPassiveGenerator.Production");
            ok += Register(GamePatches.PropertyGetter(typeof(EnergyItemProducer), "Production"),
                           nameof(AfterProduction), "EnergyItemProducer.Production");
            ok += Register(GamePatches.PropertyGetter(typeof(EnergyManualProducer), "Production"),
                           nameof(AfterProduction), "EnergyManualProducer.Production");

            // 4-5) per-building beauty contribution
            ok += Register(GamePatches.MethodByName(typeof(Buildable), "ReturnBeautyScore"),
                           nameof(AfterBeautyScore), "Buildable.ReturnBeautyScore");

            // 6-7) beauty -> morale modifier
            ok += Register(GamePatches.MethodByName(typeof(TownBeautyMoraleEffect), "ReturnModifier"),
                           nameof(AfterBeautyMorale), "TownBeautyMoraleEffect.ReturnModifier");

            _hudButton = Ui.AddHudButton("gameplaytweaks.panel", "游戏性调整", TogglePanel,
                                         HudAnchor.LeftTop, Config, "button");
            _hudButton.Visible = true;

            _panel = new TweaksPanel(this);
            _panel.Build();

            Log.Info($"ready — {ok}/7 patch(es) applied; HUD button + panel key {_panelKey.Key}");
            if (ok < 7) Log.Warn("one or more patch targets were not found — those knobs stay inert (game update?)");
        }

        private int Register(MethodBase target, string patchName, string label)
        {
            if (target == null)
            {
                Log.Warn("patch target not found: " + label);
                return 0;
            }
            var patch = AccessTools.Method(typeof(GameplayTweaksMod), patchName);
            if (Patches.Postfix(target, patch)) return 1;
            Log.Warn("patch failed: " + label);
            return 0;
        }

        public override void OnTick()
        {
            if (_panelKey != null && _panelKey.IsDown) TogglePanel();
        }

        public override void OnDisable()
        {
            ResetBaseline();
            try { _panel?.Destroy(); } catch { }
            _panel = null;
            try { _hudButton?.Destroy(); } catch { }
            _hudButton = null;
            _current = null;
            Log.Info("disabled — patches revoked by host, vanilla balance restored");
        }

        public override void OnGameStart()
        {
            ResetBaseline();
            if (_panel == null) _panel = new TweaksPanel(this);
            _panel.Build();
            RefreshBeauty();
            var key = _panelKey != null ? _panelKey.Key.ToString() : "F7";
            Ui.Toast($"游戏性调整就绪：按 {key} 或点左上角按钮打开面板（重量/发电/美观 共 7 项）",
                     ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            ResetBaseline();
            try { _panel?.Destroy(); } catch { }
            _panel = null;
        }

        private static void ResetBaseline()
        {
            _eelBaseline = -1f;
            _eelBaselineFor = null;
        }

        // ------------------------------------------------------------ panel actions

        internal void TogglePanel()
        {
            if (_panel == null) _panel = new TweaksPanel(this);
            _panel.Build();
            _panel.Toggle();
            ShowPanel = _panel.Visible;
            Config.Set(KShowPanel, ShowPanel);
            Config.Save();
        }

        /// <summary>The window's own ✕ closed it; remember that so it stays closed next load.</summary>
        internal void OnPanelHidden()
        {
            ShowPanel = false;
            Config.Set(KShowPanel, false);
            Config.Save();
        }

        internal void SetActive(bool value, bool log = true)
        {
            if (Active == value) return;
            Active = value;
            Config.Set(KActive, value);
            Config.Save();
            RefreshBeauty();   // the cached town beauty total must follow the master switch
            if (log) Log.Info($"总开关 -> {(value ? "开" : "关（全部恢复原版）")}");
        }

        internal void ToggleActive() => SetActive(!Active);

        /// <summary>Live slider move: bend the value now, but do not touch disk or the log.</summary>
        internal void LiveSet(string key, float pct)
        {
            // Quantise to whole percent so the readout, the applied multiplier and the stored
            // config value are always the same number.
            float m = Mathf.RoundToInt(pct) / 100f;
            switch (key)
            {
                case KWeightEnergy: WeightEnergy = m; break;
                case KTugCapacity: TugCapacity = m; break;
                case KGenerator: Generator = m; break;
                case KBeautyNegBuild: BeautyNegBuild = m; break;
                case KBeautyPosBuild: BeautyPosBuild = m; break;
                case KBeautyNegMorale: BeautyNegMorale = m; break;
                case KBeautyPosMorale: BeautyPosMorale = m; break;
            }
        }

        /// <summary>Slider released: persist, log one line, and refresh anything cached.</summary>
        internal void Commit(string key, float pct, string label)
        {
            LiveSet(key, pct);
            Config.Set(key, Mathf.RoundToInt(pct));
            Config.Save();

            // Only the building-score knobs feed a cached total; morale is read live.
            if (key == KBeautyNegBuild || key == KBeautyPosBuild) RefreshBeauty();

            Log.Info($"{label} -> {Mathf.RoundToInt(pct)}%");
        }

        internal void ResetToDefaults()
        {
            Active = true;
            WeightEnergy = TugCapacity = Generator = 1f;
            BeautyNegBuild = BeautyPosBuild = BeautyNegMorale = BeautyPosMorale = 1f;

            Config.Set(KActive, true);
            Config.Set(KWeightEnergy, 100);
            Config.Set(KTugCapacity, 100);
            Config.Set(KGenerator, 100);
            Config.Set(KBeautyNegBuild, 100);
            Config.Set(KBeautyPosBuild, 100);
            Config.Set(KBeautyNegMorale, 100);
            Config.Set(KBeautyPosMorale, 100);
            Config.Save();

            ResetBaseline();
            RefreshBeauty();
            _panel?.SyncFromState();
            Log.Info("all knobs reset to 100% (vanilla)");
            Ui.Toast("已恢复全部默认（100%）", ToastKind.Info);
        }

        /// <summary>Recomputes the cached town beauty total so building-score changes apply now.</summary>
        internal void RefreshBeauty()
        {
            if (!GameApi.IsPlaying) return;
            try
            {
                var community = GameApi.PlayerCommunity;
                if (community != null) community.UpdateBeautyScore();
            }
            catch (Exception e)
            {
                if (Verbose) Log.Warn("beauty refresh failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------ patches

        private static void AfterComputeEels(GameplaySettings __instance, ref float __result)
        {
            if (!Active || _eelGuard) return;
            float influence = WeightEnergy;
            if (Mathf.Approximately(influence, 1f)) return;

            // Baseline = the cost with zero town weight; the knob scales only what weight adds.
            if (_eelBaselineFor != __instance || _eelBaseline < 0f)
            {
                float baseline = __result;
                if (_computeEels != null)
                {
                    _eelGuard = true;
                    try { baseline = (float)_computeEels.Invoke(__instance, new object[] { 0f }); }
                    catch { }
                    finally { _eelGuard = false; }
                }
                _eelBaseline = baseline;
                _eelBaselineFor = __instance;
            }

            __result = Mathf.Max(0f, _eelBaseline + (__result - _eelBaseline) * influence);
        }

        private static void AfterTownTugCapacity(ref float __result)
        {
            if (!Active) return;
            float m = TugCapacity;
            if (Mathf.Approximately(m, 1f)) return;
            __result *= m;
        }

        private static void AfterProduction(ref float __result)
        {
            if (!Active) return;
            float m = Generator;
            if (Mathf.Approximately(m, 1f)) return;
            __result *= m;
        }

        private static void AfterBeautyScore(ref int __result)
        {
            if (!Active) return;
            if (__result < 0) __result = Mathf.RoundToInt(__result * BeautyNegBuild);
            else if (__result > 0) __result = Mathf.RoundToInt(__result * BeautyPosBuild);
        }

        private static void AfterBeautyMorale(ref int __result)
        {
            if (!Active) return;
            if (__result < 0) __result = Mathf.RoundToInt(__result * BeautyNegMorale);
            else if (__result > 0) __result = Mathf.RoundToInt(__result * BeautyPosMorale);
        }
    }
}
