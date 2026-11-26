namespace ValleyBuddies.Handlers.Social;

using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class SitOrRestState
{
    public bool Played { get; set; }
}

public sealed class SitOrRestHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.SitOrRest;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<SitOrRestState>();
        ctx.Npc.controller = null;
        ctx.Npc.Halt();
        ctx.Npc.followSchedule = false;
        if (!state.Played)
        {
            ctx.Npc.doEmote(24);
            ctx.Npc.Sprite?.StopAnimation();
            state.Played = true;
        }
    }
}
