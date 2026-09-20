using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlotsamMods.MiniMap
{
    /// <summary>
    /// A freely zoomable, pannable minimap built to look like the game's own map.
    ///
    /// The native world-map tiles cannot simply be reused: they hang off the deactivated
    /// WorldMap hierarchy, and re-activating that hierarchy renders the map's sea meshes at
    /// world coordinates straight into the gameplay camera. So the map is composited from data:
    ///   * sea/tile layout from WorldTile.WorldBounds
    ///   * the REAL fog-of-war mask from WorldTile.FogOfWarAlphas (public per-vertex bytes)
    ///   * the game's OWN marker icons via ISpawner.Icon, drawn as a crisp UGUI layer
    ///   * the player's platform as light dots with a dark rim in the texture, plus a small
    ///     anti-aliased heading arrow
    ///
    /// The backing texture is NOT square: it fills whatever rectangle the window body offers, so
    /// resizing the window really changes how much world you see instead of leaving white bands.
    /// </summary>
    public sealed class MiniMapMod : FlotsamModBase
    {
        private GameObject _overlay;
        private UiWindow _window;
        private MiniMapView _view;
        private TMP_Text _info;
        private TMP_Text _legend;
        private Button _followButton;
        private TMP_Text _followLabel;
        private RectTransform _controlsRt;
        private IKeybind _hotkey;
        private IKeybind _centerKey;
        private IHudButton _button;
        private bool _visible;
        private bool _follow = true;

        private const float ControlsFull = 116f;
        private const float ControlsCompact = 30f;
        private const float TipsBlock = ControlsFull - ControlsCompact;

        public bool Follow => _follow;
        public bool Visible => _visible;
        public IModContext Context => Ctx;

        public void SetFollow(bool value)
        {
            _follow = value;
            Config.Set("followTownheart", value);
            Config.Save();
            UpdateFollowButton();
        }

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _hotkey = Keybinds.Register("minimap.toggle", KeyCode.M, "小地图开关");
            _centerKey = Keybinds.Register("minimap.recenter", KeyCode.Home, "小地图回到镇心并跟随");
        }

        public override void OnEnable()
        {
            MigrateConfig();

            _visible = Config.Get("visible", true);
            _follow = Config.Get("followTownheart", true);

            // The window is built lazily on the first tick inside a save: the harvested native
            // sprites and the game's TMP font only exist once the gameplay scene is up.
            _button = Ui.AddHudButton("minimap.open", "小地图", Toggle, HudAnchor.RightTop,
                                      Config, "button");
            _button.Visible = true;
            _button.SetIcon(NativeSkin.Find("map", "compass", "icon_map"));

            ApplyVisibility();
            Log.Info("minimap ready (window builds on the first frame inside a save)");
        }

        /// <summary>
        /// The previous version's `OnGameStart` called `FitWorld()`, which persisted
        /// followTownheart = 0 and left the map zoomed out to the whole world — the state that
        /// read as "the minimap does not refresh". Reset those once, and carry the old
        /// buttonX/buttonY keys over to the new "button.x" / "button.y" naming.
        /// </summary>
        private void MigrateConfig()
        {
            if (Config.Get("schema", 0) >= 2) return;

            if (!Config.Has("button.x") && Config.Has("buttonX"))
            {
                Config.Set("button.x", Config.Get("buttonX", float.NaN));
                Config.Set("button.y", Config.Get("buttonY", float.NaN));
            }

            Config.Set("visible", true);
            Config.Set("followTownheart", true);
            if (!Config.Has("viewRange")) Config.Set("viewRange", 1400f);
            Config.Set("schema", 2);
            Config.Save();
            Log.Info("config migrated to schema 2 (follow restored, view range in world units)");
        }

        public override void OnDisable()
        {
            try { _button?.Destroy(); } catch { }
            _button = null;
            TeardownUi();
            Log.Info("minimap removed");
        }

        /// <summary>
        /// Drops the window and its texture. The harvested sprites belong to the scene that is
        /// going away, so the window is rebuilt (and re-skinned) on the next game start.
        /// </summary>
        private void TeardownUi()
        {
            if (_view != null) _view.Dispose();
            if (_window != null) _window.Destroy();
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
            _window = null;
            _view = null;
            _info = null;
            _legend = null;
            _followButton = null;
            _followLabel = null;
            _controlsRt = null;
        }

        public override void OnGameEnd()
        {
            TeardownUi();
        }

        private void EnsureUi()
        {
            if (_window != null || !GameApi.IsPlaying) return;
            BuildUi();
            Log.Info("minimap window built; skin " + (NativeSkin.Available ? "native" : "procedural"));
        }

        private void Toggle()
        {
            EnsureUi();
            if (_window == null) return;
            _visible = !_visible;
            Config.Set("visible", _visible);
            Config.Save();
            ApplyVisibility();
            if (_visible) _view?.RefreshNow();
        }

        private void ApplyVisibility()
        {
            if (_window != null) _window.Visible = _visible;
            if (_button != null) _button.SetLabel(_visible ? "隐藏地图" : "小地图");
        }

        private void UpdateFollowButton()
        {
            if (_followLabel != null) _followLabel.text = _follow ? "跟随：开" : "跟随：关";
        }

        // ------------------------------------------------------------ ui

        private void BuildUi()
        {
            NativeSkin.Harvest();

            // The map surface is derived from the window size, so resizing the window really
            // changes how much of the world you see instead of just magnifying pixels.
            int side = Mathf.Clamp(Config.Get("textureSize", 384), 128, 1024);
            float refreshHz = Mathf.Clamp(Config.Get("refreshHz", 10f), 1f, 30f);
            float viewRange = Mathf.Clamp(Config.Get("viewRange", 1400f), 100f, 200000f);
            bool showFog = Config.Get("showFog", true);
            bool fogInvert = Config.Get("fogInvert", false);
            bool showIcons = Config.Get("showIcons", true);
            bool showTown = Config.Get("showTown", true);
            float iconSize = Mathf.Clamp(Config.Get("iconSize", 20f), 8f, 48f);
            float iconMinAlpha = Mathf.Clamp01(Config.Get("iconMinAlpha", 0.45f));

            _overlay = Ui.CreateOverlay("minimap", 30600);

            _window = GameUi.Window(_overlay.transform, "小地图",
                                    new Vector2(side + 20f, side + 160f),
                                    new Vector2(600f, 40f), Config, "window", Toggle, 28f);
            _window.ContentMode = WindowContentMode.None;
            _window.MinSize = new Vector2(220f, 260f);
            _window.MaxSize = new Vector2(1400f, 1060f);
            _window.Resized += OnWindowResized;

            var surface = GameUi.NewUi("Surface", _window.Body.transform, typeof(RawImage));
            var surfaceRt = GameUi.Rect(surface);
            surfaceRt.anchorMin = new Vector2(0.5f, 1f);
            surfaceRt.anchorMax = new Vector2(0.5f, 1f);
            surfaceRt.pivot = new Vector2(0.5f, 1f);
            surfaceRt.anchoredPosition = new Vector2(0f, 0f);
            surfaceRt.sizeDelta = new Vector2(side, side);

            var raw = surface.GetComponent<RawImage>();
            raw.raycastTarget = true;

            _view = surface.AddComponent<MiniMapView>();
            _view.Initialize(this, raw, side, side, refreshHz, viewRange, showFog, fogInvert,
                             showIcons, showTown, iconSize, iconMinAlpha);

            // controls --------------------------------------------------
            var controls = GameUi.NewUi("Controls", _window.Body.transform);
            _controlsRt = GameUi.Rect(controls);
            var crt = _controlsRt;
            crt.anchorMin = new Vector2(0f, 0f);
            crt.anchorMax = new Vector2(1f, 0f);
            crt.pivot = new Vector2(0.5f, 0f);
            crt.anchoredPosition = new Vector2(0f, 0f);
            crt.sizeDelta = new Vector2(0f, ControlsFull);

            string[] labels = { "放大", "缩小", "适应世界", "跟随：开" };
            Action[] actions =
            {
                () => _view.ZoomBy(0.7f),
                () => _view.ZoomBy(1.4f),
                FitWorld,
                () =>
                {
                    SetFollow(!_follow);
                    Ui.Toast(_follow ? "小地图：跟随镇心" : "小地图：自由平移", ToastKind.Info);
                }
            };

            for (int i = 0; i < labels.Length; i++)
            {
                var btn = GameUi.TextButton(controls.transform, labels[i], actions[i], 12);
                var brt = GameUi.Rect(btn.gameObject);
                brt.anchorMin = new Vector2(i / (float)labels.Length, 1f);
                brt.anchorMax = new Vector2((i + 1) / (float)labels.Length, 1f);
                brt.pivot = new Vector2(0.5f, 1f);
                brt.offsetMin = new Vector2(2f, 0f);
                brt.offsetMax = new Vector2(-2f, 0f);
                brt.sizeDelta = new Vector2(brt.sizeDelta.x, 24f);
                brt.anchoredPosition = new Vector2(brt.anchoredPosition.x, -2f);
                if (i == 3) { _followButton = btn; _followLabel = btn.GetComponentInChildren<TMP_Text>(); }
            }
            UpdateFollowButton();

            _info = GameUi.Label(controls.transform, "", 12, GameUi.DimText, TextAnchor.MiddleLeft, bold: false);
            PlaceText(_info, -30f, 18f);

            _legend = GameUi.Label(controls.transform, "", 12, GameUi.DimText, TextAnchor.MiddleLeft, bold: false);
            PlaceText(_legend, -48f, 18f);

            var hint = GameUi.Label(controls.transform,
                                    "滚轮缩放 · 拖拽平移 · 左键点击自动航行 · 双击居中 · 右键回镇心/取消 · M 开关 · Home 复位\n拖动标题栏移动窗口，双击标题栏复位",
                                    11, GameUi.DimText, TextAnchor.UpperLeft, true, bold: false);
            var hrt = GameUi.Rect(hint.gameObject);
            hrt.anchorMin = new Vector2(0f, 1f);
            hrt.anchorMax = new Vector2(1f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.anchoredPosition = new Vector2(0f, -68f);
            hrt.sizeDelta = new Vector2(-4f, 44f);

            _window.TipsChanged += ApplyTipsHeight;
            _window.SetTips(GameUi.Rect(_info.gameObject), GameUi.Rect(_legend.gameObject), hrt);
            _window.Visible = _visible;
            OnWindowResized(_window.Size);
        }

        private void PlaceText(TMP_Text label, float y, float height)
        {
            var rt = GameUi.Rect(label.gameObject);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(-8f, height);
        }

        /// <summary>
        /// Hiding the tips must reclaim their space instead of leaving an empty white band: the
        /// controls block shrinks to just its button row and the window follows.
        /// </summary>
        private void ApplyTipsHeight(bool show)
        {
            if (_controlsRt == null || _window == null) return;
            _controlsRt.sizeDelta = new Vector2(0f, show ? ControlsFull : ControlsCompact);
            var size = _window.Size;
            _window.SetSize(new Vector2(size.x,
                Mathf.Max(_window.MinSize.y, size.y + (show ? TipsBlock : -TipsBlock))));
        }

        /// <summary>Refits the map texture to whatever rectangle the window body now offers.</summary>
        private void OnWindowResized(Vector2 windowSize)
        {
            if (_view == null || _window == null) return;
            float controls = _controlsRt != null ? _controlsRt.sizeDelta.y : ControlsFull;
            float availW = Mathf.Max(96f, windowSize.x - 16f);
            float availH = Mathf.Max(96f, windowSize.y - _window.HeaderHeight - 12f - controls - 4f);
            _view.Resize(Mathf.RoundToInt(availW), Mathf.RoundToInt(availH));
        }

        private void FitWorld()
        {
            _view.FitWorld();
            Ui.Toast("小地图：已缩放到整个世界（跟随已关）", ToastKind.Info);
        }

        public override void OnGameStart()
        {
            // Deliberately NOT FitWorld(): that would switch follow off and zoom out to the whole
            // world, which is exactly what made the map look frozen.
            _view?.RefreshNow();
            Ui.Toast($"小地图就绪：{_hotkey.Key} 开关，{_centerKey.Key} 回到镇心，左键点击自动航行，滚轮缩放 / 拖拽平移", ToastKind.Success);
        }

        /// <summary>The keybind service only reports state; a mod class is not a MonoBehaviour, so this is the pump.</summary>
        public override void OnTick()
        {
            EnsureUi();

            if (_hotkey != null && _hotkey.IsDown) Toggle();

            if (_centerKey != null && _centerKey.IsDown)
            {
                SetFollow(true);
                _view?.RefreshNow();
                Ui.Toast("小地图：已回到镇心并跟随", ToastKind.Info);
            }
        }

        internal void SetInfo(string text)
        {
            if (_info != null && _info.text != text) _info.text = text;
        }

        internal void SetLegend(string text)
        {
            if (_legend != null && _legend.text != text) _legend.text = text;
        }
    }

    /// <summary>
    /// Owns the minimap background texture, the native-icon overlay and the zoom/pan input.
    /// The texture is rectangular and always fills the surface rect.
    /// </summary>
    public sealed class MiniMapView : MonoBehaviour, IScrollHandler, IDragHandler, IBeginDragHandler,
                                      IPointerClickHandler
    {
        private const float MinViewRange = 120f;
        private const float MaxViewRange = 400000f;
        private const int MaxIcons = 260;
        private const int MinSide = 96;
        private const int MaxSide = 2048;

        private MiniMapMod _mod;
        private RawImage _image;
        private RectTransform _rect;
        private RectTransform _iconLayer;
        private Texture2D _texture;
        private Color32[] _pixels;

        private int _texW = 384;
        private int _texH = 384;
        private float _refreshInterval = 0.1f;
        private float _nextRefresh;
        private float _viewRange = 1400f;
        private Vector2 _center;
        private bool _hasCenter;
        private Rect _bounds;

        private bool _showFog = true;
        private bool _fogInvert;
        private bool _showIcons = true;
        private bool _showTown = true;
        private float _iconSize = 20f;
        private float _iconMinAlpha = 0.45f;

        private readonly List<Image> _iconPool = new List<Image>();
        private readonly List<GameWorld.Marker> _visibleMarkers = new List<GameWorld.Marker>();
        private readonly List<GameWorld.Marker> _allMarkers = new List<GameWorld.Marker>(512);
        private readonly List<GameWorld.Marker> _townDots = new List<GameWorld.Marker>();
        private readonly List<GameWorld.FogPatch> _fogPatches = new List<GameWorld.FogPatch>();
        private readonly List<Rect> _tiles = new List<Rect>();
        private Image _townheartIcon;
        private Image _headingOutline;
        private Image _playerRing;
        private Image _scaleBar;
        private TMP_Text _scaleLabel;

        // Contrast: a saturated teal sea against a deep navy void, fog as a clearly darker band.
        private static readonly Color32 Bg = new Color32(9, 24, 36, 255);
        private static readonly Color32 Sea = new Color32(56, 138, 156, 255);
        private static readonly Color32 SeaEdge = new Color32(126, 205, 214, 255);
        private static readonly Color32 FogCell = new Color32(14, 32, 44, 215);
        private static readonly Color32 TownRim = new Color32(10, 26, 34, 255);
        private static readonly Color32 TownDot = new Color32(238, 250, 245, 255);
        private static readonly Color32 AutoMark = new Color32(120, 240, 180, 230);

        public void Initialize(MiniMapMod mod, RawImage image, int width, int height, float refreshHz,
                               float viewRange, bool showFog, bool fogInvert, bool showIcons, bool showTown,
                               float iconSize, float iconMinZoomAlpha)
        {
            _mod = mod;
            _image = image;
            _rect = image.rectTransform;
            _refreshInterval = 1f / Mathf.Max(1f, refreshHz);
            _viewRange = Mathf.Clamp(viewRange, MinViewRange, MaxViewRange);
            _showFog = showFog;
            _fogInvert = fogInvert;
            _showIcons = showIcons;
            _showTown = showTown;
            _iconSize = iconSize;
            _iconMinAlpha = iconMinZoomAlpha;

            CreateTexture(width, height);

            // Native marker icons live on their own layer above the texture so they stay
            // crisp at any zoom and never need a texture readback.
            var layer = GameUi.NewUi("IconLayer", _image.transform);
            _iconLayer = GameUi.Rect(layer);
            GameUi.Stretch(_iconLayer);

            BuildScaleBar();
            RefreshNow();
        }

        private void CreateTexture(int width, int height)
        {
            _texW = Mathf.Clamp(width, MinSide, MaxSide);
            _texH = Mathf.Clamp(height, MinSide, MaxSide);

            try { if (_texture != null) UnityEngine.Object.Destroy(_texture); } catch { }
            _texture = new Texture2D(_texW, _texH, TextureFormat.RGBA32, false);
            _texture.filterMode = FilterMode.Bilinear;
            _texture.wrapMode = TextureWrapMode.Clamp;
            _pixels = new Color32[_texW * _texH];
            Fill(Bg);
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
            if (_image != null) _image.texture = _texture;
            if (_rect != null) _rect.sizeDelta = new Vector2(_texW, _texH);
        }

        /// <summary>Refits the backing texture to a new rectangle (window resize).</summary>
        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, MinSide, MaxSide);
            height = Mathf.Clamp(height, MinSide, MaxSide);
            if (width == _texW && height == _texH) return;

            CreateTexture(width, height);
            LayoutOverlays();
            RefreshNow();
        }

        private void BuildScaleBar()
        {
            var bar = GameUi.NewUi("ScaleBar", _iconLayer, typeof(Image));
            _scaleBar = bar.GetComponent<Image>();
            _scaleBar.color = new Color(1f, 1f, 1f, 0.75f);
            _scaleBar.raycastTarget = false;
            var brt = GameUi.Rect(bar);
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 0f);
            brt.pivot = new Vector2(0f, 0f);
            brt.sizeDelta = new Vector2(60f, 3f);

            _scaleLabel = GameUi.Label(_iconLayer, "", 11, new Color(1f, 1f, 1f, 0.85f),
                                       TextAnchor.MiddleLeft, bold: false);
            _scaleLabel.raycastTarget = false;
            var lrt = GameUi.Rect(_scaleLabel.gameObject);
            lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 0f);
            lrt.pivot = new Vector2(0f, 0f);
            lrt.sizeDelta = new Vector2(140f, 16f);
            LayoutOverlays();
        }

        private void LayoutOverlays()
        {
            if (_scaleBar != null)
                GameUi.Rect(_scaleBar.gameObject).anchoredPosition = new Vector2(-_texW * 0.5f + 8f, -_texH * 0.5f + 8f);
            if (_scaleLabel != null)
                GameUi.Rect(_scaleLabel.gameObject).anchoredPosition = new Vector2(-_texW * 0.5f + 8f, -_texH * 0.5f + 13f);
        }

        public void Dispose()
        {
            try
            {
                if (_image != null) _image.texture = null;
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
            }
            catch { }
            _texture = null;
            _pixels = null;
            _iconPool.Clear();
            _visibleMarkers.Clear();
            _allMarkers.Clear();
            _townDots.Clear();
        }

        // ------------------------------------------------------------ interaction

        public void OnScroll(PointerEventData eventData)
        {
            float delta = eventData.scrollDelta.y;
            if (Mathf.Approximately(delta, 0f)) return;

            if (!CursorWorld(eventData, out var before)) { ZoomBy(delta > 0f ? 0.87f : 1.15f); return; }
            ZoomBy(delta > 0f ? 0.87f : 1.15f);
            if (CursorWorld(eventData, out var after)) _center += before - after;
            _hasCenter = true;
            RefreshNow();
        }

        private bool CursorWorld(PointerEventData eventData, out Vector2 world)
        {
            world = Vector2.zero;
            if (_rect == null) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position,
                    eventData.pressEventCamera, out var local)) return false;
            // The surface pivot is TOP-centre, so local.y runs 0 (top edge) to -texH (bottom);
            // texture pixel space has its origin at the BOTTOM-left with y up.
            world = PxToWorld(new Vector2(_texW * 0.5f + local.x, _texH + local.y));
            return true;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) _mod?.SetFollow(false);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_rect == null || eventData.button != PointerEventData.InputButton.Left) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position,
                    eventData.pressEventCamera, out var now)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position - eventData.delta,
                    eventData.pressEventCamera, out var prev)) return;

            float scale = CurrentScale();
            if (scale <= 0f) return;
            _center -= (now - prev) / scale;
            _hasCenter = true;
            RefreshNow();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                GameMovement.CancelAuto();
                _mod?.SetFollow(true);
                RefreshNow();
                return;
            }
            if (eventData.button != PointerEventData.InputButton.Left) return;

            if (eventData.clickCount >= 2)
            {
                GameMovement.CancelAuto();
                if (!CursorWorld(eventData, out var centre)) return;
                _center = centre;
                _hasCenter = true;
                _mod?.SetFollow(false);
                RefreshNow();
                return;
            }

            // Single left click: sail there, the way clicking the world map does. A drag-pan
            // ending on the surface must not count as a click.
            if (UiDragHandler.JustDragged) return;
            if (!CursorWorld(eventData, out var world)) return;
            GameMovement.StartAuto(new Vector3(world.x, 0f, world.y));
            if (_mod != null)
                _mod.Context.Ui.Toast($"自动航行至 ({world.x:0}, {world.y:0})·方向键可接管·右键取消",
                                      ToastKind.Success);
            RefreshNow();
        }

        /// <summary>Multiplies the world span shown across the shorter texture axis.</summary>
        public void ZoomBy(float factor)
        {
            _viewRange = Mathf.Clamp(_viewRange * factor, MinViewRange, MaxViewRange);
            // Marked dirty only: scrolling fires many times a second and the host already saves
            // periodically and on disable, so writing the file here would thrash the disk.
            if (_mod != null) _mod.Context.Config.Set("viewRange", _viewRange);
        }

        /// <summary>Zooms out until the whole generated world fits; leaves follow alone.</summary>
        public void FitWorld()
        {
            Rect bounds;
            if (!GameWorld.TryGetWorldBounds(out bounds)) return;
            _viewRange = Mathf.Clamp(Mathf.Max(bounds.width, bounds.height) * 1.05f, MinViewRange, MaxViewRange);
            _center = bounds.center;
            _hasCenter = true;
            _mod?.SetFollow(false);
            if (_mod != null) _mod.Context.Config.Set("viewRange", _viewRange);
            RefreshNow();
        }

        public void RefreshNow()
        {
            _nextRefresh = 0f;
            Refresh();
        }

        private void Update()
        {
            if (_texture == null) return;

            // The player marker tracks every frame so a keyboard voyage reads as live motion;
            // only the composited texture is throttled.
            UpdateTownheartMarker();

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + _refreshInterval;
            Refresh();
        }

        // ------------------------------------------------------------ projection

        private float CurrentScale() => Mathf.Min(_texW, _texH) / Mathf.Max(1f, _viewRange);

        private Vector2 PxToWorld(Vector2 px)
        {
            float scale = CurrentScale();
            return _center + (px - new Vector2(_texW * 0.5f, _texH * 0.5f)) / scale;
        }

        private Vector2 WorldToPx(Vector2 world)
        {
            float scale = CurrentScale();
            return new Vector2(_texW * 0.5f + (world.x - _center.x) * scale,
                               _texH * 0.5f + (world.y - _center.y) * scale);
        }

        // ------------------------------------------------------------ refresh

        private void Refresh()
        {
            if (_texture == null || _pixels == null) return;
            if (_mod != null && !_mod.Visible) return;

            try
            {
                if (!GameWorld.TryGetWorldBounds(out _bounds))
                {
                    Fill(Bg);
                    Blit();
                    HideIcons();
                    _mod?.SetInfo("等待世界数据…（进入存档后显示）");
                    return;
                }

                var townheart = GameMovement.DisplayPosition;
                var townheart2D = new Vector2(townheart.x, townheart.z);

                if (_mod != null && _mod.Follow) { _center = townheart2D; _hasCenter = true; }
                else if (!_hasCenter) { _center = townheart2D; _hasCenter = true; }

                Fill(Bg);

                GameWorld.TileBounds(_tiles);
                for (int i = 0; i < _tiles.Count; i++)
                {
                    var a = WorldToPx(new Vector2(_tiles[i].xMin, _tiles[i].yMin));
                    var b = WorldToPx(new Vector2(_tiles[i].xMax, _tiles[i].yMax));
                    FillRect(a, b, Sea);
                    StrokeRect(a, b, SeaEdge);
                }

                DrawSwimRadius(townheart2D);

                if (GameMovement.AutoActive)
                    DrawAutoTarget(new Vector2(GameMovement.AutoTarget.x, GameMovement.AutoTarget.z));

                if (_showFog)
                {
                    GameWorld.FogPatches(_fogPatches);
                    for (int i = 0; i < _fogPatches.Count; i++) DrawFog(_fogPatches[i]);
                }

                // The player's own platform as crisp light dots with a dark rim: buildable icon
                // sprites are mostly null and shadow squares turned the town into a black blob.
                _townDots.Clear();
                if (_showTown)
                {
                    GameWorld.TownMarkers(_townDots);
                    for (int i = 0; i < _townDots.Count; i++)
                    {
                        var p = WorldToPx(new Vector2(_townDots[i].WorldPosition.x, _townDots[i].WorldPosition.z));
                        FillRect(p - new Vector2(3f, 3f), p + new Vector2(3f, 3f), TownRim);
                        FillRect(p - new Vector2(2f, 2f), p + new Vector2(2f, 2f), TownDot);
                    }
                }

                Blit();

                int shown = UpdateIcons(townheart2D);
                UpdateLegend(shown);
                UpdateScaleBar();

                _mod?.SetInfo($"视野 {_viewRange:0} 单位 · 中心 ({_center.x:0},{_center.y:0}) · " +
                              (GameMovement.AutoActive
                                  ? $"自动航行剩 {(GameMovement.AutoTarget - townheart).magnitude:0}"
                                  : GameMovement.Sailing ? "航行中（未靠港）" : "已靠港"));
            }
            catch (Exception e)
            {
                _mod?.Context?.Log.Warn("minimap draw failed: " + e.Message);
            }
        }

        private void DrawSwimRadius(Vector2 townheart2D)
        {
            float swim, map;
            if (!GameWorld.TryGetRadii(out swim, out map)) return;
            if (swim > 0f) StrokeCircle(townheart2D, swim, new Color32(255, 255, 255, 150));
        }

        /// <summary>Mint crosshair ring on the click-to-sail target while the autopilot runs.</summary>
        private void DrawAutoTarget(Vector2 world)
        {
            var c = WorldToPx(world);
            float r = 7f;
            const int steps = 24;
            var prev = c + new Vector2(r, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                var next = c + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                StrokeLine(prev, next, AutoMark);
                prev = next;
            }
            StrokeLine(c - new Vector2(r + 4f, 0f), c - new Vector2(r - 2f, 0f), AutoMark);
            StrokeLine(c + new Vector2(r - 2f, 0f), c + new Vector2(r + 4f, 0f), AutoMark);
            StrokeLine(c - new Vector2(0f, r + 4f), c - new Vector2(0f, r - 2f), AutoMark);
            StrokeLine(c + new Vector2(0f, r - 2f), c + new Vector2(0f, r + 4f), AutoMark);
        }

        private void StrokeCircle(Vector2 worldCenter, float worldRadius, Color32 color)
        {
            float scale = CurrentScale();
            float r = worldRadius * scale;
            if (r < 2f || r > Mathf.Max(_texW, _texH) * 4f) return;

            int steps = Mathf.Clamp(Mathf.CeilToInt(r * 0.6f), 24, 360);
            var c = WorldToPx(worldCenter);
            Vector2 prev = new Vector2(c.x + r, c.y);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                var next = new Vector2(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r);
                StrokeLine(prev, next, color);
                prev = next;
            }
        }

        private void StrokeLine(Vector2 a, Vector2 b, Color32 color)
        {
            float dist = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                FillRect(p, p, color);
            }
        }

        /// <summary>
        /// Draws the genuine fog mask. Vertex (i,j) lives at index j + i*(Cells+1); a cell is
        /// dark when its average alpha says "not explored". Whether 1 means fog or clear is
        /// not observable from code, hence the fogInvert config.
        /// </summary>
        private void DrawFog(GameWorld.FogPatch patch)
        {
            var alphas = patch.Alphas;
            if (alphas == null || patch.CellsX <= 0 || patch.CellsZ <= 0) return;

            int stride = patch.CellsX + 1;
            float cellW = patch.WorldBounds.width / patch.CellsX;
            float cellH = patch.WorldBounds.height / patch.CellsZ;

            var tl = WorldToPx(new Vector2(patch.WorldBounds.xMin, patch.WorldBounds.yMax));
            var br = WorldToPx(new Vector2(patch.WorldBounds.xMax, patch.WorldBounds.yMin));
            int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(tl.x, br.x) - patch.WorldBounds.xMin) / cellW) - 1);
            int i1 = Mathf.Min(patch.CellsX - 1, Mathf.CeilToInt((Mathf.Max(tl.x, br.x) - patch.WorldBounds.xMin) / cellW) + 1);
            int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(tl.y, br.y) - patch.WorldBounds.yMin) / cellH) - 1);
            int j1 = Mathf.Min(patch.CellsZ - 1, Mathf.CeilToInt((Mathf.Max(tl.y, br.y) - patch.WorldBounds.yMin) / cellH) + 1);

            for (int i = i0; i <= i1; i++)
            {
                int baseIdx = i * stride;
                for (int j = j0; j <= j1; j++)
                {
                    int a = alphas[baseIdx + j];
                    int b = alphas[baseIdx + j + 1];
                    int c = alphas[(i + 1) * stride + j];
                    int d = alphas[(i + 1) * stride + j + 1];
                    float fog = (a + b + c + d) / (4f * 255f);
                    if (_fogInvert) fog = 1f - fog;
                    if (fog < 0.5f) continue;

                    float wx0 = patch.WorldBounds.xMin + i * cellW;
                    float wy0 = patch.WorldBounds.yMin + j * cellH;
                    var pa = WorldToPx(new Vector2(wx0, wy0));
                    var pb = WorldToPx(new Vector2(wx0 + cellW, wy0 + cellH));
                    FillRect(pa, pb, FogCell);
                }
            }
        }

        // ------------------------------------------------------------ icon layer

        private void HideIcons()
        {
            for (int i = 0; i < _iconPool.Count; i++)
                if (_iconPool[i] != null && _iconPool[i].gameObject.activeSelf)
                    _iconPool[i].gameObject.SetActive(false);
        }

        /// <summary>Draws the game's own marker sprites; returns how many are shown.</summary>
        private int UpdateIcons(Vector2 townheart2D)
        {
            if (_iconLayer == null) return 0;

            _allMarkers.Clear();
            if (_showIcons) GameWorld.Markers(_allMarkers);   // town is drawn in the texture now

            _visibleMarkers.Clear();
            var viewMin = PxToWorld(Vector2.zero);
            var viewMax = PxToWorld(new Vector2(_texW, _texH));
            var view = Rect.MinMaxRect(
                Mathf.Min(viewMin.x, viewMax.x), Mathf.Min(viewMin.y, viewMax.y),
                Mathf.Max(viewMin.x, viewMax.x), Mathf.Max(viewMin.y, viewMax.y));

            for (int i = 0; i < _allMarkers.Count && _visibleMarkers.Count < MaxIcons; i++)
            {
                var w = new Vector2(_allMarkers[i].WorldPosition.x, _allMarkers[i].WorldPosition.z);
                if (!view.Contains(w)) continue;
                _visibleMarkers.Add(_allMarkers[i]);
            }

            float iconSize = _iconSize * Mathf.Clamp(1400f / Mathf.Max(1f, _viewRange), 0.8f, 1.8f);
            var half = new Vector2(_texW * 0.5f, _texH * 0.5f);

            for (int i = 0; i < _visibleMarkers.Count; i++)
            {
                var m = _visibleMarkers[i];
                var px = WorldToPx(new Vector2(m.WorldPosition.x, m.WorldPosition.z));
                var icon = GetIcon(i);
                if (icon == null) continue;

                var rt = icon.rectTransform;
                rt.anchoredPosition = px - half;
                rt.sizeDelta = new Vector2(iconSize * 0.9f, iconSize * 0.9f);

                icon.sprite = m.Icon;
                icon.enabled = m.Icon != null;

                float alpha = m.ScoutingState >= 4 ? 1f
                            : m.ScoutingState == 3 ? 0.85f
                            : _iconMinAlpha;
                icon.color = m.Kind == GameWorld.MarkerKind.Flotsam
                    ? new Color(0.95f, 0.62f, 0.30f, alpha)
                    : new Color(1f, 1f, 1f, alpha);
                icon.gameObject.SetActive(m.Icon != null);
            }

            for (int i = _visibleMarkers.Count; i < _iconPool.Count; i++)
                if (_iconPool[i] != null && _iconPool[i].gameObject.activeSelf)
                    _iconPool[i].gameObject.SetActive(false);

            EnsureTownheartIcon();
            UpdateTownheartMarker();
            return _visibleMarkers.Count;
        }

        private void EnsureTownheartIcon()
        {
            if (_townheartIcon != null) return;

            // A white ring plus a small anti-aliased arrow: readable on sea and fog alike, and
            // the heading is unmistakable. Native construction icons are dark squares and made
            // the heading impossible to read.
            var ring = GameUi.NewUi("PlayerRing", _iconLayer, typeof(Image));
            _playerRing = ring.GetComponent<Image>();
            _playerRing.sprite = NativeSkin.Find("RadialCircle_Border", "circle_border");
            _playerRing.color = new Color(1f, 1f, 1f, 0.95f);
            _playerRing.raycastTarget = false;
            if (_playerRing.sprite == null) _playerRing.gameObject.SetActive(false);

            var outline = GameUi.NewUi("HeadingOutline", _iconLayer, typeof(Image));
            _headingOutline = outline.GetComponent<Image>();
            _headingOutline.sprite = CreateArrowSprite();
            _headingOutline.color = new Color(0.05f, 0.12f, 0.16f, 1f);
            _headingOutline.raycastTarget = false;

            var go = GameUi.NewUi("Townheart", _iconLayer, typeof(Image));
            _townheartIcon = go.GetComponent<Image>();
            _townheartIcon.sprite = CreateArrowSprite();
            _townheartIcon.preserveAspect = true;
            _townheartIcon.raycastTarget = false;
        }

        private void UpdateTownheartMarker()
        {
            if (_townheartIcon == null || _iconLayer == null) return;

            var townheart = GameMovement.DisplayPosition;
            var townheart2D = new Vector2(townheart.x, townheart.z);

            // Small and fixed-ish: a giant arrow swallowed the whole town.
            float marker = Mathf.Clamp(_iconSize * 0.75f, 12f, 22f) * 1.5f;
            var centre = WorldToPx(townheart2D) - new Vector2(_texW * 0.5f, _texH * 0.5f);

            _townheartIcon.gameObject.SetActive(true);
            var rt = _townheartIcon.rectTransform;
            rt.anchoredPosition = centre;
            rt.sizeDelta = new Vector2(marker, marker);

            var forward = GameMovement.DisplayRotation * Vector3.forward;
            float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            rt.localRotation = Quaternion.Euler(0f, 0f, -angle);
            _townheartIcon.color = GameMovement.Sailing
                ? new Color(0.55f, 0.98f, 0.70f, 1f)
                : new Color(1f, 1f, 1f, 1f);

            if (_headingOutline != null)
            {
                _headingOutline.rectTransform.anchoredPosition = centre;
                _headingOutline.rectTransform.sizeDelta = new Vector2(marker + 3f, marker + 3f);
                _headingOutline.rectTransform.localRotation = rt.localRotation;
            }

            if (_playerRing != null && _playerRing.gameObject.activeSelf)
            {
                _playerRing.rectTransform.anchoredPosition = centre;
                _playerRing.rectTransform.sizeDelta = new Vector2(marker + 12f, marker + 12f);
            }
        }

        private Image GetIcon(int index)
        {
            while (_iconPool.Count <= index)
            {
                var go = GameUi.NewUi("Marker", _iconLayer, typeof(Image));
                var img = go.GetComponent<Image>();
                img.raycastTarget = false;
                img.preserveAspect = true;
                _iconPool.Add(img);
            }
            var icon = _iconPool[index];
            if (icon != null && !icon.gameObject.activeSelf) icon.gameObject.SetActive(true);
            return icon;
        }

        private void UpdateLegend(int shown)
        {
            int landmarks = 0, pois = 0, flotsamCount = 0;
            for (int i = 0; i < _visibleMarkers.Count; i++)
            {
                switch (_visibleMarkers[i].Kind)
                {
                    case GameWorld.MarkerKind.Landmark: landmarks++; break;
                    case GameWorld.MarkerKind.PointOfInterest: pois++; break;
                    case GameWorld.MarkerKind.Flotsam: flotsamCount++; break;
                }
            }
            int town = _townDots.Count;
            _mod?.SetLegend($"我的建筑 {town} · 地标 {landmarks} · 兴趣点 {pois} · 垃圾 {flotsamCount} · 共 {shown}");
        }

        /// <summary>
        /// A high-resolution anti-aliased heading arrow. The old 16px version was magnified to
        /// 40px with bilinear filtering, which is exactly the blurry jagged edge that was reported.
        /// </summary>
        private static Sprite CreateArrowSprite()
        {
            const int s = 128;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            var pixels = new Color32[s * s];

            // Triangle apex at top-centre, base at the bottom; coverage is sampled per pixel so
            // the diagonal edges stay smooth at any display size.
            Vector2 a = new Vector2(s * 0.5f, s * 0.94f);   // apex
            Vector2 b = new Vector2(s * 0.14f, s * 0.12f);  // base left
            Vector2 c = new Vector2(s * 0.86f, s * 0.12f);  // base right

            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    // Positive INSIDE for a CCW-wound triangle; the previous build took the max
                    // and subtracted it, which made every interior pixel fully transparent.
                    float d = SignedDist(p, a, b);
                    d = Mathf.Min(d, SignedDist(p, b, c));
                    d = Mathf.Min(d, SignedDist(p, c, a));
                    float coverage = Mathf.Clamp01(d + 0.75f);
                    byte alpha = (byte)Mathf.RoundToInt(coverage * 255f);
                    pixels[y * s + x] = new Color32(255, 255, 255, alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false);
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s);
        }

        /// <summary>Positive INSIDE the edge a→b for a CCW-wound triangle, in pixels.</summary>
        private static float SignedDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var e = b - a;
            var w = p - a;
            float cross = e.x * w.y - e.y * w.x;
            return cross / e.magnitude;
        }

        // ------------------------------------------------------------ raw pixels

        private void Blit()
        {
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
        }

        private void Fill(Color32 color)
        {
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = color;
        }

        private void FillRect(Vector2 a, Vector2 b, Color32 color)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x)), 0, _texW - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, b.x)), 0, _texW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, b.y)), 0, _texH - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, b.y)), 0, _texH - 1);
            if (x1 < x0 || y1 < y0) return;

            float sa = color.a / 255f;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * _texW;
                for (int x = x0; x <= x1; x++)
                {
                    int index = row + x;
                    var dst = _pixels[index];
                    // alpha blend so fog darkens the sea instead of erasing it
                    _pixels[index] = new Color32(
                        (byte)Mathf.Clamp(color.r * sa + dst.r * (1f - sa), 0f, 255f),
                        (byte)Mathf.Clamp(color.g * sa + dst.g * (1f - sa), 0f, 255f),
                        (byte)Mathf.Clamp(color.b * sa + dst.b * (1f - sa), 0f, 255f),
                        255);
                }
            }
        }

        private void StrokeRect(Vector2 a, Vector2 b, Color32 color)
        {
            FillRect(new Vector2(a.x, a.y), new Vector2(b.x, a.y), color);
            FillRect(new Vector2(a.x, b.y), new Vector2(b.x, b.y), color);
            FillRect(new Vector2(a.x, a.y), new Vector2(a.x, b.y), color);
            FillRect(new Vector2(b.x, a.y), new Vector2(b.x, b.y), color);
        }

        private void UpdateScaleBar()
        {
            if (_scaleBar == null || _scaleLabel == null) return;

            // A round number close to a quarter of the visible span.
            float target = _viewRange * 0.25f;
            float pow = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(Mathf.Max(1f, target))));
            float[] nice = { 1f, 2f, 5f, 10f };
            float units = pow;
            for (int i = 0; i < nice.Length; i++)
            {
                if (pow * nice[i] >= target) { units = pow * nice[i]; break; }
                units = pow * 10f;
            }

            float px = units * CurrentScale();
            var rt = GameUi.Rect(_scaleBar.gameObject);
            rt.sizeDelta = new Vector2(Mathf.Clamp(px, 4f, _texW - 16f), 3f);
            _scaleLabel.text = units >= 1000f ? $"{units / 1000f:0.#}k 单位" : $"{units:0} 单位";
        }
    }
}
