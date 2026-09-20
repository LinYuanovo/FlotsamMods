using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Runtime UGUI construction for mod-owned UI, styled after the game's own light theme.
    ///
    /// Art comes from <see cref="NativeSkin"/>: the white 9-sliced rounded panel plus its dark
    /// plum border overlay, the mint title-bar cap, white buttons with dark borders and a
    /// pressed sprite, and the red icon-only close button. Text uses the game's own CJK font
    /// assets (a body weight and a heavy weight), bold by default because the shipped UI sets a
    /// heavy weight on essentially all of its text. Every asset is optional — when the harvest
    /// finds nothing the same calls fall back to flat colours in the same palette and to a
    /// CJK-capable dynamic OS font.
    /// </summary>
    public static class GameUi
    {
        private static TMP_FontAsset _font;
        private static TMP_FontAsset _osFont;
        private static bool _fontResolved;

        // ------------------------------------------------------------ light-theme fallbacks

        public static readonly Color PanelBg = new Color(1f, 1f, 1f, 1f);
        public static readonly Color PanelBorder = new Color(0.14f, 0.12f, 0.19f, 0.95f);
        public static readonly Color HeaderBg = new Color(0.55f, 0.88f, 0.75f, 1f);
        // Opaque grey-green blocks, like the game's tooltip rows: alpha tints vanished against
        // the white body and read as "low contrast".
        public static readonly Color RowBg = new Color(0.86f, 0.90f, 0.88f, 1f);
        public static readonly Color RowBgAlt = new Color(0.93f, 0.96f, 0.95f, 1f);
        public static readonly Color ButtonBg = new Color(1f, 1f, 1f, 1f);
        public static readonly Color SunkenBg = new Color(0.90f, 0.94f, 0.92f, 1f);

        public static Color Accent => NativeSkin.Accent;
        public static Color TextColor => NativeSkin.Text;
        public static Color DimText => NativeSkin.DimText;
        public static Color Danger => NativeSkin.Danger;
        public static Color Good => NativeSkin.Good;

        public static Color PanelTint => NativeSkin.PanelSprite != null ? NativeSkin.PanelTint : PanelBg;
        public static Color HeaderTint => NativeSkin.HeaderSprite != null ? NativeSkin.HeaderTint : HeaderBg;
        public static Color ButtonTint => NativeSkin.ButtonSprite != null ? NativeSkin.ButtonTint : ButtonBg;

        // ------------------------------------------------------------ font

        /// <summary>
        /// Drops the cached fonts. Called when the native skin is (re)harvested, because a font
        /// resolved before a save existed can only ever be the OS fallback.
        /// </summary>
        public static void ResetFont()
        {
            _fontResolved = false;
            _font = null;
        }

        public static TMP_FontAsset UiFont
        {
            get
            {
                if (_fontResolved) return _font;
                _fontResolved = true;
                try { _font = ResolveFont(); }
                catch { _font = null; }
                return _font;
            }
        }

        private static TMP_FontAsset ResolveFont()
        {
            NativeSkin.Harvest();

            var os = OsFont();
            var native = NativeSkin.Font;

            if (native != null)
            {
                // A static atlas only contains the glyphs the game ships; append a dynamic
                // OS face so our own Chinese strings still render.
                if (os != null && native.atlasPopulationMode == AtlasPopulationMode.Static)
                    AddFallback(native, os);
                return native;
            }

            try
            {
                var def = TMP_Settings.defaultFontAsset;
                if (def != null)
                {
                    if (os != null && def.atlasPopulationMode == AtlasPopulationMode.Static)
                        AddFallback(def, os);
                    return def;
                }
            }
            catch { }

            return os;
        }

        private static void AddFallback(TMP_FontAsset target, TMP_FontAsset fallback)
        {
            try
            {
                if (target == null || fallback == null || target == fallback) return;
                if (target.fallbackFontAssetTable == null)
                    target.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!target.fallbackFontAssetTable.Contains(fallback))
                    target.fallbackFontAssetTable.Add(fallback);
            }
            catch { }
        }

        /// <summary>A dynamic CJK-capable font asset built from an installed OS face.</summary>
        public static TMP_FontAsset OsFont()
        {
            if (_osFont != null) return _osFont;
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans SC", "Arial" };
            foreach (var name in candidates)
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 32);
                    if (f == null) continue;
                    _osFont = TMP_FontAsset.CreateFontAsset(f);
                    if (_osFont != null) break;
                }
                catch { }
            }
            return _osFont;
        }

        // ------------------------------------------------------------ primitives

        public static RectTransform Rect(GameObject go) => go != null ? go.GetComponent<RectTransform>() : null;

        /// <summary>True when a sprite can safely cover an arbitrarily sized surface.</summary>
        public static bool IsSliced(Sprite sprite) => sprite != null && sprite.border.sqrMagnitude > 0f;

        public static GameObject NewUi(string name, Transform parent, params Type[] components)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            if (components != null)
                foreach (var c in components) { try { go.AddComponent(c); } catch { } }
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        public static void Stretch(RectTransform rt, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        public static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pivot,
                                  Vector2 anchoredPos, Vector2 size)
        {
            if (rt == null) return;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }

        public static GameObject CreateOverlay(string name, int sortOrder)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas),
                                    typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(ModUiMarker));
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            return go;
        }

        /// <summary>Reference resolution of the canvas a transform belongs to (1920x1080 default).</summary>
        public static Vector2 CanvasSize(Transform t)
        {
            try
            {
                var canvas = t != null ? t.GetComponentInParent<Canvas>() : null;
                if (canvas != null)
                {
                    var scaler = canvas.GetComponent<CanvasScaler>();
                    if (scaler != null && scaler.referenceResolution.sqrMagnitude > 1f)
                        return scaler.referenceResolution;
                    var rt = canvas.transform as RectTransform;
                    if (rt != null && rt.rect.size.sqrMagnitude > 1f) return rt.rect.size;
                }
            }
            catch { }
            return new Vector2(1920f, 1080f);
        }

        /// <summary>A surface using the given sprite when it can cover the area, else a flat tint.</summary>
        public static Image SpriteImage(GameObject go, Sprite native, Color fallbackColor,
                                        Color? nativeTint = null)
        {
            var img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            if (native != null)
            {
                img.sprite = native;
                if (IsSliced(native)) img.type = Image.Type.Sliced;
                img.color = nativeTint ?? Color.white;
            }
            else
            {
                img.sprite = null;
                img.color = fallbackColor;
            }
            return img;
        }

        /// <summary>
        /// A panel surface: the game's white rounded body plus its dark border overlay, so the
        /// outline stays crisp at any size exactly like the vanilla panels.
        /// </summary>
        public static GameObject Panel(Transform parent, string name, Color color, bool raycastTarget = true)
        {
            var go = NewUi(name, parent);
            var img = SpriteImage(go, NativeSkin.PanelSprite, color,
                                  NativeSkin.PanelSprite != null ? NativeSkin.PanelTint : (Color?)null);
            img.raycastTarget = raycastTarget;
            AddBorder(go.transform);
            return go;
        }

        /// <summary>The dark rounded outline the game draws on top of every panel.</summary>
        public static Image AddBorder(Transform parent, float inset = 0f)
        {
            var go = NewUi("Border", parent);
            var img = SpriteImage(go, NativeSkin.PanelBorderSprite, PanelBorder, Color.white);
            img.raycastTarget = false;
            Stretch(Rect(go), inset, inset, inset, inset);
            return img;
        }

        /// <summary>A flat, sprite-less surface (rows, dimmers, icon backings).</summary>
        public static GameObject Flat(Transform parent, string name, Color color, bool raycastTarget = false)
        {
            var go = NewUi(name, parent, typeof(Image));
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = raycastTarget;
            return go;
        }

        public static TextAlignmentOptions Align(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.MidlineLeft;
                case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.MidlineRight;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.MidlineLeft;
            }
        }

        /// <summary>
        /// Labels are bold by default: the shipped UI sets a heavy weight on essentially all of
        /// its text, and a regular weight next to it reads as "wrong font".
        /// </summary>
        public static TMP_Text Label(Transform parent, string text, int size = 16, Color? color = null,
                                     TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false,
                                     bool bold = true)
        {
            var go = NewUi("Label", parent);
            var t = go.AddComponent<TextMeshProUGUI>();
            ApplyFont(t);
            t.fontSize = size;
            t.text = text ?? "";
            t.color = color ?? TextColor;
            t.alignment = Align(anchor);
            t.raycastTarget = false;
            t.enableWordWrapping = wrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = false;
            t.margin = new Vector4(0f, 0f, 0f, 0f);

            if (bold)
            {
                // Use the game's own heavy face when it shipped one; synthesize the weight only
                // when it did not.
                var heavy = NativeSkin.FontBold;
                if (heavy != null && heavy != t.font) { t.font = heavy; t.fontStyle = FontStyles.Normal; }
                else t.fontStyle = FontStyles.Bold;
            }
            else
            {
                t.fontStyle = FontStyles.Normal;
            }
            return t;
        }

        public static void ApplyFont(TMP_Text t)
        {
            if (t == null) return;
            var font = UiFont;
            try
            {
                if (font != null)
                {
                    t.font = font;
                    var mat = NativeSkin.FontMaterial != null && NativeSkin.Font == font
                        ? NativeSkin.FontMaterial
                        : font.material;
                    if (mat != null) t.fontSharedMaterial = mat;
                }
            }
            catch { }
        }

        /// <summary>Applies the harvested native button chrome to any Button.</summary>
        public static void NativeButtonStyle(Button btn, Color? tint = null)
        {
            if (btn == null) return;
            var img = btn.targetGraphic as Image;
            if (img == null) return;

            if (NativeSkin.ButtonSprite != null)
            {
                img.sprite = NativeSkin.ButtonSprite;
                if (IsSliced(NativeSkin.ButtonSprite)) img.type = Image.Type.Sliced;
                img.color = tint ?? NativeSkin.ButtonTint;

                try
                {
                    if (NativeSkin.ButtonPressedSprite != null)
                    {
                        var state = btn.spriteState;
                        state.pressedSprite = NativeSkin.ButtonPressedSprite;
                        state.selectedSprite = null;
                        state.disabledSprite = null;
                        btn.spriteState = state;
                        btn.transition = Selectable.Transition.SpriteSwap;
                    }
                    else
                    {
                        var colors = btn.colors;
                        colors.highlightedColor = new Color(0.86f, 0.96f, 0.91f, 1f);
                        colors.pressedColor = new Color(0.72f, 0.88f, 0.80f, 1f);
                        colors.selectedColor = Color.white;
                        btn.colors = colors;
                        btn.transition = Selectable.Transition.ColorTint;
                    }
                }
                catch { }
            }
            else
            {
                img.color = tint ?? ButtonBg;
                var colors = btn.colors;
                colors.highlightedColor = new Color(0.86f, 0.96f, 0.91f, 1f);
                colors.pressedColor = new Color(0.72f, 0.88f, 0.80f, 1f);
                btn.colors = colors;
            }
        }

        /// <summary>The dark outline overlay the game stacks on top of a button background.</summary>
        private static void AddButtonBorder(Transform buttonRoot)
        {
            if (NativeSkin.ButtonBorderSprite == null) return;
            var go = NewUi("Border", buttonRoot);
            var img = SpriteImage(go, NativeSkin.ButtonBorderSprite, PanelBorder, Color.white);
            img.raycastTarget = false;
            Stretch(Rect(go));
        }

        public static Button TextButton(Transform parent, string label, Action onClick,
                                        int size = 15, Color? bg = null)
        {
            var go = NewUi("Button", parent);
            var img = go.AddComponent<Image>();
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            NativeButtonStyle(btn, bg);
            AddButtonBorder(go.transform);

            if (onClick != null)
                btn.onClick.AddListener(() =>
                {
                    // A drag that ends on the button must not fire a click.
                    if (UiDragHandler.JustDragged) return;
                    onClick();
                });

            var text = Label(go.transform, label, size, TextColor, TextAnchor.MiddleCenter);
            text.raycastTarget = false;
            Stretch(Rect(text.gameObject), 6f, 2f, 6f, 2f);
            return btn;
        }

        /// <summary>
        /// An icon-only button, like the game's close/locate/plus controls: no background, just
        /// the sprite, with a slight tint on hover.
        /// </summary>
        public static Button IconButton(Transform parent, Sprite icon, Action onClick, float size = 26f,
                                        Color? tint = null)
        {
            var go = NewUi("IconButton", parent);
            var img = go.AddComponent<Image>();
            img.sprite = icon;
            img.preserveAspect = true;
            img.color = tint ?? Color.white;
            img.raycastTarget = true;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.80f, 0.93f, 0.87f, 1f);
            colors.pressedColor = new Color(0.62f, 0.82f, 0.72f, 1f);
            colors.selectedColor = Color.white;
            btn.colors = colors;
            btn.transition = Selectable.Transition.ColorTint;

            if (onClick != null)
                btn.onClick.AddListener(() =>
                {
                    if (UiDragHandler.JustDragged) return;
                    onClick();
                });

            Rect(go).sizeDelta = new Vector2(size, size);
            return btn;
        }

        public static Image Icon(Transform parent, Sprite sprite, float size = 20f)
        {
            var go = NewUi("Icon", parent, typeof(Image));
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
            Rect(go).sizeDelta = new Vector2(size, size);
            return img;
        }

        public static TMP_InputField SearchInput(Transform parent, string placeholder, Action<string> onChanged)
        {
            var root = NewUi("SearchBox", parent);
            var bg = root.AddComponent<Image>();
            if (IsSliced(NativeSkin.SlotSprite))
            {
                bg.sprite = NativeSkin.SlotSprite;
                bg.type = Image.Type.Sliced;
                bg.color = new Color(1f, 1f, 1f, 0.9f);
            }
            else bg.color = new Color(1f, 1f, 1f, 0.85f);
            AddBorder(root.transform);

            var area = NewUi("Text Area", root.transform, typeof(RectMask2D));
            Stretch(Rect(area), 8f, 3f, 8f, 3f);

            var ph = Label(area.transform, placeholder, 15, DimText, TextAnchor.MiddleLeft, bold: false);
            Stretch(Rect(ph.gameObject));
            var txt = Label(area.transform, "", 15, TextColor, TextAnchor.MiddleLeft);
            Stretch(Rect(txt.gameObject));

            var input = root.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.textViewport = Rect(area);
            input.textComponent = txt;
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 40;
            input.fontAsset = UiFont;
            if (onChanged != null) input.onValueChanged.AddListener(v => onChanged(v));
            return input;
        }

        /// <summary>Vertical scrolling list. Returns the ScrollRect; rows go under "content".</summary>
        public static ScrollRect ScrollList(Transform parent, string name, out RectTransform content)
        {
            var root = NewUi(name, parent);
            var bg = root.AddComponent<Image>();
            if (IsSliced(NativeSkin.SlotSprite))
            {
                bg.sprite = NativeSkin.SlotSprite;
                bg.type = Image.Type.Sliced;
                bg.color = new Color(1f, 1f, 1f, 0.55f);
            }
            else bg.color = SunkenBg;
            AddBorder(root.transform);
            root.AddComponent<RectMask2D>();

            var viewport = NewUi("Viewport", root.transform);
            Stretch(Rect(viewport));

            var contentGo = NewUi("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content = Rect(contentGo);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.spacing = 2f;
            layout.padding = new RectOffset(4, 4, 4, 4);

            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = root.AddComponent<ScrollRect>();
            scroll.viewport = Rect(viewport);
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 35f;
            return scroll;
        }

        public static GameObject Row(Transform content, float height = 26f, Color? bg = null)
        {
            var go = NewUi("Row", content, typeof(LayoutElement));
            var img = go.AddComponent<Image>();
            if (IsSliced(NativeSkin.RowSprite))
            {
                img.sprite = NativeSkin.RowSprite;
                img.type = Image.Type.Sliced;
                img.color = Color.white;
            }
            else img.color = bg ?? RowBg;
            img.raycastTarget = true;
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            return go;
        }

        /// <summary>A labelled on/off chip.</summary>
        public static Button Chip(Transform parent, string label, bool on, Action onClick, int size = 13)
        {
            var btn = TextButton(parent, label, onClick, size,
                                 on ? new Color(Accent.r, Accent.g, Accent.b, 0.35f) : (Color?)null);
            var txt = btn.GetComponentInChildren<TMP_Text>();
            if (txt != null) txt.color = on ? TextColor : DimText;
            return btn;
        }

        // ------------------------------------------------------------ slider

        /// <summary>
        /// A "label [----o----] value" row built from native art: a sunken slot-sprite track, a
        /// flat accent fill and a native handle. Click or drag anywhere on the track to set the
        /// value. <paramref name="onChanged"/> fires live while dragging (apply the effect there);
        /// <paramref name="onCommit"/> fires once on release (persist / log there), so a slider can
        /// feel immediate without thrashing the config file or flooding the log. The row stretches
        /// to its parent's width; the caller positions it. The readout is formatted by
        /// <paramref name="format"/> (default: whole percent).
        /// </summary>
        public static UiSliderRow SliderRow(Transform parent, string label, float min, float max, float value,
                                            Action<float> onChanged, Action<float> onCommit = null,
                                            float labelWidth = 176f, float valueWidth = 64f,
                                            float trackHeight = 16f, int size = 14,
                                            Func<float, string> format = null)
        {
            var row = new UiSliderRow { Min = min, Max = max };
            row.Root = NewUi("SliderRow", parent);
            row.Rect = Rect(row.Root);

            row.Label = Label(row.Root.transform, label, size, TextColor, TextAnchor.MiddleLeft);
            Anchor(Rect(row.Label.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                   new Vector2(labelWidth * 0.5f, 0f), new Vector2(labelWidth, trackHeight + 8f));

            row.ValueText = Label(row.Root.transform, "", size, Accent, TextAnchor.MiddleRight);
            Anchor(Rect(row.ValueText.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                   new Vector2(-valueWidth * 0.5f, 0f), new Vector2(valueWidth, trackHeight + 8f));

            // Track stretches between the label and the value readout.
            var trackGo = NewUi("Track", row.Root.transform, typeof(Image));
            SpriteImage(trackGo, NativeSkin.SlotSprite, SunkenBg,
                        NativeSkin.SlotSprite != null ? new Color(1f, 1f, 1f, 0.92f) : (Color?)null);
            var trackImg = trackGo.GetComponent<Image>();
            trackImg.raycastTarget = true;
            var trt = Rect(trackGo);
            trt.anchorMin = new Vector2(0f, 0.5f);
            trt.anchorMax = new Vector2(1f, 0.5f);
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.offsetMin = new Vector2(labelWidth + 8f, -trackHeight * 0.5f);
            trt.offsetMax = new Vector2(-(valueWidth + 8f), trackHeight * 0.5f);

            // Fill (accent), anchored to the track's left edge; the slider drives its width.
            var fillGo = NewUi("Fill", trackGo.transform, typeof(Image));
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = Accent;
            fillImg.raycastTarget = false;
            var frt = Rect(fillGo);
            frt.anchorMin = new Vector2(0f, 0f);
            frt.anchorMax = new Vector2(0f, 1f);
            frt.pivot = new Vector2(0f, 0.5f);
            frt.anchoredPosition = Vector2.zero;
            frt.sizeDelta = new Vector2(0f, -4f);

            // Handle (native button/drag sprite), centred on the value.
            var handleGo = NewUi("Handle", trackGo.transform, typeof(Image));
            var handleImg = handleGo.GetComponent<Image>();
            var handleSprite = NativeSkin.ButtonSprite != null ? NativeSkin.ButtonSprite : NativeSkin.DragSprite;
            handleImg.sprite = handleSprite;
            if (IsSliced(handleSprite)) handleImg.type = Image.Type.Sliced;
            handleImg.color = Color.white;
            handleImg.raycastTarget = false;
            var hrt = Rect(handleGo);
            hrt.anchorMin = new Vector2(0f, 0.5f);
            hrt.anchorMax = new Vector2(0f, 0.5f);
            hrt.pivot = new Vector2(0.5f, 0.5f);
            float handleW = trackHeight + 6f;
            hrt.sizeDelta = new Vector2(handleW, trackHeight + 6f);

            var drag = trackGo.AddComponent<SliderDrag>();
            drag.Track = trt;
            drag.Fill = frt;
            drag.Handle = hrt;
            drag.HandleWidth = handleW;
            drag.Min = min;
            drag.Max = max;
            drag.ValueText = row.ValueText;
            drag.OnChanged = onChanged;
            drag.OnCommit = onCommit;
            drag.Format = format;
            row.Drag = drag;

            drag.SetValue(Mathf.Clamp(value, min, max), false);
            return row;
        }

        // ------------------------------------------------------------ windows

        /// <summary>
        /// Builds a native-looking window: white rounded panel with dark border, mint title bar
        /// carrying the title, a drag grip, a tips toggle and a red close button, plus a corner
        /// grip that resizes it. Position AND size are remembered; double-click the title bar to
        /// reset both.
        /// </summary>
        public static UiWindow Window(Transform parent, string title, Vector2 size, Vector2 defaultPosition,
                                      IConfigService config, string configKey, Action onClose,
                                      float headerHeight = 30f)
        {
            var win = new UiWindow();
            win.Config = config;
            win.ConfigKey = configKey;
            win.DefaultPosition = defaultPosition;
            win.DefaultSize = size;
            win.HeaderHeight = headerHeight;

            win.Root = Panel(parent, "Window", PanelBg);
            win.Rect = Rect(win.Root);
            win.Rect.anchorMin = win.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            win.Rect.pivot = new Vector2(0.5f, 0.5f);

            // Title bar -------------------------------------------------
            var header = NewUi("Header", win.Root.transform);
            var hImg = SpriteImage(header, NativeSkin.HeaderSprite, HeaderBg,
                                   NativeSkin.HeaderSprite != null ? NativeSkin.HeaderTint : (Color?)null);
            hImg.raycastTarget = true;
            win.Header = Rect(header);
            win.Header.anchorMin = new Vector2(0f, 1f);
            win.Header.anchorMax = new Vector2(1f, 1f);
            win.Header.pivot = new Vector2(0.5f, 1f);
            win.Header.anchoredPosition = Vector2.zero;
            win.Header.sizeDelta = new Vector2(0f, headerHeight);

            if (NativeSkin.DragSprite != null)
            {
                var grip = Icon(header.transform, NativeSkin.DragSprite, headerHeight - 12f);
                grip.color = new Color(NativeSkin.Text.r, NativeSkin.Text.g, NativeSkin.Text.b, 0.55f);
                var grt = Rect(grip.gameObject);
                Anchor(grt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                       new Vector2(8f, 0f), new Vector2(headerHeight - 12f, headerHeight - 12f));
            }

            win.Title = Label(header.transform, title, 17, TextColor, TextAnchor.MiddleCenter);
            Stretch(Rect(win.Title.gameObject), 30f, 2f, 90f + headerHeight, 2f);

            // Red X keeps the far-right corner; the tips toggle sits immediately to its left.
            win.CloseButton = IconButton(header.transform,
                                         NativeSkin.CloseSprite, onClose, headerHeight - 8f,
                                         NativeSkin.CloseSprite != null ? Color.white : (Color?)Danger);
            var crt = Rect(win.CloseButton.gameObject);
            Anchor(crt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                   new Vector2(-7f, 0f), new Vector2(headerHeight - 8f, headerHeight - 8f));

            win.TipsButton = IconButton(header.transform, NativeSkin.CheckOnSprite, win.ToggleTips,
                                        headerHeight - 10f);
            var trt = Rect(win.TipsButton.gameObject);
            Anchor(trt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                   new Vector2(-7f - (headerHeight - 8f) - 4f, 0f),
                   new Vector2(headerHeight - 10f, headerHeight - 10f));

            // The topmost toggle sits left of the tips toggle: bright = the window floats above
            // the game's own panels, dim = it ducks underneath them.
            win.TopButton = IconButton(header.transform, NativeSkin.LocateSprite, win.ToggleTopMost,
                                       headerHeight - 10f);
            var prt = Rect(win.TopButton.gameObject);
            Anchor(prt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                   new Vector2(-7f - (headerHeight - 8f) - 4f - (headerHeight - 10f) - 4f, 0f),
                   new Vector2(headerHeight - 10f, headerHeight - 10f));

            // Body ------------------------------------------------------
            var body = NewUi("Body", win.Root.transform);
            win.Body = Rect(body);

            // Resize grip ------------------------------------------------
            var resize = NewUi("ResizeGrip", win.Root.transform);
            var rImg = resize.AddComponent<Image>();
            rImg.sprite = NativeSkin.DragSprite;
            rImg.preserveAspect = true;
            rImg.color = new Color(NativeSkin.Text.r, NativeSkin.Text.g, NativeSkin.Text.b, 0.5f);
            rImg.raycastTarget = true;
            var rrt = Rect(resize);
            Anchor(rrt, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-3f, 3f), new Vector2(20f, 20f));
            var rh = resize.AddComponent<UiResizeHandler>();
            rh.Window = win;

            win.RestoreSizeAndPosition();
            win.CaptureCanvasOrders();
            win.ApplyTopMost(config == null || string.IsNullOrEmpty(configKey)
                                 || config.Get(configKey + ".top", false),
                             persist: false);
            win.MakeDraggable(header, pos =>
            {
                if (config == null || string.IsNullOrEmpty(configKey)) return;
                config.Set(configKey + ".x", pos.x);
                config.Set(configKey + ".y", pos.y);
                config.Save();
            });
            win.ApplyLayout();
            return win;
        }

        /// <summary>
        /// Attaches drag + position memory to an arbitrary rect (used for HUD widgets that are
        /// not full windows). Positions are stored as "&lt;key&gt;.x" / "&lt;key&gt;.y".
        /// </summary>
        public static UiDragHandler Remember(IConfigService config, string key, RectTransform target,
                                             GameObject handle, Vector2 fallback, bool clamp = true)
        {
            if (target == null) return null;

            float x = fallback.x, y = fallback.y;
            if (config != null && !string.IsNullOrEmpty(key))
            {
                x = config.Get(key + ".x", float.NaN);
                y = config.Get(key + ".y", float.NaN);
                if (float.IsNaN(x) || float.IsNaN(y)) { x = fallback.x; y = fallback.y; }
            }
            target.anchoredPosition = new Vector2(x, y);
            if (clamp) UiDragHandler.ClampIntoCanvas(target);

            var d = (handle != null ? handle : target.gameObject).AddComponent<UiDragHandler>();
            d.Target = target;
            d.ClampToCanvas = clamp;
            d.Moved = pos =>
            {
                if (config == null || string.IsNullOrEmpty(key)) return;
                config.Set(key + ".x", pos.x);
                config.Set(key + ".y", pos.y);
                config.Save();
            };
            return d;
        }

        public static void MakeDraggable(GameObject window, GameObject handle, Action<Vector2> onMoved = null)
        {
            var d = handle.AddComponent<UiDragHandler>();
            d.Target = Rect(window);
            d.ClampToCanvas = true;
            d.Moved = onMoved;
        }

        public static void SetRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax,
                                   Vector2 pivot, Vector2 anchoredPos, Vector2 size)
        {
            if (rt == null) return;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }
    }

    /// <summary>How a window's content reacts to a resize.</summary>
    public enum WindowContentMode
    {
        /// <summary>
        /// The body is laid out once at the default size and then scaled uniformly, so text,
        /// icons and rows grow with the window exactly as the player expects.
        /// </summary>
        Uniform,

        /// <summary>
        /// The body stretches to fill and the mod re-lays-out itself via
        /// <see cref="UiWindow.Resized"/> (used by the minimap, which rebuilds its texture).
        /// </summary>
        None
    }

    /// <summary>
    /// A draggable, resizable mod window whose position and size survive restarts. The rect is
    /// anchored to the canvas CENTRE, so a saved position means the same thing at every
    /// resolution. Double-click the title bar to reset both.
    /// </summary>
    public sealed class UiWindow
    {
        public GameObject Root;
        public RectTransform Rect;
        public RectTransform Header;
        public RectTransform Body;
        public TMP_Text Title;
        public Button CloseButton;
        public Button TipsButton;
        public Button TopButton;
        public IConfigService Config;
        public string ConfigKey;
        public Vector2 DefaultPosition;
        public Vector2 DefaultSize;
        public float HeaderHeight = 30f;

        public WindowContentMode ContentMode = WindowContentMode.Uniform;

        /// <summary>
        /// Vertical space the registered tips occupy. When the tips toggle hides them the window
        /// gives that space back instead of leaving an empty band.
        /// </summary>
        public float TipsHeight;
        public Vector2 MinSize = new Vector2(240f, 160f);
        public Vector2 MaxSize = new Vector2(1700f, 1040f);

        /// <summary>Raised after the size changes (and once at build with the restored size).</summary>
        public Action<Vector2> Resized;

        /// <summary>Raised when the header tips toggle is used, so the window can reclaim space.</summary>
        public Action<bool> TipsChanged;

        private Vector2 _referenceBody;
        private readonly List<RectTransform> _tipsRoots = new List<RectTransform>();

        /// <summary>
        /// Window height with the tips shown. Stored in config, so a window restored with its
        /// tips hidden still collapses by exactly the tips' height instead of keeping a band.
        /// </summary>
        private float _canonicalH = -1f;

        public bool Visible
        {
            get => Root != null && Root.activeSelf;
            set { if (Root != null && Root.activeSelf != value) Root.SetActive(value); }
        }

        public Vector2 Size
        {
            get => Rect != null ? Rect.sizeDelta : Vector2.zero;
            set { SetSize(value); }
        }

        public void SetTitle(string text) { if (Title != null) Title.text = text ?? ""; }

        /// <summary>
        /// Registers the window's help/tip texts so the header button left of the close button
        /// can show or hide them. The choice is remembered per window.
        /// </summary>
        public void SetTips(params RectTransform[] roots)
        {
            _tipsRoots.Clear();
            if (roots != null)
                foreach (var r in roots)
                    if (r != null) _tipsRoots.Add(r);

            bool show = true;
            if (Config != null && !string.IsNullOrEmpty(ConfigKey))
                show = Config.Get(ConfigKey + ".tips", true);
            ApplyTips(show, persist: false);
        }

        public void ToggleTips()
        {
            ApplyTips(!TipsShown(), persist: true);
        }

        private bool TipsShown()
        {
            return _tipsRoots.Count == 0 || (_tipsRoots[0] != null && _tipsRoots[0].gameObject.activeSelf);
        }

        private void ApplyTips(bool show, bool persist)
        {
            foreach (var r in _tipsRoots)
                if (r != null && r.gameObject.activeSelf != show) r.gameObject.SetActive(show);

            if (TipsButton != null)
            {
                var img = TipsButton.GetComponent<Image>();
                if (img != null)
                    img.sprite = show ? NativeSkin.CheckOnSprite : NativeSkin.CheckOffSprite;
            }

            if (persist && Config != null && !string.IsNullOrEmpty(ConfigKey))
            {
                Config.Set(ConfigKey + ".tips", show);
                Config.Save();
            }

            // Reclaim the space the tips occupied so the window collapses upward. Runs on the
            // restore path too, so a window saved with hidden tips never shows an empty band.
            if (TipsHeight > 0f && Rect != null && _canonicalH > 0f)
            {
                float target = Mathf.Clamp(_canonicalH - (show ? 0f : TipsHeight), MinSize.y, MaxSize.y);
                var sz = Rect.sizeDelta;
                if (Mathf.Abs(sz.y - target) > 0.5f)
                {
                    Rect.sizeDelta = new Vector2(sz.x, target);
                    ApplyLayout();
                    try { if (Resized != null) Resized(Rect.sizeDelta); } catch { }
                }
            }

            try { if (TipsChanged != null) TipsChanged(show); } catch { }
        }

        /// <summary>
        /// Makes the whole window click-through (used while a pinned vanilla tooltip needs the
        /// pointer: our panel must not swallow the click meant for the tooltip's slots).
        /// </summary>
        public void SetClickThrough(bool clickThrough)
        {
            if (Root == null) return;
            var group = Root.GetComponent<CanvasGroup>();
            if (group == null) group = Root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = !clickThrough;
        }

        // ------------------------------------------------------------ stacking vs native UI

        /// <summary>True while the window floats above the game's own panels.</summary>
        public bool TopMost { get; private set; } = true;

        private int _topOrder = 30000;
        private int _belowOrder = -1;
        private RenderMode _nativeMode = RenderMode.ScreenSpaceOverlay;
        private Camera _nativeCam;
        private float _nativePlane = 100f;
        private int _nativeLayer;
        private int _nativeOrder;

        public void ToggleTopMost() => ApplyTopMost(!TopMost, persist: true);

        /// <summary>
        /// Records the overlay's own sorting order and works out how to duck under the game's UI.
        /// A Screen Space - Overlay canvas is drawn in the overlay pass, i.e. above everything
        /// any camera renders - including the game's own Screen Space - Camera UI canvas - no
        /// matter what sortingOrder it carries, so sorting alone can never put a mod window under
        /// a native panel. The window therefore also mirrors the native canvas's render mode and
        /// camera while ducking (modkit overlays carry <see cref="ModUiMarker"/> and are excluded
        /// from the scan); the low digits of the top order carry over into the below band so mod
        /// windows keep their relative stacking either way.
        /// </summary>
        internal void CaptureCanvasOrders()
        {
            var canvas = Root != null ? Root.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return;
            _topOrder = canvas.sortingOrder;

            Canvas native = null;
            try { var ui = GameManager.UIManager; if (ui != null) native = ui.Canvas; } catch { }
            if (native == null)
            {
                int bestOrder = int.MinValue;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isRootCanvas || !c.gameObject.activeInHierarchy) continue;
                    if (c.GetComponent<ModUiMarker>() != null) continue;
                    if (c.sortingOrder > bestOrder) { bestOrder = c.sortingOrder; native = c; }
                }
            }
            if (native != null)
            {
                _nativeMode = native.renderMode;
                _nativeCam = native.worldCamera;
                _nativePlane = native.planeDistance;
                _nativeLayer = native.gameObject.layer;
                _nativeOrder = native.sortingOrder;
            }
            _belowOrder = _nativeOrder - 1000 + Mathf.Clamp(_topOrder % 1000, 0, 999);
            UnityEngine.Debug.Log($"[modkit] window '{Root.name}' topOrder={_topOrder} nativeCanvas=" +
                                  $"'{(native != null ? native.name : "none")}' mode={_nativeMode} " +
                                  $"order={_nativeOrder} cam={(_nativeCam != null ? _nativeCam.name : "null")} " +
                                  $"belowOrder={_belowOrder}");
        }

        /// <summary>Floats the window above the game's panels (top) or ducks it under them.</summary>
        public void ApplyTopMost(bool top, bool persist)
        {
            TopMost = top;
            var canvas = Root != null ? Root.GetComponentInParent<Canvas>() : null;
            if (canvas != null)
            {
                if (top)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = _topOrder;
                }
                else
                {
                    var mode = _nativeMode == RenderMode.WorldSpace
                        ? RenderMode.ScreenSpaceCamera : _nativeMode;
                    canvas.renderMode = mode;
                    if (mode == RenderMode.ScreenSpaceCamera)
                    {
                        canvas.worldCamera = _nativeCam;
                        canvas.planeDistance = _nativePlane;
                    }
                    canvas.gameObject.layer = _nativeLayer;
                    canvas.sortingOrder = _belowOrder;
                }
            }
            if (TopButton != null)
            {
                var img = TopButton.GetComponent<Image>();
                if (img != null) img.color = top ? Color.white : new Color(0.45f, 0.50f, 0.48f, 0.9f);
            }
            if (persist && Config != null && !string.IsNullOrEmpty(ConfigKey))
            {
                Config.Set(ConfigKey + ".top", top);
                Config.Save();
            }
        }

        public void MakeDraggable(GameObject handle, Action<Vector2> onMoved)
        {
            if (Rect == null || handle == null) return;
            var d = handle.AddComponent<UiDragHandler>();
            d.Target = Rect;
            d.ClampToCanvas = true;
            d.DoubleClick = ResetPosition;
            d.Moved = pos =>
            {
                Save(pos);
                if (onMoved != null) onMoved(pos);
            };
        }

        // ------------------------------------------------------------ size

        /// <summary>The body area available at a given window size.</summary>
        private Vector2 BodyArea(Vector2 windowSize)
        {
            return new Vector2(Mathf.Max(32f, windowSize.x - 16f),
                               Mathf.Max(32f, windowSize.y - HeaderHeight - 12f));
        }

        public void SetSize(Vector2 size)
        {
            if (Rect == null) return;
            size.x = Mathf.Clamp(size.x, MinSize.x, MaxSize.x);
            size.y = Mathf.Clamp(size.y, MinSize.y, MaxSize.y);
            if (Rect.sizeDelta == size) return;

            Rect.sizeDelta = size;
            // Persist the height as if the tips were shown, so restoring never double-counts.
            _canonicalH = size.y + (TipsShown() ? 0f : TipsHeight);
            SaveSize(size);
            ApplyLayout();
            try { if (Resized != null) Resized(size); } catch { }
        }

        /// <summary>Positions the body for the current size, honouring the content mode.</summary>
        public void ApplyLayout()
        {
            if (Rect == null || Body == null) return;
            var size = Rect.sizeDelta;
            var area = BodyArea(size);

            if (ContentMode == WindowContentMode.Uniform)
            {
                if (_referenceBody.x <= 1f || _referenceBody.y <= 1f)
                    _referenceBody = BodyArea(DefaultSize);

                float scale = Mathf.Min(area.x / _referenceBody.x, area.y / _referenceBody.y);
                scale = Mathf.Clamp(scale, 0.4f, 3f);

                Body.anchorMin = new Vector2(0f, 1f);
                Body.anchorMax = new Vector2(0f, 1f);
                Body.pivot = new Vector2(0f, 1f);
                Body.anchoredPosition = new Vector2(8f, -(HeaderHeight + 4f));
                Body.sizeDelta = _referenceBody;
                Body.localScale = new Vector3(scale, scale, 1f);
            }
            else
            {
                Body.localScale = Vector3.one;
                GameUi.Stretch(Body, 8f, HeaderHeight + 4f, 8f, 8f);
            }
        }

        private void SaveSize(Vector2 size)
        {
            if (Config == null || string.IsNullOrEmpty(ConfigKey)) return;
            Config.Set(ConfigKey + ".w", size.x);
            Config.Set(ConfigKey + ".h", _canonicalH > 0f ? _canonicalH : size.y);
            Config.Save();
        }

        public void RestoreSizeAndPosition()
        {
            if (Rect == null) return;

            var size = DefaultSize;
            if (Config != null && !string.IsNullOrEmpty(ConfigKey))
            {
                float w = Config.Get(ConfigKey + ".w", float.NaN);
                float h = Config.Get(ConfigKey + ".h", float.NaN);
                if (!float.IsNaN(w) && !float.IsNaN(h))
                    size = new Vector2(Mathf.Clamp(w, MinSize.x, MaxSize.x), Mathf.Clamp(h, MinSize.y, MaxSize.y));
            }
            // Stored height is the canonical (tips-shown) one; SetTips collapses it afterwards.
            _canonicalH = size.y;
            Rect.sizeDelta = size;

            float x = DefaultPosition.x, y = DefaultPosition.y;
            if (Config != null && !string.IsNullOrEmpty(ConfigKey))
            {
                x = Config.Get(ConfigKey + ".x", float.NaN);
                y = Config.Get(ConfigKey + ".y", float.NaN);
                if (float.IsNaN(x) || float.IsNaN(y)) { x = DefaultPosition.x; y = DefaultPosition.y; }
            }
            Rect.anchoredPosition = new Vector2(x, y);
            UiDragHandler.ClampIntoCanvas(Rect);
        }

        private void Save(Vector2 pos)
        {
            if (Config == null || string.IsNullOrEmpty(ConfigKey)) return;
            Config.Set(ConfigKey + ".x", pos.x);
            Config.Set(ConfigKey + ".y", pos.y);
            Config.Save();
        }

        /// <summary>Centres the window, restores the default size and forgets both.</summary>
        public void ResetPosition()
        {
            if (Rect == null) return;
            _canonicalH = DefaultSize.y;
            Rect.sizeDelta = new Vector2(DefaultSize.x,
                Mathf.Clamp(DefaultSize.y - (TipsShown() ? 0f : TipsHeight), MinSize.y, MaxSize.y));
            Rect.anchoredPosition = DefaultPosition;
            UiDragHandler.ClampIntoCanvas(Rect);
            if (Config != null && !string.IsNullOrEmpty(ConfigKey))
            {
                Config.Set(ConfigKey + ".x", DefaultPosition.x);
                Config.Set(ConfigKey + ".y", DefaultPosition.y);
                Config.Set(ConfigKey + ".w", DefaultSize.x);
                Config.Set(ConfigKey + ".h", DefaultSize.y);
                Config.Save();
            }
            ApplyLayout();
            try { if (Resized != null) Resized(Rect.sizeDelta); } catch { }
        }

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null;
            Rect = null;
            Header = null;
            Body = null;
            Title = null;
            CloseButton = null;
            TipsButton = null;
            TopButton = null;
            _tipsRoots.Clear();
            Resized = null;
        }
    }

    /// <summary>Corner grip that resizes a window and remembers the new size.</summary>
    public sealed class UiResizeHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public UiWindow Window;

        private Vector2 _startSize;
        private Vector2 _startPointer;
        private RectTransform _canvasRect;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Window == null || Window.Rect == null) return;
            var canvas = Window.Rect.GetComponentInParent<Canvas>();
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, eventData.position, eventData.pressEventCamera, out _startPointer);
            _startSize = Window.Rect.sizeDelta;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Window == null || Window.Rect == null || _canvasRect == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, eventData.position, eventData.pressEventCamera, out var now)) return;

            var delta = now - _startPointer;
            // The window is centre-anchored, so growing by d on one side grows the size by d.
            Window.SetSize(new Vector2(_startSize.x + delta.x, _startSize.y - delta.y));
        }
    }

    /// <summary>
    /// Legacy UGUI InputField has no onSelect/onDeselect UnityEvents (TMP's does), so this
    /// relay exposes them as plain callbacks.
    /// </summary>
    public sealed class InputFocusRelay : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        public Action Selected;
        public Action Deselected;

        public void OnSelect(BaseEventData eventData)
        {
            try { if (Selected != null) Selected(); } catch { }
        }

        public void OnDeselect(BaseEventData eventData)
        {
            try { if (Deselected != null) Deselected(); } catch { }
        }
    }

    /// <summary>
    /// Moves a RectTransform by dragging, optionally keeping it inside the canvas, and reports
    /// the new position once the drag ends so it can be persisted.
    ///
    /// Also used for HUD buttons, where it must not swallow clicks: JustDragged lets click
    /// handlers ignore drag-terminated presses.
    /// </summary>
    public sealed class UiDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
                                        IPointerClickHandler
    {
        public RectTransform Target;

        /// <summary>Keep at least this much of the target inside the canvas while dragging.</summary>
        public bool ClampToCanvas = true;
        public float ClampMargin = 24f;

        /// <summary>Raised once the drag finishes (persist the new position here).</summary>
        public Action<Vector2> Moved;

        /// <summary>Raised on a double click that was not a drag (used for "reset position").</summary>
        public Action DoubleClick;

        private static float _lastDragTime = -999f;

        public static bool JustDragged => Time.unscaledTime - _lastDragTime < 0.25f;

        private Vector2 _startPointer;
        private Vector2 _startPos;
        private RectTransform _canvasRect;
        private bool _dragged;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            var canvas = GetComponentInParent<Canvas>();
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, eventData.position, eventData.pressEventCamera, out _startPointer);
            _startPos = Target.anchoredPosition;
            _dragged = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null || _canvasRect == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, eventData.position, eventData.pressEventCamera, out var now))
            {
                var delta = now - _startPointer;
                if (!_dragged && delta.sqrMagnitude < 9f) return; // dead zone keeps plain clicks clickable
                _dragged = true;
                Target.anchoredPosition = Clamp(Target, _startPos + delta);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragged) return;
            _lastDragTime = Time.unscaledTime;
            try { if (Moved != null) Moved(Target != null ? Target.anchoredPosition : Vector2.zero); } catch { }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_dragged || DoubleClick == null) return;
            if (eventData.clickCount < 2) return;
            try { DoubleClick(); } catch { }
        }

        private Vector2 Clamp(RectTransform rt, Vector2 wanted)
        {
            if (!ClampToCanvas || rt == null || _canvasRect == null) return wanted;

            var canvas = _canvasRect.rect.size;
            if (canvas.x <= 1f || canvas.y <= 1f) return wanted;

            var size = rt.rect.size;
            float px = rt.pivot.x, py = rt.pivot.y;

            // Anchor reference point and rect centre, both in canvas-local space (canvas is centred on 0).
            float originX = (rt.anchorMin.x - 0.5f) * canvas.x;
            float originY = (rt.anchorMin.y - 0.5f) * canvas.y;
            float centreX = originX + wanted.x + (0.5f - px) * size.x;
            float centreY = originY + wanted.y + (0.5f - py) * size.y;

            // Allow the rect to leave the screen, but always keep ClampMargin of it reachable.
            float halfKeep = Mathf.Max(0f, ClampMargin - size.x * 0.5f);
            float halfKeepY = Mathf.Max(0f, ClampMargin - size.y * 0.5f);
            centreX = Mathf.Clamp(centreX, -canvas.x * 0.5f - halfKeep, canvas.x * 0.5f + halfKeep);
            centreY = Mathf.Clamp(centreY, -canvas.y * 0.5f - halfKeepY, canvas.y * 0.5f + halfKeepY);

            return new Vector2(centreX - originX - (0.5f - px) * size.x,
                               centreY - originY - (0.5f - py) * size.y);
        }

        /// <summary>One-shot clamp for a freshly restored position.</summary>
        public static void ClampIntoCanvas(RectTransform rt)
        {
            if (rt == null) return;
            try
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                var probe = rt.gameObject.GetComponent<UiDragHandler>();
                bool added = false;
                if (probe == null) { probe = rt.gameObject.AddComponent<UiDragHandler>(); added = true; }
                probe.Target = rt;
                probe._canvasRect = canvas.transform as RectTransform;
                rt.anchoredPosition = probe.Clamp(rt, rt.anchoredPosition);
                if (added) UnityEngine.Object.Destroy(probe);
            }
            catch { }
        }
    }

    /// <summary>A label + native-skinned draggable slider + value readout, as one row.</summary>
    public sealed class UiSliderRow
    {
        public GameObject Root;
        public RectTransform Rect;
        public TMPro.TMP_Text Label;
        public TMPro.TMP_Text ValueText;
        public SliderDrag Drag;
        public float Min;
        public float Max;

        public float Value
        {
            get { return Drag != null ? Drag.Value : Min; }
            set { if (Drag != null) Drag.SetValue(value, true); }
        }

        /// <summary>Set the value without firing callbacks (used when syncing from config).</summary>
        public void SetSilent(float value) { if (Drag != null) Drag.SetValue(value, false); }
    }

    /// <summary>
    /// Click/drag-to-set slider built on the kit's own primitives instead of UGUI's Slider, so the
    /// fill and handle are positioned directly and stay correct under a uniformly scaled window
    /// body. <see cref="OnChanged"/> fires on every value change (live), <see cref="OnCommit"/>
    /// only when the pointer is released, so callers can apply an effect immediately but persist
    /// and log just the final value.
    /// </summary>
    public sealed class SliderDrag : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public RectTransform Track;
        public RectTransform Fill;
        public RectTransform Handle;
        public float HandleWidth = 20f;
        public float Min, Max;
        public TMPro.TMP_Text ValueText;
        public Action<float> OnChanged;
        public Action<float> OnCommit;
        public Func<float, string> Format;

        public float Value { get; private set; }
        private bool _dragging;

        public void SetValue(float value, bool notify)
        {
            value = Mathf.Clamp(value, Min, Max);
            bool changed = Mathf.Abs(value - Value) > 0.0001f;
            Value = value;
            UpdateVisuals();
            if (notify && changed)
            {
                try { if (OnChanged != null) OnChanged(Value); } catch { }
            }
        }

        private void UpdateVisuals()
        {
            float span = Max - Min;
            float t = span > 0.0001f ? Mathf.Clamp01((Value - Min) / span) : 0f;
            float w = Track != null ? Track.rect.width : 0f;
            float usable = Mathf.Max(0f, w - HandleWidth);
            float x = HandleWidth * 0.5f + t * usable;
            if (Fill != null) Fill.sizeDelta = new Vector2(x, Fill.sizeDelta.y);
            if (Handle != null) Handle.anchoredPosition = new Vector2(x, 0f);
            if (ValueText != null)
                ValueText.text = Format != null ? Format(Value) : Mathf.RoundToInt(Value) + "%";
        }

        /// <summary>Re-reads the track width (call after a resize so the handle sits correctly).</summary>
        public void RefreshLayout() { UpdateVisuals(); }

        /// <summary>
        /// Unity fires this when the track's rect is first laid out and on every resize. The track
        /// width is 0 until the parent layout runs, so without this the fill/handle would sit at the
        /// far left until the first drag. Recomputing here makes the slider correct before any input.
        /// </summary>
        private void OnRectTransformDimensionsChange()
        {
            if (Track != null) UpdateVisuals();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _dragging = true;
            FromPointer(eventData);
        }

        public void OnDrag(PointerEventData eventData) { FromPointer(eventData); }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_dragging) return;
            _dragging = false;
            FromPointer(eventData);
            try { if (OnCommit != null) OnCommit(Value); } catch { }
        }

        private void FromPointer(PointerEventData eventData)
        {
            if (Track == null) return;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    Track, eventData.position, eventData.pressEventCamera, out local)) return;
            float w = Track.rect.width;
            float usable = Mathf.Max(1f, w - HandleWidth);
            // local.x is centred on the track pivot; shift to the left edge, then inset by the
            // handle radius so the value maps to the handle's travel, not the full track.
            float fromLeft = local.x + w * 0.5f - HandleWidth * 0.5f;
            float t = Mathf.Clamp01(fromLeft / usable);
            SetValue(Min + t * (Max - Min), true);
        }
    }
}
