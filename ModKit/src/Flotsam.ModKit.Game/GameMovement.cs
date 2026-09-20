using System;
using UnityEngine;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Driving the townheart ("ship") while the world map is CLOSED.
    ///
    /// How the game actually does it (verified against the decompile):
    ///  * With the map open, `WorldMapTownheartPhycis2D.MoveWithKeys` pushes a 2D rigidbody and
    ///    `WorldMap.Update` bills the engine for `DistanceMoved`. Nothing is written to the world
    ///    model while you travel.
    ///  * Only `WorldMap.Deactivate` (map close) performs ONE
    ///    `MovementEvent.DispatchTownheartMove(start, end, ...)`. Its single listener
    ///    `World.OnTownheartMove` -> `World.RepositionTownheart` re-bases every landmark/POI/road
    ///    of every tile relative to the townheart, despawning whatever leaves
    ///    `WorldManager.IsInSpawnRadius` (= MapRadius + 250), then fires `TownheartMoved`, whose
    ///    many listeners rebuild the terrain grid, refresh all pathfinding obstacles and warp
    ///    every drifter.
    ///
    /// So `DispatchTownheartMove` is a *settle* operation, not a movement primitive. Calling it
    /// several times a second — as the previous headless-map version did — is what made every
    /// building around the town vanish. This helper therefore accumulates a virtual voyage and
    /// commits it through the very same single dispatch the game uses, on release or on a
    /// distance threshold.
    ///
    /// The WorldMap hierarchy is never activated: doing so renders the map's own sea meshes at
    /// world coordinates into the gameplay camera. Instead the map townheart is kept in sync with
    /// `Teleport`, which works fine on an inactive hierarchy and is what the quest objectives and
    /// `WorldManager.LateUpdate` region tracking read.
    /// </summary>
    public static class GameMovement
    {
        /// <summary>Result of one driving step.</summary>
        public struct StepResult
        {
            public bool Moved;
            public bool Rotated;
            public float Distance;
            public float RotationDegrees;
            public Vector3 ToPosition;
            public Quaternion ToRotation;
            public string Blocked;
        }

        // ------------------------------------------------------------ voyage state

        /// <summary>True while a keyboard voyage is in progress and not yet committed.</summary>
        public static bool Sailing { get; set; }

        /// <summary>Uncommitted voyage position (world space, y == 0 like the game's own).</summary>
        public static Vector3 VoyagePosition { get; set; }

        /// <summary>Uncommitted voyage heading.</summary>
        public static Quaternion VoyageRotation { get; set; }

        /// <summary>Where the town is right now, including an in-flight voyage.</summary>
        public static Vector3 DisplayPosition => Sailing ? VoyagePosition : TownheartPosition;

        /// <summary>Which way the town faces, including an in-flight voyage.</summary>
        public static Quaternion DisplayRotation => Sailing ? VoyageRotation : TownheartRotation;

        public static Vector3 TownheartPosition
        {
            get { var w = GameApi.World; return w != null ? w.TownheartWorldPosition : Vector3.zero; }
        }

        public static Quaternion TownheartRotation
        {
            get { var w = GameApi.World; return w != null ? w.TownheartRotation : Quaternion.identity; }
        }

        // ------------------------------------------------------------ gating

        /// <summary>Reasons the driver must not act this frame; null when it may.</summary>
        public static string BlockReason(bool requireMapClosed = true)
        {
            if (!GameApi.IsPlaying) return "no game";
            if (GameApi.IsPaused) return "paused";
            if (GameApi.IsTyping) return "typing";
            if (requireMapClosed && GameApi.IsMapOpen) return "map open (vanilla mover owns movement)";
            if (GameApi.CameraInputBlocked) return "panel blocks camera input";
            if (GameApi.TownMovementBlocked) return "town movement blocked";
            return null;
        }

        // ------------------------------------------------------------ speeds and energy

        /// <summary>World units per second; 0 when the map or its engine is not ready.</summary>
        public static float MovementSpeed
        {
            get
            {
                var map = GameApi.Map;
                if (map == null) return 0f;
                try { return map.MovementSpeed; } catch { return 0f; }
            }
        }

        /// <summary>Degrees per second; 0 when the map or its engine is not ready.</summary>
        public static float RotationSpeed
        {
            get
            {
                var map = GameApi.Map;
                if (map == null) return 0f;
                try { return map.RotationSpeed; } catch { return 0f; }
            }
        }

        /// <summary>
        /// Distance the engine will actually allow. Returns 0 while it cools down — the same
        /// gate the vanilla mover uses (Engine.ReturnMoveableDistance).
        /// </summary>
        public static float MoveableDistance(float desired)
        {
            var engine = GameApi.FindTownheartEngine();
            if (engine == null) return desired;
            try { return engine.ReturnMoveableDistance(desired); } catch { return desired; }
        }

        /// <summary>Bills the townheart engine, exactly like WorldMap.Update does.</summary>
        public static void ConsumeEnergy(float distance)
        {
            if (distance <= 0f) return;
            var engine = GameApi.FindTownheartEngine();
            if (engine == null) return;
            try { engine.ConsumeEnergy(distance); } catch { }
        }

        public static bool EngineCoolingDown
        {
            get { try { return Engine.IsCoolingDown; } catch { return false; } }
        }

        /// <summary>How much further the town can travel on the stored energy, in world units.</summary>
        public static float RemainingRange
        {
            get
            {
                var engine = GameApi.FindTownheartEngine();
                if (engine == null) return 0f;
                try { return engine.ReturnEnergyRange(); } catch { return 0f; }
            }
        }

        public static float StoredEnergy
        {
            get
            {
                var engine = GameApi.FindTownheartEngine();
                if (engine == null) return 0f;
                try { return engine.EnergyGrid != null ? engine.EnergyGrid.ReturnStorageEnergy() : 0f; }
                catch { return 0f; }
            }
        }

        public static float EnergyCapacity
        {
            get
            {
                var engine = GameApi.FindTownheartEngine();
                if (engine == null) return 0f;
                try { return engine.EnergyGrid != null ? engine.EnergyGrid.ReturnStorageCapacity() : 0f; }
                catch { return 0f; }
            }
        }

        // ------------------------------------------------------------ world bounds

        /// <summary>
        /// The rect a commit may target, in (x, z). Mirrors World.IsPositionInWorldBounds, which
        /// is the check that logs an exception and silently refuses the move when it fails.
        /// </summary>
        public static bool TryGetTravelRect(out Rect rect)
        {
            rect = new Rect();
            var world = GameApi.World;
            if (world == null || world.Tiles == null || world.Tiles.Count == 0) return false;
            try
            {
                var first = world.Tiles[0];
                var last = world.Tiles[world.Tiles.Count - 1];
                if (first == null || last == null) return false;

                var fb = first.WorldBounds;
                var lb = last.WorldBounds;
                rect = Rect.MinMaxRect(fb.xMin, fb.yMin, lb.xMax, fb.yMax);
                return rect.width > 0f && rect.height > 0f;
            }
            catch { return false; }
        }

        /// <summary>True when a commit to this position would be accepted by the world model.</summary>
        public static bool IsInsideWorld(Vector3 position)
        {
            var world = GameApi.World;
            if (world == null || world.Tiles == null || world.Tiles.Count == 0) return false;
            try
            {
                var fb = world.Tiles[0].WorldBounds;
                float xMax = world.Tiles[world.Tiles.Count - 1].WorldBounds.xMax;
                // World.IsPositionInWorldBounds compares position.y (always 0 after
                // Vector3TopDown) against the FIRST tile's y range.
                return fb.xMin <= position.x && position.x <= xMax &&
                       fb.yMin <= position.y && position.y <= fb.yMax;
            }
            catch { return false; }
        }

        /// <summary>
        /// Asks the world to spawn the next tile when the town approaches the edge — the same
        /// call WorldMap.LateUpdate makes every frame while the map is open. Without it a long
        /// voyage ends up outside the spawned tiles and the commit is refused.
        /// </summary>
        public static void EnsureTiles(Vector3 position)
        {
            var world = GameApi.World;
            if (world == null) return;
            try { world.UpdateTiles(new Vector2(position.x, position.z)); } catch { }
        }

        /// <summary>Keeps the (inactive) map townheart where our voyage is, for quests and regions.</summary>
        public static void SyncMapTownheart(Vector3 position, Quaternion rotation)
        {
            var map = GameApi.Map;
            if (map == null) return;
            try
            {
                var townheart = map.Townheart;
                if (townheart != null) townheart.Teleport(position, rotation);

                // Teleport routes the position through WorldMapTownheartPhycis2D.
                // SetPositionAndRotation, which writes the vector straight into the Rigidbody2D's
                // transform - and a 2D body reads its position from the transform's X/Y. The map's
                // own convention is transform = (x, z, 0) (see WorldMapTownheartPhycis2D.Initialize
                // and OnEnable), but Teleport passes the top-down WORLD vector (x, 0, z), parking
                // the body on the wrong plane (y == 0). The next time the map opens, the body
                // wanders off that bogus spot, WorldMap counts the wander as DistanceMoved and
                // Deactivate commits it as the voyage - "the town drifts when I open the map".
                // Re-write the body in the plane the game itself uses.
                if (_mapPhysicsField == null)
                    _mapPhysicsField = typeof(WorldMap).GetField("_townheartPhysics",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var physics = _mapPhysicsField != null
                    ? _mapPhysicsField.GetValue(map) as WorldMapTownheartPhycis2D : null;
                if (physics != null)
                    physics.SetPositionAndRotation(new Vector3(position.x, position.z, 0f),
                                                   Quaternion.Euler(0f, 0f, -rotation.eulerAngles.y));
            }
            catch { }
        }

        private static System.Reflection.FieldInfo _mapPhysicsField;

        public static void NotifyStartMoving()
        {
            var map = GameApi.Map;
            try { if (map != null && map.Townheart != null) map.Townheart.OnStartMove(); } catch { }
            try { MovementEvent.DispatchStartedMoving(); } catch { }
        }

        public static void NotifyStopMoving()
        {
            var map = GameApi.Map;
            try { if (map != null && map.Townheart != null) map.Townheart.OnEndMove(); } catch { }
            try { MovementEvent.DispatchStoppedMoving(); } catch { }
        }

        // ------------------------------------------------------------ autopilot (minimap click-to-sail)

        /// <summary>True while a minimap click is steering the town towards <see cref="AutoTarget"/>.</summary>
        public static bool AutoActive { get; set; }

        /// <summary>Where the minimap click asked the town to sail (y flattened to 0).</summary>
        public static Vector3 AutoTarget { get; set; }

        /// <summary>Close enough to the marker to settle and call it arrived.</summary>
        public static float AutoArriveRadius = 18f;

        public static void StartAuto(Vector3 target)
        {
            AutoTarget = new Vector3(target.x, 0f, target.z);
            AutoActive = true;
        }

        public static void CancelAuto()
        {
            AutoActive = false;
        }

        /// <summary>
        /// Steering input that flies the virtual voyage to <see cref="AutoTarget"/>: turn towards
        /// it, thrust once roughly aligned, and ease off inside the braking band so the single
        /// settle lands on the marker instead of overshooting it.
        /// </summary>
        public static void AutoInput(Vector3 from, Quaternion fromRotation, out float forward, out float rotate)
        {
            forward = 0f;
            rotate = 0f;

            var dir = AutoTarget - from;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 0.5f) return;

            float desired = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            var fwd = fromRotation * Vector3.forward;
            float current = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(current, desired);

            rotate = Mathf.Clamp(delta / 12f, -1f, 1f);
            float cos = Mathf.Cos(delta * Mathf.Deg2Rad);
            forward = cos > 0.80f ? 1f : cos > 0.25f ? 0.45f : 0f;
            if (dist < 60f) forward = Mathf.Min(forward, dist / 60f);
        }

        // ------------------------------------------------------------ obstacle protection

        /// <summary>
        /// The world map protects landmarks and constructions with real colliders on its physics
        /// body; the virtual voyage has none, so without this the town could be driven (or
        /// autopiloted) straight into an island or building. The live scene keeps exactly those
        /// shapes in <c>Buildable.BlockingPolygons</c> (constructions, mooring blockers) and in
        /// each landmark spawner's outline polygon, so a step is refused when it would cross from
        /// outside into one of them - the same "you cannot move there" rule the map's colliders
        /// enforce. Steps that start inside a polygon stay allowed, so a town that is already
        /// stuck can still drive out, and a voyage that began inside one (the townheart's own
        /// platform) is never locked in place.
        /// </summary>
        public static bool BlocksMovement(Vector2 from, Vector2 to, Vector2 voyageBase, out string why)
        {
            why = null;

            // Player constructions and mooring blockers register their blocking polygons here
            // (the same set the build placement overlap test uses).
            try
            {
                foreach (var poly in Buildable.BlockingPolygons)
                {
                    if (poly == null) continue;
                    if (PolygonBlocks(poly, Vector2.zero, from, to, voyageBase))
                    {
                        why = "建筑";
                        return true;
                    }
                }
            }
            catch { }

            // Landmark islands / resource patches: their outline polygon lives in tile space, so
            // the test point is un-shifted by the spawner's world-vs-tile offset. Islands have no
            // Obstacle component, which is why the obstacle-list protection never saw them.
            try
            {
                var world = GameApi.World;
                if (world != null && world.Tiles != null)
                {
                    for (int t = 0; t < world.Tiles.Count; t++)
                    {
                        var tile = world.Tiles[t];
                        if (tile == null || tile.Landmarks == null) continue;
                        for (int i = 0; i < tile.Landmarks.Count; i++)
                        {
                            var spawner = tile.Landmarks[i];
                            if (spawner == null) continue;
                            var poly = spawner.TileSpacePolygon;
                            if (poly == null) continue;
                            var delta = spawner.WorldPosition2D - spawner.TilePosition;
                            if (PolygonBlocks(poly, delta, from, to, voyageBase))
                            {
                                why = "地标";
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool PolygonBlocks(Polygon poly, Vector2 delta, Vector2 from, Vector2 to, Vector2 basePos)
        {
            var bounds = poly.Bounds;
            var q = to - delta;
            if (q.x < bounds.xMin - 2f || q.x > bounds.xMax + 2f ||
                q.y < bounds.yMin - 2f || q.y > bounds.yMax + 2f) return false;
            if (PointInPolygon(poly, delta, from)) return false;     // already inside: let it escape
            if (PointInPolygon(poly, delta, basePos)) return false;  // voyage started inside it
            return PointInPolygon(poly, delta, to);
        }

        private static bool PointInPolygon(Polygon poly, Vector2 delta, Vector2 p)
        {
            var verts = poly.Polygon2D;
            if (verts == null || verts.Length < 3) return false;
            float x = p.x - delta.x, y = p.y - delta.y;
            bool inside = false;
            for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
            {
                var vi = verts[i];
                var vj = verts[j];
                if ((vi.y > y) != (vj.y > y) &&
                    x < (vj.x - vi.x) * (y - vi.y) / (vj.y - vi.y) + vi.x)
                    inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------ commit

        /// <summary>
        /// Performs the single settle operation the game performs when the map closes.
        /// Spawns any tile the destination needs first, clamps into the accepted rect, and
        /// reports whether the world model really moved.
        /// </summary>
        public static bool Commit(Vector3 from, Vector3 to, Quaternion fromRotation, Quaternion toRotation,
                                  float distance, out string error)
        {
            error = null;

            // The event itself flattens y to 0, so compare and clamp in that same space.
            from = new Vector3(from.x, 0f, from.z);
            to = new Vector3(to.x, 0f, to.z);

            EnsureTiles(to);

            if (!IsInsideWorld(to))
            {
                Rect rect;
                if (!TryGetTravelRect(out rect))
                {
                    error = "世界边界不可用";
                    return false;
                }
                const float margin = 8f;
                to.x = Mathf.Clamp(to.x, rect.xMin + margin, rect.xMax - margin);
                to.z = Mathf.Clamp(to.z, rect.yMin + margin, rect.yMax - margin);
                EnsureTiles(to);

                if (!IsInsideWorld(to))
                {
                    error = "目的地在已生成区域之外";
                    return false;
                }
            }

            try
            {
                MovementEvent.DispatchTownheartMove(from, to, fromRotation, toRotation, distance);
            }
            catch (Exception e)
            {
                error = "提交失败: " + e.Message;
                return false;
            }

            var world = GameApi.World;
            if (world != null && (world.TownheartWorldPosition - to).sqrMagnitude > 1f)
            {
                error = "世界拒绝了这次移动";
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ stepping

        /// <summary>
        /// One virtual driving step. Unlike the vanilla mover this rotates independently of
        /// forward thrust: `WorldMapTownheartPhycis2D.ApplyMovementAndRotation` early-outs when
        /// the engine allows no distance, so with the native path A/D do nothing unless W is held
        /// — the reason the previous version never changed heading.
        /// </summary>
        public static StepResult Step(Vector3 from, Quaternion fromRotation, float forward, float rotate,
                                      float dt, float speedMultiplier = 1f)
        {
            var result = new StepResult { ToPosition = from, ToRotation = fromRotation };

            float moveSpeed = MovementSpeed;
            float rotSpeed = RotationSpeed;
            if (moveSpeed <= 0f && rotSpeed <= 0f)
            {
                result.Blocked = "引擎未就绪";
                return result;
            }

            float rotDelta = Mathf.Clamp(rotate, -1f, 1f) * rotSpeed * dt;

            // Sign, derived from the vanilla mover rather than guessed:
            //   MoveWithKeys:      num2 = input.x * (0f - RotationSpeed)          (164006)
            //   MovePositionAndRotation: MoveRotation(_rigidbody.rotation + num2 * dt)
            //   Rotation => Quaternion.Euler(0f, 0f - _rigidbody.rotation, 0f)    (163889)
            // so the world Y rotation changes by +RotationSpeed * input.x * dt: pressing
            // rotate-right turns the heading clockwise (towards +X), which is what the
            // compass below reports as increasing.
            //
            // Axis convention: the mover is 2D and 2D "up" maps to world +Z
            // (Vector2TopDown(v3) => (v.x, v.z), 205071), so heading is rotation * Vector3.forward.
            var toRotation = fromRotation * Quaternion.Euler(0f, rotDelta, 0f);
            float desired = Mathf.Clamp(forward, -1f, 1f) * Mathf.Max(0.01f, speedMultiplier) * moveSpeed * dt;
            float allowed = desired != 0f ? MoveableDistance(desired) : 0f;

            var toPosition = from + toRotation * Vector3.forward * allowed;
            toPosition.y = 0f;

            result.Moved = Mathf.Abs(allowed) > 0.0001f;
            result.Rotated = Mathf.Abs(rotDelta) > 0.0001f;
            result.Distance = Mathf.Abs(allowed);
            result.RotationDegrees = rotDelta;
            result.ToPosition = toPosition;
            result.ToRotation = toRotation;

            if (!result.Moved && desired != 0f) result.Blocked = "引擎冷却中";
            return result;
        }
    }
}
