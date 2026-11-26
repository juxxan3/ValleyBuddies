namespace ValleyBuddies.Handlers.Social;

using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class LeaveFarmHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.LeaveFarm;

    public void Tick(JobExecutionContext ctx)
    {
        ctx.DismissCompanion(ctx.Npc.Name);
        ctx.Job.Done = true;
    }
}
