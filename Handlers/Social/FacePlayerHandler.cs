namespace ValleyBuddies.Handlers.Social;

using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class FacePlayerHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.FacePlayer;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<FacePlayerState>();
        if (ctx.Job.Data.ContainsKey("played") || state.Played)
        {
            ctx.Job.Done = true;
            return;
        }

        var player = ctx.Owner ?? Game1.player;
        if (player == null)
        {
            ctx.Job.Done = true;
            return;
        }

        int dir = ctx.Navigator.DirectionToward(ctx.Npc.Tile, player.Tile);
        ctx.Npc.faceDirection(dir);

        state.Played = true;
        ctx.Job.Data["played"] = true;
        ctx.Job.Done = true;
    }
}
