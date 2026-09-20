using System;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.KeyboardMove
{
    /// <summary>
    /// The sailing instrument panel: a native-skinned, draggable window whose position is
    /// remembered. It is the only feedback the player gets while a voyage is accumulating, so it
    /// states plainly why nothing is happening whenever movement is gated.
    /// </summary>
    public sealed class SailHud : MonoBehaviour
    {
        public KeyboardMoveMod Mod;
        public SailDriver Driver;

        private GameObject _overlay;
        private UiWindow _window;
        private TMP_Text _state, _heading, _speed, _leg, _trip, _range, _engine, _hint;
        private Button _settleButton, _pauseButton;
        private TMP_Text _pauseLabel;
        private RectTransform _footer;

        private const float FooterFull = 78f;
        private const float FooterCompact = 26f;
        private const float TipsBlock = FooterFull - FooterCompact;

        // Bottom of the last instrument row inside the body (4 top pad + 6 * 22 pitch + 20 row),
        // plus the body chrome (header 28 + 4 gap + 8 bottom margin) the window adds around it.
        private const float RowsBottom = 160f;
        private const float BodyChrome = 40f;

        private static float HeightFor(float footer) => RowsBottom + footer + BodyChrome;

        private bool _built;
        private float _nextTextUpdate;

        public void Build()
        {
            if (_built || Mod == null) return;

            // Native art only exists once the gameplay scene is up; retry from Update until then.
            if (!GameApi.IsPlaying) return;
            NativeSkin.Harvest();

            try
            {
                _overlay = Mod.Context.Ui.CreateOverlay("keyboardmove", 30400);

                _window = GameUi.Window(_overlay.transform, "航行仪表",
                                        new Vector2(300f, HeightFor(FooterFull)),
                                        new Vector2(-740f, 300f), Mod.Context.Config, "hud",
                                        () => Mod.ToggleHud(), 28f);

                // Fixed-content panel: the body stretches 1:1 instead of uniform-scaling, so
                // reclaiming the tips band collapses the layout exactly (no blank band) and the
                // text keeps its natural size.
                _window.ContentMode = WindowContentMode.None;
                _window.ApplyLayout();

                float y = -4f;
                _state = AddRow("状态", ref y);
                _heading = AddRow("航向", ref y);
                _speed = AddRow("速度", ref y);
                _leg = AddRow("本段航程", ref y);
                _trip = AddRow("累计航程", ref y);
                _range = AddRow("电量可航", ref y);
                _engine = AddRow("引擎", ref y);

                // Footer hangs directly under the last row (top-anchored), so hiding the tips
                // collapses buttons and window together with nothing left between them.
                var footer = GameUi.NewUi("Footer", _window.Body.transform);
                _footer = GameUi.Rect(footer);
                _footer.anchorMin = new Vector2(0f, 1f);
                _footer.anchorMax = new Vector2(1f, 1f);
                _footer.pivot = new Vector2(0.5f, 1f);
                _footer.anchoredPosition = new Vector2(0f, -RowsBottom);
                _footer.sizeDelta = new Vector2(0f, FooterFull);

                _settleButton = GameUi.TextButton(_footer, "立即靠港", SettleNow, 13);
                PlaceButton(_settleButton, true);
                _pauseButton = GameUi.TextButton(_footer, "暂停航行", TogglePause, 13);
                PlaceButton(_pauseButton, false);
                _pauseLabel = _pauseButton.GetComponentInChildren<TMP_Text>();

                _hint = GameUi.Label(_footer, Hint(), 12, GameUi.DimText, TextAnchor.UpperLeft, true, bold: false);
                var hrt = GameUi.Rect(_hint.gameObject);
                hrt.anchorMin = new Vector2(0f, 1f);
                hrt.anchorMax = new Vector2(1f, 1f);
                hrt.pivot = new Vector2(0.5f, 1f);
                hrt.anchoredPosition = new Vector2(0f, -30f);
                hrt.sizeDelta = new Vector2(-4f, 48f);

                _window.TipsHeight = TipsBlock;
                _window.TipsChanged += show =>
                {
                    if (_footer == null || _window == null) return;
                    float f = show ? FooterFull : FooterCompact;
                    _footer.sizeDelta = new Vector2(0f, f);
                    _window.SetSize(new Vector2(_window.Rect.sizeDelta.x, HeightFor(f)));
                };
                // The content height is fixed: a manual resize (or a stale persisted height from
                // an older build) only ever changes the width; the height snaps back to exact.
                _window.Resized += size =>
                {
                    if (_footer == null || _window == null) return;
                    float want = HeightFor(_footer.sizeDelta.y);
                    if (Mathf.Abs(size.y - want) > 0.5f)
                        _window.SetSize(new Vector2(size.x, want));
                };
                _window.SetTips(GameUi.Rect(_hint.gameObject));

                // Force the exact content height once more: repairs heights persisted by older
                // builds, whichever tips state was restored.
                _window.SetSize(new Vector2(_window.Rect.sizeDelta.x, HeightFor(_footer.sizeDelta.y)));
                _built = true;
                ApplyVisibility();
                Refresh(true);
                Mod.Context.Log.Info("sailing HUD built (" +
                                     (NativeSkin.Available ? "native skin" : "procedural fallback") + ")");
            }
            catch (Exception e)
            {
                Mod.Context.Log.Error("sailing HUD build failed: " + e.Message);
                _built = false;
            }
        }

        private string Hint()
        {
            if (Mod == null) return "";
            return $"{Mod.ForwardKey.Key}/{Mod.BackwardKey.Key} 进退 · {Mod.RotateLeftKey.Key}/{Mod.RotateRightKey.Key} 转向\n" +
                   $"松手即靠港并提交世界 · {Mod.SettleBind.Key} 立即靠港 · {Mod.ToggleBind.Key} 暂停\n" +
                   $"拖动标题栏移动本窗口，双击标题栏复位";
        }

        private TMP_Text AddRow(string caption, ref float y)
        {
            var cap = GameUi.Label(_window.Body, caption, 13, GameUi.DimText, TextAnchor.MiddleLeft);
            var crt = GameUi.Rect(cap.gameObject);
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(0f, 1f);
            crt.pivot = new Vector2(0f, 1f);
            crt.anchoredPosition = new Vector2(2f, y);
            crt.sizeDelta = new Vector2(84f, 20f);

            var value = GameUi.Label(_window.Body, "—", 13, GameUi.TextColor, TextAnchor.MiddleRight);
            var vrt = GameUi.Rect(value.gameObject);
            vrt.anchorMin = new Vector2(0f, 1f);
            vrt.anchorMax = new Vector2(1f, 1f);
            vrt.pivot = new Vector2(0.5f, 1f);
            vrt.anchoredPosition = new Vector2(44f, y);
            vrt.sizeDelta = new Vector2(-92f, 20f);

            y -= 22f;
            return value;
        }

        private void PlaceButton(Button btn, bool left)
        {
            var rt = GameUi.Rect(btn.gameObject);
            float edge = left ? 0f : 1f;
            rt.anchorMin = new Vector2(edge, 1f);
            rt.anchorMax = new Vector2(edge, 1f);
            rt.pivot = new Vector2(edge, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(132f, 26f);
        }

        private void SettleNow()
        {
            if (Driver == null) return;
            float leg = Driver.LegDistance;
            Driver.Settle("hud button");
            Mod.Report(leg > 0.01f ? $"已靠港：本段 {leg:0} 单位" : "已靠港：本段没有位移", ToastKind.Info);
        }

        private void TogglePause()
        {
            if (Mod == null) return;
            Mod.ToggleDriving();
        }

        public void ApplyVisibility()
        {
            if (_window == null) return;
            bool want = Mod != null && Mod.ShowHud && GameApi.IsPlaying && !GameApi.IsMapOpen;
            _window.Visible = want;
        }

        private void Update()
        {
            if (!_built)
            {
                Build();
                return;
            }

            ApplyVisibility();
            if (_window == null || !_window.Visible) return;

            if (Time.unscaledTime < _nextTextUpdate) return;
            _nextTextUpdate = Time.unscaledTime + 0.1f;
            Refresh(false);
        }

        private void Refresh(bool force)
        {
            if (Driver == null) return;

            string state;
            Color stateColor = GameUi.TextColor;
            if (!GameApi.IsPlaying) state = "等待存档";
            else if (GameMovement.AutoActive)
            {
                float remain = (GameMovement.AutoTarget - Driver.Position).magnitude;
                state = $"自动航行（剩 {remain:0}）";
                stateColor = GameUi.Good;
            }
            else if (Driver.Sailing) { state = "航行中（松手靠港）"; stateColor = GameUi.Good; }
            else if (Driver.Blocked != null) { state = Describe(Driver.Blocked); stateColor = GameUi.Danger; }
            else if (Mod != null && !Mod.DrivingEnabled) { state = "已暂停"; stateColor = new Color(0.96f, 0.76f, 0.32f); }
            else state = "停泊";

            Set(_state, state, stateColor);

            float heading = Driver.Heading;
            Set(_heading, $"{heading:000}°  {SailDriver.CompassOf(heading)}");
            Set(_speed, Driver.Sailing ? $"{Driver.Speed:0.0} 单位/秒" : "0.0 单位/秒");
            Set(_leg, Driver.Sailing
                ? $"{Driver.LegDistance:0} / {Mathf.Max(1f, Mod.CommitDistance):0}"
                : "0");
            Set(_trip, $"{Driver.TripDistance:0}");

            float range = GameMovement.RemainingRange;
            Set(_range, range > 0f ? $"{range:0} 单位" : "无电量", range > 0f ? GameUi.TextColor : GameUi.Danger);

            bool cooling = GameMovement.EngineCoolingDown;
            Set(_engine, cooling ? "冷却中（无法推进）" : "正常",
                cooling ? GameUi.Danger : GameUi.TextColor);

            if (_pauseLabel != null && Mod != null)
                _pauseLabel.text = Mod.DrivingEnabled ? "暂停航行" : "恢复航行";
        }

        private static string Describe(string blocked)
        {
            if (string.IsNullOrEmpty(blocked)) return "停泊";
            switch (blocked)
            {
                case "no game": return "等待存档";
                case "paused": return "游戏已暂停";
                case "typing": return "正在输入文字";
                case "map open (vanilla mover owns movement)": return "大地图已打开";
                case "panel blocks camera input": return "有面板占用输入";
                case "town movement blocked":
                    var who = GameApi.MovementBlockers();
                    return string.IsNullOrEmpty(who) ? "移动被阻挡" : "移动被阻挡：" + who;
                case "引擎未就绪": return "引擎未就绪";
                case "引擎冷却中": return "引擎冷却中";
                default: return blocked;
            }
        }

        private static void Set(TMP_Text label, string text)
        {
            Set(label, text, null);
        }

        private static void Set(TMP_Text label, string text, Color? color)
        {
            if (label == null) return;
            if (label.text != text) label.text = text;
            if (color.HasValue && label.color != color.Value) label.color = color.Value;
        }

        /// <summary>
        /// Drops the window: the harvested sprites belong to the scene that is going away, so it
        /// is rebuilt (and re-skinned) on the next game start.
        /// </summary>
        public void Teardown()
        {
            if (_window != null) _window.Destroy();
            _window = null;
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
            _state = _heading = _speed = _leg = _trip = _range = _engine = _hint = null;
            _settleButton = _pauseButton = null;
            _pauseLabel = null;
            _footer = null;
            _built = false;
        }

        private void OnDestroy()
        {
            Teardown();
            if (_window != null) _window.Destroy();
            _window = null;
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
            _built = false;
        }
    }
}
