namespace ValleyBuddies.Handlers.External;

using System;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class ExternalTaskHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.ExternalTask;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<ExternalTaskState>();
        if (string.IsNullOrWhiteSpace(state.TaskId))
        {
            if (ctx.Job.Data.TryGetValue("externalTaskId", out var rawId) && rawId is string s && !string.IsNullOrWhiteSpace(s))
            {
                state.TaskId = s;
            }
            else if (ctx.Job.Data.TryGetValue("taskId", out var rawId2) && rawId2 is string s2 && !string.IsNullOrWhiteSpace(s2))
            {
                state.TaskId = s2;
            }
        }

        if (string.IsNullOrWhiteSpace(state.TaskId))
        {
            ctx.DebugLog($"[ExternalTaskHandler] Job for {ctx.Npc.Name} has empty TaskId. Completing job.");
            ctx.Job.Done = true;
            return;
        }

        if (!ExternalTaskRegistry.Instance.TryGet(state.TaskId, out var registration) || registration == null)
        {
            ctx.DebugLog($"[ExternalTaskHandler] No external task handler registered for TaskId '{state.TaskId}' ({ctx.Npc.Name}). Completing job.");
            ctx.Job.Done = true;
            return;
        }

        try
        {
            bool keepRunning = registration.OnTick(ctx.Npc, ctx.Owner, ctx.GameTick);
            if (!keepRunning)
            {
                ctx.Job.Done = true;
            }
        }
        catch (Exception ex)
        {
            ctx.DebugLog($"[ExternalTaskHandler] Error executing external task '{state.TaskId}' for {ctx.Npc.Name}: {ex.Message}");
            ctx.Job.Done = true;
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<ExternalTaskState>();
        if (string.IsNullOrWhiteSpace(state.TaskId))
        {
            if (ctx.Job.Data.TryGetValue("externalTaskId", out var rawId) && rawId is string s && !string.IsNullOrWhiteSpace(s))
            {
                state.TaskId = s;
            }
            else if (ctx.Job.Data.TryGetValue("taskId", out var rawId2) && rawId2 is string s2 && !string.IsNullOrWhiteSpace(s2))
            {
                state.TaskId = s2;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.TaskId) &&
            ExternalTaskRegistry.Instance.TryGet(state.TaskId, out var registration) &&
            registration?.OnAbort != null)
        {
            try
            {
                registration.OnAbort(ctx.Npc, ctx.Owner);
            }
            catch (Exception ex)
            {
                ctx.DebugLog($"[ExternalTaskHandler] Error aborting external task '{state.TaskId}' for {ctx.Npc.Name}: {ex.Message}");
            }
        }
    }
}
