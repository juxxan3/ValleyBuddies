namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class ChopWoodHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.ChopWood;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var state = ctx.Job.GetOrCreateState<ChopWoodState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "gathering";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.foraging") });
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

        int maxWood = 15;

        // FASE 2: Entregar leña UNA SOLA VEZ y cambiar a "returning"
        if (state.Phase == "deliveringWood")
        {
            bool storeInChests = ModEntry.Config?.StoreHarvestInChests != false;
            var chestInfo = (storeInChests && loc is Farm) ? ctx.World.FindNearestChestWithTile(loc, ctx.Npc.Tile, 250f) : null;
            int qtyToDeliver = state.GatheredWood > 0 ? state.GatheredWood : (state.WoodCount > 0 ? state.WoodCount : 0);
            state.GatheredWood = qtyToDeliver;

            if (chestInfo != null && qtyToDeliver > 0)
            {
                var (chest, chestTile) = chestInfo.Value;
                float dist = Vector2.Distance(ctx.Npc.Tile, chestTile);
                if (dist > 2.2f)
                {
                    if (!FarmTaskHelper.TryNavigateToChest(ctx, state, loc, chestTile))
                    {
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.firewood_hand", new { count = qtyToDeliver, activity = Utilities.I18n.Get("activity.foraging") });
                        ctx.RecordMemory(ctx.Npc.Name, $"I collected {qtyToDeliver} pieces of firewood and branches and gave them to the farmer.");
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

                    var woodItem = ItemRegistry.Create("(O)388", qtyToDeliver);
                    if (woodItem != null)
                    {
                        if (ctx.World.AddItemToChest(chest, woodItem))
                            ctx.RecordDepositedItem(ctx.Npc.Name, "(O)388", "Madera", qtyToDeliver, new Point((int)chestTile.X, (int)chestTile.Y));
                        else if (player != null)
                            FarmTaskHelper.DeliverItemCoopSafe(ctx, player, woodItem);
                    }

                    state.GatheredWood = 0;
                    ctx.RecordMemory(ctx.Npc.Name, $"I collected {qtyToDeliver} pieces of firewood and branches on the farm and stored them in the chest.");
                    try { player?.changeFriendship(15, ctx.Npc); } catch { }
                    loc.playSound("coin");

                    state.CompletionMsg = Utilities.I18n.Get("bubble.firewood_chest", new { count = qtyToDeliver, activity = Utilities.I18n.Get("activity.foraging") });
                    state.Phase = "returning";
                    return;
                }
            }
            else
            {
                state.DeliverToPlayerOnArrival = true;
                state.CompletionMsg = Utilities.I18n.Get("bubble.firewood_hand", new { count = qtyToDeliver, activity = Utilities.I18n.Get("activity.foraging") });
                ctx.RecordMemory(ctx.Npc.Name, $"I collected {qtyToDeliver} pieces of firewood and branches and gave them to the farmer.");
                state.Phase = "returning";
                return;
            }
        }

        // FASE 1: Recolectar ramas y leña
        if (state.WoodCount >= maxWood)
        {
            state.Phase = "deliveringWood";
            ctx.Npc.controller = null;
            ctx.Navigator.StopCleanly(ctx.Npc);
            ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.twigs_store"), duration: 2500);
            return;
        }

        Vector2 woodTile = Vector2.Zero;
        bool foundTwig = false;
        StardewValley.TerrainFeatures.Tree? targetTree = null;

        if (state.WoodTile.HasValue)
        {
            Vector2 lockedTile = state.WoodTile.Value;
            if (loc.objects.TryGetValue(lockedTile, out var lockedObj) && IsNaturalTwig(lockedObj))
            {
                woodTile = lockedTile;
                foundTwig = true;
            }
            else if (loc.terrainFeatures.TryGetValue(lockedTile, out var lockedTf) && lockedTf is StardewValley.TerrainFeatures.Tree lt && !state.FailedWood.Contains(lockedTile))
            {
                woodTile = lockedTile;
                targetTree = lt;
            }
            else
            {
                state.WoodTile = null;
            }
        }

        if (!foundTwig && targetTree == null)
        {
            var twigCandidates = loc.objects.Pairs
                .Where(p => IsNaturalTwig(p.Value)
                         && (loc is not Farm || ctx.World.IsSafePlayableFarmTile(loc, p.Key, ctx.Npc))
                         && !state.FailedWood.Contains(p.Key))
                .Select(p => p.Key)
                .ToList();

            var selectedTwig = FarmTaskHelper.SelectBestReachableTarget(ctx, state, loc, twigCandidates, state.FailedWood, player, out bool twigsBlocked);
            if (selectedTwig.HasValue)
            {
                woodTile = selectedTwig.Value;
                foundTwig = true;
            }
            else
            {
                var treeCandidates = loc.terrainFeatures.Pairs
                    .Where(p => (loc is not Farm || ctx.World.IsSafePlayableFarmTile(loc, p.Key, ctx.Npc))
                             && !state.FailedWood.Contains(p.Key)
                             && p.Value is StardewValley.TerrainFeatures.Tree t
                             && t.growthStage.Value >= 3)
                    .Select(p => p.Key)
                    .ToList();

                var selectedTree = FarmTaskHelper.SelectBestReachableTarget(ctx, state, loc, treeCandidates, state.FailedWood, player, out bool treesBlocked);
                if (selectedTree.HasValue)
                {
                    woodTile = selectedTree.Value;
                    targetTree = (StardewValley.TerrainFeatures.Tree)loc.terrainFeatures[woodTile];
                }
                else if ((twigsBlocked || treesBlocked) && state.WoodCount == 0 && (twigCandidates.Count > 0 || treeCandidates.Count > 0))
                {
                    FarmTaskHelper.NotifyPathBlockedAndAbort(ctx, state, loc, "ChopWood");
                    return;
                }
            }
        }

        if (!foundTwig && targetTree == null)
        {
            ModEntry.ModMonitor?.Log($"[TaskDispatch] {ctx.Npc.Name} job=ChopWood loc={loc.NameOrUniqueName} candidates=0 reason=no_twigs_found", LogLevel.Debug);
            if (state.WoodCount > 0)
            {
                state.Phase = "deliveringWood";
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.twigs_empty"));
            ctx.Npc.doEmote(32);
            ctx.Job.Done = true;
            return;
        }

        if (!state.TaskDispatchedLogged)
        {
            state.TaskDispatchedLogged = true;
            ModEntry.ModMonitor?.Log($"[TaskDispatch] {ctx.Npc.Name} job=ChopWood loc={loc.NameOrUniqueName} candidates={(foundTwig ? 1 : 0) + (targetTree != null ? 1 : 0)} reason=found_targets", LogLevel.Debug);
        }

        state.WoodTile = woodTile;
        float distToWood = Vector2.Distance(ctx.Npc.Tile, woodTile);

        if (distToWood > 2.2f)
        {
            if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, woodTile, rescueThreshold: 150))
            {
                if (loc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(ctx.Npc, loc, player, woodTile))
                {
                    state.StuckTicks = 0;
                    FarmTaskHelper.ClearActiveRoute(state);
                    ctx.Job.Data["lastPathTick"] = 0UL;
                    state.TaskPauseUntil = ctx.GameTick + 35;
                    return;
                }

                if (!state.FailedWood.Contains(woodTile))
                    state.FailedWood.Add(woodTile);
                state.WoodTile = null;
                state.WorkTimer = 0;
                FarmTaskHelper.ClearActiveRoute(state);
                if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, state.FailedWood, "ChopWood", woodTile))
                    return;
                return;
            }

            FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, loc, woodTile);
        }
        else
        {
            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
            ctx.Npc.controller = null;
            ctx.Navigator.StopCleanly(ctx.Npc);
            ctx.Npc.faceGeneralDirection(woodTile * 64f);

            if (state.WorkTimer == 0)
                state.WorkTimer = 60;

            if (state.WorkTimer == 60)
            {
                Systems.ToolAnimationService.PlayAxeSwing(loc, ctx.Npc, new Point((int)woodTile.X, (int)woodTile.Y), playSound: false);
                loc.playSound("axchop");
                ctx.Npc.doEmote(28);
            }
            else if (state.WorkTimer == 30)
            {
                loc.playSound("axchop");
            }

            state.WorkTimer -= 15;
            if (state.WorkTimer > 0) return;

            if (foundTwig)
            {
                loc.objects.Remove(woodTile);
                loc.playSound("woodWhack");
            }
            else if (targetTree != null)
            {
                targetTree.shake(woodTile, true);
                loc.playSound("woodWhack");
                if (!state.FailedWood.Contains(woodTile))
                    state.FailedWood.Add(woodTile); // No sacudir el mismo árbol infinitamente
            }

            state.WorkTimer = 0;
            state.WoodTile = null;
            state.WoodCount++;
            state.GatheredWood += 1;

            if (state.WoodCount < maxWood)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.twigs_progress", new { count = state.WoodCount, max = maxWood }), duration: 1200);
            }
            else
            {
                state.Phase = "deliveringWood";
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.twigs_store"), duration: 2500);
            }
        }
    }

    private static bool IsNaturalTwig(StardewValley.Object? obj) =>
        !FarmTaskHelper.IsProtectedPlayerObject(obj)
        && obj != null
        && (obj.IsTwig()
            || obj.ItemId is "294" or "295"
            || obj.QualifiedItemId is "(O)294" or "(O)295"
            || obj.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase));

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<ChopWoodState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }
}
