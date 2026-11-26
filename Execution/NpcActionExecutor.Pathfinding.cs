using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    // Capa A: Caché ESTÁTICO PURO por mapa (jamás almacena entidades móviles -> 0% envenenamiento)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Point, (ulong Tick, bool Walkable)>> _staticMapWalkCache = new(StringComparer.OrdinalIgnoreCase);

    // Escuadrón multi-seguidor aislado por (OwnerPlayerId, NpcName)
    private static readonly ConcurrentDictionary<(long OwnerId, string NpcName), (string Loc, Point Tile, ulong Tick)> _reservedFollowSlots = new();

    // Buffers reutilizables en el hilo principal del Host para A* sin presión GC
    private static readonly PriorityQueue<Point, int> _aStarQueue = new(1024);
    private static readonly Dictionary<Point, Point> _aStarCameFrom = new(1024);
    private static readonly Dictionary<Point, int> _aStarCostSoFar = new(1024);
    private static readonly List<Point> _aStarPathBuffer = new(256);
    private static readonly Point[] _cardinalDirs = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0) };

    public static void InvalidateWalkabilityCache(string? locationName = null)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            _staticMapWalkCache.Clear();
        else
            _staticMapWalkCache.TryRemove(locationName, out _);
    }

    public static void ClearAllFollowSlots() => _reservedFollowSlots.Clear();

    public static void ReleaseFollowSlot(long ownerPlayerId, string npcName)
    {
        _reservedFollowSlots.TryRemove((ownerPlayerId, npcName), out _);
    }

    public static Farmer? GetJobFarmer(NpcJob job)
    {
        if (job == null) return null;
        if (job.OwnerPlayerId != 0)
            return Game1.GetPlayer(job.OwnerPlayerId, onlyOnline: true);
        return Context.IsMultiplayer ? null : Game1.MasterPlayer;
    }

    public static bool IsTileWalkable(
        GameLocation loc,
        Point p,
        NPC? npc = null,
        bool ignoreDynamicCharacters = true,
        bool allowWarpTile = false,
        Farmer? ignoreSpecificFarmer = null)
    {
        if (loc?.map?.Layers == null || loc.map.Layers.Count == 0)
            return false;

        var backLayer = loc.map.GetLayer("Back") ?? loc.map.Layers[0];
        if (p.X < 0 || p.Y < 0 || p.X >= backLayer.LayerWidth || p.Y >= backLayer.LayerHeight)
            return false;

        bool leavingOrWarping = allowWarpTile || (npc != null && Instance != null && Instance.IsIntentionalWarpAllowed(npc.Name));
        if (loc is Farm && !leavingOrWarping)
        {
            if (p.X < 1 || p.X > backLayer.LayerWidth - 2 || p.Y < 1 || p.Y > backLayer.LayerHeight - 2)
                return false;
        }

        if (!leavingOrWarping && loc.warps != null)
        {
            for (int i = 0; i < loc.warps.Count; i++)
            {
                var w = loc.warps[i];
                if (w != null && w.X == p.X && w.Y == p.Y)
                    return false;
            }
        }

        // CAPA A: Estática pura (cacheada 60 ticks por ubicación)
        ulong now = (ulong)Game1.ticks;
        var locCache = _staticMapWalkCache.GetOrAdd(loc.NameOrUniqueName, _ => new ConcurrentDictionary<Point, (ulong, bool)>());
        bool staticWalkable;
        if (locCache.TryGetValue(p, out var cached) && (now - cached.Tick) <= 60)
        {
            staticWalkable = cached.Walkable;
        }
        else
        {
            staticWalkable = EvaluateStaticTileWalkable(loc, p);
            locCache[p] = (now, staticWalkable);
        }

        if (!staticWalkable)
            return false;

        // CAPA B: Entidades dinámicas en vivo (solo cuando se solicita; NUNCA cacheada)
        if (!ignoreDynamicCharacters && IsDynamicEntityBlockingTile(loc, p, npc, ignoreSpecificFarmer))
            return false;

        return true;
    }

    public static bool IsWalkableBridgeTile(GameLocation loc, int x, int y)
    {
        if (loc == null) return false;
        string? bPass = loc.doesTileHaveProperty(x, y, "Passable", "Buildings");
        if (bPass != null && !bPass.Equals("F", StringComparison.OrdinalIgnoreCase))
            return true;
        if (loc.doesTileHaveProperty(x, y, "Water", "Back") != null &&
            loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport))
            return true;
        return false;
    }

    private static bool EvaluateStaticTileWalkable(GameLocation loc, Point p)
    {
        Vector2 tileVec = new(p.X, p.Y);
        bool isBridge = IsWalkableBridgeTile(loc, p.X, p.Y);
        if (!isBridge && !loc.isTilePassable(new xTile.Dimensions.Location(p.X, p.Y), Game1.viewport))
            return false;

        if (loc.isWaterTile(p.X, p.Y) && !isBridge)
            return false;

        string? bPass = loc.doesTileHaveProperty(p.X, p.Y, "Passable", "Buildings");
        if (bPass != null && bPass.Equals("F", StringComparison.OrdinalIgnoreCase))
            return false;

        Rectangle fullTileRect = new(p.X * 64 + 1, p.Y * 64 + 1, 62, 62);
        if (loc.getBuildingAt(tileVec) is Building bAt && !bAt.isTilePassable(tileVec))
            return false;

        if (loc.buildings != null)
        {
            foreach (var b in loc.buildings)
            {
                if (b == null) continue;
                if ((b.occupiesTile(p.X, p.Y, true) ||
                    (p.X >= b.tileX.Value && p.X < b.tileX.Value + b.tilesWide.Value &&
                     p.Y >= b.tileY.Value && p.Y < b.tileY.Value + b.tilesHigh.Value)) &&
                    !b.isTilePassable(tileVec))
                    return false;
            }
        }

        if (loc is Farm farmLoc && farmLoc.GetMainFarmHouse() is Building mainHouse)
        {
            if (mainHouse.occupiesTile(p.X, p.Y, true) && !mainHouse.isTilePassable(tileVec))
                return false;
        }

        if (loc.resourceClumps != null)
        {
            for (int i = 0; i < loc.resourceClumps.Count; i++)
            {
                var rc = loc.resourceClumps[i];
                if (rc != null && rc.occupiesTile(p.X, p.Y))
                    return false;
            }
        }

        if (loc.Objects != null && loc.Objects.TryGetValue(tileVec, out var obj) && obj != null)
        {
            if (obj is Fence fence && fence.isGate.Value)
                return true;
            if (!obj.isPassable())
                return false;
        }

        if (loc.furniture != null)
        {
            for (int i = 0; i < loc.furniture.Count; i++)
            {
                var furn = loc.furniture[i];
                if (furn == null || furn.furniture_type.Value == Furniture.rug)
                    continue;
                if (!furn.isPassable() && furn.GetBoundingBox().Intersects(fullTileRect))
                    return false;
            }
        }

        if (loc.terrainFeatures != null && loc.terrainFeatures.TryGetValue(tileVec, out var tf) && tf != null)
        {
            if (tf is HoeDirt hd)
            {
                if (hd.crop != null && hd.crop.raisedSeeds.Value)
                    return false;
            }
            else if (!tf.isPassable(Game1.player))
            {
                return false;
            }
        }

        if (loc.largeTerrainFeatures != null)
        {
            for (int i = 0; i < loc.largeTerrainFeatures.Count; i++)
            {
                var ltf = loc.largeTerrainFeatures[i];
                if (ltf != null && !ltf.isPassable(Game1.player) && ltf.getBoundingBox().Intersects(fullTileRect))
                    return false;
            }
        }

        return true;
    }

    private static bool IsDynamicEntityBlockingTile(GameLocation loc, Point p, NPC? selfNpc, Farmer? ignoreFarmer)
    {
        Rectangle tileRect = new(p.X * 64 + 6, p.Y * 64 + 6, 52, 52);
        if (loc.characters != null)
        {
            for (int i = 0; i < loc.characters.Count; i++)
            {
                var c = loc.characters[i];
                if (c == null || ReferenceEquals(c, selfNpc)) continue;
                if (c.GetBoundingBox().Intersects(tileRect)) return true;
            }
        }

        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (farmer == null || ReferenceEquals(farmer, ignoreFarmer)) continue;
            if (farmer.currentLocation?.NameOrUniqueName != loc.NameOrUniqueName)
                continue;
            if (farmer.GetBoundingBox().Intersects(tileRect)) return true;
        }

        if (loc.animals != null)
        {
            foreach (var animal in loc.animals.Values)
            {
                if (animal != null && animal.GetBoundingBox().Intersects(tileRect))
                    return true;
            }
        }

        return false;
    }

    private static bool IsAdjacentToWarp(GameLocation loc, Point p)
    {
        if (loc.warps == null || loc.warps.Count == 0) return false;
        for (int i = 0; i < loc.warps.Count; i++)
        {
            var w = loc.warps[i];
            if (w == null) continue;
            if (Math.Abs(w.X - p.X) + Math.Abs(w.Y - p.Y) <= 1)
                return true;
        }
        return false;
    }

    public static void AutoOpenNearbyGate(GameLocation loc, NPC npc)
    {
        if (loc?.Objects == null || npc == null) return;
        TryOpenGateAt(loc, npc.TilePoint);
        if (npc.controller?.pathToEndPoint != null && npc.controller.pathToEndPoint.Count > 0)
            TryOpenGateAt(loc, npc.controller.pathToEndPoint.Peek());
    }

    private static void TryOpenGateAt(GameLocation loc, Point pt)
    {
        if (loc.Objects.TryGetValue(new Vector2(pt.X, pt.Y), out var obj) &&
            obj is Fence fence && fence.isGate.Value && fence.gatePosition.Value == 0)
        {
            fence.toggleGate(true);
        }
    }

    public static Point GetAdjacentWalkableTile(GameLocation loc, Point target, Point fromTile, NPC? npc = null, int maxRadius = 3)
    {
        if (IsTileWalkable(loc, target, npc)) return target;
        Point best = fromTile;
        int bestScore = int.MaxValue;
        bool found = false;
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    Point cand = new(target.X + dx, target.Y + dy);
                    if (!IsTileWalkable(loc, cand, npc)) continue;
                    int score = (cand.X - fromTile.X) * (cand.X - fromTile.X) + (cand.Y - fromTile.Y) * (cand.Y - fromTile.Y);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = cand;
                        found = true;
                    }
                }
            }
            if (found) return best;
        }
        return fromTile;
    }

    public static Stack<Point>? FindCustomPath(GameLocation loc, Point start, Point goal, NPC? npc, int maxExpansions = 4500, bool allowWarpExit = false)
    {
        if (start == goal) return new Stack<Point>();
        if (!IsTileWalkable(loc, goal, npc, ignoreDynamicCharacters: true, allowWarpTile: allowWarpExit))
        {
            goal = GetAdjacentWalkableTile(loc, goal, start, npc);
            if (start == goal) return new Stack<Point>();
        }

        _aStarQueue.Clear();
        _aStarCameFrom.Clear();
        _aStarCostSoFar.Clear();
        _aStarPathBuffer.Clear();

        _aStarQueue.Enqueue(start, 0);
        _aStarCameFrom[start] = start;
        _aStarCostSoFar[start] = 0;

        int expansions = 0;
        bool found = false;

        while (_aStarQueue.Count > 0 && expansions++ < maxExpansions)
        {
            Point current = _aStarQueue.Dequeue();
            if (current == goal) { found = true; break; }

            int currentCost = _aStarCostSoFar[current];
            Point prev = _aStarCameFrom[current];
            int prevDx = current.X - prev.X;
            int prevDy = current.Y - prev.Y;

            for (int i = 0; i < 4; i++)
            {
                Point next = new(current.X + _cardinalDirs[i].X, current.Y + _cardinalDirs[i].Y);
                if (next != goal && !IsTileWalkable(loc, next, npc, ignoreDynamicCharacters: true, allowWarpTile: allowWarpExit))
                    continue;

                int turnPenalty = (current != start && (_cardinalDirs[i].X != prevDx || _cardinalDirs[i].Y != prevDy)) ? 3 : 0;
                int floorBonus = (loc.terrainFeatures != null && loc.terrainFeatures.TryGetValue(new Vector2(next.X, next.Y), out var tf) && tf is Flooring) ? -2 : 0;
                int warpBufferPenalty = (!allowWarpExit && IsAdjacentToWarp(loc, next)) ? 80 : 0;
                int newCost = currentCost + 10 + turnPenalty + floorBonus + warpBufferPenalty;

                if (!_aStarCostSoFar.TryGetValue(next, out int existingCost) || newCost < existingCost)
                {
                    _aStarCostSoFar[next] = newCost;
                    int heuristic = (Math.Abs(goal.X - next.X) + Math.Abs(goal.Y - next.Y)) * 10;
                    _aStarQueue.Enqueue(next, newCost + heuristic);
                    _aStarCameFrom[next] = current;
                }
            }
        }

        if (!found)
        {
            Point closest = start;
            int closestDist = Math.Abs(goal.X - start.X) + Math.Abs(goal.Y - start.Y);
            foreach (var kvp in _aStarCameFrom)
            {
                int d = Math.Abs(goal.X - kvp.Key.X) + Math.Abs(goal.Y - kvp.Key.Y);
                if (d < closestDist) { closestDist = d; closest = kvp.Key; }
            }
            if (closest == start) return null;
            goal = closest;
        }

        Point curr = goal;
        while (curr != start)
        {
            _aStarPathBuffer.Add(curr);
            curr = _aStarCameFrom[curr];
        }

        // Alineación Consciente del Eje: evita retrocesos en línea recta y protege giros de 90°
        if (npc != null && _aStarPathBuffer.Count > 0)
        {
            Point firstStep = _aStarPathBuffer[^1];
            int stepDx = firstStep.X - start.X;
            int stepDy = firstStep.Y - start.Y;
            Rectangle bbox = npc.GetBoundingBox();
            Rectangle startRect = new(start.X * 64, start.Y * 64, 64, 64);
            startRect.Inflate(-2, 0);
            bool needsPerpendicularAlign =
                (stepDx != 0 && (bbox.Top < startRect.Top || bbox.Bottom > startRect.Bottom)) ||
                (stepDy != 0 && (bbox.Left < startRect.Left || bbox.Right > startRect.Right));
            if (needsPerpendicularAlign)
                _aStarPathBuffer.Add(start);
        }

        var stack = new Stack<Point>(_aStarPathBuffer.Count);
        for (int i = 0; i < _aStarPathBuffer.Count; i++)
            stack.Push(_aStarPathBuffer[i]);

        return stack;
    }

    public static Point GetFollowerTargetTile(GameLocation loc, Farmer player, Point npcTile, NPC? npc = null)
    {
        Point pTile = player.TilePoint;
        ulong now = (ulong)Game1.ticks;
        long ownerId = player.UniqueMultiplayerID;

        Point[] preferredOffsets = player.FacingDirection switch
        {
            0 => new Point[] { new(-1, 1), new(1, 1), new(0, 2), new(-2, 1), new(2, 1), new(-1, 0), new(1, 0), new(0, 1) },
            1 => new Point[] { new(-1, -1), new(-1, 1), new(-2, 0), new(-2, -1), new(-2, 1), new(0, -1), new(0, 1), new(-1, 0) },
            2 => new Point[] { new(-1, -1), new(1, -1), new(0, -2), new(-2, -1), new(2, -1), new(-1, 0), new(1, 0), new(0, -1) },
            3 => new Point[] { new(1, -1), new(1, 1), new(2, 0), new(2, -1), new(2, 1), new(0, -1), new(0, 1), new(1, 0) },
            _ => new Point[] { new(-1, 1), new(1, 1), new(0, 1), new(1, 0), new(-1, 0) }
        };

        var takenByOthers = new HashSet<Point>();
        foreach (var kvp in _reservedFollowSlots)
        {
            if (npc != null && kvp.Key.OwnerId == ownerId && kvp.Key.NpcName.Equals(npc.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (kvp.Value.Loc == loc.NameOrUniqueName && (now - kvp.Value.Tick) < 120)
                takenByOthers.Add(kvp.Value.Tile);
        }

        foreach (var off in preferredOffsets)
        {
            Point candidate = new(pTile.X + off.X, pTile.Y + off.Y);
            if (!takenByOthers.Contains(candidate) && IsTileWalkable(loc, candidate, npc, ignoreDynamicCharacters: false, ignoreSpecificFarmer: player))
            {
                if (npc != null) _reservedFollowSlots[(ownerId, npc.Name)] = (loc.NameOrUniqueName, candidate, now);
                return candidate;
            }
        }

        Point fallback = GetAdjacentWalkableTile(loc, pTile, npcTile, npc);
        if (npc != null) _reservedFollowSlots[(ownerId, npc.Name)] = (loc.NameOrUniqueName, fallback, now);
        return fallback;
    }

    private static int ComputeHumanSpeed(NpcJob job, NPC npc, GameLocation loc, bool isTrackingJob)
    {
        bool isIndoors = !loc.IsOutdoors || loc is FarmHouse || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase);
        int baseSpeed = 2;
        if (isTrackingJob)
        {
            var owner = GetJobFarmer(job);
            float dist = owner != null ? Vector2.Distance(npc.Tile, owner.Tile) : 3f;
            if (isIndoors)
                baseSpeed = dist > 8.5f ? 3 : 2;
            else
                baseSpeed = dist <= 3.0f ? 2 : dist <= 6.0f ? 3 : dist <= 12.0f ? 4 : 5;
        }

        var path = npc.controller?.pathToEndPoint;
        if (path != null && path.Count > 0)
        {
            if (path.Count <= 2)
                return 2;

            // Detecta cambio de dirección de 90° respecto a la orientación actual del NPC
            Point next = path.Peek();
            int dx = next.X - npc.TilePoint.X;
            int dy = next.Y - npc.TilePoint.Y;
            bool facingHorizontal = npc.FacingDirection is 1 or 3;
            bool facingVertical = npc.FacingDirection is 0 or 2;
            if ((facingHorizontal && dy != 0) || (facingVertical && dx != 0))
                return 2;
        }

        return baseSpeed;
    }

    public static void StopNpcCleanly(NPC npc)
    {
        if (npc == null) return;
        npc.controller = null;
        npc.Halt();
        npc.speed = 2;
    }

    public static void CompleteJob(NpcJob job, NPC npc)
    {
        if (job != null) job.Done = true;
        StopNpcCleanly(npc);
    }

    public static Point GetDefaultLocationEntry(GameLocation loc, NPC? npc = null)
    {
        if (loc is Farm farm)
            return GetFarmSafePorchTile(farm);
        if (loc.warps != null && loc.warps.Count > 0)
            return new Point(loc.warps[0].X, loc.warps[0].Y);
        return new Point(10, 10);
    }

    private static void MoveNpcTowards(NpcJob job, NPC npc, GameLocation loc, Point destination, ulong gameTick, bool allowWarpExit = false)
    {
        if (npc == null || loc == null) return;
        AutoOpenNearbyGate(loc, npc);
        if (!IsTileWalkable(loc, destination, npc, ignoreDynamicCharacters: true, allowWarpTile: allowWarpExit))
            destination = GetAdjacentWalkableTile(loc, destination, npc.TilePoint, npc);

        bool isTrackingJob = job.Kind is JobKind.Follow or JobKind.EscortHome or JobKind.ComeHere;
        npc.speed = ComputeHumanSpeed(job, npc, loc, isTrackingJob);

        if (npc.TilePoint == destination)
        {
            StopNpcCleanly(npc);
            return;
        }

        ulong lastTick = job.Data.TryGetValue("lastPathTick", out var lt) && lt is ulong u ? u : 0UL;
        Point lastDest = job.Data.TryGetValue("lastDest", out var ld) && ld is Point pt ? pt : new Point(-999, -999);
        bool hasActivePath = npc.controller?.pathToEndPoint != null && npc.controller.pathToEndPoint.Count > 0;

        if (isTrackingJob)
        {
            if (hasActivePath)
            {
                int delta = Math.Abs(destination.X - lastDest.X) + Math.Abs(destination.Y - lastDest.Y);
                if (delta <= 1) return;
                if (gameTick - lastTick < 18) return;
            }
            else if (gameTick - lastTick < 10)
            {
                return;
            }
        }
        else
        {
            if (hasActivePath && destination == lastDest) return;
            if (!hasActivePath && destination == lastDest && (gameTick - lastTick) < 12) return;
        }

        job.Data["lastPathTick"] = gameTick;
        job.Data["lastDest"] = destination;
        Stack<Point>? path = FindCustomPath(loc, npc.TilePoint, destination, npc, 4500, allowWarpExit);
        if (path != null && path.Count > 0)
        {
            // OBLIGATORIO: Halt() antes de reemplazar el controlador para impedir fuga vertical (moveUp)
            npc.Halt();
            npc.controller = new PathFindController(path, loc, npc, destination)
            {
                nonDestructivePathing = true
            };
        }
        else
        {
            StopNpcCleanly(npc);
        }
    }

    public enum HumanNavResult { Walking, Arrived, Unreachable }

    public static HumanNavResult HumanNavigateToTarget(
        NpcJob job,
        NPC npc,
        GameLocation loc,
        Point targetTile,
        ulong gameTick,
        int arrivalChebyshev = 1,
        int maxStuckTicks = 240,
        int tickStep = 6)
    {
        int cheb = Math.Max(Math.Abs(npc.TilePoint.X - targetTile.X), Math.Abs(npc.TilePoint.Y - targetTile.Y));
        if (cheb <= arrivalChebyshev)
        {
            StopNpcCleanly(npc);
            npc.faceGeneralDirection(new Vector2(targetTile.X * 64 + 32, targetTile.Y * 64 + 32));
            job.Data["nav_stuck_ticks"] = 0;
            job.Data["nav_last_pixel"] = npc.Position;
            return HumanNavResult.Arrived;
        }

        int stuck = job.Data.TryGetValue("nav_stuck_ticks", out var st) && st is int s ? s : 0;
        Vector2 lastPx = job.Data.TryGetValue("nav_last_pixel", out var lp) && lp is Vector2 v ? v : npc.Position;
        if (Vector2.DistanceSquared(npc.Position, lastPx) < 0.25f)
            stuck += tickStep;
        else
        {
            stuck = Math.Max(0, stuck - tickStep * 2);
            job.Data["nav_last_pixel"] = npc.Position;
        }
        job.Data["nav_stuck_ticks"] = stuck;

        if (stuck == maxStuckTicks / 2)
        {
            StopNpcCleanly(npc);
            job.Data["lastPathTick"] = 0UL;
            job.Data["lastDest"] = new Point(-999, -999);
        }
        else if (stuck >= maxStuckTicks)
        {
            StopNpcCleanly(npc);
            job.Data["nav_stuck_ticks"] = 0;
            return HumanNavResult.Unreachable;
        }

        Point standTile = arrivalChebyshev == 0
            ? targetTile
            : GetAdjacentWalkableTile(loc, targetTile, npc.TilePoint, npc);
        MoveNpcTowards(job, npc, loc, standTile, gameTick);
        return HumanNavResult.Walking;
    }

    public static void RestoreNpcToVanillaState(NPC npc, long ownerPlayerId = 0, bool resumeSchedule = true)
    {
        if (npc == null) return;
        if (ownerPlayerId != 0)
            ReleaseFollowSlot(ownerPlayerId, npc.Name);
        else
        {
            foreach (var k in _reservedFollowSlots.Keys)
                if (k.NpcName.Equals(npc.Name, StringComparison.OrdinalIgnoreCase))
                    _reservedFollowSlots.TryRemove(k, out _);
        }

        StopNpcCleanly(npc);
        npc.Sprite?.StopAnimation();
        if (npc.isSleeping.Value)
            npc.isSleeping.Value = false;

        if (resumeSchedule)
        {
            npc.followSchedule = true;
            try { npc.checkSchedule(Game1.timeOfDay); } catch { /* safe fallback */ }
        }
    }
}
