namespace ValleyBuddies.Handlers.Farm;

using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class HarvestForageHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.HarvestForage;

    public void Tick(JobExecutionContext ctx)
    {
        if (ctx.GameTick % 30 != 0) return;
        var loc = ctx.Npc.currentLocation;
        if (loc == null) { ctx.Job.Done = true; return; }

        var state = ctx.Job.GetOrCreateState<HarvestForageState>();
        const int maxForage = 10;

        if (state.CollectedCount >= maxForage)
        {
            ctx.Npc.doEmote(20);
            ctx.Job.Done = true;
            return;
        }

        var nearest = loc.objects.Pairs
            .Where(p => p.Value != null
                     && p.Value.isForage()
                     && !state.FailedForage.Contains(p.Key))
            .OrderBy(p => Vector2.Distance(ctx.Npc.Tile, p.Key))
            .FirstOrDefault();

        if (nearest.Value == null)
        {
            if (state.CollectedCount == 0)
            {
                ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.forage_empty"));
                ctx.Npc.doEmote(32);
            }
            else
            {
                ctx.Npc.doEmote(20);
            }
            ctx.Job.Done = true;
            return;
        }

        Vector2 forageTile = nearest.Key;
        StardewValley.Object forage = nearest.Value;
        float dist = Vector2.Distance(ctx.Npc.Tile, forageTile);
        if (dist > 2.0f)
        {
            if (FarmTaskHelper.TryMonitorAndRescueStuckWorker(ctx, state, loc, forageTile, rescueThreshold: 180))
            {
                if (!state.FailedForage.Contains(forageTile))
                    state.FailedForage.Add(forageTile);
                if (FarmTaskHelper.HandleUnreachableTaskTargets(ctx, state, loc, state.FailedForage, "HarvestForage", forageTile))
                    return;
                return;
            }
            Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(loc, new Point((int)forageTile.X, (int)forageTile.Y), ctx.Npc.TilePoint);
            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, walkTile, ctx.GameTick);
        }
        else
        {
            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
            loc.objects.Remove(forageTile);
            loc.playSound("coin");
            ctx.Npc.faceGeneralDirection(forageTile * 64f);
            bool storeInChests = ModEntry.Config?.StoreHarvestInChests != false;
            var chest = storeInChests ? ctx.World.FindNearestChest(loc, forageTile, 25) : null;
            var targetFarmer = ctx.Owner ?? Game1.player;
            if (chest != null)
            {
                if (!ctx.World.AddItemToChest(chest, forage) && targetFarmer != null)
                    FarmTaskHelper.DeliverItemCoopSafe(ctx, targetFarmer, forage);
            }
            else if (targetFarmer != null)
            {
                FarmTaskHelper.DeliverItemCoopSafe(ctx, targetFarmer, forage);
                if (state.CollectedCount == 0)
                    ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.forage_found"));
            }

            state.CollectedCount++;
            ctx.Npc.doEmote(20);
            if (state.CollectedCount >= maxForage)
            {
                ctx.Job.Done = true;
            }
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        if (ctx.Job.TryGetState<HarvestForageState>(out var state))
        {
            FarmTaskHelper.ResetTaskStuckState(state, ctx.Npc);
        }
    }
}
