namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class ChopTreesHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.ChopTrees;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var state = ctx.Job.GetOrCreateState<ChopTreesState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.TreeFilter))
        {
            state.TreeFilter = ctx.Job.Data.TryGetValue("treeFilter", out var tf) ? tf.ToString()! : "all";
        }

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "chopping";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.chopping") });
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

        int maxTrees = 4;

        // FASE 1: Talar hasta 4 árboles
        if (state.Phase == "chopping")
        {
            if (state.ChoppedCount >= maxTrees)
            {
                state.Phase = "delivering";
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            StardewValley.TerrainFeatures.Tree? tree = null;
            if (state.TreeTile.HasValue
                && loc.terrainFeatures.TryGetValue(state.TreeTile.Value, out var existingTf)
                && existingTf is StardewValley.TerrainFeatures.Tree lockedTree
                && !lockedTree.tapped.Value
                && (lockedTree.growthStage.Value >= 5 || lockedTree.stump.Value))
            {
                tree = lockedTree;
            }
            else
            {
                state.TreeTile = null;
                var treeCandidates = loc.terrainFeatures.Pairs
                    .Where(p => (loc is not Farm || ctx.World.IsSafePlayableFarmTile(loc, p.Key, ctx.Npc))
                             && !state.FailedTrees.Contains(p.Key)
                             && p.Value is StardewValley.TerrainFeatures.Tree t
                             && !t.tapped.Value
                             && (t.growthStage.Value >= 5 || t.stump.Value)
                             && FarmTaskHelper.MatchesTreeFilter(t, state.TreeFilter))
                    .Select(p => p.Key)
                    .ToList();

                if (!state.TaskDispatchedLogged)
                {
                    state.TaskDispatchedLogged = true;
                    ModEntry.ModMonitor?.Log($"[TaskDispatch] {ctx.Npc.Name} job=ChopTrees loc={loc.NameOrUniqueName} candidates={treeCandidates.Count} reason={(treeCandidates.Count > 0 ? "found_targets" : "no_matching_trees")}", LogLevel.Debug);
                }

                var selectedTree = FarmTaskHelper.SelectBestReachableTarget(ctx, state, loc, treeCandidates, state.FailedTrees, player, out bool allBlocked);
                if (!selectedTree.HasValue)
                {
                    if (allBlocked && state.ChoppedCount == 0)
                    {
                        FarmTaskHelper.NotifyPathBlockedAndAbort(ctx, state, loc, "ChopTrees");
                        return;
                    }
                    state.Phase = "delivering";
                    return;
                }

                state.TreeTile = selectedTree.Value;
                tree = (StardewValley.TerrainFeatures.Tree)loc.terrainFeatures[selectedTree.Value];
            }

            Vector2 treeTile = state.TreeTile.Value;

            float dist = Vector2.Distance(ctx.Npc.Tile, treeTile);
            if (dist > 2.2f)
            {
                if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, treeTile, rescueThreshold: 150))
                {
                    if (loc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(ctx.Npc, loc, player, treeTile))
                    {
                        state.StuckTicks = 0;
                        FarmTaskHelper.ClearActiveRoute(state);
                        ctx.Job.Data["lastPathTick"] = 0UL;
                        state.TaskPauseUntil = ctx.GameTick + 35;
                        return;
                    }

                    if (!state.FailedTrees.Contains(treeTile))
                        state.FailedTrees.Add(treeTile);
                    state.TreeTile = null;
                    state.WorkTimer = 0;
                    FarmTaskHelper.ClearActiveRoute(state);
                    if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, state.FailedTrees, "ChopTrees", treeTile))
                        return;
                    return;
                }

                FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, loc, treeTile);
            }
            else
            {
                FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.faceGeneralDirection(treeTile * 64f);

                if (state.WorkTimer == 0)
                    state.WorkTimer = 60;

                if (state.WorkTimer == 60)
                {
                    loc.playSound("axchop");
                    tree.shake(treeTile, true);
                    ctx.Npc.doEmote(28);
                    Systems.ToolAnimationService.PlayAxeSwing(loc, ctx.Npc, new Point((int)treeTile.X, (int)treeTile.Y), playSound: false);
                }
                else if (state.WorkTimer == 30)
                {
                    loc.playSound("axchop");
                    tree.shake(treeTile, true);
                    Systems.ToolAnimationService.PlayAxeSwing(loc, ctx.Npc, new Point((int)treeTile.X, (int)treeTile.Y), playSound: false);
                }

                state.WorkTimer -= 15;
                if (state.WorkTimer > 0) return;

                state.WorkTimer = 0;

                bool wasAlreadyStump = tree.stump.Value;
                tree.health.Value -= 5.0f;

                if (tree.health.Value <= 0f)
                {
                    if (!wasAlreadyStump)
                    {
                        tree.stump.Value = true;
                        tree.health.Value = 4.0f;
                        loc.playSound("treecrack");
                        loc.playSound("treethud");
                        ctx.Npc.doEmote(20);
                        state.GatheredWood += 12;
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.tree_fall"), duration: 1800);
                    }
                    else
                    {
                        loc.terrainFeatures.Remove(treeTile);
                        state.TreeTile = null;
                        loc.playSound("woodWhack");
                        loc.playSound("stumpCrack");
                        ctx.Npc.doEmote(20);

                        state.ChoppedCount++;

                        bool isMahogany = tree.treeType.Value == "8";
                        if (isMahogany)
                        {
                            state.GatheredHardwood += 8;
                            state.GatheredWood += 4;
                        }
                        else
                        {
                            state.GatheredWood += 5;
                        }

                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.tree_progress", new { count = state.ChoppedCount, max = maxTrees }), duration: 2000);

                        if (state.ChoppedCount >= maxTrees)
                        {
                            state.Phase = "delivering";
                            return;
                        }
                    }
                }
                else
                {
                    loc.playSound("axchop");
                    tree.shake(treeTile, true);
                }
            }
            return;
        }

        // FASE 2: Entregar la madera UNA SOLA VEZ y pasar inmediatamente a "returning"
        if (state.Phase == "delivering")
        {
            if (state.ChoppedCount <= 0 && state.GatheredWood <= 0 && state.GatheredHardwood <= 0)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.tree_empty"));
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
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.tree_store"), duration: 2500);
                        state.DeliveringAnnounced = true;
                    }
                    if (!FarmTaskHelper.TryNavigateToChest(ctx, state, loc, chestTile))
                    {
                        int storedW = state.GatheredWood;
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.chopping_hand", new { count = storedW, trees = state.ChoppedCount, activity = Utilities.I18n.Get("activity.chopping") });
                        ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer chop down {state.ChoppedCount} wild trees ({storedW} wood) and gave them to the farmer.");
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

                    int storedW = state.GatheredWood;
                    int storedHW = state.GatheredHardwood;

                    if (state.GatheredWood > 0)
                    {
                        var woodItem = ItemRegistry.Create("(O)388", state.GatheredWood);
                        if (woodItem != null)
                        {
                            if (ctx.World.AddItemToChest(chest, woodItem))
                                ctx.RecordDepositedItem(ctx.Npc.Name, "(O)388", "Madera", state.GatheredWood, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, woodItem);
                        }
                    }
                    if (state.GatheredHardwood > 0)
                    {
                        var hwItem = ItemRegistry.Create("(O)709", state.GatheredHardwood);
                        if (hwItem != null)
                        {
                            if (ctx.World.AddItemToChest(chest, hwItem))
                                ctx.RecordDepositedItem(ctx.Npc.Name, "(O)709", "Madera noble", state.GatheredHardwood, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, hwItem);
                        }
                    }

                    state.GatheredWood = 0;
                    state.GatheredHardwood = 0;

                    try { player?.changeFriendship(20, ctx.Npc); } catch { }
                    loc.playSound("coin");
                    ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer chop down {state.ChoppedCount} wild trees on the farm ({storedW} wood) and stored the wood in the outdoor chest.");

                    state.CompletionMsg = Utilities.I18n.Get("bubble.chopping_chest", new { count = storedW, trees = state.ChoppedCount, activity = Utilities.I18n.Get("activity.chopping") });
                    state.Phase = "returning";
                    return;
                }
            }
            else
            {
                int storedW = state.GatheredWood;
                state.DeliverToPlayerOnArrival = true;
                state.CompletionMsg = Utilities.I18n.Get("bubble.chopping_hand", new { count = storedW, trees = state.ChoppedCount, activity = Utilities.I18n.Get("activity.chopping") });
                ctx.RecordMemory(ctx.Npc.Name, $"I helped the farmer chop down {state.ChoppedCount} wild trees ({storedW} wood) and gave them to the farmer.");
                state.Phase = "returning";
                return;
            }
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<ChopTreesState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }
}
