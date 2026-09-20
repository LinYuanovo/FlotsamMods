using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.BuildingFinder
{
    /// <summary>
    /// Always-available building browser that mirrors the game's own category presentation.
    ///
    /// Fixed vs v1: the category list used to be built in OnEnable, which runs at BepInEx
    /// startup — long before any scene exists — so GameManager.Settings was null and the
    /// category list came back EMPTY (everything appeared in one flat list). Categories and
    /// their native icons/colours are now (re)built lazily once the game settings exist.
    ///
    /// The vanilla list (BuildableOverview) only appears after clicking a building because
    /// it reads the community through the selected Buildable; the community itself is static
    /// (Community.PlayerCommunity), so this mod can list everything without a selection.
    ///
    /// UI: native-skinned window (harvested panel/button sprites and the game's own TMP font),
    /// draggable by its title bar with the position remembered, double-click the title bar to
    /// reset it.
    /// </summary>
    public sealed class BuildingFinderMod : FlotsamModBase
    {
        private const float RowHeight = 30f;
        private const float CatRowHeight = 28f;
        private const float CatColumnWidth = 176f;

        private GameObject _overlay;
        private UiWindow _window;
        private RectTransform _content;
        private RectTransform _categoryColumn;
        private TMP_InputField _search;
        private TMP_Text _status;
        private IKeybind _hotkey;
        private IHudButton _button;

        private readonly List<BuildableCategory> _categories = new List<BuildableCategory>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private readonly List<GameObject> _categoryButtons = new List<GameObject>();
        private readonly List<PlaceableAlertProperties> _malfunctionScratch = new List<PlaceableAlertProperties>();

        private BuildableCategory _selectedCategory;   // null == 全部
        private string _query = "";
        private bool _visible;
        private bool _categoriesBuilt;
        private float _zoom = 0.6f;
        private bool _hideAfterFocus = true;
        private bool _includeUnfinished = true;
        private bool _groupAllByCategory = true;
        private UIState _stateBeforeTyping;
        private bool _typingStatePushed;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _hotkey = Keybinds.Register("finder.toggle", KeyCode.B, "打开建筑总览");
            _zoom = Mathf.Clamp(Config.Get("zoomLevel", 0.6f), 0.05f, 3f);
            _hideAfterFocus = Config.Get("hideAfterFocus", true);
            _includeUnfinished = Config.Get("includeUnfinished", true);
            _groupAllByCategory = Config.Get("groupAllByCategory", true);
        }

        public override void OnEnable()
        {
            MigrateConfig();

            // The window is built lazily on the first tick inside a save: the harvested native
            // sprites and the game's TMP font only exist once the gameplay scene is up.
            Events.On("BuildableBuilt", _ => MarkDirty());
            Events.On("BuildablePlaced", _ => MarkDirty());
            Events.On("BuildableSalvaged", _ => MarkDirty());

            _button = Ui.AddHudButton("buildingfinder.open", "建筑总览", Toggle, HudAnchor.RightMiddle,
                                      Config, "button");
            _button.Visible = true;
            _button.SetIcon(NativeSkin.Find("build", "hammer", "construction"));

            Log.Info("building finder ready (categories are built once game settings are available)");
        }

        /// <summary>Moves the old buttonX/buttonY and windowX/windowY keys to the shared naming.</summary>
        private void MigrateConfig()
        {
            if (Config.Get("schema", 0) >= 2) return;
            if (!Config.Has("button.x") && Config.Has("buttonX"))
            {
                Config.Set("button.x", Config.Get("buttonX", float.NaN));
                Config.Set("button.y", Config.Get("buttonY", float.NaN));
            }
            Config.Set("schema", 2);
            Config.Save();
        }

        public override void OnDisable()
        {
            try { _button?.Destroy(); } catch { }
            _button = null;
            TeardownUi();
            Log.Info("building finder removed");
        }

        /// <summary>
        /// Drops the window. The harvested sprites belong to the scene that is going away, so it
        /// is rebuilt (and re-skinned) on the next game start.
        /// </summary>
        private void TeardownUi()
        {
            if (_typingStatePushed) PopTypingState();
            if (_window != null) _window.Destroy();
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
                _overlay = null;
            }
            _window = null;
            _content = null;
            _categoryColumn = null;
            _search = null;
            _status = null;
            _rows.Clear();
            _categoryButtons.Clear();
            _categories.Clear();
            _categoriesBuilt = false;
            _visible = false;
        }

        public override void OnGameEnd()
        {
            TeardownUi();
        }

        private void EnsureUi()
        {
            if (_window != null || !GameApi.IsPlaying) return;
            BuildUi();
            Log.Info("finder window built; skin " + (NativeSkin.Available ? "native" : "procedural"));
        }

        public override void OnGameStart()
        {
            // Game settings only exist once the gameplay scene is up.
            EnsureCategories(true);
            Ui.Toast($"建筑总览就绪：{_hotkey.Key} 开关，可拖动标题栏并自动记住位置", ToastKind.Success);
        }

        /// <summary>A mod class is not a MonoBehaviour, so the host pumps this once per frame.</summary>
        public override void OnTick()
        {
            EnsureUi();
            if (_hotkey != null && _hotkey.IsDown) Toggle();
        }

        private void MarkDirty()
        {
            if (_visible) Refresh();
        }

        // ------------------------------------------------------------ ui shell

        private void BuildUi()
        {
            NativeSkin.Harvest();

            _overlay = Ui.CreateOverlay("buildingfinder", 30500);

            _window = GameUi.Window(_overlay.transform, "建筑总览", new Vector2(680f, 680f),
                                    new Vector2(0f, 0f), Config, "window", Hide, 30f);

            _search = GameUi.SearchInput(_window.Body.transform, "搜索建筑名称 / 分类…", OnSearchChanged);
            var srt = GameUi.Rect(_search.gameObject);
            srt.anchorMin = new Vector2(0f, 1f);
            srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.anchoredPosition = new Vector2(0f, 0f);
            srt.sizeDelta = new Vector2(0f, 28f);
            var relay = _search.gameObject.AddComponent<InputFocusRelay>();
            relay.Selected = PushTypingState;
            relay.Deselected = PopTypingState;

            _status = GameUi.Label(_window.Body.transform, "等待游戏数据…", 13, GameUi.DimText, TextAnchor.MiddleLeft);
            var strt = GameUi.Rect(_status.gameObject);
            strt.anchorMin = new Vector2(0f, 1f);
            strt.anchorMax = new Vector2(1f, 1f);
            strt.pivot = new Vector2(0.5f, 1f);
            strt.anchoredPosition = new Vector2(0f, -32f);
            strt.sizeDelta = new Vector2(0f, 20f);

            var catBg = GameUi.Flat(_window.Body.transform, "Categories", new Color(0f, 0f, 0f, 0.28f));
            _categoryColumn = GameUi.Rect(catBg);
            _categoryColumn.anchorMin = new Vector2(0f, 0f);
            _categoryColumn.anchorMax = new Vector2(0f, 1f);
            _categoryColumn.pivot = new Vector2(0f, 1f);
            _categoryColumn.anchoredPosition = new Vector2(0f, -56f);
            _categoryColumn.sizeDelta = new Vector2(CatColumnWidth, -56f);

            var scroll = GameUi.ScrollList(_window.Body.transform, "List", out _content);
            var lrt = GameUi.Rect(scroll.gameObject);
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.offsetMin = new Vector2(CatColumnWidth + 6f, 0f);
            lrt.offsetMax = new Vector2(0f, -56f);

            _window.Visible = false;
        }

        private void Toggle()
        {
            EnsureUi();
            if (_window == null) return;
            _visible = !_visible;
            if (_window != null) _window.Visible = _visible;
            if (_visible)
            {
                EnsureCategories(false);
                Refresh();
            }
            else if (_typingStatePushed) PopTypingState();
        }

        private void Hide()
        {
            _visible = false;
            if (_window != null) _window.Visible = false;
            if (_typingStatePushed) PopTypingState();
        }

        // ------------------------------------------------------------ categories

        /// <summary>
        /// The category list can only be read once the game settings exist, so this runs on
        /// first open and on every GameStart rather than at mod-enable time.
        /// </summary>
        private void EnsureCategories(bool reforce)
        {
            if (_categoryColumn == null) return;
            if (_categoriesBuilt && !reforce) return;

            var cats = GameBuildings.Categories();
            if (cats.Count == 0)
            {
                if (_status != null) _status.text = "等待游戏数据…（进入存档后可用）";
                return;
            }

            _categories.Clear();
            _categories.AddRange(cats);

            var lastName = Config.Get("lastCategory", "");
            _selectedCategory = null;
            if (!string.IsNullOrEmpty(lastName))
            {
                foreach (var c in _categories)
                    if (SafeName(c) == lastName) { _selectedCategory = c; break; }
            }

            RebuildCategoryButtons();
            _categoriesBuilt = true;
            Log.Info($"categories loaded: {_categories.Count}");
        }

        private static string SafeName(BuildableCategory category)
        {
            try { return category.Name.ToString(); } catch { return "?"; }
        }

        private static Color SafeColor(BuildableCategory category, Color fallback)
        {
            try { return category.UIColor; } catch { return fallback; }
        }

        private static Sprite SafeIcon(BuildableCategory category)
        {
            try { return category.IconSprite; } catch { return null; }
        }

        private void RebuildCategoryButtons()
        {
            foreach (var go in _categoryButtons)
                if (go != null) UnityEngine.Object.Destroy(go);
            _categoryButtons.Clear();

            float y = 4f;
            MakeCategoryButton(null, "全部建筑", y);
            y += CatRowHeight;

            foreach (var category in _categories)
            {
                int count = GameBuildings.InCategory(category).Count;
                MakeCategoryButton(category, $"{SafeName(category)}  ({count})", y);
                y += CatRowHeight;
            }
        }

        private void MakeCategoryButton(BuildableCategory category, string label, float y)
        {
            bool selected = category == _selectedCategory;
            var baseColor = category != null ? SafeColor(category, GameUi.Accent) : GameUi.Accent;

            // A selected row is tinted with the category's own colour; an unselected one keeps
            // the native button chrome so the list still looks like the game's.
            Color? tint = selected
                ? new Color(baseColor.r * 0.55f, baseColor.g * 0.55f, baseColor.b * 0.55f, 0.95f)
                : (Color?)null;

            var btn = GameUi.TextButton(_categoryColumn, "", () => SelectCategory(category), 14, tint);
            var rt = GameUi.Rect(btn.gameObject);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -y);
            rt.sizeDelta = new Vector2(-8f, CatRowHeight - 2f);

            var sprite = category != null ? SafeIcon(category) : null;
            float textLeft = 8f;
            if (sprite != null)
            {
                var icon = GameUi.NewUi("Icon", btn.transform, typeof(Image));
                var irt = GameUi.Rect(icon);
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.pivot = new Vector2(0f, 0.5f);
                irt.anchoredPosition = new Vector2(6f, 0f);
                irt.sizeDelta = new Vector2(18f, 18f);
                var img = icon.GetComponent<Image>();
                img.sprite = sprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                textLeft = 28f;
            }

            var text = GameUi.Label(btn.transform, label, 14,
                                    selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            text.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(text.gameObject), textLeft, 1f, 4f, 1f);

            _categoryButtons.Add(btn.gameObject);
        }

        private void SelectCategory(BuildableCategory category)
        {
            _selectedCategory = category;
            Config.Set("lastCategory", category != null ? SafeName(category) : "");
            Config.Save();
            RebuildCategoryButtons();
            Refresh();
        }

        private void OnSearchChanged(string value)
        {
            _query = value;
            Refresh();
        }

        private void PushTypingState()
        {
            try
            {
                if (_typingStatePushed) return;
                _stateBeforeTyping = UIManager.State;
                UIManager.SetState(UIState.Typing);
                _typingStatePushed = true;
            }
            catch { }
        }

        private void PopTypingState()
        {
            try
            {
                if (!_typingStatePushed) return;
                UIManager.SetState(_stateBeforeTyping);
                _typingStatePushed = false;
            }
            catch { }
        }

        // ------------------------------------------------------------ list

        private void Refresh()
        {
            if (_content == null) return;
            if (!_categoriesBuilt) EnsureCategories(false);
            ClearRows();

            var all = GameBuildings.All();
            if (!_includeUnfinished)
                all.RemoveAll(b => !GameBuildings.IsFinished(b));
            int total = all.Count;

            if (_selectedCategory != null)
            {
                var list = GameBuildings.InCategory(_selectedCategory);
                if (!_includeUnfinished) list.RemoveAll(b => !GameBuildings.IsFinished(b));
                ApplyQuery(list);
                GameBuildings.Sort(list);
                AddRows(list);
                SetStatus(list.Count, total, SafeName(_selectedCategory));
            }
            else if (_groupAllByCategory && string.IsNullOrEmpty(_query) && _categories.Count > 0)
            {
                // Grouped view keeps the "全部" list readable: a coloured header per
                // category, in the game's own category order.
                int shown = 0;
                foreach (var category in _categories)
                {
                    var list = GameBuildings.InCategory(category);
                    if (!_includeUnfinished) list.RemoveAll(b => !GameBuildings.IsFinished(b));
                    if (list.Count == 0) continue;
                    GameBuildings.Sort(list);
                    AddHeader($"{SafeName(category)}  ({list.Count})", SafeColor(category, GameUi.Accent));
                    AddRows(list);
                    shown += list.Count;
                }
                SetStatus(shown, total, "全部（按分类）");
            }
            else
            {
                ApplyQuery(all);
                GameBuildings.Sort(all);
                AddRows(all);
                SetStatus(all.Count, total, string.IsNullOrEmpty(_query) ? "全部" : $"搜索“{_query}”");
            }
        }

        private void ApplyQuery(List<Buildable> list)
        {
            if (string.IsNullOrEmpty(_query)) return;
            list.RemoveAll(b => !GameBuildings.Matches(b, _query));
        }

        private void SetStatus(int shown, int total, string scope)
        {
            if (_status != null) _status.text = $"{scope}：显示 {shown} / 共 {total} 座建筑";
        }

        private void AddRows(List<Buildable> list)
        {
            int max = Mathf.Max(1, Config.Get("maxRows", 400));
            for (int i = 0; i < list.Count && i < max; i++)
                _rows.Add(CreateRow(list[i], i));
        }

        private void ClearRows()
        {
            foreach (var row in _rows)
                if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();
        }

        private void AddHeader(string text, Color color)
        {
            var header = GameUi.Row(_content, 24f, new Color(color.r, color.g, color.b, 0.22f));
            var label = GameUi.Label(header.transform, text, 14, color, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 8f, 1f, 8f, 1f);
            _rows.Add(header);
        }

        private GameObject CreateRow(Buildable buildable, int index)
        {
            var row = GameUi.Row(_content, RowHeight, index % 2 == 0 ? GameUi.RowBg : GameUi.RowBgAlt);

            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => Focus(buildable));

            var icon = GameUi.NewUi("Icon", row.transform, typeof(Image));
            var iconRt = GameUi.Rect(icon);
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.anchoredPosition = new Vector2(6f, 0f);
            iconRt.sizeDelta = new Vector2(22f, 22f);
            var iconImage = icon.GetComponent<Image>();
            iconImage.sprite = GameBuildings.IconOf(buildable);
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            bool finished = GameBuildings.IsFinished(buildable);
            string name = GameBuildings.NameOf(buildable);

            var label = GameUi.Label(row.transform,
                                     finished ? name : name + "  (建造中)",
                                     15, finished ? GameUi.TextColor : GameUi.DimText,
                                     TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 34f, 1f, 90f, 1f);

            // Same malfunction call the vanilla row uses (BuildableOverviewListItem, 67735).
            _malfunctionScratch.Clear();
            try
            {
                buildable.PopulateMalfunctions(_malfunctionScratch,
                    finished ? PlaceableAlertProperties.AlertType.Minor
                             : PlaceableAlertProperties.AlertType.Major);
            }
            catch { }

            if (_malfunctionScratch.Count > 0)
            {
                var warn = GameUi.Label(row.transform, $"⚠ {_malfunctionScratch.Count}", 13,
                                        new Color(0.96f, 0.78f, 0.32f), TextAnchor.MiddleRight);
                warn.raycastTarget = false;
                var wrt = GameUi.Rect(warn.gameObject);
                wrt.anchorMin = wrt.anchorMax = new Vector2(1f, 0.5f);
                wrt.pivot = new Vector2(1f, 0.5f);
                wrt.anchoredPosition = new Vector2(-8f, 0f);
                wrt.sizeDelta = new Vector2(60f, 20f);
            }

            return row;
        }

        private void Focus(Buildable buildable)
        {
            if (buildable == null) return;
            if (!GameBuildings.Focus(buildable, _zoom))
                Ui.Toast("无法定位该建筑（相机不可用）", ToastKind.Warning);
            if (_hideAfterFocus) Hide();
        }
    }
}
