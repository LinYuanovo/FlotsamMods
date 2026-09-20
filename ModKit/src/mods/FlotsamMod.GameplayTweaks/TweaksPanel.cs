using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.GameplayTweaks
{
    /// <summary>
    /// The tweaks window: a master switch + reset on top, a scrollable stack of seven native-skinned
    /// sliders grouped into 重量 / 发电 / 美观度, and a help line at the bottom (the header's tips
    /// toggle). Drag or click a track to set a percentage of vanilla; the effect is live, releasing
    /// persists it to config and writes one log line. Built lazily on game start and torn down on
    /// game end, like every other mod panel, because a scene change destroys the harvested sprites.
    ///
    /// Layout mirrors MaterialPanel: Uniform content mode (the whole body scales with the window, so
    /// any size works), a ScrollList as the flexible middle, and a TipsChanged handler that grows the
    /// list into the space the help line gives back — so hiding tips never leaves an empty band.
    /// </summary>
    public sealed class TweaksPanel
    {
        private const float Pad = 14f;
        private const float HeaderH = 22f;
        private const float SliderH = 30f;
        private const float ButtonH = 32f;
        private const float HelpH = 40f;

        private const float ScrollTop = -44f;      // below the button row
        private const float ScrollBottomTips = 46f; // above the help line
        private const float ScrollBottomNoTips = 6f;

        private readonly GameplayTweaksMod _mod;

        private GameObject _overlay;
        private UiWindow _window;
        private ScrollRect _scroll;
        private RectTransform _content;
        private Button _masterButton;
        private TMP_Text _masterLabel;
        private Image _masterBg;
        private readonly List<Bound> _bound = new List<Bound>();
        private bool _visible;

        public bool Visible => _visible;

        private struct Bound
        {
            public string Key;
            public UiSliderRow Row;
        }

        public TweaksPanel(GameplayTweaksMod mod)
        {
            _mod = mod;
        }

        // ------------------------------------------------------------ lifecycle

        public void Build()
        {
            if (_window != null) return;
            if (!GameApi.IsPlaying) return;
            NativeSkin.Harvest();

            try
            {
                var ui = _mod.Context.Ui;
                var config = _mod.Context.Config;
                _overlay = ui.CreateOverlay("gameplaytweaks", 30650);

                _window = GameUi.Window(_overlay.transform, "游戏性调整", new Vector2(486f, 540f),
                                        new Vector2(0f, 20f), config, "panel", Hide, 28f);
                _window.MinSize = new Vector2(452f, 360f);
                _window.MaxSize = new Vector2(1000f, 940f);

                BuildButtons();
                BuildScroll();
                BuildHelp();

                _visible = _mod.ShowPanel;
                _window.Visible = _visible;
                SyncFromState();
            }
            catch (Exception e)
            {
                _mod.Context.Log.Error("tweaks panel build failed: " + e.Message);
            }
        }

        private void BuildButtons()
        {
            var row = GameUi.NewUi("Buttons", _window.Body.transform);
            PlaceTop(GameUi.Rect(row), -6f, ButtonH);

            _masterButton = GameUi.TextButton(row.transform, "总开关：开", OnMaster, 14);
            _masterLabel = _masterButton.GetComponentInChildren<TMP_Text>();
            _masterBg = _masterButton.targetGraphic as Image;
            var mrt = GameUi.Rect(_masterButton.gameObject);
            mrt.anchorMin = new Vector2(0f, 0f);
            mrt.anchorMax = new Vector2(0.5f, 1f);
            mrt.offsetMin = new Vector2(0f, 0f);
            mrt.offsetMax = new Vector2(-4f, 0f);

            var reset = GameUi.TextButton(row.transform, "恢复默认", () => _mod.ResetToDefaults(), 14);
            var rrt = GameUi.Rect(reset.gameObject);
            rrt.anchorMin = new Vector2(0.5f, 0f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.offsetMin = new Vector2(4f, 0f);
            rrt.offsetMax = new Vector2(0f, 0f);
        }

        private void BuildScroll()
        {
            _scroll = GameUi.ScrollList(_window.Body.transform, "Tweaks", out _content);
            var srt = GameUi.Rect(_scroll.gameObject);
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.offsetMin = new Vector2(0f, ScrollBottomTips);
            srt.offsetMax = new Vector2(0f, ScrollTop);

            // ScrollList leaves childControlHeight off; turn it on so each row's LayoutElement
            // drives its height (headers and sliders stack predictably).
            var vlg = _content.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.childControlHeight = true;
                vlg.childForceExpandHeight = false;
                vlg.spacing = 5f;
            }

            Header("重量");
            Slider("重量能耗影响", GameplayTweaksMod.KWeightEnergy, 0f, 300f);
            Slider("载重上限倍率", GameplayTweaksMod.KTugCapacity, 100f, 1000f);

            Header("发电");
            Slider("发电效率", GameplayTweaksMod.KGenerator, 0f, 500f);

            Header("美观度 · 建筑分");
            Slider("负面建筑贡献", GameplayTweaksMod.KBeautyNegBuild, 0f, 200f);
            Slider("正面建筑贡献", GameplayTweaksMod.KBeautyPosBuild, 0f, 500f);

            Header("美观度 · 对士气");
            Slider("负面士气修正", GameplayTweaksMod.KBeautyNegMorale, 0f, 200f);
            Slider("正面士气修正", GameplayTweaksMod.KBeautyPosMorale, 0f, 500f);
        }

        private void BuildHelp()
        {
            var help = GameUi.Label(_window.Body.transform,
                "100% = 原版。重量：影响移动能耗与还能承载多少建筑；发电：所有发电机产出；\n" +
                "美观：建筑美观分与其对居民士气的作用。改动即时生效并记忆，关总开关整体还原。",
                11, GameUi.DimText, TextAnchor.MiddleLeft, true, bold: false);
            PlaceBottom(GameUi.Rect(help.gameObject), 0f, HelpH);

            _window.TipsHeight = HelpH;
            _window.TipsChanged += show =>
            {
                if (_scroll == null) return;
                var srt = GameUi.Rect(_scroll.gameObject);
                srt.offsetMin = new Vector2(0f, show ? ScrollBottomTips : ScrollBottomNoTips);
            };
            _window.SetTips(GameUi.Rect(help.gameObject));
        }

        public void Destroy()
        {
            _bound.Clear();
            if (_window != null) _window.Destroy();
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
            }
            _window = null;
            _overlay = null;
            _scroll = null;
            _content = null;
            _masterButton = null;
            _masterLabel = null;
            _masterBg = null;
        }

        public void SetVisible(bool value)
        {
            _visible = value;
            if (_window != null) _window.Visible = value;
            if (value) SyncFromState();
        }

        public void Toggle() => SetVisible(!_visible);

        private void Hide()
        {
            SetVisible(false);
            _mod.OnPanelHidden();
        }

        // ------------------------------------------------------------ content rows

        private void Header(string text)
        {
            var go = GameUi.NewUi("Header", _content);
            FixedHeight(go, HeaderH);
            var label = GameUi.Label(go.transform, text, 14, GameUi.Accent, TextAnchor.MiddleLeft);
            GameUi.Stretch(GameUi.Rect(label.gameObject), 4f, 0f, 4f, 0f);
        }

        private void Slider(string label, string key, float min, float max)
        {
            var row = GameUi.SliderRow(_content, label, min, max, 100f,
                                       pct => _mod.LiveSet(key, pct),
                                       pct => _mod.Commit(key, pct, label),
                                       150f, 62f, 16f, 14);
            FixedHeight(row.Root, SliderH);
            _bound.Add(new Bound { Key = key, Row = row });
        }

        private static void FixedHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
        }

        // ------------------------------------------------------------ placement helpers

        private void PlaceTop(RectTransform rt, float y, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(-2f * Pad, height);
        }

        private void PlaceBottom(RectTransform rt, float y, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(-2f * Pad, height);
        }

        // ------------------------------------------------------------ sync

        public void SyncFromState()
        {
            foreach (var b in _bound)
                b.Row.SetSilent(CurrentPct(b.Key));
            UpdateMaster();
        }

        private static float CurrentPct(string key)
        {
            switch (key)
            {
                case GameplayTweaksMod.KWeightEnergy: return GameplayTweaksMod.WeightEnergy * 100f;
                case GameplayTweaksMod.KTugCapacity: return GameplayTweaksMod.TugCapacity * 100f;
                case GameplayTweaksMod.KGenerator: return GameplayTweaksMod.Generator * 100f;
                case GameplayTweaksMod.KBeautyNegBuild: return GameplayTweaksMod.BeautyNegBuild * 100f;
                case GameplayTweaksMod.KBeautyPosBuild: return GameplayTweaksMod.BeautyPosBuild * 100f;
                case GameplayTweaksMod.KBeautyNegMorale: return GameplayTweaksMod.BeautyNegMorale * 100f;
                case GameplayTweaksMod.KBeautyPosMorale: return GameplayTweaksMod.BeautyPosMorale * 100f;
                default: return 100f;
            }
        }

        private void OnMaster()
        {
            _mod.ToggleActive();
            UpdateMaster();
        }

        private void UpdateMaster()
        {
            bool on = GameplayTweaksMod.Active;
            if (_masterLabel != null)
            {
                _masterLabel.text = on ? "总开关：开" : "总开关：关";
                _masterLabel.color = on ? GameUi.TextColor : GameUi.DimText;
            }
            if (_masterBg != null)
                _masterBg.color = on
                    ? new Color(GameUi.Accent.r, GameUi.Accent.g, GameUi.Accent.b, 0.35f)
                    : Color.white;

            foreach (var b in _bound)
            {
                if (b.Row.ValueText != null) b.Row.ValueText.color = on ? GameUi.Accent : GameUi.DimText;
                if (b.Row.Label != null) b.Row.Label.color = on ? GameUi.TextColor : GameUi.DimText;
            }
        }
    }
}
