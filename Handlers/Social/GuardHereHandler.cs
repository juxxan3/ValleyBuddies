namespace ValleyBuddies.Handlers.Social;

using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class GuardHereHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.GuardHere;

    public void Tick(JobExecutionContext ctx)
    {
        ctx.Npc.controller = null;
        ctx.Npc.Halt();
    }
}
