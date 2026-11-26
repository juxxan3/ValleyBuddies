namespace ValleyBuddies.Handlers.Social;

using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class HugOrCelebrateHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.HugOrCelebrate;

    public void Tick(JobExecutionContext ctx)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null || ctx.Npc.currentLocation == null || player.currentLocation != ctx.Npc.currentLocation)
        {
            ctx.Job.Done = true;
            return;
        }

        var state = ctx.Job.GetOrCreateState<HugOrCelebrateState>();

        float dist = Vector2.Distance(ctx.Npc.Tile, player.Tile);
        if (dist > 1.5f)
        {
            if (!ctx.Job.Data.TryGetValue("hug_start_tick", out var stObj) || stObj is not ulong startTick)
            {
                startTick = ctx.GameTick;
                ctx.Job.Data["hug_start_tick"] = startTick;
            }
            if (ctx.GameTick >= startTick + 360)
            {
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Job.Done = true;
                return;
            }
            Point adjacent = ctx.Navigator.GetAdjacentWalkableTile(ctx.Npc.currentLocation, player.TilePoint, ctx.Npc.TilePoint);
            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, ctx.Npc.currentLocation, adjacent, ctx.GameTick);
            return;
        }

        if (!ctx.Job.Data.ContainsKey("played") && !state.Played)
        {
            ctx.Npc.doEmote(20);
            try { NpcActionExecutor.TryAwardDailyActivityFriendship(player, ctx.Npc, "hug_celebrate", 20, 20); } catch { }
            state.Played = true;
            ctx.Job.Data["played"] = true;
            ctx.Job.Done = true;
        }
    }
}
