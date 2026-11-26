namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class HarvestCropsHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.HarvestCrops;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var state = ctx.Job.GetOrCreateState<HarvestCropsState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "harvesting";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.harvesting") });
            bool stillReturning = FarmTaskHelper.ReturnToPlayerAfterTask(ctx, state, msg);
            if (!stillReturning) ctx.Job.Done = true;
            return;
        }

        bool playerInGreenhouse = player?.currentLocation != null && (
            player.currentLocation.IsGreenhouse ||
            player.currentLocation.NameOrUniqueName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase) ||
            player.currentLocation.Name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)
        );
        if (playerInGreenhouse)
        {
            state.GreenhouseVisited = true;
        }

        if (state.Phase == "entering_greenhouse")
        {
            var farm = Game1.getFarm();
            var gh = Game1.getLocationFromName("Greenhouse");
            if (farm == null || gh == null)
            {
                state.GreenhouseVisited = true;
                state.Phase = "delivering";
                return;
            }

            Point ghDoor = NpcActionExecutor.GetGreenhouseDoor(farm);
            Point doorTarget = new Point(ghDoor.X, ghDoor.Y + 1);
            if (!ctx.Navigator.IsTileWalkable(farm, doorTarget, ctx.Npc))
            {
                doorTarget = ctx.Navigator.GetAdjacentWalkableTile(farm, ghDoor, ctx.Npc.TilePoint, ctx.Npc);
            }

            float distToDoor = Vector2.Distance(ctx.Npc.Tile, doorTarget.ToVector2());
            if (state.GhLastPos == Vector2.Zero) state.GhLastPos = ctx.Npc.Position;
            if (Vector2.DistanceSquared(ctx.Npc.Position, state.GhLastPos) < 0.25f)
            {
                state.GhStuckTicks += 15;
            }
            else
            {
                state.GhStuckTicks = Math.Max(0, state.GhStuckTicks - 30);
                state.GhLastPos = ctx.Npc.Position;
            }

            if (state.GhStuckTicks >= 450 && distToDoor > 4.5f)
            {
                state.GhStuckTicks = 0;
                state.GhLastPos = Vector2.Zero;
                state.GreenhouseVisited = true;
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                state.Phase = "delivering";
                return;
            }

            if (distToDoor <= 1.8f || (state.GhStuckTicks >= 300 && distToDoor <= 4.5f))
            {
                var ghWarpBack = gh.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase));
                Point entry;
                if (ghWarpBack != null)
                {
                    Point candidate = new Point(ghWarpBack.X, Math.Max(2, ghWarpBack.Y - 2));
                    entry = ctx.Navigator.IsTileWalkable(gh, candidate, ctx.Npc)
                        ? candidate
                        : NpcActionExecutor.FindClosestWalkableCardinalNeighbor(gh, candidate, ctx.Npc);
                }
                else
                {
                    entry = NpcActionExecutor.GetDefaultLocationEntry(gh, ctx.Npc);
                }

                Game1.warpCharacter(ctx.Npc, gh, new Vector2(entry.X, entry.Y));
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                if (Game1.currentLocation is Farm || Game1.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Game1.currentLocation.playSound("doorClose");
                }

                state.InsideGreenhouse = true;
                state.GreenhouseVisited = true;
                state.TargetWorkLocationName = "Greenhouse";
                state.FailedCrops.Clear();
                state.RetriedFailedCrops = false;
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                state.GhStuckTicks = 0;
                state.GhLastPos = Vector2.Zero;
                state.TaskPauseUntil = ctx.GameTick + 20;
                state.Phase = "harvesting";
                return;
            }

            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, farm, doorTarget, ctx.GameTick);
            return;
        }

        if (state.Phase == "exiting_greenhouse")
        {
            if (ctx.Npc.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
            {
                var curLoc = ctx.Npc.currentLocation;
                var ghWarpBack = curLoc.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? curLoc.warps?.FirstOrDefault();
                Point exitTile = ghWarpBack != null ? new Point(ghWarpBack.X, Math.Max(1, ghWarpBack.Y - 1)) : new Point(10, 22);

                float distToExit = Vector2.Distance(ctx.Npc.Tile, exitTile.ToVector2());
                if (state.GhLastPos == Vector2.Zero) state.GhLastPos = ctx.Npc.Position;
                if (Vector2.DistanceSquared(ctx.Npc.Position, state.GhLastPos) < 0.25f)
                {
                    state.GhStuckTicks += 15;
                }
                else
                {
                    state.GhStuckTicks = Math.Max(0, state.GhStuckTicks - 30);
                    state.GhLastPos = ctx.Npc.Position;
                }

                if (distToExit <= 1.8f || (state.GhStuckTicks >= 300 && distToExit <= 4.5f) || state.GhStuckTicks >= 600)
                {
                    if (Game1.currentLocation is Farm || Game1.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Game1.currentLocation.playSound("doorClose");
                    }

                    var outFarm = Game1.getFarm();
                    Point ghDoor = outFarm != null ? NpcActionExecutor.GetGreenhouseDoor(outFarm) : new Point(28, 14);
                    Point outDoor = new Point(ghDoor.X, ghDoor.Y + 1);
                    if (outFarm != null && !ctx.Navigator.IsTileWalkable(outFarm, outDoor, ctx.Npc))
                    {
                        outDoor = ctx.Navigator.GetAdjacentWalkableTile(outFarm, ghDoor, ghDoor, ctx.Npc);
                    }

                    Game1.warpCharacter(ctx.Npc, "Farm", new Vector2(outDoor.X, outDoor.Y));
                    ctx.Npc.controller = null;
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    state.InsideGreenhouse = false;
                    state.TargetWorkLocationName = "Farm";
                    FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                    state.GhStuckTicks = 0;
                    state.GhLastPos = Vector2.Zero;
                    state.TaskPauseUntil = ctx.GameTick + 15;
                    state.Phase = "delivering";
                    return;
                }

                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, curLoc, exitTile, ctx.GameTick);
                return;
            }
            else
            {
                state.InsideGreenhouse = false;
                state.TargetWorkLocationName = "Farm";
                state.Phase = "delivering";
            }
        }

        GameLocation? workLoc;
        if (state.InsideGreenhouse || string.Equals(state.TargetWorkLocationName, "Greenhouse", StringComparison.OrdinalIgnoreCase))
        {
            workLoc = Game1.getLocationFromName("Greenhouse");
            if (ctx.Npc.currentLocation != null && (ctx.Npc.currentLocation.IsGreenhouse || ctx.Npc.currentLocation.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase)))
                workLoc = ctx.Npc.currentLocation;
        }
        else if (playerInGreenhouse)
        {
            workLoc = player!.currentLocation;
            if (workLoc != null && !string.Equals(ctx.Npc.currentLocation?.NameOrUniqueName, workLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                var ghWarpBack = workLoc.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase));
                Point entry;
                if (ghWarpBack != null)
                {
                    Point candidate = new Point(ghWarpBack.X, Math.Max(2, ghWarpBack.Y - 2));
                    entry = ctx.Navigator.IsTileWalkable(workLoc, candidate, ctx.Npc)
                        ? candidate
                        : NpcActionExecutor.FindClosestWalkableCardinalNeighbor(workLoc, candidate, ctx.Npc);
                }
                else
                {
                    entry = NpcActionExecutor.GetDefaultLocationEntry(workLoc, ctx.Npc);
                }
                Point safeEntry = ctx.Navigator.GetAdjacentWalkableTile(workLoc, entry, entry, ctx.Npc);
                ctx.Navigator.LogWarpAudit("HarvestCrops_EnterGreenhouse", ctx.Npc, workLoc, safeEntry);
                Game1.warpCharacter(ctx.Npc, workLoc, new Vector2(safeEntry.X, safeEntry.Y));
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.doEmote(20);
                state.InsideGreenhouse = true;
                return;
            }
        }
        else
        {
            workLoc = ctx.Npc.currentLocation;
            bool npcLocValid = workLoc != null && (
                workLoc is Farm ||
                workLoc.IsGreenhouse ||
                workLoc.NameOrUniqueName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)
            );

            if (!npcLocValid)
            {
                if (player?.currentLocation != null && (
                    player.currentLocation.IsGreenhouse ||
                    player.currentLocation.NameOrUniqueName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)))
                {
                    workLoc = player.currentLocation;
                    if (!string.Equals(ctx.Npc.currentLocation?.NameOrUniqueName, workLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                    {
                        var ghWarpBack = workLoc.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase));
                        Point entry;
                        if (ghWarpBack != null)
                        {
                            Point candidate = new Point(ghWarpBack.X, Math.Max(2, ghWarpBack.Y - 2));
                            entry = ctx.Navigator.IsTileWalkable(workLoc, candidate, ctx.Npc)
                                ? candidate
                                : NpcActionExecutor.FindClosestWalkableCardinalNeighbor(workLoc, candidate, ctx.Npc);
                        }
                        else
                        {
                            entry = NpcActionExecutor.GetDefaultLocationEntry(workLoc, ctx.Npc);
                        }
                        Point safeSpawn = ctx.Navigator.GetAdjacentWalkableTile(workLoc, entry, entry, ctx.Npc);
                        ctx.Navigator.LogWarpAudit("HarvestCrops_EnterPlayerGreenhouse", ctx.Npc, workLoc, safeSpawn);
                        Game1.warpCharacter(ctx.Npc, workLoc, new Vector2(safeSpawn.X, safeSpawn.Y));
                        ctx.Npc.controller = null;
                        ctx.Navigator.StopCleanly(ctx.Npc);
                        ctx.Npc.doEmote(20);
                        return;
                    }
                }
                else
                {
                    workLoc = Game1.getFarm();
                    if (workLoc != null && !string.Equals(ctx.Npc.currentLocation?.NameOrUniqueName, workLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                    {
                        Point entry;
                        if (workLoc is Farm f)
                        {
                            Point door = ctx.Navigator.GetOwnerHomeExteriorDoorTile(f, player);
                            Point outside = new Point(door.X, door.Y + 1);
                            entry = ctx.Navigator.IsTileWalkable(f, outside, ctx.Npc)
                                ? outside
                                : ctx.Navigator.GetAdjacentWalkableTile(f, door, door, ctx.Npc);
                        }
                        else
                        {
                            entry = new Point(64, 15);
                        }
                        ctx.Navigator.LogWarpAudit("HarvestCrops_EnterFarmExteriorDoor", ctx.Npc, workLoc, entry);
                        Game1.warpCharacter(ctx.Npc, workLoc, new Vector2(entry.X, entry.Y));
                        ctx.Npc.controller = null;
                        ctx.Navigator.StopCleanly(ctx.Npc);
                        ctx.Npc.doEmote(20);
                        return;
                    }
                }
            }
        }

        if (state.TaskPauseUntil > 0 && ctx.GameTick < state.TaskPauseUntil)
            return;

        if (workLoc == null) { ctx.Job.Done = true; return; }
        if (workLoc.IsGreenhouse || workLoc.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase))
        {
            state.GreenhouseVisited = true;
        }
        ctx.Npc.speed = 2;
        ctx.Npc.addedSpeed = 0;
        FarmTaskHelper.EnsureNpcOnFreeGround(ctx, state, workLoc);

        if (state.HarvestFlowers == null && ctx.Job.Data.TryGetValue("harvestFlowers", out var hfObj))
        {
            if (hfObj is bool hfBool) state.HarvestFlowers = hfBool;
            else if (hfObj is string hfStr && bool.TryParse(hfStr, out bool hfParsed)) state.HarvestFlowers = hfParsed;
        }
        bool includeFlowers = state.HarvestFlowers ?? true;
        bool protectBeeHouseFlowers = !state.HarvestFlowers.HasValue && (ModEntry.Config?.ProtectFlowersNearBeeHouses ?? true);

        int maxHarvest = Math.Clamp(ModEntry.Config?.MaxCropsPerWaterJob ?? 200, 10, 500);

        // FASE 1: Cosechar cultivos
        if (state.Phase == "harvesting")
        {
            if (state.HarvestCount >= maxHarvest)
            {
                state.Phase = "delivering";
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            var targetCrop = FarmTaskHelper.FindNearestReadyCrop(workLoc, ctx.Npc.Tile, 250, ctx.World, state.FailedCrops, includeFlowers, protectBeeHouseFlowers, ctx.Npc);
            if (targetCrop == null && state.FailedCrops.Count > 0 && !state.RetriedFailedCrops)
            {
                state.RetriedFailedCrops = true;
                state.FailedCrops.Clear();
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Job.Data["lastPathTick"] = 0UL;
                targetCrop = FarmTaskHelper.FindNearestReadyCrop(workLoc, ctx.Npc.Tile, 250, ctx.World, state.FailedCrops, includeFlowers, protectBeeHouseFlowers, ctx.Npc);
            }

            if (targetCrop == null)
            {
                if (!state.GreenhouseVisited && state.HarvestCount < maxHarvest)
                {
                    var ghCheckFarm = workLoc as Farm ?? Game1.getFarm();
                    if (NpcActionExecutor.IsGreenhouseUnlocked(ghCheckFarm))
                    {
                        var gh = Game1.getLocationFromName("Greenhouse");
                        if (gh != null && FarmTaskHelper.FindNearestReadyCrop(gh, new Vector2(10, 15), 250, ctx.World, null, includeFlowers, protectBeeHouseFlowers, ctx.Npc) != null)
                        {
                            state.GhStuckTicks = 0;
                            state.GhLastPos = ctx.Npc.Position;
                            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                            state.Phase = "entering_greenhouse";
                            ctx.Npc.doEmote(20);
                            return;
                        }
                    }
                    state.GreenhouseVisited = true;
                }
                state.Phase = "delivering";
                return;
            }

            Vector2 cropPos = targetCrop.Value.tile;
            var hoeDirt = targetCrop.Value.dirt;
            float dist = Vector2.Distance(ctx.Npc.Tile, cropPos);

            if (dist > 2.2f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, workLoc, cropPos, rescueThreshold: 180))
                {
                    if (workLoc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(ctx.Npc, workLoc, player, cropPos))
                    {
                        state.StuckTicks = 0;
                        ctx.Job.Data["lastPathTick"] = 0UL;
                        state.TaskPauseUntil = ctx.GameTick + 35;
                        return;
                    }

                    if (!state.FailedCrops.Contains(cropPos))
                        state.FailedCrops.Add(cropPos);
                    if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, workLoc, state.FailedCrops, "HarvestCrops", cropPos))
                        return;
                    return;
                }
                if (dist > 12f)
                {
                    FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, workLoc, cropPos);
                }
                else
                {
                    Point walkTile = FarmTaskHelper.GetHarvestStandTile(workLoc, new Point((int)cropPos.X, (int)cropPos.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, workLoc, walkTile, ctx.GameTick);
                }
            }
            else
            {
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(cropPos * 64f);

                if (state.WorkTimer == 0)
                    state.WorkTimer = 30;

                if (state.WorkTimer == 30)
                {
                    if (hoeDirt.crop != null && hoeDirt.readyForHarvest())
                    {
                        workLoc.playSound("coin");
                        ctx.Npc.doEmote(20);
                    }
                }

                state.WorkTimer -= 15;
                if (state.WorkTimer > 0) return;

                var crop = hoeDirt.crop;
                if (crop != null && hoeDirt.readyForHarvest())
                {
                    var cropData = crop.GetData();
                    string harvestId = cropData?.HarvestItemId ?? crop.indexOfHarvest.Value;
                    if (string.IsNullOrWhiteSpace(harvestId)) harvestId = crop.indexOfHarvest.Value;

                    int minStack = cropData?.HarvestMinStack ?? 1;
                    int maxStack = cropData?.HarvestMaxStack ?? 1;
                    int stackToGive = minStack;
                    if (maxStack > minStack)
                    {
                        stackToGive = Game1.random.Next(minStack, maxStack + 1);
                    }
                    if (cropData?.ExtraHarvestChance > 0 && Game1.random.NextDouble() < cropData.ExtraHarvestChance)
                    {
                        stackToGive++;
                    }

                    string qid = harvestId.StartsWith("(") ? harvestId : $"(O){harvestId}";
                    var harvestItemData = ItemRegistry.GetData(qid);
                    bool isColoredFlower = crop.tintColor.Value != Color.White
                        && harvestItemData?.Category == StardewValley.Object.flowersCategory;

                    if (isColoredFlower)
                    {
                        string rawFlowerId = harvestItemData?.ItemId ?? (harvestId.StartsWith("(O)", StringComparison.OrdinalIgnoreCase) ? harvestId.Substring(3) : harvestId);
                        state.AddPendingItem(new StardewValley.Objects.ColoredObject(rawFlowerId, stackToGive, crop.tintColor.Value));
                    }
                    else
                    {
                        int curStack = state.PendingItemsByQid.TryGetValue(qid, out var cs) ? cs : 0;
                        state.PendingItemsByQid[qid] = curStack + stackToGive;
                    }

                    int regrowDays = cropData?.RegrowDays ?? -1;
                    if (regrowDays > 0)
                    {
                        crop.fullyGrown.Value = true;
                        crop.dayOfCurrentPhase.Value = regrowDays;
                        crop.updateDrawMath(cropPos);
                    }
                    else
                    {
                        hoeDirt.crop = null;
                    }

                    if (hoeDirt.readyForHarvest() && !state.FailedCrops.Contains(cropPos))
                    {
                        state.FailedCrops.Add(cropPos);
                    }

                    workLoc.playSound("harvest");
                    ctx.Npc.faceGeneralDirection(cropPos * 64f);
                    ctx.Npc.doEmote(20);

                    state.HarvestCount++;
                    state.WorkTimer = 0;

                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.harvest_progress", new { count = state.HarvestCount }), duration: 1200);

                    if (state.HarvestCount >= maxHarvest)
                    {
                        state.Phase = "delivering";
                        return;
                    }
                }
                else
                {
                    state.WorkTimer = 0;
                    if (!state.FailedCrops.Contains(cropPos))
                        state.FailedCrops.Add(cropPos);
                }
            }
            return;
        }

        // FASE 2: Entregar al cofre o al jugador UNA SOLA VEZ y pasar a "returning"
        if (state.Phase == "delivering")
        {
            if (state.HarvestCount <= 0)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.harvest_empty"));
                ctx.Npc.doEmote(32);
                ctx.Job.Done = true;
                return;
            }

            bool storeInChests = ModEntry.Config?.StoreHarvestInChests != false;
            bool inGhWork = workLoc.IsGreenhouse || workLoc.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase);
            var chestInfo = (storeInChests && (workLoc is Farm || inGhWork))
                ? ctx.World.FindNearestChestWithTile(workLoc, ctx.Npc.Tile, 250f)
                : null;
            if (chestInfo == null && inGhWork && !playerInGreenhouse)
            {
                state.GhStuckTicks = 0;
                state.GhLastPos = ctx.Npc.Position;
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                state.Phase = "exiting_greenhouse";
                return;
            }
            if (chestInfo != null)
            {
                var (chest, chestTile) = chestInfo.Value;
                float dist = Vector2.Distance(ctx.Npc.Tile, chestTile);

                if (dist > 2.2f)
                {
                    if (!state.DeliveringAnnounced)
                    {
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.harvest_store"), duration: 2500);
                        state.DeliveringAnnounced = true;
                    }
                    if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, workLoc, chestTile, rescueThreshold: 180))
                    {
                        FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.harvest_hand", new { count = state.HarvestCount, activity = Utilities.I18n.Get("activity.harvesting") });
                        ctx.RecordMemory(ctx.Npc.Name, $"I helped harvest {state.HarvestCount} ripe crops and gave them to the farmer.");
                        state.Phase = "returning";
                        return;
                    }
                    Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(workLoc, new Point((int)chestTile.X, (int)chestTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                    ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, workLoc, walkTile, ctx.GameTick);
                    return;
                }
                else
                {
                    FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                    ctx.Npc.controller = null;
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    ctx.Npc.faceGeneralDirection(chestTile * 64f);

                    workLoc.playSound("openChest");
                    chest.shakeTimer = 500;

                    foreach (var kv in state.PendingItemsByQid.ToList())
                    {
                        string id = kv.Key;
                        int qty = kv.Value;
                        if (qty > 0)
                        {
                            var item = ItemRegistry.Create(id, qty);
                            if (item != null)
                            {
                                if (ctx.World.AddItemToChest(chest, item))
                                    ctx.RecordDepositedItem(ctx.Npc.Name, id, item.DisplayName ?? "Cultivo", qty, new Point((int)chestTile.X, (int)chestTile.Y));
                                else if (player != null)
                                    FarmTaskHelper.DeliverItemCoopSafe(ctx, player, item);
                            }
                        }
                    }
                    state.PendingItemsByQid.Clear();

                    foreach (var pendingItem in state.PendingItems.ToList())
                    {
                        if (pendingItem != null && pendingItem.Stack > 0)
                        {
                            int qty = pendingItem.Stack;
                            string id = pendingItem.QualifiedItemId;
                            string disp = pendingItem.DisplayName;
                            if (ctx.World.AddItemToChest(chest, pendingItem))
                                ctx.RecordDepositedItem(ctx.Npc.Name, id, disp, qty, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, pendingItem);
                        }
                    }
                    state.PendingItems.Clear();

                    try { player?.changeFriendship(15, ctx.Npc); } catch { }
                    workLoc.playSound("coin");
                    ctx.RecordMemory(ctx.Npc.Name, $"I helped on the farm by harvesting {state.HarvestCount} ripe crops and safely stored them in the chest.");
                    state.CompletionMsg = Utilities.I18n.Get("bubble.harvest_chest", new { count = state.HarvestCount, activity = Utilities.I18n.Get("activity.harvesting") });
                    state.Phase = "returning";
                    return;
                }
            }
            else
            {
                state.DeliverToPlayerOnArrival = true;
                state.CompletionMsg = Utilities.I18n.Get("bubble.harvest_hand", new { count = state.HarvestCount, activity = Utilities.I18n.Get("activity.harvesting") });
                ctx.RecordMemory(ctx.Npc.Name, $"I helped harvest {state.HarvestCount} ripe crops and gave them to the farmer.");
                state.Phase = "returning";
                return;
            }
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<HarvestCropsState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }
}
