using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    // ─────────────── Shared farm work bridges & helpers ───────────────

    /// <summary>
    /// Safely hands an item to the farmer. If the farmer's inventory is full, drops it cleanly at their feet.
    /// </summary>
    public static void GiveItemToFarmerDirectly(Farmer? player, Item? item)
    {
        if (player == null || item == null) return;
        try
        {
            if (player.UniqueMultiplayerID != Game1.player?.UniqueMultiplayerID)
            {
                if (item.GetType() == typeof(StardewValley.Object)
                    && item.Quality == 0
                    && !string.IsNullOrWhiteSpace(item.QualifiedItemId)
                    && ModEntry.MultiplayerSync != null
                    && Context.IsMultiplayer)
                {
                    ModEntry.MultiplayerSync.SendSpouseGift(player.UniqueMultiplayerID, item.QualifiedItemId, item.Stack, "Companion");
                    return;
                }

                var remoteLoc = player.currentLocation ?? Game1.getFarm() ?? Game1.currentLocation;
                remoteLoc?.debris.Add(new Debris(item, player.Position));
                return;
            }

            if (!player.addItemToInventoryBool(item, false))
            {
                var loc = player.currentLocation ?? Game1.currentLocation;
                loc?.debris.Add(new Debris(item, player.Position));
            }
        }
        catch
        {
            try
            {
                var loc = player.currentLocation ?? Game1.currentLocation;
                loc?.debris.Add(new Debris(item, player.Position));
            }
            catch { }
        }
    }

    /// <summary>
    /// Delivers all pending gathered materials (wood, stone, fiber, crops) directly to the player upon returning from a task.
    /// </summary>
    internal static void DeliverPendingItemsToPlayer(NpcJob job, NPC npc, Farmer player)
    {
        if (job == null || player == null) return;

        if (job.TryGetState<IPendingItemCarrierState>(out var carrier))
        {
            if (carrier.DeliverToPlayerOnArrival)
            {
                carrier.DeliverToPlayerOnArrival = false;
                bool delivered = carrier.DeliverPendingItems(npc, player, Instance.FarmWorld);
                if (delivered)
                {
                    try { player.currentLocation?.playSound("coin"); } catch { }
                    try { player.changeFriendship(15, npc); } catch { }
                }
            }
            return;
        }

        if (!job.Data.TryGetValue("deliverToPlayerOnArrival", out var dpoa) || !(bool)dpoa) return;
        job.Data["deliverToPlayerOnArrival"] = false;

        bool gaveAnyItem = false;

        int gatheredWood = job.Data.TryGetValue("gatheredWood", out var gw) ? (int)gw : 0;
        int storedWood = job.Data.TryGetValue("storedWood", out var sw) ? (int)sw : 0;
        int totalWood = gatheredWood + storedWood;
        if (totalWood > 0)
        {
            GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)388", totalWood));
            job.Data["gatheredWood"] = 0;
            job.Data["storedWood"] = 0;
            gaveAnyItem = true;
        }

        int gatheredHardwood = job.Data.TryGetValue("gatheredHardwood", out var gh) ? (int)gh : 0;
        if (gatheredHardwood > 0)
        {
            GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)709", gatheredHardwood));
            job.Data["gatheredHardwood"] = 0;
            gaveAnyItem = true;
        }

        int storedStone = job.Data.TryGetValue("storedStone", out var ss) ? (int)ss : 0;
        if (storedStone > 0)
        {
            GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)390", storedStone));
            job.Data["storedStone"] = 0;
            gaveAnyItem = true;
        }

        int storedFiber = job.Data.TryGetValue("storedFiber", out var sf) ? (int)sf : 0;
        if (storedFiber > 0)
        {
            GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)771", storedFiber));
            job.Data["storedFiber"] = 0;
            gaveAnyItem = true;
        }

        foreach (var kv in job.Data.Where(k => k.Key.StartsWith("item_")).ToList())
        {
            string id = kv.Key.Substring(5);
            int qty = (int)kv.Value;
            if (qty > 0)
            {
                GiveItemToFarmerDirectly(player, ItemRegistry.Create(id, qty));
                gaveAnyItem = true;
            }
            job.Data.Remove(kv.Key);
        }

        if (gaveAnyItem)
        {
            try { player.currentLocation?.playSound("coin"); } catch { }
            try { player.changeFriendship(15, npc); } catch { }
        }
    }

    private sealed class LegacyFarmWorkerState : FarmWorkerStateBase { }

    private (JobExecutionContext Ctx, LegacyFarmWorkerState State) CreateLegacyFarmTaskContext(
        NpcJob job,
        NPC npc,
        Farmer? player,
        ulong gameTick)
    {
        Farmer? owner = player ?? GetJobFarmer(job) ?? GetCompanionOwner(npc.Name) ?? Game1.player;
        _companions.TryGetValue(npc.Name, out var companion);
        var ctx = new JobExecutionContext(
            job,
            npc,
            owner,
            companion,
            gameTick,
            _navigator,
            _farmWorld,
            RecordMemory,
            RecordDepositedItem,
            _debug_log);
        return (ctx, job.GetOrCreateState<LegacyFarmWorkerState>());
    }

    /// <summary>
    /// After completing a farm task, the NPC walks back to the player instead of teleporting.
    /// Returns true if still returning (don't mark job.Done yet), false if arrived.
    /// </summary>
    private bool ReturnToPlayerAfterTask(NpcJob job, NPC npc, ulong gameTick, string completionMessage)
    {
        var (ctx, state) = CreateLegacyFarmTaskContext(job, npc, GetJobFarmer(job), gameTick);
        return FarmTaskHelper.ReturnToPlayerAfterTask(ctx, state, completionMessage);
    }

    /// <summary>
    /// Resuelve una ruta completa hasta `goal` (o su mejor casilla caminable adyacente si `goal` es un objeto sólido),
    /// verificando que A* alcance realmente el destino sin truncarse contra bordes de acantilado/barranco.
    /// Retorna la lista ordenada de pasos si es alcanzable (vacía si ya está en posición), o null si no hay ruta completa.
    /// </summary>
    internal static List<Point>? FindCompleteRouteInternal(GameLocation loc, Point start, Point goal, NPC? npc = null, int maxExpansions = 14000)
    {
        if (loc == null) return null;

        if (IsTileWalkable(loc, goal, npc))
        {
            if (start == goal)
                return new List<Point>();

            var stack = FindCustomPath(loc, start, goal, npc, maxExpansions, allowWarpExit: false);
            if (stack == null || stack.Count == 0)
                return null;

            var route = new List<Point>(stack);
            return route[^1] == goal ? route : null;
        }

        // Si el objetivo es sólido (piedra, árbol, brote, cofre), reunir sus vecinos r=1 caminables
        // empezando por cardinales cercanos al NPC y luego diagonales, probando cuál es realmente alcanzable.
        var standCandidates = new List<(Point tile, int score)>(8);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Point cand = new(goal.X + dx, goal.Y + dy);
                if (!IsTileWalkable(loc, cand, npc))
                    continue;

                int distSq = (cand.X - start.X) * (cand.X - start.X) + (cand.Y - start.Y) * (cand.Y - start.Y);
                int diagPenalty = (dx != 0 && dy != 0) ? 2 : 0;
                standCandidates.Add((cand, distSq + diagPenalty));
            }
        }

        if (standCandidates.Count == 0)
        {
            Point fallbackStand = GetAdjacentWalkableTile(loc, goal, start, npc);
            if (!IsTileWalkable(loc, fallbackStand, npc))
                return null;
            standCandidates.Add((fallbackStand, 0));
        }
        else if (standCandidates.Count > 1)
        {
            standCandidates.Sort((a, b) => a.score.CompareTo(b.score));
        }

        int tested = 0;
        foreach (var (standTile, _) in standCandidates)
        {
            if (start == standTile)
                return new List<Point>();

            // Evitar saturar CPU si todos los lados son inalcanzables; normalmente con 2 lados opuestos basta
            if (tested++ >= 3)
                break;

            var stack = FindCustomPath(loc, start, standTile, npc, maxExpansions, allowWarpExit: false);
            if (stack == null || stack.Count == 0)
                continue;

            var route = new List<Point>(stack);
            if (route[^1] == standTile)
                return route;
        }

        return null;
    }

    /// <summary>
    /// Asegura que los objetivos (piedras, maleza, cultivos, árboles) estén dentro de los límites jugables,
    /// lejos de warps, fuera de la franja oculta detrás del tejado de la casa y con al menos 1 casilla adyacente caminable.
    /// </summary>
    public static bool IsSafePlayableFarmTile(GameLocation loc, Vector2 tile, NPC? npc = null)
    {
        if (loc == null || loc.map?.Layers == null || loc.map.Layers.Count == 0) return false;
        var backLayer = loc.map.GetLayer("Back") ?? loc.map.Layers[0];
        int width = backLayer.LayerWidth;
        int height = backLayer.LayerHeight;

        // Margen de 3 casillas respecto a los bordes del mapa
        if (tile.X < 3 || tile.X > width - 4 || tile.Y < 3 || tile.Y > height - 4)
            return false;

        // Mantener al menos 3 casillas de distancia de cualquier warp
        if (loc.warps != null)
        {
            foreach (var warp in loc.warps)
            {
                if (Math.Abs(tile.X - warp.X) <= 3 && Math.Abs(tile.Y - warp.Y) <= 3)
                    return false;
            }
        }

        // En la granja, excluir la zona oculta detrás del tejado de la casa principal (donde el NPC queda atrapado arriba de la casa)
        if (loc is Farm farmLoc)
        {
            var mainHouse = farmLoc.GetMainFarmHouse();
            if (mainHouse != null)
            {
                int hx = mainHouse.tileX.Value;
                int hy = mainHouse.tileY.Value;
                int hw = mainHouse.tilesWide.Value;
                int hh = mainHouse.tilesHigh.Value;

                // Excluir la huella del edificio más una franja de seguridad de 4 casillas arriba del tejado y 1 a los lados
                if (tile.X >= hx - 2 && tile.X <= hx + hw + 2 && tile.Y >= Math.Max(0, hy - 4) && tile.Y <= hy + hh)
                    return false;
            }
        }

        // Verificar que exista al menos una casilla cardinal o cercana caminable alrededor del objetivo
        Point pt = new Point((int)tile.X, (int)tile.Y);
        if (!HasWalkableInteractionTile(loc, pt, npc))
            return false;

        return true;
    }

    /// <summary>
    /// Validación específica para cultivos maduros (incluye bordes de granja, inmediaciones de warps/edificios
    /// y permite cosechar desde la propia casilla o las 8 casillas cardinales/diagonales).
    /// </summary>
    public static bool IsSafeHarvestableFarmTile(GameLocation loc, Vector2 tile, NPC? npc = null)
    {
        if (loc == null || loc.map?.Layers == null || loc.map.Layers.Count == 0) return false;
        var backLayer = loc.map.GetLayer("Back") ?? loc.map.Layers[0];
        int width = backLayer.LayerWidth;
        int height = backLayer.LayerHeight;

        // Margen mínimo de 1 casilla para permitir cultivos cercanos a los bordes sin salir del mapa
        if (tile.X < 1 || tile.X > width - 2 || tile.Y < 1 || tile.Y > height - 2)
            return false;

        // Excluir únicamente la casilla exacta del warp para evitar cosechas sobre activadores de mapa
        if (loc.warps != null)
        {
            foreach (var warp in loc.warps)
            {
                if ((int)tile.X == warp.X && (int)tile.Y == warp.Y)
                    return false;
            }
        }

        // En la granja, excluir únicamente la huella exacta de la casa principal (permitir cultivos alrededor del porche/cerca)
        if (loc is Farm farmLoc)
        {
            var mainHouse = farmLoc.GetMainFarmHouse();
            if (mainHouse != null)
            {
                int hx = mainHouse.tileX.Value;
                int hy = mainHouse.tileY.Value;
                int hw = mainHouse.tilesWide.Value;
                int hh = mainHouse.tilesHigh.Value;

                if (tile.X >= hx && tile.X < hx + hw && tile.Y >= hy && tile.Y < hy + hh)
                    return false;
            }
        }

        Point pt = new Point((int)tile.X, (int)tile.Y);
        return HasWalkableInteractionTile(loc, pt, npc, allowSelfOrDiagonal: true);
    }

    /// <summary>
    /// Comprueba que el objeto o árbol no esté completamente encerrado entre paredes/obstáculos inalcanzables.
    /// Alineado con los 4 vecinos cardinales por defecto, y opcionalmente con la propia casilla y 4 diagonales (cosecha).
    /// </summary>
    public static bool HasWalkableInteractionTile(GameLocation loc, Point targetTile, NPC? npc = null, bool allowSelfOrDiagonal = false)
    {
        Point[] cardinals = new Point[]
        {
            new(targetTile.X, targetTile.Y - 1),
            new(targetTile.X, targetTile.Y + 1),
            new(targetTile.X - 1, targetTile.Y),
            new(targetTile.X + 1, targetTile.Y)
        };
        for (int i = 0; i < cardinals.Length; i++)
        {
            if (IsTileWalkable(loc, cardinals[i], npc))
                return true;
        }

        if (allowSelfOrDiagonal)
        {
            if (IsTileWalkable(loc, targetTile, npc))
                return true;

            Point[] diagonals = new Point[]
            {
                new(targetTile.X - 1, targetTile.Y - 1),
                new(targetTile.X + 1, targetTile.Y - 1),
                new(targetTile.X - 1, targetTile.Y + 1),
                new(targetTile.X + 1, targetTile.Y + 1)
            };
            for (int i = 0; i < diagonals.Length; i++)
            {
                if (IsTileWalkable(loc, diagonals[i], npc))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Si el NPC está parado sobre un objeto sólido en loc.objects (ej. una piedra o rama en granja sin limpiar),
    /// lo desliza suavemente a la casilla cardinal libre más cercana una sola vez al iniciar la navegación o tarea.
    /// </summary>
    private void EnsureNpcOnFreeGround(NpcJob job, NPC npc, GameLocation loc, Farmer? player)
    {
        var (ctx, state) = CreateLegacyFarmTaskContext(job, npc, player, ModEntry.CurrentTick);
        FarmTaskHelper.EnsureNpcOnFreeGround(ctx, state, loc);
    }

    /// <summary>
    /// Intenta romper físicamente un escombro pequeño (maleza, rama o piedra pequeña) en las 4 casillas
    /// cardinales adyacentes al NPC para abrirse paso hacia desiredTargetTile sin teletransportes.
    /// Respeta un escudo absoluto: NUNCA destruye objetos del jugador, muebles, vallas, cultivos ni tocones/rocas gigantes.
    /// </summary>
    public static bool TryBreakAdjacentBlockingDebris(NPC npc, GameLocation loc, Farmer? owner, Vector2 desiredTargetTile)
    {
        if (npc == null || loc == null || loc is not Farm) return false;

        Point p = npc.TilePoint;
        Point[] cardinals = new Point[]
        {
            new(p.X, p.Y - 1),
            new(p.X, p.Y + 1),
            new(p.X - 1, p.Y),
            new(p.X + 1, p.Y)
        };

        // Priorizar el vecino que apunta más directamente hacia desiredTargetTile
        var orderedNeighbors = cardinals
            .OrderBy(cand => Vector2.DistanceSquared(new Vector2(cand.X, cand.Y), desiredTargetTile))
            .ToList();

        foreach (var adj in orderedNeighbors)
        {
            Vector2 adjVec = new Vector2(adj.X, adj.Y);
            if (loc.Objects != null && loc.Objects.TryGetValue(adjVec, out var obj) && obj != null && !obj.isPassable())
            {
                // ESCUDO DE SEGURIDAD ABSOLUTO: Jamás destruir creaciones u objetos del jugador
                if (FarmTaskHelper.IsProtectedPlayerObject(obj)) continue;
                if (loc.terrainFeatures != null && loc.terrainFeatures.TryGetValue(adjVec, out var tf) && tf is StardewValley.TerrainFeatures.HoeDirt) continue;

                // 1. Maleza (IsWeeds)
                bool isWeed = obj.IsWeeds()
                           || obj.Name.Contains("Weed", StringComparison.OrdinalIgnoreCase)
                           || obj.ParentSheetIndex is 0 or 313 or 314 or 315 or 316 or 317 or 318 or 319 or 320 or 321 or 674 or 675 or 676 or 677 or 678 or 679 or 784 or 785 or 786 or 787 or 792 or 793 or 794;

                // 2. Rama / Madera caída (IsTwig)
                bool isTwig = obj.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase)
                           || obj.Name.Contains("Branch", StringComparison.OrdinalIgnoreCase)
                           || obj.ParentSheetIndex is 294 or 295
                           || obj.ItemId is "294" or "295";

                // 3. Piedra pequeña común (IsBreakableStone / Name == "Stone")
                bool isStone = obj.IsBreakableStone()
                            || obj.Name.Equals("Stone", StringComparison.OrdinalIgnoreCase)
                            || obj.ItemId is "343" or "450" or "390"
                            || obj.QualifiedItemId is "(O)343" or "(O)450" or "(O)390";

                if (isWeed)
                {
                    npc.faceGeneralDirection(adjVec * 64f);
                    Systems.ToolAnimationService.PlayWeaponSwing(loc, npc, new Vector2(adj.X * 64f + 32f, adj.Y * 64f + 32f));
                    loc.playSound("cut");
                    obj.performRemoveAction();
                    loc.Objects.Remove(adjVec);
                    loc.debris.Add(new Debris(771, new Vector2(adjVec.X * 64f, adjVec.Y * 64f), npc.Position));
                    InvalidateWalkabilityCache(loc.NameOrUniqueName);
                    ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} broke blocking weed at {adjVec} towards {desiredTargetTile}", LogLevel.Debug);
                    return true;
                }
                else if (isTwig)
                {
                    npc.faceGeneralDirection(adjVec * 64f);
                    Systems.ToolAnimationService.PlayAxeSwing(loc, npc, adj, playSound: false);
                    loc.playSound("axchop");
                    loc.playSound("woodWhack");
                    obj.performRemoveAction();
                    loc.Objects.Remove(adjVec);
                    loc.debris.Add(new Debris(388, new Vector2(adjVec.X * 64f, adjVec.Y * 64f), npc.Position));
                    InvalidateWalkabilityCache(loc.NameOrUniqueName);
                    ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} broke blocking twig at {adjVec} towards {desiredTargetTile}", LogLevel.Debug);
                    return true;
                }
                else if (isStone)
                {
                    npc.faceGeneralDirection(adjVec * 64f);
                    Systems.ToolAnimationService.PlayPickaxeSwing(loc, npc, adj);
                    loc.playSound("hammer");
                    loc.playSound("stoneCrack");
                    obj.performRemoveAction();
                    loc.Objects.Remove(adjVec);
                    loc.debris.Add(new Debris(390, new Vector2(adjVec.X * 64f, adjVec.Y * 64f), npc.Position));
                    InvalidateWalkabilityCache(loc.NameOrUniqueName);
                    ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} broke blocking stone at {adjVec} towards {desiredTargetTile}", LogLevel.Debug);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Maneja el escape de un bolsillo de escombros en granja o el aborto limpio si se acumulan objetivos inalcanzables.
    /// Si el NPC está bloqueado, intenta romper maleza, rama o piedra pequeña adyacente para abrir paso físico.
    /// Si fallan >= 6 objetivos: detiene el trabajo inmediatamente, muestra chore.path_blocked y regresa a Follow.
    /// Devuelve true si la tarea debe detenerse/abortarse.
    /// </summary>
    private bool HandleUnreachableTaskTargets(NpcJob job, NPC npc, GameLocation loc, Farmer? player, List<Vector2> failedList, string jobName, Vector2 desiredTarget = default)
    {
        var (ctx, state) = CreateLegacyFarmTaskContext(job, npc, player, ModEntry.CurrentTick);
        return FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, failedList, jobName, desiredTarget);
    }

    internal static Chest? FindNearestChest(GameLocation loc, Vector2 fromTile, float maxRadius)
    {
        Chest? bestChest = null;
        float minDist = maxRadius;

        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is Chest chest && !chest.giftbox.Value)
            {
                float d = Vector2.Distance(fromTile, pair.Key);
                if (d < minDist)
                {
                    minDist = d;
                    bestChest = chest;
                }
            }
        }
        return bestChest;
    }

    internal static (Chest chest, Vector2 tile)? FindNearestChestWithTile(GameLocation loc, Vector2 fromTile, float maxRadius)
    {
        Chest? bestChest = null;
        Vector2 bestTile = Vector2.Zero;
        float minDist = maxRadius;

        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is Chest chest && !chest.giftbox.Value)
            {
                float d = Vector2.Distance(fromTile, pair.Key);
                if (d < minDist)
                {
                    minDist = d;
                    bestChest = chest;
                    bestTile = pair.Key;
                }
            }
        }
        return bestChest != null ? (bestChest, bestTile) : null;
    }

    internal static List<(Chest chest, Vector2 tile)> FindAllChests(GameLocation loc)
    {
        var list = new List<(Chest chest, Vector2 tile)>();
        if (loc?.objects == null) return list;
        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is Chest chest && !chest.giftbox.Value)
            {
                list.Add((chest, pair.Key));
            }
        }
        return list;
    }

    public static bool AddItemToChest(Chest chest, Item item)
    {
        if (chest == null || item == null) return false;

        // 1. Try to stack with existing items in chest
        for (int i = 0; i < chest.Items.Count; i++)
        {
            Item existing = chest.Items[i];
            if (existing != null && existing.canStackWith(item))
            {
                int remaining = existing.addToStack(item);
                if (remaining <= 0)
                {
                    chest.clearNulls();
                    return true;
                }
                item.Stack = remaining;
            }
        }

        // 2. Fill any null slot
        for (int i = 0; i < chest.Items.Count; i++)
        {
            if (chest.Items[i] == null)
            {
                chest.Items[i] = item;
                chest.clearNulls();
                return true;
            }
        }

        // 3. Add to items list if capacity allows
        if (chest.Items.Count < chest.GetActualCapacity())
        {
            chest.Items.Add(item);
            chest.clearNulls();
            return true;
        }

        // 4. Fallback to native chest.addItem
        Item? overflow = chest.addItem(item);
        chest.clearNulls();
        return overflow == null || overflow.Stack < item.Stack;
    }

    private static (Vector2 tile, StardewValley.TerrainFeatures.HoeDirt dirt)? FindNearestReadyCrop(
        GameLocation loc, Vector2 from, int radius, List<Vector2>? excluded = null)
    {
        IFarmWorldService world = Instance != null ? Instance._farmWorld : new FarmWorldService();
        return FarmTaskHelper.FindNearestReadyCrop(loc, from, radius, world, excluded);
    }
}
