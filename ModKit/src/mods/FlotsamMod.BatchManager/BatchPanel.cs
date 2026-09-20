using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.BatchManager
{
    /// <summary>
    /// The batch window. Native skin throughout (UiWindow shell, native on/off icons as
    /// checkboxes, native locate icons, game's own building/category icons and TMP font).
    /// Layout: search + status + toolbar on top, type column left, instance list right,
    /// action bar and tips at the bottom (tips collapse via UiWindow.SetTips/TipsChanged).
    /// </summary>
    internal sealed class BatchPanel
    {
        private const float RowHeight = 30f;
        private const float TypeRowHeight = 28f;
        private const float LeftWidth = 236f;
        private const float TopBlock = 84f;                 // search + status + toolbar
        private const float ActionBarHeight = 36f;
        private const float TipsBlock = 48f;

        private readonly BatchManagerMod _mod;
        private readonly Action _requestClose;

        private UiWindow _window;
        private TMP_InputField _search;
        private TMP_Text _status;
        private RectTransform _leftContent;
        private RectTransform _rightContent;
        private RectTransform _leftArea;
        private RectTransform _rightArea;
        private TMP_Text _selCount;
        private TMP_Text _jumpInfo;
        private TMP_Text _selectAllText;
        private RectTransform _actionBar;
        private RectTransform _confirmBar;
        private TMP_Text _confirmLabel;
        private RectTransform _tips;

        private readonly List<TypeGroup> _groups = new List<TypeGroup>();
        private readonly List<GameObject> _leftRows = new List<GameObject>();
        private readonly List<GameObject> _rightRows = new List<GameObject>();
        private readonly List<Buildable> _rightItems = new List<Buildable>();
        private readonly Dictionary<Buildable, Image> _rowChecks = new Dictionary<Buildable, Image>();
        private readonly HashSet<Buildable> _checked = new HashSet<Buildable>();
        private readonly List<Buildable> _checkedOrder = new List<Buildable>();   // _checked in click order (jump list)
        private readonly List<Buildable> _jumpList = new List<Buildable>();
        private readonly List<PlaceableAlertProperties> _malfunctions = new List<PlaceableAlertProperties>();
        private readonly HashSet<BuildableCategory> _expanded = new HashSet<BuildableCategory>();   // 展开的分类（null = 未分类，引用比较）

        private TypeGroup _selected;                 // null == 「全部建筑」模式
        private int _totalBuildings;
        private string _query = "";
        private bool _dirty = true;
        private bool _visible;
        private float _nextHighlight;
        private int _jumpIndex;
        private bool _confirming;
        private float _confirmDeadline;
        private BatchOp _confirmOp;
        private string _confirmVerb = "";
        private UIState _stateBeforeTyping;
        private bool _typingPushed;

        public bool Visible => _visible;

        public BatchPanel(BatchManagerMod mod, Transform overlay, Action requestClose)
        {
            _mod = mod;
            _requestClose = requestClose;
            Build(overlay);
            _window.Visible = false;
        }

        // ------------------------------------------------------------ build

        private void Build(Transform overlay)
        {
            _window = GameUi.Window(overlay, "批量管理", new Vector2(780f, 680f), Vector2.zero,
                                    _mod.Cfg, "window", () => _requestClose(), 30f);
            var body = _window.Body.transform;

            _search = GameUi.SearchInput(body, "搜索建筑型号…", v => { _query = v ?? ""; if (_confirming) LeaveConfirm(); MarkDirty(); });
            TopStrip(GameUi.Rect(_search.gameObject), 0f, 28f);
            var relay = _search.gameObject.AddComponent<InputFocusRelay>();
            relay.Selected = PushTyping;
            relay.Deselected = PopTyping;

            _status = GameUi.Label(body, "等待游戏数据…", 13, GameUi.DimText, TextAnchor.MiddleLeft);
            TopStrip(GameUi.Rect(_status.gameObject), -30f, 20f);

            BuildToolbar(body);

            // columns
            _leftArea = GameUi.Rect(GameUi.Flat(body, "LeftCol", new Color(0f, 0f, 0f, 0.10f)));
            ColumnsRect(_leftArea, 0f, LeftWidth);
            var leftScroll = GameUi.ScrollList(_leftArea, "LeftList", out _leftContent);
            GameUi.Stretch(GameUi.Rect(leftScroll.gameObject), 2f, 2f, 2f, 2f);

            _rightArea = GameUi.Rect(GameUi.Flat(body, "RightCol", new Color(0f, 0f, 0f, 0.06f)));
            ColumnsRect(_rightArea, LeftWidth + 6f, -1f);
            var rightScroll = GameUi.ScrollList(_rightArea, "RightList", out _rightContent);
            GameUi.Stretch(GameUi.Rect(rightScroll.gameObject), 2f, 2f, 2f, 2f);

            BuildActionBar(body);
            BuildConfirmBar(body);
            BuildTips(body);

            _window.TipsHeight = TipsBlock;
            _window.TipsChanged = OnTipsChanged;
            _window.SetTips(_tips);
            OnTipsChanged(true);
        }

        private void TopStrip(RectTransform rt, float y, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(0f, h);
        }

        private void ColumnsRect(RectTransform rt, float left, float widthOrNegativeOne)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(left, BottomReserve());
            rt.offsetMax = new Vector2(widthOrNegativeOne < 0f ? 0f : widthOrNegativeOne - 0f, -TopBlock);
            if (widthOrNegativeOne >= 0f)
            {
                // fixed-width left column: anchor both edges to the left
                rt.anchorMax = new Vector2(0f, 1f);
                rt.offsetMax = new Vector2(widthOrNegativeOne, -TopBlock);
            }
        }

        private float BottomReserve() => ActionBarHeight + 6f;   // tips space is added in OnTipsChanged

        private void BuildToolbar(Transform body)
        {
            var bar = GameUi.Rect(GameUi.NewUi("Toolbar", body));
            TopStrip(bar, -52f, 28f);

            float x = 2f;
            var allChip = AddChip(bar, "全选", ref x, SelectAll, 84f);
            _selectAllText = allChip.GetComponentInChildren<TMP_Text>();
            AddChip(bar, "反选", ref x, InvertAll);
            AddChip(bar, "清空", ref x, ClearAll);

            _selCount = GameUi.Label(bar.transform, "已选 0 座", 14, GameUi.TextColor, TextAnchor.MiddleLeft);
            var srt = GameUi.Rect(_selCount.gameObject);
            GameUi.Anchor(srt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x + 10f, 0f), new Vector2(120f, 24f));

            var prev = GameUi.TextButton(bar.transform, "◀", () => Jump(-1), 14);
            GameUi.Anchor(GameUi.Rect(prev.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-118f, 0f), new Vector2(30f, 24f));
            _jumpInfo = GameUi.Label(bar.transform, "-/-", 13, GameUi.DimText, TextAnchor.MiddleCenter);
            GameUi.Anchor(GameUi.Rect(_jumpInfo.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-64f, 0f), new Vector2(56f, 24f));
            var next = GameUi.TextButton(bar.transform, "▶", () => Jump(1), 14);
            GameUi.Anchor(GameUi.Rect(next.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-32f, 0f), new Vector2(30f, 24f));
        }

        private Button AddChip(RectTransform bar, string label, ref float x, Action onClick, float width = 52f)
        {
            var chip = GameUi.Chip(bar.transform, label, false, onClick, 13);
            GameUi.Anchor(GameUi.Rect(chip.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                          new Vector2(x, 0f), new Vector2(width, 24f));
            x += width + 4f;
            return chip;
        }

        private void BuildActionBar(Transform body)
        {
            _actionBar = GameUi.Rect(GameUi.NewUi("ActionBar", body));
            AnchorBar(_actionBar);

            string[] labels = { "批量拆除", "取消拆除", "批量升级", "取消升级", "启用", "停用" };
            BatchOp[] ops =
            {
                BatchOp.Salvage, BatchOp.CancelSalvage, BatchOp.Upgrade,
                BatchOp.CancelUpgrade, BatchOp.Activate, BatchOp.Deactivate,
            };
            Color?[] tints =
            {
                new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.22f),
                null, null, null, null, null,
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var btn = GameUi.TextButton(_actionBar.transform, labels[i],
                                            () => Exec(ops[idx], labels[idx], ops[idx] == BatchOp.Salvage),
                                            14, tints[idx]);
                var rt = GameUi.Rect(btn.gameObject);
                rt.anchorMin = new Vector2(idx / 6f, 0f);
                rt.anchorMax = new Vector2((idx + 1) / 6f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(3f, 3f);
                rt.offsetMax = new Vector2(-3f, -3f);
            }
        }

        private void BuildConfirmBar(Transform body)
        {
            _confirmBar = GameUi.Rect(GameUi.Flat(body, "ConfirmBar",
                                                  new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.16f)));
            AnchorBar(_confirmBar);

            _confirmLabel = GameUi.Label(_confirmBar.transform, "", 14, GameUi.Danger, TextAnchor.MiddleLeft);
            GameUi.Stretch(GameUi.Rect(_confirmLabel.gameObject), 10f, 2f, 220f, 2f);

            var no = GameUi.TextButton(_confirmBar.transform, "取消", LeaveConfirm, 14);
            GameUi.Anchor(GameUi.Rect(no.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-8f, 0f), new Vector2(90f, 28f));
            var yes = GameUi.TextButton(_confirmBar.transform, "确认执行", ConfirmNow, 14,
                                        new Color(GameUi.Danger.r, GameUi.Danger.g, GameUi.Danger.b, 0.35f));
            GameUi.Anchor(GameUi.Rect(yes.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-104f, 0f), new Vector2(96f, 28f));

            _confirmBar.gameObject.SetActive(false);
        }

        private void AnchorBar(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(0f, ActionBarHeight);
        }

        private void BuildTips(Transform body)
        {
            _tips = GameUi.Rect(GameUi.Flat(body, "Tips", GameUi.SunkenBg));
            _tips.anchorMin = new Vector2(0f, 0f);
            _tips.anchorMax = new Vector2(1f, 0f);
            _tips.pivot = new Vector2(0.5f, 0f);
            _tips.sizeDelta = new Vector2(0f, TipsBlock);

            var text = GameUi.Label(_tips.transform,
                                    "勾选 = 世界内高亮描边（防选错）；◀▶ 在勾选建筑间逐个跳转核对。\n" +
                                    "批量拆除派小人执行、材料返还，可用「取消拆除」撤回；勾选数达到阈值会要求二次确认。",
                                    12, GameUi.DimText, TextAnchor.UpperLeft, wrap: true, bold: false);
            GameUi.Stretch(GameUi.Rect(text.gameObject), 10f, 5f, 10f, 5f);
        }

        private void OnTipsChanged(bool show)
        {
            if (_actionBar != null) _actionBar.anchoredPosition = new Vector2(0f, show ? TipsBlock + 4f : 4f);
            if (_confirmBar != null) _confirmBar.anchoredPosition = new Vector2(0f, show ? TipsBlock + 4f : 4f);
            if (_leftArea != null) _leftArea.offsetMin = new Vector2(_leftArea.offsetMin.x,
                                                                     ActionBarHeight + (show ? TipsBlock + 8f : 8f));
            if (_rightArea != null) _rightArea.offsetMin = new Vector2(_rightArea.offsetMin.x,
                                                                       ActionBarHeight + (show ? TipsBlock + 8f : 8f));
        }

        // ------------------------------------------------------------ lifecycle

        public void Show()
        {
            _visible = true;
            _window.Visible = true;
            _dirty = true;
        }

        public void Hide()
        {
            _visible = false;
            if (_window != null) _window.Visible = false;
            GameBatch.HighlightAll(_checked, false);
            LeaveConfirm();
            PopTyping();
        }

        public void Destroy()
        {
            PopTyping();
            GameBatch.HighlightAll(_checked, false);
            _checked.Clear();
            _checkedOrder.Clear();
            if (_window != null) { try { _window.Destroy(); } catch { } }
            _window = null;
        }

        public void MarkDirty() => _dirty = true;

        public void Tick()
        {
            if (_window == null) return;
            if (_dirty && _visible)
            {
                _dirty = false;
                Refresh();
            }
            if (_visible && _mod.HighlightChecked && Time.unscaledTime >= _nextHighlight)
            {
                _nextHighlight = Time.unscaledTime + 0.5f;
                GameBatch.HighlightAll(_checked, true);
            }
            if (_confirming && Time.unscaledTime > _confirmDeadline) LeaveConfirm();
        }

        // ------------------------------------------------------------ data & lists

        private void Refresh()
        {
            if (_window == null) return;
            PruneChecked();
            RebuildGroups();
            RebuildLeft();
            RebuildRight();
            UpdateToolbar();
        }

        private void PruneChecked()
        {
            List<Buildable> dead = null;
            foreach (var b in _checked)
                if (b == null) (dead ?? (dead = new List<Buildable>())).Add(b);
            if (dead != null)
                foreach (var b in dead) _checked.Remove(b);
            for (int i = _checkedOrder.Count - 1; i >= 0; i--)
                if (_checkedOrder[i] == null || !_checked.Contains(_checkedOrder[i]))
                    _checkedOrder.RemoveAt(i);
        }

        private void RebuildGroups()
        {
            var all = GameBuildings.All();
            if (!_mod.IncludeUnfinished) all.RemoveAll(b => !GameBuildings.IsFinished(b));
            _groups.Clear();
            _groups.AddRange(GameBatch.GroupByType(all));

            var cats = GameBuildings.Categories();
            _groups.Sort((x, y) =>
            {
                int cx = x.Category != null ? cats.IndexOf(x.Category) : int.MaxValue;
                int cy = y.Category != null ? cats.IndexOf(y.Category) : int.MaxValue;
                if (cx != cy) return cx.CompareTo(cy);
                return string.CompareOrdinal(x.Name, y.Name);
            });

            if (_mod.SortByDistance)
            {
                var th = GameWorld.TownheartPosition;
                foreach (var g in _groups)
                    g.Items.Sort((a, b) => DistTo(a, th).CompareTo(DistTo(b, th)));
            }

            if (_selected != null && !_groups.Contains(_selected)) _selected = null;
            _totalBuildings = 0;
            foreach (var g in _groups) _totalBuildings += g.Items.Count;
        }

        private static float DistTo(Buildable b, Vector3 th)
        {
            try
            {
                if (b == null) return float.MaxValue;
                var p = b.transform.position;
                float dx = p.x - th.x, dz = p.z - th.z;
                return dx * dx + dz * dz;
            }
            catch { return float.MaxValue; }
        }

        private List<TypeGroup> VisibleGroups()
        {
            var list = new List<TypeGroup>();
            string q = (_query ?? "").Trim().ToLowerInvariant();
            foreach (var g in _groups)
            {
                if (q.Length == 0
                    || (g.Name != null && g.Name.ToLowerInvariant().Contains(q))
                    || (g.Category != null && SafeCatName(g.Category).ToLowerInvariant().Contains(q)))
                    list.Add(g);
            }
            return list;
        }

        private static string SafeCatName(BuildableCategory c)
        {
            try { return c.Name.ToString(); } catch { return ""; }
        }

        private static Color SafeColor(BuildableCategory c, Color fallback)
        {
            try { return c.UIColor; } catch { return fallback; }
        }

        private static Sprite SafeIcon(BuildableCategory c)
        {
            try { return c.IconSprite; } catch { return null; }
        }

        private void RebuildLeft()
        {
            ClearRows(_leftRows);
            var visible = VisibleGroups();
            AddLeftAllRow();

            bool searching = (_query ?? "").Trim().Length > 0;
            BuildableCategory lastCat = null;
            bool first = true;
            foreach (var g in visible)
            {
                if (first || g.Category != lastCat)
                {
                    lastCat = g.Category;
                    AddLeftHeader(g.Category, visible, searching);
                }
                first = false;
                if (searching || _expanded.Contains(g.Category)) AddLeftRow(g);
            }
        }

        /// <summary>Header click toggles the category's type rows; only the left column is rebuilt
        /// (right list, checked set and jump list stay untouched).</summary>
        private void ToggleCategory(BuildableCategory cat)
        {
            if (!_expanded.Remove(cat)) _expanded.Add(cat);
            RebuildLeft();
        }

        /// <summary>Fixed top row: every visible type's instances, concatenated in left-column order.</summary>
        private void AddLeftAllRow()
        {
            bool selected = _selected == null;
            var row = GameUi.Row(_leftContent, TypeRowHeight, null);
            TintSelected(row, selected, GameUi.Accent);
            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => SelectGroup(null));

            var name = GameUi.Label(row.transform, "全部建筑", 13,
                                    selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            name.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(name.gameObject), 8f, 1f, 96f, 1f);

            var count = GameUi.Label(row.transform, "×" + _totalBuildings, 13,
                                     selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleRight);
            count.raycastTarget = false;
            GameUi.Anchor(GameUi.Rect(count.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-6f, 0f), new Vector2(44f, 20f));

            _leftRows.Add(row);
        }

        /// <summary>Row() keeps the native sliced sprite white, so a selected row is tinted by
        /// multiplying the Image colour (same trick as BuildingFinder's category buttons).</summary>
        private static void TintSelected(GameObject row, bool selected, Color c)
        {
            if (!selected) return;
            var rowImg = row.GetComponent<Image>();
            if (rowImg != null) rowImg.color = new Color(c.r * 0.55f + 0.45f, c.g * 0.55f + 0.45f, c.b * 0.55f + 0.45f, 1f);
        }

        private void AddLeftHeader(BuildableCategory cat, List<TypeGroup> visible, bool searching)
        {
            Color c = SafeColor(cat, GameUi.Accent);
            var header = GameUi.Row(_leftContent, 22f, new Color(c.r, c.g, c.b, 0.18f));
            var button = header.AddComponent<Button>();
            button.targetGraphic = header.GetComponent<Image>();
            button.onClick.AddListener(() => ToggleCategory(cat));

            int total = 0;
            foreach (var g in visible)
                if (g.Category == cat) total += g.Items.Count;

            float textLeft = 8f;
            var sprite = SafeIcon(cat);
            if (sprite != null)
            {
                var icon = GameUi.Icon(header.transform, sprite, 18f);
                GameUi.Anchor(GameUi.Rect(icon.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(6f, 0f), new Vector2(18f, 18f));
                textLeft = 28f;
            }

            bool expanded = searching || _expanded.Contains(cat);
            string name = SafeCatName(cat);
            if (name.Length == 0) name = "未分类";
            var label = GameUi.Label(header.transform,
                                     (expanded ? "▼ " : "▶ ") + name + " (" + total + ")",
                                     13, c, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), textLeft, 1f, 8f, 1f);
            _leftRows.Add(header);
        }

        private void AddLeftRow(TypeGroup g)
        {
            bool selected = g == _selected;
            var row = GameUi.Row(_leftContent, TypeRowHeight, null);
            TintSelected(row, selected, SafeColor(g.Category, GameUi.Accent));
            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => SelectGroup(g));

            float textLeft = 8f;
            if (g.Icon != null)
            {
                var icon = GameUi.Icon(row.transform, g.Icon, 18f);
                GameUi.Anchor(GameUi.Rect(icon.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(6f, 0f), new Vector2(18f, 18f));
                textLeft = 28f;
            }

            var name = GameUi.Label(row.transform, g.Name, 13,
                                    selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            name.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(name.gameObject), textLeft, 1f, 96f, 1f);

            var count = GameUi.Label(row.transform, "×" + g.Items.Count, 13,
                                     selected ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleRight);
            count.raycastTarget = false;
            GameUi.Anchor(GameUi.Rect(count.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-6f, 0f), new Vector2(44f, 20f));

            string badge = StatusBadge(g);
            if (badge.Length > 0)
            {
                var badgeLabel = GameUi.Label(row.transform, badge, 11, GameUi.DimText, TextAnchor.MiddleRight);
                badgeLabel.raycastTarget = false;
                GameUi.Anchor(GameUi.Rect(badgeLabel.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(-52f, 0f), new Vector2(86f, 20f));
            }

            _leftRows.Add(row);
        }

        private static string StatusBadge(TypeGroup g)
        {
            var parts = new List<string>();
            if (g.Upgradable > 0) parts.Add("可升" + g.Upgradable);
            if (g.Busy > 0) parts.Add("拆/升中" + g.Busy);
            if (g.Building > 0) parts.Add("建造中" + g.Building);
            if (g.Inactive > 0) parts.Add("停用" + g.Inactive);
            return string.Join(" ", parts);
        }

        private void SelectGroup(TypeGroup g)
        {
            if (_confirming) LeaveConfirm();
            _selected = g;
            RebuildLeft();
            RebuildRight();
            UpdateToolbar();
        }

        private void RebuildRight()
        {
            ClearRows(_rightRows);
            _rightItems.Clear();
            _rowChecks.Clear();
            if (_selected == null)
            {
                // 「全部建筑」：按左栏顺序拼接所有可见型号的实例，行名带型号前缀
                int total = 0;
                foreach (var g in VisibleGroups())
                    foreach (var b in g.Items)
                    {
                        if (b == null) continue;
                        total++;
                        if (_rightItems.Count >= _mod.MaxRows) continue;
                        _rightItems.Add(b);
                        AddRightRow(b, _rightItems.Count - 1, g.Name);
                    }
                if (total > _rightItems.Count) AddTruncatedRow(total);
                return;
            }

            int count = 0;
            for (int i = 0; i < _selected.Items.Count; i++)
            {
                var b = _selected.Items[i];
                if (b == null) continue;
                count++;
                if (_rightItems.Count >= _mod.MaxRows) continue;
                _rightItems.Add(b);
                AddRightRow(b, _rightItems.Count - 1, null);
            }
            if (count > _rightItems.Count) AddTruncatedRow(count);
        }

        /// <summary>Non-interactive hint row shown when the list was cut at maxRows.</summary>
        private void AddTruncatedRow(int total)
        {
            var row = GameUi.Row(_rightContent, RowHeight, GameUi.RowBgAlt);
            var label = GameUi.Label(row.transform, $"…… 已截断，共 {total} 座（可用搜索/选型号缩小范围）",
                                     12, GameUi.DimText, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 8f, 1f, 8f, 1f);
            _rightRows.Add(row);
        }

        private void AddRightRow(Buildable b, int index, string typeName)
        {
            var row = GameUi.Row(_rightContent, RowHeight, index % 2 == 0 ? GameUi.RowBg : GameUi.RowBgAlt);
            var button = row.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            button.onClick.AddListener(() => ToggleCheck(b));

            bool on = _checked.Contains(b);
            var check = GameUi.NewUi("Check", row.transform, typeof(Image));
            var cimg = check.GetComponent<Image>();
            cimg.sprite = on ? NativeSkin.CheckOnSprite : NativeSkin.CheckOffSprite;
            cimg.preserveAspect = true;
            cimg.raycastTarget = false;
            if (cimg.sprite == null) cimg.color = on ? GameUi.Good : GameUi.DimText;
            GameUi.Anchor(GameUi.Rect(check), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                          new Vector2(7f, 0f), new Vector2(20f, 20f));
            _rowChecks[b] = cimg;

            var icon = GameBuildings.IconOf(b);
            if (icon != null)
            {
                var ii = GameUi.Icon(row.transform, icon, 20f);
                GameUi.Anchor(GameUi.Rect(ii.gameObject), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(32f, 0f), new Vector2(20f, 20f));
            }

            bool finished = GameBuildings.IsFinished(b);
            string text = GameBuildings.NameOf(b) + StatusSuffix(b);
            if (typeName != null) text = typeName + " · " + text;
            var label = GameUi.Label(row.transform, text, 14,
                                     finished ? GameUi.TextColor : GameUi.DimText, TextAnchor.MiddleLeft);
            label.raycastTarget = false;
            GameUi.Stretch(GameUi.Rect(label.gameObject), 58f, 1f, 118f, 1f);

            _malfunctions.Clear();
            try
            {
                b.PopulateMalfunctions(_malfunctions,
                    finished ? PlaceableAlertProperties.AlertType.Minor
                             : PlaceableAlertProperties.AlertType.Major);
            }
            catch { }
            if (_malfunctions.Count > 0)
            {
                var warn = GameUi.Label(row.transform, "⚠ " + _malfunctions.Count, 13,
                                        new Color(0.96f, 0.78f, 0.32f), TextAnchor.MiddleRight);
                warn.raycastTarget = false;
                GameUi.Anchor(GameUi.Rect(warn.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              new Vector2(-34f, 0f), new Vector2(44f, 20f));
            }

            var locate = GameUi.IconButton(row.transform, NativeSkin.LocateSprite, () => Focus(b), 22f);
            GameUi.Anchor(GameUi.Rect(locate.gameObject), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(-6f, 0f), new Vector2(22f, 22f));

            _rightRows.Add(row);
        }

        private static string StatusSuffix(Buildable b)
        {
            try
            {
                if (GameBatch.IsSalvaging(b)) return "（拆除中）";
                if (GameBatch.IsUpgrading(b)) return "（升级中）";
                if (b.BuildPhase != BuildPhase.Finished) return "（建造中）";
                if (!b.IsActive) return "（停用）";
                return "";
            }
            catch { return ""; }
        }

        private void ClearRows(List<GameObject> rows)
        {
            foreach (var go in rows)
                if (go != null) UnityEngine.Object.Destroy(go);
            rows.Clear();
        }

        // ------------------------------------------------------------ check & jump

        private void ToggleCheck(Buildable b)
        {
            if (b == null) return;
            if (_confirming) LeaveConfirm();
            bool nowChecked;
            if (_checked.Contains(b)) { _checked.Remove(b); _checkedOrder.Remove(b); nowChecked = false; }
            else { _checked.Add(b); _checkedOrder.Add(b); nowChecked = true; }

            if (_mod.HighlightChecked) GameBatch.Highlight(b, nowChecked);
            if (_rowChecks.TryGetValue(b, out var img) && img != null)
            {
                var sprite = nowChecked ? NativeSkin.CheckOnSprite : NativeSkin.CheckOffSprite;
                if (sprite != null) { img.sprite = sprite; img.color = Color.white; }
                else img.color = nowChecked ? GameUi.Good : GameUi.DimText;
            }
            UpdateToolbar();
        }

        private void SelectAll()
        {
            if (_confirming) LeaveConfirm();
            if (_rightItems.Count == 0)
            {
                _mod.UiS.Toast("右栏没有可勾选的建筑（左栏点「全部建筑」，或展开分类选一个型号）", ToastKind.Warning);
                _mod.L.Info("selectall: right list empty");
                return;
            }
            int added = 0;
            foreach (var b in _rightItems)
                if (b != null && _checked.Add(b))
                {
                    _checkedOrder.Add(b);
                    added++;
                    if (_mod.HighlightChecked) GameBatch.Highlight(b, true);
                }
            RebuildRight();
            UpdateToolbar();
            _mod.L.Info($"selectall: right={_rightItems.Count} added={added} total={_checked.Count}");
        }

        private void InvertAll()
        {
            if (_confirming) LeaveConfirm();
            if (_rightItems.Count == 0)
            {
                _mod.UiS.Toast("右栏没有可勾选的建筑（左栏点「全部建筑」，或展开分类选一个型号）", ToastKind.Warning);
                _mod.L.Info("invert: right list empty");
                return;
            }
            foreach (var b in _rightItems)
            {
                if (b == null) continue;
                if (_checked.Contains(b))
                {
                    _checked.Remove(b);
                    _checkedOrder.Remove(b);
                    if (_mod.HighlightChecked) GameBatch.Highlight(b, false);
                }
                else
                {
                    _checked.Add(b);
                    _checkedOrder.Add(b);
                    if (_mod.HighlightChecked) GameBatch.Highlight(b, true);
                }
            }
            RebuildRight();
            UpdateToolbar();
            _mod.L.Info($"invert: right={_rightItems.Count} total={_checked.Count}");
        }

        private void ClearAll()
        {
            if (_confirming) LeaveConfirm();
            int was = _checked.Count;
            if (_mod.HighlightChecked) GameBatch.HighlightAll(_checked, false);
            _checked.Clear();
            _checkedOrder.Clear();
            RebuildRight();
            UpdateToolbar();
            _mod.L.Info("clear: was=" + was);
        }

        private void UpdateToolbar()
        {
            if (_selCount != null) _selCount.text = "已选 " + _checked.Count + " 座";
            if (_selectAllText != null) _selectAllText.text = _selected == null ? "全选" : "全选本型号";
            if (_status != null)
                _status.text = $"显示 {_rightItems.Count} / 共 {_totalBuildings} 座建筑 · {_groups.Count} 个型号";
            UpdateJumpInfo();
        }

        private void RebuildJumpList()
        {
            _jumpList.Clear();
            foreach (var b in _checkedOrder)
                if (b != null) _jumpList.Add(b);
            if (_jumpIndex >= _jumpList.Count) _jumpIndex = 0;
        }

        private void UpdateJumpInfo()
        {
            RebuildJumpList();
            if (_jumpInfo != null)
                _jumpInfo.text = _jumpList.Count > 0 ? (_jumpIndex + 1) + "/" + _jumpList.Count : "-/-";
        }

        private void Jump(int dir)
        {
            RebuildJumpList();
            if (_jumpList.Count == 0)
            {
                _mod.UiS.Toast("先勾选建筑，再用 ◀▶ 逐个跳转", ToastKind.Info);
                return;
            }
            _jumpIndex = Mathf.FloorToInt(Mathf.Repeat(_jumpIndex + dir, _jumpList.Count));
            Focus(_jumpList[_jumpIndex]);
            UpdateJumpInfo();
        }

        private void Focus(Buildable b)
        {
            if (b == null) return;
            if (!GameBuildings.Focus(b, _mod.Zoom))
            {
                _mod.UiS.Toast("无法定位该建筑（相机不可用）", ToastKind.Warning);
                return;
            }
            if (_mod.HideAfterFocus) _requestClose();
        }

        // ------------------------------------------------------------ batch actions

        private void Exec(BatchOp op, string verb, bool needConfirm)
        {
            if (_confirming && op != _confirmOp) LeaveConfirm();
            if (_checked.Count == 0)
            {
                _mod.UiS.Toast("先在右侧勾选建筑", ToastKind.Warning);
                return;
            }
            if (needConfirm && _checked.Count >= _mod.ConfirmThreshold)
            {
                EnterConfirm(op, verb);
                return;
            }
            DoRun(op, verb);
        }

        private void DoRun(BatchOp op, string verb)
        {
            var targets = new List<Buildable>(_checkedOrder);   // stable click order = jump/log order
            Action<string> log = _mod.Verbose ? (Action<string>)(m => _mod.L.Info(m)) : null;
            BatchOutcome oc;
            try { oc = GameBatch.Run(targets, op, log); }
            catch (Exception e) { _mod.L.Error("batch run failed", e); return; }

            _mod.UiS.Toast(oc.Summary(verb), oc.Ok > 0 ? ToastKind.Success : ToastKind.Warning);
            _mod.L.Info($"{verb}: ok={oc.Ok} skip={oc.TotalSkips()} [{oc.SkipText()}]");
            MarkDirty();
        }

        private void EnterConfirm(BatchOp op, string verb)
        {
            _confirming = true;
            _confirmOp = op;
            _confirmVerb = verb;
            _confirmDeadline = Time.unscaledTime + 10f;
            string typeName = _selected != null ? _selected.Name : "全部建筑";
            _confirmLabel.text = $"确认对 {_checked.Count} 座「{typeName}」执行{verb}？";
            if (_actionBar != null) _actionBar.gameObject.SetActive(false);
            if (_confirmBar != null) _confirmBar.gameObject.SetActive(true);
        }

        private void ConfirmNow()
        {
            var op = _confirmOp;
            var verb = _confirmVerb;
            LeaveConfirm();
            DoRun(op, verb);
        }

        private void LeaveConfirm()
        {
            _confirming = false;
            if (_confirmBar != null) _confirmBar.gameObject.SetActive(false);
            if (_actionBar != null) _actionBar.gameObject.SetActive(true);
        }

        // ------------------------------------------------------------ typing state

        private void PushTyping()
        {
            try
            {
                if (_typingPushed) return;
                _stateBeforeTyping = UIManager.State;
                UIManager.SetState(UIState.Typing);
                _typingPushed = true;
            }
            catch { }
        }

        private void PopTyping()
        {
            try
            {
                if (!_typingPushed) return;
                // Only restore while the game is still in Typing: if something else changed the
                // state in the meantime, writing back the stale value would clobber it.
                if (UIManager.State == UIState.Typing) UIManager.SetState(_stateBeforeTyping);
                _typingPushed = false;
            }
            catch { }
        }
    }
}
