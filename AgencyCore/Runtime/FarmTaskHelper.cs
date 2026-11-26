namespace ValleyBuddies.AgencyCore;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Locations;
using ValleyBuddies.Execution;

public static class FarmTaskHelper
{
    /// <summary>
    /// Merges an item into a carrier's pending list (or QID stack map when provided), preserving quality, color, and stack caps.
    /// </summary>
    public static void AddMergedPendingItem(List<Item> pendingItems, Item? item, Dictionary<string, int>? pendingByQid = null)
    {
        if (item == null || item.Stack <= 0) return;

        if (pendingByQid != null && item.GetType() == typeof(StardewValley.Object) && item.Quality == 0)
        {
            string qid = item.QualifiedItemId;
            int cur = pendingByQid.TryGetValue(qid, out int existingQty) ? existingQty : 0;
            pendingByQid[qid] = cur + item.Stack;
            return;
        }

        foreach (var existing in pendingItems)
        {
            if (existing != null && existing.canStackWith(item))
            {
                int rem = existing.addToStack(item);
                if (rem <= 0)
                    return;
                item.Stack = rem;
            }
        }
        pendingItems.Add(item);
    }

    public static void DeliverItemCoopSafe(JobExecutionContext ctx, Farmer? player, Item? item)
    {
        if (player == null || item == null) return;

        if (player.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
        {
            ctx.World.GiveItemToFarmerDirectly(player, item);
            return;
        }

        if (item.GetType() == typeof(StardewValley.Object) && item.Quality == 0 && ModEntry.MultiplayerSync != null)
        {
            ModEntry.MultiplayerSync.SendSpouseGift(player.UniqueMultiplayerID, item.QualifiedItemId, item.Stack, ctx.Npc?.Name ?? string.Empty);
            return;
        }

        var dropLoc = player.currentLocation ?? ctx.Npc?.currentLocation;
        if (dropLoc != null)
        {
            Vector2 origin = player.Position != Vector2.Zero ? player.Position : (ctx.Npc?.Position ?? Vector2.Zero);
            dropLoc.debris.Add(new Debris(item, origin));
        }
    }

    public static bool IsAnimalProductObject(GameLocation? loc, StardewValley.Object? obj)
    {
        if (obj == null || IsProtectedPlayerObject(obj)) return false;

        if (obj.Category == StardewValley.Object.EggCategory
            || obj.QualifiedItemId is "(O)430" or "(O)440" or "(O)442" or "(O)444" or "(O)446" or "(O)107" or "(O)289" or "(O)305" or "(O)928")
        {
            return true;
        }

        if (loc is AnimalHouse && obj.IsSpawnedObject)
            return true;

        return obj.IsSpawnedObject && obj.Name.Contains("Egg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReadyForToolHarvest(FarmAnimal? animal)
    {
        if (animal == null || string.IsNullOrWhiteSpace(animal.currentProduce.Value))
            return false;

        return animal.GetAnimalData()?.HarvestType == FarmAnimalHarvestType.HarvestWithTool
            || animal.GetHarvestType() == FarmAnimalHarvestType.HarvestWithTool;
    }

    public static Item? TryHarvestAnimalProduce(FarmAnimal? animal, GameLocation? loc)
    {
        if (animal == null || !IsReadyForToolHarvest(animal))
            return null;

        string prodId = animal.currentProduce.Value;
        string qid = prodId.StartsWith("(") ? prodId : $"(O){prodId}";
        Item? item = ItemRegistry.Create(qid, 1, allowNull: true);
        if (item == null)
            return null;

        item.Quality = animal.produceQuality.Value;
        string harvestTool = animal.GetAnimalData()?.HarvestTool ?? string.Empty;
        string sound = (harvestTool.Contains("Shear", StringComparison.OrdinalIgnoreCase) || qid == "(O)440")
            ? "scissors"
            : "Milking";

        animal.currentProduce.Value = null;
        try { animal.ReloadTextureIfNeeded(); } catch { }
        try { loc?.playSound(sound); } catch { }
        return item;
    }

    public static Point FindSafeAnimalHouseEntryTile(AnimalHouse ah, NPC? npc)
    {
        if (ah?.map?.Layers == null || ah.map.Layers.Count == 0)
            return new Point(4, 6);

        int mapW = ah.map.Layers[0].LayerWidth;
        int mapH = ah.map.Layers[0].LayerHeight;
        int maxAllowedY = mapH - 3;
        Point basePt = new Point(mapW / 2, Math.Max(2, mapH - 3));

        if (ah.warps != null && ah.warps.Count > 0)
        {
            var w = ah.warps.FirstOrDefault(wp => wp.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? ah.warps[0];
            maxAllowedY = w.Y - 2;
            basePt = new Point(w.X, Math.Max(2, maxAllowedY));
        }

        Point bestTwoNeighbors = Point.Zero;
        float bestTwoDist = float.MaxValue;
        Point bestSingle = Point.Zero;
        float bestSingleDist = float.MaxValue;

        for (int dy = 0; dy >= -3; dy--)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                Point cand = new Point(basePt.X + dx, Math.Max(2, basePt.Y + dy));
                if (cand.Y > maxAllowedY) continue;
                if (!NpcActionExecutor.IsTileWalkable(ah, cand, npc, ignoreDynamicCharacters: true, allowWarpTile: false))
                    continue;

                int interiorWalkableNeighbors = 0;
                Point[] neighbors = new[]
                {
                    new Point(cand.X, cand.Y - 1),
                    new Point(cand.X - 1, cand.Y),
                    new Point(cand.X + 1, cand.Y),
                    new Point(cand.X, cand.Y + 1)
                };
                foreach (var n in neighbors)
                {
                    if (n.Y <= maxAllowedY && NpcActionExecutor.IsTileWalkable(ah, n, npc, ignoreDynamicCharacters: true, allowWarpTile: false))
                        interiorWalkableNeighbors++;
                }

                float d = (cand.X - basePt.X) * (cand.X - basePt.X) + (cand.Y - basePt.Y) * (cand.Y - basePt.Y);
                if (interiorWalkableNeighbors >= 2 && d < bestTwoDist)
                {
                    bestTwoDist = d;
                    bestTwoNeighbors = cand;
                }
                else if (d < bestSingleDist)
                {
                    bestSingleDist = d;
                    bestSingle = cand;
                }
            }
        }

        if (bestTwoNeighbors != Point.Zero)
            return bestTwoNeighbors;
        if (bestSingle != Point.Zero)
            return bestSingle;

        Point cardinal = NpcActionExecutor.FindClosestWalkableCardinalNeighbor(ah, basePt, npc, preferCloserTo: new Point(basePt.X, Math.Max(2, basePt.Y - 1)));
        if (cardinal != Point.Zero)
            return cardinal;

        return new Point(mapW / 2, Math.Max(2, mapH - 3));
    }
    /// <summary>
    /// Returns true if the object is a player-placed or functional entity (big craftable, chest, fence, sign, or machine holding an item)
    /// that must never be targeted for debris/stone/wood destruction.
    /// </summary>
    public static bool IsProtectedPlayerObject(StardewValley.Object? obj)
    {
        if (obj == null) return true;
        if (obj.bigCraftable.Value) return true;
        if (obj is StardewValley.Objects.Chest) return true;
        if (obj is StardewValley.Fence) return true;
        if (obj is StardewValley.Objects.Sign) return true;
        if (obj.heldObject.Value != null) return true;
        return false;
    }

    private sealed class ChestFallbackWorldService : IFarmWorldService
    {
        private readonly StardewValley.Objects.Chest _chest;
        private readonly IFarmWorldService _inner;
        private readonly Farmer? _fallbackFarmer;

        public ChestFallbackWorldService(StardewValley.Objects.Chest chest, IFarmWorldService inner, Farmer? fallbackFarmer)
        {
            _chest = chest;
            _inner = inner;
            _fallbackFarmer = fallbackFarmer;
        }

        public StardewValley.Objects.Chest? FindNearestChest(GameLocation loc, Vector2 fromTile, float maxRadius) => _inner.FindNearestChest(loc, fromTile, maxRadius);
        public (StardewValley.Objects.Chest chest, Vector2 tile)? FindNearestChestWithTile(GameLocation loc, Vector2 fromTile, float maxRadius) => _inner.FindNearestChestWithTile(loc, fromTile, maxRadius);
        public List<(StardewValley.Objects.Chest chest, Vector2 tile)> FindAllChests(GameLocation loc) => _inner.FindAllChests(loc);
        public bool AddItemToChest(StardewValley.Objects.Chest chest, Item item) => _inner.AddItemToChest(chest, item);
        public bool IsSafePlayableFarmTile(GameLocation loc, Vector2 tile, NPC? npc = null) => _inner.IsSafePlayableFarmTile(loc, tile, npc);
        public bool TryBreakAdjacentBlockingDebris(NPC npc, GameLocation loc, Farmer? owner, Vector2 desiredTargetTile) => _inner.TryBreakAdjacentBlockingDebris(npc, loc, owner, desiredTargetTile);
        public Item? TryCreateItemByName(string name, int quantity = 1) => _inner.TryCreateItemByName(name, quantity);

        public void GiveItemToFarmerDirectly(Farmer? player, Item? item)
        {
            if (item == null) return;
            if (_inner.AddItemToChest(_chest, item))
                return;
            if (_fallbackFarmer != null)
                _inner.GiveItemToFarmerDirectly(_fallbackFarmer, item);
        }
    }

    /// <summary>
    /// Rescues any pending harvested crops or gathered materials before a farm task aborts or ends the day.
    /// </summary>
    public static void RescuePendingItemsOnAbort(JobExecutionContext ctx, FarmWorkerStateBase state)
    {
        if (state is IPendingItemCarrierState carrier && ctx.Npc != null)
        {
            var owner = ctx.Owner;
            if (owner != null)
            {
                carrier.DeliverToPlayerOnArrival = true;
                carrier.DeliverPendingItems(ctx.Npc, owner, ctx.World);
                carrier.DeliverToPlayerOnArrival = false;
            }
            else
            {
                var farm = Game1.getFarm();
                var chestInfo = farm != null
                    ? ctx.World.FindNearestChestWithTile(farm, ctx.Npc.Tile, 500f)
                    : null;
                var fallbackFarmer = Game1.MasterPlayer ?? Game1.player;
                if (fallbackFarmer != null)
                {
                    IFarmWorldService targetWorld = chestInfo != null
                        ? new ChestFallbackWorldService(chestInfo.Value.chest, ctx.World, fallbackFarmer)
                        : ctx.World;
                    carrier.DeliverToPlayerOnArrival = true;
                    carrier.DeliverPendingItems(ctx.Npc, fallbackFarmer, targetWorld);
                    carrier.DeliverToPlayerOnArrival = false;
                }
            }
        }
        if (ctx.Npc != null)
        {
            ResetTaskStuckState(state, ctx.Npc);
        }
    }

    public static GameLocation? EnsureOutdoorWorkLocation(JobExecutionContext ctx, int startEmote = 32)
    {
        var npc = ctx.Npc;
        var loc = npc.currentLocation;
        var player = ctx.Owner ?? Game1.player;

        if (loc != null && (loc.IsOutdoors || loc.IsGreenhouse || loc.NameOrUniqueName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)))
        {
            return loc;
        }

        if (loc is StardewValley.Locations.FarmHouse || (loc != null && loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase)))
        {
            var farm = Game1.getFarm();
            if (farm != null && !string.Equals(loc.NameOrUniqueName, farm.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Point door = ctx.Navigator.GetOwnerHomeExteriorDoorTile(farm, player);
                Point outside = new Point(door.X, door.Y + 1);
                Point spawn = ctx.Navigator.IsTileWalkable(farm, outside, npc)
                    ? outside
                    : ctx.Navigator.GetAdjacentWalkableTile(farm, door, door, npc);
                ctx.Navigator.LogWarpAudit($"{ctx.Job.Kind}_LeaveInterior", npc, farm, spawn);
                Game1.warpCharacter(npc, farm, new Vector2(spawn.X, spawn.Y));
                npc.controller = null;
                ctx.Navigator.StopCleanly(npc);
                npc.doEmote(startEmote);
                return farm;
            }
        }

        if (loc == null)
        {
            var fallback = Game1.getFarm() ?? player?.currentLocation;
            if (fallback != null)
            {
                Point spawn;
                if (fallback is Farm f)
                {
                    Point door = ctx.Navigator.GetOwnerHomeExteriorDoorTile(f, player);
                    Point outside = new Point(door.X, door.Y + 1);
                    spawn = ctx.Navigator.IsTileWalkable(f, outside, npc)
                        ? outside
                        : ctx.Navigator.GetAdjacentWalkableTile(f, door, door, npc);
                }
                else
                {
                    spawn = ctx.Navigator.GetCrossMapSpawnTile(fallback, player?.TilePoint ?? new Point(10, 10), player?.FacingDirection ?? 2);
                }
                ctx.Navigator.LogWarpAudit($"{ctx.Job.Kind}_NullLocationRecovery", npc, fallback, spawn);
                Game1.warpCharacter(npc, fallback, new Vector2(spawn.X, spawn.Y));
                npc.controller = null;
                ctx.Navigator.StopCleanly(npc);
                return fallback;
            }
        }

        return loc;
    }

    public static void EnsureNpcOnFreeGround(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc)
    {
        if (ctx == null || ctx.Npc == null || loc == null) return;
        if (ctx.Job.Kind == JobKind.Follow) return;
        if (state.CheckedFreeGround) return;
        state.CheckedFreeGround = true;

        var npc = ctx.Npc;
        var player = ctx.Owner ?? Game1.player;
        Vector2 curTile = npc.Tile;
        if (loc.Objects != null && loc.Objects.TryGetValue(curTile, out var obj) && obj != null && !obj.isPassable())
        {
            if (loc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(npc, loc, player, curTile))
            {
                return;
            }

            Point p = npc.TilePoint;
            Point[] cardinals = new Point[]
            {
                new(p.X, p.Y - 1),
                new(p.X, p.Y + 1),
                new(p.X - 1, p.Y),
                new(p.X + 1, p.Y)
            };

            Point best = Point.Zero;
            float bestDist = float.MaxValue;
            Point targetRef = (player != null && player.currentLocation == loc && Vector2.Distance(npc.Tile, player.Tile) <= 2.5f)
                ? player.TilePoint
                : p;

            foreach (var cand in cardinals)
            {
                Vector2 cVec = new Vector2(cand.X, cand.Y);
                if (loc.isTilePassable(new xTile.Dimensions.Location(cand.X, cand.Y), Game1.viewport)
                    && (!loc.Objects.TryGetValue(cVec, out var candObj) || candObj == null || candObj.isPassable())
                    && (loc.terrainFeatures == null || !loc.terrainFeatures.TryGetValue(cVec, out var tf) || tf == null || tf.isPassable(Game1.player)))
                {
                    float d = (cand.X - targetRef.X) * (cand.X - targetRef.X) + (cand.Y - targetRef.Y) * (cand.Y - targetRef.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = cand;
                    }
                }
            }

            if (best != Point.Zero)
            {
                npc.setTilePosition(best);
                npc.Position = new Vector2(best.X * 64f, best.Y * 64f);
                ctx.Navigator.StopCleanly(npc);
                npc.controller = null;
                ModEntry.ModMonitor?.Log($"[FarmStep] Shifted {npc.Name} off solid obstacle at {curTile} to adjacent free tile {best}", LogLevel.Debug);
            }
        }
    }

    public static bool TryMonitorAndRescueStuckWorker(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc, Vector2 targetObjTile, int rescueThreshold = 90)
    {
        var npc = ctx.Npc;
        Vector2 lastPos = state.LastPixel == Vector2.Zero ? npc.Position : state.LastPixel;

        if (state.TrackedTarget != targetObjTile)
        {
            state.TrackedTarget = targetObjTile;
            state.StuckTicks = 0;
            state.LastPixel = npc.Position;
            return false;
        }

        Point currentTile = npc.TilePoint;
        Point lastTile = state.StuckLastTile == Point.Zero ? currentTile : state.StuckLastTile;
        Point prevTile = state.StuckPrevTile == Point.Zero ? currentTile : state.StuckPrevTile;

        bool oscillating = false;
        if (currentTile != lastTile)
        {
            oscillating = (currentTile == prevTile);
            state.StuckPrevTile = lastTile;
            state.StuckLastTile = currentTile;
        }

        float movedSq = Vector2.DistanceSquared(npc.Position, lastPos);
        if (movedSq < 36.0f || oscillating)
        {
            state.StuckTicks += 15;
        }
        else
        {
            state.StuckTicks = Math.Max(0, state.StuckTicks - 15);
            state.LastPixel = npc.Position;
        }

        if (state.StuckTicks == 45)
        {
            ClearActiveRoute(state);
            ctx.Navigator.StopCleanly(npc);
            ctx.Job.Data["lastPathTick"] = 0UL;
            return false;
        }

        if (state.StuckTicks >= rescueThreshold)
        {
            state.StuckTicks = 0;
            ClearActiveRoute(state);
            ctx.Navigator.StopCleanly(npc);
            ctx.Job.Data["lastPathTick"] = 0UL;
            return true;
        }

        return false;
    }

    public static void ResetTaskStuckState(FarmWorkerStateBase state, NPC npc)
    {
        state.StuckTicks = 0;
        state.LastPixel = npc.Position;
        ClearActiveRoute(state);
    }

    public static void ClearActiveRoute(FarmWorkerStateBase state)
    {
        state.ActiveRoute = null;
        state.ActiveRouteGoal = null;
        state.ActiveRouteIndex = 0;
    }

    /// <summary>
    /// Selecciona el mejor objetivo alcanzable evitando agrupar fallos en el mismo sector/acantilado:
    /// penaliza sectores con fallos previos, prioriza cercanía al NPC y al jugador (si comparte mapa),
    /// verifica ruta completa con mayor presupuesto A* (hasta 14,000 expansiones para cruzar escaleras/terrazas)
    /// y cachea la ruta resultante para caminar por waypoints intermedios.
    /// </summary>
    public static Vector2? SelectBestReachableTarget(
        JobExecutionContext ctx,
        FarmWorkerStateBase state,
        GameLocation loc,
        IReadOnlyList<Vector2> candidates,
        IReadOnlyList<Vector2>? failedList,
        Farmer? player,
        out bool failedReachabilityCheck)
    {
        failedReachabilityCheck = false;
        if (candidates == null || candidates.Count == 0)
            return null;

        Vector2 npcTile = ctx.Npc.Tile;
        bool hasPlayer = player != null && player.currentLocation?.NameOrUniqueName == loc.NameOrUniqueName;
        Vector2 playerTile = hasPlayer ? player!.Tile : npcTile;
        bool hasFailures = failedList != null && failedList.Count > 0;

        float ScoreCandidate(Vector2 c)
        {
            float distNpc = Vector2.Distance(npcTile, c);
            float distPlayer = hasPlayer ? Vector2.Distance(playerTile, c) : distNpc;
            float score = distNpc + (hasPlayer ? (hasFailures ? 0.45f : 0.25f) * distPlayer : 0f);

            // Bonus leve si está en el mismo rango de elevación y aún no hay fallos
            if (!hasFailures && Math.Abs(c.Y - npcTile.Y) <= 10 && Math.Abs(c.X - npcTile.X) <= 18)
                score -= 3.5f;

            // Fuerte penalización a sectores cercanos a objetivos ya fallidos (evita chocar 6 veces contra el mismo barranco)
            if (hasFailures)
            {
                int nearFailCount = 0;
                foreach (var f in failedList!)
                {
                    if (Vector2.Distance(c, f) <= 12f)
                        nearFailCount++;
                }
                if (nearFailCount > 0)
                    score += 1000f * nearFailCount;
            }

            return score;
        }

        var ordered = candidates.OrderBy(ScoreCandidate).ToList();
        var testedUnreachableSectors = new List<Vector2>(8);
        int sectorTests = 0;
        const int maxDistinctSectorTests = 8;

        foreach (var cand in ordered)
        {
            // Omitir otros candidatos pegados a un sector recién comprobado como inalcanzable
            bool inSkippedSector = false;
            foreach (var bad in testedUnreachableSectors)
            {
                if (Vector2.Distance(cand, bad) <= 8f)
                {
                    inSkippedSector = true;
                    break;
                }
            }
            if (inSkippedSector)
                continue;

            sectorTests++;
            Point goalPt = new Point((int)cand.X, (int)cand.Y);
            var route = ctx.Navigator.FindCompleteRoute(loc, ctx.Npc.TilePoint, goalPt, ctx.Npc, maxExpansions: 14000);
            if (route != null)
            {
                state.ActiveRoute = route;
                state.ActiveRouteGoal = cand;
                state.ActiveRouteIndex = route.Count > 22 ? 17 : Math.Max(0, route.Count - 1);
                return cand;
            }

            testedUnreachableSectors.Add(cand);
            if (sectorTests >= maxDistinctSectorTests)
                break;
        }

        // Respaldo de terraza: si el jugador está en otra zona del mismo mapa junto a objetivos,
        // comprobar si existe ruta completa hacia el jugador (para bajar por escaleras primero).
        if (hasPlayer && Vector2.Distance(npcTile, playerTile) > 10f)
        {
            var routeToPlayer = ctx.Navigator.FindCompleteRoute(loc, ctx.Npc.TilePoint, player!.TilePoint, ctx.Npc, maxExpansions: 14000);
            if (routeToPlayer != null && routeToPlayer.Count > 0)
            {
                Vector2 nearPlayerCand = ordered.OrderBy(c => Vector2.Distance(playerTile, c)).First();
                if (Vector2.Distance(playerTile, nearPlayerCand) <= 24f)
                {
                    state.ActiveRoute = routeToPlayer;
                    state.ActiveRouteGoal = nearPlayerCand;
                    state.ActiveRouteIndex = routeToPlayer.Count > 22 ? 17 : Math.Max(0, routeToPlayer.Count - 1);
                    return nearPlayerCand;
                }
            }
        }

        failedReachabilityCheck = true;
        return null;
    }

    /// <summary>
    /// Navega usando checkpoints fijos cada ~18 casillas sobre la ruta verificada por escaleras/terrazas,
    /// manteniendo estable el destino en MoveTowards (cero micro-pausas por recálculo) mientras garantiza
    /// que cada tramo quede muy por debajo del tope de 4,500 expansiones de A*.
    /// </summary>
    public static void NavigateAlongRouteOrDirect(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc, Vector2 targetTile)
    {
        const int checkpointStride = 18;
        Point goalPt = new Point((int)targetTile.X, (int)targetTile.Y);
        float directDist = Vector2.Distance(ctx.Npc.Tile, targetTile);

        // Si el objetivo cambió o no hay ruta cacheada para recorridos largos / de granja, calcular ruta por escaleras
        if (state.ActiveRouteGoal != targetTile || state.ActiveRoute == null)
        {
            if (loc is Farm || directDist > 12f)
            {
                var route = ctx.Navigator.FindCompleteRoute(loc, ctx.Npc.TilePoint, goalPt, ctx.Npc, maxExpansions: 14000);
                if (route != null && route.Count > 0)
                {
                    state.ActiveRoute = route;
                    state.ActiveRouteGoal = targetTile;
                    state.ActiveRouteIndex = route.Count > 22 ? checkpointStride - 1 : route.Count - 1;
                }
                else
                {
                    // Guardar lista vacía para evitar recalcular A* de 14k expansiones cada 15 ticks hasta Reset/Stuck45
                    state.ActiveRoute = new List<Point>();
                    state.ActiveRouteGoal = targetTile;
                    state.ActiveRouteIndex = 0;
                }
            }
            else
            {
                ClearActiveRoute(state);
            }
        }

        if (state.ActiveRoute != null && state.ActiveRoute.Count > 0)
        {
            Point npcPt = ctx.Npc.TilePoint;
            if (state.ActiveRouteIndex >= state.ActiveRoute.Count)
                state.ActiveRouteIndex = state.ActiveRoute.Count - 1;

            Point checkpoint = state.ActiveRoute[state.ActiveRouteIndex];
            int cheb = Math.Max(Math.Abs(npcPt.X - checkpoint.X), Math.Abs(npcPt.Y - checkpoint.Y));
            if (cheb <= 3)
            {
                if (state.ActiveRouteIndex < state.ActiveRoute.Count - 1)
                {
                    state.ActiveRouteIndex = Math.Min(state.ActiveRouteIndex + checkpointStride, state.ActiveRoute.Count - 1);
                    checkpoint = state.ActiveRoute[state.ActiveRouteIndex];
                }
                else if (Vector2.Distance(ctx.Npc.Tile, targetTile) > 2.5f)
                {
                    // Terminó un tramo intermedio (ej. escalera hacia jugador): re-enrutar tramo final directo al objetivo
                    var finalRoute = ctx.Navigator.FindCompleteRoute(loc, npcPt, goalPt, ctx.Npc, maxExpansions: 14000);
                    if (finalRoute != null && finalRoute.Count > 0)
                    {
                        state.ActiveRoute = finalRoute;
                        state.ActiveRouteIndex = finalRoute.Count > 22 ? checkpointStride - 1 : finalRoute.Count - 1;
                        checkpoint = finalRoute[state.ActiveRouteIndex];
                    }
                    else
                    {
                        state.ActiveRoute = new List<Point>();
                    }
                }
            }

            if (state.ActiveRoute.Count > 0 && state.ActiveRouteIndex < state.ActiveRoute.Count)
            {
                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, checkpoint, ctx.GameTick);
                return;
            }
        }

        Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(loc, goalPt, ctx.Npc.TilePoint, ctx.Npc);
        ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, walkTile, ctx.GameTick);
    }

    public static bool NotifyPathBlockedAndAbort(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc, string jobName)
    {
        var npc = ctx.Npc;
        var player = ctx.Owner ?? Game1.player;
        ctx.Navigator.StopCleanly(npc);
        string pName = player?.displayName ?? player?.Name ?? Game1.player.Name;
        string bubbleText = Utilities.I18n.Get("chore.path_blocked", new { playerName = pName });
        if (string.IsNullOrWhiteSpace(bubbleText))
            bubbleText = $"{pName}, I can't reach that spot!";

        ModEntry.ConversationBridge.ShowCompanionBubble(npc, bubbleText, 4000);
        npc.doEmote(28);
        ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} job={jobName} loc={loc.NameOrUniqueName} aborted_unreachable (all sectors blocked)", LogLevel.Debug);

        ResetTaskStuckState(state, npc);
        ctx.Job.Data.Remove("task_stuck_ticks");
        ctx.Job.Data.Remove("lastPathTick");
        ctx.Job.Data.Remove("lastDest");
        ctx.Job.Data.Remove("currentDest");
        ctx.Job.Data.Remove("targetChestTile");
        ctx.Job.Data.Remove("workTimer");
        ctx.Job.Done = true;
        return true;
    }

    public static bool TryNavigateToChest(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc, Vector2 chestTile)
    {
        if (TryMonitorAndRescueStuckWorker(ctx, state, loc, chestTile, rescueThreshold: 150))
            return false;

        NavigateAlongRouteOrDirect(ctx, state, loc, chestTile);
        return true;
    }

    public static bool HandleUnreachableTaskTargets(JobExecutionContext ctx, FarmWorkerStateBase state, GameLocation loc, List<Vector2> failedList, string jobName, Vector2 desiredTarget = default)
    {
        if (failedList == null) return false;

        var npc = ctx.Npc;
        var player = ctx.Owner ?? Game1.player;

        if (loc is Farm && desiredTarget != default)
        {
            if (ctx.World.TryBreakAdjacentBlockingDebris(npc, loc, player, desiredTarget))
            {
                state.StuckTicks = 0;
                ctx.Job.Data["task_stuck_ticks"] = 0;
                ctx.Job.Data["lastPathTick"] = 0UL;
                ctx.Job.Data["lastDest"] = new Point(-999, -999);
                return false;
            }
        }

        if (failedList.Count >= 6)
        {
            ctx.Navigator.StopCleanly(npc);
            string pName = player?.displayName ?? player?.Name ?? Game1.player.Name;
            string bubbleText = Utilities.I18n.Get("chore.path_blocked", new { playerName = pName });
            if (string.IsNullOrWhiteSpace(bubbleText))
            {
                bubbleText = $"{pName}, I can't reach that spot!";
            }
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, bubbleText, 4000);
            npc.doEmote(28);

            ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} job={jobName} loc={loc.NameOrUniqueName} aborted_unreachable (failed {failedList.Count} targets)", LogLevel.Debug);

            state.StuckTicks = 0;
            ctx.Job.Data.Remove("task_stuck_ticks");
            ctx.Job.Data.Remove("lastPathTick");
            ctx.Job.Data.Remove("lastDest");
            ctx.Job.Data.Remove("currentDest");
            ctx.Job.Data.Remove("targetChestTile");
            ctx.Job.Data.Remove("workTimer");

            ctx.Job.Done = true;
            return true;
        }

        return false;
    }

    public static bool ReturnToPlayerAfterTask(JobExecutionContext ctx, FarmWorkerStateBase state, string completionMessage)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null) return false;

        var npc = ctx.Npc;
        var npcLoc = npc.currentLocation;
        var playerLoc = player.currentLocation;
        if (npcLoc == null || playerLoc == null) return false;

        state.ReturnTicks += 15;

        if (state.ReturnTicks > 2400)
        {
            NpcActionExecutor.DeliverPendingItemsToPlayer(ctx.Job, npc, player);
            npc.controller = null;
            ctx.Navigator.StopCleanly(npc);
            npc.showTextAboveHead(completionMessage, duration: 4000);
            return false;
        }

        if (npcLoc.NameOrUniqueName != playerLoc.NameOrUniqueName)
        {
            Point spawn = ctx.Navigator.GetAdjacentWalkableTile(playerLoc, player.TilePoint, player.TilePoint, npc);
            Game1.warpCharacter(npc, playerLoc, new Vector2(spawn.X, spawn.Y));
            npc.controller = null;
            ctx.Navigator.StopCleanly(npc);
            npc.faceTowardFarmerForPeriod(3000, 3, false, player);
            npc.doEmote(20);
            NpcActionExecutor.DeliverPendingItemsToPlayer(ctx.Job, npc, player);
            npc.showTextAboveHead(completionMessage, duration: 4000);
            return false;
        }

        float dist = Vector2.Distance(npc.Tile, player.Tile);
        if (dist <= 2.2f)
        {
            npc.controller = null;
            ctx.Navigator.StopCleanly(npc);
            npc.faceTowardFarmerForPeriod(3000, 3, false, player);
            npc.doEmote(20);
            NpcActionExecutor.DeliverPendingItemsToPlayer(ctx.Job, npc, player);
            npc.showTextAboveHead(completionMessage, duration: 4000);
            try { player.changeFriendship(10, npc); } catch { }
            return false;
        }

        npc.speed = 2;
        if (dist > 12f)
        {
            NavigateAlongRouteOrDirect(ctx, state, npcLoc, player.Tile);
        }
        else
        {
            Point walkTarget = ctx.Navigator.GetAdjacentWalkableTile(npcLoc, player.TilePoint, npc.TilePoint, npc);
            ctx.Navigator.MoveTowards(ctx.Job, npc, npcLoc, walkTarget, ctx.GameTick);
        }
        return true;
    }

    public static bool IsFlowerCrop(Crop? crop)
    {
        if (crop == null) return false;
        var cropData = crop.GetData();
        string harvestId = cropData?.HarvestItemId ?? crop.indexOfHarvest.Value;
        if (string.IsNullOrWhiteSpace(harvestId)) return false;
        string qid = harvestId.StartsWith("(") ? harvestId : $"(O){harvestId}";
        return ItemRegistry.GetData(qid)?.Category == StardewValley.Object.flowersCategory;
    }

    private static List<Vector2> CollectBeeHouseTiles(GameLocation? loc)
    {
        var list = new List<Vector2>();
        if (loc?.objects == null) return list;
        foreach (var kv in loc.objects.Pairs)
        {
            if (kv.Value is StardewValley.Object obj
                && (obj.QualifiedItemId == "(BC)10"
                    || (obj.bigCraftable.Value && obj.ItemId == "10")
                    || string.Equals(obj.Name, "Bee House", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(kv.Key);
            }
        }
        return list;
    }

    private static bool IsNearAnyBeeHouse(Vector2 tile, List<Vector2> beeHouses)
    {
        for (int i = 0; i < beeHouses.Count; i++)
        {
            Vector2 bh = beeHouses[i];
            float dx = Math.Abs(bh.X - tile.X);
            float dy = Math.Abs(bh.Y - tile.Y);
            if (dx + dy <= 5.01f || Vector2.Distance(bh, tile) <= 5.25f)
                return true;
        }
        return false;
    }

    public static bool HasBeeHouseNearTile(GameLocation loc, Vector2 tile)
    {
        return IsNearAnyBeeHouse(tile, CollectBeeHouseTiles(loc));
    }

    public static Point GetHarvestStandTile(GameLocation loc, Point cropPt, Point from, NPC? npc = null)
    {
        if (loc == null) return cropPt;
        if (NpcActionExecutor.IsTileWalkable(loc, cropPt, npc))
            return cropPt;

        Point best = Point.Zero;
        float bestDist = float.MaxValue;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Point candidate = new Point(cropPt.X + dx, cropPt.Y + dy);
                if (!NpcActionExecutor.IsTileWalkable(loc, candidate, npc)) continue;
                float dist = Vector2.DistanceSquared(new Vector2(from.X, from.Y), new Vector2(candidate.X, candidate.Y));
                if (dx != 0 && dy != 0) dist += 0.25f;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
        }

        if (best != Point.Zero)
            return best;

        return NpcActionExecutor.GetAdjacentWalkableTile(loc, cropPt, from, npc);
    }

    public static (Vector2 tile, StardewValley.TerrainFeatures.HoeDirt dirt)? FindNearestReadyCrop(
        GameLocation loc,
        Vector2 from,
        int radius,
        IFarmWorldService world,
        List<Vector2>? excluded = null,
        bool includeFlowers = true,
        bool protectBeeHouseFlowers = true,
        NPC? npc = null)
    {
        if (loc == null) return null;
        (Vector2, StardewValley.TerrainFeatures.HoeDirt)? best = null;
        float bestDist = float.MaxValue;
        List<Vector2>? beeHouses = null;

        foreach (var kv in loc.terrainFeatures.Pairs)
        {
            if (excluded != null && excluded.Contains(kv.Key)) continue;
            if (kv.Value is StardewValley.TerrainFeatures.HoeDirt hd
                && hd.readyForHarvest())
            {
                if (IsFlowerCrop(hd.crop))
                {
                    if (!includeFlowers) continue;
                    if (protectBeeHouseFlowers)
                    {
                        beeHouses ??= CollectBeeHouseTiles(loc);
                        if (IsNearAnyBeeHouse(kv.Key, beeHouses)) continue;
                    }
                }

                bool reachable = loc is Farm
                    ? NpcActionExecutor.IsSafeHarvestableFarmTile(loc, kv.Key, npc)
                    : NpcActionExecutor.HasWalkableInteractionTile(loc, new Point((int)kv.Key.X, (int)kv.Key.Y), npc, allowSelfOrDiagonal: true);
                if (!reachable) continue;

                float d = Vector2.Distance(from, kv.Key);
                if (d > radius) continue;
                float score = d;
                if (excluded != null && excluded.Count > 0 && excluded.Any(f => Vector2.Distance(kv.Key, f) <= 10f))
                    score += 1000f;
                if (score < bestDist)
                {
                    bestDist = score;
                    best = (kv.Key, hd);
                }
            }
        }

        foreach (var kv in loc.objects.Pairs)
        {
            if (excluded != null && excluded.Contains(kv.Key)) continue;
            if (kv.Value is StardewValley.Objects.IndoorPot pot
                && pot.hoeDirt.Value is StardewValley.TerrainFeatures.HoeDirt hd
                && hd.readyForHarvest())
            {
                if (IsFlowerCrop(hd.crop))
                {
                    if (!includeFlowers) continue;
                    if (protectBeeHouseFlowers)
                    {
                        beeHouses ??= CollectBeeHouseTiles(loc);
                        if (IsNearAnyBeeHouse(kv.Key, beeHouses)) continue;
                    }
                }

                if (!NpcActionExecutor.HasWalkableInteractionTile(loc, new Point((int)kv.Key.X, (int)kv.Key.Y), npc, allowSelfOrDiagonal: true))
                    continue;

                float d = Vector2.Distance(from, kv.Key);
                if (d > radius) continue;
                float score = d;
                if (excluded != null && excluded.Count > 0 && excluded.Any(f => Vector2.Distance(kv.Key, f) <= 10f))
                    score += 1000f;
                if (score < bestDist)
                {
                    bestDist = score;
                    best = (kv.Key, hd);
                }
            }
        }

        return best;
    }

    public static bool MatchesTreeFilter(StardewValley.TerrainFeatures.Tree tree, string filter)
    {
        string type = tree.treeType.Value;
        return filter switch
        {
            "oak" => type is "1" or "oak",
            "maple" => type is "2" or "maple",
            "pine" => type is "3" or "pine",
            "mahogany" => type is "8" or "mahogany",
            _ => true
        };
    }

    public static string GetFetchCategoryDescription(string category)
    {
        return category?.Trim().ToLowerInvariant() switch
        {
            "wood" or "fetch_wood" => Utilities.I18n.Get("item.cat.wood"),
            "stone" or "fetch_stone" => Utilities.I18n.Get("item.cat.stone"),
            "fiber" or "fetch_fiber" => Utilities.I18n.Get("item.cat.fiber"),
            "crops" or "fetch_crops" => Utilities.I18n.Get("item.cat.crops"),
            _ => Utilities.I18n.Get("item.cat.materials")
        };
    }
}
