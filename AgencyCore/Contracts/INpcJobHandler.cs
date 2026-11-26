namespace ValleyBuddies.AgencyCore;

using ValleyBuddies.Execution;

public interface INpcJobHandler
{
    JobKind Kind { get; }
    void Tick(JobExecutionContext ctx);
    void OnAbort(JobExecutionContext ctx) { }
}
