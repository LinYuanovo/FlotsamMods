using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>Read-only queries over the player community's placed buildings.</summary>
    public static class GameBuildings
    {
        public static List<Buildable> All()
        {
            var list = new List<Buildable>();
            var community = GameApi.PlayerCommunity;
            if (community != null && community.Buildables != null)
            {
                foreach (var b in community.Buildables)
                    if (b != null) list.Add(b);
            }
            return list;
        }

        public static bool TryGetBuildableSettings(out BuildableSettings settings)
        {
            settings = null;
            var gs = GameManager.Settings;
            if (gs == null) return false;
            settings = gs.BuildableSettings;
            return settings != null;
        }

        public static List<BuildableCategory> Categories()
        {
            var result = new List<BuildableCategory>();
            if (!TryGetBuildableSettings(out var settings) || settings.Categories == null) return result;
            foreach (var c in settings.Categories)
                if (c != null) result.Add(c);
            return result;
        }

        public static List<Buildable> InCategory(BuildableCategory category)
        {
            var result = new List<Buildable>();
            if (category == null) return All();
            var community = GameApi.PlayerCommunity;
            if (community == null || community.CategorizedBuildables == null) return result;
            if (community.CategorizedBuildables.TryGetValue(category, out var list) && list != null)
            {
                foreach (var b in list)
                    if (b != null) result.Add(b);
            }
            return result;
        }

        /// <summary>Applies the game's own ordering so the mod list matches the vanilla list.</summary>
        public static void Sort(List<Buildable> list)
        {
            if (list == null || list.Count < 2) return;
            if (!TryGetBuildableSettings(out var settings)) return;
            try { settings.SortBuildableList(list); } catch { }
        }

        public static string NameOf(Buildable buildable)
        {
            if (buildable == null) return "(null)";
            try { return buildable.Name ?? "(unnamed)"; }
            catch { return "(unnamed)"; }
        }

        public static string CategoryNameOf(Buildable buildable)
        {
            try
            {
                var cat = buildable != null && buildable.Properties != null ? buildable.Properties.Category : null;
                if (cat == null) return "";
                return cat.Name.ToString();
            }
            catch { return ""; }
        }

        public static Sprite IconOf(Buildable buildable)
        {
            try { return buildable != null && buildable.Properties != null ? buildable.Properties.Icon : null; }
            catch { return null; }
        }

        public static bool IsFinished(Buildable buildable)
        {
            try { return buildable != null && buildable.BuildPhase == BuildPhase.Finished; }
            catch { return false; }
        }

        /// <summary>Case-insensitive substring match over localized name, I2 term and category.</summary>
        public static bool Matches(Buildable buildable, string query, StringBuilder scratch = null)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (buildable == null) return false;
            var q = query.Trim().ToLowerInvariant();
            if (q.Length == 0) return true;

            try
            {
                var name = buildable.Name;
                if (!string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains(q)) return true;
            }
            catch { }

            try
            {
                var props = buildable.Properties;
                if (props != null)
                {
                    var term = props.LocalizedNameTerm;
                    if (!string.IsNullOrEmpty(term) && term.ToLowerInvariant().Contains(q)) return true;
                    if (props.Category != null)
                    {
                        var cat = props.Category.Name.ToString();
                        if (!string.IsNullOrEmpty(cat) && cat.ToLowerInvariant().Contains(q)) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>Same two calls the vanilla buildable-overview row click makes.</summary>
        public static bool Focus(Buildable buildable, float zoomLevel)
        {
            if (buildable == null) return false;
            bool ok = GameApi.LockCameraOn(buildable.gameObject, zoomLevel);
            try { buildable.OnSelected(true); } catch { }
            return ok;
        }

        public static int CountByPhase(BuildPhase phase)
        {
            int n = 0;
            var community = GameApi.PlayerCommunity;
            if (community == null || community.Buildables == null) return 0;
            foreach (var b in community.Buildables)
                if (b != null && b.BuildPhase == phase) n++;
            return n;
        }
    }
}