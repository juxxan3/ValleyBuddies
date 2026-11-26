namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class MineNearbyRockHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.MineNearbyRock;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var state = ctx.Job.GetOrCreateState<MineNearbyRockState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "mining";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.mining") });
            bool stillReturning = FarmTaskHelper.ReturnToPlayerAfterTask(ctx, state, msg);
            if (!stillReturning) ctx.Job.Done = true;
            return;
        }

        if (state.TaskPauseUntil > 0 && ctx.GameTick < state.TaskPauseUntil)
            return;

        var loc = FarmTaskHelper.EnsureOutdoorWorkLocation(ctx, 32);
        if (loc == null) { ctx.Job.Done = true; return; }

        ctx.Npc.speed = 2;
        ctx.Npc.addedSpeed = 0;
        FarmTaskHelper.EnsureNpcOnFreeGround(ctx, state, loc);

        int maxMine = 20;

        // FASE 1: Picar piedras
        if (state.Phase == "mining")
        {
            if (state.MinedCount >= maxMine)
            {
                state.Phase = "delivering";
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            if (!state.TargetTile.HasValue || !loc.objects.TryGetValue(state.TargetTile.Value, out var lockedRock) || !IsNaturalMineRock(lockedRock, loc))
            {
                state.TargetTile = null;
                var rockCandidates = loc.objects.Pairs
                    .Where(p => IsNaturalMineRock(p.Value, loc)
                             && (loc is not Farm || ctx.World.IsSafePlayableFarmTile(loc, p.Key, ctx.Npc))
                             && !state.FailedRocks.Contains(p.Key))
                    .Select(p => p.Key)
                    .ToList();

                if (!state.TaskDispatchedLogged)
                {
                    state.TaskDispatchedLogged = true;
                    ModEntry.ModMonitor?.Log($"[TaskDispatch] {ctx.Npc.Name} job=MineNearbyRock loc={loc.NameOrUniqueName} candidates={rockCandidates.Count} reason={(rockCandidates.Count > 0 ? "found_targets" : "no_matching_rocks")}", LogLevel.Debug);
                }

                var selectedRock = FarmTaskHelper.SelectBestReachableTarget(ctx, state, loc, rockCandidates, state.FailedRocks, player, out bool allBlocked);
                if (!selectedRock.HasValue)
                {
                    if (allBlocked && state.MinedCount == 0)
                    {
                        FarmTaskHelper.NotifyPathBlockedAndAbort(ctx, state, loc, "MineNearbyRock");
                        return;
                    }
                    state.Phase = "delivering";
                    return;
                }

                state.TargetTile = selectedRock.Value;
            }

            Vector2 rockTile = state.TargetTile.Value;
            float dist = Vector2.Distance(ctx.Npc.Tile, rockTile);

            if (dist > 2.2f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, rockTile, rescueThreshold: 150))
                {
                    if (loc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(ctx.Npc, loc, player, rockTile))
                    {
                        state.StuckTicks = 0;
                        FarmTaskHelper.ClearActiveRoute(state);
                        ctx.Job.Data["lastPathTick"] = 0UL;
                        state.TaskPauseUntil = ctx.GameTick + 35;
                        return;
                    }

                    if (!state.FailedRocks.Contains(rockTile))
                        state.FailedRocks.Add(rockTile);
                    state.TargetTile = null;
                    state.WorkTimer = 0;
                    FarmTaskHelper.ClearActiveRoute(state);
                    if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, state.FailedRocks, "MineNearbyRock", rockTile))
                        return;
                    return;
                }

                FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, loc, rockTile);
            }
            else
            {
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(rockTile * 64f);

                if (state.WorkTimer == 0)
                    state.WorkTimer = 45;

                if (state.WorkTimer == 45)
                {
                    loc.playSound("hammer");
                    ctx.Npc.doEmote(28);
                    Systems.ToolAnimationService.PlayPickaxeSwing(loc, ctx.Npc, new Point((int)rockTile.X, (int)rockTile.Y));
                }
                else if (state.WorkTimer == 30)
                {
                    loc.playSound("hammer");
                    Systems.ToolAnimationService.PlayPickaxeSwing(loc, ctx.Npc, new Point((int)rockTile.X, (int)rockTile.Y));
                }

                state.WorkTimer -= 15;
                if (state.WorkTimer > 0) return;

                loc.objects.Remove(rockTile);
                loc.playSound("stoneCrack");
                ctx.Npc.doEmote(20);

                state.MinedCount++;
                state.StoredStone += 1;
                state.WorkTimer = 0;
                state.TargetTile = null;

                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.mine_progress", new { count = state.MinedCount }), duration: 1200);

                if (state.MinedCount >= maxMine)
                {
                    state.Phase = "delivering";
                    return;
                }
            }
            return;
        }

        // FASE 2: Entregar piedras UNA SOLA VEZ y pasar a "returning"
        if (state.Phase == "delivering")
        {
            if (state.MinedCount <= 0 || state.StoredStone <= 0)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.mine_empty"));
                ctx.Npc.doEmote(32);
                ctx.Job.Done = true;
                return;
            }

            bool storeInChests = ModEntry.Config?.StoreHarvestInChests != false;
            var chestInfo = (storeInChests && loc is Farm) ? ctx.World.FindNearestChestWithTile(loc, ctx.Npc.Tile, 250f) : null;
            if (chestInfo != null)
            {
                var (chest, chestTile) = chestInfo.Value;
                float dist = Vector2.Distance(ctx.Npc.Tile, chestTile);
                if (dist > 2.2f)
                {
                    if (!state.DeliveringAnnounced)
                    {
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.mine_store"), duration: 2500);
                        state.DeliveringAnnounced = true;
                    }
                    if (!FarmTaskHelper.TryNavigateToChest(ctx, state, loc, chestTile))
                    {
                        int fallbackQty = state.StoredStone;
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.mining_hand", new { count = state.MinedCount, items = fallbackQty, activity = Utilities.I18n.Get("activity.mining") });
                        ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer by mining {state.MinedCount} rocks/ores and delivered them.");
                        state.Phase = "returning";
                    }
                    return;
                }
                else
                {
                    FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                    ctx.Npc.controller = null;
                    ctx.Navigator.StopCleanly(ctx.Npc);
                    ctx.Npc.faceGeneralDirection(chestTile * 64f);

                    loc.playSound("openChest");
                    chest.shakeTimer = 500;

                    int qty = state.StoredStone;
                    var stoneItem = ItemRegistry.Create("(O)390", qty);
                    if (stoneItem != null)
                    {
                        if (ctx.World.AddItemToChest(chest, stoneItem))
                        {
                            ctx.RecordDepositedItem(ctx.Npc.Name, "(O)390", "Piedra", qty, new Point((int)chestTile.X, (int)chestTile.Y));
                        }
                        else if (player != null)
                        {
                            FarmTaskHelper.DeliverItemCoopSafe(ctx, player, stoneItem);
                        }
                    }

                    state.StoredStone = 0;

                    try { player?.changeFriendship(15, ctx.Npc); } catch { }
                    loc.playSound("coin");
                    ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer by mining {state.MinedCount} rocks/ores and stored {qty} stone in the chest.");

                    state.CompletionMsg = Utilities.I18n.Get("bubble.mining_chest", new { count = qty, activity = Utilities.I18n.Get("activity.mining") });
                    state.Phase = "returning";
                    return;
                }
            }
            else
            {
                int qty = state.StoredStone;
                state.DeliverToPlayerOnArrival = true;
                state.CompletionMsg = Utilities.I18n.Get("bubble.mining_hand", new { count = state.MinedCount, items = qty, activity = Utilities.I18n.Get("activity.mining") });
                ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer by mining {state.MinedCount} rocks/ores and delivered them.");
                state.Phase = "returning";
                return;
            }
        }
    }

    private static bool IsNaturalMineRock(StardewValley.Object? obj, GameLocation? loc = null)
    {
        if (FarmTaskHelper.IsProtectedPlayerObject(obj) || obj == null)
            return false;

        if (loc is Farm)
        {
            if (obj.ItemId is "751" or "290" or "764" or "765" or "95"
                or "2" or "4" or "6" or "8" or "10" or "12" or "14" or "25"
                or "816" or "817" or "818" or "819"
                or "843" or "844" or "845" or "846" or "847" or "848" or "849" or "850")
            {
                return false;
            }

            return obj.ItemId is "343" or "450" or "668" or "670" or "760" or "762"
                || obj.QualifiedItemId is "(O)343" or "(O)450"
                || string.Equals(obj.Name, "Stone", StringComparison.OrdinalIgnoreCase);
        }

        return obj.IsBreakableStone()
            || obj.ItemId is "343" or "450" or "668" or "670" or "760" or "762"
                or "751" or "290" or "764" or "765" or "95"
                or "2" or "4" or "6" or "8" or "10" or "12" or "14" or "25"
                or "816" or "817" or "818" or "819"
                or "843" or "844" or "845" or "846" or "847" or "849" or "850"
            || obj.QualifiedItemId is "(O)343" or "(O)450";
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<MineNearbyRockState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }
}
