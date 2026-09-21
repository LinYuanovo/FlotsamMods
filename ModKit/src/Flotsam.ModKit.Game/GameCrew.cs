using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FlotsamModKit.Game
{
    public static class GameCrew
    {
        public class AutoCrewResult
        {
            public bool Blocked;
            public int MarkerSeen;
            public int MarkerChanged;
            public int LandmarkSeen;
            public int LandmarkChanged;
            public int SkippedNoCapacity;
            public readonly List<string> Details = new List<string>();

            public bool HasChanges => MarkerChanged > 0 || LandmarkChanged > 0;

            public string SummaryText()
            {
                var sb = new StringBuilder();
                if (MarkerSeen > 0)
                    sb.Append("浮标 ").Append(MarkerChanged).Append('/').Append(MarkerSeen).Append(" 调整");
                if (LandmarkSeen > 0)
                {
                    if (sb.Length > 0) sb.Append('；');
                    sb.Append("收集点 ").Append(LandmarkChanged).Append('/').Append(LandmarkSeen).Append(" 调整");
                }
                if (sb.Length == 0) sb.Append("无可用目标");
                if (SkippedNoCapacity > 0)
                    sb.Append("；").Append(SkippedNoCapacity).Append(" 个缺载重参照(无船/无小人)跳过");
                return sb.ToString();
            }

            public string LogLine()
            {
                return $"markers {MarkerChanged}/{MarkerSeen} landmarks {LandmarkChanged}/{LandmarkSeen} skip {SkippedNoCapacity}";
            }
        }

        public static int SwimCapacity()
        {
            try
            {
                var community = GameApi.PlayerCommunity;
                if (community == null || community.Agents == null) return 0;
                foreach (var agent in community.Agents)
                {
                    if (agent != null && agent.Inventory != null)
                        return agent.Inventory.ReturnStorageCapacity();
                }
            }
            catch { }
            return 0;
        }

        public static int BoatCapacity(BoatType type)
        {
            try
            {
                var community = GameApi.PlayerCommunity;
                if (community == null) return 0;
                foreach (var boat in community.ReturnAllBoats())
                {
                    if (boat != null && boat.Type == type && boat.Buildable != null && boat.Buildable.Inventory != null)
                        return boat.Buildable.Inventory.ReturnStorageCapacity();
                }
            }
            catch { }
            return 0;
        }

        public static int BoatCount(BoatType type)
        {
            try
            {
                var community = GameApi.PlayerCommunity;
                return community != null ? community.ReturnBoatCount(type) : 0;
            }
            catch { return 0; }
        }

        public static BoatType? ReturnProjectBoatType(ProjectProperties properties)
        {
            try
            {
                if (properties == null || properties.TaskQueue == null) return null;
                foreach (var task in properties.TaskQueue.List)
                {
                    var reserve = task as ReserveBoat;
                    if (reserve != null && reserve.BoatType != BoatType.None) return reserve.BoatType;
                }
            }
            catch { }
            return null;
        }

        public static int OptimalCrew(int items, int capacity, int min, int max)
        {
            if (capacity <= 0 || max < min) return -1;
            int need = (items + capacity - 1) / capacity;
            if (need < min) need = min;
            if (need > max) need = max;
            return need;
        }

        public static AutoCrewResult RunAutoMarkers(int maxCrew, bool verbose, Action<string> log)
        {
            var result = new AutoCrewResult();
            var community = GameApi.PlayerCommunity;
            if (community == null || community.Markers == null) { result.Blocked = true; return result; }

            int swimCap = SwimCapacity();
            var boatCaps = new Dictionary<BoatType, int>();

            foreach (var marker in community.Markers)
            {
                if (marker == null) continue;
                var project = marker.Project;
                if (project == null || project.Properties == null) continue;
                result.MarkerSeen++;

                int items = 0;
                try { items = marker.SalvageableItemsInRadius != null ? marker.SalvageableItemsInRadius.Count : 0; }
                catch { }

                var boatType = ReturnProjectBoatType(project.Properties);
                int capacity;
                int cap = maxCrew;
                string mode;
                if (boatType == null)
                {
                    capacity = swimCap;
                    mode = "swim";
                }
                else
                {
                    if (!boatCaps.TryGetValue(boatType.Value, out capacity))
                    {
                        capacity = BoatCapacity(boatType.Value);
                        boatCaps[boatType.Value] = capacity;
                    }
                    int boats = BoatCount(boatType.Value);
                    if (boats < cap) cap = boats;
                    mode = boatType.Value.ToString();
                }

                int need = OptimalCrew(items, capacity, 1, cap);
                if (need < 0)
                {
                    result.SkippedNoCapacity++;
                    if (verbose) Detail(log, $"marker skip: mode={mode} items={items} (no capacity reference)");
                    continue;
                }

                if (need != project.AssignmentLimit)
                {
                    try
                    {
                        marker.SetAgentAmount(need);
                        result.MarkerChanged++;
                        Detail(log, $"marker {mode}: items={items} cap={capacity} -> {need} (was {project.AssignmentLimit})");
                    }
                    catch (Exception e)
                    {
                        if (verbose) Detail(log, $"marker write failed: {e.Message}");
                    }
                }
                else if (verbose)
                {
                    Detail(log, $"marker {mode}: items={items} cap={capacity} -> {need} (unchanged)");
                }
            }
            return result;
        }

        public static AutoCrewResult RunAutoLandmarks(bool verbose, Action<string> log)
        {
            var result = new AutoCrewResult();
            var world = GameApi.World;
            if (world == null || world.Tiles == null) { result.Blocked = true; return result; }

            int swimCap = SwimCapacity();
            var boatCaps = new Dictionary<BoatType, int>();
            var auditor = new InventoryAuditor();

            foreach (var tile in world.Tiles)
            {
                if (tile == null || tile.Landmarks == null) continue;
                foreach (var spawner in tile.Landmarks)
                {
                    var landmarkSpawner = spawner as LandmarkSpawner;
                    if (landmarkSpawner == null) continue;
                    var behaviour = landmarkSpawner.LandmarkBehaviour as ActionsBehaviour;
                    if (behaviour == null || behaviour.Actions == null) continue;

                    LandmarkActionSalvage salvage = null;
                    int items = 0;
                    foreach (var action in behaviour.Actions)
                    {
                        var sal = action as LandmarkActionSalvage;
                        if (sal == null || sal.State == ILandmarkActionStates.Completed) continue;
                        if (salvage == null) salvage = sal;
                        items += CountSelectedItems(sal, auditor);
                    }
                    if (salvage == null) continue;
                    result.LandmarkSeen++;

                    bool useBoat = salvage.UseBoat;
                    BoatType boatType = BoatType.SalvagingBoat;
                    if (useBoat)
                    {
                        var bt = ReturnProjectBoatType(salvage.BoatingProjectProperties);
                        if (bt != null) boatType = bt.Value;
                    }

                    int capacity;
                    if (useBoat)
                    {
                        if (!boatCaps.TryGetValue(boatType, out capacity))
                        {
                            capacity = BoatCapacity(boatType);
                            boatCaps[boatType] = capacity;
                        }
                    }
                    else
                    {
                        capacity = swimCap;
                    }

                    int min = behaviour.AssignmentLimitMinimum;
                    int max = behaviour.AssignmentLimitMaximum;
                    if (useBoat)
                    {
                        int boats = BoatCount(boatType);
                        if (boats < max) max = boats;
                    }

                    int need = OptimalCrew(items, capacity, min, max);
                    if (need < 0)
                    {
                        result.SkippedNoCapacity++;
                        if (verbose) Detail(log, $"landmark skip: boat={useBoat} items={items} (no capacity reference)");
                        continue;
                    }

                    if (need != behaviour.AssignmentLimit)
                    {
                        try
                        {
                            int before = behaviour.AssignmentLimit;
                            behaviour.SetAssignmentLimit(need);
                            result.LandmarkChanged++;
                            if (verbose) Detail(log, $"landmark boat={useBoat}: items={items} cap={capacity} -> {need} (was {before})");
                        }
                        catch (Exception e)
                        {
                            if (verbose) Detail(log, $"landmark write failed: {e.Message}");
                        }
                    }
                    else if (verbose)
                    {
                        Detail(log, $"landmark boat={useBoat}: items={items} cap={capacity} -> {need} (unchanged)");
                    }
                }
            }
            return result;
        }

        private static int CountSelectedItems(LandmarkActionSalvage salvage, InventoryAuditor auditor)
        {
            int total = 0;
            try
            {
                if (salvage.Categories == null) return 0;
                foreach (var category in salvage.Categories)
                {
                    if (category == null || !category.MarkedForSalvage) continue;
                    auditor.Reset();
                    category.CountItems(auditor);
                    foreach (var counted in auditor.CountedItems)
                    {
                        if (counted == null || !counted.WasCounted) continue;
                        if (category.ItemFilter != null && category.ItemFilter.Count > 0)
                        {
                            bool on;
                            if (category.ItemFilter.TryGetValue(counted.ItemProperties, out on) && !on) continue;
                        }
                        total += counted.ReturnCount(InventoryAuditor.CountType.All);
                    }
                }
            }
            catch { }
            return total;
        }

        private static void Detail(Action<string> log, string line)
        {
            if (log != null) log(line);
        }
    }
}
