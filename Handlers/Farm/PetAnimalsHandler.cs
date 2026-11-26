namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class PetAnimalsHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.PetAnimals;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.Npc.currentLocation is AnimalHouse unattendedAh)
        {
            ctx.Navigator.StepControllerIfLocationUnattended(ctx.Npc, unattendedAh);
        }

        if (ctx.GameTick % 20 != 0) return;

        var state = ctx.Job.GetOrCreateState<PetAnimalsState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "petting";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.petting") });
            bool stillReturning = FarmTaskHelper.ReturnToPlayerAfterTask(ctx, state, msg);
            if (!stillReturning) ctx.Job.Done = true;
            return;
        }

        var loc = FarmTaskHelper.EnsureOutdoorWorkLocation(ctx, 20);
        if (loc == null) { ctx.Job.Done = true; return; }

        if (state.Phase == "delivering")
        {
            if (state.PendingItems.Count == 0)
            {
                state.CompletionMsg = Utilities.I18n.Get("bubble.pet_done", new { count = Math.Max(1, state.PettedCount + state.CollectedProductsCount), activity = Utilities.I18n.Get("activity.petting") });
                state.Phase = "returning";
                return;
            }

            if (ctx.Npc.currentLocation is AnimalHouse inAhDeliver)
            {
                ExitAnimalHouseToFarm(ctx, state, inAhDeliver);
                return;
            }

            var farmWork = loc as Farm ?? Game1.getFarm();
            bool storeInChests = ModEntry.Config?.StoreHarvestInChests != false;
            var chestInfo = (storeInChests && farmWork != null)
                ? ctx.World.FindNearestChestWithTile(farmWork, ctx.Npc.Tile, 250f)
                : null;

            if (chestInfo != null && farmWork != null)
            {
                var (chest, chestTile) = chestInfo.Value;
                float distToChest = Vector2.Distance(ctx.Npc.Tile, chestTile);
                if (distToChest > 2.2f)
                {
                    if (!state.DeliveringAnnounced)
                    {
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.harvest_store"), duration: 2500);
                        state.DeliveringAnnounced = true;
                    }
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, farmWork, chestTile, rescueThreshold: 180))
                    {
                        FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.pet_done", new { count = Math.Max(1, state.PettedCount + state.CollectedProductsCount), activity = Utilities.I18n.Get("activity.petting") });
                        state.Phase = "returning";
                        return;
                    }
                    Point walkChest = ctx.Navigator.GetAdjacentWalkableTile(farmWork, new Point((int)chestTile.X, (int)chestTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, farmWork, walkChest, ctx.GameTick);
                    return;
                }

                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(chestTile * 64f);
                farmWork.playSound("openChest");
                chest.shakeTimer = 500;

                foreach (var pendingItem in state.PendingItems.ToList())
                {
                    if (pendingItem != null && pendingItem.Stack > 0)
                    {
                        int qty = pendingItem.Stack;
                        string qid = pendingItem.QualifiedItemId;
                        string disp = pendingItem.DisplayName;
                        if (ctx.World.AddItemToChest(chest, pendingItem))
                            ctx.RecordDepositedItem(ctx.Npc.Name, qid, disp, qty, new Point((int)chestTile.X, (int)chestTile.Y));
                        else if (player != null)
                            FarmTaskHelper.DeliverItemCoopSafe(ctx, player, pendingItem);
                    }
                }
                state.PendingItems.Clear();

                try { player?.changeFriendship(15, ctx.Npc); } catch { }
                farmWork.playSound("coin");
                ctx.RecordMemory(ctx.Npc.Name, $"I tended the animals and stored {state.CollectedProductsCount} animal products safely in the chest.");
                state.CompletionMsg = Utilities.I18n.Get("bubble.harvest_chest", new { count = state.CollectedProductsCount, activity = Utilities.I18n.Get("activity.collecting") });
                state.Phase = "returning";
                return;
            }

            state.DeliverToPlayerOnArrival = true;
            state.CompletionMsg = Utilities.I18n.Get("bubble.pet_done", new { count = Math.Max(1, state.PettedCount + state.CollectedProductsCount), activity = Utilities.I18n.Get("activity.petting") });
            state.Phase = "returning";
            return;
        }

        int maxPet = 100;

        // Si está dentro de un corral/establo (AnimalHouse)
        if (ctx.Npc.currentLocation is AnimalHouse ah)
        {
            ctx.Navigator.StepControllerIfLocationUnattended(ctx.Npc, ah);

            // 1. Prioridad: recoger productos animales en el suelo (despeja el pasillo)
            var floorProduct = ah.Objects.Pairs
                .Where(kv => FarmTaskHelper.IsAnimalProductObject(ah, kv.Value) && !state.FailedFloorProducts.Contains(kv.Key))
                .OrderBy(kv => Vector2.DistanceSquared(ctx.Npc.Tile, kv.Key))
                .FirstOrDefault();

            if (floorProduct.Value != null)
            {
                Vector2 prodTile = floorProduct.Key;
                float dist = Vector2.Distance(ctx.Npc.Tile, prodTile);
                if (dist > 2.2f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, ah, prodTile, rescueThreshold: 200))
                    {
                        if (!state.FailedFloorProducts.Contains(prodTile))
                            state.FailedFloorProducts.Add(prodTile);
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(ah, new Point((int)prodTile.X, (int)prodTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, ah, walkTile, ctx.GameTick);
                    return;
                }

                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(prodTile * 64f);
                ah.Objects.Remove(prodTile);
                state.AddPendingItem(floorProduct.Value);
                state.CollectedProductsCount++;
                ah.playSound("coin");
                ctx.Npc.doEmote(20);
                return;
            }

            var unpettedInAh = ah.animals.Values
                .Where(a => a != null && !a.wasPet.Value && !state.PettedIds.Contains("animal_" + a.myID.Value))
                .OrderBy(a => Vector2.DistanceSquared(ctx.Npc.Tile, a.Tile))
                .FirstOrDefault();

            if (unpettedInAh != null && state.PettedCount < maxPet)
            {
                float dist = Vector2.Distance(ctx.Npc.Tile, unpettedInAh.Tile);
                if (dist > 2.5f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, ah, unpettedInAh.Tile, rescueThreshold: 240))
                    {
                        state.PettedIds.Add("animal_" + unpettedInAh.myID.Value);
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(ah, new Point((int)unpettedInAh.Tile.X, (int)unpettedInAh.Tile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, ah, walkTile, ctx.GameTick);
                    return;
                }
                else
                {
                    FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                    ctx.Npc.controller = null;
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    ctx.Npc.faceGeneralDirection(unpettedInAh.Position);
                    ctx.Npc.doEmote(20);

                    state.PettedIds.Add("animal_" + unpettedInAh.myID.Value);
                    unpettedInAh.doEmote(20);
                    try { unpettedInAh.makeSound(); } catch { ah.playSound("dwop"); }

                    if (player != null && !unpettedInAh.wasPet.Value)
                    {
                        try { unpettedInAh.pet(player, is_auto_pet: false); } catch { }
                    }

                    if (!unpettedInAh.wasPet.Value)
                    {
                        unpettedInAh.wasPet.Value = true;
                        unpettedInAh.friendshipTowardFarmer.Value = Math.Min(1000, unpettedInAh.friendshipTowardFarmer.Value + 15);
                        unpettedInAh.happiness.Value = (byte)Math.Min(255, (int)unpettedInAh.happiness.Value + 30);
                    }

                    if (FarmTaskHelper.TryHarvestAnimalProduce(unpettedInAh, ah) is Item inlineProd)
                    {
                        state.AddPendingItem(inlineProd);
                        state.CollectedProductsCount++;
                        state.PettedIds.Add("harvest_" + unpettedInAh.myID.Value);
                    }

                    state.PettedCount++;
                    ctx.Npc.shake(250);
                    int v = Game1.random.Next(1, 4);
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get($"bubble.pet_progress.v{v}", new { count = state.PettedCount }), duration: 1500);
                    return;
                }
            }

            // 3. Ordeñar/esquilar animales con producto listo en este AnimalHouse
            var harvestableInAh = ah.animals.Values
                .Where(a => FarmTaskHelper.IsReadyForToolHarvest(a) && !state.PettedIds.Contains("harvest_" + a.myID.Value))
                .OrderBy(a => Vector2.DistanceSquared(ctx.Npc.Tile, a.Tile))
                .FirstOrDefault();

            if (harvestableInAh != null)
            {
                float dist = Vector2.Distance(ctx.Npc.Tile, harvestableInAh.Tile);
                if (dist > 2.5f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, ah, harvestableInAh.Tile, rescueThreshold: 240))
                    {
                        state.PettedIds.Add("harvest_" + harvestableInAh.myID.Value);
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(ah, new Point((int)harvestableInAh.Tile.X, (int)harvestableInAh.Tile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, ah, walkTile, ctx.GameTick);
                    return;
                }

                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(harvestableInAh.Position);
                state.PettedIds.Add("harvest_" + harvestableInAh.myID.Value);
                if (FarmTaskHelper.TryHarvestAnimalProduce(harvestableInAh, ah) is Item prod)
                {
                    state.AddPendingItem(prod);
                    state.CollectedProductsCount++;
                    ctx.Npc.doEmote(20);
                }
                return;
            }

            // Ya no hay más animales ni productos pendientes en este edificio: salir a Farm
            Point exitTile = Point.Zero;
            if (ah.warps != null && ah.warps.Count > 0)
            {
                var w = ah.warps.FirstOrDefault(wp => wp.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? ah.warps[0];
                exitTile = new Point(w.X, Math.Max(1, w.Y - 1));
            }
            if (exitTile == Point.Zero)
            {
                exitTile = new Point(ah.map.Layers[0].LayerWidth / 2, ah.map.Layers[0].LayerHeight - 2);
            }

            float distToExit = Vector2.Distance(ctx.Npc.Tile, exitTile.ToVector2());
            if (distToExit > 1.8f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, ah, exitTile.ToVector2(), rescueThreshold: 200))
                {
                    ExitAnimalHouseToFarm(ctx, state, ah);
                    return;
                }
                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, ah, exitTile, ctx.GameTick);
                return;
            }
            else
            {
                ExitAnimalHouseToFarm(ctx, state, ah);
                return;
            }
        }

        // Recoger productos animales en el suelo exterior (trufas, huevos, etc.)
        if (loc is Farm exteriorFarm)
        {
            var extFloorProd = exteriorFarm.Objects.Pairs
                .Where(kv => FarmTaskHelper.IsAnimalProductObject(exteriorFarm, kv.Value) && !state.FailedFloorProducts.Contains(kv.Key))
                .OrderBy(kv => Vector2.DistanceSquared(ctx.Npc.Tile, kv.Key))
                .FirstOrDefault();

            if (extFloorProd.Value != null)
            {
                Vector2 prodTile = extFloorProd.Key;
                float dist = Vector2.Distance(ctx.Npc.Tile, prodTile);
                if (dist > 2.2f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, exteriorFarm, prodTile, rescueThreshold: 200))
                    {
                        if (!state.FailedFloorProducts.Contains(prodTile))
                            state.FailedFloorProducts.Add(prodTile);
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(exteriorFarm, new Point((int)prodTile.X, (int)prodTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, exteriorFarm, walkTile, ctx.GameTick);
                    return;
                }

                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(prodTile * 64f);
                exteriorFarm.Objects.Remove(prodTile);
                state.AddPendingItem(extFloorProd.Value);
                state.CollectedProductsCount++;
                exteriorFarm.playSound("coin");
                ctx.Npc.doEmote(20);
                return;
            }
        }

        // 1. Filtrar mascotas (Pet) realmente sin registrar hoy
        var unpettedPets = loc.characters
            .Where(c => (c is StardewValley.Characters.Pet || c.GetType().Name.Contains("Pet")) && !state.PettedIds.Contains("pet_" + c.Name))
            .ToList();

        // 2. Filtrar FarmAnimal exteriores sin acariciar
        var unpettedFarmAnimals = (loc is Farm fLoc)
            ? fLoc.animals.Values.Where(a => a != null && !a.wasPet.Value && !state.PettedIds.Contains("animal_" + a.myID.Value)).ToList()
            : new List<FarmAnimal>();

        // Priorización: Si el Pet ya tiene grantedFriendshipForPet y hay FarmAnimal pendientes, atender primero a los FarmAnimal
        Character? targetPet = null;
        FarmAnimal? targetFarmAnimal = null;
        Vector2 targetTile = Vector2.Zero;

        if (unpettedFarmAnimals.Count > 0)
        {
            var closestAnimal = unpettedFarmAnimals.OrderBy(a => Vector2.Distance(ctx.Npc.Tile, a.Tile)).First();
            var closestPet = unpettedPets.OrderBy(c => Vector2.Distance(ctx.Npc.Tile, c.Tile)).FirstOrDefault();

            if (closestPet != null && closestPet is StardewValley.Characters.Pet sdvPet && !sdvPet.grantedFriendshipForPet.Value)
            {
                if (Vector2.Distance(ctx.Npc.Tile, closestPet.Tile) <= Vector2.Distance(ctx.Npc.Tile, closestAnimal.Tile))
                {
                    targetPet = closestPet;
                    targetTile = closestPet.Tile;
                }
                else
                {
                    targetFarmAnimal = closestAnimal;
                    targetTile = closestAnimal.Tile;
                }
            }
            else
            {
                targetFarmAnimal = closestAnimal;
                targetTile = closestAnimal.Tile;
            }
        }
        else if (unpettedPets.Count > 0)
        {
            var closestPet = unpettedPets.OrderBy(c => Vector2.Distance(ctx.Npc.Tile, c.Tile)).First();
            targetPet = closestPet;
            targetTile = closestPet.Tile;
        }

        if (targetPet != null || targetFarmAnimal != null)
        {
            if (state.PettedCount >= maxPet)
            {
                if (state.PendingItems.Count > 0)
                {
                    state.Phase = "delivering";
                    return;
                }
                if (state.PettedCount > 0)
                {
                    try { player?.changeFriendship(15, ctx.Npc); } catch { }
                    ctx.RecordMemory(ctx.Npc.Name, $"I visited the farm animals and pets, giving love and petting {state.PettedCount} of them.");
                    state.CompletionMsg = Utilities.I18n.Get("bubble.pet_done", new { count = state.PettedCount, activity = Utilities.I18n.Get("activity.petting") });
                    state.Phase = "returning";
                }
                else
                {
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.pet_empty"));
                    ctx.Npc.doEmote(32);
                    ctx.Job.Done = true;
                }
                return;
            }

            float dist = Vector2.Distance(ctx.Npc.Tile, targetTile);
            if (dist > 2.5f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, targetTile, rescueThreshold: 240))
                {
                    if (targetPet != null) state.PettedIds.Add("pet_" + targetPet.Name);
                    if (targetFarmAnimal != null) state.PettedIds.Add("animal_" + targetFarmAnimal.myID.Value);
                    return;
                }
                Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(loc, new Point((int)targetTile.X, (int)targetTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, walkTile, ctx.GameTick);
                return;
            }
            else
            {
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(targetTile * 64f);
                ctx.Npc.doEmote(20);

                if (targetPet != null)
                {
                    state.PettedIds.Add("pet_" + targetPet.Name);
                    targetPet.doEmote(20);
                    loc.playSound("dwop");

                    if (targetPet is StardewValley.Characters.Pet sdvPet)
                    {
                        if (!sdvPet.grantedFriendshipForPet.Value)
                        {
                            sdvPet.grantedFriendshipForPet.Value = true;
                            sdvPet.friendshipTowardFarmer.Value = Math.Min(1000, sdvPet.friendshipTowardFarmer.Value + 12);
                        }
                        if (player != null)
                        {
                            try { sdvPet.lastPetDay[player.UniqueMultiplayerID] = (int)Game1.Date.TotalDays; } catch { }
                        }
                    }
                }
                else if (targetFarmAnimal != null)
                {
                    state.PettedIds.Add("animal_" + targetFarmAnimal.myID.Value);
                    targetFarmAnimal.doEmote(20);
                    try { targetFarmAnimal.makeSound(); } catch { loc.playSound("dwop"); }

                    if (player != null && !targetFarmAnimal.wasPet.Value)
                    {
                        try { targetFarmAnimal.pet(player, is_auto_pet: false); } catch { }
                    }

                    if (!targetFarmAnimal.wasPet.Value)
                    {
                        targetFarmAnimal.wasPet.Value = true;
                        targetFarmAnimal.friendshipTowardFarmer.Value = Math.Min(1000, targetFarmAnimal.friendshipTowardFarmer.Value + 15);
                        targetFarmAnimal.happiness.Value = (byte)Math.Min(255, (int)targetFarmAnimal.happiness.Value + 30);
                    }

                    if (FarmTaskHelper.TryHarvestAnimalProduce(targetFarmAnimal, loc) is Item extInlineProd)
                    {
                        state.AddPendingItem(extInlineProd);
                        state.CollectedProductsCount++;
                        state.PettedIds.Add("harvest_" + targetFarmAnimal.myID.Value);
                    }
                }

                state.PettedCount++;
                ctx.Npc.shake(250);
                int v = Game1.random.Next(1, 4);
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get($"bubble.pet_progress.v{v}", new { count = state.PettedCount }), duration: 1500);
                return;
            }
        }

        // Ordeñar/esquilar animales exteriores con producto listo en Farm
        if (loc is Farm extFarmHarvest)
        {
            var extHarvestAnimal = extFarmHarvest.animals.Values
                .Where(a => FarmTaskHelper.IsReadyForToolHarvest(a) && !state.PettedIds.Contains("harvest_" + a.myID.Value))
                .OrderBy(a => Vector2.DistanceSquared(ctx.Npc.Tile, a.Tile))
                .FirstOrDefault();

            if (extHarvestAnimal != null)
            {
                float dist = Vector2.Distance(ctx.Npc.Tile, extHarvestAnimal.Tile);
                if (dist > 2.5f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, extFarmHarvest, extHarvestAnimal.Tile, rescueThreshold: 240))
                    {
                        state.PettedIds.Add("harvest_" + extHarvestAnimal.myID.Value);
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(extFarmHarvest, new Point((int)extHarvestAnimal.Tile.X, (int)extHarvestAnimal.Tile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, extFarmHarvest, walkTile, ctx.GameTick);
                    return;
                }

                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(extHarvestAnimal.Position);
                state.PettedIds.Add("harvest_" + extHarvestAnimal.myID.Value);
                if (FarmTaskHelper.TryHarvestAnimalProduce(extHarvestAnimal, extFarmHarvest) is Item prod)
                {
                    state.AddPendingItem(prod);
                    state.CollectedProductsCount++;
                    ctx.Npc.doEmote(20);
                }
                return;
            }
        }

        // Si no quedan animales exteriores sin acariciar en Farm, buscar en edificios interiores (Coop / Barn)
        if (loc is Farm farmMap)
        {
            var targetBuilding = farmMap.buildings
                .Where(b => b != null
                    && b.indoors.Value is AnimalHouse inAh
                    && !state.VisitedAnimalHouses.Contains(inAh.NameOrUniqueName)
                    && (inAh.Objects.Pairs.Any(kv => FarmTaskHelper.IsAnimalProductObject(inAh, kv.Value) && !state.FailedFloorProducts.Contains(kv.Key))
                        || inAh.animals.Values.Any(a => a != null
                            && ((!a.wasPet.Value && state.PettedCount < maxPet && !state.PettedIds.Contains("animal_" + a.myID.Value))
                                || (FarmTaskHelper.IsReadyForToolHarvest(a) && !state.PettedIds.Contains("harvest_" + a.myID.Value))))))
                .OrderBy(b => Vector2.DistanceSquared(ctx.Npc.Tile, new Vector2(b.getPointForHumanDoor().X, b.getPointForHumanDoor().Y)))
                .FirstOrDefault();

            if (targetBuilding != null)
            {
                var targetAh = (AnimalHouse)targetBuilding.indoors.Value;
                Point doorPt = targetBuilding.getPointForHumanDoor();
                Point approachTile = new Point(doorPt.X, doorPt.Y + 1);
                if (!ctx.Navigator.IsTileWalkable(farmMap, approachTile, ctx.Npc))
                {
                    approachTile = ctx.Navigator.GetAdjacentWalkableTile(farmMap, doorPt, ctx.Npc.TilePoint, ctx.Npc);
                }

                float distToDoor = Vector2.Distance(ctx.Npc.Tile, approachTile.ToVector2());
                if (distToDoor > 1.8f)
                {
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, farmMap, approachTile.ToVector2(), rescueThreshold: 200))
                    {
                        FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                        state.VisitedAnimalHouses.Add(targetAh.NameOrUniqueName);
                        return;
                    }
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, farmMap, approachTile, ctx.GameTick);
                    return;
                }
                else
                {
                    Point insideTile = FarmTaskHelper.FindSafeAnimalHouseEntryTile(targetAh, ctx.Npc);

                    Game1.warpCharacter(ctx.Npc, targetAh.NameOrUniqueName, insideTile.ToVector2());
                    if (Game1.currentLocation is Farm || Game1.currentLocation == targetAh) farmMap.playSound("doorClose");
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    state.VisitedAnimalHouses.Add(targetAh.NameOrUniqueName);
                    state.InsideAnimalBuilding = true;
                    state.SubBuildingFarmDoorTile = approachTile;
                    return;
                }
            }
        }

        // Trabajo completado: si recogió productos, depositar en cofre o entregar al jugador; si no, retornar
        if (state.PendingItems.Count > 0)
        {
            state.Phase = "delivering";
            return;
        }

        if (state.PettedCount > 0 || state.CollectedProductsCount > 0)
        {
            try { player?.changeFriendship(15, ctx.Npc); } catch { }
            ctx.RecordMemory(ctx.Npc.Name, $"I visited the farm animals and pets, giving love and petting {state.PettedCount} of them.");
            state.CompletionMsg = Utilities.I18n.Get("bubble.pet_done", new { count = Math.Max(1, state.PettedCount + state.CollectedProductsCount), activity = Utilities.I18n.Get("activity.petting") });
            state.Phase = "returning";
        }
        else
        {
            ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.pet_empty"));
            ctx.Npc.doEmote(32);
            ctx.Job.Done = true;
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<PetAnimalsState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }

    private static void ExitAnimalHouseToFarm(JobExecutionContext ctx, PetAnimalsState state, AnimalHouse ah)
    {
        FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
        var farm = Game1.getFarm();
        if (farm != null)
        {
            Point farmDoor = state.SubBuildingFarmDoorTile ?? (farm.buildings.FirstOrDefault(b => b.indoors.Value == ah) is Building bld
                ? new Point(bld.getPointForHumanDoor().X, bld.getPointForHumanDoor().Y + 1)
                : new Point(64, 15));

            Game1.warpCharacter(ctx.Npc, "Farm", farmDoor.ToVector2());
            if (Game1.currentLocation == ah || Game1.currentLocation is Farm) farm.playSound("doorClose");
            ctx.Navigator.StopCleanly(ctx.Npc);
        }
        state.InsideAnimalBuilding = false;
        state.SubBuildingFarmDoorTile = null;
    }
}
