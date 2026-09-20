using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// The game's own UI art, harvested at runtime from the live UI hierarchy.
    ///
    /// Flotsam's chrome is a light theme: white 9-sliced rounded panels with a dark rounded
    /// border overlay, a mint title-bar cap, white buttons with dark borders, and red icon-only
    /// close buttons. The names below were read out of the shipped asset bundles, so the table is
    /// exact; each entry still falls back to a substring search and finally to a procedural
    /// colour, so a game update that renames a sprite degrades instead of breaking.
    ///
    /// Everything is optional: every getter has a fallback in GameUi.
    /// </summary>
    public static class NativeSkin
    {
        /// <summary>Set true to write the full harvested sprite-name list to the log once.</summary>
        public static bool VerboseDump = false;

        /// <summary>Where the full name list is written so it can be inspected without a log flood.</summary>
        public static string DumpPath = null;

        private const int MaxSprites = 6000;

        private static bool _attempted;
        private static bool _ok;
        private static string _report = "(not harvested)";

        private static readonly Dictionary<string, Sprite> _sprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        // Exact names, verified against the shipped bundles.
        private static readonly string[] PanelBodyNames =
        {
            "UI_Panel_Generic_Rounded_32x32",
            "UI_Panel_Generic_Bordered_Rounded_32x32"
        };

        private static readonly string[] PanelBorderNames =
        {
            "UI_Panel_Generic_Border_Rounded_32x32",
            "UI_Panel_Generic_Border_Rounded_Thick_32x32"
        };

        private static readonly string[] PanelTopNames =
        {
            "UI_Panel_Generic_Rounded_Top_32x16",
            "UI_Panel_Generic_Bordered_Rounded_Top_32x16"
        };

        private static readonly string[] ButtonBgNames =
        {
            "UI_Button_Generic_Medium_Background_64x64",
            "UI_Panel_Generic_Rounded_32x32"
        };

        private static readonly string[] ButtonPressedNames =
        {
            "UI_Button_Generic_Medium_Background_pressed_64x64"
        };

        private static readonly string[] ButtonBorderNames =
        {
            "UI_Button_Generic_Medium_Border_64x64",
            "UI_Panel_Generic_Border_Rounded_32x32"
        };

        private static readonly string[] CloseNames =
        {
            "UI_Icon_InfoPanel_Close_64x64",
            "UI_Close"
        };

        private static readonly string[] CloseOffNames = { "UI_Icon_InfoPanel_Close_off_64x64" };
        private static readonly string[] DragNames = { "UI_Icon_InfoPanel_Drag_64x64" };
        private static readonly string[] LocateNames = { "UI_Icon_InfoPanel_Locate_64x64" };
        private static readonly string[] PlusNames = { "UI_Icon_InfoPanel_Plus_64x64" };
        private static readonly string[] MinusNames = { "UI_Icon_InfoPanel_Minus_64x64" };
        private static readonly string[] SlotNames =
        {
            "UI_Background_Generic_SoftSquare_84x84",
            "UI_Bar_Rounded_80x80"
        };
        private static readonly string[] RowSelectedNames =
        {
            "UI_Panel_Tab_Horizontal_Selected_Background_64x64"
        };
        private static readonly string[] CheckOnNames = { "UI_Icon_InfoPanel_On_64x64", "Icon_Checkbox_Check01" };
        private static readonly string[] CheckOffNames = { "UI_Icon_InfoPanel_Off_64x64" };

        public static TMP_FontAsset Font { get; private set; }
        public static TMP_FontAsset FontBold { get; private set; }
        public static Material FontMaterial { get; private set; }
        public static string FontReport { get; private set; } = "";

        public static Sprite PanelSprite { get; private set; }
        public static Sprite PanelBorderSprite { get; private set; }
        public static Sprite HeaderSprite { get; private set; }
        public static Sprite ButtonSprite { get; private set; }
        public static Sprite ButtonPressedSprite { get; private set; }
        public static Sprite ButtonBorderSprite { get; private set; }
        public static Sprite SlotSprite { get; private set; }
        public static Sprite RowSprite { get; private set; }
        public static Sprite CloseSprite { get; private set; }
        public static Sprite CloseOffSprite { get; private set; }
        public static Sprite DragSprite { get; private set; }
        public static Sprite LocateSprite { get; private set; }
        public static Sprite PlusSprite { get; private set; }
        public static Sprite MinusSprite { get; private set; }
        public static Sprite CheckOnSprite { get; private set; }
        public static Sprite CheckOffSprite { get; private set; }

        // Light-theme tints sampled from the shipped UI (pure white body, near-black plum
        // border, mint title bar, dark teal text) so mod panels read at the same contrast as
        // the game's own tooltips.
        public static Color PanelTint { get; private set; } = new Color(1f, 1f, 1f, 1f);
        public static Color HeaderTint { get; private set; } = new Color(0.55f, 0.88f, 0.75f, 1f);
        public static Color ButtonTint { get; private set; } = new Color(1f, 1f, 1f, 1f);
        public static Color Text { get; private set; } = new Color(0.15f, 0.21f, 0.19f, 1f);
        public static Color DimText { get; private set; } = new Color(0.40f, 0.47f, 0.45f, 1f);
        public static Color Accent { get; private set; } = new Color(0.16f, 0.55f, 0.42f, 1f);
        public static Color Danger { get; private set; } = new Color(0.87f, 0.30f, 0.26f, 1f);
        public static Color Good { get; private set; } = new Color(0.20f, 0.62f, 0.30f, 1f);

        public static bool Available => _ok;
        public static string Report => _report;
        public static bool Attempted => _attempted;

        // ------------------------------------------------------------ harvest

        /// <summary>Idempotent. Safe to call before the game is up; it simply retries later.</summary>
        public static bool Harvest(bool force = false)
        {
            if (_attempted && !force) return _ok;
            if (!GameApi.IsPlaying && !force) return false;

            _attempted = true;
            try
            {
                _ok = HarvestInternal();
            }
            catch (Exception e)
            {
                _ok = false;
                _report = "harvest threw: " + e.Message;
            }
            return _ok;
        }

        private static bool HarvestInternal()
        {
            var root = GameUiRoot();
            if (root == null)
            {
                _report = "no game UI root found";
                return false;
            }

            var images = root.GetComponentsInChildren<Image>(true);
            var texts = root.GetComponentsInChildren<TMP_Text>(true);

            HarvestSprites(images);
            HarvestFont(texts);

            PanelSprite = Pick(PanelBodyNames, "panel_generic_rounded");
            PanelBorderSprite = Pick(PanelBorderNames, "panel_generic_border");
            HeaderSprite = Pick(PanelTopNames, "panel_rounded_top", "titlebar");
            ButtonSprite = Pick(ButtonBgNames, "button_generic_medium_background");
            ButtonPressedSprite = Pick(ButtonPressedNames, "button_generic_medium_background_pressed");
            ButtonBorderSprite = Pick(ButtonBorderNames, "button_generic_medium_border");
            CloseSprite = Pick(CloseNames, "infopanel_close");
            CloseOffSprite = Pick(CloseOffNames, null);
            DragSprite = Pick(DragNames, "infopanel_drag");
            LocateSprite = Pick(LocateNames, "infopanel_locate");
            PlusSprite = Pick(PlusNames, "infopanel_plus");
            MinusSprite = Pick(MinusNames, "infopanel_minus");
            SlotSprite = Pick(SlotNames, "background_generic_softsquare");
            RowSprite = Pick(RowSelectedNames, null);
            CheckOnSprite = Pick(CheckOnNames, null);
            CheckOffSprite = Pick(CheckOffNames, null);

            var sb = new StringBuilder();
            sb.Append("native skin: ").Append(_sprites.Count).Append(" sprites, font=")
              .Append(Font != null ? Font.name : "(none)")
              .Append(" / bold=").Append(FontBold != null ? FontBold.name : "(none)")
              .Append(" | fonts: ").Append(FontReport)
              .Append(", panel=").Append(Name(PanelSprite))
              .Append(", border=").Append(Name(PanelBorderSprite))
              .Append(", header=").Append(Name(HeaderSprite))
              .Append(", button=").Append(Name(ButtonSprite))
              .Append(", close=").Append(Name(CloseSprite))
              .Append(", slot=").Append(Name(SlotSprite))
              .Append(" | scanned ").Append(images.Length).Append(" images / ")
              .Append(texts.Length).Append(" texts");
            _report = sb.ToString();

            if (VerboseDump) _report += "\n" + DumpSpriteNames(200);
            WriteDump();

            _ok = PanelSprite != null || ButtonSprite != null || Font != null;
            if (_ok) GameUi.ResetFont();
            return _ok;
        }

        private static void WriteDump()
        {
            if (string.IsNullOrEmpty(DumpPath)) return;
            try
            {
                var names = Names(null);
                System.IO.File.WriteAllText(DumpPath, string.Join("\n", names.ToArray()));
            }
            catch { }
        }

        private static string Name(Sprite s) => s != null ? s.name : "(none)";

        /// <summary>Exact name first, then substring needles.</summary>
        private static Sprite Pick(string[] exactNames, params string[] needles)
        {
            if (exactNames != null)
                foreach (var n in exactNames)
                    if (!string.IsNullOrEmpty(n) && _sprites.TryGetValue(n, out var s) && s != null) return s;
            return needles != null ? Find(needles) : null;
        }

        /// <summary>The game's main UI canvas, or the biggest foreign canvas as a fallback.</summary>
        private static Transform GameUiRoot()
        {
            try
            {
                var ui = GameManager.UIManager;
                if (ui != null && ui.Canvas != null) return ui.Canvas.transform;
            }
            catch { }

            try
            {
                Canvas best = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (c.name.StartsWith("Mod", StringComparison.OrdinalIgnoreCase)) continue;
                    if (c.GetComponentInParent<ModUiMarker>() != null) continue;
                    if (best == null || c.sortingOrder > best.sortingOrder) best = c;
                }
                if (best != null) return best.transform;
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------ font

        private static void HarvestFont(TMP_Text[] texts)
        {
            if (texts == null || texts.Length == 0) return;

            // Count every distinct face so the report shows what the game actually ships.
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in texts)
            {
                if (t == null) continue;
                TMP_FontAsset f;
                try { f = t.font; } catch { continue; }
                if (f == null) continue;
                counts.TryGetValue(f.name, out var n);
                counts[f.name] = n + 1;
                assets[f.name] = f;
            }

            var sb = new StringBuilder();
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in ordered)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('×').Append(kv.Value);
            }
            FontReport = sb.ToString();

            // Two weights, like the game itself: a body face and a heavy face for titles and
            // buttons. "Nexa Heavy" is a Latin display face and is only a last resort.
            string[] bodyPref = { "Regular", "Book", "Light", "Medium" };
            string[] boldPref = { "Bold", "SemiBold", "DemiBold", "Medium", "Heavy" };

            string body = PickFont(ordered, bodyPref);
            string bold = PickFont(ordered, boldPref);
            if (body == null && ordered.Count > 0) body = ordered[0].Key;
            if (bold == null) bold = body;
            if (body == null) return;

            Font = assets[body];
            FontBold = bold != null && assets.ContainsKey(bold) ? assets[bold] : Font;
            try { FontMaterial = Font != null ? Font.material : null; } catch { FontMaterial = null; }
        }

        private static string PickFont(List<KeyValuePair<string, int>> ordered, string[] preferred)
        {
            foreach (var p in preferred)
                foreach (var kv in ordered)
                    if (kv.Key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Key;
            return null;
        }

        // ------------------------------------------------------------ sprites

        private static void HarvestSprites(Image[] images)
        {
            if (images == null) return;
            _sprites.Clear();

            foreach (var img in images)
            {
                if (_sprites.Count >= MaxSprites) break;
                if (img == null) continue;
                Sprite sp;
                try { sp = img.overrideSprite != null ? img.overrideSprite : img.sprite; } catch { continue; }
                if (sp == null) continue;
                if (!_sprites.ContainsKey(sp.name)) _sprites[sp.name] = sp;
            }
        }

        /// <summary>
        /// First harvested sprite whose name contains any of the needles. Names are scanned in
        /// sorted order so the pick is stable across runs and machines.
        /// </summary>
        public static Sprite Find(params string[] needles)
        {
            if (needles == null || _sprites.Count == 0) return null;
            var keys = new List<string>(_sprites.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in needles)
            {
                if (string.IsNullOrEmpty(n)) continue;
                for (int i = 0; i < keys.Count; i++)
                    if (keys[i].IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return _sprites[keys[i]];
            }
            return null;
        }

        /// <summary>Every harvested sprite name whose name contains the needle.</summary>
        public static List<string> Names(string needle)
        {
            var list = new List<string>();
            foreach (var kv in _sprites)
                if (string.IsNullOrEmpty(needle) || kv.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    list.Add(kv.Key);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static string DumpSpriteNames(int max)
        {
            var names = Names(null);
            var sb = new StringBuilder("native sprite names (");
            sb.Append(names.Count).Append("): ");
            for (int i = 0; i < names.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            if (names.Count > max) sb.Append(", …");
            return sb.ToString();
        }

        /// <summary>Drops the cache so the next Harvest re-samples (after a scene reload).</summary>
        public static void Reset()
        {
            GameUi.ResetFont();
            _attempted = false;
            _ok = false;
            _sprites.Clear();
            Font = null;
            FontBold = null;
            FontMaterial = null;
            PanelSprite = PanelBorderSprite = HeaderSprite = null;
            ButtonSprite = ButtonPressedSprite = ButtonBorderSprite = null;
            SlotSprite = RowSprite = CloseSprite = CloseOffSprite = null;
            DragSprite = LocateSprite = PlusSprite = MinusSprite = null;
            CheckOnSprite = CheckOffSprite = null;
        }
    }

    /// <summary>Marks mod-owned canvases so the harvester never samples our own UI.</summary>
    public sealed class ModUiMarker : MonoBehaviour { }
}
