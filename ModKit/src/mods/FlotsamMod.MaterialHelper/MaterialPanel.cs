using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamMods.MaterialHelper
{
    /// <summary>
    /// One material line: what a recipe (or a building under construction) needs, what the
    /// settlement already has, what is already on the way, and the two things the player can do
    /// about it — schedule it somewhere, or fly the camera there.
    /// </summary>
    public sealed class MaterialPanel
    {
        private const float RowHeight = 34f;

        private readonly MaterialHelperMod _mod;

        private GameObject _overlay;
        private UiWindow _window;
        private RectTransform _content;
        private ScrollRect _scroll;
        private TMP_Text _context;
        private TMP_Text _summary;
        private Button _fillButton;
        private Button _pinButton;
        private TMP_Text _pinLabel;

        private readonly List<GameObject> _rows = new List<GameObject>();
        private bool _manual;
        private string _manualTitle = "";
        private readonly List<Entry> _manualReqs = new List<Entry>();

        // Rebuilt once per refresh so a dozen rows do not each rescan every producer.
        private readonly Dictionary<ItemProperties, int> _incoming = new Dictionary<ItemProperties, int>();
        private readonly Dictionary<ItemProperties, List<GameProduction.Candidate>> _candidates =
            new Dictionary<ItemProperties, List<GameProduction.Candidate>>();

        private bool _visible;
        private bool _pinned;
        private float _nextRefresh;
        private object _lastContext;
        private int _lastRecipeIndex = -1;

        public bool Visible => _visible;
        public bool Pinned => _pinned;

        public MaterialPanel(MaterialHelperMod mod)
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
                _overlay = ui.CreateOverlay("materialhelper", 30700);

                _window = GameUi.Window(_overlay.transform, "材料助手", new Vector2(480f, 470f),
                                        new Vector2(-540f, 40f), config, "panel", Hide, 28f);

                _context = GameUi.Label(_window.Body.transform, "没有打开的建筑面板", 14,
                                        GameUi.TextColor, TextAnchor.UpperLeft, true);
                Place(_context, -0f, 40f, top: true);

                _fillButton = GameUi.TextButton(_window.Body.transform, "一键补齐", OnFillAll, 13);
                PlaceButton(_fillButton, 0f, 108f);

                _pinButton = GameUi.TextButton(_window.Body.transform, "钉住", TogglePin, 13);
                PlaceButton(_pinButton, 112f, 56f);
                _pinLabel = _pinButton.GetComponentInChildren<TMP_Text>();

                var refresh = GameUi.TextButton(_window.Body.transform, "刷新", () => Refresh(true), 13);
                PlaceButton(refresh, 172f, 56f);

                var scroll = GameUi.ScrollList(_window.Body.transform, "Materials", out _content);
                _scroll = scroll;
                var srt = GameUi.Rect(scroll.gameObject);
                srt.anchorMin = new Vector2(0f, 0f);
                srt.anchorMax = new Vector2(1f, 1f);
                srt.pivot = new Vector2(0.5f, 1f);
                srt.offsetMin = new Vector2(0f, 52f);
                srt.offsetMax = new Vector2(0f, -84f);

                _summary = GameUi.Label(_window.Body.transform, "", 12, GameUi.DimText, TextAnchor.MiddleLeft);
                Place(_summary, -30f, 18f, top: false);

                var hint = GameUi.Label(_window.Body.transform,
                                        $"左键点原生材料格＝补齐该材料 · 右键＝在本面板定位该材料\n" +
                                        $"{_mod.FillKey.Key} 一键补齐整条链 · {_mod.PanelKey.Key} 开关本面板 · 拖动标题栏移动，双击复位",
                                        11, GameUi.DimText, TextAnchor.UpperLeft, true, bold: false);
                Place(hint, 0f, 28f, top: false);

                _window.TipsHeight = 28f;
                _window.TipsChanged += show =>
                {
                    // Collapse the summary/list gap together with the tips so no band remains.
                    Place(_summary, show ? -30f : -2f, 18f, top: false);
                    var srt2 = GameUi.Rect(_scroll.gameObject);
                    srt2.offsetMin = new Vector2(0f, show ? 52f : 24f);
                };
                _window.SetTips(GameUi.Rect(hint.gameObject));
                _visible = _mod.ShowPanel;
                _window.Visible = _visible;
                Refresh(true);
            }
            catch (Exception e)
            {
                _mod.Context.Log.Error("material panel build failed: " + e.Message);
            }
        }

        public void Destroy()
        {
            ClearRows();
            if (_window != null) _window.Destroy();
            if (_overlay != null)
            {
                try { UnityEngine.Object.Destroy(_overlay); } catch { }
            }
            _window = null;
            _overlay = null;
            _content = null;
            _scroll = null;
            _context = null;
            _summary = null;
            _fillButton = null;
            _pinButton = null;
            _pinLabel = null;
            _lastContext = null;
        }

        public void SetVisible(bool value)
        {
            _visible = value;
            if (_window != null) _window.Visible = value;
            if (!value) _manual = false;   // next open follows the live building panel again
            if (value) Refresh(true);
        }

        public void Toggle() => SetVisible(!_visible);

        public void SetClickThrough(bool clickThrough)
        {
            if (_window != null) _window.SetClickThrough(clickThrough);
        }

        private void Hide()
        {
            SetVisible(false);
            _mod.ShowPanel = false;
            _mod.Context.Config.Set("showPanel", false);
            _mod.Context.Config.Save();
        }

        private void TogglePin()
        {
            _pinned = !_pinned;
            if (_pinLabel != null) _pinLabel.text = _pinned ? "已钉住" : "钉住";
        }

        private void Place(TMP_Text label, float y, float height, bool top)
        {
            var rt = GameUi.Rect(label.gameObject);
            float anchorY = top ? 1f : 0f;
            rt.anchorMin = new Vector2(0f, anchorY);
            rt.anchorMax = new Vector2(1f, anchorY);
            rt.pivot = new Vector2(0.5f, anchorY);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(-4f, height);
        }

        private void PlaceButton(Button btn, float x, float width)
        {
            var rt = GameUi.Rect(btn.gameObject);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -42f);
            rt.sizeDelta = new Vector2(width, 26f);
        }

        // ------------------------------------------------------------ refresh

        /// <summary>Called by the driver; throttled unless forced.</summary>
        public void Tick()
        {
            if (_window == null)
            {
                Build();
                return;
            }
            if (!_visible) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.35f;
            Refresh(false);
        }

        public void Refresh(bool force)
        {
            if (_window == null || !_visible) return;

            if (_manual)
            {
                if (force || !(_lastContext is string s) || s != _manualTitle)
                {
                    _lastContext = _manualTitle;
                    _lastRecipeIndex = -1;
                    RebuildManual();
                }
                else
                {
                    UpdateNumbers();
                }
                UpdateManualContext();
                return;
            }

            var producer = GameProduction.GetOpenProducer(out var openBuildable);
            var recipe = producer != null ? SafeSelectedRecipe(producer) : null;

            // Rebuild the rows only when the subject actually changed; otherwise just update the
            // numbers in place, so the list does not flicker four times a second.
            object context = recipe != null ? (object)recipe : openBuildable;
            int recipeIndex = recipe != null ? recipe.Index : -1;
            if (force || context != _lastContext || recipeIndex != _lastRecipeIndex)
            {
                _lastContext = context;
                _lastRecipeIndex = recipeIndex;
                Rebuild(producer, openBuildable, recipe);
            }
            else
            {
                UpdateNumbers();
            }

            UpdateContextText(producer, openBuildable, recipe);
        }

        /// <summary>
        /// Shows a one-shot requirement list captured from the construction tooltip (hold G and
        /// click the building you want to place), instead of following the open building panel.
        /// </summary>
        public void ShowRequirements(string title, List<KeyValuePair<ItemProperties, int>> reqs)
        {
            Build();
            _manual = true;
            _manualTitle = string.IsNullOrEmpty(title) ? "建造需求检测" : title;
            _manualReqs.Clear();
            if (reqs != null)
                foreach (var r in reqs)
                    _manualReqs.Add(new Entry { Item = r.Key, Need = Mathf.Max(1, r.Value), Multiplier = 1 });

            SetVisible(true);
            _mod.ShowPanel = true;
            Refresh(true);
        }

        private void RebuildManual()
        {
            ClearRows();
            if (_content == null) return;
            for (int i = 0; i < _manualReqs.Count; i++)
                _rows.Add(CreateRow(_manualReqs[i], i));
            UpdateSummary(_manualReqs);
        }

        private void UpdateManualContext()
        {
            if (_context == null) return;
            _context.text = _manualTitle + "\n行内可补齐/定位；按住 G 点建筑可重新检测";
            if (_fillButton != null) _fillButton.interactable = _manualReqs.Count > 0;
        }

        private void OnFillAll()
        {
            if (_manual)
            {
                for (int i = 0; i < _rowRefs.Count; i++) Fill(_rowRefs[i].Entry);
                return;
            }
            _mod.FillOpenProducer(false);
        }

        private static Producer.Recipe SafeSelectedRecipe(Producer producer)
        {
            try { return producer.SelectedRecipe; } catch { return null; }
        }

        private void UpdateContextText(Producer producer, Buildable openBuildable, Producer.Recipe recipe)
        {
            if (_context == null) return;

            if (openBuildable == null)
            {
                _context.text = "没有打开的建筑面板\n点选一座建筑后这里会列出它需要的材料";
                if (_fillButton != null) _fillButton.interactable = false;
                return;
            }

            string name = GameBuildings.NameOf(openBuildable);
            bool finished = GameBuildings.IsFinished(openBuildable);

            if (recipe != null)
            {
                string recipeName = "?";
                try { recipeName = recipe.Properties.LocalizedName.ToString(); } catch { }
                _context.text = $"{name}（工坊）\n当前配方：{recipeName}";
            }
            else if (!finished)
            {
                _context.text = $"{name}（建造中）\n建造所需材料：";
            }
            else
            {
                _context.text = $"{name}\n这座建筑没有生产配方";
            }
            if (_fillButton != null) _fillButton.interactable = recipe != null;
        }

        private void Rebuild(Producer producer, Buildable openBuildable, Producer.Recipe recipe)
        {
            ClearRows();
            if (_content == null) return;

            var entries = CollectEntries(recipe, openBuildable);
            BeginPass();
            for (int i = 0; i < entries.Count; i++)
                _rows.Add(CreateRow(entries[i], i));

            UpdateSummary(entries);
        }

        private List<Entry> CollectEntries(Producer.Recipe recipe, Buildable openBuildable)
        {
            var entries = new List<Entry>();

            if (recipe != null)
            {
                try
                {
                    var ingredients = recipe.Ingredients;
                    if (ingredients != null)
                        for (int i = 0; i < ingredients.Count; i++)
                        {
                            var ing = ingredients[i];
                            if (ing == null || ing.ItemProperties == null) continue;
                            entries.Add(new Entry
                            {
                                Item = ing.ItemProperties,
                                Need = ing.Amount,
                                Recipe = recipe,
                                Multiplier = Mathf.Max(1, recipe.IsContinuous ? 1 : recipe.AmountToProduce)
                            });
                        }
                }
                catch { }
                return entries;
            }

            if (openBuildable != null && !GameBuildings.IsFinished(openBuildable))
            {
                foreach (var req in GameProduction.ConstructionRequirements(openBuildable))
                    entries.Add(new Entry { Item = req.ItemProperties, Need = req.Amount, Multiplier = 1 });
            }
            return entries;
        }

        private struct Entry
        {
            public ItemProperties Item;
            public int Need;
            public Producer.Recipe Recipe;
            public int Multiplier;
        }

        private struct RowRefs
        {
            public Entry Entry;
            public TMP_Text Counts;
            public Button Fill;
            public Button Locate;
            public Image Icon;
            public Image Background;
        }

        private readonly List<RowRefs> _rowRefs = new List<RowRefs>();

        private GameObject CreateRow(Entry entry, int index)
        {
            var row = GameUi.Row(_content, RowHeight, index % 2 == 0 ? GameUi.RowBg : GameUi.RowBgAlt);

            var refs = new RowRefs { Entry = entry };
            refs.Background = row.GetComponent<Image>();

            refs.Icon = GameUi.Icon(row.transform, GameProduction.ItemIcon(entry.Item, entry.Recipe), 24f);
            var irt = GameUi.Rect(refs.Icon.gameObject);
            GameUi.Anchor(irt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(5f, 0f), new Vector2(24f, 24f));

            var name = GameUi.Label(row.transform, GameProduction.ItemName(entry.Item), 13,
                                    GameUi.TextColor, TextAnchor.MiddleLeft);
            var nrt = GameUi.Rect(name.gameObject);
            nrt.anchorMin = new Vector2(0f, 0f);
            nrt.anchorMax = new Vector2(0f, 1f);
            nrt.pivot = new Vector2(0f, 0.5f);
            nrt.anchoredPosition = new Vector2(34f, 0f);
            nrt.sizeDelta = new Vector2(116f, -4f);

            // Stretched with a hard right stop well clear of the buttons: a fixed-width label
            // overflowed into 补齐 and the two read as one overlapping control.
            refs.Counts = GameUi.Label(row.transform, "", 12, GameUi.DimText, TextAnchor.MiddleLeft);
            var crt = GameUi.Rect(refs.Counts.gameObject);
            crt.anchorMin = new Vector2(0f, 0f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0f, 0.5f);
            crt.offsetMin = new Vector2(154f, 2f);
            crt.offsetMax = new Vector2(-176f, -2f);

            refs.Locate = GameUi.TextButton(row.transform, "定位", () => Locate(refs.Entry), 12);
            PlaceRowButton(refs.Locate, -56f, 52f);

            refs.Fill = GameUi.TextButton(row.transform, "补齐", () => Fill(refs.Entry), 12);
            PlaceRowButton(refs.Fill, -112f, 52f);

            _rowRefs.Add(refs);
            UpdateRow(_rowRefs.Count - 1);
            return row;
        }

        private static void PlaceRowButton(Button btn, float x, float width)
        {
            var rt = GameUi.Rect(btn.gameObject);
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(width, 22f);
        }

        private void UpdateNumbers()
        {
            BeginPass();
            for (int i = 0; i < _rowRefs.Count; i++) UpdateRow(i);
        }

        private void BeginPass()
        {
            try
            {
                GameProduction.IncomingCounts(_incoming);
                GameProduction.CandidateSnapshot(_candidates);
            }
            catch { }
        }

        private void UpdateRow(int index)
        {
            if (index < 0 || index >= _rowRefs.Count) return;
            var refs = _rowRefs[index];
            var item = refs.Entry.Item;
            if (item == null) return;

            int need = refs.Entry.Need * Mathf.Max(1, refs.Entry.Multiplier);
            int have = GameProduction.HaveCount(item);
            int incoming = GameProduction.Incoming(_incoming, item);
            int shortfall = need - have - incoming;

            if (refs.Counts != null)
            {
                refs.Counts.text = $"库存 {have}/{need}" + (incoming > 0 ? $" · 在途 {incoming}" : "");
                refs.Counts.color = shortfall > 0 ? GameUi.Danger : GameUi.TextColor;
            }

            var candidates = GameProduction.Candidates(_candidates, item);
            bool canFill = false;
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].Enabled) { canFill = true; break; }

            if (refs.Fill != null) refs.Fill.interactable = shortfall > 0 && canFill;
            if (refs.Locate != null) refs.Locate.interactable = canFill;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        private void UpdateSummary(List<Entry> entries)
        {
            if (_summary == null) return;
            int shortCount = 0;
            foreach (var e in entries)
            {
                int need = e.Need * Mathf.Max(1, e.Multiplier);
                if (GameProduction.HaveCount(e.Item) + GameProduction.Incoming(_incoming, e.Item) < need) shortCount++;
            }
            _summary.text = entries.Count == 0
                ? "没有可显示的材料"
                : (shortCount == 0 ? $"共 {entries.Count} 种材料，全部齐备" : $"共 {entries.Count} 种材料，{shortCount} 种不足");
            _summary.color = shortCount == 0 ? GameUi.Good : GameUi.Danger;
        }

        private void ClearRows()
        {
            foreach (var row in _rows)
                if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();
            _rowRefs.Clear();
        }

        // ------------------------------------------------------------ actions

        /// <summary>Schedules exactly the shortfall of one material at the nearest able building.</summary>
        private void Fill(Entry entry)
        {
            var item = entry.Item;
            if (item == null) return;

            int need = entry.Need * Mathf.Max(1, entry.Multiplier);
            int shortfall = need - GameProduction.HaveCount(item) - GameProduction.IncomingCount(item);
            if (shortfall <= 0)
            {
                _mod.Toast($"{GameProduction.ItemName(item)} 已经足够", ToastKind.Info);
                return;
            }

            var candidates = GameProduction.FindProducersFor(item);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Enabled) continue;

                var recipe = GameProduction.RecipeProducing(candidates[i].Producer, item);
                int perBatch = GameProduction.ProducedPerBatch(recipe, item);
                int batches = Mathf.Max(1, Mathf.CeilToInt(shortfall / (float)Mathf.Max(1, perBatch)));

                int scheduled;
                string error;
                if (GameProduction.ScheduleBatches(candidates[i].Producer, item, batches, out scheduled, out error))
                {
                    _mod.Toast($"已安排 {GameProduction.ItemName(item)} x{scheduled * perBatch} @ " +
                               $"{GameBuildings.NameOf(candidates[i].Owner)}（还缺 {shortfall}）", ToastKind.Success);
                    // The batches we just asked for may need materials of their own.
                    if (recipe != null && _mod.ChainDepth > 0)
                    {
                        var report = new GameProduction.ChainReport();
                        GameProduction.FillChain(recipe, scheduled, _mod.ChainDepth, report);
                        if (!report.Empty) _mod.Toast("连带补齐：" + report.Summary() + "\n" + report.Detail(4), ToastKind.Info);
                    }
                }
                else
                {
                    _mod.Toast($"{GameBuildings.NameOf(candidates[i].Owner)}：{error}", ToastKind.Warning);
                }
                Refresh(true);
                return;
            }

            _mod.Toast($"没有已完工且启用的建筑能生产 {GameProduction.ItemName(item)}", ToastKind.Warning);
        }

        private void Locate(Entry entry)
        {
            var item = entry.Item;
            if (item == null) return;
            var candidates = GameProduction.FindProducersFor(item);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Enabled) continue;
                GameProduction.OpenPanel(candidates[i].Producer, _mod.Zoom);
                _mod.Toast($"已定位 {GameBuildings.NameOf(candidates[i].Owner)}（{candidates[i].Distance:0} 单位）", ToastKind.Info);
                return;
            }
            _mod.Toast($"没有建筑能生产 {GameProduction.ItemName(item)}", ToastKind.Warning);
        }

        /// <summary>Opens the panel on a specific material (native right-click entry point).</summary>
        public void FocusItem(ItemProperties item)
        {
            if (_window == null) Build();
            SetVisible(true);
            _mod.ShowPanel = true;
            Refresh(true);

            if (item == null || _scroll == null) return;
            for (int i = 0; i < _rowRefs.Count; i++)
            {
                if (_rowRefs[i].Entry.Item != item) continue;
                int count = Mathf.Max(1, _rowRefs.Count);
                _scroll.verticalNormalizedPosition = count <= 1 ? 1f : 1f - i / (float)(count - 1);
                return;
            }
        }
    }
}
