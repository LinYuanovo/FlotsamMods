using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// Shared UI surface for mods: overlay canvases, HUD buttons and toasts.
    /// Everything lives under one DontDestroyOnLoad root and is created lazily, so a
    /// mod can request UI at OnEnable time without the game being ready yet.
    ///
    /// Art comes from <see cref="NativeSkin"/> (the game's own sprites and TMP font) with a
    /// procedural fallback, and HUD buttons stack automatically so two mods anchoring to the
    /// same corner do not overlap.
    /// </summary>
    public sealed class UiService : IUiService
    {
        private const float ButtonWidth = 132f;
        private const float ButtonHeight = 38f;
        private const float ButtonGap = 6f;

        private readonly ModKitLog _log;
        private GameObject _root;
        private GameObject _overlayCanvas;
        private GameObject _hudCanvas;
        private GameObject _toastCanvas;
        private Transform _toastHost;
        private readonly Dictionary<string, GameObject> _overlays = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HudButton> _buttons = new List<HudButton>();
        private readonly Dictionary<HudAnchor, int> _slots = new Dictionary<HudAnchor, int>();
        private bool _skinLogged;
        private bool _buttonsReskinned;

        public UiService(ModKitLog log)
        {
            _log = log;
        }

        public bool ManagerWindowVisible { get; set; }

        private void EnsureRoot()
        {
            if (_root != null) return;
            try
            {
                _root = new GameObject("[FlotsamModKit]");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                _overlayCanvas = GameUi.CreateOverlay("ModOverlay", 30000);
                _overlayCanvas.transform.SetParent(_root.transform, false);

                _hudCanvas = GameUi.CreateOverlay("ModHud", 29900);
                _hudCanvas.transform.SetParent(_root.transform, false);

                _toastCanvas = GameUi.CreateOverlay("ModToast", 31000);
                _toastCanvas.transform.SetParent(_root.transform, false);

                var host = GameUi.NewUi("ToastHost", _toastCanvas.transform,
                                        typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                var rt = GameUi.Rect(host);
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 170f);
                rt.sizeDelta = new Vector2(620f, 0f);
                var layout = host.GetComponent<VerticalLayoutGroup>();
                layout.childForceExpandHeight = false;
                layout.childForceExpandWidth = true;
                layout.childControlHeight = false;
                layout.childControlWidth = true;
                layout.spacing = 4f;
                layout.childAlignment = TextAnchor.LowerCenter;
                host.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                _toastHost = host.transform;
            }
            catch (Exception e)
            {
                _log?.Error($"UI root creation failed: {e}");
            }
        }

        /// <summary>Harvests native art the first time the game is actually up.</summary>
        private bool EnsureSkin()
        {
            if (_skinLogged) return false;
            if (!NativeSkin.Harvest()) return false;
            _skinLogged = true;
            _log?.Write("modkit", "INFO", NativeSkin.Report);
            return true;
        }

        /// <summary>The scene owns the harvested sprites, so they must be dropped with it.</summary>
        public void ResetSkin()
        {
            _skinLogged = false;
            _buttonsReskinned = false;
            NativeSkin.Reset();
        }

        public GameObject CreateOverlay(string id, int sortOrder = 30000)
        {
            EnsureRoot();
            if (id != null && _overlays.TryGetValue(id, out var existing) && existing != null)
                return existing;

            var canvas = GameUi.CreateOverlay("Overlay_" + id, sortOrder);
            canvas.transform.SetParent(_root != null ? _root.transform : null, false);
            if (id != null) _overlays[id] = canvas;
            return canvas;
        }

        public IHudButton AddHudButton(string id, string label, Action onClick,
                                       HudAnchor anchor = HudAnchor.RightMiddle)
        {
            EnsureRoot();
            EnsureSkin();
            var button = new HudButton(this, _hudCanvas.transform, id, label, onClick, anchor, NextSlot(anchor));
            _buttons.Add(button);
            if (_buttonsReskinned) button.Reskin();   // created after the skin was up
            return button;
        }

        public IHudButton AddHudButton(string id, string label, Action onClick, HudAnchor anchor,
                                       IConfigService positionStore, string positionKey)
        {
            var button = (HudButton)AddHudButton(id, label, onClick, anchor);
            button.AttachPersistence(positionStore, positionKey);
            return button;
        }

        private int NextSlot(HudAnchor anchor)
        {
            _slots.TryGetValue(anchor, out var index);
            _slots[anchor] = index + 1;
            return index;
        }

        public void Toast(string message, ToastKind kind = ToastKind.Info)
        {
            EnsureRoot();
            EnsureSkin();
            if (_toastHost == null || string.IsNullOrEmpty(message)) return;
            try
            {
                var color = kind == ToastKind.Error ? new Color(0.80f, 0.22f, 0.18f)
                          : kind == ToastKind.Warning ? new Color(0.72f, 0.45f, 0.10f)
                          : kind == ToastKind.Success ? new Color(0.16f, 0.50f, 0.38f)
                          : GameUi.TextColor;

                var row = GameUi.NewUi("Toast", _toastHost);
                var img = row.AddComponent<Image>();
                if (GameUi.IsSliced(NativeSkin.PanelSprite))
                {
                    img.sprite = NativeSkin.PanelSprite;
                    img.type = Image.Type.Sliced;
                    img.color = new Color(NativeSkin.PanelTint.r, NativeSkin.PanelTint.g,
                                          NativeSkin.PanelTint.b, 0.97f);
                }
                else img.color = new Color(0.98f, 0.99f, 0.98f, 0.95f);
                GameUi.AddBorder(row.transform);

                var le = row.AddComponent<LayoutElement>();
                le.minHeight = 32f;
                le.preferredHeight = 32f;

                var text = GameUi.Label(row.transform, message, 15, color, TextAnchor.MiddleLeft);
                GameUi.Stretch(GameUi.Rect(text.gameObject), 12f, 4f, 30f, 4f);

                // Every toast can be dismissed immediately, as requested.
                var close = GameUi.IconButton(row.transform, NativeSkin.CloseSprite, null, 18f);
                if (close.GetComponent<Image>().sprite == null)
                {
                    // No native close sprite: fall back to a small text cross.
                    var x = GameUi.Label(close.transform, "✕", 14, GameUi.DimText, TextAnchor.MiddleCenter);
                    GameUi.Stretch(GameUi.Rect(x.gameObject));
                }
                var crt = GameUi.Rect(close.gameObject);
                crt.anchorMin = new Vector2(1f, 0.5f);
                crt.anchorMax = new Vector2(1f, 0.5f);
                crt.pivot = new Vector2(1f, 0.5f);
                crt.anchoredPosition = new Vector2(-6f, 0f);
                crt.sizeDelta = new Vector2(18f, 18f);
                close.onClick.AddListener(() =>
                {
                    // Collapse immediately so the rows below move up at once.
                    var le = row.GetComponent<LayoutElement>();
                    if (le != null) { le.minHeight = 0f; le.preferredHeight = 0f; }
                    UnityEngine.Object.Destroy(row);
                });

                var item2 = row.AddComponent<ToastItem>();
                item2.Life = kind == ToastKind.Error ? 6f : 4f;
            }
            catch (Exception e)
            {
                _log?.Warn($"toast failed: {e.Message}");
            }
        }

        /// <summary>Called once per frame by the runtime.</summary>
        public void Tick()
        {
            bool playing = GameApi.IsPlaying;

            // Buttons created before a save was loaded had to fall back to procedural art;
            // give them the native chrome as soon as it exists. The reskin pass is driven by
            // its own flag, NOT by EnsureSkin()'s one-shot latch: an early harvest from any
            // other call site (a mod toasting inside OnGameStart, which the host delivers
            // before Ui.Tick) would otherwise latch EnsureSkin and swallow the pass, leaving
            // every button on square procedural art for the whole session.
            if (playing) EnsureSkin();
            if (playing && !_buttonsReskinned && NativeSkin.Available)
            {
                _buttonsReskinned = true;
                foreach (var b in _buttons) b.Reskin();
            }

            for (int i = _buttons.Count - 1; i >= 0; i--)
            {
                var b = _buttons[i];
                if (b.IsDestroyed) { _buttons.RemoveAt(i); continue; }
                b.ApplyVisibility(playing);
            }
        }

        public void DestroyAll()
        {
            _overlays.Clear();
            _buttons.Clear();
            _slots.Clear();
            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); } catch { }
                _root = null;
            }
        }

        private sealed class HudButton : IHudButton
        {
            private readonly GameObject _go;
            private readonly Button _button;
            private readonly RectTransform _rt;
            private readonly TMP_Text _label;
            private readonly Image _icon;
            private readonly Vector2 _defaultPosition;
            private readonly UiService _owner;
            private IConfigService _store;
            private string _storeKey;
            private bool _visible = true;
            private bool _draggable;

            public HudButton(UiService owner, Transform parent, string id, string label, Action onClick,
                             HudAnchor anchor, int slot)
            {
                _owner = owner;
                Id = id;

                var button = GameUi.TextButton(parent, label, onClick, 15);
                _button = button;
                _go = button.gameObject;
                _go.name = "HudButton_" + id;
                _label = button.GetComponentInChildren<TMP_Text>();

                _icon = GameUi.Icon(_go.transform, null, 20f);
                var irt = GameUi.Rect(_icon.gameObject);
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.pivot = new Vector2(0f, 0.5f);
                irt.anchoredPosition = new Vector2(7f, 0f);
                _icon.gameObject.SetActive(false);

                _rt = GameUi.Rect(_go);
                float step = (ButtonHeight + ButtonGap) * slot;
                switch (anchor)
                {
                    case HudAnchor.RightTop:
                        GameUi.Anchor(_rt, new Vector2(1f, 1f), new Vector2(1f, 1f),
                                      new Vector2(-12f, -140f - step), new Vector2(ButtonWidth, ButtonHeight));
                        break;
                    case HudAnchor.LeftMiddle:
                        GameUi.Anchor(_rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                                      new Vector2(12f, -step), new Vector2(ButtonWidth, ButtonHeight));
                        break;
                    case HudAnchor.LeftTop:
                        GameUi.Anchor(_rt, new Vector2(0f, 1f), new Vector2(0f, 1f),
                                      new Vector2(12f, -140f - step), new Vector2(ButtonWidth, ButtonHeight));
                        break;
                    case HudAnchor.BottomRight:
                        GameUi.Anchor(_rt, new Vector2(1f, 0f), new Vector2(1f, 0f),
                                      new Vector2(-12f - step, 12f), new Vector2(ButtonWidth, ButtonHeight));
                        break;
                    default: // RightMiddle
                        GameUi.Anchor(_rt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                                      new Vector2(-12f, -step), new Vector2(ButtonWidth, ButtonHeight));
                        break;
                }
                _defaultPosition = _rt.anchoredPosition;
            }

            public string Id { get; }
            public bool IsDestroyed => _go == null;

            public bool Visible
            {
                get => _visible;
                set => _visible = value;
            }

            public event Action<Vector2> Moved;

            public Vector2 Position
            {
                get => _rt != null ? _rt.anchoredPosition : Vector2.zero;
                set
                {
                    if (_rt == null) return;
                    _rt.anchoredPosition = value;
                    UiDragHandler.ClampIntoCanvas(_rt);
                }
            }

            public bool Draggable
            {
                get => _draggable;
                set
                {
                    if (_draggable == value) return;
                    _draggable = value;
                    if (_go == null) return;

                    if (value)
                    {
                        var drag = _go.GetComponent<UiDragHandler>();
                        if (drag == null) drag = _go.AddComponent<UiDragHandler>();
                        drag.Target = _rt;
                        drag.ClampToCanvas = true;
                        drag.DoubleClick = ResetPosition;
                        drag.Moved = p =>
                        {
                            Save(p);
                            try { if (Moved != null) Moved(p); } catch { }
                        };
                    }
                    else
                    {
                        var drag = _go.GetComponent<UiDragHandler>();
                        if (drag != null) UnityEngine.Object.Destroy(drag);
                    }
                }
            }

            /// <summary>Turns on dragging plus automatic position memory.</summary>
            public void AttachPersistence(IConfigService store, string key)
            {
                _store = store;
                _storeKey = string.IsNullOrEmpty(key) ? "button" : key;
                Draggable = true;
                Restore();
            }

            private void Restore()
            {
                if (_store == null || _rt == null) return;
                float x = _store.Get(_storeKey + ".x", float.NaN);
                float y = _store.Get(_storeKey + ".y", float.NaN);
                _rt.anchoredPosition = (!float.IsNaN(x) && !float.IsNaN(y))
                    ? new Vector2(x, y)
                    : _defaultPosition;
                UiDragHandler.ClampIntoCanvas(_rt);
            }

            private void Save(Vector2 pos)
            {
                if (_store == null || string.IsNullOrEmpty(_storeKey)) return;
                _store.Set(_storeKey + ".x", pos.x);
                _store.Set(_storeKey + ".y", pos.y);
                _store.Save();
            }

            public void ResetPosition()
            {
                if (_rt == null) return;
                _rt.anchoredPosition = _defaultPosition;
                Save(_defaultPosition);
                try { _owner?.Toast("按钮位置已复位", ToastKind.Info); } catch { }
            }

            /// <summary>Re-applies the harvested chrome to a button built before a save existed.</summary>
            public void Reskin()
            {
                if (_go == null) return;
                try
                {
                    GameUi.NativeButtonStyle(_button);
                    GameUi.ApplyFont(_label);
                }
                catch { }
            }

            public void SetLabel(string text)
            {
                if (_label != null && _label.text != text) _label.text = text;
            }

            public void SetIcon(Sprite icon)
            {
                if (_icon == null) return;
                _icon.sprite = icon;
                _icon.gameObject.SetActive(icon != null);
                if (_label != null)
                {
                    var lrt = GameUi.Rect(_label.gameObject);
                    if (lrt != null) GameUi.Stretch(lrt, icon != null ? 30f : 6f, 2f, 6f, 2f);
                }
            }

            public void ApplyVisibility(bool gameRunning)
            {
                if (_go == null) return;
                bool want = _visible && gameRunning;
                if (_go.activeSelf != want) _go.SetActive(want);
            }

            public void Destroy()
            {
                if (_go != null) UnityEngine.Object.Destroy(_go);
            }
        }
    }

    /// <summary>Self-destructing toast row with a short fade; the ✕ button shortens its life.</summary>
    public sealed class ToastItem : MonoBehaviour
    {
        public float Life = 4f;

        private float _age;
        private CanvasGroup _group;

        private void Awake()
        {
            _group = gameObject.AddComponent<CanvasGroup>();
        }

        /// <summary>Dismiss now: collapse the fade so the row goes within a couple of frames.</summary>
        public void FadeOut()
        {
            _age = Mathf.Max(_age, Life - 0.15f);
        }

        private void Update()
        {
            _age += Time.unscaledDeltaTime;
            if (_group != null && _age > Life - 0.8f)
                _group.alpha = Mathf.Clamp01((Life - _age) / 0.8f);
            if (_age >= Life) Destroy(gameObject);
        }
    }
}
