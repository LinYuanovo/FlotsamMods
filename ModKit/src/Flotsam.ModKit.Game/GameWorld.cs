using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Data-only world access for the minimap. Deliberately no engine rendering: the native
    /// world-map tiles cannot be re-activated on their own (they hang off the deactivated
    /// WorldMap hierarchy, and activating that hierarchy renders the map's sea meshes into the
    /// gameplay camera), so the minimap composites its own background from world data and draws
    /// the game's own marker sprites as an icon layer on top.
    ///
    /// All the collection methods fill a caller-owned list: the minimap redraws several times a
    /// second and must not allocate a fresh list of hundreds of markers each time.
    /// </summary>
    public static class GameWorld
    {
        public enum MarkerKind
        {
            Unknown = 0,
            PointOfInterest = 1,
            Landmark = 2,
            Road = 3,
            Flotsam = 4,
            /// <summary>A building the player placed; drawn with its own inventory icon.</summary>
            Town = 5
        }

        public struct Marker
        {
            public Vector3 WorldPosition;
            public MarkerKind Kind;
            public int ScoutingState;
            public string Label;
            public Sprite Icon;
            public object Source;
        }

        /// <summary>One tile's fog-of-war vertex grid (real explored state, no rendering).</summary>
        public struct FogPatch
        {
            public Rect WorldBounds;
            public int CellsX;
            public int CellsZ;
            /// <summary>Row-major vertex alphas: v = j + i * (CellsX + 1).</summary>
            public byte[] Alphas;
        }

        public static bool TryGetWorldBounds(out Rect bounds)
        {
            bounds = new Rect();
            try
            {
                bounds = WorldManager.ReturnWorldBounds();
                return bounds.width > 0f && bounds.height > 0f;
            }
            catch { return false; }
        }

        public static void TileBounds(List<Rect> results)
        {
            if (results == null) return;
            results.Clear();
            var world = GameApi.World;
            if (world == null || world.Tiles == null) return;
            foreach (var tile in world.Tiles)
            {
                if (tile == null) continue;
                try { results.Add(tile.WorldBounds); } catch { }
            }
        }

        /// <summary>Allocating convenience wrapper.</summary>
        public static List<Rect> TileBounds()
        {
            var list = new List<Rect>();
            TileBounds(list);
            return list;
        }

        /// <summary>
        /// Real fog-of-war state per tile (WorldTile.FogOfWarAlphas, public byte[]).
        /// The vertex grid is (Cells+1)^2; the cell count is derived from the array length,
        /// so non-square grids are skipped rather than drawn wrongly.
        /// </summary>
        public static void FogPatches(List<FogPatch> results)
        {
            if (results == null) return;
            results.Clear();
            var world = GameApi.World;
            if (world == null || world.Tiles == null) return;

            foreach (var tile in world.Tiles)
            {
                if (tile == null) continue;
                try
                {
                    var alphas = tile.FogOfWarAlphas;
                    if (alphas == null || alphas.Length < 4) continue;

                    int side = (int)Math.Round(Math.Sqrt(alphas.Length));
                    if (side <= 1 || side * side != alphas.Length) continue;

                    var bounds = tile.FogOfWarBounds;
                    if (bounds.width <= 0f || bounds.height <= 0f) continue;

                    results.Add(new FogPatch
                    {
                        WorldBounds = bounds,
                        CellsX = side - 1,
                        CellsZ = side - 1,
                        Alphas = alphas
                    });
                }
                catch { }
            }
        }

        public static List<FogPatch> FogPatches()
        {
            var list = new List<FogPatch>();
            FogPatches(list);
            return list;
        }

        public static Vector3 TownheartPosition => GameMovement.TownheartPosition;
        public static Quaternion TownheartRotation => GameMovement.TownheartRotation;

        /// <summary>
        /// All scouting markers across all tiles plus floating salvage piles, appended to
        /// <paramref name="results"/> (which is NOT cleared, so callers can layer sources).
        /// </summary>
        public static void Markers(List<Marker> results, bool includeRoads = false)
        {
            if (results == null) return;
            var world = GameApi.World;
            if (world == null || world.Tiles == null) return;
            EnsureDisposedListener(world);

            foreach (var tile in world.Tiles)
            {
                if (tile == null) continue;
                try
                {
                    if (tile.PointsOfInterest != null)
                        foreach (var s in tile.PointsOfInterest) Add(results, s as ISpawner, MarkerKind.PointOfInterest);
                    if (tile.Landmarks != null)
                        foreach (var s in tile.Landmarks) Add(results, s as ISpawner, MarkerKind.Landmark);
                    if (includeRoads && tile.Roads != null)
                        foreach (var s in tile.Roads) Add(results, s as ISpawner, MarkerKind.Road);
                }
                catch { }
            }

            try
            {
                var wm = GameManager.WorldManager;
                if (wm != null && wm.FlotsamInWorld != null)
                {
                    foreach (var f in wm.FlotsamInWorld)
                    {
                        if (f == null) continue;
                        results.Add(new Marker
                        {
                            WorldPosition = f.transform.position,
                            Kind = MarkerKind.Flotsam,
                            ScoutingState = 4,
                            Label = "漂浮垃圾",
                            Icon = null,
                            Source = f
                        });
                    }
                }
            }
            catch { }
        }

        public static List<Marker> Markers(bool includeRoads = false)
        {
            var list = new List<Marker>();
            Markers(list, includeRoads);
            return list;
        }

        /// <summary>
        /// The player's own buildings, projected into map space.
        ///
        /// Buildables live in the gameplay scene, and `WorldManager._townheartScenePosition` is a
        /// constant `Vector3.zero`, so a scene position already IS the offset from the townheart.
        /// Adding the townheart's world position places the town on the map without any rotation:
        /// unlike landmarks (re-based with `Quaternion.Inverse(townheartRotation)`), the player's
        /// own platform does not turn with the heading.
        /// </summary>
        public static void TownMarkers(List<Marker> results)
        {
            if (results == null) return;
            var community = GameApi.PlayerCommunity;
            if (community == null || community.Buildables == null) return;

            var origin = GameMovement.DisplayPosition;
            foreach (var b in community.Buildables)
            {
                if (b == null) continue;
                try
                {
                    var scene = b.transform.position;
                    results.Add(new Marker
                    {
                        WorldPosition = new Vector3(origin.x + scene.x, 0f, origin.z + scene.z),
                        Kind = MarkerKind.Town,
                        ScoutingState = 4,
                        Label = null,
                        Icon = GameBuildings.IconOf(b),
                        Source = b
                    });
                }
                catch { }
            }
        }

        /// <summary>The gameplay radius rings the game itself uses, in world units.</summary>
        public static bool TryGetRadii(out float swimRadius, out float mapRadius)
        {
            swimRadius = 0f;
            mapRadius = 0f;
            try
            {
                var gp = GameManager.Settings != null ? GameManager.Settings.GameplaySettings : null;
                if (gp == null) return false;
                swimRadius = gp.SwimmingRadius;
                mapRadius = gp.MapRadius;
                return swimRadius > 0f || mapRadius > 0f;
            }
            catch { return false; }
        }

        private static void Add(List<Marker> list, ISpawner spawner, MarkerKind kind)
        {
            if (spawner == null) return;

            // Harvested-out resources stay in the tile's spawner lists, but the world map drops
            // their markers: it destroys the visual when the game dispatches LandmarkDisposed.
            // Track that event here, or the minimap keeps advertising wood and stone that no
            // longer exist.
            if (IsDisposed(spawner) || IsDepleted(spawner)) return;

            // Read each member in its own guard: a spawner whose LandmarkBehaviour is gone throws
            // on Icon, and that must drop the ICON, not the whole marker.
            Vector3 position;
            try { position = spawner.WorldPosition; } catch { return; }

            int scouting = 0;
            try { scouting = (int)spawner.ScoutingState; } catch { }

            Sprite icon = null;
            try { icon = spawner.Icon; } catch { }

            list.Add(new Marker
            {
                WorldPosition = position,
                Kind = kind,
                ScoutingState = scouting,
                Label = null,
                // ISpawner.Icon is a public interface member, so the game's own icon
                // sprites are usable directly (only the backing field is internal).
                Icon = icon,
                Source = spawner
            });
        }

        private static readonly Dictionary<Type, MemberInfo> _nameMembers = new Dictionary<Type, MemberInfo>();

        // ------------------------------------------------------------ depletion

        private static readonly HashSet<ISpawner> _disposedSpawners = new HashSet<ISpawner>();
        private static World _disposedListenWorld;
        private static bool _disposedListening;

        /// <summary>
        /// The world map removes a landmark marker when the game dispatches LandmarkDisposed
        /// (harvested empty / salvaged out); the spawner itself stays in the tile list, so the
        /// minimap has to track the same event to stop showing collected resources.
        /// </summary>
        private static void EnsureDisposedListener(World world)
        {
            if (_disposedListenWorld != world)
            {
                _disposedListenWorld = world;
                _disposedSpawners.Clear();
            }
            if (_disposedListening || world == null) return;
            try
            {
                GameEventDispatcher.AddListener(GameEventType.LandmarkDisposed, OnLandmarkDisposed);
                _disposedListening = true;
            }
            catch { }
        }

        private static void OnLandmarkDisposed(GameEvent gameEvent)
        {
            var e = gameEvent as LandmarkNotificationEvent;
            if (e != null && e.LandmarkSpawner != null) _disposedSpawners.Add(e.LandmarkSpawner);
        }

        private static bool IsDisposed(ISpawner spawner)
        {
            return spawner != null && _disposedSpawners.Count > 0 && _disposedSpawners.Contains(spawner);
        }

        private static FieldInfo _poiFlotsamField, _poiCompositeField, _flotsamCompField;
        private static PropertyInfo _groupSpawnersProp;
        private static bool _depletionProbed;

        /// <summary>
        /// True when a spawner's harvestable content is gone: a landmark whose behaviour was
        /// disposed (or whose composition inventory is empty), or a point of interest whose every
        /// flotsam spawner has an empty composition. The world map hides exactly these; without
        /// this the minimap keeps showing resources that were already collected.
        /// </summary>
        private static bool IsDepleted(ISpawner spawner)
        {
            try
            {
                var landmark = spawner as LandmarkSpawner;
                if (landmark != null)
                {
                    // LandmarkBehaviour is a ScriptableObject instance, so the runtime harvest
                    // state lives on the spawned scene instance and is not reachable from the
                    // spawner; a missing behaviour (disposed landmark) is the only signal here.
                    return !landmark.Enabled;
                }

                var poi = spawner as PointOfInterestSpawner;
                if (poi != null)
                {
                    // The world map's own rule (WorldMapFlotsam.OnSalvage): no remaining flotsam
                    // spawners means the pile is gone and the marker is destroyed.
                    try { if (poi.ReturnSpawnerCount() <= 0f) return true; } catch { }
                    if (!_depletionProbed)
                    {
                        _depletionProbed = true;
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var poiType = typeof(PointOfInterestSpawner);
                        _poiFlotsamField = poiType.GetField("_flotsamSpawners", flags);
                        _poiCompositeField = poiType.GetField("_compositedFlotsamSpawners", flags);
                        _groupSpawnersProp = typeof(FlotsamSpawnerGroup).GetProperty("Spawners",
                            BindingFlags.Public | BindingFlags.Instance);
                        _flotsamCompField = typeof(FlotsamSpawner).GetField("_compositionInventory", flags);
                    }
                    if (_poiFlotsamField == null || _groupSpawnersProp == null || _flotsamCompField == null)
                        return false;

                    int total = 0, empty = 0;
                    CountFlotsamGroup(_poiFlotsamField.GetValue(poi), ref total, ref empty);
                    CountFlotsamGroup(_poiCompositeField.GetValue(poi), ref total, ref empty);
                    return total > 0 && empty == total;
                }
            }
            catch { }
            return false;
        }

        private static void CountFlotsamGroup(object group, ref int total, ref int empty)
        {
            if (group == null) return;
            var list = _groupSpawnersProp.GetValue(group, null) as System.Collections.IList;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var flotsam = list[i] as FlotsamSpawner;
                if (flotsam == null) continue;
                var inventory = _flotsamCompField.GetValue(flotsam) as SubInventory;
                total++;
                if (inventory != null && inventory.IsEmpty) empty++;
            }
        }

        /// <summary>
        /// Best-effort display name from whatever public member the source exposes. The member
        /// lookup is cached per type: the minimap asks for every spawner several times a second
        /// and an uncached GetProperty per spawner per refresh is a visible stall.
        /// </summary>
        public static string NameOf(object source)
        {
            if (source == null) return null;
            try
            {
                var type = source.GetType();
                MemberInfo member;
                if (!_nameMembers.TryGetValue(type, out member))
                {
                    var prop = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(string) && prop.CanRead) member = prop;
                    else
                    {
                        var field = type.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                        member = (field != null && field.FieldType == typeof(string)) ? field : null;
                    }
                    _nameMembers[type] = member;
                }

                var pi = member as PropertyInfo;
                if (pi != null) return pi.GetValue(source) as string;
                var fi = member as FieldInfo;
                if (fi != null) return fi.GetValue(source) as string;
            }
            catch { }
            return null;
        }

        public static int RegionCount()
        {
            int n = 0;
            var world = GameApi.World;
            if (world == null || world.Tiles == null) return 0;
            foreach (var t in world.Tiles)
            {
                if (t == null) continue;
                try { if (t.Regions != null) n += t.Regions.Count; } catch { }
            }
            return n;
        }

        /// <summary>Human-readable scouting state.</summary>
        public static string ScoutingLabel(int state)
        {
            switch (state)
            {
                case 0: return "未知";
                case 1: return "已标记";
                case 2: return "传闻";
                case 3: return "已确认";
                case 4: return "已侦察";
                default: return "?";
            }
        }
    }
}
