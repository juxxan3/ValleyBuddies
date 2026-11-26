namespace ValleyBuddies.Handlers.Farm;

using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class ClearDebrisHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.ClearDebris;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 15 != 0) return;

        var state = ctx.Job.GetOrCreateState<ClearDebrisState>();
        var player = ctx.Owner ?? Game1.player;

        if (string.IsNullOrEmpty(state.Phase))
            state.Phase = "clearing";

        if (state.Phase == "returning")
        {
            string msg = !string.IsNullOrEmpty(state.CompletionMsg)
                ? state.CompletionMsg
                : Utilities.I18n.Get("bubble.task_finished", new { activity = Utilities.I18n.Get("activity.clearing") });
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

        int maxClear = 35;

        // FASE 1: Limpiar maleza, ramas y piedras
        if (state.Phase == "clearing")
        {
            if (state.ClearedCount >= maxClear)
            {
                state.Phase = "delivering";
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                return;
            }

            if (state.TargetTile.HasValue)
            {
                Vector2 targetTile = state.TargetTile.Value;
                if (loc.objects.TryGetValue(targetTile, out var currentDebris) && IsNaturalDebris(currentDebris))
                {
                    float dist = Vector2.Distance(ctx.Npc.Tile, targetTile);
                    if (dist > 2.2f)
                    {
                        if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, targetTile, rescueThreshold: 150))
                        {
                            if (loc is Farm && ctx.World.TryBreakAdjacentBlockingDebris(ctx.Npc, loc, player, targetTile))
                            {
                                state.StuckTicks = 0;
                                FarmTaskHelper.ClearActiveRoute(state);
                                ctx.Job.Data["lastPathTick"] = 0UL;
                                state.TaskPauseUntil = ctx.GameTick + 35;
                                return;
                            }

                            if (!state.FailedDebris.Contains(targetTile))
                                state.FailedDebris.Add(targetTile);
                            state.TargetTile = null;
                            state.WorkTimer = 0;
                            FarmTaskHelper.ClearActiveRoute(state);
                            if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, state.FailedDebris, "ClearDebris", targetTile))
                                return;
                            return;
                        }

                        FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, loc, targetTile);
                        return;
                    }
                    else
                    {
                        FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
                        ctx.Npc.controller = null;
                        ctx.Navigator.StopCleanly(ctx.Npc);
                        ctx.Npc.faceGeneralDirection(targetTile * 64f);

                        if (state.WorkTimer == 0)
                            state.WorkTimer = 60;

                        if (state.WorkTimer == 60)
                        {
                            ctx.Npc.doEmote(28);
                            if (currentDebris.IsWeeds() || currentDebris.Name.Contains("Weeds", StringComparison.OrdinalIgnoreCase))
                                loc.playSound("cut");
                            else if (currentDebris.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase) || currentDebris.ItemId is "294" or "295")
                            {
                                Systems.ToolAnimationService.PlayAxeSwing(loc, ctx.Npc, new Point((int)targetTile.X, (int)targetTile.Y), playSound: false);
                                loc.playSound("axchop");
                            }
                            else
                                loc.playSound("hammer");
                        }
                        else if (state.WorkTimer == 30)
                        {
                            if (currentDebris.IsWeeds() || currentDebris.Name.Contains("Weeds", StringComparison.OrdinalIgnoreCase))
                                loc.playSound("cut");
                            else if (currentDebris.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase) || currentDebris.ItemId is "294" or "295")
                                loc.playSound("woodWhack");
                            else
                                loc.playSound("hammer");
                        }

                        state.WorkTimer -= 15;
                        if (state.WorkTimer > 0) return;

                        if (currentDebris.IsWeeds() || currentDebris.Name.Contains("Weeds", StringComparison.OrdinalIgnoreCase))
                        {
                            loc.playSound("cut");
                            state.StoredFiber += 1;
                        }
                        else if (currentDebris.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase) || currentDebris.ItemId is "294" or "295")
                        {
                            loc.playSound("woodWhack");
                            state.StoredWood += 1;
                        }
                        else
                        {
                            loc.playSound("stoneCrack");
                            state.StoredStone += 1;
                        }

                        loc.objects.Remove(targetTile);
                        state.ClearedCount++;
                        state.TargetTile = null;
                        state.WorkTimer = 0;

                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.debris_progress", new { count = state.ClearedCount }), duration: 1200);

                        if (state.ClearedCount >= maxClear)
                        {
                            state.Phase = "delivering";
                            return;
                        }
                    }
                }
                else
                {
                    state.TargetTile = null;
                    state.WorkTimer = 0;
                }
            }

            var debrisCandidates = loc.objects.Pairs
                .Where(p => IsNaturalDebris(p.Value)
                         && (loc is not Farm || ctx.World.IsSafePlayableFarmTile(loc, p.Key, ctx.Npc))
                         && !state.FailedDebris.Contains(p.Key))
                .Select(p => p.Key)
                .ToList();

            if (!state.TaskDispatchedLogged)
            {
                state.TaskDispatchedLogged = true;
                ModEntry.ModMonitor?.Log($"[TaskDispatch] {ctx.Npc.Name} job=ClearDebris loc={loc.NameOrUniqueName} candidates={debrisCandidates.Count} reason={(debrisCandidates.Count > 0 ? "found_targets" : "no_debris_found")}", LogLevel.Debug);
            }

            var selectedDebris = FarmTaskHelper.SelectBestReachableTarget(ctx, state, loc, debrisCandidates, state.FailedDebris, player, out bool allBlocked);
            if (selectedDebris.HasValue)
            {
                state.TargetTile = selectedDebris.Value;
                state.WorkTimer = 60;
                FarmTaskHelper.NavigateAlongRouteOrDirect(ctx, state, loc, selectedDebris.Value);
                return;
            }
            else if (allBlocked && state.ClearedCount == 0)
            {
                FarmTaskHelper.NotifyPathBlockedAndAbort(ctx, state, loc, "ClearDebris");
                return;
            }
            else
            {
                state.Phase = "delivering";
            }
            return;
        }

        // FASE 2: Entregar materiales UNA SOLA VEZ y pasar a "returning"
        if (state.Phase == "delivering")
        {
            if (state.TotalItems <= 0)
            {
                if (state.ClearedCount > 0)
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.debris_clean"));
                else
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.debris_empty"));
                ctx.Npc.doEmote(state.ClearedCount > 0 ? 20 : 32);
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
                        ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.debris_store"), duration: 2500);
                        state.DeliveringAnnounced = true;
                    }
                    if (!FarmTaskHelper.TryNavigateToChest(ctx, state, loc, chestTile))
                    {
                        int totalItems = state.TotalItems;
                        state.DeliverToPlayerOnArrival = true;
                        state.CompletionMsg = Utilities.I18n.Get("bubble.debris_hand", new { count = state.ClearedCount, items = totalItems, activity = Utilities.I18n.Get("activity.clearing") });
                        ctx.RecordMemory(ctx.Npc.Name, $"I helped clear debris and gave {totalItems} materials to the farmer.");
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

                    if (state.StoredWood > 0)
                    {
                        var item = ItemRegistry.Create("(O)388", state.StoredWood);
                        if (item != null)
                        {
                            if (ctx.World.AddItemToChest(chest, item))
                                ctx.RecordDepositedItem(ctx.Npc.Name, "(O)388", "Madera", state.StoredWood, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, item);
                        }
                    }
                    if (state.StoredStone > 0)
                    {
                        var item = ItemRegistry.Create("(O)390", state.StoredStone);
                        if (item != null)
                        {
                            if (ctx.World.AddItemToChest(chest, item))
                                ctx.RecordDepositedItem(ctx.Npc.Name, "(O)390", "Piedra", state.StoredStone, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, item);
                        }
                    }
                    if (state.StoredFiber > 0)
                    {
                        var item = ItemRegistry.Create("(O)771", state.StoredFiber);
                        if (item != null)
                        {
                            if (ctx.World.AddItemToChest(chest, item))
                                ctx.RecordDepositedItem(ctx.Npc.Name, "(O)771", "Fibra", state.StoredFiber, new Point((int)chestTile.X, (int)chestTile.Y));
                            else if (player != null)
                                FarmTaskHelper.DeliverItemCoopSafe(ctx, player, item);
                        }
                    }

                    int totalItems = state.TotalItems;
                    state.StoredWood = 0;
                    state.StoredStone = 0;
                    state.StoredFiber = 0;

                    try { player?.changeFriendship(15, ctx.Npc); } catch { }
                    loc.playSound("coin");
                    ctx.RecordMemory(ctx.Npc.Name, $"I helped clear debris and safely stored {totalItems} materials in the chest.");

                    state.CompletionMsg = Utilities.I18n.Get("bubble.debris_chest", new { count = totalItems, activity = Utilities.I18n.Get("activity.clearing") });
                    state.Phase = "returning";
                    return;
                }
            }
            else
            {
                int totalItems = state.TotalItems;
                state.DeliverToPlayerOnArrival = true;
                state.CompletionMsg = Utilities.I18n.Get("bubble.debris_hand", new { count = state.ClearedCount, items = totalItems, activity = Utilities.I18n.Get("activity.clearing") });
                ctx.RecordMemory(ctx.Npc.Name, $"I helped clear debris and gave {totalItems} materials to the farmer.");
                state.Phase = "returning";
                return;
            }
        }
    }

    private static bool IsNaturalDebris(StardewValley.Object? obj) =>
        !FarmTaskHelper.IsProtectedPlayerObject(obj)
        && obj != null
        && (obj.IsWeeds()
            || obj.IsTwig()
            || obj.IsBreakableStone()
            || obj.Name.Contains("Twig", StringComparison.OrdinalIgnoreCase)
            || obj.Name.Contains("Weeds", StringComparison.OrdinalIgnoreCase)
            || obj.ItemId is "294" or "295" or "343" or "450"
            || obj.QualifiedItemId is "(O)294" or "(O)295" or "(O)343" or "(O)450");

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<ClearDebrisState>(out var state))
        {
            FarmTaskHelper.RescuePendingItemsOnAbort(ctx, state);
        }
    }
}
