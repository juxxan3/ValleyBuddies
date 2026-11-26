namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class FetchFromChestHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.FetchFromChest;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var farm = Game1.getFarm();
        var loc = ctx.Npc.currentLocation;
        if (loc == null || farm == null) { ctx.Job.Done = true; return; }

        var state = ctx.Job.GetOrCreateState<FetchFromChestState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.FetchCategory))
        {
            state.FetchCategory = ctx.Job.Data.TryGetValue("fetchCategory", out var fc) ? fc.ToString()! : "fetch_last";
        }

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "walkingToChest";

        if (state.Phase == "walkingToChest")
        {
            var chestLoc = (ctx.World.FindAllChests(loc).Count > 0 || loc == farm) ? loc : farm;
            if (chestLoc != null && !string.Equals(ctx.Npc.currentLocation?.NameOrUniqueName, chestLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Point door = ctx.Navigator.GetOwnerHomeExteriorDoorTile(farm, player);
                Point outside = new Point(door.X, door.Y + 1);
                Point spawn = ctx.Navigator.IsTileWalkable(chestLoc, outside, ctx.Npc)
                    ? outside
                    : ctx.Navigator.GetAdjacentWalkableTile(chestLoc, door, door, ctx.Npc);

                ctx.Navigator.LogWarpAudit("FetchFromChest_MoveToFarmDoor", ctx.Npc, chestLoc, spawn);
                Game1.warpCharacter(ctx.Npc, chestLoc, new Vector2(spawn.X, spawn.Y));
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            if (chestLoc == null)
            {
                ctx.Job.Done = true;
                return;
            }

            var allChests = ctx.World.FindAllChests(chestLoc);
            if (allChests.Count == 0)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_none_farm"));
                ctx.Npc.doEmote(32);
                ctx.Job.Done = true;
                return;
            }

            Vector2 targetChestTile;
            if (state.TargetChestTile.HasValue && allChests.Any(c => c.tile == state.TargetChestTile.Value))
            {
                targetChestTile = state.TargetChestTile.Value;
            }
            else
            {
                var uncheckedChests = allChests
                    .Where(c => !state.CheckedChests.Contains(c.tile))
                    .OrderBy(c => Vector2.Distance(ctx.Npc.Tile, c.tile))
                    .ToList();

                if (uncheckedChests.Count == 0)
                {
                    ctx.Npc.controller = null;
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    string itemDescription = FarmTaskHelper.GetFetchCategoryDescription(state.FetchCategory);
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_not_found", new { item = itemDescription }), duration: 3500);
                    ctx.Npc.doEmote(28);
                    ctx.Job.Done = true;
                    return;
                }

                targetChestTile = uncheckedChests[0].tile;
                state.TargetChestTile = targetChestTile;

                if (!state.Announced)
                {
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_search"), duration: 2500);
                    state.Announced = true;
                }
                else
                {
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_search_other"), duration: 2000);
                }
            }

            var targetChest = chestLoc.getObjectAtTile((int)targetChestTile.X, (int)targetChestTile.Y) as Chest;
            if (targetChest == null)
            {
                state.CheckedChests.Add(targetChestTile);
                state.TargetChestTile = null;
                return;
            }

            float dist = Vector2.Distance(ctx.Npc.Tile, targetChestTile);
            if (dist > 2.2f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, chestLoc, targetChestTile, rescueThreshold: 180))
                {
                    state.CheckedChests.Add(targetChestTile);
                    state.TargetChestTile = null;
                    return;
                }
                Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(chestLoc, new Point((int)targetChestTile.X, (int)targetChestTile.Y), ctx.Npc.TilePoint, ctx.Npc);
                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, chestLoc, walkTile, ctx.GameTick);
            }
            else
            {
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(targetChestTile * 64f);

                chestLoc.playSound("openChest");
                targetChest.shakeTimer = 500;

                var retrievedList = new List<Item>();
                string normalizedCat = (state.FetchCategory ?? string.Empty).Trim().ToLowerInvariant();
                for (int i = targetChest.Items.Count - 1; i >= 0; i--)
                {
                    var item = targetChest.Items[i];
                    if (item == null) continue;
                    bool match = normalizedCat switch
                    {
                        "wood" or "fetch_wood" => item.QualifiedItemId is "(O)388" or "(O)709"
                                        || item.Name.Contains("Wood", StringComparison.OrdinalIgnoreCase)
                                        || item.DisplayName.Contains("Madera", StringComparison.OrdinalIgnoreCase),
                        "stone" or "fetch_stone" => item.QualifiedItemId is "(O)390" or "(O)343" or "(O)450" or "(O)378" or "(O)380" or "(O)382" or "(O)384"
                                        || item.Name.Contains("Stone", StringComparison.OrdinalIgnoreCase)
                                        || item.Name.Contains("Ore", StringComparison.OrdinalIgnoreCase)
                                        || item.DisplayName.Contains("Piedra", StringComparison.OrdinalIgnoreCase)
                                        || item.DisplayName.Contains("Mineral", StringComparison.OrdinalIgnoreCase),
                        "fiber" or "fetch_fiber" => item.QualifiedItemId == "(O)771"
                                        || item.Name.Contains("Fiber", StringComparison.OrdinalIgnoreCase)
                                        || item.DisplayName.Contains("Fibra", StringComparison.OrdinalIgnoreCase),
                        "crops" or "fetch_crops" => item.Category is StardewValley.Object.VegetableCategory or StardewValley.Object.FruitsCategory,
                        "anything" or "fetch_last" => false,
                        _ => false
                    };

                    if (match)
                    {
                        retrievedList.Add(item);
                        targetChest.Items.RemoveAt(i);
                    }
                }

                if (normalizedCat is "anything" or "fetch_last" && retrievedList.Count == 0 && targetChest.Items.Count > 0)
                {
                    for (int i = targetChest.Items.Count - 1; i >= 0; i--)
                    {
                        if (targetChest.Items[i] != null)
                        {
                            var lastItem = targetChest.Items[i];
                            retrievedList.Add(lastItem);
                            targetChest.Items.RemoveAt(i);
                            break;
                        }
                    }
                }
                targetChest.clearNulls();

                if (retrievedList.Count > 0)
                {
                    int totalQty = retrievedList.Sum(it => it.Stack);
                    string sampleName = retrievedList[0].DisplayName;
                    state.SourceChestLocationName = chestLoc.NameOrUniqueName;
                    state.SourceChestTile = targetChestTile;
                    state.RetrievedItems = retrievedList;
                    state.Phase = "returningToPlayer";
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_found_all", new { item = sampleName, qty = totalQty }), duration: 2800);
                    ctx.Npc.doEmote(20);
                }
                else
                {
                    state.CheckedChests.Add(targetChestTile);
                    state.TargetChestTile = null;

                    var remainingChests = allChests.Where(c => !state.CheckedChests.Contains(c.tile)).ToList();
                    if (remainingChests.Count > 0)
                    {
                        string itemDescription = FarmTaskHelper.GetFetchCategoryDescription(state.FetchCategory ?? "");
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_not_here_search_next", new { item = itemDescription }), duration: 2500);
                        ctx.Npc.doEmote(12);
                    }
                    else
                    {
                        string itemDescription = FarmTaskHelper.GetFetchCategoryDescription(state.FetchCategory ?? "");
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_not_found", new { item = itemDescription }), duration: 3500);
                        ctx.Npc.doEmote(28);
                        ctx.Job.Done = true;
                    }
                }
            }
        }

        if (state.Phase == "returningToPlayer")
        {
            if (player?.currentLocation == null) { ctx.Job.Done = true; return; }

            if (!string.Equals(ctx.Npc.currentLocation?.NameOrUniqueName, player.currentLocation.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Point safeSpawn = ctx.Navigator.GetCrossMapSpawnTile(player.currentLocation, player.TilePoint, player.FacingDirection);
                ctx.Navigator.LogWarpAudit("FetchFromChest_ReturnToPlayerMap", ctx.Npc, player.currentLocation, safeSpawn);
                Game1.warpCharacter(ctx.Npc, player.currentLocation, new Vector2(safeSpawn.X, safeSpawn.Y));
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
            }

            float distToPlayer = Vector2.Distance(ctx.Npc.Tile, player.Tile);
            if (distToPlayer > 2.2f)
            {
                FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, player.currentLocation, player.Tile);
                Point walkTarget = ctx.Navigator.GetAdjacentWalkableTile(player.currentLocation, player.TilePoint, ctx.Npc.TilePoint, ctx.Npc);
                ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, player.currentLocation, walkTarget, ctx.GameTick);
            }
            else
            {
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceTowardFarmerForPeriod(3000, 3, false, player);

                if (state.RetrievedItems != null && state.RetrievedItems.Count > 0)
                {
                    int totalGiven = 0;
                    string itemName = "";
                    foreach (var item in state.RetrievedItems)
                    {
                        if (item == null || item.Stack <= 0) continue;
                        totalGiven += item.Stack;
                        itemName = item.DisplayName;
                        DeliverItemSafely(ctx, player, item, state);
                    }
                    state.RetrievedItems = null;
                    player.currentLocation?.playSound("coin");
                    ctx.Npc.doEmote(20);
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_delivered", new { item = itemName }), duration: 3800);
                    ctx.RecordMemory(ctx.Npc.Name, $"I retrieved all the {itemName} ({totalGiven} items) from the chest and handed them to the farmer.");
                }

                ctx.Job.Done = true;
            }
        }
    }

    private static void DeliverItemSafely(JobExecutionContext ctx, Farmer player, Item item, FetchFromChestState? state)
    {
        if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            ctx.World.GiveItemToFarmerDirectly(player, item);
            return;
        }

        bool isPlainStandardObject = item.GetType() == typeof(StardewValley.Object)
            && item.Quality == 0
            && !string.IsNullOrWhiteSpace(item.QualifiedItemId)
            && ModEntry.MultiplayerSync != null;

        if (isPlainStandardObject)
        {
            ModEntry.MultiplayerSync!.SendSpouseGift(player.UniqueMultiplayerID, item.QualifiedItemId, item.Stack, ctx.Npc.Name);
            return;
        }

        var dropLoc = player.currentLocation ?? ctx.Npc.currentLocation;
        if (dropLoc != null)
        {
            dropLoc.debris.Add(new Debris(item, player.Position));
            return;
        }

        if (state != null)
            TryReturnItemToSourceChest(ctx, state, item);
    }

    private static bool TryReturnItemToSourceChest(JobExecutionContext ctx, FetchFromChestState state, Item item)
    {
        if (state.SourceChestTile.HasValue && !string.IsNullOrEmpty(state.SourceChestLocationName))
        {
            var srcLoc = Game1.getLocationFromName(state.SourceChestLocationName);
            if (srcLoc != null && srcLoc.objects.TryGetValue(state.SourceChestTile.Value, out var obj) && obj is Chest srcChest)
            {
                if (ctx.World.AddItemToChest(srcChest, item))
                    return true;
            }
        }
        return false;
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<FetchFromChestState>(out var state))
        {
            if (state.RetrievedItems != null && state.RetrievedItems.Count > 0)
            {
                var owner = ctx.Owner ?? Game1.player;
                foreach (var item in state.RetrievedItems.ToList())
                {
                    if (item == null || item.Stack <= 0) continue;
                    if (!TryReturnItemToSourceChest(ctx, state, item) && owner != null)
                    {
                        DeliverItemSafely(ctx, owner, item, null);
                    }
                }
                state.RetrievedItems.Clear();
                state.RetrievedItems = null;
            }
            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
        }
    }
}
