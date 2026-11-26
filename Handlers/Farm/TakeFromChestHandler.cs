namespace ValleyBuddies.Handlers.Farm;

using System;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class TakeFromChestHandler : INpcJobHandler
{
    private sealed class TakeFromChestState : FarmWorkerStateBase { }

    public JobKind Kind => JobKind.TakeFromChest;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 30 != 0) return;
        var state = ctx.Job.GetOrCreateState<TakeFromChestState>();
        var farm = Game1.getFarm();
        var loc = ctx.Npc.currentLocation;
        if (loc == null) { ctx.Job.Done = true; return; }

        var chest = ctx.World.FindNearestChest(loc, ctx.Npc.Tile, 40);
        if (chest == null)
        {
            if (farm != null && !string.Equals(loc.NameOrUniqueName, farm.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                var owner = ctx.Owner ?? Game1.player;
                Point door = ctx.Navigator.GetOwnerHomeExteriorDoorTile(farm, owner);
                Point outside = new Point(door.X, door.Y + 1);
                Point spawn = ctx.Navigator.IsTileWalkable(farm, outside, ctx.Npc)
                    ? outside
                    : ctx.Navigator.GetAdjacentWalkableTile(farm, door, door, ctx.Npc);

                ctx.Navigator.LogWarpAudit("TakeFromChest_MoveToFarmDoor", ctx.Npc, farm, spawn);
                Game1.warpCharacter(ctx.Npc, farm, new Vector2(spawn.X, spawn.Y));
                ctx.Npc.controller = null;
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Npc.doEmote(32);
                return;
            }
            ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_none_near"));
            ctx.Npc.doEmote(32);
            ctx.Job.Done = true;
            return;
        }

        Vector2 chestTile = Vector2.Zero;
        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value == chest)
            {
                chestTile = pair.Key;
                break;
            }
        }

        float dist = Vector2.Distance(ctx.Npc.Tile, chestTile);
        if (dist > 2.2f)
        {
            if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, chestTile, rescueThreshold: 180))
            {
                ctx.Npc.doEmote(28);
                ctx.Job.Done = true;
                return;
            }
            Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(loc, new Point((int)chestTile.X, (int)chestTile.Y), ctx.Npc.TilePoint);
            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, walkTile, ctx.GameTick);
        }
        else
        {
            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
            ctx.Npc.faceGeneralDirection(chestTile * 64f);
            loc.playSound("openChest");
            chest.shakeTimer = 500;

            string wanted = ctx.Job.Data.TryGetValue("item", out var it) && it != null ? it.ToString()! : "wood";
            string? resolvedId = Utilities.SafeItemResolver.ResolveObjectQualifiedId(wanted);

            int maxTake = 5;
            if (ctx.Job.Data.TryGetValue("quantity", out var qObj) && int.TryParse(qObj?.ToString(), out int reqQty))
            {
                maxTake = Math.Clamp(reqQty, 1, 5);
            }

            Item? foundItem = null;
            foreach (var item in chest.Items)
            {
                if (item == null || item.Stack <= 0) continue;
                bool idMatch = !string.IsNullOrEmpty(resolvedId) && string.Equals(item.QualifiedItemId, resolvedId, StringComparison.OrdinalIgnoreCase);
                bool nameMatch = (!string.IsNullOrWhiteSpace(item.Name) && item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                                 || (!string.IsNullOrWhiteSpace(item.DisplayName) && item.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                if (idMatch || nameMatch)
                {
                    foundItem = item;
                    break;
                }
            }

            if (foundItem != null)
            {
                int takeQty = Math.Min(foundItem.Stack, maxTake);
                foundItem.Stack -= takeQty;
                if (foundItem.Stack <= 0)
                    chest.Items.Remove(foundItem);
                chest.clearNulls();
                loc.playSound("coin");
                ctx.Npc.doEmote(20); // Heart
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_thanks", new { item = wanted }), duration: 3000);
            }
            else
            {
                ctx.Npc.doEmote(28);
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.chest_not_found", new { item = wanted }), duration: 3000);
            }
            ctx.Job.Done = true;
        }
    }
}
