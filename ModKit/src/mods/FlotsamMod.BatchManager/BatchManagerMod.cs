using System;
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
        private bool _iconSet;
        private float _lastTickErrorLog = -99f;

        internal bool HighlightChecked = true;
        internal int ConfirmThreshold = 5;
        internal int MaxRows = 400;
        internal float Zoom = 0.6f;
        internal bool SortByDistance;
        internal bool IncludeUnfinished = true;
        internal bool HideAfterFocus;
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
            MaxRows = Mathf.Max(1, Config.Get("maxRows", 400));
            Zoom = Mathf.Clamp(Config.Get("zoomLevel", 0.6f), 0.05f, 3f);
            SortByDistance = Config.Get("sortByDistance", false);
            IncludeUnfinished = Config.Get("includeUnfinished", true);
            HideAfterFocus = Config.Get("hideAfterFocus", false);
            Verbose = Config.Get("verbose", false);
            if (Config.Get("schema", 0) < 1)
            {
                Config.Set("schema", 1);
                Config.Save();
            }
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
            // The icon is set later in OnTick: harvested sprites only exist inside a save.

            Log.Info("batch manager ready");
        }

        public override void OnDisable()
        {
            try { _button?.Destroy(); } catch { }
            _button = null;
            _iconSet = false;
            Teardown();
            Log.Info("batch manager removed");
        }

        public override void OnGameStart()
        {
            MarkDirty();   // 读档后建筑数据是新的，强制下次打开/刷新时重建分组
            Ui.Toast("批量管理就绪：Ctrl+1 开关；勾选即在世界中高亮，◀▶ 逐个跳转核对", ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            _iconSet = false;   // harvested sprites die with the scene; re-apply the icon next save
            Teardown();
        }

        public override void OnTick()
        {
            EnsureUi();
            if (!_iconSet && GameApi.IsPlaying && NativeSkin.Available)
            {
                _iconSet = true;
                try { _button?.SetIcon(NativeSkin.Find("build", "hammer", "construction")); } catch { }
            }
            if (_hotkey != null && _hotkey.IsDown && GameKeys.GetCtrlHeld()) Toggle();
            try { _panel?.Tick(); }
            catch (Exception e)
            {
                // Never swallow panel errors silently: a throwing Refresh would otherwise leave
                // the lists stale with zero evidence in the log. Throttled to 1/s against spam.
                float now = Time.realtimeSinceStartup;
                if (now - _lastTickErrorLog > 1f)
                {
                    _lastTickErrorLog = now;
                    Log.Error("panel tick failed", e);
                }
            }
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
